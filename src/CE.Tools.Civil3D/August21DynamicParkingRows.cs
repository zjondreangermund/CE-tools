using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace CETools.Civil3D
{
    /// <summary>
    /// Dynamic replacement for the simple single/double parking row commands.
    /// Every generated bay carries the source baseline handle and the exact row
    /// settings. Grip editing the source line/polyline segment rebuilds only that
    /// linked row after the active AutoCAD command has finished.
    /// </summary>
    internal static class August21DynamicParkingRows
    {
        internal const string RegAppName = "CE_PARK_SIMPLE_DYNAMIC";
        private const double Tolerance = 0.000001;

        internal static void Run(Document document, bool doubleRow)
        {
            if (document == null) return;
            Baseline baseline = PromptBaseline(document);
            if (baseline == null) return;

            Settings settings = PromptSettings(document.Editor, doubleRow);
            if (settings == null) return;
            settings.DoubleRow = doubleRow;

            int count = BayCount(baseline.Length, settings.BayWidth);
            if (count < 1)
            {
                document.Editor.WriteMessage(
                    "\nCE Parking cancelled. The baseline is shorter than one bay width.");
                return;
            }

            string title = doubleRow ? "CE_PKDOUBLE" : "CE_PKROW";
            document.Editor.WriteMessage(
                "\n{0} preview: baseline={1:0.###}; bays per row={2}; width={3:0.###}; depth={4:0.###}; angle={5:0.###}.",
                title,
                baseline.Length,
                count,
                settings.BayWidth,
                settings.BayDepth,
                settings.AngleDegrees);
            if (!Confirm(document.Editor, doubleRow
                    ? "Create these linked opposing parking rows"
                    : "Create this linked parking row"))
                return;

            string group = Guid.NewGuid().ToString("N");
            int created;
            string error;
            if (!ReplaceGroup(document, group, baseline, settings, out created, out error))
            {
                document.Editor.WriteMessage("\n{0} stopped safely. {1}", title, error);
                return;
            }

            August21SimpleParkingRefreshManager.RebuildCache(document.Database);
            ForceDisplay(document);
            document.Editor.WriteMessage(
                "\n{0} complete. Dynamic closed bays created={1}. Grip-edit the source baseline and the row will rebuild automatically.",
                title,
                created);
        }

        internal static bool RefreshGroup(
            Document document,
            ParkingLink link,
            out int created,
            out string error)
        {
            created = 0;
            error = string.Empty;
            if (document == null || link == null)
            {
                error = "The dynamic parking link is unavailable.";
                return false;
            }
            Baseline baseline = ReadBaseline(
                document.Database,
                link.SourceHandle,
                link.SegmentIndex);
            if (baseline == null)
            {
                error = "The linked parking baseline is missing or no longer has the stored straight segment.";
                return false;
            }
            return ReplaceGroup(document, link.GroupId, baseline, link.Settings, out created, out error);
        }

        internal static Dictionary<string, ParkingLink> ReadLinks(Database database)
        {
            var links = new Dictionary<string, ParkingLink>(StringComparer.OrdinalIgnoreCase);
            if (database == null) return links;
            try
            {
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    BlockTableRecord space = transaction.GetObject(
                        database.CurrentSpaceId,
                        OpenMode.ForRead,
                        false) as BlockTableRecord;
                    if (space == null) return links;
                    foreach (ObjectId id in space)
                    {
                        Entity entity = null;
                        try { entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity; }
                        catch { continue; }
                        ParkingLink link;
                        if (entity != null && TryReadLink(entity, out link) &&
                            !string.IsNullOrWhiteSpace(link.GroupId) &&
                            !links.ContainsKey(link.GroupId))
                            links.Add(link.GroupId, link);
                    }
                }
            }
            catch { }
            return links;
        }

        private static bool ReplaceGroup(
            Document document,
            string group,
            Baseline baseline,
            Settings settings,
            out int created,
            out string error)
        {
            created = 0;
            error = string.Empty;
            try
            {
                int count = BayCount(baseline.Length, settings.BayWidth);
                if (count < 1)
                {
                    error = "The edited baseline is now shorter than one bay width.";
                    return false;
                }

                using (Transaction transaction =
                    document.Database.TransactionManager.StartTransaction())
                {
                    EnsureRegApp(document.Database, transaction);
                    BlockTableRecord space = transaction.GetObject(
                        document.Database.CurrentSpaceId,
                        OpenMode.ForWrite,
                        false) as BlockTableRecord;
                    if (space == null)
                    {
                        error = "The current drawing space could not be opened.";
                        return false;
                    }

                    // Delete only the outputs owned by this link. The source line is
                    // never erased or modified.
                    var erase = new List<ObjectId>();
                    foreach (ObjectId id in space)
                    {
                        Entity entity = null;
                        try { entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity; }
                        catch { continue; }
                        ParkingLink existing;
                        if (entity != null && TryReadLink(entity, out existing) &&
                            string.Equals(existing.GroupId, group, StringComparison.OrdinalIgnoreCase))
                            erase.Add(id);
                    }
                    foreach (ObjectId id in erase)
                    {
                        try
                        {
                            Entity entity = transaction.GetObject(id, OpenMode.ForWrite, false) as Entity;
                            if (entity != null && !entity.IsErased) entity.Erase();
                        }
                        catch { }
                    }

                    Vector3d direction = baseline.Direction;
                    if (settings.DoubleRow)
                    {
                        Vector3d leftNormal = Vector3d.ZAxis.CrossProduct(direction).GetNormal();
                        Vector3d leftDivider = direction.RotateBy(
                            Degrees(settings.AngleDegrees),
                            Vector3d.ZAxis);
                        Vector3d rightDivider = direction.RotateBy(
                            -Degrees(settings.AngleDegrees),
                            Vector3d.ZAxis);
                        Vector3d halfAisle = leftNormal * (settings.AisleWidth / 2.0);
                        Point3d leftInner = baseline.Start + halfAisle;
                        Point3d rightInner = baseline.Start - halfAisle;
                        for (int index = 0; index < count; index++)
                        {
                            double s0 = index * settings.BayWidth;
                            double s1 = (index + 1) * settings.BayWidth;
                            Point3d lf0 = leftInner + direction * s0;
                            Point3d lf1 = leftInner + direction * s1;
                            Point3d rf0 = rightInner + direction * s0;
                            Point3d rf1 = rightInner + direction * s1;
                            AppendBay(document.Database, transaction, space, baseline.LayerId,
                                lf0, lf1,
                                lf1 + leftDivider * settings.BayDepth,
                                lf0 + leftDivider * settings.BayDepth,
                                group, baseline, settings);
                            AppendBay(document.Database, transaction, space, baseline.LayerId,
                                rf0, rf1,
                                rf1 + rightDivider * settings.BayDepth,
                                rf0 + rightDivider * settings.BayDepth,
                                group, baseline, settings);
                            created += 2;
                        }
                    }
                    else
                    {
                        double sign = string.Equals(settings.Side, "Left", StringComparison.OrdinalIgnoreCase)
                            ? 1.0 : -1.0;
                        Vector3d divider = direction.RotateBy(
                            sign * Degrees(settings.AngleDegrees),
                            Vector3d.ZAxis);
                        for (int index = 0; index < count; index++)
                        {
                            Point3d frontStart = baseline.Start + direction * (index * settings.BayWidth);
                            Point3d frontEnd = baseline.Start + direction * ((index + 1) * settings.BayWidth);
                            AppendBay(document.Database, transaction, space, baseline.LayerId,
                                frontStart,
                                frontEnd,
                                frontEnd + divider * settings.BayDepth,
                                frontStart + divider * settings.BayDepth,
                                group, baseline, settings);
                            created++;
                        }
                    }
                    transaction.Commit();
                }
                ForceDisplay(document);
                return true;
            }
            catch (System.Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }

        private static void AppendBay(
            Database database,
            Transaction transaction,
            BlockTableRecord space,
            ObjectId layerId,
            Point3d first,
            Point3d second,
            Point3d third,
            Point3d fourth,
            string group,
            Baseline baseline,
            Settings settings)
        {
            var bay = new Polyline(4);
            bay.SetDatabaseDefaults(database);
            if (!layerId.IsNull) bay.LayerId = layerId;
            bay.Elevation = first.Z;
            bay.AddVertexAt(0, new Point2d(first.X, first.Y), 0.0, 0.0, 0.0);
            bay.AddVertexAt(1, new Point2d(second.X, second.Y), 0.0, 0.0, 0.0);
            bay.AddVertexAt(2, new Point2d(third.X, third.Y), 0.0, 0.0, 0.0);
            bay.AddVertexAt(3, new Point2d(fourth.X, fourth.Y), 0.0, 0.0, 0.0);
            bay.Closed = true;
            space.AppendEntity(bay);
            transaction.AddNewlyCreatedDBObject(bay, true);
            WriteLink(bay, group, baseline, settings);
            try { bay.RecordGraphicsModified(true); } catch { }
        }

        private static Baseline PromptBaseline(Document document)
        {
            var options = new PromptEntityOptions(
                "\nSelect a straight line or pick a straight polyline segment: ");
            options.SetRejectMessage("\nSelect an AutoCAD Line or 2D Polyline.");
            options.AddAllowedClass(typeof(Line), false);
            options.AddAllowedClass(typeof(Polyline), false);
            PromptEntityResult result = document.Editor.GetEntity(options);
            if (result.Status != PromptStatus.OK) return null;

            int segmentIndex = -1;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                Entity entity = transaction.GetObject(
                    result.ObjectId,
                    OpenMode.ForRead,
                    false) as Entity;
                if (entity == null) return null;
                Point3d start;
                Point3d end;
                Line line = entity as Line;
                if (line != null)
                {
                    start = line.StartPoint;
                    end = line.EndPoint;
                }
                else
                {
                    Polyline polyline = entity as Polyline;
                    if (polyline == null || polyline.NumberOfVertices < 2) return null;
                    Point3d closest = polyline.GetClosestPointTo(result.PickedPoint, false);
                    double parameter = polyline.GetParameterAtPoint(closest);
                    segmentIndex = (int)Math.Floor(parameter);
                    int maximum = polyline.Closed
                        ? polyline.NumberOfVertices - 1
                        : polyline.NumberOfVertices - 2;
                    segmentIndex = Math.Max(0, Math.Min(segmentIndex, maximum));
                    if (polyline.GetSegmentType(segmentIndex) != SegmentType.Line)
                    {
                        document.Editor.WriteMessage(
                            "\nCE Parking currently supports straight polyline segments only.");
                        return null;
                    }
                    start = polyline.GetPoint3dAt(segmentIndex);
                    int next = segmentIndex + 1;
                    if (next >= polyline.NumberOfVertices) next = 0;
                    end = polyline.GetPoint3dAt(next);
                }
                return MakeBaseline(
                    result.ObjectId,
                    segmentIndex,
                    entity.LayerId,
                    start,
                    end);
            }
        }

        private static Baseline ReadBaseline(
            Database database,
            string handleText,
            int segmentIndex)
        {
            ObjectId id = ResolveHandle(database, handleText);
            if (id.IsNull || id.IsErased) return null;
            try
            {
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    Entity entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                    if (entity == null) return null;
                    Line line = entity as Line;
                    if (line != null)
                        return MakeBaseline(id, -1, entity.LayerId, line.StartPoint, line.EndPoint);
                    Polyline polyline = entity as Polyline;
                    if (polyline == null || polyline.NumberOfVertices < 2) return null;
                    int maximum = polyline.Closed
                        ? polyline.NumberOfVertices - 1
                        : polyline.NumberOfVertices - 2;
                    if (segmentIndex < 0 || segmentIndex > maximum ||
                        polyline.GetSegmentType(segmentIndex) != SegmentType.Line)
                        return null;
                    int next = segmentIndex + 1;
                    if (next >= polyline.NumberOfVertices) next = 0;
                    return MakeBaseline(id, segmentIndex, entity.LayerId,
                        polyline.GetPoint3dAt(segmentIndex),
                        polyline.GetPoint3dAt(next));
                }
            }
            catch { return null; }
        }

        private static Baseline MakeBaseline(
            ObjectId id,
            int segment,
            ObjectId layerId,
            Point3d start,
            Point3d end)
        {
            Vector3d plan = new Vector3d(end.X - start.X, end.Y - start.Y, 0.0);
            if (plan.Length <= Tolerance) return null;
            return new Baseline
            {
                SourceId = id,
                SourceHandle = id.Handle.ToString(),
                SegmentIndex = segment,
                LayerId = layerId,
                Start = start,
                Direction = plan.GetNormal(),
                Length = plan.Length
            };
        }

        private static Settings PromptSettings(Editor editor, bool doubleRow)
        {
            double width;
            double depth;
            double angle;
            if (!Positive(editor, "Bay width", 2.5, out width)) return null;
            if (!Positive(editor, "Bay depth", 5.0, out depth)) return null;
            if (!Positive(editor, "Divider angle from baseline in degrees", 90.0, out angle)) return null;
            if (angle >= 180.0)
            {
                editor.WriteMessage("\nParking divider angle must be greater than 0 and less than 180 degrees.");
                return null;
            }
            double aisle = 0.0;
            string side = "Left";
            if (doubleRow)
            {
                if (!Positive(editor, "Aisle width", 6.0, out aisle)) return null;
            }
            else
            {
                var options = new PromptKeywordOptions(
                    "\nCreate parking bays on which side [Left/Right] <Left>: ")
                { AllowNone = true };
                options.Keywords.Add("Left");
                options.Keywords.Add("Right");
                PromptResult result = editor.GetKeywords(options);
                if (result.Status == PromptStatus.Cancel) return null;
                if (result.Status == PromptStatus.OK) side = result.StringResult;
            }
            return new Settings
            {
                BayWidth = width,
                BayDepth = depth,
                AngleDegrees = angle,
                AisleWidth = aisle,
                Side = side
            };
        }

        private static bool Positive(
            Editor editor,
            string label,
            double defaultValue,
            out double value)
        {
            var options = new PromptDoubleOptions(
                "\nEnter " + label + " <" +
                defaultValue.ToString("0.###", CultureInfo.InvariantCulture) + ">: ")
            {
                AllowNone = true,
                AllowNegative = false,
                AllowZero = false,
                DefaultValue = defaultValue,
                UseDefaultValue = true
            };
            PromptDoubleResult result = editor.GetDouble(options);
            value = result.Status == PromptStatus.OK ? result.Value : defaultValue;
            return result.Status == PromptStatus.OK;
        }

        private static bool Confirm(Editor editor, string message)
        {
            var options = new PromptKeywordOptions(
                "\n" + message + "? [Yes/No] <No>: ")
            { AllowNone = true };
            options.Keywords.Add("Yes");
            options.Keywords.Add("No");
            PromptResult result = editor.GetKeywords(options);
            return result.Status == PromptStatus.OK &&
                string.Equals(result.StringResult, "Yes", StringComparison.OrdinalIgnoreCase);
        }

        private static int BayCount(double length, double width)
        {
            return (int)Math.Floor((length + Tolerance) / Math.Max(width, Tolerance));
        }

        private static double Degrees(double degrees)
        {
            return degrees * Math.PI / 180.0;
        }

        private static void EnsureRegApp(Database database, Transaction transaction)
        {
            RegAppTable table = transaction.GetObject(
                database.RegAppTableId,
                OpenMode.ForRead,
                false) as RegAppTable;
            if (table == null || table.Has(RegAppName)) return;
            table.UpgradeOpen();
            var record = new RegAppTableRecord { Name = RegAppName };
            table.Add(record);
            transaction.AddNewlyCreatedDBObject(record, true);
        }

        private static void WriteLink(
            Entity entity,
            string group,
            Baseline baseline,
            Settings settings)
        {
            entity.XData = new ResultBuffer(
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, RegAppName),
                Text("GROUP=" + group),
                Text("SOURCE=" + baseline.SourceHandle),
                Text("SEGMENT=" + baseline.SegmentIndex.ToString(CultureInfo.InvariantCulture)),
                Text("DOUBLE=" + (settings.DoubleRow ? "1" : "0")),
                Text("WIDTH=" + F(settings.BayWidth)),
                Text("DEPTH=" + F(settings.BayDepth)),
                Text("ANGLE=" + F(settings.AngleDegrees)),
                Text("AISLE=" + F(settings.AisleWidth)),
                Text("SIDE=" + (settings.Side ?? "Left")));
        }

        private static bool TryReadLink(Entity entity, out ParkingLink link)
        {
            link = null;
            ResultBuffer data = entity.GetXDataForApplication(RegAppName);
            if (data == null) return false;
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (TypedValue value in data.AsArray())
            {
                string text = value.Value as string;
                if (string.IsNullOrWhiteSpace(text)) continue;
                int split = text.IndexOf('=');
                if (split <= 0) continue;
                values[text.Substring(0, split)] = text.Substring(split + 1);
            }
            string group;
            string source;
            if (!values.TryGetValue("GROUP", out group) ||
                !values.TryGetValue("SOURCE", out source)) return false;
            int segment;
            int.TryParse(Get(values, "SEGMENT", "-1"), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out segment);
            double width;
            double depth;
            double angle;
            double aisle;
            if (!D(Get(values, "WIDTH", "2.5"), out width) ||
                !D(Get(values, "DEPTH", "5"), out depth) ||
                !D(Get(values, "ANGLE", "90"), out angle) ||
                !D(Get(values, "AISLE", "0"), out aisle)) return false;
            link = new ParkingLink
            {
                GroupId = group,
                SourceHandle = source,
                SegmentIndex = segment,
                Settings = new Settings
                {
                    DoubleRow = string.Equals(Get(values, "DOUBLE", "0"), "1", StringComparison.Ordinal),
                    BayWidth = width,
                    BayDepth = depth,
                    AngleDegrees = angle,
                    AisleWidth = aisle,
                    Side = Get(values, "SIDE", "Left")
                }
            };
            return true;
        }

        private static TypedValue Text(string value)
        {
            return new TypedValue((int)DxfCode.ExtendedDataAsciiString, value);
        }

        private static string F(double value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        private static bool D(string text, out double value)
        {
            return double.TryParse(text, NumberStyles.Float,
                CultureInfo.InvariantCulture, out value);
        }

        private static string Get(Dictionary<string, string> values, string key, string fallback)
        {
            string value;
            return values.TryGetValue(key, out value) ? value : fallback;
        }

        private static ObjectId ResolveHandle(Database database, string text)
        {
            if (database == null || string.IsNullOrWhiteSpace(text)) return ObjectId.Null;
            try
            {
                long value = Convert.ToInt64(text, 16);
                ObjectId id = database.GetObjectId(false, new Handle(value), 0);
                return id.IsNull || id.IsErased ? ObjectId.Null : id;
            }
            catch { return ObjectId.Null; }
        }

        private static void ForceDisplay(Document document)
        {
            try
            {
                document.Editor.Regen();
                AcApplication.UpdateScreen();
            }
            catch { }
        }

        internal sealed class ParkingLink
        {
            internal string GroupId;
            internal string SourceHandle;
            internal int SegmentIndex;
            internal Settings Settings;
        }

        internal sealed class Settings
        {
            internal bool DoubleRow;
            internal double BayWidth;
            internal double BayDepth;
            internal double AngleDegrees;
            internal double AisleWidth;
            internal string Side;
        }

        private sealed class Baseline
        {
            internal ObjectId SourceId;
            internal string SourceHandle;
            internal int SegmentIndex;
            internal ObjectId LayerId;
            internal Point3d Start;
            internal Vector3d Direction;
            internal double Length;
        }
    }

    internal static class August21SimpleParkingRefreshManager
    {
        private static Database _database;
        private static bool _initialised;
        private static bool _busy;
        private static bool _cacheDirty = true;
        private static readonly Dictionary<string, August21DynamicParkingRows.ParkingLink> Links =
            new Dictionary<string, August21DynamicParkingRows.ParkingLink>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> PendingSources =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        internal static void Initialize()
        {
            if (_initialised) return;
            _initialised = true;
            AcApplication.Idle += OnIdle;
        }

        internal static void Terminate()
        {
            if (!_initialised) return;
            AcApplication.Idle -= OnIdle;
            DetachDatabase();
            Links.Clear();
            PendingSources.Clear();
            _initialised = false;
        }

        internal static void RebuildCache(Database database)
        {
            AttachDatabase(database);
            Links.Clear();
            foreach (KeyValuePair<string, August21DynamicParkingRows.ParkingLink> item in
                August21DynamicParkingRows.ReadLinks(database))
                Links[item.Key] = item.Value;
            _cacheDirty = false;
        }

        private static void OnIdle(object sender, EventArgs eventArgs)
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            AttachDatabase(document == null ? null : document.Database);
            if (document == null) return;
            if (_cacheDirty) RebuildCache(document.Database);
            if (_busy || PendingSources.Count == 0) return;
            string activeCommands = Convert.ToString(
                AcApplication.GetSystemVariable("CMDNAMES"),
                CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(activeCommands)) return;

            var groups = Links.Values
                .Where(link => PendingSources.Contains(link.SourceHandle))
                .Select(link => link.GroupId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            PendingSources.Clear();
            if (groups.Count == 0) return;

            _busy = true;
            try
            {
                using (DocumentLock documentLock = document.LockDocument())
                {
                    foreach (string group in groups)
                    {
                        August21DynamicParkingRows.ParkingLink link;
                        if (!Links.TryGetValue(group, out link)) continue;
                        int created;
                        string error;
                        August21DynamicParkingRows.RefreshGroup(
                            document,
                            link,
                            out created,
                            out error);
                    }
                }
                RebuildCache(document.Database);
                try
                {
                    document.Editor.Regen();
                    AcApplication.UpdateScreen();
                }
                catch { }
            }
            catch
            {
                _cacheDirty = true;
            }
            finally
            {
                _busy = false;
            }
        }

        private static void AttachDatabase(Database database)
        {
            if (ReferenceEquals(_database, database)) return;
            DetachDatabase();
            _database = database;
            _cacheDirty = true;
            if (_database == null) return;
            _database.ObjectModified += OnObjectModified;
            _database.ObjectErased += OnObjectErased;
            _database.ObjectAppended += OnObjectAppended;
        }

        private static void DetachDatabase()
        {
            if (_database != null)
            {
                _database.ObjectModified -= OnObjectModified;
                _database.ObjectErased -= OnObjectErased;
                _database.ObjectAppended -= OnObjectAppended;
            }
            _database = null;
        }

        private static void OnObjectModified(object sender, ObjectEventArgs eventArgs)
        {
            if (_busy || eventArgs == null || eventArgs.DBObject == null) return;
            string handle = SafeHandle(eventArgs.DBObject);
            if (Links.Values.Any(link => string.Equals(
                    link.SourceHandle,
                    handle,
                    StringComparison.OrdinalIgnoreCase)))
                PendingSources.Add(handle);
        }

        private static void OnObjectErased(object sender, ObjectErasedEventArgs eventArgs)
        {
            if (_busy || eventArgs == null || eventArgs.DBObject == null) return;
            string handle = SafeHandle(eventArgs.DBObject);
            if (Links.Values.Any(link => string.Equals(
                    link.SourceHandle,
                    handle,
                    StringComparison.OrdinalIgnoreCase)))
                PendingSources.Add(handle);
            _cacheDirty = true;
        }

        private static void OnObjectAppended(object sender, ObjectEventArgs eventArgs)
        {
            if (!_busy) _cacheDirty = true;
        }

        private static string SafeHandle(DBObject value)
        {
            try
            {
                return value.ObjectId.IsNull
                    ? string.Empty
                    : value.ObjectId.Handle.ToString();
            }
            catch { return string.Empty; }
        }
    }
}

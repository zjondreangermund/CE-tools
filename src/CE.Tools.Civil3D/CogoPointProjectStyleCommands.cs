using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilCogoPoint = Autodesk.Civil.DatabaseServices.CogoPoint;

[assembly: CommandClass(typeof(CETools.Civil3D.CogoPointProjectStyleCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Applies the Project Style Centre point and point-label selections to live
    /// COGO points and maintains label offsets independently from point geometry.
    /// Moving a label never changes its survey point; moving a point carries the
    /// saved label offset to the new point location.
    /// </summary>
    public sealed class CogoPointProjectStyleCommands
    {
        private const string OffsetRecordName = "CE_COGO_LABEL_OFFSET";

        [CommandMethod("CE_TOOLS", "CE_COGOPOINTSYNC", CommandFlags.Modal | CommandFlags.Redraw)]
        public void SynchronizePoints()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            CogoPointStyleResult result = ApplySelectedStyles(document, true);
            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_COGOPOINTSYNC complete. Points styled={0}; labels styled={1}; labels repositioned={2}; overlaps moved={3}; skipped={4}.{5}",
                result.PointStylesApplied,
                result.LabelStylesApplied,
                result.StoredOffsetsRestored,
                result.OverlapsMoved,
                result.Skipped,
                string.IsNullOrWhiteSpace(result.Warning) ? string.Empty : " " + result.Warning);
        }

        [CommandMethod("CE_TOOLS", "CE_COGOOVERLAPFIX", CommandFlags.Modal | CommandFlags.Redraw)]
        public void ResolvePointLabelOverlaps()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            CogoPointStyleResult result = ApplySelectedStyles(document, true);
            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_COGOOVERLAPFIX complete. COGO labels moved={0}; point coordinates unchanged.",
                result.OverlapsMoved);
        }

        internal static CogoPointStyleResult ApplySelectedStyles(
            Document document,
            bool resolveOverlaps)
        {
            var result = new CogoPointStyleResult();
            if (document == null) return result;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (civilDocument == null)
            {
                result.Warning = "No active Civil 3D document was available.";
                return result;
            }

            Database database = document.Database;
            try
            {
                using (DocumentLock documentLock = document.LockDocument())
                using (Transaction transaction =
                    database.TransactionManager.StartTransaction())
                {
                    ProjectStyleSelection selection =
                        ProjectStyleCenterCommands.ReadSelection(database);
                    string requestedPoint = ReadSelection(
                        selection,
                        "Point Style",
                        "RSA_Circle");
                    string requestedLabel = ReadSelection(
                        selection,
                        "Point Label Style",
                        "Description Only");

                    string actualPoint;
                    ObjectId pointStyleId = CivilStyleCatalogV2.ResolveStyleId(
                        database,
                        civilDocument,
                        "Point Style",
                        requestedPoint,
                        transaction,
                        out actualPoint);
                    string actualLabel;
                    ObjectId labelStyleId = CivilStyleCatalogV2.ResolveStyleId(
                        database,
                        civilDocument,
                        "Point Label Style",
                        requestedLabel,
                        transaction,
                        out actualLabel);

                    List<ObjectId> pointIds = ReadPointIds(civilDocument);
                    foreach (ObjectId pointId in pointIds)
                    {
                        CivilCogoPoint point;
                        try
                        {
                            point = transaction.GetObject(
                                pointId,
                                OpenMode.ForWrite,
                                false) as CivilCogoPoint;
                        }
                        catch
                        {
                            result.Skipped++;
                            continue;
                        }
                        if (point == null)
                        {
                            result.Skipped++;
                            continue;
                        }

                        if (!pointStyleId.IsNull)
                        {
                            try
                            {
                                point.StyleId = pointStyleId;
                                result.PointStylesApplied++;
                            }
                            catch
                            {
                                result.Skipped++;
                            }
                        }
                        if (!labelStyleId.IsNull)
                        {
                            try
                            {
                                point.LabelStyleId = labelStyleId;
                                result.LabelStylesApplied++;
                            }
                            catch
                            {
                                result.Skipped++;
                            }
                        }

                        if (string.IsNullOrWhiteSpace(point.RawDescription))
                            point.RawDescription = string.IsNullOrWhiteSpace(point.PointName)
                                ? "P" + point.PointNumber.ToString(CultureInfo.InvariantCulture)
                                : point.PointName;
                        TrySetLabelVisible(point);
                        Point3d anchor = PointLocation(point);
                        Vector3d stored;
                        if (TryReadOffset(point, transaction, out stored))
                        {
                            stored = NormalizeOffset(stored, database);
                            try
                            {
                                point.LabelLocation = anchor + stored;
                                result.StoredOffsetsRestored++;
                            }
                            catch
                            {
                                result.Skipped++;
                            }
                        }
                        else
                        {
                            Vector3d current = ReadCurrentOffset(point, anchor, database);
                            WriteOffset(point, transaction, current);
                        }
                    }
                    transaction.Commit();
                }

                if (resolveOverlaps)
                    result.OverlapsMoved = ResolveOverlaps(document);
            }
            catch (System.Exception exception)
            {
                result.Warning = "COGO synchronization stopped: " + exception.Message;
            }
            return result;
        }

        internal static void ApplyPointStyles(
            Database database,
            CivilDocument civilDocument,
            Transaction transaction,
            CivilCogoPoint point)
        {
            if (database == null || civilDocument == null ||
                transaction == null || point == null)
                return;
            ProjectStyleSelection selection =
                ProjectStyleCenterCommands.ReadSelection(database);
            string requestedPoint = ReadSelection(
                selection,
                "Point Style",
                "RSA_Circle");
            string requestedLabel = ReadSelection(
                selection,
                "Point Label Style",
                "Description Only");
            string actual;
            ObjectId pointStyleId = CivilStyleCatalogV2.ResolveStyleId(
                database,
                civilDocument,
                "Point Style",
                requestedPoint,
                transaction,
                out actual);
            ObjectId labelStyleId = CivilStyleCatalogV2.ResolveStyleId(
                database,
                civilDocument,
                "Point Label Style",
                requestedLabel,
                transaction,
                out actual);
            if (!pointStyleId.IsNull)
            {
                try { point.StyleId = pointStyleId; } catch { }
            }
            if (!labelStyleId.IsNull)
            {
                try { point.LabelStyleId = labelStyleId; } catch { }
            }
            if (string.IsNullOrWhiteSpace(point.RawDescription))
                point.RawDescription = string.IsNullOrWhiteSpace(point.PointName)
                    ? "P" + point.PointNumber.ToString(CultureInfo.InvariantCulture)
                    : point.PointName;
            TrySetLabelVisible(point);
        }

        internal static int ResolveOverlaps(Document document)
        {
            if (document == null) return 0;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (civilDocument == null) return 0;
            Database database = document.Database;
            double textHeight = PaperAnnotationScale.ModelTextHeight(database, 2.5);
            double markerHalf = Math.Max(textHeight * 0.35, 0.001);
            double gap = Math.Max(textHeight * 0.35, 0.001);
            var items = new List<CogoLabelItem>();
            var occupied = new List<Box2d>();

            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId pointId in ReadPointIds(civilDocument))
                {
                    CivilCogoPoint point;
                    try
                    {
                        point = transaction.GetObject(
                            pointId,
                            OpenMode.ForWrite,
                            false) as CivilCogoPoint;
                    }
                    catch
                    {
                        continue;
                    }
                    if (point == null) continue;
                    Point3d anchor = PointLocation(point);
                    occupied.Add(new Box2d(
                        anchor.X - markerHalf,
                        anchor.Y - markerHalf,
                        anchor.X + markerHalf,
                        anchor.Y + markerHalf));
                    string text = string.IsNullOrWhiteSpace(point.RawDescription)
                        ? point.PointName
                        : point.RawDescription;
                    Point3d location;
                    try { location = point.LabelLocation; }
                    catch { location = anchor + new Vector3d(textHeight * 2.0, textHeight * 2.0, 0.0); }
                    double width = Math.Max(
                        textHeight * 2.8,
                        Math.Max(1, (text ?? string.Empty).Length) * textHeight * 0.72);
                    double height = Math.Max(textHeight * 1.8, 0.001);
                    items.Add(new CogoLabelItem(
                        point,
                        anchor,
                        location,
                        width,
                        height));
                }

                int moved = 0;
                foreach (CogoLabelItem item in items
                    .OrderBy(value => value.Point.PointNumber))
                {
                    Point3d best = FindClearLocation(
                        item,
                        occupied,
                        gap,
                        textHeight);
                    Box2d box = LabelBox(best, item.Width, item.Height, gap);
                    occupied.Add(box);
                    if (best.DistanceTo(item.LabelLocation) > 1e-8)
                    {
                        try
                        {
                            item.Point.LabelLocation = best;
                            WriteOffset(
                                item.Point,
                                transaction,
                                best - item.Anchor);
                            moved++;
                        }
                        catch
                        {
                            // A locked/reference point is left unchanged.
                        }
                    }
                    else
                    {
                        WriteOffset(
                            item.Point,
                            transaction,
                            best - item.Anchor);
                    }
                }
                transaction.Commit();
                return moved;
            }
        }

        private static Point3d FindClearLocation(
            CogoLabelItem item,
            IList<Box2d> occupied,
            double gap,
            double textHeight)
        {
            var candidates = new List<Point3d> { item.LabelLocation };
            double step = Math.Max(textHeight * 2.4, gap * 2.0);
            Vector3d current = item.LabelLocation - item.Anchor;
            if (current.Length < step * 0.25)
                current = new Vector3d(step, step, 0.0);
            candidates.Add(item.Anchor + current);

            for (int ring = 1; ring <= 4; ring++)
            {
                double radius = step * ring;
                for (int sector = 0; sector < 16; sector++)
                {
                    double angle = Math.PI * 2.0 * sector / 16.0;
                    candidates.Add(new Point3d(
                        item.Anchor.X + Math.Cos(angle) * radius,
                        item.Anchor.Y + Math.Sin(angle) * radius,
                        item.Anchor.Z));
                }
            }

            Point3d best = candidates[0];
            double bestDistance = double.MaxValue;
            foreach (Point3d candidate in candidates)
            {
                Box2d box = LabelBox(candidate, item.Width, item.Height, gap);
                if (occupied.Any(existing => existing.Intersects(box))) continue;
                // Prefer the closest clear position to the survey point itself.
                // Original label movement is only a small tie-breaker.
                double distance = candidate.DistanceTo(item.Anchor) +
                    candidate.DistanceTo(item.LabelLocation) * 0.05;
                if (distance < bestDistance)
                {
                    best = candidate;
                    bestDistance = distance;
                }
            }
            return bestDistance == double.MaxValue ? candidates.Last() : best;
        }

        private static Box2d LabelBox(
            Point3d location,
            double width,
            double height,
            double gap)
        {
            return new Box2d(
                location.X - gap,
                location.Y - height * 0.5 - gap,
                location.X + width + gap,
                location.Y + height * 0.5 + gap);
        }

        private static string ReadSelection(
            ProjectStyleSelection selection,
            string key,
            string fallback)
        {
            string value;
            if (selection != null && selection.Exists &&
                selection.Values.TryGetValue(key, out value) &&
                !string.IsNullOrWhiteSpace(value) &&
                !string.Equals(
                    value,
                    "<Use drawing default>",
                    StringComparison.OrdinalIgnoreCase))
            {
                return value.Trim();
            }
            return fallback;
        }

        private static List<ObjectId> ReadPointIds(CivilDocument civilDocument)
        {
            var result = new List<ObjectId>();
            if (civilDocument == null) return result;
            foreach (object value in CivilStyleDiscovery.Enumerate(
                civilDocument.CogoPoints))
            {
                if (value is ObjectId)
                {
                    ObjectId id = (ObjectId)value;
                    if (!id.IsNull && !id.IsErased) result.Add(id);
                }
                else
                {
                    DBObject databaseObject = value as DBObject;
                    if (databaseObject != null && !databaseObject.ObjectId.IsNull)
                        result.Add(databaseObject.ObjectId);
                }
            }
            return result.Distinct().ToList();
        }

        private static Point3d PointLocation(CivilCogoPoint point)
        {
            return new Point3d(
                point.Easting,
                point.Northing,
                point.Elevation);
        }

        private static Vector3d ReadCurrentOffset(
            CivilCogoPoint point,
            Point3d anchor,
            Database database)
        {
            try
            {
                Point3d location = point.LabelLocation;
                Vector3d offset = location - anchor;
                if (offset.Length > 1e-8) return offset;
            }
            catch
            {
                // Use the standard project offset.
            }
            double distance = PaperAnnotationScale.ModelDistance(database, 5.0);
            return NormalizeOffset(
                new Vector3d(distance, distance, 0.0),
                database);
        }


        private static Vector3d NormalizeOffset(
            Vector3d offset,
            Database database)
        {
            double fallback = Math.Max(
                PaperAnnotationScale.ModelDistance(database, 5.0),
                0.001);
            double maximum = Math.Max(
                PaperAnnotationScale.ModelDistance(database, 8.0),
                fallback * 2.0);
            if (double.IsNaN(offset.X) || double.IsInfinity(offset.X) ||
                double.IsNaN(offset.Y) || double.IsInfinity(offset.Y) ||
                offset.Length < fallback * 0.1)
                return new Vector3d(fallback, fallback, 0.0);
            return offset.Length > maximum
                ? offset.GetNormal() * maximum
                : new Vector3d(offset.X, offset.Y, 0.0);
        }

        private static void TrySetLabelVisible(CivilCogoPoint point)
        {
            if (point == null) return;
            foreach (string name in new[]
            {
                "LabelVisibility", "LabelVisible", "ShowLabel"
            })
            {
                try
                {
                    System.Reflection.PropertyInfo property = point.GetType().GetProperty(
                        name,
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.Instance);
                    if (property == null || !property.CanWrite) continue;
                    if (property.PropertyType == typeof(bool))
                    {
                        property.SetValue(point, true, null);
                        return;
                    }
                }
                catch { }
            }
        }

        private static bool TryReadOffset(
            CivilCogoPoint point,
            Transaction transaction,
            out Vector3d offset)
        {
            offset = new Vector3d(0.0, 0.0, 0.0);
            if (point == null || point.ExtensionDictionary.IsNull) return false;
            DBDictionary dictionary = transaction.GetObject(
                point.ExtensionDictionary,
                OpenMode.ForRead,
                false) as DBDictionary;
            if (dictionary == null || !dictionary.Contains(OffsetRecordName)) return false;
            Xrecord record = transaction.GetObject(
                dictionary.GetAt(OffsetRecordName),
                OpenMode.ForRead,
                false) as Xrecord;
            if (record == null || record.Data == null) return false;
            double dx = 0.0;
            double dy = 0.0;
            bool hasX = false;
            bool hasY = false;
            foreach (TypedValue value in record.Data)
            {
                string text = value.Value as string;
                if (string.IsNullOrWhiteSpace(text)) continue;
                if (text.StartsWith("DX=", StringComparison.OrdinalIgnoreCase))
                    hasX = double.TryParse(
                        text.Substring(3),
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out dx);
                else if (text.StartsWith("DY=", StringComparison.OrdinalIgnoreCase))
                    hasY = double.TryParse(
                        text.Substring(3),
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out dy);
            }
            if (!hasX || !hasY) return false;
            offset = new Vector3d(dx, dy, 0.0);
            return true;
        }

        private static void WriteOffset(
            CivilCogoPoint point,
            Transaction transaction,
            Vector3d offset)
        {
            if (point.ExtensionDictionary.IsNull)
                point.CreateExtensionDictionary();
            DBDictionary dictionary = transaction.GetObject(
                point.ExtensionDictionary,
                OpenMode.ForWrite,
                false) as DBDictionary;
            if (dictionary == null) return;
            Xrecord record;
            if (dictionary.Contains(OffsetRecordName))
            {
                record = transaction.GetObject(
                    dictionary.GetAt(OffsetRecordName),
                    OpenMode.ForWrite,
                    false) as Xrecord;
            }
            else
            {
                record = new Xrecord();
                dictionary.SetAt(OffsetRecordName, record);
                transaction.AddNewlyCreatedDBObject(record, true);
            }
            record.Data = new ResultBuffer(
                new TypedValue(
                    (int)DxfCode.Text,
                    "DX=" + offset.X.ToString("R", CultureInfo.InvariantCulture)),
                new TypedValue(
                    (int)DxfCode.Text,
                    "DY=" + offset.Y.ToString("R", CultureInfo.InvariantCulture)));
        }

        private sealed class CogoLabelItem
        {
            public CogoLabelItem(
                CivilCogoPoint point,
                Point3d anchor,
                Point3d labelLocation,
                double width,
                double height)
            {
                Point = point;
                Anchor = anchor;
                LabelLocation = labelLocation;
                Width = width;
                Height = height;
            }

            public CivilCogoPoint Point { get; private set; }
            public Point3d Anchor { get; private set; }
            public Point3d LabelLocation { get; private set; }
            public double Width { get; private set; }
            public double Height { get; private set; }
        }

        private struct Box2d
        {
            public Box2d(double minX, double minY, double maxX, double maxY)
            {
                MinX = minX;
                MinY = minY;
                MaxX = maxX;
                MaxY = maxY;
            }

            public double MinX;
            public double MinY;
            public double MaxX;
            public double MaxY;

            public bool Intersects(Box2d other)
            {
                return !(MaxX < other.MinX ||
                         other.MaxX < MinX ||
                         MaxY < other.MinY ||
                         other.MaxY < MinY);
            }
        }
    }

    internal sealed class CogoPointStyleResult
    {
        public int PointStylesApplied { get; set; }
        public int LabelStylesApplied { get; set; }
        public int StoredOffsetsRestored { get; set; }
        public int OverlapsMoved { get; set; }
        public int Skipped { get; set; }
        public string Warning { get; set; }
    }

    internal static class CogoPointProjectStyleManager
    {
        private static Database _database;
        private static bool _initialised;
        private static bool _busy;
        private static bool _pending = true;
        private static DateTime _lastRunUtc = DateTime.MinValue;

        public static void Initialize()
        {
            if (_initialised) return;
            _initialised = true;
            AcApplication.Idle += OnIdle;
        }

        public static void Terminate()
        {
            if (!_initialised) return;
            AcApplication.Idle -= OnIdle;
            DetachDatabase();
            _initialised = false;
        }

        public static void Queue()
        {
            _pending = true;
            UniversalDynamicRefreshManager.Queue();
        }

        private static void OnIdle(object sender, EventArgs eventArgs)
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            AttachDatabase(document == null ? null : document.Database);
            if (!_pending || _busy || document == null) return;
            if ((DateTime.UtcNow - _lastRunUtc).TotalMilliseconds < 900.0) return;
            string commandNames = Convert.ToString(
                AcApplication.GetSystemVariable("CMDNAMES"),
                CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(commandNames)) return;

            _busy = true;
            try
            {
                // The universal manager owns automatic mutation. This legacy
                // watcher only forwards the request, preventing duplicate idle
                // transactions and crosshair flicker.
                UniversalDynamicRefreshManager.Queue();
                _pending = false;
                _lastRunUtc = DateTime.UtcNow;
            }
            catch
            {
                _pending = true;
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
            if (_database == null) return;
            _database.ObjectModified += OnObjectChanged;
            _database.ObjectAppended += OnObjectChanged;
            _pending = true;
        }

        private static void DetachDatabase()
        {
            if (_database != null)
            {
                _database.ObjectModified -= OnObjectChanged;
                _database.ObjectAppended -= OnObjectChanged;
            }
            _database = null;
        }

        private static void OnObjectChanged(object sender, ObjectEventArgs eventArgs)
        {
            if (_busy || eventArgs == null || eventArgs.DBObject == null) return;
            if (eventArgs.DBObject is CivilCogoPoint ||
                eventArgs.DBObject is Xrecord ||
                eventArgs.DBObject is DBDictionary)
            {
                _pending = true;
            }
        }
    }
}

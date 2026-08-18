using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilCogoPoint = Autodesk.Civil.DatabaseServices.CogoPoint;

[assembly: CommandClass(typeof(CETools.Civil3D.August18DynamicGridSettingOutCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Dynamic Grid Setting-Out for Survey Production.
    ///
    /// One command accepts preselection, normal multiple selection, Window and
    /// Crossing selection of closed 2D/3D polylines. Every selected boundary is
    /// stored by handle in one linked annotative table. COGO point ObjectIds are
    /// retained and moved in place when possible so Civil 3D labels do not flicker
    /// from unnecessary erase/recreate cycles. The Universal Dynamic Refresh
    /// manager owns automatic dependency updates; this class deliberately has no
    /// second ObjectModified/Idle watcher.
    /// </summary>
    public sealed class August18DynamicGridSettingOutCommands
    {
        private const string TableLinkKey = "CE_DYNAMIC_GRID_TABLE";
        private const string ChildLinkKey = "CE_DYNAMIC_GRID_POINT";
        private const string Version = "1";
        private const double Tolerance = 1e-7;

        [CommandMethod(
            "CE_TOOLS",
            "CE_GRIDSETTINGOUTDYNAMIC",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void CreateDynamicGrid()
        {
            Document document = Active();
            CivilDocument civil = CivilApplication.ActiveDocument;
            if (document == null || civil == null) return;

            List<ObjectId> sourceIds = SelectBoundaries(document);
            if (sourceIds.Count == 0) return;

            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Grid Setting-Out",
                "Create one linked setting-out group from multiple selected closed polylines. Choose Perimeter or Full grid; COGO points and the linked table follow source geometry and annotation-scale changes automatically.");
            settings.AddPositiveDouble(
                "DX", "01 Grid", "X spacing", 10.0,
                "Horizontal grid spacing in drawing units.");
            settings.AddPositiveDouble(
                "DY", "01 Grid", "Y spacing", 10.0,
                "Vertical grid spacing in drawing units.");
            settings.AddChoice(
                "Mode", "01 Grid", "Point layout", "Perimeter",
                "Perimeter creates boundary/grid-edge setting-out points. Full grid fills the selected boundary extents and clips candidates to the closed polyline.",
                new[] { "Perimeter", "Full grid" });
            settings.AddText(
                "Prefix", "02 Numbering", "Point prefix", "G",
                "CE logical names are stored in Raw Description and the linked table so background refresh never triggers Civil 3D duplicate Point Name dialogs.");
            settings.AddPositiveInteger(
                "Start", "02 Numbering", "Starting number", 1,
                "First logical grid point number.");
            settings.AddPaperHeight(
                "PaperHeight", "03 Annotation", "Table paper text height", 2.0,
                "Absolute paper text height. The model-space table size recalculates from the current annotation scale.");
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;

            var link = new DynamicGridLink
            {
                Mode = string.Equals(settings.Text("Mode"), "Full grid", StringComparison.OrdinalIgnoreCase)
                    ? "Full grid"
                    : "Perimeter",
                Dx = settings.Double("DX", 10.0),
                Dy = settings.Double("DY", 10.0),
                Prefix = string.IsNullOrWhiteSpace(settings.Text("Prefix"))
                    ? "G"
                    : settings.Text("Prefix").Trim(),
                StartNumber = settings.Integer("Start", 1),
                PaperHeight = PaperAnnotationScale.NormalizeConfiguredPaperHeight(
                    settings.Double("PaperHeight", 2.0)),
                SourceHandles = sourceIds.Select(id => id.Handle.ToString()).ToList()
            };

            PromptPointResult insertion = document.Editor.GetPoint(
                "\nPick insertion point for the linked Grid Setting-Out table: ");
            if (insertion.Status != PromptStatus.OK) return;

            try
            {
                int points;
                ObjectId tableId = CreateGroup(
                    document,
                    civil,
                    link,
                    insertion.Value.TransformBy(document.Editor.CurrentUserCoordinateSystem),
                    out points);
                document.Editor.SetImpliedSelection(new[] { tableId });
                document.Editor.Regen();
                document.Editor.WriteMessage(
                    "\nCE_GRIDSETTINGOUT complete. Boundaries={0}; mode={1}; linked COGO points={2}; table={3}.",
                    link.SourceHandles.Count,
                    link.Mode,
                    points,
                    tableId.Handle.ToString());
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_GRIDSETTINGOUT stopped. {0}",
                    exception.Message);
            }
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_GRIDSETTINGOUTREFRESH",
            CommandFlags.Modal | CommandFlags.Redraw)]
        public void RefreshDynamicGrids()
        {
            Document document = Active();
            if (document == null) return;
            int refreshed = RefreshAll(document);
            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_GRIDSETTINGOUTREFRESH complete. Linked grid tables refreshed={0}.",
                refreshed);
        }

        internal static int RefreshAll(Document document)
        {
            if (document == null) return 0;
            CivilDocument civil = CivilApplication.ActiveDocument;
            if (civil == null) return 0;

            List<ObjectId> tables = FindLinkedTables(document.Database);
            int refreshed = 0;
            foreach (ObjectId tableId in tables)
            {
                try
                {
                    RefreshOne(document, civil, tableId);
                    refreshed++;
                }
                catch
                {
                    // One stale group must not block all other dynamic grid groups.
                }
            }
            return refreshed;
        }

        private static ObjectId CreateGroup(
            Document document,
            CivilDocument civil,
            DynamicGridLink link,
            Point3d insertion,
            out int pointCount)
        {
            pointCount = 0;
            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                List<GridSource> sources = ReadSources(
                    document.Database,
                    transaction,
                    link.SourceHandles);
                List<GridRecord> records = BuildRecords(sources, link);
                if (records.Count == 0)
                    throw new InvalidOperationException(
                        "The selected boundaries produced no grid setting-out points. Check the spacing and closed polylines.");
                if (records.Count > 10000)
                    throw new InvalidOperationException(
                        "The requested grid would create more than 10,000 COGO points. Increase the X/Y spacing or select fewer boundaries.");

                BlockTableRecord model = OpenModelSpace(
                    document.Database,
                    transaction,
                    OpenMode.ForWrite);
                if (model == null)
                    throw new InvalidOperationException("Model space is unavailable.");

                var table = new Table();
                table.SetDatabaseDefaults(document.Database);
                table.TableStyle = document.Database.Tablestyle;
                table.Position = insertion;
                PaperAnnotationScale.SetAnnotative(table);
                ObjectId tableId = model.AppendEntity(table);
                transaction.AddNewlyCreatedDBObject(table, true);

                link.PointHandles.Clear();
                foreach (GridRecord record in records)
                {
                    ObjectId pointId = CreateCogo(
                        document.Database,
                        civil,
                        transaction,
                        record);
                    link.PointHandles[record.Key] = pointId.Handle.ToString();
                    WriteChildLink(
                        transaction.GetObject(pointId, OpenMode.ForWrite, false),
                        transaction,
                        tableId.Handle.ToString(),
                        record.Key);
                }

                PopulateTable(document.Database, table, records, link);
                WriteTableLink(table, transaction, link);
                transaction.Commit();
                pointCount = records.Count;
                return tableId;
            }
        }

        private static void RefreshOne(
            Document document,
            CivilDocument civil,
            ObjectId tableId)
        {
            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                Table table = transaction.GetObject(
                    tableId,
                    OpenMode.ForWrite,
                    false) as Table;
                if (table == null) return;

                DynamicGridLink link;
                if (!TryReadTableLink(table, transaction, out link)) return;
                List<GridSource> sources = ReadSources(
                    document.Database,
                    transaction,
                    link.SourceHandles);
                if (sources.Count == 0) return;
                List<GridRecord> records = BuildRecords(sources, link);
                if (records.Count == 0 || records.Count > 10000) return;

                var liveKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var updatedMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (GridRecord record in records)
                {
                    liveKeys.Add(record.Key);
                    ObjectId existing = ObjectId.Null;
                    string handleText;
                    if (link.PointHandles.TryGetValue(record.Key, out handleText))
                        existing = ResolveHandle(document.Database, handleText);

                    if (!UpdateCogo(transaction, existing, record))
                    {
                        EraseIfPossible(transaction, existing);
                        existing = CreateCogo(
                            document.Database,
                            civil,
                            transaction,
                            record);
                    }
                    WriteChildLink(
                        transaction.GetObject(existing, OpenMode.ForWrite, false),
                        transaction,
                        tableId.Handle.ToString(),
                        record.Key);
                    updatedMap[record.Key] = existing.Handle.ToString();
                }

                foreach (KeyValuePair<string, string> stale in link.PointHandles)
                {
                    if (liveKeys.Contains(stale.Key)) continue;
                    EraseIfPossible(
                        transaction,
                        ResolveHandle(document.Database, stale.Value));
                }

                link.PointHandles = updatedMap;
                PopulateTable(document.Database, table, records, link);
                WriteTableLink(table, transaction, link);
                transaction.Commit();
            }
        }

        private static ObjectId CreateCogo(
            Database database,
            CivilDocument civil,
            Transaction transaction,
            GridRecord record)
        {
            // The string argument is the raw description. CE deliberately does
            // not force Civil 3D PointName; PointName is drawing-global and was
            // the cause of modal duplicate-name prompts during regeneration.
            ObjectId id = civil.CogoPoints.Add(
                record.Point,
                record.Name,
                true);
            CivilCogoPoint point = transaction.GetObject(
                id,
                OpenMode.ForWrite,
                false) as CivilCogoPoint;
            if (point == null)
                throw new InvalidOperationException("Civil 3D did not return a COGO point.");
            point.RawDescription = record.Name;
            return id;
        }

        private static bool UpdateCogo(
            Transaction transaction,
            ObjectId id,
            GridRecord record)
        {
            if (id.IsNull || id.IsErased) return false;
            CivilCogoPoint point;
            try
            {
                point = transaction.GetObject(
                    id,
                    OpenMode.ForWrite,
                    false) as CivilCogoPoint;
            }
            catch
            {
                return false;
            }
            if (point == null) return false;
            point.Easting = record.Point.X;
            point.Northing = record.Point.Y;
            point.Elevation = record.Point.Z;
            point.RawDescription = record.Name;
            return true;
        }

        private static List<ObjectId> SelectBoundaries(Document document)
        {
            Editor editor = document.Editor;
            var result = new List<ObjectId>();
            PromptSelectionResult implied = editor.SelectImplied();
            if (implied.Status == PromptStatus.OK && implied.Value != null)
                result.AddRange(FilterClosedPolylines(
                    document.Database,
                    implied.Value.GetObjectIds()));

            if (result.Count == 0)
            {
                var options = new PromptSelectionOptions
                {
                    MessageForAdding =
                        "\nSelect MULTIPLE closed grid/site polylines (Window/Crossing selection is supported): ",
                    AllowDuplicates = false,
                    RejectObjectsFromNonCurrentSpace = true
                };
                var filter = new SelectionFilter(new[]
                {
                    new TypedValue((int)DxfCode.Start, "LWPOLYLINE,POLYLINE")
                });
                PromptSelectionResult selected = editor.GetSelection(options, filter);
                if (selected.Status != PromptStatus.OK || selected.Value == null)
                    return result;
                result.AddRange(FilterClosedPolylines(
                    document.Database,
                    selected.Value.GetObjectIds()));
            }

            result = result.Distinct().ToList();
            if (result.Count == 0)
                editor.WriteMessage(
                    "\nCE_GRIDSETTINGOUT requires one or more CLOSED polylines.");
            return result;
        }

        private static IEnumerable<ObjectId> FilterClosedPolylines(
            Database database,
            IEnumerable<ObjectId> ids)
        {
            var result = new List<ObjectId>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids ?? Enumerable.Empty<ObjectId>())
                {
                    Entity entity;
                    try
                    {
                        entity = transaction.GetObject(
                            id,
                            OpenMode.ForRead,
                            false) as Entity;
                    }
                    catch
                    {
                        continue;
                    }
                    if (IsClosedPolyline(entity)) result.Add(id);
                }
            }
            return result;
        }

        private static bool IsClosedPolyline(Entity entity)
        {
            Polyline lw = entity as Polyline;
            if (lw != null) return lw.Closed && lw.NumberOfVertices >= 3;
            Polyline2d p2 = entity as Polyline2d;
            if (p2 != null) return p2.Closed;
            Polyline3d p3 = entity as Polyline3d;
            return p3 != null && p3.Closed;
        }

        private static List<GridSource> ReadSources(
            Database database,
            Transaction transaction,
            IEnumerable<string> handles)
        {
            var result = new List<GridSource>();
            int index = 0;
            foreach (string handle in handles ?? Enumerable.Empty<string>())
            {
                ObjectId id = ResolveHandle(database, handle);
                if (id.IsNull || id.IsErased) continue;
                Entity entity;
                try
                {
                    entity = transaction.GetObject(
                        id,
                        OpenMode.ForRead,
                        false) as Entity;
                }
                catch
                {
                    continue;
                }
                if (!IsClosedPolyline(entity)) continue;
                List<Point3d> vertices = ReadVertices(entity, transaction);
                if (vertices.Count < 3) continue;
                Extents3d extents;
                try { extents = entity.GeometricExtents; }
                catch { continue; }
                result.Add(new GridSource
                {
                    Index = index++,
                    Handle = id.Handle.ToString(),
                    Name = string.IsNullOrWhiteSpace(entity.Layer)
                        ? "SOURCE " + index.ToString(CultureInfo.InvariantCulture)
                        : entity.Layer,
                    Vertices = vertices,
                    MinX = extents.MinPoint.X,
                    MinY = extents.MinPoint.Y,
                    MaxX = extents.MaxPoint.X,
                    MaxY = extents.MaxPoint.Y,
                    Z = vertices.Average(point => point.Z)
                });
            }
            return result;
        }

        private static List<Point3d> ReadVertices(
            Entity entity,
            Transaction transaction)
        {
            var points = new List<Point3d>();
            Polyline lw = entity as Polyline;
            if (lw != null)
            {
                for (int i = 0; i < lw.NumberOfVertices; i++)
                    points.Add(lw.GetPoint3dAt(i));
                return RemoveClosingDuplicate(points);
            }

            Polyline2d p2 = entity as Polyline2d;
            if (p2 != null)
            {
                foreach (ObjectId vertexId in p2)
                {
                    Vertex2d vertex = transaction.GetObject(
                        vertexId,
                        OpenMode.ForRead,
                        false) as Vertex2d;
                    if (vertex != null) points.Add(vertex.Position);
                }
                return RemoveClosingDuplicate(points);
            }

            Polyline3d p3 = entity as Polyline3d;
            if (p3 != null)
            {
                foreach (ObjectId vertexId in p3)
                {
                    PolylineVertex3d vertex = transaction.GetObject(
                        vertexId,
                        OpenMode.ForRead,
                        false) as PolylineVertex3d;
                    if (vertex != null) points.Add(vertex.Position);
                }
            }
            return RemoveClosingDuplicate(points);
        }

        private static List<Point3d> RemoveClosingDuplicate(List<Point3d> points)
        {
            if (points.Count > 1 && points[0].DistanceTo(points[points.Count - 1]) <= Tolerance)
                points.RemoveAt(points.Count - 1);
            return points;
        }

        private static List<GridRecord> BuildRecords(
            IList<GridSource> sources,
            DynamicGridLink link)
        {
            var raw = new List<GridRecord>();
            var coordinateKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (GridSource source in sources)
            {
                List<double> xs = Axis(source.MinX, source.MaxX, link.Dx);
                List<double> ys = Axis(source.MinY, source.MaxY, link.Dy);
                if (xs.Count == 0 || ys.Count == 0) continue;

                if (string.Equals(link.Mode, "Perimeter", StringComparison.OrdinalIgnoreCase))
                {
                    // Always retain real boundary vertices, then add the same
                    // rectangular grid-edge points the original two-corner tool
                    // produced. This preserves the familiar Perimeter behaviour
                    // while allowing multiple selected boundaries.
                    for (int vertexIndex = 0; vertexIndex < source.Vertices.Count; vertexIndex++)
                    {
                        Point3d p = source.Vertices[vertexIndex];
                        AddRecord(raw, coordinateKeys, source, p.X, p.Y, p.Z,
                            "S" + source.Index + "_V" + vertexIndex);
                    }
                    for (int xi = 0; xi < xs.Count; xi++)
                    {
                        AddRecord(raw, coordinateKeys, source, xs[xi], source.MinY, source.Z,
                            "S" + source.Index + "_B" + xi);
                        AddRecord(raw, coordinateKeys, source, xs[xi], source.MaxY, source.Z,
                            "S" + source.Index + "_T" + xi);
                    }
                    for (int yi = 0; yi < ys.Count; yi++)
                    {
                        AddRecord(raw, coordinateKeys, source, source.MinX, ys[yi], source.Z,
                            "S" + source.Index + "_L" + yi);
                        AddRecord(raw, coordinateKeys, source, source.MaxX, ys[yi], source.Z,
                            "S" + source.Index + "_R" + yi);
                    }
                }
                else
                {
                    for (int xi = 0; xi < xs.Count; xi++)
                    {
                        for (int yi = 0; yi < ys.Count; yi++)
                        {
                            double x = xs[xi];
                            double y = ys[yi];
                            if (!InsideOrOnBoundary(source.Vertices, x, y)) continue;
                            AddRecord(raw, coordinateKeys, source, x, y, source.Z,
                                "S" + source.Index + "_X" + xi + "_Y" + yi);
                        }
                    }
                    // Preserve non-grid-aligned boundary vertices for setting-out.
                    for (int vertexIndex = 0; vertexIndex < source.Vertices.Count; vertexIndex++)
                    {
                        Point3d p = source.Vertices[vertexIndex];
                        AddRecord(raw, coordinateKeys, source, p.X, p.Y, p.Z,
                            "S" + source.Index + "_V" + vertexIndex);
                    }
                }
            }

            int sequence = Math.Max(link.StartNumber, 1);
            foreach (GridRecord record in raw)
                record.Name = (string.IsNullOrWhiteSpace(link.Prefix) ? "G" : link.Prefix) +
                    (sequence++).ToString(CultureInfo.InvariantCulture);
            return raw;
        }

        private static void AddRecord(
            ICollection<GridRecord> records,
            ISet<string> coordinateKeys,
            GridSource source,
            double x,
            double y,
            double z,
            string key)
        {
            string coordinate = x.ToString("0.########", CultureInfo.InvariantCulture) + "|" +
                y.ToString("0.########", CultureInfo.InvariantCulture) + "|" +
                z.ToString("0.########", CultureInfo.InvariantCulture);
            if (!coordinateKeys.Add(coordinate)) return;
            records.Add(new GridRecord
            {
                Key = key,
                Source = source.Name,
                SourceHandle = source.Handle,
                Point = new Point3d(x, y, z)
            });
        }

        private static List<double> Axis(double minimum, double maximum, double spacing)
        {
            var result = new List<double>();
            if (!(maximum > minimum) || !(spacing > 0.0)) return result;
            result.Add(minimum);
            double value = minimum + spacing;
            int guard = 0;
            while (value < maximum - Tolerance && guard++ < 2000)
            {
                result.Add(value);
                value += spacing;
            }
            if (Math.Abs(result[result.Count - 1] - maximum) > Tolerance)
                result.Add(maximum);
            return result;
        }

        private static bool InsideOrOnBoundary(
            IList<Point3d> vertices,
            double x,
            double y)
        {
            if (vertices == null || vertices.Count < 3) return false;
            var point = new Point2d(x, y);
            for (int i = 0; i < vertices.Count; i++)
            {
                Point3d a3 = vertices[i];
                Point3d b3 = vertices[(i + 1) % vertices.Count];
                if (DistanceToSegment(
                        point,
                        new Point2d(a3.X, a3.Y),
                        new Point2d(b3.X, b3.Y)) <= Tolerance * 10.0)
                    return true;
            }

            bool inside = false;
            for (int i = 0, j = vertices.Count - 1; i < vertices.Count; j = i++)
            {
                double xi = vertices[i].X;
                double yi = vertices[i].Y;
                double xj = vertices[j].X;
                double yj = vertices[j].Y;
                bool crosses = ((yi > y) != (yj > y)) &&
                    (x < (xj - xi) * (y - yi) /
                        ((yj - yi) == 0.0 ? 1e-20 : (yj - yi)) + xi);
                if (crosses) inside = !inside;
            }
            return inside;
        }

        private static double DistanceToSegment(
            Point2d point,
            Point2d a,
            Point2d b)
        {
            double dx = b.X - a.X;
            double dy = b.Y - a.Y;
            double lengthSquared = dx * dx + dy * dy;
            if (lengthSquared <= 1e-20)
                return Math.Sqrt(
                    (point.X - a.X) * (point.X - a.X) +
                    (point.Y - a.Y) * (point.Y - a.Y));
            double t = ((point.X - a.X) * dx + (point.Y - a.Y) * dy) /
                lengthSquared;
            t = Math.Max(0.0, Math.Min(1.0, t));
            double px = a.X + t * dx;
            double py = a.Y + t * dy;
            double ex = point.X - px;
            double ey = point.Y - py;
            return Math.Sqrt(ex * ex + ey * ey);
        }

        private static void PopulateTable(
            Database database,
            Table table,
            IList<GridRecord> records,
            DynamicGridLink link)
        {
            double textHeight = PaperAnnotationScale.ModelTextHeight(
                database,
                link.PaperHeight > 0.0 ? link.PaperHeight : 2.0);
            table.SetSize(records.Count + 2, 6);
            table.SetRowHeight(Math.Max(textHeight * 1.8, 0.001));
            table.SetColumnWidth(Math.Max(textHeight * 9.0, 0.001));
            table.Columns[1].Width = Math.Max(textHeight * 14.0, 0.001);
            table.Cells[0, 0].TextString =
                "CE GRID SETTING-OUT - " + link.Mode.ToUpperInvariant();
            table.MergeCells(CellRange.Create(table, 0, 0, 0, 5));
            string[] headings = { "POINT", "SOURCE", "X", "Y", "Z", "MODE" };
            for (int column = 0; column < headings.Length; column++)
                table.Cells[1, column].TextString = headings[column];

            for (int index = 0; index < records.Count; index++)
            {
                GridRecord record = records[index];
                int row = index + 2;
                table.Cells[row, 0].TextString = record.Name;
                table.Cells[row, 1].TextString = record.Source;
                table.Cells[row, 2].TextString = record.Point.X.ToString("N3", CultureInfo.CurrentCulture);
                table.Cells[row, 3].TextString = record.Point.Y.ToString("N3", CultureInfo.CurrentCulture);
                table.Cells[row, 4].TextString = record.Point.Z.ToString("N3", CultureInfo.CurrentCulture);
                table.Cells[row, 5].TextString = link.Mode;
            }

            for (int row = 0; row < table.Rows.Count; row++)
                for (int column = 0; column < table.Columns.Count; column++)
                {
                    table.Cells[row, column].Alignment = CellAlignment.MiddleCenter;
                    table.Cells[row, column].TextHeight = textHeight;
                }
            try { table.GenerateLayout(); } catch { }
            try { table.RecordGraphicsModified(true); } catch { }
        }

        private static List<ObjectId> FindLinkedTables(Database database)
        {
            var result = new List<ObjectId>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTableRecord model = OpenModelSpace(database, transaction, OpenMode.ForRead);
                if (model == null) return result;
                foreach (ObjectId id in model)
                {
                    Table table;
                    try
                    {
                        table = transaction.GetObject(
                            id,
                            OpenMode.ForRead,
                            false) as Table;
                    }
                    catch
                    {
                        continue;
                    }
                    if (table == null || table.ExtensionDictionary.IsNull) continue;
                    DBDictionary dictionary = transaction.GetObject(
                        table.ExtensionDictionary,
                        OpenMode.ForRead,
                        false) as DBDictionary;
                    if (dictionary != null && dictionary.Contains(TableLinkKey))
                        result.Add(id);
                }
            }
            return result;
        }

        private static void WriteTableLink(
            Table table,
            Transaction transaction,
            DynamicGridLink link)
        {
            var values = new List<TypedValue>
            {
                new TypedValue((int)DxfCode.Text, "Version=" + Version),
                new TypedValue((int)DxfCode.Text, "Mode=" + link.Mode),
                new TypedValue((int)DxfCode.Text, "DX=" + link.Dx.ToString("R", CultureInfo.InvariantCulture)),
                new TypedValue((int)DxfCode.Text, "DY=" + link.Dy.ToString("R", CultureInfo.InvariantCulture)),
                new TypedValue((int)DxfCode.Text, "Prefix=" + (link.Prefix ?? "G")),
                new TypedValue((int)DxfCode.Text, "Start=" + link.StartNumber.ToString(CultureInfo.InvariantCulture)),
                new TypedValue((int)DxfCode.Text, "PaperHeight=" + link.PaperHeight.ToString("R", CultureInfo.InvariantCulture))
            };
            foreach (string source in link.SourceHandles)
                values.Add(new TypedValue((int)DxfCode.Text, "Source=" + source));
            foreach (KeyValuePair<string, string> point in link.PointHandles.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
                values.Add(new TypedValue((int)DxfCode.Text, "Point=" + point.Key + "|" + point.Value));
            WriteRecord(table, transaction, TableLinkKey, values);
        }

        private static bool TryReadTableLink(
            Table table,
            Transaction transaction,
            out DynamicGridLink link)
        {
            link = new DynamicGridLink();
            TypedValue[] values;
            if (!TryReadRecord(table, transaction, TableLinkKey, out values))
                return false;
            foreach (TypedValue value in values)
            {
                string text = Convert.ToString(value.Value, CultureInfo.InvariantCulture) ?? string.Empty;
                if (text.StartsWith("Mode=", StringComparison.OrdinalIgnoreCase))
                    link.Mode = text.Substring(5);
                else if (text.StartsWith("DX=", StringComparison.OrdinalIgnoreCase))
                    double.TryParse(text.Substring(3), NumberStyles.Float, CultureInfo.InvariantCulture, out link.Dx);
                else if (text.StartsWith("DY=", StringComparison.OrdinalIgnoreCase))
                    double.TryParse(text.Substring(3), NumberStyles.Float, CultureInfo.InvariantCulture, out link.Dy);
                else if (text.StartsWith("Prefix=", StringComparison.OrdinalIgnoreCase))
                    link.Prefix = text.Substring(7);
                else if (text.StartsWith("Start=", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(text.Substring(6), NumberStyles.Integer, CultureInfo.InvariantCulture, out link.StartNumber);
                else if (text.StartsWith("PaperHeight=", StringComparison.OrdinalIgnoreCase))
                    double.TryParse(text.Substring(12), NumberStyles.Float, CultureInfo.InvariantCulture, out link.PaperHeight);
                else if (text.StartsWith("Source=", StringComparison.OrdinalIgnoreCase))
                    link.SourceHandles.Add(text.Substring(7));
                else if (text.StartsWith("Point=", StringComparison.OrdinalIgnoreCase))
                {
                    string body = text.Substring(6);
                    int separator = body.LastIndexOf('|');
                    if (separator > 0 && separator < body.Length - 1)
                        link.PointHandles[body.Substring(0, separator)] = body.Substring(separator + 1);
                }
            }
            if (string.IsNullOrWhiteSpace(link.Mode)) link.Mode = "Perimeter";
            if (string.IsNullOrWhiteSpace(link.Prefix)) link.Prefix = "G";
            if (link.StartNumber < 1) link.StartNumber = 1;
            if (!(link.PaperHeight > 0.0)) link.PaperHeight = 2.0;
            return link.Dx > 0.0 && link.Dy > 0.0 && link.SourceHandles.Count > 0;
        }

        private static void WriteChildLink(
            DBObject owner,
            Transaction transaction,
            string tableHandle,
            string key)
        {
            WriteRecord(
                owner,
                transaction,
                ChildLinkKey,
                new[]
                {
                    new TypedValue((int)DxfCode.Text, "Table=" + (tableHandle ?? string.Empty)),
                    new TypedValue((int)DxfCode.Text, "Key=" + (key ?? string.Empty))
                });
        }

        private static void WriteRecord(
            DBObject owner,
            Transaction transaction,
            string key,
            IEnumerable<TypedValue> values)
        {
            if (owner.ExtensionDictionary.IsNull)
                owner.CreateExtensionDictionary();
            DBDictionary dictionary = transaction.GetObject(
                owner.ExtensionDictionary,
                OpenMode.ForWrite,
                false) as DBDictionary;
            if (dictionary == null) return;
            Xrecord record;
            if (dictionary.Contains(key))
            {
                record = transaction.GetObject(
                    dictionary.GetAt(key),
                    OpenMode.ForWrite,
                    false) as Xrecord;
            }
            else
            {
                record = new Xrecord();
                dictionary.SetAt(key, record);
                transaction.AddNewlyCreatedDBObject(record, true);
            }
            if (record != null)
                record.Data = new ResultBuffer(values.ToArray());
        }

        private static bool TryReadRecord(
            DBObject owner,
            Transaction transaction,
            string key,
            out TypedValue[] values)
        {
            values = new TypedValue[0];
            if (owner == null || owner.ExtensionDictionary.IsNull) return false;
            try
            {
                DBDictionary dictionary = transaction.GetObject(
                    owner.ExtensionDictionary,
                    OpenMode.ForRead,
                    false) as DBDictionary;
                if (dictionary == null || !dictionary.Contains(key)) return false;
                Xrecord record = transaction.GetObject(
                    dictionary.GetAt(key),
                    OpenMode.ForRead,
                    false) as Xrecord;
                if (record == null || record.Data == null) return false;
                values = record.Data.AsArray();
                return values.Length > 0;
            }
            catch
            {
                return false;
            }
        }

        private static ObjectId ResolveHandle(Database database, string text)
        {
            if (database == null || string.IsNullOrWhiteSpace(text)) return ObjectId.Null;
            long value;
            if (!long.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value))
                return ObjectId.Null;
            try
            {
                return database.GetObjectId(false, new Handle(value), 0);
            }
            catch
            {
                return ObjectId.Null;
            }
        }

        private static void EraseIfPossible(Transaction transaction, ObjectId id)
        {
            if (id.IsNull || id.IsErased) return;
            try
            {
                DBObject value = transaction.GetObject(id, OpenMode.ForWrite, false);
                if (value != null && !value.IsErased) value.Erase();
            }
            catch { }
        }

        private static BlockTableRecord OpenModelSpace(
            Database database,
            Transaction transaction,
            OpenMode mode)
        {
            BlockTable table = transaction.GetObject(
                database.BlockTableId,
                OpenMode.ForRead,
                false) as BlockTable;
            return table == null
                ? null
                : transaction.GetObject(
                    table[BlockTableRecord.ModelSpace],
                    mode,
                    false) as BlockTableRecord;
        }

        private static Document Active()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
        }

        private sealed class DynamicGridLink
        {
            internal string Mode = "Perimeter";
            internal double Dx = 10.0;
            internal double Dy = 10.0;
            internal string Prefix = "G";
            internal int StartNumber = 1;
            internal double PaperHeight = 2.0;
            internal List<string> SourceHandles = new List<string>();
            internal Dictionary<string, string> PointHandles =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class GridSource
        {
            internal int Index;
            internal string Handle;
            internal string Name;
            internal List<Point3d> Vertices;
            internal double MinX;
            internal double MinY;
            internal double MaxX;
            internal double MaxY;
            internal double Z;
        }

        private sealed class GridRecord
        {
            internal string Key;
            internal string Name;
            internal string Source;
            internal string SourceHandle;
            internal Point3d Point;
        }
    }
}

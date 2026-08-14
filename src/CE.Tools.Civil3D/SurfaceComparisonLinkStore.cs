using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.DatabaseServices;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

namespace CETools.Civil3D
{
    /// <summary>
    /// Persists base/comparison surface point relationships in the DWG. A linked
    /// DBPoint is the live XY anchor when present. Text/leaders retain one stored
    /// relative offset from that anchor while values are resampled from both
    /// surfaces on every CE refresh.
    /// </summary>
    internal static class SurfaceComparisonLinkStore
    {
        private const string RecordName = "CE_SURFACE_COMPARISON_LINK";
        private const string SchemaVersion = "2";
        private const double Tolerance = 0.0000001;

        public static void LinkEntities(Database database, ObjectId baseSurfaceId, ObjectId comparisonSurfaceId, Point3d point, IEnumerable<ObjectId> entityIds)
        {
            if (database == null || entityIds == null) return;
            List<ObjectId> ids = entityIds.Where(id => !id.IsNull && !id.IsErased).Distinct().ToList();
            if (ids.Count == 0) return;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                ObjectId anchorId = ObjectId.Null;
                Point3d anchorPoint = point;
                foreach (ObjectId id in ids)
                {
                    DBPoint dbPoint;
                    try { dbPoint = transaction.GetObject(id, OpenMode.ForRead, false) as DBPoint; }
                    catch { dbPoint = null; }
                    if (dbPoint == null) continue;
                    anchorId = id;
                    anchorPoint = dbPoint.Position;
                    break;
                }

                foreach (ObjectId entityId in ids)
                {
                    DBObject value = transaction.GetObject(entityId, OpenMode.ForWrite, false);
                    Point3d reference;
                    Vector3d offset = TryReadReferencePoint(value, out reference)
                        ? reference - anchorPoint
                        : Vector3d.Zero;
                    WriteRecord(value, transaction, baseSurfaceId, comparisonSurfaceId, point, anchorId, offset);
                }
                transaction.Commit();
            }
        }

        internal static bool TryResolveLiveAnchor(Database database, ObjectId baseSurfaceId, ObjectId comparisonSurfaceId, double originalX, double originalY, out Point3d point)
        {
            point = Point3d.Origin;
            if (database == null) return false;
            double best = double.MaxValue;
            Point3d candidate = Point3d.Origin;
            bool found = false;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId entityId in ReadEntityIds(database, transaction))
                {
                    DBPoint dbPoint;
                    try { dbPoint = transaction.GetObject(entityId, OpenMode.ForRead, false) as DBPoint; }
                    catch { continue; }
                    if (dbPoint == null) continue;
                    LinkData link;
                    if (!TryReadRecord(database, dbPoint, transaction, out link)) continue;
                    if (link.BaseSurfaceId != baseSurfaceId || link.ComparisonSurfaceId != comparisonSurfaceId) continue;
                    double dx = link.Point.X - originalX;
                    double dy = link.Point.Y - originalY;
                    double distance = dx * dx + dy * dy;
                    if (distance >= best) continue;
                    best = distance;
                    candidate = dbPoint.Position;
                    found = true;
                }
            }
            if (!found || best > 0.000001) return false;
            point = candidate;
            return true;
        }

        public static ObjectId CreateLinkedTable(Database database, Point3d insertionPoint, ObjectId baseSurfaceId, ObjectId comparisonSurfaceId, Point3d point, double textHeight)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = transaction.GetObject(database.CurrentSpaceId, OpenMode.ForWrite, false) as BlockTableRecord;
                if (space == null) return ObjectId.Null;
                var table = new Table();
                table.SetDatabaseDefaults(database);
                table.TableStyle = database.Tablestyle;
                table.Position = insertionPoint;
                ObjectId tableId = space.AppendEntity(table);
                transaction.AddNewlyCreatedDBObject(table, true);
                WriteRecord(table, transaction, baseSurfaceId, comparisonSurfaceId, point, ObjectId.Null, Vector3d.Zero);
                PopulateTable(table, ReadResult(database, transaction, baseSurfaceId, comparisonSurfaceId, point), Math.Max(textHeight, 0.001));
                PaperAnnotationScale.SetAnnotative(table);
                transaction.Commit();
                return tableId;
            }
        }

        public static int RefreshAll(Document document)
        {
            if (document == null) return 0;
            int changed = 0;
            Database database = document.Database;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId entityId in ReadEntityIds(database, transaction))
                {
                    DBObject value;
                    try { value = transaction.GetObject(entityId, OpenMode.ForWrite, false); }
                    catch { continue; }
                    LinkData link;
                    if (!TryReadRecord(database, value, transaction, out link)) continue;

                    Point3d livePoint = link.Point;
                    if (!link.AnchorId.IsNull && !link.AnchorId.IsErased)
                    {
                        DBPoint anchor;
                        try { anchor = transaction.GetObject(link.AnchorId, OpenMode.ForWrite, false) as DBPoint; }
                        catch { anchor = null; }
                        if (anchor != null)
                            livePoint = anchor.Position;
                    }
                    else
                    {
                        DBPoint self = value as DBPoint;
                        if (self != null)
                        {
                            livePoint = self.Position;
                            link.AnchorId = self.ObjectId;
                            link.Offset = Vector3d.Zero;
                        }
                    }

                    ComparisonResult result;
                    try
                    {
                        result = ReadResult(database, transaction, link.BaseSurfaceId, link.ComparisonSurfaceId, livePoint);
                    }
                    catch { continue; }

                    DBPoint sourcePoint = value as DBPoint;
                    if (sourcePoint != null && (link.AnchorId.IsNull || sourcePoint.ObjectId == link.AnchorId))
                    {
                        Point3d desired = new Point3d(result.Point.X, result.Point.Y, result.ComparisonZ);
                        if (sourcePoint.Position.DistanceTo(desired) > Tolerance)
                        {
                            sourcePoint.Position = desired;
                            changed++;
                        }
                    }
                    else if (UpdateEntityPosition(value, result.Point, link.Offset))
                    {
                        changed++;
                    }

                    if (UpdateEntityContents(value, result)) changed++;
                    // Keep link.Point as the ORIGINAL XY identity. The live anchor
                    // is stored separately and may move; linked tables use the
                    // original XY to resolve that same anchor on later refreshes.
                    WriteRecord(value, transaction, link.BaseSurfaceId, link.ComparisonSurfaceId, link.Point, link.AnchorId, link.Offset);
                }
                transaction.Commit();
            }
            return changed;
        }

        public static int CountLinkedEntities(Database database)
        {
            if (database == null) return 0;
            int count = 0;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId entityId in ReadEntityIds(database, transaction))
                {
                    DBObject value;
                    try { value = transaction.GetObject(entityId, OpenMode.ForRead, false); }
                    catch { continue; }
                    LinkData link;
                    if (TryReadRecord(database, value, transaction, out link)) count++;
                }
            }
            return count;
        }

        private static bool UpdateEntityPosition(DBObject value, Point3d anchor, Vector3d offset)
        {
            Point3d desired = anchor + offset;
            var text = value as MText;
            if (text != null)
            {
                if (text.Location.DistanceTo(desired) <= Tolerance) return false;
                text.Location = desired;
                return true;
            }

            var leader = value as MLeader;
            if (leader != null)
            {
                try
                {
                    Point3d current = leader.TextLocation;
                    Vector3d delta = desired - current;
                    if (delta.Length <= Tolerance) return false;
                    leader.TransformBy(Matrix3d.Displacement(delta));
                    return true;
                }
                catch { return false; }
            }
            return false;
        }

        private static bool UpdateEntityContents(DBObject value, ComparisonResult result)
        {
            var text = value as MText;
            if (text != null)
            {
                string contents = BuildContents(result, ReadFirstLine(text.Contents));
                bool changed = !string.Equals(text.Contents, contents, StringComparison.Ordinal);
                if (changed) text.Contents = contents;
                try { PaperAnnotationScale.SetAnnotative(text); } catch { }
                return changed;
            }
            var leader = value as MLeader;
            if (leader != null && leader.ContentType == ContentType.MTextContent)
            {
                MText leaderText = leader.MText;
                if (leaderText == null) return false;
                string contents = BuildContents(result, ReadFirstLine(leaderText.Contents));
                bool changed = !string.Equals(leaderText.Contents, contents, StringComparison.Ordinal);
                if (changed)
                {
                    leaderText.Contents = contents;
                    leader.MText = leaderText;
                }
                try { PaperAnnotationScale.SetAnnotative(leader); } catch { }
                return changed;
            }
            var table = value as Table;
            if (table != null)
            {
                double height = 2.0;
                try
                {
                    if (table.Rows.Count > 1 && table.Columns.Count > 0)
                        height = Math.Max(table.Cells[1, 0].TextHeight ?? height, 0.001);
                }
                catch { }
                PopulateTable(table, result, height);
                try { PaperAnnotationScale.SetAnnotative(table); } catch { }
                return true;
            }
            return false;
        }

        private static void PopulateTable(Table table, ComparisonResult result, double textHeight)
        {
            string[] headings = { "Base Surface", "Comparison Surface", "X", "Y", "Base Z", "Comparison Z", "Difference", "Result" };
            string[] values = { result.BaseName, result.ComparisonName, result.Point.X.ToString("N3", CultureInfo.CurrentCulture), result.Point.Y.ToString("N3", CultureInfo.CurrentCulture), result.BaseZ.ToString("N3", CultureInfo.CurrentCulture), result.ComparisonZ.ToString("N3", CultureInfo.CurrentCulture), result.Difference.ToString("N3", CultureInfo.CurrentCulture), result.Classification };
            table.SetSize(3, headings.Length);
            table.SetRowHeight(textHeight * 2.4);
            for (int column = 0; column < headings.Length; column++)
            {
                table.Columns[column].Width = textHeight * Math.Max(11.0, Math.Min(24.0, headings[column].Length + 4.0));
                table.Cells[1, column].TextString = headings[column];
                table.Cells[2, column].TextString = values[column];
                table.Cells[1, column].TextHeight = textHeight;
                table.Cells[2, column].TextHeight = textHeight;
                table.Cells[1, column].Alignment = CellAlignment.MiddleCenter;
                table.Cells[2, column].Alignment = CellAlignment.MiddleCenter;
            }
            table.MergeCells(CellRange.Create(table, 0, 0, 0, headings.Length - 1));
            table.Cells[0, 0].TextString = "DYNAMIC SURFACE COMPARISON";
            table.Cells[0, 0].TextHeight = textHeight * 1.15;
            table.Cells[0, 0].Alignment = CellAlignment.MiddleCenter;
            table.GenerateLayout();
        }

        private static ComparisonResult ReadResult(Database database, Transaction transaction, ObjectId baseSurfaceId, ObjectId comparisonSurfaceId, Point3d point)
        {
            CivilSurface baseSurface = transaction.GetObject(baseSurfaceId, OpenMode.ForRead, false) as CivilSurface;
            CivilSurface comparisonSurface = transaction.GetObject(comparisonSurfaceId, OpenMode.ForRead, false) as CivilSurface;
            if (baseSurface == null || comparisonSurface == null) throw new InvalidOperationException("A linked surface is unavailable.");
            double baseZ = baseSurface.FindElevationAtXY(point.X, point.Y);
            double comparisonZ = comparisonSurface.FindElevationAtXY(point.X, point.Y);
            return new ComparisonResult(baseSurface.Name, comparisonSurface.Name, new Point3d(point.X, point.Y, comparisonZ), baseZ, comparisonZ);
        }

        private static string BuildContents(ComparisonResult result, string firstLine)
        {
            var lines = new List<string>();
            if (!string.IsNullOrWhiteSpace(firstLine) &&
                !firstLine.Contains("→") &&
                !firstLine.StartsWith("BASE Z", StringComparison.OrdinalIgnoreCase))
                lines.Add(firstLine.Trim());
            lines.Add(result.BaseName + " → " + result.ComparisonName);
            lines.Add("X: " + result.Point.X.ToString("N3", CultureInfo.CurrentCulture));
            lines.Add("Y: " + result.Point.Y.ToString("N3", CultureInfo.CurrentCulture));
            lines.Add("BASE Z: " + result.BaseZ.ToString("N3", CultureInfo.CurrentCulture));
            lines.Add("COMPARISON Z: " + result.ComparisonZ.ToString("N3", CultureInfo.CurrentCulture));
            lines.Add("DIFF: " + result.Difference.ToString("N3", CultureInfo.CurrentCulture));
            lines.Add(result.Classification);
            return string.Join("\\P", lines);
        }

        private static string ReadFirstLine(string contents)
        {
            if (string.IsNullOrWhiteSpace(contents)) return string.Empty;
            int index = contents.IndexOf("\\P", StringComparison.Ordinal);
            return (index < 0 ? contents : contents.Substring(0, index)).Trim();
        }

        private static bool TryReadReferencePoint(DBObject value, out Point3d point)
        {
            point = Point3d.Origin;
            DBPoint dbPoint = value as DBPoint;
            if (dbPoint != null) { point = dbPoint.Position; return true; }
            MText text = value as MText;
            if (text != null) { point = text.Location; return true; }
            MLeader leader = value as MLeader;
            if (leader != null)
            {
                try { point = leader.TextLocation; return true; }
                catch { }
            }
            return false;
        }

        private static void WriteRecord(DBObject target, Transaction transaction, ObjectId baseSurfaceId, ObjectId comparisonSurfaceId, Point3d point, ObjectId anchorId, Vector3d offset)
        {
            if (target.ExtensionDictionary.IsNull) target.CreateExtensionDictionary();
            DBDictionary dictionary = transaction.GetObject(target.ExtensionDictionary, OpenMode.ForWrite, false) as DBDictionary;
            if (dictionary == null) return;
            Xrecord record;
            if (dictionary.Contains(RecordName)) record = transaction.GetObject(dictionary.GetAt(RecordName), OpenMode.ForWrite, false) as Xrecord;
            else { record = new Xrecord(); dictionary.SetAt(RecordName, record); transaction.AddNewlyCreatedDBObject(record, true); }
            if (record == null) return;
            record.Data = new ResultBuffer(
                new TypedValue((int)DxfCode.Text, "Schema=" + SchemaVersion),
                new TypedValue((int)DxfCode.Text, "Base=" + baseSurfaceId.Handle),
                new TypedValue((int)DxfCode.Text, "Comparison=" + comparisonSurfaceId.Handle),
                new TypedValue((int)DxfCode.Text, "Anchor=" + (anchorId.IsNull ? string.Empty : anchorId.Handle.ToString())),
                new TypedValue((int)DxfCode.Text, "X=" + point.X.ToString("R", CultureInfo.InvariantCulture)),
                new TypedValue((int)DxfCode.Text, "Y=" + point.Y.ToString("R", CultureInfo.InvariantCulture)),
                new TypedValue((int)DxfCode.Text, "Z=" + point.Z.ToString("R", CultureInfo.InvariantCulture)),
                new TypedValue((int)DxfCode.Text, "DX=" + offset.X.ToString("R", CultureInfo.InvariantCulture)),
                new TypedValue((int)DxfCode.Text, "DY=" + offset.Y.ToString("R", CultureInfo.InvariantCulture)),
                new TypedValue((int)DxfCode.Text, "DZ=" + offset.Z.ToString("R", CultureInfo.InvariantCulture)));
        }

        private static bool TryReadRecord(Database database, DBObject target, Transaction transaction, out LinkData link)
        {
            link = null;
            if (target == null || target.ExtensionDictionary.IsNull) return false;
            DBDictionary dictionary = transaction.GetObject(target.ExtensionDictionary, OpenMode.ForRead, false) as DBDictionary;
            if (dictionary == null || !dictionary.Contains(RecordName)) return false;
            Xrecord record = transaction.GetObject(dictionary.GetAt(RecordName), OpenMode.ForRead, false) as Xrecord;
            if (record == null || record.Data == null) return false;
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (TypedValue item in record.Data)
            {
                string text = item.Value as string;
                int equals = string.IsNullOrWhiteSpace(text) ? -1 : text.IndexOf('=');
                if (equals > 0) values[text.Substring(0, equals)] = text.Substring(equals + 1);
            }
            ObjectId baseId; ObjectId comparisonId; ObjectId anchorId = ObjectId.Null;
            double x, y, z, dx = 0.0, dy = 0.0, dz = 0.0;
            if (!Resolve(database, Read(values, "Base"), out baseId) ||
                !Resolve(database, Read(values, "Comparison"), out comparisonId) ||
                !double.TryParse(Read(values, "X"), NumberStyles.Float, CultureInfo.InvariantCulture, out x) ||
                !double.TryParse(Read(values, "Y"), NumberStyles.Float, CultureInfo.InvariantCulture, out y) ||
                !double.TryParse(Read(values, "Z"), NumberStyles.Float, CultureInfo.InvariantCulture, out z)) return false;
            string anchor = Read(values, "Anchor");
            if (!string.IsNullOrWhiteSpace(anchor)) Resolve(database, anchor, out anchorId);
            double.TryParse(Read(values, "DX"), NumberStyles.Float, CultureInfo.InvariantCulture, out dx);
            double.TryParse(Read(values, "DY"), NumberStyles.Float, CultureInfo.InvariantCulture, out dy);
            double.TryParse(Read(values, "DZ"), NumberStyles.Float, CultureInfo.InvariantCulture, out dz);
            link = new LinkData(baseId, comparisonId, new Point3d(x, y, z), anchorId, new Vector3d(dx, dy, dz));
            return true;
        }

        private static List<ObjectId> ReadEntityIds(Database database, Transaction transaction)
        {
            var result = new List<ObjectId>();
            BlockTable table = transaction.GetObject(database.BlockTableId, OpenMode.ForRead, false) as BlockTable;
            if (table == null) return result;
            foreach (ObjectId blockId in table)
            {
                BlockTableRecord block = transaction.GetObject(blockId, OpenMode.ForRead, false) as BlockTableRecord;
                if (block == null || block.IsFromExternalReference) continue;
                foreach (ObjectId entityId in block) result.Add(entityId);
            }
            return result;
        }

        private static bool Resolve(Database database, string text, out ObjectId id)
        {
            id = ObjectId.Null; long value;
            if (!long.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)) return false;
            try { id = database.GetObjectId(false, new Handle(value), 0); return !id.IsNull && !id.IsErased; }
            catch { return false; }
        }

        private static string Read(IDictionary<string, string> values, string key)
        {
            string value; return values.TryGetValue(key, out value) ? value : string.Empty;
        }

        private sealed class LinkData
        {
            public LinkData(ObjectId baseId, ObjectId comparisonId, Point3d point, ObjectId anchorId, Vector3d offset)
            {
                BaseSurfaceId = baseId;
                ComparisonSurfaceId = comparisonId;
                Point = point;
                AnchorId = anchorId;
                Offset = offset;
            }
            public ObjectId BaseSurfaceId { get; private set; }
            public ObjectId ComparisonSurfaceId { get; private set; }
            public Point3d Point { get; private set; }
            public ObjectId AnchorId { get; set; }
            public Vector3d Offset { get; set; }
        }

        private sealed class ComparisonResult
        {
            public ComparisonResult(string baseName, string comparisonName, Point3d point, double baseZ, double comparisonZ)
            {
                BaseName = baseName ?? string.Empty;
                ComparisonName = comparisonName ?? string.Empty;
                Point = point;
                BaseZ = baseZ;
                ComparisonZ = comparisonZ;
                Difference = comparisonZ - baseZ;
                Classification = Difference > 0.0005 ? "Fill / raise" : Difference < -0.0005 ? "Cut / lower" : "No material difference";
            }
            public string BaseName { get; private set; }
            public string ComparisonName { get; private set; }
            public Point3d Point { get; private set; }
            public double BaseZ { get; private set; }
            public double ComparisonZ { get; private set; }
            public double Difference { get; private set; }
            public string Classification { get; private set; }
        }
    }
}

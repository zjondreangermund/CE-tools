using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.DatabaseServices;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

namespace CETools.Civil3D
{
    /// <summary>
    /// Persists base/comparison surface point relationships in the DWG. Linked
    /// annotations and tables are recalculated after either surface rebuilds.
    /// </summary>
    internal static class SurfaceComparisonLinkStore
    {
        private const string RecordName = "CE_SURFACE_COMPARISON_LINK";
        private const string SchemaVersion = "1";
        private const double Tolerance = 0.0000001;

        public static void LinkEntities(Database database, ObjectId baseSurfaceId, ObjectId comparisonSurfaceId, Point3d point, IEnumerable<ObjectId> entityIds)
        {
            if (database == null || entityIds == null) return;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId entityId in entityIds)
                {
                    if (entityId.IsNull || entityId.IsErased) continue;
                    DBObject value = transaction.GetObject(entityId, OpenMode.ForWrite, false);
                    WriteRecord(value, transaction, baseSurfaceId, comparisonSurfaceId, point);
                }
                transaction.Commit();
            }
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
                WriteRecord(table, transaction, baseSurfaceId, comparisonSurfaceId, point);
                PopulateTable(table, ReadResult(database, transaction, baseSurfaceId, comparisonSurfaceId, point), Math.Max(textHeight, 0.001));
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
                    ComparisonResult result;
                    try { result = ReadResult(database, transaction, link.BaseSurfaceId, link.ComparisonSurfaceId, link.Point); }
                    catch { continue; }
                    Entity entity = value as Entity;
                    if (entity != null && !(entity is Table) && Math.Abs(result.ComparisonZ - link.Point.Z) > Tolerance)
                    {
                        try { entity.TransformBy(Matrix3d.Displacement(new Vector3d(0.0, 0.0, result.ComparisonZ - link.Point.Z))); }
                        catch { }
                    }
                    bool updated = UpdateEntity(value, result);
                    WriteRecord(value, transaction, link.BaseSurfaceId, link.ComparisonSurfaceId, new Point3d(link.Point.X, link.Point.Y, result.ComparisonZ));
                    if (updated) changed++;
                }
                transaction.Commit();
            }
            return changed;
        }

        private static bool UpdateEntity(DBObject value, ComparisonResult result)
        {
            string contents = BuildContents(result);
            var text = value as MText;
            if (text != null)
            {
                if (string.Equals(text.Contents, contents, StringComparison.Ordinal)) return false;
                text.Contents = contents;
                return true;
            }
            var leader = value as MLeader;
            if (leader != null && leader.ContentType == ContentType.MTextContent)
            {
                MText leaderText = leader.MText;
                if (leaderText == null) return false;
                if (string.Equals(leaderText.Contents, contents, StringComparison.Ordinal)) return false;
                leaderText.Contents = contents;
                leader.MText = leaderText;
                return true;
            }
            var table = value as Table;
            if (table != null)
            {
                double height = table.Rows.Count > 0 && table.Columns.Count > 0 ? Math.Max(table.Cells[0, 0].TextHeight ?? 2.5, 0.001) : 2.5;
                PopulateTable(table, result, height);
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

        private static string BuildContents(ComparisonResult result)
        {
            return string.Join("\\P", result.BaseName + " → " + result.ComparisonName, "BASE Z: " + result.BaseZ.ToString("N3", CultureInfo.CurrentCulture), "COMPARISON Z: " + result.ComparisonZ.ToString("N3", CultureInfo.CurrentCulture), "DIFF: " + result.Difference.ToString("N3", CultureInfo.CurrentCulture), result.Classification);
        }

        private static void WriteRecord(DBObject target, Transaction transaction, ObjectId baseSurfaceId, ObjectId comparisonSurfaceId, Point3d point)
        {
            if (target.ExtensionDictionary.IsNull) target.CreateExtensionDictionary();
            DBDictionary dictionary = transaction.GetObject(target.ExtensionDictionary, OpenMode.ForWrite, false) as DBDictionary;
            if (dictionary == null) return;
            Xrecord record;
            if (dictionary.Contains(RecordName)) record = transaction.GetObject(dictionary.GetAt(RecordName), OpenMode.ForWrite, false) as Xrecord;
            else { record = new Xrecord(); dictionary.SetAt(RecordName, record); transaction.AddNewlyCreatedDBObject(record, true); }
            if (record == null) return;
            record.Data = new ResultBuffer(new TypedValue((int)DxfCode.Text, "Schema=" + SchemaVersion), new TypedValue((int)DxfCode.Text, "Base=" + baseSurfaceId.Handle), new TypedValue((int)DxfCode.Text, "Comparison=" + comparisonSurfaceId.Handle), new TypedValue((int)DxfCode.Text, "X=" + point.X.ToString("R", CultureInfo.InvariantCulture)), new TypedValue((int)DxfCode.Text, "Y=" + point.Y.ToString("R", CultureInfo.InvariantCulture)), new TypedValue((int)DxfCode.Text, "Z=" + point.Z.ToString("R", CultureInfo.InvariantCulture)));
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
            ObjectId baseId; ObjectId comparisonId; double x; double y; double z;
            if (!Resolve(database, Read(values, "Base"), out baseId) || !Resolve(database, Read(values, "Comparison"), out comparisonId) || !double.TryParse(Read(values, "X"), NumberStyles.Float, CultureInfo.InvariantCulture, out x) || !double.TryParse(Read(values, "Y"), NumberStyles.Float, CultureInfo.InvariantCulture, out y) || !double.TryParse(Read(values, "Z"), NumberStyles.Float, CultureInfo.InvariantCulture, out z)) return false;
            link = new LinkData(baseId, comparisonId, new Point3d(x, y, z));
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
            public LinkData(ObjectId baseId, ObjectId comparisonId, Point3d point) { BaseSurfaceId = baseId; ComparisonSurfaceId = comparisonId; Point = point; }
            public ObjectId BaseSurfaceId { get; private set; }
            public ObjectId ComparisonSurfaceId { get; private set; }
            public Point3d Point { get; private set; }
        }

        private sealed class ComparisonResult
        {
            public ComparisonResult(string baseName, string comparisonName, Point3d point, double baseZ, double comparisonZ)
            {
                BaseName = baseName ?? string.Empty; ComparisonName = comparisonName ?? string.Empty; Point = point; BaseZ = baseZ; ComparisonZ = comparisonZ; Difference = comparisonZ - baseZ;
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

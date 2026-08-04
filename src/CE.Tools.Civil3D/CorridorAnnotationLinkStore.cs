using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.DatabaseServices;
using CivilCogoPoint = Autodesk.Civil.DatabaseServices.CogoPoint;
using CivilCorridor = Autodesk.Civil.DatabaseServices.Corridor;

namespace CETools.Civil3D
{
    /// <summary>
    /// Stores corridor annotation relationships in the DWG and refreshes output
    /// after source-point or corridor design changes.
    /// </summary>
    internal static class CorridorAnnotationLinkStore
    {
        private const string RecordName = "CE_CORRIDOR_ANNOTATION_LINK";
        private const string SchemaVersion = "1";
        private const double Tolerance = 0.0000001;

        public static void Link(
            Database database,
            ObjectId corridorId,
            ObjectId sourcePointId,
            string pointName,
            Point3d point,
            IEnumerable<ObjectId> outputIds)
        {
            if (database == null || corridorId.IsNull || sourcePointId.IsNull) return;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                DBObject source = transaction.GetObject(sourcePointId, OpenMode.ForWrite, false);
                Write(source, transaction, corridorId, sourcePointId, pointName, point);
                if (outputIds != null)
                {
                    foreach (ObjectId id in outputIds)
                    {
                        if (id.IsNull || id.IsErased || id == sourcePointId) continue;
                        DBObject output = transaction.GetObject(id, OpenMode.ForWrite, false);
                        Write(output, transaction, corridorId, sourcePointId, pointName, point);
                    }
                }
                transaction.Commit();
            }
        }

        public static ObjectId CreateLinkedTable(
            Database database,
            Point3d insertion,
            ObjectId corridorId,
            ObjectId sourcePointId,
            string pointName,
            Point3d point,
            double height)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = transaction.GetObject(
                    database.CurrentSpaceId,
                    OpenMode.ForWrite,
                    false) as BlockTableRecord;
                if (space == null) return ObjectId.Null;
                var table = new Table();
                table.SetDatabaseDefaults(database);
                table.TableStyle = database.Tablestyle;
                table.Position = insertion;
                ObjectId tableId = space.AppendEntity(table);
                transaction.AddNewlyCreatedDBObject(table, true);
                Write(table, transaction, corridorId, sourcePointId, pointName, point);
                Populate(table, Read(transaction, corridorId, sourcePointId, pointName), height);
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
                foreach (ObjectId id in EntityIds(database, transaction))
                {
                    DBObject value;
                    try { value = transaction.GetObject(id, OpenMode.ForWrite, false); }
                    catch { continue; }
                    LinkData link;
                    if (!TryRead(database, value, transaction, out link)) continue;
                    CorridorResult result;
                    try
                    {
                        result = Read(
                            transaction,
                            link.CorridorId,
                            link.SourcePointId,
                            link.PointName);
                    }
                    catch { continue; }

                    bool moved = false;
                    Entity entity = value as Entity;
                    if (id != link.SourcePointId && entity != null && !(entity is Table))
                    {
                        Vector3d displacement = result.Point - link.LastPoint;
                        if (displacement.Length > Tolerance)
                        {
                            try
                            {
                                entity.TransformBy(Matrix3d.Displacement(displacement));
                                moved = true;
                            }
                            catch { moved = false; }
                        }
                    }
                    bool updated = Update(value, result);
                    Write(
                        value,
                        transaction,
                        link.CorridorId,
                        link.SourcePointId,
                        link.PointName,
                        result.Point);
                    if (moved || updated) changed++;
                }
                transaction.Commit();
            }
            return changed;
        }

        private static CorridorResult Read(
            Transaction transaction,
            ObjectId corridorId,
            ObjectId sourcePointId,
            string pointName)
        {
            CivilCorridor corridor = transaction.GetObject(
                corridorId,
                OpenMode.ForRead,
                false) as CivilCorridor;
            if (corridor == null)
                throw new InvalidOperationException("The linked corridor is unavailable.");
            Point3d point = ReadPoint(transaction, sourcePointId);
            int regions = 0;
            foreach (Baseline baseline in corridor.Baselines)
                regions += baseline.BaselineRegions.Count;
            return new CorridorResult(
                pointName,
                corridor.Name,
                point,
                corridor.Baselines.Count,
                regions,
                corridor.CorridorSurfaces.Count,
                corridor.IsOutOfDate);
        }

        private static Point3d ReadPoint(Transaction transaction, ObjectId id)
        {
            DBObject value = transaction.GetObject(id, OpenMode.ForRead, false);
            DBPoint point = value as DBPoint;
            if (point != null) return point.Position;
            CivilCogoPoint cogo = value as CivilCogoPoint;
            if (cogo != null)
                return new Point3d(cogo.Easting, cogo.Northing, cogo.Elevation);
            throw new InvalidOperationException("The linked corridor point is unavailable.");
        }

        private static bool Update(DBObject value, CorridorResult result)
        {
            string contents = Contents(result);
            MText text = value as MText;
            if (text != null)
            {
                if (string.Equals(text.Contents, contents, StringComparison.Ordinal)) return false;
                text.Contents = contents;
                return true;
            }
            MLeader leader = value as MLeader;
            if (leader != null && leader.ContentType == ContentType.MTextContent)
            {
                MText leaderText = leader.MText;
                if (leaderText == null ||
                    string.Equals(leaderText.Contents, contents, StringComparison.Ordinal))
                    return false;
                leaderText.Contents = contents;
                leader.MText = leaderText;
                return true;
            }
            CivilCogoPoint cogo = value as CivilCogoPoint;
            if (cogo != null)
            {
                string plain = contents.Replace("\\P", "; ");
                if (string.Equals(cogo.RawDescription, plain, StringComparison.Ordinal))
                    return false;
                cogo.RawDescription = plain;
                return true;
            }
            Table table = value as Table;
            if (table != null)
            {
                double height = table.Rows.Count > 0 && table.Columns.Count > 0
                    ? Math.Max(table.Cells[0, 0].TextHeight ?? 2.5, 0.001)
                    : 2.5;
                Populate(table, result, height);
                return true;
            }
            return false;
        }

        private static string Contents(CorridorResult result)
        {
            return string.Join(
                "\\P",
                "Point Name: " + result.PointName,
                "Corridor: " + result.CorridorName,
                "Baselines: " + result.Baselines.ToString(CultureInfo.InvariantCulture),
                "Regions: " + result.Regions.ToString(CultureInfo.InvariantCulture),
                "Surfaces: " + result.Surfaces.ToString(CultureInfo.InvariantCulture),
                "X: " + result.Point.X.ToString("N3", CultureInfo.CurrentCulture),
                "Y: " + result.Point.Y.ToString("N3", CultureInfo.CurrentCulture),
                "Z: " + result.Point.Z.ToString("N3", CultureInfo.CurrentCulture),
                "Out of date: " + (result.OutOfDate ? "Yes" : "No"));
        }

        private static void Populate(Table table, CorridorResult result, double height)
        {
            string[] headings =
            {
                "Point Name", "Corridor", "Baselines", "Regions",
                "Surfaces", "X", "Y", "Z", "Out of Date"
            };
            string[] values =
            {
                result.PointName,
                result.CorridorName,
                result.Baselines.ToString(CultureInfo.InvariantCulture),
                result.Regions.ToString(CultureInfo.InvariantCulture),
                result.Surfaces.ToString(CultureInfo.InvariantCulture),
                result.Point.X.ToString("N3", CultureInfo.CurrentCulture),
                result.Point.Y.ToString("N3", CultureInfo.CurrentCulture),
                result.Point.Z.ToString("N3", CultureInfo.CurrentCulture),
                result.OutOfDate ? "Yes" : "No"
            };
            table.SetSize(3, headings.Length);
            table.SetRowHeight(height * 2.4);
            for (int column = 0; column < headings.Length; column++)
            {
                table.Columns[column].Width = height *
                    Math.Max(10.0, Math.Min(22.0, headings[column].Length + 4.0));
                table.Cells[1, column].TextString = headings[column];
                table.Cells[2, column].TextString = values[column];
                table.Cells[1, column].TextHeight = height;
                table.Cells[2, column].TextHeight = height;
                table.Cells[1, column].Alignment = CellAlignment.MiddleCenter;
                table.Cells[2, column].Alignment = CellAlignment.MiddleCenter;
            }
            table.MergeCells(CellRange.Create(table, 0, 0, 0, headings.Length - 1));
            table.Cells[0, 0].TextString = "DYNAMIC CORRIDOR POINT";
            table.Cells[0, 0].TextHeight = height * 1.15;
            table.Cells[0, 0].Alignment = CellAlignment.MiddleCenter;
            table.GenerateLayout();
        }

        private static void Write(
            DBObject target,
            Transaction transaction,
            ObjectId corridorId,
            ObjectId sourcePointId,
            string pointName,
            Point3d point)
        {
            if (target.ExtensionDictionary.IsNull) target.CreateExtensionDictionary();
            DBDictionary dictionary = transaction.GetObject(
                target.ExtensionDictionary,
                OpenMode.ForWrite,
                false) as DBDictionary;
            if (dictionary == null) return;
            Xrecord record;
            if (dictionary.Contains(RecordName))
                record = transaction.GetObject(
                    dictionary.GetAt(RecordName),
                    OpenMode.ForWrite,
                    false) as Xrecord;
            else
            {
                record = new Xrecord();
                dictionary.SetAt(RecordName, record);
                transaction.AddNewlyCreatedDBObject(record, true);
            }
            if (record == null) return;
            record.Data = new ResultBuffer(
                new TypedValue((int)DxfCode.Text, "Schema=" + SchemaVersion),
                new TypedValue((int)DxfCode.Text, "Corridor=" + corridorId.Handle),
                new TypedValue((int)DxfCode.Text, "Source=" + sourcePointId.Handle),
                new TypedValue((int)DxfCode.Text, "PointName=" + (pointName ?? string.Empty)),
                new TypedValue((int)DxfCode.Text, "X=" + point.X.ToString("R", CultureInfo.InvariantCulture)),
                new TypedValue((int)DxfCode.Text, "Y=" + point.Y.ToString("R", CultureInfo.InvariantCulture)),
                new TypedValue((int)DxfCode.Text, "Z=" + point.Z.ToString("R", CultureInfo.InvariantCulture)));
        }

        private static bool TryRead(
            Database database,
            DBObject target,
            Transaction transaction,
            out LinkData link)
        {
            link = null;
            if (target == null || target.ExtensionDictionary.IsNull) return false;
            DBDictionary dictionary = transaction.GetObject(
                target.ExtensionDictionary,
                OpenMode.ForRead,
                false) as DBDictionary;
            if (dictionary == null || !dictionary.Contains(RecordName)) return false;
            Xrecord record = transaction.GetObject(
                dictionary.GetAt(RecordName),
                OpenMode.ForRead,
                false) as Xrecord;
            if (record == null || record.Data == null) return false;
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (TypedValue item in record.Data)
            {
                string text = item.Value as string;
                int equals = string.IsNullOrWhiteSpace(text) ? -1 : text.IndexOf('=');
                if (equals > 0) values[text.Substring(0, equals)] = text.Substring(equals + 1);
            }
            ObjectId corridorId;
            ObjectId sourceId;
            double x;
            double y;
            double z;
            if (!Resolve(database, Read(values, "Corridor"), out corridorId) ||
                !Resolve(database, Read(values, "Source"), out sourceId) ||
                !double.TryParse(Read(values, "X"), NumberStyles.Float, CultureInfo.InvariantCulture, out x) ||
                !double.TryParse(Read(values, "Y"), NumberStyles.Float, CultureInfo.InvariantCulture, out y) ||
                !double.TryParse(Read(values, "Z"), NumberStyles.Float, CultureInfo.InvariantCulture, out z))
                return false;
            link = new LinkData(
                corridorId,
                sourceId,
                Read(values, "PointName"),
                new Point3d(x, y, z));
            return true;
        }

        private static List<ObjectId> EntityIds(Database database, Transaction transaction)
        {
            var result = new List<ObjectId>();
            BlockTable table = transaction.GetObject(
                database.BlockTableId,
                OpenMode.ForRead,
                false) as BlockTable;
            if (table == null) return result;
            foreach (ObjectId blockId in table)
            {
                BlockTableRecord block = transaction.GetObject(
                    blockId,
                    OpenMode.ForRead,
                    false) as BlockTableRecord;
                if (block == null || block.IsFromExternalReference) continue;
                foreach (ObjectId id in block) result.Add(id);
            }
            return result;
        }

        private static bool Resolve(Database database, string text, out ObjectId id)
        {
            id = ObjectId.Null;
            long handle;
            if (!long.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out handle))
                return false;
            try
            {
                id = database.GetObjectId(false, new Handle(handle), 0);
                return !id.IsNull && !id.IsErased;
            }
            catch { return false; }
        }

        private static string Read(IDictionary<string, string> values, string key)
        {
            string value;
            return values.TryGetValue(key, out value) ? value : string.Empty;
        }

        private sealed class LinkData
        {
            public LinkData(
                ObjectId corridorId,
                ObjectId sourcePointId,
                string pointName,
                Point3d lastPoint)
            {
                CorridorId = corridorId;
                SourcePointId = sourcePointId;
                PointName = pointName;
                LastPoint = lastPoint;
            }
            public ObjectId CorridorId { get; private set; }
            public ObjectId SourcePointId { get; private set; }
            public string PointName { get; private set; }
            public Point3d LastPoint { get; private set; }
        }

        private sealed class CorridorResult
        {
            public CorridorResult(
                string pointName,
                string corridorName,
                Point3d point,
                int baselines,
                int regions,
                int surfaces,
                bool outOfDate)
            {
                PointName = string.IsNullOrWhiteSpace(pointName) ? "P1" : pointName;
                CorridorName = corridorName ?? string.Empty;
                Point = point;
                Baselines = baselines;
                Regions = regions;
                Surfaces = surfaces;
                OutOfDate = outOfDate;
            }
            public string PointName { get; private set; }
            public string CorridorName { get; private set; }
            public Point3d Point { get; private set; }
            public int Baselines { get; private set; }
            public int Regions { get; private set; }
            public int Surfaces { get; private set; }
            public bool OutOfDate { get; private set; }
        }
    }
}

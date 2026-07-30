using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.DatabaseServices;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivilCogoPoint = Autodesk.Civil.DatabaseServices.CogoPoint;

namespace CETools.Civil3D
{
    /// <summary>
    /// Keeps alignment station/offset annotations and tables attached to a
    /// movable DBPoint/COGO source. The relationship is stored in the DWG.
    /// </summary>
    internal static class AlignmentAnnotationLinkStore
    {
        private const string RecordName = "CE_ALIGNMENT_ANNOTATION_LINK";
        private const string SchemaVersion = "1";
        private const double Tolerance = 0.0000001;

        public static void Link(
            Database database,
            ObjectId alignmentId,
            ObjectId sourcePointId,
            string pointName,
            Point3d sourcePoint,
            IEnumerable<ObjectId> outputIds)
        {
            if (database == null || alignmentId.IsNull || sourcePointId.IsNull) return;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                DBObject source = transaction.GetObject(
                    sourcePointId,
                    OpenMode.ForWrite,
                    false);
                WriteRecord(
                    source,
                    transaction,
                    alignmentId,
                    sourcePointId,
                    pointName,
                    sourcePoint);

                if (outputIds != null)
                {
                    foreach (ObjectId outputId in outputIds)
                    {
                        if (outputId.IsNull || outputId.IsErased || outputId == sourcePointId)
                            continue;
                        DBObject output = transaction.GetObject(
                            outputId,
                            OpenMode.ForWrite,
                            false);
                        WriteRecord(
                            output,
                            transaction,
                            alignmentId,
                            sourcePointId,
                            pointName,
                            sourcePoint);
                    }
                }
                transaction.Commit();
            }
        }

        public static ObjectId CreateLinkedTable(
            Database database,
            Point3d insertion,
            ObjectId alignmentId,
            ObjectId sourcePointId,
            string pointName,
            Point3d sourcePoint,
            double textHeight)
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
                WriteRecord(
                    table,
                    transaction,
                    alignmentId,
                    sourcePointId,
                    pointName,
                    sourcePoint);
                PopulateTable(
                    table,
                    Calculate(
                        transaction,
                        alignmentId,
                        sourcePointId,
                        pointName),
                    Math.Max(textHeight, 0.001));
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
                    try
                    {
                        value = transaction.GetObject(
                            entityId,
                            OpenMode.ForWrite,
                            false);
                    }
                    catch
                    {
                        continue;
                    }

                    LinkData link;
                    if (!TryReadRecord(database, value, transaction, out link))
                        continue;

                    AlignmentResult result;
                    try
                    {
                        result = Calculate(
                            transaction,
                            link.AlignmentId,
                            link.SourcePointId,
                            link.PointName);
                    }
                    catch
                    {
                        continue;
                    }

                    Entity entity = value as Entity;
                    bool isSource = entityId == link.SourcePointId;
                    bool moved = false;
                    if (!isSource && entity != null && !(entity is Table))
                    {
                        Vector3d displacement = result.Point - link.LastPoint;
                        if (displacement.Length > Tolerance)
                        {
                            try
                            {
                                entity.TransformBy(Matrix3d.Displacement(displacement));
                                moved = true;
                            }
                            catch
                            {
                                moved = false;
                            }
                        }
                    }

                    bool updated = Update(value, result);
                    WriteRecord(
                        value,
                        transaction,
                        link.AlignmentId,
                        link.SourcePointId,
                        link.PointName,
                        result.Point);
                    if (moved || updated) changed++;
                }
                transaction.Commit();
            }
            return changed;
        }

        private static AlignmentResult Calculate(
            Transaction transaction,
            ObjectId alignmentId,
            ObjectId sourcePointId,
            string pointName)
        {
            CivilAlignment alignment = transaction.GetObject(
                alignmentId,
                OpenMode.ForRead,
                false) as CivilAlignment;
            if (alignment == null)
                throw new InvalidOperationException("The linked alignment is unavailable.");
            Point3d point = ReadPoint(transaction, sourcePointId);
            double station = 0.0;
            double offset = 0.0;
            alignment.StationOffset(point.X, point.Y, ref station, ref offset);
            string stationText;
            try
            {
                stationText = alignment.GetStationStringWithEquations(station);
            }
            catch
            {
                stationText = station.ToString("N3", CultureInfo.CurrentCulture);
            }
            string side = offset > Tolerance
                ? "Right"
                : offset < -Tolerance ? "Left" : "On alignment";
            return new AlignmentResult(
                pointName,
                alignment.Name,
                point,
                station,
                stationText,
                offset,
                side);
        }

        private static Point3d ReadPoint(Transaction transaction, ObjectId id)
        {
            DBObject value = transaction.GetObject(id, OpenMode.ForRead, false);
            DBPoint point = value as DBPoint;
            if (point != null) return point.Position;
            CivilCogoPoint cogo = value as CivilCogoPoint;
            if (cogo != null)
                return new Point3d(cogo.Easting, cogo.Northing, cogo.Elevation);
            throw new InvalidOperationException("The linked alignment point is unavailable.");
        }

        private static bool Update(DBObject value, AlignmentResult result)
        {
            string contents = BuildContents(result);
            MText text = value as MText;
            if (text != null)
            {
                if (string.Equals(text.Contents, contents, StringComparison.Ordinal))
                    return false;
                text.Contents = contents;
                return true;
            }
            MLeader leader = value as MLeader;
            if (leader != null && leader.ContentType == ContentType.MTextContent)
            {
                MText leaderText = leader.MText;
                if (leaderText == null) return false;
                if (string.Equals(leaderText.Contents, contents, StringComparison.Ordinal))
                    return false;
                leaderText.Contents = contents;
                leader.MText = leaderText;
                return true;
            }
            CivilCogoPoint cogo = value as CivilCogoPoint;
            if (cogo != null)
            {
                string description = BuildPlain(result);
                if (string.Equals(cogo.RawDescription, description, StringComparison.Ordinal))
                    return false;
                cogo.RawDescription = description;
                return true;
            }
            Table table = value as Table;
            if (table != null)
            {
                double height = table.Rows.Count > 0 && table.Columns.Count > 0
                    ? Math.Max(table.Cells[0, 0].TextHeight ?? 2.5, 0.001)
                    : 2.5;
                PopulateTable(table, result, height);
                return true;
            }
            return false;
        }

        private static string BuildContents(AlignmentResult result)
        {
            return string.Join(
                "\\P",
                "Point Name: " + result.PointName,
                "Alignment: " + result.AlignmentName,
                "Station: " + result.StationText,
                "Offset: " + Math.Abs(result.Offset).ToString("N3", CultureInfo.CurrentCulture) +
                    " " + result.Side,
                "X: " + result.Point.X.ToString("N3", CultureInfo.CurrentCulture),
                "Y: " + result.Point.Y.ToString("N3", CultureInfo.CurrentCulture),
                "Z: " + result.Point.Z.ToString("N3", CultureInfo.CurrentCulture));
        }

        private static string BuildPlain(AlignmentResult result)
        {
            return string.Format(
                CultureInfo.CurrentCulture,
                "{0}; {1}; STA {2}; OFF {3:N3} {4}; X {5:N3}; Y {6:N3}; Z {7:N3}",
                result.PointName,
                result.AlignmentName,
                result.StationText,
                Math.Abs(result.Offset),
                result.Side,
                result.Point.X,
                result.Point.Y,
                result.Point.Z);
        }

        private static void PopulateTable(
            Table table,
            AlignmentResult result,
            double height)
        {
            string[] headings =
            {
                "Point Name", "Alignment", "Station", "Offset", "Side", "X", "Y", "Z"
            };
            string[] values =
            {
                result.PointName,
                result.AlignmentName,
                result.StationText,
                Math.Abs(result.Offset).ToString("N3", CultureInfo.CurrentCulture),
                result.Side,
                result.Point.X.ToString("N3", CultureInfo.CurrentCulture),
                result.Point.Y.ToString("N3", CultureInfo.CurrentCulture),
                result.Point.Z.ToString("N3", CultureInfo.CurrentCulture)
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
            table.Cells[0, 0].TextString = "DYNAMIC ALIGNMENT POINT";
            table.Cells[0, 0].TextHeight = height * 1.15;
            table.Cells[0, 0].Alignment = CellAlignment.MiddleCenter;
            table.GenerateLayout();
        }

        private static void WriteRecord(
            DBObject target,
            Transaction transaction,
            ObjectId alignmentId,
            ObjectId sourcePointId,
            string pointName,
            Point3d lastPoint)
        {
            if (target.ExtensionDictionary.IsNull) target.CreateExtensionDictionary();
            DBDictionary dictionary = transaction.GetObject(
                target.ExtensionDictionary,
                OpenMode.ForWrite,
                false) as DBDictionary;
            if (dictionary == null) return;
            Xrecord record;
            if (dictionary.Contains(RecordName))
            {
                record = transaction.GetObject(
                    dictionary.GetAt(RecordName),
                    OpenMode.ForWrite,
                    false) as Xrecord;
            }
            else
            {
                record = new Xrecord();
                dictionary.SetAt(RecordName, record);
                transaction.AddNewlyCreatedDBObject(record, true);
            }
            if (record == null) return;
            record.Data = new ResultBuffer(
                new TypedValue((int)DxfCode.Text, "Schema=" + SchemaVersion),
                new TypedValue((int)DxfCode.Text, "Alignment=" + alignmentId.Handle),
                new TypedValue((int)DxfCode.Text, "Source=" + sourcePointId.Handle),
                new TypedValue((int)DxfCode.Text, "PointName=" + (pointName ?? string.Empty)),
                new TypedValue((int)DxfCode.Text, "X=" + lastPoint.X.ToString("R", CultureInfo.InvariantCulture)),
                new TypedValue((int)DxfCode.Text, "Y=" + lastPoint.Y.ToString("R", CultureInfo.InvariantCulture)),
                new TypedValue((int)DxfCode.Text, "Z=" + lastPoint.Z.ToString("R", CultureInfo.InvariantCulture)));
        }

        private static bool TryReadRecord(
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
            ObjectId alignmentId;
            ObjectId sourceId;
            double x;
            double y;
            double z;
            if (!Resolve(database, Read(values, "Alignment"), out alignmentId) ||
                !Resolve(database, Read(values, "Source"), out sourceId) ||
                !double.TryParse(Read(values, "X"), NumberStyles.Float, CultureInfo.InvariantCulture, out x) ||
                !double.TryParse(Read(values, "Y"), NumberStyles.Float, CultureInfo.InvariantCulture, out y) ||
                !double.TryParse(Read(values, "Z"), NumberStyles.Float, CultureInfo.InvariantCulture, out z))
                return false;
            link = new LinkData(
                alignmentId,
                sourceId,
                Read(values, "PointName"),
                new Point3d(x, y, z));
            return true;
        }

        private static List<ObjectId> ReadEntityIds(
            Database database,
            Transaction transaction)
        {
            var result = new List<ObjectId>();
            BlockTable blockTable = transaction.GetObject(
                database.BlockTableId,
                OpenMode.ForRead,
                false) as BlockTable;
            if (blockTable == null) return result;
            foreach (ObjectId blockId in blockTable)
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
            catch
            {
                return false;
            }
        }

        private static string Read(IDictionary<string, string> values, string key)
        {
            string value;
            return values.TryGetValue(key, out value) ? value : string.Empty;
        }

        private sealed class LinkData
        {
            public LinkData(
                ObjectId alignmentId,
                ObjectId sourcePointId,
                string pointName,
                Point3d lastPoint)
            {
                AlignmentId = alignmentId;
                SourcePointId = sourcePointId;
                PointName = pointName;
                LastPoint = lastPoint;
            }
            public ObjectId AlignmentId { get; private set; }
            public ObjectId SourcePointId { get; private set; }
            public string PointName { get; private set; }
            public Point3d LastPoint { get; private set; }
        }

        private sealed class AlignmentResult
        {
            public AlignmentResult(
                string pointName,
                string alignmentName,
                Point3d point,
                double station,
                string stationText,
                double offset,
                string side)
            {
                PointName = string.IsNullOrWhiteSpace(pointName) ? "P1" : pointName;
                AlignmentName = alignmentName ?? string.Empty;
                Point = point;
                Station = station;
                StationText = stationText ?? string.Empty;
                Offset = offset;
                Side = side ?? string.Empty;
            }
            public string PointName { get; private set; }
            public string AlignmentName { get; private set; }
            public Point3d Point { get; private set; }
            public double Station { get; private set; }
            public string StationText { get; private set; }
            public double Offset { get; private set; }
            public string Side { get; private set; }
        }
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.DatabaseServices;
using CivilCogoPoint = Autodesk.Civil.DatabaseServices.CogoPoint;

namespace CETools.Civil3D
{
    /// <summary>
    /// Stores lightweight source-to-output coordinate relationships on generated
    /// entities. Links are intentionally stored inside the DWG so coordinates,
    /// point names, markers and polyline-vertex COGO points can be refreshed after
    /// their source point or polyline changes.
    /// </summary>
    internal static class DynamicCoordinateLinkStore
    {
        private const string FollowerRecordName = "CE_DYNAMIC_COORDINATE_FOLLOWER";
        private const string PolylineVertexRecordName = "CE_DYNAMIC_POLYLINE_VERTEX";
        private const string SchemaVersion = "1";
        private const double Tolerance = 0.0000001;

        public static void LinkGeneratedObjects(
            Database database,
            ObjectId sourceId,
            IEnumerable<ObjectId> generatedIds)
        {
            if (database == null || sourceId.IsNull || generatedIds == null) return;

            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                Point3d sourcePoint;
                string pointName;
                if (!TryReadCoordinate(
                    transaction,
                    sourceId,
                    out sourcePoint,
                    out pointName))
                {
                    return;
                }

                foreach (ObjectId generatedId in generatedIds)
                {
                    if (generatedId.IsNull || generatedId == sourceId || generatedId.IsErased)
                        continue;

                    Entity entity;
                    try
                    {
                        entity = transaction.GetObject(
                            generatedId,
                            OpenMode.ForWrite,
                            false) as Entity;
                    }
                    catch
                    {
                        continue;
                    }
                    if (entity == null || entity is Table) continue;

                    WriteFollowerRecord(
                        entity,
                        transaction,
                        sourceId,
                        sourcePoint,
                        pointName);
                }

                transaction.Commit();
            }
        }

        public static void LinkPolylineVertices(
            Database database,
            ObjectId polylineId,
            IList<ObjectId> pointIds)
        {
            if (database == null || polylineId.IsNull || pointIds == null) return;

            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                for (int index = 0; index < pointIds.Count; index++)
                {
                    ObjectId pointId = pointIds[index];
                    if (pointId.IsNull || pointId.IsErased) continue;
                    DBObject value;
                    try
                    {
                        value = transaction.GetObject(
                            pointId,
                            OpenMode.ForWrite,
                            false);
                    }
                    catch
                    {
                        continue;
                    }
                    WritePolylineVertexRecord(
                        value,
                        transaction,
                        polylineId,
                        index);
                }
                transaction.Commit();
            }
        }

        public static int Refresh(Document document)
        {
            if (document == null) return 0;
            Database database = document.Database;
            int changed = 0;

            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                List<ObjectId> entities = ReadAllEntityIds(database, transaction);

                // Move linked COGO/DBPoint objects to their current source-polyline
                // vertices before refreshing annotations and tables that use them.
                foreach (ObjectId entityId in entities)
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

                    PolylineVertexLink vertexLink;
                    if (!TryReadPolylineVertexRecord(
                        database,
                        value,
                        transaction,
                        out vertexLink))
                    {
                        continue;
                    }

                    Entity source = OpenEntity(
                        transaction,
                        vertexLink.SourceId,
                        OpenMode.ForRead);
                    if (source == null) continue;
                    List<Point3d> vertices = ReadPolylineVertices(source, transaction);
                    if (vertexLink.VertexIndex < 0 ||
                        vertexLink.VertexIndex >= vertices.Count)
                    {
                        continue;
                    }

                    Point3d current;
                    string name;
                    if (!TryReadCoordinate(
                        transaction,
                        entityId,
                        out current,
                        out name))
                    {
                        continue;
                    }

                    Point3d target = vertices[vertexLink.VertexIndex];
                    if (current.DistanceTo(target) <= Tolerance) continue;
                    if (TrySetCoordinate(value, target)) changed++;
                }

                // Refresh generated labels, leaders, markers and crosses from their
                // linked source point after all source followers have moved.
                foreach (ObjectId entityId in entities)
                {
                    Entity entity;
                    try
                    {
                        entity = transaction.GetObject(
                            entityId,
                            OpenMode.ForWrite,
                            false) as Entity;
                    }
                    catch
                    {
                        continue;
                    }
                    if (entity == null) continue;

                    CoordinateFollowerLink link;
                    if (!TryReadFollowerRecord(
                        database,
                        entity,
                        transaction,
                        out link))
                    {
                        continue;
                    }

                    Point3d sourcePoint;
                    string pointName;
                    if (!TryReadCoordinate(
                        transaction,
                        link.SourceId,
                        out sourcePoint,
                        out pointName))
                    {
                        continue;
                    }

                    Vector3d displacement = sourcePoint - link.LastSourcePoint;
                    bool moved = displacement.Length > Tolerance;
                    if (moved)
                    {
                        try
                        {
                            entity.TransformBy(Matrix3d.Displacement(displacement));
                        }
                        catch
                        {
                            moved = false;
                        }
                    }

                    bool textUpdated = UpdateCoordinateText(
                        entity,
                        pointName,
                        sourcePoint);
                    if (moved || textUpdated)
                    {
                        WriteFollowerRecord(
                            entity,
                            transaction,
                            link.SourceId,
                            sourcePoint,
                            pointName);
                        changed++;
                    }
                }

                transaction.Commit();
            }

            return changed;
        }

        public static int CountLinks(Database database)
        {
            if (database == null) return 0;
            int count = 0;
            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId entityId in ReadAllEntityIds(database, transaction))
                {
                    DBObject value;
                    try
                    {
                        value = transaction.GetObject(
                            entityId,
                            OpenMode.ForRead,
                            false);
                    }
                    catch
                    {
                        continue;
                    }
                    if (HasRecord(value, transaction, FollowerRecordName) ||
                        HasRecord(value, transaction, PolylineVertexRecordName))
                    {
                        count++;
                    }
                }
            }
            return count;
        }

        private static void WriteFollowerRecord(
            Entity entity,
            Transaction transaction,
            ObjectId sourceId,
            Point3d sourcePoint,
            string pointName)
        {
            Xrecord record = GetOrCreateRecord(
                entity,
                transaction,
                FollowerRecordName);
            if (record == null) return;
            record.Data = new ResultBuffer(
                new TypedValue((int)DxfCode.Text, "Schema=" + SchemaVersion),
                new TypedValue((int)DxfCode.Text, "Source=" + sourceId.Handle),
                new TypedValue(
                    (int)DxfCode.Text,
                    "X=" + sourcePoint.X.ToString("R", CultureInfo.InvariantCulture)),
                new TypedValue(
                    (int)DxfCode.Text,
                    "Y=" + sourcePoint.Y.ToString("R", CultureInfo.InvariantCulture)),
                new TypedValue(
                    (int)DxfCode.Text,
                    "Z=" + sourcePoint.Z.ToString("R", CultureInfo.InvariantCulture)),
                new TypedValue((int)DxfCode.Text, "PointName=" + (pointName ?? string.Empty)));
        }

        private static void WritePolylineVertexRecord(
            DBObject target,
            Transaction transaction,
            ObjectId polylineId,
            int vertexIndex)
        {
            Xrecord record = GetOrCreateRecord(
                target,
                transaction,
                PolylineVertexRecordName);
            if (record == null) return;
            record.Data = new ResultBuffer(
                new TypedValue((int)DxfCode.Text, "Schema=" + SchemaVersion),
                new TypedValue((int)DxfCode.Text, "Source=" + polylineId.Handle),
                new TypedValue(
                    (int)DxfCode.Text,
                    "VertexIndex=" + vertexIndex.ToString(CultureInfo.InvariantCulture)));
        }

        private static bool TryReadFollowerRecord(
            Database database,
            DBObject target,
            Transaction transaction,
            out CoordinateFollowerLink link)
        {
            link = null;
            Dictionary<string, string> values;
            if (!TryReadRecord(
                target,
                transaction,
                FollowerRecordName,
                out values))
            {
                return false;
            }

            ObjectId sourceId;
            double x;
            double y;
            double z;
            if (!TryResolveHandle(database, Read(values, "Source"), out sourceId) ||
                !TryParseDouble(Read(values, "X"), out x) ||
                !TryParseDouble(Read(values, "Y"), out y) ||
                !TryParseDouble(Read(values, "Z"), out z))
            {
                return false;
            }

            link = new CoordinateFollowerLink(
                sourceId,
                new Point3d(x, y, z));
            return true;
        }

        private static bool TryReadPolylineVertexRecord(
            Database database,
            DBObject target,
            Transaction transaction,
            out PolylineVertexLink link)
        {
            link = null;
            Dictionary<string, string> values;
            if (!TryReadRecord(
                target,
                transaction,
                PolylineVertexRecordName,
                out values))
            {
                return false;
            }

            ObjectId sourceId;
            int index;
            if (!TryResolveHandle(database, Read(values, "Source"), out sourceId) ||
                !int.TryParse(
                    Read(values, "VertexIndex"),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out index))
            {
                return false;
            }

            link = new PolylineVertexLink(sourceId, index);
            return true;
        }

        private static bool TryReadRecord(
            DBObject target,
            Transaction transaction,
            string recordName,
            out Dictionary<string, string> values)
        {
            values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (target == null || target.ExtensionDictionary.IsNull) return false;
            DBDictionary dictionary;
            try
            {
                dictionary = transaction.GetObject(
                    target.ExtensionDictionary,
                    OpenMode.ForRead,
                    false) as DBDictionary;
            }
            catch
            {
                return false;
            }
            if (dictionary == null || !dictionary.Contains(recordName)) return false;
            Xrecord record = transaction.GetObject(
                dictionary.GetAt(recordName),
                OpenMode.ForRead,
                false) as Xrecord;
            if (record == null || record.Data == null) return false;

            foreach (TypedValue typedValue in record.Data)
            {
                string text = typedValue.Value as string;
                if (string.IsNullOrWhiteSpace(text)) continue;
                int equals = text.IndexOf('=');
                if (equals <= 0) continue;
                values[text.Substring(0, equals)] = text.Substring(equals + 1);
            }
            return values.Count > 0;
        }

        private static Xrecord GetOrCreateRecord(
            DBObject target,
            Transaction transaction,
            string recordName)
        {
            if (target == null) return null;
            if (target.ExtensionDictionary.IsNull)
                target.CreateExtensionDictionary();
            DBDictionary dictionary = transaction.GetObject(
                target.ExtensionDictionary,
                OpenMode.ForWrite,
                false) as DBDictionary;
            if (dictionary == null) return null;
            if (dictionary.Contains(recordName))
            {
                return transaction.GetObject(
                    dictionary.GetAt(recordName),
                    OpenMode.ForWrite,
                    false) as Xrecord;
            }

            var record = new Xrecord();
            dictionary.SetAt(recordName, record);
            transaction.AddNewlyCreatedDBObject(record, true);
            return record;
        }

        private static bool HasRecord(
            DBObject target,
            Transaction transaction,
            string recordName)
        {
            if (target == null || target.ExtensionDictionary.IsNull) return false;
            try
            {
                DBDictionary dictionary = transaction.GetObject(
                    target.ExtensionDictionary,
                    OpenMode.ForRead,
                    false) as DBDictionary;
                return dictionary != null && dictionary.Contains(recordName);
            }
            catch
            {
                return false;
            }
        }

        private static List<ObjectId> ReadAllEntityIds(
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
                foreach (ObjectId entityId in block)
                    result.Add(entityId);
            }
            return result;
        }

        private static bool TryReadCoordinate(
            Transaction transaction,
            ObjectId objectId,
            out Point3d point,
            out string pointName)
        {
            point = Point3d.Origin;
            pointName = string.Empty;
            if (objectId.IsNull || objectId.IsErased) return false;
            DBObject value;
            try
            {
                value = transaction.GetObject(
                    objectId,
                    OpenMode.ForRead,
                    false);
            }
            catch
            {
                return false;
            }

            var databasePoint = value as DBPoint;
            if (databasePoint != null)
            {
                point = databasePoint.Position;
                pointName = "P-" + objectId.Handle;
                return true;
            }

            var cogo = value as CivilCogoPoint;
            if (cogo != null)
            {
                point = new Point3d(cogo.Easting, cogo.Northing, cogo.Elevation);
                pointName = string.IsNullOrWhiteSpace(cogo.PointName)
                    ? (string.IsNullOrWhiteSpace(cogo.RawDescription)
                        ? "P" + cogo.PointNumber.ToString(CultureInfo.InvariantCulture)
                        : cogo.RawDescription)
                    : cogo.PointName;
                return true;
            }

            Point3d reflected;
            if (TryReadPointProperty(value, "Location", out reflected) ||
                TryReadPointProperty(value, "Position", out reflected))
            {
                point = reflected;
                pointName = Convert.ToString(
                    ReadProperty(value, "Name"),
                    CultureInfo.CurrentCulture);
                return true;
            }

            double easting;
            double northing;
            double elevation;
            if (TryReadDoubleProperty(value, "Easting", out easting) &&
                TryReadDoubleProperty(value, "Northing", out northing) &&
                TryReadDoubleProperty(value, "Elevation", out elevation))
            {
                point = new Point3d(easting, northing, elevation);
                pointName = Convert.ToString(
                    ReadProperty(value, "PointName"),
                    CultureInfo.CurrentCulture);
                return true;
            }

            return false;
        }

        private static bool TrySetCoordinate(DBObject value, Point3d point)
        {
            var databasePoint = value as DBPoint;
            if (databasePoint != null)
            {
                databasePoint.Position = point;
                return true;
            }

            try
            {
                PropertyInfo location = value.GetType().GetProperty(
                    "Location",
                    BindingFlags.Public | BindingFlags.Instance);
                if (location != null && location.CanWrite &&
                    location.PropertyType == typeof(Point3d))
                {
                    location.SetValue(value, point, null);
                    return true;
                }
            }
            catch
            {
                // Fall through to scalar coordinate setters.
            }

            bool x = TryWriteDoubleProperty(value, "Easting", point.X);
            bool y = TryWriteDoubleProperty(value, "Northing", point.Y);
            bool z = TryWriteDoubleProperty(value, "Elevation", point.Z);
            return x && y && z;
        }

        private static bool UpdateCoordinateText(
            Entity entity,
            string pointName,
            Point3d point)
        {
            string contents = BuildMTextCoordinate(pointName, point);
            var mtext = entity as MText;
            if (mtext != null)
            {
                if (string.Equals(mtext.Contents, contents, StringComparison.Ordinal))
                    return false;
                mtext.Contents = contents;
                return true;
            }

            var databaseText = entity as DBText;
            if (databaseText != null)
            {
                string plain = BuildPlainCoordinate(pointName, point);
                if (string.Equals(databaseText.TextString, plain, StringComparison.Ordinal))
                    return false;
                databaseText.TextString = plain;
                return true;
            }

            var leader = entity as MLeader;
            if (leader != null && leader.ContentType == ContentType.MTextContent)
            {
                MText leaderText = leader.MText;
                if (leaderText == null ||
                    string.Equals(leaderText.Contents, contents, StringComparison.Ordinal))
                {
                    return false;
                }
                leaderText.Contents = contents;
                leader.MText = leaderText;
                return true;
            }

            return false;
        }

        private static string BuildMTextCoordinate(string pointName, Point3d point)
        {
            return string.Join(
                "\\P",
                "POINT NAME: " + SafePointName(pointName),
                "X-COORDINATE: " + point.X.ToString("N3", CultureInfo.CurrentCulture),
                "Y-COORDINATE: " + point.Y.ToString("N3", CultureInfo.CurrentCulture),
                "Z-COORDINATE: " + point.Z.ToString("N3", CultureInfo.CurrentCulture));
        }

        private static string BuildPlainCoordinate(string pointName, Point3d point)
        {
            return string.Format(
                CultureInfo.CurrentCulture,
                "POINT NAME: {0}; X-COORDINATE: {1:N3}; Y-COORDINATE: {2:N3}; Z-COORDINATE: {3:N3}",
                SafePointName(pointName),
                point.X,
                point.Y,
                point.Z);
        }

        private static string SafePointName(string pointName)
        {
            return string.IsNullOrWhiteSpace(pointName) ? "<UNNAMED>" : pointName.Trim();
        }

        private static List<Point3d> ReadPolylineVertices(
            Entity source,
            Transaction transaction)
        {
            var result = new List<Point3d>();
            var polyline = source as Polyline;
            if (polyline != null)
            {
                for (int index = 0; index < polyline.NumberOfVertices; index++)
                    result.Add(polyline.GetPoint3dAt(index));
                return result;
            }

            var polyline2d = source as Polyline2d;
            if (polyline2d != null)
            {
                foreach (ObjectId vertexId in polyline2d)
                {
                    Vertex2d vertex = transaction.GetObject(
                        vertexId,
                        OpenMode.ForRead,
                        false) as Vertex2d;
                    if (vertex != null) result.Add(vertex.Position);
                }
                return result;
            }

            var polyline3d = source as Polyline3d;
            if (polyline3d != null)
            {
                foreach (ObjectId vertexId in polyline3d)
                {
                    PolylineVertex3d vertex = transaction.GetObject(
                        vertexId,
                        OpenMode.ForRead,
                        false) as PolylineVertex3d;
                    if (vertex != null) result.Add(vertex.Position);
                }
            }
            return result;
        }

        private static Entity OpenEntity(
            Transaction transaction,
            ObjectId objectId,
            OpenMode openMode)
        {
            if (objectId.IsNull || objectId.IsErased) return null;
            try
            {
                return transaction.GetObject(
                    objectId,
                    openMode,
                    false) as Entity;
            }
            catch
            {
                return null;
            }
        }

        private static bool TryResolveHandle(
            Database database,
            string handleText,
            out ObjectId objectId)
        {
            objectId = ObjectId.Null;
            long value;
            if (!long.TryParse(
                handleText,
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out value))
            {
                return false;
            }
            try
            {
                objectId = database.GetObjectId(false, new Handle(value), 0);
                return !objectId.IsNull && !objectId.IsErased;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryParseDouble(string text, out double value)
        {
            return double.TryParse(
                text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out value);
        }

        private static string Read(
            IDictionary<string, string> values,
            string key)
        {
            string result;
            return values.TryGetValue(key, out result) ? result : string.Empty;
        }

        private static object ReadProperty(object value, string propertyName)
        {
            if (value == null) return null;
            try
            {
                PropertyInfo property = value.GetType().GetProperty(
                    propertyName,
                    BindingFlags.Public | BindingFlags.Instance);
                return property == null || property.GetIndexParameters().Length != 0
                    ? null
                    : property.GetValue(value, null);
            }
            catch
            {
                return null;
            }
        }

        private static bool TryReadPointProperty(
            object value,
            string propertyName,
            out Point3d point)
        {
            point = Point3d.Origin;
            object raw = ReadProperty(value, propertyName);
            if (!(raw is Point3d)) return false;
            point = (Point3d)raw;
            return true;
        }

        private static bool TryReadDoubleProperty(
            object value,
            string propertyName,
            out double number)
        {
            number = 0.0;
            object raw = ReadProperty(value, propertyName);
            if (raw == null) return false;
            try
            {
                number = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
                return !double.IsNaN(number) && !double.IsInfinity(number);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryWriteDoubleProperty(
            object value,
            string propertyName,
            double number)
        {
            if (value == null) return false;
            try
            {
                PropertyInfo property = value.GetType().GetProperty(
                    propertyName,
                    BindingFlags.Public | BindingFlags.Instance);
                if (property == null || !property.CanWrite) return false;
                object converted = Convert.ChangeType(
                    number,
                    property.PropertyType,
                    CultureInfo.InvariantCulture);
                property.SetValue(value, converted, null);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private sealed class CoordinateFollowerLink
        {
            public CoordinateFollowerLink(
                ObjectId sourceId,
                Point3d lastSourcePoint)
            {
                SourceId = sourceId;
                LastSourcePoint = lastSourcePoint;
            }

            public ObjectId SourceId { get; private set; }
            public Point3d LastSourcePoint { get; private set; }
        }

        private sealed class PolylineVertexLink
        {
            public PolylineVertexLink(ObjectId sourceId, int vertexIndex)
            {
                SourceId = sourceId;
                VertexIndex = vertexIndex;
            }

            public ObjectId SourceId { get; private set; }
            public int VertexIndex { get; private set; }
        }
    }
}

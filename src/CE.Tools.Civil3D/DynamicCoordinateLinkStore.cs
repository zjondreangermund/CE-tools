using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using CivilCogoPoint = Autodesk.Civil.DatabaseServices.CogoPoint;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

namespace CETools.Civil3D
{
    /// <summary>
    /// Compatibility implementation restored from the V60 coordinate-link workflow.
    /// Stores durable source links on generated entities so later refresh managers can
    /// locate their source points, polylines, feature lines and surfaces.
    /// </summary>
    internal static class DynamicCoordinateLinkStore
    {
        private const string FollowerRecord = "CE_DYNAMIC_COORDINATE_FOLLOWER";
        private const string VertexRecord = "CE_DYNAMIC_POLYLINE_VERTEX";
        private const string SurfaceRecord = "CE_DYNAMIC_SURFACE_ELEVATION";
        private const string PointNameRecord = "CE_DYNAMIC_POINT_NAME";

        public static void LinkGeneratedObjects(Database database, ObjectId sourceId, IEnumerable<ObjectId> generatedIds)
        {
            if (database == null || sourceId.IsNull || generatedIds == null) return;
            using (Transaction tr = database.TransactionManager.StartTransaction())
            {
                Point3d sourcePoint;
                bool hasSourcePoint = TryReadPoint(
                    tr.GetObject(sourceId, OpenMode.ForRead, false),
                    out sourcePoint);
                foreach (ObjectId id in generatedIds)
                {
                    if (id.IsNull || id.IsErased || id == sourceId) continue;
                    DBObject target;
                    try { target = tr.GetObject(id, OpenMode.ForWrite, false); }
                    catch { continue; }
                    Write(target, tr, FollowerRecord,
                        new TypedValue((int)DxfCode.Text, "Schema=2"),
                        new TypedValue((int)DxfCode.Text, "Source=" + sourceId.Handle),
                        new TypedValue((int)DxfCode.Text, "LastX=" + Coordinate(sourcePoint.X, hasSourcePoint)),
                        new TypedValue((int)DxfCode.Text, "LastY=" + Coordinate(sourcePoint.Y, hasSourcePoint)),
                        new TypedValue((int)DxfCode.Text, "LastZ=" + Coordinate(sourcePoint.Z, hasSourcePoint)));
                }
                tr.Commit();
            }
        }

        public static void LinkPolylineVertices(Database database, ObjectId sourceId, IList<ObjectId> pointIds)
        {
            if (database == null || sourceId.IsNull || pointIds == null) return;
            using (Transaction tr = database.TransactionManager.StartTransaction())
            {
                for (int index = 0; index < pointIds.Count; index++)
                {
                    ObjectId id = pointIds[index];
                    if (id.IsNull || id.IsErased) continue;
                    DBObject target;
                    try { target = tr.GetObject(id, OpenMode.ForWrite, false); }
                    catch { continue; }
                    Write(target, tr, VertexRecord,
                        new TypedValue((int)DxfCode.Text, "Schema=1"),
                        new TypedValue((int)DxfCode.Text, "Source=" + sourceId.Handle),
                        new TypedValue((int)DxfCode.Text, "Vertex=" + index));
                }
                tr.Commit();
            }
        }

        public static void LinkFeatureLineVertices(Database database, ObjectId featureLineId, IList<ObjectId> pointIds)
        {
            LinkPolylineVertices(database, featureLineId, pointIds);
        }

        public static void LinkFeatureLineVertex(Database database, ObjectId featureLineId, ObjectId pointId, int vertexIndex)
        {
            if (database == null || featureLineId.IsNull || pointId.IsNull || pointId.IsErased || vertexIndex < 0) return;
            using (Transaction tr = database.TransactionManager.StartTransaction())
            {
                DBObject target = tr.GetObject(pointId, OpenMode.ForWrite, false);
                Write(target, tr, VertexRecord,
                    new TypedValue((int)DxfCode.Text, "Schema=1"),
                    new TypedValue((int)DxfCode.Text, "Source=" + featureLineId.Handle),
                    new TypedValue((int)DxfCode.Text, "Vertex=" + vertexIndex));
                tr.Commit();
            }
        }

        public static void LinkSurfaceElevation(Database database, ObjectId surfaceId, IEnumerable<ObjectId> pointIds)
        {
            if (database == null || surfaceId.IsNull || pointIds == null) return;
            using (Transaction tr = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in pointIds)
                {
                    if (id.IsNull || id.IsErased) continue;
                    DBObject target;
                    try { target = tr.GetObject(id, OpenMode.ForWrite, false); }
                    catch { continue; }
                    Write(target, tr, SurfaceRecord,
                        new TypedValue((int)DxfCode.Text, "Schema=1"),
                        new TypedValue((int)DxfCode.Text, "Surface=" + surfaceId.Handle));
                }
                tr.Commit();
            }
        }

        public static void SetPointName(Database database, ObjectId pointId, string pointName)
        {
            if (database == null || pointId.IsNull || pointId.IsErased) return;
            string value = (pointName ?? string.Empty).Trim();
            if (value.Length > 0)
            {
                try
                {
                    CivilDocument civilDocument = CivilApplication.ActiveDocument;
                    if (civilDocument != null)
                    {
                        civilDocument.CogoPoints.SetRawDescription(
                            new List<ObjectId> { pointId },
                            new List<string> { value });
                    }
                }
                catch
                {
                    // Non-COGO anchors do not participate in the Civil collection.
                }
            }
            using (Transaction tr = database.TransactionManager.StartTransaction())
            {
                DBObject target = tr.GetObject(pointId, OpenMode.ForWrite, false);
                CivilCogoPoint cogo = target as CivilCogoPoint;
                if (cogo != null && value.Length > 0)
                {
                    try { cogo.PointName = value; }
                    catch { }
                    try { cogo.RawDescription = value; }
                    catch { }
                }
                Write(target, tr, PointNameRecord,
                    new TypedValue((int)DxfCode.Text, "Schema=1"),
                    new TypedValue((int)DxfCode.Text, "PointName=" + value));
                tr.Commit();
            }
        }

        public static string ReadPointName(DBObject value, Transaction transaction, string fallback)
        {
            string stored;
            return TryRead(value, transaction, PointNameRecord, "PointName", out stored) && !string.IsNullOrWhiteSpace(stored)
                ? stored.Trim()
                : fallback;
        }

        public static int Refresh(Document document)
        {
            if (document == null || document.Database == null) return 0;
            Database database = document.Database;
            int changed = 0;
            var pointNameRepairs = new List<KeyValuePair<ObjectId, string>>();
            using (Transaction tr = database.TransactionManager.StartTransaction())
            {
                List<ObjectId> ids = ReadCurrentSpaceIds(database, tr);

                // Update linked source points first. Polyline/feature-line vertices
                // control XY/Z; an optional surface link then controls elevation.
                foreach (ObjectId id in ids)
                {
                    DBObject target;
                    try { target = tr.GetObject(id, OpenMode.ForRead, false); }
                    catch { continue; }

                    CivilCogoPoint namedCogo = target as CivilCogoPoint;
                    if (namedCogo != null)
                    {
                        string storedName = ReadPointName(namedCogo, tr, string.Empty);
                        if (!string.IsNullOrWhiteSpace(storedName) &&
                            !string.Equals(
                                namedCogo.RawDescription,
                                storedName,
                                StringComparison.Ordinal))
                        {
                            pointNameRepairs.Add(
                                new KeyValuePair<ObjectId, string>(id, storedName));
                        }
                    }

                    Point3d current;
                    if (!TryReadPoint(target, out current)) continue;
                    Point3d updated = current;
                    bool linked = false;

                    Dictionary<string, string> vertex;
                    if (TryReadRecord(target, tr, VertexRecord, out vertex))
                    {
                        ObjectId sourceId;
                        int vertexIndex;
                        Point3d vertexPoint;
                        if (TryResolve(database, Text(vertex, "Source"), out sourceId) &&
                            int.TryParse(Text(vertex, "Vertex"), NumberStyles.Integer, CultureInfo.InvariantCulture, out vertexIndex) &&
                            TryReadVertex(tr, sourceId, vertexIndex, out vertexPoint))
                        {
                            updated = vertexPoint;
                            linked = true;
                        }
                    }

                    Dictionary<string, string> surfaceValues;
                    if (TryReadRecord(target, tr, SurfaceRecord, out surfaceValues))
                    {
                        ObjectId surfaceId;
                        if (TryResolve(database, Text(surfaceValues, "Surface"), out surfaceId))
                        {
                            CivilSurface surface = tr.GetObject(
                                surfaceId,
                                OpenMode.ForRead,
                                false) as CivilSurface;
                            if (surface != null)
                            {
                                try
                                {
                                    updated = new Point3d(
                                        updated.X,
                                        updated.Y,
                                        surface.FindElevationAtXY(updated.X, updated.Y));
                                    linked = true;
                                }
                                catch
                                {
                                    // A point outside the surface remains at its last valid elevation.
                                }
                            }
                        }
                    }

                    if (!linked || current.DistanceTo(updated) <= 0.0000001) continue;
                    target.UpgradeOpen();
                    if (TrySetPoint(target, updated)) changed++;
                }

                // Followers retain their user-chosen offset while moving with the
                // source point. Coordinate text is rebuilt from the live source.
                foreach (ObjectId id in ids)
                {
                    DBObject follower;
                    try { follower = tr.GetObject(id, OpenMode.ForRead, false); }
                    catch { continue; }
                    Dictionary<string, string> values;
                    if (!TryReadRecord(follower, tr, FollowerRecord, out values)) continue;

                    ObjectId sourceId;
                    if (!TryResolve(database, Text(values, "Source"), out sourceId)) continue;
                    DBObject source;
                    try { source = tr.GetObject(sourceId, OpenMode.ForRead, false); }
                    catch { continue; }
                    Point3d sourcePoint;
                    if (!TryReadPoint(source, out sourcePoint)) continue;

                    Point3d lastPoint;
                    bool hasLast = TryReadStoredPoint(values, out lastPoint) ||
                        TryInferFollowerAnchor(follower, out lastPoint);
                    bool updatedFollower = false;
                    if (hasLast && lastPoint.DistanceTo(sourcePoint) > 0.0000001)
                    {
                        Entity entity = follower as Entity;
                        if (entity != null)
                        {
                            follower.UpgradeOpen();
                            entity.TransformBy(Matrix3d.Displacement(sourcePoint - lastPoint));
                            updatedFollower = true;
                        }
                    }

                    if (!follower.IsWriteEnabled) follower.UpgradeOpen();
                    if (UpdateCoordinateContents(follower, source, tr, sourcePoint))
                        updatedFollower = true;
                    Write(follower, tr, FollowerRecord,
                        new TypedValue((int)DxfCode.Text, "Schema=2"),
                        new TypedValue((int)DxfCode.Text, "Source=" + sourceId.Handle),
                        new TypedValue((int)DxfCode.Text, "LastX=" + Coordinate(sourcePoint.X, true)),
                        new TypedValue((int)DxfCode.Text, "LastY=" + Coordinate(sourcePoint.Y, true)),
                        new TypedValue((int)DxfCode.Text, "LastZ=" + Coordinate(sourcePoint.Z, true)));
                    if (updatedFollower) changed++;
                }

                tr.Commit();
            }

            // Older builds stored P1/P2 in the CE link record and point name but
            // could leave Civil 3D's raw description at the numeric point value.
            // Repair those records after the read transaction so point styles
            // using <Raw Description> and coordinate tables display one name.
            foreach (KeyValuePair<ObjectId, string> repair in pointNameRepairs)
            {
                SetPointName(database, repair.Key, repair.Value);
                changed++;
            }
            return changed;
        }

        public static int CountLinks(Database database)
        {
            if (database == null) return 0;
            int count = 0;
            using (Transaction tr = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ReadCurrentSpaceIds(database, tr))
                {
                    DBObject value;
                    try { value = tr.GetObject(id, OpenMode.ForRead, false); }
                    catch { continue; }
                    Dictionary<string, string> ignored;
                    if (TryReadRecord(value, tr, FollowerRecord, out ignored) ||
                        TryReadRecord(value, tr, VertexRecord, out ignored) ||
                        TryReadRecord(value, tr, SurfaceRecord, out ignored))
                        count++;
                }
            }
            return count;
        }

        private static List<ObjectId> ReadCurrentSpaceIds(Database database, Transaction tr)
        {
            BlockTableRecord space = tr.GetObject(
                database.CurrentSpaceId,
                OpenMode.ForRead,
                false) as BlockTableRecord;
            return space == null ? new List<ObjectId>() : space.Cast<ObjectId>().ToList();
        }

        private static bool TryReadVertex(
            Transaction tr,
            ObjectId sourceId,
            int index,
            out Point3d point)
        {
            point = Point3d.Origin;
            if (index < 0 || sourceId.IsNull || sourceId.IsErased) return false;
            DBObject source;
            try { source = tr.GetObject(sourceId, OpenMode.ForRead, false); }
            catch { return false; }

            Polyline lightweight = source as Polyline;
            if (lightweight != null && index < lightweight.NumberOfVertices)
            {
                point = lightweight.GetPoint3dAt(index);
                return true;
            }

            Polyline2d polyline2d = source as Polyline2d;
            if (polyline2d != null)
            {
                List<ObjectId> vertices = polyline2d.Cast<ObjectId>().ToList();
                if (index < vertices.Count)
                {
                    Vertex2d vertex = tr.GetObject(
                        vertices[index],
                        OpenMode.ForRead,
                        false) as Vertex2d;
                    if (vertex != null) { point = vertex.Position; return true; }
                }
            }

            Polyline3d polyline3d = source as Polyline3d;
            if (polyline3d != null)
            {
                List<ObjectId> vertices = polyline3d.Cast<ObjectId>().ToList();
                if (index < vertices.Count)
                {
                    PolylineVertex3d vertex = tr.GetObject(
                        vertices[index],
                        OpenMode.ForRead,
                        false) as PolylineVertex3d;
                    if (vertex != null) { point = vertex.Position; return true; }
                }
            }

            // FeatureLine.GetPoints requires a Civil enum whose exact assembly
            // surface varies between supported hosts, so invoke it defensively.
            try
            {
                MethodInfo method = source.GetType().GetMethods()
                    .FirstOrDefault(item => item.Name == "GetPoints" &&
                        item.GetParameters().Length == 1);
                if (method == null) return false;
                Type parameterType = method.GetParameters()[0].ParameterType;
                object allPoints = Enum.Parse(parameterType, "AllPoints", true);
                object result = method.Invoke(source, new[] { allPoints });
                var points = new List<Point3d>();
                System.Collections.IEnumerable enumerable =
                    result as System.Collections.IEnumerable;
                if (enumerable != null)
                {
                    foreach (object item in enumerable)
                        if (item is Point3d) points.Add((Point3d)item);
                }
                if (index < points.Count)
                {
                    point = points[index];
                    return true;
                }
            }
            catch
            {
                // Unsupported source types are ignored without breaking other links.
            }
            return false;
        }

        private static bool TryReadPoint(DBObject value, out Point3d point)
        {
            DBPoint dbPoint = value as DBPoint;
            if (dbPoint != null) { point = dbPoint.Position; return true; }
            CivilCogoPoint cogo = value as CivilCogoPoint;
            if (cogo != null)
            {
                point = new Point3d(cogo.Easting, cogo.Northing, cogo.Elevation);
                return true;
            }
            point = Point3d.Origin;
            return false;
        }

        private static bool TrySetPoint(DBObject value, Point3d point)
        {
            DBPoint dbPoint = value as DBPoint;
            if (dbPoint != null) { dbPoint.Position = point; return true; }
            CivilCogoPoint cogo = value as CivilCogoPoint;
            if (cogo == null) return false;
            try
            {
                SetNumber(cogo, "Easting", point.X);
                SetNumber(cogo, "Northing", point.Y);
                SetNumber(cogo, "Elevation", point.Z);
                return true;
            }
            catch { return false; }
        }

        private static void SetNumber(object value, string propertyName, double number)
        {
            PropertyInfo property = value.GetType().GetProperty(
                propertyName,
                BindingFlags.Instance | BindingFlags.Public);
            if (property == null || !property.CanWrite)
                throw new InvalidOperationException(propertyName + " is read-only.");
            property.SetValue(value, number, null);
        }

        private static bool UpdateCoordinateContents(
            DBObject follower,
            DBObject source,
            Transaction tr,
            Point3d point)
        {
            string pointName = ReadSourcePointName(source, tr);
            string contents = string.Join(
                "\\P",
                pointName,
                "X: " + point.X.ToString("N3", CultureInfo.CurrentCulture),
                "Y: " + point.Y.ToString("N3", CultureInfo.CurrentCulture),
                "Z: " + point.Z.ToString("N3", CultureInfo.CurrentCulture));
            MText text = follower as MText;
            if (text != null)
            {
                if (string.Equals(text.Contents, contents, StringComparison.Ordinal)) return false;
                text.Contents = contents;
                return true;
            }
            MLeader leader = follower as MLeader;
            if (leader != null && leader.ContentType == ContentType.MTextContent)
            {
                MText leaderText = leader.MText;
                if (leaderText == null || string.Equals(leaderText.Contents, contents, StringComparison.Ordinal))
                    return false;
                leaderText.Contents = contents;
                leader.MText = leaderText;
                return true;
            }
            return false;
        }

        private static string ReadSourcePointName(DBObject source, Transaction tr)
        {
            CivilCogoPoint cogo = source as CivilCogoPoint;
            if (cogo != null)
            {
                if (!string.IsNullOrWhiteSpace(cogo.PointName)) return cogo.PointName.Trim();
                if (!string.IsNullOrWhiteSpace(cogo.RawDescription)) return cogo.RawDescription.Trim();
                return "P" + cogo.PointNumber.ToString(CultureInfo.InvariantCulture);
            }
            return ReadPointName(source, tr, "<UNNAMED>");
        }

        private static bool TryInferFollowerAnchor(DBObject follower, out Point3d point)
        {
            Circle circle = follower as Circle;
            if (circle != null) { point = circle.Center; return true; }
            Line line = follower as Line;
            if (line != null)
            {
                point = new Point3d(
                    (line.StartPoint.X + line.EndPoint.X) * 0.5,
                    (line.StartPoint.Y + line.EndPoint.Y) * 0.5,
                    (line.StartPoint.Z + line.EndPoint.Z) * 0.5);
                return true;
            }
            MText text = follower as MText;
            if (text != null && TryReadCoordinateContents(text.Contents, out point)) return true;
            MLeader leader = follower as MLeader;
            if (leader != null && leader.MText != null &&
                TryReadCoordinateContents(leader.MText.Contents, out point)) return true;
            point = Point3d.Origin;
            return false;
        }

        private static bool TryReadCoordinateContents(string contents, out Point3d point)
        {
            double x = 0.0;
            double y = 0.0;
            double z = 0.0;
            bool hasX = TryReadLabelNumber(contents, "X:", out x);
            bool hasY = TryReadLabelNumber(contents, "Y:", out y);
            bool hasZ = TryReadLabelNumber(contents, "Z:", out z);
            point = hasX && hasY && hasZ ? new Point3d(x, y, z) : Point3d.Origin;
            return hasX && hasY && hasZ;
        }

        private static bool TryReadLabelNumber(string contents, string label, out double value)
        {
            value = 0.0;
            foreach (string part in (contents ?? string.Empty).Split(new[] { "\\P" }, StringSplitOptions.None))
            {
                string text = part.Trim();
                if (!text.StartsWith(label, StringComparison.OrdinalIgnoreCase)) continue;
                string number = text.Substring(label.Length).Trim();
                return double.TryParse(number, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out value) ||
                    double.TryParse(number, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out value);
            }
            return false;
        }

        private static bool TryReadStoredPoint(
            IDictionary<string, string> values,
            out Point3d point)
        {
            double x = 0.0;
            double y = 0.0;
            double z = 0.0;
            bool valid = double.TryParse(Text(values, "LastX"), NumberStyles.Float, CultureInfo.InvariantCulture, out x) &&
                double.TryParse(Text(values, "LastY"), NumberStyles.Float, CultureInfo.InvariantCulture, out y) &&
                double.TryParse(Text(values, "LastZ"), NumberStyles.Float, CultureInfo.InvariantCulture, out z);
            point = valid ? new Point3d(x, y, z) : Point3d.Origin;
            return valid;
        }

        private static bool TryResolve(Database database, string handleText, out ObjectId id)
        {
            id = ObjectId.Null;
            long handle;
            if (database == null || !long.TryParse(
                    handleText,
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out handle))
                return false;
            try
            {
                id = database.GetObjectId(false, new Handle(handle), 0);
                return !id.IsNull && !id.IsErased;
            }
            catch { return false; }
        }

        private static string Coordinate(double value, bool valid)
        {
            return valid ? value.ToString("R", CultureInfo.InvariantCulture) : string.Empty;
        }

        private static string Text(IDictionary<string, string> values, string key)
        {
            string value;
            return values != null && values.TryGetValue(key, out value)
                ? value
                : string.Empty;
        }

        private static void Write(DBObject target, Transaction tr, string name, params TypedValue[] values)
        {
            if (target == null) return;
            if (target.ExtensionDictionary.IsNull) target.CreateExtensionDictionary();
            DBDictionary dictionary = tr.GetObject(target.ExtensionDictionary, OpenMode.ForWrite, false) as DBDictionary;
            if (dictionary == null) return;
            Xrecord record;
            if (dictionary.Contains(name)) record = tr.GetObject(dictionary.GetAt(name), OpenMode.ForWrite, false) as Xrecord;
            else
            {
                record = new Xrecord();
                dictionary.SetAt(name, record);
                tr.AddNewlyCreatedDBObject(record, true);
            }
            if (record != null) record.Data = new ResultBuffer(values);
        }

        private static bool TryRead(DBObject target, Transaction tr, string recordName, string key, out string value)
        {
            value = null;
            if (target == null || target.ExtensionDictionary.IsNull) return false;
            DBDictionary dictionary = tr.GetObject(target.ExtensionDictionary, OpenMode.ForRead, false) as DBDictionary;
            if (dictionary == null || !dictionary.Contains(recordName)) return false;
            Xrecord record = tr.GetObject(dictionary.GetAt(recordName), OpenMode.ForRead, false) as Xrecord;
            if (record == null || record.Data == null) return false;
            string prefix = key + "=";
            foreach (TypedValue item in record.Data)
            {
                string text = item.Value as string;
                if (!string.IsNullOrEmpty(text) && text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    value = text.Substring(prefix.Length);
                    return true;
                }
            }
            return false;
        }

        private static bool TryReadRecord(
            DBObject target,
            Transaction tr,
            string recordName,
            out Dictionary<string, string> values)
        {
            values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (target == null || target.ExtensionDictionary.IsNull) return false;
            DBDictionary dictionary = tr.GetObject(
                target.ExtensionDictionary,
                OpenMode.ForRead,
                false) as DBDictionary;
            if (dictionary == null || !dictionary.Contains(recordName)) return false;
            Xrecord record = tr.GetObject(
                dictionary.GetAt(recordName),
                OpenMode.ForRead,
                false) as Xrecord;
            if (record == null || record.Data == null) return false;
            foreach (TypedValue item in record.Data)
            {
                string text = item.Value as string;
                if (string.IsNullOrWhiteSpace(text)) continue;
                int equals = text.IndexOf('=');
                if (equals <= 0) continue;
                values[text.Substring(0, equals)] = text.Substring(equals + 1);
            }
            return values.Count > 0;
        }
    }
}

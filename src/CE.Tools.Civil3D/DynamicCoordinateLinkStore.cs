using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CivilCogoPoint = Autodesk.Civil.DatabaseServices.CogoPoint;

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
                foreach (ObjectId id in generatedIds)
                {
                    if (id.IsNull || id.IsErased || id == sourceId) continue;
                    DBObject target;
                    try { target = tr.GetObject(id, OpenMode.ForWrite, false); }
                    catch { continue; }
                    Write(target, tr, FollowerRecord,
                        new TypedValue((int)DxfCode.Text, "Schema=1"),
                        new TypedValue((int)DxfCode.Text, "Source=" + sourceId.Handle));
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
            using (Transaction tr = database.TransactionManager.StartTransaction())
            {
                DBObject target = tr.GetObject(pointId, OpenMode.ForWrite, false);
                CivilCogoPoint cogo = target as CivilCogoPoint;
                if (cogo != null && value.Length > 0)
                {
                    try { cogo.PointName = value; cogo.RawDescription = value; }
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
            // Existing dedicated table, comment and surface refresh managers perform
            // the geometry updates. This method remains the common safe entry point.
            return document == null ? 0 : 0;
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
    }
}

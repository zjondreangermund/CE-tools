using System;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace CETools.Civil3D
{
    /// <summary>
    /// Keeps CE parking-number labels attached to their source bay. Labels move
    /// when a bay block/polyline moves and are removed when the bay is erased.
    /// </summary>
    internal static class ParkingNumberLinkStore
    {
        private const string RegAppName = "CE_TOOLS_PARK_NUMBER";

        public static void Link(
            Database database,
            Transaction transaction,
            Entity label,
            ObjectId sourceId)
        {
            if (database == null || transaction == null || label == null ||
                sourceId.IsNull || sourceId.IsErased)
            {
                return;
            }
            EnsureRegApp(database, transaction);
            label.XData = new ResultBuffer(
                new TypedValue(
                    (int)DxfCode.ExtendedDataRegAppName,
                    RegAppName),
                new TypedValue(
                    (int)DxfCode.ExtendedDataAsciiString,
                    sourceId.Handle.ToString()));
        }

        public static int RefreshAll(Document document)
        {
            if (document == null) return 0;
            int changed = 0;
            Database database = document.Database;
            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                BlockTableRecord currentSpace = transaction.GetObject(
                    database.CurrentSpaceId,
                    OpenMode.ForRead,
                    false) as BlockTableRecord;
                if (currentSpace == null) return 0;

                foreach (ObjectId objectId in currentSpace)
                {
                    MText label = transaction.GetObject(
                        objectId,
                        OpenMode.ForRead,
                        false) as MText;
                    if (label == null) continue;
                    string sourceHandle = ReadSourceHandle(label);
                    if (string.IsNullOrWhiteSpace(sourceHandle)) continue;

                    ObjectId sourceId;
                    if (!TryResolve(database, sourceHandle, out sourceId))
                    {
                        label.UpgradeOpen();
                        label.Erase();
                        changed++;
                        continue;
                    }

                    Entity source = transaction.GetObject(
                        sourceId,
                        OpenMode.ForRead,
                        false) as Entity;
                    Point3d centre;
                    if (source == null || !TryGetCentre(source, out centre))
                        continue;
                    if (label.Location.DistanceTo(centre) <= 0.0000001)
                        continue;
                    label.UpgradeOpen();
                    label.Location = centre;
                    changed++;
                }
                transaction.Commit();
            }
            return changed;
        }

        private static string ReadSourceHandle(Entity label)
        {
            ResultBuffer data = label.GetXDataForApplication(RegAppName);
            if (data == null) return string.Empty;
            foreach (TypedValue value in data)
            {
                if (value.TypeCode ==
                    (int)DxfCode.ExtendedDataAsciiString)
                {
                    return Convert.ToString(
                        value.Value,
                        CultureInfo.InvariantCulture);
                }
            }
            return string.Empty;
        }

        private static bool TryResolve(
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
                objectId = database.GetObjectId(
                    false,
                    new Handle(value),
                    0);
                return !objectId.IsNull && !objectId.IsErased;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetCentre(Entity entity, out Point3d centre)
        {
            centre = Point3d.Origin;
            try
            {
                Extents3d extents = entity.GeometricExtents;
                centre = new Point3d(
                    (extents.MinPoint.X + extents.MaxPoint.X) / 2.0,
                    (extents.MinPoint.Y + extents.MaxPoint.Y) / 2.0,
                    (extents.MinPoint.Z + extents.MaxPoint.Z) / 2.0);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void EnsureRegApp(
            Database database,
            Transaction transaction)
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
    }
}

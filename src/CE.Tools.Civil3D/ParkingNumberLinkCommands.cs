using System;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.ParkingNumberLinkCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Keeps CE_PKNUMBER2 labels linked to their parking bay.  Refresh is
    /// deliberately explicit in this first increment so drawing edits are never
    /// made from inside an AutoCAD object-modified event.
    /// </summary>
    public sealed class ParkingNumberLinkCommands
    {
        private const string LinkRecordName = "CE_PARKING_NUMBER_LINK";

        [CommandMethod("CE_TOOLS", "CE_PKNUMBERREFRESH", CommandFlags.Modal | CommandFlags.Redraw)]
        public void RefreshParkingNumbers()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            int moved = 0;
            int removed = 0;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = transaction.GetObject(
                    document.Database.CurrentSpaceId, OpenMode.ForRead, false) as BlockTableRecord;
                if (space == null) return;

                foreach (ObjectId id in space)
                {
                    MText label = transaction.GetObject(id, OpenMode.ForWrite, false) as MText;
                    if (label == null) continue;

                    string handleText;
                    if (!TryReadSourceHandle(label, transaction, out handleText)) continue;

                    Entity bay = OpenSourceEntity(document.Database, transaction, handleText);
                    Point3d centre;
                    if (bay == null || !TryGetParkingCentre(bay, out centre))
                    {
                        label.Erase();
                        removed++;
                        continue;
                    }

                    label.Location = centre;
                    label.LayerId = bay.LayerId;
                    moved++;
                }
                transaction.Commit();
            }

            document.Editor.WriteMessage(
                "\nCE_PKNUMBERREFRESH complete. Labels refreshed={0}; labels removed for deleted/invalid bays={1}.",
                moved,
                removed);
        }

        internal static void Link(Transaction transaction, MText label, Entity bay)
        {
            if (transaction == null || label == null || bay == null) return;
            if (label.ExtensionDictionary.IsNull) label.CreateExtensionDictionary();
            DBDictionary dictionary = transaction.GetObject(
                label.ExtensionDictionary, OpenMode.ForWrite, false) as DBDictionary;
            if (dictionary == null) return;

            Xrecord record;
            if (dictionary.Contains(LinkRecordName))
                record = transaction.GetObject(dictionary.GetAt(LinkRecordName), OpenMode.ForWrite, false) as Xrecord;
            else
            {
                record = new Xrecord();
                dictionary.SetAt(LinkRecordName, record);
                transaction.AddNewlyCreatedDBObject(record, true);
            }
            if (record != null)
            {
                record.Data = new ResultBuffer(
                    new TypedValue((int)DxfCode.Text, "Schema=1"),
                    new TypedValue((int)DxfCode.Text, "Source=" + bay.Handle));
            }
        }

        private static bool TryReadSourceHandle(MText label, Transaction transaction, out string handleText)
        {
            handleText = null;
            if (label.ExtensionDictionary.IsNull) return false;
            DBDictionary dictionary = transaction.GetObject(
                label.ExtensionDictionary, OpenMode.ForRead, false) as DBDictionary;
            if (dictionary == null || !dictionary.Contains(LinkRecordName)) return false;
            Xrecord record = transaction.GetObject(dictionary.GetAt(LinkRecordName), OpenMode.ForRead, false) as Xrecord;
            if (record == null || record.Data == null) return false;
            foreach (TypedValue value in record.Data)
            {
                string text = value.Value as string;
                if (!string.IsNullOrEmpty(text) && text.StartsWith("Source=", StringComparison.OrdinalIgnoreCase))
                {
                    handleText = text.Substring("Source=".Length);
                    return handleText.Length > 0;
                }
            }
            return false;
        }

        private static Entity OpenSourceEntity(Database database, Transaction transaction, string handleText)
        {
            try
            {
                long value = Convert.ToInt64(handleText, 16);
                ObjectId id = database.GetObjectId(false, new Handle(value), 0);
                return id.IsNull || id.IsErased ? null : transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
            }
            catch { return null; }
        }

        private static bool TryGetParkingCentre(Entity entity, out Point3d centre)
        {
            Polyline polyline = entity as Polyline;
            if (!(entity is BlockReference) && (polyline == null || !polyline.Closed))
            {
                centre = Point3d.Origin;
                return false;
            }
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
                centre = Point3d.Origin;
                return false;
            }
        }
    }
}

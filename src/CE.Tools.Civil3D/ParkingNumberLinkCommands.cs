using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.ParkingNumberLinkCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Keeps CE_PKNUMBER2 labels linked to their parking bay. Manual and deferred
    /// automatic refresh use the same transaction-safe implementation.
    /// </summary>
    public sealed class ParkingNumberLinkCommands
    {
        private const string LinkRecordName = "CE_PARKING_NUMBER_LINK";

        [CommandMethod("CE_TOOLS", "CE_PKNUMBERREFRESH", CommandFlags.Modal | CommandFlags.Redraw)]
        public void RefreshParkingNumbers()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            Refresh(document, true);
        }

        internal static int Refresh(Document document, bool writeMessage)
        {
            if (document == null) return 0;

            int moved = 0;
            int removed = 0;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = transaction.GetObject(
                    document.Database.CurrentSpaceId, OpenMode.ForRead, false) as BlockTableRecord;
                if (space == null) return 0;

                foreach (ObjectId id in space)
                {
                    MText label = transaction.GetObject(id, OpenMode.ForRead, false) as MText;
                    if (label == null) continue;

                    string handleText;
                    if (!TryReadSourceHandle(label, transaction, out handleText)) continue;

                    Entity bay = OpenSourceEntity(document.Database, transaction, handleText);
                    Point3d centre;
                    if (bay == null || !TryGetParkingCentre(bay, out centre))
                    {
                        label.UpgradeOpen();
                        label.Erase();
                        removed++;
                        continue;
                    }

                    label.UpgradeOpen();
                    label.Location = centre;
                    label.LayerId = bay.LayerId;
                    moved++;
                }
                transaction.Commit();
            }

            if (writeMessage)
            {
                document.Editor.WriteMessage(
                    "\nCE_PKNUMBERREFRESH complete. Labels refreshed={0}; labels removed for deleted/invalid bays={1}.",
                    moved,
                    removed);
            }
            return moved + removed;
        }

        internal static int CountLinkedLabels(Database database)
        {
            if (database == null) return 0;
            int count = 0;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = transaction.GetObject(
                    database.CurrentSpaceId,
                    OpenMode.ForRead,
                    false) as BlockTableRecord;
                if (space == null) return 0;
                foreach (ObjectId id in space)
                {
                    MText label = transaction.GetObject(id, OpenMode.ForRead, false) as MText;
                    string handleText;
                    if (label != null && TryReadSourceHandle(label, transaction, out handleText))
                        count++;
                }
            }
            return count;
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

    /// <summary>
    /// Queues parking-label refresh after parking bay edits and performs drawing
    /// changes later on Application.Idle while the editor is quiescent. No
    /// transaction is started from ObjectModified or ObjectErased.
    /// </summary>
    internal static class ParkingNumberAutoRefreshManager
    {
        private static readonly Dictionary<Database, Document> Documents =
            new Dictionary<Database, Document>();
        private static readonly HashSet<Database> Pending =
            new HashSet<Database>();
        private static bool _internalUpdate;
        private static bool _initialized;

        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;
            AcApplication.DocumentManager.DocumentCreated += OnDocumentCreated;
            AcApplication.DocumentManager.DocumentActivated += OnDocumentActivated;
            AcApplication.DocumentManager.DocumentToBeDestroyed += OnDocumentToBeDestroyed;
            AcApplication.Idle += OnIdle;
            foreach (Document document in AcApplication.DocumentManager)
                Attach(document);
        }

        public static void Terminate()
        {
            if (!_initialized) return;
            AcApplication.DocumentManager.DocumentCreated -= OnDocumentCreated;
            AcApplication.DocumentManager.DocumentActivated -= OnDocumentActivated;
            AcApplication.DocumentManager.DocumentToBeDestroyed -= OnDocumentToBeDestroyed;
            AcApplication.Idle -= OnIdle;
            foreach (Document document in Documents.Values.ToList())
                Detach(document);
            Documents.Clear();
            Pending.Clear();
            _initialized = false;
        }

        private static void OnDocumentCreated(object sender, DocumentCollectionEventArgs args)
        {
            Attach(args.Document);
        }

        private static void OnDocumentActivated(object sender, DocumentCollectionEventArgs args)
        {
            Attach(args.Document);
        }

        private static void OnDocumentToBeDestroyed(object sender, DocumentCollectionEventArgs args)
        {
            Detach(args.Document);
        }

        private static void Attach(Document document)
        {
            if (document == null || Documents.ContainsKey(document.Database)) return;
            Documents.Add(document.Database, document);
            document.Database.ObjectModified += OnParkingBayChanged;
            document.Database.ObjectErased += OnParkingBayErased;
        }

        private static void Detach(Document document)
        {
            if (document == null || !Documents.ContainsKey(document.Database)) return;
            document.Database.ObjectModified -= OnParkingBayChanged;
            document.Database.ObjectErased -= OnParkingBayErased;
            Documents.Remove(document.Database);
            Pending.Remove(document.Database);
        }

        private static void OnParkingBayChanged(object sender, ObjectEventArgs args)
        {
            if (_internalUpdate || args == null || !IsParkingBay(args.DBObject)) return;
            Database database = args.DBObject.Database;
            if (database != null && Documents.ContainsKey(database)) Pending.Add(database);
        }

        private static void OnParkingBayErased(object sender, ObjectErasedEventArgs args)
        {
            if (_internalUpdate || args == null || !args.Erased || !IsParkingBay(args.DBObject)) return;
            Database database = args.DBObject.Database;
            if (database != null && Documents.ContainsKey(database)) Pending.Add(database);
        }

        private static bool IsParkingBay(DBObject value)
        {
            // Queue every ordinary polyline so changing a numbered closed bay to
            // an open/invalid outline removes its linked label on refresh.
            return value is BlockReference || value is Polyline;
        }

        private static void OnIdle(object sender, EventArgs args)
        {
            if (_internalUpdate || Pending.Count == 0) return;
            foreach (Database database in Pending.ToList())
            {
                Document document;
                if (!Documents.TryGetValue(database, out document) || document == null)
                {
                    Pending.Remove(database);
                    continue;
                }
                if (document != AcApplication.DocumentManager.MdiActiveDocument ||
                    !document.Editor.IsQuiescent)
                    continue;

                Pending.Remove(database);
                try
                {
                    _internalUpdate = true;
                    using (document.LockDocument())
                        ParkingNumberLinkCommands.Refresh(document, false);
                }
                catch (System.Exception exception)
                {
                    document.Editor.WriteMessage(
                        "\nCE Tools parking-number auto-refresh deferred. {0}",
                        exception.Message);
                    Pending.Add(database);
                }
                finally
                {
                    _internalUpdate = false;
                }
            }
        }
    }
}

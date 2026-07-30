using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;

namespace CETools.Civil3D
{
    /// <summary>
    /// Persists the parking bays behind a placed parking report table. Deleted
    /// bays disappear from the schedule on the next automatic linked refresh.
    /// </summary>
    internal static class ParkingReportLinkStore
    {
        private const string RegAppName = "CE_TOOLS_PARK_REPORT";

        public static void Link(
            Document document,
            ObjectId tableId,
            IEnumerable<ObjectId> sourceIds)
        {
            if (document == null || tableId.IsNull || sourceIds == null) return;
            using (Transaction transaction =
                document.Database.TransactionManager.StartTransaction())
            {
                Table table = transaction.GetObject(
                    tableId, OpenMode.ForWrite, false) as Table;
                if (table == null) return;
                EnsureRegApp(document.Database, transaction);
                var values = new List<TypedValue>
                {
                    new TypedValue((int)DxfCode.ExtendedDataRegAppName, RegAppName)
                };
                foreach (ObjectId sourceId in sourceIds)
                {
                    if (!sourceId.IsNull)
                    {
                        values.Add(new TypedValue(
                            (int)DxfCode.ExtendedDataAsciiString,
                            sourceId.Handle.ToString()));
                    }
                }
                table.XData = new ResultBuffer(values.ToArray());
                transaction.Commit();
            }
        }

        public static int RefreshAll(Document document)
        {
            if (document == null) return 0;
            int changed = 0;
            Database database = document.Database;
            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = transaction.GetObject(
                    database.CurrentSpaceId, OpenMode.ForRead, false) as BlockTableRecord;
                if (space == null) return 0;
                foreach (ObjectId objectId in space)
                {
                    Table table = transaction.GetObject(
                        objectId, OpenMode.ForRead, false) as Table;
                    if (table == null) continue;
                    List<string> handles = ReadHandles(table);
                    if (handles.Count == 0) continue;

                    var groups = new SortedDictionary<string, int>(
                        StringComparer.OrdinalIgnoreCase);
                    foreach (string handle in handles)
                    {
                        ObjectId sourceId;
                        if (!TryResolve(database, handle, out sourceId)) continue;
                        Entity source = transaction.GetObject(
                            sourceId, OpenMode.ForRead, false) as Entity;
                        string group = ReadGroup(transaction, source);
                        if (string.IsNullOrWhiteSpace(group)) continue;
                        int count;
                        groups.TryGetValue(group, out count);
                        groups[group] = count + 1;
                    }

                    table.UpgradeOpen();
                    table.UnmergeCells(CellRange.Create(
                        table, 0, 0, 0, Math.Max(0, table.Columns.Count - 1)));
                    table.SetSize(groups.Count + 2, 2);
                    table.MergeCells(CellRange.Create(table, 0, 0, 0, 1));
                    table.Cells[0, 0].TextString = "Parking Bay Report";
                    table.Cells[1, 0].TextString = "Parking Bay Group";
                    table.Cells[1, 1].TextString = "Count";
                    int row = 2;
                    foreach (KeyValuePair<string, int> group in groups)
                    {
                        table.Cells[row, 0].TextString = group.Key;
                        table.Cells[row, 1].TextString =
                            group.Value.ToString(CultureInfo.InvariantCulture);
                        row++;
                    }
                    table.GenerateLayout();
                    changed++;
                }
                transaction.Commit();
            }
            return changed;
        }

        private static string ReadGroup(Transaction transaction, Entity entity)
        {
            BlockReference block = entity as BlockReference;
            if (block != null)
            {
                BlockTableRecord definition = transaction.GetObject(
                    block.BlockTableRecord, OpenMode.ForRead, false) as BlockTableRecord;
                return definition == null ? null : "Block: " + definition.Name;
            }
            Polyline polyline = entity as Polyline;
            return polyline != null && polyline.Closed
                ? "Closed polyline layer: " + polyline.Layer
                : null;
        }

        private static List<string> ReadHandles(Entity entity)
        {
            var handles = new List<string>();
            ResultBuffer data = entity.GetXDataForApplication(RegAppName);
            if (data == null) return handles;
            foreach (TypedValue value in data)
            {
                if (value.TypeCode == (int)DxfCode.ExtendedDataAsciiString)
                    handles.Add(Convert.ToString(value.Value, CultureInfo.InvariantCulture));
            }
            return handles;
        }

        private static bool TryResolve(Database database, string text, out ObjectId objectId)
        {
            objectId = ObjectId.Null;
            long value;
            if (!long.TryParse(
                text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value))
                return false;
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

        private static void EnsureRegApp(Database database, Transaction transaction)
        {
            RegAppTable table = transaction.GetObject(
                database.RegAppTableId, OpenMode.ForRead, false) as RegAppTable;
            if (table == null || table.Has(RegAppName)) return;
            table.UpgradeOpen();
            var record = new RegAppTableRecord { Name = RegAppName };
            table.Add(record);
            transaction.AddNewlyCreatedDBObject(record, true);
        }
    }
}

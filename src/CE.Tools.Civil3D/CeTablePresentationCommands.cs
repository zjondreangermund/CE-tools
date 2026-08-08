using System;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.CeTablePresentationCommands))]

namespace CETools.Civil3D
{
    public sealed class CeTablePresentationCommands
    {
        [CommandMethod("CE_TOOLS", "CE_TABLECENTERALL", CommandFlags.Modal | CommandFlags.Redraw)]
        public void CenterAll()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            int changed = CeTablePresentationManager.CenterCeTables(document);
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_TABLECENTERALL complete. CE tables centered={0}.", changed);
        }
    }

    internal static class CeTablePresentationManager
    {
        internal static int CenterCeTables(Document document)
        {
            if (document == null) return 0;
            int changed = 0;
            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTable blocks = transaction.GetObject(document.Database.BlockTableId, OpenMode.ForRead, false) as BlockTable;
                if (blocks == null) return 0;
                foreach (ObjectId blockId in blocks)
                {
                    BlockTableRecord space;
                    try { space = transaction.GetObject(blockId, OpenMode.ForRead, false) as BlockTableRecord; }
                    catch { continue; }
                    if (space == null || space.LayoutId.IsNull) continue;
                    foreach (ObjectId id in space)
                    {
                        Table table;
                        try { table = transaction.GetObject(id, OpenMode.ForRead, false) as Table; }
                        catch { continue; }
                        if (table == null || !IsCeTable(table)) continue;
                        table.UpgradeOpen();
                        for (int row = 0; row < table.Rows.Count; row++)
                            for (int column = 0; column < table.Columns.Count; column++)
                                table.Cells[row, column].Alignment = CellAlignment.MiddleCenter;
                        try { table.GenerateLayout(); } catch { }
                        try { table.RecordGraphicsModified(true); } catch { }
                        changed++;
                    }
                }
                transaction.Commit();
            }
            return changed;
        }

        private static bool IsCeTable(Table table)
        {
            if (table == null || table.Rows.Count == 0 || table.Columns.Count == 0) return false;
            string title;
            try { title = table.Cells[0, 0].TextString ?? string.Empty; }
            catch { return false; }
            string upper = title.ToUpperInvariant();
            return upper.StartsWith("CE ") || upper.StartsWith("CE-") || upper.Contains("CE TOOLS") ||
                   upper.StartsWith("LINKED ") || upper.StartsWith("FEATURE LINE") || upper.StartsWith("POLYLINE ");
        }
    }
}

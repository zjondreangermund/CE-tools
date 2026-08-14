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
            document.Editor.WriteMessage("\nCE_TABLECENTERALL complete. CE tables centred and normalized={0}.", changed);
        }
    }

    internal static class CeTablePresentationManager
    {
        internal static int CenterCeTables(Document document)
        {
            if (document == null) return 0;
            int changed = 0;
            double baseTextHeight = ResolveStableTableTextHeight(document.Database);
            double titleTextHeight = baseTextHeight * 1.15;
            double normalRowHeight = Math.Max(baseTextHeight * 2.2, 0.001);
            double titleRowHeight = Math.Max(baseTextHeight * 2.5, normalRowHeight);
            double minimumColumnWidth = Math.Max(baseTextHeight * 7.5, 0.001);

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

                        // Reapply an absolute paper-derived size on every refresh.
                        // Never derive the next size from the current title cell,
                        // otherwise a title factor such as 1.15 compounds forever.
                        for (int row = 0; row < table.Rows.Count; row++)
                        {
                            try { table.Rows[row].Height = row == 0 ? titleRowHeight : normalRowHeight; }
                            catch { }
                            for (int column = 0; column < table.Columns.Count; column++)
                            {
                                table.Cells[row, column].Alignment = CellAlignment.MiddleCenter;
                                try { table.Cells[row, column].TextHeight = row == 0 ? titleTextHeight : baseTextHeight; }
                                catch { }
                            }
                        }
                        for (int column = 0; column < table.Columns.Count; column++)
                        {
                            try
                            {
                                if (table.Columns[column].Width < minimumColumnWidth)
                                    table.Columns[column].Width = minimumColumnWidth;
                            }
                            catch { }
                        }
                        try { PaperAnnotationScale.SetAnnotative(table); } catch { }
                        try { table.GenerateLayout(); } catch { }
                        try { table.RecordGraphicsModified(true); } catch { }
                        changed++;
                    }
                }
                transaction.Commit();
            }
            return changed;
        }

        private static double ResolveStableTableTextHeight(Database database)
        {
            double paperHeight = 2.0;
            try
            {
                AnnotationOptions settings = AnnotationSettingsStore.Read(database);
                if (settings != null)
                    paperHeight = Math.Min(2.0, Math.Max(1.8, settings.TextHeight));
            }
            catch { }
            try
            {
                return Math.Max(PaperAnnotationScale.ModelTextHeight(database, paperHeight), 0.001);
            }
            catch
            {
                return Math.Max(paperHeight, 0.001);
            }
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

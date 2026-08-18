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
        private const double Tolerance = 1e-8;

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

                        bool needsUpdate = NeedsPresentationUpdate(
                            table,
                            baseTextHeight,
                            titleTextHeight,
                            normalRowHeight,
                            titleRowHeight,
                            minimumColumnWidth);
                        if (!needsUpdate) continue;

                        table.UpgradeOpen();
                        for (int row = 0; row < table.Rows.Count; row++)
                        {
                            double desiredRowHeight = row == 0 ? titleRowHeight : normalRowHeight;
                            try
                            {
                                if (!NearlyEqual(table.Rows[row].Height, desiredRowHeight))
                                    table.Rows[row].Height = desiredRowHeight;
                            }
                            catch { }

                            for (int column = 0; column < table.Columns.Count; column++)
                            {
                                try
                                {
                                    if (table.Cells[row, column].Alignment != CellAlignment.MiddleCenter)
                                        table.Cells[row, column].Alignment = CellAlignment.MiddleCenter;
                                }
                                catch { }

                                double desiredTextHeight = row == 0 ? titleTextHeight : baseTextHeight;
                                try
                                {
                                    if (!NearlyEqual(table.Cells[row, column].TextHeight, desiredTextHeight))
                                        table.Cells[row, column].TextHeight = desiredTextHeight;
                                }
                                catch { }
                            }
                        }

                        for (int column = 0; column < table.Columns.Count; column++)
                        {
                            try
                            {
                                if (table.Columns[column].Width + Tolerance < minimumColumnWidth)
                                    table.Columns[column].Width = minimumColumnWidth;
                            }
                            catch { }
                        }

                        // Only regenerate graphics when a table actually needed a
                        // presentation change. This prevents idle linked refreshes
                        // from repeatedly flashing every CE table on screen.
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

        private static bool NeedsPresentationUpdate(
            Table table,
            double baseTextHeight,
            double titleTextHeight,
            double normalRowHeight,
            double titleRowHeight,
            double minimumColumnWidth)
        {
            if (table == null) return false;
            for (int row = 0; row < table.Rows.Count; row++)
            {
                double desiredRowHeight = row == 0 ? titleRowHeight : normalRowHeight;
                try
                {
                    if (!NearlyEqual(table.Rows[row].Height, desiredRowHeight)) return true;
                }
                catch { return true; }

                for (int column = 0; column < table.Columns.Count; column++)
                {
                    try
                    {
                        if (table.Cells[row, column].Alignment != CellAlignment.MiddleCenter) return true;
                    }
                    catch { return true; }

                    double desiredTextHeight = row == 0 ? titleTextHeight : baseTextHeight;
                    try
                    {
                        if (!NearlyEqual(table.Cells[row, column].TextHeight, desiredTextHeight)) return true;
                    }
                    catch { return true; }
                }
            }

            for (int column = 0; column < table.Columns.Count; column++)
            {
                try
                {
                    if (table.Columns[column].Width + Tolerance < minimumColumnWidth) return true;
                }
                catch { return true; }
            }
            return false;
        }

        private static bool NearlyEqual(double left, double right)
        {
            return Math.Abs(left - right) <= Math.Max(Tolerance, Math.Abs(right) * 1e-8);
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

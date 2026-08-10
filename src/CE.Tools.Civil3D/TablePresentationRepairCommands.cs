using System;
using System.Collections.Generic;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.TablePresentationRepairCommands))]

namespace CETools.Civil3D
{
    public sealed class TablePresentationRepairCommands
    {
        [CommandMethod("CE_TOOLS", "CE_TABLEPRESENTATIONFIX", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void RepairTables()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Table Presentation Repair",
                "Restore visible grid lines, centred text and readable spacing on CE tables without changing linked data or source geometry.");
            model.AddChoice("Scope", "01 Selection", "Tables", "Selected", "Repair selected tables or every AutoCAD table in current space.", new[] { "Selected", "All" });
            model.AddChoice("Grid", "02 Grid", "Table grid lines", "Show all grid lines", "Force every cell grid line visible or leave current visibility unchanged.", new[] { "Show all grid lines", "Keep current" });
            model.AddChoice("Alignment", "03 Text", "Cell text alignment", "Middle center", "Centre all table text or keep existing alignment.", new[] { "Middle center", "Keep current" });
            model.AddPositiveDouble("RowFactor", "04 Spacing", "Minimum row height factor", 1.8, "Minimum row height as a multiple of the current cell text height.");
            model.AddPositiveDouble("ColumnFactor", "04 Spacing", "Minimum column width factor", 7.5, "Minimum generic column width as a multiple of the current cell text height; existing wider columns are retained.");
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            HashSet<ObjectId> selected = null;
            if (string.Equals(model.Text("Scope"), "Selected", StringComparison.OrdinalIgnoreCase))
            {
                PromptSelectionResult result = document.Editor.SelectImplied();
                if (result.Status != PromptStatus.OK || result.Value == null || result.Value.Count == 0)
                    result = document.Editor.GetSelection(new PromptSelectionOptions { MessageForAdding = "\nSelect tables to repair: ", AllowDuplicates = false, RejectObjectsFromNonCurrentSpace = true });
                if (result.Status != PromptStatus.OK || result.Value == null) return;
                selected = new HashSet<ObjectId>(result.Value.GetObjectIds());
            }

            bool showGrid = string.Equals(model.Text("Grid"), "Show all grid lines", StringComparison.OrdinalIgnoreCase);
            bool centre = string.Equals(model.Text("Alignment"), "Middle center", StringComparison.OrdinalIgnoreCase);
            int changed = 0;
            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = transaction.GetObject(document.Database.CurrentSpaceId, OpenMode.ForRead, false) as BlockTableRecord;
                if (space == null) return;
                foreach (ObjectId id in space)
                {
                    if (selected != null && !selected.Contains(id)) continue;
                    Table table;
                    try { table = transaction.GetObject(id, OpenMode.ForWrite, false) as Table; }
                    catch { continue; }
                    if (table == null) continue;

                    double textHeight = ReadRepresentativeTextHeight(table);
                    double minimumRow = Math.Max(textHeight * model.Double("RowFactor", 1.8), 0.001);
                    double minimumColumn = Math.Max(textHeight * model.Double("ColumnFactor", 7.5), 0.001);
                    for (int row = 0; row < table.Rows.Count; row++)
                    {
                        if (table.Rows[row].Height < minimumRow) table.Rows[row].Height = minimumRow;
                        for (int column = 0; column < table.Columns.Count; column++)
                        {
                            if (centre) table.Cells[row, column].Alignment = CellAlignment.MiddleCenter;
                            if (showGrid) SetCellGridVisible(table, row, column);
                        }
                    }
                    for (int column = 0; column < table.Columns.Count; column++)
                        if (table.Columns[column].Width < minimumColumn) table.Columns[column].Width = minimumColumn;
                    try { table.GenerateLayout(); } catch { }
                    try { table.RecordGraphicsModified(true); } catch { }
                    changed++;
                }
                transaction.Commit();
            }
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_TABLEPRESENTATIONFIX complete. Tables repaired={0}.", changed);
        }

        private static void SetCellGridVisible(Table table, int row, int column)
        {
            if (table == null) return;
            try
            {
                // Some AutoCAD releases expose the modern FormattedTableData-like
                // cell overload directly on Table; discover it at runtime so the
                // Civil 3D 2023 compiler is not tied to that overload.
                MethodInfo method = table.GetType().GetMethod(
                    "SetGridVisibility",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new[]
                    {
                        typeof(int),
                        typeof(int),
                        typeof(GridLineType),
                        typeof(Autodesk.AutoCAD.DatabaseServices.Visibility)
                    },
                    null);
                if (method != null)
                {
                    method.Invoke(table, new object[]
                    {
                        row,
                        column,
                        GridLineType.AllGridLines,
                        Autodesk.AutoCAD.DatabaseServices.Visibility.Visible
                    });
                    return;
                }
            }
            catch { }

            try
            {
#pragma warning disable 618
                table.SetGridVisibility(
                    true,
                    (int)GridLineType.AllGridLines,
                    (int)(RowType.DataRow | RowType.HeaderRow | RowType.TitleRow));
#pragma warning restore 618
            }
            catch { }
        }

        private static double ReadRepresentativeTextHeight(Table table)
        {
            if (table == null) return 1.0;
            double value = 0.0;
            for (int row = 0; row < table.Rows.Count; row++)
                for (int column = 0; column < table.Columns.Count; column++)
                {
                    try
                    {
                        double cellTextHeight = table.Cells[row, column].TextHeight;
                        if (cellTextHeight > value) value = cellTextHeight;
                    }
                    catch { }
                }
            return value > 1e-9 ? value : 1.0;
        }
    }
}

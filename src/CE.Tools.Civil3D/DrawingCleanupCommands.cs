using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.DrawingCleanupCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Runs the existing AutoCAD cleanup commands through one controlled CE Tools entry point.
    /// The command can run all stages or one stage at a time without introducing duplicate tools.
    /// </summary>
    public sealed class DrawingCleanupCommands
    {
        private const string AllKeyword = "All";
        private const string OverkillKeyword = "Overkill";
        private const string AuditKeyword = "Audit";
        private const string PurgeKeyword = "Purge";

        [CommandMethod(
            "CE_TOOLS",
            "CE_DRAWCLEAN",
            CommandFlags.Modal | CommandFlags.Redraw | CommandFlags.UsePickSet)]
        public void Execute()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null)
            {
                return;
            }

            Editor editor = document.Editor;
            string mode = DisciplineWorkflowDialogs.SelectWorkflow(
                "CE Tools - Drawing Cleanup",
                "Choose the cleanup stages to run. The selected work is previewed and confirmed before AutoCAD changes the drawing.",
                new List<DisciplineWorkflowAction>
                {
                    new DisciplineWorkflowAction("Complete cleanup", AllKeyword, "Run OVERKILL, AUDIT and three PURGE passes.", "01 Recommended"),
                    new DisciplineWorkflowAction("Remove duplicate geometry", OverkillKeyword, "Run AutoCAD OVERKILL on the preselection or current space.", "02 Individual Stages"),
                    new DisciplineWorkflowAction("Audit drawing", AuditKeyword, "Run AutoCAD AUDIT and fix detected drawing errors.", "02 Individual Stages"),
                    new DisciplineWorkflowAction("Purge unused objects", PurgeKeyword, "Run three controlled PURGE passes.", "02 Individual Stages")
                });
            if (string.IsNullOrWhiteSpace(mode)) return;

            Run(document, mode);
        }

        [CommandMethod("CE_TOOLS", "CE_DRAWCLEANALL", CommandFlags.Modal | CommandFlags.Redraw | CommandFlags.UsePickSet)]
        public void RunAll()
        {
            Run(AcApplication.DocumentManager.MdiActiveDocument, AllKeyword);
        }

        [CommandMethod("CE_TOOLS", "CE_DRAWOVERKILL", CommandFlags.Modal | CommandFlags.Redraw | CommandFlags.UsePickSet)]
        public void RunOverkillOnly()
        {
            Run(AcApplication.DocumentManager.MdiActiveDocument, OverkillKeyword);
        }

        [CommandMethod("CE_TOOLS", "CE_DRAWAUDIT", CommandFlags.Modal | CommandFlags.Redraw)]
        public void RunAuditOnly()
        {
            Run(AcApplication.DocumentManager.MdiActiveDocument, AuditKeyword);
        }

        [CommandMethod("CE_TOOLS", "CE_DRAWPURGE", CommandFlags.Modal | CommandFlags.Redraw)]
        public void RunPurgeOnly()
        {
            Run(AcApplication.DocumentManager.MdiActiveDocument, PurgeKeyword);
        }

        private static void Run(Document document, string mode)
        {
            if (document == null) return;
            Editor editor = document.Editor;

            bool runOverkill = string.Equals(mode, AllKeyword, StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(mode, OverkillKeyword, StringComparison.OrdinalIgnoreCase);
            bool runAudit = string.Equals(mode, AllKeyword, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(mode, AuditKeyword, StringComparison.OrdinalIgnoreCase);
            bool runPurge = string.Equals(mode, AllKeyword, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(mode, PurgeKeyword, StringComparison.OrdinalIgnoreCase);

            editor.WriteMessage(
                "\nCE_DRAWCLEAN preview: OVERKILL={0}; AUDIT={1}; PURGE={2}.",
                runOverkill ? "Yes" : "No",
                runAudit ? "Yes" : "No",
                runPurge ? "Yes" : "No");
            editor.WriteMessage(
                "\nOVERKILL uses the preselection when available; otherwise it processes all supported geometry in the current space.");

            if (!Confirm(editor, "Run the selected drawing-cleanup stages"))
            {
                editor.WriteMessage("\nCE_DRAWCLEAN cancelled. No cleanup commands were run.");
                return;
            }

            try
            {
                if (runOverkill)
                {
                    RunOverkill(editor);
                }

                if (runAudit)
                {
                    editor.WriteMessage("\nCE_DRAWCLEAN: auditing and fixing detected drawing errors...");
                    editor.Command("_.AUDIT", "_Y");
                }

                if (runPurge)
                {
                    editor.WriteMessage("\nCE_DRAWCLEAN: purging unused named objects...");
                    RunPurgePass(editor);
                    RunPurgePass(editor);
                    RunPurgePass(editor);
                }

                editor.WriteMessage("\nCE_DRAWCLEAN complete.");
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_DRAWCLEAN stopped before all requested stages completed: " +
                    exception.Message);
            }
        }

        private static void RunOverkill(Editor editor)
        {
            PromptSelectionResult implied = editor.SelectImplied();
            if (implied.Status == PromptStatus.OK &&
                implied.Value != null &&
                implied.Value.Count > 0)
            {
                editor.WriteMessage(
                    "\nCE_DRAWCLEAN: running OVERKILL on the preselected objects using AutoCAD defaults...");
                editor.Command(
                    "_.-OVERKILL",
                    implied.Value,
                    string.Empty,
                    string.Empty);
                return;
            }

            editor.WriteMessage(
                "\nCE_DRAWCLEAN: running OVERKILL on all supported objects in the current space using AutoCAD defaults...");
            editor.Command(
                "_.-OVERKILL",
                "_ALL",
                string.Empty,
                string.Empty);
        }

        private static void RunPurgePass(Editor editor)
        {
            editor.Command(
                "_.-PURGE",
                "_ALL",
                "*",
                "_N");
        }

        private static bool Confirm(Editor editor, string message)
        {
            return DisciplineWorkflowDialogs.Confirm(
                "CE Tools - Drawing Cleanup",
                message + "?");
        }
    }
}

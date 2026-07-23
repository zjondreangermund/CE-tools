using System;
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
            var options = new PromptKeywordOptions(
                "\nDrawing cleanup [All/Overkill/Audit/Purge] <All>: ")
            {
                AllowNone = true
            };
            options.Keywords.Add(AllKeyword);
            options.Keywords.Add(OverkillKeyword);
            options.Keywords.Add(AuditKeyword);
            options.Keywords.Add(PurgeKeyword);

            PromptResult result = editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel)
            {
                return;
            }

            string mode = result.Status == PromptStatus.None
                ? AllKeyword
                : result.StringResult;

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
            var options = new PromptKeywordOptions(
                "\n" + message + "? [Yes/No] <No>: ")
            {
                AllowNone = true
            };
            options.Keywords.Add("Yes");
            options.Keywords.Add("No");

            PromptResult result = editor.GetKeywords(options);
            return result.Status == PromptStatus.OK &&
                   string.Equals(
                       result.StringResult,
                       "Yes",
                       StringComparison.OrdinalIgnoreCase);
        }
    }
}

using System;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.September14FeatureLineCompletionCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Focused completion front doors for the remaining feature-line field comments.
    /// Existing CE_FLRELLINKEXISTING remains registered in the August 24 field pack
    /// and is surfaced through CE_FIELDCOMPLETION. New stepped offsets are delegated
    /// to the August 21 candidate-first fatal-safety boundary, and direct surface
    /// links are delegated to the August 23 dynamic drape implementation.
    /// </summary>
    public sealed class September14FeatureLineCompletionCommands
    {
        [CommandMethod(
            "CE_TOOLS",
            "CE_FLSTEPSSAFE",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void CreateSafeSteppedOffsets()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Safe Multiple Feature-Line Steps",
                "Create stepped offsets from multiple source feature lines. Each candidate is committed and verified before it becomes a linked CE_FLREL child.");
            settings.AddPositiveDouble("Horizontal", "Steps", "Horizontal step", 1.0, "Horizontal offset per step.");
            settings.AddText("Vertical", "Steps", "Vertical step", "-0.500", "Signed elevation difference per step.");
            settings.AddPositiveInteger("Count", "Steps", "Step count", 1, "Number of linked children per selected source.");
            settings.AddText("Suffix", "Naming", "Child suffix", "STEP", "Suffix used for generated feature-line names.");
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;

            double vertical;
            if (!ProductionSettingsDialogModel.TryDouble(settings.Text("Vertical"), out vertical))
            {
                document.Editor.WriteMessage("\nCE_FLSTEPSSAFE cancelled. Vertical step must be a number.");
                return;
            }

            var selectionOptions = new PromptSelectionOptions
            {
                MessageForAdding = "\nSelect multiple source feature lines for stepped offsets: "
            };
            PromptSelectionResult selection = document.Editor.GetSelection(selectionOptions);
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;

            PlatformDynamicRefreshManager.EnsureInitialized();
            August21PlatformRelativeFatalSafety.PlatformStepResult result =
                August21PlatformRelativeFatalSafety.CreatePlatformSteps(
                    document,
                    selection.Value.GetObjectIds().Distinct(),
                    Math.Max(0.001, settings.Double("Horizontal", 1.0)),
                    vertical,
                    Math.Max(1, settings.Integer("Count", 1)),
                    string.IsNullOrWhiteSpace(settings.Text("Suffix")) ? "STEP" : settings.Text("Suffix").Trim());

            if (result.Created > 0) PlatformDynamicRefreshManager.Queue();
            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_FLSTEPSSAFE complete. Linked steps={0}; skipped={1}. Existing source feature lines were kept.",
                result.Created,
                result.Skipped);
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_FLSURFACELINKEXISTING",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void LinkExistingFeatureLinesToSurface()
        {
            // Reuse the established August 23 multi-feature-line dynamic drape. It
            // samples through August21SurfaceSafety and stores the persistent direct
            // drape link without rebuilding/deleting the selected source feature line.
            new August23PlatformDynamicGradingCommands().DrapeMultipleFeatureLines();
        }
    }
}

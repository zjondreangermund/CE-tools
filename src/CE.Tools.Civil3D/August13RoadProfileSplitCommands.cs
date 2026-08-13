using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.August13RoadProfileSplitCommands))]

namespace CETools.Civil3D
{
    public sealed class August13RoadProfileSplitCommands
    {
        [CommandMethod("CE_TOOLS", "CE_ROADPROFILEVIEWSPLIT", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void SplitRoadProfileViews()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (document == null || civilDocument == null) return;
            Editor editor = document.Editor;

            PromptSelectionResult selection = editor.GetSelection(new PromptSelectionOptions
            {
                MessageForAdding = "\nSelect one or more road profile views to split into station sections: "
            });
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;
            List<ObjectId> selectedIds = August13RoadProfileSplitSupport.ReadSelectedProfileViews(document.Database, selection.Value.GetObjectIds());
            if (selectedIds.Count == 0)
            {
                editor.WriteMessage("\nCE_ROADPROFILEVIEWSPLIT cancelled. No Civil 3D profile views were selected.");
                return;
            }

            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Split Road Profile Views",
                "Split selected road profile views into consecutive station sections. Default: 0.000-750.000, 750.000-1500.000, 1500.000-2250.000, etc.");
            settings.AddText("FirstStation", "01 Station sections", "First split station", "0.000", "Clamped to the selected profile-view/alignment start.");
            settings.AddText("LastStation", "01 Station sections", "Last split station", "0.000", "Use 0.000 to continue to the selected profile-view/alignment end.");
            settings.AddPositiveDouble("SectionLength", "01 Station sections", "Section length", 750.0, "Default interval is 750.000 drawing units/metres when the drawing is in metres.");
            settings.AddChoice("Layout", "02 Placement", "New view layout", "Vertical - top to bottom", "Choose how additional section views are placed.", new[] { "Vertical - top to bottom", "Horizontal - left to right" });
            settings.AddPositiveDouble("Spacing", "02 Placement", "Section view insertion spacing", 150.0, "Drawing-unit spacing between consecutive profile-view insertion points.");
            settings.AddChoice("Finalize", "03 Styles and bands", "Finalize Road profile styles/bands", "Yes", "Run CE_ROADPROFILEVIEWFINAL after splitting.", new[] { "Yes", "No" });
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;

            double first = ParseDouble(settings.Text("FirstStation"), 0.0);
            double last = ParseDouble(settings.Text("LastStation"), 0.0);
            double interval = Math.Max(settings.Double("SectionLength", 750.0), 0.001);
            double spacing = Math.Max(settings.Double("Spacing", 150.0), 1.0);
            bool horizontal = string.Equals(settings.Text("Layout"), "Horizontal - left to right", StringComparison.OrdinalIgnoreCase);
            bool finalize = !string.Equals(settings.Text("Finalize"), "No", StringComparison.OrdinalIgnoreCase);
            August13RoadProfileSplitEngine.Execute(document, civilDocument, selectedIds, first, last, interval, spacing, horizontal, finalize);
        }

        private static double ParseDouble(string text, double fallback)
        {
            double value;
            return ProductionSettingsDialogModel.TryDouble(text, out value) ? value : fallback;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivilProfileView = Autodesk.Civil.DatabaseServices.ProfileView;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.WaterProfileSheetCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Starts the existing multi-profile paper-space viewport workflow
    /// after validating the selected WATER profile views. Preserves the
    /// established viewport fit/page scale logic and title block settings.
    /// </summary>
    public sealed class WaterProfileSheetCommands
    {
        [CommandMethod("CE_TOOLS", "CE_WATERPROFILESHEETS",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void PrepareWaterProfileSheets()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civil = CivilApplication.ActiveDocument;
            if (document == null || civil == null) return;
            var waterViews = new List<ObjectId>();
            using (Transaction read = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId alignmentId in civil.GetAlignmentIds())
                {
                    CivilAlignment alignment = read.GetObject(alignmentId, OpenMode.ForRead, false)
                        as CivilAlignment;
                    if (alignment == null || alignment.IsReferenceObject ||
                        !(alignment.Name.StartsWith("W-", StringComparison.OrdinalIgnoreCase) ||
                          alignment.Name.IndexOf("Water", StringComparison.OrdinalIgnoreCase) >= 0))
                        continue;
                    waterViews.AddRange(alignment.GetProfileViewIds().Cast<ObjectId>());
                }
            }
            if (waterViews.Count == 0)
            {
                MessageBox.Show("No Water profile views were found. Create Water alignments and profiles first.",
                    "CE Tools - Water Profile Sheets");
                return;
            }

            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Water Profile Sheets",
                "Prepare Water longitudinal-section sheets using the existing multi-profile viewport engine. " +
                "Choose source layout handling, then select Water profile views in the next viewport dialog. " +
                "Existing title-block layouts, band sets and engineering profiles remain unchanged.");
            settings.AddChoice("Template", "01 Layout", "Paper-space layouts",
                "Use layouts already in drawing",
                "Import existing paper-space title-block layouts first or use layouts already present.",
                new[] { "Use layouts already in drawing", "Import layouts from another DWG first" });
            settings.AddChoice("Scope", "02 Profiles", "Water profiles",
                "Select Water profile views",
                "Select Water profile views in the drawing. The existing viewport fitting workflow controls scale and layout.",
                new[] { "Select Water profile views", "Use existing viewport-fit selection" });
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;

            if (settings.Text("Template") == "Import layouts from another DWG first")
            {
                document.Editor.WriteMessage("\nWater profile sheets: importing paper-space title blocks first. Run CE_WATERPROFILESHEETS again after completing layout import.");
                document.SendStringToExecute("_.CE_IMPORTSHEETS\n", true, false, false);
                return;
            }
            // Native select-implied objects can be carried to the existing
            // viewport-fitting workflow. Nothing in model space is edited here.
            if (settings.Text("Scope") == "Select Water profile views")
            {
                var validIds = new HashSet<ObjectId>(waterViews);
                PromptSelectionResult result = document.Editor.GetSelection(new PromptSelectionOptions
                {
                    MessageForAdding = "\nSelect Water profile views to place on sheets: ",
                    AllowDuplicates = false,
                    RejectObjectsFromNonCurrentSpace = true
                });
                if (result.Status != PromptStatus.OK || result.Value == null) return;
                var chosen = result.Value.GetObjectIds().Where(validIds.Contains).Distinct().ToArray();
                if (chosen.Length == 0)
                {
                    MessageBox.Show("No matching Water profile views selected.",
                        "CE Tools - Water Profile Sheets");
                    return;
                }
                document.Editor.SetImpliedSelection(chosen);
            }
            document.Editor.WriteMessage(
                "\nWater profile sheets: using existing CE_PROFILEVIEWPORTFIT for layout, scale and title-block-compatible viewport placement.");
            document.SendStringToExecute("_.CE_PROFILEVIEWPORTFIT\n", true, false, false);
        }
    }
}

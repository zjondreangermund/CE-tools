using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using CivilNetwork = Autodesk.Civil.DatabaseServices.Network;
using CivilPipe = Autodesk.Civil.DatabaseServices.Pipe;
using CivilStructure = Autodesk.Civil.DatabaseServices.Structure;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.WaterGravityLabelCommands))]

namespace CETools.Civil3D
{
    // Gravity Water labels must not update Sewer Production's saved label styles.
    public sealed class WaterGravityLabelCommands
    {
        [CommandMethod("CE_TOOLS", "CE_WATERGRAVITYLABELS",
            CommandFlags.Modal | CommandFlags.Redraw | CommandFlags.UsePickSet)]
        public void LabelGravityWater()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civil = CivilApplication.ActiveDocument;
            if (document == null || civil == null) return;
            var networks = new List<ObjectId>();
            using (Transaction tr = document.Database.TransactionManager.StartTransaction())
            {
                PromptSelectionResult selection = document.Editor.SelectImplied();
                bool selected = selection.Status == PromptStatus.OK &&
                    selection.Value != null && selection.Value.Count > 0;
                if (!selected)
                {
                    var scope = new PromptKeywordOptions(
                        "\nGravity water labels [All/Selected] <All>: ");
                    scope.Keywords.Add("All");
                    scope.Keywords.Add("Selected");
                    scope.AllowNone = true;
                    var answer = document.Editor.GetKeywords(scope);
                    if (answer.Status == PromptStatus.Cancel) return;
                    selected = answer.Status == PromptStatus.OK &&
                        answer.StringResult == "Selected";
                    if (selected)
                    {
                        selection = document.Editor.GetSelection(new PromptSelectionOptions
                        {
                            MessageForAdding = "\nSelect water gravity pipes, structures or networks: ",
                            AllowDuplicates = false,
                            RejectObjectsFromNonCurrentSpace = true
                        });
                        if (selection.Status != PromptStatus.OK || selection.Value == null) return;
                    }
                }
                var selectedIds = selected
                    ? new HashSet<ObjectId>(selection.Value.GetObjectIds())
                    : null;
                foreach (ObjectId id in civil.GetPipeNetworkIds())
                {
                    CivilNetwork network = tr.GetObject(id, OpenMode.ForRead, false) as CivilNetwork;
                    if (network == null || network.IsReferenceObject) continue;
                    bool water = network.Name.IndexOf("WATER", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        network.Name.StartsWith("W-", StringComparison.OrdinalIgnoreCase);
                    if (!water) continue;
                    if (selectedIds != null &&
                        !selectedIds.Contains(id) &&
                        !network.GetPipeIds().Cast<ObjectId>().Any(selectedIds.Contains) &&
                        !network.GetStructureIds().Cast<ObjectId>().Any(selectedIds.Contains))
                        continue;
                    networks.Add(id);
                }
            }
            if (networks.Count == 0)
            {
                System.Windows.Forms.MessageBox.Show("No matching Water gravity networks were found. " +
                    "The network name must include WATER or start with W-.",
                    "CE Tools - Gravity Water Labels");
                return;
            }
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Gravity Water Pipe / Structure Labels",
                "Select Water plan label styles. These choices are applied only to matched Water gravity networks; Sewer settings are not changed.");
            IList<string> pipeNames = SewerNetworkLabelCommands.ReadPipeLabelStyleNames(document);
            IList<string> structureNames = SewerNetworkLabelCommands.ReadStructureLabelStyleNames(document);
            if (pipeNames.Count == 0 || structureNames.Count == 0)
            {
                System.Windows.Forms.MessageBox.Show("Import Civil 3D gravity Pipe and Structure plan label styles first.",
                    "CE Tools - Gravity Water Labels");
                return;
            }
            model.AddChoice("PipeStyle", "01 Water labels", "Water pipe label style", WaterLabelDefaults.Read(document.Database, "Pipe Label Style", pipeNames[0]),
                "Use a water-specific Civil 3D gravity pipe plan label style.", pipeNames);
            model.AddChoice("StructureStyle", "01 Water labels", "Water structure label style",
                WaterLabelDefaults.Read(document.Database, "Structure Label Style", structureNames[0]), "Use a water-specific gravity structure style.", structureNames);
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;
            SewerNetworkLabelResult result = SewerNetworkLabelCommands.EnsureWaterLabels(
                document, networks, model.Text("PipeStyle"), model.Text("StructureStyle"));
            document.Editor.Regen();
            System.Windows.Forms.MessageBox.Show(
                "Networks: " + networks.Count +
                "\nPipe labels added: " + result.PipeLabelsAdded +
                "\nStructure labels added: " + result.StructureLabelsAdded +
                "\nExisting labels: " + result.ExistingLabels +
                "\nSkipped: " + result.Skipped +
                (string.IsNullOrWhiteSpace(result.Warning) ? "" : "\n" + result.Warning),
                "CE Tools - Gravity Water Labels");
        }
    }
}

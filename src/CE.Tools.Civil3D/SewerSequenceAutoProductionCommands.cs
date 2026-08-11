using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.SewerSequenceAutoProductionCommands))]

namespace CETools.Civil3D
{
    public sealed class SewerSequenceAutoProductionCommands
    {
        [CommandMethod("CE_TOOLS", "CE_SEWSEQPRODUCTION", CommandFlags.Modal)]
        public void SequenceAndProduce()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Sewer Sequence + Production",
                "Sequence the sewer network and automatically continue into linked alignment production. Profile creation is optional because profile-view placement needs a drawing insertion point.");
            model.AddChoice(
                "Sequence",
                "01 Sequence",
                "Sequence mode",
                "Complete network",
                "Use the normal network sequence or the main-first workflow.",
                new[] { "Complete network", "Select main first" });
            model.AddChoice(
                "Profiles",
                "02 Production",
                "Create sewer profiles after alignments",
                "No",
                "When Yes, CE_SEWPROFILE starts only after the alignment command finishes and will then request its normal surface/insertion inputs.",
                new[] { "No", "Yes" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            string sequence = string.Equals(model.Text("Sequence"), "Select main first", StringComparison.OrdinalIgnoreCase)
                ? "CE_SEWSEQMAIN"
                : "CE_SEWSEQ";
            var commands = new List<string> { sequence, "CE_SEWALIGN" };
            if (string.Equals(model.Text("Profiles"), "Yes", StringComparison.OrdinalIgnoreCase))
                commands.Add("CE_SEWPROFILE");

            document.Editor.WriteMessage(
                "\nCE_SEWSEQPRODUCTION queued safely: sequence -> sewer alignments{0}.",
                commands.Count > 2 ? " -> profiles" : string.Empty);
            CeSequentialCommandRunner.Start(
                document,
                commands,
                "CE Tools - Sewer sequence + production");
        }
    }
}

using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.August13ProductionWorkflowCommands))]

namespace CETools.Civil3D
{
    public sealed class August13ProductionWorkflowCommands
    {
        [CommandMethod("CE_TOOLS", "CE_ROADPRODUCTIONWORKFLOW", CommandFlags.Modal)]
        public void RoadProductionWorkflow()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            DisciplineWorkflowDialogs.SelectAndRun(
                document,
                "CE Tools - Road Production",
                "Choose the Road production stage.",
                new List<DisciplineWorkflowAction>
                {
                    new DisciplineWorkflowAction("ROAD SETTINGS", "CE_PROJECTSTYLES", "Select Road project styles and standards.", "01 ROAD PRODUCTION"),
                    new DisciplineWorkflowAction("ROAD LAYOUT PRODUCTION", "CE_ROADLAYOUTTOOLS", "Prepare road centrelines, offsets, junction returns and setting-out geometry.", "01 ROAD PRODUCTION"),
                    new DisciplineWorkflowAction("ROAD DESIGN PRODUCTION", "CE_ROADDESIGNPRODUCTIONWORKFLOW", "Create alignments, profiles, profile sections, assemblies, corridors, junction design and production output.", "01 ROAD PRODUCTION")
                });
        }
    }
}

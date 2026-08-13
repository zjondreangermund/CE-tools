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
            Document document = ActiveDocument();
            if (document == null) return;
            DisciplineWorkflowDialogs.SelectAndRun(
                document,
                "CE Tools - Road Production",
                "Choose the Road production stage.",
                new List<DisciplineWorkflowAction>
                {
                    new DisciplineWorkflowAction("ROAD SETTINGS", "CE_ROADPROJECTSETTINGS", "Set Road project standards, widths, layers, styles, label sets, profile-view styles and band sets.", "01 ROAD PRODUCTION"),
                    new DisciplineWorkflowAction("ROAD LAYOUT PRODUCTION", "CE_ROADLAYOUTPRODUCTIONWORKFLOW", "Prepare road centrelines, offsets, junction returns and setting-out geometry.", "01 ROAD PRODUCTION"),
                    new DisciplineWorkflowAction("ROAD DESIGN PRODUCTION", "CE_ROADDESIGNPRODUCTIONWORKFLOW", "Create alignments, profiles, profile sections, assemblies, corridors, junction design and production output.", "01 ROAD PRODUCTION")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_PRODUCTIONWORKFLOWS", CommandFlags.Modal)]
        public void ProductionWorkflows()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            DisciplineWorkflowDialogs.SelectAndRun(
                document,
                "CE Tools - Discipline Production Workflows",
                "Open the guided production workflow for the active discipline.",
                new List<DisciplineWorkflowAction>
                {
                    new DisciplineWorkflowAction("ROAD PRODUCTION", "CE_ROADPRODUCTIONWORKFLOW", "Road Settings -> Road Layout Production -> Road Design Production.", "01 DESIGN"),
                    new DisciplineWorkflowAction("STORMWATER PRODUCTION", "CE_SWPRODUCTIONCENTRE", "Open the guided Stormwater production workflow.", "01 DESIGN"),
                    new DisciplineWorkflowAction("SEWER PRODUCTION", "CE_SEWERPRODUCTIONCENTRE", "Open the guided Sewer production workflow.", "01 DESIGN"),
                    new DisciplineWorkflowAction("WATER PRODUCTION", "CE_WATERPRODUCTIONCENTRE", "Open the guided Water production workflow.", "01 DESIGN"),
                    new DisciplineWorkflowAction("BULK WATER PRODUCTION", "CE_BULKWATERPRODUCTIONCENTRE", "Open the guided Bulk Water production workflow.", "01 DESIGN"),
                    new DisciplineWorkflowAction("PLATFORM PRODUCTION", "CE_PLATFORMPRODUCTIONCENTRE", "Open the guided Platform production workflow.", "02 SITE"),
                    new DisciplineWorkflowAction("PARKING PRODUCTION", "CE_PARKINGPRODUCTIONCENTRE", "Open the guided Parking production workflow.", "02 SITE"),
                    new DisciplineWorkflowAction("SURVEY PRODUCTION", "CE_SURVEYPRODUCTIONCENTRE", "Open the guided Survey production workflow.", "03 SUPPORT"),
                    new DisciplineWorkflowAction("FLOOD PRODUCTION", "CE_FLOODPRODUCTIONCENTRE", "Open the guided Flood production workflow.", "03 SUPPORT")
                });
        }

        private static Document ActiveDocument()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
        }
    }
}

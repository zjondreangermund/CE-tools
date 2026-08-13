using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.August13ProductionWorkflowCommands))]

namespace CETools.Civil3D
{
    public sealed class August13ProductionWorkflowCommands
    {
        private const int BellmouthsPerCrossJunction = 4;

        [CommandMethod("CE_TOOLS", "CE_ROADPRODUCTIONWORKFLOW", CommandFlags.Modal)]
        public void RoadProductionWorkflow()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            DisciplineWorkflowDialogs.SelectAndRun(
                document,
                "CE Tools - New Road Production Workflow",
                "Prepare -> Align -> Profile -> Split -> Assemble -> Corridor -> Junction -> Setting-Out -> Deliver. Cross-junction setting-out completes four bellmouths before moving to the next junction.",
                new List<DisciplineWorkflowAction>
                {
                    new DisciplineWorkflowAction("Road styles and settings", "CE_ROADSTYLES", "Choose Road-only Civil 3D styles, labels, profile-view styles and band sets.", "01 PREPARE"),
                    new DisciplineWorkflowAction("Polyline length selection tools", "CE_POLYLINESELECTIONTOOLS", "Select short or equal-length source polylines before production.", "01 PREPARE"),
                    new DisciplineWorkflowAction("Create road alignments", "CE_ROADALIGN", "Create sequential Road alignments from selected source polylines.", "02 ALIGN"),
                    new DisciplineWorkflowAction("Create NGL and final profiles", "CE_ROADPROFILEFULL", "Create Road surface/design profiles and profile views.", "03 PROFILE"),
                    new DisciplineWorkflowAction("Split road profile views", "CE_ROADPROFILEVIEWSPLIT", "Default 750.000 m sections: 0.000-750.000, 750.000-1500.000, etc.; limits remain editable.", "03 PROFILE"),
                    new DisciplineWorkflowAction("Finalize profile styles and bands", "CE_ROADPROFILEVIEWFINAL", "Apply Road profile-view styles and refresh band-set data after splitting.", "03 PROFILE"),
                    new DisciplineWorkflowAction("Create Road assembly", "CE_ASSEMBLYCREATE", "Create the assembly used for Road corridor regions.", "04 ASSEMBLY"),
                    new DisciplineWorkflowAction("Assembly tools", "CE_ASSEMBLYTOOLS", "Review and select project Road assemblies.", "04 ASSEMBLY"),
                    new DisciplineWorkflowAction("Create complete Road corridors", "CE_ROADCORRIDORFULL", "Create baselines, regions, targets, surfaces, boundaries and slope patterns.", "05 CORRIDOR"),
                    new DisciplineWorkflowAction("Road junction construction tools", "CE_ROADJUNCTIONCONSTRUCTIONTOOLS", "Create and finish T/cross junction geometry and construction output.", "06 JUNCTION"),
                    new DisciplineWorkflowAction("Cross-junction setting-out - four bellmouths", "CE_JUNCTIONSETTINGOUT4", "Sequence J1.1-J1.4, then J2.1-J2.4. Bellmouths per junction = " + BellmouthsPerCrossJunction.ToString(CultureInfo.InvariantCulture) + ".", "07 SETTING-OUT"),
                    new DisciplineWorkflowAction("Road construction BOQ", "CE_ROADBOQCONSTRUCTION", "Generate construction quantities/cost output.", "08 DELIVER"),
                    new DisciplineWorkflowAction("Road design report", "CE_REPORTROAD", "Generate the Road design report.", "08 DELIVER"),
                    new DisciplineWorkflowAction("Refresh linked model data", "CE_REFRESHALL", "Refresh Road annotations, schedules, quantities and outputs.", "09 REFRESH")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_PRODUCTIONWORKFLOWS", CommandFlags.Modal)]
        public void ProductionWorkflows()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            DisciplineWorkflowDialogs.SelectAndRun(
                document,
                "CE Tools - Discipline Production Workflows",
                "Open the guided workflow for the active discipline. Road uses the new staged production sequence; the other disciplines use their dedicated Production Centres.",
                new List<DisciplineWorkflowAction>
                {
                    new DisciplineWorkflowAction("Road - new production workflow", "CE_ROADPRODUCTIONWORKFLOW", "Road workflow with profile splitting, junction setting-out and delivery stages.", "01 DESIGN"),
                    new DisciplineWorkflowAction("Stormwater production", "CE_SWPRODUCTIONCENTRE", "Open the guided Stormwater production workflow.", "01 DESIGN"),
                    new DisciplineWorkflowAction("Sewer production", "CE_SEWERPRODUCTIONCENTRE", "Open the guided Sewer production workflow.", "01 DESIGN"),
                    new DisciplineWorkflowAction("Water production", "CE_WATERPRODUCTIONCENTRE", "Open the guided Water production workflow.", "01 DESIGN"),
                    new DisciplineWorkflowAction("Bulk Water production", "CE_BULKWATERPRODUCTIONCENTRE", "Open the guided Bulk Water production workflow.", "01 DESIGN"),
                    new DisciplineWorkflowAction("Platform production", "CE_PLATFORMPRODUCTIONCENTRE", "Open the guided Platform production workflow.", "02 SITE"),
                    new DisciplineWorkflowAction("Parking production", "CE_PARKINGPRODUCTIONCENTRE", "Open the guided Parking production workflow.", "02 SITE"),
                    new DisciplineWorkflowAction("Survey production", "CE_SURVEYPRODUCTIONCENTRE", "Open the guided Survey production workflow.", "03 SUPPORT"),
                    new DisciplineWorkflowAction("Flood production", "CE_FLOODPRODUCTIONCENTRE", "Open the guided Flood production workflow.", "03 SUPPORT")
                });
        }
    }
}

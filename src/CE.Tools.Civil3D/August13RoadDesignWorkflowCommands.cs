using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.August13RoadDesignWorkflowCommands))]

namespace CETools.Civil3D
{
    public sealed class August13RoadDesignWorkflowCommands
    {
        [CommandMethod("CE_TOOLS", "CE_ROADDESIGNPRODUCTIONWORKFLOW", CommandFlags.Modal)]
        public void RoadDesignProductionWorkflow()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            DisciplineWorkflowDialogs.SelectAndRun(
                document,
                "CE Tools - Road Design Production",
                "Alignments -> Profiles -> Profile Sections -> Assemblies -> Corridors -> Junction Design -> Production Output.",
                new List<DisciplineWorkflowAction>
                {
                    new DisciplineWorkflowAction("CE-Road Alignments", "CE_ROADALIGN", "Create sequential Civil 3D Road alignments.", "01 ALIGNMENTS"),
                    new DisciplineWorkflowAction("CE-NGL and Final Road Profiles", "CE_ROADPROFILEFULL", "Create Road NGL and final/design profiles and profile views.", "02 PROFILES"),
                    new DisciplineWorkflowAction("CE-Split Road Profile Views", "CE_ROADPROFILEVIEWSPLIT", "Split selected profile views into specified station sections; default 750.000.", "02 PROFILES"),
                    new DisciplineWorkflowAction("CE-Finalize Road Profile Styles/Bands", "CE_ROADPROFILEVIEWFINAL", "Refresh Road profile-view styles and band data.", "02 PROFILES"),
                    new DisciplineWorkflowAction("CE-Road Assemblies", "CE_ASSEMBLYTOOLS", "Create or review the Road assembly used by corridors.", "03 ASSEMBLIES"),
                    new DisciplineWorkflowAction("CE-Road Corridors", "CE_ROADCORRIDORFULL", "Create and complete Road corridors, targets, surfaces and boundaries.", "04 CORRIDORS"),
                    new DisciplineWorkflowAction("CE-Rebuild Corridors", "CE_CORREBUILD", "Rebuild selected Civil 3D corridors after design changes.", "04 CORRIDORS"),
                    new DisciplineWorkflowAction("CE-Road Junction Design", "CE_ROADJUNCTIONCONSTRUCTIONTOOLS", "Create and finish Road T- and cross-junction construction geometry.", "05 JUNCTION DESIGN"),
                    new DisciplineWorkflowAction("CE-Road Production Output", "CE_REPORTTOOLS", "Open Road reports, summary sheets and drawing-book production tools.", "06 OUTPUT")
                });
        }
    }
}

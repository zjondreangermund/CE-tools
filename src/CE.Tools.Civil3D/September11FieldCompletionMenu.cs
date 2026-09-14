using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.September11FieldCompletionMenu))]

namespace CETools.Civil3D
{
    /// <summary>
    /// One discoverable front door for the current field-completion workflows.
    /// Keep this list updated whenever a new field-completion command is added so
    /// the project can be followed from the ribbon/workflow centre without hunting
    /// for command names.
    /// </summary>
    public sealed class September11FieldCompletionMenu
    {
        [CommandMethod("CE_TOOLS", "CE_FIELDCOMPLETION", CommandFlags.Modal)]
        public void Open()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            DisciplineWorkflowDialogs.SelectAndRun(
                document,
                "CE Tools - Field Completion",
                "Current field workflows for manual dynamic refresh, road-centre cleanup, sewer recalculation/profile safety, annotation scale synchronisation, project style presets, Namibia coordinates, Google Earth linework and joined hatch outer boundaries.",
                new List<DisciplineWorkflowAction>
                {
                    new DisciplineWorkflowAction(
                        "Dynamic Refresh All",
                        "CE_DYNAMICREFRESHALL",
                        "Run the established explicit/manual CE Tools refresh. The same action is also available from AutoCAD's default right-click menu as CE Dynamic Refresh All.",
                        "00 General"),
                    new DisciplineWorkflowAction(
                        "Road Reserve Centres - Generate + Clean",
                        "CE_ROADRESERVECENTRELINES",
                        "Create the road-reserve centre segments, join connected runs and remove redundant straight vertices while retaining bend and T/X junction vertices.",
                        "01 Roads"),
                    new DisciplineWorkflowAction(
                        "Clean Existing Road Centres",
                        "CE_ROADCENTRECLEAN",
                        "Join selected/open road-centre polylines and clean straight intermediate vertices without removing junction vertices.",
                        "01 Roads"),
                    new DisciplineWorkflowAction(
                        "Sewer Surface / Rules Recalculation",
                        "CE_SEWRECALC",
                        "Re-link a gravity network to one surface, apply its rules, then optionally queue sewer profiles only after the network transaction commits.",
                        "02 Sewer"),
                    new DisciplineWorkflowAction(
                        "Synchronise Annotation Scale",
                        "CE_ANNOSCALESYNC",
                        "Apply the current drawing annotation scale to supported dimensions, text, MText and multileaders so annotation follows drawing-scale changes consistently.",
                        "03 Standards"),
                    new DisciplineWorkflowAction(
                        "Discipline Style Presets",
                        "CE_DISCIPLINESTYLEPRESETS",
                        "Choose and apply the existing CE Tools discipline style preset workflow for the current drawing.",
                        "03 Standards"),
                    new DisciplineWorkflowAction(
                        "Namibia Town -> Coordinate System",
                        "CE_SURVEYLOCATIONNAMIBIA",
                        "Choose the Namibia project town and assign its mapped installed LO coordinate system without transforming geometry.",
                        "04 Survey"),
                    new DisciplineWorkflowAction(
                        "Multiple Linework -> Google Earth",
                        "CE_GOOGLEEARTHLINEWORK",
                        "Select multiple separate open OR closed 2D/3D polylines, lines, curves or feature lines and display them as separate KML LineStrings in Google Earth.",
                        "04 Survey"),
                    new DisciplineWorkflowAction(
                        "Joined Hatch Outer Boundary",
                        "CE_HATCHOUTERBOUNDARY",
                        "Select attached hatches and create one outside closed polyline for the connected hatch set by removing shared internal hatch edges. Disconnected hatch clusters receive one perimeter each.",
                        "05 CAD")
                });
        }
    }
}

using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.September11FieldCompletionMenu))]

namespace CETools.Civil3D
{
    /// <summary>
    /// One discoverable front door for the September 11 field fixes.  The actions
    /// call the same standalone commands, so users can also pin them individually.
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
                "Final field workflows for road-centre cleanup, sewer recalculation/profile safety, Namibia town coordinate systems, Google Earth linework and hatch boundaries.",
                new List<DisciplineWorkflowAction>
                {
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
                        "Namibia Town -> Coordinate System",
                        "CE_SURVEYLOCATIONNAMIBIA",
                        "Choose the Namibia project town and assign its mapped installed LO coordinate system without transforming geometry.",
                        "03 Survey"),
                    new DisciplineWorkflowAction(
                        "Multiple Linework -> Google Earth",
                        "CE_GOOGLEEARTHLINEWORK",
                        "Select multiple separate open lines, polylines or feature lines and display them as separate KML LineStrings in Google Earth.",
                        "03 Survey"),
                    new DisciplineWorkflowAction(
                        "Separate Hatch Boundaries",
                        "CE_HATCHBOUNDARIES",
                        "Create one closed boundary per hatch loop so adjacent/touching hatches retain their shared boundary line.",
                        "04 CAD")
                });
        }
    }
}

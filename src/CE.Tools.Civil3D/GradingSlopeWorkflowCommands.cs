using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.GradingSlopeWorkflowCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// One front door for CE Tools grading design, slope annotation and grading review.
    /// Existing production engines remain the source of truth; this class only exposes
    /// the combined workflow and the two August 27 internal slope routines as commands.
    /// </summary>
    public sealed class GradingSlopeWorkflowCommands
    {
        [CommandMethod("CE_TOOLS", "CE_GRADINGSLOPETOOLS", CommandFlags.Modal)]
        public void GradingAndSlopeTools()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            DisciplineWorkflowDialogs.SelectAndRun(
                document,
                "GRADING & SLOPES",
                "Platform grading, cut/fill daylight, constant grades, dynamic slope/crossfall annotation and grading review in one workflow.",
                new List<DisciplineWorkflowAction>
                {
                    new DisciplineWorkflowAction(
                        "Platform Slopes / Levels",
                        "CE_PLATFORMSLOPE",
                        "Apply high-low slope, fixed slope or flatten/elevation options to selected platform feature lines.",
                        "01 GRADING"),
                    new DisciplineWorkflowAction(
                        "Grade Feature Lines to Surface",
                        "CE_PLATFORMGRADETOSURFACE",
                        "Create dynamic cut/fill daylight grading to a selected Civil 3D surface with separate cut and fill H:V slopes.",
                        "01 GRADING"),
                    new DisciplineWorkflowAction(
                        "Constant Grade Between Endpoints",
                        "CE_PLATFORMCONSTANTGRADE",
                        "Grade multiple open feature lines between their existing fixed endpoint elevations.",
                        "01 GRADING"),
                    new DisciplineWorkflowAction(
                        "Platform Site / Surface / Infill",
                        "CE_PLATFORMSURFACE",
                        "Create/assign the platform site, surface and native grading infill where supported.",
                        "01 GRADING"),
                    new DisciplineWorkflowAction(
                        "Dynamic Feature-Line Slope Arrows",
                        "CE_FEATURELINESLOPEARROWS",
                        "Create linked leader arrows and slope percentages along multiple Civil 3D feature lines.",
                        "02 SLOPE ANNOTATION"),
                    new DisciplineWorkflowAction(
                        "Crossfall Between Two Feature Lines",
                        "CE_FEATURELINECROSSFALLARROWS",
                        "Create linked crossfall arrows and slope percentages between two feature lines.",
                        "02 SLOPE ANNOTATION"),
                    new DisciplineWorkflowAction(
                        "Dynamic Surface Slope Arrows",
                        "CE_SURFACESLOPEARROWS",
                        "Create linked slope arrows and values sampled from a Civil 3D surface.",
                        "02 SLOPE ANNOTATION"),
                    new DisciplineWorkflowAction(
                        "Refresh Feature-Line Slope Arrows",
                        "CE_SLOPEARROWSREFRESH",
                        "Refresh linked feature-line slope annotations.",
                        "02 SLOPE ANNOTATION"),
                    new DisciplineWorkflowAction(
                        "Refresh Dynamic Crossfalls",
                        "CE_DYNAMICSLOPESREFRESH",
                        "Refresh linked crossfall/dynamic slope leader sets.",
                        "02 SLOPE ANNOTATION"),
                    new DisciplineWorkflowAction(
                        "Grading Diagnostics",
                        "CE_GRADINGDIAGNOSTICS",
                        "Open the low-slope, low-point and grading review workflow.",
                        "03 REVIEW"),
                    new DisciplineWorkflowAction(
                        "Find Low Slopes",
                        "CE_LOWSLOPE",
                        "Highlight selected geometry below a chosen minimum slope.",
                        "03 REVIEW"),
                    new DisciplineWorkflowAction(
                        "Find Low Points",
                        "CE_LOWPOINTS",
                        "Find and mark candidate grading low points.",
                        "03 REVIEW"),
                    new DisciplineWorkflowAction(
                        "Clear Grading Review",
                        "CE_GRADINGREVIEWCLEAR",
                        "Remove CE-generated grading diagnostic graphics.",
                        "03 REVIEW"),
                    new DisciplineWorkflowAction(
                        "Platform Cut / Fill",
                        "CE_PLATFORMCUTFILL",
                        "Calculate linked existing-ground versus platform design cut/fill quantities.",
                        "04 QUANTITIES")
                });
        }

    }
}

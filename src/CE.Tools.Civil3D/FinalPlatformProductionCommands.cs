using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.FinalPlatformProductionCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Approved 45-stage FINAL PLATFORM PRODUCTION workflow. Every stage is
    /// launched individually so the drawing can be reviewed before proceeding.
    /// Existing engineering command implementations are reused unchanged.
    /// </summary>
    public sealed class FinalPlatformProductionCommands
    {
        [CommandMethod("CE_TOOLS", "CE_FINALPLATFORMPRODUCTION", CommandFlags.Modal)]
        public void Open()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            var actions = new List<DisciplineWorkflowAction>
            {
                new DisciplineWorkflowAction("01. CE-Platform Design Standards", "CE_PLATFORMSTYLES", "Choose feature-line, grading, surface and annotation styles.", "01 Platform Sequence"),
                new DisciplineWorkflowAction("02. Create Feature Lines", "CE_FLCREATE", "Create feature lines from selected objects and source geometry.", "02 Platform Sequence"),
                new DisciplineWorkflowAction("03. CE-Elevation from Surface - Multiple Feature Lines", "CE_PLATFORMDRAPEMULTI", "Drape multiple feature lines to the selected Civil 3D surface.", "03 Platform Sequence"),
                new DisciplineWorkflowAction("04. Platform Slopes / Levels", "CE_PLATFORMSLOPE", "Assign platform slopes and elevations to selected feature lines.", "04 Platform Sequence"),
                new DisciplineWorkflowAction("05. Grade Feature Lines to Surface", "CE_PLATFORMGRADETOSURFACE", "Create dynamic cut/fill daylight grading from selected feature lines.", "05 Platform Sequence"),
                new DisciplineWorkflowAction("06. Constant Grade Between Endpoints", "CE_PLATFORMCONSTANTGRADE", "Grade multiple open feature lines between their endpoint elevations.", "06 Platform Sequence"),
                new DisciplineWorkflowAction("07. Platform Site / Surface / Infill", "CE_PLATFORMSURFACE", "Create or assign the platform site, surface and grading infill.", "07 Platform Sequence"),
                new DisciplineWorkflowAction("08. Platform Closed Feature-Line Infill + Daylight", "CE_PLATFORMINFILL", "Create infill for closed and multiple feature-line boundaries.", "08 Platform Sequence"),
                new DisciplineWorkflowAction("09. Junction Closed Feature-Line Infill", "CE_JUNCTIONINFILL", "Create road/junction infill from selected closed feature lines.", "09 Platform Sequence"),
                new DisciplineWorkflowAction("10. Dynamic Feature-Line Slope Arrows", "CE_FEATURELINESLOPEARROWS", "Create linked slope arrows and percentage slope annotations.", "10 Platform Sequence"),
                new DisciplineWorkflowAction("11. Crossfall Between Two Feature Lines", "CE_FEATURELINECROSSFALLARROWS", "Create linked crossfall arrows and percentages between feature lines.", "11 Platform Sequence"),
                new DisciplineWorkflowAction("12. Surface Slope Arrows", "CE_SURFACESLOPEARROWS", "Create slope arrows and percentage annotations from a Civil 3D surface.", "12 Platform Sequence"),
                new DisciplineWorkflowAction("13. Refresh Feature-Line Slope Arrows", "CE_SLOPEARROWSREFRESH", "Refresh linked feature-line slope arrows and their annotations.", "13 Platform Sequence"),
                new DisciplineWorkflowAction("14. Refresh Dynamic Crossfalls", "CE_DYNAMICSLOPESREFRESH", "Refresh linked crossfall and dynamic slope arrows.", "14 Platform Sequence"),
                new DisciplineWorkflowAction("15. Grading Diagnostics", "CE_GRADINGDIAGNOSTICS", "Review low slopes, low points and grading diagnostics.", "15 Platform Sequence"),
                new DisciplineWorkflowAction("16. Find Low Slopes", "CE_LOWSLOPE", "Highlight selected grading geometry below the chosen minimum slope.", "16 Platform Sequence"),
                new DisciplineWorkflowAction("17. Find Low Points", "CE_LOWPOINTS", "Find and review critical low points on grading surfaces.", "17 Platform Sequence"),
                new DisciplineWorkflowAction("18. Clear Grading Review", "CE_GRADINGREVIEWCLEAR", "Remove CE-generated grading review graphics.", "18 Platform Sequence"),
                new DisciplineWorkflowAction("19. Platform Cut / Fill", "CE_PLATFORMCUTFILL", "Calculate linked earthwork cut and fill against design surfaces.", "19 Platform Sequence"),
                new DisciplineWorkflowAction("20. Feature-Line Colour and Site Assignment", "CE_FLAPPEARANCE", "Assign feature-line colour, layer and site settings.", "20 Platform Sequence"),
                new DisciplineWorkflowAction("21. Link Existing Relative Feature Lines", "CE_FLRELLINKEXISTING", "Link existing relative feature lines to their source geometry.", "21 Platform Sequence"),
                new DisciplineWorkflowAction("22. Safe Stepped Offsets - Multiple Feature Lines", "CE_FLSTEPSSAFE", "Create linked stepped offsets from multiple feature lines.", "22 Platform Sequence"),
                new DisciplineWorkflowAction("23. Dynamic Surface Link - Existing Feature Lines", "CE_FLSURFACELINKEXISTING", "Drape multiple existing feature lines dynamically onto a selected surface.", "23 Platform Sequence"),
                new DisciplineWorkflowAction("24. Rebuild Surface Batch Control", "CE_SURFACEBATCHCONTROL", "Control automatic rebuilding for multiple selected surfaces.", "24 Platform Sequence"),
                new DisciplineWorkflowAction("25. Surface Style - Multiple", "CE_SURFACESTYLEMULTI", "Apply a surface style to multiple selected surfaces.", "25 Platform Sequence"),
                new DisciplineWorkflowAction("26. Platform Step Offsets", "CE_PLATFORMSTEPOFFSETS", "Create multi-step stepped platform offsets.", "26 Platform Sequence"),
                new DisciplineWorkflowAction("27. Slope Annotations", "CE_SLOPEANNOTATIONS", "Apply and manage platform slope annotations.", "27 Platform Sequence"),
                new DisciplineWorkflowAction("28. Grid Difference", "CE_GRIDDIFFERENCE", "Compare grid elevations and identify differences.", "28 Platform Sequence"),
                new DisciplineWorkflowAction("29. Multi Fillet", "CE_MULTIFILLET", "Apply multiple fillets to selected geometry.", "29 Platform Sequence"),
                new DisciplineWorkflowAction("30. Multi Stretch Feature Lines", "CE_MULTISTRETCHFL", "Stretch multiple feature lines while preserving geometry.", "30 Platform Sequence"),
                new DisciplineWorkflowAction("31. Close Open Multiple Feature Lines", "CE_CLOSEOPENMULTI", "Close selected open feature-line boundaries.", "31 Platform Sequence"),
                new DisciplineWorkflowAction("32. Site Grid Presentation", "CE_SITEGRIDPRESENTATION", "Prepare site-grid presentation graphics.", "32 Platform Sequence"),
                new DisciplineWorkflowAction("33. Joined Hatch Outer Boundary", "CE_HATCHOUTERBOUNDARY", "Create outer boundaries from joined hatch geometry.", "33 Platform Sequence"),
                new DisciplineWorkflowAction("34. Merge Road / Grading Outputs", "CE_ROADSURFACEMERGE", "Merge CE Top All and other grading outputs into a combined surface.", "34 Platform Sequence"),
                new DisciplineWorkflowAction("35. Add Direction Arrows", "CE_PLDIRADD", "Add linked direction arrows to selected polylines.", "35 Platform Sequence"),
                new DisciplineWorkflowAction("36. Refresh Arrows", "CE_PLDIRREFRESH", "Refresh linked direction arrows after geometry changes.", "36 Platform Sequence"),
                new DisciplineWorkflowAction("37. Reverse Polylines", "CE_PLDIRREVERSE", "Reverse selected polylines and refresh their arrows.", "37 Platform Sequence"),
                new DisciplineWorkflowAction("38. Clear Arrows", "CE_PLDIRCLEAR", "Remove CE linked direction arrows.", "38 Platform Sequence"),
                new DisciplineWorkflowAction("39. Vertex Setting-Out", "CE_VERTEXSETTINGOUT", "Create vertex setting-out outputs for multiple feature lines.", "39 Platform Sequence"),
                new DisciplineWorkflowAction("40. Grid Setting-Out", "CE_GRIDSETTINGOUTDYNAMIC", "Create grid/perimeter setting-out inside selected boundaries.", "40 Platform Sequence"),
                new DisciplineWorkflowAction("41. Refresh Vertex Setting-Out", "CE_VERTEXSETTINGOUTREFRESH", "Refresh linked vertex setting-out outputs.", "41 Platform Sequence"),
                new DisciplineWorkflowAction("42. Drawings / Sections", "CE_PLATFORMDRAWINGS", "Create platform layouts and section drawings.", "42 Platform Sequence"),
                new DisciplineWorkflowAction("43. Lock Viewports - Current / All Layouts", "CE_VIEWPORTLOCKALL", "Lock model viewports in the current or all layouts.", "43 Platform Sequence"),
                new DisciplineWorkflowAction("44. Unlock Viewports - Current / All Layouts", "CE_VIEWPORTUNLOCKALL", "Unlock model viewports in the current or all layouts.", "44 Platform Sequence"),
                new DisciplineWorkflowAction("45. Regenerate Viewports - Current / All Layouts", "CE_VIEWPORTREGENALL", "Regenerate paper-space model viewports.", "45 Platform Sequence")
            };
            DisciplineWorkflowDialogs.SelectAndRun(
                document,
                "FINAL PLATFORM PRODUCTION",
                "Approved platform production sequence; choose steps individually for drawing review.",
                actions);
        }

        [CommandMethod("CE_TOOLS", "CE_VIEWPORTLOCKALL", CommandFlags.Modal | CommandFlags.Redraw)]
        public void LockViewports()
        {
            October03ViewportPlotProfileCommands.SetViewportLock(true);
        }

        [CommandMethod("CE_TOOLS", "CE_VIEWPORTUNLOCKALL", CommandFlags.Modal | CommandFlags.Redraw)]
        public void UnlockViewports()
        {
            October03ViewportPlotProfileCommands.SetViewportLock(false);
        }
    }
}

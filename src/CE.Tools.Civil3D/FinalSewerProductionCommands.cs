using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.FinalSewerProductionCommands))]
namespace CETools.Civil3D
{
    /// <summary>Ordered Final Sewer Production menu. The items invoke existing real commands.</summary>
    public sealed class FinalSewerProductionCommands
    {
        [CommandMethod("CE_TOOLS", "CE_FINALSEWERPRODUCTION", CommandFlags.Modal)]
        public void Open()
        {
            Document doc = AcApplication.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var actions = new List<DisciplineWorkflowAction>
            {
                new DisciplineWorkflowAction("01. CE-Project Configuration", "CE_SEWSETTINGS", "Open sewer-specific project settings, gravity-network parts and saved drafting defaults.", "01 Sewer Sequence"),
                new DisciplineWorkflowAction("02. CE-Sewer Design Standards", "CE_SEWERSTYLES", "Activate sewer network, structure, pipe and profile design styles.", "02 Sewer Sequence"),
                new DisciplineWorkflowAction("03. CE-Road Reserve Sewer Route", "CE_SEWERLAYOUTROADRESERVE", "Generate road-reserve sewer route geometry using the existing dedicated sewer-road-reserve command.", "03 Sewer Sequence"),
                new DisciplineWorkflowAction("04. MAPCLEAN - Drawing Geometry Cleanup", "MAPCLEAN", "Open AutoCAD Map 3D drawing cleanup to review and correct duplicate, overshoot and undershoot geometry before sewer network generation. Requires Map 3D functionality.", "04 Sewer Sequence"),
                new DisciplineWorkflowAction("05. CE-Sewer Geometry Cleanup", "CE_ROADCENTRECLEANSTRICT", "Apply the existing strict road centreline geometry cleanup command to prepared sewer route polylines. Review the effect on sewer geometry before continuing.", "05 Sewer Sequence"),
                new DisciplineWorkflowAction("06. CE-Sewer Network Generation", "CE_SEWERNETWORKMULTI", "Create connected sewer pipe networks from the prepared route polylines.", "06 Sewer Sequence"),
                new DisciplineWorkflowAction("07. CE-Sewer Network Connection", "CE_SEWCONNECTPARTS", "Connect pipe endpoints to compatible gravity-network structures.", "07 Sewer Sequence"),
                new DisciplineWorkflowAction("08. CE-Sewer Main Sequencing", "CE_SEWSEQMAIN", "Choose the main sewer route, then sequence structures and pipes toward the downstream outlet.", "08 Sewer Sequence"),
                new DisciplineWorkflowAction("09. CE-Sewer Alignment Generation", "CE_SEWALIGN", "Generate one Civil 3D reference alignment per sequenced sewer branch.", "09 Sewer Sequence"),
                new DisciplineWorkflowAction("10. CE-Sewer Alignment Start Correction", "CE_SEWALIGNSTARTFIX", "Repair existing sewer alignment start stations so branch chainages begin correctly.", "10 Sewer Sequence"),
                new DisciplineWorkflowAction("11. CE-Sewer Reference Alignment Assignment", "CE_PIPEALIGNMENTMULTI", "Assign branch alignments to their matching sewer pipes and structures.", "11 Sewer Sequence"),
                new DisciplineWorkflowAction("12. CE-Sewer Pipe and Structure Label Management", "CE_SEWLABELS", "Create or refresh pipe and structure labels without duplicating existing Civil 3D labels.", "12 Sewer Sequence"),
                new DisciplineWorkflowAction("13. CE-Sewer Branch Name Placement", "CE_SEWBRANCHNAMES", "Place numbered sewer branch names in plan above their corresponding pipes.", "13 Sewer Sequence"),
                new DisciplineWorkflowAction("14. CE-Sewer Surface and Gravity Slope Management", "CE_SEWLINKSURFACE", "Link sewer parts to the selected surface and apply the configured gravity slopes and cover rules.", "14 Sewer Sequence"),
                new DisciplineWorkflowAction("15. CE-Manhole Sump Elevation Management", "CE_SEWERSUMPFIX", "Review and set manhole sump levels against connected sewer invert elevations.", "15 Sewer Sequence"),
                new DisciplineWorkflowAction("16. CE-Manhole Surface Rim Elevation Management", "CE_SEWSURFACERIMS", "Update manhole rim elevations from the chosen ground surface.", "16 Sewer Sequence"),
                new DisciplineWorkflowAction("17. CE-Sewer Profile Generation", "CE_SEWPROFILE", "Create sewer profiles and profile views for linked branch alignments.", "17 Sewer Sequence"),
                new DisciplineWorkflowAction("18. CE-Sewer Profile View Standards", "CE_PROFILEVIEWSTYLEMULTI", "Assign the chosen Civil 3D profile view style to multiple sewer profile views.", "18 Sewer Sequence"),
                new DisciplineWorkflowAction("19. CE-Sewer Profile Band Management", "CE_PROFILEVIEWDATASOURCES", "Assign or repair profile-view band sets and network data sources.", "19 Sewer Sequence"),
                new DisciplineWorkflowAction("20. CE-Sewer Profile View Property Matching", "CE_PROFILEVIEWMATCH", "Match multiple sewer profile views to the reference view properties.", "20 Sewer Sequence"),
                new DisciplineWorkflowAction("21. CE-Sewer Profile View Fitting", "CE_PROFILEVIEWFITALL", "Fit sewer profile views to their network and surface elevation ranges.", "21 Sewer Sequence"),
                new DisciplineWorkflowAction("22. CE-Sewer Profile View Arrangement", "CE_PROFILEVIEWARRANGE", "Arrange sewer profile views into aligned drawing rows and columns.", "22 Sewer Sequence"),
                new DisciplineWorkflowAction("23. CE-Sewer Profile View Splitting", "CE_ROADPROFILEVIEWSPLIT", "Split long profile views where needed for the sewer long-section sheets.", "23 Sewer Sequence"),
                new DisciplineWorkflowAction("24. CE-Sewer Pipe and Structure Replacement", "CE_SEWPARTSWAPMULTI", "Replace selected pipes or structures with approved parts while preserving connections.", "24 Sewer Sequence"),
                new DisciplineWorkflowAction("25. CE-Sewer Profile Label Management", "CE_SEWPROFILELABELSTYLESMULTI", "Apply approved sewer pipe and structure profile label styles to selected views.", "25 Sewer Sequence"),
                new DisciplineWorkflowAction("26. CE-Manhole Pipe Connection Display", "CE_SEWINCOMINGPIPES", "Display connected incoming and outgoing sewer pipes at selected manholes.", "26 Sewer Sequence"),
                new DisciplineWorkflowAction("27. CE-Incoming Pipe Name and Depth Management", "CE_SEWINCOMINGLABELSMULTI", "Label incoming pipe names and depths relative to the connected manhole rim.", "27 Sewer Sequence"),
                new DisciplineWorkflowAction("28. CE-Sewer Plan and Profile Navigation", "CE_SEWLOCATEPLAN", "Locate selected sewer parts between plan and profile views.", "28 Sewer Sequence"),
                new DisciplineWorkflowAction("29. CE-Sewer Network Integrity Validation", "CE_SEWAUDITLIMITS", "Audit sewer connections, slopes, invert levels and structure cover rules.", "29 Sewer Sequence"),
                new DisciplineWorkflowAction("30. CE-Sewer Viewport Locking", "CE_VIEWPORTLOCKALL", "Lock selected or all sewer drawing layout viewports.", "30 Sewer Sequence"),
                new DisciplineWorkflowAction("31. CE-Sewer Viewport Unlocking", "CE_VIEWPORTUNLOCKALL", "Unlock selected sewer drawing viewports for further editing.", "31 Sewer Sequence"),
                new DisciplineWorkflowAction("32. CE-Sewer Viewport Regeneration", "CE_VIEWPORTREGENALL", "Regenerate sewer layout viewports after model and sheet updates.", "32 Sewer Sequence"),
                new DisciplineWorkflowAction("33. CE-Sewer Sheet Generation", "CE_SEWERSHEETS", "Generate sewer long-section and plan/profile drawing sheets.", "33 Sewer Sequence"),
                new DisciplineWorkflowAction("34. CE-Sewer Design Data Export", "CE_SEWERDESIGNDATAEXPORT", "Export sewer pipe and manhole design data by numbered branch to Excel.", "34 Sewer Sequence"),
                new DisciplineWorkflowAction("35. CE-Sewer Construction BOQ", "CE_BOQSEWER", "Calculate sewer earthworks, excavation, pipe and structure construction quantities.", "35 Sewer Sequence")
            };
            DisciplineWorkflowDialogs.SelectAndRun(doc, "FINAL SEWER PRODUCTION",
                "Approved sewer production sequence; choose steps individually for drawing review.", actions);
        }
    }
}

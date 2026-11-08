using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.FinalWaterProductionCommands))]
namespace CETools.Civil3D
{
    /// <summary>
    /// Approved 31-step Water Production order. Each action runs separately,
    /// allowing a drawing review before continuing to the next stage.
    /// </summary>
    public sealed class FinalWaterProductionCommands
    {
        [CommandMethod("CE_TOOLS", "CE_FINALWATERPRODUCTION", CommandFlags.Modal)]
        public void Open()
        {
            Document doc = AcApplication.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var actions = new List<DisciplineWorkflowAction>
            {
                new DisciplineWorkflowAction("01. CE-Water Settings", "CE_WATERSETTINGS", "Choose Water network parts, standards and saved drafting defaults.", "01 Water Sequence"),
                new DisciplineWorkflowAction("02. CE-Water Design Standards", "CE_WATERSTYLES", "Activate Water pipe, structure, fitting, appurtenance and annotation styles.", "02 Water Sequence"),
                new DisciplineWorkflowAction("03. CE-Water Route from Road Reserve", "CE_WATERROUTERESERVE", "Create preliminary Water routes from road-reserve/source geometry.", "03 Water Sequence"),
                new DisciplineWorkflowAction("04. CE-Break Water Source Routes at Crossings / T-Junctions", "CE_WATERROUTEBREAK", "Split Water routes at crossing and T-junction geometry.", "04 Water Sequence"),
                new DisciplineWorkflowAction("05. CE-Water Network Generation", "CE_WATERNETWORKMULTI", "Create the Water gravity network from the selected routes.", "05 Water Sequence"),
                new DisciplineWorkflowAction("06. CE-Water Alignment Label Management", "CE_WATERALIGNLABELS", "Apply alignment label sets and drafting standards.", "06 Water Sequence"),
                new DisciplineWorkflowAction("07. CE-Water Branch Names", "CE_WATERBRANCHNAMES", "Place and refresh numbered Water branch alignment names.", "07 Water Sequence"),
                new DisciplineWorkflowAction("08. CE-Number Water Pipes / Structures by Branch", "CE_WATERPARTNUMBER", "Preview or apply branch-specific Water pipe/structure numbers.", "08 Water Sequence"),
                new DisciplineWorkflowAction("09. CE-Water Main / Branch Sequencing", "CE_WATERSEQ", "Sequence the Water mains/branches and preserve source identity.", "09 Water Sequence"),
                new DisciplineWorkflowAction("10. CE-Water Alignment Generation", "CE_WATERALIGN", "Create or refresh tagged Water branch alignments.", "10 Water Sequence"),
                new DisciplineWorkflowAction("11. CE-Water Profile Generation", "CE_WATERPROFILE", "Create Water existing-ground profiles and profile views.", "11 Water Sequence"),
                new DisciplineWorkflowAction("12. CE-Link Water Network Parts to Surface", "CE_WATERLINKSURFACE", "Link Water pipes and structures to a chosen design surface.", "12 Water Sequence"),
                new DisciplineWorkflowAction("13. CE-Draw Water Network Parts in Matching Profile Views", "CE_WATERPROFILEPARTSMULTI", "Display only Water pipes and structures belonging to each matching profile alignment.", "13 Water Sequence"),
                new DisciplineWorkflowAction("14. CE-Water Profile View Standards", "CE_WATERPROFILEVIEWSTANDARDS", "Apply Water profile-view design styles.", "14 Water Sequence"),
                new DisciplineWorkflowAction("15. CE-Water Pipe / Structure Profile Label Styles", "CE_WATERPROFILELABELSTYLES", "Apply native Water pipe and structure profile label styles.", "15 Water Sequence"),
                new DisciplineWorkflowAction("16. CE-Water Profile Band Management", "CE_WATERPROFILEBANDMANAGEMENT", "Assign or repair Water profile-view band and data-source settings.", "16 Water Sequence"),
                new DisciplineWorkflowAction("17. CE-Match Water Profile View Properties", "CE_PROFILEVIEWMATCH", "Match profile-view properties from a reference Water view.", "17 Water Sequence"),
                new DisciplineWorkflowAction("18. CE-Fit All Water Profile Views", "CE_PROFILEVIEWFITALL", "Fit Water profile-view elevation and horizontal ranges.", "18 Water Sequence"),
                new DisciplineWorkflowAction("19. CE-Arrange Multiple Water Profile Views", "CE_PROFILEVIEWARRANGE", "Arrange Water profile views with saved spacing.", "19 Water Sequence"),
                new DisciplineWorkflowAction("20. CE-Split Water Profile Views", "CE_WATERPROFILESPLIT", "Split long Water profile views for sheets.", "20 Water Sequence"),
                new DisciplineWorkflowAction("21. CE-Connect Water Network Parts", "CE_WATERCONNECTPARTS", "Connect compatible Water pipes and structures.", "21 Water Sequence"),
                new DisciplineWorkflowAction("22. CE-Swap Multiple Water Pipes / Structures", "CE_WATERPARTSWAPMULTI", "Replace selected Water pipes or structures.", "22 Water Sequence"),
                new DisciplineWorkflowAction("23. CE-Locate Water Pipe / Structure Plan to Profile", "CE_WATERLOCATEPLAN", "Locate Water network objects between plan and profile.", "23 Water Sequence"),
                new DisciplineWorkflowAction("24. CE-Water Network Integrity Audit", "CE_WATERINTEGRITYAUDIT", "Audit Water networks for connection, geometry and surface issues.", "24 Water Sequence"),
                new DisciplineWorkflowAction("25. CE-Lock Water Viewports - Current / All Layouts", "CE_WATERVIEWPORTLOCK", "Lock Water layout viewports for sheet production.", "25 Water Sequence"),
                new DisciplineWorkflowAction("26. CE-Unlock Water Viewports - Current / All Layouts", "CE_WATERVIEWPORTUNLOCK", "Unlock Water layout viewports for edits.", "26 Water Sequence"),
                new DisciplineWorkflowAction("27. CE-Regenerate Water Viewports - Current / All Layouts", "CE_WATERVIEWPORTREGEN", "Regenerate Water viewports after changes.", "27 Water Sequence"),
                new DisciplineWorkflowAction("28. CE-Water Profile Sheets", "CE_WATERPROFILESHEETS", "Prepare Water plan/profile sheets.", "28 Water Sequence"),
                new DisciplineWorkflowAction("29. CE-Water Design Data Export", "CE_WATERDESIGNDATAEXPORT", "Export Water design data.", "29 Water Sequence"),
                new DisciplineWorkflowAction("30. CE-Water Excavation", "CE_WATEREXCAVATION", "Prepare Water excavation schedules and trench quantities.", "30 Water Sequence"),
                new DisciplineWorkflowAction("31. CE-Water Bill of Quantities", "CE_BOQWATER", "Calculate Water construction bill of quantities.", "31 Water Sequence")
            };
            DisciplineWorkflowDialogs.SelectAndRun(doc, "FINAL WATER PRODUCTION",
                "Approved Water production sequence; choose steps individually for drawing review.", actions);
        }
    }
}

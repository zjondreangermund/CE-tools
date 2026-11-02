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
                new DisciplineWorkflowAction("01. CE-Project Configuration", "CE_SEWSETTINGS", "Run CE_SEWSETTINGS on the sewer design and review the updated Civil 3D drawing before continuing.", "01 Sewer Sequence"),
                new DisciplineWorkflowAction("02. CE-Sewer Design Standards", "CE_SEWERSTYLES", "Run CE_SEWERSTYLES on the sewer design and review the updated Civil 3D drawing before continuing.", "02 Sewer Sequence"),
                new DisciplineWorkflowAction("03. CE-Sewer Reserve Layout Generation", "CE_SEWERLAYOUTROADRESERVE", "Run CE_SEWERLAYOUTROADRESERVE on the sewer design and review the updated Civil 3D drawing before continuing.", "03 Sewer Sequence"),
                new DisciplineWorkflowAction("04. CE-Sewer Route Geometry Preparation", "CE_PLBREAKJUNCTIONS", "Run CE_PLBREAKJUNCTIONS on the sewer design and review the updated Civil 3D drawing before continuing.", "04 Sewer Sequence"),
                new DisciplineWorkflowAction("05. CE-Sewer Network Generation", "CE_SEWERNETWORKMULTI", "Run CE_SEWERNETWORKMULTI on the sewer design and review the updated Civil 3D drawing before continuing.", "05 Sewer Sequence"),
                new DisciplineWorkflowAction("06. CE-Sewer Network Connection", "CE_SEWCONNECTPARTS", "Run CE_SEWCONNECTPARTS on the sewer design and review the updated Civil 3D drawing before continuing.", "06 Sewer Sequence"),
                new DisciplineWorkflowAction("07. CE-Sewer Main Sequencing", "CE_SEWSEQMAIN", "Run CE_SEWSEQMAIN on the sewer design and review the updated Civil 3D drawing before continuing.", "07 Sewer Sequence"),
                new DisciplineWorkflowAction("08. CE-Sewer Alignment Generation", "CE_SEWALIGN", "Run CE_SEWALIGN on the sewer design and review the updated Civil 3D drawing before continuing.", "08 Sewer Sequence"),
                new DisciplineWorkflowAction("09. CE-Sewer Alignment Start Correction", "CE_SEWALIGNSTARTFIX", "Run CE_SEWALIGNSTARTFIX on the sewer design and review the updated Civil 3D drawing before continuing.", "09 Sewer Sequence"),
                new DisciplineWorkflowAction("10. CE-Sewer Reference Alignment Assignment", "CE_PIPEALIGNMENTMULTI", "Run CE_PIPEALIGNMENTMULTI on the sewer design and review the updated Civil 3D drawing before continuing.", "10 Sewer Sequence"),
                new DisciplineWorkflowAction("11. CE-Sewer Pipe and Structure Label Management", "CE_SEWLABELS", "Run CE_SEWLABELS on the sewer design and review the updated Civil 3D drawing before continuing.", "11 Sewer Sequence"),
                new DisciplineWorkflowAction("12. CE-Sewer Branch Name Placement", "CE_SEWBRANCHNAMES", "Run CE_SEWBRANCHNAMES on the sewer design and review the updated Civil 3D drawing before continuing.", "12 Sewer Sequence"),
                new DisciplineWorkflowAction("13. CE-Sewer Surface and Gravity Slope Management", "CE_SEWLINKSURFACE", "Run CE_SEWLINKSURFACE on the sewer design and review the updated Civil 3D drawing before continuing.", "13 Sewer Sequence"),
                new DisciplineWorkflowAction("14. CE-Manhole Sump Elevation Management", "CE_SEWERSUMPFIX", "Run CE_SEWERSUMPFIX on the sewer design and review the updated Civil 3D drawing before continuing.", "14 Sewer Sequence"),
                new DisciplineWorkflowAction("15. CE-Manhole Surface Rim Elevation Management", "CE_SEWSURFACERIMS", "Run CE_SEWSURFACERIMS on the sewer design and review the updated Civil 3D drawing before continuing.", "15 Sewer Sequence"),
                new DisciplineWorkflowAction("16. CE-Sewer Profile Generation", "CE_SEWPROFILE", "Run CE_SEWPROFILE on the sewer design and review the updated Civil 3D drawing before continuing.", "16 Sewer Sequence"),
                new DisciplineWorkflowAction("17. CE-Sewer Profile View Standards", "CE_PROFILEVIEWSTYLEMULTI", "Run CE_PROFILEVIEWSTYLEMULTI on the sewer design and review the updated Civil 3D drawing before continuing.", "17 Sewer Sequence"),
                new DisciplineWorkflowAction("18. CE-Sewer Profile Band Management", "CE_PROFILEVIEWDATASOURCES", "Run CE_PROFILEVIEWDATASOURCES on the sewer design and review the updated Civil 3D drawing before continuing.", "18 Sewer Sequence"),
                new DisciplineWorkflowAction("19. CE-Sewer Profile View Property Matching", "CE_PROFILEVIEWMATCH", "Run CE_PROFILEVIEWMATCH on the sewer design and review the updated Civil 3D drawing before continuing.", "19 Sewer Sequence"),
                new DisciplineWorkflowAction("20. CE-Sewer Profile View Fitting", "CE_PROFILEVIEWFITALL", "Run CE_PROFILEVIEWFITALL on the sewer design and review the updated Civil 3D drawing before continuing.", "20 Sewer Sequence"),
                new DisciplineWorkflowAction("21. CE-Sewer Profile View Arrangement", "CE_PROFILEVIEWARRANGE", "Run CE_PROFILEVIEWARRANGE on the sewer design and review the updated Civil 3D drawing before continuing.", "21 Sewer Sequence"),
                new DisciplineWorkflowAction("22. CE-Sewer Profile View Splitting", "CE_ROADPROFILEVIEWSPLIT", "Run CE_ROADPROFILEVIEWSPLIT on the sewer design and review the updated Civil 3D drawing before continuing.", "22 Sewer Sequence"),
                new DisciplineWorkflowAction("23. CE-Sewer Pipe and Structure Replacement", "CE_SEWPARTSWAPMULTI", "Run CE_SEWPARTSWAPMULTI on the sewer design and review the updated Civil 3D drawing before continuing.", "23 Sewer Sequence"),
                new DisciplineWorkflowAction("24. CE-Sewer Profile Label Management", "CE_SEWPROFILELABELSTYLESMULTI", "Run CE_SEWPROFILELABELSTYLESMULTI on the sewer design and review the updated Civil 3D drawing before continuing.", "24 Sewer Sequence"),
                new DisciplineWorkflowAction("25. CE-Manhole Pipe Connection Display", "CE_SEWINCOMINGPIPES", "Run CE_SEWINCOMINGPIPES on the sewer design and review the updated Civil 3D drawing before continuing.", "25 Sewer Sequence"),
                new DisciplineWorkflowAction("26. CE-Incoming Pipe Name and Depth Management", "CE_SEWINCOMINGLABELSMULTI", "Run CE_SEWINCOMINGLABELSMULTI on the sewer design and review the updated Civil 3D drawing before continuing.", "26 Sewer Sequence"),
                new DisciplineWorkflowAction("27. CE-Sewer Plan and Profile Navigation", "CE_SEWLOCATEPLAN", "Run CE_SEWLOCATEPLAN on the sewer design and review the updated Civil 3D drawing before continuing.", "27 Sewer Sequence"),
                new DisciplineWorkflowAction("28. CE-Sewer Network Integrity Validation", "CE_SEWAUDITLIMITS", "Run CE_SEWAUDITLIMITS on the sewer design and review the updated Civil 3D drawing before continuing.", "28 Sewer Sequence"),
                new DisciplineWorkflowAction("29. CE-Sewer Viewport Locking", "CE_VIEWPORTLOCKALL", "Run CE_VIEWPORTLOCKALL on the sewer design and review the updated Civil 3D drawing before continuing.", "29 Sewer Sequence"),
                new DisciplineWorkflowAction("30. CE-Sewer Viewport Unlocking", "CE_VIEWPORTUNLOCKALL", "Run CE_VIEWPORTUNLOCKALL on the sewer design and review the updated Civil 3D drawing before continuing.", "30 Sewer Sequence"),
                new DisciplineWorkflowAction("31. CE-Sewer Viewport Regeneration", "CE_VIEWPORTREGENALL", "Run CE_VIEWPORTREGENALL on the sewer design and review the updated Civil 3D drawing before continuing.", "31 Sewer Sequence"),
                new DisciplineWorkflowAction("32. CE-Sewer Sheet Generation", "CE_SEWERSHEETS", "Run CE_SEWERSHEETS on the sewer design and review the updated Civil 3D drawing before continuing.", "32 Sewer Sequence"),
                new DisciplineWorkflowAction("33. CE-Sewer Design Data Export", "CE_SEWERDESIGNDATAEXPORT", "Run CE_SEWERDESIGNDATAEXPORT on the sewer design and review the updated Civil 3D drawing before continuing.", "33 Sewer Sequence"),
                new DisciplineWorkflowAction("34. CE-Sewer Construction BOQ", "CE_BOQSEWER", "Run CE_BOQSEWER on the sewer design and review the updated Civil 3D drawing before continuing.", "34 Sewer Sequence")
            };
            DisciplineWorkflowDialogs.SelectAndRun(doc, "FINAL SEWER PRODUCTION",
                "Ordered workflow from SEWER SEQUENCE.docx; each command is run separately for drawing review.", actions);
        }
    }
}

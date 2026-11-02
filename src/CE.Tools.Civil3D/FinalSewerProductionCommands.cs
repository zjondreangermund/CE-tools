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
                new DisciplineWorkflowAction("01. CE-Project Configuration", "CE_SEWSETTINGS", "Run existing CE command CE_SEWSETTINGS (source: SEWER SEQUENCE.docx).", "01 Sewer Sequence"),
                new DisciplineWorkflowAction("02. CE-Sewer Design Standards", "CE_SEWERSTYLES", "Run existing CE command CE_SEWERSTYLES (source: SEWER SEQUENCE.docx).", "02 Sewer Sequence"),
                new DisciplineWorkflowAction("03. CE-Sewer Reserve Layout Generation", "CE_SEWERLAYOUTROADRESERVE", "Run existing CE command CE_SEWERLAYOUTROADRESERVE (source: SEWER SEQUENCE.docx).", "03 Sewer Sequence"),
                new DisciplineWorkflowAction("04. CE-Sewer Route Geometry Preparation", "CE_PLBREAKJUNCTIONS", "Run existing CE command CE_PLBREAKJUNCTIONS (source: SEWER SEQUENCE.docx).", "04 Sewer Sequence"),
                new DisciplineWorkflowAction("05. CE-Sewer Network Generation", "CE_SEWERNETWORKMULTI", "Run existing CE command CE_SEWERNETWORKMULTI (source: SEWER SEQUENCE.docx).", "05 Sewer Sequence"),
                new DisciplineWorkflowAction("06. CE-Sewer Network Connection", "CE_SEWCONNECTPARTS", "Run existing CE command CE_SEWCONNECTPARTS (source: SEWER SEQUENCE.docx).", "06 Sewer Sequence"),
                new DisciplineWorkflowAction("07. CE-Sewer Main Sequencing", "CE_SEWSEQMAIN", "Run existing CE command CE_SEWSEQMAIN (source: SEWER SEQUENCE.docx).", "07 Sewer Sequence"),
                new DisciplineWorkflowAction("08. CE-Sewer Alignment Generation", "CE_SEWALIGN", "Run existing CE command CE_SEWALIGN (source: SEWER SEQUENCE.docx).", "08 Sewer Sequence"),
                new DisciplineWorkflowAction("09. CE-Sewer Alignment Start Correction", "CE_SEWALIGNSTARTFIX", "Run existing CE command CE_SEWALIGNSTARTFIX (source: SEWER SEQUENCE.docx).", "09 Sewer Sequence"),
                new DisciplineWorkflowAction("10. CE-Sewer Reference Alignment Assignment", "CE_PIPEALIGNMENTMULTI", "Run existing CE command CE_PIPEALIGNMENTMULTI (source: SEWER SEQUENCE.docx).", "10 Sewer Sequence"),
                new DisciplineWorkflowAction("11. CE-Sewer Pipe and Structure Label Management", "CE_SEWLABELS", "Run existing CE command CE_SEWLABELS (source: SEWER SEQUENCE.docx).", "11 Sewer Sequence"),
                new DisciplineWorkflowAction("12. CE-Sewer Branch Name Placement", "CE_SEWBRANCHNAMES", "Run existing CE command CE_SEWBRANCHNAMES (source: SEWER SEQUENCE.docx).", "12 Sewer Sequence"),
                new DisciplineWorkflowAction("13. CE-Sewer Surface and Gravity Slope Management", "CE_SEWLINKSURFACE", "Run existing CE command CE_SEWLINKSURFACE (source: SEWER SEQUENCE.docx).", "13 Sewer Sequence"),
                new DisciplineWorkflowAction("14. CE-Manhole Sump Elevation Management", "CE_SEWERSUMPFIX", "Run existing CE command CE_SEWERSUMPFIX (source: SEWER SEQUENCE.docx).", "14 Sewer Sequence"),
                new DisciplineWorkflowAction("15. CE-Manhole Surface Rim Elevation Management", "CE_SEWSURFACERIMS", "Run existing CE command CE_SEWSURFACERIMS (source: SEWER SEQUENCE.docx).", "15 Sewer Sequence"),
                new DisciplineWorkflowAction("16. CE-Sewer Profile Generation", "CE_SEWPROFILE", "Run existing CE command CE_SEWPROFILE (source: SEWER SEQUENCE.docx).", "16 Sewer Sequence"),
                new DisciplineWorkflowAction("17. CE-Sewer Profile View Standards", "CE_PROFILEVIEWSTYLEMULTI", "Run existing CE command CE_PROFILEVIEWSTYLEMULTI (source: SEWER SEQUENCE.docx).", "17 Sewer Sequence"),
                new DisciplineWorkflowAction("18. CE-Sewer Profile Band Management", "CE_PROFILEVIEWDATASOURCES", "Run existing CE command CE_PROFILEVIEWDATASOURCES (source: SEWER SEQUENCE.docx).", "18 Sewer Sequence"),
                new DisciplineWorkflowAction("19. CE-Sewer Profile View Property Matching", "CE_PROFILEVIEWMATCH", "Run existing CE command CE_PROFILEVIEWMATCH (source: SEWER SEQUENCE.docx).", "19 Sewer Sequence"),
                new DisciplineWorkflowAction("20. CE-Sewer Profile View Fitting", "CE_PROFILEVIEWFITALL", "Run existing CE command CE_PROFILEVIEWFITALL (source: SEWER SEQUENCE.docx).", "20 Sewer Sequence"),
                new DisciplineWorkflowAction("21. CE-Sewer Profile View Arrangement", "CE_PROFILEVIEWARRANGE", "Run existing CE command CE_PROFILEVIEWARRANGE (source: SEWER SEQUENCE.docx).", "21 Sewer Sequence"),
                new DisciplineWorkflowAction("22. CE-Sewer Profile View Splitting", "CE_ROADPROFILEVIEWSPLIT", "Run existing CE command CE_ROADPROFILEVIEWSPLIT (source: SEWER SEQUENCE.docx).", "22 Sewer Sequence"),
                new DisciplineWorkflowAction("23. CE-Sewer Pipe and Structure Replacement", "CE_SEWPARTSWAPMULTI", "Run existing CE command CE_SEWPARTSWAPMULTI (source: SEWER SEQUENCE.docx).", "23 Sewer Sequence"),
                new DisciplineWorkflowAction("24. CE-Sewer Profile Label Management", "CE_SEWPROFILELABELSTYLESMULTI", "Run existing CE command CE_SEWPROFILELABELSTYLESMULTI (source: SEWER SEQUENCE.docx).", "24 Sewer Sequence"),
                new DisciplineWorkflowAction("25. CE-Manhole Pipe Connection Display", "CE_SEWINCOMINGPIPES", "Run existing CE command CE_SEWINCOMINGPIPES (source: SEWER SEQUENCE.docx).", "25 Sewer Sequence"),
                new DisciplineWorkflowAction("26. CE-Incoming Pipe Name and Depth Management", "CE_SEWINCOMINGLABELSMULTI", "Run existing CE command CE_SEWINCOMINGLABELSMULTI (source: SEWER SEQUENCE.docx).", "26 Sewer Sequence"),
                new DisciplineWorkflowAction("27. CE-Sewer Plan and Profile Navigation", "CE_SEWLOCATEPLAN", "Run existing CE command CE_SEWLOCATEPLAN (source: SEWER SEQUENCE.docx).", "27 Sewer Sequence"),
                new DisciplineWorkflowAction("28. CE-Sewer Network Integrity Validation", "CE_SEWAUDITLIMITS", "Run existing CE command CE_SEWAUDITLIMITS (source: SEWER SEQUENCE.docx).", "28 Sewer Sequence"),
                new DisciplineWorkflowAction("29. CE-Sewer Viewport Locking", "CE_VIEWPORTLOCKALL", "Run existing CE command CE_VIEWPORTLOCKALL (source: SEWER SEQUENCE.docx).", "29 Sewer Sequence"),
                new DisciplineWorkflowAction("30. CE-Sewer Viewport Unlocking", "CE_VIEWPORTUNLOCKALL", "Run existing CE command CE_VIEWPORTUNLOCKALL (source: SEWER SEQUENCE.docx).", "30 Sewer Sequence"),
                new DisciplineWorkflowAction("31. CE-Sewer Viewport Regeneration", "CE_VIEWPORTREGENALL", "Run existing CE command CE_VIEWPORTREGENALL (source: SEWER SEQUENCE.docx).", "31 Sewer Sequence"),
                new DisciplineWorkflowAction("32. CE-Sewer Sheet Generation", "CE_SEWERSHEETS", "Run existing CE command CE_SEWERSHEETS (source: SEWER SEQUENCE.docx).", "32 Sewer Sequence"),
                new DisciplineWorkflowAction("33. CE-Sewer Design Data Export", "CE_SEWERDESIGNDATAEXPORT", "Run existing CE command CE_SEWERDESIGNDATAEXPORT (source: SEWER SEQUENCE.docx).", "33 Sewer Sequence"),
                new DisciplineWorkflowAction("34. CE-Sewer Construction BOQ", "CE_BOQSEWER", "Run existing CE command CE_BOQSEWER (source: SEWER SEQUENCE.docx).", "34 Sewer Sequence")
            };
            DisciplineWorkflowDialogs.SelectAndRun(doc, "FINAL SEWER PRODUCTION",
                "Ordered workflow from SEWER SEQUENCE.docx; each command is run separately for drawing review.", actions);
        }
    }
}

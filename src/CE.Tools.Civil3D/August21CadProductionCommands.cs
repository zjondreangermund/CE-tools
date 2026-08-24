using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.August21CadProductionCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// CAD-production page requested from the August 21 field workflow sheet.
    /// The page intentionally reuses the existing CE commands instead of
    /// duplicating their implementations.
    /// </summary>
    public sealed class August21CadProductionCommands
    {
        [CommandMethod("CE_TOOLS", "CE_CADPRODUCTION", CommandFlags.Modal)]
        public void CadProduction()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            DisciplineWorkflowDialogs.SelectAndRun(
                document,
                "CE-CAD PRODUCTION",
                "General CAD production, annotation, background preparation, cleanup, XREF, hatch and supplementary field production on one page.",
                new List<DisciplineWorkflowAction>
                {
                    A("CE-Bellmouth Densifier", "CE_BMINVERT", "Densify bellmouth/road geometry.", "01 GENERAL PRODUCTION"),
                    A("CE-Fast Block Edit", "CE_BLOCKEDITFAST", "Open the fast block-edit workflow.", "01 GENERAL PRODUCTION"),
                    A("CE-Select Polylines Shorter Than Length", "CE_SELECTPOLYSHORTER", "Select current-space polylines shorter than a specified length.", "01 GENERAL PRODUCTION"),
                    A("CE-Select Polylines With Same Length", "CE_SELECTPOLYSAMELENGTH", "Select polylines matching a reference length.", "01 GENERAL PRODUCTION"),
                    A("CE-Total Area", "CE_TAREA", "Calculate total selected area.", "01 GENERAL PRODUCTION"),
                    A("CE-Total Length", "CE_TLENGTH", "Calculate total selected length.", "01 GENERAL PRODUCTION"),
                    A("CE-Change Objects to Colour 250", "CE_COLOR250", "Change supported ordinary AutoCAD objects to colour 250.", "01 GENERAL PRODUCTION"),
                    A("CE-Export Civil Design to CAD Copy", "CE_EXPORTCADCOPY", "Create a CAD-copy export of the Civil design.", "01 GENERAL PRODUCTION"),
                    A("CE-Multiple Boundary Trim / Extend", "CE_BOUNDARYEDITTOOLS", "Open the multiple-boundary trim/extend tools.", "01 GENERAL PRODUCTION"),
                    A("CE-Viewport Tools", "CE_VIEWPORTTOOLS", "Open viewport production tools.", "01 GENERAL PRODUCTION"),
                    A("CE-Enable Full Undo Recording", "CE_UNDOSETTINGS", "Configure CE full undo recording.", "01 GENERAL PRODUCTION"),
                    A("CE-Redo One Step", "CE_REDO", "Redo one CE operation step.", "01 GENERAL PRODUCTION"),
                    A("CE-Undo One Step", "CE_UNDO", "Undo one CE operation step.", "01 GENERAL PRODUCTION"),
                    A("CE-Break routes at crossings / T-junctions", "CE_PLBREAKJUNCTIONS", "Safely split selected route polylines at true internal crossings and T-junctions.", "01 GENERAL PRODUCTION"),
                    A("CE-Convert Curves and Polylines", "CE_CURVECONVERT", "Convert supported curves and polylines while keeping originals by default.", "01 GENERAL PRODUCTION"),
                    A("CE-Select Same Length", "CE_SELECTPOLYSAMELENGTH", "Select objects with the same polyline length.", "01 GENERAL PRODUCTION"),
                    A("CE-Select Shorter", "CE_SELECTPOLYSHORTER", "Select shorter polylines.", "01 GENERAL PRODUCTION"),
                    A("CE-Swap XY", "CE_SETTINGOUTSWAPXY", "Swap setting-out X/Y presentation.", "01 GENERAL PRODUCTION"),
                    A("CE-Plot Polyline Boundary In Google Earth", "CE_SURVEYGOOGLEEARTHBOUNDARY", "Plot a selected polyline boundary for Google Earth.", "01 GENERAL PRODUCTION"),
                    A("CE-Extend Inside", "CE_EXTENDINSIDEMULTI", "Extend selected targets to the inside boundary.", "01 GENERAL PRODUCTION"),
                    A("CE-Extend Outside", "CE_EXTENDOUTSIDEMULTI", "Extend selected targets to the outside boundary.", "01 GENERAL PRODUCTION"),
                    A("CE-Trim Delete Inside", "CE_TRIMDELETEINSIDEMULTI", "Trim and delete geometry inside selected boundaries.", "01 GENERAL PRODUCTION"),
                    A("CE-Trim Delete Outside", "CE_TRIMDELETEOUTSIDEMULTI", "Trim and delete geometry outside selected boundaries.", "01 GENERAL PRODUCTION"),
                    A("CE-Trim Inside", "CE_TRIMINSIDEMULTI", "Trim geometry inside selected boundaries.", "01 GENERAL PRODUCTION"),
                    A("CE-Trim Outside", "CE_TRIMOUTSIDEMULTI", "Trim geometry outside selected boundaries.", "01 GENERAL PRODUCTION"),
                    A("CE-Vertex Setting-Out", "CE_VERTEXSETTINGOUT", "Linked COGO/MText/MLeader vertex setting-out.", "01 GENERAL PRODUCTION"),
                    A("CE-Grid Setting-Out - Multiple Polylines", "CE_GRIDSETTINGOUT", "Linked grid/vertex setting-out across multiple polylines with continuous numbering.", "01 GENERAL PRODUCTION"),

                    A("CE-Annotation Presentation", "CE_ROUTEANNOTATIONSTYLE", "Open annotation presentation controls.", "02 ANNOTATION PRODUCTION"),
                    A("CE-Freeze Dimensions", "CE_BGFREEZEDIMS", "Freeze dimensions for background preparation.", "02 ANNOTATION PRODUCTION"),
                    A("CE-Multiple Dimensions", "CE_MULTIDIM", "Create multiple dimensions, including open-polyline chain dimensions.", "02 ANNOTATION PRODUCTION"),
                    A("CE-Annotation Draw Order", "CE_ANNOTATIONDRAWORDER", "Correct annotation draw order.", "02 ANNOTATION PRODUCTION"),
                    A("CE-Annotation Settings", "CE_ANNOTSETTINGS", "Configure CE annotation settings.", "02 ANNOTATION PRODUCTION"),
                    A("CE-MLeader Text Above Leader", "CE_MLEADERTEXTABOVE", "Place MLeader text above its leader.", "02 ANNOTATION PRODUCTION"),

                    A("CE-Background Preparation Tools", "CE_BACKGROUNDPREPTOOLS", "Open the complete background-preparation tool set.", "03 BACKGROUND PRODUCTION"),
                    A("CE-Split Background", "CE_XREFSPLIT", "Split background/XREF content for production.", "03 BACKGROUND PRODUCTION"),

                    A("CE-Cleanup Window", "CE_DRAWCLEAN", "Open the drawing cleanup manager.", "04 CLEANUP PRODUCTION"),

                    A("CE-Create Coordinated Master XREF Drawing", "CE_MASTERXREF", "Create a coordinated master XREF drawing.", "05 XREF PRODUCTION"),
                    A("CE-Split Xrefs by Discipline", "CE_XREFDISCIPLINESPLIT", "Split XREFs by discipline.", "05 XREF PRODUCTION"),
                    A("CE-Xref Project Tools", "CE_XREFPROJECTTOOLS", "Open project XREF tools.", "05 XREF PRODUCTION"),

                    A("CE-Hatch Tools", "CE_HATCHTOOLS", "Open hatch production tools.", "06 HATCH PRODUCTION"),
                    A("CE-Match Hatch Settings", "CE_HATCHMATCH", "Match hatch settings between objects.", "06 HATCH PRODUCTION"),
                    A("CE-Create Transparent Hatches", "CE_HATCHCREATE", "Create transparent production hatches.", "06 HATCH PRODUCTION"),
                    A("CE-Edit Hatch Settings", "CE_HATCHEDIT", "Edit selected hatch settings.", "06 HATCH PRODUCTION"),
                    A("CE-Send Hatches Behind Linework", "CE_HATCHBACK", "Send hatches behind linework.", "06 HATCH PRODUCTION"),

                    A("CE-CAD Supplementary", "CE_CADSUPPLEMENTARY", "Open the recently added field geometry, slope, Site Grid, road, sewer and platform utilities.", "07 CE-CAD SUPPLEMENTARY"),
                    A("CE-Close Multiple Open Polylines / Feature Lines", "CE_CLOSEOPENMULTI", "Close multiple selected open polylines or feature lines.", "07 CE-CAD SUPPLEMENTARY"),
                    A("CE-Stretch Multiple Feature Lines", "CE_MULTISTRETCHFL", "Stretch multiple selected feature lines with native grip-aware STRETCH.", "07 CE-CAD SUPPLEMENTARY"),
                    A("CE-Offset / Construction Offset", "CE_SURVEYCONSTRUCTIONOFFSET", "Normal or per-segment construction offsets with zero-fillet joins.", "07 CE-CAD SUPPLEMENTARY"),
                    A("CE-Centre Construction Lines", "CE_SURVEYMIDCONSTRUCTION", "Create centre construction lines within a maximum separation.", "07 CE-CAD SUPPLEMENTARY"),
                    A("CE-Feature-Line Dynamic Slope Arrows", "CE_FEATURELINESLOPEARROWS", "Linked slope arrows and values for multiple feature lines.", "07 CE-CAD SUPPLEMENTARY"),
                    A("CE-Site Grid Presentation", "CE_SITEGRIDPRESENTATION", "Colour and annotative text controls for linked Site Grids.", "07 CE-CAD SUPPLEMENTARY"),
                    A("CE-Road Side Hatch", "CE_ROADHATCHSIDES", "Hatch road left/right/both sides from polylines or alignments.", "07 CE-CAD SUPPLEMENTARY")
                });
        }

        private static DisciplineWorkflowAction A(
            string title,
            string command,
            string description,
            string group)
        {
            return new DisciplineWorkflowAction(title, command, description, group);
        }
    }
}

using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.August14ProductionCentres))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Upgraded field production centres. Existing August 11 production commands
    /// remain available for compatibility; these centres place the August 14
    /// additions in the discipline workflow positions requested during testing.
    /// </summary>
    public sealed class August14ProductionCentres
    {
        [CommandMethod("CE_TOOLS", "CE_PRODUCTIONV3", CommandFlags.Modal)]
        public void ProductionV3()
        {
            Run(
                "CE-PRODUCTION CENTRE V3",
                "Updated production centres with the August 14 field-review additions. Roads/Sewer/Platforms/Flood continue through their existing production centres; Project, Survey, Stormwater, Water, Bulk Water and Parking use the upgraded centres below.",
                new List<DisciplineWorkflowAction>
                {
                    A("PROJECT PRODUCTION", "CE_PROJECTPRODUCTIONV2", "Project setup with last-saved or standard Blank information.", "01 Disciplines"),
                    A("SURVEY PRODUCTION", "CE_SURVEYPRODUCTIONV2", "Survey preparation with surface import/create, point-extent border and comparison/export tools.", "01 Disciplines"),
                    A("PLATFORM PRODUCTION", "CE_PLATFORMPRODUCTIONCENTRE", "Existing linked platform production workflow.", "01 Disciplines"),
                    A("ROAD PRODUCTION", "CE_ROADPRODUCTIONV2", "Road Settings → Layout Production → Design Production.", "01 Disciplines"),
                    A("STORMWATER PRODUCTION", "CE_SWPRODUCTIONV2", "Styles/network/alignment/profile/labels plus safe alignment fallback.", "01 Disciplines"),
                    A("SEWER PRODUCTION", "CE_SEWERPRODUCTIONCENTRE", "Existing sewer production workflow plus shared multi-network tools.", "01 Disciplines"),
                    A("WATER PRODUCTION", "CE_WATERPRODUCTIONV2", "Water production plus labels and safe profile fallback.", "01 Disciplines"),
                    A("BULK WATER PRODUCTION", "CE_BULKWATERPRODUCTIONV2", "Bulk-water route/network/profile/labels/BOQ production.", "01 Disciplines"),
                    A("PARKING PRODUCTION", "CE_PARKINGPRODUCTIONV2", "Parking layout plus linked/annotative bay numbering.", "01 Disciplines"),
                    A("FLOOD PRODUCTION", "CE_FLOODPRODUCTIONCENTRE", "Existing flood production workflow.", "01 Disciplines"),
                    A("FULL AUGUST 14 UPGRADE CENTRE", "CE_AUG14UPGRADES", "Direct access to all new field-review commands.", "02 Utilities")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_PROJECTPRODUCTIONV2", CommandFlags.Modal)]
        public void ProjectV2()
        {
            Run(
                "CE-PROJECT PRODUCTION V2",
                "Project information → styles/standards → coordination → linked delivery.",
                new List<DisciplineWorkflowAction>
                {
                    A("CE-Project Setup - Last Saved or Blank", "CE_PROJECTSETUPCHOICE", "Open the form using the drawing's last saved values or a standard Blank project form.", "01 SETTINGS"),
                    A("CE-Project Style Centre", "CE_PROJECTSTYLES", "Select Civil 3D project styles and import a source style drawing when required.", "01 SETTINGS"),
                    A("CE-Discipline Style Presets", "CE_DISCIPLINESTYLEPRESETS", "Save/apply independent style choices for every discipline.", "01 SETTINGS"),
                    A("CE-Project Coordination", "CE_PROJECTCOORDINATION", "Coordinate project source drawings and location.", "02 PREPARE"),
                    A("CE-Standards", "CE_STANDARDS", "Select/store project standards.", "02 PREPARE"),
                    A("CE-Project Information", "CE_PROJECTINFO", "Review the linked project metadata.", "05 COMPLETE"),
                    A("CE-Drawing Register", "CE_DRAWINGREGISTEREDIT", "Review drawing titles/numbers/revisions.", "06 DELIVER")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_SURVEYPRODUCTIONV2", CommandFlags.Modal)]
        public void SurveyV2()
        {
            Run(
                "CE-SURVEY PRODUCTION V2",
                "Coordinate system → PREPARE surfaces → correction/review → setting-out → comparison/export.",
                new List<DisciplineWorkflowAction>
                {
                    A("CE-Survey Location / Coordinate System", "CE_SURVEYLOCATION", "Set project location and Namibia LO coordinate system.", "01 SETTINGS"),
                    A("CE-Project Style Centre", "CE_PROJECTSTYLES", "Import/select point and surface styles.", "01 SETTINGS"),
                    A("CE-Surface Production", "CE_SURVEYSURFACEPRODUCTION", "Below PREPARE: add surface from file/objects, choose style and create point-extent border.", "02 PREPARE"),
                    A("CE-LandXML Import / Export", "CE_LANDXMLTOOLS", "Civil survey interchange.", "02 PREPARE"),
                    A("CE-Surface Tools", "CE_SFTOOLS", "Review and report current surfaces.", "03 CREATE"),
                    A("CE-Surface Correction / Review", "CE_SURFCTOOLS", "Audit/create reversible corrected surface copies.", "04 DESIGN"),
                    A("CE-Vertex Setting-Out", "CE_VERTEXSETTINGOUT", "Linked COGO/MText/MLeader setting-out.", "05 COMPLETE"),
                    A("CE-Base / Comparison Surface Table", "CE_SURFACECOMPARETABLE", "Select base/comparison surfaces and points for a linked table.", "05 COMPLETE"),
                    A("CE-Refresh Comparison Tables", "CE_COORDMULTISURFACEREFRESH", "Refresh linked surface comparison tables.", "05 COMPLETE"),
                    A("CE-Export Table to CSV/Excel", "CE_TABLEEXPORTCSV", "Export selected table to an Excel-compatible CSV.", "06 DELIVER"),
                    A("CE-Correct Table Column Spacing", "CE_TABLECOLUMNSPACE", "Apply consistent table column widths.", "06 DELIVER")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_SWPRODUCTIONV2", CommandFlags.Modal)]
        public void StormwaterV2()
        {
            Activate("Stormwater");
            Run(
                "CE-STORMWATER PRODUCTION V2",
                "Styles → multi-network sources → sequence → alignments/profiles → flow/labels → BOQ/report.",
                new List<DisciplineWorkflowAction>
                {
                    A("CE-Import Missing Project Styles", "CE_PROJECTSTYLEIMPORT", "Import a source Civil 3D style drawing when SW dropdowns are empty.", "01 SETTINGS"),
                    A("CE-Stormwater Settings", "CE_SWSETTINGS", "Choose alignment/profile/profile-view/band styles and layers.", "01 SETTINGS"),
                    A("CE-Multiple Networks from Polylines", "CE_NETWORKFROMPOLYLINESBATCH", "Create multiple Stormwater network sources in the same drawing.", "02 PREPARE"),
                    A("CE-Reset Stale Network Batch", "CE_NETWORKBATCHRESET", "Clear a stuck 'batch already running' state without deleting networks.", "02 PREPARE"),
                    A("CE-Sequence Stormwater Network", "CE_SWSEQ", "Build main/branch order.", "03 CREATE"),
                    A("CE-Create / Refresh SW Alignments", "CE_SWALIGN", "Normal linked stormwater alignment creation.", "04 DESIGN"),
                    A("CE-Safe SW Alignment Fallback", "CE_SWALIGNSAFE", "Clean duplicate/zero-length source vertices and create direct Civil alignments when the normal command rejects a source.", "04 DESIGN"),
                    A("CE-Stormwater Profiles", "CE_SWPROFILE", "Create linked stormwater profile views.", "05 COMPLETE"),
                    A("CE-Check / Correct Flow to Outlet", "CE_FLOWTOOUTLET", "Reverse selected source curves to low end or a selected outlet.", "05 COMPLETE"),
                    A("CE-Stormwater Pipe / Structure Labels", "CE_SWLABELS", "Create missing plan labels using Stormwater-selected styles.", "05 COMPLETE"),
                    A("CE-Stormwater BOQ", "CE_BOQSTORMWATER", "Create linked Stormwater quantities.", "06 DELIVER"),
                    A("CE-Stormwater Report", "CE_REPORTSTORMWATER", "Generate Stormwater report/drawing handoff.", "06 DELIVER")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_WATERPRODUCTIONV2", CommandFlags.Modal)]
        public void WaterV2()
        {
            Activate("Water");
            Run(
                "CE-WATER PRODUCTION V2",
                "Styles → multi-network sources → route sequence/alignment → profiles → labels/assets → BOQ/report.",
                new List<DisciplineWorkflowAction>
                {
                    A("CE-Import Missing Project Styles", "CE_PROJECTSTYLEIMPORT", "Import a source Civil 3D style drawing when Water dropdowns are empty.", "01 SETTINGS"),
                    A("CE-Water Settings", "CE_WATERSETTINGS", "Choose alignment/profile/profile-view/band styles and asset spacing.", "01 SETTINGS"),
                    A("CE-Multiple Networks from Polylines", "CE_NETWORKFROMPOLYLINESBATCH", "Create multiple Water network sources in the same drawing.", "02 PREPARE"),
                    A("CE-Reset Stale Network Batch", "CE_NETWORKBATCHRESET", "Clear a stuck CE network batch state.", "02 PREPARE"),
                    A("CE-Sequence Water Routes", "CE_WATERSEQ", "Store W-MAIN and branch route order.", "03 CREATE"),
                    A("CE-Water Alignments", "CE_WATERALIGN", "Create/refresh linked Water alignments.", "04 DESIGN"),
                    A("CE-Water Profiles", "CE_WATERPROFILE", "Normal linked Water profiles with pressure-part projection where supported.", "05 COMPLETE"),
                    A("CE-Safe Water Profile Fallback", "CE_WATERPROFILESAFE", "Direct surface profile/profile-view creation with no pressure projection/binding when the normal host path throws fatal/internal errors.", "05 COMPLETE"),
                    A("CE-Water Pressure-Part Labels", "CE_WATERLABELS", "Label pressure pipes, fittings and appurtenances.", "05 COMPLETE"),
                    A("CE-Water Asset Review Markers", "CE_WATERPLACE", "Place linked valve/hydrant review markers.", "05 COMPLETE"),
                    A("CE-Water BOQ", "CE_BOQWATER", "Create linked Water quantities.", "06 DELIVER"),
                    A("CE-Water Report", "CE_REPORTWATER", "Generate Water report/drawing handoff.", "06 DELIVER")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_BULKWATERPRODUCTIONV2", CommandFlags.Modal)]
        public void BulkWaterV2()
        {
            Activate("Bulk Water");
            Run(
                "CE-BULK WATER PRODUCTION V2",
                "Styles → route/network → profile/labels/setting-out → quantities/delivery.",
                new List<DisciplineWorkflowAction>
                {
                    A("CE-Import Missing Project Styles", "CE_PROJECTSTYLEIMPORT", "Import project pressure/profile styles before production.", "01 SETTINGS"),
                    A("CE-Discipline Style Preset", "CE_DISCIPLINESTYLEPRESETS", "Activate/save the Bulk Water style preset.", "01 SETTINGS"),
                    A("CE-Utility Route from Road Reserve", "CE_UTILITYFROMROADRESERVE", "Create bulk-water planning routes at selected offsets.", "02 PREPARE"),
                    A("CE-Multiple Networks from Polylines", "CE_NETWORKFROMPOLYLINESBATCH", "Batch multiple source polylines/feature lines.", "03 CREATE"),
                    A("CE-Network Data", "CE_NETWORKDATA", "Review pressure-network objects and levels.", "04 DESIGN"),
                    A("CE-Safe Profile Fallback", "CE_WATERPROFILESAFE", "Direct profile/profile-view fallback is shared with Water alignments.", "05 COMPLETE"),
                    A("CE-Bulk Water Pressure-Part Labels", "CE_BULKWATERLABELS", "Label pressure pipes, fittings and appurtenances.", "05 COMPLETE"),
                    A("CE-Vertex Setting-Out", "CE_VERTEXSETTINGOUT", "Generate linked setting-out.", "05 COMPLETE"),
                    A("CE-Bulk Water BOQ", "CE_BOQBULKWATER", "Create linked bulk-water quantities.", "06 DELIVER"),
                    A("CE-Bulk Water Report", "CE_REPORTBULKWATER", "Generate bulk-water design report.", "06 DELIVER")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_PARKINGPRODUCTIONV2", CommandFlags.Modal)]
        public void ParkingV2()
        {
            Activate("Parking");
            Run(
                "CE-PARKING PRODUCTION V2",
                "Boundary/layout → grading → linked numbering/validation → quantities.",
                new List<DisciplineWorkflowAction>
                {
                    A("CE-Parking Tools / Settings", "CE_PKTOOLS", "Open parking layout and annotation settings.", "01 SETTINGS"),
                    A("CE-Parking Options", "CE_PARKOPTIONS", "Compare parking arrangements inside a selected boundary.", "02 PREPARE"),
                    A("CE-Parking Optimiser", "CE_PARKOPTIMIZE", "Create obstacle-aware parking alternatives.", "03 CREATE"),
                    A("CE-Parking Grading", "CE_PARKGRADINGTOOLS", "Create linked grading/drainage guides.", "04 DESIGN"),
                    A("CE-Dynamic / Annotative Parking Numbers", "CE_PKNUMBERDYNAMIC", "Create P-n labels linked to bay geometry with absolute paper text height.", "05 COMPLETE"),
                    A("CE-Upgrade Existing Parking Numbers", "CE_PKNUMBERUPGRADE", "Link existing parking-number MText to bays and make it annotative.", "05 COMPLETE"),
                    A("CE-Refresh Linked Parking Numbers", "CE_PKNUMBERREFRESH", "Refresh all linked bay-number labels.", "05 COMPLETE"),
                    A("CE-Parking Skew / Width Validation", "CE_PKSKVALIDATE", "Check bay width and skew.", "05 COMPLETE"),
                    A("CE-Parking Quantities", "CE_PARKQTYTOOLS", "Create parking/layerwork quantity outputs.", "06 DELIVER")
                });
        }

        private static void Activate(string discipline)
        {
            Document document = Active();
            if (document != null) August11DisciplineStylePresetManager.ActivateForProduction(document.Database, discipline);
        }

        private static void Run(string title, string note, IList<DisciplineWorkflowAction> actions)
        {
            Document document = Active();
            if (document != null) DisciplineWorkflowDialogs.SelectAndRun(document, title, note, actions);
        }

        private static DisciplineWorkflowAction A(string title, string command, string description, string group)
        {
            return new DisciplineWorkflowAction(title, command, description, group);
        }

        private static Document Active()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
        }
    }
}

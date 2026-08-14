using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.August14StructuredDisciplineProductionCentres))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Gives every CE Production discipline the same progressive navigation used
    /// by Roads: main Production Centre -> discipline centre -> focused workflow
    /// centre -> live engineering commands. Existing engineering commands retain
    /// ownership; this class only organizes and dispatches them.
    /// </summary>
    public sealed class August14StructuredDisciplineProductionCentres
    {
        // ---------------------------------------------------------------------
        // PROJECT
        // ---------------------------------------------------------------------
        [CommandMethod("CE_TOOLS", "CE_PROJECTPRODUCTIONSTRUCTURED", CommandFlags.Modal)]
        public void ProjectProduction()
        {
            Run("CE-PROJECT PRODUCTION",
                "Choose Project Settings, Project Coordination Production or Project Delivery Production.",
                new List<DisciplineWorkflowAction>
                {
                    A("CE-Project Settings", "CE_PROJECTSETTINGSPRODUCTIONCENTRE", "Project information, standards and shared styles.", "01 Project Production"),
                    A("CE-Project Coordination Production", "CE_PROJECTCOORDINATIONPRODUCTIONCENTRE", "Coordinate sources and refresh linked project metadata.", "01 Project Production"),
                    A("CE-Project Delivery Production", "CE_PROJECTDELIVERYPRODUCTIONCENTRE", "Project information, drawing register and books.", "01 Project Production")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_PROJECTSETTINGSPRODUCTIONCENTRE", CommandFlags.Modal)]
        public void ProjectSettings()
        {
            Run("CE-PROJECT SETTINGS",
                "Set the reusable project information first, then standards and shared Civil 3D style choices.",
                new List<DisciplineWorkflowAction>
                {
                    A("CE-Project Setup - Last Saved or Blank", "CE_PROJECTSETUPCHOICE", "Open Last Saved project information or a Standard Blank project form.", "01 SETTINGS"),
                    A("CE-Project Style Centre", "CE_PROJECTSTYLES", "Select/import shared project Civil 3D styles.", "01 SETTINGS"),
                    A("CE-Discipline Style Presets", "CE_DISCIPLINESTYLEPRESETS", "Save/apply discipline-specific style presets.", "01 SETTINGS"),
                    A("CE-Standards", "CE_STANDARDS", "Select and record project standards.", "01 SETTINGS")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_PROJECTCOORDINATIONPRODUCTIONCENTRE", CommandFlags.Modal)]
        public void ProjectCoordination()
        {
            Run("CE-PROJECT COORDINATION PRODUCTION",
                "Coordinate the project environment and synchronize linked metadata before discipline production.",
                new List<DisciplineWorkflowAction>
                {
                    A("CE-Project Coordination", "CE_PROJECTCOORDINATION", "Coordinate source drawings, project location and page setup environment.", "02 PREPARE"),
                    A("CE-Project Metadata Refresh", "CE_PROJECTMETADATAREFRESH", "Refresh project metadata into linked CE outputs.", "03 CREATE"),
                    A("CE-Project Information", "CE_PROJECTINFO", "Review the current drawing's linked project information.", "05 COMPLETE")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_PROJECTDELIVERYPRODUCTIONCENTRE", CommandFlags.Modal)]
        public void ProjectDelivery()
        {
            Run("CE-PROJECT DELIVERY PRODUCTION",
                "Review project information, drawing register and final project/client books.",
                new List<DisciplineWorkflowAction>
                {
                    A("CE-Project Information", "CE_PROJECTINFO", "Review/place the linked project information table.", "05 COMPLETE"),
                    A("CE-Drawing Register", "CE_DRAWINGREGISTEREDIT", "Review drawing numbers, titles, revisions and issue information.", "06 DELIVER"),
                    A("CE-Drawing / Client Books", "CE_BOOKTOOLS", "Create drawing books, client books and indexes.", "06 DELIVER")
                });
        }

        // ---------------------------------------------------------------------
        // SURVEY
        // ---------------------------------------------------------------------
        [CommandMethod("CE_TOOLS", "CE_SURVEYPRODUCTIONSTRUCTURED", CommandFlags.Modal)]
        public void SurveyProduction()
        {
            Run("CE-SURVEY PRODUCTION",
                "Choose Survey Settings, Survey Surface Production or Survey Setting-Out / Delivery Production.",
                new List<DisciplineWorkflowAction>
                {
                    A("CE-Survey Settings", "CE_SURVEYSETTINGSPRODUCTIONCENTRE", "Location, coordinate system and survey styles.", "01 Survey Production"),
                    A("CE-Survey Surface Production", "CE_SURVEYSURFACEPRODUCTIONCENTRE", "Import/create, review and correct survey surfaces.", "01 Survey Production"),
                    A("CE-Survey Setting-Out / Delivery Production", "CE_SURVEYDELIVERYPRODUCTIONCENTRE", "Setting-out, comparisons, tables and export.", "01 Survey Production")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_SURVEYSETTINGSPRODUCTIONCENTRE", CommandFlags.Modal)]
        public void SurveySettings()
        {
            Activate("Survey");
            Run("CE-SURVEY SETTINGS",
                "Set project location/coordinate system and the survey point/surface style environment.",
                new List<DisciplineWorkflowAction>
                {
                    A("CE-Survey Location / Coordinate System", "CE_SURVEYLOCATION", "Set project area and the installed Namibia LO coordinate system.", "01 SETTINGS"),
                    A("CE-Project Style Centre - Points / Surfaces", "CE_PROJECTSTYLES", "Select/import point, point-label and surface styles.", "01 SETTINGS"),
                    A("CE-Discipline Style Presets", "CE_DISCIPLINESTYLEPRESETS", "Save/apply the Survey style preset.", "01 SETTINGS")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_SURVEYSURFACEPRODUCTIONCENTRE", CommandFlags.Modal)]
        public void SurveySurfaces()
        {
            Run("CE-SURVEY SURFACE PRODUCTION",
                "Survey interchange -> create surface -> review/correct -> linked comparison-ready terrain.",
                new List<DisciplineWorkflowAction>
                {
                    A("CE-LandXML Import / Export", "CE_LANDXMLTOOLS", "Import/export Civil survey data.", "02 PREPARE"),
                    A("CE-Surface Production", "CE_SURVEYSURFACEPRODUCTION", "Create a surface from file/objects, choose style and create point-extent border.", "02 PREPARE"),
                    A("CE-Surface Tools", "CE_SURFTOOLS", "Review/create existing-ground surfaces.", "03 CREATE"),
                    A("CE-Surface Correction / Review", "CE_SURFCTOOLS", "Audit and create reversible corrected surfaces.", "04 DESIGN")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_SURVEYDELIVERYPRODUCTIONCENTRE", CommandFlags.Modal)]
        public void SurveyDelivery()
        {
            Run("CE-SURVEY SETTING-OUT / DELIVERY PRODUCTION",
                "Create linked setting-out, compare surfaces and deliver tables/exports.",
                new List<DisciplineWorkflowAction>
                {
                    A("CE-Vertex Setting-Out", "CE_VERTEXSETTINGOUT", "Linked COGO/MText/MLeader setting-out.", "05 COMPLETE"),
                    A("CE-Grid Setting-Out", "CE_GRIDSETTINGOUT", "Linked grid/perimeter setting-out.", "05 COMPLETE"),
                    A("CE-Base / Comparison Surface Table", "CE_SURFACECOMPARETABLE", "Create linked base/comparison surface tables.", "05 COMPLETE"),
                    A("CE-Refresh Surface Comparison Tables", "CE_COORDMULTISURFACEREFRESH", "Refresh linked comparison tables.", "05 COMPLETE"),
                    A("CE-Survey Comparison / Export", "CE_SURVEYCOMPARETOOLS", "Review survey corrections and comparison output.", "06 DELIVER"),
                    A("CE-Export Table to CSV / Excel", "CE_TABLEEXPORTCSV", "Export selected CE table to Excel-compatible CSV.", "06 DELIVER"),
                    A("CE-Correct Table Column Spacing", "CE_TABLECOLUMNSPACE", "Apply consistent table column spacing.", "06 DELIVER")
                });
        }

        // ---------------------------------------------------------------------
        // PLATFORM
        // ---------------------------------------------------------------------
        [CommandMethod("CE_TOOLS", "CE_PLATFORMPRODUCTIONSTRUCTURED", CommandFlags.Modal)]
        public void PlatformProduction()
        {
            Run("CE-PLATFORM PRODUCTION",
                "Choose Platform Settings, Platform Layout Production or Platform Design Production.",
                new List<DisciplineWorkflowAction>
                {
                    A("CE-Platform Settings", "CE_PLATFORMSETTINGSPRODUCTIONCENTRE", "Platform styles and shared production settings.", "01 Platform Production"),
                    A("CE-Platform Layout Production", "CE_PLATFORMLAYOUTPRODUCTIONCENTRE", "Source boundaries, feature lines and linked offsets.", "01 Platform Production"),
                    A("CE-Platform Design Production", "CE_PLATFORMDESIGNPRODUCTIONCENTRE", "Levels, grading, setting-out, quantities and drawings.", "01 Platform Production")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_PLATFORMSETTINGSPRODUCTIONCENTRE", CommandFlags.Modal)]
        public void PlatformSettings()
        {
            Activate("Platform");
            Run("CE-PLATFORM SETTINGS",
                "Select platform feature-line, grading, surface and annotation styles before layout/design.",
                new List<DisciplineWorkflowAction>
                {
                    A("CE-Project Style Centre - Platform", "CE_PROJECTSTYLES", "Select feature-line, grading, surface and annotation styles.", "01 SETTINGS"),
                    A("CE-Discipline Style Presets", "CE_DISCIPLINESTYLEPRESETS", "Save/apply the Platform style preset.", "01 SETTINGS")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_PLATFORMLAYOUTPRODUCTIONCENTRE", CommandFlags.Modal)]
        public void PlatformLayout()
        {
            Run("CE-PLATFORM LAYOUT PRODUCTION",
                "Create the platform control geometry before levels and grading.",
                new List<DisciplineWorkflowAction>
                {
                    A("CE-Create Feature Lines", "CE_FLCREATE", "Create multiple feature lines from selected source polylines.", "02 PREPARE"),
                    A("CE-Stepped Platform Offsets", "CE_PLATFORMSTEPOFFSETS", "Create linked stepped offsets from platform controls.", "03 CREATE"),
                    A("CE-Close Feature Lines for Infill", "CE_FLCLOSEINFILL", "Close selected feature lines for infill while preserving control vertices.", "03 CREATE")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_PLATFORMDESIGNPRODUCTIONCENTRE", CommandFlags.Modal)]
        public void PlatformDesign()
        {
            Run("CE-PLATFORM DESIGN PRODUCTION",
                "Apply platform levels/grading, then complete setting-out, quantities and drawings.",
                new List<DisciplineWorkflowAction>
                {
                    A("CE-Platform Slopes / Levels", "CE_PLATFORMSLOPE", "Constant slope, fixed slope or flatten to highest elevation.", "04 DESIGN"),
                    A("CE-Drape / Platform Surface", "CE_PLATFORMDRAPE", "Drape linked platform controls to a selected surface.", "04 DESIGN"),
                    A("CE-Platform Setting-Out", "CE_PLATFORMSETTINGOUT", "Linked vertex/grid setting-out and tables.", "05 COMPLETE"),
                    A("CE-Platform Names / Register", "CE_PLATFORMTABLE", "Linked platform names, elevations and register.", "05 COMPLETE"),
                    A("CE-Platform Cut / Fill", "CE_PLATFORMCUTFILL", "Linked NG versus design quantities.", "06 DELIVER"),
                    A("CE-Platform Drawings / Sections", "CE_PLATFORMDRAWINGS", "Create platform layouts and section sources.", "06 DELIVER")
                });
        }

        // ---------------------------------------------------------------------
        // STORMWATER
        // ---------------------------------------------------------------------
        [CommandMethod("CE_TOOLS", "CE_SWPRODUCTIONSTRUCTURED", CommandFlags.Modal)]
        public void StormwaterProduction()
        {
            Activate("Stormwater");
            Run("CE-STORMWATER PRODUCTION",
                "Choose Stormwater Settings, Stormwater Network / Layout Production or Stormwater Design Production.",
                new List<DisciplineWorkflowAction>
                {
                    A("CE-Stormwater Settings", "CE_SWSETTINGSPRODUCTIONCENTRE", "Parts, styles, labels and profile settings.", "01 Stormwater Production"),
                    A("CE-Stormwater Network / Layout Production", "CE_SWLAYOUTPRODUCTIONCENTRE", "Routes, multiple networks, sequencing and flow direction.", "01 Stormwater Production"),
                    A("CE-Stormwater Design Production", "CE_SWDESIGNPRODUCTIONCENTRE", "Network data, alignments, profiles, labels, BOQ and report.", "01 Stormwater Production")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_SWSETTINGSPRODUCTIONCENTRE", CommandFlags.Modal)]
        public void StormwaterSettings()
        {
            Activate("Stormwater");
            Run("CE-STORMWATER SETTINGS",
                "Import missing styles first if needed, then choose Stormwater production styles and presets.",
                new List<DisciplineWorkflowAction>
                {
                    A("CE-Import Missing Project Styles", "CE_PROJECTSTYLEIMPORT", "Import source Civil 3D styles when dropdowns are empty.", "01 SETTINGS"),
                    A("CE-Stormwater Settings", "CE_SWSETTINGS", "Choose alignment/profile/profile-view/band styles and layers.", "01 SETTINGS"),
                    A("CE-Discipline Style Presets", "CE_DISCIPLINESTYLEPRESETS", "Save/apply the Stormwater style preset.", "01 SETTINGS")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_SWLAYOUTPRODUCTIONCENTRE", CommandFlags.Modal)]
        public void StormwaterLayout()
        {
            Run("CE-STORMWATER NETWORK / LAYOUT PRODUCTION",
                "Plan routes, create multiple network sources, sequence branches and correct flow direction.",
                new List<DisciplineWorkflowAction>
                {
                    A("CE-Utility Route from Road Reserve", "CE_UTILITYFROMROADRESERVE", "Create a preliminary route from road-reserve geometry.", "02 PREPARE"),
                    A("CE-Multiple Networks from Polylines", "CE_NETWORKFROMPOLYLINESBATCH", "Create multiple Stormwater network sources in the same drawing.", "03 CREATE"),
                    A("CE-Reset Stale Network Batch", "CE_NETWORKBATCHRESET", "Clear a stuck network batch without deleting completed networks.", "03 CREATE"),
                    A("CE-Sequence Stormwater Network", "CE_SWSEQ", "Build main/branch sequence.", "03 CREATE"),
                    A("CE-Check / Correct Flow to Outlet", "CE_FLOWTOOUTLET", "Reverse source curves toward the low end or selected outlet.", "04 DESIGN")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_SWDESIGNPRODUCTIONCENTRE", CommandFlags.Modal)]
        public void StormwaterDesign()
        {
            Run("CE-STORMWATER DESIGN PRODUCTION",
                "Review network data, then complete alignments, profiles, labels, setting-out and delivery.",
                new List<DisciplineWorkflowAction>
                {
                    A("CE-Network Data / Levels", "CE_NETWORKDATA", "Review pipe/structure levels, lengths and slopes.", "04 DESIGN"),
                    A("CE-Create / Refresh Stormwater Alignments", "CE_SWALIGN", "Create linked Stormwater alignments.", "05 COMPLETE"),
                    A("CE-Safe Stormwater Alignment Fallback", "CE_SWALIGNSAFE", "Clean duplicate/zero-length source vertices and create direct Civil alignments.", "05 COMPLETE"),
                    A("CE-Stormwater Profiles", "CE_SWPROFILE", "Create linked Stormwater profiles/profile views.", "05 COMPLETE"),
                    A("CE-Stormwater Pipe / Structure Labels", "CE_SWLABELS", "Apply Stormwater plan labels using selected styles.", "05 COMPLETE"),
                    A("CE-Vertex Setting-Out", "CE_VERTEXSETTINGOUT", "Generate linked setting-out from design geometry.", "05 COMPLETE"),
                    A("CE-Stormwater Bill of Quantities", "CE_BOQSTORMWATER", "Create linked Stormwater quantities.", "06 DELIVER"),
                    A("CE-Stormwater Report", "CE_REPORTSTORMWATER", "Generate Stormwater report/drawing handoff.", "06 DELIVER")
                });
        }

        // ---------------------------------------------------------------------
        // SEWER
        // ---------------------------------------------------------------------
        [CommandMethod("CE_TOOLS", "CE_SEWERPRODUCTIONSTRUCTURED", CommandFlags.Modal)]
        public void SewerProduction()
        {
            Activate("Sewer");
            Run("CE-SEWER PRODUCTION",
                "Choose Sewer Settings, Sewer Network / Layout Production or Sewer Design Production.",
                new List<DisciplineWorkflowAction>
                {
                    A("CE-Sewer Settings", "CE_SEWERSETTINGSPRODUCTIONCENTRE", "Parts, styles, labels, profile and band settings.", "01 Sewer Production"),
                    A("CE-Sewer Network / Layout Production", "CE_SEWERLAYOUTPRODUCTIONCENTRE", "Midblock/road-reserve routing, networks and sequencing.", "01 Sewer Production"),
                    A("CE-Sewer Design Production", "CE_SEWERDESIGNPRODUCTIONCENTRE", "Network data, alignments, profiles, labels, BOQ and report.", "01 Sewer Production")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_SEWERSETTINGSPRODUCTIONCENTRE", CommandFlags.Modal)]
        public void SewerSettings()
        {
            Activate("Sewer");
            Run("CE-SEWER SETTINGS",
                "Import missing styles if required, then choose Sewer network/profile/label settings.",
                new List<DisciplineWorkflowAction>
                {
                    A("CE-Import Missing Project Styles", "CE_PROJECTSTYLEIMPORT", "Import Civil 3D styles required by Sewer production.", "01 SETTINGS"),
                    A("CE-Sewer Settings", "CE_SEWSETTINGS", "Choose parts, styles, labels, profiles and bands.", "01 SETTINGS"),
                    A("CE-Discipline Style Presets", "CE_DISCIPLINESTYLEPRESETS", "Save/apply the Sewer style preset.", "01 SETTINGS")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_SEWERLAYOUTPRODUCTIONCENTRE", CommandFlags.Modal)]
        public void SewerLayout()
        {
            Run("CE-SEWER NETWORK / LAYOUT PRODUCTION",
                "Create the sewer route/network and establish main/branch sequence before detailed levels/profiles.",
                new List<DisciplineWorkflowAction>
                {
                    A("CE-Midblock / Road-Reserve Sewer Route", "CE_MIDBLOCKSEWERPRODUCTION", "Create continuous selected-side/low-side sewer routes and planning manholes.", "02 PREPARE"),
                    A("CE-Multiple Networks from Polylines", "CE_NETWORKFROMPOLYLINESBATCH", "Create multiple selected network sources.", "03 CREATE"),
                    A("CE-Reset Stale Network Batch", "CE_NETWORKBATCHRESET", "Clear a stuck network batch state.", "03 CREATE"),
                    A("CE-Sequence Sewer Branches / Structures / Pipes", "CE_SEWSEQ", "Build the live sewer network sequence and branch names.", "03 CREATE"),
                    A("CE-Check / Correct Flow to Outlet", "CE_FLOWTOOUTLET", "Correct selected source-curve direction toward the outlet/low point.", "04 DESIGN")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_SEWERDESIGNPRODUCTIONCENTRE", CommandFlags.Modal)]
        public void SewerDesign()
        {
            Run("CE-SEWER DESIGN PRODUCTION",
                "Review live network levels and complete alignments, profiles, labels, setting-out, BOQ and report.",
                new List<DisciplineWorkflowAction>
                {
                    A("CE-Network Data / Levels", "CE_NETWORKDATA", "Review pipe/structure levels, lengths and slopes.", "04 DESIGN"),
                    A("CE-Sewer Alignments", "CE_SEWALIGN", "Create linked branch alignments.", "05 COMPLETE"),
                    A("CE-Sewer Profiles", "CE_SEWPROFILE", "Create isolated branch profiles/profile views with band import.", "05 COMPLETE"),
                    A("CE-Sewer Labels", "CE_SEWLABELS", "Apply pipe/structure labels and branch presentation.", "05 COMPLETE"),
                    A("CE-Vertex Setting-Out", "CE_VERTEXSETTINGOUT", "Generate linked sewer setting-out.", "05 COMPLETE"),
                    A("CE-Sewer Bill of Quantities", "CE_BOQSEWER", "Create linked Sewer quantities.", "06 DELIVER"),
                    A("CE-Sewer Report", "CE_REPORTSEWER", "Generate Sewer report/drawing handoff.", "06 DELIVER")
                });
        }

        // ---------------------------------------------------------------------
        // WATER
        // ---------------------------------------------------------------------
        [CommandMethod("CE_TOOLS", "CE_WATERPRODUCTIONSTRUCTURED", CommandFlags.Modal)]
        public void WaterProduction()
        {
            Activate("Water");
            Run("CE-WATER PRODUCTION",
                "Choose Water Settings, Water Network / Layout Production or Water Design Production.",
                new List<DisciplineWorkflowAction>
                {
                    A("CE-Water Settings", "CE_WATERSETTINGSPRODUCTIONCENTRE", "Pressure-network, alignment, profile and label settings.", "01 Water Production"),
                    A("CE-Water Network / Layout Production", "CE_WATERLAYOUTPRODUCTIONCENTRE", "Routes, multiple network sources and sequence.", "01 Water Production"),
                    A("CE-Water Design Production", "CE_WATERDESIGNPRODUCTIONCENTRE", "Network data, profiles, labels/assets, setting-out and delivery.", "01 Water Production")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_WATERSETTINGSPRODUCTIONCENTRE", CommandFlags.Modal)]
        public void WaterSettings()
        {
            Activate("Water");
            Run("CE-WATER SETTINGS",
                "Import missing styles if needed, then choose Water network/profile/label settings.",
                new List<DisciplineWorkflowAction>
                {
                    A("CE-Import Missing Project Styles", "CE_PROJECTSTYLEIMPORT", "Import Civil 3D styles when Water dropdowns are empty.", "01 SETTINGS"),
                    A("CE-Water Settings", "CE_WATERSETTINGS", "Choose alignment/profile/profile-view/band styles and asset spacing.", "01 SETTINGS"),
                    A("CE-Discipline Style Presets", "CE_DISCIPLINESTYLEPRESETS", "Save/apply the Water style preset.", "01 SETTINGS")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_WATERLAYOUTPRODUCTIONCENTRE", CommandFlags.Modal)]
        public void WaterLayout()
        {
            Run("CE-WATER NETWORK / LAYOUT PRODUCTION",
                "Plan Water routes, create multiple network sources and establish route sequence.",
                new List<DisciplineWorkflowAction>
                {
                    A("CE-Utility Route from Road Reserve", "CE_UTILITYFROMROADRESERVE", "Create preliminary Water planning routes from road-reserve geometry.", "02 PREPARE"),
                    A("CE-Multiple Networks from Polylines", "CE_NETWORKFROMPOLYLINESBATCH", "Create multiple Water network sources.", "03 CREATE"),
                    A("CE-Reset Stale Network Batch", "CE_NETWORKBATCHRESET", "Clear a stuck network batch state.", "03 CREATE"),
                    A("CE-Sequence Water Routes", "CE_WATERSEQ", "Create W-MAIN and branch route order.", "03 CREATE"),
                    A("CE-Check / Correct Flow Direction", "CE_FLOWTOOUTLET", "Correct selected planning-route direction when required.", "04 DESIGN")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_WATERDESIGNPRODUCTIONCENTRE", CommandFlags.Modal)]
        public void WaterDesign()
        {
            Run("CE-WATER DESIGN PRODUCTION",
                "Review network data and complete alignments, profiles, pressure labels/assets, setting-out and delivery.",
                new List<DisciplineWorkflowAction>
                {
                    A("CE-Network Data", "CE_NETWORKDATA", "Review pressure-network objects and levels.", "04 DESIGN"),
                    A("CE-Water Alignments", "CE_WATERALIGN", "Create/refresh linked Water alignments.", "05 COMPLETE"),
                    A("CE-Water Profiles", "CE_WATERPROFILE", "Create normal linked Water profiles with pressure-part projection where supported.", "05 COMPLETE"),
                    A("CE-Safe Water Profile Fallback", "CE_WATERPROFILESAFE", "Create direct surface profile/profile view without pressure projection when needed.", "05 COMPLETE"),
                    A("CE-Water Pressure-Part Labels", "CE_WATERLABELS", "Label pressure pipes, fittings and appurtenances.", "05 COMPLETE"),
                    A("CE-Water Asset Review Markers", "CE_WATERPLACE", "Place linked valve/hydrant review markers.", "05 COMPLETE"),
                    A("CE-Vertex Setting-Out", "CE_VERTEXSETTINGOUT", "Generate linked Water setting-out.", "05 COMPLETE"),
                    A("CE-Water Bill of Quantities", "CE_BOQWATER", "Create linked Water quantities.", "06 DELIVER"),
                    A("CE-Water Report", "CE_REPORTWATER", "Generate Water report/drawing handoff.", "06 DELIVER")
                });
        }

        // ---------------------------------------------------------------------
        // BULK WATER
        // ---------------------------------------------------------------------
        [CommandMethod("CE_TOOLS", "CE_BULKWATERPRODUCTIONSTRUCTURED", CommandFlags.Modal)]
        public void BulkWaterProduction()
        {
            Activate("Bulk Water");
            Run("CE-BULK WATER PRODUCTION",
                "Choose Bulk Water Settings, Bulk Water Network / Layout Production or Bulk Water Design Production.",
                new List<DisciplineWorkflowAction>
                {
                    A("CE-Bulk Water Settings", "CE_BULKWATERSETTINGSPRODUCTIONCENTRE", "Pressure-network, profile and label settings.", "01 Bulk Water Production"),
                    A("CE-Bulk Water Network / Layout Production", "CE_BULKWATERLAYOUTPRODUCTIONCENTRE", "Long-route planning and multiple network creation.", "01 Bulk Water Production"),
                    A("CE-Bulk Water Design Production", "CE_BULKWATERDESIGNPRODUCTIONCENTRE", "Network review, profiles, labels, setting-out and delivery.", "01 Bulk Water Production")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_BULKWATERSETTINGSPRODUCTIONCENTRE", CommandFlags.Modal)]
        public void BulkWaterSettings()
        {
            Activate("Bulk Water");
            Run("CE-BULK WATER SETTINGS",
                "Prepare the Bulk Water pressure/profile style environment before route/network production.",
                new List<DisciplineWorkflowAction>
                {
                    A("CE-Import Missing Project Styles", "CE_PROJECTSTYLEIMPORT", "Import pressure/profile styles from a source Civil 3D drawing.", "01 SETTINGS"),
                    A("CE-Project Style Centre - Water / Profiles", "CE_PROJECTSTYLES", "Select pressure-network and profile styles.", "01 SETTINGS"),
                    A("CE-Discipline Style Presets", "CE_DISCIPLINESTYLEPRESETS", "Save/apply the Bulk Water style preset.", "01 SETTINGS")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_BULKWATERLAYOUTPRODUCTIONCENTRE", CommandFlags.Modal)]
        public void BulkWaterLayout()
        {
            Run("CE-BULK WATER NETWORK / LAYOUT PRODUCTION",
                "Create the long-route planning geometry and multiple pressure-network sources.",
                new List<DisciplineWorkflowAction>
                {
                    A("CE-Utility Route from Road Reserve", "CE_UTILITYFROMROADRESERVE", "Create Bulk Water planning routes at selected road-reserve offsets.", "02 PREPARE"),
                    A("CE-Multiple Networks from Polylines", "CE_NETWORKFROMPOLYLINESBATCH", "Batch multiple source polylines/feature lines.", "03 CREATE"),
                    A("CE-Reset Stale Network Batch", "CE_NETWORKBATCHRESET", "Clear a stuck network batch state.", "03 CREATE")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_BULKWATERDESIGNPRODUCTIONCENTRE", CommandFlags.Modal)]
        public void BulkWaterDesign()
        {
            Run("CE-BULK WATER DESIGN PRODUCTION",
                "Review pressure-network data and complete profiles, labels, setting-out, quantities and report.",
                new List<DisciplineWorkflowAction>
                {
                    A("CE-Network Data", "CE_NETWORKDATA", "Review selected pressure-network objects and levels.", "04 DESIGN"),
                    A("CE-Safe Bulk Water Profile Fallback", "CE_WATERPROFILESAFE", "Create direct surface profiles/profile views when the pressure projection path is unsuitable.", "05 COMPLETE"),
                    A("CE-Bulk Water Pressure-Part Labels", "CE_BULKWATERLABELS", "Label pressure pipes, fittings and appurtenances.", "05 COMPLETE"),
                    A("CE-Vertex Setting-Out", "CE_VERTEXSETTINGOUT", "Generate linked Bulk Water setting-out.", "05 COMPLETE"),
                    A("CE-Bulk Water Bill of Quantities", "CE_BOQBULKWATER", "Create linked Bulk Water quantities.", "06 DELIVER"),
                    A("CE-Bulk Water Report", "CE_REPORTBULKWATER", "Generate Bulk Water design report/drawing handoff.", "06 DELIVER")
                });
        }

        // ---------------------------------------------------------------------
        // PARKING
        // ---------------------------------------------------------------------
        [CommandMethod("CE_TOOLS", "CE_PARKINGPRODUCTIONSTRUCTURED", CommandFlags.Modal)]
        public void ParkingProduction()
        {
            Activate("Parking");
            Run("CE-PARKING AREA PRODUCTION",
                "Choose Parking Settings, Parking Layout Production or Parking Design Production.",
                new List<DisciplineWorkflowAction>
                {
                    A("CE-Parking Settings", "CE_PARKINGSETTINGSPRODUCTIONCENTRE", "Parking layout, annotation and shared style settings.", "01 Parking Production"),
                    A("CE-Parking Layout Production", "CE_PARKINGLAYOUTPRODUCTIONCENTRE", "Parking options and obstacle-aware layout generation.", "01 Parking Production"),
                    A("CE-Parking Design Production", "CE_PARKINGDESIGNPRODUCTIONCENTRE", "Grading, dynamic numbering, validation, setting-out and quantities.", "01 Parking Production")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_PARKINGSETTINGSPRODUCTIONCENTRE", CommandFlags.Modal)]
        public void ParkingSettings()
        {
            Activate("Parking");
            Run("CE-PARKING SETTINGS",
                "Set parking layout/annotation parameters and project style presets before layout production.",
                new List<DisciplineWorkflowAction>
                {
                    A("CE-Parking Tools / Settings", "CE_PKTOOLS", "Open parking layout and annotation settings.", "01 SETTINGS"),
                    A("CE-Project Style Centre", "CE_PROJECTSTYLES", "Select shared annotation/surface styles.", "01 SETTINGS"),
                    A("CE-Discipline Style Presets", "CE_DISCIPLINESTYLEPRESETS", "Save/apply the Parking style preset.", "01 SETTINGS")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_PARKINGLAYOUTPRODUCTIONCENTRE", CommandFlags.Modal)]
        public void ParkingLayout()
        {
            Run("CE-PARKING LAYOUT PRODUCTION",
                "Compare parking arrangements and generate the selected layout before grading/annotation.",
                new List<DisciplineWorkflowAction>
                {
                    A("CE-Parking Options", "CE_PARKOPTIONS", "Compare parking arrangements inside a selected boundary.", "02 PREPARE"),
                    A("CE-Parking Optimiser", "CE_PARKOPTIMIZE", "Create obstacle-aware parking alternatives.", "03 CREATE")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_PARKINGDESIGNPRODUCTIONCENTRE", CommandFlags.Modal)]
        public void ParkingDesign()
        {
            Run("CE-PARKING DESIGN PRODUCTION",
                "Grade the parking area, create linked numbers, validate bays, set out and quantify.",
                new List<DisciplineWorkflowAction>
                {
                    A("CE-Parking Grading", "CE_PARKGRADINGTOOLS", "Create linked grading/drainage guides.", "04 DESIGN"),
                    A("CE-Dynamic / Annotative Parking Numbers", "CE_PKNUMBERDYNAMIC", "Create P-number labels linked to bay geometry with paper text height.", "05 COMPLETE"),
                    A("CE-Upgrade Existing Parking Numbers", "CE_PKNUMBERUPGRADE", "Link existing P-number MText to parking bays and make it annotative.", "05 COMPLETE"),
                    A("CE-Refresh Linked Parking Numbers", "CE_PKNUMBERREFRESH", "Refresh linked bay-number labels.", "05 COMPLETE"),
                    A("CE-Parking Skew / Width Validation", "CE_PKSKVALIDATE", "Check bay width and skew.", "05 COMPLETE"),
                    A("CE-Grid / Parking Setting-Out", "CE_GRIDSETTINGOUT", "Create setting-out where applicable.", "05 COMPLETE"),
                    A("CE-Parking Quantities", "CE_PARKQTYTOOLS", "Create parking/layerwork quantity outputs.", "06 DELIVER")
                });
        }

        // ---------------------------------------------------------------------
        // FLOOD
        // ---------------------------------------------------------------------
        [CommandMethod("CE_TOOLS", "CE_FLOODPRODUCTIONSTRUCTURED", CommandFlags.Modal)]
        public void FloodProduction()
        {
            Activate("Flood");
            Run("CE-FLOOD PRODUCTION",
                "Choose Flood Settings, Flood Analysis Production or Flood Output / Delivery Production.",
                new List<DisciplineWorkflowAction>
                {
                    A("CE-Flood Settings", "CE_FLOODSETTINGSPRODUCTIONCENTRE", "Terrain, rainfall/runoff and analysis settings.", "01 Flood Production"),
                    A("CE-Flood Analysis Production", "CE_FLOODANALYSISPRODUCTIONCENTRE", "Quick flood, hydrology and affected-area analysis.", "01 Flood Production"),
                    A("CE-Flood Output / Delivery Production", "CE_FLOODDELIVERYPRODUCTIONCENTRE", "Culvert review and final flood reporting.", "01 Flood Production")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_FLOODSETTINGSPRODUCTIONCENTRE", CommandFlags.Modal)]
        public void FloodSettings()
        {
            Activate("Flood");
            Run("CE-FLOOD SETTINGS",
                "Prepare the terrain and hydrology inputs before running flood analysis.",
                new List<DisciplineWorkflowAction>
                {
                    A("CE-Hydrology / Flood Inputs", "CE_HYDROLOGYTOOLS", "Review rainfall/runoff and analysis settings.", "01 SETTINGS"),
                    A("CE-Surface / Catchment Review", "CE_SURFTOOLS", "Review the terrain source before flood calculations.", "01 SETTINGS"),
                    A("CE-Project Style Centre", "CE_PROJECTSTYLES", "Select/import surface and annotation styles used by flood outputs.", "01 SETTINGS")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_FLOODANALYSISPRODUCTIONCENTRE", CommandFlags.Modal)]
        public void FloodAnalysis()
        {
            Run("CE-FLOOD ANALYSIS PRODUCTION",
                "Run preliminary return-period analysis, terrain hydrology and affected-area review.",
                new List<DisciplineWorkflowAction>
                {
                    A("CE-Quick Flood / Rational Review", "CE_FLOODQUICK", "Pre/post return-period peak-flow and preliminary culvert screen.", "03 CREATE"),
                    A("CE-Surface Hydrology", "CE_HYDROLOGYREVIEW", "Review flow routes, catchments and terrain storage.", "04 DESIGN"),
                    A("CE-Affected Property / Flood Results", "CE_FLOODRESULTTOOLS", "Review specialist flood results and affected properties.", "04 DESIGN")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_FLOODDELIVERYPRODUCTIONCENTRE", CommandFlags.Modal)]
        public void FloodDelivery()
        {
            Run("CE-FLOOD OUTPUT / DELIVERY PRODUCTION",
                "Complete crossing/culvert review and generate the final flood reporting output.",
                new List<DisciplineWorkflowAction>
                {
                    A("CE-Culvert Review", "CE_CULVERTREVIEW", "Review candidate crossings and culvert requirements.", "05 COMPLETE"),
                    A("CE-Flood / Project Report", "CE_REPORTFULL", "Generate the final project/discipline report output.", "06 DELIVER")
                });
        }

        private static void Activate(string discipline)
        {
            Document document = Active();
            if (document != null)
                August11DisciplineStylePresetManager.ActivateForProduction(document.Database, discipline);
        }

        private static void Run(string title, string note, IList<DisciplineWorkflowAction> actions)
        {
            Document document = Active();
            if (document != null)
                DisciplineWorkflowDialogs.SelectAndRun(document, title, note, actions);
        }

        private static DisciplineWorkflowAction A(
            string title,
            string command,
            string description,
            string group)
        {
            return new DisciplineWorkflowAction(title, command, description, group);
        }

        private static Document Active()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
        }
    }
}

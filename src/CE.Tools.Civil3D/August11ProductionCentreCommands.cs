using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Windows;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.August11ProductionCentreCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Friendly front door for CE Tools discipline production. Each discipline is
    /// staged so the user sees the important production commands instead of the
    /// complete engineering command inventory.
    /// </summary>
    public sealed class August11ProductionCentreCommands
    {
        [CommandMethod("CE_TOOLS", "CE_WELCOME", CommandFlags.Modal)]
        public void Welcome()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            var window = new CeWelcomeWindow(CeThemeStore.Read());
            bool? accepted = AcApplication.ShowModalWindow(window);
            if (accepted != true || string.IsNullOrWhiteSpace(window.SelectedCommand)) return;
            document.SendStringToExecute(window.SelectedCommand.Trim() + " ", true, false, true);
        }

        [CommandMethod("CE_TOOLS", "CE_CETHEME", CommandFlags.Modal)]
        public void Theme()
        {
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Appearance",
                "Choose the CE Tools welcome/production-window theme. Civil 3D owns the host ribbon background; CE Tools buttons and icons remain compatible with both host themes.");
            model.AddChoice("Theme", "Appearance", "CE Tools theme", CeThemeStore.Read(), "Applied to CE Tools welcome and dedicated production windows.", new[] { "Dark", "Light" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;
            CeThemeStore.Write(model.Text("Theme"));
            CeInterfaceTheme.RefreshOpenWindows();
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document != null) document.Editor.WriteMessage("\nCE Tools theme saved: {0}.", CeThemeStore.Read());
        }

        [CommandMethod("CE_TOOLS", "CE_PRODUCTIONCENTRE", CommandFlags.Modal)]
        public void ProductionCentre()
        {
            Document document = Active();
            if (document == null) return;
            DisciplineWorkflowDialogs.SelectAndRun(
                document,
                "CE-PRODUCTION CENTRE",
                "Choose a discipline. Each production centre keeps Settings first, then preparation/design/production and delivery. Use CE-ENGINEERING CENTRE for the full command library.",
                new List<DisciplineWorkflowAction>
                {
                    Action("PROJECT PRODUCTION", "CE_PROJECTPRODUCTIONCENTRE", "Project setup, standards, styles, registers and coordinated delivery.", "01 Disciplines"),
                    Action("SURVEY PRODUCTION", "CE_SURVEYPRODUCTIONCENTRE", "Coordinate system, survey data, surfaces and setting-out.", "01 Disciplines"),
                    Action("PLATFORM PRODUCTION", "CE_PLATFORMPRODUCTIONCENTRE", "Platforms, levels, grading, setting-out, quantities and drawings.", "01 Disciplines"),
                    Action("ROAD PRODUCTION", "CE_ROADPRODUCTIONCENTRE", "Road Settings → Road Layout Production → Road Design Production.", "01 Disciplines"),
                    Action("STORMWATER PRODUCTION", "CE_SWPRODUCTIONCENTRE", "Routes, networks, branches, profiles, quantities and drawings.", "01 Disciplines"),
                    Action("SEWER PRODUCTION", "CE_SEWERPRODUCTIONCENTRE", "Midblock/road-reserve routing through network, profiles, setting-out and BOQ.", "01 Disciplines"),
                    Action("WATER PRODUCTION", "CE_WATERPRODUCTIONCENTRE", "Water routes, pressure network production, profiles and assets.", "01 Disciplines"),
                    Action("BULK WATER PRODUCTION", "CE_BULKWATERPRODUCTIONCENTRE", "Bulk-water routes, profiles, quantities and delivery.", "01 Disciplines"),
                    Action("PARKING AREA PRODUCTION", "CE_PARKINGPRODUCTIONCENTRE", "Boundary layout, grading, checks, setting-out and quantities.", "01 Disciplines"),
                    Action("FLOOD PRODUCTION", "CE_FLOODPRODUCTIONCENTRE", "Catchments, hydrology, affected areas, culverts and flood outputs.", "01 Disciplines"),
                    Action("CE-ENGINEERING CENTRE", "CE_ENGINEERINGINTELLIGENCECENTRE", "Open the full CE Tools command/workflow library.", "02 Full library"),
                    Action("Appearance - Dark / Light", "CE_CETHEME", "Choose the CE Tools welcome/production-window theme.", "03 Appearance")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_ENGINEERINGINTELLIGENCECENTRE", CommandFlags.Modal)]
        public void EngineeringIntelligenceCentre()
        {
            Document document = Active();
            if (document == null) return;
            document.SendStringToExecute("CE_TOOLSPALETTE ", true, false, true);
        }

        [CommandMethod("CE_TOOLS", "CE_PROJECTPRODUCTIONCENTRE", CommandFlags.Modal)]
        public void ProjectProduction()
        {
            RunCentre("PROJECT PRODUCTION", "Prepare the complete coordinated project environment before discipline design.", new[]
            {
                Action("SETTINGS - Project Setup", "CE_PROJECTSETUP", "Project/client/stage/revision/designed-drawn-checked-approved information.", "01 SETTINGS"),
                Action("Project Style Centre", "CE_PROJECTSTYLES", "Select shared discipline Civil 3D styles.", "01 SETTINGS"),
                Action("Discipline Style Presets", "CE_DISCIPLINESTYLEPRESETS", "Review, copy or activate independent Roads/SW/Sewer/Water/Platform/Bulk Water/Parking/Flood selections from the shared style library.", "01 SETTINGS"),
                Action("PREPARE - Project Coordination", "CE_PROJECTCOORDINATION", "Coordinate source drawings, location and page setups.", "02 PREPARE"),
                Action("Standards", "CE_DESIGNSTANDARDS", "Select and record project standards.", "02 PREPARE"),
                Action("CREATE - Project Metadata Refresh", "CE_PROJECTMETADATAREFRESH", "Synchronize project metadata into linked project outputs.", "03 CREATE"),
                Action("Import sheets and model from DWG", "CE_IMPORTSHEETS", "Copy all or selected layouts, viewports and model geometry from a DWG without opening it.", "06 DELIVER"),
                Action("COMPLETE - Drawing Register", "CE_DRAWINGREGISTEREDIT", "Review drawing numbers, titles, revisions and issue information.", "05 COMPLETE"),
                Action("DELIVER - Drawing / Client Books", "CE_BOOKTOOLS", "Create drawing books, client books and indexes.", "06 DELIVER"),
                Action("▶ RUN COMPLETE PROJECT PRODUCTION", "CE_PROJECTWORKFLOW", "Open one ordered, non-duplicated project workflow from setup through delivery.", "00 COMPLETE WORKFLOW")
            });
        }

        [CommandMethod("CE_TOOLS", "CE_SURVEYPRODUCTIONCENTRE", CommandFlags.Modal)]
        public void SurveyProduction()
        {
            RunCentre("SURVEY PRODUCTION", "Coordinate system → existing ground → survey cleanup → setting-out.", new[]
            {
                Action("SETTINGS - Survey Location / Coordinate System", "CE_SURVEYLOCATION", "Choose town/project area and assign the installed Namibia LO system.", "01 SETTINGS"),
                Action("SETTINGS - Survey Styles", "CE_SURVEYSTYLES", "Select point, point-label and surface styles.", "01 SETTINGS"),
                Action("PREPARE - LandXML Import / Export", "CE_LANDXMLTOOLS", "Import or export survey/Civil LandXML.", "02 PREPARE"),
                Action("Surface Tools", "CE_SURFTOOLS", "Create/review existing-ground surfaces.", "03 CREATE"),
                Action("DESIGN - Surface Correction / Review", "CE_SURFCTOOLS", "Audit and create reversible corrected surface copies.", "04 DESIGN"),
                Action("COMPLETE - Vertex Setting-Out", "CE_VERTEXSETTINGOUT", "Generate linked COGO/MText/MLeader setting-out from multiple design strings.", "05 COMPLETE"),
                Action("Grid Setting-Out", "CE_GRIDSETTINGOUT", "Generate linked grid/perimeter setting-out points.", "05 COMPLETE"),
                Action("DELIVER - Survey Comparison / Export", "CE_SURVEYCOMPARETOOLS", "Review corrections and export survey data.", "06 DELIVER"),
                Action("▶ RUN COMPLETE SURVEY PRODUCTION", "CE_SURVEYWORKFLOW", "Open one ordered, non-duplicated survey workflow from coordinate system through delivery.", "00 COMPLETE WORKFLOW")
            });
        }

        [CommandMethod("CE_TOOLS", "CE_PLATFORMPRODUCTIONCENTRE", CommandFlags.Modal)]
        public void PlatformProduction()
        {
            August11DisciplineStylePresetManager.ActivateForProduction(Active() == null ? null : Active().Database, "Platforms");
            RunCentre("PLATFORM PRODUCTION", "Source polygons → feature lines → levels → grading → setting-out → quantities → drawings.", new[]
            {
                Action("SETTINGS - Platform Styles", "CE_PLATFORMSTYLES", "Select feature-line, grading, surface and annotation styles.", "01 SETTINGS"),
                Action("PREPARE - Create Feature Lines", "CE_FLCREATE", "Create multiple feature lines from selected polylines.", "02 PREPARE"),
                Action("DESIGN - Grading & Slopes", "CE_GRADINGSLOPETOOLS", "Open grade-to-surface, constant-grade, slope/crossfall annotation and grading review tools.", "04 DESIGN"),
                Action("Platform Slopes / Levels", "CE_PLATFORMSLOPE", "Constant slope, fixed slope or flatten to highest elevation.", "04 DESIGN"),
                Action("Grade to Surface - Cut / Fill Slopes", "CE_PLATFORMGRADETOSURFACE", "Create dynamic daylight grading using separate cut and fill H:V slopes.", "04 DESIGN"),
                Action("Platform Infill + Daylight", "CE_PLATFORMINFILL", "Create infill for all or multiple selected closed platform feature lines, then continue to dynamic cut/fill daylight grading.", "04 DESIGN"),
                Action("Stepped Offsets", "CE_PLATFORMSTEPOFFSETS", "Create linked stepped offsets for multiple platforms.", "04 DESIGN"),
                Action("Drape / Platform Surface", "CE_PLATFORMDRAPE", "Drape linked platform controls to selected surface.", "04 DESIGN"),
                Action("COMPLETE - Setting-Out", "CE_PLATFORMSETTINGOUT", "Vertex/grid setting-out and linked tables.", "05 COMPLETE"),
                Action("Platform Names / Register", "CE_PLATFORMTABLE", "Linked platform names, elevations and register.", "05 COMPLETE"),
                Action("DELIVER - Cut / Fill", "CE_PLATFORMCUTFILL", "Linked NG versus design quantities.", "06 DELIVER"),
                Action("Drawings / Sections", "CE_PLATFORMDRAWINGS", "Create platform layouts and section source lines.", "06 DELIVER"),
                Action("Merge road / grading outputs", "CE_ROADSURFACEMERGE", "Create CE Top All, CE Bottom All or CE Final Surface without changing numbered source surfaces.", "05 COMPLETE"),
                Action("Merge all junction surfaces", "CE_JUNCTIONSURFACEMERGE", "Combine the CE junction grading and infill surfaces into CE Junctions All.", "05 COMPLETE"),
                Action("FINAL PLATFORM PRODUCTION", "CE_FINALPLATFORMPRODUCTION", "45-step approved Final Platform Production sequence.", "00 COMPLETE WORKFLOW"),
                Action("▶ RUN COMPLETE PLATFORM PRODUCTION", "CE_PLATFORMWORKFLOW", "Open one ordered, non-duplicated platform workflow from boundaries through drawings.", "00 COMPLETE WORKFLOW")
            });
        }

        [CommandMethod("CE_TOOLS", "CE_ROADPRODUCTIONCENTRE", CommandFlags.Modal)]
        public void RoadProduction()
        {
            Document document = Active();
            if (document == null) return;
            August11DisciplineStylePresetManager.ActivateForProduction(Active() == null ? null : Active().Database, "Roads");
            document.SendStringToExecute("CE_ROADPRODUCTIONV2 ", true, false, true);
        }

        [CommandMethod("CE_TOOLS", "CE_SWPRODUCTIONCENTRE", CommandFlags.Modal)]
        public void StormwaterProduction()
        {
            August11DisciplineStylePresetManager.ActivateForProduction(Active() == null ? null : Active().Database, "Stormwater");
            RunCentre("STORMWATER PRODUCTION", "Roads/Existing Ground → route/network/branches → hydraulic/design checks → profiles/labels/setting-out/BOQ → drawings/report.", new[]
            {
                Action("SETTINGS - Stormwater Settings", "CE_SWSETTINGS", "Choose stormwater gravity-network parts, pipe/structure styles, rule sets, alignment/profile/profile-view/band styles and labels.", "01 SETTINGS"),
                Action("Project Styles - Stormwater", "CE_SWSTYLES", "Civil 3D styles and parts used only by Stormwater production.", "01 SETTINGS"),
                Action("PREPARE - Utility Route from Road Reserve", "CE_UTILITYFROMROADRESERVE", "Create preliminary stormwater route geometry from road-reserve/source geometry.", "02 PREPARE"),
                Action("CREATE - Multiple Networks from Polylines", "CE_NETWORKFROMPOLYLINESBATCH", "Create multiple gravity networks from selected source polylines without duplicate source runs.", "03 CREATE"),
                Action("Sequence Branches / Structures / Pipes", "CE_SWSEQ", "Build the stormwater main/branch sequence and network naming.", "03 CREATE"),
                Action("DESIGN - Network Data / Levels", "CE_NETWORKDATA", "Review stormwater pipe/structure levels, lengths, slopes and connected flow.", "04 DESIGN"),
                Action("COMPLETE - Alignments", "CE_SWALIGN", "Create or refresh linked stormwater branch alignments.", "05 COMPLETE"),
                Action("Safe Alignment Fallback", "CE_SWALIGNSAFE", "Create direct Civil alignments when duplicate or zero-length source geometry prevents the normal route.", "05 COMPLETE"),
                Action("Profiles", "CE_SWPROFILE", "Create stormwater profiles/profile views with stormwater-specific styles and gravity-network data.", "05 COMPLETE"),
                Action("Pipe / Structure Labels", "CE_SWLABELS", "Apply stormwater pipe and structure labels without duplicating existing labels.", "05 COMPLETE"),
                Action("Setting-Out", "CE_VERTEXSETTINGOUT", "Create linked stormwater setting-out and leaders.", "05 COMPLETE"),
                Action("BOQ", "CE_BOQSTORMWATER", "Create linked stormwater quantities.", "05 COMPLETE"),
                Action("Stormwater Supplementary", "CE_STORMWATERFIELDSUPPLEMENTARY", "Open stormwater-specific field and supplementary utilities.", "05 COMPLETE"),
                Action("DELIVER - Stormwater Report", "CE_REPORTSTORMWATER", "Generate the stormwater report/drawing handoff.", "06 DELIVER"),
                Action("▶ RUN COMPLETE STORMWATER PRODUCTION", "CE_STORMWATERWORKFLOW", "Open one ordered, non-duplicated stormwater workflow from parts/network through delivery.", "00 COMPLETE WORKFLOW")
            });
        }

        [CommandMethod("CE_TOOLS", "CE_SEWERPRODUCTIONCENTRE", CommandFlags.Modal)]
        public void SewerProduction()
        {
            August11DisciplineStylePresetManager.ActivateForProduction(Active() == null ? null : Active().Database, "Sewer");
            RunCentre("SEWER PRODUCTION", "Cadastral/Roads/Existing Ground → route/network/branches → levels/checks → profiles/labels/setting-out/BOQ → drawings/report.", new[]
            {
                Action("SETTINGS - Sewer Settings", "CE_SEWSETTINGS", "Parts, styles, labels, profile and band settings.", "01 SETTINGS"),
                Action("Project Styles - Sewer", "CE_SEWERSTYLES", "Civil 3D styles used only by Sewer production.", "01 SETTINGS"),
                Action("PREPARE - Midblock / Road-Reserve Route", "CE_MIDBLOCKSEWERPRODUCTION", "Continuous selected-side/low-side sewer routes and planning manholes.", "02 PREPARE"),
                Action("CREATE - Multiple Networks from Polylines", "CE_NETWORKFROMPOLYLINESBATCH", "Select many source polylines and create them without duplicate source runs.", "03 CREATE"),
                Action("Sequence Branches / Structures / Pipes", "CE_SEWSEQ", "Build the live network sequence and branch names.", "03 CREATE"),
                Action("DESIGN - Network Data / Levels", "CE_NETWORKDATA", "Review pipe/structure levels, lengths and slopes.", "04 DESIGN"),
                Action("COMPLETE - Alignments", "CE_SEWALIGN", "Create linked branch alignments.", "05 COMPLETE"),
                Action("Flow toward Outlet / Low Point", "CE_SEWFLOWTOOUTLET", "Set gravity-pipe flow from connected structure topology toward one selected downstream structure while preserving endpoints and existing rule sets.", "05 COMPLETE"),
                Action("Profiles", "CE_SEWPROFILE", "Create isolated branch profiles/profile views with automatic band import.", "05 COMPLETE"),
                Action("Long-Section Bands", "CE_SEWBANDLABELS", "Apply or repair sewer band sets on multiple selected profile views with native ground-profile and gravity-network source binding.", "05 COMPLETE"),
                Action("Pipe Network Band Groups", "CE_SEWPIPEBANDGROUPS", "Repair PipeNetwork Band Label Group generation and gravity-network Data Source links on multiple profile views.", "05 COMPLETE"),
                Action("Edit One Profile View + Apply to Selected", "CE_PROFILEVIEWEDITMATCH", "Edit one native Profile View Properties window and copy the accepted style/range/band changes to all selected target views.", "05 COMPLETE"),
                Action("Band Data Sources", "CE_PROFILEVIEWDATASOURCES", "Batch-fill blank Profile 1/Profile 2/Pipe Network Data Source fields on selected profile views.", "05 COMPLETE"),
                Action("Labels", "CE_SEWLABELS", "Apply selected pipe/structure labels and branch presentation.", "05 COMPLETE"),
                Action("Setting-Out", "CE_VERTEXSETTINGOUT", "Linked setting-out for design geometry.", "05 COMPLETE"),
                Action("BOQ", "CE_BOQSEWER", "Create linked sewer quantities.", "05 COMPLETE"),
                Action("DELIVER - Sewer Report", "CE_REPORTSEWER", "Generate sewer design report/drawing handoff.", "06 DELIVER"),
                Action("FINAL SEWER PRODUCTION", "CE_FINALSEWERPRODUCTION", "34-step approved Final Sewer Production sequence.", "00 COMPLETE WORKFLOW"),
                Action("▶ RUN COMPLETE SEWER PRODUCTION", "CE_SEWERWORKFLOW", "Open one ordered, non-duplicated sewer workflow from route/parts through delivery.", "00 COMPLETE WORKFLOW")
            });
        }

        [CommandMethod("CE_TOOLS", "CE_WATERPRODUCTIONCENTRE", CommandFlags.Modal)]
        public void WaterProduction()
        {
            August11DisciplineStylePresetManager.ActivateForProduction(Active() == null ? null : Active().Database, "Water");
            RunCentre("WATER PRODUCTION", "Roads/Existing Ground → pressure route/network → mains/branches → profiles/pressure-part labels/assets → setting-out/BOQ → drawings/report.", new[]
            {
                Action("FINAL WATER PRODUCTION", "CE_FINALWATERPRODUCTION", "Open the approved 31-step numbered Water production workflow; run commands individually to review the drawing between stages.", "00 FINAL PRODUCTION"),
                Action("SETTINGS - Water Settings", "CE_WATERSETTINGS", "Choose pressure-network parts, pressure-pipe/fitting/appurtenance styles, alignment/profile/profile-view/band styles and labels.", "01 SETTINGS"),
                Action("Project Styles - Water", "CE_WATERSTYLES", "Civil 3D pressure-network and annotation styles used only by Water production.", "01 SETTINGS"),
                Action("PREPARE - Utility Route from Road Reserve", "CE_UTILITYFROMROADRESERVE", "Create preliminary water-main route geometry from road-reserve/source geometry.", "02 PREPARE"),
                Action("CREATE - Water Network from Multiple Polylines", "CE_WATERNETWORKMULTI", "Create one Water gravity network from all or selected polylines with target surface and Water parts.", "03 CREATE"),
                Action("Sequence Mains / Branches", "CE_WATERSEQ", "Build W-MAIN and water branch route order.", "03 CREATE"),
                Action("DESIGN - Network Data / Levels", "CE_NETWORKDATA", "Review water pressure pipes, fittings, appurtenances and design levels.", "04 DESIGN"),
                Action("COMPLETE - Alignments", "CE_WATERALIGN", "Create or refresh linked water alignments.", "05 COMPLETE"),
                Action("Water Branch Names", "CE_WATERBRANCHNAMES", "Add names to all or selected Water alignments without recreating them.", "05 COMPLETE"),
                Action("Link Water Gravity Parts to Surface", "CE_SEWLINKSURFACE", "Shared gravity-network surface / slope command; select Water networks only.", "04 DESIGN"),
                Action("Fit All Profile Views", "CE_PROFILEVIEWFITALL", "Fit all profile views.", "05 COMPLETE"),
                Action("Arrange Multiple Profile Views", "CE_PROFILEVIEWARRANGE", "Arrange selected profile views.", "05 COMPLETE"),
                Action("Profiles", "CE_WATERPROFILE", "Create water profiles/profile views and pressure-part projection where supported.", "05 COMPLETE"),
                Action("Repair Water Profile 1 / Profile 2 Bands", "CE_WATERBANDFIX", "Repair band profile links in existing Water views.", "05 COMPLETE"),
                Action("Water Gravity Pipes / Structures in Profile Views", "CE_WATERGRAVITYPROFILEPARTS", "Draw all or selected Water gravity parts in matching Water profile views.", "05 COMPLETE"),
                Action("Match Water Profile View Properties", "CE_PROFILEVIEWMATCH", "Match profile view styles and band settings across Water views.", "05 COMPLETE"),
                Action("Safe Profile Fallback", "CE_WATERPROFILESAFE", "Create direct surface profiles/profile views when pressure-part projection is unavailable.", "05 COMPLETE"),
                Action("Pressure-Part Labels", "CE_WATERLABELS", "Apply water pressure-pipe, fitting and appurtenance labels.", "05 COMPLETE"),
                Action("Water Gravity Pipe / Structure Labels", "CE_WATERGRAVITYLABELS", "Create Water-specific gravity pipe and structure labels without changing Sewer settings.", "05 COMPLETE"),
                Action("Number Water Pipes / Structures by Branch", "CE_WATERPARTNUMBER", "Preview or apply P1.1 / N1.1 part names along Water branch alignments.", "05 COMPLETE"),
                Action("Water Pipe / Structure Profile Label Styles", "CE_SEWPROFILELABELSTYLESMULTI", "Apply native gravity pipe and structure profile label styles to selected Water profile views.", "05 COMPLETE"),
                Action("Water Profile View Data Sources", "CE_PROFILEVIEWDATASOURCES", "Review profile and network data source links in selected Water profile views.", "05 COMPLETE"),
                Action("Water Profile Sheets", "CE_WATERPROFILESHEETS", "Prepare Water profile layouts and fit selected Water profile views with existing scale controls.", "06 DELIVER"),
                Action("Water Sheets - Import Layouts", "CE_IMPORTSHEETS", "Import selected paper-space layouts and their viewport content from another drawing.", "06 DELIVER"),
                Action("Valve / Hydrant Review", "CE_WATERPLACE", "Place linked valve and hydrant review markers/assets.", "05 COMPLETE"),
                Action("Setting-Out", "CE_VERTEXSETTINGOUT", "Create linked water setting-out and leaders.", "05 COMPLETE"),
                Action("Paper Space Black and White", "CE_PAPERMONO", "Match paper-space layout appearance to monochrome plot preview without changing entity colours.", "06 DELIVER"),
                Action("BOQ", "CE_BOQWATER", "Create linked water quantities.", "05 COMPLETE"),
                Action("Water Supplementary", "CE_WATERFIELDSUPPLEMENTARY", "Open water-specific field and supplementary utilities.", "05 COMPLETE"),
                Action("DELIVER - Water Report", "CE_REPORTWATER", "Generate the water report/drawing handoff.", "06 DELIVER"),
                Action("▶ RUN COMPLETE WATER PRODUCTION", "CE_WATERWORKFLOW", "Open one ordered, non-duplicated water workflow from pressure parts/network through delivery.", "00 COMPLETE WORKFLOW")
            });
        }

        [CommandMethod("CE_TOOLS", "CE_BULKWATERPRODUCTIONCENTRE", CommandFlags.Modal)]
        public void BulkWaterProduction()
        {
            August11DisciplineStylePresetManager.ActivateForProduction(Active() == null ? null : Active().Database, "Bulk Water");
            RunCentre("BULK WATER PRODUCTION", "Road-reserve/source route → pressure network → profile/setting-out → quantities → delivery.", new[]
            {
                Action("SETTINGS - Bulk Water Styles", "CE_BULKWATERSTYLES", "Select pressure-network and profile styles.", "01 SETTINGS"),
                Action("PREPARE - Utility Route from Erf / Road Reserve", "CE_UTILITYROUTEOFFSET", "Create bulk-water planning routes at selected offsets from erf, reserve-edge or road-centre geometry.", "02 PREPARE"),
                Action("CREATE - Multiple Networks from Polylines", "CE_NETWORKFROMPOLYLINESBATCH", "Batch source polylines into pressure network creation.", "03 CREATE"),
                Action("DESIGN - Network Data", "CE_NETWORKDATA", "Review selected pressure-network objects and levels.", "04 DESIGN"),
                Action("COMPLETE - Setting-Out", "CE_VERTEXSETTINGOUT", "Generate linked setting-out.", "05 COMPLETE"),
                Action("BOQ", "CE_BOQBULKWATER", "Create linked bulk-water quantities.", "05 COMPLETE"),
                Action("DELIVER - Bulk Water Report", "CE_REPORTBULKWATER", "Generate bulk-water design report.", "06 DELIVER"),
                Action("▶ RUN COMPLETE BULK WATER PRODUCTION", "CE_BULKWATERWORKFLOW", "Open one ordered, non-duplicated Bulk Water workflow from route/network through delivery.", "00 COMPLETE WORKFLOW")
            });
        }

        [CommandMethod("CE_TOOLS", "CE_PARKINGPRODUCTIONCENTRE", CommandFlags.Modal)]
        public void ParkingProduction()
        {
            August11DisciplineStylePresetManager.ActivateForProduction(Active() == null ? null : Active().Database, "Parking");
            RunCentre("PARKING AREA PRODUCTION", "Boundary → alternatives/layout → grading → checks/setting-out → quantities → drawings.", new[]
            {
                Action("SETTINGS - Parking Tools", "CE_PKTOOLS", "Parking layout and annotation settings.", "01 SETTINGS"),
                Action("Project Styles - Parking", "CE_PARKINGSTYLES", "Civil 3D styles used only by Parking production.", "01 SETTINGS"),
                Action("PREPARE - Parking Options", "CE_PARKOPTIONS", "Compare parking arrangements inside a selected boundary.", "02 PREPARE"),
                Action("CREATE - Parking Optimiser", "CE_PARKOPTIMIZE", "Create obstacle-aware parking alternative.", "03 CREATE"),
                Action("DESIGN - Parking Grading", "CE_PARKGRADETOOLS", "Create linked grading/drainage guides.", "04 DESIGN"),
                Action("COMPLETE - Skew / Width Validation", "CE_PKSKVALIDATE", "Check perpendicular bay width and skew.", "05 COMPLETE"),
                Action("Setting-Out", "CE_GRIDSETTINGOUT", "Grid/perimeter setting-out where applicable.", "05 COMPLETE"),
                Action("DELIVER - Parking Quantities", "CE_STANDARDQTYTOOLS", "Create parking/layerwork quantity outputs.", "06 DELIVER"),
                Action("▶ RUN COMPLETE PARKING PRODUCTION", "CE_PARKINGWORKFLOW", "Open one ordered, non-duplicated Parking workflow from boundary through quantities.", "00 COMPLETE WORKFLOW")
            });
        }

        [CommandMethod("CE_TOOLS", "CE_FLOODPRODUCTIONCENTRE", CommandFlags.Modal)]
        public void FloodProduction()
        {
            August11DisciplineStylePresetManager.ActivateForProduction(Active() == null ? null : Active().Database, "Flood");
            RunCentre("FLOOD PRODUCTION", "Existing ground/catchment → hydrology → flow/affected area → culvert review → outputs.", new[]
            {
                Action("SETTINGS - Hydrology / Flood Inputs", "CE_HYDROLOGYTOOLS", "Review rainfall/runoff and analysis settings.", "01 SETTINGS"),
                Action("Project Styles - Flood", "CE_FLOODSTYLES", "Civil 3D styles used only by Flood production.", "01 SETTINGS"),
                Action("PREPARE - Surface / Catchment Review", "CE_SURFTOOLS", "Review the terrain source before flood calculations.", "02 PREPARE"),
                Action("CREATE - Quick Flood / Rational Review", "CE_CATCHMENTQUICK", "Pre/post return-period peak-flow and preliminary culvert screen.", "03 CREATE"),
                Action("DESIGN - Surface Hydrology", "CE_HYDROLOGYTOOLS", "Flow routes, catchments and terrain storage review.", "04 DESIGN"),
                Action("Affected Property / Flood Results", "CE_FLOODRESULTTOOLS", "Review imported specialist flood results and affected properties.", "04 DESIGN"),
                Action("COMPLETE - Flood Catchment & Culvert Design", "CE_FLOODCULVERTDESIGN", "Safely sample the TIN surface and selected crossing before catchment/culvert output.", "05 COMPLETE"),
                Action("COMPLETE - Culvert Review", "CE_CULVERTREVIEW", "Review candidate crossings/culvert requirements.", "05 COMPLETE"),
                Action("DELIVER - Flood Report", "CE_REPORTFULL", "Generate project/discipline report output.", "06 DELIVER"),
                Action("▶ RUN COMPLETE FLOOD PRODUCTION", "CE_FLOODWORKFLOW", "Open one ordered, non-duplicated Flood workflow from terrain through delivery.", "00 COMPLETE WORKFLOW")
            });
        }

        private static IEnumerable<DisciplineWorkflowAction> UtilityActions(string discipline, string settings, string sequence, string alignment, string profile, string boq, string report, string hub)
        {
            return new[]
            {
                Action("SETTINGS - " + discipline + " Settings", settings, "Parts, styles, labels and profile settings.", "01 SETTINGS"),
                Action("Project Styles - " + discipline,
                    string.Equals(discipline, "Stormwater", StringComparison.OrdinalIgnoreCase) ? "CE_SWSTYLES" : "CE_WATERSTYLES",
                    "Civil 3D styles used only by " + discipline + " production.",
                    "01 SETTINGS"),
                Action("PREPARE - Utility Route Planner", "CE_UTILITYROUTEOFFSET", "Create a preliminary route from erf, reserve-edge or road-centre geometry at a selected offset.", "02 PREPARE"),
                Action("CREATE - Multiple Networks from Polylines", "CE_NETWORKFROMPOLYLINESBATCH", "Batch multiple source objects into network creation.", "03 CREATE"),
                Action("Sequence / Branches", sequence, "Create the discipline network sequence.", "03 CREATE"),
                Action("DESIGN - Network Data", "CE_NETWORKDATA", "Review levels, sizes, slopes and connected parts.", "04 DESIGN"),
                Action("COMPLETE - Alignments", alignment, "Create linked branch/route alignments.", "05 COMPLETE"),
                Action("Profiles", profile, "Create profile views and apply/import requested bands.", "05 COMPLETE"),
                Action("BOQ", boq, "Create linked discipline quantities.", "05 COMPLETE"),
                Action("DELIVER - Design Report", report, "Generate discipline report/drawing handoff.", "06 DELIVER"),
                Action("▶ RUN COMPLETE " + discipline.ToUpperInvariant() + " PRODUCTION", hub, "Open the complete discipline workflow.", "99 RUN COMPLETE")
            };
        }

        private static void RunCentre(string title, string description, IEnumerable<DisciplineWorkflowAction> actions)
        {
            Document document = Active();
            if (document == null) return;
            DisciplineWorkflowDialogs.SelectAndRun(document, title, description, actions.ToList());
        }

        private static DisciplineWorkflowAction Action(string title, string command, string description, string group)
        {
            return new DisciplineWorkflowAction(title, command, description, group);
        }

        private static Document Active()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
        }
    }

    internal static class CeThemeStore
    {
        private static string FilePath
        {
            get
            {
                string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CE Tools");
                Directory.CreateDirectory(folder);
                return Path.Combine(folder, "Theme.txt");
            }
        }

        internal static string Read()
        {
            try
            {
                string value = File.Exists(FilePath) ? File.ReadAllText(FilePath).Trim() : "Dark";
                return string.Equals(value, "Light", StringComparison.OrdinalIgnoreCase) ? "Light" : "Dark";
            }
            catch { return "Dark"; }
        }

        internal static void Write(string value)
        {
            try { File.WriteAllText(FilePath, string.Equals(value, "Light", StringComparison.OrdinalIgnoreCase) ? "Light" : "Dark"); }
            catch { }
        }
    }

    internal sealed class CeWelcomeWindow : Window
    {
        internal string SelectedCommand { get; private set; }

        internal CeWelcomeWindow(string theme)
        {
            Title = "Welcome to CE Tools";
            Width = 760;
            Height = 470;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            WindowState = WindowState.Maximized;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            ShowInTaskbar = false;

            bool light = string.Equals(theme, "Light", StringComparison.OrdinalIgnoreCase);
            Brush background = light ? Brushes.WhiteSmoke : new SolidColorBrush(Color.FromRgb(15, 19, 27));
            Brush card = light ? Brushes.White : new SolidColorBrush(Color.FromRgb(28, 35, 49));
            Brush foreground = light ? Brushes.Black : Brushes.White;
            Brush muted = light ? Brushes.DimGray : Brushes.LightGray;
            Brush accent = new SolidColorBrush(Color.FromRgb(20, 122, 220));

            var root = new Grid { Background = background, Margin = new Thickness(0) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var heading = new StackPanel { Margin = new Thickness(34, 28, 34, 18) };
            heading.Children.Add(new TextBlock { Text = "WELCOME TO CE TOOLS", FontSize = 29, FontWeight = FontWeights.Bold, Foreground = foreground });
            heading.Children.Add(new TextBlock { Text = "ENGINEERING INTELLIGENCE · LESS CLICKING · MORE ENGINEERING", FontSize = 12, Margin = new Thickness(0, 8, 0, 0), Foreground = muted });
            Grid.SetRow(heading, 0);
            root.Children.Add(heading);

            var cards = new Grid { Margin = new Thickness(34, 8, 34, 20) };
            cards.ColumnDefinitions.Add(new ColumnDefinition());
            cards.ColumnDefinitions.Add(new ColumnDefinition());
            cards.Children.Add(BuildCard("CE-PRODUCTION CENTRE", "Important commands only. Guided discipline production workflows, with Roads split into Settings, Layout Production and Design Production.", "CE_PRODUCTIONCENTRE", card, foreground, muted, accent, 0));
            cards.Children.Add(BuildCard("CE-ENGINEERING CENTRE", "The complete CE Tools command library, utilities, reports, repair tools and advanced engineering workflows.", "CE_ENGINEERINGINTELLIGENCECENTRE", card, foreground, muted, accent, 1));
            Grid.SetRow(cards, 1);
            root.Children.Add(cards);

            var bottom = new DockPanel { Margin = new Thickness(34, 0, 34, 24), LastChildFill = false };
            var themeButton = new Button { Content = light ? "Switch to Dark" : "Switch to Light", MinWidth = 130, Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 10, 0) };
            themeButton.Click += delegate
            {
                CeThemeStore.Write(light ? "Dark" : "Light");
                SelectedCommand = "CE_WELCOME";
                DialogResult = true;
            };
            DockPanel.SetDock(themeButton, Dock.Left);
            bottom.Children.Add(themeButton);
            var close = new Button { Content = "Close", MinWidth = 90, Padding = new Thickness(12, 7, 12, 7) };
            close.Click += delegate { DialogResult = false; };
            DockPanel.SetDock(close, Dock.Right);
            bottom.Children.Add(close);
            Grid.SetRow(bottom, 2);
            root.Children.Add(bottom);
            Content = root;
        }

        private Border BuildCard(string title, string description, string command, Brush card, Brush foreground, Brush muted, Brush accent, int column)
        {
            var border = new Border
            {
                Background = card,
                CornerRadius = new CornerRadius(7),
                Margin = new Thickness(column == 0 ? 0 : 10, 0, column == 0 ? 10 : 0, 0),
                Padding = new Thickness(24),
                BorderBrush = accent,
                BorderThickness = new Thickness(1)
            };
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeights.Bold, Foreground = foreground, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new TextBlock { Text = description, FontSize = 13, Foreground = muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 18, 0, 24), MinHeight = 82 });
            var button = new Button { Content = "OPEN CENTRE", Padding = new Thickness(14, 9, 14, 9), FontWeight = FontWeights.SemiBold };
            button.Click += delegate { SelectedCommand = command; DialogResult = true; };
            panel.Children.Add(button);
            border.Child = panel;
            Grid.SetColumn(border, column);
            return border;
        }
    }

    /// <summary>
    /// Creates one dedicated CE-PRODUCTION ribbon tab containing all discipline
    /// launchers as horizontal panels. AutoCAD overflows separate native RibbonTabs
    /// when the tab strip is too narrow; a single tab keeps the production launchers
    /// together and avoids hiding disciplines in the tab overflow menu.
    /// </summary>
    internal static class ProductionWorkflowRibbonBuilder
    {
        private sealed class ProductionTabDefinition
        {
            public string Id;
            public string Title;
            public string ButtonText;
            public string Description;
            public string Command;
        }

        // The established production order is retained from left to right.
        private static readonly ProductionTabDefinition[] Definitions =
        {
            new ProductionTabDefinition { Id = "CE_PRODUCTION_PROJECT_TAB", Title = "CE-PROJECT PRODUCTION", ButtonText = "CE-PROJECT WORKFLOW", Description = "Open the complete Project Production workflow. Individual specialist commands remain in the CE Tools Engineering Centre.", Command = "CE_PROJECTWORKFLOW " },
            new ProductionTabDefinition { Id = "CE_PRODUCTION_SURVEY_TAB", Title = "CE-SURVEY PRODUCTION", ButtonText = "CE-SURVEY WORKFLOW", Description = "Open the complete Survey Production workflow. Individual specialist commands remain in the CE Tools Engineering Centre.", Command = "CE_SURVEYWORKFLOW " },
            new ProductionTabDefinition { Id = "CE_PRODUCTION_PLATFORM_TAB", Title = "CE-PLATFORM PRODUCTION", ButtonText = "CE-FINAL PLATFORM PRODUCTION", Description = "Open the approved Final Platform Production sequence.", Command = "CE_FINALPLATFORMPRODUCTION " },
            new ProductionTabDefinition { Id = "CE_PRODUCTION_ROAD_TAB", Title = "CE-ROAD PRODUCTION", ButtonText = "CE-FINAL ROAD PRODUCTION", Description = "Open the approved Final Road Production sequence.", Command = "CE_FINALROADPRODUCTION " },
            new ProductionTabDefinition { Id = "CE_PRODUCTION_STORMWATER_TAB", Title = "CE-STORMWATER PRODUCTION", ButtonText = "CE-STORMWATER WORKFLOW", Description = "Open the complete Stormwater Production workflow. Individual specialist commands remain in the CE Tools Engineering Centre.", Command = "CE_STORMWATERWORKFLOW " },
            new ProductionTabDefinition { Id = "CE_PRODUCTION_SEWER_TAB", Title = "CE-SEWER PRODUCTION", ButtonText = "CE-FINAL SEWER PRODUCTION", Description = "Open the approved Final Sewer Production sequence.", Command = "CE_FINALSEWERPRODUCTION " },
            new ProductionTabDefinition { Id = "CE_PRODUCTION_WATER_TAB", Title = "CE-WATER PRODUCTION", ButtonText = "CE-FINAL WATER PRODUCTION", Description = "Open the approved Final Water Production sequence.", Command = "CE_FINALWATERPRODUCTION " },
            new ProductionTabDefinition { Id = "CE_PRODUCTION_BULKWATER_TAB", Title = "CE-BULK WATER PRODUCTION", ButtonText = "CE-BULK WATER WORKFLOW", Description = "Open the complete Bulk Water Production workflow. Individual specialist commands remain in the CE Tools Engineering Centre.", Command = "CE_BULKWATERWORKFLOW " },
            new ProductionTabDefinition { Id = "CE_PRODUCTION_PARKING_TAB", Title = "CE-PARKING PRODUCTION", ButtonText = "CE-PARKING WORKFLOW", Description = "Open the complete Parking Production workflow. Individual specialist commands remain in the CE Tools Engineering Centre.", Command = "CE_PARKINGWORKFLOW " },
            new ProductionTabDefinition { Id = "CE_PRODUCTION_FLOOD_TAB", Title = "CE-FLOOD PRODUCTION", ButtonText = "CE-FLOOD WORKFLOW", Description = "Open the complete Flood Production workflow. Individual specialist commands remain in the CE Tools Engineering Centre.", Command = "CE_FLOODWORKFLOW " },
            new ProductionTabDefinition { Id = "CE_PRODUCTION_QUANTITY_TAB", Title = "CE-QUANTITY PRODUCTION", ButtonText = "CE-QUANTITY CENTRE", Description = "Open the Quantity Production Centre.", Command = "CE_BOQCENTER " },
            new ProductionTabDefinition { Id = "CE_PRODUCTION_REPORT_TAB", Title = "CE-REPORT PRODUCTION", ButtonText = "CE-REPORT CENTRE", Description = "Open the Report Production Centre.", Command = "CE_REPORTCENTER " }
        };

        internal const string HorizontalProductionTabId = "CE_PRODUCTION_HORIZONTAL_TAB";
        private static readonly List<RibbonPanel> ProductionPanels = new List<RibbonPanel>();
        private static readonly List<RibbonButton> ProductionButtons = new List<RibbonButton>();
        private static RibbonControl ObservedRibbon;
        private static FrameworkElement ObservedRibbonElement;
        private static bool RibbonLayoutHandlersAttached;
        private static bool LayoutUpdateQueued;

        internal static bool EnsureCreated()
        {
            RibbonControl ribbon = ComponentManager.Ribbon;
            if (ribbon == null) return false;

            // Remove the earlier separate discipline tabs. AutoCAD was placing most
            // of them in its tab overflow menu on narrower displays.
            foreach (ProductionTabDefinition definition in Definitions)
            {
                RibbonTab oldTab = ribbon.Tabs.FirstOrDefault(item => item.Id == definition.Id);
                if (oldTab != null) ribbon.Tabs.Remove(oldTab);
            }

            RibbonTab legacy = ribbon.Tabs.FirstOrDefault(item =>
                item.Id == "CE_TOOLS_PRODUCTION_WORKFLOW_TAB");
            if (legacy != null) ribbon.Tabs.Remove(legacy);

            RibbonTab tab = ribbon.Tabs.FirstOrDefault(item => item.Id == HorizontalProductionTabId);
            if (tab == null)
            {
                tab = new RibbonTab
                {
                    Id = HorizontalProductionTabId,
                    Title = "CE-PRODUCTION"
                };
                ribbon.Tabs.Add(tab);
            }
            else
            {
                tab.Title = "CE-PRODUCTION";
            }

            // Rebuild in the exact approved left-to-right order.
            tab.Panels.Clear();
            ProductionPanels.Clear();
            ProductionButtons.Clear();

            FrameworkElement ribbonElement = ribbon as FrameworkElement;
            if (!ReferenceEquals(ObservedRibbonElement, ribbonElement)) Detach();
            ObservedRibbon = ribbon;
            ObservedRibbonElement = ribbonElement;
            if (ribbonElement != null && !RibbonLayoutHandlersAttached)
            {
                // Listen to size changes and tab clicks/keyboard navigation. The
                // layout updater itself checks the active tab, so no other ribbon
                // tab is resized or otherwise modified.
                ribbonElement.SizeChanged += OnRibbonSizeChanged;
                ribbonElement.PreviewMouseLeftButtonUp += OnRibbonTabInput;
                ribbonElement.PreviewKeyUp += OnRibbonTabKeyInput;
                RibbonLayoutHandlersAttached = true;
            }

            foreach (ProductionTabDefinition definition in Definitions)
            {
                var source = new RibbonPanelSource
                {
                    Id = definition.Id + "_PANEL",
                    Title = definition.Title
                };

                var button = new RibbonButton
                {
                    Id = definition.Id + "_BUTTON",
                    Text = WrapProductionRibbonLabel(definition.ButtonText),
                    ShowText = true,
                    ShowImage = true,
                    Orientation = Orientation.Vertical,
                    Image = RibbonVisuals.Small("CE_TOOLS_HOME_MENU"),
                    LargeImage = RibbonVisuals.Large("CE_TOOLS_HOME_MENU"),
                    Size = RibbonItemSize.Large,
                    ToolTip = definition.Description,
                    CommandParameter = definition.Command,
                    CommandHandler = new ProductionRibbonCommandHandler()
                };

                source.Items.Add(button);
                var panel = new RibbonPanel { Source = source };
                tab.Panels.Add(panel);
                ProductionPanels.Add(panel);
                ProductionButtons.Add(button);
            }

            QueueProductionRibbonLayout();
            return true;
        }

        internal static void Detach()
        {
            if (ObservedRibbonElement != null && RibbonLayoutHandlersAttached)
            {
                ObservedRibbonElement.SizeChanged -= OnRibbonSizeChanged;
                ObservedRibbonElement.PreviewMouseLeftButtonUp -= OnRibbonTabInput;
                ObservedRibbonElement.PreviewKeyUp -= OnRibbonTabKeyInput;
            }
            RibbonLayoutHandlersAttached = false;
            ObservedRibbonElement = null;
            ObservedRibbon = null;
        }

        private static void OnRibbonSizeChanged(object sender, SizeChangedEventArgs args)
        {
            QueueProductionRibbonLayout();
        }

        private static void OnRibbonTabInput(object sender, MouseButtonEventArgs args)
        {
            QueueProductionRibbonLayout();
        }

        private static void OnRibbonTabKeyInput(object sender, KeyEventArgs args)
        {
            QueueProductionRibbonLayout();
        }

        private static void QueueProductionRibbonLayout()
        {
            FrameworkElement ribbonElement = ObservedRibbonElement;
            if (ribbonElement == null || LayoutUpdateQueued) return;
            LayoutUpdateQueued = true;
            ribbonElement.Dispatcher.BeginInvoke(new Action(delegate
            {
                LayoutUpdateQueued = false;
                ApplyEqualProductionWidths();
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        /// <summary>
        /// Expands only the CE-PRODUCTION tab. The available width is divided
        /// equally across its twelve panel hosts. We size the generated WPF
        /// panel containers as well as the Autodesk model objects because the
        /// latter are not FrameworkElements in every AutoCAD 2023 build.
        /// </summary>
        private static void ApplyEqualProductionWidths()
        {
            if (ProductionPanels.Count == 0 || ObservedRibbon == null ||
                ObservedRibbonElement == null || !IsProductionTabActive(ObservedRibbon))
                return;

            double availableWidth = ObservedRibbonElement.ActualWidth;
            if (double.IsNaN(availableWidth) || double.IsInfinity(availableWidth) || availableWidth <= 0.0)
                return;

            // Use the ribbon's live client width. A small allowance is retained
            // for the tab's own margins and separators; each panel gets the same
            // width and the final panel absorbs any fractional remainder.
            double usableWidth = Math.Max(0.0, availableWidth - 12.0);
            double panelWidth = usableWidth / ProductionPanels.Count;
            if (panelWidth < 60.0) return; // Do not force overflow on narrow windows.

            for (int i = 0; i < ProductionPanels.Count; i++)
            {
                double width = i == ProductionPanels.Count - 1
                    ? usableWidth - (panelWidth * (ProductionPanels.Count - 1))
                    : panelWidth;
                RibbonPanel panel = ProductionPanels[i];
                RibbonButton button = i < ProductionButtons.Count ? ProductionButtons[i] : null;

                SetRibbonObjectWidth(panel, width);
                SetRibbonObjectWidth(button, width - 2.0);
                ApplyWidthToPanelVisuals(ObservedRibbonElement, panel, width);
            }
        }

        private static bool IsProductionTabActive(RibbonControl ribbon)
        {
            if (ribbon == null) return false;

            // Autodesk.Windows versions expose the selected tab under different
            // property names. Resolve it at runtime to keep this source compatible
            // with the Civil 3D 2023 ribbon assembly.
            string[] ribbonProperties = { "ActiveTab", "SelectedTab", "SelectedItem" };
            foreach (string propertyName in ribbonProperties)
            {
                object selected = GetPropertyValue(ribbon, propertyName);
                if (selected != null)
                    return IsProductionTabObject(selected);
            }

            foreach (RibbonTab tab in ribbon.Tabs)
            {
                object selected = GetPropertyValue(tab, "IsSelected") ?? GetPropertyValue(tab, "IsActive");
                if (selected is bool && (bool)selected)
                    return IsProductionTabObject(tab);
            }
            return false;
        }

        private static bool IsProductionTabObject(object value)
        {
            if (value == null) return false;
            object id = GetPropertyValue(value, "Id");
            return string.Equals(Convert.ToString(id), HorizontalProductionTabId, StringComparison.OrdinalIgnoreCase);
        }

        private static object GetPropertyValue(object instance, string name)
        {
            if (instance == null) return null;
            try
            {
                var property = instance.GetType().GetProperty(name);
                return property == null || !property.CanRead ? null : property.GetValue(instance, null);
            }
            catch { return null; }
        }

        private static void SetRibbonObjectWidth(object item, double width)
        {
            if (item == null || width <= 0.0) return;
            SetWidthProperty(item, "Width", width);
            SetWidthProperty(item, "MinWidth", width);
            SetWidthProperty(item, "MaxWidth", width);

            FrameworkElement element = item as FrameworkElement;
            if (element != null)
                element.HorizontalAlignment = HorizontalAlignment.Stretch;
        }

        private static void SetWidthProperty(object item, string propertyName, double width)
        {
            try
            {
                var property = item.GetType().GetProperty(propertyName);
                if (property != null && property.CanWrite && property.PropertyType == typeof(double))
                    property.SetValue(item, width, null);
            }
            catch { }
        }

        private static void ApplyWidthToPanelVisuals(DependencyObject root, RibbonPanel panel, double width)
        {
            if (root == null || panel == null) return;
            RibbonPanelSource source = panel.Source;
            ApplyWidthToMatchingVisual(root, panel, source, width);

            int count;
            try { count = VisualTreeHelper.GetChildrenCount(root); }
            catch { return; }
            for (int i = 0; i < count; i++)
            {
                DependencyObject child;
                try { child = VisualTreeHelper.GetChild(root, i); }
                catch { continue; }
                ApplyWidthToPanelVisuals(child, panel, width);
            }
        }

        private static void ApplyWidthToMatchingVisual(DependencyObject candidate, RibbonPanel panel, RibbonPanelSource source, double width)
        {
            FrameworkElement element = candidate as FrameworkElement;
            if (element == null) return;

            bool matches = ReferenceEquals(candidate, panel) ||
                           ReferenceEquals(element.DataContext, panel) ||
                           (source != null && ReferenceEquals(element.DataContext, source));

            // Some Autodesk ribbon templates wrap the model object in a generated
            // presenter. Match its exposed Source/Panel property by object identity.
            if (!matches)
            {
                object visualPanel = GetPropertyValue(element, "Panel");
                object visualSource = GetPropertyValue(element, "Source");
                object visualModel = GetPropertyValue(element, "RibbonPanel");
                matches = ReferenceEquals(visualPanel, panel) ||
                          ReferenceEquals(visualModel, panel) ||
                          (source != null && ReferenceEquals(visualSource, source));
            }

            if (matches)
            {
                element.Width = width;
                element.MinWidth = width;
                element.MaxWidth = width;
                element.HorizontalAlignment = HorizontalAlignment.Stretch;
            }
        }

        private static string WrapProductionRibbonLabel(string text)
        {
            string value = (text ?? string.Empty).Trim();
            if (value.Length == 0) return value;

            const int maxLineLength = 11;
            var output = new List<string>();
            foreach (string originalWord in value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string word = originalWord;
                if (word.Length > maxLineLength && word.IndexOf('-') >= 0)
                {
                    int hyphen = word.IndexOf('-');
                    string prefix = word.Substring(0, hyphen + 1);
                    string suffix = word.Substring(hyphen + 1);
                    if (prefix.Length <= maxLineLength && suffix.Length <= maxLineLength)
                    {
                        output.Add(prefix);
                        output.Add(suffix);
                        continue;
                    }
                }

                if (word.Length > maxLineLength)
                {
                    for (int offset = 0; offset < word.Length; offset += maxLineLength)
                        output.Add(word.Substring(offset, Math.Min(maxLineLength, word.Length - offset)));
                }
                else
                {
                    output.Add(word);
                }
            }

            return string.Join(Environment.NewLine, output);
        }
    }

    internal sealed class ProductionRibbonCommandHandler : ICommand
    {
        public bool CanExecute(object parameter) { return true; }
        public event EventHandler CanExecuteChanged { add { } remove { } }

        public void Execute(object parameter)
        {
            RibbonButton button = parameter as RibbonButton;
            string command = button == null ? Convert.ToString(parameter) : Convert.ToString(button.CommandParameter);
            if (string.IsNullOrWhiteSpace(command)) return;
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document != null) document.SendStringToExecute(command, true, false, true);
        }
    }
}

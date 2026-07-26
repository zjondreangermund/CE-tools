using System;
using System.Linq;
using System.Windows.Input;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Windows;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: ExtensionApplication(typeof(CETools.Civil3D.PluginEntry))]
[assembly: CommandClass(typeof(CETools.Civil3D.BellmouthDensifierCommand))]

namespace CETools.Civil3D
{
    public sealed class PluginEntry : IExtensionApplication
    {
        private static bool _ribbonCreated;
        private static bool _ribbonErrorReported;

        public void Initialize()
        {
            DynamicSectionUpdateManager.Initialize();
            DynamicIntersectionUpdateManager.Initialize();
            AcApplication.Idle += OnApplicationIdle;
        }

        public void Terminate()
        {
            AcApplication.Idle -= OnApplicationIdle;
            DynamicIntersectionUpdateManager.Terminate();
            DynamicSectionUpdateManager.Terminate();
        }

        private static void OnApplicationIdle(object sender, EventArgs e)
        {
            if (_ribbonCreated)
            {
                AcApplication.Idle -= OnApplicationIdle;
                return;
            }

            try
            {
                _ribbonCreated = RibbonBuilder.EnsureCreated();
                if (_ribbonCreated)
                    AcApplication.Idle -= OnApplicationIdle;
            }
            catch (System.Exception exception)
            {
                if (_ribbonErrorReported)
                    return;

                _ribbonErrorReported = true;
                var document = AcApplication.DocumentManager.MdiActiveDocument;
                document?.Editor.WriteMessage(
                    "\nCE Tools ribbon error: " +
                    exception.GetType().Name +
                    " - " +
                    exception.Message);
            }
        }
    }

    /// <summary>
    /// Builds the CE Tools ribbon with Civil 3D 2023/2024-compatible Autodesk
    /// ribbon types. Panel items are flattened directly into RibbonPanelSource
    /// and flyout commands are RibbonMenuItem objects.
    /// </summary>
    internal static class RibbonBuilder
    {
        private const string TabId = "CE_TOOLS_RIBBON_TAB";
        private const string ProjectPanelId = "CE_TOOLS_CATEGORY_PROJECT";
        private const string SurveyPanelId = "CE_TOOLS_CATEGORY_SURVEY";
        private const string DrawingsPanelId = "CE_TOOLS_CATEGORY_DRAWINGS";
        private const string GeometryPanelId = "CE_TOOLS_CATEGORY_GEOMETRY";
        private const string CorridorPanelId = "CE_TOOLS_CATEGORY_CORRIDORS";
        private const string SiteDesignPanelId = "CE_TOOLS_CATEGORY_SITE_DESIGN";
        private const string UtilitiesPanelId = "CE_TOOLS_CATEGORY_UTILITIES";
        private const string StandardsPanelId = "CE_TOOLS_CATEGORY_STANDARDS";
        private const string AnalysisPanelId = "CE_TOOLS_CATEGORY_ANALYSIS";
        private const string ProductionPanelId = "CE_TOOLS_CATEGORY_PRODUCTION";

        public static bool EnsureCreated()
        {
            RibbonControl ribbon = ComponentManager.Ribbon;
            if (ribbon == null)
                return false;

            RibbonTab tab = ribbon.Tabs.FirstOrDefault(item => item.Id == TabId);
            if (tab == null)
            {
                tab = new RibbonTab { Id = TabId, Title = "CE TOOLS" };
                ribbon.Tabs.Add(tab);
            }
            else
            {
                tab.Title = "CE TOOLS";
            }

            tab.Panels.Clear();
            AddProjectPanel(tab);
            AddSurveyPanel(tab);
            AddDrawingsPanel(tab);
            AddGeometryPanel(tab);
            AddCorridorPanel(tab);
            AddSiteDesignPanel(tab);
            AddUtilitiesPanel(tab);
            AddStandardsPanel(tab);
            AddAnalysisPanel(tab);
            AddProductionPanel(tab);
            return true;
        }

        private static void AddProjectPanel(RibbonTab tab)
        {
            AddPanel(tab, ProjectPanelId, "Project", Row(
                Menu("CE_TOOLS_PROJECT_MENU", "Project\nSetup", "Create, review, clear and restore portable project information.",
                    Cmd("Project Setup", "CE_PROJECTSETUP ", "Create or update project metadata and review it in a pop-up."),
                    Cmd("Project Information", "CE_PROJECTINFO ", "Review project metadata and optionally place a drawing table."),
                    Cmd("Clear Project Information", "CE_PROJECTCLEAR ", "Clear project metadata after confirmation and keep a recoverable backup."),
                    Cmd("Restore Cleared Information", "CE_PROJECTRESTORE ", "Restore the values saved before the last project clear.")),
                Menu("CE_TOOLS_COORDSYS_MENU", "Coordinate\nSystems", "Report, search, assign and clear the drawing coordinate system.",
                    Cmd("Coordinate System Tools", "CE_COORDSYS ", "Open the coordinate-system menu."),
                    Cmd("Information", "CE_COORDSYSINFO ", "Report the current coordinate system."),
                    Cmd("Assign", "CE_COORDSYSASSIGN ", "Open Autodesk's native coordinate-system selection window."),
                    Cmd("Assign by Code", "CE_COORDSYSCODE ", "Advanced direct assignment using a validated Autodesk code."),
                    Cmd("Search Library", "CE_COORDSYSSEARCH ", "Search the installed coordinate-system library."),
                    Cmd("Clear", "CE_COORDSYSCLEAR ", "Clear the assignment after confirmation.")),
                Menu("CE_TOOLS_STANDARDS_MENU", "Project\nStandards", "Select a standards source file and record its project information.",
                    Cmd("Standards Tools", "CE_STANDARDS ", "Open the standards menu."),
                    Cmd("Select Standards", "CE_STANDARDSELECT ", "Browse for a standards file, review it and save its traceable details."),
                    Cmd("Standards Information", "CE_STANDARDINFO ", "Review stored standards and optionally place a drawing table."),
                    Cmd("Clear Standards", "CE_STANDARDCLEAR ", "Clear the standards record.")),
                Menu("CE_TOOLS_PROJECT_STYLES_MENU", "Project\nStyles", "Select project styles for roads, stormwater, sewer, water and platforms.",
                    Cmd("Project Style Centre", "CE_PROJECTSTYLES ", "Select alignment, profile, point, corridor, pipe, structure and related Civil 3D styles."),
                    Cmd("Project Style Information", "CE_PROJECTSTYLEINFO ", "Review the project style selections and optionally place a schedule."),
                    Cmd("Clear Project Styles", "CE_PROJECTSTYLECLEAR ", "Clear only the stored project style selections.")),
                Menu("CE_TOOLS_UNDO_MENU", "Undo &\nRedo", "Enable full native undo recording and run one-step undo or redo.",
                    Cmd("Enable Full Undo Recording", "CE_UNDOSETTINGS ", "Enable AutoCAD full undo recording for the current session."),
                    Cmd("Undo One Step", "CE_UNDO ", "Run one native AutoCAD undo step."),
                    Cmd("Redo One Step", "CE_REDO ", "Run one native AutoCAD redo step."))));
        }

        private static void AddSurveyPanel(RibbonTab tab)
        {
            AddPanel(tab, SurveyPanelId, "Survey", Row(
                Menu("CE_TOOLS_SURVEY_MENU", "Coordinate\nTools", "Linked coordinate labels, COGO points, crosses, compact tables and polyline vertices.",
                    Cmd("Linked Picked Coordinate", "CE_COORDPICK2 ", "Create a coordinate annotation and optionally add its source point to a linked register."),
                    Cmd("Linked Coordinate Cross", "CE_COORDCROSS2 ", "Choose COGO point, cross, annotation and linked register output."),
                    Cmd("Create Linked Coordinate Table", "CE_COORDTABLE2 ", "Create a compact linked Point Name, X, Y, Z table from selected COGO or AutoCAD points."),
                    Cmd("Refresh Linked Coordinate Table", "CE_COORDREFRESH ", "Refresh table rows from the current linked source-point coordinates."),
                    Cmd("Polyline Vertex Linked Points", "CE_COORDPOLY2 ", "Create sequential dynamic COGO points in polyline direction and a linked Point Name, X, Y, Z table."),
                    Cmd("Presentation and Dynamic Refresh", "CE_PRESENTATIONTOOLS ", "Open annotation scaling, overlap correction and automatic linked-refresh workflows.")),
                Menu("CE_TOOLS_SURVEY_UTILITIES_MENU", "Survey\nUtilities", "Direction arrows and preserved coordinate workflows.",
                    Cmd("Polyline Direction Arrows", "CE_PLDIR ", "Add, replace or clear linked arrows showing stored polyline direction."),
                    Cmd("Coordinate Tools (Legacy)", "CE_COORDINATE ", "Open the legacy coordinate tools menu."),
                    Cmd("Picked Coordinate Annotation (Legacy)", "CE_COORDPICKX ", "Create the shared annotation workflow."),
                    Cmd("Coordinate Cross + Annotation (Legacy)", "CE_COORDCROSSX ", "Create the shared cross and annotation workflow."),
                    Cmd("Polyline Vertex COGO Points (Legacy)", "CE_COORDPOLY ", "Run the original sequential COGO point and XYZ table workflow."))));
        }

        private static void AddDrawingsPanel(RibbonTab tab)
        {
            AddPanel(tab, DrawingsPanelId, "Drawings", Row(
                Menu("CE_TOOLS_DRAWING_MENU", "Drawing\nTools", "AutoCAD drawing and annotation utilities.",
                    Cmd("Open Floating Command Window", "CE_TOOLSPALETTE ", "Open every CE Tools ribbon command in a searchable modeless window that can be moved to a second monitor."),
                    Cmd("Annotation Settings", "CE_ANNOTSETTINGS ", "Select 1.8, 2.0 or 5.0 height, marker circles and MLeader/MText/COGO output."),
                    Cmd("Make Selected Objects Annotative", "CE_MAKEANNOTATIVE ", "Apply CE text height and annotative settings to selected text, dimensions, leaders and tables."),
                    Cmd("Scale Selected Tables", "CE_TABLESCALE ", "Resize selected tables relative to the current CE annotation height."),
                    Cmd("Resolve Annotation Overlaps", "CE_OVERLAPFIX ", "Reposition selected labels, leaders, dimensions and tables to reduce overlap."),
                    Cmd("Refresh All Linked Data", "CE_REFRESHALL ", "Refresh linked coordinates and BOQs, then rebuild supported Civil 3D objects."),
                    Cmd("Automatic Refresh Settings", "CE_AUTOREFRESH ", "Turn automatic linked coordinate and BOQ refresh on or off."),
                    Cmd("Refresh Status", "CE_REFRESHSTATUS ", "Show linked-data inventory and pending automatic-refresh status."),
                    Cmd("Colour 250 - Geometry or Annotation", "CE_COLOR250 ", "Choose geometry only or geometry plus annotation and change accepted objects to colour 250."),
                    Cmd("Polyline Direction Arrows", "CE_PLDIR ", "Add, replace or clear linked direction arrows.")),
                Menu("CE_TOOLS_CLEANUP_MENU", "Cleanup\nManager", "Run OVERKILL, AUDIT and PURGE together or separately.",
                    Cmd("Open Cleanup Manager", "CE_CLEANUPUI ", "Open the cleanup selection window."),
                    Cmd("Full Cleanup", "CE_DRAWCLEAN All ", "Run all drawing-cleanup stages."),
                    Cmd("OVERKILL Only", "CE_DRAWCLEAN Overkill ", "Remove duplicate and overlapping geometry."),
                    Cmd("AUDIT Only", "CE_DRAWCLEAN Audit ", "Audit and fix drawing errors."),
                    Cmd("PURGE Only", "CE_DRAWCLEAN Purge ", "Purge unused named objects.")),
                Menu("CE_TOOLS_HATCH_MENU", "Hatch\nTools", "Create and edit transparent civil hatches while keeping grids, labels and linework visible.",
                    Cmd("Open Hatch Settings", "CE_HATCHUI ", "Open the hatch settings and action window."),
                    Cmd("Create Transparent Hatches", "CE_HATCHCREATE ", "Create associative hatches from selected closed boundaries."),
                    Cmd("Edit Hatch Settings", "CE_HATCHEDIT ", "Edit selected hatch pattern, scale, angle, colour and transparency."),
                    Cmd("Match Hatch Settings", "CE_HATCHMATCH ", "Copy hatch display settings from one source hatch."),
                    Cmd("Send Hatches Behind Linework", "CE_HATCHBACK ", "Move selected hatches to the back of draw order."))));
        }

        private static void AddGeometryPanel(RibbonTab tab)
        {
            AddPanel(tab, GeometryPanelId, "Geometry", Row(
                Menu("CE_TOOLS_ROADS_MENU", "Road\nTools", "Road geometry utilities.",
                    Cmd("Bellmouth Densifier", "CE_BMVERT ", "Add equal-chainage vertices to bellmouth polylines.")),
                Menu("CE_TOOLS_FEATURE_LINE_MENU", "Feature Line\nTools", "Feature-line creation, reporting, editing, annotation and linked stepped offsets.",
                    Cmd("Feature Line Tools", "CE_FLTOOLS ", "Open the legacy feature-line report and elevation menu."),
                    Cmd("Report", "CE_FLREPORTUI ", "Show feature-line details in a pop-up and optionally place a table."),
                    Cmd("Feature Line Annotation", "CE_FLLABELX ", "Create a feature-line MLeader, MText or COGO point using shared settings."),
                    Cmd("Raise / Lower", "CE_FLRAISEX ", "Review and edit selected feature-line elevations."),
                    Cmd("Raise / Lower (Legacy)", "CE_FLRAISE ", "Run the original feature-line elevation editing command."),
                    Cmd("Set Elevation", "CE_FLSETELEV ", "Set selected feature lines to one elevation."),
                    Cmd("Constant Grade Between Endpoints", "CE_FLCONSTGRADE ", "Set all existing points to a constant grade between each feature line's endpoints."),
                    Cmd("Create and Point Edit", "CE_FLEDIT ", "Open creation, surface and point-edit tools."),
                    Cmd("Create from Object", "CE_FLCREATE ", "Create feature lines from supported curves."),
                    Cmd("Elevations from Surface", "CE_FLSURFACEUI ", "Select a Civil 3D surface from a pop-up and assign feature-line elevations."),
                    Cmd("Elevations from Surface (Legacy)", "CE_FLSURFACE ", "Run the original command-line surface assignment workflow."),
                    Cmd("Insert Elevation Point", "CE_FLINSERT ", "Insert an elevation point."),
                    Cmd("Delete Elevation Point", "CE_FLDELETE ", "Delete a confirmed elevation point."),
                    Cmd("Weed Elevation Points", "CE_FLWEED ", "Remove redundant elevation points."),
                    Cmd("Linked Stepped Offset", "CE_FLREL ", "Create, update, inspect or detach linked stepped offsets."),
                    Cmd("Create Linked Offset", "CE_FLRELCREATE ", "Create relative stepped-offset feature lines."),
                    Cmd("Update Linked Offsets", "CE_FLRELUPDATE ", "Refresh linked offsets from their source."),
                    Cmd("Linked Offset Information", "CE_FLRELINFO ", "Report a linked offset relationship."),
                    Cmd("Detach Linked Offset", "CE_FLRELDETACH ", "Keep geometry but remove the CE relationship.")),
                Menu("CE_TOOLS_ALIGNMENT_MENU", "Alignment\nTools", "Alignment reporting, station-offset queries and labels.",
                    Cmd("Alignment Tools", "CE_ALTOOLS ", "Open alignment tools."),
                    Cmd("Alignment Report", "CE_ALREPORTUI ", "Show alignment details in a pop-up and optionally place a table."),
                    Cmd("Station and Offset", "CE_ALSTOFF ", "Report station and signed offset."),
                    Cmd("Station-Offset Annotation", "CE_ALLABELX ", "Create an MLeader, MText or COGO point using shared annotation settings.")),
                Menu("CE_TOOLS_PROFILE_MENU", "Profile\nTools", "Profile reporting, station elevations and plan labels.",
                    Cmd("Profile Tools", "CE_PRTOOLS ", "Open profile tools."),
                    Cmd("Profile Report", "CE_PRREPORTUI ", "Show profile details in a pop-up and optionally place a table."),
                    Cmd("Station Elevation", "CE_PRELEV ", "Report elevation and grade at a station."),
                    Cmd("Profile Annotation", "CE_PRLABELX ", "Create an MLeader, MText or COGO point using shared annotation settings.")),
                Menu("CE_TOOLS_SURFACE_MENU", "Surface\nTools", "Surface reporting, elevation labels and comparisons.",
                    Cmd("Surface Tools", "CE_SFTOOLS ", "Open surface tools."),
                    Cmd("Surface Report", "CE_SFREPORTUI ", "Show surface details in a pop-up and optionally place a table."),
                    Cmd("Surface Elevation", "CE_SFELEV ", "Report an elevation at a point."),
                    Cmd("Surface Annotation", "CE_SFLABELX ", "Create an MLeader, MText or COGO point using shared annotation settings."),
                    Cmd("Compare Surfaces", "CE_SFCOMPARE ", "Compare two surface elevations.")),
                Menu("CE_TOOLS_SURFACE_CORRECTION_MENU", "Surface Correction", "Audit surface quality, create reversible corrected copies and simplify performance copies.",
                    Cmd("Surface Correction Tools", "CE_SURFCTOOLS ", "Open audit, correction, simplification, restore, settings and information workflows."),
                    Cmd("Audit Surface Quality", "CE_SURFAUDIT ", "Screen for zero elevations, spikes, lows, extremes, holes and likely object contamination."),
                    Cmd("Create Reversible Corrected Surface", "CE_SURFCORRECT ", "Create a separate corrected surface copy without modifying the original."),
                    Cmd("Create Reversible Simplified Surface", "CE_SURFSIMPLIFY ", "Create a separate grid-decimated performance copy without modifying the original."),
                    Cmd("Restore Original / Remove Generated Copy", "CE_SURFCRESTORE ", "Erase only a selected CE generated correction/simplification surface."),
                    Cmd("Surface Correction Settings", "CE_SURFCSETTINGS ", "Store zero, spike, neighbour, contamination, audit and simplification thresholds."),
                    Cmd("Surface Correction Information", "CE_SURFCINFO ", "Review generated surface links and current correction settings.")),
                Menu("CE_TOOLS_DYNAMIC_INTERSECTION_MENU", "Dynamic\nIntersections", "Create linked plan intersections from feature lines, corridors and curves and keep them synchronised.",
                    Cmd("Dynamic Intersection Tools", "CE_INTTOOLS ", "Open create, refresh, information, detach, settings and monitor workflows."),
                    Cmd("Create Linked Intersection Set", "CE_INTCREATE ", "Create markers, elevation comparisons and a linked register from selected design sources."),
                    Cmd("Refresh Linked Intersection Set", "CE_INTREFRESH ", "Recalculate one linked set from the current feature-line, corridor or curve geometry."),
                    Cmd("Intersection Set Information", "CE_INTINFO ", "Review source handles, live/missing states, generated handles and monitor status."),
                    Cmd("Detach Intersection Set", "CE_INTDETACH ", "Remove the link and keep or delete the generated intersection geometry."),
                    Cmd("Dynamic Intersection Settings", "CE_INTSETTINGS ", "Store marker, label, tolerance, elevation warning, sampling and corridor-code settings."),
                    Cmd("Dynamic Intersection Monitor", "CE_INTMONITOR ", "Report linked sets, pending refresh and deferred update-manager status."))));
        }

        private static void AddCorridorPanel(RibbonTab tab)
        {
            AddPanel(tab, CorridorPanelId, "Corridors", Row(
                Menu("CE_TOOLS_CORRIDOR_MENU", "Corridor\nTools", "Corridor reporting, annotation, baseline inspection and rebuilding.",
                    Cmd("Corridor Tools", "CE_CORTOOLS ", "Open the legacy corridor tools menu."),
                    Cmd("Corridor Report", "CE_CORREPORTUI ", "Show corridor details in a pop-up and optionally place a table."),
                    Cmd("Baselines and Regions", "CE_CORBASEUI ", "Show baseline and region details in a pop-up and optionally place a table."),
                    Cmd("Corridor Annotation", "CE_CORLABELX ", "Create a corridor MLeader or MText using shared annotation settings."),
                    Cmd("Rebuild Corridors", "CE_CORREBUILDX ", "Explicitly call Corridor.Rebuild() for every editable selected corridor after review."),
                    Cmd("Rebuild Corridors (Legacy)", "CE_CORREBUILD ", "Run the original controlled rebuild command."))));
        }

        private static void AddSiteDesignPanel(RibbonTab tab)
        {
            AddPanel(tab, SiteDesignPanelId, "Site Design", Row(
                Menu("CE_TOOLS_PARKING_MENU", "Parking\nTools", "Straight parking rows, validation, reporting, counting and numbering.",
                    Cmd("Parking Tools", "CE_PKTOOLS ", "Open legacy parking tools."),
                    Cmd("Single Row", "CE_PKROW ", "Create a straight single row."),
                    Cmd("Double Row", "CE_PKDOUBLE ", "Create opposing rows around an aisle."),
                    Cmd("Parking Report", "CE_PKREPORTUI ", "Show parking bay groups in a pop-up and optionally place a table."),
                    Cmd("Validate and Count Bays", "CE_PKCOUNTX ", "Validate blocks and closed polylines, explain rejected objects and optionally place a table."),
                    Cmd("Count Bays (Legacy)", "CE_PKCOUNT ", "Run the original parking count command."),
                    Cmd("Validate and Number Bays", "CE_PKNUMBER2 ", "Validate objects and number accepted bays using the shared annotation height."),
                    Cmd("Number Bays (Legacy Shared)", "CE_PKNUMBERX ", "Run the shared-height parking numbering command.")),
                Menu("CE_TOOLS_PARKING_SKEW_MENU", "Parking Skew\nValidation", "Measure true perpendicular bay width, colour pass/fail dimensions and create failed-bay correction outlines.",
                    Cmd("Parking Skew Tools", "CE_PKSKTOOLS ", "Open validate, correct, clear, settings and information workflows."),
                    Cmd("Validate Perpendicular Bay Width", "CE_PKSKVALIDATE ", "Measure width perpendicular to each bay long axis and create green pass/red fail dimensions."),
                    Cmd("Create Failed-Bay Correction Outlines", "CE_PKSKCORRECT ", "Create separate target-width outlines for failed bays without changing selected source geometry."),
                    Cmd("Clear Skew Review Graphics", "CE_PKSKCLEAR ", "Clear CE parking skew dimensions, labels and correction outlines for selected sources or all."),
                    Cmd("Parking Skew Settings", "CE_PKSKSETTINGS ", "Store the 2500 mm standard, units conversion, tolerance, layers and annotation sizes."),
                    Cmd("Parking Skew Information", "CE_PKSKINFO ", "Review generated objects, live source handles and current width settings."))));
        }

        private static void AddUtilitiesPanel(RibbonTab tab)
        {
            AddPanel(tab, UtilitiesPanelId, "Utilities", Row(
                Menu("CE_TOOLS_STORMWATER_MENU", "Stormwater\nProduction", "Sequence stormwater networks and create linked alignments, profiles and profile views.",
                    Cmd("Stormwater Tools", "CE_SWTOOLS ", "Open the stormwater production menu."),
                    Cmd("Sequence Main and Branches", "CE_SWSEQ ", "Select or calculate the main branch and sequence the complete gravity network."),
                    Cmd("Create / Refresh Alignments", "CE_SWALIGN ", "Create branch alignments from a sequenced network or selected polylines."),
                    Cmd("Refresh Alignments", "CE_SWREFRESH ", "Rebuild selected stormwater alignments from their current source geometry."),
                    Cmd("Create / Refresh Profiles", "CE_SWPROFILE ", "Create existing-ground profiles, profile views and network-part displays."),
                    Cmd("Stormwater Settings", "CE_SWSETTINGS ", "Store project style names, layers and plan-label height."),
                    Cmd("Stormwater Information", "CE_SWINFO ", "Report stormwater links, generated objects and current production settings.")),
                Menu("CE_TOOLS_SEWER_PRODUCTION_MENU", "Sewer Network\nProduction", "Sequence complete sewer networks, select a main route, format alignments and create profiles.",
                    Cmd("Sewer Production Tools", "CE_SEWTOOLS ", "Open the complete sewer production menu."),
                    Cmd("Sequence Network Automatically", "CE_SEWSEQ ", "Sequence a complete network automatically or one selected path."),
                    Cmd("Sequence Network with Selected Main", "CE_SEWSEQMAIN ", "Select the intended Branch-1 route and sequence all remaining branches."),
                    Cmd("Create / Refresh Branch Alignments", "CE_SEWALIGN ", "Create sewer branch alignments and visible branch labels."),
                    Cmd("Refresh Linked Alignments", "CE_SEWREFRESH ", "Resolve linked source networks and run the alignment refresh preview."),
                    Cmd("Apply Styles and Fix Label Spacing", "CE_SEWFORMAT ", "Apply the selected alignment style and reposition CE branch labels."),
                    Cmd("Create / Refresh Sewer Profiles", "CE_SEWPROFILE ", "Create EG profiles, profile views and network-part displays."),
                    Cmd("Sewer Production Settings", "CE_SEWSETTINGS ", "Store sewer styles, profile layer and label height."),
                    Cmd("Sewer Production Information", "CE_SEWINFO ", "Report sewer links, generated output and current settings.")),
                Menu("CE_TOOLS_WATER_PRODUCTION_MENU", "Water Network\nProduction", "Sequence water mains and branches, create profiles, and place controlled asset review markers.",
                    Cmd("Water Production Tools", "CE_WATERTOOLS ", "Open the complete water production menu."),
                    Cmd("Sequence Mains and Branches", "CE_WATERSEQ ", "Sequence selected open polylines or pressure-pipe sources, with the longest route as W-MAIN."),
                    Cmd("Create / Refresh Water Alignments", "CE_WATERALIGN ", "Create linked Civil 3D alignments from water polylines or pressure-pipe sources."),
                    Cmd("Refresh Linked Water Alignments", "CE_WATERREFRESH ", "Rebuild CE water alignments from current source geometry."),
                    Cmd("Create / Refresh Water Profiles", "CE_WATERPROFILE ", "Create EG profiles and profile views and attempt pressure-part display."),
                    Cmd("Place Valve and Hydrant Review Markers", "CE_WATERPLACE ", "Preview and create linked isolating-valve, hydrant, air-valve and scour-valve review markers."),
                    Cmd("Refresh Asset Review Markers", "CE_WATERPLACEREFRESH ", "Recalculate linked water-asset review locations from current alignment geometry."),
                    Cmd("Water Production Settings", "CE_WATERSETTINGS ", "Store styles, layers, valve spacing, hydrant spacing and marker size."),
                    Cmd("Water Production Information", "CE_WATERINFO ", "Report water links, generated output, settings and refresh status."))));
        }

        private static void AddStandardsPanel(RibbonTab tab)
        {
            AddPanel(tab, StandardsPanelId, "Standards & Details", Row(
                Menu("CE_TOOLS_DESIGN_STANDARDS_MENU", "Design\nStandards", "Browse, search and apply the built-in design-standards reference library.",
                    Cmd("Design Standards Tools", "CE_DESIGNSTANDARDS ", "Open the design-standards library menu."),
                    Cmd("Browse Standards Library", "CE_STDBROWSE ", "Browse standards by engineering category."),
                    Cmd("Search Standards Library", "CE_STDSEARCH ", "Search by code, title, authority or keyword."),
                    Cmd("Apply Standard to Project", "CE_STDAPPLY ", "Record a catalogue item in the existing project standards metadata."),
                    Cmd("Current Project Standards", "CE_STANDARDINFO ", "Report the standards currently stored in the DWG.")),
                Menu("CE_TOOLS_TYPICAL_DETAILS_MENU", "Typical\nDetails", "Configure, search and insert office-approved typical details.",
                    Cmd("Typical Details Tools", "CE_DETAILTOOLS ", "Open the Typical Details command menu."),
                    Cmd("Set Master Library Folder", "CE_DETAILSETROOT ", "Store the project master-detail folder."),
                    Cmd("Search Detail Library", "CE_DETAILSEARCH ", "Search DWG, DXF and PDF assets by category or keyword."),
                    Cmd("Insert Approved DWG Detail", "CE_DETAILINSERT ", "Search and insert an approved DWG detail as a block."),
                    Cmd("Typical Details Information", "CE_DETAILINFO ", "Report the stored library root, categories and supported Phase 1 formats."))));
        }

        private static void AddAnalysisPanel(RibbonTab tab)
        {
            AddPanel(tab, AnalysisPanelId, "Analysis", Row(
                Menu("CE_TOOLS_QUANTITY_MENU", "Quantity &\nBOQ Tools", "Linked bills of quantities, explicit refresh and Excel exports by discipline.",
                    Cmd("BOQ Tools", "CE_BOQTOOLS ", "Open linked BOQ build, refresh, information and export workflows."),
                    Cmd("Build Linked BOQ", "CE_BOQBUILD ", "Create a linked drawing BOQ with quantity, rate and amount columns."),
                    Cmd("Refresh Linked BOQ", "CE_BOQREFRESH ", "Recalculate quantities from current linked source geometry while preserving matching rates."),
                    Cmd("Linked BOQ Information", "CE_BOQINFO ", "Review link schema, discipline, unit scale and stale source handles."),
                    Cmd("Export Linked BOQ to Excel", "CE_BOQEXPORT ", "Refresh and export a linked BOQ as a dependency-free .xlsx workbook."),
                    Cmd("Road BOQ Excel", "CE_BOQROAD ", "Export road surfacing, layerworks, kerbs, drainage, markings and signs."),
                    Cmd("Platform BOQ Excel", "CE_BOQPLATFORM ", "Export platform, grading, layerworks and earthwork quantities."),
                    Cmd("Stormwater BOQ Excel", "CE_BOQSTORM ", "Export stormwater pipes, culverts, structures and open drainage."),
                    Cmd("Sewer BOQ Excel", "CE_BOQSEWER ", "Export sewer pipe and structure quantities."),
                    Cmd("Water BOQ Excel", "CE_BOQWATER ", "Export water pipe, valve, fitting and hydrant quantities."),
                    Cmd("Bulk-water BOQ Excel", "CE_BOQBULKWATER ", "Export bulk pipeline, storage, pump and fitting quantities."),
                    Cmd("Total Length", "CE_TLENGTH ", "Preserved quick total of selected curve lengths by layer."),
                    Cmd("Total Area", "CE_TAREA ", "Preserved quick total of selected areas by layer.")),
                Menu("CE_TOOLS_DESIGN_REPORT_MENU", "Design\nReports", "Generate current project inventory reports, optional drawing tables and Excel exports.",
                    Cmd("Report & Production Tools", "CE_REPORTTOOLS ", "Open full, discipline, export, summary and drawing-book workflows."),
                    Cmd("Full Design Report", "CE_REPORTFULL ", "Generate a full model-space design report with CE link and layout status."),
                    Cmd("Choose Discipline Report", "CE_REPORTDISC ", "Generate a discipline report."),
                    Cmd("Road Report", "CE_REPORTROAD ", "Generate the road-design inventory report."),
                    Cmd("Platform Report", "CE_REPORTPLATFORM ", "Generate the platform/grading design report."),
                    Cmd("Stormwater Report", "CE_REPORTSTORM ", "Generate the stormwater design report."),
                    Cmd("Sewer Report", "CE_REPORTSEWER ", "Generate the sewer design report."),
                    Cmd("Water Report", "CE_REPORTWATER ", "Generate the water design report."),
                    Cmd("Bulk-water Report", "CE_REPORTBULKWATER ", "Generate the bulk-water design report."),
                    Cmd("Export Design Report", "CE_REPORTEXPORT ", "Export a full or discipline design inventory as an .xlsx workbook.")),
                Menu("CE_TOOLS_DYNAMIC_SECTION_MENU", "Dynamic Cross\nSections", "Create a linked cross section and keep it synchronised with monitored drawing changes.",
                    Cmd("Cross-section Tools", "CE_XSTOOLS ", "Open create, refresh, information, detach and monitor workflows."),
                    Cmd("Create Dynamic Cross Section", "CE_XSCREATE ", "Sample intersected surfaces and design objects and create a linked section view."),
                    Cmd("Refresh Dynamic Cross Section", "CE_XSREFRESH ", "Explicitly rebuild a linked section from current source geometry."),
                    Cmd("Cross-section Information", "CE_XSINFO ", "Review source, scales, samples, capture width and generated link status."),
                    Cmd("Detach Dynamic Cross Section", "CE_XSDETACH ", "Remove the link and keep or delete generated section geometry."),
                    Cmd("Dynamic-section Monitor", "CE_XSMONITOR ", "Report automatic update-manager and pending-refresh status."))));
        }

        private static void AddProductionPanel(RibbonTab tab)
        {
            AddPanel(tab, ProductionPanelId, "Production", Row(
                Menu("CE_TOOLS_CLIENT_BOOK_MENU", "Project Closeout\nClient Book", "Create linked A4/A3 client summary books at project closeout.",
                    Cmd("Project Closeout - A4 and A3", "CE_PROJECTCLOSEOUT ", "Create or refresh the complete A4 and A3 client summary books."),
                    Cmd("Create Client Book", "CE_CLIENTBOOK ", "Choose A4, A3 or both and create linked summary pages."),
                    Cmd("Refresh Client Book", "CE_CLIENTBOOKREFRESH ", "Refresh all linked client-book pages from current project information."),
                    Cmd("Client Book Information", "CE_CLIENTBOOKINFO ", "Review page links, issue stage, revision and stale generated handles."),
                    Cmd("Export Client Book Index", "CE_CLIENTBOOKINDEX ", "Export the linked client-book register to Excel.")),
                Menu("CE_TOOLS_PRODUCTION_MENU", "Summary &\nDrawing Books", "Generate project summary sheets and A-series client/construction drawing-book layouts.",
                    Cmd("Production Tools", "CE_REPORTTOOLS ", "Open reports, summaries and drawing-book workflows."),
                    Cmd("Create Project Summary Sheet", "CE_SUMMARYSHEET ", "Create a linked project metadata, discipline and production-readiness summary."),
                    Cmd("Refresh Project Summary", "CE_SUMMARYREFRESH ", "Refresh the summary from current model, links and layouts."),
                    Cmd("Summary Link Information", "CE_SUMMARYINFO ", "Review summary anchor and generated-object link status."),
                    Cmd("Create A-Series Drawing Books", "CE_DRAWINGBOOK ", "Create or refresh A4/A3 client and A1/A0 construction layouts."),
                    Cmd("Export Drawing Book Index", "CE_BOOKINDEX ", "Export the standard and existing layout register to Excel."))));
        }

        private static RibbonItem[] Row(params RibbonItem[] items)
        {
            return items;
        }

        private static void AddPanel(
            RibbonTab tab,
            string panelId,
            string title,
            params RibbonItem[][] rows)
        {
            var source = new RibbonPanelSource
            {
                Id = panelId,
                Title = PrefixRibbonText(title).ToUpperInvariant()
            };

            foreach (RibbonItem[] row in rows)
            {
                foreach (RibbonItem item in row)
                    source.Items.Add(item);
            }

            tab.Panels.Add(new RibbonPanel { Source = source });
        }

        private static RibbonMenuButton Menu(
            string id,
            string text,
            string toolTip,
            params RibbonCommandDefinition[] commands)
        {
            var menu = new RibbonMenuButton
            {
                Id = id,
                Text = PrefixRibbonText(text),
                ShowText = true,
                ShowImage = false,
                Size = RibbonItemSize.Large,
                ToolTip = toolTip
            };

            try
            {
                menu.Image = RibbonVisuals.Small(id);
                menu.LargeImage = RibbonVisuals.Large(id);
                menu.ShowImage = true;
            }
            catch
            {
                // Text remains available when a host/theme rejects runtime icons.
            }

            foreach (RibbonCommandDefinition command in commands)
                menu.Items.Add(CreateCommandMenuItem(command));

            return menu;
        }

        private static RibbonCommandDefinition Cmd(string text, string command, string toolTip)
        {
            return new RibbonCommandDefinition(text, command, toolTip);
        }

        private static string PrefixRibbonText(string text)
        {
            string value = text ?? string.Empty;
            return value.StartsWith("CE \u2013 ", StringComparison.Ordinal)
                ? value
                : "CE \u2013 " + value;
        }

        private static RibbonMenuItem CreateCommandMenuItem(
            RibbonCommandDefinition definition)
        {
            var menuItem = new RibbonMenuItem
            {
                Id = "CE_TOOLS_COMMAND_" + definition.Command.Trim().Replace(' ', '_'),
                Text = PrefixRibbonText(definition.Text),
                ShowText = true,
                ShowImage = false,
                CommandParameter = definition.Command,
                CommandHandler = new RibbonCommandHandler(),
                ToolTip = definition.ToolTip
            };

            try
            {
                menuItem.Image = RibbonVisuals.Small(definition.Command);
                menuItem.ShowImage = true;
            }
            catch
            {
                // Text remains available when a host/theme rejects runtime icons.
            }

            return menuItem;
        }

        private sealed class RibbonCommandDefinition
        {
            public RibbonCommandDefinition(string text, string command, string toolTip)
            {
                Text = text;
                Command = command;
                ToolTip = toolTip;
            }

            public string Text { get; }
            public string Command { get; }
            public string ToolTip { get; }
        }
    }

    internal sealed class RibbonCommandHandler : ICommand
    {
#pragma warning disable 67
        public event EventHandler CanExecuteChanged;
#pragma warning restore 67

        public bool CanExecute(object parameter)
        {
            return AcApplication.DocumentManager.MdiActiveDocument != null;
        }

        public void Execute(object parameter)
        {
            var menuItem = parameter as RibbonMenuItem;
            string command = menuItem == null ? null : menuItem.CommandParameter as string;
            if (string.IsNullOrWhiteSpace(command))
                return;

            AcApplication.DocumentManager.MdiActiveDocument?.SendStringToExecute(
                command,
                true,
                false,
                true);
        }
    }
}

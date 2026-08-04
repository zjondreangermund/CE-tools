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

        public void Initialize()
        {
            ParkingOptionAutoRefreshManager.Initialize();
            DynamicSectionUpdateManager.Initialize();
            DynamicIntersectionUpdateManager.Initialize();
            AnnotationScaleSyncManager.Initialize();
            ParkingNumberAutoRefreshManager.Initialize();
            WaterSewerCostAutoRefreshManager.Initialize();
            LinkedTableAutoRefreshManager.Initialize();
            FloatingToolsCommands.Initialize();
            AcApplication.Idle += OnApplicationIdle;
        }

        public void Terminate()
        {
            AcApplication.Idle -= OnApplicationIdle;
            FloatingToolsCommands.Terminate();
            LinkedTableAutoRefreshManager.Terminate();
            WaterSewerCostAutoRefreshManager.Terminate();
            ParkingNumberAutoRefreshManager.Terminate();
            AnnotationScaleSyncManager.Terminate();
            DynamicIntersectionUpdateManager.Terminate();
            DynamicSectionUpdateManager.Terminate();
            ParkingOptionAutoRefreshManager.Terminate();
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
                {
                    // The workflow command centre must appear once when the
                    // first Civil 3D session has a usable CE Tools ribbon.
                    // Opening it before this point produces an empty window.
                    FloatingToolsCommands.OpenAtFirstStartup();
                    AcApplication.Idle -= OnApplicationIdle;
                }
            }
            catch
            {
                // Civil 3D can raise Idle before its ribbon is fully initialized.
            }
        }
    }

    /// <summary>
    /// Builds a compact, presentation-ready CE Tools ribbon. Each engineering area
    /// receives a clearly named panel with large branded flyout buttons. Detailed
    /// commands stay inside the flyouts so the tab remains usable on smaller screens.
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
        private const string AdvancedPanelId = "CE_TOOLS_CATEGORY_ADVANCED";
        private const string ProductionPanelId = "CE_TOOLS_CATEGORY_PRODUCTION";

        public static bool EnsureCreated()
        {
            RibbonControl ribbon = ComponentManager.Ribbon;
            if (ribbon == null) return false;

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

            // CE Tools owns this tab. Rebuild it so stale panels and duplicate
            // buttons cannot remain after an upgrade or ribbon reload.
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
            AddAdvancedPanel(tab);
            AddProductionPanel(tab);
            return true;
        }

        private static void AddProjectPanel(RibbonTab tab)
        {
            AddPanel(
                tab,
                ProjectPanelId,
                "Project",
                Row(
                    Menu(
                        "CE_TOOLS_PROJECT_MENU",
                        "Project\nSetup",
                        "Create, review, clear and restore portable project information.",
                        Cmd("Project Setup", "CE_PROJECTSETUP ", "Create or update project metadata and review it in a pop-up."),
                        Cmd("Project Information", "CE_PROJECTINFO ", "Review project metadata and optionally place a drawing table."),
                        Cmd("Clear Project Information", "CE_PROJECTCLEAR ", "Clear project metadata after confirmation and keep a recoverable backup."),
                        Cmd("Restore Cleared Information", "CE_PROJECTRESTORE ", "Restore the values saved before the last project clear.")),
                    Menu(
                        "CE_TOOLS_COORDSYS_MENU",
                        "Coordinate\nSystems",
                        "Report, search, assign and clear the drawing coordinate system.",
                        Cmd("Coordinate System Tools", "CE_COORDSYS ", "Open the coordinate-system menu."),
                        Cmd("Information", "CE_COORDSYSINFO ", "Report the current coordinate system."),
                        Cmd("Assign", "CE_COORDSYSASSIGN ", "Open Autodesk's native coordinate-system selection window."),
                        Cmd("Assign by Code", "CE_COORDSYSCODE ", "Advanced direct assignment using a validated Autodesk code."),
                        Cmd("Search Library", "CE_COORDSYSSEARCH ", "Search the installed coordinate-system library."),
                        Cmd("Clear", "CE_COORDSYSCLEAR ", "Clear the assignment after confirmation.")),
                    Menu(
                        "CE_TOOLS_STANDARDS_MENU",
                        "Project\nStandards",
                        "Select a standards source file and record its project information.",
                        Cmd("Standards Tools", "CE_STANDARDS ", "Open the standards menu."),
                        Cmd("Select Standards", "CE_STANDARDSELECT ", "Browse for a standards file, review it and save its traceable details."),
                        Cmd("Standards Information", "CE_STANDARDINFO ", "Review stored standards and optionally place a drawing table."),
                        Cmd("Clear Standards", "CE_STANDARDCLEAR ", "Clear the standards record.")),
                    Menu(
                        "CE_TOOLS_PROJECT_STYLES_MENU",
                        "Project\nStyles",
                        "Select and review project Civil 3D styles by discipline.",
                        Cmd("Project Style Centre", "CE_PROJECTSTYLES ", "Select alignment, profile, corridor, point and network styles."),
                        Cmd("Project Style Information", "CE_PROJECTSTYLEINFO ", "Review stored project style selections."),
                        Cmd("Clear Project Styles", "CE_PROJECTSTYLECLEAR ", "Clear only the stored project style selections.")),
                    Menu(
                        "CE_TOOLS_UNDO_MENU",
                        "Undo &\nRedo",
                        "Enable full native undo recording and run one-step undo or redo.",
                        Cmd("Enable Full Undo Recording", "CE_UNDOSETTINGS ", "Enable AutoCAD full undo recording."),
                        Cmd("Undo One Step", "CE_UNDO ", "Run one native AutoCAD undo step."),
                        Cmd("Redo One Step", "CE_REDO ", "Run one native AutoCAD redo step.")),
                    Menu(
                        "CE_TOOLS_COMMAND_CATALOGUE_MENU",
                        "Command\nCatalogue",
                        "Search, audit and export every command declared by CE Tools.",
                        Cmd("Open All Commands", "CE_COMMANDCENTER ", "Open the searchable all-command workflow centre."),
                        Cmd("Command Report", "CE_COMMANDREPORT ", "Review every command, module and ribbon assignment."),
                        Cmd("Command Audit", "CE_COMMANDAUDIT ", "Audit unique declarations and ribbon coverage."),
                        Cmd("Export Command CSV", "CE_COMMANDEXPORT ", "Export the command catalogue to CSV."),
                        Cmd("Export Searchable HTML", "CE_COMMANDHTML ", "Create a searchable offline command reference."),
                        Cmd("Refresh Ribbon and Catalogue", "CE_RIBBONREFRESH ", "Rebuild the CE Tools ribbon and reload the command catalogue."))));
        }

        private static void AddSurveyPanel(RibbonTab tab)
        {
            AddPanel(
                tab,
                SurveyPanelId,
                "Survey",
                Row(
                    Menu(
                        "CE_TOOLS_SURVEY_MENU",
                        "Coordinate\nTools",
                        "Linked coordinate labels, COGO points, crosses, compact tables and polyline vertices.",
                        Cmd("Linked Picked Coordinate", "CE_COORDPICK2 ", "Create a coordinate annotation and optionally add its source point to a linked register."),
                        Cmd("Continuous Linked Coordinates", "CE_COORDPICKCONTINUOUS ", "Place sequential linked coordinate outputs continuously."),
                        Cmd("Linked Coordinate Cross", "CE_COORDCROSS2 ", "Choose COGO point, cross, annotation and linked register output."),
                        Cmd("Create Linked Coordinate Table", "CE_COORDTABLE2 ", "Create a compact linked Y-X-Z table from selected COGO or AutoCAD points."),
                        Cmd("Refresh Linked Coordinate Table", "CE_COORDREFRESH ", "Refresh table rows from the current linked source-point coordinates."),
                        Cmd("Polyline Vertex Linked Points", "CE_COORDPOLY2 ", "Create sequential COGO points in polyline direction and a linked Point Name, Y, X, Z table.")),
                    Menu(
                        "CE_TOOLS_SURVEY_UTILITIES_MENU",
                        "Survey\nUtilities",
                        "Direction arrows and preserved coordinate workflows.",
                        Cmd("Polyline Direction Arrows", "CE_PLDIR ", "Add, replace or clear linked arrows showing stored polyline direction."),
                        Cmd("Refresh Direction Arrows", "CE_PLDIRREFRESH ", "Reposition linked arrows after polyline edits."),
                        Cmd("Reverse Polyline and Arrows", "CE_PLDIRREVERSE ", "Reverse selected polylines and their linked arrows."),
                        Cmd("Presentation and Dynamic Refresh", "CE_PRESENTATIONTOOLS ", "Open shared annotation, overlap and rebuild workflows."),
                        Cmd("Coordinate Tools (Legacy)", "CE_COORDINATE ", "Open the legacy coordinate tools menu."),
                        Cmd("Picked Coordinate Annotation (Legacy)", "CE_COORDPICKX ", "Create the Batch 3 coordinate annotation workflow."),
                        Cmd("Coordinate Cross + Annotation (Legacy)", "CE_COORDCROSSX ", "Create the Batch 3 cross and annotation workflow."),
                        Cmd("Polyline Vertex COGO Points (Legacy)", "CE_COORDPOLY ", "Run the original sequential COGO point and XYZ table workflow."))));
        }

        private static void AddDrawingsPanel(RibbonTab tab)
        {
            AddPanel(
                tab,
                DrawingsPanelId,
                "Drawings",
                Row(
                    Menu(
                        "CE_TOOLS_DRAWING_MENU",
                        "Drawing\nTools",
                        "AutoCAD drawing and annotation utilities.",
                        Cmd("Open Workflow Centre", "CE_TOOLSPALETTE ", "Open every ribbon workflow and searchable command in the floating window."),
                        Cmd("Annotation Settings", "CE_ANNOTSETTINGS ", "Select 1.8, 2.0 or 5.0 height, marker circles and MLeader/MText/COGO output."),
                        Cmd("Make Objects Annotative", "CE_MAKEANNOTATIVE ", "Apply CE annotative settings to selected supported objects."),
                        Cmd("Synchronize Annotation Scale", "CE_ANNOSCALESYNC ", "Add the current annotation scale to supported objects."),
                        Cmd("Scale Selected Tables", "CE_TABLESCALE ", "Resize selected tables relative to CE text height."),
                        Cmd("Resolve Annotation Overlaps", "CE_OVERLAPFIX ", "Reposition supported annotations to reduce overlap."),
                        Cmd("Change Objects to Colour 250", "CE_COLOR250 ", "Change selected objects to colour 250."),
                        Cmd("Polyline Direction Arrows", "CE_PLDIR ", "Add, replace or clear linked direction arrows.")),
                    Menu(
                        "CE_TOOLS_CLEANUP_MENU",
                        "Cleanup\nManager",
                        "Run OVERKILL, AUDIT and PURGE together or separately.",
                        Cmd("Full Cleanup", "CE_DRAWCLEAN All ", "Run all drawing-cleanup stages."),
                        Cmd("OVERKILL Only", "CE_DRAWCLEAN Overkill ", "Remove duplicate and overlapping geometry."),
                        Cmd("AUDIT Only", "CE_DRAWCLEAN Audit ", "Audit and fix drawing errors."),
                        Cmd("PURGE Only", "CE_DRAWCLEAN Purge ", "Purge unused named objects.")),
                    Menu(
                        "CE_TOOLS_HATCH_MENU",
                        "Hatch\nTools",
                        "Create and edit transparent civil hatches while keeping grids, labels and linework visible.",
                        Cmd("Hatch Tools", "CE_HATCHTOOLS ", "Open the CE hatch tools menu."),
                        Cmd("Create Transparent Hatches", "CE_HATCHCREATE ", "Create associative hatches from selected closed boundaries."),
                        Cmd("Edit Hatch Settings", "CE_HATCHEDIT ", "Edit selected hatch pattern, scale, angle, colour and transparency."),
                        Cmd("Match Hatch Settings", "CE_HATCHMATCH ", "Copy hatch display settings from one source hatch."),
                        Cmd("Send Hatches Behind Linework", "CE_HATCHBACK ", "Move selected hatches to the back of draw order."))));
        }

        private static void AddGeometryPanel(RibbonTab tab)
        {
            AddPanel(
                tab,
                GeometryPanelId,
                "Geometry",
                Row(
                    Menu(
                        "CE_TOOLS_ROADS_MENU",
                        "Road\nTools",
                        "Road geometry utilities.",
                        Cmd("Bellmouth Densifier", "CE_BMVERT ", "Add equal-chainage vertices to bellmouth polylines.")),
                    Menu(
                        "CE_TOOLS_FEATURE_LINE_MENU",
                        "Feature Line\nTools",
                        "Feature-line creation, reporting, editing, annotation and linked stepped offsets.",
                        Cmd("Feature Line Tools", "CE_FLTOOLS ", "Open the legacy feature-line report and elevation menu."),
                        Cmd("Report", "CE_FLREPORTUI ", "Show feature-line details in a pop-up and optionally place a table."),
                        Cmd("Feature Line Annotation", "CE_FLLABELX ", "Create a feature-line MLeader, MText or COGO point using shared settings."),
                        Cmd("Raise / Lower", "CE_FLRAISEX ", "Explicitly edit selected feature-line elevations after a before/after review."),
                        Cmd("Raise / Lower (Legacy)", "CE_FLRAISE ", "Run the original feature-line elevation editing command."),
                        Cmd("Set Elevation", "CE_FLSETELEV ", "Set selected feature lines to one elevation."),
                        Cmd("Constant Grade Between Endpoints", "CE_FLCONSTGRADE ", "Set all existing points to a constant grade between each feature line's endpoint elevations."),
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
                    Menu(
                        "CE_TOOLS_ALIGNMENT_MENU",
                        "Alignment\nTools",
                        "Alignment reporting, station-offset queries and labels.",
                        Cmd("Alignment Tools", "CE_ALTOOLS ", "Open alignment tools."),
                        Cmd("Alignment Report", "CE_ALREPORTUI ", "Show alignment details in a pop-up and optionally place a table."),
                        Cmd("Station and Offset", "CE_ALSTOFF ", "Report station and signed offset."),
                        Cmd("Station-Offset Annotation", "CE_ALLABELX ", "Create an MLeader, MText or COGO point using shared annotation settings.")),
                    Menu(
                        "CE_TOOLS_PROFILE_MENU",
                        "Profile\nTools",
                        "Profile reporting, station elevations and plan labels.",
                        Cmd("Profile Tools", "CE_PRTOOLS ", "Open profile tools."),
                        Cmd("Batch Profile Views", "CE_PROFILEVIEWBATCHTOOLS ", "Apply profile-view styles, band sets, automatic fit and rebuild options."),
                        Cmd("Profile Report", "CE_PRREPORTUI ", "Show profile details in a pop-up and optionally place a table."),
                        Cmd("Station Elevation", "CE_PRELEV ", "Report elevation and grade at a station."),
                        Cmd("Profile Annotation", "CE_PRLABELX ", "Create an MLeader, MText or COGO point using shared annotation settings.")),
                    Menu(
                        "CE_TOOLS_SURFACE_MENU",
                        "Surface\nTools",
                        "Surface reporting, elevation labels and comparisons.",
                        Cmd("Surface Tools", "CE_SFTOOLS ", "Open surface tools."),
                        Cmd("Surface Report", "CE_SFREPORTUI ", "Show surface details in a pop-up and optionally place a table."),
                        Cmd("Surface Elevation", "CE_SFELEV ", "Report an elevation at a point."),
                        Cmd("Surface Annotation", "CE_SFLABELX ", "Create an MLeader, MText or COGO point using shared annotation settings."),
                        Cmd("Compare Surfaces", "CE_SFCOMPARE ", "Compare two surface elevations.")),
                    Menu(
                        "CE_TOOLS_SURFACE_CORRECTION_MENU",
                        "Surface\nCorrection",
                        "Audit, repair and simplify surfaces through reversible generated copies.",
                        Cmd("Surface Correction Tools", "CE_SURFCTOOLS ", "Open correction, audit, simplify, restore, settings and information workflows."),
                        Cmd("Audit Surface Quality", "CE_SURFAUDIT ", "Screen for spikes, lows, holes and likely contamination."),
                        Cmd("Create Reversible Corrected Surface", "CE_SURFCORRECT ", "Create a separate corrected copy without editing the source."),
                        Cmd("Create Reversible Simplified Surface", "CE_SURFSIMPLIFY ", "Create a separate performance copy."),
                        Cmd("Restore / Remove Generated Copy", "CE_SURFCRESTORE ", "Remove only a CE-generated surface copy."),
                        Cmd("Surface Correction Settings", "CE_SURFCSETTINGS ", "Configure audit and correction thresholds."),
                        Cmd("Surface Correction Information", "CE_SURFCINFO ", "Review generated surface links and settings."),
                        Cmd("Spike and Hole Repair", "CE_SURFSPIKEHOLEFIX ", "Create a repaired surface while keeping the original unchanged.")),
                    Menu(
                        "CE_TOOLS_DYNAMIC_INTERSECTION_MENU",
                        "Dynamic\nIntersections",
                        "Create and maintain linked plan intersection sets.",
                        Cmd("Dynamic Intersection Tools", "CE_INTTOOLS ", "Open intersection create, refresh, information, detach, settings and monitor workflows."),
                        Cmd("Create Linked Intersection Set", "CE_INTCREATE ", "Create linked markers and elevation comparisons."),
                        Cmd("Refresh Linked Intersection Set", "CE_INTREFRESH ", "Refresh a linked set from current geometry."),
                        Cmd("Intersection Set Information", "CE_INTINFO ", "Review sources, generated handles and monitor status."),
                        Cmd("Detach Intersection Set", "CE_INTDETACH ", "Remove the link and keep or delete generated geometry."),
                        Cmd("Dynamic Intersection Settings", "CE_INTSETTINGS ", "Configure marker, tolerance, sampling and corridor-code settings."),
                        Cmd("Dynamic Intersection Monitor", "CE_INTMONITOR ", "Review linked sets and deferred refresh state."))));
        }

        private static void AddCorridorPanel(RibbonTab tab)
        {
            AddPanel(
                tab,
                CorridorPanelId,
                "Corridors",
                Row(Menu(
                    "CE_TOOLS_CORRIDOR_MENU",
                    "Corridor\nTools",
                    "Corridor reporting, annotation, baseline inspection and rebuilding.",
                    Cmd("Corridor Tools", "CE_CORTOOLS ", "Open the legacy corridor tools menu."),
                    Cmd("Corridor Report", "CE_CORREPORTUI ", "Show corridor details in a pop-up and optionally place a table."),
                    Cmd("Baselines and Regions", "CE_CORBASEUI ", "Show baseline and region details in a pop-up and optionally place a table."),
                    Cmd("Corridor Annotation", "CE_CORLABELX ", "Create a corridor MLeader or MText using shared annotation settings."),
                    Cmd("Rebuild Corridors", "CE_CORREBUILDX ", "Explicitly call Corridor.Rebuild() for every editable selected corridor after review."),
                    Cmd("Rebuild Corridors (Legacy)", "CE_CORREBUILD ", "Run the original controlled rebuild command."))));
        }

        private static void AddSiteDesignPanel(RibbonTab tab)
        {
            AddPanel(
                tab,
                SiteDesignPanelId,
                "Site Design",
                Row(
                    Menu(
                        "CE_TOOLS_PARKING_MENU",
                        "Parking\nTools",
                        "Parking layout, reporting, numbering and boundary-driven options.",
                        Cmd("Parking Tools", "CE_PKTOOLS ", "Open legacy parking tools."),
                        Cmd("Single Row", "CE_PKROW ", "Create a straight single row."),
                        Cmd("Double Row", "CE_PKDOUBLE ", "Create opposing rows around an aisle."),
                        Cmd("Parking Report", "CE_PKREPORTUI ", "Show parking bay groups in a pop-up."),
                        Cmd("Validate and Count Bays", "CE_PKCOUNTX ", "Validate and count block or closed-polyline bays."),
                        Cmd("Validate and Number Bays", "CE_PKNUMBER2 ", "Validate and number accepted bays."),
                        Cmd("Number Bays (Legacy Shared)", "CE_PKNUMBERX ", "Run the preserved shared-height parking numbering workflow."),
                        Cmd("Refresh Linked Parking Numbers", "CE_PKNUMBERREFRESH ", "Refresh labels after bay edits."),
                        Cmd("Dynamic Parking Options", "CE_PARKOPTIONS ", "Generate linked parking inside a selected boundary."),
                        Cmd("Refresh Dynamic Parking", "CE_PARKOPTIONSREFRESH ", "Rebuild linked parking after boundary edits."),
                        Cmd("Parking Option Information", "CE_PARKOPTIONSINFO ", "Review linked capacity and sources."),
                        Cmd("Clear Dynamic Parking", "CE_PARKOPTIONSCLEAR ", "Remove generated bays while retaining the boundary.")),
                    Menu(
                        "CE_TOOLS_PARKING_GRADING_MENU",
                        "Parking\nGrading",
                        "Dynamic parking grading and drainage guides.",
                        Cmd("Parking Grading Tools", "CE_PARKGRADETOOLS ", "Open linked parking grading workflows."),
                        Cmd("Create Grading Guides", "CE_PARKGRADECREATE ", "Create linked low-point, crown or valley guides."),
                        Cmd("Refresh Grading Guides", "CE_PARKGRADEREFRESH ", "Rebuild guides from current boundaries."),
                        Cmd("Grading Information", "CE_PARKGRADEINFO ", "Review assumptions and source links."),
                        Cmd("Clear Grading Guides", "CE_PARKGRADECLEAR ", "Remove selected grading guides."),
                        Cmd("Automatic Parking Refresh", "CE_PARKAUTOMONITOR ", "Toggle automatic linked parking refresh."),
                        Cmd("Refresh All Linked Parking", "CE_PARKAUTOREFRESHALL ", "Rebuild every linked parking result."),
                        Cmd("Parking Refresh Status", "CE_PARKAUTOSTATUS ", "Review automatic refresh and pending links.")),
                    Menu(
                        "CE_TOOLS_PARKING_OPTIMIZER_MENU",
                        "Parking\nOptimizer",
                        "Optimize parking layouts around boundaries and obstacles.",
                        Cmd("Parking Optimizer Tools", "CE_PARKOPTIMIZERTOOLS ", "Open optimize, refresh, export, information and clear workflows."),
                        Cmd("Optimize Parking", "CE_PARKOPTIMIZE ", "Generate optimized parking candidates."),
                        Cmd("Refresh Optimized Parking", "CE_PARKOPTREFRESH ", "Recalculate a linked optimized layout."),
                        Cmd("Export Parking Report", "CE_PARKOPTEXPORT ", "Export optimized parking results."),
                        Cmd("Parking Optimizer Information", "CE_PARKOPTINFO ", "Review sources and optimization settings."),
                        Cmd("Clear Optimized Parking", "CE_PARKOPTCLEAR ", "Remove generated optimizer output.")),
                    Menu(
                        "CE_TOOLS_PARKING_SKEW_MENU",
                        "Parking Skew\nValidation",
                        "Validate perpendicular bay width and create correction outlines.",
                        Cmd("Parking Skew Tools", "CE_PKSKTOOLS ", "Open validate, correct, clear, settings and information workflows."),
                        Cmd("Validate Perpendicular Bay Width", "CE_PKSKVALIDATE ", "Measure true perpendicular width."),
                        Cmd("Create Failed-Bay Correction Outlines", "CE_PKSKCORRECT ", "Create target-width outlines without changing source geometry."),
                        Cmd("Clear Skew Review Graphics", "CE_PKSKCLEAR ", "Clear CE skew review graphics."),
                        Cmd("Parking Skew Settings", "CE_PKSKSETTINGS ", "Configure width, tolerance, layers and annotation size."),
                        Cmd("Parking Skew Information", "CE_PKSKINFO ", "Review generated objects and source handles."))));
        }

        private static void AddUtilitiesPanel(RibbonTab tab)
        {
            AddPanel(
                tab,
                UtilitiesPanelId,
                "Utilities",
                Row(
                    Menu(
                        "CE_TOOLS_STORMWATER_MENU",
                        "Stormwater\nProduction",
                        "Sequence networks and create linked alignments and profiles.",
                        Cmd("Stormwater Tools", "CE_SWTOOLS ", "Open the complete stormwater production menu."),
                        Cmd("Sequence Main and Branches", "CE_SWSEQ ", "Sequence the complete stormwater network."),
                        Cmd("Create / Refresh Alignments", "CE_SWALIGN ", "Create linked stormwater alignments."),
                        Cmd("Refresh Alignments", "CE_SWREFRESH ", "Refresh alignments from their sources."),
                        Cmd("Create / Refresh Profiles", "CE_SWPROFILE ", "Create EG profiles and profile views."),
                        Cmd("Stormwater Settings", "CE_SWSETTINGS ", "Configure project styles, layers and labels."),
                        Cmd("Stormwater Information", "CE_SWINFO ", "Review links and current settings.")),
                    Menu(
                        "CE_TOOLS_SEWER_PRODUCTION_MENU",
                        "Sewer Network\nProduction",
                        "Sequence sewer networks, format alignments and create profiles.",
                        Cmd("Sewer Production Tools", "CE_SEWTOOLS ", "Open the complete sewer production menu."),
                        Cmd("Sequence Network", "CE_SEWSEQ ", "Sequence a complete network or selected path."),
                        Cmd("Sequence with Selected Main", "CE_SEWSEQMAIN ", "Select Branch-1 and sequence remaining branches."),
                        Cmd("Create / Refresh Alignments", "CE_SEWALIGN ", "Create branch alignments and labels."),
                        Cmd("Refresh Linked Alignments", "CE_SEWREFRESH ", "Refresh from linked source networks."),
                        Cmd("Format Alignments and Labels", "CE_SEWFORMAT ", "Apply styles and improve label spacing."),
                        Cmd("Create / Refresh Profiles", "CE_SEWPROFILE ", "Create EG profiles and profile views."),
                        Cmd("Sewer Settings", "CE_SEWSETTINGS ", "Configure styles, layers and label height."),
                        Cmd("Sewer Information", "CE_SEWINFO ", "Review links and generated output."),
                        Cmd("Sort Branch Labels", "CE_SEWLABELSORT ", "Stagger branch labels to reduce overlap."),
                        Cmd("Freeze Branch Labels", "CE_SEWLABELFREEZE ", "Freeze accepted label positions."),
                        Cmd("Unfreeze Branch Labels", "CE_SEWLABELUNFREEZE ", "Return labels to automatic sorting.")),
                    Menu(
                        "CE_TOOLS_WATER_PRODUCTION_MENU",
                        "Water Network\nProduction",
                        "Create water alignments, profiles and asset review markers.",
                        Cmd("Water Production Tools", "CE_WATERTOOLS ", "Open the complete water production menu."),
                        Cmd("Sequence Mains and Branches", "CE_WATERSEQ ", "Sequence water routes and branches."),
                        Cmd("Create / Refresh Alignments", "CE_WATERALIGN ", "Create linked water alignments."),
                        Cmd("Refresh Linked Alignments", "CE_WATERREFRESH ", "Refresh from current source geometry."),
                        Cmd("Create / Refresh Profiles", "CE_WATERPROFILE ", "Create EG profiles and profile views."),
                        Cmd("Place Valve and Hydrant Review Markers", "CE_WATERPLACE ", "Place linked valve and hydrant review markers."),
                        Cmd("Refresh Asset Review Markers", "CE_WATERPLACEREFRESH ", "Refresh asset locations from alignments."),
                        Cmd("Water Settings", "CE_WATERSETTINGS ", "Configure styles, layers and spacing."),
                        Cmd("Water Information", "CE_WATERINFO ", "Review links, output and settings.")),
                    Menu(
                        "CE_TOOLS_NETWORK_SCHEDULE_MENU",
                        "Network\nSchedules",
                        "Create linked asset schedules and reports for civil networks.",
                        Cmd("Network Schedule Tools", "CE_NETWORKSCHEDULETOOLS ", "Open create, refresh, export, information and BOQ workflows."),
                        Cmd("Create Network Schedule", "CE_NETWORKSCHEDULE ", "Create a linked asset schedule."),
                        Cmd("Refresh Network Schedule", "CE_NETWORKSCHEDULEREFRESH ", "Refresh linked asset values."),
                        Cmd("Export Network Schedule", "CE_NETWORKSCHEDULEEXPORT ", "Export the schedule to Excel."),
                        Cmd("Network Schedule Information", "CE_NETWORKSCHEDULEINFO ", "Review sources and link status."),
                        Cmd("Create BOQ from Schedule", "CE_NETWORKSCHEDULEBOQ ", "Hand schedule sources to the linked BOQ builder."),
                        Cmd("Network Summary Report", "CE_NETWORKREPORT2 ", "Show a network summary popup and optional table."),
                        Cmd("Selected Network Data", "CE_NETWORKPARTREPORT2 ", "Report selected pipe and structure data."))));
        }

        private static void AddStandardsPanel(RibbonTab tab)
        {
            AddPanel(
                tab,
                StandardsPanelId,
                "Standards & Details",
                Row(
                    Menu(
                        "CE_TOOLS_DESIGN_STANDARDS_MENU",
                        "Design\nStandards",
                        "Browse, search and apply the built-in design-standards reference library.",
                        Cmd("Design Standards Tools", "CE_DESIGNSTANDARDS ", "Open the design-standards library menu."),
                        Cmd("Browse Standards Library", "CE_STDBROWSE ", "Browse standards by engineering category."),
                        Cmd("Search Standards Library", "CE_STDSEARCH ", "Search by code, title, authority or keyword."),
                        Cmd("Apply Standard to Project", "CE_STDAPPLY ", "Record a catalogue item in project standards."),
                        Cmd("Current Project Standards", "CE_STANDARDINFO ", "Report current project standards.")),
                    Menu(
                        "CE_TOOLS_TYPICAL_DETAILS_MENU",
                        "Typical\nDetails",
                        "Configure, search and insert approved typical details.",
                        Cmd("Typical Details Tools", "CE_DETAILTOOLS ", "Open the typical-details workflow."),
                        Cmd("Set Master Library Folder", "CE_DETAILSETROOT ", "Store the project master-detail folder."),
                        Cmd("Search Detail Library", "CE_DETAILSEARCH ", "Search DWG, DXF and PDF assets."),
                        Cmd("Insert Approved Detail", "CE_DETAILINSERT ", "Insert an approved DWG detail as a block."),
                        Cmd("Typical Details Information", "CE_DETAILINFO ", "Review the library root and supported formats."),
                        Cmd("Dynamic Detail Tools", "CE_DETAILPARAMTOOLS ", "Open linked parametric detail workflows.")),
                    Menu(
                        "CE_TOOLS_ENGINEERING_ASSET_MENU",
                        "Engineering\nAssets",
                        "Search, insert and audit the preserved engineering asset catalogue.",
                        Cmd("Asset Library Tools", "CE_ASSETLIBTOOLS ", "Open asset search, insertion and audit workflows."),
                        Cmd("Search Asset Catalogue", "CE_ASSETSEARCH ", "Search the 33-asset catalogue."),
                        Cmd("Insert Engineering Asset", "CE_ASSETINSERT ", "Insert a selected approved asset."),
                        Cmd("Asset Information", "CE_ASSETINFO ", "Review catalogue and source information."),
                        Cmd("Asset Library Settings", "CE_ASSETLIBSETTINGS ", "Configure asset-library locations."),
                        Cmd("Audit Asset Catalogue", "CE_ASSETCATALOGAUDIT ", "Audit catalogue files and checksums."),
                        Cmd("Export Asset Template", "CE_ASSETCATALOGTEMPLATE ", "Create an asset catalogue template."),
                        Cmd("Check Asset Revisions", "CE_ASSETREVISIONCHECK ", "Review revision state for engineering assets."))));
        }

        private static void AddAnalysisPanel(RibbonTab tab)
        {
            AddPanel(
                tab,
                AnalysisPanelId,
                "Analysis",
                Row(
                    Menu(
                        "CE_TOOLS_QUANTITY_MENU",
                        "Quantity &\nBOQ Tools",
                        "Linked bills of quantities, explicit refresh and Excel exports by discipline.",
                        Cmd("BOQ Tools", "CE_BOQTOOLS ", "Open linked BOQ build, refresh, information and export workflows."),
                        Cmd("Build Linked BOQ", "CE_BOQBUILD ", "Create a linked drawing BOQ with quantity, rate and amount columns."),
                        Cmd("Refresh Linked BOQ", "CE_BOQREFRESH ", "Recalculate quantities from current linked source geometry while preserving matching rates."),
                        Cmd("Linked BOQ Information", "CE_BOQINFO ", "Review link schema, discipline, unit scale and stale source handles."),
                        Cmd("Export Linked BOQ to Excel", "CE_BOQEXPORT ", "Refresh and export a linked BOQ as a dependency-free .xlsx workbook."),
                        Cmd("Refresh All Linked Outputs", "CE_REFRESHALL ", "Refresh linked coordinate tables, setting-out schedules, parking labels, surface comparisons, BOQs, cost estimates and cross sections."),
                        Cmd("Linked Output Refresh Status", "CE_REFRESHSTATUS ", "Review linked-output counts and automatic refresh state in the active drawing."),
                        Cmd("Automatic Linked-Table Refresh", "CE_AUTOREFRESH ", "Turn deferred automatic coordinate, setting-out and BOQ table refresh on or off for the active drawing."),
                        Cmd("Road BOQ Excel", "CE_BOQROAD ", "Export road surfacing, layerworks, kerbs, drainage, markings and signs."),
                        Cmd("Platform BOQ Excel", "CE_BOQPLATFORM ", "Export platform, grading, layerworks and earthwork quantities."),
                        Cmd("Stormwater BOQ Excel", "CE_BOQSTORM ", "Export stormwater pipes, culverts, structures and open drainage."),
                        Cmd("Sewer BOQ Excel", "CE_BOQSEWER ", "Export sewer pipe and structure quantities."),
                        Cmd("Water BOQ Excel", "CE_BOQWATER ", "Export water pipe, valve, fitting and hydrant quantities."),
                        Cmd("Bulk-water BOQ Excel", "CE_BOQBULKWATER ", "Export bulk pipeline, storage, pump and fitting quantities."),
                        Cmd("Total Length", "CE_TLENGTH ", "Preserved quick total of selected curve lengths by layer."),
                        Cmd("Total Area", "CE_TAREA ", "Preserved quick total of selected areas by layer.")),
                    Menu(
                        "CE_TOOLS_WATER_SEWER_COST_MENU",
                        "Water & Sewer\nCost Estimate",
                        "Create and maintain the linked water/sewer Excel cost estimate while preserving workbook rates and formatting.",
                        Cmd("Cost Estimate Tools", "CE_WSCOSTTOOLS ", "Open create, refresh, information and automatic-refresh options."),
                        Cmd("Create Linked Cost Estimate", "CE_WSCOSTCREATE ", "Create a linked estimate from the installed or selected Excel template."),
                        Cmd("Refresh Cost Estimate", "CE_WSCOSTREFRESH ", "Update model-derived quantities without replacing workbook rates or formatting."),
                        Cmd("Cost Estimate Information", "CE_WSCOSTINFO ", "Review workbook path, link status, unit scale, asset counts and automatic-refresh state."),
                        Cmd("Automatic Cost Refresh", "CE_WSCOSTAUTO ", "Turn deferred refresh after drawing commands on or off.")),
                    Menu(
                        "CE_TOOLS_DESIGN_REPORT_MENU",
                        "Design\nReports",
                        "Generate current project inventory reports, optional drawing tables and Excel exports.",
                        Cmd("Report & Production Tools", "CE_REPORTTOOLS ", "Open full, discipline, export, summary and drawing-book workflows."),
                        Cmd("Full Design Report", "CE_REPORTFULL ", "Generate a full model-space design report with CE link and layout status."),
                        Cmd("Choose Discipline Report", "CE_REPORTDISC ", "Generate General, Road, Platform, Stormwater, Sewer, Water or Bulk-water report."),
                        Cmd("Road Report", "CE_REPORTROAD ", "Generate the road-design inventory report."),
                        Cmd("Platform Report", "CE_REPORTPLATFORM ", "Generate the platform/grading design report."),
                        Cmd("Stormwater Report", "CE_REPORTSTORM ", "Generate the stormwater design report."),
                        Cmd("Sewer Report", "CE_REPORTSEWER ", "Generate the sewer design report."),
                        Cmd("Water Report", "CE_REPORTWATER ", "Generate the water design report."),
                        Cmd("Bulk-water Report", "CE_REPORTBULKWATER ", "Generate the bulk-water design report."),
                        Cmd("Export Design Report", "CE_REPORTEXPORT ", "Export a full or discipline design inventory as an .xlsx workbook.")),
                    Menu(
                        "CE_TOOLS_DYNAMIC_SECTION_MENU",
                        "Dynamic Cross\nSections",
                        "Create a linked cross section from a user-drawn line and keep it synchronised with monitored drawing changes.",
                        Cmd("Cross-section Tools", "CE_XSTOOLS ", "Open create, refresh, information, detach and monitor workflows."),
                        Cmd("Create Dynamic Cross Section", "CE_XSCREATE ", "Sample intersected surfaces and design objects and create a linked section view."),
                        Cmd("Refresh Dynamic Cross Section", "CE_XSREFRESH ", "Explicitly rebuild a linked section from current source geometry."),
                        Cmd("Cross-section Information", "CE_XSINFO ", "Review source, scales, samples, capture width and generated link status."),
                        Cmd("Detach Dynamic Cross Section", "CE_XSDETACH ", "Remove the link and keep or delete generated section geometry."),
                        Cmd("Dynamic-section Monitor", "CE_XSMONITOR ", "Report automatic update-manager and pending-refresh status."))));
        }

        private static void AddAdvancedPanel(RibbonTab tab)
        {
            AddPanel(
                tab,
                AdvancedPanelId,
                "Advanced Engineering",
                Row(
                    Menu(
                        "CE_TOOLS_HYDRAULIC_REVIEW_MENU",
                        "Hydrology &\nHydraulics",
                        "Catchment, flow, culvert, ponding and return-period review tools.",
                        Cmd("Hydraulic Tools", "CE_HYDRAULICTOOLS ", "Open rational-flow, catchment, culvert and pump review workflows."),
                        Cmd("Rational Flow", "CE_RATIONALFLOW ", "Calculate rational-method peak flow."),
                        Cmd("Quick Catchment Review", "CE_CATCHMENTQUICK ", "Review catchment area and runoff inputs."),
                        Cmd("Culvert Review", "CE_CULVERTREVIEW ", "Review preliminary culvert capacity."),
                        Cmd("Pump Review", "CE_PUMPREVIEW ", "Review pump duty information."),
                        Cmd("Surface Hydrology Tools", "CE_HYDROLOGYTOOLS ", "Open surface flow and catchment workflows."),
                        Cmd("Surface Flow Paths", "CE_SURFACEFLOW ", "Create surface flow-path review output."),
                        Cmd("Delineate Catchment", "CE_CATCHMENTDELINEATE ", "Delineate a surface catchment."),
                        Cmd("Compare Hydrographs", "CE_HYDROGRAPHCOMPARE ", "Compare pre/post hydrograph results."),
                        Cmd("Return-Period Hydrographs", "CE_HYDROGRAPHPERIODS ", "Generate return-period hydrograph reviews."),
                        Cmd("Flow Network and Culvert", "CE_FLOWNETWORK ", "Review a linked flow-network culvert system."),
                        Cmd("Surface Ponding Review", "CE_PONDINGREVIEW ", "Identify and report likely ponding areas."),
                        Cmd("Clear Hydraulic Review", "CE_HYDRAULICCLEAR ", "Clear generated hydraulic review output."),
                        Cmd("Clear Hydrology Review", "CE_HYDROLOGYCLEAR ", "Clear generated hydrology output.")),
                    Menu(
                        "CE_TOOLS_FLOOD_REVIEW_MENU",
                        "Flood Result\nReview",
                        "Review flood properties, frames and browser-based animations.",
                        Cmd("Flood Result Tools", "CE_FLOODRESULTTOOLS ", "Open flood result review workflows."),
                        Cmd("Flood Property Report", "CE_FLOODPROPERTYREPORT ", "Create a property-impact flood report."),
                        Cmd("Set Flood Frame", "CE_FLOODFRAMESET ", "Set plan/animation frame extents."),
                        Cmd("Reset Flood Frame", "CE_FLOODFRAMERESET ", "Clear stored flood frame extents."),
                        Cmd("Create Flood Animation", "CE_FLOODANIMATIONHTML ", "Generate an HTML flood-result animation.")),
                    Menu(
                        "CE_TOOLS_MODEL_EXCHANGE_MENU",
                        "Model & Xref\nExchange",
                        "Package, split, compare, back up and review project models and Xrefs.",
                        Cmd("Xref Project Tools", "CE_XREFPROJECTTOOLS ", "Open discipline split, backup and revision workflows."),
                        Cmd("Split Xrefs by Discipline", "CE_XREFDISCIPLINESPLIT ", "Build discipline-specific Xref sets."),
                        Cmd("Back Up All Xrefs", "CE_XREFBACKUPALL ", "Create a project Xref backup."),
                        Cmd("Xref Revision Dashboard", "CE_XREFREVISIONDASH ", "Review Xref revision differences."),
                        Cmd("Background Xref Tools", "CE_BACKGROUNDTOOLS ", "Open lightweight background workflows."),
                        Cmd("Split Background", "CE_XREFSPLIT ", "Split a selected background Xref."),
                        Cmd("Back Up Background", "CE_XREFBACKUP ", "Back up selected backgrounds."),
                        Cmd("Restore Background", "CE_XREFRESTORE ", "Restore a backed-up background."),
                        Cmd("Model Exchange Tools", "CE_MODELEXCHANGETOOLS ", "Open specialist model exchange workflows."),
                        Cmd("Export Model Package", "CE_MODELEXPORTPACKAGE ", "Create a specialist model package."),
                        Cmd("Import Model Results", "CE_MODELRESULTIMPORT ", "Import supported specialist results."),
                        Cmd("Model Result Information", "CE_MODELRESULTINFO ", "Review imported result links."),
                        Cmd("Model Design Audit", "CE_MODELREPORTTOOLS ", "Open model audit, export and information workflows."),
                        Cmd("Create Model Report", "CE_MODELREPORT ", "Create a model design audit report."),
                        Cmd("Export Model Report", "CE_MODELREPORTEXPORT ", "Export model audit results."),
                        Cmd("Fast Block Edit", "CE_BLOCKEDITFAST ", "Open a block or Xref definition for fast editing.")),
                    Menu(
                        "CE_TOOLS_ENGINEERING_SCHEDULE_MENU",
                        "Engineering\nSchedules",
                        "Linked road, sewer, section and standard quantity schedules.",
                        Cmd("Road Section Data Tools", "CE_ROADSECTIONDATATOOLS ", "Open road cross-section setting-out workflows."),
                        Cmd("Create Road Section Data", "CE_ROADSECTIONDATA ", "Create a linked road cross-section schedule."),
                        Cmd("Refresh Road Section Data", "CE_ROADSECTIONDATAREFRESH ", "Refresh road section values."),
                        Cmd("Export Road Section Data", "CE_ROADSECTIONDATAEXPORT ", "Export road section values."),
                        Cmd("Sewer Excavation Schedule", "CE_SEWEREXCAVATION ", "Create a linked excavation schedule."),
                        Cmd("Refresh Sewer Excavation", "CE_SEWEREXCAVATIONREFRESH ", "Refresh excavation quantities."),
                        Cmd("Export Sewer Excavation", "CE_SEWEREXCAVATIONEXPORT ", "Export excavation quantities."),
                        Cmd("Standard Quantity Tools", "CE_STANDARDQTYTOOLS ", "Open standard quantity workflows."),
                        Cmd("Create Standard Quantity", "CE_STANDARDQTY ", "Create a linked office quantity template."),
                        Cmd("Refresh Standard Quantity", "CE_STANDARDQTYREFRESH ", "Refresh source quantities."),
                        Cmd("Export Standard Quantity", "CE_STANDARDQTYEXPORT ", "Export a standard quantity schedule."),
                        Cmd("Detailed Section Tools", "CE_SECTIONDETAILTOOLS ", "Open linked section-detail annotation workflows."),
                        Cmd("Create Detailed Section", "CE_SECTIONDETAILCREATE ", "Create a linked detailed-section annotation."),
                        Cmd("Refresh Detailed Section", "CE_SECTIONDETAILREFRESH ", "Refresh linked detailed-section output.")),
                    Menu(
                        "CE_TOOLS_REVIEW_PRESENTATION_MENU",
                        "Review &\nPresentation",
                        "Grading diagnostics, survey comparison, pump systems and project presentation.",
                        Cmd("Grading Diagnostics", "CE_GRADINGDIAGNOSTICS ", "Open low-point and low-slope review workflows."),
                        Cmd("Find Low Points", "CE_LOWPOINTS ", "Identify grading low points."),
                        Cmd("Find Low Slopes", "CE_LOWSLOPE ", "Identify areas below the selected slope."),
                        Cmd("Clear Grading Review", "CE_GRADINGREVIEWCLEAR ", "Clear generated grading review output."),
                        Cmd("Survey Comparison Tools", "CE_SURVEYCOMPARETOOLS ", "Compare original and corrected survey surfaces."),
                        Cmd("Survey Changes", "CE_SURVEYCHANGES ", "Create a read-only survey change report."),
                        Cmd("Export Survey Changes", "CE_SURVEYCHANGEEXPORT ", "Export survey comparison results."),
                        Cmd("Pump System Tools", "CE_PUMPSYSTEMTOOLS ", "Open pump-system review workflows."),
                        Cmd("Pump System Review", "CE_PUMPSYSTEMREVIEW ", "Review a pump system and duty points."),
                        Cmd("Pump Folder Review", "CE_PUMPFOLDERREVIEW ", "Review pump files in a selected folder."),
                        Cmd("Pump Curve Template", "CE_PUMPCURVETEMPLATE ", "Create a pump-curve input template."),
                        Cmd("Project Presentation Tools", "CE_PROJECTPRESENTATIONTOOLS ", "Open project presentation workflows."),
                        Cmd("Create Project Presentation", "CE_PRESENTATIONCREATE ", "Create a presentation package."),
                        Cmd("Preview Project Presentation", "CE_PRESENTATIONPREVIEW ", "Preview generated presentation content."),
                        Cmd("Ribbon Icon Settings", "CE_RIBBONICONS ", "Review or configure CE Tools ribbon icon mode."))));
        }

        private static void AddProductionPanel(RibbonTab tab)
        {
            AddPanel(
                tab,
                ProductionPanelId,
                "Production",
                Row(
                    Menu(
                        "CE_TOOLS_CLIENT_BOOK_MENU",
                        "Project Closeout\nClient Book",
                        "Create linked A4/A3 client summary books at project closeout.",
                        Cmd("Project Closeout - A4 and A3", "CE_PROJECTCLOSEOUT ", "Create or refresh the complete A4 and A3 client summary books."),
                        Cmd("Create Client Book", "CE_CLIENTBOOK ", "Choose A4, A3 or both and create linked summary pages."),
                        Cmd("Refresh Client Book", "CE_CLIENTBOOKREFRESH ", "Refresh all linked client-book pages from current project information."),
                        Cmd("Client Book Information", "CE_CLIENTBOOKINFO ", "Review page links, issue stage, revision and stale generated handles."),
                        Cmd("Export Client Book Index", "CE_CLIENTBOOKINDEX ", "Export the linked client-book register to Excel.")),
                    Menu(
                        "CE_TOOLS_PRODUCTION_MENU",
                        "Summary &\nDrawing Books",
                        "Generate project summary sheets and A-series client/construction drawing-book layouts.",
                        Cmd("Production Tools", "CE_REPORTTOOLS ", "Open reports, summaries and drawing-book workflows."),
                        Cmd("Create Project Summary Sheet", "CE_SUMMARYSHEET ", "Create a linked project metadata, discipline and production-readiness summary."),
                        Cmd("Refresh Project Summary", "CE_SUMMARYREFRESH ", "Refresh the summary from current model, links and layouts."),
                        Cmd("Summary Link Information", "CE_SUMMARYINFO ", "Review summary anchor and generated-object link status."),
                        Cmd("Create A-Series Drawing Books", "CE_DRAWINGBOOK ", "Create or refresh A4/A3 client and A1/A0 construction layouts."),
                        Cmd("Export Drawing Book Index", "CE_BOOKINDEX ", "Export the standard and existing layout register to Excel."))));
        }

        private static RibbonRow Row(params RibbonItem[] items)
        {
            var row = new RibbonRow();
            foreach (RibbonItem item in items) row.RowItems.Add(item);
            return row;
        }

        private static void AddPanel(
            RibbonTab tab,
            string panelId,
            string title,
            params RibbonRow[] rows)
        {
            var source = new RibbonPanelSource
            {
                Id = panelId,
                Title = title.ToUpperInvariant()
            };
            foreach (RibbonRow row in rows) source.Rows.Add(row);
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
                Text = text,
                ShowText = true,
                ShowImage = true,
                Size = RibbonItemSize.Large,
                Image = RibbonVisuals.Small(id),
                LargeImage = RibbonVisuals.Large(id),
                ToolTip = toolTip
            };
            foreach (RibbonCommandDefinition command in commands)
                menu.Items.Add(CreateCommandButton(command));
            return menu;
        }

        private static RibbonCommandDefinition Cmd(
            string text,
            string command,
            string toolTip)
        {
            return new RibbonCommandDefinition(text, command, toolTip);
        }

        private static RibbonButton CreateCommandButton(
            RibbonCommandDefinition definition)
        {
            return new RibbonButton
            {
                Id = "CE_TOOLS_COMMAND_" + definition.Command.Trim().Replace(' ', '_'),
                Text = definition.Text,
                ShowText = true,
                ShowImage = true,
                Image = RibbonVisuals.Small(definition.Command),
                Size = RibbonItemSize.Standard,
                CommandParameter = definition.Command,
                CommandHandler = new RibbonCommandHandler(),
                ToolTip = definition.ToolTip
            };
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
            var button = parameter as RibbonButton;
            string command = button == null ? null : button.CommandParameter as string;
            if (string.IsNullOrWhiteSpace(command)) return;

            AcApplication.DocumentManager.MdiActiveDocument?.SendStringToExecute(
                command,
                true,
                false,
                true);
        }
    }
}

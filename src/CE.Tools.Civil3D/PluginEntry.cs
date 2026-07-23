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
    /// <summary>
    /// CE Tools application entry point and ribbon bootstrapper.
    /// </summary>
    public sealed class PluginEntry : IExtensionApplication
    {
        private static bool _ribbonCreated;

        public void Initialize()
        {
            AcApplication.Idle += OnApplicationIdle;
        }

        public void Terminate()
        {
            AcApplication.Idle -= OnApplicationIdle;
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
                    AcApplication.Idle -= OnApplicationIdle;
                }
            }
            catch
            {
                // Civil 3D can raise Idle before the ribbon is fully initialized.
                // Leave the handler attached so a later Idle event can retry.
            }
        }
    }

    internal static class RibbonBuilder
    {
        private const string TabId = "CE_TOOLS_RIBBON_TAB";
        private const string ProjectPanelId = "CE_TOOLS_CATEGORY_PROJECT";
        private const string SurveyPanelId = "CE_TOOLS_CATEGORY_SURVEY";
        private const string DrawingsPanelId = "CE_TOOLS_CATEGORY_DRAWINGS";
        private const string GeometryPanelId = "CE_TOOLS_CATEGORY_GEOMETRY";
        private const string SiteDesignPanelId = "CE_TOOLS_CATEGORY_SITE_DESIGN";
        private const string UtilitiesPanelId = "CE_TOOLS_CATEGORY_UTILITIES";
        private const string AnalysisPanelId = "CE_TOOLS_CATEGORY_ANALYSIS";

        public static bool EnsureCreated()
        {
            RibbonControl ribbon = ComponentManager.Ribbon;
            if (ribbon == null)
            {
                return false;
            }

            RibbonTab tab = ribbon.Tabs.FirstOrDefault(item => item.Id == TabId);
            if (tab == null)
            {
                tab = new RibbonTab
                {
                    Id = TabId,
                    Title = "CE Tools"
                };
                ribbon.Tabs.Add(tab);
            }

            // CE Tools owns this tab. Rebuilding it removes old layouts and prevents
            // duplicated panels or buttons after an upgrade or ribbon reload.
            tab.Panels.Clear();

            AddProjectPanel(tab);
            AddSurveyPanel(tab);
            AddDrawingsPanel(tab);
            AddGeometryPanel(tab);
            AddSiteDesignPanel(tab);
            AddUtilitiesPanel(tab);
            AddAnalysisPanel(tab);
            return true;
        }

        private static void AddProjectPanel(RibbonTab tab)
        {
            AddPanel(
                tab,
                ProjectPanelId,
                "Project",
                Row(
                    CreateMenu(
                        "CE_TOOLS_PROJECT_MENU",
                        "Project Setup",
                        "Create, review and clear portable project information stored in the DWG.",
                        Command("Project Setup", "CE_PROJECTSETUP ",
                            "Create or update the project name, client, location, template and units."),
                        Command("Project Information", "CE_PROJECTINFO ",
                            "Report the CE Tools project information stored in the drawing."),
                        Command("Clear Project Information", "CE_PROJECTCLEAR ",
                            "Remove CE Tools project metadata after confirmation."))),
                Row(
                    CreateMenu(
                        "CE_TOOLS_COORDSYS_MENU",
                        "Coordinate Systems",
                        "Report, search, assign and clear the drawing coordinate system.",
                        Command("Coordinate System Tools", "CE_COORDSYS ",
                            "Open the coordinate-system command menu."),
                        Command("Information", "CE_COORDSYSINFO ",
                            "Report the active drawing coordinate system."),
                        Command("Assign", "CE_COORDSYSASSIGN ",
                            "Validate and assign a coordinate-system code."),
                        Command("Search Library", "CE_COORDSYSSEARCH ",
                            "Search the installed Civil 3D coordinate-system library."),
                        Command("Clear", "CE_COORDSYSCLEAR ",
                            "Clear the drawing coordinate-system assignment after confirmation."))),
                Row(
                    CreateMenu(
                        "CE_TOOLS_STANDARDS_MENU",
                        "Standards Selection",
                        "Record and review the standards selected for the project.",
                        Command("Standards Tools", "CE_STANDARDS ",
                            "Open the project standards menu."),
                        Command("Select Standards", "CE_STANDARDSELECT ",
                            "Record the primary and additional project standards."),
                        Command("Standards Information", "CE_STANDARDINFO ",
                            "Report the standards stored in the drawing."),
                        Command("Clear Standards", "CE_STANDARDCLEAR ",
                            "Clear the standards record after confirmation."))));
        }

        private static void AddSurveyPanel(RibbonTab tab)
        {
            AddPanel(
                tab,
                SurveyPanelId,
                "Survey",
                Row(
                    CreateMenu(
                        "CE_TOOLS_SURVEY_MENU",
                        "Coordinate Tools",
                        "Coordinate labels, COGO-point labels, crosses, tables and polyline vertices.",
                        Command("Coordinate Tools", "CE_COORDINATE ",
                            "Open the coordinate tools menu."),
                        Command("Polyline Vertex COGO Points", "CE_COORDPOLY ",
                            "Create sequential COGO points and an XYZ table from polyline vertices."))));
        }

        private static void AddDrawingsPanel(RibbonTab tab)
        {
            AddPanel(
                tab,
                DrawingsPanelId,
                "Drawings",
                Row(
                    CreateMenu(
                        "CE_TOOLS_DRAWING_MENU",
                        "Drawing Tools",
                        "Ordinary AutoCAD drawing and annotation utilities.",
                        Command("Change Objects to Colour 250", "CE_COLOR250 ",
                            "Change selected objects to AutoCAD colour index 250."),
                        Command("Polyline Direction Arrows", "CE_PLDIR ",
                            "Add, replace or clear direction arrows linked to ordinary polylines."))),
                Row(
                    CreateMenu(
                        "CE_TOOLS_CLEANUP_MENU",
                        "Drawing Cleanup",
                        "Run OVERKILL, AUDIT and PURGE together or one stage at a time.",
                        Command("Full Cleanup", "CE_DRAWCLEAN All ",
                            "Run OVERKILL, AUDIT with fixes and three PURGE passes."),
                        Command("OVERKILL Only", "CE_DRAWCLEAN Overkill ",
                            "Remove duplicate and overlapping supported geometry."),
                        Command("AUDIT Only", "CE_DRAWCLEAN Audit ",
                            "Audit the current drawing and fix detected errors."),
                        Command("PURGE Only", "CE_DRAWCLEAN Purge ",
                            "Run three purge passes for unused named objects."))));
        }

        private static void AddGeometryPanel(RibbonTab tab)
        {
            AddPanel(
                tab,
                GeometryPanelId,
                "Geometry",
                Row(
                    CreateMenu(
                        "CE_TOOLS_ROADS_MENU",
                        "Road Tools",
                        "Road geometry utilities.",
                        Command("Bellmouth Densifier", "CE_BMVERT ",
                            "Insert equal-chainage vertices into line-and-arc polylines.")),
                    CreateMenu(
                        "CE_TOOLS_FEATURE_LINE_MENU",
                        "Feature Line Tools",
                        "Feature-line reporting, elevation editing and point management.",
                        Command("Feature Line Tools", "CE_FLTOOLS ",
                            "Open the feature-line report and elevation menu."),
                        Command("Report", "CE_FLREPORT ",
                            "Report lengths, grades, elevations and point counts."),
                        Command("Raise / Lower", "CE_FLRAISE ",
                            "Raise or lower selected feature lines by an entered offset."),
                        Command("Set Elevation", "CE_FLSETELEV ",
                            "Set all selected feature-line points to one elevation."),
                        Command("Create and Point Edit", "CE_FLEDIT ",
                            "Open creation, surface and point-edit tools."),
                        Command("Create from Object", "CE_FLCREATE ",
                            "Create feature lines from supported AutoCAD curves."),
                        Command("Elevations from Surface", "CE_FLSURFACE ",
                            "Assign feature-line elevations from a Civil 3D surface."),
                        Command("Insert Elevation Point", "CE_FLINSERT ",
                            "Insert an elevation point at a picked location."),
                        Command("Delete Elevation Point", "CE_FLDELETE ",
                            "Delete a confirmed elevation point without deleting a PI."),
                        Command("Weed Elevation Points", "CE_FLWEED ",
                            "Preview and remove redundant elevation points."))),
                Row(
                    CreateMenu(
                        "CE_TOOLS_ALIGNMENT_MENU",
                        "Alignment Tools",
                        "Alignment reporting, station-offset queries and labels.",
                        Command("Alignment Tools", "CE_ALTOOLS ",
                            "Open the alignment tools menu."),
                        Command("Alignment Report", "CE_ALREPORT ",
                            "Report selected alignment properties and combined length."),
                        Command("Station and Offset", "CE_ALSTOFF ",
                            "Report station and signed offset for a picked point."),
                        Command("Station-Offset Label", "CE_ALLABEL ",
                            "Place a quick station-offset MLeader.")),
                    CreateMenu(
                        "CE_TOOLS_PROFILE_MENU",
                        "Profile Tools",
                        "Profile reporting, station elevations and plan labels.",
                        Command("Profile Tools", "CE_PRTOOLS ",
                            "Open the profile tools menu."),
                        Command("Profile Report", "CE_PRREPORT ",
                            "Report profile, alignment, station and PVI information."),
                        Command("Station Elevation", "CE_PRELEV ",
                            "Report profile elevation and grade at a station."),
                        Command("Profile Label", "CE_PRLABEL ",
                            "Place a plan MLeader with station, elevation and grade."))),
                Row(
                    CreateMenu(
                        "CE_TOOLS_SURFACE_MENU",
                        "Surface Tools",
                        "Surface reporting, elevations, labels and comparisons.",
                        Command("Surface Tools", "CE_SFTOOLS ",
                            "Open the surface tools menu."),
                        Command("Surface Report", "CE_SFREPORT ",
                            "Report surface properties and extents."),
                        Command("Surface Elevation", "CE_SFELEV ",
                            "Report a surface elevation at a picked point."),
                        Command("Surface Label", "CE_SFLABEL ",
                            "Place a coordinate and elevation MLeader."),
                        Command("Compare Surfaces", "CE_SFCOMPARE ",
                            "Compare existing and proposed elevations at a point.")),
                    CreateMenu(
                        "CE_TOOLS_CORRIDOR_MENU",
                        "Corridor Tools",
                        "Corridor reporting, baseline inspection and controlled rebuilding.",
                        Command("Corridor Tools", "CE_CORTOOLS ",
                            "Open the corridor tools menu."),
                        Command("Corridor Report", "CE_CORREPORT ",
                            "Report corridor styles, baselines, regions, surfaces and state."),
                        Command("Baselines and Regions", "CE_CORBASE ",
                            "Report baseline, region, source and assembly details."),
                        Command("Rebuild Corridors", "CE_CORREBUILD ",
                            "Preview and rebuild editable out-of-date corridors."))));
        }

        private static void AddSiteDesignPanel(RibbonTab tab)
        {
            AddPanel(
                tab,
                SiteDesignPanelId,
                "Site Design",
                Row(
                    CreateMenu(
                        "CE_TOOLS_PARKING_MENU",
                        "Parking Tools",
                        "Straight parking rows, counting and sequential numbering.",
                        Command("Parking Tools", "CE_PKTOOLS ",
                            "Open the parking tools menu."),
                        Command("Single Row", "CE_PKROW ",
                            "Create a previewed straight parking row."),
                        Command("Double Row", "CE_PKDOUBLE ",
                            "Create opposing parking rows around an aisle."),
                        Command("Count Bays", "CE_PKCOUNT ",
                            "Count selected parking blocks and closed polylines."),
                        Command("Number Bays", "CE_PKNUMBER ",
                            "Place sequential parking-bay numbers."))));
        }

        private static void AddUtilitiesPanel(RibbonTab tab)
        {
            AddPanel(
                tab,
                UtilitiesPanelId,
                "Utilities",
                Row(
                    CreateMenu(
                        "CE_TOOLS_PIPE_NETWORK_MENU",
                        "Pipe Network Tools",
                        "Gravity-network sequencing, branch naming and branch alignments.",
                        Command("Sewer Network Sequencing", "CE_SEWSEQ ",
                            "Rename an entire gravity network or one selected path by branch."),
                        Command("Create / Refresh Branch Alignments", "CE_SEWALIGN ",
                            "Create one alignment and visible plan label for every sequenced branch."))));
        }

        private static void AddAnalysisPanel(RibbonTab tab)
        {
            AddPanel(
                tab,
                AnalysisPanelId,
                "Analysis",
                Row(
                    CreateMenu(
                        "CE_TOOLS_QUANTITY_MENU",
                        "Quantity Tools",
                        "Quick selected-object length and area totals.",
                        Command("Total Length", "CE_TLENGTH ",
                            "Total selected curve lengths with a layer breakdown."),
                        Command("Total Area", "CE_TAREA ",
                            "Total selected closed boundaries, hatches and regions by layer."))));
        }

        private static RibbonRow Row(params RibbonItem[] items)
        {
            var row = new RibbonRow();
            foreach (RibbonItem item in items)
            {
                row.RowItems.Add(item);
            }

            return row;
        }

        private static void AddPanel(
            RibbonTab tab,
            string panelId,
            string title,
            params RibbonRow[] rows)
        {
            var panelSource = new RibbonPanelSource
            {
                Id = panelId,
                Title = title
            };

            foreach (RibbonRow row in rows)
            {
                panelSource.Rows.Add(row);
            }

            tab.Panels.Add(new RibbonPanel { Source = panelSource });
        }

        private static RibbonMenuButton CreateMenu(
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
                ShowImage = false,
                Size = RibbonItemSize.Standard,
                ToolTip = toolTip
            };

            foreach (RibbonCommandDefinition command in commands)
            {
                menu.Items.Add(CreateCommandButton(command));
            }

            return menu;
        }

        private static RibbonCommandDefinition Command(
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
                Id = "CE_TOOLS_COMMAND_" +
                     definition.Command.Trim().Replace(' ', '_'),
                Text = definition.Text,
                ShowText = true,
                ShowImage = false,
                Size = RibbonItemSize.Standard,
                CommandParameter = definition.Command,
                CommandHandler = new RibbonCommandHandler(),
                ToolTip = definition.ToolTip
            };
        }

        private sealed class RibbonCommandDefinition
        {
            public RibbonCommandDefinition(
                string text,
                string command,
                string toolTip)
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
            string command = button?.CommandParameter as string;

            if (string.IsNullOrWhiteSpace(command))
            {
                return;
            }

            AcApplication.DocumentManager.MdiActiveDocument?.SendStringToExecute(
                command,
                true,
                false,
                true);
        }
    }
}

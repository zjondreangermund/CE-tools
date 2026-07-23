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
                // Civil 3D can raise Idle before its ribbon is fully initialized.
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
        private const string StandardsPanelId = "CE_TOOLS_CATEGORY_STANDARDS";
        private const string AnalysisPanelId = "CE_TOOLS_CATEGORY_ANALYSIS";

        public static bool EnsureCreated()
        {
            RibbonControl ribbon = ComponentManager.Ribbon;
            if (ribbon == null) return false;

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

            // CE Tools owns this tab. Rebuild it so old panels and duplicate buttons
            // cannot remain after an upgrade or ribbon reload.
            tab.Panels.Clear();
            AddProjectPanel(tab);
            AddSurveyPanel(tab);
            AddDrawingsPanel(tab);
            AddGeometryPanel(tab);
            AddSiteDesignPanel(tab);
            AddUtilitiesPanel(tab);
            AddStandardsPanel(tab);
            AddAnalysisPanel(tab);
            return true;
        }

        private static void AddProjectPanel(RibbonTab tab)
        {
            AddPanel(
                tab,
                ProjectPanelId,
                "Project",
                Row(Menu(
                    "CE_TOOLS_PROJECT_MENU",
                    "Project Setup",
                    "Create, review, clear and restore portable project information.",
                    Cmd("Project Setup", "CE_PROJECTSETUP ", "Create or update project metadata and review it in a pop-up."),
                    Cmd("Project Information", "CE_PROJECTINFO ", "Review project metadata and optionally place a drawing table."),
                    Cmd("Clear Project Information", "CE_PROJECTCLEAR ", "Clear project metadata after confirmation and keep a recoverable backup."),
                    Cmd("Restore Cleared Information", "CE_PROJECTRESTORE ", "Restore the values saved before the last project clear."))),
                Row(Menu(
                    "CE_TOOLS_COORDSYS_MENU",
                    "Coordinate Systems",
                    "Report, search, assign and clear the drawing coordinate system.",
                    Cmd("Coordinate System Tools", "CE_COORDSYS ", "Open the coordinate-system menu."),
                    Cmd("Information", "CE_COORDSYSINFO ", "Report the current coordinate system."),
                    Cmd("Assign", "CE_COORDSYSASSIGN ", "Open Autodesk's native coordinate-system selection window."),
                    Cmd("Assign by Code", "CE_COORDSYSCODE ", "Advanced direct assignment using a validated Autodesk code."),
                    Cmd("Search Library", "CE_COORDSYSSEARCH ", "Search the installed coordinate-system library."),
                    Cmd("Clear", "CE_COORDSYSCLEAR ", "Clear the assignment after confirmation."))),
                Row(Menu(
                    "CE_TOOLS_STANDARDS_MENU",
                    "Standards Selection",
                    "Select a standards source file and record its project information.",
                    Cmd("Standards Tools", "CE_STANDARDS ", "Open the standards menu."),
                    Cmd("Select Standards", "CE_STANDARDSELECT ", "Browse for a standards file, review it and save its traceable details."),
                    Cmd("Standards Information", "CE_STANDARDINFO ", "Review stored standards and optionally place a drawing table."),
                    Cmd("Clear Standards", "CE_STANDARDCLEAR ", "Clear the standards record."))));
        }

        private static void AddSurveyPanel(RibbonTab tab)
        {
            AddPanel(
                tab,
                SurveyPanelId,
                "Survey",
                Row(Menu(
                    "CE_TOOLS_SURVEY_MENU",
                    "Coordinate Tools",
                    "Coordinate labels, COGO points, crosses, tables and polyline vertices.",
                    Cmd("Coordinate Tools", "CE_COORDINATE ", "Open coordinate tools."),
                    Cmd("Polyline Vertex COGO Points", "CE_COORDPOLY ", "Create sequential COGO points and an XYZ table."))));
        }

        private static void AddDrawingsPanel(RibbonTab tab)
        {
            AddPanel(
                tab,
                DrawingsPanelId,
                "Drawings",
                Row(Menu(
                    "CE_TOOLS_DRAWING_MENU",
                    "Drawing Tools",
                    "Ordinary AutoCAD drawing and annotation utilities.",
                    Cmd("Change Objects to Colour 250", "CE_COLOR250 ", "Change selected objects to colour 250."),
                    Cmd("Polyline Direction Arrows", "CE_PLDIR ", "Add, replace or clear linked direction arrows."))),
                Row(Menu(
                    "CE_TOOLS_CLEANUP_MENU",
                    "Drawing Cleanup",
                    "Run OVERKILL, AUDIT and PURGE together or separately.",
                    Cmd("Full Cleanup", "CE_DRAWCLEAN All ", "Run all drawing-cleanup stages."),
                    Cmd("OVERKILL Only", "CE_DRAWCLEAN Overkill ", "Remove duplicate and overlapping geometry."),
                    Cmd("AUDIT Only", "CE_DRAWCLEAN Audit ", "Audit and fix drawing errors."),
                    Cmd("PURGE Only", "CE_DRAWCLEAN Purge ", "Purge unused named objects."))),
                Row(Menu(
                    "CE_TOOLS_HATCH_MENU",
                    "Hatch Tools",
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
                        "Road Tools",
                        "Road geometry utilities.",
                        Cmd("Bellmouth Densifier", "CE_BMVERT ", "Add equal-chainage vertices to bellmouth polylines.")),
                    Menu(
                        "CE_TOOLS_FEATURE_LINE_MENU",
                        "Feature Line Tools",
                        "Feature-line creation, reporting, editing and linked stepped offsets.",
                        Cmd("Feature Line Tools", "CE_FLTOOLS ", "Open the feature-line report and elevation menu."),
                        Cmd("Report", "CE_FLREPORT ", "Report lengths, grades, elevations and point counts."),
                        Cmd("Raise / Lower", "CE_FLRAISE ", "Raise or lower selected feature lines."),
                        Cmd("Set Elevation", "CE_FLSETELEV ", "Set selected feature lines to one elevation."),
                        Cmd("Create and Point Edit", "CE_FLEDIT ", "Open creation, surface and point-edit tools."),
                        Cmd("Create from Object", "CE_FLCREATE ", "Create feature lines from supported curves."),
                        Cmd("Elevations from Surface", "CE_FLSURFACE ", "Assign elevations from a surface."),
                        Cmd("Insert Elevation Point", "CE_FLINSERT ", "Insert an elevation point."),
                        Cmd("Delete Elevation Point", "CE_FLDELETE ", "Delete a confirmed elevation point."),
                        Cmd("Weed Elevation Points", "CE_FLWEED ", "Remove redundant elevation points."),
                        Cmd("Linked Stepped Offset", "CE_FLREL ", "Create, update, inspect or detach linked stepped offsets."),
                        Cmd("Create Linked Offset", "CE_FLRELCREATE ", "Create relative stepped-offset feature lines."),
                        Cmd("Update Linked Offsets", "CE_FLRELUPDATE ", "Refresh linked offsets from their source."),
                        Cmd("Linked Offset Information", "CE_FLRELINFO ", "Report a linked offset relationship."),
                        Cmd("Detach Linked Offset", "CE_FLRELDETACH ", "Keep geometry but remove the CE relationship."))),
                Row(
                    Menu(
                        "CE_TOOLS_ALIGNMENT_MENU",
                        "Alignment Tools",
                        "Alignment reporting, station-offset queries and labels.",
                        Cmd("Alignment Tools", "CE_ALTOOLS ", "Open alignment tools."),
                        Cmd("Alignment Report", "CE_ALREPORT ", "Report alignment properties."),
                        Cmd("Station and Offset", "CE_ALSTOFF ", "Report station and signed offset."),
                        Cmd("Station-Offset Label", "CE_ALLABEL ", "Place a station-offset MLeader.")),
                    Menu(
                        "CE_TOOLS_PROFILE_MENU",
                        "Profile Tools",
                        "Profile reporting, station elevations and plan labels.",
                        Cmd("Profile Tools", "CE_PRTOOLS ", "Open profile tools."),
                        Cmd("Profile Report", "CE_PRREPORT ", "Report profile information."),
                        Cmd("Station Elevation", "CE_PRELEV ", "Report elevation and grade at a station."),
                        Cmd("Profile Label", "CE_PRLABEL ", "Place a plan profile label."))),
                Row(
                    Menu(
                        "CE_TOOLS_SURFACE_MENU",
                        "Surface Tools",
                        "Surface reporting, elevation labels and comparisons.",
                        Cmd("Surface Tools", "CE_SFTOOLS ", "Open surface tools."),
                        Cmd("Surface Report", "CE_SFREPORT ", "Report surface properties."),
                        Cmd("Surface Elevation", "CE_SFELEV ", "Report an elevation at a point."),
                        Cmd("Surface Label", "CE_SFLABEL ", "Place a surface MLeader."),
                        Cmd("Compare Surfaces", "CE_SFCOMPARE ", "Compare two surface elevations.")),
                    Menu(
                        "CE_TOOLS_CORRIDOR_MENU",
                        "Corridor Tools",
                        "Corridor reporting, baseline inspection and rebuilding.",
                        Cmd("Corridor Tools", "CE_CORTOOLS ", "Open corridor tools."),
                        Cmd("Corridor Report", "CE_CORREPORT ", "Report corridor information."),
                        Cmd("Baselines and Regions", "CE_CORBASE ", "Report baselines and regions."),
                        Cmd("Rebuild Corridors", "CE_CORREBUILD ", "Rebuild editable out-of-date corridors."))));
        }

        private static void AddSiteDesignPanel(RibbonTab tab)
        {
            AddPanel(
                tab,
                SiteDesignPanelId,
                "Site Design",
                Row(Menu(
                    "CE_TOOLS_PARKING_MENU",
                    "Parking Tools",
                    "Straight parking rows, counting and numbering.",
                    Cmd("Parking Tools", "CE_PKTOOLS ", "Open parking tools."),
                    Cmd("Single Row", "CE_PKROW ", "Create a straight single row."),
                    Cmd("Double Row", "CE_PKDOUBLE ", "Create opposing rows around an aisle."),
                    Cmd("Count Bays", "CE_PKCOUNT ", "Count selected parking bays."),
                    Cmd("Number Bays", "CE_PKNUMBER ", "Place sequential bay numbers."))));
        }

        private static void AddUtilitiesPanel(RibbonTab tab)
        {
            AddPanel(
                tab,
                UtilitiesPanelId,
                "Utilities",
                Row(Menu(
                    "CE_TOOLS_PIPE_NETWORK_MENU",
                    "Pipe Network Tools",
                    "Gravity-network sequencing, branch naming and branch alignments.",
                    Cmd("Sewer Network Sequencing", "CE_SEWSEQ ", "Sequence a complete network or selected path."),
                    Cmd("Create / Refresh Branch Alignments", "CE_SEWALIGN ", "Create branch alignments and visible branch labels."))));
        }

        private static void AddStandardsPanel(RibbonTab tab)
        {
            AddPanel(
                tab,
                StandardsPanelId,
                "Standards",
                Row(Menu(
                    "CE_TOOLS_DESIGN_STANDARDS_MENU",
                    "Design Standards",
                    "Browse, search and apply the built-in design-standards reference library.",
                    Cmd("Design Standards Tools", "CE_DESIGNSTANDARDS ", "Open the design-standards library menu."),
                    Cmd("Browse Standards Library", "CE_STDBROWSE ", "Browse standards by engineering category."),
                    Cmd("Search Standards Library", "CE_STDSEARCH ", "Search by code, title, authority or keyword."),
                    Cmd("Apply Standard to Project", "CE_STDAPPLY ", "Record a catalogue item in the existing project standards metadata."),
                    Cmd("Current Project Standards", "CE_STANDARDINFO ", "Report the standards currently stored in the DWG."))));
        }

        private static void AddAnalysisPanel(RibbonTab tab)
        {
            AddPanel(
                tab,
                AnalysisPanelId,
                "Analysis",
                Row(Menu(
                    "CE_TOOLS_QUANTITY_MENU",
                    "Quantity Tools",
                    "Quick length and area totals.",
                    Cmd("Total Length", "CE_TLENGTH ", "Total selected curve lengths by layer."),
                    Cmd("Total Area", "CE_TAREA ", "Total selected areas by layer."))));
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
                Title = title
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
                ShowImage = false,
                Size = RibbonItemSize.Standard,
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
                ShowImage = false,
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
            string command = button?.CommandParameter as string;
            if (string.IsNullOrWhiteSpace(command)) return;

            AcApplication.DocumentManager.MdiActiveDocument?.SendStringToExecute(
                command,
                true,
                false,
                true);
        }
    }
}

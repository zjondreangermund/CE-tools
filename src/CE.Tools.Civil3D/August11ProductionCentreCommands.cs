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
    /// Friendly front door for the production workflows requested during the
    /// 11-August field review.  The complete command inventory remains in the
    /// existing Engineering Intelligence / Workflow Centre; this centre exposes
    /// only the shortest discipline production path.
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
                "Choose a discipline. Each production centre contains Settings first, then Prepare, Create, Design, Complete and Deliver. Use CE-ENGINEERING INTELLIGENCE CENTRE when you need the full command library.",
                new List<DisciplineWorkflowAction>
                {
                    Action("PROJECT PRODUCTION", "CE_PROJECTPRODUCTIONCENTRE", "Project setup, standards, styles, registers and coordinated delivery.", "01 Disciplines"),
                    Action("SURVEY PRODUCTION", "CE_SURVEYPRODUCTIONCENTRE", "Coordinate system, survey data, surfaces and setting-out.", "01 Disciplines"),
                    Action("PLATFORM PRODUCTION", "CE_PLATFORMPRODUCTIONCENTRE", "Platforms, levels, grading, setting-out, quantities and drawings.", "01 Disciplines"),
                    Action("ROAD PRODUCTION", "CE_ROADPRODUCTIONCENTRE", "Cadastral road layout through alignments, profiles and corridors.", "01 Disciplines"),
                    Action("STORMWATER PRODUCTION", "CE_SWPRODUCTIONCENTRE", "Routes, networks, branches, profiles, quantities and drawings.", "01 Disciplines"),
                    Action("SEWER PRODUCTION", "CE_SEWERPRODUCTIONCENTRE", "Midblock/road-reserve routing through network, profiles, setting-out and BOQ.", "01 Disciplines"),
                    Action("WATER PRODUCTION", "CE_WATERPRODUCTIONCENTRE", "Water routes, pressure network production, profiles and assets.", "01 Disciplines"),
                    Action("BULK WATER PRODUCTION", "CE_BULKWATERPRODUCTIONCENTRE", "Bulk-water routes, profiles, quantities and delivery.", "01 Disciplines"),
                    Action("PARKING AREA PRODUCTION", "CE_PARKINGPRODUCTIONCENTRE", "Boundary layout, grading, checks, setting-out and quantities.", "01 Disciplines"),
                    Action("FLOOD PRODUCTION", "CE_FLOODPRODUCTIONCENTRE", "Catchments, hydrology, affected areas, culverts and flood outputs.", "01 Disciplines"),
                    Action("CE-ENGINEERING INTELLIGENCE CENTRE", "CE_ENGINEERINGINTELLIGENCECENTRE", "Open the full CE Tools command/workflow library.", "02 Full library"),
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
                Action("PREPARE - Project Coordination", "CE_PROJECTCOORDINATION", "Coordinate source drawings, location and page setups.", "02 PREPARE"),
                Action("Standards", "CE_STANDARDS", "Select and record project standards.", "02 PREPARE"),
                Action("CREATE - Project Metadata Refresh", "CE_PROJECTMETADATAREFRESH", "Synchronize project metadata into linked project outputs.", "03 CREATE"),
                Action("COMPLETE - Drawing Register", "CE_DRAWINGREGISTEREDIT", "Review drawing numbers, titles, revisions and issue information.", "05 COMPLETE"),
                Action("DELIVER - Drawing / Client Books", "CE_BOOKTOOLS", "Create drawing books, client books and indexes.", "06 DELIVER"),
                Action("▶ RUN COMPLETE PROJECT PRODUCTION", "CE_PROJECTCOORDINATION", "Start the guided project-production path.", "99 RUN COMPLETE")
            });
        }

        [CommandMethod("CE_TOOLS", "CE_SURVEYPRODUCTIONCENTRE", CommandFlags.Modal)]
        public void SurveyProduction()
        {
            RunCentre("SURVEY PRODUCTION", "Coordinate system → existing ground → survey cleanup → setting-out.", new[]
            {
                Action("SETTINGS - Survey Location / Coordinate System", "CE_SURVEYLOCATION", "Choose town/project area and assign the installed Namibia LO system.", "01 SETTINGS"),
                Action("Project Style Centre - Points/Surfaces", "CE_PROJECTSTYLES", "Select point, point-label and surface styles.", "01 SETTINGS"),
                Action("PREPARE - LandXML Import / Export", "CE_LANDXMLTOOLS", "Import or export survey/Civil LandXML.", "02 PREPARE"),
                Action("Surface Tools", "CE_SURFTOOLS", "Create/review existing-ground surfaces.", "03 CREATE"),
                Action("DESIGN - Surface Correction / Review", "CE_SURFCTOOLS", "Audit and create reversible corrected surface copies.", "04 DESIGN"),
                Action("COMPLETE - Vertex Setting-Out", "CE_VERTEXSETTINGOUT", "Generate linked COGO/MText/MLeader setting-out from multiple design strings.", "05 COMPLETE"),
                Action("Grid Setting-Out", "CE_GRIDSETTINGOUT", "Generate linked grid/perimeter setting-out points.", "05 COMPLETE"),
                Action("DELIVER - Survey Comparison / Export", "CE_SURVEYCOMPARETOOLS", "Review corrections and export survey data.", "06 DELIVER"),
                Action("▶ RUN COMPLETE SURVEY PRODUCTION", "CE_SURVEYLOCATION", "Start at survey location / coordinate system.", "99 RUN COMPLETE")
            });
        }

        [CommandMethod("CE_TOOLS", "CE_PLATFORMPRODUCTIONCENTRE", CommandFlags.Modal)]
        public void PlatformProduction()
        {
            RunCentre("PLATFORM PRODUCTION", "Source polygons → feature lines → levels → grading → setting-out → quantities → drawings.", new[]
            {
                Action("SETTINGS - Project Styles / Platform", "CE_PROJECTSTYLES", "Select feature-line, grading, surface and annotation styles.", "01 SETTINGS"),
                Action("PREPARE - Create Feature Lines", "CE_FLCREATE", "Create multiple feature lines from selected polylines.", "02 PREPARE"),
                Action("DESIGN - Platform Slopes / Levels", "CE_PLATFORMSLOPE", "Constant slope, fixed slope or flatten to highest elevation.", "04 DESIGN"),
                Action("Stepped Offsets", "CE_PLATFORMSTEPOFFSETS", "Create linked stepped offsets for multiple platforms.", "04 DESIGN"),
                Action("Drape / Platform Surface", "CE_PLATFORMDRAPE", "Drape linked platform controls to selected surface.", "04 DESIGN"),
                Action("COMPLETE - Setting-Out", "CE_PLATFORMSETTINGOUT", "Vertex/grid setting-out and linked tables.", "05 COMPLETE"),
                Action("Platform Names / Register", "CE_PLATFORMTABLE", "Linked platform names, elevations and register.", "05 COMPLETE"),
                Action("DELIVER - Cut / Fill", "CE_PLATFORMCUTFILL", "Linked NG versus design quantities.", "06 DELIVER"),
                Action("Drawings / Sections", "CE_PLATFORMDRAWINGS", "Create platform layouts and section source lines.", "06 DELIVER"),
                Action("▶ RUN COMPLETE PLATFORM PRODUCTION", "CE_PLATFORMTOOLS", "Open the complete linked platform workflow.", "99 RUN COMPLETE")
            });
        }

        [CommandMethod("CE_TOOLS", "CE_ROADPRODUCTIONCENTRE", CommandFlags.Modal)]
        public void RoadProduction()
        {
            RunCentre("ROAD PRODUCTION", "Cadastral/reserve geometry → road layout → alignment/profile → corridor → setting-out/BOQ → drawings.", new[]
            {
                Action("SETTINGS - Project Road Styles", "CE_PROJECTSTYLES", "Select road alignment/profile/profile-view/band/corridor styles.", "01 SETTINGS"),
                Action("PREPARE - Road Layout Production", "CE_ROADLAYOUTTOOLS", "Reserve centrelines, edges, shoulders, junctions, road names and dimensions.", "02 PREPARE"),
                Action("Road Continuity / Junction Finish", "CE_ROADAUG11TOOLS", "Join reserve centrelines, outside offsets, junction trim boundaries and route annotation.", "02 PREPARE"),
                Action("CREATE - Alignments", "CE_ROADALIGN", "Create linked road alignments.", "03 CREATE"),
                Action("DESIGN - Complete Road Profile", "CE_ROADPROFILEFULL", "Existing ground plus final editable design profile with profile view/bands.", "04 DESIGN"),
                Action("Complete Corridor", "CE_ROADCORRIDORCOMPLETE", "Create/rebuild baselines, regions, targets and corridor surfaces.", "04 DESIGN"),
                Action("COMPLETE - Junction Setting-Out", "CE_JUNCTIONSETTINGOUT4", "Complete one full T/cross junction before continuing to the next.", "05 COMPLETE"),
                Action("Road BOQ", "CE_BOQROAD", "Create linked road quantities.", "05 COMPLETE"),
                Action("DELIVER - Road Report", "CE_REPORTROAD", "Generate the road design report.", "06 DELIVER"),
                Action("▶ RUN COMPLETE ROAD PRODUCTION", "CE_ROADPRODUCTION", "Open the ordered complete road-production workflow.", "99 RUN COMPLETE")
            });
        }

        [CommandMethod("CE_TOOLS", "CE_SWPRODUCTIONCENTRE", CommandFlags.Modal)]
        public void StormwaterProduction()
        {
            RunCentre("STORMWATER PRODUCTION", "Route → network → branches → hydraulic/design checks → profiles → setting-out/BOQ → drawings.", UtilityActions("Stormwater", "CE_SWSETTINGS", "CE_SWSEQ", "CE_SWALIGN", "CE_SWPROFILE", "CE_BOQSTORMWATER", "CE_REPORTSTORMWATER", "CE_SWTOOLS"));
        }

        [CommandMethod("CE_TOOLS", "CE_SEWERPRODUCTIONCENTRE", CommandFlags.Modal)]
        public void SewerProduction()
        {
            RunCentre("SEWER PRODUCTION", "Cadastral/Roads/Existing Ground → route/network/branches → levels/checks → profiles/labels/setting-out/BOQ → drawings/report.", new[]
            {
                Action("SETTINGS - Sewer Settings", "CE_SEWSETTINGS", "Parts, styles, labels, profile and band settings.", "01 SETTINGS"),
                Action("PREPARE - Midblock / Road-Reserve Route", "CE_MIDBLOCKSEWERPRODUCTION", "Continuous selected-side/low-side sewer routes and planning manholes.", "02 PREPARE"),
                Action("CREATE - Multiple Networks from Polylines", "CE_NETWORKFROMPOLYLINESBATCH", "Select many source polylines and create them without duplicate source runs.", "03 CREATE"),
                Action("Sequence Branches / Structures / Pipes", "CE_SEWSEQ", "Build the live network sequence and branch names.", "03 CREATE"),
                Action("DESIGN - Network Data / Levels", "CE_NETWORKDATA", "Review pipe/structure levels, lengths and slopes.", "04 DESIGN"),
                Action("COMPLETE - Alignments", "CE_SEWALIGN", "Create linked branch alignments.", "05 COMPLETE"),
                Action("Profiles", "CE_SEWPROFILE", "Create isolated branch profiles/profile views with automatic band import.", "05 COMPLETE"),
                Action("Labels", "CE_SEWLABELS", "Apply selected pipe/structure labels and branch presentation.", "05 COMPLETE"),
                Action("Setting-Out", "CE_VERTEXSETTINGOUT", "Linked setting-out for design geometry.", "05 COMPLETE"),
                Action("BOQ", "CE_BOQSEWER", "Create linked sewer quantities.", "05 COMPLETE"),
                Action("DELIVER - Sewer Report", "CE_REPORTSEWER", "Generate sewer design report/drawing handoff.", "06 DELIVER"),
                Action("▶ RUN COMPLETE SEWER PRODUCTION", "CE_SEWTOOLS", "Open the ordered sewer production workflow.", "99 RUN COMPLETE")
            });
        }

        [CommandMethod("CE_TOOLS", "CE_WATERPRODUCTIONCENTRE", CommandFlags.Modal)]
        public void WaterProduction()
        {
            RunCentre("WATER PRODUCTION", "Route → pressure network → sequence/design → profiles/assets → quantities → delivery.", UtilityActions("Water", "CE_WATERSETTINGS", "CE_WATERSEQ", "CE_WATERALIGN", "CE_WATERPROFILE", "CE_BOQWATER", "CE_REPORTWATER", "CE_WATERTOOLS"));
        }

        [CommandMethod("CE_TOOLS", "CE_BULKWATERPRODUCTIONCENTRE", CommandFlags.Modal)]
        public void BulkWaterProduction()
        {
            RunCentre("BULK WATER PRODUCTION", "Road-reserve/source route → pressure network → profile/setting-out → quantities → delivery.", new[]
            {
                Action("SETTINGS - Project / Water Styles", "CE_PROJECTSTYLES", "Select pressure-network and profile styles.", "01 SETTINGS"),
                Action("PREPARE - Utility Route from Road Reserve", "CE_UTILITYFROMROADRESERVE", "Create bulk-water planning routes at selected offsets.", "02 PREPARE"),
                Action("CREATE - Multiple Networks from Polylines", "CE_NETWORKFROMPOLYLINESBATCH", "Batch source polylines into pressure network creation.", "03 CREATE"),
                Action("DESIGN - Network Data", "CE_NETWORKDATA", "Review selected pressure-network objects and levels.", "04 DESIGN"),
                Action("COMPLETE - Setting-Out", "CE_VERTEXSETTINGOUT", "Generate linked setting-out.", "05 COMPLETE"),
                Action("BOQ", "CE_BOQBULKWATER", "Create linked bulk-water quantities.", "05 COMPLETE"),
                Action("DELIVER - Bulk Water Report", "CE_REPORTBULKWATER", "Generate bulk-water design report.", "06 DELIVER"),
                Action("▶ RUN COMPLETE BULK WATER PRODUCTION", "CE_NETWORKMULTI", "Open multi-network production and continuation tools.", "99 RUN COMPLETE")
            });
        }

        [CommandMethod("CE_TOOLS", "CE_PARKINGPRODUCTIONCENTRE", CommandFlags.Modal)]
        public void ParkingProduction()
        {
            RunCentre("PARKING AREA PRODUCTION", "Boundary → alternatives/layout → grading → checks/setting-out → quantities → drawings.", new[]
            {
                Action("SETTINGS - Parking Tools", "CE_PKTOOLS", "Parking layout and annotation settings.", "01 SETTINGS"),
                Action("PREPARE - Parking Options", "CE_PARKOPTIONS", "Compare parking arrangements inside a selected boundary.", "02 PREPARE"),
                Action("CREATE - Parking Optimiser", "CE_PARKOPTIMIZE", "Create obstacle-aware parking alternative.", "03 CREATE"),
                Action("DESIGN - Parking Grading", "CE_PARKGRADINGTOOLS", "Create linked grading/drainage guides.", "04 DESIGN"),
                Action("COMPLETE - Skew / Width Validation", "CE_PKSKVALIDATE", "Check perpendicular bay width and skew.", "05 COMPLETE"),
                Action("Setting-Out", "CE_GRIDSETTINGOUT", "Grid/perimeter setting-out where applicable.", "05 COMPLETE"),
                Action("DELIVER - Parking Quantities", "CE_PARKQTYTOOLS", "Create parking/layerwork quantity outputs.", "06 DELIVER"),
                Action("▶ RUN COMPLETE PARKING PRODUCTION", "CE_PKTOOLS", "Open parking tools and continue through the production stages.", "99 RUN COMPLETE")
            });
        }

        [CommandMethod("CE_TOOLS", "CE_FLOODPRODUCTIONCENTRE", CommandFlags.Modal)]
        public void FloodProduction()
        {
            RunCentre("FLOOD PRODUCTION", "Existing ground/catchment → hydrology → flow/affected area → culvert review → outputs.", new[]
            {
                Action("SETTINGS - Hydrology / Flood Inputs", "CE_HYDROLOGYTOOLS", "Review rainfall/runoff and analysis settings.", "01 SETTINGS"),
                Action("PREPARE - Surface / Catchment Review", "CE_SURFTOOLS", "Review the terrain source before flood calculations.", "02 PREPARE"),
                Action("CREATE - Quick Flood / Rational Review", "CE_FLOODQUICK", "Pre/post return-period peak-flow and preliminary culvert screen.", "03 CREATE"),
                Action("DESIGN - Surface Hydrology", "CE_HYDROLOGYREVIEW", "Flow routes, catchments and terrain storage review.", "04 DESIGN"),
                Action("Affected Property / Flood Results", "CE_FLOODRESULTTOOLS", "Review imported specialist flood results and affected properties.", "04 DESIGN"),
                Action("COMPLETE - Culvert Review", "CE_CULVERTREVIEW", "Review candidate crossings/culvert requirements.", "05 COMPLETE"),
                Action("DELIVER - Flood Report", "CE_REPORTFULL", "Generate project/discipline report output.", "06 DELIVER"),
                Action("▶ RUN COMPLETE FLOOD PRODUCTION", "CE_FLOODQUICK", "Start the guided flood production path.", "99 RUN COMPLETE")
            });
        }

        private static IEnumerable<DisciplineWorkflowAction> UtilityActions(string discipline, string settings, string sequence, string alignment, string profile, string boq, string report, string hub)
        {
            return new[]
            {
                Action("SETTINGS - " + discipline + " Settings", settings, "Parts, styles, labels and profile settings.", "01 SETTINGS"),
                Action("PREPARE - Utility Route Planner", "CE_UTILITYFROMROADRESERVE", "Create a preliminary route from road-reserve geometry.", "02 PREPARE"),
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
            ResizeMode = ResizeMode.NoResize;
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
            cards.Children.Add(BuildCard("CE-PRODUCTION CENTRE", "Important commands only. Guided Prepare → Create → Design → Complete → Deliver workflows for every discipline.", "CE_PRODUCTIONCENTRE", card, foreground, muted, accent, 0));
            cards.Children.Add(BuildCard("CE-ENGINEERING INTELLIGENCE CENTRE", "The complete CE Tools command library, utilities, reports, repair tools and advanced engineering workflows.", "CE_ENGINEERINGINTELLIGENCECENTRE", card, foreground, muted, accent, 1));
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
            var border = new Border { Background = card, CornerRadius = new CornerRadius(7), Margin = new Thickness(column == 0 ? 0 : 10, 0, column == 0 ? 10 : 0, 0), Padding = new Thickness(24), BorderBrush = accent, BorderThickness = new Thickness(1) };
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeights.Bold, Foreground = foreground, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new TextBlock { Text = description, FontSize = 13, Foreground = muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 18, 0, 24), MinHeight = 82 });
            var button = new Button { Content = "OPEN CENTRE  ›", Padding = new Thickness(14, 9, 14, 9), FontWeight = FontWeights.SemiBold };
            button.Click += delegate { SelectedCommand = command; DialogResult = true; };
            panel.Children.Add(button);
            border.Child = panel;
            Grid.SetColumn(border, column);
            return border;
        }
    }

    /// <summary>
    /// Dedicated production ribbon. It intentionally uses direct panel Items only,
    /// so it remains compatible with Civil 3D 2023 where RibbonRow is unavailable.
    /// </summary>
    internal static class ProductionWorkflowRibbonBuilder
    {
        private const string TabId = "CE_TOOLS_PRODUCTION_WORKFLOW_TAB";

        internal static bool EnsureCreated()
        {
            RibbonControl ribbon = ComponentManager.Ribbon;
            if (ribbon == null) return false;
            RibbonTab tab = ribbon.Tabs.FirstOrDefault(item => item.Id == TabId);
            if (tab == null)
            {
                tab = new RibbonTab { Id = TabId, Title = "CE PRODUCTION" };
                ribbon.Tabs.Add(tab);
            }
            else tab.Title = "CE PRODUCTION";
            tab.Panels.Clear();
            AddPanel(tab, "CE_PROD_HOME", "START", "CE Tools Home", "CE_WELCOME ");
            AddPanel(tab, "CE_PROD_PROJECT", "PROJECT", "Project Production", "CE_PROJECTPRODUCTIONCENTRE ");
            AddPanel(tab, "CE_PROD_SURVEY", "SURVEY", "Survey Production", "CE_SURVEYPRODUCTIONCENTRE ");
            AddPanel(tab, "CE_PROD_PLATFORM", "PLATFORM", "Platform Production", "CE_PLATFORMPRODUCTIONCENTRE ");
            AddPanel(tab, "CE_PROD_ROAD", "ROAD", "Road Production", "CE_ROADPRODUCTIONCENTRE ");
            AddPanel(tab, "CE_PROD_SW", "STORMWATER", "Stormwater Production", "CE_SWPRODUCTIONCENTRE ");
            AddPanel(tab, "CE_PROD_SEWER", "SEWER", "Sewer Production", "CE_SEWERPRODUCTIONCENTRE ");
            AddPanel(tab, "CE_PROD_WATER", "WATER", "Water Production", "CE_WATERPRODUCTIONCENTRE ");
            AddPanel(tab, "CE_PROD_BW", "BULK WATER", "Bulk Water Production", "CE_BULKWATERPRODUCTIONCENTRE ");
            AddPanel(tab, "CE_PROD_PARK", "PARKING", "Parking Production", "CE_PARKINGPRODUCTIONCENTRE ");
            AddPanel(tab, "CE_PROD_FLOOD", "FLOOD", "Flood Production", "CE_FLOODPRODUCTIONCENTRE ");
            return true;
        }

        private static void AddPanel(RibbonTab tab, string id, string title, string text, string command)
        {
            var source = new RibbonPanelSource { Id = id, Title = title };
            var button = new RibbonButton
            {
                Id = id + "_BUTTON",
                Text = text,
                ShowText = true,
                Size = RibbonItemSize.Large,
                CommandParameter = command,
                CommandHandler = new ProductionRibbonCommandHandler()
            };
            source.Items.Add(button);
            tab.Panels.Add(new RibbonPanel { Source = source });
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

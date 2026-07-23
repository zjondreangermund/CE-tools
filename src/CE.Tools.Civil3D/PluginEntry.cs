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
        private const string ProjectPanelId = "CE_TOOLS_PROJECT_PANEL";
        private const string RoadsPanelId = "CE_TOOLS_ROADS_PANEL";
        private const string AlignmentsPanelId = "CE_TOOLS_ALIGNMENTS_PANEL";
        private const string ProfilesPanelId = "CE_TOOLS_PROFILES_PANEL";
        private const string SurfacesPanelId = "CE_TOOLS_SURFACES_PANEL";
        private const string CorridorsPanelId = "CE_TOOLS_CORRIDORS_PANEL";
        private const string ParkingPanelId = "CE_TOOLS_PARKING_PANEL";
        private const string FeatureLinesPanelId = "CE_TOOLS_FEATURE_LINES_PANEL";
        private const string QuantitiesPanelId = "CE_TOOLS_QUANTITIES_PANEL";
        private const string SurveyPanelId = "CE_TOOLS_SURVEY_PANEL";
        private const string UtilitiesPanelId = "CE_TOOLS_UTILITIES_PANEL";
        private const string DrawingPanelId = "CE_TOOLS_DRAWING_PANEL";

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

            EnsureProjectPanel(tab);
            EnsureRoadsPanel(tab);
            EnsureAlignmentsPanel(tab);
            EnsureProfilesPanel(tab);
            EnsureSurfacesPanel(tab);
            EnsureCorridorsPanel(tab);
            EnsureParkingPanel(tab);
            EnsureFeatureLinesPanel(tab);
            EnsureQuantitiesPanel(tab);
            EnsureSurveyPanel(tab);
            EnsureUtilitiesPanel(tab);
            EnsureDrawingPanel(tab);
            return true;
        }

        private static void EnsureProjectPanel(RibbonTab tab)
        {
            AddPanel(
                tab,
                ProjectPanelId,
                "Project",
                CreateButton(
                    "CE_TOOLS_PROJECT_BUTTON",
                    "Project\nSetup",
                    "CE_PROJECT ",
                    "Create, review or clear portable CE Tools project metadata stored inside the DWG."),
                CreateButton(
                    "CE_TOOLS_PROJECTINFO_BUTTON",
                    "Project\nInfo",
                    "CE_PROJECTINFO ",
                    "Report project name, client, location, coordinate system, standards, template and units."),
                CreateButton(
                    "CE_TOOLS_COORDSYS_BUTTON",
                    "Coordinate\nSystems",
                    "CE_COORDSYS ",
                    "Report, search, assign or clear the Civil 3D drawing coordinate-system zone."),
                CreateButton(
                    "CE_TOOLS_STANDARDS_BUTTON",
                    "Standards\nSelection",
                    "CE_STANDARDS ",
                    "Select, review or clear the project standards recorded inside the DWG."));
        }

        private static void EnsureRoadsPanel(RibbonTab tab)
        {
            AddPanel(
                tab,
                RoadsPanelId,
                "Roads",
                CreateButton(
                    "CE_TOOLS_BMVERT_BUTTON",
                    "Bellmouth\nDensifier",
                    "CE_BMVERT ",
                    "Insert equal-chainage vertices into multiple line-and-arc polylines."));
        }

        private static void EnsureAlignmentsPanel(RibbonTab tab)
        {
            AddPanel(
                tab,
                AlignmentsPanelId,
                "Alignments",
                CreateButton(
                    "CE_TOOLS_ALTOOLS_BUTTON",
                    "Alignment\nTools",
                    "CE_ALTOOLS ",
                    "Report alignments, calculate station/offset and place quick station-offset labels."),
                CreateButton(
                    "CE_TOOLS_ALSTOFF_BUTTON",
                    "Station &&\nOffset",
                    "CE_ALSTOFF ",
                    "Select an alignment and pick a point to report station and signed offset."));
        }

        private static void EnsureProfilesPanel(RibbonTab tab)
        {
            AddPanel(
                tab,
                ProfilesPanelId,
                "Profiles",
                CreateButton(
                    "CE_TOOLS_PRTOOLS_BUTTON",
                    "Profile\nTools",
                    "CE_PRTOOLS ",
                    "Report profiles, query station elevations and place quick profile labels."),
                CreateButton(
                    "CE_TOOLS_PRELEV_BUTTON",
                    "Station &&\nElevation",
                    "CE_PRELEV ",
                    "Select a profile and enter a station to report elevation and instantaneous grade."));
        }

        private static void EnsureSurfacesPanel(RibbonTab tab)
        {
            AddPanel(
                tab,
                SurfacesPanelId,
                "Surfaces",
                CreateButton(
                    "CE_TOOLS_SFTOOLS_BUTTON",
                    "Surface\nTools",
                    "CE_SFTOOLS ",
                    "Report surfaces, query elevations, place elevation labels and compare two surfaces."),
                CreateButton(
                    "CE_TOOLS_SFELEV_BUTTON",
                    "Surface\nElevation",
                    "CE_SFELEV ",
                    "Select a surface and pick a point to report its elevation."),
                CreateButton(
                    "CE_TOOLS_SFCOMPARE_BUTTON",
                    "Compare\nSurfaces",
                    "CE_SFCOMPARE ",
                    "Compare existing and proposed surface elevations at a picked point."));
        }

        private static void EnsureCorridorsPanel(RibbonTab tab)
        {
            AddPanel(
                tab,
                CorridorsPanelId,
                "Corridors",
                CreateButton(
                    "CE_TOOLS_CORTOOLS_BUTTON",
                    "Corridor\nTools",
                    "CE_CORTOOLS ",
                    "Report corridors, inspect baselines and regions, or rebuild out-of-date corridors."),
                CreateButton(
                    "CE_TOOLS_CORBASE_BUTTON",
                    "Baseline &&\nRegions",
                    "CE_CORBASE ",
                    "Report corridor baseline, region, source and assembly information."),
                CreateButton(
                    "CE_TOOLS_CORREBUILD_BUTTON",
                    "Rebuild\nCorridors",
                    "CE_CORREBUILD ",
                    "Preview and rebuild selected editable out-of-date corridors."));
        }

        private static void EnsureParkingPanel(RibbonTab tab)
        {
            AddPanel(
                tab,
                ParkingPanelId,
                "Parking",
                CreateButton(
                    "CE_TOOLS_PKTOOLS_BUTTON",
                    "Parking\nTools",
                    "CE_PKTOOLS ",
                    "Create straight parking rows, count bays and place sequential bay numbers."),
                CreateButton(
                    "CE_TOOLS_PKROW_BUTTON",
                    "Single\nRow",
                    "CE_PKROW ",
                    "Create a previewed single parking row from a straight baseline."),
                CreateButton(
                    "CE_TOOLS_PKDOUBLE_BUTTON",
                    "Double\nRow",
                    "CE_PKDOUBLE ",
                    "Create opposing parking rows around an entered aisle width."));
        }

        private static void EnsureFeatureLinesPanel(RibbonTab tab)
        {
            AddPanel(
                tab,
                FeatureLinesPanelId,
                "Feature Lines",
                CreateButton(
                    "CE_TOOLS_FLTOOLS_BUTTON",
                    "Feature Line\nTools",
                    "CE_FLTOOLS ",
                    "Report feature-line data, raise/lower elevations or set all points to one elevation."),
                CreateButton(
                    "CE_TOOLS_FLEDIT_BUTTON",
                    "Create &&\nPoint Edit",
                    "CE_FLEDIT ",
                    "Create feature lines, assign surface elevations, and insert or delete elevation points."),
                CreateButton(
                    "CE_TOOLS_FLWEED_BUTTON",
                    "Weed\nPoints",
                    "CE_FLWEED ",
                    "Preview and remove redundant feature-line elevation points using tolerances."));
        }

        private static void EnsureQuantitiesPanel(RibbonTab tab)
        {
            AddPanel(
                tab,
                QuantitiesPanelId,
                "Quantities",
                CreateButton(
                    "CE_TOOLS_TLENGTH_BUTTON",
                    "Total\nLength",
                    "CE_TLENGTH ",
                    "Total selected curve lengths and show a layer-by-layer breakdown."),
                CreateButton(
                    "CE_TOOLS_TAREA_BUTTON",
                    "Total\nArea",
                    "CE_TAREA ",
                    "Total selected closed boundaries, hatches and regions by layer."));
        }

        private static void EnsureSurveyPanel(RibbonTab tab)
        {
            AddPanel(
                tab,
                SurveyPanelId,
                "Survey",
                CreateButton(
                    "CE_TOOLS_COORDINATE_BUTTON",
                    "Coordinate\nTools",
                    "CE_COORDINATE ",
                    "XYZ MLeaders, COGO point labels, coordinate crosses and setting-out tables."));
        }

        private static void EnsureUtilitiesPanel(RibbonTab tab)
        {
            AddPanel(
                tab,
                UtilitiesPanelId,
                "Utilities",
                CreateButton(
                    "CE_TOOLS_SEWSEQ_BUTTON",
                    "Sewer\nSequence",
                    "CE_SEWSEQ ",
                    "Select start and end manholes, then rename the connected path Branch/P/MH."));
        }

        private static void EnsureDrawingPanel(RibbonTab tab)
        {
            AddPanel(
                tab,
                DrawingPanelId,
                "Drawing",
                CreateButton(
                    "CE_TOOLS_COLOR250_BUTTON",
                    "Color\n250",
                    "CE_COLOR250 ",
                    "Change preselected or selected drawing objects to AutoCAD colour index 250."));
        }

        private static void AddPanel(
            RibbonTab tab,
            string panelId,
            string title,
            params RibbonButton[] buttons)
        {
            if (PanelExists(tab, panelId))
            {
                return;
            }

            var panelSource = new RibbonPanelSource
            {
                Id = panelId,
                Title = title
            };

            foreach (RibbonButton button in buttons)
            {
                panelSource.Items.Add(button);
            }

            tab.Panels.Add(new RibbonPanel { Source = panelSource });
        }

        private static bool PanelExists(RibbonTab tab, string panelId)
        {
            return tab.Panels.Any(
                item => item.Source != null && item.Source.Id == panelId);
        }

        private static RibbonButton CreateButton(
            string id,
            string text,
            string command,
            string toolTip)
        {
            return new RibbonButton
            {
                Id = id,
                Text = text,
                ShowText = true,
                ShowImage = false,
                Size = RibbonItemSize.Large,
                CommandParameter = command,
                CommandHandler = new RibbonCommandHandler(),
                ToolTip = toolTip
            };
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

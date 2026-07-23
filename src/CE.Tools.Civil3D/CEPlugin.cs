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
        private const string RoadsPanelId = "CE_TOOLS_ROADS_PANEL";
        private const string AlignmentsPanelId = "CE_TOOLS_ALIGNMENTS_PANEL";
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

            EnsureRoadsPanel(tab);
            EnsureAlignmentsPanel(tab);
            EnsureFeatureLinesPanel(tab);
            EnsureQuantitiesPanel(tab);
            EnsureSurveyPanel(tab);
            EnsureUtilitiesPanel(tab);
            EnsureDrawingPanel(tab);
            return true;
        }

        private static void EnsureRoadsPanel(RibbonTab tab)
        {
            if (PanelExists(tab, RoadsPanelId))
            {
                return;
            }

            var panelSource = new RibbonPanelSource
            {
                Id = RoadsPanelId,
                Title = "Roads"
            };

            panelSource.Items.Add(CreateButton(
                "CE_TOOLS_BMVERT_BUTTON",
                "Bellmouth\nDensifier",
                "CE_BMVERT ",
                "Insert equal-chainage vertices into multiple line-and-arc polylines."));

            tab.Panels.Add(new RibbonPanel { Source = panelSource });
        }

        private static void EnsureAlignmentsPanel(RibbonTab tab)
        {
            if (PanelExists(tab, AlignmentsPanelId))
            {
                return;
            }

            var panelSource = new RibbonPanelSource
            {
                Id = AlignmentsPanelId,
                Title = "Alignments"
            };

            panelSource.Items.Add(CreateButton(
                "CE_TOOLS_ALTOOLS_BUTTON",
                "Alignment\nTools",
                "CE_ALTOOLS ",
                "Report alignments, calculate station/offset and place quick station-offset labels."));

            panelSource.Items.Add(CreateButton(
                "CE_TOOLS_ALSTOFF_BUTTON",
                "Station &&\nOffset",
                "CE_ALSTOFF ",
                "Select an alignment and pick a point to report station and signed offset."));

            tab.Panels.Add(new RibbonPanel { Source = panelSource });
        }

        private static void EnsureFeatureLinesPanel(RibbonTab tab)
        {
            if (PanelExists(tab, FeatureLinesPanelId))
            {
                return;
            }

            var panelSource = new RibbonPanelSource
            {
                Id = FeatureLinesPanelId,
                Title = "Feature Lines"
            };

            panelSource.Items.Add(CreateButton(
                "CE_TOOLS_FLTOOLS_BUTTON",
                "Feature Line\nTools",
                "CE_FLTOOLS ",
                "Report feature-line data, raise/lower elevations or set all points to one elevation."));

            panelSource.Items.Add(CreateButton(
                "CE_TOOLS_FLEDIT_BUTTON",
                "Create &&\nPoint Edit",
                "CE_FLEDIT ",
                "Create feature lines, assign surface elevations, and insert or delete elevation points."));

            panelSource.Items.Add(CreateButton(
                "CE_TOOLS_FLWEED_BUTTON",
                "Weed\nPoints",
                "CE_FLWEED ",
                "Preview and remove redundant feature-line elevation points using vertical and spacing tolerances."));

            tab.Panels.Add(new RibbonPanel { Source = panelSource });
        }

        private static void EnsureQuantitiesPanel(RibbonTab tab)
        {
            if (PanelExists(tab, QuantitiesPanelId))
            {
                return;
            }

            var panelSource = new RibbonPanelSource
            {
                Id = QuantitiesPanelId,
                Title = "Quantities"
            };

            panelSource.Items.Add(CreateButton(
                "CE_TOOLS_TLENGTH_BUTTON",
                "Total\nLength",
                "CE_TLENGTH ",
                "Total selected curve lengths and show a layer-by-layer breakdown."));

            panelSource.Items.Add(CreateButton(
                "CE_TOOLS_TAREA_BUTTON",
                "Total\nArea",
                "CE_TAREA ",
                "Total selected closed boundaries, hatches and regions by layer."));

            tab.Panels.Add(new RibbonPanel { Source = panelSource });
        }

        private static void EnsureSurveyPanel(RibbonTab tab)
        {
            if (PanelExists(tab, SurveyPanelId))
            {
                return;
            }

            var panelSource = new RibbonPanelSource
            {
                Id = SurveyPanelId,
                Title = "Survey"
            };

            panelSource.Items.Add(CreateButton(
                "CE_TOOLS_COORDINATE_BUTTON",
                "Coordinate\nTools",
                "CE_COORDINATE ",
                "XYZ MLeaders, COGO point labels, coordinate crosses and setting-out tables."));

            tab.Panels.Add(new RibbonPanel { Source = panelSource });
        }

        private static void EnsureUtilitiesPanel(RibbonTab tab)
        {
            if (PanelExists(tab, UtilitiesPanelId))
            {
                return;
            }

            var panelSource = new RibbonPanelSource
            {
                Id = UtilitiesPanelId,
                Title = "Utilities"
            };

            panelSource.Items.Add(CreateButton(
                "CE_TOOLS_SEWSEQ_BUTTON",
                "Sewer\nSequence",
                "CE_SEWSEQ ",
                "Select only start and end manholes, then rename the connected path Branch/P/MH."));

            tab.Panels.Add(new RibbonPanel { Source = panelSource });
        }

        private static void EnsureDrawingPanel(RibbonTab tab)
        {
            if (PanelExists(tab, DrawingPanelId))
            {
                return;
            }

            var panelSource = new RibbonPanelSource
            {
                Id = DrawingPanelId,
                Title = "Drawing"
            };

            panelSource.Items.Add(CreateButton(
                "CE_TOOLS_COLOR250_BUTTON",
                "Color\n250",
                "CE_COLOR250 ",
                "Change preselected or selected drawing objects to AutoCAD colour index 250."));

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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Windows;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.FloatingToolsCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Opens the current CE Tools ribbon commands in a normal modeless window.
    /// The window can be dragged to another monitor and shows direct command
    /// buttons instead of requiring the ribbon flyout menus.
    /// </summary>
    public sealed class FloatingToolsCommands
    {
        private static FloatingToolsWindow _window;
        private static bool _openedAtStartup;
        private static bool _shortcutAttached;
        private static UIElement _shortcutTarget;

        [CommandMethod("CE_TOOLS", "CE_TOOLSPALETTE", CommandFlags.Modal)]
        public void OpenFloatingTools()
        {
            ShowWindow();
        }

        [CommandMethod("CE_TOOLS", "CE_WORKFLOWS", CommandFlags.Modal)]
        public void OpenWorkflows()
        {
            ShowWindow();
        }

        public static void Initialize()
        {
            AttachShortcut();
        }

        public static void Terminate()
        {
            DetachShortcut();
            if (_window != null)
                _window.Close();
        }

        public static void OpenAtFirstStartup()
        {
            if (_openedAtStartup)
                return;

            _openedAtStartup = true;
            AttachShortcut();
            ShowWindow();
        }

        private static void AttachShortcut()
        {
            if (_shortcutAttached)
                return;

            _shortcutTarget = ComponentManager.ApplicationWindow as UIElement;
            if (_shortcutTarget == null)
                return;

            _shortcutTarget.PreviewKeyDown += OnApplicationPreviewKeyDown;
            _shortcutAttached = true;
        }

        private static void DetachShortcut()
        {
            if (!_shortcutAttached || _shortcutTarget == null)
                return;

            _shortcutTarget.PreviewKeyDown -= OnApplicationPreviewKeyDown;
            _shortcutTarget = null;
            _shortcutAttached = false;
        }

        private static void OnApplicationPreviewKeyDown(object sender, KeyEventArgs args)
        {
            if (args.Key != Key.F ||
                (Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
                return;

            args.Handled = true;
            ShowWindow();
        }

        internal static void ShowWindow()
        {
            try
            {
                RibbonBuilder.EnsureCreated();
                List<FloatingToolDefinition> tools = ReadCurrentRibbonTools();
                if (tools.Count == 0)
                {
                    AcApplication.DocumentManager.MdiActiveDocument?.Editor.WriteMessage(
                        "\nCE_TOOLSPALETTE: no CE Tools ribbon commands are currently available.");
                    return;
                }

                if (_window != null)
                {
                    if (_window.WindowState == WindowState.Minimized)
                        _window.WindowState = WindowState.Normal;
                    _window.Activate();
                    return;
                }

                _window = new FloatingToolsWindow(tools);
                _window.Closed += delegate { _window = null; };
                AcApplication.ShowModelessWindow(_window);
            }
            catch (System.Exception exception)
            {
                AcApplication.DocumentManager.MdiActiveDocument?.Editor.WriteMessage(
                    "\nCE_TOOLSPALETTE could not open the floating launcher. {0}",
                    exception.Message);
            }
        }

        internal static List<FloatingToolDefinition> ReadCurrentRibbonTools()
        {
            var result = new List<FloatingToolDefinition>();
            RibbonControl ribbon = ComponentManager.Ribbon;
            if (ribbon == null)
                return result;

            RibbonTab tab = ribbon.Tabs.FirstOrDefault(
                item => item != null && item.Id == "CE_TOOLS_RIBBON_TAB");
            if (tab == null)
                return result;

            foreach (RibbonPanel panel in tab.Panels)
            {
                if (panel == null || panel.Source == null)
                    continue;

                string panelName = CleanRibbonText(panel.Source.Title);
                foreach (RibbonItem item in panel.Source.Items)
                {
                    RibbonMenuButton menu = item as RibbonMenuButton;
                    if (menu == null)
                        continue;

                    string groupName = CleanRibbonText(menu.Text);
                    foreach (object child in menu.Items)
                    {
                        RibbonMenuItem command = child as RibbonMenuItem;
                        if (command == null)
                            continue;

                        string commandText = command.CommandParameter as string;
                        if (string.IsNullOrWhiteSpace(commandText))
                            continue;

                        result.Add(new FloatingToolDefinition(
                            panelName,
                            groupName,
                            CleanRibbonText(command.Text),
                            commandText,
                            command.ToolTip == null
                                ? string.Empty
                                : command.ToolTip.ToString()));
                    }
                }
            }

            return result
                .OrderBy(item => item.Panel, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Group, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Text, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string CleanRibbonText(string text)
        {
            return string.IsNullOrWhiteSpace(text)
                ? string.Empty
                : text.Replace("\n", " ").Trim();
        }
    }

    internal sealed class FloatingToolsWindow : Window
    {
        private readonly List<FloatingToolButton> _buttons =
            new List<FloatingToolButton>();
        private readonly TextBlock _resultCount;
        private readonly TabControl _tabs;

        public FloatingToolsWindow(IList<FloatingToolDefinition> tools)
        {
            Title = "CE Tools - Discipline Workflow Command Centre";
            Width = 1240.0;
            Height = 820.0;
            MinWidth = 720.0;
            MinHeight = 480.0;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            ShowInTaskbar = true;

            var root = new DockPanel
            {
                Margin = new Thickness(12.0)
            };
            Content = root;

            var header = new Grid
            {
                Margin = new Thickness(0.0, 0.0, 0.0, 10.0)
            };
            header.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1.0, GridUnitType.Star)
            });
            header.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = GridLength.Auto
            });
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            var search = new TextBox
            {
                MinWidth = 360.0,
                Padding = new Thickness(8.0, 5.0, 8.0, 5.0),
                ToolTip = "Search command, group, panel or tooltip"
            };
            Grid.SetColumn(search, 0);
            header.Children.Add(search);

            _resultCount = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12.0, 0.0, 0.0, 0.0)
            };
            Grid.SetColumn(_resultCount, 1);
            header.Children.Add(_resultCount);

            _tabs = new TabControl();
            root.Children.Add(_tabs);

            foreach (WorkflowDefinition workflow in WorkflowCatalog.Create(tools))
                _tabs.Items.Add(CreateWorkflowTab(workflow));

            UpdateFilter(string.Empty);
            search.TextChanged += delegate
            {
                UpdateFilter(search.Text);
            };
            Loaded += delegate
            {
                search.Focus();
            };
            PreviewKeyDown += delegate(object sender, KeyEventArgs args)
            {
                if (args.Key == Key.Escape)
                {
                    search.Clear();
                    args.Handled = true;
                }
            };
            _tabs.SelectionChanged += delegate { UpdateFilter(search.Text); };
        }

        private TabItem CreateWorkflowTab(WorkflowDefinition workflow)
        {
            var page = new StackPanel
            {
                Margin = new Thickness(4.0)
            };
            page.Children.Add(new TextBlock
            {
                Text = workflow.Title,
                FontSize = 22.0,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(4.0, 4.0, 4.0, 2.0)
            });
            page.Children.Add(new TextBlock
            {
                Text = workflow.Description,
                Foreground = Brushes.DimGray,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4.0, 0.0, 4.0, 12.0)
            });

            if (workflow.Steps.Count > 0)
            {
                var flow = new WrapPanel
                {
                    Margin = new Thickness(0.0, 0.0, 0.0, 14.0)
                };
                for (int index = 0; index < workflow.Steps.Count; index++)
                {
                    WorkflowStep step = workflow.Steps[index];
                    FloatingToolDefinition tool = workflow.Tools.FirstOrDefault(
                        item => item.Command.Trim().Equals(
                            step.Command,
                            StringComparison.OrdinalIgnoreCase));
                    if (tool == null)
                        continue;

                    Button stepButton = CreateButton(tool, true);
                    stepButton.Content = new TextBlock
                    {
                        Text = (index + 1).ToString() + ". " + step.Title,
                        FontWeight = FontWeights.SemiBold,
                        TextWrapping = TextWrapping.Wrap,
                        TextAlignment = TextAlignment.Center
                    };
                    stepButton.Width = 150.0;
                    stepButton.MinHeight = 54.0;
                    flow.Children.Add(stepButton);
                    if (index < workflow.Steps.Count - 1)
                    {
                        flow.Children.Add(new TextBlock
                        {
                            Text = "  ▶  ",
                            FontSize = 18.0,
                            VerticalAlignment = VerticalAlignment.Center,
                            Foreground = Brushes.SteelBlue
                        });
                    }
                }
                page.Children.Add(flow);
            }

            page.Children.Add(new TextBlock
            {
                Text = "Available commands",
                FontSize = 16.0,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(4.0, 0.0, 4.0, 6.0)
            });
            var commandWrap = new WrapPanel();
            foreach (FloatingToolDefinition definition in workflow.Tools)
            {
                Button button = CreateButton(definition, false);
                _buttons.Add(new FloatingToolButton(definition, button, workflow.Key));
                commandWrap.Children.Add(button);
            }
            page.Children.Add(commandWrap);

            return new TabItem
            {
                Header = workflow.ShortTitle,
                Tag = workflow.Key,
                Content = new ScrollViewer
                {
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Content = page
                }
            };
        }

        private static Button CreateButton(
            FloatingToolDefinition definition,
            bool workflowStep)
        {
            var content = new Grid();
            content.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = GridLength.Auto
            });
            content.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1.0, GridUnitType.Star)
            });

            try
            {
                var image = new Image
                {
                    Source = RibbonVisuals.Small(definition.Command),
                    Width = 24.0,
                    Height = 24.0,
                    Margin = new Thickness(0.0, 0.0, 8.0, 0.0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(image, 0);
                content.Children.Add(image);
            }
            catch
            {
                // Text-only buttons remain fully usable when icon generation fails.
            }

            var labels = new StackPanel();
            labels.Children.Add(new TextBlock
            {
                Text = definition.Text,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            });
            labels.Children.Add(new TextBlock
            {
                Text = definition.Panel + " / " + definition.Group,
                FontSize = 10.0,
                Foreground = Brushes.DimGray,
                TextWrapping = TextWrapping.Wrap
            });
            Grid.SetColumn(labels, 1);
            content.Children.Add(labels);

            var button = new Button
            {
                Content = content,
                Width = 250.0,
                MinHeight = 58.0,
                Margin = new Thickness(4.0),
                Padding = new Thickness(8.0),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = definition.ToolTip,
                Tag = workflowStep ? "WorkflowStep" : "Command"
            };
            button.Click += delegate
            {
                var document = AcApplication.DocumentManager.MdiActiveDocument;
                if (document == null)
                    return;

                document.SendStringToExecute(
                    definition.Command,
                    true,
                    false,
                    true);
            };
            return button;
        }

        private void UpdateFilter(string searchText)
        {
            string query = (searchText ?? string.Empty).Trim();
            string selectedWorkflow = _tabs.SelectedItem is TabItem selected
                ? selected.Tag as string
                : null;
            int visible = 0;
            foreach (FloatingToolButton item in _buttons)
            {
                bool onPage = string.Equals(
                    item.WorkflowKey,
                    selectedWorkflow,
                    StringComparison.OrdinalIgnoreCase);
                bool show = onPage && (query.Length == 0 ||
                    item.Definition.SearchText.IndexOf(
                        query,
                        StringComparison.OrdinalIgnoreCase) >= 0);
                item.Button.Visibility = show
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                if (show)
                    visible++;
            }

            _resultCount.Text = visible.ToString() +
                " commands  •  Ctrl+F opens this window";
        }
    }

    internal sealed class FloatingToolDefinition
    {
        public FloatingToolDefinition(
            string panel,
            string group,
            string text,
            string command,
            string toolTip)
        {
            Panel = panel ?? string.Empty;
            Group = group ?? string.Empty;
            Text = text ?? string.Empty;
            Command = command ?? string.Empty;
            ToolTip = toolTip ?? string.Empty;
            SearchText = Panel + " " + Group + " " + Text + " " + Command + " " + ToolTip;
        }

        public string Panel { get; private set; }
        public string Group { get; private set; }
        public string Text { get; private set; }
        public string Command { get; private set; }
        public string ToolTip { get; private set; }
        public string SearchText { get; private set; }
    }

    internal sealed class FloatingToolButton
    {
        public FloatingToolButton(
            FloatingToolDefinition definition,
            Button button,
            string workflowKey)
        {
            Definition = definition;
            Button = button;
            WorkflowKey = workflowKey;
        }

        public FloatingToolDefinition Definition { get; private set; }
        public Button Button { get; private set; }
        public string WorkflowKey { get; private set; }
    }

    internal sealed class WorkflowStep
    {
        public WorkflowStep(string title, string command)
        {
            Title = title;
            Command = command;
        }

        public string Title { get; private set; }
        public string Command { get; private set; }
    }

    internal sealed class WorkflowDefinition
    {
        public WorkflowDefinition(
            string key,
            string shortTitle,
            string title,
            string description,
            IEnumerable<FloatingToolDefinition> tools,
            params WorkflowStep[] steps)
        {
            Key = key;
            ShortTitle = shortTitle;
            Title = title;
            Description = description;
            Tools = tools.ToList();
            Steps = steps.ToList();
        }

        public string Key { get; private set; }
        public string ShortTitle { get; private set; }
        public string Title { get; private set; }
        public string Description { get; private set; }
        public List<FloatingToolDefinition> Tools { get; private set; }
        public List<WorkflowStep> Steps { get; private set; }
    }

    internal static class WorkflowCatalog
    {
        public static IEnumerable<WorkflowDefinition> Create(
            IList<FloatingToolDefinition> tools)
        {
            yield return Build(
                "general", "General", "General Workflow",
                "Start with project information and standards, coordinate the discipline models, refresh linked data, produce quantities and issue reports.",
                tools, null,
                Step("Project setup", "CE_PROJECTSETUP"),
                Step("Project standards", "CE_PROJECTSTYLES"),
                Step("Design and coordinate", "CE_REFRESHALL"),
                Step("Create BOQs", "CE_BOQTOOLS"),
                Step("Generate reports", "CE_PRESENTATIONTOOLS"));

            yield return Build(
                "roads", "Roads", "Roads Workflow",
                "Create and style road alignments, profiles and corridors; generate cross sections, quantities and production outputs.",
                tools, new[] { "ROAD", "ALIGN", "CORRIDOR", "PROFILEVIEW", "CROSS", "SECTION", "INTERSECTION", "PARK", "BOQ" },
                Step("Create alignments", "CE_ROADALIGN"),
                Step("Create profiles", "CE_ROADPROFILES"),
                Step("Create corridors", "CE_ROADCORRIDORS"),
                Step("Create cross sections", "CE_CROSSSECTION"),
                Step("Create BOQ", "CE_BOQROAD"),
                Step("Generate report", "CE_PRESENTATIONTOOLS"));

            yield return Build(
                "stormwater", "Stormwater", "Stormwater Workflow",
                "Delineate catchments, review hydrology, create the stormwater network and profiles, then produce quantities and reports.",
                tools, new[] { "SW", "STORM", "HYDRO", "CATCHMENT", "CULVERT", "FLOOD", "BOQ" },
                Step("Delineate catchments", "CE_CATCHMENTDELINEATE"),
                Step("Run hydrology", "CE_HYDROLOGYTOOLS"),
                Step("Create network", "CE_SWSEQ"),
                Step("Create profiles", "CE_SWPROFILE"),
                Step("Create BOQ", "CE_BOQSTORM"),
                Step("Generate report", "CE_PRESENTATIONTOOLS"));

            yield return Build(
                "sewer", "Sewer", "Sewer Workflow",
                "Sequence sewer branches, create linked alignments and profiles, validate the network, then create the sewer BOQ and report.",
                tools, new[] { "SEWER", "SEW", "BRANCH", "NETWORK", "PROFILE", "BOQ" },
                Step("Sequence network", "CE_SEWERSEQ"),
                Step("Create alignments", "CE_SEWERALIGN"),
                Step("Create profiles", "CE_SEWPROFILE"),
                Step("Run design checks", "CE_SEWERPRODUCTION"),
                Step("Create BOQ", "CE_BOQSEWER"),
                Step("Generate report", "CE_PRESENTATIONTOOLS"));

            yield return Build(
                "water", "Water", "Water Workflow",
                "Create water alignments and profiles, build the pipe network, place valves, run checks, then create the BOQ and report.",
                tools, new[] { "WATER", "HYDRAULIC", "PUMP", "VALVE", "NETWORK", "BOQ" },
                Step("Create alignment", "CE_WATERALIGN"),
                Step("Create profile", "CE_WATERPROFILE"),
                Step("Create pipe network", "CE_WATERSEQ"),
                Step("Place valves", "CE_WATERPLACE"),
                Step("Run design check", "CE_WATERINFO"),
                Step("Create BOQ", "CE_BOQWATER"),
                Step("Generate report", "CE_PRESENTATIONTOOLS"));

            yield return Build(
                "bulkwater", "Bulk Water", "Bulk Water Workflow",
                "Develop bulk-water routes and profiles, review hydraulic assets and create linked quantities and reports.",
                tools, new[] { "WATER", "BULK", "HYDRAULIC", "PUMP", "PROFILE", "BOQ" },
                Step("Create alignment", "CE_WATERALIGN"),
                Step("Create profile", "CE_WATERPROFILE"),
                Step("Create network", "CE_WATERSEQ"),
                Step("Review hydraulics", "CE_HYDRAULICTOOLS"),
                Step("Create BOQ", "CE_BOQBULKWATER"),
                Step("Generate report", "CE_PRESENTATIONTOOLS"));

            yield return Build(
                "flood", "Flood", "Flood Workflow",
                "Prepare terrain and hydrology, review flood results and property impacts, create presentation frames and report outputs.",
                tools, new[] { "FLOOD", "HYDRO", "SURFACE", "CATCHMENT", "ANIMATION", "REPORT" },
                Step("Prepare surface", "CE_SURFCTOOLS"),
                Step("Run hydrology", "CE_HYDROLOGYTOOLS"),
                Step("Review flood results", "CE_FLOODRESULTTOOLS"),
                Step("Review properties", "CE_FLOODPROPERTYREPORT"),
                Step("Create animation", "CE_FLOODANIMATIONHTML"),
                Step("Generate report", "CE_PRESENTATIONTOOLS"));
        }

        private static WorkflowDefinition Build(
            string key,
            string shortTitle,
            string title,
            string description,
            IList<FloatingToolDefinition> allTools,
            string[] commandTokens,
            params WorkflowStep[] steps)
        {
            IEnumerable<FloatingToolDefinition> selected = commandTokens == null
                ? allTools
                : allTools.Where(tool =>
                    commandTokens.Any(token =>
                        tool.SearchText.IndexOf(
                            token,
                            StringComparison.OrdinalIgnoreCase) >= 0));

            var requiredCommands = new HashSet<string>(
                steps.Select(step => step.Command),
                StringComparer.OrdinalIgnoreCase);
            selected = selected.Concat(allTools.Where(tool =>
                requiredCommands.Contains(tool.Command.Trim())));

            return new WorkflowDefinition(
                key,
                shortTitle,
                title,
                description,
                selected.Distinct().OrderBy(tool => tool.Text),
                steps);
        }

        private static WorkflowStep Step(string title, string command)
        {
            return new WorkflowStep(title, command);
        }
    }
}

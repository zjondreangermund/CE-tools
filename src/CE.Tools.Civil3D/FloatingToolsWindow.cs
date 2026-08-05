using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Windows;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.FloatingToolsCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Opens every command declared by CE Tools in a normal modeless window.
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

            // Civil 3D 2023 exposes the Autodesk ribbon as a WPF UIElement.
            // ApplicationWindow is not a reliable keyboard-event target in that
            // host, which previously left Ctrl+F registered in source but inert.
            _shortcutTarget = ComponentManager.Ribbon as UIElement;
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

        internal static void ReloadWindow()
        {
            if (_window != null)
            {
                _window.Close();
                _window = null;
            }
            ShowWindow();
        }

        internal static List<FloatingToolDefinition> ReadCurrentRibbonTools()
        {
            var result = new List<FloatingToolDefinition>();
            RibbonControl ribbon = ComponentManager.Ribbon;
            RibbonTab tab = ribbon == null
                ? null
                : ribbon.Tabs.FirstOrDefault(
                    item => item != null && item.Id == "CE_TOOLS_RIBBON_TAB");
            if (tab != null)
            {
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
            }

            return MergeDeclaredCommands(result)
                .OrderBy(item => item.Panel, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Group, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Text, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static IEnumerable<FloatingToolDefinition> MergeDeclaredCommands(
            IEnumerable<FloatingToolDefinition> ribbonTools)
        {
            var commands = new Dictionary<string, FloatingToolDefinition>(
                StringComparer.OrdinalIgnoreCase);
            foreach (FloatingToolDefinition tool in ribbonTools)
            {
                string name = tool.Command.Trim();
                if (name.Length > 0 && !commands.ContainsKey(name))
                    commands.Add(name, tool);
            }

            foreach (FloatingToolDefinition tool in ReadDeclaredCommands())
            {
                string name = tool.Command.Trim();
                if (name.Length > 0 && !commands.ContainsKey(name))
                    commands.Add(name, tool);
            }
            return commands.Values;
        }

        internal static IEnumerable<FloatingToolDefinition> ReadDeclaredCommands()
        {
            Type[] types;
            try
            {
                types = Assembly.GetExecutingAssembly().GetTypes();
            }
            catch (ReflectionTypeLoadException exception)
            {
                types = exception.Types.Where(item => item != null).ToArray();
            }

            foreach (Type type in types.OrderBy(item => item.FullName))
            {
                foreach (MethodInfo method in type.GetMethods(
                    BindingFlags.Instance |
                    BindingFlags.Static |
                    BindingFlags.Public |
                    BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly))
                {
                    object[] attributes;
                    try
                    {
                        attributes = method.GetCustomAttributes(
                            typeof(CommandMethodAttribute),
                            false);
                    }
                    catch
                    {
                        continue;
                    }

                    foreach (object attribute in attributes)
                    {
                        string command = ReadAttributeText(attribute, "GlobalName");
                        if (string.IsNullOrWhiteSpace(command))
                            continue;

                        yield return new FloatingToolDefinition(
                            "Command Catalogue",
                            FriendlyName(type.Name.Replace("Commands", string.Empty)),
                            FriendlyName(method.Name),
                            command.Trim() + " ",
                            "Run " + command.Trim() + ". Declared by " +
                            type.Name + "." + method.Name + ".");
                    }
                }
            }
        }

        private static string ReadAttributeText(object attribute, string propertyName)
        {
            if (attribute == null)
                return string.Empty;
            try
            {
                PropertyInfo property = attribute.GetType().GetProperty(
                    propertyName,
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic);
                object value = property == null ? null : property.GetValue(attribute, null);
                return value as string ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string FriendlyName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var characters = new List<char>();
            char previous = '\0';
            foreach (char current in value.Replace('_', ' ').Trim())
            {
                if (characters.Count > 0 && current != ' ' &&
                    char.IsUpper(current) &&
                    (char.IsLower(previous) || char.IsDigit(previous)))
                    characters.Add(' ');
                characters.Add(current);
                previous = current;
            }
            return new string(characters.ToArray());
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
        private readonly TextBlock _usageTotals;
        private readonly TabControl _tabs;
        private readonly TextBox _search;
        private readonly ComboBox _projectSelector;
        private readonly DispatcherTimer _usageTimer;
        private readonly IList<FloatingToolDefinition> _tools;
        private readonly Dictionary<string, TabItem> _usageTabs =
            new Dictionary<string, TabItem>(StringComparer.OrdinalIgnoreCase);
        private readonly List<Button> _stepButtons = new List<Button>();
        private WorkflowStep _activeStep;
        private string _activeStepWorkflow;
        private string _activeProjectKey;
        private string _selectedProjectKey;
        private bool _refreshingProjectSelector;

        public FloatingToolsWindow(IList<FloatingToolDefinition> tools)
        {
            _tools = tools ?? new List<FloatingToolDefinition>();
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

            _search = new TextBox
            {
                MinWidth = 360.0,
                Padding = new Thickness(8.0, 5.0, 8.0, 5.0),
                ToolTip = "Search command, group, panel or tooltip"
            };
            Grid.SetColumn(_search, 0);
            header.Children.Add(_search);

            _resultCount = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12.0, 0.0, 0.0, 0.0)
            };
            Grid.SetColumn(_resultCount, 1);
            header.Children.Add(_resultCount);

            var analytics = new Grid
            {
                Margin = new Thickness(0.0, 0.0, 0.0, 10.0)
            };
            analytics.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = GridLength.Auto
            });
            analytics.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(340.0)
            });
            analytics.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1.0, GridUnitType.Star)
            });
            analytics.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = GridLength.Auto
            });
            DockPanel.SetDock(analytics, Dock.Top);
            root.Children.Add(analytics);

            var projectLabel = new TextBlock
            {
                Text = "Project / drawing:",
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0.0, 0.0, 8.0, 0.0)
            };
            Grid.SetColumn(projectLabel, 0);
            analytics.Children.Add(projectLabel);

            _projectSelector = new ComboBox
            {
                DisplayMemberPath = "SelectorText",
                MinWidth = 280.0,
                MaxDropDownHeight = 360.0,
                Padding = new Thickness(6.0, 3.0, 6.0, 3.0),
                ToolTip = "Current drawing and the last 10 opened saved DWGs"
            };
            Grid.SetColumn(_projectSelector, 1);
            analytics.Children.Add(_projectSelector);

            _usageTotals = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(14.0, 0.0, 14.0, 0.0),
                Foreground = Brushes.DarkSlateGray
            };
            Grid.SetColumn(_usageTotals, 2);
            analytics.Children.Add(_usageTotals);

            var clearUsage = new Button
            {
                Content = "Clear project stats",
                Padding = new Thickness(12.0, 5.0, 12.0, 5.0),
                ToolTip = "Clear command history, favorites and tracked time for the selected drawing only"
            };
            Grid.SetColumn(clearUsage, 3);
            analytics.Children.Add(clearUsage);

            _tabs = new TabControl();
            root.Children.Add(_tabs);

            AddUsageTab("favorites", "⭐ Favorites");
            AddUsageTab("mostused", "🔥 Most Used");
            AddUsageTab("recent", "🕒 Recent");
            foreach (WorkflowDefinition workflow in WorkflowCatalog.Create(_tools))
                _tabs.Items.Add(CreateWorkflowTab(workflow));

            if (_tabs.Items.Count > 3) _tabs.SelectedIndex = 3;
            RefreshProjectSelector(false);
            RefreshUsageTabs();
            RefreshUsageTotals();
            UpdateFilter(string.Empty);
            _search.TextChanged += delegate
            {
                UpdateFilter(_search.Text);
            };
            Loaded += delegate
            {
                _search.Focus();
                _usageTimer.Start();
            };
            PreviewKeyDown += delegate(object sender, KeyEventArgs args)
            {
                if (args.Key == Key.Escape)
                {
                    _search.Clear();
                    args.Handled = true;
                }
            };
            _tabs.SelectionChanged += delegate { UpdateFilter(_search.Text); };
            _projectSelector.SelectionChanged += delegate
            {
                if (_refreshingProjectSelector) return;
                ProjectUsageSummary selected =
                    _projectSelector.SelectedItem as ProjectUsageSummary;
                _selectedProjectKey = selected == null ? string.Empty : selected.Key;
                _projectSelector.ToolTip = selected == null ||
                    string.IsNullOrWhiteSpace(selected.FullName)
                    ? "Current drawing and the last 10 opened saved DWGs"
                    : selected.FullName;
                RefreshUsageTabs();
                RefreshUsageTotals();
                UpdateFilter(_search.Text);
            };
            clearUsage.Click += delegate { ClearSelectedProject(); };
            _usageTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1.0)
            };
            _usageTimer.Tick += delegate { RefreshUsageTotals(); };
            CommandUsageTracker.UsageChanged += OnUsageChanged;
            Closed += delegate
            {
                _usageTimer.Stop();
                CommandUsageTracker.UsageChanged -= OnUsageChanged;
            };
        }

        private void AddUsageTab(string key, string title)
        {
            var tab = new TabItem { Header = title, Tag = key };
            _usageTabs[key] = tab;
            _tabs.Items.Add(tab);
        }

        private void OnUsageChanged(object sender, EventArgs args)
        {
            Dispatcher.BeginInvoke(new Action(delegate
            {
                RefreshProjectSelector(true);
                RefreshUsageTabs();
                RefreshUsageTotals();
                UpdateFilter(_search.Text);
            }));
        }

        private void RefreshProjectSelector(bool preserveSelection)
        {
            string currentProjectKey = CommandUsageTracker.CurrentProjectKey();
            bool documentChanged = !string.Equals(
                currentProjectKey,
                _activeProjectKey,
                StringComparison.OrdinalIgnoreCase);
            _activeProjectKey = currentProjectKey;
            string requestedKey = preserveSelection && !documentChanged
                ? _selectedProjectKey
                : currentProjectKey;
            IList<ProjectUsageSummary> projects = CommandUsageTracker.Projects(10);
            ProjectUsageSummary selected = string.IsNullOrWhiteSpace(requestedKey)
                ? null
                : projects.FirstOrDefault(item =>
                    string.Equals(item.Key, requestedKey, StringComparison.OrdinalIgnoreCase));

            _refreshingProjectSelector = true;
            _projectSelector.ItemsSource = projects;
            _projectSelector.SelectedItem = selected;
            _refreshingProjectSelector = false;
            _selectedProjectKey = selected == null ? string.Empty : selected.Key;
            _projectSelector.ToolTip = selected != null &&
                !string.IsNullOrWhiteSpace(selected.FullName)
                ? selected.FullName
                : "Current drawing and the last 10 opened saved DWGs";
        }

        private void RefreshUsageTotals()
        {
            ProjectUsageSummary summary = CommandUsageTracker.Summary(
                SelectedProjectKey());
            _usageTotals.Text = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                "Total project time {0}  •  CE command time {1}  •  clicks {2:N0}  •  saved ≈{3:N0} clicks / {4}",
                FormatDuration(summary.ActiveSeconds),
                FormatDuration(summary.CommandSeconds),
                summary.Clicks,
                summary.EstimatedClicksSaved,
                FormatDuration(summary.EstimatedSecondsSaved));
        }

        private string SelectedProjectKey()
        {
            return string.IsNullOrWhiteSpace(_selectedProjectKey)
                ? CommandUsageTracker.CurrentProjectKey()
                : _selectedProjectKey;
        }

        private void ClearSelectedProject()
        {
            ProjectUsageSummary summary = CommandUsageTracker.Summary(
                SelectedProjectKey());
            string name = string.IsNullOrWhiteSpace(summary.DisplayName)
                ? "this drawing"
                : summary.DisplayName;
            MessageBoxResult result = System.Windows.MessageBox.Show(
                "Clear all tracked time, command statistics and favorites for " +
                    name + "? Other drawings will not be changed.",
                "CE Tools - Clear Project Statistics",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;
            CommandUsageTracker.ClearProject(summary.Key);
        }

        private void RefreshUsageTabs()
        {
            string projectKey = SelectedProjectKey();
            _buttons.RemoveAll(item =>
                string.Equals(item.WorkflowKey, "favorites", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.WorkflowKey, "mostused", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.WorkflowKey, "recent", StringComparison.OrdinalIgnoreCase));
            BuildUsageTab("favorites", CommandUsageTracker.Favorites(projectKey),
                "Right-click any command and choose Add to Favorites.");
            BuildUsageTab("mostused", CommandUsageTracker.MostUsed(projectKey, 24),
                "Commands ranked automatically by completed executions.");
            BuildUsageTab("recent", CommandUsageTracker.Recent(projectKey, 24),
                "The latest completed CE Tools commands for the selected drawing.");
        }

        private void BuildUsageTab(
            string key,
            IList<CommandUsageRecord> records,
            string description)
        {
            TabItem tab;
            if (!_usageTabs.TryGetValue(key, out tab)) return;
            var page = new StackPanel { Margin = new Thickness(4.0) };
            page.Children.Add(new TextBlock
            {
                Text = description,
                Foreground = Brushes.DimGray,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4.0, 4.0, 4.0, 10.0)
            });
            var wrap = new WrapPanel();
            foreach (CommandUsageRecord record in records)
            {
                FloatingToolDefinition definition = _tools.FirstOrDefault(item =>
                    string.Equals(item.Command.Trim(), record.Command,
                        StringComparison.OrdinalIgnoreCase)) ??
                    new FloatingToolDefinition(
                        "Command Catalogue",
                        "Tracked command",
                        record.Command,
                        record.Command + " ",
                        "Run " + record.Command + ".");
                Button button = CreateButton(
                    definition,
                    false,
                    null,
                    true,
                    SelectedProjectKey());
                _buttons.Add(new FloatingToolButton(definition, button, key));
                wrap.Children.Add(button);
            }
            if (records.Count == 0)
            {
                wrap.Children.Add(new TextBlock
                {
                    Text = key == "favorites"
                        ? "No favorites yet. Right-click a command card to add one."
                        : "Usage statistics will appear after CE Tools commands complete.",
                    Margin = new Thickness(8.0),
                    Foreground = Brushes.Gray
                });
            }
            page.Children.Add(wrap);
            tab.Content = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = page
            };
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

                    Button stepButton = CreateButton(
                        tool,
                        true,
                        delegate(Button clicked)
                        {
                            bool clear = ReferenceEquals(_activeStep, step) &&
                                string.Equals(_activeStepWorkflow, workflow.Key,
                                    StringComparison.OrdinalIgnoreCase);
                            foreach (Button item in _stepButtons) item.Background = null;
                            _activeStep = clear ? null : step;
                            _activeStepWorkflow = clear ? null : workflow.Key;
                            if (!clear) clicked.Background = Brushes.LightBlue;
                            UpdateFilter(_search.Text);
                        },
                        false,
                        null);
                    stepButton.Content = new TextBlock
                    {
                        Text = (index + 1).ToString() + ". " + step.Title,
                        FontWeight = FontWeights.SemiBold,
                        TextWrapping = TextWrapping.Wrap,
                        TextAlignment = TextAlignment.Center
                    };
                    stepButton.Width = 150.0;
                    stepButton.MinHeight = 54.0;
                    stepButton.ToolTip = step.Title +
                        " — click to show only the commands for this workflow step; click again to show all.";
                    _stepButtons.Add(stepButton);
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
                Button button = CreateButton(definition, false, null, false, null);
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
            bool workflowStep,
            Action<Button> customClick,
            bool showUsage,
            string usageProjectKey)
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
            if (showUsage)
            {
                CommandUsageRecord usage = CommandUsageTracker.Read(
                    usageProjectKey,
                    definition.Command);
                labels.Children.Add(new TextBlock
                {
                    Text = UsageSummary(usage),
                    FontSize = 10.0,
                    Foreground = Brushes.DarkSlateGray,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0.0, 3.0, 0.0, 0.0)
                });
            }
            labels.Children.Add(new TextBlock
            {
                Text = definition.Panel + " / " + definition.Group,
                FontSize = 10.0,
                Foreground = Brushes.DimGray,
                TextWrapping = TextWrapping.Wrap
            });
            labels.Children.Add(new TextBlock
            {
                Text = definition.Command.Trim(),
                FontSize = 10.0,
                Foreground = Brushes.SteelBlue,
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
                if (customClick != null)
                {
                    customClick(button);
                    return;
                }
                var document = AcApplication.DocumentManager.MdiActiveDocument;
                if (document == null)
                    return;

                document.SendStringToExecute(
                    definition.Command,
                    true,
                    false,
                    true);
            };
            if (!workflowStep)
            {
                var favorite = new MenuItem();
                var menu = new ContextMenu();
                menu.Items.Add(favorite);
                menu.Opened += delegate
                {
                    string projectKey = string.IsNullOrWhiteSpace(usageProjectKey)
                        ? CommandUsageTracker.CurrentProjectKey()
                        : usageProjectKey;
                    favorite.Header = CommandUsageTracker.Read(
                        projectKey,
                        definition.Command).IsFavorite
                        ? "Remove from Favorites"
                        : "Add to Favorites";
                };
                favorite.Click += delegate
                {
                    string projectKey = string.IsNullOrWhiteSpace(usageProjectKey)
                        ? CommandUsageTracker.CurrentProjectKey()
                        : usageProjectKey;
                    CommandUsageTracker.ToggleFavorite(
                        projectKey,
                        definition.Command);
                };
                button.ContextMenu = menu;
            }
            return button;
        }

        private static string UsageSummary(CommandUsageRecord usage)
        {
            if (usage == null) return string.Empty;
            return string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                "Clicks {0:N0}  •  time {1}  •  saved ≈{2:N0} clicks / {3}",
                usage.Clicks,
                FormatDuration(usage.TotalSeconds),
                usage.EstimatedClicksSaved,
                FormatDuration(usage.EstimatedSecondsSaved));
        }

        private static string FormatDuration(double seconds)
        {
            TimeSpan value = TimeSpan.FromSeconds(Math.Max(0.0, seconds));
            if (value.TotalHours >= 1.0)
                return string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    "{0:0.0}h", value.TotalHours);
            if (value.TotalMinutes >= 1.0)
                return string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    "{0:0.0}m", value.TotalMinutes);
            return string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                "{0:0}s", value.TotalSeconds);
        }

        private void UpdateFilter(string searchText)
        {
            string query = (searchText ?? string.Empty).Trim();
            string selectedWorkflow = _tabs.SelectedItem is TabItem selected
                ? selected.Tag as string
                : null;
            int visible = 0;
            int available = 0;
            foreach (FloatingToolButton item in _buttons)
            {
                bool onPage = string.Equals(
                    item.WorkflowKey,
                    selectedWorkflow,
                    StringComparison.OrdinalIgnoreCase);
                bool matchesStep = _activeStep == null ||
                    !string.Equals(_activeStepWorkflow, selectedWorkflow,
                        StringComparison.OrdinalIgnoreCase) ||
                    _activeStep.Matches(item.Definition);
                if (onPage && matchesStep)
                    available++;
                bool show = onPage && matchesStep && (query.Length == 0 ||
                    item.Definition.SearchText.IndexOf(
                        query,
                        StringComparison.OrdinalIgnoreCase) >= 0);
                item.Button.Visibility = show
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                if (show)
                    visible++;
            }

            _resultCount.Text = visible.ToString() + " of " +
                available.ToString() + " commands  •  Ctrl+F opens this window";
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
        public WorkflowStep(
            string title,
            string command,
            params string[] filterTokens)
        {
            Title = title;
            Command = command;
            FilterTokens = (filterTokens ?? new string[0])
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .ToList();
            if (FilterTokens.Count == 0)
            {
                var ignored = new HashSet<string>(
                    new[] { "OPEN", "CREATE", "REVIEW", "GENERATE", "CONFIGURE", "RUN" },
                    StringComparer.OrdinalIgnoreCase);
                FilterTokens = (title ?? string.Empty)
                    .Split(new[] { ' ', '/', '-' }, StringSplitOptions.RemoveEmptyEntries)
                    .Where(item => item.Length >= 4 && !ignored.Contains(item))
                    .ToList();
            }
        }

        public string Title { get; private set; }
        public string Command { get; private set; }
        public IList<string> FilterTokens { get; private set; }

        public bool Matches(FloatingToolDefinition definition)
        {
            if (definition == null) return false;
            if (string.Equals(
                    definition.Command.Trim(),
                    Command,
                    StringComparison.OrdinalIgnoreCase))
                return true;
            return FilterTokens.Any(token =>
                definition.SearchText.IndexOf(
                    token,
                    StringComparison.OrdinalIgnoreCase) >= 0);
        }
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
                "all", "All", "All CE Tools Commands",
                "Search and launch every CE Tools command declared by the loaded plug-in, including specialist commands that are not pinned to the ribbon.",
                tools, null);

            yield return Build(
                "general", "General", "General Workflow",
                "Start with project information and standards, coordinate the discipline models, refresh linked data, produce quantities and issue reports.",
                tools, new[] { "PROJECT", "STANDARD", "PRESENTATION", "REPORT", "BOQ", "REFRESH", "XREF", "MODEL", "DRAW", "DETAIL", "ASSET" },
                Step("Open Phase 1 utilities", "CE_PHASE1"),
                Step("Project setup", "CE_PROJECTSETUP"),
                Step("Project standards", "CE_STANDARDSELECT"),
                Step("Refresh linked outputs", "CE_REFRESHALL"),
                Step("Review refresh status", "CE_REFRESHSTATUS"),
                Step("Configure automatic refresh", "CE_AUTOREFRESH"),
                Step("Create BOQs", "CE_BOQTOOLS"),
                Step("Generate reports", "CE_PRESENTATIONTOOLS"));

            yield return Build(
                "survey", "Survey", "Survey Workflow",
                "Set the drawing coordinate system, create linked survey points and crosses, generate dynamic polyline/feature-line setting-out points and export tables, then refresh linked outputs after survey edits.",
                tools, new[] { "SURVEY", "COORD", "COGO", "PLDIR" },
                Step("Set coordinate system", "CE_COORDSYSASSIGN"),
                Step("Open survey cleanup", "CE_SURVEYCLEANUP"),
                Step("Create linked point", "CE_COORDPICK2"),
                Step("Create coordinate cross", "CE_COORDCROSS2"),
                Step("Create geometry setting-out points", "CE_COORDPOLY2"),
                Step("Create coordinate table", "CE_COORDTABLE2"),
                Step("Refresh linked coordinates", "CE_COORDREFRESH"),
                Step("Show polyline direction", "CE_PLDIR"));

            yield return Build(
                "roads", "Roads", "Roads Workflow",
                "Create and style road alignments, profiles and corridors; generate cross sections, quantities and production outputs.",
                tools, new[] { "ROAD", "ALIGN", "CORRIDOR", "PROFILEVIEW", "CROSS", "SECTION", "INTERSECTION", "PARK", "BOQ" },
                Step("Open Road Production workflow", "CE_ROADPRODUCTION"),
                Step("Create alignments", "CE_ROADALIGN"),
                Step("Create profiles", "CE_ROADPROFILES"),
                Step("Create or review assembly", "CE_ASSEMBLYTOOLS"),
                Step("Create corridors", "CE_ROADCORRIDORS"),
                Step("Create cross sections", "CE_CROSSSECTION"),
                Step("Create BOQ", "CE_BOQROAD"),
                Step("Generate report", "CE_PRESENTATIONTOOLS"));

            yield return Build(
                "stormwater", "Stormwater", "Stormwater Workflow",
                "Delineate catchments, review hydrology, create the stormwater network and profiles, then produce quantities and reports.",
                tools, new[] { "SW", "STORM", "HYDRO", "CATCHMENT", "CULVERT", "FLOOD", "BOQ" },
                Step("Open Stormwater workflow", "CE_SWTOOLS"),
                Step("Review surface hydrology", "CE_HYDROLOGYTOOLS"),
                Step("Sequence network", "CE_SWSEQ"),
                Step("Create alignments", "CE_SWALIGN"),
                Step("Create profile views", "CE_SWPROFILE"),
                Step("Configure production settings", "CE_SWSETTINGS"),
                Step("Create BOQ", "CE_BOQSTORM"),
                Step("Generate report", "CE_PRESENTATIONTOOLS"));

            yield return Build(
                "sewer", "Sewer", "Sewer Workflow",
                "Sequence sewer branches, create linked alignments and profiles, validate the network, then create the sewer BOQ and report.",
                tools, new[] { "SEWER", "SEW", "BRANCH", "NETWORK", "PROFILE", "BOQ" },
                Step("Open Sewer workflow", "CE_SEWTOOLS", "CE_SEWTOOLS"),
                Step("Sequence network", "CE_SEWSEQ", "CE_SEWSEQ", "CE_SEWLABELS"),
                Step("Create alignments", "CE_SEWALIGN", "CE_SEWALIGN", "CE_SEWREFRESH", "CE_SEWFORMAT"),
                Step("Create profile views", "CE_SEWPROFILE"),
                Step("Configure production settings", "CE_SEWSETTINGS", "CE_SEWSETTINGS", "PROJECT STYLE"),
                Step("Create BOQ", "CE_BOQSEWER", "CE_BOQSEWER", "SEWER BOQ"),
                Step("Refresh cost estimate", "CE_WSCOSTREFRESH", "CE_WSCOST"),
                Step("Generate report", "CE_PRESENTATIONTOOLS", "SEWER REPORT", "DISCIPLINE REPORT"));

            yield return Build(
                "water", "Water", "Water Workflow",
                "Create water alignments and profiles, build the pipe network, place valves, run checks, then create the BOQ and report.",
                tools, new[] { "WATER", "HYDRAULIC", "PUMP", "VALVE", "NETWORK", "BOQ" },
                Step("Open Water workflow", "CE_WATERTOOLS"),
                Step("Sequence network", "CE_WATERSEQ"),
                Step("Create alignments", "CE_WATERALIGN"),
                Step("Create profile views", "CE_WATERPROFILE"),
                Step("Place review assets", "CE_WATERPLACE"),
                Step("Configure production settings", "CE_WATERSETTINGS"),
                Step("Create BOQ", "CE_BOQWATER"),
                Step("Refresh cost estimate", "CE_WSCOSTREFRESH"),
                Step("Generate report", "CE_PRESENTATIONTOOLS"));

            yield return Build(
                "bulkwater", "Bulk Water", "Bulk Water Workflow",
                "Develop bulk-water routes and profiles, review hydraulic assets and create linked quantities and reports.",
                tools, new[] { "WATER", "BULK", "HYDRAULIC", "PUMP", "PROFILE", "BOQ" },
                Step("Open Water workflow", "CE_WATERTOOLS"),
                Step("Review hydraulics", "CE_HYDRAULICTOOLS"),
                Step("Review pump system", "CE_PUMPSYSTEMTOOLS"),
                Step("Create BOQ", "CE_BOQBULKWATER"),
                Step("Generate report", "CE_PRESENTATIONTOOLS"));

            yield return Build(
                "flood", "Flood", "Flood Workflow",
                "Prepare terrain and hydrology, review flood results and property impacts, create presentation frames and report outputs.",
                tools, new[] { "FLOOD", "HYDRO", "SURFACE", "CATCHMENT", "ANIMATION", "REPORT" },
                Step("Review surface hydrology", "CE_HYDROLOGYTOOLS"),
                Step("Trace surface flow", "CE_SURFACEFLOW"),
                Step("Delineate catchment", "CE_CATCHMENTDELINEATE"),
                Step("Open flood-result review", "CE_FLOODRESULTTOOLS"),
                Step("Create property report", "CE_FLOODPROPERTYREPORT"),
                Step("Export flood animation", "CE_FLOODANIMATIONHTML"),
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
                OrderForWorkflow(selected.Distinct(), steps),
                steps);
        }

        private static IEnumerable<FloatingToolDefinition> OrderForWorkflow(
            IEnumerable<FloatingToolDefinition> tools,
            IEnumerable<WorkflowStep> steps)
        {
            var order = steps
                .Select((step, index) => new { step.Command, Index = index })
                .ToDictionary(item => item.Command, item => item.Index,
                    StringComparer.OrdinalIgnoreCase);
            return tools
                .Select((tool, index) => new { Tool = tool, Original = index })
                .OrderBy(item => order.ContainsKey(item.Tool.Command.Trim())
                    ? order[item.Tool.Command.Trim()]
                    : int.MaxValue)
                .ThenBy(item => item.Tool.Panel, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Tool.Group, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Original)
                .Select(item => item.Tool);
        }

        private static WorkflowStep Step(
            string title,
            string command,
            params string[] filterTokens)
        {
            return new WorkflowStep(title, command, filterTokens);
        }
    }
}

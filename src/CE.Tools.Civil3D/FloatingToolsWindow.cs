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

        [CommandMethod("CE_TOOLS", "CE_TOOLSPALETTE", CommandFlags.Modal)]
        public void OpenFloatingTools()
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

        private static List<FloatingToolDefinition> ReadCurrentRibbonTools()
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

        public FloatingToolsWindow(IList<FloatingToolDefinition> tools)
        {
            Title = "CE Tools - Floating Command Window";
            Width = 1120.0;
            Height = 760.0;
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

            var wrap = new WrapPanel
            {
                Orientation = Orientation.Horizontal
            };
            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = wrap
            };
            root.Children.Add(scroll);

            foreach (FloatingToolDefinition definition in tools)
            {
                var button = CreateButton(definition);
                _buttons.Add(new FloatingToolButton(definition, button));
                wrap.Children.Add(button);
            }

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
        }

        private static Button CreateButton(FloatingToolDefinition definition)
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
                ToolTip = definition.ToolTip
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
            int visible = 0;
            foreach (FloatingToolButton item in _buttons)
            {
                bool show = query.Length == 0 ||
                    item.Definition.SearchText.IndexOf(
                        query,
                        StringComparison.OrdinalIgnoreCase) >= 0;
                item.Button.Visibility = show
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                if (show)
                    visible++;
            }

            _resultCount.Text = visible.ToString() + " commands";
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
            Button button)
        {
            Definition = definition;
            Button = button;
        }

        public FloatingToolDefinition Definition { get; private set; }
        public Button Button { get; private set; }
    }
}

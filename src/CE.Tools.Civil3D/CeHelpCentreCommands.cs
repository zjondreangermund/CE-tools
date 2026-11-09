using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.CeHelpCentreCommands))]

namespace CETools.Civil3D
{
    public sealed class CeHelpCentreCommands
    {
        [CommandMethod("CE_TOOLS", "CE_HELP", CommandFlags.Modal)]
        public void OpenHelp() { CeHelpCentreWindow.Show(); }

        [CommandMethod("CE_TOOLS", "CE_TOOLSHELP", CommandFlags.Modal)]
        public void OpenHelpAlias() { CeHelpCentreWindow.Show(); }
    }

    internal sealed class CeHelpCentreWindow : Window
    {
        private static CeHelpCentreWindow _window;
        private readonly IList<FloatingToolDefinition> _tools;
        private readonly StackPanel _catalogue = new StackPanel();
        private readonly TextBox _search = new TextBox();
        private readonly TextBlock _count = new TextBlock();
        private readonly TextBlock _detailTitle = new TextBlock();
        private readonly TextBlock _detailCommand = new TextBlock();
        private readonly TextBlock _detailDescription = new TextBlock();

        private CeHelpCentreWindow(IList<FloatingToolDefinition> tools)
        {
            _tools = tools.Where(x => x != null && !string.IsNullOrWhiteSpace(x.Command))
                .GroupBy(x => x.Command.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(x => ProductionPanelOrder(x.Panel))
                .ThenBy(x => x.Panel, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Group, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Text, StringComparer.OrdinalIgnoreCase)
                .ToList();

            Title = "CE Tools - Help Centre";
            Width = 1180; Height = 820; MinWidth = 760; MinHeight = 540;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = true;

            var root = new DockPanel { Margin = new Thickness(18) };
            Content = root;
            var header = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
            header.Children.Add(new TextBlock {
                Text = "CE TOOLS HELP CENTRE", FontSize = 25, FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(65, 164, 255)),
                Margin = new Thickness(0, 0, 0, 5)
            });
            header.Children.Add(new TextBlock {
                Text = "Browse CE Tools commands by discipline and tool group. Expand a section to read help or launch a command.",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10)
            });
            var searchRow = new DockPanel { LastChildFill = true };
            header.Children.Add(searchRow);
            _count.Text = _tools.Count + " commands"; _count.VerticalAlignment = VerticalAlignment.Center;
            _count.Margin = new Thickness(12, 0, 0, 0);
            DockPanel.SetDock(_count, Dock.Right); searchRow.Children.Add(_count);
            _search.Height = 34; _search.FontSize = 14; _search.Padding = new Thickness(8, 4, 8, 4);
            _search.VerticalContentAlignment = VerticalAlignment.Center;
            _search.ToolTip = "Search command, alias, category or description";
            _search.TextChanged += delegate { Filter(); };
            searchRow.Children.Add(_search);

            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.12, GridUnitType.Star) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.88, GridUnitType.Star) });
            root.Children.Add(body);

            var left = new DockPanel { Margin = new Thickness(0, 0, 12, 0) };
            Grid.SetColumn(left, 0); body.Children.Add(left);
            var heading = new TextBlock { Text = "COMMAND LIBRARY", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 8) };
            DockPanel.SetDock(heading, Dock.Top); left.Children.Add(heading);
            left.Children.Add(new ScrollViewer { Content = _catalogue, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

            var detailsBorder = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), Padding = new Thickness(16), Margin = new Thickness(4, 24, 0, 0) };
            Grid.SetColumn(detailsBorder, 1); body.Children.Add(detailsBorder);
            var details = new StackPanel(); detailsBorder.Child = details;
            details.Children.Add(new TextBlock { Text = "COMMAND HELP", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 16) });
            _detailTitle.FontSize = 21; _detailTitle.FontWeight = FontWeights.SemiBold; _detailTitle.TextWrapping = TextWrapping.Wrap;
            _detailTitle.Text = "Select a command"; details.Children.Add(_detailTitle);
            _detailCommand.FontFamily = new FontFamily("Consolas"); _detailCommand.FontWeight = FontWeights.Bold;
            _detailCommand.Margin = new Thickness(0, 8, 0, 14); details.Children.Add(_detailCommand);
            _detailDescription.TextWrapping = TextWrapping.Wrap; _detailDescription.FontSize = 14;
            _detailDescription.Text = "Select a command in the library to view its description, command alias and category.";
            details.Children.Add(new ScrollViewer { Content = _detailDescription, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

            BuildLibrary();
            CeInterfaceTheme.Apply(this);
        }

        internal static void Show()
        {
            try
            {
                if (_window != null) {
                    if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
                    _window.Activate(); return;
                }
                RibbonBuilder.EnsureCreated();
                ProductionWorkflowRibbonBuilder.EnsureCreated();
                List<FloatingToolDefinition> tools = FloatingToolsCommands.ReadCurrentRibbonTools();
                if (tools.Count == 0) {
                    AcApplication.DocumentManager.MdiActiveDocument?.Editor.WriteMessage("\nCE_HELP: no CE Tools commands were discovered.");
                    return;
                }
                _window = new CeHelpCentreWindow(tools);
                _window.Closed += delegate { _window = null; };
                AcApplication.ShowModelessWindow(_window);
            }
            catch (System.Exception ex) {
                AcApplication.DocumentManager.MdiActiveDocument?.Editor.WriteMessage("\nCE_HELP could not open: " + ex.Message);
            }
        }

        // Keep the discipline production centres together at the top of the Help Centre,
        // in the same left-to-right order as the CE Tools ribbon. Other tabs follow afterwards.
        private static int ProductionPanelOrder(string panel)
        {
            string value = (panel ?? string.Empty).Trim();
            if (value.StartsWith("CE-", StringComparison.OrdinalIgnoreCase))
                value = value.Substring(3).Trim();
            string[] orderedProductionPanels =
            {
                "Project Production",
                "Survey Production",
                "Platform Production",
                "Road Production",
                "Stormwater Production",
                "Sewer Production",
                "Water Production",
                "Bulk Water Production",
                "Parking Production",
                "Flood Production",
                "Quantity Production",
                "Report Production"
            };

            for (int i = 0; i < orderedProductionPanels.Length; i++)
            {
                if (string.Equals(value, orderedProductionPanels[i], StringComparison.OrdinalIgnoreCase))
                    return i;
            }

            // Non-production panels are deliberately placed after every production panel.
            return orderedProductionPanels.Length + 100;
        }

        private void BuildLibrary()
        {
            var groups = _tools.GroupBy(x =>
                (string.IsNullOrWhiteSpace(x.Panel) ? "CE Tools" : x.Panel) + "  /  " +
                (string.IsNullOrWhiteSpace(x.Group) ? "Other Commands" : x.Group),
                StringComparer.OrdinalIgnoreCase);
            foreach (var group in groups)
            {
                var expander = new Expander {
                    Header = group.Key + "  (" + group.Count() + ")",
                    IsExpanded = false, Margin = new Thickness(0, 0, 0, 6),
                    Padding = new Thickness(8, 6, 8, 6), BorderThickness = new Thickness(1)
                };
                var stack = new StackPanel();
                foreach (var tool in group) stack.Children.Add(CreateEntry(tool));
                expander.Content = stack; _catalogue.Children.Add(expander);
            }
        }

        private UIElement CreateEntry(FloatingToolDefinition tool)
        {
            var border = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(7, 8, 7, 8), Tag = tool };
            var row = new DockPanel(); border.Child = row;
            var open = new Button { Content = "Open", MinWidth = 58, Height = 27, Margin = new Thickness(8, 0, 0, 0), Tag = tool };
            open.Click += delegate(object sender, RoutedEventArgs e) {
                var selected = ((Button)sender).Tag as FloatingToolDefinition;
                if (selected == null) return;
                Document doc = AcApplication.DocumentManager.MdiActiveDocument;
                if (doc == null) return;
                string command = selected.Command.Trim();
                if (command.Length == 0) return;
                Close();
                doc.SendStringToExecute(command + " ", true, false, true);
            };
            DockPanel.SetDock(open, Dock.Right); row.Children.Add(open);
            var text = new StackPanel(); row.Children.Add(text);
            text.Children.Add(new TextBlock { Text = CeCommandDisplayNames.Normalize(string.IsNullOrWhiteSpace(tool.Text) ? tool.Command.Trim() : tool.Text), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            text.Children.Add(new TextBlock { Text = tool.Command.Trim(), FontFamily = new FontFamily("Consolas"), FontSize = 11, Opacity = 0.82 });
            text.Children.Add(new TextBlock { Text = Description(tool), FontSize = 11, TextWrapping = TextWrapping.Wrap, Opacity = 0.86 });
            border.MouseLeftButtonUp += delegate { ShowDetails(tool); };
            return border;
        }

        private void ShowDetails(FloatingToolDefinition tool)
        {
            _detailTitle.Text = CeCommandDisplayNames.Normalize(string.IsNullOrWhiteSpace(tool.Text) ? tool.Command.Trim() : tool.Text);
            _detailCommand.Text = tool.Command.Trim();
            _detailDescription.Text = "Discipline / panel: " + tool.Panel + Environment.NewLine +
                "Tool group: " + tool.Group + Environment.NewLine + Environment.NewLine + Description(tool);
        }

        private static string Description(FloatingToolDefinition tool)
        {
            string value = (tool.ToolTip ?? string.Empty).Trim();
            string generic = "Run " + tool.Command.Trim() + ". Declared by ";
            if (value.Length > 0 && value.IndexOf(generic, StringComparison.OrdinalIgnoreCase) != 0)
                return value;
            return "A detailed description has not yet been registered for this command. It is listed under " +
                (string.IsNullOrWhiteSpace(tool.Group) ? "Other Commands" : tool.Group) + " in " +
                (string.IsNullOrWhiteSpace(tool.Panel) ? "CE Tools" : tool.Panel) +
                ". Use Open to launch the command.";
        }

        private void Filter()
        {
            string query = (_search.Text ?? string.Empty).Trim();
            int visible = 0;
            foreach (Expander expander in _catalogue.Children.OfType<Expander>())
            {
                var stack = expander.Content as StackPanel;
                if (stack == null) continue;
                int matches = 0;
                foreach (Border card in stack.Children.OfType<Border>())
                {
                    var tool = card.Tag as FloatingToolDefinition;
                    bool show = tool != null && (query.Length == 0 ||
                        (tool.Command + " " + tool.Text + " " + tool.Panel + " " + tool.Group + " " + Description(tool))
                        .IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
                    card.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                    if (show) matches++;
                }
                expander.Visibility = matches > 0 ? Visibility.Visible : Visibility.Collapsed;
                expander.IsExpanded = query.Length > 0 && matches > 0;
                visible += matches;
            }
            _count.Text = visible + " of " + _tools.Count + " commands";
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.SettingsCenterCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Collects the configuration entry points that were previously scattered
    /// across ribbon flyouts and command-line menus in one searchable window.
    /// Entity selection remains in Civil 3D, while choosing the settings workflow
    /// is handled by a normal dialog.
    /// </summary>
    public sealed class SettingsCenterCommands
    {
        [CommandMethod("CE_TOOLS", "CE_SETTINGS", CommandFlags.Modal)]
        public void OpenSettingsCenter()
        {
            OpenWindow();
        }

        [CommandMethod("CE_TOOLS", "CE_SETTINGSCENTER", CommandFlags.Modal)]
        public void OpenSettingsCenterAlias()
        {
            OpenWindow();
        }

        [CommandMethod("CE_TOOLS", "CE_SETTINGSAUDIT", CommandFlags.Modal)]
        public void ShowSettingsAudit()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            IList<SettingsCenterItem> items = SettingsCenterItem.All;
            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - Settings Coverage",
                items.Count + " configuration workflows are available from CE_SETTINGS. " +
                "The window removes launcher keyword menus; Civil 3D object selection " +
                "continues in the drawing where it belongs.",
                new List<string> { "DISCIPLINE", "SETTING", "COMMAND", "PURPOSE" },
                items.Select(item => (IList<string>)new List<string>
                {
                    item.Discipline,
                    item.Title,
                    item.Command,
                    item.Description
                }).ToList(),
                "CE TOOLS SETTINGS COVERAGE");
        }

        private static void OpenWindow()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            var window = new SettingsCenterWindow(SettingsCenterItem.All);
            AcApplication.ShowModalWindow(window);
            if (string.IsNullOrWhiteSpace(window.SelectedCommand)) return;

            document.SendStringToExecute(
                window.SelectedCommand.Trim() + " ",
                true,
                false,
                true);
        }
    }

    internal sealed class SettingsCenterItem
    {
        private static readonly IList<SettingsCenterItem> Items =
            new List<SettingsCenterItem>
            {
                Item("General", "Project setup", "CE_PROJECTSETUP", "Project identity, client, issue and drawing metadata."),
                Item("General", "Import project styles", "CE_PROJECTSTYLEIMPORT", "Import approved Civil 3D styles from supplied or browsed DWG/DWT sources."),
                Item("General", "Project style centre", "CE_PROJECTSTYLES", "Alignment, profile, corridor, point and network style selections."),
                Item("General", "Annotation settings", "CE_ANNOTSETTINGS", "Paper text height, markers and annotation output."),
                Item("General", "Undo settings", "CE_UNDOSETTINGS", "Enable full native AutoCAD undo recording."),
                Item("General", "Ribbon icons", "CE_RIBBONICONS", "Review and select the installed ribbon icon mode."),
                Item("General", "Asset library", "CE_ASSETLIBSETTINGS", "Configure engineering asset library locations."),
                Item("General", "Typical-detail root", "CE_DETAILSETROOT", "Select the approved typical-detail source directory."),
                Item("General", "Typical-detail review", "CE_DETAILREVIEWSETTINGS", "Configure typical-detail review and provenance rules."),
                Item("Survey", "Setting-out schedule", "CE_SETTINGOUTTOOLS", "Create, refresh, export and inspect linked setting-out schedules."),
                Item("Survey", "Coordinate annotation", "CE_COORDINATE", "Coordinate labels, crosses and linked tables."),
                Item("Roads", "Surface correction", "CE_SURFCSETTINGS", "Surface audit and conservative correction thresholds."),
                Item("Roads", "Dynamic intersections", "CE_INTSETTINGS", "Marker, tolerance, sampling and corridor-code settings."),
                Item("Parking", "Parking skew", "CE_PKSKSETTINGS", "Bay width, skew tolerance, layers and text size."),
                Item("Parking", "Parking alternatives", "CE_PARKOPTIONS", "Generate and manage linked parking layout options."),
                Item("Stormwater", "Stormwater production", "CE_SWSETTINGS", "Project styles, layers, labels, profiles and band defaults."),
                Item("Sewer", "Sewer production", "CE_SEWSETTINGS", "Branch, alignment, profile, label and style defaults."),
                Item("Water", "Water production", "CE_WATERSETTINGS", "Water alignment, profile, style, band and spacing defaults."),
                Item("Flood", "Flood result frames", "CE_FLOODFRAMESET", "Configure imported flood-result review frames."),
                Item("Flood", "Reset flood frames", "CE_FLOODFRAMERESET", "Restore the default flood-result frame configuration."),
                Item("Production", "Automatic refresh", "CE_AUTOREFRESH", "Configure deferred linked-table and output refresh."),
                Item("Production", "Dynamic detail parameters", "CE_DETAILPARAMSETTINGS", "Configure dimensions, units and annotation for linked details.")
            };

        private SettingsCenterItem(
            string discipline,
            string title,
            string command,
            string description)
        {
            Discipline = discipline;
            Title = title;
            Command = command;
            Description = description;
        }

        public string Discipline { get; private set; }
        public string Title { get; private set; }
        public string Command { get; private set; }
        public string Description { get; private set; }

        public static IList<SettingsCenterItem> All
        {
            get { return Items; }
        }

        private static SettingsCenterItem Item(
            string discipline,
            string title,
            string command,
            string description)
        {
            return new SettingsCenterItem(discipline, title, command, description);
        }
    }

    internal sealed class SettingsCenterWindow : Window
    {
        private readonly IList<SettingsCenterItem> _items;
        private readonly StackPanel _buttons;
        private readonly TextBox _search;
        private readonly ComboBox _discipline;

        public SettingsCenterWindow(IList<SettingsCenterItem> items)
        {
            _items = items ?? new List<SettingsCenterItem>();
            Title = "CE Tools Settings Centre";
            Width = 820;
            Height = 650;
            MinWidth = 620;
            MinHeight = 440;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            ShowInTaskbar = false;
            Background = new SolidColorBrush(Color.FromRgb(244, 247, 249));

            var root = new Grid { Margin = new Thickness(18) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var heading = new TextBlock
            {
                Text = "Settings Centre",
                FontSize = 24,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(24, 52, 74)),
                Margin = new Thickness(0, 0, 0, 4)
            };
            root.Children.Add(heading);

            var note = new TextBlock
            {
                Text = "Choose a configuration workflow here. Object selection opens in the active Civil 3D drawing.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.DimGray,
                Margin = new Thickness(0, 0, 0, 12)
            };
            Grid.SetRow(note, 1);
            root.Children.Add(note);

            var filters = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            filters.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            filters.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
            _search = new TextBox
            {
                Margin = new Thickness(0, 0, 10, 0),
                Padding = new Thickness(8, 6, 8, 6),
                ToolTip = "Search setting, command or purpose"
            };
            _search.TextChanged += delegate { Rebuild(); };
            filters.Children.Add(_search);

            _discipline = new ComboBox { Padding = new Thickness(6, 4, 6, 4) };
            _discipline.Items.Add("All disciplines");
            foreach (string value in _items.Select(item => item.Discipline).Distinct().OrderBy(value => value))
                _discipline.Items.Add(value);
            _discipline.SelectedIndex = 0;
            _discipline.SelectionChanged += delegate { Rebuild(); };
            Grid.SetColumn(_discipline, 1);
            filters.Children.Add(_discipline);
            Grid.SetRow(filters, 2);
            root.Children.Add(filters);

            _buttons = new StackPanel();
            var scroll = new ScrollViewer
            {
                Content = _buttons,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            Grid.SetRow(scroll, 3);
            root.Children.Add(scroll);

            var close = new Button
            {
                Content = "Close",
                MinWidth = 100,
                Padding = new Thickness(14, 7, 14, 7),
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0),
                IsCancel = true
            };
            Grid.SetRow(close, 4);
            root.Children.Add(close);

            Content = root;
            Loaded += delegate { _search.Focus(); Rebuild(); };
        }

        public string SelectedCommand { get; private set; }

        private void Rebuild()
        {
            _buttons.Children.Clear();
            string search = (_search.Text ?? string.Empty).Trim();
            string discipline = _discipline.SelectedItem as string ?? "All disciplines";

            IEnumerable<SettingsCenterItem> filtered = _items.Where(item =>
                (discipline == "All disciplines" || item.Discipline == discipline) &&
                (search.Length == 0 ||
                 (item.Title + " " + item.Command + " " + item.Description + " " + item.Discipline)
                     .IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0));

            foreach (SettingsCenterItem item in filtered)
            {
                var button = new Button
                {
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Margin = new Thickness(0, 0, 0, 8),
                    Padding = new Thickness(12, 9, 12, 9),
                    Tag = item,
                    Content = BuildButtonContent(item)
                };
                button.Click += OnSettingClick;
                _buttons.Children.Add(button);
            }

            if (_buttons.Children.Count == 0)
                _buttons.Children.Add(new TextBlock
                {
                    Text = "No settings match the current filter.",
                    Foreground = Brushes.DimGray,
                    Margin = new Thickness(4, 12, 4, 12)
                });
        }

        private static UIElement BuildButtonContent(SettingsCenterItem item)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var left = new StackPanel();
            left.Children.Add(new TextBlock { Text = item.Discipline, FontWeight = FontWeights.SemiBold });
            left.Children.Add(new TextBlock { Text = item.Command, FontFamily = new FontFamily("Consolas"), Foreground = Brushes.DimGray });
            grid.Children.Add(left);

            var right = new StackPanel();
            right.Children.Add(new TextBlock { Text = item.Title, FontWeight = FontWeights.SemiBold, FontSize = 14 });
            right.Children.Add(new TextBlock { Text = item.Description, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray });
            Grid.SetColumn(right, 1);
            grid.Children.Add(right);
            return grid;
        }

        private void OnSettingClick(object sender, RoutedEventArgs args)
        {
            var button = sender as Button;
            var item = button == null ? null : button.Tag as SettingsCenterItem;
            if (item == null) return;
            SelectedCommand = item.Command;
            DialogResult = true;
            Close();
        }
    }
}

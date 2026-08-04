using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace CETools.Civil3D
{
    /// <summary>
    /// Shared WPF presentation for production workflow selection and persisted
    /// discipline settings. Drawing picks remain native Civil 3D interactions;
    /// configuration and command selection are handled by normal windows.
    /// </summary>
    internal static class DisciplineWorkflowDialogs
    {
        public static string SelectWorkflow(
            string title,
            string note,
            IList<DisciplineWorkflowAction> actions)
        {
            var window = new DisciplineWorkflowWindow(title, note, actions);
            AcApplication.ShowModalWindow(window);
            return window.SelectedCommand ?? string.Empty;
        }

        public static void SelectAndRun(
            Document document,
            string title,
            string note,
            IList<DisciplineWorkflowAction> actions)
        {
            if (document == null) return;
            string command = SelectWorkflow(title, note, actions);
            if (string.IsNullOrWhiteSpace(command)) return;
            document.SendStringToExecute(
                command.Trim() + " ",
                true,
                false,
                true);
        }

        public static bool EditSettings(ProductionSettingsDialogModel model)
        {
            if (model == null) return false;
            var window = new ProductionSettingsWindow(model);
            AcApplication.ShowModalWindow(window);
            return window.Accepted;
        }

        public static bool Confirm(string title, string message)
        {
            return System.Windows.MessageBox.Show(
                message ?? string.Empty,
                title ?? "CE Tools",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No) == MessageBoxResult.Yes;
        }
    }

    internal sealed class DisciplineWorkflowAction
    {
        public DisciplineWorkflowAction(
            string title,
            string command,
            string description,
            string group)
        {
            Title = title ?? string.Empty;
            Command = command ?? string.Empty;
            Description = description ?? string.Empty;
            Group = group ?? string.Empty;
        }

        public string Title { get; private set; }
        public string Command { get; private set; }
        public string Description { get; private set; }
        public string Group { get; private set; }
    }

    internal sealed class DisciplineWorkflowWindow : Window
    {
        public DisciplineWorkflowWindow(
            string title,
            string note,
            IList<DisciplineWorkflowAction> actions)
        {
            Title = title ?? "CE Tools Workflow";
            Width = 760;
            Height = 620;
            MinWidth = 600;
            MinHeight = 420;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            ShowInTaskbar = false;
            Background = new SolidColorBrush(Color.FromRgb(244, 247, 249));

            var root = new Grid { Margin = new Thickness(18) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var heading = new TextBlock
            {
                Text = title ?? "CE Tools Workflow",
                FontSize = 23,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(24, 52, 74)),
                Margin = new Thickness(0, 0, 0, 5)
            };
            root.Children.Add(heading);

            var noteBlock = new TextBlock
            {
                Text = note ?? string.Empty,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.DimGray,
                Margin = new Thickness(0, 0, 0, 14)
            };
            Grid.SetRow(noteBlock, 1);
            root.Children.Add(noteBlock);

            var stack = new StackPanel();
            foreach (IGrouping<string, DisciplineWorkflowAction> group in
                (actions ?? new List<DisciplineWorkflowAction>())
                    .GroupBy(item => item.Group)
                    .OrderBy(item => item.Key))
            {
                stack.Children.Add(new TextBlock
                {
                    Text = group.Key,
                    FontSize = 15,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromRgb(40, 113, 151)),
                    Margin = new Thickness(0, 8, 0, 7)
                });
                foreach (DisciplineWorkflowAction action in group)
                {
                    var button = new Button
                    {
                        Tag = action,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        Padding = new Thickness(12, 9, 12, 9),
                        Margin = new Thickness(0, 0, 0, 7),
                        Content = BuildActionContent(action)
                    };
                    button.Click += OnActionClick;
                    stack.Children.Add(button);
                }
            }

            var scroll = new ScrollViewer
            {
                Content = stack,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            Grid.SetRow(scroll, 2);
            root.Children.Add(scroll);

            var close = new Button
            {
                Content = "Close",
                IsCancel = true,
                MinWidth = 100,
                Padding = new Thickness(14, 7, 14, 7),
                Margin = new Thickness(0, 12, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Right
            };
            Grid.SetRow(close, 3);
            root.Children.Add(close);
            Content = root;
        }

        public string SelectedCommand { get; private set; }

        private static UIElement BuildActionContent(DisciplineWorkflowAction action)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var title = new StackPanel();
            title.Children.Add(new TextBlock
            {
                Text = action.Title,
                FontWeight = FontWeights.SemiBold,
                FontSize = 14
            });
            title.Children.Add(new TextBlock
            {
                Text = action.Command,
                FontFamily = new FontFamily("Consolas"),
                Foreground = Brushes.DimGray
            });
            grid.Children.Add(title);
            var description = new TextBlock
            {
                Text = action.Description,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brushes.DimGray
            };
            Grid.SetColumn(description, 1);
            grid.Children.Add(description);
            return grid;
        }

        private void OnActionClick(object sender, RoutedEventArgs args)
        {
            var button = sender as Button;
            var action = button == null ? null : button.Tag as DisciplineWorkflowAction;
            if (action == null || string.IsNullOrWhiteSpace(action.Command)) return;
            SelectedCommand = action.Command;
            DialogResult = true;
            Close();
        }
    }

    internal enum ProductionSettingsFieldKind
    {
        Text,
        PositiveDouble,
        PositiveInteger,
        PaperHeight,
        Choice
    }

    internal sealed class ProductionSettingsField
    {
        public ProductionSettingsField(
            string key,
            string group,
            string label,
            string description,
            ProductionSettingsFieldKind kind,
            string value,
            IEnumerable<string> choices = null)
        {
            Key = key ?? string.Empty;
            Group = group ?? string.Empty;
            Label = label ?? string.Empty;
            Description = description ?? string.Empty;
            Kind = kind;
            Value = value ?? string.Empty;
            Choices = (choices ?? new string[0])
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public string Key { get; private set; }
        public string Group { get; private set; }
        public string Label { get; private set; }
        public string Description { get; private set; }
        public ProductionSettingsFieldKind Kind { get; private set; }
        public string Value { get; set; }
        public IList<string> Choices { get; private set; }
    }

    internal sealed class ProductionSettingsDialogModel
    {
        private readonly List<ProductionSettingsField> _fields =
            new List<ProductionSettingsField>();

        public ProductionSettingsDialogModel(string title, string note)
        {
            Title = title ?? "CE Tools Settings";
            Note = note ?? string.Empty;
        }

        public string Title { get; private set; }
        public string Note { get; private set; }
        public IList<ProductionSettingsField> Fields { get { return _fields; } }

        public void AddText(string key, string group, string label, string value, string description)
        {
            Add(key, group, label, value, description, ProductionSettingsFieldKind.Text);
        }

        public void AddChoice(
            string key,
            string group,
            string label,
            string value,
            string description,
            IEnumerable<string> choices)
        {
            _fields.Add(new ProductionSettingsField(
                key,
                group,
                label,
                description,
                ProductionSettingsFieldKind.Choice,
                value,
                choices));
        }

        public void AddPositiveDouble(string key, string group, string label, double value, string description)
        {
            Add(key, group, label, value.ToString("0.###", CultureInfo.InvariantCulture), description, ProductionSettingsFieldKind.PositiveDouble);
        }

        public void AddPositiveInteger(string key, string group, string label, int value, string description)
        {
            Add(key, group, label, value.ToString(CultureInfo.InvariantCulture), description, ProductionSettingsFieldKind.PositiveInteger);
        }

        public void AddPaperHeight(string key, string group, string label, double value, string description)
        {
            Add(key, group, label, value.ToString("0.###", CultureInfo.InvariantCulture), description, ProductionSettingsFieldKind.PaperHeight);
        }

        public string Text(string key)
        {
            ProductionSettingsField field = Find(key);
            return field == null ? string.Empty : (field.Value ?? string.Empty).Trim();
        }

        public double Double(string key, double fallback)
        {
            double value;
            return TryDouble(Text(key), out value) && value > 0.0 ? value : fallback;
        }

        public int Integer(string key, int fallback)
        {
            int value;
            return int.TryParse(Text(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value > 0
                ? value
                : fallback;
        }

        private void Add(
            string key,
            string group,
            string label,
            string value,
            string description,
            ProductionSettingsFieldKind kind)
        {
            _fields.Add(new ProductionSettingsField(key, group, label, description, kind, value));
        }

        private ProductionSettingsField Find(string key)
        {
            return _fields.FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase));
        }

        internal static bool TryDouble(string text, out double value)
        {
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
                   double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
        }
    }

    internal sealed class ProductionSettingsWindow : Window
    {
        private readonly ProductionSettingsDialogModel _model;
        private readonly Dictionary<ProductionSettingsField, Control> _controls =
            new Dictionary<ProductionSettingsField, Control>();

        public ProductionSettingsWindow(ProductionSettingsDialogModel model)
        {
            _model = model;
            Title = model.Title;
            Width = 880;
            Height = 720;
            MinWidth = 660;
            MinHeight = 480;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            ShowInTaskbar = false;
            Background = new SolidColorBrush(Color.FromRgb(244, 247, 249));

            var root = new Grid { Margin = new Thickness(18) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var heading = new TextBlock
            {
                Text = model.Title,
                FontSize = 23,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(24, 52, 74)),
                Margin = new Thickness(0, 0, 0, 5)
            };
            root.Children.Add(heading);
            var note = new TextBlock
            {
                Text = model.Note,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.DimGray,
                Margin = new Thickness(0, 0, 0, 14)
            };
            Grid.SetRow(note, 1);
            root.Children.Add(note);

            var groups = new StackPanel();
            foreach (IGrouping<string, ProductionSettingsField> group in
                model.Fields.GroupBy(item => item.Group))
            {
                var panel = new StackPanel { Margin = new Thickness(10) };
                foreach (ProductionSettingsField field in group)
                    panel.Children.Add(BuildFieldRow(field));
                groups.Children.Add(new Expander
                {
                    Header = group.Key,
                    IsExpanded = true,
                    Margin = new Thickness(0, 0, 0, 10),
                    Padding = new Thickness(4),
                    Content = panel,
                    FontWeight = FontWeights.SemiBold
                });
            }
            var scroll = new ScrollViewer
            {
                Content = groups,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            Grid.SetRow(scroll, 2);
            root.Children.Add(scroll);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            var save = new Button
            {
                Content = "Save Settings",
                IsDefault = true,
                MinWidth = 120,
                Padding = new Thickness(14, 7, 14, 7),
                Margin = new Thickness(0, 0, 8, 0)
            };
            save.Click += OnSave;
            buttons.Children.Add(save);
            buttons.Children.Add(new Button
            {
                Content = "Cancel",
                IsCancel = true,
                MinWidth = 95,
                Padding = new Thickness(14, 7, 14, 7)
            });
            Grid.SetRow(buttons, 3);
            root.Children.Add(buttons);
            Content = root;
        }

        public bool Accepted { get; private set; }

        private UIElement BuildFieldRow(ProductionSettingsField field)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var label = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
            label.Children.Add(new TextBlock
            {
                Text = field.Label,
                FontWeight = FontWeights.SemiBold
            });
            label.Children.Add(new TextBlock
            {
                Text = field.Description,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                FontWeight = FontWeights.Normal,
                Foreground = Brushes.DimGray
            });
            grid.Children.Add(label);

            Control control;
            if (field.Kind == ProductionSettingsFieldKind.PaperHeight ||
                field.Kind == ProductionSettingsFieldKind.Choice)
            {
                var combo = new ComboBox
                {
                    IsEditable = true,
                    Padding = new Thickness(7, 5, 7, 5),
                    Text = field.Value,
                    FontWeight = FontWeights.Normal
                };
                IEnumerable<string> choices = field.Kind == ProductionSettingsFieldKind.PaperHeight
                    ? new[] { "1.8", "2.0", "2.5", "3.5", "5.0" }
                    : field.Choices;
                if (field.Kind == ProductionSettingsFieldKind.Choice)
                    combo.Items.Add(string.Empty);
                foreach (string choice in choices)
                    combo.Items.Add(choice);
                control = combo;
            }
            else
            {
                control = new TextBox
                {
                    Text = field.Value,
                    Padding = new Thickness(7, 5, 7, 5),
                    FontWeight = FontWeights.Normal
                };
            }
            control.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(control, 1);
            grid.Children.Add(control);
            _controls[field] = control;
            return grid;
        }

        private void OnSave(object sender, RoutedEventArgs args)
        {
            foreach (KeyValuePair<ProductionSettingsField, Control> item in _controls)
            {
                ProductionSettingsField field = item.Key;
                var textBox = item.Value as TextBox;
                var combo = item.Value as ComboBox;
                string value = textBox != null
                    ? textBox.Text
                    : combo != null ? combo.Text : string.Empty;
                value = (value ?? string.Empty).Trim();
                if (field.Kind == ProductionSettingsFieldKind.PositiveInteger)
                {
                    int integer;
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out integer) || integer <= 0)
                    {
                        ShowValidation(field.Label, "Enter a whole number greater than zero.");
                        return;
                    }
                }
                else if (field.Kind == ProductionSettingsFieldKind.PositiveDouble ||
                         field.Kind == ProductionSettingsFieldKind.PaperHeight)
                {
                    double number;
                    if (!ProductionSettingsDialogModel.TryDouble(value, out number) || number <= 0.0)
                    {
                        ShowValidation(field.Label, "Enter a number greater than zero.");
                        return;
                    }
                }
                field.Value = value;
            }

            Accepted = true;
            DialogResult = true;
            Close();
        }

        private static void ShowValidation(string field, string message)
        {
            System.Windows.MessageBox.Show(
                field + ": " + message,
                "CE Tools Settings",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    internal static class ProductionStyleCatalog
    {
        public static IList<string> ReadNames(Database database, object styleCollection)
        {
            var names = new List<string>();
            var enumerable = styleCollection as IEnumerable;
            if (database == null || enumerable == null) return names;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (object value in enumerable)
                {
                    if (!(value is ObjectId)) continue;
                    ObjectId id = (ObjectId)value;
                    if (id.IsNull || id.IsErased) continue;
                    try
                    {
                        DBObject item = transaction.GetObject(id, OpenMode.ForRead, false);
                        object name = item.GetType().GetProperty("Name") == null
                            ? null
                            : item.GetType().GetProperty("Name").GetValue(item, null);
                        string text = Convert.ToString(name, CultureInfo.CurrentCulture);
                        if (!string.IsNullOrWhiteSpace(text)) names.Add(text.Trim());
                    }
                    catch
                    {
                        // One unreadable style must not hide the remaining catalogue.
                    }
                }
            }
            return names
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}

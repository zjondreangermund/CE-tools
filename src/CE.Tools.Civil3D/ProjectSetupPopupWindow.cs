using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace CETools.Civil3D
{
    /// <summary>
    /// One-window editor for the project metadata stored by CE_PROJECTSETUP.
    /// It deliberately contains no database writes; the existing command keeps
    /// ownership of review, transaction, backup and table-placement behaviour.
    /// </summary>
    internal sealed class ProjectSetupPopupWindow : Window
    {
        private readonly Dictionary<string, TextBox> _editors =
            new Dictionary<string, TextBox>(StringComparer.OrdinalIgnoreCase);

        public ProjectSetupPopupWindow(
            IEnumerable<string> fields,
            IDictionary<string, string> initialValues)
        {
            if (fields == null)
                throw new ArgumentNullException("fields");

            Title = "CE Tools - Project Setup";
            Width = 620.0;
            MinWidth = 520.0;
            Height = 560.0;
            MinHeight = 420.0;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            ShowInTaskbar = false;

            var root = new DockPanel
            {
                Margin = new Thickness(16.0)
            };
            Content = root;

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0.0, 14.0, 0.0, 0.0)
            };
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(buttons);

            var cancel = new Button
            {
                Content = "Cancel",
                MinWidth = 90.0,
                Margin = new Thickness(8.0, 0.0, 0.0, 0.0),
                IsCancel = true
            };
            cancel.Click += delegate
            {
                Accepted = false;
                DialogResult = false;
            };
            buttons.Children.Add(cancel);

            var review = new Button
            {
                Content = "Review and Save",
                MinWidth = 130.0,
                Margin = new Thickness(8.0, 0.0, 0.0, 0.0),
                IsDefault = true
            };
            review.Click += delegate
            {
                Accepted = true;
                DialogResult = true;
            };
            buttons.Children.Add(review);

            var heading = new TextBlock
            {
                Text = "Project information",
                FontSize = 18.0,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0.0, 0.0, 0.0, 4.0)
            };
            DockPanel.SetDock(heading, Dock.Top);
            root.Children.Add(heading);

            var help = new TextBlock
            {
                Text = "Enter or update all project values in one window. " +
                       "After saving, CE Tools will show the complete result and offer to place a drawing table.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0.0, 0.0, 0.0, 14.0)
            };
            DockPanel.SetDock(help, Dock.Top);
            root.Children.Add(help);

            var form = new Grid();
            form.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(175.0)
            });
            form.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1.0, GridUnitType.Star)
            });

            int row = 0;
            foreach (string field in fields)
            {
                if (string.IsNullOrWhiteSpace(field))
                    continue;

                form.RowDefinitions.Add(new RowDefinition
                {
                    Height = GridLength.Auto
                });

                var label = new TextBlock
                {
                    Text = field,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0.0, 5.0, 12.0, 5.0)
                };
                Grid.SetRow(label, row);
                Grid.SetColumn(label, 0);
                form.Children.Add(label);

                string value = string.Empty;
                if (initialValues != null)
                    initialValues.TryGetValue(field, out value);

                var editor = new TextBox
                {
                    Text = value ?? string.Empty,
                    MinWidth = 260.0,
                    Margin = new Thickness(0.0, 4.0, 0.0, 4.0),
                    Padding = new Thickness(5.0, 3.0, 5.0, 3.0)
                };
                Grid.SetRow(editor, row);
                Grid.SetColumn(editor, 1);
                form.Children.Add(editor);
                _editors[field] = editor;
                row++;
            }

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = form
            };
            root.Children.Add(scroll);

            Loaded += delegate
            {
                foreach (KeyValuePair<string, TextBox> item in _editors)
                {
                    item.Value.Focus();
                    item.Value.SelectAll();
                    break;
                }
            };

            PreviewKeyDown += delegate(object sender, KeyEventArgs args)
            {
                if (args.Key != Key.Escape)
                    return;

                Accepted = false;
                DialogResult = false;
                args.Handled = true;
            };
        }

        public bool Accepted { get; private set; }

        public string GetValue(string field)
        {
            TextBox editor;
            if (string.IsNullOrWhiteSpace(field) ||
                !_editors.TryGetValue(field, out editor) ||
                editor == null)
            {
                return string.Empty;
            }

            return (editor.Text ?? string.Empty).Trim();
        }
    }
}

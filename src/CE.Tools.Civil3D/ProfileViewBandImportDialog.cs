using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace CETools.Civil3D
{
    /// <summary>
    /// A batch equivalent of Civil 3D's Profile View Properties &gt; Bands tab.
    /// The native dialog edits one profile view at a time; this window keeps the
    /// same band-set/show-label choices while applying them to the selected views.
    /// </summary>
    internal sealed class ProfileViewBandImportDialog : Window
    {
        private static string _lastSelectedStyle;
        private static bool _lastShowLabels = true;
        private static bool _lastOpenNative = false;
        private readonly IList<string> _styleNames;
        private readonly IList<string> _viewNames;
        private readonly ComboBox _bandSet;
        private readonly ComboBox _showLabels;
        private readonly ComboBox _openNative;

        private ProfileViewBandImportDialog(IList<string> styleNames, IList<string> viewNames)
        {
            _styleNames = styleNames ?? new List<string>();
            _viewNames = viewNames ?? new List<string>();

            Title = "Profile View Properties - Bands (CE Tools Batch)";
            Width = 920;
            Height = 690;
            MinWidth = 720;
            MinHeight = 500;
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
                Text = "Profile View Properties - Bands",
                FontSize = 23,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(24, 52, 74)),
                Margin = new Thickness(0, 0, 0, 5)
            };
            root.Children.Add(heading);

            var note = new TextBlock
            {
                Text = "Choose the Civil 3D band set, enable its band labels, and apply the same Bands-tab settings to every selected profile view. Multi-view batches keep the complete selection active. After applying, you can either finish the batch or open CE Tools Band Data Sources for the selected views.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.DimGray,
                Margin = new Thickness(0, 0, 0, 14)
            };
            Grid.SetRow(note, 1);
            root.Children.Add(note);

            var controls = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(215) });
            controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            controls.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            controls.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            controls.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            controls.Children.Add(LabelBlock(
                "Band type",
                "The native Civil 3D band type used by this workflow."));
            var bandType = new TextBox
            {
                Text = "Profile Data / Profile View Band Set",
                IsReadOnly = true,
                Padding = new Thickness(7, 5, 7, 5),
                Background = new SolidColorBrush(Color.FromRgb(235, 239, 242))
            };
            Grid.SetColumn(bandType, 1);
            controls.Children.Add(bandType);

            var styleLabel = LabelBlock(
                "Band set style",
                "This is the same style selected by Import band set... on the Civil 3D Bands tab.");
            Grid.SetRow(styleLabel, 1);
            controls.Children.Add(styleLabel);
            _bandSet = new ComboBox
            {
                Padding = new Thickness(7, 5, 7, 5),
                IsEditable = false,
                ItemsSource = _styleNames
            };
            if (_styleNames.Count > 0)
            {
                int previous = _styleNames.ToList().FindIndex(name =>
                    string.Equals(name, _lastSelectedStyle, StringComparison.OrdinalIgnoreCase));
                _bandSet.SelectedIndex = previous >= 0 ? previous : 0;
            }
            Grid.SetColumn(_bandSet, 1);
            Grid.SetRow(_bandSet, 1);
            controls.Children.Add(_bandSet);

            var labelPanel = new StackPanel { Orientation = Orientation.Horizontal };
            labelPanel.Children.Add(new TextBlock
            {
                Text = "Labels",
                FontWeight = FontWeights.SemiBold,
                Width = 120
            });
            labelPanel.Children.Add(new TextBlock
            {
                Text = "Persist Show Labels in both top and bottom band collections.",
                FontSize = 11,
                Foreground = Brushes.DimGray,
                VerticalAlignment = VerticalAlignment.Center
            });
            Grid.SetRow(labelPanel, 2);
            controls.Children.Add(labelPanel);
            _showLabels = new ComboBox
            {
                ItemsSource = new[] { "Yes - show labels for every band row", "No - hide band labels" },
                SelectedIndex = _lastShowLabels ? 0 : 1,
                Padding = new Thickness(7, 5, 7, 5),
                FontWeight = FontWeights.SemiBold,
                IsEditable = false
            };
            Grid.SetColumn(_showLabels, 1);
            Grid.SetRow(_showLabels, 2);
            controls.Children.Add(_showLabels);
            Grid.SetRow(controls, 2);
            root.Children.Add(controls);

            var selectedPanel = new GroupBox
            {
                Header = "Selected profile views",
                Padding = new Thickness(8),
                Margin = new Thickness(0, 0, 0, 10)
            };
            var selectedRoot = new Grid();
            selectedRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            selectedRoot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var selectedNote = new TextBlock
            {
                Text = _viewNames.Count.ToString() + " profile view(s) will receive the imported band set.",
                Foreground = Brushes.DimGray,
                Margin = new Thickness(0, 0, 0, 6)
            };
            selectedRoot.Children.Add(selectedNote);
            var views = new ListBox
            {
                ItemsSource = _viewNames,
                IsHitTestVisible = false,
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(185, 195, 205))
            };
            Grid.SetRow(views, 1);
            selectedRoot.Children.Add(views);
            selectedPanel.Content = selectedRoot;
            Grid.SetRow(selectedPanel, 3);
            root.Children.Add(selectedPanel);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            _openNative = new ComboBox
            {
                ItemsSource = _viewNames.Count == 1
                    ? new[]
                    {
                        "Finish batch and keep selected",
                        "Open native Profile View Properties"
                    }
                    : new[]
                    {
                        "Finish batch and keep all selected",
                        "Open Band Data Sources for selected views",
                        "Edit first selected in native properties, apply to all others"
                    },
                SelectedIndex = _viewNames.Count == 1 && _lastOpenNative ? 1 : 0,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 18, 0),
                MinWidth = 360,
                IsEditable = false,
                IsEnabled = true
            };
            buttons.Children.Add(_openNative);
            var apply = new Button
            {
                Content = "Import band set to selected views",
                IsDefault = true,
                MinWidth = 205,
                Padding = new Thickness(14, 7, 14, 7),
                Margin = new Thickness(0, 0, 8, 0)
            };
            apply.Click += OnApply;
            buttons.Children.Add(apply);
            var cancel = new Button
            {
                Content = "Cancel",
                IsCancel = true,
                MinWidth = 95,
                Padding = new Thickness(14, 7, 14, 7)
            };
            cancel.Click += delegate { DialogResult = false; Close(); };
            buttons.Children.Add(cancel);
            Grid.SetRow(buttons, 4);
            root.Children.Add(buttons);

            Content = root;
        }

        internal string SelectedStyleName { get; private set; }
        internal bool ShowLabels { get; private set; }
        internal bool OpenNativeDialog { get; private set; }
        internal bool OpenBandDataSources { get; private set; }
        internal bool OpenEditMatch { get; private set; }

        internal static ProfileViewBandImportDialog Show(
            IList<string> styleNames,
            IList<string> viewNames)
        {
            if (styleNames == null || styleNames.Count == 0) return null;
            var window = new ProfileViewBandImportDialog(styleNames, viewNames);
            AcApplication.ShowModalWindow(window);
            return window.Accepted ? window : null;
        }

        internal bool Accepted { get; private set; }

        private void OnApply(object sender, RoutedEventArgs args)
        {
            string selected = _bandSet == null ? string.Empty : _bandSet.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(selected))
            {
                MessageBox.Show(
                    "Choose a Civil 3D profile-view band set before applying.",
                    "CE Tools - Bands",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }
            SelectedStyleName = selected;
            ShowLabels = _showLabels != null && _showLabels.SelectedIndex == 0;
            int postAction = _openNative == null ? 0 : _openNative.SelectedIndex;
            OpenNativeDialog = _viewNames.Count == 1 && postAction == 1;
            OpenBandDataSources = _viewNames.Count > 1 && postAction == 1;
            OpenEditMatch = _viewNames.Count > 1 && postAction == 2;
            _lastSelectedStyle = SelectedStyleName;
            _lastShowLabels = ShowLabels;
            _lastOpenNative = OpenNativeDialog;
            Accepted = true;
            DialogResult = true;
            Close();
        }

        private static StackPanel LabelBlock(string label, string description)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 12, 10) };
            panel.Children.Add(new TextBlock
            {
                Text = label,
                FontWeight = FontWeights.SemiBold
            });
            panel.Children.Add(new TextBlock
            {
                Text = description,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                Foreground = Brushes.DimGray
            });
            return panel;
        }
    }
}

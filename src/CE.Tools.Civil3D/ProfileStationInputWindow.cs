using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace CETools.Civil3D
{
    internal sealed class ProfileStationInputWindow : Window
    {
        private readonly TextBox _station;

        public ProfileStationInputWindow()
        {
            Title = "CE Tools - Profile Annotation";
            Width = 430.0;
            Height = 190.0;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;

            var panel = new StackPanel { Margin = new Thickness(16.0) };
            panel.Children.Add(new TextBlock
            {
                Text = "Profile station",
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0.0, 0.0, 0.0, 6.0)
            });
            _station = new TextBox
            {
                Text = "0.000",
                Margin = new Thickness(0.0, 0.0, 0.0, 14.0)
            };
            panel.Children.Add(_station);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var cancel = new Button
            {
                Content = "Cancel",
                Width = 90.0,
                Margin = new Thickness(0.0, 0.0, 8.0, 0.0),
                IsCancel = true
            };
            var accept = new Button
            {
                Content = "Continue",
                Width = 100.0,
                IsDefault = true
            };
            accept.Click += Accept;
            buttons.Children.Add(cancel);
            buttons.Children.Add(accept);
            panel.Children.Add(buttons);
            Content = panel;
        }

        public bool Accepted { get; private set; }

        public double Station { get; private set; }

        private void Accept(object sender, RoutedEventArgs args)
        {
            double station;
            if (!double.TryParse(
                    _station.Text,
                    NumberStyles.Float | NumberStyles.AllowThousands,
                    CultureInfo.CurrentCulture,
                    out station) &&
                !double.TryParse(
                    _station.Text,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out station))
            {
                MessageBox.Show(
                    this,
                    "Enter a valid station value.",
                    "CE Tools",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                _station.Focus();
                _station.SelectAll();
                return;
            }
            Station = station;
            Accepted = true;
            DialogResult = true;
        }
    }
}

using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
namespace CETools.Civil3D
{
    internal static class CePointInputDialog
    {
        internal static bool TryGet(Document document, string title, Point3d initial, out Point3d point)
        {
            point = initial;
            var window = new Window { Title = "CE Tools - " + title, Width = 530, Height = 330,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
            var panel = new StackPanel { Margin = new Thickness(20) };
            panel.Children.Add(new TextBlock { Text = title + " (world coordinates)", FontSize = 20, Margin = new Thickness(0,0,0,12) });
            var inputs = new TextBox[3];
            double[] coordinates = { initial.X, initial.Y, initial.Z };
            for (int i = 0; i < 3; i++)
            {
                var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
                row.Children.Add(new TextBlock { Text = new[] { "X", "Y", "Z" }[i], Width = 40 });
                inputs[i] = new TextBox { Text = coordinates[i].ToString("R", CultureInfo.CurrentCulture) };
                row.Children.Add(inputs[i]); panel.Children.Add(row);
            }
            var buttons = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) };
            var pick = new Button { Content = "Pick in drawing", Padding = new Thickness(12, 6, 12, 6) };
            pick.Click += (s, e) => {
                try
                {
                    using (document.Editor.StartUserInteraction(new WindowInteropHelper(window).Handle))
                    {
                        var result = document.Editor.GetPoint("\n" + title + ": ");
                        if (result.Status != PromptStatus.OK) return;
                        var world = result.Value.TransformBy(document.Editor.CurrentUserCoordinateSystem);
                        double[] values = { world.X, world.Y, world.Z };
                        for (int i = 0; i < 3; i++) inputs[i].Text = values[i].ToString("R", CultureInfo.CurrentCulture);
                    }
                }
                catch (System.Exception ex) { CeMessageDialog.Show("Point", ex.Message, false, window); }
            };
            buttons.Children.Add(pick);
            var ok = new Button { Content = "Continue", IsDefault = true, Padding = new Thickness(12, 6, 12, 6) };
            ok.Click += (s, e) => {
                for (int i = 0; i < 3; i++)
                    if (!double.TryParse(inputs[i].Text, NumberStyles.Float, CultureInfo.CurrentCulture, out coordinates[i]) ||
                        double.IsNaN(coordinates[i]) || double.IsInfinity(coordinates[i]))
                    { inputs[i].Focus(); CeMessageDialog.Show("Point", "Enter finite coordinates.", false, window); return; }
                window.DialogResult = true;
            };
            buttons.Children.Add(ok);
            buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(12, 6, 12, 6) });
            panel.Children.Add(buttons); window.Content = panel;
            window.Loaded += (s, e) => CeInterfaceTheme.Apply(window);
            if (AcApplication.ShowModalWindow(window) != true) return false;
            point = new Point3d(coordinates[0], coordinates[1], coordinates[2]); return true;
        }
    }
}

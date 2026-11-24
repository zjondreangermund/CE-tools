using System.Windows;
using System.Windows.Controls;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
namespace CETools.Civil3D
{
    internal static class CeMessageDialog
    {
        internal static bool Show(string title, string message, bool confirm = false, Window owner = null)
        {
            var window = new Window { Title = "CE Tools - " + title, Width = 650, Height = 440,
                WindowStartupLocation = WindowStartupLocation.CenterOwner };
            if (owner != null) window.Owner = owner;
            var root = new DockPanel { Margin = new Thickness(20) };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var ok = new Button { Content = confirm ? "Apply" : "OK", MinWidth = 90, Margin = new Thickness(5), Padding = new Thickness(10,6,10,6), IsDefault = !confirm };
            ok.Click += (s, e) => window.DialogResult = true;
            buttons.Children.Add(ok);
            if (confirm) buttons.Children.Add(new Button { Content = "Cancel", MinWidth = 90, Margin = new Thickness(5), Padding = new Thickness(10,6,10,6), IsCancel = true, IsDefault = true });
            DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
            root.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new TextBlock { Text = message ?? "", TextWrapping = TextWrapping.Wrap, FontSize = 14 } });
            window.Content = root; window.Loaded += (s, e) => CeInterfaceTheme.Apply(window);
            return AcApplication.ShowModalWindow(window) == true;
        }
    }
}

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CETools.Civil3D
{
    /// <summary>
    /// Applies the saved CE Tools Dark/Light preference to every CE-owned WPF
    /// window. Civil 3D owns its native application chrome/ribbon background;
    /// all CE Tools windows, cards, inputs, tabs, reports and workflow centres
    /// use this palette consistently.
    /// </summary>
    internal static class CeInterfaceTheme
    {
        private static bool _initialized;

        internal static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;
            try
            {
                EventManager.RegisterClassHandler(
                    typeof(Window),
                    FrameworkElement.LoadedEvent,
                    new RoutedEventHandler(OnWindowLoaded),
                    true);
                EventManager.RegisterClassHandler(
                    typeof(Window),
                    Keyboard.PreviewKeyDownEvent,
                    new KeyEventHandler(OnWindowPreviewKeyDown),
                    true);
            }
            catch { }
        }

        internal static void Apply(Window window)
        {
            if (window == null) return;
            Initialize();
            if (!IsCeWindow(window)) return;

            // The welcome screen has its own branded card layout but reads the
            // exact same persisted theme. Do not flatten those custom cards.
            if (window is CeWelcomeWindow) return;

            bool light = string.Equals(CeThemeStore.Read(), "Light", StringComparison.OrdinalIgnoreCase);
            ThemePalette palette = light ? ThemePalette.Light() : ThemePalette.Dark();

            try
            {
                window.Background = palette.Window;
                window.Foreground = palette.Foreground;
                ApplyImplicitStyles(window, palette);
                if (window.Content is DependencyObject)
                    ApplyTree((DependencyObject)window.Content, palette);
            }
            catch
            {
                // Appearance must never prevent an engineering command opening.
            }
        }

        internal static void RefreshOpenWindows()
        {
            Initialize();
            try
            {
                System.Windows.Application application = System.Windows.Application.Current;
                if (application == null) return;
                foreach (Window window in application.Windows)
                    Apply(window);
            }
            catch { }
        }

        internal static Brush ForegroundBrush()
        {
            return string.Equals(CeThemeStore.Read(), "Light", StringComparison.OrdinalIgnoreCase)
                ? Brushes.Black
                : Brushes.White;
        }

        internal static Brush MutedBrush()
        {
            return string.Equals(CeThemeStore.Read(), "Light", StringComparison.OrdinalIgnoreCase)
                ? Brushes.DimGray
                : new SolidColorBrush(Color.FromRgb(184, 194, 207));
        }

        private static void OnWindowLoaded(object sender, RoutedEventArgs args)
        {
            Apply(sender as Window);
        }

        private static void OnWindowPreviewKeyDown(object sender, KeyEventArgs args)
        {
            if (args == null || args.Key != Key.Escape) return;
            Window window = sender as Window;
            if (window == null) return;

            // Workflow and production centres are persistent navigation windows.
            // Escape must never dismiss them accidentally. Settings dialogs keep
            // their normal Escape/Cancel behaviour.
            if (window is FloatingToolsWindow || window is DisciplineWorkflowWindow)
                args.Handled = true;
        }

        private static bool IsCeWindow(Window window)
        {
            if (window == null) return false;
            Type type = window.GetType();
            string ns = type.Namespace ?? string.Empty;
            if (ns.StartsWith("CETools.Civil3D", StringComparison.Ordinal)) return true;
            string title = window.Title ?? string.Empty;
            return title.StartsWith("CE Tools", StringComparison.OrdinalIgnoreCase) ||
                   title.StartsWith("CE-", StringComparison.OrdinalIgnoreCase) ||
                   title.StartsWith("CE ", StringComparison.OrdinalIgnoreCase);
        }

        private static void ApplyImplicitStyles(Window window, ThemePalette palette)
        {
            SetStyle(window, typeof(TextBlock), new Setter(TextBlock.ForegroundProperty, palette.Foreground));
            SetStyle(window, typeof(Label), new Setter(Control.ForegroundProperty, palette.Foreground));
            SetStyle(window, typeof(CheckBox), new Setter(Control.ForegroundProperty, palette.Foreground));
            SetStyle(window, typeof(RadioButton), new Setter(Control.ForegroundProperty, palette.Foreground));
            SetStyle(window, typeof(GroupBox), new Setter(Control.ForegroundProperty, palette.Foreground));
            SetStyle(window, typeof(Expander), new Setter(Control.ForegroundProperty, palette.Foreground));

            SetStyle(window, typeof(Button),
                new Setter(Control.BackgroundProperty, palette.Card),
                new Setter(Control.ForegroundProperty, palette.Foreground),
                new Setter(Control.BorderBrushProperty, palette.Border));
            SetStyle(window, typeof(TextBox),
                new Setter(Control.BackgroundProperty, palette.Input),
                new Setter(Control.ForegroundProperty, palette.Foreground),
                new Setter(Control.BorderBrushProperty, palette.Border));
            SetStyle(window, typeof(ComboBox),
                new Setter(Control.BackgroundProperty, palette.Input),
                new Setter(Control.ForegroundProperty, palette.Foreground),
                new Setter(Control.BorderBrushProperty, palette.Border));
            SetStyle(window, typeof(ListBox),
                new Setter(Control.BackgroundProperty, palette.Input),
                new Setter(Control.ForegroundProperty, palette.Foreground),
                new Setter(Control.BorderBrushProperty, palette.Border));
            SetStyle(window, typeof(ListView),
                new Setter(Control.BackgroundProperty, palette.Input),
                new Setter(Control.ForegroundProperty, palette.Foreground),
                new Setter(Control.BorderBrushProperty, palette.Border));
            SetStyle(window, typeof(TabControl),
                new Setter(Control.BackgroundProperty, palette.Window),
                new Setter(Control.ForegroundProperty, palette.Foreground),
                new Setter(Control.BorderBrushProperty, palette.Border));
            SetStyle(window, typeof(TabItem),
                new Setter(Control.BackgroundProperty, palette.Card),
                new Setter(Control.ForegroundProperty, palette.Foreground));
            SetStyle(window, typeof(DataGrid),
                new Setter(Control.BackgroundProperty, palette.Input),
                new Setter(Control.ForegroundProperty, palette.Foreground),
                new Setter(Control.BorderBrushProperty, palette.Border),
                new Setter(DataGrid.HorizontalGridLinesBrushProperty, palette.Border),
                new Setter(DataGrid.VerticalGridLinesBrushProperty, palette.Border));
            SetStyle(window, typeof(DataGridCell),
                new Setter(Control.BackgroundProperty, palette.Input),
                new Setter(Control.ForegroundProperty, palette.Foreground),
                new Setter(Control.BorderBrushProperty, palette.Border));
            SetStyle(window, typeof(DataGridColumnHeader),
                new Setter(Control.BackgroundProperty, palette.Card),
                new Setter(Control.ForegroundProperty, palette.Foreground),
                new Setter(Control.BorderBrushProperty, palette.Border));
        }

        private static void SetStyle(Window window, Type type, params Setter[] setters)
        {
            try
            {
                var style = new Style(type);
                foreach (Setter setter in setters) style.Setters.Add(setter);
                window.Resources[type] = style;
            }
            catch { }
        }

        private static void ApplyTree(DependencyObject value, ThemePalette palette)
        {
            if (value == null) return;

            TextBlock text = value as TextBlock;
            if (text != null)
            {
                text.Foreground = IsMuted(text.Foreground) ? palette.Muted :
                    IsAccent(text.Foreground) ? palette.Accent : palette.Foreground;
            }

            Control control = value as Control;
            if (control != null)
            {
                if (control is Button || control is TabItem)
                {
                    control.Background = palette.Card;
                    control.Foreground = palette.Foreground;
                    control.BorderBrush = palette.Border;
                }
                else if (control is TextBox || control is ComboBox || control is ListBox || control is ListView || control is DataGrid)
                {
                    control.Background = palette.Input;
                    control.Foreground = palette.Foreground;
                    control.BorderBrush = palette.Border;
                }
                else if (control is Label || control is CheckBox || control is RadioButton || control is GroupBox || control is Expander)
                {
                    control.Foreground = palette.Foreground;
                }
            }

            Border border = value as Border;
            if (border != null && border.Background != null && border.Background != Brushes.Transparent)
            {
                border.Background = palette.Card;
                if (border.BorderThickness.Left > 0.0 || border.BorderThickness.Top > 0.0 ||
                    border.BorderThickness.Right > 0.0 || border.BorderThickness.Bottom > 0.0)
                    border.BorderBrush = palette.Border;
            }

            int count;
            try { count = VisualTreeHelper.GetChildrenCount(value); }
            catch { return; }
            for (int index = 0; index < count; index++)
            {
                DependencyObject child;
                try { child = VisualTreeHelper.GetChild(value, index); }
                catch { continue; }
                ApplyTree(child, palette);
            }
        }

        private static bool IsMuted(Brush brush)
        {
            SolidColorBrush solid = brush as SolidColorBrush;
            if (solid == null) return false;
            Color color = solid.Color;
            int spread = Math.Max(color.R, Math.Max(color.G, color.B)) - Math.Min(color.R, Math.Min(color.G, color.B));
            return spread < 24 && color.R >= 70 && color.R <= 210;
        }

        private static bool IsAccent(Brush brush)
        {
            SolidColorBrush solid = brush as SolidColorBrush;
            if (solid == null) return false;
            Color color = solid.Color;
            return color.B > color.R + 20 || color.B > color.G + 20;
        }

        private sealed class ThemePalette
        {
            internal Brush Window;
            internal Brush Card;
            internal Brush Input;
            internal Brush Foreground;
            internal Brush Muted;
            internal Brush Border;
            internal Brush Accent;

            internal static ThemePalette Dark()
            {
                return new ThemePalette
                {
                    Window = new SolidColorBrush(Color.FromRgb(15, 19, 27)),
                    Card = new SolidColorBrush(Color.FromRgb(28, 35, 49)),
                    Input = new SolidColorBrush(Color.FromRgb(20, 27, 38)),
                    Foreground = Brushes.White,
                    Muted = new SolidColorBrush(Color.FromRgb(184, 194, 207)),
                    Border = new SolidColorBrush(Color.FromRgb(67, 82, 103)),
                    Accent = new SolidColorBrush(Color.FromRgb(74, 168, 255))
                };
            }

            internal static ThemePalette Light()
            {
                return new ThemePalette
                {
                    Window = new SolidColorBrush(Color.FromRgb(244, 247, 249)),
                    Card = Brushes.White,
                    Input = Brushes.White,
                    Foreground = Brushes.Black,
                    Muted = Brushes.DimGray,
                    Border = new SolidColorBrush(Color.FromRgb(184, 192, 201)),
                    Accent = new SolidColorBrush(Color.FromRgb(20, 122, 220))
                };
            }
        }
    }
}

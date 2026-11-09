using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
            if (window is CeWelcomeWindow)
            {
                // Keep the branded welcome layout, but make its check marks
                // use the same high-contrast theme-dependent brush.
                ApplyCheckBoxStyle(window, CurrentPalette());
                return;
            }

            ThemePalette palette = CurrentPalette();
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

        private static ThemePalette CurrentPalette()
        {
            return string.Equals(CeThemeStore.Read(), "Light", StringComparison.OrdinalIgnoreCase)
                ? ThemePalette.Light()
                : ThemePalette.Dark();
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
            if (window is FloatingToolsWindow || window is DisciplineWorkflowWindow)
                args.Handled = true;
        }

        private static void HookComboBox(ComboBox combo)
        {
            if (combo == null) return;
            try
            {
                // Popup item containers are created lazily and may never inherit
                // the Window's implicit ComboBoxItem style. Assign the style to
                // the ComboBox itself so future and virtualized items stay white.
                if (string.Equals(CeThemeStore.Read(), "Dark", StringComparison.OrdinalIgnoreCase))
                {
                    var itemStyle = new Style(typeof(ComboBoxItem));
                    itemStyle.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
                    itemStyle.Setters.Add(new Setter(System.Windows.Documents.TextElement.ForegroundProperty, Brushes.White));
                    itemStyle.Setters.Add(new Setter(Control.BackgroundProperty,
                        new SolidColorBrush(Color.FromRgb(29, 38, 53))));
                    itemStyle.Setters.Add(new Setter(UIElement.OpacityProperty, 1.0));
                    combo.ItemContainerStyle = itemStyle;
                    combo.Foreground = Brushes.White;
                }
                // Civil 3D 2023 targets the .NET Framework WPF API where
                // ComboBox.DropDownOpenedEvent is not exposed as a routed-event
                // identifier. Hook the supported instance event instead.
                combo.DropDownOpened -= OnComboBoxDropDownOpened;
                combo.DropDownOpened += OnComboBoxDropDownOpened;
            }
            catch { }
        }

        private static void OnComboBoxDropDownOpened(object sender, EventArgs args)
        {
            ComboBox combo = sender as ComboBox;
            if (combo == null) return;
            Window owner = Window.GetWindow(combo);
            if (owner == null || !IsCeWindow(owner)) return;

            ThemePalette palette = CurrentPalette();
            try
            {
                // ComboBox dropdowns are hosted in a separate Popup visual tree,
                // so normal Window resource inheritance is not sufficient.
                Popup popup = combo.Template == null
                    ? null
                    : combo.Template.FindName("PART_Popup", combo) as Popup;
                if (popup != null && popup.Child is DependencyObject)
                {
                    ApplyTree((DependencyObject)popup.Child, palette);
                    if (string.Equals(CeThemeStore.Read(), "Dark", StringComparison.OrdinalIgnoreCase))
                        ForceDropdownTextWhite((DependencyObject)popup.Child);
                }

                foreach (object item in combo.Items)
                {
                    ComboBoxItem container = combo.ItemContainerGenerator.ContainerFromItem(item) as ComboBoxItem;
                    if (container == null) continue;
                    ApplyComboItem(container, palette);
                    if (string.Equals(CeThemeStore.Read(), "Dark", StringComparison.OrdinalIgnoreCase))
                        ForceDropdownTextWhite(container);
                }
            }
            catch { }
        }

        // ComboBox popup is hosted separately; inherited label/text colors can
        // stay muted gray despite white foreground on the ComboBox itself.
        private static void ForceDropdownTextWhite(DependencyObject visual)
        {
            if (visual == null) return;
            TextBlock text = visual as TextBlock;
            if (text != null)
            {
                text.Foreground = Brushes.White;
                text.Opacity = 1.0;
            }
            ComboBoxItem item = visual as ComboBoxItem;
            if (item != null)
            {
                // WPF editable ComboBox items can inherit disabled/muted text
                // even when the container foreground is white.
                item.Foreground = Brushes.White;
                item.SetCurrentValue(Control.ForegroundProperty, Brushes.White);
                item.SetValue(System.Windows.Documents.TextElement.ForegroundProperty, Brushes.White);
                item.Opacity = 1.0;
            }
            int count = 0;
            try { count = VisualTreeHelper.GetChildrenCount(visual); } catch { return; }
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = null;
                try { child = VisualTreeHelper.GetChild(visual, i); } catch { }
                if (child != null) ForceDropdownTextWhite(child);
            }
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
            ApplyCheckBoxStyle(window, palette);
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
            SetComboBoxItemStyle(window, palette);
            SetStyle(window, typeof(ListBox),
                new Setter(Control.BackgroundProperty, palette.Input),
                new Setter(Control.ForegroundProperty, palette.Foreground),
                new Setter(Control.BorderBrushProperty, palette.Border));
            SetStyle(window, typeof(ListBoxItem),
                new Setter(Control.BackgroundProperty, palette.Input),
                new Setter(Control.ForegroundProperty, palette.Foreground));
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

        // CheckBox.Foreground only colours its caption; WPF's default
        // theme renders the tick itself using a separate system brush.
        // Provide one CE-owned template so dark backgrounds get a WHITE tick
        // and light backgrounds get a BLACK tick on every CE WPF window.
        private static void ApplyCheckBoxStyle(Window window, ThemePalette palette)
        {
            try
            {
                var row = new FrameworkElementFactory(typeof(StackPanel));
                row.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
                row.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

                var square = new FrameworkElementFactory(typeof(Border));
                square.SetValue(FrameworkElement.WidthProperty, 18.0);
                square.SetValue(FrameworkElement.HeightProperty, 18.0);
                square.SetValue(Border.CornerRadiusProperty, new CornerRadius(2));
                square.SetValue(Border.BorderThicknessProperty, new Thickness(1.25));
                square.SetValue(Border.BorderBrushProperty, palette.Border);
                square.SetValue(Border.BackgroundProperty, palette.Input);
                square.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

                var mark = new FrameworkElementFactory(typeof(System.Windows.Shapes.Path));
                mark.Name = "CECheckMark";
                mark.SetValue(System.Windows.Shapes.Path.DataProperty,
                    Geometry.Parse("M 2,7 L 5.5,10.5 L 13,2.5"));
                mark.SetValue(System.Windows.Shapes.Path.StrokeProperty,
                    palette.IsDark ? Brushes.White : Brushes.Black);
                mark.SetValue(System.Windows.Shapes.Path.StrokeThicknessProperty, 2.2);
                mark.SetValue(System.Windows.Shapes.Path.StrokeStartLineCapProperty, PenLineCap.Round);
                mark.SetValue(System.Windows.Shapes.Path.StrokeEndLineCapProperty, PenLineCap.Round);
                mark.SetValue(FrameworkElement.WidthProperty, 15.0);
                mark.SetValue(FrameworkElement.HeightProperty, 15.0);
                mark.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
                
                var dash = new FrameworkElementFactory(typeof(System.Windows.Shapes.Path));
                dash.Name = "CEIndeterminateMark";
                dash.SetValue(System.Windows.Shapes.Path.DataProperty,
                    Geometry.Parse("M 3,8 L 12,8"));
                dash.SetValue(System.Windows.Shapes.Path.StrokeProperty,
                    palette.IsDark ? Brushes.White : Brushes.Black);
                dash.SetValue(System.Windows.Shapes.Path.StrokeThicknessProperty, 2.2);
                dash.SetValue(FrameworkElement.WidthProperty, 15.0);
                dash.SetValue(FrameworkElement.HeightProperty, 15.0);
                dash.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
                // A Grid allows indeterminate and checked glyphs to overlap.
                var glyphs = new FrameworkElementFactory(typeof(Grid));
                glyphs.AppendChild(mark);
                glyphs.AppendChild(dash);
                square.AppendChild(glyphs);
                row.AppendChild(square);

                var caption = new FrameworkElementFactory(typeof(ContentPresenter));
                caption.SetValue(ContentPresenter.ContentSourceProperty, "Content");
                caption.SetValue(FrameworkElement.MarginProperty, new Thickness(7, 0, 0, 0));
                caption.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
                row.AppendChild(caption);

                var template = new ControlTemplate(typeof(CheckBox));
                template.VisualTree = row;

                var checkedTrigger = new Trigger
                {
                    Property = ToggleButton.IsCheckedProperty,
                    Value = true
                };
                checkedTrigger.Setters.Add(
                    new Setter(UIElement.VisibilityProperty, Visibility.Visible, "CECheckMark"));
                template.Triggers.Add(checkedTrigger);
                var indeterminateTrigger = new Trigger
                {
                    Property = ToggleButton.IsCheckedProperty,
                    Value = null
                };
                indeterminateTrigger.Setters.Add(
                    new Setter(UIElement.VisibilityProperty, Visibility.Visible, "CEIndeterminateMark"));
                template.Triggers.Add(indeterminateTrigger);
                var disabledTrigger = new Trigger
                {
                    Property = UIElement.IsEnabledProperty,
                    Value = false
                };
                disabledTrigger.Setters.Add(
                    new Setter(UIElement.OpacityProperty, 0.5));
                template.Triggers.Add(disabledTrigger);

                var style = new Style(typeof(CheckBox));
                style.Setters.Add(new Setter(Control.ForegroundProperty, palette.Foreground));
                style.Setters.Add(new Setter(Control.TemplateProperty, template));
                style.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
                window.Resources[typeof(CheckBox)] = style;
            }
            catch
            {
                // A theme failure must never interrupt an engineering command.
            }
        }

        private static void SetComboBoxItemStyle(Window window, ThemePalette palette)
        {
            try
            {
                var style = new Style(typeof(ComboBoxItem));
                style.Setters.Add(new Setter(Control.BackgroundProperty, palette.Input));
                style.Setters.Add(new Setter(Control.ForegroundProperty,
                    palette.IsDark ? Brushes.White : palette.Foreground));
                style.Setters.Add(new Setter(System.Windows.Documents.TextElement.ForegroundProperty,
                    palette.IsDark ? Brushes.White : palette.Foreground));
                style.Setters.Add(new Setter(Control.BorderBrushProperty, palette.Border));
                style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 4, 6, 4)));

                var highlighted = new Trigger
                {
                    Property = ComboBoxItem.IsHighlightedProperty,
                    Value = true
                };
                highlighted.Setters.Add(new Setter(Control.BackgroundProperty, palette.Accent));
                highlighted.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
                style.Triggers.Add(highlighted);

                var selected = new Trigger
                {
                    Property = ComboBoxItem.IsSelectedProperty,
                    Value = true
                };
                selected.Setters.Add(new Setter(Control.BackgroundProperty, palette.Accent));
                selected.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
                style.Triggers.Add(selected);
                window.Resources[typeof(ComboBoxItem)] = style;
            }
            catch { }
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

        private static void ApplyComboItem(ComboBoxItem item, ThemePalette palette)
        {
            if (item == null) return;
            item.Background = item.IsHighlighted || item.IsSelected ? palette.Accent : palette.Input;
            item.Foreground = palette.IsDark || item.IsHighlighted || item.IsSelected ? Brushes.White : palette.Foreground;
            item.SetCurrentValue(Control.ForegroundProperty,
                palette.IsDark || item.IsHighlighted || item.IsSelected ? Brushes.White : palette.Foreground);
            if (palette.IsDark)
                item.SetValue(System.Windows.Documents.TextElement.ForegroundProperty, Brushes.White);
            item.BorderBrush = palette.Border;
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
                else if (control is ComboBoxItem)
                {
                    ApplyComboItem((ComboBoxItem)control, palette);
                }
                else if (control is ListBoxItem)
                {
                    control.Background = palette.Input;
                    control.Foreground = palette.Foreground;
                    control.BorderBrush = palette.Border;
                }
                else if (control is ComboBox)
                {
                    HookComboBox((ComboBox)control);
                    control.Background = palette.Input;
                    control.Foreground = palette.Foreground;
                    control.BorderBrush = palette.Border;
                }
                else if (control is TextBox || control is ListBox || control is ListView || control is DataGrid || control is ScrollViewer)
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

            // Many CE Tools popups set a light Background directly on a
            // Grid, DockPanel or StackPanel. Setting Window.Background alone
            // leaves those full-size panels bright white in Dark mode.
            Panel panel = value as Panel;
            if (panel != null && panel.Background != null &&
                panel.Background != Brushes.Transparent)
                panel.Background = palette.Window;

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
            internal bool IsDark;
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
                    IsDark = true,
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
                    IsDark = false,
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

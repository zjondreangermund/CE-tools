using System;
using System.Drawing;
using System.Runtime.CompilerServices;
using System.Windows.Forms;

namespace CETools.Civil3D
{
    internal static class CeWinFormsTheme
    {
        private sealed class Applied { public Applied() { } internal string Theme; }
        private static readonly ConditionalWeakTable<Form, Applied> States = new ConditionalWeakTable<Form, Applied>();
        private static bool initialized;
        internal static void Initialize()
        {
            if (initialized) return;
            initialized = true;
            Application.Idle += (s, e) => Refresh();
        }
        internal static void Refresh()
        {
            string theme = CeThemeStore.Read();
            foreach (Form form in Application.OpenForms)
            {
                if (!(form.GetType().Namespace ?? "").StartsWith("CETools.Civil3D", StringComparison.Ordinal) &&
                    !(form.Text ?? "").StartsWith("CE Tools", StringComparison.OrdinalIgnoreCase)) continue;
                var state = States.GetOrCreateValue(form);
                if (state.Theme == theme) continue;
                Apply(form, string.Equals(theme, "Light", StringComparison.OrdinalIgnoreCase));
                state.Theme = theme;
            }
        }
        private static void Apply(Control control, bool light)
        {
            Color background = light ? Color.White : Color.FromArgb(15, 19, 27);
            Color input = light ? Color.White : Color.FromArgb(28, 35, 49);
            Color foreground = light ? Color.Black : Color.White;
            control.BackColor = control is TextBoxBase || control is ListControl || control is ButtonBase ? input : background;
            control.ForeColor = foreground;
            var button = control as Button;
            if (button != null) { button.UseVisualStyleBackColor = false; button.FlatStyle = FlatStyle.Flat; }
            var grid = control as DataGridView;
            if (grid != null)
            {
                grid.EnableHeadersVisualStyles = false; grid.BackgroundColor = background;
                foreach (var style in new[] { grid.DefaultCellStyle, grid.ColumnHeadersDefaultCellStyle,
                    grid.RowHeadersDefaultCellStyle, grid.RowsDefaultCellStyle, grid.AlternatingRowsDefaultCellStyle })
                {
                    style.BackColor = input; style.ForeColor = foreground;
                    style.SelectionBackColor = light ? Color.LightSteelBlue : Color.FromArgb(45, 65, 90);
                    style.SelectionForeColor = foreground;
                }
            }
            foreach (Control child in control.Controls) Apply(child, light);
            control.ControlAdded -= ChildAdded;
            control.ControlAdded += ChildAdded;
        }
        private static void ChildAdded(object sender, ControlEventArgs e)
        {
            Apply(e.Control, string.Equals(CeThemeStore.Read(), "Light", StringComparison.OrdinalIgnoreCase));
        }
    }
}

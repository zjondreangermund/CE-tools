using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.PaperSpaceMonochromeCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Display layout plot styles without altering object/layer colours,
    /// engineering entities, viewport camera settings or scale.
    /// </summary>
    public sealed class PaperSpaceMonochromeCommands
    {
        [CommandMethod("CE_TOOLS", "CE_PAPERMONO", CommandFlags.Modal | CommandFlags.Redraw)]
        public void SetPaperMonochrome()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            var database = document.Database;
            var layouts = new List<string>();
            using (Transaction read = database.TransactionManager.StartTransaction())
            {
                var dictionary = (DBDictionary)read.GetObject(database.LayoutDictionaryId, OpenMode.ForRead);
                foreach (DBDictionaryEntry pair in dictionary)
                {
                    Layout layout = read.GetObject(pair.Value, OpenMode.ForRead) as Layout;
                    if (layout != null && !layout.ModelType) layouts.Add(layout.LayoutName);
                }
            }
            if (layouts.Count == 0) return;
            var validator = PlotSettingsValidator.Current;
            using (var plotSettings = new PlotSettings(false))
                validator.RefreshLists(plotSettings);
            bool colorDependent = database.PlotStyleMode;
            string style = colorDependent ? "monochrome.ctb" : "monochrome.stb";
            bool styleAvailable = validator.GetPlotStyleSheetList()
                .Cast<string>().Any(name => string.Equals(name, style, StringComparison.OrdinalIgnoreCase));
            using (var form = new Form { Text = "CE Tools - Paper Space Black & White", Width = 530,
                Height = 265, StartPosition = FormStartPosition.CenterScreen,
                FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false })
            {
                var label = new Label { Dock = DockStyle.Top, Height = 75,
                    Text = "Display monochrome plot styles in Paper Space, matching plot preview.\n" +
                    "This changes only layout plot settings. Model-space colours and engineering data remain unchanged.",
                    Padding = new Padding(12) };
                var selection = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList };
                selection.Items.Add("All paper-space layouts");
                foreach (string item in layouts) selection.Items.Add(item);
                selection.SelectedIndex = 0;
                var info = new Label { Dock = DockStyle.Top, Height = 45,
                    Text = "Plot table: " + style + (styleAvailable ? "" : " (not installed; display existing plot style only)"),
                    Padding = new Padding(12) };
                var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 45,
                    FlowDirection = System.Windows.Forms.FlowDirection.RightToLeft };
                var ok = new Button { Text = "Apply", AutoSize = true, DialogResult = DialogResult.OK };
                var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
                buttons.Controls.Add(ok); buttons.Controls.Add(cancel);
                form.AcceptButton = ok; form.CancelButton = cancel;
                form.Controls.Add(info); form.Controls.Add(selection); form.Controls.Add(label); form.Controls.Add(buttons);
                if (form.ShowDialog() != DialogResult.OK) return;
                var chosen = selection.SelectedIndex == 0 ? layouts :
                    new List<string> { Convert.ToString(selection.SelectedItem) };
                int updated = 0;
                using (document.LockDocument())
                using (Transaction edit = database.TransactionManager.StartTransaction())
                {
                    var dictionary = (DBDictionary)edit.GetObject(database.LayoutDictionaryId, OpenMode.ForRead);
                    foreach (string name in chosen)
                    {
                        if (!dictionary.Contains(name)) continue;
                        var layout = (Layout)edit.GetObject(dictionary.GetAt(name), OpenMode.ForWrite);
                        if (styleAvailable) validator.SetCurrentStyleSheet(layout, style);
                        layout.ShowPlotStyles = true;
                        updated++;
                    }
                    edit.Commit();
                }
                document.Editor.Regen();
                MessageBox.Show("Paper Space layouts updated: " + updated +
                    "\nDisplay plot styles: ON" +
                    "\nPlot style table: " + (styleAvailable ? style : "unchanged (requested table not installed)") +
                    "\n\nThis is a layout display change; existing drawing object colours are preserved.",
                    "CE Tools - Paper Space Display");
            }
        }
    }
}

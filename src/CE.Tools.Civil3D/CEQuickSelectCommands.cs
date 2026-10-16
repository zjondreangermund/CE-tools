using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.CEQuickSelectCommands))]

namespace CETools.Civil3D
{
    /// <summary>V154 CE Quick Select: scan model space and every layout for filtered objects.</summary>
    public sealed class CEQuickSelectCommands
    {
        [CommandMethod("CE_TOOLS", "CE_QUICKSELECT", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void QuickSelect()
        {
            Document doc = AcApplication.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            using (var dialog = new QuickSelectDialog())
            {
                if (dialog.ShowDialog() != DialogResult.OK) return;
                var matches = new List<ObjectId>();
                int scanned = 0;
                using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
                {
                    BlockTable bt = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                    foreach (ObjectId recordId in bt)
                    {
                        BlockTableRecord record = tr.GetObject(recordId, OpenMode.ForRead) as BlockTableRecord;
                        if (record == null || !record.IsLayout) continue;
                        foreach (ObjectId id in record)
                        {
                            Entity entity = null;
                            try { entity = tr.GetObject(id, OpenMode.ForRead, false) as Entity; }
                            catch { }
                            if (entity == null || entity.IsErased) continue;
                            scanned++;
                            if (Matches(entity, dialog.TypeFilter, dialog.PropertyFilter,
                                dialog.OperatorFilter, dialog.FilterValue))
                                matches.Add(id);
                        }
                    }
                    tr.Commit();
                }
                if (matches.Count > 0)
                    doc.Editor.SetImpliedSelection(matches.ToArray());
                else
                    doc.Editor.SetImpliedSelection(new ObjectId[0]);
                doc.Editor.WriteMessage("\nCE_QUICKSELECT: scanned {0} objects, selected {1}.",
                    scanned, matches.Count);
            }
        }
        private static bool Matches(Entity entity, string type, string property, string comparison, string value)
        {
            if (type != "All object types")
            {
                string actualType;
                try { actualType = entity.GetRXClass().DxfName ?? ""; }
                catch { return false; }
                string expected = type;
                switch (type)
                {
                    case "Polyline": expected = "LWPOLYLINE"; break;
                    case "Block reference": expected = "INSERT"; break;
                    case "Feature line": expected = "AECC_FEATURE_LINE"; break;
                    case "Alignment": expected = "AECC_ALIGNMENT"; break;
                    case "Profile": expected = "AECC_PROFILE"; break;
                    case "Profile view": expected = "AECC_PROFILE_VIEW"; break;
                }
                if (!string.Equals(actualType, expected, StringComparison.OrdinalIgnoreCase)) return false;
            }
            string actual = "";
            try
            {
                switch (property)
                {
                    case "Color": actual = entity.Color.ColorIndex.ToString(CultureInfo.InvariantCulture); break;
                    case "Layer": actual = entity.Layer; break;
                    case "Linetype": actual = entity.Linetype; break;
                    case "Linetype scale": actual = entity.LinetypeScale.ToString(CultureInfo.InvariantCulture); break;
                    case "Lineweight": actual = entity.LineWeight.ToString(); break;
                    case "Transparency": actual = entity.Transparency.Alpha.ToString(CultureInfo.InvariantCulture); break;
                    case "Handle": actual = entity.Handle.ToString(); break;
                    case "Thickness":
                        var info = entity.GetType().GetProperty("Thickness");
                        actual = info == null ? "" : Convert.ToString(info.GetValue(entity, null), CultureInfo.InvariantCulture);
                        break;
                }
            }
            catch { return false; }
            actual = actual ?? "";
            value = value ?? "";
            int cmp = string.Compare(actual, value, StringComparison.OrdinalIgnoreCase);
            double numberA, numberB;
            if (double.TryParse(actual, NumberStyles.Any, CultureInfo.InvariantCulture, out numberA) &&
                double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out numberB))
                cmp = numberA.CompareTo(numberB);
            switch (comparison)
            {
                case "Not Equals": return cmp != 0;
                case "Contains": return actual.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
                case "Begins with": return actual.StartsWith(value, StringComparison.OrdinalIgnoreCase);
                case "Greater than": return cmp > 0;
                case "Less than": return cmp < 0;
                default: return cmp == 0;
            }
        }

        private sealed class QuickSelectDialog : Form
        {
            private readonly ComboBox _type = new ComboBox();
            private readonly ComboBox _property = new ComboBox();
            private readonly ComboBox _operator = new ComboBox();
            private readonly TextBox _value = new TextBox();
            internal string TypeFilter { get { return _type.SelectedItem.ToString(); } }
            internal string PropertyFilter { get { return _property.SelectedItem.ToString(); } }
            internal string OperatorFilter { get { return _operator.SelectedItem.ToString(); } }
            internal string FilterValue { get { return _value.Text; } }
            internal QuickSelectDialog()
            {
                Text = "CE Tools - Quick Select";
                Width = 520; Height = 342;
                FormBorderStyle = FormBorderStyle.FixedDialog;
                StartPosition = FormStartPosition.CenterScreen;
                MaximizeBox = false; MinimizeBox = false;
                AddField("Object type", _type, 20,
                    new[] { "All object types", "LINE", "Polyline", "CIRCLE", "ARC", "TEXT",
                        "MTEXT", "HATCH", "Block reference", "VIEWPORT", "Feature line",
                        "Alignment", "Profile", "Profile view" });
                AddField("Property", _property, 65,
                    new[] { "Color", "Layer", "Linetype", "Linetype scale",
                        "Lineweight", "Transparency", "Thickness", "Handle" });
                AddField("Operator", _operator, 110,
                    new[] { "Equals", "Not Equals", "Contains",
                        "Begins with", "Greater than", "Less than" });
                Controls.Add(new Label { Left=20, Top=162, Width=120, Text="Value" });
                _value.Left=155; _value.Top=155; _value.Width=325; _value.Text="10";
                Controls.Add(_value);
                Controls.Add(new Label { Left=20, Top=195, Width=465, Height=43,
                    Text="Searches model space and every paper-space layout. Matching objects are placed in the active selection." });
                var ok = new Button { Text="Select", Left=275, Top=245,
                    Width=95, DialogResult=DialogResult.OK };
                var cancel = new Button { Text="Cancel", Left=385, Top=245,
                    Width=95, DialogResult=DialogResult.Cancel };
                Controls.Add(ok); Controls.Add(cancel);
                AcceptButton=ok; CancelButton=cancel;
            }
            private void AddField(string title, ComboBox combo, int y, string[] values)
            {
                Controls.Add(new Label { Left=20, Top=y+7, Width=130, Text=title });
                combo.Left=155; combo.Top=y; combo.Width=325;
                combo.DropDownStyle=ComboBoxStyle.DropDownList;
                combo.Items.AddRange(values.Cast<object>().ToArray());
                combo.SelectedIndex=0;
                Controls.Add(combo);
            }
        }
    }
}

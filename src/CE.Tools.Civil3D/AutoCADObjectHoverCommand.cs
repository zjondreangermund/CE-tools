using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.AutoCADObjectHoverCommand))]

namespace CETools.Civil3D
{
    /// <summary>Read-only live inspector for native AutoCAD entities under the cursor.</summary>
    public sealed class AutoCADObjectHoverCommand
    {
        private static Document _document;
        private static Form _window;
        private static Label _display;
        private static ObjectId _lastId = ObjectId.Null;
        private static Point3d _lastPoint;
        private static readonly object Sync = new object();

        [CommandMethod("CE_TOOLS", "CE_AUTOCADOBJECTHOVER", CommandFlags.Modal)]
        public void Start()
        {
            Document doc = AcApplication.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            Form form = new HoverInspectorForm
            {
                Text = "CE Tools - AutoCAD Object Hover (LIVE)",
                Width = 560, Height = 430, TopMost = false,
                StartPosition = FormStartPosition.Manual
            };
            _display = new Label
            {
                Dock = DockStyle.Fill, Padding = new Padding(14),
                Font = new System.Drawing.Font("Segoe UI", 10),
                AutoSize = false, Text =
                    "Move the cursor directly over an AutoCAD object.\r\n" +
                    "Object properties update automatically. Press Enter or Esc to finish."
            };
            form.Controls.Add(_display);

            lock (Sync)
            {
                _document = doc; _window = form;
                _lastId = ObjectId.Null;
                doc.Editor.PointMonitor += OnPointMonitor;
            }

            try
            {
                Autodesk.AutoCAD.ApplicationServices.Application.ShowModelessDialog(form);
                while (true)
                {
                    PromptPointOptions options = new PromptPointOptions(
                        "\nAutoCAD Object Hover active. Move cursor over an object; press Enter or Esc to finish: ");
                    options.AllowNone = true;
                    PromptPointResult result = doc.Editor.GetPoint(options);
                    if (result.Status != PromptStatus.OK) break;
                }
            }
            finally
            {
                lock (Sync)
                {
                    doc.Editor.PointMonitor -= OnPointMonitor;
                    if (_window != null && !_window.IsDisposed) _window.Close();
                    _window = null; _display = null; _document = null;
                    _lastId = ObjectId.Null;
                }
            }
        }

        private static void OnPointMonitor(object sender, PointMonitorEventArgs e)
        {
            Document doc; Form form;
            lock (Sync) { doc = _document; form = _window; }
            if (doc == null || form == null || form.IsDisposed) return;

            ObjectId id = ObjectId.Null;
            try
            {
                FullSubentityPath[] paths = e.Context.GetPickedEntities();
                if (paths != null && paths.Length > 0)
                {
                    ObjectId[] ids = paths[0].GetObjectIds();
                    if (ids != null && ids.Length > 0) id = ids[ids.Length - 1];
                }
            }
            catch { }

            Point3d cursor = e.Context.RawPoint;
            lock (Sync)
            {
                if (id == _lastId && cursor.DistanceTo(_lastPoint) < 0.01) return;
                _lastId = id; _lastPoint = cursor;
            }

            // Keep the last valid object report while the cursor passes across
            // gaps between pickable graphics. This avoids flashing between object
            // data and an empty-state message during normal cursor movement.
            if (id.IsNull) return;
            string report = BuildReport(doc, id, cursor);

            ((HoverInspectorForm)form).UpdateReport(_display, report);
        }

        private static string BuildReport(Document doc, ObjectId id, Point3d cursor)
        {
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                Entity ent;
                try { ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity; }
                catch { ent = null; }
                if (ent == null) return "Object is no longer available.";

                StringBuilder b = new StringBuilder();
                b.AppendLine("AUTOCAD OBJECT HOVER — LIVE");
                b.AppendLine("Cursor X: " + F(cursor.X) + "    Y: " + F(cursor.Y) + "    Z: " + F(cursor.Z));
                b.AppendLine();
                b.AppendLine("Object type: " + ent.GetType().Name);
                b.AppendLine("Handle: " + ent.Handle);
                b.AppendLine("Layer: " + Safe(() => ent.Layer));
                b.AppendLine("Color: " + Safe(() => ent.Color.ToString()));
                b.AppendLine("Linetype: " + Safe(() => ent.Linetype));
                b.AppendLine("Lineweight: " + Safe(() => ent.LineWeight.ToString()));
                try { b.AppendLine("Extents min: " + Format(ent.GeometricExtents.MinPoint)); } catch { }
                try { b.AppendLine("Extents max: " + Format(ent.GeometricExtents.MaxPoint)); } catch { }
                b.AppendLine();

                AddProperty(b, ent, "Length");
                AddProperty(b, ent, "Area");
                AddProperty(b, ent, "Radius");
                AddProperty(b, ent, "Diameter");
                AddProperty(b, ent, "Circumference");
                AddProperty(b, ent, "Center");
                AddProperty(b, ent, "StartPoint");
                AddProperty(b, ent, "EndPoint");
                AddProperty(b, ent, "StartAngle");
                AddProperty(b, ent, "EndAngle");
                AddProperty(b, ent, "Normal");
                AddProperty(b, ent, "Closed");
                AddProperty(b, ent, "NumberOfVertices");
                AddProperty(b, ent, "TextString");
                AddProperty(b, ent, "Height");
                AddProperty(b, ent, "Rotation");
                AddProperty(b, ent, "Name");
                AddProperty(b, ent, "ScaleFactors");
                AddProperty(b, ent, "Position");

                try
                {
                    Polyline pl = ent as Polyline;
                    if (pl != null)
                    {
                        b.AppendLine("Vertices:");
                        for (int i = 0; i < pl.NumberOfVertices; i++)
                        {
                            Point2d p = pl.GetPoint2dAt(i);
                            b.AppendLine("  " + (i + 1) + ": X " + F(p.X) + "  Y " + F(p.Y) +
                                         "  Z " + F(pl.GetPoint3dAt(i).Z));
                        }
                    }
                }
                catch { }
                tr.Commit();
                return b.ToString();
            }
        }

        private static void AddProperty(StringBuilder b, object obj, string name)
        {
            try
            {
                PropertyInfo p = obj.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
                if (p == null || !p.CanRead || p.GetIndexParameters().Length != 0) return;
                object value = p.GetValue(obj, null);
                if (value != null) b.AppendLine(name + ": " + Format(value));
            }
            catch { }
        }

        private static string Format(object value)
        {
            if (value is double) return F((double)value);
            if (value is Point3d)
            {
                Point3d p = (Point3d)value;
                return F(p.X) + ", " + F(p.Y) + ", " + F(p.Z);
            }
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static string Safe(Func<string> getter)
        {
            try { return getter(); } catch { return "N/A"; }
        }

        private static string F(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}

using System;
using System.Collections.Generic;
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
using Autodesk.Civil.ApplicationServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.RoadModelHoverCommand))]

namespace CETools.Civil3D
{
    /// <summary>Read-only live inspector for Civil 3D road alignments.</summary>
    public sealed class RoadModelHoverCommand
    {
        private static Document _document;
        private static Form _window;
        private static Label _display;
        private static ComboBox _fgSurfaceCombo;
        private static ComboBox _egSurfaceCombo;
        private static ComboBox _bottomSurfaceCombo;
        private static ObjectId _selectedFgSurface = ObjectId.Null;
        private static ObjectId _selectedEgSurface = ObjectId.Null;
        private static ObjectId _selectedBottomSurface = ObjectId.Null;
        private static List<ObjectId> _alignmentIds;
        private sealed class SurfaceChoice
        {
            public ObjectId Id { get; set; }
            public string Name { get; set; }
            public override string ToString() { return Name; }
        }
        private static Point3d _lastPoint;
        private static bool _hasLastPoint;
        private static readonly object Sync = new object();

        [CommandMethod("CE_TOOLS", "CE_ROADMODELHOVER", CommandFlags.Modal)]
        public void Start()
        {
            Document doc = AcApplication.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            List<ObjectId> ids = ReadAlignmentIds();
            if (ids.Count == 0)
            {
                doc.Editor.WriteMessage("\nCE_ROADMODELHOVER: no Civil 3D road alignments found.");
                return;
            }

            Form form = new HoverInspectorForm { Text = "CE Tools - Road Model Hover (LIVE)", Width = 680, Height = 650,
                TopMost = false, StartPosition = FormStartPosition.Manual };
            TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            FlowLayoutPanel selectors = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(8) };
            _fgSurfaceCombo = AddSurfaceSelector(selectors, "FG surface:");
            _egSurfaceCombo = AddSurfaceSelector(selectors, "EG surface:");
            _bottomSurfaceCombo = AddSurfaceSelector(selectors, "Bottom surface:");
            _display = new Label { Dock = DockStyle.Fill, Padding = new Padding(12),
                Font = new System.Drawing.Font("Segoe UI", 10), AutoSize = false,
                Text = "Select FG, EG and Bottom surfaces above, then move the cursor over a road alignment." };
            layout.Controls.Add(selectors, 0, 0);
            layout.Controls.Add(_display, 0, 1);
            form.Controls.Add(layout);
            lock (Sync)
            {
                _document = doc; _window = form; _alignmentIds = ids;
                _hasLastPoint = false;
                doc.Editor.PointMonitor += OnPointMonitor;
            }
            try
            {
                Autodesk.AutoCAD.ApplicationServices.Application.ShowModelessDialog(form);
                while (true)
                {
                    PromptPointOptions options = new PromptPointOptions(
                        "\nMove cursor over a road alignment. Press Enter or Esc to finish: ");
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
                    _window = null; _display = null; _document = null; _alignmentIds = null;
                }
            }
        }

        private static List<ObjectId> ReadAlignmentIds()
        {
            List<ObjectId> ids = new List<ObjectId>();
            CivilDocument civil = CivilApplication.ActiveDocument;
            if (civil == null) return ids;
            try
            {
                MethodInfo m = civil.GetType().GetMethod("GetAlignmentIds", Type.EmptyTypes);
                var values = m == null ? null : m.Invoke(civil, null) as System.Collections.IEnumerable;
                if (values != null)
                    foreach (object value in values)
                        if (value is ObjectId && !((ObjectId)value).IsNull) ids.Add((ObjectId)value);
            }
            catch { }
            return ids.Distinct().ToList();
        }

        private static void OnPointMonitor(object sender, PointMonitorEventArgs e)
        {
            Document doc; Form form; List<ObjectId> ids;
            lock (Sync) { doc = _document; form = _window; ids = _alignmentIds; }
            if (doc == null || form == null || form.IsDisposed || ids == null) return;
            bool snapped;
            Point3d cursor = GetSnapAwarePoint(e.Context, out snapped);
            lock (Sync)
            {
                if (_hasLastPoint && _lastPoint.DistanceTo(cursor) < 0.01) return;
                _lastPoint = cursor; _hasLastPoint = true;
            }
            string report = BuildReport(doc, ids, cursor, snapped);
            ((HoverInspectorForm)form).UpdateReport(_display, report);
        }

        private static string BuildReport(Document doc, List<ObjectId> ids, Point3d cursor, bool snapped)
        {
            double bestOffset = double.MaxValue;
            string best = null;
            ObjectId bestAlignmentId = ObjectId.Null;
            double bestStation = 0.0;
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids)
                {
                    try
                    {
                        DBObject alignment = tr.GetObject(id, OpenMode.ForRead, false);
                        double station, offset;
                        if (!TryStationOffset(alignment, cursor, out station, out offset)) continue;
                        double absOffset = Math.Abs(offset);
                        if (absOffset < bestOffset)
                        {
                            bestOffset = absOffset;
                            bestAlignmentId = id;
                            bestStation = station;
                            best = FormatAlignment(alignment, station, offset, cursor);
                        }
                    }
                    catch { }
                }
                tr.Commit();
            }
            ObjectId fg = _selectedFgSurface;
            ObjectId eg = _selectedEgSurface;
            ObjectId bottom = _selectedBottomSurface;
            string surfaceReport = BuildSurfaceReport(doc, fg, eg, bottom, cursor);
            string gradeReport = bestAlignmentId.IsNull
                ? "\r\nROAD GRADES: no road alignment found at this point."
                : BuildGradeReport(doc, bestAlignmentId, cursor);
            string positionSource = snapped ? "Object snap point (computed point)" : "Cursor point (no active object snap)";
            return "ROAD MODEL HOVER — LIVE\r\n" +
                "Position source: " + positionSource + "\r\n" +
                "Inspection point Y: " + Signed(-cursor.X) + "    X: " + Signed(-cursor.Y) + "    Z: " + F(cursor.Z) + "\r\n\r\n" +
                (best ?? "No road alignment found near the cursor.") +
                surfaceReport + gradeReport +
                "\r\n\r\nRead-only inspection. Station and offset are measured from the nearest road alignment.";
        }

        private static ComboBox AddSurfaceSelector(Control parent, string caption)
        {
            Label label = new Label { Text = caption, AutoSize = true, Margin = new Padding(4, 8, 2, 2) };
            ComboBox combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 155, Margin = new Padding(2, 4, 8, 2) };
            combo.Items.Add("<Not selected>");
            combo.SelectedIndex = 0;
            foreach (SurfaceChoice choice in ReadSurfaceChoices()) combo.Items.Add(choice);
            combo.SelectedIndexChanged += (sender, args) =>
            {
                SurfaceChoice selected = combo.SelectedItem as SurfaceChoice;
                ObjectId id = selected == null ? ObjectId.Null : selected.Id;
                if (caption.StartsWith("FG", StringComparison.OrdinalIgnoreCase)) _selectedFgSurface = id;
                else if (caption.StartsWith("EG", StringComparison.OrdinalIgnoreCase)) _selectedEgSurface = id;
                else _selectedBottomSurface = id;
            };
            parent.Controls.Add(label);
            parent.Controls.Add(combo);
            return combo;
        }

        private static List<SurfaceChoice> ReadSurfaceChoices()
        {
            List<SurfaceChoice> result = new List<SurfaceChoice>();
            try
            {
                CivilDocument civil = CivilApplication.ActiveDocument;
                MethodInfo method = civil == null ? null : civil.GetType().GetMethod("GetSurfaceIds", Type.EmptyTypes);
                System.Collections.IEnumerable ids = method == null ? null : method.Invoke(civil, null) as System.Collections.IEnumerable;
                if (ids == null) return result;
                Document doc = AcApplication.DocumentManager.MdiActiveDocument;
                if (doc == null) return result;
                using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
                {
                    foreach (object item in ids)
                    {
                        if (!(item is ObjectId)) continue;
                        ObjectId id = (ObjectId)item;
                        try
                        {
                            DBObject obj = tr.GetObject(id, OpenMode.ForRead, false);
                            string name = Read(obj, "Name", "Surface " + id.Handle.ToString());
                            result.Add(new SurfaceChoice { Id = id, Name = name });
                        }
                        catch { }
                    }
                    tr.Commit();
                }
            }
            catch { }
            return result.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        private static string BuildSurfaceReport(Document doc, ObjectId fgId, ObjectId egId, ObjectId bottomId, Point3d cursor)
        {
            double? fg = ReadSurfaceElevation(doc, fgId, cursor.X, cursor.Y);
            double? eg = ReadSurfaceElevation(doc, egId, cursor.X, cursor.Y);
            double? bottom = ReadSurfaceElevation(doc, bottomId, cursor.X, cursor.Y);
            StringBuilder b = new StringBuilder();
            b.Append("\r\n\r\nSELECTED SURFACE ELEVATIONS\r\n");
            b.Append("FG elevation: ").Append(FormatNullable(fg)).Append("\r\n");
            b.Append("EG elevation: ").Append(FormatNullable(eg)).Append("\r\n");
            b.Append("Bottom elevation: ").Append(FormatNullable(bottom)).Append("\r\n");
            b.Append("FG - Bottom thickness: ").Append(FormatDifference(fg, bottom)).Append("\r\n");
            b.Append("FG - EG difference: ").Append(FormatDifference(fg, eg));
            return b.ToString();
        }

        private static Point3d GetSnapAwarePoint(InputPointContext context, out bool snapped)
        {
            snapped = false;
            Point3d raw = context.RawPoint;
            try
            {
                PropertyInfo p = context.GetType().GetProperty("ComputedPoint");
                if (p != null)
                {
                    object value = p.GetValue(context, null);
                    if (value is Point3d)
                    {
                        Point3d computed = (Point3d)value;
                        if (computed.DistanceTo(raw) > 1e-8)
                        {
                            snapped = true;
                            return computed;
                        }
                    }
                }
            }
            catch { }
            return raw;
        }

        private static ObjectId FindNearestAlignment(Document doc, List<ObjectId> ids, Point3d cursor)
        {
            ObjectId best = ObjectId.Null;
            double bestOffset = double.MaxValue;
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids)
                {
                    try
                    {
                        DBObject alignment = tr.GetObject(id, OpenMode.ForRead, false);
                        double station, offset;
                        if (TryStationOffset(alignment, cursor, out station, out offset) && Math.Abs(offset) < bestOffset)
                        { bestOffset = Math.Abs(offset); best = id; }
                    }
                    catch { }
                }
                tr.Commit();
            }
            return best;
        }

        private static string BuildGradeReport(Document doc, ObjectId alignmentId, Point3d cursor)
        {
            if (alignmentId.IsNull || _selectedFgSurface.IsNull) return "\r\nFG crossfall / longitudinal grade: select an FG surface and hover over a road.";
            double station, offset;
            Point3d left, right, before, after;
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                DBObject alignment = tr.GetObject(alignmentId, OpenMode.ForRead, false);
                if (!TryStationOffset(alignment, cursor, out station, out offset) ||
                    !TryAlignmentPoint(alignment, station, offset - 1.0, out left) ||
                    !TryAlignmentPoint(alignment, station, offset + 1.0, out right) ||
                    !TryAlignmentPoint(alignment, station - 1.0, offset, out before) ||
                    !TryAlignmentPoint(alignment, station + 1.0, offset, out after))
                { tr.Commit(); return "\r\nFG crossfall / longitudinal grade: not available at this alignment point."; }
                tr.Commit();
            }
            double? zl = ReadSurfaceElevation(doc, _selectedFgSurface, left.X, left.Y);
            double? zr = ReadSurfaceElevation(doc, _selectedFgSurface, right.X, right.Y);
            double? zb = ReadSurfaceElevation(doc, _selectedFgSurface, before.X, before.Y);
            double? za = ReadSurfaceElevation(doc, _selectedFgSurface, after.X, after.Y);
            StringBuilder b = new StringBuilder("\r\nROAD GRADES (FG SURFACE)\r\n");
            b.Append("Cross-perpendicular slope (1 m either side of cursor offset): ").Append(Slope(zl, zr, 2.0)).Append("\r\n");
            b.Append("Longitudinal slope (1 m before/after at cursor offset): ").Append(Slope(zb, za, 2.0));
            return b.ToString();
        }

        private static string Slope(double? a, double? b, double run)
        {
            return a.HasValue && b.HasValue ? (((b.Value-a.Value)/run)*100.0).ToString("+0.000;-0.000;0.000", CultureInfo.CurrentCulture) + " %" : "—";
        }

        private static bool TryAlignmentPoint(DBObject alignment, double station, double offset, out Point3d point)
        {
            point = Point3d.Origin;
            try
            {
                MethodInfo method = alignment.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                    .FirstOrDefault(m => m.Name == "PointLocation" && m.GetParameters().Length == 4);
                if (method == null) return false;
                object[] args = { station, offset, 0.0, 0.0 };
                method.Invoke(alignment, args);
                point = new Point3d(Convert.ToDouble(args[2], CultureInfo.InvariantCulture),
                    Convert.ToDouble(args[3], CultureInfo.InvariantCulture), 0);
                return true;
            }
            catch { return false; }
        }

        private static double? ReadSurfaceElevation(Document doc, ObjectId id, double x, double y)
        {
            if (id.IsNull || doc == null) return null;
            try
            {
                using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
                {
                    DBObject surface = tr.GetObject(id, OpenMode.ForRead, false);
                    MethodInfo method = surface.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                        .FirstOrDefault(m => m.Name == "FindElevationAtXY" && m.GetParameters().Length == 2);
                    if (method == null) return null;
                    object value = method.Invoke(surface, new object[] { x, y });
                    tr.Commit();
                    return value == null ? (double?)null : Convert.ToDouble(value, CultureInfo.InvariantCulture);
                }
            }
            catch { return null; }
        }

        private static string FormatNullable(double? value)
        {
            return value.HasValue ? value.Value.ToString("0.000", CultureInfo.CurrentCulture) : "—";
        }

        private static string FormatDifference(double? a, double? b)
        {
            return a.HasValue && b.HasValue ? (a.Value - b.Value).ToString("+0.000;-0.000;0.000", CultureInfo.CurrentCulture) + " m" : "—";
        }

        private static bool TryStationOffset(DBObject alignment, Point3d cursor, out double station, out double offset)
        {
            station = 0; offset = 0;
            try
            {
                MethodInfo method = alignment.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                    .FirstOrDefault(m => m.Name == "StationOffset" && m.GetParameters().Length == 4);
                if (method == null) return false;
                object[] args = { cursor.X, cursor.Y, 0.0, 0.0 };
                method.Invoke(alignment, args);
                station = Convert.ToDouble(args[2], CultureInfo.InvariantCulture);
                offset = Convert.ToDouble(args[3], CultureInfo.InvariantCulture);
                return true;
            }
            catch { return false; }
        }

        private static string FormatAlignment(DBObject alignment, double station, double offset, Point3d cursor)
        {
            List<string> lines = new List<string>
            {
                "Nearest road: " + Read(alignment, "Name", alignment.Handle.ToString()),
                "Description: " + Read(alignment, "Description", "—"),
                "Alignment type: " + alignment.GetType().Name,
                "Layer: " + (alignment is Entity ? ((Entity)alignment).Layer : "—"),
                "Station: " + station.ToString("0+000.000", CultureInfo.CurrentCulture),
                "Offset: " + offset.ToString("+0.000;-0.000;0.000", CultureInfo.CurrentCulture) + " m",
                "Start station: " + Read(alignment, "StartingStation", "—"),
                "End station: " + Read(alignment, "EndingStation", "—"),
                "Length: " + Read(alignment, "Length", "—"),
                "Cursor Z: " + F(cursor.Z),
                "Assigned assembly at this station: " + FindAssemblyForAlignment(alignment.ObjectId, station)
            };
            return string.Join("\r\n", lines);
        }

        private static string FindAssemblyForAlignment(ObjectId alignmentId, double station)
        {
            try
            {
                CivilDocument civil = CivilApplication.ActiveDocument;
                MethodInfo getter = civil == null ? null : civil.GetType().GetMethod("GetCorridorIds", Type.EmptyTypes);
                System.Collections.IEnumerable corridorIds = getter == null ? null : getter.Invoke(civil, null) as System.Collections.IEnumerable;
                Document doc = AcApplication.DocumentManager.MdiActiveDocument;
                if (corridorIds == null || doc == null) return "Not found";
                using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
                {
                    foreach (object item in corridorIds)
                    {
                        if (!(item is ObjectId)) continue;
                        DBObject corridor = tr.GetObject((ObjectId)item, OpenMode.ForRead, false);
                        object baselines = GetProperty(corridor, "Baselines");
                        if (!(baselines is System.Collections.IEnumerable)) continue;
                        foreach (object baseline in (System.Collections.IEnumerable)baselines)
                        {
                            object baseAlignment = GetProperty(baseline, "AlignmentId");
                            if (!(baseAlignment is ObjectId) || (ObjectId)baseAlignment != alignmentId) continue;
                            object regions = GetProperty(baseline, "BaselineRegions");
                            if (!(regions is System.Collections.IEnumerable)) continue;
                            foreach (object region in (System.Collections.IEnumerable)regions)
                            {
                                double regionStart = ReadDoubleProperty(region, "StartStation", double.NegativeInfinity);
                                double regionEnd = ReadDoubleProperty(region, "EndStation", double.PositiveInfinity);
                                if (station < Math.Min(regionStart, regionEnd) - 1e-6 ||
                                    station > Math.Max(regionStart, regionEnd) + 1e-6) continue;
                                object assemblyId = GetProperty(region, "AssemblyId");
                                if (assemblyId is ObjectId && !((ObjectId)assemblyId).IsNull)
                                {
                                    DBObject assembly = tr.GetObject((ObjectId)assemblyId, OpenMode.ForRead, false);
                                    string assemblyName = Read(assembly, "Name", null);
                                    if (!string.IsNullOrEmpty(assemblyName)) { tr.Commit(); return assemblyName; }
                                }
                            }
                        }
                    }
                    tr.Commit();
                }
            }
            catch { }
            return "Not found";
        }

        private static double ReadDoubleProperty(object obj, string name, double fallback)
        {
            try
            {
                object value = GetProperty(obj, name);
                return value == null ? fallback : Convert.ToDouble(value, CultureInfo.InvariantCulture);
            }
            catch { return fallback; }
        }

        private static object GetProperty(object obj, string name)
        {
            try { PropertyInfo p = obj == null ? null : obj.GetType().GetProperty(name); return p == null ? null : p.GetValue(obj, null); }
            catch { return null; }
        }

        private static string Read(object obj, string name, string fallback)
        {
            try
            {
                PropertyInfo p = obj.GetType().GetProperty(name);
                object value = p == null ? null : p.GetValue(obj, null);
                return value == null ? fallback : Convert.ToString(value, CultureInfo.CurrentCulture);
            }
            catch { return fallback; }
        }
        private static string Signed(double value)
        {
            return value.ToString("+0.000;-0.000;0.000", CultureInfo.CurrentCulture);
        }

        private static string F(double value) { return value.ToString("0.000", CultureInfo.CurrentCulture); }
    }
}

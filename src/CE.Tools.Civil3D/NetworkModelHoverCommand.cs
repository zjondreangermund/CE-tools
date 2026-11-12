using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.NetworkModelHoverCommand))]

namespace CETools.Civil3D
{
    /// <summary>Read-only live inspector for gravity sewer and pressure/water networks.</summary>
    public sealed class NetworkModelHoverCommand
    {
        private static Document _document;
        private static Form _window;
        private static Label _display;
        private static ComboBox _egSurfaceCombo;
        private static ObjectId _egSurfaceId = ObjectId.Null;
        private static List<ObjectId> _networkIds;
        private static Point3d _lastPoint;
        private static bool _hasLastPoint;
        private static readonly object Sync = new object();

        [CommandMethod("CE_TOOLS", "CE_NETWORKMODELHOVER", CommandFlags.Modal)]
        public void Start()
        {
            Document doc = AcApplication.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            List<ObjectId> ids = ReadNetworkIds();
            if (ids.Count == 0)
            {
                doc.Editor.WriteMessage("\nCE_NETWORKMODELHOVER: no sewer/water gravity or pressure networks found.");
                return;
            }

            Form form = new HoverInspectorForm();
            form.Text = "CE Tools - Sewer / Water Model Hover (LIVE)";
            form.Width = 620; form.Height = 455;
            form.StartPosition = FormStartPosition.Manual;
            form.TopMost = false;

            Panel surfacePanel = new Panel { Dock = DockStyle.Top, Height = 42, Padding = new Padding(10, 8, 10, 4) };
            Label surfaceLabel = new Label { Text = "EG surface:", AutoSize = true, Left = 8, Top = 12 };
            _egSurfaceCombo = new ComboBox { Left = 82, Top = 7, Width = 300, DropDownStyle = ComboBoxStyle.DropDownList };
            _egSurfaceCombo.Items.Add(new SurfaceChoice(ObjectId.Null, "<Select EG surface>"));
            foreach (SurfaceChoice choice in ReadSurfaceChoices(doc))
                _egSurfaceCombo.Items.Add(choice);
            _egSurfaceCombo.SelectedIndex = 0;
            _egSurfaceId = ObjectId.Null;
            _egSurfaceCombo.SelectedIndexChanged += (sender, args) =>
            {
                SurfaceChoice selected = _egSurfaceCombo.SelectedItem as SurfaceChoice;
                lock (Sync) _egSurfaceId = selected == null ? ObjectId.Null : selected.Id;
            };
            surfacePanel.Controls.Add(surfaceLabel);
            surfacePanel.Controls.Add(_egSurfaceCombo);

            _display = new Label { Dock = DockStyle.Fill, Padding = new Padding(12), Font = new System.Drawing.Font("Segoe UI", 10), AutoSize = false };
            _display.Text = "Move the cursor over a sewer or water pipe / structure.\r\nLive data will appear here.";
            form.Controls.Add(_display);
            form.Controls.Add(surfacePanel);

            lock (Sync)
            {
                _document = doc; _window = form; _networkIds = ids;
                _hasLastPoint = false;
                doc.Editor.PointMonitor += OnPointMonitor;
            }
            try
            {
                Autodesk.AutoCAD.ApplicationServices.Application.ShowModelessDialog(form);
                // PointMonitor is raised during point acquisition, not GetString.
                // Keep GetPoint active so cursor movement continuously updates the live inspector.
                while (true)
                {
                    PromptPointOptions pointOptions = new PromptPointOptions(
                        "\nMove cursor over sewer/water network. Click to continue tracking; press Enter or Esc to finish: ");
                    pointOptions.AllowNone = true;
                    PromptPointResult pointResult = doc.Editor.GetPoint(pointOptions);
                    if (pointResult.Status != PromptStatus.OK)
                        break;
                }
            }
            finally
            {
                lock (Sync)
                {
                    doc.Editor.PointMonitor -= OnPointMonitor;
                    if (_window != null && !_window.IsDisposed) _window.Close();
                    _window = null; _display = null; _egSurfaceCombo = null; _egSurfaceId = ObjectId.Null;
                _document = null; _networkIds = null;
                }
            }
        }

        private static List<ObjectId> ReadNetworkIds()
        {
            var ids = new List<ObjectId>();
            CivilDocument civil = CivilApplication.ActiveDocument;
            if (civil == null) return ids;
            foreach (string methodName in new[] { "GetPipeNetworkIds", "GetPressurePipeNetworkIds" })
            {
                try
                {
                    MethodInfo method = civil.GetType().GetMethod(methodName, Type.EmptyTypes);
                    var result = method == null ? null : method.Invoke(civil, null) as System.Collections.IEnumerable;
                    if (result != null)
                        foreach (object item in result) if (item is ObjectId && !((ObjectId)item).IsNull) ids.Add((ObjectId)item);
                }
                catch { }
            }
            return ids.Distinct().ToList();
        }

        private sealed class SurfaceChoice
        {
            public ObjectId Id;
            public string Name;
            public SurfaceChoice(ObjectId id, string name) { Id = id; Name = name; }
            public override string ToString() { return Name; }
        }

        private static List<SurfaceChoice> ReadSurfaceChoices(Document doc)
        {
            List<SurfaceChoice> choices = new List<SurfaceChoice>();
            try
            {
                CivilDocument civil = CivilApplication.ActiveDocument;
                if (civil == null) return choices;
                using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId id in civil.GetSurfaceIds())
                    {
                        try
                        {
                            DBObject obj = tr.GetObject(id, OpenMode.ForRead, false);
                            CivilSurface surface = obj as CivilSurface;
                            if (surface != null)
                                choices.Add(new SurfaceChoice(id, Read(obj, "Name", id.Handle.ToString())));
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return choices.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static void OnPointMonitor(object sender, PointMonitorEventArgs e)
        {
            Document doc; Form form; List<ObjectId> networks;
            lock (Sync) { doc = _document; form = _window; networks = _networkIds; }
            if (doc == null || form == null || form.IsDisposed || networks == null) return;
            Point3d p = e.Context.RawPoint;
            lock (Sync)
            {
                if (_hasLastPoint && _lastPoint.DistanceTo(p) < 0.01) return;
                _lastPoint = p; _hasLastPoint = true;
            }

            ObjectId pickedId = ObjectId.Null;
            try
            {
                FullSubentityPath[] paths = e.Context.GetPickedEntities();
                if (paths != null && paths.Length > 0)
                {
                    ObjectId[] picked = paths[0].GetObjectIds();
                    if (picked != null && picked.Length > 0) pickedId = picked[picked.Length - 1];
                }
            }
            catch { }

            string report = BuildReport(doc, networks, p, pickedId);
            ((HoverInspectorForm)form).UpdateReport(_display, report);
        }

        private static string BuildReport(Document doc, List<ObjectId> networkIds, Point3d cursor, ObjectId pickedId)
        {
            double bestDistance = 3.0; // plan distance in drawing units; adjustable in a later settings dialog
            string best = null;
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId networkId in networkIds)
                {
                    DBObject network;
                    try { network = tr.GetObject(networkId, OpenMode.ForRead, false); } catch { continue; }
                    foreach (string memberMethod in new[] { "GetPipeIds", "GetStructureIds" })
                    {
                        try
                        {
                            MethodInfo getter = network.GetType().GetMethod(memberMethod, Type.EmptyTypes);
                            var members = getter == null ? null : getter.Invoke(network, null) as System.Collections.IEnumerable;
                            if (members == null) continue;
                            foreach (object rawId in members)
                            {
                                if (!(rawId is ObjectId)) continue;
                                DBObject part;
                                try { part = tr.GetObject((ObjectId)rawId, OpenMode.ForRead, false); } catch { continue; }
                                Point3d a, b;
                                bool pipe = memberMethod == "GetPipeIds";
                                if (pipe)
                                {
                                    if (!TryPoint(part, "StartPoint", out a) || !TryPoint(part, "EndPoint", out b)) continue;
                                }
                                else
                                {
                                    if (!TryPoint(part, "Position", out a)) continue;
                                    b = a;
                                }
                                double distance = pipe ? DistanceToSegmentXY(cursor, a, b) : DistanceXY(cursor, a);
                                bool isPicked = !pickedId.IsNull && (ObjectId)rawId == pickedId;
                                bool currentIsPicked = best != null && best.StartsWith("PICKED|", StringComparison.Ordinal);
                                bool currentIsStructure = best != null && best.StartsWith("STRUCTURE|", StringComparison.Ordinal);
                                if (isPicked || (!currentIsPicked &&
                                    ((!pipe && distance <= Math.Max(bestDistance, 1.0) &&
                                      (!currentIsStructure || distance < bestDistance - 0.000001)) ||
                                     (pipe && !currentIsStructure && distance < bestDistance - 0.000001))))
                                {
                                    bestDistance = distance;
                                    string details = FormatPart(doc, tr, part, pipe, network, a, b, distance, cursor);
                                    best = (isPicked ? "PICKED|" : (!pipe ? "STRUCTURE|" : "PIPE|")) + details;
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            return "SEWER / WATER MODEL HOVER — LIVE\r\n" +
                "Cursor Y: " + Signed(-cursor.X) + "    X: " + Signed(-cursor.Y) + "\r\n\r\n" +
                (best == null ? "No network component within 3 drawing units.\r\nMove closer to a pipe or structure." : best.Substring(best.IndexOf('|') + 1)) +
                "\r\n\r\nUpdates automatically as the cursor moves. Read-only inspection.";
        }

        private static string FormatPart(Document doc, Transaction tr, DBObject part, bool pipe, DBObject network, Point3d a, Point3d b, double distance, Point3d cursor)
        {
            string kind = pipe ? "PIPE" : "STRUCTURE / MANHOLE";
            var lines = new List<string>
            {
                "Nearest component: " + kind,
                "Network: " + Read(part, "NetworkName", Read(network, "Name", "<network>")),
                "Name / ID: " + Read(part, "Name", part.Handle.ToString()),
                "Description: " + Read(part, "Description", Read(part, "RawDescription", "—")),
                "Layer: " + (part is Entity ? ((Entity)part).Layer : "—"),
                "Plan distance from cursor: " + F(distance) + " m"
            };
            if (pipe)
            {
                lines.Add("Pipe size: " + FormatNominalPipeSize(ReadDouble(part, "NominalDiameter", "InnerDiameterOrWidth", "Diameter")));
                lines.Add("Material / type: " + ReadAny(part, "Material", "PartFamilyName", "PartType", "—"));
                lines.Add("Length: " + ReadAny(part, "Length3DCenterToCenter", "Length2DCenterToCenter", "Length", "—"));
                lines.Add("Start invert / Z: " + F(a.Z));
                lines.Add("End invert / Z: " + F(b.Z));

                Point3d pipePoint;
                double pipeParameter;
                ProjectPointToPipeXY(cursor, a, b, out pipePoint, out pipeParameter);
                lines.Add("Pipe point along length: " + (pipeParameter * 100.0).ToString("0.0", CultureInfo.CurrentCulture) + " %");
                lines.Add("Invert elevation at cursor point: " + F(pipePoint.Z));
                ObjectId egSurfaceId;
                lock (Sync) egSurfaceId = _egSurfaceId;
                if (egSurfaceId.IsNull)
                {
                    lines.Add("EG elevation at cursor point: Select an EG surface above");
                    lines.Add("Depth below EG at cursor point: —");
                }
                else
                {
                    double egElevation;
                    if (TrySurfaceElevation(tr, egSurfaceId, pipePoint.X, pipePoint.Y, out egElevation))
                    {
                        lines.Add("EG elevation at cursor point: " + F(egElevation));
                        lines.Add("Depth below EG at cursor point: " + F(egElevation - pipePoint.Z) + " m");
                    }
                    else
                    {
                        lines.Add("EG elevation at cursor point: Outside selected surface / unavailable");
                        lines.Add("Depth below EG at cursor point: —");
                    }
                }

                double horizontal = Math.Sqrt(Math.Pow(b.X-a.X,2)+Math.Pow(b.Y-a.Y,2));
                if (horizontal > 0.000001) lines.Add("Approx. grade: " + (((b.Z-a.Z)/horizontal)*100.0).ToString("+0.000;-0.000;0.000", CultureInfo.CurrentCulture) + " %");
                lines.Add("Upstream structure: " + ResolveStructureLabel(doc, tr, part, "StartStructureId"));
                lines.Add("Downstream structure: " + ResolveStructureLabel(doc, tr, part, "EndStructureId"));
            }
            else
            {
                lines.Add("Cover / rim level: " + ReadAny(part, "RimElevation", "CoverElevation", "Elevation", "—"));
                lines.Add("Sump / invert level: " + ReadAny(part, "SumpElevation", "LowestElevation", "InvertElevation", "—"));
                string rim = ReadAny(part, "RimElevation", "CoverElevation", "Elevation", null);
                string sump = ReadAny(part, "SumpElevation", "LowestElevation", "InvertElevation", null);
                string depth = ReadAny(part, "Depth", "StructureDepth", "PartSizeDepth", null);
                if (string.IsNullOrWhiteSpace(depth))
                {
                    double rimValue, sumpValue;
                    if (TryNumber(rim, out rimValue) && TryNumber(sump, out sumpValue))
                        depth = F(Math.Abs(rimValue - sumpValue)) + " m (calculated: rim - sump)";
                }
                lines.Add("Depth: " + (string.IsNullOrWhiteSpace(depth) ? "Not available from part properties" : depth));
                List<ConnectedPipeInfo> connected = FindConnectedPipes(doc, tr, part);
                lines.Add("Connected pipes and actual sizes: " + (connected.Count == 0 ? "Not found" :
                    string.Join(", ", connected.Select(x => x.Name + " (" + FormatNominalPipeSize(x.Diameter) + ")").Distinct().ToArray())));
                lines.Add("Junction type: " + (connected.Count >= 3 ? "T-junction / 3+ connected pipes" :
                    connected.Count == 2 ? "Two-way junction" : connected.Count == 1 ? "End structure" : "Not determined"));
                string sizeTransition = DescribePipeSizeTransition(connected);
                if (!string.IsNullOrEmpty(sizeTransition)) lines.Add("Pipe size change at junction: " + sizeTransition);
                string bend = GetWaterBendType(connected);
                if (!string.IsNullOrEmpty(bend)) lines.Add("Water bend type: " + bend);
                lines.Add("Position Z: " + F(a.Z));
            }
            return string.Join("\r\n", lines);
        }

        private static void ProjectPointToPipeXY(Point3d cursor, Point3d start, Point3d end,
            out Point3d projected, out double parameter)
        {
            double dx = end.X - start.X;
            double dy = end.Y - start.Y;
            double lengthSquared = dx * dx + dy * dy;
            parameter = lengthSquared < 1e-12 ? 0.0 :
                ((cursor.X - start.X) * dx + (cursor.Y - start.Y) * dy) / lengthSquared;
            parameter = Math.Max(0.0, Math.Min(1.0, parameter));
            projected = new Point3d(start.X + parameter * dx,
                start.Y + parameter * dy, start.Z + parameter * (end.Z - start.Z));
        }

        private static bool TrySurfaceElevation(Transaction tr, ObjectId surfaceId,
            double x, double y, out double elevation)
        {
            elevation = 0.0;
            try
            {
                DBObject obj = tr.GetObject(surfaceId, OpenMode.ForRead, false);
                CivilSurface surface = obj as CivilSurface;
                if (surface == null) return false;
                MethodInfo method = surface.GetType().GetMethod("FindElevationAtXY",
                    BindingFlags.Public | BindingFlags.Instance, null,
                    new[] { typeof(double), typeof(double) }, null);
                if (method == null) return false;
                object value = method.Invoke(surface, new object[] { x, y });
                if (value == null) return false;
                elevation = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                return !double.IsNaN(elevation) && !double.IsInfinity(elevation);
            }
            catch { return false; }
        }

        private static string ResolveStructureLabel(Document doc, Transaction tr, DBObject pipe, string propertyName)
        {
            try
            {
                PropertyInfo p = pipe.GetType().GetProperty(propertyName);
                object value = p == null ? null : p.GetValue(pipe, null);
                if (value is ObjectId)
                {
                    ObjectId id = (ObjectId)value;
                    if (id.IsNull || !id.IsValid) return "—";
                    DBObject structure = tr.GetObject(id, OpenMode.ForRead, false);
                    string name = Read(structure, "Name", null);
                    string desc = Read(structure, "Description", null);
                    return !string.IsNullOrWhiteSpace(name) ? name + " [" + id.Handle + "]" :
                        (!string.IsNullOrWhiteSpace(desc) ? desc + " [" + id.Handle + "]" : id.Handle.ToString());
                }
                if (value != null) return Convert.ToString(value, CultureInfo.CurrentCulture);
            }
            catch { }
            return "—";
        }

        private sealed class ConnectedPipeInfo
        {
            public string Name;
            public Point3d OtherEnd;
            public double Diameter;
        }

        private static List<ConnectedPipeInfo> FindConnectedPipes(Document doc, Transaction tr, DBObject structure)
        {
            List<ConnectedPipeInfo> result = new List<ConnectedPipeInfo>();
            Point3d structurePosition;
            if (!TryPoint(structure, "Position", out structurePosition)) structurePosition = Point3d.Origin;
            try
            {
                ObjectId structureId = structure.ObjectId;
                foreach (ObjectId networkId in ReadNetworkIds())
                {
                    DBObject network;
                    try { network = tr.GetObject(networkId, OpenMode.ForRead, false); } catch { continue; }
                    MethodInfo getter = network.GetType().GetMethod("GetPipeIds", Type.EmptyTypes);
                    var ids = getter == null ? null : getter.Invoke(network, null) as System.Collections.IEnumerable;
                    if (ids == null) continue;
                    foreach (object item in ids)
                    {
                        if (!(item is ObjectId)) continue;
                        ObjectId pipeId = (ObjectId)item;
                        DBObject pipe;
                        try { pipe = tr.GetObject(pipeId, OpenMode.ForRead, false); } catch { continue; }
                        ObjectId startId = ReadObjectId(pipe, "StartStructureId");
                        ObjectId endId = ReadObjectId(pipe, "EndStructureId");
                        bool atStart = !startId.IsNull && startId == structureId;
                        bool atEnd = !endId.IsNull && endId == structureId;
                        if (!atStart && !atEnd) continue;
                        Point3d start, finish;
                        if (!TryPoint(pipe, "StartPoint", out start) || !TryPoint(pipe, "EndPoint", out finish)) continue;
                        result.Add(new ConnectedPipeInfo {
                            Name = Read(pipe, "Name", pipe.Handle.ToString()),
                            Diameter = ReadDouble(pipe, "InnerDiameterOrWidth", "NominalDiameter", "Diameter"),
                            OtherEnd = atStart
                                ? new Point3d(finish.X - structurePosition.X, finish.Y - structurePosition.Y, finish.Z - structurePosition.Z)
                                : new Point3d(start.X - structurePosition.X, start.Y - structurePosition.Y, start.Z - structurePosition.Z)
                        });
                    }
                }
            }
            catch { }
            return result;
        }

        private static double ReadDouble(object obj, params string[] names)
        {
            foreach (string name in names)
            {
                try
                {
                    PropertyInfo p = obj.GetType().GetProperty(name);
                    if (p == null) continue;
                    object value = p.GetValue(obj, null);
                    if (value == null) continue;
                    double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                    if (number > 0 && !double.IsNaN(number) && !double.IsInfinity(number)) return number;
                }
                catch { }
            }
            return 0;
        }

        private static string DescribePipeSizeTransition(List<ConnectedPipeInfo> pipes)
        {
            if (pipes == null || pipes.Count < 2) return null;
            List<double> sizes = pipes.Select(p => p.Diameter)
                .Where(v => v > 0 && !double.IsNaN(v) && !double.IsInfinity(v))
                .OrderBy(v => v).ToList();
            if (sizes.Count < 2) return "Size data unavailable for all connected pipes";
            double min = sizes.First(), max = sizes.Last();
            if (Math.Abs(max - min) <= Math.Max(0.0001, max * 0.001)) return "No reducing size detected";
            List<ConnectedPipeInfo> ordered = pipes
                .Where(p => p.Diameter > 0)
                .OrderByDescending(p => p.Diameter).ToList();
            string sequence = string.Join(" → ", ordered.Select(p =>
                p.Name + " " + FormatNominalPipeSize(p.Diameter)).ToArray());
            return sequence + " (largest-to-smallest size sequence; flow direction not inferred)";
        }

        private static string FormatNominalPipeSize(double diameter)
        {
            if (diameter <= 0 || double.IsNaN(diameter) || double.IsInfinity(diameter))
                return "DN unavailable";

            // Civil 3D part properties may expose an internal diameter rather than
            // the catalog nominal size. Map that measured value upward to the
            // corresponding standard metric nominal size (e.g. 82 -> DN90; 69 -> DN75).
            double mm = diameter <= 2.0 ? diameter * 1000.0 : diameter;
            double[] nominalSizes =
            {
                20, 25, 32, 40, 50, 63, 75, 90, 110, 125, 160, 200,
                225, 250, 280, 315, 355, 400, 450, 500, 560, 630,
                710, 800, 900, 1000, 1200, 1400, 1600
            };
            foreach (double nominal in nominalSizes)
                if (mm <= nominal + 0.5)
                    return "DN" + nominal.ToString("0", CultureInfo.InvariantCulture);

            return "DN" + Math.Ceiling(mm / 100.0 * 100.0).ToString("0", CultureInfo.InvariantCulture);
        }

        private static string Signed(double value)
        {
            return value.ToString("+0.000;-0.000;0.000", CultureInfo.CurrentCulture);
        }

        private static string GetWaterBendType(List<ConnectedPipeInfo> pipes)
        {
            if (pipes == null || pipes.Count < 2) return null;
            double bestAngle = 0.0;
            for (int i = 0; i < pipes.Count; i++)
                for (int j = i + 1; j < pipes.Count; j++)
                {
                    double ax = pipes[i].OtherEnd.X, ay = pipes[i].OtherEnd.Y;
                    double bx = pipes[j].OtherEnd.X, by = pipes[j].OtherEnd.Y;
                    double la = Math.Sqrt(ax * ax + ay * ay), lb = Math.Sqrt(bx * bx + by * by);
                    if (la < 1e-9 || lb < 1e-9) continue;
                    double dot = (ax * bx + ay * by) / (la * lb);
                    double angle = Math.Acos(Math.Max(-1.0, Math.Min(1.0, dot))) * 180.0 / Math.PI;
                    double bend = 180.0 - angle;
                    if (bend > bestAngle) bestAngle = bend;
                }
            double[] standard = { 11.25, 22.5, 45.0, 90.0 };
            if (bestAngle < 1.0) return "Straight / through";
            double nearest = standard.OrderBy(x => Math.Abs(x - bestAngle)).First();
            return Math.Abs(nearest - bestAngle) <= 5.0
                ? nearest.ToString("0.##", CultureInfo.InvariantCulture) + "° bend (estimated from connected pipe directions)"
                : bestAngle.ToString("0.0", CultureInfo.InvariantCulture) + "° bend (estimated from connected pipe directions)";
        }

        private static ObjectId ReadObjectId(DBObject obj, string propertyName)
        {
            try
            {
                PropertyInfo p = obj.GetType().GetProperty(propertyName);
                object value = p == null ? null : p.GetValue(obj, null);
                if (value is ObjectId) return (ObjectId)value;
            }
            catch { }
            return ObjectId.Null;
        }

        private static bool TryNumber(string value, out double number)
        {
            return double.TryParse(value, NumberStyles.Any, CultureInfo.CurrentCulture, out number) ||
                   double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out number);
        }

        private static string ReadAny(DBObject obj, string a, string b, string c, string fallback)
        { string v = Read(obj,a,null); if (v == null) v=Read(obj,b,null); if (v == null) v=Read(obj,c,null); return v ?? fallback; }
        private static string Read(DBObject obj, string name, string fallback)
        { try { PropertyInfo p=obj.GetType().GetProperty(name); object v=p==null?null:p.GetValue(obj,null); return v==null?fallback:Convert.ToString(v,CultureInfo.CurrentCulture); } catch { return fallback; } }
        private static bool TryPoint(DBObject obj, string name, out Point3d point)
        { try { PropertyInfo p=obj.GetType().GetProperty(name); object v=p==null?null:p.GetValue(obj,null); if(v is Point3d){point=(Point3d)v;return true;} } catch{} point=Point3d.Origin; return false; }
        private static double DistanceXY(Point3d p, Point3d q) { return Math.Sqrt(Math.Pow(p.X-q.X,2)+Math.Pow(p.Y-q.Y,2)); }
        private static double DistanceToSegmentXY(Point3d p, Point3d a, Point3d b)
        {
            double dx=b.X-a.X, dy=b.Y-a.Y, d=dx*dx+dy*dy;
            if(d<1e-12) return DistanceXY(p,a);
            double t=((p.X-a.X)*dx+(p.Y-a.Y)*dy)/d; t=Math.Max(0,Math.Min(1,t));
            return DistanceXY(p,new Point3d(a.X+t*dx,a.Y+t*dy,a.Z+t*(b.Z-a.Z)));
        }
        private static string F(double v) { return v.ToString("0.000",CultureInfo.CurrentCulture); }
    }
}

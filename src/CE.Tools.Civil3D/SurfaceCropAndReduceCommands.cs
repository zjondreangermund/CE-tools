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
using Autodesk.Civil.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

[assembly: CommandClass(typeof(CETools.Civil3D.SurfaceCropAndReduceCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Creates a separate, reduced TIN from regularly sampled elevations inside a
    /// closed polyline boundary. The source surface is never modified.
    /// This is an approximation workflow; users control sample spacing.
    /// </summary>
    public sealed class SurfaceCropAndReduceCommands
    {
        [CommandMethod("CE_TOOLS", "CE_SURFACECROPANDREDUCE", CommandFlags.Modal)]
        public void CropAndReduce()
        {
            Document doc = AcApplication.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            List<SurfaceChoice> choices = ReadSurfaces(doc);
            if (choices.Count == 0)
            {
                doc.Editor.WriteMessage("\nCE Tools: no TIN surfaces found.");
                return;
            }

            string outputName;
            double spacing;
            ObjectId sourceId;
            using (var dialog = new CropSettingsDialog(choices))
            {
                if (Autodesk.AutoCAD.ApplicationServices.Application.ShowModalDialog(dialog) != DialogResult.OK) return;
                sourceId = dialog.SelectedSurfaceId;
                outputName = dialog.OutputName;
                spacing = dialog.SampleSpacing;
            }

            PromptEntityOptions boundaryOptions =
                new PromptEntityOptions("\nSelect a closed polyline crop boundary: ");
            boundaryOptions.SetRejectMessage("\nSelect a closed lightweight 2D polyline.");
            boundaryOptions.AddAllowedClass(typeof(Polyline), true);
            PromptEntityResult boundaryResult = doc.Editor.GetEntity(boundaryOptions);
            if (boundaryResult.Status != PromptStatus.OK) return;

            int added = 0, skipped = 0;
            ObjectId outputId = ObjectId.Null;
            string stage = "starting crop operation";
            try
            {
                using (doc.LockDocument())
                using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
                {
                    stage = "opening selected source surface and crop boundary";
                    CivilSurface source = tr.GetObject(sourceId, OpenMode.ForRead, false) as CivilSurface;
                    Polyline boundary = tr.GetObject(boundaryResult.ObjectId, OpenMode.ForRead, false) as Polyline;
                    if (source == null) throw new InvalidOperationException("Selected source is not a supported Civil 3D surface.");
                    if (boundary == null || !boundary.Closed || boundary.NumberOfVertices < 3)
                        throw new InvalidOperationException("The crop boundary must be a closed polyline.");

                    for (int i = 0; i < boundary.NumberOfVertices; i++)
                        if (Math.Abs(boundary.GetBulgeAt(i)) > 1e-12)
                            throw new InvalidOperationException("Use a straight-segment boundary. Curved edges require tessellation before sampling.");
                    stage = "reading crop boundary extents";
                    Extents3d ext = boundary.GeometricExtents;
                    double minX = ext.MinPoint.X, maxX = ext.MaxPoint.X;
                    double minY = ext.MinPoint.Y, maxY = ext.MaxPoint.Y;
                    var points = new List<Point3d>();

                    // Include all boundary vertices so the cropped TIN respects the
                    // selected outline more closely than a grid-only sample.
                    for (int i = 0; i < boundary.NumberOfVertices; i++)
                    {
                        Point3d v = boundary.GetPoint3dAt(i);
                        TryAddSample(source, points, v.X, v.Y, ref skipped);
                    }

                    double columns = Math.Max(1, Math.Ceiling((maxX - minX) / spacing));
                    double rows = Math.Max(1, Math.Ceiling((maxY - minY) / spacing));
                    // Validate before integer casts: extreme extents must not overflow the cap.
                    if (double.IsNaN(columns) || double.IsNaN(rows) ||
                        double.IsInfinity(columns) || double.IsInfinity(rows) ||
                        (columns + 1) * (rows + 1) > 2000000)
                        throw new InvalidOperationException("The selected spacing creates more than 2,000,000 grid checks. Increase sample spacing.");
                    int nx = (int)columns, ny = (int)rows;

                    stage = "sampling elevations inside crop boundary";
                    for (int ix = 0; ix <= nx; ix++)
                    {
                        double x = minX + (maxX - minX) * ix / nx;
                        for (int iy = 0; iy <= ny; iy++)
                        {
                            double y = minY + (maxY - minY) * iy / ny;
                            if (!Inside(boundary, x, y)) continue;
                            int before = points.Count;
                            TryAddSample(source, points, x, y, ref skipped);
                            if (points.Count > before) added++;
                        }
                    }

                    if (points.Count < 3)
                        throw new InvalidOperationException("Fewer than three valid source elevations were found inside the boundary.");

                    stage = "creating output TIN surface";
                    outputId = source.StyleId.IsNull
                        ? TinSurface.Create(doc.Database, outputName)
                        : TinSurface.Create(outputName, source.StyleId);

                    TinSurface output = tr.GetObject(outputId, OpenMode.ForWrite, false) as TinSurface;
                    if (output == null) throw new InvalidOperationException("Civil 3D did not create the output TIN surface.");

                    stage = "adding sampled points to output TIN";
                    AddPointCollection(output, points);
                    stage = "applying crop boundary to output TIN";
                    AddOuterBoundary(output, boundaryResult.ObjectId);
                    stage = "rebuilding output TIN";
                    output.Rebuild();
                    tr.Commit();
                }

                doc.Editor.WriteMessage(
                    "\nCE_SURFACECROPANDREDUCE complete. Created '{0}' with approximately {1:N0} sampled points. " +
                    "Sample spacing: {2:N3} drawing units. Invalid/outside samples skipped: {3}. " +
                    "The original surface was not modified.",
                    outputName, added, spacing, skipped);
            }
            catch (System.Exception ex)
            {
                doc.Editor.WriteMessage("\nCE_SURFACECROPANDREDUCE failed while {0}: {1}", stage, ex.ToString());
            }
        }

        private static List<SurfaceChoice> ReadSurfaces(Document doc)
        {
            var list = new List<SurfaceChoice>();
            CivilDocument civil = CivilApplication.ActiveDocument;
            if (civil == null) return list;
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in civil.GetSurfaceIds())
                {
                    CivilSurface surface = tr.GetObject(id, OpenMode.ForRead, false) as CivilSurface;
                    if (surface != null && surface is TinSurface)
                        list.Add(new SurfaceChoice(id, surface.Name));
                }
            }
            return list.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static void TryAddSample(CivilSurface source, List<Point3d> points,
            double x, double y, ref int skipped)
        {
            try
            {
                double z = source.FindElevationAtXY(x, y);
                if (double.IsNaN(z) || double.IsInfinity(z)) { skipped++; return; }
                if (!points.Any(p => Math.Abs(p.X - x) < 1e-7 && Math.Abs(p.Y - y) < 1e-7))
                    points.Add(new Point3d(x, y, z));
            }
            catch { skipped++; }
        }

        private static bool Inside(Polyline poly, double x, double y)
        {
            bool inside = false;
            int count = poly.NumberOfVertices;
            for (int i = 0, j = count - 1; i < count; j = i++)
            {
                Point2d a = poly.GetPoint2dAt(i), b = poly.GetPoint2dAt(j);
                bool crosses = ((a.Y > y) != (b.Y > y)) &&
                    (x < (b.X - a.X) * (y - a.Y) / ((b.Y - a.Y) == 0 ? 1e-30 : (b.Y - a.Y)) + a.X);
                if (crosses) inside = !inside;
            }
            return inside;
        }


        private static object GetRequiredPropertyValue(object instance, string propertyName, string description)
        {
            if (instance == null)
                throw new ArgumentNullException("instance", "Cannot read " + description + " from a null object.");

            PropertyInfo property = instance.GetType().GetProperty(
                propertyName, BindingFlags.Public | BindingFlags.Instance);

            if (property == null)
                throw new MissingMemberException(
                    instance.GetType().FullName + " does not expose the required " +
                    propertyName + " property for " + description + ".");

            return property.GetValue(instance, null);
        }

        private static void AddPointCollection(DBObject surface, IList<Point3d> points)
        {
            if (surface == null) throw new ArgumentNullException("surface");
            TinSurface tin = surface as TinSurface;
            if (tin == null) throw new InvalidOperationException("Output object is not a TIN surface.");

            Point3dCollection collection = new Point3dCollection(points.ToArray());
            MethodInfo addVertices = tin.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "AddVertices" &&
                    m.GetParameters().Length == 1 &&
                    m.GetParameters()[0].ParameterType.IsAssignableFrom(typeof(Point3dCollection)));
            if (addVertices != null)
            {
                addVertices.Invoke(tin, new object[] { collection });
                return;
            }

            // Some Civil 3D releases expose point insertion as AddPoints rather than AddVertices.
            MethodInfo addPoints = tin.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => (m.Name == "AddPoints" || m.Name == "AddPointCollection") &&
                    m.GetParameters().Length == 1 &&
                    m.GetParameters()[0].ParameterType.IsAssignableFrom(typeof(Point3dCollection)));
            if (addPoints != null)
            {
                addPoints.Invoke(tin, new object[] { collection });
                return;
            }

            throw new MissingMethodException("This Civil 3D TinSurface version exposes neither AddVertices nor a compatible AddPoints method.");
        }

        private static void AddOuterBoundary(DBObject surface, ObjectId boundaryId)
        {
            object definition = GetRequiredPropertyValue(surface, "BoundariesDefinition", "surface boundary definition");
            if (definition == null) throw new InvalidOperationException("Surface BoundariesDefinition returned null.");
            foreach (MethodInfo method in definition.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.Name == "AddBoundaries"))
            {
                ParameterInfo[] p = method.GetParameters();
                if (p.Length != 4) continue;
                if (p[0].ParameterType.IsAssignableFrom(typeof(ObjectIdCollection)))
                {
                    method.Invoke(definition, new object[] {
                        new ObjectIdCollection(new[] { boundaryId }), 0.01,
                        Enum.Parse(p[2].ParameterType, "Outer"), true });
                    return;
                }
            }
            // Point sampling is already constrained to the polygon; inability to
            // add the boundary is reported rather than silently claiming exact crop.
            throw new MissingMethodException("Civil 3D boundary AddBoundaries overload was not found.");
        }

        private sealed class SurfaceChoice
        {
            public ObjectId Id; public string Name;
            public SurfaceChoice(ObjectId id, string name) { Id = id; Name = name; }
            public override string ToString() { return Name; }
        }

        private sealed class CropSettingsDialog : Form
        {
            private readonly ComboBox _surface = new ComboBox();
            private readonly TextBox _name = new TextBox();
            private readonly NumericUpDown _spacing = new NumericUpDown();
            public ObjectId SelectedSurfaceId { get { return ((SurfaceChoice)_surface.SelectedItem).Id; } }
            public string OutputName { get { return _name.Text.Trim(); } }
            public double SampleSpacing { get { return (double)_spacing.Value; } }

            public CropSettingsDialog(IList<SurfaceChoice> choices)
            {
                Text = "CE Tools - Crop and Reduce Surface";
                Width = 470; Height = 255; FormBorderStyle = FormBorderStyle.FixedDialog;
                StartPosition = FormStartPosition.CenterScreen; MaximizeBox = false; MinimizeBox = false;
                var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 2, RowCount = 5 };
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
                layout.Controls.Add(new System.Windows.Forms.Label { Text = "Source TIN surface:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
                _surface.DropDownStyle = ComboBoxStyle.DropDownList; _surface.Dock = DockStyle.Fill;
                foreach (SurfaceChoice choice in choices) _surface.Items.Add(choice);
                if (_surface.Items.Count > 0) _surface.SelectedIndex = 0;
                layout.Controls.Add(_surface, 1, 0);
                layout.Controls.Add(new System.Windows.Forms.Label { Text = "Output surface name:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
                _name.Text = "CE-CROPPED-SURFACE"; _name.Dock = DockStyle.Fill;
                layout.Controls.Add(_name, 1, 1);
                layout.Controls.Add(new System.Windows.Forms.Label { Text = "Sample spacing (drawing units):", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
                _spacing.DecimalPlaces = 2; _spacing.Minimum = 0.10M; _spacing.Maximum = 10000M; _spacing.Value = 5M; _spacing.Increment = 0.5M; _spacing.Dock = DockStyle.Fill;
                layout.Controls.Add(_spacing, 1, 2);
                layout.Controls.Add(new System.Windows.Forms.Label { Text = "Smaller spacing = more detail and larger output.", AutoSize = true, ForeColor = System.Drawing.Color.DimGray }, 0, 3);
                layout.SetColumnSpan(layout.Controls[layout.Controls.Count - 1], 2);
                var buttons = new FlowLayoutPanel { FlowDirection = System.Windows.Forms.FlowDirection.RightToLeft, Dock = DockStyle.Fill };
                var ok = new Button { Text = "Continue", DialogResult = DialogResult.OK, Width = 95 };
                var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 95 };
                buttons.Controls.Add(ok); buttons.Controls.Add(cancel);
                layout.Controls.Add(buttons, 0, 4); layout.SetColumnSpan(buttons, 2);
                Controls.Add(layout); AcceptButton = ok; CancelButton = cancel;
            }
        }
    }
}

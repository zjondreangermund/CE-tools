using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.ViewportModelBoundaryCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Creates the actual model-space footprint represented by a selected
    /// paper-space viewport, including a non-rectangular viewport clip when one
    /// is present, then switches to model space and zooms to the new boundary.
    /// </summary>
    public sealed class ViewportModelBoundaryCommands
    {
        private const string BoundaryLayerName = "CE-VP-BOUNDARY";

        [CommandMethod("CE_TOOLS", "CE_VIEWPORTBOUNDARY", CommandFlags.Modal | CommandFlags.Redraw)]
        public void CreateViewportBoundary()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            Editor editor = document.Editor;
            Database database = document.Database;

            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Model Space Viewport Boundaries",
                "Create the model-space footprint of one current paper-space viewport, every paper-space tab, or selected layout tabs. All viewport boundaries are drawn on CE-VP-BOUNDARY.");
            settings.AddChoice("Scope", "01 Layouts", "Viewport boundary scope", "All paper-space tabs",
                "Choose one viewport on the active layout, all paper-space layout tabs, or selected layout tabs (multiple viewports per tab are included).",
                new[] { "Current selected viewport", "All paper-space tabs", "Selected paper-space tabs" });
            settings.AddChoice("Zoom", "02 Model space", "Zoom in model space", "Zoom to all new boundaries",
                "Zoom to all created boundaries, or retain the current model-space view.",
                new[] { "Zoom to all new boundaries", "Keep current model view" });
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;
            if (settings.Text("Scope") != "Current selected viewport")
            {
                CreateLayoutViewportBoundaries(document, settings);
                return;
            }

            if (database.TileMode)
            {
                editor.WriteMessage("\nCE_VIEWPORTBOUNDARY: switch to a layout and select the paper-space viewport you want to plot in model space.");
                return;
            }

            var options = new PromptEntityOptions("\nSelect a layout viewport: ");
            options.SetRejectMessage("\nSelect a paper-space viewport.");
            options.AddAllowedClass(typeof(Viewport), false);
            PromptEntityResult picked = editor.GetEntity(options);
            if (picked.Status != PromptStatus.OK) return;

            ObjectId boundaryId = ObjectId.Null;
            List<Point3d> modelBoundary;
            string viewportHandle;

            try
            {
                using (DocumentLock documentLock = document.LockDocument())
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    Viewport viewport = transaction.GetObject(picked.ObjectId, OpenMode.ForRead, false) as Viewport;
                    if (viewport == null || viewport.Number == 1)
                    {
                        editor.WriteMessage("\nCE_VIEWPORTBOUNDARY: the overall layout viewport cannot be used. Select a model viewport on the layout.");
                        return;
                    }
                    if (viewport.PerspectiveOn)
                    {
                        editor.WriteMessage("\nCE_VIEWPORTBOUNDARY: perspective viewports do not have a single planar plot footprint. Use a parallel viewport for an exact boundary.");
                        return;
                    }

                    viewportHandle = viewport.Handle.ToString();
                    List<Point2d> paperBoundary = ReadPaperBoundary(viewport, transaction);
                    if (paperBoundary.Count < 3)
                    {
                        editor.WriteMessage("\nCE_VIEWPORTBOUNDARY: the selected viewport boundary could not be read.");
                        return;
                    }

                    modelBoundary = paperBoundary
                        .Select(point => PaperPointToModel(point, viewport))
                        .ToList();
                    RemoveConsecutiveDuplicates(modelBoundary);
                    if (modelBoundary.Count < 3)
                    {
                        editor.WriteMessage("\nCE_VIEWPORTBOUNDARY: the calculated model-space boundary is degenerate.");
                        return;
                    }

                    ObjectId layerId = EnsureLayer(database, transaction);
                    BlockTableRecord modelSpace = transaction.GetObject(
                        SymbolUtilityServices.GetBlockModelSpaceId(database),
                        OpenMode.ForWrite,
                        false) as BlockTableRecord;
                    if (modelSpace == null) return;

                    Entity boundary = CreateBoundaryEntity(modelBoundary);
                    boundary.SetDatabaseDefaults(database);
                    boundary.LayerId = layerId;
                    modelSpace.AppendEntity(boundary);
                    transaction.AddNewlyCreatedDBObject(boundary, true);
                    boundaryId = boundary.ObjectId;
                    WriteViewportLink(boundary, viewportHandle, transaction);
                    transaction.Commit();
                }
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage("\nCE_VIEWPORTBOUNDARY failed. {0}", exception.Message);
                return;
            }

            try
            {
                AcApplication.SetSystemVariable("TILEMODE", 1);
                editor.Regen();
            }
            catch { }

            if (!boundaryId.IsNull)
            {
                try
                {
                    editor.SetImpliedSelection(new[] { boundaryId });
                    editor.Command("_.ZOOM", "_Object", boundaryId, "");
                }
                catch
                {
                    ZoomToPoints(editor, modelBoundary);
                }
                try { editor.SetImpliedSelection(new[] { boundaryId }); } catch { }
            }

            editor.WriteMessage("\nCE_VIEWPORTBOUNDARY: viewport boundary created on layer '{0}' and model space zoomed to it.", BoundaryLayerName);
        }

        private sealed class LayoutTabInfo
        {
            internal ObjectId LayoutId;
            internal ObjectId BlockId;
            internal string Name;
            internal int Order;
        }

        private static List<LayoutTabInfo> GetPaperLayouts(Database database)
        {
            var layouts = new List<LayoutTabInfo>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                DBDictionary dictionary = transaction.GetObject(database.LayoutDictionaryId, OpenMode.ForRead) as DBDictionary;
                if (dictionary == null) return layouts;
                foreach (DBDictionaryEntry entry in dictionary)
                {
                    Layout layout = transaction.GetObject(entry.Value, OpenMode.ForRead, false) as Layout;
                    if (layout == null || layout.ModelType) continue;
                    layouts.Add(new LayoutTabInfo {
                        LayoutId = entry.Value, BlockId = layout.BlockTableRecordId,
                        Name = layout.LayoutName, Order = layout.TabOrder });
                }
            }
            return layouts.OrderBy(item => item.Order).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private sealed class LayoutTabSelectionDialog : System.Windows.Window
        {
            private readonly Dictionary<string, System.Windows.Controls.CheckBox> checkboxes =
                new Dictionary<string, System.Windows.Controls.CheckBox>(StringComparer.OrdinalIgnoreCase);
            internal HashSet<string> Selected { get; private set; }
            internal LayoutTabSelectionDialog(IEnumerable<LayoutTabInfo> layouts)
            {
                Title = "CE Tools - Select Paper Space Layout Tabs";
                Width = 520; Height = 570; MinWidth = 420; MinHeight = 360;
                WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen;
                Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(15, 19, 27));
                Foreground = System.Windows.Media.Brushes.White;
                var root = new System.Windows.Controls.DockPanel { Margin = new System.Windows.Thickness(16) };
                Content = root;
                var footer = new System.Windows.Controls.StackPanel {
                    Orientation = System.Windows.Controls.Orientation.Horizontal,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                    Margin = new System.Windows.Thickness(0, 12, 0, 0)
                };
                System.Windows.Controls.DockPanel.SetDock(footer, System.Windows.Controls.Dock.Bottom);
                root.Children.Add(footer);
                var tickAll = new System.Windows.Controls.Button {
                    Content = "Tick all", Padding = new System.Windows.Thickness(12, 5, 12, 5),
                    Margin = new System.Windows.Thickness(0, 0, 8, 0) };
                var clear = new System.Windows.Controls.Button {
                    Content = "Clear", Padding = new System.Windows.Thickness(12, 5, 12, 5),
                    Margin = new System.Windows.Thickness(0, 0, 8, 0) };
                var confirm = new System.Windows.Controls.Button {
                    Content = "Create selected boundaries", Padding = new System.Windows.Thickness(12, 5, 12, 5),
                    Margin = new System.Windows.Thickness(0, 0, 8, 0), IsDefault = true };
                var cancel = new System.Windows.Controls.Button {
                    Content = "Cancel", Padding = new System.Windows.Thickness(12, 5, 12, 5), IsCancel = true };
                footer.Children.Add(tickAll); footer.Children.Add(clear);
                footer.Children.Add(confirm); footer.Children.Add(cancel);
                var title = new System.Windows.Controls.TextBlock {
                    Text = "Choose the paper-space tabs to project into model space",
                    Foreground = System.Windows.Media.Brushes.White,
                    FontWeight = System.Windows.FontWeights.SemiBold, FontSize = 17,
                    Margin = new System.Windows.Thickness(0, 0, 0, 12) };
                System.Windows.Controls.DockPanel.SetDock(title, System.Windows.Controls.Dock.Top);
                root.Children.Add(title);
                var scroll = new System.Windows.Controls.ScrollViewer {
                    VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto };
                var entries = new System.Windows.Controls.StackPanel();
                scroll.Content = entries;
                root.Children.Add(scroll);
                foreach (LayoutTabInfo layout in layouts)
                {
                    var checkbox = new System.Windows.Controls.CheckBox {
                        Content = layout.Name, IsChecked = true,
                        Foreground = System.Windows.Media.Brushes.White,
                        Margin = new System.Windows.Thickness(4, 7, 4, 7),
                        FontSize = 14 };
                    checkboxes[layout.Name] = checkbox;
                    entries.Children.Add(checkbox);
                }
                tickAll.Click += (sender, args) => {
                    foreach (var check in checkboxes.Values) check.IsChecked = true; };
                clear.Click += (sender, args) => {
                    foreach (var check in checkboxes.Values) check.IsChecked = false; };
                confirm.Click += (sender, args) => {
                    Selected = new HashSet<string>(checkboxes
                        .Where(item => item.Value.IsChecked == true)
                        .Select(item => item.Key), StringComparer.OrdinalIgnoreCase);
                    if (Selected.Count == 0)
                    {
                        System.Windows.MessageBox.Show("Select at least one paper-space tab.",
                            "CE Tools - No layout selected");
                        return;
                    }
                    DialogResult = true;
                    Close();
                };
                cancel.Click += (sender, args) => { DialogResult = false; Close(); };
            }
        }

        private static void CreateLayoutViewportBoundaries(Document document, ProductionSettingsDialogModel settings)
        {
            Database database = document.Database;
            Editor editor = document.Editor;
            List<LayoutTabInfo> layouts = GetPaperLayouts(database);
            if (layouts.Count == 0)
            {
                editor.WriteMessage("\\nCE_VIEWPORTBOUNDARY: no paper-space layout tabs found.");
                return;
            }
            if (settings.Text("Scope") == "Selected paper-space tabs")
            {
                var window = new LayoutTabSelectionDialog(layouts);
                Autodesk.AutoCAD.ApplicationServices.Core.Application.ShowModalWindow(window);
                if (window.DialogResult != true || window.Selected == null) return;
                layouts = layouts.Where(layout => window.Selected.Contains(layout.Name)).ToList();
            }
            var allPoints = new List<Point3d>();
            var ids = new List<ObjectId>();
            int ignored = 0, invalid = 0;
            try
            {
                using (DocumentLock documentLock = document.LockDocument())
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    ObjectId layerId = EnsureLayer(database, transaction);
                    BlockTableRecord modelSpace = transaction.GetObject(
                        SymbolUtilityServices.GetBlockModelSpaceId(database),
                        OpenMode.ForWrite, false) as BlockTableRecord;
                    if (modelSpace == null) return;
                    foreach (LayoutTabInfo layout in layouts)
                    {
                        BlockTableRecord paperSpace = transaction.GetObject(
                            layout.BlockId, OpenMode.ForRead, false) as BlockTableRecord;
                        if (paperSpace == null) { invalid++; continue; }
                        foreach (ObjectId id in paperSpace)
                        {
                            Viewport viewport = transaction.GetObject(id, OpenMode.ForRead, false) as Viewport;
                            if (viewport == null) continue;
                            // Number=1 is the overall paper-space viewport, not a model view.
                            if (viewport.Number == 1 || viewport.PerspectiveOn)
                            { ignored++; continue; }
                            try
                            {
                                List<Point2d> paper = ReadPaperBoundary(viewport, transaction);
                                if (paper.Count < 3) { invalid++; continue; }
                                List<Point3d> model = paper.Select(point => PaperPointToModel(point, viewport)).ToList();
                                RemoveConsecutiveDuplicates(model);
                                if (model.Count < 3 || model.Any(p => double.IsNaN(p.X) || double.IsNaN(p.Y)))
                                { invalid++; continue; }
                                Entity boundary = CreateBoundaryEntity(model);
                                boundary.SetDatabaseDefaults(database);
                                boundary.LayerId = layerId;
                                modelSpace.AppendEntity(boundary);
                                transaction.AddNewlyCreatedDBObject(boundary, true);
                                WriteViewportLink(boundary, viewport.Handle.ToString(), transaction);
                                ids.Add(boundary.ObjectId);
                                allPoints.AddRange(model);
                            }
                            catch { invalid++; }
                        }
                    }
                    transaction.Commit();
                }
            }
            catch (System.Exception error)
            {
                editor.WriteMessage("\\nCE_VIEWPORTBOUNDARY failed; no new boundaries committed: {0}", error.Message);
                return;
            }
            if (ids.Count == 0)
            {
                editor.WriteMessage("\\nCE_VIEWPORTBOUNDARY: no usable non-perspective model viewports found in {0} layout tabs.", layouts.Count);
                return;
            }
            try
            {
                AcApplication.SetSystemVariable("TILEMODE", 1);
                editor.Regen();
                if (settings.Text("Zoom") == "Zoom to all new boundaries")
                    ZoomToPoints(editor, allPoints);
            }
            catch (System.Exception error)
            {
                editor.WriteMessage("\\nBoundaries were created, but model-space switch/zoom could not complete: {0}", error.Message);
            }
            try { editor.SetImpliedSelection(ids.ToArray()); } catch { }
            editor.WriteMessage(
                "\\nCE_VIEWPORTBOUNDARY: created {0} model-space boundaries from {1} paper-space tabs on layer '{2}'. Skipped {3} overall/perspective and {4} invalid viewports.",
                ids.Count, layouts.Count, BoundaryLayerName, ignored, invalid);
        }

        private static List<Point2d> ReadPaperBoundary(Viewport viewport, Transaction transaction)
        {
            if (viewport.NonRectClipOn && !viewport.NonRectClipEntityId.IsNull)
            {
                Entity clip = transaction.GetObject(viewport.NonRectClipEntityId, OpenMode.ForRead, false) as Entity;
                List<Point2d> sampled = SamplePaperEntity(clip);
                if (sampled.Count >= 3) return sampled;
            }

            double halfWidth = viewport.Width * 0.5;
            double halfHeight = viewport.Height * 0.5;
            Point3d centre = viewport.CenterPoint;
            return new List<Point2d>
            {
                new Point2d(centre.X - halfWidth, centre.Y - halfHeight),
                new Point2d(centre.X + halfWidth, centre.Y - halfHeight),
                new Point2d(centre.X + halfWidth, centre.Y + halfHeight),
                new Point2d(centre.X - halfWidth, centre.Y + halfHeight)
            };
        }

        private static List<Point2d> SamplePaperEntity(Entity entity)
        {
            var result = new List<Point2d>();
            Polyline polyline = entity as Polyline;
            if (polyline != null)
            {
                int count = polyline.NumberOfVertices;
                for (int index = 0; index < count; index++)
                {
                    SegmentType type;
                    try { type = polyline.GetSegmentType(index); }
                    catch { type = SegmentType.Line; }

                    if (type == SegmentType.Arc)
                    {
                        try
                        {
                            CircularArc2d arc = polyline.GetArcSegment2dAt(index);
                            int divisions = Math.Max(4, (int)Math.Ceiling(Math.Abs(arc.EndAngle - arc.StartAngle) / (Math.PI / 18.0)));
                            double delta = NormalizeArcDelta(arc.StartAngle, arc.EndAngle, arc.IsClockWise);
                            for (int step = 0; step < divisions; step++)
                            {
                                double angle = arc.StartAngle + delta * step / divisions;
                                result.Add(new Point2d(
                                    arc.Center.X + arc.Radius * Math.Cos(angle),
                                    arc.Center.Y + arc.Radius * Math.Sin(angle)));
                            }
                            continue;
                        }
                        catch { }
                    }
                    Point2d point = polyline.GetPoint2dAt(index);
                    result.Add(point);
                }
                return result;
            }

            Curve curve = entity as Curve;
            if (curve != null)
            {
                try
                {
                    double start = curve.StartParam;
                    double end = curve.EndParam;
                    int divisions = 72;
                    for (int index = 0; index < divisions; index++)
                    {
                        double parameter = start + (end - start) * index / divisions;
                        Point3d point = curve.GetPointAtParameter(parameter);
                        result.Add(new Point2d(point.X, point.Y));
                    }
                }
                catch { }
            }
            return result;
        }

        private static double NormalizeArcDelta(double start, double end, bool clockwise)
        {
            double delta = end - start;
            if (!clockwise)
            {
                while (delta <= 0.0) delta += Math.PI * 2.0;
            }
            else
            {
                while (delta >= 0.0) delta -= Math.PI * 2.0;
            }
            return delta;
        }

        private static Point3d PaperPointToModel(Point2d paperPoint, Viewport viewport)
        {
            double scale = Math.Abs(viewport.CustomScale) < 1e-12 ? 1.0 : viewport.CustomScale;
            double dcsX = viewport.ViewCenter.X + (paperPoint.X - viewport.CenterPoint.X) / scale;
            double dcsY = viewport.ViewCenter.Y + (paperPoint.Y - viewport.CenterPoint.Y) / scale;
            Point3d dcsPoint = new Point3d(dcsX, dcsY, 0.0);

            Matrix3d dcsToWcs = Matrix3d.PlaneToWorld(viewport.ViewDirection);
            dcsToWcs = Matrix3d.Displacement(viewport.ViewTarget - Point3d.Origin) * dcsToWcs;
            dcsToWcs = Matrix3d.Rotation(
                -viewport.TwistAngle,
                viewport.ViewDirection,
                viewport.ViewTarget) * dcsToWcs;
            return dcsPoint.TransformBy(dcsToWcs);
        }

        private static Entity CreateBoundaryEntity(IList<Point3d> points)
        {
            bool horizontal = points.All(point => Math.Abs(point.Z - points[0].Z) <= 1e-6);
            if (horizontal)
            {
                var polyline = new Polyline(points.Count);
                for (int index = 0; index < points.Count; index++)
                    polyline.AddVertexAt(index, new Point2d(points[index].X, points[index].Y), 0.0, 0.0, 0.0);
                polyline.Elevation = points[0].Z;
                polyline.Closed = true;
                return polyline;
            }

            var collection = new Point3dCollection();
            foreach (Point3d point in points) collection.Add(point);
            collection.Add(points[0]);
            return new Polyline3d(Poly3dType.SimplePoly, collection, false);
        }

        private static ObjectId EnsureLayer(Database database, Transaction transaction)
        {
            LayerTable table = transaction.GetObject(database.LayerTableId, OpenMode.ForRead, false) as LayerTable;
            if (table == null) return database.Clayer;
            if (table.Has(BoundaryLayerName)) return table[BoundaryLayerName];
            table.UpgradeOpen();
            var layer = new LayerTableRecord { Name = BoundaryLayerName, IsPlottable = false };
            ObjectId id = table.Add(layer);
            transaction.AddNewlyCreatedDBObject(layer, true);
            return id;
        }

        private static void WriteViewportLink(Entity boundary, string viewportHandle, Transaction transaction)
        {
            if (boundary.ExtensionDictionary.IsNull)
                boundary.CreateExtensionDictionary();
            DBDictionary dictionary = transaction.GetObject(boundary.ExtensionDictionary, OpenMode.ForWrite, false) as DBDictionary;
            if (dictionary == null) return;
            const string key = "CE_VIEWPORT_BOUNDARY";
            if (dictionary.Contains(key))
            {
                Xrecord existing = transaction.GetObject(dictionary.GetAt(key), OpenMode.ForWrite, false) as Xrecord;
                if (existing != null)
                    existing.Data = new ResultBuffer(new TypedValue((int)DxfCode.Text, viewportHandle));
                return;
            }
            var record = new Xrecord
            {
                Data = new ResultBuffer(new TypedValue((int)DxfCode.Text, viewportHandle))
            };
            dictionary.SetAt(key, record);
            transaction.AddNewlyCreatedDBObject(record, true);
        }

        private static void RemoveConsecutiveDuplicates(List<Point3d> points)
        {
            for (int index = points.Count - 1; index > 0; index--)
                if (points[index].DistanceTo(points[index - 1]) <= 1e-8)
                    points.RemoveAt(index);
            if (points.Count > 2 && points[0].DistanceTo(points[points.Count - 1]) <= 1e-8)
                points.RemoveAt(points.Count - 1);
        }

        private static void ZoomToPoints(Editor editor, IList<Point3d> points)
        {
            if (points == null || points.Count == 0) return;
            double minX = points.Min(point => point.X);
            double maxX = points.Max(point => point.X);
            double minY = points.Min(point => point.Y);
            double maxY = points.Max(point => point.Y);
            double width = Math.Max(1e-6, maxX - minX);
            double height = Math.Max(1e-6, maxY - minY);
            using (ViewTableRecord view = editor.GetCurrentView())
            {
                double viewRatio = Math.Max(1e-6, view.Width / Math.Max(1e-6, view.Height));
                double boxRatio = width / height;
                if (boxRatio > viewRatio) height = width / viewRatio;
                else width = height * viewRatio;
                view.CenterPoint = new Point2d((minX + maxX) * 0.5, (minY + maxY) * 0.5);
                view.Width = width * 1.10;
                view.Height = height * 1.10;
                editor.SetCurrentView(view);
            }
        }
    }
}

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

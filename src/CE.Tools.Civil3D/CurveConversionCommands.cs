using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.CurveConversionCommands))]

namespace CETools.Civil3D
{
    public sealed class CurveConversionCommands
    {
        [CommandMethod("CE_TOOLS", "CE_CURVECONVERT", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void ConvertCurves()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            var model = new ProductionSettingsDialogModel(
                "CE Tools - Convert Curves to Polylines",
                "Convert selected lines, arcs, circles, splines and 3D polylines through one popup. Source objects can be retained or replaced.");
            model.AddChoice(
                "Mode", "01 Conversion", "Conversion mode", "Auto-detect selected objects",
                "Auto-detect converts lines/arcs/circles/splines/3D polylines to lightweight polylines and converts lightweight polylines to 3D polylines.",
                new[]
                {
                    "Auto-detect selected objects",
                    "Lines to polylines",
                    "Arcs to polylines",
                    "Circles to polylines",
                    "Splines to polylines",
                    "3D polylines to polylines",
                    "Polylines to 3D polylines"
                });
            model.AddDouble(
                "Segment", "02 Approximation", "Maximum segment length", 1.0,
                "Curves are sampled so no generated chord is longer than this drawing-unit distance.");
            model.AddPositiveInteger(
                "ArcVertices", "02 Approximation", "Minimum vertices on arcs", 12,
                "Every converted arc receives at least this many visible polyline vertices.");
            model.AddPositiveInteger(
                "CircleVertices", "02 Approximation", "Minimum vertices on circles", 36,
                "Every converted circle receives at least this many visible polyline vertices.");
            model.AddChoice(
                "Keep", "03 Source", "Source objects", "Keep originals",
                "Keep the source objects or erase them only after each converted object is created successfully.",
                new[] { "Keep originals", "Replace originals" });
            model.AddChoice(
                "Layer", "03 Source", "Output layer", "Use source layer",
                "Use each source layer or place all converted objects on the current layer.",
                new[] { "Use source layer", "Use current layer" });
            model.AddChoice(
                "Elevation", "04 2D output", "2D elevation", "Use first source elevation",
                "Lightweight polylines have one elevation. Choose the first source elevation or flatten to zero.",
                new[] { "Use first source elevation", "Flatten to zero" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            PromptSelectionResult selection = SelectCurves(document.Editor);
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;

            string mode = model.Text("Mode");
            bool keep = string.Equals(model.Text("Keep"), "Keep originals", StringComparison.OrdinalIgnoreCase);
            bool sourceLayer = string.Equals(model.Text("Layer"), "Use source layer", StringComparison.OrdinalIgnoreCase);
            bool flatten = string.Equals(model.Text("Elevation"), "Flatten to zero", StringComparison.OrdinalIgnoreCase);
            double maximumSegment = Math.Max(model.Double("Segment", 1.0), 0.001);
            int minimumArcVertices = Math.Max(model.Integer("ArcVertices", 12), 4);
            int minimumCircleVertices = Math.Max(model.Integer("CircleVertices", 36), 12);
            int converted = 0;
            int skipped = 0;
            var rows = new List<IList<string>>();

            try
            {
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    BlockTableRecord space = transaction.GetObject(
                        document.Database.CurrentSpaceId,
                        OpenMode.ForWrite,
                        false) as BlockTableRecord;
                    if (space == null) throw new InvalidOperationException("The active drawing space is unavailable.");

                    foreach (ObjectId id in selection.Value.GetObjectIds().Distinct())
                    {
                        Entity source;
                        try { source = transaction.GetObject(id, OpenMode.ForWrite, false) as Entity; }
                        catch { skipped++; continue; }
                        if (source == null || source.IsErased || !MatchesMode(source, mode))
                        {
                            skipped++;
                            continue;
                        }

                        Entity output = CreateOutput(
                            source,
                            mode,
                            maximumSegment,
                            minimumArcVertices,
                            minimumCircleVertices,
                            flatten);
                        if (output == null)
                        {
                            skipped++;
                            continue;
                        }
                        output.SetDatabaseDefaults(document.Database);
                        output.LayerId = sourceLayer ? source.LayerId : document.Database.Clayer;
                        try { output.Color = source.Color; }
                        catch { output.ColorIndex = 256; }
                        output.LinetypeId = source.LinetypeId;
                        output.LineWeight = source.LineWeight;
                        try { output.Transparency = source.Transparency; } catch { }
                        ObjectId outputId = space.AppendEntity(output);
                        transaction.AddNewlyCreatedDBObject(output, true);
                        if (!keep) source.Erase(true);
                        converted++;
                        rows.Add(new List<string>
                        {
                            source.GetType().Name,
                            output.GetType().Name,
                            source.Handle.ToString(),
                            outputId.Handle.ToString(),
                            source.Layer
                        });
                    }
                    transaction.Commit();
                }
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage("\nCE_CURVECONVERT failed. The transaction was not committed. {0}", exception.Message);
                return;
            }

            document.Editor.Regen();
            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - Curve Conversion Complete",
                string.Format(CultureInfo.CurrentCulture, "Converted={0}; skipped={1}; originals={2}.", converted, skipped, keep ? "kept" : "replaced"),
                new List<string> { "Source Type", "Output Type", "Source Handle", "Output Handle", "Layer" },
                rows,
                "CE TOOLS CURVE CONVERSION REGISTER");
            document.Editor.WriteMessage("\nCE_CURVECONVERT complete. Converted={0}; skipped={1}.", converted, skipped);
        }

        [CommandMethod("CE_TOOLS", "CE_CONVERTCURVES", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void ConvertCurvesAlias()
        {
            ConvertCurves();
        }

        private static PromptSelectionResult SelectCurves(Editor editor)
        {
            PromptSelectionResult implied = editor.SelectImplied();
            if (implied.Status == PromptStatus.OK && implied.Value != null && implied.Value.Count > 0)
                return implied;
            var options = new PromptSelectionOptions
            {
                MessageForAdding = "\nSelect lines, arcs, circles, splines, lightweight polylines or 3D polylines: ",
                AllowDuplicates = false,
                RejectObjectsFromNonCurrentSpace = true
            };
            return editor.GetSelection(options);
        }

        private static bool MatchesMode(Entity source, string mode)
        {
            if (string.Equals(mode, "Auto-detect selected objects", StringComparison.OrdinalIgnoreCase))
                return source is Line || source is Arc || source is Circle || source is Spline || source is Polyline3d || source is Polyline || source is Polyline2d;
            if (string.Equals(mode, "Lines to polylines", StringComparison.OrdinalIgnoreCase)) return source is Line;
            if (string.Equals(mode, "Arcs to polylines", StringComparison.OrdinalIgnoreCase)) return source is Arc;
            if (string.Equals(mode, "Circles to polylines", StringComparison.OrdinalIgnoreCase)) return source is Circle;
            if (string.Equals(mode, "Splines to polylines", StringComparison.OrdinalIgnoreCase)) return source is Spline;
            if (string.Equals(mode, "3D polylines to polylines", StringComparison.OrdinalIgnoreCase)) return source is Polyline3d;
            if (string.Equals(mode, "Polylines to 3D polylines", StringComparison.OrdinalIgnoreCase)) return source is Polyline || source is Polyline2d;
            return false;
        }

        private static Entity CreateOutput(
            Entity source,
            string mode,
            double maximumSegment,
            int minimumArcVertices,
            int minimumCircleVertices,
            bool flatten)
        {
            // Auto-detect converts supported non-polyline curves to visible 2D
            // polylines. Existing 2D polylines are changed to 3D only when the
            // user explicitly selects the Polylines-to-3D mode.
            bool to3d = string.Equals(
                mode,
                "Polylines to 3D polylines",
                StringComparison.OrdinalIgnoreCase);
            if (!to3d)
            {
                Arc sourceArc = source as Arc;
                if (sourceArc != null)
                    return CreateExactArcPolyline(sourceArc, flatten);
                Circle sourceCircle = source as Circle;
                if (sourceCircle != null)
                    return CreateExactCirclePolyline(sourceCircle, flatten);
            }

            List<Point3d> points = Sample(
                source,
                maximumSegment,
                minimumArcVertices,
                minimumCircleVertices);
            if (points.Count < 2) return null;
            bool closed = IsClosed(source);
            RemoveClosingDuplicate(points);
            if (points.Count < 2) return null;

            if (to3d)
            {
                var collection = new Point3dCollection(points.ToArray());
                return new Polyline3d(Poly3dType.SimplePoly, collection, closed);
            }

            double elevation = flatten ? 0.0 : points[0].Z;
            var polyline = new Polyline(points.Count);
            for (int index = 0; index < points.Count; index++)
                polyline.AddVertexAt(index, new Point2d(points[index].X, points[index].Y), 0.0, 0.0, 0.0);
            polyline.Elevation = elevation;
            polyline.Closed = closed;
            return polyline;
        }

        private static Polyline CreateExactArcPolyline(Arc arc, bool flatten)
        {
            double sweep = arc.EndAngle - arc.StartAngle;
            while (sweep <= 0.0) sweep += Math.PI * 2.0;
            int segments = Math.Max(1, (int)Math.Ceiling(sweep / Math.PI));
            var polyline = new Polyline(segments + 1);
            for (int index = 0; index <= segments; index++)
            {
                double angle = arc.StartAngle + sweep * index / segments;
                Point3d point = arc.Center +
                    new Vector3d(Math.Cos(angle), Math.Sin(angle), 0.0) * arc.Radius;
                double bulge = index < segments
                    ? Math.Tan((sweep / segments) / 4.0)
                    : 0.0;
                polyline.AddVertexAt(
                    index,
                    new Point2d(point.X, point.Y),
                    bulge,
                    0.0,
                    0.0);
            }
            polyline.Elevation = flatten ? 0.0 : arc.Center.Z;
            polyline.Closed = false;
            return polyline;
        }

        private static Polyline CreateExactCirclePolyline(Circle circle, bool flatten)
        {
            const int segments = 4;
            double bulge = Math.Tan(Math.PI / 8.0);
            var polyline = new Polyline(segments);
            for (int index = 0; index < segments; index++)
            {
                double angle = Math.PI * 2.0 * index / segments;
                Point3d point = circle.Center +
                    new Vector3d(Math.Cos(angle), Math.Sin(angle), 0.0) * circle.Radius;
                polyline.AddVertexAt(
                    index,
                    new Point2d(point.X, point.Y),
                    bulge,
                    0.0,
                    0.0);
            }
            polyline.Elevation = flatten ? 0.0 : circle.Center.Z;
            polyline.Closed = true;
            return polyline;
        }

        private static List<Point3d> Sample(
            Entity entity,
            double maximumSegment,
            int minimumArcVertices,
            int minimumCircleVertices)
        {
            var points = new List<Point3d>();
            Line line = entity as Line;
            if (line != null)
            {
                points.Add(line.StartPoint);
                points.Add(line.EndPoint);
                return points;
            }
            Polyline lightweight = entity as Polyline;
            if (lightweight != null)
            {
                AddSamples(lightweight, maximumSegment, minimumArcVertices, minimumCircleVertices, points, true);
                return points;
            }
            Curve curve = entity as Curve;
            if (curve != null)
            {
                AddSamples(curve, maximumSegment, minimumArcVertices, minimumCircleVertices, points, true);
                return points;
            }
            return points;
        }

        private static void AddSamples(
            Curve curve,
            double maximumSegment,
            int minimumArcVertices,
            int minimumCircleVertices,
            IList<Point3d> points,
            bool includeStart)
        {
            if (curve == null) return;
            double length;
            try { length = curve.GetDistanceAtParameter(curve.EndParam) - curve.GetDistanceAtParameter(curve.StartParam); }
            catch
            {
                try { length = curve.StartPoint.DistanceTo(curve.EndPoint); }
                catch { return; }
            }
            int segments = Math.Max(1, (int)Math.Ceiling(Math.Abs(length) / maximumSegment));
            if (curve is Circle)
                segments = Math.Max(segments, Math.Max(minimumCircleVertices, 12));
            if (curve is Arc)
                segments = Math.Max(segments, Math.Max(minimumArcVertices, 4));
            for (int index = includeStart ? 0 : 1; index <= segments; index++)
            {
                double fraction = (double)index / segments;
                double parameter = curve.StartParam + ((curve.EndParam - curve.StartParam) * fraction);
                try { AddDistinct(points, curve.GetPointAtParameter(parameter)); }
                catch { }
            }
        }

        private static bool IsClosed(Entity entity)
        {
            Polyline polyline = entity as Polyline;
            if (polyline != null) return polyline.Closed;
            Polyline2d polyline2d = entity as Polyline2d;
            if (polyline2d != null) return polyline2d.Closed;
            Polyline3d polyline3d = entity as Polyline3d;
            if (polyline3d != null) return polyline3d.Closed;
            return entity is Circle;
        }

        private static void AddDistinct(IList<Point3d> points, Point3d point)
        {
            if (points.Count == 0 || points[points.Count - 1].DistanceTo(point) > 1e-8)
                points.Add(point);
        }

        private static void RemoveClosingDuplicate(IList<Point3d> points)
        {
            if (points.Count > 1 && points[0].DistanceTo(points[points.Count - 1]) <= 1e-8)
                points.RemoveAt(points.Count - 1);
        }
    }
}

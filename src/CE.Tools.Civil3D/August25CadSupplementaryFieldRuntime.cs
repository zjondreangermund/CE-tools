using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil;
using CivilFeatureLine = Autodesk.Civil.DatabaseServices.FeatureLine;

namespace CETools.Civil3D
{
    /// <summary>
    /// August 25 field runtime for the CAD Supplementary commands that must work
    /// against multiple Civil FeatureLines/AutoCAD curves without one bad object
    /// aborting or damaging the rest of the selection.
    /// </summary>
    internal static class August25CadSupplementaryFieldRuntime
    {
        private const double Tol = 1e-7;

        internal static void StretchFeatureLines(Document document)
        {
            if (document == null) return;
            Editor editor = document.Editor;
            PromptSelectionResult selection = Select(editor,
                "\nSelect Civil 3D feature lines to stretch: ");
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;

            PromptPointResult firstCorner = editor.GetPoint(
                "\nFirst corner of crossing stretch window: ");
            if (firstCorner.Status != PromptStatus.OK) return;
            var secondOptions = new PromptCornerOptions(
                "\nOpposite corner of crossing stretch window: ", firstCorner.Value);
            PromptPointResult secondCorner = editor.GetCorner(secondOptions);
            if (secondCorner.Status != PromptStatus.OK) return;
            PromptPointResult basePoint = editor.GetPoint("\nSpecify stretch base point: ");
            if (basePoint.Status != PromptStatus.OK) return;
            var destinationOptions = new PromptPointOptions("\nSpecify second point: ")
            {
                BasePoint = basePoint.Value,
                UseBasePoint = true
            };
            PromptPointResult destination = editor.GetPoint(destinationOptions);
            if (destination.Status != PromptStatus.OK) return;

            Point3d corner1 = firstCorner.Value.TransformBy(editor.CurrentUserCoordinateSystem);
            Point3d corner2 = secondCorner.Value.TransformBy(editor.CurrentUserCoordinateSystem);
            Point3d baseWorld = basePoint.Value.TransformBy(editor.CurrentUserCoordinateSystem);
            Point3d destinationWorld = destination.Value.TransformBy(editor.CurrentUserCoordinateSystem);
            Vector3d displacement = destinationWorld - baseWorld;
            if (displacement.Length <= Tol) return;

            int changed = 0;
            int skipped = 0;
            int failed = 0;
            foreach (ObjectId id in selection.Value.GetObjectIds().Distinct())
            {
                try
                {
                    using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                    {
                        CivilFeatureLine featureLine = transaction.GetObject(
                            id, OpenMode.ForWrite, false) as CivilFeatureLine;
                        if (featureLine == null || featureLine.IsReferenceObject ||
                            LayerLocked(transaction, featureLine.LayerId))
                        {
                            skipped++;
                            continue;
                        }

                        // Use the native stretch protocol exposed by the Civil entity.
                        // This preserves FeatureLine curves/elevations instead of
                        // rebuilding it from sampled points.
                        var stretchPoints = new Point3dCollection();
                        featureLine.GetStretchPoints(stretchPoints);
                        var indices = new IntegerCollection();
                        for (int index = 0; index < stretchPoints.Count; index++)
                        {
                            if (InsidePlanWindow(stretchPoints[index], corner1, corner2))
                                indices.Add(index);
                        }
                        if (indices.Count == 0)
                        {
                            skipped++;
                            continue;
                        }

                        featureLine.MoveStretchPointsAt(indices, displacement);
                        try { featureLine.RecordGraphicsModified(true); } catch { }
                        transaction.Commit();
                        changed++;
                    }
                }
                catch (System.Exception exception)
                {
                    failed++;
                    editor.WriteMessage("\nFeature line left unchanged: {0}", exception.Message);
                }
            }

            August21DisplayRefresh.Flush(document);
            editor.WriteMessage(
                "\nCE_MULTISTRETCHFL complete. Feature lines stretched={0}; skipped={1}; failed={2}.",
                changed, skipped, failed);
        }

        internal static void ConstructionOffsets(Document document)
        {
            if (document == null) return;
            Editor editor = document.Editor;
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Survey Offset / Construction Offset",
                "Offset multiple lines, polylines or Civil FeatureLines. Construction-line mode creates one finite zero-fillet construction polyline per source and removes redundant straight-section vertices.");
            model.AddPositiveDouble("Distance", "01 Offset", "Offset distance", 1.0,
                "Drawing-unit offset distance.");
            model.AddChoice("Mode", "01 Offset", "Offset type", "Normal offset",
                "Construction-line offset creates a finite joined construction polyline with zero-radius corners.",
                new[] { "Normal offset", "Construction-line offset" });
            model.AddChoice("Side", "01 Offset", "Offset side", "Pick side",
                "Pick side is safest for mixed directions. Left/right uses source direction.",
                new[] { "Pick side", "Left", "Right" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            PromptSelectionResult selection = Select(editor,
                "\nSelect lines, lightweight polylines and/or Civil FeatureLines to offset: ");
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;

            Point3d? sidePoint = null;
            if (string.Equals(model.Text("Side"), "Pick side", StringComparison.OrdinalIgnoreCase))
            {
                PromptPointResult point = editor.GetPoint("\nPick the required offset side: ");
                if (point.Status != PromptStatus.OK) return;
                sidePoint = point.Value.TransformBy(editor.CurrentUserCoordinateSystem);
            }

            double distance = Math.Max(model.Double("Distance", 1.0), Tol);
            bool construction = string.Equals(
                model.Text("Mode"), "Construction-line offset", StringComparison.OrdinalIgnoreCase);
            int created = 0;
            int skipped = 0;
            int failed = 0;

            foreach (ObjectId id in selection.Value.GetObjectIds().Distinct())
            {
                try
                {
                    using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                    {
                        Entity source = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                        if (source == null || LayerLocked(transaction, source.LayerId))
                        {
                            skipped++;
                            continue;
                        }

                        List<Point3d> plan = ReadPlanPoints(source);
                        if (plan.Count < 2)
                        {
                            skipped++;
                            continue;
                        }
                        plan = SimplifyStraightVertices(plan);
                        double sign = ResolveSign(plan, distance, model.Text("Side"), sidePoint);
                        BlockTableRecord space = transaction.GetObject(
                            source.OwnerId.IsNull ? document.Database.CurrentSpaceId : source.OwnerId,
                            OpenMode.ForWrite, false) as BlockTableRecord;
                        if (space == null) { skipped++; continue; }

                        if (construction)
                        {
                            List<Point3d> offsetPoints = BuildZeroFilletOffset(plan, sign * distance);
                            if (offsetPoints.Count < 2) { skipped++; continue; }
                            Polyline output = CreatePlanPolyline(
                                document.Database, offsetPoints, source.LayerId, false);
                            space.AppendEntity(output);
                            transaction.AddNewlyCreatedDBObject(output, true);
                            created++;
                        }
                        else
                        {
                            Curve autoCadCurve = source as Curve;
                            DBObjectCollection values = null;
                            Polyline transient = null;
                            try
                            {
                                if (autoCadCurve != null)
                                    values = autoCadCurve.GetOffsetCurves(sign * distance);
                                else
                                {
                                    transient = CreatePlanPolyline(
                                        document.Database, plan, source.LayerId, false);
                                    values = transient.GetOffsetCurves(sign * distance);
                                }
                                if (values == null || values.Count == 0)
                                    throw new InvalidOperationException("No offset geometry was returned.");
                                foreach (DBObject value in values)
                                {
                                    Entity output = value as Entity;
                                    if (output == null) { value.Dispose(); continue; }
                                    output.SetDatabaseDefaults(document.Database);
                                    output.LayerId = source.LayerId;
                                    space.AppendEntity(output);
                                    transaction.AddNewlyCreatedDBObject(output, true);
                                    created++;
                                }
                            }
                            finally
                            {
                                if (transient != null) transient.Dispose();
                            }
                        }
                        transaction.Commit();
                    }
                }
                catch (System.Exception exception)
                {
                    failed++;
                    editor.WriteMessage("\nOffset source left unchanged: {0}", exception.Message);
                }
            }

            August21DisplayRefresh.Flush(document);
            editor.WriteMessage(
                "\nCE_SURVEYCONSTRUCTIONOFFSET complete. Created={0}; skipped={1}; failed={2}; mode={3}.",
                created, skipped, failed,
                construction ? "finite construction polyline / zero-fillet" : "normal offset");
        }

        internal static void MiddleConstructionLines(Document document)
        {
            if (document == null) return;
            Editor editor = document.Editor;
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Centre Construction Lines",
                "Create one finite centre construction polyline between every selected pair. Collinear sample points are removed so straight sections contain no intermediate vertices.");
            model.AddPositiveDouble("Maximum", "01 Geometry", "Maximum pair distance", 20.0,
                "Pairs whose sampled separation exceeds this value are skipped.");
            model.AddPositiveInteger("Samples", "01 Geometry", "Curve samples per pair", 24,
                "Samples are used to follow bends, then redundant collinear vertices are removed.");
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            PromptSelectionResult selection = Select(editor,
                "\nSelect lines, polylines or Civil FeatureLines in pairs: ");
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;
            ObjectId[] ids = selection.Value.GetObjectIds().Distinct().ToArray();
            if (ids.Length < 2) return;

            double maximum = model.Double("Maximum", 20.0);
            int samples = Math.Max(2, model.Integer("Samples", 24));
            int created = 0;
            int skipped = 0;
            for (int pair = 0; pair + 1 < ids.Length; pair += 2)
            {
                try
                {
                    using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                    {
                        Entity first = transaction.GetObject(ids[pair], OpenMode.ForRead, false) as Entity;
                        Entity second = transaction.GetObject(ids[pair + 1], OpenMode.ForRead, false) as Entity;
                        if (first == null || second == null) { skipped++; continue; }
                        List<Point3d> a = ReadPlanPoints(first);
                        List<Point3d> b = ReadPlanPoints(second);
                        if (a.Count < 2 || b.Count < 2) { skipped++; continue; }

                        bool reverseB = PlanDistance(a[0], b[b.Count - 1]) + PlanDistance(a[a.Count - 1], b[0]) <
                                        PlanDistance(a[0], b[0]) + PlanDistance(a[a.Count - 1], b[b.Count - 1]);
                        var mid = new List<Point3d>();
                        double worst = 0.0;
                        for (int sample = 0; sample <= samples; sample++)
                        {
                            double fraction = sample / (double)samples;
                            Point3d pa = PointAtFraction(a, fraction);
                            Point3d pb = PointAtFraction(b, reverseB ? 1.0 - fraction : fraction);
                            worst = Math.Max(worst, PlanDistance(pa, pb));
                            mid.Add(new Point3d(
                                (pa.X + pb.X) * 0.5,
                                (pa.Y + pb.Y) * 0.5,
                                (pa.Z + pb.Z) * 0.5));
                        }
                        if (worst > maximum) { skipped++; continue; }
                        mid = SimplifyStraightVertices(mid);
                        if (mid.Count < 2) { skipped++; continue; }

                        BlockTableRecord space = transaction.GetObject(
                            document.Database.CurrentSpaceId, OpenMode.ForWrite, false) as BlockTableRecord;
                        Polyline output = CreatePlanPolyline(
                            document.Database, mid, first.LayerId, false);
                        space.AppendEntity(output);
                        transaction.AddNewlyCreatedDBObject(output, true);
                        transaction.Commit();
                        created++;
                    }
                }
                catch (System.Exception exception)
                {
                    skipped++;
                    editor.WriteMessage("\nCentre-line pair skipped: {0}", exception.Message);
                }
            }

            August21DisplayRefresh.Flush(document);
            editor.WriteMessage(
                "\nCE_SURVEYMIDCONSTRUCTION complete. Construction polylines={0}; skipped/unpaired={1}.",
                created, skipped + (ids.Length % 2));
        }

        private static PromptSelectionResult Select(Editor editor, string message)
        {
            PromptSelectionResult implied = editor.SelectImplied();
            if (implied.Status == PromptStatus.OK && implied.Value != null && implied.Value.Count > 0)
            {
                editor.SetImpliedSelection(new ObjectId[0]);
                return implied;
            }
            return editor.GetSelection(new PromptSelectionOptions
            {
                MessageForAdding = message,
                AllowDuplicates = false,
                RejectObjectsFromNonCurrentSpace = true
            });
        }

        private static List<Point3d> ReadPlanPoints(Entity entity)
        {
            var result = new List<Point3d>();
            Line line = entity as Line;
            if (line != null)
            {
                result.Add(line.StartPoint);
                result.Add(line.EndPoint);
                return result;
            }
            Polyline polyline = entity as Polyline;
            if (polyline != null)
            {
                for (int index = 0; index < polyline.NumberOfVertices; index++)
                    result.Add(polyline.GetPoint3dAt(index));
                return result;
            }
            CivilFeatureLine featureLine = entity as CivilFeatureLine;
            if (featureLine != null && !featureLine.IsReferenceObject)
            {
                Point3dCollection points = featureLine.GetPoints(FeatureLinePointType.AllPoints);
                if (points != null)
                    foreach (Point3d point in points) result.Add(point);
            }
            return result;
        }

        private static List<Point3d> SimplifyStraightVertices(IList<Point3d> source)
        {
            var points = new List<Point3d>();
            foreach (Point3d point in source)
            {
                if (points.Count == 0 || PlanDistance(points[points.Count - 1], point) > Tol)
                    points.Add(point);
            }
            if (points.Count <= 2) return points;
            var result = new List<Point3d> { points[0] };
            for (int index = 1; index < points.Count - 1; index++)
            {
                Point3d previous = result[result.Count - 1];
                Point3d current = points[index];
                Point3d next = points[index + 1];
                double ax = current.X - previous.X;
                double ay = current.Y - previous.Y;
                double bx = next.X - current.X;
                double by = next.Y - current.Y;
                double cross = Math.Abs(ax * by - ay * bx);
                double scale = Math.Max(1.0, Math.Sqrt(ax * ax + ay * ay) * Math.Sqrt(bx * bx + by * by));
                if (cross > Tol * scale) result.Add(current);
            }
            result.Add(points[points.Count - 1]);
            return result;
        }

        private static List<Point3d> BuildZeroFilletOffset(IList<Point3d> source, double offset)
        {
            var segments = new List<Tuple<Point3d, Point3d>>();
            for (int index = 0; index + 1 < source.Count; index++)
            {
                Point3d a = source[index];
                Point3d b = source[index + 1];
                double dx = b.X - a.X;
                double dy = b.Y - a.Y;
                double length = Math.Sqrt(dx * dx + dy * dy);
                if (length <= Tol) continue;
                double nx = -dy / length * offset;
                double ny = dx / length * offset;
                segments.Add(Tuple.Create(
                    new Point3d(a.X + nx, a.Y + ny, a.Z),
                    new Point3d(b.X + nx, b.Y + ny, b.Z)));
            }
            if (segments.Count == 0) return new List<Point3d>();
            var result = new List<Point3d> { segments[0].Item1 };
            for (int index = 0; index + 1 < segments.Count; index++)
            {
                Point3d intersection;
                if (TryInfiniteIntersection(
                        segments[index].Item1, segments[index].Item2,
                        segments[index + 1].Item1, segments[index + 1].Item2,
                        out intersection))
                    result.Add(intersection);
                else
                    result.Add(segments[index].Item2);
            }
            result.Add(segments[segments.Count - 1].Item2);
            return SimplifyStraightVertices(result);
        }

        private static bool TryInfiniteIntersection(
            Point3d a1, Point3d a2, Point3d b1, Point3d b2, out Point3d point)
        {
            point = Point3d.Origin;
            double rx = a2.X - a1.X;
            double ry = a2.Y - a1.Y;
            double sx = b2.X - b1.X;
            double sy = b2.Y - b1.Y;
            double denominator = rx * sy - ry * sx;
            if (Math.Abs(denominator) <= Tol) return false;
            double qx = b1.X - a1.X;
            double qy = b1.Y - a1.Y;
            double t = (qx * sy - qy * sx) / denominator;
            point = new Point3d(a1.X + t * rx, a1.Y + t * ry, a1.Z);
            return true;
        }

        private static double ResolveSign(
            IList<Point3d> points, double distance, string side, Point3d? picked)
        {
            if (string.Equals(side, "Left", StringComparison.OrdinalIgnoreCase)) return 1.0;
            if (string.Equals(side, "Right", StringComparison.OrdinalIgnoreCase)) return -1.0;
            if (!picked.HasValue || points.Count < 2) return 1.0;
            List<Point3d> positive = BuildZeroFilletOffset(points, distance);
            List<Point3d> negative = BuildZeroFilletOffset(points, -distance);
            double pd = DistanceToPolyline(positive, picked.Value);
            double nd = DistanceToPolyline(negative, picked.Value);
            return pd <= nd ? 1.0 : -1.0;
        }

        private static double DistanceToPolyline(IList<Point3d> points, Point3d point)
        {
            if (points == null || points.Count < 2) return double.PositiveInfinity;
            double best = double.PositiveInfinity;
            for (int index = 0; index + 1 < points.Count; index++)
                best = Math.Min(best, DistanceToSegment(point, points[index], points[index + 1]));
            return best;
        }

        private static double DistanceToSegment(Point3d p, Point3d a, Point3d b)
        {
            double dx = b.X - a.X;
            double dy = b.Y - a.Y;
            double length2 = dx * dx + dy * dy;
            if (length2 <= Tol) return PlanDistance(p, a);
            double t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / length2;
            t = Math.Max(0.0, Math.Min(1.0, t));
            double x = a.X + t * dx;
            double y = a.Y + t * dy;
            double ex = p.X - x;
            double ey = p.Y - y;
            return Math.Sqrt(ex * ex + ey * ey);
        }

        private static Polyline CreatePlanPolyline(
            Database database, IList<Point3d> points, ObjectId layerId, bool closed)
        {
            var output = new Polyline(points.Count);
            output.SetDatabaseDefaults(database);
            output.LayerId = layerId;
            output.Elevation = points.Count > 0 ? points[0].Z : 0.0;
            for (int index = 0; index < points.Count; index++)
                output.AddVertexAt(index, new Point2d(points[index].X, points[index].Y), 0.0, 0.0, 0.0);
            output.Closed = closed;
            return output;
        }

        private static Point3d PointAtFraction(IList<Point3d> points, double fraction)
        {
            if (points.Count == 0) return Point3d.Origin;
            if (points.Count == 1) return points[0];
            var lengths = new double[points.Count - 1];
            double total = 0.0;
            for (int index = 0; index < lengths.Length; index++)
            {
                lengths[index] = PlanDistance(points[index], points[index + 1]);
                total += lengths[index];
            }
            if (total <= Tol) return points[0];
            double target = Math.Max(0.0, Math.Min(1.0, fraction)) * total;
            double walked = 0.0;
            for (int index = 0; index < lengths.Length; index++)
            {
                if (walked + lengths[index] >= target || index == lengths.Length - 1)
                {
                    double ratio = lengths[index] <= Tol ? 0.0 : (target - walked) / lengths[index];
                    Point3d a = points[index];
                    Point3d b = points[index + 1];
                    return new Point3d(
                        a.X + (b.X - a.X) * ratio,
                        a.Y + (b.Y - a.Y) * ratio,
                        a.Z + (b.Z - a.Z) * ratio);
                }
                walked += lengths[index];
            }
            return points[points.Count - 1];
        }

        private static bool InsidePlanWindow(Point3d point, Point3d first, Point3d second)
        {
            double minX = Math.Min(first.X, second.X) - Tol;
            double maxX = Math.Max(first.X, second.X) + Tol;
            double minY = Math.Min(first.Y, second.Y) - Tol;
            double maxY = Math.Max(first.Y, second.Y) + Tol;
            return point.X >= minX && point.X <= maxX && point.Y >= minY && point.Y <= maxY;
        }

        private static double PlanDistance(Point3d first, Point3d second)
        {
            double dx = first.X - second.X;
            double dy = first.Y - second.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static bool LayerLocked(Transaction transaction, ObjectId layerId)
        {
            try
            {
                LayerTableRecord layer = transaction.GetObject(
                    layerId, OpenMode.ForRead, false) as LayerTableRecord;
                return layer != null && layer.IsLocked;
            }
            catch { return true; }
        }
    }
}

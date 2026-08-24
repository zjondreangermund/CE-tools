using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using AcCurve = Autodesk.AutoCAD.DatabaseServices.Curve;
using CivilFeatureLine = Autodesk.Civil.DatabaseServices.FeatureLine;

[assembly: CommandClass(typeof(CETools.Civil3D.August24FieldGeometryCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Field-requested CAD/Survey geometry utilities. These commands are deliberately
    /// transaction-local and do not queue the universal model refresh.
    /// </summary>
    public sealed class August24FieldGeometryCommands
    {
        private const double Tol = 1e-7;

        [CommandMethod("CE_TOOLS", "CE_CLOSEOPENMULTI", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void CloseOpenMultiple()
        {
            Document document = Active();
            if (document == null) return;
            PromptSelectionResult selection = Select(document.Editor,
                "\nSelect open lightweight polylines and/or Civil 3D feature lines to close: ");
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;

            int polylines = 0;
            int featureLines = 0;
            int alreadyClosed = 0;
            int failed = 0;

            foreach (ObjectId id in selection.Value.GetObjectIds().Distinct())
            {
                try
                {
                    using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                    {
                        DBObject value = transaction.GetObject(id, OpenMode.ForRead, false);
                        Polyline polyline = value as Polyline;
                        if (polyline != null)
                        {
                            if (polyline.Closed) { alreadyClosed++; continue; }
                            if (Locked(transaction, polyline.LayerId)) { failed++; continue; }
                            polyline.UpgradeOpen();
                            polyline.Closed = true;
                            try { polyline.RecordGraphicsModified(true); } catch { }
                            transaction.Commit();
                            polylines++;
                            continue;
                        }

                        CivilFeatureLine featureLine = value as CivilFeatureLine;
                        if (featureLine == null || featureLine.IsReferenceObject || Locked(transaction, featureLine.LayerId))
                        {
                            failed++;
                            continue;
                        }
                        if (featureLine.Closed) { alreadyClosed++; continue; }

                        Point3dCollection sourcePoints = featureLine.GetPoints(FeatureLinePointType.AllPoints);
                        if (sourcePoints == null || sourcePoints.Count < 2)
                        {
                            failed++;
                            continue;
                        }

                        string originalName = featureLine.Name;
                        ObjectId layerId = featureLine.LayerId;
                        ObjectId siteId = featureLine.SiteId;
                        string styleName = featureLine.StyleName;

                        BlockTableRecord space = transaction.GetObject(
                            featureLine.OwnerId,
                            OpenMode.ForWrite,
                            false) as BlockTableRecord;
                        if (space == null) { failed++; continue; }

                        var points = new Point3dCollection();
                        foreach (Point3d point in sourcePoints) points.Add(point);
                        if (points.Count > 1 && PlanDistance(points[0], points[points.Count - 1]) <= Tol)
                        {
                            // Polyline3d closed flag supplies the closing segment; do not duplicate its first vertex.
                            var trimmed = new Point3dCollection();
                            for (int index = 0; index < points.Count - 1; index++) trimmed.Add(points[index]);
                            points = trimmed;
                        }

                        var temporary = new Polyline3d(Poly3dType.SimplePoly, points, true);
                        temporary.SetDatabaseDefaults(document.Database);
                        temporary.LayerId = layerId;
                        space.AppendEntity(temporary);
                        transaction.AddNewlyCreatedDBObject(temporary, true);

                        // Free the Civil feature-line name only inside this transaction. If creation
                        // fails the transaction rolls back and the original is untouched.
                        featureLine.UpgradeOpen();
                        featureLine.Name = "CE_TMP_CLOSE_" + Guid.NewGuid().ToString("N");
                        string targetName = string.IsNullOrWhiteSpace(originalName)
                            ? "CE-CLOSED-FEATURELINE-" + id.Handle.ToString()
                            : originalName;
                        ObjectId replacementId = siteId.IsNull
                            ? CivilFeatureLine.Create(targetName, temporary.ObjectId)
                            : CivilFeatureLine.Create(targetName, temporary.ObjectId, siteId);
                        CivilFeatureLine replacement = transaction.GetObject(
                            replacementId, OpenMode.ForWrite, false) as CivilFeatureLine;
                        if (replacement == null || !replacement.Closed)
                            throw new InvalidOperationException("Civil 3D did not return a closed replacement feature line.");
                        replacement.LayerId = layerId;
                        if (!string.IsNullOrWhiteSpace(styleName)) replacement.StyleName = styleName;
                        if (!temporary.IsErased) temporary.Erase();
                        featureLine.Erase();
                        transaction.Commit();
                        featureLines++;
                    }
                }
                catch (System.Exception exception)
                {
                    failed++;
                    document.Editor.WriteMessage("\nA selected object was left unchanged: {0}", exception.Message);
                }
            }

            August21DisplayRefresh.Flush(document);
            document.Editor.WriteMessage(
                "\nCE_CLOSEOPENMULTI complete. Polylines closed={0}; feature lines closed={1}; already closed={2}; failed/skipped={3}.",
                polylines, featureLines, alreadyClosed, failed);
        }

        [CommandMethod("CE_TOOLS", "CE_MULTISTRETCHFL", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void StretchMultipleFeatureLines()
        {
            Document document = Active();
            if (document == null) return;
            PromptSelectionResult selection = Select(document.Editor,
                "\nSelect multiple feature lines to stretch with the native AutoCAD/Civil grip-aware STRETCH command: ");
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;

            var valid = new List<ObjectId>();
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in selection.Value.GetObjectIds().Distinct())
                {
                    try
                    {
                        CivilFeatureLine featureLine = transaction.GetObject(id, OpenMode.ForRead, false) as CivilFeatureLine;
                        if (featureLine != null && !featureLine.IsReferenceObject && !Locked(transaction, featureLine.LayerId))
                            valid.Add(id);
                    }
                    catch { }
                }
            }
            if (valid.Count == 0)
            {
                document.Editor.WriteMessage("\nCE_MULTISTRETCHFL: no editable feature lines were selected.");
                return;
            }

            document.Editor.SetImpliedSelection(valid.ToArray());
            document.Editor.WriteMessage(
                "\nCE_MULTISTRETCHFL: {0} feature lines preselected. Specify the native crossing window/base point for STRETCH.",
                valid.Count);
            document.SendStringToExecute("_.STRETCH ", true, false, true);
        }

        [CommandMethod("CE_TOOLS", "CE_SURVEYCONSTRUCTIONOFFSET", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void ConstructionOffsets()
        {
            Document document = Active();
            if (document == null) return;

            var model = new ProductionSettingsDialogModel(
                "CE Tools - Survey Offset / Construction Offset",
                "Offset selected lines/polylines normally, or create separate construction offsets for every straight section. Adjacent straight construction offsets are zero-fillet connected at their nearest usable crossing.");
            model.AddPositiveDouble("Distance", "01 Offset", "Offset distance", 1.0,
                "Drawing-unit offset distance.");
            model.AddChoice("Mode", "01 Offset", "Offset type", "Normal offset",
                "Construction mode treats every straight segment separately and joins adjacent offsets at a zero-radius intersection.",
                new[] { "Normal offset", "Construction-line offset" });
            model.AddChoice("Side", "01 Offset", "Offset side", "Pick side",
                "Pick side is safest for mixed curve directions. Left/right uses source curve direction.",
                new[] { "Pick side", "Left", "Right" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            PromptSelectionResult selection = Select(document.Editor,
                "\nSelect lines or lightweight polylines to offset: ");
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;

            Point3d? sidePoint = null;
            if (string.Equals(model.Text("Side"), "Pick side", StringComparison.OrdinalIgnoreCase))
            {
                PromptPointResult point = document.Editor.GetPoint("\nPick the required offset side: ");
                if (point.Status != PromptStatus.OK) return;
                sidePoint = point.Value.TransformBy(document.Editor.CurrentUserCoordinateSystem);
            }

            double distance = Math.Max(model.Double("Distance", 1.0), Tol);
            bool construction = string.Equals(model.Text("Mode"), "Construction-line offset", StringComparison.OrdinalIgnoreCase);
            int created = 0;
            int skipped = 0;

            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = transaction.GetObject(
                    document.Database.CurrentSpaceId, OpenMode.ForWrite, false) as BlockTableRecord;
                foreach (ObjectId id in selection.Value.GetObjectIds().Distinct())
                {
                    AcCurve curve = null;
                    try { curve = transaction.GetObject(id, OpenMode.ForRead, false) as AcCurve; }
                    catch { }
                    if (!(curve is Line) && !(curve is Polyline)) { skipped++; continue; }
                    Entity sourceEntity = curve as Entity;
                    if (sourceEntity == null || Locked(transaction, sourceEntity.LayerId)) { skipped++; continue; }

                    double sign = ResolveSign(curve, distance, model.Text("Side"), sidePoint);
                    if (!construction)
                    {
                        DBObjectCollection offsets = null;
                        try
                        {
                            offsets = curve.GetOffsetCurves(sign * distance);
                            foreach (DBObject value in offsets)
                            {
                                Entity entity = value as Entity;
                                if (entity == null) { value.Dispose(); continue; }
                                entity.SetDatabaseDefaults(document.Database);
                                entity.LayerId = sourceEntity.LayerId;
                                space.AppendEntity(entity);
                                transaction.AddNewlyCreatedDBObject(entity, true);
                                created++;
                            }
                        }
                        catch { skipped++; Dispose(offsets); }
                        continue;
                    }

                    List<Line> lines = BuildStraightConstructionOffsets(document.Database, curve, sign * distance, sourceEntity.LayerId);
                    JoinConsecutiveAtZeroFillet(lines);
                    foreach (Line line in lines)
                    {
                        space.AppendEntity(line);
                        transaction.AddNewlyCreatedDBObject(line, true);
                        created++;
                    }
                }
                transaction.Commit();
            }

            August21DisplayRefresh.Flush(document);
            document.Editor.WriteMessage(
                "\nCE_SURVEYCONSTRUCTIONOFFSET complete. Created={0}; skipped={1}; mode={2}.",
                created, skipped, construction ? "construction-line / zero-fillet" : "normal offset");
        }

        [CommandMethod("CE_TOOLS", "CE_SURVEYMIDCONSTRUCTION", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void MiddleConstructionLines()
        {
            Document document = Active();
            if (document == null) return;
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Centre Construction Lines",
                "Create a construction line midway between each selected pair of lines/polylines. Pairs beyond the maximum separation are skipped.");
            model.AddPositiveDouble("Maximum", "01 Geometry", "Maximum pair distance", 20.0,
                "Pairs whose sampled separation exceeds this value are not connected.");
            model.AddPositiveInteger("Samples", "01 Geometry", "Samples per pair", 20,
                "More samples follow curved/bent polylines more closely.");
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            PromptSelectionResult selection = Select(document.Editor,
                "\nSelect lines/polylines in pairs for centre construction lines: ");
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;
            ObjectId[] ids = selection.Value.GetObjectIds().Distinct().ToArray();
            if (ids.Length < 2)
            {
                document.Editor.WriteMessage("\nCE_SURVEYMIDCONSTRUCTION requires at least two curves.");
                return;
            }

            double maximum = model.Double("Maximum", 20.0);
            int samples = Math.Max(2, model.Integer("Samples", 20));
            int created = 0;
            int skipped = 0;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = transaction.GetObject(document.Database.CurrentSpaceId, OpenMode.ForWrite, false) as BlockTableRecord;
                var curves = new List<AcCurve>();
                foreach (ObjectId id in ids)
                {
                    AcCurve curve = null;
                    try { curve = transaction.GetObject(id, OpenMode.ForRead, false) as AcCurve; } catch { }
                    if (curve is Line || curve is Polyline) curves.Add(curve); else skipped++;
                }

                for (int index = 0; index + 1 < curves.Count; index += 2)
                {
                    AcCurve first = curves[index];
                    AcCurve second = curves[index + 1];
                    bool reverseSecond = EndpointPairDistance(first, second, true) < EndpointPairDistance(first, second, false);
                    var midpoints = new List<Point3d>();
                    double worst = 0.0;
                    for (int sample = 0; sample <= samples; sample++)
                    {
                        double fraction = sample / (double)samples;
                        Point3d a = PointAtFraction(first, fraction);
                        Point3d b = PointAtFraction(second, reverseSecond ? 1.0 - fraction : fraction);
                        worst = Math.Max(worst, PlanDistance(a, b));
                        midpoints.Add(new Point3d((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5, (a.Z + b.Z) * 0.5));
                    }
                    if (worst > maximum) { skipped++; continue; }

                    var output = new Polyline(midpoints.Count);
                    output.SetDatabaseDefaults(document.Database);
                    Entity firstEntity = first as Entity;
                    if (firstEntity != null) output.LayerId = firstEntity.LayerId;
                    output.Elevation = midpoints[0].Z;
                    for (int vertex = 0; vertex < midpoints.Count; vertex++)
                        output.AddVertexAt(vertex, new Point2d(midpoints[vertex].X, midpoints[vertex].Y), 0.0, 0.0, 0.0);
                    space.AppendEntity(output);
                    transaction.AddNewlyCreatedDBObject(output, true);
                    created++;
                }
                transaction.Commit();
            }
            August21DisplayRefresh.Flush(document);
            document.Editor.WriteMessage(
                "\nCE_SURVEYMIDCONSTRUCTION complete. Centre construction lines={0}; skipped/unpaired={1}.",
                created, skipped + (ids.Length % 2));
        }

        private static Document Active()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
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

        private static bool Locked(Transaction transaction, ObjectId layerId)
        {
            try
            {
                LayerTableRecord layer = transaction.GetObject(layerId, OpenMode.ForRead, false) as LayerTableRecord;
                return layer != null && layer.IsLocked;
            }
            catch { return true; }
        }

        private static double ResolveSign(AcCurve curve, double distance, string side, Point3d? picked)
        {
            if (string.Equals(side, "Left", StringComparison.OrdinalIgnoreCase)) return 1.0;
            if (string.Equals(side, "Right", StringComparison.OrdinalIgnoreCase)) return -1.0;
            if (!picked.HasValue) return 1.0;
            double positive = OffsetDistanceToPick(curve, distance, picked.Value);
            double negative = OffsetDistanceToPick(curve, -distance, picked.Value);
            return positive <= negative ? 1.0 : -1.0;
        }

        private static double OffsetDistanceToPick(AcCurve curve, double offset, Point3d pick)
        {
            DBObjectCollection values = null;
            try
            {
                values = curve.GetOffsetCurves(offset);
                double best = double.PositiveInfinity;
                foreach (DBObject value in values)
                {
                    AcCurve offsetCurve = value as AcCurve;
                    if (offsetCurve == null) continue;
                    Point3d closest = offsetCurve.GetClosestPointTo(new Point3d(pick.X, pick.Y, 0.0), false);
                    best = Math.Min(best, PlanDistance(closest, pick));
                }
                return best;
            }
            catch { return double.PositiveInfinity; }
            finally { Dispose(values); }
        }

        private static List<Line> BuildStraightConstructionOffsets(Database database, AcCurve curve, double offset, ObjectId layerId)
        {
            var sourceSegments = new List<Line>();
            Line sourceLine = curve as Line;
            if (sourceLine != null)
            {
                sourceSegments.Add(new Line(sourceLine.StartPoint, sourceLine.EndPoint));
            }
            Polyline sourcePolyline = curve as Polyline;
            if (sourcePolyline != null)
            {
                int segmentCount = sourcePolyline.Closed ? sourcePolyline.NumberOfVertices : sourcePolyline.NumberOfVertices - 1;
                for (int index = 0; index < segmentCount; index++)
                {
                    if (sourcePolyline.GetSegmentType(index) != SegmentType.Line) continue;
                    LineSegment2d segment = sourcePolyline.GetLineSegment2dAt(index);
                    sourceSegments.Add(new Line(
                        new Point3d(segment.StartPoint.X, segment.StartPoint.Y, sourcePolyline.Elevation),
                        new Point3d(segment.EndPoint.X, segment.EndPoint.Y, sourcePolyline.Elevation)));
                }
            }

            var result = new List<Line>();
            foreach (Line segment in sourceSegments)
            {
                Vector3d direction = segment.EndPoint - segment.StartPoint;
                if (direction.Length <= Tol) { segment.Dispose(); continue; }
                Vector3d normal = new Vector3d(-direction.Y, direction.X, 0.0).GetNormal() * offset;
                var line = new Line(segment.StartPoint + normal, segment.EndPoint + normal);
                line.SetDatabaseDefaults(database);
                line.LayerId = layerId;
                result.Add(line);
                segment.Dispose();
            }
            return result;
        }

        private static void JoinConsecutiveAtZeroFillet(IList<Line> lines)
        {
            for (int index = 0; index + 1 < lines.Count; index++)
            {
                Point3d intersection;
                if (!TryInfiniteIntersection(lines[index], lines[index + 1], out intersection)) continue;
                double local = Math.Max(lines[index].Length, lines[index + 1].Length) * 4.0 + 1.0;
                if (intersection.DistanceTo(lines[index].EndPoint) > local ||
                    intersection.DistanceTo(lines[index + 1].StartPoint) > local) continue;
                lines[index].EndPoint = intersection;
                lines[index + 1].StartPoint = intersection;
            }
        }

        private static bool TryInfiniteIntersection(Line first, Line second, out Point3d point)
        {
            point = Point3d.Origin;
            double x1 = first.StartPoint.X, y1 = first.StartPoint.Y;
            double x2 = first.EndPoint.X, y2 = first.EndPoint.Y;
            double x3 = second.StartPoint.X, y3 = second.StartPoint.Y;
            double x4 = second.EndPoint.X, y4 = second.EndPoint.Y;
            double denominator = (x1 - x2) * (y3 - y4) - (y1 - y2) * (x3 - x4);
            if (Math.Abs(denominator) <= Tol) return false;
            double determinant1 = x1 * y2 - y1 * x2;
            double determinant2 = x3 * y4 - y3 * x4;
            double x = (determinant1 * (x3 - x4) - (x1 - x2) * determinant2) / denominator;
            double y = (determinant1 * (y3 - y4) - (y1 - y2) * determinant2) / denominator;
            point = new Point3d(x, y, (first.EndPoint.Z + second.StartPoint.Z) * 0.5);
            return true;
        }

        private static double EndpointPairDistance(AcCurve first, AcCurve second, bool reverseSecond)
        {
            try
            {
                Point3d bStart = reverseSecond ? second.EndPoint : second.StartPoint;
                Point3d bEnd = reverseSecond ? second.StartPoint : second.EndPoint;
                return PlanDistance(first.StartPoint, bStart) + PlanDistance(first.EndPoint, bEnd);
            }
            catch { return double.PositiveInfinity; }
        }

        private static Point3d PointAtFraction(AcCurve curve, double fraction)
        {
            fraction = Math.Max(0.0, Math.Min(1.0, fraction));
            try
            {
                double start = curve.GetDistanceAtParameter(curve.StartParam);
                double end = curve.GetDistanceAtParameter(curve.EndParam);
                return curve.GetPointAtDist(start + (end - start) * fraction);
            }
            catch
            {
                double parameter = curve.StartParam + (curve.EndParam - curve.StartParam) * fraction;
                return curve.GetPointAtParameter(parameter);
            }
        }

        private static double PlanDistance(Point3d first, Point3d second)
        {
            double dx = first.X - second.X;
            double dy = first.Y - second.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static void Dispose(DBObjectCollection collection)
        {
            if (collection == null) return;
            foreach (DBObject value in collection)
            {
                try { value.Dispose(); } catch { }
            }
        }
    }
}

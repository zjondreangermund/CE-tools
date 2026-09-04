using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace CETools.Civil3D
{
    /// <summary>
    /// Final September 04 field runtime for the two failures reproduced in Civil 3D:
    /// plan-XY T/X junctions that were not detected, and destructive source replacement.
    /// Every selected lightweight polyline keeps its database object/handle. The source
    /// geometry becomes the first verified split span and only the remaining spans are
    /// appended as new lightweight polylines.
    /// </summary>
    internal static class September04GridBreakStabilityRuntime
    {
        private const double PlanTolerance = 0.01;
        private const double DistanceTolerance = 0.00001;

        private sealed class PlanPolyline : IDisposable
        {
            internal ObjectId SourceId;
            internal Polyline Source;
            internal Polyline Plan;

            public void Dispose()
            {
                if (Plan != null)
                {
                    try { Plan.Dispose(); } catch { }
                    Plan = null;
                }
            }
        }

        internal static void BreakPolylinesAtJunctions(Document document)
        {
            if (document == null || document.Database == null) return;
            Editor editor = document.Editor;

            PromptSelectionResult selection = Select(editor);
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;

            List<ObjectId> selectedIds = selection.Value.GetObjectIds()
                .Where(id => !id.IsNull && !id.IsErased)
                .Distinct()
                .ToList();
            if (selectedIds.Count < 2)
            {
                editor.WriteMessage("\nCE_PLBREAKJUNCTIONS: select at least two open lightweight polylines.");
                return;
            }

            var cuts = selectedIds.ToDictionary(id => id, id => new List<double>());
            int junctionLocations = 0;
            int validSources = 0;

            try
            {
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    var plans = new List<PlanPolyline>();
                    try
                    {
                        foreach (ObjectId id in selectedIds)
                        {
                            Polyline source = transaction.GetObject(id, OpenMode.ForRead, false) as Polyline;
                            if (source == null || source.IsErased || source.Closed || source.NumberOfVertices < 2)
                                continue;
                            plans.Add(new PlanPolyline
                            {
                                SourceId = id,
                                Source = source,
                                Plan = BuildPlanPolyline(source)
                            });
                        }
                        validSources = plans.Count;

                        for (int firstIndex = 0; firstIndex < plans.Count; firstIndex++)
                        {
                            for (int secondIndex = firstIndex + 1; secondIndex < plans.Count; secondIndex++)
                            {
                                PlanPolyline first = plans[firstIndex];
                                PlanPolyline second = plans[secondIndex];
                                var locations = new List<Point2d>();

                                // Primary route: flatten both polylines to world XY before
                                // IntersectWith. This catches crossings even when source Z
                                // elevations differ.
                                CollectPlanIntersections(first.Plan, second.Plan, locations);

                                // Deterministic straight-segment fallback for field drawings.
                                // This catches orthogonal T/X layouts even if transient
                                // IntersectWith is unavailable in a particular Civil session.
                                CollectStraightSegmentIntersections(first.Source, second.Source, locations);

                                // T junctions are endpoint-on-through-polyline contacts. The
                                // endpoint source is already broken there; only the through
                                // source receives an internal split distance.
                                CollectEndpointTouches(first.Plan, second.Plan, locations);

                                foreach (Point2d location in UniqueLocations(locations))
                                {
                                    bool cutFirst = TryAddInternalCut(first.Source, location, cuts[first.SourceId]);
                                    bool cutSecond = TryAddInternalCut(second.Source, location, cuts[second.SourceId]);
                                    if (cutFirst || cutSecond) junctionLocations++;
                                }
                            }
                        }
                    }
                    finally
                    {
                        foreach (PlanPolyline plan in plans) plan.Dispose();
                    }
                }
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_PLBREAKJUNCTIONS stopped during plan-junction analysis. No selected polyline was changed. {0}",
                    exception.Message);
                return;
            }

            foreach (List<double> distances in cuts.Values)
            {
                distances.Sort();
                RemoveNearDuplicateDistances(distances);
            }

            List<ObjectId> affected = cuts
                .Where(pair => pair.Value.Count > 0)
                .Select(pair => pair.Key)
                .ToList();
            if (affected.Count == 0)
            {
                editor.WriteMessage(
                    "\nCE_PLBREAKJUNCTIONS: no internal plan-XY crossings or T-junctions were found in {0} usable polylines.",
                    validSources);
                return;
            }

            int splitSources = 0;
            int appendedPieces = 0;
            int unchanged = 0;
            foreach (ObjectId id in affected)
            {
                try
                {
                    int created;
                    string failure;
                    if (SplitAndKeepSource(document.Database, id, cuts[id], out created, out failure))
                    {
                        splitSources++;
                        appendedPieces += created;
                    }
                    else
                    {
                        unchanged++;
                        if (!string.IsNullOrWhiteSpace(failure))
                            editor.WriteMessage("\nPolyline {0} kept unchanged: {1}", id.Handle, failure);
                    }
                }
                catch (System.Exception exception)
                {
                    unchanged++;
                    editor.WriteMessage("\nPolyline {0} kept unchanged: {1}", id.Handle, exception.Message);
                }
            }

            August21DisplayRefresh.Flush(document);
            editor.WriteMessage(
                "\nCE_PLBREAKJUNCTIONS complete. Junctions={0}; source polylines split={1}; additional polyline spans={2}; unchanged={3}. Source handles were retained and no selected source polyline was erased.",
                junctionLocations,
                splitSources,
                appendedPieces,
                unchanged);
        }

        private static PromptSelectionResult Select(Editor editor)
        {
            PromptSelectionResult implied = editor.SelectImplied();
            if (implied.Status == PromptStatus.OK && implied.Value != null && implied.Value.Count > 0)
            {
                var filtered = new List<ObjectId>();
                // Do not rely on implied-selection DXF filtering: validate again
                // when the selected objects are opened below.
                filtered.AddRange(implied.Value.GetObjectIds().Where(id => !id.IsNull));
                if (filtered.Count > 0)
                {
                    editor.SetImpliedSelection(new ObjectId[0]);
                    return implied;
                }
            }

            return editor.GetSelection(
                new PromptSelectionOptions
                {
                    MessageForAdding = "\nSelect lightweight polylines to BREAK AND KEEP at T-junctions/crossings: ",
                    AllowDuplicates = false,
                    RejectObjectsFromNonCurrentSpace = true
                },
                new SelectionFilter(new[]
                {
                    new TypedValue((int)DxfCode.Start, "LWPOLYLINE")
                }));
        }

        private static Polyline BuildPlanPolyline(Polyline source)
        {
            var plan = new Polyline(source.NumberOfVertices);
            for (int index = 0; index < source.NumberOfVertices; index++)
            {
                Point3d world = source.GetPoint3dAt(index);
                plan.AddVertexAt(
                    index,
                    new Point2d(world.X, world.Y),
                    source.GetBulgeAt(index),
                    0.0,
                    0.0);
            }
            plan.Closed = source.Closed;
            plan.Elevation = 0.0;
            return plan;
        }

        private static void CollectPlanIntersections(
            Polyline first,
            Polyline second,
            IList<Point2d> locations)
        {
            if (first == null || second == null || locations == null) return;
            var hits = new Point3dCollection();
            try
            {
                first.IntersectWith(
                    second,
                    Intersect.OnBothOperands,
                    hits,
                    IntPtr.Zero,
                    IntPtr.Zero);
                foreach (Point3d hit in hits)
                    AddUniqueLocation(locations, new Point2d(hit.X, hit.Y));
            }
            catch
            {
                // Straight-segment and endpoint fallbacks below still run.
            }
        }

        private static void CollectStraightSegmentIntersections(
            Polyline first,
            Polyline second,
            IList<Point2d> locations)
        {
            int firstSegments = first == null ? 0 : first.NumberOfVertices - 1;
            int secondSegments = second == null ? 0 : second.NumberOfVertices - 1;
            for (int a = 0; a < firstSegments; a++)
            {
                if (!IsStraight(first, a)) continue;
                Point3d a0w = first.GetPoint3dAt(a);
                Point3d a1w = first.GetPoint3dAt(a + 1);
                Point2d a0 = new Point2d(a0w.X, a0w.Y);
                Point2d a1 = new Point2d(a1w.X, a1w.Y);
                for (int b = 0; b < secondSegments; b++)
                {
                    if (!IsStraight(second, b)) continue;
                    Point3d b0w = second.GetPoint3dAt(b);
                    Point3d b1w = second.GetPoint3dAt(b + 1);
                    Point2d b0 = new Point2d(b0w.X, b0w.Y);
                    Point2d b1 = new Point2d(b1w.X, b1w.Y);
                    Point2d hit;
                    if (TrySegmentIntersection(a0, a1, b0, b1, out hit))
                        AddUniqueLocation(locations, hit);
                }
            }
        }

        private static bool IsStraight(Polyline polyline, int segment)
        {
            try { return polyline.GetSegmentType(segment) == SegmentType.Line; }
            catch { return Math.Abs(polyline.GetBulgeAt(segment)) <= 1e-10; }
        }

        private static bool TrySegmentIntersection(
            Point2d a,
            Point2d b,
            Point2d c,
            Point2d d,
            out Point2d hit)
        {
            hit = new Point2d();
            double rX = b.X - a.X;
            double rY = b.Y - a.Y;
            double sX = d.X - c.X;
            double sY = d.Y - c.Y;
            double denominator = rX * sY - rY * sX;
            double qX = c.X - a.X;
            double qY = c.Y - a.Y;

            if (Math.Abs(denominator) <= 1e-12)
            {
                // Collinear overlaps are intentionally not mass-broken. Endpoint
                // touch logic still catches a true T contact at an overlap end.
                return false;
            }

            double t = (qX * sY - qY * sX) / denominator;
            double u = (qX * rY - qY * rX) / denominator;
            double parameterTolerance = 1e-9;
            if (t < -parameterTolerance || t > 1.0 + parameterTolerance ||
                u < -parameterTolerance || u > 1.0 + parameterTolerance)
                return false;

            hit = new Point2d(a.X + t * rX, a.Y + t * rY);
            return true;
        }

        private static void CollectEndpointTouches(
            Polyline first,
            Polyline second,
            IList<Point2d> locations)
        {
            if (first == null || second == null) return;
            AddEndpointIfOnOther(first.StartPoint, second, locations);
            AddEndpointIfOnOther(first.EndPoint, second, locations);
            AddEndpointIfOnOther(second.StartPoint, first, locations);
            AddEndpointIfOnOther(second.EndPoint, first, locations);
        }

        private static void AddEndpointIfOnOther(
            Point3d endpoint,
            Polyline otherPlan,
            IList<Point2d> locations)
        {
            try
            {
                Point3d planPoint = new Point3d(endpoint.X, endpoint.Y, 0.0);
                Point3d closest = otherPlan.GetClosestPointTo(planPoint, false);
                if (PlanDistance(planPoint, closest) <= PlanTolerance)
                    AddUniqueLocation(locations, new Point2d(closest.X, closest.Y));
            }
            catch { }
        }

        private static bool TryAddInternalCut(
            Polyline source,
            Point2d planLocation,
            IList<double> cuts)
        {
            if (source == null || cuts == null) return false;
            try
            {
                Point3d first = source.GetPoint3dAt(0);
                Point3d probe = new Point3d(planLocation.X, planLocation.Y, first.Z);
                Point3d closest = source.GetClosestPointTo(probe, false);
                if (PlanDistance(probe, closest) > PlanTolerance) return false;
                double distance = source.GetDistAtPoint(closest);
                double length = source.Length;
                double endTolerance = Math.Max(DistanceTolerance, Math.Min(PlanTolerance, length * 1e-6));
                if (distance <= endTolerance || distance >= length - endTolerance) return false;
                if (cuts.Any(existing => Math.Abs(existing - distance) <= endTolerance)) return false;
                cuts.Add(distance);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool SplitAndKeepSource(
            Database database,
            ObjectId sourceId,
            IList<double> distances,
            out int appendedPieces,
            out string failure)
        {
            appendedPieces = 0;
            failure = string.Empty;
            if (distances == null || distances.Count == 0)
            {
                failure = "No internal split distances were supplied.";
                return false;
            }

            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                Polyline source = transaction.GetObject(sourceId, OpenMode.ForWrite, false) as Polyline;
                if (source == null || source.IsErased || source.Closed || source.NumberOfVertices < 2)
                {
                    failure = "Source is unavailable, closed, or not a usable lightweight polyline.";
                    return false;
                }
                if (LayerLocked(transaction, source.LayerId))
                {
                    failure = "Source layer is locked.";
                    return false;
                }

                double originalLength = source.Length;
                var splitPoints = new Point3dCollection();
                foreach (double distance in distances
                    .Where(value => value > DistanceTolerance && value < originalLength - DistanceTolerance)
                    .OrderBy(value => value))
                {
                    Point3d point = source.GetPointAtDist(distance);
                    if (!ContainsPoint(splitPoints, point)) splitPoints.Add(point);
                }
                if (splitPoints.Count == 0)
                {
                    failure = "All detected junctions resolved to source endpoints.";
                    return false;
                }

                DBObjectCollection raw = null;
                var pieces = new List<Polyline>();
                try
                {
                    raw = source.GetSplitCurves(splitPoints);
                    if (raw != null)
                    {
                        foreach (DBObject value in raw)
                        {
                            Polyline piece = value as Polyline;
                            if (piece != null && piece.NumberOfVertices >= 2 && piece.Length > DistanceTolerance)
                                pieces.Add(piece);
                            else
                                try { value.Dispose(); } catch { }
                        }
                    }

                    if (pieces.Count < 2)
                    {
                        failure = "AutoCAD returned fewer than two valid split spans.";
                        DisposePieces(pieces);
                        return false;
                    }

                    double totalLength = pieces.Sum(piece => piece.Length);
                    double lengthTolerance = Math.Max(0.001, originalLength * 1e-7);
                    if (Math.Abs(totalLength - originalLength) > lengthTolerance)
                    {
                        failure = "Split-span length verification failed; source was left unchanged.";
                        DisposePieces(pieces);
                        return false;
                    }

                    pieces = pieces
                        .OrderBy(piece => DistanceFromStart(source, piece.StartPoint))
                        .ToList();

                    BlockTableRecord owner = transaction.GetObject(
                        source.OwnerId,
                        OpenMode.ForWrite,
                        false) as BlockTableRecord;
                    if (owner == null)
                    {
                        failure = "Source owner space is unavailable.";
                        DisposePieces(pieces);
                        return false;
                    }

                    // Append all additional spans first. If anything below throws,
                    // the transaction rolls back and the original source remains
                    // untouched in the drawing.
                    for (int index = 1; index < pieces.Count; index++)
                    {
                        Polyline piece = pieces[index];
                        CopyEntityProperties(source, piece);
                        owner.AppendEntity(piece);
                        transaction.AddNewlyCreatedDBObject(piece, true);
                        appendedPieces++;
                    }

                    Polyline firstPiece = pieces[0];
                    ReplacePolylineGeometry(source, firstPiece);
                    try { source.RecordGraphicsModified(true); } catch { }
                    firstPiece.Dispose();
                    transaction.Commit();
                    return true;
                }
                catch
                {
                    foreach (Polyline piece in pieces)
                    {
                        if (piece == null || piece.IsNewObject) continue;
                        try { piece.Dispose(); } catch { }
                    }
                    throw;
                }
            }
        }

        private static void ReplacePolylineGeometry(Polyline target, Polyline sourcePiece)
        {
            if (target == null || sourcePiece == null || sourcePiece.NumberOfVertices < 2)
                throw new InvalidOperationException("Verified source split span is invalid.");

            target.Closed = false;
            int desired = sourcePiece.NumberOfVertices;
            while (target.NumberOfVertices > desired)
                target.RemoveVertexAt(target.NumberOfVertices - 1);

            while (target.NumberOfVertices < desired)
            {
                int index = target.NumberOfVertices;
                target.AddVertexAt(
                    index,
                    sourcePiece.GetPoint2dAt(index),
                    sourcePiece.GetBulgeAt(index),
                    sourcePiece.GetStartWidthAt(index),
                    sourcePiece.GetEndWidthAt(index));
            }

            for (int index = 0; index < desired; index++)
            {
                target.SetPointAt(index, sourcePiece.GetPoint2dAt(index));
                target.SetBulgeAt(index, sourcePiece.GetBulgeAt(index));
                target.SetStartWidthAt(index, sourcePiece.GetStartWidthAt(index));
                target.SetEndWidthAt(index, sourcePiece.GetEndWidthAt(index));
            }
            target.Elevation = sourcePiece.Elevation;
            target.Normal = sourcePiece.Normal;
            target.Closed = sourcePiece.Closed;
        }

        private static void CopyEntityProperties(Entity source, Entity target)
        {
            if (source == null || target == null) return;
            try { target.SetPropertiesFrom(source); } catch { }
            try { target.LayerId = source.LayerId; } catch { }
        }

        private static double DistanceFromStart(Polyline source, Point3d point)
        {
            try
            {
                Point3d closest = source.GetClosestPointTo(point, false);
                return source.GetDistAtPoint(closest);
            }
            catch
            {
                return double.MaxValue;
            }
        }

        private static bool ContainsPoint(Point3dCollection points, Point3d candidate)
        {
            foreach (Point3d point in points)
                if (PlanDistance(point, candidate) <= DistanceTolerance) return true;
            return false;
        }

        private static void RemoveNearDuplicateDistances(List<double> distances)
        {
            if (distances == null || distances.Count < 2) return;
            for (int index = distances.Count - 1; index > 0; index--)
            {
                if (Math.Abs(distances[index] - distances[index - 1]) <= DistanceTolerance)
                    distances.RemoveAt(index);
            }
        }

        private static IEnumerable<Point2d> UniqueLocations(IEnumerable<Point2d> values)
        {
            var result = new List<Point2d>();
            foreach (Point2d value in values ?? Enumerable.Empty<Point2d>())
                AddUniqueLocation(result, value);
            return result;
        }

        private static void AddUniqueLocation(IList<Point2d> values, Point2d candidate)
        {
            if (values.Any(value => PlanDistance(value, candidate) <= PlanTolerance * 0.25)) return;
            values.Add(candidate);
        }

        private static double PlanDistance(Point2d first, Point2d second)
        {
            double dx = first.X - second.X;
            double dy = first.Y - second.Y;
            return Math.Sqrt(dx * dx + dy * dy);
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
                LayerTableRecord layer = transaction.GetObject(layerId, OpenMode.ForRead, false) as LayerTableRecord;
                return layer != null && layer.IsLocked;
            }
            catch
            {
                return true;
            }
        }

        private static void DisposePieces(IEnumerable<Polyline> pieces)
        {
            foreach (Polyline piece in pieces ?? Enumerable.Empty<Polyline>())
            {
                try { piece.Dispose(); } catch { }
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace CETools.Civil3D
{
    internal static class August25CadSupplementaryBreakAnalysis
    {
        private const double Tolerance = 0.000001;

        private sealed class PlanSegment
        {
            internal Point3d Start;
            internal Point3d End;
            internal double StartDistance;
            internal double EndDistance;
        }

        internal static Dictionary<ObjectId, August25BreakPlan> Analyse(
            Database database,
            IList<ObjectId> ids,
            out int uniqueIntersections)
        {
            var result = ids.ToDictionary(
                id => id,
                id => new August25BreakPlan { SourceId = id });
            var unique = new List<Point3d>();

            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                for (int firstIndex = 0; firstIndex < ids.Count; firstIndex++)
                {
                    Curve first = transaction.GetObject(ids[firstIndex], OpenMode.ForRead, false) as Curve;
                    if (first == null || first.IsErased) continue;
                    double firstLength = CurveLength(first);
                    if (firstLength <= Tolerance) continue;

                    for (int secondIndex = firstIndex + 1; secondIndex < ids.Count; secondIndex++)
                    {
                        Curve second = transaction.GetObject(ids[secondIndex], OpenMode.ForRead, false) as Curve;
                        if (second == null || second.IsErased) continue;
                        double secondLength = CurveLength(second);
                        if (secondLength <= Tolerance) continue;

                        var native = new Point3dCollection();
                        try
                        {
                            first.IntersectWith(second, Intersect.OnBothOperands, native, IntPtr.Zero, IntPtr.Zero);
                        }
                        catch { }

                        foreach (Point3d point in native)
                        {
                            bool firstInternal = AddNativeDistance(
                                first,
                                point,
                                firstLength,
                                result[ids[firstIndex]].Distances);
                            bool secondInternal = AddNativeDistance(
                                second,
                                point,
                                secondLength,
                                result[ids[secondIndex]].Distances);
                            if (firstInternal || secondInternal) AddUniquePlan(unique, point);
                        }

                        AddPlanLinearIntersections(
                            transaction,
                            first,
                            second,
                            firstLength,
                            secondLength,
                            result[ids[firstIndex]].Distances,
                            result[ids[secondIndex]].Distances,
                            unique);
                    }
                }
            }

            foreach (August25BreakPlan plan in result.Values)
            {
                List<double> ordered = plan.Distances.OrderBy(value => value).ToList();
                plan.Distances.Clear();
                foreach (double value in ordered)
                {
                    if (plan.Distances.Count == 0 ||
                        Math.Abs(plan.Distances[plan.Distances.Count - 1] - value) > 0.00001)
                        plan.Distances.Add(value);
                }
            }

            uniqueIntersections = unique.Count;
            return result;
        }

        private static void AddPlanLinearIntersections(
            Transaction transaction,
            Curve first,
            Curve second,
            double firstLength,
            double secondLength,
            IList<double> firstDistances,
            IList<double> secondDistances,
            IList<Point3d> unique)
        {
            List<PlanSegment> firstSegments = ReadStraightSegments(transaction, first);
            List<PlanSegment> secondSegments = ReadStraightSegments(transaction, second);
            if (firstSegments.Count == 0 || secondSegments.Count == 0) return;

            foreach (PlanSegment firstSegment in firstSegments)
            {
                foreach (PlanSegment secondSegment in secondSegments)
                {
                    double firstRatio;
                    double secondRatio;
                    Point3d planPoint;
                    if (!TryPlanIntersection(
                            firstSegment,
                            secondSegment,
                            out firstRatio,
                            out secondRatio,
                            out planPoint))
                        continue;

                    double firstDistance = firstSegment.StartDistance +
                        (firstSegment.EndDistance - firstSegment.StartDistance) * firstRatio;
                    double secondDistance = secondSegment.StartDistance +
                        (secondSegment.EndDistance - secondSegment.StartDistance) * secondRatio;
                    bool firstInternal = AddDistance(firstDistances, firstDistance, firstLength);
                    bool secondInternal = AddDistance(secondDistances, secondDistance, secondLength);
                    if (firstInternal || secondInternal) AddUniquePlan(unique, planPoint);
                }
            }
        }

        private static List<PlanSegment> ReadStraightSegments(Transaction transaction, Curve curve)
        {
            var result = new List<PlanSegment>();
            Polyline lightweight = curve as Polyline;
            if (lightweight != null)
            {
                int count = lightweight.NumberOfVertices;
                if (count < 2) return result;
                double total = CurveLength(lightweight);
                int segmentCount = lightweight.Closed ? count : count - 1;
                for (int index = 0; index < segmentCount; index++)
                {
                    int next = (index + 1) % count;
                    if (Math.Abs(lightweight.GetBulgeAt(index)) > Tolerance) continue;
                    Point3d start = lightweight.GetPoint3dAt(index);
                    Point3d end = lightweight.GetPoint3dAt(next);
                    if (PlanDistance(start, end) <= Tolerance) continue;
                    double startDistance = index == 0 ? 0.0 : TryDistanceAtPoint(lightweight, start);
                    double endDistance = lightweight.Closed && next == 0
                        ? total
                        : TryDistanceAtPoint(lightweight, end);
                    if (!(endDistance > startDistance + Tolerance)) continue;
                    result.Add(new PlanSegment
                    {
                        Start = start,
                        End = end,
                        StartDistance = startDistance,
                        EndDistance = endDistance
                    });
                }
                return result;
            }

            Polyline3d threeDimensional = curve as Polyline3d;
            if (threeDimensional != null)
            {
                var vertices = new List<Point3d>();
                foreach (ObjectId vertexId in threeDimensional)
                {
                    PolylineVertex3d vertex = transaction.GetObject(
                        vertexId,
                        OpenMode.ForRead,
                        false) as PolylineVertex3d;
                    if (vertex != null) vertices.Add(vertex.Position);
                }
                int segmentCount = threeDimensional.Closed ? vertices.Count : vertices.Count - 1;
                double running = 0.0;
                for (int index = 0; index < segmentCount; index++)
                {
                    Point3d start = vertices[index];
                    Point3d end = vertices[(index + 1) % vertices.Count];
                    double length = start.DistanceTo(end);
                    if (length <= Tolerance) continue;
                    result.Add(new PlanSegment
                    {
                        Start = start,
                        End = end,
                        StartDistance = running,
                        EndDistance = running + length
                    });
                    running += length;
                }
            }
            return result;
        }

        private static bool TryPlanIntersection(
            PlanSegment first,
            PlanSegment second,
            out double firstRatio,
            out double secondRatio,
            out Point3d point)
        {
            firstRatio = 0.0;
            secondRatio = 0.0;
            point = Point3d.Origin;
            double rx = first.End.X - first.Start.X;
            double ry = first.End.Y - first.Start.Y;
            double sx = second.End.X - second.Start.X;
            double sy = second.End.Y - second.Start.Y;
            double denominator = rx * sy - ry * sx;
            if (Math.Abs(denominator) <= Tolerance) return false;

            double qx = second.Start.X - first.Start.X;
            double qy = second.Start.Y - first.Start.Y;
            firstRatio = (qx * sy - qy * sx) / denominator;
            secondRatio = (qx * ry - qy * rx) / denominator;
            if (firstRatio < -Tolerance || firstRatio > 1.0 + Tolerance ||
                secondRatio < -Tolerance || secondRatio > 1.0 + Tolerance)
                return false;

            firstRatio = Math.Max(0.0, Math.Min(1.0, firstRatio));
            secondRatio = Math.Max(0.0, Math.Min(1.0, secondRatio));
            point = new Point3d(
                first.Start.X + (first.End.X - first.Start.X) * firstRatio,
                first.Start.Y + (first.End.Y - first.Start.Y) * firstRatio,
                0.0);
            return true;
        }

        private static bool AddNativeDistance(Curve curve, Point3d point, double length, IList<double> distances)
        {
            try
            {
                Point3d onCurve = curve.GetClosestPointTo(point, false);
                return AddDistance(distances, curve.GetDistAtPoint(onCurve), length);
            }
            catch { return false; }
        }

        private static bool AddDistance(IList<double> distances, double distance, double length)
        {
            double tolerance = Math.Max(0.00001, length * 0.00000001);
            if (double.IsNaN(distance) || double.IsInfinity(distance) ||
                distance <= tolerance || length - distance <= tolerance)
                return false;
            if (!distances.Any(existing => Math.Abs(existing - distance) <= tolerance))
                distances.Add(distance);
            return true;
        }

        private static void AddUniquePlan(IList<Point3d> points, Point3d point)
        {
            if (points.Any(existing => PlanDistance(existing, point) <= 0.00001)) return;
            points.Add(new Point3d(point.X, point.Y, 0.0));
        }

        private static double TryDistanceAtPoint(Curve curve, Point3d point)
        {
            try { return curve.GetDistAtPoint(point); }
            catch { return double.NaN; }
        }

        internal static double CurveLength(Curve curve)
        {
            if (curve == null) return 0.0;
            try
            {
                return Math.Abs(
                    curve.GetDistanceAtParameter(curve.EndParam) -
                    curve.GetDistanceAtParameter(curve.StartParam));
            }
            catch { return 0.0; }
        }

        internal static double PlanDistance(Point3d first, Point3d second)
        {
            double dx = first.X - second.X;
            double dy = first.Y - second.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }
}

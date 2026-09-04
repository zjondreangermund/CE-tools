using System;
using System.Collections.Generic;
using System.Linq;

namespace CETools.Core
{
    /// <summary>
    /// Small CAD-independent plan geometry used by the Civil 3D break-at-junctions
    /// command. It deliberately works in XY only so crossings still resolve when
    /// selected route polylines carry different elevations.
    /// </summary>
    public struct PlanPoint
    {
        public PlanPoint(double x, double y)
        {
            X = x;
            Y = y;
        }

        public double X { get; }
        public double Y { get; }
    }

    public sealed class PlanPolylinePath
    {
        private readonly PlanPoint[] _points;

        public PlanPolylinePath(IEnumerable<PlanPoint> points)
        {
            if (points == null) throw new ArgumentNullException(nameof(points));
            _points = points.ToArray();
            if (_points.Length < 2)
                throw new ArgumentException("A plan polyline path requires at least two points.", nameof(points));
        }

        public IReadOnlyList<PlanPoint> Points => _points;

        public double Length
        {
            get
            {
                double length = 0.0;
                for (int index = 0; index + 1 < _points.Length; index++)
                    length += Distance(_points[index], _points[index + 1]);
                return length;
            }
        }

        private static double Distance(PlanPoint first, PlanPoint second)
        {
            double dx = second.X - first.X;
            double dy = second.Y - first.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }

    public sealed class PlanJunctionPlan
    {
        internal PlanJunctionPlan(
            IReadOnlyList<IReadOnlyList<double>> cutsByPath,
            IReadOnlyList<PlanPoint> junctions)
        {
            CutsByPath = cutsByPath;
            Junctions = junctions;
        }

        public IReadOnlyList<IReadOnlyList<double>> CutsByPath { get; }
        public IReadOnlyList<PlanPoint> Junctions { get; }
    }

    public static class PlanJunctionPlanner
    {
        public static PlanJunctionPlan Build(
            IReadOnlyList<PlanPolylinePath> paths,
            double tolerance = 0.01)
        {
            if (paths == null) throw new ArgumentNullException(nameof(paths));
            if (double.IsNaN(tolerance) || double.IsInfinity(tolerance) || tolerance <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(tolerance));

            var cuts = new List<List<double>>(paths.Count);
            for (int index = 0; index < paths.Count; index++)
            {
                if (paths[index] == null)
                    throw new ArgumentException("Plan paths cannot contain null values.", nameof(paths));
                cuts.Add(new List<double>());
            }

            var junctions = new List<PlanPoint>();
            for (int firstIndex = 0; firstIndex < paths.Count; firstIndex++)
            {
                for (int secondIndex = firstIndex + 1; secondIndex < paths.Count; secondIndex++)
                {
                    PlanPolylinePath first = paths[firstIndex];
                    PlanPolylinePath second = paths[secondIndex];
                    var candidates = new List<PlanPoint>();
                    CollectPairCandidates(first, second, tolerance, candidates);

                    foreach (PlanPoint candidate in candidates)
                    {
                        double firstStation;
                        double secondStation;
                        if (!TryStation(first, candidate, tolerance, out firstStation) ||
                            !TryStation(second, candidate, tolerance, out secondStation))
                            continue;

                        bool firstInternal = IsInternal(firstStation, first.Length, tolerance);
                        bool secondInternal = IsInternal(secondStation, second.Length, tolerance);
                        if (!firstInternal && !secondInternal)
                            continue; // shared endpoint only: there is nothing to break.

                        AddUniquePoint(junctions, candidate, tolerance);
                        if (firstInternal)
                            AddUniqueStation(cuts[firstIndex], firstStation, tolerance);
                        if (secondInternal)
                            AddUniqueStation(cuts[secondIndex], secondStation, tolerance);
                    }
                }
            }

            var readonlyCuts = new List<IReadOnlyList<double>>(cuts.Count);
            foreach (List<double> values in cuts)
            {
                values.Sort();
                readonlyCuts.Add(values.ToArray());
            }

            return new PlanJunctionPlan(readonlyCuts, junctions.ToArray());
        }

        private static void CollectPairCandidates(
            PlanPolylinePath first,
            PlanPolylinePath second,
            double tolerance,
            IList<PlanPoint> candidates)
        {
            IReadOnlyList<PlanPoint> a = first.Points;
            IReadOnlyList<PlanPoint> b = second.Points;

            for (int firstSegment = 0; firstSegment + 1 < a.Count; firstSegment++)
            {
                PlanPoint a0 = a[firstSegment];
                PlanPoint a1 = a[firstSegment + 1];
                for (int secondSegment = 0; secondSegment + 1 < b.Count; secondSegment++)
                {
                    PlanPoint b0 = b[secondSegment];
                    PlanPoint b1 = b[secondSegment + 1];
                    PlanPoint hit;
                    if (TrySegmentIntersection(a0, a1, b0, b1, tolerance, out hit))
                        AddUniquePoint(candidates, hit, tolerance);

                    // Explicit endpoint-on-through-segment checks are required for
                    // field T-junctions, including tiny drafting gaps inside the
                    // configured plan tolerance.
                    AddProjectedEndpoint(a0, b0, b1, tolerance, candidates);
                    AddProjectedEndpoint(a1, b0, b1, tolerance, candidates);
                    AddProjectedEndpoint(b0, a0, a1, tolerance, candidates);
                    AddProjectedEndpoint(b1, a0, a1, tolerance, candidates);
                }
            }
        }

        private static bool TrySegmentIntersection(
            PlanPoint a,
            PlanPoint b,
            PlanPoint c,
            PlanPoint d,
            double tolerance,
            out PlanPoint hit)
        {
            hit = default(PlanPoint);
            double rX = b.X - a.X;
            double rY = b.Y - a.Y;
            double sX = d.X - c.X;
            double sY = d.Y - c.Y;
            double rLength = Math.Sqrt(rX * rX + rY * rY);
            double sLength = Math.Sqrt(sX * sX + sY * sY);
            if (rLength <= tolerance * 0.001 || sLength <= tolerance * 0.001)
                return false;

            double denominator = rX * sY - rY * sX;
            double qX = c.X - a.X;
            double qY = c.Y - a.Y;
            double scale = Math.Max(1.0, rLength * sLength);
            if (Math.Abs(denominator) <= 1e-12 * scale)
                return false;

            double t = (qX * sY - qY * sX) / denominator;
            double u = (qX * rY - qY * rX) / denominator;
            double tTolerance = tolerance / Math.Max(rLength, tolerance);
            double uTolerance = tolerance / Math.Max(sLength, tolerance);
            if (t < -tTolerance || t > 1.0 + tTolerance ||
                u < -uTolerance || u > 1.0 + uTolerance)
                return false;

            t = Math.Max(0.0, Math.Min(1.0, t));
            hit = new PlanPoint(a.X + t * rX, a.Y + t * rY);
            return true;
        }

        private static void AddProjectedEndpoint(
            PlanPoint endpoint,
            PlanPoint segmentStart,
            PlanPoint segmentEnd,
            double tolerance,
            IList<PlanPoint> candidates)
        {
            PlanPoint projected;
            if (TryProject(endpoint, segmentStart, segmentEnd, tolerance, out projected))
                AddUniquePoint(candidates, projected, tolerance);
        }

        private static bool TryProject(
            PlanPoint point,
            PlanPoint start,
            PlanPoint end,
            double tolerance,
            out PlanPoint projected)
        {
            projected = default(PlanPoint);
            double dx = end.X - start.X;
            double dy = end.Y - start.Y;
            double lengthSquared = dx * dx + dy * dy;
            if (lengthSquared <= 1e-20) return false;

            double length = Math.Sqrt(lengthSquared);
            double t = ((point.X - start.X) * dx + (point.Y - start.Y) * dy) / lengthSquared;
            double parameterTolerance = tolerance / Math.Max(length, tolerance);
            if (t < -parameterTolerance || t > 1.0 + parameterTolerance)
                return false;

            double clamped = Math.Max(0.0, Math.Min(1.0, t));
            projected = new PlanPoint(start.X + clamped * dx, start.Y + clamped * dy);
            return Distance(point, projected) <= tolerance;
        }

        private static bool TryStation(
            PlanPolylinePath path,
            PlanPoint point,
            double tolerance,
            out double station)
        {
            station = 0.0;
            double running = 0.0;
            IReadOnlyList<PlanPoint> values = path.Points;
            double bestDistance = double.MaxValue;
            double bestStation = 0.0;

            for (int index = 0; index + 1 < values.Count; index++)
            {
                PlanPoint start = values[index];
                PlanPoint end = values[index + 1];
                double dx = end.X - start.X;
                double dy = end.Y - start.Y;
                double lengthSquared = dx * dx + dy * dy;
                double length = Math.Sqrt(lengthSquared);
                if (length <= 1e-12) continue;

                double t = ((point.X - start.X) * dx + (point.Y - start.Y) * dy) / lengthSquared;
                t = Math.Max(0.0, Math.Min(1.0, t));
                PlanPoint projected = new PlanPoint(start.X + t * dx, start.Y + t * dy);
                double distance = Distance(point, projected);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestStation = running + t * length;
                }
                running += length;
            }

            if (bestDistance > tolerance) return false;
            station = bestStation;
            return true;
        }

        private static bool IsInternal(double station, double length, double tolerance)
        {
            double endTolerance = Math.Max(tolerance * 0.1, Math.Min(tolerance, length * 1e-6));
            return station > endTolerance && station < length - endTolerance;
        }

        private static void AddUniqueStation(IList<double> values, double candidate, double tolerance)
        {
            double stationTolerance = Math.Max(0.000001, tolerance * 0.1);
            for (int index = 0; index < values.Count; index++)
                if (Math.Abs(values[index] - candidate) <= stationTolerance) return;
            values.Add(candidate);
        }

        private static void AddUniquePoint(IList<PlanPoint> values, PlanPoint candidate, double tolerance)
        {
            double pointTolerance = Math.Max(0.000001, tolerance * 0.5);
            for (int index = 0; index < values.Count; index++)
                if (Distance(values[index], candidate) <= pointTolerance) return;
            values.Add(candidate);
        }

        private static double Distance(PlanPoint first, PlanPoint second)
        {
            double dx = second.X - first.X;
            double dy = second.Y - first.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }
}

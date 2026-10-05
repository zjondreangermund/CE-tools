using System;
using System.Collections.Generic;

namespace CETools.Core
{
    public struct GradingPoint
    {
        public GradingPoint(double x, double y, double z) { X = x; Y = y; Z = z; }
        public double X { get; }
        public double Y { get; }
        public double Z { get; }
    }

    public static class GradingSlopeTicks
    {
        // Intersect the sampling ray with the actual toe segments. A short ray's
        // terrain sample may lie beyond the chord between neighbouring long rays.
        public static bool TryShortTick(GradingPoint source, GradingPoint sampledToe,
            IList<GradingPoint> toe, bool closed, bool cut, out GradingPoint start, out GradingPoint end)
        {
            start = source;
            end = source;
            if (toe == null || toe.Count < 2) return false;
            double dx = sampledToe.X - source.X, dy = sampledToe.Y - source.Y;
            if (dx * dx + dy * dy < 1e-12) return false;
            double closest = double.MaxValue;
            GradingPoint crossing = sampledToe;
            int count = closed ? toe.Count : toe.Count - 1;
            for (int i = 0; i < count; i++)
            {
                GradingPoint a = toe[i], b = toe[(i + 1) % toe.Count];
                double sx = b.X - a.X, sy = b.Y - a.Y;
                double determinant = dx * sy - dy * sx;
                if (Math.Abs(determinant) < 1e-12) continue;
                double ax = a.X - source.X, ay = a.Y - source.Y;
                double t = (ax * sy - ay * sx) / determinant;
                double u = (ax * dy - ay * dx) / determinant;
                if (t <= 1e-9 || u < -1e-9 || u > 1 + 1e-9) continue;
                double distance = Math.Abs(t - 1);
                if (distance >= closest) continue;
                closest = distance;
                u = Math.Max(0, Math.Min(1, u));
                crossing = new GradingPoint(a.X + u * sx, a.Y + u * sy, a.Z + u * (b.Z - a.Z));
            }
            if (closest == double.MaxValue) return false;
            start = cut ? crossing : source;
            end = new GradingPoint((source.X + crossing.X) / 2, (source.Y + crossing.Y) / 2,
                (source.Z + crossing.Z) / 2);
            return true;
        }
    }
}

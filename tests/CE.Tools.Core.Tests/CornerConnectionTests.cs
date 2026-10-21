using System;
using CETools.Core;

namespace CETools.Core.Tests
{
    internal static class CornerConnectionTests
    {
        internal static int Run()
        {
            int count = 0;
            foreach (double rotation in new[] { 0.0, 0.7, 2.3 })
            foreach (int side in new[] { -1, 1 })
            foreach (double angle in new[] { -35.0, 0, 45 })
            {
                double tx = Math.Cos(rotation), ty = Math.Sin(rotation);
                double nx = -ty * side, ny = tx * side;
                CornerConnectionPlan a, b;
                Check(CornerConnectionPlan.TryCreate(120, 430, tx, ty, nx, ny, -1, 10, 1, 1.5, 15, angle, out a), "create");
                Check(CornerConnectionPlan.TryCreate(120, 430, tx, ty, nx, ny, -1, 80, 1, 1.5, 15, angle, out b), "wide erf");
                Near(a.StartX, b.StartX); Near(a.StartY, b.StartY); Near(a.EndX, b.EndX); Near(a.EndY, b.EndY);
                double dx = a.EndX - a.StartX, dy = a.EndY - a.StartY;
                Near(Math.Sqrt(dx * dx + dy * dy), 15);
                Near(a.StartX + dx * 0.1, 120 + tx);
                Near(a.StartY + dy * 0.1, 430 + ty);
                CornerConnectionPlan shifted;
                Check(CornerConnectionPlan.TryCreate(120, 430, tx, ty, nx, ny, -1, 80, 3, 1.5, 15, angle, out shifted), "offset");
                Near(shifted.StartX - a.StartX, 2 * tx); Near(shifted.EndY - a.EndY, 2 * ty);
                count++;
            }
            CornerConnectionPlan invalid;
            Check(!CornerConnectionPlan.TryCreate(0, 0, 1, 0, 0, 1, 1, 2, 3, 1, 15, 0, out invalid), "reject offset outside frontage; never scale");
            Check(!CornerConnectionPlan.TryCreate(0, 0, 1, 0, 0, 1, 1, 20, 1, 1, 15, 90, out invalid), "reject invalid angle");
            return count + 2;
        }
        private static void Near(double a, double b) { Check(Math.Abs(a - b) < 1e-8, "fixed corner dimensions"); }
        private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    }
}

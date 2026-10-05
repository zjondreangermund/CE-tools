using System;
using CETools.Core;

namespace CETools.Core.Tests
{
    internal static class GradingSlopeTickTests
    {
        internal static int Run()
        {
            // The terrain sample is beyond the displayed toe chord. Both cut and
            // fill ticks must meet that chord, even when its order is reversed.
            var edge = new GradingPoint(0, 0, 10);
            var sample = new GradingPoint(0, 12, 16);
            var toe = new[] { new GradingPoint(-5, 8, 14), new GradingPoint(5, 8, 14) };
            GradingPoint start, end;
            if (!GradingSlopeTicks.TryShortTick(edge, sample, toe, false, true, out start, out end))
                throw new Exception("Cut tick could not reach the toe chord.");
            Near(8, start.Y); Near(14, start.Z); Near(4, end.Y); Near(12, end.Z);
            Array.Reverse(toe);
            if (!GradingSlopeTicks.TryShortTick(edge, sample, toe, false, true, out start, out end))
                throw new Exception("Reversed toe changed cut tick geometry.");
            Near(8, start.Y);
            toe = new[] { new GradingPoint(-5, 8, 6), new GradingPoint(5, 8, 6) };
            if (!GradingSlopeTicks.TryShortTick(edge, new GradingPoint(0, 12, 4), toe, false, false, out start, out end))
                throw new Exception("Fill tick could not reach toe chord.");
            Near(0, start.Y); Near(10, start.Z); Near(4, end.Y); Near(8, end.Z);
            if (GradingSlopeTicks.TryShortTick(edge, new GradingPoint(0, -12, 4), toe, false, true, out start, out end))
                throw new Exception("A toe behind the source must not receive a forward tick.");
            return 4;
        }
        private static void Near(double expected, double actual)
        {
            if (Math.Abs(expected - actual) > 1e-9) throw new Exception("Incorrect cut/fill short tick endpoint.");
        }
    }
}

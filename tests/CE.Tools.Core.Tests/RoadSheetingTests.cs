using System;
using CETools.Core;

namespace CETools.Core.Tests
{
    internal static class RoadSheetingTests
    {
        internal static int Run()
        {
            Equal(2000.0, RoadSheetingPlanner.DefaultSegmentLength("A0"));
            Equal(1000.0, RoadSheetingPlanner.DefaultSegmentLength("A1"));

            var a0 = RoadSheetingPlanner.Create(0.0, 8000.0, 2000.0, 2);
            Equal(2, a0.Count);
            Equal(2, a0[0].Segments.Count);
            Equal(0.0, a0[0].Segments[0].StartStation);
            Equal(2000.0, a0[0].Segments[0].EndStation);
            Equal(2000.0, a0[0].Segments[1].StartStation);
            Equal(4000.0, a0[0].Segments[1].EndStation);
            Equal(4000.0, a0[1].Segments[0].StartStation);
            Equal(8000.0, a0[1].Segments[1].EndStation);

            var a1 = RoadSheetingPlanner.Create(0.0, 8000.0, 1000.0, 2);
            Equal(4, a1.Count);

            var partial = RoadSheetingPlanner.Create(125.0, 4625.0, 2000.0, 2);
            Equal(2, partial.Count);
            Equal(3, partial[0].Segments.Count + partial[1].Segments.Count);
            Equal(4625.0, partial[1].Segments[0].EndStation);

            Equal("0+000", RoadSheetingPlanner.StationText(0.0));
            Equal("2+000", RoadSheetingPlanner.StationText(2000.0));
            Equal("2+345.5", RoadSheetingPlanner.StationText(2345.5));

            // Curved 1 km road: direction stays near horizontal through 560 m,
            // then turns enough to require the remaining chainage beside it.
            var stations = new double[] { 0, 100, 200, 300, 400, 500, 560, 620, 700, 800, 900, 1000 };
            var headings = new double[]
            {
                0, 0.01, 0.02, 0.03, 0.04, 0.05, 0.06,
                0.32, 0.36, 0.38, 0.40, 0.41
            };
            var curved = RoadSheetingPlanner.SplitPlanByHeading(
                stations,
                headings,
                12.0,
                100.0,
                6);
            Equal(2, curved.Count);
            Equal(0.0, curved[0].StartStation);
            Equal(560.0, curved[0].EndStation);
            Equal(560.0, curved[1].StartStation);
            Equal(1000.0, curved[1].EndStation);

            // 180-degree reversal represents the same horizontal road axis.
            Near(0.0, RoadSheetingPlanner.SmallestAxisAngleDifference(0.0, Math.PI));
            return 24;
        }

        private static void Equal(int expected, int actual)
        {
            if (expected != actual) throw new Exception("Road sheet count mismatch. Expected " + expected + ", got " + actual + ".");
        }

        private static void Equal(double expected, double actual)
        {
            if (Math.Abs(expected - actual) > 1e-9) throw new Exception("Road sheet value mismatch. Expected " + expected + ", got " + actual + ".");
        }

        private static void Near(double expected, double actual)
        {
            if (Math.Abs(expected - actual) > 1e-9) throw new Exception("Road sheet angle mismatch. Expected " + expected + ", got " + actual + ".");
        }

        private static void Equal(string expected, string actual)
        {
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
                throw new Exception("Road station text mismatch. Expected " + expected + ", got " + actual + ".");
        }
    }
}

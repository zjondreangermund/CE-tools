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
            return 17;
        }

        private static void Equal(int expected, int actual)
        {
            if (expected != actual) throw new Exception("Road sheet count mismatch. Expected " + expected + ", got " + actual + ".");
        }

        private static void Equal(double expected, double actual)
        {
            if (Math.Abs(expected - actual) > 1e-9) throw new Exception("Road sheet value mismatch. Expected " + expected + ", got " + actual + ".");
        }

        private static void Equal(string expected, string actual)
        {
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
                throw new Exception("Road station text mismatch. Expected " + expected + ", got " + actual + ".");
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using CETools.Core;

namespace CETools.Flood.Tests
{
    internal static class Program
    {
        private static int Main()
        {
            try
            {
                PropertyAssignmentWorks();
                DepthThresholdFiltersDrySamples();
                FramesSortByScenarioAndTime();
                PropertyPeakAndFirstFrameAreReported();
                Console.WriteLine("Flood result analysis tests passed: 4");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("Flood result analysis test failure:");
                Console.Error.WriteLine(exception);
                return 1;
            }
        }

        private static void PropertyAssignmentWorks()
        {
            FloodAnalysisResult result = FloodResultAnalyzer.Analyse(
                new[]
                {
                    new FloodProperty("A", Rectangle(0, 0, 10, 10)),
                    new FloodProperty("B", Rectangle(10, 0, 20, 10))
                },
                new[]
                {
                    Point(5, 5, .3, .8, "S", "00:10:00"),
                    Point(15, 5, .6, 1.2, "S", "00:10:00")
                },
                .05);
            Equal(1, result.PropertyFrames.Single(item => item.PropertyId == "A").WetPointCount, "property A wet points");
            Equal(1, result.PropertyFrames.Single(item => item.PropertyId == "B").WetPointCount, "property B wet points");
        }

        private static void DepthThresholdFiltersDrySamples()
        {
            FloodAnalysisResult result = FloodResultAnalyzer.Analyse(
                new[] { new FloodProperty("A", Rectangle(0, 0, 10, 10)) },
                new[]
                {
                    Point(2, 2, .04, .2, "S", "00:00:00"),
                    Point(4, 4, .20, .4, "S", "00:00:00")
                },
                .05);
            FloodPropertyFrameSummary summary = result.PropertyFrames.Single();
            Equal(2, summary.PointCount, "all sampled points");
            Equal(1, summary.WetPointCount, "thresholded wet points");
            Near(.20, summary.MaximumDepthMetres.Value, "maximum depth");
        }

        private static void FramesSortByScenarioAndTime()
        {
            FloodAnalysisResult result = FloodResultAnalyzer.Analyse(
                new FloodProperty[0],
                new[]
                {
                    Point(0, 0, .1, .2, "B", "00:10:00"),
                    Point(0, 0, .1, .2, "A", "00:20:00"),
                    Point(0, 0, .1, .2, "A", "00:05:00")
                },
                0.0);
            Equal("A", result.Frames[0].Key.Scenario, "first scenario");
            Equal("00:05:00", result.Frames[0].Key.Time, "first time");
            Equal("00:20:00", result.Frames[1].Key.Time, "second time");
            Equal("B", result.Frames[2].Key.Scenario, "third scenario");
        }

        private static void PropertyPeakAndFirstFrameAreReported()
        {
            FloodAnalysisResult result = FloodResultAnalyzer.Analyse(
                new[] { new FloodProperty("A", Rectangle(0, 0, 10, 10)) },
                new[]
                {
                    Point(5, 5, .1, .2, "S", "00:05:00"),
                    Point(5, 5, .8, 1.5, "S", "00:20:00"),
                    Point(5, 5, .3, .5, "S", "00:10:00")
                },
                .05);
            FloodPropertySummary summary = result.PropertySummaries.Single();
            Equal(3, summary.AffectedFrameCount, "affected frames");
            Equal("00:05:00", summary.FirstAffectedFrame.Time, "first affected frame");
            Equal("00:20:00", summary.PeakFrame.Time, "peak frame");
            Near(.8, summary.MaximumDepthMetres.Value, "property maximum depth");
        }

        private static FloodResultPoint Point(
            double x,
            double y,
            double depth,
            double velocity,
            string scenario,
            string time)
        {
            return new FloodResultPoint(
                x, y, 0.0, depth, velocity, 100.0 + depth,
                depth * (velocity + .5), scenario, time, string.Empty);
        }

        private static ParkingPolygon Rectangle(double minX, double minY, double maxX, double maxY)
        {
            return new ParkingPolygon(new[]
            {
                new ParkingPoint(minX, minY), new ParkingPoint(maxX, minY),
                new ParkingPoint(maxX, maxY), new ParkingPoint(minX, maxY)
            });
        }

        private static void Equal(int expected, int actual, string label)
        {
            if (expected != actual) throw new InvalidOperationException($"{label}: expected {expected}, received {actual}.");
        }

        private static void Equal(string expected, string actual, string label)
        {
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
                throw new InvalidOperationException($"{label}: expected {expected}, received {actual}.");
        }

        private static void Near(double expected, double actual, string label)
        {
            if (Math.Abs(expected - actual) > 1e-10)
                throw new InvalidOperationException($"{label}: expected {expected}, received {actual}.");
        }
    }
}

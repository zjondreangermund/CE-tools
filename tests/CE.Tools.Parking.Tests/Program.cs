using System;
using System.Collections.Generic;
using System.Linq;
using CETools.Core;

namespace CETools.Parking.Tests
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length != 1)
            {
                Console.Error.WriteLine("Specify one test: alternatives, obstacles, accessible or islands.");
                return 2;
            }
            try
            {
                switch (args[0].ToLowerInvariant())
                {
                    case "alternatives": Alternatives(); break;
                    case "obstacles": Obstacles(); break;
                    case "accessible": Accessible(); break;
                    case "islands": Islands(); break;
                    default: throw new InvalidOperationException("Unknown parking test: " + args[0]);
                }
                Console.WriteLine("Parking optimiser test passed: " + args[0]);
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("Parking optimiser test failed: " + args[0]);
                Console.Error.WriteLine(exception);
                return 1;
            }
        }

        private static void Alternatives()
        {
            IReadOnlyList<ParkingLayoutOption> options = ParkingLayoutOptimizer.Optimise(
                Rectangle(0.0, 0.0, 60.0, 40.0),
                new ParkingPolygon[0],
                new ParkingPoint(3.0, 20.0),
                Settings(80, 2, 10, 2.0));
            Equal(6, options.Count, "alternative count");
            True(options[0].TotalBayCount > 0, "best option should contain bays");
            True(options[0].Score >= options[options.Count - 1].Score, "options must be score ordered");
            True(options.Any(item => NearValue(item.ParkingAngleDegrees, 90.0)), "90 degree option missing");
            True(options.Any(item => NearValue(item.ParkingAngleDegrees, 60.0)), "60 degree option missing");
            True(options.Any(item => NearValue(item.ParkingAngleDegrees, 45.0)), "45 degree option missing");
        }

        private static void Obstacles()
        {
            ParkingLayoutSettings settings = Settings(80, 0, 0, 0.0);
            ParkingLayoutOption clear = ParkingLayoutOptimizer.Optimise(
                Rectangle(0.0, 0.0, 60.0, 40.0),
                new ParkingPolygon[0],
                new ParkingPoint(3.0, 20.0),
                settings)[0];
            ParkingLayoutOption obstructed = ParkingLayoutOptimizer.Optimise(
                Rectangle(0.0, 0.0, 60.0, 40.0),
                new[] { Rectangle(20.0, 0.0, 40.0, 40.0) },
                new ParkingPoint(3.0, 20.0),
                settings)[0];
            True(obstructed.TotalBayCount < clear.TotalBayCount,
                $"obstacle did not reduce capacity: clear={clear.TotalBayCount}, obstructed={obstructed.TotalBayCount}");
            True(obstructed.Rejections.ObstacleConflict > 0 || obstructed.Rejections.AisleBoundaryOrObstacle > 0,
                "obstacle rejection count was not recorded");
        }

        private static void Accessible()
        {
            ParkingLayoutOption best = ParkingLayoutOptimizer.Optimise(
                Rectangle(0.0, 0.0, 80.0, 50.0),
                new ParkingPolygon[0],
                new ParkingPoint(4.0, 25.0),
                Settings(100, 3, 0, 0.0))[0];
            Equal(3, best.AccessibleBayCount,
                "accessible count; notes=" + best.Notes);
            Equal(0, best.MissingAccessibleBays, "accessible shortfall");
            Equal(3, best.Bays.Count(item => item.Type == ParkingElementType.AccessibleBay), "accessible bay elements");
            Equal(3, best.Aisles.Count(item => item.Type == ParkingElementType.AccessAisle), "accessible aisle elements");
        }

        private static void Islands()
        {
            ParkingLayoutOption best = ParkingLayoutOptimizer.Optimise(
                Rectangle(0.0, 0.0, 80.0, 50.0),
                new ParkingPolygon[0],
                new ParkingPoint(4.0, 25.0),
                Settings(100, 0, 5, 2.0))[0];
            True(best.Islands.Count > 0, "no islands were generated");
            True(best.Islands.All(item => item.Type == ParkingElementType.LandscapeIsland), "incorrect island element type");
            True(best.HasEntranceConnection, "no entrance-to-aisle connection was generated; notes=" + best.Notes);
        }

        private static ParkingLayoutSettings Settings(int target, int accessible, int islandInterval, double islandWidth)
        {
            return new ParkingLayoutSettings(
                target, accessible, 2.5, 3.6, 1.5, 5.0, 6.0, 6.0,
                islandInterval, islandWidth,
                new[] { 90.0, 60.0, 45.0 },
                new[] { 0.0, 90.0 });
        }

        private static ParkingPolygon Rectangle(double minX, double minY, double maxX, double maxY)
        {
            return new ParkingPolygon(new[]
            {
                new ParkingPoint(minX, minY), new ParkingPoint(maxX, minY),
                new ParkingPoint(maxX, maxY), new ParkingPoint(minX, maxY)
            });
        }

        private static bool NearValue(double first, double second)
        {
            return Math.Abs(first - second) <= 0.001;
        }

        private static void Equal(int expected, int actual, string label)
        {
            if (expected != actual)
                throw new InvalidOperationException($"{label}: expected {expected}, received {actual}.");
        }

        private static void True(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}

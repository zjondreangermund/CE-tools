using System;
using System.Collections.Generic;
using CETools.Core;

namespace CETools.PlanJunction.Tests
{
    internal static class Program
    {
        private static int Main()
        {
            try
            {
                ScreenshotStyleNetworkFindsCrossingsAndTJunctions();
                NearTJunctionInsideToleranceCutsThroughRouteOnly();
                SharedEndpointsAreNotBroken();
                SimpleXCrossingCutsBothRoutes();
                Console.WriteLine("CE Tools plan-junction tests passed: 4");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("CE Tools plan-junction test failure:");
                Console.Error.WriteLine(exception);
                return 1;
            }
        }

        private static void ScreenshotStyleNetworkFindsCrossingsAndTJunctions()
        {
            var paths = new List<PlanPolylinePath>
            {
                Path(P(0, 0), P(20, 0), P(20, 20)),
                Path(P(0, 10), P(60, 10)),
                Path(P(20, 20), P(45, 20)),
                Path(P(35, 5), P(35, 45)),
                Path(P(45, 20), P(45, 40)),
                Path(P(25, 35), P(45, 35))
            };

            PlanJunctionPlan plan = PlanJunctionPlanner.Build(paths, 0.01);

            Equal(5, plan.Junctions.Count, "junction count");
            Equal(1, plan.CutsByPath[0].Count, "top-L cuts");
            Equal(2, plan.CutsByPath[1].Count, "long horizontal cuts");
            Equal(1, plan.CutsByPath[2].Count, "middle horizontal cuts");
            Equal(3, plan.CutsByPath[3].Count, "centre vertical cuts");
            Equal(1, plan.CutsByPath[4].Count, "right vertical T cut");
            Equal(1, plan.CutsByPath[5].Count, "lower horizontal crossing cut");

            Near(30.0, plan.CutsByPath[0][0], "top-L station");
            Near(20.0, plan.CutsByPath[1][0], "first long-horizontal station");
            Near(35.0, plan.CutsByPath[1][1], "second long-horizontal station");
        }

        private static void NearTJunctionInsideToleranceCutsThroughRouteOnly()
        {
            var paths = new List<PlanPolylinePath>
            {
                Path(P(0, 0), P(20, 0)),
                Path(P(10, 0.005), P(10, 10))
            };

            PlanJunctionPlan plan = PlanJunctionPlanner.Build(paths, 0.01);
            Equal(1, plan.Junctions.Count, "near T junction count");
            Equal(1, plan.CutsByPath[0].Count, "through route cut count");
            Equal(0, plan.CutsByPath[1].Count, "branch endpoint cut count");
            Near(10.0, plan.CutsByPath[0][0], "near T through station");
        }

        private static void SharedEndpointsAreNotBroken()
        {
            var paths = new List<PlanPolylinePath>
            {
                Path(P(0, 0), P(10, 0)),
                Path(P(10, 0), P(10, 10))
            };

            PlanJunctionPlan plan = PlanJunctionPlanner.Build(paths, 0.01);
            Equal(0, plan.Junctions.Count, "shared endpoint junction count");
            Equal(0, plan.CutsByPath[0].Count, "first shared endpoint cuts");
            Equal(0, plan.CutsByPath[1].Count, "second shared endpoint cuts");
        }

        private static void SimpleXCrossingCutsBothRoutes()
        {
            var paths = new List<PlanPolylinePath>
            {
                Path(P(0, 5), P(10, 5)),
                Path(P(5, 0), P(5, 10))
            };

            PlanJunctionPlan plan = PlanJunctionPlanner.Build(paths, 0.01);
            Equal(1, plan.Junctions.Count, "X junction count");
            Equal(1, plan.CutsByPath[0].Count, "X horizontal cuts");
            Equal(1, plan.CutsByPath[1].Count, "X vertical cuts");
            Near(5.0, plan.CutsByPath[0][0], "X horizontal station");
            Near(5.0, plan.CutsByPath[1][0], "X vertical station");
        }

        private static PlanPolylinePath Path(params PlanPoint[] points)
        {
            return new PlanPolylinePath(points);
        }

        private static PlanPoint P(double x, double y)
        {
            return new PlanPoint(x, y);
        }

        private static void Equal(int expected, int actual, string label)
        {
            if (expected != actual)
                throw new InvalidOperationException(label + ": expected " + expected + ", received " + actual + ".");
        }

        private static void Near(double expected, double actual, string label, double tolerance = 1e-8)
        {
            if (Math.Abs(expected - actual) > tolerance)
                throw new InvalidOperationException(label + ": expected " + expected + ", received " + actual + ".");
        }
    }
}

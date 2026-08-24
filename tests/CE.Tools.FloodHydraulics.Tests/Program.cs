using System;
using System.Collections.Generic;
using CETools.Core;

namespace CETools.FloodHydraulics.Tests
{
    internal static class Program
    {
        private static int Main()
        {
            try
            {
                double q = FloodCulvertHydraulics.RationalPeakFlow(0.50, 0.70, 60.0);
                Near(5.833333333333333, q, 1e-9);

                var intensities = new Dictionary<int, double>
                {
                    { 2, 25.0 }, { 5, 35.0 }, { 10, 45.0 }, { 20, 55.0 },
                    { 25, 60.0 }, { 50, 75.0 }, { 100, 90.0 }
                };
                IDictionary<int, double> flows = FloodCulvertHydraulics.RationalReturnPeriodFlows(0.5, 0.7, intensities);
                True(flows[100] > flows[50]);
                True(flows[50] > flows[25]);

                var box = new CulvertSection(CulvertShape.Box, 1.2, 0.9, 2, 0.012, 0.01);
                CulvertHydraulicResult boxReview = FloodCulvertHydraulics.Review(box, 3.5);
                True(boxReview.CapacityCubicMetresPerSecond > 0.0);
                True(boxReview.NormalDepthMetres > 0.0 && boxReview.NormalDepthMetres <= 0.9);
                True(boxReview.VelocityMetresPerSecond > 0.0);

                CulvertHydraulicResult recommendation = FloodCulvertHydraulics.Recommend(
                    3.5, CulvertShape.Box, 0.012, 0.01, 4, 1.0);
                True(recommendation != null);
                True(recommendation.CapacityCubicMetresPerSecond >= 3.5);

                var pipe = new CulvertSection(CulvertShape.Pipe, 1.2, 1.2, 2, 0.013, 0.01);
                CulvertHydraulicResult pipeReview = FloodCulvertHydraulics.Review(pipe, 2.0);
                True(pipeReview.NormalDepthMetres <= 1.2);

                Console.WriteLine("Flood hydraulics regression passed.");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                return 1;
            }
        }

        private static void Near(double expected, double actual, double tolerance)
        {
            if (Math.Abs(expected - actual) > tolerance)
                throw new InvalidOperationException("Expected " + expected + ", received " + actual + ".");
        }

        private static void True(bool condition)
        {
            if (!condition) throw new InvalidOperationException("Expected condition to be true.");
        }
    }
}

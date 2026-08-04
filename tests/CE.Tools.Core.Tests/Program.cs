using System;
using System.Collections.Generic;
using System.Linq;
using CETools.Core;

namespace CETools.Core.Tests
{
    internal static class Program
    {
        private static int _tests;

        private static int Main()
        {
            try
            {
                MaximumSpacingCreatesEqualIntervals();
                ExactMaximumDoesNotCreateExtraInterval();
                SegmentCountCreatesRequestedIntervals();
                SemicircleBulgeSplitsCorrectly();
                NegativeBulgeKeepsDirection();
                WidthInterpolationWorks();
                InvalidInputThrows();
                ExcessivePlansAreRejectedBeforeAllocation();
                PriorityFloodFillsEnclosedPit();
                FlowRouteTerminatesWithoutCycle();
                AccumulationReachesSingleOutlet();
                CatchmentContainsUpstreamCells();
                ModifiedRationalHydrographMatchesPeak();
                SystemCurveIncreasesWithFlow();
                PumpDutyPointFindsIntersection();
                PumpReviewChecksNpshMargin();

                Console.WriteLine($"CE Tools core tests passed: {_tests}");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("CE Tools core test failure:");
                Console.Error.WriteLine(exception);
                return 1;
            }
        }

        private static void MaximumSpacingCreatesEqualIntervals()
        {
            DensifyPlan plan = DensifyPlanner.ByMaximumSpacing(10.0, 3.0);
            Equal(4, plan.SegmentCount);
            Near(2.5, plan.EqualSpacing);
            Equal(3, plan.Stations.Count);
            Near(2.5, plan.Stations[0]);
            Near(5.0, plan.Stations[1]);
            Near(7.5, plan.Stations[2]);
            Pass();
        }

        private static void ExactMaximumDoesNotCreateExtraInterval()
        {
            DensifyPlan plan = DensifyPlanner.ByMaximumSpacing(10.0, 2.0);
            Equal(5, plan.SegmentCount);
            Near(2.0, plan.EqualSpacing);
            Pass();
        }

        private static void SegmentCountCreatesRequestedIntervals()
        {
            DensifyPlan plan = DensifyPlanner.BySegmentCount(12.0, 3);
            Near(4.0, plan.EqualSpacing);
            Equal(2, plan.Stations.Count);
            Near(4.0, plan.Stations[0]);
            Near(8.0, plan.Stations[1]);
            Pass();
        }

        private static void SemicircleBulgeSplitsCorrectly()
        {
            BulgeSplit split = BulgeMath.Split(1.0, 0.5);
            double expected = Math.Tan(Math.PI / 8.0);
            Near(expected, split.FirstBulge);
            Near(expected, split.SecondBulge);
            Pass();
        }

        private static void NegativeBulgeKeepsDirection()
        {
            BulgeSplit split = BulgeMath.Split(-1.0, 0.25);
            True(split.FirstBulge < 0.0);
            True(split.SecondBulge < 0.0);
            Pass();
        }

        private static void WidthInterpolationWorks()
        {
            Near(4.0, BulgeMath.Interpolate(2.0, 10.0, 0.25));
            Pass();
        }

        private static void InvalidInputThrows()
        {
            Throws<ArgumentOutOfRangeException>(() => DensifyPlanner.ByMaximumSpacing(10.0, 0.0));
            Throws<ArgumentOutOfRangeException>(() => DensifyPlanner.BySegmentCount(10.0, 0));
            Throws<ArgumentOutOfRangeException>(() => BulgeMath.Split(1.0, 1.0));
            Throws<ArgumentOutOfRangeException>(
                () => ModifiedRationalHydrograph.Create(1.0, 1.2, 50.0, 20.0, 30.0, 5.0));
            Throws<ArgumentOutOfRangeException>(
                () => PumpSystemCurve.SystemHeadMetres(
                    10.0,
                    new SystemCurveDefinition(5.0, 100.0, 0.0, 120.0, 1.0)));
            Pass();
        }

        private static void ExcessivePlansAreRejectedBeforeAllocation()
        {
            Throws<ArgumentOutOfRangeException>(
                () => DensifyPlanner.BySegmentCount(
                    10.0,
                    DensifyPlanner.MaximumSupportedSegments + 1));

            Throws<ArgumentOutOfRangeException>(
                () => DensifyPlanner.ByMaximumSpacing(
                    10.0,
                    10.0 / (DensifyPlanner.MaximumSupportedSegments + 1.0)));

            Pass();
        }

        private static void PriorityFloodFillsEnclosedPit()
        {
            double[] elevations =
            {
                10.0, 10.0, 10.0,
                10.0,  1.0, 10.0,
                10.0, 10.0, 10.0
            };
            HydrologyGridAnalysis analysis = new HydrologyGrid(
                3,
                3,
                1.0,
                elevations).Analyse();

            int centre = analysis.IndexOf(1, 1);
            Near(10.0, analysis.FilledElevations[centre]);
            Near(9.0, analysis.FillDepth(centre));
            True(analysis.FlowTo[centre] >= 0);
            Pass();
        }

        private static void FlowRouteTerminatesWithoutCycle()
        {
            HydrologyGridAnalysis analysis = CreateSingleOutletAnalysis();
            int start = analysis.IndexOf(0, 0);
            int outlet = analysis.IndexOf(2, 2);
            IReadOnlyList<GridCell> route = analysis.TraceRoute(start);

            True(route.Count >= 2);
            Equal(start, route[0].Index);
            Equal(outlet, route[route.Count - 1].Index);
            Equal(route.Count, route.Select(cell => cell.Index).Distinct().Count());
            Pass();
        }

        private static void AccumulationReachesSingleOutlet()
        {
            HydrologyGridAnalysis analysis = CreateSingleOutletAnalysis();
            int outlet = analysis.IndexOf(2, 2);

            Equal(outlet, analysis.FindMaximumAccumulationCell());
            Near(9.0, analysis.AccumulationArea[outlet]);
            Equal(-1, analysis.FlowTo[outlet]);
            Pass();
        }

        private static void CatchmentContainsUpstreamCells()
        {
            HydrologyGridAnalysis analysis = CreateSingleOutletAnalysis();
            int outlet = analysis.IndexOf(2, 2);
            IReadOnlyList<GridCell> catchment = analysis.DelineateCatchment(outlet);

            Equal(9, catchment.Count);
            True(catchment.Any(cell => cell.Index == analysis.IndexOf(0, 0)));
            True(catchment.Any(cell => cell.Index == analysis.IndexOf(1, 1)));
            True(catchment.Any(cell => cell.Index == outlet));
            Pass();
        }

        private static void ModifiedRationalHydrographMatchesPeak()
        {
            HydrographSeries series = ModifiedRationalHydrograph.Create(
                10.0,
                0.7,
                50.0,
                20.0,
                30.0,
                5.0);
            double expectedPeak = 0.7 * 50.0 * 10.0 / 360.0;
            double maximum = series.Points.Max(point => point.FlowCubicMetresPerSecond);

            Near(expectedPeak, series.PeakFlowCubicMetresPerSecond);
            Near(expectedPeak, maximum);
            Near(0.0, series.Points[0].FlowCubicMetresPerSecond);
            Near(0.0, series.Points[series.Points.Count - 1].FlowCubicMetresPerSecond);
            Near(50.0, series.Points[series.Points.Count - 1].TimeMinutes);
            Pass();
        }

        private static void SystemCurveIncreasesWithFlow()
        {
            var definition = new SystemCurveDefinition(
                8.0,
                1200.0,
                0.25,
                130.0,
                3.5);
            double zero = PumpSystemCurve.SystemHeadMetres(0.0, definition);
            double ten = PumpSystemCurve.SystemHeadMetres(10.0, definition);
            double twenty = PumpSystemCurve.SystemHeadMetres(20.0, definition);

            Near(8.0, zero);
            True(ten > zero);
            True(twenty > ten);
            Pass();
        }

        private static void PumpDutyPointFindsIntersection()
        {
            var pump = new[]
            {
                new PumpCurvePoint(0.0, 20.0, 70.0, 5.0, 2.0),
                new PumpCurvePoint(20.0, 0.0, 80.0, 9.0, 4.0)
            };
            var system = new SystemCurveDefinition(
                12.0,
                0.0,
                0.2,
                130.0,
                0.0);
            PumpDutyPoint duty = PumpSystemCurve.FindDutyPoint(pump, system);

            True(duty != null);
            Near(8.0, duty.FlowLitresPerSecond);
            Near(12.0, duty.PumpHeadMetres);
            Near(12.0, duty.SystemHeadMetres);
            Near(74.0, duty.EfficiencyPercent.Value);
            Near(6.6, duty.PowerKilowatts.Value);
            Near(2.8, duty.NpshRequiredMetres.Value);
            Pass();
        }

        private static void PumpReviewChecksNpshMargin()
        {
            var pump = new[]
            {
                new PumpCurvePoint(0.0, 20.0, 70.0, 5.0, 2.0),
                new PumpCurvePoint(20.0, 0.0, 80.0, 9.0, 4.0)
            };
            var system = new SystemCurveDefinition(
                12.0,
                0.0,
                0.2,
                130.0,
                0.0);
            PumpSuitabilityReview pass = PumpSystemCurve.Review(pump, system, 4.0, 1.0);
            PumpSuitabilityReview fail = PumpSystemCurve.Review(pump, system, 3.2, 1.0);

            True(pass.DutyPoint != null);
            True(pass.NpshPass);
            Near(1.2, pass.NpshMarginMetres.Value);
            True(!fail.NpshPass);
            Near(0.4, fail.NpshMarginMetres.Value);
            Pass();
        }

        private static HydrologyGridAnalysis CreateSingleOutletAnalysis()
        {
            double[] elevations =
            {
                9.0, 8.0, 7.0,
                8.0, 7.0, 6.0,
                7.0, 6.0, 5.0
            };
            return new HydrologyGrid(3, 3, 1.0, elevations).Analyse();
        }

        private static void Pass()
        {
            _tests++;
        }

        private static void Near(double expected, double actual, double tolerance = 1e-10)
        {
            if (Math.Abs(expected - actual) > tolerance)
            {
                throw new InvalidOperationException($"Expected {expected}, received {actual}.");
            }
        }

        private static void Equal(int expected, int actual)
        {
            if (expected != actual)
            {
                throw new InvalidOperationException($"Expected {expected}, received {actual}.");
            }
        }

        private static void True(bool condition)
        {
            if (!condition)
            {
                throw new InvalidOperationException("Expected condition to be true.");
            }
        }

        private static void Throws<T>(Action action) where T : Exception
        {
            try
            {
                action();
            }
            catch (T)
            {
                return;
            }

            throw new InvalidOperationException($"Expected exception {typeof(T).Name}.");
        }
    }
}

using System;
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
            Pass();
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

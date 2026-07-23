using System;
using System.Collections.Generic;

namespace CETools.Core
{
    /// <summary>
    /// Defines the interior equal-chainage stations to insert along a curve.
    /// </summary>
    public sealed class DensifyPlan
    {
        public DensifyPlan(
            double totalLength,
            int segmentCount,
            double equalSpacing,
            IReadOnlyList<double> stations)
        {
            TotalLength = totalLength;
            SegmentCount = segmentCount;
            EqualSpacing = equalSpacing;
            Stations = stations ?? throw new ArgumentNullException(nameof(stations));
        }

        public double TotalLength { get; }

        public int SegmentCount { get; }

        public double EqualSpacing { get; }

        /// <summary>
        /// Interior stations only. Zero and the end station are excluded.
        /// </summary>
        public IReadOnlyList<double> Stations { get; }
    }

    /// <summary>
    /// Builds equal-chainage densification plans without Autodesk dependencies.
    /// </summary>
    public static class DensifyPlanner
    {
        private const double RatioTolerance = 1e-10;

        public static DensifyPlan ByMaximumSpacing(double totalLength, double maximumSpacing)
        {
            ValidatePositiveFinite(totalLength, nameof(totalLength));
            ValidatePositiveFinite(maximumSpacing, nameof(maximumSpacing));

            double ratio = totalLength / maximumSpacing;
            if (ratio > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maximumSpacing),
                    "The requested spacing would create more segments than this version supports.");
            }

            int segmentCount = Math.Max(1, (int)Math.Ceiling(ratio - RatioTolerance));
            return Build(totalLength, segmentCount);
        }

        public static DensifyPlan BySegmentCount(double totalLength, int segmentCount)
        {
            ValidatePositiveFinite(totalLength, nameof(totalLength));

            if (segmentCount < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(segmentCount),
                    "Segment count must be at least one.");
            }

            return Build(totalLength, segmentCount);
        }

        private static DensifyPlan Build(double totalLength, int segmentCount)
        {
            double spacing = totalLength / segmentCount;
            var stations = new List<double>(Math.Max(0, segmentCount - 1));

            for (int index = 1; index < segmentCount; index++)
            {
                stations.Add(spacing * index);
            }

            return new DensifyPlan(totalLength, segmentCount, spacing, stations);
        }

        private static void ValidatePositiveFinite(double value, string parameterName)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0.0)
            {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    "Value must be finite and greater than zero.");
            }
        }
    }

    /// <summary>
    /// Mathematics used when an AutoCAD lightweight-polyline arc is split.
    /// </summary>
    public static class BulgeMath
    {
        public static BulgeSplit Split(double originalBulge, double distanceFraction)
        {
            if (double.IsNaN(originalBulge) || double.IsInfinity(originalBulge))
            {
                throw new ArgumentOutOfRangeException(nameof(originalBulge));
            }

            if (double.IsNaN(distanceFraction) ||
                double.IsInfinity(distanceFraction) ||
                distanceFraction <= 0.0 ||
                distanceFraction >= 1.0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(distanceFraction),
                    "Split fraction must be strictly between zero and one.");
            }

            if (Math.Abs(originalBulge) < 1e-15)
            {
                return new BulgeSplit(0.0, 0.0);
            }

            // AutoCAD bulge = tan(included angle / 4). Arc distance is linear
            // with included angle, so the distance fraction splits the angle.
            double quarterAngle = Math.Atan(originalBulge);
            double first = Math.Tan(quarterAngle * distanceFraction);
            double second = Math.Tan(quarterAngle * (1.0 - distanceFraction));

            return new BulgeSplit(first, second);
        }

        public static double Interpolate(double startValue, double endValue, double fraction)
        {
            return startValue + ((endValue - startValue) * fraction);
        }
    }

    public struct BulgeSplit
    {
        public BulgeSplit(double firstBulge, double secondBulge)
        {
            FirstBulge = firstBulge;
            SecondBulge = secondBulge;
        }

        public double FirstBulge { get; }

        public double SecondBulge { get; }
    }
}

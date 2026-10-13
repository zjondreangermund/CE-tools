using System;
using System.Collections.Generic;

namespace CETools.Core
{
    public sealed class RoadSheetSegment
    {
        public RoadSheetSegment(int index, double startStation, double endStation)
        {
            Index = index;
            StartStation = startStation;
            EndStation = endStation;
        }

        public int Index { get; }
        public double StartStation { get; }
        public double EndStation { get; }
        public double Length => EndStation - StartStation;
    }

    public sealed class RoadSheetPlan
    {
        public RoadSheetPlan(int sheetIndex, IReadOnlyList<RoadSheetSegment> segments)
        {
            SheetIndex = sheetIndex;
            Segments = segments ?? throw new ArgumentNullException(nameof(segments));
        }

        public int SheetIndex { get; }
        public IReadOnlyList<RoadSheetSegment> Segments { get; }
    }

    public static class RoadSheetingPlanner
    {
        public static double DefaultSegmentLength(string sheetSize)
        {
            if (string.Equals(sheetSize, "A0", StringComparison.OrdinalIgnoreCase))
                return 2000.0;
            if (string.Equals(sheetSize, "A1", StringComparison.OrdinalIgnoreCase))
                return 1000.0;
            throw new ArgumentOutOfRangeException(nameof(sheetSize), "Supported road sheet sizes are A0 and A1.");
        }

        public static IReadOnlyList<RoadSheetPlan> Create(
            double startStation,
            double endStation,
            double segmentLength,
            int segmentsPerSheet)
        {
            if (double.IsNaN(startStation) || double.IsInfinity(startStation))
                throw new ArgumentOutOfRangeException(nameof(startStation));
            if (double.IsNaN(endStation) || double.IsInfinity(endStation) ||
                endStation <= startStation)
                throw new ArgumentOutOfRangeException(nameof(endStation));
            if (double.IsNaN(segmentLength) || double.IsInfinity(segmentLength) ||
                segmentLength <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(segmentLength));
            if (segmentsPerSheet <= 0 || segmentsPerSheet > 8)
                throw new ArgumentOutOfRangeException(nameof(segmentsPerSheet));

            var segments = new List<RoadSheetSegment>();
            int segmentIndex = 0;
            for (double station = startStation;
                 station < endStation - 1e-9;
                 station += segmentLength)
            {
                double next = Math.Min(station + segmentLength, endStation);
                segments.Add(new RoadSheetSegment(segmentIndex++, station, next));
            }

            var sheets = new List<RoadSheetPlan>();
            for (int index = 0; index < segments.Count; index += segmentsPerSheet)
            {
                int count = Math.Min(segmentsPerSheet, segments.Count - index);
                sheets.Add(new RoadSheetPlan(
                    sheets.Count,
                    segments.GetRange(index, count).AsReadOnly()));
            }
            return sheets.AsReadOnly();
        }

        public static IReadOnlyList<RoadSheetSegment> SplitPlanByHeading(
            IReadOnlyList<double> stations,
            IReadOnlyList<double> headingsRadians,
            double maximumHeadingChangeDegrees,
            double minimumSegmentLength,
            int maximumSegments)
        {
            if (stations == null) throw new ArgumentNullException(nameof(stations));
            if (headingsRadians == null) throw new ArgumentNullException(nameof(headingsRadians));
            if (stations.Count != headingsRadians.Count || stations.Count < 2)
                throw new ArgumentException("Road heading samples must contain matching station and heading values.");
            if (double.IsNaN(maximumHeadingChangeDegrees) ||
                double.IsInfinity(maximumHeadingChangeDegrees) ||
                maximumHeadingChangeDegrees <= 0.0 ||
                maximumHeadingChangeDegrees >= 90.0)
                throw new ArgumentOutOfRangeException(nameof(maximumHeadingChangeDegrees));
            if (double.IsNaN(minimumSegmentLength) ||
                double.IsInfinity(minimumSegmentLength) ||
                minimumSegmentLength <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(minimumSegmentLength));
            if (maximumSegments < 1 || maximumSegments > 12)
                throw new ArgumentOutOfRangeException(nameof(maximumSegments));

            for (int index = 1; index < stations.Count; index++)
            {
                if (stations[index] <= stations[index - 1])
                    throw new ArgumentException("Road heading sample stations must increase.");
            }

            double start = stations[0];
            double end = stations[stations.Count - 1];
            if (end - start <= minimumSegmentLength * 1.25)
                return new[]
                {
                    new RoadSheetSegment(0, start, end)
                };

            double tolerance = maximumHeadingChangeDegrees * Math.PI / 180.0;
            var result = new List<RoadSheetSegment>();
            int segmentStartIndex = 0;

            for (int index = 1; index < stations.Count; index++)
            {
                if (result.Count + 1 >= maximumSegments)
                    break;

                double reference = headingsRadians[segmentStartIndex];
                double deviation = SmallestAxisAngleDifference(
                    reference,
                    headingsRadians[index]);

                if (deviation <= tolerance)
                    continue;

                int splitIndex = Math.Max(segmentStartIndex + 1, index - 1);
                double splitStation = stations[splitIndex];
                double currentStart = stations[segmentStartIndex];

                // Do not create tiny plan windows around short curves. When the
                // first candidate is too close to the start, keep sampling until
                // the minimum useful paper/model length has been reached.
                if (splitStation - currentStart < minimumSegmentLength)
                    continue;

                // Also protect the final remainder. A curve near the end belongs
                // in the current viewport when it would leave a tiny last panel.
                if (end - splitStation < minimumSegmentLength)
                    break;

                result.Add(new RoadSheetSegment(
                    result.Count,
                    currentStart,
                    splitStation));
                segmentStartIndex = splitIndex;
            }

            double finalStart = stations[segmentStartIndex];
            if (result.Count == 0 || end - finalStart > 1e-9)
            {
                result.Add(new RoadSheetSegment(
                    result.Count,
                    finalStart,
                    end));
            }

            return result.AsReadOnly();
        }

        public static double SmallestAxisAngleDifference(
            double firstRadians,
            double secondRadians)
        {
            if (double.IsNaN(firstRadians) || double.IsInfinity(firstRadians))
                throw new ArgumentOutOfRangeException(nameof(firstRadians));
            if (double.IsNaN(secondRadians) || double.IsInfinity(secondRadians))
                throw new ArgumentOutOfRangeException(nameof(secondRadians));

            // A road reads horizontally whether its tangent points left or right,
            // so compare directions as an axis (180 degrees), not a vector
            // (360 degrees).
            double halfTurn = Math.PI;
            double difference = Math.Abs(firstRadians - secondRadians) % halfTurn;
            if (difference > halfTurn * 0.5)
                difference = halfTurn - difference;
            return difference;
        }

        public static string StationText(double station)
        {
            if (double.IsNaN(station) || double.IsInfinity(station))
                throw new ArgumentOutOfRangeException(nameof(station));
            double rounded = Math.Round(station, 3, MidpointRounding.AwayFromZero);
            int kilometres = (int)Math.Floor(Math.Abs(rounded) / 1000.0);
            double metres = Math.Abs(rounded) - kilometres * 1000.0;
            string sign = rounded < 0.0 ? "-" : string.Empty;
            return sign + kilometres.ToString("0") + "+" + metres.ToString("000.###");
        }
    }
}

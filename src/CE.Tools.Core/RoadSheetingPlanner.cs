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

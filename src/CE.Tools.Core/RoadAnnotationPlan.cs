using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace CETools.Core
{
    /// <summary>Plan-reading rules shared by road annotation and setting-out.</summary>
    public static class RoadAnnotationPlan
    {
        public static int RoadNumber(string name)
        {
            Match match = Regex.Match(name ?? string.Empty,
                @"(?:^|[^A-Z0-9])(?:ROAD|RD)[\s_\-]*0*(\d+)(?=$|[^0-9])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            int value;
            return match.Success && int.TryParse(match.Groups[1].Value,
                NumberStyles.None, CultureInfo.InvariantCulture, out value) && value > 0 ? value : 0;
        }

        // Includes the end sections between a road end and its first/last junction.
        // A junction at an endpoint and repeated hits from adjoining roads produce one break.
        public static IList<double> SectionMidpoints(double length, IEnumerable<double> junctions)
        {
            if (double.IsNaN(length) || double.IsInfinity(length) || length <= 1e-7)
                return new List<double>();
            var breaks = new List<double> { 0.0 };
            foreach (double station in (junctions ?? Enumerable.Empty<double>())
                .Where(x => !double.IsNaN(x) && !double.IsInfinity(x) && x > 1e-5 && x < length - 1e-5)
                .OrderBy(x => x))
                if (station - breaks[breaks.Count - 1] > 1e-5) breaks.Add(station);
            breaks.Add(length);
            return breaks.Zip(breaks.Skip(1), (a, b) => (a + b) * 0.5).ToList();
        }

        public static double ClockwiseFromTopLeft(double x, double y)
        {
            // Start at the west boundary of the NW quadrant: NW, NE, SE, SW.
            // Starting at exactly 135 degrees incorrectly sends some NW returns to the end.
            double angle = Math.PI - Math.Atan2(y, x);
            return (angle + Math.PI * 2.0) % (Math.PI * 2.0);
        }

        public static double ReadableAngle(double angle)
        {
            while (angle > Math.PI / 2.0) angle -= Math.PI;
            while (angle < -Math.PI / 2.0) angle += Math.PI;
            return angle;
        }
    }

    public sealed class RoadPointSequence
    {
        private readonly Dictionary<int, int> next = new Dictionary<int, int>();

        public RoadPointSequence(IDictionary<int, int> starts = null)
        {
            if (starts != null)
                foreach (var pair in starts) next[pair.Key] = Math.Max(1, pair.Value);
        }

        public string Next(string prefix, int road)
        {
            if (road <= 0) throw new ArgumentOutOfRangeException(nameof(road), "Assign an owning road before numbering.");
            int point;
            if (!next.TryGetValue(road, out point)) point = 1;
            next[road] = point + 1;
            return prefix + road.ToString(CultureInfo.InvariantCulture) + "." + point.ToString(CultureInfo.InvariantCulture);
        }
    }
}

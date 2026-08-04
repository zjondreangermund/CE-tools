using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CETools.Core
{
    /// <summary>
    /// Host-independent analysis of imported flood result points against property
    /// polygons and scenario/time frames. Point samples do not define a continuous
    /// flood surface and must not be treated as legal flood lines or certified hazard.
    /// </summary>
    public static class FloodResultAnalyzer
    {
        public static FloodAnalysisResult Analyse(
            IEnumerable<FloodProperty> properties,
            IEnumerable<FloodResultPoint> results,
            double minimumDepthMetres)
        {
            if (properties == null) throw new ArgumentNullException(nameof(properties));
            if (results == null) throw new ArgumentNullException(nameof(results));
            if (double.IsNaN(minimumDepthMetres) || double.IsInfinity(minimumDepthMetres) || minimumDepthMetres < 0.0)
                throw new ArgumentOutOfRangeException(nameof(minimumDepthMetres));

            List<FloodProperty> propertyList = properties.ToList();
            List<FloodResultPoint> resultList = results.ToList();
            for (int index = 0; index < propertyList.Count; index++)
                propertyList[index].Validate(index);
            for (int index = 0; index < resultList.Count; index++)
                resultList[index].Validate(index);

            List<FloodFrame> frames = resultList
                .GroupBy(item => new FloodFrameKey(item.Scenario, item.Time))
                .Select(group => new FloodFrame(group.Key, group.ToList()))
                .OrderBy(item => item.Key.Scenario, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(item => item.Key.SortTime)
                .ThenBy(item => item.Key.Time, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            var summaries = new List<FloodPropertyFrameSummary>();
            foreach (FloodProperty property in propertyList)
            {
                ParkingBounds bounds = property.Polygon.Bounds;
                foreach (FloodFrame frame in frames)
                {
                    List<FloodResultPoint> inside = frame.Points
                        .Where(point => point.X >= bounds.MinX && point.X <= bounds.MaxX &&
                                        point.Y >= bounds.MinY && point.Y <= bounds.MaxY &&
                                        property.Polygon.Contains(new ParkingPoint(point.X, point.Y), true))
                        .ToList();
                    List<FloodResultPoint> wet = inside
                        .Where(point => point.DepthMetres.HasValue && point.DepthMetres.Value >= minimumDepthMetres)
                        .ToList();
                    summaries.Add(new FloodPropertyFrameSummary(
                        property.Id,
                        frame.Key,
                        inside.Count,
                        wet.Count,
                        Maximum(wet.Select(item => item.DepthMetres)),
                        Maximum(wet.Select(item => item.VelocityMetresPerSecond)),
                        Maximum(wet.Select(item => item.WaterLevelMetres)),
                        Maximum(wet.Select(item => item.HazardIndex)),
                        Average(wet.Select(item => item.DepthMetres)),
                        wet.Count > 0));
                }
            }

            List<FloodPropertySummary> propertySummaries = propertyList
                .Select(property =>
                {
                    List<FloodPropertyFrameSummary> items = summaries
                        .Where(item => string.Equals(item.PropertyId, property.Id, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    FloodPropertyFrameSummary peak = items
                        .OrderByDescending(item => item.MaximumHazardIndex ?? -1.0)
                        .ThenByDescending(item => item.MaximumDepthMetres ?? -1.0)
                        .ThenByDescending(item => item.WetPointCount)
                        .FirstOrDefault();
                    FloodPropertyFrameSummary firstWet = items
                        .Where(item => item.Affected)
                        .OrderBy(item => item.Frame.SortTime)
                        .ThenBy(item => item.Frame.Time, StringComparer.CurrentCultureIgnoreCase)
                        .FirstOrDefault();
                    return new FloodPropertySummary(
                        property.Id,
                        items.Count(item => item.Affected),
                        Maximum(items.Select(item => item.MaximumDepthMetres)),
                        Maximum(items.Select(item => item.MaximumVelocityMetresPerSecond)),
                        Maximum(items.Select(item => item.MaximumWaterLevelMetres)),
                        Maximum(items.Select(item => item.MaximumHazardIndex)),
                        firstWet == null ? null : firstWet.Frame,
                        peak == null ? null : peak.Frame);
                })
                .ToList();

            return new FloodAnalysisResult(
                propertyList,
                frames,
                summaries,
                propertySummaries,
                minimumDepthMetres,
                ComputeBounds(propertyList, resultList));
        }

        private static FloodBounds ComputeBounds(
            IList<FloodProperty> properties,
            IList<FloodResultPoint> results)
        {
            var x = new List<double>();
            var y = new List<double>();
            foreach (FloodProperty property in properties)
            {
                x.AddRange(property.Polygon.Vertices.Select(item => item.X));
                y.AddRange(property.Polygon.Vertices.Select(item => item.Y));
            }
            x.AddRange(results.Select(item => item.X));
            y.AddRange(results.Select(item => item.Y));
            return x.Count == 0
                ? new FloodBounds(0.0, 0.0, 1.0, 1.0)
                : new FloodBounds(x.Min(), y.Min(), x.Max(), y.Max());
        }

        private static double? Maximum(IEnumerable<double?> values)
        {
            List<double> present = values.Where(item => item.HasValue).Select(item => item.Value).ToList();
            return present.Count == 0 ? (double?)null : present.Max();
        }

        private static double? Average(IEnumerable<double?> values)
        {
            List<double> present = values.Where(item => item.HasValue).Select(item => item.Value).ToList();
            return present.Count == 0 ? (double?)null : present.Average();
        }
    }

    public sealed class FloodProperty
    {
        public FloodProperty(string id, ParkingPolygon polygon)
        {
            Id = id ?? string.Empty;
            Polygon = polygon;
        }
        public string Id { get; private set; }
        public ParkingPolygon Polygon { get; private set; }
        public void Validate(int index)
        {
            if (string.IsNullOrWhiteSpace(Id))
                throw new ArgumentException("Property ID is missing at index " + index.ToString(CultureInfo.InvariantCulture));
            if (Polygon == null) throw new ArgumentNullException("Property polygon " + Id);
            Polygon.Validate("property " + Id);
        }
    }

    public sealed class FloodResultPoint
    {
        public FloodResultPoint(
            double x,
            double y,
            double? z,
            double? depthMetres,
            double? velocityMetresPerSecond,
            double? waterLevelMetres,
            double? hazardIndex,
            string scenario,
            string time,
            string sourceHandle)
        {
            X = x; Y = y; Z = z; DepthMetres = depthMetres;
            VelocityMetresPerSecond = velocityMetresPerSecond;
            WaterLevelMetres = waterLevelMetres; HazardIndex = hazardIndex;
            Scenario = scenario ?? string.Empty; Time = time ?? string.Empty;
            SourceHandle = sourceHandle ?? string.Empty;
        }
        public double X { get; private set; }
        public double Y { get; private set; }
        public double? Z { get; private set; }
        public double? DepthMetres { get; private set; }
        public double? VelocityMetresPerSecond { get; private set; }
        public double? WaterLevelMetres { get; private set; }
        public double? HazardIndex { get; private set; }
        public string Scenario { get; private set; }
        public string Time { get; private set; }
        public string SourceHandle { get; private set; }
        public void Validate(int index)
        {
            Finite(X, "X", index); Finite(Y, "Y", index);
            OptionalFinite(Z, "Z", index); OptionalFinite(DepthMetres, "Depth", index);
            OptionalFinite(VelocityMetresPerSecond, "Velocity", index);
            OptionalFinite(WaterLevelMetres, "WaterLevel", index);
            OptionalFinite(HazardIndex, "HazardIndex", index);
        }
        private static void Finite(double value, string name, int index)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                throw new ArgumentOutOfRangeException(name, "Invalid flood point at index " + index);
        }
        private static void OptionalFinite(double? value, string name, int index)
        {
            if (value.HasValue) Finite(value.Value, name, index);
        }
    }

    public sealed class FloodFrameKey : IEquatable<FloodFrameKey>
    {
        public FloodFrameKey(string scenario, string time)
        {
            Scenario = string.IsNullOrWhiteSpace(scenario) ? "<Unspecified>" : scenario.Trim();
            Time = string.IsNullOrWhiteSpace(time) ? "<Unspecified>" : time.Trim();
            TimeSpan parsed;
            SortTime = TimeSpan.TryParse(Time, CultureInfo.InvariantCulture, out parsed)
                ? parsed.TotalSeconds : double.MaxValue;
        }
        public string Scenario { get; private set; }
        public string Time { get; private set; }
        public double SortTime { get; private set; }
        public bool Equals(FloodFrameKey other)
        {
            return other != null &&
                string.Equals(Scenario, other.Scenario, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Time, other.Time, StringComparison.OrdinalIgnoreCase);
        }
        public override bool Equals(object obj) { return Equals(obj as FloodFrameKey); }
        public override int GetHashCode()
        {
            return StringComparer.OrdinalIgnoreCase.GetHashCode(Scenario) * 397 ^
                StringComparer.OrdinalIgnoreCase.GetHashCode(Time);
        }
        public override string ToString() { return Scenario + " | " + Time; }
    }

    public sealed class FloodFrame
    {
        public FloodFrame(FloodFrameKey key, IReadOnlyList<FloodResultPoint> points)
        { Key = key; Points = points; }
        public FloodFrameKey Key { get; private set; }
        public IReadOnlyList<FloodResultPoint> Points { get; private set; }
    }

    public sealed class FloodPropertyFrameSummary
    {
        public FloodPropertyFrameSummary(
            string propertyId,
            FloodFrameKey frame,
            int pointCount,
            int wetPointCount,
            double? maximumDepthMetres,
            double? maximumVelocityMetresPerSecond,
            double? maximumWaterLevelMetres,
            double? maximumHazardIndex,
            double? averageDepthMetres,
            bool affected)
        {
            PropertyId = propertyId; Frame = frame; PointCount = pointCount;
            WetPointCount = wetPointCount; MaximumDepthMetres = maximumDepthMetres;
            MaximumVelocityMetresPerSecond = maximumVelocityMetresPerSecond;
            MaximumWaterLevelMetres = maximumWaterLevelMetres;
            MaximumHazardIndex = maximumHazardIndex;
            AverageDepthMetres = averageDepthMetres; Affected = affected;
        }
        public string PropertyId { get; private set; }
        public FloodFrameKey Frame { get; private set; }
        public int PointCount { get; private set; }
        public int WetPointCount { get; private set; }
        public double? MaximumDepthMetres { get; private set; }
        public double? MaximumVelocityMetresPerSecond { get; private set; }
        public double? MaximumWaterLevelMetres { get; private set; }
        public double? MaximumHazardIndex { get; private set; }
        public double? AverageDepthMetres { get; private set; }
        public bool Affected { get; private set; }
    }

    public sealed class FloodPropertySummary
    {
        public FloodPropertySummary(
            string propertyId,
            int affectedFrameCount,
            double? maximumDepthMetres,
            double? maximumVelocityMetresPerSecond,
            double? maximumWaterLevelMetres,
            double? maximumHazardIndex,
            FloodFrameKey firstAffectedFrame,
            FloodFrameKey peakFrame)
        {
            PropertyId = propertyId; AffectedFrameCount = affectedFrameCount;
            MaximumDepthMetres = maximumDepthMetres;
            MaximumVelocityMetresPerSecond = maximumVelocityMetresPerSecond;
            MaximumWaterLevelMetres = maximumWaterLevelMetres;
            MaximumHazardIndex = maximumHazardIndex;
            FirstAffectedFrame = firstAffectedFrame; PeakFrame = peakFrame;
        }
        public string PropertyId { get; private set; }
        public int AffectedFrameCount { get; private set; }
        public double? MaximumDepthMetres { get; private set; }
        public double? MaximumVelocityMetresPerSecond { get; private set; }
        public double? MaximumWaterLevelMetres { get; private set; }
        public double? MaximumHazardIndex { get; private set; }
        public FloodFrameKey FirstAffectedFrame { get; private set; }
        public FloodFrameKey PeakFrame { get; private set; }
    }

    public sealed class FloodBounds
    {
        public FloodBounds(double minX, double minY, double maxX, double maxY)
        { MinX = minX; MinY = minY; MaxX = maxX; MaxY = maxY; }
        public double MinX { get; private set; }
        public double MinY { get; private set; }
        public double MaxX { get; private set; }
        public double MaxY { get; private set; }
        public double Width { get { return Math.Max(1e-9, MaxX - MinX); } }
        public double Height { get { return Math.Max(1e-9, MaxY - MinY); } }
    }

    public sealed class FloodAnalysisResult
    {
        public FloodAnalysisResult(
            IReadOnlyList<FloodProperty> properties,
            IReadOnlyList<FloodFrame> frames,
            IReadOnlyList<FloodPropertyFrameSummary> propertyFrames,
            IReadOnlyList<FloodPropertySummary> propertySummaries,
            double minimumDepthMetres,
            FloodBounds bounds)
        {
            Properties = properties; Frames = frames; PropertyFrames = propertyFrames;
            PropertySummaries = propertySummaries; MinimumDepthMetres = minimumDepthMetres;
            Bounds = bounds;
        }
        public IReadOnlyList<FloodProperty> Properties { get; private set; }
        public IReadOnlyList<FloodFrame> Frames { get; private set; }
        public IReadOnlyList<FloodPropertyFrameSummary> PropertyFrames { get; private set; }
        public IReadOnlyList<FloodPropertySummary> PropertySummaries { get; private set; }
        public double MinimumDepthMetres { get; private set; }
        public FloodBounds Bounds { get; private set; }
    }
}

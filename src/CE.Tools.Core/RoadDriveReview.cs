using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CETools.Core
{
    public static class RoadDriveReviewer
    {
        private const double Gravity = 9.80665;
        private const double Tolerance = 1e-9;

        public static RoadDriveAnalysis Analyse(IEnumerable<RoadDriveSample> samples, RoadDriveCriteria criteria)
        {
            if (samples == null) throw new ArgumentNullException(nameof(samples));
            if (criteria == null) throw new ArgumentNullException(nameof(criteria));
            criteria.Validate();
            List<RoadDriveSample> ordered = samples.OrderBy(item => item.Station).ToList();
            if (ordered.Count < 3) throw new ArgumentException("At least three road-drive samples are required.", nameof(samples));
            for (int index = 0; index < ordered.Count; index++)
            {
                ordered[index].Validate(index);
                if (index > 0 && ordered[index].Station <= ordered[index - 1].Station)
                    throw new ArgumentException("Road-drive sample stations must be strictly increasing.", nameof(samples));
            }
            double speedMetresPerSecond = criteria.DesignSpeedKilometresPerHour / 3.6;
            double speedRadius = speedMetresPerSecond * speedMetresPerSecond / (Gravity * criteria.MaximumLateralAccelerationRatio);
            double requiredRadius = Math.Max(criteria.MinimumHorizontalRadiusMetres, speedRadius);
            double stoppingSightDistance = speedMetresPerSecond * criteria.ReactionTimeSeconds + speedMetresPerSecond * speedMetresPerSecond / (2.0 * criteria.BrakingDecelerationMetresPerSecondSquared);
            var segmentGrades = new List<RoadDriveSegment>();
            var issues = new List<RoadDriveIssue>();
            double maximumAbsoluteGrade = 0.0;
            double minimumRadius = double.PositiveInfinity;
            double maximumLateralRatio = 0.0;
            double maximumGradeChangeRate = 0.0;
            for (int index = 1; index < ordered.Count; index++)
            {
                RoadDriveSample first = ordered[index - 1];
                RoadDriveSample second = ordered[index];
                double plan = PlanDistance(first, second);
                if (plan <= Tolerance)
                {
                    issues.Add(new RoadDriveIssue(second.Station, RoadDriveIssueType.Geometry, RoadDriveSeverity.Error, 0.0, 0.0, "Consecutive samples have no usable plan distance."));
                    segmentGrades.Add(new RoadDriveSegment(first.Station, second.Station, plan, 0.0));
                    continue;
                }
                double grade = (second.Z - first.Z) / plan * 100.0;
                maximumAbsoluteGrade = Math.Max(maximumAbsoluteGrade, Math.Abs(grade));
                segmentGrades.Add(new RoadDriveSegment(first.Station, second.Station, plan, grade));
                if (Math.Abs(grade) > criteria.MaximumAbsoluteGradePercent + Tolerance)
                    issues.Add(new RoadDriveIssue((first.Station + second.Station) * 0.5, RoadDriveIssueType.Grade, RoadDriveSeverity.Warning, Math.Abs(grade), criteria.MaximumAbsoluteGradePercent, "Absolute longitudinal grade exceeds the selected screening limit."));
            }
            var radii = Enumerable.Repeat(double.PositiveInfinity, ordered.Count).ToArray();
            var lateralRatios = new double[ordered.Count];
            var gradeChangeRates = new double[ordered.Count];
            for (int index = 1; index < ordered.Count - 1; index++)
            {
                RoadDriveSample previous = ordered[index - 1];
                RoadDriveSample current = ordered[index];
                RoadDriveSample next = ordered[index + 1];
                double radius = Circumradius(previous, current, next);
                radii[index] = radius;
                if (!double.IsInfinity(radius))
                {
                    minimumRadius = Math.Min(minimumRadius, radius);
                    double lateralRatio = speedMetresPerSecond * speedMetresPerSecond / (Gravity * radius);
                    lateralRatios[index] = lateralRatio;
                    maximumLateralRatio = Math.Max(maximumLateralRatio, lateralRatio);
                    if (radius + Tolerance < requiredRadius)
                        issues.Add(new RoadDriveIssue(current.Station, RoadDriveIssueType.HorizontalRadius, RoadDriveSeverity.Warning, radius, requiredRadius, "Horizontal radius is below the selected/speed-based screening radius."));
                    if (lateralRatio > criteria.MaximumLateralAccelerationRatio + Tolerance)
                        issues.Add(new RoadDriveIssue(current.Station, RoadDriveIssueType.LateralAcceleration, RoadDriveSeverity.Warning, lateralRatio, criteria.MaximumLateralAccelerationRatio, "Speed-based lateral acceleration ratio exceeds the screening limit."));
                }
                RoadDriveSegment incoming = segmentGrades[index - 1];
                RoadDriveSegment outgoing = segmentGrades[index];
                double referenceLength = Math.Max(Tolerance, (incoming.PlanLengthMetres + outgoing.PlanLengthMetres) * 0.5);
                double gradeChangeRate = Math.Abs(outgoing.GradePercent - incoming.GradePercent) / referenceLength * 100.0;
                gradeChangeRates[index] = gradeChangeRate;
                maximumGradeChangeRate = Math.Max(maximumGradeChangeRate, gradeChangeRate);
                if (gradeChangeRate > criteria.MaximumGradeChangePercentPer100Metres + Tolerance)
                    issues.Add(new RoadDriveIssue(current.Station, RoadDriveIssueType.GradeChange, RoadDriveSeverity.Review, gradeChangeRate, criteria.MaximumGradeChangePercentPer100Metres, "Change in grade between adjacent sampled segments exceeds the screening limit."));
            }
            List<RoadCameraFrame> cameraFrames = BuildCameraFrames(ordered);
            return new RoadDriveAnalysis(ordered, segmentGrades, radii, lateralRatios, gradeChangeRates, cameraFrames, issues.OrderBy(item => item.Station).ThenBy(item => item.Type).ToList(), requiredRadius, stoppingSightDistance, maximumAbsoluteGrade, double.IsInfinity(minimumRadius) ? (double?)null : minimumRadius, maximumLateralRatio, maximumGradeChangeRate);
        }

        private static List<RoadCameraFrame> BuildCameraFrames(IReadOnlyList<RoadDriveSample> samples)
        {
            var frames = new List<RoadCameraFrame>();
            for (int index = 0; index < samples.Count; index++)
            {
                RoadDriveSample origin = samples[index];
                RoadDriveSample target = index < samples.Count - 1 ? samples[index + 1] : samples[index - 1];
                double dx = target.X - origin.X, dy = target.Y - origin.Y, dz = target.Z - origin.Z;
                if (index == samples.Count - 1) { dx = -dx; dy = -dy; dz = -dz; }
                double plan = Math.Sqrt(dx * dx + dy * dy);
                double heading = Math.Atan2(dy, dx) * 180.0 / Math.PI;
                if (heading < 0.0) heading += 360.0;
                double pitch = Math.Atan2(dz, Math.Max(Tolerance, plan)) * 180.0 / Math.PI;
                frames.Add(new RoadCameraFrame(origin.Station, origin.X, origin.Y, origin.Z, heading, pitch));
            }
            return frames;
        }

        private static double Circumradius(RoadDriveSample first, RoadDriveSample second, RoadDriveSample third)
        {
            double a = PlanDistance(second, third), b = PlanDistance(first, third), c = PlanDistance(first, second);
            if (a <= Tolerance || b <= Tolerance || c <= Tolerance) return double.PositiveInfinity;
            double twiceArea = Math.Abs((second.X - first.X) * (third.Y - first.Y) - (second.Y - first.Y) * (third.X - first.X));
            if (twiceArea <= Tolerance) return double.PositiveInfinity;
            return a * b * c / (2.0 * twiceArea);
        }

        private static double PlanDistance(RoadDriveSample first, RoadDriveSample second)
        {
            double dx = second.X - first.X, dy = second.Y - first.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        internal static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }

    public sealed class RoadDriveCriteria
    {
        public RoadDriveCriteria(double designSpeedKilometresPerHour, double maximumAbsoluteGradePercent, double maximumGradeChangePercentPer100Metres, double minimumHorizontalRadiusMetres, double maximumLateralAccelerationRatio, double reactionTimeSeconds, double brakingDecelerationMetresPerSecondSquared)
        {
            DesignSpeedKilometresPerHour = designSpeedKilometresPerHour;
            MaximumAbsoluteGradePercent = maximumAbsoluteGradePercent;
            MaximumGradeChangePercentPer100Metres = maximumGradeChangePercentPer100Metres;
            MinimumHorizontalRadiusMetres = minimumHorizontalRadiusMetres;
            MaximumLateralAccelerationRatio = maximumLateralAccelerationRatio;
            ReactionTimeSeconds = reactionTimeSeconds;
            BrakingDecelerationMetresPerSecondSquared = brakingDecelerationMetresPerSecondSquared;
        }
        public double DesignSpeedKilometresPerHour { get; private set; }
        public double MaximumAbsoluteGradePercent { get; private set; }
        public double MaximumGradeChangePercentPer100Metres { get; private set; }
        public double MinimumHorizontalRadiusMetres { get; private set; }
        public double MaximumLateralAccelerationRatio { get; private set; }
        public double ReactionTimeSeconds { get; private set; }
        public double BrakingDecelerationMetresPerSecondSquared { get; private set; }
        public void Validate()
        {
            Positive(DesignSpeedKilometresPerHour, nameof(DesignSpeedKilometresPerHour)); Positive(MaximumAbsoluteGradePercent, nameof(MaximumAbsoluteGradePercent)); Positive(MaximumGradeChangePercentPer100Metres, nameof(MaximumGradeChangePercentPer100Metres)); NonNegative(MinimumHorizontalRadiusMetres, nameof(MinimumHorizontalRadiusMetres)); Positive(MaximumLateralAccelerationRatio, nameof(MaximumLateralAccelerationRatio)); Positive(ReactionTimeSeconds, nameof(ReactionTimeSeconds)); Positive(BrakingDecelerationMetresPerSecondSquared, nameof(BrakingDecelerationMetresPerSecondSquared));
        }
        private static void Positive(double value, string name) { if (!RoadDriveReviewer.IsFinite(value) || value <= 0.0) throw new ArgumentOutOfRangeException(name); }
        private static void NonNegative(double value, string name) { if (!RoadDriveReviewer.IsFinite(value) || value < 0.0) throw new ArgumentOutOfRangeException(name); }
    }

    public sealed class RoadDriveSample
    {
        public RoadDriveSample(double station, double x, double y, double z) { Station = station; X = x; Y = y; Z = z; }
        public double Station { get; private set; } public double X { get; private set; } public double Y { get; private set; } public double Z { get; private set; }
        public void Validate(int index) { if (!RoadDriveReviewer.IsFinite(Station) || !RoadDriveReviewer.IsFinite(X) || !RoadDriveReviewer.IsFinite(Y) || !RoadDriveReviewer.IsFinite(Z)) throw new ArgumentOutOfRangeException("samples", "Invalid road-drive sample at index " + index.ToString(CultureInfo.InvariantCulture)); }
    }

    public sealed class RoadDriveSegment
    {
        public RoadDriveSegment(double startStation, double endStation, double planLengthMetres, double gradePercent) { StartStation = startStation; EndStation = endStation; PlanLengthMetres = planLengthMetres; GradePercent = gradePercent; }
        public double StartStation { get; private set; } public double EndStation { get; private set; } public double PlanLengthMetres { get; private set; } public double GradePercent { get; private set; }
    }

    public enum RoadDriveIssueType { Geometry, Grade, GradeChange, HorizontalRadius, LateralAcceleration }
    public enum RoadDriveSeverity { Review, Warning, Error }

    public sealed class RoadDriveIssue
    {
        public RoadDriveIssue(double station, RoadDriveIssueType type, RoadDriveSeverity severity, double value, double limit, string message) { Station = station; Type = type; Severity = severity; Value = value; Limit = limit; Message = message; }
        public double Station { get; private set; } public RoadDriveIssueType Type { get; private set; } public RoadDriveSeverity Severity { get; private set; } public double Value { get; private set; } public double Limit { get; private set; } public string Message { get; private set; }
    }

    public sealed class RoadCameraFrame
    {
        public RoadCameraFrame(double station, double x, double y, double z, double headingDegrees, double pitchDegrees) { Station = station; X = x; Y = y; Z = z; HeadingDegrees = headingDegrees; PitchDegrees = pitchDegrees; }
        public double Station { get; private set; } public double X { get; private set; } public double Y { get; private set; } public double Z { get; private set; } public double HeadingDegrees { get; private set; } public double PitchDegrees { get; private set; }
    }

    public sealed class RoadDriveAnalysis
    {
        public RoadDriveAnalysis(IReadOnlyList<RoadDriveSample> samples, IReadOnlyList<RoadDriveSegment> segments, IReadOnlyList<double> horizontalRadiiMetres, IReadOnlyList<double> lateralAccelerationRatios, IReadOnlyList<double> gradeChangeRates, IReadOnlyList<RoadCameraFrame> cameraFrames, IReadOnlyList<RoadDriveIssue> issues, double requiredHorizontalRadiusMetres, double stoppingSightDistanceMetres, double maximumAbsoluteGradePercent, double? minimumHorizontalRadiusMetres, double maximumLateralAccelerationRatio, double maximumGradeChangePercentPer100Metres)
        { Samples = samples; Segments = segments; HorizontalRadiiMetres = horizontalRadiiMetres; LateralAccelerationRatios = lateralAccelerationRatios; GradeChangeRates = gradeChangeRates; CameraFrames = cameraFrames; Issues = issues; RequiredHorizontalRadiusMetres = requiredHorizontalRadiusMetres; StoppingSightDistanceMetres = stoppingSightDistanceMetres; MaximumAbsoluteGradePercent = maximumAbsoluteGradePercent; MinimumHorizontalRadiusMetres = minimumHorizontalRadiusMetres; MaximumLateralAccelerationRatio = maximumLateralAccelerationRatio; MaximumGradeChangePercentPer100Metres = maximumGradeChangePercentPer100Metres; }
        public IReadOnlyList<RoadDriveSample> Samples { get; private set; } public IReadOnlyList<RoadDriveSegment> Segments { get; private set; } public IReadOnlyList<double> HorizontalRadiiMetres { get; private set; } public IReadOnlyList<double> LateralAccelerationRatios { get; private set; } public IReadOnlyList<double> GradeChangeRates { get; private set; } public IReadOnlyList<RoadCameraFrame> CameraFrames { get; private set; } public IReadOnlyList<RoadDriveIssue> Issues { get; private set; } public double RequiredHorizontalRadiusMetres { get; private set; } public double StoppingSightDistanceMetres { get; private set; } public double MaximumAbsoluteGradePercent { get; private set; } public double? MinimumHorizontalRadiusMetres { get; private set; } public double MaximumLateralAccelerationRatio { get; private set; } public double MaximumGradeChangePercentPer100Metres { get; private set; }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace CETools.Civil3D
{
    /// <summary>
    /// Preserved branch-label placement rules recovered from the V54 working source.
    /// The sewer alignment workflow can call this helper while the remaining V50–V60
    /// source reconciliation is completed.
    /// </summary>
    internal static class SewerBranchLabelPlacement
    {
        // Integration trigger: keep this helper wired into the active sewer alignment command.
        // Previous compatibility baseline was DefaultPaperHeight = 3.5; the
        // approved sewer production presentation now uses 5.0 mm.
        internal const double DefaultPaperHeight = 5.0;
        internal const double RepeatSpacing = 50.0;
        internal const double OffsetFactor = 2.75;
        internal const int MaximumLabelsPerBranch = 200;
        private const double GeometryTolerance = 1e-8;

        internal sealed class Placement
        {
            internal Placement(Point3d point, double rotation)
            {
                Point = point;
                Rotation = rotation;
            }

            internal Point3d Point { get; }
            internal double Rotation { get; }
        }

        internal static IReadOnlyList<Placement> BuildPlacements(
            IReadOnlyList<Point3d> points)
        {
            return BuildPlacements(
                points,
                RepeatSpacing,
                "Every pipe");
        }

        internal static IReadOnlyList<Placement> BuildPlacements(
            IReadOnlyList<Point3d> points,
            double longStraightSectionLength,
            string longSectionFrequency)
        {
            var result = new List<Placement>();
            if (points == null ||
                points.Count < 2)
                return result;

            var segmentLengths =
                new double[points.Count - 1];
            var segmentAngles =
                new double[points.Count - 1];
            var cumulative =
                new double[points.Count];

            double totalLength = 0.0;
            for (int index = 0;
                 index < points.Count - 1;
                 index++)
            {
                double length =
                    points[index]
                        .DistanceTo(
                            points[index + 1]);
                segmentLengths[index] =
                    length;
                segmentAngles[index] =
                    length <= GeometryTolerance
                        ? double.NaN
                        : Math.Atan2(
                            points[index + 1].Y -
                                points[index].Y,
                            points[index + 1].X -
                                points[index].X);
                totalLength += length;
                cumulative[index + 1] =
                    totalLength;
            }

            if (totalLength <= GeometryTolerance)
                return result;

            double longThreshold =
                Math.Max(
                    GeometryTolerance,
                    longStraightSectionLength <=
                        GeometryTolerance
                        ? RepeatSpacing
                        : longStraightSectionLength);
            bool everySecondPipe =
                string.Equals(
                    longSectionFrequency,
                    "Every second pipe",
                    StringComparison.OrdinalIgnoreCase);

            List<StraightRun> runs =
                BuildStraightRuns(
                    segmentLengths,
                    segmentAngles,
                    cumulative);

            foreach (StraightRun run in runs)
            {
                if (result.Count >=
                    MaximumLabelsPerBranch)
                    break;

                // Every straight branch section receives one name at the
                // geometric centre of the complete straight run, not merely
                // at the midpoint of whichever pipe happens to be longest.
                double runCentreDistance =
                    run.StartDistance +
                    (run.Length * 0.5);
                AddUniquePlacement(
                    result,
                    PlacementAtDistance(
                        points,
                        segmentLengths,
                        runCentreDistance));

                if (run.Length <=
                    longThreshold)
                    continue;

                // Long straight runs receive extra names over pipe centres.
                // The user can choose every pipe or every second pipe to reduce
                // annotation density while the centre-of-run label remains.
                int stride =
                    everySecondPipe
                        ? 2
                        : 1;
                int ordinal = 0;
                for (int segmentIndex =
                         run.StartSegment;
                     segmentIndex <=
                         run.EndSegment;
                     segmentIndex++)
                {
                    if (segmentLengths[
                            segmentIndex] <=
                        GeometryTolerance)
                        continue;

                    if ((ordinal % stride) == 0)
                    {
                        double pipeCentreDistance =
                            cumulative[
                                segmentIndex] +
                            (segmentLengths[
                                segmentIndex] *
                             0.5);
                        AddUniquePlacement(
                            result,
                            PlacementAtDistance(
                                points,
                                segmentLengths,
                                pipeCentreDistance));
                        if (result.Count >=
                            MaximumLabelsPerBranch)
                            break;
                    }
                    ordinal++;
                }
            }

            return result
                .OrderBy(
                    item =>
                        DistanceAlongPlan(
                            points,
                            item.Point))
                .Take(
                    MaximumLabelsPerBranch)
                .ToList();
        }

        private static List<StraightRun> BuildStraightRuns(
            IReadOnlyList<double> lengths,
            IReadOnlyList<double> angles,
            IReadOnlyList<double> cumulative)
        {
            var runs =
                new List<StraightRun>();
            if (lengths == null ||
                angles == null ||
                cumulative == null)
                return runs;

            int index = 0;
            while (index < lengths.Count)
            {
                while (index < lengths.Count &&
                       lengths[index] <=
                           GeometryTolerance)
                    index++;
                if (index >= lengths.Count)
                    break;

                int start = index;
                int end = index;
                double referenceAngle =
                    angles[index];

                while (end + 1 <
                       lengths.Count)
                {
                    int next =
                        end + 1;
                    if (lengths[next] <=
                        GeometryTolerance)
                    {
                        end = next;
                        continue;
                    }

                    if (!SameStraightDirection(
                            referenceAngle,
                            angles[next]))
                        break;

                    // Keep the run direction stable while absorbing very small
                    // survey/Civil 3D coordinate noise along a nominally
                    // straight branch.
                    referenceAngle =
                        MeanDirection(
                            referenceAngle,
                            angles[next]);
                    end = next;
                }

                double startDistance =
                    cumulative[start];
                double endDistance =
                    cumulative[
                        Math.Min(
                            end + 1,
                            cumulative.Count - 1)];
                double length =
                    Math.Max(
                        0.0,
                        endDistance -
                        startDistance);

                if (length >
                    GeometryTolerance)
                {
                    runs.Add(
                        new StraightRun(
                            start,
                            end,
                            startDistance,
                            length));
                }

                index =
                    end + 1;
            }

            return runs;
        }

        private static bool SameStraightDirection(
            double first,
            double second)
        {
            if (double.IsNaN(first) ||
                double.IsNaN(second))
                return false;

            double delta =
                second - first;
            while (delta > Math.PI)
                delta -=
                    Math.PI * 2.0;
            while (delta < -Math.PI)
                delta +=
                    Math.PI * 2.0;

            const double straightToleranceRadians =
                Math.PI / 90.0; // 2 degrees.
            return Math.Abs(delta) <=
                   straightToleranceRadians;
        }

        private static double MeanDirection(
            double first,
            double second)
        {
            double x =
                Math.Cos(first) +
                Math.Cos(second);
            double y =
                Math.Sin(first) +
                Math.Sin(second);
            return Math.Abs(x) <=
                       GeometryTolerance &&
                   Math.Abs(y) <=
                       GeometryTolerance
                ? first
                : Math.Atan2(y, x);
        }

        private static void AddUniquePlacement(
            ICollection<Placement> placements,
            Placement candidate)
        {
            if (placements == null ||
                candidate == null)
                return;

            const double duplicateTolerance =
                0.01;
            if (placements.Any(item =>
                    item != null &&
                    item.Point.DistanceTo(
                        candidate.Point) <=
                    duplicateTolerance))
                return;

            placements.Add(candidate);
        }

        private static double DistanceAlongPlan(
            IReadOnlyList<Point3d> points,
            Point3d target)
        {
            if (points == null ||
                points.Count < 2)
                return 0.0;

            double travelled = 0.0;
            double bestDistance =
                double.MaxValue;
            double bestAlong = 0.0;

            for (int index = 0;
                 index < points.Count - 1;
                 index++)
            {
                Point3d a =
                    points[index];
                Point3d b =
                    points[index + 1];
                Vector2d vector =
                    new Vector2d(
                        b.X - a.X,
                        b.Y - a.Y);
                double length =
                    vector.Length;
                if (length <=
                    GeometryTolerance)
                    continue;

                Vector2d unit =
                    vector.GetNormal();
                Vector2d toTarget =
                    new Vector2d(
                        target.X - a.X,
                        target.Y - a.Y);
                double local =
                    Math.Max(
                        0.0,
                        Math.Min(
                            length,
                            toTarget.DotProduct(
                                unit)));
                Point2d projected =
                    new Point2d(
                        a.X +
                            unit.X * local,
                        a.Y +
                            unit.Y * local);
                double distance =
                    projected.GetDistanceTo(
                        new Point2d(
                            target.X,
                            target.Y));
                if (distance <
                    bestDistance)
                {
                    bestDistance =
                        distance;
                    bestAlong =
                        travelled +
                        local;
                }
                travelled +=
                    length;
            }

            return bestAlong;
        }

        private sealed class StraightRun
        {
            internal StraightRun(
                int startSegment,
                int endSegment,
                double startDistance,
                double length)
            {
                StartSegment =
                    startSegment;
                EndSegment =
                    endSegment;
                StartDistance =
                    startDistance;
                Length =
                    length;
            }

            internal int StartSegment { get; private set; }
            internal int EndSegment { get; private set; }
            internal double StartDistance { get; private set; }
            internal double Length { get; private set; }
        }

        internal static Point3d OffsetPoint(
            Database database,
            Placement placement,
            double paperHeight,
            bool placeAbove)
        {
            if (database == null)
            {
                throw new ArgumentNullException(nameof(database));
            }

            if (placement == null)
            {
                throw new ArgumentNullException(nameof(placement));
            }

            double offsetDistance = ResolveScaleAwarePaperDistance(
                database,
                paperHeight * OffsetFactor);
            double side = placeAbove ? 1.0 : -1.0;
            var normal = new Vector3d(
                -Math.Sin(placement.Rotation),
                Math.Cos(placement.Rotation),
                0.0);
            return placement.Point + (normal * offsetDistance * side);
        }

        internal static void ConfigureLabel(
            MText label,
            Database database,
            Placement placement,
            string branchName,
            double paperHeight,
            bool placeAbove)
        {
            ConfigureLabel(
                label,
                database,
                placement,
                branchName,
                paperHeight,
                paperHeight * OffsetFactor,
                paperHeight * OffsetFactor,
                placeAbove);
        }

        internal static void ConfigureLabel(
            MText label,
            Database database,
            Placement placement,
            string branchName,
            double paperHeight,
            double aboveOffsetPaperDistance,
            double belowOffsetPaperDistance,
            bool placeAbove)
        {
            if (label == null)
            {
                throw new ArgumentNullException(nameof(label));
            }

            double paperOffset = placeAbove
                ? aboveOffsetPaperDistance
                : belowOffsetPaperDistance;
            double offsetDistance = ResolveScaleAwarePaperDistance(
                database,
                Math.Max(paperOffset, GeometryTolerance));
            double side = placeAbove ? 1.0 : -1.0;
            var normal = new Vector3d(
                -Math.Sin(placement.Rotation),
                Math.Cos(placement.Rotation),
                0.0);
            label.Location = placement.Point + (normal * offsetDistance * side);
            label.Attachment = placeAbove
                ? AttachmentPoint.BottomCenter
                : AttachmentPoint.TopCenter;
            label.Annotative = AnnotativeStates.True;
            // MText.TextHeight is the model height even for this annotative
            // entity in Civil 3D 2023. Convert the selected absolute paper-mm
            // value through the active annotation scale so Properties reports
            // exactly 1.8/2.0/2.5/3.5/5.0 instead of a doubled or 0.005 value.
            paperHeight = PaperAnnotationScale.NormalizeConfiguredPaperHeight(
                paperHeight);
            label.TextHeight = PaperAnnotationScale.ModelTextHeight(
                database,
                paperHeight);
            label.Rotation = placement.Rotation;
            label.Contents = branchName ?? string.Empty;
            label.ColorIndex = 3;
            label.BackgroundFill = true;
            label.UseBackgroundColor = true;
        }

        internal static double ResolveScaleAwarePaperDistance(
            Database database,
            double paperMillimetres)
        {
            return Math.Max(
                PaperAnnotationScale.ModelDistance(database, paperMillimetres),
                GeometryTolerance);
        }

        private static Placement PlacementAtDistance(
            IReadOnlyList<Point3d> points,
            IReadOnlyList<double> segmentLengths,
            double targetDistance)
        {
            double travelled = 0.0;
            for (int index = 0; index < segmentLengths.Count; index++)
            {
                double segmentLength = segmentLengths[index];
                if (segmentLength <= GeometryTolerance)
                {
                    continue;
                }

                if (travelled + segmentLength >= targetDistance ||
                    index == segmentLengths.Count - 1)
                {
                    double fraction = Math.Max(
                        0.0,
                        Math.Min(
                            1.0,
                            (targetDistance - travelled) / segmentLength));
                    Point3d start = points[index];
                    Point3d end = points[index + 1];
                    var point = new Point3d(
                        start.X + ((end.X - start.X) * fraction),
                        start.Y + ((end.Y - start.Y) * fraction),
                        start.Z + ((end.Z - start.Z) * fraction));
                    double rotation = NormalizeReadableRotation(
                        Math.Atan2(end.Y - start.Y, end.X - start.X));
                    return new Placement(point, rotation);
                }

                travelled += segmentLength;
            }

            Point3d fallback = points[points.Count - 1];
            Point3d previous = points[points.Count - 2];
            return new Placement(
                fallback,
                NormalizeReadableRotation(
                    Math.Atan2(
                        fallback.Y - previous.Y,
                        fallback.X - previous.X)));
        }

        private static double NormalizeReadableRotation(double rotation)
        {
            while (rotation > Math.PI)
            {
                rotation -= Math.PI * 2.0;
            }

            while (rotation <= -Math.PI)
            {
                rotation += Math.PI * 2.0;
            }

            if (rotation > Math.PI / 2.0)
            {
                rotation -= Math.PI;
            }
            else if (rotation < -Math.PI / 2.0)
            {
                rotation += Math.PI;
            }

            return rotation;
        }
    }
}

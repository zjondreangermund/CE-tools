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
        internal const double DefaultPaperHeight = 3.5;
        internal const double RepeatSpacing = 50.0;
        internal const double OffsetFactor = 5.0;
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
            var result = new List<Placement>();
            if (points == null || points.Count < 2)
            {
                return result;
            }

            var segmentLengths = new double[points.Count - 1];
            double totalLength = 0.0;
            for (int index = 0; index < points.Count - 1; index++)
            {
                double length = points[index].DistanceTo(points[index + 1]);
                segmentLengths[index] = length;
                totalLength += length;
            }

            if (totalLength <= GeometryTolerance)
            {
                return result;
            }

            int labelCount = Math.Max(1, Math.Min(
                MaximumLabelsPerBranch,
                (int)Math.Ceiling(totalLength / RepeatSpacing)));

            // Put labels at real pipe/tangent midpoints. Equal-distance points
            // along the complete branch can fall on a bend or immediately next
            // to a junction, which makes the name appear off-centre.
            var candidates = new List<PlacementCandidate>();
            double travelled = 0.0;
            for (int index = 0; index < segmentLengths.Length; index++)
            {
                double length = segmentLengths[index];
                if (length > GeometryTolerance)
                {
                    candidates.Add(new PlacementCandidate(
                        length,
                        travelled + (length * 0.5)));
                }
                travelled += length;
            }
            foreach (PlacementCandidate candidate in candidates
                .OrderByDescending(item => item.Length)
                .Take(Math.Min(labelCount, candidates.Count))
                .OrderBy(item => item.Distance))
            {
                result.Add(PlacementAtDistance(
                    points,
                    segmentLengths,
                    candidate.Distance));
            }

            return result;
        }

        private sealed class PlacementCandidate
        {
            internal PlacementCandidate(double length, double distance)
            {
                Length = length;
                Distance = distance;
            }

            internal double Length { get; private set; }
            internal double Distance { get; private set; }
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
            if (label == null)
            {
                throw new ArgumentNullException(nameof(label));
            }

            label.Location = OffsetPoint(
                database,
                placement,
                paperHeight,
                placeAbove);
            label.Attachment = placeAbove
                ? AttachmentPoint.BottomCenter
                : AttachmentPoint.TopCenter;
            label.Annotative = AnnotativeStates.True;
            label.TextHeight = PaperAnnotationScale.AnnotativeTextHeight(
                database,
                paperHeight);
            label.Rotation = placement.Rotation;
            label.Contents = branchName ?? string.Empty;
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

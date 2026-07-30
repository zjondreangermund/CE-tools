using System;
using System.Collections.Generic;
using System.Globalization;
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
        internal const double DefaultPaperHeight = 3.5;
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

            int labelCount = Math.Max(
                1,
                Math.Min(
                    MaximumLabelsPerBranch,
                    (int)Math.Ceiling(totalLength / RepeatSpacing)));

            for (int labelIndex = 0; labelIndex < labelCount; labelIndex++)
            {
                double targetDistance =
                    ((labelIndex + 0.5) / labelCount) * totalLength;
                result.Add(PlacementAtDistance(
                    points,
                    segmentLengths,
                    targetDistance));
            }

            return result;
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
            label.Attachment = AttachmentPoint.MiddleCenter;
            label.Annotative = AnnotativeStates.True;
            label.TextHeight = paperHeight;
            label.Rotation = placement.Rotation;
            label.Contents = branchName ?? string.Empty;
            label.BackgroundFill = true;
            label.UseBackgroundColor = true;
        }

        internal static double ResolveScaleAwarePaperDistance(
            Database database,
            double paperMillimetres)
        {
            double annotationScale = 1.0;
            try
            {
                annotationScale = Convert.ToDouble(
                    Autodesk.AutoCAD.ApplicationServices.Core.Application
                        .GetSystemVariable("CANNOSCALEVALUE"),
                    CultureInfo.InvariantCulture);
            }
            catch
            {
                annotationScale = Math.Max(database.Dimscale, 1.0);
            }

            if (!(annotationScale > 0.0))
            {
                annotationScale = 1.0;
            }

            string units = database.Insunits.ToString();
            double millimetresToDrawingUnits =
                string.Equals(units, "Millimeters", StringComparison.OrdinalIgnoreCase) ? 1.0 :
                string.Equals(units, "Centimeters", StringComparison.OrdinalIgnoreCase) ? 0.1 :
                string.Equals(units, "Meters", StringComparison.OrdinalIgnoreCase) ? 0.001 :
                string.Equals(units, "Inches", StringComparison.OrdinalIgnoreCase) ? 0.0393700787 :
                string.Equals(units, "Feet", StringComparison.OrdinalIgnoreCase) ? 0.0032808399 :
                0.001;

            return Math.Max(
                paperMillimetres * annotationScale * millimetresToDrawingUnits,
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

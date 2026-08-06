using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil;
using CivilFeatureLine = Autodesk.Civil.DatabaseServices.FeatureLine;

namespace CETools.Civil3D
{
    internal sealed class VertexSettingSource
    {
        public ObjectId SourceId { get; set; }
        public string Handle { get; set; }
        public string Name { get; set; }
        public IList<VertexSettingRecord> Records { get; set; }
        public IList<VertexRadialDimension> Dimensions { get; set; }
    }

    internal sealed class VertexSettingRecord
    {
        public string Key { get; set; }
        public string Kind { get; set; }
        public string SourceHandle { get; set; }
        public string SourceName { get; set; }
        public int SegmentIndex { get; set; }
        public Point3d Point { get; set; }
        public double SegmentLength { get; set; }
        public double? Radius { get; set; }
        public string PointName { get; set; }
        public Vector3d? AnnotationOffset { get; set; }
    }

    internal sealed class VertexRadialDimension
    {
        public string Key { get; set; }
        public string SourceHandle { get; set; }
        public int SegmentIndex { get; set; }
        public Point3d Center { get; set; }
        public Point3d ChordPoint { get; set; }
        public double Radius { get; set; }
    }

    /// <summary>
    /// Extracts setting-out points from multiple AutoCAD polylines and Civil 3D
    /// feature lines. The thresholds intentionally match the engineering rules in
    /// the CE Tools request: arc midpoint above 10 m, tangent midpoint above 20 m,
    /// and three quarter points above 40 m.
    /// </summary>
    internal static class VertexSettingOutGeometry
    {
        private const double Tolerance = 1e-8;

        public static IList<VertexSettingSource> ReadSources(
            Database database,
            Transaction transaction,
            IEnumerable<ObjectId> sourceIds,
            out int rejected)
        {
            var result = new List<VertexSettingSource>();
            rejected = 0;
            if (database == null || transaction == null || sourceIds == null)
                return result;

            foreach (ObjectId id in sourceIds.Where(item => !item.IsNull).Distinct())
            {
                Entity entity;
                try
                {
                    entity = transaction.GetObject(
                        id,
                        OpenMode.ForRead,
                        false) as Entity;
                }
                catch
                {
                    rejected++;
                    continue;
                }

                VertexSettingSource source = BuildSource(entity, transaction);
                if (source == null || source.Records == null || source.Records.Count == 0)
                {
                    rejected++;
                    continue;
                }
                result.Add(source);
            }
            return result;
        }

        public static bool IsSupported(Entity entity)
        {
            return entity is Polyline ||
                   entity is Polyline2d ||
                   entity is Polyline3d ||
                   entity is CivilFeatureLine;
        }

        private static VertexSettingSource BuildSource(
            Entity entity,
            Transaction transaction)
        {
            if (entity == null || !IsSupported(entity)) return null;

            var points = new List<Point3d>();
            var bulges = new List<double>();
            bool closed;

            Polyline lightweight = entity as Polyline;
            if (lightweight != null)
            {
                for (int index = 0; index < lightweight.NumberOfVertices; index++)
                    points.Add(lightweight.GetPoint3dAt(index));
                closed = lightweight.Closed;
                int segmentCount = closed ? points.Count : Math.Max(0, points.Count - 1);
                for (int index = 0; index < segmentCount; index++)
                    bulges.Add(lightweight.GetBulgeAt(index));
                return BuildFromVertices(entity, points, bulges, closed);
            }

            Polyline2d polyline2d = entity as Polyline2d;
            if (polyline2d != null)
            {
                foreach (ObjectId vertexId in polyline2d)
                {
                    Vertex2d vertex = transaction.GetObject(
                        vertexId,
                        OpenMode.ForRead,
                        false) as Vertex2d;
                    if (vertex == null) continue;
                    points.Add(vertex.Position);
                    bulges.Add(vertex.Bulge);
                }
                closed = polyline2d.Closed;
                RemoveClosingDuplicate(points, bulges);
                TrimBulges(points, bulges, closed);
                return BuildFromVertices(entity, points, bulges, closed);
            }

            Polyline3d polyline3d = entity as Polyline3d;
            if (polyline3d != null)
            {
                foreach (ObjectId vertexId in polyline3d)
                {
                    PolylineVertex3d vertex = transaction.GetObject(
                        vertexId,
                        OpenMode.ForRead,
                        false) as PolylineVertex3d;
                    if (vertex != null) points.Add(vertex.Position);
                }
                closed = polyline3d.Closed;
                RemoveClosingDuplicate(points, null);
                int segmentCount = closed ? points.Count : Math.Max(0, points.Count - 1);
                for (int index = 0; index < segmentCount; index++) bulges.Add(0.0);
                return BuildFromVertices(entity, points, bulges, closed);
            }

            CivilFeatureLine featureLine = entity as CivilFeatureLine;
            if (featureLine != null)
            {
                Point3dCollection piPoints = featureLine.GetPoints(
                    FeatureLinePointType.PIPoint);
                foreach (Point3d point in piPoints) points.Add(point);
                closed = featureLine.Closed;
                RemoveClosingDuplicate(points, null);
                int segmentCount = closed ? points.Count : Math.Max(0, points.Count - 1);
                for (int index = 0; index < segmentCount; index++)
                {
                    double bulge = 0.0;
                    try { bulge = featureLine.GetBulge(index); }
                    catch { bulge = 0.0; }
                    bulges.Add(bulge);
                }
                return BuildFromVertices(entity, points, bulges, closed);
            }

            return null;
        }

        private static VertexSettingSource BuildFromVertices(
            Entity entity,
            IList<Point3d> points,
            IList<double> bulges,
            bool closed)
        {
            if (entity == null || points == null || points.Count < 2) return null;
            int segmentCount = closed ? points.Count : points.Count - 1;
            if (segmentCount < 1) return null;

            string handle = entity.Handle.ToString();
            string sourceName = ResolveSourceName(entity);
            var records = new List<VertexSettingRecord>();
            var dimensions = new List<VertexRadialDimension>();

            for (int segment = 0; segment < segmentCount; segment++)
            {
                Point3d start = points[segment];
                Point3d end = points[(segment + 1) % points.Count];
                double bulge = segment < bulges.Count ? bulges[segment] : 0.0;

                records.Add(Record(
                    handle,
                    sourceName,
                    "V" + segment.ToString(CultureInfo.InvariantCulture),
                    "VERTEX",
                    segment + 1,
                    start,
                    0.0,
                    null));

                if (Math.Abs(bulge) > Tolerance &&
                    TryArc(start, end, bulge, out ArcData arc))
                {
                    if (arc.Length > 10.0 + Tolerance)
                    {
                        records.Add(Record(
                            handle,
                            sourceName,
                            "A" + segment.ToString(CultureInfo.InvariantCulture) + "M",
                            "ARC MIDPOINT",
                            segment + 1,
                            arc.MidPoint,
                            arc.Length,
                            arc.Radius));
                    }

                    records.Add(Record(
                        handle,
                        sourceName,
                        "A" + segment.ToString(CultureInfo.InvariantCulture) + "C",
                        "ARC CENTER",
                        segment + 1,
                        arc.Center,
                        arc.Length,
                        arc.Radius));

                    dimensions.Add(new VertexRadialDimension
                    {
                        Key = handle + "|D|" + segment.ToString(CultureInfo.InvariantCulture),
                        SourceHandle = handle,
                        SegmentIndex = segment + 1,
                        Center = arc.Center,
                        ChordPoint = arc.MidPoint,
                        Radius = arc.Radius
                    });
                    continue;
                }

                double length = PlanDistance(start, end);
                if (length > 40.0 + Tolerance)
                {
                    AddTangentPoint(records, handle, sourceName, segment, start, end, 0.25, length, "TANGENT 1/4");
                    AddTangentPoint(records, handle, sourceName, segment, start, end, 0.50, length, "TANGENT MIDPOINT");
                    AddTangentPoint(records, handle, sourceName, segment, start, end, 0.75, length, "TANGENT 3/4");
                }
                else if (length > 20.0 + Tolerance)
                {
                    AddTangentPoint(records, handle, sourceName, segment, start, end, 0.50, length, "TANGENT MIDPOINT");
                }
            }

            if (!closed)
            {
                int index = points.Count - 1;
                records.Add(Record(
                    handle,
                    sourceName,
                    "V" + index.ToString(CultureInfo.InvariantCulture),
                    "VERTEX",
                    segmentCount,
                    points[index],
                    0.0,
                    null));
            }

            return new VertexSettingSource
            {
                SourceId = entity.ObjectId,
                Handle = handle,
                Name = sourceName,
                Records = records,
                Dimensions = dimensions
            };
        }

        private static void AddTangentPoint(
            ICollection<VertexSettingRecord> records,
            string handle,
            string sourceName,
            int segment,
            Point3d start,
            Point3d end,
            double fraction,
            double length,
            string kind)
        {
            string fractionKey = Math.Round(fraction * 100.0)
                .ToString("0", CultureInfo.InvariantCulture);
            records.Add(Record(
                handle,
                sourceName,
                "T" + segment.ToString(CultureInfo.InvariantCulture) + "F" + fractionKey,
                kind,
                segment + 1,
                Interpolate(start, end, fraction),
                length,
                null));
        }

        private static VertexSettingRecord Record(
            string handle,
            string sourceName,
            string localKey,
            string kind,
            int segmentIndex,
            Point3d point,
            double segmentLength,
            double? radius)
        {
            return new VertexSettingRecord
            {
                Key = handle + "|" + localKey,
                Kind = kind,
                SourceHandle = handle,
                SourceName = sourceName,
                SegmentIndex = segmentIndex,
                Point = point,
                SegmentLength = segmentLength,
                Radius = radius,
                PointName = string.Empty
            };
        }

        private static bool TryArc(
            Point3d start,
            Point3d end,
            double bulge,
            out ArcData arc)
        {
            arc = null;
            double chord = PlanDistance(start, end);
            if (chord <= Tolerance) return false;

            double includedAngle = 4.0 * Math.Atan(bulge);
            double sine = Math.Sin(Math.Abs(includedAngle) * 0.5);
            double tangent = Math.Tan(includedAngle * 0.5);
            if (Math.Abs(sine) <= Tolerance || Math.Abs(tangent) <= Tolerance)
                return false;

            double radius = chord / (2.0 * sine);
            double midX = (start.X + end.X) * 0.5;
            double midY = (start.Y + end.Y) * 0.5;
            double normalX = -(end.Y - start.Y) / chord;
            double normalY = (end.X - start.X) / chord;
            double centerOffset = chord / (2.0 * tangent);
            double centerX = midX + normalX * centerOffset;
            double centerY = midY + normalY * centerOffset;
            double midZ = (start.Z + end.Z) * 0.5;

            double startAngle = Math.Atan2(start.Y - centerY, start.X - centerX);
            double midAngle = startAngle + includedAngle * 0.5;
            Point3d midPoint = new Point3d(
                centerX + radius * Math.Cos(midAngle),
                centerY + radius * Math.Sin(midAngle),
                midZ);
            Point3d center = new Point3d(centerX, centerY, midZ);

            arc = new ArcData
            {
                Center = center,
                MidPoint = midPoint,
                Radius = radius,
                Length = Math.Abs(includedAngle) * radius
            };
            return true;
        }

        private static Point3d Interpolate(
            Point3d start,
            Point3d end,
            double fraction)
        {
            return new Point3d(
                start.X + (end.X - start.X) * fraction,
                start.Y + (end.Y - start.Y) * fraction,
                start.Z + (end.Z - start.Z) * fraction);
        }

        private static double PlanDistance(Point3d start, Point3d end)
        {
            double dx = end.X - start.X;
            double dy = end.Y - start.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static string ResolveSourceName(Entity entity)
        {
            CivilFeatureLine featureLine = entity as CivilFeatureLine;
            if (featureLine != null && !string.IsNullOrWhiteSpace(featureLine.Name))
                return featureLine.Name;
            if (!string.IsNullOrWhiteSpace(entity.Layer)) return entity.Layer;
            return entity.Handle.ToString();
        }

        private static void RemoveClosingDuplicate(
            IList<Point3d> points,
            IList<double> bulges)
        {
            if (points == null || points.Count < 2) return;
            if (points[0].DistanceTo(points[points.Count - 1]) > Tolerance) return;
            points.RemoveAt(points.Count - 1);
            if (bulges != null && bulges.Count > points.Count)
                bulges.RemoveAt(bulges.Count - 1);
        }

        private static void TrimBulges(
            IList<Point3d> points,
            IList<double> bulges,
            bool closed)
        {
            int required = closed ? points.Count : Math.Max(0, points.Count - 1);
            while (bulges.Count > required) bulges.RemoveAt(bulges.Count - 1);
            while (bulges.Count < required) bulges.Add(0.0);
        }

        private sealed class ArcData
        {
            public Point3d Center { get; set; }
            public Point3d MidPoint { get; set; }
            public double Radius { get; set; }
            public double Length { get; set; }
        }
    }
}

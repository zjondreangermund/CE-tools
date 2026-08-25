using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace CETools.Civil3D
{
    internal static class August25StraightPolylineSplitter
    {
        private const double Tolerance = 0.000001;

        internal static DBObjectCollection TryBuild(Polyline source, IList<double> cuts)
        {
            if (source == null || source.Closed || source.NumberOfVertices < 2 || cuts == null || cuts.Count == 0)
                return null;

            double constantWidth;
            try { constantWidth = source.ConstantWidth; }
            catch { return null; }
            if (double.IsNaN(constantWidth) || double.IsInfinity(constantWidth)) return null;

            var vertices = new List<Point2d>();
            var cumulative = new List<double>();
            double running = 0.0;
            for (int index = 0; index < source.NumberOfVertices; index++)
            {
                if (index < source.NumberOfVertices - 1)
                {
                    double bulge;
                    try { bulge = source.GetBulgeAt(index); }
                    catch { return null; }
                    if (Math.Abs(bulge) > Tolerance) return null;
                }

                Point2d point = source.GetPoint2dAt(index);
                vertices.Add(point);
                if (index == 0)
                {
                    cumulative.Add(0.0);
                }
                else
                {
                    running += vertices[index - 1].GetDistanceTo(point);
                    cumulative.Add(running);
                }
            }

            double sourceLength = August25CadSupplementaryBreakAnalysis.CurveLength(source);
            if (!LengthMatches(sourceLength, running)) return null;

            var boundaries = new List<double> { 0.0 };
            boundaries.AddRange(cuts);
            boundaries.Add(sourceLength);
            var pieces = new DBObjectCollection();

            for (int pieceIndex = 0; pieceIndex + 1 < boundaries.Count; pieceIndex++)
            {
                double startDistance = boundaries[pieceIndex];
                double endDistance = boundaries[pieceIndex + 1];
                if (endDistance - startDistance <= DistanceTolerance(sourceLength))
                {
                    Dispose(pieces);
                    return null;
                }

                var points = new List<Point2d>();
                AddPoint(points, PointAtDistance(vertices, cumulative, startDistance));
                for (int vertex = 1; vertex < vertices.Count - 1; vertex++)
                {
                    if (cumulative[vertex] > startDistance + DistanceTolerance(sourceLength) &&
                        cumulative[vertex] < endDistance - DistanceTolerance(sourceLength))
                        AddPoint(points, vertices[vertex]);
                }
                AddPoint(points, PointAtDistance(vertices, cumulative, endDistance));
                if (points.Count < 2)
                {
                    Dispose(pieces);
                    return null;
                }

                var piece = new Polyline(points.Count);
                piece.Normal = source.Normal;
                piece.Elevation = source.Elevation;
                piece.Thickness = source.Thickness;
                for (int index = 0; index < points.Count; index++)
                    piece.AddVertexAt(index, points[index], 0.0, constantWidth, constantWidth);
                piece.Closed = false;
                pieces.Add(piece);
            }

            return pieces.Count == cuts.Count + 1 ? pieces : null;
        }

        private static Point2d PointAtDistance(
            IList<Point2d> vertices,
            IList<double> cumulative,
            double distance)
        {
            if (distance <= Tolerance) return vertices[0];
            double total = cumulative[cumulative.Count - 1];
            if (distance >= total - Tolerance) return vertices[vertices.Count - 1];

            for (int index = 0; index + 1 < cumulative.Count; index++)
            {
                if (distance > cumulative[index + 1] + Tolerance) continue;
                double segment = cumulative[index + 1] - cumulative[index];
                if (segment <= Tolerance) return vertices[index];
                double ratio = (distance - cumulative[index]) / segment;
                ratio = Math.Max(0.0, Math.Min(1.0, ratio));
                return new Point2d(
                    vertices[index].X + (vertices[index + 1].X - vertices[index].X) * ratio,
                    vertices[index].Y + (vertices[index + 1].Y - vertices[index].Y) * ratio);
            }
            return vertices[vertices.Count - 1];
        }

        private static void AddPoint(IList<Point2d> points, Point2d point)
        {
            if (points.Count > 0 && points[points.Count - 1].GetDistanceTo(point) <= Tolerance) return;
            points.Add(point);
        }

        private static bool LengthMatches(double expected, double actual)
        {
            return Math.Abs(expected - actual) <= DistanceTolerance(Math.Max(Math.Abs(expected), Math.Abs(actual)));
        }

        private static double DistanceTolerance(double length)
        {
            return Math.Max(0.00001, Math.Abs(length) * 0.00000001);
        }

        private static void Dispose(DBObjectCollection values)
        {
            if (values == null) return;
            foreach (DBObject value in values)
            {
                try { if (value != null && value.Database == null) value.Dispose(); } catch { }
            }
        }
    }
}

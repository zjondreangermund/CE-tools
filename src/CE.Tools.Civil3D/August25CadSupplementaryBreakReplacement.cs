using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace CETools.Civil3D
{
    internal static class August25CadSupplementaryBreakReplacement
    {
        private const double Tolerance = 0.000001;

        private sealed class CoverageInterval
        {
            internal double Start;
            internal double End;
            internal double Length;
        }

        internal static bool TryReplaceOneAtomic(
            Database database,
            August25BreakPlan plan,
            out int createdCount)
        {
            createdCount = 0;
            if (database == null || plan == null || plan.SourceId.IsNull || plan.SourceId.IsErased)
                return false;

            try
            {
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    Curve source = transaction.GetObject(plan.SourceId, OpenMode.ForRead, false) as Curve;
                    if (source == null || source.IsErased) return false;
                    double originalLength = August25CadSupplementaryBreakAnalysis.CurveLength(source);
                    if (originalLength <= Tolerance) return false;

                    List<double> cuts = UniqueCuts(plan.Distances, originalLength);
                    if (cuts.Count == 0) return false;

                    DBObjectCollection pieces = BuildCandidatePieces(source, cuts);
                    if (pieces == null || pieces.Count != cuts.Count + 1)
                    {
                        DisposeUnowned(pieces);
                        return false;
                    }

                    BlockTableRecord owner = transaction.GetObject(
                        source.OwnerId,
                        OpenMode.ForWrite,
                        false) as BlockTableRecord;
                    if (owner == null)
                    {
                        DisposeUnowned(pieces);
                        return false;
                    }

                    var newIds = new List<ObjectId>();
                    foreach (DBObject value in pieces)
                    {
                        Entity entity = value as Entity;
                        Curve candidate = value as Curve;
                        if (entity == null || candidate == null ||
                            August25CadSupplementaryBreakAnalysis.CurveLength(candidate) <= Tolerance)
                        {
                            DisposeUnowned(pieces);
                            return false;
                        }

                        try { entity.SetPropertiesFrom(source); } catch { }
                        ObjectId newId = owner.AppendEntity(entity);
                        transaction.AddNewlyCreatedDBObject(entity, true);
                        try { entity.RecordGraphicsModified(true); } catch { }
                        newIds.Add(newId);
                    }

                    if (!VerifyCompleteCoverage(source, newIds, cuts, originalLength, transaction))
                    {
                        // Transaction disposal rolls back every provisional piece.
                        return false;
                    }

                    source.UpgradeOpen();
                    source.Erase();
                    transaction.Commit();
                    createdCount = newIds.Count;
                    return true;
                }
            }
            catch
            {
                // No commit means the original source remains and provisional pieces vanish.
                return false;
            }
        }

        private static DBObjectCollection BuildCandidatePieces(Curve source, IList<double> cuts)
        {
            Polyline lightweight = source as Polyline;
            DBObjectCollection manual = August25StraightPolylineSplitter.TryBuild(lightweight, cuts);
            if (manual != null) return manual;

            try
            {
                var splitPoints = new Point3dCollection();
                foreach (double distance in cuts)
                    splitPoints.Add(source.GetPointAtDist(distance));
                return source.GetSplitCurves(splitPoints);
            }
            catch { return null; }
        }

        private static bool VerifyCompleteCoverage(
            Curve source,
            IList<ObjectId> newIds,
            IList<double> cuts,
            double originalLength,
            Transaction transaction)
        {
            if (source == null || newIds == null || newIds.Count != cuts.Count + 1)
                return false;

            var intervals = new List<CoverageInterval>();
            double totalLength = 0.0;
            foreach (ObjectId id in newIds)
            {
                if (id.IsNull || id.IsErased) return false;
                Curve piece = transaction.GetObject(id, OpenMode.ForRead, false) as Curve;
                if (piece == null || piece.IsErased) return false;
                double length = August25CadSupplementaryBreakAnalysis.CurveLength(piece);
                if (length <= Tolerance) return false;

                double first;
                double second;
                if (!TryProjectEndpointDistance(source, piece.StartPoint, originalLength, out first) ||
                    !TryProjectEndpointDistance(source, piece.EndPoint, originalLength, out second))
                    return false;

                double start = Math.Min(first, second);
                double end = Math.Max(first, second);
                if (end - start <= DistanceTolerance(originalLength)) return false;
                if (!LengthMatches(length, end - start)) return false;
                intervals.Add(new CoverageInterval { Start = start, End = end, Length = length });
                totalLength += length;
            }

            if (!LengthMatches(originalLength, totalLength)) return false;
            intervals = intervals.OrderBy(item => item.Start).ToList();

            var expected = new List<double> { 0.0 };
            expected.AddRange(cuts);
            expected.Add(originalLength);
            if (intervals.Count + 1 != expected.Count) return false;

            double tolerance = DistanceTolerance(originalLength);
            for (int index = 0; index < intervals.Count; index++)
            {
                if (Math.Abs(intervals[index].Start - expected[index]) > tolerance ||
                    Math.Abs(intervals[index].End - expected[index + 1]) > tolerance)
                    return false;
                if (index > 0 &&
                    Math.Abs(intervals[index - 1].End - intervals[index].Start) > tolerance)
                    return false;
            }
            return true;
        }

        private static bool TryProjectEndpointDistance(
            Curve source,
            Point3d endpoint,
            double sourceLength,
            out double distance)
        {
            distance = 0.0;
            try
            {
                Point3d closest = source.GetClosestPointTo(endpoint, false);
                double geometryTolerance = Math.Max(0.00001, sourceLength * 0.00000001);
                if (endpoint.DistanceTo(closest) > geometryTolerance) return false;
                distance = source.GetDistAtPoint(closest);
                return distance >= -DistanceTolerance(sourceLength) &&
                    distance <= sourceLength + DistanceTolerance(sourceLength);
            }
            catch { return false; }
        }

        private static List<double> UniqueCuts(IEnumerable<double> values, double length)
        {
            var result = new List<double>();
            double tolerance = DistanceTolerance(length);
            foreach (double value in values.OrderBy(item => item))
            {
                if (value <= tolerance || length - value <= tolerance) continue;
                if (result.Count == 0 || Math.Abs(result[result.Count - 1] - value) > tolerance)
                    result.Add(value);
            }
            return result;
        }

        private static bool LengthMatches(double expected, double actual)
        {
            return Math.Abs(expected - actual) <=
                DistanceTolerance(Math.Max(Math.Abs(expected), Math.Abs(actual)));
        }

        private static double DistanceTolerance(double length)
        {
            return Math.Max(0.00001, Math.Abs(length) * 0.00000001);
        }

        private static void DisposeUnowned(DBObjectCollection values)
        {
            if (values == null) return;
            foreach (DBObject value in values)
            {
                try { if (value != null && value.Database == null) value.Dispose(); } catch { }
            }
        }
    }
}

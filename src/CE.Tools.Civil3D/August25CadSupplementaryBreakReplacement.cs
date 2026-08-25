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

            var newIds = new List<ObjectId>();
            List<double> cuts = null;
            double originalLength = 0.0;

            try
            {
                // Field-safety rule: commit replacement geometry while the source is
                // still present. Never erase a selected route in the same transaction
                // that creates transient/new replacement objects.
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    Curve source = transaction.GetObject(plan.SourceId, OpenMode.ForRead, false) as Curve;
                    if (source == null || source.IsErased) return false;
                    originalLength = August25CadSupplementaryBreakAnalysis.CurveLength(source);
                    if (originalLength <= Tolerance) return false;

                    cuts = UniqueCuts(plan.Distances, originalLength);
                    if (cuts.Count == 0) return false;

                    DBObjectCollection pieces = BuildCandidatePieces(source, cuts);
                    if (pieces == null || pieces.Count != cuts.Count + 1)
                    {
                        DisposeUnowned(pieces);
                        return false;
                    }

                    BlockTableRecord owner = transaction.GetObject(
                        source.OwnerId, OpenMode.ForWrite, false) as BlockTableRecord;
                    if (owner == null)
                    {
                        DisposeUnowned(pieces);
                        return false;
                    }

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

                    if (newIds.Count != cuts.Count + 1) return false;
                    transaction.Commit();
                }

                // Re-open the source and every replacement from the database after
                // commit. This catches field cases where transient split geometry
                // looked complete but one persisted tail/span was missing.
                if (!VerifyPersistedReplacement(
                        database, plan.SourceId, newIds, cuts, originalLength))
                {
                    CleanupPersisted(database, newIds);
                    return false;
                }

                // Only now may the original route be erased. If this transaction
                // fails the original remains; provisional replacements are removed.
                try
                {
                    using (Transaction transaction = database.TransactionManager.StartTransaction())
                    {
                        Entity source = transaction.GetObject(
                            plan.SourceId, OpenMode.ForWrite, false) as Entity;
                        if (source == null || source.IsErased)
                        {
                            CleanupPersisted(database, newIds);
                            return false;
                        }
                        source.Erase();
                        transaction.Commit();
                    }
                }
                catch
                {
                    CleanupPersisted(database, newIds);
                    return false;
                }

                createdCount = newIds.Count;
                return true;
            }
            catch
            {
                CleanupPersisted(database, newIds);
                return false;
            }
        }

        private static bool VerifyPersistedReplacement(
            Database database,
            ObjectId sourceId,
            IList<ObjectId> newIds,
            IList<double> cuts,
            double originalLength)
        {
            try
            {
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    Curve source = transaction.GetObject(sourceId, OpenMode.ForRead, false) as Curve;
                    if (source == null || source.IsErased) return false;
                    return VerifyCompleteCoverage(
                        source, newIds, cuts, originalLength, transaction);
                }
            }
            catch { return false; }
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
            if (source == null || newIds == null || cuts == null ||
                newIds.Count != cuts.Count + 1)
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

        private static void CleanupPersisted(Database database, IEnumerable<ObjectId> ids)
        {
            if (database == null || ids == null) return;
            try
            {
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId id in ids)
                    {
                        if (id.IsNull || id.IsErased) continue;
                        try
                        {
                            Entity entity = transaction.GetObject(id, OpenMode.ForWrite, false) as Entity;
                            if (entity != null && !entity.IsErased) entity.Erase();
                        }
                        catch { }
                    }
                    transaction.Commit();
                }
            }
            catch { }
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

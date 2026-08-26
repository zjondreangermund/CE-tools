using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace CETools.Civil3D
{
    /// <summary>
    /// Two-phase batch replacement for CE_PLBREAKJUNCTIONS. All replacement spans
    /// for all affected selected sources are persisted and verified while every
    /// original remains in the database. Only after the whole batch is verified are
    /// the original polylines erased together in one transaction. Any failure cleans
    /// provisional replacements and leaves every original selected object untouched.
    /// </summary>
    internal static class August26CadSupplementaryBreakReplacement
    {
        private const double Tolerance = 0.000001;

        private sealed class CoverageInterval
        {
            internal double Start;
            internal double End;
            internal double Length;
        }

        private sealed class StagedSource
        {
            internal ObjectId SourceId;
            internal readonly List<ObjectId> NewIds = new List<ObjectId>();
            internal List<double> Cuts;
            internal double OriginalLength;
        }

        internal static bool TryReplaceBatch(
            Database database,
            IList<August25BreakPlan> plans,
            out int replacedSources,
            out int createdSegments,
            out string failure)
        {
            replacedSources = 0;
            createdSegments = 0;
            failure = string.Empty;
            if (database == null || plans == null || plans.Count == 0)
            {
                failure = "No affected source polylines were supplied.";
                return false;
            }

            var staged = new List<StagedSource>();
            try
            {
                // Phase 1: create every candidate replacement with every original
                // still present. A selected source is never erased during staging.
                foreach (August25BreakPlan plan in plans)
                {
                    StagedSource source;
                    string reason;
                    if (!TryStageOne(database, plan, out source, out reason))
                    {
                        failure = string.IsNullOrWhiteSpace(reason)
                            ? "A selected source could not be staged safely."
                            : reason;
                        CleanupPersisted(database, staged.SelectMany(item => item.NewIds));
                        return false;
                    }
                    staged.Add(source);
                }

                // Phase 2: reopen the persisted replacement geometry and verify full
                // first/intermediate/last coverage for every source before any erase.
                foreach (StagedSource source in staged)
                {
                    if (!VerifyPersistedReplacement(database, source))
                    {
                        failure = "Persisted split coverage verification failed. All original selected polylines were retained.";
                        CleanupPersisted(database, staged.SelectMany(item => item.NewIds));
                        return false;
                    }
                }

                // Phase 3: erase all originals in one transaction. If even one source
                // cannot be opened/erased, the transaction rolls back for all of them.
                try
                {
                    using (Transaction transaction = database.TransactionManager.StartTransaction())
                    {
                        foreach (StagedSource source in staged)
                        {
                            Entity original = transaction.GetObject(
                                source.SourceId,
                                OpenMode.ForWrite,
                                false) as Entity;
                            if (original == null || original.IsErased)
                                throw new InvalidOperationException("An original selected polyline disappeared before final replacement.");
                            if (LayerLocked(transaction, original.LayerId))
                                throw new InvalidOperationException("A selected polyline is on a locked layer.");
                            original.Erase();
                        }
                        transaction.Commit();
                    }
                }
                catch (System.Exception exception)
                {
                    failure = "Original erase transaction was rolled back: " + exception.Message;
                    CleanupPersisted(database, staged.SelectMany(item => item.NewIds));
                    return false;
                }

                replacedSources = staged.Count;
                createdSegments = staged.Sum(item => item.NewIds.Count);
                return true;
            }
            catch (System.Exception exception)
            {
                failure = exception.Message;
                CleanupPersisted(database, staged.SelectMany(item => item.NewIds));
                return false;
            }
        }

        private static bool TryStageOne(
            Database database,
            August25BreakPlan plan,
            out StagedSource staged,
            out string failure)
        {
            staged = null;
            failure = string.Empty;
            if (plan == null || plan.SourceId.IsNull || plan.SourceId.IsErased)
            {
                failure = "A selected source ID is no longer valid.";
                return false;
            }

            var result = new StagedSource { SourceId = plan.SourceId };
            try
            {
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    Curve source = transaction.GetObject(plan.SourceId, OpenMode.ForRead, false) as Curve;
                    if (source == null || source.IsErased)
                    {
                        failure = "A selected source is no longer available.";
                        return false;
                    }
                    if (LayerLocked(transaction, source.LayerId))
                    {
                        failure = "A selected source is on a locked layer.";
                        return false;
                    }

                    result.OriginalLength = August25CadSupplementaryBreakAnalysis.CurveLength(source);
                    if (result.OriginalLength <= Tolerance)
                    {
                        failure = "A selected source has no usable length.";
                        return false;
                    }
                    result.Cuts = UniqueCuts(plan.Distances, result.OriginalLength);
                    if (result.Cuts.Count == 0)
                    {
                        failure = "A selected affected source had no valid internal cut distance.";
                        return false;
                    }

                    DBObjectCollection pieces = BuildCandidatePieces(source, result.Cuts);
                    if (pieces == null || pieces.Count != result.Cuts.Count + 1)
                    {
                        DisposeUnowned(pieces);
                        failure = "Civil 3D did not return every expected replacement span.";
                        return false;
                    }

                    BlockTableRecord owner = transaction.GetObject(
                        source.OwnerId,
                        OpenMode.ForWrite,
                        false) as BlockTableRecord;
                    if (owner == null)
                    {
                        DisposeUnowned(pieces);
                        failure = "The source drawing space could not be opened for replacement geometry.";
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
                            failure = "One generated split span was invalid.";
                            return false;
                        }

                        try { entity.SetPropertiesFrom(source); } catch { }
                        entity.LayerId = source.LayerId;
                        ObjectId newId = owner.AppendEntity(entity);
                        transaction.AddNewlyCreatedDBObject(entity, true);
                        try { entity.RecordGraphicsModified(true); } catch { }
                        result.NewIds.Add(newId);
                    }

                    if (result.NewIds.Count != result.Cuts.Count + 1)
                    {
                        failure = "Not all expected split spans were appended.";
                        return false;
                    }
                    transaction.Commit();
                }

                staged = result;
                return true;
            }
            catch (System.Exception exception)
            {
                failure = exception.Message;
                CleanupPersisted(database, result.NewIds);
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

        private static bool VerifyPersistedReplacement(Database database, StagedSource staged)
        {
            try
            {
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    Curve source = transaction.GetObject(
                        staged.SourceId,
                        OpenMode.ForRead,
                        false) as Curve;
                    if (source == null || source.IsErased) return false;
                    return VerifyCompleteCoverage(
                        source,
                        staged.NewIds,
                        staged.Cuts,
                        staged.OriginalLength,
                        transaction);
                }
            }
            catch { return false; }
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
            if (values == null) return result;
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

        private static bool LayerLocked(Transaction transaction, ObjectId layerId)
        {
            try
            {
                LayerTableRecord layer = transaction.GetObject(
                    layerId,
                    OpenMode.ForRead,
                    false) as LayerTableRecord;
                return layer != null && layer.IsLocked;
            }
            catch { return true; }
        }

        private static void CleanupPersisted(Database database, IEnumerable<ObjectId> ids)
        {
            if (database == null || ids == null) return;
            try
            {
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId id in ids.Distinct())
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

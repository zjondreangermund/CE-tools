using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace CETools.Civil3D
{
    /// <summary>
    /// Field-safe implementation for CE_PLBREAKJUNCTIONS. Every selected source
    /// is analysed while all originals still exist. Replacement pieces are then
    /// created and committed first, verified in a second transaction, and only
    /// after successful verification is that one original erased. A failed source
    /// is left untouched and any provisional pieces are removed.
    /// </summary>
    internal static class August21SafePolylineBreakEngine
    {
        private const double Tolerance = 0.000001;

        private sealed class SplitPlan
        {
            public ObjectId SourceId;
            public List<Point3d> Points = new List<Point3d>();
        }

        internal static void Run(Document document)
        {
            if (document == null) return;

            PromptSelectionResult selection = document.Editor.GetSelection(
                new PromptSelectionOptions
                {
                    MessageForAdding = "\nSelect all 2D/3D polylines to break at crossings and T-junctions: ",
                    AllowDuplicates = false,
                    RejectObjectsFromNonCurrentSpace = true
                },
                new SelectionFilter(new[]
                {
                    new TypedValue((int)DxfCode.Start, "LWPOLYLINE,POLYLINE")
                }));
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;

            List<ObjectId> ids = selection.Value.GetObjectIds()
                .Where(id => !id.IsNull && !id.IsErased)
                .Distinct()
                .ToList();
            if (ids.Count < 2)
            {
                document.Editor.WriteMessage(
                    "\nCE_PLBREAKJUNCTIONS: select at least two intersecting polylines.");
                return;
            }

            Dictionary<ObjectId, SplitPlan> plans;
            int junctions;
            try
            {
                plans = Analyse(document.Database, ids, out junctions);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_PLBREAKJUNCTIONS stopped during read-only intersection analysis. {0}",
                    exception.Message);
                return;
            }

            int affected = plans.Values.Count(plan => plan.Points.Count > 0);
            if (junctions == 0 || affected == 0)
            {
                document.Editor.WriteMessage(
                    "\nCE_PLBREAKJUNCTIONS: no internal crossings or T-junctions were found. Endpoint-to-endpoint connections were retained.");
                return;
            }

            if (!PopupTablePresenter.ShowReview(
                "CE Tools - Safe Polyline Junction Preview",
                "Accept to split each affected source independently. CE Tools commits and verifies every replacement set before erasing that source. A failed source remains untouched.",
                new List<KeyValuePair<string, string>>
                {
                    Pair("Selected polylines", ids.Count),
                    Pair("Unique crossing/junction locations", junctions),
                    Pair("Polylines to split", affected)
                },
                "Break Polylines Safely"))
                return;

            int replaced = 0;
            int created = 0;
            int preserved = 0;
            foreach (SplitPlan plan in plans.Values.Where(value => value.Points.Count > 0))
            {
                int createdForSource;
                if (TryReplaceOne(document.Database, plan, out createdForSource))
                {
                    replaced++;
                    created += createdForSource;
                }
                else
                {
                    preserved++;
                }
            }

            August21DisplayRefresh.Flush(document);
            document.Editor.WriteMessage(
                "\nCE_PLBREAKJUNCTIONS complete. Sources safely replaced={0}; segments created={1}; failed sources preserved={2}; junctions={3}.",
                replaced,
                created,
                preserved,
                junctions);
        }

        private static Dictionary<ObjectId, SplitPlan> Analyse(
            Database database,
            IList<ObjectId> ids,
            out int uniqueIntersections)
        {
            var result = ids.ToDictionary(
                id => id,
                id => new SplitPlan { SourceId = id });
            var unique = new List<Point3d>();

            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                for (int firstIndex = 0; firstIndex < ids.Count; firstIndex++)
                {
                    Curve first = transaction.GetObject(
                        ids[firstIndex], OpenMode.ForRead, false) as Curve;
                    if (first == null || first.IsErased) continue;

                    for (int secondIndex = firstIndex + 1; secondIndex < ids.Count; secondIndex++)
                    {
                        Curve second = transaction.GetObject(
                            ids[secondIndex], OpenMode.ForRead, false) as Curve;
                        if (second == null || second.IsErased) continue;

                        var intersections = new Point3dCollection();
                        try
                        {
                            first.IntersectWith(
                                second,
                                Intersect.OnBothOperands,
                                intersections,
                                IntPtr.Zero,
                                IntPtr.Zero);
                        }
                        catch
                        {
                            continue;
                        }

                        foreach (Point3d intersection in intersections)
                        {
                            AddInternal(first, intersection, result[ids[firstIndex]].Points);
                            AddInternal(second, intersection, result[ids[secondIndex]].Points);
                            AddUnique(unique, intersection);
                        }
                    }
                }
            }

            uniqueIntersections = unique.Count;
            return result;
        }

        private static void AddInternal(
            Curve curve,
            Point3d candidate,
            IList<Point3d> points)
        {
            try
            {
                Point3d onCurve = curve.GetClosestPointTo(candidate, false);
                double distance = curve.GetDistAtPoint(onCurve);
                double length = CurveLength(curve);
                if (distance <= Tolerance || length - distance <= Tolerance) return;
                AddUnique(points, onCurve);
            }
            catch { }
        }

        private static void AddUnique(IList<Point3d> points, Point3d point)
        {
            if (points.Any(existing => existing.DistanceTo(point) <= Tolerance)) return;
            points.Add(point);
        }

        private static bool TryReplaceOne(
            Database database,
            SplitPlan plan,
            out int createdCount)
        {
            createdCount = 0;
            if (plan == null || plan.SourceId.IsNull || plan.SourceId.IsErased ||
                plan.Points == null || plan.Points.Count == 0)
                return false;

            var newIds = new List<ObjectId>();
            double originalLength = 0.0;
            ObjectId ownerId = ObjectId.Null;

            try
            {
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    Curve source = transaction.GetObject(
                        plan.SourceId, OpenMode.ForRead, false) as Curve;
                    if (source == null || source.IsErased) return false;
                    originalLength = CurveLength(source);
                    if (originalLength <= Tolerance) return false;
                    ownerId = source.OwnerId;

                    Point3d[] ordered = plan.Points
                        .Select(point => source.GetClosestPointTo(point, false))
                        .OrderBy(point => source.GetDistAtPoint(point))
                        .ToArray();
                    if (ordered.Length == 0) return false;

                    DBObjectCollection pieces = source.GetSplitCurves(
                        new Point3dCollection(ordered));
                    if (pieces == null || pieces.Count != ordered.Length + 1)
                    {
                        DisposePieces(pieces);
                        return false;
                    }

                    double proposedLength = 0.0;
                    foreach (DBObject value in pieces)
                    {
                        Curve curve = value as Curve;
                        if (curve == null)
                        {
                            DisposePieces(pieces);
                            return false;
                        }
                        double length = CurveLength(curve);
                        if (length <= Tolerance)
                        {
                            DisposePieces(pieces);
                            return false;
                        }
                        proposedLength += length;
                    }
                    if (!LengthMatches(originalLength, proposedLength))
                    {
                        DisposePieces(pieces);
                        return false;
                    }

                    BlockTableRecord owner = transaction.GetObject(
                        ownerId, OpenMode.ForWrite, false) as BlockTableRecord;
                    if (owner == null)
                    {
                        DisposePieces(pieces);
                        return false;
                    }

                    foreach (DBObject value in pieces)
                    {
                        Entity entity = value as Entity;
                        if (entity == null) continue;
                        try { entity.SetPropertiesFrom(source); } catch { }
                        ObjectId id = owner.AppendEntity(entity);
                        transaction.AddNewlyCreatedDBObject(entity, true);
                        try { entity.RecordGraphicsModified(true); } catch { }
                        newIds.Add(id);
                    }
                    transaction.Commit();
                }

                if (newIds.Count < 2 || !VerifyReplacement(
                        database, plan.SourceId, newIds, originalLength))
                {
                    Cleanup(database, newIds);
                    return false;
                }

                try
                {
                    using (Transaction transaction = database.TransactionManager.StartTransaction())
                    {
                        Entity source = transaction.GetObject(
                            plan.SourceId, OpenMode.ForWrite, false) as Entity;
                        if (source == null || source.IsErased)
                        {
                            Cleanup(database, newIds);
                            return false;
                        }
                        source.Erase();
                        transaction.Commit();
                    }
                }
                catch
                {
                    Cleanup(database, newIds);
                    return false;
                }

                createdCount = newIds.Count;
                return true;
            }
            catch
            {
                Cleanup(database, newIds);
                return false;
            }
        }

        private static bool VerifyReplacement(
            Database database,
            ObjectId sourceId,
            IList<ObjectId> newIds,
            double originalLength)
        {
            try
            {
                double total = 0.0;
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    Curve source = transaction.GetObject(
                        sourceId, OpenMode.ForRead, false) as Curve;
                    if (source == null || source.IsErased) return false;

                    foreach (ObjectId id in newIds)
                    {
                        if (id.IsNull || id.IsErased) return false;
                        Curve segment = transaction.GetObject(
                            id, OpenMode.ForRead, false) as Curve;
                        if (segment == null || segment.IsErased) return false;
                        double length = CurveLength(segment);
                        if (length <= Tolerance) return false;
                        total += length;
                    }
                }
                return LengthMatches(originalLength, total);
            }
            catch
            {
                return false;
            }
        }

        private static void Cleanup(Database database, IEnumerable<ObjectId> ids)
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
                            Entity entity = transaction.GetObject(
                                id, OpenMode.ForWrite, false) as Entity;
                            if (entity != null && !entity.IsErased) entity.Erase();
                        }
                        catch { }
                    }
                    transaction.Commit();
                }
            }
            catch { }
        }

        private static double CurveLength(Curve curve)
        {
            if (curve == null) return 0.0;
            try
            {
                double start = curve.GetDistanceAtParameter(curve.StartParam);
                double end = curve.GetDistanceAtParameter(curve.EndParam);
                return Math.Abs(end - start);
            }
            catch
            {
                return 0.0;
            }
        }

        private static bool LengthMatches(double expected, double actual)
        {
            double tolerance = Math.Max(0.00001, Math.Abs(expected) * 0.0000001);
            return Math.Abs(expected - actual) <= tolerance;
        }

        private static void DisposePieces(DBObjectCollection pieces)
        {
            if (pieces == null) return;
            foreach (DBObject value in pieces)
            {
                try { value.Dispose(); } catch { }
            }
        }

        private static KeyValuePair<string, string> Pair(string name, int value)
        {
            return new KeyValuePair<string, string>(
                name,
                value.ToString(CultureInfo.CurrentCulture));
        }
    }

    internal static class August21DisplayRefresh
    {
        internal static void Flush(Document document)
        {
            if (document == null) return;
            try { document.Database.TransactionManager.QueueForGraphicsFlush(); } catch { }
            try { document.Editor.Regen(); } catch { }
            try { Autodesk.AutoCAD.ApplicationServices.Core.Application.UpdateScreen(); } catch { }
            try
            {
                document.SendStringToExecute("_.REGEN ", true, false, false);
            }
            catch { }
        }
    }
}

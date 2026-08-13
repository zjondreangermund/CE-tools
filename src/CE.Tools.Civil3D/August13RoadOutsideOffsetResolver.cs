using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace CETools.Civil3D
{
    /// <summary>
    /// Resolves the CE road centreline that owns an EDGE/SHOULDER through the
    /// existing CE_ROAD_LAYOUT parent-handle chain, then chooses only the offset
    /// candidate that moves farther away from that same centreline. This prevents
    /// the opposite road edge from being offset back into the carriageway when a
    /// nearby/crossing centreline becomes the nearest object on the other side.
    /// </summary>
    internal static class August13RoadOutsideOffsetResolver
    {
        private const string RecordKey = "CE_ROAD_LAYOUT";
        private const string CenterLayer = "CE-ROAD-CENTERLINE";
        private const double Tol = 1e-7;

        internal static Curve ChooseOutsideOffset(
            Polyline source,
            double distance,
            Transaction transaction,
            Database database,
            IList<Polyline> fallbackCentres)
        {
            if (source == null || transaction == null || database == null || distance <= Tol)
                return null;

            Polyline parentCentre = ResolveParentCentreline(source, transaction, database);
            if (parentCentre == null)
                parentCentre = ChooseFallbackParent(source, fallbackCentres);

            var candidates = new List<Curve>();
            foreach (double signed in new[] { -Math.Abs(distance), Math.Abs(distance) })
            {
                DBObjectCollection offsets;
                try { offsets = source.GetOffsetCurves(signed); }
                catch { continue; }

                foreach (DBObject value in offsets)
                {
                    Curve curve = value as Curve;
                    if (curve != null) candidates.Add(curve);
                    else value.Dispose();
                }
            }

            if (candidates.Count == 0) return null;

            // A linked parent centreline is the authoritative reference. Compare
            // both signed candidates against that same parent instead of asking
            // which centreline happens to be nearest after the offset is made.
            if (parentCentre != null)
            {
                double sourceDistance = AverageDistanceToCentre(source, parentCentre);
                Curve best = null;
                double bestGain = double.MinValue;
                double minimumGain = Math.Max(Math.Abs(distance) * 0.05, 1e-5);

                foreach (Curve candidate in candidates)
                {
                    double candidateDistance = AverageDistanceToCentre(candidate, parentCentre);
                    double gain = candidateDistance - sourceDistance;
                    double sameSideFraction = SameSideFraction(source, candidate, parentCentre);

                    // The outside offset must increase separation from the parent
                    // centreline and remain on the same side of it as its source.
                    // This also rejects a very large offset that crosses the road
                    // centre and lands farther away on the opposite carriageway.
                    bool outward = gain > minimumGain && sameSideFraction >= 0.60;
                    if (outward && gain > bestGain)
                    {
                        if (best != null) best.Dispose();
                        best = candidate;
                        bestGain = gain;
                    }
                    else
                    {
                        candidate.Dispose();
                    }
                }

                return best;
            }

            // Legacy/unlinked geometry fallback. Keep the old behaviour only when
            // no CE parent chain can be resolved, but score several points rather
            // than a single midpoint so junctions are less likely to flip sides.
            Curve fallbackBest = null;
            double fallbackScore = double.MinValue;
            foreach (Curve candidate in candidates)
            {
                double score = AverageDistanceToNearestCentre(candidate, fallbackCentres);
                if (score > fallbackScore)
                {
                    if (fallbackBest != null) fallbackBest.Dispose();
                    fallbackBest = candidate;
                    fallbackScore = score;
                }
                else
                {
                    candidate.Dispose();
                }
            }
            return fallbackBest;
        }

        private static Polyline ResolveParentCentreline(
            Entity source,
            Transaction transaction,
            Database database)
        {
            Entity current = source;
            var visited = new HashSet<ObjectId>();

            for (int depth = 0; depth < 5 && current != null; depth++)
            {
                Polyline currentPolyline = current as Polyline;
                if (currentPolyline != null &&
                    string.Equals(currentPolyline.Layer, CenterLayer, StringComparison.OrdinalIgnoreCase))
                    return currentPolyline;

                string parentHandle = ReadParentHandle(current, transaction);
                if (string.IsNullOrWhiteSpace(parentHandle)) return null;

                ObjectId parentId = ResolveHandle(database, parentHandle);
                if (parentId.IsNull || parentId.IsErased || !visited.Add(parentId)) return null;

                try { current = transaction.GetObject(parentId, OpenMode.ForRead, false) as Entity; }
                catch { return null; }
            }

            return null;
        }

        private static string ReadParentHandle(Entity entity, Transaction transaction)
        {
            if (entity == null || transaction == null || entity.ExtensionDictionary.IsNull)
                return string.Empty;

            try
            {
                DBDictionary dictionary = transaction.GetObject(
                    entity.ExtensionDictionary,
                    OpenMode.ForRead,
                    false) as DBDictionary;
                if (dictionary == null || !dictionary.Contains(RecordKey))
                    return string.Empty;

                Xrecord record = transaction.GetObject(
                    dictionary.GetAt(RecordKey),
                    OpenMode.ForRead,
                    false) as Xrecord;
                TypedValue[] values = record == null || record.Data == null
                    ? null
                    : record.Data.AsArray();
                if (values == null || values.Length < 2)
                    return string.Empty;

                return Convert.ToString(values[1].Value, CultureInfo.InvariantCulture) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static ObjectId ResolveHandle(Database database, string text)
        {
            long value;
            if (database == null || string.IsNullOrWhiteSpace(text) ||
                !long.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value))
                return ObjectId.Null;

            try { return database.GetObjectId(false, new Handle(value), 0); }
            catch { return ObjectId.Null; }
        }

        private static Polyline ChooseFallbackParent(Polyline source, IList<Polyline> centres)
        {
            if (source == null || centres == null || centres.Count == 0) return null;
            return centres
                .Where(item => item != null && !item.IsErased)
                .OrderBy(item => AverageDistanceToCentre(source, item))
                .FirstOrDefault();
        }

        private static double AverageDistanceToCentre(Curve curve, Curve centre)
        {
            if (curve == null || centre == null) return double.MaxValue;
            double total = 0.0;
            int count = 0;
            foreach (double fraction in SampleFractions())
            {
                Point3d point;
                try { point = PointAtFraction(curve, fraction); }
                catch { continue; }

                try
                {
                    Point3d closest = centre.GetClosestPointTo(point, false);
                    total += point.DistanceTo(closest);
                    count++;
                }
                catch { }
            }
            return count == 0 ? double.MaxValue : total / count;
        }

        private static double AverageDistanceToNearestCentre(Curve curve, IList<Polyline> centres)
        {
            if (curve == null) return double.MinValue;
            if (centres == null || centres.Count == 0) return 0.0;

            double total = 0.0;
            int count = 0;
            foreach (double fraction in SampleFractions())
            {
                Point3d point;
                try { point = PointAtFraction(curve, fraction); }
                catch { continue; }

                double nearest = double.MaxValue;
                foreach (Polyline centre in centres)
                {
                    if (centre == null || centre.IsErased) continue;
                    try
                    {
                        double current = point.DistanceTo(centre.GetClosestPointTo(point, false));
                        if (current < nearest) nearest = current;
                    }
                    catch { }
                }

                if (nearest < double.MaxValue)
                {
                    total += nearest;
                    count++;
                }
            }
            return count == 0 ? 0.0 : total / count;
        }

        private static double SameSideFraction(Curve source, Curve candidate, Curve centre)
        {
            int same = 0;
            int compared = 0;
            foreach (double fraction in SampleFractions())
            {
                try
                {
                    Point3d sourcePoint = PointAtFraction(source, fraction);
                    Point3d candidatePoint = PointAtFraction(candidate, fraction);
                    Point3d sourceCentre = centre.GetClosestPointTo(sourcePoint, false);
                    Point3d candidateCentre = centre.GetClosestPointTo(candidatePoint, false);
                    Vector2d sourceVector = new Vector2d(
                        sourcePoint.X - sourceCentre.X,
                        sourcePoint.Y - sourceCentre.Y);
                    Vector2d candidateVector = new Vector2d(
                        candidatePoint.X - candidateCentre.X,
                        candidatePoint.Y - candidateCentre.Y);
                    if (sourceVector.Length <= Tol || candidateVector.Length <= Tol) continue;
                    compared++;
                    if (sourceVector.DotProduct(candidateVector) > 0.0) same++;
                }
                catch { }
            }
            return compared == 0 ? 0.0 : (double)same / compared;
        }

        private static IEnumerable<double> SampleFractions()
        {
            yield return 0.10;
            yield return 0.30;
            yield return 0.50;
            yield return 0.70;
            yield return 0.90;
        }

        private static Point3d PointAtFraction(Curve curve, double fraction)
        {
            double total = curve.GetDistanceAtParameter(curve.EndParam);
            if (total <= Tol) return curve.StartPoint;
            double clamped = Math.Max(0.0, Math.Min(1.0, fraction));
            return curve.GetPointAtDist(total * clamped);
        }
    }
}

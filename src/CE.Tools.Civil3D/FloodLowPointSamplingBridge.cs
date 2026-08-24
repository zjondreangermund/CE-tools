using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.DatabaseServices;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivilFeatureLine = Autodesk.Civil.DatabaseServices.FeatureLine;
using CivilFeatureLinePointType = Autodesk.Civil.FeatureLinePointType;
using CivilTinSurface = Autodesk.Civil.DatabaseServices.TinSurface;

namespace CETools.Civil3D
{
    /// <summary>
    /// Finds the true reviewed low point for Flood Production crossing sources.
    /// Alignments and 2D polylines are sampled against the selected TIN surface;
    /// feature lines use their complete Civil 3D control/elevation point set.
    /// </summary>
    internal static class FloodLowPointSamplingBridge
    {
        private const int MaximumSamples = 20000;

        internal static CrossingLowPoint FindLowestPoint(
            Database database,
            ObjectId surfaceId,
            ObjectId sourceId,
            double sampleStepDrawing,
            double unitsPerMetre)
        {
            if (database == null || sourceId.IsNull) return null;
            double step = Math.Max(sampleStepDrawing, Math.Max(0.01, unitsPerMetre * 0.10));
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                CivilTinSurface surface = transaction.GetObject(surfaceId, OpenMode.ForRead, false) as CivilTinSurface;
                DBObject source = transaction.GetObject(sourceId, OpenMode.ForRead, false);
                if (surface == null || source == null) return null;

                CivilAlignment alignment = source as CivilAlignment;
                if (alignment != null)
                    return FromAlignment(alignment, surface, step, unitsPerMetre);

                Polyline polyline = source as Polyline;
                if (polyline != null)
                    return FromPolyline(polyline, surface, step, unitsPerMetre);

                CivilFeatureLine featureLine = source as CivilFeatureLine;
                if (featureLine != null)
                    return FromFeatureLine(featureLine, unitsPerMetre);
            }
            return null;
        }

        private static CrossingLowPoint FromAlignment(
            CivilAlignment alignment,
            CivilTinSurface surface,
            double step,
            double unitsPerMetre)
        {
            double start = alignment.StartingStation;
            double end = alignment.EndingStation;
            double stationLength = Math.Abs(end - start);
            int samples = Math.Max(2, Math.Min(MaximumSamples, (int)Math.Ceiling(stationLength / step) + 1));
            CrossingLowPoint best = null;
            for (int index = 0; index < samples; index++)
            {
                double station = start + (end - start) * index / (samples - 1.0);
                double x = 0.0, y = 0.0;
                try { alignment.PointLocation(station, 0.0, ref x, ref y); }
                catch { continue; }
                double z;
                try { z = surface.FindElevationAtXY(x, y); }
                catch { continue; }
                if (best == null || z < best.Point.Z)
                    best = new CrossingLowPoint(
                        new Point3d(x, y, z),
                        station,
                        "Alignment",
                        alignment.Name);
            }
            return best;
        }

        private static CrossingLowPoint FromPolyline(
            Polyline polyline,
            CivilTinSurface surface,
            double step,
            double unitsPerMetre)
        {
            double length;
            try { length = polyline.Length; }
            catch { return null; }
            int samples = Math.Max(2, Math.Min(MaximumSamples, (int)Math.Ceiling(length / step) + 1));
            CrossingLowPoint best = null;
            for (int index = 0; index < samples; index++)
            {
                double distance = length * index / (samples - 1.0);
                Point3d plan;
                try { plan = polyline.GetPointAtDist(distance); }
                catch { continue; }
                double z;
                try { z = surface.FindElevationAtXY(plan.X, plan.Y); }
                catch { continue; }
                if (best == null || z < best.Point.Z)
                    best = new CrossingLowPoint(
                        new Point3d(plan.X, plan.Y, z),
                        distance / unitsPerMetre,
                        "Polyline",
                        polyline.Layer);
            }
            return best;
        }

        private static CrossingLowPoint FromFeatureLine(
            CivilFeatureLine featureLine,
            double unitsPerMetre)
        {
            Point3dCollection points;
            try { points = featureLine.GetPoints(CivilFeatureLinePointType.AllPoints); }
            catch { return null; }
            if (points == null || points.Count == 0) return null;

            List<Point3d> values = points.Cast<Point3d>().ToList();
            int low = 0;
            for (int index = 1; index < values.Count; index++)
                if (values[index].Z < values[low].Z) low = index;

            double chainage = 0.0;
            for (int index = 1; index <= low; index++)
            {
                double dx = values[index].X - values[index - 1].X;
                double dy = values[index].Y - values[index - 1].Y;
                chainage += Math.Sqrt(dx * dx + dy * dy) / unitsPerMetre;
            }
            return new CrossingLowPoint(
                values[low],
                chainage,
                "Feature Line",
                featureLine.Layer);
        }
    }
}

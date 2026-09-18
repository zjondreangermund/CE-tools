using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil;
using Autodesk.Civil.ApplicationServices;
using CivilFeatureLine = Autodesk.Civil.DatabaseServices.FeatureLine;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace CETools.Civil3D
{
    /// <summary>
    /// Civil 3D 2023 safety boundary for surface-driven commands.
    ///
    /// Surface objects are opened only in a short read transaction. Their sampled
    /// elevations are copied to plain managed values before any feature-line write
    /// transaction begins. This deliberately avoids keeping a Civil Surface DBObject
    /// alive while AutoCAD/Civil geometry is being created, erased or edited.
    ///
    /// FeatureLine.SetPointElevation uses the feature line's PI-point index. Passing
    /// indexes obtained from AllPoints can include elevation/intermediate points and
    /// can drive Civil 3D into native instability. Surface application therefore
    /// samples and writes PI points only. Native intermediate-TIN insertion remains
    /// disabled in this fatal-safe path.
    /// </summary>
    internal static class August21SurfaceSafety
    {
        internal static ObjectId ResolveFreshSurfaceId(Document document, string surfaceName)
        {
            if (document == null || document.Database == null ||
                string.IsNullOrWhiteSpace(surfaceName) ||
                string.Equals(surfaceName, August20SurfaceChoice.None, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(surfaceName, "<Keep source elevations>", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(surfaceName, "<Pick surface in drawing>", StringComparison.OrdinalIgnoreCase))
                return ObjectId.Null;

            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (civilDocument == null) return ObjectId.Null;

            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId candidate in civilDocument.GetSurfaceIds())
                {
                    if (candidate.IsNull || candidate.IsErased) continue;
                    CivilSurface surface = null;
                    try
                    {
                        surface = transaction.GetObject(candidate, OpenMode.ForRead, false) as CivilSurface;
                    }
                    catch
                    {
                        continue;
                    }
                    if (surface != null && string.Equals(
                            surface.Name,
                            surfaceName,
                            StringComparison.OrdinalIgnoreCase))
                        return candidate;
                }
            }
            return ObjectId.Null;
        }

        internal static string ReadSurfaceName(Document document, ObjectId surfaceId)
        {
            if (document == null || document.Database == null ||
                surfaceId.IsNull || surfaceId.IsErased)
                return string.Empty;
            try
            {
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    CivilSurface surface = transaction.GetObject(
                        surfaceId,
                        OpenMode.ForRead,
                        false) as CivilSurface;
                    return surface == null ? string.Empty : (surface.Name ?? string.Empty);
                }
            }
            catch
            {
                return string.Empty;
            }
        }

        internal static bool TryApplyFeatureLineElevations(
            Document document,
            ObjectId featureLineId,
            string surfaceName,
            bool includeIntermediate,
            out string error)
        {
            error = string.Empty;
            if (document == null || document.Database == null)
            {
                error = "No active drawing is available.";
                return false;
            }
            if (featureLineId.IsNull || featureLineId.IsErased)
            {
                error = "The feature line is unavailable.";
                return false;
            }

            ObjectId surfaceId = ResolveFreshSurfaceId(document, surfaceName);
            if (surfaceId.IsNull)
            {
                error = "The selected Civil 3D surface could not be resolved safely by name.";
                return false;
            }

            List<Point3d> sourcePoints;
            try
            {
                using (Transaction readFeatureLine =
                    document.Database.TransactionManager.StartTransaction())
                {
                    CivilFeatureLine featureLine = readFeatureLine.GetObject(
                        featureLineId,
                        OpenMode.ForRead,
                        false) as CivilFeatureLine;
                    if (featureLine == null || featureLine.IsReferenceObject)
                    {
                        error = "The selected object is not an editable feature line.";
                        return false;
                    }
                    Point3dCollection collection = featureLine.GetPoints(
                        FeatureLinePointType.PIPoint);
                    sourcePoints = collection == null
                        ? new List<Point3d>()
                        : collection.Cast<Point3d>().ToList();
                }
            }
            catch (System.Exception exception)
            {
                error = "Feature-line PI read failed: " + exception.Message;
                return false;
            }

            if (sourcePoints.Count == 0)
            {
                error = "The feature line has no readable PI elevation points.";
                return false;
            }

            var sampled = new List<double?>(sourcePoints.Count);
            int sampledCount = 0;
            try
            {
                using (Transaction surfaceRead =
                    document.Database.TransactionManager.StartTransaction())
                {
                    CivilSurface surface = surfaceRead.GetObject(
                        surfaceId,
                        OpenMode.ForRead,
                        false) as CivilSurface;
                    if (surface == null)
                    {
                        error = "The selected Civil 3D surface is not readable.";
                        return false;
                    }

                    foreach (Point3d point in sourcePoints)
                    {
                        try
                        {
                            double elevation = surface.FindElevationAtXY(point.X, point.Y);
                            if (double.IsNaN(elevation) || double.IsInfinity(elevation))
                                sampled.Add(null);
                            else
                            {
                                sampled.Add(elevation);
                                sampledCount++;
                            }
                        }
                        catch
                        {
                            // A point outside the TIN/boundary is left unchanged.
                            sampled.Add(null);
                        }
                    }
                }
            }
            catch (System.Exception exception)
            {
                error = "Surface sampling failed safely: " + exception.Message;
                return false;
            }

            if (sampledCount == 0)
            {
                error = "The selected surface has no readable elevations at the feature-line PI points.";
                return false;
            }

            try
            {
                using (Transaction writeFeatureLine =
                    document.Database.TransactionManager.StartTransaction())
                {
                    CivilFeatureLine featureLine = writeFeatureLine.GetObject(
                        featureLineId,
                        OpenMode.ForWrite,
                        false) as CivilFeatureLine;
                    if (featureLine == null || featureLine.IsReferenceObject)
                    {
                        error = "The feature line is no longer editable.";
                        return false;
                    }

                    Point3dCollection currentPi = featureLine.GetPoints(
                        FeatureLinePointType.PIPoint);
                    ApplyPointElevations(
                        featureLine,
                        currentPi,
                        sourcePoints,
                        sampled);
                    try { featureLine.RecordGraphicsModified(true); } catch { }
                    writeFeatureLine.Commit();
                }
            }
            catch (System.Exception exception)
            {
                error = "Feature-line PI elevation write failed safely: " + exception.Message;
                return false;
            }

            // Make the surface relationship in its own transaction after the
            // absolute PI elevations are safely committed.  This avoids the
            // out-of-range/zero-elevation state seen when Civil 3D 2023 is asked to
            // switch the same freshly-created points to relative mode while they
            // are still being rewritten.
            TryLinkRelativeToSurface(
                document,
                featureLineId,
                surfaceId);

            string verificationError;
            if (!VerifyAppliedElevations(
                    document,
                    featureLineId,
                    sourcePoints,
                    sampled,
                    out verificationError))
            {
                // Keep a correct absolute feature line even if the host rejects
                // relative-to-surface state.  A separate committed retry uses
                // fresh PI coordinates and therefore cannot address stale indexes.
                if (!RetryAbsoluteElevations(
                        document,
                        featureLineId,
                        sourcePoints,
                        sampled,
                        out verificationError))
                {
                    error = verificationError;
                    return false;
                }
            }

            // Civil 3D's native AssignElevationsFromSurface(..., true) is not used
            // here because it can keep the Surface object active while Civil mutates
            // the feature line. The UI option is retained, but fatal-safe creation
            // intentionally updates existing PI points only.
            try
            {
                document.Database.TransactionManager.QueueForGraphicsFlush();
                document.Editor.Regen();
                AcApplication.UpdateScreen();
            }
            catch { }
            August21GraphicsRefreshManager.MarkDirty();
            return true;
        }

        private static void ApplyPointElevations(
            CivilFeatureLine featureLine,
            Point3dCollection currentPi,
            IList<Point3d> sampledPoints,
            IList<double?> elevations)
        {
            if (featureLine == null ||
                currentPi == null ||
                sampledPoints == null ||
                elevations == null)
                return;

            int written = 0;
            int count = Math.Min(sampledPoints.Count, elevations.Count);
            for (int sampleIndex = 0; sampleIndex < count; sampleIndex++)
            {
                if (!elevations[sampleIndex].HasValue) continue;
                int piIndex = ClosestPlanPointIndex(
                    currentPi,
                    sampledPoints[sampleIndex]);
                if (piIndex < 0 || piIndex >= currentPi.Count) continue;

                featureLine.SetPointElevation(
                    piIndex,
                    elevations[sampleIndex].Value);
                written++;
            }

            if (written == 0)
                throw new InvalidOperationException(
                    "No sampled surface elevation could be matched to a writable feature-line PI point.");
        }

        private static int ClosestPlanPointIndex(
            Point3dCollection points,
            Point3d target)
        {
            if (points == null || points.Count == 0) return -1;
            int best = -1;
            double bestDistance = double.MaxValue;
            for (int index = 0; index < points.Count; index++)
            {
                double dx = points[index].X - target.X;
                double dy = points[index].Y - target.Y;
                double distance = (dx * dx) + (dy * dy);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = index;
                }
            }
            return best;
        }

        private static bool TryLinkRelativeToSurface(
            Document document,
            ObjectId featureLineId,
            ObjectId surfaceId)
        {
            if (document == null ||
                featureLineId.IsNull ||
                surfaceId.IsNull)
                return false;

            try
            {
                using (Transaction transaction =
                    document.Database.TransactionManager.StartTransaction())
                {
                    CivilFeatureLine featureLine = transaction.GetObject(
                        featureLineId,
                        OpenMode.ForWrite,
                        false) as CivilFeatureLine;
                    if (featureLine == null || featureLine.IsReferenceObject)
                        return false;

                    PropertyInfo surfaceProperty = featureLine.GetType().GetProperty(
                        "RelativeSurfaceId",
                        BindingFlags.Public | BindingFlags.Instance);
                    if (surfaceProperty == null ||
                        !surfaceProperty.CanWrite ||
                        surfaceProperty.PropertyType != typeof(ObjectId))
                        return false;

                    surfaceProperty.SetValue(featureLine, surfaceId, null);

                    Point3dCollection points = featureLine.GetPoints(
                        FeatureLinePointType.PIPoint);
                    int linked = 0;
                    if (points != null)
                    {
                        // Always use a fresh point collection after the absolute
                        // elevation transaction. SetPointRelativeElevation requires
                        // a point that is actually on the current feature line.
                        foreach (Point3d point in points.Cast<Point3d>().ToList())
                        {
                            try
                            {
                                featureLine.SetPointRelativeElevation(
                                    point,
                                    true,
                                    0.0);
                                linked++;
                            }
                            catch { }
                        }
                    }

                    try { featureLine.RecordGraphicsModified(true); } catch { }
                    transaction.Commit();
                    return linked > 0;
                }
            }
            catch { return false; }
        }

        private static bool VerifyAppliedElevations(
            Document document,
            ObjectId featureLineId,
            IList<Point3d> sampledPoints,
            IList<double?> elevations,
            out string error)
        {
            error = string.Empty;
            if (document == null ||
                featureLineId.IsNull ||
                sampledPoints == null ||
                elevations == null)
            {
                error = "Feature-line elevation verification could not start.";
                return false;
            }

            try
            {
                using (Transaction transaction =
                    document.Database.TransactionManager.StartTransaction())
                {
                    CivilFeatureLine featureLine = transaction.GetObject(
                        featureLineId,
                        OpenMode.ForRead,
                        false) as CivilFeatureLine;
                    if (featureLine == null)
                    {
                        error = "The created feature line could not be reopened for elevation verification.";
                        return false;
                    }

                    Point3dCollection currentPi = featureLine.GetPoints(
                        FeatureLinePointType.PIPoint);
                    int checkedCount = 0;
                    int count = Math.Min(sampledPoints.Count, elevations.Count);
                    for (int index = 0; index < count; index++)
                    {
                        if (!elevations[index].HasValue) continue;
                        int piIndex = ClosestPlanPointIndex(
                            currentPi,
                            sampledPoints[index]);
                        if (piIndex < 0 || piIndex >= currentPi.Count) continue;

                        double actual = currentPi[piIndex].Z;
                        double expected = elevations[index].Value;
                        if (double.IsNaN(actual) ||
                            double.IsInfinity(actual) ||
                            Math.Abs(actual - expected) > 0.05)
                        {
                            error = string.Format(
                                CultureInfo.CurrentCulture,
                                "Surface elevation verification failed at XY {0:0.###},{1:0.###}. Expected Z={2:0.###}; feature-line Z={3:0.###}.",
                                sampledPoints[index].X,
                                sampledPoints[index].Y,
                                expected,
                                actual);
                            return false;
                        }
                        checkedCount++;
                    }

                    if (checkedCount == 0)
                    {
                        error = "No feature-line PI elevation could be verified against the selected surface.";
                        return false;
                    }
                }
                return true;
            }
            catch (System.Exception exception)
            {
                error = "Feature-line elevation verification failed: " + exception.Message;
                return false;
            }
        }

        private static bool RetryAbsoluteElevations(
            Document document,
            ObjectId featureLineId,
            IList<Point3d> sampledPoints,
            IList<double?> elevations,
            out string error)
        {
            error = string.Empty;
            try
            {
                using (Transaction transaction =
                    document.Database.TransactionManager.StartTransaction())
                {
                    CivilFeatureLine featureLine = transaction.GetObject(
                        featureLineId,
                        OpenMode.ForWrite,
                        false) as CivilFeatureLine;
                    if (featureLine == null || featureLine.IsReferenceObject)
                    {
                        error = "The feature line is not editable during the absolute-elevation retry.";
                        return false;
                    }

                    Point3dCollection currentPi = featureLine.GetPoints(
                        FeatureLinePointType.PIPoint);
                    int count = Math.Min(sampledPoints.Count, elevations.Count);
                    int written = 0;
                    for (int index = 0; index < count; index++)
                    {
                        if (!elevations[index].HasValue) continue;
                        int piIndex = ClosestPlanPointIndex(
                            currentPi,
                            sampledPoints[index]);
                        if (piIndex < 0 || piIndex >= currentPi.Count) continue;

                        Point3d currentPoint = currentPi[piIndex];
                        try
                        {
                            if (featureLine.IsElevationRelativeToSurface(currentPoint))
                            {
                                featureLine.SetPointRelativeElevation(
                                    currentPoint,
                                    false,
                                    elevations[index].Value);
                            }
                            else
                            {
                                featureLine.SetPointElevation(
                                    piIndex,
                                    elevations[index].Value);
                            }
                            written++;
                        }
                        catch
                        {
                            try
                            {
                                featureLine.SetPointElevation(
                                    piIndex,
                                    elevations[index].Value);
                                written++;
                            }
                            catch { }
                        }
                    }

                    if (written == 0)
                    {
                        error = "Civil 3D rejected every sampled feature-line elevation.";
                        return false;
                    }
                    try { featureLine.RecordGraphicsModified(true); } catch { }
                    transaction.Commit();
                }

                return VerifyAppliedElevations(
                    document,
                    featureLineId,
                    sampledPoints,
                    elevations,
                    out error);
            }
            catch (System.Exception exception)
            {
                error = "Absolute feature-line elevation retry failed: " + exception.Message;
                return false;
            }
        }

        internal static bool TrySampleElevations(
            Document document,
            string surfaceName,
            IList<Point3d> points,
            out List<double?> elevations,
            out string error)
        {
            elevations = new List<double?>();
            error = string.Empty;
            if (points == null) return true;
            ObjectId surfaceId = ResolveFreshSurfaceId(document, surfaceName);
            if (surfaceId.IsNull)
            {
                error = "The selected Civil 3D surface could not be resolved safely by name.";
                return false;
            }
            try
            {
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    CivilSurface surface = transaction.GetObject(
                        surfaceId,
                        OpenMode.ForRead,
                        false) as CivilSurface;
                    if (surface == null)
                    {
                        error = "The selected Civil 3D surface is not readable.";
                        return false;
                    }
                    foreach (Point3d point in points)
                    {
                        try
                        {
                            double z = surface.FindElevationAtXY(point.X, point.Y);
                            elevations.Add(double.IsNaN(z) || double.IsInfinity(z)
                                ? (double?)null
                                : z);
                        }
                        catch { elevations.Add(null); }
                    }
                }
                return true;
            }
            catch (System.Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }
    }
}

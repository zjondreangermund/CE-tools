using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
        private const double Tolerance = 0.000001;

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

                    Point3dCollection current = featureLine.GetPoints(
                        FeatureLinePointType.PIPoint);
                    int count = Math.Min(
                        current == null ? 0 : current.Count,
                        sampled.Count);
                    for (int index = 0; index < count; index++)
                    {
                        if (!sampled[index].HasValue) continue;
                        if (Math.Abs(current[index].Z - sampled[index].Value) <= Tolerance)
                            continue;
                        featureLine.SetPointElevation(index, sampled[index].Value);
                    }
                    try { featureLine.RecordGraphicsModified(true); } catch { }
                    writeFeatureLine.Commit();
                }
            }
            catch (System.Exception exception)
            {
                error = "Feature-line PI elevation write failed safely: " + exception.Message;
                return false;
            }

            // Civil 3D's native AssignElevationsFromSurface(..., true) is not used
            // here because it can keep the Surface object active while Civil mutates
            // the feature line. The UI option is retained, but fatal-safe creation
            // intentionally updates existing PI points only. Intermediate TIN break
            // insertion can be added later without reintroducing the crash-prone
            // native mutation path.
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

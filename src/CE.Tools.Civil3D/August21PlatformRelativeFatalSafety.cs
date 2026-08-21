using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil;
using CivilFeatureLine = Autodesk.Civil.DatabaseServices.FeatureLine;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

namespace CETools.Civil3D
{
    /// <summary>
    /// Final fatal-safety boundary for linked/platform feature-line workflows.
    /// Native FeatureLine.Create is invoked only from committed temporary geometry.
    /// Rebuilds are candidate-first: an existing linked child is never erased until
    /// its replacement has been created, elevated, linked and verified.
    /// Surface draping samples first through August21SurfaceSafety and never keeps a
    /// Civil surface open while mutating a feature line.
    /// </summary>
    internal static class August21PlatformRelativeFatalSafety
    {
        private const string RelationKey = "CE_FLREL";
        private const string DrapeKey = "CE_PLATFORM_DRAPE";
        private const double Tolerance = 1e-7;

        internal sealed class PlatformStepResult
        {
            internal int Created { get; set; }
            internal int Skipped { get; set; }
        }

        internal static int CreateRelativeSet(
            Document document,
            ObjectId sourceId,
            double sign,
            double horizontalStep,
            double verticalStep,
            int count,
            string prefix)
        {
            if (document == null || sourceId.IsNull || count < 1) return 0;
            int created = 0;
            var names = ReadFeatureLineNames(document.Database);
            for (int sequence = 1; sequence <= count; sequence++)
            {
                double horizontal = sign * horizontalStep * sequence;
                double vertical = verticalStep * sequence;
                string requested = UniqueName(
                    (string.IsNullOrWhiteSpace(prefix) ? "FeatureLine-STEP" : prefix.Trim()) +
                    "-" + sequence.ToString(CultureInfo.InvariantCulture),
                    names);
                ObjectId childId;
                string error;
                if (!TryCreateOffsetCandidate(
                        document,
                        sourceId,
                        horizontal,
                        vertical,
                        requested,
                        ObjectId.Null,
                        null,
                        ObjectId.Null,
                        out childId,
                        out error))
                {
                    throw new InvalidOperationException(
                        "Step " + sequence.ToString(CultureInfo.InvariantCulture) +
                        " failed safely. " + error);
                }
                try
                {
                    string sourceHandle = sourceId.Handle.ToString();
                    WriteRelation(document, childId, new Relation
                    {
                        SourceHandle = sourceHandle,
                        HorizontalOffset = horizontal,
                        VerticalOffset = vertical,
                        Sequence = sequence
                    });
                    VerifyFeatureLine(document, childId);
                    created++;
                }
                catch
                {
                    Cleanup(document, childId);
                    throw;
                }
            }
            return created;
        }

        internal static int RebuildRelativeSource(Document document, ObjectId sourceId)
        {
            if (document == null || sourceId.IsNull || !sourceId.IsValid || sourceId.IsErased)
                return 0;

            List<ChildSnapshot> children = ReadChildren(document.Database, sourceId);
            if (children.Count == 0) return 0;

            ValidateSource(document, sourceId);
            int rebuilt = 0;
            foreach (ChildSnapshot old in children.OrderBy(item => item.Link.Sequence))
            {
                ObjectId candidateId = ObjectId.Null;
                try
                {
                    string candidateName = UniqueTemporaryName(document.Database, "CE_SAFE_FLREL");
                    string error;
                    if (!TryCreateOffsetCandidate(
                            document,
                            sourceId,
                            old.Link.HorizontalOffset,
                            old.Link.VerticalOffset,
                            candidateName,
                            old.LayerId,
                            old.StyleName,
                            old.SiteId,
                            out candidateId,
                            out error))
                    {
                        document.Editor.WriteMessage(
                            "\nLinked feature line '{0}' was kept because its replacement failed safely. {1}",
                            old.Name,
                            error);
                        continue;
                    }

                    WriteRelation(document, candidateId, old.Link);
                    VerifyFeatureLine(document, candidateId);
                    if (!CommitCandidateSwap(document, old, candidateId, out error))
                    {
                        Cleanup(document, candidateId);
                        document.Editor.WriteMessage(
                            "\nLinked feature line '{0}' was kept because the replacement swap failed safely. {1}",
                            old.Name,
                            error);
                        continue;
                    }
                    rebuilt++;
                }
                catch (System.Exception exception)
                {
                    Cleanup(document, candidateId);
                    document.Editor.WriteMessage(
                        "\nLinked feature line '{0}' was kept after a safe rebuild failure. {1}",
                        old.Name,
                        exception.Message);
                }
            }
            return rebuilt;
        }

        internal static PlatformStepResult CreatePlatformSteps(
            Document document,
            IEnumerable<ObjectId> sourceIds,
            double horizontal,
            double vertical,
            int count,
            string suffix)
        {
            var result = new PlatformStepResult();
            if (document == null || sourceIds == null) return result;
            var names = ReadFeatureLineNames(document.Database);
            foreach (ObjectId sourceId in sourceIds.Where(id => !id.IsNull).Distinct())
            {
                try
                {
                    bool closed;
                    string baseName;
                    using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                    {
                        CivilFeatureLine source = OpenFeatureLine(transaction, sourceId, OpenMode.ForRead);
                        EnsureEditable(source, transaction);
                        closed = source.Closed;
                        baseName = string.IsNullOrWhiteSpace(source.Name) ? "PLATFORM" : source.Name;
                    }

                    double sign = 1.0;
                    if (closed)
                    {
                        using (Polyline plan = ReadPlan(document, sourceId))
                            sign = OutwardSign(plan, Math.Max(horizontal, 0.001));
                    }

                    for (int sequence = 1; sequence <= count; sequence++)
                    {
                        string requested = UniqueName(
                            baseName + "-" +
                            (string.IsNullOrWhiteSpace(suffix) ? "STEP" : suffix.Trim()) + "-" +
                            sequence.ToString(CultureInfo.InvariantCulture),
                            names);
                        ObjectId childId;
                        string error;
                        double offset = sign * horizontal * sequence;
                        double dz = vertical * sequence;
                        if (!TryCreateOffsetCandidate(
                                document,
                                sourceId,
                                offset,
                                dz,
                                requested,
                                ObjectId.Null,
                                null,
                                ObjectId.Null,
                                out childId,
                                out error))
                        {
                            result.Skipped++;
                            document.Editor.WriteMessage(
                                "\nPlatform '{0}' step {1} was skipped safely. {2}",
                                baseName,
                                sequence,
                                error);
                            continue;
                        }
                        try
                        {
                            WriteRelation(document, childId, new Relation
                            {
                                SourceHandle = sourceId.Handle.ToString(),
                                HorizontalOffset = offset,
                                VerticalOffset = dz,
                                Sequence = sequence
                            });
                            VerifyFeatureLine(document, childId);
                            result.Created++;
                        }
                        catch (System.Exception exception)
                        {
                            Cleanup(document, childId);
                            result.Skipped++;
                            document.Editor.WriteMessage(
                                "\nPlatform '{0}' step {1} was removed after a safe link failure. {2}",
                                baseName,
                                sequence,
                                exception.Message);
                        }
                    }
                }
                catch (System.Exception exception)
                {
                    result.Skipped++;
                    document.Editor.WriteMessage(
                        "\nA platform source was skipped safely. " + exception.Message);
                }
            }
            return result;
        }

        internal static int DrapeSelection(
            Document document,
            IEnumerable<ObjectId> featureLineIds,
            string surfaceName,
            ObjectId surfaceId,
            bool intermediate)
        {
            if (document == null || featureLineIds == null || surfaceId.IsNull ||
                string.IsNullOrWhiteSpace(surfaceName)) return 0;
            int linked = 0;
            foreach (ObjectId childId in featureLineIds.Where(id => !id.IsNull).Distinct())
            {
                try
                {
                    ValidateSource(document, childId);
                    Relation relation;
                    if (!TryReadRelation(document.Database, childId, out relation))
                    {
                        relation = new Relation
                        {
                            SourceHandle = childId.Handle.ToString(),
                            HorizontalOffset = 0.0,
                            VerticalOffset = 0.0,
                            Sequence = 0
                        };
                    }
                    string error;
                    if (!August21SurfaceSafety.TryApplyFeatureLineElevations(
                            document,
                            childId,
                            surfaceName,
                            intermediate,
                            out error))
                    {
                        document.Editor.WriteMessage(
                            "\nA selected platform step was not draped. " + error);
                        continue;
                    }
                    WriteDrape(document, childId, new DrapeRelation
                    {
                        SourceHandle = relation.SourceHandle,
                        SurfaceHandle = surfaceId.Handle.ToString(),
                        VerticalOffset = relation.VerticalOffset,
                        Sequence = relation.Sequence,
                        Intermediate = intermediate
                    });
                    VerifyFeatureLine(document, childId);
                    linked++;
                }
                catch (System.Exception exception)
                {
                    document.Editor.WriteMessage(
                        "\nA selected platform step was skipped safely. " + exception.Message);
                }
            }
            return linked;
        }

        internal static int RefreshPlatformDrapes(Document document)
        {
            if (document == null) return 0;
            List<DrapeSnapshot> snapshots = ReadDrapes(document.Database);
            if (snapshots.Count == 0) return 0;

            int refreshed = 0;
            var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (DrapeSnapshot snapshot in snapshots)
            {
                try
                {
                    ObjectId childId = Resolve(document.Database, snapshot.ChildHandle);
                    ObjectId sourceId = Resolve(document.Database, snapshot.Link.SourceHandle);
                    ObjectId surfaceId = Resolve(document.Database, snapshot.Link.SurfaceHandle);
                    string surfaceName = ReadSurfaceName(document.Database, surfaceId);
                    if (childId.IsNull || surfaceId.IsNull || string.IsNullOrWhiteSpace(surfaceName))
                        continue;

                    string error;
                    if (!August21SurfaceSafety.TryApplyFeatureLineElevations(
                            document,
                            childId,
                            surfaceName,
                            snapshot.Link.Intermediate,
                            out error))
                    {
                        document.Editor.WriteMessage(
                            "\nA linked platform drape was skipped safely. " + error);
                        continue;
                    }
                    refreshed++;

                    if (!sourceId.IsNull && sourceId != childId)
                    {
                        if (UpdateSourceFromDrapedChild(
                                document,
                                sourceId,
                                childId,
                                snapshot.Link.VerticalOffset,
                                out error))
                        {
                            sources.Add(snapshot.Link.SourceHandle);
                            refreshed++;
                        }
                        else
                        {
                            document.Editor.WriteMessage(
                                "\nA platform source was kept unchanged after drape refresh. " + error);
                        }
                    }
                }
                catch (System.Exception exception)
                {
                    document.Editor.WriteMessage(
                        "\nA linked platform drape was isolated after an error. " + exception.Message);
                }
            }

            foreach (string sourceHandle in sources)
            {
                ObjectId sourceId = Resolve(document.Database, sourceHandle);
                if (sourceId.IsNull) continue;
                refreshed += RebuildRelativeSource(document, sourceId);
            }

            foreach (DrapeSnapshot snapshot in snapshots)
            {
                try
                {
                    ObjectId targetId = snapshot.Link.Sequence > 0
                        ? FindChild(document.Database, snapshot.Link.SourceHandle, snapshot.Link.Sequence)
                        : Resolve(document.Database, snapshot.ChildHandle);
                    ObjectId surfaceId = Resolve(document.Database, snapshot.Link.SurfaceHandle);
                    string surfaceName = ReadSurfaceName(document.Database, surfaceId);
                    if (targetId.IsNull || string.IsNullOrWhiteSpace(surfaceName)) continue;
                    string error;
                    if (!August21SurfaceSafety.TryApplyFeatureLineElevations(
                            document,
                            targetId,
                            surfaceName,
                            snapshot.Link.Intermediate,
                            out error))
                        continue;
                    WriteDrape(document, targetId, snapshot.Link);
                    refreshed++;
                }
                catch (System.Exception exception)
                {
                    document.Editor.WriteMessage(
                        "\nA rebuilt platform drape was skipped safely. " + exception.Message);
                }
            }
            return refreshed;
        }

        internal static void CreateJoinedFeatureLine(
            Document document,
            IList<ObjectId> selectedIds,
            double gapTolerance,
            string requestedName,
            out int pieceCount,
            out int vertexCount,
            out double largestGap,
            out string outputName)
        {
            pieceCount = 0;
            vertexCount = 0;
            largestGap = 0.0;
            outputName = string.Empty;
            if (document == null || selectedIds == null || selectedIds.Count < 2)
                throw new InvalidOperationException("At least two feature-line pieces are required.");

            var pieces = new List<Piece>();
            ObjectId layerId = ObjectId.Null;
            ObjectId siteId = ObjectId.Null;
            string styleName = string.Empty;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in selectedIds.Where(value => !value.IsNull).Distinct())
                {
                    CivilFeatureLine featureLine = OpenFeatureLine(transaction, id, OpenMode.ForRead);
                    EnsureEditable(featureLine, transaction);
                    if (featureLine.Closed)
                        throw new InvalidOperationException(
                            "Select open stepped feature-line pieces. Closed feature lines are not supported.");
                    Point3dCollection points = featureLine.GetPoints(FeatureLinePointType.AllPoints);
                    if (points == null || points.Count < 2 || points.Cast<Point3d>().Any(point => !Finite(point)))
                        throw new InvalidOperationException("Every selected feature-line piece requires finite points.");
                    pieces.Add(new Piece(id, points.Cast<Point3d>().ToList()));
                    if (layerId.IsNull)
                    {
                        layerId = featureLine.LayerId;
                        siteId = featureLine.SiteId;
                        styleName = featureLine.StyleName;
                    }
                }
            }
            if (pieces.Count < 2)
                throw new InvalidOperationException("At least two local feature-line pieces are required.");

            List<Piece> ordered = OrderPieces(pieces, gapTolerance, out largestGap);
            List<Point3d> joined = FlattenPieces(ordered);
            if (joined.Count < 2 || joined.Any(point => !Finite(point)))
                throw new InvalidOperationException("The selected pieces did not produce a valid joined path.");

            outputName = UniqueName(
                string.IsNullOrWhiteSpace(requestedName) ? "CE-STEPPED-FL" : requestedName.Trim(),
                ReadFeatureLineNames(document.Database));

            ObjectId temporaryId = ObjectId.Null;
            ObjectId featureLineId = ObjectId.Null;
            try
            {
                temporaryId = CommitPolyline3d(document, joined, false, layerId);
                string error;
                if (!TryCreateFromCommittedTemporary(
                        document,
                        temporaryId,
                        outputName,
                        siteId,
                        layerId,
                        styleName,
                        out featureLineId,
                        out error))
                {
                    throw new InvalidOperationException(error);
                }
                VerifyFeatureLine(document, featureLineId);
                pieceCount = ordered.Count;
                vertexCount = joined.Count;
            }
            catch
            {
                Cleanup(document, featureLineId);
                throw;
            }
            finally
            {
                Cleanup(document, temporaryId);
            }
        }

        private static bool TryCreateOffsetCandidate(
            Document document,
            ObjectId sourceId,
            double horizontalOffset,
            double verticalOffset,
            string name,
            ObjectId layerOverride,
            string styleOverride,
            ObjectId siteOverride,
            out ObjectId childId,
            out string error)
        {
            childId = ObjectId.Null;
            error = string.Empty;
            ObjectId temporaryId = ObjectId.Null;
            try
            {
                ObjectId layerId;
                ObjectId siteId;
                string styleName;
                using (Transaction read = document.Database.TransactionManager.StartTransaction())
                {
                    CivilFeatureLine source = OpenFeatureLine(read, sourceId, OpenMode.ForRead);
                    EnsureEditable(source, read);
                    layerId = layerOverride.IsNull ? source.LayerId : layerOverride;
                    siteId = siteOverride.IsNull ? source.SiteId : siteOverride;
                    styleName = styleOverride == null ? source.StyleName : styleOverride;
                }

                using (Polyline plan = ReadPlan(document, sourceId))
                {
                    DBObjectCollection offsets = null;
                    Curve curve = null;
                    try
                    {
                        offsets = plan.GetOffsetCurves(horizontalOffset);
                        if (offsets == null || offsets.Count != 1)
                        {
                            error = "The offset generated multiple or no curves; simplify self-intersections or reduce the offset.";
                            return false;
                        }
                        curve = offsets[0] as Curve;
                        if (!SafeCurve(curve, out error)) return false;
                        temporaryId = CommitCurve(document, curve, layerId);
                        curve = null;
                    }
                    finally
                    {
                        if (offsets != null)
                        {
                            foreach (DBObject value in offsets)
                            {
                                if (value != null && value.ObjectId.IsNull)
                                {
                                    try { value.Dispose(); } catch { }
                                }
                            }
                        }
                    }
                }

                if (!TryCreateFromCommittedTemporary(
                        document,
                        temporaryId,
                        name,
                        siteId,
                        layerId,
                        styleName,
                        out childId,
                        out error))
                    return false;

                if (!ApplyRelativeElevations(document, sourceId, childId, verticalOffset, out error))
                    return false;
                VerifyFeatureLine(document, childId);
                return true;
            }
            catch (System.Exception exception)
            {
                error = exception.Message;
                return false;
            }
            finally
            {
                Cleanup(document, temporaryId);
                if (!string.IsNullOrWhiteSpace(error) && !childId.IsNull)
                {
                    Cleanup(document, childId);
                    childId = ObjectId.Null;
                }
            }
        }

        private static ObjectId CommitCurve(Document document, Curve curve, ObjectId layerId)
        {
            if (curve == null) throw new InvalidOperationException("Temporary offset curve is unavailable.");
            ObjectId id;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = transaction.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(document.Database),
                    OpenMode.ForWrite,
                    false) as BlockTableRecord;
                if (space == null) throw new InvalidOperationException("Model space is unavailable.");
                curve.SetDatabaseDefaults(document.Database);
                if (!layerId.IsNull) curve.LayerId = layerId;
                id = space.AppendEntity(curve);
                transaction.AddNewlyCreatedDBObject(curve, true);
                transaction.Commit();
            }
            VerifyCommittedCurve(document, id);
            return id;
        }

        private static ObjectId CommitPolyline3d(
            Document document,
            IList<Point3d> points,
            bool closed,
            ObjectId layerId)
        {
            var collection = new Point3dCollection();
            foreach (Point3d point in points) collection.Add(point);
            ObjectId id;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = transaction.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(document.Database),
                    OpenMode.ForWrite,
                    false) as BlockTableRecord;
                if (space == null) throw new InvalidOperationException("Model space is unavailable.");
                var temporary = new Polyline3d(Poly3dType.SimplePoly, collection, closed);
                temporary.SetDatabaseDefaults(document.Database);
                if (!layerId.IsNull) temporary.LayerId = layerId;
                id = space.AppendEntity(temporary);
                transaction.AddNewlyCreatedDBObject(temporary, true);
                transaction.Commit();
            }
            VerifyCommittedCurve(document, id);
            return id;
        }

        private static bool TryCreateFromCommittedTemporary(
            Document document,
            ObjectId temporaryId,
            string name,
            ObjectId siteId,
            ObjectId layerId,
            string styleName,
            out ObjectId featureLineId,
            out string error)
        {
            featureLineId = ObjectId.Null;
            error = string.Empty;
            try
            {
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    Curve temporary = transaction.GetObject(
                        temporaryId,
                        OpenMode.ForRead,
                        false) as Curve;
                    if (!SafeCurve(temporary, out error)) return false;

                    featureLineId = siteId.IsNull
                        ? CivilFeatureLine.Create(name, temporaryId)
                        : CivilFeatureLine.Create(name, temporaryId, siteId);
                    if (featureLineId.IsNull || !featureLineId.IsValid || featureLineId.IsErased)
                    {
                        error = "Civil 3D returned no valid feature-line ObjectId.";
                        return false;
                    }
                    CivilFeatureLine child = transaction.GetObject(
                        featureLineId,
                        OpenMode.ForWrite,
                        false) as CivilFeatureLine;
                    if (child == null)
                    {
                        error = "Civil 3D did not return an ordinary feature line.";
                        return false;
                    }
                    if (!layerId.IsNull) child.LayerId = layerId;
                    if (!string.IsNullOrWhiteSpace(styleName)) child.StyleName = styleName;
                    Point3dCollection points = child.GetPoints(FeatureLinePointType.AllPoints);
                    if (points == null || points.Count < 2 || points.Cast<Point3d>().Any(point => !Finite(point)))
                    {
                        error = "The created feature line failed point verification.";
                        return false;
                    }
                    transaction.Commit();
                }
                return true;
            }
            catch (System.Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }

        private static bool ApplyRelativeElevations(
            Document document,
            ObjectId sourceId,
            ObjectId childId,
            double verticalOffset,
            out string error)
        {
            error = string.Empty;
            try
            {
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    CivilFeatureLine source = OpenFeatureLine(transaction, sourceId, OpenMode.ForRead);
                    CivilFeatureLine child = OpenFeatureLine(transaction, childId, OpenMode.ForWrite);
                    EnsureEditable(source, transaction);
                    if (child == null || child.IsReferenceObject)
                        throw new InvalidOperationException("The candidate feature line is unavailable or referenced.");

                    Point3dCollection points = child.GetPoints(FeatureLinePointType.AllPoints);
                    if (points == null || points.Count < 2)
                        throw new InvalidOperationException("The candidate feature line has too few points.");
                    for (int index = 0; index < points.Count; index++)
                    {
                        Point3d point = points[index];
                        Point3d sourcePoint = source.GetClosestPointTo(
                            new Point3d(point.X, point.Y, 0.0),
                            Vector3d.ZAxis,
                            false);
                        double elevation = sourcePoint.Z + verticalOffset;
                        if (!Finite(elevation))
                            throw new InvalidOperationException("A linked elevation calculation was non-finite.");
                        SetAbsoluteElevation(child, point, index, elevation);
                    }

                    Point3dCollection elevationPoints = source.GetPoints(FeatureLinePointType.ElevationPoint);
                    if (elevationPoints != null)
                    {
                        foreach (Point3d sourcePoint in elevationPoints)
                        {
                            try
                            {
                                Point3d target = child.GetClosestPointTo(
                                    new Point3d(sourcePoint.X, sourcePoint.Y, 0.0),
                                    Vector3d.ZAxis,
                                    false);
                                child.InsertElevationPoint(target);
                                Point3dCollection updated = child.GetPoints(FeatureLinePointType.AllPoints);
                                int nearest = ClosestIndex(updated, target);
                                if (nearest >= 0)
                                    SetAbsoluteElevation(
                                        child,
                                        updated[nearest],
                                        nearest,
                                        sourcePoint.Z + verticalOffset);
                            }
                            catch (ArgumentException) { }
                            catch (Autodesk.AutoCAD.Runtime.Exception) { }
                        }
                    }
                    transaction.Commit();
                }
                return true;
            }
            catch (System.Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }

        private static bool CommitCandidateSwap(
            Document document,
            ChildSnapshot old,
            ObjectId candidateId,
            out string error)
        {
            error = string.Empty;
            try
            {
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    CivilFeatureLine oldChild = OpenFeatureLine(transaction, old.ObjectId, OpenMode.ForWrite);
                    CivilFeatureLine candidate = OpenFeatureLine(transaction, candidateId, OpenMode.ForWrite);
                    if (oldChild == null || oldChild.IsReferenceObject)
                        throw new InvalidOperationException("The existing linked child became unavailable.");
                    if (candidate == null || candidate.IsReferenceObject)
                        throw new InvalidOperationException("The verified replacement became unavailable.");
                    if (IsLayerLocked(transaction, oldChild.LayerId))
                        throw new InvalidOperationException("The existing linked child is on a locked layer.");

                    string desiredName = string.IsNullOrWhiteSpace(old.Name)
                        ? "CE-LINKED-FL"
                        : old.Name;
                    SetNameOrThrow(oldChild, "CE_OLD_FLREL_" + Guid.NewGuid().ToString("N"));
                    SetNameOrThrow(candidate, desiredName);
                    oldChild.Erase();
                    transaction.Commit();
                }
                return true;
            }
            catch (System.Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }

        private static bool UpdateSourceFromDrapedChild(
            Document document,
            ObjectId sourceId,
            ObjectId childId,
            double verticalOffset,
            out string error)
        {
            error = string.Empty;
            try
            {
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    CivilFeatureLine child = OpenFeatureLine(transaction, childId, OpenMode.ForRead);
                    CivilFeatureLine source = OpenFeatureLine(transaction, sourceId, OpenMode.ForWrite);
                    EnsureEditable(source, transaction);
                    if (child == null || child.IsReferenceObject)
                        throw new InvalidOperationException("The draped child is unavailable or referenced.");
                    Point3dCollection sourcePoints = source.GetPoints(FeatureLinePointType.AllPoints);
                    if (sourcePoints == null || sourcePoints.Count < 2)
                        throw new InvalidOperationException("The platform source has too few points.");
                    for (int index = 0; index < sourcePoints.Count; index++)
                    {
                        Point3d point = sourcePoints[index];
                        Point3d nearest = child.GetClosestPointTo(
                            new Point3d(point.X, point.Y, 0.0),
                            Vector3d.ZAxis,
                            false);
                        double elevation = nearest.Z - verticalOffset;
                        if (!Finite(elevation))
                            throw new InvalidOperationException("A platform source elevation calculation was non-finite.");
                        SetAbsoluteElevation(source, point, index, elevation);
                    }
                    transaction.Commit();
                }
                return true;
            }
            catch (System.Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }

        private static void WriteRelation(Document document, ObjectId childId, Relation relation)
        {
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                CivilFeatureLine child = OpenFeatureLine(transaction, childId, OpenMode.ForWrite);
                if (child == null) throw new InvalidOperationException("The linked child is unavailable.");
                WriteRecord(child, transaction, RelationKey, new ResultBuffer(
                    new TypedValue((int)DxfCode.Text, relation.SourceHandle ?? string.Empty),
                    new TypedValue((int)DxfCode.Real, relation.HorizontalOffset),
                    new TypedValue((int)DxfCode.Real, relation.VerticalOffset),
                    new TypedValue((int)DxfCode.Int32, relation.Sequence)));
                transaction.Commit();
            }
        }

        private static void WriteDrape(Document document, ObjectId childId, DrapeRelation relation)
        {
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                CivilFeatureLine child = OpenFeatureLine(transaction, childId, OpenMode.ForWrite);
                if (child == null) throw new InvalidOperationException("The draped child is unavailable.");
                WriteRecord(child, transaction, DrapeKey, new ResultBuffer(
                    new TypedValue((int)DxfCode.Text, relation.SourceHandle ?? string.Empty),
                    new TypedValue((int)DxfCode.Text, relation.SurfaceHandle ?? string.Empty),
                    new TypedValue((int)DxfCode.Real, relation.VerticalOffset),
                    new TypedValue((int)DxfCode.Int32, relation.Sequence),
                    new TypedValue((int)DxfCode.Int16, relation.Intermediate ? 1 : 0)));
                transaction.Commit();
            }
        }

        private static List<ChildSnapshot> ReadChildren(Database database, ObjectId sourceId)
        {
            var result = new List<ChildSnapshot>();
            string sourceHandle = sourceId.Handle.ToString();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = transaction.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(database),
                    OpenMode.ForRead,
                    false) as BlockTableRecord;
                if (space == null) return result;
                foreach (ObjectId id in space)
                {
                    CivilFeatureLine child = OpenFeatureLine(transaction, id, OpenMode.ForRead);
                    Relation relation;
                    if (child == null || !TryReadRelation(child, transaction, out relation) ||
                        !string.Equals(relation.SourceHandle, sourceHandle, StringComparison.OrdinalIgnoreCase))
                        continue;
                    result.Add(new ChildSnapshot
                    {
                        ObjectId = id,
                        Name = child.Name,
                        LayerId = child.LayerId,
                        StyleName = child.StyleName,
                        SiteId = child.SiteId,
                        Link = relation
                    });
                }
            }
            return result;
        }

        private static bool TryReadRelation(Database database, ObjectId childId, out Relation relation)
        {
            relation = null;
            try
            {
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    CivilFeatureLine child = OpenFeatureLine(transaction, childId, OpenMode.ForRead);
                    return TryReadRelation(child, transaction, out relation);
                }
            }
            catch { return false; }
        }

        private static bool TryReadRelation(
            CivilFeatureLine child,
            Transaction transaction,
            out Relation relation)
        {
            relation = null;
            TypedValue[] values = ReadRecord(child, transaction, RelationKey);
            if (values == null || values.Length < 4) return false;
            try
            {
                relation = new Relation
                {
                    SourceHandle = Convert.ToString(values[0].Value, CultureInfo.InvariantCulture),
                    HorizontalOffset = Convert.ToDouble(values[1].Value, CultureInfo.InvariantCulture),
                    VerticalOffset = Convert.ToDouble(values[2].Value, CultureInfo.InvariantCulture),
                    Sequence = Convert.ToInt32(values[3].Value, CultureInfo.InvariantCulture)
                };
                return !string.IsNullOrWhiteSpace(relation.SourceHandle) &&
                    Finite(relation.HorizontalOffset) && Finite(relation.VerticalOffset);
            }
            catch { relation = null; return false; }
        }

        private static List<DrapeSnapshot> ReadDrapes(Database database)
        {
            var result = new List<DrapeSnapshot>();
            try
            {
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    BlockTableRecord space = transaction.GetObject(
                        SymbolUtilityServices.GetBlockModelSpaceId(database),
                        OpenMode.ForRead,
                        false) as BlockTableRecord;
                    if (space == null) return result;
                    foreach (ObjectId id in space)
                    {
                        CivilFeatureLine child = OpenFeatureLine(transaction, id, OpenMode.ForRead);
                        if (child == null) continue;
                        TypedValue[] values = ReadRecord(child, transaction, DrapeKey);
                        if (values == null || values.Length < 5) continue;
                        try
                        {
                            var link = new DrapeRelation
                            {
                                SourceHandle = Convert.ToString(values[0].Value, CultureInfo.InvariantCulture),
                                SurfaceHandle = Convert.ToString(values[1].Value, CultureInfo.InvariantCulture),
                                VerticalOffset = Convert.ToDouble(values[2].Value, CultureInfo.InvariantCulture),
                                Sequence = Convert.ToInt32(values[3].Value, CultureInfo.InvariantCulture),
                                Intermediate = Convert.ToInt32(values[4].Value, CultureInfo.InvariantCulture) != 0
                            };
                            if (!string.IsNullOrWhiteSpace(link.SourceHandle) &&
                                !string.IsNullOrWhiteSpace(link.SurfaceHandle) &&
                                Finite(link.VerticalOffset))
                                result.Add(new DrapeSnapshot(child.Handle.ToString(), link));
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return result;
        }

        private static ObjectId FindChild(Database database, string sourceHandle, int sequence)
        {
            try
            {
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    BlockTableRecord space = transaction.GetObject(
                        SymbolUtilityServices.GetBlockModelSpaceId(database),
                        OpenMode.ForRead,
                        false) as BlockTableRecord;
                    if (space == null) return ObjectId.Null;
                    foreach (ObjectId id in space)
                    {
                        CivilFeatureLine child = OpenFeatureLine(transaction, id, OpenMode.ForRead);
                        Relation relation;
                        if (child != null && TryReadRelation(child, transaction, out relation) &&
                            relation.Sequence == sequence &&
                            string.Equals(relation.SourceHandle, sourceHandle, StringComparison.OrdinalIgnoreCase))
                            return id;
                    }
                }
            }
            catch { }
            return ObjectId.Null;
        }

        private static string ReadSurfaceName(Database database, ObjectId surfaceId)
        {
            if (surfaceId.IsNull || !surfaceId.IsValid || surfaceId.IsErased) return string.Empty;
            try
            {
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    CivilSurface surface = transaction.GetObject(surfaceId, OpenMode.ForRead, false) as CivilSurface;
                    return surface == null ? string.Empty : surface.Name;
                }
            }
            catch { return string.Empty; }
        }

        private static Polyline ReadPlan(Document document, ObjectId sourceId)
        {
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                CivilFeatureLine source = OpenFeatureLine(transaction, sourceId, OpenMode.ForRead);
                EnsureEditable(source, transaction);
                Point3dCollection collection = source.GetPoints(FeatureLinePointType.PIPoint);
                if (collection == null || collection.Count < 2)
                    throw new InvalidOperationException("At least two PI points are required.");
                List<Point3d> points = collection.Cast<Point3d>().ToList();
                if (points.Any(point => !Finite(point)))
                    throw new InvalidOperationException("The feature line contains a non-finite PI point.");
                if (source.Closed && points.Count > 2 && PlanDistance(points[0], points[points.Count - 1]) <= Tolerance)
                    points.RemoveAt(points.Count - 1);
                if (points.Count < 2)
                    throw new InvalidOperationException("The feature line collapses to fewer than two PI points.");
                var plan = new Polyline(points.Count)
                {
                    Closed = source.Closed,
                    Elevation = 0.0,
                    Normal = Vector3d.ZAxis
                };
                int segments = source.Closed ? points.Count : points.Count - 1;
                for (int index = 0; index < points.Count; index++)
                {
                    double bulge = index < segments ? source.GetBulge(index) : 0.0;
                    if (!Finite(bulge))
                    {
                        plan.Dispose();
                        throw new InvalidOperationException("The feature line contains a non-finite bulge.");
                    }
                    plan.AddVertexAt(
                        index,
                        new Point2d(points[index].X, points[index].Y),
                        bulge,
                        0.0,
                        0.0);
                }
                string error;
                if (!SafeCurve(plan, out error))
                {
                    plan.Dispose();
                    throw new InvalidOperationException(error);
                }
                return plan;
            }
        }

        private static void ValidateSource(Document document, ObjectId sourceId)
        {
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                CivilFeatureLine source = OpenFeatureLine(transaction, sourceId, OpenMode.ForRead);
                EnsureEditable(source, transaction);
                Point3dCollection points = source.GetPoints(FeatureLinePointType.AllPoints);
                if (points == null || points.Count < 2 || points.Cast<Point3d>().Any(point => !Finite(point)))
                    throw new InvalidOperationException("The feature line failed finite-point validation.");
                bool distinct = false;
                Point3d first = points[0];
                for (int index = 1; index < points.Count; index++)
                {
                    if (PlanDistance(first, points[index]) > Tolerance)
                    {
                        distinct = true;
                        break;
                    }
                }
                if (!distinct) throw new InvalidOperationException("The feature line collapses to one plan point.");
            }
        }

        private static CivilFeatureLine OpenFeatureLine(
            Transaction transaction,
            ObjectId id,
            OpenMode mode)
        {
            if (transaction == null || id.IsNull || !id.IsValid || id.IsErased) return null;
            try { return transaction.GetObject(id, mode, false) as CivilFeatureLine; }
            catch { return null; }
        }

        private static void EnsureEditable(CivilFeatureLine featureLine, Transaction transaction)
        {
            if (featureLine == null || featureLine.IsErased)
                throw new InvalidOperationException("The feature line is unavailable.");
            if (featureLine.IsReferenceObject)
                throw new InvalidOperationException("Referenced feature lines are read-only.");
            if (IsLayerLocked(transaction, featureLine.LayerId))
                throw new InvalidOperationException("The feature line is on a locked layer.");
        }

        private static bool IsLayerLocked(Transaction transaction, ObjectId layerId)
        {
            if (layerId.IsNull) return false;
            try
            {
                LayerTableRecord layer = transaction.GetObject(layerId, OpenMode.ForRead, false) as LayerTableRecord;
                return layer != null && layer.IsLocked;
            }
            catch { return true; }
        }

        private static bool SafeCurve(Curve curve, out string error)
        {
            error = string.Empty;
            if (curve == null)
            {
                error = "The temporary curve is unavailable.";
                return false;
            }
            try
            {
                Point3d start = curve.StartPoint;
                Point3d end = curve.EndPoint;
                if (!Finite(start) || !Finite(end))
                {
                    error = "The temporary curve contains non-finite endpoints.";
                    return false;
                }
                double length = Math.Abs(
                    curve.GetDistanceAtParameter(curve.EndParam) -
                    curve.GetDistanceAtParameter(curve.StartParam));
                if (!Finite(length) || length <= Tolerance)
                {
                    error = "The temporary curve has zero or invalid length.";
                    return false;
                }
                return true;
            }
            catch (System.Exception exception)
            {
                error = "The temporary curve failed geometry validation. " + exception.Message;
                return false;
            }
        }

        private static void VerifyCommittedCurve(Document document, ObjectId id)
        {
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                Curve curve = transaction.GetObject(id, OpenMode.ForRead, false) as Curve;
                string error;
                if (!SafeCurve(curve, out error))
                    throw new InvalidOperationException(error);
            }
        }

        private static void VerifyFeatureLine(Document document, ObjectId id)
        {
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                CivilFeatureLine featureLine = OpenFeatureLine(transaction, id, OpenMode.ForRead);
                if (featureLine == null || featureLine.IsReferenceObject)
                    throw new InvalidOperationException("The created feature line failed verification.");
                Point3dCollection points = featureLine.GetPoints(FeatureLinePointType.AllPoints);
                if (points == null || points.Count < 2 || points.Cast<Point3d>().Any(point => !Finite(point)))
                    throw new InvalidOperationException("The created feature line has invalid point data.");
            }
        }

        private static HashSet<string> ReadFeatureLineNames(Database database)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    BlockTableRecord space = transaction.GetObject(
                        SymbolUtilityServices.GetBlockModelSpaceId(database),
                        OpenMode.ForRead,
                        false) as BlockTableRecord;
                    if (space == null) return names;
                    foreach (ObjectId id in space)
                    {
                        CivilFeatureLine featureLine = OpenFeatureLine(transaction, id, OpenMode.ForRead);
                        if (featureLine != null && !string.IsNullOrWhiteSpace(featureLine.Name))
                            names.Add(featureLine.Name);
                    }
                }
            }
            catch { }
            return names;
        }

        private static string UniqueTemporaryName(Database database, string prefix)
        {
            var names = ReadFeatureLineNames(database);
            string candidate;
            do
            {
                candidate = prefix + "_" + Guid.NewGuid().ToString("N");
            }
            while (names.Contains(candidate));
            return candidate;
        }

        private static string UniqueName(string requested, ISet<string> names)
        {
            string baseName = string.IsNullOrWhiteSpace(requested) ? "CE-FEATURE-LINE" : requested.Trim();
            string candidate = baseName;
            int suffix = 2;
            while (!names.Add(candidate))
            {
                candidate = baseName + " (" + suffix.ToString(CultureInfo.InvariantCulture) + ")";
                suffix++;
            }
            return candidate;
        }

        private static void SetNameOrThrow(CivilFeatureLine featureLine, string name)
        {
            PropertyInfo property = featureLine.GetType().GetProperty(
                "Name",
                BindingFlags.Public | BindingFlags.Instance);
            if (property == null || !property.CanWrite)
                throw new InvalidOperationException("This Civil 3D host does not expose a writable feature-line Name property.");
            property.SetValue(featureLine, name, null);
        }

        private static void SetAbsoluteElevation(
            CivilFeatureLine featureLine,
            Point3d point,
            int index,
            double elevation)
        {
            if (!Finite(elevation))
                throw new InvalidOperationException("A feature-line elevation is non-finite.");
            try
            {
                if (featureLine.IsElevationRelativeToSurface(point))
                {
                    featureLine.SetPointRelativeElevation(point, false, elevation);
                    return;
                }
            }
            catch { }
            featureLine.SetPointElevation(index, elevation);
        }

        private static int ClosestIndex(Point3dCollection points, Point3d target)
        {
            if (points == null || points.Count == 0) return -1;
            int best = 0;
            double distance = points[0].DistanceTo(target);
            for (int index = 1; index < points.Count; index++)
            {
                double current = points[index].DistanceTo(target);
                if (current < distance)
                {
                    best = index;
                    distance = current;
                }
            }
            return best;
        }

        private static double OutwardSign(Polyline plan, double distance)
        {
            double positive = OffsetArea(plan, distance);
            double negative = OffsetArea(plan, -distance);
            if (double.IsNaN(positive)) return double.IsNaN(negative) ? 1.0 : -1.0;
            if (double.IsNaN(negative)) return 1.0;
            return positive >= negative ? 1.0 : -1.0;
        }

        private static double OffsetArea(Polyline plan, double offset)
        {
            DBObjectCollection values = null;
            try
            {
                values = plan.GetOffsetCurves(offset);
                if (values == null || values.Count != 1) return double.NaN;
                Polyline polyline = values[0] as Polyline;
                return polyline == null || !polyline.Closed ? double.NaN : Math.Abs(polyline.Area);
            }
            catch { return double.NaN; }
            finally
            {
                if (values != null)
                    foreach (DBObject value in values)
                        try { if (value != null) value.Dispose(); } catch { }
            }
        }

        private static List<Piece> OrderPieces(
            IList<Piece> source,
            double gapTolerance,
            out double largestGap)
        {
            var remaining = source.Skip(1).ToList();
            var ordered = new List<Piece> { source[0] };
            largestGap = 0.0;
            while (remaining.Count > 0)
            {
                Point3d head = ordered[0].Points[0];
                Point3d tail = ordered[ordered.Count - 1].Points[ordered[ordered.Count - 1].Points.Count - 1];
                Attachment best = null;
                foreach (Piece candidate in remaining)
                {
                    Point3d start = candidate.Points[0];
                    Point3d end = candidate.Points[candidate.Points.Count - 1];
                    Consider(ref best, candidate, false, false, PlanDistance(tail, start));
                    Consider(ref best, candidate, true, false, PlanDistance(tail, end));
                    Consider(ref best, candidate, false, true, PlanDistance(head, end));
                    Consider(ref best, candidate, true, true, PlanDistance(head, start));
                }
                if (best == null || best.Distance > gapTolerance)
                {
                    double distance = best == null ? double.PositiveInfinity : best.Distance;
                    throw new InvalidOperationException(
                        "The nearest remaining endpoint gap is " +
                        (double.IsInfinity(distance) ? "unavailable" : distance.ToString("0.###", CultureInfo.CurrentCulture)) +
                        ", which exceeds the maximum gap tolerance of " +
                        gapTolerance.ToString("0.###", CultureInfo.CurrentCulture) + ".");
                }
                Piece piece = best.Reverse ? best.Piece.Reversed() : best.Piece;
                if (best.Prepend) ordered.Insert(0, piece); else ordered.Add(piece);
                remaining.Remove(best.Piece);
                largestGap = Math.Max(largestGap, best.Distance);
            }
            return ordered;
        }

        private static void Consider(
            ref Attachment best,
            Piece piece,
            bool reverse,
            bool prepend,
            double distance)
        {
            if (best == null || distance < best.Distance)
                best = new Attachment(piece, reverse, prepend, distance);
        }

        private static List<Point3d> FlattenPieces(IList<Piece> pieces)
        {
            var points = new List<Point3d>();
            foreach (Piece piece in pieces)
            {
                for (int index = 0; index < piece.Points.Count; index++)
                {
                    Point3d point = piece.Points[index];
                    if (points.Count > 0 && index == 0 &&
                        PlanDistance(points[points.Count - 1], point) <= Tolerance)
                        continue;
                    points.Add(point);
                }
            }
            return points;
        }

        private static double PlanDistance(Point3d first, Point3d second)
        {
            double dx = first.X - second.X;
            double dy = first.Y - second.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static bool Finite(Point3d point)
        {
            return Finite(point.X) && Finite(point.Y) && Finite(point.Z);
        }

        private static bool Finite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static ObjectId Resolve(Database database, string handle)
        {
            long value;
            if (database == null || string.IsNullOrWhiteSpace(handle) ||
                !long.TryParse(handle, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value))
                return ObjectId.Null;
            try { return database.GetObjectId(false, new Handle(value), 0); }
            catch { return ObjectId.Null; }
        }

        private static void WriteRecord(
            Entity entity,
            Transaction transaction,
            string key,
            ResultBuffer data)
        {
            if (entity.ExtensionDictionary.IsNull) entity.CreateExtensionDictionary();
            DBDictionary dictionary = transaction.GetObject(
                entity.ExtensionDictionary,
                OpenMode.ForWrite,
                false) as DBDictionary;
            if (dictionary == null) throw new InvalidOperationException("The CE link dictionary is unavailable.");
            Xrecord record;
            if (dictionary.Contains(key))
            {
                record = transaction.GetObject(dictionary.GetAt(key), OpenMode.ForWrite, false) as Xrecord;
            }
            else
            {
                record = new Xrecord();
                dictionary.SetAt(key, record);
                transaction.AddNewlyCreatedDBObject(record, true);
            }
            if (record == null) throw new InvalidOperationException("The CE link record is unavailable.");
            record.Data = data;
        }

        private static TypedValue[] ReadRecord(Entity entity, Transaction transaction, string key)
        {
            if (entity == null || entity.ExtensionDictionary.IsNull) return null;
            try
            {
                DBDictionary dictionary = transaction.GetObject(
                    entity.ExtensionDictionary,
                    OpenMode.ForRead,
                    false) as DBDictionary;
                if (dictionary == null || !dictionary.Contains(key)) return null;
                Xrecord record = transaction.GetObject(
                    dictionary.GetAt(key),
                    OpenMode.ForRead,
                    false) as Xrecord;
                return record == null || record.Data == null ? null : record.Data.AsArray();
            }
            catch { return null; }
        }

        private static void Cleanup(Document document, ObjectId id)
        {
            if (document == null || id.IsNull || !id.IsValid || id.IsErased) return;
            try
            {
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    Entity entity = transaction.GetObject(id, OpenMode.ForWrite, false) as Entity;
                    if (entity != null && !entity.IsErased) entity.Erase();
                    transaction.Commit();
                }
            }
            catch { }
        }

        private sealed class Relation
        {
            internal string SourceHandle;
            internal double HorizontalOffset;
            internal double VerticalOffset;
            internal int Sequence;
        }

        private sealed class ChildSnapshot
        {
            internal ObjectId ObjectId;
            internal string Name;
            internal ObjectId LayerId;
            internal string StyleName;
            internal ObjectId SiteId;
            internal Relation Link;
        }

        private sealed class DrapeRelation
        {
            internal string SourceHandle;
            internal string SurfaceHandle;
            internal double VerticalOffset;
            internal int Sequence;
            internal bool Intermediate;
        }

        private sealed class DrapeSnapshot
        {
            internal DrapeSnapshot(string childHandle, DrapeRelation link)
            {
                ChildHandle = childHandle;
                Link = link;
            }
            internal string ChildHandle;
            internal DrapeRelation Link;
        }

        private sealed class Piece
        {
            internal Piece(ObjectId objectId, List<Point3d> points)
            {
                ObjectId = objectId;
                Points = points;
            }
            internal ObjectId ObjectId;
            internal List<Point3d> Points;
            internal Piece Reversed()
            {
                var points = new List<Point3d>(Points);
                points.Reverse();
                return new Piece(ObjectId, points);
            }
        }

        private sealed class Attachment
        {
            internal Attachment(Piece piece, bool reverse, bool prepend, double distance)
            {
                Piece = piece;
                Reverse = reverse;
                Prepend = prepend;
                Distance = distance;
            }
            internal Piece Piece;
            internal bool Reverse;
            internal bool Prepend;
            internal double Distance;
        }
    }
}

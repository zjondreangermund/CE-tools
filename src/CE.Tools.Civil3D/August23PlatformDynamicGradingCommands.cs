using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil;
using Autodesk.Civil.ApplicationServices;
using CivilFeatureLine = Autodesk.Civil.DatabaseServices.FeatureLine;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace CETools.Civil3D
{
    /// <summary>
    /// August 23 platform production completion:
    /// - safe multi-feature-line surface draping with persistent links;
    /// - dynamic daylight/grade-to-surface links that never modify the target surface;
    /// - native Civil 3D grading-group/infill attempts for closed platforms; and
    /// - endpoint gap closing that moves only the chosen moving endpoint.
    ///
    /// The grade-to-surface relation is stored on the source feature line. A daylight
    /// replacement is built and verified before an existing daylight is erased, so a
    /// failed refresh leaves the last valid result intact.
    /// </summary>
    internal sealed class August23PlatformDynamicGradingCommands
    {
        private const string GradeLinkKey = "CE_PLATFORM_GRADE_LINK";
        private const string DirectDrapeKey = "CE_PLATFORM_DIRECT_DRAPE";
        private const string PlatformSiteName = "CE-PLATFORM-SITE";
        private const double Tolerance = 0.000001;

        [CommandMethod("CE_TOOLS", "CE_PLATFORMDRAPEMULTI", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void DrapeMultipleFeatureLines()
        {
            PlatformDynamicRefreshManager.EnsureInitialized();
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            List<SurfaceOption> surfaces = ReadSurfaces(document);
            if (surfaces.Count == 0)
            {
                document.Editor.WriteMessage("\nCE_PLATFORMDRAPEMULTI cancelled. No Civil 3D surfaces were found.");
                return;
            }

            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Drape Multiple Feature Lines",
                "Drape multiple feature lines to one Civil 3D surface. The persistent link refreshes when the source or surface changes.");
            settings.AddChoice("Surface", "Surface", "Target surface", surfaces[0].Name, "Select the controlling Civil 3D surface.", surfaces.Select(item => item.Name));
            settings.AddChoice("Intermediate", "Surface", "Intermediate points", "No", "Existing feature-line points are sampled safely; the option is retained for workflow compatibility.", new[] { "No", "Yes" });
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;

            SurfaceOption selectedSurface = surfaces.FirstOrDefault(item => string.Equals(item.Name, settings.Text("Surface"), StringComparison.OrdinalIgnoreCase));
            if (selectedSurface == null) return;
            bool intermediate = string.Equals(settings.Text("Intermediate"), "Yes", StringComparison.OrdinalIgnoreCase);

            PromptSelectionResult selection = SelectFeatureLines(document.Editor, "\nSelect feature lines to drape dynamically: ");
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;

            int linked = 0;
            int skipped = 0;
            foreach (ObjectId featureLineId in selection.Value.GetObjectIds().Distinct())
            {
                string error;
                if (!IsEditableFeatureLine(document.Database, featureLineId, out error))
                {
                    skipped++;
                    document.Editor.WriteMessage("\nDrape skipped. " + error);
                    continue;
                }

                if (!August21SurfaceSafety.TryApplyFeatureLineElevations(document, featureLineId, selectedSurface.Name, intermediate, out error))
                {
                    skipped++;
                    document.Editor.WriteMessage("\nDrape skipped safely. " + error);
                    continue;
                }

                try
                {
                    WriteDirectDrapeLink(document.Database, featureLineId, new DirectDrapeLink
                    {
                        SurfaceHandle = selectedSurface.ObjectId.Handle.ToString(),
                        Intermediate = intermediate
                    });
                    linked++;
                }
                catch (System.Exception exception)
                {
                    skipped++;
                    document.Editor.WriteMessage("\nDrape geometry was kept, but its persistent link could not be written. " + exception.Message);
                }
            }

            document.Editor.Regen();
            PlatformDynamicRefreshManager.Queue();
            document.Editor.WriteMessage("\nCE_PLATFORMDRAPEMULTI complete. Dynamic links={0}; skipped={1}.", linked, skipped);
        }

        [CommandMethod("CE_TOOLS", "CE_PLATFORMGRADETOSURFACE", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void GradeToSurface()
        {
            PlatformDynamicRefreshManager.EnsureInitialized();
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            List<SurfaceOption> surfaces = ReadSurfaces(document);
            if (surfaces.Count == 0)
            {
                document.Editor.WriteMessage("\nCE_PLATFORMGRADETOSURFACE cancelled. No Civil 3D surfaces were found.");
                return;
            }

            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Grade Platforms to Surface",
                "Create dynamic daylight feature lines from multiple platform edges. The target surface is sampled read-only and is never rebuilt or given breaklines by this command.");
            settings.AddChoice("Surface", "Target", "Target surface", surfaces[0].Name, "Natural ground / controlling target surface.", surfaces.Select(item => item.Name));
            settings.AddPositiveDouble("CutRatio", "Slopes", "Cut slope H:V", 2.0, "Example: 2.0 means 2H:1V when the target is above the platform edge.");
            settings.AddPositiveDouble("FillRatio", "Slopes", "Fill slope H:V", 2.0, "Example: 2.0 means 2H:1V when the target is below the platform edge.");
            settings.AddPositiveDouble("MaxDistance", "Search", "Maximum daylight search", 50.0, "Maximum horizontal distance searched from every source point.");
            settings.AddPositiveDouble("SearchStep", "Search", "Surface search step", 0.5, "Horizontal search increment before the final intersection is bisected.");
            settings.AddChoice("Side", "Direction", "Projection side", "Auto", "Auto uses outward for closed platforms and Left for open feature lines.", new[] { "Auto", "Left", "Right" });
            settings.AddChoice("Infill", "Grading", "Native grading infill", "Yes", "Attempt a native Civil 3D grading group/infill for closed platforms. Daylight geometry remains valid if the host infill API rejects the region.", new[] { "Yes", "No" });
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;

            SurfaceOption selectedSurface = surfaces.FirstOrDefault(item => string.Equals(item.Name, settings.Text("Surface"), StringComparison.OrdinalIgnoreCase));
            if (selectedSurface == null) return;

            PromptSelectionResult selection = SelectFeatureLines(document.Editor, "\nSelect multiple platform/source feature lines to grade to surface: ");
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;

            var requested = new GradeLink
            {
                SurfaceHandle = selectedSurface.ObjectId.Handle.ToString(),
                CutRatio = Math.Max(0.001, settings.Double("CutRatio", 2.0)),
                FillRatio = Math.Max(0.001, settings.Double("FillRatio", 2.0)),
                MaxDistance = Math.Max(0.10, settings.Double("MaxDistance", 50.0)),
                SearchStep = Math.Max(0.05, settings.Double("SearchStep", 0.5)),
                Side = SafeSide(settings.Text("Side")),
                NativeInfill = string.Equals(settings.Text("Infill"), "Yes", StringComparison.OrdinalIgnoreCase)
            };

            int completed = 0;
            int skipped = 0;
            int infills = 0;
            foreach (ObjectId sourceId in selection.Value.GetObjectIds().Distinct())
            {
                GradeBuildResult result = BuildOrRefreshGrade(document, sourceId, requested, true);
                if (result.Success)
                {
                    completed++;
                    if (result.NativeInfillCreated) infills++;
                }
                else
                {
                    skipped++;
                    document.Editor.WriteMessage("\nGrade-to-surface skipped safely. " + result.Message);
                }
            }

            document.Editor.Regen();
            PlatformDynamicRefreshManager.Queue();
            document.Editor.WriteMessage(
                "\nCE_PLATFORMGRADETOSURFACE complete. Dynamic daylight links={0}; native infills={1}; skipped={2}.",
                completed,
                infills,
                skipped);
        }

        [CommandMethod("CE_TOOLS", "CE_FLCLOSEGAP", CommandFlags.Modal | CommandFlags.Redraw)]
        public void CloseFeatureLineGap()
        {
            PlatformDynamicRefreshManager.EnsureInitialized();
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            PromptEntityOptions fixedOptions = new PromptEntityOptions("\nSelect the feature line endpoint that must stay fixed: ");
            fixedOptions.SetRejectMessage("\nSelect an editable Civil 3D feature line.");
            fixedOptions.AddAllowedClass(typeof(CivilFeatureLine), false);
            PromptEntityResult fixedResult = document.Editor.GetEntity(fixedOptions);
            if (fixedResult.Status != PromptStatus.OK) return;

            PromptEntityOptions movingOptions = new PromptEntityOptions("\nSelect the feature line endpoint that must move onto the fixed endpoint: ");
            movingOptions.SetRejectMessage("\nSelect an editable Civil 3D feature line.");
            movingOptions.AddAllowedClass(typeof(CivilFeatureLine), false);
            PromptEntityResult movingResult = document.Editor.GetEntity(movingOptions);
            if (movingResult.Status != PromptStatus.OK) return;
            if (fixedResult.ObjectId == movingResult.ObjectId)
            {
                document.Editor.WriteMessage("\nCE_FLCLOSEGAP cancelled. Select two different feature lines.");
                return;
            }

            string error;
            if (!MoveSelectedEndpointExactly(
                    document,
                    fixedResult.ObjectId,
                    fixedResult.PickedPoint,
                    movingResult.ObjectId,
                    movingResult.PickedPoint,
                    out error))
            {
                document.Editor.WriteMessage("\nCE_FLCLOSEGAP cancelled safely. " + error);
                return;
            }

            document.Editor.Regen();
            PlatformDynamicRefreshManager.Queue();
            document.Editor.WriteMessage("\nCE_FLCLOSEGAP complete. The fixed endpoint was unchanged; only the selected moving endpoint was snapped exactly to it.");
        }

        internal static int RefreshAll(Document document)
        {
            if (document == null || document.Database == null) return 0;

            var directDrapes = new List<KeyValuePair<ObjectId, DirectDrapeLink>>();
            var grades = new List<KeyValuePair<ObjectId, GradeLink>>();
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = transaction.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(document.Database),
                    OpenMode.ForRead,
                    false) as BlockTableRecord;
                if (space == null) return 0;
                foreach (ObjectId id in space)
                {
                    CivilFeatureLine featureLine = OpenFeatureLine(transaction, id, OpenMode.ForRead);
                    if (featureLine == null) continue;
                    DirectDrapeLink drape;
                    if (TryReadDirectDrapeLink(featureLine, transaction, out drape))
                        directDrapes.Add(new KeyValuePair<ObjectId, DirectDrapeLink>(id, drape));
                    GradeLink grade;
                    if (TryReadGradeLink(featureLine, transaction, out grade))
                        grades.Add(new KeyValuePair<ObjectId, GradeLink>(id, grade));
                }
            }

            int refreshed = 0;
            foreach (KeyValuePair<ObjectId, DirectDrapeLink> item in directDrapes)
            {
                ObjectId surfaceId = ResolveHandle(document.Database, item.Value.SurfaceHandle);
                string surfaceName = August21SurfaceSafety.ReadSurfaceName(document, surfaceId);
                if (surfaceId.IsNull || string.IsNullOrWhiteSpace(surfaceName)) continue;
                string error;
                if (August21SurfaceSafety.TryApplyFeatureLineElevations(document, item.Key, surfaceName, item.Value.Intermediate, out error))
                    refreshed++;
                else
                    document.Editor.WriteMessage("\nA linked multi-drape was kept unchanged after a safe refresh failure. " + error);
            }

            foreach (KeyValuePair<ObjectId, GradeLink> item in grades)
            {
                GradeBuildResult result = BuildOrRefreshGrade(document, item.Key, item.Value, false);
                if (result.Success) refreshed++;
                else document.Editor.WriteMessage("\nA linked grade-to-surface daylight was kept unchanged. " + result.Message);
            }
            return refreshed;
        }

        private static GradeBuildResult BuildOrRefreshGrade(Document document, ObjectId sourceId, GradeLink requested, bool explicitCommand)
        {
            var result = new GradeBuildResult();
            if (document == null || sourceId.IsNull)
            {
                result.Message = "The source feature line is unavailable.";
                return result;
            }

            GradeLink existing = ReadGradeLink(document.Database, sourceId);
            GradeLink link = requested.Clone();
            if (string.IsNullOrWhiteSpace(link.ChildHandle) && existing != null) link.ChildHandle = existing.ChildHandle;
            if (string.IsNullOrWhiteSpace(link.GroupHandle) && existing != null) link.GroupHandle = existing.GroupHandle;
            if (string.IsNullOrWhiteSpace(link.InfillHandle) && existing != null) link.InfillHandle = existing.InfillHandle;

            ObjectId surfaceId = ResolveHandle(document.Database, link.SurfaceHandle);
            if (surfaceId.IsNull)
            {
                result.Message = "The linked target surface no longer exists.";
                return result;
            }

            if (link.NativeInfill)
            {
                try
                {
                    EnsureSourceHasSite(document, sourceId);
                }
                catch (System.Exception exception)
                {
                    if (explicitCommand)
                        document.Editor.WriteMessage("\nNative infill site preparation was skipped; daylight generation will continue. " + exception.Message);
                }
            }

            SourceSnapshot source;
            string error;
            if (!TryReadSource(document.Database, sourceId, out source, out error))
            {
                result.Message = error;
                return result;
            }

            List<Point3d> daylight;
            if (!TryBuildDaylight(document.Database, surfaceId, source, link, out daylight, out error))
            {
                result.Message = error;
                return result;
            }
            if (daylight.Count < 2 || daylight.Any(point => !Finite(point)))
            {
                result.Message = "The daylight calculation did not produce a valid finite feature line.";
                return result;
            }

            ObjectId candidateId;
            if (!TryCreateFeatureLineCandidate(document, source, daylight, out candidateId, out error))
            {
                result.Message = error;
                return result;
            }

            ObjectId oldChildId = ResolveHandle(document.Database, link.ChildHandle);
            string desiredName = ReadFeatureLineName(document.Database, oldChildId);
            if (string.IsNullOrWhiteSpace(desiredName))
                desiredName = UniqueFeatureLineName(document.Database, SafeName(source.Name, "PLATFORM") + "-DAYLIGHT", oldChildId);

            if (!TrySwapCandidate(document.Database, oldChildId, candidateId, desiredName, out error))
            {
                Cleanup(document.Database, candidateId);
                result.Message = error;
                return result;
            }

            link.ChildHandle = candidateId.Handle.ToString();
            ObjectId groupId = ResolveHandle(document.Database, link.GroupHandle);
            ObjectId previousInfillId = ResolveHandle(document.Database, link.InfillHandle);
            link.InfillHandle = string.Empty;

            if (!previousInfillId.IsNull)
                Cleanup(document.Database, previousInfillId);

            if (link.NativeInfill && source.Closed && !source.SiteId.IsNull)
            {
                if (groupId.IsNull)
                {
                    groupId = TryCreateGradingGroup(document.Database, source.SiteId, "CE-PLATFORM-GRADE-" + sourceId.Handle.ToString());
                    if (!groupId.IsNull) link.GroupHandle = groupId.Handle.ToString();
                }
                if (!groupId.IsNull)
                {
                    ObjectId infillId;
                    if (TryCreateInfill(groupId, Centre(source.Points), out infillId))
                    {
                        link.InfillHandle = infillId.IsNull ? string.Empty : infillId.Handle.ToString();
                        result.NativeInfillCreated = true;
                    }
                }
            }

            try
            {
                WriteGradeLink(document.Database, sourceId, link);
            }
            catch (System.Exception exception)
            {
                result.Message = "The daylight was created, but the persistent grade link could not be written: " + exception.Message;
                return result;
            }

            result.Success = true;
            result.Message = string.Empty;
            return result;
        }

        private static bool TryBuildDaylight(Database database, ObjectId surfaceId, SourceSnapshot source, GradeLink link, out List<Point3d> daylight, out string error)
        {
            daylight = new List<Point3d>();
            error = string.Empty;
            if (source.Points == null || source.Points.Count < 2)
            {
                error = "The source feature line has too few points.";
                return false;
            }

            double area = source.Closed ? SignedArea(source.Points) : 0.0;
            try
            {
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    CivilSurface surface = transaction.GetObject(surfaceId, OpenMode.ForRead, false) as CivilSurface;
                    if (surface == null)
                    {
                        error = "The target surface is not readable.";
                        return false;
                    }

                    for (int index = 0; index < source.Points.Count; index++)
                    {
                        Point3d sourcePoint = source.Points[index];
                        Vector2d direction;
                        if (!TryProjectionDirection(source.Points, index, source.Closed, area, link.Side, out direction))
                        {
                            error = "A source vertex has no usable plan direction for daylight projection.";
                            return false;
                        }
                        Point3d intersection;
                        if (!TryFindDaylight(surface, sourcePoint, direction, link, out intersection))
                        {
                            error = "No cut/fill daylight intersection was found within " + link.MaxDistance.ToString("N2", CultureInfo.CurrentCulture) + " at source point " + (index + 1).ToString(CultureInfo.InvariantCulture) + ". The previous linked daylight, if any, was kept.";
                            return false;
                        }
                        daylight.Add(intersection);
                    }
                }
            }
            catch (System.Exception exception)
            {
                error = "Target-surface daylight sampling failed safely: " + exception.Message;
                return false;
            }
            return true;
        }

        private static bool TryFindDaylight(CivilSurface surface, Point3d source, Vector2d direction, GradeLink link, out Point3d result)
        {
            result = Point3d.Origin;
            double step = Math.Max(0.05, Math.Min(link.SearchStep, link.MaxDistance));
            bool modeKnown = false;
            bool cut = false;
            bool previousValid = false;
            double previousDistance = 0.0;
            double previousDifference = 0.0;

            for (double distance = 0.0; distance <= link.MaxDistance + Tolerance; distance += step)
            {
                double terrain;
                if (!TrySurfaceElevation(surface, source, direction, distance, out terrain))
                {
                    previousValid = false;
                    continue;
                }
                if (!modeKnown)
                {
                    double delta = terrain - source.Z;
                    if (Math.Abs(delta) <= 0.005 && distance <= Tolerance)
                    {
                        result = new Point3d(source.X, source.Y, terrain);
                        return true;
                    }
                    cut = delta > 0.0;
                    modeKnown = true;
                }

                double ratio = cut ? link.CutRatio : link.FillRatio;
                double gradeElevation = source.Z + (cut ? distance / ratio : -distance / ratio);
                double difference = gradeElevation - terrain;
                if (Math.Abs(difference) <= 0.005)
                {
                    result = new Point3d(source.X + direction.X * distance, source.Y + direction.Y * distance, terrain);
                    return true;
                }

                if (previousValid && Math.Sign(previousDifference) != Math.Sign(difference))
                    return TryBisectDaylight(surface, source, direction, cut, ratio, previousDistance, distance, previousDifference, difference, out result);

                previousValid = true;
                previousDistance = distance;
                previousDifference = difference;
            }
            return false;
        }

        private static bool TryBisectDaylight(CivilSurface surface, Point3d source, Vector2d direction, bool cut, double ratio, double low, double high, double lowDifference, double highDifference, out Point3d result)
        {
            result = Point3d.Origin;
            for (int iteration = 0; iteration < 32; iteration++)
            {
                double middle = (low + high) * 0.5;
                double terrain;
                if (!TrySurfaceElevation(surface, source, direction, middle, out terrain))
                    return false;
                double gradeElevation = source.Z + (cut ? middle / ratio : -middle / ratio);
                double difference = gradeElevation - terrain;
                if (Math.Abs(difference) <= 0.001 || Math.Abs(high - low) <= 0.001)
                {
                    result = new Point3d(source.X + direction.X * middle, source.Y + direction.Y * middle, terrain);
                    return true;
                }
                if (Math.Sign(lowDifference) == Math.Sign(difference))
                {
                    low = middle;
                    lowDifference = difference;
                }
                else
                {
                    high = middle;
                    highDifference = difference;
                }
            }
            double finalDistance = (low + high) * 0.5;
            double finalTerrain;
            if (!TrySurfaceElevation(surface, source, direction, finalDistance, out finalTerrain)) return false;
            result = new Point3d(source.X + direction.X * finalDistance, source.Y + direction.Y * finalDistance, finalTerrain);
            return true;
        }

        private static bool TrySurfaceElevation(CivilSurface surface, Point3d source, Vector2d direction, double distance, out double elevation)
        {
            elevation = 0.0;
            try
            {
                elevation = surface.FindElevationAtXY(source.X + direction.X * distance, source.Y + direction.Y * distance);
                return Finite(elevation);
            }
            catch { return false; }
        }

        private static bool TryProjectionDirection(IList<Point3d> points, int index, bool closed, double signedArea, string side, out Vector2d direction)
        {
            direction = new Vector2d();
            int count = points.Count;
            if (count < 2) return false;

            Vector2d tangent;
            if (!closed && index == 0)
                tangent = new Vector2d(points[1].X - points[0].X, points[1].Y - points[0].Y);
            else if (!closed && index == count - 1)
                tangent = new Vector2d(points[count - 1].X - points[count - 2].X, points[count - 1].Y - points[count - 2].Y);
            else
            {
                int previous = index == 0 ? count - 1 : index - 1;
                int next = index == count - 1 ? 0 : index + 1;
                Vector2d incoming = new Vector2d(points[index].X - points[previous].X, points[index].Y - points[previous].Y);
                Vector2d outgoing = new Vector2d(points[next].X - points[index].X, points[next].Y - points[index].Y);
                if (incoming.Length > Tolerance) incoming = incoming.GetNormal();
                if (outgoing.Length > Tolerance) outgoing = outgoing.GetNormal();
                tangent = incoming + outgoing;
                if (tangent.Length <= Tolerance) tangent = outgoing.Length > Tolerance ? outgoing : incoming;
            }
            if (tangent.Length <= Tolerance) return false;
            tangent = tangent.GetNormal();
            Vector2d left = new Vector2d(-tangent.Y, tangent.X);

            double sign;
            if (string.Equals(side, "Left", StringComparison.OrdinalIgnoreCase)) sign = 1.0;
            else if (string.Equals(side, "Right", StringComparison.OrdinalIgnoreCase)) sign = -1.0;
            else if (closed) sign = signedArea >= 0.0 ? -1.0 : 1.0;
            else sign = 1.0;

            direction = left.MultiplyBy(sign);
            return direction.Length > Tolerance;
        }

        private static bool MoveSelectedEndpointExactly(Document document, ObjectId fixedId, Point3d fixedPick, ObjectId movingId, Point3d movingPick, out string error)
        {
            error = string.Empty;
            try
            {
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    CivilFeatureLine fixedLine = OpenFeatureLine(transaction, fixedId, OpenMode.ForRead);
                    CivilFeatureLine movingLine = OpenFeatureLine(transaction, movingId, OpenMode.ForWrite);
                    if (!Editable(fixedLine, transaction) || !Editable(movingLine, transaction))
                    {
                        error = "Both feature lines must be local, editable and on unlocked layers.";
                        return false;
                    }

                    Point3dCollection fixedPoints = fixedLine.GetPoints(FeatureLinePointType.PIPoint);
                    Point3dCollection movingPoints = movingLine.GetPoints(FeatureLinePointType.PIPoint);
                    if (fixedPoints == null || fixedPoints.Count < 2 || movingPoints == null || movingPoints.Count < 2)
                    {
                        error = "Both feature lines need readable start and end points.";
                        return false;
                    }

                    Point3d fixedEndpoint = NearestEndpoint(fixedPoints, fixedPick);
                    Point3d movingEndpoint = NearestEndpoint(movingPoints, movingPick);
                    Point3dCollection grips = new Point3dCollection();
                    IntegerCollection snapModes = new IntegerCollection(1);
                    IntegerCollection geometryIds = new IntegerCollection(1);
                    snapModes.Add(0);
                    geometryIds.Add(0);
                    movingLine.GetGripPoints(grips, snapModes, geometryIds);
                    if (grips.Count == 0)
                    {
                        error = "Civil 3D returned no editable grips for the moving feature line.";
                        return false;
                    }

                    int gripIndex = ClosestIndex(grips, movingEndpoint);
                    if (gripIndex < 0)
                    {
                        error = "The selected moving endpoint grip could not be resolved.";
                        return false;
                    }

                    var indices = new IntegerCollection(1);
                    indices.Add(gripIndex);
                    Vector3d offset = fixedEndpoint - grips[gripIndex];
                    movingLine.MoveGripPointsAt(indices, offset);

                    Point3dCollection verification = movingLine.GetPoints(FeatureLinePointType.PIPoint);
                    Point3d newEndpoint = NearestEndpoint(verification, fixedEndpoint);
                    if (newEndpoint.DistanceTo(fixedEndpoint) > 0.0001)
                    {
                        error = "Civil 3D did not accept an exact endpoint move; the transaction was rolled back instead of moving both endpoints to a midpoint.";
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

        private static bool TryCreateFeatureLineCandidate(Document document, SourceSnapshot source, IList<Point3d> points, out ObjectId featureLineId, out string error)
        {
            featureLineId = ObjectId.Null;
            error = string.Empty;
            ObjectId temporaryId = ObjectId.Null;
            try
            {
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    BlockTableRecord space = transaction.GetObject(
                        SymbolUtilityServices.GetBlockModelSpaceId(document.Database),
                        OpenMode.ForWrite,
                        false) as BlockTableRecord;
                    var temporary = new Polyline3d(Poly3dType.SimplePoly, new Point3dCollection(points.ToArray()), source.Closed);
                    temporary.SetDatabaseDefaults(document.Database);
                    if (!source.LayerId.IsNull) temporary.LayerId = source.LayerId;
                    temporaryId = space.AppendEntity(temporary);
                    transaction.AddNewlyCreatedDBObject(temporary, true);
                    transaction.Commit();
                }

                string candidateName = "CE-GRADE-CANDIDATE-" + Guid.NewGuid().ToString("N");
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    featureLineId = source.SiteId.IsNull
                        ? CivilFeatureLine.Create(candidateName, temporaryId)
                        : CivilFeatureLine.Create(candidateName, temporaryId, source.SiteId);
                    CivilFeatureLine featureLine = OpenFeatureLine(transaction, featureLineId, OpenMode.ForWrite);
                    if (featureLine == null || featureLine.IsReferenceObject)
                        throw new InvalidOperationException("Civil 3D did not return an editable daylight feature line.");
                    if (!source.LayerId.IsNull) featureLine.LayerId = source.LayerId;
                    if (!string.IsNullOrWhiteSpace(source.StyleName))
                    {
                        try { featureLine.StyleName = source.StyleName; } catch { }
                    }
                    Point3dCollection verification = featureLine.GetPoints(FeatureLinePointType.AllPoints);
                    if (verification == null || verification.Count < 2 || verification.Cast<Point3d>().Any(point => !Finite(point)))
                        throw new InvalidOperationException("The daylight candidate failed finite-point verification.");
                    transaction.Commit();
                }
                return true;
            }
            catch (System.Exception exception)
            {
                error = exception.Message;
                if (!featureLineId.IsNull) Cleanup(document.Database, featureLineId);
                featureLineId = ObjectId.Null;
                return false;
            }
            finally
            {
                if (!temporaryId.IsNull) Cleanup(document.Database, temporaryId);
            }
        }

        private static bool TrySwapCandidate(Database database, ObjectId oldChildId, ObjectId candidateId, string desiredName, out string error)
        {
            error = string.Empty;
            try
            {
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    CivilFeatureLine candidate = OpenFeatureLine(transaction, candidateId, OpenMode.ForWrite);
                    if (candidate == null) throw new InvalidOperationException("The verified daylight candidate became unavailable.");
                    CivilFeatureLine oldChild = OpenFeatureLine(transaction, oldChildId, OpenMode.ForWrite);
                    if (oldChild != null)
                    {
                        try { oldChild.Name = "CE-OLD-DAYLIGHT-" + Guid.NewGuid().ToString("N"); } catch { }
                    }
                    try { candidate.Name = desiredName; }
                    catch { candidate.Name = "CE-DAYLIGHT-" + candidateId.Handle.ToString(); }
                    if (oldChild != null && !oldChild.IsErased) oldChild.Erase();
                    transaction.Commit();
                }
                return true;
            }
            catch (System.Exception exception)
            {
                error = "The new daylight was verified, but the candidate swap failed safely: " + exception.Message;
                return false;
            }
        }

        private static void EnsureSourceHasSite(Document document, ObjectId sourceId)
        {
            ObjectId currentSite = ObjectId.Null;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                CivilFeatureLine source = OpenFeatureLine(transaction, sourceId, OpenMode.ForRead);
                if (source == null || source.IsReferenceObject) throw new InvalidOperationException("The source feature line is unavailable or referenced.");
                currentSite = source.SiteId;
            }
            if (!currentSite.IsNull) return;

            ObjectId siteId = EnsureSite(document.Database, CivilApplication.ActiveDocument, PlatformSiteName);
            if (siteId.IsNull) throw new InvalidOperationException("Civil 3D could not create or resolve the CE platform site.");
            if (!MoveToSite(sourceId, siteId)) throw new InvalidOperationException("Civil 3D could not move the source feature line into the CE platform site.");
        }

        private static ObjectId EnsureSite(Database database, CivilDocument civilDocument, string name)
        {
            if (civilDocument == null) return ObjectId.Null;
            try
            {
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId id in civilDocument.GetSiteIds())
                    {
                        DBObject value = transaction.GetObject(id, OpenMode.ForRead, false);
                        PropertyInfo property = value.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
                        string current = property == null ? string.Empty : Convert.ToString(property.GetValue(value, null), CultureInfo.CurrentCulture);
                        if (string.Equals(current, name, StringComparison.OrdinalIgnoreCase)) return id;
                    }
                }
            }
            catch { }

            Type siteType = typeof(CivilFeatureLine).Assembly.GetType("Autodesk.Civil.DatabaseServices.Site", false);
            if (siteType == null) return ObjectId.Null;
            foreach (MethodInfo method in siteType.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(item => string.Equals(item.Name, "Create", StringComparison.OrdinalIgnoreCase)).OrderBy(item => item.GetParameters().Length))
            {
                object[] args = BuildHostArguments(method.GetParameters(), database, civilDocument, name, ObjectId.Null, Point3d.Origin);
                if (args == null) continue;
                try
                {
                    object value = method.Invoke(null, args);
                    if (value is ObjectId) return (ObjectId)value;
                    DBObject dbObject = value as DBObject;
                    if (dbObject != null) return dbObject.ObjectId;
                }
                catch { }
            }
            return ObjectId.Null;
        }

        private static bool MoveToSite(ObjectId featureLineId, ObjectId siteId)
        {
            foreach (MethodInfo method in typeof(CivilFeatureLine).GetMethods(BindingFlags.Public | BindingFlags.Static).Where(item => item.Name.IndexOf("MoveToSite", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length != 2 || parameters.Any(parameter => parameter.ParameterType != typeof(ObjectId))) continue;
                try { method.Invoke(null, new object[] { featureLineId, siteId }); return true; }
                catch { }
            }
            return false;
        }

        private static ObjectId TryCreateGradingGroup(Database database, ObjectId siteId, string name)
        {
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            Type groupType = typeof(CivilFeatureLine).Assembly.GetType("Autodesk.Civil.DatabaseServices.GradingGroup", false);
            if (groupType == null) return ObjectId.Null;
            ObjectId id = ObjectId.Null;
            foreach (MethodInfo method in groupType.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(item => string.Equals(item.Name, "Create", StringComparison.OrdinalIgnoreCase)).OrderBy(item => item.GetParameters().Length))
            {
                object[] args = BuildHostArguments(method.GetParameters(), database, civilDocument, name, siteId, Point3d.Origin);
                if (args == null) continue;
                try
                {
                    object value = method.Invoke(null, args);
                    if (value is ObjectId) { id = (ObjectId)value; break; }
                }
                catch { }
            }
            if (id.IsNull) return id;
            try
            {
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    DBObject group = transaction.GetObject(id, OpenMode.ForWrite, false);
                    SetProperty(group, "AutomaticSurfaceCreation", false);
                    transaction.Commit();
                }
            }
            catch { }
            return id;
        }

        private static bool TryCreateInfill(ObjectId groupId, Point3d seed, out ObjectId infillId)
        {
            infillId = ObjectId.Null;
            Type gradingType = typeof(CivilFeatureLine).Assembly.GetType("Autodesk.Civil.DatabaseServices.Grading", false);
            if (gradingType == null) return false;
            foreach (MethodInfo method in gradingType.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(item => item.Name.IndexOf("CreateInfill", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                ParameterInfo[] parameters = method.GetParameters();
                var args = new object[parameters.Length];
                bool valid = true;
                for (int index = 0; index < parameters.Length; index++)
                {
                    Type type = parameters[index].ParameterType;
                    if (type == typeof(ObjectId)) args[index] = groupId;
                    else if (type == typeof(Point3d)) args[index] = seed;
                    else if (type == typeof(string)) args[index] = "CE Platform Grade Infill";
                    else if (type == typeof(bool)) args[index] = true;
                    else if (parameters[index].HasDefaultValue) args[index] = parameters[index].DefaultValue;
                    else { valid = false; break; }
                }
                if (!valid) continue;
                try
                {
                    object value = method.Invoke(null, args);
                    if (value is ObjectId) infillId = (ObjectId)value;
                    return true;
                }
                catch { }
            }
            return false;
        }

        private static object[] BuildHostArguments(ParameterInfo[] parameters, Database database, CivilDocument civilDocument, string name, ObjectId relatedId, Point3d point)
        {
            var args = new object[parameters.Length];
            for (int index = 0; index < parameters.Length; index++)
            {
                Type type = parameters[index].ParameterType;
                if (type == typeof(Database)) args[index] = database;
                else if (type == typeof(CivilDocument)) args[index] = civilDocument;
                else if (type == typeof(string)) args[index] = name;
                else if (type == typeof(ObjectId)) args[index] = relatedId;
                else if (type == typeof(Point3d)) args[index] = point;
                else if (type == typeof(bool)) args[index] = true;
                else if (type == typeof(double)) args[index] = 0.0;
                else if (parameters[index].HasDefaultValue) args[index] = parameters[index].DefaultValue;
                else return null;
            }
            return args;
        }

        private static void SetProperty(object target, string name, object value)
        {
            if (target == null) return;
            PropertyInfo property = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property == null || !property.CanWrite) return;
            try { property.SetValue(target, value, null); } catch { }
        }

        private static bool TryReadSource(Database database, ObjectId sourceId, out SourceSnapshot snapshot, out string error)
        {
            snapshot = null;
            error = string.Empty;
            try
            {
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    CivilFeatureLine source = OpenFeatureLine(transaction, sourceId, OpenMode.ForRead);
                    if (!Editable(source, transaction))
                    {
                        error = "The source feature line is unavailable, referenced or on a locked layer.";
                        return false;
                    }
                    Point3dCollection collection = source.GetPoints(FeatureLinePointType.AllPoints);
                    if (collection == null || collection.Count < 2)
                    {
                        error = "The source feature line has too few points.";
                        return false;
                    }
                    snapshot = new SourceSnapshot
                    {
                        ObjectId = sourceId,
                        Name = source.Name,
                        LayerId = source.LayerId,
                        SiteId = source.SiteId,
                        StyleName = source.StyleName,
                        Closed = source.Closed,
                        Points = collection.Cast<Point3d>().ToList()
                    };
                }
                return snapshot.Points.All(Finite);
            }
            catch (System.Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }

        private static bool IsEditableFeatureLine(Database database, ObjectId id, out string error)
        {
            error = string.Empty;
            try
            {
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    CivilFeatureLine featureLine = OpenFeatureLine(transaction, id, OpenMode.ForRead);
                    if (!Editable(featureLine, transaction))
                    {
                        error = "The selected object is not a local editable feature line or is on a locked layer.";
                        return false;
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

        private static List<SurfaceOption> ReadSurfaces(Document document)
        {
            var result = new List<SurfaceOption>();
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (document == null || civilDocument == null) return result;
            try
            {
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId id in civilDocument.GetSurfaceIds())
                    {
                        CivilSurface surface = transaction.GetObject(id, OpenMode.ForRead, false) as CivilSurface;
                        if (surface != null && !string.IsNullOrWhiteSpace(surface.Name)) result.Add(new SurfaceOption(surface.Name, id));
                    }
                }
            }
            catch { }
            return result.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        private static PromptSelectionResult SelectFeatureLines(Editor editor, string message)
        {
            PromptSelectionResult implied = editor.SelectImplied();
            if (implied.Status == PromptStatus.OK && implied.Value != null && implied.Value.Count > 0)
            {
                editor.SetImpliedSelection(new ObjectId[0]);
                return implied;
            }
            return editor.GetSelection(new PromptSelectionOptions
            {
                MessageForAdding = message,
                AllowDuplicates = false,
                RejectObjectsFromNonCurrentSpace = true
            });
        }

        private static void WriteDirectDrapeLink(Database database, ObjectId sourceId, DirectDrapeLink link)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                CivilFeatureLine source = OpenFeatureLine(transaction, sourceId, OpenMode.ForWrite);
                if (source == null) throw new InvalidOperationException("The draped feature line is unavailable.");
                Xrecord record = Record(source, transaction, DirectDrapeKey);
                record.Data = new ResultBuffer(
                    new TypedValue((int)DxfCode.Text, link.SurfaceHandle ?? string.Empty),
                    new TypedValue((int)DxfCode.Int16, link.Intermediate ? 1 : 0));
                transaction.Commit();
            }
        }

        private static bool TryReadDirectDrapeLink(CivilFeatureLine source, Transaction transaction, out DirectDrapeLink link)
        {
            link = null;
            TypedValue[] values = ReadRecord(source, transaction, DirectDrapeKey);
            if (values == null || values.Length < 2) return false;
            link = new DirectDrapeLink
            {
                SurfaceHandle = Convert.ToString(values[0].Value, CultureInfo.InvariantCulture),
                Intermediate = Convert.ToInt16(values[1].Value, CultureInfo.InvariantCulture) != 0
            };
            return !string.IsNullOrWhiteSpace(link.SurfaceHandle);
        }

        private static void WriteGradeLink(Database database, ObjectId sourceId, GradeLink link)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                CivilFeatureLine source = OpenFeatureLine(transaction, sourceId, OpenMode.ForWrite);
                if (source == null) throw new InvalidOperationException("The grade source feature line is unavailable.");
                Xrecord record = Record(source, transaction, GradeLinkKey);
                record.Data = new ResultBuffer(
                    new TypedValue((int)DxfCode.Text, link.SurfaceHandle ?? string.Empty),
                    new TypedValue((int)DxfCode.Text, link.ChildHandle ?? string.Empty),
                    new TypedValue((int)DxfCode.Text, link.GroupHandle ?? string.Empty),
                    new TypedValue((int)DxfCode.Text, link.InfillHandle ?? string.Empty),
                    new TypedValue((int)DxfCode.Real, link.CutRatio),
                    new TypedValue((int)DxfCode.Real, link.FillRatio),
                    new TypedValue((int)DxfCode.Real, link.MaxDistance),
                    new TypedValue((int)DxfCode.Real, link.SearchStep),
                    new TypedValue((int)DxfCode.Text, link.Side ?? "Auto"),
                    new TypedValue((int)DxfCode.Int16, link.NativeInfill ? 1 : 0));
                transaction.Commit();
            }
        }

        private static GradeLink ReadGradeLink(Database database, ObjectId sourceId)
        {
            try
            {
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    CivilFeatureLine source = OpenFeatureLine(transaction, sourceId, OpenMode.ForRead);
                    GradeLink link;
                    return source != null && TryReadGradeLink(source, transaction, out link) ? link : null;
                }
            }
            catch { return null; }
        }

        private static bool TryReadGradeLink(CivilFeatureLine source, Transaction transaction, out GradeLink link)
        {
            link = null;
            TypedValue[] values = ReadRecord(source, transaction, GradeLinkKey);
            if (values == null || values.Length < 10) return false;
            try
            {
                link = new GradeLink
                {
                    SurfaceHandle = Convert.ToString(values[0].Value, CultureInfo.InvariantCulture),
                    ChildHandle = Convert.ToString(values[1].Value, CultureInfo.InvariantCulture),
                    GroupHandle = Convert.ToString(values[2].Value, CultureInfo.InvariantCulture),
                    InfillHandle = Convert.ToString(values[3].Value, CultureInfo.InvariantCulture),
                    CutRatio = Convert.ToDouble(values[4].Value, CultureInfo.InvariantCulture),
                    FillRatio = Convert.ToDouble(values[5].Value, CultureInfo.InvariantCulture),
                    MaxDistance = Convert.ToDouble(values[6].Value, CultureInfo.InvariantCulture),
                    SearchStep = Convert.ToDouble(values[7].Value, CultureInfo.InvariantCulture),
                    Side = SafeSide(Convert.ToString(values[8].Value, CultureInfo.InvariantCulture)),
                    NativeInfill = Convert.ToInt16(values[9].Value, CultureInfo.InvariantCulture) != 0
                };
                return !string.IsNullOrWhiteSpace(link.SurfaceHandle);
            }
            catch { link = null; return false; }
        }

        private static Xrecord Record(DBObject owner, Transaction transaction, string key)
        {
            if (owner.ExtensionDictionary.IsNull) owner.CreateExtensionDictionary();
            DBDictionary dictionary = transaction.GetObject(owner.ExtensionDictionary, OpenMode.ForWrite, false) as DBDictionary;
            if (dictionary == null) throw new InvalidOperationException("The feature-line extension dictionary is unavailable.");
            if (dictionary.Contains(key)) return transaction.GetObject(dictionary.GetAt(key), OpenMode.ForWrite, false) as Xrecord;
            var record = new Xrecord();
            dictionary.SetAt(key, record);
            transaction.AddNewlyCreatedDBObject(record, true);
            return record;
        }

        private static TypedValue[] ReadRecord(DBObject owner, Transaction transaction, string key)
        {
            if (owner == null || owner.ExtensionDictionary.IsNull) return null;
            DBDictionary dictionary = transaction.GetObject(owner.ExtensionDictionary, OpenMode.ForRead, false) as DBDictionary;
            if (dictionary == null || !dictionary.Contains(key)) return null;
            Xrecord record = transaction.GetObject(dictionary.GetAt(key), OpenMode.ForRead, false) as Xrecord;
            return record == null || record.Data == null ? null : record.Data.AsArray();
        }

        private static ObjectId ResolveHandle(Database database, string handleText)
        {
            if (database == null || string.IsNullOrWhiteSpace(handleText)) return ObjectId.Null;
            try
            {
                long value;
                if (!long.TryParse(handleText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)) return ObjectId.Null;
                ObjectId id = database.GetObjectId(false, new Handle(value), 0);
                return id.IsNull || id.IsErased ? ObjectId.Null : id;
            }
            catch { return ObjectId.Null; }
        }

        private static string ReadFeatureLineName(Database database, ObjectId id)
        {
            if (id.IsNull) return string.Empty;
            try
            {
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    CivilFeatureLine featureLine = OpenFeatureLine(transaction, id, OpenMode.ForRead);
                    return featureLine == null ? string.Empty : featureLine.Name;
                }
            }
            catch { return string.Empty; }
        }

        private static string UniqueFeatureLineName(Database database, string requested, ObjectId ignored)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(database), OpenMode.ForRead, false) as BlockTableRecord;
                if (space != null)
                {
                    foreach (ObjectId id in space)
                    {
                        if (id == ignored) continue;
                        CivilFeatureLine featureLine = OpenFeatureLine(transaction, id, OpenMode.ForRead);
                        if (featureLine != null && !string.IsNullOrWhiteSpace(featureLine.Name)) names.Add(featureLine.Name);
                    }
                }
            }
            string candidate = requested;
            int suffix = 2;
            while (names.Contains(candidate)) candidate = requested + " (" + (suffix++).ToString(CultureInfo.InvariantCulture) + ")";
            return candidate;
        }

        private static CivilFeatureLine OpenFeatureLine(Transaction transaction, ObjectId id, OpenMode mode)
        {
            if (id.IsNull || id.IsErased) return null;
            try { return transaction.GetObject(id, mode, false) as CivilFeatureLine; }
            catch { return null; }
        }

        private static bool Editable(CivilFeatureLine featureLine, Transaction transaction)
        {
            if (featureLine == null || featureLine.IsReferenceObject) return false;
            LayerTableRecord layer = transaction.GetObject(featureLine.LayerId, OpenMode.ForRead, false) as LayerTableRecord;
            return layer == null || !layer.IsLocked;
        }

        private static void Cleanup(Database database, ObjectId id)
        {
            if (database == null || id.IsNull || id.IsErased) return;
            try
            {
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    DBObject value = transaction.GetObject(id, OpenMode.ForWrite, false);
                    if (value != null && !value.IsErased) value.Erase();
                    transaction.Commit();
                }
            }
            catch { }
        }

        private static Point3d NearestEndpoint(Point3dCollection points, Point3d pick)
        {
            if (points == null || points.Count == 0) return Point3d.Origin;
            Point3d first = points[0];
            Point3d last = points[points.Count - 1];
            return first.DistanceTo(pick) <= last.DistanceTo(pick) ? first : last;
        }

        private static int ClosestIndex(Point3dCollection points, Point3d target)
        {
            int best = -1;
            double distance = double.MaxValue;
            for (int index = 0; index < points.Count; index++)
            {
                double current = points[index].DistanceTo(target);
                if (current < distance) { distance = current; best = index; }
            }
            return best;
        }

        private static double SignedArea(IList<Point3d> points)
        {
            double area = 0.0;
            for (int index = 0; index < points.Count; index++)
            {
                Point3d a = points[index];
                Point3d b = points[(index + 1) % points.Count];
                area += a.X * b.Y - b.X * a.Y;
            }
            return area * 0.5;
        }

        private static Point3d Centre(IList<Point3d> points)
        {
            if (points == null || points.Count == 0) return Point3d.Origin;
            return new Point3d(points.Average(point => point.X), points.Average(point => point.Y), points.Average(point => point.Z));
        }

        private static string SafeName(string value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        private static string SafeSide(string value)
        {
            if (string.Equals(value, "Left", StringComparison.OrdinalIgnoreCase)) return "Left";
            if (string.Equals(value, "Right", StringComparison.OrdinalIgnoreCase)) return "Right";
            return "Auto";
        }

        private static bool Finite(Point3d point)
        {
            return Finite(point.X) && Finite(point.Y) && Finite(point.Z);
        }

        private static bool Finite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private sealed class SurfaceOption
        {
            internal SurfaceOption(string name, ObjectId objectId) { Name = name; ObjectId = objectId; }
            internal string Name { get; private set; }
            internal ObjectId ObjectId { get; private set; }
        }

        private sealed class DirectDrapeLink
        {
            internal string SurfaceHandle { get; set; }
            internal bool Intermediate { get; set; }
        }

        private sealed class GradeLink
        {
            internal string SurfaceHandle { get; set; }
            internal string ChildHandle { get; set; }
            internal string GroupHandle { get; set; }
            internal string InfillHandle { get; set; }
            internal double CutRatio { get; set; }
            internal double FillRatio { get; set; }
            internal double MaxDistance { get; set; }
            internal double SearchStep { get; set; }
            internal string Side { get; set; }
            internal bool NativeInfill { get; set; }

            internal GradeLink Clone()
            {
                return new GradeLink
                {
                    SurfaceHandle = SurfaceHandle,
                    ChildHandle = ChildHandle,
                    GroupHandle = GroupHandle,
                    InfillHandle = InfillHandle,
                    CutRatio = CutRatio,
                    FillRatio = FillRatio,
                    MaxDistance = MaxDistance,
                    SearchStep = SearchStep,
                    Side = SafeSide(Side),
                    NativeInfill = NativeInfill
                };
            }
        }

        private sealed class SourceSnapshot
        {
            internal ObjectId ObjectId { get; set; }
            internal string Name { get; set; }
            internal ObjectId LayerId { get; set; }
            internal ObjectId SiteId { get; set; }
            internal string StyleName { get; set; }
            internal bool Closed { get; set; }
            internal List<Point3d> Points { get; set; }
        }

        private sealed class GradeBuildResult
        {
            internal bool Success { get; set; }
            internal bool NativeInfillCreated { get; set; }
            internal string Message { get; set; }
        }
    }
}

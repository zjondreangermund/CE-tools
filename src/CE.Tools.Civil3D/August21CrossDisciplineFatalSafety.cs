using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilFeatureLine = Autodesk.Civil.DatabaseServices.FeatureLine;

namespace CETools.Civil3D
{
    /// <summary>
    /// Final Civil 3D 2023 field-safety boundary for commands that create native
    /// feature-line objects. User/source geometry is never passed directly to
    /// FeatureLine.Create. A committed temporary clone/sanitized polyline is used,
    /// the returned feature line is reopened and verified, and only then is the
    /// temporary object removed. Every selected source is isolated in its own set
    /// of transactions so one malformed object cannot poison a whole batch.
    /// </summary>
    internal static class August21SafeFeatureLineCreation
    {
        private const double Tolerance = 0.000001;

        internal static void RunCreateFromObjects(Document document)
        {
            if (document == null || document.Database == null) return;
            Editor editor = document.Editor;

            List<SurfaceChoice> surfaces = WorkflowRepairCommands.ReadSurfaceChoices(document);
            string[] surfaceNames = new[] { "<Keep source elevations>" }
                .Concat(surfaces.Select(item => item.Name))
                .ToArray();
            List<string> layerNames = ReadLayerNames(document.Database);
            List<string> siteNames = ReadSiteNames(document);
            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Create Feature Lines (Fatal-Safe)",
                "Creates each feature line independently from a committed temporary clone. " +
                "Choose surface/elevation handling plus the output layer, ACI colour and Civil 3D Site. " +
                "Saved choices become the defaults for the next drawing/session.");
            settings.AddChoice(
                "Surface", "01 Elevation", "Surface", surfaceNames[0],
                "Keep source elevations or safely sample a Civil 3D surface after the feature line has been created and verified.",
                surfaceNames);
            settings.AddChoice(
                "Intermediate", "01 Elevation", "Intermediate surface points", "No",
                "For Civil 3D 2023 fatal-safety, surface elevations are sampled at existing feature-line points. Native intermediate TIN insertion is deliberately avoided.",
                new[] { "No", "Yes" });
            settings.AddChoice(
                "Layer", "02 Output", "Layer", "<Source layer>",
                "Use each source object's layer, choose an existing layer, or type a new layer name to create it.",
                new[] { "<Source layer>" }.Concat(layerNames));
            settings.AddChoice(
                "Color", "02 Output", "Colour (ACI)", "ByLayer",
                "ByLayer is recommended. You may also enter an AutoCAD colour index from 1 to 255.",
                new[] { "ByLayer", "1", "2", "3", "4", "5", "6", "7", "8", "9", "250" });
            settings.AddChoice(
                "Site", "02 Output", "Civil 3D Site", "<Siteless>",
                "Keep the feature lines siteless, choose an existing Site, or type a new Site name to create it.",
                new[] { "<Siteless>" }.Concat(siteNames));
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;

            SurfaceChoice selectedSurface = surfaces.FirstOrDefault(item =>
                string.Equals(item.Name, settings.Text("Surface"), StringComparison.OrdinalIgnoreCase));
            bool requestedIntermediate = string.Equals(
                settings.Text("Intermediate"), "Yes", StringComparison.OrdinalIgnoreCase);
            string outputLayer = string.IsNullOrWhiteSpace(settings.Text("Layer"))
                ? "<Source layer>"
                : settings.Text("Layer").Trim();
            int outputColor = ParseAciColor(settings.Text("Color"));
            string requestedSite = string.IsNullOrWhiteSpace(settings.Text("Site"))
                ? "<Siteless>"
                : settings.Text("Site").Trim();
            ObjectId outputSiteId;
            string siteError;
            if (!TryResolveOrCreateSite(
                    document,
                    requestedSite,
                    out outputSiteId,
                    out siteError))
            {
                editor.WriteMessage(
                    "\nCE_FLCREATE cancelled. Site could not be resolved/created: {0}",
                    siteError);
                return;
            }

            PromptSelectionResult selection = editor.SelectImplied();
            if (selection.Status != PromptStatus.OK || selection.Value == null || selection.Value.Count == 0)
            {
                selection = editor.GetSelection(new PromptSelectionOptions
                {
                    MessageForAdding = "\nSelect lines, arcs or polylines to convert to feature lines: ",
                    AllowDuplicates = false,
                    RejectObjectsFromNonCurrentSpace = true
                });
            }
            editor.SetImpliedSelection(new ObjectId[0]);
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;

            int created = 0;
            int skipped = 0;
            int failed = 0;
            int surfaceFailures = 0;
            using (DocumentLock documentLock = document.LockDocument())
            {
                foreach (ObjectId sourceId in selection.Value.GetObjectIds().Distinct())
                {
                    ObjectId featureLineId;
                    string error;
                    FeatureCreateState state = TryCreateFromProtectedClone(
                        document,
                        sourceId,
                        outputLayer,
                        outputColor,
                        outputSiteId,
                        out featureLineId,
                        out error);
                    if (state == FeatureCreateState.Skipped)
                    {
                        skipped++;
                        continue;
                    }
                    if (state != FeatureCreateState.Created)
                    {
                        failed++;
                        if (!string.IsNullOrWhiteSpace(error))
                            editor.WriteMessage("\nFeature-line source skipped safely: {0}", error);
                        continue;
                    }

                    created++;
                    if (selectedSurface != null)
                    {
                        string surfaceError;
                        if (!August21SurfaceSafety.TryApplyFeatureLineElevations(
                                document,
                                featureLineId,
                                selectedSurface.Name,
                                requestedIntermediate,
                                out surfaceError))
                        {
                            surfaceFailures++;
                            if (!string.IsNullOrWhiteSpace(surfaceError))
                                editor.WriteMessage("\nFeature line retained with source elevations; surface update skipped safely: {0}", surfaceError);
                        }
                    }
                }
            }

            try { editor.Regen(); AcApplication.UpdateScreen(); } catch { }
            editor.WriteMessage(
                "\nCE_FLCREATE complete (fatal-safe). Created={0}; unsupported/invalid={1}; failed={2}; surface updates skipped={3}; layer={4}; colour={5}; site={6}. Source objects retained.",
                created,
                skipped,
                failed,
                surfaceFailures,
                outputLayer,
                outputColor == 256 ? "ByLayer" : outputColor.ToString(CultureInfo.InvariantCulture),
                requestedSite);
        }

        internal static void RunPlatformFeatureLinesAtSlope(Document document)
        {
            if (document == null || document.Database == null) return;
            Editor editor = document.Editor;

            var referenceOptions = new PromptEntityOptions(
                "\nSelect the reference feature line for platform slope control: ");
            referenceOptions.SetRejectMessage("\nSelect a Civil 3D feature line.");
            referenceOptions.AddAllowedClass(typeof(CivilFeatureLine), false);
            PromptEntityResult referenceResult = editor.GetEntity(referenceOptions);
            if (referenceResult.Status != PromptStatus.OK) return;

            List<Point3d> controls = ReadFeatureLinePoints(
                document,
                referenceResult.ObjectId);
            if (controls.Count < 2)
            {
                editor.WriteMessage("\nCE_PLATFORMFEATURELINESLOPE cancelled. The reference feature line has fewer than two safe control points.");
                return;
            }

            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Platform Feature Lines at Slope (Fatal-Safe)",
                "Each selected platform polyline is read first, converted to a committed temporary 3D polyline, then passed independently to Civil 3D. Source cadastral/platform polylines are retained.");
            settings.AddChoice("Mode", "01 Grade", "Slope rule", "Fixed slope",
                "Fixed slope forces the calculated grade. Minimum slope keeps an existing vertex only when it already satisfies the requested minimum grade.",
                new[] { "Fixed slope", "Minimum slope" });
            settings.AddDouble("Slope", "01 Grade", "Slope (%)", 2.0,
                "Positive design slope magnitude in percent.");
            settings.AddChoice("Direction", "02 Direction", "Slope direction", "Fall away from reference",
                "Fall away lowers the target with distance; Fall toward raises it.",
                new[] { "Fall away from reference", "Fall toward reference" });
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;

            PromptSelectionResult selection = editor.SelectImplied();
            if (selection.Status != PromptStatus.OK || selection.Value == null || selection.Value.Count == 0)
            {
                selection = editor.GetSelection(
                    new PromptSelectionOptions
                    {
                        MessageForAdding = "\nSelect multiple platform polylines: ",
                        AllowDuplicates = false,
                        RejectObjectsFromNonCurrentSpace = true
                    },
                    new SelectionFilter(new[]
                    {
                        new TypedValue((int)DxfCode.Start, "LWPOLYLINE")
                    }));
            }
            editor.SetImpliedSelection(new ObjectId[0]);
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;

            double slope = Math.Abs(settings.Double("Slope", 2.0)) / 100.0;
            bool minimum = string.Equals(
                settings.Text("Mode"), "Minimum slope", StringComparison.OrdinalIgnoreCase);
            bool away = string.Equals(
                settings.Text("Direction"), "Fall away from reference", StringComparison.OrdinalIgnoreCase);
            int created = 0;
            int skipped = 0;
            int failed = 0;

            using (DocumentLock documentLock = document.LockDocument())
            {
                foreach (ObjectId sourceId in selection.Value.GetObjectIds().Distinct())
                {
                    if (sourceId == referenceResult.ObjectId) { skipped++; continue; }
                    ObjectId tempId;
                    ObjectId sourceLayer;
                    string prepareError;
                    if (!TryCreateSlopeTemporary(
                            document,
                            sourceId,
                            controls,
                            slope,
                            minimum,
                            away,
                            out tempId,
                            out sourceLayer,
                            out prepareError))
                    {
                        skipped++;
                        if (!string.IsNullOrWhiteSpace(prepareError))
                            editor.WriteMessage("\nPlatform source skipped safely: {0}", prepareError);
                        continue;
                    }

                    ObjectId featureLineId;
                    string createError;
                    if (!TryCreateFromCommittedTemporary(
                            document,
                            tempId,
                            sourceLayer,
                            ObjectId.Null,
                            256,
                            out featureLineId,
                            out createError))
                    {
                        failed++;
                        Cleanup(document, tempId, featureLineId);
                        if (!string.IsNullOrWhiteSpace(createError))
                            editor.WriteMessage("\nPlatform feature line failed safely: {0}", createError);
                        continue;
                    }

                    Cleanup(document, tempId, ObjectId.Null);
                    created++;
                }
            }

            try { editor.Regen(); AcApplication.UpdateScreen(); } catch { }
            editor.WriteMessage(
                "\nCE_PLATFORMFEATURELINESLOPE complete (fatal-safe). Created={0}; skipped={1}; failed={2}; source polylines retained; slope={3:0.###}% {4}.",
                created, skipped, failed, slope * 100.0, away ? "away" : "toward");
        }

        private static FeatureCreateState TryCreateFromProtectedClone(
            Document document,
            ObjectId sourceId,
            string outputLayerName,
            int outputColor,
            ObjectId siteId,
            out ObjectId featureLineId,
            out string error)
        {
            featureLineId = ObjectId.Null;
            error = string.Empty;
            ObjectId temporaryId = ObjectId.Null;
            ObjectId sourceLayer = ObjectId.Null;
            try
            {
                using (Transaction transaction =
                    document.Database.TransactionManager.StartTransaction())
                {
                    if (sourceId.IsNull || !sourceId.IsValid || sourceId.IsErased)
                        return FeatureCreateState.Skipped;
                    Entity source = transaction.GetObject(
                        sourceId,
                        OpenMode.ForRead,
                        false) as Entity;
                    if (!Supported(source)) return FeatureCreateState.Skipped;
                    LayerTableRecord layer = transaction.GetObject(
                        source.LayerId,
                        OpenMode.ForRead,
                        false) as LayerTableRecord;
                    if (layer != null && layer.IsLocked) return FeatureCreateState.Skipped;
                    if (!SafeCurve(source as Curve, out error))
                        return FeatureCreateState.Skipped;

                    Entity temporary = source.Clone() as Entity;
                    if (temporary == null)
                    {
                        error = "The source could not be cloned to an isolated temporary object.";
                        return FeatureCreateState.Failed;
                    }
                    BlockTableRecord space = transaction.GetObject(
                        document.Database.CurrentSpaceId,
                        OpenMode.ForWrite,
                        false) as BlockTableRecord;
                    if (space == null)
                    {
                        temporary.Dispose();
                        error = "The current drawing space is unavailable.";
                        return FeatureCreateState.Failed;
                    }
                    temporary.SetDatabaseDefaults(document.Database);
                    temporary.LayerId = source.LayerId;
                    temporaryId = space.AppendEntity(temporary);
                    transaction.AddNewlyCreatedDBObject(temporary, true);
                    sourceLayer = ResolveOutputLayer(
                        document.Database,
                        transaction,
                        outputLayerName,
                        source.LayerId);
                    transaction.Commit();
                }

                if (!TryCreateFromCommittedTemporary(
                        document,
                        temporaryId,
                        sourceLayer,
                        siteId,
                        outputColor,
                        out featureLineId,
                        out error))
                {
                    Cleanup(document, temporaryId, featureLineId);
                    return FeatureCreateState.Failed;
                }
                Cleanup(document, temporaryId, ObjectId.Null);
                return FeatureCreateState.Created;
            }
            catch (System.Exception exception)
            {
                error = exception.Message;
                Cleanup(document, temporaryId, featureLineId);
                return FeatureCreateState.Failed;
            }
        }

        private static bool TryCreateFromCommittedTemporary(
            Document document,
            ObjectId temporaryId,
            ObjectId layerId,
            ObjectId siteId,
            int colorIndex,
            out ObjectId featureLineId,
            out string error)
        {
            featureLineId = ObjectId.Null;
            error = string.Empty;
            try
            {
                using (Transaction transaction =
                    document.Database.TransactionManager.StartTransaction())
                {
                    Entity temporary = transaction.GetObject(
                        temporaryId,
                        OpenMode.ForRead,
                        false) as Entity;
                    if (temporary == null || temporary.IsErased)
                    {
                        error = "The committed temporary source is unavailable.";
                        return false;
                    }
                    if (!SafeCurve(temporary as Curve, out error)) return false;

                    if (siteId.IsNull)
                        featureLineId = CivilFeatureLine.Create(string.Empty, temporaryId);
                    else
                        featureLineId = CivilFeatureLine.Create(
                            string.Empty,
                            temporaryId,
                            siteId);
                    if (featureLineId.IsNull || !featureLineId.IsValid || featureLineId.IsErased)
                    {
                        error = "Civil 3D returned no valid feature-line ObjectId.";
                        return false;
                    }
                    CivilFeatureLine featureLine = transaction.GetObject(
                        featureLineId,
                        OpenMode.ForWrite,
                        false) as CivilFeatureLine;
                    if (featureLine == null || featureLine.GetType() != typeof(CivilFeatureLine))
                    {
                        error = "Civil 3D did not return an ordinary feature line.";
                        return false;
                    }
                    Point3dCollection points = featureLine.GetPoints(
                        FeatureLinePointType.AllPoints);
                    if (points == null || points.Count < 2 ||
                        points.Cast<Point3d>().Any(point => !Finite(point)))
                    {
                        error = "The created feature line failed point verification.";
                        return false;
                    }
                    if (!layerId.IsNull) featureLine.LayerId = layerId;
                    featureLine.ColorIndex =
                        colorIndex >= 1 && colorIndex <= 255
                            ? colorIndex
                            : 256;
                    try { featureLine.RecordGraphicsModified(true); } catch { }
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

        private static bool TryCreateSlopeTemporary(
            Document document,
            ObjectId sourceId,
            IList<Point3d> controls,
            double slope,
            bool minimum,
            bool away,
            out ObjectId temporaryId,
            out ObjectId layerId,
            out string error)
        {
            temporaryId = ObjectId.Null;
            layerId = ObjectId.Null;
            error = string.Empty;
            try
            {
                var output = new Point3dCollection();
                bool closed;
                using (Transaction read = document.Database.TransactionManager.StartTransaction())
                {
                    Polyline source = read.GetObject(
                        sourceId,
                        OpenMode.ForRead,
                        false) as Polyline;
                    if (source == null || source.IsErased || source.NumberOfVertices < 2)
                    {
                        error = "Select a lightweight platform polyline with at least two vertices.";
                        return false;
                    }
                    LayerTableRecord layer = read.GetObject(
                        source.LayerId,
                        OpenMode.ForRead,
                        false) as LayerTableRecord;
                    if (layer != null && layer.IsLocked)
                    {
                        error = "The source polyline is on a locked layer.";
                        return false;
                    }
                    layerId = source.LayerId;
                    closed = source.Closed;
                    for (int index = 0; index < source.NumberOfVertices; index++)
                    {
                        Point3d point = source.GetPoint3dAt(index);
                        if (!Finite(point))
                        {
                            error = "The source polyline contains a non-finite vertex.";
                            return false;
                        }
                        double referenceZ;
                        double planDistance;
                        ClosestReference(point, controls, out referenceZ, out planDistance);
                        double requiredZ = away
                            ? referenceZ - planDistance * slope
                            : referenceZ + planDistance * slope;
                        double z = requiredZ;
                        if (minimum)
                        {
                            z = away
                                ? (point.Z <= requiredZ ? point.Z : requiredZ)
                                : (point.Z >= requiredZ ? point.Z : requiredZ);
                        }
                        if (double.IsNaN(z) || double.IsInfinity(z))
                        {
                            error = "The calculated platform elevation is invalid.";
                            return false;
                        }
                        output.Add(new Point3d(point.X, point.Y, z));
                    }
                }

                if (output.Count < 2 ||
                    Enumerable.Range(1, output.Count - 1)
                        .All(index => output[index].DistanceTo(output[0]) <= Tolerance))
                {
                    error = "The source polyline collapses to one point.";
                    return false;
                }

                using (Transaction write = document.Database.TransactionManager.StartTransaction())
                {
                    BlockTableRecord space = write.GetObject(
                        document.Database.CurrentSpaceId,
                        OpenMode.ForWrite,
                        false) as BlockTableRecord;
                    if (space == null)
                    {
                        error = "The current drawing space is unavailable.";
                        return false;
                    }
                    var temporary = new Polyline3d(
                        Poly3dType.SimplePoly,
                        output,
                        closed);
                    temporary.SetDatabaseDefaults(document.Database);
                    if (!layerId.IsNull) temporary.LayerId = layerId;
                    temporaryId = space.AppendEntity(temporary);
                    write.AddNewlyCreatedDBObject(temporary, true);
                    write.Commit();
                }
                return true;
            }
            catch (System.Exception exception)
            {
                error = exception.Message;
                Cleanup(document, temporaryId, ObjectId.Null);
                return false;
            }
        }

        private static List<string> ReadLayerNames(
            Database database)
        {
            var result = new List<string>();
            if (database == null) return result;
            try
            {
                using (Transaction transaction =
                    database.TransactionManager.StartTransaction())
                {
                    LayerTable table = transaction.GetObject(
                        database.LayerTableId,
                        OpenMode.ForRead,
                        false) as LayerTable;
                    if (table == null) return result;
                    foreach (ObjectId id in table)
                    {
                        LayerTableRecord layer = transaction.GetObject(
                            id,
                            OpenMode.ForRead,
                            false) as LayerTableRecord;
                        if (layer != null &&
                            !string.IsNullOrWhiteSpace(layer.Name))
                            result.Add(layer.Name);
                    }
                }
            }
            catch { }
            return result
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(item => item, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private static ObjectId ResolveOutputLayer(
            Database database,
            Transaction transaction,
            string requested,
            ObjectId sourceLayerId)
        {
            if (database == null || transaction == null)
                return sourceLayerId;
            if (string.IsNullOrWhiteSpace(requested) ||
                string.Equals(
                    requested,
                    "<Source layer>",
                    StringComparison.OrdinalIgnoreCase))
                return sourceLayerId;

            string name = requested.Trim();
            LayerTable table = transaction.GetObject(
                database.LayerTableId,
                OpenMode.ForRead,
                false) as LayerTable;
            if (table == null)
                return sourceLayerId;
            if (table.Has(name))
                return table[name];

            table.UpgradeOpen();
            var layer = new LayerTableRecord { Name = name };
            ObjectId id = table.Add(layer);
            transaction.AddNewlyCreatedDBObject(layer, true);
            return id;
        }

        private static int ParseAciColor(string value)
        {
            if (string.IsNullOrWhiteSpace(value) ||
                string.Equals(
                    value.Trim(),
                    "ByLayer",
                    StringComparison.OrdinalIgnoreCase))
                return 256;
            int parsed;
            return int.TryParse(
                    value.Trim(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out parsed) &&
                parsed >= 1 &&
                parsed <= 255
                    ? parsed
                    : 256;
        }

        private static List<string> ReadSiteNames(
            Document document)
        {
            var result = new List<string>();
            if (document == null) return result;
            Autodesk.Civil.ApplicationServices.CivilDocument civil =
                Autodesk.Civil.ApplicationServices.CivilApplication.ActiveDocument;
            if (civil == null) return result;

            try
            {
                using (Transaction transaction =
                    document.Database.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId id in civil.GetSiteIds())
                    {
                        Autodesk.Civil.DatabaseServices.Site site =
                            transaction.GetObject(
                                id,
                                OpenMode.ForRead,
                                false) as Autodesk.Civil.DatabaseServices.Site;
                        if (site != null &&
                            !string.IsNullOrWhiteSpace(site.Name))
                            result.Add(site.Name);
                    }
                }
            }
            catch { }

            return result
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(item => item, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private static bool TryResolveOrCreateSite(
            Document document,
            string requested,
            out ObjectId siteId,
            out string error)
        {
            siteId = ObjectId.Null;
            error = string.Empty;
            if (document == null)
            {
                error = "No active document.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(requested) ||
                string.Equals(
                    requested,
                    "<Siteless>",
                    StringComparison.OrdinalIgnoreCase))
                return true;

            Autodesk.Civil.ApplicationServices.CivilDocument civil =
                Autodesk.Civil.ApplicationServices.CivilApplication.ActiveDocument;
            if (civil == null)
            {
                error = "No active Civil 3D document.";
                return false;
            }

            try
            {
                using (Transaction transaction =
                    document.Database.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId id in civil.GetSiteIds())
                    {
                        Autodesk.Civil.DatabaseServices.Site site =
                            transaction.GetObject(
                                id,
                                OpenMode.ForRead,
                                false) as Autodesk.Civil.DatabaseServices.Site;
                        if (site != null &&
                            string.Equals(
                                site.Name,
                                requested,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            siteId = id;
                            return true;
                        }
                    }
                }

                siteId = Autodesk.Civil.DatabaseServices.Site.Create(
                    civil,
                    requested.Trim());
                if (siteId.IsNull)
                {
                    error = "Civil 3D returned no Site ObjectId.";
                    return false;
                }
                return true;
            }
            catch (System.Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }

        private static List<Point3d> ReadFeatureLinePoints(
            Document document,
            ObjectId featureLineId)
        {
            var result = new List<Point3d>();
            try
            {
                using (Transaction transaction =
                    document.Database.TransactionManager.StartTransaction())
                {
                    CivilFeatureLine featureLine = transaction.GetObject(
                        featureLineId,
                        OpenMode.ForRead,
                        false) as CivilFeatureLine;
                    if (featureLine == null || featureLine.IsErased) return result;
                    Point3dCollection points = featureLine.GetPoints(
                        FeatureLinePointType.AllPoints);
                    if (points == null) return result;
                    result.AddRange(points.Cast<Point3d>().Where(Finite));
                }
            }
            catch { }
            return result;
        }

        private static void Cleanup(
            Document document,
            ObjectId temporaryId,
            ObjectId failedFeatureLineId)
        {
            if (document == null || document.Database == null) return;
            try
            {
                using (Transaction transaction =
                    document.Database.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId id in new[] { failedFeatureLineId, temporaryId })
                    {
                        if (id.IsNull || !id.IsValid || id.IsErased) continue;
                        try
                        {
                            Entity entity = transaction.GetObject(
                                id,
                                OpenMode.ForWrite,
                                false) as Entity;
                            if (entity != null && !entity.IsErased) entity.Erase();
                        }
                        catch { }
                    }
                    transaction.Commit();
                }
            }
            catch { }
        }

        private static bool Supported(Entity entity)
        {
            return entity is Line ||
                   entity is Arc ||
                   entity is Polyline ||
                   entity is Polyline2d ||
                   entity is Polyline3d;
        }

        private static bool SafeCurve(Curve curve, out string error)
        {
            error = string.Empty;
            if (curve == null)
            {
                error = "The selected object is not a supported curve.";
                return false;
            }
            try
            {
                Extents3d extents = curve.GeometricExtents;
                if (!Finite(extents.MinPoint) || !Finite(extents.MaxPoint))
                {
                    error = "The curve has invalid geometric extents.";
                    return false;
                }
                double length = curve.GetDistanceAtParameter(curve.EndParam);
                if (double.IsNaN(length) || double.IsInfinity(length) || length <= Tolerance)
                {
                    error = "The curve has zero/invalid length.";
                    return false;
                }
                Polyline polyline = curve as Polyline;
                if (polyline != null)
                {
                    if (polyline.NumberOfVertices < 2)
                    {
                        error = "The polyline has fewer than two vertices.";
                        return false;
                    }
                    for (int index = 0; index < polyline.NumberOfVertices; index++)
                    {
                        if (!Finite(polyline.GetPoint3dAt(index)) ||
                            double.IsNaN(polyline.GetBulgeAt(index)) ||
                            double.IsInfinity(polyline.GetBulgeAt(index)))
                        {
                            error = "The polyline contains an invalid vertex/bulge.";
                            return false;
                        }
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

        private static void ClosestReference(
            Point3d point,
            IList<Point3d> controls,
            out double z,
            out double distance)
        {
            z = controls[0].Z;
            distance = double.MaxValue;
            for (int index = 0; index < controls.Count - 1; index++)
            {
                Point3d a = controls[index];
                Point3d b = controls[index + 1];
                double vx = b.X - a.X;
                double vy = b.Y - a.Y;
                double length2 = vx * vx + vy * vy;
                double t = length2 <= 1e-12
                    ? 0.0
                    : ((point.X - a.X) * vx + (point.Y - a.Y) * vy) / length2;
                t = Math.Max(0.0, Math.Min(1.0, t));
                double x = a.X + vx * t;
                double y = a.Y + vy * t;
                double dx = point.X - x;
                double dy = point.Y - y;
                double candidate = Math.Sqrt(dx * dx + dy * dy);
                if (candidate < distance)
                {
                    distance = candidate;
                    z = a.Z + (b.Z - a.Z) * t;
                }
            }
            if (double.IsInfinity(distance) || distance == double.MaxValue)
                distance = 0.0;
        }

        private static bool Finite(Point3d point)
        {
            return !double.IsNaN(point.X) && !double.IsInfinity(point.X) &&
                   !double.IsNaN(point.Y) && !double.IsInfinity(point.Y) &&
                   !double.IsNaN(point.Z) && !double.IsInfinity(point.Z);
        }

        private enum FeatureCreateState
        {
            Skipped,
            Created,
            Failed
        }
    }
}
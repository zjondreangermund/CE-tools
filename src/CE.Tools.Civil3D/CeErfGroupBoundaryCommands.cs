using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.CeErfGroupBoundaryCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Creates one closed outer perimeter per connected erf block from selected erf polylines.
    /// Road-reserve-separated blocks remain separate. Adjacent erven are combined with AutoCAD ACIS Region Boolean Unite so shared
    /// internal boundaries are removed. Original cadastral entities are never edited.
    /// </summary>
    public sealed class CeErfGroupBoundaryCommands
    {
        private const double PointTolerance = 1e-6;
        private const string PreviewLayerName = "CE-ERF-BOUNDARY-PREVIEW";

        [CommandMethod("CE_TOOLS", "CE_CREATEERFGROUPBOUNDARIES",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void CreateErfGroupBoundaries()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            ProductionSettingsDialogModel settings = BuildSettings();
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;

            double offset;
            if (!ProductionSettingsDialogModel.TryDouble(settings.Text("Offset"), out offset) ||
                double.IsNaN(offset) || double.IsInfinity(offset) || offset < 0.0)
            {
                DisciplineWorkflowDialogs.Confirm("CE Tools - Invalid offset",
                    "Boundary offset must be zero or a positive drawing-unit value.");
                return;
            }

            string outputLayer = settings.Text("Layer");
            if (string.IsNullOrWhiteSpace(outputLayer)) outputLayer = "CE-ERF-GROUP-BOUNDARY";
            if (outputLayer.IndexOfAny(new[] { '<', '>', '/', '\\', '"', ':', ';', '?', '*', '|', '=', ',' }) >= 0)
            {
                DisciplineWorkflowDialogs.Confirm("CE Tools - Invalid layer",
                    "The output layer name contains characters that are not allowed in an AutoCAD layer name.");
                return;
            }

            int colorIndex = ParseColorIndex(settings.Text("Color"));
            string selectionMode = settings.Text("SelectionMode");
            bool acceptCoincidentOpen = string.Equals(
                settings.Text("OpenClosure"),
                "Also accept open polylines with coincident endpoints",
                StringComparison.OrdinalIgnoreCase);
            ObjectId[] selectedIds = SelectErfPolylines(document.Editor, selectionMode, settings.Text("SourceLayer"));
            if (selectedIds == null || selectedIds.Length == 0)
            {
                document.Editor.WriteMessage("\nCE_CREATEERFGROUPBOUNDARIES cancelled. No erf polylines were selected.");
                return;
            }

            var createdPreviewIds = new List<ObjectId>();
            int acceptedSources = 0;
            int skippedSources = 0;
            int failedBooleanUnions = 0;
            int outputLoops = 0;
            int mergedErfCount = 0;
            int fallbackTraceAttempts = 0;
            int fallbackTraceCandidates = 0;
            int fallbackTraceRecoveries = 0;
            int fallbackTraceRejected = 0;
            int incompleteLoopRegions = 0;
            double outputElevation = 0.0;
            Vector3d outputNormal = Vector3d.ZAxis;
            bool outputPlaneSet = false;

            try
            {
                using (DocumentLock documentLock = document.LockDocument())
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    var regions = new List<Region>();
                    foreach (ObjectId id in selectedIds.Distinct())
                    {
                        Polyline source = null;
                        try { source = transaction.GetObject(id, OpenMode.ForRead, false) as Polyline; }
                        catch { }
                        bool sourceCanBeClosed = source != null && source.Closed;
                        if (source != null && !source.Closed && acceptCoincidentOpen &&
                            source.NumberOfVertices >= 4)
                        {
                            Point3d firstPoint = source.GetPoint3dAt(0);
                            Point3d lastPoint = source.GetPoint3dAt(source.NumberOfVertices - 1);
                            sourceCanBeClosed = firstPoint.DistanceTo(lastPoint) <= PointTolerance;
                        }

                        if (source == null || !sourceCanBeClosed || source.NumberOfVertices < 3 ||
                            source.Normal.DotProduct(Vector3d.ZAxis) < 0.999999)
                        {
                            // Erf cadastral boundaries are expected to be planar in
                            // the drawing XY plane. Do not silently rotate/flatten
                            // polylines from a different plane.
                            skippedSources++;
                            continue;
                        }

                        if (!outputPlaneSet)
                        {
                            outputElevation = source.Elevation;
                            outputNormal = source.Normal;
                            outputPlaneSet = true;
                        }
                        else if (Math.Abs(Math.Abs(source.Normal.DotProduct(outputNormal)) - 1.0) > 1e-6 ||
                                 Math.Abs(source.Elevation - outputElevation) > 1e-4)
                        {
                            // A single output polyline set must be coplanar. Do not
                            // silently flatten or distort mixed-plane cadastral data.
                            skippedSources++;
                            continue;
                        }

                        try
                        {
                            // Region.CreateFromCurves requires a truly closed curve.
                            // For an open polyline whose endpoints coincide, prepare a
                            // temporary closed copy and remove the duplicate last vertex.
                            // The source entity in the drawing is never opened for write
                            // and is never modified.
                            Polyline regionInput = source;
                            Polyline temporaryClosedCopy = null;
                            try
                            {
                                if (!source.Closed)
                                {
                                    temporaryClosedCopy = source.Clone() as Polyline;
                                    if (temporaryClosedCopy == null ||
                                        temporaryClosedCopy.NumberOfVertices < 4 ||
                                        temporaryClosedCopy.GetPoint3dAt(0).DistanceTo(
                                            temporaryClosedCopy.GetPoint3dAt(
                                                temporaryClosedCopy.NumberOfVertices - 1)) > PointTolerance)
                                    {
                                        skippedSources++;
                                        continue;
                                    }

                                    temporaryClosedCopy.RemoveVertexAt(
                                        temporaryClosedCopy.NumberOfVertices - 1);
                                    temporaryClosedCopy.Closed = true;
                                    regionInput = temporaryClosedCopy;
                                }

                                var curves = new DBObjectCollection();
                                curves.Add(regionInput);
                                DBObjectCollection made = Region.CreateFromCurves(curves);
                                Region region = made.Cast<DBObject>().OfType<Region>().FirstOrDefault();
                                foreach (DBObject item in made)
                                    if (!ReferenceEquals(item, region)) item.Dispose();

                                if (region == null)
                                {
                                    skippedSources++;
                                    continue;
                                }
                                regions.Add(region);
                                acceptedSources++;
                            }
                            finally
                            {
                                if (temporaryClosedCopy != null)
                                    temporaryClosedCopy.Dispose();
                            }
                        }
                        catch
                        {
                            skippedSources++;
                        }
                    }

                    if (regions.Count == 0)
                    {
                        foreach (Region region in regions) try { region.Dispose(); } catch { }
                        document.Editor.WriteMessage(
                            "\nCE_CREATEERFGROUPBOUNDARIES stopped. No valid closed, planar lightweight erf polylines could be converted to regions. Source geometry was not changed.");
                        return;
                    }

                    string groupingMode = settings.Text("Grouping");
                    bool separateRoadReserveGroups = string.Equals(groupingMode,
                        "Combine touching erven and keep road-reserve-separated groups separate",
                        StringComparison.OrdinalIgnoreCase);
                    bool combineTouching = separateRoadReserveGroups ||
                        string.Equals(groupingMode, "Combine touching erven into connected groups",
                            StringComparison.OrdinalIgnoreCase);

                    // Process each spatially connected erf block independently.
                    // A disconnected skew block cannot affect another block's union
                    // or perimeter tracing during a multi-block selection.
                    List<List<Region>> regionComponents = BuildRegionComponents(regions, PointTolerance);
                    var previewPolylines = new List<Polyline>();

                    foreach (List<Region> component in regionComponents)
                    {
                        if (combineTouching)
                            MergeTouchingRegions(component, ref failedBooleanUnions, ref mergedErfCount, true);

                        foreach (Region region in component)
                        {
                            DBObjectCollection exploded = new DBObjectCollection();
                            try { region.Explode(exploded); }
                            catch
                            {
                                DisposeObjects(exploded);
                                continue;
                            }

                            double expectedRegionArea = 0.0;
                            try { expectedRegionArea = Math.Abs(region.Area); } catch { }
                            List<Polyline> loops = BuildClosedPolylines(
                                exploded, outputNormal, outputElevation, expectedRegionArea,
                                ref fallbackTraceAttempts, ref fallbackTraceCandidates,
                                ref fallbackTraceRecoveries, ref fallbackTraceRejected,
                                ref incompleteLoopRegions);
                            DisposeObjects(exploded);

                            foreach (Polyline loop in loops)
                            {
                                Polyline finalLoop = ApplyOutwardOffset(loop, offset);
                                if (finalLoop == null)
                                {
                                    loop.Dispose();
                                    continue;
                                }
                                if (!ReferenceEquals(finalLoop, loop)) loop.Dispose();
                                previewPolylines.Add(finalLoop);
                            }
                        }

                        foreach (Region region in component)
                            try { region.Dispose(); } catch { }
                    }

                    if (previewPolylines.Count == 0)
                    {
                        foreach (Polyline polyline in previewPolylines) try { polyline.Dispose(); } catch { }
                        document.Editor.WriteMessage(
                            "\nCE_CREATEERFGROUPBOUNDARIES stopped. The selected geometry did not produce any closed outer perimeter loops. Original erf lines were preserved.");
                        return;
                    }

                    EnsureLayer(document.Database, transaction, PreviewLayerName, 4);
                    BlockTableRecord space = (BlockTableRecord)transaction.GetObject(
                        document.Database.CurrentSpaceId, OpenMode.ForWrite);
                    foreach (Polyline polyline in previewPolylines)
                    {
                        polyline.SetDatabaseDefaults(document.Database);
                        polyline.Layer = PreviewLayerName;
                        polyline.Color = Color.FromColorIndex(ColorMethod.ByAci, 4);
                        space.AppendEntity(polyline);
                        transaction.AddNewlyCreatedDBObject(polyline, true);
                        createdPreviewIds.Add(polyline.ObjectId);
                    }
                    outputLoops = createdPreviewIds.Count;
                    transaction.Commit();
                }

                document.Editor.Regen();
                string previewMessage =
                    "Preview created: " + outputLoops.ToString(CultureInfo.InvariantCulture) +
                    " closed boundary loop(s) from " + acceptedSources.ToString(CultureInfo.InvariantCulture) +
                    " valid erf polyline(s).\n\n" +
                    "Skipped source polylines: " + skippedSources.ToString(CultureInfo.InvariantCulture) + "\n" +
                    "Successful region unions: " + mergedErfCount.ToString(CultureInfo.InvariantCulture) + "\n" +
                    "Failed region-union attempts: " + failedBooleanUnions.ToString(CultureInfo.InvariantCulture) + "\n" +
                    "Fallback trace attempts: " + fallbackTraceAttempts.ToString(CultureInfo.InvariantCulture) + "\n" +
                    "Incomplete greedy traces: " + incompleteLoopRegions.ToString(CultureInfo.InvariantCulture) + "\n" +
                    "Fallback candidates: " + fallbackTraceCandidates.ToString(CultureInfo.InvariantCulture) + "\n" +
                    "Fallback trace recoveries: " + fallbackTraceRecoveries.ToString(CultureInfo.InvariantCulture) + "\n" +
                    "Fallback candidates rejected: " + fallbackTraceRejected.ToString(CultureInfo.InvariantCulture) + "\n" +
                    "Boundary offset: " + offset.ToString("0.###", CultureInfo.InvariantCulture) + "\n\n" +
                    "Keep this preview and create the final boundaries?";
                bool keep = DisciplineWorkflowDialogs.Confirm(
                    "CE Tools - Erf Group Boundary Preview", previewMessage);

                using (DocumentLock documentLock = document.LockDocument())
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    LayerTableRecord targetLayer = keep
                        ? EnsureLayer(document.Database, transaction, outputLayer, colorIndex)
                        : null;
                    foreach (ObjectId id in createdPreviewIds)
                    {
                        Polyline boundary = null;
                        try { boundary = transaction.GetObject(id, OpenMode.ForWrite, false) as Polyline; }
                        catch { }
                        if (boundary == null) continue;

                        if (keep)
                        {
                            boundary.LayerId = targetLayer.ObjectId;
                            boundary.Color = colorIndex == 256
                                ? Color.FromColorIndex(ColorMethod.ByLayer, 256)
                                : Color.FromColorIndex(ColorMethod.ByAci, (short)colorIndex);
                        }
                        else
                        {
                            boundary.Erase();
                        }
                    }

                    RemoveLayerIfEmpty(document.Database, transaction, PreviewLayerName);
                    transaction.Commit();
                }

                document.Editor.Regen();
                if (!keep)
                {
                    document.Editor.WriteMessage(
                        "\nCE_CREATEERFGROUPBOUNDARIES cancelled after preview. Temporary preview entities were removed; original erf boundaries remain unchanged.");
                    return;
                }

                try { document.Editor.SetImpliedSelection(createdPreviewIds.ToArray()); } catch { }
                document.Editor.WriteMessage(
                    "\nCE_CREATEERFGROUPBOUNDARIES complete. Source erven converted={0}; successful region unions={1}; output closed boundary loops={2}; skipped source polylines={3}; failed union attempts={4}; fallback trace attempts={5}; incomplete greedy traces={6}; fallback candidates={7}; fallback recoveries={8}; rejected candidates={9}; output layer={10}; offset={11}. Original erf lines were preserved.",
                    acceptedSources, mergedErfCount, outputLoops, skippedSources, failedBooleanUnions,
                    fallbackTraceAttempts, incompleteLoopRegions, fallbackTraceCandidates,
                    fallbackTraceRecoveries, fallbackTraceRejected, outputLayer,
                    offset.ToString("0.###", CultureInfo.InvariantCulture));
            }
            catch (System.Exception exception)
            {
                // Remove any preview entities left behind by a failed operation.
                try
                {
                    using (DocumentLock documentLock = document.LockDocument())
                    using (Transaction cleanup = document.Database.TransactionManager.StartTransaction())
                    {
                        foreach (ObjectId id in createdPreviewIds)
                        {
                            try
                            {
                                Entity entity = cleanup.GetObject(id, OpenMode.ForWrite, false) as Entity;
                                if (entity != null && !entity.IsErased) entity.Erase();
                            }
                            catch { }
                        }
                        RemoveLayerIfEmpty(document.Database, cleanup, PreviewLayerName);
                        cleanup.Commit();
                    }
                }
                catch { }

                document.Editor.WriteMessage(
                    "\nCE_CREATEERFGROUPBOUNDARIES failed safely: {0}. Original erf polylines were not modified.",
                    exception.Message);
            }
        }


        [CommandMethod("CE_TOOLS", "CE_CHECKERFGEOMETRY",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void CheckErfGeometry()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            Editor editor = document.Editor;
            var filter = new SelectionFilter(new[]
            {
                new TypedValue((int)DxfCode.Start, "LWPOLYLINE")
            });
            PromptSelectionResult selection = editor.GetSelection(
                new PromptSelectionOptions
                {
                    MessageForAdding = "\nSelect all erf polylines to diagnose (window/crossing is supported): ",
                    MessageForRemoval = "\nRemove erf polylines from selection: ",
                    AllowDuplicates = false,
                    RejectObjectsFromNonCurrentSpace = true
                }, filter);

            if (selection.Status != PromptStatus.OK || selection.Value == null)
            {
                editor.WriteMessage("\nCE_CHECKERFGEOMETRY cancelled. No source geometry was changed.");
                return;
            }

            int selectedCount = selection.Value.Count;
            int validCount = 0;
            int openCount = 0;
            int nonPlanarCount = 0;
            int invalidAreaCount = 0;
            int zeroLengthSegments = 0;
            int duplicateSegments = 0;
            int sharedVertices = 0;
            int highDegreeVertices = 0;
            int failedRegionConversions = 0;
            int successfulUnions = 0;
            int failedUnions = 0;
            int outputLoops = 0;
            int fallbackAttempts = 0;
            int fallbackCandidates = 0;
            int fallbackRecoveries = 0;
            int fallbackRejected = 0;
            int incompleteTraces = 0;
            int spatialComponentCount = 0;

            var segmentUse = new Dictionary<string, int>(StringComparer.Ordinal);
            var vertexUse = new Dictionary<string, int>(StringComparer.Ordinal);
            var regionHandles = new Dictionary<Region, string>();
            var problemReports = new List<string>();
            var regions = new List<Region>();

            Func<Point3d, string> pointKey = delegate(Point3d point)
            {
                long x = (long)Math.Round(point.X / PointTolerance);
                long y = (long)Math.Round(point.Y / PointTolerance);
                return x.ToString(CultureInfo.InvariantCulture) + ":" +
                       y.ToString(CultureInfo.InvariantCulture);
            };

            Func<Point3d, Point3d, string> segmentKey = delegate(Point3d a, Point3d b)
            {
                string ka = pointKey(a);
                string kb = pointKey(b);
                return string.CompareOrdinal(ka, kb) <= 0 ? ka + "|" + kb : kb + "|" + ka;
            };

            try
            {
                using (DocumentLock documentLock = document.LockDocument())
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId id in selection.Value.GetObjectIds().Distinct())
                    {
                        Polyline source = null;
                        try { source = transaction.GetObject(id, OpenMode.ForRead, false) as Polyline; }
                        catch { }
                        if (source == null) continue;

                        string handle = source.Handle.ToString();
                        if (!source.Closed)
                        {
                            openCount++;
                            problemReports.Add("OPEN POLYLINE handle=" + handle);
                            continue;
                        }
                        if (source.NumberOfVertices < 3)
                        {
                            invalidAreaCount++;
                            problemReports.Add("TOO FEW VERTICES handle=" + handle);
                            continue;
                        }
                        if (source.Normal.DotProduct(Vector3d.ZAxis) < 0.999999)
                        {
                            nonPlanarCount++;
                            problemReports.Add("NON-XY PLANE handle=" + handle);
                            continue;
                        }

                        double area = 0.0;
                        try { area = Math.Abs(source.Area); } catch { }
                        if (area <= PointTolerance * PointTolerance)
                        {
                            invalidAreaCount++;
                            problemReports.Add("ZERO/INVALID AREA handle=" + handle);
                            continue;
                        }

                        validCount++;
                        for (int i = 0; i < source.NumberOfVertices; i++)
                        {
                            Point3d vertex = source.GetPoint3dAt(i);
                            string vk = pointKey(vertex);
                            int count;
                            vertexUse[vk] = vertexUse.TryGetValue(vk, out count) ? count + 1 : 1;

                            Point3d next = source.GetPoint3dAt((i + 1) % source.NumberOfVertices);
                            if (vertex.DistanceTo(next) <= PointTolerance)
                            {
                                zeroLengthSegments++;
                                problemReports.Add("ZERO-LENGTH SEGMENT handle=" + handle +
                                    " vertex=" + i.ToString(CultureInfo.InvariantCulture));
                            }

                            string sk = segmentKey(vertex, next);
                            segmentUse[sk] = segmentUse.TryGetValue(sk, out count) ? count + 1 : 1;
                        }

                        try
                        {
                            var curves = new DBObjectCollection();
                            curves.Add(source);
                            DBObjectCollection made = Region.CreateFromCurves(curves);
                            Region region = made.Cast<DBObject>().OfType<Region>().FirstOrDefault();
                            foreach (DBObject item in made)
                                if (!ReferenceEquals(item, region)) item.Dispose();

                            if (region == null)
                            {
                                failedRegionConversions++;
                                problemReports.Add("REGION CONVERSION FAILED handle=" + handle);
                                continue;
                            }

                            regions.Add(region);
                            regionHandles[region] = handle;
                        }
                        catch
                        {
                            failedRegionConversions++;
                            problemReports.Add("REGION CONVERSION EXCEPTION handle=" + handle);
                        }
                    }

                    duplicateSegments = segmentUse.Values.Count(value => value > 1);
                    sharedVertices = vertexUse.Values.Count(value => value > 1);
                    highDegreeVertices = vertexUse.Values.Count(value => value > 2);

                    List<List<Region>> components = BuildRegionComponents(regions, PointTolerance);
                    int componentNumber = 0;
                    spatialComponentCount = components.Count;

                    foreach (List<Region> component in components)
                    {
                        componentNumber++;
                        List<string> componentHandles = component
                            .Where(regionHandles.ContainsKey)
                            .Select(region => regionHandles[region])
                            .ToList();

                        MergeTouchingRegions(component, ref failedUnions, ref successfulUnions, true);

                        int regionNumber = 0;
                        foreach (Region region in component)
                        {
                            regionNumber++;
                            double expectedArea = 0.0;
                            try { expectedArea = Math.Abs(region.Area); } catch { }

                            int attemptsBefore = fallbackAttempts;
                            int incompleteBefore = incompleteTraces;
                            int recoveredBefore = fallbackRecoveries;
                            int candidatesBefore = fallbackCandidates;
                            int rejectedBefore = fallbackRejected;

                            DBObjectCollection exploded = new DBObjectCollection();
                            List<Polyline> loops = new List<Polyline>();
                            try
                            {
                                region.Explode(exploded);
                                loops = BuildClosedPolylines(
                                    exploded, Vector3d.ZAxis, 0.0, expectedArea,
                                    ref fallbackAttempts, ref fallbackCandidates,
                                    ref fallbackRecoveries, ref fallbackRejected,
                                    ref incompleteTraces);
                            }
                            catch (System.Exception ex)
                            {
                                problemReports.Add("REGION TRACE EXCEPTION component=" +
                                    componentNumber.ToString(CultureInfo.InvariantCulture) +
                                    " region=" + regionNumber.ToString(CultureInfo.InvariantCulture) +
                                    " message=" + ex.Message);
                            }
                            finally
                            {
                                DisposeObjects(exploded);
                            }

                            outputLoops += loops.Count;
                            bool traceProblem = loops.Count != 1 ||
                                incompleteTraces > incompleteBefore ||
                                fallbackRecoveries > recoveredBefore ||
                                fallbackAttempts > attemptsBefore;

                            if (traceProblem)
                            {
                                Extents3d extents;
                                string extentText = "unavailable";
                                try
                                {
                                    extents = region.GeometricExtents;
                                    extentText = string.Format(CultureInfo.InvariantCulture,
                                        "X[{0:0.###},{1:0.###}] Y[{2:0.###},{3:0.###}]",
                                        extents.MinPoint.X, extents.MaxPoint.X,
                                        extents.MinPoint.Y, extents.MaxPoint.Y);
                                }
                                catch { }

                                string handlesText = componentHandles.Count <= 30
                                    ? string.Join(",", componentHandles)
                                    : string.Join(",", componentHandles.Take(30)) +
                                      ",...(+" + (componentHandles.Count - 30).ToString(CultureInfo.InvariantCulture) + ")";
                                problemReports.Add(
                                    "TRACE REGION component=" + componentNumber.ToString(CultureInfo.InvariantCulture) +
                                    " region=" + regionNumber.ToString(CultureInfo.InvariantCulture) +
                                    " sourceCount=" + componentHandles.Count.ToString(CultureInfo.InvariantCulture) +
                                    " sourceHandles=" + handlesText +
                                    " area=" + expectedArea.ToString("0.###", CultureInfo.InvariantCulture) +
                                    " loops=" + loops.Count.ToString(CultureInfo.InvariantCulture) +
                                    " fallbackAttempts=" + (fallbackAttempts - attemptsBefore).ToString(CultureInfo.InvariantCulture) +
                                    " candidates=" + (fallbackCandidates - candidatesBefore).ToString(CultureInfo.InvariantCulture) +
                                    " recovered=" + (fallbackRecoveries - recoveredBefore).ToString(CultureInfo.InvariantCulture) +
                                    " rejected=" + (fallbackRejected - rejectedBefore).ToString(CultureInfo.InvariantCulture) +
                                    " extents=" + extentText);
                            }

                            foreach (Polyline loop in loops)
                                try { loop.Dispose(); } catch { }
                        }

                        foreach (Region region in component)
                            try { region.Dispose(); } catch { }
                    }

                    transaction.Commit();
                }

                editor.WriteMessage(
                    "\nCE_CHECKERFGEOMETRY complete. Selected={0}; validClosedPlanar={1}; open={2}; nonXY={3}; zeroOrInvalidArea={4}; zeroLengthSegments={5}; duplicateSegmentKeys={6}; sharedVertexKeys={7}; highDegreeVertexKeys={8}; regionConversionFailures={9}; spatialComponents={10}; successfulUnions={11}; failedUnions={12}; extractedLoops={13}; fallbackAttempts={14}; fallbackCandidates={15}; fallbackRecoveries={16}; rejectedCandidates={17}. Original erf polylines were not modified.",
                    selectedCount, validCount, openCount, nonPlanarCount, invalidAreaCount,
                    zeroLengthSegments, duplicateSegments, sharedVertices, highDegreeVertices,
                    failedRegionConversions, spatialComponentCount, successfulUnions,
                    failedUnions, outputLoops, fallbackAttempts, fallbackCandidates,
                    fallbackRecoveries, fallbackRejected);
                editor.WriteMessage("\n--- ERF GEOMETRY DIAGNOSTIC DETAILS ---");
                if (problemReports.Count == 0)
                    editor.WriteMessage("\nNo obvious source-polyline or region-tracing anomalies were reported.");
                else
                    foreach (string report in problemReports)
                        editor.WriteMessage("\n" + report);
                editor.WriteMessage("\n--- END ERF GEOMETRY DIAGNOSTIC ---");
            }
            catch (System.Exception exception)
            {
                foreach (Region region in regions)
                    try { region.Dispose(); } catch { }
                editor.WriteMessage("\nCE_CHECKERFGEOMETRY stopped safely: {0}. Original erf polylines were not modified.",
                    exception.Message);
            }
        }

        private static ProductionSettingsDialogModel BuildSettings()
        {
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Create Erf Group Boundaries",
                "Select closed erf polylines, or optionally open polylines whose endpoints coincide. Adjacent erven are combined using an AutoCAD region union; shared internal boundaries are removed. Original cadastral lines are never changed. A temporary preview is shown before final creation.");

            model.AddChoice("SelectionMode", "01 Selection", "How to select erven",
                "Select individual / multiple erven",
                "For manual selection, use picks, window, crossing or fence to select one or more disconnected groups.",
                new[]
                {
                    "Select individual / multiple erven",
                    "Select all erven inside a window",
                    "Select all closed polylines on a layer"
                });
            model.AddText("SourceLayer", "01 Selection", "Source erf layer (for layer selection)", "",
                "Used only when 'Select all closed polylines on a layer' is selected. Leave blank to enter the layer after closing this window.");
            model.AddChoice("OpenClosure", "01 Selection", "Open polyline handling",
                "Closed polylines only",
                "Optionally accept open lightweight polylines when their first and last endpoints are at the same location. The duplicate final vertex is removed on a temporary copy only; original geometry is preserved.",
                new[]
                {
                    "Closed polylines only",
                    "Also accept open polylines with coincident endpoints"
                });

            model.AddText("Offset", "02 Boundary settings", "Boundary offset (drawing units)", "0.00",
                "Zero follows the exact outside erf perimeter. A positive value offsets the resulting closed boundary outward.");
            model.AddChoice("Closed", "02 Boundary settings", "Create closed polyline", "Yes",
                "Output boundaries are always closed polylines.", new[] { "Yes" });
            model.AddChoice("RemoveShared", "02 Boundary settings", "Remove internal shared boundaries", "Yes",
                "Adjacent erf regions are united so shared internal edges are not included in the outside boundary.", new[] { "Yes" });
            model.AddChoice("Preserve", "02 Boundary settings", "Preserve original erf lines", "Yes",
                "Original selected erf polylines are read-only and are never erased or modified.", new[] { "Yes" });
            model.AddChoice("Grouping", "02 Boundary settings", "Boundary grouping",
                "Combine touching erven and keep road-reserve-separated groups separate",
                "Creates one outer boundary for each connected erf block. Touching erven within a block are combined; blocks separated by road reserves remain separate and each receives its own boundary.",
                new[]
                {
                    "Combine touching erven into connected groups",
                    "Create one boundary per selected erf",
                    "Combine touching erven and keep road-reserve-separated groups separate"
                });

            model.AddText("Layer", "03 Output", "Output layer", "CE-ERF-GROUP-BOUNDARY",
                "Layer for the final closed boundary polylines. It will be created if it does not exist.");
            model.AddChoice("Color", "03 Output", "Boundary colour", "Yellow (ACI 2)",
                "Colour is assigned directly to the created boundary polylines.",
                new[]
                {
                    "ByLayer (256)", "Red (ACI 1)", "Yellow (ACI 2)", "Green (ACI 3)",
                    "Cyan (ACI 4)", "Blue (ACI 5)", "Magenta (ACI 6)", "White (ACI 7)"
                });
            return model;
        }

        private static ObjectId[] SelectErfPolylines(Editor editor, string mode, string sourceLayer)
        {
            SelectionFilter filter = new SelectionFilter(new[]
            {
                new TypedValue((int)DxfCode.Start, "LWPOLYLINE")
            });

            if (string.Equals(mode, "Select all closed polylines on a layer", StringComparison.OrdinalIgnoreCase))
            {
                string layer = (sourceLayer ?? string.Empty).Trim();
                if (layer.Length == 0)
                {
                    var prompt = new PromptStringOptions("\nEnter the source erf layer name: ")
                    {
                        AllowSpaces = true
                    };
                    PromptResult layerResult = editor.GetString(prompt);
                    if (layerResult.Status != PromptStatus.OK) return new ObjectId[0];
                    layer = layerResult.StringResult.Trim();
                }
                if (layer.Length == 0) return new ObjectId[0];

                var layerFilter = new SelectionFilter(new[]
                {
                    new TypedValue((int)DxfCode.Start, "LWPOLYLINE"),
                    new TypedValue(8, layer)
                });
                PromptSelectionResult all = editor.SelectAll(layerFilter);
                return all.Status == PromptStatus.OK && all.Value != null
                    ? all.Value.GetObjectIds()
                    : new ObjectId[0];
            }

            if (string.Equals(mode, "Select all erven inside a window", StringComparison.OrdinalIgnoreCase))
            {
                PromptPointResult first = editor.GetPoint("\nSpecify first corner of erf selection window: ");
                if (first.Status != PromptStatus.OK) return new ObjectId[0];
                var secondOptions = new PromptCornerOptions("\nSpecify opposite corner: ", first.Value);
                PromptPointResult second = editor.GetCorner(secondOptions);
                if (second.Status != PromptStatus.OK) return new ObjectId[0];
                PromptSelectionResult window = editor.SelectWindow(first.Value, second.Value, filter);
                return window.Status == PromptStatus.OK && window.Value != null
                    ? window.Value.GetObjectIds()
                    : new ObjectId[0];
            }

            PromptSelectionResult selection = editor.GetSelection(
                new PromptSelectionOptions
                {
                    MessageForAdding = "\nSelect closed erf polylines (use window/crossing/fence for multiple groups): ",
                    MessageForRemoval = "\nRemove erf polylines from selection: ",
                    AllowDuplicates = false,
                    RejectObjectsFromNonCurrentSpace = true
                }, filter);
            return selection.Status == PromptStatus.OK && selection.Value != null
                ? selection.Value.GetObjectIds()
                : new ObjectId[0];
        }

        private static List<List<Region>> BuildRegionComponents(
            List<Region> regions,
            double tolerance)
        {
            var components = new List<List<Region>>();
            var remaining = new HashSet<int>(Enumerable.Range(0, regions.Count));

            while (remaining.Count > 0)
            {
                int seed = remaining.First();
                remaining.Remove(seed);
                var component = new List<Region>();
                var queue = new Queue<int>();
                queue.Enqueue(seed);

                while (queue.Count > 0)
                {
                    int current = queue.Dequeue();
                    Region currentRegion = regions[current];
                    component.Add(currentRegion);

                    Extents3d currentExtents;
                    try { currentExtents = currentRegion.GeometricExtents; }
                    catch { continue; }

                    foreach (int candidate in remaining.ToList())
                    {
                        Extents3d candidateExtents;
                        try { candidateExtents = regions[candidate].GeometricExtents; }
                        catch { continue; }

                        bool extentsTouch =
                            currentExtents.MinPoint.X <= candidateExtents.MaxPoint.X + tolerance &&
                            currentExtents.MaxPoint.X + tolerance >= candidateExtents.MinPoint.X &&
                            currentExtents.MinPoint.Y <= candidateExtents.MaxPoint.Y + tolerance &&
                            currentExtents.MaxPoint.Y + tolerance >= candidateExtents.MinPoint.Y;

                        if (extentsTouch)
                        {
                            remaining.Remove(candidate);
                            queue.Enqueue(candidate);
                        }
                    }
                }

                components.Add(component);
            }

            return components;
        }

        private static void MergeTouchingRegions(
            List<Region> regions,
            ref int failedUnionAttempts,
            ref int mergedCount,
            bool skipRoadReserveSeparatedPairs = false)
        {
            bool merged;
            do
            {
                merged = false;
                for (int i = 0; i < regions.Count && !merged; i++)
                {
                    for (int j = i + 1; j < regions.Count; j++)
                    {
                        if (skipRoadReserveSeparatedPairs &&
                            !RegionExtentsTouchOrOverlap(regions[i], regions[j], PointTolerance))
                            continue;

                        Region united;
                        if (!TryUnionRegions(regions[i], regions[j], out united))
                        {
                            failedUnionAttempts++;
                            continue;
                        }

                        // Replace the source pair only after a successful ACIS union.
                        // If both union orders fail, the original regions remain intact.
                        regions[i].Dispose();
                        regions[j].Dispose();
                        regions[i] = united;
                        regions.RemoveAt(j);
                        mergedCount++;
                        merged = true;
                        break;
                    }
                }
            }
            while (merged);
        }

        private static bool TryUnionRegions(Region first, Region second, out Region united)
        {
            united = null;

            // ACIS Boolean Unite can be order-sensitive for some coincident or
            // skew parcel edges. Try both operand orders on disposable clones.
            // The originals are never modified unless one complete attempt succeeds.
            for (int order = 0; order < 2; order++)
            {
                Region left = null;
                Region right = null;
                try
                {
                    Region sourceLeft = order == 0 ? first : second;
                    Region sourceRight = order == 0 ? second : first;

                    left = sourceLeft.Clone() as Region;
                    right = sourceRight.Clone() as Region;
                    if (left == null || right == null)
                        continue;

                    left.BooleanOperation(BooleanOperationType.BoolUnite, right);
                    right.Dispose();
                    right = null;

                    united = left;
                    left = null;
                    return true;
                }
                catch
                {
                    // Retry once with reversed operands. A failed clone/Boolean
                    // operation must not consume or alter either original region.
                }
                finally
                {
                    if (left != null) try { left.Dispose(); } catch { }
                    if (right != null) try { right.Dispose(); } catch { }
                }
            }

            return false;
        }

        private static bool RegionExtentsTouchOrOverlap(Region first, Region second, double tolerance)
        {
            try
            {
                Extents3d a = first.GeometricExtents;
                Extents3d b = second.GeometricExtents;
                return a.MinPoint.X <= b.MaxPoint.X + tolerance &&
                       a.MaxPoint.X + tolerance >= b.MinPoint.X &&
                       a.MinPoint.Y <= b.MaxPoint.Y + tolerance &&
                       a.MaxPoint.Y + tolerance >= b.MinPoint.Y;
            }
            catch
            {
                return true;
            }
        }

        private static List<Polyline> BuildClosedPolylines(
            DBObjectCollection exploded,
            Vector3d normal,
            double elevation,
            double expectedRegionArea,
            ref int fallbackTraceAttempts,
            ref int fallbackTraceCandidates,
            ref int fallbackTraceRecoveries,
            ref int fallbackTraceRejected,
            ref int incompleteLoopRegions)
        {
            var rawSegments = new List<BoundarySegment>();
            foreach (DBObject item in exploded)
            {
                Line line = item as Line;
                if (line != null)
                {
                    rawSegments.Add(new BoundarySegment(ToPlane(line.StartPoint, normal),
                        ToPlane(line.EndPoint, normal), 0.0));
                    continue;
                }

                Polyline pline = item as Polyline;
                if (pline != null && pline.NumberOfVertices >= 2)
                {
                    int count = pline.Closed ? pline.NumberOfVertices : pline.NumberOfVertices - 1;
                    for (int i = 0; i < count; i++)
                    {
                        int next = (i + 1) % pline.NumberOfVertices;
                        rawSegments.Add(new BoundarySegment(
                            ToPlane(pline.GetPoint3dAt(i), normal),
                            ToPlane(pline.GetPoint3dAt(next), normal),
                            pline.GetBulgeAt(i)));
                    }
                    continue;
                }

                Arc arc = item as Arc;
                if (arc != null)
                {
                    Point3d a = ToPlane(arc.StartPoint, normal);
                    Point3d b = ToPlane(arc.EndPoint, normal);
                    Point3d c = ToPlane(arc.Center, normal);
                    double a0 = Math.Atan2(a.Y - c.Y, a.X - c.X);
                    double a1 = Math.Atan2(b.Y - c.Y, b.X - c.X);
                    double sweep = a1 - a0;
                    while (sweep <= 0.0) sweep += Math.PI * 2.0;
                    if (arc.Normal.DotProduct(normal) < 0.0) sweep -= Math.PI * 2.0;
                    rawSegments.Add(new BoundarySegment(a, b, Math.Tan(sweep / 4.0)));
                }
            }

            var results = new List<Polyline>();
            if (rawSegments.Count == 0) return results;

            double scale = 1.0;
            foreach (BoundarySegment segment in rawSegments)
            {
                scale = Math.Max(scale, Math.Abs(segment.Start.X));
                scale = Math.Max(scale, Math.Abs(segment.Start.Y));
                scale = Math.Max(scale, Math.Abs(segment.End.X));
                scale = Math.Max(scale, Math.Abs(segment.End.Y));
            }
            double tolerance = Math.Max(PointTolerance, scale * 1e-8);

            // Build a real endpoint graph first. This avoids the old greedy chain
            // routine consuming the wrong segment when several erf edges meet at
            // the same junction or when ACIS returns slightly different coordinates.
            var nodePoints = new List<Point3d>();
            var nodeLookup = new Dictionary<string, List<int>>();
            var edgeStarts = new List<int>();
            var edgeEnds = new List<int>();
            var edgeBulges = new List<double>();
            var adjacency = new List<List<int>>();

            Func<long, long, string> makeCellKey = delegate(long x, long y)
            {
                return x.ToString(CultureInfo.InvariantCulture) + ":" +
                       y.ToString(CultureInfo.InvariantCulture);
            };

            Func<Point3d, int> getNode = delegate(Point3d point)
            {
                long qx = (long)Math.Floor(point.X / tolerance);
                long qy = (long)Math.Floor(point.Y / tolerance);
                int bestIndex = -1;
                double bestDistance = tolerance * 1.5;

                // Search the 3x3 neighbourhood. With skewed/rotated parcels,
                // numerically equivalent endpoints can fall on opposite sides
                // of a spatial-cell boundary. A single-cell hash misses those joins.
                for (long dx = -1; dx <= 1; dx++)
                {
                    for (long dy = -1; dy <= 1; dy++)
                    {
                        List<int> candidates;
                        if (!nodeLookup.TryGetValue(makeCellKey(qx + dx, qy + dy), out candidates))
                            continue;

                        foreach (int candidate in candidates)
                        {
                            double distance = nodePoints[candidate].DistanceTo(point);
                            if (distance <= bestDistance)
                            {
                                bestDistance = distance;
                                bestIndex = candidate;
                            }
                        }
                    }
                }

                if (bestIndex >= 0) return bestIndex;

                int index = nodePoints.Count;
                nodePoints.Add(point);
                string key = makeCellKey(qx, qy);
                List<int> bucket;
                if (!nodeLookup.TryGetValue(key, out bucket))
                {
                    bucket = new List<int>();
                    nodeLookup[key] = bucket;
                }
                bucket.Add(index);
                adjacency.Add(new List<int>());
                return index;
            };

            foreach (BoundarySegment segment in rawSegments)
            {
                int a = getNode(segment.Start);
                int b = getNode(segment.End);
                if (a == b) continue;
                int edge = edgeStarts.Count;
                edgeStarts.Add(a);
                edgeEnds.Add(b);
                edgeBulges.Add(segment.Bulge);
                adjacency[a].Add(edge);
                adjacency[b].Add(edge);
            }

            var used = new bool[edgeStarts.Count];
            for (int seed = 0; seed < edgeStarts.Count; seed++)
            {
                if (used[seed]) continue;

                int startNode = edgeStarts[seed];
                int currentNode = startNode;
                int currentEdge = seed;
                var chain = new List<BoundarySegment>();
                var chainEdges = new List<int>();
                var localUsed = new HashSet<int>();
                int safety = edgeStarts.Count + 2;
                bool closed = false;

                while (safety-- > 0)
                {
                    if (used[currentEdge] || localUsed.Contains(currentEdge))
                        break;

                    bool forward = edgeStarts[currentEdge] == currentNode;
                    int nextNode = forward ? edgeEnds[currentEdge] : edgeStarts[currentEdge];
                    double bulge = forward ? edgeBulges[currentEdge] : -edgeBulges[currentEdge];

                    Point3d from = nodePoints[currentNode];
                    Point3d to = nodePoints[nextNode];
                    chain.Add(new BoundarySegment(from, to, bulge));
                    chainEdges.Add(currentEdge);
                    localUsed.Add(currentEdge);

                    if (nextNode == startNode)
                    {
                        closed = true;
                        break;
                    }

                    // Find the next unused edge at this graph node. Degree two is
                    // the normal case. At a branch, follow the smallest positive
                    // turn, but do not globally consume edges until a closed loop
                    // has actually been completed.
                    int chosen = -1;
                    double bestTurn = double.MaxValue;
                    double incomingAngle = Math.Atan2(to.Y - from.Y, to.X - from.X);

                    foreach (int candidate in adjacency[nextNode])
                    {
                        if (used[candidate] || localUsed.Contains(candidate)) continue;

                        bool candidateForward = edgeStarts[candidate] == nextNode;
                        int candidateOther = candidateForward ? edgeEnds[candidate] : edgeStarts[candidate];
                        Point3d candidatePoint = nodePoints[candidateOther];
                        double outgoingAngle = Math.Atan2(candidatePoint.Y - to.Y,
                            candidatePoint.X - to.X);
                        double turn = outgoingAngle - incomingAngle;
                        while (turn < 0.0) turn += Math.PI * 2.0;

                        if (turn < bestTurn)
                        {
                            bestTurn = turn;
                            chosen = candidate;
                        }
                    }

                    if (chosen < 0) break;
                    currentNode = nextNode;
                    currentEdge = chosen;
                }

                // Critical recovery: a failed/open trace must not consume its
                // segments. Another seed may reach the same branch from the
                // opposite direction and form the valid perimeter loop.
                if (!closed || chain.Count < 3 ||
                    chain[chain.Count - 1].End.DistanceTo(chain[0].Start) > tolerance * 1.5)
                    continue;

                foreach (int edgeIndex in chainEdges)
                    used[edgeIndex] = true;

                var boundary = new Polyline(chain.Count);
                boundary.Normal = Vector3d.ZAxis;
                boundary.Elevation = elevation;
                for (int i = 0; i < chain.Count; i++)
                    boundary.AddVertexAt(i,
                        new Point2d(chain[i].Start.X, chain[i].Start.Y),
                        chain[i].Bulge, 0.0, 0.0);
                boundary.Closed = true;
                results.Add(boundary);
            }


            // Recovery path: retain the original greedy result when it represents
            // the complete region. A closed-but-incomplete trace must also be
            // recovered, not just a region that produced zero loops.
            double greedyArea = results.Count == 0
                ? 0.0
                : results.Max(SafeArea);
            double areaAllowance = Math.Max(tolerance * tolerance,
                Math.Abs(expectedRegionArea) * 1e-7);
            bool missingOrIncomplete = results.Count == 0 ||
                (expectedRegionArea > areaAllowance &&
                 greedyArea + areaAllowance < expectedRegionArea);

            if (missingOrIncomplete)
            {
                fallbackTraceAttempts++;
                if (results.Count > 0) incompleteLoopRegions++;

                List<Polyline> recovered = BuildClosedPolylinesHalfEdge(
                    rawSegments, elevation, tolerance);

                if (recovered.Count > 0)
                    fallbackTraceCandidates++;

                // Region.Area excludes enclosed voids, whereas an external
                // perimeter's enclosed area includes them. Comparing those two
                // values directly can reject the correct outer boundary.
                // For a region with no greedy loop, accept the first valid
                // half-edge perimeter. For an incomplete greedy loop, replace
                // it only when the fallback has a larger area and contains the
                // existing loop's extents, protecting already-correct geometry.
                bool acceptRecovered = results.Count == 0 && recovered.Count > 0;
                if (!acceptRecovered && recovered.Count > 0 && results.Count > 0)
                {
                    try
                    {
                        Polyline oldLargest = results.OrderByDescending(SafeArea).First();
                        Polyline newLargest = recovered.OrderByDescending(SafeArea).First();
                        Extents3d oldExtents = oldLargest.GeometricExtents;
                        Extents3d newExtents = newLargest.GeometricExtents;
                        bool containsOld =
                            newExtents.MinPoint.X <= oldExtents.MinPoint.X + tolerance &&
                            newExtents.MinPoint.Y <= oldExtents.MinPoint.Y + tolerance &&
                            newExtents.MaxPoint.X + tolerance >= oldExtents.MaxPoint.X &&
                            newExtents.MaxPoint.Y + tolerance >= oldExtents.MaxPoint.Y;
                        acceptRecovered = containsOld &&
                            SafeArea(newLargest) > SafeArea(oldLargest) + areaAllowance;
                    }
                    catch
                    {
                        acceptRecovered = false;
                    }
                }

                if (acceptRecovered)
                {
                    foreach (Polyline oldLoop in results)
                        try { oldLoop.Dispose(); } catch { }
                    results.Clear();
                    results.AddRange(recovered);
                    fallbackTraceRecoveries++;
                }
                else
                {
                    if (recovered.Count > 0) fallbackTraceRejected++;
                    foreach (Polyline recoveredLoop in recovered)
                        try { recoveredLoop.Dispose(); } catch { }
                }
            }

            return results;
        }

        private static List<Polyline> BuildClosedPolylinesHalfEdge(
            List<BoundarySegment> rawSegments,
            double elevation,
            double tolerance)
        {
            var results = new List<Polyline>();
            if (rawSegments == null || rawSegments.Count == 0) return results;

            var nodePoints = new List<Point3d>();
            var nodeLookup = new Dictionary<string, List<int>>();
            var edgeStarts = new List<int>();
            var edgeEnds = new List<int>();
            var edgeBulges = new List<double>();
            var outgoing = new List<List<int>>();

            Func<long, long, string> cellKey = delegate(long x, long y)
            {
                return x.ToString(CultureInfo.InvariantCulture) + ":" +
                       y.ToString(CultureInfo.InvariantCulture);
            };

            Func<Point3d, int> getNode = delegate(Point3d point)
            {
                long qx = (long)Math.Floor(point.X / tolerance);
                long qy = (long)Math.Floor(point.Y / tolerance);
                int best = -1;
                double bestDistance = tolerance * 1.5;

                for (long dx = -1; dx <= 1; dx++)
                {
                    for (long dy = -1; dy <= 1; dy++)
                    {
                        List<int> candidates;
                        if (!nodeLookup.TryGetValue(cellKey(qx + dx, qy + dy), out candidates))
                            continue;

                        foreach (int candidate in candidates)
                        {
                            double distance = nodePoints[candidate].DistanceTo(point);
                            if (distance <= bestDistance)
                            {
                                bestDistance = distance;
                                best = candidate;
                            }
                        }
                    }
                }

                if (best >= 0) return best;

                int index = nodePoints.Count;
                nodePoints.Add(point);
                string key = cellKey(qx, qy);
                List<int> bucket;
                if (!nodeLookup.TryGetValue(key, out bucket))
                {
                    bucket = new List<int>();
                    nodeLookup[key] = bucket;
                }
                bucket.Add(index);
                outgoing.Add(new List<int>());
                return index;
            };

            foreach (BoundarySegment segment in rawSegments)
            {
                int a = getNode(segment.Start);
                int b = getNode(segment.End);
                if (a == b) continue;

                int edge = edgeStarts.Count;
                edgeStarts.Add(a);
                edgeEnds.Add(b);
                edgeBulges.Add(segment.Bulge);
                outgoing[a].Add(edge * 2);
                outgoing[b].Add(edge * 2 + 1);
            }

            int halfEdgeCount = edgeStarts.Count * 2;
            if (halfEdgeCount == 0) return results;

            foreach (List<int> list in outgoing)
            {
                list.Sort(delegate(int left, int right)
                {
                    int le = left / 2;
                    bool lf = (left % 2) == 0;
                    int ln = lf ? edgeEnds[le] : edgeStarts[le];
                    int re = right / 2;
                    bool rf = (right % 2) == 0;
                    int rn = rf ? edgeEnds[re] : edgeStarts[re];

                    Point3d origin = nodePoints[lf ? edgeStarts[le] : edgeEnds[le]];
                    double la = Math.Atan2(nodePoints[ln].Y - origin.Y,
                        nodePoints[ln].X - origin.X);
                    double ra = Math.Atan2(nodePoints[rn].Y - origin.Y,
                        nodePoints[rn].X - origin.X);
                    int compare = la.CompareTo(ra);
                    return compare != 0 ? compare : left.CompareTo(right);
                });
            }

            // Determine endpoint-connected graph components. The largest closed
            // face in each component is its external perimeter; smaller nested
            // faces are internal parcel edges and are deliberately not emitted.
            var nodeComponent = new int[nodePoints.Count];
            for (int i = 0; i < nodeComponent.Length; i++) nodeComponent[i] = -1;
            int componentCount = 0;

            for (int seedNode = 0; seedNode < nodePoints.Count; seedNode++)
            {
                if (nodeComponent[seedNode] >= 0) continue;

                var queue = new Queue<int>();
                queue.Enqueue(seedNode);
                nodeComponent[seedNode] = componentCount;

                while (queue.Count > 0)
                {
                    int node = queue.Dequeue();
                    foreach (int half in outgoing[node])
                    {
                        int edge = half / 2;
                        int other = (half % 2) == 0 ? edgeEnds[edge] : edgeStarts[edge];
                        if (nodeComponent[other] >= 0) continue;
                        nodeComponent[other] = componentCount;
                        queue.Enqueue(other);
                    }
                }
                componentCount++;
            }

            var visited = new bool[halfEdgeCount];
            var bestLoopByComponent = new Dictionary<int, Polyline>();

            for (int seedHalf = 0; seedHalf < halfEdgeCount; seedHalf++)
            {
                if (visited[seedHalf]) continue;

                int currentHalf = seedHalf;
                var chain = new List<BoundarySegment>();
                int safety = halfEdgeCount + 2;
                bool closed = false;

                while (safety-- > 0)
                {
                    if (visited[currentHalf])
                    {
                        closed = currentHalf == seedHalf;
                        break;
                    }

                    int edge = currentHalf / 2;
                    bool forward = (currentHalf % 2) == 0;
                    int fromNode = forward ? edgeStarts[edge] : edgeEnds[edge];
                    int toNode = forward ? edgeEnds[edge] : edgeStarts[edge];

                    chain.Add(new BoundarySegment(
                        nodePoints[fromNode],
                        nodePoints[toNode],
                        forward ? edgeBulges[edge] : -edgeBulges[edge]));
                    visited[currentHalf] = true;

                    int reverseHalf = currentHalf ^ 1;
                    List<int> atDestination = outgoing[toNode];
                    int reversePosition = atDestination.IndexOf(reverseHalf);
                    if (reversePosition < 0 || atDestination.Count == 0) break;

                    int nextPosition = reversePosition - 1;
                    if (nextPosition < 0) nextPosition = atDestination.Count - 1;
                    currentHalf = atDestination[nextPosition];

                    if (currentHalf == seedHalf)
                    {
                        closed = true;
                        break;
                    }
                }

                if (!closed || chain.Count < 3) continue;

                Polyline candidate = null;
                try
                {
                    candidate = new Polyline(chain.Count);
                    candidate.Normal = Vector3d.ZAxis;
                    candidate.Elevation = elevation;
                    for (int i = 0; i < chain.Count; i++)
                    {
                        candidate.AddVertexAt(i,
                            new Point2d(chain[i].Start.X, chain[i].Start.Y),
                            chain[i].Bulge, 0.0, 0.0);
                    }
                    candidate.Closed = true;

                    double area = SafeArea(candidate);
                    if (area <= tolerance * tolerance)
                    {
                        candidate.Dispose();
                        candidate = null;
                        continue;
                    }

                    int component = nodeComponent[edgeStarts[seedHalf / 2]];
                    Polyline previous;
                    if (!bestLoopByComponent.TryGetValue(component, out previous) ||
                        area > SafeArea(previous))
                    {
                        if (previous != null) previous.Dispose();
                        bestLoopByComponent[component] = candidate;
                        candidate = null;
                    }
                }
                catch
                {
                    // A malformed face is ignored; other face walks are retained.
                }
                finally
                {
                    if (candidate != null) candidate.Dispose();
                }
            }

            foreach (Polyline boundary in bestLoopByComponent.Values)
                results.Add(boundary);

            return results;
        }

        private static Point3d ToPlane(Point3d point, Vector3d normal)
        {
            Matrix3d worldToPlane = Matrix3d.WorldToPlane(normal);
            return point.TransformBy(worldToPlane);
        }

        private static bool SamePoint(Point3d a, Point3d b)
        {
            return a.DistanceTo(b) <= PointTolerance;
        }

        private static Polyline ApplyOutwardOffset(Polyline source, double distance)
        {
            if (distance <= PointTolerance) return source;
            try
            {
                double signedArea = 0.0;
                for (int i = 0; i < source.NumberOfVertices; i++)
                {
                    Point2d a = source.GetPoint2dAt(i);
                    Point2d b = source.GetPoint2dAt((i + 1) % source.NumberOfVertices);
                    signedArea += a.X * b.Y - b.X * a.Y;
                }
                double signedDistance = signedArea >= 0.0 ? -distance : distance;
                DBObjectCollection offsets = source.GetOffsetCurves(signedDistance);
                Polyline best = offsets.Cast<DBObject>().OfType<Polyline>()
                    .OrderByDescending(p => SafeArea(p))
                    .FirstOrDefault();
                foreach (DBObject item in offsets)
                    if (!ReferenceEquals(item, best)) item.Dispose();
                if (best == null || !best.Closed)
                {
                    if (best != null) best.Dispose();
                    return null;
                }
                return best;
            }
            catch
            {
                return null;
            }
        }

        private static double SafeArea(Polyline polyline)
        {
            try { return Math.Abs(polyline.Area); }
            catch { return 0.0; }
        }

        private static int ParseColorIndex(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.StartsWith("ByLayer", StringComparison.OrdinalIgnoreCase))
                return 256;
            int open = value.LastIndexOf("ACI ", StringComparison.OrdinalIgnoreCase);
            int close = value.LastIndexOf(')');
            int parsed;
            return open >= 0 && close > open &&
                   int.TryParse(value.Substring(open + 4, close - open - 4), out parsed) &&
                   parsed >= 1 && parsed <= 255 ? parsed : 2;
        }

        private static LayerTableRecord EnsureLayer(
            Database database,
            Transaction transaction,
            string layerName,
            int colorIndex)
        {
            LayerTable table = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
            if (table.Has(layerName))
                return (LayerTableRecord)transaction.GetObject(table[layerName], OpenMode.ForWrite);

            table.UpgradeOpen();
            var record = new LayerTableRecord
            {
                Name = layerName,
                Color = Color.FromColorIndex(ColorMethod.ByAci,
                    (short)(colorIndex == 256 ? 7 : colorIndex))
            };
            ObjectId id = table.Add(record);
            transaction.AddNewlyCreatedDBObject(record, true);
            return record;
        }

        private static void RemoveLayerIfEmpty(Database database, Transaction transaction, string layerName)
        {
            LayerTable table = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
            if (!table.Has(layerName)) return;
            ObjectId layerId = table[layerName];
            LayerTableRecord layer = (LayerTableRecord)transaction.GetObject(layerId, OpenMode.ForRead);
            if (layer == null || layer.IsErased || layer.IsDependent) return;

            bool hasEntities = false;
            BlockTable blocks = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
            foreach (ObjectId blockId in blocks)
            {
                BlockTableRecord block = (BlockTableRecord)transaction.GetObject(blockId, OpenMode.ForRead);
                foreach (ObjectId entityId in block)
                {
                    Entity entity = null;
                    try { entity = transaction.GetObject(entityId, OpenMode.ForRead, false) as Entity; }
                    catch { }
                    if (entity != null && !entity.IsErased &&
                        string.Equals(entity.Layer, layerName, StringComparison.OrdinalIgnoreCase))
                    {
                        hasEntities = true;
                        break;
                    }
                }
                if (hasEntities) break;
            }

            if (!hasEntities)
            {
                layer.UpgradeOpen();
                layer.Erase();
            }
        }

        private static void DisposeObjects(DBObjectCollection objects)
        {
            if (objects == null) return;
            foreach (DBObject item in objects)
                try { item.Dispose(); } catch { }
        }

        private sealed class BoundarySegment
        {
            public BoundarySegment(Point3d start, Point3d end, double bulge)
            {
                Start = start;
                End = end;
                Bulge = bulge;
            }
            public Point3d Start { get; private set; }
            public Point3d End { get; private set; }
            public double Bulge { get; private set; }
        }
    }
}

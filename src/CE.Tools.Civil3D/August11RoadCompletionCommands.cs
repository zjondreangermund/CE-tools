using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.August11RoadCompletionCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Field-test completion tools around the preliminary road layout engine.
    /// These commands work on CE-generated drafting geometry and keep cadastral
    /// source boundaries untouched.
    /// </summary>
    public sealed class August11RoadCompletionCommands
    {
        private const string CenterLayer = "CE-ROAD-CENTERLINE";
        private const string EdgeLayer = "CE-ROAD-EDGE";
        private const string ShoulderLayer = "CE-ROAD-SHOULDER";
        private const string JunctionLayer = "CE-ROAD-JUNCTION";
        private const string TrimLayer = "CE-JUNCTION-TRIM-BOUNDARY";
        private const double Tol = 1e-7;

        [CommandMethod("CE_TOOLS", "CE_ROADAUG11TOOLS", CommandFlags.Modal)]
        public void Tools()
        {
            Document document = Active();
            if (document == null) return;
            DisciplineWorkflowDialogs.SelectAndRun(
                document,
                "CE Tools - Road Layout Completion",
                "Close reserve-centreline gaps, finish junctions, create outside offsets and standardise route-plan annotation before Civil 3D road production.",
                new List<DisciplineWorkflowAction>
                {
                    new DisciplineWorkflowAction("Join continuous road reserve centrelines", "CE_ROADCONTINUITYFIX", "Join touching CE road-centreline pieces into straight-through continuous road strings.", "01 Centreline"),
                    new DisciplineWorkflowAction("Create outside road / sidewalk offsets", "CE_ROADOUTSIDEOFFSET", "Choose road edges or sidewalk/shoulder edges and offset automatically away from the road centre.", "02 Offsets"),
                    new DisciplineWorkflowAction("Create junction trim boundaries", "CE_JUNCTIONTRIMBOUNDARIES", "Create non-plot closed boundaries around multiple junctions and continue into trim-inside.", "03 Junctions"),
                    new DisciplineWorkflowAction("Complete junction setting-out in four-quadrant order", "CE_JUNCTIONSETTINGOUT4", "Order every junction group before passing it to linked junction setting-out.", "03 Junctions"),
                    new DisciplineWorkflowAction("Route annotation presentation", "CE_ROUTEANNOTATIONSTYLE", "Paper text sizes, masks, dimension metre suffix and arrow size.", "04 Annotation"),
                    new DisciplineWorkflowAction("Shift selected route annotations", "CE_ROUTESHIFTANNOTATION", "Move multiple selected text/dimensions/leaders together to resolve overlap.", "04 Annotation"),
                    new DisciplineWorkflowAction("Extract polyline arc segments", "CE_POLYLINEARCS", "Create true Arc entities from curved polyline segments.", "05 Geometry"),
                    new DisciplineWorkflowAction("Road junction / bellmouth tools", "CE_ROADJUNCTIONBULK", "Regenerate multiple T/cross junction returns from current road geometry.", "05 Geometry"),
                    new DisciplineWorkflowAction("Continue to Road Production Centre", "CE_ROADPRODUCTIONCENTRE", "Create alignments, final profiles, corridors, setting-out, BOQ and delivery.", "99 Continue")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_ROADCONTINUITYFIX", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void JoinRoadCentrelines()
        {
            Document document = Active();
            if (document == null) return;
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Continuous Road Reserve Centrelines",
                "Join CE road-centreline pieces that meet at common endpoints. At cross junctions CE Tools continues through the straightest available piece instead of turning into the crossing road.");
            model.AddChoice("Scope", "01 Source", "Road centrelines", "All", "Use all CE road centreline polylines or only selected ones.", new[] { "All", "Selected" });
            model.AddPositiveDouble("Tolerance", "02 Join", "Endpoint join tolerance", 0.20, "Maximum endpoint gap to bridge when joining road-centreline pieces.");
            model.AddDouble("Angle", "02 Join", "Maximum continuation angle", 20.0, "At a junction, only continue into a sufficiently straight next segment.");
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            double tolerance = Math.Max(0.001, model.Double("Tolerance", 0.20));
            double maximumTurn = Math.Max(0.1, Math.Min(89.0, model.Double("Angle", 20.0))) * Math.PI / 180.0;
            List<ObjectId> ids = ResolveLayerPolylines(document, CenterLayer, model.Text("Scope"));
            if (ids.Count == 0)
            {
                document.Editor.WriteMessage("\nCE_ROADCONTINUITYFIX: no CE road centrelines were found.");
                return;
            }

            int chainsCreated = 0;
            int originalsJoined = 0;
            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(document.Database), OpenMode.ForWrite, false) as BlockTableRecord;
                List<RoadPiece> pieces = new List<RoadPiece>();
                foreach (ObjectId id in ids)
                {
                    Polyline polyline;
                    try { polyline = transaction.GetObject(id, OpenMode.ForRead, false) as Polyline; }
                    catch { continue; }
                    if (polyline == null || polyline.Closed || polyline.NumberOfVertices < 2 || polyline.Length <= Tol) continue;
                    pieces.Add(RoadPiece.From(polyline));
                }
                var used = new HashSet<ObjectId>();
                foreach (RoadPiece seed in pieces)
                {
                    if (used.Contains(seed.Id)) continue;
                    var chain = new List<OrientedPiece> { new OrientedPiece(seed, false) };
                    used.Add(seed.Id);
                    ExtendChain(chain, pieces, used, true, tolerance, maximumTurn);
                    ExtendChain(chain, pieces, used, false, tolerance, maximumTurn);
                    if (chain.Count <= 1) continue;

                    Polyline joined = BuildJoinedPolyline(chain);
                    if (joined == null || joined.NumberOfVertices < 2)
                    {
                        if (joined != null) joined.Dispose();
                        continue;
                    }
                    joined.SetDatabaseDefaults(document.Database);
                    joined.Layer = CenterLayer;
                    space.AppendEntity(joined);
                    transaction.AddNewlyCreatedDBObject(joined, true);
                    foreach (OrientedPiece item in chain)
                    {
                        Polyline original;
                        try { original = transaction.GetObject(item.Piece.Id, OpenMode.ForWrite, false) as Polyline; }
                        catch { continue; }
                        if (original != null && !original.IsErased)
                        {
                            original.Erase();
                            originalsJoined++;
                        }
                    }
                    chainsCreated++;
                }
                transaction.Commit();
            }
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_ROADCONTINUITYFIX complete. Continuous road strings created={0}; source pieces joined={1}. Recreate/refresh road edges after a centreline join so parent links use the new continuous strings.", chainsCreated, originalsJoined);
        }

        [CommandMethod("CE_TOOLS", "CE_ROADOUTSIDEOFFSET", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void OutsideOffsets()
        {
            Document document = Active();
            if (document == null) return;
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Outside Road / Sidewalk Offset",
                "Create only the offset side that is farther away from the nearest CE road centreline. This removes the need to guess Positive or Negative offset direction.");
            model.AddChoice("Source", "01 Source", "Source geometry", "Road edges", "Choose road edges or sidewalk/shoulder edges.", new[] { "Road edges", "Sidewalk / shoulder edges" });
            model.AddChoice("Scope", "01 Source", "Scope", "Selected", "Process selected source geometry or all matching CE geometry.", new[] { "Selected", "All" });
            model.AddPositiveDouble("Distance", "02 Offset", "Outside offset distance", 1.5, "Offset distance in drawing units.");
            model.AddText("Layer", "03 Output", "Output layer", "CE-ROAD-OUTSIDE-OFFSET", "Layer for generated outside offsets.");
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;
            string sourceLayer = string.Equals(model.Text("Source"), "Road edges", StringComparison.OrdinalIgnoreCase) ? EdgeLayer : ShoulderLayer;
            List<ObjectId> sources = ResolveLayerPolylines(document, sourceLayer, model.Text("Scope"));
            double distance = Math.Max(0.001, model.Double("Distance", 1.5));
            int created = 0;
            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(document.Database), OpenMode.ForWrite, false) as BlockTableRecord;
                ObjectId outputLayer = EnsureLayer(document.Database, transaction, SafeLayer(model.Text("Layer"), "CE-ROAD-OUTSIDE-OFFSET"), true);
                List<Polyline> centres = space.Cast<ObjectId>().Select(id =>
                {
                    try { return transaction.GetObject(id, OpenMode.ForRead, false) as Polyline; }
                    catch { return null; }
                }).Where(value => value != null && string.Equals(value.Layer, CenterLayer, StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (ObjectId id in sources)
                {
                    Polyline source;
                    try { source = transaction.GetObject(id, OpenMode.ForRead, false) as Polyline; }
                    catch { continue; }
                    if (source == null) continue;
                    Curve best = ChooseOutsideOffset(source, distance, centres);
                    if (best == null) continue;
                    best.SetDatabaseDefaults(document.Database);
                    best.LayerId = outputLayer;
                    space.AppendEntity(best);
                    transaction.AddNewlyCreatedDBObject(best, true);
                    created++;
                }
                transaction.Commit();
            }
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_ROADOUTSIDEOFFSET complete. Outside offsets created={0}.", created);
        }

        [CommandMethod("CE_TOOLS", "CE_JUNCTIONTRIMBOUNDARIES", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void JunctionTrimBoundaries()
        {
            Document document = Active();
            if (document == null) return;
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Junction Trim Boundaries",
                "Cluster generated bellmouth/junction geometry, create one closed non-plot trim boundary per junction, then optionally launch the existing multi-boundary Trim Inside workflow.");
            model.AddChoice("Scope", "01 Junctions", "Junction geometry", "All", "Use all CE junction geometry or only selected junction curves.", new[] { "All", "Selected" });
            model.AddPositiveDouble("Grouping", "02 Boundary", "Junction grouping distance", 30.0, "Curves with nearby centres are treated as one junction.");
            model.AddPositiveDouble("Margin", "02 Boundary", "Boundary margin", 0.5, "Extra distance around the junction extents.");
            model.AddChoice("Trim", "03 Action", "After creating boundaries", "Run Trim Inside", "Launch CE_TRIMINSIDEMULTI using the newly created boundaries, or create boundaries only.", new[] { "Run Trim Inside", "Create boundaries only" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;
            List<ObjectId> curves = ResolveLayerCurves(document, JunctionLayer, model.Text("Scope"));
            if (curves.Count == 0)
            {
                document.Editor.WriteMessage("\nCE_JUNCTIONTRIMBOUNDARIES: no CE junction curves were found.");
                return;
            }
            double grouping = Math.Max(0.1, model.Double("Grouping", 30.0));
            double margin = Math.Max(0.0, model.Double("Margin", 0.5));
            var boundaryIds = new List<ObjectId>();
            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(document.Database), OpenMode.ForWrite, false) as BlockTableRecord;
                ObjectId layer = EnsureLayer(document.Database, transaction, TrimLayer, false);
                List<CurveBox> boxes = ReadCurveBoxes(curves, transaction);
                foreach (List<CurveBox> cluster in ClusterCurveBoxes(boxes, grouping))
                {
                    Extents3d extents = cluster[0].Extents;
                    foreach (CurveBox box in cluster.Skip(1)) extents.AddExtents(box.Extents);
                    double minX = extents.MinPoint.X - margin;
                    double minY = extents.MinPoint.Y - margin;
                    double maxX = extents.MaxPoint.X + margin;
                    double maxY = extents.MaxPoint.Y + margin;
                    if (maxX - minX <= Tol || maxY - minY <= Tol) continue;
                    var boundary = new Polyline(4) { Closed = true };
                    boundary.SetDatabaseDefaults(document.Database);
                    boundary.LayerId = layer;
                    boundary.AddVertexAt(0, new Point2d(minX, minY), 0.0, 0.0, 0.0);
                    boundary.AddVertexAt(1, new Point2d(maxX, minY), 0.0, 0.0, 0.0);
                    boundary.AddVertexAt(2, new Point2d(maxX, maxY), 0.0, 0.0, 0.0);
                    boundary.AddVertexAt(3, new Point2d(minX, maxY), 0.0, 0.0, 0.0);
                    space.AppendEntity(boundary);
                    transaction.AddNewlyCreatedDBObject(boundary, true);
                    boundaryIds.Add(boundary.ObjectId);
                }
                transaction.Commit();
            }
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_JUNCTIONTRIMBOUNDARIES complete. Non-plot boundaries created={0}.", boundaryIds.Count);
            if (boundaryIds.Count > 0 && string.Equals(model.Text("Trim"), "Run Trim Inside", StringComparison.OrdinalIgnoreCase))
            {
                document.Editor.SetImpliedSelection(boundaryIds.ToArray());
                document.SendStringToExecute("CE_TRIMINSIDEMULTI ", true, false, true);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_JUNCTIONSETTINGOUT4", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void JunctionSettingOutFourQuadrants()
        {
            Document document = Active();
            if (document == null) return;
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Ordered Junction Setting-Out",
                "Group return curves by junction, finish every quadrant in that junction, then continue to the next junction. The ordered set is passed to the linked road-junction setting-out command.");
            model.AddChoice("Scope", "01 Junctions", "Junction geometry", "All", "Use all CE junction returns or only selected curves.", new[] { "All", "Selected" });
            model.AddPositiveDouble("Grouping", "02 Order", "Junction grouping distance", 30.0, "Distance used to group return curves into one junction.");
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;
            List<ObjectId> ids = ResolveLayerCurves(document, JunctionLayer, model.Text("Scope"));
            if (ids.Count == 0) return;
            ObjectId[] ordered;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                List<CurveBox> boxes = ReadCurveBoxes(ids, transaction);
                List<List<CurveBox>> groups = ClusterCurveBoxes(boxes, Math.Max(0.1, model.Double("Grouping", 30.0)));
                ordered = groups
                    .OrderByDescending(group => group.Average(item => item.Center.Y))
                    .ThenBy(group => group.Average(item => item.Center.X))
                    .SelectMany(group =>
                    {
                        Point2d centre = new Point2d(group.Average(item => item.Center.X), group.Average(item => item.Center.Y));
                        return group.OrderBy(item => NormalizeAngle(Math.Atan2(item.Center.Y - centre.Y, item.Center.X - centre.X)));
                    })
                    .Select(item => item.Id)
                    .ToArray();
            }
            document.Editor.SetImpliedSelection(ordered);
            document.Editor.WriteMessage("\nCE_JUNCTIONSETTINGOUT4: ordered junction curves={0}; each grouped junction is completed before the next.", ordered.Length);
            document.SendStringToExecute("CE_ROADJUNCTIONSETTINGOUT ", true, false, true);
        }

        [CommandMethod("CE_TOOLS", "CE_ROUTEANNOTATIONSTYLE", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void RouteAnnotationStyle()
        {
            Document document = Active();
            if (document == null) return;
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Route Planner Annotation",
                "Standardise selected/all route-plan text, dimensions and leaders. Paper-size values are converted through the active annotation scale.");
            model.AddChoice("Scope", "01 Selection", "Annotations", "Selected", "Apply to selected supported annotations or all current-space route annotations.", new[] { "Selected", "All" });
            model.AddChoice("TextHeight", "02 Paper", "Paper text height (mm)", "2.5", "Paper text height.", new[] { "1.8", "2.0", "2.5", "3.5", "5.0" });
            model.AddChoice("Mask", "02 Paper", "Background mask", "On", "Apply background mask to MText/MLeader content where supported.", new[] { "On", "Off" });
            model.AddPositiveDouble("Arrow", "02 Paper", "Arrow size (mm)", 3.0, "Paper arrow size for dimensions/leaders where supported.");
            model.AddChoice("Metres", "03 Dimensions", "Dimension display", "Show metre suffix", "Append m to route dimensions or keep current postfix.", new[] { "Show metre suffix", "Keep current" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;
            double paperHeight;
            if (!double.TryParse(model.Text("TextHeight"), NumberStyles.Float, CultureInfo.InvariantCulture, out paperHeight)) paperHeight = 2.5;
            double height = Math.Max(PaperAnnotationScale.ModelTextHeight(document.Database, paperHeight), 0.001);
            double arrow = Math.Max(PaperAnnotationScale.ModelDistance(document.Database, model.Double("Arrow", 3.0)), 0.001);
            bool mask = string.Equals(model.Text("Mask"), "On", StringComparison.OrdinalIgnoreCase);
            bool metres = string.Equals(model.Text("Metres"), "Show metre suffix", StringComparison.OrdinalIgnoreCase);
            List<ObjectId> ids = ResolveAnnotations(document, model.Text("Scope"));
            int changed = 0;
            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids)
                {
                    Entity entity;
                    try { entity = transaction.GetObject(id, OpenMode.ForWrite, false) as Entity; }
                    catch { continue; }
                    if (entity == null) continue;
                    DBText text = entity as DBText;
                    MText mtext = entity as MText;
                    Dimension dimension = entity as Dimension;
                    MLeader leader = entity as MLeader;
                    if (text != null) text.Height = height;
                    if (mtext != null)
                    {
                        mtext.TextHeight = height;
                        try { mtext.BackgroundFill = mask; } catch { }
                        try { mtext.UseBackgroundColor = true; } catch { }
                    }
                    if (dimension != null)
                    {
                        TrySetProperty(dimension, "Dimtxt", height);
                        TrySetProperty(dimension, "Dimasz", arrow);
                        if (metres) TrySetProperty(dimension, "Dimpost", "<> m");
                    }
                    if (leader != null)
                    {
                        TrySetProperty(leader, "TextHeight", height);
                        TrySetProperty(leader, "ArrowSize", arrow);
                        try
                        {
                            MText leaderText = leader.MText;
                            if (leaderText != null)
                            {
                                leaderText.TextHeight = height;
                                leaderText.BackgroundFill = mask;
                                leaderText.UseBackgroundColor = true;
                                leader.MText = leaderText;
                            }
                        }
                        catch { }
                    }
                    TrySetAnnotative(entity);
                    changed++;
                }
                transaction.Commit();
            }
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_ROUTEANNOTATIONSTYLE complete. Annotations updated={0}; paper text={1:0.0} mm; arrow={2:0.0} mm.", changed, paperHeight, model.Double("Arrow", 3.0));
        }

        [CommandMethod("CE_TOOLS", "CE_ROUTESHIFTANNOTATION", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void ShiftRouteAnnotations()
        {
            Document document = Active();
            if (document == null) return;
            List<ObjectId> ids = ResolveAnnotations(document, "Selected");
            if (ids.Count == 0) return;
            PromptPointResult from = document.Editor.GetPoint("\nBase point for annotation shift: ");
            if (from.Status != PromptStatus.OK) return;
            PromptPointOptions toOptions = new PromptPointOptions("\nNew point: ") { BasePoint = from.Value, UseBasePoint = true, UseDashedLine = true };
            PromptPointResult to = document.Editor.GetPoint(toOptions);
            if (to.Status != PromptStatus.OK) return;
            Vector3d displacement = to.Value - from.Value;
            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids)
                {
                    Entity entity;
                    try { entity = transaction.GetObject(id, OpenMode.ForWrite, false) as Entity; }
                    catch { continue; }
                    if (entity == null) continue;
                    try { entity.TransformBy(Matrix3d.Displacement(displacement)); } catch { }
                }
                transaction.Commit();
            }
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_ROUTESHIFTANNOTATION complete. Selected annotations moved together={0}.", ids.Count);
        }

        [CommandMethod("CE_TOOLS", "CE_POLYLINEARCS", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void PolylineCurvesToArcs()
        {
            Document document = Active();
            if (document == null) return;
            PromptSelectionResult selected = document.Editor.SelectImplied();
            if (selected.Status != PromptStatus.OK || selected.Value == null || selected.Value.Count == 0)
            {
                selected = document.Editor.GetSelection(
                    new PromptSelectionOptions { MessageForAdding = "\nSelect lightweight polylines containing curved/bulged segments: ", AllowDuplicates = false, RejectObjectsFromNonCurrentSpace = true },
                    new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, "LWPOLYLINE") }));
            }
            if (selected.Status != PromptStatus.OK || selected.Value == null) return;
            int arcsCreated = 0;
            int straightSegments = 0;
            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = transaction.GetObject(document.Database.CurrentSpaceId, OpenMode.ForWrite, false) as BlockTableRecord;
                foreach (ObjectId id in selected.Value.GetObjectIds())
                {
                    Polyline polyline;
                    try { polyline = transaction.GetObject(id, OpenMode.ForRead, false) as Polyline; }
                    catch { continue; }
                    if (polyline == null) continue;
                    int segmentCount = polyline.Closed ? polyline.NumberOfVertices : polyline.NumberOfVertices - 1;
                    for (int index = 0; index < segmentCount; index++)
                    {
                        if (Math.Abs(polyline.GetBulgeAt(index)) <= Tol)
                        {
                            straightSegments++;
                            continue;
                        }
                        try
                        {
                            CircularArc2d geometry = polyline.GetArcSegment2dAt(index);
                            var arc = new Arc(new Point3d(geometry.Center.X, geometry.Center.Y, polyline.Elevation), geometry.Radius, geometry.StartAngle, geometry.EndAngle);
                            arc.SetDatabaseDefaults(document.Database);
                            arc.LayerId = polyline.LayerId;
                            space.AppendEntity(arc);
                            transaction.AddNewlyCreatedDBObject(arc, true);
                            arcsCreated++;
                        }
                        catch { }
                    }
                }
                transaction.Commit();
            }
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_POLYLINEARCS complete. True arc segments created={0}; straight segments unchanged={1}.", arcsCreated, straightSegments);
        }

        private static Document Active() { return AcApplication.DocumentManager.MdiActiveDocument; }

        private static List<ObjectId> ResolveLayerPolylines(Document document, string layer, string scope)
        {
            if (string.Equals(scope, "Selected", StringComparison.OrdinalIgnoreCase))
            {
                PromptSelectionResult selected = document.Editor.SelectImplied();
                if (selected.Status != PromptStatus.OK || selected.Value == null || selected.Value.Count == 0)
                    selected = document.Editor.GetSelection(new PromptSelectionOptions { MessageForAdding = "\nSelect CE " + layer + " polylines: ", AllowDuplicates = false, RejectObjectsFromNonCurrentSpace = true });
                if (selected.Status != PromptStatus.OK || selected.Value == null) return new List<ObjectId>();
                return FilterLayerPolylines(document.Database, selected.Value.GetObjectIds(), layer);
            }
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(document.Database), OpenMode.ForRead, false) as BlockTableRecord;
                return space == null ? new List<ObjectId>() : FilterLayerPolylines(document.Database, space.Cast<ObjectId>(), layer);
            }
        }

        private static List<ObjectId> FilterLayerPolylines(Database database, IEnumerable<ObjectId> ids, string layer)
        {
            var result = new List<ObjectId>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids.Where(value => !value.IsNull && !value.IsErased).Distinct())
                {
                    Polyline polyline;
                    try { polyline = transaction.GetObject(id, OpenMode.ForRead, false) as Polyline; }
                    catch { continue; }
                    if (polyline != null && string.Equals(polyline.Layer, layer, StringComparison.OrdinalIgnoreCase)) result.Add(id);
                }
            }
            return result;
        }

        private static List<ObjectId> ResolveLayerCurves(Document document, string layer, string scope)
        {
            IEnumerable<ObjectId> ids;
            if (string.Equals(scope, "Selected", StringComparison.OrdinalIgnoreCase))
            {
                PromptSelectionResult selected = document.Editor.SelectImplied();
                if (selected.Status != PromptStatus.OK || selected.Value == null || selected.Value.Count == 0)
                    selected = document.Editor.GetSelection(new PromptSelectionOptions { MessageForAdding = "\nSelect CE junction curves: ", AllowDuplicates = false, RejectObjectsFromNonCurrentSpace = true });
                if (selected.Status != PromptStatus.OK || selected.Value == null) return new List<ObjectId>();
                ids = selected.Value.GetObjectIds();
            }
            else
            {
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    BlockTableRecord space = transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(document.Database), OpenMode.ForRead, false) as BlockTableRecord;
                    ids = space == null ? new ObjectId[0] : space.Cast<ObjectId>().ToArray();
                }
            }
            var result = new List<ObjectId>();
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids)
                {
                    Curve curve;
                    try { curve = transaction.GetObject(id, OpenMode.ForRead, false) as Curve; }
                    catch { continue; }
                    if (curve != null && string.Equals(curve.Layer, layer, StringComparison.OrdinalIgnoreCase)) result.Add(id);
                }
            }
            return result;
        }

        private static Curve ChooseOutsideOffset(Polyline source, double distance, IList<Polyline> centres)
        {
            var candidates = new List<Curve>();
            foreach (double signed in new[] { -distance, distance })
            {
                DBObjectCollection offsets;
                try { offsets = source.GetOffsetCurves(signed); }
                catch { continue; }
                foreach (DBObject value in offsets)
                {
                    Curve curve = value as Curve;
                    if (curve != null) candidates.Add(curve); else value.Dispose();
                }
            }
            if (candidates.Count == 0) return null;
            Curve best = null;
            double bestDistance = double.MinValue;
            foreach (Curve candidate in candidates)
            {
                Point3d midpoint;
                try { midpoint = candidate.GetPointAtDist(candidate.GetDistanceAtParameter((candidate.StartParam + candidate.EndParam) * 0.5)); }
                catch { midpoint = candidate.StartPoint; }
                double nearest = centres.Count == 0 ? 0.0 : centres.Min(centre =>
                {
                    try { return midpoint.DistanceTo(centre.GetClosestPointTo(midpoint, false)); }
                    catch { return 0.0; }
                });
                if (nearest > bestDistance)
                {
                    if (best != null) best.Dispose();
                    best = candidate;
                    bestDistance = nearest;
                }
                else candidate.Dispose();
            }
            return best;
        }

        private static List<CurveBox> ReadCurveBoxes(IEnumerable<ObjectId> ids, Transaction transaction)
        {
            var result = new List<CurveBox>();
            foreach (ObjectId id in ids)
            {
                Curve curve;
                try { curve = transaction.GetObject(id, OpenMode.ForRead, false) as Curve; }
                catch { continue; }
                if (curve == null) continue;
                Extents3d extents;
                try { extents = curve.GeometricExtents; }
                catch { continue; }
                result.Add(new CurveBox(id, extents));
            }
            return result;
        }

        private static List<List<CurveBox>> ClusterCurveBoxes(List<CurveBox> boxes, double distance)
        {
            var groups = new List<List<CurveBox>>();
            foreach (CurveBox box in boxes)
            {
                List<CurveBox> target = groups.FirstOrDefault(group => group.Any(item => item.Center.GetDistanceTo(box.Center) <= distance));
                if (target == null)
                {
                    target = new List<CurveBox>();
                    groups.Add(target);
                }
                target.Add(box);
            }
            return groups;
        }

        private static List<ObjectId> ResolveAnnotations(Document document, string scope)
        {
            IEnumerable<ObjectId> ids;
            if (string.Equals(scope, "Selected", StringComparison.OrdinalIgnoreCase))
            {
                PromptSelectionResult selected = document.Editor.SelectImplied();
                if (selected.Status != PromptStatus.OK || selected.Value == null || selected.Value.Count == 0)
                    selected = document.Editor.GetSelection(new PromptSelectionOptions { MessageForAdding = "\nSelect route text, dimensions and leaders: ", AllowDuplicates = false, RejectObjectsFromNonCurrentSpace = true });
                if (selected.Status != PromptStatus.OK || selected.Value == null) return new List<ObjectId>();
                ids = selected.Value.GetObjectIds();
            }
            else
            {
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    BlockTableRecord space = transaction.GetObject(document.Database.CurrentSpaceId, OpenMode.ForRead, false) as BlockTableRecord;
                    ids = space == null ? new ObjectId[0] : space.Cast<ObjectId>().ToArray();
                }
            }
            var result = new List<ObjectId>();
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids)
                {
                    Entity entity;
                    try { entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity; }
                    catch { continue; }
                    if (entity is DBText || entity is MText || entity is Dimension || entity is MLeader) result.Add(id);
                }
            }
            return result;
        }

        private static void TrySetProperty(object target, string name, object value)
        {
            if (target == null) return;
            try
            {
                PropertyInfo property = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (property == null || !property.CanWrite) return;
                object converted = value;
                if (value != null && !property.PropertyType.IsInstanceOfType(value)) converted = Convert.ChangeType(value, property.PropertyType, CultureInfo.InvariantCulture);
                property.SetValue(target, converted, null);
            }
            catch { }
        }

        private static void TrySetAnnotative(Entity entity)
        {
            try
            {
                PropertyInfo property = entity.GetType().GetProperty("Annotative", BindingFlags.Public | BindingFlags.Instance);
                if (property == null || !property.CanWrite || !property.PropertyType.IsEnum) return;
                object value = Enum.Parse(property.PropertyType, "True", true);
                property.SetValue(entity, value, null);
            }
            catch { }
        }

        private static ObjectId EnsureLayer(Database database, Transaction transaction, string name, bool plottable)
        {
            LayerTable table = transaction.GetObject(database.LayerTableId, OpenMode.ForRead, false) as LayerTable;
            if (table.Has(name))
            {
                LayerTableRecord existing = transaction.GetObject(table[name], OpenMode.ForWrite, false) as LayerTableRecord;
                if (existing != null) existing.IsPlottable = plottable;
                return table[name];
            }
            table.UpgradeOpen();
            var layer = new LayerTableRecord { Name = name, IsPlottable = plottable, Color = Color.FromColorIndex(ColorMethod.ByAci, 8) };
            ObjectId id = table.Add(layer);
            transaction.AddNewlyCreatedDBObject(layer, true);
            return id;
        }

        private static string SafeLayer(string value, string fallback)
        {
            string result = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
            foreach (char invalid in new[] { '<', '>', '/', '\\', '"', ':', ';', '?', '*', '|', '=', ',' }) result = result.Replace(invalid, '-');
            return string.IsNullOrWhiteSpace(result) ? fallback : result;
        }

        private static void ExtendChain(List<OrientedPiece> chain, IList<RoadPiece> pieces, ISet<ObjectId> used, bool tail, double tolerance, double maximumTurn)
        {
            int guard = 0;
            while (guard++ < pieces.Count + 4)
            {
                OrientedPiece edge = tail ? chain[chain.Count - 1] : chain[0];
                Point2d endPoint = tail ? edge.End : edge.Start;
                Vector2d direction = tail ? edge.EndDirection : -edge.StartDirection;
                RoadCandidate best = default(RoadCandidate);
                bool found = false;
                foreach (RoadPiece piece in pieces)
                {
                    if (used.Contains(piece.Id)) continue;
                    foreach (bool reverse in new[] { false, true })
                    {
                        OrientedPiece candidate = new OrientedPiece(piece, reverse);
                        Point2d candidatePoint = tail ? candidate.Start : candidate.End;
                        double gap = endPoint.GetDistanceTo(candidatePoint);
                        if (gap > tolerance) continue;
                        Vector2d candidateDirection = tail ? candidate.StartDirection : -candidate.EndDirection;
                        double angle = AngleBetween(direction, candidateDirection);
                        if (angle > maximumTurn) continue;
                        if (!found || angle < best.Angle || (Math.Abs(angle - best.Angle) < 1e-9 && gap < best.Gap))
                        {
                            best = new RoadCandidate(candidate, angle, gap);
                            found = true;
                        }
                    }
                }
                if (!found) break;
                used.Add(best.Piece.Piece.Id);
                if (tail) chain.Add(best.Piece); else chain.Insert(0, best.Piece);
            }
        }

        private static Polyline BuildJoinedPolyline(IEnumerable<OrientedPiece> chain)
        {
            var points = new List<Point2d>();
            foreach (OrientedPiece item in chain)
            {
                IEnumerable<Point2d> values = item.Reverse ? item.Piece.Points.AsEnumerable().Reverse() : item.Piece.Points;
                foreach (Point2d point in values)
                {
                    if (points.Count == 0 || points[points.Count - 1].GetDistanceTo(point) > Tol) points.Add(point);
                }
            }
            if (points.Count < 2) return null;
            var result = new Polyline(points.Count) { Closed = false };
            for (int index = 0; index < points.Count; index++) result.AddVertexAt(index, points[index], 0.0, 0.0, 0.0);
            return result;
        }

        private static double AngleBetween(Vector2d first, Vector2d second)
        {
            if (first.Length <= Tol || second.Length <= Tol) return Math.PI;
            double dot = Math.Max(-1.0, Math.Min(1.0, first.GetNormal().DotProduct(second.GetNormal())));
            return Math.Acos(dot);
        }

        private static double NormalizeAngle(double angle)
        {
            double value = angle;
            while (value < 0.0) value += Math.PI * 2.0;
            while (value >= Math.PI * 2.0) value -= Math.PI * 2.0;
            return value;
        }

        private sealed class RoadPiece
        {
            internal ObjectId Id;
            internal List<Point2d> Points;
            internal static RoadPiece From(Polyline polyline)
            {
                var result = new RoadPiece { Id = polyline.ObjectId, Points = new List<Point2d>() };
                for (int index = 0; index < polyline.NumberOfVertices; index++) result.Points.Add(polyline.GetPoint2dAt(index));
                return result;
            }
        }

        private struct OrientedPiece
        {
            internal OrientedPiece(RoadPiece piece, bool reverse) { Piece = piece; Reverse = reverse; }
            internal RoadPiece Piece;
            internal bool Reverse;
            internal Point2d Start { get { return Reverse ? Piece.Points[Piece.Points.Count - 1] : Piece.Points[0]; } }
            internal Point2d End { get { return Reverse ? Piece.Points[0] : Piece.Points[Piece.Points.Count - 1]; } }
            internal Vector2d StartDirection
            {
                get
                {
                    Point2d a = Start;
                    Point2d b = Reverse ? Piece.Points[Piece.Points.Count - 2] : Piece.Points[1];
                    return b - a;
                }
            }
            internal Vector2d EndDirection
            {
                get
                {
                    Point2d b = End;
                    Point2d a = Reverse ? Piece.Points[1] : Piece.Points[Piece.Points.Count - 2];
                    return b - a;
                }
            }
        }

        private struct RoadCandidate
        {
            internal RoadCandidate(OrientedPiece piece, double angle, double gap) { Piece = piece; Angle = angle; Gap = gap; }
            internal OrientedPiece Piece;
            internal double Angle;
            internal double Gap;
        }

        private sealed class CurveBox
        {
            internal CurveBox(ObjectId id, Extents3d extents)
            {
                Id = id;
                Extents = extents;
                Center = new Point2d((extents.MinPoint.X + extents.MaxPoint.X) * 0.5, (extents.MinPoint.Y + extents.MaxPoint.Y) * 0.5);
            }
            internal ObjectId Id;
            internal Extents3d Extents;
            internal Point2d Center;
        }
    }
}

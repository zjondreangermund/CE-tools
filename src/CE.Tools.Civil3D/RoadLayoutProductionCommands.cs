using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilCogoPoint = Autodesk.Civil.DatabaseServices.CogoPoint;

[assembly: CommandClass(typeof(CETools.Civil3D.RoadLayoutProductionCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Preliminary road-layout production from cadastral/reserve geometry. The
    /// commands deliberately create separate CE geometry and never erase the
    /// selected cadastral source objects. Generated road edges, shoulders, labels
    /// and dimensions retain parent handles so they can be refreshed after the
    /// source road centreline changes.
    /// </summary>
    public sealed class RoadLayoutProductionCommands
    {
        private const string RecordKey = "CE_ROAD_LAYOUT";
        private const string CenterLayer = "CE-ROAD-CENTERLINE";
        private const string EdgeLayer = "CE-ROAD-EDGE";
        private const string ShoulderLayer = "CE-ROAD-SHOULDER";
        private const string JunctionLayer = "CE-ROAD-JUNCTION";
        private const string LabelLayer = "CE-ROAD-NAME";
        private const string DimensionLayer = "CE-ROAD-DIM";
        private const string SettingOutLayer = "CE-ROAD-JUNCTION-SETTINGOUT";
        private const double Tol = 1e-7;

        [CommandMethod("CE_TOOLS", "CE_ROADLAYOUTTOOLS", CommandFlags.Modal)]
        public void Tools()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            DisciplineWorkflowDialogs.SelectAndRun(
                document,
                "CE Tools - Road Layout Production",
                "Create preliminary road reserve centrelines, edges, shoulders, junction geometry, road names, dimensions and junction-only setting-out before Civil 3D alignment/corridor production.",
                new List<DisciplineWorkflowAction>
                {
                    new DisciplineWorkflowAction("Road reserve centrelines", "CE_ROADRESERVECENTERLINES", "Create road-centre polylines between opposing cadastral/reserve boundaries, including mixed reserve widths.", "01 Reserve geometry"),
                    new DisciplineWorkflowAction("Road edges", "CE_ROADEDGES", "Offset all or selected road centrelines to road edges.", "02 Offsets"),
                    new DisciplineWorkflowAction("Sidewalk / shoulder edges", "CE_ROADSHOULDERS", "Offset all or selected road edges to sidewalk/shoulder lines.", "02 Offsets"),
                    new DisciplineWorkflowAction("General road offset", "CE_ROADOFFSET", "Offset road centrelines, road edges or shoulder edges by a specified distance.", "02 Offsets"),
                    new DisciplineWorkflowAction("Create T/cross junctions in bulk", "CE_ROADJUNCTIONBULK", "Detect multiple road-centre intersections and create T/cross bellmouth returns for all or selected roads.", "03 Junctions"),
                    new DisciplineWorkflowAction("Trim junction middles", "CE_ROADJUNCTIONTRIM", "Trim generated road lines through multiple detected junction areas.", "03 Junctions"),
                    new DisciplineWorkflowAction("Road names", "CE_ROADNAMES", "Create linked sequential road-name labels on all or selected road centrelines.", "04 Annotation"),
                    new DisciplineWorkflowAction("Lane / road-width dimensions", "CE_ROADDIMENSIONS", "Dimension centre-to-edge lane widths and/or complete edge-to-edge road widths.", "04 Annotation"),
                    new DisciplineWorkflowAction("Junction-only vertex setting-out", "CE_ROADJUNCTIONSETTINGOUT", "Create sequenced COGO setting-out points only on selected/all T and cross junction returns.", "05 Setting-out"),
                    new DisciplineWorkflowAction("Refresh linked road layout", "CE_ROADLAYOUTREFRESH", "Recreate linked road edges/shoulders and reposition linked road names/dimensions from current parent geometry.", "06 Maintain"),
                    new DisciplineWorkflowAction("Continue to Civil 3D road production", "CE_ROADPRODUCTION", "Create alignments, profiles, assemblies, corridors and production output from the accepted road layout.", "07 Civil 3D production")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_ROADRESERVECENTERLINES", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void ReserveCenterlines()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Road Reserve Centrelines",
                "Select closed cadastral/reserve polylines. CE Tools pairs sufficiently parallel opposing boundary segments and creates a centre polyline through the open reserve. Different reserve widths are handled per paired segment.");
            model.AddDouble("MinWidth", "Reserve width", "Minimum road reserve width", 6.0, "Ignore opposing boundaries closer than this distance.");
            model.AddDouble("MaxWidth", "Reserve width", "Maximum road reserve width", 60.0, "Ignore opposing boundaries farther apart than this distance.");
            model.AddDouble("Parallel", "Detection", "Parallel tolerance (degrees)", 7.5, "Maximum angular difference between opposing cadastral boundary segments.");
            model.AddDouble("MinOverlap", "Detection", "Minimum overlapping length", 4.0, "Minimum common projected length required to form a road-centre segment.");
            model.AddChoice("Replace", "Output", "Existing CE road centrelines", "Keep existing", "Choose whether previously generated reserve-centrelines are retained or replaced.", new[] { "Keep existing", "Replace existing" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            PromptSelectionResult selection = SelectClosedPolylines(document.Editor, "\nSelect cadastral/reserve closed polylines: ");
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;

            double minWidth = Math.Max(0.001, model.Double("MinWidth", 6.0));
            double maxWidth = Math.Max(minWidth, model.Double("MaxWidth", 60.0));
            double angleTolerance = Math.Max(0.1, model.Double("Parallel", 7.5)) * Math.PI / 180.0;
            double minOverlap = Math.Max(0.001, model.Double("MinOverlap", 4.0));
            bool replace = string.Equals(model.Text("Replace"), "Replace existing", StringComparison.OrdinalIgnoreCase);

            int created = 0;
            int rejected = 0;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = GetModelSpace(document.Database, transaction, OpenMode.ForWrite);
                ObjectId layerId = GetOrCreateLayer(document.Database, transaction, CenterLayer);
                if (replace) EraseByKind(space, transaction, "CENTER");

                List<Polyline> parcels = selection.Value.GetObjectIds()
                    .Select(id => transaction.GetObject(id, OpenMode.ForRead, false) as Polyline)
                    .Where(poly => poly != null && poly.Closed && poly.NumberOfVertices >= 3)
                    .ToList();
                List<BoundarySegment> segments = BuildBoundarySegments(parcels);
                var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int firstIndex = 0; firstIndex < segments.Count; firstIndex++)
                {
                    BoundarySegment first = segments[firstIndex];
                    for (int secondIndex = firstIndex + 1; secondIndex < segments.Count; secondIndex++)
                    {
                        BoundarySegment second = segments[secondIndex];
                        if (first.SourceId == second.SourceId) continue;
                        double angular = ParallelAngle(first.Direction, second.Direction);
                        if (angular > angleTolerance) continue;
                        double width = PerpendicularDistance(first.MidPoint, second.Start, second.End);
                        if (width < minWidth || width > maxWidth) continue;

                        Point3d overlapStart;
                        Point3d overlapEnd;
                        if (!TryCommonMidline(first, second, minOverlap, out overlapStart, out overlapEnd)) continue;
                        Point3d mid = Mid(overlapStart, overlapEnd);
                        if (parcels.Any(parcel => PointInside(parcel, mid)))
                        {
                            rejected++;
                            continue;
                        }
                        string key = SegmentKey(overlapStart, overlapEnd, 0.05);
                        if (!keys.Add(key)) continue;

                        var centre = new Polyline(2);
                        centre.SetDatabaseDefaults(document.Database);
                        centre.LayerId = layerId;
                        centre.AddVertexAt(0, new Point2d(overlapStart.X, overlapStart.Y), 0.0, 0.0, 0.0);
                        centre.AddVertexAt(1, new Point2d(overlapEnd.X, overlapEnd.Y), 0.0, 0.0, 0.0);
                        ObjectId id = space.AppendEntity(centre);
                        transaction.AddNewlyCreatedDBObject(centre, true);
                        WriteLink(centre, transaction, new RoadLink
                        {
                            Kind = "CENTER",
                            ParentHandle = string.Empty,
                            SourceHandles = first.SourceHandle + "," + second.SourceHandle,
                            Offset = 0.0,
                            Width = width,
                            Group = string.Empty,
                            Name = string.Empty
                        });
                        created++;
                    }
                }
                transaction.Commit();
            }
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_ROADRESERVECENTERLINES complete. Centre polylines={0}; rejected inside cadastral parcels={1}.", created, rejected);
        }

        [CommandMethod("CE_TOOLS", "CE_ROADEDGES", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void RoadEdges()
        {
            CreateOffsets("CENTER", EdgeLayer, "EDGE", "CE Tools - Road Edges", "Offset from road centreline", 3.7);
        }

        [CommandMethod("CE_TOOLS", "CE_ROADSHOULDERS", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void RoadShoulders()
        {
            CreateOffsets("EDGE", ShoulderLayer, "SHOULDER", "CE Tools - Sidewalk / Shoulder Edges", "Offset outward from each road edge", 1.5);
        }

        [CommandMethod("CE_TOOLS", "CE_ROADOFFSET", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void GeneralOffset()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            var model = new ProductionSettingsDialogModel(
                "CE Tools - General Road Offset",
                "Offset all or selected CE road centrelines, road edges or sidewalk/shoulder edges.");
            model.AddChoice("Source", "01 Source", "Source road geometry", "Road centrelines", "Choose the CE road geometry family to offset.", new[] { "Road centrelines", "Road edges", "Sidewalk / shoulder edges" });
            model.AddChoice("Scope", "01 Source", "Scope", "Selected", "Process only selected source objects or every matching CE source in model space.", new[] { "Selected", "All" });
            model.AddPositiveDouble("Distance", "02 Offset", "Offset distance", 1.0, "Drawing-unit offset distance.");
            model.AddChoice("Side", "02 Offset", "Offset side", "Both sides", "Create positive, negative or both offset sides.", new[] { "Both sides", "Positive side", "Negative side" });
            model.AddText("Layer", "03 Output", "Output layer", "CE-ROAD-OFFSET", "Layer for the generated offset geometry.");
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;
            string kind = string.Equals(model.Text("Source"), "Road edges", StringComparison.OrdinalIgnoreCase)
                ? "EDGE"
                : string.Equals(model.Text("Source"), "Sidewalk / shoulder edges", StringComparison.OrdinalIgnoreCase)
                    ? "SHOULDER" : "CENTER";
            RunOffset(document, kind, model.Text("Layer"), "OFFSET", model.Text("Scope"), model.Double("Distance", 1.0), model.Text("Side"), false);
        }

        [CommandMethod("CE_TOOLS", "CE_ROADJUNCTIONBULK", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void BulkJunctions()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Bulk Road Junctions",
                "Detect intersections between CE road centrelines. A centreline ending at another road forms a T-junction; two through roads form a cross-junction.");
            model.AddChoice("Scope", "01 Selection", "Roads", "All", "Use every CE road centreline or only selected road centreline polylines.", new[] { "All", "Selected" });
            model.AddChoice("Type", "01 Selection", "Junction type", "T and Cross", "Create both detected junction types or only one type.", new[] { "T and Cross", "T only", "Cross only" });
            model.AddPositiveDouble("Radius", "02 Geometry", "Bellmouth radius", 10.0, "Return radius.");
            model.AddPositiveDouble("HalfWidth", "02 Geometry", "Default road half-width", 3.7, "Used when a centreline has no generated edge offset to infer its half-width.");
            model.AddChoice("Replace", "03 Output", "Existing generated junctions", "Replace existing", "Replace prior CE bulk-junction arcs or keep them.", new[] { "Replace existing", "Keep existing" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            List<ObjectId> centerIds = ResolveRoadScope(document, "CENTER", model.Text("Scope"), "\nSelect road centreline polylines: ");
            if (centerIds.Count < 2)
            {
                document.Editor.WriteMessage("\nCE_ROADJUNCTIONBULK cancelled. At least two road centrelines are required.");
                return;
            }
            double radius = Math.Max(0.01, model.Double("Radius", 10.0));
            double defaultHalf = Math.Max(0.01, model.Double("HalfWidth", 3.7));
            string type = model.Text("Type");
            bool allowT = !string.Equals(type, "Cross only", StringComparison.OrdinalIgnoreCase);
            bool allowCross = !string.Equals(type, "T only", StringComparison.OrdinalIgnoreCase);
            int tCount = 0;
            int crossCount = 0;
            int arcs = 0;

            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = GetModelSpace(document.Database, transaction, OpenMode.ForWrite);
                ObjectId layerId = GetOrCreateLayer(document.Database, transaction, JunctionLayer);
                if (string.Equals(model.Text("Replace"), "Replace existing", StringComparison.OrdinalIgnoreCase))
                    EraseByKind(space, transaction, "JUNCTION_ARC");

                List<Polyline> roads = centerIds
                    .Select(id => transaction.GetObject(id, OpenMode.ForRead, false) as Polyline)
                    .Where(poly => poly != null && !poly.Closed && poly.Length > Tol)
                    .ToList();
                var pointKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < roads.Count; i++)
                {
                    for (int j = i + 1; j < roads.Count; j++)
                    {
                        Point3dCollection intersections = new Point3dCollection();
                        try { roads[i].IntersectWith(roads[j], Intersect.OnBothOperands, intersections, IntPtr.Zero, IntPtr.Zero); }
                        catch { continue; }
                        foreach (Point3d point in intersections)
                        {
                            string key = PointKey(point, 0.05);
                            if (!pointKeys.Add(key)) continue;
                            bool firstEnd = IsNearEnd(roads[i], point, 0.20);
                            bool secondEnd = IsNearEnd(roads[j], point, 0.20);
                            bool isT = firstEnd ^ secondEnd;
                            bool isCross = !firstEnd && !secondEnd;
                            if ((!isT || !allowT) && (!isCross || !allowCross)) continue;
                            if (!isT && !isCross) continue;

                            Polyline main = isT ? (firstEnd ? roads[j] : roads[i]) : roads[i];
                            Polyline side = isT ? (firstEnd ? roads[i] : roads[j]) : roads[j];
                            Vector2d x = TangentAt(main, point);
                            Vector2d y = new Vector2d(-x.Y, x.X);
                            Vector2d sideDirection = TangentAway(side, point);
                            if (sideDirection.DotProduct(y) < 0.0) y = -y;
                            double halfWidth = InferHalfWidth(space, transaction, main, defaultHalf);
                            string group = Guid.NewGuid().ToString("N");
                            IEnumerable<int[]> signs = isCross
                                ? new[] { new[] { -1, 1 }, new[] { 1, 1 }, new[] { 1, -1 }, new[] { -1, -1 } }
                                : new[] { new[] { -1, 1 }, new[] { 1, 1 } };
                            foreach (int[] sign in signs)
                            {
                                Arc arc = CreateLocalQuarterArc(document.Database, point, x, y, sign[0], sign[1], halfWidth, radius);
                                arc.LayerId = layerId;
                                ObjectId arcId = space.AppendEntity(arc);
                                transaction.AddNewlyCreatedDBObject(arc, true);
                                WriteLink(arc, transaction, new RoadLink
                                {
                                    Kind = "JUNCTION_ARC",
                                    ParentHandle = main.Handle.ToString(),
                                    SourceHandles = main.Handle + "," + side.Handle,
                                    Offset = radius,
                                    Width = halfWidth * 2.0,
                                    Group = group,
                                    Name = isT ? "T" : "CROSS"
                                });
                                arcs++;
                            }
                            if (isT) tCount++; else crossCount++;
                        }
                    }
                }
                transaction.Commit();
            }
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_ROADJUNCTIONBULK complete. T-junctions={0}; cross-junctions={1}; return arcs={2}.", tCount, crossCount, arcs);
        }

        [CommandMethod("CE_TOOLS", "CE_ROADJUNCTIONTRIM", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void TrimJunctionMiddles()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Trim Road Lines at Junctions",
                "Trim the middle portions of CE road centrelines, edges, shoulders and general offsets within a circular junction zone. Source cadastral objects are never modified.");
            model.AddChoice("Scope", "01 Scope", "Road geometry", "All", "Process all generated CE road geometry or selected objects only.", new[] { "All", "Selected" });
            model.AddPositiveDouble("Radius", "02 Junction zone", "Trim radius from junction centre", 12.0, "Road-line portions inside this radius are removed.");
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;
            double radius = Math.Max(0.01, model.Double("Radius", 12.0));
            List<ObjectId> targets = ResolveRoadGeometryScope(document, model.Text("Scope"));
            if (targets.Count == 0) return;

            int trimmed = 0;
            int erased = 0;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = GetModelSpace(document.Database, transaction, OpenMode.ForWrite);
                List<Point3d> junctions = ReadJunctionCenters(space, transaction);
                foreach (ObjectId targetId in targets.Distinct().ToList())
                {
                    if (targetId.IsNull || targetId.IsErased) continue;
                    Curve curve = transaction.GetObject(targetId, OpenMode.ForWrite, false) as Curve;
                    if (curve == null || curve.Closed) continue;
                    RoadLink link;
                    TryReadLink(curve, transaction, out link);
                    bool modified = false;
                    foreach (Point3d centre in junctions)
                    {
                        if (curve.IsErased) break;
                        Point3d closest;
                        double distanceAlong;
                        try
                        {
                            closest = curve.GetClosestPointTo(centre, false);
                            double lateral = PlanDistance(closest, centre);
                            if (lateral >= radius) continue;
                            distanceAlong = curve.GetDistAtPoint(closest);
                            double half = Math.Sqrt(Math.Max(0.0, radius * radius - lateral * lateral));
                            double first = Math.Max(0.0, distanceAlong - half);
                            double second = Math.Min(curve.GetDistanceAtParameter(curve.EndParam), distanceAlong + half);
                            if (second - first <= 1e-5) continue;
                            if (first <= 1e-5 && second >= curve.GetDistanceAtParameter(curve.EndParam) - 1e-5)
                            {
                                curve.Erase();
                                erased++;
                                modified = true;
                                break;
                            }
                            Point3dCollection split = new Point3dCollection();
                            if (first > 1e-5) split.Add(curve.GetPointAtDist(first));
                            if (second < curve.GetDistanceAtParameter(curve.EndParam) - 1e-5) split.Add(curve.GetPointAtDist(second));
                            if (split.Count == 0) continue;
                            DBObjectCollection pieces = curve.GetSplitCurves(split);
                            foreach (DBObject item in pieces)
                            {
                                Curve piece = item as Curve;
                                if (piece == null) { item.Dispose(); continue; }
                                Point3d mid = piece.GetPointAtParameter((piece.StartParam + piece.EndParam) * 0.5);
                                if (PlanDistance(mid, centre) < radius - 1e-5)
                                {
                                    piece.Dispose();
                                    continue;
                                }
                                piece.SetDatabaseDefaults(document.Database);
                                piece.LayerId = curve.LayerId;
                                space.AppendEntity(piece);
                                transaction.AddNewlyCreatedDBObject(piece, true);
                                if (link != null) WriteLink(piece, transaction, link);
                            }
                            curve.Erase();
                            trimmed++;
                            modified = true;
                            break;
                        }
                        catch { }
                    }
                    if (modified) continue;
                }
                transaction.Commit();
            }
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_ROADJUNCTIONTRIM complete. Trimmed objects={0}; fully removed inside junction zone={1}.", trimmed, erased);
        }

        [CommandMethod("CE_TOOLS", "CE_ROADNAMES", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void RoadNames()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Road Names",
                "Add sequential linked road names to all or selected CE road centrelines, using the same branch-style idea as utility naming.");
            model.AddChoice("Scope", "01 Selection", "Roads", "All", "Name every road centreline or only selected centrelines.", new[] { "All", "Selected" });
            model.AddText("Prefix", "02 Naming", "Road name prefix", "ROAD", "Names are created as ROAD-1, ROAD-2 and so on.");
            model.AddPositiveInteger("Start", "02 Naming", "Starting number", 1, "First road number.");
            model.AddDouble("Offset", "03 Annotation", "Label offset", 2.0, "Drawing-unit perpendicular offset from the centreline.");
            model.AddDouble("TextHeight", "03 Annotation", "Paper text height", 2.5, "Annotative paper height.");
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;
            List<ObjectId> roads = ResolveRoadScope(document, "CENTER", model.Text("Scope"), "\nSelect road centrelines to name: ");
            if (roads.Count == 0) return;
            string prefix = string.IsNullOrWhiteSpace(model.Text("Prefix")) ? "ROAD" : model.Text("Prefix").Trim();
            int start = model.Integer("Start", 1);
            double offset = model.Double("Offset", 2.0);
            double textHeight = PaperAnnotationScale.ModelTextHeight(document.Database, Math.Max(0.5, model.Double("TextHeight", 2.5)));

            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = GetModelSpace(document.Database, transaction, OpenMode.ForWrite);
                ObjectId layerId = GetOrCreateLayer(document.Database, transaction, LabelLayer);
                var ordered = roads.Select(id => transaction.GetObject(id, OpenMode.ForRead, false) as Polyline)
                    .Where(poly => poly != null)
                    .OrderByDescending(poly => MidPoint(poly).Y)
                    .ThenBy(poly => MidPoint(poly).X)
                    .ToList();
                int index = 0;
                foreach (Polyline road in ordered)
                {
                    EraseChildren(space, transaction, "ROAD_NAME", road.Handle.ToString());
                    Point3d anchor = MidPoint(road);
                    Vector2d tangent = TangentAt(road, anchor);
                    Vector2d normal = new Vector2d(-tangent.Y, tangent.X);
                    Point3d location = anchor + new Vector3d(normal.X * offset, normal.Y * offset, 0.0);
                    var text = new MText();
                    text.SetDatabaseDefaults(document.Database);
                    text.LayerId = layerId;
                    text.Location = location;
                    text.Attachment = AttachmentPoint.MiddleCenter;
                    text.TextHeight = textHeight;
                    text.Rotation = Math.Atan2(tangent.Y, tangent.X);
                    text.Contents = prefix + "-" + (start + index).ToString(CultureInfo.InvariantCulture);
                    PaperAnnotationScale.SetAnnotative(text);
                    space.AppendEntity(text);
                    transaction.AddNewlyCreatedDBObject(text, true);
                    WriteLink(text, transaction, new RoadLink
                    {
                        Kind = "ROAD_NAME",
                        ParentHandle = road.Handle.ToString(),
                        SourceHandles = road.Handle.ToString(),
                        Offset = offset,
                        Width = 0.0,
                        Group = string.Empty,
                        Name = text.Contents
                    });
                    index++;
                }
                transaction.Commit();
            }
            document.Editor.Regen();
        }

        [CommandMethod("CE_TOOLS", "CE_ROADDIMENSIONS", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void RoadDimensions()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Road Width Dimensions",
                "Dimension linked lane widths from centreline to road edge and/or the full edge-to-edge road width.");
            model.AddChoice("Scope", "01 Selection", "Roads", "All", "Dimension all or selected road centrelines.", new[] { "All", "Selected" });
            model.AddChoice("Mode", "02 Dimensions", "Dimension type", "Lane and full road widths", "Choose lane widths, complete road width, or both.", new[] { "Lane widths", "Full road width", "Lane and full road widths" });
            model.AddPositiveDouble("Offset", "02 Dimensions", "Dimension-line offset", 1.0, "Extra distance outside the measured road geometry for the dimension line.");
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;
            List<ObjectId> roads = ResolveRoadScope(document, "CENTER", model.Text("Scope"), "\nSelect road centrelines to dimension: ");
            if (roads.Count == 0) return;
            bool lane = !string.Equals(model.Text("Mode"), "Full road width", StringComparison.OrdinalIgnoreCase);
            bool full = !string.Equals(model.Text("Mode"), "Lane widths", StringComparison.OrdinalIgnoreCase);
            double dimOffset = model.Double("Offset", 1.0);
            int count = 0;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = GetModelSpace(document.Database, transaction, OpenMode.ForWrite);
                ObjectId layerId = GetOrCreateLayer(document.Database, transaction, DimensionLayer);
                foreach (ObjectId id in roads)
                {
                    Polyline road = transaction.GetObject(id, OpenMode.ForRead, false) as Polyline;
                    if (road == null) continue;
                    string handle = road.Handle.ToString();
                    EraseChildren(space, transaction, "ROAD_DIM", handle);
                    List<Curve> edges = ReadChildren(space, transaction, "EDGE", handle).Take(2).ToList();
                    if (edges.Count < 2) continue;
                    Point3d centre = MidPoint(road);
                    Point3d first = edges[0].GetClosestPointTo(centre, false);
                    Point3d second = edges[1].GetClosestPointTo(centre, false);
                    Vector2d tangent = TangentAt(road, centre);
                    Vector2d normal = new Vector2d(-tangent.Y, tangent.X);
                    if (lane)
                    {
                        count += CreateAlignedDimension(document.Database, transaction, space, layerId, centre, first,
                            Mid(centre, first) + new Vector3d(normal.X * dimOffset, normal.Y * dimOffset, 0.0), handle);
                        count += CreateAlignedDimension(document.Database, transaction, space, layerId, centre, second,
                            Mid(centre, second) - new Vector3d(normal.X * dimOffset, normal.Y * dimOffset, 0.0), handle);
                    }
                    if (full)
                    {
                        Point3d linePoint = Mid(first, second) + new Vector3d(tangent.X * dimOffset, tangent.Y * dimOffset, 0.0);
                        count += CreateAlignedDimension(document.Database, transaction, space, layerId, first, second, linePoint, handle);
                    }
                }
                transaction.Commit();
            }
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_ROADDIMENSIONS complete. Dimensions created={0}.", count);
        }

        [CommandMethod("CE_TOOLS", "CE_ROADJUNCTIONSETTINGOUT", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void JunctionSettingOut()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (civilDocument == null) return;
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Junction Vertex Setting-Out",
                "Create COGO setting-out points only at T/cross junction return endpoints and arc midpoints. Junction groups are sequenced top-to-bottom and left-to-right.");
            model.AddChoice("Scope", "01 Selection", "Junctions", "All", "Use all generated junction returns or only selected return arcs.", new[] { "All", "Selected" });
            model.AddText("Prefix", "02 Numbering", "Point prefix", "J", "Names are generated as J1.1, J1.2, etc.");
            model.AddPositiveInteger("Start", "02 Numbering", "Starting junction number", 1, "First junction group number.");
            model.AddChoice("IncludeMid", "03 Points", "Arc midpoint", "Yes", "Include the midpoint of each bellmouth arc as a setting-out point.", new[] { "Yes", "No" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;
            List<ObjectId> arcs = ResolveRoadScope(document, "JUNCTION_ARC", model.Text("Scope"), "\nSelect generated junction return arcs: ");
            if (arcs.Count == 0) return;
            string prefix = string.IsNullOrWhiteSpace(model.Text("Prefix")) ? "J" : model.Text("Prefix").Trim();
            int start = model.Integer("Start", 1);
            bool includeMid = string.Equals(model.Text("IncludeMid"), "Yes", StringComparison.OrdinalIgnoreCase);
            int created = 0;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                var groups = new Dictionary<string, List<Arc>>(StringComparer.OrdinalIgnoreCase);
                foreach (ObjectId id in arcs)
                {
                    Arc arc = transaction.GetObject(id, OpenMode.ForRead, false) as Arc;
                    if (arc == null) continue;
                    RoadLink link;
                    if (!TryReadLink(arc, transaction, out link) || string.IsNullOrWhiteSpace(link.Group)) continue;
                    List<Arc> list;
                    if (!groups.TryGetValue(link.Group, out list)) { list = new List<Arc>(); groups.Add(link.Group, list); }
                    list.Add(arc);
                }
                var orderedGroups = groups.Values
                    .OrderByDescending(group => group.Average(arc => arc.Center.Y))
                    .ThenBy(group => group.Average(arc => arc.Center.X))
                    .ToList();
                int groupIndex = 0;
                foreach (List<Arc> group in orderedGroups)
                {
                    Point3d groupCentre = new Point3d(group.Average(a => a.Center.X), group.Average(a => a.Center.Y), group.Average(a => a.Center.Z));
                    var points = new List<Point3d>();
                    foreach (Arc arc in group.OrderBy(a => ClockwiseKey(a.Center, groupCentre)))
                    {
                        points.Add(arc.StartPoint);
                        if (includeMid) points.Add(arc.GetPointAtParameter((arc.StartParam + arc.EndParam) * 0.5));
                        points.Add(arc.EndPoint);
                    }
                    points = Deduplicate(points, 0.005);
                    points = points.OrderBy(point => ClockwiseKey(point, groupCentre)).ToList();
                    int pointIndex = 1;
                    foreach (Point3d point in points)
                    {
                        string name = prefix + (start + groupIndex).ToString(CultureInfo.InvariantCulture) + "." + pointIndex.ToString(CultureInfo.InvariantCulture);
                        ObjectId cogoId = civilDocument.CogoPoints.Add(point, name, true);
                        CivilCogoPoint cogo = transaction.GetObject(cogoId, OpenMode.ForWrite, false) as CivilCogoPoint;
                        if (cogo != null)
                        {
                            cogo.RawDescription = name;
                            try { cogo.PointName = name; } catch { }
                            ObjectId layerId = GetOrCreateLayer(document.Database, transaction, SettingOutLayer);
                            cogo.LayerId = layerId;
                        }
                        created++;
                        pointIndex++;
                    }
                    groupIndex++;
                }
                transaction.Commit();
            }
            try { CogoPointProjectStyleCommands.ApplySelectedStyles(document, false); } catch { }
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_ROADJUNCTIONSETTINGOUT complete. COGO points created={0}.", created);
        }

        [CommandMethod("CE_TOOLS", "CE_ROADLAYOUTREFRESH", CommandFlags.Modal | CommandFlags.Redraw)]
        public void Refresh()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            int refreshed = RefreshAll(document);
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_ROADLAYOUTREFRESH complete. Linked road objects refreshed={0}.", refreshed);
        }

        internal static int RefreshAll(Document document)
        {
            if (document == null) return 0;
            int refreshed = 0;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = GetModelSpace(document.Database, transaction, OpenMode.ForWrite);
                List<ObjectId> ids = space.Cast<ObjectId>().ToList();
                foreach (ObjectId id in ids)
                {
                    Entity entity;
                    try { entity = transaction.GetObject(id, OpenMode.ForWrite, false) as Entity; }
                    catch { continue; }
                    if (entity == null || entity.IsErased) continue;
                    RoadLink link;
                    if (!TryReadLink(entity, transaction, out link) || string.IsNullOrWhiteSpace(link.ParentHandle)) continue;
                    ObjectId parentId = ResolveHandle(document.Database, link.ParentHandle);
                    Curve parent = parentId.IsNull ? null : transaction.GetObject(parentId, OpenMode.ForRead, false) as Curve;
                    if (parent == null) continue;
                    if (string.Equals(link.Kind, "ROAD_NAME", StringComparison.OrdinalIgnoreCase))
                    {
                        MText text = entity as MText;
                        if (text == null) continue;
                        Point3d anchor = MidPoint(parent);
                        Vector2d tangent = TangentAt(parent, anchor);
                        Vector2d normal = new Vector2d(-tangent.Y, tangent.X);
                        text.Location = anchor + new Vector3d(normal.X * link.Offset, normal.Y * link.Offset, 0.0);
                        text.Rotation = Math.Atan2(tangent.Y, tangent.X);
                        refreshed++;
                    }
                }
                transaction.Commit();
            }
            return refreshed;
        }

        private static void CreateOffsets(string sourceKind, string outputLayer, string outputKind, string title, string label, double defaultDistance)
        {
            Document document = ActiveDocument();
            if (document == null) return;
            var model = new ProductionSettingsDialogModel(title, "Create linked offsets for all or selected CE road geometry.");
            model.AddChoice("Scope", "01 Selection", "Scope", "All", "Use all matching CE road objects or selected objects only.", new[] { "All", "Selected" });
            model.AddPositiveDouble("Distance", "02 Offset", label, defaultDistance, "Drawing-unit offset distance.");
            model.AddChoice("Side", "02 Offset", "Sides", "Both sides", "Create both sides or one signed side.", new[] { "Both sides", "Positive side", "Negative side" });
            model.AddChoice("Replace", "03 Output", "Existing linked children", "Replace existing", "Replace prior linked children from each processed source.", new[] { "Replace existing", "Keep existing" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;
            RunOffset(document, sourceKind, outputLayer, outputKind, model.Text("Scope"), model.Double("Distance", defaultDistance), model.Text("Side"), string.Equals(model.Text("Replace"), "Replace existing", StringComparison.OrdinalIgnoreCase));
        }

        private static void RunOffset(Document document, string sourceKind, string outputLayer, string outputKind, string scope, double distance, string side, bool replace)
        {
            List<ObjectId> sourceIds = ResolveRoadScope(document, sourceKind, scope, "\nSelect CE road source objects: ");
            if (sourceIds.Count == 0) return;
            bool positive = !string.Equals(side, "Negative side", StringComparison.OrdinalIgnoreCase);
            bool negative = !string.Equals(side, "Positive side", StringComparison.OrdinalIgnoreCase);
            int created = 0;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = GetModelSpace(document.Database, transaction, OpenMode.ForWrite);
                ObjectId layerId = GetOrCreateLayer(document.Database, transaction, string.IsNullOrWhiteSpace(outputLayer) ? "CE-ROAD-OFFSET" : outputLayer.Trim());
                foreach (ObjectId sourceId in sourceIds)
                {
                    Curve source = transaction.GetObject(sourceId, OpenMode.ForRead, false) as Curve;
                    if (source == null) continue;
                    if (replace) EraseChildren(space, transaction, outputKind, source.Handle.ToString());
                    foreach (double signed in new[] { positive ? distance : double.NaN, negative ? -distance : double.NaN }.Where(value => !double.IsNaN(value)))
                    {
                        DBObjectCollection offsets;
                        try { offsets = source.GetOffsetCurves(signed); }
                        catch { continue; }
                        foreach (DBObject value in offsets)
                        {
                            Curve child = value as Curve;
                            if (child == null) { value.Dispose(); continue; }
                            child.SetDatabaseDefaults(document.Database);
                            child.LayerId = layerId;
                            space.AppendEntity(child);
                            transaction.AddNewlyCreatedDBObject(child, true);
                            WriteLink(child, transaction, new RoadLink
                            {
                                Kind = outputKind,
                                ParentHandle = source.Handle.ToString(),
                                SourceHandles = source.Handle.ToString(),
                                Offset = signed,
                                Width = Math.Abs(signed) * 2.0,
                                Group = string.Empty,
                                Name = string.Empty
                            });
                            created++;
                        }
                    }
                }
                transaction.Commit();
            }
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE road offset complete. {0} objects created on {1}.", created, outputLayer);
        }

        private static List<ObjectId> ResolveRoadScope(Document document, string kind, string scope, string prompt)
        {
            if (string.Equals(scope, "Selected", StringComparison.OrdinalIgnoreCase))
            {
                PromptSelectionResult result = document.Editor.SelectImplied();
                if (result.Status != PromptStatus.OK || result.Value == null || result.Value.Count == 0)
                    result = document.Editor.GetSelection(new PromptSelectionOptions { MessageForAdding = prompt, AllowDuplicates = false, RejectObjectsFromNonCurrentSpace = true });
                document.Editor.SetImpliedSelection(new ObjectId[0]);
                if (result.Status != PromptStatus.OK || result.Value == null) return new List<ObjectId>();
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                    return result.Value.GetObjectIds().Where(id => HasKind(transaction, id, kind)).ToList();
            }
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = GetModelSpace(document.Database, transaction, OpenMode.ForRead);
                return space.Cast<ObjectId>().Where(id => HasKind(transaction, id, kind)).ToList();
            }
        }

        private static List<ObjectId> ResolveRoadGeometryScope(Document document, string scope)
        {
            string[] kinds = { "CENTER", "EDGE", "SHOULDER", "OFFSET" };
            if (string.Equals(scope, "Selected", StringComparison.OrdinalIgnoreCase))
            {
                PromptSelectionResult result = document.Editor.GetSelection(new PromptSelectionOptions { MessageForAdding = "\nSelect CE road lines to trim: ", AllowDuplicates = false, RejectObjectsFromNonCurrentSpace = true });
                if (result.Status != PromptStatus.OK || result.Value == null) return new List<ObjectId>();
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                    return result.Value.GetObjectIds().Where(id => kinds.Any(kind => HasKind(transaction, id, kind))).ToList();
            }
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = GetModelSpace(document.Database, transaction, OpenMode.ForRead);
                return space.Cast<ObjectId>().Where(id => kinds.Any(kind => HasKind(transaction, id, kind))).ToList();
            }
        }

        private static bool HasKind(Transaction transaction, ObjectId id, string kind)
        {
            Entity entity;
            try { entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity; }
            catch { return false; }
            RoadLink link;
            return entity != null && TryReadLink(entity, transaction, out link) && string.Equals(link.Kind, kind, StringComparison.OrdinalIgnoreCase);
        }

        private static List<BoundarySegment> BuildBoundarySegments(IEnumerable<Polyline> polylines)
        {
            var result = new List<BoundarySegment>();
            foreach (Polyline polyline in polylines)
            {
                for (int index = 0; index < polyline.NumberOfVertices; index++)
                {
                    int next = (index + 1) % polyline.NumberOfVertices;
                    Point3d first = polyline.GetPoint3dAt(index);
                    Point3d second = polyline.GetPoint3dAt(next);
                    Vector2d vector = new Vector2d(second.X - first.X, second.Y - first.Y);
                    if (vector.Length <= Tol) continue;
                    result.Add(new BoundarySegment
                    {
                        SourceId = polyline.ObjectId,
                        SourceHandle = polyline.Handle.ToString(),
                        Start = first,
                        End = second,
                        Direction = vector.GetNormal(),
                        MidPoint = Mid(first, second)
                    });
                }
            }
            return result;
        }

        private static bool TryCommonMidline(BoundarySegment first, BoundarySegment second, double minOverlap, out Point3d start, out Point3d end)
        {
            Vector2d axis = first.Direction;
            Point2d origin = new Point2d(first.Start.X, first.Start.Y);
            double a0 = 0.0;
            double a1 = Project(new Point2d(first.End.X, first.End.Y) - origin, axis);
            double b0 = Project(new Point2d(second.Start.X, second.Start.Y) - origin, axis);
            double b1 = Project(new Point2d(second.End.X, second.End.Y) - origin, axis);
            double firstMin = Math.Min(a0, a1), firstMax = Math.Max(a0, a1);
            double secondMin = Math.Min(b0, b1), secondMax = Math.Max(b0, b1);
            double lo = Math.Max(firstMin, secondMin), hi = Math.Min(firstMax, secondMax);
            if (hi - lo < minOverlap) { start = Point3d.Origin; end = Point3d.Origin; return false; }
            Point3d firstLo = PointAtProjection(first, origin, axis, lo);
            Point3d firstHi = PointAtProjection(first, origin, axis, hi);
            Point3d secondLo = PointAtProjection(second, origin, axis, lo);
            Point3d secondHi = PointAtProjection(second, origin, axis, hi);
            start = Mid(firstLo, secondLo);
            end = Mid(firstHi, secondHi);
            return PlanDistance(start, end) >= minOverlap;
        }

        private static Point3d PointAtProjection(BoundarySegment segment, Point2d origin, Vector2d axis, double projection)
        {
            Point2d target = origin + axis.MultiplyBy(projection);
            Point3d candidate = new Point3d(target.X, target.Y, 0.0);
            Vector2d seg = new Vector2d(segment.End.X - segment.Start.X, segment.End.Y - segment.Start.Y);
            double length2 = seg.DotProduct(seg);
            if (length2 <= Tol) return segment.Start;
            double t = new Vector2d(candidate.X - segment.Start.X, candidate.Y - segment.Start.Y).DotProduct(seg) / length2;
            t = Math.Max(0.0, Math.Min(1.0, t));
            return new Point3d(segment.Start.X + seg.X * t, segment.Start.Y + seg.Y * t, 0.0);
        }

        private static double ParallelAngle(Vector2d first, Vector2d second)
        {
            double dot = Math.Abs(first.GetNormal().DotProduct(second.GetNormal()));
            dot = Math.Max(-1.0, Math.Min(1.0, dot));
            return Math.Acos(dot);
        }

        private static double PerpendicularDistance(Point3d point, Point3d lineStart, Point3d lineEnd)
        {
            Vector2d line = new Vector2d(lineEnd.X - lineStart.X, lineEnd.Y - lineStart.Y);
            if (line.Length <= Tol) return double.MaxValue;
            Vector2d p = new Vector2d(point.X - lineStart.X, point.Y - lineStart.Y);
            return Math.Abs(line.X * p.Y - line.Y * p.X) / line.Length;
        }

        private static bool PointInside(Polyline polygon, Point3d point)
        {
            if (polygon == null || !polygon.Closed || polygon.NumberOfVertices < 3) return false;
            bool inside = false;
            for (int i = 0, j = polygon.NumberOfVertices - 1; i < polygon.NumberOfVertices; j = i++)
            {
                Point2d a = polygon.GetPoint2dAt(i);
                Point2d b = polygon.GetPoint2dAt(j);
                bool intersects = ((a.Y > point.Y) != (b.Y > point.Y)) &&
                    (point.X < (b.X - a.X) * (point.Y - a.Y) / ((b.Y - a.Y) == 0.0 ? 1e-20 : (b.Y - a.Y)) + a.X);
                if (intersects) inside = !inside;
            }
            return inside;
        }

        private static Arc CreateLocalQuarterArc(Database database, Point3d centre, Vector2d x, Vector2d y, int sx, int sy, double halfWidth, double radius)
        {
            Vector2d ux = x.GetNormal();
            Vector2d uy = y.GetNormal();
            Vector2d local = ux.MultiplyBy(sx * (halfWidth + radius)) + uy.MultiplyBy(sy * (halfWidth + radius));
            Point3d arcCentre = centre + new Vector3d(local.X, local.Y, 0.0);
            double baseAngle = Math.Atan2(ux.Y, ux.X);
            double start;
            double end;
            if (sx > 0 && sy > 0) { start = baseAngle + Math.PI; end = baseAngle + Math.PI * 1.5; }
            else if (sx < 0 && sy > 0) { start = baseAngle + Math.PI * 1.5; end = baseAngle + Math.PI * 2.0; }
            else if (sx < 0 && sy < 0) { start = baseAngle; end = baseAngle + Math.PI * 0.5; }
            else { start = baseAngle + Math.PI * 0.5; end = baseAngle + Math.PI; }
            var arc = new Arc(arcCentre, Vector3d.ZAxis, radius, NormalizeAngle(start), NormalizeAngle(end));
            arc.SetDatabaseDefaults(database);
            return arc;
        }

        private static double NormalizeAngle(double angle)
        {
            while (angle < 0.0) angle += Math.PI * 2.0;
            while (angle >= Math.PI * 2.0) angle -= Math.PI * 2.0;
            return angle;
        }

        private static double InferHalfWidth(BlockTableRecord space, Transaction transaction, Polyline road, double fallback)
        {
            List<Curve> edges = ReadChildren(space, transaction, "EDGE", road.Handle.ToString()).ToList();
            if (edges.Count == 0) return fallback;
            Point3d mid = MidPoint(road);
            double average = edges.Select(edge => PlanDistance(edge.GetClosestPointTo(mid, false), mid)).Where(value => value > Tol).DefaultIfEmpty(fallback).Average();
            return average > Tol ? average : fallback;
        }

        private static IEnumerable<Curve> ReadChildren(BlockTableRecord space, Transaction transaction, string kind, string parentHandle)
        {
            foreach (ObjectId id in space)
            {
                Curve curve;
                try { curve = transaction.GetObject(id, OpenMode.ForRead, false) as Curve; }
                catch { continue; }
                RoadLink link;
                if (curve != null && TryReadLink(curve, transaction, out link) &&
                    string.Equals(link.Kind, kind, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(link.ParentHandle, parentHandle, StringComparison.OrdinalIgnoreCase))
                    yield return curve;
            }
        }

        private static List<Point3d> ReadJunctionCenters(BlockTableRecord space, Transaction transaction)
        {
            var groups = new Dictionary<string, List<Arc>>(StringComparer.OrdinalIgnoreCase);
            foreach (ObjectId id in space)
            {
                Arc arc;
                try { arc = transaction.GetObject(id, OpenMode.ForRead, false) as Arc; }
                catch { continue; }
                RoadLink link;
                if (arc == null || !TryReadLink(arc, transaction, out link) ||
                    !string.Equals(link.Kind, "JUNCTION_ARC", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(link.Group)) continue;
                List<Arc> list;
                if (!groups.TryGetValue(link.Group, out list)) { list = new List<Arc>(); groups.Add(link.Group, list); }
                list.Add(arc);
            }
            return groups.Values.Select(list =>
            {
                List<Point3d> points = list.SelectMany(arc => new[] { arc.StartPoint, arc.EndPoint }).ToList();
                return new Point3d(points.Average(p => p.X), points.Average(p => p.Y), points.Average(p => p.Z));
            }).ToList();
        }

        private static bool IsNearEnd(Curve curve, Point3d point, double tolerance)
        {
            return PlanDistance(curve.StartPoint, point) <= tolerance || PlanDistance(curve.EndPoint, point) <= tolerance;
        }

        private static Vector2d TangentAway(Curve curve, Point3d junction)
        {
            double startDistance = PlanDistance(curve.StartPoint, junction);
            double endDistance = PlanDistance(curve.EndPoint, junction);
            Point3d target = startDistance <= endDistance ? curve.GetPointAtDist(Math.Min(curve.GetDistanceAtParameter(curve.EndParam), Math.Max(0.1, curve.GetDistanceAtParameter(curve.EndParam) * 0.05))) : curve.GetPointAtDist(Math.Max(0.0, curve.GetDistanceAtParameter(curve.EndParam) * 0.95));
            Vector2d vector = new Vector2d(target.X - junction.X, target.Y - junction.Y);
            return vector.Length <= Tol ? new Vector2d(1.0, 0.0) : vector.GetNormal();
        }

        private static Vector2d TangentAt(Curve curve, Point3d point)
        {
            double total = curve.GetDistanceAtParameter(curve.EndParam);
            if (total <= Tol) return new Vector2d(1.0, 0.0);
            Point3d closest = curve.GetClosestPointTo(point, false);
            double distance = curve.GetDistAtPoint(closest);
            double delta = Math.Max(0.01, Math.Min(total * 0.02, 1.0));
            Point3d before = curve.GetPointAtDist(Math.Max(0.0, distance - delta));
            Point3d after = curve.GetPointAtDist(Math.Min(total, distance + delta));
            Vector2d vector = new Vector2d(after.X - before.X, after.Y - before.Y);
            return vector.Length <= Tol ? new Vector2d(1.0, 0.0) : vector.GetNormal();
        }

        private static Point3d MidPoint(Curve curve)
        {
            double total = curve.GetDistanceAtParameter(curve.EndParam);
            return curve.GetPointAtDist(total * 0.5);
        }

        private static int CreateAlignedDimension(Database database, Transaction transaction, BlockTableRecord space, ObjectId layerId, Point3d first, Point3d second, Point3d linePoint, string parentHandle)
        {
            if (PlanDistance(first, second) <= Tol) return 0;
            var dimension = new AlignedDimension(first, second, linePoint, string.Empty, database.Dimstyle);
            dimension.SetDatabaseDefaults(database);
            dimension.LayerId = layerId;
            space.AppendEntity(dimension);
            transaction.AddNewlyCreatedDBObject(dimension, true);
            WriteLink(dimension, transaction, new RoadLink
            {
                Kind = "ROAD_DIM",
                ParentHandle = parentHandle,
                SourceHandles = parentHandle,
                Offset = 0.0,
                Width = PlanDistance(first, second),
                Group = string.Empty,
                Name = string.Empty
            });
            return 1;
        }

        private static void EraseByKind(BlockTableRecord space, Transaction transaction, string kind)
        {
            foreach (ObjectId id in space.Cast<ObjectId>().ToList())
            {
                Entity entity;
                try { entity = transaction.GetObject(id, OpenMode.ForWrite, false) as Entity; }
                catch { continue; }
                RoadLink link;
                if (entity != null && TryReadLink(entity, transaction, out link) && string.Equals(link.Kind, kind, StringComparison.OrdinalIgnoreCase)) entity.Erase();
            }
        }

        private static void EraseChildren(BlockTableRecord space, Transaction transaction, string kind, string parentHandle)
        {
            foreach (ObjectId id in space.Cast<ObjectId>().ToList())
            {
                Entity entity;
                try { entity = transaction.GetObject(id, OpenMode.ForWrite, false) as Entity; }
                catch { continue; }
                RoadLink link;
                if (entity != null && TryReadLink(entity, transaction, out link) &&
                    string.Equals(link.Kind, kind, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(link.ParentHandle, parentHandle, StringComparison.OrdinalIgnoreCase)) entity.Erase();
            }
        }

        private static void WriteLink(Entity entity, Transaction transaction, RoadLink link)
        {
            if (entity.ExtensionDictionary.IsNull) entity.CreateExtensionDictionary();
            DBDictionary dictionary = transaction.GetObject(entity.ExtensionDictionary, OpenMode.ForWrite, false) as DBDictionary;
            if (dictionary == null) return;
            Xrecord record;
            if (dictionary.Contains(RecordKey)) record = transaction.GetObject(dictionary.GetAt(RecordKey), OpenMode.ForWrite, false) as Xrecord;
            else
            {
                record = new Xrecord();
                dictionary.SetAt(RecordKey, record);
                transaction.AddNewlyCreatedDBObject(record, true);
            }
            record.Data = new ResultBuffer(
                new TypedValue((int)DxfCode.Text, link.Kind ?? string.Empty),
                new TypedValue((int)DxfCode.Text, link.ParentHandle ?? string.Empty),
                new TypedValue((int)DxfCode.Text, link.SourceHandles ?? string.Empty),
                new TypedValue((int)DxfCode.Real, link.Offset),
                new TypedValue((int)DxfCode.Real, link.Width),
                new TypedValue((int)DxfCode.Text, link.Group ?? string.Empty),
                new TypedValue((int)DxfCode.Text, link.Name ?? string.Empty));
        }

        private static bool TryReadLink(Entity entity, Transaction transaction, out RoadLink link)
        {
            link = null;
            if (entity == null || entity.ExtensionDictionary.IsNull) return false;
            DBDictionary dictionary = transaction.GetObject(entity.ExtensionDictionary, OpenMode.ForRead, false) as DBDictionary;
            if (dictionary == null || !dictionary.Contains(RecordKey)) return false;
            Xrecord record = transaction.GetObject(dictionary.GetAt(RecordKey), OpenMode.ForRead, false) as Xrecord;
            TypedValue[] values = record == null || record.Data == null ? null : record.Data.AsArray();
            if (values == null || values.Length < 7) return false;
            link = new RoadLink
            {
                Kind = Convert.ToString(values[0].Value, CultureInfo.InvariantCulture),
                ParentHandle = Convert.ToString(values[1].Value, CultureInfo.InvariantCulture),
                SourceHandles = Convert.ToString(values[2].Value, CultureInfo.InvariantCulture),
                Offset = Convert.ToDouble(values[3].Value, CultureInfo.InvariantCulture),
                Width = Convert.ToDouble(values[4].Value, CultureInfo.InvariantCulture),
                Group = Convert.ToString(values[5].Value, CultureInfo.InvariantCulture),
                Name = Convert.ToString(values[6].Value, CultureInfo.InvariantCulture)
            };
            return true;
        }

        private static ObjectId ResolveHandle(Database database, string text)
        {
            long value;
            if (database == null || string.IsNullOrWhiteSpace(text) || !long.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)) return ObjectId.Null;
            try { return database.GetObjectId(false, new Handle(value), 0); }
            catch { return ObjectId.Null; }
        }

        private static PromptSelectionResult SelectClosedPolylines(Editor editor, string message)
        {
            PromptSelectionResult implied = editor.SelectImplied();
            if (implied.Status == PromptStatus.OK && implied.Value != null && implied.Value.Count > 0)
            {
                editor.SetImpliedSelection(new ObjectId[0]);
                return implied;
            }
            var filter = new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, "LWPOLYLINE") });
            return editor.GetSelection(new PromptSelectionOptions { MessageForAdding = message, AllowDuplicates = false, RejectObjectsFromNonCurrentSpace = true }, filter);
        }

        private static BlockTableRecord GetModelSpace(Database database, Transaction transaction, OpenMode mode)
        {
            return transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(database), mode, false) as BlockTableRecord;
        }

        private static ObjectId GetOrCreateLayer(Database database, Transaction transaction, string name)
        {
            LayerTable table = transaction.GetObject(database.LayerTableId, OpenMode.ForRead, false) as LayerTable;
            if (table.Has(name)) return table[name];
            table.UpgradeOpen();
            var layer = new LayerTableRecord { Name = name };
            ObjectId id = table.Add(layer);
            transaction.AddNewlyCreatedDBObject(layer, true);
            return id;
        }

        private static Document ActiveDocument()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
        }

        private static Point3d Mid(Point3d a, Point3d b)
        {
            return new Point3d((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5, (a.Z + b.Z) * 0.5);
        }

        private static double PlanDistance(Point3d a, Point3d b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static double Project(Vector2d vector, Vector2d axis)
        {
            return vector.DotProduct(axis);
        }

        private static string PointKey(Point3d point, double grid)
        {
            return Math.Round(point.X / grid).ToString(CultureInfo.InvariantCulture) + ":" + Math.Round(point.Y / grid).ToString(CultureInfo.InvariantCulture);
        }

        private static string SegmentKey(Point3d a, Point3d b, double grid)
        {
            string first = PointKey(a, grid), second = PointKey(b, grid);
            return string.CompareOrdinal(first, second) <= 0 ? first + "|" + second : second + "|" + first;
        }

        private static List<Point3d> Deduplicate(IEnumerable<Point3d> values, double tolerance)
        {
            var result = new List<Point3d>();
            foreach (Point3d point in values)
                if (!result.Any(existing => PlanDistance(existing, point) <= tolerance)) result.Add(point);
            return result;
        }

        private static double ClockwiseKey(Point3d point, Point3d centre)
        {
            double angle = Math.Atan2(point.Y - centre.Y, point.X - centre.X);
            double fromTopClockwise = Math.PI * 0.5 - angle;
            while (fromTopClockwise < 0.0) fromTopClockwise += Math.PI * 2.0;
            return fromTopClockwise;
        }

        private sealed class BoundarySegment
        {
            public ObjectId SourceId { get; set; }
            public string SourceHandle { get; set; }
            public Point3d Start { get; set; }
            public Point3d End { get; set; }
            public Vector2d Direction { get; set; }
            public Point3d MidPoint { get; set; }
        }

        private sealed class RoadLink
        {
            public string Kind { get; set; }
            public string ParentHandle { get; set; }
            public string SourceHandles { get; set; }
            public double Offset { get; set; }
            public double Width { get; set; }
            public string Group { get; set; }
            public string Name { get; set; }
        }
    }
}

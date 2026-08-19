using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.August19CadastralSewerRouteCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// August 19 automatic cadastral sewer planner.  This command is deliberately
    /// separate from the dedicated Midblock and Road-Reserve sewer workflows.
    /// It classifies shared cadastral edges as Midblock candidates and exterior
    /// cadastral edges as Road-Reserve candidates, analyses a selected surface,
    /// and builds the shortest practical connected route toward the site low side.
    /// </summary>
    public sealed class August19CadastralSewerRouteCommands
    {
        private const string LinkKey = "CE_CADASTRAL_SEWER_ROUTE";
        private const string MidblockLayer = "CE-SEWER-CADASTRAL-MIDBLOCK";
        private const string RoadReserveLayer = "CE-SEWER-CADASTRAL-ROADRESERVE";
        private const string ManholeLayer = "CE-SEWER-CADASTRAL-MH";
        private const string AnalysisLayer = "CE-SEWER-CADASTRAL-ANALYSIS";
        private const double Tol = 1e-7;
        private const double SnapTolerance = 0.05;

        [CommandMethod("CE_TOOLS", "CE_SEWERFROMCADASTRAL", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void CreateSewerFromCadastral()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            var model = new ProductionSettingsDialogModel(
                "CE Tools - Sewer Route from Cadastral Data",
                "Create connected preliminary sewer routes directly from cadastral erf boundaries. CE Tools analyses a selected Civil 3D surface, determines the site/network low point, and chooses the shortest practical route toward that low point. Midblock and Road-Reserve sewer remain separate dedicated commands.");
            model.AddChoice("Scope", "01 Cadastral", "Erf boundaries", "Selected",
                "Use selected closed cadastral erf polylines or all non-CE closed lightweight polylines in model space.",
                new[] { "Selected", "All" });
            model.AddChoice("Preference", "02 Routing", "Automatic route preference", "Shortest practical route",
                "Choose the shortest practical cadastral route, or slightly prefer Midblock / Road-Reserve edges while still respecting the surface flow direction.",
                new[] { "Shortest practical route", "Prefer midblock", "Prefer road reserve" });
            model.AddPositiveDouble("MidblockOffset", "02 Routing", "Offset from shared erf boundary", 1.5,
                "Offset shared/midblock cadastral route edges from the common erf boundary.");
            model.AddPositiveDouble("RoadReserveOffset", "02 Routing", "Offset from outer erf boundary", 5.0,
                "Offset exterior cadastral route edges away from the erf interior and toward the road reserve / road centre.");
            model.AddChoice("Spacing", "03 Manholes", "Maximum planning manhole spacing", "60 m",
                "Place planning manholes at every route vertex/junction and split long route edges to this maximum spacing.",
                new[] { "60 m", "80 m", "Custom" });
            model.AddPositiveDouble("CustomSpacing", "03 Manholes", "Custom maximum spacing", 60.0,
                "Used only when Custom spacing is selected.");
            model.AddPositiveDouble("StartSetback", "03 Manholes", "Starting manhole setback", 1.5,
                "Set leaf/start manholes back from terminal cadastral boundaries by this distance where geometry permits.");
            model.AddPositiveDouble("ManholeDiameter", "03 Manholes", "Planning manhole diameter", 1.2,
                "Diameter of preliminary planning manhole circles.");
            model.AddChoice("Replace", "04 Output", "Existing cadastral sewer output", "Replace existing",
                "Replace prior CE cadastral sewer routes/manholes or retain them.",
                new[] { "Replace existing", "Keep existing" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            List<ObjectId> parcelIds = ResolveParcels(document, model.Text("Scope"));
            if (parcelIds.Count == 0)
            {
                document.Editor.WriteMessage("\nCE_SEWERFROMCADASTRAL: no closed cadastral erf polylines were found.");
                return;
            }

            ObjectId surfaceId = PromptSurface(document);
            if (surfaceId.IsNull)
            {
                document.Editor.WriteMessage("\nCE_SEWERFROMCADASTRAL cancelled. Select a Civil 3D surface so CE Tools can analyse slopes and the site low point.");
                return;
            }

            double midblockOffset = Math.Max(0.0, model.Double("MidblockOffset", 1.5));
            double roadReserveOffset = Math.Max(0.0, model.Double("RoadReserveOffset", 5.0));
            double spacing = string.Equals(model.Text("Spacing"), "80 m", StringComparison.OrdinalIgnoreCase)
                ? 80.0
                : string.Equals(model.Text("Spacing"), "Custom", StringComparison.OrdinalIgnoreCase)
                    ? Math.Max(1.0, model.Double("CustomSpacing", 60.0))
                    : 60.0;
            double startSetback = Math.Max(0.0, model.Double("StartSetback", 1.5));
            double manholeDiameter = Math.Max(0.1, model.Double("ManholeDiameter", 1.2));

            int created = 0;
            int manholes = 0;
            int served = 0;
            int skipped = 0;
            LowPointResult low = new LowPointResult();

            try
            {
                using (DocumentLock documentLock = document.LockDocument())
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    Surface surface = transaction.GetObject(surfaceId, OpenMode.ForRead, false) as Surface;
                    if (surface == null)
                    {
                        document.Editor.WriteMessage("\nCE_SEWERFROMCADASTRAL: selected object is not a readable Civil 3D surface.");
                        return;
                    }

                    List<ParcelInfo> parcels = ReadParcels(parcelIds, transaction);
                    Graph graph = BuildGraph(parcels);
                    if (parcels.Count == 0 || graph.Nodes.Count == 0 || graph.Edges.Count == 0)
                    {
                        document.Editor.WriteMessage("\nCE_SEWERFROMCADASTRAL: cadastral geometry did not produce a usable connected boundary graph.");
                        return;
                    }

                    Dictionary<int, double> elevations = SampleNodeElevations(graph, surface);
                    low = ResolveLowPoint(parcels, graph, surface, elevations);
                    if (!low.Valid)
                    {
                        document.Editor.WriteMessage("\nCE_SEWERFROMCADASTRAL: the selected surface has no readable elevations over the cadastral data.");
                        return;
                    }

                    RoutingResult routing = BuildRouting(graph, parcels, elevations, low.NodeId, model.Text("Preference"));
                    served = routing.ServedParcels;
                    if (routing.SelectedEdgeIds.Count == 0)
                    {
                        document.Editor.WriteMessage("\nCE_SEWERFROMCADASTRAL: no connected flow-compatible route to the low-point outlet could be found.");
                        return;
                    }

                    Dictionary<int, Vector2d> shifts = BuildNodeShifts(
                        graph, parcels, routing.SelectedEdgeIds, surface, low.Point,
                        midblockOffset, roadReserveOffset);
                    Dictionary<int, int> selectedDegree = BuildSelectedDegree(graph, routing.SelectedEdgeIds);

                    BlockTableRecord space = transaction.GetObject(
                        SymbolUtilityServices.GetBlockModelSpaceId(document.Database), OpenMode.ForWrite, false) as BlockTableRecord;
                    if (space == null) return;

                    ObjectId midblockLayer = EnsureLayer(document.Database, transaction, MidblockLayer);
                    ObjectId roadLayer = EnsureLayer(document.Database, transaction, RoadReserveLayer);
                    ObjectId mhLayer = EnsureLayer(document.Database, transaction, ManholeLayer);
                    ObjectId analysisLayer = EnsureLayer(document.Database, transaction, AnalysisLayer);
                    if (string.Equals(model.Text("Replace"), "Replace existing", StringComparison.OrdinalIgnoreCase))
                        EraseExisting(space, transaction);

                    var mhPoints = new Dictionary<string, Point3d>(StringComparer.OrdinalIgnoreCase);
                    int routeNumber = 1;
                    foreach (int edgeId in routing.SelectedEdgeIds.OrderBy(value => value))
                    {
                        Edge edge;
                        if (!graph.Edges.TryGetValue(edgeId, out edge)) { skipped++; continue; }
                        int upstream = ResolveUpstream(edge, routing.DistanceToOutlet, routing.NextTowardOutlet);
                        int downstream = upstream == edge.A ? edge.B : edge.A;
                        Point2d start = Shifted(graph.Nodes[upstream].Point, shifts, upstream);
                        Point2d end = Shifted(graph.Nodes[downstream].Point, shifts, downstream);
                        double length = Distance(start, end);
                        if (length <= Tol) { skipped++; continue; }

                        int degree;
                        bool leafStart = selectedDegree.TryGetValue(upstream, out degree) && degree == 1 && upstream != low.NodeId;
                        if (leafStart && startSetback > Tol && length > startSetback + 0.25)
                            start = MoveToward(start, end, Math.Min(startSetback, length * 0.45));

                        List<Point2d> points = SplitBySpacing(start, end, spacing);
                        if (points.Count < 2) { skipped++; continue; }
                        var route = new Polyline(points.Count);
                        for (int i = 0; i < points.Count; i++)
                            route.AddVertexAt(i, points[i], 0.0, 0.0, 0.0);
                        route.SetDatabaseDefaults(document.Database);
                        route.LayerId = string.Equals(edge.Kind, "MIDBLOCK", StringComparison.OrdinalIgnoreCase)
                            ? midblockLayer : roadLayer;
                        space.AppendEntity(route);
                        transaction.AddNewlyCreatedDBObject(route, true);
                        WriteLink(route, transaction, edge, surfaceId, low, routeNumber);
                        created++;
                        routeNumber++;

                        foreach (Point2d point in points)
                        {
                            string key = PointKey(point);
                            if (!mhPoints.ContainsKey(key))
                                mhPoints[key] = new Point3d(point.X, point.Y, 0.0);
                        }
                    }

                    int mhNumber = 1;
                    foreach (Point3d location in mhPoints.Values
                        .OrderByDescending(point => Distance(new Point2d(point.X, point.Y), low.Point)))
                    {
                        var circle = new Circle(location, Vector3d.ZAxis, manholeDiameter * 0.5);
                        circle.SetDatabaseDefaults(document.Database);
                        circle.LayerId = mhLayer;
                        space.AppendEntity(circle);
                        transaction.AddNewlyCreatedDBObject(circle, true);

                        var label = new DBText
                        {
                            Position = location + new Vector3d(manholeDiameter, manholeDiameter, 0.0),
                            TextString = "MH-P" + mhNumber.ToString(CultureInfo.InvariantCulture),
                            Height = Math.Max(PaperAnnotationScale.ModelTextHeight(document.Database, 2.0), 0.001),
                            LayerId = mhLayer
                        };
                        label.SetDatabaseDefaults(document.Database);
                        space.AppendEntity(label);
                        transaction.AddNewlyCreatedDBObject(label, true);
                        mhNumber++;
                        manholes++;
                    }

                    AddAnalysisMarker(document.Database, transaction, space, analysisLayer,
                        low.SurfaceMinimumSample, "SAMPLED SITE LOW POINT", low.SurfaceMinimumElevation, manholeDiameter);
                    if (Distance(low.SurfaceMinimumSample, low.Point) > SnapTolerance)
                        AddAnalysisMarker(document.Database, transaction, space, analysisLayer,
                            low.Point, "NETWORK OUTLET", low.Elevation, manholeDiameter);

                    transaction.Commit();
                }
            }
            catch (Autodesk.AutoCAD.Runtime.Exception ex)
            {
                document.Editor.WriteMessage("\nCE_SEWERFROMCADASTRAL stopped safely: {0}", ex.Message);
                return;
            }
            catch (System.Exception ex)
            {
                document.Editor.WriteMessage("\nCE_SEWERFROMCADASTRAL stopped safely: {0}", ex.Message);
                return;
            }

            document.Editor.SetImpliedSelection(new ObjectId[0]);
            document.Editor.Regen();
            UniversalDynamicRefreshManager.Queue();
            document.Editor.WriteMessage(
                "\nCE_SEWERFROMCADASTRAL complete. Served erfs={0}/{1}; route segments={2}; planning manholes={3}; skipped={4}; sampled site low point EL={5:0.###}; network outlet EL={6:0.###}.",
                served, parcelIds.Count, created, manholes, skipped,
                low.SurfaceMinimumElevation, low.Elevation);
        }

        [CommandMethod("CE_TOOLS", "CE_SEWERFROMROADRESERVE", CommandFlags.Modal)]
        public void SewerFromRoadReserve()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            document.Editor.WriteMessage("\nOpening the separate Road-Reserve sewer route workflow...");
            document.SendStringToExecute("CE_UTILITYFROMROADRESERVE ", true, false, true);
        }

        private static ObjectId PromptSurface(Document document)
        {
            var options = new PromptEntityOptions("\nSelect Civil 3D surface for cadastral sewer slope / low-point analysis: ");
            options.SetRejectMessage("\nSelect a Civil 3D surface.");
            options.AddAllowedClass(typeof(Surface), true);
            PromptEntityResult result = document.Editor.GetEntity(options);
            return result.Status == PromptStatus.OK ? result.ObjectId : ObjectId.Null;
        }

        private static List<ObjectId> ResolveParcels(Document document, string scope)
        {
            if (string.Equals(scope, "Selected", StringComparison.OrdinalIgnoreCase))
            {
                PromptSelectionResult selection = document.Editor.SelectImplied();
                if (selection.Status != PromptStatus.OK || selection.Value == null || selection.Value.Count == 0)
                {
                    selection = document.Editor.GetSelection(
                        new PromptSelectionOptions
                        {
                            MessageForAdding = "\nSelect closed cadastral erf polylines: ",
                            AllowDuplicates = false,
                            RejectObjectsFromNonCurrentSpace = true
                        },
                        new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, "LWPOLYLINE") }));
                }
                if (selection.Status != PromptStatus.OK || selection.Value == null) return new List<ObjectId>();
                return FilterClosed(document.Database, selection.Value.GetObjectIds());
            }

            var ids = new List<ObjectId>();
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = transaction.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(document.Database), OpenMode.ForRead, false) as BlockTableRecord;
                if (space == null) return ids;
                foreach (ObjectId id in space)
                {
                    Polyline polyline;
                    try { polyline = transaction.GetObject(id, OpenMode.ForRead, false) as Polyline; }
                    catch { continue; }
                    if (polyline == null || !polyline.Closed || polyline.NumberOfVertices < 3) continue;
                    if (!string.IsNullOrWhiteSpace(polyline.Layer) && polyline.Layer.StartsWith("CE-", StringComparison.OrdinalIgnoreCase)) continue;
                    ids.Add(id);
                }
            }
            return ids;
        }

        private static List<ObjectId> FilterClosed(Database database, IEnumerable<ObjectId> ids)
        {
            var result = new List<ObjectId>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids.Where(value => !value.IsNull && !value.IsErased).Distinct())
                {
                    Polyline polyline;
                    try { polyline = transaction.GetObject(id, OpenMode.ForRead, false) as Polyline; }
                    catch { continue; }
                    if (polyline != null && polyline.Closed && polyline.NumberOfVertices >= 3)
                        result.Add(id);
                }
            }
            return result;
        }

        private static List<ParcelInfo> ReadParcels(IEnumerable<ObjectId> ids, Transaction transaction)
        {
            var result = new List<ParcelInfo>();
            int index = 0;
            foreach (ObjectId id in ids)
            {
                Polyline polyline;
                try { polyline = transaction.GetObject(id, OpenMode.ForRead, false) as Polyline; }
                catch { continue; }
                if (polyline == null || !polyline.Closed || polyline.NumberOfVertices < 3) continue;

                var points = new List<Point2d>();
                for (int i = 0; i < polyline.NumberOfVertices; i++)
                {
                    Point2d point;
                    try { point = polyline.GetPoint2dAt(i); }
                    catch { continue; }
                    if (!Finite(point)) continue;
                    if (points.Count == 0 || Distance(points[points.Count - 1], point) > SnapTolerance * 0.1)
                        points.Add(point);
                }
                if (points.Count >= 2 && Distance(points[0], points[points.Count - 1]) <= SnapTolerance * 0.1)
                    points.RemoveAt(points.Count - 1);
                if (points.Count < 3) continue;

                double minX = points.Min(point => point.X);
                double maxX = points.Max(point => point.X);
                double minY = points.Min(point => point.Y);
                double maxY = points.Max(point => point.Y);
                if (maxX - minX <= Tol || maxY - minY <= Tol) continue;
                result.Add(new ParcelInfo(index++, id, points, PolygonCentroid(points)));
            }
            return result;
        }

        private static Graph BuildGraph(List<ParcelInfo> parcels)
        {
            var graph = new Graph();
            var nodes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var edges = new Dictionary<string, Edge>(StringComparer.OrdinalIgnoreCase);
            foreach (ParcelInfo parcel in parcels)
            {
                for (int i = 0; i < parcel.Points.Count; i++)
                {
                    Point2d p1 = parcel.Points[i];
                    Point2d p2 = parcel.Points[(i + 1) % parcel.Points.Count];
                    if (Distance(p1, p2) <= SnapTolerance * 0.1) continue;
                    int a = NodeId(graph, nodes, p1);
                    int b = NodeId(graph, nodes, p2);
                    if (a == b) continue;
                    string key = EdgeKey(graph.Nodes[a].Point, graph.Nodes[b].Point);
                    Edge edge;
                    if (!edges.TryGetValue(key, out edge))
                    {
                        edge = new Edge(graph.Edges.Count, a, b);
                        edges[key] = edge;
                        graph.Edges[edge.Id] = edge;
                        graph.Nodes[a].EdgeIds.Add(edge.Id);
                        graph.Nodes[b].EdgeIds.Add(edge.Id);
                    }
                    if (!edge.ParcelIds.Contains(parcel.Index)) edge.ParcelIds.Add(parcel.Index);
                    if (!parcel.EdgeIds.Contains(edge.Id)) parcel.EdgeIds.Add(edge.Id);
                    if (!parcel.NodeIds.Contains(a)) parcel.NodeIds.Add(a);
                    if (!parcel.NodeIds.Contains(b)) parcel.NodeIds.Add(b);
                }
            }
            foreach (Edge edge in graph.Edges.Values)
                edge.Kind = edge.ParcelIds.Count >= 2 ? "MIDBLOCK" : "ROAD_RESERVE";
            return graph;
        }

        private static int NodeId(Graph graph, Dictionary<string, int> lookup, Point2d point)
        {
            string key = PointKey(point);
            int id;
            if (lookup.TryGetValue(key, out id)) return id;
            id = graph.Nodes.Count;
            graph.Nodes[id] = new Node(id, point);
            lookup[key] = id;
            return id;
        }

        private static Dictionary<int, double> SampleNodeElevations(Graph graph, Surface surface)
        {
            var result = new Dictionary<int, double>();
            foreach (Node node in graph.Nodes.Values)
            {
                double elevation;
                if (TryElevation(surface, node.Point, out elevation)) result[node.Id] = elevation;
            }
            return result;
        }

        private static LowPointResult ResolveLowPoint(
            List<ParcelInfo> parcels, Graph graph, Surface surface, Dictionary<int, double> elevations)
        {
            var samples = new List<SurfaceSample>();
            foreach (Node node in graph.Nodes.Values)
            {
                double z;
                if (elevations.TryGetValue(node.Id, out z)) samples.Add(new SurfaceSample(node.Point, z));
            }
            foreach (ParcelInfo parcel in parcels)
            {
                double z;
                if (TryElevation(surface, parcel.Center, out z)) samples.Add(new SurfaceSample(parcel.Center, z));
                for (int i = 0; i < parcel.Points.Count; i++)
                {
                    Point2d a = parcel.Points[i];
                    Point2d b = parcel.Points[(i + 1) % parcel.Points.Count];
                    Point2d mid = new Point2d((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5);
                    if (TryElevation(surface, mid, out z)) samples.Add(new SurfaceSample(mid, z));
                }
            }
            if (samples.Count == 0) return new LowPointResult();
            SurfaceSample minimum = samples.OrderBy(sample => sample.Elevation).First();

            int outlet = -1;
            double best = double.MaxValue;
            foreach (Node node in graph.Nodes.Values)
            {
                double z;
                if (!elevations.TryGetValue(node.Id, out z)) continue;
                double score = Distance(node.Point, minimum.Point) + Math.Max(0.0, z - minimum.Elevation) * 5.0;
                if (score < best) { best = score; outlet = node.Id; }
            }
            if (outlet < 0) return new LowPointResult();
            return new LowPointResult
            {
                Valid = true,
                NodeId = outlet,
                Point = graph.Nodes[outlet].Point,
                Elevation = elevations[outlet],
                SurfaceMinimumSample = minimum.Point,
                SurfaceMinimumElevation = minimum.Elevation
            };
        }

        private static RoutingResult BuildRouting(
            Graph graph, List<ParcelInfo> parcels, Dictionary<int, double> elevations,
            int outletNode, string preference)
        {
            var distance = graph.Nodes.Keys.ToDictionary(id => id, id => double.MaxValue);
            var next = new Dictionary<int, int>();
            var visited = new HashSet<int>();
            distance[outletNode] = 0.0;

            while (visited.Count < graph.Nodes.Count)
            {
                int current = -1;
                double currentDistance = double.MaxValue;
                foreach (KeyValuePair<int, double> pair in distance)
                {
                    if (visited.Contains(pair.Key)) continue;
                    if (pair.Value < currentDistance)
                    {
                        current = pair.Key;
                        currentDistance = pair.Value;
                    }
                }
                if (current < 0 || currentDistance == double.MaxValue) break;
                visited.Add(current);

                foreach (int edgeId in graph.Nodes[current].EdgeIds)
                {
                    Edge edge;
                    if (!graph.Edges.TryGetValue(edgeId, out edge)) continue;
                    int neighbor = edge.A == current ? edge.B : edge.A;
                    if (visited.Contains(neighbor)) continue;
                    double candidate = currentDistance + EdgeCost(graph, edge, current, neighbor, elevations, preference);
                    if (candidate + 1e-9 < distance[neighbor])
                    {
                        distance[neighbor] = candidate;
                        next[neighbor] = current;
                    }
                }
            }

            var selected = new HashSet<int>();
            int served = 0;
            foreach (ParcelInfo parcel in parcels)
            {
                Edge serviceEdge = null;
                double serviceCost = double.MaxValue;
                int downstream = -1;
                foreach (int edgeId in parcel.EdgeIds)
                {
                    Edge edge;
                    if (!graph.Edges.TryGetValue(edgeId, out edge)) continue;
                    double da;
                    double db;
                    if (!distance.TryGetValue(edge.A, out da)) da = double.MaxValue;
                    if (!distance.TryGetValue(edge.B, out db)) db = double.MaxValue;
                    double d = Math.Min(da, db);
                    if (d == double.MaxValue) continue;
                    double value = d + Distance(graph.Nodes[edge.A].Point, graph.Nodes[edge.B].Point) * 0.5;
                    if (value < serviceCost)
                    {
                        serviceCost = value;
                        serviceEdge = edge;
                        downstream = da <= db ? edge.A : edge.B;
                    }
                }
                if (serviceEdge == null || downstream < 0) continue;
                served++;
                selected.Add(serviceEdge.Id);

                int node = downstream;
                int guard = 0;
                while (node != outletNode && guard++ <= graph.Nodes.Count + 5)
                {
                    int lower;
                    if (!next.TryGetValue(node, out lower)) break;
                    int edgeId = FindEdge(graph, node, lower);
                    if (edgeId >= 0) selected.Add(edgeId);
                    node = lower;
                }
            }

            return new RoutingResult
            {
                SelectedEdgeIds = selected,
                ServedParcels = served,
                DistanceToOutlet = distance,
                NextTowardOutlet = next
            };
        }

        private static double EdgeCost(
            Graph graph, Edge edge, int downstream, int upstream,
            Dictionary<int, double> elevations, string preference)
        {
            double length = Math.Max(Distance(graph.Nodes[edge.A].Point, graph.Nodes[edge.B].Point), 0.001);
            double typeFactor = 1.0;
            if (string.Equals(preference, "Prefer midblock", StringComparison.OrdinalIgnoreCase))
                typeFactor = string.Equals(edge.Kind, "MIDBLOCK", StringComparison.OrdinalIgnoreCase) ? 0.90 : 1.10;
            else if (string.Equals(preference, "Prefer road reserve", StringComparison.OrdinalIgnoreCase))
                typeFactor = string.Equals(edge.Kind, "ROAD_RESERVE", StringComparison.OrdinalIgnoreCase) ? 0.90 : 1.10;

            double downZ;
            double upZ;
            double adverse = 0.0;
            if (elevations.TryGetValue(downstream, out downZ) && elevations.TryGetValue(upstream, out upZ))
                adverse = Math.Max(0.0, downZ - upZ);
            return length * typeFactor + adverse * 1000.0 + (adverse > 0.01 ? length * 20.0 : 0.0);
        }

        private static int FindEdge(Graph graph, int a, int b)
        {
            foreach (int edgeId in graph.Nodes[a].EdgeIds)
            {
                Edge edge;
                if (!graph.Edges.TryGetValue(edgeId, out edge)) continue;
                if ((edge.A == a && edge.B == b) || (edge.A == b && edge.B == a)) return edgeId;
            }
            return -1;
        }

        private static Dictionary<int, Vector2d> BuildNodeShifts(
            Graph graph, List<ParcelInfo> parcels, IEnumerable<int> selectedEdgeIds,
            Surface surface, Point2d lowPoint, double midblockOffset, double roadOffset)
        {
            var sums = new Dictionary<int, Vector2d>();
            var counts = new Dictionary<int, int>();
            foreach (int edgeId in selectedEdgeIds)
            {
                Edge edge;
                if (!graph.Edges.TryGetValue(edgeId, out edge)) continue;
                Point2d a = graph.Nodes[edge.A].Point;
                Point2d b = graph.Nodes[edge.B].Point;
                Vector2d direction = new Vector2d(b.X - a.X, b.Y - a.Y);
                if (direction.Length <= Tol) continue;
                direction = direction.GetNormal();
                Vector2d left = new Vector2d(-direction.Y, direction.X);
                double offset = string.Equals(edge.Kind, "MIDBLOCK", StringComparison.OrdinalIgnoreCase)
                    ? midblockOffset : roadOffset;
                if (offset <= Tol) continue;
                Point2d mid = new Point2d((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5);
                Vector2d normal = OffsetNormal(edge, parcels, surface, mid, left, lowPoint);
                Vector2d shift = normal.MultiplyBy(offset);
                AddShift(sums, counts, edge.A, shift);
                AddShift(sums, counts, edge.B, shift);
            }
            var result = new Dictionary<int, Vector2d>();
            foreach (KeyValuePair<int, Vector2d> pair in sums)
            {
                int count;
                if (counts.TryGetValue(pair.Key, out count) && count > 0)
                    result[pair.Key] = pair.Value.MultiplyBy(1.0 / count);
            }
            return result;
        }

        private static Vector2d OffsetNormal(
            Edge edge, List<ParcelInfo> parcels, Surface surface,
            Point2d midpoint, Vector2d left, Point2d lowPoint)
        {
            Vector2d right = new Vector2d(-left.X, -left.Y);
            if (string.Equals(edge.Kind, "ROAD_RESERVE", StringComparison.OrdinalIgnoreCase) && edge.ParcelIds.Count > 0)
            {
                ParcelInfo parcel = parcels.FirstOrDefault(value => value.Index == edge.ParcelIds[0]);
                if (parcel != null)
                {
                    Point2d lp = midpoint + left;
                    Point2d rp = midpoint + right;
                    return Distance(lp, parcel.Center) >= Distance(rp, parcel.Center) ? left : right;
                }
            }
            if (edge.ParcelIds.Count >= 2)
            {
                ParcelInfo p1 = parcels.FirstOrDefault(value => value.Index == edge.ParcelIds[0]);
                ParcelInfo p2 = parcels.FirstOrDefault(value => value.Index == edge.ParcelIds[1]);
                if (p1 != null && p2 != null)
                {
                    double z1;
                    double z2;
                    bool h1 = TryElevation(surface, p1.Center, out z1);
                    bool h2 = TryElevation(surface, p2.Center, out z2);
                    Point2d target = h1 && h2 ? (z1 <= z2 ? p1.Center : p2.Center) : lowPoint;
                    return Distance(midpoint + left, target) <= Distance(midpoint + right, target) ? left : right;
                }
            }
            return Distance(midpoint + left, lowPoint) <= Distance(midpoint + right, lowPoint) ? left : right;
        }

        private static void AddShift(Dictionary<int, Vector2d> sums, Dictionary<int, int> counts, int id, Vector2d shift)
        {
            Vector2d current;
            if (!sums.TryGetValue(id, out current)) current = new Vector2d(0.0, 0.0);
            sums[id] = current + shift;
            int count;
            counts.TryGetValue(id, out count);
            counts[id] = count + 1;
        }

        private static Dictionary<int, int> BuildSelectedDegree(Graph graph, IEnumerable<int> edgeIds)
        {
            var result = new Dictionary<int, int>();
            foreach (int edgeId in edgeIds)
            {
                Edge edge;
                if (!graph.Edges.TryGetValue(edgeId, out edge)) continue;
                int count;
                result.TryGetValue(edge.A, out count);
                result[edge.A] = count + 1;
                result.TryGetValue(edge.B, out count);
                result[edge.B] = count + 1;
            }
            return result;
        }

        private static int ResolveUpstream(Edge edge, Dictionary<int, double> distance, Dictionary<int, int> next)
        {
            int target;
            if (next.TryGetValue(edge.A, out target) && target == edge.B) return edge.A;
            if (next.TryGetValue(edge.B, out target) && target == edge.A) return edge.B;
            double da;
            double db;
            if (!distance.TryGetValue(edge.A, out da)) da = double.MaxValue;
            if (!distance.TryGetValue(edge.B, out db)) db = double.MaxValue;
            return da >= db ? edge.A : edge.B;
        }

        private static List<Point2d> SplitBySpacing(Point2d start, Point2d end, double spacing)
        {
            var result = new List<Point2d> { start };
            double length = Distance(start, end);
            int pieces = Math.Max(1, (int)Math.Ceiling(length / Math.Max(spacing, 1.0)));
            for (int i = 1; i < pieces; i++)
            {
                double t = (double)i / pieces;
                result.Add(new Point2d(start.X + (end.X - start.X) * t, start.Y + (end.Y - start.Y) * t));
            }
            result.Add(end);
            return result;
        }

        private static Point2d MoveToward(Point2d from, Point2d to, double distance)
        {
            Vector2d vector = new Vector2d(to.X - from.X, to.Y - from.Y);
            if (vector.Length <= Tol) return from;
            return from + vector.GetNormal().MultiplyBy(distance);
        }

        private static Point2d Shifted(Point2d point, Dictionary<int, Vector2d> shifts, int id)
        {
            Vector2d shift;
            return shifts.TryGetValue(id, out shift) ? point + shift : point;
        }

        private static bool TryElevation(Surface surface, Point2d point, out double elevation)
        {
            elevation = double.NaN;
            try
            {
                elevation = surface.FindElevationAtXY(point.X, point.Y);
                return !double.IsNaN(elevation) && !double.IsInfinity(elevation);
            }
            catch { return false; }
        }

        private static void AddAnalysisMarker(
            Database database, Transaction transaction, BlockTableRecord space, ObjectId layerId,
            Point2d point, string title, double elevation, double diameter)
        {
            var circle = new Circle(new Point3d(point.X, point.Y, 0.0), Vector3d.ZAxis, Math.Max(diameter, 1.0));
            circle.SetDatabaseDefaults(database);
            circle.LayerId = layerId;
            space.AppendEntity(circle);
            transaction.AddNewlyCreatedDBObject(circle, true);
            var text = new DBText
            {
                Position = new Point3d(point.X + Math.Max(diameter, 1.0), point.Y + Math.Max(diameter, 1.0), 0.0),
                TextString = title + "  EL=" + elevation.ToString("0.###", CultureInfo.InvariantCulture),
                Height = Math.Max(PaperAnnotationScale.ModelTextHeight(database, 2.5), 0.001),
                LayerId = layerId
            };
            text.SetDatabaseDefaults(database);
            space.AppendEntity(text);
            transaction.AddNewlyCreatedDBObject(text, true);
        }

        private static ObjectId EnsureLayer(Database database, Transaction transaction, string name)
        {
            LayerTable layers = transaction.GetObject(database.LayerTableId, OpenMode.ForRead, false) as LayerTable;
            if (layers == null) return ObjectId.Null;
            if (layers.Has(name)) return layers[name];
            layers.UpgradeOpen();
            var layer = new LayerTableRecord { Name = name };
            ObjectId id = layers.Add(layer);
            transaction.AddNewlyCreatedDBObject(layer, true);
            return id;
        }

        private static void WriteLink(
            Polyline route, Transaction transaction, Edge edge, ObjectId surfaceId,
            LowPointResult low, int routeNumber)
        {
            if (route.ExtensionDictionary.IsNull) route.CreateExtensionDictionary();
            DBDictionary dictionary = transaction.GetObject(route.ExtensionDictionary, OpenMode.ForWrite, false) as DBDictionary;
            if (dictionary == null) return;
            Xrecord record;
            if (dictionary.Contains(LinkKey))
                record = transaction.GetObject(dictionary.GetAt(LinkKey), OpenMode.ForWrite, false) as Xrecord;
            else
            {
                record = new Xrecord();
                dictionary.SetAt(LinkKey, record);
                transaction.AddNewlyCreatedDBObject(record, true);
            }
            record.Data = new ResultBuffer(
                new TypedValue((int)DxfCode.Text, edge.Kind ?? string.Empty),
                new TypedValue((int)DxfCode.Text, surfaceId.Handle.ToString()),
                new TypedValue((int)DxfCode.Real, low.Point.X),
                new TypedValue((int)DxfCode.Real, low.Point.Y),
                new TypedValue((int)DxfCode.Real, low.Elevation),
                new TypedValue((int)DxfCode.Int32, routeNumber));
        }

        private static void EraseExisting(BlockTableRecord space, Transaction transaction)
        {
            foreach (ObjectId id in space.Cast<ObjectId>().ToList())
            {
                Entity entity;
                try { entity = transaction.GetObject(id, OpenMode.ForWrite, false) as Entity; }
                catch { continue; }
                if (entity == null) continue;
                if (string.Equals(entity.Layer, MidblockLayer, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(entity.Layer, RoadReserveLayer, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(entity.Layer, ManholeLayer, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(entity.Layer, AnalysisLayer, StringComparison.OrdinalIgnoreCase))
                {
                    try { entity.Erase(); }
                    catch { }
                }
            }
        }

        private static Point2d PolygonCentroid(IList<Point2d> points)
        {
            double twiceArea = 0.0;
            double cx = 0.0;
            double cy = 0.0;
            for (int i = 0; i < points.Count; i++)
            {
                Point2d a = points[i];
                Point2d b = points[(i + 1) % points.Count];
                double cross = a.X * b.Y - b.X * a.Y;
                twiceArea += cross;
                cx += (a.X + b.X) * cross;
                cy += (a.Y + b.Y) * cross;
            }
            if (Math.Abs(twiceArea) <= Tol)
                return new Point2d(points.Average(point => point.X), points.Average(point => point.Y));
            return new Point2d(cx / (3.0 * twiceArea), cy / (3.0 * twiceArea));
        }

        private static string PointKey(Point2d point)
        {
            long x = (long)Math.Round(point.X / SnapTolerance, MidpointRounding.AwayFromZero);
            long y = (long)Math.Round(point.Y / SnapTolerance, MidpointRounding.AwayFromZero);
            return x.ToString(CultureInfo.InvariantCulture) + ":" + y.ToString(CultureInfo.InvariantCulture);
        }

        private static string EdgeKey(Point2d a, Point2d b)
        {
            string first = PointKey(a);
            string second = PointKey(b);
            return string.CompareOrdinal(first, second) <= 0 ? first + "|" + second : second + "|" + first;
        }

        private static bool Finite(Point2d point)
        {
            return !double.IsNaN(point.X) && !double.IsInfinity(point.X) &&
                   !double.IsNaN(point.Y) && !double.IsInfinity(point.Y);
        }

        private static double Distance(Point2d a, Point2d b)
        {
            double dx = a.X - b.X;
            double dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private sealed class ParcelInfo
        {
            internal ParcelInfo(int index, ObjectId id, List<Point2d> points, Point2d center)
            {
                Index = index;
                Id = id;
                Points = points;
                Center = center;
                NodeIds = new List<int>();
                EdgeIds = new List<int>();
            }
            internal int Index { get; private set; }
            internal ObjectId Id { get; private set; }
            internal List<Point2d> Points { get; private set; }
            internal Point2d Center { get; private set; }
            internal List<int> NodeIds { get; private set; }
            internal List<int> EdgeIds { get; private set; }
        }

        private sealed class Node
        {
            internal Node(int id, Point2d point) { Id = id; Point = point; EdgeIds = new List<int>(); }
            internal int Id { get; private set; }
            internal Point2d Point { get; private set; }
            internal List<int> EdgeIds { get; private set; }
        }

        private sealed class Edge
        {
            internal Edge(int id, int a, int b)
            {
                Id = id; A = a; B = b; ParcelIds = new List<int>(); Kind = string.Empty;
            }
            internal int Id { get; private set; }
            internal int A { get; private set; }
            internal int B { get; private set; }
            internal List<int> ParcelIds { get; private set; }
            internal string Kind { get; set; }
        }

        private sealed class Graph
        {
            internal Graph() { Nodes = new Dictionary<int, Node>(); Edges = new Dictionary<int, Edge>(); }
            internal Dictionary<int, Node> Nodes { get; private set; }
            internal Dictionary<int, Edge> Edges { get; private set; }
        }

        private sealed class SurfaceSample
        {
            internal SurfaceSample(Point2d point, double elevation) { Point = point; Elevation = elevation; }
            internal Point2d Point { get; private set; }
            internal double Elevation { get; private set; }
        }

        private sealed class LowPointResult
        {
            internal LowPointResult() { NodeId = -1; Elevation = double.NaN; SurfaceMinimumElevation = double.NaN; }
            internal bool Valid { get; set; }
            internal int NodeId { get; set; }
            internal Point2d Point { get; set; }
            internal double Elevation { get; set; }
            internal Point2d SurfaceMinimumSample { get; set; }
            internal double SurfaceMinimumElevation { get; set; }
        }

        private sealed class RoutingResult
        {
            internal RoutingResult()
            {
                SelectedEdgeIds = new HashSet<int>();
                DistanceToOutlet = new Dictionary<int, double>();
                NextTowardOutlet = new Dictionary<int, int>();
            }
            internal HashSet<int> SelectedEdgeIds { get; set; }
            internal int ServedParcels { get; set; }
            internal Dictionary<int, double> DistanceToOutlet { get; set; }
            internal Dictionary<int, int> NextTowardOutlet { get; set; }
        }
    }
}

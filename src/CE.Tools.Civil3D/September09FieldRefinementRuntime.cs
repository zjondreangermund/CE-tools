using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil;
using Autodesk.Civil.ApplicationServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilFeatureLine = Autodesk.Civil.DatabaseServices.FeatureLine;
using CivilGeneralSegmentLabel = Autodesk.Civil.DatabaseServices.GeneralSegmentLabel;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;
using CivilSurfaceSlopeLabel = Autodesk.Civil.DatabaseServices.SurfaceSlopeLabel;
using CivilLabelStyle = Autodesk.Civil.DatabaseServices.Styles.LabelStyle;

namespace CETools.Civil3D
{
    /// <summary>
    /// September 09 field refinements requested after Civil 3D field testing.
    /// This runtime owns no CommandMethod attributes; existing unique command
    /// front doors are routed here by the final staged build boundary.
    /// </summary>
    internal static class September09FieldRefinementRuntime
    {
        private const double Tol = 1e-7;
        private const double JunctionTolerance = 0.01;
        private const string RoadCentreLayer = "CE-ROAD-CENTRELINE";
        private const string CrossfallLayer = "CE-SLOPE-CROSSFALL";
        private static double _lastFilletRadius = 0.0;
        private static double _lastFilletSearch = 20.0;

        // -----------------------------------------------------------------
        // GRID DIFFERENCE - same cell presentation as DESIGN LEVEL
        // -----------------------------------------------------------------
        internal static bool EnsureStyledGridDifferenceColumn(Table table)
        {
            if (table == null) return false;
            bool changed = September09FieldEngineeringRuntime.EnsureGridDifferenceColumn(table);

            int headerRow;
            int designColumn;
            int differenceColumn;
            if (!FindDifferenceColumns(table, out headerRow, out designColumn, out differenceColumn))
                return changed;

            for (int row = headerRow; row < table.Rows.Count; row++)
            {
                try
                {
                    // Copy the visible text presentation from DESIGN LEVEL so the
                    // inserted DIFFERENCE column never falls back to tiny defaults.
                    table.Cells[row, differenceColumn].TextHeight = table.Cells[row, designColumn].TextHeight;
                    table.Cells[row, differenceColumn].TextStyleId = table.Cells[row, designColumn].TextStyleId;
                    table.Cells[row, differenceColumn].Alignment = table.Cells[row, designColumn].Alignment;
                }
                catch { }
            }
            return true;
        }

        internal static int EnsureStyledGridDifferenceColumns(Document document)
        {
            if (document == null) return 0;
            int changed = 0;
            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTable table = transaction.GetObject(document.Database.BlockTableId, OpenMode.ForRead, false) as BlockTable;
                if (table == null) return 0;
                foreach (ObjectId recordId in table)
                {
                    BlockTableRecord record = null;
                    try { record = transaction.GetObject(recordId, OpenMode.ForRead, false) as BlockTableRecord; }
                    catch { }
                    if (record == null) continue;
                    foreach (ObjectId id in record)
                    {
                        Table candidate = null;
                        try { candidate = transaction.GetObject(id, OpenMode.ForWrite, false) as Table; }
                        catch { }
                        if (candidate == null) continue;
                        int header;
                        int design;
                        int difference;
                        if (!FindDifferenceColumnsOrGrid(candidate, out header, out design, out difference)) continue;
                        if (EnsureStyledGridDifferenceColumn(candidate)) changed++;
                    }
                }
                transaction.Commit();
            }
            return changed;
        }

        private static bool FindDifferenceColumnsOrGrid(Table table, out int headerRow, out int designColumn, out int differenceColumn)
        {
            if (FindDifferenceColumns(table, out headerRow, out designColumn, out differenceColumn)) return true;
            headerRow = -1;
            designColumn = -1;
            differenceColumn = -1;
            int rows = Math.Min(5, table.Rows.Count);
            for (int row = 0; row < rows; row++)
            {
                bool ng = false;
                for (int col = 0; col < table.Columns.Count; col++)
                {
                    string text = CellText(table, row, col).Trim().ToUpperInvariant();
                    if (text == "NG LEVEL") ng = true;
                    if (text == "DESIGN LEVEL") designColumn = col;
                    if (text == "DIFFERENCE") differenceColumn = col;
                }
                if (ng && designColumn >= 0)
                {
                    headerRow = row;
                    return true;
                }
            }
            return false;
        }

        private static bool FindDifferenceColumns(Table table, out int headerRow, out int designColumn, out int differenceColumn)
        {
            headerRow = -1;
            designColumn = -1;
            differenceColumn = -1;
            int rows = Math.Min(5, table.Rows.Count);
            for (int row = 0; row < rows; row++)
            {
                int design = -1;
                int difference = -1;
                for (int col = 0; col < table.Columns.Count; col++)
                {
                    string text = CellText(table, row, col).Trim().ToUpperInvariant();
                    if (text == "DESIGN LEVEL") design = col;
                    else if (text == "DIFFERENCE") difference = col;
                }
                if (design >= 0 && difference >= 0)
                {
                    headerRow = row;
                    designColumn = design;
                    differenceColumn = difference;
                    return true;
                }
            }
            return false;
        }

        private static string CellText(Table table, int row, int col)
        {
            try { return table.Cells[row, col].TextString ?? string.Empty; }
            catch { return string.Empty; }
        }

        // -----------------------------------------------------------------
        // ROAD RESERVE CENTRES - closed/open source boundaries, joined network,
        // exact shared junction nodes and optional radius fillets at direction bends.
        // -----------------------------------------------------------------
        internal static void RoadReserveCentrePolylines(Document document)
        {
            if (document == null) return;
            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Road Reserve Centre Polylines",
                "Create joined road-centre polylines from closed cadastral/erf boundaries, open/normal road-boundary polylines/lines, or both. T/X junction endpoints are snapped to one shared point; direction-change vertices can be filleted automatically.");
            settings.AddChoice("SourceMode", "01 Sources", "Source geometry", "Closed + open boundaries",
                "Closed = cadastral/erf parcel outlines. Open = ordinary open LINE/LWPOLYLINE road-reserve edges. Both accepts either type.",
                new[] { "Closed cadastral/erf boundaries", "Open/normal road boundaries", "Closed + open boundaries" });
            settings.AddPositiveDouble("MinWidth", "02 Reserve detection", "Minimum road reserve width", 4.0,
                "Ignore opposite boundaries closer than this plan distance.");
            settings.AddPositiveDouble("MaxWidth", "02 Reserve detection", "Maximum road reserve width", 40.0,
                "Ignore opposite boundaries farther apart than this plan distance.");
            settings.AddPositiveDouble("Angle", "02 Reserve detection", "Maximum parallel angle (degrees)", 8.0,
                "Direction tolerance for paired road-reserve sides.");
            settings.AddPositiveDouble("Overlap", "02 Reserve detection", "Minimum common edge length", 4.0,
                "Minimum projected overlap required before a centre segment is accepted.");
            settings.AddPositiveDouble("Join", "03 Junctions", "Junction join/extension distance", 20.0,
                "Extend nearby centre endpoints to their support-line crossing, then split and share exact T/X junction nodes.");
            settings.AddDouble("Radius", "03 Junctions", "Centreline fillet radius", 0.0,
                "Radius applied at every unambiguous direction-change vertex. Use 0 for sharp joined corners.");
            settings.AddChoice("Facing", "02 Reserve detection", "Closed-boundary pairing", "Facing parcel sides only",
                "For closed parcel data, reject same-side parallel edges by checking parcel-centroid facing. Open sources are paired geometrically.",
                new[] { "Facing parcel sides only", "All parallel boundaries" });
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;

            double minWidth = Math.Max(0.001, settings.Double("MinWidth", 4.0));
            double maxWidth = Math.Max(0.001, settings.Double("MaxWidth", 40.0));
            if (maxWidth < minWidth) { double swap = maxWidth; maxWidth = minWidth; minWidth = swap; }
            double angle = Math.Max(0.1, Math.Min(30.0, settings.Double("Angle", 8.0)));
            double overlap = Math.Max(0.001, settings.Double("Overlap", 4.0));
            double join = Math.Max(0.001, settings.Double("Join", 20.0));
            double radius = Math.Max(0.0, settings.Double("Radius", 0.0));
            string sourceMode = settings.Text("SourceMode");
            bool closedAllowed = !string.Equals(sourceMode, "Open/normal road boundaries", StringComparison.OrdinalIgnoreCase);
            bool openAllowed = !string.Equals(sourceMode, "Closed cadastral/erf boundaries", StringComparison.OrdinalIgnoreCase);
            bool facingOnly = !string.Equals(settings.Text("Facing"), "All parallel boundaries", StringComparison.OrdinalIgnoreCase);

            List<ObjectId> selected = SelectBoundaryObjects(document, closedAllowed, openAllowed);
            if (selected.Count < 2)
            {
                document.Editor.WriteMessage("\nCE_ROADRESERVECENTRELINES requires at least two supported boundary objects.");
                return;
            }

            List<BoundarySource> sources;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                sources = ReadBoundarySources(transaction, selected, closedAllowed, openAllowed);
            if (sources.Count < 2)
            {
                document.Editor.WriteMessage("\nNo usable road-reserve boundary geometry was found.");
                return;
            }

            List<NetworkSegment> candidates = BuildCentreCandidates(sources, minWidth, maxWidth, angle, overlap, facingOnly);
            candidates = DeduplicateSegments(candidates, 0.10);
            ExtendCentreEndpoints(candidates, join);
            List<NetworkSegment> split = SplitAtAllIntersections(candidates);
            SnapNetworkNodes(split, Math.Max(0.01, Math.Min(0.10, join * 0.01)));
            List<List<Point2d>> chains = TraceNetworkChains(split);

            if (chains.Count == 0)
            {
                document.Editor.WriteMessage("\nNo centreline network matched the selected road-reserve settings.");
                return;
            }

            int created = 0;
            int filleted = 0;
            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                ObjectId layerId = GetOrCreateLayer(document.Database, transaction, RoadCentreLayer, 1);
                BlockTableRecord space = transaction.GetObject(document.Database.CurrentSpaceId, OpenMode.ForWrite, false) as BlockTableRecord;
                if (space == null) return;
                foreach (List<Point2d> chain in chains)
                {
                    if (chain.Count < 2) continue;
                    Polyline polyline = BuildFilletedPolyline(chain, radius, out int bendsFilleted);
                    if (polyline == null || polyline.NumberOfVertices < 2) continue;
                    polyline.SetDatabaseDefaults(document.Database);
                    polyline.LayerId = layerId;
                    polyline.Color = Color.FromColorIndex(ColorMethod.ByLayer, 256);
                    space.AppendEntity(polyline);
                    transaction.AddNewlyCreatedDBObject(polyline, true);
                    created++;
                    filleted += bendsFilleted;
                }
                transaction.Commit();
            }
            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_ROADRESERVECENTRELINES JOINED complete. Sources={0}; joined centre polylines={1}; filleted direction changes={2}; T/X junctions share exact endpoints; source boundaries unchanged.",
                sources.Count, created, filleted);
        }

        private static List<ObjectId> SelectBoundaryObjects(Document document, bool closedAllowed, bool openAllowed)
        {
            List<ObjectId> result = new List<ObjectId>();
            PromptSelectionResult implied = document.Editor.SelectImplied();
            if (implied.Status == PromptStatus.OK && implied.Value != null)
                result.AddRange(FilterBoundaryObjects(document.Database, implied.Value.GetObjectIds(), closedAllowed, openAllowed));
            if (result.Count >= 2) return result.Distinct().ToList();

            var options = new PromptSelectionOptions
            {
                MessageForAdding = "\nSelect multiple closed/open road-reserve LINE/LWPOLYLINE boundaries: ",
                AllowDuplicates = false,
                RejectObjectsFromNonCurrentSpace = true
            };
            PromptSelectionResult selection = document.Editor.GetSelection(options);
            if (selection.Status != PromptStatus.OK || selection.Value == null) return new List<ObjectId>();
            return FilterBoundaryObjects(document.Database, selection.Value.GetObjectIds(), closedAllowed, openAllowed).Distinct().ToList();
        }

        private static List<ObjectId> FilterBoundaryObjects(Database database, IEnumerable<ObjectId> ids, bool closedAllowed, bool openAllowed)
        {
            var result = new List<ObjectId>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids ?? Enumerable.Empty<ObjectId>())
                {
                    Entity entity = null;
                    try { entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity; } catch { }
                    Line line = entity as Line;
                    if (line != null && openAllowed && line.Length > Tol) { result.Add(id); continue; }
                    Polyline polyline = entity as Polyline;
                    if (polyline == null || polyline.NumberOfVertices < 2) continue;
                    if (polyline.Closed ? closedAllowed : openAllowed) result.Add(id);
                }
            }
            return result;
        }

        private static List<BoundarySource> ReadBoundarySources(Transaction transaction, IList<ObjectId> ids, bool closedAllowed, bool openAllowed)
        {
            var result = new List<BoundarySource>();
            for (int sourceIndex = 0; sourceIndex < ids.Count; sourceIndex++)
            {
                Entity entity = null;
                try { entity = transaction.GetObject(ids[sourceIndex], OpenMode.ForRead, false) as Entity; } catch { }
                if (entity == null) continue;
                var source = new BoundarySource { Index = sourceIndex, Id = ids[sourceIndex] };
                Line line = entity as Line;
                if (line != null && openAllowed)
                {
                    source.Closed = false;
                    AddBoundarySegment(source, new Point2d(line.StartPoint.X, line.StartPoint.Y), new Point2d(line.EndPoint.X, line.EndPoint.Y));
                }
                else
                {
                    Polyline polyline = entity as Polyline;
                    if (polyline == null || (polyline.Closed ? !closedAllowed : !openAllowed)) continue;
                    source.Closed = polyline.Closed;
                    var vertices = new List<Point2d>();
                    for (int i = 0; i < polyline.NumberOfVertices; i++) vertices.Add(polyline.GetPoint2dAt(i));
                    source.Centroid = source.Closed ? PolygonCentroid(vertices) : Average(vertices);
                    int segmentCount = source.Closed ? polyline.NumberOfVertices : polyline.NumberOfVertices - 1;
                    for (int segment = 0; segment < segmentCount; segment++)
                    {
                        int next = (segment + 1) % polyline.NumberOfVertices;
                        bool curved = false;
                        try { curved = polyline.GetSegmentType(segment) != SegmentType.Line; } catch { }
                        int divisions = curved ? 12 : 1;
                        for (int part = 0; part < divisions; part++)
                        {
                            double p0 = segment + (double)part / divisions;
                            double p1 = segment + (double)(part + 1) / divisions;
                            Point3d a;
                            Point3d b;
                            try { a = polyline.GetPointAtParameter(p0); b = polyline.GetPointAtParameter(p1); }
                            catch { continue; }
                            AddBoundarySegment(source, new Point2d(a.X, a.Y), new Point2d(b.X, b.Y));
                        }
                    }
                }
                if (source.Segments.Count > 0)
                {
                    if (!source.Closed && source.Centroid == Point2d.Origin)
                        source.Centroid = Average(source.Segments.SelectMany(s => new[] { s.A, s.B }).ToList());
                    result.Add(source);
                }
            }
            return result;
        }

        private static void AddBoundarySegment(BoundarySource source, Point2d a, Point2d b)
        {
            if (Distance(a, b) <= Tol) return;
            source.Segments.Add(new BoundarySegment { Source = source, A = a, B = b });
        }

        private static List<NetworkSegment> BuildCentreCandidates(IList<BoundarySource> sources, double minWidth, double maxWidth, double angleDeg, double minOverlap, bool facingOnly)
        {
            var result = new List<NetworkSegment>();
            double cosine = Math.Cos(angleDeg * Math.PI / 180.0);
            for (int i = 0; i < sources.Count; i++)
            for (int j = i + 1; j < sources.Count; j++)
            {
                foreach (BoundarySegment a in sources[i].Segments)
                foreach (BoundarySegment b in sources[j].Segments)
                {
                    Vector2d da = a.B - a.A;
                    Vector2d db = b.B - b.A;
                    if (da.Length <= Tol || db.Length <= Tol) continue;
                    da = da.GetNormal();
                    db = db.GetNormal();
                    double dot = da.DotProduct(db);
                    if (Math.Abs(dot) < cosine) continue;
                    if (dot < 0.0) { Point2d swap = b.A; b = new BoundarySegment { Source = b.Source, A = b.B, B = swap }; db = -db; }

                    Point2d midA = Mid(a.A, a.B);
                    Point2d midB = Mid(b.A, b.B);
                    Vector2d normal = new Vector2d(-da.Y, da.X);
                    double width = Math.Abs((midB - midA).DotProduct(normal));
                    if (width < minWidth - Tol || width > maxWidth + Tol) continue;

                    double lenA = Distance(a.A, a.B);
                    double b0 = (b.A - a.A).DotProduct(da);
                    double b1 = (b.B - a.A).DotProduct(da);
                    double low = Math.Max(0.0, Math.Min(b0, b1));
                    double high = Math.Min(lenA, Math.Max(b0, b1));
                    if (high - low < minOverlap) continue;

                    if (facingOnly && a.Source.Closed && b.Source.Closed)
                    {
                        Vector2d towardB = midB - midA;
                        Vector2d towardA = midA - midB;
                        Vector2d outA = midA - a.Source.Centroid;
                        Vector2d outB = midB - b.Source.Centroid;
                        if (outA.DotProduct(towardB) <= 0.0 || outB.DotProduct(towardA) <= 0.0) continue;
                    }

                    Point2d a0 = a.A + da * low;
                    Point2d a1 = a.A + da * high;
                    Point2d bAt0;
                    Point2d bAt1;
                    if (!PointOnSupportAtProjection(b.A, db, a.A, da, low, out bAt0) ||
                        !PointOnSupportAtProjection(b.A, db, a.A, da, high, out bAt1)) continue;
                    Point2d c0 = Mid(a0, bAt0);
                    Point2d c1 = Mid(a1, bAt1);
                    if (Distance(c0, c1) > Tol) result.Add(new NetworkSegment(c0, c1));
                }
            }
            return result;
        }

        private static bool PointOnSupportAtProjection(Point2d basePoint, Vector2d direction, Point2d origin, Vector2d axis, double projection, out Point2d result)
        {
            double denominator = direction.DotProduct(axis);
            if (Math.Abs(denominator) <= Tol) { result = Point2d.Origin; return false; }
            double current = (basePoint - origin).DotProduct(axis);
            double t = (projection - current) / denominator;
            result = basePoint + direction * t;
            return true;
        }

        private static List<NetworkSegment> DeduplicateSegments(IList<NetworkSegment> input, double tolerance)
        {
            var result = new List<NetworkSegment>();
            foreach (NetworkSegment item in input.OrderByDescending(e => Distance(e.A, e.B)))
            {
                bool duplicate = result.Any(existing =>
                    (Distance(item.A, existing.A) <= tolerance && Distance(item.B, existing.B) <= tolerance) ||
                    (Distance(item.A, existing.B) <= tolerance && Distance(item.B, existing.A) <= tolerance));
                if (!duplicate) result.Add(new NetworkSegment(item.A, item.B));
            }
            return result;
        }

        private static void ExtendCentreEndpoints(IList<NetworkSegment> segments, double maximumDistance)
        {
            Point2d[] start = segments.Select(s => s.A).ToArray();
            Point2d[] end = segments.Select(s => s.B).ToArray();
            for (int i = 0; i < segments.Count; i++)
            {
                segments[i].A = ClosestSupportCrossing(i, start[i], start, end, maximumDistance);
                segments[i].B = ClosestSupportCrossing(i, end[i], start, end, maximumDistance);
            }
        }

        private static Point2d ClosestSupportCrossing(int index, Point2d endpoint, Point2d[] starts, Point2d[] ends, double maximumDistance)
        {
            Point2d best = endpoint;
            double bestDistance = maximumDistance + Tol;
            for (int j = 0; j < starts.Length; j++)
            {
                if (j == index) continue;
                Point2d intersection;
                if (!TryInfiniteIntersection(starts[index], ends[index], starts[j], ends[j], out intersection)) continue;
                double distance = Distance(endpoint, intersection);
                if (distance > bestDistance) continue;
                if (DistanceToSegment(intersection, starts[j], ends[j]) > maximumDistance + Tol) continue;
                bestDistance = distance;
                best = intersection;
            }
            return best;
        }

        private static List<NetworkSegment> SplitAtAllIntersections(IList<NetworkSegment> segments)
        {
            var stations = segments.Select(s => new List<double> { 0.0, 1.0 }).ToList();
            for (int i = 0; i < segments.Count; i++)
            for (int j = i + 1; j < segments.Count; j++)
            {
                double ti;
                double tj;
                Point2d point;
                if (!TrySegmentIntersection(segments[i].A, segments[i].B, segments[j].A, segments[j].B, out ti, out tj, out point)) continue;
                AddUnique(stations[i], ti, 1e-8);
                AddUnique(stations[j], tj, 1e-8);
            }

            var result = new List<NetworkSegment>();
            for (int i = 0; i < segments.Count; i++)
            {
                List<double> values = stations[i].OrderBy(v => v).ToList();
                for (int k = 0; k + 1 < values.Count; k++)
                {
                    if (values[k + 1] - values[k] <= 1e-8) continue;
                    Point2d a = Lerp(segments[i].A, segments[i].B, values[k]);
                    Point2d b = Lerp(segments[i].A, segments[i].B, values[k + 1]);
                    if (Distance(a, b) > Tol) result.Add(new NetworkSegment(a, b));
                }
            }
            return result;
        }

        private static void SnapNetworkNodes(IList<NetworkSegment> segments, double tolerance)
        {
            var nodes = new List<Point2d>();
            foreach (NetworkSegment segment in segments)
            {
                segment.A = GetOrAddNode(nodes, segment.A, tolerance);
                segment.B = GetOrAddNode(nodes, segment.B, tolerance);
            }
        }

        private static Point2d GetOrAddNode(IList<Point2d> nodes, Point2d point, double tolerance)
        {
            for (int i = 0; i < nodes.Count; i++) if (Distance(nodes[i], point) <= tolerance) return nodes[i];
            nodes.Add(point);
            return point;
        }

        private static List<List<Point2d>> TraceNetworkChains(IList<NetworkSegment> segments)
        {
            var nodes = new List<Point2d>();
            var edges = new List<GraphEdge>();
            double tolerance = 1e-5;
            foreach (NetworkSegment segment in segments)
            {
                int a = NodeIndex(nodes, segment.A, tolerance);
                int b = NodeIndex(nodes, segment.B, tolerance);
                if (a != b) edges.Add(new GraphEdge(a, b));
            }
            var adjacency = new Dictionary<int, List<int>>();
            for (int i = 0; i < edges.Count; i++)
            {
                AddAdjacency(adjacency, edges[i].A, i);
                AddAdjacency(adjacency, edges[i].B, i);
            }
            var used = new HashSet<int>();
            var chains = new List<List<Point2d>>();

            foreach (int startNode in adjacency.Keys.Where(n => adjacency[n].Count != 2).OrderBy(n => n))
            {
                foreach (int edgeIndex in adjacency[startNode].ToArray())
                {
                    if (used.Contains(edgeIndex)) continue;
                    List<Point2d> chain = TraceChain(startNode, edgeIndex, nodes, edges, adjacency, used);
                    if (chain.Count >= 2) chains.Add(chain);
                }
            }
            for (int edgeIndex = 0; edgeIndex < edges.Count; edgeIndex++)
            {
                if (used.Contains(edgeIndex)) continue;
                List<Point2d> chain = TraceChain(edges[edgeIndex].A, edgeIndex, nodes, edges, adjacency, used);
                if (chain.Count >= 2) chains.Add(chain);
            }
            return chains;
        }

        private static List<Point2d> TraceChain(int startNode, int startEdge, IList<Point2d> nodes, IList<GraphEdge> edges, IDictionary<int, List<int>> adjacency, ISet<int> used)
        {
            var points = new List<Point2d> { nodes[startNode] };
            int node = startNode;
            int edge = startEdge;
            while (edge >= 0 && !used.Contains(edge))
            {
                used.Add(edge);
                int next = edges[edge].Other(node);
                points.Add(nodes[next]);
                if (!adjacency.ContainsKey(next) || adjacency[next].Count != 2) break;
                int candidate = adjacency[next].FirstOrDefault(e => !used.Contains(e));
                if (used.Contains(candidate)) break;
                node = next;
                edge = candidate;
            }
            return RemoveConsecutiveDuplicates(points, 1e-6);
        }

        private static Polyline BuildFilletedPolyline(IList<Point2d> points, double radius, out int filleted)
        {
            filleted = 0;
            if (points == null || points.Count < 2) return null;
            var vertices = new List<FilletVertex>();
            vertices.Add(new FilletVertex(points[0], 0.0));
            for (int i = 1; i + 1 < points.Count; i++)
            {
                Point2d previous = points[i - 1];
                Point2d corner = points[i];
                Point2d next = points[i + 1];
                if (radius <= Tol)
                {
                    vertices.Add(new FilletVertex(corner, 0.0));
                    continue;
                }
                Vector2d toPrevious = previous - corner;
                Vector2d toNext = next - corner;
                if (toPrevious.Length <= Tol || toNext.Length <= Tol)
                {
                    vertices.Add(new FilletVertex(corner, 0.0));
                    continue;
                }
                toPrevious = toPrevious.GetNormal();
                toNext = toNext.GetNormal();
                double dot = Math.Max(-1.0, Math.Min(1.0, toPrevious.DotProduct(toNext)));
                double theta = Math.Acos(dot);
                if (theta <= 0.001 || Math.Abs(Math.PI - theta) <= 0.001)
                {
                    vertices.Add(new FilletVertex(corner, 0.0));
                    continue;
                }
                double tangent = radius / Math.Tan(theta * 0.5);
                double maxTangent = 0.49 * Math.Min(Distance(previous, corner), Distance(corner, next));
                if (tangent <= Tol || tangent >= maxTangent)
                {
                    vertices.Add(new FilletVertex(corner, 0.0));
                    continue;
                }
                Point2d tangentIn = corner + toPrevious * tangent;
                Point2d tangentOut = corner + toNext * tangent;
                Vector2d incoming = corner - previous;
                Vector2d outgoing = next - corner;
                double cross = incoming.X * outgoing.Y - incoming.Y * outgoing.X;
                double deflection = Math.PI - theta;
                double bulge = Math.Tan(deflection * 0.25) * (cross >= 0.0 ? 1.0 : -1.0);
                vertices.Add(new FilletVertex(tangentIn, bulge));
                vertices.Add(new FilletVertex(tangentOut, 0.0));
                filleted++;
            }
            vertices.Add(new FilletVertex(points[points.Count - 1], 0.0));

            var polyline = new Polyline(vertices.Count);
            for (int i = 0; i < vertices.Count; i++)
                polyline.AddVertexAt(i, vertices[i].Point, vertices[i].Bulge, 0.0, 0.0);
            return polyline;
        }

        // -----------------------------------------------------------------
        // MULTI FILLET - terminal support-line intersections, not nearest endpoint
        // pairs. This fixes arbitrary pairing on road-centre/construction networks.
        // -----------------------------------------------------------------
        internal static void MultiFillet(Document document)
        {
            if (document == null) return;
            PromptSelectionResult selection = SelectLinePolyline(document.Editor,
                "\nSelect multiple OPEN Lines/Polylines to fillet at their nearest valid support-line junctions: ");
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;
            ObjectId[] ids = selection.Value.GetObjectIds().Distinct().ToArray();
            if (ids.Length < 2) return;

            PromptDoubleOptions radiusOptions = new PromptDoubleOptions(
                string.Format(CultureInfo.InvariantCulture, "\nSpecify fillet radius <{0:0.###}>: ", _lastFilletRadius))
            {
                AllowNegative = false,
                AllowZero = true,
                DefaultValue = _lastFilletRadius,
                UseDefaultValue = true
            };
            PromptDoubleResult radius = document.Editor.GetDouble(radiusOptions);
            if (radius.Status != PromptStatus.OK) return;
            _lastFilletRadius = Math.Max(0.0, radius.Value);

            PromptDoubleOptions searchOptions = new PromptDoubleOptions(
                string.Format(CultureInfo.InvariantCulture, "\nMaximum endpoint-to-junction distance <{0:0.###}>: ", _lastFilletSearch))
            {
                AllowNegative = false,
                AllowZero = false,
                DefaultValue = _lastFilletSearch,
                UseDefaultValue = true
            };
            PromptDoubleResult search = document.Editor.GetDouble(searchOptions);
            if (search.Status != PromptStatus.OK) return;
            _lastFilletSearch = Math.Max(0.001, search.Value);

            List<Terminal> terminals = ReadTerminals(document.Database, ids);
            var candidates = new List<FilletCandidate>();
            for (int i = 0; i < terminals.Count; i++)
            for (int j = i + 1; j < terminals.Count; j++)
            {
                if (terminals[i].Id == terminals[j].Id) continue;
                Point2d crossing;
                if (!TryInfiniteIntersection(To2d(terminals[i].Point), To2d(terminals[i].Inner), To2d(terminals[j].Point), To2d(terminals[j].Inner), out crossing)) continue;
                double firstDistance = Distance(To2d(terminals[i].Point), crossing);
                double secondDistance = Distance(To2d(terminals[j].Point), crossing);
                if (firstDistance > _lastFilletSearch || secondDistance > _lastFilletSearch) continue;
                candidates.Add(new FilletCandidate(terminals[i], terminals[j], crossing, Math.Max(firstDistance, secondDistance)));
            }
            candidates = candidates.OrderBy(c => c.Score).ToList();

            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int completed = 0;
            int skipped = 0;
            foreach (FilletCandidate candidate in candidates)
            {
                string firstKey = TerminalKey(candidate.First);
                string secondKey = TerminalKey(candidate.Second);
                if (used.Contains(firstKey) || used.Contains(secondKey)) continue;
                string failure;
                if (ApplyFillet(document.Database, candidate, _lastFilletRadius, out failure))
                {
                    used.Add(firstKey);
                    used.Add(secondKey);
                    completed++;
                }
                else
                {
                    skipped++;
                    if (!string.IsNullOrWhiteSpace(failure)) document.Editor.WriteMessage("\nFillet skipped: {0}", failure);
                }
            }
            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_MULTIFILLET complete. Valid junction fillets={0}; skipped={1}; radius={2:0.###}. Support-line crossings, not arbitrary nearest endpoints, control pairing.",
                completed, skipped, _lastFilletRadius);
        }

        private static List<Terminal> ReadTerminals(Database database, IEnumerable<ObjectId> ids)
        {
            var result = new List<Terminal>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids)
                {
                    Entity entity = null;
                    try { entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity; } catch { }
                    Line line = entity as Line;
                    if (line != null && line.Length > Tol)
                    {
                        result.Add(new Terminal(id, true, line.StartPoint, line.EndPoint));
                        result.Add(new Terminal(id, false, line.EndPoint, line.StartPoint));
                        continue;
                    }
                    Polyline polyline = entity as Polyline;
                    if (polyline == null || polyline.Closed || polyline.NumberOfVertices < 2) continue;
                    if (TerminalSegmentStraight(polyline, true)) result.Add(new Terminal(id, true, polyline.StartPoint, polyline.GetPoint3dAt(1)));
                    if (TerminalSegmentStraight(polyline, false)) result.Add(new Terminal(id, false, polyline.EndPoint, polyline.GetPoint3dAt(polyline.NumberOfVertices - 2)));
                }
            }
            return result;
        }

        private static bool ApplyFillet(Database database, FilletCandidate candidate, double radius, out string failure)
        {
            failure = string.Empty;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                Entity first = transaction.GetObject(candidate.First.Id, OpenMode.ForWrite, false) as Entity;
                Entity second = transaction.GetObject(candidate.Second.Id, OpenMode.ForWrite, false) as Entity;
                if (first == null || second == null) { failure = "source unavailable"; return false; }
                Terminal a;
                Terminal b;
                if (!ReadCurrentTerminal(first, candidate.First.IsStart, out a) || !ReadCurrentTerminal(second, candidate.Second.IsStart, out b))
                { failure = "terminal segment is not straight/open"; return false; }
                Point2d crossing2d;
                if (!TryInfiniteIntersection(To2d(a.Point), To2d(a.Inner), To2d(b.Point), To2d(b.Inner), out crossing2d))
                { failure = "support lines are parallel"; return false; }
                Point3d crossing = new Point3d(crossing2d.X, crossing2d.Y, (a.Point.Z + b.Point.Z) * 0.5);
                Point3d targetA = crossing;
                Point3d targetB = crossing;
                Point3d center = Point3d.Origin;
                bool createArc = radius > Tol;
                if (createArc)
                {
                    Vector3d rayA = new Vector3d(a.Inner.X - crossing.X, a.Inner.Y - crossing.Y, 0.0);
                    Vector3d rayB = new Vector3d(b.Inner.X - crossing.X, b.Inner.Y - crossing.Y, 0.0);
                    if (rayA.Length <= Tol || rayB.Length <= Tol) { failure = "invalid terminal direction"; return false; }
                    rayA = rayA.GetNormal();
                    rayB = rayB.GetNormal();
                    double dot = Math.Max(-1.0, Math.Min(1.0, rayA.DotProduct(rayB)));
                    double theta = Math.Acos(dot);
                    if (theta <= 0.001 || Math.Abs(Math.PI - theta) <= 0.001) { failure = "unsupported tangent angle"; return false; }
                    double tangent = radius / Math.Tan(theta * 0.5);
                    double centreDistance = radius / Math.Sin(theta * 0.5);
                    if (tangent <= Tol || double.IsInfinity(tangent) || double.IsNaN(tangent)) { failure = "invalid radius"; return false; }
                    targetA = crossing + rayA * tangent;
                    targetB = crossing + rayB * tangent;
                    Vector3d bisector = rayA + rayB;
                    if (bisector.Length <= Tol) { failure = "angle bisector unavailable"; return false; }
                    center = crossing + bisector.GetNormal() * centreDistance;
                }
                SetTerminal(first, a.IsStart, targetA);
                SetTerminal(second, b.IsStart, targetB);
                if (createArc)
                {
                    double startAngle = Math.Atan2(targetA.Y - center.Y, targetA.X - center.X);
                    double endAngle = Math.Atan2(targetB.Y - center.Y, targetB.X - center.X);
                    double ccw = NormalizeAngle(endAngle - startAngle);
                    Arc arc = ccw <= Math.PI
                        ? new Arc(center, radius, startAngle, startAngle + ccw)
                        : new Arc(center, radius, endAngle, endAngle + NormalizeAngle(startAngle - endAngle));
                    arc.SetDatabaseDefaults(database);
                    CopyProperties(first, arc);
                    BlockTableRecord owner = transaction.GetObject(first.OwnerId, OpenMode.ForWrite, false) as BlockTableRecord;
                    if (owner == null) { arc.Dispose(); failure = "owner space unavailable"; return false; }
                    owner.AppendEntity(arc);
                    transaction.AddNewlyCreatedDBObject(arc, true);
                }
                transaction.Commit();
                return true;
            }
        }

        // -----------------------------------------------------------------
        // CUT ALL X/T JUNCTIONS - every selected LINE/LWPOLYLINE becomes separate
        // spans at every crossing. Original handle is retained as the first span.
        // -----------------------------------------------------------------
        internal static void CutAllJunctions(Document document)
        {
            if (document == null) return;
            PromptSelectionResult selection = SelectLinePolyline(document.Editor,
                "\nSelect multiple LINE/LWPOLYLINE routes to CUT at ALL crossings and T-junctions and KEEP every segment: ");
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;
            ObjectId[] ids = selection.Value.GetObjectIds().Distinct().ToArray();
            if (ids.Length < 2) return;
            List<CutRoute> routes = ReadCutRoutes(document.Database, ids);
            var cuts = routes.ToDictionary(r => r.Id, r => new List<double>());
            int junctions = 0;
            for (int i = 0; i < routes.Count; i++)
            for (int j = i + 1; j < routes.Count; j++)
            {
                bool found = CollectRouteJunctions(routes[i], routes[j], cuts[routes[i].Id], cuts[routes[j].Id]);
                if (found) junctions++;
            }
            int sources = 0;
            int additional = 0;
            int unchanged = 0;
            foreach (CutRoute route in routes)
            {
                NormalizeStations(cuts[route.Id], route.Length);
                if (cuts[route.Id].Count == 0) continue;
                int added;
                string failure;
                bool ok = route.IsLine
                    ? SplitLineAll(document.Database, route.Id, cuts[route.Id], out added, out failure)
                    : SplitPolylineAll(document.Database, route.Id, cuts[route.Id], out added, out failure);
                if (ok) { sources++; additional += added; }
                else { unchanged++; document.Editor.WriteMessage("\nRoute {0} unchanged: {1}", route.Id.Handle, failure); }
            }
            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_PLBREAKJUNCTIONS ALL-SEGMENTS complete. Junction pairs={0}; sources cut={1}; additional separate spans={2}; unchanged={3}. Every successful crossing/T cut is a separate LINE/POLYLINE segment and no source handle was erased.",
                junctions, sources, additional, unchanged);
        }

        private static List<CutRoute> ReadCutRoutes(Database database, IEnumerable<ObjectId> ids)
        {
            var result = new List<CutRoute>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids)
                {
                    Entity entity = null;
                    try { entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity; } catch { }
                    Line line = entity as Line;
                    if (line != null && line.Length > Tol)
                    {
                        CutRoute route = new CutRoute(id, true, line.Length, To2d(line.StartPoint), To2d(line.EndPoint));
                        route.Samples.Add(new CutSample(To2d(line.StartPoint), To2d(line.EndPoint), 0.0, line.Length));
                        result.Add(route);
                        continue;
                    }
                    Polyline polyline = entity as Polyline;
                    if (polyline == null || polyline.Closed || polyline.NumberOfVertices < 2 || polyline.Length <= Tol) continue;
                    CutRoute polyRoute = new CutRoute(id, false, polyline.Length, To2d(polyline.StartPoint), To2d(polyline.EndPoint));
                    for (int segment = 0; segment + 1 < polyline.NumberOfVertices; segment++)
                    {
                        bool curved = false;
                        try { curved = polyline.GetSegmentType(segment) != SegmentType.Line; } catch { }
                        int divisions = curved ? 16 : 1;
                        for (int part = 0; part < divisions; part++)
                        {
                            double p0 = segment + (double)part / divisions;
                            double p1 = segment + (double)(part + 1) / divisions;
                            try
                            {
                                Point3d a = polyline.GetPointAtParameter(p0);
                                Point3d b = polyline.GetPointAtParameter(p1);
                                double s0 = polyline.GetDistanceAtParameter(p0);
                                double s1 = polyline.GetDistanceAtParameter(p1);
                                if (Distance(To2d(a), To2d(b)) > Tol) polyRoute.Samples.Add(new CutSample(To2d(a), To2d(b), s0, s1));
                            }
                            catch { }
                        }
                    }
                    if (polyRoute.Samples.Count > 0) result.Add(polyRoute);
                }
            }
            return result;
        }

        private static bool CollectRouteJunctions(CutRoute first, CutRoute second, IList<double> firstCuts, IList<double> secondCuts)
        {
            bool found = false;
            foreach (CutSample a in first.Samples)
            foreach (CutSample b in second.Samples)
            {
                double ta;
                double tb;
                Point2d point;
                if (!TrySegmentIntersection(a.A, a.B, b.A, b.B, out ta, out tb, out point)) continue;
                double firstStation = a.S0 + (a.S1 - a.S0) * ta;
                double secondStation = b.S0 + (b.S1 - b.S0) * tb;
                if (firstStation > JunctionTolerance && firstStation < first.Length - JunctionTolerance) AddUnique(firstCuts, firstStation, JunctionTolerance * 0.25);
                if (secondStation > JunctionTolerance && secondStation < second.Length - JunctionTolerance) AddUnique(secondCuts, secondStation, JunctionTolerance * 0.25);
                found = true;
            }
            found |= CollectEndpointOnRoute(first.Start, first, second, secondCuts);
            found |= CollectEndpointOnRoute(first.End, first, second, secondCuts);
            found |= CollectEndpointOnRoute(second.Start, second, first, firstCuts);
            found |= CollectEndpointOnRoute(second.End, second, first, firstCuts);
            return found;
        }

        private static bool CollectEndpointOnRoute(Point2d endpoint, CutRoute endpointOwner, CutRoute host, IList<double> hostCuts)
        {
            double bestDistance = JunctionTolerance + Tol;
            double bestStation = -1.0;
            foreach (CutSample sample in host.Samples)
            {
                double t;
                Point2d projected = ProjectToSegment(endpoint, sample.A, sample.B, out t);
                double distance = Distance(endpoint, projected);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestStation = sample.S0 + (sample.S1 - sample.S0) * t;
                }
            }
            if (bestStation > JunctionTolerance && bestStation < host.Length - JunctionTolerance && bestDistance <= JunctionTolerance)
            {
                AddUnique(hostCuts, bestStation, JunctionTolerance * 0.25);
                return true;
            }
            return false;
        }

        private static void NormalizeStations(List<double> values, double length)
        {
            values.RemoveAll(v => v <= JunctionTolerance || v >= length - JunctionTolerance || double.IsNaN(v) || double.IsInfinity(v));
            values.Sort();
            for (int i = values.Count - 1; i > 0; i--) if (Math.Abs(values[i] - values[i - 1]) <= JunctionTolerance * 0.25) values.RemoveAt(i);
        }

        private static bool SplitLineAll(Database database, ObjectId id, IList<double> cuts, out int added, out string failure)
        {
            added = 0;
            failure = string.Empty;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                Line source = transaction.GetObject(id, OpenMode.ForWrite, false) as Line;
                if (source == null) { failure = "source line unavailable"; return false; }
                Point3d start = source.StartPoint;
                Vector3d direction = source.EndPoint - source.StartPoint;
                double length = direction.Length;
                if (length <= Tol) { failure = "zero length"; return false; }
                direction = direction.GetNormal();
                List<double> boundaries = new List<double> { 0.0 };
                boundaries.AddRange(cuts.Where(c => c > JunctionTolerance && c < length - JunctionTolerance));
                boundaries.Add(length);
                boundaries = boundaries.Distinct().OrderBy(v => v).ToList();
                if (boundaries.Count < 3) { failure = "no internal cuts"; return false; }
                BlockTableRecord owner = transaction.GetObject(source.OwnerId, OpenMode.ForWrite, false) as BlockTableRecord;
                if (owner == null) { failure = "owner unavailable"; return false; }
                Point3d originalEnd = source.EndPoint;
                source.EndPoint = start + direction * boundaries[1];
                for (int i = 1; i + 1 < boundaries.Count; i++)
                {
                    var span = new Line(start + direction * boundaries[i], start + direction * boundaries[i + 1]);
                    CopyProperties(source, span);
                    owner.AppendEntity(span);
                    transaction.AddNewlyCreatedDBObject(span, true);
                    added++;
                }
                transaction.Commit();
                return true;
            }
        }

        private static bool SplitPolylineAll(Database database, ObjectId id, IList<double> cuts, out int added, out string failure)
        {
            added = 0;
            failure = string.Empty;
            DBObjectCollection splitObjects = null;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                Polyline source = transaction.GetObject(id, OpenMode.ForWrite, false) as Polyline;
                if (source == null || source.Closed) { failure = "source polyline unavailable/closed"; return false; }
                var splitPoints = new Point3dCollection();
                foreach (double station in cuts)
                {
                    try { splitPoints.Add(source.GetPointAtDist(station)); } catch { }
                }
                if (splitPoints.Count == 0) { failure = "no valid split points"; return false; }
                try { splitObjects = source.GetSplitCurves(splitPoints); }
                catch (System.Exception exception) { failure = exception.Message; return false; }
                List<Polyline> pieces = splitObjects.Cast<DBObject>().OfType<Polyline>().ToList();
                if (pieces.Count < 2) { DisposeUnowned(splitObjects); failure = "native split returned fewer than two spans"; return false; }
                Point3d originalStart = source.StartPoint;
                pieces = pieces.OrderBy(p => Math.Min(p.StartPoint.DistanceTo(originalStart), p.EndPoint.DistanceTo(originalStart))).ToList();
                BlockTableRecord owner = transaction.GetObject(source.OwnerId, OpenMode.ForWrite, false) as BlockTableRecord;
                if (owner == null) { DisposeUnowned(splitObjects); failure = "owner unavailable"; return false; }
                ReplacePolylineGeometry(source, pieces[0]);
                for (int i = 1; i < pieces.Count; i++)
                {
                    Polyline piece = pieces[i];
                    CopyProperties(source, piece);
                    owner.AppendEntity(piece);
                    transaction.AddNewlyCreatedDBObject(piece, true);
                    added++;
                }
                transaction.Commit();
            }
            DisposeUnowned(splitObjects);
            return true;
        }

        private static void ReplacePolylineGeometry(Polyline target, Polyline source)
        {
            while (target.NumberOfVertices > 0) target.RemoveVertexAt(target.NumberOfVertices - 1);
            target.Elevation = source.Elevation;
            target.Normal = source.Normal;
            target.Closed = source.Closed;
            for (int i = 0; i < source.NumberOfVertices; i++)
                target.AddVertexAt(i, source.GetPoint2dAt(i), source.GetBulgeAt(i), source.GetStartWidthAt(i), source.GetEndWidthAt(i));
        }

        private static void DisposeUnowned(DBObjectCollection collection)
        {
            if (collection == null) return;
            foreach (DBObject value in collection)
            {
                try { if (value != null && value.Database == null) value.Dispose(); } catch { }
            }
        }

        // -----------------------------------------------------------------
        // SURFACE SLOPE LABELS - surface selected in popup, not entity prompt.
        // -----------------------------------------------------------------
        internal static void SurfaceSlopeLabelsPopup(Document document)
        {
            if (document == null) return;
            CivilDocument civil = CivilApplication.ActiveDocument;
            if (civil == null) return;
            Dictionary<string, ObjectId> surfaces = ReadSurfaceChoices(document.Database, civil);
            if (surfaces.Count == 0)
            {
                document.Editor.WriteMessage("\nCE_SURFACESLOPEARROWS cancelled. No Civil 3D surface exists in this drawing.");
                return;
            }
            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Native Civil 3D Surface Slope Labels",
                "Choose the Civil 3D surface in this popup. Labels are native SurfaceSlopeLabel entities and remain dynamically attached to the selected surface.");
            settings.AddChoice("Surface", "01 Surface", "Surface", surfaces.Keys.First(),
                "Surface used for all new native slope labels.", surfaces.Keys);
            settings.AddChoice("Mode", "02 Placement", "Placement", "Pick locations",
                "Pick = place individual labels. Grid = place labels automatically over the surface extents.",
                new[] { "Pick locations", "Grid" });
            settings.AddPositiveDouble("Spacing", "02 Placement", "Grid spacing", 20.0,
                "Used only for Grid placement.");
            settings.AddPositiveDouble("Maximum", "02 Placement", "Maximum grid labels", 250.0,
                "Safety limit for Grid placement.");
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;
            ObjectId surfaceId;
            if (!surfaces.TryGetValue(settings.Text("Surface"), out surfaceId)) return;
            int created = 0;
            if (string.Equals(settings.Text("Mode"), "Grid", StringComparison.OrdinalIgnoreCase))
            {
                foreach (Point2d point in CollectSurfaceGridPoints(document.Database, surfaceId, settings.Double("Spacing", 20.0), Math.Max(1, (int)Math.Round(settings.Double("Maximum", 250.0)))))
                {
                    try { CivilSurfaceSlopeLabel.Create(surfaceId, point); created++; } catch { }
                }
            }
            else
            {
                while (true)
                {
                    var options = new PromptPointOptions("\nPick slope-label point <Enter to finish>: ") { AllowNone = true };
                    PromptPointResult picked = document.Editor.GetPoint(options);
                    if (picked.Status == PromptStatus.None || picked.Status == PromptStatus.Cancel) break;
                    if (picked.Status != PromptStatus.OK) break;
                    Point3d point = picked.Value.TransformBy(document.Editor.CurrentUserCoordinateSystem);
                    if (!PointOnSurface(document.Database, surfaceId, point.X, point.Y))
                    {
                        document.Editor.WriteMessage("\nPoint is outside the selected surface; skipped.");
                        continue;
                    }
                    try { CivilSurfaceSlopeLabel.Create(surfaceId, new Point2d(point.X, point.Y)); created++; }
                    catch (System.Exception exception) { document.Editor.WriteMessage("\nSlope label skipped: {0}", exception.Message); }
                }
            }
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_SURFACESLOPEARROWS complete. Native Civil 3D SurfaceSlopeLabel entities created={0}.", created);
        }

        internal static void SlopeAnnotations(Document document)
        {
            if (document == null) return;
            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Slope Annotation Options",
                "Choose native surface slope labels, one native Civil 3D slope between two feature lines, dynamic multiple crossfall annotations, or slopes along feature lines.");
            settings.AddChoice("Mode", "01 Slope type", "Slope type", "Single slope between two feature lines - Civil 3D",
                "Single = select two feature lines then pick the crossfall insertion position; CE creates a Civil FeatureLine crossfall segment plus native GeneralSegmentLabel. Surface = native SurfaceSlopeLabel.",
                new[]
                {
                    "Surface slope - native Civil 3D label",
                    "Single slope between two feature lines - Civil 3D",
                    "Dynamic multiple slopes between two feature lines",
                    "Slope along feature lines"
                });
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;
            string mode = settings.Text("Mode");
            if (string.Equals(mode, "Surface slope - native Civil 3D label", StringComparison.OrdinalIgnoreCase))
            {
                SurfaceSlopeLabelsPopup(document);
                return;
            }
            if (string.Equals(mode, "Single slope between two feature lines - Civil 3D", StringComparison.OrdinalIgnoreCase))
            {
                SingleFeatureLineSlope(document);
                return;
            }
            if (string.Equals(mode, "Dynamic multiple slopes between two feature lines", StringComparison.OrdinalIgnoreCase))
            {
                new August27DynamicSlopeGridHatchCommands().FeatureLineCrossfallArrows();
                return;
            }
            August27DynamicSlopeGridHatchCommands.FeatureLineSlopeArrows(document);
        }

        private static void SingleFeatureLineSlope(Document document)
        {
            PromptSelectionResult selection = document.Editor.GetSelection(new PromptSelectionOptions
            {
                MessageForAdding = "\nSelect exactly TWO Civil 3D feature lines: ",
                AllowDuplicates = false,
                RejectObjectsFromNonCurrentSpace = true
            });
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;
            List<ObjectId> featureIds = new List<ObjectId>();
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in selection.Value.GetObjectIds().Distinct())
                {
                    CivilFeatureLine feature = null;
                    try { feature = transaction.GetObject(id, OpenMode.ForRead, false) as CivilFeatureLine; } catch { }
                    if (feature != null && !feature.IsReferenceObject) featureIds.Add(id);
                }
            }
            if (featureIds.Count != 2)
            {
                document.Editor.WriteMessage("\nExactly two editable Civil 3D feature lines are required.");
                return;
            }

            CivilDocument civil = CivilApplication.ActiveDocument;
            Dictionary<string, ObjectId> lineStyles = ReadGeneralLineStyles(document.Database, civil);
            string preferredStyle = lineStyles.Keys.FirstOrDefault(name => name.IndexOf("slope", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("grade", StringComparison.OrdinalIgnoreCase) >= 0)
                ?? lineStyles.Keys.FirstOrDefault() ?? string.Empty;
            var styleSettings = new ProductionSettingsDialogModel(
                "CE Tools - Single Feature-Line Crossfall",
                "Select the Civil 3D General Line label style for the native crossfall label, then pick the desired crossfall insertion position in plan.");
            if (lineStyles.Count > 0)
                styleSettings.AddChoice("Style", "01 Civil label", "General line label style", preferredStyle,
                    "A style containing a Grade/Slope component is recommended.", lineStyles.Keys);
            if (!DisciplineWorkflowDialogs.EditSettings(styleSettings)) return;

            PromptPointResult insertion = document.Editor.GetPoint("\nPick the SINGLE slope insertion/crossfall position between the selected feature lines: ");
            if (insertion.Status != PromptStatus.OK) return;
            Point3d pick = insertion.Value.TransformBy(document.Editor.CurrentUserCoordinateSystem);

            Point3d first;
            Point3d second;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                CivilFeatureLine a = transaction.GetObject(featureIds[0], OpenMode.ForRead, false) as CivilFeatureLine;
                CivilFeatureLine b = transaction.GetObject(featureIds[1], OpenMode.ForRead, false) as CivilFeatureLine;
                if (a == null || b == null || !ClosestPointOnFeatureLine(a, pick, out first) || !ClosestPointOnFeatureLine(b, pick, out second))
                {
                    document.Editor.WriteMessage("\nCould not resolve a crossfall position on both feature lines.");
                    return;
                }
            }
            if (PlanDistance(first, second) <= Tol)
            {
                document.Editor.WriteMessage("\nThe selected feature lines coincide at the insertion position; no crossfall segment was created.");
                return;
            }

            ObjectId crossfallFeatureId = ObjectId.Null;
            ObjectId labelId = ObjectId.Null;
            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                ObjectId layerId = GetOrCreateLayer(document.Database, transaction, CrossfallLayer, 2);
                BlockTableRecord space = transaction.GetObject(document.Database.CurrentSpaceId, OpenMode.ForWrite, false) as BlockTableRecord;
                if (space == null) return;
                var vertices = new Point3dCollection { first, second };
                var source3d = new Polyline3d(Poly3dType.SimplePoly, vertices, false);
                source3d.SetDatabaseDefaults(document.Database);
                source3d.LayerId = layerId;
                space.AppendEntity(source3d);
                transaction.AddNewlyCreatedDBObject(source3d, true);
                transaction.Commit();

                try { crossfallFeatureId = CivilFeatureLine.Create("CE Crossfall " + Guid.NewGuid().ToString("N").Substring(0, 8), source3d.ObjectId); }
                catch (System.Exception exception)
                {
                    document.Editor.WriteMessage("\nCivil 3D crossfall FeatureLine creation failed: {0}", exception.Message);
                    return;
                }
            }

            try
            {
                string selectedStyle = styleSettings.Text("Style");
                ObjectId lineStyleId;
                ObjectId curveStyleId = FirstGeneralCurveStyle(civil);
                if (lineStyles.TryGetValue(selectedStyle, out lineStyleId) && !lineStyleId.IsNull && !curveStyleId.IsNull)
                    labelId = CivilGeneralSegmentLabel.Create(crossfallFeatureId, 0.5, lineStyleId, curveStyleId);
                else
                    labelId = CivilGeneralSegmentLabel.Create(crossfallFeatureId, 0.5);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage("\nCivil 3D GeneralSegmentLabel creation failed: {0}", exception.Message);
                return;
            }
            document.Editor.Regen();
            double horizontal = PlanDistance(first, second);
            double slope = horizontal <= Tol ? 0.0 : (second.Z - first.Z) / horizontal * 100.0;
            document.Editor.WriteMessage(
                "\nSingle feature-line crossfall created as Civil 3D entities. FeatureLine={0}; GeneralSegmentLabel={1}; measured slope={2:0.###}%.",
                crossfallFeatureId.Handle, labelId.Handle, slope);
        }

        private static Dictionary<string, ObjectId> ReadSurfaceChoices(Database database, CivilDocument civil)
        {
            var result = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);
            if (civil == null) return result;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in civil.GetSurfaceIds())
                {
                    CivilSurface surface = null;
                    try { surface = transaction.GetObject(id, OpenMode.ForRead, false) as CivilSurface; } catch { }
                    if (surface == null) continue;
                    string name = string.IsNullOrWhiteSpace(surface.Name) ? id.Handle.ToString() : surface.Name;
                    if (!result.ContainsKey(name)) result[name] = id;
                }
            }
            return result;
        }

        private static Dictionary<string, ObjectId> ReadGeneralLineStyles(Database database, CivilDocument civil)
        {
            var result = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);
            if (civil == null) return result;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in civil.Styles.LabelStyles.GeneralLineLabelStyles)
                {
                    CivilLabelStyle style = null;
                    try { style = transaction.GetObject(id, OpenMode.ForRead, false) as CivilLabelStyle; } catch { }
                    if (style == null) continue;
                    string name = style.Name ?? id.Handle.ToString();
                    if (!result.ContainsKey(name)) result[name] = id;
                }
            }
            return result;
        }

        private static ObjectId FirstGeneralCurveStyle(CivilDocument civil)
        {
            if (civil == null) return ObjectId.Null;
            foreach (ObjectId id in civil.Styles.LabelStyles.GeneralCurveLabelStyles) return id;
            return ObjectId.Null;
        }

        private static bool ClosestPointOnFeatureLine(CivilFeatureLine feature, Point3d reference, out Point3d best)
        {
            best = Point3d.Origin;
            Point3dCollection points;
            try { points = feature.GetPoints(FeatureLinePointType.AllPoints); }
            catch { return false; }
            if (points == null || points.Count < 2) return false;
            double bestDistance = double.MaxValue;
            for (int i = 0; i + 1 < points.Count; i++)
            {
                Point3d a = points[i];
                Point3d b = points[i + 1];
                double t;
                Point2d projection = ProjectToSegment(To2d(reference), To2d(a), To2d(b), out t);
                double distance = Distance(To2d(reference), projection);
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                best = new Point3d(projection.X, projection.Y, a.Z + (b.Z - a.Z) * t);
            }
            return bestDistance < double.MaxValue;
        }

        private static List<Point2d> CollectSurfaceGridPoints(Database database, ObjectId surfaceId, double spacing, int maximum)
        {
            var result = new List<Point2d>();
            spacing = Math.Max(0.001, spacing);
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                CivilSurface surface = transaction.GetObject(surfaceId, OpenMode.ForRead, false) as CivilSurface;
                if (surface == null) return result;
                Extents3d extents;
                try { extents = surface.GeometricExtents; } catch { return result; }
                for (double x = extents.MinPoint.X; x <= extents.MaxPoint.X + Tol && result.Count < maximum; x += spacing)
                for (double y = extents.MinPoint.Y; y <= extents.MaxPoint.Y + Tol && result.Count < maximum; y += spacing)
                {
                    try
                    {
                        double z = surface.FindElevationAtXY(x, y);
                        if (!double.IsNaN(z) && !double.IsInfinity(z)) result.Add(new Point2d(x, y));
                    }
                    catch { }
                }
            }
            return result;
        }

        private static bool PointOnSurface(Database database, ObjectId surfaceId, double x, double y)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                CivilSurface surface = null;
                try { surface = transaction.GetObject(surfaceId, OpenMode.ForRead, false) as CivilSurface; } catch { }
                if (surface == null) return false;
                try { double z = surface.FindElevationAtXY(x, y); return !double.IsNaN(z) && !double.IsInfinity(z); }
                catch { return false; }
            }
        }

        // -----------------------------------------------------------------
        // shared helpers
        // -----------------------------------------------------------------
        private static PromptSelectionResult SelectLinePolyline(Editor editor, string message)
        {
            var options = new PromptSelectionOptions { MessageForAdding = message, AllowDuplicates = false, RejectObjectsFromNonCurrentSpace = true };
            var filter = new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, "LINE,LWPOLYLINE") });
            return editor.GetSelection(options, filter);
        }

        private static bool TerminalSegmentStraight(Polyline polyline, bool start)
        {
            try
            {
                int segment = start ? 0 : polyline.NumberOfVertices - 2;
                return polyline.GetSegmentType(segment) == SegmentType.Line;
            }
            catch { return false; }
        }

        private static bool ReadCurrentTerminal(Entity entity, bool start, out Terminal terminal)
        {
            Line line = entity as Line;
            if (line != null)
            {
                terminal = start
                    ? new Terminal(line.ObjectId, true, line.StartPoint, line.EndPoint)
                    : new Terminal(line.ObjectId, false, line.EndPoint, line.StartPoint);
                return true;
            }
            Polyline polyline = entity as Polyline;
            if (polyline != null && !polyline.Closed && polyline.NumberOfVertices >= 2 && TerminalSegmentStraight(polyline, start))
            {
                terminal = start
                    ? new Terminal(polyline.ObjectId, true, polyline.StartPoint, polyline.GetPoint3dAt(1))
                    : new Terminal(polyline.ObjectId, false, polyline.EndPoint, polyline.GetPoint3dAt(polyline.NumberOfVertices - 2));
                return true;
            }
            terminal = null;
            return false;
        }

        private static void SetTerminal(Entity entity, bool start, Point3d target)
        {
            Line line = entity as Line;
            if (line != null) { if (start) line.StartPoint = target; else line.EndPoint = target; return; }
            Polyline polyline = entity as Polyline;
            if (polyline == null) return;
            int index = start ? 0 : polyline.NumberOfVertices - 1;
            polyline.SetPointAt(index, new Point2d(target.X, target.Y));
        }

        private static string TerminalKey(Terminal terminal) { return terminal.Id.Handle + (terminal.IsStart ? ":S" : ":E"); }

        private static void CopyProperties(Entity source, Entity target)
        {
            if (source == null || target == null) return;
            try { target.LayerId = source.LayerId; } catch { }
            try { target.Color = source.Color; } catch { }
            try { target.LinetypeId = source.LinetypeId; } catch { }
            try { target.LinetypeScale = source.LinetypeScale; } catch { }
            try { target.LineWeight = source.LineWeight; } catch { }
        }

        private static ObjectId GetOrCreateLayer(Database database, Transaction transaction, string name, short colorIndex)
        {
            LayerTable layers = transaction.GetObject(database.LayerTableId, OpenMode.ForRead, false) as LayerTable;
            if (layers.Has(name)) return layers[name];
            layers.UpgradeOpen();
            var layer = new LayerTableRecord { Name = name, Color = Color.FromColorIndex(ColorMethod.ByAci, colorIndex) };
            ObjectId id = layers.Add(layer);
            transaction.AddNewlyCreatedDBObject(layer, true);
            return id;
        }

        private static bool TryInfiniteIntersection(Point2d a0, Point2d a1, Point2d b0, Point2d b1, out Point2d point)
        {
            Vector2d r = a1 - a0;
            Vector2d s = b1 - b0;
            double denominator = Cross(r, s);
            if (Math.Abs(denominator) <= 1e-10) { point = Point2d.Origin; return false; }
            double t = Cross(b0 - a0, s) / denominator;
            point = a0 + r * t;
            return true;
        }

        private static bool TrySegmentIntersection(Point2d a0, Point2d a1, Point2d b0, Point2d b1, out double ta, out double tb, out Point2d point)
        {
            Vector2d r = a1 - a0;
            Vector2d s = b1 - b0;
            double denominator = Cross(r, s);
            if (Math.Abs(denominator) <= 1e-10) { ta = tb = 0.0; point = Point2d.Origin; return false; }
            Vector2d q = b0 - a0;
            ta = Cross(q, s) / denominator;
            tb = Cross(q, r) / denominator;
            if (ta < -1e-8 || ta > 1.0 + 1e-8 || tb < -1e-8 || tb > 1.0 + 1e-8) { point = Point2d.Origin; return false; }
            ta = Math.Max(0.0, Math.Min(1.0, ta));
            tb = Math.Max(0.0, Math.Min(1.0, tb));
            point = a0 + r * ta;
            return true;
        }

        private static Point2d ProjectToSegment(Point2d point, Point2d a, Point2d b, out double t)
        {
            Vector2d ab = b - a;
            double lengthSquared = ab.DotProduct(ab);
            if (lengthSquared <= Tol) { t = 0.0; return a; }
            t = (point - a).DotProduct(ab) / lengthSquared;
            t = Math.Max(0.0, Math.Min(1.0, t));
            return a + ab * t;
        }

        private static double DistanceToSegment(Point2d point, Point2d a, Point2d b)
        {
            double t;
            return Distance(point, ProjectToSegment(point, a, b, out t));
        }

        private static double NormalizeAngle(double value)
        {
            double twoPi = Math.PI * 2.0;
            while (value < 0.0) value += twoPi;
            while (value >= twoPi) value -= twoPi;
            return value;
        }

        private static Point2d PolygonCentroid(IList<Point2d> points)
        {
            if (points == null || points.Count == 0) return Point2d.Origin;
            double area2 = 0.0, x = 0.0, y = 0.0;
            for (int i = 0; i < points.Count; i++)
            {
                Point2d a = points[i];
                Point2d b = points[(i + 1) % points.Count];
                double cross = a.X * b.Y - b.X * a.Y;
                area2 += cross;
                x += (a.X + b.X) * cross;
                y += (a.Y + b.Y) * cross;
            }
            if (Math.Abs(area2) <= Tol) return Average(points);
            return new Point2d(x / (3.0 * area2), y / (3.0 * area2));
        }

        private static Point2d Average(IList<Point2d> points)
        {
            if (points == null || points.Count == 0) return Point2d.Origin;
            return new Point2d(points.Average(p => p.X), points.Average(p => p.Y));
        }

        private static List<Point2d> RemoveConsecutiveDuplicates(IEnumerable<Point2d> source, double tolerance)
        {
            var result = new List<Point2d>();
            foreach (Point2d point in source)
                if (result.Count == 0 || Distance(result[result.Count - 1], point) > tolerance) result.Add(point);
            return result;
        }

        private static int NodeIndex(IList<Point2d> nodes, Point2d point, double tolerance)
        {
            for (int i = 0; i < nodes.Count; i++) if (Distance(nodes[i], point) <= tolerance) return i;
            nodes.Add(point);
            return nodes.Count - 1;
        }

        private static void AddAdjacency(IDictionary<int, List<int>> adjacency, int node, int edge)
        {
            List<int> list;
            if (!adjacency.TryGetValue(node, out list)) { list = new List<int>(); adjacency[node] = list; }
            list.Add(edge);
        }

        private static void AddUnique(IList<double> values, double value, double tolerance)
        {
            if (!values.Any(existing => Math.Abs(existing - value) <= tolerance)) values.Add(value);
        }

        private static Point2d Mid(Point2d a, Point2d b) { return new Point2d((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5); }
        private static Point2d Lerp(Point2d a, Point2d b, double t) { return new Point2d(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t); }
        private static Point2d To2d(Point3d p) { return new Point2d(p.X, p.Y); }
        private static double Distance(Point2d a, Point2d b) { double dx = a.X - b.X, dy = a.Y - b.Y; return Math.Sqrt(dx * dx + dy * dy); }
        private static double PlanDistance(Point3d a, Point3d b) { return Distance(To2d(a), To2d(b)); }
        private static double Cross(Vector2d a, Vector2d b) { return a.X * b.Y - a.Y * b.X; }

        private sealed class BoundarySource
        {
            internal int Index;
            internal ObjectId Id;
            internal bool Closed;
            internal Point2d Centroid;
            internal readonly List<BoundarySegment> Segments = new List<BoundarySegment>();
        }
        private sealed class BoundarySegment
        {
            internal BoundarySource Source;
            internal Point2d A;
            internal Point2d B;
        }
        private sealed class NetworkSegment
        {
            internal Point2d A;
            internal Point2d B;
            internal NetworkSegment(Point2d a, Point2d b) { A = a; B = b; }
        }
        private sealed class GraphEdge
        {
            internal int A;
            internal int B;
            internal GraphEdge(int a, int b) { A = a; B = b; }
            internal int Other(int node) { return node == A ? B : A; }
        }
        private sealed class FilletVertex
        {
            internal Point2d Point;
            internal double Bulge;
            internal FilletVertex(Point2d point, double bulge) { Point = point; Bulge = bulge; }
        }
        private sealed class Terminal
        {
            internal ObjectId Id;
            internal bool IsStart;
            internal Point3d Point;
            internal Point3d Inner;
            internal Terminal(ObjectId id, bool isStart, Point3d point, Point3d inner) { Id = id; IsStart = isStart; Point = point; Inner = inner; }
        }
        private sealed class FilletCandidate
        {
            internal Terminal First;
            internal Terminal Second;
            internal Point2d Crossing;
            internal double Score;
            internal FilletCandidate(Terminal first, Terminal second, Point2d crossing, double score) { First = first; Second = second; Crossing = crossing; Score = score; }
        }
        private sealed class CutSample
        {
            internal Point2d A;
            internal Point2d B;
            internal double S0;
            internal double S1;
            internal CutSample(Point2d a, Point2d b, double s0, double s1) { A = a; B = b; S0 = s0; S1 = s1; }
        }
        private sealed class CutRoute
        {
            internal ObjectId Id;
            internal bool IsLine;
            internal double Length;
            internal Point2d Start;
            internal Point2d End;
            internal readonly List<CutSample> Samples = new List<CutSample>();
            internal CutRoute(ObjectId id, bool isLine, double length, Point2d start, Point2d end) { Id = id; IsLine = isLine; Length = length; Start = start; End = end; }
        }
    }
}

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

[assembly: CommandClass(typeof(CETools.Civil3D.August19RoadReserveSewerAndSafetyCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// August 19 road-reserve production that is deliberately separate from the
    /// cadastral-shortest-route and Midblock sewer workflows.  The sewer command
    /// works directly from cadastral erf boundaries, validates reserve geometry,
    /// identifies opposing road-reserve edges, offsets the sewer toward the road
    /// centre, samples a selected surface and directs generated route pieces toward
    /// the site low point.  A safe road-centreline command uses the same reserve
    /// conditions so malformed cadastral polygons never reach native offset/intersection
    /// geometry before they have been validated.
    /// </summary>
    public sealed class August19RoadReserveSewerAndSafetyCommands
    {
        private const string SewerRouteLayer = "CE-SEWER-ROADRESERVE-ROUTE";
        private const string SewerMhLayer = "CE-SEWER-ROADRESERVE-MH";
        private const string SewerAnalysisLayer = "CE-SEWER-ROADRESERVE-ANALYSIS";
        private const string RoadCenterLayer = "CE-ROAD-CENTERLINE";
        private const double Tol = 1e-8;
        private const double Snap = 0.05;

        [CommandMethod("CE_TOOLS", "CE_SEWERROADRESERVE", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void SewerRoadReserve()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            var model = new ProductionSettingsDialogModel(
                "CE Tools - Road Reserve Sewer Route",
                "Create Sewer-only road-reserve routes directly from cadastral erf boundaries. CE Tools validates opposing reserve edges, offsets each route from the outer erf boundary toward the road centre, analyses a selected surface and directs the route network toward the site low point.");
            model.AddChoice("Scope", "01 Cadastral", "Erf boundaries", "Selected",
                "Use selected closed cadastral erf polylines or all non-CE closed lightweight polylines in model space.",
                new[] { "Selected", "All" });
            model.AddPositiveDouble("ErfOffset", "02 Road Reserve", "Offset from erf boundary into road reserve", 1.5,
                "Offset each accepted outer erf/road-reserve edge toward the opposing road boundary/road centre.");
            model.AddDouble("MinWidth", "02 Road Reserve", "Minimum road reserve width", 6.0,
                "Reject opposing reserve edges closer than this distance.");
            model.AddDouble("MaxWidth", "02 Road Reserve", "Maximum road reserve width", 60.0,
                "Reject opposing reserve edges farther apart than this distance.");
            model.AddDouble("Parallel", "02 Road Reserve", "Maximum opposing-edge angle difference", 7.5,
                "Maximum angular difference in degrees between opposing road-reserve edges.");
            model.AddDouble("MinOverlapPercent", "02 Road Reserve", "Minimum overlapping edge length (%)", 50.0,
                "Required projected overlap as a percentage of the shorter opposing edge.");
            model.AddPositiveDouble("MinEdge", "02 Road Reserve", "Minimum usable reserve-edge length", 4.0,
                "Shorter cadastral boundary edges are not used as road-reserve frontage.");
            model.AddChoice("Spacing", "03 Manholes", "Maximum planning manhole spacing", "60 m",
                "Create route vertices/manholes at crossings, T-junctions and this maximum spacing.",
                new[] { "60 m", "80 m", "Custom" });
            model.AddPositiveDouble("CustomSpacing", "03 Manholes", "Custom maximum spacing", 60.0,
                "Used only when Custom spacing is selected.");
            model.AddPositiveDouble("StartSetback", "03 Manholes", "Starting manhole setback from erf boundary", 1.5,
                "Leaf/start manholes are moved this distance away from terminal erf boundaries where geometry permits.");
            model.AddPositiveDouble("ManholeDiameter", "03 Manholes", "Planning manhole diameter", 1.2,
                "Diameter of preliminary road-reserve planning manhole circles.");
            model.AddChoice("Replace", "04 Output", "Existing Road Reserve sewer output", "Replace existing",
                "Replace prior CE Road Reserve sewer routes/manholes or keep them.",
                new[] { "Replace existing", "Keep existing" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            List<ObjectId> ids = ResolveParcels(document, model.Text("Scope"));
            if (ids.Count == 0)
            {
                document.Editor.WriteMessage("\nCE_SEWERROADRESERVE: no closed cadastral erf polylines were found.");
                return;
            }

            ObjectId surfaceId = PromptSurface(document,
                "\nSelect Civil 3D surface for Road Reserve sewer slope / site-low-point analysis: ");
            if (surfaceId.IsNull)
            {
                document.Editor.WriteMessage("\nCE_SEWERROADRESERVE cancelled. A Civil 3D surface is required for flow direction and the site low point.");
                return;
            }

            double offset = Math.Max(0.0, model.Double("ErfOffset", 1.5));
            double minWidth = Math.Max(0.1, model.Double("MinWidth", 6.0));
            double maxWidth = Math.Max(minWidth, model.Double("MaxWidth", 60.0));
            double maxAngle = Math.Max(0.1, model.Double("Parallel", 7.5));
            double minOverlap = Math.Max(1.0, Math.Min(100.0, model.Double("MinOverlapPercent", 50.0)));
            double minEdge = Math.Max(0.1, model.Double("MinEdge", 4.0));
            double spacing = string.Equals(model.Text("Spacing"), "80 m", StringComparison.OrdinalIgnoreCase)
                ? 80.0
                : string.Equals(model.Text("Spacing"), "Custom", StringComparison.OrdinalIgnoreCase)
                    ? Math.Max(1.0, model.Double("CustomSpacing", 60.0))
                    : 60.0;
            double startSetback = Math.Max(0.0, model.Double("StartSetback", 1.5));
            double mhDiameter = Math.Max(0.1, model.Double("ManholeDiameter", 1.2));

            int invalid = 0;
            int roadEdges = 0;
            int routePieces = 0;
            int manholes = 0;
            int served = 0;
            int selectedCount = ids.Count;
            Point2d lowPoint = new Point2d();
            double lowElevation = double.NaN;

            try
            {
                using (DocumentLock documentLock = document.LockDocument())
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    Surface surface = transaction.GetObject(surfaceId, OpenMode.ForRead, false) as Surface;
                    if (surface == null)
                    {
                        document.Editor.WriteMessage("\nCE_SEWERROADRESERVE: selected object is not a readable Civil 3D surface.");
                        return;
                    }

                    List<Parcel> parcels = ReadValidParcels(ids, transaction, ref invalid);
                    if (parcels.Count == 0)
                    {
                        document.Editor.WriteMessage("\nCE_SEWERROADRESERVE: all selected cadastral polygons failed the reserve-geometry safety checks.");
                        return;
                    }

                    List<ReserveEdge> exterior = BuildExteriorEdges(parcels, minEdge);
                    Dictionary<int, OpposingMatch> matches = MatchOpposingEdges(
                        exterior, parcels, minWidth, maxWidth, maxAngle, minOverlap);
                    if (matches.Count == 0)
                    {
                        document.Editor.WriteMessage(
                            "\nCE_SEWERROADRESERVE: no opposing road-reserve edges satisfied width={0:0.###}-{1:0.###}, angle<={2:0.###} deg and overlap>={3:0.#}%.",
                            minWidth, maxWidth, maxAngle, minOverlap);
                        return;
                    }

                    var servedParcels = new HashSet<int>();
                    var raw = new List<RouteSegment>();
                    foreach (ReserveEdge edge in exterior)
                    {
                        OpposingMatch match;
                        if (!matches.TryGetValue(edge.Id, out match)) continue;
                        ReserveEdge opposite = exterior.FirstOrDefault(value => value.Id == match.OtherEdgeId);
                        if (opposite == null) continue;
                        Point2d a;
                        Point2d b;
                        if (!TryOffsetIntoReserve(edge, opposite, offset, match.Width, out a, out b)) continue;
                        if (Distance(a, b) <= Tol) continue;
                        raw.Add(new RouteSegment(edge.Id, edge.ParcelIndex, a, b));
                        servedParcels.Add(edge.ParcelIndex);
                        roadEdges++;
                    }
                    served = servedParcels.Count;
                    if (raw.Count == 0)
                    {
                        document.Editor.WriteMessage("\nCE_SEWERROADRESERVE: accepted reserve pairs did not produce usable in-reserve route geometry.");
                        return;
                    }

                    if (!TrySiteLowPoint(surface, parcels, raw, out lowPoint, out lowElevation))
                    {
                        document.Editor.WriteMessage("\nCE_SEWERROADRESERVE: the selected surface has no readable elevations over the road-reserve/cadastral data.");
                        return;
                    }

                    List<RoutePiece> pieces = SplitAtJunctionsAndSpacing(raw, spacing);
                    if (pieces.Count == 0)
                    {
                        document.Editor.WriteMessage("\nCE_SEWERROADRESERVE: route geometry could not be segmented into network-ready pieces.");
                        return;
                    }
                    OrientPiecesTowardLowPoint(pieces, surface, lowPoint);
                    ApplyLeafSetback(pieces, lowPoint, startSetback);

                    BlockTableRecord space = transaction.GetObject(
                        SymbolUtilityServices.GetBlockModelSpaceId(document.Database), OpenMode.ForWrite, false) as BlockTableRecord;
                    if (space == null) return;
                    ObjectId routeLayer = EnsureLayer(document.Database, transaction, SewerRouteLayer);
                    ObjectId mhLayer = EnsureLayer(document.Database, transaction, SewerMhLayer);
                    ObjectId analysisLayer = EnsureLayer(document.Database, transaction, SewerAnalysisLayer);
                    if (string.Equals(model.Text("Replace"), "Replace existing", StringComparison.OrdinalIgnoreCase))
                        EraseByLayers(space, transaction, SewerRouteLayer, SewerMhLayer, SewerAnalysisLayer);

                    var mhLocations = new Dictionary<string, Point2d>(StringComparer.OrdinalIgnoreCase);
                    int pieceNumber = 1;
                    foreach (RoutePiece piece in pieces)
                    {
                        if (Distance(piece.Start, piece.End) <= Tol) continue;
                        var polyline = new Polyline(2);
                        polyline.SetDatabaseDefaults(document.Database);
                        polyline.LayerId = routeLayer;
                        polyline.AddVertexAt(0, piece.Start, 0.0, 0.0, 0.0);
                        polyline.AddVertexAt(1, piece.End, 0.0, 0.0, 0.0);
                        space.AppendEntity(polyline);
                        transaction.AddNewlyCreatedDBObject(polyline, true);
                        WriteRouteLink(polyline, transaction, surfaceId, lowPoint, lowElevation, pieceNumber);
                        mhLocations[PointKey(piece.Start)] = piece.Start;
                        mhLocations[PointKey(piece.End)] = piece.End;
                        routePieces++;
                        pieceNumber++;
                    }

                    int mhNumber = 1;
                    foreach (Point2d point in mhLocations.Values
                        .OrderByDescending(value => Distance(value, lowPoint)))
                    {
                        Point3d location = new Point3d(point.X, point.Y, 0.0);
                        var circle = new Circle(location, Vector3d.ZAxis, mhDiameter * 0.5);
                        circle.SetDatabaseDefaults(document.Database);
                        circle.LayerId = mhLayer;
                        space.AppendEntity(circle);
                        transaction.AddNewlyCreatedDBObject(circle, true);

                        var label = new DBText
                        {
                            Position = location + new Vector3d(mhDiameter, mhDiameter, 0.0),
                            TextString = "RR-MH" + mhNumber.ToString(CultureInfo.InvariantCulture),
                            Height = Math.Max(PaperAnnotationScale.ModelTextHeight(document.Database, 2.0), 0.001),
                            LayerId = mhLayer
                        };
                        label.SetDatabaseDefaults(document.Database);
                        space.AppendEntity(label);
                        transaction.AddNewlyCreatedDBObject(label, true);
                        mhNumber++;
                        manholes++;
                    }

                    AddLowPointMarker(document.Database, transaction, space, analysisLayer, lowPoint, lowElevation, mhDiameter);
                    transaction.Commit();
                }
            }
            catch (Autodesk.AutoCAD.Runtime.Exception ex)
            {
                document.Editor.WriteMessage("\nCE_SEWERROADRESERVE stopped safely before native geometry could terminate Civil 3D: {0}", ex.Message);
                return;
            }
            catch (System.Exception ex)
            {
                document.Editor.WriteMessage("\nCE_SEWERROADRESERVE stopped safely: {0}", ex.Message);
                return;
            }

            document.Editor.SetImpliedSelection(new ObjectId[0]);
            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_SEWERROADRESERVE complete. Valid road-facing erf edges={0}; served erfs={1}/{2}; route pieces={3}; planning manholes={4}; rejected cadastral polygons={5}; site low point EL={6:0.###}.",
                roadEdges, served, selectedCount, routePieces, manholes, invalid, lowElevation);
        }

        [CommandMethod("CE_TOOLS", "CE_ROADRESERVECENTERLINESSAFE", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void SafeRoadReserveCenterlines()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Safe Road Reserve Centrelines",
                "Create road centrelines only after cadastral polygons and opposing reserve edges pass the August 19 road-reserve safety conditions. Invalid/degenerate geometry is rejected before any native Civil 3D/AutoCAD geometry operation.");
            model.AddDouble("MinWidth", "01 Reserve", "Minimum road reserve width", 6.0, "Minimum accepted opposing-edge separation.");
            model.AddDouble("MaxWidth", "01 Reserve", "Maximum road reserve width", 60.0, "Maximum accepted opposing-edge separation.");
            model.AddDouble("Parallel", "02 Detection", "Maximum opposing-edge angle difference", 7.5, "Degrees.");
            model.AddDouble("MinOverlapPercent", "02 Detection", "Minimum overlapping edge length (%)", 50.0, "Percentage of the shorter edge.");
            model.AddPositiveDouble("MinEdge", "02 Detection", "Minimum usable reserve-edge length", 4.0, "Reject shorter boundary edges.");
            model.AddChoice("Replace", "03 Output", "Existing CE road centrelines", "Keep existing", "Keep or replace prior generated centreline geometry.", new[] { "Keep existing", "Replace existing" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            PromptSelectionResult selection = SelectClosed(document.Editor, "\nSelect cadastral/reserve closed polylines: ");
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;
            double minWidth = Math.Max(0.1, model.Double("MinWidth", 6.0));
            double maxWidth = Math.Max(minWidth, model.Double("MaxWidth", 60.0));
            double maxAngle = Math.Max(0.1, model.Double("Parallel", 7.5));
            double minOverlap = Math.Max(1.0, Math.Min(100.0, model.Double("MinOverlapPercent", 50.0)));
            double minEdge = Math.Max(0.1, model.Double("MinEdge", 4.0));
            int invalid = 0;
            int created = 0;

            try
            {
                using (DocumentLock documentLock = document.LockDocument())
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    List<Parcel> parcels = ReadValidParcels(selection.Value.GetObjectIds(), transaction, ref invalid);
                    List<ReserveEdge> exterior = BuildExteriorEdges(parcels, minEdge);
                    Dictionary<int, OpposingMatch> matches = MatchOpposingEdges(exterior, parcels, minWidth, maxWidth, maxAngle, minOverlap);
                    BlockTableRecord space = transaction.GetObject(
                        SymbolUtilityServices.GetBlockModelSpaceId(document.Database), OpenMode.ForWrite, false) as BlockTableRecord;
                    if (space == null) return;
                    ObjectId layerId = EnsureLayer(document.Database, transaction, RoadCenterLayer);
                    if (string.Equals(model.Text("Replace"), "Replace existing", StringComparison.OrdinalIgnoreCase))
                        EraseByLayers(space, transaction, RoadCenterLayer);

                    var pairKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (KeyValuePair<int, OpposingMatch> pair in matches)
                    {
                        int firstId = pair.Key;
                        int secondId = pair.Value.OtherEdgeId;
                        string pairKey = Math.Min(firstId, secondId).ToString(CultureInfo.InvariantCulture) + ":" +
                                         Math.Max(firstId, secondId).ToString(CultureInfo.InvariantCulture);
                        if (!pairKeys.Add(pairKey)) continue;
                        ReserveEdge first = exterior.FirstOrDefault(value => value.Id == firstId);
                        ReserveEdge second = exterior.FirstOrDefault(value => value.Id == secondId);
                        if (first == null || second == null) continue;
                        Point2d start;
                        Point2d end;
                        if (!TryCommonMidline(first, second, minOverlap, out start, out end)) continue;
                        if (Distance(start, end) <= Tol) continue;
                        if (parcels.Any(parcel => PointInside(parcel.Points, Mid(start, end)))) continue;
                        var centre = new Polyline(2);
                        centre.SetDatabaseDefaults(document.Database);
                        centre.LayerId = layerId;
                        centre.AddVertexAt(0, start, 0.0, 0.0, 0.0);
                        centre.AddVertexAt(1, end, 0.0, 0.0, 0.0);
                        space.AppendEntity(centre);
                        transaction.AddNewlyCreatedDBObject(centre, true);
                        created++;
                    }
                    transaction.Commit();
                }
            }
            catch (Autodesk.AutoCAD.Runtime.Exception ex)
            {
                document.Editor.WriteMessage("\nCE_ROADRESERVECENTERLINESSAFE stopped safely: {0}", ex.Message);
                return;
            }
            catch (System.Exception ex)
            {
                document.Editor.WriteMessage("\nCE_ROADRESERVECENTERLINESSAFE stopped safely: {0}", ex.Message);
                return;
            }

            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_ROADRESERVECENTERLINESSAFE complete. Centreline segments={0}; rejected cadastral polygons={1}.",
                created, invalid);
        }

        private static List<ObjectId> ResolveParcels(Document document, string scope)
        {
            if (string.Equals(scope, "Selected", StringComparison.OrdinalIgnoreCase))
            {
                PromptSelectionResult selected = document.Editor.SelectImplied();
                if (selected.Status != PromptStatus.OK || selected.Value == null || selected.Value.Count == 0)
                    selected = SelectClosed(document.Editor, "\nSelect closed cadastral erf polylines: ");
                return selected.Status == PromptStatus.OK && selected.Value != null
                    ? selected.Value.GetObjectIds().Distinct().ToList()
                    : new List<ObjectId>();
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

        private static PromptSelectionResult SelectClosed(Editor editor, string message)
        {
            return editor.GetSelection(
                new PromptSelectionOptions
                {
                    MessageForAdding = message,
                    AllowDuplicates = false,
                    RejectObjectsFromNonCurrentSpace = true
                },
                new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, "LWPOLYLINE") }));
        }

        private static ObjectId PromptSurface(Document document, string message)
        {
            var options = new PromptEntityOptions(message);
            options.SetRejectMessage("\nSelect a Civil 3D surface.");
            options.AddAllowedClass(typeof(Surface), true);
            PromptEntityResult result = document.Editor.GetEntity(options);
            return result.Status == PromptStatus.OK ? result.ObjectId : ObjectId.Null;
        }

        private static List<Parcel> ReadValidParcels(IEnumerable<ObjectId> ids, Transaction transaction, ref int invalid)
        {
            var result = new List<Parcel>();
            int index = 0;
            foreach (ObjectId id in ids.Where(value => !value.IsNull && !value.IsErased).Distinct())
            {
                Polyline polyline;
                try { polyline = transaction.GetObject(id, OpenMode.ForRead, false) as Polyline; }
                catch { invalid++; continue; }
                if (polyline == null || !polyline.Closed || polyline.NumberOfVertices < 3)
                {
                    invalid++;
                    continue;
                }
                var points = new List<Point2d>();
                bool bad = false;
                for (int i = 0; i < polyline.NumberOfVertices; i++)
                {
                    Point2d point;
                    try { point = polyline.GetPoint2dAt(i); }
                    catch { bad = true; break; }
                    if (!Finite(point)) { bad = true; break; }
                    if (points.Count > 0 && Distance(points[points.Count - 1], point) <= Tol) { bad = true; break; }
                    points.Add(point);
                }
                if (bad || points.Count < 3 || UniquePointCount(points) < 3 || Math.Abs(SignedArea(points)) <= Tol || SelfIntersects(points))
                {
                    invalid++;
                    continue;
                }
                result.Add(new Parcel(index++, id, points, PolygonCentroid(points)));
            }
            return result;
        }

        private static List<ReserveEdge> BuildExteriorEdges(List<Parcel> parcels, double minEdge)
        {
            var buckets = new Dictionary<string, List<ReserveEdge>>(StringComparer.OrdinalIgnoreCase);
            int nextId = 0;
            foreach (Parcel parcel in parcels)
            {
                for (int i = 0; i < parcel.Points.Count; i++)
                {
                    Point2d a = parcel.Points[i];
                    Point2d b = parcel.Points[(i + 1) % parcel.Points.Count];
                    double length = Distance(a, b);
                    if (length < minEdge) continue;
                    var edge = new ReserveEdge(nextId++, parcel.Index, a, b, length);
                    string key = UndirectedSegmentKey(a, b);
                    List<ReserveEdge> list;
                    if (!buckets.TryGetValue(key, out list))
                    {
                        list = new List<ReserveEdge>();
                        buckets[key] = list;
                    }
                    list.Add(edge);
                }
            }
            return buckets.Values.Where(list => list.Count == 1).Select(list => list[0]).ToList();
        }

        private static Dictionary<int, OpposingMatch> MatchOpposingEdges(
            List<ReserveEdge> edges, List<Parcel> parcels,
            double minWidth, double maxWidth, double maxAngleDegrees, double minOverlapPercent)
        {
            var result = new Dictionary<int, OpposingMatch>();
            double angleLimit = maxAngleDegrees * Math.PI / 180.0;
            for (int i = 0; i < edges.Count; i++)
            {
                ReserveEdge first = edges[i];
                OpposingMatch best = null;
                for (int j = 0; j < edges.Count; j++)
                {
                    if (i == j) continue;
                    ReserveEdge second = edges[j];
                    if (first.ParcelIndex == second.ParcelIndex) continue;
                    double angle = ParallelAngle(first, second);
                    if (angle > angleLimit) continue;
                    double width = PerpendicularGap(first, second);
                    if (width < minWidth || width > maxWidth) continue;
                    double overlap = ProjectedOverlap(first, second);
                    double required = Math.Min(first.Length, second.Length) * minOverlapPercent / 100.0;
                    if (overlap + Tol < required) continue;
                    if (!FacesReserveGap(first, second, parcels)) continue;
                    double score = width + angle * 10.0 - overlap * 0.01;
                    if (best == null || score < best.Score)
                        best = new OpposingMatch(second.Id, width, overlap, score);
                }
                if (best != null) result[first.Id] = best;
            }
            return result;
        }

        private static bool FacesReserveGap(ReserveEdge first, ReserveEdge second, List<Parcel> parcels)
        {
            Parcel p1 = parcels.FirstOrDefault(value => value.Index == first.ParcelIndex);
            Parcel p2 = parcels.FirstOrDefault(value => value.Index == second.ParcelIndex);
            if (p1 == null || p2 == null) return false;
            Vector2d d = new Vector2d(first.B.X - first.A.X, first.B.Y - first.A.Y);
            if (d.Length <= Tol) return false;
            d = d.GetNormal();
            Point2d m1 = Mid(first.A, first.B);
            Point2d m2 = Mid(second.A, second.B);
            double parcelSide = Cross(d, new Vector2d(p1.Center.X - m1.X, p1.Center.Y - m1.Y));
            double gapSide = Cross(d, new Vector2d(m2.X - m1.X, m2.Y - m1.Y));
            if (Math.Abs(parcelSide) <= Tol || Math.Abs(gapSide) <= Tol || parcelSide * gapSide >= 0.0) return false;

            Vector2d d2 = new Vector2d(second.B.X - second.A.X, second.B.Y - second.A.Y);
            if (d2.Length <= Tol) return false;
            d2 = d2.GetNormal();
            double parcelSide2 = Cross(d2, new Vector2d(p2.Center.X - m2.X, p2.Center.Y - m2.Y));
            double gapSide2 = Cross(d2, new Vector2d(m1.X - m2.X, m1.Y - m2.Y));
            return Math.Abs(parcelSide2) > Tol && Math.Abs(gapSide2) > Tol && parcelSide2 * gapSide2 < 0.0;
        }

        private static bool TryOffsetIntoReserve(
            ReserveEdge edge, ReserveEdge opposite, double requestedOffset, double reserveWidth,
            out Point2d start, out Point2d end)
        {
            start = edge.A;
            end = edge.B;
            Vector2d d = new Vector2d(edge.B.X - edge.A.X, edge.B.Y - edge.A.Y);
            if (d.Length <= Tol) return false;
            d = d.GetNormal();
            Vector2d normal = new Vector2d(-d.Y, d.X);
            Point2d m1 = Mid(edge.A, edge.B);
            Point2d m2 = Mid(opposite.A, opposite.B);
            Vector2d towardOther = new Vector2d(m2.X - m1.X, m2.Y - m1.Y);
            if (normal.DotProduct(towardOther) < 0.0) normal = -normal;
            double maximumInside = Math.Max(0.0, reserveWidth * 0.45);
            double distance = Math.Min(Math.Max(0.0, requestedOffset), maximumInside);
            Vector2d shift = normal.MultiplyBy(distance);
            start = edge.A + shift;
            end = edge.B + shift;
            return Finite(start) && Finite(end);
        }

        private static bool TryCommonMidline(
            ReserveEdge first, ReserveEdge second, double minOverlapPercent,
            out Point2d start, out Point2d end)
        {
            start = new Point2d();
            end = new Point2d();
            Vector2d u = new Vector2d(first.B.X - first.A.X, first.B.Y - first.A.Y);
            if (u.Length <= Tol) return false;
            double firstLength = u.Length;
            u = u.GetNormal();
            double sC = Dot(new Vector2d(second.A.X - first.A.X, second.A.Y - first.A.Y), u);
            double sD = Dot(new Vector2d(second.B.X - first.A.X, second.B.Y - first.A.Y), u);
            double lo = Math.Max(0.0, Math.Min(sC, sD));
            double hi = Math.Min(firstLength, Math.Max(sC, sD));
            double overlap = hi - lo;
            double required = Math.Min(first.Length, second.Length) * minOverlapPercent / 100.0;
            if (overlap + Tol < required || overlap <= Tol) return false;
            Point2d p1 = first.A + u.MultiplyBy(lo);
            Point2d p2 = first.A + u.MultiplyBy(hi);
            Point2d q1;
            Point2d q2;
            if (!PointAtProjectedStation(second, first.A, u, lo, out q1) ||
                !PointAtProjectedStation(second, first.A, u, hi, out q2)) return false;
            start = Mid(p1, q1);
            end = Mid(p2, q2);
            return Finite(start) && Finite(end);
        }

        private static bool PointAtProjectedStation(ReserveEdge edge, Point2d origin, Vector2d axis, double station, out Point2d point)
        {
            point = new Point2d();
            Vector2d v = new Vector2d(edge.B.X - edge.A.X, edge.B.Y - edge.A.Y);
            double denom = Dot(v, axis);
            if (Math.Abs(denom) <= Tol) return false;
            double startStation = Dot(new Vector2d(edge.A.X - origin.X, edge.A.Y - origin.Y), axis);
            double t = (station - startStation) / denom;
            t = Math.Max(0.0, Math.Min(1.0, t));
            point = new Point2d(edge.A.X + v.X * t, edge.A.Y + v.Y * t);
            return Finite(point);
        }

        private static bool TrySiteLowPoint(Surface surface, List<Parcel> parcels, List<RouteSegment> routes, out Point2d point, out double elevation)
        {
            point = new Point2d();
            elevation = double.NaN;
            bool found = false;
            var samples = new List<Point2d>();
            foreach (Parcel parcel in parcels)
            {
                samples.Add(parcel.Center);
                samples.AddRange(parcel.Points);
            }
            foreach (RouteSegment route in routes)
            {
                samples.Add(route.A);
                samples.Add(route.B);
                samples.Add(Mid(route.A, route.B));
            }
            foreach (Point2d sample in samples)
            {
                double z;
                if (!TryElevation(surface, sample, out z)) continue;
                if (!found || z < elevation)
                {
                    found = true;
                    point = sample;
                    elevation = z;
                }
            }
            return found;
        }

        private static List<RoutePiece> SplitAtJunctionsAndSpacing(List<RouteSegment> routes, double spacing)
        {
            var breaks = routes.ToDictionary(route => route.Id, route => new List<double> { 0.0, 1.0 });
            for (int i = 0; i < routes.Count; i++)
            {
                for (int j = i + 1; j < routes.Count; j++)
                {
                    double ti;
                    double tj;
                    Point2d point;
                    if (!TrySegmentIntersection(routes[i].A, routes[i].B, routes[j].A, routes[j].B, out ti, out tj, out point)) continue;
                    AddBreak(breaks[routes[i].Id], ti);
                    AddBreak(breaks[routes[j].Id], tj);
                }
            }

            var result = new List<RoutePiece>();
            int id = 0;
            foreach (RouteSegment route in routes)
            {
                List<double> values = breaks[route.Id].OrderBy(value => value).ToList();
                for (int i = 0; i < values.Count - 1; i++)
                {
                    Point2d a = Lerp(route.A, route.B, values[i]);
                    Point2d b = Lerp(route.A, route.B, values[i + 1]);
                    double length = Distance(a, b);
                    if (length <= Tol) continue;
                    int pieces = Math.Max(1, (int)Math.Ceiling(length / Math.Max(spacing, 1.0)));
                    Point2d last = a;
                    for (int part = 1; part <= pieces; part++)
                    {
                        Point2d next = Lerp(a, b, (double)part / pieces);
                        if (Distance(last, next) > Tol)
                            result.Add(new RoutePiece(id++, route.ParcelIndex, last, next));
                        last = next;
                    }
                }
            }
            return result;
        }

        private static void OrientPiecesTowardLowPoint(List<RoutePiece> pieces, Surface surface, Point2d lowPoint)
        {
            foreach (RoutePiece piece in pieces)
            {
                double zStart;
                double zEnd;
                bool hasStart = TryElevation(surface, piece.Start, out zStart);
                bool hasEnd = TryElevation(surface, piece.End, out zEnd);
                bool reverse = false;
                if (hasStart && hasEnd && Math.Abs(zStart - zEnd) > 0.001)
                    reverse = zStart < zEnd;
                else
                    reverse = Distance(piece.Start, lowPoint) < Distance(piece.End, lowPoint);
                if (reverse)
                {
                    Point2d temp = piece.Start;
                    piece.Start = piece.End;
                    piece.End = temp;
                }
            }
        }

        private static void ApplyLeafSetback(List<RoutePiece> pieces, Point2d lowPoint, double setback)
        {
            if (setback <= Tol) return;
            var degree = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (RoutePiece piece in pieces)
            {
                Increment(degree, PointKey(piece.Start));
                Increment(degree, PointKey(piece.End));
            }
            foreach (RoutePiece piece in pieces)
            {
                string startKey = PointKey(piece.Start);
                int count;
                if (!degree.TryGetValue(startKey, out count) || count != 1) continue;
                if (Distance(piece.Start, lowPoint) <= Distance(piece.End, lowPoint)) continue;
                double length = Distance(piece.Start, piece.End);
                if (length <= setback + 0.25) continue;
                piece.Start = MoveToward(piece.Start, piece.End, Math.Min(setback, length * 0.45));
            }
        }

        private static void WriteRouteLink(Polyline route, Transaction transaction, ObjectId surfaceId, Point2d lowPoint, double lowElevation, int number)
        {
            if (route.ExtensionDictionary.IsNull) route.CreateExtensionDictionary();
            DBDictionary dictionary = transaction.GetObject(route.ExtensionDictionary, OpenMode.ForWrite, false) as DBDictionary;
            if (dictionary == null) return;
            var record = new Xrecord
            {
                Data = new ResultBuffer(
                    new TypedValue((int)DxfCode.Text, "ROAD_RESERVE_SEWER"),
                    new TypedValue((int)DxfCode.Text, surfaceId.Handle.ToString()),
                    new TypedValue((int)DxfCode.Real, lowPoint.X),
                    new TypedValue((int)DxfCode.Real, lowPoint.Y),
                    new TypedValue((int)DxfCode.Real, lowElevation),
                    new TypedValue((int)DxfCode.Int32, number))
            };
            dictionary.SetAt("CE_ROAD_RESERVE_SEWER", record);
            transaction.AddNewlyCreatedDBObject(record, true);
        }

        private static void AddLowPointMarker(Database database, Transaction transaction, BlockTableRecord space, ObjectId layerId, Point2d point, double elevation, double diameter)
        {
            Point3d p = new Point3d(point.X, point.Y, 0.0);
            var circle = new Circle(p, Vector3d.ZAxis, Math.Max(1.0, diameter));
            circle.SetDatabaseDefaults(database);
            circle.LayerId = layerId;
            space.AppendEntity(circle);
            transaction.AddNewlyCreatedDBObject(circle, true);
            var text = new DBText
            {
                Position = p + new Vector3d(Math.Max(1.0, diameter), Math.Max(1.0, diameter), 0.0),
                TextString = "ROAD RESERVE SEWER - SITE LOW POINT  EL=" + elevation.ToString("0.###", CultureInfo.InvariantCulture),
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
            var record = new LayerTableRecord { Name = name };
            ObjectId id = layers.Add(record);
            transaction.AddNewlyCreatedDBObject(record, true);
            return id;
        }

        private static void EraseByLayers(BlockTableRecord space, Transaction transaction, params string[] layers)
        {
            var names = new HashSet<string>(layers ?? new string[0], StringComparer.OrdinalIgnoreCase);
            foreach (ObjectId id in space.Cast<ObjectId>().ToList())
            {
                Entity entity;
                try { entity = transaction.GetObject(id, OpenMode.ForWrite, false) as Entity; }
                catch { continue; }
                if (entity != null && names.Contains(entity.Layer))
                {
                    try { entity.Erase(); }
                    catch { }
                }
            }
        }

        private static bool SelfIntersects(IList<Point2d> points)
        {
            int count = points.Count;
            for (int i = 0; i < count; i++)
            {
                Point2d a1 = points[i];
                Point2d a2 = points[(i + 1) % count];
                for (int j = i + 1; j < count; j++)
                {
                    if (j == i || j == (i + 1) % count || (i == 0 && j == count - 1)) continue;
                    Point2d b1 = points[j];
                    Point2d b2 = points[(j + 1) % count];
                    double ta;
                    double tb;
                    Point2d intersection;
                    if (TrySegmentIntersection(a1, a2, b1, b2, out ta, out tb, out intersection) &&
                        ta > Tol && ta < 1.0 - Tol && tb > Tol && tb < 1.0 - Tol)
                        return true;
                }
            }
            return false;
        }

        private static bool TrySegmentIntersection(Point2d a, Point2d b, Point2d c, Point2d d, out double ta, out double tb, out Point2d point)
        {
            ta = 0.0;
            tb = 0.0;
            point = new Point2d();
            Vector2d r = new Vector2d(b.X - a.X, b.Y - a.Y);
            Vector2d s = new Vector2d(d.X - c.X, d.Y - c.Y);
            double denom = Cross(r, s);
            if (Math.Abs(denom) <= Tol) return false;
            Vector2d ca = new Vector2d(c.X - a.X, c.Y - a.Y);
            ta = Cross(ca, s) / denom;
            tb = Cross(ca, r) / denom;
            if (ta < -Tol || ta > 1.0 + Tol || tb < -Tol || tb > 1.0 + Tol) return false;
            ta = Math.Max(0.0, Math.Min(1.0, ta));
            tb = Math.Max(0.0, Math.Min(1.0, tb));
            point = Lerp(a, b, ta);
            return Finite(point);
        }

        private static double ParallelAngle(ReserveEdge first, ReserveEdge second)
        {
            Vector2d a = new Vector2d(first.B.X - first.A.X, first.B.Y - first.A.Y).GetNormal();
            Vector2d b = new Vector2d(second.B.X - second.A.X, second.B.Y - second.A.Y).GetNormal();
            double dot = Math.Abs(Math.Max(-1.0, Math.Min(1.0, a.DotProduct(b))));
            return Math.Acos(dot);
        }

        private static double PerpendicularGap(ReserveEdge first, ReserveEdge second)
        {
            Vector2d d = new Vector2d(first.B.X - first.A.X, first.B.Y - first.A.Y);
            if (d.Length <= Tol) return double.MaxValue;
            d = d.GetNormal();
            Vector2d n = new Vector2d(-d.Y, d.X);
            Point2d m1 = Mid(first.A, first.B);
            Point2d m2 = Mid(second.A, second.B);
            return Math.Abs(Dot(new Vector2d(m2.X - m1.X, m2.Y - m1.Y), n));
        }

        private static double ProjectedOverlap(ReserveEdge first, ReserveEdge second)
        {
            Vector2d axis = new Vector2d(first.B.X - first.A.X, first.B.Y - first.A.Y);
            if (axis.Length <= Tol) return 0.0;
            double firstLength = axis.Length;
            axis = axis.GetNormal();
            double c = Dot(new Vector2d(second.A.X - first.A.X, second.A.Y - first.A.Y), axis);
            double d = Dot(new Vector2d(second.B.X - first.A.X, second.B.Y - first.A.Y), axis);
            double lo = Math.Max(0.0, Math.Min(c, d));
            double hi = Math.Min(firstLength, Math.Max(c, d));
            return Math.Max(0.0, hi - lo);
        }

        private static bool PointInside(IList<Point2d> polygon, Point2d point)
        {
            bool inside = false;
            for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
            {
                Point2d pi = polygon[i];
                Point2d pj = polygon[j];
                bool crosses = ((pi.Y > point.Y) != (pj.Y > point.Y)) &&
                               (point.X < (pj.X - pi.X) * (point.Y - pi.Y) / ((pj.Y - pi.Y) + 1e-30) + pi.X);
                if (crosses) inside = !inside;
            }
            return inside;
        }

        private static int UniquePointCount(IEnumerable<Point2d> points)
        {
            return new HashSet<string>(points.Select(PointKey), StringComparer.OrdinalIgnoreCase).Count;
        }

        private static double SignedArea(IList<Point2d> points)
        {
            double value = 0.0;
            for (int i = 0; i < points.Count; i++)
            {
                Point2d a = points[i];
                Point2d b = points[(i + 1) % points.Count];
                value += a.X * b.Y - b.X * a.Y;
            }
            return value * 0.5;
        }

        private static Point2d PolygonCentroid(IList<Point2d> points)
        {
            double area2 = 0.0;
            double cx = 0.0;
            double cy = 0.0;
            for (int i = 0; i < points.Count; i++)
            {
                Point2d a = points[i];
                Point2d b = points[(i + 1) % points.Count];
                double cross = a.X * b.Y - b.X * a.Y;
                area2 += cross;
                cx += (a.X + b.X) * cross;
                cy += (a.Y + b.Y) * cross;
            }
            if (Math.Abs(area2) <= Tol)
                return new Point2d(points.Average(value => value.X), points.Average(value => value.Y));
            return new Point2d(cx / (3.0 * area2), cy / (3.0 * area2));
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

        private static void AddBreak(List<double> values, double value)
        {
            value = Math.Max(0.0, Math.Min(1.0, value));
            if (!values.Any(existing => Math.Abs(existing - value) <= 1e-8)) values.Add(value);
        }

        private static void Increment(Dictionary<string, int> values, string key)
        {
            int current;
            values.TryGetValue(key, out current);
            values[key] = current + 1;
        }

        private static Point2d MoveToward(Point2d from, Point2d to, double distance)
        {
            Vector2d vector = new Vector2d(to.X - from.X, to.Y - from.Y);
            if (vector.Length <= Tol) return from;
            return from + vector.GetNormal().MultiplyBy(distance);
        }

        private static Point2d Lerp(Point2d a, Point2d b, double t)
        {
            return new Point2d(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
        }

        private static Point2d Mid(Point2d a, Point2d b)
        {
            return new Point2d((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5);
        }

        private static string PointKey(Point2d point)
        {
            long x = (long)Math.Round(point.X / Snap, MidpointRounding.AwayFromZero);
            long y = (long)Math.Round(point.Y / Snap, MidpointRounding.AwayFromZero);
            return x.ToString(CultureInfo.InvariantCulture) + ":" + y.ToString(CultureInfo.InvariantCulture);
        }

        private static string UndirectedSegmentKey(Point2d a, Point2d b)
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

        private static double Cross(Vector2d a, Vector2d b) { return a.X * b.Y - a.Y * b.X; }
        private static double Dot(Vector2d a, Vector2d b) { return a.X * b.X + a.Y * b.Y; }

        private sealed class Parcel
        {
            internal Parcel(int index, ObjectId id, List<Point2d> points, Point2d center)
            {
                Index = index; Id = id; Points = points; Center = center;
            }
            internal int Index { get; private set; }
            internal ObjectId Id { get; private set; }
            internal List<Point2d> Points { get; private set; }
            internal Point2d Center { get; private set; }
        }

        private sealed class ReserveEdge
        {
            internal ReserveEdge(int id, int parcelIndex, Point2d a, Point2d b, double length)
            {
                Id = id; ParcelIndex = parcelIndex; A = a; B = b; Length = length;
            }
            internal int Id { get; private set; }
            internal int ParcelIndex { get; private set; }
            internal Point2d A { get; private set; }
            internal Point2d B { get; private set; }
            internal double Length { get; private set; }
        }

        private sealed class OpposingMatch
        {
            internal OpposingMatch(int otherEdgeId, double width, double overlap, double score)
            {
                OtherEdgeId = otherEdgeId; Width = width; Overlap = overlap; Score = score;
            }
            internal int OtherEdgeId { get; private set; }
            internal double Width { get; private set; }
            internal double Overlap { get; private set; }
            internal double Score { get; private set; }
        }

        private sealed class RouteSegment
        {
            internal RouteSegment(int id, int parcelIndex, Point2d a, Point2d b)
            {
                Id = id; ParcelIndex = parcelIndex; A = a; B = b;
            }
            internal int Id { get; private set; }
            internal int ParcelIndex { get; private set; }
            internal Point2d A { get; private set; }
            internal Point2d B { get; private set; }
        }

        private sealed class RoutePiece
        {
            internal RoutePiece(int id, int parcelIndex, Point2d start, Point2d end)
            {
                Id = id; ParcelIndex = parcelIndex; Start = start; End = end;
            }
            internal int Id { get; private set; }
            internal int ParcelIndex { get; private set; }
            internal Point2d Start { get; set; }
            internal Point2d End { get; set; }
        }
    }
}

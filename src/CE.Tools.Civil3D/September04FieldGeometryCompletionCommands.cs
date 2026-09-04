using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil;
using CivilFeatureLine = Autodesk.Civil.DatabaseServices.FeatureLine;

namespace CETools.Civil3D
{
    /// <summary>
    /// Final September 04 field-geometry completion layer.
    ///
    /// The routines in this class deliberately sit after the historical repair
    /// chain.  They use native AutoCAD split curves for T/X breaks (while keeping
    /// the selected database object/handle), add the signed Grid Setting-Out
    /// Design-minus-NG column, provide a repeatable multi-fillet command, and
    /// create a new green endpoint-connector polyline without changing the source
    /// Line/Polyline/FeatureLine objects.
    /// </summary>
    public sealed class September04FieldGeometryCompletionCommands
    {
        private const double Eps = 0.000001;
        private const double JunctionTolerance = 0.01;
        private const int ArcSamples = 16;

        private static double _lastFilletRadius = 0.0;
        private static double _lastFilletSearch = 20.0;
        private static double _lastEndpointDistance = 20.0;
        private static double _lastCentrePairDistance = 20.0;
        private static double _lastCentreJoinDistance = 20.0;
        private static string _lastCentreMode = "Zero-fillet finite centre lines";

        private sealed class SampleSegment
        {
            internal Point2d A;
            internal Point2d B;
            internal double StationA;
            internal double StationB;
        }

        private sealed class Route
        {
            internal ObjectId Id;
            internal bool IsLine;
            internal double Length;
            internal readonly List<SampleSegment> Samples = new List<SampleSegment>();
            internal Point2d Start;
            internal Point2d End;
        }

        private sealed class CentreSegment
        {
            internal ObjectId SourceId;
            internal Point3d Start;
            internal Point3d End;
        }

        private sealed class EndSlot
        {
            internal ObjectId Id;
            internal bool IsStart;
            internal Point3d Point;
            internal Point3d Inner;
        }

        private sealed class ConnectorSource
        {
            internal ObjectId Id;
            internal Point3d Start;
            internal Point3d End;
        }

        [CommandMethod("CE_MULTIFILLET", CommandFlags.Modal | CommandFlags.UsePickSet)]
        public void MultiFilletCommand()
        {
            Document document = Application.DocumentManager.MdiActiveDocument;
            MultiFillet(document);
        }

        [CommandMethod("CE_CONNECTENDPOINTS", CommandFlags.Modal | CommandFlags.UsePickSet)]
        public void ConnectEndpointsCommand()
        {
            Document document = Application.DocumentManager.MdiActiveDocument;
            ConnectEndpoints(document);
        }

        [CommandMethod("CE_GRIDDIFFERENCE", CommandFlags.Modal)]
        public void GridDifferenceCommand()
        {
            Document document = Application.DocumentManager.MdiActiveDocument;
            int changed = EnsureGridDifferenceColumns(document);
            if (document != null)
                document.Editor.WriteMessage("\nCE_GRIDDIFFERENCE complete. Grid table(s) updated={0}.", changed);
        }

        // ---------------------------------------------------------------------
        // T / X BREAK - native split curves, plan-XY detection, keep source
        // ---------------------------------------------------------------------

        internal static void BreakAtJunctions(Document document)
        {
            if (document == null || document.Database == null) return;
            Editor editor = document.Editor;
            PromptSelectionResult selection = SelectLinePolyline(editor,
                "\nSelect LINE/LWPOLYLINE routes to break and KEEP at crossings/T-junctions: ");
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;

            List<ObjectId> ids = selection.Value.GetObjectIds()
                .Where(id => !id.IsNull && !id.IsErased)
                .Distinct()
                .ToList();
            if (ids.Count < 2)
            {
                editor.WriteMessage("\nCE_PLBREAKJUNCTIONS: select at least two LINE/LWPOLYLINE routes.");
                return;
            }

            List<Route> routes = ReadRoutes(document.Database, ids);
            int ignored = Math.Max(0, ids.Count - routes.Count);
            if (routes.Count < 2)
            {
                editor.WriteMessage("\nCE_PLBREAKJUNCTIONS: fewer than two usable open routes were found; ignored={0}.", ignored);
                return;
            }

            var cuts = new Dictionary<ObjectId, List<double>>();
            foreach (Route route in routes) cuts[route.Id] = new List<double>();
            var junctionLocations = new List<Point2d>();

            for (int i = 0; i < routes.Count; i++)
            {
                for (int j = i + 1; j < routes.Count; j++)
                {
                    Route first = routes[i];
                    Route second = routes[j];
                    CollectCrossings(first, second, cuts[first.Id], cuts[second.Id], junctionLocations);
                    CollectEndpointTJunctions(first, second, cuts[first.Id], cuts[second.Id], junctionLocations);
                }
            }

            foreach (List<double> list in cuts.Values)
                NormalizeStations(list);

            int splitSources = 0;
            int newSpans = 0;
            int unchanged = 0;
            foreach (Route route in routes)
            {
                List<double> routeCuts = cuts[route.Id];
                if (routeCuts.Count == 0) continue;
                int created;
                string failure;
                bool ok = route.IsLine
                    ? SplitLineKeepSource(document.Database, route.Id, routeCuts, out created, out failure)
                    : SplitPolylineNativeKeepSource(document.Database, route.Id, routeCuts, out created, out failure);
                if (ok)
                {
                    splitSources++;
                    newSpans += created;
                }
                else
                {
                    unchanged++;
                    editor.WriteMessage("\nRoute {0} kept unchanged: {1}", route.Id.Handle, failure);
                }
            }

            August21DisplayRefresh.Flush(document);
            editor.WriteMessage(
                "\nCE_PLBREAKJUNCTIONS NATIVE KEEP-SOURCE complete. Junctions={0}; sources split={1}; additional spans={2}; unchanged={3}; ignored={4}. No selected source entity was erased.",
                UniquePlanPoints(junctionLocations).Count, splitSources, newSpans, unchanged, ignored);
        }

        private static List<Route> ReadRoutes(Database database, IEnumerable<ObjectId> ids)
        {
            var result = new List<Route>();
            using (Transaction tr = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids)
                {
                    Entity entity;
                    try { entity = tr.GetObject(id, OpenMode.ForRead, false) as Entity; }
                    catch { continue; }
                    if (entity == null || entity.IsErased) continue;

                    Line line = entity as Line;
                    if (line != null && line.Length > Eps)
                    {
                        result.Add(new Route
                        {
                            Id = id,
                            IsLine = true,
                            Length = line.Length,
                            Start = To2d(line.StartPoint),
                            End = To2d(line.EndPoint),
                            Samples =
                            {
                                new SampleSegment
                                {
                                    A = To2d(line.StartPoint), B = To2d(line.EndPoint),
                                    StationA = 0.0, StationB = line.Length
                                }
                            }
                        });
                        continue;
                    }

                    Polyline pl = entity as Polyline;
                    if (pl == null || pl.Closed || pl.NumberOfVertices < 2 || pl.Length <= Eps) continue;
                    Route route = SamplePolyline(pl);
                    if (route != null)
                    {
                        route.Id = id;
                        route.IsLine = false;
                        result.Add(route);
                    }
                }
            }
            return result;
        }

        private static Route SamplePolyline(Polyline pl)
        {
            var route = new Route
            {
                Length = pl.Length,
                Start = To2d(pl.StartPoint),
                End = To2d(pl.EndPoint)
            };

            for (int segment = 0; segment + 1 < pl.NumberOfVertices; segment++)
            {
                bool curved = false;
                try { curved = pl.GetSegmentType(segment) != SegmentType.Line; }
                catch
                {
                    try { curved = Math.Abs(pl.GetBulgeAt(segment)) > Eps; }
                    catch { curved = false; }
                }

                int divisions = curved ? ArcSamples : 1;
                for (int part = 0; part < divisions; part++)
                {
                    double p0 = segment + (double)part / divisions;
                    double p1 = segment + (double)(part + 1) / divisions;
                    Point3d a;
                    Point3d b;
                    try
                    {
                        a = pl.GetPointAtParameter(p0);
                        b = pl.GetPointAtParameter(p1);
                    }
                    catch { continue; }
                    if (PlanDistance(a, b) <= Eps) continue;

                    double s0;
                    double s1;
                    try
                    {
                        s0 = pl.GetDistAtPoint(a);
                        s1 = pl.GetDistAtPoint(b);
                    }
                    catch
                    {
                        // Exact segment endpoints are always valid stations; this
                        // fallback still gives a monotonic station approximation.
                        s0 = pl.Length * p0 / Math.Max(1.0, pl.EndParam);
                        s1 = pl.Length * p1 / Math.Max(1.0, pl.EndParam);
                    }
                    route.Samples.Add(new SampleSegment
                    {
                        A = To2d(a), B = To2d(b), StationA = s0, StationB = s1
                    });
                }
            }
            return route.Samples.Count == 0 ? null : route;
        }

        private static void CollectCrossings(
            Route first, Route second,
            IList<double> firstCuts, IList<double> secondCuts,
            IList<Point2d> locations)
        {
            foreach (SampleSegment a in first.Samples)
            {
                foreach (SampleSegment b in second.Samples)
                {
                    double ta;
                    double tb;
                    Point2d point;
                    if (!TrySegmentIntersection(a.A, a.B, b.A, b.B, out ta, out tb, out point)) continue;
                    double sa = Lerp(a.StationA, a.StationB, Clamp01(ta));
                    double sb = Lerp(b.StationA, b.StationB, Clamp01(tb));
                    bool cutA = AddInternalStation(first, sa, firstCuts);
                    bool cutB = AddInternalStation(second, sb, secondCuts);
                    if (cutA || cutB) AddUniquePoint(locations, point);
                }
            }
        }

        private static void CollectEndpointTJunctions(
            Route first, Route second,
            IList<double> firstCuts, IList<double> secondCuts,
            IList<Point2d> locations)
        {
            AddEndpointAgainstRoute(first.Start, second, secondCuts, locations);
            AddEndpointAgainstRoute(first.End, second, secondCuts, locations);
            AddEndpointAgainstRoute(second.Start, first, firstCuts, locations);
            AddEndpointAgainstRoute(second.End, first, firstCuts, locations);
        }

        private static void AddEndpointAgainstRoute(
            Point2d endpoint, Route host, IList<double> hostCuts, IList<Point2d> locations)
        {
            double bestDistance = double.MaxValue;
            double bestStation = 0.0;
            Point2d bestPoint = endpoint;
            foreach (SampleSegment segment in host.Samples)
            {
                double t;
                Point2d projected;
                double distance = DistanceToSegment(endpoint, segment.A, segment.B, out t, out projected);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestStation = Lerp(segment.StationA, segment.StationB, t);
                    bestPoint = projected;
                }
            }
            if (bestDistance <= JunctionTolerance && AddInternalStation(host, bestStation, hostCuts))
                AddUniquePoint(locations, bestPoint);
        }

        private static bool AddInternalStation(Route route, double station, IList<double> values)
        {
            double endTol = Math.Max(Eps, Math.Min(JunctionTolerance, route.Length * 0.00001));
            if (station <= endTol || station >= route.Length - endTol) return false;
            if (values.Any(value => Math.Abs(value - station) <= endTol)) return false;
            values.Add(station);
            return true;
        }

        private static bool SplitPolylineNativeKeepSource(
            Database database, ObjectId id, IList<double> cuts,
            out int added, out string failure)
        {
            added = 0;
            failure = string.Empty;
            using (Transaction tr = database.TransactionManager.StartTransaction())
            {
                Polyline source = tr.GetObject(id, OpenMode.ForWrite, false) as Polyline;
                if (source == null || source.IsErased || source.Closed || source.NumberOfVertices < 2)
                {
                    failure = "Source is not a usable open lightweight polyline.";
                    return false;
                }
                if (LayerLocked(tr, source.LayerId))
                {
                    failure = "Source layer is locked.";
                    return false;
                }

                List<double> stations = cuts
                    .Where(value => value > Eps && value < source.Length - Eps)
                    .OrderBy(value => value)
                    .ToList();
                NormalizeStations(stations);
                var splitPoints = new Point3dCollection();
                foreach (double station in stations)
                {
                    try { splitPoints.Add(source.GetPointAtDist(station)); }
                    catch { }
                }
                if (splitPoints.Count == 0)
                {
                    failure = "No native split points remained.";
                    return false;
                }

                DBObjectCollection raw = null;
                try
                {
                    raw = source.GetSplitCurves(splitPoints);
                    var pieces = new List<Polyline>();
                    if (raw != null)
                    {
                        foreach (DBObject value in raw)
                        {
                            Polyline piece = value as Polyline;
                            if (piece != null && piece.NumberOfVertices >= 2 && piece.Length > Eps)
                                pieces.Add(piece);
                        }
                    }
                    if (pieces.Count < 2)
                    {
                        failure = "AutoCAD native GetSplitCurves did not return at least two valid polyline spans.";
                        return false;
                    }

                    double sourceLength = source.Length;
                    double pieceLength = pieces.Sum(piece => piece.Length);
                    if (Math.Abs(pieceLength - sourceLength) > Math.Max(0.001, sourceLength * 0.00001))
                    {
                        failure = "Native split length verification failed; source stayed unchanged.";
                        return false;
                    }

                    Point3d sourceStart = source.StartPoint;
                    pieces = pieces.OrderBy(piece => Math.Min(
                        PlanDistance(sourceStart, piece.StartPoint),
                        PlanDistance(sourceStart, piece.EndPoint))).ToList();

                    BlockTableRecord owner = tr.GetObject(source.OwnerId, OpenMode.ForWrite, false) as BlockTableRecord;
                    if (owner == null)
                    {
                        failure = "Source owner space is unavailable.";
                        return false;
                    }

                    for (int index = 1; index < pieces.Count; index++)
                    {
                        Polyline piece = pieces[index];
                        CopyProperties(source, piece);
                        owner.AppendEntity(piece);
                        tr.AddNewlyCreatedDBObject(piece, true);
                        added++;
                    }

                    ReplacePolylineGeometry(source, pieces[0]);
                    try { source.RecordGraphicsModified(true); } catch { }
                    tr.Commit();
                    return true;
                }
                catch (System.Exception ex)
                {
                    failure = ex.Message;
                    return false;
                }
                finally
                {
                    DisposeUnowned(raw);
                }
            }
        }

        private static bool SplitLineKeepSource(
            Database database, ObjectId id, IList<double> cuts,
            out int added, out string failure)
        {
            added = 0;
            failure = string.Empty;
            using (Transaction tr = database.TransactionManager.StartTransaction())
            {
                Line source = tr.GetObject(id, OpenMode.ForWrite, false) as Line;
                if (source == null || source.IsErased || source.Length <= Eps)
                {
                    failure = "Source is not a usable line.";
                    return false;
                }
                if (LayerLocked(tr, source.LayerId))
                {
                    failure = "Source layer is locked.";
                    return false;
                }

                double length = source.Length;
                List<double> stations = cuts.Where(v => v > Eps && v < length - Eps).OrderBy(v => v).ToList();
                NormalizeStations(stations);
                if (stations.Count == 0) { failure = "No internal split stations remained."; return false; }

                Point3d originalStart = source.StartPoint;
                Vector3d direction = (source.EndPoint - source.StartPoint).GetNormal();
                var boundaries = new List<double> { 0.0 };
                boundaries.AddRange(stations);
                boundaries.Add(length);

                BlockTableRecord owner = tr.GetObject(source.OwnerId, OpenMode.ForWrite, false) as BlockTableRecord;
                if (owner == null) { failure = "Source owner space is unavailable."; return false; }
                for (int index = 1; index + 1 < boundaries.Count; index++)
                {
                    Point3d a = originalStart + direction * boundaries[index];
                    Point3d b = originalStart + direction * boundaries[index + 1];
                    if (a.DistanceTo(b) <= Eps) continue;
                    var span = new Line(a, b);
                    CopyProperties(source, span);
                    owner.AppendEntity(span);
                    tr.AddNewlyCreatedDBObject(span, true);
                    added++;
                }
                source.EndPoint = originalStart + direction * boundaries[1];
                try { source.RecordGraphicsModified(true); } catch { }
                tr.Commit();
                return true;
            }
        }

        // ---------------------------------------------------------------------
        // CENTRE CONSTRUCTION - XLINE option retained + zero-fillet finite mode
        // ---------------------------------------------------------------------

        internal static void MiddleConstructionLines(Document document)
        {
            if (document == null || document.Database == null) return;
            Editor editor = document.Editor;
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Centre Construction Lines",
                "Create centre geometry between selected source pairs. Zero-fillet mode extends/trims each centre segment to its closest neighbouring centre-line crossing. The original XLINE construction-entity mode remains available.");
            model.AddPositiveDouble("Maximum", "01 Geometry", "Maximum pair distance", _lastCentrePairDistance,
                "Corresponding source sections farther apart than this are skipped.");
            model.AddPositiveDouble("Join", "01 Geometry", "Maximum zero-fillet join distance", _lastCentreJoinDistance,
                "Only a closest support-line crossing within this distance may move a finite centre-line endpoint.");
            model.AddChoice("Mode", "01 Geometry", "Output", _lastCentreMode,
                "Zero-fillet finite centre lines meet exactly at the closest crossing. XLINE mode creates true AutoCAD construction-line entities.",
                new[] { "Zero-fillet finite centre lines", "Construction XLINE entities" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            _lastCentrePairDistance = Math.Max(Eps, model.Double("Maximum", _lastCentrePairDistance));
            _lastCentreJoinDistance = Math.Max(Eps, model.Double("Join", _lastCentreJoinDistance));
            _lastCentreMode = model.Text("Mode");

            PromptSelectionResult selection = SelectGeneral(editor,
                "\nSelect Lines, Polylines and/or FeatureLines in pairs for centre construction geometry: ");
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;
            ObjectId[] ids = selection.Value.GetObjectIds().Where(id => !id.IsNull && !id.IsErased).Distinct().ToArray();
            if (ids.Length < 2)
            {
                editor.WriteMessage("\nCE_SURVEYMIDCONSTRUCTION requires at least two source objects.");
                return;
            }

            var centres = new List<CentreSegment>();
            using (Transaction tr = document.Database.TransactionManager.StartTransaction())
            {
                for (int pair = 0; pair + 1 < ids.Length; pair += 2)
                {
                    Entity first = tr.GetObject(ids[pair], OpenMode.ForRead, false) as Entity;
                    Entity second = tr.GetObject(ids[pair + 1], OpenMode.ForRead, false) as Entity;
                    List<CentreSegment> a = ReadFiniteSegments(first);
                    List<CentreSegment> b = ReadFiniteSegments(second);
                    int count = Math.Min(a.Count, b.Count);
                    for (int i = 0; i < count; i++)
                    {
                        CentreSegment secondAligned = AlignCentre(a[i], b[i]);
                        if (Math.Max(PlanDistance(a[i].Start, secondAligned.Start), PlanDistance(a[i].End, secondAligned.End)) > _lastCentrePairDistance)
                            continue;
                        centres.Add(new CentreSegment
                        {
                            SourceId = ids[pair],
                            Start = MidPoint(a[i].Start, secondAligned.Start),
                            End = MidPoint(a[i].End, secondAligned.End)
                        });
                    }
                }
            }

            if (centres.Count == 0)
            {
                editor.WriteMessage("\nCE_SURVEYMIDCONSTRUCTION: no usable corresponding straight sections were found.");
                return;
            }

            bool xlineMode = string.Equals(_lastCentreMode, "Construction XLINE entities", StringComparison.OrdinalIgnoreCase);
            if (!xlineMode) ZeroFilletCentreEndpoints(centres, _lastCentreJoinDistance);

            int created = 0;
            using (Transaction tr = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord owner = tr.GetObject(document.Database.CurrentSpaceId, OpenMode.ForWrite, false) as BlockTableRecord;
                if (owner == null) return;
                foreach (CentreSegment segment in centres)
                {
                    Entity source = null;
                    try { source = tr.GetObject(segment.SourceId, OpenMode.ForRead, false) as Entity; } catch { }
                    Vector3d direction = new Vector3d(segment.End.X - segment.Start.X, segment.End.Y - segment.Start.Y, 0.0);
                    if (direction.Length <= Eps) continue;
                    Entity output;
                    if (xlineMode)
                    {
                        var xline = new Xline { BasePoint = segment.Start, UnitDir = direction.GetNormal() };
                        output = xline;
                    }
                    else
                    {
                        output = new Line(segment.Start, segment.End);
                    }
                    output.SetDatabaseDefaults(document.Database);
                    if (source != null) CopyProperties(source, output);
                    owner.AppendEntity(output);
                    tr.AddNewlyCreatedDBObject(output, true);
                    created++;
                }
                tr.Commit();
            }
            August21DisplayRefresh.Flush(document);
            editor.WriteMessage("\nCE_SURVEYMIDCONSTRUCTION complete. Created={0}; mode={1}; unpaired={2}.", created, _lastCentreMode, ids.Length % 2);
        }

        private static void ZeroFilletCentreEndpoints(IList<CentreSegment> segments, double maximumDistance)
        {
            var starts = segments.Select(s => s.Start).ToArray();
            var ends = segments.Select(s => s.End).ToArray();
            for (int i = 0; i < segments.Count; i++)
            {
                Point3d newStart;
                if (ClosestSupportIntersection(i, starts[i], starts, ends, maximumDistance, out newStart))
                    segments[i].Start = newStart;
                Point3d newEnd;
                if (ClosestSupportIntersection(i, ends[i], starts, ends, maximumDistance, out newEnd))
                    segments[i].End = newEnd;
            }
        }

        private static bool ClosestSupportIntersection(
            int sourceIndex, Point3d endpoint, Point3d[] starts, Point3d[] ends,
            double maximumDistance, out Point3d best)
        {
            best = endpoint;
            double bestDistance = maximumDistance + Eps;
            for (int j = 0; j < starts.Length; j++)
            {
                if (j == sourceIndex) continue;
                Point2d intersection;
                if (!TryInfiniteIntersection(To2d(starts[sourceIndex]), To2d(ends[sourceIndex]), To2d(starts[j]), To2d(ends[j]), out intersection))
                    continue;
                double distance = PlanDistance(To2d(endpoint), intersection);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = new Point3d(intersection.X, intersection.Y, endpoint.Z);
                }
            }
            return bestDistance <= maximumDistance;
        }

        // ---------------------------------------------------------------------
        // MULTI FILLET - repeated command/defaults + radius 0 supported
        // ---------------------------------------------------------------------

        private static void MultiFillet(Document document)
        {
            if (document == null || document.Database == null) return;
            Editor editor = document.Editor;
            PromptSelectionResult selection = SelectLinePolyline(editor,
                "\nSelect multiple open Lines/Polylines to fillet: ");
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;
            ObjectId[] ids = selection.Value.GetObjectIds().Where(id => !id.IsNull && !id.IsErased).Distinct().ToArray();
            if (ids.Length < 2) { editor.WriteMessage("\nCE_MULTIFILLET: select at least two objects."); return; }

            PromptDoubleOptions radiusOptions = new PromptDoubleOptions(
                string.Format(CultureInfo.InvariantCulture, "\nSpecify fillet radius <{0:0.###}>: ", _lastFilletRadius));
            radiusOptions.AllowNegative = false;
            radiusOptions.AllowZero = true;
            radiusOptions.DefaultValue = _lastFilletRadius;
            radiusOptions.UseDefaultValue = true;
            PromptDoubleResult radiusResult = editor.GetDouble(radiusOptions);
            if (radiusResult.Status != PromptStatus.OK) return;
            _lastFilletRadius = Math.Max(0.0, radiusResult.Value);

            PromptDoubleOptions searchOptions = new PromptDoubleOptions(
                string.Format(CultureInfo.InvariantCulture, "\nMaximum endpoint pairing distance <{0:0.###}>: ", _lastFilletSearch));
            searchOptions.AllowNegative = false;
            searchOptions.AllowZero = false;
            searchOptions.DefaultValue = _lastFilletSearch;
            searchOptions.UseDefaultValue = true;
            PromptDoubleResult searchResult = editor.GetDouble(searchOptions);
            if (searchResult.Status != PromptStatus.OK) return;
            _lastFilletSearch = Math.Max(Eps, searchResult.Value);

            List<EndSlot> slots = ReadFilletSlots(document.Database, ids);
            var candidates = new List<Tuple<double, EndSlot, EndSlot>>();
            for (int i = 0; i < slots.Count; i++)
                for (int j = i + 1; j < slots.Count; j++)
                    if (slots[i].Id != slots[j].Id)
                    {
                        double distance = PlanDistance(slots[i].Point, slots[j].Point);
                        if (distance <= _lastFilletSearch)
                            candidates.Add(Tuple.Create(distance, slots[i], slots[j]));
                    }
            candidates.Sort((a, b) => a.Item1.CompareTo(b.Item1));

            var used = new HashSet<string>(StringComparer.Ordinal);
            int completed = 0;
            int skipped = 0;
            foreach (Tuple<double, EndSlot, EndSlot> candidate in candidates)
            {
                string keyA = candidate.Item2.Id.Handle + (candidate.Item2.IsStart ? ":S" : ":E");
                string keyB = candidate.Item3.Id.Handle + (candidate.Item3.IsStart ? ":S" : ":E");
                if (used.Contains(keyA) || used.Contains(keyB)) continue;
                string failure;
                if (FilletPair(document.Database, candidate.Item2, candidate.Item3, _lastFilletRadius, out failure))
                {
                    used.Add(keyA);
                    used.Add(keyB);
                    completed++;
                }
                else
                {
                    skipped++;
                    if (!string.IsNullOrWhiteSpace(failure)) editor.WriteMessage("\nFillet pair skipped: {0}", failure);
                }
            }
            August21DisplayRefresh.Flush(document);
            editor.WriteMessage("\nCE_MULTIFILLET complete. Fillets={0}; skipped={1}; radius={2:0.###}. Press Enter at the AutoCAD command line to repeat this command; previous radius/search values are retained.", completed, skipped, _lastFilletRadius);
        }

        private static List<EndSlot> ReadFilletSlots(Database database, IEnumerable<ObjectId> ids)
        {
            var result = new List<EndSlot>();
            using (Transaction tr = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids)
                {
                    Entity entity = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                    Line line = entity as Line;
                    if (line != null && line.Length > Eps)
                    {
                        result.Add(new EndSlot { Id = id, IsStart = true, Point = line.StartPoint, Inner = line.EndPoint });
                        result.Add(new EndSlot { Id = id, IsStart = false, Point = line.EndPoint, Inner = line.StartPoint });
                        continue;
                    }
                    Polyline pl = entity as Polyline;
                    if (pl == null || pl.Closed || pl.NumberOfVertices < 2) continue;
                    if (TerminalStraight(pl, true))
                        result.Add(new EndSlot { Id = id, IsStart = true, Point = pl.StartPoint, Inner = pl.GetPoint3dAt(1) });
                    if (TerminalStraight(pl, false))
                        result.Add(new EndSlot { Id = id, IsStart = false, Point = pl.EndPoint, Inner = pl.GetPoint3dAt(pl.NumberOfVertices - 2) });
                }
            }
            return result;
        }

        private static bool FilletPair(Database database, EndSlot firstSlot, EndSlot secondSlot, double radius, out string failure)
        {
            failure = string.Empty;
            using (Transaction tr = database.TransactionManager.StartTransaction())
            {
                Entity first = tr.GetObject(firstSlot.Id, OpenMode.ForWrite, false) as Entity;
                Entity second = tr.GetObject(secondSlot.Id, OpenMode.ForWrite, false) as Entity;
                if (first == null || second == null) { failure = "entity unavailable"; return false; }
                if (LayerLocked(tr, first.LayerId) || LayerLocked(tr, second.LayerId)) { failure = "locked layer"; return false; }

                EndSlot a;
                EndSlot b;
                if (!ReadCurrentTerminal(first, firstSlot.IsStart, out a) || !ReadCurrentTerminal(second, secondSlot.IsStart, out b))
                { failure = "terminal segment is not straight/open"; return false; }

                Point2d intersection2d;
                if (!TryInfiniteIntersection(To2d(a.Point), To2d(a.Inner), To2d(b.Point), To2d(b.Inner), out intersection2d))
                { failure = "terminal support lines are parallel"; return false; }
                Point3d intersection = new Point3d(intersection2d.X, intersection2d.Y, (a.Point.Z + b.Point.Z) * 0.5);

                Point3d targetA = intersection;
                Point3d targetB = intersection;
                Point3d center = Point3d.Origin;
                bool createArc = radius > Eps;
                if (createArc)
                {
                    Vector3d rayA = new Vector3d(a.Inner.X - intersection.X, a.Inner.Y - intersection.Y, 0.0);
                    Vector3d rayB = new Vector3d(b.Inner.X - intersection.X, b.Inner.Y - intersection.Y, 0.0);
                    if (rayA.Length <= Eps || rayB.Length <= Eps) { failure = "intersection is at an interior control point"; return false; }
                    rayA = rayA.GetNormal();
                    rayB = rayB.GetNormal();
                    double dot = Math.Max(-1.0, Math.Min(1.0, rayA.DotProduct(rayB)));
                    double theta = Math.Acos(dot);
                    if (theta <= 0.001 || Math.Abs(Math.PI - theta) <= 0.001) { failure = "unsupported tangent angle"; return false; }
                    double tangent = radius / Math.Tan(theta * 0.5);
                    double centreDistance = radius / Math.Sin(theta * 0.5);
                    if (tangent <= Eps || double.IsNaN(tangent) || double.IsInfinity(tangent)) { failure = "invalid tangent distance"; return false; }
                    targetA = intersection + rayA * tangent;
                    targetB = intersection + rayB * tangent;
                    Vector3d bisector = rayA + rayB;
                    if (bisector.Length <= Eps) { failure = "angle bisector unavailable"; return false; }
                    center = intersection + bisector.GetNormal() * centreDistance;
                }

                SetTerminal(first, a.IsStart, targetA);
                SetTerminal(second, b.IsStart, targetB);

                if (createArc)
                {
                    double startAngle = Math.Atan2(targetA.Y - center.Y, targetA.X - center.X);
                    double endAngle = Math.Atan2(targetB.Y - center.Y, targetB.X - center.X);
                    double ccw = NormalizeAngle(endAngle - startAngle);
                    Arc arc;
                    if (ccw <= Math.PI)
                        arc = new Arc(center, radius, startAngle, startAngle + ccw);
                    else
                    {
                        double reverse = NormalizeAngle(startAngle - endAngle);
                        arc = new Arc(center, radius, endAngle, endAngle + reverse);
                    }
                    arc.SetDatabaseDefaults(database);
                    CopyProperties(first, arc);
                    BlockTableRecord owner = tr.GetObject(first.OwnerId, OpenMode.ForWrite, false) as BlockTableRecord;
                    if (owner == null) { arc.Dispose(); failure = "owner space unavailable"; return false; }
                    owner.AppendEntity(arc);
                    tr.AddNewlyCreatedDBObject(arc, true);
                }
                tr.Commit();
                return true;
            }
        }

        // ---------------------------------------------------------------------
        // CONNECT ENDPOINTS - sources untouched, new green polyline
        // ---------------------------------------------------------------------

        private static void ConnectEndpoints(Document document)
        {
            if (document == null || document.Database == null) return;
            Editor editor = document.Editor;
            PromptSelectionResult selection = SelectGeneral(editor,
                "\nSelect Lines, Polylines and/or Civil FeatureLines whose endpoints must be connected: ");
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;
            ObjectId[] ids = selection.Value.GetObjectIds().Where(id => !id.IsNull && !id.IsErased).Distinct().ToArray();
            List<ConnectorSource> sources = ReadConnectorSources(document.Database, ids);
            if (sources.Count < 2)
            {
                editor.WriteMessage("\nCE_CONNECTENDPOINTS: fewer than two supported source objects were selected.");
                return;
            }

            PromptDoubleOptions distanceOptions = new PromptDoubleOptions(
                string.Format(CultureInfo.InvariantCulture, "\nMaximum distance between neighbouring endpoints <{0:0.###}>: ", _lastEndpointDistance));
            distanceOptions.AllowNegative = false;
            distanceOptions.AllowZero = false;
            distanceOptions.DefaultValue = _lastEndpointDistance;
            distanceOptions.UseDefaultValue = true;
            PromptDoubleResult distanceResult = editor.GetDouble(distanceOptions);
            if (distanceResult.Status != PromptStatus.OK) return;
            _lastEndpointDistance = Math.Max(Eps, distanceResult.Value);

            PromptPointOptions sideOptions = new PromptPointOptions("\nPick near the endpoint side to connect, or press Enter for automatic closest cluster: ");
            sideOptions.AllowNone = true;
            PromptPointResult sideResult = editor.GetPoint(sideOptions);
            Point3d? sidePick = sideResult.Status == PromptStatus.OK
                ? (Point3d?)sideResult.Value.TransformBy(editor.CurrentUserCoordinateSystem)
                : null;
            if (sideResult.Status != PromptStatus.OK && sideResult.Status != PromptStatus.None) return;

            List<Point3d> chosen = ChooseConnectorEndpoints(sources, sidePick);
            List<Point3d> cluster = LargestConnectedCluster(chosen, _lastEndpointDistance);
            if (cluster.Count < 2)
            {
                editor.WriteMessage("\nCE_CONNECTENDPOINTS: no endpoint cluster met the {0:0.###} distance limit.", _lastEndpointDistance);
                return;
            }
            cluster = SortAlongPrincipalDirection(cluster);

            ObjectId createdId = ObjectId.Null;
            using (Transaction tr = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord owner = tr.GetObject(document.Database.CurrentSpaceId, OpenMode.ForWrite, false) as BlockTableRecord;
                if (owner == null) return;
                var output = new Polyline(cluster.Count);
                output.SetDatabaseDefaults(document.Database);
                output.ColorIndex = 3; // requested green connector
                for (int i = 0; i < cluster.Count; i++)
                    output.AddVertexAt(i, new Point2d(cluster[i].X, cluster[i].Y), 0.0, 0.0, 0.0);
                output.Closed = false;
                owner.AppendEntity(output);
                tr.AddNewlyCreatedDBObject(output, true);
                createdId = output.ObjectId;
                tr.Commit();
            }
            August21DisplayRefresh.Flush(document);
            editor.WriteMessage("\nCE_CONNECTENDPOINTS complete. New green polyline={0}; connected endpoints={1}. Original Lines/Polylines/FeatureLines were not changed. Press Enter to repeat with the previous distance.", createdId.IsNull ? "<none>" : createdId.Handle.ToString(), cluster.Count);
        }

        // ---------------------------------------------------------------------
        // GRID TABLE - DESIGN LEVEL minus NG LEVEL
        // ---------------------------------------------------------------------

        internal static int EnsureGridDifferenceColumns(Document document)
        {
            if (document == null || document.Database == null) return 0;
            int changed = 0;
            try
            {
                using (Transaction tr = document.Database.TransactionManager.StartTransaction())
                {
                    BlockTableRecord space = tr.GetObject(document.Database.CurrentSpaceId, OpenMode.ForRead, false) as BlockTableRecord;
                    if (space == null) return 0;
                    foreach (ObjectId id in space)
                    {
                        Table table = null;
                        try { table = tr.GetObject(id, OpenMode.ForRead, false) as Table; } catch { }
                        if (table == null || table.IsErased || table.Rows.Count < 2 || table.Columns.Count < 2) continue;
                        if (!LooksLikeGridSettingOutTable(table)) continue;
                        table.UpgradeOpen();
                        if (EnsureDifferenceColumn(table)) changed++;
                    }
                    tr.Commit();
                }
            }
            catch { }
            return changed;
        }

        private static bool LooksLikeGridSettingOutTable(Table table)
        {
            for (int row = 0; row < Math.Min(3, table.Rows.Count); row++)
                for (int col = 0; col < table.Columns.Count; col++)
                {
                    string text = CellText(table, row, col).ToUpperInvariant();
                    if (text.Contains("GRID SETTING-OUT")) return true;
                }
            return false;
        }

        private static bool EnsureDifferenceColumn(Table table)
        {
            int headerRow = -1;
            int ng = -1;
            int design = -1;
            int difference = -1;
            for (int row = 0; row < Math.Min(4, table.Rows.Count); row++)
            {
                int rowNg = -1;
                int rowDesign = -1;
                int rowDiff = -1;
                for (int col = 0; col < table.Columns.Count; col++)
                {
                    string header = CellText(table, row, col).Trim().ToUpperInvariant();
                    if (header == "NG LEVEL" || header == "NG") rowNg = col;
                    if (header == "DESIGN LEVEL" || header == "DESIGN") rowDesign = col;
                    if (header == "DIFFERENCE" || header == "DESIGN - NG" || header == "DESIGN-NG") rowDiff = col;
                }
                if (rowNg >= 0 && rowDesign >= 0)
                {
                    headerRow = row;
                    ng = rowNg;
                    design = rowDesign;
                    difference = rowDiff;
                    break;
                }
            }
            if (headerRow < 0) return false;

            if (difference < 0)
            {
                int insertAt = Math.Min(table.Columns.Count, design + 1);
                double width = 18.0;
                try { width = Math.Max(1.0, table.Columns[design].Width); } catch { }
                table.InsertColumns(insertAt, width, 1);
                difference = insertAt;
                if (ng >= insertAt) ng++;
                if (design >= insertAt) design++;
            }
            table.Cells[headerRow, difference].TextString = "DIFFERENCE";

            bool any = false;
            for (int row = headerRow + 1; row < table.Rows.Count; row++)
            {
                double ngValue;
                double designValue;
                if (!TryReadNumber(CellText(table, row, ng), out ngValue) ||
                    !TryReadNumber(CellText(table, row, design), out designValue))
                    continue;
                table.Cells[row, difference].TextString = (designValue - ngValue).ToString("0.000", CultureInfo.InvariantCulture);
                any = true;
            }
            try { table.GenerateLayout(); } catch { }
            try { table.RecordGraphicsModified(true); } catch { }
            return any || difference >= 0;
        }

        // ---------------------------------------------------------------------
        // Shared helpers
        // ---------------------------------------------------------------------

        private static PromptSelectionResult SelectLinePolyline(Editor editor, string message)
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
            }, new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, "LINE,LWPOLYLINE") }));
        }

        private static PromptSelectionResult SelectGeneral(Editor editor, string message)
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

        private static List<CentreSegment> ReadFiniteSegments(Entity source)
        {
            var result = new List<CentreSegment>();
            if (source == null) return result;
            Line line = source as Line;
            if (line != null)
            {
                if (PlanDistance(line.StartPoint, line.EndPoint) > Eps)
                    result.Add(new CentreSegment { SourceId = line.ObjectId, Start = line.StartPoint, End = line.EndPoint });
                return result;
            }
            Polyline pl = source as Polyline;
            if (pl != null)
            {
                int segmentCount = pl.Closed ? pl.NumberOfVertices : pl.NumberOfVertices - 1;
                for (int i = 0; i < segmentCount; i++)
                {
                    if (!SegmentStraight(pl, i)) continue;
                    int next = (i + 1) % pl.NumberOfVertices;
                    result.Add(new CentreSegment { SourceId = pl.ObjectId, Start = pl.GetPoint3dAt(i), End = pl.GetPoint3dAt(next) });
                }
                return result;
            }
            CivilFeatureLine feature = source as CivilFeatureLine;
            if (feature != null && !feature.IsReferenceObject)
            {
                Point3dCollection points = feature.GetPoints(FeatureLinePointType.PIPoint);
                if (points == null) return result;
                int count = feature.Closed ? points.Count : points.Count - 1;
                for (int i = 0; i < count; i++)
                {
                    double bulge = 0.0;
                    try { bulge = feature.GetBulge(i); } catch { }
                    if (Math.Abs(bulge) > Eps) continue;
                    int next = (i + 1) % points.Count;
                    result.Add(new CentreSegment { SourceId = feature.ObjectId, Start = points[i], End = points[next] });
                }
            }
            return result;
        }

        private static CentreSegment AlignCentre(CentreSegment first, CentreSegment second)
        {
            double direct = PlanDistance(first.Start, second.Start) + PlanDistance(first.End, second.End);
            double reverse = PlanDistance(first.Start, second.End) + PlanDistance(first.End, second.Start);
            return reverse < direct
                ? new CentreSegment { SourceId = second.SourceId, Start = second.End, End = second.Start }
                : new CentreSegment { SourceId = second.SourceId, Start = second.Start, End = second.End };
        }

        private static List<ConnectorSource> ReadConnectorSources(Database database, IEnumerable<ObjectId> ids)
        {
            var result = new List<ConnectorSource>();
            using (Transaction tr = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids)
                {
                    Entity entity;
                    try { entity = tr.GetObject(id, OpenMode.ForRead, false) as Entity; } catch { continue; }
                    Line line = entity as Line;
                    if (line != null && line.Length > Eps)
                    {
                        result.Add(new ConnectorSource { Id = id, Start = line.StartPoint, End = line.EndPoint });
                        continue;
                    }
                    Polyline pl = entity as Polyline;
                    if (pl != null && !pl.Closed && pl.NumberOfVertices >= 2)
                    {
                        result.Add(new ConnectorSource { Id = id, Start = pl.StartPoint, End = pl.EndPoint });
                        continue;
                    }
                    CivilFeatureLine feature = entity as CivilFeatureLine;
                    if (feature != null && !feature.IsReferenceObject && !feature.Closed)
                    {
                        Point3dCollection points = feature.GetPoints(FeatureLinePointType.PIPoint);
                        if (points != null && points.Count >= 2)
                            result.Add(new ConnectorSource { Id = id, Start = points[0], End = points[points.Count - 1] });
                    }
                }
            }
            return result;
        }

        private static List<Point3d> ChooseConnectorEndpoints(IList<ConnectorSource> sources, Point3d? pick)
        {
            if (pick.HasValue)
                return sources.Select(s => PlanDistance(s.Start, pick.Value) <= PlanDistance(s.End, pick.Value) ? s.Start : s.End).ToList();

            // Find the endpoint that is most central to one endpoint from every
            // other source, then choose the nearest end of each source to it.
            Point3d bestSeed = sources[0].Start;
            double bestScore = double.MaxValue;
            foreach (ConnectorSource candidateSource in sources)
            {
                foreach (Point3d seed in new[] { candidateSource.Start, candidateSource.End })
                {
                    double score = 0.0;
                    foreach (ConnectorSource other in sources)
                        score += Math.Min(PlanDistance(seed, other.Start), PlanDistance(seed, other.End));
                    if (score < bestScore) { bestScore = score; bestSeed = seed; }
                }
            }
            return sources.Select(s => PlanDistance(s.Start, bestSeed) <= PlanDistance(s.End, bestSeed) ? s.Start : s.End).ToList();
        }

        private static List<Point3d> LargestConnectedCluster(IList<Point3d> points, double maximumDistance)
        {
            var remaining = new HashSet<int>(Enumerable.Range(0, points.Count));
            var best = new List<Point3d>();
            while (remaining.Count > 0)
            {
                int seed = remaining.First();
                remaining.Remove(seed);
                var queue = new Queue<int>();
                queue.Enqueue(seed);
                var indices = new List<int> { seed };
                while (queue.Count > 0)
                {
                    int current = queue.Dequeue();
                    foreach (int candidate in remaining.ToArray())
                    {
                        if (PlanDistance(points[current], points[candidate]) <= maximumDistance)
                        {
                            remaining.Remove(candidate);
                            queue.Enqueue(candidate);
                            indices.Add(candidate);
                        }
                    }
                }
                List<Point3d> cluster = indices.Select(index => points[index]).ToList();
                if (cluster.Count > best.Count) best = cluster;
            }
            return Unique3dPlan(best);
        }

        private static List<Point3d> SortAlongPrincipalDirection(IList<Point3d> points)
        {
            if (points.Count <= 2) return points.ToList();
            int a = 0;
            int b = 1;
            double farthest = -1.0;
            for (int i = 0; i < points.Count; i++)
                for (int j = i + 1; j < points.Count; j++)
                {
                    double d = PlanDistance(points[i], points[j]);
                    if (d > farthest) { farthest = d; a = i; b = j; }
                }
            Vector3d axis = new Vector3d(points[b].X - points[a].X, points[b].Y - points[a].Y, 0.0);
            if (axis.Length <= Eps) return points.ToList();
            axis = axis.GetNormal();
            Point3d origin = points[a];
            return points.OrderBy(p => (p - origin).DotProduct(axis)).ToList();
        }

        private static bool ReadCurrentTerminal(Entity entity, bool start, out EndSlot slot)
        {
            slot = null;
            Line line = entity as Line;
            if (line != null && line.Length > Eps)
            {
                slot = new EndSlot { Id = line.ObjectId, IsStart = start, Point = start ? line.StartPoint : line.EndPoint, Inner = start ? line.EndPoint : line.StartPoint };
                return true;
            }
            Polyline pl = entity as Polyline;
            if (pl == null || pl.Closed || pl.NumberOfVertices < 2 || !TerminalStraight(pl, start)) return false;
            slot = new EndSlot
            {
                Id = pl.ObjectId,
                IsStart = start,
                Point = start ? pl.StartPoint : pl.EndPoint,
                Inner = start ? pl.GetPoint3dAt(1) : pl.GetPoint3dAt(pl.NumberOfVertices - 2)
            };
            return true;
        }

        private static void SetTerminal(Entity entity, bool start, Point3d point)
        {
            Line line = entity as Line;
            if (line != null)
            {
                if (start) line.StartPoint = point; else line.EndPoint = point;
                return;
            }
            Polyline pl = entity as Polyline;
            if (pl != null)
            {
                int index = start ? 0 : pl.NumberOfVertices - 1;
                pl.SetPointAt(index, new Point2d(point.X, point.Y));
            }
        }

        private static bool TerminalStraight(Polyline pl, bool start)
        {
            int segment = start ? 0 : pl.NumberOfVertices - 2;
            return SegmentStraight(pl, segment);
        }

        private static bool SegmentStraight(Polyline pl, int segment)
        {
            try { return pl.GetSegmentType(segment) == SegmentType.Line; }
            catch
            {
                try { return Math.Abs(pl.GetBulgeAt(segment)) <= Eps; }
                catch { return false; }
            }
        }

        private static void ReplacePolylineGeometry(Polyline target, Polyline replacement)
        {
            target.Closed = false;
            while (target.NumberOfVertices > replacement.NumberOfVertices)
                target.RemoveVertexAt(target.NumberOfVertices - 1);
            while (target.NumberOfVertices < replacement.NumberOfVertices)
            {
                int index = target.NumberOfVertices;
                target.AddVertexAt(index, replacement.GetPoint2dAt(index), 0.0, 0.0, 0.0);
            }
            target.Normal = replacement.Normal;
            target.Elevation = replacement.Elevation;
            target.Thickness = replacement.Thickness;
            for (int index = 0; index < replacement.NumberOfVertices; index++)
            {
                target.SetPointAt(index, replacement.GetPoint2dAt(index));
                target.SetBulgeAt(index, replacement.GetBulgeAt(index));
                target.SetStartWidthAt(index, replacement.GetStartWidthAt(index));
                target.SetEndWidthAt(index, replacement.GetEndWidthAt(index));
            }
            target.Closed = false;
        }

        private static void CopyProperties(Entity source, Entity target)
        {
            if (source == null || target == null) return;
            try { target.SetPropertiesFrom(source); } catch { }
            try { target.LayerId = source.LayerId; } catch { }
        }

        private static bool LayerLocked(Transaction tr, ObjectId layerId)
        {
            try
            {
                LayerTableRecord layer = tr.GetObject(layerId, OpenMode.ForRead, false) as LayerTableRecord;
                return layer != null && layer.IsLocked;
            }
            catch { return true; }
        }

        private static bool TrySegmentIntersection(Point2d a, Point2d b, Point2d c, Point2d d,
            out double t, out double u, out Point2d intersection)
        {
            t = 0.0; u = 0.0; intersection = Point2d.Origin;
            double rx = b.X - a.X;
            double ry = b.Y - a.Y;
            double sx = d.X - c.X;
            double sy = d.Y - c.Y;
            double denom = Cross(rx, ry, sx, sy);
            if (Math.Abs(denom) <= 1e-12) return false;
            double qx = c.X - a.X;
            double qy = c.Y - a.Y;
            t = Cross(qx, qy, sx, sy) / denom;
            u = Cross(qx, qy, rx, ry) / denom;
            double lenA = Math.Max(Eps, Math.Sqrt(rx * rx + ry * ry));
            double lenB = Math.Max(Eps, Math.Sqrt(sx * sx + sy * sy));
            double ta = JunctionTolerance / lenA;
            double tb = JunctionTolerance / lenB;
            if (t < -ta || t > 1.0 + ta || u < -tb || u > 1.0 + tb) return false;
            intersection = new Point2d(a.X + Clamp01(t) * rx, a.Y + Clamp01(t) * ry);
            return true;
        }

        private static bool TryInfiniteIntersection(Point2d a, Point2d b, Point2d c, Point2d d, out Point2d intersection)
        {
            intersection = Point2d.Origin;
            double rx = b.X - a.X;
            double ry = b.Y - a.Y;
            double sx = d.X - c.X;
            double sy = d.Y - c.Y;
            double denom = Cross(rx, ry, sx, sy);
            if (Math.Abs(denom) <= 1e-12) return false;
            double qx = c.X - a.X;
            double qy = c.Y - a.Y;
            double t = Cross(qx, qy, sx, sy) / denom;
            intersection = new Point2d(a.X + t * rx, a.Y + t * ry);
            return true;
        }

        private static double DistanceToSegment(Point2d point, Point2d a, Point2d b, out double t, out Point2d projection)
        {
            double dx = b.X - a.X;
            double dy = b.Y - a.Y;
            double len2 = dx * dx + dy * dy;
            if (len2 <= Eps * Eps)
            {
                t = 0.0; projection = a; return PlanDistance(point, a);
            }
            t = ((point.X - a.X) * dx + (point.Y - a.Y) * dy) / len2;
            t = Clamp01(t);
            projection = new Point2d(a.X + t * dx, a.Y + t * dy);
            return PlanDistance(point, projection);
        }

        private static void NormalizeStations(IList<double> values)
        {
            if (values == null) return;
            List<double> sorted = values.OrderBy(v => v).ToList();
            values.Clear();
            foreach (double value in sorted)
            {
                if (values.Count == 0 || Math.Abs(values[values.Count - 1] - value) > Eps)
                    values.Add(value);
            }
        }

        private static List<Point2d> UniquePlanPoints(IEnumerable<Point2d> values)
        {
            var result = new List<Point2d>();
            foreach (Point2d point in values)
                AddUniquePoint(result, point);
            return result;
        }

        private static void AddUniquePoint(IList<Point2d> values, Point2d point)
        {
            if (values.Any(existing => PlanDistance(existing, point) <= JunctionTolerance)) return;
            values.Add(point);
        }

        private static List<Point3d> Unique3dPlan(IEnumerable<Point3d> points)
        {
            var result = new List<Point3d>();
            foreach (Point3d point in points)
                if (!result.Any(existing => PlanDistance(existing, point) <= Eps)) result.Add(point);
            return result;
        }

        private static void DisposeUnowned(DBObjectCollection values)
        {
            if (values == null) return;
            foreach (DBObject value in values)
            {
                try { if (value != null && value.Database == null) value.Dispose(); } catch { }
            }
        }

        private static string CellText(Table table, int row, int column)
        {
            try { return Convert.ToString(table.Cells[row, column].TextString, CultureInfo.InvariantCulture) ?? string.Empty; }
            catch { return string.Empty; }
        }

        private static bool TryReadNumber(string text, out double value)
        {
            string cleaned = (text ?? string.Empty).Trim().Replace(" ", string.Empty);
            if (double.TryParse(cleaned, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out value)) return true;
            return double.TryParse(cleaned, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out value);
        }

        private static Point2d To2d(Point3d point) { return new Point2d(point.X, point.Y); }
        private static Point3d MidPoint(Point3d a, Point3d b) { return new Point3d((a.X+b.X)*0.5, (a.Y+b.Y)*0.5, (a.Z+b.Z)*0.5); }
        private static double Lerp(double a, double b, double t) { return a + (b-a)*t; }
        private static double Clamp01(double value) { return value < 0.0 ? 0.0 : (value > 1.0 ? 1.0 : value); }
        private static double Cross(double ax, double ay, double bx, double by) { return ax*by - ay*bx; }
        private static double NormalizeAngle(double angle) { double twoPi = Math.PI * 2.0; while (angle < 0.0) angle += twoPi; while (angle >= twoPi) angle -= twoPi; return angle; }
        private static double PlanDistance(Point2d a, Point2d b) { double dx=a.X-b.X, dy=a.Y-b.Y; return Math.Sqrt(dx*dx+dy*dy); }
        private static double PlanDistance(Point3d a, Point3d b) { double dx=a.X-b.X, dy=a.Y-b.Y; return Math.Sqrt(dx*dx+dy*dy); }
    }
}

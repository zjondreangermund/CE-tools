using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.August11BellmouthTrimCommands))]

namespace CETools.Civil3D
{
    public sealed class August11BellmouthTrimCommands
    {
        private const string JunctionLayer = "CE-ROAD-JUNCTION";
        private const string EdgeLayer = "CE-ROAD-EDGE";
        private const string ShoulderLayer = "CE-ROAD-SHOULDER";
        private const string RoadLayoutRecordKey = "CE_ROAD_LAYOUT";
        private const double Tol = 1e-7;

        [CommandMethod("CE_TOOLS", "CE_BELLMOUTHTRIMEDGES", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void TrimEdgesToBellmouthTangencies()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Trim Road / Shoulder Edges to Bellmouths",
                "Use the actual start/end tangent points of the generated bellmouth curves. Multiple junctions on the same road edge are handled in one operation; the portions through each junction are removed while outside pieces are retained.");
            model.AddChoice("Target", "01 Targets", "Geometry to trim", "Road edges + sidewalk/shoulder edges", "Choose generated road edges, sidewalk/shoulder edges, or both.", new[] { "Road edges + sidewalk/shoulder edges", "Road edges only", "Sidewalk/shoulder edges only" });
            model.AddChoice("Scope", "01 Targets", "Scope", "All", "Trim all matching CE geometry or only selected target curves.", new[] { "All", "Selected" });
            model.AddPositiveDouble("Tolerance", "02 Tangencies", "Maximum projection distance", 2.5, "Bellmouth tangent endpoints may project onto an outer shoulder edge. Accept a tangent station only when the endpoint is within this distance of the target curve.");
            model.AddPositiveDouble("Grouping", "02 Tangencies", "Legacy junction grouping distance", 50.0, "Only used for older bellmouths that do not contain a CE junction-group ID. New CE bellmouths are grouped exactly by their stored junction ID.");
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            List<ObjectId> targets = ResolveTargets(document, model.Text("Target"), model.Text("Scope"));
            if (targets.Count == 0)
            {
                document.Editor.WriteMessage("\nCE_BELLMOUTHTRIMEDGES: no matching road/shoulder edge curves were found.");
                return;
            }
            double tolerance = Math.Max(0.01, model.Double("Tolerance", 2.5));
            double grouping = Math.Max(1.0, model.Double("Grouping", 50.0));
            int trimmed = 0;
            int removedPieces = 0;
            int keptPieces = 0;

            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(document.Database), OpenMode.ForWrite, false) as BlockTableRecord;
                if (space == null) return;
                List<JunctionGroup> junctions = ReadJunctionGroups(space, transaction, grouping);
                if (junctions.Count == 0)
                {
                    document.Editor.WriteMessage("\nCE_BELLMOUTHTRIMEDGES: no generated bellmouth curves were found on {0}.", JunctionLayer);
                    return;
                }

                foreach (ObjectId targetId in targets.Distinct().ToList())
                {
                    Curve curve;
                    try { curve = transaction.GetObject(targetId, OpenMode.ForWrite, false) as Curve; }
                    catch { continue; }
                    if (curve == null || curve.Closed || curve.IsErased) continue;
                    double total;
                    try { total = curve.GetDistanceAtParameter(curve.EndParam); }
                    catch { continue; }
                    if (total <= Tol) continue;

                    List<Interval> intervals = BuildTrimIntervals(curve, junctions, tolerance, total);
                    if (intervals.Count == 0) continue;
                    List<double> splitDistances = intervals.SelectMany(item => new[] { item.Start, item.End })
                        .Where(value => value > Tol && value < total - Tol)
                        .OrderBy(value => value)
                        .Distinct(new DistanceComparer(1e-5))
                        .ToList();
                    if (splitDistances.Count == 0)
                    {
                        if (intervals.Any(item => item.Start <= Tol && item.End >= total - Tol))
                        {
                            curve.Erase();
                            trimmed++;
                            removedPieces++;
                        }
                        continue;
                    }

                    var points = new Point3dCollection();
                    foreach (double distance in splitDistances)
                    {
                        try { points.Add(curve.GetPointAtDist(distance)); }
                        catch { }
                    }
                    if (points.Count == 0) continue;
                    DBObjectCollection pieces;
                    try { pieces = curve.GetSplitCurves(points); }
                    catch { continue; }
                    foreach (DBObject value in pieces)
                    {
                        Curve piece = value as Curve;
                        if (piece == null) { value.Dispose(); continue; }
                        Point3d midpoint;
                        try { midpoint = piece.GetPointAtParameter((piece.StartParam + piece.EndParam) * 0.5); }
                        catch { piece.Dispose(); continue; }
                        double station;
                        try
                        {
                            Point3d projected = curve.GetClosestPointTo(midpoint, false);
                            station = curve.GetDistAtPoint(projected);
                        }
                        catch { piece.Dispose(); continue; }
                        if (intervals.Any(item => station > item.Start - 1e-5 && station < item.End + 1e-5))
                        {
                            piece.Dispose();
                            removedPieces++;
                            continue;
                        }
                        piece.SetDatabaseDefaults(document.Database);
                        piece.LayerId = curve.LayerId;
                        try { piece.Color = curve.Color; } catch { }
                        try { piece.LinetypeId = curve.LinetypeId; } catch { }
                        try { piece.LineWeight = curve.LineWeight; } catch { }
                        space.AppendEntity(piece);
                        transaction.AddNewlyCreatedDBObject(piece, true);
                        keptPieces++;
                    }
                    curve.Erase();
                    trimmed++;
                }
                transaction.Commit();
            }
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_BELLMOUTHTRIMEDGES complete. Source curves trimmed={0}; outside pieces kept={1}; junction pieces removed={2}.", trimmed, keptPieces, removedPieces);
        }

        private static List<ObjectId> ResolveTargets(Document document, string target, string scope)
        {
            HashSet<string> layers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.Equals(target, "Sidewalk/shoulder edges only", StringComparison.OrdinalIgnoreCase)) layers.Add(EdgeLayer);
            if (!string.Equals(target, "Road edges only", StringComparison.OrdinalIgnoreCase)) layers.Add(ShoulderLayer);
            IEnumerable<ObjectId> ids;
            if (string.Equals(scope, "Selected", StringComparison.OrdinalIgnoreCase))
            {
                PromptSelectionResult selection = document.Editor.GetSelection(new PromptSelectionOptions { MessageForAdding = "\nSelect multiple road-edge / shoulder-edge curves: ", AllowDuplicates = false, RejectObjectsFromNonCurrentSpace = true });
                if (selection.Status != PromptStatus.OK || selection.Value == null) return new List<ObjectId>();
                ids = selection.Value.GetObjectIds();
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
                foreach (ObjectId id in ids.Where(value => !value.IsNull && !value.IsErased).Distinct())
                {
                    Curve curve;
                    try { curve = transaction.GetObject(id, OpenMode.ForRead, false) as Curve; }
                    catch { continue; }
                    if (curve != null && layers.Contains(curve.Layer)) result.Add(id);
                }
            }
            return result;
        }

        private static List<JunctionGroup> ReadJunctionGroups(BlockTableRecord space, Transaction transaction, double legacyGrouping)
        {
            var exact = new Dictionary<string, JunctionGroup>(StringComparer.OrdinalIgnoreCase);
            var legacy = new List<JunctionGroup>();

            foreach (ObjectId id in space)
            {
                Arc arc;
                try { arc = transaction.GetObject(id, OpenMode.ForRead, false) as Arc; }
                catch { continue; }
                if (arc == null || !string.Equals(arc.Layer, JunctionLayer, StringComparison.OrdinalIgnoreCase)) continue;

                string storedGroup;
                if (TryReadStoredJunctionGroup(arc, transaction, out storedGroup) && !string.IsNullOrWhiteSpace(storedGroup))
                {
                    JunctionGroup group;
                    if (!exact.TryGetValue(storedGroup, out group))
                    {
                        group = new JunctionGroup(arc.Center, storedGroup);
                        exact.Add(storedGroup, group);
                    }
                    group.AddArc(arc.Center, arc.StartPoint, arc.EndPoint);
                    continue;
                }

                // Compatibility only for bellmouths created before CE stored a
                // junction-group GUID. Use a more generous distance and keep this
                // path separate from exact new-group handling.
                JunctionGroup legacyGroup = legacy.FirstOrDefault(item => item.Centre.DistanceTo(arc.Center) <= legacyGrouping);
                if (legacyGroup == null)
                {
                    legacyGroup = new JunctionGroup(arc.Center, string.Empty);
                    legacy.Add(legacyGroup);
                }
                legacyGroup.AddArc(arc.Center, arc.StartPoint, arc.EndPoint);
            }

            return exact.Values.Concat(legacy).ToList();
        }

        private static bool TryReadStoredJunctionGroup(Entity entity, Transaction transaction, out string group)
        {
            group = string.Empty;
            if (entity == null || entity.ExtensionDictionary.IsNull) return false;
            try
            {
                DBDictionary dictionary = transaction.GetObject(entity.ExtensionDictionary, OpenMode.ForRead, false) as DBDictionary;
                if (dictionary == null || !dictionary.Contains(RoadLayoutRecordKey)) return false;
                Xrecord record = transaction.GetObject(dictionary.GetAt(RoadLayoutRecordKey), OpenMode.ForRead, false) as Xrecord;
                TypedValue[] values = record == null || record.Data == null ? null : record.Data.AsArray();
                if (values == null || values.Length < 7) return false;
                group = Convert.ToString(values[5].Value) ?? string.Empty;
                return !string.IsNullOrWhiteSpace(group);
            }
            catch
            {
                group = string.Empty;
                return false;
            }
        }

        private static List<Interval> BuildTrimIntervals(Curve curve, IEnumerable<JunctionGroup> junctions, double tolerance, double total)
        {
            var intervals = new List<Interval>();
            foreach (JunctionGroup junction in junctions)
            {
                var stations = new List<double>();
                foreach (Point3d tangent in junction.TangentPoints)
                {
                    try
                    {
                        Point3d projected = curve.GetClosestPointTo(tangent, false);
                        if (PlanDistance(projected, tangent) > tolerance) continue;
                        double station = curve.GetDistAtPoint(projected);
                        if (station >= -Tol && station <= total + Tol) stations.Add(Math.Max(0.0, Math.Min(total, station)));
                    }
                    catch { }
                }
                stations = stations.OrderBy(value => value).Distinct(new DistanceComparer(1e-4)).ToList();
                if (stations.Count < 2) continue;
                double start = stations.First();
                double end = stations.Last();
                if (end - start > Tol) intervals.Add(new Interval(start, end));
            }
            if (intervals.Count <= 1) return intervals;
            intervals = intervals.OrderBy(item => item.Start).ToList();
            var merged = new List<Interval> { intervals[0] };
            for (int i = 1; i < intervals.Count; i++)
            {
                Interval last = merged[merged.Count - 1];
                Interval current = intervals[i];
                if (current.Start <= last.End + 1e-5) merged[merged.Count - 1] = new Interval(last.Start, Math.Max(last.End, current.End));
                else merged.Add(current);
            }
            return merged;
        }

        private static double PlanDistance(Point3d a, Point3d b)
        {
            double dx = a.X - b.X;
            double dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private sealed class JunctionGroup
        {
            private readonly List<Point3d> _arcCentres;

            internal JunctionGroup(Point3d centre, string key)
            {
                Centre = centre;
                Key = key ?? string.Empty;
                TangentPoints = new List<Point3d>();
                _arcCentres = new List<Point3d>();
            }

            internal string Key;
            internal Point3d Centre;
            internal List<Point3d> TangentPoints;

            internal void AddArc(Point3d centre, Point3d start, Point3d end)
            {
                _arcCentres.Add(centre);
                TangentPoints.Add(start);
                TangentPoints.Add(end);
                Centre = new Point3d(
                    _arcCentres.Average(p => p.X),
                    _arcCentres.Average(p => p.Y),
                    _arcCentres.Average(p => p.Z));
            }
        }

        private struct Interval
        {
            internal Interval(double start, double end) { Start = start; End = end; }
            internal double Start;
            internal double End;
        }

        private sealed class DistanceComparer : IEqualityComparer<double>
        {
            private readonly double _tolerance;
            internal DistanceComparer(double tolerance) { _tolerance = Math.Max(tolerance, 1e-9); }
            public bool Equals(double x, double y) { return Math.Abs(x - y) <= _tolerance; }
            public int GetHashCode(double obj) { return Math.Round(obj / _tolerance).GetHashCode(); }
        }
    }
}

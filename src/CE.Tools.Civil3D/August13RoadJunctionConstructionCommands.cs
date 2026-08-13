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
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivilFeatureLine = Autodesk.Civil.DatabaseServices.FeatureLine;

[assembly: CommandClass(typeof(CETools.Civil3D.August13RoadJunctionConstructionCommands))]

namespace CETools.Civil3D
{
    public sealed class August13RoadJunctionConstructionCommands
    {
        [CommandMethod("CE_TOOLS", "CE_ROADJUNCTIONCONSTRUCTIONTOOLS", CommandFlags.Modal)]
        public void Tools()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            DisciplineWorkflowDialogs.SelectAndRun(
                document,
                "CE Tools - Road Junction Construction",
                "Create/finish T- and cross-junction bellmouth geometry, split road corridor regions at the junction limits, exclude only the junction regions from normal corridor processing, then rebuild the construction outputs.",
                new List<DisciplineWorkflowAction>
                {
                    new DisciplineWorkflowAction(
                        "CE-Create T-Junction",
                        "CE_ROADTJUNCTION",
                        "Create linked T-junction bellmouth returns.",
                        "01 Create"),
                    new DisciplineWorkflowAction(
                        "CE-Create Cross-Junction",
                        "CE_ROADCROSSJUNCTION",
                        "Create four linked cross-junction bellmouth returns.",
                        "01 Create"),
                    new DisciplineWorkflowAction(
                        "CE-Finalize Multiple Junction Corridors",
                        "CE_ROADJUNCTIONCONSTRUCTION",
                        "Use selected/all bellmouth curves or feature lines to split multiple road corridors at junction limits and exclude the junction regions.",
                        "02 Corridor"),
                    new DisciplineWorkflowAction(
                        "CE-Repair Corridor Surfaces / Slopes",
                        "CE_ROADCORRIDOROUTPUTFIX",
                        "Rebuild CE-TOP, CE-BOTTOM and corridor slope patterns after the junction-region changes.",
                        "03 Complete"),
                    new DisciplineWorkflowAction(
                        "CE-Junction Setting-Out",
                        "CE_JUNCTIONSETTINGOUT4",
                        "Create construction setting-out for completed T/cross junctions.",
                        "03 Complete")
                });
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_ROADJUNCTIONCONSTRUCTION",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void FinalizeJunctionRegions()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (document == null || civilDocument == null) return;

            var model = new ProductionSettingsDialogModel(
                "CE Tools - Junction Corridor Regions",
                "Split road corridor regions at bellmouth end limits. Regions inside a junction remain in the corridor definition but are excluded from normal through-road processing so dedicated junction/intersection construction can occupy the intersection.");
            model.AddChoice(
                "JunctionScope",
                "01 Junction geometry",
                "Junction source",
                "Selected junction curves / feature lines",
                "Use selected bellmouth arcs, polylines or feature lines, or all CE junction geometry in the drawing.",
                new[]
                {
                    "Selected junction curves / feature lines",
                    "All CE junction geometry"
                });
            model.AddChoice(
                "CorridorScope",
                "02 Corridors",
                "Road corridors",
                "All road corridors",
                "Process all road corridors or only selected corridors.",
                new[]
                {
                    "All road corridors",
                    "Selected road corridors"
                });
            model.AddDouble(
                "MaxOffset",
                "03 Station projection",
                "Maximum junction offset from baseline",
                30.0,
                "Only bellmouth/feature-line limit points within this distance of a road baseline are used.");
            model.AddDouble(
                "Cluster",
                "03 Station projection",
                "Junction station cluster gap",
                40.0,
                "Projected stations separated by more than this distance are treated as different junctions.");
            model.AddDouble(
                "Margin",
                "03 Station projection",
                "Extra corridor exclusion margin",
                0.10,
                "Extra distance before/after each bellmouth limit. Use 0 for exact bellmouth end limits.");
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            bool allJunctions = string.Equals(
                model.Text("JunctionScope"),
                "All CE junction geometry",
                StringComparison.OrdinalIgnoreCase);
            bool selectedCorridors = string.Equals(
                model.Text("CorridorScope"),
                "Selected road corridors",
                StringComparison.OrdinalIgnoreCase);
            double maxOffset = Math.Max(model.Double("MaxOffset", 30.0), 0.01);
            double clusterGap = Math.Max(model.Double("Cluster", 40.0), 0.01);
            double margin = Math.Max(model.Double("Margin", 0.10), 0.0);

            List<Point3d> junctionLimits = ReadJunctionLimitPoints(
                document,
                allJunctions);
            if (junctionLimits.Count < 2)
            {
                document.Editor.WriteMessage(
                    "\nCE_ROADJUNCTIONCONSTRUCTION cancelled. Select at least one bellmouth/return curve or feature line with two usable limit points.");
                return;
            }

            HashSet<ObjectId> corridorIds = selectedCorridors
                ? SelectCorridorIds(document)
                : null;
            if (selectedCorridors &&
                (corridorIds == null || corridorIds.Count == 0))
                return;

            int corridorsChanged = 0;
            int baselinesChanged = 0;
            int splitCount = 0;
            int excludedCount = 0;
            var report = new List<IList<string>>();

            try
            {
                using (DocumentLock documentLock = document.LockDocument())
                using (Transaction transaction =
                    document.Database.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId corridorId in civilDocument.CorridorCollection)
                    {
                        if (selectedCorridors && !corridorIds.Contains(corridorId))
                            continue;

                        Corridor corridor = transaction.GetObject(
                            corridorId,
                            OpenMode.ForWrite,
                            false) as Corridor;
                        if (!IsRoadCorridor(corridor)) continue;

                        int corridorSplits = 0;
                        int corridorExcluded = 0;
                        int corridorBaselines = 0;

                        foreach (Baseline baseline in corridor.Baselines)
                        {
                            if (baseline == null || baseline.AlignmentId.IsNull)
                                continue;
                            CivilAlignment alignment = transaction.GetObject(
                                baseline.AlignmentId,
                                OpenMode.ForRead,
                                false) as CivilAlignment;
                            if (!IsRoadAlignment(alignment)) continue;

                            List<double> stations = ProjectJunctionStations(
                                alignment,
                                junctionLimits,
                                maxOffset);
                            List<StationInterval> intervals = BuildIntervals(
                                stations,
                                clusterGap,
                                margin,
                                baseline.StartStation,
                                baseline.EndStation);
                            if (intervals.Count == 0) continue;

                            baseline.NeedsProcessing = true;
                            foreach (StationInterval interval in intervals)
                            {
                                corridorSplits += SplitAtStation(baseline, interval.Start);
                                corridorSplits += SplitAtStation(baseline, interval.End);
                            }
                            corridorExcluded += ApplyJunctionExclusions(
                                baseline,
                                intervals);
                            corridorBaselines++;
                        }

                        if (corridorSplits > 0 || corridorExcluded > 0)
                        {
                            corridor.Rebuild();
                            corridorsChanged++;
                            baselinesChanged += corridorBaselines;
                            splitCount += corridorSplits;
                            excludedCount += corridorExcluded;
                            report.Add(new List<string>
                            {
                                corridor.Name,
                                corridorBaselines.ToString(CultureInfo.InvariantCulture),
                                corridorSplits.ToString(CultureInfo.InvariantCulture),
                                corridorExcluded.ToString(CultureInfo.InvariantCulture),
                                "Dedicated junction region ready"
                            });
                        }
                    }
                    transaction.Commit();
                }

                document.Editor.Regen();
                GridReportPresenter.ShowReportAndOfferTable(
                    document,
                    "CE Tools - Road Junction Corridor Completion",
                    "Bellmouth/feature-line limits were projected to each road baseline. Regions were split at those limits and only the junction regions were excluded from normal through-road corridor processing.",
                    new List<string>
                    {
                        "Corridor",
                        "Baselines",
                        "Region Splits",
                        "Excluded Junction Regions",
                        "Status"
                    },
                    report,
                    "CE ROAD JUNCTION CORRIDOR REGISTER");
                document.Editor.WriteMessage(
                    "\nCE_ROADJUNCTIONCONSTRUCTION complete. Corridors={0}; baselines={1}; splits={2}; excluded junction regions={3}.",
                    corridorsChanged,
                    baselinesChanged,
                    splitCount,
                    excludedCount);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_ROADJUNCTIONCONSTRUCTION failed. No transaction was committed. {0}",
                    exception.Message);
            }
        }

        private static List<Point3d> ReadJunctionLimitPoints(
            Document document,
            bool allGenerated)
        {
            var result = new List<Point3d>();
            ObjectId[] ids;

            if (allGenerated)
            {
                var found = new List<ObjectId>();
                using (Transaction transaction =
                    document.Database.TransactionManager.StartTransaction())
                {
                    BlockTable blockTable = transaction.GetObject(
                        document.Database.BlockTableId,
                        OpenMode.ForRead,
                        false) as BlockTable;
                    BlockTableRecord modelSpace = blockTable == null
                        ? null
                        : transaction.GetObject(
                            blockTable[BlockTableRecord.ModelSpace],
                            OpenMode.ForRead,
                            false) as BlockTableRecord;
                    if (modelSpace != null)
                    {
                        foreach (ObjectId id in modelSpace)
                        {
                            Entity entity = transaction.GetObject(
                                id,
                                OpenMode.ForRead,
                                false) as Entity;
                            if (entity != null && IsGeneratedJunctionEntity(entity))
                                found.Add(id);
                        }
                    }
                }
                ids = found.ToArray();
            }
            else
            {
                PromptSelectionOptions options = new PromptSelectionOptions
                {
                    MessageForAdding =
                        "\nSelect multiple junction bellmouth arcs/polylines/feature lines: ",
                    AllowDuplicates = false
                };
                PromptSelectionResult selection = document.Editor.GetSelection(options);
                if (selection.Status != PromptStatus.OK || selection.Value == null)
                    return result;
                ids = selection.Value.GetObjectIds();
            }

            using (Transaction transaction =
                document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids)
                {
                    DBObject value = transaction.GetObject(
                        id,
                        OpenMode.ForRead,
                        false);
                    Point3d start;
                    Point3d end;
                    if (!TryReadLimits(value, out start, out end)) continue;
                    AddUniquePoint(result, start, 0.005);
                    AddUniquePoint(result, end, 0.005);
                }
            }
            return result;
        }

        private static bool TryReadLimits(
            DBObject value,
            out Point3d start,
            out Point3d end)
        {
            start = Point3d.Origin;
            end = Point3d.Origin;

            Curve curve = value as Curve;
            if (curve != null)
            {
                try
                {
                    start = curve.StartPoint;
                    end = curve.EndPoint;
                    return start.DistanceTo(end) > 1e-6;
                }
                catch { }
            }

            CivilFeatureLine featureLine = value as CivilFeatureLine;
            if (featureLine != null)
            {
                try
                {
                    Point3dCollection points = featureLine.GetPoints(
                        FeatureLinePointType.AllPoints);
                    if (points != null && points.Count >= 2)
                    {
                        start = points[0];
                        end = points[points.Count - 1];
                        return start.DistanceTo(end) > 1e-6;
                    }
                }
                catch { }
            }
            return false;
        }

        private static bool IsGeneratedJunctionEntity(Entity entity)
        {
            string layer = entity == null ? string.Empty : entity.Layer ?? string.Empty;
            if (layer.IndexOf("JUNCTION", StringComparison.OrdinalIgnoreCase) >= 0 ||
                layer.IndexOf("BELLMOUTH", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            try
            {
                ResultBuffer buffer = entity.GetXDataForApplication("CE_ROAD_JUNCTION");
                if (buffer != null)
                {
                    buffer.Dispose();
                    return true;
                }
            }
            catch { }
            return false;
        }

        private static void AddUniquePoint(
            IList<Point3d> points,
            Point3d point,
            double tolerance)
        {
            if (points.Any(item => item.DistanceTo(point) <= tolerance)) return;
            points.Add(point);
        }

        private static HashSet<ObjectId> SelectCorridorIds(Document document)
        {
            var result = new HashSet<ObjectId>();
            PromptSelectionOptions options = new PromptSelectionOptions
            {
                MessageForAdding = "\nSelect the road corridors to split at junctions: ",
                AllowDuplicates = false
            };
            PromptSelectionResult selection = document.Editor.GetSelection(options);
            if (selection.Status != PromptStatus.OK || selection.Value == null)
                return result;

            using (Transaction transaction =
                document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in selection.Value.GetObjectIds())
                {
                    Corridor corridor = transaction.GetObject(
                        id,
                        OpenMode.ForRead,
                        false) as Corridor;
                    if (corridor != null) result.Add(id);
                }
            }
            return result;
        }

        private static List<double> ProjectJunctionStations(
            CivilAlignment alignment,
            IEnumerable<Point3d> points,
            double maxOffset)
        {
            var stations = new List<double>();
            foreach (Point3d point in points)
            {
                double station = 0.0;
                double offset = 0.0;
                try
                {
                    alignment.StationOffset(
                        point.X,
                        point.Y,
                        ref station,
                        ref offset);
                }
                catch
                {
                    continue;
                }
                if (Math.Abs(offset) <= maxOffset)
                    stations.Add(station);
            }
            return stations
                .Distinct(new StationComparer(0.005))
                .OrderBy(value => value)
                .ToList();
        }

        private static List<StationInterval> BuildIntervals(
            IList<double> stations,
            double clusterGap,
            double margin,
            double baselineStart,
            double baselineEnd)
        {
            var result = new List<StationInterval>();
            if (stations == null || stations.Count < 2) return result;

            var current = new List<double>();
            foreach (double station in stations.OrderBy(value => value))
            {
                if (current.Count == 0 ||
                    station - current[current.Count - 1] <= clusterGap)
                {
                    current.Add(station);
                }
                else
                {
                    AddInterval(result, current, margin, baselineStart, baselineEnd);
                    current.Clear();
                    current.Add(station);
                }
            }
            AddInterval(result, current, margin, baselineStart, baselineEnd);
            return MergeIntervals(result);
        }

        private static void AddInterval(
            ICollection<StationInterval> result,
            IList<double> cluster,
            double margin,
            double baselineStart,
            double baselineEnd)
        {
            if (cluster == null || cluster.Count < 2) return;
            double start = Math.Max(baselineStart, cluster.Min() - margin);
            double end = Math.Min(baselineEnd, cluster.Max() + margin);
            if (end - start <= 0.03) return;
            result.Add(new StationInterval(start, end));
        }

        private static List<StationInterval> MergeIntervals(
            IEnumerable<StationInterval> source)
        {
            var ordered = source.OrderBy(item => item.Start).ToList();
            var result = new List<StationInterval>();
            foreach (StationInterval item in ordered)
            {
                if (result.Count == 0 ||
                    item.Start > result[result.Count - 1].End + 0.01)
                {
                    result.Add(new StationInterval(item.Start, item.End));
                }
                else
                {
                    result[result.Count - 1].End = Math.Max(
                        result[result.Count - 1].End,
                        item.End);
                }
            }
            return result;
        }

        private static int SplitAtStation(Baseline baseline, double station)
        {
            if (baseline == null) return 0;
            for (int index = 0; index < baseline.BaselineRegions.Count; index++)
            {
                BaselineRegion region = baseline.BaselineRegions[index];
                if (region == null) continue;
                if (station <= region.StartStation + 0.011 ||
                    station >= region.EndStation - 0.011)
                    continue;
                if (station > region.StartStation && station < region.EndStation)
                {
                    region.Split(station);
                    return 1;
                }
            }
            return 0;
        }

        private static int ApplyJunctionExclusions(
            Baseline baseline,
            IList<StationInterval> intervals)
        {
            int excluded = 0;
            int sequence = 1;
            for (int index = 0; index < baseline.BaselineRegions.Count; index++)
            {
                BaselineRegion region = baseline.BaselineRegions[index];
                if (region == null) continue;
                double middle = (region.StartStation + region.EndStation) * 0.5;
                bool inside = intervals.Any(item =>
                    middle >= item.Start - 0.005 &&
                    middle <= item.End + 0.005);
                region.NeedsProcessing = !inside;
                if (inside)
                {
                    try
                    {
                        region.Name = string.Format(
                            CultureInfo.InvariantCulture,
                            "CE-JUNCTION-EXCLUDE-{0:000}",
                            sequence++);
                    }
                    catch { }
                    excluded++;
                }
            }
            return excluded;
        }

        private static bool IsRoadAlignment(CivilAlignment alignment)
        {
            if (alignment == null) return false;
            string name = alignment.Name ?? string.Empty;
            string description = alignment.Description ?? string.Empty;
            return name.StartsWith("RD", StringComparison.OrdinalIgnoreCase) ||
                   name.StartsWith("ROAD", StringComparison.OrdinalIgnoreCase) ||
                   description.IndexOf("CE road", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsRoadCorridor(Corridor corridor)
        {
            if (corridor == null) return false;
            string name = corridor.Name ?? string.Empty;
            string description = corridor.Description ?? string.Empty;
            return name.IndexOf("CORRIDOR", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.StartsWith("RD", StringComparison.OrdinalIgnoreCase) ||
                   description.IndexOf("CE road", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private sealed class StationInterval
        {
            internal StationInterval(double start, double end)
            {
                Start = start;
                End = end;
            }
            internal double Start { get; set; }
            internal double End { get; set; }
        }

        private sealed class StationComparer : IEqualityComparer<double>
        {
            private readonly double _tolerance;
            internal StationComparer(double tolerance)
            {
                _tolerance = Math.Max(tolerance, 1e-9);
            }
            public bool Equals(double x, double y)
            {
                return Math.Abs(x - y) <= _tolerance;
            }
            public int GetHashCode(double value)
            {
                return Math.Round(value / _tolerance).GetHashCode();
            }
        }
    }
}

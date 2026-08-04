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
using CETools.Core;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.FlowNetworkCulvertCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Extracts major D8 flow links above a contributing-area threshold and screens
    /// their plan intersections with selected road/kerb/centreline curves. Candidate
    /// markers are review points only and require full culvert hydraulic/design checks.
    /// </summary>
    public sealed class FlowNetworkCulvertCommands
    {
        private const string RegAppName = "CE_HYDROLOGY_REVIEW";
        private const string ReviewLayer = "CE-HYDROLOGY-REVIEW";
        private const int MaximumNetworkEdges = 20000;
        private const int MaximumCurveSamples = 5000;
        private const double Tolerance = 1e-9;

        [CommandMethod("CE_TOOLS", "CE_FLOWNETWORK", CommandFlags.Modal | CommandFlags.Redraw)]
        public void FlowNetwork()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            HydrologyCivilInput input;
            if (!SurfaceHydrologyCommands.PromptAnalysisInput(document, out input)) return;

            double thresholdHectares;
            if (!PromptPositiveDouble(
                    document.Editor,
                    "Minimum contributing area for major flow links (ha)",
                    1.0,
                    out thresholdHectares))
                return;

            List<ObjectId> crossingCurveIds = PromptCrossingCurves(document.Editor);
            if (crossingCurveIds == null) return;

            try
            {
                HydrologySample sample = SurfaceHydrologyCommands.SampleAndAnalyse(
                    document.Database,
                    input);
                double thresholdDrawingArea = thresholdHectares * 10000.0 *
                    input.UnitsPerMetre * input.UnitsPerMetre;
                List<MajorFlowEdge> edges = BuildMajorEdges(
                    sample,
                    thresholdDrawingArea);
                if (edges.Count == 0)
                {
                    document.Editor.WriteMessage(
                        "\nCE_FLOWNETWORK complete. No D8 flow link met the {0:N3} ha threshold.",
                        thresholdHectares);
                    return;
                }
                if (edges.Count > MaximumNetworkEdges)
                {
                    document.Editor.WriteMessage(
                        "\nCE_FLOWNETWORK stopped. {0:N0} major flow links exceed the {1:N0} graphics limit. Increase the contributing-area threshold or grid spacing.",
                        edges.Count,
                        MaximumNetworkEdges);
                    return;
                }

                List<PlanSegment> crossingSegments = ReadCrossingSegments(
                    document.Database,
                    crossingCurveIds,
                    input.Spacing);
                List<CulvertCandidate> candidates = FindCandidates(
                    sample,
                    edges,
                    crossingSegments,
                    input.UnitsPerMetre);
                double maximumArea = edges.Max(item =>
                    item.AccumulationDrawingArea /
                    (input.UnitsPerMetre * input.UnitsPerMetre) / 10000.0);
                double networkLength = edges.Sum(item => item.PlanLength) /
                    input.UnitsPerMetre;
                List<IList<string>> rows = BuildRows(
                    candidates,
                    edges.Count,
                    networkLength,
                    maximumArea,
                    thresholdHectares);

                GridReportPresenter.ShowReportAndOfferTable(
                    document,
                    "CE Tools - Major Flow Network and Culvert Candidates",
                    string.Format(
                        CultureInfo.CurrentCulture,
                        "Priority-filled D8 screening. Major links={0}; network length={1:N1} m; candidate road crossings={2}. Candidate positions require survey, alignment, inlet/outlet and hydraulic verification.",
                        edges.Count,
                        networkLength,
                        candidates.Count),
                    rows,
                    "CE TOOLS FLOW NETWORK AND CULVERT CANDIDATES");

                var review = new List<KeyValuePair<string, string>>
                {
                    Pair("Major flow links", edges.Count.ToString(CultureInfo.InvariantCulture)),
                    Pair("Contributing-area threshold", thresholdHectares.ToString("N3", CultureInfo.CurrentCulture) + " ha"),
                    Pair("Network plan length", networkLength.ToString("N1", CultureInfo.CurrentCulture) + " m"),
                    Pair("Maximum accumulated area", maximumArea.ToString("N3", CultureInfo.CurrentCulture) + " ha"),
                    Pair("Selected crossing curves", crossingCurveIds.Count.ToString(CultureInfo.InvariantCulture)),
                    Pair("Candidate crossings", candidates.Count.ToString(CultureInfo.InvariantCulture)),
                    Pair("Source surface/boundary/roads changed", "No"),
                    Pair("Design status", "Position screening only — use CE_CULVERTREVIEW and full inlet/outlet/flood analysis")
                };
                if (!PopupTablePresenter.ShowReview(
                        "CE Tools - Create Flow Network Review",
                        "The network follows sampled priority-filled D8 links. Candidate crossings are geometric intersections with selected curves, not approved culvert positions.",
                        review,
                        "Create Review"))
                    return;

                int generated = CreateGraphics(
                    document.Database,
                    input,
                    sample,
                    edges,
                    candidates);
                document.Editor.Regen();
                document.Editor.WriteMessage(
                    "\nCE_FLOWNETWORK complete. Links={0}; candidates={1}; generated graphics={2}.",
                    edges.Count,
                    candidates.Count,
                    generated);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_FLOWNETWORK failed. No source surface, boundary or crossing curve was modified. {0}",
                    exception.Message);
            }
        }

        private static List<MajorFlowEdge> BuildMajorEdges(
            HydrologySample sample,
            double thresholdDrawingArea)
        {
            var result = new List<MajorFlowEdge>();
            for (int index = 0; index < sample.Analysis.Active.Count; index++)
            {
                if (!sample.Analysis.Active[index] ||
                    sample.Analysis.AccumulationArea[index] + Tolerance < thresholdDrawingArea)
                    continue;
                int downstream = sample.Analysis.FlowTo[index];
                if (downstream < 0 || !sample.Analysis.Active[downstream]) continue;
                Point3d start = SurfaceHydrologyCommands.CellPoint(sample, index, true);
                Point3d end = SurfaceHydrologyCommands.CellPoint(sample, downstream, true);
                double dx = end.X - start.X;
                double dy = end.Y - start.Y;
                double length = Math.Sqrt(dx * dx + dy * dy);
                if (length <= Tolerance) continue;
                result.Add(new MajorFlowEdge(
                    index,
                    downstream,
                    start,
                    end,
                    length,
                    sample.Analysis.AccumulationArea[index]));
            }
            return result;
        }

        private static List<ObjectId> PromptCrossingCurves(Editor editor)
        {
            var options = new PromptKeywordOptions(
                "\nSelect road/kerb/centreline curves for crossing screening [Yes/No] <Yes>: ")
            {
                AllowNone = true
            };
            options.Keywords.Add("Yes");
            options.Keywords.Add("No");
            PromptResult result = editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel) return null;
            bool select = result.Status == PromptStatus.None ||
                string.Equals(result.StringResult, "Yes", StringComparison.OrdinalIgnoreCase);
            if (!select) return new List<ObjectId>();

            PromptSelectionResult selection = editor.GetSelection(
                new PromptSelectionOptions
                {
                    MessageForAdding = "\nSelect road, kerb or centreline curves: "
                });
            if (selection.Status == PromptStatus.Cancel) return null;
            if (selection.Status != PromptStatus.OK) return new List<ObjectId>();
            return selection.Value.GetObjectIds().ToList();
        }

        private static List<PlanSegment> ReadCrossingSegments(
            Database database,
            IEnumerable<ObjectId> ids,
            double gridSpacing)
        {
            var result = new List<PlanSegment>();
            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids)
                {
                    Curve curve;
                    try
                    {
                        curve = transaction.GetObject(
                            id,
                            OpenMode.ForRead,
                            false) as Curve;
                    }
                    catch
                    {
                        continue;
                    }
                    if (curve == null) continue;
                    LayerTableRecord layer = transaction.GetObject(
                        curve.LayerId,
                        OpenMode.ForRead,
                        false) as LayerTableRecord;
                    if (layer != null && layer.IsLocked) continue;
                    List<Point3d> points = SampleCurve(
                        curve,
                        Math.Max(gridSpacing * 0.25, Tolerance));
                    for (int index = 1; index < points.Count; index++)
                    {
                        Point3d first = points[index - 1];
                        Point3d second = points[index];
                        if (PlanDistance(first, second) <= Tolerance) continue;
                        result.Add(new PlanSegment(
                            id,
                            curve.Layer,
                            first,
                            second));
                    }
                }
            }
            return result;
        }

        private static List<Point3d> SampleCurve(
            Curve curve,
            double step)
        {
            Line line = curve as Line;
            if (line != null)
                return new List<Point3d> { line.StartPoint, line.EndPoint };

            double startDistance;
            double endDistance;
            try
            {
                startDistance = curve.GetDistanceAtParameter(curve.StartParam);
                endDistance = curve.GetDistanceAtParameter(curve.EndParam);
            }
            catch
            {
                return new List<Point3d>();
            }
            double length = Math.Abs(endDistance - startDistance);
            int divisions = Math.Max(1, Math.Min(
                MaximumCurveSamples,
                (int)Math.Ceiling(length / step)));
            var points = new List<Point3d>();
            for (int index = 0; index <= divisions; index++)
            {
                double distance = startDistance +
                    (endDistance - startDistance) * index / divisions;
                try
                {
                    Point3d point = curve.GetPointAtDist(distance);
                    if (points.Count == 0 ||
                        PlanDistance(points[points.Count - 1], point) > Tolerance)
                        points.Add(point);
                }
                catch
                {
                    // A failed sample is omitted; later valid samples remain usable.
                }
            }
            return points;
        }

        private static List<CulvertCandidate> FindCandidates(
            HydrologySample sample,
            IList<MajorFlowEdge> flowEdges,
            IList<PlanSegment> crossingSegments,
            double unitsPerMetre)
        {
            var result = new List<CulvertCandidate>();
            double mergeDistance = sample.CellSize * 0.35;
            foreach (MajorFlowEdge flow in flowEdges)
            {
                foreach (PlanSegment crossing in crossingSegments)
                {
                    Point2d intersection;
                    if (!TrySegmentIntersection(
                            new Point2d(flow.Start.X, flow.Start.Y),
                            new Point2d(flow.End.X, flow.End.Y),
                            new Point2d(crossing.Start.X, crossing.Start.Y),
                            new Point2d(crossing.End.X, crossing.End.Y),
                            out intersection))
                        continue;
                    if (result.Any(item =>
                        Distance(item.Position.X, item.Position.Y, intersection.X, intersection.Y) <= mergeDistance))
                        continue;
                    double z = sample.Analysis.OriginalElevations[flow.StartIndex];
                    double areaHectares = flow.AccumulationDrawingArea /
                        (unitsPerMetre * unitsPerMetre) / 10000.0;
                    double bearing = EngineeringBearing(flow.Start, flow.End);
                    result.Add(new CulvertCandidate(
                        result.Count + 1,
                        new Point3d(intersection.X, intersection.Y, z),
                        areaHectares,
                        bearing,
                        crossing.Layer,
                        crossing.SourceId.Handle.ToString(),
                        flow.StartIndex));
                }
            }
            return result
                .OrderByDescending(item => item.ContributingAreaHectares)
                .ThenBy(item => item.CandidateNumber)
                .Select((item, index) => item.WithCandidateNumber(index + 1))
                .ToList();
        }

        private static bool TrySegmentIntersection(
            Point2d firstStart,
            Point2d firstEnd,
            Point2d secondStart,
            Point2d secondEnd,
            out Point2d intersection)
        {
            intersection = Point2d.Origin;
            double rx = firstEnd.X - firstStart.X;
            double ry = firstEnd.Y - firstStart.Y;
            double sx = secondEnd.X - secondStart.X;
            double sy = secondEnd.Y - secondStart.Y;
            double denominator = Cross(rx, ry, sx, sy);
            if (Math.Abs(denominator) <= Tolerance) return false;
            double qx = secondStart.X - firstStart.X;
            double qy = secondStart.Y - firstStart.Y;
            double t = Cross(qx, qy, sx, sy) / denominator;
            double u = Cross(qx, qy, rx, ry) / denominator;
            if (t < -Tolerance || t > 1.0 + Tolerance ||
                u < -Tolerance || u > 1.0 + Tolerance)
                return false;
            intersection = new Point2d(
                firstStart.X + t * rx,
                firstStart.Y + t * ry);
            return true;
        }

        private static double Cross(
            double firstX,
            double firstY,
            double secondX,
            double secondY)
        {
            return firstX * secondY - firstY * secondX;
        }

        private static double EngineeringBearing(
            Point3d start,
            Point3d end)
        {
            double bearing = Math.Atan2(
                end.X - start.X,
                end.Y - start.Y) * 180.0 / Math.PI;
            if (bearing < 0.0) bearing += 360.0;
            return bearing;
        }

        private static double Distance(
            double firstX,
            double firstY,
            double secondX,
            double secondY)
        {
            double dx = secondX - firstX;
            double dy = secondY - firstY;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static double PlanDistance(Point3d first, Point3d second)
        {
            return Distance(first.X, first.Y, second.X, second.Y);
        }

        private static List<IList<string>> BuildRows(
            IList<CulvertCandidate> candidates,
            int edgeCount,
            double networkLengthMetres,
            double maximumAreaHectares,
            double thresholdHectares)
        {
            var rows = new List<IList<string>>
            {
                new List<string>
                {
                    "ITEM", "X", "Y", "SURFACE Z", "AREA (ha)",
                    "FLOW BEARING", "CROSSING LAYER", "SOURCE HANDLE / ACTION"
                },
                new List<string>
                {
                    "NETWORK SUMMARY", string.Empty, string.Empty, string.Empty,
                    maximumAreaHectares.ToString("0.######", CultureInfo.InvariantCulture),
                    string.Empty,
                    edgeCount + " links; " + networkLengthMetres.ToString("0.###", CultureInfo.InvariantCulture) + " m",
                    "Threshold " + thresholdHectares.ToString("0.###", CultureInfo.InvariantCulture) + " ha"
                }
            };
            if (candidates.Count == 0)
            {
                rows.Add(new List<string>
                {
                    "NO CROSSINGS", string.Empty, string.Empty, string.Empty,
                    string.Empty, string.Empty, string.Empty,
                    "No selected curve intersected a qualifying D8 link"
                });
                return rows;
            }
            foreach (CulvertCandidate candidate in candidates)
            {
                rows.Add(new List<string>
                {
                    "C" + candidate.CandidateNumber,
                    candidate.Position.X.ToString("0.###", CultureInfo.InvariantCulture),
                    candidate.Position.Y.ToString("0.###", CultureInfo.InvariantCulture),
                    candidate.Position.Z.ToString("0.###", CultureInfo.InvariantCulture),
                    candidate.ContributingAreaHectares.ToString("0.######", CultureInfo.InvariantCulture),
                    candidate.FlowBearingDegrees.ToString("0.0", CultureInfo.InvariantCulture) + " deg",
                    candidate.CrossingLayer,
                    candidate.CrossingHandle + " / verify alignment, skew, levels and hydraulics"
                });
            }
            return rows;
        }

        private static int CreateGraphics(
            Database database,
            HydrologyCivilInput input,
            HydrologySample sample,
            IList<MajorFlowEdge> edges,
            IList<CulvertCandidate> candidates)
        {
            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                EnsureRegApp(database, transaction);
                ObjectId layerId = GetOrCreateLayer(
                    database,
                    transaction,
                    ReviewLayer);
                BlockTableRecord currentSpace = transaction.GetObject(
                    database.CurrentSpaceId,
                    OpenMode.ForWrite,
                    false) as BlockTableRecord;
                if (currentSpace == null)
                    throw new InvalidOperationException(
                        "The current drawing space could not be opened.");
                int created = 0;
                foreach (MajorFlowEdge edge in edges)
                {
                    var line = new Line(edge.Start, edge.End);
                    line.SetDatabaseDefaults(database);
                    line.LayerId = layerId;
                    line.Color = Color.FromColorIndex(ColorMethod.ByAci, 5);
                    Tag(line, "MajorFlowLink", input, 0);
                    Append(currentSpace, transaction, line);
                    created++;
                }
                foreach (CulvertCandidate candidate in candidates)
                {
                    var marker = new Circle(
                        candidate.Position,
                        Vector3d.ZAxis,
                        Math.Max(sample.CellSize * 0.35, Tolerance));
                    marker.SetDatabaseDefaults(database);
                    marker.LayerId = layerId;
                    marker.Color = Color.FromColorIndex(ColorMethod.ByAci, 1);
                    Tag(marker, "CulvertCandidate", input, candidate.CandidateNumber);
                    Append(currentSpace, transaction, marker);
                    created++;

                    var label = new MText();
                    label.SetDatabaseDefaults(database);
                    label.Location = candidate.Position + new Vector3d(
                        sample.CellSize * 0.45,
                        sample.CellSize * 0.45,
                        0.0);
                    label.TextHeight = Math.Max(
                        database.Textsize,
                        sample.CellSize * 0.16);
                    label.Contents = string.Format(
                        CultureInfo.CurrentCulture,
                        "C{0} CULVERT CANDIDATE\\PAREA {1:N3} ha\\PFLOW {2:N1} deg\\PVERIFY HYDRAULICS",
                        candidate.CandidateNumber,
                        candidate.ContributingAreaHectares,
                        candidate.FlowBearingDegrees);
                    label.Attachment = AttachmentPoint.BottomLeft;
                    label.LayerId = layerId;
                    label.Color = Color.FromColorIndex(ColorMethod.ByAci, 1);
                    Tag(label, "CulvertCandidateLabel", input, candidate.CandidateNumber);
                    Append(currentSpace, transaction, label);
                    created++;
                }
                transaction.Commit();
                return created;
            }
        }

        private static void Tag(
            Entity entity,
            string role,
            HydrologyCivilInput input,
            int itemNumber)
        {
            entity.XData = new ResultBuffer(
                new TypedValue(
                    (int)DxfCode.ExtendedDataRegAppName,
                    RegAppName),
                new TypedValue(
                    (int)DxfCode.ExtendedDataAsciiString,
                    "Role=" + role),
                new TypedValue(
                    (int)DxfCode.ExtendedDataAsciiString,
                    "Item=" + itemNumber.ToString(CultureInfo.InvariantCulture)),
                new TypedValue(
                    (int)DxfCode.ExtendedDataAsciiString,
                    "Surface=" + input.SurfaceId.Handle),
                new TypedValue(
                    (int)DxfCode.ExtendedDataAsciiString,
                    "Boundary=" + input.BoundaryId.Handle));
        }

        private static void EnsureRegApp(
            Database database,
            Transaction transaction)
        {
            RegAppTable table = transaction.GetObject(
                database.RegAppTableId,
                OpenMode.ForRead,
                false) as RegAppTable;
            if (table == null || table.Has(RegAppName)) return;
            table.UpgradeOpen();
            var record = new RegAppTableRecord { Name = RegAppName };
            table.Add(record);
            transaction.AddNewlyCreatedDBObject(record, true);
        }

        private static ObjectId GetOrCreateLayer(
            Database database,
            Transaction transaction,
            string name)
        {
            LayerTable table = transaction.GetObject(
                database.LayerTableId,
                OpenMode.ForRead,
                false) as LayerTable;
            if (table == null)
                throw new InvalidOperationException(
                    "The layer table could not be opened.");
            if (table.Has(name)) return table[name];
            table.UpgradeOpen();
            var layer = new LayerTableRecord { Name = name };
            layer.Color = Color.FromColorIndex(ColorMethod.ByAci, 5);
            ObjectId id = table.Add(layer);
            transaction.AddNewlyCreatedDBObject(layer, true);
            return id;
        }

        private static void Append(
            BlockTableRecord currentSpace,
            Transaction transaction,
            Entity entity)
        {
            currentSpace.AppendEntity(entity);
            transaction.AddNewlyCreatedDBObject(entity, true);
        }

        private static bool PromptPositiveDouble(
            Editor editor,
            string label,
            double defaultValue,
            out double value)
        {
            var options = new PromptDoubleOptions(
                "\n" + label + " <" +
                defaultValue.ToString("0.###", CultureInfo.InvariantCulture) +
                ">: ")
            {
                AllowNone = true,
                AllowNegative = false,
                AllowZero = false,
                DefaultValue = defaultValue,
                UseDefaultValue = true
            };
            PromptDoubleResult result = editor.GetDouble(options);
            if (result.Status == PromptStatus.Cancel)
            {
                value = defaultValue;
                return false;
            }
            value = result.Status == PromptStatus.OK
                ? result.Value
                : defaultValue;
            return result.Status == PromptStatus.OK ||
                   result.Status == PromptStatus.None;
        }

        private static KeyValuePair<string, string> Pair(
            string key,
            string value)
        {
            return new KeyValuePair<string, string>(key, value);
        }

        private static Document ActiveDocument()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
        }
    }

    internal sealed class MajorFlowEdge
    {
        public MajorFlowEdge(
            int startIndex,
            int endIndex,
            Point3d start,
            Point3d end,
            double planLength,
            double accumulationDrawingArea)
        {
            StartIndex = startIndex;
            EndIndex = endIndex;
            Start = start;
            End = end;
            PlanLength = planLength;
            AccumulationDrawingArea = accumulationDrawingArea;
        }

        public int StartIndex { get; private set; }
        public int EndIndex { get; private set; }
        public Point3d Start { get; private set; }
        public Point3d End { get; private set; }
        public double PlanLength { get; private set; }
        public double AccumulationDrawingArea { get; private set; }
    }

    internal sealed class PlanSegment
    {
        public PlanSegment(
            ObjectId sourceId,
            string layer,
            Point3d start,
            Point3d end)
        {
            SourceId = sourceId;
            Layer = layer;
            Start = start;
            End = end;
        }

        public ObjectId SourceId { get; private set; }
        public string Layer { get; private set; }
        public Point3d Start { get; private set; }
        public Point3d End { get; private set; }
    }

    internal sealed class CulvertCandidate
    {
        public CulvertCandidate(
            int candidateNumber,
            Point3d position,
            double contributingAreaHectares,
            double flowBearingDegrees,
            string crossingLayer,
            string crossingHandle,
            int flowCellIndex)
        {
            CandidateNumber = candidateNumber;
            Position = position;
            ContributingAreaHectares = contributingAreaHectares;
            FlowBearingDegrees = flowBearingDegrees;
            CrossingLayer = crossingLayer;
            CrossingHandle = crossingHandle;
            FlowCellIndex = flowCellIndex;
        }

        public int CandidateNumber { get; private set; }
        public Point3d Position { get; private set; }
        public double ContributingAreaHectares { get; private set; }
        public double FlowBearingDegrees { get; private set; }
        public string CrossingLayer { get; private set; }
        public string CrossingHandle { get; private set; }
        public int FlowCellIndex { get; private set; }

        public CulvertCandidate WithCandidateNumber(int candidateNumber)
        {
            return new CulvertCandidate(
                candidateNumber,
                Position,
                ContributingAreaHectares,
                FlowBearingDegrees,
                CrossingLayer,
                CrossingHandle,
                FlowCellIndex);
        }
    }
}

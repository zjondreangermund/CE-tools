using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.DatabaseServices;
using CETools.Core;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.SurfaceHydrologyCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Preliminary regular-grid surface hydrology workflows. A selected TIN surface
    /// is sampled inside a closed boundary, then the host-independent priority-flood
    /// and D8 engine derives routes, contributing area and an outlet catchment.
    /// Generated plan graphics are separate, tagged and removable.
    /// </summary>
    public sealed class SurfaceHydrologyCommands
    {
        private const string RegAppName = "CE_HYDROLOGY_REVIEW";
        private const string ReviewLayer = "CE-HYDROLOGY-REVIEW";
        private const int MaximumCells = 250000;
        private const double Tolerance = 1e-9;

        [CommandMethod("CE_TOOLS", "CE_HYDROLOGYTOOLS", CommandFlags.Modal)]
        public void HydrologyTools()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            DisciplineWorkflowDialogs.SelectAndRun(
                document,
                "CE Tools - Surface Hydrology",
                "Run preliminary surface-flow, catchment and pre/post hydrograph workflows.",
                new List<DisciplineWorkflowAction>
                {
                    new DisciplineWorkflowAction("Surface flow route", "CE_SURFACEFLOW", "Trace a route over a sampled Civil 3D surface.", "01 Terrain"),
                    new DisciplineWorkflowAction("Delineate catchment", "CE_CATCHMENTDELINEATE", "Derive a preliminary outlet catchment from the sampled surface.", "01 Terrain"),
                    new DisciplineWorkflowAction("Compare hydrographs", "CE_HYDROGRAPHCOMPARE", "Review pre- and post-development hydrograph inputs.", "02 Hydrology"),
                    new DisciplineWorkflowAction("Clear hydrology graphics", "CE_HYDROLOGYCLEAR", "Remove CE Tools hydrology review graphics.", "03 Cleanup")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_SURFACEFLOW", CommandFlags.Modal | CommandFlags.Redraw)]
        public void SurfaceFlow()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            HydrologyCivilInput input;
            if (!PromptAnalysisInput(document, out input)) return;

            var modeOptions = new PromptKeywordOptions(
                "\nFlow-route start [Pick/MaximumAccumulation] <MaximumAccumulation>: ")
            {
                AllowNone = true
            };
            modeOptions.Keywords.Add("Pick");
            modeOptions.Keywords.Add("MaximumAccumulation");
            PromptResult modeResult = document.Editor.GetKeywords(modeOptions);
            if (modeResult.Status == PromptStatus.Cancel) return;

            try
            {
                HydrologySample sample = SampleAndAnalyse(document.Database, input);
                int start;
                if (modeResult.Status == PromptStatus.OK && Equal(modeResult.StringResult, "Pick"))
                {
                    PromptPointResult pointResult = document.Editor.GetPoint(
                        "\nPick a point near the desired flow-route start: ");
                    if (pointResult.Status != PromptStatus.OK) return;
                    Point3d point = pointResult.Value.TransformBy(
                        document.Editor.CurrentUserCoordinateSystem);
                    start = FindNearestActiveCell(sample, point);
                }
                else
                {
                    start = sample.Analysis.FindMaximumAccumulationCell();
                }
                if (start < 0)
                {
                    document.Editor.WriteMessage(
                        "\nCE_SURFACEFLOW stopped. No active grid cell could be selected.");
                    return;
                }

                IReadOnlyList<GridCell> route = sample.Analysis.TraceRoute(start);
                FlowRouteSummary summary = SummariseRoute(sample, route, input.UnitsPerMetre);
                var review = new List<KeyValuePair<string, string>>
                {
                    Pair("Surface", input.SurfaceName),
                    Pair("Grid rows x columns", sample.Rows + " x " + sample.Columns),
                    Pair("Active sampled cells", sample.ActiveCount.ToString(CultureInfo.InvariantCulture)),
                    Pair("Grid spacing", input.Spacing.ToString("N3", CultureInfo.CurrentCulture)),
                    Pair("Filled depression cells", sample.FilledCellCount.ToString(CultureInfo.InvariantCulture)),
                    Pair("Maximum fill depth", sample.MaximumFillDepth.ToString("N3", CultureInfo.CurrentCulture)),
                    Pair("Route cells", route.Count.ToString(CultureInfo.InvariantCulture)),
                    Pair("Route length", summary.LengthMetres.ToString("N2", CultureInfo.CurrentCulture) + " m"),
                    Pair("Contributing area at route start", summary.StartAreaHectares.ToString("N3", CultureInfo.CurrentCulture) + " ha"),
                    Pair("Route outlet", FormatPoint(summary.OutletPoint)),
                    Pair("Model status", "Regular-grid D8 screening — not a calibrated 1D/2D flood model")
                };
                if (!PopupTablePresenter.ShowReview(
                        "CE Tools - Surface Flow Route",
                        "The selected Civil 3D surface and boundary remain unchanged. Only removable CE review graphics will be created.",
                        review,
                        "Create Flow Review"))
                    return;

                int generated = CreateFlowGraphics(
                    document.Database,
                    input,
                    sample,
                    route,
                    summary);
                document.Editor.Regen();
                document.Editor.WriteMessage(
                    "\nCE_SURFACEFLOW complete. Active cells={0}; route cells={1}; generated graphics={2}.",
                    sample.ActiveCount,
                    route.Count,
                    generated);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_SURFACEFLOW failed. No source surface or boundary was modified. {0}",
                    exception.Message);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_CATCHMENTDELINEATE", CommandFlags.Modal | CommandFlags.Redraw)]
        public void DelineateCatchment()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            HydrologyCivilInput input;
            if (!PromptAnalysisInput(document, out input)) return;
            PromptPointResult outletResult = document.Editor.GetPoint(
                "\nPick the catchment outlet point inside the analysis boundary: ");
            if (outletResult.Status != PromptStatus.OK) return;
            Point3d outletPick = outletResult.Value.TransformBy(
                document.Editor.CurrentUserCoordinateSystem);

            try
            {
                HydrologySample sample = SampleAndAnalyse(document.Database, input);
                int outlet = FindNearestActiveCell(sample, outletPick);
                if (outlet < 0)
                    throw new InvalidOperationException(
                        "No active sampled surface cell lies near the selected outlet.");
                IReadOnlyList<GridCell> catchment = sample.Analysis.DelineateCatchment(outlet);
                if (catchment.Count == 0)
                    throw new InvalidOperationException(
                        "The selected outlet produced an empty grid catchment.");

                int upstream = FindFarthestCatchmentCell(sample, catchment, outlet);
                IReadOnlyList<GridCell> route = sample.Analysis.TraceRoute(upstream);
                double areaSquareMetres = catchment.Count * sample.CellArea /
                    (input.UnitsPerMetre * input.UnitsPerMetre);
                double areaHectares = areaSquareMetres / 10000.0;
                double maximumAccumulation = sample.Analysis.AccumulationArea[outlet] /
                    (input.UnitsPerMetre * input.UnitsPerMetre) / 10000.0;
                int perimeterEdges = CountCatchmentBoundaryEdges(catchment);

                var review = new List<KeyValuePair<string, string>>
                {
                    Pair("Surface", input.SurfaceName),
                    Pair("Outlet cell", CellName(sample.Analysis.CellOf(outlet))),
                    Pair("Outlet point", FormatPoint(CellPoint(sample, outlet, false))),
                    Pair("Catchment cells", catchment.Count.ToString(CultureInfo.InvariantCulture)),
                    Pair("Catchment area", areaHectares.ToString("N3", CultureInfo.CurrentCulture) + " ha"),
                    Pair("Accumulated area at outlet", maximumAccumulation.ToString("N3", CultureInfo.CurrentCulture) + " ha"),
                    Pair("Perimeter grid edges", perimeterEdges.ToString(CultureInfo.InvariantCulture)),
                    Pair("Longest review route cells", route.Count.ToString(CultureInfo.InvariantCulture)),
                    Pair("Grid spacing", input.Spacing.ToString("N3", CultureInfo.CurrentCulture)),
                    Pair("Model status", "Grid catchment screening — verify against surveyed terrain and approved hydrology")
                };
                if (!PopupTablePresenter.ShowReview(
                        "CE Tools - Outlet Catchment Delineation",
                        "The catchment follows priority-filled D8 grid routing. It is a preliminary delineation and does not replace calibrated hydrological or flood modelling.",
                        review,
                        "Create Catchment Review"))
                    return;

                int generated = CreateCatchmentGraphics(
                    document.Database,
                    input,
                    sample,
                    catchment,
                    outlet,
                    route,
                    areaHectares);
                document.Editor.Regen();
                document.Editor.WriteMessage(
                    "\nCE_CATCHMENTDELINEATE complete. Cells={0}; area={1:N3} ha; generated graphics={2}.",
                    catchment.Count,
                    areaHectares,
                    generated);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_CATCHMENTDELINEATE failed. No source surface or boundary was modified. {0}",
                    exception.Message);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_HYDROGRAPHCOMPARE", CommandFlags.Modal | CommandFlags.Redraw)]
        public void CompareHydrographs()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            Editor editor = document.Editor;

            double area;
            double intensity;
            double preCoefficient;
            double postCoefficient;
            double preTc;
            double postTc;
            double duration;
            double timeStep;
            if (!PromptPositiveDouble(editor, "Catchment area (ha)", 10.0, out area) ||
                !PromptPositiveDouble(editor, "Rainfall intensity (mm/h)", 50.0, out intensity) ||
                !PromptRatio(editor, "Pre-development runoff coefficient", 0.35, out preCoefficient) ||
                !PromptRatio(editor, "Post-development runoff coefficient", 0.75, out postCoefficient) ||
                !PromptPositiveDouble(editor, "Pre-development time of concentration (minutes)", 30.0, out preTc) ||
                !PromptPositiveDouble(editor, "Post-development time of concentration (minutes)", 20.0, out postTc) ||
                !PromptPositiveDouble(editor, "Storm duration (minutes)", 30.0, out duration) ||
                !PromptPositiveDouble(editor, "Hydrograph time step (minutes)", 2.0, out timeStep))
                return;

            try
            {
                HydrographSeries pre = ModifiedRationalHydrograph.Create(
                    area,
                    preCoefficient,
                    intensity,
                    preTc,
                    duration,
                    timeStep);
                HydrographSeries post = ModifiedRationalHydrograph.Create(
                    area,
                    postCoefficient,
                    intensity,
                    postTc,
                    duration,
                    timeStep);
                List<IList<string>> rows = BuildHydrographRows(pre, post, timeStep);
                GridReportPresenter.ShowReportAndOfferTable(
                    document,
                    "CE Tools - Pre/Post Development Hydrograph Review",
                    string.Format(
                        CultureInfo.CurrentCulture,
                        "Modified-rational screening. Area={0:N3} ha; intensity={1:N2} mm/h; pre peak={2:N3} m³/s; post peak={3:N3} m³/s. Not a calibrated hydrograph model.",
                        area,
                        intensity,
                        pre.PeakFlowCubicMetresPerSecond,
                        post.PeakFlowCubicMetresPerSecond),
                    rows,
                    "CE TOOLS PRE POST HYDROGRAPH REVIEW");

                if (PromptYesNo(editor, "Export the hydrograph comparison to Excel", false))
                {
                    string path;
                    if (PromptExcelPath(editor, "CE-Tools-Pre-Post-Hydrograph.xlsx", out path))
                    {
                        SimpleXlsxWriter.Write(path, "Hydrograph", rows);
                        editor.WriteMessage(
                            "\nCE_HYDROGRAPHCOMPARE workbook created: {0}",
                            path);
                    }
                }
                editor.WriteMessage(
                    "\nCE_HYDROGRAPHCOMPARE complete. Pre peak={0:N3} m3/s; post peak={1:N3} m3/s; increase={2:N3} m3/s.",
                    pre.PeakFlowCubicMetresPerSecond,
                    post.PeakFlowCubicMetresPerSecond,
                    post.PeakFlowCubicMetresPerSecond - pre.PeakFlowCubicMetresPerSecond);
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_HYDROGRAPHCOMPARE failed. {0}",
                    exception.Message);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_HYDROLOGYCLEAR", CommandFlags.Modal | CommandFlags.Redraw)]
        public void ClearHydrologyReview()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            int count = CountReviewGraphics(document.Database);
            if (count == 0)
            {
                document.Editor.WriteMessage(
                    "\nCE_HYDROLOGYCLEAR: no CE surface-hydrology review graphics were found in the current space.");
                return;
            }
            var review = new List<KeyValuePair<string, string>>
            {
                Pair("Review objects to erase", count.ToString(CultureInfo.InvariantCulture)),
                Pair("Source surfaces modified", "No"),
                Pair("Source boundaries modified", "No")
            };
            if (!PopupTablePresenter.ShowReview(
                    "CE Tools - Clear Surface Hydrology Review",
                    "Only CE-generated route, catchment-perimeter, marker and label graphics will be erased.",
                    review,
                    "Clear Review"))
                return;

            int erased = EraseReviewGraphics(document.Database);
            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_HYDROLOGYCLEAR complete. Review objects erased={0}.",
                erased);
        }

        internal static bool PromptAnalysisInput(
            Document document,
            out HydrologyCivilInput input)
        {
            input = null;
            Editor editor = document.Editor;
            var surfaceOptions = new PromptEntityOptions(
                "\nSelect a Civil 3D TIN surface: ");
            surfaceOptions.SetRejectMessage(
                "\nSelect a Civil 3D TIN surface.");
            surfaceOptions.AddAllowedClass(typeof(TinSurface), true);
            PromptEntityResult surfaceResult = editor.GetEntity(surfaceOptions);
            if (surfaceResult.Status != PromptStatus.OK) return false;

            var boundaryOptions = new PromptEntityOptions(
                "\nSelect a closed analysis-boundary polyline: ");
            boundaryOptions.SetRejectMessage(
                "\nSelect a closed lightweight polyline.");
            boundaryOptions.AddAllowedClass(typeof(Polyline), false);
            PromptEntityResult boundaryResult = editor.GetEntity(boundaryOptions);
            if (boundaryResult.Status != PromptStatus.OK) return false;

            double spacing;
            double unitsPerMetre;
            if (!PromptPositiveDouble(editor, "Grid spacing in drawing units", 10.0, out spacing) ||
                !PromptPositiveDouble(editor, "Drawing units per metre", 1.0, out unitsPerMetre))
                return false;

            using (Transaction transaction =
                document.Database.TransactionManager.StartTransaction())
            {
                TinSurface surface = transaction.GetObject(
                    surfaceResult.ObjectId,
                    OpenMode.ForRead,
                    false) as TinSurface;
                Polyline boundary = transaction.GetObject(
                    boundaryResult.ObjectId,
                    OpenMode.ForRead,
                    false) as Polyline;
                if (surface == null || boundary == null || !boundary.Closed)
                {
                    editor.WriteMessage(
                        "\nThe selected surface or boundary is invalid; the boundary must be closed.");
                    return false;
                }
                Extents3d extents = boundary.GeometricExtents;
                int columns = Math.Max(2, (int)Math.Ceiling(
                    (extents.MaxPoint.X - extents.MinPoint.X) / spacing));
                int rows = Math.Max(2, (int)Math.Ceiling(
                    (extents.MaxPoint.Y - extents.MinPoint.Y) / spacing));
                long cells = (long)rows * columns;
                if (cells > MaximumCells)
                {
                    editor.WriteMessage(
                        "\nThe requested grid contains {0:N0} cells, above the limit of {1:N0}. Increase grid spacing.",
                        cells,
                        MaximumCells);
                    return false;
                }
                input = new HydrologyCivilInput(
                    surfaceResult.ObjectId,
                    boundaryResult.ObjectId,
                    string.IsNullOrWhiteSpace(surface.Name) ? "<Unnamed surface>" : surface.Name,
                    spacing,
                    unitsPerMetre,
                    rows,
                    columns,
                    extents.MinPoint.X,
                    extents.MinPoint.Y,
                    ReadBoundaryPolygon(boundary, spacing));
            }
            return input.Polygon.Count >= 3;
        }

        internal static HydrologySample SampleAndAnalyse(
            Database database,
            HydrologyCivilInput input)
        {
            int count = checked(input.Rows * input.Columns);
            var elevations = new double[count];
            var active = new bool[count];
            int activeCount = 0;
            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                TinSurface surface = transaction.GetObject(
                    input.SurfaceId,
                    OpenMode.ForRead,
                    false) as TinSurface;
                Polyline boundary = transaction.GetObject(
                    input.BoundaryId,
                    OpenMode.ForRead,
                    false) as Polyline;
                if (surface == null || boundary == null || !boundary.Closed)
                    throw new InvalidOperationException(
                        "The linked surface or closed analysis boundary is no longer available.");

                for (int row = 0; row < input.Rows; row++)
                {
                    for (int column = 0; column < input.Columns; column++)
                    {
                        int index = row * input.Columns + column;
                        double x = input.OriginX + (column + 0.5) * input.Spacing;
                        double y = input.OriginY + (row + 0.5) * input.Spacing;
                        if (!PointInPolygon(input.Polygon, x, y)) continue;
                        try
                        {
                            elevations[index] = surface.FindElevationAtXY(x, y);
                            active[index] = true;
                            activeCount++;
                        }
                        catch
                        {
                            active[index] = false;
                        }
                    }
                }
            }
            if (activeCount == 0)
                throw new InvalidOperationException(
                    "No valid TIN surface elevations were sampled inside the selected boundary.");

            var grid = new HydrologyGrid(
                input.Rows,
                input.Columns,
                input.Spacing,
                elevations,
                active);
            HydrologyGridAnalysis analysis = grid.Analyse();
            int filled = 0;
            double maximumFill = 0.0;
            for (int index = 0; index < analysis.Active.Count; index++)
            {
                if (!analysis.Active[index]) continue;
                double depth = analysis.FillDepth(index);
                if (depth > Tolerance) filled++;
                maximumFill = Math.Max(maximumFill, depth);
            }
            return new HydrologySample(
                input.Rows,
                input.Columns,
                input.OriginX,
                input.OriginY,
                input.Spacing,
                activeCount,
                filled,
                maximumFill,
                analysis);
        }

        private static List<Point2d> ReadBoundaryPolygon(
            Polyline boundary,
            double spacing)
        {
            var points = new List<Point2d>();
            double length = boundary.Length;
            double sampling = Math.Max(spacing * 0.35, length / 5000.0);
            int divisions = Math.Max(
                boundary.NumberOfVertices,
                Math.Min(5000, (int)Math.Ceiling(length / Math.Max(sampling, Tolerance))));
            for (int index = 0; index < divisions; index++)
            {
                double distance = length * index / divisions;
                Point3d point = boundary.GetPointAtDist(distance);
                AddDistinct(points, new Point2d(point.X, point.Y));
            }
            return points;
        }

        private static void AddDistinct(
            IList<Point2d> points,
            Point2d point)
        {
            if (points.Count == 0 ||
                points[points.Count - 1].GetDistanceTo(point) > Tolerance)
                points.Add(point);
        }

        private static bool PointInPolygon(
            IList<Point2d> polygon,
            double x,
            double y)
        {
            bool inside = false;
            for (int current = 0, previous = polygon.Count - 1;
                 current < polygon.Count;
                 previous = current++)
            {
                Point2d a = polygon[current];
                Point2d b = polygon[previous];
                bool intersects = (a.Y > y) != (b.Y > y) &&
                    x < (b.X - a.X) * (y - a.Y) /
                        ((b.Y - a.Y) == 0.0 ? Tolerance : (b.Y - a.Y)) + a.X;
                if (intersects) inside = !inside;
            }
            return inside;
        }

        private static int FindNearestActiveCell(
            HydrologySample sample,
            Point3d point)
        {
            int best = -1;
            double bestDistance = double.PositiveInfinity;
            for (int index = 0; index < sample.Analysis.Active.Count; index++)
            {
                if (!sample.Analysis.Active[index]) continue;
                Point3d centre = CellPoint(sample, index, false);
                double dx = centre.X - point.X;
                double dy = centre.Y - point.Y;
                double distance = dx * dx + dy * dy;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = index;
                }
            }
            return best;
        }

        private static int FindFarthestCatchmentCell(
            HydrologySample sample,
            IReadOnlyList<GridCell> catchment,
            int outlet)
        {
            Point3d outletPoint = CellPoint(sample, outlet, false);
            int best = outlet;
            double bestDistance = -1.0;
            foreach (GridCell cell in catchment)
            {
                Point3d point = CellPoint(sample, cell.Index, false);
                double dx = point.X - outletPoint.X;
                double dy = point.Y - outletPoint.Y;
                double distance = dx * dx + dy * dy;
                if (distance > bestDistance)
                {
                    bestDistance = distance;
                    best = cell.Index;
                }
            }
            return best;
        }

        private static FlowRouteSummary SummariseRoute(
            HydrologySample sample,
            IReadOnlyList<GridCell> route,
            double unitsPerMetre)
        {
            double length = 0.0;
            for (int index = 1; index < route.Count; index++)
            {
                Point3d first = CellPoint(sample, route[index - 1].Index, false);
                Point3d second = CellPoint(sample, route[index].Index, false);
                double dx = second.X - first.X;
                double dy = second.Y - first.Y;
                length += Math.Sqrt(dx * dx + dy * dy);
            }
            int start = route[0].Index;
            int outlet = route[route.Count - 1].Index;
            double areaHectares = sample.Analysis.AccumulationArea[start] /
                (unitsPerMetre * unitsPerMetre) / 10000.0;
            return new FlowRouteSummary(
                length / unitsPerMetre,
                areaHectares,
                CellPoint(sample, outlet, false));
        }

        private static int CreateFlowGraphics(
            Database database,
            HydrologyCivilInput input,
            HydrologySample sample,
            IReadOnlyList<GridCell> route,
            FlowRouteSummary summary)
        {
            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                EnsureRegApp(database, transaction);
                ObjectId layerId = GetOrCreateLayer(
                    database,
                    transaction,
                    ReviewLayer,
                    5);
                BlockTableRecord currentSpace = transaction.GetObject(
                    database.CurrentSpaceId,
                    OpenMode.ForWrite,
                    false) as BlockTableRecord;
                if (currentSpace == null)
                    throw new InvalidOperationException(
                        "The current drawing space could not be opened.");
                int created = 0;
                var points = new Point3dCollection();
                foreach (GridCell cell in route)
                    points.Add(CellPoint(sample, cell.Index, true));
                if (points.Count >= 2)
                {
                    var path = new Polyline3d(Poly3dType.SimplePoly, points, false);
                    path.SetDatabaseDefaults(database);
                    path.LayerId = layerId;
                    path.Color = Color.FromColorIndex(ColorMethod.ByAci, 5);
                    Tag(path, "FlowRoute", input);
                    Append(currentSpace, transaction, path);
                    created++;
                }
                Point3d outlet = summary.OutletPoint;
                var marker = new Circle(
                    outlet,
                    Vector3d.ZAxis,
                    Math.Max(input.Spacing * 0.35, Tolerance));
                marker.SetDatabaseDefaults(database);
                marker.LayerId = layerId;
                marker.Color = Color.FromColorIndex(ColorMethod.ByAci, 1);
                Tag(marker, "Outlet", input);
                Append(currentSpace, transaction, marker);
                created++;

                var label = CreateLabel(
                    database,
                    outlet + new Vector3d(input.Spacing * 0.5, input.Spacing * 0.5, 0.0),
                    Math.Max(input.Spacing * 0.18, database.Textsize),
                    string.Format(
                        CultureInfo.CurrentCulture,
                        "CE FLOW ROUTE\\PAREA AT START {0:N3} ha\\PLENGTH {1:N2} m",
                        summary.StartAreaHectares,
                        summary.LengthMetres));
                label.LayerId = layerId;
                Tag(label, "FlowLabel", input);
                Append(currentSpace, transaction, label);
                created++;
                transaction.Commit();
                return created;
            }
        }

        private static int CreateCatchmentGraphics(
            Database database,
            HydrologyCivilInput input,
            HydrologySample sample,
            IReadOnlyList<GridCell> catchment,
            int outlet,
            IReadOnlyList<GridCell> route,
            double areaHectares)
        {
            var cells = new HashSet<int>(catchment.Select(cell => cell.Index));
            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                EnsureRegApp(database, transaction);
                ObjectId layerId = GetOrCreateLayer(
                    database,
                    transaction,
                    ReviewLayer,
                    3);
                BlockTableRecord currentSpace = transaction.GetObject(
                    database.CurrentSpaceId,
                    OpenMode.ForWrite,
                    false) as BlockTableRecord;
                if (currentSpace == null)
                    throw new InvalidOperationException(
                        "The current drawing space could not be opened.");
                int created = 0;
                foreach (GridCell cell in catchment)
                {
                    foreach (CatchmentEdge edge in ExposedEdges(sample, cell, cells))
                    {
                        var line = new Line(edge.Start, edge.End);
                        line.SetDatabaseDefaults(database);
                        line.LayerId = layerId;
                        line.Color = Color.FromColorIndex(ColorMethod.ByAci, 3);
                        Tag(line, "CatchmentPerimeter", input);
                        Append(currentSpace, transaction, line);
                        created++;
                    }
                }

                var routePoints = new Point3dCollection();
                foreach (GridCell cell in route)
                    routePoints.Add(CellPoint(sample, cell.Index, true));
                if (routePoints.Count >= 2)
                {
                    var path = new Polyline3d(
                        Poly3dType.SimplePoly,
                        routePoints,
                        false);
                    path.SetDatabaseDefaults(database);
                    path.LayerId = layerId;
                    path.Color = Color.FromColorIndex(ColorMethod.ByAci, 5);
                    Tag(path, "CatchmentRoute", input);
                    Append(currentSpace, transaction, path);
                    created++;
                }

                Point3d outletPoint = CellPoint(sample, outlet, false);
                var marker = new Circle(
                    outletPoint,
                    Vector3d.ZAxis,
                    Math.Max(input.Spacing * 0.4, Tolerance));
                marker.SetDatabaseDefaults(database);
                marker.LayerId = layerId;
                marker.Color = Color.FromColorIndex(ColorMethod.ByAci, 1);
                Tag(marker, "CatchmentOutlet", input);
                Append(currentSpace, transaction, marker);
                created++;

                var label = CreateLabel(
                    database,
                    outletPoint + new Vector3d(input.Spacing * 0.6, input.Spacing * 0.6, 0.0),
                    Math.Max(input.Spacing * 0.18, database.Textsize),
                    string.Format(
                        CultureInfo.CurrentCulture,
                        "CE GRID CATCHMENT\\PAREA {0:N3} ha\\PCELLS {1}",
                        areaHectares,
                        catchment.Count));
                label.LayerId = layerId;
                Tag(label, "CatchmentLabel", input);
                Append(currentSpace, transaction, label);
                created++;
                transaction.Commit();
                return created;
            }
        }

        private static IEnumerable<CatchmentEdge> ExposedEdges(
            HydrologySample sample,
            GridCell cell,
            ISet<int> cells)
        {
            double left = sample.OriginX + cell.Column * sample.CellSize;
            double right = left + sample.CellSize;
            double bottom = sample.OriginY + cell.Row * sample.CellSize;
            double top = bottom + sample.CellSize;
            if (!ContainsCell(sample, cells, cell.Row - 1, cell.Column))
                yield return new CatchmentEdge(
                    new Point3d(left, bottom, 0.0),
                    new Point3d(right, bottom, 0.0));
            if (!ContainsCell(sample, cells, cell.Row, cell.Column + 1))
                yield return new CatchmentEdge(
                    new Point3d(right, bottom, 0.0),
                    new Point3d(right, top, 0.0));
            if (!ContainsCell(sample, cells, cell.Row + 1, cell.Column))
                yield return new CatchmentEdge(
                    new Point3d(right, top, 0.0),
                    new Point3d(left, top, 0.0));
            if (!ContainsCell(sample, cells, cell.Row, cell.Column - 1))
                yield return new CatchmentEdge(
                    new Point3d(left, top, 0.0),
                    new Point3d(left, bottom, 0.0));
        }

        private static bool ContainsCell(
            HydrologySample sample,
            ISet<int> cells,
            int row,
            int column)
        {
            if (row < 0 || row >= sample.Rows ||
                column < 0 || column >= sample.Columns)
                return false;
            return cells.Contains(row * sample.Columns + column);
        }

        private static int CountCatchmentBoundaryEdges(
            IReadOnlyList<GridCell> catchment)
        {
            var cells = new HashSet<string>(catchment.Select(
                cell => cell.Row + ":" + cell.Column));
            int count = 0;
            foreach (GridCell cell in catchment)
            {
                if (!cells.Contains((cell.Row - 1) + ":" + cell.Column)) count++;
                if (!cells.Contains((cell.Row + 1) + ":" + cell.Column)) count++;
                if (!cells.Contains(cell.Row + ":" + (cell.Column - 1))) count++;
                if (!cells.Contains(cell.Row + ":" + (cell.Column + 1))) count++;
            }
            return count;
        }

        internal static Point3d CellPoint(
            HydrologySample sample,
            int index,
            bool useFilledElevation)
        {
            GridCell cell = sample.Analysis.CellOf(index);
            double x = sample.OriginX + (cell.Column + 0.5) * sample.CellSize;
            double y = sample.OriginY + (cell.Row + 0.5) * sample.CellSize;
            double z = useFilledElevation
                ? sample.Analysis.FilledElevations[index]
                : sample.Analysis.OriginalElevations[index];
            return new Point3d(x, y, z);
        }

        private static List<IList<string>> BuildHydrographRows(
            HydrographSeries pre,
            HydrographSeries post,
            double timeStep)
        {
            double end = Math.Max(
                pre.Points[pre.Points.Count - 1].TimeMinutes,
                post.Points[post.Points.Count - 1].TimeMinutes);
            int steps = (int)Math.Ceiling(end / timeStep);
            var rows = new List<IList<string>>
            {
                new List<string>
                {
                    "TIME (MIN)", "PRE Q (m3/s)", "POST Q (m3/s)",
                    "INCREASE (m3/s)", "POST / PRE"
                }
            };
            for (int step = 0; step <= steps; step++)
            {
                double time = Math.Min(step * timeStep, end);
                double preFlow = InterpolateHydrograph(pre.Points, time);
                double postFlow = InterpolateHydrograph(post.Points, time);
                rows.Add(new List<string>
                {
                    time.ToString("0.###", CultureInfo.InvariantCulture),
                    preFlow.ToString("0.######", CultureInfo.InvariantCulture),
                    postFlow.ToString("0.######", CultureInfo.InvariantCulture),
                    (postFlow - preFlow).ToString("0.######", CultureInfo.InvariantCulture),
                    preFlow > Tolerance
                        ? (postFlow / preFlow).ToString("0.###", CultureInfo.InvariantCulture)
                        : string.Empty
                });
                if (time >= end) break;
            }
            return rows;
        }

        private static double InterpolateHydrograph(
            IReadOnlyList<HydrographPoint> points,
            double time)
        {
            if (time <= points[0].TimeMinutes)
                return points[0].FlowCubicMetresPerSecond;
            for (int index = 1; index < points.Count; index++)
            {
                HydrographPoint current = points[index];
                if (time > current.TimeMinutes) continue;
                HydrographPoint previous = points[index - 1];
                double range = current.TimeMinutes - previous.TimeMinutes;
                if (range <= Tolerance) return current.FlowCubicMetresPerSecond;
                double fraction = (time - previous.TimeMinutes) / range;
                return previous.FlowCubicMetresPerSecond +
                    (current.FlowCubicMetresPerSecond - previous.FlowCubicMetresPerSecond) *
                    fraction;
            }
            return points[points.Count - 1].FlowCubicMetresPerSecond;
        }

        private static int CountReviewGraphics(Database database)
        {
            int count = 0;
            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                BlockTableRecord currentSpace = transaction.GetObject(
                    database.CurrentSpaceId,
                    OpenMode.ForRead,
                    false) as BlockTableRecord;
                if (currentSpace == null) return 0;
                foreach (ObjectId id in currentSpace)
                {
                    Entity entity = transaction.GetObject(
                        id,
                        OpenMode.ForRead,
                        false) as Entity;
                    if (entity != null &&
                        entity.GetXDataForApplication(RegAppName) != null)
                        count++;
                }
            }
            return count;
        }

        private static int EraseReviewGraphics(Database database)
        {
            int count = 0;
            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                BlockTableRecord currentSpace = transaction.GetObject(
                    database.CurrentSpaceId,
                    OpenMode.ForWrite,
                    false) as BlockTableRecord;
                if (currentSpace == null) return 0;
                foreach (ObjectId id in currentSpace.Cast<ObjectId>().ToList())
                {
                    Entity entity = transaction.GetObject(
                        id,
                        OpenMode.ForRead,
                        false) as Entity;
                    if (entity == null ||
                        entity.GetXDataForApplication(RegAppName) == null)
                        continue;
                    entity.UpgradeOpen();
                    entity.Erase();
                    count++;
                }
                transaction.Commit();
            }
            return count;
        }

        private static void Tag(
            Entity entity,
            string role,
            HydrologyCivilInput input)
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
                    "Surface=" + input.SurfaceId.Handle),
                new TypedValue(
                    (int)DxfCode.ExtendedDataAsciiString,
                    "Boundary=" + input.BoundaryId.Handle),
                new TypedValue(
                    (int)DxfCode.ExtendedDataAsciiString,
                    "Spacing=" + input.Spacing.ToString("R", CultureInfo.InvariantCulture)));
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
            string name,
            short colour)
        {
            LayerTable table = transaction.GetObject(
                database.LayerTableId,
                OpenMode.ForRead,
                false) as LayerTable;
            if (table == null)
                throw new InvalidOperationException(
                    "The layer table could not be opened.");
            LayerTableRecord layer;
            if (table.Has(name))
            {
                layer = transaction.GetObject(
                    table[name],
                    OpenMode.ForWrite,
                    false) as LayerTableRecord;
            }
            else
            {
                table.UpgradeOpen();
                layer = new LayerTableRecord { Name = name };
                table.Add(layer);
                transaction.AddNewlyCreatedDBObject(layer, true);
            }
            if (layer == null) return table[name];
            layer.Color = Color.FromColorIndex(ColorMethod.ByAci, colour);
            layer.IsOff = false;
            layer.IsFrozen = false;
            layer.IsLocked = false;
            layer.IsPlottable = true;
            return layer.ObjectId;
        }

        private static MText CreateLabel(
            Database database,
            Point3d location,
            double height,
            string contents)
        {
            var label = new MText();
            label.SetDatabaseDefaults(database);
            label.Location = location;
            label.TextHeight = Math.Max(height, Tolerance);
            label.Contents = contents;
            label.Attachment = AttachmentPoint.BottomLeft;
            return label;
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

        private static bool PromptRatio(
            Editor editor,
            string label,
            double defaultValue,
            out double value)
        {
            if (!PromptPositiveDouble(editor, label, defaultValue, out value))
                return false;
            if (value <= 1.0) return true;
            editor.WriteMessage(
                "\n{0} must be greater than zero and no more than 1.0.",
                label);
            return false;
        }

        private static bool PromptYesNo(
            Editor editor,
            string question,
            bool defaultYes)
        {
            var options = new PromptKeywordOptions(
                "\n" + question + " [Yes/No] <" +
                (defaultYes ? "Yes" : "No") + ">: ")
            {
                AllowNone = true
            };
            options.Keywords.Add("Yes");
            options.Keywords.Add("No");
            PromptResult result = editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel) return false;
            return result.Status == PromptStatus.None
                ? defaultYes
                : Equal(result.StringResult, "Yes");
        }

        private static bool PromptExcelPath(
            Editor editor,
            string defaultName,
            out string path)
        {
            path = string.Empty;
            var options = new PromptSaveFileOptions(
                "\nChoose the hydrograph Excel workbook path: ")
            {
                DialogCaption = "Export CE Tools Hydrograph Review",
                Filter = "Excel Workbook (*.xlsx)|*.xlsx",
                InitialFileName = defaultName
            };
            PromptFileNameResult result = editor.GetFileNameForSave(options);
            if (result.Status != PromptStatus.OK) return false;
            path = result.StringResult;
            if (!path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
                path += ".xlsx";
            return true;
        }

        private static string CellName(GridCell cell)
        {
            return "R" + cell.Row.ToString(CultureInfo.InvariantCulture) +
                   " C" + cell.Column.ToString(CultureInfo.InvariantCulture);
        }

        private static string FormatPoint(Point3d point)
        {
            return string.Format(
                CultureInfo.CurrentCulture,
                "X {0:N3}; Y {1:N3}; Z {2:N3}",
                point.X,
                point.Y,
                point.Z);
        }

        private static bool Equal(string first, string second)
        {
            return string.Equals(
                first,
                second,
                StringComparison.OrdinalIgnoreCase);
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

    internal sealed class HydrologyCivilInput
    {
        public HydrologyCivilInput(
            ObjectId surfaceId,
            ObjectId boundaryId,
            string surfaceName,
            double spacing,
            double unitsPerMetre,
            int rows,
            int columns,
            double originX,
            double originY,
            IList<Point2d> polygon)
        {
            SurfaceId = surfaceId;
            BoundaryId = boundaryId;
            SurfaceName = surfaceName;
            Spacing = spacing;
            UnitsPerMetre = unitsPerMetre;
            Rows = rows;
            Columns = columns;
            OriginX = originX;
            OriginY = originY;
            Polygon = polygon == null
                ? new List<Point2d>()
                : new List<Point2d>(polygon);
        }

        public ObjectId SurfaceId { get; private set; }
        public ObjectId BoundaryId { get; private set; }
        public string SurfaceName { get; private set; }
        public double Spacing { get; private set; }
        public double UnitsPerMetre { get; private set; }
        public int Rows { get; private set; }
        public int Columns { get; private set; }
        public double OriginX { get; private set; }
        public double OriginY { get; private set; }
        public IList<Point2d> Polygon { get; private set; }
    }

    internal sealed class HydrologySample
    {
        public HydrologySample(
            int rows,
            int columns,
            double originX,
            double originY,
            double cellSize,
            int activeCount,
            int filledCellCount,
            double maximumFillDepth,
            HydrologyGridAnalysis analysis)
        {
            Rows = rows;
            Columns = columns;
            OriginX = originX;
            OriginY = originY;
            CellSize = cellSize;
            ActiveCount = activeCount;
            FilledCellCount = filledCellCount;
            MaximumFillDepth = maximumFillDepth;
            Analysis = analysis;
        }

        public int Rows { get; private set; }
        public int Columns { get; private set; }
        public double OriginX { get; private set; }
        public double OriginY { get; private set; }
        public double CellSize { get; private set; }
        public double CellArea { get { return CellSize * CellSize; } }
        public int ActiveCount { get; private set; }
        public int FilledCellCount { get; private set; }
        public double MaximumFillDepth { get; private set; }
        public HydrologyGridAnalysis Analysis { get; private set; }
    }

    internal sealed class FlowRouteSummary
    {
        public FlowRouteSummary(
            double lengthMetres,
            double startAreaHectares,
            Point3d outletPoint)
        {
            LengthMetres = lengthMetres;
            StartAreaHectares = startAreaHectares;
            OutletPoint = outletPoint;
        }

        public double LengthMetres { get; private set; }
        public double StartAreaHectares { get; private set; }
        public Point3d OutletPoint { get; private set; }
    }

    internal readonly struct CatchmentEdge
    {
        public CatchmentEdge(Point3d start, Point3d end)
        {
            Start = start;
            End = end;
        }

        public Point3d Start { get; }
        public Point3d End { get; }
    }
}

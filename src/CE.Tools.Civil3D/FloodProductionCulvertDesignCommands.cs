using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using CETools.Core;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivilFeatureLine = Autodesk.Civil.DatabaseServices.FeatureLine;
using CivilFeatureLinePointType = Autodesk.Civil.DatabaseServices.FeatureLinePointType;
using CivilTinSurface = Autodesk.Civil.DatabaseServices.TinSurface;

[assembly: CommandClass(typeof(CETools.Civil3D.FloodProductionCulvertDesignCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// One-command Flood Production workflow for a road/centreline low point,
    /// sampled Civil 3D TIN catchment, longest review watercourse, rational-method
    /// return-period flows and preliminary Manning culvert sizing. The workflow is
    /// intentionally one modal command/one drawing transaction so Undo receives one
    /// useful design action and the model is flushed only once at completion.
    /// </summary>
    public sealed class FloodProductionCulvertDesignCommands
    {
        private const string OutputLayer = "CE-FLOOD-CULVERT-DESIGN";
        private const double Tolerance = 1e-9;

        [CommandMethod("CE_TOOLS", "CE_FLOODCULVERTDESIGN", CommandFlags.Modal | CommandFlags.Redraw)]
        public void FloodCulvertDesign()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            Editor editor = document.Editor;

            FloodDesignSettings settings;
            if (!ReadSettings(out settings)) return;

            PromptEntityResult surfacePick = PromptEntity(
                editor,
                "\nSelect Civil 3D TIN surface for catchment and alignment levels: ",
                typeof(CivilTinSurface));
            if (surfacePick.Status != PromptStatus.OK) return;

            PromptEntityResult boundaryPick = PromptEntity(
                editor,
                "\nSelect closed polyline limiting the hydrology analysis: ",
                typeof(Polyline));
            if (boundaryPick.Status != PromptStatus.OK) return;

            PromptEntityResult crossingPick = editor.GetEntity(
                "\nSelect culvert centreline source (Alignment / Polyline / Feature Line): ");
            if (crossingPick.Status != PromptStatus.OK) return;

            try
            {
                CrossingLowPoint lowPoint = FindLowestPoint(
                    document.Database,
                    surfacePick.ObjectId,
                    crossingPick.ObjectId,
                    settings.SampleStepMetres * settings.UnitsPerMetre,
                    settings.UnitsPerMetre);
                if (lowPoint == null)
                    throw new InvalidOperationException(
                        "The selected crossing source is not a supported Alignment, Polyline or Feature Line.");

                HydrologyCivilInput input = BuildHydrologyInput(
                    document.Database,
                    surfacePick.ObjectId,
                    boundaryPick.ObjectId,
                    settings);
                HydrologySample sample = SurfaceHydrologyCommands.SampleAndAnalyse(
                    document.Database,
                    input);
                int outlet = FindNearestActiveCell(sample, lowPoint.Point);
                if (outlet < 0)
                    throw new InvalidOperationException(
                        "The low point does not fall near an active cell inside the selected hydrology boundary.");

                IReadOnlyList<GridCell> catchment = sample.Analysis.DelineateCatchment(outlet);
                if (catchment == null || catchment.Count == 0)
                    throw new InvalidOperationException("The low point produced an empty catchment.");

                int upstream = FindFarthestCatchmentCell(sample, catchment, outlet);
                IReadOnlyList<GridCell> route = sample.Analysis.TraceRoute(upstream);
                RouteSummary routeSummary = SummariseRoute(sample, route, settings.UnitsPerMetre);
                double areaKm2 = catchment.Count * sample.CellArea /
                    (settings.UnitsPerMetre * settings.UnitsPerMetre) / 1000000.0;
                double tcMinutes = settings.TimeOfConcentrationMode == "Kirpich screening"
                    ? KirpichMinutes(routeSummary.LengthMetres, routeSummary.SlopeDecimal)
                    : settings.TimeOfConcentrationMinutes;

                IDictionary<int, double> intensities = BuildIntensities(settings, tcMinutes);
                IDictionary<int, double> flows = FloodCulvertHydraulics.RationalReturnPeriodFlows(
                    areaKm2,
                    settings.RunoffCoefficient,
                    intensities);
                double designFlow = flows[settings.DesignReturnPeriod];

                CulvertSection section = BuildSection(settings, designFlow);
                CulvertHydraulicResult designReview = FloodCulvertHydraulics.Review(section, designFlow);
                var periodResults = new Dictionary<int, CulvertHydraulicResult>();
                foreach (int period in FloodCulvertHydraulics.StandardReturnPeriods)
                    periodResults[period] = FloodCulvertHydraulics.Review(section, flows[period]);

                double invertRl = lowPoint.Point.Z + settings.InvertOffsetMetres * settings.UnitsPerMetre;
                FloodDesignResult result = new FloodDesignResult(
                    settings,
                    lowPoint,
                    input,
                    sample,
                    catchment,
                    route,
                    routeSummary,
                    areaKm2,
                    tcMinutes,
                    intensities,
                    flows,
                    section,
                    designReview,
                    periodResults,
                    invertRl);

                if (!ShowPreCreateReview(result)) return;
                CreateDrawingOutput(document.Database, result);
                August21DisplayRefresh.Flush(document);
                ShowHydraflowReview(result);
                editor.WriteMessage(
                    "\nCE_FLOODCULVERTDESIGN complete. Area={0:N4} km2; Q{1}={2:N3} m3/s; {3}; adequate={4}.",
                    areaKm2,
                    settings.DesignReturnPeriod,
                    designFlow,
                    section.Description,
                    designReview.Adequate ? "YES" : "NO");
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_FLOODCULVERTDESIGN failed. No source surface, boundary or centreline was changed. {0}",
                    exception.Message);
            }
        }

        private static bool ReadSettings(out FloodDesignSettings value)
        {
            value = null;
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Flood Catchment & Culvert Design",
                "One command creates the low point, longest review watercourse, catchment graphics, rational Q2-Q100 review, preliminary culvert sizing and low-point annotation. Rainfall/IDF inputs remain project engineering inputs.");

            model.AddText("Description", "01 General", "Catchment / culvert description", "Culvert 1", "Used in watercourse, catchment and low-point labels.");
            model.AddPositiveDouble("UnitsPerMetre", "01 General", "Drawing units per metre", 1.0, "1.0 for metre drawings, 1000 for millimetre drawings.");
            model.AddPositiveDouble("GridSpacing", "01 General", "Hydrology grid spacing (drawing units)", 10.0, "Sampling grid used on the selected Civil 3D TIN surface.");
            model.AddPositiveDouble("SampleStep", "01 General", "Alignment low-point sample step (m)", 5.0, "Maximum spacing used when sampling a Civil 3D alignment against the selected surface.");

            model.AddPositiveDouble("C", "02 Rational Hydrology", "Run-off coefficient C", 0.65, "Rational-method coefficient, greater than 0 and no more than 1.");
            model.AddChoice("TcMode", "02 Rational Hydrology", "Time of concentration", "Kirpich screening", "Use a specified Tc or preliminary Kirpich estimate from the longest watercourse.", new[] { "Kirpich screening", "Specified" });
            model.AddPositiveDouble("Tc", "02 Rational Hydrology", "Specified Tc (minutes)", 20.0, "Used when Time of concentration = Specified.");
            model.AddChoice("IntensityMode", "02 Rational Hydrology", "Rainfall intensity mode", "Specified by return period", "Use project intensities directly or a user-supplied generic IDF equation i = A*T^B/(Tc+D)^E.", new[] { "Specified by return period", "IDF coefficients" });
            model.AddPositiveDouble("I2", "02 Rational Hydrology", "Q2 intensity (mm/h)", 25.0, "Project 1:2 rainfall intensity.");
            model.AddPositiveDouble("I5", "02 Rational Hydrology", "Q5 intensity (mm/h)", 35.0, "Project 1:5 rainfall intensity.");
            model.AddPositiveDouble("I10", "02 Rational Hydrology", "Q10 intensity (mm/h)", 45.0, "Project 1:10 rainfall intensity.");
            model.AddPositiveDouble("I20", "02 Rational Hydrology", "Q20 intensity (mm/h)", 55.0, "Project 1:20 rainfall intensity.");
            model.AddPositiveDouble("I25", "02 Rational Hydrology", "Q25 intensity (mm/h)", 60.0, "Project 1:25 rainfall intensity.");
            model.AddPositiveDouble("I50", "02 Rational Hydrology", "Q50 intensity (mm/h)", 75.0, "Project 1:50 rainfall intensity.");
            model.AddPositiveDouble("I100", "02 Rational Hydrology", "Q100 intensity (mm/h)", 90.0, "Project 1:100 rainfall intensity.");
            model.AddPositiveDouble("IdfA", "02 Rational Hydrology", "IDF A", 1000.0, "Coefficient A in i=A*T^B/(Tc+D)^E.");
            model.AddPositiveDouble("IdfB", "02 Rational Hydrology", "IDF B", 0.10, "Return-period exponent B.");
            model.AddPositiveDouble("IdfD", "02 Rational Hydrology", "IDF D", 10.0, "Duration offset D in minutes.");
            model.AddPositiveDouble("IdfE", "02 Rational Hydrology", "IDF E", 0.75, "Duration exponent E.");

            model.AddChoice("DesignPeriod", "03 Culvert", "Culvert design return period", "Q25", "Flow used for automatic recommendation and adequacy.", new[] { "Q2", "Q5", "Q10", "Q20", "Q25", "Q50", "Q100" });
            model.AddChoice("CulvertMode", "03 Culvert", "Culvert sizing", "Auto box", "Automatically select a standard box/pipe or review a manually entered section.", new[] { "Auto box", "Auto pipe", "Manual box", "Manual pipe" });
            model.AddPositiveDouble("Width", "03 Culvert", "Manual width / pipe diameter (mm)", 1200.0, "Manual box width or pipe internal diameter.");
            model.AddPositiveDouble("Height", "03 Culvert", "Manual box height (mm)", 900.0, "Ignored for circular pipes.");
            model.AddPositiveInteger("Barrels", "03 Culvert", "Manual barrels", 2, "Number of parallel barrels in manual mode.");
            model.AddPositiveInteger("MaxBarrels", "03 Culvert", "Maximum barrels for auto sizing", 4, "Automatic search tries one barrel through this value.");
            model.AddPositiveDouble("ManningN", "03 Culvert", "Manning n", 0.012, "Preliminary full/partial-flow screening roughness.");
            model.AddPositiveDouble("CulvertSlope", "03 Culvert", "Culvert slope (%)", 1.0, "Longitudinal slope used in preliminary Manning screening.");
            model.AddPositiveDouble("CapacityFactor", "03 Culvert", "Auto-sizing capacity factor", 1.0, "Auto size must have at least design Q multiplied by this factor.");
            model.AddText("InvertOffset", "03 Culvert", "Invert offset from low point (m)", "0.0", "May be negative. Water levels are reported from this preliminary invert.");

            model.AddChoice("CoordinateOrder", "04 Annotation", "Coordinate order", "X then Y", "Swap displayed X/Y headings only; source geometry remains unchanged.", new[] { "X then Y", "Y then X" });
            model.AddChoice("XSign", "04 Annotation", "Displayed X sign", "Keep X sign", "Reverse display sign without changing geometry.", new[] { "Keep X sign", "Reverse X sign" });
            model.AddChoice("YSign", "04 Annotation", "Displayed Y sign", "Keep Y sign", "Reverse display sign without changing geometry.", new[] { "Keep Y sign", "Reverse Y sign" });
            model.AddPaperHeight("TextHeight", "04 Annotation", "Paper text height (mm)", 2.5, "Applied through the current annotation scale.");
            model.AddText("SnapshotPath", "05 Hydraflow Express", "Saved Hydraflow Express snapshot", string.Empty, "Optional PNG/JPG path. The result popup can also browse for the Hydraflow Express profile snapshot.");

            if (!DisciplineWorkflowDialogs.EditSettings(model)) return false;

            double c = model.Double("C", 0.65);
            if (c > 1.0)
            {
                MessageBox.Show("Run-off coefficient C must be no more than 1.0.", "CE Tools - Flood Design", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            int designPeriod;
            if (!int.TryParse(model.Text("DesignPeriod").TrimStart('Q'), NumberStyles.Integer, CultureInfo.InvariantCulture, out designPeriod))
                designPeriod = 25;
            double invertOffset = 0.0;
            ProductionSettingsDialogModel.TryDouble(model.Text("InvertOffset"), out invertOffset);

            value = new FloodDesignSettings
            {
                Description = model.Text("Description"),
                UnitsPerMetre = model.Double("UnitsPerMetre", 1.0),
                GridSpacing = model.Double("GridSpacing", 10.0),
                SampleStepMetres = model.Double("SampleStep", 5.0),
                RunoffCoefficient = c,
                TimeOfConcentrationMode = model.Text("TcMode"),
                TimeOfConcentrationMinutes = model.Double("Tc", 20.0),
                IntensityMode = model.Text("IntensityMode"),
                IdfA = model.Double("IdfA", 1000.0),
                IdfB = model.Double("IdfB", 0.10),
                IdfD = model.Double("IdfD", 10.0),
                IdfE = model.Double("IdfE", 0.75),
                DesignReturnPeriod = designPeriod,
                CulvertMode = model.Text("CulvertMode"),
                ManualWidthMetres = model.Double("Width", 1200.0) / 1000.0,
                ManualHeightMetres = model.Double("Height", 900.0) / 1000.0,
                ManualBarrels = model.Integer("Barrels", 2),
                MaximumBarrels = model.Integer("MaxBarrels", 4),
                ManningN = model.Double("ManningN", 0.012),
                CulvertSlopeDecimal = model.Double("CulvertSlope", 1.0) / 100.0,
                CapacityFactor = model.Double("CapacityFactor", 1.0),
                InvertOffsetMetres = invertOffset,
                CoordinateOrder = model.Text("CoordinateOrder"),
                XSign = model.Text("XSign"),
                YSign = model.Text("YSign"),
                PaperTextHeight = model.Double("TextHeight", 2.5),
                SnapshotPath = model.Text("SnapshotPath")
            };
            foreach (int period in FloodCulvertHydraulics.StandardReturnPeriods)
                value.SpecifiedIntensities[period] = model.Double("I" + period, DefaultIntensity(period));
            return true;
        }

        private static IDictionary<int, double> BuildIntensities(FloodDesignSettings settings, double tcMinutes)
        {
            var values = new Dictionary<int, double>();
            foreach (int period in FloodCulvertHydraulics.StandardReturnPeriods)
            {
                if (settings.IntensityMode == "IDF coefficients")
                {
                    values[period] = settings.IdfA * Math.Pow(period, settings.IdfB) /
                        Math.Pow(tcMinutes + settings.IdfD, settings.IdfE);
                }
                else values[period] = settings.SpecifiedIntensities[period];
            }
            return values;
        }

        private static CulvertSection BuildSection(FloodDesignSettings settings, double designFlow)
        {
            bool pipe = settings.CulvertMode.IndexOf("pipe", StringComparison.OrdinalIgnoreCase) >= 0;
            bool automatic = settings.CulvertMode.StartsWith("Auto", StringComparison.OrdinalIgnoreCase);
            CulvertShape shape = pipe ? CulvertShape.Pipe : CulvertShape.Box;
            if (!automatic)
            {
                double height = pipe ? settings.ManualWidthMetres : settings.ManualHeightMetres;
                return new CulvertSection(
                    shape,
                    settings.ManualWidthMetres,
                    height,
                    settings.ManualBarrels,
                    settings.ManningN,
                    settings.CulvertSlopeDecimal);
            }

            CulvertHydraulicResult recommendation = FloodCulvertHydraulics.Recommend(
                designFlow,
                shape,
                settings.ManningN,
                settings.CulvertSlopeDecimal,
                settings.MaximumBarrels,
                settings.CapacityFactor);
            if (recommendation == null)
                throw new InvalidOperationException(
                    "No standard " + shape + " section in the current CE screening range can carry the selected design flow. Use Manual sizing or revise the design criteria.");
            return recommendation.Section;
        }

        private static HydrologyCivilInput BuildHydrologyInput(
            Database database,
            ObjectId surfaceId,
            ObjectId boundaryId,
            FloodDesignSettings settings)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                CivilTinSurface surface = transaction.GetObject(surfaceId, OpenMode.ForRead, false) as CivilTinSurface;
                Polyline boundary = transaction.GetObject(boundaryId, OpenMode.ForRead, false) as Polyline;
                if (surface == null) throw new InvalidOperationException("Selected hydrology surface is not a TIN surface.");
                if (boundary == null || !boundary.Closed) throw new InvalidOperationException("Hydrology analysis boundary must be a closed polyline.");
                Extents3d ext = boundary.GeometricExtents;
                int columns = Math.Max(2, (int)Math.Ceiling((ext.MaxPoint.X - ext.MinPoint.X) / settings.GridSpacing));
                int rows = Math.Max(2, (int)Math.Ceiling((ext.MaxPoint.Y - ext.MinPoint.Y) / settings.GridSpacing));
                if ((long)rows * columns > 250000L)
                    throw new InvalidOperationException("Hydrology grid exceeds 250,000 cells. Increase Grid Spacing or reduce the analysis boundary.");
                return new HydrologyCivilInput(
                    surfaceId,
                    boundaryId,
                    surface.Name,
                    settings.GridSpacing,
                    settings.UnitsPerMetre,
                    rows,
                    columns,
                    ext.MinPoint.X,
                    ext.MinPoint.Y,
                    ReadBoundary(boundary, settings.GridSpacing));
            }
        }

        private static IList<Point2d> ReadBoundary(Polyline boundary, double spacing)
        {
            var points = new List<Point2d>();
            double length = Math.Max(boundary.Length, spacing);
            int divisions = Math.Max(boundary.NumberOfVertices, Math.Min(5000, (int)Math.Ceiling(length / Math.Max(spacing, 1e-6))));
            for (int i = 0; i < divisions; i++)
            {
                Point3d point = boundary.GetPointAtDist(length * i / divisions);
                if (points.Count == 0 || points[points.Count - 1].GetDistanceTo(new Point2d(point.X, point.Y)) > 1e-8)
                    points.Add(new Point2d(point.X, point.Y));
            }
            return points;
        }

        private static CrossingLowPoint FindLowestPoint(
            Database database,
            ObjectId surfaceId,
            ObjectId sourceId,
            double sampleStepDrawing,
            double unitsPerMetre)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                CivilTinSurface surface = transaction.GetObject(surfaceId, OpenMode.ForRead, false) as CivilTinSurface;
                DBObject source = transaction.GetObject(sourceId, OpenMode.ForRead, false);
                CivilAlignment alignment = source as CivilAlignment;
                if (alignment != null)
                {
                    double start = alignment.StartingStation;
                    double end = alignment.EndingStation;
                    double length = Math.Max(0.0, end - start);
                    int count = Math.Max(2, Math.Min(10000, (int)Math.Ceiling(length / Math.Max(sampleStepDrawing, 0.1)) + 1));
                    CrossingLowPoint best = null;
                    for (int index = 0; index < count; index++)
                    {
                        double station = start + (end - start) * index / (count - 1.0);
                        double x = 0.0;
                        double y = 0.0;
                        try { alignment.PointLocation(station, 0.0, ref x, ref y); }
                        catch { continue; }
                        double z;
                        try { z = surface.FindElevationAtXY(x, y); }
                        catch { continue; }
                        if (best == null || z < best.Point.Z)
                            best = new CrossingLowPoint(new Point3d(x, y, z), station, "Alignment", alignment.Name);
                    }
                    return best;
                }

                CivilFeatureLine feature = source as CivilFeatureLine;
                if (feature != null)
                {
                    Point3dCollection collection = feature.GetPoints(CivilFeatureLinePointType.AllPoints);
                    var points = collection.Cast<Point3d>().ToList();
                    return LowestFromPoints(points, unitsPerMetre, "Feature Line", feature.Layer);
                }

                Polyline polyline = source as Polyline;
                if (polyline != null)
                {
                    var points = new List<Point3d>();
                    for (int i = 0; i < polyline.NumberOfVertices; i++)
                        points.Add(polyline.GetPoint3dAt(i));
                    CrossingLowPoint low = LowestFromPoints(points, unitsPerMetre, "Polyline", polyline.Layer);
                    if (low != null)
                    {
                        try { low.Chainage = polyline.GetDistAtPoint(polyline.GetClosestPointTo(low.Point, false)) / unitsPerMetre; }
                        catch { }
                    }
                    return low;
                }
            }
            return null;
        }

        private static CrossingLowPoint LowestFromPoints(IList<Point3d> points, double unitsPerMetre, string type, string name)
        {
            if (points == null || points.Count == 0) return null;
            int lowIndex = 0;
            for (int i = 1; i < points.Count; i++) if (points[i].Z < points[lowIndex].Z) lowIndex = i;
            double chainage = 0.0;
            for (int i = 1; i <= lowIndex; i++) chainage += PlanDistance(points[i - 1], points[i]) / unitsPerMetre;
            return new CrossingLowPoint(points[lowIndex], chainage, type, name);
        }

        private static int FindNearestActiveCell(HydrologySample sample, Point3d point)
        {
            int best = -1;
            double bestDistance = double.MaxValue;
            for (int index = 0; index < sample.Analysis.Active.Count; index++)
            {
                if (!sample.Analysis.Active[index]) continue;
                Point3d centre = SurfaceHydrologyCommands.CellPoint(sample, index, false);
                double distance = PlanDistance(centre, point);
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                best = index;
            }
            return best;
        }

        private static int FindFarthestCatchmentCell(HydrologySample sample, IReadOnlyList<GridCell> catchment, int outlet)
        {
            Point3d outletPoint = SurfaceHydrologyCommands.CellPoint(sample, outlet, false);
            int best = outlet;
            double bestDistance = -1.0;
            foreach (GridCell cell in catchment)
            {
                Point3d point = SurfaceHydrologyCommands.CellPoint(sample, cell.Index, false);
                double d = PlanDistance(point, outletPoint);
                if (d <= bestDistance) continue;
                bestDistance = d;
                best = cell.Index;
            }
            return best;
        }

        private static RouteSummary SummariseRoute(HydrologySample sample, IReadOnlyList<GridCell> route, double unitsPerMetre)
        {
            if (route == null || route.Count == 0) return new RouteSummary(0.0, 0.0, Point3d.Origin, Point3d.Origin);
            double length = 0.0;
            Point3d first = SurfaceHydrologyCommands.CellPoint(sample, route[0].Index, false);
            Point3d last = first;
            for (int index = 1; index < route.Count; index++)
            {
                Point3d current = SurfaceHydrologyCommands.CellPoint(sample, route[index].Index, false);
                length += PlanDistance(last, current) / unitsPerMetre;
                last = current;
            }
            double slope = length <= Tolerance ? 0.0 : Math.Max(0.0, (first.Z - last.Z) / length);
            return new RouteSummary(length, slope, first, last);
        }

        private static double KirpichMinutes(double lengthMetres, double slopeDecimal)
        {
            if (lengthMetres <= 0.0 || slopeDecimal <= 1e-8) return 20.0;
            return 0.01947 * Math.Pow(lengthMetres, 0.77) * Math.Pow(slopeDecimal, -0.385);
        }

        private static bool ShowPreCreateReview(FloodDesignResult result)
        {
            var rows = new List<KeyValuePair<string, string>>
            {
                Pair("Description", result.Settings.Description),
                Pair("Low point", "CH " + result.LowPoint.Chainage.ToString("0.00", CultureInfo.CurrentCulture) + " / RL " + result.LowPoint.Point.Z.ToString("0.000", CultureInfo.CurrentCulture)),
                Pair("Catchment area", result.AreaKm2.ToString("0.####", CultureInfo.CurrentCulture) + " km²"),
                Pair("Longest watercourse", (result.RouteSummary.LengthMetres / 1000.0).ToString("0.###", CultureInfo.CurrentCulture) + " km"),
                Pair("Watercourse slope", (result.RouteSummary.SlopeDecimal * 100.0).ToString("0.###", CultureInfo.CurrentCulture) + "%"),
                Pair("Time of concentration", result.TimeOfConcentrationMinutes.ToString("0.##", CultureInfo.CurrentCulture) + " min"),
                Pair("Run-off coefficient C", result.Settings.RunoffCoefficient.ToString("0.###", CultureInfo.CurrentCulture)),
                Pair("Design flow", "Q" + result.Settings.DesignReturnPeriod + " = " + result.Flows[result.Settings.DesignReturnPeriod].ToString("0.###", CultureInfo.CurrentCulture) + " m³/s"),
                Pair("Culvert", result.Section.Description),
                Pair("Preliminary capacity", result.DesignReview.CapacityCubicMetresPerSecond.ToString("0.###", CultureInfo.CurrentCulture) + " m³/s"),
                Pair("Adequacy", result.DesignReview.Adequate ? "ADEQUATE" : "NOT ADEQUATE"),
                Pair("Hydraulic scope", "Manning normal/full-flow screening; verify inlet/outlet control in Hydraflow Express")
            };
            foreach (int period in FloodCulvertHydraulics.StandardReturnPeriods)
                rows.Add(Pair("Q" + period + " / WL", result.Flows[period].ToString("0.###", CultureInfo.CurrentCulture) + " m³/s / " + result.WaterLevel(period).ToString("0.000", CultureInfo.CurrentCulture)));
            return PopupTablePresenter.ShowReview(
                "CE Tools - Flood / Culvert Design Preview",
                "The selected TIN surface, analysis boundary and centreline remain unchanged. One grouped CE output will be created and the drawing will refresh once.",
                rows,
                "Create Flood Design");
        }

        private static void CreateDrawingOutput(Database database, FloodDesignResult result)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                EnsureLayer(database, transaction, OutputLayer);
                BlockTableRecord space = transaction.GetObject(database.CurrentSpaceId, OpenMode.ForWrite, false) as BlockTableRecord;
                if (space == null) throw new InvalidOperationException("Current drawing space is unavailable.");

                var lowCircle = new Circle(result.LowPoint.Point, Vector3d.ZAxis, 2.0 * result.Settings.UnitsPerMetre);
                AddEntity(space, transaction, lowCircle);

                var route = new Polyline();
                for (int i = 0; i < result.Route.Count; i++)
                {
                    Point3d point = SurfaceHydrologyCommands.CellPoint(result.Sample, result.Route[i].Index, false);
                    route.AddVertexAt(route.NumberOfVertices, new Point2d(point.X, point.Y), 0.0, 0.0, 0.0);
                }
                AddEntity(space, transaction, route);
                Point3d routeMid = PointAlongPolyline(route, route.Length * 0.5);
                AddText(space, transaction, routeMid,
                    result.Settings.Description + " WATERCOURSE\\P" +
                    (result.RouteSummary.LengthMetres / 1000.0).ToString("0.###", CultureInfo.CurrentCulture) + " km | S=" +
                    (result.RouteSummary.SlopeDecimal * 100.0).ToString("0.###", CultureInfo.CurrentCulture) + "%",
                    result.Settings);

                ISet<int> catchmentSet = new HashSet<int>(result.Catchment.Select(item => item.Index));
                foreach (GridCell cell in result.Catchment)
                    foreach (PlanEdge edge in ExposedEdges(result.Sample, cell, catchmentSet))
                        AddEntity(space, transaction, new Line(edge.Start, edge.End));

                Point3d areaLabelPoint = AverageCatchmentPoint(result.Sample, result.Catchment);
                int dp = result.Settings.DesignReturnPeriod;
                AddText(space, transaction, areaLabelPoint,
                    result.Settings.Description + " CATCHMENT\\P" +
                    "A=" + result.AreaKm2.ToString("0.####", CultureInfo.CurrentCulture) + " km² | " +
                    "i" + dp + "=" + result.Intensities[dp].ToString("0.##", CultureInfo.CurrentCulture) + " mm/h | " +
                    "C=" + result.Settings.RunoffCoefficient.ToString("0.###", CultureInfo.CurrentCulture) + " | " +
                    "Q" + dp + "=" + result.Flows[dp].ToString("0.###", CultureInfo.CurrentCulture) + " m³/s",
                    result.Settings);

                AddText(space, transaction, result.LowPoint.Point + new Vector3d(3.0 * result.Settings.UnitsPerMetre, 3.0 * result.Settings.UnitsPerMetre, 0.0), BuildLowPointText(result), result.Settings);
                transaction.Commit();
            }
        }

        private static string BuildLowPointText(FloodDesignResult result)
        {
            double x = result.LowPoint.Point.X;
            double y = result.LowPoint.Point.Y;
            if (result.Settings.XSign == "Reverse X sign") x = -x;
            if (result.Settings.YSign == "Reverse Y sign") y = -y;
            string coordinates = result.Settings.CoordinateOrder == "Y then X"
                ? "Y=" + y.ToString("0.000", CultureInfo.CurrentCulture) + " X=" + x.ToString("0.000", CultureInfo.CurrentCulture)
                : "X=" + x.ToString("0.000", CultureInfo.CurrentCulture) + " Y=" + y.ToString("0.000", CultureInfo.CurrentCulture);
            int dp = result.Settings.DesignReturnPeriod;
            return "CULVERT @ CH " + result.LowPoint.Chainage.ToString("0.00", CultureInfo.CurrentCulture) + "\\P" +
                coordinates + " | RL=" + result.LowPoint.Point.Z.ToString("0.000", CultureInfo.CurrentCulture) + "\\P" +
                result.Section.Description + " | S=" + (result.Settings.CulvertSlopeDecimal * 100.0).ToString("0.###", CultureInfo.CurrentCulture) + "%\\P" +
                "Q" + dp + "=" + result.Flows[dp].ToString("0.###", CultureInfo.CurrentCulture) + " m³/s | " +
                (result.DesignReview.Adequate ? "ADEQUATE" : "NOT ADEQUATE");
        }

        private static IEnumerable<PlanEdge> ExposedEdges(HydrologySample sample, GridCell cell, ISet<int> cells)
        {
            double left = sample.OriginX + cell.Column * sample.CellSize;
            double right = left + sample.CellSize;
            double bottom = sample.OriginY + cell.Row * sample.CellSize;
            double top = bottom + sample.CellSize;
            if (!ContainsCell(sample, cells, cell.Row - 1, cell.Column)) yield return new PlanEdge(new Point3d(left, bottom, 0), new Point3d(right, bottom, 0));
            if (!ContainsCell(sample, cells, cell.Row + 1, cell.Column)) yield return new PlanEdge(new Point3d(right, top, 0), new Point3d(left, top, 0));
            if (!ContainsCell(sample, cells, cell.Row, cell.Column - 1)) yield return new PlanEdge(new Point3d(left, top, 0), new Point3d(left, bottom, 0));
            if (!ContainsCell(sample, cells, cell.Row, cell.Column + 1)) yield return new PlanEdge(new Point3d(right, bottom, 0), new Point3d(right, top, 0));
        }

        private static bool ContainsCell(HydrologySample sample, ISet<int> cells, int row, int column)
        {
            if (row < 0 || row >= sample.Rows || column < 0 || column >= sample.Columns) return false;
            return cells.Contains(sample.Analysis.IndexOf(row, column));
        }

        private static Point3d AverageCatchmentPoint(HydrologySample sample, IReadOnlyList<GridCell> cells)
        {
            double x = 0.0, y = 0.0, z = 0.0;
            foreach (GridCell cell in cells)
            {
                Point3d p = SurfaceHydrologyCommands.CellPoint(sample, cell.Index, false);
                x += p.X; y += p.Y; z += p.Z;
            }
            double count = Math.Max(1, cells.Count);
            return new Point3d(x / count, y / count, z / count);
        }

        private static void AddText(BlockTableRecord space, Transaction transaction, Point3d point, string text, FloodDesignSettings settings)
        {
            var mtext = new MText
            {
                Location = point,
                Contents = text,
                TextHeight = Math.Max(0.1, settings.PaperTextHeight * settings.UnitsPerMetre),
                Attachment = AttachmentPoint.MiddleCenter,
                Layer = OutputLayer
            };
            AddEntity(space, transaction, mtext);
        }

        private static void AddEntity(BlockTableRecord space, Transaction transaction, Entity entity)
        {
            entity.Layer = OutputLayer;
            space.AppendEntity(entity);
            transaction.AddNewlyCreatedDBObject(entity, true);
        }

        private static void EnsureLayer(Database database, Transaction transaction, string name)
        {
            LayerTable table = transaction.GetObject(database.LayerTableId, OpenMode.ForRead, false) as LayerTable;
            if (table == null || table.Has(name)) return;
            table.UpgradeOpen();
            var layer = new LayerTableRecord { Name = name };
            table.Add(layer);
            transaction.AddNewlyCreatedDBObject(layer, true);
        }

        private static Point3d PointAlongPolyline(Polyline polyline, double distance)
        {
            try { return polyline.GetPointAtDist(Math.Max(0.0, Math.Min(distance, polyline.Length))); }
            catch { return polyline.GeometricExtents.MinPoint; }
        }

        private static void ShowHydraflowReview(FloodDesignResult result)
        {
            var window = new HydraflowFloodReviewWindow(result);
            AcApplication.ShowModalWindow(window);
        }

        private static PromptEntityResult PromptEntity(Editor editor, string message, Type allowed)
        {
            var options = new PromptEntityOptions(message);
            options.SetRejectMessage("\nSelect a supported object.");
            options.AddAllowedClass(allowed, true);
            return editor.GetEntity(options);
        }

        private static double DefaultIntensity(int period)
        {
            switch (period)
            {
                case 2: return 25.0;
                case 5: return 35.0;
                case 10: return 45.0;
                case 20: return 55.0;
                case 25: return 60.0;
                case 50: return 75.0;
                default: return 90.0;
            }
        }

        private static double PlanDistance(Point3d a, Point3d b)
        {
            double dx = b.X - a.X;
            double dy = b.Y - a.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static KeyValuePair<string, string> Pair(string key, string value)
        {
            return new KeyValuePair<string, string>(key, value);
        }
    }

    internal sealed class FloodDesignSettings
    {
        public FloodDesignSettings() { SpecifiedIntensities = new Dictionary<int, double>(); }
        public string Description { get; set; }
        public double UnitsPerMetre { get; set; }
        public double GridSpacing { get; set; }
        public double SampleStepMetres { get; set; }
        public double RunoffCoefficient { get; set; }
        public string TimeOfConcentrationMode { get; set; }
        public double TimeOfConcentrationMinutes { get; set; }
        public string IntensityMode { get; set; }
        public IDictionary<int, double> SpecifiedIntensities { get; private set; }
        public double IdfA { get; set; }
        public double IdfB { get; set; }
        public double IdfD { get; set; }
        public double IdfE { get; set; }
        public int DesignReturnPeriod { get; set; }
        public string CulvertMode { get; set; }
        public double ManualWidthMetres { get; set; }
        public double ManualHeightMetres { get; set; }
        public int ManualBarrels { get; set; }
        public int MaximumBarrels { get; set; }
        public double ManningN { get; set; }
        public double CulvertSlopeDecimal { get; set; }
        public double CapacityFactor { get; set; }
        public double InvertOffsetMetres { get; set; }
        public string CoordinateOrder { get; set; }
        public string XSign { get; set; }
        public string YSign { get; set; }
        public double PaperTextHeight { get; set; }
        public string SnapshotPath { get; set; }
    }

    internal sealed class CrossingLowPoint
    {
        public CrossingLowPoint(Point3d point, double chainage, string sourceType, string sourceName)
        {
            Point = point; Chainage = chainage; SourceType = sourceType; SourceName = sourceName;
        }
        public Point3d Point { get; private set; }
        public double Chainage { get; set; }
        public string SourceType { get; private set; }
        public string SourceName { get; private set; }
    }

    internal sealed class RouteSummary
    {
        public RouteSummary(double lengthMetres, double slopeDecimal, Point3d start, Point3d end)
        {
            LengthMetres = lengthMetres; SlopeDecimal = slopeDecimal; Start = start; End = end;
        }
        public double LengthMetres { get; private set; }
        public double SlopeDecimal { get; private set; }
        public Point3d Start { get; private set; }
        public Point3d End { get; private set; }
    }

    internal sealed class PlanEdge
    {
        public PlanEdge(Point3d start, Point3d end) { Start = start; End = end; }
        public Point3d Start { get; private set; }
        public Point3d End { get; private set; }
    }

    internal sealed class FloodDesignResult
    {
        public FloodDesignResult(
            FloodDesignSettings settings,
            CrossingLowPoint lowPoint,
            HydrologyCivilInput input,
            HydrologySample sample,
            IReadOnlyList<GridCell> catchment,
            IReadOnlyList<GridCell> route,
            RouteSummary routeSummary,
            double areaKm2,
            double tc,
            IDictionary<int, double> intensities,
            IDictionary<int, double> flows,
            CulvertSection section,
            CulvertHydraulicResult review,
            IDictionary<int, CulvertHydraulicResult> periodResults,
            double invertRl)
        {
            Settings = settings; LowPoint = lowPoint; Input = input; Sample = sample;
            Catchment = catchment; Route = route; RouteSummary = routeSummary;
            AreaKm2 = areaKm2; TimeOfConcentrationMinutes = tc; Intensities = intensities;
            Flows = flows; Section = section; DesignReview = review; PeriodResults = periodResults;
            InvertRl = invertRl;
        }
        public FloodDesignSettings Settings { get; private set; }
        public CrossingLowPoint LowPoint { get; private set; }
        public HydrologyCivilInput Input { get; private set; }
        public HydrologySample Sample { get; private set; }
        public IReadOnlyList<GridCell> Catchment { get; private set; }
        public IReadOnlyList<GridCell> Route { get; private set; }
        public RouteSummary RouteSummary { get; private set; }
        public double AreaKm2 { get; private set; }
        public double TimeOfConcentrationMinutes { get; private set; }
        public IDictionary<int, double> Intensities { get; private set; }
        public IDictionary<int, double> Flows { get; private set; }
        public CulvertSection Section { get; private set; }
        public CulvertHydraulicResult DesignReview { get; private set; }
        public IDictionary<int, CulvertHydraulicResult> PeriodResults { get; private set; }
        public double InvertRl { get; private set; }
        public double WaterLevel(int period)
        {
            return InvertRl + PeriodResults[period].NormalDepthMetres * Settings.UnitsPerMetre;
        }
    }

    internal sealed class HydraflowFloodReviewWindow : Window
    {
        private readonly FloodDesignResult _result;
        private readonly Image _image;
        private readonly TextBlock _imageStatus;

        public HydraflowFloodReviewWindow(FloodDesignResult result)
        {
            _result = result;
            Title = "CE Tools - Culvert Hydraulic Check / Hydraflow Express";
            Width = 1050;
            Height = 760;
            MinWidth = 780;
            MinHeight = 560;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = false;
            Background = Brushes.White;

            var root = new Grid { Margin = new Thickness(16) };
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var left = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
            left.Children.Add(new TextBlock { Text = "CULVERT HYDRAULIC CHECK — Q" + result.Settings.DesignReturnPeriod, FontSize = 22, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 14) });
            left.Children.Add(Line("Design Flow", result.Flows[result.Settings.DesignReturnPeriod].ToString("0.###") + " m³/s"));
            left.Children.Add(Line("Culvert", result.Section.Description));
            left.Children.Add(Line("Slope", (result.Settings.CulvertSlopeDecimal * 100.0).ToString("0.###") + "%"));
            left.Children.Add(Line("Velocity", result.DesignReview.VelocityMetresPerSecond.ToString("0.###") + " m/s"));
            left.Children.Add(Line("Capacity", result.DesignReview.CapacityCubicMetresPerSecond.ToString("0.###") + " m³/s"));
            left.Children.Add(Line("Normal depth", result.DesignReview.NormalDepthMetres.ToString("0.###") + " m"));
            left.Children.Add(Line("Adequacy", result.DesignReview.Adequate ? "ADEQUATE" : "NOT ADEQUATE"));
            left.Children.Add(new TextBlock { Text = "\nRETURN-PERIOD WATER LEVELS", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 10, 0, 4) });
            foreach (int period in FloodCulvertHydraulics.StandardReturnPeriods)
                left.Children.Add(Line("Q" + period, result.Flows[period].ToString("0.###") + " m³/s  |  WL " + result.WaterLevel(period).ToString("0.000")));
            left.Children.Add(new TextBlock { Text = "\nCE water levels are preliminary Manning normal-depth screening. Hydraflow Express inlet/outlet-control results remain the detailed hydraulic check.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray });
            root.Children.Add(left);

            var right = new Grid();
            right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            _imageStatus = new TextBlock { Text = "Hydraflow Express hydraulic profile snapshot", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) };
            right.Children.Add(_imageStatus);
            var border = new Border { BorderBrush = Brushes.Silver, BorderThickness = new Thickness(1), Background = Brushes.WhiteSmoke, Padding = new Thickness(8) };
            _image = new Image { Stretch = Stretch.Uniform };
            border.Child = _image;
            Grid.SetRow(border, 1);
            right.Children.Add(border);
            Grid.SetColumn(right, 1);
            root.Children.Add(right);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            var browse = new Button { Content = "Browse Hydraflow Snapshot...", MinWidth = 190, Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 8, 0) };
            browse.Click += OnBrowse;
            buttons.Children.Add(browse);
            buttons.Children.Add(new Button { Content = "Close", IsDefault = true, IsCancel = true, MinWidth = 100, Padding = new Thickness(12, 7, 12, 7) });
            Grid.SetRow(buttons, 1);
            Grid.SetColumnSpan(buttons, 2);
            root.Children.Add(buttons);
            Content = root;

            LoadImage(result.Settings.SnapshotPath);
        }

        private static TextBlock Line(string label, string value)
        {
            return new TextBlock { Text = label + ": " + value, FontSize = 14, Margin = new Thickness(0, 2, 0, 2), TextWrapping = TextWrapping.Wrap };
        }

        private void OnBrowse(object sender, RoutedEventArgs args)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select Hydraflow Express hydraulic profile snapshot",
                Filter = "Image files (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp|All files (*.*)|*.*"
            };
            if (dialog.ShowDialog(this) == true) LoadImage(dialog.FileName);
        }

        private void LoadImage(string path)
        {
            _image.Source = null;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                _imageStatus.Text = "Hydraflow Express snapshot — browse to a saved PNG/JPG after running the detailed check.";
                return;
            }
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();
                _image.Source = bitmap;
                _imageStatus.Text = "Hydraflow Express snapshot — " + Path.GetFileName(path);
            }
            catch (System.Exception exception)
            {
                _imageStatus.Text = "Snapshot could not be loaded: " + exception.Message;
            }
        }
    }
}

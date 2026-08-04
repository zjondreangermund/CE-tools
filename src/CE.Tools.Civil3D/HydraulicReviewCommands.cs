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
using Autodesk.Civil.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

[assembly: CommandClass(typeof(CETools.Civil3D.HydraulicReviewCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Preliminary hydraulic screening tools. Results are intentionally labelled
    /// as review values and must be checked against project standards, calibrated
    /// hydrology/hydraulics and engineer-approved design methods.
    /// </summary>
    public sealed class HydraulicReviewCommands
    {
        private const string ReviewRegApp = "CE_HYDRAULIC_REVIEW";
        private const string CatchmentLayer = "CE-REVIEW-CATCHMENT";
        private const double GeometryTolerance = 0.000001;

        [CommandMethod("CE_TOOLS", "CE_HYDRAULICTOOLS", CommandFlags.Modal)]
        public void HydraulicTools()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            var options = new PromptKeywordOptions(
                "\nHydraulic review tools [Catchment/Rational/Culvert/Pump/Clear] <Catchment>: ")
            {
                AllowNone = true
            };
            options.Keywords.Add("Catchment");
            options.Keywords.Add("Rational");
            options.Keywords.Add("Culvert");
            options.Keywords.Add("Pump");
            options.Keywords.Add("Clear");
            PromptResult result = document.Editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel) return;
            string choice = result.Status == PromptStatus.OK
                ? result.StringResult
                : "Catchment";
            string command;
            if (string.Equals(choice, "Rational", StringComparison.OrdinalIgnoreCase))
                command = "CE_RATIONALFLOW ";
            else if (string.Equals(choice, "Culvert", StringComparison.OrdinalIgnoreCase))
                command = "CE_CULVERTREVIEW ";
            else if (string.Equals(choice, "Pump", StringComparison.OrdinalIgnoreCase))
                command = "CE_PUMPREVIEW ";
            else if (string.Equals(choice, "Clear", StringComparison.OrdinalIgnoreCase))
                command = "CE_HYDRAULICCLEAR ";
            else
                command = "CE_CATCHMENTQUICK ";
            document.SendStringToExecute(command, true, false, true);
        }

        [CommandMethod("CE_TOOLS", "CE_RATIONALFLOW", CommandFlags.Modal | CommandFlags.Redraw)]
        public void RationalMethodFlow()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            Editor editor = document.Editor;

            double areaHa;
            double coefficient;
            if (!PromptPositiveDouble(editor, "Catchment area (ha)", 1.0, out areaHa))
                return;
            if (!PromptRangeDouble(editor, "Runoff coefficient C", 0.7, 0.0, 1.0, out coefficient))
                return;

            int[] periods = { 2, 5, 10, 20, 25, 50, 100 };
            var scenarios = new List<RationalFlowScenario>();
            double previousIntensity = 50.0;
            foreach (int period in periods)
            {
                double intensity;
                if (!PromptPositiveDouble(
                        editor,
                        "Rainfall intensity for 1:" + period.ToString(CultureInfo.InvariantCulture) + " (mm/h)",
                        previousIntensity,
                        out intensity))
                {
                    return;
                }
                previousIntensity = intensity;
                scenarios.Add(new RationalFlowScenario(
                    period,
                    intensity,
                    coefficient * intensity * areaHa / 360.0));
            }

            var rows = new List<KeyValuePair<string, string>>
            {
                Pair("Method", "Rational Method: Q = C i A / 360"),
                Pair("Catchment area", areaHa.ToString("N3", CultureInfo.CurrentCulture) + " ha"),
                Pair("Runoff coefficient", coefficient.ToString("N3", CultureInfo.CurrentCulture)),
                Pair("Status", "Preliminary screening — verify rainfall data, time of concentration and project standard")
            };
            foreach (RationalFlowScenario scenario in scenarios)
            {
                rows.Add(Pair(
                    "1:" + scenario.ReturnPeriod.ToString(CultureInfo.InvariantCulture),
                    string.Format(
                        CultureInfo.CurrentCulture,
                        "i {0:N3} mm/h; Q {1:N4} m³/s; {2:N2} L/s",
                        scenario.Intensity,
                        scenario.Flow,
                        scenario.Flow * 1000.0)));
            }
            PopupTablePresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - Rational-Method Flow Review",
                "The result is a preliminary peak-flow screen. Confirm the governing hydrological method, rainfall source, concentration time and safety factors before design use.",
                rows,
                "CE TOOLS RATIONAL FLOW REVIEW");

            if (PromptYesNo(editor, "Export the rational-flow scenarios to Excel", false))
            {
                ExportRationalScenarios(editor, areaHa, coefficient, scenarios);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_CULVERTREVIEW", CommandFlags.Modal | CommandFlags.Redraw)]
        public void CulvertCapacityReview()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            Editor editor = document.Editor;

            double designFlow;
            if (!PromptPositiveDouble(editor, "Design flow (m³/s)", 1.0, out designFlow))
                return;

            var typeOptions = new PromptKeywordOptions(
                "\nCulvert type [Circular/Box] <Circular>: ")
            {
                AllowNone = true
            };
            typeOptions.Keywords.Add("Circular");
            typeOptions.Keywords.Add("Box");
            PromptResult typeResult = editor.GetKeywords(typeOptions);
            if (typeResult.Status == PromptStatus.Cancel) return;
            bool circular = typeResult.Status != PromptStatus.OK ||
                string.Equals(typeResult.StringResult, "Circular", StringComparison.OrdinalIgnoreCase);

            double width;
            double height;
            if (circular)
            {
                if (!PromptPositiveDouble(editor, "Internal diameter (m)", 0.9, out width))
                    return;
                height = width;
            }
            else
            {
                if (!PromptPositiveDouble(editor, "Internal width (m)", 1.2, out width))
                    return;
                if (!PromptPositiveDouble(editor, "Internal height (m)", 0.9, out height))
                    return;
            }

            int barrels;
            if (!PromptPositiveInteger(editor, "Number of barrels", 1, out barrels))
                return;
            double roughness;
            double slopePercent;
            if (!PromptPositiveDouble(editor, "Manning roughness n", 0.013, out roughness))
                return;
            if (!PromptPositiveDouble(editor, "Culvert slope (%)", 1.0, out slopePercent))
                return;

            double area;
            double wettedPerimeter;
            if (circular)
            {
                area = Math.PI * width * width / 4.0;
                wettedPerimeter = Math.PI * width;
            }
            else
            {
                area = width * height;
                wettedPerimeter = width + (2.0 * height);
            }
            double hydraulicRadius = area / wettedPerimeter;
            double slope = slopePercent / 100.0;
            double singleCapacity =
                (1.0 / roughness) *
                area *
                Math.Pow(hydraulicRadius, 2.0 / 3.0) *
                Math.Sqrt(slope);
            double totalCapacity = singleCapacity * barrels;
            double velocity = singleCapacity / area;
            int requiredBarrels = singleCapacity > GeometryTolerance
                ? (int)Math.Ceiling(designFlow / singleCapacity)
                : int.MaxValue;

            var rows = new List<KeyValuePair<string, string>>
            {
                Pair("Method", "Manning full-flow screening"),
                Pair("Culvert", circular
                    ? width.ToString("N3", CultureInfo.CurrentCulture) + " m circular"
                    : width.ToString("N3", CultureInfo.CurrentCulture) + " m × " + height.ToString("N3", CultureInfo.CurrentCulture) + " m box"),
                Pair("Barrels", barrels.ToString(CultureInfo.InvariantCulture)),
                Pair("Manning n", roughness.ToString("N4", CultureInfo.CurrentCulture)),
                Pair("Slope", slopePercent.ToString("N3", CultureInfo.CurrentCulture) + "%"),
                Pair("Single-barrel capacity", singleCapacity.ToString("N4", CultureInfo.CurrentCulture) + " m³/s"),
                Pair("Total full-flow capacity", totalCapacity.ToString("N4", CultureInfo.CurrentCulture) + " m³/s"),
                Pair("Full-flow velocity", velocity.ToString("N3", CultureInfo.CurrentCulture) + " m/s"),
                Pair("Design flow", designFlow.ToString("N4", CultureInfo.CurrentCulture) + " m³/s"),
                Pair("Screening result", totalCapacity >= designFlow ? "Capacity meets entered flow" : "Capacity below entered flow"),
                Pair("Calculated barrels at entered size", requiredBarrels == int.MaxValue ? "Unavailable" : requiredBarrels.ToString(CultureInfo.InvariantCulture)),
                Pair("Required verification", "Inlet/outlet control, headwater, tailwater, blockage, debris, freeboard, erosion and authority criteria")
            };
            PopupTablePresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - Culvert Capacity Review",
                "This is a full-flow Manning screen, not a final culvert design. Use an approved hydraulic method or model for inlet/outlet control and flood-level assessment.",
                rows,
                "CE TOOLS CULVERT CAPACITY REVIEW");
        }

        [CommandMethod("CE_TOOLS", "CE_PUMPREVIEW", CommandFlags.Modal | CommandFlags.Redraw)]
        public void PumpDutyReview()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            Editor editor = document.Editor;

            double flowLitresPerSecond;
            double staticHead;
            double pipeLength;
            double diameterMillimetres;
            double hazenWilliams;
            double efficiencyPercent;
            double ratedFlow;
            double ratedHead;
            if (!PromptPositiveDouble(editor, "Required flow (L/s)", 10.0, out flowLitresPerSecond)) return;
            if (!PromptNonNegativeDouble(editor, "Static head (m)", 10.0, out staticHead)) return;
            if (!PromptPositiveDouble(editor, "Rising-main length (m)", 500.0, out pipeLength)) return;
            if (!PromptPositiveDouble(editor, "Internal pipe diameter (mm)", 150.0, out diameterMillimetres)) return;
            if (!PromptPositiveDouble(editor, "Hazen-Williams coefficient C", 130.0, out hazenWilliams)) return;
            if (!PromptRangeDouble(editor, "Overall pump/motor efficiency (%)", 70.0, 1.0, 100.0, out efficiencyPercent)) return;
            if (!PromptPositiveDouble(editor, "Candidate pump rated flow (L/s)", flowLitresPerSecond, out ratedFlow)) return;
            if (!PromptPositiveDouble(editor, "Candidate pump rated head (m)", staticHead, out ratedHead)) return;

            double flow = flowLitresPerSecond / 1000.0;
            double diameter = diameterMillimetres / 1000.0;
            double frictionHead =
                10.67 *
                pipeLength *
                Math.Pow(flow, 1.852) /
                (Math.Pow(hazenWilliams, 1.852) * Math.Pow(diameter, 4.87));
            double totalHead = staticHead + frictionHead;
            double hydraulicPower = 1000.0 * 9.81 * flow * totalHead / 1000.0;
            double inputPower = hydraulicPower / (efficiencyPercent / 100.0);
            bool flowPass = ratedFlow >= flowLitresPerSecond;
            bool headPass = ratedHead >= totalHead;

            var rows = new List<KeyValuePair<string, string>>
            {
                Pair("Method", "Hazen-Williams preliminary duty-point screening"),
                Pair("Required flow", flowLitresPerSecond.ToString("N3", CultureInfo.CurrentCulture) + " L/s"),
                Pair("Static head", staticHead.ToString("N3", CultureInfo.CurrentCulture) + " m"),
                Pair("Calculated friction head", frictionHead.ToString("N3", CultureInfo.CurrentCulture) + " m"),
                Pair("Required total dynamic head", totalHead.ToString("N3", CultureInfo.CurrentCulture) + " m"),
                Pair("Hydraulic power", hydraulicPower.ToString("N3", CultureInfo.CurrentCulture) + " kW"),
                Pair("Estimated input power", inputPower.ToString("N3", CultureInfo.CurrentCulture) + " kW"),
                Pair("Candidate rating", ratedFlow.ToString("N3", CultureInfo.CurrentCulture) + " L/s at " + ratedHead.ToString("N3", CultureInfo.CurrentCulture) + " m"),
                Pair("Flow screening", flowPass ? "Pass" : "Fail"),
                Pair("Head screening", headPass ? "Pass" : "Fail"),
                Pair("Overall screening", flowPass && headPass ? "Candidate reaches entered duty point" : "Candidate does not reach entered duty point"),
                Pair("Required verification", "Manufacturer pump curve, system curve, fittings/minor losses, NPSH, duty/standby philosophy, surge and operating range")
            };
            PopupTablePresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - Pump Duty-Point Review",
                "This screen compares an entered pump rating with a simplified duty point. Final selection requires the manufacturer curve and a complete hydraulic system assessment.",
                rows,
                "CE TOOLS PUMP DUTY REVIEW");
        }

        [CommandMethod("CE_TOOLS", "CE_CATCHMENTQUICK", CommandFlags.Modal | CommandFlags.Redraw)]
        public void QuickCatchmentReview()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            Editor editor = document.Editor;

            var boundaryOptions = new PromptEntityOptions(
                "\nSelect a closed catchment boundary polyline: ");
            boundaryOptions.SetRejectMessage("\nSelect a closed lightweight polyline.");
            boundaryOptions.AddAllowedClass(typeof(Polyline), false);
            PromptEntityResult boundaryResult = editor.GetEntity(boundaryOptions);
            if (boundaryResult.Status != PromptStatus.OK) return;

            double unitsPerMetre;
            if (!PromptPositiveDouble(editor, "Drawing units per metre", 1.0, out unitsPerMetre))
                return;

            var surfaceOptions = new PromptEntityOptions(
                "\nSelect a Civil 3D surface to sample or press Esc to report boundary geometry only: ");
            surfaceOptions.SetRejectMessage("\nSelect a Civil 3D surface.");
            surfaceOptions.AddAllowedClass(typeof(CivilSurface), false);
            PromptEntityResult surfaceResult = editor.GetEntity(surfaceOptions);
            ObjectId surfaceId = surfaceResult.Status == PromptStatus.OK
                ? surfaceResult.ObjectId
                : ObjectId.Null;

            double spacing = 0.0;
            if (!surfaceId.IsNull &&
                !PromptPositiveDouble(editor, "Surface sample spacing in drawing units", 10.0 * unitsPerMetre, out spacing))
            {
                return;
            }

            CatchmentReview review;
            try
            {
                review = AnalyseCatchment(
                    document.Database,
                    boundaryResult.ObjectId,
                    surfaceId,
                    unitsPerMetre,
                    spacing);
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage("\nCE_CATCHMENTQUICK stopped. {0}", exception.Message);
                return;
            }

            var rows = new List<KeyValuePair<string, string>>
            {
                Pair("Boundary area", review.AreaSquareMetres.ToString("N3", CultureInfo.CurrentCulture) + " m²"),
                Pair("Boundary area", review.AreaHectares.ToString("N4", CultureInfo.CurrentCulture) + " ha"),
                Pair("Boundary perimeter", review.PerimeterMetres.ToString("N3", CultureInfo.CurrentCulture) + " m"),
                Pair("Sampled surface", review.SurfaceName),
                Pair("Valid samples", review.SampleCount.ToString(CultureInfo.InvariantCulture)),
                Pair("Minimum elevation", FormatNullable(review.MinimumElevation, " m")),
                Pair("Maximum elevation", FormatNullable(review.MaximumElevation, " m")),
                Pair("Relief", review.MinimumElevation.HasValue && review.MaximumElevation.HasValue
                    ? (review.MaximumElevation.Value - review.MinimumElevation.Value).ToString("N3", CultureInfo.CurrentCulture) + " m"
                    : string.Empty),
                Pair("Candidate low point", review.LowPoint.HasValue ? FormatPoint(review.LowPoint.Value) : "<Not sampled>"),
                Pair("Status", "Quick catchment/surface screen — not a hydrological delineation or flood model")
            };
            PopupTablePresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - Quick Catchment Review",
                "The boundary area is exact for the selected polyline. Surface minimum/maximum values are grid samples and can miss smaller depressions when spacing is coarse.",
                rows,
                "CE TOOLS QUICK CATCHMENT REVIEW");

            if (review.LowPoint.HasValue &&
                PromptYesNo(editor, "Create a removable marker at the sampled candidate low point", true))
            {
                CreateCatchmentMarker(
                    document.Database,
                    boundaryResult.ObjectId.Handle.ToString(),
                    review.LowPoint.Value,
                    document.Database.Textsize);
                editor.Regen();
            }
        }

        [CommandMethod("CE_TOOLS", "CE_HYDRAULICCLEAR", CommandFlags.Modal | CommandFlags.Redraw)]
        public void ClearHydraulicReviewGraphics()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            int count = CountReviewGraphics(document.Database);
            if (count == 0)
            {
                document.Editor.WriteMessage("\nCE_HYDRAULICCLEAR: no CE hydraulic review graphics were found.");
                return;
            }
            var rows = new List<KeyValuePair<string, string>>
            {
                Pair("Review objects to remove", count.ToString(CultureInfo.InvariantCulture)),
                Pair("Catchment/design sources retained", "Yes")
            };
            if (!PopupTablePresenter.ShowReview(
                    "CE Tools - Clear Hydraulic Review",
                    "Only CE-generated hydraulic/catchment review graphics will be erased.",
                    rows,
                    "Clear Review"))
                return;
            int erased = EraseReviewGraphics(document.Database);
            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_HYDRAULICCLEAR complete. Review objects removed={0}.",
                erased);
        }

        private static CatchmentReview AnalyseCatchment(
            Database database,
            ObjectId boundaryId,
            ObjectId surfaceId,
            double unitsPerMetre,
            double spacing)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                Polyline boundary = transaction.GetObject(
                    boundaryId,
                    OpenMode.ForRead,
                    false) as Polyline;
                if (boundary == null || !boundary.Closed || boundary.NumberOfVertices < 3)
                    throw new InvalidOperationException("The selected catchment boundary is not a valid closed polyline.");
                double unitScale = unitsPerMetre;
                double areaSquareMetres = Math.Abs(boundary.Area) / (unitScale * unitScale);
                double perimeterMetres = boundary.Length / unitScale;
                CivilSurface surface = surfaceId.IsNull
                    ? null
                    : transaction.GetObject(surfaceId, OpenMode.ForRead, false) as CivilSurface;
                string surfaceName = surface == null ? "<Boundary only>" : surface.Name;

                var polygon = new List<Point2d>();
                for (int index = 0; index < boundary.NumberOfVertices; index++)
                    polygon.Add(boundary.GetPoint2dAt(index));
                double minX = polygon.Min(point => point.X);
                double maxX = polygon.Max(point => point.X);
                double minY = polygon.Min(point => point.Y);
                double maxY = polygon.Max(point => point.Y);
                double? minimum = null;
                double? maximum = null;
                Point3d? lowPoint = null;
                int samples = 0;

                if (surface != null)
                {
                    double safeSpacing = Math.Max(spacing, GeometryTolerance);
                    for (double x = minX; x <= maxX + GeometryTolerance; x += safeSpacing)
                    {
                        for (double y = minY; y <= maxY + GeometryTolerance; y += safeSpacing)
                        {
                            Point2d point = new Point2d(x, y);
                            if (!PointInPolygon(polygon, point)) continue;
                            double elevation;
                            try
                            {
                                elevation = surface.FindElevationAtXY(x, y);
                            }
                            catch
                            {
                                continue;
                            }
                            samples++;
                            if (!minimum.HasValue || elevation < minimum.Value)
                            {
                                minimum = elevation;
                                lowPoint = new Point3d(x, y, elevation);
                            }
                            if (!maximum.HasValue || elevation > maximum.Value)
                                maximum = elevation;
                        }
                    }
                    for (int index = 0; index < polygon.Count; index++)
                    {
                        Point2d point = polygon[index];
                        double elevation;
                        try
                        {
                            elevation = surface.FindElevationAtXY(point.X, point.Y);
                        }
                        catch
                        {
                            continue;
                        }
                        samples++;
                        if (!minimum.HasValue || elevation < minimum.Value)
                        {
                            minimum = elevation;
                            lowPoint = new Point3d(point.X, point.Y, elevation);
                        }
                        if (!maximum.HasValue || elevation > maximum.Value)
                            maximum = elevation;
                    }
                }

                return new CatchmentReview(
                    areaSquareMetres,
                    areaSquareMetres / 10000.0,
                    perimeterMetres,
                    surfaceName,
                    samples,
                    minimum,
                    maximum,
                    lowPoint);
            }
        }

        private static bool PointInPolygon(IList<Point2d> polygon, Point2d point)
        {
            bool inside = false;
            int previous = polygon.Count - 1;
            for (int current = 0; current < polygon.Count; current++)
            {
                Point2d first = polygon[previous];
                Point2d second = polygon[current];
                if (PointOnSegment(first, second, point)) return true;
                bool crosses =
                    ((second.Y > point.Y) != (first.Y > point.Y)) &&
                    (point.X <
                     ((first.X - second.X) * (point.Y - second.Y) /
                      ((first.Y - second.Y) + GeometryTolerance)) + second.X);
                if (crosses) inside = !inside;
                previous = current;
            }
            return inside;
        }

        private static bool PointOnSegment(Point2d first, Point2d second, Point2d point)
        {
            Vector2d segment = second - first;
            Vector2d offset = point - first;
            double cross = (segment.X * offset.Y) - (segment.Y * offset.X);
            if (Math.Abs(cross) > GeometryTolerance) return false;
            double dot = offset.DotProduct(segment);
            return dot >= -GeometryTolerance && dot <= segment.LengthSqrd + GeometryTolerance;
        }

        private static void CreateCatchmentMarker(
            Database database,
            string sourceHandle,
            Point3d point,
            double textHeight)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                EnsureRegApp(database, transaction);
                ObjectId layerId = GetOrCreateLayer(database, transaction, CatchmentLayer);
                BlockTableRecord currentSpace = transaction.GetObject(
                    database.CurrentSpaceId,
                    OpenMode.ForWrite,
                    false) as BlockTableRecord;
                if (currentSpace == null)
                    throw new InvalidOperationException("The current drawing space could not be opened.");
                double height = NormalizeHeight(textHeight);
                double radius = Math.Max(height, 0.001);
                var circle = new Circle(point, Vector3d.ZAxis, radius);
                circle.SetDatabaseDefaults(database);
                circle.LayerId = layerId;
                circle.Color = Color.FromColorIndex(ColorMethod.ByAci, 5);
                WriteReviewTag(circle, "CatchmentLowPoint", sourceHandle);
                currentSpace.AppendEntity(circle);
                transaction.AddNewlyCreatedDBObject(circle, true);
                var label = new MText();
                label.SetDatabaseDefaults(database);
                label.LayerId = layerId;
                label.Color = Color.FromColorIndex(ColorMethod.ByAci, 5);
                label.Location = point + new Vector3d(radius * 1.5, radius * 1.5, 0.0);
                label.TextHeight = height;
                label.Attachment = AttachmentPoint.BottomLeft;
                label.Contents = string.Format(
                    CultureInfo.CurrentCulture,
                    "SAMPLED LOW POINT\\PX {0:N3}\\PY {1:N3}\\PZ {2:N3}",
                    point.X,
                    point.Y,
                    point.Z);
                label.BackgroundFill = true;
                label.UseBackgroundColor = true;
                WriteReviewTag(label, "CatchmentLowPointLabel", sourceHandle);
                currentSpace.AppendEntity(label);
                transaction.AddNewlyCreatedDBObject(label, true);
                transaction.Commit();
            }
        }

        private static void ExportRationalScenarios(
            Editor editor,
            double areaHa,
            double coefficient,
            IList<RationalFlowScenario> scenarios)
        {
            var options = new PromptSaveFileOptions(
                "\nSelect rational-flow Excel workbook path: ")
            {
                Filter = "Excel Workbook (*.xlsx)|*.xlsx",
                DialogCaption = "Export CE Tools Rational Flow Review",
                InitialFileName = "CE-Rational-Flow-Review.xlsx"
            };
            PromptFileNameResult result = editor.GetFileNameForSave(options);
            if (result.Status != PromptStatus.OK) return;
            string path = result.StringResult;
            if (!path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
                path += ".xlsx";
            var cells = new List<IList<string>>
            {
                new List<string>
                {
                    "RETURN PERIOD",
                    "INTENSITY (mm/h)",
                    "AREA (ha)",
                    "RUNOFF COEFFICIENT",
                    "FLOW (m3/s)",
                    "FLOW (L/s)"
                }
            };
            foreach (RationalFlowScenario scenario in scenarios)
            {
                cells.Add(new List<string>
                {
                    "1:" + scenario.ReturnPeriod.ToString(CultureInfo.InvariantCulture),
                    scenario.Intensity.ToString("0.###", CultureInfo.InvariantCulture),
                    areaHa.ToString("0.###", CultureInfo.InvariantCulture),
                    coefficient.ToString("0.###", CultureInfo.InvariantCulture),
                    scenario.Flow.ToString("0.####", CultureInfo.InvariantCulture),
                    (scenario.Flow * 1000.0).ToString("0.##", CultureInfo.InvariantCulture)
                });
            }
            SimpleXlsxWriter.Write(path, "Rational Flow", cells);
            editor.WriteMessage("\nCE_RATIONALFLOW Excel export complete: {0}", path);
        }

        private static int CountReviewGraphics(Database database)
        {
            int count = 0;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTableRecord currentSpace = transaction.GetObject(
                    database.CurrentSpaceId,
                    OpenMode.ForRead,
                    false) as BlockTableRecord;
                if (currentSpace == null) return 0;
                foreach (ObjectId objectId in currentSpace)
                {
                    Entity entity = transaction.GetObject(
                        objectId,
                        OpenMode.ForRead,
                        false) as Entity;
                    if (entity != null && entity.GetXDataForApplication(ReviewRegApp) != null)
                        count++;
                }
            }
            return count;
        }

        private static int EraseReviewGraphics(Database database)
        {
            int count = 0;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTableRecord currentSpace = transaction.GetObject(
                    database.CurrentSpaceId,
                    OpenMode.ForRead,
                    false) as BlockTableRecord;
                if (currentSpace == null) return 0;
                foreach (ObjectId objectId in currentSpace.Cast<ObjectId>().ToList())
                {
                    Entity entity = transaction.GetObject(
                        objectId,
                        OpenMode.ForRead,
                        false) as Entity;
                    if (entity == null || entity.GetXDataForApplication(ReviewRegApp) == null)
                        continue;
                    entity.UpgradeOpen();
                    entity.Erase();
                    count++;
                }
                transaction.Commit();
            }
            return count;
        }

        private static void WriteReviewTag(Entity entity, string kind, string sourceHandle)
        {
            entity.XData = new ResultBuffer(
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, ReviewRegApp),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, "Kind=" + kind),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, "Source=" + sourceHandle));
        }

        private static void EnsureRegApp(Database database, Transaction transaction)
        {
            RegAppTable table = transaction.GetObject(
                database.RegAppTableId,
                OpenMode.ForRead,
                false) as RegAppTable;
            if (table == null || table.Has(ReviewRegApp)) return;
            table.UpgradeOpen();
            var record = new RegAppTableRecord { Name = ReviewRegApp };
            table.Add(record);
            transaction.AddNewlyCreatedDBObject(record, true);
        }

        private static ObjectId GetOrCreateLayer(
            Database database,
            Transaction transaction,
            string name)
        {
            LayerTable layers = transaction.GetObject(
                database.LayerTableId,
                OpenMode.ForRead,
                false) as LayerTable;
            if (layers == null)
                throw new InvalidOperationException("The layer table could not be opened.");
            if (layers.Has(name)) return layers[name];
            layers.UpgradeOpen();
            var layer = new LayerTableRecord { Name = name };
            ObjectId id = layers.Add(layer);
            transaction.AddNewlyCreatedDBObject(layer, true);
            return id;
        }

        private static bool PromptPositiveDouble(
            Editor editor,
            string name,
            double defaultValue,
            out double value)
        {
            var options = new PromptDoubleOptions(
                "\n" + name + " <" + defaultValue.ToString("0.###", CultureInfo.InvariantCulture) + ">: ")
            {
                AllowNone = true,
                AllowNegative = false,
                AllowZero = false,
                DefaultValue = defaultValue,
                UseDefaultValue = true
            };
            PromptDoubleResult result = editor.GetDouble(options);
            value = result.Status == PromptStatus.OK ? result.Value : defaultValue;
            return result.Status == PromptStatus.OK;
        }

        private static bool PromptNonNegativeDouble(
            Editor editor,
            string name,
            double defaultValue,
            out double value)
        {
            var options = new PromptDoubleOptions(
                "\n" + name + " <" + defaultValue.ToString("0.###", CultureInfo.InvariantCulture) + ">: ")
            {
                AllowNone = true,
                AllowNegative = false,
                AllowZero = true,
                DefaultValue = defaultValue,
                UseDefaultValue = true
            };
            PromptDoubleResult result = editor.GetDouble(options);
            value = result.Status == PromptStatus.OK ? result.Value : defaultValue;
            return result.Status == PromptStatus.OK;
        }

        private static bool PromptRangeDouble(
            Editor editor,
            string name,
            double defaultValue,
            double minimum,
            double maximum,
            out double value)
        {
            if (!PromptPositiveDouble(editor, name, defaultValue, out value))
                return false;
            if (value < minimum || value > maximum)
            {
                editor.WriteMessage(
                    "\n{0} must be between {1:N3} and {2:N3}.",
                    name,
                    minimum,
                    maximum);
                return false;
            }
            return true;
        }

        private static bool PromptPositiveInteger(
            Editor editor,
            string name,
            int defaultValue,
            out int value)
        {
            var options = new PromptIntegerOptions(
                "\n" + name + " <" + defaultValue.ToString(CultureInfo.InvariantCulture) + ">: ")
            {
                AllowNone = true,
                AllowNegative = false,
                AllowZero = false,
                DefaultValue = defaultValue,
                UseDefaultValue = true
            };
            PromptIntegerResult result = editor.GetInteger(options);
            value = result.Status == PromptStatus.OK ? result.Value : defaultValue;
            return result.Status == PromptStatus.OK;
        }

        private static bool PromptYesNo(Editor editor, string message, bool defaultValue)
        {
            var options = new PromptKeywordOptions(
                "\n" + message + " [Yes/No] <" + (defaultValue ? "Yes" : "No") + ">: ")
            {
                AllowNone = true
            };
            options.Keywords.Add("Yes");
            options.Keywords.Add("No");
            PromptResult result = editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel) return false;
            return result.Status == PromptStatus.None
                ? defaultValue
                : string.Equals(result.StringResult, "Yes", StringComparison.OrdinalIgnoreCase);
        }

        private static double NormalizeHeight(double value)
        {
            if (Math.Abs(value - 1.8) < 0.05) return 1.8;
            if (Math.Abs(value - 5.0) < 0.05) return 5.0;
            return 2.0;
        }

        private static string FormatNullable(double? value, string suffix)
        {
            return value.HasValue
                ? value.Value.ToString("N3", CultureInfo.CurrentCulture) + suffix
                : string.Empty;
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

        private static KeyValuePair<string, string> Pair(string key, string value)
        {
            return new KeyValuePair<string, string>(key, value);
        }

        private static Document ActiveDocument()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
        }
    }

    internal sealed class RationalFlowScenario
    {
        public RationalFlowScenario(int returnPeriod, double intensity, double flow)
        {
            ReturnPeriod = returnPeriod;
            Intensity = intensity;
            Flow = flow;
        }

        public int ReturnPeriod { get; private set; }
        public double Intensity { get; private set; }
        public double Flow { get; private set; }
    }

    internal sealed class CatchmentReview
    {
        public CatchmentReview(
            double areaSquareMetres,
            double areaHectares,
            double perimeterMetres,
            string surfaceName,
            int sampleCount,
            double? minimumElevation,
            double? maximumElevation,
            Point3d? lowPoint)
        {
            AreaSquareMetres = areaSquareMetres;
            AreaHectares = areaHectares;
            PerimeterMetres = perimeterMetres;
            SurfaceName = surfaceName;
            SampleCount = sampleCount;
            MinimumElevation = minimumElevation;
            MaximumElevation = maximumElevation;
            LowPoint = lowPoint;
        }

        public double AreaSquareMetres { get; private set; }
        public double AreaHectares { get; private set; }
        public double PerimeterMetres { get; private set; }
        public string SurfaceName { get; private set; }
        public int SampleCount { get; private set; }
        public double? MinimumElevation { get; private set; }
        public double? MaximumElevation { get; private set; }
        public Point3d? LowPoint { get; private set; }
    }
}

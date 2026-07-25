using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.AdvancedParkingPlanningCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Boundary-driven parking option planning for the CE Tools master item list.
    /// The command compares 90, 60 and 45 degree alternatives, creates one closed
    /// polyline per accepted bay and stores a link to the source boundary so the
    /// chosen option can be regenerated after grip edits.
    /// </summary>
    public sealed class AdvancedParkingPlanningCommands
    {
        private const string RegAppName = "CE_PARK_OPTIONS";
        private const string SchemaVersion = "1";
        private const double GeometryTolerance = 0.000001;

        [CommandMethod(
            "CE_TOOLS",
            "CE_PARKOPTIONS",
            CommandFlags.Modal | CommandFlags.Redraw)]
        public void CreateParkingOptions()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            ParkingBoundary boundary = PromptBoundary(document);
            if (boundary == null) return;

            Editor editor = document.Editor;
            int target;
            if (!PromptPositiveInteger(editor, "Target parking bays", 120, out target))
                return;

            ParkingOptionSettings settings;
            if (!PromptSettings(editor, target, out settings))
                return;

            List<ParkingOptionResult> options = AnalyseOptions(boundary, settings);
            var window = new ParkingOptionsWindow(options, target);
            AcApplication.ShowModalWindow(window);
            if (!window.Accepted || window.SelectedOption == null)
            {
                editor.WriteMessage("\nCE_PARKOPTIONS cancelled. No parking geometry was created.");
                return;
            }

            ParkingOptionResult selected = window.SelectedOption;
            List<KeyValuePair<string, string>> review = BuildOptionRows(selected, settings);
            if (!PopupTablePresenter.ShowReview(
                    "CE Tools - Parking Layout Option",
                    "The selected option will create one closed polyline per bay and replace any earlier CE parking option linked to this boundary.",
                    review,
                    "Create Option"))
            {
                editor.WriteMessage("\nCE_PARKOPTIONS cancelled. No parking geometry was created.");
                return;
            }

            try
            {
                int created = ReplaceLinkedOption(
                    document,
                    boundary,
                    selected,
                    settings);
                editor.Regen();
                editor.WriteMessage(
                    "\nCE_PARKOPTIONS complete. Angle={0}; capacity={1}; bays created={2}; target={3}.",
                    selected.AngleDegrees,
                    selected.Capacity,
                    created,
                    settings.TargetBayCount);

                List<KeyValuePair<string, string>> reportRows = BuildOptionRows(selected, settings);
                reportRows.Add(Pair("Bays created", created.ToString(CultureInfo.InvariantCulture)));
                reportRows.Add(Pair("Boundary handle", boundary.HandleText));
                PopupTablePresenter.ShowReportAndOfferTable(
                    document,
                    "CE Tools - Parking Option Result",
                    "The bay polylines are linked to the selected boundary. Run CE_PARKOPTIONSREFRESH after changing the boundary with grips.",
                    reportRows,
                    "CE TOOLS PARKING OPTION");
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_PARKOPTIONS cancelled. No parking option transaction was committed. {0}",
                    exception.Message);
            }
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_PARKOPTIONSREFRESH",
            CommandFlags.Modal | CommandFlags.Redraw)]
        public void RefreshParkingOption()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            ParkingBoundary boundary = PromptBoundary(document);
            if (boundary == null) return;

            ParkingOptionSettings settings;
            int existingCount;
            if (!TryReadLinkedSettings(
                    document.Database,
                    boundary.HandleText,
                    out settings,
                    out existingCount))
            {
                document.Editor.WriteMessage(
                    "\nCE_PARKOPTIONSREFRESH stopped. No CE parking option is linked to the selected boundary.");
                return;
            }

            ParkingOptionResult option = BuildOption(boundary, settings, settings.AngleDegrees);
            List<KeyValuePair<string, string>> rows = BuildOptionRows(option, settings);
            rows.Add(Pair("Existing linked bays", existingCount.ToString(CultureInfo.InvariantCulture)));
            if (!PopupTablePresenter.ShowReview(
                    "CE Tools - Refresh Parking Option",
                    "The current linked bays will be replaced from the edited boundary using the stored angle and dimensions.",
                    rows,
                    "Refresh"))
            {
                document.Editor.WriteMessage("\nCE_PARKOPTIONSREFRESH cancelled.");
                return;
            }

            try
            {
                int created = ReplaceLinkedOption(document, boundary, option, settings);
                document.Editor.Regen();
                document.Editor.WriteMessage(
                    "\nCE_PARKOPTIONSREFRESH complete. Previous bays={0}; refreshed bays={1}; capacity={2}.",
                    existingCount,
                    created,
                    option.Capacity);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_PARKOPTIONSREFRESH cancelled. The linked option was not replaced. {0}",
                    exception.Message);
            }
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_PARKOPTIONSINFO",
            CommandFlags.Modal | CommandFlags.Redraw)]
        public void ParkingOptionInformation()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            string boundaryHandle;
            ObjectId boundaryId;
            if (!PromptBoundaryOrBay(document, out boundaryHandle, out boundaryId))
                return;

            ParkingOptionSettings settings;
            int existingCount;
            if (!TryReadLinkedSettings(
                    document.Database,
                    boundaryHandle,
                    out settings,
                    out existingCount))
            {
                document.Editor.WriteMessage(
                    "\nCE_PARKOPTIONSINFO: no linked CE parking option was found.");
                return;
            }

            ParkingBoundary boundary = ReadBoundary(document.Database, boundaryId);
            if (boundary == null)
            {
                document.Editor.WriteMessage(
                    "\nCE_PARKOPTIONSINFO: the linked source boundary is missing or invalid.");
                return;
            }

            ParkingOptionResult option = BuildOption(boundary, settings, settings.AngleDegrees);
            List<KeyValuePair<string, string>> rows = BuildOptionRows(option, settings);
            rows.Add(Pair("Existing linked bays", existingCount.ToString(CultureInfo.InvariantCulture)));
            rows.Add(Pair("Boundary handle", boundaryHandle));
            rows.Add(Pair("Boundary area", Format(boundary.Area)));
            PopupTablePresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - Parking Option Information",
                "Capacity is recalculated from the current boundary geometry; existing linked bays show what is currently drawn.",
                rows,
                "CE TOOLS PARKING OPTION INFORMATION");
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_PARKOPTIONSCLEAR",
            CommandFlags.Modal | CommandFlags.Redraw)]
        public void ClearParkingOption()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            string boundaryHandle;
            ObjectId boundaryId;
            if (!PromptBoundaryOrBay(document, out boundaryHandle, out boundaryId))
                return;

            ParkingOptionSettings settings;
            int existingCount;
            if (!TryReadLinkedSettings(
                    document.Database,
                    boundaryHandle,
                    out settings,
                    out existingCount))
            {
                document.Editor.WriteMessage("\nCE_PARKOPTIONSCLEAR: no linked bays were found.");
                return;
            }

            var rows = new List<KeyValuePair<string, string>>
            {
                Pair("Boundary handle", boundaryHandle),
                Pair("Linked bays to remove", existingCount.ToString(CultureInfo.InvariantCulture)),
                Pair("Boundary retained", "Yes")
            };
            if (!PopupTablePresenter.ShowReview(
                    "CE Tools - Clear Parking Option",
                    "Only CE parking bay polylines linked to this boundary will be erased. The source boundary remains unchanged.",
                    rows,
                    "Clear Bays"))
            {
                document.Editor.WriteMessage("\nCE_PARKOPTIONSCLEAR cancelled.");
                return;
            }

            int removed = EraseLinkedBays(document.Database, boundaryHandle);
            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_PARKOPTIONSCLEAR complete. Linked bays removed={0}.",
                removed);
        }

        private static List<ParkingOptionResult> AnalyseOptions(
            ParkingBoundary boundary,
            ParkingOptionSettings settings)
        {
            return new List<ParkingOptionResult>
            {
                BuildOption(boundary, settings, 90.0),
                BuildOption(boundary, settings, 60.0),
                BuildOption(boundary, settings, 45.0)
            };
        }

        private static ParkingOptionResult BuildOption(
            ParkingBoundary boundary,
            ParkingOptionSettings settings,
            double angleDegrees)
        {
            double radians = DegreesToRadians(angleDegrees);
            double sine = Math.Sin(radians);
            double cosine = Math.Cos(radians);
            double pitch = settings.BayWidth / Math.Max(sine, GeometryTolerance);
            double projectedDepth =
                (settings.BayDepth * sine) +
                (settings.BayWidth * Math.Abs(cosine));
            double moduleDepth = (projectedDepth * 2.0) + settings.AisleWidth;

            List<ParkingBayGeometry> best = new List<ParkingBayGeometry>();
            int bestRows = 0;
            double availableWidth = Math.Max(0.0, boundary.MaxX - boundary.MinX);
            double availableDepth = Math.Max(0.0, boundary.MaxY - boundary.MinY);
            if (pitch <= GeometryTolerance ||
                projectedDepth <= GeometryTolerance ||
                availableWidth <= GeometryTolerance ||
                availableDepth < projectedDepth)
            {
                return new ParkingOptionResult(
                    angleDegrees,
                    pitch,
                    projectedDepth,
                    0,
                    0,
                    best);
            }

            int moduleCount = Math.Max(1, (int)Math.Floor(availableDepth / moduleDepth));
            double usedModuleDepth = moduleCount * moduleDepth;
            double remainingDepth = Math.Max(0.0, availableDepth - usedModuleDepth);
            double[] yOffsets =
            {
                0.0,
                remainingDepth / 2.0,
                remainingDepth
            };
            double remainderWidth = Math.Max(
                0.0,
                availableWidth - (Math.Floor(availableWidth / pitch) * pitch));
            double[] xOffsets =
            {
                0.0,
                remainderWidth / 2.0,
                remainderWidth
            };

            foreach (double yOffset in yOffsets)
            {
                foreach (double xOffset in xOffsets)
                {
                    var candidates = new List<ParkingBayGeometry>();
                    var rowKeys = new HashSet<string>(StringComparer.Ordinal);
                    for (int module = 0; module < moduleCount; module++)
                    {
                        double moduleBottom =
                            boundary.MinY + yOffset + (module * moduleDepth);
                        double lowerBaselineY = moduleBottom + projectedDepth;
                        double upperBaselineY = lowerBaselineY + settings.AisleWidth;

                        AddRowCandidates(
                            boundary,
                            settings,
                            angleDegrees,
                            pitch,
                            lowerBaselineY,
                            -1.0,
                            xOffset,
                            module,
                            "L",
                            candidates,
                            rowKeys);
                        AddRowCandidates(
                            boundary,
                            settings,
                            angleDegrees,
                            pitch,
                            upperBaselineY,
                            1.0,
                            xOffset,
                            module,
                            "U",
                            candidates,
                            rowKeys);
                    }

                    if (candidates.Count > best.Count)
                    {
                        best = candidates;
                        bestRows = rowKeys.Count;
                    }
                }
            }

            best = best
                .OrderBy(item => item.RowIndex)
                .ThenBy(item => item.Sequence)
                .ToList();
            return new ParkingOptionResult(
                angleDegrees,
                pitch,
                projectedDepth,
                best.Count,
                bestRows,
                best);
        }

        private static void AddRowCandidates(
            ParkingBoundary boundary,
            ParkingOptionSettings settings,
            double angleDegrees,
            double pitch,
            double baselineY,
            double sideSign,
            double xOffset,
            int module,
            string rowSuffix,
            ICollection<ParkingBayGeometry> output,
            ISet<string> rowKeys)
        {
            double radians = DegreesToRadians(angleDegrees) * sideSign;
            Vector2d divider = new Vector2d(
                Math.Cos(radians),
                Math.Sin(radians));
            double skewAllowance = settings.BayDepth * Math.Abs(divider.X);
            double startX = boundary.MinX - skewAllowance + xOffset;
            double endX = boundary.MaxX + skewAllowance;
            int sequence = 0;
            string rowKey = module.ToString(CultureInfo.InvariantCulture) + rowSuffix;

            for (double x = startX; x + pitch <= endX + GeometryTolerance; x += pitch)
            {
                Point2d first = new Point2d(x, baselineY);
                Point2d second = new Point2d(x + pitch, baselineY);
                Point2d fourth = new Point2d(
                    first.X + (divider.X * settings.BayDepth),
                    first.Y + (divider.Y * settings.BayDepth));
                Point2d third = new Point2d(
                    second.X + (divider.X * settings.BayDepth),
                    second.Y + (divider.Y * settings.BayDepth));

                var corners = new[] { first, second, third, fourth };
                if (!BayInsideBoundary(boundary.Polygon, corners))
                {
                    sequence++;
                    continue;
                }

                output.Add(new ParkingBayGeometry(
                    boundary.ToWorld(first),
                    boundary.ToWorld(second),
                    boundary.ToWorld(third),
                    boundary.ToWorld(fourth),
                    (module * 2) + (sideSign > 0.0 ? 1 : 0),
                    sequence));
                rowKeys.Add(rowKey);
                sequence++;
            }
        }

        private static bool BayInsideBoundary(
            IList<Point2d> polygon,
            IList<Point2d> corners)
        {
            for (int index = 0; index < corners.Count; index++)
            {
                if (!PointInPolygon(polygon, corners[index]))
                    return false;
            }

            Point2d centre = new Point2d(
                corners.Average(point => point.X),
                corners.Average(point => point.Y));
            return PointInPolygon(polygon, centre);
        }

        private static bool PointInPolygon(
            IList<Point2d> polygon,
            Point2d point)
        {
            if (polygon == null || polygon.Count < 3) return false;
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
                     ((first.X - second.X) *
                      (point.Y - second.Y) /
                      ((first.Y - second.Y) + GeometryTolerance)) + second.X);
                if (crosses) inside = !inside;
                previous = current;
            }
            return inside;
        }

        private static bool PointOnSegment(
            Point2d first,
            Point2d second,
            Point2d point)
        {
            Vector2d segment = second - first;
            Vector2d offset = point - first;
            double cross = (segment.X * offset.Y) - (segment.Y * offset.X);
            if (Math.Abs(cross) > GeometryTolerance) return false;
            double dot = offset.DotProduct(segment);
            if (dot < -GeometryTolerance) return false;
            return dot <= segment.LengthSqrd + GeometryTolerance;
        }

        private static int ReplaceLinkedOption(
            Document document,
            ParkingBoundary boundary,
            ParkingOptionResult option,
            ParkingOptionSettings settings)
        {
            Database database = document.Database;
            int limit = Math.Min(settings.TargetBayCount, option.Bays.Count);
            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                EnsureRegApp(database, transaction);
                BlockTableRecord currentSpace = transaction.GetObject(
                    database.CurrentSpaceId,
                    OpenMode.ForWrite,
                    false) as BlockTableRecord;
                if (currentSpace == null)
                    throw new InvalidOperationException("The current drawing space could not be opened.");

                EraseLinkedBays(currentSpace, transaction, boundary.HandleText);
                ObjectId layerId = GetOrCreateLayer(
                    database,
                    transaction,
                    "CE-PARK-OPTION-" +
                    option.AngleDegrees.ToString("0", CultureInfo.InvariantCulture));

                for (int index = 0; index < limit; index++)
                {
                    ParkingBayGeometry geometry = option.Bays[index];
                    var bay = new Polyline(4);
                    bay.SetDatabaseDefaults(database);
                    bay.LayerId = layerId;
                    bay.Elevation = boundary.Elevation;
                    bay.AddVertexAt(0, ToPoint2d(geometry.First), 0.0, 0.0, 0.0);
                    bay.AddVertexAt(1, ToPoint2d(geometry.Second), 0.0, 0.0, 0.0);
                    bay.AddVertexAt(2, ToPoint2d(geometry.Third), 0.0, 0.0, 0.0);
                    bay.AddVertexAt(3, ToPoint2d(geometry.Fourth), 0.0, 0.0, 0.0);
                    bay.Closed = true;
                    WriteLink(
                        bay,
                        boundary.HandleText,
                        option.AngleDegrees,
                        settings,
                        index + 1);
                    currentSpace.AppendEntity(bay);
                    transaction.AddNewlyCreatedDBObject(bay, true);
                }

                transaction.Commit();
            }
            return limit;
        }

        private static int EraseLinkedBays(
            Database database,
            string boundaryHandle)
        {
            int erased;
            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                BlockTableRecord currentSpace = transaction.GetObject(
                    database.CurrentSpaceId,
                    OpenMode.ForWrite,
                    false) as BlockTableRecord;
                erased = currentSpace == null
                    ? 0
                    : EraseLinkedBays(currentSpace, transaction, boundaryHandle);
                transaction.Commit();
            }
            return erased;
        }

        private static int EraseLinkedBays(
            BlockTableRecord currentSpace,
            Transaction transaction,
            string boundaryHandle)
        {
            int erased = 0;
            foreach (ObjectId objectId in currentSpace.Cast<ObjectId>().ToList())
            {
                Entity entity = transaction.GetObject(
                    objectId,
                    OpenMode.ForRead,
                    false) as Entity;
                ParkingOptionSettings settings;
                string linkedBoundary;
                if (entity == null ||
                    !TryReadLink(entity, out linkedBoundary, out settings) ||
                    !string.Equals(
                        linkedBoundary,
                        boundaryHandle,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                entity.UpgradeOpen();
                entity.Erase();
                erased++;
            }
            return erased;
        }

        private static bool TryReadLinkedSettings(
            Database database,
            string boundaryHandle,
            out ParkingOptionSettings settings,
            out int count)
        {
            settings = null;
            count = 0;
            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                BlockTableRecord currentSpace = transaction.GetObject(
                    database.CurrentSpaceId,
                    OpenMode.ForRead,
                    false) as BlockTableRecord;
                if (currentSpace == null) return false;

                foreach (ObjectId objectId in currentSpace)
                {
                    Entity entity = transaction.GetObject(
                        objectId,
                        OpenMode.ForRead,
                        false) as Entity;
                    ParkingOptionSettings candidate;
                    string linkedBoundary;
                    if (entity == null ||
                        !TryReadLink(entity, out linkedBoundary, out candidate) ||
                        !string.Equals(
                            linkedBoundary,
                            boundaryHandle,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    count++;
                    if (settings == null) settings = candidate;
                }
            }
            return settings != null;
        }

        private static void WriteLink(
            Entity entity,
            string boundaryHandle,
            double angleDegrees,
            ParkingOptionSettings settings,
            int index)
        {
            entity.XData = new ResultBuffer(
                new TypedValue(
                    (int)DxfCode.ExtendedDataRegAppName,
                    RegAppName),
                new TypedValue(
                    (int)DxfCode.ExtendedDataAsciiString,
                    "Schema=" + SchemaVersion),
                new TypedValue(
                    (int)DxfCode.ExtendedDataAsciiString,
                    "Boundary=" + boundaryHandle),
                new TypedValue(
                    (int)DxfCode.ExtendedDataAsciiString,
                    "Angle=" + angleDegrees.ToString("R", CultureInfo.InvariantCulture)),
                new TypedValue(
                    (int)DxfCode.ExtendedDataAsciiString,
                    "Target=" + settings.TargetBayCount.ToString(CultureInfo.InvariantCulture)),
                new TypedValue(
                    (int)DxfCode.ExtendedDataAsciiString,
                    "Width=" + settings.BayWidth.ToString("R", CultureInfo.InvariantCulture)),
                new TypedValue(
                    (int)DxfCode.ExtendedDataAsciiString,
                    "Depth=" + settings.BayDepth.ToString("R", CultureInfo.InvariantCulture)),
                new TypedValue(
                    (int)DxfCode.ExtendedDataAsciiString,
                    "Aisle=" + settings.AisleWidth.ToString("R", CultureInfo.InvariantCulture)),
                new TypedValue(
                    (int)DxfCode.ExtendedDataAsciiString,
                    "Index=" + index.ToString(CultureInfo.InvariantCulture)));
        }

        private static bool TryReadLink(
            Entity entity,
            out string boundaryHandle,
            out ParkingOptionSettings settings)
        {
            boundaryHandle = string.Empty;
            settings = null;
            ResultBuffer data = entity.GetXDataForApplication(RegAppName);
            if (data == null) return false;

            double angle = 90.0;
            int target = 0;
            double width = 0.0;
            double depth = 0.0;
            double aisle = 0.0;
            foreach (TypedValue typedValue in data)
            {
                string text = typedValue.Value as string;
                if (string.IsNullOrWhiteSpace(text)) continue;
                if (text.StartsWith("Boundary=", StringComparison.OrdinalIgnoreCase))
                    boundaryHandle = text.Substring("Boundary=".Length);
                else if (text.StartsWith("Angle=", StringComparison.OrdinalIgnoreCase))
                    double.TryParse(
                        text.Substring("Angle=".Length),
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out angle);
                else if (text.StartsWith("Target=", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(
                        text.Substring("Target=".Length),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out target);
                else if (text.StartsWith("Width=", StringComparison.OrdinalIgnoreCase))
                    double.TryParse(
                        text.Substring("Width=".Length),
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out width);
                else if (text.StartsWith("Depth=", StringComparison.OrdinalIgnoreCase))
                    double.TryParse(
                        text.Substring("Depth=".Length),
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out depth);
                else if (text.StartsWith("Aisle=", StringComparison.OrdinalIgnoreCase))
                    double.TryParse(
                        text.Substring("Aisle=".Length),
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out aisle);
            }

            if (string.IsNullOrWhiteSpace(boundaryHandle) ||
                target <= 0 ||
                width <= GeometryTolerance ||
                depth <= GeometryTolerance ||
                aisle <= GeometryTolerance)
            {
                return false;
            }

            settings = new ParkingOptionSettings(
                target,
                width,
                depth,
                aisle,
                angle);
            return true;
        }

        private static ParkingBoundary PromptBoundary(Document document)
        {
            var options = new PromptEntityOptions(
                "\nSelect one closed parking-area boundary polyline: ");
            options.SetRejectMessage("\nSelect a closed lightweight 2D polyline.");
            options.AddAllowedClass(typeof(Polyline), false);
            PromptEntityResult result = document.Editor.GetEntity(options);
            if (result.Status != PromptStatus.OK) return null;

            ParkingBoundary boundary = ReadBoundary(
                document.Database,
                result.ObjectId);
            if (boundary == null)
            {
                document.Editor.WriteMessage(
                    "\nCE parking options require a closed, non-self-intersecting plan polyline with at least three vertices.");
            }
            return boundary;
        }

        private static ParkingBoundary ReadBoundary(
            Database database,
            ObjectId objectId)
        {
            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                Polyline polyline = transaction.GetObject(
                    objectId,
                    OpenMode.ForRead,
                    false) as Polyline;
                if (polyline == null ||
                    !polyline.Closed ||
                    polyline.NumberOfVertices < 3 ||
                    Math.Abs(polyline.Area) <= GeometryTolerance)
                {
                    return null;
                }

                LayerTableRecord layer = transaction.GetObject(
                    polyline.LayerId,
                    OpenMode.ForRead,
                    false) as LayerTableRecord;
                if (layer != null && layer.IsLocked) return null;

                var worldPoints = new List<Point3d>();
                for (int segment = 0;
                     segment < polyline.NumberOfVertices;
                     segment++)
                {
                    SegmentType segmentType = polyline.GetSegmentType(segment);
                    int samples = segmentType == SegmentType.Arc ? 12 : 1;
                    for (int sample = 0; sample < samples; sample++)
                    {
                        double parameter = segment + (sample / (double)samples);
                        Point3d point = polyline.GetPointAtParameter(parameter);
                        if (worldPoints.Count == 0 ||
                            worldPoints[worldPoints.Count - 1].DistanceTo(point) >
                            GeometryTolerance)
                        {
                            worldPoints.Add(point);
                        }
                    }
                }

                if (worldPoints.Count < 3) return null;
                int longestIndex = 0;
                double longest = 0.0;
                for (int index = 0; index < worldPoints.Count; index++)
                {
                    Point3d first = worldPoints[index];
                    Point3d second = worldPoints[(index + 1) % worldPoints.Count];
                    double length = new Vector2d(
                        second.X - first.X,
                        second.Y - first.Y).Length;
                    if (length > longest)
                    {
                        longest = length;
                        longestIndex = index;
                    }
                }
                if (longest <= GeometryTolerance) return null;

                Point3d origin = worldPoints[longestIndex];
                Point3d next = worldPoints[(longestIndex + 1) % worldPoints.Count];
                Vector3d direction = new Vector3d(
                    next.X - origin.X,
                    next.Y - origin.Y,
                    0.0).GetNormal();
                Vector3d normal = Vector3d.ZAxis.CrossProduct(direction).GetNormal();
                var local = new List<Point2d>();
                foreach (Point3d world in worldPoints)
                {
                    Vector3d offset = world - origin;
                    local.Add(new Point2d(
                        offset.DotProduct(direction),
                        offset.DotProduct(normal)));
                }

                return new ParkingBoundary(
                    objectId,
                    objectId.Handle.ToString(),
                    polyline.LayerId,
                    polyline.Elevation,
                    Math.Abs(polyline.Area),
                    origin,
                    direction,
                    normal,
                    local);
            }
        }

        private static bool PromptBoundaryOrBay(
            Document document,
            out string boundaryHandle,
            out ObjectId boundaryId)
        {
            boundaryHandle = string.Empty;
            boundaryId = ObjectId.Null;
            var options = new PromptEntityOptions(
                "\nSelect the source parking boundary or one linked CE parking bay: ");
            options.SetRejectMessage("\nSelect a lightweight polyline.");
            options.AddAllowedClass(typeof(Polyline), false);
            PromptEntityResult result = document.Editor.GetEntity(options);
            if (result.Status != PromptStatus.OK) return false;

            using (Transaction transaction =
                document.Database.TransactionManager.StartTransaction())
            {
                Entity entity = transaction.GetObject(
                    result.ObjectId,
                    OpenMode.ForRead,
                    false) as Entity;
                ParkingOptionSettings settings;
                string linkedBoundary;
                if (entity != null &&
                    TryReadLink(entity, out linkedBoundary, out settings))
                {
                    ObjectId resolved;
                    if (!TryResolveHandle(
                            document.Database,
                            linkedBoundary,
                            out resolved))
                    {
                        document.Editor.WriteMessage(
                            "\nThe linked parking boundary is missing.");
                        return false;
                    }
                    boundaryHandle = linkedBoundary;
                    boundaryId = resolved;
                    return true;
                }
            }

            ParkingBoundary boundary = ReadBoundary(
                document.Database,
                result.ObjectId);
            if (boundary == null)
            {
                document.Editor.WriteMessage(
                    "\nThe selected polyline is neither a valid parking boundary nor a linked CE parking bay.");
                return false;
            }
            boundaryHandle = boundary.HandleText;
            boundaryId = boundary.ObjectId;
            return true;
        }

        private static bool PromptSettings(
            Editor editor,
            int target,
            out ParkingOptionSettings settings)
        {
            settings = null;
            double width;
            double depth;
            double aisle;
            if (!PromptPositiveDouble(editor, "Bay width", 2.5, out width))
                return false;
            if (!PromptPositiveDouble(editor, "Bay depth", 5.0, out depth))
                return false;
            if (!PromptPositiveDouble(editor, "Aisle width", 6.0, out aisle))
                return false;
            settings = new ParkingOptionSettings(
                target,
                width,
                depth,
                aisle,
                90.0);
            return true;
        }

        private static bool PromptPositiveInteger(
            Editor editor,
            string name,
            int defaultValue,
            out int value)
        {
            var options = new PromptIntegerOptions(
                "\n" + name + " <" +
                defaultValue.ToString(CultureInfo.InvariantCulture) + ">: ")
            {
                AllowNone = true,
                AllowNegative = false,
                AllowZero = false,
                DefaultValue = defaultValue,
                UseDefaultValue = true
            };
            PromptIntegerResult result = editor.GetInteger(options);
            value = result.Status == PromptStatus.OK
                ? result.Value
                : defaultValue;
            return result.Status == PromptStatus.OK;
        }

        private static bool PromptPositiveDouble(
            Editor editor,
            string name,
            double defaultValue,
            out double value)
        {
            var options = new PromptDoubleOptions(
                "\n" + name + " <" +
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
            value = result.Status == PromptStatus.OK
                ? result.Value
                : defaultValue;
            return result.Status == PromptStatus.OK;
        }

        private static List<KeyValuePair<string, string>> BuildOptionRows(
            ParkingOptionResult option,
            ParkingOptionSettings settings)
        {
            int planned = Math.Min(settings.TargetBayCount, option.Capacity);
            return new List<KeyValuePair<string, string>>
            {
                Pair("Parking angle", option.AngleDegrees.ToString("0", CultureInfo.InvariantCulture) + " degrees"),
                Pair("Target bays", settings.TargetBayCount.ToString(CultureInfo.InvariantCulture)),
                Pair("Calculated capacity", option.Capacity.ToString(CultureInfo.InvariantCulture)),
                Pair("Bays to create", planned.ToString(CultureInfo.InvariantCulture)),
                Pair("Target achieved", option.Capacity >= settings.TargetBayCount ? "Yes" : "No"),
                Pair("Usable rows", option.RowCount.ToString(CultureInfo.InvariantCulture)),
                Pair("Bay width", Format(settings.BayWidth)),
                Pair("Bay depth", Format(settings.BayDepth)),
                Pair("Aisle width", Format(settings.AisleWidth)),
                Pair("Longitudinal pitch", Format(option.Pitch)),
                Pair("Projected row depth", Format(option.ProjectedDepth)),
                Pair("Output", "Individual closed polylines")
            };
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
            ObjectId objectId = layers.Add(layer);
            transaction.AddNewlyCreatedDBObject(layer, true);
            return objectId;
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

        private static bool TryResolveHandle(
            Database database,
            string handleText,
            out ObjectId objectId)
        {
            objectId = ObjectId.Null;
            long value;
            if (!long.TryParse(
                    handleText,
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out value))
            {
                return false;
            }
            try
            {
                objectId = database.GetObjectId(false, new Handle(value), 0);
                return !objectId.IsNull && !objectId.IsErased;
            }
            catch
            {
                return false;
            }
        }

        private static Point2d ToPoint2d(Point3d point)
        {
            return new Point2d(point.X, point.Y);
        }

        private static double DegreesToRadians(double value)
        {
            return value * Math.PI / 180.0;
        }

        private static string Format(double value)
        {
            return value.ToString("N3", CultureInfo.CurrentCulture);
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

    internal sealed class ParkingBoundary
    {
        public ParkingBoundary(
            ObjectId objectId,
            string handleText,
            ObjectId layerId,
            double elevation,
            double area,
            Point3d origin,
            Vector3d direction,
            Vector3d normal,
            IList<Point2d> polygon)
        {
            ObjectId = objectId;
            HandleText = handleText;
            LayerId = layerId;
            Elevation = elevation;
            Area = area;
            Origin = origin;
            Direction = direction;
            Normal = normal;
            Polygon = polygon.ToList();
            MinX = Polygon.Min(point => point.X);
            MaxX = Polygon.Max(point => point.X);
            MinY = Polygon.Min(point => point.Y);
            MaxY = Polygon.Max(point => point.Y);
        }

        public ObjectId ObjectId { get; private set; }
        public string HandleText { get; private set; }
        public ObjectId LayerId { get; private set; }
        public double Elevation { get; private set; }
        public double Area { get; private set; }
        public Point3d Origin { get; private set; }
        public Vector3d Direction { get; private set; }
        public Vector3d Normal { get; private set; }
        public List<Point2d> Polygon { get; private set; }
        public double MinX { get; private set; }
        public double MaxX { get; private set; }
        public double MinY { get; private set; }
        public double MaxY { get; private set; }

        public Point3d ToWorld(Point2d point)
        {
            return Origin +
                (Direction * point.X) +
                (Normal * point.Y);
        }
    }

    internal sealed class ParkingOptionSettings
    {
        public ParkingOptionSettings(
            int targetBayCount,
            double bayWidth,
            double bayDepth,
            double aisleWidth,
            double angleDegrees)
        {
            TargetBayCount = targetBayCount;
            BayWidth = bayWidth;
            BayDepth = bayDepth;
            AisleWidth = aisleWidth;
            AngleDegrees = angleDegrees;
        }

        public int TargetBayCount { get; private set; }
        public double BayWidth { get; private set; }
        public double BayDepth { get; private set; }
        public double AisleWidth { get; private set; }
        public double AngleDegrees { get; private set; }
    }

    internal sealed class ParkingOptionResult
    {
        public ParkingOptionResult(
            double angleDegrees,
            double pitch,
            double projectedDepth,
            int capacity,
            int rowCount,
            IList<ParkingBayGeometry> bays)
        {
            AngleDegrees = angleDegrees;
            Pitch = pitch;
            ProjectedDepth = projectedDepth;
            Capacity = capacity;
            RowCount = rowCount;
            Bays = bays == null
                ? new List<ParkingBayGeometry>()
                : bays.ToList();
        }

        public double AngleDegrees { get; private set; }
        public double Pitch { get; private set; }
        public double ProjectedDepth { get; private set; }
        public int Capacity { get; private set; }
        public int RowCount { get; private set; }
        public List<ParkingBayGeometry> Bays { get; private set; }

        public string DisplayText
        {
            get
            {
                return string.Format(
                    CultureInfo.CurrentCulture,
                    "{0:0}° parking — capacity {1}, rows {2}, pitch {3:N3}",
                    AngleDegrees,
                    Capacity,
                    RowCount,
                    Pitch);
            }
        }
    }

    internal sealed class ParkingBayGeometry
    {
        public ParkingBayGeometry(
            Point3d first,
            Point3d second,
            Point3d third,
            Point3d fourth,
            int rowIndex,
            int sequence)
        {
            First = first;
            Second = second;
            Third = third;
            Fourth = fourth;
            RowIndex = rowIndex;
            Sequence = sequence;
        }

        public Point3d First { get; private set; }
        public Point3d Second { get; private set; }
        public Point3d Third { get; private set; }
        public Point3d Fourth { get; private set; }
        public int RowIndex { get; private set; }
        public int Sequence { get; private set; }
    }

    internal sealed class ParkingOptionsWindow : Window
    {
        private readonly ListBox _options;

        public ParkingOptionsWindow(
            IList<ParkingOptionResult> options,
            int targetBayCount)
        {
            Accepted = false;
            Title = "CE Tools - Parking Layout Alternatives";
            Width = 620;
            Height = 430;
            MinWidth = 520;
            MinHeight = 340;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.CanResize;

            var root = new DockPanel { Margin = new Thickness(16) };
            Content = root;

            var heading = new TextBlock
            {
                Text = "Parking layout alternatives",
                FontSize = 20,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 6)
            };
            DockPanel.SetDock(heading, Dock.Top);
            root.Children.Add(heading);

            var note = new TextBlock
            {
                Text = "Target: " +
                    targetBayCount.ToString(CultureInfo.InvariantCulture) +
                    " bays. Select the preferred alternative. Capacity is based on bays whose complete footprint falls inside the selected boundary.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            };
            DockPanel.SetDock(note, Dock.Top);
            root.Children.Add(note);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            var create = new Button
            {
                Content = "Use Selected Option",
                MinWidth = 150,
                Margin = new Thickness(6, 0, 0, 0),
                Padding = new Thickness(10, 6, 10, 6)
            };
            create.Click += delegate
            {
                SelectedOption = _options.SelectedItem as ParkingOptionResult;
                if (SelectedOption == null)
                {
                    MessageBox.Show(
                        this,
                        "Select one parking option first.",
                        "CE Tools",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }
                Accepted = true;
                DialogResult = true;
            };
            var cancel = new Button
            {
                Content = "Cancel",
                MinWidth = 90,
                Margin = new Thickness(6, 0, 0, 0),
                Padding = new Thickness(10, 6, 10, 6)
            };
            cancel.Click += delegate
            {
                Accepted = false;
                DialogResult = false;
            };
            buttons.Children.Add(create);
            buttons.Children.Add(cancel);
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(buttons);

            _options = new ListBox
            {
                ItemsSource = options == null
                    ? new List<ParkingOptionResult>()
                    : options,
                DisplayMemberPath = "DisplayText",
                MinHeight = 180
            };
            if (_options.Items.Count > 0)
                _options.SelectedIndex = 0;
            root.Children.Add(_options);
        }

        public bool Accepted { get; private set; }
        public ParkingOptionResult SelectedOption { get; private set; }
    }
}

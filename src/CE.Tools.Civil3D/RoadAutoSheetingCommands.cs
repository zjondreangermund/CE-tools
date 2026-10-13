using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using CETools.Core;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivilProfileView = Autodesk.Civil.DatabaseServices.ProfileView;

[assembly: CommandClass(typeof(CETools.Civil3D.RoadAutoSheetingCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Automatic long-road plan/profile sheeting.
    /// A0 defaults to 2000 m per road segment; A1 defaults to 1000 m.
    /// Two plan/profile sets are stacked on each cloned paper layout by default.
    /// Plan viewports are twisted to the sampled road chord so the road reads
    /// horizontally on paper. Existing road/profile production commands remain
    /// unchanged.
    /// </summary>
    public sealed class RoadAutoSheetingCommands
    {
        private const double Tol = 0.000001;
        private const string TitlePrefix = "CE-SHEET:";

        [CommandMethod(
            "CE_TOOLS",
            "CE_ROADAUTOSHEETS",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void CreateRoadSheets()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civil = CivilApplication.ActiveDocument;
            if (document == null || civil == null) return;

            List<string> layouts = ReadPaperLayouts(document.Database);
            if (layouts.Count == 0)
            {
                document.Editor.WriteMessage(
                    "\nCE_ROADAUTOSHEETS: create at least one paper-space layout to use as the sheet template.");
                return;
            }

            string current = LayoutManager.Current == null
                ? string.Empty
                : LayoutManager.Current.CurrentLayout;
            string templateDefault = layouts.FirstOrDefault(
                name => string.Equals(name, current, StringComparison.OrdinalIgnoreCase))
                ?? layouts[0];

            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Automatic Road Plan / Profile Sheets",
                "Create long-road drawing sheets from a paper-layout template. " +
                "A0 defaults to 2000 m per plan/profile segment and A1 to 1000 m. " +
                "Two plan/profile sets are stacked per sheet. Every plan viewport is automatically rotated so its road segment lies horizontally.");
            settings.AddChoice(
                "RoadScope", "01 Roads", "Road scope", "All road alignments",
                "Process every RD-/ROAD alignment or select multiple road alignments after saving settings.",
                new[] { "All road alignments", "Selected road alignments" });
            settings.AddChoice(
                "SheetSize", "02 Sheets", "Sheet size", "A0",
                "Landscape sheet size. A0 defaults to 2000 m per segment; A1 defaults to 1000 m.",
                new[] { "A0", "A1" });
            settings.AddChoice(
                "SegmentLengthMode", "02 Sheets", "Chainage length", "Automatic from sheet size",
                "Use A0=2000 m/A1=1000 m or enter a custom length.",
                new[] { "Automatic from sheet size", "Custom" });
            settings.AddPositiveDouble(
                "CustomSegmentLength", "02 Sheets", "Custom segment length (m)", 2000.0,
                "Used only when Chainage length is Custom.");
            settings.AddPositiveInteger(
                "SegmentsPerSheet", "02 Sheets", "Plan/profile sets per sheet", 2,
                "Default is two stacked plan/profile sets on each sheet.");
            settings.AddChoice(
                "TemplateLayout", "03 Template", "Paper layout template", templateDefault,
                "This layout is cloned so its title block/page setup is retained. The template itself is never modified.",
                layouts);
            settings.AddChoice(
                "ExistingSheets", "03 Template", "Existing generated layouts", "Reuse and refresh",
                "Reuse layouts with the same CE sheet name and replace only their model viewports/CE chainage titles, or create a unique new layout name.",
                new[] { "Reuse and refresh", "Create unique names" });
            settings.AddPositiveDouble(
                "OuterMargin", "04 Paper geometry", "Outer margin (mm)", 15.0,
                "Paper-space margin around generated viewports.");
            settings.AddPositiveDouble(
                "RightReserve", "04 Paper geometry", "Right title-block reserve (mm)", 20.0,
                "Extra paper-space width kept clear at the right side for title-block information.");
            settings.AddPositiveDouble(
                "BottomReserve", "04 Paper geometry", "Bottom reserve (mm)", 20.0,
                "Extra paper-space height kept clear below generated viewports.");
            settings.AddPositiveDouble(
                "PairGap", "04 Paper geometry", "Gap between plan/profile sets (mm)", 10.0,
                "Vertical paper-space gap between stacked chainage sets.");
            settings.AddPositiveDouble(
                "PlanProfileGap", "04 Paper geometry", "Plan-to-profile gap (mm)", 8.0,
                "Vertical paper-space gap between each plan viewport and its matching profile viewport.");
            settings.AddPositiveDouble(
                "PlanClearance", "05 Model fit", "Plan clearance around road (m)", 40.0,
                "Model-space clearance added around the sampled road segment before fitting the rotated plan viewport.");
            settings.AddPositiveDouble(
                "ProfileClearance", "05 Model fit", "Profile-view clearance", 5.0,
                "Model-space clearance around the profile view/range.");
            settings.AddChoice(
                "ChainageTitles", "06 Finish", "Add chainage titles", "Yes",
                "Add a paper-space road/chainage title above every plan viewport.",
                new[] { "Yes", "No" });
            settings.AddChoice(
                "LockViewports", "06 Finish", "Lock generated viewports", "Yes",
                "Lock every generated plan/profile viewport after fitting.",
                new[] { "Yes", "No" });

            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;

            string sheetSize = settings.Text("SheetSize");
            double segmentLength = string.Equals(
                settings.Text("SegmentLengthMode"),
                "Custom",
                StringComparison.OrdinalIgnoreCase)
                ? settings.Double("CustomSegmentLength", 2000.0)
                : RoadSheetingPlanner.DefaultSegmentLength(sheetSize);
            int perSheet = Math.Max(1, settings.Integer("SegmentsPerSheet", 2));
            double margin = settings.Double("OuterMargin", 15.0);
            double rightReserve = settings.Double("RightReserve", 20.0);
            double bottomReserve = settings.Double("BottomReserve", 20.0);
            double pairGap = settings.Double("PairGap", 10.0);
            double planProfileGap = settings.Double("PlanProfileGap", 8.0);
            double planClearance = settings.Double("PlanClearance", 40.0);
            double profileClearance = settings.Double("ProfileClearance", 5.0);
            bool titles = string.Equals(settings.Text("ChainageTitles"), "Yes", StringComparison.OrdinalIgnoreCase);
            bool lockViewports = string.Equals(settings.Text("LockViewports"), "Yes", StringComparison.OrdinalIgnoreCase);
            bool reuse = string.Equals(settings.Text("ExistingSheets"), "Reuse and refresh", StringComparison.OrdinalIgnoreCase);

            List<ObjectId> roadIds = ResolveRoadAlignments(
                document,
                civil,
                settings.Text("RoadScope"));
            if (roadIds.Count == 0)
            {
                document.Editor.WriteMessage(
                    "\nCE_ROADAUTOSHEETS: no road alignments were selected/found.");
                return;
            }

            var roads = new List<RoadInfo>();
            using (Transaction tr = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in roadIds.Distinct())
                {
                    CivilAlignment alignment = null;
                    try { alignment = tr.GetObject(id, OpenMode.ForRead, false) as CivilAlignment; }
                    catch { }
                    if (alignment == null) continue;

                    List<ObjectId> profileViews = ReadProfileViews(
                        document.Database,
                        tr,
                        alignment.ObjectId);
                    if (profileViews.Count == 0)
                    {
                        document.Editor.WriteMessage(
                            "\n{0}: skipped. No profile view is linked to this road. Run CE_ROADPROFILEFULL first.",
                            alignment.Name);
                        continue;
                    }

                    roads.Add(new RoadInfo
                    {
                        AlignmentId = alignment.ObjectId,
                        Name = alignment.Name,
                        StartStation = alignment.StartingStation,
                        EndStation = alignment.EndingStation,
                        ProfileViewIds = profileViews
                    });
                }
            }

            if (roads.Count == 0)
            {
                document.Editor.WriteMessage(
                    "\nCE_ROADAUTOSHEETS: none of the selected roads has a linked profile view.");
                return;
            }

            roads = roads
                .OrderBy(item => RoadSortKey(item.Name))
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            double paperWidth = string.Equals(sheetSize, "A1", StringComparison.OrdinalIgnoreCase)
                ? 841.0
                : 1189.0;
            double paperHeight = string.Equals(sheetSize, "A1", StringComparison.OrdinalIgnoreCase)
                ? 594.0
                : 841.0;

            int sheetsCreated = 0;
            int sheetsRefreshed = 0;
            int planViewports = 0;
            int profileViewports = 0;
            int missingProfiles = 0;
            int warnings = 0;

            foreach (RoadInfo road in roads)
            {
                IReadOnlyList<RoadSheetPlan> sheetPlans;
                try
                {
                    sheetPlans = RoadSheetingPlanner.Create(
                        road.StartStation,
                        road.EndStation,
                        segmentLength,
                        perSheet);
                }
                catch (System.Exception exception)
                {
                    document.Editor.WriteMessage(
                        "\n{0}: sheet planning failed. {1}",
                        road.Name,
                        exception.Message);
                    warnings++;
                    continue;
                }

                foreach (RoadSheetPlan sheetPlan in sheetPlans)
                {
                    string layoutName = BuildLayoutName(
                        road.Name,
                        sheetPlan,
                        sheetSize);
                    bool existed = LayoutExists(document.Database, layoutName);
                    if (existed && !reuse)
                        layoutName = UniqueLayoutName(document.Database, layoutName);

                    if (!existed || !reuse)
                    {
                        string cloneError;
                        if (!TryCloneLayout(
                                settings.Text("TemplateLayout"),
                                layoutName,
                                out cloneError))
                        {
                            document.Editor.WriteMessage(
                                "\n{0}: layout clone failed. {1}",
                                layoutName,
                                cloneError);
                            warnings++;
                            continue;
                        }
                        sheetsCreated++;
                    }
                    else
                    {
                        sheetsRefreshed++;
                    }

                    try
                    {
                        using (DocumentLock documentLock = document.LockDocument())
                        using (Transaction tr = document.Database.TransactionManager.StartTransaction())
                        {
                            Layout layout = FindLayout(
                                document.Database,
                                tr,
                                layoutName);
                            if (layout == null)
                                throw new InvalidOperationException("Generated layout could not be reopened.");

                            BlockTableRecord paper = tr.GetObject(
                                layout.BlockTableRecordId,
                                OpenMode.ForWrite,
                                false) as BlockTableRecord;
                            if (paper == null)
                                throw new InvalidOperationException("Paper-space block could not be opened.");

                            ClearGeneratedPaperObjects(paper, tr);

                            List<PaperPair> pairs = BuildPaperPairs(
                                paperWidth,
                                paperHeight,
                                margin,
                                rightReserve,
                                bottomReserve,
                                pairGap,
                                planProfileGap,
                                sheetPlan.Segments.Count);

                            CivilAlignment alignment = tr.GetObject(
                                road.AlignmentId,
                                OpenMode.ForRead,
                                false) as CivilAlignment;
                            if (alignment == null)
                                throw new InvalidOperationException("Road alignment could not be reopened.");

                            for (int index = 0;
                                 index < sheetPlan.Segments.Count;
                                 index++)
                            {
                                RoadSheetSegment segment = sheetPlan.Segments[index];
                                PaperPair pair = pairs[index];

                                Viewport plan = CreateViewport(
                                    paper,
                                    tr,
                                    pair.PlanCenter,
                                    pair.Width,
                                    pair.PlanHeight);
                                if (FitPlanViewport(
                                        alignment,
                                        plan,
                                        segment.StartStation,
                                        segment.EndStation,
                                        planClearance,
                                        lockViewports))
                                    planViewports++;
                                else
                                    warnings++;

                                ObjectId profileViewId = FindBestProfileView(
                                    road.ProfileViewIds,
                                    tr,
                                    segment.StartStation,
                                    segment.EndStation);
                                if (profileViewId.IsNull)
                                {
                                    missingProfiles++;
                                }
                                else
                                {
                                    CivilProfileView profileView = tr.GetObject(
                                        profileViewId,
                                        OpenMode.ForRead,
                                        false) as CivilProfileView;
                                    Viewport profile = CreateViewport(
                                        paper,
                                        tr,
                                        pair.ProfileCenter,
                                        pair.Width,
                                        pair.ProfileHeight);
                                    if (profileView != null &&
                                        FitProfileViewport(
                                            profileView,
                                            profile,
                                            segment.StartStation,
                                            segment.EndStation,
                                            profileClearance,
                                            lockViewports))
                                        profileViewports++;
                                    else
                                        warnings++;
                                }

                                if (titles)
                                    AddChainageTitle(
                                        paper,
                                        tr,
                                        pair,
                                        road.Name,
                                        segment.StartStation,
                                        segment.EndStation);
                            }

                            tr.Commit();
                        }
                    }
                    catch (System.Exception exception)
                    {
                        warnings++;
                        document.Editor.WriteMessage(
                            "\n{0}: sheet refresh failed. {1}",
                            layoutName,
                            exception.Message);
                    }
                }
            }

            try
            {
                document.Editor.Regen();
                document.Editor.Command("_.REGENALL");
            }
            catch
            {
                try { document.Editor.Regen(); }
                catch { }
            }

            document.Editor.WriteMessage(
                "\nCE_ROADAUTOSHEETS complete. Roads={0}; layouts created={1}; refreshed={2}; plan viewports={3}; profile viewports={4}; missing profile ranges={5}; warnings={6}; sheet={7}; segment length={8:0.###} m; sets/sheet={9}.",
                roads.Count,
                sheetsCreated,
                sheetsRefreshed,
                planViewports,
                profileViewports,
                missingProfiles,
                warnings,
                sheetSize,
                segmentLength,
                perSheet);
        }

        private static List<ObjectId> ResolveRoadAlignments(
            Document document,
            CivilDocument civil,
            string scope)
        {
            var result = new List<ObjectId>();
            if (string.Equals(
                    scope,
                    "Selected road alignments",
                    StringComparison.OrdinalIgnoreCase))
            {
                try { document.Editor.SetImpliedSelection(new ObjectId[0]); }
                catch { }
                PromptSelectionResult selection = document.Editor.GetSelection(
                    new PromptSelectionOptions
                    {
                        MessageForAdding = "\nSelect multiple road alignments: ",
                        MessageForRemoval = "\nRemove road alignments: ",
                        AllowDuplicates = false,
                        RejectObjectsFromNonCurrentSpace = true
                    });
                if (selection.Status != PromptStatus.OK ||
                    selection.Value == null)
                    return result;

                using (Transaction tr = document.Database.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId id in selection.Value.GetObjectIds())
                    {
                        CivilAlignment alignment = null;
                        try { alignment = tr.GetObject(id, OpenMode.ForRead, false) as CivilAlignment; }
                        catch { }
                        if (alignment != null && IsRoadAlignment(alignment.Name))
                            result.Add(id);
                    }
                }
                return result;
            }

            using (Transaction tr = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in civil.GetAlignmentIds())
                {
                    CivilAlignment alignment = null;
                    try { alignment = tr.GetObject(id, OpenMode.ForRead, false) as CivilAlignment; }
                    catch { }
                    if (alignment != null && IsRoadAlignment(alignment.Name))
                        result.Add(id);
                }
            }
            return result;
        }

        private static bool IsRoadAlignment(string name)
        {
            string value = (name ?? string.Empty).Trim().ToUpperInvariant();
            return value.StartsWith("RD-") ||
                value.StartsWith("ROAD") ||
                value.Contains(" ROAD ");
        }

        private static int RoadSortKey(string name)
        {
            string value = name ?? string.Empty;
            int best = int.MaxValue;
            int current = 0;
            bool reading = false;
            foreach (char ch in value)
            {
                if (char.IsDigit(ch))
                {
                    reading = true;
                    current = Math.Min(999999, current * 10 + (ch - '0'));
                }
                else if (reading)
                {
                    best = current;
                    break;
                }
            }
            if (reading && best == int.MaxValue) best = current;
            return best;
        }

        private static List<ObjectId> ReadProfileViews(
            Database database,
            Transaction tr,
            ObjectId alignmentId)
        {
            var result = new List<ObjectId>();
            BlockTableRecord model = tr.GetObject(
                SymbolUtilityServices.GetBlockModelSpaceId(database),
                OpenMode.ForRead,
                false) as BlockTableRecord;
            if (model == null) return result;

            foreach (ObjectId id in model)
            {
                CivilProfileView view = null;
                try { view = tr.GetObject(id, OpenMode.ForRead, false) as CivilProfileView; }
                catch { }
                if (view != null && view.AlignmentId == alignmentId)
                    result.Add(id);
            }
            return result;
        }

        private static ObjectId FindBestProfileView(
            IEnumerable<ObjectId> ids,
            Transaction tr,
            double start,
            double end)
        {
            ObjectId containing = ObjectId.Null;
            double containingSpan = double.MaxValue;
            foreach (ObjectId id in ids ?? Enumerable.Empty<ObjectId>())
            {
                CivilProfileView view = null;
                try { view = tr.GetObject(id, OpenMode.ForRead, false) as CivilProfileView; }
                catch { }
                if (view == null) continue;

                double viewStart;
                double viewEnd;
                try
                {
                    viewStart = view.StationStart;
                    viewEnd = view.StationEnd;
                }
                catch
                {
                    continue;
                }

                if (Math.Abs(viewStart - start) <= 0.01 &&
                    Math.Abs(viewEnd - end) <= 0.01)
                    return id;

                if (viewStart <= start + 0.01 &&
                    viewEnd >= end - 0.01)
                {
                    double span = viewEnd - viewStart;
                    if (span < containingSpan)
                    {
                        containing = id;
                        containingSpan = span;
                    }
                }
            }
            return containing;
        }

        private static bool FitPlanViewport(
            CivilAlignment alignment,
            Viewport viewport,
            double start,
            double end,
            double clearance,
            bool lockAfter)
        {
            if (alignment == null || viewport == null) return false;
            var points = new List<Point2d>();
            const int samples = 40;
            for (int index = 0; index <= samples; index++)
            {
                double station = start + (end - start) * index / samples;
                Point2d point;
                if (TryPointLocation(alignment, station, out point))
                    points.Add(point);
            }
            if (points.Count < 2) return false;

            Point2d first = points[0];
            Point2d last = points[points.Count - 1];
            double dx = last.X - first.X;
            double dy = last.Y - first.Y;
            if (Math.Sqrt(dx * dx + dy * dy) <= Tol)
            {
                Point2d before = points[Math.Max(0, points.Count / 2 - 1)];
                Point2d after = points[Math.Min(points.Count - 1, points.Count / 2 + 1)];
                dx = after.X - before.X;
                dy = after.Y - before.Y;
            }
            double angle = Math.Atan2(dy, dx);
            double cos = Math.Cos(angle);
            double sin = Math.Sin(angle);

            double minU = double.MaxValue;
            double maxU = double.MinValue;
            double minV = double.MaxValue;
            double maxV = double.MinValue;
            foreach (Point2d point in points)
            {
                double u = cos * point.X + sin * point.Y;
                double v = -sin * point.X + cos * point.Y;
                minU = Math.Min(minU, u);
                maxU = Math.Max(maxU, u);
                minV = Math.Min(minV, v);
                maxV = Math.Max(maxV, v);
            }

            minU -= clearance;
            maxU += clearance;
            minV -= clearance;
            maxV += clearance;
            double width = Math.Max(Tol, maxU - minU);
            double height = Math.Max(Tol, maxV - minV);
            double centerU = (minU + maxU) * 0.5;
            double centerV = (minV + maxV) * 0.5;
            double centerX = cos * centerU - sin * centerV;
            double centerY = sin * centerU + cos * centerV;
            return ApplyViewport(
                viewport,
                new Point3d(centerX, centerY, 0.0),
                width,
                height,
                angle,
                lockAfter);
        }

        private static bool TryPointLocation(
            CivilAlignment alignment,
            double station,
            out Point2d point)
        {
            point = Point2d.Origin;
            if (alignment == null) return false;
            try
            {
                MethodInfo method = alignment.GetType()
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(item =>
                    {
                        if (!string.Equals(item.Name, "PointLocation", StringComparison.Ordinal))
                            return false;
                        ParameterInfo[] p = item.GetParameters();
                        return p.Length == 4 &&
                            p[0].ParameterType == typeof(double) &&
                            p[1].ParameterType == typeof(double);
                    });
                if (method == null) return false;
                object[] args = { station, 0.0, 0.0, 0.0 };
                method.Invoke(alignment, args);
                double x = Convert.ToDouble(args[2], CultureInfo.InvariantCulture);
                double y = Convert.ToDouble(args[3], CultureInfo.InvariantCulture);
                if (double.IsNaN(x) || double.IsNaN(y) ||
                    double.IsInfinity(x) || double.IsInfinity(y))
                    return false;
                point = new Point2d(x, y);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool FitProfileViewport(
            CivilProfileView profileView,
            Viewport viewport,
            double start,
            double end,
            double clearance,
            bool lockAfter)
        {
            if (profileView == null || viewport == null) return false;
            Extents3d extents;
            try { extents = profileView.GeometricExtents; }
            catch { return false; }

            double viewStart;
            double viewEnd;
            try
            {
                viewStart = profileView.StationStart;
                viewEnd = profileView.StationEnd;
            }
            catch
            {
                return false;
            }

            double minX = extents.MinPoint.X;
            double maxX = extents.MaxPoint.X;
            double minY = extents.MinPoint.Y;
            double maxY = extents.MaxPoint.Y;

            if (viewEnd > viewStart + Tol &&
                (start > viewStart + 0.01 || end < viewEnd - 0.01))
            {
                double fractionStart = Math.Max(0.0, Math.Min(1.0, (start - viewStart) / (viewEnd - viewStart)));
                double fractionEnd = Math.Max(0.0, Math.Min(1.0, (end - viewStart) / (viewEnd - viewStart)));
                double fullWidth = maxX - minX;
                double croppedMin = minX + fullWidth * fractionStart;
                double croppedMax = minX + fullWidth * fractionEnd;
                minX = Math.Min(croppedMin, croppedMax);
                maxX = Math.Max(croppedMin, croppedMax);
            }

            minX -= clearance;
            maxX += clearance;
            minY -= clearance;
            maxY += clearance;

            return ApplyViewport(
                viewport,
                new Point3d(
                    (minX + maxX) * 0.5,
                    (minY + maxY) * 0.5,
                    0.0),
                Math.Max(Tol, maxX - minX),
                Math.Max(Tol, maxY - minY),
                0.0,
                lockAfter);
        }

        private static bool ApplyViewport(
            Viewport viewport,
            Point3d target,
            double modelWidth,
            double modelHeight,
            double twist,
            bool lockAfter)
        {
            if (viewport == null ||
                modelWidth <= Tol ||
                modelHeight <= Tol ||
                viewport.Width <= Tol ||
                viewport.Height <= Tol)
                return false;

            double scale = Math.Min(
                viewport.Width / modelWidth,
                viewport.Height / modelHeight);
            if (scale <= Tol ||
                double.IsNaN(scale) ||
                double.IsInfinity(scale))
                return false;

            try
            {
                viewport.Locked = false;
                viewport.On = true;
                viewport.ViewDirection = Vector3d.ZAxis;
                viewport.ViewTarget = target;
                viewport.ViewCenter = Point2d.Origin;
                viewport.TwistAngle = twist;
                viewport.ViewHeight = viewport.Height / scale;
                viewport.CustomScale = scale;
                viewport.Locked = lockAfter;
                viewport.RecordGraphicsModified(true);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static Viewport CreateViewport(
            BlockTableRecord paper,
            Transaction tr,
            Point3d center,
            double width,
            double height)
        {
            var viewport = new Viewport();
            viewport.SetDatabaseDefaults();
            viewport.CenterPoint = center;
            viewport.Width = width;
            viewport.Height = height;
            paper.AppendEntity(viewport);
            tr.AddNewlyCreatedDBObject(viewport, true);
            viewport.On = true;
            return viewport;
        }

        private static List<PaperPair> BuildPaperPairs(
            double paperWidth,
            double paperHeight,
            double margin,
            double rightReserve,
            double bottomReserve,
            double pairGap,
            double planProfileGap,
            int count)
        {
            double left = Math.Max(0.0, margin);
            double right = Math.Max(left + 50.0, paperWidth - margin - Math.Max(0.0, rightReserve));
            double bottom = Math.Max(0.0, margin + Math.Max(0.0, bottomReserve));
            double top = Math.Max(bottom + 50.0, paperHeight - margin);
            double width = right - left;
            double totalHeight = top - bottom;
            double safePairGap = count > 1 ? Math.Max(0.0, pairGap) : 0.0;
            double pairHeight = (totalHeight - safePairGap * Math.Max(0, count - 1)) / Math.Max(1, count);
            if (pairHeight <= 30.0)
                throw new InvalidOperationException("Paper margins/reserves leave too little height for the requested number of plan/profile sets.");

            var result = new List<PaperPair>();
            for (int index = 0; index < count; index++)
            {
                double pairTop = top - index * (pairHeight + safePairGap);
                double usable = Math.Max(20.0, pairHeight - planProfileGap - 5.0);
                double planHeight = usable * 0.58;
                double profileHeight = usable - planHeight;
                double titleHeight = 5.0;
                double planTop = pairTop - titleHeight;
                Point3d planCenter = new Point3d(
                    (left + right) * 0.5,
                    planTop - planHeight * 0.5,
                    0.0);
                Point3d profileCenter = new Point3d(
                    (left + right) * 0.5,
                    planTop - planHeight - planProfileGap - profileHeight * 0.5,
                    0.0);
                result.Add(new PaperPair
                {
                    Left = left,
                    Top = pairTop,
                    Width = width,
                    PlanHeight = planHeight,
                    ProfileHeight = profileHeight,
                    PlanCenter = planCenter,
                    ProfileCenter = profileCenter
                });
            }
            return result;
        }

        private static void AddChainageTitle(
            BlockTableRecord paper,
            Transaction tr,
            PaperPair pair,
            string roadName,
            double start,
            double end)
        {
            var text = new MText();
            text.SetDatabaseDefaults();
            text.Location = new Point3d(pair.Left, pair.Top - 1.0, 0.0);
            text.Attachment = AttachmentPoint.TopLeft;
            text.TextHeight = 3.5;
            text.Contents = TitlePrefix + " " + roadName +
                " | CH " + RoadSheetingPlanner.StationText(start) +
                " - " + RoadSheetingPlanner.StationText(end);
            paper.AppendEntity(text);
            tr.AddNewlyCreatedDBObject(text, true);
        }

        private static void ClearGeneratedPaperObjects(
            BlockTableRecord paper,
            Transaction tr)
        {
            foreach (ObjectId id in paper.Cast<ObjectId>().ToList())
            {
                Entity entity = null;
                try { entity = tr.GetObject(id, OpenMode.ForWrite, false) as Entity; }
                catch { }
                if (entity == null) continue;

                Viewport viewport = entity as Viewport;
                if (viewport != null)
                {
                    if (viewport.Number > 1)
                    {
                        try { viewport.Erase(); }
                        catch { }
                    }
                    continue;
                }

                MText text = entity as MText;
                if (text != null &&
                    (text.Contents ?? string.Empty).StartsWith(
                        TitlePrefix,
                        StringComparison.OrdinalIgnoreCase))
                {
                    try { text.Erase(); }
                    catch { }
                }
            }
        }

        private static List<string> ReadPaperLayouts(Database database)
        {
            var result = new List<string>();
            using (Transaction tr = database.TransactionManager.StartTransaction())
            {
                DBDictionary layouts = tr.GetObject(
                    database.LayoutDictionaryId,
                    OpenMode.ForRead,
                    false) as DBDictionary;
                if (layouts == null) return result;
                foreach (DBDictionaryEntry entry in layouts)
                {
                    Layout layout = tr.GetObject(
                        entry.Value,
                        OpenMode.ForRead,
                        false) as Layout;
                    if (layout != null && !layout.ModelType)
                        result.Add(layout.LayoutName);
                }
            }
            return result
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool LayoutExists(Database database, string name)
        {
            using (Transaction tr = database.TransactionManager.StartTransaction())
                return FindLayout(database, tr, name) != null;
        }

        private static Layout FindLayout(
            Database database,
            Transaction tr,
            string name)
        {
            DBDictionary layouts = tr.GetObject(
                database.LayoutDictionaryId,
                OpenMode.ForRead,
                false) as DBDictionary;
            if (layouts == null) return null;
            foreach (DBDictionaryEntry entry in layouts)
            {
                Layout layout = tr.GetObject(
                    entry.Value,
                    OpenMode.ForRead,
                    false) as Layout;
                if (layout != null &&
                    string.Equals(layout.LayoutName, name, StringComparison.OrdinalIgnoreCase))
                    return layout;
            }
            return null;
        }

        private static bool TryCloneLayout(
            string template,
            string newName,
            out string error)
        {
            error = string.Empty;
            try
            {
                LayoutManager manager = LayoutManager.Current;
                if (manager == null)
                {
                    error = "LayoutManager is unavailable.";
                    return false;
                }

                MethodInfo clone = manager.GetType()
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(method =>
                    {
                        if (!string.Equals(method.Name, "CloneLayout", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(method.Name, "CopyLayout", StringComparison.OrdinalIgnoreCase))
                            return false;
                        ParameterInfo[] p = method.GetParameters();
                        return p.Length == 3 &&
                            p[0].ParameterType == typeof(string) &&
                            p[1].ParameterType == typeof(string) &&
                            p[2].ParameterType == typeof(int);
                    });
                if (clone == null)
                {
                    error = "This AutoCAD build does not expose LayoutManager.CloneLayout/CopyLayout.";
                    return false;
                }

                int tabOrder = Math.Max(1, ReadPaperLayouts(
                    AcApplication.DocumentManager.MdiActiveDocument.Database).Count + 1);
                clone.Invoke(manager, new object[] { template, newName, tabOrder });
                return true;
            }
            catch (TargetInvocationException exception)
            {
                error = exception.InnerException == null
                    ? exception.Message
                    : exception.InnerException.Message;
                return false;
            }
            catch (System.Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }

        private static string UniqueLayoutName(Database database, string desired)
        {
            if (!LayoutExists(database, desired)) return desired;
            for (int suffix = 2; suffix < 1000; suffix++)
            {
                string candidate = desired + "-" + suffix.ToString(CultureInfo.InvariantCulture);
                if (!LayoutExists(database, candidate)) return candidate;
            }
            return desired + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
        }

        private static string BuildLayoutName(
            string roadName,
            RoadSheetPlan sheet,
            string size)
        {
            string clean = new string((roadName ?? "ROAD")
                .Where(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_')
                .ToArray());
            if (string.IsNullOrWhiteSpace(clean)) clean = "ROAD";
            double start = sheet.Segments.First().StartStation;
            double end = sheet.Segments.Last().EndStation;
            string value = string.Format(
                CultureInfo.InvariantCulture,
                "CE-{0}-{1:0000}-{2:0000}-{3}",
                clean,
                Math.Round(start),
                Math.Round(end),
                size);
            return value.Length <= 60 ? value : value.Substring(0, 60);
        }

        private sealed class RoadInfo
        {
            internal ObjectId AlignmentId;
            internal string Name;
            internal double StartStation;
            internal double EndStation;
            internal List<ObjectId> ProfileViewIds;
        }

        private sealed class PaperPair
        {
            internal double Left;
            internal double Top;
            internal double Width;
            internal double PlanHeight;
            internal double ProfileHeight;
            internal Point3d PlanCenter;
            internal Point3d ProfileCenter;
        }
    }
}

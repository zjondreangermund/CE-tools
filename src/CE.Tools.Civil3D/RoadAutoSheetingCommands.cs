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
        private const string NorthArrowLayerName = "CE-NORTH-ARROWS";

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
            settings.AddChoice(
                "ViewportSizing", "04 Paper geometry", "Viewport paper sizing", "Specified dimensions",
                "Use fixed paper-space viewport sizes like the approved template, or let CE Tools fill the available sheet area.",
                new[] { "Specified dimensions", "Automatic from sheet" });
            settings.AddPositiveDouble(
                "PlanViewportWidth", "04 Paper geometry", "Plan viewport length / width (mm)", 650.0,
                "Paper-space width of each complete plan viewport range before any curve split.");
            settings.AddPositiveDouble(
                "PlanViewportHeight", "04 Paper geometry", "Plan viewport height (mm)", 70.0,
                "Paper-space height of each plan viewport.");
            settings.AddPositiveDouble(
                "ProfileViewportWidth", "04 Paper geometry", "Profile viewport length / width (mm)", 650.0,
                "Paper-space width of the matching long-section/profile viewport.");
            settings.AddPositiveDouble(
                "ProfileViewportHeight", "04 Paper geometry", "Profile viewport height (mm)", 150.0,
                "Paper-space height of the matching long-section/profile viewport including bands.");
            settings.AddChoice(
                "PlanScaleMode", "05 Model fit", "Plan viewport scale", "Automatic fit",
                "Automatically fit each plan range or force a specified 1:N viewport scale.",
                new[] { "Automatic fit", "Specified scale" });
            settings.AddPositiveDouble(
                "PlanScaleDenominator", "05 Model fit", "Plan scale denominator (1:N)", 1000.0,
                "Example: enter 1000 for 1:1000. Used only when Plan viewport scale is Specified scale.");
            settings.AddChoice(
                "ProfileScaleMode", "05 Model fit", "Profile viewport scale", "Automatic fit",
                "Automatically fit the long section or force a specified 1:N viewport scale.",
                new[] { "Automatic fit", "Specified scale" });
            settings.AddChoice("ProfileFitPriority", "05 Model fit",
                "Profile viewport fit priority", "Fit complete profile content",
                "Fit the selected long-section including bands and annotations, or retain a forced viewport scale even when it clips.",
                new[] { "Fit complete profile content", "Retain specified scale" });
            settings.AddPositiveDouble(
                "ProfileScaleDenominator", "05 Model fit", "Profile scale denominator (1:N)", 1000.0,
                "Example: enter 1000 for 1:1000. Used only when Profile viewport scale is Specified scale.");
            settings.AddPositiveDouble(
                "ModelUnitsPerMetre", "05 Model fit", "Model units per metre", 1.0,
                "Use 1 for metre drawings or 1000 for millimetre drawings.");
            settings.AddChoice(
                "AddNorthArrow", "06 Finish", "Add north arrow to every plan viewport", "Yes",
                "Insert one north-arrow block for every plan viewport and rotate it to the viewport twist.",
                new[] { "Yes", "No" });
            settings.AddChoice(
                "NorthArrowSource", "06 Finish", "North arrow source", "Select block after saving settings",
                "Use a north-arrow block already defined in the drawing, or select one block reference after saving settings.",
                BuildNorthArrowChoices(document.Database));
            settings.AddPositiveDouble(
                "NorthArrowScale", "06 Finish", "North arrow block scale", 1.0,
                "Uniform paper-space scale factor for each inserted north-arrow block.");
            settings.AddPositiveDouble(
                "NorthArrowOffset", "06 Finish", "North arrow offset left of plan viewport (mm)", 12.0,
                "Distance from the left edge of each plan viewport to the north-arrow insertion point.");
            settings.AddPositiveDouble(
                "PlanClearance", "05 Model fit", "Plan clearance around road (m)", 40.0,
                "Model-space clearance added around the sampled road segment before fitting the rotated plan viewport.");
            settings.AddPositiveDouble("PlanTopTolerance", "05 Model fit", "Plan top tolerance from road centreline (m)", 35.0,
                "Clearance measured above the rotated road centreline.");
            settings.AddPositiveDouble("PlanBottomTolerance", "05 Model fit", "Plan bottom tolerance from road centreline (m)", 35.0,
                "Clearance measured below the rotated road centreline.");
            settings.AddPositiveDouble("PlanStartTolerance", "05 Model fit", "Plan left / start chainage tolerance (m)", 12.0,
                "Extra room before the road segment start.");
            settings.AddPositiveDouble("PlanEndTolerance", "05 Model fit", "Plan right / end chainage tolerance (m)", 12.0,
                "Extra room after the road segment end.");
            settings.AddPositiveDouble("ProfileTopTolerance", "05 Model fit", "Profile top tolerance from content (m)", 3.0,
                "Extra model-space height above the visible profile content.");
            settings.AddPositiveDouble("ProfileBottomTolerance", "05 Model fit", "Profile bottom tolerance from content (m)", 3.0,
                "Extra model-space height below the visible profile content and bands.");
            settings.AddPositiveDouble("ProfileLeftTolerance", "05 Model fit", "Profile left tolerance from content (m)", 3.0,
                "Extra model-space width to the left of the profile labels and frame.");
            settings.AddPositiveDouble("ProfileRightTolerance", "05 Model fit", "Profile right tolerance from content (m)", 3.0,
                "Extra model-space width to the right of the profile labels and frame.");
            settings.AddChoice(
                "CurveSplit", "05 Model fit", "Split plan viewport at disruptive curves", "Yes",
                "When the road heading changes too much inside one normal chainage block, place the remaining chainage in an adjacent plan viewport and rotate it independently.",
                new[] { "Yes", "No" });
            settings.AddPositiveDouble(
                "CurveHeadingTolerance", "05 Model fit", "Maximum heading change per plan viewport (deg)", 12.0,
                "Default 12 degrees. A larger direction change starts a new adjacent plan viewport.");
            settings.AddPositiveDouble(
                "MinimumPlanSubrange", "05 Model fit", "Minimum plan sub-viewport length (m)", 100.0,
                "Prevents small curves from creating unusably short plan viewport ranges.");
            settings.AddPositiveDouble(
                "PlanSubViewportGap", "04 Paper geometry", "Gap between adjacent plan viewports (mm)", 5.0,
                "Paper-space gap between curve-split plan viewports.");
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
            bool specifiedViewportSizing = string.Equals(settings.Text("ViewportSizing"), "Specified dimensions", StringComparison.OrdinalIgnoreCase);
            double planViewportWidth = settings.Double("PlanViewportWidth", 650.0);
            double planViewportHeight = settings.Double("PlanViewportHeight", 70.0);
            double profileViewportWidth = settings.Double("ProfileViewportWidth", 650.0);
            double profileViewportHeight = settings.Double("ProfileViewportHeight", 150.0);
            double modelUnitsPerMetre = settings.Double("ModelUnitsPerMetre", 1.0);
            double planFixedScale = string.Equals(settings.Text("PlanScaleMode"), "Specified scale", StringComparison.OrdinalIgnoreCase)
                ? 1000.0 / (settings.Double("PlanScaleDenominator", 1000.0) * modelUnitsPerMetre)
                : 0.0;
            bool profileFitContent = !string.Equals(
                settings.Text("ProfileFitPriority"), "Retain specified scale",
                StringComparison.OrdinalIgnoreCase);
            double profileFixedScale = string.Equals(settings.Text("ProfileScaleMode"), "Specified scale", StringComparison.OrdinalIgnoreCase)
                ? 1000.0 / (settings.Double("ProfileScaleDenominator", 1000.0) * modelUnitsPerMetre)
                : 0.0;
            bool addNorthArrow = string.Equals(settings.Text("AddNorthArrow"), "Yes", StringComparison.OrdinalIgnoreCase);
            double northArrowScale = settings.Double("NorthArrowScale", 1.0);
            double northArrowOffset = settings.Double("NorthArrowOffset", 12.0);
            ObjectId northArrowBlockId = addNorthArrow
                ? ResolveNorthArrowBlock(document, settings.Text("NorthArrowSource"))
                : ObjectId.Null;
            if (addNorthArrow && northArrowBlockId.IsNull)
                document.Editor.WriteMessage(
                    "\nCE_ROADAUTOSHEETS: north arrows were requested but no north-arrow block was selected/resolved. Sheets will continue without north arrows.");
            double planClearance = settings.Double("PlanClearance", 40.0);
            double planTopTolerance = settings.Double("PlanTopTolerance", 35.0);
            double planBottomTolerance = settings.Double("PlanBottomTolerance", 35.0);
            double planStartTolerance = settings.Double("PlanStartTolerance", 12.0);
            double planEndTolerance = settings.Double("PlanEndTolerance", 12.0);
            double profileTopTolerance = settings.Double("ProfileTopTolerance", 3.0);
            double profileBottomTolerance = settings.Double("ProfileBottomTolerance", 3.0);
            double profileLeftTolerance = settings.Double("ProfileLeftTolerance", 3.0);
            double profileRightTolerance = settings.Double("ProfileRightTolerance", 3.0);
            bool curveSplit = string.Equals(settings.Text("CurveSplit"), "Yes", StringComparison.OrdinalIgnoreCase);
            double curveHeadingTolerance = settings.Double("CurveHeadingTolerance", 12.0);
            double minimumPlanSubrange = settings.Double("MinimumPlanSubrange", 100.0);
            double planSubViewportGap = settings.Double("PlanSubViewportGap", 5.0);
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

            // Resolve impossible fixed paper sizes before cloning any layout. A1 cannot
            // hold two 70 + 250 mm plan/profile pairs with the configured reserves.
            if (specifiedViewportSizing)
            {
                double usableWidth = paperWidth - 2.0 * margin - rightReserve;
                double usableHeight = paperHeight - 2.0 * margin - bottomReserve;
                double pairHeight = 5.0 + planViewportHeight + planProfileGap + profileViewportHeight;
                if (planViewportWidth > usableWidth + 0.01 ||
                    profileViewportWidth > usableWidth + 0.01 ||
                    pairHeight > usableHeight + 0.01)
                {
                    document.Editor.WriteMessage(
                        "\nCE_ROADAUTOSHEETS: viewport dimensions do not fit {0}. " +
                        "Available width={1:0.##} mm, height={2:0.##} mm; " +
                        "requested plan={3:0.##} x {4:0.##} mm, profile={5:0.##} x {6:0.##} mm. " +
                        "Reduce dimensions/reserves or choose a larger sheet.",
                        sheetSize, usableWidth, usableHeight,
                        planViewportWidth, planViewportHeight,
                        profileViewportWidth, profileViewportHeight);
                    return;
                }

                int fittingPairs = (int)Math.Floor(
                    (usableHeight + pairGap) / (pairHeight + pairGap));
                if (perSheet > fittingPairs)
                {
                    document.Editor.WriteMessage(
                        "\nCE_ROADAUTOSHEETS: {0} plan/profile sets cannot fit on {1} " +
                        "with the specified heights. Using {2} set(s) per sheet instead.",
                        perSheet, sheetSize, fittingPairs);
                    perSheet = fittingPairs;
                }

                // A specified 1:N viewport cannot depict a longer chainage range
                // than its paper width. Allow the configured clearance on both ends.
                // This avoids empty fitted viewports at e.g. 1:1000/800 mm/1000 m.
                double maxLength = double.PositiveInfinity;
                if (planFixedScale > Tol)
                    maxLength = Math.Min(maxLength,
                        planViewportWidth / planFixedScale - planStartTolerance - planEndTolerance);
                if (profileFixedScale > Tol)
                    maxLength = Math.Min(maxLength,
                        profileViewportWidth / profileFixedScale - profileLeftTolerance - profileRightTolerance);
                if (maxLength <= Tol)
                {
                    document.Editor.WriteMessage(
                        "\nCE_ROADAUTOSHEETS: viewport width/scale cannot fit even " +
                        "the configured clearance. Increase width, reduce clearance, " +
                        "or use a smaller viewport scale.");
                    return;
                }
                if (segmentLength > maxLength)
                {
                    // Slight headroom for numerical tolerance and alignment curvature.
                    double adjustedLength = Math.Floor(maxLength * 0.95 / 10.0) * 10.0;
                    if (adjustedLength <= Tol)
                        adjustedLength = maxLength * 0.95;
                    document.Editor.WriteMessage(
                        "\nCE_ROADAUTOSHEETS: a {0:0.##} m segment will not fit " +
                        "the specified paper width and scale. Using {1:0.##} m " +
                        "segments to preserve the requested 1:N scale.",
                        segmentLength, adjustedLength);
                    segmentLength = adjustedLength;
                }
            }

            int sheetsCreated = 0;
            int sheetsRefreshed = 0;
            int planViewports = 0;
            int curveSplitPlanViewports = 0;
            int profileViewports = 0;
            int missingProfiles = 0;
            int warnings = 0;
            string originalLayoutName =
                LayoutManager.Current == null
                    ? string.Empty
                    : LayoutManager.Current.CurrentLayout;

            // Interleave sections by road: sheet 1 contains first ranges from
            // RD-01 and RD-02, rather than two ranges from RD-01.
            var allJobs = new List<RoadSheetJob>();
            foreach (RoadInfo road in roads)
            {
                IReadOnlyList<RoadSheetPlan> planned;
                try
                {
                    planned = RoadSheetingPlanner.Create(
                        road.StartStation, road.EndStation, segmentLength, 1);
                }
                catch (System.Exception exception)
                {
                    document.Editor.WriteMessage(
                        "\n{0}: sheet planning failed. {1}", road.Name, exception.Message);
                    warnings++;
                    continue;
                }
                foreach (RoadSheetPlan plannedSheet in planned)
                    foreach (RoadSheetSegment segment in plannedSheet.Segments)
                        allJobs.Add(new RoadSheetJob { Road = road, Segment = segment });
            }
            var orderedJobs = allJobs
                .GroupBy(item => item.Road.Name, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.ToList())
                .ToList();
            var sheetQueue = new List<RoadSheetJob>();
            for (int offset = 0; orderedJobs.Any(group => group.Count > offset); offset++)
                foreach (List<RoadSheetJob> roadJobs in orderedJobs)
                    if (roadJobs.Count > offset)
                        sheetQueue.Add(roadJobs[offset]);

            for (int sheetIndex = 0; sheetIndex * perSheet < sheetQueue.Count; sheetIndex++)
            {
                List<RoadSheetJob> sheetJobs = sheetQueue
                    .Skip(sheetIndex * perSheet).Take(perSheet).ToList();
                    string layoutName = string.Format(
                        CultureInfo.InvariantCulture, "CE-SHEET-{0:000}-{1}",
                        sheetIndex + 1, sheetSize);
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

                    string sheetStage = "ACTIVATE LAYOUT";
                    try
                    {
                        using (DocumentLock documentLock = document.LockDocument())
                        {
                            LayoutManager manager = LayoutManager.Current;
                            if (manager == null)
                                throw new InvalidOperationException("LayoutManager is unavailable.");

                            if (!string.Equals(
                                    manager.CurrentLayout,
                                    layoutName,
                                    StringComparison.OrdinalIgnoreCase))
                                manager.CurrentLayout = layoutName;

                            document.Editor.SwitchToPaperSpace();
                            AcApplication.UpdateScreen();

                            sheetStage = "OPEN PAPER SPACE";
                            using (Transaction tr = document.Database.TransactionManager.StartTransaction())
                            {
                            Layout layout = FindLayout(
                                document.Database,
                                tr,
                                layoutName);
                            if (layout == null)
                                throw new InvalidOperationException("Generated layout could not be reopened.");

                            sheetStage = "OPEN PAPER BLOCK";
                            BlockTableRecord paper = tr.GetObject(
                                layout.BlockTableRecordId,
                                OpenMode.ForWrite,
                                false) as BlockTableRecord;
                            if (paper == null)
                                throw new InvalidOperationException("Paper-space block could not be opened.");

                            sheetStage = "CLEAR OLD VIEWPORTS";
                            ClearGeneratedPaperObjects(paper, tr);

                            sheetStage = "BUILD PAPER SLOTS";
                            List<PaperPair> pairs = BuildPaperPairs(
                                paperWidth,
                                paperHeight,
                                margin,
                                rightReserve,
                                bottomReserve,
                                pairGap,
                                planProfileGap,
                                sheetJobs.Count,
                                specifiedViewportSizing,
                                planViewportWidth,
                                planViewportHeight,
                                profileViewportWidth,
                                profileViewportHeight);

                            for (int index = 0; index < sheetJobs.Count; index++)
                            {
                                RoadSheetJob job = sheetJobs[index];
                                RoadInfo road = job.Road;
                                RoadSheetSegment segment = job.Segment;
                                PaperPair pair = pairs[index];
                                sheetStage = "OPEN ROAD ALIGNMENT " + road.Name;
                                CivilAlignment alignment = tr.GetObject(
                                    road.AlignmentId, OpenMode.ForRead, false) as CivilAlignment;
                                if (alignment == null)
                                    throw new InvalidOperationException("Road alignment could not be reopened.");

                                IReadOnlyList<RoadSheetSegment> planRanges =
                                    curveSplit
                                        ? ReadCurveAwarePlanRanges(
                                            alignment,
                                            segment.StartStation,
                                            segment.EndStation,
                                            curveHeadingTolerance,
                                            minimumPlanSubrange)
                                        : new[]
                                        {
                                            new RoadSheetSegment(
                                                0,
                                                segment.StartStation,
                                                segment.EndStation)
                                        };

                                List<PlanPaperSlot> planSlots = BuildPlanSlots(
                                    pair,
                                    planRanges,
                                    planSubViewportGap);

                                for (int planIndex = 0;
                                     planIndex < planRanges.Count;
                                     planIndex++)
                                {
                                    RoadSheetSegment planRange = planRanges[planIndex];
                                    PlanPaperSlot planSlot = planSlots[planIndex];
                                    sheetStage = "CREATE PLAN VIEWPORT " +
                                        RoadSheetingPlanner.StationText(planRange.StartStation) + "-" +
                                        RoadSheetingPlanner.StationText(planRange.EndStation);
                                    Viewport plan = CreateViewport(
                                        paper,
                                        tr,
                                        planSlot.Center,
                                        planSlot.Width,
                                        pair.PlanHeight);
                                    sheetStage = "FIT PLAN VIEWPORT " +
                                        RoadSheetingPlanner.StationText(planRange.StartStation) + "-" +
                                        RoadSheetingPlanner.StationText(planRange.EndStation);
                                    if (FitPlanViewport(
                                            alignment,
                                            plan,
                                            planRange.StartStation,
                                            planRange.EndStation,
                                            planTopTolerance,
                                            planBottomTolerance,
                                            planStartTolerance,
                                            planEndTolerance,
                                            planFixedScale,
                                            lockViewports))
                                    {
                                        planViewports++;
                                        if (planRanges.Count > 1)
                                            curveSplitPlanViewports++;
                                        if (addNorthArrow && !northArrowBlockId.IsNull)
                                            AddNorthArrow(
                                                paper,
                                                tr,
                                                northArrowBlockId,
                                                planSlot,
                                                pair,
                                                plan.TwistAngle,
                                                northArrowScale,
                                                northArrowOffset);
                                    }
                                    else
                                    {
                                        warnings++;
                                    }

                                    if (titles)
                                        AddChainageTitle(
                                            paper,
                                            tr,
                                            planSlot,
                                            pair.Top,
                                            road.Name,
                                            planRange.StartStation,
                                            planRange.EndStation);
                                }

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
                                    sheetStage = "CREATE PROFILE VIEWPORT " +
                                        RoadSheetingPlanner.StationText(segment.StartStation) + "-" +
                                        RoadSheetingPlanner.StationText(segment.EndStation);
                                    Viewport profile = CreateViewport(
                                        paper,
                                        tr,
                                        pair.ProfileCenter,
                                        pair.ProfileWidth,
                                        pair.ProfileHeight);
                                    sheetStage = "FIT PROFILE VIEWPORT " +
                                        RoadSheetingPlanner.StationText(segment.StartStation) + "-" +
                                        RoadSheetingPlanner.StationText(segment.EndStation);
                                    if (profileView != null &&
                                        FitProfileViewport(
                                            profileView,
                                            profile,
                                            segment.StartStation,
                                            segment.EndStation,
                                            profileTopTolerance,
                                            profileBottomTolerance,
                                            profileLeftTolerance,
                                            profileRightTolerance,
                                            profileFixedScale,
                                            lockViewports,
                                            profileFitContent))
                                        profileViewports++;
                                    else
                                        warnings++;
                                }


                            }

                            sheetStage = "COMMIT SHEET";
                            tr.Commit();
                            }
                        }
                    }
                    catch (System.Exception exception)
                    {
                        warnings++;
                        document.Editor.WriteMessage(
                            "\n{0}: sheet refresh failed at {1}. {2}",
                            layoutName,
                            sheetStage,
                            exception.Message);
                    }
            }

            try
            {
                if (!string.IsNullOrWhiteSpace(originalLayoutName) &&
                    LayoutManager.Current != null &&
                    !string.Equals(
                        LayoutManager.Current.CurrentLayout,
                        originalLayoutName,
                        StringComparison.OrdinalIgnoreCase))
                    LayoutManager.Current.CurrentLayout = originalLayoutName;
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_ROADAUTOSHEETS: could not restore original layout '{0}'. {1}",
                    originalLayoutName,
                    exception.Message);
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
                "\nCE_ROADAUTOSHEETS complete. Roads={0}; layouts created={1}; refreshed={2}; plan viewports={3}; curve-split plan viewports={4}; profile viewports={5}; missing profile ranges={6}; warnings={7}; sheet={8}; segment length={9:0.###} m; sets/sheet={10}.",
                roads.Count,
                sheetsCreated,
                sheetsRefreshed,
                planViewports,
                curveSplitPlanViewports,
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

        private static IReadOnlyList<RoadSheetSegment> ReadCurveAwarePlanRanges(
            CivilAlignment alignment,
            double start,
            double end,
            double maximumHeadingChangeDegrees,
            double minimumSegmentLength)
        {
            if (alignment == null || end <= start + Tol)
                return new[] { new RoadSheetSegment(0, start, end) };

            double length = end - start;
            double sampleStep = Math.Max(5.0, Math.Min(20.0, length / 40.0));
            int count = Math.Max(2, (int)Math.Ceiling(length / sampleStep) + 1);
            var stations = new List<double>();
            var headings = new List<double>();

            for (int index = 0; index < count; index++)
            {
                double station = index == count - 1
                    ? end
                    : Math.Min(end, start + index * sampleStep);
                double heading;
                if (!TryHeadingAtStation(
                        alignment,
                        station,
                        start,
                        end,
                        Math.Max(2.0, Math.Min(10.0, sampleStep * 0.5)),
                        out heading))
                    continue;
                if (stations.Count > 0 &&
                    station <= stations[stations.Count - 1] + Tol)
                    continue;
                stations.Add(station);
                headings.Add(heading);
            }

            if (stations.Count < 2)
                return new[] { new RoadSheetSegment(0, start, end) };

            if (stations[0] > start + Tol)
            {
                stations.Insert(0, start);
                headings.Insert(0, headings[0]);
            }
            if (stations[stations.Count - 1] < end - Tol)
            {
                stations.Add(end);
                headings.Add(headings[headings.Count - 1]);
            }

            try
            {
                return RoadSheetingPlanner.SplitPlanByHeading(
                    stations,
                    headings,
                    maximumHeadingChangeDegrees,
                    minimumSegmentLength,
                    6);
            }
            catch
            {
                return new[] { new RoadSheetSegment(0, start, end) };
            }
        }

        private static bool TryHeadingAtStation(
            CivilAlignment alignment,
            double station,
            double rangeStart,
            double rangeEnd,
            double probe,
            out double heading)
        {
            heading = 0.0;
            double beforeStation = Math.Max(rangeStart, station - probe);
            double afterStation = Math.Min(rangeEnd, station + probe);
            if (afterStation <= beforeStation + Tol)
                return false;

            Point2d before;
            Point2d after;
            if (!TryPointLocation(alignment, beforeStation, out before) ||
                !TryPointLocation(alignment, afterStation, out after))
                return false;

            double dx = after.X - before.X;
            double dy = after.Y - before.Y;
            if (Math.Sqrt(dx * dx + dy * dy) <= Tol)
                return false;

            heading = Math.Atan2(dy, dx);
            return true;
        }

        private static List<PlanPaperSlot> BuildPlanSlots(
            PaperPair pair,
            IReadOnlyList<RoadSheetSegment> ranges,
            double requestedGap)
        {
            if (pair == null)
                throw new ArgumentNullException(nameof(pair));
            if (ranges == null || ranges.Count == 0)
                throw new ArgumentException("At least one plan range is required.", nameof(ranges));

            double gap = ranges.Count > 1
                ? Math.Max(0.0, requestedGap)
                : 0.0;
            double usable = pair.PlanWidth - gap * Math.Max(0, ranges.Count - 1);
            if (usable <= 20.0)
                throw new InvalidOperationException(
                    "The plan area is too narrow for the curve-split viewports. Reduce the adjacent viewport gap or the number of curve splits.");

            double totalLength = ranges.Sum(item => Math.Max(Tol, item.Length));
            var result = new List<PlanPaperSlot>();
            double left = pair.PlanLeft;

            for (int index = 0; index < ranges.Count; index++)
            {
                double width = index == ranges.Count - 1
                    ? pair.PlanLeft + pair.PlanWidth - left
                    : usable * Math.Max(Tol, ranges[index].Length) / totalLength;
                if (width <= 10.0)
                    throw new InvalidOperationException(
                        "A curve split produced a plan viewport that is too narrow to use.");

                result.Add(new PlanPaperSlot
                {
                    Left = left,
                    Width = width,
                    Center = new Point3d(
                        left + width * 0.5,
                        pair.PlanCenter.Y,
                        0.0)
                });
                left += width + gap;
            }
            return result;
        }

        private static bool FitPlanViewport(
            CivilAlignment alignment,
            Viewport viewport,
            double start,
            double end,
            double topTolerance,
            double bottomTolerance,
            double startTolerance,
            double endTolerance,
            double fixedScale,
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

            // Along the rotated road: start/end correspond to sheet left/right.
            minU -= startTolerance;
            maxU += endTolerance;
            // Perpendicular to the road: independent centreline top/bottom.
            minV -= bottomTolerance;
            maxV += topTolerance;
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
                NormalizeTwist(-angle),
                fixedScale,
                lockAfter,
                true);
        }

        private static double NormalizeTwist(double radians)
        {
            double full = Math.PI * 2.0;
            double value = radians % full;
            if (value < 0.0) value += full;
            // Road orientation is axial, so keep all plan annotation readable
            // instead of rotating a westbound alignment upside down.
            if (value > Math.PI * 0.5 && value < Math.PI * 1.5)
                value = (value + Math.PI) % full;
            return value;
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
            double topTolerance,
            double bottomTolerance,
            double leftTolerance,
            double rightTolerance,
            double fixedScale,
            bool lockAfter,
            bool fitContent)
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

            minX -= leftTolerance;
            maxX += rightTolerance;
            minY -= bottomTolerance;
            maxY += topTolerance;

            double width = Math.Max(Tol, maxX - minX);
            double height = Math.Max(Tol, maxY - minY);
            double fitScale = Math.Min(viewport.Width / width, viewport.Height / height);
            double requestedScale = fixedScale;
            // The profile's internal horizontal and vertical exaggeration is part
            // of its Civil 3D style. Paper-space viewport zoom is uniform.
            // An incompatible 1:N can otherwise reveal neighbouring profiles.
            if (fitContent && requestedScale > Tol &&
                (width * requestedScale > viewport.Width + 0.01 ||
                 height * requestedScale > viewport.Height + 0.01))
            {
                Document active = AcApplication.DocumentManager.MdiActiveDocument;
                if (active != null)
                    active.Editor.WriteMessage(
                        "\nCE_ROADAUTOSHEETS: profile at 1:{0:0.##} requires " +
                        "{1:0.##} x {2:0.##} mm; available {3:0.##} x {4:0.##} mm. " +
                        "Fitting content at 1:{5:0.##}. Set Profile viewport fit priority " +
                        "to Retain specified scale to prevent automatic adjustment.",
                        1000.0 / requestedScale, width * requestedScale,
                        height * requestedScale, viewport.Width, viewport.Height,
                        1000.0 / fitScale);
                fixedScale = 0.0;
            }

            return ApplyViewport(
                viewport,
                new Point3d(
                    (minX + maxX) * 0.5,
                    (minY + maxY) * 0.5,
                    0.0),
                width,
                height,
                0.0,
                fixedScale,
                lockAfter);
        }

        private static bool ApplyViewport(
            Viewport viewport,
            Point3d target,
            double modelWidth,
            double modelHeight,
            double twist,
            double fixedScale,
            bool lockAfter,
            bool autoFitIfFixedScaleExceedsPaper = false)
        {
            if (viewport == null ||
                modelWidth <= Tol ||
                modelHeight <= Tol ||
                viewport.Width <= Tol ||
                viewport.Height <= Tol)
                return false;

            double scale = fixedScale > Tol
                ? fixedScale
                : Math.Min(
                    viewport.Width / modelWidth,
                    viewport.Height / modelHeight);
            if (scale <= Tol ||
                double.IsNaN(scale) ||
                double.IsInfinity(scale))
                return false;
            if (fixedScale > Tol &&
                (modelWidth * scale > viewport.Width + 0.01 ||
                 modelHeight * scale > viewport.Height + 0.01))
            {
                if (!autoFitIfFixedScaleExceedsPaper)
                    return false;

                // Honour the user's explicit 1:N; do not secretly alter the viewport scale.
                // If no physically possible paper rectangle fits the chosen dimensions,
                // centre the requested range and report that its outer clearance clips.
                Document activeDocument = AcApplication.DocumentManager.MdiActiveDocument;
                if (activeDocument != null)
                    activeDocument.Editor.WriteMessage(
                        "\nCE_ROADAUTOSHEETS: specified plan scale 1:{0:0.##} retained. " +
                        "The required road+tolerance envelope is {1:0.##} x {2:0.##} mm, " +
                        "but the paper viewport is only {3:0.##} x {4:0.##} mm. " +
                        "Some model content may be clipped. Increase paper dimensions " +
                        "or reduce tolerances/chainage length.",
                        1000.0 / fixedScale,
                        modelWidth * fixedScale, modelHeight * fixedScale,
                        viewport.Width, viewport.Height);
            }

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
            viewport.SetDatabaseDefaults(paper.Database);
            viewport.CenterPoint = center;
            viewport.Width = width;
            viewport.Height = height;
            paper.AppendEntity(viewport);
            tr.AddNewlyCreatedDBObject(viewport, true);
            viewport.On = true;
            viewport.GridOn = false;
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
            int count,
            bool specifiedDimensions,
            double requestedPlanWidth,
            double requestedPlanHeight,
            double requestedProfileWidth,
            double requestedProfileHeight)
        {
            double left = Math.Max(0.0, margin);
            double right = Math.Max(left + 50.0, paperWidth - margin - Math.Max(0.0, rightReserve));
            double bottom = Math.Max(0.0, margin + Math.Max(0.0, bottomReserve));
            double top = Math.Max(bottom + 50.0, paperHeight - margin);
            double availableWidth = right - left;
            double availableHeight = top - bottom;
            double safePairGap = count > 1 ? Math.Max(0.0, pairGap) : 0.0;

            double planWidth;
            double profileWidth;
            double planHeight;
            double profileHeight;
            double pairHeight;

            if (specifiedDimensions)
            {
                planWidth = requestedPlanWidth;
                profileWidth = requestedProfileWidth;
                planHeight = requestedPlanHeight;
                profileHeight = requestedProfileHeight;
                if (planWidth > availableWidth + 0.01 || profileWidth > availableWidth + 0.01)
                    throw new InvalidOperationException("Specified plan/profile viewport length is wider than the available sheet area.");
                pairHeight = 5.0 + planHeight + planProfileGap + profileHeight;
                double required = pairHeight * count + safePairGap * Math.Max(0, count - 1);
                if (required > availableHeight + 0.01)
                    throw new InvalidOperationException("Specified plan/profile viewport heights do not fit on this sheet. Reduce heights/gaps or sets per sheet.");
            }
            else
            {
                planWidth = availableWidth;
                profileWidth = availableWidth;
                pairHeight = (availableHeight - safePairGap * Math.Max(0, count - 1)) / Math.Max(1, count);
                if (pairHeight <= 30.0)
                    throw new InvalidOperationException("Paper margins/reserves leave too little height for the requested number of plan/profile sets.");
                double usable = Math.Max(20.0, pairHeight - planProfileGap - 5.0);
                planHeight = usable * 0.58;
                profileHeight = usable - planHeight;
            }

            var result = new List<PaperPair>();
            for (int index = 0; index < count; index++)
            {
                double pairTop = top - index * (pairHeight + safePairGap);
                double planLeft = left + (availableWidth - planWidth) * 0.5;
                double profileLeft = left + (availableWidth - profileWidth) * 0.5;
                double titleHeight = 5.0;
                double planTop = pairTop - titleHeight;
                Point3d planCenter = new Point3d(
                    planLeft + planWidth * 0.5,
                    planTop - planHeight * 0.5,
                    0.0);
                Point3d profileCenter = new Point3d(
                    profileLeft + profileWidth * 0.5,
                    planTop - planHeight - planProfileGap - profileHeight * 0.5,
                    0.0);
                result.Add(new PaperPair
                {
                    Left = left,
                    Top = pairTop,
                    Width = availableWidth,
                    PlanLeft = planLeft,
                    PlanWidth = planWidth,
                    ProfileLeft = profileLeft,
                    ProfileWidth = profileWidth,
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
            PlanPaperSlot slot,
            double pairTop,
            string roadName,
            double start,
            double end)
        {
            var text = new MText();
            text.SetDatabaseDefaults();
            text.Location = new Point3d(slot.Left, pairTop - 1.0, 0.0);
            text.Width = Math.Max(10.0, slot.Width);
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

                BlockReference block = entity as BlockReference;
                if (block != null &&
                    string.Equals(block.Layer, NorthArrowLayerName, StringComparison.OrdinalIgnoreCase))
                {
                    try { block.Erase(); }
                    catch { }
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

        private static IEnumerable<string> BuildNorthArrowChoices(Database database)
        {
            var result = new List<string> { "Select block after saving settings" };
            if (database == null) return result;
            try
            {
                using (Transaction tr = database.TransactionManager.StartTransaction())
                {
                    BlockTable table = tr.GetObject(
                        database.BlockTableId,
                        OpenMode.ForRead,
                        false) as BlockTable;
                    if (table != null)
                    {
                        foreach (ObjectId id in table)
                        {
                            BlockTableRecord record = tr.GetObject(
                                id,
                                OpenMode.ForRead,
                                false) as BlockTableRecord;
                            if (record == null ||
                                record.IsLayout ||
                                record.IsAnonymous ||
                                string.IsNullOrWhiteSpace(record.Name))
                                continue;
                            string upper = record.Name.ToUpperInvariant();
                            if (upper.Contains("NORTH") ||
                                upper.Contains("N-ARROW") ||
                                upper.Contains("N_ARROW") ||
                                upper.Contains("NORTHARROW"))
                                result.Add(record.Name);
                        }
                    }
                }
            }
            catch { }
            return result
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => string.Equals(name, "Select block after saving settings", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static ObjectId ResolveNorthArrowBlock(
            Document document,
            string source)
        {
            if (document == null) return ObjectId.Null;
            Database database = document.Database;
            if (!string.IsNullOrWhiteSpace(source) &&
                !string.Equals(
                    source,
                    "Select block after saving settings",
                    StringComparison.OrdinalIgnoreCase))
            {
                using (Transaction tr = database.TransactionManager.StartTransaction())
                {
                    BlockTable table = tr.GetObject(
                        database.BlockTableId,
                        OpenMode.ForRead,
                        false) as BlockTable;
                    if (table != null && table.Has(source))
                        return table[source];
                }
            }

            PromptEntityOptions options = new PromptEntityOptions(
                "\nSelect one north-arrow block reference to use on all generated plan viewports: ");
            options.SetRejectMessage("\nSelect a block reference.");
            options.AddAllowedClass(typeof(BlockReference), true);
            PromptEntityResult picked = document.Editor.GetEntity(options);
            if (picked.Status != PromptStatus.OK)
                return ObjectId.Null;

            using (Transaction tr = database.TransactionManager.StartTransaction())
            {
                BlockReference block = tr.GetObject(
                    picked.ObjectId,
                    OpenMode.ForRead,
                    false) as BlockReference;
                return block == null ? ObjectId.Null : block.BlockTableRecord;
            }
        }

        private static void AddNorthArrow(
            BlockTableRecord paper,
            Transaction tr,
            ObjectId blockDefinitionId,
            PlanPaperSlot slot,
            PaperPair pair,
            double viewportTwist,
            double scale,
            double offsetLeft)
        {
            if (paper == null ||
                tr == null ||
                blockDefinitionId.IsNull ||
                slot == null ||
                pair == null)
                return;

            ObjectId layerId = EnsureNorthArrowLayer(
                paper.Database,
                tr);
            Point3d position = new Point3d(
                slot.Left - Math.Max(0.0, offsetLeft),
                pair.PlanCenter.Y,
                0.0);

            var arrow = new BlockReference(
                position,
                blockDefinitionId);
            arrow.SetDatabaseDefaults(paper.Database);
            arrow.LayerId = layerId;
            arrow.Rotation = NormalizeTwist(viewportTwist);
            double safeScale = Math.Max(0.0001, scale);
            arrow.ScaleFactors = new Scale3d(
                safeScale,
                safeScale,
                safeScale);
            paper.AppendEntity(arrow);
            tr.AddNewlyCreatedDBObject(arrow, true);
        }

        private static ObjectId EnsureNorthArrowLayer(
            Database database,
            Transaction tr)
        {
            LayerTable layers = tr.GetObject(
                database.LayerTableId,
                OpenMode.ForRead,
                false) as LayerTable;
            if (layers == null) return ObjectId.Null;
            if (layers.Has(NorthArrowLayerName))
                return layers[NorthArrowLayerName];

            layers.UpgradeOpen();
            var layer = new LayerTableRecord
            {
                Name = NorthArrowLayerName,
                IsPlottable = true
            };
            ObjectId id = layers.Add(layer);
            tr.AddNewlyCreatedDBObject(layer, true);
            return id;
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

        private sealed class RoadSheetJob
        {
            internal RoadInfo Road;
            internal RoadSheetSegment Segment;
        }

        private sealed class RoadInfo
        {
            internal ObjectId AlignmentId;
            internal string Name;
            internal double StartStation;
            internal double EndStation;
            internal List<ObjectId> ProfileViewIds;
        }

        private sealed class PlanPaperSlot
        {
            internal double Left;
            internal double Width;
            internal Point3d Center;
        }

        private sealed class PaperPair
        {
            internal double Left;
            internal double Top;
            internal double Width;
            internal double PlanLeft;
            internal double PlanWidth;
            internal double ProfileLeft;
            internal double ProfileWidth;
            internal double PlanHeight;
            internal double ProfileHeight;
            internal Point3d PlanCenter;
            internal Point3d ProfileCenter;
        }
    }
}

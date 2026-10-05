using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivilProfile = Autodesk.Civil.DatabaseServices.Profile;
using ProfilePVI = Autodesk.Civil.DatabaseServices.ProfilePVI;

[assembly: CommandClass(typeof(CETools.Civil3D.ProfileViewBatchCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Version-tolerant batch profile-view cleanup. The workflow applies selected
    /// profile-view and band-set styles, requests automatic station/elevation
    /// ranges where supported, rebuilds available views and reports unsupported
    /// API operations instead of silently claiming success.
    /// </summary>
    public sealed class ProfileViewBatchCommands
    {
        [CommandMethod("CE_TOOLS", "CE_PROFILEVIEWBATCHTOOLS", CommandFlags.Modal)]
        public void ProfileViewBatchTools()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            var window = new ProfileViewBatchLauncherWindow();
            AcApplication.ShowModalWindow(window);
            if (string.IsNullOrWhiteSpace(window.Command)) return;
            document.SendStringToExecute(window.Command, true, false, true);
        }

        [CommandMethod("CE_TOOLS", "CE_PROFILEVIEWBATCH", CommandFlags.Modal | CommandFlags.Redraw | CommandFlags.UsePickSet)]
        public void BatchCleanup()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            List<ProfileViewItem> views = ReadProfileViews(document);
            if (views.Count == 0)
            {
                document.Editor.WriteMessage(
                    "\nCE_PROFILEVIEWBATCH: no accessible Civil 3D profile views were found.");
                return;
            }

            ProfileViewStyleCatalogue catalogue = ReadStyleCatalogue(document);
            var window = new ProfileViewBatchWindow(views, catalogue);
            AcApplication.ShowModalWindow(window);
            if (!window.Accepted)
            {
                document.Editor.WriteMessage("\nCE_PROFILEVIEWBATCH cancelled.");
                return;
            }
            List<ProfileViewItem> selected = window.SelectedViews;
            if (selected.Count == 0)
            {
                document.Editor.WriteMessage(
                    "\nCE_PROFILEVIEWBATCH cancelled. No profile views were selected.");
                return;
            }

            var review = new List<KeyValuePair<string, string>>
            {
                Pair("Selected profile views", selected.Count.ToString(CultureInfo.InvariantCulture)),
                Pair("Profile-view style", window.ApplyProfileViewStyle ? window.SelectedProfileViewStyle.DisplayName : "<Keep current>"),
                Pair("Band-set style", window.ApplyBandSetStyle ? window.SelectedBandSetStyle.DisplayName : "<Keep current>"),
                Pair("Automatic station/elevation fit", window.AutoFit ? "Yes" : "No"),
                Pair("Rebuild/update views", window.Rebuild ? "Yes" : "No"),
                Pair("Run annotation overlap cleanup", window.RunOverlapCleanup ? "Yes" : "No"),
                Pair("API boundary", "Unsupported properties/methods are reported and skipped")
            };
            if (!PopupTablePresenter.ShowReview(
                    "CE Tools - Batch Profile View Cleanup",
                    "The selected operations are applied in one transaction where possible. Civil 3D version-specific operations that are unavailable are counted as unsupported.",
                    review,
                    "Apply Cleanup"))
            {
                document.Editor.WriteMessage("\nCE_PROFILEVIEWBATCH cancelled.");
                return;
            }

            ProfileViewBatchResult result = ApplyBatch(
                document,
                selected,
                window.ApplyProfileViewStyle ? window.SelectedProfileViewStyle : null,
                window.ApplyBandSetStyle ? window.SelectedBandSetStyle : null,
                window.AutoFit,
                window.Rebuild);
            document.Editor.Regen();
            ShowResult(document, result, "CE Tools - Profile View Batch Result");
            if (window.RunOverlapCleanup)
            {
                document.Editor.WriteMessage(
                    "\nSelect the profile-view labels/notes requiring overlap cleanup.");
                document.SendStringToExecute("CE_OVERLAPFIX ", true, false, true);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_PROFILEVIEWFITALL", CommandFlags.Modal | CommandFlags.Redraw | CommandFlags.UsePickSet)]
        public void FitAllProfileViews()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            // Existing scope prompt already supports All or multiple selected
            // profile views. The saved margin settings below control the vertical
            // fit needed for Civil 3D high/low and geometry labels to remain clear.
            List<ProfileViewItem> views = PromptScope(
                document,
                ReadProfileViews(document));
            if (views.Count == 0) return;

            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Fit Profile Views and Label Clearance",
                "Fit all or multiple selected profile views, centre the displayed profile data, and reserve grid-row clearance so top/bottom labels do not touch or clip against the frame.");
            settings.AddPositiveDouble(
                "RowHeight",
                "01 Vertical fit",
                "Profile-view row height",
                1.0,
                "Elevation represented by one horizontal profile-view row. Use the drawing's normal grid-row interval.");
            settings.AddPositiveInteger(
                "BottomRows",
                "01 Vertical fit",
                "Rows below lowest profile",
                1,
                "Clear rows between the lowest displayed profile and the bottom frame.");
            settings.AddPositiveInteger(
                "ProfileToTopLabelRows",
                "01 Vertical fit",
                "Rows from highest profile to top labels",
                1,
                "Clear rows reserved between the highest displayed profile and its top labels.");
            settings.AddPositiveInteger(
                "TopLabelToFrameRows",
                "01 Vertical fit",
                "Rows from top labels to top frame",
                1,
                "Clear rows reserved above the top text before the profile-view frame.");
            if (!DisciplineWorkflowDialogs.EditSettings(settings))
                return;

            double rowHeight = Math.Max(
                0.001,
                settings.Double("RowHeight", 1.0));
            int bottomRows = Math.Max(
                1,
                settings.Integer("BottomRows", 1));
            int profileToTopRows = Math.Max(
                1,
                settings.Integer("ProfileToTopLabelRows", 1));
            int topLabelToFrameRows = Math.Max(
                1,
                settings.Integer("TopLabelToFrameRows", 1));

            ProfileViewBatchResult result = ApplyBatch(
                document,
                views,
                null,
                null,
                true,
                true);
            result.ViewsLabelMarginFit += FitProfileViewElevationRanges(
                document,
                views,
                rowHeight,
                bottomRows,
                profileToTopRows,
                topLabelToFrameRows);
            document.Editor.Regen();
            ShowResult(
                document,
                result,
                "CE Tools - Profile View Fit Result");
        }

        [CommandMethod("CE_TOOLS", "CE_PROFILEVIEWARRANGE", CommandFlags.Modal | CommandFlags.Redraw | CommandFlags.UsePickSet)]
        public void ArrangeProfileViews()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            List<ProfileViewItem> views = PromptScope(
                document,
                ReadProfileViews(document));
            if (views.Count == 0) return;

            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Arrange Multiple Profile Views",
                "Sort and space multiple selected Civil 3D profile views without changing their profile, band or label data. Horizontal, vertical and grid layouts use the actual displayed view extents.");
            settings.AddChoice(
                "Layout",
                "01 Layout",
                "Arrangement",
                "Grid",
                "Choose the placement pattern for the selected profile views.",
                new[] { "Horizontal", "Vertical", "Grid" });
            settings.AddChoice(
                "Sort",
                "01 Layout",
                "Sort order",
                "Branch / alignment order",
                "Branch/alignment order places Branch-1, Branch-2, Branch-3, etc. in sequence. Current position orders the existing views top-to-bottom then left-to-right.",
                new[] { "Branch / alignment order", "Profile view name", "Current position" });
            settings.AddPositiveDouble(
                "HorizontalSpacing",
                "02 Spacing",
                "Horizontal spacing (drawing units)",
                20.0,
                "Clear gap between adjacent profile-view extents.");
            settings.AddPositiveDouble(
                "VerticalSpacing",
                "02 Spacing",
                "Vertical spacing (drawing units)",
                20.0,
                "Clear gap between adjacent profile-view extents.");
            settings.AddPositiveInteger(
                "GridColumns",
                "03 Grid",
                "Grid columns",
                3,
                "Number of columns used only for Grid arrangement.");
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;

            string layout = settings.Text("Layout");
            string sort = settings.Text("Sort");
            double horizontalSpacing =
                settings.Double("HorizontalSpacing", 20.0);
            double verticalSpacing =
                settings.Double("VerticalSpacing", 20.0);
            int gridColumns =
                Math.Max(1, settings.Integer("GridColumns", 3));

            int moved = ArrangeViews(
                document,
                views,
                layout,
                sort,
                horizontalSpacing,
                verticalSpacing,
                gridColumns);

            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_PROFILEVIEWARRANGE complete. Views arranged={0}; layout={1}; horizontal gap={2:0.###}; vertical gap={3:0.###}.",
                moved,
                layout,
                horizontalSpacing,
                verticalSpacing);
        }

        [CommandMethod("CE_TOOLS", "CE_PROFILEVIEWBATCHINFO", CommandFlags.Modal | CommandFlags.Redraw)]
        public void ProfileViewBatchInformation()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            List<ProfileViewItem> views = ReadProfileViews(document);
            if (views.Count == 0)
            {
                document.Editor.WriteMessage(
                    "\nCE_PROFILEVIEWBATCHINFO: no accessible profile views were found.");
                return;
            }
            var rows = new List<IList<string>>();
            foreach (ProfileViewItem view in views)
            {
                rows.Add(new List<string>
                {
                    view.Name,
                    view.AlignmentName,
                    view.StyleName,
                    view.BandSetName,
                    view.StationRange,
                    view.ElevationRange,
                    view.OutOfDate
                });
            }
            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - Profile View Batch Information",
                "Accessible profile views, current styles, ranges and update state.",
                new[]
                {
                    "PROFILE VIEW",
                    "ALIGNMENT",
                    "STYLE",
                    "BAND SET",
                    "STATION RANGE",
                    "ELEVATION RANGE",
                    "STATE"
                },
                rows,
                "CE TOOLS PROFILE VIEW BATCH INFORMATION");
        }

        private static int ArrangeViews(
            Document document,
            IList<ProfileViewItem> views,
            string layout,
            string sort,
            double horizontalSpacing,
            double verticalSpacing,
            int gridColumns)
        {
            if (document == null ||
                views == null ||
                views.Count == 0)
                return 0;

            var items = new List<ProfileViewPlacement>();
            using (Transaction read =
                document.Database.TransactionManager.StartTransaction())
            {
                foreach (ProfileViewItem view in views)
                {
                    Entity entity = null;
                    try
                    {
                        entity = read.GetObject(
                            view.ObjectId,
                            OpenMode.ForRead,
                            false) as Entity;
                    }
                    catch { }
                    if (entity == null) continue;

                    Extents3d extents;
                    try { extents = entity.GeometricExtents; }
                    catch { continue; }

                    items.Add(new ProfileViewPlacement
                    {
                        Item = view,
                        Min = extents.MinPoint,
                        Max = extents.MaxPoint
                    });
                }
            }

            if (items.Count == 0) return 0;

            if (string.Equals(
                    sort,
                    "Current position",
                    StringComparison.OrdinalIgnoreCase))
            {
                items = items
                    .OrderByDescending(item => item.Max.Y)
                    .ThenBy(item => item.Min.X)
                    .ToList();
            }
            else if (string.Equals(
                sort,
                "Profile view name",
                StringComparison.OrdinalIgnoreCase))
            {
                items = items
                    .OrderBy(item => item.Item.Name,
                        StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }
            else
            {
                items = items
                    .OrderBy(item => BranchSortNumber(
                        item.Item.AlignmentName))
                    .ThenBy(item => item.Item.AlignmentName,
                        StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(item => item.Item.Name,
                        StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }

            ProfileViewPlacement anchor = items[0];
            double anchorLeft = anchor.Min.X;
            double anchorTop = anchor.Max.Y;
            double maxWidth = items.Max(item =>
                Math.Max(0.001, item.Max.X - item.Min.X));
            double maxHeight = items.Max(item =>
                Math.Max(0.001, item.Max.Y - item.Min.Y));

            int moved = 0;
            using (DocumentLock documentLock =
                document.LockDocument())
            using (Transaction transaction =
                document.Database.TransactionManager.StartTransaction())
            {
                double horizontalCursor = anchorLeft;
                double verticalCursorTop = anchorTop;

                for (int index = 0; index < items.Count; index++)
                {
                    ProfileViewPlacement item = items[index];
                    double targetLeft;
                    double targetTop;

                    if (string.Equals(
                            layout,
                            "Horizontal",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        targetLeft = horizontalCursor;
                        targetTop = anchorTop;
                        horizontalCursor +=
                            (item.Max.X - item.Min.X) +
                            horizontalSpacing;
                    }
                    else if (string.Equals(
                        layout,
                        "Vertical",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        targetLeft = anchorLeft;
                        targetTop = verticalCursorTop;
                        verticalCursorTop -=
                            (item.Max.Y - item.Min.Y) +
                            verticalSpacing;
                    }
                    else
                    {
                        int column = index % gridColumns;
                        int row = index / gridColumns;
                        targetLeft =
                            anchorLeft +
                            column * (maxWidth + horizontalSpacing);
                        targetTop =
                            anchorTop -
                            row * (maxHeight + verticalSpacing);
                    }

                    double dx = targetLeft - item.Min.X;
                    double dy = targetTop - item.Max.Y;
                    if (Math.Abs(dx) <= 1e-9 &&
                        Math.Abs(dy) <= 1e-9)
                        continue;

                    Entity entity = null;
                    try
                    {
                        entity = transaction.GetObject(
                            item.Item.ObjectId,
                            OpenMode.ForWrite,
                            false) as Entity;
                    }
                    catch { }
                    if (entity == null) continue;

                    entity.TransformBy(
                        Matrix3d.Displacement(
                            new Vector3d(dx, dy, 0.0)));
                    moved++;
                }

                transaction.Commit();
            }

            return moved;
        }

        private static int BranchSortNumber(string alignmentName)
        {
            if (string.IsNullOrWhiteSpace(alignmentName))
                return int.MaxValue;

            string value = alignmentName.Trim();
            int dash = value.IndexOf('-');
            string candidate = dash >= 0
                ? value.Substring(dash + 1)
                : value;
            int number;
            return int.TryParse(
                new string(candidate
                    .TakeWhile(char.IsDigit)
                    .ToArray()),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out number)
                ? number
                : int.MaxValue;
        }

        private static ProfileViewBatchResult ApplyBatch(
            Document document,
            IList<ProfileViewItem> views,
            ProfileViewStyleChoice profileViewStyle,
            ProfileViewStyleChoice bandSetStyle,
            bool autoFit,
            bool rebuild)
        {
            var result = new ProfileViewBatchResult();
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ProfileViewItem item in views)
                {
                    DBObject view;
                    try
                    {
                        view = transaction.GetObject(
                            item.ObjectId,
                            OpenMode.ForWrite,
                            false);
                    }
                    catch
                    {
                        result.Failed++;
                        continue;
                    }
                    if (view == null)
                    {
                        result.Failed++;
                        continue;
                    }

                    bool changed = false;
                    if (profileViewStyle != null)
                    {
                        if (TrySetObjectIdProperty(view, "StyleId", profileViewStyle.ObjectId) ||
                            TrySetStringProperty(view, "StyleName", profileViewStyle.DisplayName))
                        {
                            result.ProfileStylesApplied++;
                            changed = true;
                        }
                        else result.Unsupported++;
                    }
                    if (bandSetStyle != null)
                    {
                        if (TryApplyBandSet(view, bandSetStyle))
                        {
                            result.BandSetsApplied++;
                            changed = true;
                        }
                        else result.Unsupported++;
                    }
                    if (autoFit)
                    {
                        int fit = 0;
                        if (TrySetAutomaticEnum(view, "StationRangeMode")) fit++;
                        if (TrySetAutomaticEnum(view, "ElevationRangeMode")) fit++;
                        if (TrySetBooleanProperty(view, "AutomaticStationRange", true)) fit++;
                        if (TrySetBooleanProperty(view, "AutomaticElevationRange", true)) fit++;
                        if (fit > 0)
                        {
                            result.ViewsAutoFit++;
                            changed = true;
                        }
                        else result.Unsupported++;
                    }
                    if (rebuild)
                    {
                        if (TryInvokeNoArguments(view, "Rebuild") ||
                            TryInvokeNoArguments(view, "Update") ||
                            TryInvokeNoArguments(view, "UpdateDisplay"))
                        {
                            result.ViewsRebuilt++;
                            changed = true;
                        }
                        else result.Unsupported++;
                    }
                    if (changed) result.ViewsChanged++;
                }
                transaction.Commit();
            }
            return result;
        }

        private static int FitProfileViewElevationRanges(
            Document document,
            IList<ProfileViewItem> views,
            double rowHeight,
            int bottomRows,
            int profileToTopRows,
            int topLabelToFrameRows)
        {
            if (document == null || views == null || views.Count == 0)
                return 0;

            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (civilDocument == null)
                return 0;

            int fitted = 0;
            using (Transaction transaction =
                document.Database.TransactionManager.StartTransaction())
            {
                foreach (ProfileViewItem item in views)
                {
                    DBObject rawView = null;
                    try
                    {
                        rawView = transaction.GetObject(
                            item.ObjectId,
                            OpenMode.ForWrite,
                            false);
                    }
                    catch { }
                    if (rawView == null) continue;

                    ObjectId alignmentId = FindAlignmentForProfileView(
                        civilDocument,
                        transaction,
                        item.ObjectId);
                    if (alignmentId.IsNull) continue;

                    CivilAlignment alignment = transaction.GetObject(
                        alignmentId,
                        OpenMode.ForRead,
                        false) as CivilAlignment;
                    if (alignment == null) continue;

                    double? startValue = ReadDoubleProperty(
                        rawView,
                        "StationStart");
                    double? endValue = ReadDoubleProperty(
                        rawView,
                        "StationEnd");
                    double stationStart = startValue ??
                        (ReadDoubleProperty(
                            alignment,
                            "StartingStation") ?? 0.0);
                    double stationEnd = endValue ??
                        (ReadDoubleProperty(
                            alignment,
                            "EndingStation") ?? stationStart);
                    if (stationEnd < stationStart)
                    {
                        double swap = stationStart;
                        stationStart = stationEnd;
                        stationEnd = swap;
                    }
                    if (stationEnd - stationStart <= 1e-9)
                        continue;

                    double minimum = double.PositiveInfinity;
                    double maximum = double.NegativeInfinity;
                    foreach (ObjectId profileId in alignment.GetProfileIds())
                    {
                        CivilProfile profile = null;
                        try
                        {
                            profile = transaction.GetObject(
                                profileId,
                                OpenMode.ForRead,
                                false) as CivilProfile;
                        }
                        catch { }
                        if (profile == null ||
                            (profile.Name ?? string.Empty).StartsWith(
                                "CE_BAND_SRC_",
                                StringComparison.OrdinalIgnoreCase))
                            continue;

                        double profileStart =
                            ReadDoubleProperty(
                                profile,
                                "StartingStation") ?? stationStart;
                        double profileEnd =
                            ReadDoubleProperty(
                                profile,
                                "EndingStation") ?? stationEnd;
                        double first = Math.Max(
                            stationStart,
                            Math.Min(profileStart, profileEnd));
                        double last = Math.Min(
                            stationEnd,
                            Math.Max(profileStart, profileEnd));
                        if (last < first) continue;

                        int samples = Math.Min(
                            400,
                            Math.Max(
                                20,
                                (int)Math.Ceiling(
                                    (last - first) / 5.0)));
                        for (int index = 0; index <= samples; index++)
                        {
                            double station = samples <= 0
                                ? first
                                : first +
                                  (last - first) *
                                  index / samples;
                            try
                            {
                                double elevation =
                                    profile.ElevationAt(station);
                                if (double.IsNaN(elevation) ||
                                    double.IsInfinity(elevation))
                                    continue;
                                minimum = Math.Min(minimum, elevation);
                                maximum = Math.Max(maximum, elevation);
                            }
                            catch { }
                        }

                        try
                        {
                            foreach (ProfilePVI pvi in profile.PVIs)
                            {
                                if (pvi == null ||
                                    pvi.Station < first - 1e-6 ||
                                    pvi.Station > last + 1e-6)
                                    continue;
                                minimum = Math.Min(
                                    minimum,
                                    pvi.Elevation);
                                maximum = Math.Max(
                                    maximum,
                                    pvi.Elevation);
                            }
                        }
                        catch { }
                    }

                    if (double.IsInfinity(minimum) ||
                        double.IsInfinity(maximum) ||
                        double.IsNaN(minimum) ||
                        double.IsNaN(maximum))
                        continue;

                    // Snap the data envelope to whole grid rows. Reserve one row
                    // below the lowest profile, one row from the highest profile
                    // to top labels, and one row from top text to the top frame by
                    // default. This is the requested drawing presentation.
                    double frameMinimum =
                        Math.Floor(minimum / rowHeight) *
                        rowHeight -
                        bottomRows * rowHeight;
                    double frameMaximum =
                        Math.Ceiling(maximum / rowHeight) *
                        rowHeight +
                        (profileToTopRows +
                         topLabelToFrameRows) *
                        rowHeight;
                    if (frameMaximum <= frameMinimum + rowHeight)
                        frameMaximum =
                            frameMinimum +
                            rowHeight * 2.0;

                    bool modeSet =
                        TrySetEnumByNames(
                            rawView,
                            "ElevationRangeMode",
                            new[]
                            {
                                "UserSpecified",
                                "User",
                                "Manual",
                                "Specified"
                            }) ||
                        TrySetBooleanProperty(
                            rawView,
                            "AutomaticElevationRange",
                            false);
                    bool minSet =
                        TrySetDoubleProperty(
                            rawView,
                            "ElevationMin",
                            frameMinimum) ||
                        TrySetDoubleProperty(
                            rawView,
                            "ElevationMinimum",
                            frameMinimum);
                    bool maxSet =
                        TrySetDoubleProperty(
                            rawView,
                            "ElevationMax",
                            frameMaximum) ||
                        TrySetDoubleProperty(
                            rawView,
                            "ElevationMaximum",
                            frameMaximum);

                    if (minSet && maxSet)
                    {
                        // Keep station fitting automatic so the profile is centred
                        // left/right while the vertical range uses the explicit
                        // label-clearance envelope above.
                        TrySetAutomaticEnum(
                            rawView,
                            "StationRangeMode");
                        TrySetBooleanProperty(
                            rawView,
                            "AutomaticStationRange",
                            true);
                        TryInvokeNoArguments(rawView, "Update");
                        TryInvokeNoArguments(rawView, "Rebuild");
                        fitted++;
                    }
                    else if (modeSet)
                    {
                        // Do not report a fit unless both elevation limits were
                        // accepted by the host Civil 3D build.
                    }
                }

                transaction.Commit();
            }
            return fitted;
        }

        private static ObjectId FindAlignmentForProfileView(
            CivilDocument civilDocument,
            Transaction transaction,
            ObjectId profileViewId)
        {
            if (civilDocument == null ||
                transaction == null ||
                profileViewId.IsNull)
                return ObjectId.Null;

            foreach (ObjectId alignmentId in
                civilDocument.GetAlignmentIds())
            {
                DBObject alignment = null;
                try
                {
                    alignment = transaction.GetObject(
                        alignmentId,
                        OpenMode.ForRead,
                        false);
                }
                catch { }
                if (alignment == null) continue;

                if (ReadObjectIds(
                        alignment,
                        "GetProfileViewIds")
                    .Any(id => id == profileViewId))
                    return alignmentId;
            }
            return ObjectId.Null;
        }

        private static bool TrySetDoubleProperty(
            object value,
            string name,
            double setting)
        {
            PropertyInfo property =
                FindWritableProperty(value, name);
            if (property == null) return false;
            try
            {
                object converted =
                    Convert.ChangeType(
                        setting,
                        property.PropertyType,
                        CultureInfo.InvariantCulture);
                property.SetValue(value, converted, null);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TrySetEnumByNames(
            object value,
            string propertyName,
            IEnumerable<string> names)
        {
            PropertyInfo property =
                FindWritableProperty(
                    value,
                    propertyName);
            if (property == null ||
                !property.PropertyType.IsEnum)
                return false;

            foreach (string requested in
                names ?? Enumerable.Empty<string>())
            {
                string actual = Enum.GetNames(
                    property.PropertyType)
                    .FirstOrDefault(name =>
                        string.Equals(
                            name,
                            requested,
                            StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrWhiteSpace(actual))
                    continue;
                try
                {
                    property.SetValue(
                        value,
                        Enum.Parse(
                            property.PropertyType,
                            actual),
                        null);
                    return true;
                }
                catch { }
            }
            return false;
        }

        private static bool TryApplyBandSet(
            object profileView,
            ProfileViewStyleChoice style)
        {
            if (profileView == null || style == null) return false;
            if (TrySetObjectIdProperty(profileView, "BandSetStyleId", style.ObjectId) ||
                TrySetObjectIdProperty(profileView, "ProfileViewBandSetStyleId", style.ObjectId) ||
                TrySetStringProperty(profileView, "BandSetStyleName", style.DisplayName))
                return true;

            object bands = ReadProperty(profileView, "Bands");
            if (bands == null) return false;
            foreach (string methodName in new[]
            {
                "ImportBandSetStyle",
                "ApplyBandSetStyle",
                "SetBandSetStyle"
            })
            {
                if (TryInvokeObjectId(bands, methodName, style.ObjectId)) return true;
            }
            return false;
        }

        private static bool TrySetAutomaticEnum(object value, string propertyName)
        {
            PropertyInfo property = FindWritableProperty(value, propertyName);
            if (property == null || !property.PropertyType.IsEnum) return false;
            string automaticName = Enum.GetNames(property.PropertyType)
                .FirstOrDefault(name =>
                    string.Equals(name, "Automatic", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "Auto", StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(automaticName)) return false;
            try
            {
                property.SetValue(value, Enum.Parse(property.PropertyType, automaticName), null);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TrySetBooleanProperty(object value, string name, bool setting)
        {
            PropertyInfo property = FindWritableProperty(value, name);
            if (property == null || property.PropertyType != typeof(bool)) return false;
            try
            {
                property.SetValue(value, setting, null);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TrySetObjectIdProperty(object value, string name, ObjectId setting)
        {
            if (setting.IsNull) return false;
            PropertyInfo property = FindWritableProperty(value, name);
            if (property == null || property.PropertyType != typeof(ObjectId)) return false;
            try
            {
                property.SetValue(value, setting, null);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TrySetStringProperty(object value, string name, string setting)
        {
            PropertyInfo property = FindWritableProperty(value, name);
            if (property == null || property.PropertyType != typeof(string)) return false;
            try
            {
                property.SetValue(value, setting, null);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static PropertyInfo FindWritableProperty(object value, string name)
        {
            return value == null
                ? null
                : value.GetType().GetProperty(
                    name,
                    BindingFlags.Public | BindingFlags.Instance);
        }

        private static bool TryInvokeNoArguments(object value, string methodName)
        {
            if (value == null) return false;
            try
            {
                MethodInfo method = value.GetType().GetMethod(
                    methodName,
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    Type.EmptyTypes,
                    null);
                if (method == null) return false;
                method.Invoke(value, null);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryInvokeObjectId(object value, string methodName, ObjectId objectId)
        {
            if (value == null || objectId.IsNull) return false;
            try
            {
                MethodInfo method = value.GetType().GetMethod(
                    methodName,
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new[] { typeof(ObjectId) },
                    null);
                if (method == null) return false;
                method.Invoke(value, new object[] { objectId });
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static List<ProfileViewItem> PromptScope(
            Document document,
            IList<ProfileViewItem> all)
        {
            if (all == null || all.Count == 0) return new List<ProfileViewItem>();
            var options = new PromptKeywordOptions(
                "\nProfile view scope [All/Select] <All>: ")
            {
                AllowNone = true
            };
            options.Keywords.Add("All");
            options.Keywords.Add("Select");
            PromptResult result = document.Editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel) return new List<ProfileViewItem>();
            if (result.Status != PromptStatus.OK ||
                string.Equals(result.StringResult, "All", StringComparison.OrdinalIgnoreCase))
                return all.ToList();

            PromptSelectionResult selection = document.Editor.GetSelection(new PromptSelectionOptions
            {
                MessageForAdding = "\nSelect Civil 3D profile views: ",
                AllowDuplicates = false,
                RejectObjectsFromNonCurrentSpace = true
            });
            if (selection.Status != PromptStatus.OK) return new List<ProfileViewItem>();
            var selectedIds = new HashSet<ObjectId>(selection.Value.GetObjectIds());
            return all.Where(item => selectedIds.Contains(item.ObjectId)).ToList();
        }

        private static List<ProfileViewItem> ReadProfileViews(Document document)
        {
            var result = new List<ProfileViewItem>();
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (civilDocument == null) return result;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId alignmentId in civilDocument.GetAlignmentIds())
                {
                    DBObject alignment = transaction.GetObject(alignmentId, OpenMode.ForRead, false);
                    string alignmentName = ReadStringProperty(alignment, "Name");
                    foreach (ObjectId viewId in ReadObjectIds(alignment, "GetProfileViewIds"))
                    {
                        DBObject view = transaction.GetObject(viewId, OpenMode.ForRead, false);
                        if (view == null) continue;
                        result.Add(new ProfileViewItem(
                            viewId,
                            ReadStringProperty(view, "Name"),
                            alignmentName,
                            ReadStringProperty(view, "StyleName"),
                            FirstNonBlank(
                                ReadStringProperty(view, "BandSetStyleName"),
                                ReadNestedString(view, "Bands", "BandSetStyleName")),
                            FormatRange(view, "StationStart", "StationEnd"),
                            FormatRange(view, "ElevationMin", "ElevationMax"),
                            ReadBoolProperty(view, "IsOutOfDate") ? "Out of date" : "Current"));
                    }
                }
            }
            return result
                .OrderBy(item => item.AlignmentName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private static ProfileViewStyleCatalogue ReadStyleCatalogue(Document document)
        {
            var catalogue = new ProfileViewStyleCatalogue();
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (civilDocument == null) return catalogue;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                // Civil 3D 2023 exposes these collections as StyleCollectionBase
                // instances. Reading them only through reflection can miss their
                // explicit enumerator and leaves the WPF dropdowns empty. Prefer
                // the strongly typed API, then retain the reflective fallbacks
                // for minor-version compatibility.
                AddStyles(
                    civilDocument.Styles.ProfileViewStyles,
                    transaction,
                    catalogue.ProfileViewStyles);
                AddStyles(
                    civilDocument.Styles.ProfileViewBandSetStyles,
                    transaction,
                    catalogue.BandSetStyles);

                object styles = ReadProperty(civilDocument, "Styles");
                AddStyles(
                    ReadProperty(styles, "ProfileViewStyles"),
                    transaction,
                    catalogue.ProfileViewStyles);
                object bandSets = ReadProperty(styles, "ProfileViewBandSetStyles");
                if (bandSets == null)
                {
                    object root = ReadProperty(styles, "BandSetStyles");
                    bandSets = ReadProperty(root, "ProfileViewBandSetStyles");
                }
                AddStyles(
                    bandSets,
                    transaction,
                    catalogue.BandSetStyles);
                AddCurrentViewStyles(
                    document,
                    transaction,
                    catalogue);
            }
            return catalogue;
        }

        private static void AddStyles(
            object collection,
            Transaction transaction,
            ICollection<ProfileViewStyleChoice> target)
        {
            if (collection == null) return;
            IEnumerable enumerable = collection as IEnumerable;
            if (enumerable != null)
            {
                foreach (object item in enumerable)
                    AddStyleChoice(item, transaction, target);
                return;
            }

            // Some Civil 3D style collections expose an explicit enumerator that
            // is not visible through System.Collections.IEnumerable.
            try
            {
                MethodInfo getEnumerator = collection.GetType().GetMethod(
                    "GetEnumerator",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null,
                    Type.EmptyTypes,
                    null);
                object iterator = getEnumerator == null
                    ? null
                    : getEnumerator.Invoke(collection, null);
                MethodInfo moveNext = iterator == null
                    ? null
                    : iterator.GetType().GetMethod(
                        "MoveNext",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                PropertyInfo current = iterator == null
                    ? null
                    : iterator.GetType().GetProperty(
                        "Current",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (moveNext != null && current != null)
                {
                    while ((bool)moveNext.Invoke(iterator, null))
                        AddStyleChoice(current.GetValue(iterator, null), transaction, target);
                    if (target.Count > 0) return;
                }
            }
            catch
            {
                // Continue to the Count/indexer fallback.
            }

            // Other Civil 3D releases expose Count and an ObjectId indexer
            // without implementing either enumerable interface.
            object countValue = ReadProperty(collection, "Count");
            int count;
            if (countValue == null ||
                !int.TryParse(
                    Convert.ToString(countValue, CultureInfo.InvariantCulture),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out count))
                return;
            PropertyInfo indexer = collection.GetType()
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(property =>
                    property.GetIndexParameters().Length == 1);
            if (indexer == null || indexer.GetGetMethod() == null) return;
            Type indexType = indexer.GetIndexParameters()[0].ParameterType;
            for (int index = 0; index < count; index++)
            {
                try
                {
                    AddStyleChoice(
                        indexer.GetValue(
                            collection,
                            new[]
                            {
                                Convert.ChangeType(
                                    index,
                                    indexType,
                                    CultureInfo.InvariantCulture)
                            }),
                        transaction,
                        target);
                }
                catch
                {
                    // Continue with the styles that are accessible.
                }
            }
        }

        private static void AddStyleChoice(
            object value,
            Transaction transaction,
            ICollection<ProfileViewStyleChoice> target)
        {
            ObjectId id;
            if (value is ObjectId)
                id = (ObjectId)value;
            else if (value is DBObject)
                id = ((DBObject)value).ObjectId;
            else
                return;
            if (id.IsNull || target.Any(choice => choice.ObjectId == id)) return;
            DBObject style = transaction.GetObject(id, OpenMode.ForRead, false);
            string name = ReadStringProperty(style, "Name");
            if (!string.IsNullOrWhiteSpace(name))
                target.Add(new ProfileViewStyleChoice(id, name));
        }

        private static void AddCurrentViewStyles(
            Document document,
            Transaction transaction,
            ProfileViewStyleCatalogue catalogue)
        {
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (civilDocument == null) return;
            foreach (ObjectId alignmentId in civilDocument.GetAlignmentIds())
            {
                DBObject alignment = transaction.GetObject(
                    alignmentId,
                    OpenMode.ForRead,
                    false);
                foreach (ObjectId viewId in ReadObjectIds(alignment, "GetProfileViewIds"))
                {
                    DBObject view = transaction.GetObject(
                        viewId,
                        OpenMode.ForRead,
                        false);
                    AddStyleChoice(
                        ReadProperty(view, "StyleId"),
                        transaction,
                        catalogue.ProfileViewStyles);
                    foreach (string propertyName in new[]
                    {
                        "BandSetStyleId",
                        "ProfileViewBandSetStyleId"
                    })
                    {
                        AddStyleChoice(
                            ReadProperty(view, propertyName),
                            transaction,
                            catalogue.BandSetStyles);
                    }
                }
            }
        }

        private static IEnumerable<ObjectId> ReadObjectIds(object value, string methodName)
        {
            if (value == null) return Enumerable.Empty<ObjectId>();
            try
            {
                MethodInfo method = value.GetType().GetMethod(
                    methodName,
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    Type.EmptyTypes,
                    null);
                IEnumerable enumerable = method == null
                    ? null
                    : method.Invoke(value, null) as IEnumerable;
                if (enumerable == null) return Enumerable.Empty<ObjectId>();
                var result = new List<ObjectId>();
                foreach (object item in enumerable)
                    if (item is ObjectId) result.Add((ObjectId)item);
                return result;
            }
            catch
            {
                return Enumerable.Empty<ObjectId>();
            }
        }

        private static string FormatRange(object value, string minimumName, string maximumName)
        {
            double? minimum = ReadDoubleProperty(value, minimumName);
            double? maximum = ReadDoubleProperty(value, maximumName);
            return minimum.HasValue && maximum.HasValue
                ? minimum.Value.ToString("N3", CultureInfo.CurrentCulture) + " - " +
                  maximum.Value.ToString("N3", CultureInfo.CurrentCulture)
                : "<Unavailable>";
        }

        private static double? ReadDoubleProperty(object value, string name)
        {
            object raw = ReadProperty(value, name);
            if (raw == null) return null;
            try
            {
                double result = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
                return double.IsNaN(result) || double.IsInfinity(result)
                    ? (double?)null
                    : result;
            }
            catch
            {
                return null;
            }
        }

        private static bool ReadBoolProperty(object value, string name)
        {
            object raw = ReadProperty(value, name);
            return raw is bool && (bool)raw;
        }

        private static string ReadStringProperty(object value, string name)
        {
            return Convert.ToString(ReadProperty(value, name), CultureInfo.CurrentCulture) ?? string.Empty;
        }

        private static string ReadNestedString(object value, string parent, string child)
        {
            return ReadStringProperty(ReadProperty(value, parent), child);
        }

        private static object ReadProperty(object value, string name)
        {
            if (value == null) return null;
            try
            {
                PropertyInfo property = value.GetType().GetProperty(
                    name,
                    BindingFlags.Public | BindingFlags.Instance);
                MethodInfo getter = property == null ? null : property.GetGetMethod();
                return getter == null || property.GetIndexParameters().Length != 0
                    ? null
                    : getter.Invoke(value, null);
            }
            catch
            {
                return null;
            }
        }

        private static string FirstNonBlank(params string[] values)
        {
            foreach (string value in values)
                if (!string.IsNullOrWhiteSpace(value)) return value;
            return string.Empty;
        }

        private static void ShowResult(
            Document document,
            ProfileViewBatchResult result,
            string title)
        {
            var rows = new List<KeyValuePair<string, string>>
            {
                Pair("Profile views changed", result.ViewsChanged.ToString(CultureInfo.InvariantCulture)),
                Pair("Profile-view styles applied", result.ProfileStylesApplied.ToString(CultureInfo.InvariantCulture)),
                Pair("Band sets applied", result.BandSetsApplied.ToString(CultureInfo.InvariantCulture)),
                Pair("Views set to automatic fit", result.ViewsAutoFit.ToString(CultureInfo.InvariantCulture)),
                Pair("Views fitted with label-row clearance", result.ViewsLabelMarginFit.ToString(CultureInfo.InvariantCulture)),
                Pair("Views rebuilt/updated", result.ViewsRebuilt.ToString(CultureInfo.InvariantCulture)),
                Pair("Unsupported API operations", result.Unsupported.ToString(CultureInfo.InvariantCulture)),
                Pair("Failed views", result.Failed.ToString(CultureInfo.InvariantCulture))
            };
            PopupTablePresenter.ShowReportAndOfferTable(
                document,
                title,
                "Unsupported operations were not reported as successful. Review the result before drawing issue.",
                rows,
                "CE TOOLS PROFILE VIEW BATCH RESULT");
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

    internal sealed class ProfileViewItem
    {
        public ProfileViewItem(
            ObjectId objectId,
            string name,
            string alignmentName,
            string styleName,
            string bandSetName,
            string stationRange,
            string elevationRange,
            string outOfDate)
        {
            ObjectId = objectId;
            Name = string.IsNullOrWhiteSpace(name) ? objectId.Handle.ToString() : name;
            AlignmentName = alignmentName;
            StyleName = styleName;
            BandSetName = bandSetName;
            StationRange = stationRange;
            ElevationRange = elevationRange;
            OutOfDate = outOfDate;
            IsSelected = true;
        }

        public ObjectId ObjectId { get; private set; }
        public string Name { get; private set; }
        public string AlignmentName { get; private set; }
        public string StyleName { get; private set; }
        public string BandSetName { get; private set; }
        public string StationRange { get; private set; }
        public string ElevationRange { get; private set; }
        public string OutOfDate { get; private set; }
        public bool IsSelected { get; set; }
    }

    internal sealed class ProfileViewStyleChoice
    {
        public ProfileViewStyleChoice(ObjectId objectId, string displayName)
        {
            ObjectId = objectId;
            DisplayName = displayName;
        }

        public ObjectId ObjectId { get; private set; }
        public string DisplayName { get; private set; }
        public override string ToString() { return DisplayName; }
    }

    internal sealed class ProfileViewStyleCatalogue
    {
        public ProfileViewStyleCatalogue()
        {
            ProfileViewStyles = new List<ProfileViewStyleChoice>();
            BandSetStyles = new List<ProfileViewStyleChoice>();
        }

        public List<ProfileViewStyleChoice> ProfileViewStyles { get; private set; }
        public List<ProfileViewStyleChoice> BandSetStyles { get; private set; }
    }

    internal sealed class ProfileViewPlacement
    {
        internal ProfileViewItem Item { get; set; }
        internal Point3d Min { get; set; }
        internal Point3d Max { get; set; }
    }

    internal sealed class ProfileViewBatchResult
    {
        public int ViewsChanged { get; set; }
        public int ProfileStylesApplied { get; set; }
        public int BandSetsApplied { get; set; }
        public int ViewsAutoFit { get; set; }
        public int ViewsLabelMarginFit { get; set; }
        public int ViewsRebuilt { get; set; }
        public int Unsupported { get; set; }
        public int Failed { get; set; }
    }

    internal sealed class ProfileViewBatchWindow : Window
    {
        private readonly IList<ProfileViewItem> _views;
        private readonly ComboBox _profileStyle;
        private readonly ComboBox _bandSet;
        private readonly CheckBox _applyProfileStyle;
        private readonly CheckBox _applyBandSet;
        private readonly CheckBox _autoFit;
        private readonly CheckBox _rebuild;
        private readonly CheckBox _overlap;

        public ProfileViewBatchWindow(
            IList<ProfileViewItem> views,
            ProfileViewStyleCatalogue catalogue)
        {
            _views = views;
            Accepted = false;
            Title = "CE Tools - Batch Profile View Cleanup";
            Width = 860;
            Height = 720;
            MinWidth = 680;
            MinHeight = 520;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.CanResize;

            var root = new DockPanel { Margin = new Thickness(16) };
            Content = root;
            var heading = new TextBlock
            {
                Text = "Batch Profile View Cleanup",
                FontSize = 20,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 6)
            };
            DockPanel.SetDock(heading, Dock.Top);
            root.Children.Add(heading);
            var note = new TextBlock
            {
                Text = "Select profile views and the operations to apply. Unsupported Civil 3D API operations will be counted and reported.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            };
            DockPanel.SetDock(note, Dock.Top);
            root.Children.Add(note);

            var actions = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            _applyProfileStyle = new CheckBox { Content = "Apply profile-view style", Margin = new Thickness(0, 2, 0, 2) };
            _profileStyle = new ComboBox
            {
                ItemsSource = catalogue.ProfileViewStyles,
                SelectedIndex = catalogue.ProfileViewStyles.Count > 0 ? 0 : -1,
                IsTextSearchEnabled = true,
                MinWidth = 360,
                Margin = new Thickness(20, 2, 0, 4)
            };
            _applyBandSet = new CheckBox { Content = "Apply profile-view band-set style", Margin = new Thickness(0, 2, 0, 2) };
            _bandSet = new ComboBox
            {
                ItemsSource = catalogue.BandSetStyles,
                SelectedIndex = catalogue.BandSetStyles.Count > 0 ? 0 : -1,
                IsTextSearchEnabled = true,
                MinWidth = 360,
                Margin = new Thickness(20, 2, 0, 4)
            };
            _autoFit = new CheckBox { Content = "Set automatic station/elevation range where supported", IsChecked = true, Margin = new Thickness(0, 2, 0, 2) };
            _rebuild = new CheckBox { Content = "Rebuild/update views where supported", IsChecked = true, Margin = new Thickness(0, 2, 0, 2) };
            _overlap = new CheckBox { Content = "Open CE overlap cleanup after batch operation", IsChecked = false, Margin = new Thickness(0, 2, 0, 2) };
            actions.Children.Add(_applyProfileStyle);
            actions.Children.Add(_profileStyle);
            actions.Children.Add(_applyBandSet);
            actions.Children.Add(_bandSet);
            actions.Children.Add(_autoFit);
            actions.Children.Add(_rebuild);
            actions.Children.Add(_overlap);
            DockPanel.SetDock(actions, Dock.Top);
            root.Children.Add(actions);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0)
            };
            var selectAll = new Button { Content = "Select All", MinWidth = 90, Padding = new Thickness(8, 5, 8, 5) };
            selectAll.Click += delegate { foreach (ProfileViewItem item in _views) item.IsSelected = true; RefreshItems(); };
            var selectNone = new Button { Content = "Clear Selection", MinWidth = 110, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(8, 5, 8, 5) };
            selectNone.Click += delegate { foreach (ProfileViewItem item in _views) item.IsSelected = false; RefreshItems(); };
            var apply = new Button { Content = "Review and Apply", MinWidth = 120, Margin = new Thickness(16, 0, 0, 0), Padding = new Thickness(8, 5, 8, 5) };
            apply.Click += delegate
            {
                if (!SelectedViews.Any())
                {
                    MessageBox.Show(this, "Select at least one profile view.", "CE Tools", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (ApplyProfileViewStyle && SelectedProfileViewStyle == null)
                {
                    MessageBox.Show(this, "No profile-view style is available/selected.", "CE Tools", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (ApplyBandSetStyle && SelectedBandSetStyle == null)
                {
                    MessageBox.Show(this, "No profile-view band-set style is available/selected.", "CE Tools", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                Accepted = true;
                DialogResult = true;
            };
            var cancel = new Button { Content = "Cancel", MinWidth = 90, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(8, 5, 8, 5) };
            cancel.Click += delegate { Accepted = false; DialogResult = false; };
            buttons.Children.Add(selectAll);
            buttons.Children.Add(selectNone);
            buttons.Children.Add(apply);
            buttons.Children.Add(cancel);
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(buttons);

            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            _itemsPanel = new StackPanel();
            scroll.Content = _itemsPanel;
            root.Children.Add(scroll);
            RefreshItems();
        }

        private readonly StackPanel _itemsPanel;
        public bool Accepted { get; private set; }
        public bool ApplyProfileViewStyle { get { return _applyProfileStyle.IsChecked == true; } }
        public bool ApplyBandSetStyle { get { return _applyBandSet.IsChecked == true; } }
        public bool AutoFit { get { return _autoFit.IsChecked == true; } }
        public bool Rebuild { get { return _rebuild.IsChecked == true; } }
        public bool RunOverlapCleanup { get { return _overlap.IsChecked == true; } }
        public ProfileViewStyleChoice SelectedProfileViewStyle { get { return _profileStyle.SelectedItem as ProfileViewStyleChoice; } }
        public ProfileViewStyleChoice SelectedBandSetStyle { get { return _bandSet.SelectedItem as ProfileViewStyleChoice; } }
        public List<ProfileViewItem> SelectedViews { get { return _views.Where(item => item.IsSelected).ToList(); } }

        private void RefreshItems()
        {
            _itemsPanel.Children.Clear();
            foreach (ProfileViewItem item in _views)
            {
                var check = new CheckBox
                {
                    IsChecked = item.IsSelected,
                    Content = item.AlignmentName + " — " + item.Name + " | " + item.StyleName + " | " + item.OutOfDate,
                    Margin = new Thickness(0, 3, 0, 3)
                };
                ProfileViewItem captured = item;
                check.Checked += delegate { captured.IsSelected = true; };
                check.Unchecked += delegate { captured.IsSelected = false; };
                _itemsPanel.Children.Add(check);
            }
        }
    }

    internal sealed class ProfileViewBatchLauncherWindow : Window
    {
        public ProfileViewBatchLauncherWindow()
        {
            Title = "CE Tools - Profile View Batch Tools";
            Width = 460;
            Height = 485;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var root = new StackPanel { Margin = new Thickness(18) };
            Content = root;
            root.Children.Add(new TextBlock
            {
                Text = "Profile View Batch Tools",
                FontSize = 20,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 8)
            });
            root.Children.Add(new TextBlock
            {
                Text = "Choose a workflow. All configuration is completed in popup windows.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            });
            AddButton(root, "Batch styles, band sets and rebuild", "CE_PROFILEVIEWBATCH ");
            AddButton(root, "Edit one native Profile View, then apply to selected", "CE_PROFILEVIEWEDITMATCH ");
            AddButton(root, "Match first/source profile view to multiple views", "CE_PROFILEVIEWMATCH ");
            AddButton(root, "Batch-edit band Data Sources", "CE_PROFILEVIEWDATASOURCES ");
            AddButton(root, "Repair Sewer Pipe Network Band Labels", "CE_SEWPIPEBANDGROUPS ");
            AddButton(root, "Import Road Band Set + Show Labels", "CE_ROADBANDLABELS ");
            AddButton(root, "Fit all selected profile views", "CE_PROFILEVIEWFITALL ");
            AddButton(root, "Arrange selected profile views - horizontal / vertical / grid", "CE_PROFILEVIEWARRANGE ");
            AddButton(root, "Profile-view information", "CE_PROFILEVIEWBATCHINFO ");
        }

        public string Command { get; private set; }

        private void AddButton(Panel panel, string text, string command)
        {
            var button = new Button
            {
                Content = text,
                Margin = new Thickness(0, 3, 0, 3),
                Padding = new Thickness(8, 6, 8, 6)
            };
            button.Click += delegate
            {
                Command = command;
                DialogResult = true;
            };
            panel.Children.Add(button);
        }
    }
}

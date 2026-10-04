using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivilProfileView = Autodesk.Civil.DatabaseServices.ProfileView;

[assembly: CommandClass(typeof(CETools.Civil3D.October03ViewportPlotProfileCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// October 3 production utilities for paper-space viewport control and
    /// branch-aware profile-view layout.  Viewports are never created or erased;
    /// the commands only fit, lock/unlock and regenerate existing layout viewports.
    /// </summary>
    public sealed class October03ViewportPlotProfileCommands
    {
        private const double Tol = 0.000001;

        [CommandMethod(
            "CE_TOOLS",
            "CE_VIEWPORTREGENALL",
            CommandFlags.Modal | CommandFlags.Redraw)]
        public void RegenerateViewports()
        {
            Document document =
                AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Regenerate Viewports",
                "Regenerate every model viewport in the current layout or all paper-space layouts in the drawing. Saved scope becomes the default.");
            settings.AddChoice(
                "Scope",
                "01 Scope",
                "Viewport scope",
                "All layouts in drawing",
                "Choose whether REGEN applies only to the current paper layout or to every paper layout.",
                new[] { "Current layout", "All layouts in drawing" });
            if (!DisciplineWorkflowDialogs.EditSettings(settings))
                return;

            bool allLayouts = string.Equals(
                settings.Text("Scope"),
                "All layouts in drawing",
                StringComparison.OrdinalIgnoreCase);
            int touched = MarkViewportsForGraphicsRefresh(
                document,
                allLayouts);

            try
            {
                document.Editor.Regen();
                document.Editor.Command("_.REGENALL");
                AcApplication.UpdateScreen();
            }
            catch
            {
                try { document.Editor.Regen(); }
                catch { }
            }

            document.Editor.WriteMessage(
                "\nCE_VIEWPORTREGENALL complete. Viewports queued/regenerated={0}; scope={1}.",
                touched,
                allLayouts ? "all layouts" : "current layout");
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_PROFILEVIEWPORTFIT",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void FitProfileViewsIntoViewports()
        {
            Document document =
                AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civil =
                CivilApplication.ActiveDocument;
            if (document == null || civil == null)
                return;

            List<string> layouts =
                ReadPaperLayoutNames(document.Database);
            if (layouts.Count == 0)
            {
                document.Editor.WriteMessage(
                    "\nCE_PROFILEVIEWPORTFIT: this drawing has no paper-space layouts.");
                return;
            }

            string currentLayout =
                LayoutManager.Current == null
                    ? string.Empty
                    : LayoutManager.Current.CurrentLayout;
            string defaultLayout = layouts.FirstOrDefault(name =>
                string.Equals(
                    name,
                    currentLayout,
                    StringComparison.OrdinalIgnoreCase)) ??
                layouts[0];

            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Fit Branch Profile Views Into Viewports",
                "Fit selected or all Civil 3D profile views into existing paper-space viewports. " +
                "Profile views are sequenced by numeric Branch order. Long branches can consume consecutive viewports; Smart packing may reserve a larger viewport for a long branch and reuse smaller gaps for later short branches.");
            settings.AddChoice(
                "ProfileScope",
                "01 Sources",
                "Profile-view scope",
                "All profile views",
                "Process every profile view in the drawing, or select only the required profile views after saving settings.",
                new[] { "All profile views", "Selected profile views" });
            settings.AddChoice(
                "Layout",
                "02 Layout",
                "Target layout",
                defaultLayout,
                "Existing paper-space layout whose model viewports will be used.",
                layouts);
            settings.AddChoice(
                "Packing",
                "02 Layout",
                "Viewport packing",
                "Smart fit - reuse smaller gaps",
                "Strict order consumes viewports top-left to top-right then downward. Smart fit may move a long branch to the next suitable wider viewport and leave a smaller slot for a later short branch.",
                new[] {
                    "Smart fit - reuse smaller gaps",
                    "Strict branch order"
                });
            settings.AddChoice(
                "LongBranch",
                "02 Layout",
                "Long branch handling",
                "Split across consecutive viewports",
                "When a branch cannot fit at the viewport's vertical-fit scale, split its displayed X range across additional viewports or shrink it into one viewport.",
                new[] {
                    "Split across consecutive viewports",
                    "Shrink into one viewport"
                });
            settings.AddText(
                "HorizontalClearance",
                "03 Clearance",
                "Horizontal clearance",
                "5",
                "Model-space clearance added at the left/right of the profile view. Zero is allowed.");
            settings.AddText(
                "VerticalClearance",
                "03 Clearance",
                "Vertical clearance",
                "5",
                "Model-space clearance added above/below the profile view. Zero is allowed.");
            settings.AddChoice(
                "LockAfter",
                "04 Finish",
                "Lock fitted viewports",
                "Yes",
                "Lock each viewport after its model view has been fitted.",
                new[] { "Yes", "No" });
            settings.AddChoice(
                "RegenAfter",
                "04 Finish",
                "Regenerate all viewports",
                "Yes",
                "Run a full viewport regeneration after fitting.",
                new[] { "Yes", "No" });
            if (!DisciplineWorkflowDialogs.EditSettings(settings))
                return;

            double horizontalClearance;
            double verticalClearance;
            if (!ProductionSettingsDialogModel.TryDouble(
                    settings.Text("HorizontalClearance"),
                    out horizontalClearance) ||
                !ProductionSettingsDialogModel.TryDouble(
                    settings.Text("VerticalClearance"),
                    out verticalClearance) ||
                horizontalClearance < 0.0 ||
                verticalClearance < 0.0)
            {
                document.Editor.WriteMessage(
                    "\nCE_PROFILEVIEWPORTFIT cancelled. Horizontal and vertical clearance must be zero or greater.");
                return;
            }

            List<ProfileSource> profiles =
                ReadProfileSources(document, civil);
            if (string.Equals(
                    settings.Text("ProfileScope"),
                    "Selected profile views",
                    StringComparison.OrdinalIgnoreCase))
            {
                profiles = SelectProfileSources(
                    document,
                    profiles);
            }
            if (profiles.Count == 0)
            {
                document.Editor.WriteMessage(
                    "\nCE_PROFILEVIEWPORTFIT: no profile views were selected/found.");
                return;
            }

            string layoutName = settings.Text("Layout");
            List<ViewportSlot> slots =
                ReadLayoutViewports(
                    document.Database,
                    layoutName);
            if (slots.Count == 0)
            {
                document.Editor.WriteMessage(
                    "\nCE_PROFILEVIEWPORTFIT: layout '{0}' has no model viewports.",
                    layoutName);
                return;
            }

            profiles = profiles
                .OrderBy(item => item.Branch)
                .ThenBy(item => item.AlignmentName,
                    StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(item => item.StationStart)
                .ThenBy(item => item.Name,
                    StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            slots = SortViewportSlots(slots);

            bool smart = string.Equals(
                settings.Text("Packing"),
                "Smart fit - reuse smaller gaps",
                StringComparison.OrdinalIgnoreCase);
            bool splitLong = string.Equals(
                settings.Text("LongBranch"),
                "Split across consecutive viewports",
                StringComparison.OrdinalIgnoreCase);
            bool lockAfter = string.Equals(
                settings.Text("LockAfter"),
                "Yes",
                StringComparison.OrdinalIgnoreCase);
            bool regenAfter = string.Equals(
                settings.Text("RegenAfter"),
                "Yes",
                StringComparison.OrdinalIgnoreCase);

            FitResult result = FitProfiles(
                document,
                profiles,
                slots,
                horizontalClearance,
                verticalClearance,
                smart,
                splitLong,
                lockAfter);

            if (regenAfter)
            {
                MarkViewportsForGraphicsRefresh(
                    document,
                    true);
                try
                {
                    document.Editor.Regen();
                    document.Editor.Command("_.REGENALL");
                    AcApplication.UpdateScreen();
                }
                catch
                {
                    try { document.Editor.Regen(); }
                    catch { }
                }
            }
            else
            {
                try { document.Editor.Regen(); }
                catch { }
            }

            document.Editor.WriteMessage(
                "\nCE_PROFILEVIEWPORTFIT complete. Branch/profile views fitted={0}; viewport segments used={1}; long views split={2}; profile views not fitted={3}; unused viewports={4}; layout={5}.",
                result.ProfilesFitted,
                result.ViewportsUsed,
                result.ProfilesSplit,
                result.ProfilesNotFitted,
                Math.Max(0, slots.Count - result.ViewportsUsed),
                layoutName);
        }

        internal static void SetViewportLock(bool locked)
        {
            Document document =
                AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            var settings = new ProductionSettingsDialogModel(
                locked
                    ? "CE Tools - Lock Viewports"
                    : "CE Tools - Unlock Viewports",
                "Apply the viewport lock state to the current layout or every paper-space layout in this drawing. The selected scope is saved as the next default.");
            settings.AddChoice(
                "Scope",
                "01 Scope",
                "Viewport scope",
                "All layouts in drawing",
                "All layouts includes every paper-space layout, not only the active sheet.",
                new[] { "Current layout", "All layouts in drawing" });
            settings.AddChoice(
                "Regen",
                "02 Finish",
                "Regenerate after change",
                "Yes",
                "Regenerate the drawing after the viewport lock state changes.",
                new[] { "Yes", "No" });
            if (!DisciplineWorkflowDialogs.EditSettings(settings))
                return;

            bool allLayouts = string.Equals(
                settings.Text("Scope"),
                "All layouts in drawing",
                StringComparison.OrdinalIgnoreCase);
            int changed = SetViewportLockState(
                document,
                allLayouts,
                locked);

            if (string.Equals(
                    settings.Text("Regen"),
                    "Yes",
                    StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    document.Editor.Regen();
                    AcApplication.UpdateScreen();
                }
                catch { }
            }

            document.Editor.WriteMessage(
                "\n{0} complete. Viewports changed={1}; scope={2}.",
                locked
                    ? "CE_VIEWPORTLOCKALL"
                    : "CE_VIEWPORTUNLOCKALL",
                changed,
                allLayouts ? "all layouts" : "current layout");
        }

        private static int SetViewportLockState(
            Document document,
            bool allLayouts,
            bool locked)
        {
            int changed = 0;
            string current = LayoutManager.Current == null
                ? string.Empty
                : LayoutManager.Current.CurrentLayout;

            using (DocumentLock documentLock =
                document.LockDocument())
            using (Transaction transaction =
                document.Database.TransactionManager.StartTransaction())
            {
                foreach (Layout layout in ReadLayouts(
                    document.Database,
                    transaction))
                {
                    if (layout == null || layout.ModelType)
                        continue;
                    if (!allLayouts &&
                        !string.Equals(
                            layout.LayoutName,
                            current,
                            StringComparison.OrdinalIgnoreCase))
                        continue;

                    BlockTableRecord paper =
                        transaction.GetObject(
                            layout.BlockTableRecordId,
                            OpenMode.ForRead,
                            false) as BlockTableRecord;
                    if (paper == null)
                        continue;

                    foreach (ObjectId id in paper)
                    {
                        Viewport viewport = null;
                        try
                        {
                            viewport = transaction.GetObject(
                                id,
                                OpenMode.ForWrite,
                                false) as Viewport;
                        }
                        catch { }
                        if (viewport == null ||
                            viewport.Number <= 1)
                            continue;
                        if (viewport.Locked == locked)
                            continue;
                        viewport.Locked = locked;
                        changed++;
                    }
                }
                transaction.Commit();
            }
            return changed;
        }

        private static int MarkViewportsForGraphicsRefresh(
            Document document,
            bool allLayouts)
        {
            int count = 0;
            string current = LayoutManager.Current == null
                ? string.Empty
                : LayoutManager.Current.CurrentLayout;

            using (DocumentLock documentLock =
                document.LockDocument())
            using (Transaction transaction =
                document.Database.TransactionManager.StartTransaction())
            {
                foreach (Layout layout in ReadLayouts(
                    document.Database,
                    transaction))
                {
                    if (layout == null || layout.ModelType)
                        continue;
                    if (!allLayouts &&
                        !string.Equals(
                            layout.LayoutName,
                            current,
                            StringComparison.OrdinalIgnoreCase))
                        continue;

                    BlockTableRecord paper =
                        transaction.GetObject(
                            layout.BlockTableRecordId,
                            OpenMode.ForRead,
                            false) as BlockTableRecord;
                    if (paper == null)
                        continue;

                    foreach (ObjectId id in paper)
                    {
                        Viewport viewport = null;
                        try
                        {
                            viewport = transaction.GetObject(
                                id,
                                OpenMode.ForWrite,
                                false) as Viewport;
                        }
                        catch { }
                        if (viewport == null ||
                            viewport.Number <= 1)
                            continue;
                        try
                        {
                            viewport.RecordGraphicsModified(true);
                        }
                        catch { }
                        count++;
                    }
                }
                transaction.Commit();
            }
            return count;
        }

        private static IEnumerable<Layout> ReadLayouts(
            Database database,
            Transaction transaction)
        {
            var result = new List<Layout>();
            DBDictionary dictionary = transaction.GetObject(
                database.LayoutDictionaryId,
                OpenMode.ForRead,
                false) as DBDictionary;
            if (dictionary == null)
                return result;

            foreach (System.Collections.DictionaryEntry entry in dictionary)
            {
                Layout layout = null;
                try
                {
                    ObjectId layoutId =
                        entry.Value is ObjectId
                            ? (ObjectId)entry.Value
                            : ObjectId.Null;
                    if (!layoutId.IsNull)
                    {
                        layout = transaction.GetObject(
                            layoutId,
                            OpenMode.ForRead,
                            false) as Layout;
                    }
                }
                catch { }
                if (layout != null)
                    result.Add(layout);
            }
            return result;
        }

        private static List<string> ReadPaperLayoutNames(
            Database database)
        {
            var result = new List<string>();
            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                foreach (Layout layout in ReadLayouts(
                    database,
                    transaction))
                {
                    if (layout != null &&
                        !layout.ModelType &&
                        !string.IsNullOrWhiteSpace(layout.LayoutName))
                        result.Add(layout.LayoutName);
                }
            }
            return result
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(item => item,
                    StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private static List<ViewportSlot> ReadLayoutViewports(
            Database database,
            string layoutName)
        {
            var result = new List<ViewportSlot>();
            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                Layout selected = ReadLayouts(
                        database,
                        transaction)
                    .FirstOrDefault(layout =>
                        layout != null &&
                        !layout.ModelType &&
                        string.Equals(
                            layout.LayoutName,
                            layoutName,
                            StringComparison.OrdinalIgnoreCase));
                if (selected == null)
                    return result;

                BlockTableRecord paper =
                    transaction.GetObject(
                        selected.BlockTableRecordId,
                        OpenMode.ForRead,
                        false) as BlockTableRecord;
                if (paper == null)
                    return result;

                foreach (ObjectId id in paper)
                {
                    Viewport viewport = null;
                    try
                    {
                        viewport = transaction.GetObject(
                            id,
                            OpenMode.ForRead,
                            false) as Viewport;
                    }
                    catch { }
                    if (viewport == null ||
                        viewport.Number <= 1 ||
                        viewport.Width <= Tol ||
                        viewport.Height <= Tol)
                        continue;

                    result.Add(new ViewportSlot
                    {
                        Id = id,
                        Center = viewport.CenterPoint,
                        Width = viewport.Width,
                        Height = viewport.Height
                    });
                }
            }
            return result;
        }

        private static List<ViewportSlot> SortViewportSlots(
            IList<ViewportSlot> source)
        {
            if (source == null)
                return new List<ViewportSlot>();
            double rowBand = Math.Max(
                1.0,
                source.Count == 0
                    ? 1.0
                    : source.Average(item => item.Height) * 0.45);
            return source
                .OrderByDescending(item =>
                    Math.Round(
                        item.Center.Y / rowBand,
                        MidpointRounding.AwayFromZero))
                .ThenBy(item => item.Center.X)
                .ToList();
        }

        private static List<ProfileSource> ReadProfileSources(
            Document document,
            CivilDocument civil)
        {
            var result = new List<ProfileSource>();
            using (Transaction transaction =
                document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId alignmentId in civil.GetAlignmentIds())
                {
                    CivilAlignment alignment = null;
                    try
                    {
                        alignment = transaction.GetObject(
                            alignmentId,
                            OpenMode.ForRead,
                            false) as CivilAlignment;
                    }
                    catch { }
                    if (alignment == null)
                        continue;

                    foreach (ObjectId viewId in
                        alignment.GetProfileViewIds())
                    {
                        CivilProfileView view = null;
                        try
                        {
                            view = transaction.GetObject(
                                viewId,
                                OpenMode.ForRead,
                                false) as CivilProfileView;
                        }
                        catch { }
                        if (view == null)
                            continue;

                        Extents3d extents;
                        try
                        {
                            extents =
                                ((Entity)view).GeometricExtents;
                        }
                        catch
                        {
                            continue;
                        }

                        double stationStart =
                            ReadDoubleProperty(
                                view,
                                "StationStart",
                                alignment.StartingStation);
                        result.Add(new ProfileSource
                        {
                            Id = viewId,
                            Name = string.IsNullOrWhiteSpace(view.Name)
                                ? viewId.Handle.ToString()
                                : view.Name,
                            AlignmentName = alignment.Name ?? string.Empty,
                            Branch = BranchNumber(
                                alignment.Name,
                                view.Name),
                            StationStart = stationStart,
                            Min = extents.MinPoint,
                            Max = extents.MaxPoint
                        });
                    }
                }
            }
            return result;
        }

        private static List<ProfileSource> SelectProfileSources(
            Document document,
            IList<ProfileSource> all)
        {
            if (all == null || all.Count == 0)
                return new List<ProfileSource>();

            PromptSelectionResult selection =
                document.Editor.SelectImplied();
            if (selection.Status != PromptStatus.OK ||
                selection.Value == null ||
                selection.Value.Count == 0)
            {
                selection = document.Editor.GetSelection(
                    new PromptSelectionOptions
                    {
                        MessageForAdding =
                            "\nSelect Civil 3D profile views to fit into layout viewports: ",
                        AllowDuplicates = false,
                        RejectObjectsFromNonCurrentSpace = false
                    });
            }
            try
            {
                document.Editor.SetImpliedSelection(
                    new ObjectId[0]);
            }
            catch { }

            if (selection.Status != PromptStatus.OK ||
                selection.Value == null)
                return new List<ProfileSource>();

            var ids = new HashSet<ObjectId>(
                selection.Value.GetObjectIds());
            return all
                .Where(item => ids.Contains(item.Id))
                .ToList();
        }

        private static int BranchNumber(
            string alignmentName,
            string viewName)
        {
            foreach (string text in new[]
            {
                alignmentName ?? string.Empty,
                viewName ?? string.Empty
            })
            {
                Match match = Regex.Match(
                    text,
                    @"(?:BRANCH|BR)s*[-_ ]*s*(d+)",
                    RegexOptions.IgnoreCase |
                    RegexOptions.CultureInvariant);
                int number;
                if (match.Success &&
                    int.TryParse(
                        match.Groups[1].Value,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out number))
                    return number;

                match = Regex.Match(
                    text,
                    @"d+",
                    RegexOptions.CultureInvariant);
                if (match.Success &&
                    int.TryParse(
                        match.Value,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out number))
                    return number;
            }
            return int.MaxValue;
        }

        private static double ReadDoubleProperty(
            object value,
            string propertyName,
            double fallback)
        {
            if (value == null)
                return fallback;
            try
            {
                PropertyInfo property = value.GetType().GetProperty(
                    propertyName,
                    BindingFlags.Public |
                    BindingFlags.Instance);
                if (property == null ||
                    !property.CanRead)
                    return fallback;
                double result = Convert.ToDouble(
                    property.GetValue(value, null),
                    CultureInfo.InvariantCulture);
                return double.IsNaN(result) ||
                    double.IsInfinity(result)
                        ? fallback
                        : result;
            }
            catch
            {
                return fallback;
            }
        }

        private static FitResult FitProfiles(
            Document document,
            IList<ProfileSource> profiles,
            IList<ViewportSlot> orderedSlots,
            double horizontalClearance,
            double verticalClearance,
            bool smartPacking,
            bool splitLong,
            bool lockAfter)
        {
            var result = new FitResult();
            var available = orderedSlots
                .Select((slot, index) =>
                    new SlotUse
                    {
                        Slot = slot,
                        Order = index,
                        Used = false
                    })
                .ToList();

            using (DocumentLock documentLock =
                document.LockDocument())
            using (Transaction transaction =
                document.Database.TransactionManager.StartTransaction())
            {
                foreach (ProfileSource profile in profiles)
                {
                    List<SlotUse> free = available
                        .Where(item => !item.Used)
                        .OrderBy(item => item.Order)
                        .ToList();
                    if (free.Count == 0)
                    {
                        result.ProfilesNotFitted++;
                        continue;
                    }

                    SlotUse first = free[0];
                    if (smartPacking)
                    {
                        SlotUse fitting = free.FirstOrDefault(item =>
                            FitsAtVerticalScale(
                                profile,
                                item.Slot,
                                horizontalClearance,
                                verticalClearance));
                        if (fitting != null)
                            first = fitting;
                    }

                    if (FitsAtVerticalScale(
                            profile,
                            first.Slot,
                            horizontalClearance,
                            verticalClearance) ||
                        !splitLong)
                    {
                        if (ApplyViewportFit(
                                transaction,
                                first.Slot,
                                profile.Min.X -
                                    horizontalClearance,
                                profile.Max.X +
                                    horizontalClearance,
                                profile.Min.Y -
                                    verticalClearance,
                                profile.Max.Y +
                                    verticalClearance,
                                lockAfter))
                        {
                            first.Used = true;
                            result.ViewportsUsed++;
                            result.ProfilesFitted++;
                        }
                        else
                        {
                            result.ProfilesNotFitted++;
                        }
                        continue;
                    }

                    // Split only the visible horizontal model range. Every segment
                    // keeps the complete profile height and clearance, so vertical
                    // scale/readability stays consistent while a long branch moves
                    // through consecutive available paper-space viewports.
                    double requiredCoreWidth =
                        Math.Max(
                            0.0,
                            profile.Max.X - profile.Min.X);
                    double availableCoreWidth = free.Sum(slotUse =>
                        CoreModelWidthAtVerticalScale(
                            profile,
                            slotUse.Slot,
                            horizontalClearance,
                            verticalClearance));
                    if (availableCoreWidth + Tol <
                        requiredCoreWidth)
                    {
                        // Never leave a branch half-fitted. If the remaining
                        // viewports cannot display the complete long profile,
                        // leave them untouched and report the branch as pending.
                        result.ProfilesNotFitted++;
                        continue;
                    }

                    double x = profile.Min.X;
                    int usedForProfile = 0;
                    foreach (SlotUse slotUse in free)
                    {
                        if (x >= profile.Max.X - Tol)
                            break;

                        double coreWidth =
                            CoreModelWidthAtVerticalScale(
                                profile,
                                slotUse.Slot,
                                horizontalClearance,
                                verticalClearance);
                        if (coreWidth <= Tol)
                            continue;

                        double endX = Math.Min(
                            profile.Max.X,
                            x + coreWidth);
                        if (!ApplyViewportFit(
                                transaction,
                                slotUse.Slot,
                                x - horizontalClearance,
                                endX + horizontalClearance,
                                profile.Min.Y -
                                    verticalClearance,
                                profile.Max.Y +
                                    verticalClearance,
                                lockAfter))
                            continue;

                        slotUse.Used = true;
                        result.ViewportsUsed++;
                        usedForProfile++;
                        x = endX;
                    }

                    if (x >= profile.Max.X - Tol &&
                        usedForProfile > 0)
                    {
                        result.ProfilesFitted++;
                        if (usedForProfile > 1)
                            result.ProfilesSplit++;
                    }
                    else
                    {
                        result.ProfilesNotFitted++;
                    }
                }

                transaction.Commit();
            }

            return result;
        }

        private static bool FitsAtVerticalScale(
            ProfileSource profile,
            ViewportSlot viewport,
            double horizontalClearance,
            double verticalClearance)
        {
            double totalWidth =
                Math.Max(
                    Tol,
                    profile.Max.X -
                    profile.Min.X +
                    2.0 * horizontalClearance);
            double totalHeight =
                Math.Max(
                    Tol,
                    profile.Max.Y -
                    profile.Min.Y +
                    2.0 * verticalClearance);
            double widthCapacity =
                totalHeight *
                viewport.Width /
                Math.Max(viewport.Height, Tol);
            return totalWidth <= widthCapacity + Tol;
        }

        private static double CoreModelWidthAtVerticalScale(
            ProfileSource profile,
            ViewportSlot viewport,
            double horizontalClearance,
            double verticalClearance)
        {
            double totalHeight =
                Math.Max(
                    Tol,
                    profile.Max.Y -
                    profile.Min.Y +
                    2.0 * verticalClearance);
            double totalWidthCapacity =
                totalHeight *
                viewport.Width /
                Math.Max(viewport.Height, Tol);
            return Math.Max(
                0.0,
                totalWidthCapacity -
                2.0 * horizontalClearance);
        }

        private static bool ApplyViewportFit(
            Transaction transaction,
            ViewportSlot slot,
            double minX,
            double maxX,
            double minY,
            double maxY,
            bool lockAfter)
        {
            Viewport viewport = null;
            try
            {
                viewport = transaction.GetObject(
                    slot.Id,
                    OpenMode.ForWrite,
                    false) as Viewport;
            }
            catch { }
            if (viewport == null ||
                viewport.Number <= 1)
                return false;

            double width =
                Math.Max(Tol, maxX - minX);
            double height =
                Math.Max(Tol, maxY - minY);
            double scale =
                Math.Min(
                    viewport.Width / width,
                    viewport.Height / height);
            if (double.IsNaN(scale) ||
                double.IsInfinity(scale) ||
                scale <= Tol)
                return false;

            double finalModelHeight =
                viewport.Height / scale;
            double centerX = (minX + maxX) * 0.5;
            double centerY = (minY + maxY) * 0.5;

            try
            {
                viewport.On = true;
                viewport.ViewDirection = Vector3d.ZAxis;
                viewport.TwistAngle = 0.0;
                viewport.ViewTarget = Point3d.Origin;
                viewport.ViewCenter =
                    new Point2d(centerX, centerY);
                viewport.ViewHeight = finalModelHeight;
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

        private sealed class ProfileSource
        {
            internal ObjectId Id;
            internal string Name;
            internal string AlignmentName;
            internal int Branch;
            internal double StationStart;
            internal Point3d Min;
            internal Point3d Max;
        }

        private sealed class ViewportSlot
        {
            internal ObjectId Id;
            internal Point3d Center;
            internal double Width;
            internal double Height;
        }

        private sealed class SlotUse
        {
            internal ViewportSlot Slot;
            internal int Order;
            internal bool Used;
        }

        private sealed class FitResult
        {
            internal int ProfilesFitted;
            internal int ProfilesNotFitted;
            internal int ProfilesSplit;
            internal int ViewportsUsed;
        }
    }

    /// <summary>
    /// Keeps AutoCAD's native Plot-to-file folder aligned with the active DWG and
    /// opens the newly written PDF after a successful PLOT/PRINT.  The operation
    /// is intentionally outside the plot command: background plotting may finish
    /// after CommandEnded, so the PDF is discovered by a short bounded poll.
    /// </summary>
    internal static class October03PlotPdfManager
    {
        private sealed class PlotState
        {
            internal string Folder;
            internal DateTime StartedUtc;
            internal Dictionary<string, DateTime> Baseline =
                new Dictionary<string, DateTime>(
                    StringComparer.OrdinalIgnoreCase);
            internal bool Waiting;
        }

        private static readonly Dictionary<Document, PlotState> States =
            new Dictionary<Document, PlotState>();
        private static readonly HashSet<Document> Attached =
            new HashSet<Document>();
        private static bool _initialized;

        internal static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;
            AcApplication.Idle += OnIdle;
            Attach(
                AcApplication.DocumentManager.MdiActiveDocument);
        }

        internal static void Terminate()
        {
            if (!_initialized) return;
            _initialized = false;
            AcApplication.Idle -= OnIdle;
            foreach (Document document in
                Attached.ToList())
                Detach(document);
            Attached.Clear();
            States.Clear();
        }

        private static void OnIdle(object sender, EventArgs args)
        {
            if (!_initialized) return;
            Document document =
                AcApplication.DocumentManager.MdiActiveDocument;
            Attach(document);
            ApplyDrawingFolder(document);
        }

        private static void Attach(Document document)
        {
            if (document == null ||
                Attached.Contains(document))
                return;
            try
            {
                document.CommandWillStart +=
                    OnCommandWillStart;
                document.CommandEnded +=
                    OnCommandEnded;
                document.CommandCancelled +=
                    OnCommandCancelled;
                document.CommandFailed +=
                    OnCommandCancelled;
                Attached.Add(document);
            }
            catch { }
        }

        private static void Detach(Document document)
        {
            if (document == null) return;
            try
            {
                document.CommandWillStart -=
                    OnCommandWillStart;
            }
            catch { }
            try
            {
                document.CommandEnded -=
                    OnCommandEnded;
            }
            catch { }
            try
            {
                document.CommandCancelled -=
                    OnCommandCancelled;
            }
            catch { }
            try
            {
                document.CommandFailed -=
                    OnCommandCancelled;
            }
            catch { }
            Attached.Remove(document);
            States.Remove(document);
        }

        private static void OnCommandWillStart(
            object sender,
            CommandEventArgs args)
        {
            Document document = sender as Document;
            if (document == null ||
                args == null ||
                !IsPlotCommand(args.GlobalCommandName))
                return;

            string folder = DrawingFolder(document);
            if (string.IsNullOrWhiteSpace(folder))
                return;

            ApplyDrawingFolder(document);

            var state = new PlotState
            {
                Folder = folder,
                StartedUtc = DateTime.UtcNow,
                Waiting = true
            };
            try
            {
                foreach (string file in
                    Directory.GetFiles(
                        folder,
                        "*.pdf",
                        SearchOption.TopDirectoryOnly))
                {
                    state.Baseline[file] =
                        File.GetLastWriteTimeUtc(file);
                }
            }
            catch { }
            States[document] = state;
        }

        private static void OnCommandEnded(
            object sender,
            CommandEventArgs args)
        {
            Document document = sender as Document;
            PlotState state;
            if (document == null ||
                args == null ||
                !IsPlotCommand(args.GlobalCommandName) ||
                !States.TryGetValue(document, out state) ||
                state == null ||
                !state.Waiting)
                return;

            state.Waiting = false;
            string folder = state.Folder;
            DateTime started = state.StartedUtc;
            Dictionary<string, DateTime> baseline =
                state.Baseline.ToDictionary(
                    item => item.Key,
                    item => item.Value,
                    StringComparer.OrdinalIgnoreCase);

            Task.Run(delegate
            {
                string pdf = WaitForPdf(
                    folder,
                    started,
                    baseline);
                if (string.IsNullOrWhiteSpace(pdf))
                    return;
                try
                {
                    Process.Start(
                        new ProcessStartInfo(pdf)
                        {
                            UseShellExecute = true
                        });
                }
                catch { }
            });
        }

        private static void OnCommandCancelled(
            object sender,
            CommandEventArgs args)
        {
            Document document = sender as Document;
            PlotState state;
            if (document != null &&
                States.TryGetValue(document, out state) &&
                state != null)
                state.Waiting = false;
        }

        private static string WaitForPdf(
            string folder,
            DateTime startedUtc,
            IDictionary<string, DateTime> baseline)
        {
            for (int attempt = 0;
                 attempt < 40;
                 attempt++)
            {
                try
                {
                    string candidate = Directory
                        .GetFiles(
                            folder,
                            "*.pdf",
                            SearchOption.TopDirectoryOnly)
                        .Select(file => new
                        {
                            File = file,
                            Time =
                                File.GetLastWriteTimeUtc(file)
                        })
                        .Where(item =>
                        {
                            DateTime before =
                                DateTime.MinValue;
                            bool existed =
                                baseline != null &&
                                baseline.TryGetValue(
                                    item.File,
                                    out before);
                            return !existed ||
                                item.Time >
                                    before.AddMilliseconds(50);
                        })
                        .OrderByDescending(item => item.Time)
                        .Select(item => item.File)
                        .FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(candidate))
                    {
                        // Give the PDF driver a moment to release its file handle.
                        Thread.Sleep(350);
                        return candidate;
                    }
                }
                catch { }
                Thread.Sleep(500);
            }
            return string.Empty;
        }

        private static bool IsPlotCommand(string value)
        {
            string command =
                (value ?? string.Empty)
                    .Trim()
                    .TrimStart('_', '.', '-')
                    .ToUpperInvariant();
            return command == "PLOT" ||
                command == "PRINT";
        }

        private static string DrawingFolder(
            Document document)
        {
            if (document == null)
                return string.Empty;

            string filename = string.Empty;
            try
            {
                filename =
                    document.Database.Filename;
            }
            catch { }
            if (string.IsNullOrWhiteSpace(filename))
            {
                try { filename = document.Name; }
                catch { }
            }

            if (string.IsNullOrWhiteSpace(filename) ||
                !Path.IsPathRooted(filename))
                return string.Empty;

            string folder =
                Path.GetDirectoryName(filename);
            return !string.IsNullOrWhiteSpace(folder) &&
                Directory.Exists(folder)
                    ? folder
                    : string.Empty;
        }

        private static void ApplyDrawingFolder(
            Document document)
        {
            string folder =
                DrawingFolder(document);
            if (string.IsNullOrWhiteSpace(folder))
                return;

            // Civil 3D / AutoCAD 2023 exposes the current plot-to-file folder
            // through the PLOTTOFILEPATH system variable. Do not depend on
            // Application.AcadApplication here: that COM bridge is not exposed
            // by the referenced 2023 managed Application type in all installs.
            try
            {
                AcApplication.SetSystemVariable(
                    "PLOTTOFILEPATH",
                    folder);
            }
            catch
            {
                // Plotting remains available even when a host build does not
                // expose this system variable.
            }
        }
    }
}

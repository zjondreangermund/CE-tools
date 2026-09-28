using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using Autodesk.Civil.DatabaseServices.Styles;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;

[assembly: CommandClass(typeof(CETools.Civil3D.September14AlignmentBandStyleCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// September 14 field fixes for applying Civil 3D alignment label sets and
    /// road profile-view band sets to several existing objects in one operation.
    /// </summary>
    public sealed class September14AlignmentBandStyleCommands
    {
        [CommandMethod("CE_TOOLS", "CE_ALIGNLABELSETMULTI", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void ApplyAlignmentLabelSetToMultiple()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (civilDocument == null)
            {
                document.Editor.WriteMessage("\nCE_ALIGNLABELSETMULTI requires an active Civil 3D drawing.");
                return;
            }

            List<StyleChoice> styles = ReadAlignmentLabelSetStyles(document.Database, civilDocument);
            if (styles.Count == 0)
            {
                document.Editor.WriteMessage("\nNo Civil 3D alignment label set styles exist in this drawing.");
                return;
            }

            StyleChoice choice = ChooseStyle(
                document,
                "CE Tools - Alignment Label Sets",
                "Apply one existing Civil 3D alignment label set style to every selected alignment.",
                "Alignment label set",
                styles);
            if (choice == null) return;

            List<StyleChoice> alignmentStyles = ReadAlignmentStyles(document.Database, civilDocument);
            StyleChoice alignmentStyle = alignmentStyles.Count == 0
                ? null
                : ChooseStyle(
                    document,
                    "CE Tools - Alignment Styles",
                    "Optionally apply an existing alignment display style together with the label set.",
                    "Alignment style",
                    alignmentStyles);
            if (alignmentStyles.Count > 0 && alignmentStyle == null) return;

            var intervalSettings = new ProductionSettingsDialogModel(
                "CE Tools - Alignment Label Intervals",
                "Set the station-label intervals after importing the selected label set. Unsupported label groups are left unchanged.");
            intervalSettings.AddPositiveDouble("Major", "01 Intervals", "Major label interval", 20.0, "Major station label spacing in drawing units.");
            intervalSettings.AddPositiveDouble("Minor", "01 Intervals", "Minor label interval", 5.0, "Minor station label spacing in drawing units.");
            if (!DisciplineWorkflowDialogs.EditSettings(intervalSettings)) return;
            double majorInterval = intervalSettings.Double("Major", 20.0);
            double minorInterval = intervalSettings.Double("Minor", 5.0);

            PromptSelectionResult selection = GetSelection(
                document.Editor,
                "\nSelect one or more Civil 3D alignments to receive the label set: ");
            if (selection.Status != PromptStatus.OK) return;

            int applied = 0;
            int skipped = 0;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject selected in selection.Value)
                {
                    if (selected == null)
                    {
                        skipped++;
                        continue;
                    }

                    CivilAlignment alignment = transaction.GetObject(
                        selected.ObjectId,
                        OpenMode.ForWrite,
                        false) as CivilAlignment;
                    if (alignment == null || alignment.IsReferenceObject)
                    {
                        skipped++;
                        continue;
                    }

                    alignment.ImportLabelSet(choice.Id);
                    if (alignmentStyle != null) alignment.StyleId = alignmentStyle.Id;
                    ApplyLabelIntervals(alignment, majorInterval, minorInterval);
                    applied++;
                }

                transaction.Commit();
            }

            document.Editor.WriteMessage(
                "\nCE_ALIGNLABELSETMULTI complete. Label set '{0}', alignment style and major/minor intervals applied to {1} alignment(s); skipped {2} non-editable/non-alignment object(s).",
                choice.Name,
                applied,
                skipped);
        }

        [CommandMethod("CE_TOOLS", "CE_ROADBANDLABELS", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void ApplyRoadBandSetAndShowLabels()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (civilDocument == null)
            {
                document.Editor.WriteMessage("\nCE_ROADBANDLABELS requires an active Civil 3D drawing.");
                return;
            }

            List<StyleChoice> styles = ReadProfileViewBandSetStyles(document.Database, civilDocument);
            if (styles.Count == 0)
            {
                document.Editor.WriteMessage("\nNo Civil 3D profile-view band set styles exist in this drawing.");
                return;
            }

            PromptSelectionResult selection = GetSelection(
                document.Editor,
                "\nSelect one or more Civil 3D profile views to receive the band set: ");
            if (selection.Status != PromptStatus.OK) return;

            List<ObjectId> selectedProfileViewIds = ReadProfileViewSelection(
                document.Database,
                selection);
            if (selectedProfileViewIds.Count == 0)
            {
                document.Editor.WriteMessage(
                    "\nCE_ROADBANDLABELS: the selection contains no editable Civil 3D profile views.");
                return;
            }

            ProfileViewBandImportDialog batchDialog = ProfileViewBandImportDialog.Show(
                styles.Select(item => item.Name).ToList(),
                selectedProfileViewIds.Select(id => "Profile View " + id.Handle.ToString()).ToList());
            if (batchDialog == null) return;

            StyleChoice choice = styles.FirstOrDefault(item =>
                string.Equals(item.Name, batchDialog.SelectedStyleName, StringComparison.CurrentCultureIgnoreCase));
            if (choice == null)
            {
                document.Editor.WriteMessage("\nThe selected Civil 3D band set could not be resolved.");
                return;
            }

            int applied = 0;
            int bandsEnabled = 0;
            int bandItemsLinked = 0;
            int bandLinkWarnings = 0;
            int roadSourceProfilesAlreadyInView = 0;
            int skipped = 0;
            int failed = 0;
            var profileViewIds = new List<ObjectId>();
            var seen = new HashSet<ObjectId>();
            using (DocumentLock documentLock = document.LockDocument())
            {
                foreach (ObjectId selectedId in selectedProfileViewIds)
                {
                    if (selectedId.IsNull || !seen.Add(selectedId))
                    {
                        continue;
                    }

                    try
                    {
                        // Import and commit each view independently. Civil 3D 2023
                        // materialises the native band-item collection on commit;
                        // importing a whole selection in one transaction can leave
                        // later views with band rows but no visible labels.
                        using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                        {
                            ProfileView profileView = transaction.GetObject(
                                selectedId,
                                OpenMode.ForWrite,
                                false) as ProfileView;
                            if (profileView == null || profileView.IsReferenceObject)
                            {
                                skipped++;
                                continue;
                            }

                            profileView.Bands.ImportBandSetStyle(choice.Id);
                            ProfileViewBandSetStyle bandSetStyle = transaction.GetObject(
                                choice.Id, OpenMode.ForRead, false) as ProfileViewBandSetStyle;
                            if (bandSetStyle == null ||
                                EnsureImportedBandRows(profileView, bandSetStyle) == 0)
                                throw new InvalidOperationException(
                                    "The selected band set has no materialized band rows on this profile view.");
                            try { profileView.RecordGraphicsModified(true); } catch { }
                            transaction.Commit();
                        }

                        // A setting/style assignment is not evidence of a band on
                        // the graph. Reopen the committed view and count its rows.
                        using (Transaction verify = document.Database.TransactionManager.StartTransaction())
                        {
                            ProfileView committed = verify.GetObject(
                                selectedId, OpenMode.ForRead, false) as ProfileView;
                            if (committed == null || CountBandRows(committed) == 0)
                                throw new InvalidOperationException(
                                    "No band rows persisted after the import transaction committed.");
                        }
                        profileViewIds.Add(selectedId);
                        applied++;
                        // Civil 3D 2023 materializes band graphics during Regen.
                        // Flush each imported view before editing the next one,
                        // as happens when the native dialog is used individually.
                        try
                        {
                            document.Database.TransactionManager.QueueForGraphicsFlush();
                            document.Editor.Regen();
                        }
                        catch { }
                    }
                    catch (System.Exception exception)
                    {
                        failed++;
                        document.Editor.WriteMessage(
                            "\nCE_ROADBANDLABELS could not import the band set to profile view {0}: {1}",
                            selectedId.Handle,
                            exception.Message);
                    }
                }

                // Commit compatibility profiles before they are referenced by
                // the band collections. Civil 3D can reject a source created in
                // the same transaction as SetBottomBandItems, even though the
                // profile object itself is readable in that transaction.
                foreach (ObjectId id in profileViewIds)
                {
                    try
                    {
                        using (Transaction prepare = document.Database.TransactionManager.StartTransaction())
                        {
                            ProfileView view = prepare.GetObject(id, OpenMode.ForWrite, false) as ProfileView;
                            CivilAlignment alignment = view == null || view.AlignmentId.IsNull
                                ? null : prepare.GetObject(view.AlignmentId, OpenMode.ForRead, false) as CivilAlignment;
                            if (alignment != null)
                            {
                                ObjectId ground, left, centre, right, final;
                                August13RoadProfileViewFinalizerCommands.ResolveRoadProfiles(
                                    alignment, prepare, out ground, out left, out centre, out right, out final);
                                ProfileViewBandDataBinder.PrepareRoadBandSources(
                                    view, ground, left, centre, right, final);
                            }
                            prepare.Commit();
                        }
                    }
                    catch (System.Exception exception)
                    {
                        document.Editor.WriteMessage(
                            "\nCE_ROADBANDLABELS: source preparation for view {0}: {1}",
                            id.Handle, exception.Message);
                    }
                }

                // Run a second committed pass after the import commits. Bind each
                // imported band's profile source by its role (ground, left, centre,
                // right or final design), then enable labels on both band rows.
                foreach (ObjectId id in profileViewIds)
                {
                    try
                    {
                        using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                        {
                            ProfileView profileView = transaction.GetObject(
                                id,
                                OpenMode.ForWrite,
                                false) as ProfileView;
                            if (profileView == null || profileView.IsReferenceObject)
                            {
                                skipped++;
                                continue;
                            }

                            int localLinked = 0;
                            int localWarnings = 0;
                            try
                            {
                                CivilAlignment alignment = profileView.AlignmentId.IsNull
                                    ? null
                                    : transaction.GetObject(
                                        profileView.AlignmentId,
                                        OpenMode.ForRead,
                                        false) as CivilAlignment;
                                if (alignment != null)
                                {
                                    ObjectId groundProfileId;
                                    ObjectId leftProfileId;
                                    ObjectId centreProfileId;
                                    ObjectId rightProfileId;
                                    ObjectId finalProfileId;
                                    August13RoadProfileViewFinalizerCommands.ResolveRoadProfiles(
                                        alignment,
                                        transaction,
                                        out groundProfileId,
                                        out leftProfileId,
                                        out centreProfileId,
                                        out rightProfileId,
                                        out finalProfileId);
                                    roadSourceProfilesAlreadyInView +=
                                        ProfileViewBandDataBinder.CountRoadSourceProfilesAlreadyInView(
                                            profileView,
                                            groundProfileId,
                                            leftProfileId,
                                            centreProfileId,
                                            rightProfileId,
                                            finalProfileId);
                                    int localLinkWarnings;
                                    localLinked = ProfileViewBandDataBinder.BindRoad(
                                        profileView,
                                        groundProfileId,
                                        leftProfileId,
                                        centreProfileId,
                                        rightProfileId,
                                        finalProfileId,
                                        out localLinkWarnings);
                                    localWarnings += localLinkWarnings;
                                }
                                else
                                    localWarnings++;
                            }
                            catch (System.Exception exception)
                            {
                                throw new InvalidOperationException("Band source binding failed: " + exception.Message, exception);
                            }

                            int localEnabled = batchDialog.ShowLabels
                                ? EnableBandLabels(profileView)
                                : 0;
                            try { profileView.RecordGraphicsModified(true); } catch { }
                            transaction.Commit();
                            bandItemsLinked += localLinked;
                            bandLinkWarnings += localWarnings;
                            bandsEnabled += localEnabled;
                        }
                        try
                        {
                            document.Database.TransactionManager.QueueForGraphicsFlush();
                            document.Editor.Regen();
                        }
                        catch { }
                    }
                    catch (System.Exception exception)
                    {
                        failed++;
                        document.Editor.WriteMessage(
                            "\nCE_ROADBANDLABELS could not finish profile view {0}: {1}",
                            id.Handle,
                            exception.Message);
                    }
                }
            }
            try
            {
                document.Database.TransactionManager.QueueForGraphicsFlush();
                document.Editor.Regen();
                AcApplication.UpdateScreen();
            }
            catch { }

            int nativeBandLabelValues = 0;
            int bandStylesWithoutLabelComponents = 0;
            int viewsWithNativeLabels = 0;
            foreach (ObjectId id in profileViewIds)
            {
                try
                {
                    using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                    {
                        ProfileView profileView = transaction.GetObject(
                            id, OpenMode.ForRead, false) as ProfileView;
                        if (profileView != null)
                        {
                            int localNativeLabels =
                                ProfileViewBandDataBinder.CountProfileDataBandLabelSubentities(
                                    profileView, transaction);
                            nativeBandLabelValues += localNativeLabels;
                            if (localNativeLabels > 0) viewsWithNativeLabels++;
                            else document.Editor.WriteMessage(
                                "\nCE_ROADBANDLABELS: profile view {0} still has no native profile-band label values.",
                                id.Handle);
                            bandStylesWithoutLabelComponents +=
                                ProfileViewBandDataBinder.CountBandStylesWithoutLabelComponents(
                                    profileView, transaction);
                        }
                        transaction.Commit();
                    }
                }
                catch { }
            }

            document.Editor.WriteMessage(
                "\nCE_ROADBANDLABELS complete. Band set '{0}' imported to {1} profile view(s); verified band sources={2}; labels on={3}; native profile-band label values={4}; styles without label components={5}; source-link warnings={6}; skipped={7}; failed={8}; road source profiles already in graph={9}.",
                choice.Name,
                applied,
                bandItemsLinked,
                bandsEnabled,
                nativeBandLabelValues,
                bandStylesWithoutLabelComponents,
                bandLinkWarnings,
                skipped,
                failed,
                roadSourceProfilesAlreadyInView);
            document.Editor.WriteMessage(
                "\nCE_ROADBANDLABELS: profile views with native label values={0}/{1}.",
                viewsWithNativeLabels, profileViewIds.Count);
            if (nativeBandLabelValues == 0)
            {
                document.Editor.WriteMessage(
                    "\nCivil 3D generated no native profile-band label values. Check each band's Profile 1/Profile 2 source and its band style label components; labels enabled alone does not confirm label values exist.");
            }
            if (bandLinkWarnings > 0)
            {
                document.Editor.WriteMessage(
                    "\n{0} band source assignment(s) did not verify after write. Review the Profile 1/Profile 2 source fields on the selected profile view bands.",
                    bandLinkWarnings);
            }
            if (roadSourceProfilesAlreadyInView > 0)
            {
                document.Editor.WriteMessage(
                    "\nBand sources were saved through the native top/bottom band collections. Original profiles are preferred; compatibility copies are used only when source assignment fails.");
            }

            if (batchDialog.OpenNativeDialog && profileViewIds.Count > 0)
            {
                try
                {
                    // Civil 3D's native command edits one view at a time. Open it
                    // after the committed batch so the operator can inspect the
                    // exact Bands tab shown in the native Profile View Properties
                    // window without interrupting the multi-view operation.
                    document.Editor.SetImpliedSelection(new[] { profileViewIds[0] });
                    document.SendStringToExecute("_.EditGraphProperties ", true, false, true);
                }
                catch (System.Exception exception)
                {
                    document.Editor.WriteMessage(
                        "\nCE_ROADBANDLABELS could not open native Profile View Properties: {0}",
                        exception.Message);
                }
            }
        }

        private static int EnableBandLabels(ProfileView profileView)
        {
            int found;
            int enabled;
            ProfileViewBandPersistence.EnableLabels(profileView, out found, out enabled);
            return enabled;
        }

        internal static int EnsureImportedBandRows(
            ProfileView view,
            ProfileViewBandSetStyle style)
        {
            // ImportBandSetStyle normally populates both collections. On some
            // Civil 3D drawings it only assigns the style: explicitly add the
            // missing rows using the style's actual band style ObjectIds.
            using (ProfileViewBandItemCollection top = view.Bands.GetTopBandItems())
            {
                if (top.Count == 0)
                {
                    var templates = style.GetTopBandSetItems();
                    for (int index = 0; index < templates.Count; index++)
                    {
                        top.Add(templates[index].BandStyleId);
                        CopyBandRow(top[top.Count - 1], templates[index]);
                    }
                    if (top.Count > 0) view.Bands.SetTopBandItems(top);
                }
            }
            using (ProfileViewBandItemCollection bottom = view.Bands.GetBottomBandItems())
            {
                if (bottom.Count == 0)
                {
                    var templates = style.GetBottomBandSetItems();
                    for (int index = 0; index < templates.Count; index++)
                    {
                        bottom.Add(templates[index].BandStyleId);
                        CopyBandRow(bottom[bottom.Count - 1], templates[index]);
                    }
                    if (bottom.Count > 0) view.Bands.SetBottomBandItems(bottom);
                }
            }
            return CountBandRows(view);
        }

        private static void CopyBandRow(
            ProfileViewBandItem target,
            ProfileViewBandSetItem source)
        {
            target.Gap = source.Gap;
            target.MajorInterval = source.MajorInterval;
            target.MinorInterval = source.MinorInterval;
            target.ShowLabels = source.ShowLabels;
            target.LabelAtStartStation = source.LabelAtStartStation;
            target.LabelAtEndStation = source.LabelAtEndStation;
            target.Weeding = source.Weeding;
        }

        internal static int CountBandRows(ProfileView view)
        {
            using (ProfileViewBandItemCollection top = view.Bands.GetTopBandItems())
            using (ProfileViewBandItemCollection bottom = view.Bands.GetBottomBandItems())
                return top.Count + bottom.Count;
        }

        private static List<StyleChoice> ReadAlignmentLabelSetStyles(Database database, CivilDocument civilDocument)
        {
            var result = new List<StyleChoice>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                AlignmentLabelSetStyleCollection collection = civilDocument.Styles.LabelSetStyles.AlignmentLabelSetStyles;
                for (int index = 0; index < collection.Count; index++)
                {
                    ObjectId id = collection[index];
                    AlignmentLabelSetStyle style = transaction.GetObject(id, OpenMode.ForRead, false) as AlignmentLabelSetStyle;
                    if (style != null && !string.IsNullOrWhiteSpace(style.Name))
                        result.Add(new StyleChoice(style.Name, id));
                }
            }
            result.Sort((left, right) => StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name));
            return result;
        }

        private static List<StyleChoice> ReadProfileViewBandSetStyles(Database database, CivilDocument civilDocument)
        {
            var result = new List<StyleChoice>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                ProfileViewBandSetStyleCollection collection = civilDocument.Styles.ProfileViewBandSetStyles;
                for (int index = 0; index < collection.Count; index++)
                {
                    ObjectId id = collection[index];
                    ProfileViewBandSetStyle style = transaction.GetObject(id, OpenMode.ForRead, false) as ProfileViewBandSetStyle;
                    if (style != null && !string.IsNullOrWhiteSpace(style.Name))
                        result.Add(new StyleChoice(style.Name, id));
                }
            }
            result.Sort((left, right) => StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name));
            return result;
        }

        private static List<StyleChoice> ReadAlignmentStyles(Database database, CivilDocument civilDocument)
        {
            var result = new List<StyleChoice>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                AlignmentStyleCollection collection = civilDocument.Styles.AlignmentStyles;
                for (int index = 0; index < collection.Count; index++)
                {
                    ObjectId id = collection[index];
                    AlignmentStyle style = transaction.GetObject(id, OpenMode.ForRead, false) as AlignmentStyle;
                    if (style != null && !string.IsNullOrWhiteSpace(style.Name))
                        result.Add(new StyleChoice(style.Name, id));
                }
            }
            result.Sort((left, right) => StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name));
            return result;
        }

        private static void ApplyLabelIntervals(CivilAlignment alignment, double major, double minor)
        {
            MethodInfo getter = alignment.GetType().GetMethod("GetLabelGroupIds", Type.EmptyTypes);
            object value = getter == null ? null : getter.Invoke(alignment, null);
            IEnumerable<ObjectId> ids = value as IEnumerable<ObjectId>;
            if (ids == null) return;
            Transaction transaction = alignment.Database.TransactionManager.TopTransaction;
            if (transaction == null) return;
            foreach (ObjectId id in ids)
            {
                DBObject group = transaction.GetObject(id, OpenMode.ForWrite, false);
                string name = group == null ? string.Empty : group.GetType().Name;
                double interval = name.IndexOf("Minor", StringComparison.OrdinalIgnoreCase) >= 0 ? minor : major;
                foreach (string propertyName in new[] { "Increment", "StationIncrement", "MajorStationInterval", "MinorStationInterval" })
                {
                    PropertyInfo property = group == null ? null : group.GetType().GetProperty(propertyName);
                    if (property != null && property.CanWrite && property.PropertyType == typeof(double))
                    {
                        try { property.SetValue(group, interval, null); } catch { }
                    }
                }
            }
        }

        private static StyleChoice ChooseStyle(
            Document document,
            string title,
            string description,
            string fieldLabel,
            IList<StyleChoice> styles)
        {
            var names = new List<string>();
            foreach (StyleChoice style in styles) names.Add(style.Name);

            var model = new ProductionSettingsDialogModel(title, description);
            model.AddChoice(
                "Style",
                "01 Style",
                fieldLabel,
                names[0],
                "Choose an existing Civil 3D style from the active drawing.",
                names);
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return null;

            string selectedName = model.Text("Style");
            foreach (StyleChoice style in styles)
            {
                if (string.Equals(style.Name, selectedName, StringComparison.CurrentCultureIgnoreCase))
                    return style;
            }

            document.Editor.WriteMessage("\nThe selected Civil 3D style could not be resolved.");
            return null;
        }

        private static PromptSelectionResult GetSelection(Editor editor, string message)
        {
            PromptSelectionResult implied = editor.SelectImplied();
            if (implied.Status == PromptStatus.OK && implied.Value != null && implied.Value.Count > 0)
            {
                editor.SetImpliedSelection(new ObjectId[0]);
                return implied;
            }

            PromptSelectionResult prompted = editor.GetSelection(new PromptSelectionOptions
            {
                MessageForAdding = message,
                AllowDuplicates = false,
                RejectObjectsFromNonCurrentSpace = true
            });
            if (prompted.Status == PromptStatus.OK &&
                prompted.Value != null &&
                prompted.Value.Count > 0)
                return prompted;

            try
            {
                MethodInfo method = editor.GetType().GetMethod(
                    "SelectPrevious",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    Type.EmptyTypes,
                    null);
                PromptSelectionResult previous = method == null
                    ? null
                    : method.Invoke(editor, null) as PromptSelectionResult;
                if (previous != null &&
                    previous.Status == PromptStatus.OK &&
                    previous.Value != null &&
                    previous.Value.Count > 0)
                    return previous;
            }
            catch { }

            return prompted;
        }

        private static List<ObjectId> ReadProfileViewSelection(
            Database database,
            PromptSelectionResult selection)
        {
            var result = new List<ObjectId>();
            if (database == null || selection == null || selection.Value == null)
                return result;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject selected in selection.Value)
                {
                    if (selected == null || selected.ObjectId.IsNull || selected.ObjectId.IsErased)
                        continue;
                    try
                    {
                        ProfileView view = transaction.GetObject(
                            selected.ObjectId,
                            OpenMode.ForRead,
                            false) as ProfileView;
                        if (view != null && !view.IsReferenceObject)
                            result.Add(selected.ObjectId);
                    }
                    catch { }
                }
            }
            return result.Distinct().ToList();
        }

        private sealed class StyleChoice
        {
            internal StyleChoice(string name, ObjectId id)
            {
                Name = name;
                Id = id;
            }

            internal string Name { get; }
            internal ObjectId Id { get; }
        }
    }
}

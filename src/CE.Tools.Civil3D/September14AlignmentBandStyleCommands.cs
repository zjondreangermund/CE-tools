using System;
using System.Collections.Generic;
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

            StyleChoice choice = ChooseStyle(
                document,
                "CE Tools - Road Profile Band Sets",
                "Apply one existing profile-view band set to multiple road profile views and force each imported top/bottom band item to Show Labels.",
                "Profile-view band set",
                styles);
            if (choice == null) return;

            PromptSelectionResult selection = GetSelection(
                document.Editor,
                "\nSelect one or more Civil 3D profile views to receive the band set: ");
            if (selection.Status != PromptStatus.OK) return;

            int applied = 0;
            int bandsEnabled = 0;
            int skipped = 0;
            var profileViewIds = new List<ObjectId>();
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject selected in selection.Value)
                {
                    if (selected == null)
                    {
                        skipped++;
                        continue;
                    }

                    ProfileView profileView = transaction.GetObject(
                        selected.ObjectId,
                        OpenMode.ForWrite,
                        false) as ProfileView;
                    if (profileView == null || profileView.IsReferenceObject)
                    {
                        skipped++;
                        continue;
                    }

                    profileView.Bands.ImportBandSetStyle(choice.Id);
                    profileViewIds.Add(profileView.ObjectId);
                    applied++;
                }

                transaction.Commit();
            }

            // Civil 3D materialises imported band items only after the import
            // transaction commits.  Reopen the views before changing ShowLabels;
            // doing both in one transaction leaves the labels hidden until the
            // user manually re-imports the band set in Profile View Properties.
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in profileViewIds)
                {
                    ProfileView profileView = transaction.GetObject(id, OpenMode.ForWrite, false) as ProfileView;
                    if (profileView == null || profileView.IsReferenceObject) continue;
                    bandsEnabled += EnableBandLabels(profileView);
                    try { profileView.RecordGraphicsModified(true); } catch { }
                }
                transaction.Commit();
            }
            try
            {
                document.Database.TransactionManager.QueueForGraphicsFlush();
                document.Editor.Regen();
                AcApplication.UpdateScreen();
            }
            catch { }

            document.Editor.WriteMessage(
                "\nCE_ROADBANDLABELS complete. Band set '{0}' applied to {1} profile view(s); Show Labels enabled on {2} band item(s); skipped {3} non-editable/non-profile-view object(s).",
                choice.Name,
                applied,
                bandsEnabled,
                skipped);
        }

        private static int EnableBandLabels(ProfileView profileView)
        {
            int changed = 0;

            using (ProfileViewBandItemCollection top = profileView.Bands.GetTopBandItems())
            {
                for (int index = 0; index < top.Count; index++)
                {
                    ProfileViewBandItem item = top[index];
                    if (!item.ShowLabels)
                    {
                        item.ShowLabels = true;
                        changed++;
                    }
                }
                // Do not write the collection back: Civil 3D 2023 can return
                // a read-only band collection and abort with eNotOpenForWrite.
                // Individual ProfileViewBandItem.ShowLabels writes commit
                // through the owning ProfileView transaction.
            }

            using (ProfileViewBandItemCollection bottom = profileView.Bands.GetBottomBandItems())
            {
                for (int index = 0; index < bottom.Count; index++)
                {
                    ProfileViewBandItem item = bottom[index];
                    if (!item.ShowLabels)
                    {
                        item.ShowLabels = true;
                        changed++;
                    }
                }
                // See the top-band note above; the individual item writes are
                // sufficient and avoid the collection-level setter.
            }

            return changed;
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

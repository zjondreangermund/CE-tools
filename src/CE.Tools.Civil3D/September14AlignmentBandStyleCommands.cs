using System;
using System.Collections.Generic;
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
                    applied++;
                }

                transaction.Commit();
            }

            document.Editor.WriteMessage(
                "\nCE_ALIGNLABELSETMULTI complete. Label set '{0}' applied to {1} alignment(s); skipped {2} non-editable/non-alignment object(s).",
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
                    bandsEnabled += EnableBandLabels(profileView);
                    applied++;
                }

                transaction.Commit();
            }

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
                profileView.Bands.SetTopBandItems(top);
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
                profileView.Bands.SetBottomBandItems(bottom);
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

            return editor.GetSelection(new PromptSelectionOptions
            {
                MessageForAdding = message,
                AllowDuplicates = false,
                RejectObjectsFromNonCurrentSpace = true
            });
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

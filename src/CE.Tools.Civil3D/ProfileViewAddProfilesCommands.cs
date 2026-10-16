#if CE_CIVIL3D_2024
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivilProfile = Autodesk.Civil.DatabaseServices.Profile;
using CivilProfileView = Autodesk.Civil.DatabaseServices.ProfileView;

[assembly: CommandClass(typeof(CETools.Civil3D.ProfileViewAddProfilesCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Batch-add existing Civil 3D profiles to All or Selected profile views.
    /// Each profile is only eligible for views of its own parent alignment.
    /// Does not create profiles, change band styles, change existing view ranges,
    /// or disturb profiles that are already included.
    /// </summary>
    public sealed class ProfileViewAddProfilesCommands
    {
        [CommandMethod("CE_TOOLS", "CE_PROFILEADDTOVIEWS",
            CommandFlags.Modal | CommandFlags.Redraw | CommandFlags.UsePickSet)]
        public void AddProfilesToViews()
        {
            Document doc = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civil = CivilApplication.ActiveDocument;
            if (doc == null || civil == null) return;

            var choice = new PromptKeywordOptions(
                "\nAdd profiles to profile views [All/Selected] <Selected>: ");
            choice.Keywords.Add("All");
            choice.Keywords.Add("Selected");
            choice.Keywords.Default = "Selected";
            choice.AllowNone = true;
            PromptResult mode = doc.Editor.GetKeywords(choice);
            if (mode.Status == PromptStatus.Cancel ||
                mode.Status == PromptStatus.Error) return;
            bool all = string.Equals(mode.StringResult,
                "All", StringComparison.OrdinalIgnoreCase);

            HashSet<ObjectId> requested = null;
            if (!all)
            {
                PromptSelectionResult selection = doc.Editor.SelectImplied();
                if (selection.Status != PromptStatus.OK ||
                    selection.Value == null || selection.Value.Count == 0)
                    selection = doc.Editor.GetSelection(
                        new PromptSelectionOptions
                        {
                            MessageForAdding =
                                "\nSelect MULTIPLE existing profile views: ",
                            RejectObjectsFromNonCurrentSpace = true,
                            AllowDuplicates = false
                        });
                doc.Editor.SetImpliedSelection(new ObjectId[0]);
                if (selection.Status != PromptStatus.OK ||
                    selection.Value == null) return;
                requested = new HashSet<ObjectId>(
                    selection.Value.GetObjectIds());
            }

            var views = new List<BatchView>();
            var profiles = new List<BatchProfile>();
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                BlockTable bt = tr.GetObject(
                    doc.Database.BlockTableId, OpenMode.ForRead) as BlockTable;
                if (bt == null) return;
                BlockTableRecord model = tr.GetObject(
                    bt[BlockTableRecord.ModelSpace],
                    OpenMode.ForRead) as BlockTableRecord;
                if (model == null) return;
                var selectedAlignments = new HashSet<ObjectId>();
                foreach (ObjectId id in model)
                {
                    if (requested != null && !requested.Contains(id)) continue;
                    CivilProfileView view;
                    try { view = tr.GetObject(id, OpenMode.ForRead, false) as CivilProfileView; }
                    catch { continue; }
                    if (view == null) continue;
                    ObjectId alignmentId = ReadObjectId(view, "AlignmentId");
                    if (alignmentId.IsNull)
                        alignmentId = ReadObjectId(view, "ParentAlignmentId");
                    if (alignmentId.IsNull) continue;
                    CivilAlignment alignment;
                    try { alignment = tr.GetObject(
                        alignmentId, OpenMode.ForRead, false) as CivilAlignment; }
                    catch { continue; }
                    if (alignment == null) continue;
                    views.Add(new BatchView {
                        ViewId = id, AlignmentId = alignmentId,
                        Name = view.Name, AlignmentName = alignment.Name
                    });
                    selectedAlignments.Add(alignmentId);
                }
                foreach (ObjectId alignmentId in selectedAlignments)
                {
                    CivilAlignment alignment = tr.GetObject(
                        alignmentId, OpenMode.ForRead, false) as CivilAlignment;
                    if (alignment == null) continue;
                    foreach (ObjectId profileId in alignment.GetProfileIds())
                    {
                        CivilProfile profile;
                        try { profile = tr.GetObject(
                            profileId, OpenMode.ForRead, false) as CivilProfile; }
                        catch { continue; }
                        if (profile == null) continue;
                        profiles.Add(new BatchProfile {
                            ProfileId = profileId,
                            AlignmentId = alignmentId,
                            Display = alignment.Name + " / " + profile.Name
                        });
                    }
                }
                tr.Commit();
            }
            if (views.Count == 0 || profiles.Count == 0)
            {
                doc.Editor.WriteMessage(
                    "\nCE_PROFILEADDTOVIEWS: No writable/compatible profile views " +
                    "or profiles found. Views must belong to Civil 3D alignments.");
                return;
            }

            // Select actual profile objects from the drawing, rather than
            // blindly putting every NG/design/edge profile into every view.
            List<ObjectId> selectedProfiles;
            using (var window = new BatchProfilePicker(profiles, views.Count))
            {
                if (window.ShowDialog() != DialogResult.OK) return;
                selectedProfiles = window.SelectedProfileIds;
            }
            if (selectedProfiles.Count == 0)
            {
                doc.Editor.WriteMessage(
                    "\nCE_PROFILEADDTOVIEWS: No profiles selected; drawing unchanged.");
                return;
            }
            var selectedSet = new HashSet<ObjectId>(selectedProfiles);
            int updated = 0, added = 0, already = 0, unsupported = 0, failed = 0;
            var problems = new List<string>();
            using (doc.LockDocument())
            {
                // Use an independent transaction for each view so one rejected
                // profile does not undo unrelated successful views.
                foreach (BatchView info in views)
                {
                    List<ObjectId> matching = profiles.Where(p =>
                        p.AlignmentId == info.AlignmentId &&
                        selectedSet.Contains(p.ProfileId))
                        .Select(p => p.ProfileId).Distinct().ToList();
                    if (matching.Count == 0) continue;
                    try
                    {
                        using (Transaction tr =
                            doc.Database.TransactionManager.StartTransaction())
                        {
                            CivilProfileView view = tr.GetObject(
                                info.ViewId, OpenMode.ForWrite, false)
                                as CivilProfileView;
                            if (view == null || view.IsReferenceObject)
                            { failed++; continue; }
                            int changes = 0;
                            foreach (ObjectId profileId in matching)
                            {
                                bool wasAlreadyDrawn;
                                // Autodesk's ProfileView.GraphOverrides is the
                                // Profiles tab. Existing alignment profiles are
                                // already represented there; the Draw checkbox
                                // controls visibility. Avoid duplicating rows.
                                if (TryEnableProfileInView(
                                        view, profileId, out wasAlreadyDrawn))
                                {
                                    if (wasAlreadyDrawn) already++;
                                    else { changes++; added++; }
                                }
                                else
                                {
                                    unsupported++;
                                    problems.Add(info.Name +
                                        ": profile not found in native Profiles tab " +
                                        profileId.Handle.ToString());
                                }
                            }
                            tr.Commit();
                            if (changes > 0) updated++;
                        }
                    }
                    catch (System.Exception exception)
                    {
                        failed++;
                        problems.Add(info.Name + ": " + exception.Message);
                    }
                }
            }
            doc.Editor.WriteMessage(
                "\nCE_PROFILEADDTOVIEWS complete. Views selected={0}; " +
                "views updated={1}; profiles added={2}; already shown={3}; " +
                "unsupported={4}; failed views={5}.",
                views.Count, updated, added, already, unsupported, failed);
            foreach (string problem in problems.Take(12))
                doc.Editor.WriteMessage("\n  " + problem);
            if (added > 0) doc.Editor.Regen();
        }

        private static ObjectId ReadObjectId(object owner, string name)
        {
            try
            {
                PropertyInfo prop = owner.GetType().GetProperty(name,
                    BindingFlags.Public | BindingFlags.Instance);
                object value = prop == null ? null : prop.GetValue(owner, null);
                return value is ObjectId ? (ObjectId)value : ObjectId.Null;
            }
            catch { return ObjectId.Null; }
        }

        private static bool TryEnableProfileInView(
            CivilProfileView view, ObjectId profileId, out bool alreadyDrawn)
        {
            alreadyDrawn = false;
            if (view == null || profileId.IsNull) return false;
            try
            {
                // Native Civil 3D Profiles tab API: ProfileOverride.Draw.
                // GraphOverrides lists the alignment's profiles even if not
                // currently drawn. Do not attempt to add duplicate profiles.
                // The 2024 Civil 3D managed API exposes the Profiles-tab
                // collection and its Draw flag directly. No unsupported
                // AddProfile() reflection or collection mutation is required.
                foreach (ProfileOverride profile in view.GraphOverrides)
                {
                    if (profile.ProfileId != profileId) continue;
                    alreadyDrawn = profile.Draw;
                    if (!alreadyDrawn) profile.Draw = true;
                    return true;
                }
            }
            catch { }
            return false;
        }

        private sealed class BatchView
        {
            internal ObjectId ViewId;
            internal ObjectId AlignmentId;
            internal string Name;
            internal string AlignmentName;
        }

        private sealed class BatchProfile
        {
            internal ObjectId ProfileId;
            internal ObjectId AlignmentId;
            internal string Display;
            public override string ToString() { return Display; }
        }

        private sealed class BatchProfilePicker : Form
        {
            private readonly CheckedListBox _items;
            private readonly IList<BatchProfile> _profiles;

            internal List<ObjectId> SelectedProfileIds
            {
                get
                {
                    return _items.CheckedItems.Cast<BatchProfile>()
                        .Select(item => item.ProfileId).ToList();
                }
            }

            internal BatchProfilePicker(
                IList<BatchProfile> profiles, int viewCount)
            {
                _profiles = profiles.OrderBy(p => p.Display,
                    StringComparer.CurrentCultureIgnoreCase).ToList();
                Text = "CE Tools - Add Profiles to Profile Views";
                Width = 670;
                Height = 590;
                MinimumSize = new System.Drawing.Size(550, 430);
                StartPosition = FormStartPosition.CenterScreen;
                var title = new System.Windows.Forms.Label {
                    Dock = DockStyle.Top, Height = 68,
                    Text = "Select profiles to display in the " + viewCount +
                        " chosen profile view(s).\r\n" +
                        "Only profiles belonging to each view's alignment are shown.",
                    Padding = new Padding(12, 10, 12, 2)
                };
                Controls.Add(title);

                _items = new CheckedListBox {
                    Dock = DockStyle.Fill, CheckOnClick = true,
                    HorizontalScrollbar = true,
                    IntegralHeight = false
                };
                foreach (BatchProfile item in _profiles)
                    _items.Items.Add(item, true);

                var bottom = new FlowLayoutPanel {
                    Dock = DockStyle.Bottom, Height = 52,
                    FlowDirection = System.Windows.Forms.FlowDirection.RightToLeft,
                    Padding = new Padding(8)
                };
                var apply = new Button { Text = "Add profiles", Width = 118,
                    DialogResult = DialogResult.OK };
                var cancel = new Button { Text = "Cancel", Width = 90,
                    DialogResult = DialogResult.Cancel };
                var clear = new Button { Text = "Clear", Width = 74 };
                var select = new Button { Text = "Select all", Width = 92 };
                clear.Click += (sender, e) => CheckAll(false);
                select.Click += (sender, e) => CheckAll(true);
                bottom.Controls.Add(apply);
                bottom.Controls.Add(cancel);
                bottom.Controls.Add(clear);
                bottom.Controls.Add(select);
                Controls.Add(_items);
                Controls.Add(bottom);
                AcceptButton = apply;
                CancelButton = cancel;
            }

            private void CheckAll(bool value)
            {
                for (int i = 0; i < _items.Items.Count; i++)
                    _items.SetItemChecked(i, value);
            }
        }
    }
}
#endif

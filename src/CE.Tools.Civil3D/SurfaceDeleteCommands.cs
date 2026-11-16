using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.SurfaceDeleteCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Presents all deletable Civil 3D surfaces in a checked multi-select popup.
    /// Only checked surfaces are previewed and erased after explicit confirmation.
    /// </summary>
    public sealed class SurfaceDeleteCommands
    {
        private sealed class SurfaceChoice
        {
            public ObjectId Id { get; private set; }
            public string Name { get; private set; }

            public SurfaceChoice(ObjectId id, string name)
            {
                Id = id;
                Name = name ?? "(unnamed surface)";
            }

            public override string ToString() { return Name; }
        }

        [CommandMethod("CE_TOOLS", "CE_SURFACEDELETE", CommandFlags.Modal | CommandFlags.Redraw)]
        public void DeleteSurfaces()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (document == null || civilDocument == null) return;

            Editor editor = document.Editor;
            var choices = new List<SurfaceChoice>();
            int referenceCount = 0;
            using (Transaction tr = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in civilDocument.GetSurfaceIds())
                {
                    if (id.IsNull) continue;
                    try
                    {
                        CivilSurface surface = tr.GetObject(id, OpenMode.ForRead, false) as CivilSurface;
                        if (surface == null) continue;
                        if (surface.IsReferenceObject)
                        {
                            referenceCount++;
                            continue;
                        }
                        choices.Add(new SurfaceChoice(id, surface.Name));
                    }
                    catch
                    {
                        // Ignore unavailable surface records; they cannot safely be offered for deletion.
                    }
                }
            }

            choices = choices.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
            if (choices.Count == 0)
            {
                editor.WriteMessage("\nCE_SURFACEDELETE: no deletable Civil 3D surfaces were found. Reference surfaces skipped: {0}.", referenceCount);
                return;
            }

            List<SurfaceChoice> selected = ShowSurfacePicker(choices);
            if (selected == null) return;
            if (selected.Count == 0)
            {
                editor.WriteMessage("\nCE_SURFACEDELETE: no surfaces selected. Nothing was deleted.");
                return;
            }

            string list = string.Join(Environment.NewLine, selected.Select(x => x.Name).Take(40).ToArray());
            if (selected.Count > 40) list += Environment.NewLine + "... and " + (selected.Count - 40) + " more.";
            string message = "The following " + selected.Count + " surface(s) will be permanently erased from this drawing:\n\n" +
                list + "\n\nReference surfaces available in the drawing but excluded from deletion: " + referenceCount +
                "\n\nContinue?";
            if (MessageBox.Show(message, "CE Tools - Confirm Surface Deletion",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            {
                editor.WriteMessage("\nCE_SURFACEDELETE cancelled. No surfaces were deleted.");
                return;
            }

            int deleted = 0, failed = 0;
            using (document.LockDocument())
            using (Transaction tr = document.Database.TransactionManager.StartTransaction())
            {
                foreach (SurfaceChoice choice in selected)
                {
                    try
                    {
                        CivilSurface surface = tr.GetObject(choice.Id, OpenMode.ForWrite, false) as CivilSurface;
                        if (surface == null || surface.IsReferenceObject)
                        {
                            failed++;
                            continue;
                        }
                        surface.Erase();
                        deleted++;
                    }
                    catch (System.Exception ex)
                    {
                        failed++;
                        editor.WriteMessage("\n  Could not delete surface '{0}': {1}", choice.Name, ex.Message);
                    }
                }
                tr.Commit();
            }
            editor.Regen();
            editor.WriteMessage("\nCE_SURFACEDELETE complete: {0} deleted; {1} failed/skipped; {2} reference surfaces excluded.",
                deleted, failed, referenceCount);
        }

        private static List<SurfaceChoice> ShowSurfacePicker(IList<SurfaceChoice> surfaces)
        {
            using (Form form = new Form())
            using (CheckedListBox list = new CheckedListBox())
            using (Button selectAll = new Button())
            using (Button clearAll = new Button())
            using (Button deleteSelected = new Button())
            using (Button cancel = new Button())
            using (Label instruction = new Label())
            using (Label count = new Label())
            {
                form.Text = "CE Tools - Select Surfaces to Delete";
                form.StartPosition = FormStartPosition.CenterScreen;
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.MinimizeBox = false;
                form.MaximizeBox = false;
                form.ShowInTaskbar = false;
                form.ClientSize = new Size(520, 510);
                form.Font = new System.Drawing.Font("Segoe UI", 9F);

                instruction.Text = "Tick the surfaces you want to delete:";
                instruction.SetBounds(14, 12, 480, 24);

                list.CheckOnClick = true;
                list.HorizontalScrollbar = true;
                list.IntegralHeight = false;
                list.SetBounds(14, 42, 492, 350);
                foreach (SurfaceChoice surface in surfaces) list.Items.Add(surface, false);

                selectAll.Text = "Select All";
                selectAll.SetBounds(14, 402, 105, 30);
                selectAll.Click += delegate
                {
                    for (int i = 0; i < list.Items.Count; i++) list.SetItemChecked(i, true);
                    UpdateCount(list, count);
                };

                clearAll.Text = "Clear All";
                clearAll.SetBounds(126, 402, 105, 30);
                clearAll.Click += delegate
                {
                    for (int i = 0; i < list.Items.Count; i++) list.SetItemChecked(i, false);
                    UpdateCount(list, count);
                };

                count.SetBounds(245, 407, 255, 22);
                count.TextAlign = ContentAlignment.MiddleRight;
                count.Text = "0 selected of " + surfaces.Count;
                list.ItemCheck += delegate(object sender, ItemCheckEventArgs e)
                {
                    // ItemCheck fires before the collection reflects the new state.
                    form.BeginInvoke((MethodInvoker)delegate { UpdateCount(list, count); });
                };

                deleteSelected.Text = "Continue";
                deleteSelected.DialogResult = DialogResult.OK;
                deleteSelected.SetBounds(300, 458, 95, 32);
                cancel.Text = "Cancel";
                cancel.DialogResult = DialogResult.Cancel;
                cancel.SetBounds(411, 458, 95, 32);

                form.Controls.Add(instruction);
                form.Controls.Add(list);
                form.Controls.Add(selectAll);
                form.Controls.Add(clearAll);
                form.Controls.Add(count);
                form.Controls.Add(deleteSelected);
                form.Controls.Add(cancel);
                form.AcceptButton = deleteSelected;
                form.CancelButton = cancel;

                if (Autodesk.AutoCAD.ApplicationServices.Application.ShowModalDialog(form) != DialogResult.OK) return null;
                var result = new List<SurfaceChoice>();
                foreach (object item in list.CheckedItems)
                {
                    SurfaceChoice choice = item as SurfaceChoice;
                    if (choice != null) result.Add(choice);
                }
                return result;
            }
        }

        private static void UpdateCount(CheckedListBox list, Label count)
        {
            count.Text = list.CheckedItems.Count + " selected of " + list.Items.Count;
        }
    }
}

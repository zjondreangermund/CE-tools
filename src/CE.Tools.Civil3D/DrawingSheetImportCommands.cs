using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.DrawingSheetImportCommands))]

namespace CETools.Civil3D
{
    /// <summary>Imports sheets and their model without opening an editor document.</summary>
    public sealed class DrawingSheetImportCommands
    {
        [CommandMethod("CE_TOOLS", "CE_IMPORTSHEETS", CommandFlags.Modal)]
        public void ImportSheets()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            try
            {
                string path;
                using (var file = new OpenFileDialog
                {
                    Title = "CE Tools - Select source drawing",
                    Filter = "AutoCAD drawings (*.dwg)|*.dwg",
                    CheckFileExists = true,
                    Multiselect = false,
                    RestoreDirectory = true
                })
                {
                    if (file.ShowDialog() != DialogResult.OK) return;
                    path = Path.GetFullPath(file.FileName);
                }
                if (!string.IsNullOrEmpty(document.Database.Filename) &&
                    string.Equals(path, Path.GetFullPath(document.Database.Filename), StringComparison.OrdinalIgnoreCase))
                {
                    Show("Choose a different drawing. The source cannot be the active drawing.");
                    return;
                }

                // A side database reads the saved file only. It never creates an MDI document.
                using (var source = new Database(false, true))
                {
                    source.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, string.Empty);
                    source.CloseInput(true);
                    List<Sheet> sheets = ReadSheets(source);
                    if (sheets.Count == 0)
                    {
                        Show("This drawing contains no paper-space layouts.");
                        return;
                    }
                    using (var picker = new SheetPicker(path, sheets))
                    {
                        if (Autodesk.AutoCAD.ApplicationServices.Application.ShowModalDialog(picker) != DialogResult.OK) return;
                        List<Sheet> selected = picker.SelectedSheets;
                        if (selected.Count == 0) return;
                        ImportResult result;
                        using (document.LockDocument())
                            result = CopySheets(source, document.Database, selected);
                        try { document.Editor.Regen(); }
                        catch { /* The import is already committed; report it as successful. */ }
                        Show("Imported " + result.Names.Count + " layout(s), " + result.PaperCount +
                            " paper-space objects (including " + result.ViewportCount + " viewports), and " +
                            result.ModelCount + " model-space objects.\n\n" + string.Join("\n", result.Names) +
                            "\n\nThe model was copied once at its original coordinates. Existing model geometry may also appear in these views." +
                            "\nExternal references, images and data links still require their supporting files. Printer configurations and fonts must be installed locally.");
                    }
                }
            }
            catch (System.Exception error)
            {
                Show("The sheets could not be imported. No import transaction was committed.\n\n" +
                    error.GetBaseException().Message, MessageBoxIcon.Error);
            }
        }

        // Copy only paper-space title-block entities. Viewports and model geometry
        // are deliberately excluded; this command never changes sheet scaling.
        [CommandMethod("CE_TOOLS", "CE_IMPORTTITLEBLOCK", CommandFlags.Modal)]
        public void ImportTitleBlock()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            try
            {
                string path;
                using (var file = new OpenFileDialog
                {
                    Title = "CE Tools - Select drawing containing title block",
                    Filter = "AutoCAD drawings (*.dwg)|*.dwg",
                    CheckFileExists = true,
                    RestoreDirectory = true
                })
                {
                    if (file.ShowDialog() != DialogResult.OK) return;
                    path = Path.GetFullPath(file.FileName);
                }
                if (!string.IsNullOrEmpty(document.Database.Filename) &&
                    string.Equals(path, Path.GetFullPath(document.Database.Filename), StringComparison.OrdinalIgnoreCase))
                {
                    Show("Choose a different source drawing.");
                    return;
                }

                using (var source = new Database(false, true))
                {
                    source.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, string.Empty);
                    source.CloseInput(true);
                    List<Sheet> sheets = ReadSheets(source);
                    if (sheets.Count == 0)
                    {
                        Show("No paper-space layouts found in the source drawing.");
                        return;
                    }
                    using (var picker = new TitleBlockPicker(path, sheets))
                    {
                        if (Autodesk.AutoCAD.ApplicationServices.Application.ShowModalDialog(picker) != DialogResult.OK)
                            return;
                        // Page coordinates are in paper-space drawing units.
                        // A true paper lower-left origin is (0,0), not entity extents
                        // (which can be shifted by text, leaders and invisible attributes).
                        Point3d sourceBase = Point3d.Origin;
                        Point3d destinationBase = Point3d.Origin;
                        if (!picker.AutomaticBasepoint)
                        {
                            PromptPointResult position = document.Editor.GetPoint(
                                "\\nSpecify destination insertion/base point on the CURRENT paper-space page: ");
                            if (position.Status != PromptStatus.OK) return;
                            destinationBase = position.Value;
                        }

                        if (document.Database.TileMode)
                        {
                            Show("Activate a paper-space layout in the destination drawing first.");
                            return;
                        }
                        int copied;
                        using (document.LockDocument())
                            copied = CopyTitleBlock(source, document.Database, picker.SelectedSheet,
                                destinationBase - sourceBase);
                        document.Editor.Regen();
                        Show("Copied " + copied + " title-block paper-space entities from '" +
                            picker.SelectedSheet.Name + "' into the CURRENT layout.\\n\\n" +
                            (picker.AutomaticBasepoint
                                ? "Source and destination page bottom-left basepoints: (0,0)."
                                : "Source page bottom-left basepoint: (0,0); destination: specified point.") +
                            "\\nText, block attributes, linetypes and annotation scaling retain their source definitions. " +
                            "Check fonts and any external references on this workstation.");
                    }
                }
            }
            catch (System.Exception ex)
            {
                Show("Title-block import failed: " + ex.GetBaseException().Message, MessageBoxIcon.Error);
            }
        }

        private static int CopyTitleBlock(Database source, Database destination, Sheet sheet, Vector3d displacement)
        {
            // Keep the source read transaction separate from the destination write.
            // Preserve entities' own scales/styles and clone block dependencies.
            using (Transaction read = source.TransactionManager.StartTransaction())
            {
                var layout = (Layout)read.GetObject(sheet.Id, OpenMode.ForRead);
                var ids = new ObjectIdCollection();
                var paper = (BlockTableRecord)read.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
                foreach (ObjectId id in paper)
                {
                    Entity entity = read.GetObject(id, OpenMode.ForRead) as Entity;
                    // Paper-space viewport #1 and all drawing viewports must stay
                    // in the original sheet. Their annotation scales are not title blocks.
                    if (entity != null && !(entity is Viewport) && !entity.IsErased)
                        ids.Add(id);
                }
                if (ids.Count == 0) return 0;
                using (Transaction write = destination.TransactionManager.StartTransaction())
                {
                    BlockTableRecord destinationPaper = (BlockTableRecord)write.GetObject(
                        destination.CurrentSpaceId, OpenMode.ForWrite);
                    if (!destinationPaper.IsLayout || destinationPaper.IsModelSpace)
                        throw new InvalidOperationException("Target must be a paper-space layout.");

                    using (var mapping = new IdMapping())
                    {
                        source.WblockCloneObjects(ids, destinationPaper.ObjectId, mapping,
                            DuplicateRecordCloning.MangleName, false);
                        Matrix3d shift = Matrix3d.Displacement(displacement);
                        int count = 0;
                        foreach (ObjectId id in ids)
                        {
                            IdPair pair = mapping[id];
                            if (!pair.IsCloned || pair.Value.IsNull || pair.Value.IsErased)
                                throw new InvalidOperationException("Title-block entity could not be cloned: " + id.Handle);
                            Entity clone = write.GetObject(pair.Value, OpenMode.ForWrite, false) as Entity;
                            if (clone == null) continue;
                            if (displacement.Length > 1e-9) clone.TransformBy(shift);
                            count++;
                        }
                        write.Commit();
                        return count;
                    }
                }
            }
        }

        private sealed class TitleBlockPicker : Form
        {
            private readonly ComboBox layoutChoice;
            private readonly CheckBox autoBase;
            internal Sheet SelectedSheet { get; private set; }
            internal bool AutomaticBasepoint { get { return autoBase.Checked; } }

            internal TitleBlockPicker(string path, IList<Sheet> sheets)
            {
                Text = "CE Tools - Import Title Block";
                Width = 670;
                Height = 295;
                MinimumSize = new System.Drawing.Size(520, 275);
                StartPosition = FormStartPosition.CenterParent;
                AutoScaleMode = AutoScaleMode.Dpi;

                var header = new Label
                {
                    Dock = DockStyle.Top, Height = 80, Padding = new Padding(12),
                    Text = "Source DWG: " + Path.GetFileName(path) +
                        "\\nSelect the paper-space sheet containing the title block. " +
                        "All non-viewport paper-space entities are copied; model geometry is excluded."
                };
                layoutChoice = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList };
                foreach (Sheet item in sheets) layoutChoice.Items.Add(item);
                layoutChoice.SelectedIndex = 0;
                var layoutLabel = new Label { Dock = DockStyle.Top, Text = "Source paper-space layout:", Height = 25 };
                autoBase = new CheckBox
                {
                    Dock = DockStyle.Top, Height = 40, Checked = true,
                    Text = "Auto: align bottom-left paper corner of both layouts (0,0)"
                };
                var manualHint = new Label
                {
                    Dock = DockStyle.Top, Height = 27,
                    Text = "Uncheck Auto to pick a custom destination basepoint after closing this dialog."
                };
                var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48 };
                var confirm = new Button { Text = "Import title block", AutoSize = true };
                var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
                confirm.Click += (sender, args) =>
                {
                    SelectedSheet = layoutChoice.SelectedItem as Sheet;
                    if (SelectedSheet == null) return;
                    DialogResult = DialogResult.OK;
                    Close();
                };
                buttons.Controls.Add(confirm);
                buttons.Controls.Add(cancel);
                Controls.Add(manualHint);
                Controls.Add(autoBase);
                Controls.Add(layoutChoice);
                Controls.Add(layoutLabel);
                Controls.Add(header);
                Controls.Add(buttons);
                AcceptButton = confirm;
                CancelButton = cancel;
            }
        }

        private static void Show(string message, MessageBoxIcon icon = MessageBoxIcon.Information)
        {
            MessageBox.Show(message, "CE Tools - Import Sheets", MessageBoxButtons.OK, icon);
        }

        private static List<Sheet> ReadSheets(Database source)
        {
            var sheets = new List<Sheet>();
            using (Transaction tr = source.TransactionManager.StartTransaction())
            {
                var dictionary = (DBDictionary)tr.GetObject(source.LayoutDictionaryId, OpenMode.ForRead);
                foreach (DBDictionaryEntry entry in dictionary)
                {
                    var layout = (Layout)tr.GetObject(entry.Value, OpenMode.ForRead);
                    if (!layout.ModelType)
                        sheets.Add(new Sheet { Id = entry.Value, Name = layout.LayoutName, Order = layout.TabOrder });
                }
            }
            return sheets.OrderBy(s => s.Order).ThenBy(s => s.Name).ToList();
        }

        private static ImportResult CopySheets(Database source, Database destination, IList<Sheet> sheets)
        {
            var result = new ImportResult();
            using (Transaction read = source.TransactionManager.StartTransaction())
            using (Transaction write = destination.TransactionManager.StartTransaction())
            {
                var layouts = (DBDictionary)write.GetObject(destination.LayoutDictionaryId, OpenMode.ForWrite);
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int tab = 0;
                foreach (DBDictionaryEntry entry in layouts)
                {
                    var layout = (Layout)write.GetObject(entry.Value, OpenMode.ForRead);
                    names.Add(layout.LayoutName);
                    tab = Math.Max(tab, layout.TabOrder);
                }
                var sourceBlocks = (BlockTable)read.GetObject(source.BlockTableId, OpenMode.ForRead);
                var targetBlocks = (BlockTable)write.GetObject(destination.BlockTableId, OpenMode.ForWrite);
                var jobs = new List<CloneJob>();
                ObjectIdCollection modelIds = EntityIds(read, sourceBlocks[BlockTableRecord.ModelSpace]);
                // Exactly one model-space clone, shared by every imported viewport.
                jobs.Add(new CloneJob { Ids = modelIds, Owner = targetBlocks[BlockTableRecord.ModelSpace] });
                result.ModelCount = modelIds.Count;

                foreach (Sheet sheet in sheets)
                {
                    var original = (Layout)read.GetObject(sheet.Id, OpenMode.ForRead);
                    string name = UniqueName(sheet.Name, names);
                    int paperIndex = 0;
                    string paperName;
                    do { paperName = "*Paper_Space" + paperIndex++; } while (targetBlocks.Has(paperName));
                    var paper = new BlockTableRecord { Name = paperName };
                    targetBlocks.Add(paper);
                    write.AddNewlyCreatedDBObject(paper, true);
                    var imported = new Layout { LayoutName = name };
                    imported.AddToLayoutDictionary(destination, paper.ObjectId);
                    write.AddNewlyCreatedDBObject(imported, true);
                    // Layout.CopyFrom is the Autodesk layout-import pattern. Entities use WblockCloneObjects.
                    imported.CopyFrom(original);
                    imported.LayoutName = name;
                    imported.TabOrder = ++tab;
                    CopyPageSetup(source, destination, read, write, original, imported);
                    ObjectIdCollection paperIds = EntityIds(read, original.BlockTableRecordId);
                    result.PaperCount += paperIds.Count;
                    foreach (ObjectId id in paperIds)
                        if (read.GetObject(id, OpenMode.ForRead) is Viewport) result.ViewportCount++;
                    jobs.Add(new CloneJob { Ids = paperIds, Owner = paper.ObjectId });
                    result.Names.Add(name);
                }

                // Shared mapping keeps model references, clipping objects and layer references consistent
                // across the complete import. Translate only after the last non-empty owner batch.
                List<CloneJob> nonEmpty = jobs.Where(j => j.Ids.Count > 0).ToList();
                using (var mapping = new IdMapping())
                {
                    for (int i = 0; i < nonEmpty.Count; i++)
                        source.WblockCloneObjects(nonEmpty[i].Ids, nonEmpty[i].Owner, mapping,
                            DuplicateRecordCloning.MangleName, i < nonEmpty.Count - 1);
                    foreach (CloneJob job in nonEmpty)
                        foreach (ObjectId id in job.Ids)
                        {
                            IdPair pair = mapping[id];
                            if (!pair.IsCloned || pair.Value.IsNull || pair.Value.IsErased)
                                throw new InvalidOperationException("A source object could not be copied: " + id.Handle);
                        }
                }
                // Layouts, model, viewports and dependent definitions commit together or roll back together.
                write.Commit();
            }
            return result;
        }

        private static ObjectIdCollection EntityIds(Transaction tr, ObjectId blockId)
        {
            var result = new ObjectIdCollection();
            var block = (BlockTableRecord)tr.GetObject(blockId, OpenMode.ForRead);
            foreach (ObjectId id in block)
                if (!id.IsErased && tr.GetObject(id, OpenMode.ForRead) is Entity) result.Add(id);
            return result;
        }

        private static string UniqueName(string requested, ISet<string> used)
        {
            string name = requested;
            int suffix = 1;
            while (!used.Add(name))
            {
                string tail = " (Imported " + suffix++ + ")";
                name = requested.Substring(0, Math.Min(requested.Length, 255 - tail.Length)) + tail;
            }
            return name;
        }

        private static void CopyPageSetup(Database source, Database destination, Transaction read,
            Transaction write, Layout original, Layout imported)
        {
            if (string.IsNullOrEmpty(original.PlotSettingsName)) return;
            var sourceSetups = (DBDictionary)read.GetObject(source.PlotSettingsDictionaryId, OpenMode.ForRead);
            if (!sourceSetups.Contains(original.PlotSettingsName))
            {
                imported.PlotSettingsName = string.Empty;
                return; // The layout still retains its own copied plot settings.
            }
            var targetSetups = (DBDictionary)write.GetObject(destination.PlotSettingsDictionaryId, OpenMode.ForWrite);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (DBDictionaryEntry entry in targetSetups) names.Add(entry.Key);
            var originalSetup = (PlotSettings)read.GetObject(sourceSetups.GetAt(original.PlotSettingsName), OpenMode.ForRead);
            var setup = new PlotSettings(false);
            setup.CopyFrom(originalSetup);
            setup.PlotSettingsName = UniqueName(original.PlotSettingsName, names);
            setup.AddToPlotSettingsDictionary(destination);
            write.AddNewlyCreatedDBObject(setup, true);
            imported.PlotSettingsName = setup.PlotSettingsName;
        }

        private sealed class Sheet
        {
            internal ObjectId Id;
            internal string Name;
            internal int Order;
            public override string ToString() { return Name; }
        }

        private sealed class CloneJob
        {
            internal ObjectIdCollection Ids;
            internal ObjectId Owner;
        }

        private sealed class ImportResult
        {
            internal readonly List<string> Names = new List<string>();
            internal int ModelCount, PaperCount, ViewportCount;
        }

        private sealed class SheetPicker : Form
        {
            private readonly CheckedListBox choices;
            internal List<Sheet> SelectedSheets { get; private set; }

            internal SheetPicker(string path, IList<Sheet> sheets)
            {
                Text = "CE Tools - Import Sheets and Model";
                Width = 700; Height = 570;
                MinimumSize = new System.Drawing.Size(540, 420);
                StartPosition = FormStartPosition.CenterParent;
                AutoScaleMode = AutoScaleMode.Dpi;
                var description = new Label
                {
                    Dock = DockStyle.Top, Height = 110, Padding = new Padding(12),
                    Text = "Source: " + Path.GetFileName(path) +
                        "\n\nCheck layouts to import, or select all. All source model-space objects will be copied once at their original coordinates. Duplicate layout names receive an Imported suffix."
                };
                choices = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, HorizontalScrollbar = true };
                foreach (Sheet sheet in sheets) choices.Items.Add(sheet, true);
                var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 54, Padding = new Padding(8) };
                var all = new Button { Text = "Select all", AutoSize = true };
                var none = new Button { Text = "Clear selection", AutoSize = true };
                var import = new Button { Text = "Import sheets", AutoSize = true };
                var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
                all.Click += (s, e) => SetChecks(true);
                none.Click += (s, e) => SetChecks(false);
                import.Click += (s, e) =>
                {
                    SelectedSheets = choices.CheckedItems.Cast<Sheet>().ToList();
                    if (SelectedSheets.Count == 0)
                    {
                        MessageBox.Show(this, "Select at least one layout.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }
                    DialogResult = DialogResult.OK;
                    Close();
                };
                buttons.Controls.AddRange(new Control[] { all, none, import, cancel });
                Controls.Add(choices); Controls.Add(description); Controls.Add(buttons);
                AcceptButton = import; CancelButton = cancel;
            }

            private void SetChecks(bool value)
            {
                for (int i = 0; i < choices.Items.Count; i++) choices.SetItemChecked(i, value);
            }
        }
    }
}

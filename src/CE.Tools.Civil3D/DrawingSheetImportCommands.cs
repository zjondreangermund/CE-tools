using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
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
                        if (AcApplication.ShowModalDialog(picker) != DialogResult.OK) return;
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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.PaperSpaceMediaFitCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Selects a larger available paper size so the existing paper-space viewport
    /// arrangement fits inside the sheet. Viewport and other paper-space entities
    /// are never moved or resized.
    /// </summary>
    public sealed class PaperSpaceMediaFitCommands
    {
        private const string AllLayoutsChoice = "All paper-space layouts";

        private sealed class LayoutInfo
        {
            internal string Name;
            internal int TabOrder;
        }

        [CommandMethod("CE_TOOLS", "CE_PAPERFITVIEWPORTS", CommandFlags.Modal | CommandFlags.Redraw)]
        public void FitPaperToViewports()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            List<LayoutInfo> layouts = ReadLayouts(document.Database);
            if (layouts.Count == 0)
            {
                document.Editor.WriteMessage("\nCE_PAPERFITVIEWPORTS: no paper-space layouts were found.");
                return;
            }

            string choice = ChooseLayout(layouts);
            if (string.IsNullOrWhiteSpace(choice)) return;

            List<string> targets = string.Equals(choice, AllLayoutsChoice, StringComparison.OrdinalIgnoreCase)
                ? layouts.Select(item => item.Name).ToList()
                : new List<string> { choice };

            int updated = 0;
            int noViewports = 0;
            int alreadyFits = 0;
            int failed = 0;
            var messages = new List<string>();

            try
            {
                using (document.LockDocument())
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    DBDictionary dictionary = (DBDictionary)transaction.GetObject(
                        document.Database.LayoutDictionaryId, OpenMode.ForRead);

                    foreach (string layoutName in targets)
                    {
                        if (!dictionary.Contains(layoutName))
                        {
                            failed++;
                            messages.Add(layoutName + ": layout not found.");
                            continue;
                        }

                        Layout layout = transaction.GetObject(
                            dictionary.GetAt(layoutName), OpenMode.ForWrite, false) as Layout;
                        if (layout == null || layout.ModelType)
                        {
                            failed++;
                            messages.Add(layoutName + ": not a paper-space layout.");
                            continue;
                        }

                        Extents2d? viewportBounds = GetViewportBounds(layout, transaction);
                        if (!viewportBounds.HasValue)
                        {
                            noViewports++;
                            messages.Add(layoutName + ": no model-space viewports found.");
                            continue;
                        }

                        Extents2d bounds = viewportBounds.Value;
                        if (bounds.MinPoint.X < -1e-6 || bounds.MinPoint.Y < -1e-6)
                        {
                            failed++;
                            messages.Add(layoutName + ": one or more viewports extend left/below the paper origin. The sheet cannot be fitted without moving Paper Space objects.");
                            continue;
                        }

                        double margin = layout.PlotPaperUnits == PlotPaperUnit.Inches ? 0.4 : 10.0;
                        double requiredWidth = bounds.MaxPoint.X + margin;
                        double requiredHeight = bounds.MaxPoint.Y + margin;

                        string error;
                        bool changed;
                        if (TryFitLayout(layout, requiredWidth, requiredHeight, out changed, out error))
                        {
                            if (changed) updated++;
                            else alreadyFits++;
                            messages.Add(layoutName + (changed ? ": paper size increased to fit viewport extents." : ": current paper size already accommodates the viewports."));
                        }
                        else
                        {
                            failed++;
                            messages.Add(layoutName + ": " + error);
                        }
                    }

                    transaction.Commit();
                }
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage("\nCE_PAPERFITVIEWPORTS failed: " + exception.Message);
                return;
            }

            try
            {
                document.Editor.Regen();
                AcApplication.UpdateScreen();
            }
            catch { }

            document.Editor.WriteMessage(
                "\nCE_PAPERFITVIEWPORTS complete. Layouts changed={0}; already fit={1}; no viewports={2}; unable to fit={3}.",
                updated, alreadyFits, noViewports, failed);
            foreach (string message in messages)
                document.Editor.WriteMessage("\n  - " + message);
            document.Editor.WriteMessage(
                "\nNote: the command changes the configured sheet media size only. It does not translate the paper boundary independently of Paper Space coordinates.");
        }

        private static List<LayoutInfo> ReadLayouts(Database database)
        {
            var result = new List<LayoutInfo>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                DBDictionary dictionary = (DBDictionary)transaction.GetObject(
                    database.LayoutDictionaryId, OpenMode.ForRead);
                foreach (DBDictionaryEntry entry in dictionary)
                {
                    Layout layout = transaction.GetObject(entry.Value, OpenMode.ForRead, false) as Layout;
                    if (layout == null || layout.ModelType) continue;
                    result.Add(new LayoutInfo { Name = layout.LayoutName, TabOrder = layout.TabOrder });
                }
            }
            return result.OrderBy(item => item.TabOrder)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static string ChooseLayout(List<LayoutInfo> layouts)
        {
            using (var form = new Form())
            {
                form.Text = "CE Tools - Fit Paper to Viewports";
                form.Width = 560;
                form.Height = 235;
                form.StartPosition = FormStartPosition.CenterScreen;
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.MaximizeBox = false;
                form.MinimizeBox = false;

                var description = new Label
                {
                    Dock = DockStyle.Top,
                    Height = 86,
                    Padding = new Padding(14, 12, 14, 8),
                    Text = "Choose whether to fit every Paper Space layout or one selected layout.\r\n" +
                           "The command selects a larger available paper size based on the existing viewport positions.\r\n" +
                           "Viewports, title blocks and other Paper Space objects are not moved."
                };

                var selector = new ComboBox
                {
                    Dock = DockStyle.Top,
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    Margin = new Padding(14)
                };
                selector.Items.Add(AllLayoutsChoice);
                foreach (LayoutInfo layout in layouts) selector.Items.Add(layout.Name);
                selector.SelectedIndex = 0;

                var buttons = new FlowLayoutPanel
                {
                    Dock = DockStyle.Bottom,
                    Height = 48,
                    FlowDirection = System.Windows.Forms.FlowDirection.RightToLeft,
                    Padding = new Padding(8)
                };
                var apply = new Button { Text = "Apply", AutoSize = true, DialogResult = DialogResult.OK };
                var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
                buttons.Controls.Add(apply);
                buttons.Controls.Add(cancel);

                form.Controls.Add(selector);
                form.Controls.Add(description);
                form.Controls.Add(buttons);
                form.AcceptButton = apply;
                form.CancelButton = cancel;

                form.Shown += (sender, args) => CeWinFormsTheme.Refresh();
                return Autodesk.AutoCAD.ApplicationServices.Application.ShowModalDialog(form) == DialogResult.OK
                    ? Convert.ToString(selector.SelectedItem)
                    : null;
            }
        }

        private static Extents2d? GetViewportBounds(Layout layout, Transaction transaction)
        {
            BlockTableRecord paper = transaction.GetObject(
                layout.BlockTableRecordId, OpenMode.ForRead, false) as BlockTableRecord;
            if (paper == null) return null;

            bool found = false;
            double minX = double.MaxValue;
            double minY = double.MaxValue;
            double maxX = double.MinValue;
            double maxY = double.MinValue;

            foreach (ObjectId id in paper)
            {
                Viewport viewport = transaction.GetObject(id, OpenMode.ForRead, false) as Viewport;
                if (viewport == null || viewport.Number == 1) continue;

                try
                {
                    Extents3d extents = viewport.GeometricExtents;
                    minX = Math.Min(minX, extents.MinPoint.X);
                    minY = Math.Min(minY, extents.MinPoint.Y);
                    maxX = Math.Max(maxX, extents.MaxPoint.X);
                    maxY = Math.Max(maxY, extents.MaxPoint.Y);
                    found = true;
                }
                catch
                {
                    // Ignore viewports for which AutoCAD cannot return a geometric extent.
                }
            }

            return found ? new Extents2d(new Point2d(minX, minY), new Point2d(maxX, maxY)) : (Extents2d?)null;
        }

        private static bool TryFitLayout(Layout layout, double requiredWidth, double requiredHeight,
            out bool changed, out string error)
        {
            changed = false;
            error = null;
            PlotSettingsValidator validator = PlotSettingsValidator.Current;

            using (var settings = new PlotSettings(layout.ModelType))
            {
                settings.CopyFrom(layout);
                try
                {
                    validator.RefreshLists(settings);
                    System.Collections.Specialized.StringCollection mediaNames =
                        validator.GetCanonicalMediaNameList(settings);
                    if (mediaNames == null || mediaNames.Count == 0)
                    {
                        error = "the configured plotter returned no available paper sizes.";
                        return false;
                    }

                    string bestMedia = null;
                    PlotRotation bestRotation = PlotRotation.Degrees000;
                    double bestArea = double.MaxValue;

                    foreach (string media in mediaNames)
                    {
                        if (string.IsNullOrWhiteSpace(media)) continue;

                        foreach (PlotRotation rotation in new[]
                        {
                            PlotRotation.Degrees000,
                            PlotRotation.Degrees090
                        })
                        {
                            using (var candidate = new PlotSettings(layout.ModelType))
                            {
                                candidate.CopyFrom(settings);
                                try
                                {
                                    validator.SetCanonicalMediaName(candidate, media);
                                    validator.SetPlotRotation(candidate, rotation);
                                    Point2d size = candidate.PlotPaperSize;
                                    double width = size.X;
                                    double height = size.Y;
                                    if (width <= 0 || height <= 0) continue;

                                    if (width + 1e-6 < requiredWidth || height + 1e-6 < requiredHeight)
                                        continue;

                                    double area = width * height;
                                    if (area < bestArea)
                                    {
                                        bestArea = area;
                                        bestMedia = media;
                                        bestRotation = rotation;
                                    }
                                }
                                catch
                                {
                                    // Some plotters expose media names that cannot be applied to this layout.
                                }
                            }
                        }
                    }

                    if (bestMedia == null)
                    {
                        error = "no available paper size on the current plotter is large enough. Choose a plotter with a larger/custom sheet size first.";
                        return false;
                    }

                    Point2d currentSize = layout.PlotPaperSize;
                    double currentWidth = currentSize.X;
                    double currentHeight = currentSize.Y;
                    if (currentWidth + 1e-6 >= requiredWidth &&
                        currentHeight + 1e-6 >= requiredHeight)
                    {
                        return true;
                    }

                    validator.SetCanonicalMediaName(settings, bestMedia);
                    validator.SetPlotRotation(settings, bestRotation);
                    layout.CopyFrom(settings);
                    changed = true;
                    return true;
                }
                catch (System.Exception exception)
                {
                    error = "plotter paper-size update failed: " + exception.Message;
                    return false;
                }
            }
        }
    }
}

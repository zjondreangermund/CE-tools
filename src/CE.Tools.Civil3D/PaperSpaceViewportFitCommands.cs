using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.PaperSpaceViewportFitCommands))]

namespace CETools.Civil3D
{
    /// <summary>Moves all paper-space entities in checked layouts together using picked points.</summary>
    public sealed class PaperSpaceViewportFitCommands
    {
        private sealed class LayoutInfo
        {
            internal string Name;
            internal int TabOrder;
        }


        public void MovePaperSpaceContents()
        {
            Document doc = AcApplication.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            List<LayoutInfo> layouts = ReadLayouts(doc.Database);
            if (layouts.Count == 0)
            {
                doc.Editor.WriteMessage("\nCE_PAPERFITVIEWPORTS: no paper-space layouts were found.");
                return;
            }

            List<string> targets = ChooseLayouts(layouts);
            if (targets == null || targets.Count == 0)
            {
                doc.Editor.WriteMessage("\nCE_PAPERFITVIEWPORTS: no paper-space tabs selected. Nothing moved.");
                return;
            }

            Point3d basePoint, insertionPoint;
            if (!CePointInputDialog.TryGet(doc, "Base point", Point3d.Origin, out basePoint) ||
                !CePointInputDialog.TryGet(doc, "Insertion point", basePoint, out insertionPoint)) return;
            Vector3d delta = insertionPoint - basePoint;
            if (delta.Length < Tolerance.Global.EqualPoint)
            {
                doc.Editor.WriteMessage("\nThe points are identical. Nothing moved.");
                return;
            }

            int moved = 0, skipped = 0, layoutErrors = 0;
            Matrix3d displacement = Matrix3d.Displacement(delta);
            var report = new List<string>();

            try
            {
                using (doc.LockDocument())
                using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
                {
                    DBDictionary dict = (DBDictionary)tr.GetObject(doc.Database.LayoutDictionaryId, OpenMode.ForRead);
                    foreach (string name in targets)
                    {
                        if (!dict.Contains(name))
                        {
                            layoutErrors++;
                            report.Add(name + ": layout not found.");
                            continue;
                        }

                        Layout layout = tr.GetObject(dict.GetAt(name), OpenMode.ForRead, false) as Layout;
                        if (layout == null || layout.ModelType)
                        {
                            layoutErrors++;
                            report.Add(name + ": invalid paper-space layout.");
                            continue;
                        }

                        BlockTableRecord btr = tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead, false) as BlockTableRecord;
                        if (btr == null)
                        {
                            layoutErrors++;
                            report.Add(name + ": paper-space record unavailable.");
                            continue;
                        }

                        int thisMoved = 0, thisSkipped = 0;
                        foreach (ObjectId id in btr)
                        {
                            if (id.IsNull || !id.IsValid || id.IsErased) continue;
                            try
                            {
                                Entity ent = tr.GetObject(id, OpenMode.ForWrite, false, true) as Entity;
                                if (ent == null) { thisSkipped++; continue; }
                                ent.TransformBy(displacement);
                                thisMoved++;
                            }
                            catch { thisSkipped++; }
                        }

                        moved += thisMoved;
                        skipped += thisSkipped;
                        report.Add(name + ": moved " + thisMoved + " objects; skipped " + thisSkipped + ".");
                    }
                    tr.Commit();
                }
            }
            catch (System.Exception ex)
            {
                doc.Editor.WriteMessage("\nCE_PAPERFITVIEWPORTS failed: " + ex.Message);
                return;
            }

            try { doc.Editor.Regen(); AcApplication.UpdateScreen(); } catch { }
            doc.Editor.WriteMessage(
                "\nCE_PAPERFITVIEWPORTS complete. Layouts processed={0}; objects moved={1}; skipped={2}; layout errors={3}.",
                targets.Count - layoutErrors, moved, skipped, layoutErrors);
            foreach (string line in report) doc.Editor.WriteMessage("\n  - " + line);
            doc.Editor.WriteMessage(
                "\nAll checked Paper Space contents, including viewports, were translated together. Model Space geometry and paper size were not changed.");
        }

        [CommandMethod("CE_TOOLS", "CE_MOVEPAPERSPACES", CommandFlags.Modal | CommandFlags.Redraw)]
        public void MovePaperSpacesAlias()
        {
            MovePaperSpaceContents();
        }

        private static List<LayoutInfo> ReadLayouts(Database db)
        {
            var list = new List<LayoutInfo>();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                DBDictionary dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                foreach (DBDictionaryEntry entry in dict)
                {
                    Layout layout = tr.GetObject(entry.Value, OpenMode.ForRead, false) as Layout;
                    if (layout != null && !layout.ModelType)
                        list.Add(new LayoutInfo { Name = layout.LayoutName, TabOrder = layout.TabOrder });
                }
                tr.Commit();
            }
            return list.OrderBy(x => x.TabOrder)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static List<string> ChooseLayouts(List<LayoutInfo> layouts)
        {
            var window = new PaperSpaceSelectionWindow(layouts);
            bool? result = AcApplication.ShowModalWindow(window);
            return result == true ? window.SelectedLayouts : null;
        }

        private sealed class PaperSpaceSelectionWindow : Window
        {
            private readonly List<LayoutInfo> _layouts;
            private readonly List<CheckBox> _layoutChecks = new List<CheckBox>();
            private readonly CheckBox _selectAll;
            internal List<string> SelectedLayouts { get; private set; }

            internal PaperSpaceSelectionWindow(List<LayoutInfo> layouts)
            {
                _layouts = layouts ?? new List<LayoutInfo>();
                Title = "CE Tools - Move Paper Space Tabs";
                Width = 590;
                Height = 570;
                MinWidth = 500;
                MinHeight = 420;
                WindowStartupLocation = WindowStartupLocation.CenterOwner;
                ResizeMode = ResizeMode.CanResize;

                var root = new DockPanel { Margin = new Thickness(18) };
                Content = root;

                var buttonPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 12, 0, 0)
                };
                DockPanel.SetDock(buttonPanel, Dock.Bottom);
                root.Children.Add(buttonPanel);

                var cancel = new Button { Content = "Cancel", Width = 90, Margin = new Thickness(5), IsCancel = true };
                cancel.Click += (sender, args) => { DialogResult = false; Close(); };
                buttonPanel.Children.Add(cancel);

                var continueButton = new Button { Content = "Continue", Width = 100, Margin = new Thickness(5), IsDefault = true };
                continueButton.Click += (sender, args) =>
                {
                    SelectedLayouts = _layoutChecks.Where(x => x.IsChecked == true)
                        .Select(x => Convert.ToString(x.Tag)).ToList();
                    if (SelectedLayouts.Count == 0)
                    {
                        MessageBox.Show(this, "Select at least one Paper Space tab.", "CE Tools");
                        return;
                    }
                    DialogResult = true;
                    Close();
                };
                buttonPanel.Children.Add(continueButton);

                var content = new StackPanel();
                root.Children.Add(content);

                content.Children.Add(new TextBlock
                {
                    Text = "Select the Paper Space tabs to move. You can select every tab or tick individual layouts.",
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 12)
                });

                _selectAll = new CheckBox
                {
                    Content = "Select all Paper Space tabs",
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(0, 2, 0, 8)
                };
                _selectAll.Checked += (sender, args) => SetAll(true);
                _selectAll.Unchecked += (sender, args) => SetAll(false);
                content.Children.Add(_selectAll);

                var listBorder = new Border
                {
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(8),
                    Margin = new Thickness(0, 0, 0, 10),
                    Child = new ScrollViewer
                    {
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                        MinHeight = 230,
                        Content = BuildLayoutList()
                    }
                };
                content.Children.Add(listBorder);

                content.Children.Add(new TextBlock
                {
                    Text = "After continuing, pick the base point and then the new insertion point on screen. All objects in each checked layout—including viewports, title blocks and annotations—will move together.",
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 0)
                });

                Loaded += (sender, args) => CeInterfaceTheme.Apply(this);
            }

            private UIElement BuildLayoutList()
            {
                var panel = new StackPanel();
                foreach (LayoutInfo layout in _layouts)
                {
                    var check = new CheckBox
                    {
                        Content = layout.Name,
                        Tag = layout.Name,
                        Margin = new Thickness(3, 4, 3, 4)
                    };
                    check.Checked += (sender, args) => UpdateSelectAllState();
                    check.Unchecked += (sender, args) => UpdateSelectAllState();
                    _layoutChecks.Add(check);
                    panel.Children.Add(check);
                }
                return panel;
            }

            private void SetAll(bool value)
            {
                foreach (CheckBox check in _layoutChecks)
                    check.IsChecked = value;
            }

            private void UpdateSelectAllState()
            {
                if (_selectAll == null) return;
                bool allChecked = _layoutChecks.Count > 0 && _layoutChecks.All(x => x.IsChecked == true);
                bool noneChecked = _layoutChecks.All(x => x.IsChecked != true);
                _selectAll.IsThreeState = false;
                _selectAll.Checked -= SelectAllChecked;
                _selectAll.Unchecked -= SelectAllUnchecked;
                _selectAll.IsChecked = allChecked;
                _selectAll.Checked += SelectAllChecked;
                _selectAll.Unchecked += SelectAllUnchecked;
            }

            private void SelectAllChecked(object sender, RoutedEventArgs args) { SetAll(true); }
            private void SelectAllUnchecked(object sender, RoutedEventArgs args) { SetAll(false); }
        }
    }
}

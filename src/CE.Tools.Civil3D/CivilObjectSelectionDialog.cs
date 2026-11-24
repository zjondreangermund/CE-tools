using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilEntity = Autodesk.Civil.DatabaseServices.Entity;
using Part = Autodesk.Civil.DatabaseServices.Part;

namespace CETools.Civil3D
{
    /// <summary>Named dropdown and optional drawing pick using an owned modal interaction.</summary>
    internal static class CivilObjectSelectionDialog
    {
        internal static List<ObjectId> Select(Document document, string title, IEnumerable<ObjectId> ids, bool multiple)
        {
            var choices = new Dictionary<string, ObjectId>();
            using (var tr = document.Database.TransactionManager.StartTransaction())
                foreach (ObjectId id in ids.Distinct())
                {
                    if (id.IsNull || !id.IsValid || id.IsErased || id.Database != document.Database) continue;
                    var entity = tr.GetObject(id, OpenMode.ForRead) as CivilEntity;
                    if (entity != null) choices[entity.Name + " [" + id.Handle + "]"] = id;
                }
            if (choices.Count == 0) return new List<ObjectId>();
            var window = new Window { Title = "CE Tools - " + title, Width = 640, Height = 440,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.CanResize };
            var panel = new StackPanel { Margin = new Thickness(20) };
            panel.Children.Add(new TextBlock { Text = title, FontSize = 22, Margin = new Thickness(0, 0, 0, 16) });
            var combo = new ComboBox { ItemsSource = choices.Keys.OrderBy(x => x).ToList(), SelectedIndex = 0, MinHeight = 30 };
            panel.Children.Add(combo);
            var selected = new ListBox { Height = 180, Margin = new Thickness(0, 10, 0, 10) };
            panel.Children.Add(selected);
            Action<string> add = key => {
                if (!multiple) selected.Items.Clear();
                if (!selected.Items.Contains(key)) selected.Items.Add(key);
            };
            var buttons = new WrapPanel();
            var addButton = new Button { Content = "Add selected", Padding = new Thickness(12, 6, 12, 6) };
            addButton.Click += (s, e) => { if (combo.SelectedItem != null) add((string)combo.SelectedItem); };
            buttons.Children.Add(addButton);
            var remove = new Button { Content = "Remove", Padding = new Thickness(12, 6, 12, 6) };
            remove.Click += (s, e) => { if (selected.SelectedItem != null) selected.Items.Remove(selected.SelectedItem); };
            buttons.Children.Add(remove);
            var pick = new Button { Content = "Select in drawing", Padding = new Thickness(12, 6, 12, 6) };
            pick.Click += (s, e) => {
                try
                {
                    using (document.Editor.StartUserInteraction(new WindowInteropHelper(window).Handle))
                    {
                        var result = document.Editor.GetSelection(new PromptSelectionOptions { AllowDuplicates = false,
                            MessageForAdding = "\nSelect objects (select a pipe or structure for its network): " });
                        if (result.Status != PromptStatus.OK) return;
                        using (var tr = document.Database.TransactionManager.StartTransaction())
                            foreach (ObjectId picked in result.Value.GetObjectIds())
                            {
                                ObjectId match = picked;
                                if (!choices.ContainsValue(match))
                                {
                                    var part = tr.GetObject(picked, OpenMode.ForRead) as Part;
                                    if (part != null) match = part.NetworkId;
                                }
                                var key = choices.FirstOrDefault(x => x.Value == match).Key;
                                if (key == null) continue;
                                add(key);
                                if (!multiple) break;
                            }
                    }
                }
                catch (System.Exception ex) { CeMessageDialog.Show("Selection", ex.Message, false, window); }
            };
            buttons.Children.Add(pick);
            var ok = new Button { Content = "Continue", Padding = new Thickness(12, 6, 12, 6), IsDefault = true };
            ok.Click += (s, e) => {
                if (selected.Items.Count == 0 && combo.SelectedItem != null) add((string)combo.SelectedItem);
                window.DialogResult = true;
            };
            buttons.Children.Add(ok);
            var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(12, 6, 12, 6) };
            buttons.Children.Add(cancel);
            panel.Children.Add(buttons); window.Content = panel;
            window.Loaded += (s, e) => CeInterfaceTheme.Apply(window);
            return AcApplication.ShowModalWindow(window) == true
                ? selected.Items.Cast<string>().Select(key => choices[key]).ToList() : new List<ObjectId>();
        }
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.CivilObjectBatchStyleCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Popup-only batch style application for alignments and corridors. The
    /// implementation deliberately uses ObjectId/reflection at the Civil API
    /// boundary so the same build remains usable in Civil 3D 2023 and 2024.
    /// </summary>
    public sealed class CivilObjectBatchStyleCommands
    {
        [CommandMethod("CE_TOOLS", "CE_ALIGNMENTBATCH", CommandFlags.Modal | CommandFlags.Redraw)]
        public void AlignmentBatch()
        {
            RunBatch(BatchObjectKind.Alignment);
        }

        [CommandMethod("CE_TOOLS", "CE_CORRIDORBATCH", CommandFlags.Modal | CommandFlags.Redraw)]
        public void CorridorBatch()
        {
            RunBatch(BatchObjectKind.Corridor);
        }

        private static void RunBatch(BatchObjectKind kind)
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (document == null || civilDocument == null) return;

            List<BatchCivilItem> items = ReadItems(document, civilDocument, kind);
            if (items.Count == 0)
            {
                MessageBox.Show(
                    kind == BatchObjectKind.Alignment
                        ? "No alignments were found in the current drawing."
                        : "No corridors were found in the current drawing.",
                    "CE Tools",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            List<BatchStyleChoice> primary = ReadStyles(
                document,
                kind == BatchObjectKind.Alignment
                    ? ReadPath(civilDocument, "Styles", "AlignmentStyles")
                    : ReadPath(civilDocument, "Styles", "CorridorStyles"));
            List<BatchStyleChoice> secondary = kind == BatchObjectKind.Corridor
                ? ReadStyles(document, ReadPath(civilDocument, "Styles", "CodeSetStyles"))
                : new List<BatchStyleChoice>();

            var window = new CivilObjectBatchWindow(kind, items, primary, secondary);
            AcApplication.ShowModalWindow(window);
            if (!window.Accepted) return;

            int changed = 0;
            int unsupported = 0;
            int failed = 0;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (BatchCivilItem item in window.SelectedItems)
                {
                    try
                    {
                        DBObject value = transaction.GetObject(
                            item.ObjectId,
                            OpenMode.ForWrite,
                            false);
                        bool itemChanged = false;
                        if (window.ApplyPrimary)
                        {
                            itemChanged |= TrySetObjectId(
                                value,
                                new[] { "StyleId" },
                                window.SelectedPrimary.ObjectId);
                            if (!itemChanged) unsupported++;
                        }
                        if (kind == BatchObjectKind.Corridor && window.ApplySecondary)
                        {
                            bool codeChanged = TrySetObjectId(
                                value,
                                new[] { "CodeSetStyleId" },
                                window.SelectedSecondary.ObjectId);
                            itemChanged |= codeChanged;
                            if (!codeChanged) unsupported++;
                        }
                        if (window.Rebuild && kind == BatchObjectKind.Corridor)
                        {
                            if (!TryInvoke(value, "Rebuild")) unsupported++;
                        }
                        if (itemChanged) changed++;
                    }
                    catch
                    {
                        failed++;
                    }
                }
                transaction.Commit();
            }

            document.Editor.Regen();
            MessageBox.Show(
                string.Format(
                    CultureInfo.CurrentCulture,
                    "Selected: {0}\nChanged: {1}\nUnsupported operations: {2}\nFailed: {3}",
                    window.SelectedItems.Count,
                    changed,
                    unsupported,
                    failed),
                kind == BatchObjectKind.Alignment
                    ? "CE Tools - Alignment Batch Result"
                    : "CE Tools - Corridor Batch Result",
                MessageBoxButton.OK,
                failed == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }

        private static List<BatchCivilItem> ReadItems(
            Document document,
            CivilDocument civilDocument,
            BatchObjectKind kind)
        {
            IEnumerable ids = kind == BatchObjectKind.Alignment
                ? civilDocument.GetAlignmentIds()
                : InvokeEnumerable(civilDocument, "GetCorridorIds");
            var result = new List<BatchCivilItem>();
            if (ids == null) return result;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (object item in ids)
                {
                    if (!(item is ObjectId)) continue;
                    ObjectId id = (ObjectId)item;
                    if (id.IsNull) continue;
                    DBObject value = transaction.GetObject(id, OpenMode.ForRead, false);
                    result.Add(new BatchCivilItem(
                        id,
                        ReadString(value, "Name") ?? id.Handle.ToString(),
                        ReadString(value, "StyleName") ?? "<Current>",
                        kind == BatchObjectKind.Corridor
                            ? ReadString(value, "CodeSetStyleName") ?? "<Current>"
                            : string.Empty));
                }
            }
            return result
                .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private static List<BatchStyleChoice> ReadStyles(
            Document document,
            object collection)
        {
            var result = new List<BatchStyleChoice>();
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (object item in EnumerateCollection(collection))
                {
                    ObjectId id = item is ObjectId
                        ? (ObjectId)item
                        : item is DBObject
                            ? ((DBObject)item).ObjectId
                            : ObjectId.Null;
                    if (id.IsNull || result.Any(style => style.ObjectId == id)) continue;
                    DBObject style = transaction.GetObject(id, OpenMode.ForRead, false);
                    string name = ReadString(style, "Name");
                    if (!string.IsNullOrWhiteSpace(name))
                        result.Add(new BatchStyleChoice(id, name));
                }
            }
            return result
                .OrderBy(style => style.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private static IEnumerable<object> EnumerateCollection(object collection)
        {
            if (collection == null) yield break;
            IEnumerable enumerable = collection as IEnumerable;
            if (enumerable != null)
            {
                foreach (object item in enumerable) yield return item;
                yield break;
            }

            // Civil 3D 2023 style collections can expose Count plus an indexer
            // without implementing the non-generic IEnumerable interface.
            int count;
            object countValue = ReadPath(collection, "Count");
            if (countValue == null ||
                !int.TryParse(
                    Convert.ToString(countValue, CultureInfo.InvariantCulture),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out count))
                yield break;
            PropertyInfo indexer = collection.GetType()
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(property =>
                    property.GetIndexParameters().Length == 1 &&
                    property.GetGetMethod() != null);
            if (indexer == null) yield break;
            Type indexType = indexer.GetIndexParameters()[0].ParameterType;
            for (int index = 0; index < count; index++)
            {
                object value;
                try
                {
                    value = indexer.GetValue(
                        collection,
                        new[]
                        {
                            Convert.ChangeType(
                                index,
                                indexType,
                                CultureInfo.InvariantCulture)
                        });
                }
                catch
                {
                    continue;
                }
                yield return value;
            }
        }

        private static object ReadPath(object value, params string[] names)
        {
            foreach (string name in names)
            {
                if (value == null) return null;
                PropertyInfo property = value.GetType().GetProperty(
                    name,
                    BindingFlags.Public | BindingFlags.Instance);
                value = property == null ? null : property.GetValue(value, null);
            }
            return value;
        }

        private static string ReadString(object value, string name)
        {
            object propertyValue = ReadPath(value, name);
            return propertyValue == null
                ? null
                : Convert.ToString(propertyValue, CultureInfo.CurrentCulture);
        }

        private static IEnumerable InvokeEnumerable(object value, string methodName)
        {
            if (value == null) return null;
            try
            {
                MethodInfo method = value.GetType().GetMethod(
                    methodName,
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    Type.EmptyTypes,
                    null);
                return method == null ? null : method.Invoke(value, null) as IEnumerable;
            }
            catch
            {
                return null;
            }
        }

        private static bool TrySetObjectId(
            object target,
            IEnumerable<string> propertyNames,
            ObjectId objectId)
        {
            foreach (string propertyName in propertyNames)
            {
                try
                {
                    PropertyInfo property = target.GetType().GetProperty(
                        propertyName,
                        BindingFlags.Public | BindingFlags.Instance);
                    if (property == null || !property.CanWrite ||
                        property.PropertyType != typeof(ObjectId)) continue;
                    property.SetValue(target, objectId, null);
                    return true;
                }
                catch
                {
                    // Try the next compatible property.
                }
            }
            return false;
        }

        private static bool TryInvoke(object target, string methodName)
        {
            try
            {
                MethodInfo method = target.GetType().GetMethod(
                    methodName,
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    Type.EmptyTypes,
                    null);
                if (method == null) return false;
                method.Invoke(target, null);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    internal enum BatchObjectKind
    {
        Alignment,
        Corridor
    }

    internal sealed class BatchCivilItem
    {
        public BatchCivilItem(
            ObjectId objectId,
            string name,
            string style,
            string secondaryStyle)
        {
            ObjectId = objectId;
            Name = name;
            Style = style;
            SecondaryStyle = secondaryStyle;
            IsSelected = true;
        }

        public ObjectId ObjectId { get; private set; }
        public string Name { get; private set; }
        public string Style { get; private set; }
        public string SecondaryStyle { get; private set; }
        public bool IsSelected { get; set; }
    }

    internal sealed class BatchStyleChoice
    {
        public BatchStyleChoice(ObjectId objectId, string name)
        {
            ObjectId = objectId;
            Name = name;
        }

        public ObjectId ObjectId { get; private set; }
        public string Name { get; private set; }
        public override string ToString() { return Name; }
    }

    internal sealed class CivilObjectBatchWindow : Window
    {
        private readonly BatchObjectKind _kind;
        private readonly IList<BatchCivilItem> _items;
        private readonly StackPanel _itemsPanel;
        private readonly CheckBox _applyPrimary;
        private readonly CheckBox _applySecondary;
        private readonly CheckBox _rebuild;
        private readonly ComboBox _primary;
        private readonly ComboBox _secondary;

        public CivilObjectBatchWindow(
            BatchObjectKind kind,
            IList<BatchCivilItem> items,
            IList<BatchStyleChoice> primary,
            IList<BatchStyleChoice> secondary)
        {
            _kind = kind;
            _items = items;
            Title = kind == BatchObjectKind.Alignment
                ? "CE Tools - Batch Alignments"
                : "CE Tools - Batch Corridors";
            Width = 820;
            Height = 680;
            MinWidth = 640;
            MinHeight = 480;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var root = new DockPanel { Margin = new Thickness(16) };
            Content = root;
            var heading = new TextBlock
            {
                Text = kind == BatchObjectKind.Alignment
                    ? "Batch Alignment Styles"
                    : "Batch Corridor Styles",
                FontSize = 20,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 10)
            };
            DockPanel.SetDock(heading, Dock.Top);
            root.Children.Add(heading);

            var settings = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            _applyPrimary = new CheckBox
            {
                Content = kind == BatchObjectKind.Alignment
                    ? "Apply alignment style"
                    : "Apply corridor style",
                IsChecked = primary.Count > 0
            };
            _primary = new ComboBox
            {
                ItemsSource = primary,
                SelectedIndex = primary.Count > 0 ? 0 : -1,
                Margin = new Thickness(20, 3, 0, 8),
                MinWidth = 360
            };
            settings.Children.Add(_applyPrimary);
            settings.Children.Add(_primary);
            _applySecondary = new CheckBox
            {
                Content = "Apply corridor code-set style",
                IsChecked = kind == BatchObjectKind.Corridor && secondary.Count > 0,
                Visibility = kind == BatchObjectKind.Corridor
                    ? Visibility.Visible
                    : Visibility.Collapsed
            };
            _secondary = new ComboBox
            {
                ItemsSource = secondary,
                SelectedIndex = secondary.Count > 0 ? 0 : -1,
                Margin = new Thickness(20, 3, 0, 8),
                MinWidth = 360,
                Visibility = kind == BatchObjectKind.Corridor
                    ? Visibility.Visible
                    : Visibility.Collapsed
            };
            _rebuild = new CheckBox
            {
                Content = "Rebuild selected corridors after applying styles",
                IsChecked = true,
                Visibility = kind == BatchObjectKind.Corridor
                    ? Visibility.Visible
                    : Visibility.Collapsed
            };
            settings.Children.Add(_applySecondary);
            settings.Children.Add(_secondary);
            settings.Children.Add(_rebuild);
            DockPanel.SetDock(settings, Dock.Top);
            root.Children.Add(settings);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0)
            };
            var all = Button("Select All");
            all.Click += delegate
            {
                foreach (BatchCivilItem item in _items) item.IsSelected = true;
                RefreshItems();
            };
            var clear = Button("Clear Selection");
            clear.Margin = new Thickness(6, 0, 0, 0);
            clear.Click += delegate
            {
                foreach (BatchCivilItem item in _items) item.IsSelected = false;
                RefreshItems();
            };
            var apply = Button("Apply Batch");
            apply.Margin = new Thickness(16, 0, 0, 0);
            apply.Click += delegate
            {
                if (!SelectedItems.Any())
                {
                    Warn("Select at least one item.");
                    return;
                }
                if (ApplyPrimary && SelectedPrimary == null)
                {
                    Warn("The drawing contains no selectable primary style.");
                    return;
                }
                if (ApplySecondary && SelectedSecondary == null)
                {
                    Warn("The drawing contains no selectable code-set style.");
                    return;
                }
                Accepted = true;
                DialogResult = true;
            };
            var cancel = Button("Cancel");
            cancel.Margin = new Thickness(6, 0, 0, 0);
            cancel.Click += delegate { DialogResult = false; };
            buttons.Children.Add(all);
            buttons.Children.Add(clear);
            buttons.Children.Add(apply);
            buttons.Children.Add(cancel);
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(buttons);

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            _itemsPanel = new StackPanel();
            scroll.Content = _itemsPanel;
            root.Children.Add(scroll);
            RefreshItems();
        }

        public bool Accepted { get; private set; }
        public bool ApplyPrimary { get { return _applyPrimary.IsChecked == true; } }
        public bool ApplySecondary
        {
            get
            {
                return _kind == BatchObjectKind.Corridor &&
                    _applySecondary.IsChecked == true;
            }
        }
        public bool Rebuild { get { return _rebuild.IsChecked == true; } }
        public BatchStyleChoice SelectedPrimary { get { return _primary.SelectedItem as BatchStyleChoice; } }
        public BatchStyleChoice SelectedSecondary { get { return _secondary.SelectedItem as BatchStyleChoice; } }
        public List<BatchCivilItem> SelectedItems
        {
            get { return _items.Where(item => item.IsSelected).ToList(); }
        }

        private static Button Button(string text)
        {
            return new Button
            {
                Content = text,
                MinWidth = 100,
                Padding = new Thickness(8, 5, 8, 5)
            };
        }

        private void Warn(string text)
        {
            MessageBox.Show(
                this,
                text,
                "CE Tools",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        private void RefreshItems()
        {
            _itemsPanel.Children.Clear();
            foreach (BatchCivilItem item in _items)
            {
                var check = new CheckBox
                {
                    IsChecked = item.IsSelected,
                    Content = string.IsNullOrWhiteSpace(item.SecondaryStyle)
                        ? string.Format("{0} — {1}", item.Name, item.Style)
                        : string.Format(
                            "{0} — {1} | {2}",
                            item.Name,
                            item.Style,
                            item.SecondaryStyle),
                    Margin = new Thickness(0, 3, 0, 3)
                };
                check.Checked += delegate { item.IsSelected = true; };
                check.Unchecked += delegate { item.IsSelected = false; };
                _itemsPanel.Children.Add(check);
            }
        }
    }
}

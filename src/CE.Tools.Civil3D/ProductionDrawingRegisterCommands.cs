using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.ProductionDrawingRegisterCommands))]

namespace CETools.Civil3D
{
    public sealed class ProductionDrawingRegisterCommands
    {
        [CommandMethod("CE_TOOLS", "CE_DRAWINGREGISTEREDIT", CommandFlags.Modal | CommandFlags.Redraw)]
        public void EditDrawingRegister()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            ProductionDrawingRegisterData result;
            EditForProduction(
                document,
                ReadLayoutSeeds(document.Database),
                "Save Register",
                out result);
        }

        internal static bool EditForProduction(
            Document document,
            IEnumerable<ProductionDrawingSeed> seeds,
            string actionText,
            out ProductionDrawingRegisterData result)
        {
            result = null;
            if (document == null) return false;
            ProductionDrawingRegisterData data = ProductionDrawingRegisterStore.Read(
                document.Database);
            IDictionary<string, string> project =
                ProjectSetupCommands.ReadSharedProjectMetadata(document.Database);
            data.ApplyProjectDefaults(project);
            data.MergeSeeds(seeds ?? Enumerable.Empty<ProductionDrawingSeed>());
            data.ApplyRowDefaults();

            var window = new ProductionDrawingRegisterWindow(
                data,
                string.IsNullOrWhiteSpace(actionText)
                    ? "Save"
                    : actionText);
            AcApplication.ShowModalWindow(window);
            if (!window.Accepted) return false;

            result = window.BuildResult();
            result.ApplyRowDefaults();
            ProductionDrawingRegisterStore.Write(document.Database, result);
            ProjectSetupCommands.MergeSharedProjectMetadata(
                document.Database,
                result.Headers);
            ProjectSetupCommands.RefreshInformationTables(document);
            document.Editor.WriteMessage(
                "\nCE drawing register saved. Rows={0}; title metadata is linked to production layouts and exports.",
                result.Rows.Count);
            return true;
        }

        internal static List<ProductionDrawingSeed> ReadLayoutSeeds(Database database)
        {
            var result = new List<ProductionDrawingSeed>();
            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                DBDictionary layouts = transaction.GetObject(
                    database.LayoutDictionaryId,
                    OpenMode.ForRead,
                    false) as DBDictionary;
                if (layouts == null) return result;
                foreach (DBDictionaryEntry entry in layouts)
                {
                    Layout layout = transaction.GetObject(
                        entry.Value,
                        OpenMode.ForRead,
                        false) as Layout;
                    if (layout == null || layout.ModelType) continue;
                    result.Add(new ProductionDrawingSeed(
                        layout.LayoutName,
                        layout.LayoutName,
                        "Project drawing",
                        "Existing",
                        "As shown"));
                }
            }
            return result;
        }
    }

    internal sealed class ProductionDrawingSeed
    {
        internal ProductionDrawingSeed(
            string layout,
            string title,
            string purpose,
            string paper,
            string scale)
        {
            Layout = layout ?? string.Empty;
            Title = title ?? string.Empty;
            Purpose = purpose ?? string.Empty;
            Paper = paper ?? string.Empty;
            Scale = scale ?? string.Empty;
        }
        internal string Layout { get; private set; }
        internal string Title { get; private set; }
        internal string Purpose { get; private set; }
        internal string Paper { get; private set; }
        internal string Scale { get; private set; }
    }

    internal sealed class ProductionDrawingRegisterRow
    {
        public string DrawingNumber { get; set; }
        public string Layout { get; set; }
        public string Title { get; set; }
        public string Purpose { get; set; }
        public string Paper { get; set; }
        public string Scale { get; set; }
        public string Stage { get; set; }
        public string Revision { get; set; }
        public string IssueDate { get; set; }

        internal ProductionDrawingRegisterRow Clone()
        {
            return new ProductionDrawingRegisterRow
            {
                DrawingNumber = DrawingNumber ?? string.Empty,
                Layout = Layout ?? string.Empty,
                Title = Title ?? string.Empty,
                Purpose = Purpose ?? string.Empty,
                Paper = Paper ?? string.Empty,
                Scale = Scale ?? string.Empty,
                Stage = Stage ?? string.Empty,
                Revision = Revision ?? string.Empty,
                IssueDate = IssueDate ?? string.Empty
            };
        }
    }

    internal sealed class ProductionDrawingRegisterData
    {
        internal static readonly string[] HeaderFields =
        {
            "Project Name",
            "Project Number",
            "Client",
            "Company",
            "Project Stage",
            "Revision",
            "Issue Date",
            "Drawing Number Prefix",
            "Designed By",
            "Drawn By",
            "Checked By",
            "Approved By",
            "Title Block Source"
        };

        internal ProductionDrawingRegisterData()
        {
            Headers = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            foreach (string field in HeaderFields) Headers[field] = string.Empty;
            Rows = new List<ProductionDrawingRegisterRow>();
        }

        internal IDictionary<string, string> Headers { get; private set; }
        internal List<ProductionDrawingRegisterRow> Rows { get; private set; }

        internal string Header(string name)
        {
            string value;
            return Headers.TryGetValue(name, out value)
                ? value ?? string.Empty
                : string.Empty;
        }

        internal void ApplyProjectDefaults(IDictionary<string, string> project)
        {
            foreach (string field in HeaderFields)
            {
                if (string.Equals(field, "Title Block Source", StringComparison.OrdinalIgnoreCase))
                    continue;
                string existing = Header(field);
                string value;
                if (string.IsNullOrWhiteSpace(existing) &&
                    project != null && project.TryGetValue(field, out value))
                    Headers[field] = value ?? string.Empty;
            }
            if (string.IsNullOrWhiteSpace(Header("Issue Date")))
                Headers["Issue Date"] = DateTime.Today.ToString(
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(Header("Drawing Number Prefix")))
                Headers["Drawing Number Prefix"] = "CE";
            if (string.IsNullOrWhiteSpace(Header("Title Block Source")))
            {
                string bundled = ProductionTitleBlockManager.FindBundledSource();
                if (!string.IsNullOrWhiteSpace(bundled))
                    Headers["Title Block Source"] = bundled;
            }
        }

        internal void MergeSeeds(IEnumerable<ProductionDrawingSeed> seeds)
        {
            foreach (ProductionDrawingSeed seed in seeds)
            {
                if (seed == null || string.IsNullOrWhiteSpace(seed.Layout)) continue;
                ProductionDrawingRegisterRow row = Find(seed.Layout);
                if (row == null)
                {
                    row = new ProductionDrawingRegisterRow
                    {
                        Layout = seed.Layout,
                        Title = seed.Title,
                        Purpose = seed.Purpose,
                        Paper = seed.Paper,
                        Scale = seed.Scale
                    };
                    Rows.Add(row);
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(row.Title)) row.Title = seed.Title;
                    if (string.IsNullOrWhiteSpace(row.Purpose)) row.Purpose = seed.Purpose;
                    if (string.IsNullOrWhiteSpace(row.Paper)) row.Paper = seed.Paper;
                    if (string.IsNullOrWhiteSpace(row.Scale)) row.Scale = seed.Scale;
                }
            }
        }

        internal void ApplyRowDefaults()
        {
            string prefix = Header("Drawing Number Prefix");
            string stage = Header("Project Stage");
            string revision = Header("Revision");
            string issueDate = Header("Issue Date");
            int next = 1;
            foreach (ProductionDrawingRegisterRow row in Rows)
            {
                if (string.IsNullOrWhiteSpace(row.DrawingNumber))
                    row.DrawingNumber = (string.IsNullOrWhiteSpace(prefix) ? "CE" : prefix) +
                        "-" + next.ToString("000", CultureInfo.InvariantCulture);
                if (string.IsNullOrWhiteSpace(row.Title)) row.Title = row.Layout;
                if (string.IsNullOrWhiteSpace(row.Purpose)) row.Purpose = "Project drawing";
                if (string.IsNullOrWhiteSpace(row.Scale)) row.Scale = "As shown";
                if (string.IsNullOrWhiteSpace(row.Stage)) row.Stage = stage;
                if (string.IsNullOrWhiteSpace(row.Revision)) row.Revision = revision;
                if (string.IsNullOrWhiteSpace(row.IssueDate)) row.IssueDate = issueDate;
                next++;
            }
        }

        internal ProductionDrawingRegisterRow Find(string layout)
        {
            return Rows.FirstOrDefault(row => string.Equals(
                row.Layout,
                layout,
                StringComparison.OrdinalIgnoreCase));
        }

        internal ProductionDrawingRegisterData Clone()
        {
            var result = new ProductionDrawingRegisterData();
            foreach (KeyValuePair<string, string> pair in Headers)
                result.Headers[pair.Key] = pair.Value ?? string.Empty;
            result.Rows.Clear();
            result.Rows.AddRange(Rows.Select(row => row.Clone()));
            return result;
        }
    }

    internal static class ProductionDrawingRegisterStore
    {
        private const string RootName = "CE_TOOLS";
        private const string RecordName = "DRAWING_REGISTER_METADATA";

        internal static ProductionDrawingRegisterData Read(Database database)
        {
            var result = new ProductionDrawingRegisterData();
            if (database == null) return result;
            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                DBDictionary named = transaction.GetObject(
                    database.NamedObjectsDictionaryId,
                    OpenMode.ForRead,
                    false) as DBDictionary;
                if (named == null || !named.Contains(RootName)) return result;
                DBDictionary root = transaction.GetObject(
                    named.GetAt(RootName),
                    OpenMode.ForRead,
                    false) as DBDictionary;
                if (root == null || !root.Contains(RecordName)) return result;
                Xrecord record = transaction.GetObject(
                    root.GetAt(RecordName),
                    OpenMode.ForRead,
                    false) as Xrecord;
                if (record == null || record.Data == null) return result;
                foreach (TypedValue value in record.Data)
                {
                    string text = value.Value as string;
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    string[] parts = text.Split('|');
                    if (parts.Length == 3 && parts[0] == "H")
                        result.Headers[Decode(parts[1])] = Decode(parts[2]);
                    else if (parts.Length == 10 && parts[0] == "R")
                    {
                        result.Rows.Add(new ProductionDrawingRegisterRow
                        {
                            DrawingNumber = Decode(parts[1]),
                            Layout = Decode(parts[2]),
                            Title = Decode(parts[3]),
                            Purpose = Decode(parts[4]),
                            Paper = Decode(parts[5]),
                            Scale = Decode(parts[6]),
                            Stage = Decode(parts[7]),
                            Revision = Decode(parts[8]),
                            IssueDate = Decode(parts[9])
                        });
                    }
                }
            }
            return result;
        }

        internal static void Write(
            Database database,
            ProductionDrawingRegisterData data)
        {
            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                DBDictionary named = transaction.GetObject(
                    database.NamedObjectsDictionaryId,
                    OpenMode.ForWrite,
                    false) as DBDictionary;
                DBDictionary root;
                if (named.Contains(RootName))
                    root = transaction.GetObject(
                        named.GetAt(RootName),
                        OpenMode.ForWrite,
                        false) as DBDictionary;
                else
                {
                    root = new DBDictionary();
                    named.SetAt(RootName, root);
                    transaction.AddNewlyCreatedDBObject(root, true);
                }
                Xrecord record;
                if (root.Contains(RecordName))
                    record = transaction.GetObject(
                        root.GetAt(RecordName),
                        OpenMode.ForWrite,
                        false) as Xrecord;
                else
                {
                    record = new Xrecord();
                    root.SetAt(RecordName, record);
                    transaction.AddNewlyCreatedDBObject(record, true);
                }
                var values = new List<TypedValue>
                {
                    new TypedValue((int)DxfCode.Text, "SCHEMA|1")
                };
                foreach (string field in ProductionDrawingRegisterData.HeaderFields)
                    values.Add(new TypedValue(
                        (int)DxfCode.Text,
                        "H|" + Encode(field) + "|" + Encode(data.Header(field))));
                foreach (ProductionDrawingRegisterRow row in data.Rows)
                {
                    values.Add(new TypedValue(
                        (int)DxfCode.Text,
                        string.Join("|", new[]
                        {
                            "R",
                            Encode(row.DrawingNumber),
                            Encode(row.Layout),
                            Encode(row.Title),
                            Encode(row.Purpose),
                            Encode(row.Paper),
                            Encode(row.Scale),
                            Encode(row.Stage),
                            Encode(row.Revision),
                            Encode(row.IssueDate)
                        })));
                }
                record.Data = new ResultBuffer(values.ToArray());
                transaction.Commit();
            }
        }

        private static string Encode(string value)
        {
            return Convert.ToBase64String(
                Encoding.UTF8.GetBytes(value ?? string.Empty));
        }

        private static string Decode(string value)
        {
            try
            {
                return Encoding.UTF8.GetString(
                    Convert.FromBase64String(value ?? string.Empty));
            }
            catch
            {
                return string.Empty;
            }
        }
    }

    internal sealed class ProductionDrawingRegisterWindow : Window
    {
        private readonly IDictionary<string, TextBox> _headers =
            new Dictionary<string, TextBox>(StringComparer.OrdinalIgnoreCase);
        private readonly ObservableCollection<ProductionDrawingRegisterRow> _rows;
        private readonly DataGrid _grid;

        internal ProductionDrawingRegisterWindow(
            ProductionDrawingRegisterData source,
            string actionText)
        {
            Title = "CE Tools - Drawing Titles and Register";
            Width = 1180;
            Height = 760;
            MinWidth = 860;
            MinHeight = 560;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.CanResizeWithGrip;

            _rows = new ObservableCollection<ProductionDrawingRegisterRow>(
                source.Rows.Select(row => row.Clone()));
            var root = new DockPanel { Margin = new Thickness(14) };
            Content = root;

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0)
            };
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(buttons);
            var add = Button("Add Drawing", 105);
            add.Click += delegate
            {
                _rows.Add(new ProductionDrawingRegisterRow
                {
                    Stage = Value("Project Stage"),
                    Revision = Value("Revision"),
                    IssueDate = Value("Issue Date"),
                    Scale = "As shown"
                });
            };
            buttons.Children.Add(add);
            var remove = Button("Remove Selected", 125);
            remove.Margin = new Thickness(6, 0, 0, 0);
            remove.Click += delegate
            {
                ProductionDrawingRegisterRow row =
                    _grid.SelectedItem as ProductionDrawingRegisterRow;
                if (row != null) _rows.Remove(row);
            };
            buttons.Children.Add(remove);
            var cancel = Button("Cancel", 90);
            cancel.IsCancel = true;
            cancel.Margin = new Thickness(18, 0, 0, 0);
            cancel.Click += delegate { DialogResult = false; };
            buttons.Children.Add(cancel);
            var save = Button(actionText, 145);
            save.IsDefault = true;
            save.Margin = new Thickness(6, 0, 0, 0);
            save.Click += delegate
            {
                _grid.CommitEdit(DataGridEditingUnit.Cell, true);
                _grid.CommitEdit(DataGridEditingUnit.Row, true);
                if (_rows.Any(row => string.IsNullOrWhiteSpace(row.Layout)))
                {
                    MessageBox.Show(
                        "Every drawing-register row must have a layout name.",
                        "CE Tools",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }
                Accepted = true;
                DialogResult = true;
            };
            buttons.Children.Add(save);

            var heading = new TextBlock
            {
                Text = "Drawing titles, title block information and drawing register",
                FontSize = 20,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 4)
            };
            DockPanel.SetDock(heading, Dock.Top);
            root.Children.Add(heading);
            var note = new TextBlock
            {
                Text = "Edit project issue data and every sheet in one popup. The saved values drive drawing titles, title-block attributes, on-sheet registers and Excel indexes.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            };
            DockPanel.SetDock(note, Dock.Top);
            root.Children.Add(note);

            var headerGrid = BuildHeaderGrid(source);
            var headerScroll = new ScrollViewer
            {
                Content = headerGrid,
                Height = 215,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(0, 0, 0, 10)
            };
            DockPanel.SetDock(headerScroll, Dock.Top);
            root.Children.Add(headerScroll);

            _grid = new DataGrid
            {
                ItemsSource = _rows,
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                SelectionMode = DataGridSelectionMode.Single,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.All
            };
            AddColumn("Drawing No.", "DrawingNumber", 110);
            AddColumn("Layout", "Layout", 145);
            AddColumn("Title", "Title", 220);
            AddColumn("Purpose / Discipline", "Purpose", 155);
            AddColumn("Paper", "Paper", 75);
            AddColumn("Scale", "Scale", 85);
            AddColumn("Stage", "Stage", 105);
            AddColumn("Revision", "Revision", 75);
            AddColumn("Issue Date", "IssueDate", 100);
            root.Children.Add(_grid);
        }

        internal bool Accepted { get; private set; }

        internal ProductionDrawingRegisterData BuildResult()
        {
            var result = new ProductionDrawingRegisterData();
            foreach (string field in ProductionDrawingRegisterData.HeaderFields)
                result.Headers[field] = Value(field);
            result.Rows.Clear();
            result.Rows.AddRange(_rows.Select(row => row.Clone()));
            return result;
        }

        private Grid BuildHeaderGrid(ProductionDrawingRegisterData source)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(175)
            });
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
            int row = 0;
            foreach (string field in ProductionDrawingRegisterData.HeaderFields)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var label = new TextBlock
                {
                    Text = field,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 3, 10, 3)
                };
                Grid.SetRow(label, row);
                grid.Children.Add(label);
                var editor = new TextBox
                {
                    Text = source.Header(field),
                    Margin = new Thickness(0, 2, 0, 2),
                    Padding = new Thickness(4, 2, 4, 2)
                };
                _headers[field] = editor;
                if (string.Equals(field, "Title Block Source", StringComparison.OrdinalIgnoreCase))
                {
                    var panel = new DockPanel();
                    var browse = Button("Browse...", 85);
                    DockPanel.SetDock(browse, Dock.Right);
                    browse.Margin = new Thickness(6, 2, 0, 2);
                    browse.Click += delegate
                    {
                        var dialog = new Microsoft.Win32.OpenFileDialog
                        {
                            Title = "Select CE Tools title-block source DWG",
                            Filter = "AutoCAD drawing (*.dwg)|*.dwg|All files (*.*)|*.*",
                            CheckFileExists = true,
                            Multiselect = false
                        };
                        if (dialog.ShowDialog() == true)
                            editor.Text = dialog.FileName;
                    };
                    panel.Children.Add(browse);
                    panel.Children.Add(editor);
                    Grid.SetRow(panel, row);
                    Grid.SetColumn(panel, 1);
                    grid.Children.Add(panel);
                }
                else
                {
                    Grid.SetRow(editor, row);
                    Grid.SetColumn(editor, 1);
                    grid.Children.Add(editor);
                }
                row++;
            }
            return grid;
        }

        private void AddColumn(string header, string path, double width)
        {
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = header,
                Binding = new Binding(path)
                {
                    Mode = BindingMode.TwoWay,
                    UpdateSourceTrigger = UpdateSourceTrigger.LostFocus
                },
                Width = new DataGridLength(width)
            });
        }

        private string Value(string name)
        {
            TextBox editor;
            return _headers.TryGetValue(name, out editor)
                ? (editor.Text ?? string.Empty).Trim()
                : string.Empty;
        }

        private static Button Button(string text, double width)
        {
            return new Button
            {
                Content = text,
                MinWidth = width,
                Padding = new Thickness(8, 4, 8, 4)
            };
        }
    }

    internal static class ProductionTitleBlockManager
    {
        internal static string FindBundledSource()
        {
            try
            {
                string folder = Path.GetDirectoryName(
                    Assembly.GetExecutingAssembly().Location) ?? string.Empty;
                string path = Path.GetFullPath(Path.Combine(
                    folder,
                    "..",
                    "..",
                    "Resources",
                    "TitleBlocks",
                    "CE TOOLS - TITLE BLOCKS.dwg"));
                return File.Exists(path) ? path : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        internal static ObjectId TryInsert(
            Database destination,
            Transaction transaction,
            BlockTableRecord paperSpace,
            string sourcePath,
            string paperName,
            Point3d insertion,
            ProductionDrawingRegisterData register,
            ProductionDrawingRegisterRow row,
            out string diagnostic)
        {
            diagnostic = string.Empty;
            if (destination == null || transaction == null || paperSpace == null ||
                string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                diagnostic = "No readable title-block source DWG was selected.";
                return ObjectId.Null;
            }

            try
            {
                string blockName;
                using (var source = new Database(false, true))
                {
                    source.ReadDwgFile(sourcePath, FileShare.Read, true, string.Empty);
                    source.CloseInput(true);
                    ObjectId sourceBlockId = FindBestBlock(
                        source,
                        paperName,
                        out blockName);
                    if (sourceBlockId.IsNull)
                    {
                        diagnostic = "No compatible " + paperName +
                            " attributed block definition was found in the selected DWG.";
                        return ObjectId.Null;
                    }
                    var ids = new ObjectIdCollection();
                    ids.Add(sourceBlockId);
                    var mapping = new IdMapping();
                    source.WblockCloneObjects(
                        ids,
                        destination.BlockTableId,
                        mapping,
                        DuplicateRecordCloning.Replace,
                        false);
                }

                BlockTable blockTable = transaction.GetObject(
                    destination.BlockTableId,
                    OpenMode.ForRead,
                    false) as BlockTable;
                if (blockTable == null || !blockTable.Has(blockName))
                {
                    diagnostic = "The title-block definition could not be cloned into the active drawing.";
                    return ObjectId.Null;
                }

                ObjectId definitionId = blockTable[blockName];
                var reference = new BlockReference(insertion, definitionId);
                reference.SetDatabaseDefaults(destination);
                paperSpace.AppendEntity(reference);
                transaction.AddNewlyCreatedDBObject(reference, true);

                BlockTableRecord definition = transaction.GetObject(
                    definitionId,
                    OpenMode.ForRead,
                    false) as BlockTableRecord;
                IDictionary<string, string> values = BuildAttributeValues(register, row);
                foreach (ObjectId id in definition)
                {
                    AttributeDefinition attribute = transaction.GetObject(
                        id,
                        OpenMode.ForRead,
                        false) as AttributeDefinition;
                    if (attribute == null || attribute.Constant) continue;
                    var value = new AttributeReference();
                    value.SetAttributeFromBlock(attribute, reference.BlockTransform);
                    value.TextString = ResolveAttributeValue(
                        attribute.Tag,
                        attribute.TextString,
                        values);
                    reference.AttributeCollection.AppendAttribute(value);
                    transaction.AddNewlyCreatedDBObject(value, true);
                }
                diagnostic = "Title block inserted from " + Path.GetFileName(sourcePath) + ".";
                return reference.ObjectId;
            }
            catch (System.Exception exception)
            {
                diagnostic = "Title-block source could not be inserted: " + exception.Message;
                return ObjectId.Null;
            }
        }

        private static ObjectId FindBestBlock(
            Database source,
            string paperName,
            out string blockName)
        {
            blockName = string.Empty;
            ObjectId best = ObjectId.Null;
            int bestScore = int.MinValue;
            using (Transaction transaction =
                source.TransactionManager.StartTransaction())
            {
                BlockTable blocks = transaction.GetObject(
                    source.BlockTableId,
                    OpenMode.ForRead,
                    false) as BlockTable;
                foreach (ObjectId id in blocks)
                {
                    BlockTableRecord block = transaction.GetObject(
                        id,
                        OpenMode.ForRead,
                        false) as BlockTableRecord;
                    if (block == null || block.IsLayout || block.IsAnonymous ||
                        block.IsFromExternalReference) continue;
                    int attributes = 0;
                    foreach (ObjectId entityId in block)
                    {
                        if (transaction.GetObject(
                                entityId,
                                OpenMode.ForRead,
                                false) is AttributeDefinition)
                            attributes++;
                    }
                    int score = attributes * 4;
                    string name = block.Name ?? string.Empty;
                    if (name.IndexOf(paperName ?? string.Empty,
                            StringComparison.OrdinalIgnoreCase) >= 0)
                        score += 100;
                    if (name.IndexOf("TITLE", StringComparison.OrdinalIgnoreCase) >= 0)
                        score += 25;
                    if (score > bestScore && attributes > 0)
                    {
                        bestScore = score;
                        best = id;
                        blockName = name;
                    }
                }
            }
            return best;
        }

        private static IDictionary<string, string> BuildAttributeValues(
            ProductionDrawingRegisterData data,
            ProductionDrawingRegisterRow row)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "PROJECT", data.Header("Project Name") },
                { "PROJECTNAME", data.Header("Project Name") },
                { "PROJECTNO", data.Header("Project Number") },
                { "PROJECTNUMBER", data.Header("Project Number") },
                { "CLIENT", data.Header("Client") },
                { "COMPANY", data.Header("Company") },
                { "DRAWINGNO", row.DrawingNumber },
                { "DRAWINGNUMBER", row.DrawingNumber },
                { "DWGNO", row.DrawingNumber },
                { "TITLE", row.Title },
                { "DRAWINGTITLE", row.Title },
                { "SHEETTITLE", row.Title },
                { "PURPOSE", row.Purpose },
                { "DISCIPLINE", row.Purpose },
                { "SCALE", row.Scale },
                { "STAGE", row.Stage },
                { "STATUS", row.Stage },
                { "REV", row.Revision },
                { "REVISION", row.Revision },
                { "DATE", row.IssueDate },
                { "ISSUEDATE", row.IssueDate },
                { "DESIGNED", data.Header("Designed By") },
                { "DESIGNEDBY", data.Header("Designed By") },
                { "DRAWN", data.Header("Drawn By") },
                { "DRAWNBY", data.Header("Drawn By") },
                { "CHECKED", data.Header("Checked By") },
                { "CHECKEDBY", data.Header("Checked By") },
                { "APPROVED", data.Header("Approved By") },
                { "APPROVEDBY", data.Header("Approved By") },
                { "LAYOUT", row.Layout },
                { "SHEET", row.Layout }
            };
            return result;
        }

        private static string ResolveAttributeValue(
            string tag,
            string fallback,
            IDictionary<string, string> values)
        {
            string key = NormalizeTag(tag);
            string value;
            if (values.TryGetValue(key, out value)) return value ?? string.Empty;
            foreach (KeyValuePair<string, string> pair in values)
            {
                if (key.Contains(pair.Key) || pair.Key.Contains(key))
                    return pair.Value ?? string.Empty;
            }
            return fallback ?? string.Empty;
        }

        private static string NormalizeTag(string value)
        {
            return new string((value ?? string.Empty)
                .Where(char.IsLetterOrDigit)
                .Select(char.ToUpperInvariant)
                .ToArray());
        }
    }
}

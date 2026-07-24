using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Windows;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.TypicalDetailsReviewCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Phase 2 typical-detail standards review. DWG and DXF assets are opened in
    /// side databases and inspected without modifying the source files. PDF assets
    /// receive a traceable file-level review record and explicit manual-visual-
    /// review findings because AutoCAD managed APIs do not expose safe PDF content
    /// inspection here. Findings are heuristics for engineering/office review and
    /// never silently alter an approved detail.
    /// </summary>
    public sealed class TypicalDetailsReviewCommands
    {
        private const string CeDictionaryName = "CE_TOOLS";
        private const string LibraryRootRecordName = "TYPICAL_DETAIL_LIBRARY";
        private const string SettingsRecordName = "TYPICAL_DETAIL_REVIEW_SETTINGS";
        private const string ResultsRecordName = "TYPICAL_DETAIL_REVIEW_RESULTS";
        private const string SchemaVersion = "1";
        private const int MaximumStoredRows = 10000;

        private static readonly string[] SupportedExtensions =
        {
            ".dwg", ".dxf", ".pdf"
        };

        private static readonly string[] Categories =
        {
            "Roadworks",
            "Stormwater",
            "Sewer",
            "Water",
            "Earthworks",
            "Parking",
            "Landscaping",
            "Structures",
            "Standard Construction Notes",
            "General Details"
        };

        [CommandMethod("CE_DETAILREVIEWTOOLS", CommandFlags.Modal)]
        public void ReviewTools()
        {
            Document document = ActiveDocument();
            if (document == null)
                return;

            var options = new PromptKeywordOptions(
                "\nTypical-detail standards review [Single/Library/Report/Settings/Information] <Single>: ")
            {
                AllowNone = true
            };
            foreach (string keyword in new[]
            {
                "Single", "Library", "Report", "Settings", "Information"
            })
                options.Keywords.Add(keyword);
            PromptResult result = document.Editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel)
                return;

            string choice = result.Status == PromptStatus.OK
                ? result.StringResult
                : "Single";
            if (choice.Equals("Library", StringComparison.OrdinalIgnoreCase))
                ReviewLibrary();
            else if (choice.Equals("Report", StringComparison.OrdinalIgnoreCase))
                ShowStoredReport();
            else if (choice.Equals("Settings", StringComparison.OrdinalIgnoreCase))
                ConfigureSettings();
            else if (choice.Equals("Information", StringComparison.OrdinalIgnoreCase))
                Information();
            else
                ReviewSingle();
        }

        [CommandMethod("CE_DETAILREVIEWSETTINGS", CommandFlags.Modal)]
        public void ConfigureSettings()
        {
            Document document = ActiveDocument();
            if (document == null)
                return;

            Editor editor = document.Editor;
            ReviewSettings settings = ReviewSettings.Read(document.Database);
            if (!PromptText(editor, "Approved text styles (comma separated; blank = review only)", settings.ApprovedTextStyles, out settings.ApprovedTextStyles))
                return;
            if (!PromptText(editor, "Approved dimension styles (comma separated; blank = review only)", settings.ApprovedDimensionStyles, out settings.ApprovedDimensionStyles))
                return;
            if (!PromptText(editor, "Preferred layer prefix (blank = no prefix rule)", settings.LayerPrefix, out settings.LayerPrefix))
                return;
            if (!PromptText(editor, "Title/title-block keywords", settings.TitleKeywords, out settings.TitleKeywords))
                return;
            if (!PromptText(editor, "Revision keywords", settings.RevisionKeywords, out settings.RevisionKeywords))
                return;
            if (!PromptText(editor, "General-notes keywords", settings.NotesKeywords, out settings.NotesKeywords))
                return;
            if (!PromptText(editor, "Legend keywords", settings.LegendKeywords, out settings.LegendKeywords))
                return;
            if (!PromptText(editor, "North-arrow keywords", settings.NorthArrowKeywords, out settings.NorthArrowKeywords))
                return;
            if (!PromptText(editor, "Company-logo keywords", settings.LogoKeywords, out settings.LogoKeywords))
                return;
            if (!PromptText(editor, "Sheet-number attribute/text keywords", settings.SheetNumberKeywords, out settings.SheetNumberKeywords))
                return;
            if (!PromptText(editor, "Scale keywords", settings.ScaleKeywords, out settings.ScaleKeywords))
                return;
            if (!PromptPositiveInteger(editor, "Maximum library files per review run", settings.MaximumFiles, out settings.MaximumFiles))
                return;
            if (!PromptPositiveInteger(editor, "Maximum findings per file", settings.MaximumFindingsPerFile, out settings.MaximumFindingsPerFile))
                return;

            settings.Write(document.Database);
            editor.WriteMessage(
                "\nCE_DETAILREVIEWSETTINGS saved. Source detail files will remain read-only during review.");
        }

        [CommandMethod("CE_DETAILREVIEW", CommandFlags.Modal)]
        public void ReviewSingle()
        {
            Document document = ActiveDocument();
            if (document == null)
                return;

            string root = ReadLibraryRoot(document.Database);
            var dialog = new OpenFileDialog(
                "Select typical detail for standards review",
                root,
                "dwg;dxf;pdf",
                "CE_DETAILREVIEW",
                OpenFileDialog.OpenFileDialogFlags.DoNotTransferRemoteFiles);
            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                return;

            ReviewSettings settings = ReviewSettings.Read(document.Database);
            DetailReviewResult result = ReviewFile(dialog.Filename, settings);
            StoreResults(document.Database, new[] { result }, false);
            ShowResults(document, new[] { result }, "CE Typical Detail Standards Review");
            WriteSummary(document.Editor, new[] { result });
        }

        [CommandMethod("CE_DETAILREVIEWLIB", CommandFlags.Modal)]
        public void ReviewLibrary()
        {
            Document document = ActiveDocument();
            if (document == null)
                return;

            string root = ReadLibraryRoot(document.Database);
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                document.Editor.WriteMessage(
                    "\nCE_DETAILREVIEWLIB: configure a valid master library with CE_DETAILSETROOT first.");
                return;
            }

            ReviewSettings settings = ReviewSettings.Read(document.Database);
            List<string> files;
            try
            {
                files = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                    .Where(path => SupportedExtensions.Contains(
                        Path.GetExtension(path),
                        StringComparer.OrdinalIgnoreCase))
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .Take(settings.MaximumFiles)
                    .ToList();
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_DETAILREVIEWLIB could not enumerate the library. " + exception.Message);
                return;
            }

            if (files.Count == 0)
            {
                document.Editor.WriteMessage(
                    "\nCE_DETAILREVIEWLIB: no DWG, DXF or PDF files were found under the configured root.");
                return;
            }

            document.Editor.WriteMessage(
                "\nCE_DETAILREVIEWLIB preview: root={0}; files={1}; limit={2}. Source files are opened read-only and are never saved.",
                root,
                files.Count,
                settings.MaximumFiles);
            if (!Confirm(document.Editor, "Review these typical-detail assets and replace the stored review register"))
                return;

            var results = new List<DetailReviewResult>();
            int index = 0;
            foreach (string path in files)
            {
                index++;
                document.Editor.WriteMessage(
                    "\n  Reviewing {0}/{1}: {2}",
                    index,
                    files.Count,
                    Path.GetFileName(path));
                results.Add(ReviewFile(path, settings));
            }

            StoreResults(document.Database, results, true);
            ShowResults(document, results, "CE Typical Details Library Standards Review");
            WriteSummary(document.Editor, results);
        }

        [CommandMethod("CE_DETAILREVIEWREPORT", CommandFlags.Modal)]
        public void ShowStoredReport()
        {
            Document document = ActiveDocument();
            if (document == null)
                return;

            StoredReviewRegister register = ReadStoredResults(document.Database);
            if (register.Results.Count == 0)
            {
                document.Editor.WriteMessage(
                    "\nCE_DETAILREVIEWREPORT: no stored Phase 2 review results exist. Run CE_DETAILREVIEW or CE_DETAILREVIEWLIB.");
                return;
            }

            ShowResults(
                document,
                register.Results,
                "CE Stored Typical Details Standards Review");
            document.Editor.WriteMessage(
                "\nStored review register: schema={0}; reviewed={1}; files={2}; findings={3}.",
                register.Schema,
                register.ReviewedAt,
                register.Results.Count,
                register.Results.Sum(item => item.Findings.Count));
        }

        [CommandMethod("CE_DETAILREVIEWINFO", CommandFlags.Modal)]
        public void Information()
        {
            Document document = ActiveDocument();
            if (document == null)
                return;

            ReviewSettings settings = ReviewSettings.Read(document.Database);
            StoredReviewRegister register = ReadStoredResults(document.Database);
            string root = ReadLibraryRoot(document.Database);
            document.Editor.WriteMessage(
                "\nCE Typical Details Phase 2 Information" +
                "\n  Library root: " + (string.IsNullOrWhiteSpace(root) ? "<Not configured>" : root) +
                "\n  Stored review schema: " + register.Schema +
                "\n  Last review: " + register.ReviewedAt +
                "\n  Stored files: " + register.Results.Count +
                "\n  Stored findings: " + register.Results.Sum(item => item.Findings.Count) +
                "\n  Approved text styles: " + EmptyAsAny(settings.ApprovedTextStyles) +
                "\n  Approved dimension styles: " + EmptyAsAny(settings.ApprovedDimensionStyles) +
                "\n  Preferred layer prefix: " + EmptyAsAny(settings.LayerPrefix) +
                "\n  Maximum files/run: " + settings.MaximumFiles +
                "\n  Maximum findings/file: " + settings.MaximumFindingsPerFile +
                "\n  DWG/DXF review is read-only. PDF content remains a manual visual-review requirement." +
                "\n  Findings are consistency/improvement prompts, not automatic engineering approval or source-file modification.");
        }

        private static DetailReviewResult ReviewFile(string path, ReviewSettings settings)
        {
            string fullPath;
            try { fullPath = Path.GetFullPath(path); }
            catch { fullPath = path ?? string.Empty; }

            var result = new DetailReviewResult(
                fullPath,
                Path.GetExtension(fullPath).TrimStart('.').ToUpperInvariant(),
                Categorise(fullPath),
                File.Exists(fullPath) ? new FileInfo(fullPath).LastWriteTimeUtc : DateTime.MinValue);

            if (!File.Exists(fullPath))
            {
                result.Add("Error", "File", "File does not exist", fullPath);
                return result;
            }

            string extension = Path.GetExtension(fullPath).ToLowerInvariant();
            if (extension == ".pdf")
            {
                ReviewPdfFile(result, settings);
                result.Trim(settings.MaximumFindingsPerFile);
                return result;
            }

            try
            {
                using (Database sourceDatabase = OpenSideDatabase(fullPath, extension))
                {
                    ReviewDatabase(sourceDatabase, result, settings);
                }
            }
            catch (System.Exception exception)
            {
                result.Add(
                    "Error",
                    "File",
                    "AutoCAD could not open or inspect the asset read-only",
                    exception.GetType().Name + ": " + exception.Message);
            }
            result.Trim(settings.MaximumFindingsPerFile);
            return result;
        }

        private static Database OpenSideDatabase(string path, string extension)
        {
            var database = new Database(false, true);
            try
            {
                if (extension.Equals(".dwg", StringComparison.OrdinalIgnoreCase))
                {
                    database.ReadDwgFile(
                        path,
                        FileOpenMode.OpenForReadAndAllShare,
                        false,
                        string.Empty);
                    database.CloseInput(true);
                    return database;
                }

                if (extension.Equals(".dxf", StringComparison.OrdinalIgnoreCase))
                {
                    InvokeDxfIn(database, path);
                    return database;
                }

                throw new NotSupportedException("Unsupported detail format: " + extension);
            }
            catch
            {
                database.Dispose();
                throw;
            }
        }

        private static void InvokeDxfIn(Database database, string path)
        {
            foreach (MethodInfo method in typeof(Database).GetMethods(
                BindingFlags.Public | BindingFlags.Instance)
                .Where(item => item.Name.Equals("DxfIn", StringComparison.OrdinalIgnoreCase)))
            {
                ParameterInfo[] parameters = method.GetParameters();
                object[] arguments;
                if (parameters.Length == 1 && parameters[0].ParameterType == typeof(string))
                    arguments = new object[] { path };
                else if (parameters.Length == 2 &&
                         parameters[0].ParameterType == typeof(string) &&
                         parameters[1].ParameterType == typeof(string))
                    arguments = new object[] { path, Path.ChangeExtension(path, ".ce-review.log") };
                else
                    continue;
                try
                {
                    method.Invoke(database, arguments);
                    return;
                }
                catch (TargetInvocationException exception)
                {
                    if (exception.InnerException != null)
                        throw exception.InnerException;
                    throw;
                }
            }
            throw new MissingMethodException(
                "The installed AutoCAD managed API exposes no supported Database.DxfIn overload.");
        }

        private static void ReviewPdfFile(DetailReviewResult result, ReviewSettings settings)
        {
            FileInfo info = new FileInfo(result.Path);
            result.Add(
                "Info",
                "File",
                "PDF file-level metadata recorded",
                "Size=" + info.Length.ToString("N0", CultureInfo.InvariantCulture) +
                " bytes; modified UTC=" + info.LastWriteTimeUtc.ToString("s", CultureInfo.InvariantCulture));

            string searchable = Normalise(Path.GetFileNameWithoutExtension(result.Path));
            CheckFilenameKeyword(result, "Title format", settings.TitleKeywordList, searchable);
            CheckFilenameKeyword(result, "Revision", settings.RevisionKeywordList, searchable);
            CheckFilenameKeyword(result, "Sheet numbering", settings.SheetNumberKeywordList, searchable);

            foreach (string area in new[]
            {
                "Title format", "Revision table", "General notes", "Legends",
                "North arrow", "Fonts", "Dimensions", "Company logo",
                "Sheet numbering", "Layers", "Lineweights", "Scales",
                "Symbols", "Missing dimensions/notes/callouts/labels"
            })
            {
                result.Add(
                    "Review",
                    area,
                    "Manual visual review required for PDF content",
                    "Phase 2 does not rasterise/OCR or silently infer PDF drawing content.");
            }
        }

        private static void CheckFilenameKeyword(
            DetailReviewResult result,
            string area,
            IReadOnlyList<string> keywords,
            string searchable)
        {
            bool found = keywords.Any(keyword => searchable.Contains(Normalise(keyword)));
            result.Add(
                found ? "Info" : "Review",
                area,
                found
                    ? "Filename contains a configured keyword"
                    : "Filename does not evidence the configured item",
                Path.GetFileName(result.Path));
        }

        private static void ReviewDatabase(
            Database database,
            DetailReviewResult result,
            ReviewSettings settings)
        {
            var inventory = new DrawingInventory();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                ReadNamedTables(database, transaction, inventory);
                ReadLayoutsAndEntities(database, transaction, inventory);
            }

            result.Add(
                "Info",
                "Inventory",
                "Drawing inventory captured",
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Layouts={0}; entities={1}; blocks={2}; layers={3}; text styles={4}; dimension styles={5}",
                    inventory.LayoutCount,
                    inventory.EntityCount,
                    inventory.BlockReferences.Count,
                    inventory.Layers.Count,
                    inventory.TextStyles.Count,
                    inventory.DimensionStyles.Count));

            ReviewTitleAndSheet(result, inventory, settings);
            ReviewRevision(result, inventory, settings);
            ReviewNotesLegendsNorthLogo(result, inventory, settings);
            ReviewFontsAndDimensions(result, inventory, settings);
            ReviewLayersAndLineweights(result, inventory, settings);
            ReviewScalesAndSymbols(result, inventory, settings);
            ReviewMissingContent(result, inventory, settings);
            ReviewOverrides(result, inventory);
        }

        private static void ReadNamedTables(
            Database database,
            Transaction transaction,
            DrawingInventory inventory)
        {
            TextStyleTable textStyles = transaction.GetObject(
                database.TextStyleTableId,
                OpenMode.ForRead,
                false) as TextStyleTable;
            foreach (ObjectId id in textStyles)
            {
                TextStyleTableRecord style = transaction.GetObject(
                    id,
                    OpenMode.ForRead,
                    false) as TextStyleTableRecord;
                if (style == null)
                    continue;
                inventory.TextStyles.Add(new TextStyleInfo(
                    style.Name,
                    style.FileName,
                    style.BigFontFileName));
            }

            DimStyleTable dimStyles = transaction.GetObject(
                database.DimStyleTableId,
                OpenMode.ForRead,
                false) as DimStyleTable;
            foreach (ObjectId id in dimStyles)
            {
                DimStyleTableRecord style = transaction.GetObject(
                    id,
                    OpenMode.ForRead,
                    false) as DimStyleTableRecord;
                if (style != null)
                    inventory.DimensionStyles.Add(style.Name);
            }

            LayerTable layers = transaction.GetObject(
                database.LayerTableId,
                OpenMode.ForRead,
                false) as LayerTable;
            foreach (ObjectId id in layers)
            {
                LayerTableRecord layer = transaction.GetObject(
                    id,
                    OpenMode.ForRead,
                    false) as LayerTableRecord;
                if (layer == null)
                    continue;
                string lineType = string.Empty;
                try
                {
                    LinetypeTableRecord record = transaction.GetObject(
                        layer.LinetypeObjectId,
                        OpenMode.ForRead,
                        false) as LinetypeTableRecord;
                    lineType = record == null ? string.Empty : record.Name;
                }
                catch { }
                inventory.Layers.Add(new LayerInfo(
                    layer.Name,
                    layer.Color == null ? string.Empty : layer.Color.ToString(),
                    layer.LineWeight.ToString(),
                    lineType,
                    layer.IsOff,
                    layer.IsFrozen,
                    layer.IsLocked));
            }
        }

        private static void ReadLayoutsAndEntities(
            Database database,
            Transaction transaction,
            DrawingInventory inventory)
        {
            DBDictionary layouts = transaction.GetObject(
                database.LayoutDictionaryId,
                OpenMode.ForRead,
                false) as DBDictionary;
            if (layouts != null)
            {
                foreach (DBDictionaryEntry entry in layouts)
                {
                    Layout layout = transaction.GetObject(
                        entry.Value,
                        OpenMode.ForRead,
                        false) as Layout;
                    if (layout == null)
                        continue;
                    inventory.LayoutCount++;
                    inventory.LayoutNames.Add(layout.LayoutName);
                    inventory.LayoutDetails.Add(BuildLayoutEvidence(layout));
                }
            }

            BlockTable blocks = transaction.GetObject(
                database.BlockTableId,
                OpenMode.ForRead,
                false) as BlockTable;
            foreach (ObjectId blockId in blocks)
            {
                BlockTableRecord block = transaction.GetObject(
                    blockId,
                    OpenMode.ForRead,
                    false) as BlockTableRecord;
                if (block == null)
                    continue;
                bool isDefinition = !block.IsLayout;
                if (isDefinition && !block.IsAnonymous && !block.IsFromExternalReference)
                    inventory.BlockDefinitions.Add(block.Name);

                foreach (ObjectId entityId in block)
                {
                    Entity entity = transaction.GetObject(
                        entityId,
                        OpenMode.ForRead,
                        false) as Entity;
                    if (entity == null)
                        continue;
                    inventory.EntityCount++;
                    CountEntity(inventory, entity);
                    ReadEntityContent(transaction, inventory, entity, block.IsLayout, block.Name);
                }
            }
        }

        private static string BuildLayoutEvidence(Layout layout)
        {
            var values = new List<string> { layout.LayoutName };
            object canonical = ReadProperty(layout, "CanonicalMediaName");
            object plotType = ReadProperty(layout, "PlotType");
            object scale = ReadProperty(layout, "CustomPrintScale");
            if (canonical != null) values.Add("media=" + canonical);
            if (plotType != null) values.Add("plot=" + plotType);
            if (scale != null) values.Add("scale=" + scale);
            return string.Join("; ", values);
        }

        private static void CountEntity(DrawingInventory inventory, Entity entity)
        {
            string type = entity.GetType().Name;
            int count;
            inventory.EntityTypes.TryGetValue(type, out count);
            inventory.EntityTypes[type] = count + 1;

            if (entity.ColorIndex != 256 && entity.ColorIndex != 0)
                inventory.NonByLayerColourCount++;
            if (entity.LineWeight.ToString().IndexOf("ByLayer", StringComparison.OrdinalIgnoreCase) < 0)
                inventory.NonByLayerLineweightCount++;
            if (entity.Layer.Equals("0", StringComparison.OrdinalIgnoreCase))
                inventory.LayerZeroEntityCount++;

            if (entity is Dimension) inventory.DimensionCount++;
            if (entity is Leader || entity.GetType().Name.IndexOf("MLeader", StringComparison.OrdinalIgnoreCase) >= 0)
                inventory.LeaderCount++;
            if (entity is Table) inventory.TableCount++;
            if (entity.GetType().Name.IndexOf("RasterImage", StringComparison.OrdinalIgnoreCase) >= 0)
                inventory.RasterImageCount++;
            if (entity.GetType().Name.IndexOf("Viewport", StringComparison.OrdinalIgnoreCase) >= 0)
                inventory.ViewportCount++;
        }

        private static void ReadEntityContent(
            Transaction transaction,
            DrawingInventory inventory,
            Entity entity,
            bool inLayoutBlock,
            string ownerBlockName)
        {
            DBText text = entity as DBText;
            if (text != null)
            {
                AddText(inventory, text.TextString, text.TextStyleName, text.Height, entity.Layer, inLayoutBlock);
                return;
            }

            MText mtext = entity as MText;
            if (mtext != null)
            {
                string style = string.Empty;
                try
                {
                    TextStyleTableRecord record = transaction.GetObject(
                        mtext.TextStyleId,
                        OpenMode.ForRead,
                        false) as TextStyleTableRecord;
                    style = record == null ? string.Empty : record.Name;
                }
                catch { }
                AddText(inventory, mtext.Text, style, mtext.TextHeight, entity.Layer, inLayoutBlock);
                return;
            }

            Dimension dimension = entity as Dimension;
            if (dimension != null)
            {
                string style = string.Empty;
                try
                {
                    DimStyleTableRecord record = transaction.GetObject(
                        dimension.DimensionStyle,
                        OpenMode.ForRead,
                        false) as DimStyleTableRecord;
                    style = record == null ? string.Empty : record.Name;
                }
                catch { }
                inventory.UsedDimensionStyles.Add(style);
                AddSearchable(inventory, dimension.DimensionText, "Dimension");
                return;
            }

            Table table = entity as Table;
            if (table != null)
            {
                for (int row = 0; row < table.Rows.Count; row++)
                {
                    for (int column = 0; column < table.Columns.Count; column++)
                    {
                        string value;
                        try { value = table.Cells[row, column].TextString; }
                        catch { value = string.Empty; }
                        AddSearchable(inventory, value, "Table");
                    }
                }
                return;
            }

            BlockReference block = entity as BlockReference;
            if (block != null)
            {
                string blockName = ResolveBlockName(transaction, block);
                var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (ObjectId attributeId in block.AttributeCollection)
                {
                    AttributeReference attribute = transaction.GetObject(
                        attributeId,
                        OpenMode.ForRead,
                        false) as AttributeReference;
                    if (attribute == null)
                        continue;
                    attributes[attribute.Tag ?? string.Empty] = attribute.TextString ?? string.Empty;
                    AddSearchable(
                        inventory,
                        (attribute.Tag ?? string.Empty) + " " + (attribute.TextString ?? string.Empty),
                        "Attribute");
                }
                inventory.BlockReferences.Add(new BlockReferenceInfo(
                    blockName,
                    entity.Layer,
                    inLayoutBlock,
                    ownerBlockName,
                    attributes));
                AddSearchable(inventory, blockName, "Block");
            }
        }

        private static void AddText(
            DrawingInventory inventory,
            string value,
            string style,
            double height,
            string layer,
            bool inLayout)
        {
            inventory.TextCount++;
            inventory.UsedTextStyles.Add(style ?? string.Empty);
            inventory.TextHeights.Add(height);
            inventory.TextItems.Add(new TextItem(
                value ?? string.Empty,
                style ?? string.Empty,
                height,
                layer ?? string.Empty,
                inLayout));
            AddSearchable(inventory, value, "Text");
        }

        private static void AddSearchable(DrawingInventory inventory, string value, string source)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;
            inventory.SearchableItems.Add(new SearchableItem(
                Normalise(value),
                source,
                value.Trim()));
        }

        private static string ResolveBlockName(Transaction transaction, BlockReference block)
        {
            try
            {
                BlockTableRecord definition = transaction.GetObject(
                    block.DynamicBlockTableRecord.IsNull
                        ? block.BlockTableRecord
                        : block.DynamicBlockTableRecord,
                    OpenMode.ForRead,
                    false) as BlockTableRecord;
                return definition == null ? "<Unknown block>" : definition.Name;
            }
            catch
            {
                return "<Unknown block>";
            }
        }

        private static void ReviewTitleAndSheet(
            DetailReviewResult result,
            DrawingInventory inventory,
            ReviewSettings settings)
        {
            List<BlockReferenceInfo> titleCandidates = inventory.BlockReferences
                .Where(block => block.InLayout &&
                    (ContainsKeyword(block.Name, settings.TitleKeywordList) ||
                     block.Attributes.Keys.Any(tag =>
                        ContainsKeyword(tag, settings.TitleKeywordList) ||
                        ContainsKeyword(tag, settings.SheetNumberKeywordList))))
                .ToList();
            bool titleText = ContainsSearchable(inventory, settings.TitleKeywordList);
            result.Add(
                titleCandidates.Count > 0 || titleText ? "Pass" : "Warning",
                "Title format",
                titleCandidates.Count > 0 || titleText
                    ? "A title/title-block indicator was found"
                    : "No configured title/title-block indicator was found",
                titleCandidates.Count > 0
                    ? string.Join(", ", titleCandidates.Select(item => item.Name).Distinct())
                    : "Search keywords: " + settings.TitleKeywords);

            bool sheetNumber = titleCandidates.Any(candidate =>
                candidate.Attributes.Any(attribute =>
                    ContainsKeyword(attribute.Key, settings.SheetNumberKeywordList) &&
                    !string.IsNullOrWhiteSpace(attribute.Value))) ||
                ContainsSearchable(inventory, settings.SheetNumberKeywordList);
            result.Add(
                sheetNumber ? "Pass" : "Warning",
                "Sheet numbering",
                sheetNumber
                    ? "A sheet/drawing-number indicator was found"
                    : "No populated configured sheet-number indicator was found",
                "Keywords: " + settings.SheetNumberKeywords);
        }

        private static void ReviewRevision(
            DetailReviewResult result,
            DrawingInventory inventory,
            ReviewSettings settings)
        {
            bool found = ContainsSearchable(inventory, settings.RevisionKeywordList) ||
                inventory.BlockReferences.Any(block =>
                    ContainsKeyword(block.Name, settings.RevisionKeywordList) ||
                    block.Attributes.Keys.Any(tag => ContainsKeyword(tag, settings.RevisionKeywordList)));
            result.Add(
                found ? "Pass" : "Warning",
                "Revision table",
                found
                    ? "Revision text, table, block or attribute indicator was found"
                    : "No configured revision indicator was found",
                "Tables=" + inventory.TableCount + "; keywords=" + settings.RevisionKeywords);
        }

        private static void ReviewNotesLegendsNorthLogo(
            DetailReviewResult result,
            DrawingInventory inventory,
            ReviewSettings settings)
        {
            AddKeywordFinding(result, inventory, "General notes", settings.NotesKeywordList, settings.NotesKeywords);
            AddKeywordFinding(result, inventory, "Legends", settings.LegendKeywordList, settings.LegendKeywords);
            AddKeywordFinding(result, inventory, "North arrow", settings.NorthArrowKeywordList, settings.NorthArrowKeywords);

            bool logo = ContainsSearchable(inventory, settings.LogoKeywordList) ||
                inventory.BlockReferences.Any(block => ContainsKeyword(block.Name, settings.LogoKeywordList)) ||
                inventory.RasterImageCount > 0;
            result.Add(
                logo ? "Pass" : "Review",
                "Company logo",
                logo
                    ? "A configured logo indicator or raster image was found"
                    : "No company-logo indicator was found",
                "Raster images=" + inventory.RasterImageCount + "; keywords=" + settings.LogoKeywords);
        }

        private static void AddKeywordFinding(
            DetailReviewResult result,
            DrawingInventory inventory,
            string area,
            IReadOnlyList<string> keywords,
            string originalKeywords)
        {
            List<SearchableItem> matches = FindSearchable(inventory, keywords).Take(5).ToList();
            result.Add(
                matches.Count > 0 ? "Pass" : "Review",
                area,
                matches.Count > 0
                    ? "A configured indicator was found"
                    : "No configured indicator was found",
                matches.Count > 0
                    ? string.Join(" | ", matches.Select(item => item.Original))
                    : "Keywords: " + originalKeywords);
        }

        private static void ReviewFontsAndDimensions(
            DetailReviewResult result,
            DrawingInventory inventory,
            ReviewSettings settings)
        {
            List<string> approvedText = Split(settings.ApprovedTextStyles);
            List<string> usedText = inventory.UsedTextStyles
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value)
                .ToList();
            List<string> unapprovedText = approvedText.Count == 0
                ? new List<string>()
                : usedText.Where(style => !approvedText.Contains(style, StringComparer.OrdinalIgnoreCase)).ToList();
            result.Add(
                approvedText.Count == 0 ? "Review" : unapprovedText.Count == 0 ? "Pass" : "Warning",
                "Fonts and text styles",
                approvedText.Count == 0
                    ? "No approved text-style list is configured"
                    : unapprovedText.Count == 0
                        ? "All used text styles are in the configured approved list"
                        : "Unapproved text styles are used",
                "Used=" + JoinOrNone(usedText) +
                "; fonts=" + JoinOrNone(inventory.TextStyles.Select(style => style.Name + "=" + style.Font).ToList()) +
                (unapprovedText.Count == 0 ? string.Empty : "; unapproved=" + string.Join(", ", unapprovedText)));

            List<string> approvedDims = Split(settings.ApprovedDimensionStyles);
            List<string> usedDims = inventory.UsedDimensionStyles
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value)
                .ToList();
            List<string> unapprovedDims = approvedDims.Count == 0
                ? new List<string>()
                : usedDims.Where(style => !approvedDims.Contains(style, StringComparer.OrdinalIgnoreCase)).ToList();
            result.Add(
                inventory.DimensionCount == 0 ? "Warning" :
                    approvedDims.Count == 0 ? "Review" :
                    unapprovedDims.Count == 0 ? "Pass" : "Warning",
                "Dimensions and dimension styles",
                inventory.DimensionCount == 0
                    ? "No dimension entities were found"
                    : approvedDims.Count == 0
                        ? "Dimensions exist, but no approved dimension-style list is configured"
                        : unapprovedDims.Count == 0
                            ? "Dimension styles match the configured approved list"
                            : "Unapproved dimension styles are used",
                "Dimensions=" + inventory.DimensionCount +
                "; used styles=" + JoinOrNone(usedDims) +
                (unapprovedDims.Count == 0 ? string.Empty : "; unapproved=" + string.Join(", ", unapprovedDims)));
        }

        private static void ReviewLayersAndLineweights(
            DetailReviewResult result,
            DrawingInventory inventory,
            ReviewSettings settings)
        {
            List<string> nonPrefixed = string.IsNullOrWhiteSpace(settings.LayerPrefix)
                ? new List<string>()
                : inventory.Layers
                    .Select(layer => layer.Name)
                    .Where(name =>
                        !name.Equals("0", StringComparison.OrdinalIgnoreCase) &&
                        !name.Equals("DEFPOINTS", StringComparison.OrdinalIgnoreCase) &&
                        !name.StartsWith(settings.LayerPrefix, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(name => name)
                    .ToList();
            result.Add(
                string.IsNullOrWhiteSpace(settings.LayerPrefix) ? "Review" :
                    nonPrefixed.Count == 0 ? "Pass" : "Warning",
                "Layer naming",
                string.IsNullOrWhiteSpace(settings.LayerPrefix)
                    ? "No preferred layer prefix is configured"
                    : nonPrefixed.Count == 0
                        ? "All non-system layers use the configured prefix"
                        : "Layers outside the configured prefix were found",
                nonPrefixed.Count == 0
                    ? "Layers=" + inventory.Layers.Count
                    : string.Join(", ", nonPrefixed.Take(30)));

            List<string> defaultLineweightLayers = inventory.Layers
                .Where(layer =>
                    layer.LineWeight.IndexOf("Default", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    layer.LineWeight.IndexOf("000", StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(layer => layer.Name)
                .ToList();
            result.Add(
                defaultLineweightLayers.Count == 0 ? "Pass" : "Review",
                "Layer lineweights",
                defaultLineweightLayers.Count == 0
                    ? "No default/zero layer lineweights were identified"
                    : "Layers with default/zero lineweight require office-standard review",
                JoinOrNone(defaultLineweightLayers.Take(30).ToList()));

            result.Add(
                inventory.LayerZeroEntityCount == 0 ? "Pass" : "Review",
                "Layer use",
                inventory.LayerZeroEntityCount == 0
                    ? "No model/detail entities were counted on layer 0"
                    : "Entities on layer 0 require review",
                "Layer-0 entity count=" + inventory.LayerZeroEntityCount);
        }

        private static void ReviewScalesAndSymbols(
            DetailReviewResult result,
            DrawingInventory inventory,
            ReviewSettings settings)
        {
            bool scaleText = ContainsSearchable(inventory, settings.ScaleKeywordList);
            bool layoutScale = inventory.LayoutDetails.Any(value =>
                value.IndexOf("scale=", StringComparison.OrdinalIgnoreCase) >= 0);
            result.Add(
                scaleText || layoutScale ? "Pass" : "Review",
                "Scales",
                scaleText || layoutScale
                    ? "A scale indicator was found in text or layout settings"
                    : "No scale indicator was identified",
                "Viewports=" + inventory.ViewportCount +
                "; layouts=" + string.Join(" | ", inventory.LayoutDetails.Take(10)));

            List<string> symbols = inventory.BlockReferences
                .Select(block => block.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name)
                .ToList();
            result.Add(
                symbols.Count > 0 ? "Pass" : "Review",
                "Symbols and blocks",
                symbols.Count > 0
                    ? "Block/symbol references were found"
                    : "No block/symbol references were found",
                JoinOrNone(symbols.Take(40).ToList()));
        }

        private static void ReviewMissingContent(
            DetailReviewResult result,
            DrawingInventory inventory,
            ReviewSettings settings)
        {
            if (inventory.DimensionCount == 0)
                result.Add("Warning", "Missing dimensions", "No dimensions were found", "Manual engineering dimension review required");
            if (inventory.LeaderCount == 0)
                result.Add("Review", "Missing callouts", "No Leader/MLeader-like callouts were found", "Manual callout and label review required");
            if (inventory.TextCount == 0)
                result.Add("Warning", "Missing notes and labels", "No DBText/MText was found", "Manual notes/labels review required");
            else
            {
                int shortLabels = inventory.TextItems.Count(item =>
                    !string.IsNullOrWhiteSpace(item.Value) && item.Value.Trim().Length <= 30);
                int longNotes = inventory.TextItems.Count(item =>
                    !string.IsNullOrWhiteSpace(item.Value) && item.Value.Trim().Length > 60);
                result.Add(
                    shortLabels > 0 ? "Pass" : "Review",
                    "Labels",
                    shortLabels > 0 ? "Short label-like text exists" : "No short label-like text was identified",
                    "Short text items=" + shortLabels);
                result.Add(
                    longNotes > 0 || ContainsSearchable(inventory, settings.NotesKeywordList) ? "Pass" : "Review",
                    "Notes",
                    longNotes > 0 || ContainsSearchable(inventory, settings.NotesKeywordList)
                        ? "Long note-like text or configured notes keyword exists"
                        : "No long note-like text or configured notes keyword was identified",
                    "Long text items=" + longNotes);
            }
        }

        private static void ReviewOverrides(
            DetailReviewResult result,
            DrawingInventory inventory)
        {
            result.Add(
                inventory.NonByLayerColourCount == 0 ? "Pass" : "Review",
                "Colour consistency",
                inventory.NonByLayerColourCount == 0
                    ? "No explicit non-ByLayer colours were counted"
                    : "Explicit entity-colour overrides require review",
                "Non-ByLayer colour count=" + inventory.NonByLayerColourCount);
            result.Add(
                inventory.NonByLayerLineweightCount == 0 ? "Pass" : "Review",
                "Lineweight consistency",
                inventory.NonByLayerLineweightCount == 0
                    ? "No explicit non-ByLayer lineweights were counted"
                    : "Explicit entity-lineweight overrides require review",
                "Non-ByLayer lineweight count=" + inventory.NonByLayerLineweightCount);
        }

        private static bool ContainsSearchable(
            DrawingInventory inventory,
            IReadOnlyList<string> keywords)
        {
            return FindSearchable(inventory, keywords).Any();
        }

        private static IEnumerable<SearchableItem> FindSearchable(
            DrawingInventory inventory,
            IReadOnlyList<string> keywords)
        {
            return inventory.SearchableItems.Where(item =>
                keywords.Any(keyword => item.Normalised.Contains(Normalise(keyword))));
        }

        private static bool ContainsKeyword(string value, IReadOnlyList<string> keywords)
        {
            string normalised = Normalise(value);
            return keywords.Any(keyword => normalised.Contains(Normalise(keyword)));
        }

        private static void ShowResults(
            Document document,
            IEnumerable<DetailReviewResult> results,
            string title)
        {
            List<DetailReviewResult> resultList = results.ToList();
            var rows = new List<IList<string>>();
            foreach (DetailReviewResult result in resultList)
            {
                foreach (ReviewFinding finding in result.Findings)
                {
                    rows.Add(new List<string>
                    {
                        Path.GetFileName(result.Path),
                        result.Format,
                        result.Category,
                        finding.Severity,
                        finding.Area,
                        finding.Finding,
                        finding.Evidence,
                        result.Path
                    });
                }
            }
            if (rows.Count == 0)
            {
                rows.Add(new List<string>
                {
                    "No findings", "", "", "", "", "", "", ""
                });
            }

            string note =
                "Files=" + resultList.Count +
                " | findings=" + rows.Count +
                " | warnings/errors=" + resultList.Sum(item => item.Findings.Count(finding =>
                    finding.Severity.Equals("Warning", StringComparison.OrdinalIgnoreCase) ||
                    finding.Severity.Equals("Error", StringComparison.OrdinalIgnoreCase))) +
                ". Source DWG/DXF/PDF assets were not modified. Findings require office/engineering review.";
            GridReportPresenter.ShowReportAndOfferTable(
                document,
                title,
                note,
                new List<string>
                {
                    "File", "Format", "Category", "Severity", "Review Area", "Finding", "Evidence", "Source Path"
                },
                rows,
                "CE Typical Details Standards Review");
        }

        private static void WriteSummary(
            Editor editor,
            IEnumerable<DetailReviewResult> results)
        {
            List<DetailReviewResult> list = results.ToList();
            editor.WriteMessage(
                "\nCE typical-details standards review complete. Files={0}; findings={1}; warnings={2}; errors={3}; PDF manual-review rows={4}.",
                list.Count,
                list.Sum(item => item.Findings.Count),
                list.Sum(item => item.Findings.Count(finding => finding.Severity == "Warning")),
                list.Sum(item => item.Findings.Count(finding => finding.Severity == "Error")),
                list.Sum(item => item.Findings.Count(finding =>
                    finding.Finding.IndexOf("Manual visual review required", StringComparison.OrdinalIgnoreCase) >= 0)));
            editor.WriteMessage(
                "\nNo reviewed source file was saved, changed, normalised or approved automatically.");
        }

        private static void StoreResults(
            Database database,
            IEnumerable<DetailReviewResult> results,
            bool replace)
        {
            List<DetailReviewResult> combined = replace
                ? new List<DetailReviewResult>()
                : ReadStoredResults(database).Results;
            foreach (DetailReviewResult result in results)
            {
                combined.RemoveAll(item =>
                    item.Path.Equals(result.Path, StringComparison.OrdinalIgnoreCase));
                combined.Add(result);
            }

            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                DBDictionary ce = OpenOrCreateCeDictionary(database, transaction);
                Xrecord record = OpenOrCreateRecord(ce, ResultsRecordName, transaction);
                var values = new List<TypedValue>
                {
                    new TypedValue((int)DxfCode.Text, "Schema=" + SchemaVersion),
                    new TypedValue((int)DxfCode.Text, "ReviewedAt=" + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture))
                };
                int storedRows = 0;
                foreach (DetailReviewResult result in combined
                    .OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase))
                {
                    values.Add(new TypedValue(
                        (int)DxfCode.Text,
                        "File=" + Encode(result.Path) + "|" +
                        Encode(result.Format) + "|" +
                        Encode(result.Category) + "|" +
                        result.ModifiedUtc.ToString("o", CultureInfo.InvariantCulture)));
                    foreach (ReviewFinding finding in result.Findings)
                    {
                        if (storedRows++ >= MaximumStoredRows)
                            break;
                        values.Add(new TypedValue(
                            (int)DxfCode.Text,
                            "Finding=" + Encode(result.Path) + "|" +
                            Encode(finding.Severity) + "|" +
                            Encode(finding.Area) + "|" +
                            Encode(finding.Finding) + "|" +
                            Encode(finding.Evidence)));
                    }
                    if (storedRows >= MaximumStoredRows)
                        break;
                }
                record.Data = new ResultBuffer(values.ToArray());
                transaction.Commit();
            }
        }

        private static StoredReviewRegister ReadStoredResults(Database database)
        {
            string schema = SchemaVersion;
            string reviewedAt = "<Never>";
            var files = new Dictionary<string, DetailReviewResult>(StringComparer.OrdinalIgnoreCase);
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                DBDictionary nod = transaction.GetObject(
                    database.NamedObjectsDictionaryId,
                    OpenMode.ForRead,
                    false) as DBDictionary;
                if (nod == null || !nod.Contains(CeDictionaryName))
                    return new StoredReviewRegister(schema, reviewedAt, files.Values);
                DBDictionary ce = transaction.GetObject(
                    nod.GetAt(CeDictionaryName),
                    OpenMode.ForRead,
                    false) as DBDictionary;
                if (ce == null || !ce.Contains(ResultsRecordName))
                    return new StoredReviewRegister(schema, reviewedAt, files.Values);
                Xrecord record = transaction.GetObject(
                    ce.GetAt(ResultsRecordName),
                    OpenMode.ForRead,
                    false) as Xrecord;
                if (record == null || record.Data == null)
                    return new StoredReviewRegister(schema, reviewedAt, files.Values);

                foreach (TypedValue value in record.Data)
                {
                    string text = value.Value as string;
                    if (string.IsNullOrWhiteSpace(text))
                        continue;
                    if (text.StartsWith("Schema=", StringComparison.OrdinalIgnoreCase))
                    {
                        schema = text.Substring("Schema=".Length);
                        continue;
                    }
                    if (text.StartsWith("ReviewedAt=", StringComparison.OrdinalIgnoreCase))
                    {
                        reviewedAt = text.Substring("ReviewedAt=".Length);
                        continue;
                    }
                    if (text.StartsWith("File=", StringComparison.OrdinalIgnoreCase))
                    {
                        string[] parts = SplitEncoded(text.Substring("File=".Length), 4);
                        DateTime modified;
                        DateTime.TryParse(parts[3], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out modified);
                        files[parts[0]] = new DetailReviewResult(parts[0], parts[1], parts[2], modified);
                        continue;
                    }
                    if (text.StartsWith("Finding=", StringComparison.OrdinalIgnoreCase))
                    {
                        string[] parts = SplitEncoded(text.Substring("Finding=".Length), 5);
                        DetailReviewResult result;
                        if (!files.TryGetValue(parts[0], out result))
                        {
                            result = new DetailReviewResult(parts[0], string.Empty, Categorise(parts[0]), DateTime.MinValue);
                            files[parts[0]] = result;
                        }
                        result.Add(parts[1], parts[2], parts[3], parts[4]);
                    }
                }
            }
            return new StoredReviewRegister(schema, reviewedAt, files.Values);
        }

        private static DBDictionary OpenOrCreateCeDictionary(
            Database database,
            Transaction transaction)
        {
            DBDictionary nod = transaction.GetObject(
                database.NamedObjectsDictionaryId,
                OpenMode.ForWrite,
                false) as DBDictionary;
            if (nod.Contains(CeDictionaryName))
                return transaction.GetObject(
                    nod.GetAt(CeDictionaryName),
                    OpenMode.ForWrite,
                    false) as DBDictionary;
            var ce = new DBDictionary();
            nod.SetAt(CeDictionaryName, ce);
            transaction.AddNewlyCreatedDBObject(ce, true);
            return ce;
        }

        private static Xrecord OpenOrCreateRecord(
            DBDictionary dictionary,
            string name,
            Transaction transaction)
        {
            if (dictionary.Contains(name))
                return transaction.GetObject(
                    dictionary.GetAt(name),
                    OpenMode.ForWrite,
                    false) as Xrecord;
            var record = new Xrecord();
            dictionary.SetAt(name, record);
            transaction.AddNewlyCreatedDBObject(record, true);
            return record;
        }

        private static string ReadLibraryRoot(Database database)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                DBDictionary nod = transaction.GetObject(
                    database.NamedObjectsDictionaryId,
                    OpenMode.ForRead,
                    false) as DBDictionary;
                if (nod == null || !nod.Contains(CeDictionaryName))
                    return string.Empty;
                DBDictionary ce = transaction.GetObject(
                    nod.GetAt(CeDictionaryName),
                    OpenMode.ForRead,
                    false) as DBDictionary;
                if (ce == null || !ce.Contains(LibraryRootRecordName))
                    return string.Empty;
                Xrecord record = transaction.GetObject(
                    ce.GetAt(LibraryRootRecordName),
                    OpenMode.ForRead,
                    false) as Xrecord;
                if (record == null || record.Data == null)
                    return string.Empty;
                return record.Data.AsArray()
                    .Where(value => value.TypeCode == (int)DxfCode.Text)
                    .Select(value => value.Value as string)
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
            }
        }

        private static string Categorise(string path)
        {
            string searchable = Normalise(path);
            foreach (string category in Categories)
            {
                if (searchable.Contains(Normalise(category)))
                    return category;
            }
            if (searchable.Contains("KERB") || searchable.Contains("ROAD") || searchable.Contains("PAVEMENT")) return "Roadworks";
            if (searchable.Contains("STORM") || searchable.Contains("DRAIN") || searchable.Contains("CULVERT") || searchable.Contains("HEADWALL")) return "Stormwater";
            if (searchable.Contains("SEWER") || searchable.Contains("MANHOLE")) return "Sewer";
            if (searchable.Contains("WATER") || searchable.Contains("HYDRANT") || searchable.Contains("VALVE")) return "Water";
            if (searchable.Contains("PARK")) return "Parking";
            if (searchable.Contains("STRUCT") || searchable.Contains("CONCRETE") || searchable.Contains("REINFORC")) return "Structures";
            if (searchable.Contains("NOTE")) return "Standard Construction Notes";
            return "General Details";
        }

        private static string Normalise(string value)
        {
            return new string((value ?? string.Empty)
                .ToUpperInvariant()
                .Select(character => char.IsLetterOrDigit(character) ? character : ' ')
                .ToArray());
        }

        private static List<string> Split(string value)
        {
            return (value ?? string.Empty)
                .Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(item => item.Trim())
                .Where(item => item.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string JoinOrNone(IReadOnlyList<string> values)
        {
            return values == null || values.Count == 0
                ? "<None>"
                : string.Join(", ", values);
        }

        private static string EmptyAsAny(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "<Not configured>" : value;
        }

        private static string Encode(string value)
        {
            return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value ?? string.Empty));
        }

        private static string Decode(string value)
        {
            try { return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(value ?? string.Empty)); }
            catch { return string.Empty; }
        }

        private static string[] SplitEncoded(string payload, int expected)
        {
            string[] raw = (payload ?? string.Empty).Split('|');
            var values = new string[expected];
            for (int index = 0; index < expected; index++)
                values[index] = index < raw.Length ? Decode(raw[index]) : string.Empty;
            return values;
        }

        private static object ReadProperty(object owner, string propertyName)
        {
            if (owner == null)
                return null;
            PropertyInfo property = owner.GetType().GetProperty(
                propertyName,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (property == null || !property.CanRead)
                return null;
            try { return property.GetValue(owner, null); }
            catch { return null; }
        }

        private static bool PromptText(
            Editor editor,
            string label,
            string current,
            out string value)
        {
            var options = new PromptStringOptions(
                "\n" + label + " <" + (current ?? string.Empty) + ">: ")
            {
                AllowSpaces = true
            };
            PromptResult result = editor.GetString(options);
            if (result.Status == PromptStatus.Cancel)
            {
                value = current;
                return false;
            }
            value = result.Status == PromptStatus.None
                ? current
                : result.StringResult.Trim();
            return true;
        }

        private static bool PromptPositiveInteger(
            Editor editor,
            string label,
            int current,
            out int value)
        {
            var options = new PromptIntegerOptions(
                "\n" + label + " <" + current.ToString(CultureInfo.InvariantCulture) + ">: ")
            {
                AllowNegative = false,
                AllowZero = false,
                UseDefaultValue = true,
                DefaultValue = current
            };
            PromptIntegerResult result = editor.GetInteger(options);
            value = result.Status == PromptStatus.OK ? result.Value : current;
            return result.Status == PromptStatus.OK;
        }

        private static bool Confirm(Editor editor, string message)
        {
            var options = new PromptKeywordOptions(
                "\n" + message + "? [Yes/No] <No>: ")
            {
                AllowNone = true
            };
            options.Keywords.Add("Yes");
            options.Keywords.Add("No");
            PromptResult result = editor.GetKeywords(options);
            return result.Status == PromptStatus.OK &&
                   result.StringResult.Equals("Yes", StringComparison.OrdinalIgnoreCase);
        }

        private static Document ActiveDocument()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
        }

        private sealed class DetailReviewResult
        {
            public DetailReviewResult(string path, string format, string category, DateTime modifiedUtc)
            {
                Path = path ?? string.Empty;
                Format = format ?? string.Empty;
                Category = category ?? string.Empty;
                ModifiedUtc = modifiedUtc;
                Findings = new List<ReviewFinding>();
            }
            public string Path { get; }
            public string Format { get; }
            public string Category { get; }
            public DateTime ModifiedUtc { get; }
            public List<ReviewFinding> Findings { get; }
            public void Add(string severity, string area, string finding, string evidence)
            {
                Findings.Add(new ReviewFinding(severity, area, finding, evidence));
            }
            public void Trim(int maximum)
            {
                if (Findings.Count <= maximum)
                    return;
                int removed = Findings.Count - maximum + 1;
                Findings.RemoveRange(maximum - 1, Findings.Count - (maximum - 1));
                Findings.Add(new ReviewFinding(
                    "Review",
                    "Report limit",
                    "Additional findings were truncated",
                    removed.ToString(CultureInfo.InvariantCulture) + " row(s) omitted; increase MaximumFindingsPerFile to review more."));
            }
        }

        private sealed class ReviewFinding
        {
            public ReviewFinding(string severity, string area, string finding, string evidence)
            {
                Severity = severity ?? string.Empty;
                Area = area ?? string.Empty;
                Finding = finding ?? string.Empty;
                Evidence = evidence ?? string.Empty;
            }
            public string Severity { get; }
            public string Area { get; }
            public string Finding { get; }
            public string Evidence { get; }
        }

        private sealed class StoredReviewRegister
        {
            public StoredReviewRegister(string schema, string reviewedAt, IEnumerable<DetailReviewResult> results)
            {
                Schema = schema ?? SchemaVersion;
                ReviewedAt = reviewedAt ?? "<Never>";
                Results = results == null
                    ? new List<DetailReviewResult>()
                    : results.OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToList();
            }
            public string Schema { get; }
            public string ReviewedAt { get; }
            public List<DetailReviewResult> Results { get; }
        }

        private sealed class DrawingInventory
        {
            public DrawingInventory()
            {
                LayoutNames = new List<string>();
                LayoutDetails = new List<string>();
                EntityTypes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                BlockDefinitions = new List<string>();
                BlockReferences = new List<BlockReferenceInfo>();
                TextStyles = new List<TextStyleInfo>();
                UsedTextStyles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                DimensionStyles = new List<string>();
                UsedDimensionStyles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                Layers = new List<LayerInfo>();
                TextHeights = new List<double>();
                TextItems = new List<TextItem>();
                SearchableItems = new List<SearchableItem>();
            }
            public int LayoutCount { get; set; }
            public int EntityCount { get; set; }
            public int TextCount { get; set; }
            public int DimensionCount { get; set; }
            public int LeaderCount { get; set; }
            public int TableCount { get; set; }
            public int RasterImageCount { get; set; }
            public int ViewportCount { get; set; }
            public int NonByLayerColourCount { get; set; }
            public int NonByLayerLineweightCount { get; set; }
            public int LayerZeroEntityCount { get; set; }
            public List<string> LayoutNames { get; }
            public List<string> LayoutDetails { get; }
            public Dictionary<string, int> EntityTypes { get; }
            public List<string> BlockDefinitions { get; }
            public List<BlockReferenceInfo> BlockReferences { get; }
            public List<TextStyleInfo> TextStyles { get; }
            public HashSet<string> UsedTextStyles { get; }
            public List<string> DimensionStyles { get; }
            public HashSet<string> UsedDimensionStyles { get; }
            public List<LayerInfo> Layers { get; }
            public List<double> TextHeights { get; }
            public List<TextItem> TextItems { get; }
            public List<SearchableItem> SearchableItems { get; }
        }

        private sealed class TextStyleInfo
        {
            public TextStyleInfo(string name, string font, string bigFont)
            {
                Name = name ?? string.Empty;
                Font = font ?? string.Empty;
                BigFont = bigFont ?? string.Empty;
            }
            public string Name { get; }
            public string Font { get; }
            public string BigFont { get; }
        }

        private sealed class LayerInfo
        {
            public LayerInfo(
                string name,
                string colour,
                string lineWeight,
                string lineType,
                bool isOff,
                bool isFrozen,
                bool isLocked)
            {
                Name = name ?? string.Empty;
                Colour = colour ?? string.Empty;
                LineWeight = lineWeight ?? string.Empty;
                LineType = lineType ?? string.Empty;
                IsOff = isOff;
                IsFrozen = isFrozen;
                IsLocked = isLocked;
            }
            public string Name { get; }
            public string Colour { get; }
            public string LineWeight { get; }
            public string LineType { get; }
            public bool IsOff { get; }
            public bool IsFrozen { get; }
            public bool IsLocked { get; }
        }

        private sealed class BlockReferenceInfo
        {
            public BlockReferenceInfo(
                string name,
                string layer,
                bool inLayout,
                string ownerBlock,
                IDictionary<string, string> attributes)
            {
                Name = name ?? string.Empty;
                Layer = layer ?? string.Empty;
                InLayout = inLayout;
                OwnerBlock = ownerBlock ?? string.Empty;
                Attributes = new Dictionary<string, string>(
                    attributes ?? new Dictionary<string, string>(),
                    StringComparer.OrdinalIgnoreCase);
            }
            public string Name { get; }
            public string Layer { get; }
            public bool InLayout { get; }
            public string OwnerBlock { get; }
            public Dictionary<string, string> Attributes { get; }
        }

        private sealed class TextItem
        {
            public TextItem(string value, string style, double height, string layer, bool inLayout)
            {
                Value = value ?? string.Empty;
                Style = style ?? string.Empty;
                Height = height;
                Layer = layer ?? string.Empty;
                InLayout = inLayout;
            }
            public string Value { get; }
            public string Style { get; }
            public double Height { get; }
            public string Layer { get; }
            public bool InLayout { get; }
        }

        private sealed class SearchableItem
        {
            public SearchableItem(string normalised, string source, string original)
            {
                Normalised = normalised ?? string.Empty;
                Source = source ?? string.Empty;
                Original = original ?? string.Empty;
            }
            public string Normalised { get; }
            public string Source { get; }
            public string Original { get; }
        }

        private sealed class ReviewSettings
        {
            public string ApprovedTextStyles = string.Empty;
            public string ApprovedDimensionStyles = string.Empty;
            public string LayerPrefix = string.Empty;
            public string TitleKeywords = "TITLE,TITLE BLOCK,DRAWING TITLE,BORDER";
            public string RevisionKeywords = "REV,REVISION,REVISIONS";
            public string NotesKeywords = "GENERAL NOTES,NOTES,CONSTRUCTION NOTES";
            public string LegendKeywords = "LEGEND,KEY";
            public string NorthArrowKeywords = "NORTH,NORTH ARROW";
            public string LogoKeywords = "LOGO,COMPANY";
            public string SheetNumberKeywords = "SHEET,DRAWING NO,DWG NO,DRAWING NUMBER,SHEET NO";
            public string ScaleKeywords = "SCALE,NTS,NOT TO SCALE";
            public int MaximumFiles = 500;
            public int MaximumFindingsPerFile = 100;

            public IReadOnlyList<string> TitleKeywordList => Split(TitleKeywords);
            public IReadOnlyList<string> RevisionKeywordList => Split(RevisionKeywords);
            public IReadOnlyList<string> NotesKeywordList => Split(NotesKeywords);
            public IReadOnlyList<string> LegendKeywordList => Split(LegendKeywords);
            public IReadOnlyList<string> NorthArrowKeywordList => Split(NorthArrowKeywords);
            public IReadOnlyList<string> LogoKeywordList => Split(LogoKeywords);
            public IReadOnlyList<string> SheetNumberKeywordList => Split(SheetNumberKeywords);
            public IReadOnlyList<string> ScaleKeywordList => Split(ScaleKeywords);

            public static ReviewSettings Read(Database database)
            {
                var settings = new ReviewSettings();
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    DBDictionary nod = transaction.GetObject(
                        database.NamedObjectsDictionaryId,
                        OpenMode.ForRead,
                        false) as DBDictionary;
                    if (nod == null || !nod.Contains(CeDictionaryName))
                        return settings;
                    DBDictionary ce = transaction.GetObject(
                        nod.GetAt(CeDictionaryName),
                        OpenMode.ForRead,
                        false) as DBDictionary;
                    if (ce == null || !ce.Contains(SettingsRecordName))
                        return settings;
                    Xrecord record = transaction.GetObject(
                        ce.GetAt(SettingsRecordName),
                        OpenMode.ForRead,
                        false) as Xrecord;
                    string[] values = record == null || record.Data == null
                        ? new string[0]
                        : record.Data.AsArray()
                            .Where(value => value.TypeCode == (int)DxfCode.Text)
                            .Select(value => Convert.ToString(value.Value, CultureInfo.InvariantCulture))
                            .ToArray();
                    if (values.Length >= 13)
                    {
                        settings.ApprovedTextStyles = values[0];
                        settings.ApprovedDimensionStyles = values[1];
                        settings.LayerPrefix = values[2];
                        settings.TitleKeywords = values[3];
                        settings.RevisionKeywords = values[4];
                        settings.NotesKeywords = values[5];
                        settings.LegendKeywords = values[6];
                        settings.NorthArrowKeywords = values[7];
                        settings.LogoKeywords = values[8];
                        settings.SheetNumberKeywords = values[9];
                        settings.ScaleKeywords = values[10];
                        int.TryParse(values[11], NumberStyles.Integer, CultureInfo.InvariantCulture, out settings.MaximumFiles);
                        int.TryParse(values[12], NumberStyles.Integer, CultureInfo.InvariantCulture, out settings.MaximumFindingsPerFile);
                    }
                }
                settings.Normalize();
                return settings;
            }

            public void Write(Database database)
            {
                Normalize();
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    DBDictionary ce = OpenOrCreateCeDictionary(database, transaction);
                    Xrecord record = OpenOrCreateRecord(ce, SettingsRecordName, transaction);
                    string[] values =
                    {
                        ApprovedTextStyles,
                        ApprovedDimensionStyles,
                        LayerPrefix,
                        TitleKeywords,
                        RevisionKeywords,
                        NotesKeywords,
                        LegendKeywords,
                        NorthArrowKeywords,
                        LogoKeywords,
                        SheetNumberKeywords,
                        ScaleKeywords,
                        MaximumFiles.ToString(CultureInfo.InvariantCulture),
                        MaximumFindingsPerFile.ToString(CultureInfo.InvariantCulture)
                    };
                    record.Data = new ResultBuffer(values
                        .Select(value => new TypedValue((int)DxfCode.Text, value ?? string.Empty))
                        .ToArray());
                    transaction.Commit();
                }
            }

            private void Normalize()
            {
                if (TitleKeywords == null) TitleKeywords = string.Empty;
                if (RevisionKeywords == null) RevisionKeywords = string.Empty;
                if (NotesKeywords == null) NotesKeywords = string.Empty;
                if (LegendKeywords == null) LegendKeywords = string.Empty;
                if (NorthArrowKeywords == null) NorthArrowKeywords = string.Empty;
                if (LogoKeywords == null) LogoKeywords = string.Empty;
                if (SheetNumberKeywords == null) SheetNumberKeywords = string.Empty;
                if (ScaleKeywords == null) ScaleKeywords = string.Empty;
                if (MaximumFiles < 1) MaximumFiles = 500;
                if (MaximumFindingsPerFile < 10) MaximumFindingsPerFile = 100;
            }
        }
    }
}

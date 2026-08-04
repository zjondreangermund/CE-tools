using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.TypicalDetailsCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Phase 1 master typical-details catalogue. The workflow stores a library
    /// root in the current DWG, searches approved DWG/DXF/PDF assets and inserts
    /// DWG details as traceable block references.
    /// </summary>
    public sealed class TypicalDetailsCommands
    {
        private const string CeDictionaryName = "CE_TOOLS";
        private const string LibraryRootRecordName = "TYPICAL_DETAIL_LIBRARY";
        private const string InsertLinkRecordName = "CE_TYPICAL_DETAIL_LINK";
        private const int MaximumDisplayedResults = 100;

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

        [CommandMethod("CE_DETAILTOOLS", CommandFlags.Modal)]
        public void DetailTools()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null)
                return;

            PromptKeywordOptions options = new PromptKeywordOptions(
                "\nTypical Details [SetRoot/Search/Insert/Info] <Search>: ")
            {
                AllowNone = true
            };
            options.Keywords.Add("SetRoot");
            options.Keywords.Add("Search");
            options.Keywords.Add("Insert");
            options.Keywords.Add("Info");

            PromptResult result = document.Editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel)
                return;

            string choice = result.Status == PromptStatus.OK
                ? result.StringResult
                : "Search";

            if (choice.Equals("SetRoot", StringComparison.OrdinalIgnoreCase))
                SetLibraryRoot();
            else if (choice.Equals("Insert", StringComparison.OrdinalIgnoreCase))
                InsertDetail();
            else if (choice.Equals("Info", StringComparison.OrdinalIgnoreCase))
                ShowLibraryInformation();
            else
                SearchLibrary();
        }

        [CommandMethod("CE_DETAILSETROOT", CommandFlags.Modal)]
        public void SetLibraryRoot()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null)
                return;

            string currentRoot = ReadLibraryRoot(document.Database);
            var browser = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select the master CE Tools Typical Details folder",
                ShowNewFolderButton = false,
                SelectedPath = Directory.Exists(currentRoot) ? currentRoot : string.Empty
            };
            if (browser.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

            string root;
            try
            {
                root = Path.GetFullPath(
                    Environment.ExpandEnvironmentVariables(browser.SelectedPath));
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage("\nCE_DETAILSETROOT: invalid folder path. " + exception.Message);
                return;
            }

            if (!Directory.Exists(root))
            {
                document.Editor.WriteMessage("\nCE_DETAILSETROOT: folder not found: " + root);
                return;
            }

            WriteLibraryRoot(document.Database, root);
            int count = EnumerateAssets(root).Count;
            document.Editor.WriteMessage(
                "\nTypical Details master library saved. Supported assets found: " +
                count +
                ". Root: " +
                root);
        }

        [CommandMethod("CE_DETAILSEARCH", CommandFlags.Modal)]
        public void SearchLibrary()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null)
                return;

            Editor editor = document.Editor;
            string root = RequireLibraryRoot(document);
            if (string.IsNullOrWhiteSpace(root))
                return;

            PromptStringOptions options = new PromptStringOptions(
                "\nSearch Typical Details by name, category or keyword <all>: ")
            {
                AllowSpaces = true
            };
            PromptResult result = editor.GetString(options);
            if (result.Status == PromptStatus.Cancel)
                return;

            string query = result.Status == PromptStatus.OK
                ? result.StringResult.Trim()
                : string.Empty;

            List<DetailAsset> matches = FindAssets(root, query, null);
            WriteSearchResults(editor, matches, root);

            editor.WriteMessage(
                "\nPhase 1 catalogue formats: DWG can be inserted; DXF and PDF are indexed for review/reference. " +
                "Only office-approved, engineer-reviewed details should be issued.");
        }

        [CommandMethod("CE_DETAILINSERT", CommandFlags.Modal)]
        public void InsertDetail()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null)
                return;

            Editor editor = document.Editor;
            Database database = document.Database;
            string root = RequireLibraryRoot(document);
            if (string.IsNullOrWhiteSpace(root))
                return;

            PromptStringOptions searchOptions = new PromptStringOptions(
                "\nSearch approved DWG details by name, category or keyword <all>: ")
            {
                AllowSpaces = true
            };
            PromptResult searchResult = editor.GetString(searchOptions);
            if (searchResult.Status == PromptStatus.Cancel)
                return;

            string query = searchResult.Status == PromptStatus.OK
                ? searchResult.StringResult.Trim()
                : string.Empty;

            List<DetailAsset> matches = FindAssets(root, query, ".dwg");
            WriteSearchResults(editor, matches, root);
            if (matches.Count == 0)
                return;

            int displayedCount = Math.Min(matches.Count, MaximumDisplayedResults);
            PromptIntegerOptions numberOptions = new PromptIntegerOptions(
                "\nEnter the DWG detail number to insert: ")
            {
                AllowNegative = false,
                AllowZero = false,
                LowerLimit = 1,
                UpperLimit = displayedCount
            };
            PromptIntegerResult numberResult = editor.GetInteger(numberOptions);
            if (numberResult.Status != PromptStatus.OK)
                return;

            DetailAsset selected = matches[numberResult.Value - 1];

            PromptPointResult pointResult = editor.GetPoint(
                "\nSpecify the typical-detail insertion point: ");
            if (pointResult.Status != PromptStatus.OK)
                return;

            PromptDoubleOptions scaleOptions = new PromptDoubleOptions(
                "\nUniform detail scale <1.0>: ")
            {
                AllowNegative = false,
                AllowZero = false,
                UseDefaultValue = true,
                DefaultValue = 1.0
            };
            PromptDoubleResult scaleResult = editor.GetDouble(scaleOptions);
            if (scaleResult.Status != PromptStatus.OK)
                return;

            PromptDoubleOptions rotationOptions = new PromptDoubleOptions(
                "\nRotation in degrees <0>: ")
            {
                AllowNegative = true,
                AllowZero = true,
                UseDefaultValue = true,
                DefaultValue = 0.0
            };
            PromptDoubleResult rotationResult = editor.GetDouble(rotationOptions);
            if (rotationResult.Status != PromptStatus.OK)
                return;

            ObjectId blockDefinitionId;
            string blockName;
            try
            {
                blockName = CreateUniqueBlockName(
                    database,
                    "CE_DETAIL_" + Path.GetFileNameWithoutExtension(selected.FullPath));

                using (var sourceDatabase = new Database(false, true))
                {
                    sourceDatabase.ReadDwgFile(
                        selected.FullPath,
                        FileShare.Read,
                        true,
                        string.Empty);
                    blockDefinitionId = database.Insert(
                        blockName,
                        sourceDatabase,
                        false);
                }
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_DETAILINSERT could not import the selected DWG. " +
                    exception.Message);
                return;
            }

            ObjectId referenceId;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTableRecord currentSpace = (BlockTableRecord)transaction.GetObject(
                    database.CurrentSpaceId,
                    OpenMode.ForWrite,
                    false);

                var reference = new BlockReference(
                    pointResult.Value,
                    blockDefinitionId)
                {
                    LayerId = database.Clayer,
                    ScaleFactors = new Scale3d(scaleResult.Value),
                    Rotation = rotationResult.Value * Math.PI / 180.0
                };

                referenceId = currentSpace.AppendEntity(reference);
                transaction.AddNewlyCreatedDBObject(reference, true);
                StoreDetailLink(
                    reference,
                    transaction,
                    selected,
                    scaleResult.Value,
                    rotationResult.Value);
                transaction.Commit();
            }

            editor.WriteMessage(
                "\nTypical detail inserted as block '" +
                blockName +
                "'. Source: " +
                selected.RelativePath +
                ". Link handle: " +
                referenceId.Handle +
                ".");
        }

        [CommandMethod("CE_DETAILINFO", CommandFlags.Modal)]
        public void ShowLibraryInformation()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null)
                return;

            Editor editor = document.Editor;
            string root = ReadLibraryRoot(document.Database);
            editor.WriteMessage(
                "\nCE Typical Details Phase 1" +
                "\nLibrary root: " +
                (string.IsNullOrWhiteSpace(root) ? "<not configured>" : root) +
                "\nCategories: " +
                string.Join(", ", Categories) +
                "\nIndexed formats: DWG, DXF, PDF." +
                "\nInsertion: approved DWG details as traceable blocks." +
                "\nPlanned later phases: standards review, missing-note/dimension checks, parametric detail generation, " +
                "dynamic refresh and BOQ linkage.");
        }

        private static string RequireLibraryRoot(Document document)
        {
            string root = ReadLibraryRoot(document.Database);
            if (string.IsNullOrWhiteSpace(root))
            {
                document.Editor.WriteMessage(
                    "\nTypical Details library is not configured. Run CE_DETAILSETROOT first.");
                return null;
            }

            if (!Directory.Exists(root))
            {
                document.Editor.WriteMessage(
                    "\nThe stored Typical Details folder is unavailable: " +
                    root +
                    ". Run CE_DETAILSETROOT to update it.");
                return null;
            }

            return root;
        }

        private static List<DetailAsset> FindAssets(
            string root,
            string query,
            string requiredExtension)
        {
            IEnumerable<DetailAsset> assets = EnumerateAssets(root);
            if (!string.IsNullOrWhiteSpace(requiredExtension))
            {
                assets = assets.Where(
                    asset => asset.Extension.Equals(
                        requiredExtension,
                        StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(query))
            {
                string[] terms = query.Split(
                    new[] { ' ', ',', ';' },
                    StringSplitOptions.RemoveEmptyEntries);

                assets = assets.Where(asset =>
                {
                    string searchable =
                        asset.Category + " " +
                        asset.RelativePath + " " +
                        Path.GetFileNameWithoutExtension(asset.FullPath);
                    return terms.All(
                        term => searchable.IndexOf(
                            term,
                            StringComparison.OrdinalIgnoreCase) >= 0);
                });
            }

            return assets
                .OrderBy(asset => asset.Category)
                .ThenBy(asset => asset.RelativePath)
                .ToList();
        }

        private static List<DetailAsset> EnumerateAssets(string root)
        {
            var assets = new List<DetailAsset>();
            var folders = new Stack<string>();
            folders.Push(root);

            while (folders.Count > 0)
            {
                string folder = folders.Pop();

                try
                {
                    foreach (string child in Directory.GetDirectories(folder))
                        folders.Push(child);
                }
                catch
                {
                    // Continue with folders that are accessible.
                }

                try
                {
                    foreach (string file in Directory.GetFiles(folder))
                    {
                        string extension = Path.GetExtension(file);
                        if (!extension.Equals(".dwg", StringComparison.OrdinalIgnoreCase) &&
                            !extension.Equals(".dxf", StringComparison.OrdinalIgnoreCase) &&
                            !extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                            continue;

                        string relative = GetRelativePath(root, file);
                        assets.Add(
                            new DetailAsset(
                                file,
                                relative,
                                extension,
                                ClassifyCategory(relative)));
                    }
                }
                catch
                {
                    // Continue with folders that are accessible.
                }
            }

            return assets;
        }

        private static void WriteSearchResults(
            Editor editor,
            IList<DetailAsset> matches,
            string root)
        {
            editor.WriteMessage(
                "\nTypical Details results: " +
                matches.Count +
                ". Library: " +
                root);

            int displayed = Math.Min(matches.Count, MaximumDisplayedResults);
            for (int index = 0; index < displayed; index++)
            {
                DetailAsset asset = matches[index];
                editor.WriteMessage(
                    "\n  " +
                    (index + 1) +
                    ". [" +
                    asset.Extension.TrimStart('.').ToUpperInvariant() +
                    "] " +
                    asset.Category +
                    " | " +
                    asset.RelativePath);
            }

            if (matches.Count > displayed)
            {
                editor.WriteMessage(
                    "\n  ... " +
                    (matches.Count - displayed) +
                    " additional results. Refine the search to display them.");
            }
        }

        private static string ClassifyCategory(string relativePath)
        {
            foreach (string category in Categories)
            {
                if (relativePath.IndexOf(
                    category,
                    StringComparison.OrdinalIgnoreCase) >= 0)
                    return category;
            }

            string value = relativePath.ToLowerInvariant();
            if (value.Contains("road") || value.Contains("kerb") || value.Contains("island"))
                return "Roadworks";
            if (value.Contains("storm") || value.Contains("drain") || value.Contains("culvert") || value.Contains("headwall"))
                return "Stormwater";
            if (value.Contains("sewer") || value.Contains("manhole") || value.Contains("inspection chamber"))
                return "Sewer";
            if (value.Contains("water") || value.Contains("valve") || value.Contains("hydrant") || value.Contains("tank"))
                return "Water";
            if (value.Contains("earth") || value.Contains("layerwork") || value.Contains("pavement"))
                return "Earthworks";
            if (value.Contains("parking"))
                return "Parking";
            if (value.Contains("landscape") || value.Contains("plant"))
                return "Landscaping";
            if (value.Contains("bridge") || value.Contains("structure") || value.Contains("reinforcement"))
                return "Structures";
            if (value.Contains("note") || value.Contains("legend"))
                return "Standard Construction Notes";

            return "General Details";
        }

        private static string GetRelativePath(string root, string path)
        {
            string normalizedRoot = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string normalizedPath = Path.GetFullPath(path);

            if (normalizedPath.StartsWith(
                normalizedRoot,
                StringComparison.OrdinalIgnoreCase))
            {
                return normalizedPath
                    .Substring(normalizedRoot.Length)
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }

            return normalizedPath;
        }

        private static string CreateUniqueBlockName(
            Database database,
            string proposedName)
        {
            string cleaned = new string(
                proposedName
                    .Select(character =>
                        char.IsLetterOrDigit(character) ||
                        character == '_' ||
                        character == '-'
                            ? character
                            : '_')
                    .ToArray());

            if (string.IsNullOrWhiteSpace(cleaned))
                cleaned = "CE_DETAIL";

            using (Transaction transaction = database.TransactionManager.StartOpenCloseTransaction())
            {
                BlockTable table = (BlockTable)transaction.GetObject(
                    database.BlockTableId,
                    OpenMode.ForRead,
                    false);

                string candidate = cleaned;
                int suffix = 2;
                while (table.Has(candidate))
                {
                    candidate = cleaned + "_" + suffix;
                    suffix++;
                }

                return candidate;
            }
        }

        private static string ReadLibraryRoot(Database database)
        {
            using (Transaction transaction = database.TransactionManager.StartOpenCloseTransaction())
            {
                DBDictionary namedObjects = (DBDictionary)transaction.GetObject(
                    database.NamedObjectsDictionaryId,
                    OpenMode.ForRead,
                    false);

                if (!namedObjects.Contains(CeDictionaryName))
                    return null;

                DBDictionary ceDictionary = transaction.GetObject(
                    namedObjects.GetAt(CeDictionaryName),
                    OpenMode.ForRead,
                    false) as DBDictionary;

                if (ceDictionary == null ||
                    !ceDictionary.Contains(LibraryRootRecordName))
                    return null;

                Xrecord record = transaction.GetObject(
                    ceDictionary.GetAt(LibraryRootRecordName),
                    OpenMode.ForRead,
                    false) as Xrecord;

                if (record == null || record.Data == null)
                    return null;

                foreach (TypedValue value in record.Data)
                {
                    if (value.TypeCode == (int)DxfCode.Text)
                        return Convert.ToString(value.Value);
                }

                return null;
            }
        }

        private static void WriteLibraryRoot(
            Database database,
            string root)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                DBDictionary namedObjects = (DBDictionary)transaction.GetObject(
                    database.NamedObjectsDictionaryId,
                    OpenMode.ForWrite,
                    false);

                DBDictionary ceDictionary;
                if (namedObjects.Contains(CeDictionaryName))
                {
                    ceDictionary = (DBDictionary)transaction.GetObject(
                        namedObjects.GetAt(CeDictionaryName),
                        OpenMode.ForWrite,
                        false);
                }
                else
                {
                    ceDictionary = new DBDictionary();
                    namedObjects.SetAt(CeDictionaryName, ceDictionary);
                    transaction.AddNewlyCreatedDBObject(ceDictionary, true);
                }

                Xrecord record;
                if (ceDictionary.Contains(LibraryRootRecordName))
                {
                    record = (Xrecord)transaction.GetObject(
                        ceDictionary.GetAt(LibraryRootRecordName),
                        OpenMode.ForWrite,
                        false);
                }
                else
                {
                    record = new Xrecord();
                    ceDictionary.SetAt(LibraryRootRecordName, record);
                    transaction.AddNewlyCreatedDBObject(record, true);
                }

                record.Data = new ResultBuffer(
                    new TypedValue((int)DxfCode.Text, root),
                    new TypedValue((int)DxfCode.Text, DateTime.UtcNow.ToString("O")));
                transaction.Commit();
            }
        }

        private static void StoreDetailLink(
            BlockReference reference,
            Transaction transaction,
            DetailAsset asset,
            double scale,
            double rotationDegrees)
        {
            if (reference.ExtensionDictionary.IsNull)
                reference.CreateExtensionDictionary();

            DBDictionary dictionary = (DBDictionary)transaction.GetObject(
                reference.ExtensionDictionary,
                OpenMode.ForWrite,
                false);

            Xrecord record;
            if (dictionary.Contains(InsertLinkRecordName))
            {
                record = (Xrecord)transaction.GetObject(
                    dictionary.GetAt(InsertLinkRecordName),
                    OpenMode.ForWrite,
                    false);
            }
            else
            {
                record = new Xrecord();
                dictionary.SetAt(InsertLinkRecordName, record);
                transaction.AddNewlyCreatedDBObject(record, true);
            }

            record.Data = new ResultBuffer(
                new TypedValue((int)DxfCode.Text, "1"),
                new TypedValue((int)DxfCode.Text, asset.FullPath),
                new TypedValue((int)DxfCode.Text, asset.Category),
                new TypedValue((int)DxfCode.Real, scale),
                new TypedValue((int)DxfCode.Real, rotationDegrees),
                new TypedValue((int)DxfCode.Text, DateTime.UtcNow.ToString("O")));
        }

        private sealed class DetailAsset
        {
            public DetailAsset(
                string fullPath,
                string relativePath,
                string extension,
                string category)
            {
                FullPath = fullPath;
                RelativePath = relativePath;
                Extension = extension;
                Category = category;
            }

            public string FullPath { get; }
            public string RelativePath { get; }
            public string Extension { get; }
            public string Category { get; }
        }
    }
}

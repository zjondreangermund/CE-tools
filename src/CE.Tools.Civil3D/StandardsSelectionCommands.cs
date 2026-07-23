using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Microsoft.Win32;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.StandardsSelectionCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Records project standards and the selected standards source file inside the
    /// current DWG. The selected file path, type, modified date and checksum make
    /// the project selection traceable and portable with the drawing metadata.
    /// </summary>
    public sealed class StandardsSelectionCommands
    {
        private const string RootDictionaryName = "CE_TOOLS";
        private const string StandardsRecordName = "STANDARDS_SELECTION";
        private const string ProjectRecordName = "PROJECT_METADATA";
        private const string SchemaVersion = "2";

        private static readonly string[] FieldOrder =
        {
            "Region / Framework",
            "Design Discipline",
            "Primary Standard",
            "Additional Standards",
            "Edition / Revision",
            "Approval Authority",
            "Standards File",
            "File Type",
            "File Modified",
            "File SHA-256",
            "Notes",
            "Selection Date"
        };

        [CommandMethod("CE_TOOLS", "CE_STANDARDS", CommandFlags.Modal | CommandFlags.Redraw)]
        public void StandardsMenu()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null)
            {
                return;
            }

            var options = new PromptKeywordOptions(
                "\nCE Standards Selection [Select/Info/Clear] <Select>: ")
            {
                AllowNone = true
            };
            options.Keywords.Add("Select");
            options.Keywords.Add("Info");
            options.Keywords.Add("Clear");

            PromptResult result = document.Editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel)
            {
                return;
            }

            string mode = result.Status == PromptStatus.None
                ? "Select"
                : result.StringResult;

            if (string.Equals(mode, "Info", StringComparison.OrdinalIgnoreCase))
            {
                ReportStandards(document);
            }
            else if (string.Equals(mode, "Clear", StringComparison.OrdinalIgnoreCase))
            {
                ClearStandards(document);
            }
            else
            {
                SelectStandards(document);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_STANDARDSELECT", CommandFlags.Modal | CommandFlags.Redraw)]
        public void StandardSelect()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document != null)
            {
                SelectStandards(document);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_STANDARDINFO", CommandFlags.Modal | CommandFlags.Redraw)]
        public void StandardInfo()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document != null)
            {
                ReportStandards(document);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_STANDARDCLEAR", CommandFlags.Modal | CommandFlags.Redraw)]
        public void StandardClear()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document != null)
            {
                ClearStandards(document);
            }
        }

        private static void SelectStandards(Document document)
        {
            Editor editor = document.Editor;
            StandardsMetadata existing = ReadStandards(document.Database);

            string standardsFile = BrowseForStandardsFile(existing.Get("Standards File"));
            if (standardsFile == null)
            {
                editor.WriteMessage(
                    "\nCE_STANDARDSELECT cancelled. No standards source file was selected.");
                return;
            }

            string region = PromptForRegion(editor, existing.Get("Region / Framework"));
            if (region == null)
            {
                return;
            }

            var proposed = new StandardsMetadata();
            proposed.Set("Region / Framework", region);

            string discipline = PromptForText(
                editor,
                "Design Discipline",
                FirstNonBlank(existing.Get("Design Discipline"), "General Civil"));
            if (discipline == null)
            {
                return;
            }
            proposed.Set("Design Discipline", discipline);

            string fileStandardName = Path.GetFileNameWithoutExtension(standardsFile);
            string primaryDefault = FirstNonBlank(
                existing.Get("Primary Standard"),
                FirstNonBlank(fileStandardName, SuggestedPrimaryStandard(region)));
            string primary = PromptForText(editor, "Primary Standard", primaryDefault);
            if (primary == null)
            {
                return;
            }
            proposed.Set("Primary Standard", primary);

            string additional = PromptForText(
                editor,
                "Additional Standards (enter - to clear)",
                existing.Get("Additional Standards"));
            if (additional == null)
            {
                return;
            }
            proposed.Set("Additional Standards", ClearMarker(additional));

            string revision = PromptForText(
                editor,
                "Edition / Revision (enter - to clear)",
                existing.Get("Edition / Revision"));
            if (revision == null)
            {
                return;
            }
            proposed.Set("Edition / Revision", ClearMarker(revision));

            string authority = PromptForText(
                editor,
                "Approval Authority (enter - to clear)",
                existing.Get("Approval Authority"));
            if (authority == null)
            {
                return;
            }
            proposed.Set("Approval Authority", ClearMarker(authority));

            string notes = PromptForText(
                editor,
                "Notes (enter - to clear)",
                existing.Get("Notes"));
            if (notes == null)
            {
                return;
            }

            FileInfo fileInfo;
            try
            {
                fileInfo = new FileInfo(standardsFile);
                proposed.Set("Standards File", fileInfo.FullName);
                proposed.Set("File Type", fileInfo.Extension.TrimStart('.').ToUpperInvariant());
                proposed.Set(
                    "File Modified",
                    fileInfo.LastWriteTimeUtc.ToString("yyyy-MM-dd HH:mm:ss 'UTC'"));
                proposed.Set("File SHA-256", ComputeSha256(fileInfo.FullName));
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_STANDARDSELECT cancelled. The selected file could not be inspected. {0}",
                    exception.Message);
                return;
            }

            proposed.Set("Notes", ClearMarker(notes));
            proposed.Set("Selection Date", DateTime.UtcNow.ToString("yyyy-MM-dd"));

            string reviewNote =
                "Review the selected standards source and project information before saving. " +
                "CE Tools records the source file and its checksum in the DWG; automatic Civil 3D style import is handled separately.";
            if (!PopupTablePresenter.ShowReview(
                "CE Tools - Standards Selection",
                reviewNote,
                BuildRows(proposed),
                "Save"))
            {
                editor.WriteMessage(
                    "\nCE_STANDARDSELECT cancelled. Existing standards metadata was not changed.");
                return;
            }

            try
            {
                bool projectMetadataUpdated;
                WriteStandards(document.Database, proposed, out projectMetadataUpdated);
                editor.WriteMessage(
                    "\nCE_STANDARDSELECT complete. Standards metadata and source-file details saved inside this DWG.{0}",
                    projectMetadataUpdated
                        ? " The CE Project Standards field was synchronised."
                        : string.Empty);

                PopupTablePresenter.ShowReportAndOfferTable(
                    document,
                    "CE Tools - Standards Selection",
                    "The standards selection is stored inside the current drawing. Choose Place Table to insert a project standards register.",
                    BuildRows(proposed),
                    "CE Tools Standards Selection");
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_STANDARDSELECT cancelled. Existing metadata was retained. {0}",
                    exception.Message);
            }
        }

        private static void ReportStandards(Document document)
        {
            StandardsMetadata metadata = ReadStandards(document.Database);
            if (!metadata.Exists)
            {
                document.Editor.WriteMessage(
                    "\nCE_STANDARDINFO: no CE Tools standards selection is stored in this drawing.");
                return;
            }

            document.Editor.WriteMessage("\nCE Tools Standards Selection");
            WriteStandards(document.Editor, metadata);
            document.Editor.WriteMessage(
                "\n  Status: recorded project selection and source-file reference; compliance has not been automatically verified.");

            PopupTablePresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - Standards Selection",
                "This register records the selected standards and the exact source-file checksum stored in the current DWG.",
                BuildRows(metadata),
                "CE Tools Standards Selection");
        }

        private static void ClearStandards(Document document)
        {
            Editor editor = document.Editor;
            StandardsMetadata existing = ReadStandards(document.Database);
            if (!existing.Exists)
            {
                editor.WriteMessage(
                    "\nCE_STANDARDCLEAR: no CE Tools standards selection is stored in this drawing.");
                return;
            }

            if (!PopupTablePresenter.ShowReview(
                "CE Tools - Clear Standards Selection",
                "Remove this standards selection and its source-file reference from the current drawing?",
                BuildRows(existing),
                "Clear"))
            {
                editor.WriteMessage("\nCE_STANDARDCLEAR cancelled. Metadata was not changed.");
                return;
            }

            try
            {
                bool projectMetadataUpdated;
                RemoveStandards(document.Database, out projectMetadataUpdated);
                editor.WriteMessage(
                    "\nCE_STANDARDCLEAR complete. Standards metadata removed.{0}",
                    projectMetadataUpdated
                        ? " The CE Project Standards field was also cleared."
                        : string.Empty);
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_STANDARDCLEAR cancelled. Metadata was not removed. {0}",
                    exception.Message);
            }
        }

        private static string BrowseForStandardsFile(string existingPath)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Select the project standards source file",
                CheckFileExists = true,
                CheckPathExists = true,
                Multiselect = false,
                Filter =
                    "Civil 3D / AutoCAD standards (*.dwt;*.dwg;*.dws)|*.dwt;*.dwg;*.dws|" +
                    "Standards documents (*.pdf;*.docx;*.xlsx;*.xls;*.csv;*.xml)|*.pdf;*.docx;*.xlsx;*.xls;*.csv;*.xml|" +
                    "All files (*.*)|*.*"
            };

            try
            {
                if (!string.IsNullOrWhiteSpace(existingPath))
                {
                    string fullPath = Path.GetFullPath(existingPath);
                    dialog.InitialDirectory = Path.GetDirectoryName(fullPath);
                    dialog.FileName = Path.GetFileName(fullPath);
                }
            }
            catch
            {
                // An invalid previous path must not prevent a new selection.
            }

            bool? result = dialog.ShowDialog();
            return result == true ? dialog.FileName : null;
        }

        private static string ComputeSha256(string path)
        {
            using (FileStream stream = File.OpenRead(path))
            using (SHA256 sha256 = SHA256.Create())
            {
                byte[] hash = sha256.ComputeHash(stream);
                return BitConverter.ToString(hash).Replace("-", string.Empty);
            }
        }

        private static string PromptForRegion(Editor editor, string existing)
        {
            string defaultRegion = NormalizeRegion(existing);
            if (string.IsNullOrWhiteSpace(defaultRegion))
            {
                defaultRegion = "Namibia";
            }

            var options = new PromptKeywordOptions(
                string.Format(
                    "\nRegion / framework [Namibia/SouthAfrica/International/Custom] <{0}>: ",
                    defaultRegion))
            {
                AllowNone = true
            };
            options.Keywords.Add("Namibia");
            options.Keywords.Add("SouthAfrica");
            options.Keywords.Add("International");
            options.Keywords.Add("Custom");

            PromptResult result = editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel)
            {
                return null;
            }

            return result.Status == PromptStatus.None
                ? defaultRegion
                : result.StringResult;
        }

        private static string PromptForText(Editor editor, string field, string defaultValue)
        {
            string prompt = string.IsNullOrWhiteSpace(defaultValue)
                ? string.Format("\n{0}: ", field)
                : string.Format("\n{0} <{1}>: ", field, defaultValue);

            var options = new PromptStringOptions(prompt)
            {
                AllowSpaces = true,
                UseDefaultValue = !string.IsNullOrWhiteSpace(defaultValue),
                DefaultValue = defaultValue ?? string.Empty
            };

            PromptResult result = editor.GetString(options);
            if (result.Status != PromptStatus.OK)
            {
                return null;
            }

            return (result.StringResult ?? string.Empty).Trim();
        }

        private static StandardsMetadata ReadStandards(Database database)
        {
            var metadata = new StandardsMetadata();

            try
            {
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    DBDictionary namedObjects = transaction.GetObject(
                        database.NamedObjectsDictionaryId,
                        OpenMode.ForRead,
                        false) as DBDictionary;
                    if (namedObjects == null || !namedObjects.Contains(RootDictionaryName))
                    {
                        return metadata;
                    }

                    DBDictionary root = transaction.GetObject(
                        namedObjects.GetAt(RootDictionaryName),
                        OpenMode.ForRead,
                        false) as DBDictionary;
                    if (root == null || !root.Contains(StandardsRecordName))
                    {
                        return metadata;
                    }

                    Xrecord record = transaction.GetObject(
                        root.GetAt(StandardsRecordName),
                        OpenMode.ForRead,
                        false) as Xrecord;
                    if (record == null || record.Data == null)
                    {
                        return metadata;
                    }

                    ReadPairs(record.Data, metadata.Set);
                    metadata.Exists = true;
                }
            }
            catch
            {
                // A malformed or inaccessible record is treated as absent.
            }

            return metadata;
        }

        private static void WriteStandards(
            Database database,
            StandardsMetadata metadata,
            out bool projectMetadataUpdated)
        {
            projectMetadataUpdated = false;

            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                DBDictionary root = OpenOrCreateRootDictionary(database, transaction);
                Xrecord standardsRecord = OpenOrCreateRecord(
                    root,
                    StandardsRecordName,
                    transaction);
                standardsRecord.Data = BuildResultBuffer(metadata);

                projectMetadataUpdated = TryUpdateProjectStandards(
                    root,
                    BuildProjectStandardsSummary(metadata),
                    transaction);

                transaction.Commit();
            }
        }

        private static void RemoveStandards(Database database, out bool projectMetadataUpdated)
        {
            projectMetadataUpdated = false;

            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                DBDictionary namedObjects = transaction.GetObject(
                    database.NamedObjectsDictionaryId,
                    OpenMode.ForRead,
                    false) as DBDictionary;
                if (namedObjects == null || !namedObjects.Contains(RootDictionaryName))
                {
                    return;
                }

                DBDictionary root = transaction.GetObject(
                    namedObjects.GetAt(RootDictionaryName),
                    OpenMode.ForWrite,
                    false) as DBDictionary;
                if (root == null)
                {
                    return;
                }

                if (root.Contains(StandardsRecordName))
                {
                    ObjectId recordId = root.GetAt(StandardsRecordName);
                    DBObject record = transaction.GetObject(recordId, OpenMode.ForWrite, false);
                    root.Remove(StandardsRecordName);
                    record.Erase();
                }

                projectMetadataUpdated = TryUpdateProjectStandards(
                    root,
                    string.Empty,
                    transaction);
                transaction.Commit();
            }
        }

        private static DBDictionary OpenOrCreateRootDictionary(
            Database database,
            Transaction transaction)
        {
            DBDictionary namedObjects = (DBDictionary)transaction.GetObject(
                database.NamedObjectsDictionaryId,
                OpenMode.ForWrite,
                false);

            if (namedObjects.Contains(RootDictionaryName))
            {
                return (DBDictionary)transaction.GetObject(
                    namedObjects.GetAt(RootDictionaryName),
                    OpenMode.ForWrite,
                    false);
            }

            var root = new DBDictionary();
            namedObjects.SetAt(RootDictionaryName, root);
            transaction.AddNewlyCreatedDBObject(root, true);
            return root;
        }

        private static Xrecord OpenOrCreateRecord(
            DBDictionary root,
            string recordName,
            Transaction transaction)
        {
            if (root.Contains(recordName))
            {
                return (Xrecord)transaction.GetObject(
                    root.GetAt(recordName),
                    OpenMode.ForWrite,
                    false);
            }

            var record = new Xrecord();
            root.SetAt(recordName, record);
            transaction.AddNewlyCreatedDBObject(record, true);
            return record;
        }

        private static ResultBuffer BuildResultBuffer(StandardsMetadata metadata)
        {
            var values = new List<TypedValue>
            {
                new TypedValue((int)DxfCode.Text, "Schema"),
                new TypedValue((int)DxfCode.Text, SchemaVersion)
            };

            for (int index = 0; index < FieldOrder.Length; index++)
            {
                string field = FieldOrder[index];
                values.Add(new TypedValue((int)DxfCode.Text, field));
                values.Add(new TypedValue((int)DxfCode.Text, metadata.Get(field)));
            }

            return new ResultBuffer(values.ToArray());
        }

        private static bool TryUpdateProjectStandards(
            DBDictionary root,
            string standardsSummary,
            Transaction transaction)
        {
            if (!root.Contains(ProjectRecordName))
            {
                return false;
            }

            Xrecord projectRecord = transaction.GetObject(
                root.GetAt(ProjectRecordName),
                OpenMode.ForWrite,
                false) as Xrecord;
            if (projectRecord == null || projectRecord.Data == null)
            {
                return false;
            }

            var pairs = new List<KeyValuePair<string, string>>();
            ReadPairs(
                projectRecord.Data,
                delegate(string key, string value)
                {
                    pairs.Add(new KeyValuePair<string, string>(key, value));
                });

            bool found = false;
            for (int index = 0; index < pairs.Count; index++)
            {
                if (string.Equals(pairs[index].Key, "Standards", StringComparison.OrdinalIgnoreCase))
                {
                    pairs[index] = new KeyValuePair<string, string>("Standards", standardsSummary);
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                pairs.Add(new KeyValuePair<string, string>("Standards", standardsSummary));
            }

            var values = new List<TypedValue>();
            foreach (KeyValuePair<string, string> pair in pairs)
            {
                values.Add(new TypedValue((int)DxfCode.Text, pair.Key));
                values.Add(new TypedValue((int)DxfCode.Text, pair.Value ?? string.Empty));
            }

            projectRecord.Data = new ResultBuffer(values.ToArray());
            return true;
        }

        private static void ReadPairs(ResultBuffer buffer, Action<string, string> consumer)
        {
            string pendingKey = null;
            foreach (TypedValue value in buffer)
            {
                string text = value.Value as string;
                if (text == null)
                {
                    continue;
                }

                if (pendingKey == null)
                {
                    pendingKey = text;
                }
                else
                {
                    consumer(pendingKey, text);
                    pendingKey = null;
                }
            }
        }

        private static IList<KeyValuePair<string, string>> BuildRows(
            StandardsMetadata metadata)
        {
            return PopupTablePresenter.BuildRows(FieldOrder, metadata.Get);
        }

        private static void WriteStandards(Editor editor, StandardsMetadata metadata)
        {
            for (int index = 0; index < FieldOrder.Length; index++)
            {
                string field = FieldOrder[index];
                string value = metadata.Get(field);
                editor.WriteMessage(
                    "\n  {0}: {1}",
                    field,
                    string.IsNullOrWhiteSpace(value) ? "<Not set>" : value);
            }
        }

        private static string BuildProjectStandardsSummary(StandardsMetadata metadata)
        {
            var parts = new List<string>();
            AddIfPresent(parts, metadata.Get("Primary Standard"));
            AddIfPresent(parts, metadata.Get("Additional Standards"));
            AddIfPresent(parts, metadata.Get("Edition / Revision"));

            string standardsFile = metadata.Get("Standards File");
            if (!string.IsNullOrWhiteSpace(standardsFile))
            {
                AddIfPresent(parts, Path.GetFileName(standardsFile));
            }

            return string.Join(" | ", parts.ToArray());
        }

        private static void AddIfPresent(List<string> values, string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                values.Add(value.Trim());
            }
        }

        private static string SuggestedPrimaryStandard(string region)
        {
            if (string.Equals(region, "SouthAfrica", StringComparison.OrdinalIgnoreCase))
            {
                return "SANRAL; TRH; TMH; SANS; Red Book";
            }

            if (string.Equals(region, "International", StringComparison.OrdinalIgnoreCase))
            {
                return "Project-specific international standards";
            }

            if (string.Equals(region, "Custom", StringComparison.OrdinalIgnoreCase))
            {
                return "Project-specific standards";
            }

            return "Namibian authority requirements; SANS; project specifications";
        }

        private static string NormalizeRegion(string value)
        {
            if (string.Equals(value, "Namibia", StringComparison.OrdinalIgnoreCase))
            {
                return "Namibia";
            }
            if (string.Equals(value, "SouthAfrica", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "South Africa", StringComparison.OrdinalIgnoreCase))
            {
                return "SouthAfrica";
            }
            if (string.Equals(value, "International", StringComparison.OrdinalIgnoreCase))
            {
                return "International";
            }
            if (string.Equals(value, "Custom", StringComparison.OrdinalIgnoreCase))
            {
                return "Custom";
            }
            return null;
        }

        private static string FirstNonBlank(string first, string second)
        {
            return string.IsNullOrWhiteSpace(first) ? second : first;
        }

        private static string ClearMarker(string value)
        {
            return string.Equals(value, "-", StringComparison.Ordinal)
                ? string.Empty
                : value;
        }

        private sealed class StandardsMetadata
        {
            private readonly Dictionary<string, string> _values =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            public bool Exists { get; set; }

            public string Get(string field)
            {
                string value;
                return _values.TryGetValue(field, out value) ? value : string.Empty;
            }

            public void Set(string field, string value)
            {
                if (!string.Equals(field, "Schema", StringComparison.OrdinalIgnoreCase))
                {
                    _values[field] = value ?? string.Empty;
                }
            }
        }
    }
}

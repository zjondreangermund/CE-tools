using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.StandardsSelectionCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Records the project standards selected by the user inside the current DWG.
    /// This first version stores project intent only; it does not claim compliance
    /// or automatically change Civil 3D styles and design criteria.
    /// </summary>
    public sealed class StandardsSelectionCommands
    {
        private const string RootDictionaryName = "CE_TOOLS";
        private const string StandardsRecordName = "STANDARDS_SELECTION";
        private const string ProjectRecordName = "PROJECT_METADATA";
        private const string SchemaVersion = "1";

        private static readonly string[] FieldOrder =
        {
            "Region / Framework",
            "Design Discipline",
            "Primary Standard",
            "Additional Standards",
            "Edition / Revision",
            "Approval Authority",
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

            string primaryDefault = FirstNonBlank(
                existing.Get("Primary Standard"),
                SuggestedPrimaryStandard(region));
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
            proposed.Set("Notes", ClearMarker(notes));
            proposed.Set("Selection Date", DateTime.UtcNow.ToString("yyyy-MM-dd"));

            editor.WriteMessage("\nCE Standards Selection preview");
            WriteStandards(editor, proposed);
            editor.WriteMessage(
                "\n  IMPORTANT: This records the selected standards only. " +
                "The project contract, authority requirements and current editions must still be verified.");

            if (!Confirm(editor, "Save this standards selection"))
            {
                editor.WriteMessage("\nCE_STANDARDSELECT cancelled. Existing standards metadata was not changed.");
                return;
            }

            try
            {
                bool projectMetadataUpdated;
                WriteStandards(document.Database, proposed, out projectMetadataUpdated);
                editor.WriteMessage(
                    "\nCE_STANDARDSELECT complete. Standards metadata saved inside this DWG.{0}",
                    projectMetadataUpdated
                        ? " The CE Project Standards field was synchronised."
                        : string.Empty);
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
                "\n  Status: recorded project selection only; compliance has not been automatically verified.");
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

            WriteStandards(editor, existing);
            if (!Confirm(editor, "Remove this CE Tools standards selection"))
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
                   string.Equals(result.StringResult, "Yes", StringComparison.OrdinalIgnoreCase);
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

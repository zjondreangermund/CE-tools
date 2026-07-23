using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.ProjectSetupCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Stores portable CE Tools project metadata inside the current DWG by using
    /// a Named Objects Dictionary entry and XRecord.
    /// </summary>
    public sealed class ProjectSetupCommands
    {
        private const string RootDictionaryName = "CE_TOOLS";
        private const string ProjectRecordName = "PROJECT_METADATA";
        private const string SchemaVersion = "1";

        private static readonly string[] FieldOrder =
        {
            "Project Name",
            "Client",
            "Country",
            "Town",
            "Coordinate System",
            "Standards",
            "Drawing Template",
            "Units"
        };

        [CommandMethod(
            "CE_TOOLS",
            "CE_PROJECT",
            CommandFlags.Modal | CommandFlags.Redraw)]
        public void ProjectMenu()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null)
            {
                return;
            }

            var options = new PromptKeywordOptions(
                "\nCE Project [Setup/Info/Clear] <Setup>: ")
            {
                AllowNone = true
            };
            options.Keywords.Add("Setup");
            options.Keywords.Add("Info");
            options.Keywords.Add("Clear");

            PromptResult result = document.Editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel)
            {
                return;
            }

            string mode = result.Status == PromptStatus.None
                ? "Setup"
                : result.StringResult;

            if (string.Equals(mode, "Info", StringComparison.OrdinalIgnoreCase))
            {
                ReportProjectInfo(document);
            }
            else if (string.Equals(mode, "Clear", StringComparison.OrdinalIgnoreCase))
            {
                ClearProjectInfo(document);
            }
            else
            {
                SetupProject(document);
            }
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_PROJECTSETUP",
            CommandFlags.Modal | CommandFlags.Redraw)]
        public void ProjectSetup()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document != null)
            {
                SetupProject(document);
            }
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_PROJECTINFO",
            CommandFlags.Modal | CommandFlags.Redraw)]
        public void ProjectInfo()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document != null)
            {
                ReportProjectInfo(document);
            }
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_PROJECTCLEAR",
            CommandFlags.Modal | CommandFlags.Redraw)]
        public void ProjectClear()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document != null)
            {
                ClearProjectInfo(document);
            }
        }

        private static void SetupProject(Document document)
        {
            Editor editor = document.Editor;
            ProjectMetadata existing = ReadProjectMetadata(document.Database);
            var proposed = new ProjectMetadata();

            for (int index = 0; index < FieldOrder.Length; index++)
            {
                string field = FieldOrder[index];
                string currentValue = existing.Get(field);
                if (string.IsNullOrWhiteSpace(currentValue) &&
                    string.Equals(field, "Units", StringComparison.OrdinalIgnoreCase))
                {
                    currentValue = "Metric";
                }

                PromptResult result = PromptForValue(editor, field, currentValue);
                if (result.Status != PromptStatus.OK)
                {
                    editor.WriteMessage(
                        "\nCE_PROJECTSETUP cancelled. Existing project metadata was not changed.");
                    return;
                }

                proposed.Set(field, result.StringResult == null
                    ? string.Empty
                    : result.StringResult.Trim());
            }

            try
            {
                WriteProjectMetadata(document.Database, proposed);
                editor.WriteMessage("\nCE_PROJECTSETUP complete. Project metadata saved inside this DWG.");
                WriteMetadata(editor, proposed);
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_PROJECTSETUP cancelled. Existing metadata was not replaced. {0}",
                    exception.Message);
            }
        }

        private static void ReportProjectInfo(Document document)
        {
            ProjectMetadata metadata = ReadProjectMetadata(document.Database);
            if (!metadata.Exists)
            {
                document.Editor.WriteMessage(
                    "\nCE_PROJECTINFO: no CE Tools project metadata is stored in this drawing.");
                return;
            }

            document.Editor.WriteMessage("\nCE Tools Project Information");
            WriteMetadata(document.Editor, metadata);
        }

        private static void ClearProjectInfo(Document document)
        {
            Editor editor = document.Editor;
            ProjectMetadata metadata = ReadProjectMetadata(document.Database);
            if (!metadata.Exists)
            {
                editor.WriteMessage(
                    "\nCE_PROJECTCLEAR: no CE Tools project metadata is stored in this drawing.");
                return;
            }

            WriteMetadata(editor, metadata);

            var options = new PromptKeywordOptions(
                "\nRemove this CE Tools project metadata? [Yes/No] <No>: ")
            {
                AllowNone = true
            };
            options.Keywords.Add("Yes");
            options.Keywords.Add("No");

            PromptResult confirmation = editor.GetKeywords(options);
            if (confirmation.Status != PromptStatus.OK ||
                !string.Equals(
                    confirmation.StringResult,
                    "Yes",
                    StringComparison.OrdinalIgnoreCase))
            {
                editor.WriteMessage("\nCE_PROJECTCLEAR cancelled. Metadata was not changed.");
                return;
            }

            try
            {
                using (Transaction transaction =
                    document.Database.TransactionManager.StartTransaction())
                {
                    DBDictionary namedObjects = (DBDictionary)transaction.GetObject(
                        document.Database.NamedObjectsDictionaryId,
                        OpenMode.ForRead,
                        false);

                    if (!namedObjects.Contains(RootDictionaryName))
                    {
                        editor.WriteMessage("\nCE_PROJECTCLEAR: metadata record was already absent.");
                        return;
                    }

                    DBDictionary root = transaction.GetObject(
                        namedObjects.GetAt(RootDictionaryName),
                        OpenMode.ForRead,
                        false) as DBDictionary;
                    if (root == null || !root.Contains(ProjectRecordName))
                    {
                        editor.WriteMessage("\nCE_PROJECTCLEAR: metadata record was already absent.");
                        return;
                    }

                    Xrecord record = transaction.GetObject(
                        root.GetAt(ProjectRecordName),
                        OpenMode.ForWrite,
                        false) as Xrecord;
                    if (record != null)
                    {
                        record.Erase();
                    }

                    transaction.Commit();
                }

                editor.WriteMessage("\nCE_PROJECTCLEAR complete. CE project metadata removed.");
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_PROJECTCLEAR cancelled. Metadata was not removed. {0}",
                    exception.Message);
            }
        }

        private static PromptResult PromptForValue(
            Editor editor,
            string fieldName,
            string defaultValue)
        {
            string prompt = string.IsNullOrWhiteSpace(defaultValue)
                ? string.Format("\n{0}: ", fieldName)
                : string.Format("\n{0} <{1}>: ", fieldName, defaultValue);

            var options = new PromptStringOptions(prompt)
            {
                AllowSpaces = true,
                UseDefaultValue = !string.IsNullOrWhiteSpace(defaultValue),
                DefaultValue = defaultValue ?? string.Empty
            };

            return editor.GetString(options);
        }

        private static ProjectMetadata ReadProjectMetadata(Database database)
        {
            var metadata = new ProjectMetadata();

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
                    if (root == null || !root.Contains(ProjectRecordName))
                    {
                        return metadata;
                    }

                    Xrecord record = transaction.GetObject(
                        root.GetAt(ProjectRecordName),
                        OpenMode.ForRead,
                        false) as Xrecord;
                    if (record == null || record.Data == null)
                    {
                        return metadata;
                    }

                    string pendingKey = null;
                    foreach (TypedValue value in record.Data)
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
                            if (!string.Equals(pendingKey, "Schema", StringComparison.OrdinalIgnoreCase))
                            {
                                metadata.Set(pendingKey, text);
                            }
                            pendingKey = null;
                        }
                    }

                    metadata.Exists = true;
                }
            }
            catch
            {
                // A malformed or inaccessible metadata record is treated as absent.
            }

            return metadata;
        }

        private static void WriteProjectMetadata(
            Database database,
            ProjectMetadata metadata)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                DBDictionary namedObjects = (DBDictionary)transaction.GetObject(
                    database.NamedObjectsDictionaryId,
                    OpenMode.ForWrite,
                    false);

                DBDictionary root;
                if (namedObjects.Contains(RootDictionaryName))
                {
                    root = (DBDictionary)transaction.GetObject(
                        namedObjects.GetAt(RootDictionaryName),
                        OpenMode.ForWrite,
                        false);
                }
                else
                {
                    root = new DBDictionary();
                    namedObjects.SetAt(RootDictionaryName, root);
                    transaction.AddNewlyCreatedDBObject(root, true);
                }

                Xrecord record;
                if (root.Contains(ProjectRecordName))
                {
                    record = (Xrecord)transaction.GetObject(
                        root.GetAt(ProjectRecordName),
                        OpenMode.ForWrite,
                        false);
                }
                else
                {
                    record = new Xrecord();
                    root.SetAt(ProjectRecordName, record);
                    transaction.AddNewlyCreatedDBObject(record, true);
                }

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

                record.Data = new ResultBuffer(values.ToArray());
                transaction.Commit();
            }
        }

        private static void WriteMetadata(Editor editor, ProjectMetadata metadata)
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

        private sealed class ProjectMetadata
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
                _values[field] = value ?? string.Empty;
            }
        }
    }
}

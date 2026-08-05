using System;
using System.Collections.Generic;
using System.Globalization;
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
    /// Named Objects Dictionary XRecords. Project information can be reviewed in
    /// a pop-up and placed as an AutoCAD table. Clear creates a recoverable backup.
    /// </summary>
    public sealed class ProjectSetupCommands
    {
        private const string RootDictionaryName = "CE_TOOLS";
        private const string ProjectRecordName = "PROJECT_METADATA";
        private const string ProjectBackupRecordName = "PROJECT_METADATA_CLEAR_BACKUP";
        private const string SchemaVersion = "2";

        internal static readonly string[] FieldOrder =
        {
            "Project Name",
            "Project Number",
            "Client",
            "Company",
            "Country",
            "Town",
            "Coordinate System",
            "Standards",
            "Drawing Template",
            "Units",
            "Project Stage",
            "Revision",
            "Issue Date",
            "Drawing Number Prefix",
            "Designed By",
            "Drawn By",
            "Checked By",
            "Approved By"
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

            DisciplineWorkflowDialogs.SelectAndRun(
                document,
                "CE Tools - Project Setup",
                "Create, inspect and safely maintain drawing-embedded CE Tools project metadata.",
                new List<DisciplineWorkflowAction>
                {
                    new DisciplineWorkflowAction("Set up project", "CE_PROJECTSETUP", "Enter project, client, location, standards, template and units.", "01 Project"),
                    new DisciplineWorkflowAction("Project information", "CE_PROJECTINFO", "Review stored project metadata and optionally place a table.", "01 Project"),
                    new DisciplineWorkflowAction("Clear project metadata", "CE_PROJECTCLEAR", "Back up and clear the current project metadata.", "02 Recovery"),
                    new DisciplineWorkflowAction("Restore project metadata", "CE_PROJECTRESTORE", "Restore the latest CE Tools metadata backup.", "02 Recovery")
                });
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

        [CommandMethod(
            "CE_TOOLS",
            "CE_PROJECTRESTORE",
            CommandFlags.Modal | CommandFlags.Redraw)]
        public void ProjectRestore()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document != null)
            {
                RestoreProjectInfo(document);
            }
        }

        private static void SetupProject(Document document)
        {
            Editor editor = document.Editor;
            ProjectMetadata existing = ReadProjectMetadata(
                document.Database,
                ProjectRecordName);
            var initialValues = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            foreach (string field in FieldOrder)
            {
                string value = existing.Get(field);
                if (string.IsNullOrWhiteSpace(value) &&
                    string.Equals(field, "Units", StringComparison.OrdinalIgnoreCase))
                    value = "Metric";
                if (string.IsNullOrWhiteSpace(value) &&
                    string.Equals(field, "Issue Date", StringComparison.OrdinalIgnoreCase))
                    value = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                initialValues[field] = value ?? string.Empty;
            }

            var window = new ProjectSetupPopupWindow(
                FieldOrder,
                initialValues);
            AcApplication.ShowModalWindow(window);
            if (!window.Accepted)
            {
                editor.WriteMessage(
                    "\nCE_PROJECTSETUP cancelled. Existing project metadata was not changed.");
                return;
            }

            var proposed = new ProjectMetadata();
            foreach (string field in FieldOrder)
                proposed.Set(field, window.GetValue(field));

            if (!PopupTablePresenter.ShowReview(
                "CE Tools - Project Setup",
                "Review the project information before it is saved inside this drawing and linked to title blocks and drawing registers.",
                BuildRows(proposed),
                "Save"))
            {
                editor.WriteMessage(
                    "\nCE_PROJECTSETUP cancelled. Existing project metadata was not changed.");
                return;
            }

            try
            {
                WriteProjectMetadata(document.Database, proposed, clearBackup: true);
                RefreshInformationTables(document);
                editor.WriteMessage(
                    "\nCE_PROJECTSETUP complete. Project metadata saved inside this DWG.");
                PopupTablePresenter.ShowReportAndOfferTable(
                    document,
                    "CE Tools - Project Information",
                    "Project setup is complete and is now the shared source for drawing titles and registers.",
                    BuildRows(proposed),
                    "CE Tools Project Information");
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_PROJECTSETUP cancelled. Existing metadata was not replaced. {0}",
                    exception.Message);
            }
        }

        internal static IDictionary<string, string> ReadSharedProjectMetadata(
            Database database)
        {
            ProjectMetadata metadata = ReadProjectMetadata(
                database,
                ProjectRecordName);
            var result = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            foreach (string field in FieldOrder)
                result[field] = metadata.Get(field);
            return result;
        }

        internal static void MergeSharedProjectMetadata(
            Database database,
            IDictionary<string, string> values)
        {
            ProjectMetadata metadata = ReadProjectMetadata(
                database,
                ProjectRecordName);
            foreach (string field in FieldOrder)
            {
                string value;
                if (values != null && values.TryGetValue(field, out value))
                    metadata.Set(field, value ?? string.Empty);
            }
            metadata.Exists = true;
            WriteProjectMetadata(database, metadata, clearBackup: false);
        }

        private static void ReportProjectInfo(Document document)
        {
            ProjectMetadata metadata = ReadProjectMetadata(
                document.Database,
                ProjectRecordName);
            if (!metadata.Exists)
            {
                document.Editor.WriteMessage(
                    "\nCE_PROJECTINFO: no CE Tools project metadata is stored in this drawing.");
                return;
            }

            document.Editor.WriteMessage("\nCE Tools Project Information");
            WriteMetadata(document.Editor, metadata);
            PopupTablePresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - Project Information",
                "The information below is stored inside the current DWG. Choose Place Table to add a drawing table.",
                BuildRows(metadata),
                "CE Tools Project Information");
        }

        internal static int RefreshInformationTables(Document document)
        {
            if (document == null) return 0;
            ProjectMetadata metadata = ReadProjectMetadata(
                document.Database,
                ProjectRecordName);
            if (!metadata.Exists) return 0;
            return PopupTablePresenter.RefreshKeyValueTables(
                document.Database,
                "CE Tools Project Information",
                BuildRows(metadata));
        }

        private static void ClearProjectInfo(Document document)
        {
            Editor editor = document.Editor;
            ProjectMetadata metadata = ReadProjectMetadata(
                document.Database,
                ProjectRecordName);
            if (!metadata.Exists)
            {
                editor.WriteMessage(
                    "\nCE_PROJECTCLEAR: no CE Tools project metadata is stored in this drawing.");
                return;
            }

            WriteMetadata(editor, metadata);

            if (!PopupTablePresenter.ShowReview(
                "CE Tools - Clear Project Information",
                "The current project information will be removed, but CE Tools will keep one recoverable backup for CE_PROJECTRESTORE.",
                BuildRows(metadata),
                "Clear"))
            {
                editor.WriteMessage("\nCE_PROJECTCLEAR cancelled. Metadata was not changed.");
                return;
            }

            try
            {
                BackupAndRemoveProjectMetadata(document.Database);
                editor.WriteMessage(
                    "\nCE_PROJECTCLEAR complete. Project metadata removed. " +
                    "Run CE_PROJECTRESTORE to recover the cleared values.");
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_PROJECTCLEAR cancelled. Metadata was not removed. {0}",
                    exception.Message);
            }
        }

        private static void RestoreProjectInfo(Document document)
        {
            Editor editor = document.Editor;
            ProjectMetadata backup = ReadProjectMetadata(
                document.Database,
                ProjectBackupRecordName);
            if (!backup.Exists)
            {
                editor.WriteMessage(
                    "\nCE_PROJECTRESTORE: no cleared project-information backup is available.");
                return;
            }

            if (!PopupTablePresenter.ShowReview(
                "CE Tools - Restore Project Information",
                "Restore the project information that was saved immediately before the last CE_PROJECTCLEAR command.",
                BuildRows(backup),
                "Restore"))
            {
                editor.WriteMessage("\nCE_PROJECTRESTORE cancelled. Metadata was not changed.");
                return;
            }

            try
            {
                RestoreProjectMetadata(document.Database);
                editor.WriteMessage(
                    "\nCE_PROJECTRESTORE complete. Cleared project metadata restored.");
                PopupTablePresenter.ShowReportAndOfferTable(
                    document,
                    "CE Tools - Restored Project Information",
                    "The cleared project information has been restored. Choose Place Table to add it to the drawing.",
                    BuildRows(backup),
                    "CE Tools Project Information");
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_PROJECTRESTORE cancelled. The backup was retained. {0}",
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

        private static ProjectMetadata ReadProjectMetadata(
            Database database,
            string recordName)
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
                    if (root == null || !root.Contains(recordName))
                    {
                        return metadata;
                    }

                    Xrecord record = transaction.GetObject(
                        root.GetAt(recordName),
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
                // A malformed or inaccessible metadata record is treated as absent.
            }

            return metadata;
        }

        private static void WriteProjectMetadata(
            Database database,
            ProjectMetadata metadata,
            bool clearBackup)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                DBDictionary root = OpenOrCreateRootDictionary(database, transaction);
                Xrecord record = OpenOrCreateRecord(
                    root,
                    ProjectRecordName,
                    transaction);
                record.Data = BuildResultBuffer(metadata);

                if (clearBackup)
                {
                    RemoveRecordIfPresent(root, ProjectBackupRecordName, transaction);
                }

                transaction.Commit();
            }
        }

        private static void BackupAndRemoveProjectMetadata(Database database)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                DBDictionary namedObjects = (DBDictionary)transaction.GetObject(
                    database.NamedObjectsDictionaryId,
                    OpenMode.ForWrite,
                    false);
                if (!namedObjects.Contains(RootDictionaryName))
                {
                    return;
                }

                DBDictionary root = (DBDictionary)transaction.GetObject(
                    namedObjects.GetAt(RootDictionaryName),
                    OpenMode.ForWrite,
                    false);
                if (!root.Contains(ProjectRecordName))
                {
                    return;
                }

                Xrecord projectRecord = transaction.GetObject(
                    root.GetAt(ProjectRecordName),
                    OpenMode.ForWrite,
                    false) as Xrecord;
                if (projectRecord == null || projectRecord.Data == null)
                {
                    return;
                }

                Xrecord backupRecord = OpenOrCreateRecord(
                    root,
                    ProjectBackupRecordName,
                    transaction);
                backupRecord.Data = CloneResultBuffer(projectRecord.Data);

                root.Remove(ProjectRecordName);
                projectRecord.Erase();
                transaction.Commit();
            }
        }

        private static void RestoreProjectMetadata(Database database)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                DBDictionary namedObjects = (DBDictionary)transaction.GetObject(
                    database.NamedObjectsDictionaryId,
                    OpenMode.ForWrite,
                    false);
                if (!namedObjects.Contains(RootDictionaryName))
                {
                    throw new InvalidOperationException("The CE Tools metadata dictionary is unavailable.");
                }

                DBDictionary root = (DBDictionary)transaction.GetObject(
                    namedObjects.GetAt(RootDictionaryName),
                    OpenMode.ForWrite,
                    false);
                if (!root.Contains(ProjectBackupRecordName))
                {
                    throw new InvalidOperationException("No project-information backup is available.");
                }

                Xrecord backupRecord = transaction.GetObject(
                    root.GetAt(ProjectBackupRecordName),
                    OpenMode.ForWrite,
                    false) as Xrecord;
                if (backupRecord == null || backupRecord.Data == null)
                {
                    throw new InvalidOperationException("The project-information backup is empty.");
                }

                Xrecord projectRecord = OpenOrCreateRecord(
                    root,
                    ProjectRecordName,
                    transaction);
                projectRecord.Data = CloneResultBuffer(backupRecord.Data);

                root.Remove(ProjectBackupRecordName);
                backupRecord.Erase();
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

        private static void RemoveRecordIfPresent(
            DBDictionary root,
            string recordName,
            Transaction transaction)
        {
            if (!root.Contains(recordName))
            {
                return;
            }

            DBObject record = transaction.GetObject(
                root.GetAt(recordName),
                OpenMode.ForWrite,
                false);
            root.Remove(recordName);
            record.Erase();
        }

        private static ResultBuffer BuildResultBuffer(ProjectMetadata metadata)
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

        private static ResultBuffer CloneResultBuffer(ResultBuffer source)
        {
            var values = new List<TypedValue>();
            foreach (TypedValue value in source)
            {
                values.Add(new TypedValue(value.TypeCode, value.Value));
            }

            return new ResultBuffer(values.ToArray());
        }

        private static void ReadPairs(
            ResultBuffer buffer,
            Action<string, string> consumer)
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
                    if (!string.Equals(pendingKey, "Schema", StringComparison.OrdinalIgnoreCase))
                    {
                        consumer(pendingKey, text);
                    }
                    pendingKey = null;
                }
            }
        }

        private static IList<KeyValuePair<string, string>> BuildRows(
            ProjectMetadata metadata)
        {
            return PopupTablePresenter.BuildRows(FieldOrder, metadata.Get);
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

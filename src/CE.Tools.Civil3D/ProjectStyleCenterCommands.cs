using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices.Styles;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.ProjectStyleCenterCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Central project style selections for roads, stormwater, sewer, water and
    /// platforms. The style catalogue is read from the active Civil 3D drawing by
    /// reflection so the same source remains compatible with Civil 3D 2023/2024.
    /// Selections are stored in the DWG Named Objects Dictionary.
    /// </summary>
    public sealed class ProjectStyleCenterCommands
    {
        private const string RootDictionaryName = "CE_TOOLS";
        private const string RecordName = "PROJECT_STYLE_SELECTION";
        private const string SchemaVersion = "1";

        private static readonly string[] Disciplines =
        {
            "Roads",
            "Stormwater",
            "Sewer",
            "Water",
            "Platforms"
        };

        private static readonly string[] SelectionKeys =
        {
            "Alignment Style",
            "Alignment Label Set Style",
            "Profile Style",
            "Profile View Style",
            "Profile View Band Set Style",
            "Surface Style",
            "Point Style",
            "Point Label Style",
            "Corridor Style",
            "Code Set Style",
            "Assembly Style",
            "Pipe Style",
            "Structure Style",
            "Pressure Pipe Style",
            "Fitting Style",
            "Appurtenance Style"
        };

        [CommandMethod("CE_TOOLS", "CE_PROJECTSTYLES", CommandFlags.Modal | CommandFlags.Redraw)]
        public void ProjectStyles()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            while (true)
            {
                Dictionary<string, List<string>> catalogue =
                    ReadCivilStyleCatalogue(document);
                ProjectStyleSelection existing = ReadSelection(document.Database);
                var window = new ProjectStyleCenterWindow(
                    Disciplines,
                    SelectionKeys,
                    catalogue,
                    existing);
                AcApplication.ShowModalWindow(window);
                if (window.ImportRequested)
                {
                    ImportProjectStyleSource(document, false);
                    continue;
                }
                if (!window.Accepted)
                {
                    document.Editor.WriteMessage("\nCE_PROJECTSTYLES cancelled. Existing project style selections were not changed.");
                    return;
                }

                ProjectStyleSelection selection = window.BuildSelection();
                WriteSelection(document.Database, selection);
                document.Editor.WriteMessage(
                    "\nCE_PROJECTSTYLES complete. Discipline={0}; stored style selections={1}.",
                    selection.Discipline,
                    selection.Values.Count);
                ShowSelection(document, selection, "CE Tools - Project Style Centre");
                return;
            }
        }

        [CommandMethod("CE_TOOLS", "CE_PROJECTSTYLEIMPORT", CommandFlags.Modal | CommandFlags.Redraw)]
        public void ImportProjectStyles()
        {
            Document document = ActiveDocument();
            if (document != null) ImportProjectStyleSource(document, true);
        }

        [CommandMethod("CE_TOOLS", "CE_PROJECTSTYLEINFO", CommandFlags.Modal | CommandFlags.Redraw)]
        public void ProjectStyleInformation()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            ProjectStyleSelection selection = ReadSelection(document.Database);
            if (!selection.Exists)
            {
                document.Editor.WriteMessage(
                    "\nCE_PROJECTSTYLEINFO: no project style selections are stored. Run CE_PROJECTSTYLES first.");
                return;
            }

            ShowSelection(document, selection, "CE Tools - Project Style Information");
        }

        [CommandMethod("CE_TOOLS", "CE_PROJECTSTYLECLEAR", CommandFlags.Modal | CommandFlags.Redraw)]
        public void ClearProjectStyles()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            ProjectStyleSelection selection = ReadSelection(document.Database);
            if (!selection.Exists)
            {
                document.Editor.WriteMessage("\nCE_PROJECTSTYLECLEAR: no project style selections are stored.");
                return;
            }

            if (!PopupTablePresenter.ShowReview(
                "CE Tools - Clear Project Style Selections",
                "The stored style choices will be removed from this DWG. Civil 3D styles themselves will not be deleted.",
                BuildRows(selection),
                "Clear"))
            {
                document.Editor.WriteMessage("\nCE_PROJECTSTYLECLEAR cancelled.");
                return;
            }

            RemoveSelection(document.Database);
            document.Editor.WriteMessage("\nCE_PROJECTSTYLECLEAR complete. Stored choices were removed; drawing styles were unchanged.");
        }

        [CommandMethod("CE_TOOLS", "CE_UNDOSETTINGS", CommandFlags.Modal | CommandFlags.Redraw)]
        public void UndoSettings()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            document.Editor.WriteMessage(
                "\nCE_UNDOSETTINGS enables AutoCAD full undo recording. Native REDO remains available until a new modifying command is started.");
            document.SendStringToExecute("_.UNDO _Control _All ", true, false, true);
        }

        [CommandMethod("CE_TOOLS", "CE_UNDO", CommandFlags.Modal | CommandFlags.Redraw)]
        public void UndoOneStep()
        {
            Document document = ActiveDocument();
            if (document != null)
                document.SendStringToExecute("_.UNDO 1 ", true, false, true);
        }

        [CommandMethod("CE_TOOLS", "CE_REDO", CommandFlags.Modal | CommandFlags.Redraw)]
        public void RedoOneStep()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            PromptIntegerResult count = document.Editor.GetInteger(
                new PromptIntegerOptions("\nNumber of actions to redo <1>: ")
                {
                    AllowNegative = false,
                    AllowZero = false,
                    DefaultValue = 1,
                    LowerLimit = 1,
                    UseDefaultValue = true
                });
            if (count.Status != PromptStatus.OK) return;

            for (int index = 0; index < count.Value; index++)
            {
                document.SendStringToExecute("_.REDO ", true, false, true);
            }
        }

        private static void ShowSelection(
            Document document,
            ProjectStyleSelection selection,
            string title)
        {
            PopupTablePresenter.ShowReportAndOfferTable(
                document,
                title,
                "These choices are stored inside the current DWG and can be used as the project style schedule.",
                BuildRows(selection),
                "CE TOOLS PROJECT STYLE SCHEDULE");
        }

        private static List<KeyValuePair<string, string>> BuildRows(
            ProjectStyleSelection selection)
        {
            var rows = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("Discipline", selection.Discipline)
            };
            foreach (string key in SelectionKeys)
            {
                string value;
                if (!selection.Values.TryGetValue(key, out value) ||
                    string.IsNullOrWhiteSpace(value))
                {
                    value = "<Use drawing default>";
                }
                rows.Add(new KeyValuePair<string, string>(key, value));
            }
            return rows;
        }

        private static bool ImportProjectStyleSource(
            Document document,
            bool showResult)
        {
            List<ProjectStyleSource> sources = FindBundledStyleSources();
            var sourceLabels = sources
                .Select(source => source.DisplayName)
                .ToList();
            const string allBundledLabel = "All supplied CE style sources (01 to 03)";
            if (sources.Count > 1) sourceLabels.Insert(0, allBundledLabel);
            const string browseLabel = "Browse for another Civil 3D DWG or DWT...";
            sourceLabels.Add(browseLabel);

            var model = new ProductionSettingsDialogModel(
                "CE Tools - Import Project Styles",
                "Choose the approved source drawing(s). CE Tools uses Civil 3D's supported style export API and never imports design geometry.");
            model.AddChoice(
                "Source",
                "Style Source",
                "Source drawing or template",
                sourceLabels[0],
                "The three supplied CE project drawings are installed with the application bundle.",
                sourceLabels);
            model.AddChoice(
                "Conflict",
                "Conflict Handling",
                "Same-name style handling",
                "Keep existing and rename imported",
                "Choose whether current drawing styles win or the approved source replaces matching names.",
                new[]
                {
                    "Keep existing and rename imported",
                    "Replace matching styles from source"
                });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return false;

            string selected = model.Text("Source");
            var sourcePaths = new List<string>();
            if (string.Equals(selected, browseLabel, StringComparison.OrdinalIgnoreCase))
            {
                var browse = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Select Civil 3D Project Style Source",
                    Filter = "Civil 3D drawing or template (*.dwg;*.dwt)|*.dwg;*.dwt|All files (*.*)|*.*",
                    CheckFileExists = true,
                    Multiselect = false
                };
                if (browse.ShowDialog() != true) return false;
                sourcePaths.Add(browse.FileName);
            }
            else if (string.Equals(
                selected,
                allBundledLabel,
                StringComparison.OrdinalIgnoreCase))
            {
                sourcePaths.AddRange(sources.Select(source => source.FilePath));
            }
            else
            {
                ProjectStyleSource source = sources.FirstOrDefault(item =>
                    string.Equals(
                        item.DisplayName,
                        selected,
                        StringComparison.OrdinalIgnoreCase));
                if (source != null) sourcePaths.Add(source.FilePath);
            }

            if (sourcePaths.Count == 0 || sourcePaths.Any(path =>
                string.IsNullOrWhiteSpace(path) || !File.Exists(path)))
            {
                MessageBox.Show(
                    "The selected style source could not be found. Reinstall CE Tools or browse to another Civil 3D DWG/DWT.",
                    "CE Tools - Import Project Styles",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return false;
            }

            bool replace = string.Equals(
                model.Text("Conflict"),
                "Replace matching styles from source",
                StringComparison.OrdinalIgnoreCase);
            if (!PopupTablePresenter.ShowReview(
                "CE Tools - Confirm Style Import",
                replace
                    ? "Matching style names in the active drawing will be replaced by the approved source definitions."
                    : "Existing styles will remain; same-name source styles will be imported under a renamed copy.",
                new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string, string>("Sources", string.Join(", ", sourcePaths.Select(Path.GetFileName))),
                    new KeyValuePair<string, string>("Source count", sourcePaths.Count.ToString(CultureInfo.CurrentCulture)),
                    new KeyValuePair<string, string>("Conflict handling", replace ? "Replace" : "Keep and rename")
                },
                "Import"))
            {
                return false;
            }

            int exported = 0;
            try
            {
                foreach (string sourcePath in sourcePaths)
                {
                    exported += ExportStylesFromSource(
                        sourcePath,
                        document.Database,
                        replace
                            ? StyleConflictResolverType.Override
                            : StyleConflictResolverType.Rename);
                }
            }
            catch (System.Exception exception)
            {
                MessageBox.Show(
                    "Civil 3D could not import styles from the selected source.\n\n" +
                    exception.Message,
                    "CE Tools - Import Project Styles",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return false;
            }

            Dictionary<string, List<string>> catalogue =
                ReadCivilStyleCatalogue(document);
            int installedChoices = catalogue.Values.Sum(items =>
                Math.Max(0, items.Count - 1));
            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_PROJECTSTYLEIMPORT complete. Source styles processed={0}; installed style choices found={1}.",
                exported,
                installedChoices);
            if (showResult)
            {
                PopupTablePresenter.ShowReportAndOfferTable(
                    document,
                    "CE Tools - Project Styles Imported",
                    "The source drawing remains unchanged. Open Project Style Centre to assign imported styles by discipline.",
                    new List<KeyValuePair<string, string>>
                    {
                        new KeyValuePair<string, string>("Sources", string.Join(", ", sourcePaths.Select(Path.GetFileName))),
                        new KeyValuePair<string, string>("Source styles processed", exported.ToString(CultureInfo.CurrentCulture)),
                        new KeyValuePair<string, string>("Installed selectable choices", installedChoices.ToString(CultureInfo.CurrentCulture)),
                        new KeyValuePair<string, string>("Conflict handling", replace ? "Replaced matching styles" : "Kept existing styles")
                    },
                    "CE TOOLS PROJECT STYLE IMPORT");
            }
            return true;
        }

        private static int ExportStylesFromSource(
            string sourcePath,
            Database destination,
            StyleConflictResolverType conflictResolution)
        {
            using (var source = new Database(false, true))
            {
                source.ReadDwgFile(
                    sourcePath,
                    FileShare.Read,
                    true,
                    string.Empty);
                source.CloseInput(true);
                CivilDocument civilSource = CivilDocument.GetCivilDocument(source);
                if (civilSource == null)
                    throw new InvalidOperationException(
                        "The selected file does not contain a readable Civil 3D document.");

                var styleIds = new ObjectIdCollection();
                using (Transaction transaction =
                    source.TransactionManager.StartTransaction())
                {
                    var candidates = new HashSet<ObjectId>();
                    CollectStyleObjectIds(
                        ReadProperty(civilSource, "Styles"),
                        0,
                        new HashSet<object>(ReferenceEqualityComparer.Instance),
                        candidates);
                    foreach (ObjectId id in candidates)
                    {
                        if (id.IsNull || id.IsErased) continue;
                        DBObject value;
                        try
                        {
                            value = transaction.GetObject(id, OpenMode.ForRead, false);
                        }
                        catch
                        {
                            continue;
                        }
                        if (value is StyleBase) styleIds.Add(id);
                    }
                    if (styleIds.Count == 0)
                        throw new InvalidOperationException(
                            "No transferable Civil 3D styles were found in the selected file.");

                    StyleBase.ExportTo(
                        styleIds,
                        destination,
                        conflictResolution);
                    transaction.Commit();
                }
                return styleIds.Count;
            }
        }

        private static void CollectStyleObjectIds(
            object value,
            int depth,
            ISet<object> visited,
            ISet<ObjectId> result)
        {
            if (value == null || value is string || depth > 6 || visited.Contains(value))
                return;
            visited.Add(value);

            foreach (object item in EnumerateStyleItems(value))
            {
                if (item is ObjectId) result.Add((ObjectId)item);
            }

            PropertyInfo[] properties;
            try
            {
                properties = value.GetType().GetProperties(
                    BindingFlags.Public | BindingFlags.Instance);
            }
            catch
            {
                return;
            }
            foreach (PropertyInfo property in properties)
            {
                if (!property.CanRead || property.GetIndexParameters().Length != 0)
                    continue;
                if (depth > 0 &&
                    property.Name.IndexOf("Style", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                object child;
                try
                {
                    child = property.GetValue(value, null);
                }
                catch
                {
                    continue;
                }
                CollectStyleObjectIds(child, depth + 1, visited, result);
            }
        }

        private static List<ProjectStyleSource> FindBundledStyleSources()
        {
            string assemblyFolder = Path.GetDirectoryName(
                Assembly.GetExecutingAssembly().Location) ?? string.Empty;
            string resources = Path.GetFullPath(Path.Combine(
                assemblyFolder,
                "..",
                "..",
                "Resources",
                "ProjectStyles"));
            if (!Directory.Exists(resources)) return new List<ProjectStyleSource>();
            return Directory.GetFiles(resources, "*.dwg")
                .Concat(Directory.GetFiles(resources, "*.dwt"))
                .OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase)
                .Select((path, index) => new ProjectStyleSource(
                    "CE supplied source " + (index + 1).ToString("00", CultureInfo.InvariantCulture) +
                    " — " + Path.GetFileName(path),
                    path))
                .ToList();
        }

        private static Dictionary<string, List<string>> ReadCivilStyleCatalogue(
            Document document)
        {
            var result = new Dictionary<string, List<string>>(
                StringComparer.OrdinalIgnoreCase);
            foreach (string key in SelectionKeys)
                result[key] = new List<string> { "<Use drawing default>" };

            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (civilDocument == null) return result;

            object stylesRoot = ReadProperty(civilDocument, "Styles");
            if (stylesRoot == null) return result;

            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
            using (Transaction transaction =
                document.Database.TransactionManager.StartTransaction())
            {
                CollectStyles(
                    stylesRoot,
                    string.Empty,
                    0,
                    visited,
                    transaction,
                    result);
            }

            // Work from a stable key snapshot. Replacing a dictionary value while
            // enumerating its KeyValuePair collection throws "Collection was
            // modified" on the .NET Framework used by Civil 3D 2023.
            foreach (string catalogueKey in result.Keys.ToList())
            {
                List<string> ordered = result[catalogueKey]
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
                ordered.RemoveAll(value => string.Equals(
                    value,
                    "<Use drawing default>",
                    StringComparison.OrdinalIgnoreCase));
                ordered.Insert(0, "<Use drawing default>");
                result[catalogueKey] = ordered;
            }

            return result;
        }

        private static void CollectStyles(
            object value,
            string path,
            int depth,
            ISet<object> visited,
            Transaction transaction,
            IDictionary<string, List<string>> result)
        {
            if (value == null || depth > 4 || visited.Contains(value)) return;
            visited.Add(value);

            IList<object> items = EnumerateStyleItems(value);
            if (items.Count > 0)
            {
                int count = 0;
                foreach (object item in items)
                {
                    if (count++ > 10000) break;
                    string name = ReadStyleName(item, transaction);
                    string key = MapStyleKey(path);
                    if (!string.IsNullOrWhiteSpace(key) &&
                        !string.IsNullOrWhiteSpace(name))
                    {
                        result[key].Add(name);
                    }
                }
            }

            PropertyInfo[] properties;
            try
            {
                properties = value.GetType().GetProperties(
                    BindingFlags.Public | BindingFlags.Instance);
            }
            catch
            {
                return;
            }

            foreach (PropertyInfo property in properties)
            {
                if (property.GetIndexParameters().Length != 0 || !property.CanRead)
                    continue;
                string propertyName = property.Name;
                if (propertyName.IndexOf("Style", StringComparison.OrdinalIgnoreCase) < 0 &&
                    depth > 0)
                    continue;

                object child;
                try
                {
                    child = property.GetValue(value, null);
                }
                catch
                {
                    continue;
                }

                if (child == null || child is string) continue;
                string childPath = string.IsNullOrWhiteSpace(path)
                    ? propertyName
                    : path + "." + propertyName;
                CollectStyles(
                    child,
                    childPath,
                    depth + 1,
                    visited,
                    transaction,
                    result);
            }
        }

        private static IList<object> EnumerateStyleItems(object value)
        {
            var result = new List<object>();
            if (value == null || value is string) return result;

            IEnumerable enumerable = value as IEnumerable;
            if (enumerable != null)
            {
                try
                {
                    foreach (object item in enumerable) result.Add(item);
                }
                catch
                {
                    result.Clear();
                }
                if (result.Count > 0) return result;
            }

            try
            {
                MethodInfo objectIdsMethod = value.GetType().GetMethod(
                    "GetObjectIds",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    Type.EmptyTypes,
                    null);
                object objectIds = objectIdsMethod == null
                    ? null
                    : objectIdsMethod.Invoke(value, null);
                IEnumerable objectIdEnumerable = objectIds as IEnumerable;
                if (objectIdEnumerable != null)
                {
                    foreach (object item in objectIdEnumerable) result.Add(item);
                    if (result.Count > 0) return result;
                }
            }
            catch
            {
                result.Clear();
            }

            try
            {
                PropertyInfo countProperty = value.GetType().GetProperty(
                    "Count",
                    BindingFlags.Public | BindingFlags.Instance);
                int count = countProperty == null
                    ? 0
                    : Convert.ToInt32(
                        countProperty.GetValue(value, null),
                        CultureInfo.InvariantCulture);
                PropertyInfo indexer = value.GetType().GetProperties(
                        BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(property =>
                    {
                        ParameterInfo[] parameters = property.GetIndexParameters();
                        return parameters.Length == 1 &&
                            parameters[0].ParameterType == typeof(int) &&
                            property.CanRead;
                    });
                if (indexer != null)
                {
                    for (int index = 0; index < count; index++)
                        result.Add(indexer.GetValue(value, new object[] { index }));
                }
            }
            catch
            {
                result.Clear();
            }

            return result;
        }

        private static string ReadStyleName(object item, Transaction transaction)
        {
            if (item == null) return string.Empty;
            try
            {
                if (item is ObjectId)
                {
                    ObjectId id = (ObjectId)item;
                    if (id.IsNull || id.IsErased) return string.Empty;
                    DBObject databaseObject = transaction.GetObject(
                        id,
                        OpenMode.ForRead,
                        false);
                    return Convert.ToString(
                        ReadProperty(databaseObject, "Name"),
                        CultureInfo.CurrentCulture);
                }

                object name = ReadProperty(item, "Name");
                return Convert.ToString(name, CultureInfo.CurrentCulture);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string MapStyleKey(string path)
        {
            string value = (path ?? string.Empty).ToUpperInvariant();
            if (value.Contains("PRESSURE") && value.Contains("PIPE")) return "Pressure Pipe Style";
            if (value.Contains("APPURTENANCE")) return "Appurtenance Style";
            if (value.Contains("FITTING")) return "Fitting Style";
            if (value.Contains("STRUCTURE")) return "Structure Style";
            if (value.Contains("PIPE")) return "Pipe Style";
            if (value.Contains("CODESET") || value.Contains("CODE SET")) return "Code Set Style";
            if (value.Contains("ASSEMBLY")) return "Assembly Style";
            if (value.Contains("CORRIDOR")) return "Corridor Style";
            if (value.Contains("PROFILEVIEW") && value.Contains("BAND")) return "Profile View Band Set Style";
            if (value.Contains("PROFILEVIEW")) return "Profile View Style";
            if (value.Contains("PROFILE")) return "Profile Style";
            if (value.Contains("ALIGNMENT") && value.Contains("LABEL")) return "Alignment Label Set Style";
            if (value.Contains("ALIGNMENT")) return "Alignment Style";
            if (value.Contains("POINT") && value.Contains("LABEL")) return "Point Label Style";
            if (value.Contains("POINT")) return "Point Style";
            if (value.Contains("SURFACE")) return "Surface Style";
            return string.Empty;
        }

        private static object ReadProperty(object value, string propertyName)
        {
            if (value == null) return null;
            try
            {
                PropertyInfo property = value.GetType().GetProperty(
                    propertyName,
                    BindingFlags.Public | BindingFlags.Instance);
                return property == null || property.GetIndexParameters().Length != 0
                    ? null
                    : property.GetValue(value, null);
            }
            catch
            {
                return null;
            }
        }

        private static ProjectStyleSelection ReadSelection(Database database)
        {
            var selection = new ProjectStyleSelection();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                DBDictionary namedObjects = (DBDictionary)transaction.GetObject(
                    database.NamedObjectsDictionaryId,
                    OpenMode.ForRead,
                    false);
                if (!namedObjects.Contains(RootDictionaryName)) return selection;
                DBDictionary root = transaction.GetObject(
                    namedObjects.GetAt(RootDictionaryName),
                    OpenMode.ForRead,
                    false) as DBDictionary;
                if (root == null || !root.Contains(RecordName)) return selection;
                Xrecord record = transaction.GetObject(
                    root.GetAt(RecordName),
                    OpenMode.ForRead,
                    false) as Xrecord;
                if (record == null || record.Data == null) return selection;

                foreach (TypedValue typedValue in record.Data)
                {
                    string text = typedValue.Value as string;
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    int equals = text.IndexOf('=');
                    if (equals <= 0) continue;
                    string key = text.Substring(0, equals);
                    string value = text.Substring(equals + 1);
                    if (string.Equals(key, "Discipline", StringComparison.OrdinalIgnoreCase))
                        selection.Discipline = value;
                    else if (!string.Equals(key, "Schema", StringComparison.OrdinalIgnoreCase))
                        selection.Values[key] = value;
                }
                selection.Exists = true;
            }
            return selection;
        }

        private static void WriteSelection(
            Database database,
            ProjectStyleSelection selection)
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
                if (root.Contains(RecordName))
                {
                    record = (Xrecord)transaction.GetObject(
                        root.GetAt(RecordName),
                        OpenMode.ForWrite,
                        false);
                }
                else
                {
                    record = new Xrecord();
                    root.SetAt(RecordName, record);
                    transaction.AddNewlyCreatedDBObject(record, true);
                }

                var values = new List<TypedValue>
                {
                    new TypedValue((int)DxfCode.Text, "Schema=" + SchemaVersion),
                    new TypedValue((int)DxfCode.Text, "Discipline=" + selection.Discipline)
                };
                foreach (string key in SelectionKeys)
                {
                    string value;
                    if (selection.Values.TryGetValue(key, out value))
                    {
                        values.Add(new TypedValue(
                            (int)DxfCode.Text,
                            key + "=" + (value ?? string.Empty)));
                    }
                }
                record.Data = new ResultBuffer(values.ToArray());
                transaction.Commit();
            }
        }

        private static void RemoveSelection(Database database)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                DBDictionary namedObjects = (DBDictionary)transaction.GetObject(
                    database.NamedObjectsDictionaryId,
                    OpenMode.ForRead,
                    false);
                if (!namedObjects.Contains(RootDictionaryName)) return;
                DBDictionary root = transaction.GetObject(
                    namedObjects.GetAt(RootDictionaryName),
                    OpenMode.ForWrite,
                    false) as DBDictionary;
                if (root != null && root.Contains(RecordName))
                {
                    DBObject record = transaction.GetObject(
                        root.GetAt(RecordName),
                        OpenMode.ForWrite,
                        false);
                    record.Erase();
                    root.Remove(RecordName);
                }
                transaction.Commit();
            }
        }

        private static Document ActiveDocument()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
        }
    }

    internal sealed class ProjectStyleSelection
    {
        public ProjectStyleSelection()
        {
            Discipline = "Roads";
            Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        public bool Exists { get; set; }
        public string Discipline { get; set; }
        public Dictionary<string, string> Values { get; private set; }
    }

    internal sealed class ProjectStyleSource
    {
        public ProjectStyleSource(string displayName, string filePath)
        {
            DisplayName = displayName ?? string.Empty;
            FilePath = filePath ?? string.Empty;
        }

        public string DisplayName { get; private set; }
        public string FilePath { get; private set; }
    }

    internal sealed class ProjectStyleCenterWindow : Window
    {
        private readonly ComboBox _discipline;
        private readonly Dictionary<string, ComboBox> _selectors;

        public ProjectStyleCenterWindow(
            IEnumerable<string> disciplines,
            IEnumerable<string> keys,
            IDictionary<string, List<string>> catalogue,
            ProjectStyleSelection existing)
        {
            Accepted = false;
            ImportRequested = false;
            _selectors = new Dictionary<string, ComboBox>(StringComparer.OrdinalIgnoreCase);
            Title = "CE Tools - Project Style Centre";
            Width = 760;
            Height = 720;
            MinWidth = 620;
            MinHeight = 520;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.CanResize;

            var root = new DockPanel { Margin = new Thickness(16) };
            Content = root;

            var heading = new TextBlock
            {
                Text = "Project Civil 3D Styles",
                FontSize = 20,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 6)
            };
            DockPanel.SetDock(heading, Dock.Top);
            root.Children.Add(heading);

            var note = new TextBlock
            {
                Text = "Choose the discipline and preferred drawing styles. " +
                    Math.Max(
                        0,
                        catalogue.Values.Sum(items => Math.Max(0, items.Count - 1))) +
                    " installed Civil 3D style choices were found. Choices are stored inside this DWG; drawing styles are not renamed or deleted.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            };
            DockPanel.SetDock(note, Dock.Top);
            root.Children.Add(note);

            var disciplinePanel = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            disciplinePanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
            disciplinePanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            disciplinePanel.Children.Add(new TextBlock
            {
                Text = "Discipline",
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.SemiBold
            });
            _discipline = new ComboBox
            {
                ItemsSource = disciplines.ToList(),
                MinWidth = 280,
                Margin = new Thickness(8, 2, 0, 2)
            };
            _discipline.SelectedItem = string.IsNullOrWhiteSpace(existing.Discipline)
                ? "Roads"
                : existing.Discipline;
            Grid.SetColumn(_discipline, 1);
            disciplinePanel.Children.Add(_discipline);
            DockPanel.SetDock(disciplinePanel, Dock.Top);
            root.Children.Add(disciplinePanel);

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            int row = 0;
            foreach (string key in keys)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var label = new TextBlock
                {
                    Text = key,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 4, 8, 4)
                };
                Grid.SetRow(label, row);
                grid.Children.Add(label);

                List<string> items;
                if (!catalogue.TryGetValue(key, out items) || items.Count == 0)
                    items = new List<string> { "<Use drawing default>" };
                var selector = new ComboBox
                {
                    ItemsSource = items,
                    IsEditable = true,
                    IsTextSearchEnabled = true,
                    Margin = new Thickness(0, 3, 0, 3),
                    MinWidth = 340
                };
                string selected;
                if (!existing.Values.TryGetValue(key, out selected) ||
                    string.IsNullOrWhiteSpace(selected))
                {
                    selected = "<Use drawing default>";
                }
                selector.Text = selected;
                Grid.SetColumn(selector, 1);
                Grid.SetRow(selector, row);
                grid.Children.Add(selector);
                _selectors[key] = selector;
                row++;
            }
            scroll.Content = grid;
            root.Children.Add(scroll);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            var save = new Button
            {
                Content = "Review and Save",
                MinWidth = 130,
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(0, 0, 8, 0),
                IsDefault = true
            };
            save.Click += delegate
            {
                Accepted = true;
                DialogResult = true;
                Close();
            };
            var import = new Button
            {
                Content = "Import Source Styles...",
                MinWidth = 150,
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(0, 0, 8, 0)
            };
            import.Click += delegate
            {
                ImportRequested = true;
                Close();
            };
            var cancel = new Button
            {
                Content = "Cancel",
                MinWidth = 90,
                Padding = new Thickness(12, 6, 12, 6),
                IsCancel = true
            };
            buttons.Children.Add(import);
            buttons.Children.Add(save);
            buttons.Children.Add(cancel);
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(buttons);

            // The scroll area must be the DockPanel's final fill child. Keeping
            // the button row last caused it to stretch across the full window at
            // high DPI and covered the style selectors.
            root.Children.Remove(scroll);
            root.Children.Add(scroll);
        }

        public bool Accepted { get; private set; }
        public bool ImportRequested { get; private set; }

        public ProjectStyleSelection BuildSelection()
        {
            var result = new ProjectStyleSelection
            {
                Exists = true,
                Discipline = Convert.ToString(
                    _discipline.SelectedItem,
                    CultureInfo.CurrentCulture)
            };
            if (string.IsNullOrWhiteSpace(result.Discipline)) result.Discipline = "Roads";
            foreach (KeyValuePair<string, ComboBox> pair in _selectors)
            {
                string value = pair.Value.Text == null
                    ? string.Empty
                    : pair.Value.Text.Trim();
                result.Values[pair.Key] = value;
            }
            return result;
        }
    }

    internal sealed class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceEqualityComparer Instance =
            new ReferenceEqualityComparer();

        public new bool Equals(object first, object second)
        {
            return ReferenceEquals(first, second);
        }

        public int GetHashCode(object value)
        {
            return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value);
        }
    }
}

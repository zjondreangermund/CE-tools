using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.ProjectStylePresetCommands))]

namespace CETools.Civil3D
{
    internal static class ProjectStylePresetManager
    {
        private const string RootDictionaryName = "CE_TOOLS";
        private const string ActiveRecordName = "PROJECT_STYLE_SELECTION";
        private const string DisciplineRecordPrefix = "PROJECT_STYLE_SELECTION_";
        private const string PresetHeader = "CE_TOOLS_PROJECT_STYLE_PRESET_V1";

        private static readonly HashSet<Document> PromptedDocuments =
            new HashSet<Document>();
        private static readonly Queue<Document> PendingDocuments =
            new Queue<Document>();
        private static bool _initialized;
        private static bool _showing;

        internal static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;
            DocumentCollection documents = AcApplication.DocumentManager;
            documents.DocumentCreated += OnDocumentCreated;
            documents.DocumentActivated += OnDocumentActivated;
            documents.DocumentToBeDestroyed += OnDocumentToBeDestroyed;
            AcApplication.Idle += OnIdle;
            QueueDocument(documents.MdiActiveDocument);
        }

        internal static void Terminate()
        {
            if (!_initialized) return;
            _initialized = false;
            DocumentCollection documents = AcApplication.DocumentManager;
            documents.DocumentCreated -= OnDocumentCreated;
            documents.DocumentActivated -= OnDocumentActivated;
            documents.DocumentToBeDestroyed -= OnDocumentToBeDestroyed;
            AcApplication.Idle -= OnIdle;
            PromptedDocuments.Clear();
            PendingDocuments.Clear();
        }

        internal static bool SaveFromDrawing(Document document)
        {
            if (document == null) return false;
            ProjectStyleSelection selection =
                ProjectStyleCenterCommands.ReadSelection(document.Database);
            if (!selection.Exists || string.IsNullOrWhiteSpace(selection.Discipline))
                return false;

            Dictionary<string, ProjectStyleSelection> presets = LoadPresets();
            presets[selection.Discipline] = Clone(selection);
            SavePresets(presets);
            try
            {
                using (DocumentLock documentLock = document.LockDocument())
                {
                    WriteDisciplineSelection(document.Database, selection);
                    SynchronizeDisciplineSettings(document.Database, selection);
                }
                return true;
            }
            catch
            {
                QueueDocument(document);
                return false;
            }
        }

        internal static bool ApplySavedPreset(Document document, bool showResult)
        {
            if (document == null) return false;
            Dictionary<string, ProjectStyleSelection> presets = LoadPresets();
            if (presets.Count == 0) return false;

            ProjectStyleSelection existing =
                ProjectStyleCenterCommands.ReadSelection(document.Database);
            ProjectStyleSelection selected = null;
            if (existing.Exists && !string.IsNullOrWhiteSpace(existing.Discipline))
                presets.TryGetValue(existing.Discipline, out selected);
            if (selected == null)
                selected = presets.OrderBy(item => item.Key)
                    .Select(item => item.Value)
                    .FirstOrDefault();
            if (selected == null) return false;

            try
            {
                using (DocumentLock documentLock = document.LockDocument())
                {
                    WriteActiveSelection(document.Database, selected);
                    foreach (ProjectStyleSelection preset in presets.Values)
                        WriteDisciplineSelection(document.Database, preset);
                    SynchronizeDisciplineSettings(document.Database, selected);
                }
            }
            catch
            {
                QueueDocument(document);
                return false;
            }
            document.Editor.Regen();
            if (showResult)
            {
                document.Editor.WriteMessage(
                    "\nCE_PROJECTSTYLEAPPLY complete. Saved discipline={0}; selections applied={1}.",
                    selected.Discipline,
                    selected.Values.Count);
            }
            return true;
        }

        internal static bool HasSavedPreset()
        {
            try { return LoadPresets().Count > 0; }
            catch { return false; }
        }

        internal static string SavedPresetSummary()
        {
            Dictionary<string, ProjectStyleSelection> presets = LoadPresets();
            if (presets.Count == 0) return "No saved project style preset is available.";
            return string.Join(", ", presets.Keys.OrderBy(value => value)) +
                " (" + presets.Sum(item => item.Value.Values.Count)
                    .ToString(CultureInfo.CurrentCulture) + " stored choices)";
        }

        internal static void ShowChoice(Document document, bool force)
        {
            if (document == null || _showing || !HasSavedPreset()) return;
            if (!force && PromptedDocuments.Contains(document)) return;
            PromptedDocuments.Add(document);
            _showing = true;
            try
            {
                ProjectStyleSelection current =
                    ProjectStyleCenterCommands.ReadSelection(document.Database);
                var window = new ProjectStylePresetChoiceWindow(
                    current.Exists,
                    current.Discipline,
                    SavedPresetSummary());
                AcApplication.ShowModalWindow(window);
                if (window.Choice == ProjectStyleOpeningChoice.UseSaved)
                {
                    if (!ApplySavedPreset(document, true))
                    {
                        PromptedDocuments.Remove(document);
                        QueueDocument(document);
                    }
                }
                else
                    document.Editor.WriteMessage(
                        "\nCE Tools kept the project style selections already stored in this drawing.");
            }
            finally
            {
                _showing = false;
            }
        }

        private static void OnDocumentCreated(
            object sender,
            DocumentCollectionEventArgs e)
        {
            QueueDocument(e == null ? null : e.Document);
        }

        private static void OnDocumentActivated(
            object sender,
            DocumentCollectionEventArgs e)
        {
            QueueDocument(e == null ? null : e.Document);
        }

        private static void OnDocumentToBeDestroyed(
            object sender,
            DocumentCollectionEventArgs e)
        {
            if (e != null && e.Document != null)
                PromptedDocuments.Remove(e.Document);
        }

        private static void QueueDocument(Document document)
        {
            if (document == null || PromptedDocuments.Contains(document)) return;
            if (!PendingDocuments.Contains(document)) PendingDocuments.Enqueue(document);
        }

        private static void OnIdle(object sender, EventArgs e)
        {
            if (_showing || PendingDocuments.Count == 0 || !HasSavedPreset()) return;
            Document active = AcApplication.DocumentManager.MdiActiveDocument;
            if (active == null) return;
            string commandNames = Convert.ToString(
                AcApplication.GetSystemVariable("CMDNAMES"),
                CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(commandNames)) return;
            object commandActive = AcApplication.GetSystemVariable("CMDACTIVE");
            if (Convert.ToInt32(commandActive, CultureInfo.InvariantCulture) != 0) return;

            int count = PendingDocuments.Count;
            while (count-- > 0)
            {
                Document candidate = PendingDocuments.Dequeue();
                if (candidate == null || PromptedDocuments.Contains(candidate)) continue;
                if (!ReferenceEquals(candidate, active))
                {
                    PendingDocuments.Enqueue(candidate);
                    continue;
                }
                ShowChoice(candidate, false);
                break;
            }
        }

        private static string PresetPath
        {
            get
            {
                string folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "CE Tools");
                Directory.CreateDirectory(folder);
                return Path.Combine(folder, "ProjectStylePresets.ceps");
            }
        }

        private static Dictionary<string, ProjectStyleSelection> LoadPresets()
        {
            var result = new Dictionary<string, ProjectStyleSelection>(
                StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(PresetPath)) return result;
            string[] lines = File.ReadAllLines(PresetPath, Encoding.UTF8);
            if (lines.Length == 0 || !string.Equals(
                    lines[0].Trim(), PresetHeader,
                    StringComparison.Ordinal))
                return result;

            foreach (string line in lines.Skip(1))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                string[] parts = line.Split(new[] { '|' }, 3);
                if (parts.Length != 3) continue;
                string discipline = Decode(parts[0]);
                string key = Decode(parts[1]);
                string value = Decode(parts[2]);
                if (string.IsNullOrWhiteSpace(discipline) ||
                    string.IsNullOrWhiteSpace(key)) continue;
                ProjectStyleSelection selection;
                if (!result.TryGetValue(discipline, out selection))
                {
                    selection = new ProjectStyleSelection
                    {
                        Exists = true,
                        Discipline = discipline
                    };
                    result[discipline] = selection;
                }
                selection.Values[key] = value;
            }
            return result;
        }

        private static void SavePresets(
            IDictionary<string, ProjectStyleSelection> presets)
        {
            var lines = new List<string> { PresetHeader };
            foreach (KeyValuePair<string, ProjectStyleSelection> pair in
                presets.OrderBy(item => item.Key))
            {
                foreach (KeyValuePair<string, string> value in
                    pair.Value.Values.OrderBy(item => item.Key))
                {
                    lines.Add(
                        Encode(pair.Key) + "|" +
                        Encode(value.Key) + "|" +
                        Encode(value.Value ?? string.Empty));
                }
            }
            string temporary = PresetPath + ".tmp";
            File.WriteAllLines(temporary, lines, Encoding.UTF8);
            if (File.Exists(PresetPath)) File.Delete(PresetPath);
            File.Move(temporary, PresetPath);
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
                return Encoding.UTF8.GetString(Convert.FromBase64String(value));
            }
            catch
            {
                return string.Empty;
            }
        }

        private static ProjectStyleSelection Clone(ProjectStyleSelection source)
        {
            var result = new ProjectStyleSelection
            {
                Exists = true,
                Discipline = source.Discipline
            };
            foreach (KeyValuePair<string, string> value in source.Values)
                result.Values[value.Key] = value.Value;
            return result;
        }

        private static void WriteActiveSelection(
            Database database,
            ProjectStyleSelection selection)
        {
            WriteSelectionRecord(database, ActiveRecordName, selection);
        }

        private static void WriteDisciplineSelection(
            Database database,
            ProjectStyleSelection selection)
        {
            string suffix = new string((selection.Discipline ?? "GENERAL")
                .ToUpperInvariant()
                .Select(character => char.IsLetterOrDigit(character)
                    ? character
                    : '_')
                .ToArray());
            WriteSelectionRecord(
                database,
                DisciplineRecordPrefix + suffix,
                selection);
        }

        private static void WriteSelectionRecord(
            Database database,
            string recordName,
            ProjectStyleSelection selection)
        {
            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                DBDictionary named = (DBDictionary)transaction.GetObject(
                    database.NamedObjectsDictionaryId,
                    OpenMode.ForWrite,
                    false);
                DBDictionary root;
                if (named.Contains(RootDictionaryName))
                {
                    root = (DBDictionary)transaction.GetObject(
                        named.GetAt(RootDictionaryName),
                        OpenMode.ForWrite,
                        false);
                }
                else
                {
                    root = new DBDictionary();
                    named.SetAt(RootDictionaryName, root);
                    transaction.AddNewlyCreatedDBObject(root, true);
                }

                Xrecord record;
                if (root.Contains(recordName))
                {
                    record = (Xrecord)transaction.GetObject(
                        root.GetAt(recordName),
                        OpenMode.ForWrite,
                        false);
                }
                else
                {
                    record = new Xrecord();
                    root.SetAt(recordName, record);
                    transaction.AddNewlyCreatedDBObject(record, true);
                }

                var values = new List<TypedValue>
                {
                    new TypedValue((int)DxfCode.Text, "Schema=2"),
                    new TypedValue((int)DxfCode.Text,
                        "Discipline=" + (selection.Discipline ?? "Roads"))
                };
                foreach (KeyValuePair<string, string> value in
                    selection.Values.OrderBy(item => item.Key))
                {
                    values.Add(new TypedValue(
                        (int)DxfCode.Text,
                        value.Key + "=" + (value.Value ?? string.Empty)));
                }
                record.Data = new ResultBuffer(values.ToArray());
                transaction.Commit();
            }
        }

        private static void SynchronizeDisciplineSettings(
            Database database,
            ProjectStyleSelection selection)
        {
            if (!string.Equals(
                    selection.Discipline,
                    "Sewer",
                    StringComparison.OrdinalIgnoreCase))
                return;

            SewerProductionSettings settings = SewerProductionSettings.Read(database);
            settings.AlignmentStyle = Read(selection, "Alignment Style", settings.AlignmentStyle);
            settings.AlignmentLabelSetStyle = Read(selection, "Alignment Label Set Style", settings.AlignmentLabelSetStyle);
            settings.ProfileStyle = Read(selection, "Profile Style", settings.ProfileStyle);
            settings.ProfileLabelSetStyle = Read(selection, "Profile Label Set Style", settings.ProfileLabelSetStyle);
            settings.ProfileViewStyle = Read(selection, "Profile View Style", settings.ProfileViewStyle);
            settings.ProfileViewBandSetStyle = Read(selection, "Profile View Band Set Style", settings.ProfileViewBandSetStyle);
            settings.PipePlanLabelStyle = Read(selection, "Pipe Label Style", settings.PipePlanLabelStyle);
            settings.StructurePlanLabelStyle = Read(selection, "Structure Label Style", settings.StructurePlanLabelStyle);
            settings.Write(database);
        }

        private static string Read(
            ProjectStyleSelection selection,
            string key,
            string fallback)
        {
            string value;
            return selection.Values.TryGetValue(key, out value) &&
                !string.IsNullOrWhiteSpace(value) &&
                !string.Equals(value, "<Use drawing default>", StringComparison.OrdinalIgnoreCase)
                ? value
                : fallback;
        }
    }

    public sealed class ProjectStylePresetCommands
    {
        [CommandMethod("CE_PROJECTSTYLESAVE", CommandFlags.Modal | CommandFlags.Redraw)]
        public void SavePreset()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            bool saved = ProjectStylePresetManager.SaveFromDrawing(document);
            document.Editor.WriteMessage(saved
                ? "\nCE_PROJECTSTYLESAVE complete. The current discipline was added to the saved cross-drawing project style preset."
                : "\nCE_PROJECTSTYLESAVE: no project style selection is stored in this DWG. Run CE_PROJECTSTYLES first.");
        }

        [CommandMethod("CE_PROJECTSTYLEAPPLY", CommandFlags.Modal | CommandFlags.Redraw)]
        public void ApplyPreset()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            if (!ProjectStylePresetManager.ApplySavedPreset(document, true))
                document.Editor.WriteMessage(
                    "\nCE_PROJECTSTYLEAPPLY: no saved cross-drawing project style preset is available.");
        }

        [CommandMethod("CE_PROJECTSTYLEOPENPROMPT", CommandFlags.Modal | CommandFlags.Redraw)]
        public void ShowOpeningPrompt()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document != null)
                ProjectStylePresetManager.ShowChoice(document, true);
        }
    }

    internal enum ProjectStyleOpeningChoice
    {
        KeepExisting,
        UseSaved,
        Cancel
    }

    internal sealed class ProjectStylePresetChoiceWindow : Window
    {
        public ProjectStylePresetChoiceWindow(
            bool drawingHasSelection,
            string drawingDiscipline,
            string savedSummary)
        {
            Choice = ProjectStyleOpeningChoice.KeepExisting;
            Title = "CE Tools - Project Styles for This Drawing";
            Width = 570;
            Height = 310;
            MinWidth = 520;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;

            var root = new StackPanel { Margin = new Thickness(22) };
            Content = root;
            root.Children.Add(new TextBlock
            {
                Text = "Choose the project styles to use",
                FontSize = 21,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 10)
            });
            root.Children.Add(new TextBlock
            {
                Text = drawingHasSelection
                    ? "This drawing already stores project style selections for " +
                        (string.IsNullOrWhiteSpace(drawingDiscipline)
                            ? "a discipline"
                            : drawingDiscipline) + "."
                    : "This drawing does not yet contain CE Tools project style selections.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            });
            root.Children.Add(new TextBlock
            {
                Text = "Saved preset: " + savedSummary,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 18)
            });

            var keep = new Button
            {
                Content = "Keep existing drawing project styles",
                MinHeight = 42,
                Margin = new Thickness(0, 0, 0, 8),
                IsDefault = drawingHasSelection
            };
            keep.Click += delegate
            {
                Choice = ProjectStyleOpeningChoice.KeepExisting;
                DialogResult = true;
                Close();
            };
            root.Children.Add(keep);

            var useSaved = new Button
            {
                Content = "Use saved project styles",
                MinHeight = 42,
                IsDefault = !drawingHasSelection
            };
            useSaved.Click += delegate
            {
                Choice = ProjectStyleOpeningChoice.UseSaved;
                DialogResult = true;
                Close();
            };
            root.Children.Add(useSaved);

            var cancel = new Button
            {
                Content = "Cancel",
                Width = 90,
                Margin = new Thickness(0, 14, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                IsCancel = true
            };
            cancel.Click += delegate
            {
                Choice = ProjectStyleOpeningChoice.Cancel;
                Close();
            };
            root.Children.Add(cancel);
        }

        public ProjectStyleOpeningChoice Choice { get; private set; }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.September14ReusableStylePresetCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// User-level project style preset library.
    ///
    /// The existing discipline presets live inside one DWG. These commands persist
    /// the selected Project Style Centre names under %APPDATA% so the same named
    /// preset can be activated in any other drawing on the workstation. Civil 3D
    /// style definitions are never renamed/deleted; if the destination drawing does
    /// not contain a selected style definition, CE_PROJECTSTYLEIMPORT remains the
    /// supported way to import the approved Civil 3D styles first.
    /// </summary>
    public sealed class September14ReusableStylePresetCommands
    {
        private const string Schema = "1";
        private const string Extension = ".cepreset";

        [CommandMethod("CE_TOOLS", "CE_STYLEPRESETLIBRARY", CommandFlags.Modal | CommandFlags.Redraw)]
        public void Library()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            var model = new ProductionSettingsDialogModel(
                "CE Tools - Reusable Project Style Presets",
                "Save the current Project Style Centre selection under your own name, then apply it in another drawing. Presets are stored in your CE Tools user library and are independent of the current DWG.");
            model.AddChoice(
                "Action",
                "01 Action",
                "Preset library action",
                "Apply saved preset to this drawing",
                "Selections are reusable between drawings. Style definitions themselves remain Civil 3D drawing content.",
                new[]
                {
                    "Apply saved preset to this drawing",
                    "Save current selection under another name",
                    "Review reusable presets",
                    "Delete reusable preset"
                });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            string action = model.Text("Action") ?? string.Empty;
            if (action.StartsWith("Save", StringComparison.OrdinalIgnoreCase))
                SaveCurrent(document);
            else if (action.StartsWith("Review", StringComparison.OrdinalIgnoreCase))
                Review(document);
            else if (action.StartsWith("Delete", StringComparison.OrdinalIgnoreCase))
                Delete(document);
            else
                Apply(document);
        }

        [CommandMethod("CE_TOOLS", "CE_STYLEPRESETSAVE", CommandFlags.Modal | CommandFlags.Redraw)]
        public void Save()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document != null) SaveCurrent(document);
        }

        [CommandMethod("CE_TOOLS", "CE_STYLEPRESETAPPLY", CommandFlags.Modal | CommandFlags.Redraw)]
        public void ApplySaved()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document != null) Apply(document);
        }

        private static void SaveCurrent(Document document)
        {
            ProjectStyleSelection selection = ProjectStyleCenterCommands.ReadSelection(document.Database);
            if (selection == null || !selection.Exists)
            {
                document.Editor.WriteMessage(
                    "\nCE_STYLEPRESETSAVE: no Project Style Centre selection exists. Run CE_PROJECTSTYLES first, choose the discipline/styles, then save the reusable preset.");
                return;
            }

            string suggested = string.IsNullOrWhiteSpace(selection.Discipline)
                ? "Project Styles"
                : selection.Discipline + " - Project Styles";
            var nameWindow = new ReusableStylePresetNameWindow(suggested);
            AcApplication.ShowModalWindow(nameWindow);
            if (!nameWindow.Accepted) return;

            string presetName = (nameWindow.PresetName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(presetName)) return;

            string folder = EnsureLibraryFolder();
            string filePath = Path.Combine(folder, SafeFileName(presetName) + Extension);
            if (File.Exists(filePath))
            {
                bool replace = PopupTablePresenter.ShowReview(
                    "CE Tools - Replace Reusable Style Preset",
                    "A reusable preset with this file name already exists. Replace it with the current Project Style Centre choices?",
                    new List<KeyValuePair<string, string>>
                    {
                        new KeyValuePair<string, string>("Preset", presetName),
                        new KeyValuePair<string, string>("Discipline", selection.Discipline ?? "Roads"),
                        new KeyValuePair<string, string>("File", filePath)
                    },
                    "Replace");
                if (!replace) return;
            }

            var reusable = new ReusableStylePreset
            {
                Name = presetName,
                Discipline = string.IsNullOrWhiteSpace(selection.Discipline) ? "Roads" : selection.Discipline.Trim(),
                SourceDrawing = document.Name ?? string.Empty
            };
            foreach (KeyValuePair<string, string> pair in selection.Values)
                reusable.Values[pair.Key] = pair.Value ?? string.Empty;

            WritePreset(filePath, reusable);
            document.Editor.WriteMessage(
                "\nCE_STYLEPRESETSAVE complete. Saved reusable preset '{0}' ({1}, {2} choices). It can now be applied in another drawing with CE_STYLEPRESETLIBRARY.",
                reusable.Name,
                reusable.Discipline,
                reusable.Values.Count);
        }

        private static void Apply(Document document)
        {
            List<ReusableStylePresetFile> files = ReadLibrary();
            if (files.Count == 0)
            {
                document.Editor.WriteMessage(
                    "\nCE_STYLEPRESETAPPLY: no reusable presets exist yet. Configure CE_PROJECTSTYLES and use CE_STYLEPRESETSAVE first.");
                return;
            }

            string[] labels = files.Select(item => item.Label).ToArray();
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Apply Reusable Project Style Preset",
                "Choose a user-level preset. CE Tools copies those selected style names into this DWG and makes the discipline preset active.");
            model.AddChoice(
                "Preset",
                "01 Reusable Preset",
                "Saved preset",
                labels[0],
                "If this drawing does not contain one of the selected Civil 3D style definitions, run CE_PROJECTSTYLEIMPORT before production commands.",
                labels);
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            string selectedLabel = model.Text("Preset") ?? string.Empty;
            ReusableStylePresetFile chosen = files.FirstOrDefault(item =>
                string.Equals(item.Label, selectedLabel, StringComparison.OrdinalIgnoreCase));
            if (chosen == null) return;

            ReusableStylePreset reusable;
            string error;
            if (!TryReadPreset(chosen.FilePath, out reusable, out error))
            {
                MessageBox.Show(
                    "The reusable preset could not be read.\n\n" + error,
                    "CE Tools - Reusable Project Style Presets",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            var selection = new ProjectStyleSelection
            {
                Exists = true,
                Discipline = string.IsNullOrWhiteSpace(reusable.Discipline) ? "Roads" : reusable.Discipline
            };
            foreach (KeyValuePair<string, string> pair in reusable.Values)
                selection.Values[pair.Key] = pair.Value;

            // Store both the discipline snapshot and the active Project Style Centre
            // selection using the already-proven DWG preset manager. This deliberately
            // avoids touching Civil style definitions or geometry.
            August11DisciplineStylePresetManager.SavePreset(document.Database, selection);
            bool activated = August11DisciplineStylePresetManager.Activate(document.Database, selection.Discipline);
            if (!activated)
            {
                document.Editor.WriteMessage("\nCE_STYLEPRESETAPPLY stopped safely: the imported preset could not be activated.");
                return;
            }

            CogoPointProjectStyleManager.Queue();
            CogoPointProjectStyleCommands.ApplySelectedStyles(document, true);
            document.Editor.Regen();

            PopupTablePresenter.ShowReview(
                "CE Tools - Reusable Style Preset Applied",
                "The selected Project Style Centre names are now active in this DWG. Missing Civil 3D style definitions can be imported with CE_PROJECTSTYLEIMPORT.",
                new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string, string>("Preset", reusable.Name),
                    new KeyValuePair<string, string>("Discipline", selection.Discipline),
                    new KeyValuePair<string, string>("Saved style choices", selection.Values.Count.ToString()),
                    new KeyValuePair<string, string>("Original source drawing", string.IsNullOrWhiteSpace(reusable.SourceDrawing) ? "<Not recorded>" : reusable.SourceDrawing),
                    new KeyValuePair<string, string>("Style definitions", "Use CE_PROJECTSTYLEIMPORT if a named style is not installed in this drawing")
                },
                "Close");
            document.Editor.WriteMessage(
                "\nCE_STYLEPRESETAPPLY complete. '{0}' is active for {1} in this drawing.",
                reusable.Name,
                selection.Discipline);
        }

        private static void Review(Document document)
        {
            List<ReusableStylePresetFile> files = ReadLibrary();
            if (files.Count == 0)
            {
                document.Editor.WriteMessage("\nCE_STYLEPRESETLIBRARY: no reusable presets exist yet.");
                return;
            }

            var rows = new List<IList<string>>();
            foreach (ReusableStylePresetFile item in files)
            {
                ReusableStylePreset preset;
                string error;
                if (TryReadPreset(item.FilePath, out preset, out error))
                {
                    rows.Add(new List<string>
                    {
                        preset.Name,
                        preset.Discipline,
                        preset.Values.Count.ToString(),
                        preset.SourceDrawing
                    });
                }
                else
                {
                    rows.Add(new List<string> { Path.GetFileNameWithoutExtension(item.FilePath), "<Unreadable>", "0", error });
                }
            }

            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - Reusable Project Style Presets",
                "These named selections are stored outside the DWG under your CE Tools user library and can be applied to other drawings.",
                new List<string> { "Preset", "Discipline", "Choices", "Source Drawing" },
                rows,
                "CE TOOLS REUSABLE PROJECT STYLE PRESETS");
        }

        private static void Delete(Document document)
        {
            List<ReusableStylePresetFile> files = ReadLibrary();
            if (files.Count == 0)
            {
                document.Editor.WriteMessage("\nCE_STYLEPRESETLIBRARY: no reusable presets exist to delete.");
                return;
            }

            string[] labels = files.Select(item => item.Label).ToArray();
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Delete Reusable Style Preset",
                "Delete only the saved user-library preset. No drawing styles or DWG selections are changed.");
            model.AddChoice("Preset", "01 Reusable Preset", "Saved preset", labels[0], "Choose the reusable preset to delete.", labels);
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            string selectedLabel = model.Text("Preset") ?? string.Empty;
            ReusableStylePresetFile chosen = files.FirstOrDefault(item =>
                string.Equals(item.Label, selectedLabel, StringComparison.OrdinalIgnoreCase));
            if (chosen == null) return;

            bool remove = PopupTablePresenter.ShowReview(
                "CE Tools - Delete Reusable Style Preset",
                "This removes the preset file from your CE Tools user library. Current drawing styles and active settings remain unchanged.",
                new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string, string>("Preset", chosen.Label),
                    new KeyValuePair<string, string>("File", chosen.FilePath)
                },
                "Delete");
            if (!remove) return;

            try
            {
                File.Delete(chosen.FilePath);
                document.Editor.WriteMessage("\nDeleted reusable style preset: {0}.", chosen.Label);
            }
            catch (System.Exception exception)
            {
                MessageBox.Show(
                    "The reusable preset could not be deleted.\n\n" + exception.Message,
                    "CE Tools - Reusable Project Style Presets",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private static string EnsureLibraryFolder()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string folder = Path.Combine(appData, "CE Tools", "Style Presets");
            Directory.CreateDirectory(folder);
            return folder;
        }

        private static List<ReusableStylePresetFile> ReadLibrary()
        {
            string folder = EnsureLibraryFolder();
            var result = new List<ReusableStylePresetFile>();
            foreach (string filePath in Directory.GetFiles(folder, "*" + Extension))
            {
                ReusableStylePreset preset;
                string error;
                string label;
                if (TryReadPreset(filePath, out preset, out error))
                {
                    label = preset.Name + " — " + preset.Discipline;
                }
                else
                {
                    label = Path.GetFileNameWithoutExtension(filePath) + " — unreadable";
                }
                result.Add(new ReusableStylePresetFile(filePath, label));
            }
            return result.OrderBy(item => item.Label, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        private static void WritePreset(string filePath, ReusableStylePreset preset)
        {
            var lines = new List<string>
            {
                "CE_TOOLS_STYLE_PRESET=" + Schema,
                "Name=" + Encode(preset.Name),
                "Discipline=" + Encode(preset.Discipline),
                "SourceDrawing=" + Encode(preset.SourceDrawing)
            };
            foreach (KeyValuePair<string, string> pair in preset.Values.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
            {
                lines.Add("Value=" + Encode(pair.Key) + "|" + Encode(pair.Value));
            }
            File.WriteAllLines(filePath, lines.ToArray(), new UTF8Encoding(false));
        }

        private static bool TryReadPreset(string filePath, out ReusableStylePreset preset, out string error)
        {
            preset = new ReusableStylePreset();
            error = string.Empty;
            try
            {
                string[] lines = File.ReadAllLines(filePath, Encoding.UTF8);
                if (lines.Length == 0 || !string.Equals(lines[0], "CE_TOOLS_STYLE_PRESET=" + Schema, StringComparison.Ordinal))
                {
                    error = "Unsupported or missing CE Tools style preset schema.";
                    return false;
                }

                foreach (string line in lines.Skip(1))
                {
                    if (line.StartsWith("Name=", StringComparison.Ordinal))
                        preset.Name = Decode(line.Substring("Name=".Length));
                    else if (line.StartsWith("Discipline=", StringComparison.Ordinal))
                        preset.Discipline = Decode(line.Substring("Discipline=".Length));
                    else if (line.StartsWith("SourceDrawing=", StringComparison.Ordinal))
                        preset.SourceDrawing = Decode(line.Substring("SourceDrawing=".Length));
                    else if (line.StartsWith("Value=", StringComparison.Ordinal))
                    {
                        string payload = line.Substring("Value=".Length);
                        int separator = payload.IndexOf('|');
                        if (separator <= 0) continue;
                        string key = Decode(payload.Substring(0, separator));
                        string value = Decode(payload.Substring(separator + 1));
                        if (!string.IsNullOrWhiteSpace(key)) preset.Values[key] = value;
                    }
                }

                if (string.IsNullOrWhiteSpace(preset.Name))
                    preset.Name = Path.GetFileNameWithoutExtension(filePath);
                if (string.IsNullOrWhiteSpace(preset.Discipline)) preset.Discipline = "Roads";
                return true;
            }
            catch (System.Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }

        private static string Encode(string value)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? string.Empty));
        }

        private static string Decode(string value)
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value ?? string.Empty));
        }

        private static string SafeFileName(string value)
        {
            string result = (value ?? string.Empty).Trim();
            foreach (char invalid in Path.GetInvalidFileNameChars()) result = result.Replace(invalid, '_');
            if (string.IsNullOrWhiteSpace(result)) result = "Project Style Preset";
            return result;
        }

        private sealed class ReusableStylePreset
        {
            public ReusableStylePreset()
            {
                Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            public string Name { get; set; }
            public string Discipline { get; set; }
            public string SourceDrawing { get; set; }
            public Dictionary<string, string> Values { get; private set; }
        }

        private sealed class ReusableStylePresetFile
        {
            public ReusableStylePresetFile(string filePath, string label)
            {
                FilePath = filePath;
                Label = label;
            }

            public string FilePath { get; private set; }
            public string Label { get; private set; }
        }

        private sealed class ReusableStylePresetNameWindow : Window
        {
            private readonly TextBox _name;

            public ReusableStylePresetNameWindow(string suggested)
            {
                Accepted = false;
                Title = "CE Tools - Save Reusable Style Preset";
                Width = 500;
                Height = 210;
                MinWidth = 420;
                MinHeight = 190;
                WindowStartupLocation = WindowStartupLocation.CenterOwner;
                ResizeMode = ResizeMode.NoResize;

                var root = new DockPanel { Margin = new Thickness(18) };
                Content = root;

                var heading = new TextBlock
                {
                    Text = "Save project style preset as",
                    FontSize = 18,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 0, 0, 8)
                };
                DockPanel.SetDock(heading, Dock.Top);
                root.Children.Add(heading);

                var note = new TextBlock
                {
                    Text = "Give this style selection a reusable name. It will be available in other drawings on this workstation.",
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 10)
                };
                DockPanel.SetDock(note, Dock.Top);
                root.Children.Add(note);

                _name = new TextBox
                {
                    Text = suggested ?? string.Empty,
                    MinWidth = 380,
                    Margin = new Thickness(0, 0, 0, 14)
                };
                DockPanel.SetDock(_name, Dock.Top);
                root.Children.Add(_name);

                var buttons = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right
                };
                var cancel = new Button { Content = "Cancel", MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
                cancel.Click += delegate { DialogResult = false; Close(); };
                var save = new Button { Content = "Save", MinWidth = 90, IsDefault = true };
                save.Click += delegate
                {
                    if (string.IsNullOrWhiteSpace(_name.Text)) return;
                    Accepted = true;
                    DialogResult = true;
                    Close();
                };
                buttons.Children.Add(cancel);
                buttons.Children.Add(save);
                DockPanel.SetDock(buttons, Dock.Bottom);
                root.Children.Add(buttons);

                Loaded += delegate
                {
                    _name.Focus();
                    _name.SelectAll();
                };
            }

            public bool Accepted { get; private set; }
            public string PresetName { get { return _name.Text; } }
        }
    }
}

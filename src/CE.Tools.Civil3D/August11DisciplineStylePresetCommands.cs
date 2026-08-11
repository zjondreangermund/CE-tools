using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.August11DisciplineStylePresetCommands))]

namespace CETools.Civil3D
{
    public sealed class August11DisciplineStylePresetCommands
    {
        private static readonly string[] Disciplines =
        {
            "Roads", "Stormwater", "Sewer", "Water", "Platforms", "Bulk Water", "Parking", "Flood"
        };

        [CommandMethod("CE_TOOLS", "CE_DISCIPLINESTYLEPRESETS", CommandFlags.Modal | CommandFlags.Redraw)]
        public void Presets()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Discipline Style Presets",
                "The Civil 3D style catalogue is shared by the project, but each discipline stores its own selected style names. CE_PROJECTSTYLES automatically snapshots Roads/Stormwater/Sewer/Water/Platforms when saved; this window can also copy/apply/review presets explicitly.");
            model.AddChoice("Discipline", "01 Preset", "Discipline", "Roads", "Choose the discipline preset.", Disciplines);
            model.AddChoice("Action", "02 Action", "Action", "Review preset", "Review, activate, or copy the current Project Style Centre selection into the chosen discipline preset.", new[] { "Review preset", "Activate preset", "Copy current Project Style selection to preset" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;
            string discipline = model.Text("Discipline");
            string action = model.Text("Action");
            if (string.Equals(action, "Copy current Project Style selection to preset", StringComparison.OrdinalIgnoreCase))
            {
                ProjectStyleSelection current = ProjectStyleCenterCommands.ReadSelection(document.Database);
                if (!current.Exists)
                {
                    document.Editor.WriteMessage("\nCE_DISCIPLINESTYLEPRESETS: no current Project Style Centre selection exists. Run CE_PROJECTSTYLES first.");
                    return;
                }
                var copy = Clone(current, discipline);
                August11DisciplineStylePresetManager.SavePreset(document.Database, copy);
                document.Editor.WriteMessage("\nSaved {0} project style choices as the {1} discipline preset.", copy.Values.Count, discipline);
            }
            else if (string.Equals(action, "Activate preset", StringComparison.OrdinalIgnoreCase))
            {
                bool activated = August11DisciplineStylePresetManager.Activate(document.Database, discipline);
                document.Editor.WriteMessage(activated ? "\nActivated {0} discipline style preset." : "\nNo stored {0} discipline preset exists yet.", discipline);
                if (activated) CogoPointProjectStyleManager.Queue();
            }
            Show(document, discipline);
        }

        [CommandMethod("CE_TOOLS", "CE_DISCIPLINESTYLEINFO", CommandFlags.Modal | CommandFlags.Redraw)]
        public void Info()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            var rows = new List<IList<string>>();
            foreach (string discipline in Disciplines)
            {
                ProjectStyleSelection preset = August11DisciplineStylePresetManager.ReadPreset(document.Database, discipline);
                rows.Add(new List<string>
                {
                    discipline,
                    preset.Exists ? preset.Values.Count.ToString(CultureInfo.CurrentCulture) : "0",
                    preset.Exists ? Summarize(preset) : "<Not saved yet>"
                });
            }
            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - Discipline Style Presets",
                "All presets reference style names from the same active/project Civil 3D style library; only the selections are stored independently per discipline.",
                new List<string> { "Discipline", "Saved Choices", "Key Styles" },
                rows,
                "CE TOOLS DISCIPLINE STYLE PRESET REGISTER");
        }

        private static void Show(Document document, string discipline)
        {
            ProjectStyleSelection preset = August11DisciplineStylePresetManager.ReadPreset(document.Database, discipline);
            var rows = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("Discipline", discipline),
                new KeyValuePair<string, string>("Preset", preset.Exists ? "Saved" : "Not saved")
            };
            if (preset.Exists)
            {
                foreach (KeyValuePair<string, string> pair in preset.Values.OrderBy(item => item.Key, StringComparer.CurrentCultureIgnoreCase))
                    rows.Add(new KeyValuePair<string, string>(pair.Key, string.IsNullOrWhiteSpace(pair.Value) ? "<Use drawing default>" : pair.Value));
            }
            PopupTablePresenter.ShowReview(
                "CE Tools - " + discipline + " Style Preset",
                "This preset is independent from the other discipline selections but points to the same Civil 3D style names installed in the project drawing.",
                rows,
                "Close");
        }

        private static ProjectStyleSelection Clone(ProjectStyleSelection source, string discipline)
        {
            var result = new ProjectStyleSelection { Exists = true, Discipline = discipline };
            if (source != null)
            {
                foreach (KeyValuePair<string, string> pair in source.Values) result.Values[pair.Key] = pair.Value;
            }
            return result;
        }

        private static string Summarize(ProjectStyleSelection selection)
        {
            if (selection == null || !selection.Exists) return string.Empty;
            string[] preferred = { "Alignment Style", "Profile View Style", "Profile View Band Set Style", "Pipe Style", "Pressure Pipe Style", "Surface Style" };
            var values = new List<string>();
            foreach (string key in preferred)
            {
                string value;
                if (selection.Values.TryGetValue(key, out value) && !string.IsNullOrWhiteSpace(value) && !value.StartsWith("<Use", StringComparison.OrdinalIgnoreCase))
                    values.Add(key.Replace(" Style", string.Empty) + ": " + value);
            }
            return values.Count == 0 ? "Drawing defaults" : string.Join(" | ", values.Take(3));
        }
    }

    internal static class August11DisciplineStylePresetManager
    {
        private const string RootDictionaryName = "CE_TOOLS";
        private const string ActiveRecordName = "PROJECT_STYLE_SELECTION";
        private const string PresetPrefix = "PROJECT_STYLE_PRESET_";
        private const string Schema = "2";

        internal static void SavePreset(Database database, ProjectStyleSelection selection)
        {
            if (database == null || selection == null || string.IsNullOrWhiteSpace(selection.Discipline)) return;
            WriteRecord(database, PresetName(selection.Discipline), selection);
        }

        internal static ProjectStyleSelection ReadPreset(Database database, string discipline)
        {
            return ReadRecord(database, PresetName(discipline), discipline);
        }

        internal static bool Activate(Database database, string discipline)
        {
            if (database == null) return false;
            ProjectStyleSelection preset = ReadPreset(database, discipline);
            if (!preset.Exists) return false;
            WriteRecord(database, ActiveRecordName, preset);
            return true;
        }

        private static void WriteRecord(Database database, string recordName, ProjectStyleSelection selection)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                DBDictionary named = transaction.GetObject(database.NamedObjectsDictionaryId, OpenMode.ForWrite, false) as DBDictionary;
                if (named == null) return;
                DBDictionary root;
                if (named.Contains(RootDictionaryName)) root = transaction.GetObject(named.GetAt(RootDictionaryName), OpenMode.ForWrite, false) as DBDictionary;
                else
                {
                    root = new DBDictionary();
                    named.SetAt(RootDictionaryName, root);
                    transaction.AddNewlyCreatedDBObject(root, true);
                }
                if (root == null) return;
                Xrecord record;
                if (root.Contains(recordName)) record = transaction.GetObject(root.GetAt(recordName), OpenMode.ForWrite, false) as Xrecord;
                else
                {
                    record = new Xrecord();
                    root.SetAt(recordName, record);
                    transaction.AddNewlyCreatedDBObject(record, true);
                }
                if (record == null) return;
                var values = new List<TypedValue>
                {
                    new TypedValue((int)DxfCode.Text, "Schema=" + Schema),
                    new TypedValue((int)DxfCode.Text, "Discipline=" + (selection.Discipline ?? string.Empty))
                };
                foreach (KeyValuePair<string, string> pair in selection.Values.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
                    values.Add(new TypedValue((int)DxfCode.Text, pair.Key + "=" + (pair.Value ?? string.Empty)));
                record.Data = new ResultBuffer(values.ToArray());
                transaction.Commit();
            }
        }

        private static ProjectStyleSelection ReadRecord(Database database, string recordName, string fallbackDiscipline)
        {
            var result = new ProjectStyleSelection { Discipline = fallbackDiscipline ?? "Roads" };
            if (database == null) return result;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                DBDictionary named = transaction.GetObject(database.NamedObjectsDictionaryId, OpenMode.ForRead, false) as DBDictionary;
                if (named == null || !named.Contains(RootDictionaryName)) return result;
                DBDictionary root = transaction.GetObject(named.GetAt(RootDictionaryName), OpenMode.ForRead, false) as DBDictionary;
                if (root == null || !root.Contains(recordName)) return result;
                Xrecord record = transaction.GetObject(root.GetAt(recordName), OpenMode.ForRead, false) as Xrecord;
                if (record == null || record.Data == null) return result;
                foreach (TypedValue typed in record.Data)
                {
                    string text = typed.Value as string;
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    int equals = text.IndexOf('=');
                    if (equals <= 0) continue;
                    string key = text.Substring(0, equals);
                    string value = text.Substring(equals + 1);
                    if (string.Equals(key, "Discipline", StringComparison.OrdinalIgnoreCase)) result.Discipline = value;
                    else if (!string.Equals(key, "Schema", StringComparison.OrdinalIgnoreCase)) result.Values[key] = value;
                }
                result.Exists = true;
            }
            return result;
        }

        private static string PresetName(string discipline)
        {
            string value = string.IsNullOrWhiteSpace(discipline) ? "ROADS" : discipline.Trim().ToUpperInvariant();
            foreach (char c in new[] { ' ', '-', '/', '\\', '.', ':' }) value = value.Replace(c, '_');
            return PresetPrefix + value;
        }
    }
}

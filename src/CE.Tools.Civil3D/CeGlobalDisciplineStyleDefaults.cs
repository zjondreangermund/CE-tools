using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace CETools.Civil3D
{
    /// <summary>
    /// Bridges the newer per-DWG discipline preset records with the established
    /// user-global ProjectStylePresets.ceps file. Style names are portable across
    /// drawings; Civil ObjectIds are deliberately never persisted here.
    /// </summary>
    internal static class CeGlobalDisciplineStyleDefaults
    {
        private const string PresetHeader = "CE_TOOLS_PROJECT_STYLE_PRESET_V1";

        internal static ProjectStyleSelection Read(string discipline)
        {
            string requested = NormalizeDiscipline(discipline);
            Dictionary<string, ProjectStyleSelection> all = Load();
            ProjectStyleSelection result;
            if (all.TryGetValue(requested, out result)) return Clone(result);
            return new ProjectStyleSelection { Discipline = requested };
        }

        internal static bool Save(ProjectStyleSelection selection)
        {
            if (selection == null || string.IsNullOrWhiteSpace(selection.Discipline)) return false;
            try
            {
                Dictionary<string, ProjectStyleSelection> all = Load();
                ProjectStyleSelection copy = Clone(selection);
                copy.Exists = true;
                copy.Discipline = NormalizeDiscipline(selection.Discipline);
                all[copy.Discipline] = copy;
                Write(all);
                return true;
            }
            catch { return false; }
        }

        internal static bool Exists(string discipline)
        {
            return Read(discipline).Exists;
        }

        private static string FilePath
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

        private static Dictionary<string, ProjectStyleSelection> Load()
        {
            var result = new Dictionary<string, ProjectStyleSelection>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(FilePath)) return result;
                string[] lines = File.ReadAllLines(FilePath, Encoding.UTF8);
                if (lines.Length == 0 || !string.Equals(lines[0].Trim(), PresetHeader, StringComparison.Ordinal))
                    return result;

                foreach (string line in lines.Skip(1))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    string[] parts = line.Split(new[] { '|' }, 3);
                    if (parts.Length != 3) continue;
                    string discipline = NormalizeDiscipline(Decode(parts[0]));
                    string key = Decode(parts[1]);
                    string value = Decode(parts[2]);
                    if (string.IsNullOrWhiteSpace(discipline) || string.IsNullOrWhiteSpace(key)) continue;
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
            }
            catch { }
            return result;
        }

        private static void Write(IDictionary<string, ProjectStyleSelection> presets)
        {
            var lines = new List<string> { PresetHeader };
            foreach (KeyValuePair<string, ProjectStyleSelection> pair in presets.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
            {
                foreach (KeyValuePair<string, string> value in pair.Value.Values.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
                {
                    lines.Add(
                        Encode(pair.Key) + "|" +
                        Encode(value.Key) + "|" +
                        Encode(value.Value ?? string.Empty));
                }
            }
            string temporary = FilePath + ".tmp";
            File.WriteAllLines(temporary, lines, Encoding.UTF8);
            if (File.Exists(FilePath)) File.Delete(FilePath);
            File.Move(temporary, FilePath);
        }

        private static string NormalizeDiscipline(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "Roads" : value.Trim();
        }

        private static string Encode(string value)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? string.Empty));
        }

        private static string Decode(string value)
        {
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(value)); }
            catch { return string.Empty; }
        }

        private static ProjectStyleSelection Clone(ProjectStyleSelection source)
        {
            var result = new ProjectStyleSelection
            {
                Exists = source != null && source.Exists,
                Discipline = source == null ? "Roads" : NormalizeDiscipline(source.Discipline)
            };
            if (source != null)
            {
                foreach (KeyValuePair<string, string> pair in source.Values)
                    result.Values[pair.Key] = pair.Value;
            }
            return result;
        }
    }
}

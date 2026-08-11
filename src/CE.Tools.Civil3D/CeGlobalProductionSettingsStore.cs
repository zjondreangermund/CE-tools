using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace CETools.Civil3D
{
    /// <summary>
    /// User-global defaults for every CE Tools ProductionSettingsDialogModel.
    /// The active DWG may still carry its own POPUP_SETTINGS record for project
    /// portability, but the latest explicitly saved user settings are loaded last
    /// so the same discipline settings follow the user into every drawing/session.
    /// </summary>
    internal static class CeGlobalProductionSettingsStore
    {
        private const string Header = "CE_TOOLS_GLOBAL_PRODUCTION_SETTINGS_V1";
        private static readonly object Sync = new object();

        internal static void Load(ProductionSettingsDialogModel model)
        {
            if (model == null) return;
            try
            {
                lock (Sync)
                {
                    Dictionary<string, Dictionary<string, string>> all = ReadAll();
                    Dictionary<string, string> values;
                    if (!all.TryGetValue(NormalizeTitle(model.Title), out values)) return;
                    foreach (ProductionSettingsField field in model.Fields)
                    {
                        string value;
                        if (values.TryGetValue(field.Key ?? string.Empty, out value))
                            field.Value = value ?? string.Empty;
                    }
                }
            }
            catch
            {
                // A damaged user preference file must never block engineering work.
            }
        }

        internal static void Save(ProductionSettingsDialogModel model)
        {
            if (model == null) return;
            try
            {
                lock (Sync)
                {
                    Dictionary<string, Dictionary<string, string>> all = ReadAll();
                    var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (ProductionSettingsField field in model.Fields)
                    {
                        if (field == null || string.IsNullOrWhiteSpace(field.Key)) continue;
                        values[field.Key] = field.Value ?? string.Empty;
                    }
                    all[NormalizeTitle(model.Title)] = values;
                    WriteAll(all);
                }
            }
            catch
            {
                // User-global persistence is helpful, never a reason to fail Save Settings.
            }
        }

        private static string FilePath
        {
            get
            {
                string folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "CE Tools");
                Directory.CreateDirectory(folder);
                return Path.Combine(folder, "ProductionSettings.defaults");
            }
        }

        private static Dictionary<string, Dictionary<string, string>> ReadAll()
        {
            var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(FilePath)) return result;

            string[] lines = File.ReadAllLines(FilePath, Encoding.UTF8);
            if (lines.Length == 0 || !string.Equals(lines[0].Trim(), Header, StringComparison.Ordinal))
                return result;

            foreach (string line in lines.Skip(1))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                string[] parts = line.Split(new[] { '|' }, 3);
                if (parts.Length != 3) continue;
                string title = NormalizeTitle(Decode(parts[0]));
                string key = Decode(parts[1]);
                string value = Decode(parts[2]);
                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(key)) continue;
                Dictionary<string, string> values;
                if (!result.TryGetValue(title, out values))
                {
                    values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    result[title] = values;
                }
                values[key] = value;
            }
            return result;
        }

        private static void WriteAll(IDictionary<string, Dictionary<string, string>> all)
        {
            var lines = new List<string> { Header };
            foreach (KeyValuePair<string, Dictionary<string, string>> model in
                all.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
            {
                foreach (KeyValuePair<string, string> value in
                    model.Value.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
                {
                    lines.Add(
                        Encode(model.Key) + "|" +
                        Encode(value.Key) + "|" +
                        Encode(value.Value ?? string.Empty));
                }
            }

            string temporary = FilePath + ".tmp";
            File.WriteAllLines(temporary, lines, Encoding.UTF8);
            if (File.Exists(FilePath)) File.Delete(FilePath);
            File.Move(temporary, FilePath);
        }

        private static string NormalizeTitle(string value)
        {
            return (value ?? string.Empty).Trim();
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
    }
}

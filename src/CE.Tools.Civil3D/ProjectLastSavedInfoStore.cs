using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace CETools.Civil3D
{
    /// <summary>
    /// Stores the last project-information form outside the DWG so a new drawing
    /// can reuse the previous project setup. Drawing-embedded metadata remains the
    /// authoritative source for the current DWG; this file is only a reusable
    /// starting point for CE_PROJECTSETUPCHOICE.
    /// </summary>
    internal static class ProjectLastSavedInfoStore
    {
        private const string Header = "CE_TOOLS_LAST_PROJECT_INFORMATION_V1";

        internal static bool TryRead(
            out IDictionary<string, string> values,
            out string savedWhen)
        {
            values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            savedWhen = string.Empty;
            foreach (string field in ProjectSetupCommands.FieldOrder)
                values[field] = string.Empty;

            string path = StorePath();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return false;

            try
            {
                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                if (lines.Length == 0 ||
                    !string.Equals(lines[0].Trim(), Header, StringComparison.Ordinal))
                    return false;

                bool foundField = false;
                for (int index = 1; index < lines.Length; index++)
                {
                    string line = lines[index] ?? string.Empty;
                    if (line.StartsWith("SavedUtc\t", StringComparison.Ordinal))
                    {
                        string raw = line.Substring("SavedUtc\t".Length).Trim();
                        DateTime parsed;
                        if (DateTime.TryParse(
                            raw,
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                            out parsed))
                        {
                            savedWhen = parsed.ToLocalTime().ToString(
                                "yyyy-MM-dd HH:mm",
                                CultureInfo.InvariantCulture);
                        }
                        continue;
                    }

                    if (!line.StartsWith("Field\t", StringComparison.Ordinal))
                        continue;

                    string payload = line.Substring("Field\t".Length);
                    int separator = payload.IndexOf('\t');
                    if (separator <= 0) continue;

                    string field = Decode(payload.Substring(0, separator));
                    string value = Decode(payload.Substring(separator + 1));
                    if (string.IsNullOrWhiteSpace(field)) continue;

                    bool known = false;
                    foreach (string expected in ProjectSetupCommands.FieldOrder)
                    {
                        if (!string.Equals(expected, field, StringComparison.OrdinalIgnoreCase))
                            continue;
                        values[expected] = value ?? string.Empty;
                        known = true;
                        break;
                    }
                    if (known) foundField = true;
                }

                return foundField;
            }
            catch
            {
                values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string field in ProjectSetupCommands.FieldOrder)
                    values[field] = string.Empty;
                savedWhen = string.Empty;
                return false;
            }
        }

        internal static bool TryWrite(
            IDictionary<string, string> values,
            out string error)
        {
            error = string.Empty;
            try
            {
                string path = StorePath();
                if (string.IsNullOrWhiteSpace(path))
                    throw new InvalidOperationException("The CE Tools local project-information folder is unavailable.");

                string folder = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(folder))
                    Directory.CreateDirectory(folder);

                var lines = new List<string>
                {
                    Header,
                    "SavedUtc\t" + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
                };

                foreach (string field in ProjectSetupCommands.FieldOrder)
                {
                    string value = string.Empty;
                    if (values != null) values.TryGetValue(field, out value);
                    lines.Add(
                        "Field\t" + Encode(field) + "\t" + Encode(value ?? string.Empty));
                }

                File.WriteAllLines(path, lines.ToArray(), new UTF8Encoding(false));
                return true;
            }
            catch (System.Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }

        internal static bool HasMeaningfulProjectInformation(
            IDictionary<string, string> values)
        {
            if (values == null) return false;
            foreach (string field in ProjectSetupCommands.FieldOrder)
            {
                if (string.Equals(field, "Units", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(field, "Issue Date", StringComparison.OrdinalIgnoreCase))
                    continue;

                string value;
                if (values.TryGetValue(field, out value) && !string.IsNullOrWhiteSpace(value))
                    return true;
            }
            return false;
        }

        private static string StorePath()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(local)) return string.Empty;
            return Path.Combine(local, "CE Tools", "LastProjectInformation.dat");
        }

        private static string Encode(string value)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? string.Empty));
        }

        private static string Decode(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(value.Trim()));
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}

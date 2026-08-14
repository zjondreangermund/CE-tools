using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace CETools.Civil3D
{
    /// <summary>
    /// Reusable company defaults for CE Project Setup. Project-specific fields
    /// remain drawing/project owned; this store only supplies the company's
    /// normal standards/template/units and production responsibility defaults.
    /// </summary>
    internal static class ProjectCompanyStandardInfoStore
    {
        private const string Header = "CE_TOOLS_COMPANY_PROJECT_STANDARD_V1";

        internal static readonly string[] CompanyFields =
        {
            "Company",
            "Country",
            "Standards",
            "Drawing Template",
            "Units",
            "Designed By",
            "Drawn By",
            "Checked By",
            "Approved By"
        };

        internal static bool TryRead(out IDictionary<string, string> values, out string savedWhen)
        {
            values = Empty();
            savedWhen = string.Empty;
            string path = StorePath();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;

            try
            {
                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                if (lines.Length == 0 || !string.Equals(lines[0].Trim(), Header, StringComparison.Ordinal))
                    return false;

                bool found = false;
                for (int index = 1; index < lines.Length; index++)
                {
                    string line = lines[index] ?? string.Empty;
                    if (line.StartsWith("SavedUtc\t", StringComparison.Ordinal))
                    {
                        DateTime parsed;
                        string raw = line.Substring("SavedUtc\t".Length).Trim();
                        if (DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out parsed))
                        {
                            savedWhen = parsed.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                        }
                        continue;
                    }
                    if (!line.StartsWith("Field\t", StringComparison.Ordinal)) continue;
                    string payload = line.Substring("Field\t".Length);
                    int split = payload.IndexOf('\t');
                    if (split <= 0) continue;
                    string field = Decode(payload.Substring(0, split));
                    string value = Decode(payload.Substring(split + 1));
                    foreach (string expected in CompanyFields)
                    {
                        if (!string.Equals(expected, field, StringComparison.OrdinalIgnoreCase)) continue;
                        values[expected] = value ?? string.Empty;
                        found = true;
                        break;
                    }
                }
                return found;
            }
            catch
            {
                values = Empty();
                savedWhen = string.Empty;
                return false;
            }
        }

        internal static bool TryWrite(IDictionary<string, string> source, out string error)
        {
            error = string.Empty;
            try
            {
                string path = StorePath();
                if (string.IsNullOrWhiteSpace(path))
                    throw new InvalidOperationException("The CE Tools company-standard folder is unavailable.");
                string folder = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(folder)) Directory.CreateDirectory(folder);

                var lines = new List<string>
                {
                    Header,
                    "SavedUtc\t" + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
                };
                foreach (string field in CompanyFields)
                {
                    string value = string.Empty;
                    if (source != null) source.TryGetValue(field, out value);
                    lines.Add("Field\t" + Encode(field) + "\t" + Encode(value ?? string.Empty));
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

        internal static IDictionary<string, string> ApplyToBlank(IDictionary<string, string> standard)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string field in ProjectSetupCommands.FieldOrder) result[field] = string.Empty;
            if (standard != null)
            {
                foreach (string field in CompanyFields)
                {
                    string value;
                    if (standard.TryGetValue(field, out value)) result[field] = value ?? string.Empty;
                }
            }
            if (string.IsNullOrWhiteSpace(result["Units"])) result["Units"] = "Metric";
            result["Issue Date"] = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return result;
        }

        private static IDictionary<string, string> Empty()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string field in CompanyFields) result[field] = string.Empty;
            return result;
        }

        private static string StorePath()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(local)) return string.Empty;
            return Path.Combine(local, "CE Tools", "CompanyProjectStandard.dat");
        }

        private static string Encode(string value)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? string.Empty));
        }

        private static string Decode(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(value.Trim())); }
            catch { return string.Empty; }
        }
    }
}

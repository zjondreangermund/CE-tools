using System;
using System.Globalization;

namespace CETools.Civil3D
{
    /// <summary>
    /// Keeps user-facing CE Tools command labels consistent without changing the
    /// registered AutoCAD command names (CE_...).
    /// </summary>
    internal static class CeCommandDisplayNames
    {
        internal static string Prefix(string text)
        {
            string value = (text ?? string.Empty).Trim();
            if (value.Length == 0) return value;
            if (value.StartsWith("CE-", StringComparison.OrdinalIgnoreCase)) return value;

            if (value.StartsWith("CE Tools ", StringComparison.OrdinalIgnoreCase))
                return "CE-Tools " + value.Substring("CE Tools ".Length).TrimStart();
            if (value.StartsWith("CE ", StringComparison.OrdinalIgnoreCase))
                return "CE-" + value.Substring(3).TrimStart();
            if (value.StartsWith("▶ ", StringComparison.Ordinal))
                return "▶ CE-" + value.Substring(2).TrimStart();

            return "CE-" + value;
        }

        internal static string ProductionTitle(string text)
        {
            string value = (text ?? string.Empty).Trim();
            if (value.Length == 0) return value;
            if (value.StartsWith("CE-", StringComparison.OrdinalIgnoreCase))
                value = value.Substring(3).TrimStart();

            bool allUpper = string.Equals(
                value,
                value.ToUpperInvariant(),
                StringComparison.Ordinal);
            if (allUpper)
                value = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.ToLowerInvariant());

            return Prefix(value);
        }
    }
}

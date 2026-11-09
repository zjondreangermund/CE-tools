using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace CETools.Civil3D
{
    /// <summary>
    /// Normalises user-facing CE Tools labels only. Registered AutoCAD command
    /// names and source filenames are deliberately not changed for compatibility.
    /// </summary>
    internal static class CeCommandDisplayNames
    {
        private static readonly Regex DevelopmentDatePrefix = new Regex(
            @"^(January|February|March|April|May|June|July|August|September|October|November|December)(?:\s*\d{1,2})?([\s_-]*)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        internal static string Normalize(string text)
        {
            string value = (text ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            if (value.Length == 0) return value;

            value = DevelopmentDatePrefix.Replace(value, string.Empty, 1).TrimStart();
            if (value.StartsWith("▶ ", StringComparison.Ordinal))
                return "▶ " + Normalize(value.Substring(2));

            if (value.StartsWith("CE-", StringComparison.OrdinalIgnoreCase))
                return "CE-" + value.Substring(3).TrimStart();
            if (value.StartsWith("CE Tools ", StringComparison.OrdinalIgnoreCase))
                value = value.Substring("CE Tools ".Length).TrimStart();
            else if (value.StartsWith("CE ", StringComparison.OrdinalIgnoreCase))
                value = value.Substring(3).TrimStart();

            return "CE-" + value;
        }

        internal static string Prefix(string text) { return Normalize(text); }

        internal static string ProductionTitle(string text)
        {
            string value = Normalize(text);
            if (value.StartsWith("CE-", StringComparison.OrdinalIgnoreCase))
                value = value.Substring(3).TrimStart();

            bool allUpper = string.Equals(value, value.ToUpperInvariant(), StringComparison.Ordinal);
            if (allUpper)
                value = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.ToLowerInvariant());

            return "CE-" + value;
        }
    }
}

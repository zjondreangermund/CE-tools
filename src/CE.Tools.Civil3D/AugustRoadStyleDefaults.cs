using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Autodesk.AutoCAD.DatabaseServices;

namespace CETools.Civil3D
{
    internal static class AugustRoadStyleDefaults
    {
        internal static ObjectId Resolve(
            Database database,
            object collection,
            string configured,
            string category,
            out string actualName)
        {
            actualName = "<Drawing default>";
            IList<object> values = CivilStyleDiscovery.Enumerate(collection);
            if (values.Count == 0)
                throw new InvalidOperationException("A required Civil 3D " + category + " style collection is unavailable or empty.");

            ObjectId first = ObjectId.Null;
            ObjectId best = ObjectId.Null;
            int bestScore = int.MinValue;
            string bestName = string.Empty;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (object item in values)
                {
                    if (!(item is ObjectId)) continue;
                    ObjectId id = (ObjectId)item;
                    if (id.IsNull) continue;
                    if (first.IsNull) first = id;
                    DBObject style;
                    try { style = transaction.GetObject(id, OpenMode.ForRead, false); }
                    catch { continue; }
                    string name = ReadName(style);
                    if (!string.IsNullOrWhiteSpace(configured) &&
                        !configured.StartsWith("<", StringComparison.Ordinal) &&
                        string.Equals(name, configured, StringComparison.OrdinalIgnoreCase))
                    {
                        actualName = name;
                        return id;
                    }
                    int score = Score(name, category);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = id;
                        bestName = name;
                    }
                }
                if (!best.IsNull && bestScore > 0)
                {
                    actualName = bestName;
                    return best;
                }
                if (!first.IsNull)
                {
                    DBObject style = transaction.GetObject(first, OpenMode.ForRead, false);
                    actualName = ReadName(style);
                    return first;
                }
            }
            throw new InvalidOperationException("The required Civil 3D " + category + " style collection is empty.");
        }

        private static int Score(string name, string category)
        {
            string text = (name ?? string.Empty).ToLowerInvariant();
            string type = (category ?? string.Empty).ToLowerInvariant();
            int score = 0;
            if (text.Contains("road")) score += 80;
            if (text.Contains("centre") || text.Contains("center")) score += 20;
            if (text.Contains("station")) score += 15;
            if (text.Contains("major")) score += 8;
            if (text.Contains("minor")) score += 8;
            if (text.Contains("full grid")) score += 30;
            if (text.Contains("single-band") || text.Contains("single band")) score += 25;
            if (text.Contains("design")) score += 10;
            if (text.Contains("profile") && type.Contains("profile")) score += 10;
            if (text.Contains("alignment") && type.Contains("alignment")) score += 10;

            if (text.Contains("pipe")) score -= 120;
            if (text.Contains("sewer")) score -= 120;
            if (text.Contains("storm")) score -= 100;
            if (text.Contains("water")) score -= 100;
            if (text.Contains("sanitary")) score -= 100;
            if (text.Contains("pressure")) score -= 80;
            if (text.Contains("devotech") && text.Contains("pipe")) score -= 150;

            if (string.Equals(category, "Band Set", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(name, AugustRoadProfileDefaults.DefaultBandSet, StringComparison.OrdinalIgnoreCase))
                score += 500;
            return score;
        }

        private static string ReadName(object value)
        {
            if (value == null) return string.Empty;
            try
            {
                PropertyInfo property = value.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
                return property == null ? string.Empty : Convert.ToString(property.GetValue(value, null), CultureInfo.CurrentCulture);
            }
            catch { return string.Empty; }
        }
    }
}

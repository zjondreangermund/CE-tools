using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.DatabaseServices;

namespace CETools.Civil3D
{
    /// <summary>
    /// Reads Civil 3D styles without assuming that every 2023 style collection
    /// implements the same managed IEnumerable surface.  The Named Objects
    /// Dictionary fallback also makes freshly imported styles available without
    /// requiring the drawing to be closed and reopened.
    /// </summary>
    internal static class CivilStyleDiscovery
    {
        internal static IList<object> Enumerate(object collection)
        {
            var values = new List<object>();
            if (collection == null || collection is string) return values;

            IEnumerable enumerable = collection as IEnumerable;
            if (enumerable != null)
            {
                try
                {
                    foreach (object value in enumerable) values.Add(value);
                }
                catch { values.Clear(); }
                if (values.Count > 0) return values;
            }

            foreach (string methodName in new[] { "GetObjectIds", "GetItemIds", "GetIds" })
            {
                try
                {
                    MethodInfo method = collection.GetType().GetMethod(
                        methodName,
                        BindingFlags.Public | BindingFlags.Instance,
                        null,
                        Type.EmptyTypes,
                        null);
                    object result = method == null ? null : method.Invoke(collection, null);
                    IEnumerable ids = result as IEnumerable;
                    if (ids == null) continue;
                    foreach (object value in ids) values.Add(value);
                    if (values.Count > 0) return values;
                }
                catch { values.Clear(); }
            }

            // Several Aecc collection wrappers expose GetEnumerator without
            // declaring System.Collections.IEnumerable on the public type.
            try
            {
                MethodInfo getEnumerator = collection.GetType().GetMethod(
                    "GetEnumerator",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null,
                    Type.EmptyTypes,
                    null);
                IEnumerator iterator = getEnumerator == null
                    ? null
                    : getEnumerator.Invoke(collection, null) as IEnumerator;
                if (iterator != null)
                {
                    while (iterator.MoveNext()) values.Add(iterator.Current);
                    if (values.Count > 0) return values;
                }
            }
            catch { values.Clear(); }

            try
            {
                PropertyInfo countProperty = collection.GetType().GetProperty(
                    "Count",
                    BindingFlags.Public | BindingFlags.Instance);
                int count = countProperty == null
                    ? 0
                    : Convert.ToInt32(countProperty.GetValue(collection, null),
                        CultureInfo.InvariantCulture);
                PropertyInfo indexer = collection.GetType()
                    .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(property =>
                    {
                        ParameterInfo[] parameters = property.GetIndexParameters();
                        return parameters.Length == 1 &&
                            parameters[0].ParameterType == typeof(int) &&
                            property.GetGetMethod() != null;
                    });
                MethodInfo getItem = collection.GetType().GetMethod(
                    "get_Item",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new[] { typeof(int) },
                    null);
                for (int index = 0; index < count; index++)
                {
                    object value = indexer != null
                        ? indexer.GetValue(collection, new object[] { index })
                        : getItem != null
                            ? getItem.Invoke(collection, new object[] { index })
                            : null;
                    if (value != null) values.Add(value);
                }
            }
            catch { values.Clear(); }
            return values;
        }

        internal static IList<string> ReadNames(
            Database database,
            object styleCollection,
            string category)
        {
            var names = new List<string>();
            if (database == null) return names;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (object value in Enumerate(styleCollection))
                    AddStyleName(value, transaction, names);

                if (names.Count == 0)
                {
                    IDictionary<string, List<string>> catalogue =
                        ReadCatalogue(database, transaction);
                    List<string> discovered;
                    if (catalogue.TryGetValue(category ?? string.Empty, out discovered))
                        names.AddRange(discovered);
                }
            }
            return names
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        internal static IDictionary<string, List<string>> ReadCatalogue(Database database)
        {
            if (database == null)
                return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            using (Transaction transaction = database.TransactionManager.StartTransaction())
                return ReadCatalogue(database, transaction);
        }

        private static IDictionary<string, List<string>> ReadCatalogue(
            Database database,
            Transaction transaction)
        {
            var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var visited = new HashSet<ObjectId>();
            ScanDictionary(
                database.NamedObjectsDictionaryId,
                string.Empty,
                0,
                transaction,
                visited,
                result);
            return result;
        }

        private static void ScanDictionary(
            ObjectId dictionaryId,
            string path,
            int depth,
            Transaction transaction,
            ISet<ObjectId> visited,
            IDictionary<string, List<string>> result)
        {
            if (dictionaryId.IsNull || depth > 14 || visited.Contains(dictionaryId)) return;
            visited.Add(dictionaryId);
            DBDictionary dictionary;
            try
            {
                dictionary = transaction.GetObject(
                    dictionaryId,
                    OpenMode.ForRead,
                    false) as DBDictionary;
            }
            catch { return; }
            if (dictionary == null) return;

            foreach (DBDictionaryEntry entry in dictionary)
            {
                string childPath = string.IsNullOrWhiteSpace(path)
                    ? entry.Key
                    : path + "." + entry.Key;
                DBObject value;
                try
                {
                    value = transaction.GetObject(entry.Value, OpenMode.ForRead, false);
                }
                catch { continue; }
                DBDictionary child = value as DBDictionary;
                if (child != null)
                {
                    ScanDictionary(
                        child.ObjectId,
                        childPath,
                        depth + 1,
                        transaction,
                        visited,
                        result);
                    continue;
                }

                string name = Convert.ToString(ReadProperty(value, "Name"),
                    CultureInfo.CurrentCulture);
                if (string.IsNullOrWhiteSpace(name)) continue;
                string category = MapCategory(childPath + "." + value.GetType().Name);
                if (string.IsNullOrWhiteSpace(category)) continue;
                List<string> list;
                if (!result.TryGetValue(category, out list))
                {
                    list = new List<string>();
                    result[category] = list;
                }
                list.Add(name.Trim());
            }
        }

        private static string MapCategory(string source)
        {
            string value = (source ?? string.Empty)
                .Replace("_", string.Empty)
                .Replace(" ", string.Empty)
                .ToUpperInvariant();
            if (value.Contains("PROFILEVIEW") && value.Contains("BAND")) return "Profile View Band Set Style";
            if (value.Contains("PROFILE") && value.Contains("LABELSET")) return "Profile Label Set Style";
            if (value.Contains("ALIGNMENT") && value.Contains("LABELSET")) return "Alignment Label Set Style";
            if (value.Contains("STRUCTURE") && value.Contains("LABEL")) return "Structure Label Style";
            if (value.Contains("PRESSURE") && value.Contains("PIPE") && value.Contains("LABEL")) return "Pressure Pipe Label Style";
            if (value.Contains("PIPE") && value.Contains("LABEL")) return "Pipe Label Style";
            if (value.Contains("POINT") && value.Contains("LABEL")) return "Point Label Style";
            if (value.Contains("PRESSURE") && value.Contains("PIPE")) return "Pressure Pipe Style";
            if (value.Contains("APPURTENANCE")) return "Appurtenance Style";
            if (value.Contains("FITTING")) return "Fitting Style";
            if (value.Contains("STRUCTURE")) return "Structure Style";
            if (value.Contains("PIPE")) return "Pipe Style";
            if (value.Contains("CODESET")) return "Code Set Style";
            if (value.Contains("ASSEMBLY")) return "Assembly Style";
            if (value.Contains("CORRIDOR")) return "Corridor Style";
            if (value.Contains("PROFILEVIEW")) return "Profile View Style";
            if (value.Contains("PROFILE")) return "Profile Style";
            if (value.Contains("ALIGNMENT")) return "Alignment Style";
            if (value.Contains("POINT")) return "Point Style";
            if (value.Contains("SURFACE")) return "Surface Style";
            return string.Empty;
        }

        private static void AddStyleName(
            object value,
            Transaction transaction,
            ICollection<string> names)
        {
            try
            {
                DBObject item = null;
                if (value is ObjectId)
                {
                    ObjectId id = (ObjectId)value;
                    if (id.IsNull || id.IsErased) return;
                    item = transaction.GetObject(id, OpenMode.ForRead, false);
                }
                else item = value as DBObject;
                object source = item ?? value;
                string name = Convert.ToString(ReadProperty(source, "Name"),
                    CultureInfo.CurrentCulture);
                if (!string.IsNullOrWhiteSpace(name)) names.Add(name.Trim());
            }
            catch { }
        }

        private static object ReadProperty(object value, string propertyName)
        {
            if (value == null) return null;
            try
            {
                PropertyInfo property = value.GetType().GetProperty(
                    propertyName,
                    BindingFlags.Public | BindingFlags.Instance);
                return property == null || property.GetIndexParameters().Length != 0
                    ? null
                    : property.GetValue(value, null);
            }
            catch { return null; }
        }
    }
}

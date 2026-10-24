using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;

namespace CETools.Civil3D
{
    internal static class AutoDesignDictionaryEntries
    {
        internal static IList<KeyValuePair<string, ObjectId>> Read(DBDictionary dictionary)
        {
            var entries = new List<KeyValuePair<string, ObjectId>>();
            // Use AutoCAD's typed enumerator. LINQ Cast goes through the
            // non-generic IEnumerable surface and assumes the boxed entry type.
            using (var iterator = dictionary.GetEnumerator())
            {
                while (iterator.MoveNext())
                {
                    var entry = iterator.Entry;
                    entries.Add(new KeyValuePair<string, ObjectId>(entry.Key, entry.Value));
                }
            }
            return entries.OrderBy(entry => entry.Key, StringComparer.Ordinal).ToList();
        }
    }
}

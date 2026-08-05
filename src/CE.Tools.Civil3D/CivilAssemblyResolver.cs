using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;

namespace CETools.Civil3D
{
    /// <summary>
    /// Resolves Civil 3D assemblies across the API shapes exposed by Civil 3D
    /// 2023 and 2024. Some 2023 installations do not expose GetAssemblyIds(),
    /// while others expose an AssemblyCollection that is not directly enumerable.
    /// </summary>
    internal static class CivilAssemblyResolver
    {
        public static IList<ObjectId> GetAssemblyIds(
            CivilDocument civilDocument,
            Database database)
        {
            var result = new List<ObjectId>();
            var seen = new HashSet<ObjectId>();
            if (civilDocument == null || database == null) return result;

            AddFromMethod(civilDocument, "GetAssemblyIds", result, seen);
            AddFromMethod(civilDocument, "GetAssemblies", result, seen);
            AddFromProperty(civilDocument, "AssemblyCollection", result, seen);
            AddFromProperty(civilDocument, "Assemblies", result, seen);

            // A final database scan protects drawings created by older releases,
            // proxy-heavy drawings and Civil 3D 2023 builds where the collection
            // exists but does not reveal its ObjectIds through reflection.
            AddFromDatabase(database, result, seen);

            return result
                .Where(id => !id.IsNull && !id.IsErased)
                .OrderBy(id => id.Handle.Value)
                .ToList();
        }

        private static void AddFromMethod(
            object owner,
            string methodName,
            ICollection<ObjectId> result,
            ISet<ObjectId> seen)
        {
            if (owner == null) return;
            try
            {
                MethodInfo method = owner.GetType().GetMethod(
                    methodName,
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    Type.EmptyTypes,
                    null);
                if (method == null) return;
                AddValue(method.Invoke(owner, null), result, seen, 0);
            }
            catch
            {
                // Continue to the collection and database fallbacks.
            }
        }

        private static void AddFromProperty(
            object owner,
            string propertyName,
            ICollection<ObjectId> result,
            ISet<ObjectId> seen)
        {
            if (owner == null) return;
            try
            {
                PropertyInfo property = owner.GetType().GetProperty(
                    propertyName,
                    BindingFlags.Public | BindingFlags.Instance);
                if (property == null) return;
                AddValue(property.GetValue(owner, null), result, seen, 0);
            }
            catch
            {
                // Continue to the remaining discovery paths.
            }
        }

        private static void AddValue(
            object value,
            ICollection<ObjectId> result,
            ISet<ObjectId> seen,
            int depth)
        {
            if (value == null || depth > 3) return;

            if (value is ObjectId)
            {
                AddId((ObjectId)value, result, seen);
                return;
            }

            DBObject databaseObject = value as DBObject;
            if (databaseObject != null)
            {
                AddId(databaseObject.ObjectId, result, seen);
                return;
            }

            IEnumerable enumerable = value as IEnumerable;
            if (enumerable != null && !(value is string))
            {
                try
                {
                    foreach (object item in enumerable)
                        AddValue(item, result, seen, depth + 1);
                }
                catch
                {
                    // Some Civil collections throw from their enumerator. The
                    // method/property probes below still recover their IDs.
                }
            }

            Type type = value.GetType();
            foreach (string methodName in new[]
            {
                "GetObjectIds",
                "GetItemIds",
                "GetAssemblyIds",
                "ToObjectIdCollection"
            })
            {
                try
                {
                    MethodInfo method = type.GetMethod(
                        methodName,
                        BindingFlags.Public | BindingFlags.Instance,
                        null,
                        Type.EmptyTypes,
                        null);
                    if (method != null)
                        AddValue(method.Invoke(value, null), result, seen, depth + 1);
                }
                catch
                {
                    // Probe the next compatible API shape.
                }
            }

            foreach (string propertyName in new[]
            {
                "ObjectIds",
                "Ids",
                "Items"
            })
            {
                try
                {
                    PropertyInfo property = type.GetProperty(
                        propertyName,
                        BindingFlags.Public | BindingFlags.Instance);
                    if (property != null)
                        AddValue(property.GetValue(value, null), result, seen, depth + 1);
                }
                catch
                {
                    // Probe the next compatible API shape.
                }
            }
        }

        private static void AddFromDatabase(
            Database database,
            ICollection<ObjectId> result,
            ISet<ObjectId> seen)
        {
            try
            {
                using (Transaction transaction =
                    database.TransactionManager.StartTransaction())
                {
                    BlockTable blockTable = transaction.GetObject(
                        database.BlockTableId,
                        OpenMode.ForRead,
                        false) as BlockTable;
                    if (blockTable == null) return;

                    foreach (ObjectId recordId in blockTable)
                    {
                        BlockTableRecord record = transaction.GetObject(
                            recordId,
                            OpenMode.ForRead,
                            false) as BlockTableRecord;
                        if (record == null || record.IsLayout == false &&
                            record.Name.StartsWith("*", StringComparison.Ordinal))
                        {
                            // Model space and layout records are still inspected;
                            // anonymous definition records are skipped.
                            if (record == null || recordId != blockTable[BlockTableRecord.ModelSpace])
                                continue;
                        }

                        foreach (ObjectId id in record)
                        {
                            if (id.IsNull || id.IsErased || seen.Contains(id)) continue;
                            string className = string.Empty;
                            try
                            {
                                className = id.ObjectClass == null
                                    ? string.Empty
                                    : id.ObjectClass.Name;
                            }
                            catch
                            {
                                // Open the object below and inspect its managed type.
                            }

                            bool likelyAssembly =
                                className.IndexOf("Assembly", StringComparison.OrdinalIgnoreCase) >= 0 &&
                                className.IndexOf("Subassembly", StringComparison.OrdinalIgnoreCase) < 0;
                            if (!likelyAssembly)
                            {
                                try
                                {
                                    DBObject value = transaction.GetObject(
                                        id,
                                        OpenMode.ForRead,
                                        false);
                                    string typeName = value == null
                                        ? string.Empty
                                        : value.GetType().Name;
                                    likelyAssembly = string.Equals(
                                        typeName,
                                        "Assembly",
                                        StringComparison.OrdinalIgnoreCase);
                                }
                                catch
                                {
                                    likelyAssembly = false;
                                }
                            }

                            if (likelyAssembly) AddId(id, result, seen);
                        }
                    }
                }
            }
            catch
            {
                // Discovery is best effort. Callers report an empty result and
                // offer assembly creation instead of crashing the command.
            }
        }

        private static void AddId(
            ObjectId id,
            ICollection<ObjectId> result,
            ISet<ObjectId> seen)
        {
            if (id.IsNull || id.IsErased || seen.Contains(id)) return;
            seen.Add(id);
            result.Add(id);
        }
    }
}

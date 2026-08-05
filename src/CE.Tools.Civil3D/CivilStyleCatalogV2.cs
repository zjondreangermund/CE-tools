using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices.Styles;

namespace CETools.Civil3D
{
    /// <summary>
    /// Strict Civil 3D style catalogue used by production dialogs.
    /// Only real StyleBase objects are returned; Aecc display representations,
    /// views and other named database objects are deliberately excluded.
    /// </summary>
    internal static class CivilStyleCatalogV2
    {
        internal const string DrawingDefault = "<Use drawing default>";

        private static readonly IDictionary<string, string[]> KnownPaths =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                { "Alignment Style", new[] { "AlignmentStyles" } },
                { "Alignment Label Set Style", new[] { "LabelSetStyles.AlignmentLabelSetStyles" } },
                { "Alignment Label Style", new[] { "LabelStyles.AlignmentLabelStyles" } },
                { "Profile Style", new[] { "ProfileStyles" } },
                { "Profile Label Set Style", new[] { "LabelSetStyles.ProfileLabelSetStyles" } },
                { "Profile Label Style", new[] { "LabelStyles.ProfileLabelStyles" } },
                { "Profile View Style", new[] { "ProfileViewStyles" } },
                { "Profile View Band Set Style", new[] { "ProfileViewBandSetStyles" } },
                { "Profile View Label Style", new[] { "LabelStyles.ProfileViewLabelStyles" } },
                { "Surface Style", new[] { "SurfaceStyles" } },
                { "Surface Label Style", new[] { "LabelStyles.SurfaceLabelStyles" } },
                { "Point Style", new[] { "PointStyles" } },
                { "Point Label Style", new[] { "LabelStyles.PointLabelStyles" } },
                { "Feature Line Style", new[] { "FeatureLineStyles" } },
                { "Feature Line Label Style", new[] { "LabelStyles.FeatureLineLabelStyles" } },
                { "Parcel Style", new[] { "ParcelStyles" } },
                { "Parcel Label Style", new[] { "LabelStyles.ParcelLabelStyles" } },
                { "Catchment Style", new[] { "CatchmentStyles" } },
                { "Catchment Label Style", new[] { "LabelStyles.CatchmentLabelStyles" } },
                { "Grading Style", new[] { "GradingStyles" } },
                { "Corridor Style", new[] { "CorridorStyles" } },
                { "Code Set Style", new[] { "CodeSetStyles" } },
                { "Assembly Style", new[] { "AssemblyStyles" } },
                { "Pipe Style", new[] { "PipeStyles" } },
                { "Pipe Label Style", new[] {
                    "LabelStyles.PipeLabelStyles.PlanProfileLabelStyles",
                    "LabelStyles.PipeLabelStyles.PlanLabelStyles"
                } },
                { "Structure Style", new[] { "StructureStyles" } },
                { "Structure Label Style", new[] {
                    "LabelStyles.StructureLabelStyles.LabelStyles",
                    "LabelStyles.StructureLabelStyles"
                } },
                { "Pipe Rule Set", new[] { "PipeRuleSetStyles" } },
                { "Structure Rule Set", new[] { "StructureRuleSetStyles" } },
                { "Pressure Pipe Style", new[] {
                    "PressurePipeStyles",
                    "PressureNetworkStyles.PressurePipeStyles"
                } },
                { "Pressure Pipe Label Style", new[] {
                    "LabelStyles.PressurePipeLabelStyles.PlanProfileLabelStyles",
                    "LabelStyles.PressurePipeLabelStyles",
                    "PressureNetworkStyles.PressurePipeLabelStyles"
                } },
                { "Fitting Style", new[] {
                    "FittingStyles",
                    "PressureNetworkStyles.FittingStyles"
                } },
                { "Fitting Label Style", new[] {
                    "LabelStyles.PressureFittingLabelStyles",
                    "LabelStyles.FittingLabelStyles"
                } },
                { "Appurtenance Style", new[] {
                    "AppurtenanceStyles",
                    "PressureNetworkStyles.AppurtenanceStyles"
                } },
                { "Appurtenance Label Style", new[] {
                    "LabelStyles.PressureAppurtenanceLabelStyles",
                    "LabelStyles.AppurtenanceLabelStyles"
                } },
                { "Section Style", new[] { "SectionStyles" } },
                { "Section Label Set Style", new[] { "LabelSetStyles.SectionLabelSetStyles" } },
                { "Section Label Style", new[] { "LabelStyles.SectionLabelStyles" } },
                { "Section View Style", new[] { "SectionViewStyles" } },
                { "Section View Band Set Style", new[] { "SectionViewBandSetStyles" } },
                { "Section View Label Style", new[] { "LabelStyles.SectionViewLabelStyles" } },
                { "Sample Line Style", new[] { "SampleLineStyles" } },
                { "Sample Line Label Style", new[] { "LabelStyles.SampleLineLabelStyles" } },
                { "Mass Haul View Style", new[] { "MassHaulViewStyles" } },
                { "Mass Haul Line Style", new[] { "MassHaulLineStyles" } },
                { "Marker Style", new[] { "MarkerStyles" } },
                { "Alignment Table Style", new[] { "TableStyles.AlignmentTableStyles" } },
                { "Parcel Table Style", new[] { "TableStyles.ParcelTableStyles" } },
                { "Point Table Style", new[] { "TableStyles.PointTableStyles" } },
                { "Pipe Table Style", new[] { "TableStyles.PipeTableStyles" } },
                { "Structure Table Style", new[] { "TableStyles.StructureTableStyles" } },
                { "Surface Table Style", new[] { "TableStyles.SurfaceTableStyles" } }
            };

        internal static Dictionary<string, List<string>> ReadProjectCatalogue(
            Document document,
            IEnumerable<string> selectionKeys)
        {
            var result = new Dictionary<string, List<string>>(
                StringComparer.OrdinalIgnoreCase);
            foreach (string key in selectionKeys ?? Enumerable.Empty<string>())
                result[key] = new List<string> { DrawingDefault };

            if (document == null) return result;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (civilDocument == null) return result;
            object stylesRoot = ReadProperty(civilDocument, "Styles");
            if (stylesRoot == null) return result;

            using (Transaction transaction =
                document.Database.TransactionManager.StartTransaction())
            {
                foreach (KeyValuePair<string, string[]> entry in KnownPaths)
                {
                    List<string> target;
                    if (!result.TryGetValue(entry.Key, out target)) continue;
                    foreach (string path in entry.Value)
                    {
                        object root = ReadPropertyPath(stylesRoot, path);
                        if (root == null) continue;
                        var ids = new HashSet<ObjectId>();
                        CollectStyleIds(
                            root,
                            transaction,
                            0,
                            new HashSet<object>(ReferenceComparer.Instance),
                            ids);
                        foreach (ObjectId id in ids)
                        {
                            StyleBase style = OpenStyle(id, transaction);
                            if (style != null && !string.IsNullOrWhiteSpace(style.Name))
                                target.Add(style.Name.Trim());
                        }
                    }
                }
            }

            // Newly imported styles may be visible in the DWG dictionaries before
            // every Civil 3D 2023 managed collection refreshes. This fallback is
            // safe because CivilStyleDiscovery now accepts StyleBase objects only.
            IDictionary<string, List<string>> databaseCatalogue =
                CivilStyleDiscovery.ReadCatalogue(document.Database);
            foreach (KeyValuePair<string, List<string>> entry in databaseCatalogue)
            {
                List<string> target;
                if (result.TryGetValue(entry.Key, out target))
                    target.AddRange(entry.Value);
            }

            foreach (string key in result.Keys.ToList())
            {
                List<string> ordered = result[key]
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Where(value => !LooksLikeRuntimeClassName(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
                ordered.RemoveAll(value => string.Equals(
                    value, DrawingDefault, StringComparison.OrdinalIgnoreCase));
                ordered.Insert(0, DrawingDefault);
                result[key] = ordered;
            }
            return result;
        }

        internal static IList<string> ReadNames(
            Database database,
            object collection)
        {
            return ReadNames(database, collection, string.Empty);
        }

        internal static IList<string> ReadNames(
            Database database,
            object collection,
            string category)
        {
            var names = new List<string>();
            if (database == null) return names;
            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ReadObjectIds(collection, transaction))
                {
                    StyleBase style = OpenStyle(id, transaction);
                    if (style != null &&
                        !string.IsNullOrWhiteSpace(style.Name) &&
                        !LooksLikeRuntimeClassName(style.Name))
                    {
                        names.Add(style.Name.Trim());
                    }
                }
            }

            if (names.Count == 0 && !string.IsNullOrWhiteSpace(category))
            {
                IDictionary<string, List<string>> fallback =
                    CivilStyleDiscovery.ReadCatalogue(database);
                List<string> values;
                if (fallback.TryGetValue(category, out values))
                    names.AddRange(values);
            }

            return names
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Where(value => !LooksLikeRuntimeClassName(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        internal static IList<ObjectId> ReadObjectIds(
            object collection,
            Transaction transaction)
        {
            var ids = new HashSet<ObjectId>();
            CollectStyleIds(
                collection,
                transaction,
                0,
                new HashSet<object>(ReferenceComparer.Instance),
                ids);
            return ids
                .Where(id => !id.IsNull && !id.IsErased)
                .OrderBy(id => id.Handle.Value)
                .ToList();
        }

        internal static IList<object> Enumerate(object collection)
        {
            var result = new List<object>();
            if (collection == null || collection is string) return result;

            IEnumerable enumerable = collection as IEnumerable;
            if (enumerable != null)
            {
                try
                {
                    foreach (object item in enumerable) result.Add(item);
                }
                catch
                {
                    result.Clear();
                }
                if (result.Count > 0) return result;
            }

            foreach (string methodName in new[]
            {
                "GetObjectIds", "GetItemIds", "GetIds"
            })
            {
                try
                {
                    MethodInfo method = collection.GetType().GetMethod(
                        methodName,
                        BindingFlags.Public | BindingFlags.Instance,
                        null,
                        Type.EmptyTypes,
                        null);
                    object value = method == null
                        ? null
                        : method.Invoke(collection, null);
                    IEnumerable values = value as IEnumerable;
                    if (values == null) continue;
                    foreach (object item in values) result.Add(item);
                    if (result.Count > 0) return result;
                }
                catch
                {
                    result.Clear();
                }
            }

            try
            {
                MethodInfo getEnumerator = collection.GetType().GetMethod(
                    "GetEnumerator",
                    BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.Instance,
                    null,
                    Type.EmptyTypes,
                    null);
                IEnumerator iterator = getEnumerator == null
                    ? null
                    : getEnumerator.Invoke(collection, null) as IEnumerator;
                if (iterator != null)
                {
                    while (iterator.MoveNext()) result.Add(iterator.Current);
                    if (result.Count > 0) return result;
                }
            }
            catch
            {
                result.Clear();
            }

            try
            {
                PropertyInfo countProperty = collection.GetType().GetProperty(
                    "Count",
                    BindingFlags.Public | BindingFlags.Instance);
                int count = countProperty == null
                    ? 0
                    : Convert.ToInt32(
                        countProperty.GetValue(collection, null),
                        CultureInfo.InvariantCulture);
                PropertyInfo indexer = collection.GetType()
                    .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(property =>
                    {
                        ParameterInfo[] parameters =
                            property.GetIndexParameters();
                        return parameters.Length == 1 &&
                            property.GetGetMethod() != null &&
                            IsIntegral(parameters[0].ParameterType);
                    });
                MethodInfo getItem = collection.GetType()
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(method =>
                    {
                        if (!string.Equals(
                                method.Name,
                                "get_Item",
                                StringComparison.Ordinal))
                            return false;
                        ParameterInfo[] parameters = method.GetParameters();
                        return parameters.Length == 1 &&
                            IsIntegral(parameters[0].ParameterType);
                    });

                for (int index = 0; index < count; index++)
                {
                    object item = null;
                    if (indexer != null)
                    {
                        Type indexType =
                            indexer.GetIndexParameters()[0].ParameterType;
                        item = indexer.GetValue(
                            collection,
                            new[]
                            {
                                Convert.ChangeType(
                                    index,
                                    indexType,
                                    CultureInfo.InvariantCulture)
                            });
                    }
                    else if (getItem != null)
                    {
                        Type indexType =
                            getItem.GetParameters()[0].ParameterType;
                        item = getItem.Invoke(
                            collection,
                            new[]
                            {
                                Convert.ChangeType(
                                    index,
                                    indexType,
                                    CultureInfo.InvariantCulture)
                            });
                    }
                    if (item != null) result.Add(item);
                }
            }
            catch
            {
                result.Clear();
            }
            return result;
        }

        private static void CollectStyleIds(
            object value,
            Transaction transaction,
            int depth,
            ISet<object> visited,
            ISet<ObjectId> result)
        {
            if (value == null || value is string || depth > 8) return;

            ObjectId directId = ObjectId.Null;
            if (value is ObjectId)
                directId = (ObjectId)value;
            else
            {
                DBObject databaseObject = value as DBObject;
                if (databaseObject != null) directId = databaseObject.ObjectId;
            }

            if (!directId.IsNull)
            {
                StyleBase style = OpenStyle(directId, transaction);
                if (style != null) result.Add(directId);
                return;
            }

            Type type = value.GetType();
            if (type.IsPrimitive || type.IsEnum || value is decimal)
                return;
            if (visited.Contains(value)) return;
            visited.Add(value);

            IList<object> items = Enumerate(value);
            foreach (object item in items)
                CollectStyleIds(item, transaction, depth + 1, visited, result);
            if (items.Count > 0) return;

            PropertyInfo[] properties;
            try
            {
                properties = type.GetProperties(
                    BindingFlags.Public | BindingFlags.Instance);
            }
            catch
            {
                return;
            }

            foreach (PropertyInfo property in properties)
            {
                if (!property.CanRead ||
                    property.GetIndexParameters().Length != 0 ||
                    !ShouldTraverse(property.Name, depth))
                    continue;
                object child;
                try
                {
                    child = property.GetValue(value, null);
                }
                catch
                {
                    continue;
                }
                CollectStyleIds(
                    child,
                    transaction,
                    depth + 1,
                    visited,
                    result);
            }
        }

        private static StyleBase OpenStyle(
            ObjectId id,
            Transaction transaction)
        {
            if (id.IsNull || id.IsErased || transaction == null) return null;
            try
            {
                return transaction.GetObject(
                    id,
                    OpenMode.ForRead,
                    false) as StyleBase;
            }
            catch
            {
                return null;
            }
        }

        private static object ReadPropertyPath(object value, string path)
        {
            object current = value;
            foreach (string part in (path ?? string.Empty).Split('.'))
            {
                current = ReadProperty(current, part);
                if (current == null) return null;
            }
            return current;
        }

        private static object ReadProperty(object value, string propertyName)
        {
            if (value == null) return null;
            try
            {
                PropertyInfo property = value.GetType().GetProperty(
                    propertyName,
                    BindingFlags.Public | BindingFlags.Instance);
                return property == null ||
                    property.GetIndexParameters().Length != 0
                    ? null
                    : property.GetValue(value, null);
            }
            catch
            {
                return null;
            }
        }

        private static bool ShouldTraverse(string propertyName, int depth)
        {
            if (depth == 0) return true;
            string name = (propertyName ?? string.Empty).ToUpperInvariant();
            return name.Contains("STYLE") ||
                name.Contains("LABEL") ||
                name.Contains("BAND") ||
                name.Contains("RULE") ||
                name.Contains("TABLE") ||
                name.Contains("ITEM");
        }

        private static bool IsIntegral(Type type)
        {
            Type value = Nullable.GetUnderlyingType(type) ?? type;
            return value == typeof(byte) ||
                value == typeof(sbyte) ||
                value == typeof(short) ||
                value == typeof(ushort) ||
                value == typeof(int) ||
                value == typeof(uint) ||
                value == typeof(long) ||
                value == typeof(ulong);
        }

        private static bool LooksLikeRuntimeClassName(string value)
        {
            string text = (value ?? string.Empty).Trim();
            return text.StartsWith("AeccDb", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("AecDb", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("AcDb", StringComparison.OrdinalIgnoreCase);
        }

        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            internal static readonly ReferenceComparer Instance =
                new ReferenceComparer();

            public new bool Equals(object left, object right)
            {
                return ReferenceEquals(left, right);
            }

            public int GetHashCode(object value)
            {
                return RuntimeHelpers.GetHashCode(value);
            }
        }
    }
}

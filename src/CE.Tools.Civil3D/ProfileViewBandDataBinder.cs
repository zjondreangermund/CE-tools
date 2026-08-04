using System;
using System.Collections.Generic;
using System.Reflection;
using Autodesk.AutoCAD.DatabaseServices;

namespace CETools.Civil3D
{
    /// <summary>
    /// Civil 3D creates the band rows from a band-set style but does not always
    /// assign their profile/network data sources. Populate those sources through
    /// the 2023/2024-compatible reflected band-item API.
    /// </summary>
    internal static class ProfileViewBandDataBinder
    {
        internal static int Bind(
            DBObject profileView,
            ObjectId surfaceProfileId,
            ObjectId designProfileId,
            ObjectId networkId)
        {
            if (profileView == null) return 0;
            object bands = ReadProperty(profileView, "Bands");
            if (bands == null) bands = ReadProperty(profileView, "BandItems");
            if (bands == null) return 0;

            int updated = 0;
            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
            foreach (string methodName in new[]
            {
                "GetBottomBandItems",
                "GetTopBandItems",
                "GetBandItems"
            })
            {
                object collection = InvokeNoArguments(bands, methodName);
                foreach (object item in CivilStyleDiscovery.Enumerate(collection))
                {
                    if (item == null || visited.Contains(item)) continue;
                    visited.Add(item);
                    if (AssignSources(
                            item,
                            surfaceProfileId,
                            designProfileId,
                            networkId))
                        updated++;
                }
            }

            // A few builds expose the band collection itself as the enumerable.
            foreach (object item in CivilStyleDiscovery.Enumerate(bands))
            {
                if (item == null || visited.Contains(item)) continue;
                visited.Add(item);
                if (AssignSources(item, surfaceProfileId, designProfileId, networkId))
                    updated++;
            }
            return updated;
        }

        private static bool AssignSources(
            object item,
            ObjectId surfaceProfileId,
            ObjectId designProfileId,
            ObjectId networkId)
        {
            bool changed = false;
            string identity = (item.GetType().Name + " " +
                Convert.ToString(ReadProperty(item, "BandType")) + " " +
                Convert.ToString(ReadProperty(item, "Name")) + " " +
                Convert.ToString(ReadProperty(item, "StyleName"))).ToUpperInvariant();
            bool networkBand = identity.Contains("PIPE") ||
                               identity.Contains("NETWORK") ||
                               identity.Contains("PRESSURE");
            foreach (PropertyInfo property in item.GetType().GetProperties(
                BindingFlags.Public | BindingFlags.Instance))
            {
                if (!property.CanWrite || property.PropertyType != typeof(ObjectId) ||
                    property.GetIndexParameters().Length != 0)
                    continue;
                string name = (property.Name ?? string.Empty).ToUpperInvariant();
                ObjectId source = ObjectId.Null;
                if (name.Contains("PROFILE2") || name.Contains("SECONDARY"))
                    source = designProfileId.IsNull ? surfaceProfileId : designProfileId;
                else if (name.Contains("DATASOURCE") && networkBand && !networkId.IsNull)
                    source = networkId;
                else if (name.Contains("PROFILE1") || name.Contains("PROFILE") ||
                         name.Contains("DATASOURCE"))
                    source = surfaceProfileId;
                else if (name.Contains("NETWORK"))
                    source = networkId;
                if (source.IsNull) continue;
                try
                {
                    property.SetValue(item, source, null);
                    changed = true;
                }
                catch { }
            }

            foreach (MethodInfo method in item.GetType().GetMethods(
                BindingFlags.Public | BindingFlags.Instance))
            {
                if (method.Name.IndexOf("DataSource", StringComparison.OrdinalIgnoreCase) < 0 &&
                    method.Name.IndexOf("Profile", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length != 1 || parameters[0].ParameterType != typeof(ObjectId))
                    continue;
                string name = (method.Name + parameters[0].Name).ToUpperInvariant();
                ObjectId source = name.Contains("2")
                    ? (designProfileId.IsNull ? surfaceProfileId : designProfileId)
                    : surfaceProfileId;
                if (name.Contains("NETWORK") ||
                    (name.Contains("DATASOURCE") && networkBand))
                    source = networkId;
                if (source.IsNull) continue;
                try
                {
                    method.Invoke(item, new object[] { source });
                    changed = true;
                }
                catch { }
            }
            return changed;
        }

        private static object ReadProperty(object value, string name)
        {
            if (value == null) return null;
            try
            {
                PropertyInfo property = value.GetType().GetProperty(
                    name,
                    BindingFlags.Public | BindingFlags.Instance);
                return property == null ? null : property.GetValue(value, null);
            }
            catch { return null; }
        }

        private static object InvokeNoArguments(object value, string name)
        {
            if (value == null) return null;
            try
            {
                MethodInfo method = value.GetType().GetMethod(
                    name,
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    Type.EmptyTypes,
                    null);
                return method == null ? null : method.Invoke(value, null);
            }
            catch { return null; }
        }

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            internal static readonly ReferenceEqualityComparer Instance =
                new ReferenceEqualityComparer();

            public new bool Equals(object x, object y)
            {
                return ReferenceEquals(x, y);
            }

            public int GetHashCode(object value)
            {
                return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value);
            }
        }
    }
}

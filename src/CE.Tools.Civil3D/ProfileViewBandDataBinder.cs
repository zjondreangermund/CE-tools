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

            ObjectId sewerProfileId = designProfileId.IsNull
                ? surfaceProfileId
                : designProfileId;
            return BindInternal(profileView, surfaceProfileId, sewerProfileId,
                sewerProfileId, sewerProfileId, surfaceProfileId, networkId, false);
        }

        internal static int BindRoad(
            DBObject profileView,
            ObjectId leftProfileId,
            ObjectId centreProfileId,
            ObjectId rightProfileId,
            ObjectId finalDesignProfileId)
        {
            return BindRoad(
                profileView,
                ObjectId.Null,
                leftProfileId,
                centreProfileId,
                rightProfileId,
                finalDesignProfileId);
        }

        internal static int BindRoad(
            DBObject profileView,
            ObjectId groundProfileId,
            ObjectId leftProfileId,
            ObjectId centreProfileId,
            ObjectId rightProfileId,
            ObjectId finalDesignProfileId)
        {
            return BindInternal(profileView, leftProfileId, centreProfileId,
                rightProfileId, finalDesignProfileId, groundProfileId,
                ObjectId.Null, true);
        }

        internal static bool HasRoadBandRoles(
            DBObject profileView,
            string alignmentName)
        {
            if (profileView == null) return false;

            string viewName = Convert.ToString(ReadProperty(profileView, "Name")) ?? string.Empty;
            if (LooksLikeRoadName(viewName) || LooksLikeRoadName(alignmentName))
                return true;

            object bands = ReadProperty(profileView, "Bands");
            if (bands == null) bands = ReadProperty(profileView, "BandItems");
            if (bands == null) return false;

            bool hasRoad = false;
            bool hasLeft = false;
            bool hasRight = false;
            bool hasCentre = false;
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
                    AccumulateRoadBandIdentity(item, visited,
                        ref hasRoad, ref hasLeft, ref hasRight, ref hasCentre);
            }
            foreach (object item in CivilStyleDiscovery.Enumerate(bands))
                AccumulateRoadBandIdentity(item, visited,
                    ref hasRoad, ref hasLeft, ref hasRight, ref hasCentre);

            return hasRoad || (hasLeft && hasRight && hasCentre);
        }

        private static void AccumulateRoadBandIdentity(
            object item,
            HashSet<object> visited,
            ref bool hasRoad,
            ref bool hasLeft,
            ref bool hasRight,
            ref bool hasCentre)
        {
            if (item == null || visited.Contains(item)) return;
            visited.Add(item);
            string identity = GetBandIdentity(item);
            hasRoad = hasRoad || identity.Contains("ROAD");
            hasLeft = hasLeft || identity.Contains("LEFT") || identity.Contains("LHS");
            hasRight = hasRight || identity.Contains("RIGHT") || identity.Contains("RHS");
            hasCentre = hasCentre || identity.Contains("CENTRE") ||
                        identity.Contains("CENTER") || identity.Contains("C/L");
        }

        private static bool LooksLikeRoadName(string value)
        {
            string name = (value ?? string.Empty).Trim().ToUpperInvariant();
            return name.Contains("ROAD") ||
                   name.StartsWith("RD-") ||
                   name.StartsWith("RD_") ||
                   name.StartsWith("RD ");
        }

        private static string GetBandIdentity(object item)
        {
            return (item.GetType().Name + " " +
                Convert.ToString(ReadProperty(item, "BandType")) + " " +
                Convert.ToString(ReadProperty(item, "Name")) + " " +
                Convert.ToString(ReadProperty(item, "StyleName")) + " " +
                ReadBandStyleName(item)).ToUpperInvariant();
        }

        private static int BindInternal(
            DBObject profileView,
            ObjectId leftProfileId,
            ObjectId centreProfileId,
            ObjectId rightProfileId,
            ObjectId finalDesignProfileId,
            ObjectId groundProfileId,
            ObjectId networkId,
            bool roadRoles)
        {
            if (profileView == null) return 0;
            object bands = ReadProperty(profileView, "Bands");
            if (bands == null) bands = ReadProperty(profileView, "BandItems");
            if (bands == null) return 0;

            // Labels can be disabled at both the profile-view and band-item
            // levels. Rebinding only the data sources leaves the rows empty in
            // Civil 3D 2023 even though the band set appears in Properties.
            SetBooleanIfAvailable(profileView, true,
                "ShowLabels", "DisplayLabels", "LabelsVisible",
                "ShowBandLabels", "BandLabelsVisible");

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
                    if (AssignSources(item, groundProfileId, leftProfileId,
                            centreProfileId, rightProfileId, finalDesignProfileId,
                            networkId, roadRoles))
                        updated++;
                }
                // Civil 3D 2023 can return a read-only wrapper for the band
                // collection immediately after a profile view is created. Do not
                // call SetTop/SetBottom or a collection-level refresh here; those
                // native writes were the eNotOpenForWrite abort path in
                // CE_SEWPROFILE. Individual band-item properties are committed by
                // the owning ProfileView transaction below.
            }

            // A few builds expose the band collection itself as the enumerable.
            foreach (object item in CivilStyleDiscovery.Enumerate(bands))
            {
                if (item == null || visited.Contains(item)) continue;
                visited.Add(item);
                if (AssignSources(item, groundProfileId, leftProfileId,
                        centreProfileId, rightProfileId, finalDesignProfileId,
                        networkId, roadRoles))
                    updated++;
            }
            // Do not invoke Update/Rebuild/Refresh on the band collection.
            // Those methods reopen Civil 3D's internal collection and can abort
            // with !dbobji.cpp@8671:eNotOpenForWrite in Civil 3D 2023.
            try
            {
                Entity entity = profileView as Entity;
                if (entity != null) entity.RecordGraphicsModified(true);
            }
            catch { }
            return updated;
        }

        private static bool AssignSources(
            object item,
            ObjectId groundProfileId,
            ObjectId leftProfileId,
            ObjectId centreProfileId,
            ObjectId rightProfileId,
            ObjectId finalDesignProfileId,
            ObjectId networkId,
            bool roadRoles)
        {
            bool changed = false;
            string identity = GetBandIdentity(item);
            bool networkBand = identity.Contains("PIPE") ||
                               identity.Contains("NETWORK") ||
                               identity.Contains("PRESSURE") ||
                               identity.Contains("STRUCTURE") ||
                               identity.Contains("MANHOLE");
            ObjectId primaryProfileId = centreProfileId;
            ObjectId secondaryProfileId = finalDesignProfileId.IsNull
                ? centreProfileId
                : finalDesignProfileId;
            if (roadRoles)
            {
                bool groundBand =
                    identity.Contains("GROUND") ||
                    identity.Contains("NATURAL") ||
                    identity.Contains("NGL") ||
                    identity.Contains("EXIST") ||
                    identity.Contains("SURFACE") ||
                    identity.Contains("-EG") ||
                    identity.Contains(" EG ") ||
                    identity.EndsWith(" EG", StringComparison.Ordinal);
                if (groundBand && !groundProfileId.IsNull)
                {
                    primaryProfileId = groundProfileId;
                    secondaryProfileId = groundProfileId;
                }
                bool leftBand =
                    identity.Contains("LEFT") ||
                    identity.Contains("LHS") ||
                    identity.Contains(" HL ") ||
                    identity.Contains("HL") ||
                    identity.EndsWith(" HL", StringComparison.Ordinal);
                bool rightBand =
                    identity.Contains("RIGHT") ||
                    identity.Contains("RHS") ||
                    identity.Contains(" HR ") ||
                    identity.Contains("HR") ||
                    identity.EndsWith(" HR", StringComparison.Ordinal);
                bool centreBand =
                    identity.Contains("CENTRE") ||
                    identity.Contains("CENTER") ||
                    identity.Contains("CENTRELINE") ||
                    identity.Contains("CENTERLINE");
                bool verticalBand =
                    identity.Contains("VERTICAL") ||
                    identity.Contains("CREST") ||
                    identity.Contains("SAG") ||
                    identity.Contains("GRADE BREAK") ||
                    identity.Contains("GRADEBREAK");
                bool horizontalBand =
                    identity.Contains("HORIZONTAL");

                if (leftBand && !leftProfileId.IsNull)
                {
                    primaryProfileId = leftProfileId;
                    secondaryProfileId = leftProfileId;
                }
                else if (rightBand && !rightProfileId.IsNull)
                {
                    primaryProfileId = rightProfileId;
                    secondaryProfileId = rightProfileId;
                }
                else if (centreBand && !centreProfileId.IsNull)
                {
                    primaryProfileId = centreProfileId;
                    secondaryProfileId = centreProfileId;
                }
                else if (verticalBand && !finalDesignProfileId.IsNull)
                {
                    primaryProfileId = finalDesignProfileId;
                    secondaryProfileId = finalDesignProfileId;
                }

                // Horizontal-curve rows belong to alignment geometry. Do not
                // accidentally overwrite them with the final road profile merely
                // because their style name contains the word "Curve".
                if (verticalBand && !horizontalBand)
                    secondaryProfileId = finalDesignProfileId.IsNull
                        ? primaryProfileId
                        : finalDesignProfileId;
            }

            changed = SetBooleanIfAvailable(item, true,
                "ShowLabels",
                "DisplayLabels",
                "LabelsVisible",
                "ShowBandLabels",
                "BandLabelsVisible",
                "Visible",
                "IsVisible") || changed;
            changed = InvokeBooleanSetter(item, true,
                "SetShowLabels",
                "SetDisplayLabels",
                "SetLabelsVisible",
                "SetBandLabelsVisible",
                "SetVisible") || changed;
            foreach (PropertyInfo property in item.GetType().GetProperties(
                BindingFlags.Public | BindingFlags.Instance))
            {
                if (!property.CanWrite || property.PropertyType != typeof(ObjectId) ||
                    property.GetIndexParameters().Length != 0)
                    continue;
                string name = (property.Name ?? string.Empty).ToUpperInvariant();
                ObjectId source = ObjectId.Null;
                if (name.Contains("PROFILE2") || name.Contains("SECONDARY"))
                    source = secondaryProfileId;
                else if (name.Contains("DATASOURCE") && networkBand && !networkId.IsNull)
                    source = networkId;
                else if (name.Contains("PROFILE1") || name.Contains("PROFILE") ||
                         name.Contains("DATASOURCE"))
                    source = primaryProfileId;
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
                    ? secondaryProfileId
                    : primaryProfileId;
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

        private static bool InvokeBooleanSetter(
            object target,
            bool value,
            params string[] names)
        {
            if (target == null || names == null) return false;
            bool changed = false;
            foreach (string name in names)
            {
                foreach (MethodInfo method in target.GetType().GetMethods(
                    BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!string.Equals(method.Name, name, StringComparison.OrdinalIgnoreCase))
                        continue;
                    ParameterInfo[] parameters = method.GetParameters();
                    if (parameters.Length != 1 || parameters[0].ParameterType != typeof(bool))
                        continue;
                    try
                    {
                        method.Invoke(target, new object[] { value });
                        changed = true;
                    }
                    catch { }
                }
            }
            return changed;
        }

        private static bool SetBooleanIfAvailable(
            object target,
            bool value,
            params string[] names)
        {
            if (target == null || names == null) return false;
            bool changed = false;
            foreach (string name in names)
            {
                try
                {
                    PropertyInfo property = target.GetType().GetProperty(
                        name,
                        BindingFlags.Public | BindingFlags.Instance);
                    if (property == null ||
                        !property.CanWrite ||
                        property.PropertyType != typeof(bool))
                        continue;
                    property.SetValue(target, value, null);
                    changed = true;
                }
                catch { }
            }
            return changed;
        }

        private static void CommitCollection(object bands, string getterName, object collection)
        {
            if (bands == null || collection == null || string.IsNullOrWhiteSpace(getterName)) return;
            string setterName = getterName.StartsWith("Get", StringComparison.Ordinal)
                ? "Set" + getterName.Substring(3)
                : string.Empty;
            if (setterName.Length == 0) return;
            foreach (MethodInfo method in bands.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!string.Equals(method.Name, setterName, StringComparison.Ordinal) ||
                    method.GetParameters().Length != 1) continue;
                try { method.Invoke(bands, new[] { collection }); return; } catch { }
            }
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

        private static string ReadBandStyleName(object item)
        {
            ObjectId id = ObjectId.Null;
            foreach (string name in new[] { "BandStyleId", "StyleId" })
            {
                object value = ReadProperty(item, name);
                if (value is ObjectId && !((ObjectId)value).IsNull)
                { id = (ObjectId)value; break; }
            }
            if (id.IsNull || id.Database == null) return string.Empty;
            try
            {
                Transaction transaction = id.Database.TransactionManager.TopTransaction;
                if (transaction == null) return string.Empty;
                DBObject style = transaction.GetObject(id, OpenMode.ForRead, false);
                return Convert.ToString(ReadProperty(style, "Name"));
            }
            catch { return string.Empty; }
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

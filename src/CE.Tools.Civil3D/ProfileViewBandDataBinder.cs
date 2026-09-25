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
        internal static int CountProfileDataBandLabelSubentities(
            Autodesk.Civil.DatabaseServices.ProfileView profileView,
            Transaction transaction)
        {
            if (profileView == null || transaction == null) return 0;
            int count = 0;
            try
            {
                ObjectIdCollection groups =
                    Autodesk.Civil.DatabaseServices.ProfileDataBandLabelGroup.GetAvailableLabelGroupIds(
                        profileView.ObjectId);
                if (groups == null) return 0;
                for (int index = 0; index < groups.Count; index++)
                {
                    try
                    {
                        Autodesk.Civil.DatabaseServices.ProfileDataBandLabelGroup group = transaction.GetObject(
                            groups[index], OpenMode.ForRead, false)
                            as Autodesk.Civil.DatabaseServices.ProfileDataBandLabelGroup;
                        if (group != null) count += (int)group.SubEntityCount;
                    }
                    catch { }
                }
            }
            catch { }
            return count;
        }

        internal static int CountBandStylesWithoutLabelComponents(
            Autodesk.Civil.DatabaseServices.ProfileView profileView,
            Transaction transaction)
        {
            if (profileView == null || transaction == null) return 0;
            int missing = 0;
            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
            object bands = ReadProperty(profileView, "Bands");
            if (bands == null) bands = ReadProperty(profileView, "BandItems");
            if (bands == null) return 0;
            foreach (string methodName in new[] { "GetTopBandItems", "GetBottomBandItems" })
            {
                object collection = InvokeNoArguments(bands, methodName);
                foreach (object item in CivilStyleDiscovery.Enumerate(collection))
                {
                    if (item == null || visited.Contains(item)) continue;
                    visited.Add(item);
                    ObjectId styleId = ReadBandStyleId(item);
                    if (styleId.IsNull) continue;
                    bool hasLabelComponent = false;
                    try
                    {
                        DBObject style = transaction.GetObject(
                            styleId, OpenMode.ForRead, false);
                        if (style == null) continue;
                        foreach (PropertyInfo property in style.GetType().GetProperties(
                            BindingFlags.Public | BindingFlags.Instance))
                        {
                            if (property.Name.IndexOf("LabelStyleId",
                                    StringComparison.OrdinalIgnoreCase) < 0 ||
                                property.PropertyType != typeof(ObjectId) ||
                                property.GetIndexParameters().Length != 0)
                                continue;
                            try
                            {
                                object value = property.GetValue(style, null);
                                if (value is ObjectId && !((ObjectId)value).IsNull)
                                {
                                    hasLabelComponent = true;
                                    break;
                                }
                            }
                            catch { }
                        }
                    }
                    catch { continue; }
                    if (!hasLabelComponent) missing++;
                }
            }
            return missing;
        }

        internal static int CountRoadSourceProfilesAlreadyInView(
            DBObject profileView,
            params ObjectId[] profileIds)
        {
            if (profileView == null || profileIds == null || profileIds.Length == 0)
                return 0;
            object graphOverrides = ReadProperty(profileView, "GraphOverrides");
            if (graphOverrides == null) return 0;
            var displayed = new HashSet<ObjectId>();
            foreach (object item in CivilStyleDiscovery.Enumerate(graphOverrides))
            {
                object value = ReadProperty(item, "ProfileId");
                if (value is ObjectId && !((ObjectId)value).IsNull)
                    displayed.Add((ObjectId)value);
            }
            int count = 0;
            var seen = new HashSet<ObjectId>();
            foreach (ObjectId id in profileIds)
            {
                if (id.IsNull || !seen.Add(id)) continue;
                if (displayed.Contains(id)) count++;
            }
            return count;
        }

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
            int sourceWarnings;
            return BindInternal(profileView, surfaceProfileId, sewerProfileId,
                sewerProfileId, sewerProfileId, surfaceProfileId, networkId, false,
                out sourceWarnings);
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
            int sourceWarnings;
            return BindRoad(profileView, groundProfileId, leftProfileId,
                centreProfileId, rightProfileId, finalDesignProfileId,
                out sourceWarnings);
        }

        internal static int BindRoad(
            DBObject profileView,
            ObjectId groundProfileId,
            ObjectId leftProfileId,
            ObjectId centreProfileId,
            ObjectId rightProfileId,
            ObjectId finalDesignProfileId,
            out int sourceWarnings)
        {
            return BindInternal(profileView, leftProfileId, centreProfileId,
                rightProfileId, finalDesignProfileId, groundProfileId,
                ObjectId.Null, true, out sourceWarnings);
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
            bool roadRoles,
            out int sourceWarnings)
        {
            sourceWarnings = 0;
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
                    bool sourceExpected;
                    bool linked = AssignSources(item, groundProfileId, leftProfileId,
                        centreProfileId, rightProfileId, finalDesignProfileId,
                        networkId, roadRoles, out sourceExpected);
                    if (linked)
                        updated++;
                    else if (sourceExpected)
                        sourceWarnings++;
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
                bool sourceExpected;
                bool linked = AssignSources(item, groundProfileId, leftProfileId,
                    centreProfileId, rightProfileId, finalDesignProfileId,
                    networkId, roadRoles, out sourceExpected);
                if (linked)
                    updated++;
                else if (sourceExpected)
                    sourceWarnings++;
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
            bool roadRoles,
            out bool sourceExpected)
        {
            sourceExpected = false;
            int sourceFieldsExpected = 0;
            int sourceFieldsLinked = 0;
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

            SetBooleanIfAvailable(item, true,
                "ShowLabels",
                "DisplayLabels",
                "LabelsVisible",
                "ShowBandLabels",
                "BandLabelsVisible");
            SetBooleanIfAvailable(item, true, "Visible", "IsVisible");
            InvokeBooleanSetter(item, true,
                "SetShowLabels",
                "SetDisplayLabels",
                "SetLabelsVisible",
                "SetBandLabelsVisible",
                "SetVisible");
            foreach (PropertyInfo property in item.GetType().GetProperties(
                BindingFlags.Public | BindingFlags.Instance))
            {
                if (!property.CanWrite || property.PropertyType != typeof(ObjectId) ||
                    property.GetIndexParameters().Length != 0)
                    continue;
                string name = (property.Name ?? string.Empty).ToUpperInvariant();
                ObjectId source = ObjectId.Null;
                bool recognized = false;
                if (!networkBand &&
                    (name.Contains("PROFILE2") || name.Contains("SECONDARY")))
                {
                    source = secondaryProfileId;
                    recognized = true;
                }
                else if (name.Contains("DATASOURCE"))
                {
                    if (networkBand && !networkId.IsNull)
                    {
                        source = networkId;
                        recognized = true;
                    }
                }
                else if (name.Contains("NETWORK"))
                {
                    if (!networkId.IsNull)
                    {
                        source = networkId;
                        recognized = true;
                    }
                }
                else if (!networkBand &&
                    (name.Contains("PROFILE1") || name.Contains("PROFILE")))
                {
                    source = primaryProfileId;
                    recognized = true;
                }
                else
                    continue;

                if (!recognized) continue;
                sourceExpected = true;
                sourceFieldsExpected++;
                if (source.IsNull) continue;
                if (TrySetObjectIdProperty(item, property, source))
                    sourceFieldsLinked++;
            }

            foreach (MethodInfo method in item.GetType().GetMethods(
                BindingFlags.Public | BindingFlags.Instance))
            {
                if (!method.Name.StartsWith("Set", StringComparison.OrdinalIgnoreCase) ||
                    (method.Name.IndexOf("DataSource", StringComparison.OrdinalIgnoreCase) < 0 &&
                     method.Name.IndexOf("Profile", StringComparison.OrdinalIgnoreCase) < 0 &&
                     method.Name.IndexOf("Network", StringComparison.OrdinalIgnoreCase) < 0))
                    continue;
                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length != 1 || parameters[0].ParameterType != typeof(ObjectId))
                    continue;
                string name = (method.Name + parameters[0].Name).ToUpperInvariant();
                ObjectId source = name.Contains("2")
                    ? secondaryProfileId
                    : primaryProfileId;
                bool networkSource = name.Contains("NETWORK") || name.Contains("DATASOURCE");
                bool recognized = networkBand
                    ? networkSource
                    : name.Contains("PROFILE");
                if (!recognized) continue;
                if (networkSource)
                {
                    if (!networkBand || networkId.IsNull) continue;
                    source = networkId;
                }
                sourceExpected = true;
                sourceFieldsExpected++;
                if (source.IsNull) continue;
                try
                {
                    method.Invoke(item, new object[] { source });
                    ObjectId readback = ReadObjectIdFromMethodTarget(item, name);
                    if (!readback.IsNull && readback == source)
                        sourceFieldsLinked++;
                }
                catch { }
            }
            return sourceExpected && sourceFieldsExpected > 0 &&
                   sourceFieldsLinked == sourceFieldsExpected;
        }

        private static bool TrySetObjectIdProperty(
            object target,
            PropertyInfo property,
            ObjectId source)
        {
            if (target == null || property == null || source.IsNull) return false;
            try
            {
                object existing = property.GetValue(target, null);
                if (existing is ObjectId && (ObjectId)existing == source)
                    return true;
            }
            catch { }
            try
            {
                property.SetValue(target, source, null);
                object readback = property.GetValue(target, null);
                return readback is ObjectId && (ObjectId)readback == source;
            }
            catch { return false; }
        }

        private static ObjectId ReadObjectIdFromMethodTarget(object target, string setterName)
        {
            if (target == null || string.IsNullOrWhiteSpace(setterName))
                return ObjectId.Null;
            string name = setterName.ToUpperInvariant();
            string propertyName = name.Contains("DATASOURCE") ? "DataSourceId" :
                name.Contains("NETWORK") ? "NetworkId" :
                name.Contains("PROFILE2") || name.Contains("SECONDARY") ? "Profile2Id" :
                name.Contains("PROFILE1") || name.Contains("PROFILE") ? "Profile1Id" :
                string.Empty;
            object value = string.IsNullOrEmpty(propertyName)
                ? null
                : ReadProperty(target, propertyName);
            return value is ObjectId ? (ObjectId)value : ObjectId.Null;
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

        private static ObjectId ReadBandStyleId(object item)
        {
            foreach (string name in new[] { "BandStyleId", "StyleId" })
            {
                object value = ReadProperty(item, name);
                if (value is ObjectId && !((ObjectId)value).IsNull)
                    return (ObjectId)value;
            }
            return ObjectId.Null;
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

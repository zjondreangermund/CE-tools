using System;
using System.Reflection;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using Autodesk.Civil.DatabaseServices.Styles;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivilProfile = Autodesk.Civil.DatabaseServices.Profile;

[assembly: CommandClass(typeof(CETools.Civil3D.August13RoadProfileViewFinalizerCommands))]

namespace CETools.Civil3D
{
    public sealed class August13RoadProfileViewFinalizerCommands
    {
        [CommandMethod("CE_TOOLS", "CE_ROADPROFILEVIEWFINAL", CommandFlags.Modal | CommandFlags.Redraw)]
        public void FinalizeRoadProfileViews()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (document == null || civilDocument == null) return;

            int imported;
            string importMessage;
            ProfileStyleAutoImportRuntime.EnsureBundledProfileStyles(
                document,
                out imported,
                out importMessage);

            RoadProductionSettings settings = RoadProductionSettings.Read(document.Database);
            IList<string> viewNames = CivilStyleCatalogV2.ReadNames(
                document.Database,
                civilDocument,
                "Profile View Style");
            IList<string> bandNames = CivilStyleCatalogV2.ReadNames(
                document.Database,
                civilDocument,
                "Profile View Band Set Style");

            string requestedView = ChooseRoadProfileViewStyle(
                viewNames,
                settings.ProfileViewStyle);
            string requestedBand = RoadProductionSettings.SelectPreferredBandSet(
                bandNames,
                settings.ProfileViewBandSetStyle);

            int views = 0;
            int bandItems = 0;
            var warnings = new List<string>();

            try
            {
                using (DocumentLock documentLock = document.LockDocument())
                using (Transaction transaction =
                    document.Database.TransactionManager.StartTransaction())
                {
                    string actualView;
                    string actualBand;
                    ObjectId viewStyleId = CivilStyleCatalogV2.ResolveStyleId(
                        document.Database,
                        civilDocument,
                        "Profile View Style",
                        requestedView,
                        transaction,
                        out actualView);
                    ObjectId bandSetId = CivilStyleCatalogV2.ResolveStyleId(
                        document.Database,
                        civilDocument,
                        "Profile View Band Set Style",
                        requestedBand,
                        transaction,
                        out actualBand);

                    BlockTable blockTable = transaction.GetObject(
                        document.Database.BlockTableId,
                        OpenMode.ForRead,
                        false) as BlockTable;
                    BlockTableRecord modelSpace = blockTable == null
                        ? null
                        : transaction.GetObject(
                            blockTable[BlockTableRecord.ModelSpace],
                            OpenMode.ForRead,
                            false) as BlockTableRecord;
                    if (modelSpace == null)
                        throw new InvalidOperationException("Model space could not be opened.");

                    foreach (ObjectId id in modelSpace)
                    {
                        ProfileView profileView = transaction.GetObject(
                            id,
                            OpenMode.ForWrite,
                            false) as ProfileView;
                        if (profileView == null) continue;

                        ObjectId alignmentId = ReadObjectIdProperty(profileView, "AlignmentId");
                        if (alignmentId.IsNull)
                            alignmentId = ReadObjectIdProperty(profileView, "ParentAlignmentId");
                        if (alignmentId.IsNull)
                            alignmentId = ReadObjectIdProperty(profileView, "ParentAlignment");
                        CivilAlignment alignment = alignmentId.IsNull
                            ? null
                            : transaction.GetObject(
                                alignmentId,
                                OpenMode.ForRead,
                                false) as CivilAlignment;
                        if (!IsRoadAlignment(alignment)) continue;

                        try
                        {
                            ProfileStyleLinker.Apply(
                                profileView,
                                viewStyleId,
                                bandSetId);

                            ObjectId groundProfileId;
                            ObjectId leftProfileId;
                            ObjectId centreProfileId;
                            ObjectId rightProfileId;
                            ObjectId finalProfileId;
                            ResolveRoadProfiles(
                                alignment,
                                transaction,
                                out groundProfileId,
                                out leftProfileId,
                                out centreProfileId,
                                out rightProfileId,
                                out finalProfileId);
                            EnsureProfilesInProfileView(
                                profileView,
                                leftProfileId,
                                centreProfileId,
                                rightProfileId,
                                finalProfileId);
                            bandItems += ProfileViewBandDataBinder.BindRoad(
                                profileView,
                                groundProfileId,
                                leftProfileId,
                                centreProfileId,
                                rightProfileId,
                                finalProfileId);
                            views++;
                        }
                        catch (System.Exception exception)
                        {
                            warnings.Add(
                                (alignment == null
                                    ? profileView.Handle.ToString()
                                    : alignment.Name) +
                                ": " + exception.Message);
                        }
                    }

                    settings.ProfileViewStyle = actualView;
                    settings.ProfileViewBandSetStyle = actualBand;
                    transaction.Commit();
                }

                // Persist the resolved Road-only choices after the Civil transaction.
                settings.Write(document.Database);
                document.Editor.Regen();
                document.Editor.WriteMessage(
                    "\nCE_ROADPROFILEVIEWFINAL complete. Road profile views={0}; band items/data sources updated={1}; profile-view style={2}; band set={3}. {4}",
                    views,
                    bandItems,
                    settings.ProfileViewStyle,
                    settings.ProfileViewBandSetStyle,
                    importMessage);
                foreach (string warning in warnings.Take(8))
                    document.Editor.WriteMessage("\n  Warning: {0}", warning);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_ROADPROFILEVIEWFINAL failed. {0}",
                    exception.Message);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_PROFILELABELSETMULTI", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void AssignProfileLabelSetToMultipleFinalProfiles()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (document == null || civilDocument == null) return;

            List<ObjectId> profileIds = SelectObjects(
                document,
                "\nSelect final design profiles for the label set: ",
                obj => obj is CivilProfile);
            if (profileIds.Count == 0) return;

            IList<string> names = CivilStyleCatalogV2.ReadNames(
                document.Database, civilDocument, "Profile Label Set Style");
            if (names.Count == 0)
            {
                document.Editor.WriteMessage("\nCE_PROFILELABELSETMULTI cancelled. No profile label-set styles were found.");
                return;
            }

            var model = new ProductionSettingsDialogModel(
                "CE Tools - Assign Profile Label Sets",
                "Apply one Civil 3D profile label-set style to the selected profiles by rebuilding their profile-view label groups. Existing profile geometry and PVIs are unchanged.");
            model.AddChoice(
                "LabelSet", "01 Labels", "Profile label set", names[0],
                "Choose the label-set style to apply to every selected final design profile.",
                names.ToArray());
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            int applied = 0;
            int failed = 0;
            int viewsUpdated = 0;
            int labelGroupsCreated = 0;
            int drawFlagsEnabled = 0;

            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                string actual;
                ObjectId labelSetId = CivilStyleCatalogV2.ResolveStyleId(
                    document.Database,
                    civilDocument,
                    "Profile Label Set Style",
                    model.Text("LabelSet"),
                    transaction,
                    out actual);
                ProfileLabelSetStyle labelSet = labelSetId.IsNull
                    ? null
                    : transaction.GetObject(labelSetId, OpenMode.ForRead, false) as ProfileLabelSetStyle;
                if (labelSet == null)
                {
                    document.Editor.WriteMessage(
                        "\nCE_PROFILELABELSETMULTI cancelled. The selected profile label-set style could not be opened.");
                    return;
                }

                foreach (ObjectId id in profileIds.Distinct())
                {
                    CivilProfile profile = transaction.GetObject(
                        id, OpenMode.ForRead, false) as CivilProfile;
                    if (profile == null)
                    {
                        failed++;
                        continue;
                    }

                    int profileViews;
                    int profileGroups;
                    int profileDrawFlags;
                    bool ok = TryApplyProfileLabelSet(
                        transaction,
                        profile,
                        labelSet,
                        out profileViews,
                        out profileGroups,
                        out profileDrawFlags);
                    viewsUpdated += profileViews;
                    labelGroupsCreated += profileGroups;
                    drawFlagsEnabled += profileDrawFlags;
                    if (ok) applied++; else failed++;
                }
                transaction.Commit();

                document.Editor.Regen();
                document.Editor.WriteMessage(
                    "\nCE_PROFILELABELSETMULTI complete. Profiles updated={0}; failed={1}; profile views updated={2}; label groups created={3}; profile draws enabled={4}; label set={5}.",
                    applied,
                    failed,
                    viewsUpdated,
                    labelGroupsCreated,
                    drawFlagsEnabled,
                    actual);
            }
        }

        private static bool TryApplyProfileLabelSet(
            Transaction transaction,
            CivilProfile profile,
            ProfileLabelSetStyle labelSet,
            out int viewsUpdated,
            out int labelGroupsCreated,
            out int drawFlagsEnabled)
        {
            viewsUpdated = 0;
            labelGroupsCreated = 0;
            drawFlagsEnabled = 0;
            if (profile == null || labelSet == null) return false;

            CivilAlignment alignment = null;
            try
            {
                alignment = transaction.GetObject(
                    profile.AlignmentId,
                    OpenMode.ForRead,
                    false) as CivilAlignment;
            }
            catch
            {
                return false;
            }
            if (alignment == null) return false;

            foreach (ObjectId profileViewId in alignment.GetProfileViewIds())
            {
                ProfileView profileView = null;
                try
                {
                    profileView = transaction.GetObject(
                        profileViewId,
                        OpenMode.ForWrite,
                        false) as ProfileView;
                }
                catch
                {
                    continue;
                }
                if (profileView == null) continue;

                ProfileOverride matchingOverride = null;
                try
                {
                    foreach (ProfileOverride profileOverride in profileView.GraphOverrides)
                    {
                        if (profileOverride != null &&
                            profileOverride.ProfileId == profile.ObjectId)
                        {
                            matchingOverride = profileOverride;
                            break;
                        }
                    }
                }
                catch
                {
                    matchingOverride = null;
                }
                try
                {
                    if (matchingOverride != null && !matchingOverride.Draw)
                    {
                        matchingOverride.Draw = true;
                        drawFlagsEnabled++;
                    }

                    int created = ApplyProfileLabelSetToView(
                        transaction,
                        profileView,
                        profileViewId,
                        profile.ObjectId,
                        labelSet);
                    if (created > 0)
                    {
                        viewsUpdated++;
                        labelGroupsCreated += created;
                    }
                }
                catch
                {
                    // A damaged or incompatible profile view must not stop the
                    // remaining selected profiles from being processed.
                }
            }

            return viewsUpdated > 0;
        }

        private static int ApplyProfileLabelSetToView(
            Transaction transaction,
            ProfileView profileView,
            ObjectId profileViewId,
            ObjectId profileId,
            ProfileLabelSetStyle labelSet)
        {
            if (profileView == null || labelSet == null || labelSet.Count == 0)
                return 0;

            ProfileViewStyle profileViewStyle = transaction.GetObject(
                profileView.StyleId,
                OpenMode.ForRead,
                false) as ProfileViewStyle;
            if (profileViewStyle == null) return 0;

            ObjectIdCollection existingGroups =
                ProfileLabelGroup.GetAvailableLabelGroupIds(
                    ProfileLabelGroup.GetClass(typeof(ProfileLabelGroup)),
                    profileViewId,
                    profileId,
                    true);
            foreach (ObjectId existingId in existingGroups)
            {
                try
                {
                    ProfileLabelGroup existing = transaction.GetObject(
                        existingId,
                        OpenMode.ForWrite,
                        false) as ProfileLabelGroup;
                    if (existing != null) existing.Erase();
                }
                catch
                {
                    // An orphaned label group is not allowed to block the
                    // valid groups from the selected label set.
                }
            }

            int created = 0;
            for (int index = labelSet.Count - 1; index >= 0; index--)
            {
                ProfileLabelSetItem item;
                try { item = labelSet[index]; }
                catch { continue; }
                if (item == null || item.LabelStyleId.IsNull) continue;

                foreach (ObjectId groupId in CreateProfileLabelGroups(
                    profileViewId,
                    profileId,
                    profileViewStyle,
                    item))
                {
                    if (groupId.IsNull) continue;
                    ApplyProfileLabelGroupSettings(
                        transaction,
                        groupId,
                        item);
                    created++;
                }
            }

            profileView.RecordGraphicsModified(true);
            return created;
        }

        private static List<ObjectId> CreateProfileLabelGroups(
            ObjectId profileViewId,
            ObjectId profileId,
            ProfileViewStyle profileViewStyle,
            ProfileLabelSetItem item)
        {
            var result = new List<ObjectId>();
            if (item == null || item.LabelStyleId.IsNull) return result;

            switch (item.LabelStyleType)
            {
                case LabelStyleType.ProfileMajorStation:
                    TryAddProfileLabelGroup(
                        result,
                        () => ProfileStationLabelGroup.CreateMajor(
                            profileViewId,
                            profileId,
                            item.LabelStyleId,
                            profileViewStyle.BottomAxis.MajorTickStyle.Interval));
                    break;

                case LabelStyleType.ProfileMinorStation:
                    TryAddProfileLabelGroup(
                        result,
                        () => ProfileMinorStationLabelGroup.Create(
                            profileViewId,
                            item.LabelStyleId,
                            profileViewStyle.BottomAxis.MinorTickStyle.Interval));
                    break;

                case LabelStyleType.ProfileHorizontalGeometryPoint:
                    TryAddProfileLabelGroup(
                        result,
                        () => ProfileHorizontalGeometryPointLabelGroup.Create(
                            profileViewId,
                            profileId,
                            item.LabelStyleId));
                    break;

                case LabelStyleType.ProfileGradeBreaks:
                    TryAddProfileLabelGroup(
                        result,
                        () => ProfilePVILabelGroup.Create(
                            profileViewId,
                            profileId,
                            item.LabelStyleId));
                    break;

                case LabelStyleType.ProfileLine:
                    TryAddProfileLabelGroup(
                        result,
                        () => ProfileLineLabelGroup.Create(
                            profileViewId,
                            profileId,
                            item.LabelStyleId));
                    break;

                case LabelStyleType.ProfileCrestCurve:
                    TryAddProfileLabelGroup(
                        result,
                        () => ProfileCrestCurveLabelGroup.Create(
                            profileViewId,
                            profileId,
                            item.LabelStyleId));
                    break;

                case LabelStyleType.ProfileSagCurve:
                    TryAddProfileLabelGroup(
                        result,
                        () => ProfileSagCurveLabelGroup.Create(
                            profileViewId,
                            profileId,
                            item.LabelStyleId));
                    break;

                case LabelStyleType.ProfileCurve:
                    // Civil 3D exposes separate crest and sag groups for a
                    // generic "Profile Curve" label-set item.
                    TryAddProfileLabelGroup(
                        result,
                        () => ProfileCrestCurveLabelGroup.Create(
                            profileViewId,
                            profileId,
                            item.LabelStyleId));
                    TryAddProfileLabelGroup(
                        result,
                        () => ProfileSagCurveLabelGroup.Create(
                            profileViewId,
                            profileId,
                            item.LabelStyleId));
                    break;
            }

            return result;
        }

        private static void TryAddProfileLabelGroup(
            IList<ObjectId> result,
            Func<ObjectId> factory)
        {
            try
            {
                ObjectId id = factory();
                if (!id.IsNull) result.Add(id);
            }
            catch
            {
                // Keep unsupported label-set item types isolated.
            }
        }

        private static void ApplyProfileLabelGroupSettings(
            Transaction transaction,
            ObjectId groupId,
            ProfileLabelSetItem item)
        {
            try
            {
                ProfileLabelGroup group = transaction.GetObject(
                    groupId,
                    OpenMode.ForWrite,
                    false) as ProfileLabelGroup;
                if (group != null)
                {
                    group.DefaultDimensionAnchorOption =
                        (Autodesk.Civil.DimensionAnchorOptionType)item.DimensionAnchorOption;
                    group.DefaultDimensionAnchorValue = item.DimensionAnchorValue;
                }
            }
            catch
            {
            }

            try
            {
                ProfileHorizontalGeometryPointLabelGroup geometryGroup =
                    transaction.GetObject(
                        groupId,
                        OpenMode.ForWrite,
                        false) as ProfileHorizontalGeometryPointLabelGroup;
                if (geometryGroup == null) return;

                var options = geometryGroup.GetGeometryPointsOptions();
                var labeledPoints = item.GetLabeledAlignmentGeometryPoints();
                options.UnSelectAll();
                foreach (var pair in labeledPoints)
                {
                    try { options[pair.Key].Selected = true; }
                    catch { }
                }
                geometryGroup.SetGeometryPointsOptions(options);
            }
            catch
            {
            }
        }

        [CommandMethod("CE_TOOLS", "CE_PROFILEVIEWSTYLEMULTI", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void AssignProfileViewStyleToMultipleViews()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (document == null || civilDocument == null) return;

            List<ObjectId> viewIds = SelectObjects(
                document,
                "\nSelect profile views for the profile-view style: ",
                obj => obj is ProfileView);
            if (viewIds.Count == 0) return;

            IList<string> names = CivilStyleCatalogV2.ReadNames(
                document.Database, civilDocument, "Profile View Style");
            if (names.Count == 0)
            {
                document.Editor.WriteMessage("\nCE_PROFILEVIEWSTYLEMULTI cancelled. No profile-view styles were found.");
                return;
            }

            var model = new ProductionSettingsDialogModel(
                "CE Tools - Assign Profile View Styles",
                "Apply one Civil 3D profile-view style to all selected profile views and regenerate their display.");
            model.AddChoice(
                "Style", "01 Style", "Profile view style", names[0],
                "Choose the profile-view style to apply to every selected view.",
                names.ToArray());
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            int applied = 0;
            int failed = 0;
            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                string actual;
                ObjectId styleId = CivilStyleCatalogV2.ResolveStyleId(
                    document.Database,
                    civilDocument,
                    "Profile View Style",
                    model.Text("Style"),
                    transaction,
                    out actual);

                foreach (ObjectId id in viewIds.Distinct())
                {
                    ProfileView view = transaction.GetObject(
                        id, OpenMode.ForWrite, false) as ProfileView;
                    if (view == null)
                    {
                        failed++;
                        continue;
                    }
                    if (TrySetObjectIdProperty(view, "StyleId", styleId) ||
                        TrySetObjectIdProperty(view, "ProfileViewStyleId", styleId))
                    {
                        TryInvoke(view, "Rebuild");
                        Entity entity = view as Entity;
                        if (entity != null) entity.RecordGraphicsModified(true);
                        applied++;
                    }
                    else failed++;
                }
                transaction.Commit();
            }

            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_PROFILEVIEWSTYLEMULTI complete. Views updated={0}; failed={1}; style={2}.",
                applied, failed, model.Text("Style"));
        }

        private static string ChooseRoadProfileViewStyle(
            IList<string> names,
            string current)
        {
            List<string> values = names == null
                ? new List<string>()
                : names.Where(item => !string.IsNullOrWhiteSpace(item)).ToList();

            string exact = values.FirstOrDefault(item =>
                string.Equals(
                    item,
                    current ?? string.Empty,
                    StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(exact)) return exact;

            string road = values.FirstOrDefault(item =>
                item.IndexOf("ROAD", StringComparison.OrdinalIgnoreCase) >= 0 &&
                item.IndexOf("SEWER", StringComparison.OrdinalIgnoreCase) < 0 &&
                item.IndexOf("PIPE", StringComparison.OrdinalIgnoreCase) < 0);
            if (!string.IsNullOrWhiteSpace(road)) return road;

            return string.IsNullOrWhiteSpace(current)
                ? CivilStyleCatalogV2.DrawingDefault
                : current;
        }

        private static void ResolveRoadProfiles(
            CivilAlignment alignment,
            Transaction transaction,
            out ObjectId groundProfileId,
            out ObjectId leftProfileId,
            out ObjectId centreProfileId,
            out ObjectId rightProfileId,
            out ObjectId finalProfileId)
        {
            groundProfileId = ObjectId.Null;
            leftProfileId = ObjectId.Null;
            centreProfileId = ObjectId.Null;
            rightProfileId = ObjectId.Null;
            finalProfileId = ObjectId.Null;
            ObjectId fallbackGroundProfileId = ObjectId.Null;
            ObjectId fallbackDesignProfileId = ObjectId.Null;
            if (alignment == null) return;

            foreach (ObjectId profileId in alignment.GetProfileIds())
            {
                CivilProfile profile = transaction.GetObject(
                    profileId,
                    OpenMode.ForRead,
                    false) as CivilProfile;
                if (profile == null) continue;
                string identity = ((profile.Name ?? string.Empty) + " " +
                    (profile.Description ?? string.Empty)).ToUpperInvariant();

                bool ground = IsGroundProfileIdentity(identity);
                bool excluded = ground;
                if (ground && fallbackGroundProfileId.IsNull)
                    fallbackGroundProfileId = profileId;
                if (!excluded && fallbackDesignProfileId.IsNull)
                    fallbackDesignProfileId = profileId;
                if (finalProfileId.IsNull && IsFinalDesignProfileIdentity(identity))
                    finalProfileId = profileId;
                if (!excluded && leftProfileId.IsNull &&
                    (identity.Contains("LEFT") || identity.Contains("LHS") ||
                     identity.Contains(" HL") || identity.Contains("HL-") ||
                     identity.Contains("LEFT-EDGE") || identity.Contains("LEFT EDGE")))
                    leftProfileId = profileId;
                if (!excluded && rightProfileId.IsNull &&
                    (identity.Contains("RIGHT") || identity.Contains("RHS") ||
                     identity.Contains(" HR") || identity.Contains("HR-") ||
                     identity.Contains("RIGHT-EDGE") || identity.Contains("RIGHT EDGE")))
                    rightProfileId = profileId;
                if (!excluded && centreProfileId.IsNull &&
                    (identity.Contains("CENTRE") || identity.Contains("CENTER") ||
                     identity.Contains("CENTRELINE") || identity.Contains("CENTERLINE")))
                    centreProfileId = profileId;
            }

            if (groundProfileId.IsNull) groundProfileId = fallbackGroundProfileId;
            if (finalProfileId.IsNull) finalProfileId = fallbackDesignProfileId;
            if (centreProfileId.IsNull) centreProfileId = finalProfileId;
            if (leftProfileId.IsNull) leftProfileId = centreProfileId;
            if (rightProfileId.IsNull) rightProfileId = centreProfileId;
        }

        private static bool IsGroundProfileIdentity(string identity)
        {
            string value = (identity ?? string.Empty).ToUpperInvariant();
            return value.Contains("NGL") ||
                   value.Contains("NATURAL") ||
                   value.Contains("EXIST") ||
                   value.Contains("GROUND") ||
                   value.Contains("SURFACE") ||
                   ContainsProfileToken(value, "EG");
        }

        private static bool IsFinalDesignProfileIdentity(string identity)
        {
            string value = (identity ?? string.Empty).ToUpperInvariant();
            return !IsGroundProfileIdentity(value) &&
                   (ContainsProfileToken(value, "FG") ||
                    value.Contains("FINAL") ||
                    value.Contains("DESIGN") ||
                    value.Contains("PROPOSED") ||
                    value.Contains("ROAD"));
        }

        private static bool ContainsProfileToken(string identity, string token)
        {
            string value = (identity ?? string.Empty)
                .Replace("-", " ")
                .Replace("_", " ")
                .Replace("/", " ")
                .Replace(".", " ");
            string padded = " " + value + " ";
            return padded.IndexOf(
                " " + (token ?? string.Empty).Trim() + " ",
                StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static int EnsureProfilesInProfileView(
            ProfileView profileView,
            params ObjectId[] profileIds)
        {
            if (profileView == null || profileIds == null) return 0;
            HashSet<ObjectId> existing = new HashSet<ObjectId>();
            object current = InvokeNoArguments(profileView, "GetProfileIds") ??
                ReadProperty(profileView, "ProfileIds") ??
                ReadProperty(profileView, "Profiles");
            foreach (object value in CivilStyleDiscovery.Enumerate(current))
            {
                if (value is ObjectId) existing.Add((ObjectId)value);
                else if (value is DBObject) existing.Add(((DBObject)value).ObjectId);
            }

            int added = 0;
            foreach (ObjectId id in profileIds.Distinct())
            {
                if (id.IsNull || existing.Contains(id)) continue;
                if (Invoke(profileView, "AddProfile", id) ||
                    Invoke(profileView, "AddProfileId", id))
                {
                    existing.Add(id);
                    added++;
                }
            }
            return added;
        }

        private static object ReadProperty(object target, string name)
        {
            if (target == null) return null;
            try
            {
                PropertyInfo property = target.GetType().GetProperty(
                    name,
                    BindingFlags.Public | BindingFlags.Instance);
                return property == null ? null : property.GetValue(target, null);
            }
            catch { return null; }
        }

        private static object InvokeNoArguments(object target, string name)
        {
            if (target == null) return null;
            try
            {
                MethodInfo method = target.GetType().GetMethod(
                    name,
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    Type.EmptyTypes,
                    null);
                return method == null ? null : method.Invoke(target, null);
            }
            catch { return null; }
        }

        private static bool Invoke(object target, string name, params object[] arguments)
        {
            if (target == null) return false;
            foreach (MethodInfo method in target.GetType().GetMethods(
                BindingFlags.Public | BindingFlags.Instance))
            {
                if (!string.Equals(method.Name, name, StringComparison.OrdinalIgnoreCase))
                    continue;
                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length != arguments.Length) continue;
                bool compatible = true;
                for (int index = 0; index < parameters.Length; index++)
                {
                    if (!parameters[index].ParameterType.IsInstanceOfType(arguments[index]))
                    {
                        compatible = false;
                        break;
                    }
                }
                if (!compatible) continue;
                try
                {
                    method.Invoke(target, arguments);
                    return true;
                }
                catch { }
            }
            return false;
        }

        private static ObjectId ReadObjectIdProperty(object target, string propertyName)
        {
            if (target == null) return ObjectId.Null;
            try
            {
                System.Reflection.PropertyInfo property =
                    target.GetType().GetProperty(propertyName);
                if (property == null) return ObjectId.Null;
                object value = property.GetValue(target, null);
                if (value is ObjectId) return (ObjectId)value;
                DBObject databaseObject = value as DBObject;
                if (databaseObject != null) return databaseObject.ObjectId;
                if (value != null)
                {
                    PropertyInfo objectIdProperty = value.GetType().GetProperty(
                        "ObjectId",
                        BindingFlags.Public | BindingFlags.Instance);
                    object raw = objectIdProperty == null
                        ? null
                        : objectIdProperty.GetValue(value, null);
                    if (raw is ObjectId) return (ObjectId)raw;
                }
                return ObjectId.Null;
            }
            catch
            {
                return ObjectId.Null;
            }
        }

        private static List<ObjectId> SelectObjects(
            Document document,
            string prompt,
            Func<DBObject, bool> predicate)
        {
            PromptSelectionResult selected = document.Editor.SelectImplied();
            if (selected.Status != PromptStatus.OK ||
                selected.Value == null || selected.Value.Count == 0)
            {
                selected = document.Editor.GetSelection(new PromptSelectionOptions
                {
                    MessageForAdding = prompt,
                    AllowDuplicates = false,
                    RejectObjectsFromNonCurrentSpace = true
                });
            }
            document.Editor.SetImpliedSelection(new ObjectId[0]);
            if (selected.Status != PromptStatus.OK || selected.Value == null)
                return new List<ObjectId>();

            var result = new List<ObjectId>();
            using (Transaction transaction =
                document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in selected.Value.GetObjectIds())
                {
                    DBObject value = null;
                    try { value = transaction.GetObject(id, OpenMode.ForRead, false); }
                    catch { }
                    if (value != null && predicate(value)) result.Add(id);
                }
            }
            return result;
        }

        private static bool TrySetObjectIdProperty(
            object target,
            string propertyName,
            ObjectId value)
        {
            if (target == null || value.IsNull) return false;
            try
            {
                PropertyInfo property = target.GetType().GetProperty(
                    propertyName,
                    BindingFlags.Public | BindingFlags.Instance);
                if (property == null || !property.CanWrite ||
                    property.PropertyType != typeof(ObjectId))
                    return false;
                property.SetValue(target, value, null);
                return true;
            }
            catch { return false; }
        }

        private static bool TryInvoke(object target, string name, params object[] arguments)
        {
            if (target == null) return false;
            foreach (MethodInfo method in target.GetType().GetMethods(
                BindingFlags.Public | BindingFlags.Instance))
            {
                if (!string.Equals(method.Name, name, StringComparison.OrdinalIgnoreCase))
                    continue;
                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length != (arguments == null ? 0 : arguments.Length))
                    continue;
                bool compatible = true;
                for (int index = 0; index < parameters.Length; index++)
                {
                    if (arguments[index] == null ||
                        !parameters[index].ParameterType.IsInstanceOfType(arguments[index]))
                    {
                        compatible = false;
                        break;
                    }
                }
                if (!compatible) continue;
                try
                {
                    method.Invoke(target, arguments);
                    return true;
                }
                catch { }
            }
            return false;
        }

        private static bool IsRoadAlignment(CivilAlignment alignment)
        {
            if (alignment == null) return false;
            string name = alignment.Name ?? string.Empty;
            string description = alignment.Description ?? string.Empty;
            return name.StartsWith("RD", StringComparison.OrdinalIgnoreCase) ||
                   name.StartsWith("ROAD", StringComparison.OrdinalIgnoreCase) ||
                   description.IndexOf("CE road", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}

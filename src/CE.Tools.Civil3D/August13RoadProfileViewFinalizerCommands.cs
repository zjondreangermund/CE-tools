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

                            ObjectId leftProfileId;
                            ObjectId centreProfileId;
                            ObjectId rightProfileId;
                            ObjectId finalProfileId;
                            ResolveRoadProfiles(
                                alignment,
                                transaction,
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
                "Apply one Civil 3D profile label-set style to all selected final design profiles. This writes the label-set ObjectId and refreshes the profile graphics.");
            model.AddChoice(
                "LabelSet", "01 Labels", "Profile label set", names[0],
                "Choose the label-set style to apply to every selected final design profile.",
                names.ToArray());
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            int applied = 0;
            int failed = 0;
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

                foreach (ObjectId id in profileIds.Distinct())
                {
                    CivilProfile profile = transaction.GetObject(
                        id, OpenMode.ForWrite, false) as CivilProfile;
                    if (profile == null)
                    {
                        failed++;
                        continue;
                    }
                    bool ok = TrySetObjectIdProperty(profile, "LabelSetId", labelSetId) ||
                              TrySetObjectIdProperty(profile, "LabelSetStyleId", labelSetId);
                    if (ok)
                    {
                        Entity entity = profile as Entity;
                        if (entity != null) entity.RecordGraphicsModified(true);
                        applied++;
                    }
                    else failed++;
                }
                transaction.Commit();
            }

            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_PROFILELABELSETMULTI complete. Profiles updated={0}; failed={1}; label set={2}.",
                applied, failed, model.Text("LabelSet"));
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
            out ObjectId leftProfileId,
            out ObjectId centreProfileId,
            out ObjectId rightProfileId,
            out ObjectId finalProfileId)
        {
            leftProfileId = ObjectId.Null;
            centreProfileId = ObjectId.Null;
            rightProfileId = ObjectId.Null;
            finalProfileId = ObjectId.Null;
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

                bool excluded = identity.Contains("NGL") ||
                                identity.Contains("NATURAL") ||
                                identity.Contains("EXIST") ||
                                identity.Contains("GROUND") ||
                                identity.Contains("SURFACE") ||
                                identity.Contains("EG");
                if (!excluded && fallbackDesignProfileId.IsNull)
                    fallbackDesignProfileId = profileId;
                if (finalProfileId.IsNull && !excluded &&
                    (identity.Contains("-FG") ||
                     identity.Contains("FINAL") ||
                     identity.Contains("DESIGN") ||
                     identity.Contains("ROAD")))
                    finalProfileId = profileId;
                if (leftProfileId.IsNull &&
                    (identity.Contains("LEFT") || identity.Contains("LHS") ||
                     identity.Contains(" HL") || identity.Contains("HL-") ||
                     identity.Contains("LEFT-EDGE") || identity.Contains("LEFT EDGE")))
                    leftProfileId = profileId;
                if (rightProfileId.IsNull &&
                    (identity.Contains("RIGHT") || identity.Contains("RHS") ||
                     identity.Contains(" HR") || identity.Contains("HR-") ||
                     identity.Contains("RIGHT-EDGE") || identity.Contains("RIGHT EDGE")))
                    rightProfileId = profileId;
                if (centreProfileId.IsNull &&
                    (identity.Contains("CENTRE") || identity.Contains("CENTER") ||
                     identity.Contains("CENTRELINE") || identity.Contains("CENTERLINE")))
                    centreProfileId = profileId;
            }

            if (finalProfileId.IsNull) finalProfileId = fallbackDesignProfileId;
            if (centreProfileId.IsNull) centreProfileId = finalProfileId;
            if (leftProfileId.IsNull) leftProfileId = centreProfileId;
            if (rightProfileId.IsNull) rightProfileId = centreProfileId;
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
                return value is ObjectId ? (ObjectId)value : ObjectId.Null;
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

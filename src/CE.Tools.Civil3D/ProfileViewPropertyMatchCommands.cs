using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivilNetwork = Autodesk.Civil.DatabaseServices.Network;
using CivilProfile = Autodesk.Civil.DatabaseServices.Profile;
using CivilProfileView = Autodesk.Civil.DatabaseServices.ProfileView;

[assembly: CommandClass(typeof(CETools.Civil3D.ProfileViewPropertyMatchCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Field tools for making multiple profile views use one reference view's
    /// presentation and for batch-editing the native Bands-tab data sources.
    /// The operations persist the top/bottom collections back to Civil 3D rather
    /// than editing temporary ProfileViewBandItem wrappers.
    /// </summary>
    public sealed class ProfileViewPropertyMatchCommands
    {
        private static ProfileViewEditMatchSession _editMatchSession;

        [CommandMethod("CE_TOOLS", "CE_PROFILEVIEWEDITMATCH",
            CommandFlags.Modal | CommandFlags.Redraw | CommandFlags.UsePickSet)]
        public void EditOneProfileViewThenApplyToSelected()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            ObjectId sourceId = PromptProfileView(
                document.Editor,
                "\nSelect the ONE profile view to edit in native Profile View Properties: ");
            if (sourceId.IsNull) return;

            List<ObjectId> targets = PromptProfileViews(
                document,
                "\nSelect all OTHER profile views that must receive the same edits: ",
                sourceId);
            if (targets.Count == 0)
            {
                document.Editor.WriteMessage(
                    "\nCE_PROFILEVIEWEDITMATCH: no target profile views were selected.");
                return;
            }

            _editMatchSession = new ProfileViewEditMatchSession
            {
                Database = document.Database,
                SourceId = sourceId,
                TargetIds = targets.ToList()
            };

            try
            {
                document.Editor.SetImpliedSelection(new[] { sourceId });
                document.Editor.WriteMessage(
                    "\nEdit the source Profile View Properties once. Click OK/Apply and close the native Civil 3D window; CE Tools will then copy the edited view style, ranges and complete Bands-tab rows to {0} selected target view(s), mapping profile/network sources to each target.",
                    targets.Count);

                // Queue the native modal editor first and the CE apply command
                // immediately after it. AutoCAD executes the second command only
                // after EditGraphProperties has closed.
                document.SendStringToExecute(
                    "_.EditGraphProperties \nCE_PROFILEVIEWEDITMATCHAPPLY ",
                    true,
                    false,
                    true);
            }
            catch (System.Exception exception)
            {
                _editMatchSession = null;
                document.Editor.WriteMessage(
                    "\nCE_PROFILEVIEWEDITMATCH could not open native Profile View Properties: {0}",
                    exception.Message);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_PROFILEVIEWEDITMATCHAPPLY",
            CommandFlags.Modal | CommandFlags.Redraw)]
        public void ApplyEditedProfileViewToSelected()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civil = CivilApplication.ActiveDocument;
            ProfileViewEditMatchSession session = _editMatchSession;
            _editMatchSession = null;

            if (document == null || civil == null || session == null ||
                !ReferenceEquals(session.Database, document.Database))
            {
                if (document != null)
                    document.Editor.WriteMessage(
                        "\nCE_PROFILEVIEWEDITMATCHAPPLY: no pending edit-and-match session exists for this drawing.");
                return;
            }

            int completed = 0;
            int copiedRows = 0;
            int failed = 0;
            using (DocumentLock documentLock = document.LockDocument())
            {
                foreach (ObjectId targetId in session.TargetIds.Distinct())
                {
                    try
                    {
                        SewerLongSectionContext targetContext =
                            SewerLongSectionBandService.ResolveContext(
                                document.Database,
                                civil,
                                targetId);

                        using (Transaction tr =
                            document.Database.TransactionManager.StartTransaction())
                        {
                            CivilProfileView source = tr.GetObject(
                                session.SourceId,
                                OpenMode.ForRead,
                                false) as CivilProfileView;
                            CivilProfileView target = tr.GetObject(
                                targetId,
                                OpenMode.ForWrite,
                                false) as CivilProfileView;
                            if (source == null || target == null ||
                                target.IsReferenceObject)
                                throw new InvalidOperationException(
                                    "Source/target profile view is unavailable or read-only.");

                            target.StyleId = source.StyleId;
                            CopyViewRangeProperties(source, target);
                            copiedRows += CopyBands(
                                source,
                                target,
                                tr,
                                true,
                                false,
                                targetContext.NetworkId);

                            try { target.RecordGraphicsModified(true); }
                            catch { }
                            tr.Commit();
                        }
                        completed++;
                    }
                    catch (System.Exception exception)
                    {
                        failed++;
                        document.Editor.WriteMessage(
                            "\nCE_PROFILEVIEWEDITMATCHAPPLY target {0} skipped: {1}",
                            targetId.Handle,
                            exception.Message);
                    }
                }
            }

            try
            {
                document.Editor.SetImpliedSelection(
                    session.TargetIds.Distinct().ToArray());
                document.Database.TransactionManager.QueueForGraphicsFlush();
                document.Editor.Regen();
                AcApplication.UpdateScreen();
            }
            catch { }

            document.Editor.WriteMessage(
                "\nCE_PROFILEVIEWEDITMATCH complete. Edited source={0}; target views updated={1}; native band rows copied={2}; failed={3}.",
                session.SourceId.Handle,
                completed,
                copiedRows,
                failed);
        }

        [CommandMethod("CE_TOOLS", "CE_PROFILEVIEWMATCH",
            CommandFlags.Modal | CommandFlags.Redraw | CommandFlags.UsePickSet)]
        public void MatchProfileViewProperties()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            ObjectId sourceId = PromptProfileView(
                document.Editor,
                "\nSelect SOURCE profile view whose properties/bands must be matched: ");
            if (sourceId.IsNull) return;

            List<ObjectId> targetIds = PromptProfileViews(
                document,
                "\nSelect multiple TARGET profile views to match to the first/source view: ",
                sourceId);
            if (targetIds.Count == 0) return;

            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Match Profile View Properties",
                "The first/source profile view remains unchanged. The selected target views can receive the same profile-view style, station/elevation settings, complete native top/bottom band rows, band styles, label settings and data-source assignments.");
            settings.AddChoice("ViewStyle", "01 Presentation", "Match profile-view style", "Yes",
                "Copies the source Profile View Style to every target.", new[] { "Yes", "No" });
            settings.AddChoice("Ranges", "01 Presentation", "Match station/elevation properties", "Yes",
                "Copies writable station/elevation range and split-view properties where Civil 3D 2023 exposes them.", new[] { "Yes", "No" });
            settings.AddChoice("Bands", "02 Bands", "Match all band rows / headings", "Yes",
                "Rebuilds the target top/bottom band collections from the source, including row style, gap, label ranges, staggering and native data-source fields.", new[] { "Yes", "No" });
            settings.AddChoice("Sources", "02 Bands", "Band data-source handling", "Match source exactly",
                "Exact mode copies Profile 1/Profile 2/Pipe Network source IDs from the source view. Target-profile mode first tries to find a profile with the same name on each target alignment.",
                new[] { "Match source exactly", "Match profile names on target alignment" });
            settings.AddChoice("Labels", "02 Bands", "Show band labels", "Yes",
                "Forces Show Labels on for every copied band row.", new[] { "Yes", "No" });
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;

            bool copyStyle = IsYes(settings.Text("ViewStyle"));
            bool copyRanges = IsYes(settings.Text("Ranges"));
            bool copyBands = IsYes(settings.Text("Bands"));
            bool showLabels = IsYes(settings.Text("Labels"));
            bool mapProfiles = string.Equals(
                settings.Text("Sources"),
                "Match profile names on target alignment",
                StringComparison.OrdinalIgnoreCase);

            int completed = 0, bandRows = 0, groups = 0, failed = 0;
            using (DocumentLock documentLock = document.LockDocument())
            {
                foreach (ObjectId targetId in targetIds)
                {
                    try
                    {
                        using (Transaction tr = document.Database.TransactionManager.StartTransaction())
                        {
                            CivilProfileView source = tr.GetObject(
                                sourceId, OpenMode.ForRead, false) as CivilProfileView;
                            CivilProfileView target = tr.GetObject(
                                targetId, OpenMode.ForWrite, false) as CivilProfileView;
                            if (source == null || target == null || target.IsReferenceObject)
                                throw new InvalidOperationException("Source/target is not an editable profile view.");

                            if (copyStyle) target.StyleId = source.StyleId;
                            if (copyRanges) CopyViewRangeProperties(source, target);
                            if (copyBands)
                                bandRows += CopyBands(
                                    source, target, tr, mapProfiles, showLabels,
                                    ObjectId.Null);

                            try { target.RecordGraphicsModified(true); } catch { }
                            tr.Commit();
                        }

                        if (copyBands)
                        {
                            try
                            {
                                groups += PipeNetworkBandLabelGroup
                                    .GetAvailableLabelGroupIds(targetId).Count;
                            }
                            catch { }
                        }
                        completed++;
                    }
                    catch (System.Exception exception)
                    {
                        failed++;
                        document.Editor.WriteMessage(
                            "\nCE_PROFILEVIEWMATCH target {0} skipped: {1}",
                            targetId.Handle, exception.Message);
                    }
                }
            }

            try
            {
                document.Editor.SetImpliedSelection(targetIds.ToArray());
                document.Database.TransactionManager.QueueForGraphicsFlush();
                document.Editor.Regen();
                AcApplication.UpdateScreen();
            }
            catch { }

            document.Editor.WriteMessage(
                "\nCE_PROFILEVIEWMATCH complete. Source={0}; targets matched={1}; copied native band rows={2}; PipeNetwork band-label groups visible={3}; failed={4}.",
                sourceId.Handle, completed, bandRows, groups, failed);
        }

        [CommandMethod("CE_TOOLS", "CE_PROFILEVIEWDATASOURCES",
            CommandFlags.Modal | CommandFlags.Redraw | CommandFlags.UsePickSet)]
        public void ApplyBandDataSourcesToMultipleViews()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civil = CivilApplication.ActiveDocument;
            if (document == null || civil == null) return;

            List<ObjectId> viewIds = PromptProfileViews(
                document,
                "\nSelect multiple profile views whose band Data Source fields must be updated: ",
                ObjectId.Null);
            if (viewIds.Count == 0) return;

            List<ProfileChoice> profiles = ReadProfiles(document.Database, civil);
            List<NetworkChoice> networks = ReadNetworks(document.Database, civil);
            const string keep = "<Keep current>";
            const string same = "<Same as Profile 1>";

            var profile1Choices = new List<string> { keep };
            profile1Choices.AddRange(profiles.Select(item => item.Display));
            var profile2Choices = new List<string> { keep, same };
            profile2Choices.AddRange(profiles.Select(item => item.Display));
            var networkChoices = new List<string> { keep };
            networkChoices.AddRange(networks.Select(item => item.Display));

            var model = new ProductionSettingsDialogModel(
                "CE Tools - Profile View Band Data Sources",
                "Batch equivalent of editing the Bands tab Data Source columns. Apply one Profile 1, Profile 2 and/or Pipe Network source to compatible band items on every selected profile view.");
            model.AddChoice("Rows", "01 Scope", "Apply to band rows", "All compatible rows",
                "Choose whether to update all compatible rows, only profile-data rows, or only Pipe Network rows.",
                new[] { "All compatible rows", "Profile rows only", "Pipe Network rows only" });
            model.AddChoice("Profile1", "02 Profile data", "Profile 1", keep,
                "Selected profile is written to Profile 1 on every compatible profile-data band item.", profile1Choices);
            model.AddChoice("Profile2", "02 Profile data", "Profile 2", same,
                "Selected profile is written to Profile 2 on every compatible profile-data band item.", profile2Choices);
            model.AddChoice("Network", "03 Pipe network", "Pipe Network data source", keep,
                "Selected gravity network is written to DataSourceId for every Pipe Network band item.", networkChoices);
            model.AddChoice("Labels", "04 Labels", "Show labels for edited rows", "Yes",
                "Turns Show Labels on after source assignment so blank-source repairs become visible immediately.", new[] { "Yes", "No" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            ProfileChoice profile1 = FindProfile(profiles, model.Text("Profile1"));
            ProfileChoice profile2 = string.Equals(model.Text("Profile2"), same, StringComparison.OrdinalIgnoreCase)
                ? profile1 : FindProfile(profiles, model.Text("Profile2"));
            NetworkChoice network = FindNetwork(networks, model.Text("Network"));
            bool profileRows = !string.Equals(model.Text("Rows"), "Pipe Network rows only", StringComparison.OrdinalIgnoreCase);
            bool networkRows = !string.Equals(model.Text("Rows"), "Profile rows only", StringComparison.OrdinalIgnoreCase);
            bool showLabels = IsYes(model.Text("Labels"));

            int views = 0, rows = 0, verified = 0, pipeGroups = 0, failed = 0;
            using (DocumentLock documentLock = document.LockDocument())
            {
                foreach (ObjectId viewId in viewIds)
                {
                    try
                    {
                        using (Transaction tr = document.Database.TransactionManager.StartTransaction())
                        {
                            CivilProfileView view = tr.GetObject(
                                viewId, OpenMode.ForWrite, false) as CivilProfileView;
                            if (view == null || view.IsReferenceObject)
                                throw new InvalidOperationException("Profile view is read-only or unavailable.");

                            int local = 0, localVerified = 0;
                            ProfileViewBandPersistence.Update(
                                view,
                                item =>
                                {
                                    bool isNetwork =
                                        item.BandType == Autodesk.Civil.BandType.PipeNetwork ||
                                        item.BandType == Autodesk.Civil.BandType.PressureNetwork;
                                    if (isNetwork)
                                    {
                                        if (networkRows && network != null && !network.Id.IsNull)
                                        {
                                            item.DataSourceId = network.Id;
                                            local++;
                                        }
                                    }
                                    else if (profileRows)
                                    {
                                        if (profile1 != null && !profile1.Id.IsNull)
                                        {
                                            item.Profile1Id = profile1.Id;
                                            local++;
                                        }
                                        if (profile2 != null && !profile2.Id.IsNull)
                                        {
                                            item.Profile2Id = profile2.Id;
                                            local++;
                                        }
                                    }
                                    if (showLabels) item.ShowLabels = true;
                                },
                                item =>
                                {
                                    bool isNetwork =
                                        item.BandType == Autodesk.Civil.BandType.PipeNetwork ||
                                        item.BandType == Autodesk.Civil.BandType.PressureNetwork;
                                    if (isNetwork && networkRows && network != null &&
                                        item.DataSourceId == network.Id) localVerified++;
                                    if (!isNetwork && profileRows && profile1 != null &&
                                        item.Profile1Id == profile1.Id) localVerified++;
                                });
                            try { view.RecordGraphicsModified(true); } catch { }
                            tr.Commit();
                            rows += local;
                            verified += localVerified;
                        }
                        try
                        {
                            pipeGroups += PipeNetworkBandLabelGroup
                                .GetAvailableLabelGroupIds(viewId).Count;
                        }
                        catch { }
                        views++;
                    }
                    catch (System.Exception exception)
                    {
                        failed++;
                        document.Editor.WriteMessage(
                            "\nCE_PROFILEVIEWDATASOURCES view {0} skipped: {1}",
                            viewId.Handle, exception.Message);
                    }
                }
            }

            try
            {
                document.Editor.SetImpliedSelection(viewIds.ToArray());
                document.Editor.Regen();
                AcApplication.UpdateScreen();
            }
            catch { }

            document.Editor.WriteMessage(
                "\nCE_PROFILEVIEWDATASOURCES complete. Views={0}; source fields written={1}; verified fields={2}; PipeNetwork band-label groups={3}; failed={4}.",
                views, rows, verified, pipeGroups, failed);
        }

        [CommandMethod("CE_TOOLS", "CE_SEWPIPEBANDGROUPS",
            CommandFlags.Modal | CommandFlags.Redraw | CommandFlags.UsePickSet)]
        public void RepairPipeNetworkBandLabelGroups()
        {
            // The native PipeNetworkBandLabelGroup is generated by Civil 3D from
            // a Pipe Network band row. CE_SEWBANDLABELS materialises those rows,
            // assigns DataSourceId and enables labels for all selected views.
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            document.SendStringToExecute("CE_SEWBANDLABELS ", true, false, true);
        }

        private static int CopyBands(
            CivilProfileView source,
            CivilProfileView target,
            Transaction tr,
            bool mapProfiles,
            bool showLabels,
            ObjectId targetNetworkId)
        {
            int copied = 0;
            copied += CopyBandLocation(
                source.Bands.GetTopBandItems(),
                target.Bands.GetTopBandItems(),
                true, source, target, tr, mapProfiles, showLabels,
                targetNetworkId);
            copied += CopyBandLocation(
                source.Bands.GetBottomBandItems(),
                target.Bands.GetBottomBandItems(),
                false, source, target, tr, mapProfiles, showLabels,
                targetNetworkId);
            return copied;
        }

        private static int CopyBandLocation(
            ProfileViewBandItemCollection sourceRows,
            ProfileViewBandItemCollection targetRows,
            bool top,
            CivilProfileView sourceView,
            CivilProfileView targetView,
            Transaction tr,
            bool mapProfiles,
            bool showLabels,
            ObjectId targetNetworkId)
        {
            try
            {
                ClearCollection(targetRows);
                for (int index = 0; index < sourceRows.Count; index++)
                {
                    ProfileViewBandItem source = sourceRows[index];
                    targetRows.Add(source.BandStyleId);
                    ProfileViewBandItem target = targetRows[targetRows.Count - 1];
                    CopyWritableBandProperties(source, target);
                    TrySetObjectId(target, "AlignmentId", targetView.AlignmentId);

                    bool networkBand =
                        source.BandType == Autodesk.Civil.BandType.PipeNetwork ||
                        source.BandType == Autodesk.Civil.BandType.PressureNetwork;
                    if (mapProfiles)
                    {
                        ObjectId mapped1 = MapProfile(
                            source.Profile1Id, targetView.AlignmentId, tr);
                        ObjectId mapped2 = MapProfile(
                            source.Profile2Id, targetView.AlignmentId, tr);
                        if (!mapped1.IsNull) target.Profile1Id = mapped1;
                        if (!mapped2.IsNull) target.Profile2Id = mapped2;
                        if (networkBand && !targetNetworkId.IsNull)
                            target.DataSourceId = targetNetworkId;
                    }
                    else
                    {
                        if (!source.Profile1Id.IsNull)
                            target.Profile1Id = source.Profile1Id;
                        if (!source.Profile2Id.IsNull)
                            target.Profile2Id = source.Profile2Id;
                        if (networkBand && !source.DataSourceId.IsNull)
                            target.DataSourceId = source.DataSourceId;
                    }
                    if (showLabels) target.ShowLabels = true;
                }
                if (top)
                {
                    if (targetRows.Count > 0) targetView.Bands.SetTopBandItems(targetRows);
                }
                else
                {
                    if (targetRows.Count > 0) targetView.Bands.SetBottomBandItems(targetRows);
                }
                return sourceRows.Count;
            }
            finally
            {
                sourceRows.Dispose();
                targetRows.Dispose();
            }
        }

        private static void ClearCollection(ProfileViewBandItemCollection rows)
        {
            if (rows == null) return;
            MethodInfo clear = rows.GetType().GetMethod(
                "Clear", BindingFlags.Public | BindingFlags.Instance,
                null, Type.EmptyTypes, null);
            if (clear != null)
            {
                clear.Invoke(rows, null);
                return;
            }
            MethodInfo removeAt = rows.GetType().GetMethod(
                "RemoveAt", BindingFlags.Public | BindingFlags.Instance,
                null, new[] { typeof(int) }, null);
            if (removeAt == null)
                throw new InvalidOperationException(
                    "Civil 3D 2023 does not expose a writable band-row clear operation.");
            for (int index = rows.Count - 1; index >= 0; index--)
                removeAt.Invoke(rows, new object[] { index });
        }

        private static void CopyWritableBandProperties(
            ProfileViewBandItem source,
            ProfileViewBandItem target)
        {
            foreach (PropertyInfo property in source.GetType().GetProperties(
                BindingFlags.Public | BindingFlags.Instance))
            {
                if (!property.CanRead || !property.CanWrite ||
                    property.GetIndexParameters().Length != 0 ||
                    string.Equals(property.Name, "BandStyleId", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(property.Name, "BandType", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(property.Name, "AlignmentId", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(property.Name, "Profile1Id", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(property.Name, "Profile2Id", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(property.Name, "DataSourceId", StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    PropertyInfo targetProperty = target.GetType().GetProperty(
                        property.Name, BindingFlags.Public | BindingFlags.Instance);
                    if (targetProperty == null || !targetProperty.CanWrite ||
                        targetProperty.PropertyType != property.PropertyType ||
                        targetProperty.GetIndexParameters().Length != 0)
                        continue;
                    object value = property.GetValue(source, null);
                    targetProperty.SetValue(target, value, null);
                }
                catch { }
            }
        }

        private static void CopyViewRangeProperties(
            CivilProfileView source,
            CivilProfileView target)
        {
            foreach (string name in new[]
            {
                "ElevationMin", "ElevationMax", "StationStart", "StationEnd",
                "ElevationRangeMode", "StationRangeMode",
                "SplitProfileView", "SplitDatum"
            })
            {
                try
                {
                    PropertyInfo from = source.GetType().GetProperty(
                        name, BindingFlags.Public | BindingFlags.Instance);
                    PropertyInfo to = target.GetType().GetProperty(
                        name, BindingFlags.Public | BindingFlags.Instance);
                    if (from == null || to == null || !from.CanRead || !to.CanWrite ||
                        from.PropertyType != to.PropertyType) continue;
                    to.SetValue(target, from.GetValue(source, null), null);
                }
                catch { }
            }
        }

        private static ObjectId MapProfile(
            ObjectId sourceProfileId,
            ObjectId targetAlignmentId,
            Transaction tr)
        {
            if (sourceProfileId.IsNull || targetAlignmentId.IsNull) return ObjectId.Null;
            CivilProfile source;
            CivilAlignment targetAlignment;
            try
            {
                source = tr.GetObject(sourceProfileId, OpenMode.ForRead, false) as CivilProfile;
                targetAlignment = tr.GetObject(targetAlignmentId, OpenMode.ForRead, false) as CivilAlignment;
            }
            catch { return ObjectId.Null; }
            if (source == null || targetAlignment == null) return ObjectId.Null;
            if (source.AlignmentId == targetAlignmentId) return sourceProfileId;
            foreach (ObjectId id in targetAlignment.GetProfileIds())
            {
                try
                {
                    CivilProfile candidate = tr.GetObject(id, OpenMode.ForRead, false) as CivilProfile;
                    if (candidate != null &&
                        string.Equals(candidate.Name, source.Name,
                            StringComparison.CurrentCultureIgnoreCase))
                        return id;
                }
                catch { }
            }
            return ObjectId.Null;
        }

        private static bool TrySetObjectId(object target, string name, ObjectId value)
        {
            if (target == null || value.IsNull) return false;
            try
            {
                PropertyInfo property = target.GetType().GetProperty(
                    name, BindingFlags.Public | BindingFlags.Instance);
                if (property == null || !property.CanWrite ||
                    property.PropertyType != typeof(ObjectId)) return false;
                property.SetValue(target, value, null);
                return true;
            }
            catch { return false; }
        }

        private static ObjectId PromptProfileView(Editor editor, string message)
        {
            var options = new PromptEntityOptions(message);
            PromptEntityResult result = editor.GetEntity(options);
            if (result.Status != PromptStatus.OK) return ObjectId.Null;
            try
            {
                using (Transaction tr =
                    result.ObjectId.Database.TransactionManager.StartTransaction())
                    return tr.GetObject(result.ObjectId, OpenMode.ForRead, false)
                        is CivilProfileView ? result.ObjectId : ObjectId.Null;
            }
            catch { return ObjectId.Null; }
        }

        private static List<ObjectId> PromptProfileViews(
            Document document,
            string message,
            ObjectId exclude)
        {
            PromptSelectionResult selection = document.Editor.SelectImplied();
            if (selection.Status != PromptStatus.OK ||
                selection.Value == null ||
                selection.Value.Count == 0)
            {
                selection = document.Editor.GetSelection(new PromptSelectionOptions
                {
                    MessageForAdding = message,
                    AllowDuplicates = false,
                    RejectObjectsFromNonCurrentSpace = true
                });
            }
            if (selection.Status != PromptStatus.OK || selection.Value == null)
                return new List<ObjectId>();

            var result = new List<ObjectId>();
            using (Transaction tr = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in selection.Value.GetObjectIds().Distinct())
                {
                    if (!exclude.IsNull && id == exclude) continue;
                    try
                    {
                        CivilProfileView view = tr.GetObject(
                            id, OpenMode.ForRead, false) as CivilProfileView;
                        if (view != null && !view.IsReferenceObject) result.Add(id);
                    }
                    catch { }
                }
            }
            return result;
        }

        private static List<ProfileChoice> ReadProfiles(
            Database database,
            CivilDocument civil)
        {
            var result = new List<ProfileChoice>();
            using (Transaction tr = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId alignmentId in civil.GetAlignmentIds())
                {
                    CivilAlignment alignment;
                    try { alignment = tr.GetObject(alignmentId, OpenMode.ForRead, false) as CivilAlignment; }
                    catch { continue; }
                    if (alignment == null) continue;
                    foreach (ObjectId profileId in alignment.GetProfileIds())
                    {
                        try
                        {
                            CivilProfile profile = tr.GetObject(profileId, OpenMode.ForRead, false) as CivilProfile;
                            if (profile == null) continue;
                            result.Add(new ProfileChoice
                            {
                                Id = profileId,
                                Display = alignment.Name + " | " + profile.Name +
                                    " [" + profileId.Handle.ToString() + "]"
                            });
                        }
                        catch { }
                    }
                }
            }
            return result.OrderBy(item => item.Display,
                StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        private static List<NetworkChoice> ReadNetworks(
            Database database,
            CivilDocument civil)
        {
            var result = new List<NetworkChoice>();
            using (Transaction tr = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in civil.GetPipeNetworkIds())
                {
                    try
                    {
                        CivilNetwork network = tr.GetObject(id, OpenMode.ForRead, false) as CivilNetwork;
                        if (network == null) continue;
                        result.Add(new NetworkChoice
                        {
                            Id = id,
                            Display = network.Name + " [" + id.Handle.ToString() + "]"
                        });
                    }
                    catch { }
                }
            }
            return result.OrderBy(item => item.Display,
                StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        private static ProfileChoice FindProfile(
            IEnumerable<ProfileChoice> values,
            string display)
        {
            return values.FirstOrDefault(item =>
                string.Equals(item.Display, display,
                    StringComparison.CurrentCultureIgnoreCase));
        }

        private static NetworkChoice FindNetwork(
            IEnumerable<NetworkChoice> values,
            string display)
        {
            return values.FirstOrDefault(item =>
                string.Equals(item.Display, display,
                    StringComparison.CurrentCultureIgnoreCase));
        }

        private static bool IsYes(string value)
        {
            return string.Equals(value, "Yes", StringComparison.OrdinalIgnoreCase);
        }

        private sealed class ProfileViewEditMatchSession
        {
            internal Database Database;
            internal ObjectId SourceId;
            internal List<ObjectId> TargetIds;
        }

        private sealed class ProfileChoice
        {
            internal ObjectId Id;
            internal string Display;
        }

        private sealed class NetworkChoice
        {
            internal ObjectId Id;
            internal string Display;
        }
    }
}

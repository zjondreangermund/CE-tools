using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using Autodesk.Civil.DatabaseServices.Styles;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivilNetwork = Autodesk.Civil.DatabaseServices.Network;
using CivilPipe = Autodesk.Civil.DatabaseServices.Pipe;
using CivilProfile = Autodesk.Civil.DatabaseServices.Profile;
using CivilProfileView = Autodesk.Civil.DatabaseServices.ProfileView;

[assembly: CommandClass(typeof(CETools.Civil3D.SewerLongSectionBandCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Sewer-specific long-section band application.  Civil 3D 2023 can accept a
    /// ProfileViewBandSetStyle during ProfileView.Create without materialising the
    /// native band rows.  It can also leave pipe-network rows without a DataSourceId.
    /// This command imports/materialises the selected band set per view, binds the
    /// existing-ground profile and gravity network, enables labels and verifies the
    /// committed native collections.
    /// </summary>
    public sealed class SewerLongSectionBandCommands
    {
        private const string SewerProfileApp = "CE_TOOLS_SEWPROFILE";

        [CommandMethod("CE_TOOLS", "CE_SEWBANDLABELS",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void ApplySewerLongSectionBands()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civil = CivilApplication.ActiveDocument;
            if (document == null || civil == null) return;

            List<ObjectId> viewIds = SelectProfileViews(document);
            if (viewIds.Count == 0)
            {
                document.Editor.WriteMessage(
                    "\nCE_SEWBANDLABELS: no editable Civil 3D profile views selected.");
                return;
            }

            IList<string> bandSets = CivilStyleCatalogV2.ReadNames(
                document.Database, civil, "Profile View Band Set Style");
            IList<string> viewStyles = CivilStyleCatalogV2.ReadNames(
                document.Database, civil, "Profile View Style");
            if (bandSets.Count == 0)
            {
                document.Editor.WriteMessage(
                    "\nCE_SEWBANDLABELS: this drawing contains no profile-view band-set styles.");
                return;
            }

            SewerProductionSettings saved = SewerProductionSettings.Read(document.Database);
            string defaultBand = MatchOrFirst(bandSets, saved.ProfileViewBandSetStyle);
            const string keepStyle = "<Keep current profile-view style>";
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Sewer Long Section Bands",
                "Apply the selected Civil 3D sewer band set to every selected profile view. CE Tools materialises all native rows, binds profile-data rows to the existing-ground profile and Pipe Network rows to the matching gravity network, then enables labels.");
            model.AddChoice(
                "BandSet", "01 Long section", "Sewer band set", defaultBand,
                "Choose the native Civil 3D band-set style used for Reference, Distance, Ground Level, Pipe Invert Level, Slope/Length, Depth to Invert and other configured sewer rows.",
                bandSets);
            model.AddChoice(
                "ViewStyle", "01 Long section", "Profile-view style",
                string.IsNullOrWhiteSpace(saved.ProfileViewStyle) ? keepStyle : saved.ProfileViewStyle,
                "Keep the current view style or apply one installed sewer profile-view style to all selected views.",
                new[] { keepStyle }.Concat(viewStyles).Distinct(StringComparer.CurrentCultureIgnoreCase).ToList());
            model.AddChoice(
                "Remember", "02 Defaults", "Save as sewer-production defaults", "Yes",
                "Save the selected band set and profile-view style for future CE_SEWPROFILE long sections.",
                new[] { "Yes", "No" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            string bandName = model.Text("BandSet");
            string viewStyleName = model.Text("ViewStyle");
            ObjectId bandSetId;
            ObjectId viewStyleId = ObjectId.Null;
            using (Transaction tr = document.Database.TransactionManager.StartTransaction())
            {
                string actual;
                bandSetId = CivilStyleCatalogV2.ResolveStyleId(
                    document.Database, civil, "Profile View Band Set Style",
                    bandName, tr, out actual);
                if (!string.Equals(viewStyleName, keepStyle, StringComparison.OrdinalIgnoreCase))
                {
                    string actualView;
                    viewStyleId = CivilStyleCatalogV2.ResolveStyleId(
                        document.Database, civil, "Profile View Style",
                        viewStyleName, tr, out actualView);
                }
            }

            if (string.Equals(model.Text("Remember"), "Yes", StringComparison.OrdinalIgnoreCase))
            {
                saved.ProfileViewBandSetStyle = bandName;
                if (!viewStyleId.IsNull) saved.ProfileViewStyle = viewStyleName;
                saved.Write(document.Database);
            }

            int completed = 0;
            int rows = 0;
            int linked = 0;
            int labels = 0;
            int missingNetwork = 0;
            int missingGround = 0;
            int failed = 0;

            using (DocumentLock documentLock = document.LockDocument())
            {
                foreach (ObjectId viewId in viewIds)
                {
                    try
                    {
                        SewerLongSectionContext context = SewerLongSectionBandService.ResolveContext(
                            document.Database, civil, viewId);
                        if (context.NetworkId.IsNull) missingNetwork++;
                        if (context.GroundProfileId.IsNull) missingGround++;

                        SewerBandApplyResult result = SewerLongSectionBandService.Apply(
                            document.Database,
                            viewId,
                            bandSetId,
                            viewStyleId,
                            context.GroundProfileId,
                            context.NetworkId);
                        completed++;
                        rows += result.BandRows;
                        linked += result.LinkedSources;
                        labels += result.LabelsEnabled;
                    }
                    catch (System.Exception exception)
                    {
                        failed++;
                        document.Editor.WriteMessage(
                            "\nCE_SEWBANDLABELS could not finish profile view {0}: {1}",
                            viewId.Handle, exception.Message);
                    }
                    try
                    {
                        document.Database.TransactionManager.QueueForGraphicsFlush();
                        document.Editor.Regen();
                    }
                    catch { }
                }
            }

            try
            {
                document.Editor.SetImpliedSelection(viewIds.ToArray());
                document.Database.TransactionManager.QueueForGraphicsFlush();
                document.Editor.Regen();
                AcApplication.UpdateScreen();
            }
            catch { }

            document.Editor.WriteMessage(
                "\nCE_SEWBANDLABELS complete. Profile views={0}; native band rows={1}; verified source links={2}; labels enabled={3}; missing network source={4}; missing ground profile={5}; failed={6}.",
                completed, rows, linked, labels, missingNetwork, missingGround, failed);
            if (missingNetwork > 0)
                document.Editor.WriteMessage(
                    "\nOne or more views could not resolve a gravity-network source. CE-generated sewer views resolve from their CE_TOOLS_SEWPROFILE tag; other views are matched to the network whose pipes follow the profile-view alignment.");
        }

        private static List<ObjectId> SelectProfileViews(Document document)
        {
            PromptSelectionResult selection = document.Editor.SelectImplied();
            if (selection.Status != PromptStatus.OK ||
                selection.Value == null ||
                selection.Value.Count == 0)
            {
                selection = document.Editor.GetSelection(new PromptSelectionOptions
                {
                    MessageForAdding = "\nSelect multiple sewer profile views to receive the long-section band set: ",
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
                    try
                    {
                        CivilProfileView view = tr.GetObject(id, OpenMode.ForRead, false) as CivilProfileView;
                        if (view != null && !view.IsReferenceObject) result.Add(id);
                    }
                    catch { }
                }
            }
            return result;
        }

        private static string MatchOrFirst(IList<string> values, string requested)
        {
            if (values == null || values.Count == 0) return string.Empty;
            string match = values.FirstOrDefault(item =>
                string.Equals(item, requested, StringComparison.CurrentCultureIgnoreCase));
            return string.IsNullOrWhiteSpace(match) ? values[0] : match;
        }
    }

    internal static class SewerLongSectionBandService
    {
        private const string SewerProfileApp = "CE_TOOLS_SEWPROFILE";

        internal static SewerBandApplyResult Apply(
            Database database,
            ObjectId viewId,
            ObjectId bandSetStyleId,
            ObjectId viewStyleId,
            ObjectId groundProfileId,
            ObjectId networkId)
        {
            if (database == null || viewId.IsNull || bandSetStyleId.IsNull)
                throw new ArgumentException("A profile view and sewer band-set style are required.");

            int materializedRows;
            // Import one view per transaction. Civil 3D 2023 may report a style
            // assignment without creating the native top/bottom rows until the
            // profile view has committed.
            using (Transaction import = database.TransactionManager.StartTransaction())
            {
                CivilProfileView view = import.GetObject(
                    viewId, OpenMode.ForWrite, false) as CivilProfileView;
                if (view == null || view.IsReferenceObject)
                    throw new InvalidOperationException("The selected object is not an editable profile view.");

                if (!viewStyleId.IsNull)
                    SetObjectId(view, viewStyleId, "StyleId");

                ProfileViewBandSetStyle style = import.GetObject(
                    bandSetStyleId, OpenMode.ForRead, false) as ProfileViewBandSetStyle;
                if (style == null)
                    throw new InvalidOperationException("The selected sewer band-set style is unavailable.");

                view.Bands.ImportBandSetStyle(bandSetStyleId);
                materializedRows = September14AlignmentBandStyleCommands.EnsureImportedBandRows(
                    view, style);
                if (materializedRows == 0)
                    throw new InvalidOperationException(
                        "Civil 3D did not materialise any native rows from the selected sewer band set.");

                try { view.RecordGraphicsModified(true); } catch { }
                import.Commit();
            }

            int linked;
            int found;
            int labelsEnabled;
            using (Transaction bind = database.TransactionManager.StartTransaction())
            {
                CivilProfileView view = bind.GetObject(
                    viewId, OpenMode.ForWrite, false) as CivilProfileView;
                if (view == null)
                    throw new InvalidOperationException("The profile view became unavailable after band import.");

                linked = ProfileViewBandDataBinder.Bind(
                    view,
                    groundProfileId,
                    ObjectId.Null,
                    networkId);

                // Field guard: every native Pipe Network row from the selected
                // sewer band-set style must carry the gravity-network DataSource.
                // Civil 3D 2023 can leave only the first row linked even when the
                // remaining rows are present. Re-assert and verify the complete
                // top/bottom collections before regeneration.
                int networkRowsLinked = EnsureEveryNetworkBandSource(
                    view,
                    networkId);
                linked = Math.Max(linked, networkRowsLinked);

                ProfileViewBandPersistence.EnableLabels(view, out found, out labelsEnabled);
                try { view.RecordGraphicsModified(true); } catch { }
                bind.Commit();
            }

            int persistedRows;
            using (Transaction verify = database.TransactionManager.StartTransaction())
            {
                CivilProfileView view = verify.GetObject(
                    viewId, OpenMode.ForRead, false) as CivilProfileView;
                persistedRows = CountRows(view);
                if (persistedRows == 0)
                    throw new InvalidOperationException(
                        "The sewer band rows disappeared after the import transaction committed.");

                int unlinkedNetworkRows = CountUnlinkedNetworkRows(
                    view,
                    networkId);
                if (!networkId.IsNull && unlinkedNetworkRows > 0)
                    throw new InvalidOperationException(
                        unlinkedNetworkRows.ToString(CultureInfo.InvariantCulture) +
                        " Pipe Network band row(s) still have a blank/wrong Data Source after the repair.");
            }

            return new SewerBandApplyResult
            {
                BandRows = persistedRows,
                LinkedSources = linked,
                LabelsEnabled = labelsEnabled,
                LabelsFound = found
            };
        }

        internal static SewerLongSectionContext ResolveContext(
            Database database,
            CivilDocument civil,
            ObjectId viewId)
        {
            var result = new SewerLongSectionContext();
            if (database == null || civil == null || viewId.IsNull) return result;

            using (Transaction tr = database.TransactionManager.StartTransaction())
            {
                CivilProfileView view = tr.GetObject(
                    viewId, OpenMode.ForRead, false) as CivilProfileView;
                if (view == null) return result;
                result.AlignmentId = view.AlignmentId;
                result.NetworkId = ResolveTaggedNetwork(database, view);
                result.GroundProfileId = ResolveGroundProfile(tr, view.AlignmentId);
                if (result.NetworkId.IsNull && !view.AlignmentId.IsNull)
                {
                    CivilAlignment alignment = tr.GetObject(
                        view.AlignmentId, OpenMode.ForRead, false) as CivilAlignment;
                    result.NetworkId = ResolveNetworkByAlignment(civil, tr, alignment);
                }
            }
            return result;
        }

        private static ObjectId ResolveTaggedNetwork(Database database, CivilProfileView view)
        {
            try
            {
                using (ResultBuffer data = view.GetXDataForApplication(SewerProfileApp))
                {
                    if (data == null) return ObjectId.Null;
                    string branchKey = data.AsArray()
                        .Where(value => value.TypeCode == (int)DxfCode.ExtendedDataAsciiString)
                        .Select(value => Convert.ToString(value.Value, CultureInfo.InvariantCulture))
                        .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value) && value.Contains("|"));
                    if (string.IsNullOrWhiteSpace(branchKey)) return ObjectId.Null;
                    string handleText = branchKey.Split('|')[0];
                    long handleValue;
                    if (!long.TryParse(handleText, NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture, out handleValue))
                        return ObjectId.Null;
                    ObjectId id = database.GetObjectId(false, new Handle(handleValue), 0);
                    return id.IsNull || id.IsErased ? ObjectId.Null : id;
                }
            }
            catch { return ObjectId.Null; }
        }

        private static ObjectId ResolveGroundProfile(Transaction tr, ObjectId alignmentId)
        {
            if (alignmentId.IsNull) return ObjectId.Null;
            CivilAlignment alignment;
            try { alignment = tr.GetObject(alignmentId, OpenMode.ForRead, false) as CivilAlignment; }
            catch { return ObjectId.Null; }
            if (alignment == null) return ObjectId.Null;

            var profiles = new List<Tuple<ObjectId, int>>();
            foreach (ObjectId id in alignment.GetProfileIds())
            {
                try
                {
                    CivilProfile profile = tr.GetObject(id, OpenMode.ForRead, false) as CivilProfile;
                    if (profile == null) continue;
                    string name = profile.Name ?? string.Empty;
                    int score = 0;
                    if (name.IndexOf("-EG-", StringComparison.OrdinalIgnoreCase) >= 0) score += 100;
                    if (name.IndexOf("EXIST", StringComparison.OrdinalIgnoreCase) >= 0) score += 60;
                    if (name.IndexOf("GROUND", StringComparison.OrdinalIgnoreCase) >= 0) score += 60;
                    if (name.IndexOf("NGL", StringComparison.OrdinalIgnoreCase) >= 0) score += 50;
                    object type = ReadProperty(profile, "ProfileType");
                    if (type != null &&
                        Convert.ToString(type, CultureInfo.InvariantCulture)
                            .IndexOf("SURFACE", StringComparison.OrdinalIgnoreCase) >= 0)
                        score += 80;
                    profiles.Add(Tuple.Create(id, score));
                }
                catch { }
            }
            return profiles
                .OrderByDescending(item => item.Item2)
                .Select(item => item.Item1)
                .FirstOrDefault();
        }

        private static ObjectId ResolveNetworkByAlignment(
            CivilDocument civil,
            Transaction tr,
            CivilAlignment alignment)
        {
            if (alignment == null) return ObjectId.Null;
            ObjectId best = ObjectId.Null;
            int bestScore = 0;

            foreach (ObjectId networkId in civil.GetPipeNetworkIds())
            {
                CivilNetwork network;
                try { network = tr.GetObject(networkId, OpenMode.ForRead, false) as CivilNetwork; }
                catch { continue; }
                if (network == null) continue;

                int score = 0;
                try
                {
                    if (network.ReferenceAlignmentId == alignment.ObjectId) score += 1000;
                }
                catch { }

                foreach (ObjectId pipeId in network.GetPipeIds())
                {
                    try
                    {
                        CivilPipe pipe = tr.GetObject(pipeId, OpenMode.ForRead, false) as CivilPipe;
                        if (pipe == null) continue;
                        if (OnAlignment(alignment, pipe.StartPoint) &&
                            OnAlignment(alignment, pipe.EndPoint))
                            score += 10;
                    }
                    catch { }
                }
                if (score > bestScore)
                {
                    bestScore = score;
                    best = networkId;
                }
            }
            return bestScore > 0 ? best : ObjectId.Null;
        }

        private static bool OnAlignment(CivilAlignment alignment, Point3d point)
        {
            try
            {
                double station = 0.0;
                double offset = 0.0;
                alignment.StationOffset(point.X, point.Y, ref station, ref offset);
                return station >= alignment.StartingStation - 0.05 &&
                       station <= alignment.EndingStation + 0.05 &&
                       Math.Abs(offset) <= 1.0;
            }
            catch { return false; }
        }

        private static int CountRows(CivilProfileView view)
        {
            if (view == null) return 0;
            int total = 0;
            using (ProfileViewBandItemCollection top = view.Bands.GetTopBandItems())
                total += top.Count;
            using (ProfileViewBandItemCollection bottom = view.Bands.GetBottomBandItems())
                total += bottom.Count;
            return total;
        }

        private static bool SetObjectId(object target, ObjectId id, string propertyName)
        {
            try
            {
                PropertyInfo property = target.GetType().GetProperty(
                    propertyName, BindingFlags.Public | BindingFlags.Instance);
                if (property == null || !property.CanWrite ||
                    property.PropertyType != typeof(ObjectId))
                    return false;
                property.SetValue(target, id, null);
                return true;
            }
            catch { return false; }
        }

        private static object ReadProperty(object target, string propertyName)
        {
            if (target == null) return null;
            try
            {
                PropertyInfo property = target.GetType().GetProperty(
                    propertyName, BindingFlags.Public | BindingFlags.Instance);
                return property == null || !property.CanRead
                    ? null
                    : property.GetValue(target, null);
            }
            catch { return null; }
        }
    }

    internal sealed class SewerLongSectionContext
    {
        internal ObjectId AlignmentId = ObjectId.Null;
        internal ObjectId GroundProfileId = ObjectId.Null;
        internal ObjectId NetworkId = ObjectId.Null;
    }

    internal sealed class SewerBandApplyResult
    {
        internal int BandRows;
        internal int LinkedSources;
        internal int LabelsFound;
        internal int LabelsEnabled;
    }
}

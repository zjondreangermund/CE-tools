using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
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

                            ObjectId surfaceProfileId;
                            ObjectId designProfileId;
                            ResolveRoadProfiles(
                                alignment,
                                transaction,
                                out surfaceProfileId,
                                out designProfileId);
                            bandItems += ProfileViewBandDataBinder.Bind(
                                profileView,
                                surfaceProfileId,
                                designProfileId,
                                ObjectId.Null);
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
            out ObjectId surfaceProfileId,
            out ObjectId designProfileId)
        {
            surfaceProfileId = ObjectId.Null;
            designProfileId = ObjectId.Null;
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

                if (designProfileId.IsNull &&
                    (identity.Contains("-FG") ||
                     identity.Contains("FINAL") ||
                     identity.Contains("DESIGN")))
                {
                    designProfileId = profileId;
                    continue;
                }

                if (surfaceProfileId.IsNull &&
                    (identity.Contains("NGL") ||
                     identity.Contains("EG") ||
                     identity.Contains("EXIST") ||
                     identity.Contains("SURFACE")))
                {
                    surfaceProfileId = profileId;
                }
            }

            if (surfaceProfileId.IsNull || designProfileId.IsNull)
            {
                foreach (ObjectId profileId in alignment.GetProfileIds())
                {
                    if (surfaceProfileId.IsNull && profileId != designProfileId)
                        surfaceProfileId = profileId;
                    else if (designProfileId.IsNull && profileId != surfaceProfileId)
                        designProfileId = profileId;
                }
            }
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

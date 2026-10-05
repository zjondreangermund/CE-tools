using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivilProfile = Autodesk.Civil.DatabaseServices.Profile;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

[assembly: CommandClass(typeof(CETools.Civil3D.RoadEdgeLevelProfileCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Creates sampled left/right road-edge level profiles from the matching
    /// TOP-RD-* corridor surface at a user-specified half width. The standard
    /// road profile-view finalizer already recognises LEFT-EDGE / RIGHT-EDGE
    /// profile names and binds them into the left/right edge-level band rows.
    /// </summary>
    public sealed class RoadEdgeLevelProfileCommands
    {
        private const string LayerName = "CE-ROAD-EDGE-PROFILES";

        [CommandMethod(
            "CE_TOOLS",
            "CE_ROADEDGELEVELS",
            CommandFlags.Modal |
            CommandFlags.UsePickSet |
            CommandFlags.Redraw)]
        public void CreateRoadEdgeLevelProfiles()
        {
            Document document =
                AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civilDocument =
                CivilApplication.ActiveDocument;
            if (document == null || civilDocument == null)
                return;

            RoadProductionSettings roadSettings =
                RoadProductionSettings.Read(document.Database);

            var model = new ProductionSettingsDialogModel(
                "CE Tools - Left / Right Road Edge Levels",
                "Create left and right edge-level profiles at a specified road half-width by sampling the matching TOP-RD road surface. The profiles are named LEFT-EDGE / RIGHT-EDGE so the road band rows use them automatically.");
            model.AddChoice(
                "Scope",
                "01 Roads",
                "Road alignment scope",
                "All road alignments",
                "Process every CE/RD road alignment or multiple selected road alignments.",
                new[]
                {
                    "All road alignments",
                    "Selected road alignments"
                });
            model.AddPositiveDouble(
                "HalfWidth",
                "02 Edge levels",
                "Left/right edge width from centreline",
                roadSettings.EdgeLevelHalfWidth,
                "Horizontal offset from the road centreline. Left uses -width and right uses +width.");
            model.AddPositiveDouble(
                "SampleInterval",
                "02 Edge levels",
                "Profile sampling interval",
                roadSettings.EdgeLevelSampleInterval,
                "Station spacing used to sample the TOP-RD surface. Start/end stations are always included.");
            model.AddChoice(
                "RefreshBands",
                "03 Finish",
                "Refresh road profile views and band data",
                "Yes",
                "Run CE_ROADPROFILEVIEWFINAL after creating the edge profiles so LEFT EDGE / RIGHT EDGE band rows point to the new profiles.",
                new[] { "Yes", "No" });
            if (!DisciplineWorkflowDialogs.EditSettings(model))
                return;

            double halfWidth =
                Math.Max(0.001, model.Double(
                    "HalfWidth",
                    roadSettings.EdgeLevelHalfWidth));
            double interval =
                Math.Max(0.10, model.Double(
                    "SampleInterval",
                    roadSettings.EdgeLevelSampleInterval));
            roadSettings.EdgeLevelHalfWidth = halfWidth;
            roadSettings.EdgeLevelSampleInterval = interval;

            HashSet<ObjectId> selectedIds = null;
            if (string.Equals(
                    model.Text("Scope"),
                    "Selected road alignments",
                    StringComparison.OrdinalIgnoreCase))
            {
                PromptSelectionResult selection =
                    document.Editor.SelectImplied();
                if (selection.Status != PromptStatus.OK ||
                    selection.Value == null ||
                    selection.Value.Count == 0)
                {
                    selection = document.Editor.GetSelection(
                        new PromptSelectionOptions
                        {
                            MessageForAdding =
                                "\nSelect multiple road alignments for left/right edge levels: ",
                            AllowDuplicates = false,
                            RejectObjectsFromNonCurrentSpace = true
                        });
                }
                document.Editor.SetImpliedSelection(
                    new ObjectId[0]);
                if (selection.Status != PromptStatus.OK ||
                    selection.Value == null)
                    return;
                selectedIds = new HashSet<ObjectId>(
                    selection.Value.GetObjectIds());
            }

            int roads = 0;
            int profiles = 0;
            int sampledPoints = 0;
            var warnings = new List<string>();

            try
            {
                using (DocumentLock documentLock =
                    document.LockDocument())
                using (Transaction transaction =
                    document.Database.TransactionManager.StartTransaction())
                {
                    string actualProfileStyle;
                    string actualLabelSet;
                    ObjectId profileStyleId =
                        CivilStyleCatalogV2.ResolveStyleId(
                            document.Database,
                            civilDocument,
                            "Profile Style",
                            roadSettings.ProfileStyle,
                            transaction,
                            out actualProfileStyle);
                    ObjectId profileLabelSetId =
                        CivilStyleCatalogV2.ResolveStyleId(
                            document.Database,
                            civilDocument,
                            "Profile Label Set Style",
                            roadSettings.ProfileLabelSetStyle,
                            transaction,
                            out actualLabelSet);
                    if (profileStyleId.IsNull ||
                        profileLabelSetId.IsNull)
                        throw new InvalidOperationException(
                            "A valid road Profile Style and Profile Label Set Style are required. Set them in CE Road Project Settings first.");

                    ObjectId layerId = EnsureLayer(
                        document.Database,
                        transaction,
                        LayerName);

                    List<CivilSurface> topSurfaces =
                        civilDocument.GetSurfaceIds()
                            .Cast<ObjectId>()
                            .Select(id =>
                            {
                                try
                                {
                                    return transaction.GetObject(
                                        id,
                                        OpenMode.ForRead,
                                        false) as CivilSurface;
                                }
                                catch
                                {
                                    return null;
                                }
                            })
                            .Where(surface =>
                                surface != null &&
                                (surface.Name ?? string.Empty)
                                    .StartsWith(
                                        "TOP-RD-",
                                        StringComparison.OrdinalIgnoreCase))
                            .ToList();

                    foreach (ObjectId alignmentId in
                        civilDocument.GetAlignmentIds())
                    {
                        CivilAlignment alignment = null;
                        try
                        {
                            alignment = transaction.GetObject(
                                alignmentId,
                                OpenMode.ForRead,
                                false) as CivilAlignment;
                        }
                        catch { }
                        if (alignment == null ||
                            !IsRoadAlignment(alignment) ||
                            (selectedIds != null &&
                             !selectedIds.Contains(alignmentId)))
                            continue;

                        CivilSurface surface =
                            FindMatchingTopSurface(
                                alignment,
                                topSurfaces);
                        if (surface == null)
                        {
                            warnings.Add(
                                alignment.Name +
                                ": matching TOP-RD-* surface not found.");
                            continue;
                        }

                        roads++;
                        int leftPoints;
                        int rightPoints;
                        if (CreateOneEdgeProfile(
                                document.Database,
                                transaction,
                                alignment,
                                surface,
                                -halfWidth,
                                interval,
                                alignment.Name + "-LEFT-EDGE",
                                layerId,
                                profileStyleId,
                                profileLabelSetId,
                                out leftPoints))
                        {
                            profiles++;
                            sampledPoints += leftPoints;
                        }
                        else
                        {
                            warnings.Add(
                                alignment.Name +
                                ": LEFT-EDGE profile could not be created.");
                        }

                        if (CreateOneEdgeProfile(
                                document.Database,
                                transaction,
                                alignment,
                                surface,
                                halfWidth,
                                interval,
                                alignment.Name + "-RIGHT-EDGE",
                                layerId,
                                profileStyleId,
                                profileLabelSetId,
                                out rightPoints))
                        {
                            profiles++;
                            sampledPoints += rightPoints;
                        }
                        else
                        {
                            warnings.Add(
                                alignment.Name +
                                ": RIGHT-EDGE profile could not be created.");
                        }
                    }

                    roadSettings.ProfileStyle =
                        actualProfileStyle;
                    roadSettings.ProfileLabelSetStyle =
                        actualLabelSet;
                    transaction.Commit();
                }

                roadSettings.Write(document.Database);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_ROADEDGELEVELS failed. {0}",
                    exception.Message);
                return;
            }

            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_ROADEDGELEVELS complete. Roads={0}; edge profiles={1}; sampled points={2}; half-width={3:N3}; warnings={4}.",
                roads,
                profiles,
                sampledPoints,
                halfWidth,
                warnings.Count);
            foreach (string warning in warnings.Take(8))
                document.Editor.WriteMessage(
                    "\n  Warning: {0}",
                    warning);

            if (string.Equals(
                    model.Text("RefreshBands"),
                    "Yes",
                    StringComparison.OrdinalIgnoreCase))
            {
                document.SendStringToExecute(
                    "CE_ROADPROFILEVIEWFINAL ",
                    true,
                    false,
                    true);
            }
        }

        private static bool CreateOneEdgeProfile(
            Database database,
            Transaction transaction,
            CivilAlignment alignment,
            CivilSurface surface,
            double offset,
            double interval,
            string profileName,
            ObjectId layerId,
            ObjectId styleId,
            ObjectId labelSetId,
            out int pointsAdded)
        {
            pointsAdded = 0;
            if (database == null ||
                transaction == null ||
                alignment == null ||
                surface == null)
                return false;

            var existingProfileIds =
                new List<ObjectId>();
            foreach (ObjectId existingId in
                alignment.GetProfileIds())
                existingProfileIds.Add(existingId);

            foreach (ObjectId existingId in
                existingProfileIds)
            {
                CivilProfile existing = null;
                try
                {
                    existing = transaction.GetObject(
                        existingId,
                        OpenMode.ForWrite,
                        false) as CivilProfile;
                }
                catch { }
                if (existing != null &&
                    string.Equals(
                        existing.Name,
                        profileName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    existing.Erase();
                }
            }

            ObjectId profileId = CivilProfile.CreateByLayout(
                profileName,
                alignment.ObjectId,
                layerId,
                styleId,
                labelSetId);
            CivilProfile profile = transaction.GetObject(
                profileId,
                OpenMode.ForWrite,
                false) as CivilProfile;
            if (profile == null)
                return false;

            double start = alignment.StartingStation;
            double end = alignment.EndingStation;
            if (end < start)
            {
                double swap = start;
                start = end;
                end = swap;
            }

            var stations = new List<double> { start };
            for (double station = start + interval;
                 station < end - 1e-7;
                 station += interval)
                stations.Add(station);
            if (end > start + 1e-7)
                stations.Add(end);

            foreach (double station in stations)
            {
                double easting = 0.0;
                double northing = 0.0;
                try
                {
                    alignment.PointLocation(
                        station,
                        offset,
                        ref easting,
                        ref northing);
                    double elevation =
                        surface.FindElevationAtXY(
                            easting,
                            northing);
                    if (double.IsNaN(elevation) ||
                        double.IsInfinity(elevation))
                        continue;
                    profile.PVIs.AddPVI(
                        station,
                        elevation);
                    pointsAdded++;
                }
                catch { }
            }

            if (pointsAdded >= 2)
            {
                profile.Description =
                    "CE road edge level from " +
                    surface.Name +
                    " at offset " +
                    offset.ToString(
                        "N3",
                        CultureInfo.InvariantCulture);
                return true;
            }

            profile.Erase();
            pointsAdded = 0;
            return false;
        }

        private static CivilSurface FindMatchingTopSurface(
            CivilAlignment alignment,
            IEnumerable<CivilSurface> surfaces)
        {
            if (alignment == null)
                return null;
            string roadKey =
                RoadKey(alignment.Name);
            List<CivilSurface> values =
                (surfaces ?? Enumerable.Empty<CivilSurface>())
                    .Where(item => item != null)
                    .ToList();
            if (!string.IsNullOrWhiteSpace(roadKey))
            {
                CivilSurface exact = values.FirstOrDefault(
                    item => string.Equals(
                        RoadKey(item.Name),
                        roadKey,
                        StringComparison.OrdinalIgnoreCase));
                if (exact != null)
                    return exact;
            }
            return values.Count == 1
                ? values[0]
                : null;
        }

        private static string RoadKey(string value)
        {
            Match match = Regex.Match(
                value ?? string.Empty,
                @"RD[\s_-]*(?<number>\d+)",
                RegexOptions.IgnoreCase |
                RegexOptions.CultureInvariant);
            int number;
            return match.Success &&
                int.TryParse(
                    match.Groups["number"].Value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out number)
                ? "RD-" + number.ToString(
                    "00",
                    CultureInfo.InvariantCulture)
                : string.Empty;
        }

        private static bool IsRoadAlignment(
            CivilAlignment alignment)
        {
            if (alignment == null)
                return false;
            return alignment.AlignmentType ==
                       AlignmentType.Centerline &&
                   (!string.IsNullOrWhiteSpace(
                        RoadKey(alignment.Name)) ||
                    (alignment.Description ?? string.Empty)
                        .IndexOf(
                            "CE road",
                            StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static ObjectId EnsureLayer(
            Database database,
            Transaction transaction,
            string name)
        {
            LayerTable layers = transaction.GetObject(
                database.LayerTableId,
                OpenMode.ForRead,
                false) as LayerTable;
            if (layers == null)
                return ObjectId.Null;
            if (layers.Has(name))
                return layers[name];

            layers.UpgradeOpen();
            var layer = new LayerTableRecord
            {
                Name = name
            };
            ObjectId id = layers.Add(layer);
            transaction.AddNewlyCreatedDBObject(
                layer,
                true);
            return id;
        }
    }
}

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
            model.AddChoice(
                "WidthSource",
                "02 Edge levels",
                "Left/right width source",
                "Active corridor assembly widths",
                "Read station-varying left/right lane/road widths from the applied assemblies used by the active corridor, or use one manual equal half-width.",
                new[]
                {
                    "Active corridor assembly widths",
                    "Manual equal half-width"
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

            string widthSource =
                model.Text("WidthSource");
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

                        List<StationWidth> assemblyWidths =
                            string.Equals(
                                widthSource,
                                "Active corridor assembly widths",
                                StringComparison.OrdinalIgnoreCase)
                                ? ReadActiveCorridorWidths(
                                    civilDocument,
                                    transaction,
                                    alignment)
                                : new List<StationWidth>();
                        if (string.Equals(
                                widthSource,
                                "Active corridor assembly widths",
                                StringComparison.OrdinalIgnoreCase) &&
                            assemblyWidths.Count == 0)
                        {
                            warnings.Add(
                                alignment.Name +
                                ": no usable active-corridor assembly lane widths were found; manual half-width " +
                                halfWidth.ToString("N3", CultureInfo.InvariantCulture) +
                                " was used.");
                        }

                        roads++;
                        int leftPoints;
                        int rightPoints;
                        if (CreateOneEdgeProfile(
                                document.Database,
                                transaction,
                                alignment,
                                surface,
                                true,
                                halfWidth,
                                assemblyWidths,
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
                                false,
                                halfWidth,
                                assemblyWidths,
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
                "\nCE_ROADEDGELEVELS complete. Roads={0}; edge profiles={1}; sampled points={2}; width source={3}; manual fallback half-width={4:N3}; warnings={5}.",
                roads,
                profiles,
                sampledPoints,
                widthSource,
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
            bool leftSide,
            double manualHalfWidth,
            IList<StationWidth> assemblyWidths,
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
                    double offset =
                        ResolveEdgeOffset(
                            station,
                            leftSide,
                            manualHalfWidth,
                            assemblyWidths);
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
                    " using " +
                    ((assemblyWidths != null &&
                      assemblyWidths.Count > 0)
                        ? "active corridor assembly widths"
                        : "manual half-width " +
                          manualHalfWidth.ToString(
                              "N3",
                              CultureInfo.InvariantCulture)) +
                    " (" +
                    (leftSide ? "LEFT" : "RIGHT") +
                    ")";
                return true;
            }

            profile.Erase();
            pointsAdded = 0;
            return false;
        }

        private static List<StationWidth> ReadActiveCorridorWidths(
            CivilDocument civilDocument,
            Transaction transaction,
            CivilAlignment alignment)
        {
            var result = new List<StationWidth>();
            if (civilDocument == null ||
                transaction == null ||
                alignment == null)
                return result;

            foreach (ObjectId corridorId in
                civilDocument.CorridorCollection)
            {
                Corridor corridor = null;
                try
                {
                    corridor = transaction.GetObject(
                        corridorId,
                        OpenMode.ForRead,
                        false) as Corridor;
                }
                catch { }
                if (corridor == null)
                    continue;

                foreach (Baseline baseline in
                    corridor.Baselines)
                {
                    if (baseline == null ||
                        baseline.AlignmentId !=
                            alignment.ObjectId)
                        continue;

                    foreach (BaselineRegion region in
                        baseline.BaselineRegions)
                    {
                        if (region == null)
                            continue;
                        foreach (AppliedAssembly assembly in
                            region.AppliedAssemblies)
                        {
                            double station;
                            if (assembly == null ||
                                !TryAppliedAssemblyStation(
                                    assembly,
                                    out station))
                                continue;

                            var offsets =
                                new List<double>();
                            foreach (CalculatedLink link in
                                assembly.Links)
                            {
                                if (link == null ||
                                    !IsRoadLaneLink(
                                        link.CorridorCodes))
                                    continue;
                                foreach (CalculatedPoint point in
                                    link.CalculatedPoints)
                                {
                                    if (point == null)
                                        continue;
                                    offsets.Add(
                                        point
                                            .StationOffsetElevationToBaseline
                                            .Y);
                                }
                            }

                            double left = offsets
                                .Where(value => value < -1e-6)
                                .DefaultIfEmpty(0.0)
                                .Min();
                            double right = offsets
                                .Where(value => value > 1e-6)
                                .DefaultIfEmpty(0.0)
                                .Max();
                            if (left >= -1e-6 ||
                                right <= 1e-6)
                                continue;

                            result.Add(
                                new StationWidth
                                {
                                    Station = station,
                                    LeftOffset = left,
                                    RightOffset = right
                                });
                        }
                    }
                }
            }

            return result
                .GroupBy(item =>
                    Math.Round(item.Station, 4))
                .Select(group => group.First())
                .OrderBy(item => item.Station)
                .ToList();
        }

        private static bool TryAppliedAssemblyStation(
            AppliedAssembly assembly,
            out double station)
        {
            station = 0.0;
            if (assembly == null)
                return false;
            foreach (CalculatedPoint point in
                assembly.Points)
            {
                if (point == null)
                    continue;
                station =
                    point.StationOffsetElevationToBaseline.X;
                return true;
            }
            return false;
        }

        private static bool IsRoadLaneLink(
            CorridorCodeCollection codes)
        {
            if (codes == null)
                return false;
            var values = new List<string>();
            foreach (string code in codes)
            {
                if (!string.IsNullOrWhiteSpace(code))
                    values.Add(code);
            }
            string text =
                string.Join(" ", values)
                    .ToUpperInvariant();
            if (text.Contains("SIDEWALK") ||
                text.Contains("SHOULDER") ||
                text.Contains("SHLD") ||
                text.Contains("VERGE") ||
                text.Contains("DAYLIGHT") ||
                text.Contains("SLOPE") ||
                text.Contains("BATTER") ||
                text.Contains("KERB") ||
                text.Contains("CURB"))
                return false;
            return text.Contains("LANE") ||
                   text.Contains("PAVE") ||
                   text.Contains("ROAD") ||
                   text.Contains("TOP") ||
                   text.Contains("ETW");
        }

        private static double ResolveEdgeOffset(
            double station,
            bool leftSide,
            double manualHalfWidth,
            IList<StationWidth> widths)
        {
            if (widths == null ||
                widths.Count == 0)
                return leftSide
                    ? -manualHalfWidth
                    : manualHalfWidth;

            StationWidth first = widths[0];
            if (station <= first.Station)
                return leftSide
                    ? first.LeftOffset
                    : first.RightOffset;

            StationWidth last =
                widths[widths.Count - 1];
            if (station >= last.Station)
                return leftSide
                    ? last.LeftOffset
                    : last.RightOffset;

            for (int index = 0;
                 index < widths.Count - 1;
                 index++)
            {
                StationWidth a = widths[index];
                StationWidth b = widths[index + 1];
                if (station < a.Station ||
                    station > b.Station)
                    continue;

                double span =
                    b.Station - a.Station;
                double fraction =
                    span <= 1e-9
                        ? 0.0
                        : (station - a.Station) /
                          span;
                double av = leftSide
                    ? a.LeftOffset
                    : a.RightOffset;
                double bv = leftSide
                    ? b.LeftOffset
                    : b.RightOffset;
                return av +
                    (bv - av) *
                    fraction;
            }

            return leftSide
                ? last.LeftOffset
                : last.RightOffset;
        }

        private sealed class StationWidth
        {
            internal double Station { get; set; }
            internal double LeftOffset { get; set; }
            internal double RightOffset { get; set; }
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

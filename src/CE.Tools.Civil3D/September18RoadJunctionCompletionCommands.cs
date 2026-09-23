using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivilProfile = Autodesk.Civil.DatabaseServices.Profile;
using CivilProfileView = Autodesk.Civil.DatabaseServices.ProfileView;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;
using CivilFeatureLine = Autodesk.Civil.DatabaseServices.FeatureLine;

[assembly: CommandClass(typeof(CETools.Civil3D.September18RoadJunctionCompletionCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// September 18 road/junction completion batch.
    /// The commands keep Civil 3D source objects intact where possible and use
    /// reflected API calls for version-sensitive 2023/2024 members.
    /// </summary>
    public sealed class September18RoadJunctionCompletionCommands
    {
        private const string JunctionLayer = "CE-ROAD-JUNCTION";
        private const string JunctionApp = "CE_ROAD_JUNCTION";
        private const double StationTolerance = 0.011;

        [CommandMethod("CE_TOOLS", "CE_ROADALIGNREVERSEMULTI", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void ReverseMultipleRoadAlignments()
        {
            Document document = ActiveDocument();
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (document == null || civilDocument == null) return;

            List<ObjectId> alignmentIds = SelectObjects(
                document,
                "\nSelect road alignments to reverse: ",
                obj => obj is CivilAlignment);
            if (alignmentIds.Count == 0)
            {
                document.Editor.WriteMessage("\nCE_ROADALIGNREVERSEMULTI cancelled. No Civil 3D alignments selected.");
                return;
            }

            int reversed = 0;
            int profiles = 0;
            int profileViews = 0;
            int corridors = 0;
            int failed = 0;
            var rows = new List<IList<string>>();

            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in alignmentIds.Distinct())
                {
                    CivilAlignment alignment = SafeOpen<CivilAlignment>(transaction, id, OpenMode.ForWrite);
                    if (alignment == null) continue;

                    string name = alignment.Name;
                    double stationStart = alignment.StartingStation;
                    double stationEnd = alignment.EndingStation;
                    Dictionary<ObjectId, List<FinalProfilePvi>> finalProfilePvis =
                        CaptureFinalProfilePvis(alignment, transaction, null);
                    if (!TryInvoke(alignment, "Reverse"))
                    {
                        failed++;
                        rows.Add(new List<string> { name, "No", "0", "0", "0", "Civil 3D Reverse() API unavailable" });
                        continue;
                    }

                    reversed++;

                    // Civil 3D reverses NGL/surface profiles with the alignment,
                    // but layout/final road profiles can retain their original PVI
                    // station order. Re-map only those final design profiles so left,
                    // centre and right road elevations continue in the new direction.
                    foreach (KeyValuePair<ObjectId, List<FinalProfilePvi>> item in finalProfilePvis)
                    {
                        CivilProfile finalProfile = SafeOpen<CivilProfile>(
                            transaction,
                            item.Key,
                            OpenMode.ForWrite);
                        if (finalProfile != null)
                            ReverseFinalProfilePvis(
                                finalProfile,
                                item.Value,
                                stationStart,
                                stationEnd);
                    }

                    int localProfiles = 0;
                    int localViews = 0;
                    int localCorridors = 0;

                    foreach (ObjectId profileId in ReadObjectIds(InvokeReturning(alignment, "GetProfileIds")))
                    {
                        DBObject profile = SafeOpen<DBObject>(transaction, profileId, OpenMode.ForWrite);
                        if (profile == null) continue;
                        // Alignment reversal updates stationing natively. Force the design
                        // profile and graphics to refresh in the same transaction so the
                        // final road profile does not remain displayed in the old direction.
                        TryInvoke(profile, "Rebuild");
                        TryInvoke(profile, "Update");
                        Entity entity = profile as Entity;
                        if (entity != null) entity.RecordGraphicsModified(true);
                        localProfiles++;
                    }

                    foreach (ObjectId viewId in ReadObjectIds(InvokeReturning(alignment, "GetProfileViewIds")))
                    {
                        Entity view = SafeOpen<Entity>(transaction, viewId, OpenMode.ForWrite);
                        if (view == null) continue;
                        TryInvoke(view, "Rebuild");
                        view.RecordGraphicsModified(true);
                        localViews++;
                    }

                    foreach (DBObject corridor in ReadCorridors(civilDocument, transaction, OpenMode.ForWrite))
                    {
                        if (!CorridorUsesAlignment(corridor, id)) continue;
                        if (TryInvoke(corridor, "Rebuild")) localCorridors++;
                        Entity entity = corridor as Entity;
                        if (entity != null) entity.RecordGraphicsModified(true);
                    }

                    profiles += localProfiles;
                    profileViews += localViews;
                    corridors += localCorridors;
                    rows.Add(new List<string>
                    {
                        name,
                        "Yes",
                        localProfiles.ToString(CultureInfo.InvariantCulture),
                        localViews.ToString(CultureInfo.InvariantCulture),
                        localCorridors.ToString(CultureInfo.InvariantCulture),
                        "Reversed and refreshed"
                    });
                }
                transaction.Commit();
            }

            document.Editor.Regen();
            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - Reverse Multiple Road Alignments",
                string.Format(
                    CultureInfo.CurrentCulture,
                    "Alignments reversed={0}; profiles refreshed={1}; profile views refreshed={2}; corridors rebuilt={3}; failed={4}.",
                    reversed, profiles, profileViews, corridors, failed),
                new[] { "ALIGNMENT", "REVERSED", "PROFILES", "PROFILE VIEWS", "CORRIDORS", "STATUS" },
                rows,
                "CE ROAD ALIGNMENT REVERSE REGISTER");
        }

        [CommandMethod("CE_TOOLS", "CE_ROADPROFILEVIEWREVERSEMULTI", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void ReverseMultipleSelectedDesignProfileViews()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            List<ObjectId> viewIds = SelectObjects(
                document,
                "\nSelect design road profile views to reverse: ",
                obj => obj is CivilProfileView);
            if (viewIds.Count == 0)
            {
                document.Editor.WriteMessage("\nCE_ROADPROFILEVIEWREVERSEMULTI cancelled. No profile views selected.");
                return;
            }

            int changed = 0;
            int skipped = 0;
            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId viewId in viewIds.Distinct())
                {
                    CivilProfileView view = SafeOpen<CivilProfileView>(transaction, viewId, OpenMode.ForWrite);
                    if (view == null) { skipped++; continue; }
                    ObjectId alignmentId = ResolveAlignmentId(view);
                    if (alignmentId.IsNull)
                        alignmentId = ReadObjectId(ReadProperty(view, "ParentAlignment"));
                    CivilAlignment alignment = SafeOpen<CivilAlignment>(transaction, alignmentId, OpenMode.ForRead);
                    if (alignment == null) { skipped++; continue; }

                    Dictionary<ObjectId, List<FinalProfilePvi>> profiles =
                        CaptureFinalProfilePvis(alignment, transaction, view);
                    bool local = false;
                    foreach (KeyValuePair<ObjectId, List<FinalProfilePvi>> item in profiles)
                    {
                        CivilProfile profile = SafeOpen<CivilProfile>(transaction, item.Key, OpenMode.ForWrite);
                        if (profile == null) continue;
                        if (ReverseFinalProfilePvis(profile, item.Value,
                                alignment.StartingStation, alignment.EndingStation))
                            local = true;
                    }

                    if (TryInvoke(view, "Rebuild") || TryInvoke(view, "Update") ||
                        TryInvoke(view, "UpdateDisplay"))
                        local = true;
                    Entity entity = view as Entity;
                    if (entity != null) entity.RecordGraphicsModified(true);
                    if (local) changed++; else skipped++;
                }
                transaction.Commit();
            }

            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_ROADPROFILEVIEWREVERSEMULTI complete. Views changed={0}; skipped={1}.",
                changed, skipped);
        }

        [CommandMethod("CE_TOOLS", "CE_ROADSURFACENAMES", CommandFlags.Modal | CommandFlags.Redraw)]
        public void NormalizeRoadSurfaceNames()
        {
            Document document = ActiveDocument();
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (document == null || civilDocument == null) return;

            int renamed = 0;
            int corridors = 0;
            var rows = new List<IList<string>>();

            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (DBObject corridor in ReadCorridors(civilDocument, transaction, OpenMode.ForWrite))
                {
                    corridors++;
                    string road = ResolveRoadName(corridor, transaction);
                    if (string.IsNullOrWhiteSpace(road))
                    {
                        rows.Add(new List<string> { DisplayName(corridor), "-", "-", "Road number could not be resolved" });
                        continue;
                    }

                    object surfaces = ReadProperty(corridor, "CorridorSurfaces") ?? ReadProperty(corridor, "Surfaces");
                    int local = 0;
                    foreach (object surface in Enumerate(surfaces))
                    {
                        string oldName = Convert.ToString(ReadProperty(surface, "Name"), CultureInfo.CurrentCulture) ?? string.Empty;
                        string upper = oldName.ToUpperInvariant();
                        string newName = null;
                        if (upper.Contains("BOTTOM") || upper.Contains("DATUM") || upper.Contains("SUBGRADE"))
                            newName = "BOTTOM-" + road;
                        else if (upper.Contains("TOP") || upper.Contains("PAVE"))
                            newName = "TOP-" + road;
                        if (string.IsNullOrWhiteSpace(newName) || string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase))
                            continue;

                        if (TrySetProperty(surface, "Name", newName))
                        {
                            renamed++;
                            local++;
                        }
                    }
                    TryInvoke(corridor, "Rebuild");
                    rows.Add(new List<string>
                    {
                        DisplayName(corridor),
                        road,
                        local.ToString(CultureInfo.InvariantCulture),
                        local > 0 ? "Renamed" : "Already named / no TOP-BOTTOM surface"
                    });
                }
                transaction.Commit();
            }

            document.Editor.Regen();
            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - Road Surface Names",
                string.Format(CultureInfo.CurrentCulture, "Corridors={0}; corridor surfaces renamed={1}.", corridors, renamed),
                new[] { "CORRIDOR", "ROAD", "RENAMED", "STATUS" },
                rows,
                "CE ROAD SURFACE NAME REGISTER");
        }

        [CommandMethod("CE_TOOLS", "CE_ROADTOPBOTTOMPROFILE", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void AddRoadTopBottomSurfacesToProfileViews()
        {
            Document document = ActiveDocument();
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (document == null || civilDocument == null) return;

            List<ObjectId> viewIds = SelectObjects(
                document,
                "\nSelect road profile views that must show crossing TOP/BOTTOM road surfaces: ",
                obj => obj is CivilProfileView);
            if (viewIds.Count == 0)
            {
                document.Editor.WriteMessage("\nCE_ROADTOPBOTTOMPROFILE cancelled. No Civil 3D profile views selected.");
                return;
            }

            var model = new ProductionSettingsDialogModel(
                "CE Tools - Road TOP/BOTTOM Crossing Profiles",
                "Add TOP-RD-xx and BOTTOM-RD-xx surfaces as surface profiles to the alignments behind the selected profile views. Surface gaps remain gaps; only actual crossing coverage is drawn.");
            model.AddText("Roads", "01 Surfaces", "Road numbers", "ALL",
                "ALL uses every TOP-RD/BOTTOM-RD surface. Or enter comma-separated road names such as RD-01,RD-05.");
            model.AddText("Layer", "01 Surfaces", "Surface-profile layer", "CE-ROAD-SURFACE-PROFILES",
                "Layer for the generated TOP/BOTTOM surface profiles.");
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;
            HashSet<string> requestedRoads = ParseRoadFilter(model.Text("Roads"));

            int created = 0;
            int existing = 0;
            int failed = 0;
            var rows = new List<IList<string>>();

            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                List<CivilSurface> surfaces = new List<CivilSurface>();
                foreach (ObjectId surfaceId in civilDocument.GetSurfaceIds())
                {
                    CivilSurface surface = SafeOpen<CivilSurface>(transaction, surfaceId, OpenMode.ForRead);
                    if (surface == null) continue;
                    string name = surface.Name ?? string.Empty;
                    if (!IsRoadTopBottomSurface(name)) continue;
                    if (!RoadFilterMatches(name, requestedRoads)) continue;
                    surfaces.Add(surface);
                }

                foreach (ObjectId viewId in viewIds.Distinct())
                {
                    CivilProfileView view = SafeOpen<CivilProfileView>(transaction, viewId, OpenMode.ForWrite);
                    if (view == null) continue;
                    ObjectId alignmentId = ResolveAlignmentId(view);
                    if (alignmentId.IsNull) continue;
                    CivilAlignment alignment = SafeOpen<CivilAlignment>(transaction, alignmentId, OpenMode.ForRead);
                    if (alignment == null) continue;

                    ObjectId templateProfileId = ObjectId.Null;
                    DBObject templateProfile = null;
                    foreach (ObjectId candidateId in ReadObjectIds(InvokeReturning(alignment, "GetProfileIds")))
                    {
                        DBObject candidate = SafeOpen<DBObject>(
                            transaction, candidateId, OpenMode.ForRead);
                        if (candidate == null) continue;
                        ObjectId candidateStyle = ReadObjectId(ReadProperty(candidate, "StyleId"));
                        if (templateProfile == null || !candidateStyle.IsNull)
                        {
                            templateProfileId = candidateId;
                            templateProfile = candidate;
                        }
                        if (!candidateStyle.IsNull) break;
                    }
                    ObjectId templateLayerId = templateProfile is Entity
                        ? ((Entity)templateProfile).LayerId
                        : document.Database.Clayer;
                    ObjectId layerId = GetOrCreateLayer(
                        document.Database,
                        transaction,
                        model.Text("Layer"),
                        templateLayerId);
                    ObjectId templateStyleId = ReadObjectId(ReadProperty(templateProfile, "StyleId"));
                    ObjectId templateLabelSetId = ReadObjectId(ReadProperty(templateProfile, "LabelSetId"));
                    if (templateLabelSetId.IsNull)
                        templateLabelSetId = ReadObjectId(ReadProperty(templateProfile, "LabelSetStyleId"));
                    string templateStyleName = ReadObjectName(transaction, templateStyleId);
                    string templateLabelSetName = ReadObjectName(transaction, templateLabelSetId);
                    string resolvedStyleName;
                    string resolvedLabelSetName;
                    ObjectId styleId = CivilStyleCatalogV2.ResolveStyleId(
                        document.Database,
                        civilDocument,
                        "Profile Style",
                        templateStyleName,
                        transaction,
                        out resolvedStyleName);
                    ObjectId labelSetId = CivilStyleCatalogV2.ResolveStyleId(
                        document.Database,
                        civilDocument,
                        "Profile Label Set Style",
                        templateLabelSetName,
                        transaction,
                        out resolvedLabelSetName);

                    HashSet<string> existingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (ObjectId profileId in ReadObjectIds(InvokeReturning(alignment, "GetProfileIds")))
                    {
                        DBObject existingProfile = SafeOpen<DBObject>(transaction, profileId, OpenMode.ForRead);
                        string existingName = Convert.ToString(ReadProperty(existingProfile, "Name"), CultureInfo.CurrentCulture);
                        if (!string.IsNullOrWhiteSpace(existingName)) existingNames.Add(existingName);
                    }

                    foreach (CivilSurface surface in surfaces)
                    {
                        string profileName = SafeProfileName(surface.Name + " @ " + alignment.Name);
                        if (existingNames.Contains(profileName))
                        {
                            existing++;
                            rows.Add(new List<string> { view.Name, alignment.Name, surface.Name, profileName, "Already exists" });
                            continue;
                        }

                        try
                        {
                            ObjectId newProfileId = CreateSurfaceProfile(
                                profileName,
                                alignmentId,
                                surface.ObjectId,
                                layerId,
                                styleId,
                                labelSetId);
                            if (newProfileId.IsNull) throw new InvalidOperationException("Civil 3D returned no profile ObjectId.");
                            if (!TryInvoke(view, "AddProfile", newProfileId) &&
                                !TryInvoke(view, "AddProfileId", newProfileId))
                                throw new InvalidOperationException("The TOP/BOTTOM profile was created but could not be added to the selected profile view.");
                            created++;
                            existingNames.Add(profileName);
                            rows.Add(new List<string> { view.Name, alignment.Name, surface.Name, profileName, "Created" });
                        }
                        catch (System.Exception exception)
                        {
                            failed++;
                            rows.Add(new List<string> { view.Name, alignment.Name, surface.Name, profileName, "Failed: " + exception.Message });
                        }
                    }

                    TryInvoke(view, "Rebuild");
                    view.RecordGraphicsModified(true);
                }
                transaction.Commit();
            }

            document.Editor.Regen();
            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - Road TOP/BOTTOM Crossing Profiles",
                string.Format(CultureInfo.CurrentCulture, "Surface profiles created={0}; existing={1}; failed={2}.", created, existing, failed),
                new[] { "PROFILE VIEW", "ALIGNMENT", "SURFACE", "PROFILE", "STATUS" },
                rows,
                "CE ROAD TOP BOTTOM PROFILE REGISTER");
        }

        [CommandMethod("CE_TOOLS", "CE_ROADJUNCTIONFEATURELINESTOP", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void PasteSelectedJunctionFeatureLinesToRoadTopSurfaces()
        {
            Document document = ActiveDocument();
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (document == null || civilDocument == null) return;

            List<ObjectId> featureIds = SelectObjects(
                document,
                "\nSelect road-junction feature lines to paste to all road TOP surfaces: ",
                obj => obj is CivilFeatureLine);
            if (featureIds.Count == 0)
            {
                document.Editor.WriteMessage("\nCE_ROADJUNCTIONFEATURELINESTOP cancelled. No feature lines selected.");
                return;
            }

            int surfaces = 0;
            int draped = 0;
            int vertices = 0;
            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                List<CivilSurface> topSurfaces = new List<CivilSurface>();
                foreach (ObjectId surfaceId in civilDocument.GetSurfaceIds())
                {
                    CivilSurface surface = SafeOpen<CivilSurface>(transaction, surfaceId, OpenMode.ForWrite);
                    if (surface == null ||
                        !surface.Name.StartsWith("TOP-", StringComparison.OrdinalIgnoreCase))
                        continue;
                    topSurfaces.Add(surface);
                }

                foreach (ObjectId featureId in featureIds.Distinct())
                {
                    CivilFeatureLine line = SafeOpen<CivilFeatureLine>(
                        transaction, featureId, OpenMode.ForWrite);
                    if (line == null) continue;

                    foreach (CivilSurface surface in topSurfaces)
                    {
                        if (TryAssignFeatureLineElevations(line, surface))
                            draped++;
                        int local = 0;
                        try
                        {
                            foreach (Point3d point in line.GetPoints(FeatureLinePointType.AllPoints))
                            {
                                double elevation;
                                if (!TryFindSurfaceElevation(surface, point.X, point.Y, out elevation))
                                    continue;
                                if (TryAddSurfaceVertex(
                                    surface,
                                    new Point3d(point.X, point.Y, elevation)))
                                    local++;
                            }
                        }
                        catch { }

                        if (local > 0)
                        {
                            surfaces++;
                            vertices += local;
                            TryInvoke(surface, "Rebuild");
                            surface.RecordGraphicsModified(true);
                        }
                    }

                    line.RecordGraphicsModified(true);
                }
                transaction.Commit();
            }

            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_ROADJUNCTIONFEATURELINESTOP complete. Selected feature lines={0}; TOP surfaces processed={1}; feature lines draped={2}; vertices pasted={3}.",
                featureIds.Count, surfaces, draped, vertices);
        }

        [CommandMethod("CE_TOOLS", "CE_ROADTJUNCTIONASSEMBLYLIMITS", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void ApplyTJunctionAssemblyLimits()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            List<ObjectId> corridorIds = SelectObjects(
                document,
                "\nSelect corridors to trim at T-junction assembly limits: ",
                obj => obj != null && obj.GetType().Name.IndexOf("Corridor", StringComparison.OrdinalIgnoreCase) >= 0);
            if (corridorIds.Count == 0)
            {
                document.Editor.WriteMessage("\nCE_ROADTJUNCTIONASSEMBLYLIMITS cancelled. No corridors selected.");
                return;
            }

            var model = new ProductionSettingsDialogModel(
                "CE Tools - T-Junction Assembly Limits",
                "Use the magenta CE T-junction closure lines as assembly/region limits. The side-road corridor is split at the limit and the short terminal region between the junction endpoint and the road edge is removed. Cross-junction corridors are not trimmed.");
            model.AddPositiveDouble("MaxOffset", "01 Matching", "Maximum alignment offset to T-limit", 1.0,
                "Only a baseline whose alignment passes this close to the centre of a T-limit line is treated as the intersecting side road.");
            model.AddPositiveDouble("MaxTerminalLength", "02 Cleanup", "Maximum short terminal region", 30.0,
                "Safety limit. A terminal region longer than this is not removed automatically.");
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            double maxOffset = model.Double("MaxOffset", 1.0);
            double maxTerminalLength = model.Double("MaxTerminalLength", 30.0);
            int split = 0;
            int removed = 0;
            int skipped = 0;
            var rows = new List<IList<string>>();

            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                List<TJunctionLimit> limits = ReadTJunctionLimits(document.Database, transaction);
                foreach (ObjectId corridorId in corridorIds.Distinct())
                {
                    DBObject corridor = SafeOpen<DBObject>(transaction, corridorId, OpenMode.ForWrite);
                    if (corridor == null) continue;
                    int localSplit = 0;
                    int localRemoved = 0;
                    int localSkipped = 0;

                    foreach (object baselineObject in Enumerate(ReadProperty(corridor, "Baselines")))
                    {
                        Baseline baseline = baselineObject as Baseline;
                        if (baseline == null) continue;
                        ObjectId alignmentId = ReadObjectId(ReadProperty(baseline, "AlignmentId"));
                        if (alignmentId.IsNull) alignmentId = ReadObjectId(ReadProperty(baseline, "AlignmentObjectId"));
                        CivilAlignment alignment = SafeOpen<CivilAlignment>(transaction, alignmentId, OpenMode.ForRead);
                        if (alignment == null) continue;

                        foreach (TJunctionLimit limit in limits)
                        {
                            double limitStation;
                            double limitOffset;
                            if (!TryStationOffset(alignment, limit.MidPoint, out limitStation, out limitOffset)) continue;
                            if (Math.Abs(limitOffset) > maxOffset) continue;
                            if (limitStation <= baseline.StartStation + StationTolerance ||
                                limitStation >= baseline.EndStation - StationTolerance) continue;

                            double junctionStation;
                            double junctionOffset;
                            if (!TryStationOffset(alignment, limit.Junction, out junctionStation, out junctionOffset)) continue;

                            if (SplitAtStation(baseline, limitStation)) localSplit++;

                            int removeIndex = junctionStation < limitStation ? 0 : baseline.BaselineRegions.Count - 1;
                            if (removeIndex < 0 || removeIndex >= baseline.BaselineRegions.Count) continue;
                            BaselineRegion terminal = baseline.BaselineRegions[removeIndex];
                            double length = terminal.EndStation - terminal.StartStation;
                            bool touchesLimit = Math.Abs((junctionStation < limitStation ? terminal.EndStation : terminal.StartStation) - limitStation) <= 0.05;
                            bool touchesBaselineEnd = junctionStation < limitStation
                                ? Math.Abs(terminal.StartStation - baseline.StartStation) <= 0.05
                                : Math.Abs(terminal.EndStation - baseline.EndStation) <= 0.05;

                            if (!touchesLimit || !touchesBaselineEnd || length > maxTerminalLength)
                            {
                                localSkipped++;
                                continue;
                            }

                            if (TryRemoveRegion(baseline.BaselineRegions, terminal, removeIndex))
                            {
                                localRemoved++;
                                baseline.NeedsProcessing = true;
                            }
                            else
                            {
                                localSkipped++;
                            }
                        }
                    }

                    if (localSplit > 0 || localRemoved > 0)
                    {
                        TryInvoke(corridor, "Rebuild");
                        Entity entity = corridor as Entity;
                        if (entity != null) entity.RecordGraphicsModified(true);
                    }

                    split += localSplit;
                    removed += localRemoved;
                    skipped += localSkipped;
                    rows.Add(new List<string>
                    {
                        DisplayName(corridor),
                        localSplit.ToString(CultureInfo.InvariantCulture),
                        localRemoved.ToString(CultureInfo.InvariantCulture),
                        localSkipped.ToString(CultureInfo.InvariantCulture),
                        localRemoved > 0 ? "T-junction side-road limits applied" : "No eligible T terminal region removed"
                    });
                }
                transaction.Commit();
            }

            document.Editor.Regen();
            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - T-Junction Assembly Limits",
                string.Format(CultureInfo.CurrentCulture, "Region splits={0}; short terminal regions removed={1}; skipped={2}.", split, removed, skipped),
                new[] { "CORRIDOR", "SPLITS", "SHORT REGIONS REMOVED", "SKIPPED", "STATUS" },
                rows,
                "CE T JUNCTION ASSEMBLY LIMIT REGISTER");
        }

        [CommandMethod("CE_TOOLS", "CE_JUNCTIONENDPOINTSTOTOPSURFACES", CommandFlags.Modal | CommandFlags.Redraw)]
        public void PasteJunctionEndpointsToTopSurfaces()
        {
            Document document = ActiveDocument();
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (document == null || civilDocument == null) return;

            int surfaceCount = 0;
            int pointsAdded = 0;
            int failed = 0;
            var rows = new List<IList<string>>();

            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                List<Point3d> endpoints = ReadJunctionEndpoints(document.Database, transaction);
                foreach (ObjectId surfaceId in civilDocument.GetSurfaceIds())
                {
                    CivilSurface surface = SafeOpen<CivilSurface>(transaction, surfaceId, OpenMode.ForWrite);
                    if (surface == null || !surface.Name.StartsWith("TOP-RD-", StringComparison.OrdinalIgnoreCase)) continue;
                    surfaceCount++;
                    int local = 0;
                    int localFailed = 0;
                    foreach (Point3d point in endpoints)
                    {
                        try
                        {
                            double z = surface.FindElevationAtXY(point.X, point.Y);
                            Point3d surfacePoint = new Point3d(point.X, point.Y, z);
                            if (TryAddSurfaceVertex(surface, surfacePoint)) local++;
                            else localFailed++;
                        }
                        catch
                        {
                            // A road surface does not cover every junction. Outside
                            // points are expected and are not forced into that surface.
                        }
                    }
                    TryInvoke(surface, "Rebuild");
                    Entity entity = surface as Entity;
                    if (entity != null) entity.RecordGraphicsModified(true);
                    pointsAdded += local;
                    failed += localFailed;
                    rows.Add(new List<string>
                    {
                        surface.Name,
                        endpoints.Count.ToString(CultureInfo.InvariantCulture),
                        local.ToString(CultureInfo.InvariantCulture),
                        localFailed.ToString(CultureInfo.InvariantCulture)
                    });
                }
                transaction.Commit();
            }

            document.Editor.Regen();
            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - Junction End Points to Road TOP Surfaces",
                string.Format(CultureInfo.CurrentCulture, "TOP road surfaces={0}; endpoint insertions={1}; API failures={2}.", surfaceCount, pointsAdded, failed),
                new[] { "TOP SURFACE", "JUNCTION ENDPOINTS", "ADDED", "FAILED" },
                rows,
                "CE JUNCTION ENDPOINT SURFACE REGISTER");
        }

        [CommandMethod("CE_TOOLS", "CE_PIPESLOPETOOUTLET", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void ReversePipeSlopesTowardOutlet()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            List<ObjectId> pipeIds = SelectObjects(
                document,
                "\nSelect gravity pipes whose slope direction must flow toward the outlet: ",
                obj => obj != null && obj.GetType().Name.Equals("Pipe", StringComparison.OrdinalIgnoreCase));
            if (pipeIds.Count == 0)
            {
                document.Editor.WriteMessage("\nCE_PIPESLOPETOOUTLET cancelled. No Civil 3D pipes selected.");
                return;
            }

            PromptPointResult outletResult = document.Editor.GetPoint("\nSpecify the network low point / outlet: ");
            if (outletResult.Status != PromptStatus.OK) return;
            Point3d outlet = outletResult.Value;

            int reversed = 0;
            int already = 0;
            int failed = 0;
            var rows = new List<IList<string>>();

            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in pipeIds.Distinct())
                {
                    DBObject pipe = SafeOpen<DBObject>(transaction, id, OpenMode.ForWrite);
                    if (pipe == null) continue;
                    object startValue = ReadProperty(pipe, "StartPoint");
                    object endValue = ReadProperty(pipe, "EndPoint");
                    if (!(startValue is Point3d) || !(endValue is Point3d))
                    {
                        failed++;
                        rows.Add(new List<string> { id.Handle.ToString(), "-", "-", "Pipe endpoint API unavailable" });
                        continue;
                    }

                    Point3d start = (Point3d)startValue;
                    Point3d end = (Point3d)endValue;
                    double startDistance = PlanDistance(start, outlet);
                    double endDistance = PlanDistance(end, outlet);
                    bool outletAtStart = startDistance <= endDistance;
                    double outletElevation = outletAtStart ? start.Z : end.Z;
                    double upstreamElevation = outletAtStart ? end.Z : start.Z;

                    if (outletElevation <= upstreamElevation + 1e-9)
                    {
                        already++;
                        rows.Add(new List<string>
                        {
                            id.Handle.ToString(),
                            start.Z.ToString("0.###", CultureInfo.CurrentCulture),
                            end.Z.ToString("0.###", CultureInfo.CurrentCulture),
                            "Already falls toward outlet"
                        });
                        continue;
                    }

                    Point3d newStart = new Point3d(start.X, start.Y, end.Z);
                    Point3d newEnd = new Point3d(end.X, end.Y, start.Z);
                    bool startSet = TrySetProperty(pipe, "StartPoint", newStart);
                    bool endSet = TrySetProperty(pipe, "EndPoint", newEnd);
                    if (startSet && endSet)
                    {
                        reversed++;
                        TryInvoke(pipe, "ApplyRules");
                        Entity entity = pipe as Entity;
                        if (entity != null) entity.RecordGraphicsModified(true);
                        rows.Add(new List<string>
                        {
                            id.Handle.ToString(),
                            newStart.Z.ToString("0.###", CultureInfo.CurrentCulture),
                            newEnd.Z.ToString("0.###", CultureInfo.CurrentCulture),
                            "Slope reversed toward outlet"
                        });
                    }
                    else
                    {
                        // Do not leave a half-applied elevation swap.
                        if (startSet) TrySetProperty(pipe, "StartPoint", start);
                        if (endSet) TrySetProperty(pipe, "EndPoint", end);
                        failed++;
                        rows.Add(new List<string>
                        {
                            id.Handle.ToString(),
                            start.Z.ToString("0.###", CultureInfo.CurrentCulture),
                            end.Z.ToString("0.###", CultureInfo.CurrentCulture),
                            "Civil 3D endpoint write unavailable"
                        });
                    }
                }
                transaction.Commit();
            }

            document.Editor.Regen();
            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - Pipe Slopes to Outlet",
                string.Format(CultureInfo.CurrentCulture, "Pipes reversed={0}; already correct={1}; failed={2}.", reversed, already, failed),
                new[] { "PIPE", "START Z", "END Z", "STATUS" },
                rows,
                "CE PIPE SLOPE OUTLET REGISTER");
        }

        [CommandMethod("CE_TOOLS", "CE_PIPESLOPEJUNCTIONFIX", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void FixPipeSlopesAtJunctions()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            List<ObjectId> pipeIds = SelectObjects(
                document,
                "\nSelect sewer pipes whose junction elevations must be levelled: ",
                obj => obj != null &&
                    obj.GetType().Name.Equals("Pipe", StringComparison.OrdinalIgnoreCase));
            if (pipeIds.Count == 0)
            {
                document.Editor.WriteMessage("\nCE_PIPESLOPEJUNCTIONFIX cancelled. No pipes selected.");
                return;
            }

            var junctions = new Dictionary<ObjectId, List<PipeEndpoint>>();
            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId pipeId in pipeIds.Distinct())
                {
                    DBObject pipe = SafeOpen<DBObject>(
                        transaction, pipeId, OpenMode.ForRead);
                    if (pipe == null) continue;
                    ObjectId startStructure = ReadObjectId(
                        ReadProperty(pipe, "StartStructureId"));
                    ObjectId endStructure = ReadObjectId(
                        ReadProperty(pipe, "EndStructureId"));
                    object startValue = ReadProperty(pipe, "StartPoint");
                    object endValue = ReadProperty(pipe, "EndPoint");
                    if (!(startValue is Point3d) || !(endValue is Point3d)) continue;
                    AddPipeEndpoint(
                        junctions, startStructure,
                        new PipeEndpoint(pipeId, true, (Point3d)startValue));
                    AddPipeEndpoint(
                        junctions, endStructure,
                        new PipeEndpoint(pipeId, false, (Point3d)endValue));
                }

                int changed = 0;
                foreach (KeyValuePair<ObjectId, List<PipeEndpoint>> group in junctions)
                {
                    if (group.Key.IsNull || group.Value == null || group.Value.Count < 2)
                        continue;
                    double junctionElevation = group.Value.Min(item => item.Point.Z);
                    foreach (PipeEndpoint endpoint in group.Value)
                    {
                        DBObject pipe = SafeOpen<DBObject>(
                            transaction, endpoint.PipeId, OpenMode.ForWrite);
                        if (pipe == null) continue;
                        Point3d point = new Point3d(
                            endpoint.Point.X,
                            endpoint.Point.Y,
                            junctionElevation);
                        bool applied = endpoint.Start
                            ? TrySetProperty(pipe, "StartPoint", point)
                            : TrySetProperty(pipe, "EndPoint", point);
                        if (applied)
                        {
                            changed++;
                            TryInvoke(pipe, "ApplyRules");
                            Entity entity = pipe as Entity;
                            if (entity != null) entity.RecordGraphicsModified(true);
                        }
                    }
                }
                transaction.Commit();
                document.Editor.Regen();
                document.Editor.WriteMessage(
                    "\nCE_PIPESLOPEJUNCTIONFIX complete. Junction endpoints levelled={0}; junctions processed={1}.",
                    changed, junctions.Count);
            }
        }

        private static void AddPipeEndpoint(
            IDictionary<ObjectId, List<PipeEndpoint>> groups,
            ObjectId structureId,
            PipeEndpoint endpoint)
        {
            if (structureId.IsNull || endpoint == null) return;
            List<PipeEndpoint> values;
            if (!groups.TryGetValue(structureId, out values))
            {
                values = new List<PipeEndpoint>();
                groups[structureId] = values;
            }
            values.Add(endpoint);
        }

        private static Dictionary<ObjectId, List<FinalProfilePvi>> CaptureFinalProfilePvis(
            CivilAlignment alignment,
            Transaction transaction,
            CivilProfileView selectedView)
        {
            var result = new Dictionary<ObjectId, List<FinalProfilePvi>>();
            if (alignment == null || transaction == null) return result;

            IEnumerable<ObjectId> candidateIds =
                alignment.GetProfileIds().Cast<ObjectId>();
            if (selectedView != null)
            {
                var viewIds = new List<ObjectId>();
                foreach (string member in new[] { "GetProfileIds", "GetProfiles" })
                    viewIds.AddRange(ReadObjectIds(InvokeReturning(selectedView, member)));
                foreach (string property in new[] { "ProfileIds", "Profiles" })
                    viewIds.AddRange(ReadObjectIds(ReadProperty(selectedView, property)));
                viewIds = viewIds.Where(id => !id.IsNull).Distinct().ToList();
                if (viewIds.Count > 0)
                    candidateIds = viewIds;
            }

            foreach (ObjectId profileId in candidateIds)
            {
                CivilProfile profile = SafeOpen<CivilProfile>(
                    transaction,
                    profileId,
                    OpenMode.ForRead);
                if (profile == null || !IsFinalRoadProfile(profile)) continue;

                var snapshots = new List<FinalProfilePvi>();
                foreach (ProfilePVI pvi in CivilStyleDiscovery.Enumerate(profile.PVIs)
                    .OfType<ProfilePVI>()
                    .OrderBy(item => item.Station))
                {
                    try { snapshots.Add(new FinalProfilePvi(pvi.Station, pvi.Elevation)); }
                    catch { }
                }
                if (snapshots.Count >= 2) result[profileId] = snapshots;
            }
            return result;
        }

        private static bool IsFinalRoadProfile(CivilProfile profile)
        {
            string identity = ((profile.Name ?? string.Empty) + " " +
                (profile.Description ?? string.Empty)).ToUpperInvariant();
            bool excluded = IsGroundProfileIdentity(identity);
            return !excluded && IsFinalDesignProfileIdentity(identity);
        }

        private static bool ReverseFinalProfilePvis(
            CivilProfile profile,
            IList<FinalProfilePvi> original,
            double stationStart,
            double stationEnd)
        {
            if (profile == null || original == null || original.Count < 2)
                return false;

            List<ProfilePVI> live = CivilStyleDiscovery.Enumerate(profile.PVIs)
                .OfType<ProfilePVI>()
                .OrderBy(item => item.Station)
                .ToList();
            if (live.Count != original.Count)
                return RebuildFinalProfilePvis(profile, original, stationStart, stationEnd);

            double expectedFirst = stationStart + stationEnd - original[original.Count - 1].Station;
            if (Math.Abs(live[0].Station - expectedFirst) <= StationTolerance)
                return false; // already transformed by Civil 3D

            bool changed = true;
            for (int index = 0; index < live.Count; index++)
            {
                FinalProfilePvi source = original[original.Count - 1 - index];
                changed = TrySetPviValues(
                    live[index],
                    stationStart + stationEnd - source.Station,
                    source.Elevation) && changed;
            }

            // ProfilePVI.Station is read-only in some Civil 3D 2023 builds.
            // Rebuild the PVI collection only when the live wrappers cannot be
            // moved directly; this keeps NGL profiles untouched.
            return changed || RebuildFinalProfilePvis(
                profile,
                original,
                stationStart,
                stationEnd);
        }

        private static bool RebuildFinalProfilePvis(
            CivilProfile profile,
            IList<FinalProfilePvi> original,
            double stationStart,
            double stationEnd)
        {
            object pvis = profile == null ? null : profile.PVIs;
            if (pvis == null) return false;

            List<ProfilePVI> live = CivilStyleDiscovery.Enumerate(pvis)
                .OfType<ProfilePVI>()
                .OrderBy(item => item.Station)
                .ToList();
            for (int index = live.Count - 1; index >= 0; index--)
            {
                ProfilePVI item = live[index];
                if (!TryInvoke(pvis, "RemovePVI", item) &&
                    !TryInvoke(pvis, "Remove", item) &&
                    !TryInvoke(pvis, "RemoveAt", index))
                    return false;
            }

            MethodInfo add = pvis.GetType().GetMethods(
                BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(method =>
                {
                    if (method.Name.IndexOf("AddPVI", StringComparison.OrdinalIgnoreCase) < 0)
                        return false;
                    ParameterInfo[] parameters = method.GetParameters();
                    return parameters.Length >= 2 &&
                           parameters[0].ParameterType == typeof(double) &&
                           parameters[1].ParameterType == typeof(double);
                });
            if (add == null) return false;

            foreach (FinalProfilePvi source in original.Reverse())
            {
                ParameterInfo[] parameters = add.GetParameters();
                object[] arguments = new object[parameters.Length];
                arguments[0] = stationStart + stationEnd - source.Station;
                arguments[1] = source.Elevation;
                for (int index = 2; index < parameters.Length; index++)
                {
                    Type type = parameters[index].ParameterType;
                    if (parameters[index].HasDefaultValue)
                        arguments[index] = parameters[index].DefaultValue;
                    else if (type == typeof(double))
                        arguments[index] = 0.0;
                    else if (type == typeof(bool))
                        arguments[index] = false;
                    else if (type.IsEnum)
                        arguments[index] = Enum.GetValues(type).GetValue(0);
                    else
                        return false;
                }
                try { add.Invoke(pvis, arguments); }
                catch { return false; }
            }
            return true;
        }

        private static bool TrySetPviValues(
            ProfilePVI pvi,
            double station,
            double elevation)
        {
            if (pvi == null) return false;
            bool stationSet = false;
            bool elevationSet = false;

            try
            {
                PropertyInfo stationProperty = pvi.GetType().GetProperty(
                    "Station",
                    BindingFlags.Public | BindingFlags.Instance);
                if (stationProperty != null && stationProperty.CanWrite &&
                    stationProperty.PropertyType == typeof(double))
                {
                    stationProperty.SetValue(pvi, station, null);
                    stationSet = true;
                }
            }
            catch { }

            try
            {
                PropertyInfo elevationProperty = pvi.GetType().GetProperty(
                    "Elevation",
                    BindingFlags.Public | BindingFlags.Instance);
                if (elevationProperty != null && elevationProperty.CanWrite &&
                    elevationProperty.PropertyType == typeof(double))
                {
                    elevationProperty.SetValue(pvi, elevation, null);
                    elevationSet = true;
                }
            }
            catch { }

            if (!stationSet)
                stationSet = TryInvoke(pvi, "SetStation", station);
            if (!elevationSet)
                elevationSet = TryInvoke(pvi, "SetElevation", elevation);
            return stationSet && elevationSet;
        }

        private static Document ActiveDocument()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
        }

        private static List<ObjectId> SelectObjects(Document document, string prompt, Func<DBObject, bool> predicate)
        {
            PromptSelectionResult selected = document.Editor.SelectImplied();
            if (selected.Status != PromptStatus.OK || selected.Value == null || selected.Value.Count == 0)
            {
                selected = document.Editor.GetSelection(new PromptSelectionOptions
                {
                    MessageForAdding = prompt,
                    AllowDuplicates = false,
                    RejectObjectsFromNonCurrentSpace = true
                });
            }
            document.Editor.SetImpliedSelection(new ObjectId[0]);
            if (selected.Status != PromptStatus.OK || selected.Value == null) return new List<ObjectId>();

            var result = new List<ObjectId>();
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in selected.Value.GetObjectIds())
                {
                    DBObject value = SafeOpen<DBObject>(transaction, id, OpenMode.ForRead);
                    if (value != null && predicate(value)) result.Add(id);
                }
            }
            return result;
        }

        private static List<DBObject> ReadCorridors(CivilDocument civilDocument, Transaction transaction, OpenMode mode)
        {
            var result = new List<DBObject>();
            object collection = ReadProperty(civilDocument, "CorridorCollection");
            foreach (object value in Enumerate(collection))
            {
                ObjectId id = value is ObjectId
                    ? (ObjectId)value
                    : value is DBObject ? ((DBObject)value).ObjectId : ObjectId.Null;
                DBObject corridor = id.IsNull ? value as DBObject : SafeOpen<DBObject>(transaction, id, mode);
                if (corridor != null && corridor.GetType().Name.IndexOf("Corridor", StringComparison.OrdinalIgnoreCase) >= 0)
                    result.Add(corridor);
            }
            return result;
        }

        private static bool CorridorUsesAlignment(DBObject corridor, ObjectId alignmentId)
        {
            foreach (object baseline in Enumerate(ReadProperty(corridor, "Baselines")))
            {
                ObjectId current = ReadObjectId(ReadProperty(baseline, "AlignmentId"));
                if (current.IsNull) current = ReadObjectId(ReadProperty(baseline, "AlignmentObjectId"));
                if (current == alignmentId) return true;
            }
            return false;
        }

        private static string ResolveRoadName(DBObject corridor, Transaction transaction)
        {
            foreach (object baseline in Enumerate(ReadProperty(corridor, "Baselines")))
            {
                ObjectId alignmentId = ReadObjectId(ReadProperty(baseline, "AlignmentId"));
                if (alignmentId.IsNull) alignmentId = ReadObjectId(ReadProperty(baseline, "AlignmentObjectId"));
                CivilAlignment alignment = SafeOpen<CivilAlignment>(transaction, alignmentId, OpenMode.ForRead);
                if (alignment == null) continue;
                string normalized = NormalizeRoadName(alignment.Name);
                if (!string.IsNullOrWhiteSpace(normalized)) return normalized;
            }
            return NormalizeRoadName(DisplayName(corridor));
        }

        private static string NormalizeRoadName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            Match match = Regex.Match(value, @"(?i)\bRD[\s_-]*(\d+)\b");
            if (!match.Success) return null;
            int number;
            if (!int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
                return null;
            return "RD-" + number.ToString("00", CultureInfo.InvariantCulture);
        }

        private static string DisplayName(DBObject value)
        {
            if (value == null) return "-";
            string name = Convert.ToString(ReadProperty(value, "Name"), CultureInfo.CurrentCulture);
            return string.IsNullOrWhiteSpace(name) ? value.Handle.ToString() : name;
        }

        private static ObjectId GetOrCreateLayer(
            Database database,
            Transaction transaction,
            string requested,
            ObjectId fallback)
        {
            string name = string.IsNullOrWhiteSpace(requested)
                ? string.Empty
                : requested.Trim();
            if (string.IsNullOrWhiteSpace(name))
                return fallback;
            try
            {
                LayerTable table = transaction.GetObject(
                    database.LayerTableId,
                    OpenMode.ForRead,
                    false) as LayerTable;
                if (table == null) return fallback;
                if (table.Has(name)) return table[name];
                table.UpgradeOpen();
                LayerTableRecord layer = new LayerTableRecord { Name = name };
                ObjectId id = table.Add(layer);
                transaction.AddNewlyCreatedDBObject(layer, true);
                return id;
            }
            catch
            {
                return fallback;
            }
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

        private static string ReadObjectName(Transaction transaction, ObjectId id)
        {
            if (transaction == null || id.IsNull) return string.Empty;
            try
            {
                DBObject value = transaction.GetObject(id, OpenMode.ForRead, false);
                return Convert.ToString(ReadProperty(value, "Name"), CultureInfo.CurrentCulture) ?? string.Empty;
            }
            catch { return string.Empty; }
        }

        private static bool IsRoadTopBottomSurface(string name)
        {
            return !string.IsNullOrWhiteSpace(name) &&
                   (name.StartsWith("TOP-RD-", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("BOTTOM-RD-", StringComparison.OrdinalIgnoreCase));
        }

        private static HashSet<string> ParseRoadFilter(string text)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(text) || string.Equals(text.Trim(), "ALL", StringComparison.OrdinalIgnoreCase))
                return result;
            foreach (string token in text.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string road = NormalizeRoadName(token.Trim());
                if (!string.IsNullOrWhiteSpace(road)) result.Add(road);
            }
            return result;
        }

        private static bool RoadFilterMatches(string surfaceName, HashSet<string> roads)
        {
            if (roads == null || roads.Count == 0) return true;
            string road = NormalizeRoadName(surfaceName);
            return !string.IsNullOrWhiteSpace(road) && roads.Contains(road);
        }

        private static string SafeProfileName(string value)
        {
            string cleaned = Regex.Replace(value ?? "CE-ROAD-SURFACE", @"[\r\n\t<>:""/\\|?*]", "-").Trim();
            return cleaned.Length <= 120 ? cleaned : cleaned.Substring(0, 120);
        }

        private static ObjectId CreateSurfaceProfile(
            string name,
            ObjectId alignmentId,
            ObjectId surfaceId,
            ObjectId layerId,
            ObjectId styleId,
            ObjectId labelSetId)
        {
            lastProfileException = null;
            // Civil 3D 2023 exposes the six-argument overload directly. The
            // previous reflection-only path swallowed the real API exception
            // and reported every row as "no compatible overload".
            try
            {
                ObjectId direct = CivilProfile.CreateFromSurface(
                    name, alignmentId, surfaceId, layerId, styleId, labelSetId);
                if (!direct.IsNull) return direct;
            }
            catch (System.Exception directException)
            {
                // Keep probing below; the final message includes the actual
                // Civil 3D reason instead of hiding it behind overload text.
                lastProfileException = directException;
            }

            Type type = typeof(CivilAlignment).Assembly.GetType(
                "Autodesk.Civil.DatabaseServices.Profile", true);
            foreach (MethodInfo method in type.GetMethods(
                BindingFlags.Public | BindingFlags.Static)
                .Where(item => item.Name == "CreateFromSurface")
                .OrderBy(item => item.GetParameters().Length))
            {
                object[] arguments;
                if (!BuildProfileArguments(
                    method.GetParameters(), name, alignmentId, surfaceId,
                    layerId, styleId, labelSetId, out arguments))
                    continue;
                try
                {
                    object result = method.Invoke(null, arguments);
                    if (result is ObjectId) return (ObjectId)result;
                }
                catch (TargetInvocationException exception)
                {
                    lastProfileException = exception.InnerException ?? exception;
                }
            }
            string detail = lastProfileException == null
                ? string.Empty
                : " Last Civil 3D error: " + lastProfileException.Message;
            throw new InvalidOperationException(
                "No compatible Profile.CreateFromSurface overload was found." + detail);
        }

        private static System.Exception lastProfileException;

        private static bool BuildProfileArguments(
            ParameterInfo[] parameters,
            string name,
            ObjectId alignmentId,
            ObjectId surfaceId,
            ObjectId layerId,
            ObjectId styleId,
            ObjectId labelSetId,
            out object[] arguments)
        {
            arguments = new object[parameters.Length];
            ObjectId[] fallback = { alignmentId, surfaceId, layerId, styleId, labelSetId };
            int fallbackIndex = 0;
            for (int index = 0; index < parameters.Length; index++)
            {
                ParameterInfo parameter = parameters[index];
                string parameterName = (parameter.Name ?? string.Empty).ToLowerInvariant();
                if (parameter.ParameterType == typeof(string))
                    arguments[index] = name;
                else if (parameter.ParameterType == typeof(ObjectId))
                {
                    if (parameterName.Contains("alignment")) arguments[index] = alignmentId;
                    else if (parameterName.Contains("surface")) arguments[index] = surfaceId;
                    else if (parameterName.Contains("layer")) arguments[index] = layerId;
                    else if (parameterName.Contains("label")) arguments[index] = labelSetId;
                    else if (parameterName.Contains("style")) arguments[index] = styleId;
                    else return false;
                }
                else if (parameter.HasDefaultValue)
                    arguments[index] = parameter.DefaultValue;
                else if (parameter.ParameterType.IsEnum)
                    arguments[index] = Enum.GetValues(parameter.ParameterType).GetValue(0);
                else if (parameter.ParameterType == typeof(bool))
                    arguments[index] = false;
                else if (parameter.ParameterType == typeof(double))
                    arguments[index] = 0.0;
                else if (parameter.ParameterType.IsValueType)
                    arguments[index] = Activator.CreateInstance(parameter.ParameterType);
                else
                    return false;
            }
            return true;
        }

        private static List<TJunctionLimit> ReadTJunctionLimits(Database database, Transaction transaction)
        {
            var result = new List<TJunctionLimit>();
            BlockTable table = transaction.GetObject(database.BlockTableId, OpenMode.ForRead, false) as BlockTable;
            if (table == null) return result;
            BlockTableRecord model = transaction.GetObject(table[BlockTableRecord.ModelSpace], OpenMode.ForRead, false) as BlockTableRecord;
            if (model == null) return result;

            foreach (ObjectId id in model)
            {
                Curve curve = SafeOpen<Curve>(transaction, id, OpenMode.ForRead);
                if (curve == null || !string.Equals(curve.Layer, JunctionLayer, StringComparison.OrdinalIgnoreCase)) continue;
                ResultBuffer data = null;
                try { data = curve.GetXDataForApplication(JunctionApp); }
                catch { }
                if (data == null) continue;
                TypedValue[] values = data.AsArray();
                if (values.Length < 5) continue;
                string marker = values.Skip(1)
                    .Where(item => item.TypeCode == (int)DxfCode.ExtendedDataAsciiString)
                    .Select(item => Convert.ToString(item.Value, CultureInfo.InvariantCulture))
                    .FirstOrDefault();
                if (!string.Equals(marker, "T-LIMIT", StringComparison.OrdinalIgnoreCase)) continue;
                List<double> reals = values
                    .Where(item => item.TypeCode == (int)DxfCode.ExtendedDataReal)
                    .Select(item => Convert.ToDouble(item.Value, CultureInfo.InvariantCulture))
                    .ToList();
                if (reals.Count < 2) continue;
                Point3d start;
                Point3d end;
                try
                {
                    start = curve.StartPoint;
                    end = curve.EndPoint;
                }
                catch { continue; }
                result.Add(new TJunctionLimit
                {
                    MidPoint = new Point3d((start.X + end.X) * 0.5, (start.Y + end.Y) * 0.5, 0.0),
                    Junction = new Point3d(reals[0], reals[1], reals.Count > 2 ? reals[2] : 0.0)
                });
            }
            return result;
        }

        private static bool TryStationOffset(CivilAlignment alignment, Point3d point, out double station, out double offset)
        {
            station = 0.0;
            offset = double.MaxValue;
            if (alignment == null) return false;

            foreach (MethodInfo method in alignment.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(item => item.Name == "StationOffset"))
            {
                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length < 4) continue;
                object[] arguments = new object[parameters.Length];
                int doubleInput = 0;
                int byRefDouble = 0;
                bool usable = true;
                for (int index = 0; index < parameters.Length; index++)
                {
                    Type parameterType = parameters[index].ParameterType;
                    Type effective = parameterType.IsByRef ? parameterType.GetElementType() : parameterType;
                    if (effective == typeof(double))
                    {
                        if (parameterType.IsByRef)
                        {
                            arguments[index] = 0.0;
                            byRefDouble++;
                        }
                        else
                        {
                            arguments[index] = doubleInput++ == 0 ? point.X : point.Y;
                        }
                    }
                    else if (parameters[index].HasDefaultValue)
                        arguments[index] = parameters[index].DefaultValue;
                    else
                    {
                        usable = false;
                        break;
                    }
                }
                if (!usable || byRefDouble < 2) continue;
                try
                {
                    method.Invoke(alignment, arguments);
                    List<double> outputs = new List<double>();
                    for (int index = 0; index < parameters.Length; index++)
                        if (parameters[index].ParameterType.IsByRef &&
                            parameters[index].ParameterType.GetElementType() == typeof(double))
                            outputs.Add(Convert.ToDouble(arguments[index], CultureInfo.InvariantCulture));
                    if (outputs.Count >= 2)
                    {
                        station = outputs[0];
                        offset = outputs[1];
                        return true;
                    }
                }
                catch
                {
                }
            }
            return false;
        }

        private static bool SplitAtStation(Baseline baseline, double station)
        {
            if (baseline == null) return false;
            for (int index = 0; index < baseline.BaselineRegions.Count; index++)
            {
                BaselineRegion region = baseline.BaselineRegions[index];
                if (region == null) continue;
                if (station <= region.StartStation + StationTolerance || station >= region.EndStation - StationTolerance)
                    continue;
                try
                {
                    region.Split(station);
                    baseline.NeedsProcessing = true;
                    return true;
                }
                catch
                {
                    return false;
                }
            }
            return false;
        }

        private static bool TryRemoveRegion(object collection, BaselineRegion region, int index)
        {
            if (collection == null || region == null) return false;
            if (TryInvoke(collection, "RemoveAt", index)) return true;
            if (TryInvoke(collection, "Remove", region)) return true;
            if (TryInvoke(collection, "Erase", region)) return true;
            return false;
        }

        private static List<Point3d> ReadJunctionEndpoints(Database database, Transaction transaction)
        {
            var result = new List<Point3d>();
            BlockTable table = transaction.GetObject(database.BlockTableId, OpenMode.ForRead, false) as BlockTable;
            if (table == null) return result;
            BlockTableRecord model = transaction.GetObject(table[BlockTableRecord.ModelSpace], OpenMode.ForRead, false) as BlockTableRecord;
            if (model == null) return result;

            foreach (ObjectId id in model)
            {
                Curve curve = SafeOpen<Curve>(transaction, id, OpenMode.ForRead);
                if (curve == null || !string.Equals(curve.Layer, JunctionLayer, StringComparison.OrdinalIgnoreCase)) continue;
                ResultBuffer data = null;
                try { data = curve.GetXDataForApplication(JunctionApp); }
                catch { }
                if (data == null) continue;
                try
                {
                    AddUniquePoint(result, curve.StartPoint);
                    AddUniquePoint(result, curve.EndPoint);
                }
                catch
                {
                }
            }
            return result;
        }

        private static void AddUniquePoint(List<Point3d> points, Point3d point)
        {
            Point3d plan = new Point3d(point.X, point.Y, 0.0);
            if (!points.Any(existing => PlanDistance(existing, plan) <= 0.001))
                points.Add(plan);
        }

        private static bool TryAssignFeatureLineElevations(
            CivilFeatureLine line,
            CivilSurface surface)
        {
            if (line == null || surface == null) return false;
            bool changed = false;

            // Use the typed Civil 3D 2023 call first. The previous reflected
            // implementation returned as soon as the method was found, even
            // when Civil 3D left the feature-line vertices at elevation 0.000.
            try
            {
                line.AssignElevationsFromSurface(surface.ObjectId, true);
                changed = true;
            }
            catch { }

            foreach (string methodName in new[]
            {
                "AssignElevationsFromSurface",
                "AssignElevationsFromSurfaceId",
                "DrapeToSurface"
            })
            {
                if (TryInvoke(line, methodName, surface.ObjectId) ||
                    TryInvoke(line, methodName, surface))
                    changed = true;
            }

            // Always verify the actual vertex elevations by sampling the
            // selected TOP surface. This also repairs hosts where the managed
            // AssignElevationsFromSurface wrapper reports success but does not
            // persist the point elevations.
            int index = 0;
            try
            {
                foreach (Point3d point in line.GetPoints(FeatureLinePointType.AllPoints))
                {
                    double elevation;
                    if (!TryFindSurfaceElevation(surface, point.X, point.Y, out elevation))
                    {
                        index++;
                        continue;
                    }
                    if (TrySetFeatureLinePointElevation(line, index, elevation))
                        changed = true;
                    index++;
                }
            }
            catch { }
            return changed;
        }

        private static bool TryFindSurfaceElevation(
            CivilSurface surface,
            double x,
            double y,
            out double elevation)
        {
            elevation = 0.0;
            if (surface == null) return false;
            try
            {
                elevation = surface.FindElevationAtXY(x, y);
                return !double.IsNaN(elevation) && !double.IsInfinity(elevation);
            }
            catch { return false; }
        }

        private static bool TrySetFeatureLinePointElevation(
            CivilFeatureLine line,
            int index,
            double elevation)
        {
            foreach (MethodInfo method in line.GetType().GetMethods(
                BindingFlags.Public | BindingFlags.Instance))
            {
                if (method.Name.IndexOf("SetPointElevation",
                    StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length == 2 &&
                    parameters[0].ParameterType == typeof(int) &&
                    parameters[1].ParameterType == typeof(double))
                {
                    try
                    {
                        method.Invoke(line, new object[] { index, elevation });
                        return true;
                    }
                    catch { }
                }
                if (parameters.Length == 3 &&
                    parameters[0].ParameterType == typeof(FeatureLinePointType) &&
                    parameters[1].ParameterType == typeof(int) &&
                    parameters[2].ParameterType == typeof(double))
                {
                    try
                    {
                        method.Invoke(line, new object[]
                        {
                            FeatureLinePointType.AllPoints, index, elevation
                        });
                        return true;
                    }
                    catch { }
                }
            }
            return false;
        }

        private static bool TryAddSurfaceVertex(CivilSurface surface, Point3d point)
        {
            if (surface == null) return false;
            if (TryInvoke(surface, "AddVertex", point) || TryInvoke(surface, "AddPoint", point))
                return true;

            foreach (string propertyName in new[] { "PointsDefinition", "Definition", "SurfaceDefinition" })
            {
                object definition = ReadProperty(surface, propertyName);
                if (definition == null) continue;
                if (TryInvoke(definition, "AddPoint", point) || TryInvoke(definition, "AddVertex", point))
                    return true;
            }
            return false;
        }

        private static double PlanDistance(Point3d first, Point3d second)
        {
            double dx = first.X - second.X;
            double dy = first.Y - second.Y;
            return Math.Sqrt((dx * dx) + (dy * dy));
        }

        private static T SafeOpen<T>(Transaction transaction, ObjectId id, OpenMode mode) where T : DBObject
        {
            if (transaction == null || id.IsNull || id.IsErased) return null;
            try { return transaction.GetObject(id, mode, false) as T; }
            catch { return null; }
        }

        private static ObjectId ReadObjectId(object value)
        {
            if (value is ObjectId) return (ObjectId)value;
            DBObject databaseObject = value as DBObject;
            if (databaseObject != null) return databaseObject.ObjectId;
            if (value == null) return ObjectId.Null;
            try
            {
                PropertyInfo property = value.GetType().GetProperty(
                    "ObjectId", BindingFlags.Public | BindingFlags.Instance);
                object raw = property == null ? null : property.GetValue(value, null);
                return raw is ObjectId ? (ObjectId)raw : ObjectId.Null;
            }
            catch { return ObjectId.Null; }
        }

        private static ObjectId ResolveAlignmentId(object view)
        {
            if (view == null) return ObjectId.Null;
            foreach (string propertyName in new[]
            {
                "AlignmentId", "ParentAlignmentId", "AlignmentObjectId",
                "ParentAlignment"
            })
            {
                ObjectId id = ReadObjectId(ReadProperty(view, propertyName));
                if (!id.IsNull) return id;
            }
            foreach (string methodName in new[] { "GetAlignmentId", "GetParentAlignmentId" })
            {
                ObjectId id = ReadObjectId(InvokeReturning(view, methodName));
                if (!id.IsNull) return id;
            }
            return ObjectId.Null;
        }

        private static IEnumerable<ObjectId> ReadObjectIds(object value)
        {
            if (value == null) yield break;
            IEnumerable enumerable = value as IEnumerable;
            if (enumerable == null) yield break;
            foreach (object item in enumerable)
            {
                if (item is ObjectId) yield return (ObjectId)item;
                else if (item is DBObject) yield return ((DBObject)item).ObjectId;
            }
        }

        private static IEnumerable<object> Enumerate(object value)
        {
            if (value == null) yield break;
            IEnumerable enumerable = value as IEnumerable;
            if (enumerable == null) yield break;
            foreach (object item in enumerable) yield return item;
        }

        private static object ReadProperty(object value, string name)
        {
            if (value == null || string.IsNullOrWhiteSpace(name)) return null;
            try
            {
                PropertyInfo property = value.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                return property == null ? null : property.GetValue(value, null);
            }
            catch { return null; }
        }

        private static bool TrySetProperty(object value, string name, object propertyValue)
        {
            if (value == null || string.IsNullOrWhiteSpace(name)) return false;
            try
            {
                PropertyInfo property = value.GetType().GetProperty(
                    name, BindingFlags.Public | BindingFlags.Instance);
                if (property != null && property.CanWrite)
                {
                    property.SetValue(value, propertyValue, null);
                    return true;
                }
            }
            catch { }

            // Pipe endpoint properties are read-only in some Civil 3D
            // versions; use the corresponding mutator when available.
            string setterName = "Set" + name;
            foreach (MethodInfo method in value.GetType().GetMethods(
                BindingFlags.Public | BindingFlags.Instance))
            {
                if (!string.Equals(method.Name, setterName,
                    StringComparison.OrdinalIgnoreCase)) continue;
                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length != 1 || propertyValue == null ||
                    !parameters[0].ParameterType.IsInstanceOfType(propertyValue))
                    continue;
                try
                {
                    method.Invoke(value, new[] { propertyValue });
                    return true;
                }
                catch { }
            }
            return false;
        }

        private static object InvokeReturning(object value, string methodName)
        {
            if (value == null) return null;
            try
            {
                MethodInfo method = value.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                return method == null ? null : method.Invoke(value, null);
            }
            catch { return null; }
        }

        private static bool TryInvoke(object value, string methodName, params object[] arguments)
        {
            if (value == null) return false;
            foreach (MethodInfo method in value.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(item => item.Name == methodName))
            {
                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length != (arguments == null ? 0 : arguments.Length)) continue;
                bool compatible = true;
                for (int index = 0; index < parameters.Length; index++)
                {
                    object argument = arguments[index];
                    Type target = parameters[index].ParameterType;
                    if (argument == null)
                    {
                        if (target.IsValueType && Nullable.GetUnderlyingType(target) == null) { compatible = false; break; }
                    }
                    else if (!target.IsAssignableFrom(argument.GetType()))
                    {
                        try
                        {
                            arguments[index] = Convert.ChangeType(argument, target, CultureInfo.InvariantCulture);
                        }
                        catch
                        {
                            compatible = false;
                            break;
                        }
                    }
                }
                if (!compatible) continue;
                try
                {
                    method.Invoke(value, arguments);
                    return true;
                }
                catch
                {
                }
            }
            return false;
        }

        private sealed class PipeEndpoint
        {
            internal PipeEndpoint(ObjectId pipeId, bool start, Point3d point)
            {
                PipeId = pipeId;
                Start = start;
                Point = point;
            }
            internal ObjectId PipeId { get; private set; }
            internal bool Start { get; private set; }
            internal Point3d Point { get; private set; }
        }

        private sealed class FinalProfilePvi
        {
            internal FinalProfilePvi(double station, double elevation)
            {
                Station = station;
                Elevation = elevation;
            }
            internal double Station { get; private set; }
            internal double Elevation { get; private set; }
        }

        private sealed class TJunctionLimit
        {
            internal Point3d MidPoint;
            internal Point3d Junction;
        }
    }
}

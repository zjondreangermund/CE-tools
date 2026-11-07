using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CETools.Core;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilNetwork = Autodesk.Civil.DatabaseServices.Network;
using CivilPart = Autodesk.Civil.DatabaseServices.Part;
using CivilPipe = Autodesk.Civil.DatabaseServices.Pipe;
using CivilStructure = Autodesk.Civil.DatabaseServices.Structure;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

[assembly: CommandClass(typeof(CETools.Civil3D.WaterGravityProfilePartsCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Draws ordinary Water gravity-network pipes and structures in only the
    /// profile views belonging to the source route that generated each part.
    /// Water alignments are linked to their source polylines by CE_TOOLS_WATER
    /// XData. Pipe-to-route matching uses tagged source-segment plan geometry
    /// (XY only, so NG elevation changes do not break ownership), then binds each
    /// pipe's Civil 3D RefAlignmentId before profile projection.
    /// </summary>
    public sealed class WaterGravityProfilePartsCommands
    {
        private const string WaterRegApp = "CE_TOOLS_WATER";
        private const double SourcePointTolerance = 0.02;

        [CommandMethod("CE_TOOLS", "CE_WATERPROFILEPARTSMULTI",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void DrawMultipleWaterNetworksInProfileViews()
        {
            DrawWaterPartsInProfileViews();
        }

        [CommandMethod("CE_TOOLS", "CE_WATERLINKSURFACE",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void LinkWaterPartsToSurface()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            CivilDocument civil = CivilDocument.GetCivilDocument(document.Database);
            Editor editor = document.Editor;

            PromptKeywordOptions scopeOptions = new PromptKeywordOptions(
                "\nWater surface scope [AllWaterNetworkParts/Select/ProfileViews] <AllWaterNetworkParts>: ")
            { AllowNone = true };
            scopeOptions.Keywords.Add("AllWaterNetworkParts");
            scopeOptions.Keywords.Add("Select");
            scopeOptions.Keywords.Add("ProfileViews");
            PromptResult scopeResult = editor.GetKeywords(scopeOptions);
            if (scopeResult.Status == PromptStatus.Cancel) return;
            string scope = scopeResult.Status == PromptStatus.OK
                ? scopeResult.StringResult
                : "AllWaterNetworkParts";

            List<NetworkChoice> networks = ReadWaterNetworks(document.Database, civil);
            if (networks.Count == 0)
            {
                editor.WriteMessage("\nCE_WATERLINKSURFACE: no Water gravity networks were found.");
                return;
            }

            var waterPartIds = new HashSet<ObjectId>();
            using (Transaction read = document.Database.TransactionManager.StartTransaction())
            {
                foreach (NetworkChoice choice in networks)
                {
                    CivilNetwork network = read.GetObject(choice.Id, OpenMode.ForRead, false) as CivilNetwork;
                    if (network == null) continue;
                    foreach (ObjectId id in network.GetPipeIds()) waterPartIds.Add(id);
                    foreach (ObjectId id in network.GetStructureIds()) waterPartIds.Add(id);
                }
            }

            List<ObjectId> selectedPartIds = new List<ObjectId>();
            if (string.Equals(scope, "AllWaterNetworkParts", StringComparison.OrdinalIgnoreCase))
            {
                selectedPartIds.AddRange(waterPartIds);
            }
            else if (string.Equals(scope, "Select", StringComparison.OrdinalIgnoreCase))
            {
                PromptSelectionResult selected = editor.GetSelection(
                    new PromptSelectionOptions
                    {
                        MessageForAdding = "\nSelect Water network pipes/structures: ",
                        MessageForRemoval = "\nRemove Water network parts: ",
                        AllowDuplicates = false,
                        RejectObjectsFromNonCurrentSpace = true
                    });
                if (selected.Status != PromptStatus.OK || selected.Value == null) return;
                selectedPartIds.AddRange(
                    selected.Value.GetObjectIds().Where(id => waterPartIds.Contains(id)));
            }
            else if (string.Equals(scope, "ProfileViews", StringComparison.OrdinalIgnoreCase))
            {
                PromptSelectionResult selectedViews = editor.GetSelection(
                    new PromptSelectionOptions
                    {
                        MessageForAdding = "\nSelect Water profile views. Only Water parts belonging to each view's exact branch alignment will be linked: ",
                        MessageForRemoval = "\nRemove Water profile views: ",
                        AllowDuplicates = false,
                        RejectObjectsFromNonCurrentSpace = true
                    });
                if (selectedViews.Status != PromptStatus.OK || selectedViews.Value == null) return;
                selectedPartIds.AddRange(
                    ReadWaterPartsForProfileViews(
                        document,
                        civil,
                        selectedViews.Value.GetObjectIds()));
            }

            selectedPartIds = selectedPartIds
                .Where(id => !id.IsNull && !id.IsErased && waterPartIds.Contains(id))
                .Distinct()
                .ToList();
            if (selectedPartIds.Count == 0)
            {
                editor.WriteMessage(
                    "\nCE_WATERLINKSURFACE: no Water network parts were found in the selected scope. Sewer and other-discipline parts are excluded.");
                return;
            }

            September09SewerSurfaceRulesRuntime.LinkSelectedPartsToSurface(
                document,
                civil,
                selectedPartIds,
                "Water");
        }

        private static List<ObjectId> ReadWaterPartsForProfileViews(
            Document document,
            CivilDocument civil,
            IEnumerable<ObjectId> selectedViewIds)
        {
            var result = new HashSet<ObjectId>();
            List<ViewChoice> views = ReadSelectedProfileViews(
                document.Database, selectedViewIds);
            if (views.Count == 0) return result.ToList();

            Dictionary<ObjectId, RouteBinding> routeByAlignment =
                ReadWaterRouteBindings(document.Database, civil);
            if (routeByAlignment.Count == 0) return result.ToList();

            var pipeAlignment = new Dictionary<ObjectId, ObjectId>();
            var structuresByAlignment = new Dictionary<ObjectId, HashSet<ObjectId>>();
            List<NetworkChoice> networks = ReadWaterNetworks(document.Database, civil);

            using (Transaction tr = document.Database.TransactionManager.StartTransaction())
            {
                foreach (NetworkChoice choice in networks)
                {
                    CivilNetwork network = tr.GetObject(
                        choice.Id, OpenMode.ForRead, false) as CivilNetwork;
                    if (network == null) continue;

                    // Resolve every pipe once against the tagged source route.
                    foreach (ObjectId pipeId in network.GetPipeIds())
                    {
                        CivilPipe pipe = tr.GetObject(
                            pipeId, OpenMode.ForRead, false) as CivilPipe;
                        if (pipe == null) continue;
                        bool ambiguous, referenceConflict;
                        ObjectId alignmentId = ResolvePipeAlignment(
                            pipe, routeByAlignment, out ambiguous, out referenceConflict);
                        if (!alignmentId.IsNull)
                            pipeAlignment[pipeId] = alignmentId;
                    }

                    // A structure belongs only to the branches of its connected
                    // pipes. Exact source vertices are used only for unconnected nodes.
                    foreach (ObjectId structureId in network.GetStructureIds())
                    {
                        CivilStructure structure = tr.GetObject(
                            structureId, OpenMode.ForRead, false) as CivilStructure;
                        if (structure == null) continue;
                        var owners = new HashSet<ObjectId>();
                        foreach (ObjectId pipeId in SewerPipeConnections.PipeIds(structure))
                        {
                            ObjectId owner;
                            if (pipeAlignment.TryGetValue(pipeId, out owner) && !owner.IsNull)
                                owners.Add(owner);
                        }
                        if (owners.Count == 0)
                            owners = ResolveStructureSourceAlignments(
                                structure, routeByAlignment);
                        structuresByAlignment[structureId] = owners;
                    }
                }
            }

            // Exact profile-view AlignmentId equality is mandatory. No proximity
            // matching or cross-branch fallback is permitted.
            foreach (ViewChoice view in views)
            {
                if (!routeByAlignment.ContainsKey(view.AlignmentId))
                    continue;
                foreach (KeyValuePair<ObjectId, ObjectId> pipe in pipeAlignment)
                    if (pipe.Value == view.AlignmentId)
                        result.Add(pipe.Key);
                foreach (KeyValuePair<ObjectId, HashSet<ObjectId>> structure in structuresByAlignment)
                    if (structure.Value.Contains(view.AlignmentId))
                        result.Add(structure.Key);
            }
            return result.ToList();
        }

        // Backwards-compatible command name; it now uses the same strict
        // source-route matching as the new multi-profile command.
        [CommandMethod("CE_TOOLS", "CE_WATERGRAVITYPROFILEPARTS",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void DrawParts()
        {
            DrawWaterPartsInProfileViews();
        }

        private static void DrawWaterPartsInProfileViews()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            PromptSelectionResult viewSelection = document.Editor.GetSelection(
                new PromptSelectionOptions
                {
                    MessageForAdding =
                        "\nSelect the Water profile views for the corresponding branch alignments: ",
                    AllowDuplicates = false,
                    RejectObjectsFromNonCurrentSpace = true
                });
            if (viewSelection.Status != PromptStatus.OK ||
                viewSelection.Value == null ||
                viewSelection.Value.Count == 0)
                return;
            DrawWaterPartsInProfileViews(document, viewSelection.Value.GetObjectIds());
        }

        /// <summary>
        /// Places ordinary Water gravity-network parts in supplied profile views.
        /// The same strict tagged source-segment matching is used by the interactive
        /// command and by CE_WATERPROFILE immediately after each branch view is made.
        /// Returns the number of native parts added.
        /// </summary>
        internal static int DrawWaterPartsInProfileViews(
            Document document,
            IEnumerable<ObjectId> selectedViewIds)
        {
            CivilDocument civil = CivilApplication.ActiveDocument;
            if (document == null || civil == null) return 0;

            List<NetworkChoice> networks = ReadWaterNetworks(document.Database, civil);
            if (networks.Count == 0)
            {
                document.Editor.WriteMessage(
                    "\nCE_WATERPROFILEPARTSMULTI: no Water gravity networks were found.");
                return 0;
            }

            List<ViewChoice> views = ReadSelectedProfileViews(
                document.Database,
                selectedViewIds ?? Enumerable.Empty<ObjectId>());
            if (views.Count == 0)
            {
                document.Editor.WriteMessage(
                    "\nCE_WATERPROFILEPARTSMULTI: no Civil 3D profile views were found in the selection.");
                return 0;
            }

            Dictionary<ObjectId, RouteBinding> routeByAlignment =
                ReadWaterRouteBindings(document.Database, civil);
            if (routeByAlignment.Count == 0)
            {
                document.Editor.WriteMessage(
                    "\nCE_WATERPROFILEPARTSMULTI: no linked CE water alignments/source polylines were found. " +
                    "Run CE_WATERALIGN on the original source polylines first.");
                return 0;
            }

            int networkCount = 0;
            int pipesConsidered = 0;
            int pipesMatched = 0;
            int structuresConsidered = 0;
            int structuresMatched = 0;
            int unmatchedParts = 0;
            int referenceAlignmentConflicts = 0;
            int ambiguousPipeMatches = 0;
            int drawn = 0;
            int alreadyPresentOrRejected = 0;
            int pipeReferencesVerified = 0;
            int viewsMatched = 0;

            // Resolve every pipe to a route once. The route binding is derived
            // from the original source polyline's tagged handle and exact
            // consecutive source vertices, not from proximity to an alignment.
            var pipeAlignment = new Dictionary<ObjectId, ObjectId>();
            var structureAlignments = new Dictionary<ObjectId, HashSet<ObjectId>>();

            using (Transaction read = document.Database.TransactionManager.StartTransaction())
            {
                foreach (NetworkChoice networkChoice in networks)
                {
                    CivilNetwork network = read.GetObject(
                        networkChoice.Id, OpenMode.ForRead, false) as CivilNetwork;
                    if (network == null) continue;
                    networkCount++;

                    foreach (ObjectId pipeId in network.GetPipeIds())
                    {
                        pipesConsidered++;
                        CivilPipe pipe = read.GetObject(
                            pipeId, OpenMode.ForRead, false) as CivilPipe;
                        if (pipe == null) continue;

                        bool ambiguous;
                        bool referenceConflict;
                        ObjectId alignmentId = ResolvePipeAlignment(
                            pipe,
                            routeByAlignment,
                            out ambiguous,
                            out referenceConflict);
                        if (ambiguous)
                            ambiguousPipeMatches++;
                        if (referenceConflict)
                            referenceAlignmentConflicts++;
                        if (alignmentId.IsNull)
                        {
                            unmatchedParts++;
                            continue;
                        }

                        pipeAlignment[pipeId] = alignmentId;
                        pipesMatched++;
                    }

                    foreach (ObjectId structureId in network.GetStructureIds())
                    {
                        structuresConsidered++;
                        CivilStructure structure = read.GetObject(
                            structureId, OpenMode.ForRead, false) as CivilStructure;
                        if (structure == null) continue;

                        // Connected pipe ownership is authoritative for a structure.
                        // Do not let Civil 3D's RefAlignmentId pull a shared junction
                        // into an unrelated branch profile.
                        var alignmentIds = new HashSet<ObjectId>();
                        foreach (ObjectId connectedPipeId in SewerPipeConnections.PipeIds(structure))
                        {
                            ObjectId connectedAlignment;
                            if (pipeAlignment.TryGetValue(connectedPipeId, out connectedAlignment) &&
                                !connectedAlignment.IsNull)
                                alignmentIds.Add(connectedAlignment);
                        }

                        // For intentionally unconnected parts, map only exact source
                        // route vertices. This also handles a shared loop/junction node
                        // without trusting a possibly generic RefAlignmentId.
                        if (alignmentIds.Count == 0)
                            alignmentIds = ResolveStructureSourceAlignments(
                                structure,
                                routeByAlignment);

                        if (alignmentIds.Count == 0)
                        {
                            unmatchedParts++;
                            continue;
                        }

                        structureAlignments[structureId] = alignmentIds;
                        structuresMatched++;
                    }
                }
            }

            if (pipesMatched == 0 && structuresMatched == 0)
            {
                document.Editor.WriteMessage(
                    "\nCE_WATERPROFILEPARTSMULTI: no network parts could be matched to the tagged source routes. " +
                    "Check that the original source polylines still exist and that CE_WATERALIGN created their linked alignments. " +
                    "Pipes checked={0}; unmatched parts={1}.",
                    pipesConsidered,
                    unmatchedParts);
                return 0;
            }

            viewsMatched = views.Count(view => routeByAlignment.ContainsKey(view.AlignmentId));

            var pipesDrawnByView = new Dictionary<ObjectId, int>();
            var structuresDrawnByView = new Dictionary<ObjectId, int>();
            using (DocumentLock documentLock = document.LockDocument())
            {
                foreach (KeyValuePair<ObjectId, ObjectId> pair in pipeAlignment)
                {
                    foreach (ViewChoice view in views)
                    {
                        // Exact branch alignment equality is required. A pipe
                        // cannot be placed in a merely nearby or intersecting view.
                        if (pair.Value != view.AlignmentId) continue;
                        // Civil 3D uses each pipe's RefAlignmentId when it
                        // computes its station range in a profile view. Bind the
                        // pipe to the exact tagged branch before adding it; otherwise
                        // a newly created/recreated pipe can be projected from 0+000.
                        bool referenceBound = EnsurePipeReferenceAlignment(
                            document.Database, pair.Key, pair.Value);
                        if (referenceBound)
                            pipeReferencesVerified++;
                        if (referenceBound &&
                            AddPartToProfileView(document.Database, pair.Key, view.Id))
                        {
                            drawn++;
                            int count;
                            pipesDrawnByView.TryGetValue(view.Id, out count);
                            pipesDrawnByView[view.Id] = count + 1;
                        }
                        else
                            alreadyPresentOrRejected++;
                    }
                }

                foreach (KeyValuePair<ObjectId, HashSet<ObjectId>> pair in structureAlignments)
                {
                    foreach (ViewChoice view in views)
                    {
                        if (!pair.Value.Contains(view.AlignmentId)) continue;
                        if (AddPartToProfileView(document.Database, pair.Key, view.Id))
                        {
                            drawn++;
                            int count;
                            structuresDrawnByView.TryGetValue(view.Id, out count);
                            structuresDrawnByView[view.Id] = count + 1;
                        }
                        else
                            alreadyPresentOrRejected++;
                    }
                }
            }

            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_WATERPROFILEPARTSMULTI complete. Networks={0}; selected profile views={1}; " +
                "water alignments matched={2}; pipes checked={3}; pipes matched={4}; " +
                "structures checked={5}; structures matched={6}; parts drawn={7}; " +
                "already present/rejected={8}; unmatched parts={9}; ambiguous pipe matches={10}; " +
                "RefAlignment conflicts overridden by source geometry={11}; pipe reference alignments verified={12}. " +
                "Each pipe is assigned by its tagged source-polyline segment before RefAlignment is considered; " +
                "shared structures follow connected pipes or exact source vertices. " +
                "Pipe elevations, slopes, cover, styles and network rules were not changed.",
                networkCount,
                views.Count,
                viewsMatched,
                pipesConsidered,
                pipesMatched,
                structuresConsidered,
                structuresMatched,
                drawn,
                alreadyPresentOrRejected,
                unmatchedParts,
                ambiguousPipeMatches,
                referenceAlignmentConflicts,
                pipeReferencesVerified);

            foreach (ViewChoice view in views)
            {
                RouteBinding route;
                if (!routeByAlignment.TryGetValue(view.AlignmentId, out route))
                {
                    document.Editor.WriteMessage(
                        "\n  ProfileView {0}: skipped - its alignment is not a tagged CE Water branch.",
                        view.Id);
                    continue;
                }
                int branchPipes, branchStructures;
                pipesDrawnByView.TryGetValue(view.Id, out branchPipes);
                structuresDrawnByView.TryGetValue(view.Id, out branchStructures);
                document.Editor.WriteMessage(
                    "\n  {0}: pipes drawn={1}; structures drawn={2}; alignment handle={3}.",
                    route.RouteName,
                    branchPipes,
                    branchStructures,
                    view.AlignmentId.Handle);
            }
            return drawn;
        }

        private static List<NetworkChoice> ReadWaterNetworks(
            Database database,
            CivilDocument civil)
        {
            var result = new List<NetworkChoice>();
            using (Transaction tr = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in civil.GetPipeNetworkIds())
                {
                    CivilNetwork network = tr.GetObject(
                        id, OpenMode.ForRead, false) as CivilNetwork;
                    if (network == null || network.IsReferenceObject) continue;

                    string name = network.Name ?? string.Empty;
                    string description = network.Description ?? string.Empty;
                    bool isWater =
                        name.IndexOf("Water", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.StartsWith("W-", StringComparison.OrdinalIgnoreCase) ||
                        description.IndexOf("CE Tools water network", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!isWater) continue;

                    result.Add(new NetworkChoice(id, name));
                }
            }
            return result;
        }

        private static List<ViewChoice> ReadSelectedProfileViews(
            Database database,
            IEnumerable<ObjectId> selectedIds)
        {
            var result = new List<ViewChoice>();
            using (Transaction tr = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in selectedIds.Distinct())
                {
                    try
                    {
                        ProfileView view = tr.GetObject(
                            id, OpenMode.ForRead, false) as ProfileView;
                        if (view == null || view.IsReferenceObject ||
                            view.AlignmentId.IsNull || view.AlignmentId.IsErased)
                            continue;
                        result.Add(new ViewChoice(id, view.AlignmentId));
                    }
                    catch { }
                }
            }
            return result;
        }

        private static Dictionary<ObjectId, RouteBinding> ReadWaterRouteBindings(
            Database database,
            CivilDocument civil)
        {
            var alignmentBySourceHandle =
                new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);

            using (Transaction tr = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId alignmentId in civil.GetAlignmentIds())
                {
                    CivilAlignment alignment = tr.GetObject(
                        alignmentId, OpenMode.ForRead, false) as CivilAlignment;
                    if (alignment == null || alignment.IsReferenceObject) continue;

                    string type, routeName, sourceHandle, extra;
                    if (!TryReadWaterTag(
                        alignment, out type, out routeName, out sourceHandle, out extra) ||
                        !string.Equals(type, "Alignment", StringComparison.OrdinalIgnoreCase) ||
                        string.IsNullOrWhiteSpace(sourceHandle))
                        continue;

                    alignmentBySourceHandle[sourceHandle] = alignmentId;
                }

                var result = new Dictionary<ObjectId, RouteBinding>();
                BlockTable blocks = (BlockTable)tr.GetObject(
                    database.BlockTableId, OpenMode.ForRead, false);
                BlockTableRecord modelSpace = (BlockTableRecord)tr.GetObject(
                    blocks[BlockTableRecord.ModelSpace], OpenMode.ForRead, false);

                foreach (ObjectId entityId in modelSpace)
                {
                    Entity source = null;
                    try { source = tr.GetObject(entityId, OpenMode.ForRead, false) as Entity; }
                    catch { }
                    if (source == null) continue;

                    string type, routeName, sourceHandle, extra;
                    if (!TryReadWaterTag(
                        source, out type, out routeName, out sourceHandle, out extra) ||
                        !string.Equals(type, "Source", StringComparison.OrdinalIgnoreCase))
                        continue;

                    ObjectId alignmentId;
                    if (!alignmentBySourceHandle.TryGetValue(
                        sourceHandle, out alignmentId))
                        continue;

                    List<Point3d> points = ReadSourcePolylinePoints(source);
                    if (points.Count < 2) continue;

                    result[alignmentId] = new RouteBinding(
                        alignmentId,
                        sourceHandle,
                        routeName,
                        points);
                }
                return result;
            }
        }

        private static List<Point3d> ReadSourcePolylinePoints(Entity source)
        {
            var points = new List<Point3d>();
            Polyline polyline = source as Polyline;
            if (polyline != null)
            {
                for (int i = 0; i < polyline.NumberOfVertices; i++)
                    points.Add(polyline.GetPoint3dAt(i));
                if (polyline.Closed && points.Count > 2)
                    points.Add(points[0]);
                return points;
            }

            // CE_WATERALIGN currently creates its linked route alignments from
            // selected 2D polylines. Other source types are intentionally not
            // guessed here; a future explicit source-type mapper can extend this.
            return points;
        }

        private static ObjectId ResolvePipeAlignment(
            CivilPipe pipe,
            Dictionary<ObjectId, RouteBinding> routeByAlignment,
            out bool ambiguous,
            out bool referenceConflict)
        {
            ambiguous = false;
            referenceConflict = false;
            if (pipe == null) return ObjectId.Null;

            // Source geometry is the primary ownership key. Civil 3D's
            // RefAlignmentId can be shared, generic, or stale on gravity
            // parts in a multi-branch network.
            var matches = new List<ObjectId>();
            foreach (RouteBinding route in routeByAlignment.Values)
            {
                if (MatchesSourceSegment(
                    pipe.StartPoint,
                    pipe.EndPoint,
                    route.Points))
                    matches.Add(route.AlignmentId);
            }

            ObjectId referenced = ResolvePartReferenceAlignment(
                pipe,
                routeByAlignment);

            if (matches.Count == 1)
            {
                if (!referenced.IsNull && referenced != matches[0])
                    referenceConflict = true;
                return matches[0];
            }

            if (matches.Count > 1)
            {
                // Coincident source segments are ambiguous. RefAlignment can
                // disambiguate only when it agrees with one exact geometry match.
                if (!referenced.IsNull && matches.Contains(referenced))
                    return referenced;
                ambiguous = true;
                return ObjectId.Null;
            }

            // No exact source segment match: do not fall back to RefAlignmentId.
            // That fallback can group several branches into one profile view.
            return ObjectId.Null;
        }

        private static HashSet<ObjectId> ResolveStructureSourceAlignments(
            CivilStructure structure,
            Dictionary<ObjectId, RouteBinding> routeByAlignment)
        {
            var matches = new HashSet<ObjectId>();
            if (structure == null) return matches;

            Point3d location = structure.Position;
            foreach (RouteBinding route in routeByAlignment.Values)
            {
                if (MatchesSourceVertex(location, route.Points))
                    matches.Add(route.AlignmentId);
            }
            return matches;
        }

        private static bool MatchesSourceVertex(
            Point3d location,
            IList<Point3d> sourcePoints)
        {
            for (int i = 0; i < sourcePoints.Count; i++)
            {
                if (NearPoint(location, sourcePoints[i]))
                    return true;
            }
            return false;
        }

        private static ObjectId ResolvePartReferenceAlignment(
            Part part,
            Dictionary<ObjectId, RouteBinding> routeByAlignment)
        {
            if (part == null) return ObjectId.Null;

            ObjectId alignmentId = part.RefAlignmentId;
            if (alignmentId.IsNull || alignmentId.IsErased ||
                !routeByAlignment.ContainsKey(alignmentId))
                return ObjectId.Null;

            return alignmentId;
        }

        private static bool MatchesSourceSegment(
            Point3d pipeStart,
            Point3d pipeEnd,
            IList<Point3d> sourcePoints)
        {
            // Compare plan geometry only. Water surface linking changes pipe Z
            // elevations, so a 3D endpoint comparison incorrectly loses the
            // source-route match after the pipes are adjusted to NG cover.
            // A recreated pipe may also start/end between original polyline
            // vertices; accept it when both endpoints lie on the same tagged
            // source segment in XY.
            for (int i = 0; i < sourcePoints.Count - 1; i++)
            {
                Point3d a = sourcePoints[i];
                Point3d b = sourcePoints[i + 1];
                if (PointOnSourceSegmentXY(pipeStart, a, b) &&
                    PointOnSourceSegmentXY(pipeEnd, a, b))
                    return true;
            }
            return false;
        }

        private static bool PointOnSourceSegmentXY(
            Point3d point,
            Point3d start,
            Point3d end)
        {
            double dx = end.X - start.X;
            double dy = end.Y - start.Y;
            double lengthSquared = dx * dx + dy * dy;
            if (lengthSquared <= 1e-12)
                return Math.Sqrt(
                    (point.X - start.X) * (point.X - start.X) +
                    (point.Y - start.Y) * (point.Y - start.Y)) <= SourcePointTolerance;

            double t = ((point.X - start.X) * dx +
                        (point.Y - start.Y) * dy) / lengthSquared;
            double length = Math.Sqrt(lengthSquared);
            double endTolerance = SourcePointTolerance / Math.Max(length, 1e-9);
            if (t < -endTolerance || t > 1.0 + endTolerance)
                return false;

            double projectedX = start.X + t * dx;
            double projectedY = start.Y + t * dy;
            double offsetX = point.X - projectedX;
            double offsetY = point.Y - projectedY;
            return Math.Sqrt(offsetX * offsetX + offsetY * offsetY) <= SourcePointTolerance;
        }

        private static bool NearPoint(Point3d first, Point3d second)
        {
            double dx = first.X - second.X;
            double dy = first.Y - second.Y;
            return Math.Sqrt(dx * dx + dy * dy) <= SourcePointTolerance;
        }

        private static bool TryReadWaterTag(
            DBObject item,
            out string objectType,
            out string routeName,
            out string sourceHandle,
            out string extra)
        {
            objectType = routeName = sourceHandle = extra = string.Empty;
            try
            {
                using (ResultBuffer data = item.GetXDataForApplication(WaterRegApp))
                {
                    if (data == null) return false;
                    string[] values = data.AsArray()
                        .Where(value =>
                            value.TypeCode == (int)DxfCode.ExtendedDataAsciiString)
                        .Select(value => value.Value as string)
                        .Where(value => value != null)
                        .ToArray();
                    if (values.Length < 4) return false;
                    objectType = values[0];
                    routeName = values[1];
                    sourceHandle = values[2];
                    extra = values[3];
                    return true;
                }
            }
            catch { return false; }
        }

        private static bool EnsurePipeReferenceAlignment(            Database database,
            ObjectId pipeId,
            ObjectId alignmentId)
        {
            if (alignmentId.IsNull || alignmentId.IsErased)
                return false;

            try
            {
                using (Transaction tr = database.TransactionManager.StartTransaction())
                {
                    CivilPart part = tr.GetObject(
                        pipeId, OpenMode.ForRead, false) as CivilPart;
                    if (part == null || part.IsReferenceObject)
                        return false;

                    if (part.RefAlignmentId != alignmentId)
                    {
                        part.UpgradeOpen();
                        part.RefAlignmentId = alignmentId;
                    }

                    if (part.RefAlignmentId != alignmentId)
                        return false;

                    tr.Commit();
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool AddPartToProfileView(
            Database database,
            ObjectId partId,
            ObjectId profileViewId)
        {
            try
            {
                using (Transaction edit = database.TransactionManager.StartTransaction())
                {
                    DBObject part = edit.GetObject(
                        partId, OpenMode.ForWrite, false);
                    if (part == null || part.IsErased) return false;

                    MethodInfo add = part.GetType().GetMethod(
                        "AddToProfileView",
                        BindingFlags.Public | BindingFlags.Instance,
                        null,
                        new[] { typeof(ObjectId) },
                        null);
                    if (add == null) return false;

                    add.Invoke(part, new object[] { profileViewId });
                    edit.Commit();
                    return true;
                }
            }
            catch
            {
                // Civil 3D throws when a part is already displayed in a view;
                // keep the batch running and report it as already present/rejected.
                return false;
            }
        }

        private sealed class NetworkChoice
        {
            internal NetworkChoice(ObjectId id, string name)
            {
                Id = id;
                Name = name ?? string.Empty;
            }

            internal ObjectId Id { get; private set; }
            internal string Name { get; private set; }
        }

        private sealed class ViewChoice
        {
            internal ViewChoice(ObjectId id, ObjectId alignmentId)
            {
                Id = id;
                AlignmentId = alignmentId;
            }

            internal ObjectId Id { get; private set; }
            internal ObjectId AlignmentId { get; private set; }
        }

        private sealed class RouteBinding
        {
            internal RouteBinding(
                ObjectId alignmentId,
                string sourceHandle,
                string routeName,
                List<Point3d> points)
            {
                AlignmentId = alignmentId;
                SourceHandle = sourceHandle ?? string.Empty;
                RouteName = routeName ?? string.Empty;
                Points = points ?? new List<Point3d>();
            }

            internal ObjectId AlignmentId { get; private set; }
            internal string SourceHandle { get; private set; }
            internal string RouteName { get; private set; }
            internal List<Point3d> Points { get; private set; }
        }
    }
}
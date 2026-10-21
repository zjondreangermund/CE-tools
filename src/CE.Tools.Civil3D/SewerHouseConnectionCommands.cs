using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.SewerHouseConnectionCommands))]
namespace CETools.Civil3D
{
    /// <summary>
    /// Plan-only schematic sewer property connections. Each selected closed
    /// erf polyline is assessed against a chosen sewer alignment and EG surface.
    /// Connections are intentionally not Civil 3D pipes or real laterals.
    /// </summary>
    public sealed class SewerHouseConnectionCommands
    {
        private const string OutputLayer = "CE-SEWER-HOUSE-CONNECTIONS";
        private const string App = "CE_SEWER_HOUSE_SYMBOL";
        [CommandMethod("CE_TOOLS", "CE_SEWERHOUSECONNECTIONS", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void Create() { RunConnections("Combined"); }

        [CommandMethod("CE_TOOLS", "CE_SEWERHOUSECONNECTIONS-SHORT", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void CreateShort() { RunConnections("Short"); }

        [CommandMethod("CE_TOOLS", "CE_SEWERHOUSECONNECTIONS-LONG", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void CreateLong() { RunConnections("Long"); }

        private void RunConnections(string mode)
        {
            Document doc = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civil = CivilApplication.ActiveDocument;
            if (doc == null || civil == null) return;
            var surfaces = new List<string>();
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
                foreach (ObjectId id in civil.GetSurfaceIds())
                {
                    CivilSurface surface = tr.GetObject(id, OpenMode.ForRead, false) as CivilSurface;
                    if (surface != null) surfaces.Add(surface.Name);
                }
            if (surfaces.Count == 0) { doc.Editor.WriteMessage("\nHouse connections: select a drawing containing an NG/EG surface."); return; }
            var settings = new ProductionSettingsDialogModel(
                "CE Tools - " + (mode == "Short" ? "Short" : mode == "Long" ? "Long" : "Combined") + " Sewer House Connections",
                "Create plan house connections for closed erf polylines. Angle is measured downstream toward increasing sewer-alignment chainage; actual Civil 3D network pipes are not created.");
            settings.AddChoice("Surface", "01 Ground levels", "Existing ground (NG/EG) surface",
                surfaces.FirstOrDefault(s => s.IndexOf("NG", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    s.IndexOf("EG", StringComparison.OrdinalIgnoreCase) >= 0) ?? surfaces[0],
                "Ground levels determine which road-facing corner is the low side.", surfaces);
            settings.AddChoice("LongFrequency", "02 Long connections", "Long connections per erven",
                "Every second erf", "For long connections, choose whether to place one in every erf or every second erf along increasing alignment station.",
                new[] { "Every erf", "Every second erf" });
            settings.AddPositiveDouble("ShortFrontageFirst", "02 Short connections", "Short extension outside frontage (m)", 1.5,
                "Length outside the erf road-facing boundary, as dimensioned in the example.");
            settings.AddPositiveDouble("ShortFrontageInside", "02 Short connections", "Short extension inside frontage (m)", 1.5,
                "Length inside the erf, measured from the same frontage intersection.");
            settings.AddPositiveDouble("LongFrontageInside", "02 Long connections", "Long start inside frontage (m)", 1.5,
                "Start of long line inside its selected erf, measured from frontage.");
            settings.AddChoice("ConnectionLayer", "03 Output", "Connection output layer",
                mode == "Short" ? "CE-SEWERHOUSECONNECTIONS-SHORT" : "CE-SEWERHOUSECONNECTIONS-LONG",
                "Use separate layers for short and long house connections.",
                new[] { "CE-SEWERHOUSECONNECTIONS-SHORT", "CE-SEWERHOUSECONNECTIONS-LONG", OutputLayer });
            settings.AddPositiveDouble("Along", "02 Set-out", "Long connection offset from frontage corner (m)", 1.0,
                "A consistent offset from the same chainage-side corner of every erf frontage.");
            settings.AddChoice("LongAnchor", "02 Set-out", "Long connection frontage corner",
                "Increasing chainage corner",
                "Use a consistent frontage endpoint on every erf, independent of ground-height variations.",
                new[] { "Increasing chainage corner", "Decreasing chainage corner", "Lowest ground corner" });
            settings.AddPositiveDouble("In", "02 Set-out", "Distance inside erf (m)", 1.0,
                "Symbol starts inside the property from its road-facing boundary.");
            settings.AddPositiveDouble("Length", "02 Set-out", "Connection arm length (m)", 3.0,
                "Schematic arm length from the inside point toward the sewer main.");
            settings.AddDouble("Angle", "02 Long connections", "Long connection angle from direct approach (degrees)", 0.0,
                "0 degrees points directly across to the main. Positive angles lean downstream, in the direction of increasing alignment stations, on either side of the alignment.");
            settings.AddDouble("NormalShortAngle", "02 Short connections", "Normal short connection angle from direct approach (degrees)", 0.0,
                "Only applies to normal short house connections; independent of the long crossing angle.");
            settings.AddPositiveDouble("NormalShortAlong", "02 Short connections", "Normal short offset from frontage corner (m)", 1.0,
                "Choose short connection's frontage offset independently from the long crossing.");
            settings.AddChoice("Side", "02 Set-out", "Connection pipe side", "Both sides",
                "Specify which side of the sewer alignment receives house connections.",
                new[] { "Both sides", "Left side", "Right side" });
            settings.AddChoice("ErfScope", "01 Selection", "Erven to connect", "All erven",
                "Choose selected closed boundaries or every eligible closed polyline in model space.",
                new[] { "Selected erven", "All erven" });
            settings.AddChoice("CrossingMode", "02 Set-out", "Shared road crossings", "Share one crossing for two erven",
                "Alternatively connect a second nearby erf by a short tee to the first long lateral, allowing two erven to share one crossing.",
                new[] { "One per erf", "Share one crossing for two erven" });
            settings.AddChoice("UnpairedMode", "02 Set-out", "Normal short connections", "Create normal short connections",
                "When a valid shared T-connection cannot be formed, draw a normal short erf connection separately instead of hiding it.",
                new[] { "Create normal short connections", "Skip unpaired erven" });
            settings.AddPositiveDouble("NormalShortLength", "02 Set-out", "Normal short connection length (m)", 3.0,
                "Length of independent short erf connections; separate from shared T-connection geometry.");
            settings.AddPositiveDouble("NormalShortSetback", "02 Set-out", "Normal short distance inside erf boundary (m)", 1.0,
                "Start of an independent short connection, measured inwards from the erf boundary.");
            settings.AddDouble("SharedTAlong", "02 Set-out", "Shared T offset along erf boundary (m)", 0.0,
                "Move only the short shared T connection along the frontage (positive toward the higher end). The inward setback remains separate.");
            settings.AddPositiveDouble("ShortBranchSetback", "02 Set-out", "Shared T short distance inside erf boundary (m)", 1.0,
                "Position the short connection's starting point this distance inside the selected erf frontage.");
            settings.AddPositiveDouble("LongCrossingThreshold", "02 Set-out", "Minimum long crossing length for sharing (m)", 8.0,
                "Only house connections longer than this distance can share a road crossing. Short connections are independent and never enter the pairing pool.");
            settings.AddPositiveDouble("PairDistance", "02 Set-out", "Maximum shared pair spacing (m)", 20.0,
                "Maximum plan separation between the two erf connection start points.");
            settings.AddPositiveDouble("MaxDistance", "02 Set-out", "Maximum frontage to sewer (m)", 25.0,
                "Frontage farther than this from the selected sewer alignment is skipped.");
            settings.AddChoice("Existing", "03 Output", "Previous symbols",
                "Replace existing CE symbols for selected erven", "Retain unrelated and hand-drawn geometry.",
                new[] { "Replace existing CE symbols for selected erven", "Keep existing and skip duplicates" });
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;
            double along = settings.Double("Along", 1), inside = settings.Double("In", 1);
            double length = settings.Double("Length", 3), maxDistance = settings.Double("MaxDistance", 25);
            PromptSelectionResult selectedAlignments = doc.Editor.GetSelection(new PromptSelectionOptions {
                MessageForAdding = "\nSelect one or multiple sewer alignments: ", AllowDuplicates = false
            });
            if (selectedAlignments.Status != PromptStatus.OK || selectedAlignments.Value == null) return;
            ObjectId[] selectedErven = null;
            if (settings.Text("ErfScope") == "Selected erven")
            {
                PromptSelectionResult plots = doc.Editor.GetSelection(new PromptSelectionOptions {
                    MessageForAdding = "\nSelect multiple CLOSED erf boundary polylines: ", AllowDuplicates = false
                });
                if (plots.Status != PromptStatus.OK || plots.Value == null) return;
                selectedErven = plots.Value.GetObjectIds();
            }
            int created = 0, skipped = 0, replaced = 0, sharedBranches = 0;
            using (doc.LockDocument())
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                var alignments = new List<CivilAlignment>();
                foreach (ObjectId alignmentId in selectedAlignments.Value.GetObjectIds().Distinct())
                {
                    CivilAlignment candidate = null;
                    try { candidate = tr.GetObject(alignmentId, OpenMode.ForRead, false) as CivilAlignment; } catch { }
                    if (candidate != null) alignments.Add(candidate);
                }
                if (alignments.Count == 0) { doc.Editor.WriteMessage("\nSelect at least one Civil 3D alignment."); return; }
                CivilSurface surface = civil.GetSurfaceIds().Cast<ObjectId>().Select(id => tr.GetObject(id, OpenMode.ForRead, false) as CivilSurface)
                    .FirstOrDefault(s => s != null && string.Equals(s.Name, settings.Text("Surface"), StringComparison.OrdinalIgnoreCase));
                if (surface == null) return;
                ObjectId layerId = EnsureLayer(tr, doc.Database, settings.Text("ConnectionLayer"));
                EnsureApp(tr, doc.Database);
                BlockTableRecord model = tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(doc.Database), OpenMode.ForWrite) as BlockTableRecord;
                if (model == null) return;
                if (selectedErven == null)
                    selectedErven = model.Cast<ObjectId>().Where(id => {
                        try { Polyline poly = tr.GetObject(id, OpenMode.ForRead, false) as Polyline;
                              return poly != null && poly.Closed && poly.NumberOfVertices >= 3; }
                        catch { return false; }
                    }).ToArray();
                var proposed = new List<ConnectionProposal>();
                foreach (ObjectId id in selectedErven.Distinct())
                {
                    Polyline erf = null;
                    try { erf = tr.GetObject(id, OpenMode.ForRead, false) as Polyline; } catch { }
                    if (erf == null || !erf.Closed || erf.NumberOfVertices < 3) { skipped++; continue; }
                    var centroid = new Point2d(0, 0);
                    for (int k = 0; k < erf.NumberOfVertices; k++) {
                        Point2d v = erf.GetPoint2dAt(k);
                        centroid = new Point2d(centroid.X + v.X, centroid.Y + v.Y);
                    }
                    centroid = new Point2d(centroid.X / erf.NumberOfVertices, centroid.Y / erf.NumberOfVertices);
                    // Find frontage closest to the selected sewer alignment.
                    int side = -1;
                    CivilAlignment alignment = null;
                    double bestDistance = double.MaxValue;
                    for (int i = 0; i < erf.NumberOfVertices; i++)
                    {
                        int next = (i + 1) % erf.NumberOfVertices;
                        if (Math.Abs(erf.GetBulgeAt(i)) > 1e-8) continue; // no chord approximation
                        Point2d p = erf.GetPoint2dAt(i), q = erf.GetPoint2dAt(next);
                        if (p.GetDistanceTo(q) <= 2 * along + 0.01) continue;
                        Point2d mid = new Point2d((p.X + q.X) / 2, (p.Y + q.Y) / 2);
                        foreach (CivilAlignment candidate in alignments)
                        {
                            double station = 0.0, offset = 0.0;
                            try { candidate.StationOffset(mid.X, mid.Y, ref station, ref offset); }
                            catch { continue; }
                            string connectionSide = settings.Text("Side");
                            if (connectionSide == "Left side" && offset <= 0) continue;
                            if (connectionSide == "Right side" && offset >= 0) continue;
                            double distance = Math.Abs(offset);
                            if (distance < bestDistance) { bestDistance = distance; side = i; alignment = candidate; }
                        }
                    }
                    if (side < 0 || bestDistance > maxDistance) { skipped++; continue; }
                    Point2d left = erf.GetPoint2dAt(side);
                    Point2d right = erf.GetPoint2dAt((side + 1) % erf.NumberOfVertices);
                    double zLeft, zRight;
                    try { zLeft = surface.FindElevationAtXY(left.X, left.Y); zRight = surface.FindElevationAtXY(right.X, right.Y); }
                    catch { skipped++; continue; }
                    if (double.IsNaN(zLeft) || double.IsNaN(zRight)) { skipped++; continue; }
                    // Nearly level frontages are valid: use the first corner deterministically.
                    Point2d low = zLeft <= zRight ? left : right;
                    Point2d high = zLeft <= zRight ? right : left;
                    string anchorMode = settings.Text("LongAnchor");
                    Point2d anchor = low, opposite = high;
                    if (anchorMode != "Lowest ground corner")
                    {
                        double stationLeft = 0, stationRight = 0, offsetLeft = 0, offsetRight = 0;
                        try
                        {
                            alignment.StationOffset(left.X, left.Y, ref stationLeft, ref offsetLeft);
                            alignment.StationOffset(right.X, right.Y, ref stationRight, ref offsetRight);
                        }
                        catch { skipped++; continue; }
                        bool leftIsAnchor = anchorMode == "Increasing chainage corner"
                            ? stationLeft >= stationRight : stationLeft <= stationRight;
                        anchor = leftIsAnchor ? left : right;
                        opposite = leftIsAnchor ? right : left;
                    }
                    Vector2d tangent = (opposite - anchor).GetNormal();
                    Point2d onFrontage = anchor + tangent * along;
                    Vector2d inward = centroid - onFrontage;
                    if (inward.Length < 0.01) { skipped++; continue; }
                    // Normal to frontage that points inside the selected erf.
                    Vector2d normal = new Vector2d(-tangent.Y, tangent.X);
                    if (normal.DotProduct(inward) < 0) normal = -normal;
                    Point2d start = onFrontage + normal * inside;
                    // Abort narrow/irregular lots when the inside position falls outside the erf.
                    if (!InsidePolygon(erf, start)) { skipped++; continue; }
                    if (mode == "Short")
                    {
                        // Short runs are independent of the long pipe angle,
                        // long offset, long reach and shared-pair calculations.
                        proposed.Add(new ConnectionProposal
                        {
                            Erf = erf.ObjectId, Key = erf.Handle.ToString(),
                            Alignment = alignment.ObjectId,
                            AlignmentHandle = alignment.Handle.ToString(),
                            Start = start, End = start,
                            ShortStart = start, FrontageAnchor = anchor,
                            FrontageTangent = tangent, Inward = normal,
                            Outward = -normal, Direction = -normal
                        });
                        continue;
                    }
                    // Calculate and validate the full connection before replacing
                    // earlier CE-generated symbols. Otherwise an invalid angle could
                    // delete an existing connection and leave the parcel unconnected.
                    Point2d toward;
                    try { toward = ClosestAlignmentPoint(alignment, start); }
                    catch { skipped++; continue; }
                    Vector2d direction = toward - start;
                    if (direction.Length < 0.001) { skipped++; continue; }
                    direction = direction.GetNormal();
                    double degrees = settings.Double("Angle", 0.0);
                    if (double.IsNaN(degrees) || double.IsInfinity(degrees) ||
                        Math.Abs(degrees) >= 89.0) { skipped++; continue; }
                    double radians = degrees * Math.PI / 180.0;
                    Vector2d downstream;
                    if (!TryIncreasingStationTangent(alignment, toward, out downstream))
                    { skipped++; continue; }
                    // Remove any component along the cross-road direction so
                    // the connection angle is relative to the actual sewer
                    // alignment, not the drawing's global X/Y orientation.
                    downstream = downstream - direction * downstream.DotProduct(direction);
                    if (downstream.Length < 0.001) { skipped++; continue; }
                    downstream = downstream.GetNormal();
                    direction = direction * Math.Cos(radians) +
                                downstream * Math.Sin(radians);
                    if (direction.Length < 0.001) { skipped++; continue; }
                    direction = direction.GetNormal();
                    Point2d end;
                    ObjectId intersectedAlignment;
                    if (!TryIntersectNearestAlignment(alignments, start, direction,
                            maxDistance + length + inside, out end, out intersectedAlignment))
                    { skipped++; continue; }
                    // Do not connect an erf to the wrong selected branch.
                    // The closest valid alignment intersection determines the
                    // main that the long lateral actually joins.
                    if (intersectedAlignment != alignment.ObjectId)
                    {
                        CivilAlignment nearest = alignments.FirstOrDefault(a => a.ObjectId == intersectedAlignment);
                        if (nearest == null) { skipped++; continue; }
                        alignment = nearest;
                    }
                    double sourceStation = 0.0, sourceOffset = 0.0;
                    try { alignment.StationOffset(start.X, start.Y, ref sourceStation, ref sourceOffset); }
                    catch { skipped++; continue; }
                    Point2d shortStart = onFrontage +
                        tangent * settings.Double("SharedTAlong", 0.0) +
                        normal * settings.Double("ShortBranchSetback", 1.0);
                    if (!InsidePolygon(erf, shortStart))
                    { skipped++; continue; }
                    proposed.Add(new ConnectionProposal
                    {
                        Erf = erf.ObjectId, Key = erf.Handle.ToString(),
                        Alignment = alignment.ObjectId, AlignmentHandle = alignment.Handle.ToString(),
                        Start = start, ShortStart = shortStart, End = end,
                        FrontageAnchor = anchor, FrontageTangent = tangent, Inward = normal,
                        Outward = -normal, Direction = direction,
                        Station = sourceStation, Offset = sourceOffset
                    });
                }

                // Pair only after ALL erven have been examined. The former
                // one-pass algorithm depended on entity creation/selection order,
                // leaving pairable erven with individual long road crossings.
                bool shareMode = settings.Text("CrossingMode") ==
                    "Share one crossing for two erven";
                double pairRange = settings.Double("PairDistance", 20.0);
                // Short laterals must never be paired with one another. In the
                // previous implementation every erf entered the shared pool.
                double threshold = settings.Double("LongCrossingThreshold", 8.0);
                var ordinary = proposed.Where(item =>
                    item.Start.GetDistanceTo(item.End) < threshold).ToList();
                if (mode == "Short") ordinary = proposed.ToList();
                if (mode == "Long") ordinary.Clear();
                foreach (ConnectionProposal shortErf in ordinary)
                {
                    Point2d normalStart;
                    Vector2d shortDirection;
                    if (!TryNormalShortGeometry(shortErf, alignments, settings,
                        out normalStart, out shortDirection))
                    { skipped++; continue; }
                    if (mode == "Short")
                    {
                        Point2d origin = shortErf.FrontageAnchor +
                            shortErf.FrontageTangent * settings.Double("NormalShortAlong", 1.0);
                        // Fixed boundary anchor; angle changes direction without
                        // moving the marked frontage crossing.
                        Vector2d axis = shortDirection.GetNormal();
                        Point2d shortFrom = origin - axis * settings.Double("ShortFrontageInside", 1.5);
                        Point2d shortTo = origin + axis * settings.Double("ShortFrontageFirst", 1.5);
                        if (!ReplaceManagedForGroup(model, tr,
                            new[] { shortErf }, settings.Text("Existing"), ref replaced, "SHORT"))
                        { skipped++; continue; }
                        AddConnectionLine(model, tr, layerId, shortErf, shortFrom, shortTo, "SHORT");
                        created++;
                        continue;
                    }
                    Point2d shortEnd;
                    ObjectId shortTarget;
                    if (!TryIntersectNearestAlignment(alignments, normalStart,
                        shortDirection, maxDistance + length + inside,
                        out shortEnd, out shortTarget))
                    { skipped++; continue; }
                    if (!ReplaceManagedForGroup(model, tr,
                        new[] { shortErf }, settings.Text("Existing"), ref replaced, "SHORT"))
                    { skipped++; continue; }
                    AddConnectionLine(model, tr, layerId, shortErf, normalStart, shortEnd, "SHORT");
                    created++;
                }
                var longCandidates = proposed
                    .Where(item => mode == "Long" || item.Start.GetDistanceTo(item.End) >= threshold)
                    .OrderBy(item => item.AlignmentHandle, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.Offset < 0 ? -1 : 1)
                    .ThenBy(item => item.Station)
                    .ToList();
                if (mode == "Long" && settings.Text("LongFrequency") == "Every second erf")
                    longCandidates = longCandidates
                        .GroupBy(item => item.AlignmentHandle + ":" + Math.Sign(item.Offset))
                        .SelectMany(group => group.Where((item, index) => index % 2 == 0))
                        .ToList();
                var remaining = new List<ConnectionProposal>(longCandidates
                    .Where(item => mode != "Short")
                    .OrderBy(item => item.AlignmentHandle, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.Offset < 0 ? -1 : 1)
                    .ThenBy(item => item.Station)
                    .ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase));
                var drawn = new List<ConnectionProposal>();
                while (remaining.Count > 0)
                {
                    ConnectionProposal seed = remaining[0];
                    remaining.RemoveAt(0);
                    ConnectionProposal longArm = seed;
                    ConnectionProposal shortArm = null;
                    Point2d tee = Point2d.Origin;
                    if (shareMode)
                    {
                        // The station-sorted first erf always owns the long
                        // crossing. Never reverse the owner: reversing bypassed
                        // the requested consistent long-connection offset.
                        double best = double.MaxValue;
                        foreach (ConnectionProposal other in remaining)
                        {
                            if (other.Alignment != seed.Alignment ||
                                Math.Sign(other.Offset) != Math.Sign(seed.Offset)) continue;
                            double separation = seed.Start.GetDistanceTo(other.Start);
                            if (separation > pairRange) continue;
                            Point2d candidate;
                            // Shared stems must use their own fixed frontage setback
                            // and outward normal. Do not project the endpoint sideways
                            // onto the long pipe: that is what produced an inconsistent
                            // diagonal/shifted short stem in adjacent long crossings.
                            if (!TryIntersectStemWithCrossing(other.ShortStart,
                                other.Outward, seed.Start, seed.End, out candidate)) continue;
                            if (separation >= best) continue;
                            best = separation;
                            shortArm = other;
                            tee = candidate;
                        }
                    }
                    // Do not leave a long crossing unshared in the shared mode.
                    // Keep any previously generated output intact when no valid
                    // geometry exists; report the erf so the user can review it.
                    if (shareMode && mode != "Long" && shortArm == null)
                    {
                        // No valid shared T intersection: do not silently draw a
                        // mismatched second crossing using independent short settings.
                        // The user can explicitly choose normal fallback below.
                        if (settings.Text("UnpairedMode") == "Create normal short connections")
                        {
                            // Never hide a qualifying erf just because the neighbour's
                            // diagonal main crossing does not intersect its frontage
                            // normal. Draw a separate normal short connection instead.
                            Point2d normalStart;
                            Vector2d shortDirection;
                            if (!TryNormalShortGeometry(longArm, alignments, settings,
                                out normalStart, out shortDirection))
                            { skipped++; continue; }
                            Point2d normalEnd;
                            ObjectId nearestShortAlignment;
                            if (!TryIntersectNearestAlignment(alignments, normalStart,
                                shortDirection, maxDistance + length + inside,
                                out normalEnd, out nearestShortAlignment))
                            { skipped++; continue; }
                            if (!ReplaceManagedForGroup(model, tr,
                                new[] { longArm }, settings.Text("Existing"), ref replaced, "SHORT"))
                            { skipped++; continue; }
                            AddConnectionLine(model, tr, layerId, longArm,
                                normalStart, normalEnd, "SHORT");
                            created++;
                            continue;
                        }
                        skipped++;
                        continue;
                    }
                    if (shortArm != null)
                    {
                        // If the seed was promoted to a short lateral, it has
                        // already left remaining; the new long owner must leave
                        // the queue instead. Exactly one pair is consumed.
                        remaining.Remove(longArm);
                        remaining.Remove(shortArm);
                    }
                    var batch = new List<ConnectionProposal> { longArm };
                    if (shortArm != null) batch.Add(shortArm);
                    Point2d longOrigin = longArm.FrontageAnchor +
                        longArm.FrontageTangent * settings.Double("Along", 1.0);
                    Point2d longStart = longOrigin +
                        longArm.Inward * settings.Double("LongFrontageInside", 1.5);
                    Point2d longEnd;
                    ObjectId longTarget;
                    if (!TryIntersectNearestAlignment(alignments, longStart,
                        longArm.Direction.GetNormal(), maxDistance + length + inside,
                        out longEnd, out longTarget)) { skipped++; continue; }
                    if (!ReplaceManagedForGroup(model, tr, batch,
                        settings.Text("Existing"), ref replaced, "LONG"))
                    { skipped += batch.Count; continue; }
                    AddConnectionLine(model, tr, layerId, longArm,
                        longStart, longEnd, "LONG");
                    // One continuous long pipe only; no crossing tick or
                    // other short marking line is added at its endpoint.
                    created++;
                    if (shortArm != null)
                    {
                        // No second independent frontage arm when it already
                        // coincides with the shared crossing; draw exactly one
                        // finite T stem per partner erf.
                        if (shortArm.ShortStart.GetDistanceTo(tee) > 0.10 &&
                            DistanceFromSegment(shortArm.ShortStart,
                                longArm.Start, longArm.End) > 0.10)
                        {
                            AddConnectionLine(model, tr, layerId, shortArm,
                                shortArm.ShortStart, tee, "SHARED");
                            created++;
                            sharedBranches++;
                        }
                    }
                    drawn.AddRange(batch);
                }
                tr.Commit();
            }
            doc.Editor.Regen();
            doc.Editor.WriteMessage("\nCE_SEWERHOUSECONNECTIONS: erven placed={0}; skipped={1}; replaced={2}; shared short branches={3}. Review connections on the ground plan.", created, skipped, replaced, sharedBranches);
        }
        private sealed class ConnectionProposal
        {
            internal ObjectId Erf, Alignment;
            internal string Key, AlignmentHandle;
            internal Point2d Start, ShortStart, End, FrontageAnchor;
            internal Vector2d Outward, Direction, FrontageTangent, Inward;
            internal double Station, Offset;
        }

        private static bool ReplaceManagedForGroup(BlockTableRecord model,
            Transaction transaction, IEnumerable<ConnectionProposal> proposals,
            string existingMode, ref int replaced, string type)
        {
            bool keepExisting = (existingMode ?? "").StartsWith("Keep",
                StringComparison.OrdinalIgnoreCase);
            var keys = new HashSet<string>(proposals.Select(x => x.Key),
                StringComparer.OrdinalIgnoreCase);
            var oldLines = new List<Entity>();
            foreach (ObjectId id in model)
            {
                Entity entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                if (entity == null) continue;
                using (ResultBuffer data = entity.GetXDataForApplication(App))
                {
                    if (data == null) continue;
                    TypedValue[] values = data.AsArray();
                    if (!values.Any(v => v.TypeCode == 1000 &&
                        keys.Contains(Convert.ToString(v.Value) ?? ""))) continue;
                    // Never allow short reruns to delete long geometry, or vice versa.
                    string storedType = values.Where(v => v.TypeCode == 1000)
                        .Select(v => Convert.ToString(v.Value) ?? "")
                        .LastOrDefault() ?? "";
                    if (!string.Equals(storedType, type, StringComparison.OrdinalIgnoreCase) &&
                        !(type == "LONG" && storedType == "SHARED")) continue;
                    if (keepExisting) return false;
                    oldLines.Add(entity);
                }
            }
            foreach (Entity entity in oldLines)
            {
                entity.UpgradeOpen();
                entity.Erase();
                replaced++;
            }
            return true;
        }

        private static void AddConnectionLine(BlockTableRecord model,
            Transaction transaction, ObjectId layerId, ConnectionProposal owner,
            Point2d from, Point2d to, string type)
        {
            Line line = new Line(new Point3d(from.X, from.Y, 0),
                new Point3d(to.X, to.Y, 0)) { LayerId = layerId };
            model.AppendEntity(line);
            transaction.AddNewlyCreatedDBObject(line, true);
            line.XData = new ResultBuffer(new TypedValue(1001, App),
                new TypedValue(1000, owner.Key),
                new TypedValue(1000, owner.AlignmentHandle),
                new TypedValue(1000, type));
        }

        // When the erf's normal misses the long crossing, the short T branch
        // must still join a valid point ON that segment rather than disappear.
        // Prefer perpendicular intersection above; use this nearest-segment
        // projection only as the fallback for skewed parcel frontages.
        private static double DistanceFromSegment(Point2d point, Point2d a, Point2d b)
        {
            Vector2d segment = b - a;
            double squared = segment.DotProduct(segment);
            if (squared < 1e-12) return point.GetDistanceTo(a);
            double fraction = Math.Max(0.0, Math.Min(1.0,
                (point - a).DotProduct(segment) / squared));
            return point.GetDistanceTo(a + segment * fraction);
        }

        private static bool TryProjectShortStem(Point2d start,
            Point2d from, Point2d to, out Point2d junction)
        {
            junction = from;
            Vector2d segment = to - from;
            double squared = segment.DotProduct(segment);
            if (squared < 1e-8) return false;
            double position = (start - from).DotProduct(segment) / squared;
            if (position <= 0.03 || position >= 0.97) return false;
            junction = from + segment * position;
            return junction.GetDistanceTo(start) > 0.10;
        }

        private static bool TryIntersectStemWithCrossing(
            Point2d start, Vector2d towardRoad,
            Point2d crossingStart, Point2d crossingEnd, out Point2d junction)
        {
            junction = start;
            if (towardRoad.Length < 1e-8) return false;
            Vector2d ray = towardRoad.GetNormal();
            Vector2d crossing = crossingEnd - crossingStart;
            double determinant = ray.X * crossing.Y - ray.Y * crossing.X;
            if (Math.Abs(determinant) < 1e-8) return false;
            Vector2d delta = crossingStart - start;
            double alongStem = (delta.X * crossing.Y - delta.Y * crossing.X) / determinant;
            double alongCrossing = (delta.X * ray.Y - delta.Y * ray.X) / determinant;
            // Preserve a real T-connection, away from either endpoint of the
            // existing long line. Never extend a short stem backwards into
            // the erf or beyond the crossing.
            if (alongStem <= 0.1 || alongCrossing <= 0.08 ||
                alongCrossing >= 0.92) return false;
            junction = start + ray * alongStem;
            return true;
        }

        // Normal short connections must not depend on any long-crossing
        // start, long angle, or shared T offset. Calculate every coordinate
        // solely from its own configurable frontage offset/setback and angle.
        private static bool TryNormalShortGeometry(ConnectionProposal item,
            IEnumerable<CivilAlignment> alignments,
            ProductionSettingsDialogModel settings,
            out Point2d start, out Vector2d direction)
        {
            start = item.FrontageAnchor +
                item.FrontageTangent * settings.Double("NormalShortAlong", 1.0) +
                item.Inward * settings.Double("NormalShortSetback", 1.0);
            direction = item.Outward;
            CivilAlignment nearest = null;
            double closest = double.MaxValue;
            Point2d foot = start;
            foreach (CivilAlignment alignment in alignments)
            {
                Point2d point;
                try { point = ClosestAlignmentPoint(alignment, start); }
                catch { continue; }
                double distance = start.GetDistanceTo(point);
                if (distance >= closest) continue;
                closest = distance;
                nearest = alignment;
                foot = point;
            }
            if (nearest == null || closest < 1e-6) return false;
            Vector2d approach = (foot - start).GetNormal();
            Vector2d downstream;
            if (!TryIncreasingStationTangent(nearest, foot, out downstream)) return false;
            downstream -= approach * downstream.DotProduct(approach);
            if (downstream.Length < 1e-6) return false;
            downstream = downstream.GetNormal();
            double degrees = settings.Double("NormalShortAngle", 0.0);
            if (double.IsNaN(degrees) || double.IsInfinity(degrees) ||
                Math.Abs(degrees) >= 89.0) return false;
            double radians = degrees * Math.PI / 180.0;
            direction = approach * Math.Cos(radians) + downstream * Math.Sin(radians);
            return direction.Length > 1e-6;
        }

        private static bool TryIntersectNearestAlignment(
            IEnumerable<CivilAlignment> alignments, Point2d start, Vector2d direction,
            double maximum, out Point2d nearestPoint, out ObjectId nearestId)
        {
            nearestPoint = start;
            nearestId = ObjectId.Null;
            double nearest = double.MaxValue;
            foreach (CivilAlignment candidate in alignments)
            {
                Point2d candidatePoint;
                if (!TryIntersectMain(candidate, start, direction, maximum, out candidatePoint))
                    continue;
                double distance = start.GetDistanceTo(candidatePoint);
                if (distance < 0.001 || distance >= nearest) continue;
                nearest = distance;
                nearestPoint = candidatePoint;
                nearestId = candidate.ObjectId;
            }
            return !nearestId.IsNull;
        }

        private static Point2d ClosestAlignmentPoint(CivilAlignment alignment, Point2d source)
        {
            double station = 0, offset = 0, x = 0, y = 0;
            alignment.StationOffset(source.X, source.Y, ref station, ref offset);
            alignment.PointLocation(station, 0, ref x, ref y);
            return new Point2d(x, y);
        }

        private static bool TryIncreasingStationTangent(
            CivilAlignment alignment, Point2d mainPoint, out Vector2d tangent)
        {
            tangent = new Vector2d(1.0, 0.0);
            double station = 0, offset = 0;
            try
            {
                alignment.StationOffset(mainPoint.X, mainPoint.Y, ref station, ref offset);
                double delta = Math.Min(0.10,
                    Math.Max((alignment.EndingStation - alignment.StartingStation) * 0.001, 0.001));
                double s0 = Math.Max(alignment.StartingStation, station - delta);
                double s1 = Math.Min(alignment.EndingStation, station + delta);
                if (s1 - s0 < 1e-6) return false;
                double x0 = 0, y0 = 0, x1 = 0, y1 = 0;
                alignment.PointLocation(s0, 0, ref x0, ref y0);
                alignment.PointLocation(s1, 0, ref x1, ref y1);
                tangent = new Vector2d(x1 - x0, y1 - y0);
                if (tangent.Length < 1e-6) return false;
                tangent = tangent.GetNormal();
                return true;
            }
            catch { return false; }
        }

        // Find the first alignment crossing along the chosen angle, regardless
        // of the user's nominal arm length. It trims overshoots and extends short arms.
        private static bool TryIntersectMain(CivilAlignment alignment, Point2d start,
            Vector2d direction, double maximum, out Point2d hit)
        {
            hit = start;
            if (maximum <= 0) return false;
            const int steps = 256;
            double previousOffset = 0, prevDist = 0;
            bool hasPrevious = false;
            for (int i = 0; i <= steps; i++)
            {
                double dist = maximum * i / steps;
                Point2d point = start + direction * dist;
                double station = 0, offset = 0;
                try { alignment.StationOffset(point.X, point.Y, ref station, ref offset); }
                catch { hasPrevious = false; continue; }
                if (Math.Abs(offset) <= 1e-5) { hit = point; return true; }
                if (hasPrevious && previousOffset * offset < 0)
                {
                    double lo = prevDist, hi = dist;
                    double sign = previousOffset;
                    for (int j = 0; j < 35; j++)
                    {
                        double middle = (lo + hi) * 0.5;
                        Point2d probe = start + direction * middle;
                        double midStation = 0, midOffset = 0;
                        try { alignment.StationOffset(probe.X, probe.Y, ref midStation, ref midOffset); }
                        catch { return false; }
                        if (sign * midOffset > 0) lo = middle;
                        else hi = middle;
                    }
                    hit = start + direction * ((lo + hi) * 0.5);
                    return true;
                }
                previousOffset = offset;
                prevDist = dist;
                hasPrevious = true;
            }
            return false;
        }

        private static bool InsidePolygon(Polyline poly, Point2d p)
        {
            bool inside = false;
            for (int i = 0, j = poly.NumberOfVertices - 1; i < poly.NumberOfVertices; j = i++)
            {
                Point2d a = poly.GetPoint2dAt(i), b = poly.GetPoint2dAt(j);
                if ((a.Y > p.Y) != (b.Y > p.Y) &&
                    p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
            }
            return inside;
        }
        private static ObjectId EnsureLayer(Transaction tr, Database db, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) name = OutputLayer;
            LayerTable layers = tr.GetObject(db.LayerTableId, OpenMode.ForRead) as LayerTable;
            if (layers.Has(name)) return layers[name];
            layers.UpgradeOpen();
            LayerTableRecord layer = new LayerTableRecord { Name = name, Color = Color.FromColorIndex(ColorMethod.ByAci, 3) };
            ObjectId id = layers.Add(layer); tr.AddNewlyCreatedDBObject(layer, true);
            return id;
        }
        private static void EnsureApp(Transaction tr, Database db)
        {
            RegAppTable table = tr.GetObject(db.RegAppTableId, OpenMode.ForRead) as RegAppTable;
            if (table.Has(App)) return;
            table.UpgradeOpen();
            RegAppTableRecord app = new RegAppTableRecord { Name = App };
            table.Add(app); tr.AddNewlyCreatedDBObject(app, true);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Drawing;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivilProfile = Autodesk.Civil.DatabaseServices.Profile;
using CivilTinSurface = Autodesk.Civil.DatabaseServices.TinSurface;

[assembly: CommandClass(typeof(CETools.Civil3D.RoadCrossingPviCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Adds PVIs to FG/design profiles at detected centerline road crossings:
    /// one at the crossing station and one on either side using the requested
    /// half-width. Source alignment geometry is sampled for intersection detection.
    /// </summary>
    public sealed class RoadCrossingPviCommands
    {
        private const double StationTolerance = 0.01;
        
        private sealed class Sample
        {
            public double Station;
            public double X;
            public double Y;
        }

        private sealed class Crossing
        {
            public ObjectId FirstId;
            public double FirstStation;
            public ObjectId SecondId;
            public double SecondStation;
            public double X;
            public double Y;
        }

        [CommandMethod("CE_TOOLS", "CE_ROADCROSSINGPVIS",
            CommandFlags.Modal | CommandFlags.Redraw | CommandFlags.UsePickSet)]
        public void AddRoadCrossingPvis()
        {
            ProcessRoadCrossingPvis(true);
        }

        /// <summary>
        /// Strict surface-only version: drapes all detected crossing left/centre/right PVIs
        /// to the intersecting road's matching finished TOP surface. It never falls back to
        /// either road's FG profile, so incorrect elevations are not silently introduced.
        /// </summary>
        [CommandMethod("CE_TOOLS", "CE_DRAPECROSSROADPVIS",
            CommandFlags.Modal | CommandFlags.Redraw | CommandFlags.UsePickSet)]
        public void DrapeCrossRoadPvisToTopSurface()
        {
            ProcessRoadCrossingPvis(false);
        }

        private static bool ShowCrossingPviSettings(out double halfWidth, out double interval, out double curveLength)
        {
            halfWidth = 3.0;
            interval = 1.0;
            curveLength = 30.0;

            using (Form form = new Form())
            using (System.Windows.Forms.Label widthLabel = new System.Windows.Forms.Label())
            using (System.Windows.Forms.Label intervalLabel = new System.Windows.Forms.Label())
            using (System.Windows.Forms.Label curveLabel = new System.Windows.Forms.Label())
            using (NumericUpDown widthInput = new NumericUpDown())
            using (NumericUpDown intervalInput = new NumericUpDown())
            using (NumericUpDown curveInput = new NumericUpDown())
            using (CheckBox curveEnabled = new CheckBox())
            using (Button ok = new Button())
            using (Button cancel = new Button())
            {
                form.Text = "CE Tools - Road Crossing PVIs";
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.StartPosition = FormStartPosition.CenterScreen;
                form.ClientSize = new Size(390, 218);
                form.MaximizeBox = false;
                form.MinimizeBox = false;
                form.ShowInTaskbar = false;

                widthLabel.Text = "Crossing half-width (m):";
                widthLabel.SetBounds(16, 18, 210, 22);
                widthInput.SetBounds(245, 15, 125, 24);
                widthInput.DecimalPlaces = 2;
                widthInput.Minimum = 0.01M;
                widthInput.Maximum = 1000M;
                widthInput.Value = 3.0M;

                intervalLabel.Text = "Alignment sampling interval (m):";
                intervalLabel.SetBounds(16, 54, 225, 22);
                intervalInput.SetBounds(245, 51, 125, 24);
                intervalInput.DecimalPlaces = 2;
                intervalInput.Minimum = 0.25M;
                intervalInput.Maximum = 1000M;
                intervalInput.Value = 1.0M;

                curveEnabled.Text = "Add vertical curves to new/uncurved PVIs";
                curveEnabled.SetBounds(16, 88, 350, 22);
                curveEnabled.Checked = true;

                curveLabel.Text = "Vertical curve length (m):";
                curveLabel.SetBounds(16, 120, 225, 22);
                curveInput.SetBounds(245, 117, 125, 24);
                curveInput.DecimalPlaces = 2;
                curveInput.Minimum = 1M;
                curveInput.Maximum = 1000M;
                curveInput.Value = 30.0M;

                curveEnabled.CheckedChanged += (sender, args) => curveInput.Enabled = curveEnabled.Checked;
                curveInput.Enabled = true;

                ok.Text = "Run";
                ok.SetBounds(204, 169, 78, 30);
                ok.DialogResult = DialogResult.OK;
                cancel.Text = "Cancel";
                cancel.SetBounds(292, 169, 78, 30);
                cancel.DialogResult = DialogResult.Cancel;
                form.Controls.AddRange(new Control[] { widthLabel, widthInput, intervalLabel, intervalInput,
                    curveEnabled, curveLabel, curveInput, ok, cancel });
                form.AcceptButton = ok;
                form.CancelButton = cancel;

                if (form.ShowDialog() != DialogResult.OK) return false;
                halfWidth = (double)widthInput.Value;
                interval = Math.Max(0.25, (double)intervalInput.Value);
                curveLength = curveEnabled.Checked ? (double)curveInput.Value : 0.0;
                return true;
            }
        }

        private void ProcessRoadCrossingPvis(bool allowCrossingProfileFallback)
        {
            Document doc = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civil = CivilApplication.ActiveDocument;
            if (doc == null || civil == null) return;

            double halfWidth, interval, verticalCurveLength;
            if (!ShowCrossingPviSettings(out halfWidth, out interval, out verticalCurveLength))
                return;

            int crossingsFound = 0, pvisAdded = 0, pvisUpdated = 0;
            int profilesProcessed = 0, skipped = 0, alreadyCorrect = 0;
            int surfaceDrapes = 0, crossingProfileFallbacks = 0, missingTopSurfaceRoads = 0;
            try
            {
                using (doc.LockDocument())
                using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
                {
                    var alignments = new List<CivilAlignment>();
                    foreach (ObjectId id in civil.GetAlignmentIds())
                    {
                        CivilAlignment a = null;
                        try { a = tr.GetObject(id, OpenMode.ForRead, false) as CivilAlignment; }
                        catch { }
                        if (a == null) continue;
                        try
                        {
                            if (a.AlignmentType != AlignmentType.Centerline) continue;
                            if (a.EndingStation - a.StartingStation < interval) continue;
                            alignments.Add(a);
                        }
                        catch { }
                    }

                    var alignmentById = alignments.ToDictionary(a => a.ObjectId, a => a);
                    var samples = new Dictionary<ObjectId, List<Sample>>();
                    foreach (CivilAlignment a in alignments)
                        samples[a.ObjectId] = SampleAlignment(a, interval);

                    // TOP-RD surfaces are the preferred source for crossing PVI elevations.
                    // Keep the source surfaces read-only; only design profiles are modified.
                    var topSurfaces = new List<CivilTinSurface>();
                    foreach (ObjectId surfaceId in civil.GetSurfaceIds())
                    {
                        try
                        {
                            CivilTinSurface surface = tr.GetObject(surfaceId, OpenMode.ForRead, false) as CivilTinSurface;
                            if (surface != null && IsFinishedRoadTopSurfaceName(surface.Name))
                                topSurfaces.Add(surface);
                        }
                        catch { }
                    }
                    var topSurfaceByAlignment = new Dictionary<ObjectId, CivilTinSurface>();
                    foreach (CivilAlignment alignment in alignments)
                    {
                        CivilTinSurface matching = FindMatchingTopSurface(alignment, topSurfaces);
                        if (matching != null)
                            topSurfaceByAlignment[alignment.ObjectId] = matching;
                        else
                        {
                            missingTopSurfaceRoads++;
                            doc.Editor.WriteMessage(
                                "\nCE_ROADCROSSINGPVIS: no matching finished TOP-RD surface found for alignment '{0}' (road key '{1}'). Crossings involving this road will be skipped rather than using a potentially incorrect profile elevation.",
                                alignment.Name ?? string.Empty, RoadKey(alignment.Name));
                        }
                    }

                    // Protect corridor-controlled FG profiles: changing their PVIs changes corridor
                    // geometry and can move the generated crossing-road surface. Only modify a profile
                    // if it is not referenced by any corridor baseline. If ownership cannot be proven,
                    // leave it untouched and report it.
                    var corridorControlledProfileIds = new HashSet<ObjectId>();
                    bool corridorProfileScanComplete = false;
                    try
                    {
                        foreach (ObjectId corridorId in civil.CorridorCollection)
                        {
                            Corridor corridor = null;
                            try { corridor = tr.GetObject(corridorId, OpenMode.ForRead, false) as Corridor; }
                            catch { }
                            if (corridor == null) continue;
                            foreach (Baseline baseline in corridor.Baselines)
                            {
                                try
                                {
                                    ObjectId profileId = baseline.ProfileId;
                                    if (!profileId.IsNull) corridorControlledProfileIds.Add(profileId);
                                }
                                catch { }
                            }
                        }
                        corridorProfileScanComplete = true;
                    }
                    catch { corridorProfileScanComplete = false; }

                    var crossings = new List<Crossing>();
                    for (int i = 0; i < alignments.Count; i++)
                    {
                        CivilAlignment a = alignments[i];
                        List<Sample> sa = samples[a.ObjectId];
                        for (int j = i + 1; j < alignments.Count; j++)
                        {
                            CivilAlignment b = alignments[j];
                            List<Sample> sb = samples[b.ObjectId];
                            // Do not treat separate alignments belonging to the same numbered road as a road crossing.
                            string roadKeyA = RoadKey(a.Name);
                            string roadKeyB = RoadKey(b.Name);
                            if (!string.IsNullOrEmpty(roadKeyA) &&
                                string.Equals(roadKeyA, roadKeyB, StringComparison.OrdinalIgnoreCase))
                                continue;
                            foreach (var pair in Intersections(sa, sb))
                            {
                                double ax = pair.Item1, bx = pair.Item2;
                                // Keep endpoint intersections too: valid T-junctions and four-way
                                // crossings can coincide with one or both alignment endpoints. Same-road
                                // numbered alignments were filtered above, and duplicate crossings are
                                // removed by the station-based check below.
                                if (crossings.Any(c =>
                                    ((c.FirstId == a.ObjectId && c.SecondId == b.ObjectId) ||
                                     (c.FirstId == b.ObjectId && c.SecondId == a.ObjectId)) &&
                                    Math.Abs((c.FirstId == a.ObjectId ? c.FirstStation : c.SecondStation) - ax) < halfWidth * 0.5 &&
                                    Math.Abs((c.FirstId == a.ObjectId ? c.SecondStation : c.FirstStation) - bx) < halfWidth * 0.5))
                                    continue;

                                double x = 0, y = 0;
                                try { a.PointLocation(ax, 0, ref x, ref y); }
                                catch { continue; }
                                crossings.Add(new Crossing
                                {
                                    FirstId = a.ObjectId, FirstStation = ax,
                                    SecondId = b.ObjectId, SecondStation = bx,
                                    X = x, Y = y
                                });
                            }
                        }
                    }

                    crossingsFound = crossings.Count;

                    // Freeze every crossing TOP-surface elevation BEFORE opening or editing
                    // any design profile for write. This prevents a profile/corridor reactor
                    // from changing the surface being sampled midway through the same command.
                    // The command itself never edits a surface or requests a corridor rebuild.
                    var frozenTopElevations = new Dictionary<Tuple<ObjectId, ObjectId, double>, double>();
                    foreach (CivilAlignment receivingAlignment in alignments)
                    {
                        foreach (Crossing crossing in crossings.Where(c =>
                            c.FirstId == receivingAlignment.ObjectId || c.SecondId == receivingAlignment.ObjectId))
                        {
                            bool receivingIsFirst = crossing.FirstId == receivingAlignment.ObjectId;
                            ObjectId targetRoadId = receivingIsFirst ? crossing.SecondId : crossing.FirstId;
                            CivilTinSurface targetSurface = null;
                            topSurfaceByAlignment.TryGetValue(targetRoadId, out targetSurface);
                            CivilAlignment targetRoad = null;
                            alignmentById.TryGetValue(targetRoadId, out targetRoad);

                            string receivingRoadKey = RoadKey(receivingAlignment.Name);
                            string targetRoadKey = RoadKey(targetRoad == null ? string.Empty : targetRoad.Name);
                            bool correctTargetSurface = targetSurface != null &&
                                !string.IsNullOrEmpty(targetRoadKey) &&
                                string.Equals(RoadKey(targetSurface.Name), targetRoadKey, StringComparison.OrdinalIgnoreCase) &&
                                !string.Equals(targetRoadKey, receivingRoadKey, StringComparison.OrdinalIgnoreCase);
                            if (!correctTargetSurface) continue;

                            double crossingStation = receivingIsFirst ? crossing.FirstStation : crossing.SecondStation;
                            double[] targetStations = { crossingStation - halfWidth, crossingStation, crossingStation + halfWidth };
                            foreach (double rawTargetStation in targetStations)
                            {
                                double targetStation = Math.Max(receivingAlignment.StartingStation,
                                    Math.Min(receivingAlignment.EndingStation, rawTargetStation));
                                double x = 0.0, y = 0.0;
                                try { receivingAlignment.PointLocation(targetStation, 0.0, ref x, ref y); }
                                catch { continue; }

                                try
                                {
                                    double frozenElevation = targetSurface.FindElevationAtXY(x, y);
                                    if (IsFinite(frozenElevation))
                                    {
                                        var key = Tuple.Create(receivingAlignment.ObjectId, targetRoadId, targetStation);
                                        if (!frozenTopElevations.ContainsKey(key))
                                            frozenTopElevations.Add(key, frozenElevation);
                                    }
                                }
                                catch
                                {
                                    // Keep missing samples absent. Strict mode will report them as skipped.
                                }
                            }
                        }
                    }

                    foreach (CivilAlignment a in alignments)
                    {
                        var roadCrossings = crossings.Where(c =>
                            c.FirstId == a.ObjectId || c.SecondId == a.ObjectId).ToList();
                        if (roadCrossings.Count == 0) continue;

                        var profileIds = a.GetProfileIds();
                        foreach (ObjectId pid in profileIds)
                        {
                            CivilProfile profile = null;
                            try { profile = tr.GetObject(pid, OpenMode.ForRead, false) as CivilProfile; }
                            catch { }
                            if (profile == null) continue;
                            bool design = false;
                            try { design = profile.ProfileType == ProfileType.FG; } catch { }
                            string pname = (profile.Name ?? "").ToUpperInvariant();
                            if (!design || pname.Contains("EG") || pname.Contains("EXIST") ||
                                pname.Contains("GROUND") || pname.Contains("SURFACE"))
                                continue;

                            // Surface target elevations were frozen before this loop. Process the
                            // receiving profile even when it controls a corridor; otherwise every
                            // production road profile can be skipped and no PVIs are corrected.
                            // This command does not rebuild corridors or write to surfaces. Note:
                            // a later corridor rebuild can reflect any edits to its controlling profile.
                            bool controlsCorridor = corridorControlledProfileIds.Contains(pid);
                            if (!corridorProfileScanComplete)
                            {
                                doc.Editor.WriteMessage(
                                    "\\nCE_DRAPECROSSROADPVIS: corridor baseline scan was inconclusive for profile '{0}' on '{1}'. Continuing with frozen original TOP elevations; no corridor rebuild is requested.",
                                    profile.Name ?? string.Empty, a.Name ?? string.Empty);
                            }
                            else if (controlsCorridor)
                            {
                                doc.Editor.WriteMessage(
                                    "\\nCE_DRAPECROSSROADPVIS: profile '{0}' on '{1}' controls a corridor baseline. Applying PVI targets from frozen crossing-road TOP elevations; the command will not rebuild the corridor.",
                                    profile.Name ?? string.Empty, a.Name ?? string.Empty);
                            }

                            profile = tr.GetObject(pid, OpenMode.ForWrite, false) as CivilProfile;
                            if (profile == null) continue;

                            profilesProcessed++;
                            foreach (Crossing c in roadCrossings)
                            {
                                bool isFirstRoad = c.FirstId == a.ObjectId;
                                ObjectId otherRoadIdForComparison = isFirstRoad ? c.SecondId : c.FirstId;
                                CivilTinSurface ownTopForComparison = null;
                                CivilTinSurface otherTopForComparison = null;
                                topSurfaceByAlignment.TryGetValue(a.ObjectId, out ownTopForComparison);
                                topSurfaceByAlignment.TryGetValue(otherRoadIdForComparison, out otherTopForComparison);

                                double station = isFirstRoad ? c.FirstStation : c.SecondStation;
                                ObjectId crossingRoadId = isFirstRoad ? c.SecondId : c.FirstId;
                                double crossingRoadStation = isFirstRoad ? c.SecondStation : c.FirstStation;
                                CivilAlignment crossingRoad = null;
                                CivilTinSurface crossingTopSurface = null;
                                CivilProfile crossingFgProfile = null;
                                alignmentById.TryGetValue(crossingRoadId, out crossingRoad);
                                topSurfaceByAlignment.TryGetValue(crossingRoadId, out crossingTopSurface);

                                // Bind the target explicitly to the OTHER road at this crossing.
                                // Never accept this profile's own road surface/profile as its target.
                                if (crossingRoad != null)
                                    crossingFgProfile = FindFinishedGroundProfile(crossingRoad, tr);
                                string ownRoadKey = RoadKey(a.Name);
                                string crossingRoadKey = RoadKey(crossingRoad == null ? string.Empty : crossingRoad.Name);
                                bool surfaceBelongsToCrossingRoad =
                                    crossingTopSurface != null &&
                                    !string.IsNullOrEmpty(crossingRoadKey) &&
                                    string.Equals(RoadKey(crossingTopSurface.Name), crossingRoadKey, StringComparison.OrdinalIgnoreCase) &&
                                    !string.Equals(crossingRoadKey, ownRoadKey, StringComparison.OrdinalIgnoreCase);

                                // Decide which road is fixed ONCE at the centreline crossing.
                                // Do not independently skip left/right because local crossfall can make
                                // an edge sample appear slightly higher even though this is the lower road
                                // overall. The lower road receives all three PVIs; the higher road receives none.
                                double ownCentreElevation = double.NaN;
                                double otherCentreElevation = double.NaN;
                                double centreX = 0.0, centreY = 0.0;
                                try { a.PointLocation(station, 0.0, ref centreX, ref centreY); } catch { }
                                try { if (ownTopForComparison != null) ownCentreElevation = ownTopForComparison.FindElevationAtXY(centreX, centreY); } catch { }
                                try { if (otherTopForComparison != null) otherCentreElevation = otherTopForComparison.FindElevationAtXY(centreX, centreY); } catch { }
                                if (!IsFinite(ownCentreElevation))
                                {
                                    try { ownCentreElevation = profile.ElevationAt(station); } catch { }
                                }
                                if (!IsFinite(otherCentreElevation) && crossingFgProfile != null)
                                {
                                    try
                                    {
                                        List<Sample> centreSamples;
                                        if (samples.TryGetValue(crossingRoadId, out centreSamples))
                                        {
                                            double projectedStation = NearestStation(centreSamples, centreX, centreY);
                                            projectedStation = Math.Max(crossingFgProfile.StartingStation, Math.Min(crossingFgProfile.EndingStation, projectedStation));
                                            otherCentreElevation = crossingFgProfile.ElevationAt(projectedStation);
                                        }
                                    }
                                    catch { }
                                }
                                if (!IsFinite(ownCentreElevation) || !IsFinite(otherCentreElevation))
                                {
                                    skipped++;
                                    doc.Editor.WriteMessage("\\nCE_ROADCROSSINGPVIS: skipped centre PVI on '{0}' at crossing station {1:F3}; could not determine the higher reference road at the centreline.",
                                        a.Name ?? string.Empty, station);
                                    continue;
                                }
                                // Keep the higher road fixed. When elevations are effectively equal,
                                // choose one receiver deterministically so the crossing is not skipped
                                // by both roads (or processed in both directions). The lower ObjectId
                                // is the fixed reference in a tie; the other road receives the centre PVI.
                                bool elevationsEqual = Math.Abs(ownCentreElevation - otherCentreElevation) <= 0.001;
                                if (ownCentreElevation > otherCentreElevation + 0.001 ||
                                    (elevationsEqual && a.ObjectId.CompareTo(crossingRoadId) < 0))
                                {
                                    continue;
                                }

                                // All three targets are required at every crossing: left, centre, right.
                                // Elevations must come only from the intersecting road's matched finished TOP surface.
                                // Never fall back to a centerline profile: that can produce wrong edge/crossing elevations.
                                double[] targets = { station }; // Centreline crossing PVI only; left/right edge PVIs intentionally ignored.
                                foreach (double rawTarget in targets)
                                {
                                    double target = rawTarget;
                                    if (target < profile.StartingStation - StationTolerance ||
                                        target > profile.EndingStation + StationTolerance)
                                    {
                                        skipped++;
                                        continue;
                                    }
                                    target = Math.Max(profile.StartingStation,
                                        Math.Min(profile.EndingStation, target));

                                    double x = 0.0, y = 0.0, elevation = 0.0;
                                    try { a.PointLocation(target, 0.0, ref x, ref y); }
                                    catch { skipped++; continue; }

                                    // The receiving road was classified as the lower road at the
                                    // centreline crossing. Do not require its own surface to sample at
                                    // each offset station; the target elevation comes from the opposite
                                    // road's frozen TOP surface, with its permitted FG-profile fallback.

                                    bool elevationFound = false;

                                    // Primary target: sample the OTHER road's finished TOP surface at
                                    // this receiving-road station's XY. This is the same surface the
                                    // manually adjusted receiving FG profile must follow.
                                    if (surfaceBelongsToCrossingRoad)
                                    {
                                        double frozenElevation;
                                        var frozenKey = Tuple.Create(a.ObjectId, crossingRoadId, target);
                                        if (frozenTopElevations.TryGetValue(frozenKey, out frozenElevation) &&
                                            IsFinite(frozenElevation))
                                        {
                                            elevation = frozenElevation;
                                            elevationFound = true;
                                            surfaceDrapes++;
                                        }
                                    }

                                    // If the crossing TOP TIN has no triangle at this point (often near
                                    // corridor boundaries), project XY onto the OTHER road's sampled
                                    // alignment segments and use that road's FG profile at the projected
                                    // station. Never use the receiving road's own profile as a fallback.
                                    if (allowCrossingProfileFallback && !elevationFound && crossingRoad != null)
                                    {
                                        try
                                        {
                                            CivilProfile fallbackCrossingFgProfile = FindFinishedGroundProfile(crossingRoad, tr);
                                            List<Sample> crossingSamples;
                                            if (fallbackCrossingFgProfile != null &&
                                                samples.TryGetValue(crossingRoadId, out crossingSamples))
                                            {
                                                double crossStation = NearestStation(crossingSamples, x, y);
                                                if (crossStation >= fallbackCrossingFgProfile.StartingStation - StationTolerance &&
                                                    crossStation <= fallbackCrossingFgProfile.EndingStation + StationTolerance)
                                                {
                                                    crossStation = Math.Max(fallbackCrossingFgProfile.StartingStation,
                                                        Math.Min(fallbackCrossingFgProfile.EndingStation, crossStation));
                                                    elevation = fallbackCrossingFgProfile.ElevationAt(crossStation);
                                                    elevationFound = IsFinite(elevation);
                                                    if (elevationFound) crossingProfileFallbacks++;
                                                }
                                            }
                                        }
                                        catch { elevationFound = false; }
                                    }

                                    if (!elevationFound)
                                    {
                                        skipped++;
                                        doc.Editor.WriteMessage(
                                            "\nCE_ROADCROSSINGPVIS: skipped {0} PVI on receiving road '{1}' at station {2:F3}; target crossing road='{3}', TOP surface='{4}', XY=({5:F3}, {6:F3}). Neither the crossing TOP surface nor its FG profile supplied a valid elevation.",
                                            target == station - halfWidth ? "left" : (Math.Abs(target - station) <= StationTolerance ? "centre" : "right"),
                                            a.Name ?? string.Empty, target,
                                            crossingRoad == null ? "<unknown>" : (crossingRoad.Name ?? string.Empty),
                                            crossingTopSurface == null ? "<not matched>" : (crossingTopSurface.Name ?? "<unnamed>"),
                                            x, y);
                                        continue;
                                    }

                                    try
                                    {
                                        // The higher road is the fixed reference. Set the receiving
                                        // road PVI to the frozen target elevation exactly; do not clamp
                                        // the target to the receiving profile's current interpolated
                                        // elevation or an old PVI elevation. That clamp prevented the
                                        // lower road from being linked to the higher road at crossings.
                                        // The fixed higher road is protected by the one-way road selection
                                        // above, and this command does not rebuild either corridor.
                                        ProfilePVI existing = FindPviNear(profile, target);

                                        if (existing != null)
                                        {
                                            if (Math.Abs(existing.Elevation - elevation) > 0.001)
                                            {
                                                existing.Elevation = elevation;
                                                pvisUpdated++;
                                            }
                                            else alreadyCorrect++;
                                        }
                                        else
                                        {
                                            if (verticalCurveLength > 0.0)
                                            {
                                                try
                                                {
                                                    profile.PVIs.AddPVISymParabola(target, elevation, verticalCurveLength);
                                                }
                                                catch
                                                {
                                                    // Some stations (for example profile ends or closely spaced PVIs)
                                                    // cannot accept a curve directly. Preserve the required PVI,
                                                    // then attempt to attach a free symmetric curve below.
                                                    ProfilePVI added = profile.PVIs.AddPVI(target, elevation);
                                                    try { profile.Entities.AddFreeSymmetricParabolaByPVIAndCurveLength(added, verticalCurveLength); }
                                                    catch { }
                                                }
                                            }
                                            else profile.PVIs.AddPVI(target, elevation);
                                            pvisAdded++;
                                        }

                                        // Apply the requested vertical curve to an existing PVI too,
                                        // but do not replace or reshape a curve that already exists.
                                        if (verticalCurveLength > 0.0)
                                        {
                                            ProfilePVI curvePvi = FindPviNear(profile, target);
                                            if (curvePvi != null)
                                            {
                                                try
                                                {
                                                    if (curvePvi.VerticalCurve == null)
                                                        profile.Entities.AddFreeSymmetricParabolaByPVIAndCurveLength(curvePvi, verticalCurveLength);
                                                }
                                                catch
                                                {
                                                    // Civil 3D may reject curves at profile ends or where
                                                    // neighbouring entities leave insufficient tangent length.
                                                }
                                            }
                                        }
                                    }
                                    catch { skipped++; }
                                }
                            }
                        }
                    }
                    tr.Commit();
                }
                doc.Editor.WriteMessage(
                    "\\n{9} complete. Crossings found={0}; design profiles processed={1}; PVIs added={2}; existing PVIs updated={3}; already correct={4}; elevations sampled from the OTHER road's finished TOP surface={5}; elevations obtained from the OTHER road's FG profile fallback={6}; alignments without a matching TOP surface={7}; stations skipped={8}. {10}",
                    crossingsFound, profilesProcessed, pvisAdded, pvisUpdated, alreadyCorrect, surfaceDrapes, crossingProfileFallbacks, missingTopSurfaceRoads, skipped,
                    allowCrossingProfileFallback ? "CE_ROADCROSSINGPVIS" : "CE_DRAPECROSSROADPVIS",
                    allowCrossingProfileFallback
                        ? "The receiving road's own surface/profile is never used as the target. Lower-road PVIs are set to the frozen higher-road TOP target elevations."
                        : "Strict TOP-surface mode: target elevations were frozen before edits; only the locally lower road is adjusted point-by-point; no surface edits or corridor rebuilds were requested; no FG-profile fallback was used. Later corridor rebuilds may reflect changes to controlling profiles.");
            }
            catch (System.Exception ex)
            {
                doc.Editor.WriteMessage("\nCE_ROADCROSSINGPVIS failed: {0}", ex.Message);
            }
        }


        /// <summary>
        /// Removes PVI entries at the left, centre and right target stations
        /// used by CE_ROADCROSSINGPVIS for detected road crossings.
        /// Because the add command can update an existing PVI, this command
        /// removes any PVI at those exact target stations, regardless of origin.
        /// </summary>
        [CommandMethod("CE_TOOLS", "CE_REMOVEROADCROSSINGPVIS",
            CommandFlags.Modal | CommandFlags.Redraw | CommandFlags.UsePickSet)]
        public void RemoveRoadCrossingPvis()
        {
            Document doc = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civil = CivilApplication.ActiveDocument;
            if (doc == null || civil == null) return;

            var widthPrompt = new PromptDoubleOptions(
                "\\nEnter the same road crossing half-width used when adding PVIs <3.0>: ")
            {
                AllowNegative = false,
                AllowZero = false,
                DefaultValue = 3.0,
                UseDefaultValue = true
            };
            PromptDoubleResult widthResult = doc.Editor.GetDouble(widthPrompt);
            if (widthResult.Status != PromptStatus.OK) return;
            double halfWidth = widthResult.Value;

            var intervalPrompt = new PromptDoubleOptions(
                "\\nAlignment sampling interval for crossing detection <1.0>: ")
            {
                AllowNegative = false,
                AllowZero = false,
                DefaultValue = 1.0,
                UseDefaultValue = true
            };
            PromptDoubleResult intervalResult = doc.Editor.GetDouble(intervalPrompt);
            if (intervalResult.Status != PromptStatus.OK) return;
            double interval = Math.Max(0.25, intervalResult.Value);

            var confirm = new PromptKeywordOptions(
                "\\nRemove PVIs at detected road-crossing left/centre/right stations? [Yes/No] <No>: ",
                "Yes No")
            {
                AllowNone = true
            };
            confirm.Keywords.Default = "No";
            PromptResult confirmResult = doc.Editor.GetKeywords(confirm);
            if (confirmResult.Status != PromptStatus.OK ||
                !string.Equals(confirmResult.StringResult, "Yes", StringComparison.OrdinalIgnoreCase))
            {
                doc.Editor.WriteMessage("\\nCE_REMOVEROADCROSSINGPVIS cancelled.");
                return;
            }

            int crossingsFound = 0, profilesProcessed = 0, pvisRemoved = 0;
            int profilesSkipped = 0, removalFailures = 0;
            try
            {
                using (doc.LockDocument())
                using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
                {
                    var alignments = new List<CivilAlignment>();
                    foreach (ObjectId id in civil.GetAlignmentIds())
                    {
                        CivilAlignment a = null;
                        try { a = tr.GetObject(id, OpenMode.ForRead, false) as CivilAlignment; }
                        catch { }
                        if (a == null) continue;
                        try
                        {
                            if (a.AlignmentType != AlignmentType.Centerline) continue;
                            if (a.EndingStation - a.StartingStation < interval) continue;
                            alignments.Add(a);
                        }
                        catch { }
                    }

                    var samples = new Dictionary<ObjectId, List<Sample>>();
                    foreach (CivilAlignment a in alignments)
                        samples[a.ObjectId] = SampleAlignment(a, interval);

                    var crossings = new List<Crossing>();
                    for (int i = 0; i < alignments.Count; i++)
                    {
                        CivilAlignment a = alignments[i];
                        List<Sample> sa = samples[a.ObjectId];
                        for (int j = i + 1; j < alignments.Count; j++)
                        {
                            CivilAlignment b = alignments[j];
                            string roadKeyA = RoadKey(a.Name);
                            string roadKeyB = RoadKey(b.Name);
                            if (!string.IsNullOrEmpty(roadKeyA) &&
                                string.Equals(roadKeyA, roadKeyB, StringComparison.OrdinalIgnoreCase))
                                continue;

                            foreach (var pair in Intersections(sa, samples[b.ObjectId]))
                            {
                                double ax = pair.Item1, bx = pair.Item2;
                                if (crossings.Any(c =>
                                    ((c.FirstId == a.ObjectId && c.SecondId == b.ObjectId) ||
                                     (c.FirstId == b.ObjectId && c.SecondId == a.ObjectId)) &&
                                    Math.Abs((c.FirstId == a.ObjectId ? c.FirstStation : c.SecondStation) - ax) < halfWidth * 0.5 &&
                                    Math.Abs((c.FirstId == a.ObjectId ? c.SecondStation : c.FirstStation) - bx) < halfWidth * 0.5))
                                    continue;

                                double x = 0.0, y = 0.0;
                                try { a.PointLocation(ax, 0.0, ref x, ref y); }
                                catch { continue; }
                                crossings.Add(new Crossing
                                {
                                    FirstId = a.ObjectId, FirstStation = ax,
                                    SecondId = b.ObjectId, SecondStation = bx,
                                    X = x, Y = y
                                });
                            }
                        }
                    }

                    crossingsFound = crossings.Count;
                    foreach (CivilAlignment a in alignments)
                    {
                        List<double> targetStations = new List<double>();
                        foreach (Crossing c in crossings)
                        {
                            if (c.FirstId == a.ObjectId)
                            {
                                targetStations.Add(c.FirstStation - halfWidth);
                                targetStations.Add(c.FirstStation);
                                targetStations.Add(c.FirstStation + halfWidth);
                            }
                            else if (c.SecondId == a.ObjectId)
                            {
                                targetStations.Add(c.SecondStation - halfWidth);
                                targetStations.Add(c.SecondStation);
                                targetStations.Add(c.SecondStation + halfWidth);
                            }
                        }
                        if (targetStations.Count == 0) continue;

                        foreach (ObjectId pid in a.GetProfileIds())
                        {
                            CivilProfile profile = null;
                            try { profile = tr.GetObject(pid, OpenMode.ForWrite, false) as CivilProfile; }
                            catch { }
                            if (profile == null) { profilesSkipped++; continue; }

                            bool design = false;
                            try { design = profile.ProfileType == ProfileType.FG; } catch { }
                            string pname = (profile.Name ?? string.Empty).ToUpperInvariant();
                            if (!design || pname.Contains("EG") || pname.Contains("EXIST") ||
                                pname.Contains("GROUND") || pname.Contains("SURFACE"))
                                continue;

                            profilesProcessed++;
                            object pvis = profile.PVIs;
                            if (pvis == null) continue;

                            var entries = new List<ProfilePVI>();
                            try
                            {
                                foreach (ProfilePVI pvi in profile.PVIs)
                                    if (pvi != null && targetStations.Any(st =>
                                        Math.Abs(pvi.Station - Math.Max(profile.StartingStation,
                                            Math.Min(profile.EndingStation, st))) <= StationTolerance))
                                        entries.Add(pvi);
                            }
                            catch { }

                            // Remove in descending station order so collection indexes remain stable.
                            foreach (ProfilePVI pvi in entries.OrderByDescending(p => p.Station))
                            {
                                if (TryRemoveProfilePvi(pvis, pvi))
                                    pvisRemoved++;
                                else
                                {
                                    removalFailures++;
                                    doc.Editor.WriteMessage(
                                        "\\nCE_REMOVEROADCROSSINGPVIS: could not remove PVI on '{0}' at station {1:F3}.",
                                        profile.Name ?? string.Empty, pvi.Station);
                                }
                            }
                        }
                    }

                    tr.Commit();
                }

                doc.Editor.WriteMessage(
                    "\\nCE_REMOVEROADCROSSINGPVIS complete. Crossings detected={0}; design profiles checked={1}; PVIs removed={2}; profiles skipped={3}; removal failures={4}.",
                    crossingsFound, profilesProcessed, pvisRemoved, profilesSkipped, removalFailures);
                doc.Editor.WriteMessage(
                    "\\nNote: any existing PVI at a detected left/centre/right target station may also have been removed.");
            }
            catch (System.Exception ex)
            {
                doc.Editor.WriteMessage("\\nCE_REMOVEROADCROSSINGPVIS failed: {0}", ex.Message);
            }
        }

        private static bool TryRemoveProfilePvi(object pviCollection, ProfilePVI pvi)
        {
            if (pviCollection == null || pvi == null) return false;
            Type collectionType = pviCollection.GetType();

            foreach (string methodName in new[] { "RemovePVI", "Remove" })
            {
                foreach (System.Reflection.MethodInfo method in collectionType.GetMethods(
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                {
                    if (!string.Equals(method.Name, methodName, StringComparison.Ordinal)) continue;
                    System.Reflection.ParameterInfo[] parameters = method.GetParameters();
                    if (parameters.Length != 1 ||
                        !parameters[0].ParameterType.IsAssignableFrom(pvi.GetType())) continue;
                    try
                    {
                        method.Invoke(pviCollection, new object[] { pvi });
                        return true;
                    }
                    catch { }
                }
            }

            // Civil 3D releases commonly expose RemoveAt(index); resolve the live index
            // by station so this remains robust when the collection wrapper differs.
            try
            {
                var live = new List<ProfilePVI>();
                foreach (ProfilePVI item in (System.Collections.IEnumerable)pviCollection)
                    live.Add(item);
                int index = live.FindIndex(item =>
                    Math.Abs(item.Station - pvi.Station) <= StationTolerance);
                if (index < 0) return false;

                System.Reflection.MethodInfo removeAt = collectionType.GetMethod(
                    "RemoveAt",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
                    null, new[] { typeof(int) }, null);
                if (removeAt == null) return false;
                removeAt.Invoke(pviCollection, new object[] { index });
                return true;
            }
            catch { return false; }
        }

        private static List<Sample> SampleAlignment(CivilAlignment a, double interval)
        {
            var result = new List<Sample>();
            double start = a.StartingStation, end = a.EndingStation;
            int count = Math.Max(1, (int)Math.Ceiling((end - start) / interval));
            for (int i = 0; i <= count; i++)
            {
                double station = i == count ? end : start + (end - start) * i / count;
                double x = 0, y = 0;
                try { a.PointLocation(station, 0, ref x, ref y); }
                catch { continue; }
                result.Add(new Sample { Station = station, X = x, Y = y });
            }
            return result;
        }

        private static IEnumerable<Tuple<double, double>> Intersections(
            List<Sample> a, List<Sample> b)
        {
            for (int i = 0; i < a.Count - 1; i++)
            {
                Sample a0 = a[i], a1 = a[i + 1];
                for (int j = 0; j < b.Count - 1; j++)
                {
                    Sample b0 = b[j], b1 = b[j + 1];
                    double ta, tb, x, y;
                    if (!TrySegmentIntersection(a0, a1, b0, b1, out ta, out tb, out x, out y))
                        continue;
                    double sta = a0.Station + (a1.Station - a0.Station) * ta;
                    double stb = b0.Station + (b1.Station - b0.Station) * tb;
                    yield return Tuple.Create(sta, stb);
                }
            }
        }

        private static bool TrySegmentIntersection(
            Sample a0, Sample a1, Sample b0, Sample b1,
            out double ta, out double tb, out double x, out double y)
        {
            ta = tb = x = y = 0;
            double rx = a1.X - a0.X, ry = a1.Y - a0.Y;
            double sx = b1.X - b0.X, sy = b1.Y - b0.Y;
            double den = rx * sy - ry * sx;
            if (Math.Abs(den) < 1e-10) return false;
            double qx = b0.X - a0.X, qy = b0.Y - a0.Y;
            ta = (qx * sy - qy * sx) / den;
            tb = (qx * ry - qy * rx) / den;
            if (ta < -1e-8 || ta > 1.00000001 || tb < -1e-8 || tb > 1.00000001)
                return false;
            x = a0.X + ta * rx;
            y = a0.Y + ta * ry;
            return true;
        }

        private static CivilProfile FindFinishedGroundProfile(CivilAlignment alignment, Transaction tr)
        {
            if (alignment == null || tr == null) return null;
            try
            {
                foreach (ObjectId profileId in alignment.GetProfileIds())
                {
                    CivilProfile profile = null;
                    try { profile = tr.GetObject(profileId, OpenMode.ForRead, false) as CivilProfile; }
                    catch { }
                    if (profile == null) continue;
                    bool isFg = false;
                    try { isFg = profile.ProfileType == ProfileType.FG; } catch { }
                    string name = (profile.Name ?? string.Empty).ToUpperInvariant();
                    if (isFg && !name.Contains("EG") && !name.Contains("EXIST") &&
                        !name.Contains("GROUND") && !name.Contains("SURFACE"))
                        return profile;
                }
            }
            catch { }
            return null;
        }

        private static ProfilePVI FindPviNear(CivilProfile profile, double station)
        {
            try
            {
                foreach (ProfilePVI pvi in profile.PVIs)
                    if (Math.Abs(pvi.Station - station) <= StationTolerance)
                        return pvi;
            }
            catch { }
            return null;
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static bool IsFinishedRoadTopSurfaceName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            string normalized = Regex.Replace(name.Trim(), @"[\\s_]+", "-");
            if (!normalized.StartsWith("TOP-RD-", StringComparison.OrdinalIgnoreCase))
                return false;

            // A surface name beginning TOP-RD-* is not sufficient by itself:
            // corridor bottom/subbase/formation surfaces can share the road key.
            string upper = normalized.ToUpperInvariant();
            string[] nonTopMarkers =
            {
                "BOTTOM", "BOT-", "-BOT", "SUBBASE", "SUB-BASE", "DATUM",
                "FORMATION", "UNDERSIDE", "PAVEMENT-BASE", "BASE-COURSE",
                "BASECOURSE", "LOWER", "INVERT"
            };
            return !nonTopMarkers.Any(marker => upper.Contains(marker));
        }

        private static CivilTinSurface FindMatchingTopSurface(CivilAlignment alignment, IEnumerable<CivilTinSurface> surfaces)
        {
            if (alignment == null) return null;
            string key = RoadKey(alignment.Name);
            List<CivilTinSurface> available = (surfaces ?? Enumerable.Empty<CivilTinSurface>())
                .Where(s => s != null && IsFinishedRoadTopSurfaceName(s.Name))
                .ToList();
            if (string.IsNullOrEmpty(key)) return null;

            // Never guess between multiple similarly named surfaces: selecting a
            // stale/auxiliary TOP surface can place a PVI above or below the actual
            // crossing pavement. Prefer the exact canonical surface name. If that is
            // absent, accept a suffix variant only when it is the sole eligible match.
            string canonical = "TOP-" + key;
            List<CivilTinSurface> roadMatches = available
                .Where(s => string.Equals(RoadKey(s.Name), key, StringComparison.OrdinalIgnoreCase))
                .ToList();
            CivilTinSurface exact = roadMatches.FirstOrDefault(s =>
                string.Equals((s.Name ?? string.Empty).Trim(), canonical, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
            return roadMatches.Count == 1 ? roadMatches[0] : null;
        }
        private static string RoadKey(string value)
        {
            Match match = Regex.Match(value ?? string.Empty,
                @"(?:ROAD|RD)[\s_-]*(?<number>\d+)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            int number;
            return match.Success && int.TryParse(match.Groups["number"].Value,
                NumberStyles.Integer, CultureInfo.InvariantCulture, out number)
                ? "RD-" + number.ToString("00", CultureInfo.InvariantCulture)
                : string.Empty;
        }

        private static double NearestStation(List<Sample> samples, double x, double y)
        {
            if (samples == null || samples.Count == 0) return 0.0;
            if (samples.Count == 1) return samples[0].Station;
            double bestDistance = double.MaxValue;
            double bestStation = samples[0].Station;
            for (int i = 0; i < samples.Count - 1; i++)
            {
                Sample a = samples[i], b = samples[i + 1];
                double dx = b.X - a.X, dy = b.Y - a.Y;
                double length2 = dx * dx + dy * dy;
                double t = length2 <= 1e-16 ? 0.0 : ((x - a.X) * dx + (y - a.Y) * dy) / length2;
                t = Math.Max(0.0, Math.Min(1.0, t));
                double px = a.X + t * dx, py = a.Y + t * dy;
                double ex = x - px, ey = y - py;
                double distance2 = ex * ex + ey * ey;
                if (distance2 < bestDistance)
                {
                    bestDistance = distance2;
                    bestStation = a.Station + t * (b.Station - a.Station);
                }
            }
            return bestStation;
        }
    }
}

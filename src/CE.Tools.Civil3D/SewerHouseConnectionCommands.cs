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
        public void Create()
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
                "CE Tools - Sewer House Connection Symbols",
                "Create plan house connections for closed erf polylines. Angle is measured downstream toward increasing sewer-alignment chainage; actual Civil 3D network pipes are not created.");
            settings.AddChoice("Surface", "01 Ground levels", "Existing ground (NG/EG) surface",
                surfaces.FirstOrDefault(s => s.IndexOf("NG", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    s.IndexOf("EG", StringComparison.OrdinalIgnoreCase) >= 0) ?? surfaces[0],
                "Ground levels determine which road-facing corner is the low side.", surfaces);
            settings.AddPositiveDouble("Along", "02 Set-out", "Offset along side boundary (m)", 1.0,
                "Distance from the lower side erf corner along the sewer-facing frontage.");
            settings.AddPositiveDouble("In", "02 Set-out", "Distance inside erf (m)", 1.0,
                "Symbol starts inside the property from its road-facing boundary.");
            settings.AddPositiveDouble("Length", "02 Set-out", "Connection arm length (m)", 3.0,
                "Schematic arm length from the inside point toward the sewer main.");
            settings.AddDouble("Angle", "02 Set-out", "Connection angle from direct approach (degrees)", 0.0,
                "0 degrees points directly across to the main. Positive angles lean downstream, in the direction of increasing alignment stations, on either side of the alignment.");
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
            settings.AddPositiveDouble("ShortBranchSetback", "02 Set-out", "Shared T short distance inside erf boundary (m)", 1.0,
                "Position the short connection's starting point this distance inside the selected erf frontage.");
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
                ObjectId layerId = EnsureLayer(tr, doc.Database);
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
                    Point2d high = zLeft < zRight ? right : left;
                    Vector2d tangent = (high - low).GetNormal();
                    Point2d onFrontage = low + tangent * along;
                    Vector2d inward = centroid - onFrontage;
                    if (inward.Length < 0.01) { skipped++; continue; }
                    // Normal to frontage that points inside the selected erf.
                    Vector2d normal = new Vector2d(-tangent.Y, tangent.X);
                    if (normal.DotProduct(inward) < 0) normal = -normal;
                    Point2d start = onFrontage + normal * inside;
                    // Abort narrow/irregular lots when the inside position falls outside the erf.
                    if (!InsidePolygon(erf, start)) { skipped++; continue; }
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
                    if (!TryIntersectMain(alignment, start, direction,
                            maxDistance + length + inside, out end))
                    { skipped++; continue; }
                    double sourceStation = 0.0, sourceOffset = 0.0;
                    try { alignment.StationOffset(start.X, start.Y, ref sourceStation, ref sourceOffset); }
                    catch { skipped++; continue; }
                    Point2d shortStart = onFrontage + normal *
                        settings.Double("ShortBranchSetback", 1.0);
                    if (!InsidePolygon(erf, shortStart))
                    { skipped++; continue; }
                    proposed.Add(new ConnectionProposal
                    {
                        Erf = erf.ObjectId, Key = erf.Handle.ToString(),
                        Alignment = alignment.ObjectId, AlignmentHandle = alignment.Handle.ToString(),
                        Start = start, ShortStart = shortStart, End = end,
                        Outward = -normal, Station = sourceStation, Offset = sourceOffset
                    });
                }

                // Pair only after ALL erven have been examined. The former
                // one-pass algorithm depended on entity creation/selection order,
                // leaving pairable erven with individual long road crossings.
                bool shareMode = settings.Text("CrossingMode") ==
                    "Share one crossing for two erven";
                double pairRange = settings.Double("PairDistance", 20.0);
                var remaining = new List<ConnectionProposal>(proposed
                    .OrderBy(item => item.AlignmentHandle, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.Offset < 0 ? -1 : 1)
                    .ThenBy(item => item.Station)
                    .ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase));
                var drawn = new List<ConnectionProposal>();
                while (remaining.Count > 0)
                {
                    ConnectionProposal longArm = remaining[0];
                    remaining.RemoveAt(0);
                    ConnectionProposal shortArm = null;
                    Point2d tee = Point2d.Origin;
                    if (shareMode)
                    {
                        double best = double.MaxValue;
                        foreach (ConnectionProposal other in remaining)
                        {
                            if (other.Alignment != longArm.Alignment ||
                                Math.Sign(other.Offset) != Math.Sign(longArm.Offset)) continue;
                            double separation = longArm.Start.GetDistanceTo(other.Start);
                            if (separation > pairRange) continue;
                            Point2d candidate;
                            if (!TryIntersectStemWithCrossing(other.ShortStart,
                                other.Outward, longArm.Start, longArm.End, out candidate))
                                continue;
                            if (separation >= best) continue;
                            best = separation;
                            shortArm = other;
                            tee = candidate;
                        }
                    }
                    // Do not leave a long crossing unshared in the shared mode.
                    // Keep any previously generated output intact when no valid
                    // geometry exists; report the erf so the user can review it.
                    if (shareMode && shortArm == null)
                    {
                        if (settings.Text("UnpairedMode") == "Create normal short connections")
                        {
                            // Never hide a qualifying erf just because the neighbour's
                            // diagonal main crossing does not intersect its frontage
                            // normal. Draw a separate normal short connection instead.
                            Point2d boundaryPoint = longArm.ShortStart -
                                longArm.Outward * settings.Double("ShortBranchSetback", 1.0);
                            Point2d normalStart = boundaryPoint -
                                longArm.Outward * settings.Double("NormalShortSetback", 1.0);
                            Point2d normalEnd = normalStart +
                                longArm.Outward.GetNormal() *
                                settings.Double("NormalShortLength", 3.0);
                            if (!ReplaceManagedForGroup(model, tr,
                                new[] { longArm }, settings.Text("Existing"), ref replaced))
                            { skipped++; continue; }
                            AddConnectionLine(model, tr, layerId, longArm,
                                normalStart, normalEnd);
                            created++;
                            continue;
                        }
                        skipped++;
                        continue;
                    }
                    if (shortArm != null) remaining.Remove(shortArm);
                    var batch = new List<ConnectionProposal> { longArm };
                    if (shortArm != null) batch.Add(shortArm);
                    if (!ReplaceManagedForGroup(model, tr, batch,
                        settings.Text("Existing"), ref replaced))
                    { skipped += batch.Count; continue; }
                    AddConnectionLine(model, tr, layerId, longArm,
                        longArm.Start, longArm.End);
                    Vector2d axis = longArm.End - longArm.Start;
                    if (axis.Length > 1e-8)
                    {
                        Vector2d across = new Vector2d(-axis.Y, axis.X).GetNormal();
                        AddConnectionLine(model, tr, layerId, longArm,
                            longArm.End + across * 0.45,
                            longArm.End - across * 0.45);
                    }
                    created++;
                    if (shortArm != null)
                    {
                        AddConnectionLine(model, tr, layerId, shortArm,
                            shortArm.ShortStart, tee);
                        created++;
                        sharedBranches++;
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
            internal Point2d Start, ShortStart, End;
            internal Vector2d Outward;
            internal double Station, Offset;
        }

        private static bool ReplaceManagedForGroup(BlockTableRecord model,
            Transaction transaction, IEnumerable<ConnectionProposal> proposals,
            string existingMode, ref int replaced)
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
                    if (!data.AsArray().Any(v => v.TypeCode == 1000 &&
                        keys.Contains(Convert.ToString(v.Value) ?? ""))) continue;
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
            Point2d from, Point2d to)
        {
            Line line = new Line(new Point3d(from.X, from.Y, 0),
                new Point3d(to.X, to.Y, 0)) { LayerId = layerId };
            model.AppendEntity(line);
            transaction.AddNewlyCreatedDBObject(line, true);
            line.XData = new ResultBuffer(new TypedValue(1001, App),
                new TypedValue(1000, owner.Key),
                new TypedValue(1000, owner.AlignmentHandle));
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
        private static ObjectId EnsureLayer(Transaction tr, Database db)
        {
            LayerTable layers = tr.GetObject(db.LayerTableId, OpenMode.ForRead) as LayerTable;
            if (layers.Has(OutputLayer)) return layers[OutputLayer];
            layers.UpgradeOpen();
            LayerTableRecord layer = new LayerTableRecord { Name = OutputLayer, Color = Color.FromColorIndex(ColorMethod.ByAci, 3) };
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

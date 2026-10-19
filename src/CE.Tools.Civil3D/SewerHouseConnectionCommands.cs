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
                "Draw 2D green sewer house connection marks on the downhill ROAD-FACING boundary of each selected erf. Review unsuitable parcels; no sewer pipes are created.");
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
            settings.AddPositiveDouble("MaxDistance", "02 Set-out", "Maximum frontage to sewer (m)", 25.0,
                "Frontage farther than this from the selected sewer alignment is skipped.");
            settings.AddChoice("Existing", "03 Output", "Previous symbols",
                "Replace existing CE symbols for selected erven", "Retain unrelated and hand-drawn geometry.",
                new[] { "Replace existing CE symbols for selected erven", "Keep existing and skip duplicates" });
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;
            double along = settings.Double("Along", 1), inside = settings.Double("In", 1);
            double length = settings.Double("Length", 3), maxDistance = settings.Double("MaxDistance", 25);
            PromptEntityOptions aPrompt = new PromptEntityOptions("\nSelect sewer branch alignment: ");
            aPrompt.AddAllowedClass(typeof(CivilAlignment), true);
            PromptEntityResult aResult = doc.Editor.GetEntity(aPrompt);
            if (aResult.Status != PromptStatus.OK) return;
            PromptSelectionOptions plotsPrompt = new PromptSelectionOptions {
                MessageForAdding = "\nSelect multiple CLOSED erf boundary polylines: ",
                AllowDuplicates = false
            };
            PromptSelectionResult plots = doc.Editor.GetSelection(plotsPrompt);
            if (plots.Status != PromptStatus.OK || plots.Value == null) return;
            int created = 0, skipped = 0, replaced = 0;
            using (doc.LockDocument())
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                CivilAlignment alignment = tr.GetObject(aResult.ObjectId, OpenMode.ForRead, false) as CivilAlignment;
                CivilSurface surface = civil.GetSurfaceIds().Select(id => tr.GetObject(id, OpenMode.ForRead, false) as CivilSurface)
                    .FirstOrDefault(s => s != null && string.Equals(s.Name, settings.Text("Surface"), StringComparison.OrdinalIgnoreCase));
                if (alignment == null || surface == null) return;
                ObjectId layerId = EnsureLayer(tr, doc.Database);
                EnsureApp(tr, doc.Database);
                BlockTableRecord model = tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(doc.Database), OpenMode.ForWrite) as BlockTableRecord;
                if (model == null) return;
                foreach (ObjectId id in plots.Value.GetObjectIds().Distinct())
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
                    double bestDistance = double.MaxValue;
                    for (int i = 0; i < erf.NumberOfVertices; i++)
                    {
                        int next = (i + 1) % erf.NumberOfVertices;
                        if (Math.Abs(erf.GetBulgeAt(i)) > 1e-8) continue; // no chord approximation
                        Point2d p = erf.GetPoint2dAt(i), q = erf.GetPoint2dAt(next);
                        if (p.GetDistanceTo(q) <= 2 * along + 0.01) continue;
                        Point2d mid = new Point2d((p.X + q.X) / 2, (p.Y + q.Y) / 2);
                        double station, offset;
                        try { alignment.StationOffset(mid.X, mid.Y, out station, out offset); }
                        catch { continue; }
                        double distance = Math.Abs(offset);
                        if (distance < bestDistance) { bestDistance = distance; side = i; }
                    }
                    if (side < 0 || bestDistance > maxDistance) { skipped++; continue; }
                    Point2d left = erf.GetPoint2dAt(side);
                    Point2d right = erf.GetPoint2dAt((side + 1) % erf.NumberOfVertices);
                    double zLeft, zRight;
                    try { zLeft = surface.FindElevationAtXY(left.X, left.Y); zRight = surface.FindElevationAtXY(right.X, right.Y); }
                    catch { skipped++; continue; }
                    if (double.IsNaN(zLeft) || double.IsNaN(zRight) || Math.Abs(zLeft - zRight) < 0.005) { skipped++; continue; }
                    Point2d low = zLeft < zRight ? left : right;
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
                    string key = erf.Handle.ToString();
                    var matches = new List<Entity>();
                    foreach (ObjectId objectId in model)
                    {
                        Entity existing = tr.GetObject(objectId, OpenMode.ForRead, false) as Entity;
                        if (existing == null) continue;
                        ResultBuffer data = existing.GetXDataForApplication(App);
                        if (data == null) continue;
                        using (data)
                        {
                            TypedValue[] arr = data.AsArray();
                            if (arr.Any(v => v.TypeCode == 1000 && string.Equals(Convert.ToString(v.Value), key, StringComparison.OrdinalIgnoreCase)))
                                matches.Add(existing);
                        }
                    }
                    if (matches.Count > 0)
                    {
                        if (settings.Text("Existing").StartsWith("Keep", StringComparison.OrdinalIgnoreCase)) { skipped++; continue; }
                        foreach (Entity previous in matches) { previous.UpgradeOpen(); previous.Erase(); replaced++; }
                    }
                    // A schematic angle toward the road with a short end tick.
                    Point2d end = start - normal * length + tangent * Math.Min(length * 0.35, 1.0);
                    Point2d tickA = end + tangent * 0.45, tickB = end - tangent * 0.45;
                    foreach (var pair in new[] { Tuple.Create(start, end), Tuple.Create(tickA, tickB) })
                    {
                        Line line = new Line(new Point3d(pair.Item1.X, pair.Item1.Y, 0),
                            new Point3d(pair.Item2.X, pair.Item2.Y, 0)) { LayerId = layerId };
                        model.AppendEntity(line);
                        tr.AddNewlyCreatedDBObject(line, true);
                        line.XData = new ResultBuffer(
                            new TypedValue(1001, App), new TypedValue(1000, key),
                            new TypedValue(1000, alignment.Handle.ToString()));
                    }
                    created++;
                }
                tr.Commit();
            }
            doc.Editor.Regen();
            doc.Editor.WriteMessage("\nCE_SEWERHOUSECONNECTIONS: erven placed={0}; skipped for review={1}; old symbol lines replaced={2}. Check symbols on the ground plan.", created, skipped, replaced);
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

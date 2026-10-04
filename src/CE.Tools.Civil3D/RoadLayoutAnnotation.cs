using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace CETools.Civil3D
{
    public sealed partial class RoadLayoutProductionCommands
    {
        private const string SectionPlacement = "Midpoints between cross and T-junctions";

        private void PlaceRoadNames()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            var model = new ProductionSettingsDialogModel("CE Tools - Road Names",
                "Name all or selected road centrelines, with readable labels at the road midpoint or in every section between junctions.");
            model.AddChoice("Scope", "01 Selection", "Roads", "All", "Roads to name.", new[] { "All", "Selected" });
            model.AddText("Prefix", "02 Naming", "Road name prefix", "ROAD", "For example ROAD-1, ROAD-2.");
            model.AddPositiveInteger("Start", "02 Naming", "Starting number", 1, "First road number.");
            model.AddText("Layer", "03 Annotation", "Road-name layer", LabelLayer, "Existing or new layer for road names.");
            model.AddChoice("Position", "03 Annotation", "Name position", "Above", "Relative to the readable road direction in plan.", new[] { "Above", "Below", "Centered" });
            model.AddChoice("Placement", "03 Annotation", "Name locations", SectionPlacement,
                "Detect all crossing roads, including unselected roads. Include road-end sections.", new[] { "Road midpoint", SectionPlacement });
            model.AddDouble("Offset", "03 Annotation", "Label offset", 2.0, "Perpendicular drawing distance; centered names use zero offset.");
            model.AddPositiveDouble("TextHeight", "03 Annotation", "Paper text height", 2.5, "Annotative paper height.");
            model.AddChoice("Avoid", "03 Annotation", "Avoid overlapping annotations", "Yes", "Move labels outward; centered labels stay centered and are skipped if crowded.", new[] { "Yes", "No" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;
            List<ObjectId> ids = ResolveRoadScope(document, "CENTER", model.Text("Scope"), "\nSelect road centrelines to name: ");
            if (ids.Count == 0) return;
            string prefix = string.IsNullOrWhiteSpace(model.Text("Prefix")) ? "ROAD" : model.Text("Prefix").Trim();
            int created = 0, skipped = 0;
            using (Transaction tr = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = GetModelSpace(document.Database, tr, OpenMode.ForWrite);
                List<Curve> network = AnnotationRoads(space, tr);
                List<Polyline> roads = ids.Select(id => tr.GetObject(id, OpenMode.ForRead) as Polyline).Where(r => r != null)
                    .OrderBy(RoadOrderGroup).ThenBy(RoadOrderPrimary).ThenBy(r => MidPoint(r).Y).ThenBy(r => MidPoint(r).X).ToList();
                foreach (Polyline road in roads) EraseChildren(space, tr, "ROAD_NAME", road.Handle.ToString());
                var occupancy = new RoadAnnotationPlacement.Occupancy(space, tr);
                int index = model.Integer("Start", 1);
                foreach (Polyline road in roads)
                {
                    string name = prefix + "-" + (index++).ToString(CultureInfo.InvariantCulture);
                    RoadLink parent;
                    if (!TryReadLink(road, tr, out parent)) parent = new RoadLink { Kind = "CENTER" };
                    road.UpgradeOpen();
                    parent.Name = name;
                    WriteLink(road, tr, parent); // Store identity on the road, not on an arbitrary label.
                    var recipe = new AnnotationRecipe { Name = name, Layer = SafeLayer(model.Text("Layer"), LabelLayer),
                        Position = model.Text("Position"), Sections = model.Text("Placement") == SectionPlacement,
                        Offset = Math.Abs(model.Double("Offset", 2)), PaperHeight = model.Double("TextHeight", 2.5), Avoid = model.Text("Avoid") != "No" };
                    created += AddRoadNames(document.Database, tr, space, road, network, recipe, occupancy, ref skipped);
                }
                tr.Commit();
            }
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_ROADNAMES complete. Labels={0}; crowded locations skipped={1}.", created, skipped);
        }

        private void PlaceRoadDimensions()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            var model = new ProductionSettingsDialogModel("CE Tools - Road Width Dimensions",
                "Measure linked road edges at road or junction-section midpoints. Lane and full-width dimensions use separate dimension lines.");
            model.AddChoice("Scope", "01 Selection", "Roads", "All", "Roads to dimension.", new[] { "All", "Selected" });
            model.AddText("Layer", "02 Dimensions", "Dimension layer", DimensionLayer, "Existing or new dimension layer.");
            model.AddChoice("Mode", "02 Dimensions", "Dimension type", "Lane and full road widths", "Widths to measure.", new[] { "Lane widths", "Full road width", "Lane and full road widths" });
            model.AddChoice("Placement", "02 Dimensions", "Dimension locations", SectionPlacement,
                "Detect all crossing roads, including unselected roads. Include road-end sections.", new[] { "Road midpoint", SectionPlacement });
            model.AddPositiveDouble("Offset", "02 Dimensions", "Dimension-line offset", 1, "Distance along the road from the measured cross-section to the dimension line.");
            model.AddChoice("Avoid", "02 Dimensions", "Avoid overlapping annotations", "Yes", "Move dimension lines along the road, keeping measurement anchors at the section midpoint.", new[] { "Yes", "No" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;
            List<ObjectId> ids = ResolveRoadScope(document, "CENTER", model.Text("Scope"), "\nSelect road centrelines to dimension: ");
            if (ids.Count == 0) return;
            int count = 0, skipped = 0;
            using (Transaction tr = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = GetModelSpace(document.Database, tr, OpenMode.ForWrite);
                List<Curve> network = AnnotationRoads(space, tr);
                foreach (ObjectId id in ids) EraseChildren(space, tr, "ROAD_DIM", id.Handle.ToString());
                var occupancy = new RoadAnnotationPlacement.Occupancy(space, tr);
                foreach (ObjectId id in ids)
                {
                    Curve road = tr.GetObject(id, OpenMode.ForRead) as Curve;
                    if (road == null) continue;
                    var recipe = new AnnotationRecipe { Layer = SafeLayer(model.Text("Layer"), DimensionLayer), Mode = model.Text("Mode"),
                        Sections = model.Text("Placement") == SectionPlacement, Offset = model.Double("Offset", 1), Avoid = model.Text("Avoid") != "No" };
                    count += AddRoadDimensions(document.Database, tr, space, road, network, recipe, occupancy, ref skipped);
                }
                tr.Commit();
            }
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_ROADDIMENSIONS complete. Dimensions={0}; crowded/missing-edge locations skipped={1}.", count, skipped);
        }

        private static List<Curve> AnnotationRoads(BlockTableRecord space, Transaction tr)
        {
            return space.Cast<ObjectId>().Where(id => !id.IsErased).Select(id => tr.GetObject(id, OpenMode.ForRead, false) as Curve)
                .Where(c => c != null && !c.IsErased && (HasRoadKind(c, tr) || string.Equals(c.Layer, CenterLayer, StringComparison.OrdinalIgnoreCase))).ToList();
        }

        private static bool HasRoadKind(Entity entity, Transaction tr)
        {
            RoadLink link;
            return TryReadLink(entity, tr, out link) && link.Kind == "CENTER";
        }

        private static int AddRoadNames(Database db, Transaction tr, BlockTableRecord space, Curve road,
            IList<Curve> network, AnnotationRecipe recipe, RoadAnnotationPlacement.Occupancy occupancy, ref int skipped)
        {
            ObjectId layer = GetOrCreateLayer(db, tr, recipe.Layer);
            int count = 0;
            foreach (double station in RoadAnnotationPlacement.Stations(road, network, recipe.Sections))
            {
                Point3d anchor = road.GetPointAtDist(station);
                Vector3d tangent = RoadAnnotationPlacement.Tangent(road, station);
                Vector3d normal = RoadAnnotationPlacement.Above(tangent);
                bool centered = recipe.Position == "Centered";
                if (recipe.Position == "Below") normal = -normal;
                var text = new MText();
                text.SetDatabaseDefaults(db);
                text.LayerId = layer;
                text.Attachment = AttachmentPoint.MiddleCenter;
                text.TextHeight = PaperAnnotationScale.ModelTextHeight(db, recipe.PaperHeight);
                text.Rotation = Math.Atan2(tangent.Y, tangent.X);
                text.Contents = recipe.Name;
                if (!occupancy.PlaceText(text, anchor, centered ? new Vector3d(0, 0, 0) : normal, centered ? 0 : recipe.Offset, recipe.Avoid))
                { text.Dispose(); skipped++; continue; }
                PaperAnnotationScale.SetAnnotative(text);
                space.AppendEntity(text);
                tr.AddNewlyCreatedDBObject(text, true);
                WriteAnnotationLink(text, tr, road, "ROAD_NAME", recipe);
                count++;
            }
            return count;
        }

        private static int AddRoadDimensions(Database db, Transaction tr, BlockTableRecord space, Curve road,
            IList<Curve> network, AnnotationRecipe recipe, RoadAnnotationPlacement.Occupancy occupancy, ref int skipped)
        {
            List<Curve> edges = ReadChildren(space, tr, "EDGE", road.Handle.ToString()).ToList();
            ObjectId layer = GetOrCreateLayer(db, tr, recipe.Layer);
            int count = 0;
            double height = PaperAnnotationScale.ModelTextHeight(db, 2.5);
            using (DimStyleTableRecord style = db.GetDimstyleData())
                height = Math.Max(height, Math.Max(style.Dimtxt, style.Dimasz) * Math.Max(1.0, style.Dimscale));
            foreach (double station in RoadAnnotationPlacement.Stations(road, network, recipe.Sections))
            {
                Point3d centre = road.GetPointAtDist(station);
                Vector3d tangent = RoadAnnotationPlacement.Tangent(road, station);
                Vector3d normal = RoadAnnotationPlacement.Above(tangent);
                var hits = new List<Point3d>();
                // Intersect the actual edge pieces, rather than projecting to an edge endpoint
                // after junction trimming (which produces diagonal, incorrect widths).
                using (var section = new Xline { BasePoint = centre, UnitDir = normal })
                    foreach (Curve edge in edges)
                    {
                        var points = new Point3dCollection();
                        try { section.IntersectWith(edge, Intersect.OnBothOperands, points, IntPtr.Zero, IntPtr.Zero); }
                        catch { continue; }
                        foreach (Point3d point in points) hits.Add(point);
                    }
                var left = hits.Where(p => (p - centre).DotProduct(normal) > Tol).OrderBy(p => p.DistanceTo(centre)).ToList();
                var right = hits.Where(p => (p - centre).DotProduct(normal) < -Tol).OrderBy(p => p.DistanceTo(centre)).ToList();
                if (left.Count == 0 || right.Count == 0) { skipped++; continue; }
                var pairs = new List<Tuple<Point3d, Point3d, int>>();
                if (recipe.Mode != "Full road width")
                {
                    pairs.Add(Tuple.Create(left[0], centre, 1));
                    pairs.Add(Tuple.Create(centre, right[0], -1));
                }
                if (recipe.Mode != "Lane widths") pairs.Add(Tuple.Create(left[0], right[0], 2));
                foreach (var pair in pairs)
                {
                    bool placed = false;
                    for (int attempt = 0; attempt < (recipe.Avoid ? 40 : 1); attempt++)
                    {
                        double shift = Math.Sign(pair.Item3) * (recipe.Offset + (Math.Abs(pair.Item3) - 1) * height * 4 + attempt * height * 2);
                        Point3d midpoint = Mid(pair.Item1, pair.Item2) + tangent * shift;
                        // Reserve the dimension line, arrows and text; anchors stay fixed.
                        Extents3d bounds = RoadAnnotationPlacement.Occupancy.TextBox(midpoint,
                            pair.Item1.DistanceTo(pair.Item2) + height * 4, height * 2.5, Math.Atan2(normal.Y, normal.X));
                        if (recipe.Avoid && !occupancy.Free(bounds, height * 0.5)) continue;
                        var dim = new AlignedDimension(pair.Item1, pair.Item2, midpoint, string.Empty, db.Dimstyle);
                        dim.SetDatabaseDefaults(db);
                        dim.LayerId = layer;
                        space.AppendEntity(dim);
                        tr.AddNewlyCreatedDBObject(dim, true);
                        WriteAnnotationLink(dim, tr, road, "ROAD_DIM", recipe);
                        occupancy.Reserve(bounds);
                        count++; placed = true; break;
                    }
                    if (!placed) skipped++;
                }
            }
            return count;
        }

        private static void WriteAnnotationLink(Entity entity, Transaction tr, Curve road, string kind, AnnotationRecipe recipe)
        {
            WriteLink(entity, tr, new RoadLink { Kind = kind, ParentHandle = road.Handle.ToString(), SourceHandles = road.Handle.ToString(),
                Offset = recipe.Offset, Name = recipe.Name, Group = recipe.Encode(), Width = recipe.PaperHeight });
        }

        private static int RefreshAnnotations(Document document)
        {
            if (document == null) return 0;
            int count = 0, skipped = 0;
            using (Transaction tr = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = GetModelSpace(document.Database, tr, OpenMode.ForWrite);
                var jobs = new Dictionary<string, Tuple<Curve, string, AnnotationRecipe>>();
                foreach (ObjectId id in space.Cast<ObjectId>().ToList())
                {
                    if (id.IsErased) continue;
                    Entity entity = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                    RoadLink link;
                    if (entity == null || entity.IsErased || !TryReadLink(entity, tr, out link) ||
                        (link.Kind != "ROAD_NAME" && link.Kind != "ROAD_DIM")) continue;
                    ObjectId parent = ResolveHandle(document.Database, link.ParentHandle);
                    if (parent.IsNull || parent.IsErased) continue;
                    Curve road = tr.GetObject(parent, OpenMode.ForRead, false) as Curve;
                    if (road == null) continue;
                    // Legacy dimensions contain no placement/mode settings; leave them intact.
                    if (link.Kind == "ROAD_DIM" && !(link.Group ?? "").StartsWith("ANNOT2|", StringComparison.Ordinal)) continue;
                    jobs[link.Kind + ":" + link.ParentHandle] = Tuple.Create(road, link.Kind, AnnotationRecipe.Decode(link, entity));
                }
                foreach (var job in jobs.Values) EraseChildren(space, tr, job.Item2, job.Item1.Handle.ToString());
                var occupancy = new RoadAnnotationPlacement.Occupancy(space, tr);
                List<Curve> network = AnnotationRoads(space, tr);
                foreach (var job in jobs.Values.OrderBy(j => j.Item2 == "ROAD_NAME" ? 0 : 1))
                    count += job.Item2 == "ROAD_NAME"
                        ? AddRoadNames(document.Database, tr, space, job.Item1, network, job.Item3, occupancy, ref skipped)
                        : AddRoadDimensions(document.Database, tr, space, job.Item1, network, job.Item3, occupancy, ref skipped);
                tr.Commit();
            }
            if (skipped > 0) document.Editor.WriteMessage("\nRoad annotation refresh: crowded/missing-edge locations skipped={0}.", skipped);
            return count;
        }

        private sealed class AnnotationRecipe
        {
            internal string Name = "", Layer, Position = "Above", Mode = "Lane and full road widths";
            internal double Offset, PaperHeight = 2.5;
            internal bool Sections, Avoid = true;
            internal string Encode() { return "ANNOT2|" + Position + "|" + Mode + "|" + Sections + "|" + Avoid; }
            internal static AnnotationRecipe Decode(RoadLink link, Entity entity)
            {
                var recipe = new AnnotationRecipe { Name = link.Name, Layer = entity.Layer, Offset = Math.Abs(link.Offset),
                    Position = link.Offset < 0 ? "Below" : "Above", PaperHeight = link.Width > 0 ? link.Width : 2.5 };
                string[] fields = (link.Group ?? "").Split('|');
                if (fields.Length == 5 && fields[0] == "ANNOT2")
                { recipe.Position = fields[1]; recipe.Mode = fields[2]; recipe.Sections = fields[3] == "True"; recipe.Avoid = fields[4] == "True"; }
                else if (entity is MText)
                    recipe.PaperHeight = ((MText)entity).TextHeight / Math.Max(1e-8, PaperAnnotationScale.ModelTextHeight(entity.Database, 1.0));
                return recipe;
            }
        }
    }
}

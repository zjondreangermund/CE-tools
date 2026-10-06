using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivilCorridor = Autodesk.Civil.DatabaseServices.Corridor;

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
            model.AddChoice("Existing", "04 Existing labels", "Existing road names", "Replace existing road names",
                "Replace only CE-generated road-name labels for the selected/all road sources, or keep existing labels and create only missing names.",
                new[] { "Replace existing road names", "Keep existing road names" });
            model.AddChoice("Mask", "03 Annotation", "Background mask", "Yes",
                "Use a drawing-background mask on every road name. Border offset factor is fixed at 1.1 for consistent presentation.",
                new[] { "Yes", "No" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;
            string prefix = string.IsNullOrWhiteSpace(model.Text("Prefix")) ? "ROAD" : model.Text("Prefix").Trim();
            bool replaceNames = string.Equals(
                model.Text("Existing"),
                "Replace existing road names",
                StringComparison.OrdinalIgnoreCase);
            bool backgroundMask = !string.Equals(
                model.Text("Mask"),
                "No",
                StringComparison.OrdinalIgnoreCase);
            int created = 0, skipped = 0, existingKept = 0;
            using (Transaction tr = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = GetModelSpace(document.Database, tr, OpenMode.ForWrite);
                List<RoadAnnotationSource> roads = ResolveRoadAnnotationSources(
                    document,
                    tr,
                    space,
                    model.Text("Scope"),
                    "\nSelect Civil 3D road alignments or CE road centrelines to name: ");
                if (roads.Count == 0) return;

                List<Curve> network = roads.Select(item => item.Geometry).ToList();
                // Include other road geometry too, so section midpoints see every
                // T/cross junction even when only a subset is being named.
                network.AddRange(AnnotationRoads(space, tr)
                    .Where(curve => !network.Contains(curve)));

                roads = roads
                    .OrderBy(item => RoadOrderGroup(item.Geometry))
                    .ThenBy(item => RoadOrderPrimary(item.Geometry))
                    .ThenBy(item => MidPoint(item.Geometry).Y)
                    .ThenBy(item => MidPoint(item.Geometry).X)
                    .ToList();

                var occupancy = new RoadAnnotationPlacement.Occupancy(space, tr);
                int index = model.Integer("Start", 1);
                foreach (RoadAnnotationSource road in roads)
                {
                    string name = prefix + "-" + (index++).ToString(CultureInfo.InvariantCulture);
                    WriteRoadIdentity(road.Parent, tr, name);

                    bool hasExisting = HasAnnotationChild(
                        space,
                        tr,
                        "ROAD_NAME",
                        road.Parent.Handle.ToString());
                    if (replaceNames)
                        EraseChildren(
                            space,
                            tr,
                            "ROAD_NAME",
                            road.Parent.Handle.ToString());
                    else if (hasExisting)
                    {
                        existingKept++;
                        continue;
                    }

                    var recipe = new AnnotationRecipe
                    {
                        Name = name,
                        Layer = SafeLayer(model.Text("Layer"), LabelLayer),
                        Position = model.Text("Position"),
                        Sections = model.Text("Placement") == SectionPlacement,
                        Offset = Math.Abs(model.Double("Offset", 2)),
                        PaperHeight = model.Double("TextHeight", 2.5),
                        Avoid = model.Text("Avoid") != "No",
                        BackgroundMask = backgroundMask
                    };
                    created += AddRoadNames(
                        document.Database,
                        tr,
                        space,
                        road,
                        network,
                        recipe,
                        occupancy,
                        ref skipped);
                }

                foreach (RoadAnnotationSource road in roads)
                    road.DisposeTransient();
                tr.Commit();
            }
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_ROADNAMES complete. Labels created={0}; existing road-name groups kept={1}; crowded locations skipped={2}.", created, existingKept, skipped);
        }

        private void PlaceRoadDimensions()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            var model = new ProductionSettingsDialogModel("CE Tools - Road Width Dimensions",
                "Measure linked road edges at road or junction-section midpoints. Lane and full-width dimensions use separate dimension lines.");
            model.AddChoice("Scope", "01 Selection", "Roads", "All", "Roads to dimension.", new[] { "All", "Selected" });
            model.AddText("Layer", "02 Dimensions", "Dimension layer", DimensionLayer, "Existing or new dimension layer.");
            List<string> dimensionStyles = ReadDimensionStyleNames(document.Database);
            string currentDimStyle = CurrentDimensionStyleName(document.Database);
            model.AddChoice("DimStyle", "02 Dimensions", "Dimension style", currentDimStyle,
                "Use an existing drawing DIMSTYLE for every generated road-width dimension.", dimensionStyles);
            model.AddChoice("WidthSource", "02 Dimensions", "Road width source", "Auto: active corridor assembly / linked edges",
                "Auto reads lane/road offsets from the applied assembly used by the active corridor for Civil 3D alignments and falls back to linked CE road edges. Linked edges only keeps the preliminary-layout behaviour.",
                new[] { "Auto: active corridor assembly / linked edges", "Active corridor assembly lanes", "Linked CE road edges only" });
            model.AddChoice("Mode", "02 Dimensions", "Dimension type", "Lane and full road widths", "Widths to measure.", new[] { "Lane widths", "Full road width", "Lane and full road widths" });
            model.AddChoice("Placement", "02 Dimensions", "Dimension locations", SectionPlacement,
                "Detect all crossing roads, including unselected roads. Include road-end sections.", new[] { "Road midpoint", SectionPlacement });
            model.AddPositiveDouble("Offset", "02 Dimensions", "Dimension-line offset", 1, "Distance along the road from the measured cross-section to the dimension line.");
            model.AddChoice("Avoid", "02 Dimensions", "Avoid overlapping annotations", "Yes", "Move dimension lines along the road, keeping measurement anchors at the section midpoint.", new[] { "Yes", "No" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;
            int count = 0, skipped = 0;
            using (Transaction tr = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = GetModelSpace(document.Database, tr, OpenMode.ForWrite);
                List<RoadAnnotationSource> roads = ResolveRoadAnnotationSources(
                    document,
                    tr,
                    space,
                    model.Text("Scope"),
                    "\nSelect Civil 3D road alignments or CE road centrelines to dimension: ");
                if (roads.Count == 0) return;

                List<Curve> network = roads.Select(item => item.Geometry).ToList();
                network.AddRange(AnnotationRoads(space, tr)
                    .Where(curve => !network.Contains(curve)));
                foreach (RoadAnnotationSource road in roads)
                    EraseChildren(
                        space,
                        tr,
                        "ROAD_DIM",
                        road.Parent.Handle.ToString());

                ObjectId dimensionStyleId = ResolveDimensionStyleId(
                    document.Database,
                    tr,
                    model.Text("DimStyle"));
                var occupancy = new RoadAnnotationPlacement.Occupancy(space, tr);
                foreach (RoadAnnotationSource road in roads)
                {
                    var recipe = new AnnotationRecipe
                    {
                        Layer = SafeLayer(model.Text("Layer"), DimensionLayer),
                        Mode = model.Text("Mode"),
                        Sections = model.Text("Placement") == SectionPlacement,
                        Offset = model.Double("Offset", 1),
                        Avoid = model.Text("Avoid") != "No",
                        DimensionStyleId = dimensionStyleId,
                        WidthSource = model.Text("WidthSource")
                    };
                    count += AddRoadDimensions(
                        document.Database,
                        tr,
                        space,
                        road,
                        network,
                        recipe,
                        occupancy,
                        ref skipped);
                }

                foreach (RoadAnnotationSource road in roads)
                    road.DisposeTransient();
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

        private static int AddRoadNames(Database db, Transaction tr, BlockTableRecord space, RoadAnnotationSource source,
            IList<Curve> network, AnnotationRecipe recipe, RoadAnnotationPlacement.Occupancy occupancy, ref int skipped)
        {
            Curve road = source.Geometry;
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
                if (recipe.BackgroundMask)
                {
                    text.BackgroundFill = true;
                    text.UseBackgroundColor = true;
                    try { text.BackgroundScaleFactor = 1.1; } catch { }
                }
                if (!occupancy.PlaceText(text, anchor, centered ? new Vector3d(0, 0, 0) : normal, centered ? 0 : recipe.Offset, recipe.Avoid))
                { text.Dispose(); skipped++; continue; }
                PaperAnnotationScale.SetAnnotative(text);
                space.AppendEntity(text);
                tr.AddNewlyCreatedDBObject(text, true);
                WriteAnnotationLink(text, tr, source.Parent, "ROAD_NAME", recipe);
                count++;
            }
            return count;
        }

        private static int AddRoadDimensions(Database db, Transaction tr, BlockTableRecord space, RoadAnnotationSource source,
            IList<Curve> network, AnnotationRecipe recipe, RoadAnnotationPlacement.Occupancy occupancy, ref int skipped)
        {
            Curve road = source.Geometry;
            List<Curve> edges = ReadChildren(space, tr, "EDGE", source.Parent.Handle.ToString()).ToList();
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
                List<Point3d> left;
                List<Point3d> right;
                if (!TryRoadWidthPoints(
                        source,
                        tr,
                        space,
                        station,
                        centre,
                        normal,
                        edges,
                        recipe.WidthSource,
                        out left,
                        out right))
                {
                    skipped++;
                    continue;
                }
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
                        var dim = new AlignedDimension(
                            pair.Item1,
                            pair.Item2,
                            midpoint,
                            string.Empty,
                            recipe.DimensionStyleId.IsNull ? db.Dimstyle : recipe.DimensionStyleId);
                        dim.SetDatabaseDefaults(db);
                        dim.LayerId = layer;
                        space.AppendEntity(dim);
                        tr.AddNewlyCreatedDBObject(dim, true);
                        WriteAnnotationLink(dim, tr, source.Parent, "ROAD_DIM", recipe);
                        occupancy.Reserve(bounds);
                        count++; placed = true; break;
                    }
                    if (!placed) skipped++;
                }
            }
            return count;
        }

        private static void WriteAnnotationLink(Entity entity, Transaction tr, Entity road, string kind, AnnotationRecipe recipe)
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
            internal string Name = "", Layer, Position = "Above", Mode = "Lane and full road widths", WidthSource = "";
            internal double Offset, PaperHeight = 2.5;
            internal bool Sections, Avoid = true, BackgroundMask = true;
            internal ObjectId DimensionStyleId = ObjectId.Null;
            internal string Encode()
            {
                return "ANNOT3|" + Position + "|" + Mode + "|" + Sections + "|" + Avoid + "|" +
                    (BackgroundMask ? "1" : "0") + "|" + WidthSource + "|" +
                    (DimensionStyleId.IsNull ? string.Empty : DimensionStyleId.Handle.ToString());
            }
            internal static AnnotationRecipe Decode(RoadLink link, Entity entity)
            {
                var recipe = new AnnotationRecipe { Name = link.Name, Layer = entity.Layer, Offset = Math.Abs(link.Offset),
                    Position = link.Offset < 0 ? "Below" : "Above", PaperHeight = link.Width > 0 ? link.Width : 2.5 };
                string[] fields = (link.Group ?? "").Split('|');
                if (fields.Length >= 8 && fields[0] == "ANNOT3")
                {
                    recipe.Position = fields[1];
                    recipe.Mode = fields[2];
                    recipe.Sections = fields[3] == "True";
                    recipe.Avoid = fields[4] == "True";
                    recipe.BackgroundMask = fields[5] != "0";
                    recipe.WidthSource = fields[6];
                    long handle;
                    if (long.TryParse(fields[7], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out handle))
                    {
                        try { recipe.DimensionStyleId = entity.Database.GetObjectId(false, new Handle(handle), 0); } catch { }
                    }
                }
                else if (fields.Length == 5 && fields[0] == "ANNOT2")
                { recipe.Position = fields[1]; recipe.Mode = fields[2]; recipe.Sections = fields[3] == "True"; recipe.Avoid = fields[4] == "True"; }
                else if (entity is MText)
                    recipe.PaperHeight = ((MText)entity).TextHeight / Math.Max(1e-8, PaperAnnotationScale.ModelTextHeight(entity.Database, 1.0));
                return recipe;
            }
        }
    }
}

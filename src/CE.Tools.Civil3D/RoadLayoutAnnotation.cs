using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil;
using Autodesk.Civil.ApplicationServices;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivilCorridor = Autodesk.Civil.DatabaseServices.Corridor;
using CivilBaseline = Autodesk.Civil.DatabaseServices.Baseline;
using CivilBaselineRegion = Autodesk.Civil.DatabaseServices.BaselineRegion;
using CivilAppliedAssembly = Autodesk.Civil.DatabaseServices.AppliedAssembly;
using CivilCalculatedLink = Autodesk.Civil.DatabaseServices.CalculatedLink;
using CivilCalculatedPoint = Autodesk.Civil.DatabaseServices.CalculatedPoint;
using CivilCorridorCodeCollection = Autodesk.Civil.DatabaseServices.CorridorCodeCollection;

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
                    .OrderBy(item => AnnotationRoadOrderGroup(item.Geometry))
                    .ThenBy(item => AnnotationRoadOrderPrimary(item.Geometry))
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

        private static List<RoadAnnotationSource> ResolveRoadAnnotationSources(
            Document document,
            Transaction transaction,
            BlockTableRecord space,
            string scope,
            string prompt)
        {
            var result = new List<RoadAnnotationSource>();
            if (document == null || transaction == null || space == null)
                return result;

            HashSet<ObjectId> selected = null;
            if (string.Equals(scope, "Selected", StringComparison.OrdinalIgnoreCase))
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
                            MessageForAdding = prompt,
                            AllowDuplicates = false,
                            RejectObjectsFromNonCurrentSpace = true
                        });
                }
                document.Editor.SetImpliedSelection(new ObjectId[0]);
                if (selection.Status != PromptStatus.OK ||
                    selection.Value == null)
                    return result;
                selected = new HashSet<ObjectId>(
                    selection.Value.GetObjectIds());
            }

            CivilDocument civil = CivilApplication.ActiveDocument;
            var alignmentIds = new List<ObjectId>();
            if (civil != null)
            {
                foreach (ObjectId id in civil.GetAlignmentIds())
                {
                    if (selected != null && !selected.Contains(id))
                        continue;
                    CivilAlignment alignment = null;
                    try
                    {
                        alignment = transaction.GetObject(
                            id,
                            OpenMode.ForRead,
                            false) as CivilAlignment;
                    }
                    catch { }
                    if (!IsRoadAnnotationAlignment(alignment))
                        continue;
                    alignmentIds.Add(id);
                }
            }

            // Prefer Civil 3D alignments when the drawing has them. This prevents
            // duplicate labels on preliminary CE centre polylines and the final
            // road alignments occupying the same route.
            if (alignmentIds.Count > 0)
            {
                foreach (ObjectId id in alignmentIds)
                {
                    CivilAlignment alignment = transaction.GetObject(
                        id,
                        OpenMode.ForRead,
                        false) as CivilAlignment;
                    Polyline geometry =
                        BuildAlignmentAnnotationCurve(
                            alignment,
                            2.0);
                    if (geometry == null)
                        continue;
                    result.Add(new RoadAnnotationSource
                    {
                        Parent = alignment,
                        Alignment = alignment,
                        Geometry = geometry,
                        Transient = true
                    });
                }
                return result;
            }

            IEnumerable<ObjectId> ids = selected == null
                ? space.Cast<ObjectId>()
                : selected;
            foreach (ObjectId id in ids)
            {
                Polyline road = null;
                try
                {
                    road = transaction.GetObject(
                        id,
                        OpenMode.ForRead,
                        false) as Polyline;
                }
                catch { }
                if (road == null ||
                    !HasKind(transaction, id, "CENTER"))
                    continue;
                result.Add(new RoadAnnotationSource
                {
                    Parent = road,
                    Geometry = road,
                    Transient = false
                });
            }
            return result;
        }

        private static bool IsRoadAnnotationAlignment(
            CivilAlignment alignment)
        {
            if (alignment == null ||
                alignment.AlignmentType != AlignmentType.Centerline)
                return false;

            if (CETools.Core.RoadAnnotationPlan.RoadNumber(
                    alignment.Name) > 0)
                return true;
            return (alignment.Description ?? string.Empty)
                .IndexOf(
                    "CE road",
                    StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static Polyline BuildAlignmentAnnotationCurve(
            CivilAlignment alignment,
            double interval)
        {
            if (alignment == null)
                return null;
            double start = alignment.StartingStation;
            double end = alignment.EndingStation;
            if (end < start)
            {
                double swap = start;
                start = end;
                end = swap;
            }
            if (end - start <= Tol)
                return null;

            int segments = Math.Max(
                2,
                (int)Math.Ceiling(
                    alignment.Length /
                    Math.Max(0.25, interval)));
            segments = Math.Min(segments, 50000);
            var polyline = new Polyline(segments + 1);
            int added = 0;
            for (int index = 0; index <= segments; index++)
            {
                double station =
                    start +
                    (end - start) *
                    index / segments;
                double x = 0.0;
                double y = 0.0;
                try
                {
                    alignment.PointLocation(
                        station,
                        0.0,
                        ref x,
                        ref y);
                    polyline.AddVertexAt(
                        added++,
                        new Point2d(x, y),
                        0.0,
                        0.0,
                        0.0);
                }
                catch { }
            }
            if (added < 2)
            {
                polyline.Dispose();
                return null;
            }
            return polyline;
        }

        private static void WriteRoadIdentity(
            Entity road,
            Transaction transaction,
            string name)
        {
            if (road == null ||
                transaction == null)
                return;

            RoadLink link;
            if (TryReadLink(road, transaction, out link))
            {
                if (!road.IsWriteEnabled)
                    road.UpgradeOpen();
                link.Name = name;
                WriteLink(
                    road,
                    transaction,
                    link);
            }

            if (!road.IsWriteEnabled)
                road.UpgradeOpen();
            if (road.ExtensionDictionary.IsNull)
                road.CreateExtensionDictionary();
            DBDictionary dictionary =
                transaction.GetObject(
                    road.ExtensionDictionary,
                    OpenMode.ForWrite,
                    false) as DBDictionary;
            if (dictionary == null)
                return;

            const string key = "CE_ROAD_NAME_LINK";
            Xrecord record;
            if (dictionary.Contains(key))
                record = transaction.GetObject(
                    dictionary.GetAt(key),
                    OpenMode.ForWrite,
                    false) as Xrecord;
            else
            {
                record = new Xrecord();
                dictionary.SetAt(
                    key,
                    record);
                transaction.AddNewlyCreatedDBObject(
                    record,
                    true);
            }
            if (record != null)
            {
                record.Data = new ResultBuffer(
                    new TypedValue(
                        (int)DxfCode.Text,
                        "ROAD_NAME"),
                    new TypedValue(
                        (int)DxfCode.Text,
                        name ?? string.Empty));
            }
        }

        private static bool HasAnnotationChild(
            BlockTableRecord space,
            Transaction transaction,
            string kind,
            string parentHandle)
        {
            foreach (ObjectId id in space)
            {
                Entity entity = null;
                try
                {
                    entity = transaction.GetObject(
                        id,
                        OpenMode.ForRead,
                        false) as Entity;
                }
                catch { }
                RoadLink link;
                if (entity != null &&
                    TryReadLink(
                        entity,
                        transaction,
                        out link) &&
                    string.Equals(
                        link.Kind,
                        kind,
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(
                        link.ParentHandle,
                        parentHandle,
                        StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static int AnnotationRoadOrderGroup(
            Curve curve)
        {
            if (curve == null)
                return 2;
            try
            {
                Extents3d extents =
                    curve.GeometricExtents;
                double width =
                    Math.Abs(
                        extents.MaxPoint.X -
                        extents.MinPoint.X);
                double height =
                    Math.Abs(
                        extents.MaxPoint.Y -
                        extents.MinPoint.Y);
                return width >= height ? 0 : 1;
            }
            catch
            {
                return 2;
            }
        }

        private static double AnnotationRoadOrderPrimary(
            Curve curve)
        {
            Point3d mid = MidPoint(curve);
            return AnnotationRoadOrderGroup(curve) == 0
                ? -mid.Y
                : mid.X;
        }

        private static List<string> ReadDimensionStyleNames(
            Database database)
        {
            var names = new List<string>();
            if (database == null)
                return names;
            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                DimStyleTable table = transaction.GetObject(
                    database.DimStyleTableId,
                    OpenMode.ForRead,
                    false) as DimStyleTable;
                if (table != null)
                {
                    foreach (ObjectId id in table)
                    {
                        DimStyleTableRecord style =
                            transaction.GetObject(
                                id,
                                OpenMode.ForRead,
                                false) as DimStyleTableRecord;
                        if (style != null &&
                            !string.IsNullOrWhiteSpace(style.Name))
                            names.Add(style.Name);
                    }
                }
            }
            if (names.Count == 0)
                names.Add("Standard");
            return names
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(
                    item => item,
                    StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private static string CurrentDimensionStyleName(
            Database database)
        {
            if (database == null ||
                database.Dimstyle.IsNull)
                return "Standard";
            try
            {
                using (Transaction transaction =
                    database.TransactionManager.StartTransaction())
                {
                    DimStyleTableRecord style =
                        transaction.GetObject(
                            database.Dimstyle,
                            OpenMode.ForRead,
                            false) as DimStyleTableRecord;
                    return style == null ||
                        string.IsNullOrWhiteSpace(style.Name)
                        ? "Standard"
                        : style.Name;
                }
            }
            catch
            {
                return "Standard";
            }
        }

        private static ObjectId ResolveDimensionStyleId(
            Database database,
            Transaction transaction,
            string requested)
        {
            if (database == null ||
                transaction == null)
                return ObjectId.Null;
            DimStyleTable table = transaction.GetObject(
                database.DimStyleTableId,
                OpenMode.ForRead,
                false) as DimStyleTable;
            if (table != null &&
                !string.IsNullOrWhiteSpace(requested) &&
                table.Has(requested))
                return table[requested];
            return database.Dimstyle;
        }

        private static bool TryRoadWidthPoints(
            RoadAnnotationSource source,
            Transaction transaction,
            BlockTableRecord space,
            double geometryStation,
            Point3d centre,
            Vector3d normal,
            IList<Curve> edges,
            string widthSource,
            out List<Point3d> left,
            out List<Point3d> right)
        {
            left = new List<Point3d>();
            right = new List<Point3d>();

            bool corridorRequested =
                !string.Equals(
                    widthSource,
                    "Linked CE road edges only",
                    StringComparison.OrdinalIgnoreCase);
            bool corridorOnly =
                string.Equals(
                    widthSource,
                    "Active corridor assembly lanes",
                    StringComparison.OrdinalIgnoreCase);
            if (corridorRequested &&
                source != null &&
                source.Alignment != null)
            {
                double leftOffset;
                double rightOffset;
                double alignmentStation;
                if (TryReadCorridorLaneOffsets(
                        source,
                        transaction,
                        geometryStation,
                        out alignmentStation,
                        out leftOffset,
                        out rightOffset))
                {
                    double lx = 0.0, ly = 0.0;
                    double rx = 0.0, ry = 0.0;
                    try
                    {
                        source.Alignment.PointLocation(
                            alignmentStation,
                            leftOffset,
                            ref lx,
                            ref ly);
                        source.Alignment.PointLocation(
                            alignmentStation,
                            rightOffset,
                            ref rx,
                            ref ry);
                        Point3d lp =
                            new Point3d(lx, ly, centre.Z);
                        Point3d rp =
                            new Point3d(rx, ry, centre.Z);
                        if ((lp - centre).DotProduct(normal) > 0)
                        {
                            left.Add(lp);
                            right.Add(rp);
                        }
                        else
                        {
                            left.Add(rp);
                            right.Add(lp);
                        }
                        return true;
                    }
                    catch { }
                }
                if (corridorOnly)
                    return false;
            }

            var hits = new List<Point3d>();
            using (var section = new Xline
            {
                BasePoint = centre,
                UnitDir = normal
            })
            {
                foreach (Curve edge in
                    edges ?? Enumerable.Empty<Curve>())
                {
                    var points =
                        new Point3dCollection();
                    try
                    {
                        section.IntersectWith(
                            edge,
                            Intersect.OnBothOperands,
                            points,
                            IntPtr.Zero,
                            IntPtr.Zero);
                    }
                    catch
                    {
                        continue;
                    }
                    foreach (Point3d point in points)
                        hits.Add(point);
                }
            }

            left = hits
                .Where(point =>
                    (point - centre)
                    .DotProduct(normal) > Tol)
                .OrderBy(point =>
                    point.DistanceTo(centre))
                .ToList();
            right = hits
                .Where(point =>
                    (point - centre)
                    .DotProduct(normal) < -Tol)
                .OrderBy(point =>
                    point.DistanceTo(centre))
                .ToList();
            return left.Count > 0 &&
                   right.Count > 0;
        }

        private static bool TryReadCorridorLaneOffsets(
            RoadAnnotationSource source,
            Transaction transaction,
            double geometryStation,
            out double alignmentStation,
            out double leftOffset,
            out double rightOffset)
        {
            alignmentStation = 0.0;
            leftOffset = 0.0;
            rightOffset = 0.0;
            if (source == null ||
                source.Alignment == null ||
                source.Geometry == null ||
                transaction == null)
                return false;

            double geometryLength =
                source.Geometry.GetDistanceAtParameter(
                    source.Geometry.EndParam);
            double fraction =
                geometryLength <= Tol
                    ? 0.0
                    : Math.Max(
                        0.0,
                        Math.Min(
                            1.0,
                            geometryStation /
                            geometryLength));
            alignmentStation =
                source.Alignment.StartingStation +
                (source.Alignment.EndingStation -
                 source.Alignment.StartingStation) *
                fraction;

            CivilDocument civil =
                CivilApplication.ActiveDocument;
            if (civil == null)
                return false;

            CivilAppliedAssembly nearest = null;
            double nearestDistance =
                double.MaxValue;
            foreach (ObjectId corridorId in
                civil.CorridorCollection)
            {
                CivilCorridor corridor = null;
                try
                {
                    corridor = transaction.GetObject(
                        corridorId,
                        OpenMode.ForRead,
                        false) as CivilCorridor;
                }
                catch { }
                if (corridor == null)
                    continue;

                foreach (CivilBaseline baseline in
                    corridor.Baselines)
                {
                    if (baseline == null ||
                        baseline.AlignmentId !=
                            source.Alignment.ObjectId)
                        continue;
                    foreach (CivilBaselineRegion region in
                        baseline.BaselineRegions)
                    {
                        if (region == null)
                            continue;
                        foreach (CivilAppliedAssembly assembly in
                            region.AppliedAssemblies)
                        {
                            if (assembly == null)
                                continue;
                            double station;
                            if (!TryAppliedAssemblyStation(
                                    assembly,
                                    out station))
                                continue;
                            double distance =
                                Math.Abs(
                                    station -
                                    alignmentStation);
                            if (distance <
                                nearestDistance)
                            {
                                nearestDistance =
                                    distance;
                                nearest = assembly;
                            }
                        }
                    }
                }
            }
            if (nearest == null)
                return false;

            var offsets = new List<double>();
            foreach (CivilCalculatedLink link in
                nearest.Links)
            {
                if (link == null ||
                    !IsLaneOrRoadLink(
                        link.CorridorCodes))
                    continue;
                foreach (CivilCalculatedPoint point in
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
            if (offsets.Count < 2)
                return false;

            double positive = offsets
                .Where(value => value > Tol)
                .DefaultIfEmpty(0.0)
                .Max();
            double negative = offsets
                .Where(value => value < -Tol)
                .DefaultIfEmpty(0.0)
                .Min();
            if (positive <= Tol ||
                negative >= -Tol)
                return false;

            leftOffset = positive;
            rightOffset = negative;
            return true;
        }

        private static bool TryAppliedAssemblyStation(
            CivilAppliedAssembly assembly,
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

        private static bool IsLaneOrRoadLink(
            CivilCorridorCodeCollection codes)
        {
            if (codes == null)
                return false;
            var codeValues = new List<string>();
            foreach (string code in codes)
            {
                if (!string.IsNullOrWhiteSpace(code))
                    codeValues.Add(code);
            }
            string text =
                string.Join(" ", codeValues)
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

        private sealed class RoadAnnotationSource
        {
            internal Entity Parent;
            internal CivilAlignment Alignment;
            internal Curve Geometry;
            internal bool Transient;

            internal void DisposeTransient()
            {
                if (Transient &&
                    Geometry != null)
                {
                    try { Geometry.Dispose(); }
                    catch { }
                }
            }
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
                BlockTableRecord space =
                    GetModelSpace(
                        document.Database,
                        tr,
                        OpenMode.ForWrite);
                var jobs =
                    new Dictionary<string, Tuple<RoadAnnotationSource, string, AnnotationRecipe>>();

                foreach (ObjectId id in
                    space.Cast<ObjectId>().ToList())
                {
                    if (id.IsErased) continue;
                    Entity entity = tr.GetObject(
                        id,
                        OpenMode.ForRead,
                        false) as Entity;
                    RoadLink link;
                    if (entity == null ||
                        entity.IsErased ||
                        !TryReadLink(entity, tr, out link) ||
                        (link.Kind != "ROAD_NAME" &&
                         link.Kind != "ROAD_DIM"))
                        continue;

                    ObjectId parentId =
                        ResolveHandle(
                            document.Database,
                            link.ParentHandle);
                    if (parentId.IsNull ||
                        parentId.IsErased)
                        continue;

                    Entity parent = null;
                    try
                    {
                        parent = tr.GetObject(
                            parentId,
                            OpenMode.ForRead,
                            false) as Entity;
                    }
                    catch { }
                    if (parent == null)
                        continue;

                    RoadAnnotationSource source = null;
                    CivilAlignment alignment =
                        parent as CivilAlignment;
                    if (alignment != null)
                    {
                        Polyline geometry =
                            BuildAlignmentAnnotationCurve(
                                alignment,
                                2.0);
                        if (geometry != null)
                        {
                            source = new RoadAnnotationSource
                            {
                                Parent = alignment,
                                Alignment = alignment,
                                Geometry = geometry,
                                Transient = true
                            };
                        }
                    }
                    else
                    {
                        Curve road =
                            parent as Curve;
                        if (road != null)
                        {
                            source = new RoadAnnotationSource
                            {
                                Parent = parent,
                                Geometry = road,
                                Transient = false
                            };
                        }
                    }
                    if (source == null)
                        continue;

                    // Legacy dimensions with no annotation recipe remain untouched.
                    // ANNOT2 and ANNOT3 are both safe/current linked formats.
                    if (link.Kind == "ROAD_DIM" &&
                        !(link.Group ?? string.Empty)
                            .StartsWith(
                                "ANNOT",
                                StringComparison.Ordinal))
                    {
                        source.DisposeTransient();
                        continue;
                    }

                    string key =
                        link.Kind + ":" +
                        link.ParentHandle;
                    Tuple<RoadAnnotationSource, string, AnnotationRecipe> old;
                    if (jobs.TryGetValue(key, out old))
                        old.Item1.DisposeTransient();
                    jobs[key] = Tuple.Create(
                        source,
                        link.Kind,
                        AnnotationRecipe.Decode(
                            link,
                            entity));
                }

                foreach (var job in jobs.Values)
                {
                    EraseChildren(
                        space,
                        tr,
                        job.Item2,
                        job.Item1.Parent.Handle.ToString());
                }

                var occupancy =
                    new RoadAnnotationPlacement.Occupancy(
                        space,
                        tr);
                List<Curve> network =
                    jobs.Values
                        .Select(job => job.Item1.Geometry)
                        .Where(geometry => geometry != null)
                        .ToList();
                network.AddRange(
                    AnnotationRoads(
                        space,
                        tr)
                    .Where(curve =>
                        !network.Contains(curve)));

                foreach (var job in
                    jobs.Values.OrderBy(
                        value =>
                            value.Item2 == "ROAD_NAME"
                                ? 0
                                : 1))
                {
                    count += job.Item2 == "ROAD_NAME"
                        ? AddRoadNames(
                            document.Database,
                            tr,
                            space,
                            job.Item1,
                            network,
                            job.Item3,
                            occupancy,
                            ref skipped)
                        : AddRoadDimensions(
                            document.Database,
                            tr,
                            space,
                            job.Item1,
                            network,
                            job.Item3,
                            occupancy,
                            ref skipped);
                }

                foreach (var job in jobs.Values)
                    job.Item1.DisposeTransient();
                tr.Commit();
            }

            if (skipped > 0)
            {
                document.Editor.WriteMessage(
                    "\nRoad annotation refresh: crowded/missing-width locations skipped={0}.",
                    skipped);
            }
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

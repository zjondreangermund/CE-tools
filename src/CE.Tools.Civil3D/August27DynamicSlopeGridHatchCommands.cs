using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivilFeatureLine = Autodesk.Civil.DatabaseServices.FeatureLine;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

[assembly: CommandClass(typeof(CETools.Civil3D.August27DynamicSlopeGridHatchCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// August 27 field boundary for dynamic slope presentation, safe road-side
    /// hatching and survey endpoint connection. Slope graphics are real AutoCAD
    /// Leader entities plus annotative MText; no slope arrow is built from a
    /// Polyline/Line arrow shaft and manually drawn arrow-head segments.
    /// </summary>
    public sealed class August27DynamicSlopeGridHatchCommands
    {
        internal const string SourceKey = "CE_AUG27_SLOPE_SOURCE";
        internal const string ChildKey = "CE_AUG27_SLOPE_CHILD";
        internal const string CrossfallKey = "CE_AUG27_CROSSFALL_SOURCE";
        private const string OldSlopeChildKey = "CE_FIELD_SLOPE_LINK";
        private const string SlopeLayer = "CE-SLOPE-LEADERS";
        private const string SlopeTextLayer = "CE-SLOPE-TEXT";
        private const string HatchLayer = "CE-ROAD-HATCH";
        private const string HatchBoundaryLayer = "CE-ROAD-HATCH-BOUNDARY";
        private const double Tol = 1e-7;

        [CommandMethod("CE_TOOLS", "CE_FEATURELINECROSSFALLARROWS", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void FeatureLineCrossfallArrows()
        {
            Document document = Active();
            if (document == null) return;
            August27DynamicSlopeManager.Initialize();

            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Slope Between Two Feature Lines",
                "Create linked leader arrows and slope values between two feature lines. The annotation rebuilds automatically after either feature line is moved or its elevations change.");
            settings.AddPositiveDouble("Spacing", "01 Sampling", "Arrow spacing", 20.0,
                "Approximate plan spacing between crossfall arrows.");
            settings.AddPaperHeight("TextHeight", "02 Annotation", "Paper text height", 2.5,
                "Absolute annotative paper height.");
            settings.AddPositiveDouble("Arrow", "02 Annotation", "Arrow size (paper mm)", 2.5,
                "Leader arrow size stored as a paper-size setting and rebuilt for the active annotation scale.");
            settings.AddChoice("Colour", "02 Annotation", "Colour", "Yellow",
                "Colour for the leader and slope value.",
                ColourNames());
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;

            PromptSelectionResult selection = Select(document.Editor,
                "\nSelect exactly two Civil 3D feature lines for crossfall arrows: ");
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;
            ObjectId[] ids = selection.Value.GetObjectIds().Distinct().ToArray();
            var features = new List<ObjectId>();
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids)
                {
                    try
                    {
                        CivilFeatureLine feature = transaction.GetObject(id, OpenMode.ForRead, false) as CivilFeatureLine;
                        if (feature != null && !feature.IsReferenceObject) features.Add(id);
                    }
                    catch { }
                }
            }
            if (features.Count != 2)
            {
                document.Editor.WriteMessage("\nCE_FEATURELINECROSSFALLARROWS requires exactly two editable feature lines.");
                return;
            }

            int colour = ColourIndex(settings.Text("Colour"));
            WriteCrossfallSettings(document, features[0], features[1],
                settings.Double("Spacing", 20.0), settings.Double("TextHeight", 2.5),
                settings.Double("Arrow", 2.5), colour);
            int created = RebuildCrossfall(document, features[0], features[1]);
            August27DynamicSlopeManager.RegisterDocument(document);
            August21DisplayRefresh.Flush(document);
            document.Editor.WriteMessage("\nCE_FEATURELINECROSSFALLARROWS complete. Dynamic crossfall leader sets created={0}.", created);
        }

        [CommandMethod("CE_TOOLS", "CE_CONNECTENDPOINTS", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void ConnectSelectedEndpoints()
        {
            Document document = Active();
            if (document == null) return;
            PromptSelectionResult selection = Select(document.Editor,
                "\nSelect multiple polylines and/or Civil 3D feature lines whose endpoints must be connected: ");
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;
            PromptPointResult pick = document.Editor.GetPoint(
                "\nPick near the side/endpoints to connect: ");
            if (pick.Status != PromptStatus.OK) return;
            Point3d reference = pick.Value.TransformBy(document.Editor.CurrentUserCoordinateSystem);

            var points = new List<Point3d>();
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in selection.Value.GetObjectIds().Distinct())
                {
                    Entity entity = null;
                    try { entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity; } catch { }
                    Point3d start, end;
                    if (!TryEndpoints(entity, out start, out end)) continue;
                    points.Add(PlanDistance(start, reference) <= PlanDistance(end, reference) ? start : end);
                }
            }
            if (points.Count < 2)
            {
                document.Editor.WriteMessage("\nCE_CONNECTENDPOINTS requires at least two supported source objects.");
                return;
            }

            double xSpan = points.Max(p => p.X) - points.Min(p => p.X);
            double ySpan = points.Max(p => p.Y) - points.Min(p => p.Y);
            points = xSpan >= ySpan
                ? points.OrderBy(p => p.X).ThenBy(p => p.Y).ToList()
                : points.OrderBy(p => p.Y).ThenBy(p => p.X).ToList();

            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = transaction.GetObject(document.Database.CurrentSpaceId, OpenMode.ForWrite, false) as BlockTableRecord;
                var output = new Polyline(points.Count);
                output.SetDatabaseDefaults(document.Database);
                output.Elevation = points[0].Z;
                for (int index = 0; index < points.Count; index++)
                    output.AddVertexAt(index, new Point2d(points[index].X, points[index].Y), 0.0, 0.0, 0.0);
                space.AppendEntity(output);
                transaction.AddNewlyCreatedDBObject(output, true);
                transaction.Commit();
            }
            August21DisplayRefresh.Flush(document);
            document.Editor.WriteMessage("\nCE_CONNECTENDPOINTS complete. One polyline connected {0} selected endpoints.", points.Count);
        }

        [CommandMethod("CE_TOOLS", "CE_DYNAMICSLOPESREFRESH", CommandFlags.Modal | CommandFlags.Redraw)]
        public void RefreshDynamicSlopeLeaders()
        {
            Document document = Active();
            if (document == null) return;
            int refreshed = RefreshAllDynamicSlopeSources(document);
            August21DisplayRefresh.Flush(document);
            document.Editor.WriteMessage("\nCE_DYNAMICSLOPESREFRESH complete. Dynamic slope source groups refreshed={0}.", refreshed);
        }

        internal static void FeatureLineSlopeArrows(Document document)
        {
            if (document == null) return;
            August27DynamicSlopeManager.Initialize();
            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Dynamic Feature-Line Slope Leaders",
                "Create true AutoCAD leader arrows and annotative slope values for multiple feature lines. Moving a feature line or changing elevations automatically rebuilds its linked annotations.");
            settings.AddPaperHeight("TextHeight", "01 Annotation", "Paper text height", 2.5,
                "Absolute annotative paper height.");
            settings.AddPositiveDouble("Arrow", "01 Annotation", "Arrow size (paper mm)", 2.5,
                "Paper-size leader arrow setting. It is rescaled when the drawing annotation scale changes.");
            settings.AddChoice("Colour", "01 Annotation", "Colour", "Yellow",
                "Leader and value colour.", ColourNames());
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;
            PromptSelectionResult selection = Select(document.Editor,
                "\nSelect multiple feature lines for dynamic slope leaders: ");
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;

            var ids = new List<ObjectId>();
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in selection.Value.GetObjectIds().Distinct())
                {
                    try
                    {
                        CivilFeatureLine feature = transaction.GetObject(id, OpenMode.ForRead, false) as CivilFeatureLine;
                        if (feature != null && !feature.IsReferenceObject) ids.Add(id);
                    }
                    catch { }
                }
            }
            int created = 0;
            foreach (ObjectId id in ids)
            {
                WriteSourceSettings(document, id, "FEATURE", 0.0,
                    settings.Double("TextHeight", 2.5), settings.Double("Arrow", 2.5),
                    ColourIndex(settings.Text("Colour")));
                created += RebuildSingleSource(document, id);
            }
            August27DynamicSlopeManager.RegisterDocument(document);
            August21DisplayRefresh.Flush(document);
            document.Editor.WriteMessage("\nCE_FEATURELINESLOPEARROWS complete. Dynamic leader/value entities created={0}; sources={1}.", created, ids.Count);
        }

        internal static void SurfaceSlopeArrows(Document document)
        {
            if (document == null) return;
            August27DynamicSlopeManager.Initialize();
            PromptEntityOptions options = new PromptEntityOptions("\nSelect Civil 3D surface for dynamic slope leaders: ");
            options.SetRejectMessage("\nSelect a Civil 3D surface.");
            options.AddAllowedClass(typeof(CivilSurface), false);
            PromptEntityResult selected = document.Editor.GetEntity(options);
            if (selected.Status != PromptStatus.OK) return;

            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Dynamic Surface Slope Leaders",
                "Sample a Civil 3D surface and create true leader arrows in the steepest-downhill direction. Surface edits and annotation-scale changes automatically rebuild the linked arrows and values.");
            settings.AddPositiveDouble("Spacing", "01 Sampling", "Grid spacing", 20.0,
                "Plan spacing between sampled leader arrows.");
            settings.AddPaperHeight("TextHeight", "02 Annotation", "Paper text height", 2.5,
                "Absolute annotative paper height.");
            settings.AddPositiveDouble("Arrow", "02 Annotation", "Arrow length (paper mm)", 12.0,
                "Leader display length as a paper-size setting.");
            settings.AddChoice("Colour", "02 Annotation", "Colour", "Yellow",
                "Leader and value colour.", ColourNames());
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;

            WriteSourceSettings(document, selected.ObjectId, "SURFACE",
                settings.Double("Spacing", 20.0), settings.Double("TextHeight", 2.5),
                settings.Double("Arrow", 12.0), ColourIndex(settings.Text("Colour")));
            int created = RebuildSingleSource(document, selected.ObjectId);
            August27DynamicSlopeManager.RegisterDocument(document);
            August21DisplayRefresh.Flush(document);
            document.Editor.WriteMessage("\nCE_SURFACESLOPEARROWS complete. Dynamic surface leader sets created={0}.", created);
        }

        internal static void RoadHatchSides(Document document)
        {
            if (document == null) return;
            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Road Side Hatch (Safe)",
                "Create road-side hatch strips with one source/side per transaction. A failed hatch rolls back only that strip and cannot delete or corrupt the other selected roads.");
            settings.AddPositiveDouble("Distance", "01 Geometry", "Hatch width / offset distance", 5.0,
                "Width of the hatch strip from the selected road line.");
            settings.AddChoice("Side", "01 Geometry", "Side", "Both",
                "Hatch left, right or both sides.", new[] { "Left", "Right", "Both" });
            settings.AddChoice("Pattern", "02 Hatch", "Hatch pattern", "ANSI31",
                "Choose a known AutoCAD predefined hatch pattern.",
                new[] { "ANSI31", "ANSI32", "ANSI33", "ANSI34", "ANSI35", "ANSI36", "ANSI37", "ANSI38", "SOLID", "AR-CONC", "GRAVEL" });
            settings.AddPositiveDouble("Scale", "02 Hatch", "Hatch scale", 1.0,
                "Pattern scale; ignored for SOLID.");
            settings.AddChoice("Colour", "02 Hatch", "Hatch colour", "ByLayer",
                "Choose hatch colour from the dropdown.", ColourNames(true));
            settings.AddPositiveDouble("Sample", "03 Alignments", "Alignment sample interval", 5.0,
                "Plan sampling interval when an alignment is selected.");
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;
            PromptSelectionResult selection = Select(document.Editor,
                "\nSelect multiple road polylines and/or alignments: ");
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;

            int created = 0, skipped = 0, failed = 0;
            string side = settings.Text("Side");
            foreach (ObjectId id in selection.Value.GetObjectIds().Distinct())
            {
                if (string.Equals(side, "Left", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(side, "Both", StringComparison.OrdinalIgnoreCase))
                {
                    if (TryCreateHatchStrip(document, id, Math.Abs(settings.Double("Distance", 5.0)), settings)) created++;
                    else { skipped++; failed++; }
                }
                if (string.Equals(side, "Right", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(side, "Both", StringComparison.OrdinalIgnoreCase))
                {
                    if (TryCreateHatchStrip(document, id, -Math.Abs(settings.Double("Distance", 5.0)), settings)) created++;
                    else { skipped++; failed++; }
                }
            }
            August21DisplayRefresh.Flush(document);
            document.Editor.WriteMessage("\nCE_ROADHATCHSIDES complete. Hatch strips created={0}; failed/skipped={1}. Each failed source/side was rolled back independently.", created, skipped);
        }

        internal static int RefreshAllDynamicSlopeSources(Document document)
        {
            if (document == null) return 0;
            var sourceIds = new HashSet<ObjectId>();
            var pairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord model = transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(document.Database), OpenMode.ForRead, false) as BlockTableRecord;
                if (model == null) return 0;
                foreach (ObjectId id in model)
                {
                    Entity entity = null;
                    try { entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity; } catch { }
                    SourceSettings settings;
                    if (entity != null && TryReadSourceSettings(entity, transaction, out settings)) sourceIds.Add(id);
                    CrossfallSettings cross;
                    if (entity != null && TryReadCrossfall(entity, transaction, out cross)) pairs.Add(PairKey(entity.Handle.ToString(), cross.OtherHandle));
                }
            }
            int count = 0;
            foreach (ObjectId id in sourceIds) { RebuildSingleSource(document, id); count++; }
            foreach (string pair in pairs)
            {
                string[] parts = pair.Split('|');
                if (parts.Length != 2) continue;
                ObjectId a = ResolveHandle(document.Database, parts[0]);
                ObjectId b = ResolveHandle(document.Database, parts[1]);
                if (!a.IsNull && !b.IsNull) { RebuildCrossfall(document, a, b); count++; }
            }
            return count;
        }

        internal static bool HasDynamicSlopeLink(DBObject value, Transaction transaction)
        {
            SourceSettings source;
            CrossfallSettings cross;
            return value != null && (TryReadSourceSettings(value, transaction, out source) || TryReadCrossfall(value, transaction, out cross));
        }

        internal static void RefreshChanged(Document document, IEnumerable<string> handles, bool scaleChanged)
        {
            if (document == null) return;
            if (scaleChanged) { RefreshAllDynamicSlopeSources(document); return; }
            var dirty = new HashSet<string>(handles ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            if (dirty.Count == 0) return;
            var singles = new List<ObjectId>();
            var pairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (string handle in dirty)
                {
                    ObjectId id = ResolveHandle(document.Database, handle);
                    if (id.IsNull || id.IsErased) continue;
                    Entity entity = null;
                    try { entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity; } catch { }
                    SourceSettings source;
                    if (entity != null && TryReadSourceSettings(entity, transaction, out source)) singles.Add(id);
                    CrossfallSettings cross;
                    if (entity != null && TryReadCrossfall(entity, transaction, out cross)) pairs.Add(PairKey(handle, cross.OtherHandle));
                }
            }
            foreach (ObjectId id in singles.Distinct()) RebuildSingleSource(document, id);
            foreach (string pair in pairs)
            {
                string[] parts = pair.Split('|');
                if (parts.Length != 2) continue;
                ObjectId a = ResolveHandle(document.Database, parts[0]);
                ObjectId b = ResolveHandle(document.Database, parts[1]);
                if (!a.IsNull && !b.IsNull) RebuildCrossfall(document, a, b);
            }
        }

        private static int RebuildSingleSource(Document document, ObjectId sourceId)
        {
            SourceSettings settings;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                Entity source = null;
                try { source = transaction.GetObject(sourceId, OpenMode.ForRead, false) as Entity; } catch { }
                if (source == null || !TryReadSourceSettings(source, transaction, out settings)) return 0;
            }
            string handle = sourceId.Handle.ToString();
            EraseChildren(document, handle, string.Empty);
            return string.Equals(settings.Mode, "SURFACE", StringComparison.OrdinalIgnoreCase)
                ? BuildSurfaceChildren(document, sourceId, settings)
                : BuildFeatureChildren(document, sourceId, settings);
        }

        private static int BuildFeatureChildren(Document document, ObjectId sourceId, SourceSettings settings)
        {
            int created = 0;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                CivilFeatureLine source = transaction.GetObject(sourceId, OpenMode.ForRead, false) as CivilFeatureLine;
                if (source == null || source.IsReferenceObject) return 0;
                Point3dCollection points = source.GetPoints(FeatureLinePointType.AllPoints);
                if (points == null || points.Count < 2) return 0;
                BlockTableRecord space = transaction.GetObject(document.Database.CurrentSpaceId, OpenMode.ForWrite, false) as BlockTableRecord;
                ObjectId leaderLayer = EnsureLayer(document.Database, transaction, SlopeLayer);
                ObjectId textLayer = EnsureLayer(document.Database, transaction, SlopeTextLayer);
                double textHeight = PaperAnnotationScale.AnnotativeTextHeight(document.Database, settings.PaperText);
                for (int i = 0; i + 1 < points.Count; i++)
                {
                    Point3d a = points[i], b = points[i + 1];
                    double run = PlanDistance(a, b);
                    if (run <= Tol) continue;
                    Point3d high = a.Z >= b.Z ? a : b;
                    Point3d low = a.Z >= b.Z ? b : a;
                    double slope = Math.Abs(a.Z - b.Z) / run * 100.0;
                    created += AppendLeaderSet(document.Database, transaction, space, high, low,
                        slope, settings, source.Handle.ToString(), string.Empty, "FEATURE", i,
                        leaderLayer, textLayer, textHeight);
                }
                transaction.Commit();
            }
            return created;
        }

        private static int BuildSurfaceChildren(Document document, ObjectId sourceId, SourceSettings settings)
        {
            int created = 0;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                CivilSurface surface = transaction.GetObject(sourceId, OpenMode.ForRead, false) as CivilSurface;
                Entity entity = surface as Entity;
                if (surface == null || entity == null) return 0;
                Extents3d extents;
                try { extents = entity.GeometricExtents; } catch { return 0; }
                BlockTableRecord space = transaction.GetObject(document.Database.CurrentSpaceId, OpenMode.ForWrite, false) as BlockTableRecord;
                ObjectId leaderLayer = EnsureLayer(document.Database, transaction, SlopeLayer);
                ObjectId textLayer = EnsureLayer(document.Database, transaction, SlopeTextLayer);
                double spacing = Math.Max(settings.Spacing, 0.1);
                double displayLength = PaperAnnotationScale.ModelDistance(document.Database, Math.Max(settings.PaperArrow, 2.0));
                double delta = Math.Max(spacing * 0.15, 0.05);
                double textHeight = PaperAnnotationScale.AnnotativeTextHeight(document.Database, settings.PaperText);
                int guard = 0;
                for (double x = extents.MinPoint.X + spacing * 0.5; x < extents.MaxPoint.X && guard < 600; x += spacing)
                {
                    for (double y = extents.MinPoint.Y + spacing * 0.5; y < extents.MaxPoint.Y && guard < 600; y += spacing)
                    {
                        double z, x1, x2, y1, y2;
                        try
                        {
                            z = surface.FindElevationAtXY(x, y);
                            x1 = surface.FindElevationAtXY(x - delta, y);
                            x2 = surface.FindElevationAtXY(x + delta, y);
                            y1 = surface.FindElevationAtXY(x, y - delta);
                            y2 = surface.FindElevationAtXY(x, y + delta);
                        }
                        catch { continue; }
                        double gx = (x2 - x1) / (2.0 * delta);
                        double gy = (y2 - y1) / (2.0 * delta);
                        double slope = Math.Sqrt(gx * gx + gy * gy) * 100.0;
                        if (slope <= Tol) continue;
                        Vector3d down = new Vector3d(-gx, -gy, 0.0).GetNormal();
                        Point3d high = new Point3d(x, y, z);
                        Point3d low = high + down * displayLength;
                        created += AppendLeaderSet(document.Database, transaction, space, high, low,
                            slope, settings, surface.Handle.ToString(), string.Empty, "SURFACE", guard,
                            leaderLayer, textLayer, textHeight);
                        guard++;
                    }
                }
                transaction.Commit();
            }
            return created;
        }

        private static int RebuildCrossfall(Document document, ObjectId firstId, ObjectId secondId)
        {
            if (firstId.IsNull || secondId.IsNull) return 0;
            CrossfallSettings settings;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                CivilFeatureLine first = transaction.GetObject(firstId, OpenMode.ForRead, false) as CivilFeatureLine;
                if (first == null || !TryReadCrossfall(first, transaction, out settings)) return 0;
            }
            string aHandle = firstId.Handle.ToString();
            string bHandle = secondId.Handle.ToString();
            EraseChildren(document, aHandle, bHandle);
            EraseChildren(document, bHandle, aHandle);

            int created = 0;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                CivilFeatureLine a = transaction.GetObject(firstId, OpenMode.ForRead, false) as CivilFeatureLine;
                CivilFeatureLine b = transaction.GetObject(secondId, OpenMode.ForRead, false) as CivilFeatureLine;
                if (a == null || b == null) return 0;
                double lenA = CurveLength(a), lenB = CurveLength(b);
                double minLength = Math.Min(lenA, lenB);
                if (minLength <= Tol) return 0;
                int count = Math.Max(2, (int)Math.Ceiling(minLength / Math.Max(settings.Spacing, 0.1)));
                count = Math.Min(count, 250);
                BlockTableRecord space = transaction.GetObject(document.Database.CurrentSpaceId, OpenMode.ForWrite, false) as BlockTableRecord;
                ObjectId leaderLayer = EnsureLayer(document.Database, transaction, SlopeLayer);
                ObjectId textLayer = EnsureLayer(document.Database, transaction, SlopeTextLayer);
                double textHeight = PaperAnnotationScale.AnnotativeTextHeight(document.Database, settings.PaperText);
                var sourceSettings = new SourceSettings { Mode = "CROSSFALL", Spacing = settings.Spacing, PaperText = settings.PaperText, PaperArrow = settings.PaperArrow, Colour = settings.Colour };
                for (int i = 0; i <= count; i++)
                {
                    double f = i / (double)count;
                    Point3d pa, pb;
                    try { pa = a.GetPointAtDist(lenA * f); pb = b.GetPointAtDist(lenB * f); }
                    catch { continue; }
                    double run = PlanDistance(pa, pb);
                    if (run <= Tol) continue;
                    Point3d high = pa.Z >= pb.Z ? pa : pb;
                    Point3d low = pa.Z >= pb.Z ? pb : pa;
                    double slope = Math.Abs(pa.Z - pb.Z) / run * 100.0;
                    created += AppendLeaderSet(document.Database, transaction, space, high, low,
                        slope, sourceSettings, a.Handle.ToString(), b.Handle.ToString(), "CROSSFALL", i,
                        leaderLayer, textLayer, textHeight);
                }
                transaction.Commit();
            }
            return created;
        }

        private static int AppendLeaderSet(Database database, Transaction transaction, BlockTableRecord space,
            Point3d high, Point3d low, double slope, SourceSettings settings, string sourceHandle,
            string otherHandle, string role, int index, ObjectId leaderLayer, ObjectId textLayer, double textHeight)
        {
            Point3d mid = new Point3d((high.X + low.X) * 0.5, (high.Y + low.Y) * 0.5, (high.Z + low.Z) * 0.5);
            Vector3d direction = new Vector3d(low.X - high.X, low.Y - high.Y, 0.0);
            if (direction.Length <= Tol) return 0;
            direction = direction.GetNormal();

            var text = new MText();
            text.SetDatabaseDefaults(database);
            text.LayerId = textLayer;
            text.ColorIndex = (short)settings.Colour;
            text.Location = mid;
            text.TextHeight = Math.Max(textHeight, 0.001);
            text.Contents = slope.ToString("0.0#", CultureInfo.InvariantCulture) + "%";
            text.Rotation = Math.Atan2(direction.Y, direction.X);
            text.Attachment = AttachmentPoint.MiddleCenter;
            PaperAnnotationScale.SetAnnotative(text);
            ObjectId textId = space.AppendEntity(text);
            transaction.AddNewlyCreatedDBObject(text, true);
            WriteChild(text, transaction, sourceHandle, otherHandle, role, index);

            var leader = new Leader();
            leader.SetDatabaseDefaults(database);
            leader.LayerId = leaderLayer;
            leader.ColorIndex = (short)settings.Colour;
            leader.HasArrowHead = true;
            // In an AutoCAD Leader the first vertex owns the arrow head. Put it at
            // the low point so every arrow points in the downhill direction.
            leader.AppendVertex(low);
            leader.AppendVertex(high);
            PaperAnnotationScale.SetAnnotative(leader);
            space.AppendEntity(leader);
            transaction.AddNewlyCreatedDBObject(leader, true);
            WriteChild(leader, transaction, sourceHandle, otherHandle, role, index);
            return 2;
        }

        private static bool TryCreateHatchStrip(Document document, ObjectId sourceId, double offset, ProductionSettingsDialogModel settings)
        {
            try
            {
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    Entity sourceEntity = transaction.GetObject(sourceId, OpenMode.ForRead, false) as Entity;
                    using (Polyline source = BuildRoadPolyline(sourceEntity, settings.Double("Sample", 5.0)))
                    {
                        if (source == null || source.NumberOfVertices < 2 || source.Closed) return false;
                        DBObjectCollection offsets = null;
                        try
                        {
                            offsets = source.GetOffsetCurves(offset);
                            Polyline other = offsets.Cast<DBObject>().OfType<Polyline>().FirstOrDefault();
                            if (other == null) return false;
                            List<Point3d> first = SampleCurve(source, 80);
                            List<Point3d> second = SampleCurve(other, 80);
                            if (first.Count < 2 || second.Count < 2) return false;
                            second.Reverse();
                            var boundary = new Polyline(first.Count + second.Count);
                            boundary.SetDatabaseDefaults(document.Database);
                            ObjectId boundaryLayer = EnsureLayer(document.Database, transaction, HatchBoundaryLayer);
                            ObjectId hatchLayer = EnsureLayer(document.Database, transaction, HatchLayer);
                            boundary.LayerId = boundaryLayer;
                            int vertex = 0;
                            foreach (Point3d point in first.Concat(second))
                                boundary.AddVertexAt(vertex++, new Point2d(point.X, point.Y), 0.0, 0.0, 0.0);
                            boundary.Closed = true;
                            BlockTableRecord space = transaction.GetObject(document.Database.CurrentSpaceId, OpenMode.ForWrite, false) as BlockTableRecord;
                            ObjectId boundaryId = space.AppendEntity(boundary);
                            transaction.AddNewlyCreatedDBObject(boundary, true);

                            var hatch = new Hatch();
                            hatch.SetDatabaseDefaults(document.Database);
                            hatch.LayerId = hatchLayer;
                            int colour = ColourIndex(settings.Text("Colour"), true);
                            if (colour == 256) hatch.ColorIndex = 256;
                            else hatch.Color = Color.FromColorIndex(ColorMethod.ByAci, (short)colour);
                            space.AppendEntity(hatch);
                            transaction.AddNewlyCreatedDBObject(hatch, true);
                            string pattern = settings.Text("Pattern");
                            hatch.SetHatchPattern(HatchPatternType.PreDefined,
                                string.IsNullOrWhiteSpace(pattern) ? "ANSI31" : pattern.Trim());
                            if (!string.Equals(pattern, "SOLID", StringComparison.OrdinalIgnoreCase))
                                hatch.PatternScale = Math.Max(settings.Double("Scale", 1.0), 0.001);
                            // Non-associative output avoids Civil 3D reactor/re-entry faults;
                            // the strip geometry is fully built before the Hatch is evaluated.
                            hatch.Associative = false;
                            var loop = new ObjectIdCollection { boundaryId };
                            hatch.AppendLoop(HatchLoopTypes.Outermost, loop);
                            hatch.EvaluateHatch(true);
                            transaction.Commit();
                            return true;
                        }
                        finally
                        {
                            if (offsets != null)
                                foreach (DBObject value in offsets)
                                    try { if (value != null && value.Database == null) value.Dispose(); } catch { }
                        }
                    }
                }
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage("\nRoad hatch strip left unchanged/rolled back: {0}", exception.Message);
                return false;
            }
        }

        private static Polyline BuildRoadPolyline(Entity entity, double sampleInterval)
        {
            Polyline source = entity as Polyline;
            if (source != null)
            {
                var copy = new Polyline(source.NumberOfVertices);
                copy.Elevation = source.Elevation;
                for (int i = 0; i < source.NumberOfVertices; i++)
                    copy.AddVertexAt(i, source.GetPoint2dAt(i), source.GetBulgeAt(i), 0.0, 0.0);
                copy.Closed = source.Closed;
                return copy;
            }
            CivilAlignment alignment = entity as CivilAlignment;
            if (alignment == null) return null;
            try
            {
                double start = alignment.StartingStation;
                double end = alignment.EndingStation;
                if (!(end > start)) return null;
                int count = Math.Max(2, (int)Math.Ceiling((end - start) / Math.Max(sampleInterval, 0.25)));
                var output = new Polyline(count + 1);
                for (int i = 0; i <= count; i++)
                {
                    double station = start + (end - start) * i / count;
                    double easting = 0.0, northing = 0.0;
                    alignment.PointLocation(station, 0.0, ref easting, ref northing);
                    output.AddVertexAt(i, new Point2d(easting, northing), 0.0, 0.0, 0.0);
                }
                return output;
            }
            catch { return null; }
        }

        private static List<Point3d> SampleCurve(Curve curve, int count)
        {
            var result = new List<Point3d>();
            for (int i = 0; i <= Math.Max(2, count); i++)
            {
                double p = curve.StartParam + (curve.EndParam - curve.StartParam) * i / Math.Max(2.0, count);
                try { result.Add(curve.GetPointAtParameter(p)); } catch { }
            }
            return result;
        }

        private static void WriteSourceSettings(Document document, ObjectId sourceId, string mode, double spacing, double paperText, double paperArrow, int colour)
        {
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                Entity source = transaction.GetObject(sourceId, OpenMode.ForWrite, false) as Entity;
                if (source == null) return;
                WriteRecord(source, transaction, SourceKey, new[]
                {
                    new TypedValue((int)DxfCode.Text, "V1"),
                    new TypedValue((int)DxfCode.Text, mode),
                    new TypedValue((int)DxfCode.Real, spacing),
                    new TypedValue((int)DxfCode.Real, paperText),
                    new TypedValue((int)DxfCode.Real, paperArrow),
                    new TypedValue((int)DxfCode.Int32, colour)
                });
                transaction.Commit();
            }
        }

        private static void WriteCrossfallSettings(Document document, ObjectId firstId, ObjectId secondId, double spacing, double paperText, double paperArrow, int colour)
        {
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                Entity first = transaction.GetObject(firstId, OpenMode.ForWrite, false) as Entity;
                Entity second = transaction.GetObject(secondId, OpenMode.ForWrite, false) as Entity;
                if (first == null || second == null) return;
                WriteCrossfall(first, transaction, second.Handle.ToString(), spacing, paperText, paperArrow, colour);
                WriteCrossfall(second, transaction, first.Handle.ToString(), spacing, paperText, paperArrow, colour);
                transaction.Commit();
            }
        }

        private static void WriteCrossfall(Entity source, Transaction transaction, string otherHandle, double spacing, double paperText, double paperArrow, int colour)
        {
            WriteRecord(source, transaction, CrossfallKey, new[]
            {
                new TypedValue((int)DxfCode.Text, "V1"),
                new TypedValue((int)DxfCode.Text, otherHandle),
                new TypedValue((int)DxfCode.Real, spacing),
                new TypedValue((int)DxfCode.Real, paperText),
                new TypedValue((int)DxfCode.Real, paperArrow),
                new TypedValue((int)DxfCode.Int32, colour)
            });
        }

        private static bool TryReadSourceSettings(DBObject source, Transaction transaction, out SourceSettings settings)
        {
            settings = new SourceSettings();
            TypedValue[] values;
            if (!TryReadRecord(source, transaction, SourceKey, out values) || values.Length < 6) return false;
            try
            {
                settings.Mode = Convert.ToString(values[1].Value, CultureInfo.InvariantCulture);
                settings.Spacing = Convert.ToDouble(values[2].Value, CultureInfo.InvariantCulture);
                settings.PaperText = Convert.ToDouble(values[3].Value, CultureInfo.InvariantCulture);
                settings.PaperArrow = Convert.ToDouble(values[4].Value, CultureInfo.InvariantCulture);
                settings.Colour = Convert.ToInt32(values[5].Value, CultureInfo.InvariantCulture);
                return !string.IsNullOrWhiteSpace(settings.Mode);
            }
            catch { return false; }
        }

        private static bool TryReadCrossfall(DBObject source, Transaction transaction, out CrossfallSettings settings)
        {
            settings = new CrossfallSettings();
            TypedValue[] values;
            if (!TryReadRecord(source, transaction, CrossfallKey, out values) || values.Length < 6) return false;
            try
            {
                settings.OtherHandle = Convert.ToString(values[1].Value, CultureInfo.InvariantCulture);
                settings.Spacing = Convert.ToDouble(values[2].Value, CultureInfo.InvariantCulture);
                settings.PaperText = Convert.ToDouble(values[3].Value, CultureInfo.InvariantCulture);
                settings.PaperArrow = Convert.ToDouble(values[4].Value, CultureInfo.InvariantCulture);
                settings.Colour = Convert.ToInt32(values[5].Value, CultureInfo.InvariantCulture);
                return !string.IsNullOrWhiteSpace(settings.OtherHandle);
            }
            catch { return false; }
        }

        private static void WriteChild(Entity child, Transaction transaction, string sourceHandle, string otherHandle, string role, int index)
        {
            WriteRecord(child, transaction, ChildKey, new[]
            {
                new TypedValue((int)DxfCode.Text, sourceHandle ?? string.Empty),
                new TypedValue((int)DxfCode.Text, otherHandle ?? string.Empty),
                new TypedValue((int)DxfCode.Text, role ?? string.Empty),
                new TypedValue((int)DxfCode.Int32, index)
            });
        }

        private static void EraseChildren(Document document, string sourceHandle, string otherHandle)
        {
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord model = transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(document.Database), OpenMode.ForRead, false) as BlockTableRecord;
                if (model == null) return;
                foreach (ObjectId id in model.Cast<ObjectId>().ToList())
                {
                    Entity child = null;
                    try { child = transaction.GetObject(id, OpenMode.ForRead, false) as Entity; } catch { }
                    TypedValue[] values;
                    bool matchNew = child != null && TryReadRecord(child, transaction, ChildKey, out values) && values.Length >= 2 &&
                        string.Equals(Convert.ToString(values[0].Value, CultureInfo.InvariantCulture), sourceHandle, StringComparison.OrdinalIgnoreCase) &&
                        (string.IsNullOrEmpty(otherHandle) || string.Equals(Convert.ToString(values[1].Value, CultureInfo.InvariantCulture), otherHandle, StringComparison.OrdinalIgnoreCase));
                    bool matchOld = child != null && string.IsNullOrEmpty(otherHandle) && TryReadRecord(child, transaction, OldSlopeChildKey, out values) && values.Length > 0 &&
                        string.Equals(Convert.ToString(values[0].Value, CultureInfo.InvariantCulture), sourceHandle, StringComparison.OrdinalIgnoreCase);
                    if (!matchNew && !matchOld) continue;
                    child.UpgradeOpen();
                    child.Erase();
                }
                transaction.Commit();
            }
        }

        private static void WriteRecord(DBObject owner, Transaction transaction, string key, IEnumerable<TypedValue> values)
        {
            if (owner.ExtensionDictionary.IsNull) owner.CreateExtensionDictionary();
            DBDictionary dictionary = transaction.GetObject(owner.ExtensionDictionary, OpenMode.ForWrite, false) as DBDictionary;
            if (dictionary == null) return;
            Xrecord record;
            if (dictionary.Contains(key)) record = transaction.GetObject(dictionary.GetAt(key), OpenMode.ForWrite, false) as Xrecord;
            else
            {
                record = new Xrecord();
                dictionary.SetAt(key, record);
                transaction.AddNewlyCreatedDBObject(record, true);
            }
            if (record != null) record.Data = new ResultBuffer(values.ToArray());
        }

        private static bool TryReadRecord(DBObject owner, Transaction transaction, string key, out TypedValue[] values)
        {
            values = new TypedValue[0];
            if (owner == null || owner.ExtensionDictionary.IsNull) return false;
            try
            {
                DBDictionary dictionary = transaction.GetObject(owner.ExtensionDictionary, OpenMode.ForRead, false) as DBDictionary;
                if (dictionary == null || !dictionary.Contains(key)) return false;
                Xrecord record = transaction.GetObject(dictionary.GetAt(key), OpenMode.ForRead, false) as Xrecord;
                values = record == null || record.Data == null ? new TypedValue[0] : record.Data.AsArray();
                return values.Length > 0;
            }
            catch { return false; }
        }

        private static ObjectId ResolveHandle(Database database, string text)
        {
            long value;
            if (database == null || string.IsNullOrWhiteSpace(text) ||
                !long.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)) return ObjectId.Null;
            try { return database.GetObjectId(false, new Handle(value), 0); } catch { return ObjectId.Null; }
        }

        private static ObjectId EnsureLayer(Database database, Transaction transaction, string name)
        {
            LayerTable table = transaction.GetObject(database.LayerTableId, OpenMode.ForRead, false) as LayerTable;
            if (table.Has(name)) return table[name];
            table.UpgradeOpen();
            var layer = new LayerTableRecord { Name = name };
            ObjectId id = table.Add(layer);
            transaction.AddNewlyCreatedDBObject(layer, true);
            return id;
        }

        private static bool TryEndpoints(Entity entity, out Point3d start, out Point3d end)
        {
            start = Point3d.Origin; end = Point3d.Origin;
            Curve curve = entity as Curve;
            if (curve != null)
            {
                try { start = curve.StartPoint; end = curve.EndPoint; return true; } catch { }
            }
            CivilFeatureLine feature = entity as CivilFeatureLine;
            if (feature != null)
            {
                try
                {
                    Point3dCollection points = feature.GetPoints(FeatureLinePointType.PIPoint);
                    if (points != null && points.Count >= 2) { start = points[0]; end = points[points.Count - 1]; return true; }
                }
                catch { }
            }
            return false;
        }

        private static double CurveLength(Curve curve)
        {
            try { return Math.Abs(curve.GetDistanceAtParameter(curve.EndParam) - curve.GetDistanceAtParameter(curve.StartParam)); }
            catch { return 0.0; }
        }

        private static PromptSelectionResult Select(Editor editor, string message)
        {
            PromptSelectionResult implied = editor.SelectImplied();
            if (implied.Status == PromptStatus.OK && implied.Value != null && implied.Value.Count > 0)
            {
                editor.SetImpliedSelection(new ObjectId[0]);
                return implied;
            }
            return editor.GetSelection(new PromptSelectionOptions { MessageForAdding = message, AllowDuplicates = false, RejectObjectsFromNonCurrentSpace = true });
        }

        private static Document Active() { return AcApplication.DocumentManager.MdiActiveDocument; }
        private static double PlanDistance(Point3d a, Point3d b) { double dx = a.X - b.X, dy = a.Y - b.Y; return Math.Sqrt(dx * dx + dy * dy); }
        private static string PairKey(string a, string b) { return string.Compare(a, b, StringComparison.OrdinalIgnoreCase) <= 0 ? a + "|" + b : b + "|" + a; }

        private static IEnumerable<string> ColourNames(bool includeByLayer = false)
        {
            var values = new List<string>();
            if (includeByLayer) values.Add("ByLayer");
            values.AddRange(new[] { "Red", "Yellow", "Green", "Cyan", "Blue", "Magenta", "White/Grey" });
            return values;
        }

        private static int ColourIndex(string name, bool allowByLayer = false)
        {
            if (allowByLayer && string.Equals(name, "ByLayer", StringComparison.OrdinalIgnoreCase)) return 256;
            if (string.Equals(name, "Red", StringComparison.OrdinalIgnoreCase)) return 1;
            if (string.Equals(name, "Yellow", StringComparison.OrdinalIgnoreCase)) return 2;
            if (string.Equals(name, "Green", StringComparison.OrdinalIgnoreCase)) return 3;
            if (string.Equals(name, "Cyan", StringComparison.OrdinalIgnoreCase)) return 4;
            if (string.Equals(name, "Blue", StringComparison.OrdinalIgnoreCase)) return 5;
            if (string.Equals(name, "Magenta", StringComparison.OrdinalIgnoreCase)) return 6;
            return 7;
        }

        private sealed class SourceSettings { public string Mode; public double Spacing, PaperText, PaperArrow; public int Colour; }
        private sealed class CrossfallSettings { public string OtherHandle; public double Spacing, PaperText, PaperArrow; public int Colour; }
    }

    /// <summary>
    /// Deferred dynamic refresh. ObjectModified only marks source handles dirty;
    /// actual Civil 3D reads/writes happen at Idle after the command stack is empty.
    /// The active annotation scale is polled as well, so changing CANNOSCALE rebuilds
    /// leader geometry and annotative text without requiring a manual refresh.
    /// </summary>
    internal static class August27DynamicSlopeManager
    {
        private sealed class State
        {
            public readonly HashSet<string> Dirty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public bool Busy;
            public bool Pending;
            public string AnnotationScale;
        }

        private static readonly Dictionary<Document, State> States = new Dictionary<Document, State>();
        private static bool _initialized;

        internal static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;
            AcApplication.DocumentManager.DocumentActivated += OnDocumentActivated;
            AcApplication.DocumentManager.DocumentCreated += OnDocumentActivated;
            AcApplication.DocumentManager.DocumentToBeDestroyed += OnDocumentDestroyed;
            AcApplication.Idle += OnIdle;
            RegisterDocument(AcApplication.DocumentManager.MdiActiveDocument);
        }

        internal static void Terminate()
        {
            if (!_initialized) return;
            _initialized = false;
            AcApplication.DocumentManager.DocumentActivated -= OnDocumentActivated;
            AcApplication.DocumentManager.DocumentCreated -= OnDocumentActivated;
            AcApplication.DocumentManager.DocumentToBeDestroyed -= OnDocumentDestroyed;
            AcApplication.Idle -= OnIdle;
            foreach (Document document in States.Keys.ToList()) Detach(document);
            States.Clear();
        }

        internal static void RegisterDocument(Document document)
        {
            if (document == null || document.Database == null) return;
            State state;
            if (States.TryGetValue(document, out state)) return;
            state = new State { AnnotationScale = CurrentScale() };
            States.Add(document, state);
            document.Database.ObjectModified += OnObjectModified;
            document.CommandEnded += OnCommandEnded;
        }

        private static void OnDocumentActivated(object sender, DocumentCollectionEventArgs args) { RegisterDocument(args == null ? null : args.Document); }
        private static void OnDocumentDestroyed(object sender, DocumentCollectionEventArgs args) { if (args != null) Detach(args.Document); }

        private static void Detach(Document document)
        {
            if (document == null) return;
            try { document.Database.ObjectModified -= OnObjectModified; document.CommandEnded -= OnCommandEnded; } catch { }
            States.Remove(document);
        }

        private static void OnObjectModified(object sender, ObjectEventArgs args)
        {
            if (args == null || args.DBObject == null || args.DBObject.ObjectId.IsNull) return;
            foreach (KeyValuePair<Document, State> pair in States)
            {
                if (pair.Value.Busy || pair.Key.Database != sender) continue;
                // Do not open the DB from inside ObjectModified. Mark the handle and
                // validate whether it owns a dynamic source record later at Idle.
                try { pair.Value.Dirty.Add(args.DBObject.Handle.ToString()); pair.Value.Pending = true; } catch { }
            }
        }

        private static void OnCommandEnded(object sender, CommandEventArgs args)
        {
            Document document = sender as Document;
            State state;
            if (document != null && States.TryGetValue(document, out state) && state.Dirty.Count > 0) state.Pending = true;
        }

        private static void OnIdle(object sender, EventArgs args)
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            State state;
            if (document == null || !States.TryGetValue(document, out state) || state.Busy) return;
            string scale = CurrentScale();
            bool scaleChanged = !string.Equals(scale, state.AnnotationScale, StringComparison.OrdinalIgnoreCase);
            if (scaleChanged) { state.AnnotationScale = scale; state.Pending = true; }
            if (!state.Pending && !scaleChanged) return;
            string commands = Convert.ToString(AcApplication.GetSystemVariable("CMDNAMES"), CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(commands)) return;

            string[] dirty = state.Dirty.ToArray();
            state.Dirty.Clear();
            state.Pending = false;
            state.Busy = true;
            try
            {
                August27DynamicSlopeGridHatchCommands.RefreshChanged(document, dirty, scaleChanged);
                August21DisplayRefresh.Flush(document);
            }
            catch { }
            finally { state.Busy = false; }
        }

        private static string CurrentScale()
        {
            try { return Convert.ToString(AcApplication.GetSystemVariable("CANNOSCALE"), CultureInfo.InvariantCulture) ?? string.Empty; }
            catch { return string.Empty; }
        }
    }
}

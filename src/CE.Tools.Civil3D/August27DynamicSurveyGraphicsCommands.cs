using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
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

[assembly: CommandClass(typeof(CETools.Civil3D.August27DynamicSurveyGraphicsCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// August 27 field additions.  Existing August24 command front doors are routed
    /// here by the final build boundary; the two new Survey commands are registered
    /// directly by this class.
    /// </summary>
    public sealed class August27DynamicSurveyGraphicsCommands
    {
        [CommandMethod("CE_TOOLS", "CE_FEATURELINECROSSSLOPE", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void FeatureLineCrossSlope()
        {
            August27DynamicSurveyGraphicsRuntime.FeatureLineCrossSlope(AcApplication.DocumentManager.MdiActiveDocument);
        }

        [CommandMethod("CE_TOOLS", "CE_CONNECTSELECTEDENDPOINTS", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void ConnectSelectedEndpoints()
        {
            August27DynamicSurveyGraphicsRuntime.ConnectSelectedEndpoints(AcApplication.DocumentManager.MdiActiveDocument);
        }
    }

    internal static class August27DynamicSurveyGraphicsRuntime
    {
        private const double Tol = 0.000001;
        private const string LegacySlopeLinkKey = "CE_FIELD_SLOPE_LINK";
        private const string RoadHatchLayer = "CE-ROAD-HATCH";
        private const string RoadHatchBoundaryLayer = "CE-ROAD-HATCH-BOUNDARY";

        internal enum DynamicKind
        {
            FeatureLine,
            Surface,
            CrossSlope
        }

        internal sealed class DynamicConfig
        {
            internal Guid Id = Guid.NewGuid();
            internal DynamicKind Kind;
            internal readonly List<ObjectId> Sources = new List<ObjectId>();
            internal readonly List<ObjectId> Children = new List<ObjectId>();
            internal double Spacing;
            internal double ArrowPaper;
            internal double TextPaper;
            internal int Colour;
            internal int Precision;
        }

        internal static void FeatureLineSlopeArrows(Document document)
        {
            if (document == null) return;
            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Dynamic Feature-Line Slope Leaders",
                "Create true Leader arrows and annotative slope values on multiple feature lines. Geometry/elevation edits and annotation-scale changes rebuild the linked annotations automatically.");
            settings.AddPositiveDouble("Arrow", "01 Annotation", "Leader length (paper mm)", 8.0,
                "Fixed plotted leader length. The model length is recalculated when annotation scale changes.");
            settings.AddPaperHeight("TextHeight", "01 Annotation", "Paper text height", 2.5,
                "Absolute annotative paper text height.");
            settings.AddPositiveInteger("Precision", "01 Annotation", "Slope decimals", 2,
                "Number of decimals displayed in the slope percentage.");
            settings.AddPositiveInteger("Colour", "01 Annotation", "Colour index", 2,
                "AutoCAD colour index 1-255.");
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;

            PromptSelectionResult selection = Select(document.Editor,
                "\nSelect multiple Civil 3D feature lines for dynamic slope leaders: ");
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;

            var ids = new List<ObjectId>();
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in selection.Value.GetObjectIds().Distinct())
                {
                    try
                    {
                        CivilFeatureLine featureLine = transaction.GetObject(id, OpenMode.ForRead, false) as CivilFeatureLine;
                        if (featureLine != null && !featureLine.IsReferenceObject) ids.Add(id);
                    }
                    catch { }
                }
            }
            if (ids.Count == 0) return;

            EraseLegacySlopeChildren(document, ids);
            var config = new DynamicConfig
            {
                Kind = DynamicKind.FeatureLine,
                ArrowPaper = settings.Double("Arrow", 8.0),
                TextPaper = settings.Double("TextHeight", 2.5),
                Colour = ClampColour(settings.Integer("Colour", 2)),
                Precision = Math.Max(0, Math.Min(6, settings.Integer("Precision", 2)))
            };
            config.Sources.AddRange(ids);
            int created = Rebuild(document, config);
            August27DynamicSurveyGraphicsManager.Watch(document, config);
            August21DisplayRefresh.Flush(document);
            document.Editor.WriteMessage(
                "\nCE_FEATURELINESLOPEARROWS complete. Dynamic Leader arrows/text created={0}; watched feature lines={1}.",
                created, ids.Count);
        }

        internal static void SurfaceSlopeArrows(Document document)
        {
            if (document == null) return;
            var options = new PromptEntityOptions("\nSelect Civil 3D surface for dynamic slope leaders: ");
            options.SetRejectMessage("\nSelect a Civil 3D surface.");
            options.AddAllowedClass(typeof(CivilSurface), false);
            PromptEntityResult selected = document.Editor.GetEntity(options);
            if (selected.Status != PromptStatus.OK) return;

            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Dynamic Surface Slope Leaders",
                "Sample the surface, create true downhill Leader arrows and annotative percentages, and rebuild them after surface or annotation-scale changes.");
            settings.AddPositiveDouble("Spacing", "01 Sampling", "Grid spacing", 20.0,
                "Plan spacing between sampled slope arrows.");
            settings.AddPositiveDouble("Arrow", "02 Annotation", "Leader length (paper mm)", 8.0,
                "Fixed plotted leader length recalculated at the current annotation scale.");
            settings.AddPaperHeight("TextHeight", "02 Annotation", "Paper text height", 2.5,
                "Absolute annotative paper text height.");
            settings.AddPositiveInteger("Precision", "02 Annotation", "Slope decimals", 2,
                "Number of decimals displayed in the slope percentage.");
            settings.AddPositiveInteger("Colour", "02 Annotation", "Colour index", 2,
                "AutoCAD colour index 1-255.");
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;

            var config = new DynamicConfig
            {
                Kind = DynamicKind.Surface,
                Spacing = Math.Max(settings.Double("Spacing", 20.0), 0.01),
                ArrowPaper = settings.Double("Arrow", 8.0),
                TextPaper = settings.Double("TextHeight", 2.5),
                Colour = ClampColour(settings.Integer("Colour", 2)),
                Precision = Math.Max(0, Math.Min(6, settings.Integer("Precision", 2)))
            };
            config.Sources.Add(selected.ObjectId);
            int created = Rebuild(document, config);
            August27DynamicSurveyGraphicsManager.Watch(document, config);
            August21DisplayRefresh.Flush(document);
            document.Editor.WriteMessage(
                "\nCE_SURFACESLOPEARROWS complete. Dynamic surface Leader/text entities created={0}.", created);
        }

        internal static void FeatureLineCrossSlope(Document document)
        {
            if (document == null) return;
            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Dynamic Slope Between Two Feature Lines",
                "Select exactly two feature lines. CE Tools samples corresponding positions and draws true Leader arrows from high to low with dynamic annotative cross-slope values.");
            settings.AddPositiveDouble("Spacing", "01 Sampling", "Maximum sample spacing", 10.0,
                "Approximate plan spacing along the shorter feature line.");
            settings.AddPositiveDouble("Arrow", "02 Annotation", "Leader inset (paper mm)", 1.5,
                "Paper-space inset from each sampled feature line endpoint.");
            settings.AddPaperHeight("TextHeight", "02 Annotation", "Paper text height", 2.5,
                "Absolute annotative paper text height.");
            settings.AddPositiveInteger("Precision", "02 Annotation", "Slope decimals", 2,
                "Number of decimals displayed in the cross-slope percentage.");
            settings.AddPositiveInteger("Colour", "02 Annotation", "Colour index", 2,
                "AutoCAD colour index 1-255.");
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;

            PromptSelectionResult selection = Select(document.Editor,
                "\nSelect exactly two Civil 3D feature lines: ");
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;
            var ids = new List<ObjectId>();
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in selection.Value.GetObjectIds().Distinct())
                {
                    try
                    {
                        CivilFeatureLine featureLine = transaction.GetObject(id, OpenMode.ForRead, false) as CivilFeatureLine;
                        if (featureLine != null && !featureLine.IsReferenceObject) ids.Add(id);
                    }
                    catch { }
                }
            }
            if (ids.Count != 2)
            {
                document.Editor.WriteMessage("\nCE_FEATURELINECROSSSLOPE requires exactly two editable feature lines.");
                return;
            }

            var config = new DynamicConfig
            {
                Kind = DynamicKind.CrossSlope,
                Spacing = Math.Max(settings.Double("Spacing", 10.0), 0.01),
                ArrowPaper = settings.Double("Arrow", 1.5),
                TextPaper = settings.Double("TextHeight", 2.5),
                Colour = ClampColour(settings.Integer("Colour", 2)),
                Precision = Math.Max(0, Math.Min(6, settings.Integer("Precision", 2)))
            };
            config.Sources.AddRange(ids);
            int created = Rebuild(document, config);
            August27DynamicSurveyGraphicsManager.Watch(document, config);
            August21DisplayRefresh.Flush(document);
            document.Editor.WriteMessage(
                "\nCE_FEATURELINECROSSSLOPE complete. Dynamic cross-slope Leader/text entities created={0}.", created);
        }

        internal static void RefreshAll(Document document)
        {
            if (document == null) return;
            int refreshed = August27DynamicSurveyGraphicsManager.RefreshAll(document);
            August21DisplayRefresh.Flush(document);
            document.Editor.WriteMessage(
                "\nCE_SLOPEARROWSREFRESH complete. Dynamic slope annotation entities rebuilt={0}.", refreshed);
        }

        internal static void ConnectSelectedEndpoints(Document document)
        {
            if (document == null) return;
            PromptSelectionResult selection = Select(document.Editor,
                "\nSelect polylines and/or Civil 3D feature lines whose endpoints must be connected: ");
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;
            ObjectId[] ids = selection.Value.GetObjectIds().Distinct().ToArray();
            if (ids.Length < 2)
            {
                document.Editor.WriteMessage("\nSelect at least two source objects.");
                return;
            }

            var pairs = new List<Tuple<Point3d, Point3d>>();
            ObjectId layerId = ObjectId.Null;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids)
                {
                    Entity entity = null;
                    try { entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity; } catch { }
                    Point3d first;
                    Point3d second;
                    if (entity == null || !TryEndpoints(entity, out first, out second)) continue;
                    if (layerId.IsNull) layerId = entity.LayerId;
                    pairs.Add(Tuple.Create(first, second));
                }
            }
            if (pairs.Count < 2)
            {
                document.Editor.WriteMessage("\nNot enough supported source endpoints were found.");
                return;
            }

            var chosen = new List<Point3d> { pairs[0].Item1 };
            for (int index = 1; index < pairs.Count; index++)
            {
                Point3d previous = chosen[chosen.Count - 1];
                chosen.Add(PlanDistance(previous, pairs[index].Item1) <= PlanDistance(previous, pairs[index].Item2)
                    ? pairs[index].Item1 : pairs[index].Item2);
            }

            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = transaction.GetObject(document.Database.CurrentSpaceId, OpenMode.ForWrite, false) as BlockTableRecord;
                var output = new Polyline(chosen.Count);
                output.SetDatabaseDefaults(document.Database);
                if (!layerId.IsNull) output.LayerId = layerId;
                output.Elevation = chosen[0].Z;
                for (int index = 0; index < chosen.Count; index++)
                    output.AddVertexAt(index, new Point2d(chosen[index].X, chosen[index].Y), 0.0, 0.0, 0.0);
                space.AppendEntity(output);
                transaction.AddNewlyCreatedDBObject(output, true);
                transaction.Commit();
            }
            August21DisplayRefresh.Flush(document);
            document.Editor.WriteMessage(
                "\nCE_CONNECTSELECTEDENDPOINTS complete. One polyline connected {0} selected source endpoints.", chosen.Count);
        }

        internal static void RoadSideHatch(Document document)
        {
            if (document == null) return;
            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Road Side Hatch (Safe)",
                "Create isolated hatch strips per selected road source. Invalid strips are skipped without rolling back or destabilising the rest of the selection.");
            settings.AddPositiveDouble("Distance", "01 Geometry", "Hatch width / offset distance", 5.0,
                "Width of the hatch strip from the selected road line.");
            settings.AddChoice("Side", "01 Geometry", "Side", "Both",
                "Hatch the left side, right side or both sides.", new[] { "Left", "Right", "Both" });
            settings.AddChoice("Pattern", "02 Hatch", "Hatch pattern", "ANSI31",
                "Select a safe built-in AutoCAD hatch pattern.",
                new[] { "SOLID", "ANSI31", "ANSI32", "ANSI33", "ANSI34", "ANSI35", "ANSI36", "ANSI37", "ANSI38" });
            settings.AddChoice("Colour", "02 Hatch", "Hatch colour", "By layer",
                "Select the hatch colour without typing an ACI number.",
                new[] { "By layer", "Red", "Yellow", "Green", "Cyan", "Blue", "Magenta", "White" });
            settings.AddPositiveDouble("Scale", "02 Hatch", "Hatch scale", 1.0,
                "Pattern scale for non-solid hatches.");
            settings.AddPositiveDouble("Sample", "03 Alignments", "Alignment sample interval", 5.0,
                "Plan sampling interval when a Civil 3D alignment is selected.");
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;

            PromptSelectionResult selection = Select(document.Editor,
                "\nSelect multiple road polylines and/or Civil 3D alignments: ");
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;

            double distance = Math.Max(settings.Double("Distance", 5.0), Tol);
            string side = settings.Text("Side");
            string pattern = settings.Text("Pattern");
            double scale = Math.Max(settings.Double("Scale", 1.0), 0.001);
            int colour = ColourFromName(settings.Text("Colour"));
            double sample = Math.Max(settings.Double("Sample", 5.0), 0.25);
            int created = 0;
            int skipped = 0;

            foreach (ObjectId id in selection.Value.GetObjectIds().Distinct())
            {
                var offsets = new List<double>();
                if (string.Equals(side, "Left", StringComparison.OrdinalIgnoreCase) || string.Equals(side, "Both", StringComparison.OrdinalIgnoreCase)) offsets.Add(distance);
                if (string.Equals(side, "Right", StringComparison.OrdinalIgnoreCase) || string.Equals(side, "Both", StringComparison.OrdinalIgnoreCase)) offsets.Add(-distance);
                foreach (double offset in offsets)
                {
                    if (TryCreateHatchStripIsolated(document, id, offset, pattern, scale, colour, sample)) created++;
                    else skipped++;
                }
            }
            August21DisplayRefresh.Flush(document);
            document.Editor.WriteMessage(
                "\nCE_ROADHATCHSIDES complete. Safe hatch strips created={0}; skipped={1}. No selected road source was modified.",
                created, skipped);
        }

        internal static int Rebuild(Document document, DynamicConfig config)
        {
            if (document == null || config == null) return 0;
            switch (config.Kind)
            {
                case DynamicKind.FeatureLine: return RebuildFeatureLine(document, config);
                case DynamicKind.Surface: return RebuildSurface(document, config);
                case DynamicKind.CrossSlope: return RebuildCrossSlope(document, config);
                default: return 0;
            }
        }

        private static int RebuildFeatureLine(Document document, DynamicConfig config)
        {
            int created = 0;
            double arrowLength = PaperAnnotationScale.ModelDistance(document.Database, Math.Max(config.ArrowPaper, 0.1));
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                EraseChildren(transaction, config);
                BlockTableRecord space = transaction.GetObject(document.Database.CurrentSpaceId, OpenMode.ForWrite, false) as BlockTableRecord;
                foreach (ObjectId id in config.Sources.Distinct())
                {
                    CivilFeatureLine source = null;
                    try { source = transaction.GetObject(id, OpenMode.ForRead, false) as CivilFeatureLine; } catch { }
                    if (source == null || source.IsReferenceObject) continue;
                    Point3dCollection points = source.GetPoints(FeatureLinePointType.PIPoint);
                    for (int index = 0; index + 1 < points.Count; index++)
                    {
                        Point3d a = points[index];
                        Point3d b = points[index + 1];
                        double run = PlanDistance(a, b);
                        if (run <= Tol) continue;
                        double grade = (b.Z - a.Z) / run * 100.0;
                        Point3d high = a.Z >= b.Z ? a : b;
                        Point3d low = a.Z >= b.Z ? b : a;
                        Vector3d direction = PlanUnit(low - high);
                        if (direction.Length <= Tol) continue;
                        Point3d mid = Mid(a, b);
                        double usable = Math.Min(arrowLength, run * 0.75);
                        Point3d tip = mid + direction * (usable * 0.5);
                        Point3d tail = mid - direction * (usable * 0.5);
                        AppendLeaderAndText(document.Database, transaction, space, config, tip, tail, grade, source.LayerId, ref created);
                    }
                }
                transaction.Commit();
            }
            return created;
        }

        private static int RebuildCrossSlope(Document document, DynamicConfig config)
        {
            if (config.Sources.Count != 2) return 0;
            int created = 0;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                EraseChildren(transaction, config);
                CivilFeatureLine first = transaction.GetObject(config.Sources[0], OpenMode.ForRead, false) as CivilFeatureLine;
                CivilFeatureLine second = transaction.GetObject(config.Sources[1], OpenMode.ForRead, false) as CivilFeatureLine;
                if (first == null || second == null) { transaction.Commit(); return 0; }
                List<Point3d> a = ToList(first.GetPoints(FeatureLinePointType.PIPoint));
                List<Point3d> b = ToList(second.GetPoints(FeatureLinePointType.PIPoint));
                if (a.Count < 2 || b.Count < 2) { transaction.Commit(); return 0; }
                if (PlanDistance(a[0], b[b.Count - 1]) + PlanDistance(a[a.Count - 1], b[0]) <
                    PlanDistance(a[0], b[0]) + PlanDistance(a[a.Count - 1], b[b.Count - 1])) b.Reverse();
                double length = Math.Min(PolylinePlanLength(a), PolylinePlanLength(b));
                int samples = Math.Max(1, (int)Math.Ceiling(length / Math.Max(config.Spacing, 0.01)));
                samples = Math.Min(samples, 5000);
                double inset = PaperAnnotationScale.ModelDistance(document.Database, Math.Max(config.ArrowPaper, 0.0));
                BlockTableRecord space = transaction.GetObject(document.Database.CurrentSpaceId, OpenMode.ForWrite, false) as BlockTableRecord;
                for (int index = 0; index <= samples; index++)
                {
                    double fraction = index / (double)samples;
                    Point3d pa = PointAtFraction(a, fraction);
                    Point3d pb = PointAtFraction(b, fraction);
                    double run = PlanDistance(pa, pb);
                    if (run <= Tol) continue;
                    double grade = (pb.Z - pa.Z) / run * 100.0;
                    Point3d high = pa.Z >= pb.Z ? pa : pb;
                    Point3d low = pa.Z >= pb.Z ? pb : pa;
                    Vector3d direction = PlanUnit(low - high);
                    double localInset = Math.Min(inset, run * 0.2);
                    Point3d tail = high + direction * localInset;
                    Point3d tip = low - direction * localInset;
                    if (PlanDistance(tail, tip) <= Tol) { tail = high; tip = low; }
                    AppendLeaderAndText(document.Database, transaction, space, config, tip, tail, grade, first.LayerId, ref created);
                }
                transaction.Commit();
            }
            return created;
        }

        private static int RebuildSurface(Document document, DynamicConfig config)
        {
            if (config.Sources.Count == 0) return 0;
            int created = 0;
            double arrowLength = PaperAnnotationScale.ModelDistance(document.Database, Math.Max(config.ArrowPaper, 0.1));
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                EraseChildren(transaction, config);
                CivilSurface surface = transaction.GetObject(config.Sources[0], OpenMode.ForRead, false) as CivilSurface;
                if (surface == null) { transaction.Commit(); return 0; }
                Extents3d extents;
                try { extents = surface.GeometricExtents; }
                catch { transaction.Commit(); return 0; }
                double spacing = Math.Max(config.Spacing, 0.01);
                double delta = Math.Max(spacing * 0.1, 0.01);
                int nx = Math.Min(1000, Math.Max(1, (int)Math.Ceiling((extents.MaxPoint.X - extents.MinPoint.X) / spacing)));
                int ny = Math.Min(1000, Math.Max(1, (int)Math.Ceiling((extents.MaxPoint.Y - extents.MinPoint.Y) / spacing)));
                int guard = 0;
                BlockTableRecord space = transaction.GetObject(document.Database.CurrentSpaceId, OpenMode.ForWrite, false) as BlockTableRecord;
                for (int ix = 0; ix <= nx && guard < 20000; ix++)
                {
                    double x = extents.MinPoint.X + (extents.MaxPoint.X - extents.MinPoint.X) * ix / nx;
                    for (int iy = 0; iy <= ny && guard < 20000; iy++)
                    {
                        double y = extents.MinPoint.Y + (extents.MaxPoint.Y - extents.MinPoint.Y) * iy / ny;
                        double z, xp, xm, yp, ym;
                        if (!TryElevation(surface, x, y, out z) ||
                            !TryElevation(surface, x + delta, y, out xp) ||
                            !TryElevation(surface, x - delta, y, out xm) ||
                            !TryElevation(surface, x, y + delta, out yp) ||
                            !TryElevation(surface, x, y - delta, out ym)) continue;
                        double gx = (xp - xm) / (2.0 * delta);
                        double gy = (yp - ym) / (2.0 * delta);
                        double grade = Math.Sqrt(gx * gx + gy * gy) * 100.0;
                        if (grade <= Tol) continue;
                        Vector3d downhill = new Vector3d(-gx, -gy, 0.0);
                        if (downhill.Length <= Tol) continue;
                        downhill = downhill.GetNormal();
                        Point3d mid = new Point3d(x, y, z);
                        Point3d tip = mid + downhill * (arrowLength * 0.5);
                        Point3d tail = mid - downhill * (arrowLength * 0.5);
                        AppendLeaderAndText(document.Database, transaction, space, config, tip, tail, grade, surface.LayerId, ref created);
                        guard++;
                    }
                }
                transaction.Commit();
            }
            return created;
        }

        private static void AppendLeaderAndText(Database database, Transaction transaction, BlockTableRecord space,
            DynamicConfig config, Point3d tip, Point3d tail, double grade, ObjectId layerId, ref int created)
        {
            var leader = new Leader();
            leader.SetDatabaseDefaults(database);
            leader.LayerId = layerId;
            leader.ColorIndex = (short)config.Colour;
            leader.HasArrowHead = true;
            leader.AppendVertex(new Point3d(tip.X, tip.Y, 0.0));
            leader.AppendVertex(new Point3d(tail.X, tail.Y, 0.0));
            PaperAnnotationScale.SetAnnotative(leader);
            ObjectId leaderId = space.AppendEntity(leader);
            transaction.AddNewlyCreatedDBObject(leader, true);
            config.Children.Add(leaderId);
            created++;

            Vector3d direction = PlanUnit(tip - tail);
            Vector3d normal = new Vector3d(-direction.Y, direction.X, 0.0);
            double textOffset = PaperAnnotationScale.ModelDistance(database, Math.Max(config.TextPaper, 0.1) * 1.2);
            Point3d location = Mid(tip, tail) + normal * textOffset;
            var text = new MText();
            text.SetDatabaseDefaults(database);
            text.LayerId = layerId;
            text.ColorIndex = (short)config.Colour;
            text.Location = new Point3d(location.X, location.Y, 0.0);
            text.TextHeight = PaperAnnotationScale.AnnotativeTextHeight(database, Math.Max(config.TextPaper, 0.1));
            text.Contents = grade.ToString("F" + config.Precision.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture) + "%";
            text.Rotation = ReadableRotation(Math.Atan2(direction.Y, direction.X));
            text.Attachment = AttachmentPoint.MiddleCenter;
            PaperAnnotationScale.SetAnnotative(text);
            ObjectId textId = space.AppendEntity(text);
            transaction.AddNewlyCreatedDBObject(text, true);
            config.Children.Add(textId);
            created++;
        }

        private static void EraseChildren(Transaction transaction, DynamicConfig config)
        {
            foreach (ObjectId id in config.Children.ToList())
            {
                if (id.IsNull || id.IsErased) continue;
                try
                {
                    Entity child = transaction.GetObject(id, OpenMode.ForWrite, false) as Entity;
                    if (child != null && !child.IsErased) child.Erase();
                }
                catch { }
            }
            config.Children.Clear();
        }

        private static void EraseLegacySlopeChildren(Document document, IEnumerable<ObjectId> sourceIds)
        {
            var handles = new HashSet<string>(sourceIds.Select(id => id.Handle.ToString()), StringComparer.OrdinalIgnoreCase);
            try
            {
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    BlockTableRecord model = transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(document.Database), OpenMode.ForRead, false) as BlockTableRecord;
                    foreach (ObjectId id in model.Cast<ObjectId>().ToList())
                    {
                        Entity entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                        if (entity == null || entity.ExtensionDictionary.IsNull) continue;
                        DBDictionary dict = transaction.GetObject(entity.ExtensionDictionary, OpenMode.ForRead, false) as DBDictionary;
                        if (dict == null || !dict.Contains(LegacySlopeLinkKey)) continue;
                        Xrecord record = transaction.GetObject(dict.GetAt(LegacySlopeLinkKey), OpenMode.ForRead, false) as Xrecord;
                        TypedValue[] values = record == null || record.Data == null ? new TypedValue[0] : record.Data.AsArray();
                        if (values.Length == 0 || !handles.Contains(Convert.ToString(values[0].Value, CultureInfo.InvariantCulture))) continue;
                        entity.UpgradeOpen();
                        entity.Erase();
                    }
                    transaction.Commit();
                }
            }
            catch { }
        }

        private static bool TryCreateHatchStripIsolated(Document document, ObjectId sourceId, double offset,
            string pattern, double scale, int colour, double sampleInterval)
        {
            try
            {
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    Entity entity = transaction.GetObject(sourceId, OpenMode.ForRead, false) as Entity;
                    using (Polyline source = BuildRoadPolyline(entity, sampleInterval))
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
                            var loop = first.Concat(second).ToList();
                            if (loop.Count < 4) return false;

                            ObjectId hatchLayer = GetOrCreateLayer(document.Database, transaction, RoadHatchLayer);
                            ObjectId boundaryLayer = GetOrCreateLayer(document.Database, transaction, RoadHatchBoundaryLayer);
                            BlockTableRecord space = transaction.GetObject(document.Database.CurrentSpaceId, OpenMode.ForWrite, false) as BlockTableRecord;

                            var boundary = new Polyline(loop.Count);
                            boundary.SetDatabaseDefaults(document.Database);
                            boundary.LayerId = boundaryLayer;
                            for (int index = 0; index < loop.Count; index++)
                                boundary.AddVertexAt(index, new Point2d(loop[index].X, loop[index].Y), 0.0, 0.0, 0.0);
                            boundary.Closed = true;
                            if (!BoundaryLooksUsable(boundary)) return false;
                            ObjectId boundaryId = space.AppendEntity(boundary);
                            transaction.AddNewlyCreatedDBObject(boundary, true);

                            var hatch = new Hatch();
                            hatch.SetDatabaseDefaults(document.Database);
                            hatch.LayerId = hatchLayer;
                            if (colour == 256) hatch.ColorIndex = 256;
                            else hatch.Color = Color.FromColorIndex(ColorMethod.ByAci, (short)colour);
                            space.AppendEntity(hatch);
                            transaction.AddNewlyCreatedDBObject(hatch, true);
                            hatch.SetHatchPattern(HatchPatternType.PreDefined, string.IsNullOrWhiteSpace(pattern) ? "ANSI31" : pattern);
                            if (!string.Equals(pattern, "SOLID", StringComparison.OrdinalIgnoreCase)) hatch.PatternScale = scale;
                            hatch.Associative = true;
                            var ids = new ObjectIdCollection();
                            ids.Add(boundaryId);
                            hatch.AppendLoop(HatchLoopTypes.Outermost, ids);
                            hatch.EvaluateHatch(false);
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
                document.Editor.WriteMessage("\nRoad hatch strip skipped safely: {0}", exception.Message);
                return false;
            }
        }

        private static bool BoundaryLooksUsable(Polyline boundary)
        {
            try
            {
                if (boundary == null || boundary.NumberOfVertices < 4 || !boundary.Closed) return false;
                return Math.Abs(boundary.Area) > Tol * Tol;
            }
            catch { return false; }
        }

        private static Polyline BuildRoadPolyline(Entity entity, double sampleInterval)
        {
            Polyline source = entity as Polyline;
            if (source != null)
            {
                var copy = new Polyline(source.NumberOfVertices);
                for (int index = 0; index < source.NumberOfVertices; index++)
                    copy.AddVertexAt(index, source.GetPoint2dAt(index), source.GetBulgeAt(index), 0.0, 0.0);
                copy.Elevation = source.Elevation;
                copy.Closed = source.Closed;
                return copy;
            }
            CivilAlignment alignment = entity as CivilAlignment;
            if (alignment == null) return null;
            double start = ReadDouble(alignment, "StartingStation");
            double end = ReadDouble(alignment, "EndingStation");
            if (!(end > start)) return null;
            sampleInterval = Math.Max(sampleInterval, 0.25);
            int count = Math.Max(2, (int)Math.Ceiling((end - start) / sampleInterval));
            var polyline = new Polyline(count + 1);
            for (int index = 0; index <= count; index++)
            {
                double station = start + (end - start) * index / count;
                Point2d point;
                if (!TryAlignmentPoint(alignment, station, out point)) { polyline.Dispose(); return null; }
                polyline.AddVertexAt(index, point, 0.0, 0.0, 0.0);
            }
            return polyline;
        }

        private static bool TryAlignmentPoint(CivilAlignment alignment, double station, out Point2d point)
        {
            point = Point2d.Origin;
            try
            {
                MethodInfo method = alignment.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                    .FirstOrDefault(item => item.Name == "PointLocation" && item.GetParameters().Length == 4);
                if (method == null) return false;
                object[] values = { station, 0.0, 0.0, 0.0 };
                method.Invoke(alignment, values);
                point = new Point2d(Convert.ToDouble(values[2], CultureInfo.InvariantCulture), Convert.ToDouble(values[3], CultureInfo.InvariantCulture));
                return true;
            }
            catch { return false; }
        }

        private static List<Point3d> SampleCurve(Curve curve, int count)
        {
            var result = new List<Point3d>();
            count = Math.Max(2, count);
            for (int index = 0; index <= count; index++)
            {
                double parameter = curve.StartParam + (curve.EndParam - curve.StartParam) * index / count;
                try { result.Add(curve.GetPointAtParameter(parameter)); } catch { }
            }
            return result;
        }

        private static bool TryEndpoints(Entity entity, out Point3d first, out Point3d second)
        {
            first = Point3d.Origin;
            second = Point3d.Origin;
            Curve curve = entity as Curve;
            if (curve != null)
            {
                try { first = curve.StartPoint; second = curve.EndPoint; return true; } catch { }
            }
            CivilFeatureLine featureLine = entity as CivilFeatureLine;
            if (featureLine != null)
            {
                try
                {
                    Point3dCollection points = featureLine.GetPoints(FeatureLinePointType.PIPoint);
                    if (points != null && points.Count >= 2)
                    {
                        first = points[0]; second = points[points.Count - 1]; return true;
                    }
                }
                catch { }
            }
            return false;
        }

        private static PromptSelectionResult Select(Editor editor, string message)
        {
            PromptSelectionResult implied = editor.SelectImplied();
            if (implied.Status == PromptStatus.OK && implied.Value != null && implied.Value.Count > 0)
            {
                editor.SetImpliedSelection(new ObjectId[0]);
                return implied;
            }
            return editor.GetSelection(new PromptSelectionOptions
            {
                MessageForAdding = message,
                AllowDuplicates = false,
                RejectObjectsFromNonCurrentSpace = true
            });
        }

        private static ObjectId GetOrCreateLayer(Database database, Transaction transaction, string name)
        {
            LayerTable table = transaction.GetObject(database.LayerTableId, OpenMode.ForRead, false) as LayerTable;
            if (table.Has(name)) return table[name];
            table.UpgradeOpen();
            var layer = new LayerTableRecord { Name = name };
            ObjectId id = table.Add(layer);
            transaction.AddNewlyCreatedDBObject(layer, true);
            return id;
        }

        private static int ColourFromName(string name)
        {
            if (string.Equals(name, "Red", StringComparison.OrdinalIgnoreCase)) return 1;
            if (string.Equals(name, "Yellow", StringComparison.OrdinalIgnoreCase)) return 2;
            if (string.Equals(name, "Green", StringComparison.OrdinalIgnoreCase)) return 3;
            if (string.Equals(name, "Cyan", StringComparison.OrdinalIgnoreCase)) return 4;
            if (string.Equals(name, "Blue", StringComparison.OrdinalIgnoreCase)) return 5;
            if (string.Equals(name, "Magenta", StringComparison.OrdinalIgnoreCase)) return 6;
            if (string.Equals(name, "White", StringComparison.OrdinalIgnoreCase)) return 7;
            return 256;
        }

        private static int ClampColour(int value) { return Math.Max(1, Math.Min(255, value)); }

        private static bool TryElevation(CivilSurface surface, double x, double y, out double elevation)
        {
            elevation = 0.0;
            try { elevation = surface.FindElevationAtXY(x, y); return !(double.IsNaN(elevation) || double.IsInfinity(elevation)); }
            catch { return false; }
        }

        private static List<Point3d> ToList(Point3dCollection points)
        {
            var result = new List<Point3d>();
            if (points != null) foreach (Point3d point in points) result.Add(point);
            return result;
        }

        private static double PolylinePlanLength(IList<Point3d> points)
        {
            double result = 0.0;
            for (int index = 0; index + 1 < points.Count; index++) result += PlanDistance(points[index], points[index + 1]);
            return result;
        }

        private static Point3d PointAtFraction(IList<Point3d> points, double fraction)
        {
            if (points == null || points.Count == 0) return Point3d.Origin;
            if (points.Count == 1) return points[0];
            double total = PolylinePlanLength(points);
            if (total <= Tol) return points[0];
            double target = Math.Max(0.0, Math.Min(1.0, fraction)) * total;
            double walked = 0.0;
            for (int index = 0; index + 1 < points.Count; index++)
            {
                double length = PlanDistance(points[index], points[index + 1]);
                if (walked + length >= target || index == points.Count - 2)
                {
                    double ratio = length <= Tol ? 0.0 : (target - walked) / length;
                    Point3d a = points[index];
                    Point3d b = points[index + 1];
                    return new Point3d(
                        a.X + (b.X - a.X) * ratio,
                        a.Y + (b.Y - a.Y) * ratio,
                        a.Z + (b.Z - a.Z) * ratio);
                }
                walked += length;
            }
            return points[points.Count - 1];
        }

        private static Vector3d PlanUnit(Vector3d value)
        {
            var plan = new Vector3d(value.X, value.Y, 0.0);
            return plan.Length <= Tol ? Vector3d.Zero : plan.GetNormal();
        }

        private static Point3d Mid(Point3d first, Point3d second)
        {
            return new Point3d((first.X + second.X) * 0.5, (first.Y + second.Y) * 0.5, 0.0);
        }

        private static double PlanDistance(Point3d first, Point3d second)
        {
            double dx = first.X - second.X;
            double dy = first.Y - second.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static double ReadableRotation(double angle)
        {
            while (angle < 0.0) angle += Math.PI * 2.0;
            while (angle >= Math.PI * 2.0) angle -= Math.PI * 2.0;
            if (angle > Math.PI * 0.5 && angle < Math.PI * 1.5) angle += Math.PI;
            while (angle >= Math.PI * 2.0) angle -= Math.PI * 2.0;
            return angle;
        }

        private static double ReadDouble(object value, string propertyName)
        {
            try
            {
                PropertyInfo property = value.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
                return property == null ? 0.0 : Convert.ToDouble(property.GetValue(value, null), CultureInfo.InvariantCulture);
            }
            catch { return 0.0; }
        }
    }

    /// <summary>
    /// Document-local watcher. Source modifications mark only affected dynamic
    /// annotation groups dirty. CommandEnded performs the Civil/AutoCAD writes after
    /// the source command has completed. Annotation-scale changes rebuild every group.
    /// </summary>
    internal static class August27DynamicSurveyGraphicsManager
    {
        private sealed class State
        {
            internal readonly List<August27DynamicSurveyGraphicsRuntime.DynamicConfig> Configs =
                new List<August27DynamicSurveyGraphicsRuntime.DynamicConfig>();
            internal readonly HashSet<ObjectId> DirtySources = new HashSet<ObjectId>();
            internal bool Busy;
            internal double Scale;
        }

        private static readonly Dictionary<Document, State> States = new Dictionary<Document, State>();

        internal static void Watch(Document document, August27DynamicSurveyGraphicsRuntime.DynamicConfig config)
        {
            if (document == null || config == null) return;
            State state;
            if (!States.TryGetValue(document, out state))
            {
                state = new State();
                state.Scale = CurrentScale(document.Database);
                States.Add(document, state);
                try { document.Database.ObjectModified += OnObjectModified; } catch { }
                try { document.CommandEnded += OnCommandEnded; } catch { }
            }
            state.Configs.Add(config);
        }

        internal static int RefreshAll(Document document)
        {
            State state;
            if (document == null || !States.TryGetValue(document, out state)) return 0;
            if (state.Busy) return 0;
            state.Busy = true;
            int created = 0;
            try
            {
                foreach (August27DynamicSurveyGraphicsRuntime.DynamicConfig config in state.Configs.ToList())
                    created += August27DynamicSurveyGraphicsRuntime.Rebuild(document, config);
                state.DirtySources.Clear();
                state.Scale = CurrentScale(document.Database);
                return created;
            }
            finally { state.Busy = false; }
        }

        private static void OnObjectModified(object sender, ObjectEventArgs args)
        {
            if (args == null || args.DBObject == null) return;
            ObjectId id;
            try { id = args.DBObject.ObjectId; } catch { return; }
            if (id.IsNull) return;
            foreach (KeyValuePair<Document, State> pair in States)
            {
                State state = pair.Value;
                if (state.Busy || pair.Key.Database != sender) continue;
                if (state.Configs.Any(config => config.Sources.Contains(id))) state.DirtySources.Add(id);
            }
        }

        private static void OnCommandEnded(object sender, CommandEventArgs args)
        {
            Document document = sender as Document;
            State state;
            if (document == null || !States.TryGetValue(document, out state) || state.Busy) return;
            double scale = CurrentScale(document.Database);
            bool scaleChanged = Math.Abs(scale - state.Scale) > Math.Max(0.000001, Math.Abs(state.Scale) * 0.000001);
            List<August27DynamicSurveyGraphicsRuntime.DynamicConfig> configs = scaleChanged
                ? state.Configs.ToList()
                : state.Configs.Where(config => config.Sources.Any(source => state.DirtySources.Contains(source))).ToList();
            if (configs.Count == 0) { state.Scale = scale; return; }

            state.Busy = true;
            try
            {
                foreach (August27DynamicSurveyGraphicsRuntime.DynamicConfig config in configs)
                    August27DynamicSurveyGraphicsRuntime.Rebuild(document, config);
                state.DirtySources.Clear();
                state.Scale = scale;
                August21DisplayRefresh.Flush(document);
            }
            catch { }
            finally { state.Busy = false; }
        }

        private static double CurrentScale(Database database)
        {
            try { return PaperAnnotationScale.ModelDistance(database, 1.0); }
            catch { return 1.0; }
        }
    }
}

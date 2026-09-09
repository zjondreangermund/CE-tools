using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil;
using Autodesk.Civil.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilFeatureLine = Autodesk.Civil.DatabaseServices.FeatureLine;

[assembly: CommandClass(typeof(CETools.Civil3D.DynamicSegmentLabelCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Creates presentation-ready, annotative segment-length text and optional
    /// closed-polyline area text. Source geometry is persisted on the objects so
    /// CE Tools can rebuild the labels after grip edits, moves and scale changes.
    /// </summary>
    public sealed class DynamicSegmentLabelCommands
    {
        [CommandMethod("CE_TOOLS", "CE_SEGMENTLABELS", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        [CommandMethod("CE_TOOLS", "CE_SEGLABELS", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void Create()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            List<string> layers = DynamicSegmentLabelManager.ReadLayerNames(document.Database);
            var layerChoices = new List<string> { "<Source layer>", "CE SEGMENT LABELS" };
            layerChoices.AddRange(layers.Where(value =>
                !layerChoices.Contains(value, StringComparer.OrdinalIgnoreCase)));

            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Multiple Dimensions: Segment Lengths & Areas",
                "Add annotative segment-length text to multiple AutoCAD lines/polylines and Civil 3D feature lines. Curved segments can follow the curve. Closed polylines can also receive a dynamic area label.");
            settings.AddChoice(
                "LengthUnits", "01 Dimension", "Segment length units", "Millimetres",
                "Metres uses drawing geometry converted to metres. Millimetres displays the same physical length x1000.",
                new[] { "Millimetres", "Metres" });
            settings.AddChoice(
                "AreaUnits", "01 Dimension", "Closed polyline area", "m²",
                "Choose the area unit for closed polylines, or None to create length labels only.",
                new[] { "None", "m²", "ha", "km²" });
            settings.AddChoice(
                "CurveText", "01 Dimension", "Curved segment text", "Follow curve",
                "Follow curve lays the length characters along the true polyline/feature-line arc. Tangent at midpoint keeps one straight label rotated to the arc tangent.",
                new[] { "Follow curve", "Tangent at midpoint" });
            settings.AddPositiveDouble(
                "TextHeight", "02 Appearance", "Text height (paper mm)", 2.0,
                "Paper text height. Output is annotative and is rebuilt at the current annotation scale.");
            settings.AddChoice(
                "Background", "02 Appearance", "Background mask", "Enabled",
                "Add a drawing-background mask behind each generated text object.",
                new[] { "Enabled", "Disabled" });
            settings.AddChoice(
                "Layer", "02 Appearance", "Text layer", "CE SEGMENT LABELS",
                "Choose an existing layer, the source object's layer, or CE SEGMENT LABELS (created automatically when needed).",
                layerChoices);
            settings.AddChoice(
                "Position", "03 Placement", "Text position", "Above",
                "Place every segment label above, below, or directly on the source segment.",
                new[] { "Above", "Below", "On top" });
            settings.AddPositiveDouble(
                "Offset", "03 Placement", "Text offset (paper mm)", 2.0,
                "Perpendicular paper-space offset from the segment midpoint. Ignored when position is On top.");
            settings.AddChoice(
                "Dynamic", "04 Dynamic", "Dynamic update", "Enabled",
                "Enabled keeps lengths, areas, positions, rotations and paper size linked to the source geometry.",
                new[] { "Enabled", "Disabled" });

            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;

            PromptSelectionResult selection = document.Editor.SelectImplied();
            if (selection.Status != PromptStatus.OK || selection.Value == null || selection.Value.Count == 0)
            {
                var options = new PromptSelectionOptions
                {
                    MessageForAdding = "\nSelect multiple lines, polylines and/or Civil 3D feature lines: ",
                    AllowDuplicates = false,
                    RejectObjectsFromNonCurrentSpace = true
                };
                selection = document.Editor.GetSelection(options);
            }
            document.Editor.SetImpliedSelection(new ObjectId[0]);
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;

            var values = new SegmentLabelSettings
            {
                LengthUnits = settings.Text("LengthUnits"),
                AreaUnits = settings.Text("AreaUnits"),
                CurvedText = settings.Text("CurveText"),
                TextHeightPaper = PaperAnnotationScale.NormalizeConfiguredPaperHeight(
                    settings.Double("TextHeight", 2.0)),
                BackgroundMask = string.Equals(
                    settings.Text("Background"), "Enabled", StringComparison.OrdinalIgnoreCase),
                Layer = settings.Text("Layer"),
                Position = settings.Text("Position"),
                OffsetPaper = Math.Max(settings.Double("Offset", 2.0), 0.0),
                Dynamic = string.Equals(
                    settings.Text("Dynamic"), "Enabled", StringComparison.OrdinalIgnoreCase)
            };

            SegmentLabelBuildResult result;
            try
            {
                result = DynamicSegmentLabelManager.CreateOrReplace(
                    document,
                    selection.Value.GetObjectIds().Distinct().ToArray(),
                    values);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage("\nCE_SEGMENTLABELS stopped. {0}", exception.Message);
                return;
            }

            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_SEGMENTLABELS complete. Sources={0}; segment labels={1}; area labels={2}; unsupported={3}; failed={4}; dynamic={5}.",
                result.Sources,
                result.SegmentLabels,
                result.AreaLabels,
                result.Unsupported,
                result.Failed,
                values.Dynamic ? "On" : "Off");
        }

        [CommandMethod("CE_TOOLS", "CE_SEGMENTLABELREFRESH", CommandFlags.Modal | CommandFlags.Redraw)]
        public void Refresh()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            int refreshed = DynamicSegmentLabelManager.RefreshAll(document);
            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_SEGMENTLABELREFRESH complete. Dynamic sources refreshed={0}.",
                refreshed);
        }
    }

    internal static class DynamicSegmentLabelManager
    {
        private const string SourceRecord = "CE_DYNAMIC_SEGMENT_SOURCE";
        private const string OutputRecord = "CE_DYNAMIC_SEGMENT_OUTPUT";
        private const string DefaultLayer = "CE SEGMENT LABELS";
        private const double Tolerance = 1e-8;

        internal static List<string> ReadLayerNames(Database database)
        {
            var result = new List<string>();
            if (database == null) return result;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                LayerTable table = transaction.GetObject(
                    database.LayerTableId, OpenMode.ForRead, false) as LayerTable;
                if (table != null)
                {
                    foreach (ObjectId id in table)
                    {
                        LayerTableRecord layer = transaction.GetObject(
                            id, OpenMode.ForRead, false) as LayerTableRecord;
                        if (layer != null && !string.IsNullOrWhiteSpace(layer.Name))
                            result.Add(layer.Name);
                    }
                }
            }
            return result
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        internal static SegmentLabelBuildResult CreateOrReplace(
            Document document,
            IEnumerable<ObjectId> sourceIds,
            SegmentLabelSettings settings)
        {
            var result = new SegmentLabelBuildResult();
            if (document == null || document.Database == null || sourceIds == null || settings == null)
                return result;

            Database database = document.Database;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = transaction.GetObject(
                    database.CurrentSpaceId, OpenMode.ForWrite, false) as BlockTableRecord;
                if (space == null) return result;

                Dictionary<string, List<ObjectId>> outputMap = ReadOutputMap(transaction, space);

                foreach (ObjectId id in sourceIds.Where(value => !value.IsNull && !value.IsErased).Distinct())
                {
                    Entity source;
                    try { source = transaction.GetObject(id, OpenMode.ForWrite, false) as Entity; }
                    catch { result.Failed++; continue; }
                    if (source == null || !IsSupported(source))
                    {
                        result.Unsupported++;
                        continue;
                    }

                    List<SegmentGeometry> segments;
                    double? sourceArea;
                    if (!TryBuildGeometry(transaction, source, out segments, out sourceArea) ||
                        segments.Count == 0)
                    {
                        result.Unsupported++;
                        continue;
                    }

                    result.Sources++;
                    string handle = source.ObjectId.Handle.ToString();
                    List<ObjectId> oldOutputs;
                    if (outputMap.TryGetValue(handle, out oldOutputs))
                        EraseOutputs(transaction, oldOutputs);

                    try
                    {
                        EnsureOutputLayer(database, transaction, settings.Layer);
                        int outputCount = BuildOutputs(
                            database,
                            transaction,
                            space,
                            source,
                            segments,
                            sourceArea,
                            settings,
                            ref result);
                        string fingerprint = Fingerprint(database, source, segments, sourceArea, settings);
                        WriteSourceSettings(source, transaction, settings, fingerprint, outputCount);
                    }
                    catch
                    {
                        result.Failed++;
                    }
                }

                transaction.Commit();
            }

            if (settings.Dynamic) UniversalDynamicRefreshManager.Queue();
            return result;
        }

        internal static int RefreshAll(Document document)
        {
            if (document == null || document.Database == null) return 0;
            Database database = document.Database;
            int refreshed = 0;

            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = transaction.GetObject(
                    database.CurrentSpaceId, OpenMode.ForWrite, false) as BlockTableRecord;
                if (space == null) return 0;

                Dictionary<string, List<ObjectId>> outputs = ReadOutputMap(transaction, space);
                var knownSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var sourceIds = space.Cast<ObjectId>().Where(id => !id.IsNull && !id.IsErased).ToList();

                foreach (ObjectId id in sourceIds)
                {
                    Entity source;
                    try { source = transaction.GetObject(id, OpenMode.ForRead, false) as Entity; }
                    catch { continue; }
                    if (source == null) continue;

                    Dictionary<string, string> values;
                    if (!TryReadRecord(source, transaction, SourceRecord, out values)) continue;

                    string handle = source.ObjectId.Handle.ToString();
                    knownSources.Add(handle);
                    SegmentLabelSettings settings = SegmentLabelSettings.From(values);
                    if (settings == null || !settings.Dynamic) continue;

                    List<SegmentGeometry> segments;
                    double? sourceArea;
                    if (!TryBuildGeometry(transaction, source, out segments, out sourceArea) ||
                        segments.Count == 0)
                        continue;

                    string currentFingerprint = Fingerprint(
                        database, source, segments, sourceArea, settings);
                    string previousFingerprint = Value(values, "Fingerprint");
                    int expectedCount = Integer(values, "OutputCount", -1);
                    List<ObjectId> linked;
                    int actualCount = outputs.TryGetValue(handle, out linked)
                        ? linked.Count(id2 => !id2.IsNull && !id2.IsErased)
                        : 0;

                    if (string.Equals(
                            currentFingerprint,
                            previousFingerprint,
                            StringComparison.OrdinalIgnoreCase) &&
                        expectedCount >= 0 &&
                        actualCount == expectedCount)
                        continue;

                    try
                    {
                        if (linked != null) EraseOutputs(transaction, linked);
                        if (!source.IsWriteEnabled) source.UpgradeOpen();
                        EnsureOutputLayer(database, transaction, settings.Layer);
                        var build = new SegmentLabelBuildResult();
                        int outputCount = BuildOutputs(
                            database,
                            transaction,
                            space,
                            source,
                            segments,
                            sourceArea,
                            settings,
                            ref build);
                        WriteSourceSettings(
                            source,
                            transaction,
                            settings,
                            currentFingerprint,
                            outputCount);
                        refreshed++;
                    }
                    catch
                    {
                        // One bad source must not block other linked annotations.
                    }
                }

                foreach (KeyValuePair<string, List<ObjectId>> pair in outputs)
                {
                    if (knownSources.Contains(pair.Key)) continue;
                    EraseOutputs(transaction, pair.Value);
                }

                transaction.Commit();
            }
            return refreshed;
        }

        private static int BuildOutputs(
            Database database,
            Transaction transaction,
            BlockTableRecord space,
            Entity source,
            IList<SegmentGeometry> segments,
            double? sourceArea,
            SegmentLabelSettings settings,
            ref SegmentLabelBuildResult result)
        {
            int outputCount = 0;
            double textHeight = PaperAnnotationScale.AnnotativeTextHeight(
                database, settings.TextHeightPaper);
            double offset = string.Equals(
                settings.Position, "On top", StringComparison.OrdinalIgnoreCase)
                ? 0.0
                : PaperAnnotationScale.ModelDistance(database, settings.OffsetPaper);
            string layer = ResolveLayer(source, settings.Layer);

            foreach (SegmentGeometry segment in segments)
            {
                string label = "L = " + FormatLength(database, segment.Length, settings.LengthUnits);
                if (segment.IsArc &&
                    string.Equals(settings.CurvedText, "Follow curve", StringComparison.OrdinalIgnoreCase))
                {
                    outputCount += AddCurvedText(
                        database, transaction, space, source, segment, label,
                        textHeight, offset, layer, settings);
                }
                else
                {
                    Point3d point;
                    double rotation;
                    TextPlacement(segment, settings.Position, offset, out point, out rotation);
                    AddText(
                        database, transaction, space, source.ObjectId.Handle.ToString(),
                        label, point, rotation, textHeight, layer, settings.BackgroundMask);
                    outputCount++;
                }
                result.SegmentLabels++;
            }

            if (sourceArea.HasValue &&
                !string.Equals(settings.AreaUnits, "None", StringComparison.OrdinalIgnoreCase))
            {
                Polyline polyline = source as Polyline;
                if (polyline != null && polyline.Closed)
                {
                    Point3d areaPoint = InteriorLabelPoint(segments);
                    string areaText = "A = " + FormatArea(database, sourceArea.Value, settings.AreaUnits);
                    AddText(
                        database, transaction, space, source.ObjectId.Handle.ToString(),
                        areaText, areaPoint, 0.0, textHeight, layer, settings.BackgroundMask);
                    outputCount++;
                    result.AreaLabels++;
                }
            }
            return outputCount;
        }

        private static bool TryBuildGeometry(
            Transaction transaction,
            Entity source,
            out List<SegmentGeometry> segments,
            out double? sourceArea)
        {
            segments = new List<SegmentGeometry>();
            sourceArea = null;

            Line line = source as Line;
            if (line != null)
            {
                AddLineSegment(segments, line.StartPoint, line.EndPoint);
                return segments.Count > 0;
            }

            Polyline polyline = source as Polyline;
            if (polyline != null)
            {
                AddPolylineSegments(segments, polyline);
                if (polyline.Closed)
                {
                    try { sourceArea = Math.Abs(polyline.Area); }
                    catch { sourceArea = null; }
                }
                return segments.Count > 0;
            }

            CivilFeatureLine featureLine = source as CivilFeatureLine;
            if (featureLine != null && featureLine.GetType() == typeof(CivilFeatureLine))
            {
                DBObjectCollection exploded = new DBObjectCollection();
                try
                {
                    featureLine.Explode(exploded);
                    foreach (DBObject item in exploded)
                    {
                        Line explodedLine = item as Line;
                        if (explodedLine != null)
                        {
                            AddLineSegment(segments, explodedLine.StartPoint, explodedLine.EndPoint);
                            continue;
                        }

                        Arc arc = item as Arc;
                        if (arc != null)
                        {
                            double sweep = arc.EndAngle - arc.StartAngle;
                            while (sweep <= 0.0) sweep += Math.PI * 2.0;
                            AddArcSegment(
                                segments,
                                Plan(arc.StartPoint),
                                Plan(arc.EndPoint),
                                Plan(arc.Center),
                                arc.Radius,
                                arc.StartAngle,
                                sweep);
                            continue;
                        }

                        Polyline explodedPolyline = item as Polyline;
                        if (explodedPolyline != null)
                            AddPolylineSegments(segments, explodedPolyline);
                    }
                }
                catch
                {
                    segments.Clear();
                }
                finally
                {
                    foreach (DBObject item in exploded)
                    {
                        try { item.Dispose(); }
                        catch { }
                    }
                }

                if (segments.Count == 0)
                {
                    try
                    {
                        Point3dCollection points = featureLine.GetPoints(FeatureLinePointType.AllPoints);
                        if (points != null)
                            for (int index = 0; index < points.Count - 1; index++)
                                AddLineSegment(segments, points[index], points[index + 1]);
                    }
                    catch { }
                }
                return segments.Count > 0;
            }

            return false;
        }

        private static void AddPolylineSegments(
            ICollection<SegmentGeometry> segments,
            Polyline polyline)
        {
            if (polyline == null || polyline.NumberOfVertices < 2) return;
            int count = polyline.Closed ? polyline.NumberOfVertices : polyline.NumberOfVertices - 1;
            for (int index = 0; index < count; index++)
            {
                Point3d start = Plan(polyline.GetPoint3dAt(index));
                Point3d end = Plan(polyline.GetPoint3dAt((index + 1) % polyline.NumberOfVertices));
                if (start.DistanceTo(end) <= Tolerance) continue;
                double bulge = 0.0;
                try { bulge = polyline.GetBulgeAt(index); }
                catch { }
                if (Math.Abs(bulge) <= Tolerance)
                {
                    AddLineSegment(segments, start, end);
                    continue;
                }

                double chord = start.DistanceTo(end);
                double radius = chord * (1.0 + bulge * bulge) / (4.0 * Math.Abs(bulge));
                Vector3d direction = end - start;
                Vector3d left = new Vector3d(-direction.Y, direction.X, 0.0).GetNormal();
                double centerOffset = chord * (1.0 - bulge * bulge) / (4.0 * bulge);
                Point3d center = Mid(start, end) + left * centerOffset;
                double startAngle = Math.Atan2(start.Y - center.Y, start.X - center.X);
                double sweep = 4.0 * Math.Atan(bulge);
                AddArcSegment(segments, start, end, center, radius, startAngle, sweep);
            }
        }

        private static void AddLineSegment(
            ICollection<SegmentGeometry> segments,
            Point3d start,
            Point3d end)
        {
            start = Plan(start);
            end = Plan(end);
            double length = start.DistanceTo(end);
            if (length <= Tolerance) return;
            segments.Add(new SegmentGeometry
            {
                Start = start,
                End = end,
                Length = length,
                MidPoint = Mid(start, end),
                Tangent = (end - start).GetNormal()
            });
        }

        private static void AddArcSegment(
            ICollection<SegmentGeometry> segments,
            Point3d start,
            Point3d end,
            Point3d center,
            double radius,
            double startAngle,
            double sweep)
        {
            if (!(radius > Tolerance) || Math.Abs(sweep) <= Tolerance)
            {
                AddLineSegment(segments, start, end);
                return;
            }
            double middleAngle = startAngle + sweep * 0.5;
            Point3d middle = new Point3d(
                center.X + radius * Math.Cos(middleAngle),
                center.Y + radius * Math.Sin(middleAngle),
                0.0);
            double direction = sweep >= 0.0 ? 1.0 : -1.0;
            Vector3d tangent = new Vector3d(
                -Math.Sin(middleAngle) * direction,
                Math.Cos(middleAngle) * direction,
                0.0);

            segments.Add(new SegmentGeometry
            {
                Start = Plan(start),
                End = Plan(end),
                IsArc = true,
                Center = Plan(center),
                Radius = radius,
                StartAngle = startAngle,
                Sweep = sweep,
                Length = radius * Math.Abs(sweep),
                MidPoint = middle,
                Tangent = tangent.GetNormal()
            });
        }

        private static int AddCurvedText(
            Database database,
            Transaction transaction,
            BlockTableRecord space,
            Entity source,
            SegmentGeometry segment,
            string label,
            double textHeight,
            double offset,
            string layer,
            SegmentLabelSettings settings)
        {
            if (string.IsNullOrEmpty(label) || segment.Radius <= Tolerance)
                return 0;

            double sourceDirection = segment.Sweep >= 0.0 ? 1.0 : -1.0;
            double middleAngle = segment.StartAngle + segment.Sweep * 0.5;
            Vector3d tangent = new Vector3d(
                -Math.Sin(middleAngle) * sourceDirection,
                Math.Cos(middleAngle) * sourceDirection,
                0.0).GetNormal();
            if (!Readable(tangent))
            {
                sourceDirection *= -1.0;
                tangent = -tangent;
            }

            Vector3d leftAtMiddle = new Vector3d(-tangent.Y, tangent.X, 0.0);
            double side = PlacementSide(settings.Position, leftAtMiddle);
            double advance = Math.Max(textHeight * 0.62, Tolerance);
            if (label.Length > 1)
            {
                double available = segment.Radius * Math.Abs(segment.Sweep) * 0.82;
                advance = Math.Min(advance, available / (label.Length - 1));
            }

            double centreIndex = (label.Length - 1) * 0.5;
            int created = 0;
            for (int index = 0; index < label.Length; index++)
            {
                char character = label[index];
                if (char.IsWhiteSpace(character)) continue;
                double angle = middleAngle +
                    sourceDirection * (index - centreIndex) * advance / segment.Radius;
                Point3d onArc = new Point3d(
                    segment.Center.X + segment.Radius * Math.Cos(angle),
                    segment.Center.Y + segment.Radius * Math.Sin(angle),
                    0.0);
                Vector3d charTangent = new Vector3d(
                    -Math.Sin(angle) * sourceDirection,
                    Math.Cos(angle) * sourceDirection,
                    0.0).GetNormal();
                Vector3d charLeft = new Vector3d(-charTangent.Y, charTangent.X, 0.0);
                Point3d location = onArc + charLeft * side * offset;
                double rotation = NormalizeReadableRotation(
                    Math.Atan2(charTangent.Y, charTangent.X));

                AddText(
                    database,
                    transaction,
                    space,
                    source.ObjectId.Handle.ToString(),
                    character.ToString(),
                    location,
                    rotation,
                    textHeight,
                    layer,
                    settings.BackgroundMask);
                created++;
            }
            return created;
        }

        private static void TextPlacement(
            SegmentGeometry segment,
            string position,
            double offset,
            out Point3d location,
            out double rotation)
        {
            Vector3d tangent = segment.Tangent.Length <= Tolerance
                ? Vector3d.XAxis
                : segment.Tangent.GetNormal();
            if (!Readable(tangent)) tangent = -tangent;
            Vector3d left = new Vector3d(-tangent.Y, tangent.X, 0.0);
            double side = PlacementSide(position, left);
            location = segment.MidPoint + left * side * offset;
            rotation = NormalizeReadableRotation(Math.Atan2(tangent.Y, tangent.X));
        }

        private static double PlacementSide(string position, Vector3d left)
        {
            if (string.Equals(position, "On top", StringComparison.OrdinalIgnoreCase))
                return 0.0;

            double aboveSide;
            if (Math.Abs(left.Y) > Tolerance)
                aboveSide = left.Y >= 0.0 ? 1.0 : -1.0;
            else
                aboveSide = left.X <= 0.0 ? 1.0 : -1.0;

            return string.Equals(position, "Below", StringComparison.OrdinalIgnoreCase)
                ? -aboveSide
                : aboveSide;
        }

        private static void AddText(
            Database database,
            Transaction transaction,
            BlockTableRecord space,
            string sourceHandle,
            string contents,
            Point3d location,
            double rotation,
            double textHeight,
            string layer,
            bool backgroundMask)
        {
            var text = new MText();
            text.SetDatabaseDefaults(database);
            text.Contents = contents ?? string.Empty;
            text.Location = Plan(location);
            text.TextHeight = Math.Max(textHeight, Tolerance);
            text.Attachment = AttachmentPoint.MiddleCenter;
            text.Rotation = rotation;
            text.Layer = layer;
            if (backgroundMask)
            {
                try
                {
                    text.BackgroundFill = true;
                    text.UseBackgroundColor = true;
                    text.BackgroundScaleFactor = 1.15;
                }
                catch { }
            }
            PaperAnnotationScale.SetAnnotative(text);
            space.AppendEntity(text);
            transaction.AddNewlyCreatedDBObject(text, true);
            WriteRecord(
                text,
                transaction,
                OutputRecord,
                new TypedValue((int)DxfCode.Text, "Schema=1"),
                new TypedValue((int)DxfCode.Text, "Source=" + sourceHandle));
        }

        private static Point3d InteriorLabelPoint(IList<SegmentGeometry> segments)
        {
            List<Point3d> polygon = Tessellate(segments);
            if (polygon.Count < 3) return Average(polygon);

            Point3d centroid;
            if (!TryPolygonCentroid(polygon, out centroid))
                centroid = Average(polygon);
            if (PointInPolygon(centroid, polygon)) return centroid;

            double minX = polygon.Min(point => point.X);
            double maxX = polygon.Max(point => point.X);
            double minY = polygon.Min(point => point.Y);
            double maxY = polygon.Max(point => point.Y);
            Point3d target = new Point3d(
                (minX + maxX) * 0.5,
                (minY + maxY) * 0.5,
                0.0);
            Point3d best = polygon[0];
            double bestDistance = double.MaxValue;
            const int grid = 16;
            for (int x = 1; x < grid; x++)
            {
                for (int y = 1; y < grid; y++)
                {
                    Point3d candidate = new Point3d(
                        minX + (maxX - minX) * x / grid,
                        minY + (maxY - minY) * y / grid,
                        0.0);
                    if (!PointInPolygon(candidate, polygon)) continue;
                    double distance = candidate.DistanceTo(target);
                    if (distance < bestDistance)
                    {
                        best = candidate;
                        bestDistance = distance;
                    }
                }
            }
            return bestDistance < double.MaxValue ? best : centroid;
        }

        private static List<Point3d> Tessellate(IList<SegmentGeometry> segments)
        {
            var points = new List<Point3d>();
            foreach (SegmentGeometry segment in segments)
            {
                if (points.Count == 0) points.Add(segment.Start);
                if (!segment.IsArc)
                {
                    points.Add(segment.End);
                    continue;
                }
                int steps = Math.Max(4, (int)Math.Ceiling(Math.Abs(segment.Sweep) / (Math.PI / 18.0)));
                for (int step = 1; step <= steps; step++)
                {
                    double angle = segment.StartAngle + segment.Sweep * step / steps;
                    points.Add(new Point3d(
                        segment.Center.X + segment.Radius * Math.Cos(angle),
                        segment.Center.Y + segment.Radius * Math.Sin(angle),
                        0.0));
                }
            }
            if (points.Count > 1 && points[0].DistanceTo(points[points.Count - 1]) <= Tolerance)
                points.RemoveAt(points.Count - 1);
            return points;
        }

        private static bool TryPolygonCentroid(IList<Point3d> polygon, out Point3d centroid)
        {
            centroid = Point3d.Origin;
            if (polygon == null || polygon.Count < 3) return false;
            double crossSum = 0.0;
            double x = 0.0;
            double y = 0.0;
            for (int index = 0; index < polygon.Count; index++)
            {
                Point3d first = polygon[index];
                Point3d second = polygon[(index + 1) % polygon.Count];
                double cross = first.X * second.Y - second.X * first.Y;
                crossSum += cross;
                x += (first.X + second.X) * cross;
                y += (first.Y + second.Y) * cross;
            }
            if (Math.Abs(crossSum) <= Tolerance) return false;
            centroid = new Point3d(x / (3.0 * crossSum), y / (3.0 * crossSum), 0.0);
            return true;
        }

        private static bool PointInPolygon(Point3d point, IList<Point3d> polygon)
        {
            bool inside = false;
            for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
            {
                Point3d a = polygon[i];
                Point3d b = polygon[j];
                bool intersects = ((a.Y > point.Y) != (b.Y > point.Y)) &&
                    (point.X < (b.X - a.X) * (point.Y - a.Y) /
                        ((b.Y - a.Y) + 1e-20) + a.X);
                if (intersects) inside = !inside;
            }
            return inside;
        }

        private static string FormatLength(Database database, double drawingLength, string units)
        {
            double metres = drawingLength * DrawingUnitToMetres(database);
            if (string.Equals(units, "Millimetres", StringComparison.OrdinalIgnoreCase))
                return (metres * 1000.0).ToString("0", CultureInfo.InvariantCulture);
            return metres.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static string FormatArea(Database database, double drawingArea, string units)
        {
            double factor = DrawingUnitToMetres(database);
            double squareMetres = drawingArea * factor * factor;
            if (string.Equals(units, "ha", StringComparison.OrdinalIgnoreCase))
                return (squareMetres / 10000.0).ToString("0.####", CultureInfo.InvariantCulture) + " ha";
            if (string.Equals(units, "km²", StringComparison.OrdinalIgnoreCase))
                return (squareMetres / 1000000.0).ToString("0.######", CultureInfo.InvariantCulture) + " km²";
            return squareMetres.ToString("0.###", CultureInfo.InvariantCulture) + " m²";
        }

        private static double DrawingUnitToMetres(Database database)
        {
            string units = database == null ? string.Empty : database.Insunits.ToString();
            if (string.Equals(units, "Millimeters", StringComparison.OrdinalIgnoreCase)) return 0.001;
            if (string.Equals(units, "Centimeters", StringComparison.OrdinalIgnoreCase)) return 0.01;
            if (string.Equals(units, "Meters", StringComparison.OrdinalIgnoreCase)) return 1.0;
            if (string.Equals(units, "Kilometers", StringComparison.OrdinalIgnoreCase)) return 1000.0;
            if (string.Equals(units, "Inches", StringComparison.OrdinalIgnoreCase)) return 0.0254;
            if (string.Equals(units, "Feet", StringComparison.OrdinalIgnoreCase)) return 0.3048;
            if (string.Equals(units, "Yards", StringComparison.OrdinalIgnoreCase)) return 0.9144;
            return 1.0;
        }

        private static string ResolveLayer(Entity source, string requested)
        {
            if (string.Equals(requested, "<Source layer>", StringComparison.OrdinalIgnoreCase))
                return source.Layer;
            return string.IsNullOrWhiteSpace(requested) ? DefaultLayer : requested.Trim();
        }

        private static void EnsureOutputLayer(
            Database database,
            Transaction transaction,
            string requested)
        {
            if (string.Equals(requested, "<Source layer>", StringComparison.OrdinalIgnoreCase))
                return;
            string name = string.IsNullOrWhiteSpace(requested) ? DefaultLayer : requested.Trim();
            LayerTable table = transaction.GetObject(
                database.LayerTableId, OpenMode.ForRead, false) as LayerTable;
            if (table == null || table.Has(name)) return;
            table.UpgradeOpen();
            var record = new LayerTableRecord { Name = name };
            table.Add(record);
            transaction.AddNewlyCreatedDBObject(record, true);
        }

        private static Dictionary<string, List<ObjectId>> ReadOutputMap(
            Transaction transaction,
            BlockTableRecord space)
        {
            var result = new Dictionary<string, List<ObjectId>>(StringComparer.OrdinalIgnoreCase);
            foreach (ObjectId id in space)
            {
                DBObject value;
                try { value = transaction.GetObject(id, OpenMode.ForRead, false); }
                catch { continue; }
                Dictionary<string, string> record;
                if (!TryReadRecord(value, transaction, OutputRecord, out record)) continue;
                string source = Value(record, "Source");
                if (string.IsNullOrWhiteSpace(source)) continue;
                List<ObjectId> ids;
                if (!result.TryGetValue(source, out ids))
                {
                    ids = new List<ObjectId>();
                    result[source] = ids;
                }
                ids.Add(id);
            }
            return result;
        }

        private static void EraseOutputs(Transaction transaction, IEnumerable<ObjectId> ids)
        {
            if (ids == null) return;
            foreach (ObjectId id in ids)
            {
                if (id.IsNull || id.IsErased) continue;
                try
                {
                    DBObject value = transaction.GetObject(id, OpenMode.ForWrite, false);
                    value.Erase(true);
                }
                catch { }
            }
        }

        private static void WriteSourceSettings(
            Entity source,
            Transaction transaction,
            SegmentLabelSettings settings,
            string fingerprint,
            int outputCount)
        {
            WriteRecord(
                source,
                transaction,
                SourceRecord,
                new TypedValue((int)DxfCode.Text, "Schema=1"),
                new TypedValue((int)DxfCode.Text, "LengthUnits=" + settings.LengthUnits),
                new TypedValue((int)DxfCode.Text, "AreaUnits=" + settings.AreaUnits),
                new TypedValue((int)DxfCode.Text, "CurvedText=" + settings.CurvedText),
                new TypedValue((int)DxfCode.Text, "TextHeight=" + Number(settings.TextHeightPaper)),
                new TypedValue((int)DxfCode.Text, "Background=" + (settings.BackgroundMask ? "1" : "0")),
                new TypedValue((int)DxfCode.Text, "Layer=" + settings.Layer),
                new TypedValue((int)DxfCode.Text, "Position=" + settings.Position),
                new TypedValue((int)DxfCode.Text, "Offset=" + Number(settings.OffsetPaper)),
                new TypedValue((int)DxfCode.Text, "Dynamic=" + (settings.Dynamic ? "1" : "0")),
                new TypedValue((int)DxfCode.Text, "Fingerprint=" + fingerprint),
                new TypedValue((int)DxfCode.Text, "OutputCount=" + outputCount.ToString(CultureInfo.InvariantCulture)));
        }

        private static string Fingerprint(
            Database database,
            Entity source,
            IList<SegmentGeometry> segments,
            double? area,
            SegmentLabelSettings settings)
        {
            ulong hash = 1469598103934665603UL;
            Hash(ref hash, source.GetType().FullName);
            Hash(ref hash, source.Layer);
            Hash(ref hash, settings.LengthUnits);
            Hash(ref hash, settings.AreaUnits);
            Hash(ref hash, settings.CurvedText);
            Hash(ref hash, settings.Layer);
            Hash(ref hash, settings.Position);
            Hash(ref hash, settings.BackgroundMask ? "1" : "0");
            Hash(ref hash, PaperAnnotationScale.AnnotativeTextHeight(
                database, settings.TextHeightPaper));
            Hash(ref hash, PaperAnnotationScale.ModelDistance(
                database, Math.Max(settings.OffsetPaper, 0.000001)));
            foreach (SegmentGeometry segment in segments)
            {
                Hash(ref hash, segment.Start.X);
                Hash(ref hash, segment.Start.Y);
                Hash(ref hash, segment.End.X);
                Hash(ref hash, segment.End.Y);
                Hash(ref hash, segment.IsArc ? 1.0 : 0.0);
                Hash(ref hash, segment.Center.X);
                Hash(ref hash, segment.Center.Y);
                Hash(ref hash, segment.Radius);
                Hash(ref hash, segment.Sweep);
            }
            if (area.HasValue) Hash(ref hash, area.Value);
            return hash.ToString("X16", CultureInfo.InvariantCulture);
        }

        private static void Hash(ref ulong hash, string value)
        {
            string text = value ?? string.Empty;
            foreach (char character in text)
            {
                hash ^= character;
                hash *= 1099511628211UL;
            }
        }

        private static void Hash(ref ulong hash, double value)
        {
            Hash(ref hash, Math.Round(value, 9).ToString("R", CultureInfo.InvariantCulture));
        }

        private static void WriteRecord(
            DBObject target,
            Transaction transaction,
            string recordName,
            params TypedValue[] values)
        {
            if (target == null || transaction == null || string.IsNullOrWhiteSpace(recordName)) return;
            if (!target.IsWriteEnabled) target.UpgradeOpen();
            if (target.ExtensionDictionary.IsNull) target.CreateExtensionDictionary();
            DBDictionary dictionary = transaction.GetObject(
                target.ExtensionDictionary, OpenMode.ForWrite, false) as DBDictionary;
            if (dictionary == null) return;

            Xrecord record;
            if (dictionary.Contains(recordName))
                record = transaction.GetObject(
                    dictionary.GetAt(recordName), OpenMode.ForWrite, false) as Xrecord;
            else
            {
                record = new Xrecord();
                dictionary.SetAt(recordName, record);
                transaction.AddNewlyCreatedDBObject(record, true);
            }
            if (record != null) record.Data = new ResultBuffer(values);
        }

        private static bool TryReadRecord(
            DBObject target,
            Transaction transaction,
            string recordName,
            out Dictionary<string, string> values)
        {
            values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (target == null || target.ExtensionDictionary.IsNull) return false;
            DBDictionary dictionary;
            try
            {
                dictionary = transaction.GetObject(
                    target.ExtensionDictionary, OpenMode.ForRead, false) as DBDictionary;
            }
            catch { return false; }
            if (dictionary == null || !dictionary.Contains(recordName)) return false;

            Xrecord record;
            try
            {
                record = transaction.GetObject(
                    dictionary.GetAt(recordName), OpenMode.ForRead, false) as Xrecord;
            }
            catch { return false; }
            if (record == null || record.Data == null) return false;

            foreach (TypedValue item in record.Data)
            {
                string text = item.Value as string;
                if (string.IsNullOrEmpty(text)) continue;
                int separator = text.IndexOf('=');
                if (separator <= 0) continue;
                values[text.Substring(0, separator)] = text.Substring(separator + 1);
            }
            return values.Count > 0;
        }

        private static bool IsSupported(Entity source)
        {
            return source is Line ||
                source is Polyline ||
                (source is CivilFeatureLine && source.GetType() == typeof(CivilFeatureLine));
        }

        private static string Value(IDictionary<string, string> values, string key)
        {
            string value;
            return values != null && values.TryGetValue(key, out value)
                ? value ?? string.Empty
                : string.Empty;
        }

        private static int Integer(IDictionary<string, string> values, string key, int fallback)
        {
            int parsed;
            return int.TryParse(
                Value(values, key),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out parsed)
                ? parsed
                : fallback;
        }

        private static string Number(double value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        private static bool Readable(Vector3d tangent)
        {
            double angle = Math.Atan2(tangent.Y, tangent.X);
            return angle >= -Math.PI * 0.5 && angle <= Math.PI * 0.5;
        }

        private static double NormalizeReadableRotation(double angle)
        {
            while (angle > Math.PI) angle -= Math.PI * 2.0;
            while (angle <= -Math.PI) angle += Math.PI * 2.0;
            if (angle > Math.PI * 0.5) angle -= Math.PI;
            if (angle < -Math.PI * 0.5) angle += Math.PI;
            return angle;
        }

        private static Point3d Plan(Point3d point)
        {
            return new Point3d(point.X, point.Y, 0.0);
        }

        private static Point3d Mid(Point3d first, Point3d second)
        {
            return new Point3d(
                (first.X + second.X) * 0.5,
                (first.Y + second.Y) * 0.5,
                0.0);
        }

        private static Point3d Average(IList<Point3d> points)
        {
            if (points == null || points.Count == 0) return Point3d.Origin;
            double x = 0.0;
            double y = 0.0;
            foreach (Point3d point in points)
            {
                x += point.X;
                y += point.Y;
            }
            return new Point3d(x / points.Count, y / points.Count, 0.0);
        }
    }

    internal sealed class SegmentLabelSettings
    {
        internal string LengthUnits { get; set; }
        internal string AreaUnits { get; set; }
        internal string CurvedText { get; set; }
        internal double TextHeightPaper { get; set; }
        internal bool BackgroundMask { get; set; }
        internal string Layer { get; set; }
        internal string Position { get; set; }
        internal double OffsetPaper { get; set; }
        internal bool Dynamic { get; set; }

        internal static SegmentLabelSettings From(IDictionary<string, string> values)
        {
            if (values == null || values.Count == 0) return null;
            double textHeight;
            double offset;
            if (!double.TryParse(
                    Read(values, "TextHeight"),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out textHeight))
                textHeight = 2.0;
            if (!double.TryParse(
                    Read(values, "Offset"),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out offset))
                offset = 2.0;

            return new SegmentLabelSettings
            {
                LengthUnits = Default(Read(values, "LengthUnits"), "Millimetres"),
                AreaUnits = Default(Read(values, "AreaUnits"), "m²"),
                CurvedText = Default(Read(values, "CurvedText"), "Follow curve"),
                TextHeightPaper = PaperAnnotationScale.NormalizeConfiguredPaperHeight(textHeight),
                BackgroundMask = Read(values, "Background") != "0",
                Layer = Default(Read(values, "Layer"), "CE SEGMENT LABELS"),
                Position = Default(Read(values, "Position"), "Above"),
                OffsetPaper = Math.Max(offset, 0.0),
                Dynamic = Read(values, "Dynamic") != "0"
            };
        }

        private static string Read(IDictionary<string, string> values, string key)
        {
            string value;
            return values.TryGetValue(key, out value) ? value ?? string.Empty : string.Empty;
        }

        private static string Default(string value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }
    }

    internal sealed class SegmentLabelBuildResult
    {
        internal int Sources { get; set; }
        internal int SegmentLabels { get; set; }
        internal int AreaLabels { get; set; }
        internal int Unsupported { get; set; }
        internal int Failed { get; set; }
    }

    internal struct SegmentGeometry
    {
        internal Point3d Start;
        internal Point3d End;
        internal bool IsArc;
        internal Point3d Center;
        internal double Radius;
        internal double StartAngle;
        internal double Sweep;
        internal double Length;
        internal Point3d MidPoint;
        internal Vector3d Tangent;
    }
}

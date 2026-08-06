#!/usr/bin/env python3
"""Integrate the final Civil 3D runtime comment fixes before the next build."""

from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "src" / "CE.Tools.Civil3D"


def read(name: str) -> str:
    return (SRC / name).read_text(encoding="utf-8")


def write(name: str, text: str) -> None:
    (SRC / name).write_text(text, encoding="utf-8")


def replace_once(text: str, old: str, new: str, label: str) -> str:
    if new in text:
        return text
    if old not in text:
        raise SystemExit(f"Could not patch {label}.")
    return text.replace(old, new, 1)


def insert_before(text: str, marker: str, addition: str, label: str) -> str:
    if addition.strip() in text:
        return text
    index = text.find(marker)
    if index < 0:
        raise SystemExit(f"Could not insert {label}.")
    return text[:index] + addition + text[index:]


# ---------------------------------------------------------------------------
# Curve conversion: visible segmented polylines, explicit source retention,
# minimum arc/circle vertices, and no surprising 2D-to-3D auto conversion.
# ---------------------------------------------------------------------------
curve = read("CurveConversionCommands.cs")
curve = replace_once(
    curve,
    '''            model.AddChoice(
                "Keep", "03 Source", "Source objects", "Keep originals",''',
    '''            model.AddPositiveInteger(
                "ArcVertices", "02 Approximation", "Minimum vertices on arcs", 12,
                "Every converted arc receives at least this many visible polyline vertices.");
            model.AddPositiveInteger(
                "CircleVertices", "02 Approximation", "Minimum vertices on circles", 36,
                "Every converted circle receives at least this many visible polyline vertices.");
            model.AddChoice(
                "Keep", "03 Source", "Source objects", "Keep originals",''',
    "curve approximation popup",
)
curve = replace_once(
    curve,
    '''            double maximumSegment = Math.Max(model.Double("Segment", 1.0), 0.001);
            int converted = 0;''',
    '''            double maximumSegment = Math.Max(model.Double("Segment", 1.0), 0.001);
            int minimumArcVertices = Math.Max(model.Integer("ArcVertices", 12), 4);
            int minimumCircleVertices = Math.Max(model.Integer("CircleVertices", 36), 12);
            int converted = 0;''',
    "curve popup values",
)
curve = replace_once(
    curve,
    '''                        Entity output = CreateOutput(source, mode, maximumSegment, flatten);''',
    '''                        Entity output = CreateOutput(
                            source,
                            mode,
                            maximumSegment,
                            minimumArcVertices,
                            minimumCircleVertices,
                            flatten);''',
    "curve output arguments",
)
curve = replace_once(
    curve,
    '''                        output.ColorIndex = 256;
                        output.LinetypeId = source.LinetypeId;
                        output.LineWeight = source.LineWeight;''',
    '''                        try { output.Color = source.Color; }
                        catch { output.ColorIndex = 256; }
                        output.LinetypeId = source.LinetypeId;
                        output.LineWeight = source.LineWeight;
                        try { output.Transparency = source.Transparency; } catch { }''',
    "curve source display",
)
curve = replace_once(
    curve,
    '''        private static Entity CreateOutput(Entity source, string mode, double maximumSegment, bool flatten)
        {
            bool to3d = string.Equals(mode, "Polylines to 3D polylines", StringComparison.OrdinalIgnoreCase) ||
                (string.Equals(mode, "Auto-detect selected objects", StringComparison.OrdinalIgnoreCase) && (source is Polyline || source is Polyline2d));
            List<Point3d> points = Sample(source, maximumSegment);''',
    '''        private static Entity CreateOutput(
            Entity source,
            string mode,
            double maximumSegment,
            int minimumArcVertices,
            int minimumCircleVertices,
            bool flatten)
        {
            // Auto-detect converts supported non-polyline curves to visible 2D
            // polylines. Existing 2D polylines are changed to 3D only when the
            // user explicitly selects the Polylines-to-3D mode.
            bool to3d = string.Equals(
                mode,
                "Polylines to 3D polylines",
                StringComparison.OrdinalIgnoreCase);
            List<Point3d> points = Sample(
                source,
                maximumSegment,
                minimumArcVertices,
                minimumCircleVertices);''',
    "curve output method",
)
curve = replace_once(
    curve,
    '''        private static List<Point3d> Sample(Entity entity, double maximumSegment)
        {''',
    '''        private static List<Point3d> Sample(
            Entity entity,
            double maximumSegment,
            int minimumArcVertices,
            int minimumCircleVertices)
        {''',
    "curve sample signature",
)
curve = curve.replace(
    "AddSamples(lightweight, maximumSegment, points, true);",
    "AddSamples(lightweight, maximumSegment, minimumArcVertices, minimumCircleVertices, points, true);",
)
curve = curve.replace(
    "AddSamples(curve, maximumSegment, points, true);",
    "AddSamples(curve, maximumSegment, minimumArcVertices, minimumCircleVertices, points, true);",
)
curve = replace_once(
    curve,
    '''        private static void AddSamples(Curve curve, double maximumSegment, IList<Point3d> points, bool includeStart)
        {''',
    '''        private static void AddSamples(
            Curve curve,
            double maximumSegment,
            int minimumArcVertices,
            int minimumCircleVertices,
            IList<Point3d> points,
            bool includeStart)
        {''',
    "curve sample method",
)
curve = replace_once(
    curve,
    '''            if (curve is Circle) segments = Math.Max(segments, 24);
            if (curve is Arc) segments = Math.Max(segments, 4);''',
    '''            if (curve is Circle)
                segments = Math.Max(segments, Math.Max(minimumCircleVertices, 12));
            if (curve is Arc)
                segments = Math.Max(segments, Math.Max(minimumArcVertices, 4));''',
    "curve minimum vertices",
)
write("CurveConversionCommands.cs", curve)


# ---------------------------------------------------------------------------
# Vertex setting-out: display-only X/Y order/sign controls, closed-filled
# leaders/dimensions, bounded offsets, and persisted popup settings.
# ---------------------------------------------------------------------------
vertex = read("VertexSettingOutCommands.cs")
vertex = replace_once(
    vertex,
    '''            settings.AddPositiveDouble(
                "Offset", "03 Annotation", "MText/MLeader offset", 3.0,
                "Drawing-unit offset from each setting-out point to its annotation.");
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;''',
    '''            settings.AddPositiveDouble(
                "Offset", "03 Annotation", "MText/MLeader offset", 3.0,
                "Drawing-unit offset from each setting-out point to its annotation.");
            settings.AddChoice(
                "CoordinateOrder", "04 Coordinate Display", "Coordinate order", "X then Y",
                "Change only the annotation and table display order. The true drawing coordinates remain unchanged.",
                new[] { "X then Y", "Y then X" });
            settings.AddChoice(
                "XSign", "04 Coordinate Display", "Displayed X sign", "Keep X sign",
                "Keep or reverse the displayed X sign without changing the COGO point or source geometry.",
                new[] { "Keep X sign", "Reverse X sign" });
            settings.AddChoice(
                "YSign", "04 Coordinate Display", "Displayed Y sign", "Keep Y sign",
                "Keep or reverse the displayed Y sign without changing the COGO point or source geometry.",
                new[] { "Keep Y sign", "Reverse Y sign" });
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;''',
    "vertex coordinate popup",
)
vertex = replace_once(
    vertex,
    '''            string elevationMode = settings.Text("Elevation");
            ObjectId elevationSourceId;''',
    '''            string elevationMode = settings.Text("Elevation");
            string coordinateOrder = settings.Text("CoordinateOrder");
            string xSign = settings.Text("XSign");
            string ySign = settings.Text("YSign");
            ObjectId elevationSourceId;''',
    "vertex coordinate settings",
)
vertex = replace_once(
    vertex,
    '''                ElevationMode = elevationMode,
                ElevationSourceHandle = elevationSourceId.IsNull''',
    '''                ElevationMode = elevationMode,
                CoordinateOrder = coordinateOrder,
                XSign = xSign,
                YSign = ySign,
                ElevationSourceHandle = elevationSourceId.IsNull''',
    "vertex coordinate link",
)
vertex = replace_once(
    vertex,
    '''                document.Editor.SetImpliedSelection(new[] { tableId });
                document.Editor.Regen();''',
    '''                document.Editor.SetImpliedSelection(new[] { tableId });
                RuntimeAnnotationLinkManager.ClampLinkedAnnotations(document, true);
                document.Editor.Regen();''',
    "vertex post-create repair",
)
vertex = replace_once(
    vertex,
    '''                RefreshTable(document, selected.ObjectId, out points, out dimensions);
                document.Editor.Regen();''',
    '''                RefreshTable(document, selected.ObjectId, out points, out dimensions);
                RuntimeAnnotationLinkManager.ClampLinkedAnnotations(document, true);
                document.Editor.Regen();''',
    "vertex post-refresh repair",
)
vertex = vertex.replace(
    "PopulateTable(table, records, textHeight, link.OutputType);",
    "PopulateTable(table, records, textHeight, link);",
)
vertex = vertex.replace("text.Contents = LabelText(record);", "text.Contents = LabelText(record, link);")
vertex = vertex.replace("mtext.Contents = LabelText(record);", "mtext.Contents = LabelText(record, link);")
vertex = replace_once(
    vertex,
    '''                leader.MLeaderStyle = database.MLeaderstyle;
                leader.ArrowSymbolId = ObjectId.Null;''',
    '''                leader.MLeaderStyle = database.MLeaderstyle;
                // ObjectId.Null is AutoCAD's native closed-filled arrow. Use the
                // drawing's configured dimension arrow when one is available.
                leader.ArrowSymbolId = database.Dimblk.IsNull
                    ? ObjectId.Null
                    : database.Dimblk;''',
    "vertex closed leader",
)
vertex = replace_once(
    vertex,
    '''            radial.SetDatabaseDefaults(database);
            PaperAnnotationScale.SetAnnotative(radial);''',
    '''            radial.SetDatabaseDefaults(database);
            SetClosedFilledDimensionArrow(radial, database);
            PaperAnnotationScale.SetAnnotative(radial);''',
    "radial arrow creation",
)
vertex = replace_once(
    vertex,
    '''            radial.LeaderLength = Math.Max(textHeight * 3.0, dimension.Radius * 0.15);
            return true;''',
    '''            radial.LeaderLength = Math.Max(textHeight * 3.0, dimension.Radius * 0.15);
            SetClosedFilledDimensionArrow(radial, radial.Database);
            return true;''',
    "radial arrow refresh",
)
vertex = replace_once(
    vertex,
    '''        private static void PopulateTable(
            Table table,
            IList<VertexSettingRecord> records,
            double textHeight,
            string outputType)''',
    '''        private static void PopulateTable(
            Table table,
            IList<VertexSettingRecord> records,
            double textHeight,
            VertexSettingLink link)''',
    "vertex table signature",
)
vertex = vertex.replace(
    'table.Cells[0, 0].TextString = "CE VERTEX SETTING-OUT - " + outputType.ToUpperInvariant();',
    'table.Cells[0, 0].TextString = "CE VERTEX SETTING-OUT - " + (link.OutputType ?? string.Empty).ToUpperInvariant();',
)
vertex = replace_once(
    vertex,
    '''            string[] headings =
            {
                "POINT NAME", "TYPE", "SOURCE", "SEGMENT", "X", "Y", "Z", "RADIUS", "SEGMENT LENGTH"
            };''',
    '''            bool yFirst = string.Equals(
                link.CoordinateOrder,
                "Y then X",
                StringComparison.OrdinalIgnoreCase);
            string[] headings =
            {
                "POINT NAME", "TYPE", "SOURCE", "SEGMENT",
                yFirst ? "Y" : "X",
                yFirst ? "X" : "Y",
                "Z", "RADIUS", "SEGMENT LENGTH"
            };''',
    "vertex table headings",
)
vertex = replace_once(
    vertex,
    '''                table.Cells[row, 4].TextString = record.Point.X.ToString("N3", CultureInfo.CurrentCulture);
                table.Cells[row, 5].TextString = record.Point.Y.ToString("N3", CultureInfo.CurrentCulture);''',
    '''                double displayX = DisplayX(record.Point, link);
                double displayY = DisplayY(record.Point, link);
                table.Cells[row, 4].TextString = (yFirst ? displayY : displayX)
                    .ToString("N3", CultureInfo.CurrentCulture);
                table.Cells[row, 5].TextString = (yFirst ? displayX : displayY)
                    .ToString("N3", CultureInfo.CurrentCulture);''',
    "vertex table coordinates",
)
old_label = '''        private static string LabelText(VertexSettingRecord record)
        {
            return string.Join(
                "\\\\P",
                record.PointName,
                "X=" + record.Point.X.ToString("N3", CultureInfo.CurrentCulture),
                "Y=" + record.Point.Y.ToString("N3", CultureInfo.CurrentCulture),
                "Z=" + record.Point.Z.ToString("N3", CultureInfo.CurrentCulture));
        }
'''
new_label = '''        private static string LabelText(
            VertexSettingRecord record,
            VertexSettingLink link)
        {
            double displayX = DisplayX(record.Point, link);
            double displayY = DisplayY(record.Point, link);
            bool yFirst = string.Equals(
                link.CoordinateOrder,
                "Y then X",
                StringComparison.OrdinalIgnoreCase);
            string first = (yFirst ? "Y=" : "X=") +
                (yFirst ? displayY : displayX)
                    .ToString("N3", CultureInfo.CurrentCulture);
            string second = (yFirst ? "X=" : "Y=") +
                (yFirst ? displayX : displayY)
                    .ToString("N3", CultureInfo.CurrentCulture);
            return string.Join(
                "\\\\P",
                record.PointName,
                first,
                second,
                "Z=" + record.Point.Z.ToString("N3", CultureInfo.CurrentCulture));
        }

        private static double DisplayX(
            Point3d point,
            VertexSettingLink link)
        {
            return string.Equals(
                link.XSign,
                "Reverse X sign",
                StringComparison.OrdinalIgnoreCase)
                ? -point.X
                : point.X;
        }

        private static double DisplayY(
            Point3d point,
            VertexSettingLink link)
        {
            return string.Equals(
                link.YSign,
                "Reverse Y sign",
                StringComparison.OrdinalIgnoreCase)
                ? -point.Y
                : point.Y;
        }

        private static void SetClosedFilledDimensionArrow(
            Dimension dimension,
            Database database)
        {
            if (dimension == null || database == null) return;
            ObjectId arrow = database.Dimblk.IsNull
                ? ObjectId.Null
                : database.Dimblk;
            foreach (string name in new[] { "Dimblk", "Dimblk1", "Dimblk2" })
            {
                try
                {
                    PropertyInfo property = dimension.GetType().GetProperty(
                        name,
                        BindingFlags.Public | BindingFlags.Instance);
                    if (property == null || !property.CanWrite ||
                        property.PropertyType != typeof(ObjectId)) continue;
                    property.SetValue(dimension, arrow, null);
                }
                catch { }
            }
        }
'''
if new_label not in vertex:
    if old_label not in vertex:
        raise SystemExit("Could not patch vertex label display.")
    vertex = vertex.replace(old_label, new_label, 1)
vertex = replace_once(
    vertex,
    '''            Vector3d offset = record.AnnotationOffset ??
                new Vector3d(defaultOffset, defaultOffset, 0.0);
            return record.Point + offset;''',
    '''            Vector3d offset = record.AnnotationOffset ??
                new Vector3d(defaultOffset, defaultOffset, 0.0);
            double maximum = Math.Max(defaultOffset * 5.0, defaultOffset);
            if (offset.Length > maximum)
                offset = offset.GetNormal() * maximum;
            return record.Point + offset;''',
    "vertex bounded output offset",
)
vertex = replace_once(
    vertex,
    '''            values.Add(new TypedValue(
                (int)DxfCode.ExtendedDataAsciiString,
                "ELEVHANDLE=" + (link.ElevationSourceHandle ?? string.Empty)));''',
    '''            values.Add(new TypedValue(
                (int)DxfCode.ExtendedDataAsciiString,
                "ELEVHANDLE=" + (link.ElevationSourceHandle ?? string.Empty)));
            values.Add(new TypedValue(
                (int)DxfCode.ExtendedDataAsciiString,
                "ORDER=" + (link.CoordinateOrder ?? "X then Y")));
            values.Add(new TypedValue(
                (int)DxfCode.ExtendedDataAsciiString,
                "XSIGN=" + (link.XSign ?? "Keep X sign")));
            values.Add(new TypedValue(
                (int)DxfCode.ExtendedDataAsciiString,
                "YSIGN=" + (link.YSign ?? "Keep Y sign")));''',
    "vertex link coordinate serialization",
)
vertex = replace_once(
    vertex,
    '''                ElevationMode = "Source geometry",
                ElevationSourceHandle = string.Empty,
                SourceHandles = new List<string>()''',
    '''                ElevationMode = "Source geometry",
                ElevationSourceHandle = string.Empty,
                CoordinateOrder = "X then Y",
                XSign = "Keep X sign",
                YSign = "Keep Y sign",
                SourceHandles = new List<string>()''',
    "vertex link defaults",
)
vertex = replace_once(
    vertex,
    '''                else if (value.StartsWith("SRC=", StringComparison.OrdinalIgnoreCase))
                    link.SourceHandles.Add(value.Substring(4));''',
    '''                else if (value.StartsWith("ORDER=", StringComparison.OrdinalIgnoreCase))
                    link.CoordinateOrder = value.Substring(6);
                else if (value.StartsWith("XSIGN=", StringComparison.OrdinalIgnoreCase))
                    link.XSign = value.Substring(6);
                else if (value.StartsWith("YSIGN=", StringComparison.OrdinalIgnoreCase))
                    link.YSign = value.Substring(6);
                else if (value.StartsWith("SRC=", StringComparison.OrdinalIgnoreCase))
                    link.SourceHandles.Add(value.Substring(4));''',
    "vertex link coordinate reader",
)
vertex = replace_once(
    vertex,
    '''            public string ElevationMode { get; set; }
            public string ElevationSourceHandle { get; set; }
            public IList<string> SourceHandles { get; set; }''',
    '''            public string ElevationMode { get; set; }
            public string ElevationSourceHandle { get; set; }
            public string CoordinateOrder { get; set; }
            public string XSign { get; set; }
            public string YSign { get; set; }
            public IList<string> SourceHandles { get; set; }''',
    "vertex link fields",
)
write("VertexSettingOutCommands.cs", vertex)


# ---------------------------------------------------------------------------
# Project style preset: wait for quiescence and hold a DocumentLock for every
# active-DWG write, preventing eLockViolation when applying saved styles.
# ---------------------------------------------------------------------------
preset = read("ProjectStylePresetManager.cs")
preset = replace_once(
    preset,
    '''            Dictionary<string, ProjectStyleSelection> presets = LoadPresets();
            presets[selection.Discipline] = Clone(selection);
            SavePresets(presets);
            WriteDisciplineSelection(document.Database, selection);
            SynchronizeDisciplineSettings(document.Database, selection);
            return true;''',
    '''            Dictionary<string, ProjectStyleSelection> presets = LoadPresets();
            presets[selection.Discipline] = Clone(selection);
            SavePresets(presets);
            try
            {
                using (DocumentLock documentLock = document.LockDocument())
                {
                    WriteDisciplineSelection(document.Database, selection);
                    SynchronizeDisciplineSettings(document.Database, selection);
                }
                return true;
            }
            catch
            {
                QueueDocument(document);
                return false;
            }''',
    "project style save lock",
)
preset = replace_once(
    preset,
    '''            WriteActiveSelection(document.Database, selected);
            foreach (ProjectStyleSelection preset in presets.Values)
                WriteDisciplineSelection(document.Database, preset);
            SynchronizeDisciplineSettings(document.Database, selected);
            document.Editor.Regen();''',
    '''            try
            {
                using (DocumentLock documentLock = document.LockDocument())
                {
                    WriteActiveSelection(document.Database, selected);
                    foreach (ProjectStyleSelection preset in presets.Values)
                        WriteDisciplineSelection(document.Database, preset);
                    SynchronizeDisciplineSettings(document.Database, selected);
                }
            }
            catch
            {
                QueueDocument(document);
                return false;
            }
            document.Editor.Regen();''',
    "project style apply lock",
)
preset = replace_once(
    preset,
    '''                if (window.Choice == ProjectStyleOpeningChoice.UseSaved)
                    ApplySavedPreset(document, true);
                else''',
    '''                if (window.Choice == ProjectStyleOpeningChoice.UseSaved)
                {
                    if (!ApplySavedPreset(document, true))
                    {
                        PromptedDocuments.Remove(document);
                        QueueDocument(document);
                    }
                }
                else''',
    "project style failed apply requeue",
)
preset = replace_once(
    preset,
    '''            Document active = AcApplication.DocumentManager.MdiActiveDocument;
            if (active == null) return;

            int count = PendingDocuments.Count;''',
    '''            Document active = AcApplication.DocumentManager.MdiActiveDocument;
            if (active == null) return;
            string commandNames = Convert.ToString(
                AcApplication.GetSystemVariable("CMDNAMES"),
                CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(commandNames)) return;
            object commandActive = AcApplication.GetSystemVariable("CMDACTIVE");
            if (Convert.ToInt32(commandActive, CultureInfo.InvariantCulture) != 0) return;

            int count = PendingDocuments.Count;''',
    "project style quiescent prompt",
)
write("ProjectStylePresetManager.cs", preset)


# ---------------------------------------------------------------------------
# COGO labels: keep descriptions visible, reject invalid/huge stored offsets,
# keep overlap moves close, and delegate automatic updates to the central manager.
# ---------------------------------------------------------------------------
cogo = read("CogoPointProjectStyleCommands.cs")
cogo = replace_once(
    cogo,
    '''                        Point3d anchor = PointLocation(point);
                        Vector3d stored;''',
    '''                        if (string.IsNullOrWhiteSpace(point.RawDescription))
                            point.RawDescription = string.IsNullOrWhiteSpace(point.PointName)
                                ? "P" + point.PointNumber.ToString(CultureInfo.InvariantCulture)
                                : point.PointName;
                        TrySetLabelVisible(point);
                        Point3d anchor = PointLocation(point);
                        Vector3d stored;''',
    "COGO visible description",
)
cogo = replace_once(
    cogo,
    '''                        if (TryReadOffset(point, transaction, out stored))
                        {
                            try
                            {
                                point.LabelLocation = anchor + stored;''',
    '''                        if (TryReadOffset(point, transaction, out stored))
                        {
                            stored = NormalizeOffset(stored, database);
                            try
                            {
                                point.LabelLocation = anchor + stored;''',
    "COGO bounded stored offset",
)
cogo = replace_once(
    cogo,
    '''            if (!labelStyleId.IsNull)
            {
                try { point.LabelStyleId = labelStyleId; } catch { }
            }
        }''',
    '''            if (!labelStyleId.IsNull)
            {
                try { point.LabelStyleId = labelStyleId; } catch { }
            }
            if (string.IsNullOrWhiteSpace(point.RawDescription))
                point.RawDescription = string.IsNullOrWhiteSpace(point.PointName)
                    ? "P" + point.PointNumber.ToString(CultureInfo.InvariantCulture)
                    : point.PointName;
            TrySetLabelVisible(point);
        }''',
    "COGO point creation visibility",
)
cogo = cogo.replace("for (int ring = 1; ring <= 10; ring++)", "for (int ring = 1; ring <= 5; ring++)")
cogo = replace_once(
    cogo,
    '''            double distance = PaperAnnotationScale.ModelDistance(database, 5.0);
            return new Vector3d(distance, distance, 0.0);''',
    '''            double distance = PaperAnnotationScale.ModelDistance(database, 5.0);
            return NormalizeOffset(
                new Vector3d(distance, distance, 0.0),
                database);''',
    "COGO default offset",
)
helpers = '''
        private static Vector3d NormalizeOffset(
            Vector3d offset,
            Database database)
        {
            double fallback = Math.Max(
                PaperAnnotationScale.ModelDistance(database, 5.0),
                0.001);
            double maximum = Math.Max(
                PaperAnnotationScale.ModelDistance(database, 15.0),
                fallback * 2.0);
            if (double.IsNaN(offset.X) || double.IsInfinity(offset.X) ||
                double.IsNaN(offset.Y) || double.IsInfinity(offset.Y) ||
                offset.Length < fallback * 0.1)
                return new Vector3d(fallback, fallback, 0.0);
            return offset.Length > maximum
                ? offset.GetNormal() * maximum
                : new Vector3d(offset.X, offset.Y, 0.0);
        }

        private static void TrySetLabelVisible(CivilCogoPoint point)
        {
            if (point == null) return;
            foreach (string name in new[]
            {
                "LabelVisibility", "LabelVisible", "ShowLabel"
            })
            {
                try
                {
                    System.Reflection.PropertyInfo property = point.GetType().GetProperty(
                        name,
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.Instance);
                    if (property == null || !property.CanWrite) continue;
                    if (property.PropertyType == typeof(bool))
                    {
                        property.SetValue(point, true, null);
                        return;
                    }
                }
                catch { }
            }
        }

'''
cogo = insert_before(
    cogo,
    "        private static bool TryReadOffset(",
    helpers,
    "COGO offset helpers",
)
cogo = replace_once(
    cogo,
    '''        public static void Queue()
        {
            _pending = true;
        }''',
    '''        public static void Queue()
        {
            _pending = true;
            UniversalDynamicRefreshManager.Queue();
        }''',
    "COGO central queue",
)
cogo = replace_once(
    cogo,
    '''            _busy = true;
            try
            {
                CogoPointProjectStyleCommands.ApplySelectedStyles(document, true);
                _pending = false;
                _lastRunUtc = DateTime.UtcNow;''',
    '''            _busy = true;
            try
            {
                // The universal manager owns automatic mutation. This legacy
                // watcher only forwards the request, preventing duplicate idle
                // transactions and crosshair flicker.
                UniversalDynamicRefreshManager.Queue();
                _pending = false;
                _lastRunUtc = DateTime.UtcNow;''',
    "COGO duplicate refresh removal",
)
write("CogoPointProjectStyleCommands.cs", cogo)


# ---------------------------------------------------------------------------
# Generic presentation: bind overlap correction back to each real linked anchor
# and let the central refresh manager own automatic updates.
# ---------------------------------------------------------------------------
presentation = read("CommentPresentationCommands.cs")
presentation = replace_once(
    presentation,
    '''            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\\nCE_OVERLAPFIX complete.''',
    '''            RuntimeAnnotationLinkManager.ClampLinkedAnnotations(document, true);
            UniversalDynamicRefreshManager.Queue();
            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\\nCE_OVERLAPFIX complete.''',
    "bounded generic overlap",
)
presentation = replace_once(
    presentation,
    '''        public static void MarkPending()
        {
            _pending = true;
            LinkedTableAutoRefreshManager.Queue(''',
    '''        public static void MarkPending()
        {
            _pending = true;
            UniversalDynamicRefreshManager.Queue();
            LinkedTableAutoRefreshManager.Queue(''',
    "presentation central queue",
)
presentation = replace_once(
    presentation,
    '''            if (!Enabled || !_pending || _busy || document == null) return;
            if ((DateTime.UtcNow - _lastRunUtc).TotalMilliseconds < 800.0) return;''',
    '''            if (!Enabled || !_pending || _busy || document == null) return;
            if (UniversalDynamicRefreshManager.Enabled)
            {
                UniversalDynamicRefreshManager.Queue();
                _pending = false;
                return;
            }
            if ((DateTime.UtcNow - _lastRunUtc).TotalMilliseconds < 800.0) return;''',
    "presentation duplicate idle removal",
)
write("CommentPresentationCommands.cs", presentation)


# ---------------------------------------------------------------------------
# Sewer labels: selected Civil style remains authoritative; remove custom text
# overrides, keep labels near their part, and cap collision movements.
# ---------------------------------------------------------------------------
sewer_labels = read("SewerLabelStyleSyncCommands.cs")
method_start = sewer_labels.find("        private static void ApplyPipeLabelPresentation(")
method_end = sewer_labels.find("        private static List<ObjectId> ReadTextComponentIds", method_start)
if method_start < 0 or method_end < 0:
    raise SystemExit("Could not locate pipe label presentation method.")
replacement = '''        private static void ApplyPipeLabelPresentation(
            object label,
            Transaction transaction)
        {
            // The selected Civil 3D label style remains authoritative. Clear old
            // text overrides and repair dragged-state/plan-readability instead of
            // replacing the office style with a hard-coded three-line label.
            SewerPlanLabelRuntimeManager.ConfigureLabel(label, transaction);
        }

'''
sewer_labels = sewer_labels[:method_start] + replacement + sewer_labels[method_end:]
sewer_labels = sewer_labels.replace("for (int attempt = 1; attempt <= 8; attempt++)", "for (int attempt = 1; attempt <= 4; attempt++)")
sewer_labels = replace_once(
    sewer_labels,
    '''                document.Editor.Regen();
                result.OverlapsMoved = ResolveOverlaps(document);
                document.Editor.Regen();''',
    '''                document.Editor.Regen();
                result.OverlapsMoved = ResolveOverlaps(document);
                SewerPlanLabelRuntimeManager.Apply(document);
                document.Editor.Regen();''',
    "sewer runtime label presentation",
)
write("SewerLabelStyleSyncCommands.cs", sewer_labels)


# ---------------------------------------------------------------------------
# Sewer resequencing: temporary collision-safe names before branch compaction;
# delegate automatic execution to the universal manager.
# ---------------------------------------------------------------------------
sewer_dynamic = read("SewerNetworkDynamicSequenceManager.cs")
sewer_dynamic = replace_once(
    sewer_dynamic,
    '''            int pipes = 0;
            int structures = 0;
            for (int branchIndex = 0; branchIndex < branches.Count; branchIndex++)''',
    '''            int pipes = 0;
            int structures = 0;
            // Civil 3D enforces unique part names immediately. Rename every live
            // part to a collision-safe temporary value before compacting Branch-4
            // to Branch-3 or rebuilding a reconnected sequence.
            string token = Guid.NewGuid().ToString("N");
            int temporary = 1;
            foreach (ObjectId pipeId in graph.Pipes.Keys.OrderBy(id => id.Handle.Value))
            {
                CivilPipe pipe = transaction.GetObject(
                    pipeId,
                    OpenMode.ForWrite,
                    false) as CivilPipe;
                if (pipe != null)
                    pipe.Name = "CE_TMP_PIPE_" + token + "_" +
                        temporary++.ToString(CultureInfo.InvariantCulture);
            }
            foreach (ObjectId structureId in graph.Structures.Keys.OrderBy(id => id.Handle.Value))
            {
                CivilStructure structure = transaction.GetObject(
                    structureId,
                    OpenMode.ForWrite,
                    false) as CivilStructure;
                if (structure != null)
                    structure.Name = "CE_TMP_MH_" + token + "_" +
                        temporary++.ToString(CultureInfo.InvariantCulture);
            }

            for (int branchIndex = 0; branchIndex < branches.Count; branchIndex++)''',
    "sewer temporary names",
)
sewer_dynamic = replace_once(
    sewer_dynamic,
    '''        internal static void Queue()
        {
            _pending = true;
            _lastChangeUtc = DateTime.UtcNow;
        }''',
    '''        internal static void Queue()
        {
            _pending = true;
            _lastChangeUtc = DateTime.UtcNow;
            UniversalDynamicRefreshManager.Queue();
        }''',
    "sewer central queue",
)
sewer_dynamic = replace_once(
    sewer_dynamic,
    '''            if (!_pending || _busy || document == null) return;
            if ((DateTime.UtcNow - _lastChangeUtc).TotalMilliseconds < 1200.0) return;''',
    '''            if (!_pending || _busy || document == null) return;
            if (UniversalDynamicRefreshManager.Enabled)
            {
                UniversalDynamicRefreshManager.Queue();
                _pending = false;
                return;
            }
            if ((DateTime.UtcNow - _lastChangeUtc).TotalMilliseconds < 1200.0) return;''',
    "sewer duplicate idle removal",
)
write("SewerNetworkDynamicSequenceManager.cs", sewer_dynamic)


# ---------------------------------------------------------------------------
# Universal refresh: one central deferred cycle for all linked annotation,
# Civil labels, sewer topology and profile-view band data.
# ---------------------------------------------------------------------------
universal = read("UniversalDynamicRefreshCommands.cs")
universal = universal.replace("internal static double DelaySeconds { get; set; } = 1.2;", "internal static double DelaySeconds { get; set; } = 1.8;")
universal = replace_once(
    universal,
    '''                try { CogoPointProjectStyleCommands.ApplySelectedStyles(document, true); }
                catch { result.Warnings++; }
                try { SewerNetworkDynamicSequenceCommands.ResequenceAll(document, false); }''',
    '''                try { CogoPointProjectStyleCommands.ApplySelectedStyles(document, true); }
                catch { result.Warnings++; }
                try { RuntimeAnnotationLinkManager.ClampLinkedAnnotations(document, true); }
                catch { result.Warnings++; }
                try { SewerNetworkDynamicSequenceCommands.ResequenceAll(document, false); }''',
    "universal annotation runtime",
)
universal = replace_once(
    universal,
    '''                try { result.JunctionLabels += RoadJunctionCompletionCommands.RefreshAll(document); }
                catch { result.Warnings++; }
                try { result.MetadataAttributes += ProductionMetadataDynamicManager.Refresh(document); }''',
    '''                try { result.JunctionLabels += RoadJunctionCompletionCommands.RefreshAll(document); }
                catch { result.Warnings++; }
                try { SewerPlanLabelRuntimeManager.Apply(document); }
                catch { result.Warnings++; }
                try { ProfileViewBandRuntimeManager.RefreshAll(document); }
                catch { result.Warnings++; }
                try { result.MetadataAttributes += ProductionMetadataDynamicManager.Refresh(document); }''',
    "universal label and band runtime",
)
universal = replace_once(
    universal,
    '''            string commands = Convert.ToString(AcApplication.GetSystemVariable("CMDNAMES"), CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(commands)) return;''',
    '''            string commands = Convert.ToString(AcApplication.GetSystemVariable("CMDNAMES"), CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(commands)) return;
            int commandActive = Convert.ToInt32(
                AcApplication.GetSystemVariable("CMDACTIVE"),
                CultureInfo.InvariantCulture);
            if (commandActive != 0) return;''',
    "universal quiescent check",
)
write("UniversalDynamicRefreshCommands.cs", universal)


# ---------------------------------------------------------------------------
# Sewer profile production: immediately link the selected band style and live
# network/profile sources after profile views are created.
# ---------------------------------------------------------------------------
sewer_production = read("SewerProductionCommands.cs")
sewer_production = replace_once(
    sewer_production,
    '''                editor.WriteMessage(
                    "\\nCE_SEWPROFILE complete. Surface profiles: {0}; profile views: {1}; network parts added where supported: {2}.",''',
    '''                ProfileViewBandRuntimeManager.RefreshAll(document);
                editor.WriteMessage(
                    "\\nCE_SEWPROFILE complete. Surface profiles: {0}; profile views: {1}; network parts added where supported: {2}.",''',
    "sewer profile band linking",
)
write("SewerProductionCommands.cs", sewer_production)


# ---------------------------------------------------------------------------
# Ribbon: surface the final repair centre and direct maintenance commands in the
# already-organised Survey and Production panels.
# ---------------------------------------------------------------------------
ribbon = read("PluginEntry.cs")
ribbon = replace_once(
    ribbon,
    '''                        Cmd("Resolve COGO Label Overlaps", "CE_COGOOVERLAPFIX ", "Move COGO labels only while keeping survey point coordinates fixed."),
                        Cmd("Convert Curves and Polylines", "CE_CURVECONVERT ", "Convert lines, arcs, circles, splines, lightweight and 3D polylines through one popup.")),''',
    '''                        Cmd("Resolve COGO Label Overlaps", "CE_COGOOVERLAPFIX ", "Move COGO labels only while keeping survey point coordinates fixed."),
                        Cmd("Repair All Linked Annotations", "CE_ANNOTATIONLINKREPAIR ", "Refresh and re-anchor COGO, MText, MLeader, tables and radius dimensions close to their true source points."),
                        Cmd("Convert Curves and Polylines", "CE_CURVECONVERT ", "Convert lines, arcs, circles, splines, lightweight and 3D polylines through one popup.")),''',
    "survey runtime ribbon",
)
ribbon = replace_once(
    ribbon,
    '''                        Cmd("Production Tools", "CE_REPORTTOOLS ", "Open reports, summaries and drawing-book workflows."),''',
    '''                        Cmd("Finish Runtime Comments", "CE_RUNTIMEFINISH ", "Open the final annotation, sewer-label and profile-band completion centre before building."),
                        Cmd("Apply Sewer Label Presentation", "CE_PIPELABELPRESENTATION ", "Apply selected pipe/structure label styles, flow arrows, plan readability and bounded offsets."),
                        Cmd("Batch Profile View Band Sets", "CE_PROFILEBANDSBATCH ", "Apply and link multiple band sets across selected profile views."),
                        Cmd("Refresh Profile View Bands", "CE_PROFILEBANDREFRESH ", "Refresh live profile/network band data at structures and manholes."),
                        Cmd("Production Tools", "CE_REPORTTOOLS ", "Open reports, summaries and drawing-book workflows."),''',
    "production runtime ribbon",
)
write("PluginEntry.cs", ribbon)

print("Pre-build runtime completion integration applied.")

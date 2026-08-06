#!/usr/bin/env python3
"""Wire dynamic COGO styles, vertex anchors and sewer resequencing into CE Tools."""

from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]
CIVIL = ROOT / "src" / "CE.Tools.Civil3D"
PLUGIN = CIVIL / "PluginEntry.cs"
PROJECT = CIVIL / "ProjectStyleCenterCommands.cs"
VERTEX = CIVIL / "VertexSettingOutCommands.cs"
GEOMETRY = CIVIL / "VertexSettingOutGeometry.cs"
SURVEY = CIVIL / "SurveyCoordinateWorkflowCommands.cs"
REFRESH = CIVIL / "CommentPresentationCommands.cs"
COGO = CIVIL / "CogoPointProjectStyleCommands.cs"
SEWER = CIVIL / "SewerNetworkDynamicSequenceManager.cs"


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig")


def write(path: Path, text: str) -> None:
    path.write_text(text, encoding="utf-8")


def replace_once(path: Path, old: str, new: str) -> None:
    text = read(path)
    if new in text:
        return
    if old not in text:
        raise SystemExit(f"Expected integration marker was not found in {path}: {old[:90]!r}")
    write(path, text.replace(old, new, 1))


def regex_once(path: Path, pattern: str, replacement: str) -> None:
    text = read(path)
    updated, count = re.subn(pattern, replacement, text, count=1, flags=re.S)
    if count != 1:
        raise SystemExit(f"Expected one regex match in {path}; found {count}: {pattern[:100]}")
    write(path, updated)


# Application lifecycle.
replace_once(
    PLUGIN,
    "            ProjectStylePresetManager.Initialize();\n            AcApplication.Idle += OnApplicationIdle;",
    "            ProjectStylePresetManager.Initialize();\n"
    "            CogoPointProjectStyleManager.Initialize();\n"
    "            SewerNetworkDynamicSequenceManager.Initialize();\n"
    "            AcApplication.Idle += OnApplicationIdle;",
)
replace_once(
    PLUGIN,
    "            AcApplication.Idle -= OnApplicationIdle;\n            ProjectStylePresetManager.Terminate();",
    "            AcApplication.Idle -= OnApplicationIdle;\n"
    "            SewerNetworkDynamicSequenceManager.Terminate();\n"
    "            CogoPointProjectStyleManager.Terminate();\n"
    "            ProjectStylePresetManager.Terminate();",
)

# Ribbon access and shared polyline-vertex popup.
replace_once(
    PLUGIN,
    '                        Cmd("Project Style Information", "CE_PROJECTSTYLEINFO ", "Review stored project style selections."),\n'
    '                        Cmd("Clear Project Styles", "CE_PROJECTSTYLECLEAR ", "Clear only the stored project style selections.")),',
    '                        Cmd("Project Style Information", "CE_PROJECTSTYLEINFO ", "Review stored project style selections."),\n'
    '                        Cmd("Save Project Styles for Other Drawings", "CE_PROJECTSTYLESAVE ", "Save the current discipline selections as the reusable CE project preset."),\n'
    '                        Cmd("Apply Saved Project Styles", "CE_PROJECTSTYLEAPPLY ", "Apply the reusable saved project styles to the active drawing."),\n'
    '                        Cmd("Clear Project Styles", "CE_PROJECTSTYLECLEAR ", "Clear only the stored project style selections.")),',
)
replace_once(
    PLUGIN,
    '                        Cmd("Polyline Vertex Linked Points", "CE_COORDPOLY2 ", "Create sequential COGO points in polyline direction and a linked Point Name, Y, X, Z table."),',
    '                        Cmd("Polyline Vertex Linked Points", "CE_COORDPOLY2 ", "Open the same dynamic vertex setting-out popup in vertices-only mode."),',
)
replace_once(
    PLUGIN,
    '                        Cmd("Export Vertex Setting-Out", "CE_VERTEXSETTINGOUTEXPORT ", "Refresh and export a linked vertex setting-out table to Excel.")),',
    '                        Cmd("Export Vertex Setting-Out", "CE_VERTEXSETTINGOUTEXPORT ", "Refresh and export a linked vertex setting-out table to Excel."),\n'
    '                        Cmd("Synchronize COGO Project Styles", "CE_COGOPOINTSYNC ", "Apply RSA_Circle/Description Only or the saved Project Style Centre point choices to every COGO point."),\n'
    '                        Cmd("Resolve COGO Label Overlaps", "CE_COGOOVERLAPFIX ", "Move COGO labels only while keeping survey point coordinates fixed.")),',
)
replace_once(
    PLUGIN,
    '                        Cmd("Sequence with Selected Main", "CE_SEWSEQMAIN ", "Select Branch-1 and sequence remaining branches."),',
    '                        Cmd("Sequence with Selected Main", "CE_SEWSEQMAIN ", "Select Branch-1 and sequence remaining branches."),\n'
    '                        Cmd("Dynamic Resequence Selected Network", "CE_SEWAUTOSEQ ", "Compact Branch/P/MH numbering after deletions or reconnections and refresh linked outputs."),\n'
    '                        Cmd("Dynamic Resequence All CE Networks", "CE_SEWAUTOSEQALL ", "Refresh all previously CE-sequenced sewer networks."),\n'
    '                        Cmd("Dynamic Resequence Settings", "CE_SEWAUTOSEQSETTINGS ", "Enable or disable automatic topology resequencing."),',
)

# Project Style Centre applies point styles immediately after saving.
replace_once(
    PROJECT,
    "                ProjectStylePresetManager.SaveFromDrawing(document);\n                document.Editor.WriteMessage(",
    "                ProjectStylePresetManager.SaveFromDrawing(document);\n"
    "                CogoPointProjectStyleManager.Queue();\n"
    "                CogoPointProjectStyleCommands.ApplySelectedStyles(document, true);\n"
    "                document.Editor.WriteMessage(",
)

# Geometry records retain annotation offsets when labels are moved.
replace_once(
    GEOMETRY,
    "        public string PointName { get; set; }\n",
    "        public string PointName { get; set; }\n"
    "        public Vector3d? AnnotationOffset { get; set; }\n",
)

# Vertex source imports and schema.
replace_once(
    VERTEX,
    "using System.Collections.Generic;\n",
    "using System.Collections;\nusing System.Collections.Generic;\n",
)
replace_once(
    VERTEX,
    "using System.Linq;\n",
    "using System.Linq;\nusing System.Reflection;\n",
)
replace_once(
    VERTEX,
    '        private const string SchemaVersion = "1";',
    '        private const string SchemaVersion = "2";',
)

# One popup for full setting-out or simple polyline vertices, with live Z source.
replace_once(
    VERTEX,
    '            settings.AddChoice(\n'
    '                "Output", "01 Output", "Point output", "COGO",\n'
    '                "Choose one dynamic point/annotation output for every generated setting-out location.",\n'
    '                new[] { "COGO", "MText", "MLeader" });',
    '            settings.AddChoice(\n'
    '                "Output", "01 Output", "Point output", "COGO",\n'
    '                "Choose one dynamic point/annotation output for every generated setting-out location.",\n'
    '                new[] { "COGO", "MText", "MLeader" });\n'
    '            settings.AddChoice(\n'
    '                "Generation", "01 Output", "Point generation", "Engineering setting-out points",\n'
    '                "Choose the complete arc/tangent engineering rules or only the original polyline/feature-line vertices.",\n'
    '                new[] { "Engineering setting-out points", "Polyline vertices only" });\n'
    '            settings.AddChoice(\n'
    '                "Elevation", "01 Output", "XYZ elevation source", "Source geometry",\n'
    '                "Read Z from the selected source geometry, a Civil 3D surface, or a separate feature line. The reference remains linked on refresh.",\n'
    '                new[] { "Source geometry", "Select Civil 3D surface", "Select feature line" });',
)
replace_once(
    VERTEX,
    '            double labelOffset = settings.Double("Offset", 3.0);\n\n'
    '            PromptPointResult tablePoint = document.Editor.GetPoint(',
    '            double labelOffset = settings.Double("Offset", 3.0);\n'
    '            string generationMode = settings.Text("Generation");\n'
    '            string elevationMode = settings.Text("Elevation");\n'
    '            ObjectId elevationSourceId;\n'
    '            if (!PromptElevationSource(\n'
    '                    document,\n'
    '                    elevationMode,\n'
    '                    out elevationSourceId)) return;\n\n'
    '            PromptPointResult tablePoint = document.Editor.GetPoint(',
)
replace_once(
    VERTEX,
    "            rejected += geometryRejected;\n            if (sources.Count == 0)",
    "            rejected += geometryRejected;\n"
    "            ApplyGenerationMode(sources, generationMode);\n"
    "            ApplyElevationReference(\n"
    "                document.Database,\n"
    "                sources,\n"
    "                elevationMode,\n"
    "                elevationSourceId);\n"
    "            if (sources.Count == 0 || sources.All(item => item.Records.Count == 0))",
)
replace_once(
    VERTEX,
    "                LabelOffset = labelOffset,\n                SourceHandles = sources.Select(item => item.Handle).ToList()",
    "                LabelOffset = labelOffset,\n"
    "                GenerationMode = generationMode,\n"
    "                ElevationMode = elevationMode,\n"
    "                ElevationSourceHandle = elevationSourceId.IsNull\n"
    "                    ? string.Empty\n"
    "                    : elevationSourceId.Handle.ToString(),\n"
    "                SourceHandles = sources.Select(item => item.Handle).ToList()",
)
replace_once(
    VERTEX,
    "                IList<VertexSettingSource> sources = VertexSettingOutGeometry.ReadSources(\n"
    "                    document.Database,\n"
    "                    transaction,\n"
    "                    sourceIds,\n"
    "                    out rejected);\n"
    "                if (sources.Count == 0)",
    "                IList<VertexSettingSource> sources = VertexSettingOutGeometry.ReadSources(\n"
    "                    document.Database,\n"
    "                    transaction,\n"
    "                    sourceIds,\n"
    "                    out rejected);\n"
    "                ApplyGenerationMode(sources, link.GenerationMode);\n"
    "                ApplyElevationReference(\n"
    "                    document.Database,\n"
    "                    sources,\n"
    "                    link.ElevationMode,\n"
    "                    ResolveHandle(document.Database, link.ElevationSourceHandle));\n"
    "                if (sources.Count == 0 || sources.All(item => item.Records.Count == 0))",
)

# Preserve manual/overlap offsets before MLeader recreation.
replace_once(
    VERTEX,
    "                    EraseIfPossible(transaction, existing);\n                    CreateOutput(document.Database, civilDocument, transaction, modelSpace, link, record, textHeight);",
    "                    CaptureCurrentAnnotationOffset(\n"
    "                        transaction,\n"
    "                        existing,\n"
    "                        record);\n"
    "                    EraseIfPossible(transaction, existing);\n"
    "                    CreateOutput(document.Database, civilDocument, transaction, modelSpace, link, record, textHeight);",
)

# COGO styles, MLeader arrow and stored anchors on creation.
replace_once(
    VERTEX,
    "                try { point.PointName = record.PointName; } catch { }\n                WriteOutputLink(point, transaction, link.GroupId, record.Key);",
    "                try { point.PointName = record.PointName; } catch { }\n"
    "                CogoPointProjectStyleCommands.ApplyPointStyles(\n"
    "                    database,\n"
    "                    civilDocument,\n"
    "                    transaction,\n"
    "                    point);\n"
    "                WriteOutputLink(\n"
    "                    point, transaction, link.GroupId, record.Key, record.Point);",
)
replace_once(
    VERTEX,
    "                Point3d location = LabelLocation(record.Point, link.LabelOffset);",
    "                Point3d location = OutputLocation(record, link.LabelOffset);",
)
replace_once(
    VERTEX,
    "                leader.SetDatabaseDefaults(database);\n                leader.ContentType = ContentType.MTextContent;",
    "                leader.SetDatabaseDefaults(database);\n"
    "                leader.MLeaderStyle = database.MLeaderstyle;\n"
    "                leader.ArrowSymbolId = ObjectId.Null;\n"
    "                leader.ContentType = ContentType.MTextContent;",
)
replace_once(
    VERTEX,
    "                WriteOutputLink(leader, transaction, link.GroupId, record.Key);",
    "                WriteOutputLink(\n"
    "                    leader, transaction, link.GroupId, record.Key, record.Point);",
)
replace_once(
    VERTEX,
    "            mtext.Location = LabelLocation(record.Point, link.LabelOffset);",
    "            mtext.Location = OutputLocation(record, link.LabelOffset);",
)
replace_once(
    VERTEX,
    "            WriteOutputLink(mtext, transaction, link.GroupId, record.Key);",
    "            WriteOutputLink(\n"
    "                mtext, transaction, link.GroupId, record.Key, record.Point);",
)

# Existing COGO and MText refresh use live styles and saved offsets.
replace_once(
    VERTEX,
    "                try { cogo.PointName = record.PointName; } catch { }\n                return true;",
    "                try { cogo.PointName = record.PointName; } catch { }\n"
    "                CogoPointProjectStyleCommands.ApplyPointStyles(\n"
    "                    cogo.Database,\n"
    "                    CivilApplication.ActiveDocument,\n"
    "                    transaction,\n"
    "                    cogo);\n"
    "                WriteOutputLink(\n"
    "                    cogo, transaction, link.GroupId, record.Key, record.Point);\n"
    "                return true;",
)
replace_once(
    VERTEX,
    "                mtext.Location = LabelLocation(record.Point, link.LabelOffset);\n"
    "                mtext.TextHeight = textHeight;\n"
    "                mtext.Contents = LabelText(record);\n"
    "                return true;",
    "                CaptureCurrentAnnotationOffset(transaction, id, record);\n"
    "                mtext.Location = OutputLocation(record, link.LabelOffset);\n"
    "                mtext.TextHeight = textHeight;\n"
    "                mtext.Contents = LabelText(record);\n"
    "                WriteOutputLink(\n"
    "                    mtext, transaction, link.GroupId, record.Key, record.Point);\n"
    "                return true;",
)

# Wider table fields while retaining centered text.
replace_once(
    VERTEX,
    "            table.SetColumnWidth(Math.Max(textHeight * 8.0, 0.001));\n"
    "            table.Cells[0, 0].TextString",
    "            table.SetColumnWidth(Math.Max(textHeight * 8.0, 0.001));\n"
    "            table.Columns[0].Width = Math.Max(textHeight * 9.0, 0.001);\n"
    "            table.Columns[1].Width = Math.Max(textHeight * 18.0, 0.001);\n"
    "            table.Columns[2].Width = Math.Max(textHeight * 14.0, 0.001);\n"
    "            table.Columns[8].Width = Math.Max(textHeight * 12.0, 0.001);\n"
    "            table.Cells[0, 0].TextString",
)

# Table link schema stores generation and elevation dependencies.
replace_once(
    VERTEX,
    "            foreach (string handle in link.SourceHandles)\n                values.Add(new TypedValue((int)DxfCode.ExtendedDataAsciiString, handle));",
    "            values.Add(new TypedValue(\n"
    "                (int)DxfCode.ExtendedDataAsciiString,\n"
    "                \"GEN=\" + (link.GenerationMode ?? string.Empty)));\n"
    "            values.Add(new TypedValue(\n"
    "                (int)DxfCode.ExtendedDataAsciiString,\n"
    "                \"ELEV=\" + (link.ElevationMode ?? string.Empty)));\n"
    "            values.Add(new TypedValue(\n"
    "                (int)DxfCode.ExtendedDataAsciiString,\n"
    "                \"ELEVHANDLE=\" + (link.ElevationSourceHandle ?? string.Empty)));\n"
    "            foreach (string handle in link.SourceHandles)\n"
    "                values.Add(new TypedValue(\n"
    "                    (int)DxfCode.ExtendedDataAsciiString,\n"
    "                    \"SRC=\" + handle));",
)
replace_once(
    VERTEX,
    "                LabelOffset = Convert.ToDouble(values[7].Value, CultureInfo.InvariantCulture),\n                SourceHandles = new List<string>()",
    "                LabelOffset = Convert.ToDouble(values[7].Value, CultureInfo.InvariantCulture),\n"
    "                GenerationMode = \"Engineering setting-out points\",\n"
    "                ElevationMode = \"Source geometry\",\n"
    "                ElevationSourceHandle = string.Empty,\n"
    "                SourceHandles = new List<string>()",
)
replace_once(
    VERTEX,
    "            for (int index = 8; index < values.Length; index++)\n"
    "            {\n"
    "                string handle = Convert.ToString(values[index].Value);\n"
    "                if (!string.IsNullOrWhiteSpace(handle)) link.SourceHandles.Add(handle);\n"
    "            }",
    "            for (int index = 8; index < values.Length; index++)\n"
    "            {\n"
    "                string value = Convert.ToString(values[index].Value);\n"
    "                if (string.IsNullOrWhiteSpace(value)) continue;\n"
    "                if (value.StartsWith(\"GEN=\", StringComparison.OrdinalIgnoreCase))\n"
    "                    link.GenerationMode = value.Substring(4);\n"
    "                else if (value.StartsWith(\"ELEV=\", StringComparison.OrdinalIgnoreCase))\n"
    "                    link.ElevationMode = value.Substring(5);\n"
    "                else if (value.StartsWith(\"ELEVHANDLE=\", StringComparison.OrdinalIgnoreCase))\n"
    "                    link.ElevationSourceHandle = value.Substring(11);\n"
    "                else if (value.StartsWith(\"SRC=\", StringComparison.OrdinalIgnoreCase))\n"
    "                    link.SourceHandles.Add(value.Substring(4));\n"
    "                else\n"
    "                    link.SourceHandles.Add(value);\n"
    "            }",
)

# Output XData keeps the geometry anchor for MText/MLeader offset preservation.
replace_once(
    VERTEX,
    "        private static void WriteOutputLink(\n"
    "            Entity entity,\n"
    "            Transaction transaction,\n"
    "            string groupId,\n"
    "            string key)\n"
    "        {\n"
    "            entity.XData = LinkBuffer(\"OUTPUT\", groupId, key);\n"
    "        }",
    "        private static void WriteOutputLink(\n"
    "            Entity entity,\n"
    "            Transaction transaction,\n"
    "            string groupId,\n"
    "            string key,\n"
    "            Point3d anchor)\n"
    "        {\n"
    "            entity.XData = LinkBuffer(\"OUTPUT\", groupId, key, anchor);\n"
    "        }",
)
replace_once(
    VERTEX,
    "            entity.XData = LinkBuffer(\"DIM\", groupId, key);",
    "            entity.XData = LinkBuffer(\"DIM\", groupId, key, null);",
)
regex_once(
    VERTEX,
    r"        private static ResultBuffer LinkBuffer\(string type, string groupId, string key\)\n        \{.*?\n        \}\n\n        private static bool TryReadEntityLink",
    '''        private static ResultBuffer LinkBuffer(
            string type,
            string groupId,
            string key,
            Point3d? anchor)
        {
            var values = new List<TypedValue>
            {
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, AppName),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, type),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, SchemaVersion),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, groupId),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, key)
            };
            if (anchor.HasValue)
            {
                values.Add(new TypedValue(
                    (int)DxfCode.ExtendedDataReal,
                    anchor.Value.X));
                values.Add(new TypedValue(
                    (int)DxfCode.ExtendedDataReal,
                    anchor.Value.Y));
                values.Add(new TypedValue(
                    (int)DxfCode.ExtendedDataReal,
                    anchor.Value.Z));
            }
            return new ResultBuffer(values.ToArray());
        }

        private static bool TryReadEntityLink''',
)

# Helpers inserted before group inventory.
helper_marker = "        private static void InventoryGroup(\n"
helpers = r'''        private static Point3d OutputLocation(
            VertexSettingRecord record,
            double defaultOffset)
        {
            Vector3d offset = record.AnnotationOffset ??
                new Vector3d(defaultOffset, defaultOffset, 0.0);
            return record.Point + offset;
        }

        private static void CaptureCurrentAnnotationOffset(
            Transaction transaction,
            ObjectId id,
            VertexSettingRecord record)
        {
            if (transaction == null || record == null || id.IsNull || id.IsErased)
                return;
            Entity entity;
            try
            {
                entity = transaction.GetObject(
                    id,
                    OpenMode.ForRead,
                    false) as Entity;
            }
            catch
            {
                return;
            }
            if (entity == null) return;
            Point3d anchor;
            if (!TryReadOutputAnchor(entity, out anchor)) return;
            MText mtext = entity as MText;
            if (mtext != null)
            {
                record.AnnotationOffset = mtext.Location - anchor;
                return;
            }
            MLeader leader = entity as MLeader;
            if (leader != null)
            {
                try
                {
                    record.AnnotationOffset = leader.TextLocation - anchor;
                }
                catch
                {
                    // Keep the default offset when a proxy leader blocks access.
                }
            }
        }

        private static bool TryReadOutputAnchor(
            Entity entity,
            out Point3d anchor)
        {
            anchor = Point3d.Origin;
            if (entity == null) return false;
            ResultBuffer buffer = entity.GetXDataForApplication(AppName);
            if (buffer == null) return false;
            TypedValue[] values = buffer.AsArray();
            if (values.Length < 8) return false;
            try
            {
                anchor = new Point3d(
                    Convert.ToDouble(values[5].Value, CultureInfo.InvariantCulture),
                    Convert.ToDouble(values[6].Value, CultureInfo.InvariantCulture),
                    Convert.ToDouble(values[7].Value, CultureInfo.InvariantCulture));
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void ApplyGenerationMode(
            IList<VertexSettingSource> sources,
            string mode)
        {
            if (sources == null ||
                !string.Equals(
                    mode,
                    "Polyline vertices only",
                    StringComparison.OrdinalIgnoreCase))
                return;
            foreach (VertexSettingSource source in sources)
            {
                source.Records = source.Records
                    .Where(record => string.Equals(
                        record.Kind,
                        "VERTEX",
                        StringComparison.OrdinalIgnoreCase))
                    .ToList();
                source.Dimensions = new List<VertexRadialDimension>();
            }
        }

        private static bool PromptElevationSource(
            Document document,
            string mode,
            out ObjectId sourceId)
        {
            sourceId = ObjectId.Null;
            if (document == null || string.IsNullOrWhiteSpace(mode) ||
                string.Equals(
                    mode,
                    "Source geometry",
                    StringComparison.OrdinalIgnoreCase))
                return true;

            var options = new PromptEntityOptions(
                string.Equals(
                    mode,
                    "Select Civil 3D surface",
                    StringComparison.OrdinalIgnoreCase)
                    ? "\nSelect the Civil 3D surface used for all setting-out Z values: "
                    : "\nSelect the feature line used for all setting-out Z values: ");
            PromptEntityResult selected = document.Editor.GetEntity(options);
            if (selected.Status != PromptStatus.OK) return false;
            using (Transaction transaction =
                document.Database.TransactionManager.StartTransaction())
            {
                DBObject value = transaction.GetObject(
                    selected.ObjectId,
                    OpenMode.ForRead,
                    false);
                bool valid = string.Equals(
                    mode,
                    "Select Civil 3D surface",
                    StringComparison.OrdinalIgnoreCase)
                    ? value is Autodesk.Civil.DatabaseServices.Surface
                    : value is Autodesk.Civil.DatabaseServices.FeatureLine;
                if (!valid)
                {
                    document.Editor.WriteMessage(
                        "\nThe selected object is not the required Civil 3D elevation source.");
                    return false;
                }
            }
            sourceId = selected.ObjectId;
            return true;
        }

        private static void ApplyElevationReference(
            Database database,
            IList<VertexSettingSource> sources,
            string mode,
            ObjectId sourceId)
        {
            if (database == null || sources == null || sourceId.IsNull ||
                string.IsNullOrWhiteSpace(mode) ||
                string.Equals(
                    mode,
                    "Source geometry",
                    StringComparison.OrdinalIgnoreCase))
                return;
            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                DBObject reference;
                try
                {
                    reference = transaction.GetObject(
                        sourceId,
                        OpenMode.ForRead,
                        false);
                }
                catch
                {
                    return;
                }
                Autodesk.Civil.DatabaseServices.Surface surface =
                    reference as Autodesk.Civil.DatabaseServices.Surface;
                Autodesk.Civil.DatabaseServices.FeatureLine featureLine =
                    reference as Autodesk.Civil.DatabaseServices.FeatureLine;
                if (surface == null && featureLine == null) return;

                foreach (VertexSettingSource source in sources)
                {
                    foreach (VertexSettingRecord record in source.Records)
                    {
                        double elevation;
                        if (TryReadReferenceElevation(
                                surface,
                                featureLine,
                                record.Point,
                                out elevation))
                        {
                            record.Point = new Point3d(
                                record.Point.X,
                                record.Point.Y,
                                elevation);
                        }
                    }
                    foreach (VertexRadialDimension dimension in source.Dimensions)
                    {
                        double centerElevation;
                        if (TryReadReferenceElevation(
                                surface,
                                featureLine,
                                dimension.Center,
                                out centerElevation))
                        {
                            dimension.Center = new Point3d(
                                dimension.Center.X,
                                dimension.Center.Y,
                                centerElevation);
                        }
                        double chordElevation;
                        if (TryReadReferenceElevation(
                                surface,
                                featureLine,
                                dimension.ChordPoint,
                                out chordElevation))
                        {
                            dimension.ChordPoint = new Point3d(
                                dimension.ChordPoint.X,
                                dimension.ChordPoint.Y,
                                chordElevation);
                        }
                    }
                }
            }
        }

        private static bool TryReadReferenceElevation(
            Autodesk.Civil.DatabaseServices.Surface surface,
            Autodesk.Civil.DatabaseServices.FeatureLine featureLine,
            Point3d point,
            out double elevation)
        {
            elevation = point.Z;
            try
            {
                if (surface != null)
                {
                    elevation = surface.FindElevationAtXY(point.X, point.Y);
                    return !double.IsNaN(elevation) &&
                           !double.IsInfinity(elevation);
                }
                if (featureLine != null)
                {
                    Point3d closest = featureLine.GetClosestPointTo(
                        new Point3d(point.X, point.Y, point.Z),
                        false);
                    elevation = closest.Z;
                    return !double.IsNaN(elevation) &&
                           !double.IsInfinity(elevation);
                }
            }
            catch
            {
                return false;
            }
            return false;
        }

'''
text = read(VERTEX)
if "private static Point3d OutputLocation(" not in text:
    if helper_marker not in text:
        raise SystemExit("Vertex helper insertion marker was not found")
    write(VERTEX, text.replace(helper_marker, helpers + helper_marker, 1))

# Link model fields.
replace_once(
    VERTEX,
    "            public double LabelOffset { get; set; }\n            public IList<string> SourceHandles { get; set; }",
    "            public double LabelOffset { get; set; }\n"
    "            public string GenerationMode { get; set; }\n"
    "            public string ElevationMode { get; set; }\n"
    "            public string ElevationSourceHandle { get; set; }\n"
    "            public IList<string> SourceHandles { get; set; }",
)

# Delegate the legacy linked polyline command to the same enhanced popup.
replace_once(
    SURVEY,
    "            Document document = ActiveDocument();\n            if (document == null) return;\n            CivilDocument civilDocument = CivilApplication.ActiveDocument;",
    "            Document document = ActiveDocument();\n"
    "            if (document == null) return;\n"
    "            document.Editor.WriteMessage(\n"
    "                \"\\nCE_COORDPOLY2 now uses the shared dynamic vertex setting-out popup. Choose 'Polyline vertices only' for the original workflow.\");\n"
    "            document.SendStringToExecute(\n"
    "                \"CE_VERTEXSETTINGOUT \",\n"
    "                true,\n"
    "                false,\n"
    "                true);\n"
    "            return;\n"
    "            CivilDocument civilDocument = CivilApplication.ActiveDocument;",
)

# Robust point-style resolution for any remaining coordinate commands.
regex_once(
    SURVEY,
    r"        private static void ResolveSelectedPointStyles\(\n            Database database,\n            Transaction transaction,\n            out ObjectId pointStyleId,\n            out ObjectId pointLabelStyleId\)\n        \{.*?\n        \}\n\n        private static string ReadSelectedStyle",
    '''        private static void ResolveSelectedPointStyles(
            Database database,
            Transaction transaction,
            out ObjectId pointStyleId,
            out ObjectId pointLabelStyleId)
        {
            pointStyleId = ObjectId.Null;
            pointLabelStyleId = ObjectId.Null;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (civilDocument == null) return;
            ProjectStyleSelection selection =
                ProjectStyleCenterCommands.ReadSelection(database);
            string requestedPoint = ReadSelectedStyle(selection, "Point Style");
            string requestedLabel = ReadSelectedStyle(selection, "Point Label Style");
            if (string.IsNullOrWhiteSpace(requestedPoint))
                requestedPoint = "RSA_Circle";
            if (string.IsNullOrWhiteSpace(requestedLabel))
                requestedLabel = "Description Only";
            string actual;
            pointStyleId = CivilStyleCatalogV2.ResolveStyleId(
                database,
                civilDocument,
                "Point Style",
                requestedPoint,
                transaction,
                out actual);
            pointLabelStyleId = CivilStyleCatalogV2.ResolveStyleId(
                database,
                civilDocument,
                "Point Label Style",
                requestedLabel,
                transaction,
                out actual);
        }

        private static string ReadSelectedStyle''',
)

# Closed-filled arrow for coordinate MLeaders.
replace_once(
    SURVEY,
    "                    leader.SetDatabaseDefaults(database);\n                    leader.ContentType = ContentType.MTextContent;",
    "                    leader.SetDatabaseDefaults(database);\n"
    "                    leader.MLeaderStyle = database.MLeaderstyle;\n"
    "                    leader.ArrowSymbolId = ObjectId.Null;\n"
    "                    leader.ContentType = ContentType.MTextContent;",
)

# Explicit refresh includes point styling/overlap pass.
replace_once(
    REFRESH,
    "                summary.CoordinateFollowers +=\n                    PolylineDirectionCommands.RefreshLinkedArrows(document);",
    "                summary.CoordinateFollowers +=\n"
    "                    PolylineDirectionCommands.RefreshLinkedArrows(document);\n"
    "                CogoPointProjectStyleCommands.ApplySelectedStyles(\n"
    "                    document,\n"
    "                    true);",
)

# Shared single-point style helper.
cogo_marker = "        internal static int ResolveOverlaps(Document document)\n"
cogo_helper = r'''        internal static void ApplyPointStyles(
            Database database,
            CivilDocument civilDocument,
            Transaction transaction,
            CivilCogoPoint point)
        {
            if (database == null || civilDocument == null ||
                transaction == null || point == null)
                return;
            ProjectStyleSelection selection =
                ProjectStyleCenterCommands.ReadSelection(database);
            string requestedPoint = ReadSelection(
                selection,
                "Point Style",
                "RSA_Circle");
            string requestedLabel = ReadSelection(
                selection,
                "Point Label Style",
                "Description Only");
            string actual;
            ObjectId pointStyleId = CivilStyleCatalogV2.ResolveStyleId(
                database,
                civilDocument,
                "Point Style",
                requestedPoint,
                transaction,
                out actual);
            ObjectId labelStyleId = CivilStyleCatalogV2.ResolveStyleId(
                database,
                civilDocument,
                "Point Label Style",
                requestedLabel,
                transaction,
                out actual);
            if (!pointStyleId.IsNull)
            {
                try { point.StyleId = pointStyleId; } catch { }
            }
            if (!labelStyleId.IsNull)
            {
                try { point.LabelStyleId = labelStyleId; } catch { }
            }
        }

'''
text = read(COGO)
if "internal static void ApplyPointStyles(" not in text:
    if cogo_marker not in text:
        raise SystemExit("COGO point-style helper marker was not found")
    write(COGO, text.replace(cogo_marker, cogo_helper + cogo_marker, 1))

# Fix dynamic sewer edge metadata and listen for erasures.
replace_once(
    SEWER,
    "                    pipe.EndStructureId,\n                    Math.Max(length, 0.001));",
    "                    pipe.EndStructureId,\n"
    "                    Math.Max(length, 0.001),\n"
    "                    (pipe.Name ?? string.Empty).StartsWith(\n"
    "                        \"P1.\",\n"
    "                        StringComparison.OrdinalIgnoreCase) ||\n"
    "                    string.Equals(\n"
    "                        pipe.Description,\n"
    "                        \"Branch-1\",\n"
    "                        StringComparison.OrdinalIgnoreCase));",
)
regex_once(
    SEWER,
    r"            candidates = candidates\.Where\(edge =>\n            \{\n                return edge != null && IsBranchOnePipe\(edge\.Id, topology\);\n            \}\)\.ToList\(\);\n            return BuildSimplePath\(topology, candidates\);\n        \}\n\n        private static bool IsBranchOnePipe\(.*?\n        \}\n\n        private static SewerBranchPath BuildSimplePath",
    '''            candidates = candidates
                .Where(edge => edge != null && edge.IsBranchOne)
                .ToList();
            return BuildSimplePath(topology, candidates);
        }

        private static SewerBranchPath BuildSimplePath''',
)
replace_once(
    SEWER,
    "            ObjectId end,\n            double length)\n",
    "            ObjectId end,\n            double length,\n            bool isBranchOne)\n",
)
replace_once(
    SEWER,
    "            Length = length;\n        }\n",
    "            Length = length;\n            IsBranchOne = isBranchOne;\n        }\n",
)
replace_once(
    SEWER,
    "        public double Length { get; private set; }\n        public ObjectId Other(ObjectId node)",
    "        public double Length { get; private set; }\n"
    "        public bool IsBranchOne { get; private set; }\n"
    "        public ObjectId Other(ObjectId node)",
)
replace_once(
    SEWER,
    "            _database.ObjectModified += OnObjectChanged;\n            _database.ObjectAppended += OnObjectChanged;",
    "            _database.ObjectModified += OnObjectChanged;\n"
    "            _database.ObjectAppended += OnObjectChanged;\n"
    "            _database.ObjectErased += OnObjectErased;",
)
replace_once(
    SEWER,
    "                _database.ObjectModified -= OnObjectChanged;\n                _database.ObjectAppended -= OnObjectChanged;",
    "                _database.ObjectModified -= OnObjectChanged;\n"
    "                _database.ObjectAppended -= OnObjectChanged;\n"
    "                _database.ObjectErased -= OnObjectErased;",
)
replace_once(
    SEWER,
    "        private static void OnObjectChanged(object sender, ObjectEventArgs eventArgs)\n",
    "        private static void OnObjectErased(\n"
    "            object sender,\n"
    "            ObjectErasedEventArgs eventArgs)\n"
    "        {\n"
    "            if (_busy || eventArgs == null || eventArgs.DBObject == null) return;\n"
    "            if (eventArgs.DBObject is CivilPipe ||\n"
    "                eventArgs.DBObject is CivilStructure ||\n"
    "                eventArgs.DBObject is CivilNetwork)\n"
    "            {\n"
    "                Queue();\n"
    "            }\n"
    "        }\n\n"
    "        private static void OnObjectChanged(object sender, ObjectEventArgs eventArgs)\n",
)

print("Applied dynamic COGO, vertex-anchor and sewer-topology integration.")

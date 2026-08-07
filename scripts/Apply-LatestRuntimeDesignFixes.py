#!/usr/bin/env python3
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / 'src' / 'CE.Tools.Civil3D'


def replace(path, old, new, count=None):
    p = SRC / path
    text = p.read_text(encoding='utf-8')
    found = text.count(old)
    if found == 0:
        raise SystemExit(f'{path}: replacement marker not found:\n{old[:240]}')
    if count is not None and found != count:
        raise SystemExit(f'{path}: expected {count} marker(s), found {found}: {old[:120]}')
    text = text.replace(old, new, count if count is not None else -1)
    p.write_text(text, encoding='utf-8')

# 1. Universal refresh: duplicate handler registration was producing repeated work/undo noise.
replace('UniversalDynamicRefreshCommands.cs',
'''            _document.CommandWillStart += OnCommandWillStart;\n            _document.CommandWillStart += OnCommandWillStart;''',
'''            _document.CommandWillStart += OnCommandWillStart;''', 1)
replace('UniversalDynamicRefreshCommands.cs',
'''                _document.CommandWillStart -= OnCommandWillStart;\n                _document.CommandWillStart -= OnCommandWillStart;''',
'''                _document.CommandWillStart -= OnCommandWillStart;''', 1)

# 2. Force closed-filled arrows instead of inheriting architectural tick/other DIMBLK.
replace('VertexSettingOutCommands.cs',
'''                // ObjectId.Null is AutoCAD's native closed-filled arrow. Use the\n                // drawing's configured dimension arrow when one is available.\n                leader.ArrowSymbolId = database.Dimblk.IsNull\n                    ? ObjectId.Null\n                    : database.Dimblk;''',
'''                // ObjectId.Null is AutoCAD's native closed-filled arrow. Do not\n                // inherit DIMBLK because a project DIMSTYLE may use architectural ticks.\n                leader.ArrowSymbolId = ObjectId.Null;''', 1)
replace('VertexSettingOutCommands.cs',
'''            ObjectId arrow = database.Dimblk.IsNull\n                ? ObjectId.Null\n                : database.Dimblk;''',
'''            // Force the AutoCAD closed-filled default independently of DIMSTYLE.\n            ObjectId arrow = ObjectId.Null;''', 1)
replace('PreBuildRuntimeCompletionCommands.cs',
'''                // ObjectId.Null is AutoCAD's native closed-filled arrow. Use the\n                // current dimension arrow when the drawing explicitly stores one.\n                leader.ArrowSymbolId = database.Dimblk.IsNull\n                    ? ObjectId.Null\n                    : database.Dimblk;''',
'''                // Force a closed-filled arrow even when the drawing DIMSTYLE uses ticks.\n                leader.ArrowSymbolId = ObjectId.Null;''', 1)

# 3. Keep linked labels close to their true anchors.
replace('PreBuildRuntimeCompletionCommands.cs',
'''                PaperAnnotationScale.ModelDistance(database, 15.0),''',
'''                PaperAnnotationScale.ModelDistance(database, 8.0),''', 1)
replace('PreBuildRuntimeCompletionCommands.cs',
'''            for (int ring = 1; ring <= 5; ring++)''',
'''            for (int ring = 1; ring <= 4; ring++)''', 1)
replace('CogoPointProjectStyleCommands.cs',
'''            for (int ring = 1; ring <= 5; ring++)''',
'''            for (int ring = 1; ring <= 4; ring++)''', 1)
replace('CogoPointProjectStyleCommands.cs',
'''                double distance = candidate.DistanceTo(item.LabelLocation);\n                if (distance < bestDistance)\n                {\n                    best = candidate;\n                    bestDistance = distance;\n                }''',
'''                // Prefer the closest clear position to the survey point itself.\n                // Original label movement is only a small tie-breaker.\n                double distance = candidate.DistanceTo(item.Anchor) +\n                    candidate.DistanceTo(item.LabelLocation) * 0.05;\n                if (distance < bestDistance)\n                {\n                    best = candidate;\n                    bestDistance = distance;\n                }''', 1)
replace('CogoPointProjectStyleCommands.cs',
'''                PaperAnnotationScale.ModelDistance(database, 15.0),''',
'''                PaperAnnotationScale.ModelDistance(database, 8.0),''', 1)

# 4. Coordinate-order option now changes the X/Y labels only, not the numeric values.
replace('VertexSettingOutCommands.cs',
'''                table.Cells[row, 4].TextString = (yFirst ? displayY : displayX)\n                    .ToString("N3", CultureInfo.CurrentCulture);\n                table.Cells[row, 5].TextString = (yFirst ? displayX : displayY)\n                    .ToString("N3", CultureInfo.CurrentCulture);''',
'''                // Keep the numeric coordinate columns fixed and swap only their\n                // displayed X/Y headings when requested. Drawing coordinates never change.\n                table.Cells[row, 4].TextString = displayX\n                    .ToString("N3", CultureInfo.CurrentCulture);\n                table.Cells[row, 5].TextString = displayY\n                    .ToString("N3", CultureInfo.CurrentCulture);''', 1)
replace('VertexSettingOutCommands.cs',
'''            string first = (yFirst ? "Y=" : "X=") +\n                (yFirst ? displayY : displayX)\n                    .ToString("N3", CultureInfo.CurrentCulture);\n            string second = (yFirst ? "X=" : "Y=") +\n                (yFirst ? displayX : displayY)\n                    .ToString("N3", CultureInfo.CurrentCulture);''',
'''            string first = (yFirst ? "Y=" : "X=") +\n                displayX.ToString("N3", CultureInfo.CurrentCulture);\n            string second = (yFirst ? "X=" : "Y=") +\n                displayY.ToString("N3", CultureInfo.CurrentCulture);''', 1)
replace('VertexSettingOutCommands.cs',
'''                "Change only the annotation and table display order. The true drawing coordinates remain unchanged.",\n                new[] { "X then Y", "Y then X" });''',
'''                "Swap only the displayed X/Y letters/headings. Numeric coordinate values and true drawing coordinates remain unchanged.",\n                new[] { "X then Y", "Y then X" });''', 1)

# 5. Table graphics: force table block regeneration so values display immediately without a grip-drag.
replace('VertexSettingOutCommands.cs',
'''            table.GenerateLayout();\n        }\n\n        private static string LabelText(''',
'''            ForceTableGraphics(table);\n        }\n\n        private static void ForceTableGraphics(Table table)\n        {\n            if (table == null) return;\n            try { table.GenerateLayout(); } catch { }\n            try { table.RecordGraphicsModified(true); } catch { }\n            try\n            {\n                MethodInfo method = table.GetType().GetMethod(\n                    "RecomputeTableBlock",\n                    BindingFlags.Public | BindingFlags.Instance,\n                    null,\n                    new[] { typeof(bool) },\n                    null);\n                if (method != null) method.Invoke(table, new object[] { true });\n            }\n            catch { }\n        }\n\n        private static string LabelText(''', 1)

# 6. Radial dimension text follows the radius direction and sits outside the arc.
replace('VertexSettingOutCommands.cs',
'''            radial.SetDatabaseDefaults(database);\n            SetClosedFilledDimensionArrow(radial, database);\n            PaperAnnotationScale.SetAnnotative(radial);''',
'''            radial.SetDatabaseDefaults(database);\n            PositionRadialText(radial, dimension, textHeight);\n            SetClosedFilledDimensionArrow(radial, database);\n            PaperAnnotationScale.SetAnnotative(radial);''', 1)
replace('VertexSettingOutCommands.cs',
'''            radial.LeaderLength = Math.Max(textHeight * 3.0, dimension.Radius * 0.15);\n            SetClosedFilledDimensionArrow(radial, radial.Database);\n            return true;''',
'''            radial.LeaderLength = Math.Max(textHeight * 3.0, dimension.Radius * 0.15);\n            PositionRadialText(radial, dimension, textHeight);\n            SetClosedFilledDimensionArrow(radial, radial.Database);\n            return true;''', 1)
replace('VertexSettingOutCommands.cs',
'''        private static void PopulateTable(''',
'''        private static void PositionRadialText(\n            RadialDimension radial,\n            VertexRadialDimension dimension,\n            double textHeight)\n        {\n            if (radial == null || dimension == null) return;\n            Vector3d direction = dimension.ChordPoint - dimension.Center;\n            if (direction.Length <= 1e-8) direction = Vector3d.XAxis;\n            direction = direction.GetNormal();\n            double offset = Math.Max(textHeight * 4.0, dimension.Radius * 0.20);\n            try\n            {\n                radial.TextPosition = dimension.Center +\n                    direction * (dimension.Radius + offset);\n            }\n            catch { }\n        }\n\n        private static void PopulateTable(''', 1)
replace('VertexSettingOutCommands.cs',
'''            double maximum = Math.Max(defaultOffset * 5.0, defaultOffset);''',
'''            double maximum = Math.Max(defaultOffset * 3.0, defaultOffset);''', 1)

# 7. Global report tables are centred and immediately regenerated.
replace('GridReportPresenter.cs',
'''using System.Collections.Generic;\nusing System.Windows;''',
'''using System.Collections.Generic;\nusing System.Reflection;\nusing System.Windows;''', 1)
replace('GridReportPresenter.cs',
'''                            table.Cells[tableRow, columnIndex].Alignment =\n                                CellAlignment.MiddleLeft;''',
'''                            table.Cells[tableRow, columnIndex].Alignment =\n                                CellAlignment.MiddleCenter;''', 1)
replace('GridReportPresenter.cs',
'''                    table.GenerateLayout();\n                    currentSpace.AppendEntity(table);\n                    transaction.AddNewlyCreatedDBObject(table, true);\n                    transaction.Commit();''',
'''                    currentSpace.AppendEntity(table);\n                    transaction.AddNewlyCreatedDBObject(table, true);\n                    ForceTableGraphics(table);\n                    transaction.Commit();''', 1)
replace('GridReportPresenter.cs',
'''        private static string GetValue(IList<string> row, int columnIndex)''',
'''        private static void ForceTableGraphics(Table table)\n        {\n            if (table == null) return;\n            try { table.GenerateLayout(); } catch { }\n            try { table.RecordGraphicsModified(true); } catch { }\n            try\n            {\n                MethodInfo method = table.GetType().GetMethod(\n                    "RecomputeTableBlock",\n                    BindingFlags.Public | BindingFlags.Instance,\n                    null,\n                    new[] { typeof(bool) },\n                    null);\n                if (method != null) method.Invoke(table, new object[] { true });\n            }\n            catch { }\n        }\n\n        private static string GetValue(IList<string> row, int columnIndex)''', 1)

# 8. Remove the hard-coded 1 m sewer topology fallback. Use actual endpoint geometry.
replace('SewerProductionCommands.cs',
'''                double length = pipe.Length3DCenterToCenter;\n                if (double.IsNaN(length) || double.IsInfinity(length) || length <= 0.0)\n                    length = 1.0;''',
'''                double length = pipe.Length3DCenterToCenter;\n                if (double.IsNaN(length) || double.IsInfinity(length) || length <= 0.0)\n                {\n                    try\n                    {\n                        length = pipe.GetPointAtParam(0.0).DistanceTo(\n                            pipe.GetPointAtParam(1.0));\n                    }\n                    catch\n                    {\n                        length = start.Position.DistanceTo(end.Position);\n                    }\n                }\n                if (double.IsNaN(length) || double.IsInfinity(length) || length <= 0.0)\n                    throw new InvalidOperationException(\n                        "A sewer pipe has no readable Civil 3D or geometric length.");''', 1)

# 9. Arc/circle conversion: preserve exact curved geometry with bulges.
replace('CurveConversionCommands.cs',
'''            List<Point3d> points = Sample(\n                source,\n                maximumSegment,\n                minimumArcVertices,\n                minimumCircleVertices);''',
'''            if (!to3d)\n            {\n                Arc sourceArc = source as Arc;\n                if (sourceArc != null)\n                    return CreateExactArcPolyline(sourceArc, flatten);\n                Circle sourceCircle = source as Circle;\n                if (sourceCircle != null)\n                    return CreateExactCirclePolyline(sourceCircle, flatten);\n            }\n\n            List<Point3d> points = Sample(\n                source,\n                maximumSegment,\n                minimumArcVertices,\n                minimumCircleVertices);''', 1)
replace('CurveConversionCommands.cs',
'''        private static List<Point3d> Sample(''',
'''        private static Polyline CreateExactArcPolyline(Arc arc, bool flatten)\n        {\n            double sweep = arc.EndAngle - arc.StartAngle;\n            while (sweep <= 0.0) sweep += Math.PI * 2.0;\n            int segments = Math.Max(1, (int)Math.Ceiling(sweep / Math.PI));\n            var polyline = new Polyline(segments + 1);\n            for (int index = 0; index <= segments; index++)\n            {\n                double angle = arc.StartAngle + sweep * index / segments;\n                Point3d point = arc.Center +\n                    new Vector3d(Math.Cos(angle), Math.Sin(angle), 0.0) * arc.Radius;\n                double bulge = index < segments\n                    ? Math.Tan((sweep / segments) / 4.0)\n                    : 0.0;\n                polyline.AddVertexAt(\n                    index,\n                    new Point2d(point.X, point.Y),\n                    bulge,\n                    0.0,\n                    0.0);\n            }\n            polyline.Elevation = flatten ? 0.0 : arc.Center.Z;\n            polyline.Closed = false;\n            return polyline;\n        }\n\n        private static Polyline CreateExactCirclePolyline(Circle circle, bool flatten)\n        {\n            const int segments = 4;\n            double bulge = Math.Tan(Math.PI / 8.0);\n            var polyline = new Polyline(segments);\n            for (int index = 0; index < segments; index++)\n            {\n                double angle = Math.PI * 2.0 * index / segments;\n                Point3d point = circle.Center +\n                    new Vector3d(Math.Cos(angle), Math.Sin(angle), 0.0) * circle.Radius;\n                polyline.AddVertexAt(\n                    index,\n                    new Point2d(point.X, point.Y),\n                    bulge,\n                    0.0,\n                    0.0);\n            }\n            polyline.Elevation = flatten ? 0.0 : circle.Center.Z;\n            polyline.Closed = true;\n            return polyline;\n        }\n\n        private static List<Point3d> Sample(''', 1)

# 10. Cosmetic ribbon command text only: strip leading CE/CE Tools while preserving commands.
replace('PluginEntry.cs',
'''        private static RibbonCommandDefinition Cmd(\n            string text,\n            string command,\n            string toolTip)\n        {\n            return new RibbonCommandDefinition(text, command, toolTip);\n        }''',
'''        private static RibbonCommandDefinition Cmd(\n            string text,\n            string command,\n            string toolTip)\n        {\n            return new RibbonCommandDefinition(\n                NormalizeDisplayText(text),\n                command,\n                toolTip);\n        }\n\n        private static string NormalizeDisplayText(string text)\n        {\n            string value = (text ?? string.Empty).Trim();\n            foreach (string prefix in new[] { "CE Tools ", "CE " })\n            {\n                if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))\n                    return value.Substring(prefix.Length).TrimStart();\n            }\n            return value;\n        }''', 1)

print('Latest runtime source patch applied.')
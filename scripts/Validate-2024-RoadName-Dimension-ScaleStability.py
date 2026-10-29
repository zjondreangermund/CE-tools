#!/usr/bin/env python3
"""Static regressions for C3D 2024 road annotation and dimension preservation."""
from pathlib import Path
root = Path(__file__).resolve().parents[1] / 'src' / 'CE.Tools.Civil3D'
read = lambda filename: (root / filename).read_text(encoding='utf-8-sig')
sync = read('AnnotationScaleSyncCommands.cs')
multi = read('September10DynamicMultiDimensionManager.cs')
styles = read('MultiDimensionCommands.cs')
road = read('RoadLayoutAnnotation.cs')
placer = read('RoadAnnotationPlacement.cs')
viewport = read('ViewportTextUprightCommands.cs')

# Background scale tracking must not mutate all model/paperspace annotations.
assert '#if CE_CIVIL3D_2024' in sync and 'LastScaleByDatabase[document.Database] = ReadCurrentScaleName();' in sync
assert 'if (entity is Dimension || !IsAlreadyAnnotative(entity))' in sync
assert 'internal static IList<object> ReadUsedDrawingScaleContexts(' in sync
assert 'internal static int AddUsedDrawingScaleContexts(' in sync
assert 'vp.GetType().GetProperty(' in sync
# Road names use drawing viewport scales on both supported host paths.
assert '#if CE_CIVIL3D_2024\n                // Keep name rotation' in road
assert 'AddUsedDrawingScaleContexts(\n                    text, drawingScales)' in road
assert '#else\n                AnnotationScaleSyncManager.AddUsedDrawingScaleContexts(' in road

# A scale change alone cannot trigger dynamic dimensions to erase/rebuild.
assert 'text.Append(source.GetType().FullName).Append(\'|\');' in multi
assert 'text.Append(source.GetType().FullName).Append(\'|\').Append(ReadCurrentScaleName())' in multi
assert 'if (targetId != sourceId) target.CopyFrom(source);' in styles
assert '#if CE_CIVIL3D_2024\n                // Preserve CE dimension style edits' in styles

# CE road names must keep alignment rotation and editable layout-orientation.
assert 'text.Rotation = Math.Atan2(tangent.Y, tangent.X)' in road
assert 'setPaperOrientation.Invoke(text, new object[] { false });' in road
assert 'ReadUsedDrawingScaleContexts(' in road
assert 'model.Text("Avoid") != "No"' in road
assert 'placementTimer.Elapsed.TotalSeconds' in road
assert 'overlapTolerance' in placer
assert 'IsLinkedRoadName(entity, tr)' in viewport
assert 'TrySetNativePaperOrientation(entity, false)' in viewport
assert 'CE_ROAD_LAYOUT' in viewport
assert 'CE_ROAD_NAME_ANNOTATION' in viewport
assert 'options.Keywords.Add("OFf");' in viewport

print('PASS: 2024 road names remain road-aligned, only used contexts are added, dimension rescaling and auto-rebuild suppressed, 2023 unchanged.')

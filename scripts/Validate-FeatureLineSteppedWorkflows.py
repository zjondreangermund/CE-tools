#!/usr/bin/env python3
"""Protect linked stepped-offset and stepped feature-line healing workflows."""

from __future__ import annotations

from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "src" / "CE.Tools.Civil3D"
errors: list[str] = []


def read(name: str) -> str:
    path = SRC / name
    if not path.is_file():
        errors.append(f"Missing Civil 3D source: {name}")
        return ""
    return path.read_text(encoding="utf-8-sig")


relative = read("FeatureLineRelativeCommands.cs")
healing = read("FeatureLineSteppedJoinCommands.cs")
construction = read("FeatureLineConstructionCommands.cs")
ribbon = read("PluginEntry.cs")
refresh = read("CommentPresentationCommands.cs")
junction = read("August13JunctionFallbackCommands.cs")
grading = read("August23PlatformDynamicGradingCommands.cs")
road_refresh = read("August24RoadElevationDynamicManager.cs")

required = {
    "FeatureLineRelativeCommands.cs": (
        '"CE_FLRELCREATE"',
        '"CE_FLRELUPDATE"',
        '"CE Tools - Linked Stepped Feature Lines"',
        'settings.AddPositiveDouble(',
        'settings.AddPositiveInteger(',
        'public static int RefreshAll(Document document)',
        'private const string RecordKey = "CE_FLREL";',
        '"VerticalMode"',
        '"Grade (%)"',
        '"Slope (H:V)"',
        '"Offset side"',
        '"Left"',
        '"Right"',
        '"Both sides"',
        '"Select multiple SOURCE feature lines for stepped offsets: "',
    ),
    "FeatureLineSteppedJoinCommands.cs": (
        '"CE_FLSTEPJOIN"',
        '"CE Tools - Heal Stepped Feature Lines"',
        '"GapTolerance"',
        'List<FeaturePiece> ordered = OrderPieces(',
        'List<Point3d> joinedPoints = FlattenPieces(ordered);',
        'new Polyline3d(',
        'CivilFeatureLine.Create(outputName, sourcePolyline.ObjectId)',
        'sourcePoints.Cast<Point3d>().ToList()',
    ),
    "FeatureLineConstructionCommands.cs": (
        '"Heal stepped feature lines", "CE_FLSTEPJOIN"',
    ),
    "PluginEntry.cs": (
        '"Heal Stepped Feature Lines", "CE_FLSTEPJOIN "',
        '"Create Linked Offset Set", "CE_FLRELCREATE "',
        '"Update Linked Offset Set", "CE_FLRELUPDATE "',
    ),
    "CommentPresentationCommands.cs": (
        "FeatureLineRelativeCommands.RefreshAll(document)",
    ),
    "August13JunctionFallbackCommands.cs": (
        '"CE_FLRELCREATE"',
        '"CE_GRADINGSLOPETOOLS"',
        '"CE_JUNCTIONGRADETOSURFACE"',
    ),
    "August23PlatformDynamicGradingCommands.cs": (
        '"CE_PLATFORMGRADETOSURFACE"',
        '"CE_JUNCTIONGRADETOSURFACE"',
        '"Grade to Surface"',
        '"CutFormat"',
        '"FillFormat"',
        '"Slope (H:V)"',
        '"Grade (%)"',
        '"CutSlope"',
        '"CutGrade"',
        '"FillSlope"',
        '"FillGrade"',
        '"ShowSlopeLines"',
        '"CutSlopeLayer"',
        '"FillSlopeLayer"',
        'TryCreateSlopeLines(',
        'CleanupHandleList(',
        '"CE-JUNCTION-CUT-SLOPES"',
        '"CE-JUNCTION-FILL-SLOPES"',
    ),
}

texts = {
    "FeatureLineRelativeCommands.cs": relative,
    "FeatureLineSteppedJoinCommands.cs": healing,
    "FeatureLineConstructionCommands.cs": construction,
    "PluginEntry.cs": ribbon,
    "CommentPresentationCommands.cs": refresh,
    "August13JunctionFallbackCommands.cs": junction,
    "August23PlatformDynamicGradingCommands.cs": grading,
    "August24RoadElevationDynamicManager.cs": road_refresh,
}

for name, markers in required.items():
    for marker in markers:
        if marker not in texts[name]:
            errors.append(f"{name} is missing stepped-workflow marker: {marker}")
    if texts[name].count("{") != texts[name].count("}"):
        errors.append(f"{name} has unbalanced braces")

create_body = relative.split("private static void Create(Document document)", 1)[-1]
create_body = create_body.split("private static void Update(Document document)", 1)[0]
for legacy_prompt in (
    "Horizontal step distance <1.000>",
    "Number of linked stepped offsets <1>",
    "Linked feature-line name prefix <",
    "Create these linked stepped-offset feature lines",
):
    if legacy_prompt in create_body:
        errors.append(f"Linked stepped creation restored command-line settings: {legacy_prompt}")

update_body = relative.split("private static void Update(Document document)", 1)[-1]
update_body = update_body.split("public static int RefreshAll(Document document)", 1)[0]
if "Confirm(editor" in update_body:
    errors.append("CE_FLRELUPDATE must rebuild the selected source set without a second confirmation")

if '"CE_FLOFFSET"' in junction:
    errors.append("Junction stepped-offset fallback must not route back to the legacy single-source CE_FLOFFSET command")
if '"CE_PLATFORMGRADETOSURFACE"' in junction:
    errors.append("Junction fallback must route through the ordered CE_JUNCTIONGRADETOSURFACE workflow")
if '100.0 / Math.Max(0.001, Math.Abs(settings.Double("CutGrade", 50.0)))' not in grading:
    errors.append("Junction grade-to-surface must convert cut Grade (%) to the equivalent H:V daylight ratio")
if '100.0 / Math.Max(0.001, Math.Abs(settings.Double("FillGrade", 50.0)))' not in grading:
    errors.append("Junction grade-to-surface must convert fill Grade (%) to the equivalent H:V daylight ratio")
if 'settings.Text("VerticalMode")' not in create_body or 'settings.Text("SlopeDirection")' not in create_body:
    errors.append("Linked stepped offsets must calculate vertical change from the selected elevation/grade/slope mode")
if 'ResolveNamedOffsetSign' not in create_body:
    errors.append("Linked stepped offsets must retain selectable side control for bellmouth/source offsets")

if '"Select multiple SOURCE feature lines for stepped offsets: "' not in relative:
    errors.append("CE_FLRELCREATE must expose one batch selection set for multiple source feature lines")
if 'selection.Value.GetObjectIds().Distinct()' not in create_body:
    errors.append("CE_FLRELCREATE must process every distinct selected feature line")
if '"Show cut / fill slope lines"' not in grading:
    errors.append("Junction grading must expose the slope-line display option")
if '"CE_FLRELCREATEBATCH"' not in relative or '"CE_FLRELCREATEBATCH"' not in junction:
    errors.append("Junction stepped offsets must expose and route through the explicit multi-feature-line batch command")
if '"Slope-line interval / frequency (m)"' not in grading or '"SlopeLineInterval"' not in grading:
    errors.append("Junction grading must expose configurable slope-ray spacing/frequency")
if 'TryCreateCivilSlopeRay(' not in grading or 'CivilFeatureLine.Create(' not in grading:
    errors.append("Junction cut/fill slope rays must be native Civil 3D feature lines rather than plain AutoCAD lines")
if 'cut ? cutLayerId : fillLayerId' not in grading:
    errors.append("Cut and fill Civil 3D slope rays must be separated by layer")
if 'featureLine.Explode(exploded)' not in grading or 'curve.GetPointAtDist(localDistance)' not in grading:
    errors.append("Bellmouth grading rays must sample the actual exploded curve geometry by chainage")
if 'curve.GetFirstDerivative(point)' not in grading:
    errors.append("Bellmouth grading rays must use the local curve tangent for their normal direction")
if 'sample.HalfLength = (validRayIndex++ % 2) == 1' not in grading or 'Halfway(sample.Point, sample.EndPoint)' not in grading:
    errors.append("Every second cut/fill grading ray must be half length while preserving the full daylight classification")
if 'curveSampleSpacing' not in grading or 'BuildSlopeRaySamples(' not in grading or 'curveSamples[index]' not in grading:
    errors.append("Grade-to-surface daylight boundary must be sampled from the real bellmouth/road curve geometry")
if 'internal static int RefreshLinkedGrades(' not in grading:
    errors.append("Linked road/junction grade-to-surface sources must expose targeted automatic refresh")
if 'August23PlatformDynamicGradingCommands.RefreshLinkedGrades(' not in road_refresh:
    errors.append("Road elevation edits must refresh only affected linked daylight/grading sources")
if 'August24RoadElevationDynamicManager.Initialize();' not in ribbon or 'August24RoadElevationDynamicManager.Terminate();' not in ribbon:
    errors.append("Targeted road daylight refresh manager must be initialized and terminated with CE Tools")

if "gapTolerance" not in healing or "best.Distance > gapTolerance" not in healing:
    errors.append("Stepped healing no longer protects the maximum bridge distance")
if "if (!sourcePolyline.IsErased) sourcePolyline.Erase();" not in healing:
    errors.append("Stepped healing no longer cleans up its temporary 3D polyline")

if errors:
    print("CE Tools stepped feature-line workflow validation failed:", file=sys.stderr)
    for error in errors:
        print(f"- {error}", file=sys.stderr)
    raise SystemExit(1)

print(
    "Stepped feature-line workflows passed: popup multi-offset creation, automatic linked refresh, "
    "multi-source creation, bellmouth side control, curve-following alternating long/half cut-fill grading rays, grade/slope vertical modes, one-selection set rebuild, gap-tolerant healing and endpoint-vertex preservation are protected."
)

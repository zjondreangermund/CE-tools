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
        '"CE_PLATFORMGRADETOSURFACE"',
    ),
}

texts = {
    "FeatureLineRelativeCommands.cs": relative,
    "FeatureLineSteppedJoinCommands.cs": healing,
    "FeatureLineConstructionCommands.cs": construction,
    "PluginEntry.cs": ribbon,
    "CommentPresentationCommands.cs": refresh,
    "August13JunctionFallbackCommands.cs": junction,
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
if 'settings.Text("VerticalMode")' not in create_body or 'settings.Text("SlopeDirection")' not in create_body:
    errors.append("Linked stepped offsets must calculate vertical change from the selected elevation/grade/slope mode")
if 'ResolveNamedOffsetSign' not in create_body:
    errors.append("Linked stepped offsets must retain selectable side control for bellmouth/source offsets")

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
    "multi-source creation, bellmouth side control, grade/slope vertical modes, one-selection set rebuild, gap-tolerant healing and endpoint-vertex preservation are protected."
)

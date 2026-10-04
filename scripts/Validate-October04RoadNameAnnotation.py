#!/usr/bin/env python3
"""Regression guards for the Oct 4 centered road-name annotation workflow."""

from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "src" / "CE.Tools.Civil3D"

files = {
    "command": SRC / "RoadNameAnnotationCommands.cs",
    "sync": SRC / "August11RoadNamingCurveCommands.cs",
    "supplementary": SRC / "August24FieldCompletionCommands.cs",
    "field": SRC / "September11FieldCompletionMenu.cs",
}

texts = {}
errors = []
for key, path in files.items():
    if not path.is_file():
        errors.append("Missing source: " + str(path))
        texts[key] = ""
    else:
        texts[key] = path.read_text(encoding="utf-8-sig")

command = texts["command"]
sync = texts["sync"]
supplementary = texts["supplementary"]
field = texts["field"]

for marker in [
    '"CE_ROADNAMEANNOTATE"',
    'CommandFlags.UsePickSet',
    '"All road alignments"',
    '"Selected road alignments"',
    'ReadAllRoadAlignmentIds(',
    'ReadSelectedRoadAlignmentIds(',
    'corridor.Baselines',
    'baseline.AlignmentId',
    'TryResolveCentredPlacement(',
    'AboveNormal(',
    'AttachmentPoint.BottomCenter',
    'PaperAnnotationScale.ModelDistance(',
    'PaperAnnotationScale.ModelTextHeight(',
    '"CE-ROAD-NAME"',
    '"CE_ROAD_NAME_ANNOTATION"',
    'ReadExistingLabels(',
    'WriteLabelLink(',
    '"Description (fallback to name)"',
    '"Text colour (ACI)"',
    '"Background mask"',
]:
    if marker not in command:
        errors.append("Missing road-name annotation marker: " + marker)

for marker in [
    '"CE_ROAD_NAME_ANNOTATION"',
    'GetXDataForApplication(',
]:
    if marker not in sync:
        errors.append("Missing generated-label feedback guard: " + marker)

for marker in [
    '"CE-Road Names - Centre Above Roads"',
    '"CE_ROADNAMEANNOTATE"',
]:
    if marker not in supplementary:
        errors.append("Missing road supplementary menu marker: " + marker)

for marker in [
    '"Road Names - Centre Above Roads"',
    '"CE_ROADNAMEANNOTATE"',
]:
    if marker not in field:
        errors.append("Missing Field Completion road-name marker: " + marker)

if errors:
    print("October 4 road-name annotation validation FAILED", file=sys.stderr)
    for error in errors:
        print(" - " + error, file=sys.stderr)
    raise SystemExit(1)

print(
    "October 4 road-name annotation validation passed: all/selected road scope, "
    "centred midpoint placement, readable above-road offset, persisted presentation "
    "settings, duplicate-safe updates and menu wiring are guarded."
)

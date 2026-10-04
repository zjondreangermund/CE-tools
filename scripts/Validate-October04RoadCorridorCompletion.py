#!/usr/bin/env python3
"""Regression guards for the 4 Oct road-corridor / BOQ field fixes."""

from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "src" / "CE.Tools.Civil3D"

files = {
    "corridor": SRC / "RoadCorridorCompletionCommands.cs",
    "boq": SRC / "August13RoadConstructionBoqCommands.cs",
    "field": SRC / "September24FieldCompletionCommands.cs",
    "workflow": SRC / "DisciplineWorkflowDialogs.cs",
}

texts = {}
errors = []
for key, path in files.items():
    if not path.is_file():
        errors.append(f"Missing source: {path}")
        texts[key] = ""
    else:
        texts[key] = path.read_text(encoding="utf-8-sig")

corridor = texts["corridor"]
boq = texts["boq"]
field = texts["field"]
workflow = texts["workflow"]

required_corridor = [
    '"BasicSidewalkSlopeMode"',
    '"BasicSidewalkSlope"',
    '"Apply sloped sidewalk"',
    '"Subassembly.SidewalkSlopesAndBase"',
    'assembly.ReplaceSubassembly(',
    'ApplySidewalkSlopeParameter(',
    '"CodeSetStyle"',
    '"Code Set Style"',
    'CodeSetStyleName = model.Text("CodeSetStyle")',
    'assembly.CodeSetStyleId =',
    'CreateMissingSlopePatterns(',
    '"HINGE_CUT"',
    '"HINGE_FILL"',
    '"DAYLIGHT_CUT"',
    '"DAYLIGHT_FILL"',
    'patterns.Add(',
    '"IsBuild"',
    '"BottomLinks"',
    'ReadCompleteCorridorSelection(',
    'WriteCompleteCorridorSelection(',
    '"Road Length (m)"',
    'TotalRoadLength',
]
for marker in required_corridor:
    if marker not in corridor:
        errors.append("Missing road-corridor marker: " + marker)

required_boq = [
    'FindBottomCorridorSurface(',
    '"BOTTOM-RD-"',
    'if (!datum.IsBuild)',
    'datum.IsBuild = true;',
    'corridor.Rebuild();',
    '"Road length - " + road.Key',
    '"Total road length"',
    'TotalRoadLength',
    'RoadLengths',
]
for marker in required_boq:
    if marker not in boq:
        errors.append("Missing BOQ marker: " + marker)

required_field = [
    'IEnumerable<ObjectId> initiallySelected = null',
    'IsChecked = selectedIds.Contains(choice.Id)',
]
for marker in required_field:
    if marker not in field:
        errors.append("Missing remembered multi-select marker: " + marker)

required_workflow = [
    'LastWorkflowCommands',
    'RememberWorkflowCommand(',
    'LastWorkflowCommand(title)',
    'initialButton.Focus();',
    'initialButton.BringIntoView();',
]
for marker in required_workflow:
    if marker not in workflow:
        errors.append("Missing remembered workflow marker: " + marker)

for forbidden in [
    'FindCorridorSurface(corridor, "CE-BOTTOM")',
    'CE-BOTTOM corridor surface is missing or is not built',
]:
    if forbidden in boq:
        errors.append("Obsolete CE-BOTTOM BOQ behavior remains: " + forbidden)

if 'foreach (object pattern in CivilStyleDiscovery.Enumerate(patterns))' not in corridor:
    errors.append("Existing slope patterns are no longer refreshed.")
if 'CreateMissingSlopePatterns(' not in corridor:
    errors.append("Missing slope-pattern creation fallback for reruns.")

if errors:
    print("October 4 road corridor completion validation FAILED", file=sys.stderr)
    for error in errors:
        print(" - " + error, file=sys.stderr)
    raise SystemExit(1)

print(
    "October 4 road corridor completion validation passed: BasicSidewalk slope "
    "upgrade, code-set style assignment, native slope-pattern recreation, "
    "remembered defaults, road lengths and road-numbered BOTTOM surfaces are guarded."
)

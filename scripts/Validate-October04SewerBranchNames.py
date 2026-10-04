#!/usr/bin/env python3
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "src" / "CE.Tools.Civil3D"

align = (SRC / "SewerBranchAlignmentCommands.cs").read_text(encoding="utf-8-sig")
place = (SRC / "SewerBranchLabelPlacement.cs").read_text(encoding="utf-8-sig")
settings = (SRC / "SewerProductionCommands.cs").read_text(encoding="utf-8-sig")
supp = (SRC / "August24FieldCompletionCommands.cs").read_text(encoding="utf-8-sig")
field = (SRC / "September11FieldCompletionMenu.cs").read_text(encoding="utf-8-sig")

errors = []

for marker in [
    '"CE_SEWBRANCHNAMES"',
    '"All sewer branches"',
    '"Selected sewer parts"',
    '"Branch-name side"',
    '"Above"',
    'ReadAllSequencedSewerNetworks(',
    'ReadSelectedNetworks(',
    'BuildNetworkPlan(',
    'RemoveExistingGeneratedLabels(',
    'SewerBranchLabelPlacement.BuildPlacements(',
    'SewerBranchLabelPlacement.ConfigureLabel(',
    'BuildTag(\n                                        branchKey,\n                                        "Label")',
]:
    if marker not in align:
        errors.append("Missing branch-name command marker: " + marker)

for marker in [
    'runCentreDistance',
    'pipeCentreDistance',
    '"Every second pipe"',
    'label.ColorIndex = 3;',
    'label.Rotation = placement.Rotation;',
    'AttachmentPoint.BottomCenter',
]:
    if marker not in place:
        errors.append("Missing requested branch-name presentation marker: " + marker)

if 'public string BranchLabelSide { get; set; } = "Above";' not in settings:
    errors.append("New sewer drawings do not default branch labels above the run.")

for source, marker in [
    (supp, '"CE-Sewer Branch Names - Centre Above Pipes", "CE_SEWBRANCHNAMES"'),
    (field, '"Sewer Branch Names - Centre Above Pipes"'),
    (field, '"CE_SEWBRANCHNAMES"'),
]:
    if marker not in source:
        errors.append("Missing sewer branch-name menu marker: " + marker)

if errors:
    print("October 4 sewer branch-name validation FAILED", file=sys.stderr)
    for error in errors:
        print(" - " + error, file=sys.stderr)
    raise SystemExit(1)

print(
    "October 4 sewer branch-name validation passed: green Branch-x text, "
    "straight-run centre placement, readable rotation, above-pipe default, "
    "long-run repeats, all/selected scope and menu wiring are guarded."
)

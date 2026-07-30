#!/usr/bin/env python3
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
checks = {
    "src/CE.Tools.Civil3D/SewerSequenceCommands.cs": [
        "SewerBranchAlignmentCommands.RequestAutomaticRun",
        "MH\" + (index + 1)",
    ],
    "src/CE.Tools.Civil3D/SewerBranchAlignmentCommands.cs": [
        "alignment direction=first manhole to connection point",
        "BuildLabelPlacements",
        "BranchLabelRepeatSpacing",
        "label.Annotative = AnnotativeStates.True",
        "label.Rotation = placement.Rotation",
        "[2.5/3.5/5]",
        "[Above/Below]",
    ],
    "src/CE.Tools.Civil3D/SewerProductionCommands.cs": [
        "SurfaceSelectionWindow",
        "WorkflowRepairCommands.ReadSurfaceChoices(document)",
        "CreateProfileObjects",
        "AddBranchParts",
    ],
    "src/CE.Tools.Civil3D/SewerLabelLayoutCommands.cs": [
        "CE_SEWLABELSORT",
        "CE_SEWLABELFREEZE",
        "OverlapsAny",
    ],
    "src/CE.Tools.Civil3D/PluginEntry.cs": [
        "Fix Stormwater Label Spacing",
        "Fix Water Label Spacing",
        "Apply Styles and Fix Label Spacing",
    ],
}

errors = []
for relative, needles in checks.items():
    text = (ROOT / relative).read_text(encoding="utf-8")
    for needle in needles:
        if needle not in text:
            errors.append(f"{relative}: missing {needle!r}")
if errors:
    raise SystemExit("\n".join(errors))
print("Sewer/water/stormwater red-yellow V25 source validation passed.")

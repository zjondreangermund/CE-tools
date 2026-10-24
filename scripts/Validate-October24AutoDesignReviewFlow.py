#!/usr/bin/env python3
"""Source-level regression checks for the AutoDesign screenshot follow-up.

This is not an Autodesk Civil 3D runtime test. It verifies that the supported
Road/Sewer source-selection paths and manual engineering review gates remain.
"""
from pathlib import Path

root = Path(__file__).resolve().parents[1]
command = (root / "src/CE.Tools.Civil3D/AutoDesignCommands.cs").read_text(encoding="utf-8")
alignment = (root / "src/CE.Tools.Civil3D/SewerBranchAlignmentCommands.cs").read_text(encoding="utf-8")

checks = {
    "workflow selected before scanning Sewer networks":
        command.index('string scope = workflow.Text("Scope");')
        < command.index("GetPipeNetworkIds()"),
    "Road context popup":
        'CE Tools - AutoDesign: Road Inputs' in command,
    "Road alignment options":
        'Road alignment / review focus' in command and "GetAlignmentIds()" in command,
    "road-wide changes require consent when focused":
        'CE AutoDesign - Road scope' in command and "drawing-wide refresh of all eligible" in command,
    "Sewer source selection conditional":
        'if (sewer)\n            {\n                setStage("reading Sewer networks")' in command,
    "no unconditional stale alignment approval":
        'add("alignment-review"' not in command,
    "missing alignment assessment":
        "CountMissingForAutoDesign(doc, network)" in command
        and "internal static int CountMissingForAutoDesign" in alignment,
    "explicit profile approval remains":
        'AutoDesignWorkflowPolicy.ProfileApproval' in command,
    "actionable profile setup":
        'Open Sewer Profile Setup (CE_SEWPROFILE)' in command
        and 'SendStringToExecute("_.CE_SEWPROFILE' in command,
    "final native validation remains":
        'Validate(doc, sewer, roads, network, true)' in command,
}
for name, passed in checks.items():
    if not passed:
        raise AssertionError("AutoDesign regression: " + name)
    print("PASS:", name)

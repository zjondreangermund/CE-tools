#!/usr/bin/env python3
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

checks = {
    "src/CE.Tools.Civil3D/WorkflowRepairCommands.cs": [
        "Numbering order [Selection/LeftToRight/TopToBottom]",
        "Selection/window order",
        "validation.Candidates.Sort",
    ],
    "src/CE.Tools.Civil3D/AnnotationCommands.cs": [
        "ParkingNumberLinkStore.Link",
        "text.Annotative = AnnotativeStates.True",
    ],
    "src/CE.Tools.Civil3D/ParkingNumberLinkStore.cs": [
        "label.Location = centre",
        "label.Erase()",
    ],
    "src/CE.Tools.Civil3D/ParkingReportLinkStore.cs": [
        "CE_TOOLS_PARK_REPORT",
        "public static int RefreshAll",
        "table.SetSize(groups.Count + 2, 2)",
    ],
    "src/CE.Tools.Civil3D/CommentPresentationCommands.cs": [
        "ParkingReportLinkStore.RefreshAll(document)",
    ],
    "src/CE.Tools.Civil3D/ParkingSkewValidationCommands.cs": [
        "TryShortestPolygonEdge",
        "perpendicular bounding-box projection under-reports skew",
        "bayWidthAxis",
    ],
    "src/CE.Tools.Civil3D/ClosedParkingBayWorkflow.cs": [
        "One selectable block per parking bay",
        "CreateBayBlockDefinition",
        "AppendBayBlock",
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

print("Parking red/yellow V24 source validation passed.")

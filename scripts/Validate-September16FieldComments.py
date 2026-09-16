#!/usr/bin/env python3
"""Static regression gate for the September 16 field-comment completion batch."""
from pathlib import Path
import sys

root = Path(__file__).resolve().parents[1]

checks = {
    "src/CE.Tools.Civil3D/MultiDimensionCommands.cs": [
        '"Dynamic", "03 Dynamic", "Dynamic update"',
        "DynamicMultiDimensionManager.BeginCommand(",
        "DynamicMultiDimensionManager.BeginSource(transaction, polyline",
        "DynamicMultiDimensionManager.BeginSource(transaction, featureLine",
        "internal static int RebuildDynamicSource(",
        "DynamicMultiDimensionManager.CaptureOutput(transaction, dimension);",
    ],
    "src/CE.Tools.Civil3D/UniversalDynamicRefreshCommands.cs": [
        "DynamicMultiDimensionManager.RefreshAll(document);",
    ],
    "src/CE.Tools.Civil3D/September16FieldCommentCompletionCommands.cs": [
        '"CE_ROADJUNCTIONBATCH"',
        '"MainHalfWidth"',
        '"SideHalfWidth"',
        '"EndpointTolerance"',
        "IntersectWith(",
        "CrossReturns(",
        "TReturns(",
        "CE_ROADJUNCTIONCONSTRUCTION",
    ],
    "src/CE.Tools.Civil3D/SewerSequenceCommands.cs": [
        "OrderBy(id => nodes[id].RimElevation)",
        "firstEndpointAlreadyAssigned",
        "progresses toward the downstream junction",
    ],
    "src/CE.Tools.Civil3D/SewerNetworkDynamicSequenceManager.cs": [
        "WalkBranchSegment begins at the already-owned junction",
        "branch.Nodes.Reverse();",
        "branch.Edges.Reverse();",
    ],
    "src/CE.Tools.Civil3D/September11FieldCompletionMenu.cs": [
        '"Batch T/Cross Junction Bellmouths"',
        '"Split Multiple Corridors at Junctions"',
        '"Corridor Frequencies / Targets / Slopes"',
        '"Dynamic Dimensions - All Types"',
    ],
}

failed = False
for relative, markers in checks.items():
    path = root / relative
    if not path.is_file():
        print(f"FAIL missing file: {relative}")
        failed = True
        continue
    text = path.read_text(encoding="utf-8")
    for marker in markers:
        if marker not in text:
            print(f"FAIL {relative}: missing {marker}")
            failed = True

# Preserve the non-destructive safety boundaries.
junction = (root / "src/CE.Tools.Civil3D/September16FieldCommentCompletionCommands.cs").read_text(encoding="utf-8")
for forbidden in ("Erase(", "OpenMode.ForWrite) as Curve"):
    if forbidden in junction:
        print(f"FAIL batch junction source must remain read-only: {forbidden}")
        failed = True

if failed:
    sys.exit(1)
print("September 16 field-comment completion regression passed.")

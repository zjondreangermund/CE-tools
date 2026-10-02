#!/usr/bin/env python3
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "src" / "CE.Tools.Civil3D"

files = {
    "excavation": SRC / "SewerExcavationCommentCommands.cs",
    "network": SRC / "NetworkCommentCommands.cs",
    "swap": SRC / "SewerPartSwapCommands.cs",
    "align": SRC / "SewerBranchAlignmentCommands.cs",
    "profile": SRC / "ProfileViewBatchCommands.cs",
    "menu": SRC / "September11FieldCompletionMenu.cs",
}

errors = []
texts = {}
for name, path in files.items():
    if not path.exists():
        errors.append(f"Missing source file: {path.relative_to(ROOT)}")
        texts[name] = ""
    else:
        texts[name] = path.read_text(encoding="utf-8-sig")

required = {
    "excavation": [
        "BuildNetworkSummary(",
        "NetworkExcavationSummary",
        "ExcavationToBottom",
        "TotalExcavationToBedding",
        "TrenchWidths",
        "PipeSizes",
        "PipeSizeQuantities",
        "Bedding",
        "Blanket",
        "Fill",
    ],
    "network": [
        '"Pipe Sizes"',
        '"Pipe-size Excavation"',
        '"Trench Width(s)"',
        '"Exc to Bottom m³"',
        '"Bedding m³"',
        '"Blanket m³"',
        '"Fill m³"',
        '"Total Exc to Bedding m³"',
        "SewerExcavationCommentCommands.BuildNetworkSummary(",
    ],
    "swap": [
        '"CE_SEWPARTSWAPMULTI"',
        '"SwapPartFamilyAndSize"',
        '"Pipe family / size"',
        '"Structure / manhole family / size"',
        '"profile views: "',
        "PartsList",
        "PartFamily",
        "PartSize",
    ],
    "align": [
        '"AllNetworkParts"',
        "ReadAllSequencedSewerNetworks(",
        "EnsureAlignmentDirectionAtBranchStart(",
        '"Reverse"',
        "ForceAlignmentStartStationZero(",
        '"MH" +',
        '".1"',
    ],
    "profile": [
        '"CE_PROFILEVIEWARRANGE"',
        '"Horizontal"',
        '"Vertical"',
        '"Grid"',
        '"Horizontal spacing (drawing units)"',
        '"Vertical spacing (drawing units)"',
        '"Grid columns"',
        "Matrix3d.Displacement(",
        "BranchSortNumber(",
    ],
    "menu": [
        '"CE_SEWPARTSWAPMULTI"',
        '"CE_SEWALIGN"',
        '"CE_PROFILEVIEWARRANGE"',
        "MH1.1, MH2.1, MH3.1",
    ],
}

for name, markers in required.items():
    for marker in markers:
        if marker not in texts[name]:
            errors.append(f"{files[name].name} missing marker: {marker}")
    if texts[name].count("{") != texts[name].count("}"):
        errors.append(f"{files[name].name} has unbalanced braces")

if "new Line(" in texts["swap"]:
    errors.append("Batch sewer part swap must change Civil 3D parts, not draw replacement CAD lines")

if errors:
    print("October 2 sewer/profile batch validation FAILED", file=sys.stderr)
    for error in errors:
        print(" -", error, file=sys.stderr)
    raise SystemExit(1)

print("October 2 sewer/profile batch validation passed.")

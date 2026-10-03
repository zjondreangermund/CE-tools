#!/usr/bin/env python3
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "src" / "CE.Tools.Civil3D"
align = (SRC / "SewerBranchAlignmentCommands.cs").read_text(encoding="utf-8-sig")
menu = (SRC / "September11FieldCompletionMenu.cs").read_text(encoding="utf-8-sig")

errors = []

required_align = [
    '"CE_SEWALIGNSTARTFIX"',
    "ResolveSequencedBranchStart(",
    "firstPipe.SequenceNumber != 1",
    "firstPipe.StartStructureId",
    "firstPipe.EndStructureId",
    "shared main-branch structure such as MH1.2",
    "ReadStructureSortKey(",
    "StartStructureName",
    "EnsureAlignmentDirectionAtBranchStart(",
    '"Reverse"',
    "alignment.StationEquations.Count",
    "alignment.StationEquations.Remove(",
    "alignment.ReferencePoint = startPoint",
    "alignment.ReferencePointStation = 0.0",
    "alignment.StationOffset(",
    "ResolveExistingBranchAlignmentId(",
    "branch.StartStructureName",
    "currentBranchCandidates",
    "item.Key.Value.Branch == branchNumber",
    "key.Value.Sequence == 1",
    "SewerPipeConnections.TryForward(",
]
for marker in required_align:
    if marker not in align:
        errors.append(f"SewerBranchAlignmentCommands.cs missing marker: {marker}")

for forbidden in [
    'string expectedStartName = "MH" +',
    '"MH1.1, MH2.1, MH3.1"',
]:
    if forbidden in align:
        errors.append(f"Old brittle branch-start assumption remains: {forbidden}")

required_menu = [
    '"Sewer Branch Alignments - Start Structure at 0+000"',
    '"CE_SEWALIGNSTARTFIX"',
    "P#.1 topology",
    "one-pipe/short branches",
    "branch's own MH#.1",
]
for marker in required_menu:
    if marker not in menu:
        errors.append(f"September11FieldCompletionMenu.cs missing marker: {marker}")

if align.count("{") != align.count("}"):
    errors.append("SewerBranchAlignmentCommands.cs has unbalanced braces")

if errors:
    print("October 3 sewer alignment start validation FAILED", file=sys.stderr)
    for error in errors:
        print(" -", error, file=sys.stderr)
    raise SystemExit(1)

print("October 3 sewer alignment start validation passed.")

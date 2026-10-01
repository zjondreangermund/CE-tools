#!/usr/bin/env python3
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
errors = []

def read(relative):
    path = ROOT / relative
    if not path.is_file():
        errors.append("missing file: " + relative)
        return ""
    return path.read_text(encoding="utf-8-sig")

junction = read("src/CE.Tools.Civil3D/September16FieldCommentCompletionCommands.cs")
references = read("src/CE.Tools.Civil3D/SewerPartAlignmentBinding.cs")
parts = read("src/CE.Tools.Civil3D/SewerProfilePartsBatchCommands.cs")
labels = read("src/CE.Tools.Civil3D/SewerProfileLabelStyleBatchCommands.cs")
menu = read("src/CE.Tools.Civil3D/September11FieldCompletionMenu.cs")

for marker in [
    '"Add T-junction edge-centre-edge feature lines"',
    '"Feature Line edge-centre-edge"',
    '"Polyline edge-centre-edge"',
    "AddTJunctionLimitFeatureLines(",
    "new Polyline(3)",
    '"T-LIMIT"',
    "Existing bellmouth returns were not closed or modified",
]:
    if marker not in junction:
        errors.append("T-junction edge/centre/edge workflow missing: " + marker)

if "August26CadSupplementaryFieldRuntime.CloseOpenMultiple(document, true);" in junction:
    errors.append("junction bellmouth workflow still closes selected feature lines into loops")

for marker in [
    '"Entire selected sewer network - automatic by branch"',
    "AssignEntireNetworksByBranch(",
    "ResolveBranchAlignments(",
    "TryReadGeneratedAlignmentTag(",
    "PipeBranchPattern",
    "StructureBranchPattern",
    "P1.x / MH1.x use Branch-1",
]:
    if marker not in references:
        errors.append("automatic sewer reference assignment missing: " + marker)

for marker in [
    "pipe.RefAlignmentId == view.AlignmentId",
    "Pipe branch reference is authoritative once assigned",
    "connectedPipe.RefAlignmentId == view.AlignmentId",
    "A junction manhole can legitimately belong to more than one",
]:
    if marker not in parts:
        errors.append("sewer profile branch filtering does not use current per-branch part references: " + marker)

for marker in [
    "pipeLabelsInView",
    "structureLabelsInView",
    "HasPartLabelInView(",
    "GetPartProfileLabelIds() returns labels",
]:
    if marker not in labels:
        errors.append("per-view sewer profile label repair missing: " + marker)

if "HasPartLabel(part, tr" in labels:
    errors.append("old global profile-part label suppression is still active")

for marker in [
    "Batch T/Cross Junction Bellmouths and T-Limits",
    "Assign Sewer Reference Alignments by Branch",
    "Missing labels are checked per profile view",
]:
    if marker not in menu:
        errors.append("Field Completion description missing: " + marker)

if errors:
    print("September 30 junction/sewer completion validation FAILED")
    for error in errors:
        print(" -", error)
    raise SystemExit(1)

print("September 30 junction/sewer completion validation passed.")

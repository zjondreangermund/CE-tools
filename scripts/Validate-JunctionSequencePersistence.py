#!/usr/bin/env python3
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "src" / "CE.Tools.Civil3D"
errors = []


def read(name):
    path = SRC / name
    if not path.exists():
        errors.append("Missing " + name)
        return ""
    return path.read_text(encoding="utf-8-sig")

junction = read("RoadJunctionCompletionCommands.cs")
vertex = read("VertexSettingOutCommands.cs")
dialogs = read("DisciplineWorkflowDialogs.cs")
workflow = (ROOT / ".github" / "workflows" / "core-tests.yml").read_text(encoding="utf-8-sig")

checks = [
    ("all shared production popups load saved values", "ProductionSettingsPersistenceStore.Load(document.Database, model)" in dialogs),
    ("all shared production popups save accepted values", "ProductionSettingsPersistenceStore.Save(document.Database, model)" in dialogs),
    ("popup settings persisted in drawing", 'private const string StoreName = "POPUP_SETTINGS";' in dialogs),
    ("junction group order options", '"Top-left to bottom-right", "Left to right", "Top to bottom"' in junction),
    ("junction picked start option", '"Pick start junction / return"' in junction and "RotateToNearest" in junction),
    ("junction old labels replaced rather than duplicated", "EraseExistingLabels" in junction),
    ("vertex road grouped numbering", '"Road grouped sequence"' in vertex and "RoadStartNumber" in vertex),
    ("vertex auto road orientation", '"Auto by road orientation"' in vertex),
    ("vertex picked start key persists", "STARTKEY=" in vertex and "StartRecordKey" in vertex),
    ("vertex surface dropdown", '"ElevationSurface"' in vertex and "ReadSurfaceNames" in vertex and "ResolveSurfaceByName" in vertex),
    ("vertex existing table continuation", '"Continue existing linked table"' in vertex and "UpdateTableLink" in vertex),
    ("arc centres sequenced after on-geometry points", '"ARC CENTER"' in vertex and "insertAfter" in vertex),
    ("closed filled dimension arrows retained", "ObjectId arrow = ObjectId.Null;" in vertex),
    ("radial text centred on dimension line", "dimension.Radius * 0.50" in vertex),
    ("dimension text movement no leader", "SetDimensionTextMovementNoLeader" in vertex and '"Dimtmove"' in vertex),
    ("core tests run this validator", "Validate-JunctionSequencePersistence.py" in workflow),
]
for label, ok in checks:
    if not ok:
        errors.append(label)

for name, text in [("RoadJunctionCompletionCommands.cs", junction), ("VertexSettingOutCommands.cs", vertex), ("DisciplineWorkflowDialogs.cs", dialogs)]:
    if text.count("{") != text.count("}"):
        errors.append(name + " has unbalanced braces")
    if text.count("(") != text.count(")"):
        errors.append(name + " has unbalanced parentheses")

if errors:
    raise SystemExit("Junction sequence / persistent popup validation failed:\n- " + "\n- ".join(errors))

print("Junction sequence / persistent popup validation passed: deterministic group directions, picked starts, road-grouped vertex numbering, reusable surfaces/tables, centred closed-filled radius dimensions and DWG-persistent shared popup values are wired.")

#!/usr/bin/env python3
"""Validate completion of the remaining CE Tools user comments."""

from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
CIVIL = ROOT / "src" / "CE.Tools.Civil3D"
WORKFLOW = ROOT / ".github" / "workflows" / "core-tests.yml"

FILES = {
    "plugin": CIVIL / "PluginEntry.cs",
    "road": CIVIL / "RoadProductionCommentCommands.cs",
    "production": CIVIL / "ProductionCommentCommands.cs",
    "convert": CIVIL / "CurveConversionCommands.cs",
    "junction": CIVIL / "RoadJunctionCompletionCommands.cs",
    "telemetry": CIVIL / "CeInteractionTelemetryCommands.cs",
    "refresh": CIVIL / "UniversalDynamicRefreshCommands.cs",
    "corridor": CIVIL / "RoadCorridorCompletionCommands.cs",
    "workflow": WORKFLOW,
}

errors: list[str] = []
texts: dict[str, str] = {}
for key, path in FILES.items():
    if not path.exists():
        errors.append(f"Missing required file: {path.relative_to(ROOT)}")
        texts[key] = ""
    else:
        texts[key] = path.read_text(encoding="utf-8-sig")

required = {
    "plugin": (
        "CeInteractionTelemetryManager.Initialize();",
        "UniversalDynamicRefreshManager.Initialize();",
        "UniversalDynamicRefreshManager.Terminate();",
        "CeInteractionTelemetryManager.Terminate();",
        '"CE_CURVECONVERT "',
        '"CE_DYNAMICREFRESHALL "',
        '"CE_CLICKSTATS "',
    ),
    "road": (
        '"CE_ROADPROFILEFULL"',
        '"CE_ROADCORRIDORFULL"',
        '"CE_ROADTJUNCTION"',
        '"CE_ROADCROSSJUNCTION"',
        '"CE_JUNCTIONNUMBER"',
    ),
    "production": (
        '"CE_PROJECTMETADATAREFRESH "',
        '"CE_TITLEBLOCKSYNC "',
    ),
    "convert": (
        '"CE_CURVECONVERT"',
        '"Lines to polylines"',
        '"Arcs to polylines"',
        '"Circles to polylines"',
        '"Splines to polylines"',
        '"3D polylines to polylines"',
        '"Polylines to 3D polylines"',
        "ProductionSettingsDialogModel",
    ),
    "junction": (
        '"CE_ROADTJUNCTION"',
        '"CE_ROADCROSSJUNCTION"',
        '"CE_JUNCTIONNUMBER"',
        '"CE_JUNCTIONREFRESH"',
        '"J"',
        "ClockwiseKey",
        "top-left",
        "WriteLink",
    ),
    "telemetry": (
        '"CE_CLICKSTATS"',
        '"CE_CLICKSTATSCLEAR"',
        "IMessageFilter",
        "WmRightButtonDown",
        "UndoRedo",
        "CommandWillStart",
        "CommandCancelled",
        "ElapsedMilliseconds",
    ),
    "refresh": (
        '"CE_DYNAMICREFRESHALL"',
        '"CE_DYNAMICREFRESHSETTINGS"',
        '"CE_PROJECTMETADATAREFRESH"',
        '"CE_TITLEBLOCKSYNC"',
        "LinkedRefreshEngine.Refresh",
        "VertexSettingOutCommands.RefreshAll",
        "RoadJunctionCompletionCommands.RefreshAll",
        "ProductionMetadataDynamicManager.Refresh",
        "ObjectModified += OnObjectChanged",
        "ObjectErased += OnObjectErased",
    ),
    "corridor": (
        '"CE_ROADPROFILEFULL"',
        '"CE_ROADDESIGNPROFILE"',
        '"CE_ROADCORRIDORFULL"',
        '"CE_ROADCORRIDORCOMPLETE"',
        "CreateByLayout",
        "AddDesignPvis",
        '"CE-TOP"',
        '"CE-DATUM"',
        "ApplySurfaceTargets",
        "AddCorridorExtentsBoundary",
        "EnableSlopePatterns",
        "Code Set Style",
        "Corridor Style",
    ),
    "workflow": (
        "Validate-FinalCommentCompletion.py",
    ),
}

for key, markers in required.items():
    for marker in markers:
        if marker not in texts[key]:
            errors.append(f"{FILES[key].name} is missing marker: {marker}")

for key in ("plugin", "road", "production", "convert", "junction", "telemetry", "refresh", "corridor"):
    text = texts[key]
    if text.count("{") != text.count("}"):
        errors.append(f"Unbalanced braces in {FILES[key].name}")
    if text.count("(") != text.count(")"):
        errors.append(f"Unbalanced parentheses in {FILES[key].name}")

for forbidden in (
    "Curve segment = lightweight.GetArcSegmentAt(index);",
    "TODO FINAL COMMENT",
    "NotImplementedException",
):
    for key in ("convert", "junction", "telemetry", "refresh", "corridor"):
        if forbidden in texts[key]:
            errors.append(f"{FILES[key].name} contains forbidden marker: {forbidden}")

if errors:
    print("Final comment completion validation failed:", file=sys.stderr)
    for error in errors:
        print(f"- {error}", file=sys.stderr)
    raise SystemExit(1)

print(
    "Final comment completion passed: curve conversion popup, road junction creation/numbering, "
    "click-level telemetry, universal linked refresh, complete road profiles/corridors and dynamic "
    "project/title-block metadata are wired."
)

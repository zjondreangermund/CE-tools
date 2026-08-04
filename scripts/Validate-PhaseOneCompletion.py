#!/usr/bin/env python3
"""Protect the source-complete CE Tools Phase 1 utility milestone."""

from __future__ import annotations

from pathlib import Path
import re
import sys

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "src" / "CE.Tools.Civil3D"
errors: list[str] = []


def read(path: Path) -> str:
    if not path.is_file():
        errors.append(f"Missing Phase 1 file: {path.relative_to(ROOT)}")
        return ""
    return path.read_text(encoding="utf-8-sig")


phase_one = read(SOURCE / "PhaseOneUtilityCommands.cs")
ribbon = read(SOURCE / "PluginEntry.cs")
workflow = read(SOURCE / "FloatingToolsWindow.cs")
annotation = read(SOURCE / "AnnotationCommands.cs")
completion = read(ROOT / "docs" / "PHASE_1_COMPLETION.md")

required_commands = (
    "CE_PHASE1",
    "CE_VIEWPORTTOOLS",
    "CE_VIEWPORTREPORT",
    "CE_VIEWPORTLOCKALL",
    "CE_VIEWPORTUNLOCKALL",
    "CE_LAYERTOOLS",
    "CE_LAYERREPORT",
    "CE_LAYERPALETTE",
    "CE_EXCELTOOLS",
    "CE_LABELTOOLS",
    "CE_SURVEYCLEANUP",
)
for command in required_commands:
    if f'"{command}"' not in phase_one:
        errors.append(f"Phase 1 command is missing: {command}")
    if command in ("CE_PHASE1", "CE_VIEWPORTTOOLS", "CE_LAYERTOOLS", "CE_EXCELTOOLS", "CE_LABELTOOLS", "CE_SURVEYCLEANUP"):
        if f'"{command} "' not in ribbon:
            errors.append(f"Phase 1 ribbon launcher is missing: {command}")

for marker in (
    "ReadViewports",
    "viewport.Locked = locked",
    "LayerTableRecord",
    "GridReportPresenter.ShowReportAndOfferTable",
    "CE_BOQEXPORT",
    "CE_SETTINGOUTEXPORT",
    "CE_COORDPICK2",
    "CE_OVERLAPFIX",
    "CE_SURFCTOOLS",
):
    if marker not in phase_one:
        errors.append(f"Phase 1 implementation marker is missing: {marker}")

visual_launchers = {
    "AlignmentCommands.cs": ("CE Tools - Alignment Utilities", "Alignment tool [Report/StationOffset"),
    "FeatureLineCommands.cs": ("CE Tools - Feature Line Utilities", "Feature Line tool [Report/RaiseLower"),
    "FeatureLineConstructionCommands.cs": ("CE Tools - Feature Line Construction", "Feature Line edit [Create/Surface"),
    "FeatureLineRelativeCommands.cs": ("CE Tools - Linked Feature Lines", "Linked feature-line tool [Create/Update"),
    "HatchCommands.cs": ("CE Tools - Hatch Utilities", "CE Hatch tool [Create/Edit"),
    "SurfaceCommands.cs": ("CE Tools - Surface Utilities", "Surface tool [Report/Elevation"),
    "BackgroundXrefManagementCommands.cs": ("CE Tools - Background and XREF Utilities", "Background/XREF tools [Review/LightCopy"),
    "SettingOutScheduleCommands.cs": ("CE Tools - Setting-Out Schedules", "Setting-out tools [Create/Refresh"),
    "WaterSewerCostEstimateCommands.cs": ("CE Tools - Water and Sewer Cost Estimate", "Water/sewer cost estimate [Create/Refresh"),
    "ProjectSetupCommands.cs": ("CE Tools - Project Setup", "CE Project [Setup/Info"),
    "SurveyCorrectionComparisonCommands.cs": ("CE Tools - Survey Correction Comparison", "Survey correction comparison [Report/Export"),
    "XrefProjectManagementCommands.cs": ("CE Tools - Project XREF Management", "Project XREF tools [Split/Dashboard"),
    "EngineeringAssetLibraryCommands.cs": ("CE Tools - Engineering Asset Library", "Engineering asset library [Settings/Template"),
    "DrawingCleanupCommands.cs": ("CE Tools - Drawing Cleanup", "Drawing cleanup [All/Overkill"),
}
for filename, (title, legacy_prompt) in visual_launchers.items():
    text = read(SOURCE / filename)
    if title not in text:
        errors.append(f"{filename} is missing Phase 1 visual launcher: {title}")
    if legacy_prompt in text:
        errors.append(f"{filename} restored a legacy parent keyword menu: {legacy_prompt}")
    if text.count("{") != text.count("}"):
        errors.append(f"{filename} has unbalanced braces")

for legacy in (
    "Use these settings or edit them [Continue/Settings]",
    "Annotation text height [Small1.8/Standard2.0/Large5.0]",
    "Annotation output [MLeader/MText/COGO]",
):
    if legacy in annotation:
        errors.append(f"Annotation settings restored a command-line settings prompt: {legacy}")
for marker in (
    "CE Tools - Annotation Settings",
    "model.AddPaperHeight",
    'new[] { "MLeader", "MText", "COGO" }',
):
    if marker not in annotation:
        errors.append(f"Annotation settings window is missing: {marker}")

workflow_steps = (
    "CE_PHASE1", "CE_SURVEYCLEANUP", "CE_SWTOOLS", "CE_SWPROFILE",
    "CE_SEWPROFILE", "CE_WATERTOOLS", "CE_WATERPROFILE",
    "CE_HYDROLOGYTOOLS", "CE_FLOODRESULTTOOLS", "CE_FLOODANIMATIONHTML",
)
for command in workflow_steps:
    if f'"{command}")' not in workflow:
        errors.append(f"Floating workflow step is missing: {command}")

for marker in (
    "Source-complete milestone",
    "Feature Line Utilities",
    "Viewport Tools",
    "Layer Manager",
    "Excel Tools",
    "Coordinate Utilities",
    "Label Utilities",
    "Parking Utilities",
    "413 unique commands",
):
    if marker not in completion:
        errors.append(f"Phase 1 completion ledger is missing: {marker}")

command_pattern = re.compile(r"\[\s*CommandMethod\s*\((.*?)\)\s*\]", re.IGNORECASE | re.DOTALL)
string_pattern = re.compile(r'"((?:[^"\\]|\\.)*)"')
commands: set[str] = set()
for path in SOURCE.glob("*.cs"):
    text = read(path)
    for match in command_pattern.finditer(text):
        values = string_pattern.findall(match.group(1))
        if values:
            commands.add(values[-1].upper())
if len(commands) < 413:
    errors.append(f"Phase 1 command surface regressed below 413 ({len(commands)} found)")

if phase_one.count("{") != phase_one.count("}"):
    errors.append("PhaseOneUtilityCommands.cs has unbalanced braces")

if errors:
    print("CE Tools Phase 1 completion validation failed:", file=sys.stderr)
    for error in errors:
        print(f"- {error}", file=sys.stderr)
    raise SystemExit(1)

print(
    "CE Tools Phase 1 source-completion validation passed: all original utility "
    "families, visual launchers, workflow steps and 413 commands are present."
)

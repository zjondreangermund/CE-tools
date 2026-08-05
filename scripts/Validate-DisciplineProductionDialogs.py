#!/usr/bin/env python3
"""Protect dialog-based Stormwater, Sewer and Water production workflows."""

from __future__ import annotations

from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "src" / "CE.Tools.Civil3D"
errors: list[str] = []


def read(name: str) -> str:
    path = SOURCE / name
    if not path.is_file():
        errors.append(f"Missing discipline source: {path.relative_to(ROOT)}")
        return ""
    return path.read_text(encoding="utf-8-sig")


dialogs = read("DisciplineWorkflowDialogs.cs")
workflow_repair = read("WorkflowRepairCommands.cs")
disciplines = {
    "StormwaterProductionCommands.cs": (
        "CE Tools — Stormwater Workflow",
        "CE Tools — Stormwater Settings",
        "CE_SWPROFILE",
    ),
    "SewerProductionCommands.cs": (
        "CE Tools — Sewer Workflow",
        "CE Tools — Sewer Settings",
        "CE_SEWPROFILE",
    ),
    "WaterProductionCommands.cs": (
        "CE Tools — Water Workflow",
        "CE Tools — Water Settings",
        "CE_WATERPROFILE",
    ),
}

for marker in (
    "class DisciplineWorkflowWindow",
    "class ProductionSettingsWindow",
    "class ProductionStyleCatalog",
    "SelectWorkflow(",
    "EditSettings(",
    "MessageBoxButton.YesNo",
    "ProductionSettingsFieldKind.Choice",
    'new[] { "1.8", "2.0", "2.5", "3.5", "5.0" }',
):
    if marker not in dialogs:
        errors.append(f"Shared production-dialog source is missing: {marker}")

for filename, markers in disciplines.items():
    text = read(filename)
    for marker in markers:
        if marker not in text:
            errors.append(f"{filename} is missing dialog marker: {marker}")
    for marker in (
        "DisciplineWorkflowDialogs.SelectWorkflow",
        "DisciplineWorkflowDialogs.EditSettings",
        "ReadNames(",
        "ProfileColumns",
        "ProfileHorizontalSpacing",
        "ProfileVerticalSpacing",
        "SurfaceSelectionWindow",
    ):
        if marker not in text:
            errors.append(f"{filename} is missing persisted workflow marker: {marker}")

legacy_prompts = {
    "StormwaterProductionCommands.cs": (
        "Stormwater tools [Sequence/Alignments/Profiles/Settings/Info]",
        "Horizontal profile-view spacing <250>",
    ),
    "SewerProductionCommands.cs": (
        "Sewer tools [Sequence/SelectMain/Alignments/Refresh/Format/Profiles/Settings/Info]",
        "Profile views per row <2>",
    ),
    "WaterProductionCommands.cs": (
        "Water tools [Sequence/Alignments/Refresh/Profiles/PlaceAssets/RefreshAssets/Settings/Info]",
        "Maximum isolating-valve spacing <",
    ),
}
for filename, prompts in legacy_prompts.items():
    text = read(filename)
    for prompt in prompts:
        if prompt in text:
            errors.append(f"{filename} restored a legacy command-line workflow: {prompt}")

if "string noteText = null" not in workflow_repair:
    errors.append("SurfaceSelectionWindow must accept discipline-specific guidance")

for name, text in [("DisciplineWorkflowDialogs.cs", dialogs)] + [
    (name, read(name)) for name in disciplines
]:
    if text.count("{") != text.count("}"):
        errors.append(f"{name} has unbalanced braces")

if errors:
    print("CE Tools discipline-production dialog validation failed:", file=sys.stderr)
    for error in errors:
        print(f"- {error}", file=sys.stderr)
    raise SystemExit(1)

print(
    "CE Tools Stormwater, Sewer and Water dialog validation passed: "
    "workflow launchers, installed-style choices, paper heights, profile layout "
    "persistence, surface selectors and modal confirmations are present."
)

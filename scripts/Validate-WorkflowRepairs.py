#!/usr/bin/env python3
"""Source-shape validation for CE Tools Batch 4 workflow repairs.

The Autodesk assemblies are unavailable in GitHub Actions, so this check does
not replace Civil 3D compilation. It protects command wiring, mutation markers,
validation explanations and preservation of existing production commands.
"""

from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
WORKFLOW = ROOT / "src" / "CE.Tools.Civil3D" / "WorkflowRepairCommands.cs"
RIBBON = ROOT / "src" / "CE.Tools.Civil3D" / "PluginEntry.cs"
LEGACY_CORRIDOR = ROOT / "src" / "CE.Tools.Civil3D" / "CorridorCommands.cs"
LEGACY_FEATURE = ROOT / "src" / "CE.Tools.Civil3D" / "FeatureLineCommands.cs"

errors: list[str] = []

for path in (WORKFLOW, RIBBON, LEGACY_CORRIDOR, LEGACY_FEATURE):
    if not path.exists():
        errors.append(f"Missing required file: {path.relative_to(ROOT)}")

if errors:
    print("\n".join(errors), file=sys.stderr)
    raise SystemExit(1)

workflow = WORKFLOW.read_text(encoding="utf-8")
ribbon = RIBBON.read_text(encoding="utf-8")
legacy_corridor = LEGACY_CORRIDOR.read_text(encoding="utf-8")
legacy_feature = LEGACY_FEATURE.read_text(encoding="utf-8")

commands = [
    "CE_CORREBUILDX",
    "CE_FLRAISEX",
    "CE_FLSURFACEUI",
    "CE_FLCONSTGRADE",
    "CE_PKCOUNTX",
    "CE_PKNUMBER2",
]

for command in commands:
    if f'"{command}"' not in workflow:
        errors.append(f"Workflow command is not declared: {command}")
    if f'"{command} "' not in ribbon:
        errors.append(f"Ribbon is not linked to workflow command: {command}")

required_mutation_markers = [
    "corridor.Rebuild();",
    "featureLine.SetPointElevation",
    "featureLine.SetPointRelativeElevation",
    "featureLine.AssignElevationsFromSurface",
    "transaction.Commit();",
    "PopupTablePresenter.ShowReview",
    "SurfaceSelectionWindow",
    "RejectedReasons",
    "Polyline is open",
    "Closed polyline has zero area",
    "AnnotationSettingsStore.Prepare",
]
for marker in required_mutation_markers:
    if marker not in workflow:
        errors.append(f"Workflow repair source is missing expected marker: {marker}")

# The original command names remain available for command-line compatibility.
for command, source in (
    ("CE_CORREBUILD", legacy_corridor),
    ("CE_FLRAISE", legacy_feature),
):
    if f'"{command}"' not in source:
        errors.append(f"Legacy compatibility command was removed: {command}")

# Existing source already dispatches these legacy commands to mutation methods;
# keep that fact guarded while the explicit X commands are manually validated.
if "RebuildCorridors(document);" not in legacy_corridor:
    errors.append("Legacy CE_CORREBUILD no longer dispatches to RebuildCorridors")
if "RaiseLower(document);" not in legacy_feature:
    errors.append("Legacy CE_FLRAISE no longer dispatches to RaiseLower")

for preserved in (
    "CE_BMVERT ",
    "CE_TLENGTH ",
    "CE_TAREA ",
    "CE_COORDPOLY ",
    "CE_FLWEED ",
    "CE_FLREL ",
):
    if preserved not in ribbon:
        errors.append(f"Working ribbon command was removed: {preserved.strip()}")

for name, text in (
    ("WorkflowRepairCommands.cs", workflow),
    ("PluginEntry.cs", ribbon),
):
    if text.count("{") != text.count("}"):
        errors.append(f"Unbalanced braces detected in {name}")
    if text.count("(") != text.count(")"):
        errors.append(f"Unbalanced parentheses detected in {name}")

if errors:
    print("CE Tools Batch 4 workflow validation failed:", file=sys.stderr)
    for error in errors:
        print(f"- {error}", file=sys.stderr)
    raise SystemExit(1)

print("CE Tools Batch 4 workflow source validation passed.")

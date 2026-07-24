#!/usr/bin/env python3
"""Source-shape validation for CE Tools water network production."""

from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "src" / "CE.Tools.Civil3D" / "WaterProductionCommands.cs"
PLUGIN = ROOT / "src" / "CE.Tools.Civil3D" / "PluginEntry.cs"

errors: list[str] = []
for path in (SOURCE, PLUGIN):
    if not path.exists():
        errors.append(f"Missing required source: {path.relative_to(ROOT)}")

if not errors:
    source = SOURCE.read_text(encoding="utf-8")
    plugin = PLUGIN.read_text(encoding="utf-8")

    commands = (
        "CE_WATERTOOLS",
        "CE_WATERSETTINGS",
        "CE_WATERSEQ",
        "CE_WATERALIGN",
        "CE_WATERREFRESH",
        "CE_WATERPROFILE",
        "CE_WATERPLACE",
        "CE_WATERPLACEREFRESH",
        "CE_WATERINFO",
    )
    for command in commands:
        if f'"{command}"' not in source:
            errors.append(f"WaterProductionCommands.cs missing command: {command}")
        if f'"{command} "' not in plugin:
            errors.append(f"PluginEntry.cs missing ribbon command: {command}")

    required_source_markers = (
        "Pressure",
        "W-MAIN",
        "W-B",
        "CE_TOOLS_WATER",
        "WATER_PRODUCTION_SETTINGS",
        "CreateFromSurface",
        "ProfileView",
        "Isolating Valve",
        "Fire Hydrant",
        "Air Valve",
        "Scour Valve",
        "Maximum spacing review",
        "Hydrant coverage review",
        "Local high point review",
        "Local low point review",
        "review markers only",
        "CE_WATERREFRESH",
        "CE_WATERPLACEREFRESH",
        "BuildTag",
        "TryReadTag",
        "RemoveGeneratedForSource",
        "TryAddPressurePartsToProfileView",
    )
    for marker in required_source_markers:
        if marker not in source:
            errors.append(f"WaterProductionCommands.cs missing marker: {marker}")

    required_ribbon_markers = (
        "CE_TOOLS_WATER_PRODUCTION_MENU",
        "Water Network\\nProduction",
        "Place Valve and Hydrant Review Markers",
        "Refresh Asset Review Markers",
    )
    for marker in required_ribbon_markers:
        if marker not in plugin:
            errors.append(f"PluginEntry.cs missing water ribbon marker: {marker}")

    forbidden_claims = (
        "automatically approved",
        "final authority approval",
        "guaranteed pressure",
        "hydraulic design complete",
    )
    lower_source = source.lower()
    for claim in forbidden_claims:
        if claim in lower_source:
            errors.append(f"Water source contains unsafe release claim: {claim}")

    for path, text in ((SOURCE, source), (PLUGIN, plugin)):
        if text.count("{") != text.count("}"):
            errors.append(f"Unbalanced braces in {path.name}")
        if text.count("(") != text.count(")"):
            errors.append(f"Unbalanced parentheses in {path.name}")

if errors:
    print("Water production source validation failed:", file=sys.stderr)
    for error in errors:
        print(f"- {error}", file=sys.stderr)
    raise SystemExit(1)

print("Water production source validation passed.")

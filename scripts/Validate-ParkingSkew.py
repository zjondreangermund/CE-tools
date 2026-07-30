#!/usr/bin/env python3
"""Source-shape validation for parking perpendicular-width workflows."""

from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "src" / "CE.Tools.Civil3D" / "ParkingSkewValidationCommands.cs"
PLUGIN = ROOT / "src" / "CE.Tools.Civil3D" / "PluginEntry.cs"

errors: list[str] = []
for path in (SOURCE, PLUGIN):
    if not path.exists():
        errors.append(f"Missing required source: {path.relative_to(ROOT)}")

if not errors:
    source = SOURCE.read_text(encoding="utf-8")
    plugin = PLUGIN.read_text(encoding="utf-8")

    commands = (
        "CE_PKSKTOOLS",
        "CE_PKSKSETTINGS",
        "CE_PKSKVALIDATE",
        "CE_PKSKCORRECT",
        "CE_PKSKCLEAR",
        "CE_PKSKINFO",
    )
    for command in commands:
        if f'"{command}"' not in source:
            errors.append(f"ParkingSkewValidationCommands.cs missing command: {command}")
        if f'"{command} "' not in plugin:
            errors.append(f"PluginEntry.cs missing ribbon command: {command}")

    source_markers = (
        "RequiredWidthMillimetres = 2500.0",
        "DrawingUnitsPerMillimetre",
        "TryMinimumAreaRectangle",
        "ConvexHull",
        "perpendicular",
        "PassColour = 3",
        "FailColour = 1",
        "AlignedDimension",
        "COMPLIANT",
        "NON-COMPLIANT",
        "Correction outline",
        "Original geometry retained for review",
        "compliant source bays changed=0",
        "CE_TOOLS_PK_SKEW",
        "PARKING_SKEW_SETTINGS",
        "CE_PKCOUNTX",
        "CE_PKNUMBER2",
        "CE_PKREPORTUI",
        "GridReportPresenter.ShowReportAndOfferTable",
    )
    for marker in source_markers:
        if marker not in source:
            errors.append(f"ParkingSkewValidationCommands.cs missing marker: {marker}")

    ribbon_markers = (
        "CE_TOOLS_PARKING_SKEW_MENU",
        "Parking Skew\\nValidation",
        "Validate Perpendicular Bay Width",
        "Create Failed-Bay Correction Outlines",
        "Clear Skew Review Graphics",
    )
    for marker in ribbon_markers:
        if marker not in plugin:
            errors.append(f"PluginEntry.cs missing parking-skew ribbon marker: {marker}")

    for preserved in (
        '"CE_PKCOUNTX "',
        '"CE_PKNUMBER2 "',
        '"CE_PKREPORTUI "',
        '"CE_PKROW "',
        '"CE_PKDOUBLE "',
    ):
        if preserved not in plugin:
            errors.append(f"PluginEntry.cs lost existing parking command: {preserved}")

    unsafe_claims = (
        "automatically approved",
        "no engineering review required",
        "guaranteed standards compliant",
        "source bays changed=1",
    )
    lower_source = source.lower()
    for claim in unsafe_claims:
        if claim in lower_source:
            errors.append(f"Parking skew source contains unsafe claim: {claim}")

    for path, text in ((SOURCE, source), (PLUGIN, plugin)):
        if text.count("{") != text.count("}"):
            errors.append(f"Unbalanced braces in {path.name}")
        if text.count("(") != text.count(")"):
            errors.append(f"Unbalanced parentheses in {path.name}")

if errors:
    print("Parking skew source validation failed:", file=sys.stderr)
    for error in errors:
        print(f"- {error}", file=sys.stderr)
    raise SystemExit(1)

print("Parking skew source validation passed.")

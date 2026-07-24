#!/usr/bin/env python3
"""Source-shape checks for review-comments Batch 5.

Autodesk/Civil 3D assemblies are unavailable in GitHub Actions, so this
validator checks command declarations, link persistence, compact table rules,
polyline direction handling and preservation of existing utilities.
"""

from pathlib import Path
import re
import sys

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "src" / "CE.Tools.Civil3D" / "SurveyCoordinateWorkflowCommands.cs"
RIBBON = ROOT / "src" / "CE.Tools.Civil3D" / "PluginEntry.cs"
DIRECTION = ROOT / "src" / "CE.Tools.Civil3D" / "PolylineDirectionCommands.cs"
LEGACY_POLY = ROOT / "src" / "CE.Tools.Civil3D" / "CoordinatePolylineCommands.cs"

errors: list[str] = []
for path in (SOURCE, RIBBON, DIRECTION, LEGACY_POLY):
    if not path.exists():
        errors.append(f"Missing required file: {path.relative_to(ROOT)}")

if errors:
    print("\n".join(errors), file=sys.stderr)
    raise SystemExit(1)

source = SOURCE.read_text(encoding="utf-8")
ribbon = RIBBON.read_text(encoding="utf-8")
direction = DIRECTION.read_text(encoding="utf-8")
legacy_poly = LEGACY_POLY.read_text(encoding="utf-8")

commands = [
    "CE_COORDPICK2",
    "CE_COORDCROSS2",
    "CE_COORDTABLE2",
    "CE_COORDREFRESH",
    "CE_COORDPOLY2",
]
for command in commands:
    if f'"{command}"' not in source:
        errors.append(f"Survey coordinate command is not declared: {command}")

required_markers = [
    'LinkRecordName = "CE_COORDINATE_LINKS"',
    "table.ExtensionDictionary",
    "table.CreateExtensionDictionary()",
    "Handle=",
    "database.GetObjectId",
    "A coordinate table cannot be populated with zero rows.",
    '"Y / NORTHING"',
    '"X / EASTING"',
    '"Z / ELEVATION"',
    "ReadPolylineVertices",
    "CivilApplication.ActiveDocument",
    "CogoPoints.Add",
    "SetRawDescription",
    "CreateCrossLinework",
]
for marker in required_markers:
    if marker not in source:
        errors.append(f"Linked coordinate implementation is missing: {marker}")

if "Math.Max(height * 5.5, 12.0)" not in source:
    errors.append("Compact coordinate-table width rule is missing")
if re.search(r"SetColumnWidth\([^\n]*(?:2500|5000)", source):
    errors.append("Oversized coordinate-table width was introduced")

# Existing direction-arrow and legacy vertex-point utilities remain available.
for marker, text, description in (
    ('"CE_PLDIR"', direction, "polyline direction-arrow command"),
    ('"CE_COORDPOLY"', legacy_poly, "legacy polyline vertex command"),
    ('"CE_BMVERT ', ribbon, "Bellmouth ribbon command"),
    ('"CE_TLENGTH ', ribbon, "Total Length ribbon command"),
    ('"CE_TAREA ', ribbon, "Total Area ribbon command"),
):
    if marker not in text:
        errors.append(f"Existing working utility was removed: {description}")

for name, text in (
    (SOURCE.name, source),
    (RIBBON.name, ribbon),
):
    if text.count("{") != text.count("}"):
        errors.append(f"Unbalanced braces detected in {name}")

if errors:
    print("CE Tools survey-coordinate validation failed:", file=sys.stderr)
    for error in errors:
        print(f"- {error}", file=sys.stderr)
    raise SystemExit(1)

print("CE Tools linked survey-coordinate source validation passed.")

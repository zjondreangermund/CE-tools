#!/usr/bin/env python3
"""Source-shape checks for review-comments Batch 5.

Autodesk/Civil 3D assemblies are unavailable in GitHub Actions, so this
validator checks command declarations, ribbon links, link persistence, compact
table rules, polyline direction handling and preserved utilities.
"""

from pathlib import Path
import re
import sys

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "src" / "CE.Tools.Civil3D" / "SurveyCoordinateWorkflowCommands.cs"
RIBBON = ROOT / "src" / "CE.Tools.Civil3D" / "PluginEntry.cs"
DIRECTION = ROOT / "src" / "CE.Tools.Civil3D" / "PolylineDirectionCommands.cs"
LEGACY_POLY = ROOT / "src" / "CE.Tools.Civil3D" / "CoordinatePolylineCommands.cs"
DYNAMIC_LINKS = ROOT / "src" / "CE.Tools.Civil3D" / "DynamicCoordinateLinkStore.cs"

errors: list[str] = []
for path in (SOURCE, RIBBON, DIRECTION, LEGACY_POLY, DYNAMIC_LINKS):
    if not path.exists():
        errors.append(f"Missing required file: {path.relative_to(ROOT)}")

if errors:
    print("\n".join(errors), file=sys.stderr)
    raise SystemExit(1)

source = SOURCE.read_text(encoding="utf-8")
ribbon = RIBBON.read_text(encoding="utf-8")
direction = DIRECTION.read_text(encoding="utf-8")
legacy_poly = LEGACY_POLY.read_text(encoding="utf-8")
dynamic_links = DYNAMIC_LINKS.read_text(encoding="utf-8")

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
    if f'"{command} "' not in ribbon:
        errors.append(f"Survey ribbon is not linked to command: {command}")

required_markers = [
    'LinkRecordName = "CE_COORDINATE_LINKS"',
    "table.ExtensionDictionary",
    "table.CreateExtensionDictionary()",
    "Handle=",
    "database.GetObjectId",
    "A coordinate table cannot be populated with zero rows.",
    '"POINT NAME"',
    '"X"',
    '"Y"',
    '"Z"',
    "const int columns = 4;",
    "table.Cells[tableRow, column].Alignment = CellAlignment.MiddleCenter;",
    "ReadGeometryPointDefinitions",
    "Select one or more polylines and/or Civil 3D feature lines",
    '"ArcThreshold", "01 Point Rules", "Long arc/bellmouth length (m)", 10.0',
    '"TangentMidThreshold", "01 Point Rules", "One-point tangent length (m)", 20.0',
    '"TangentThreeThreshold", "01 Point Rules", "Three-point tangent length (m)", 40.0',
    "DrawingUnitsPerMetre(source.Database)",
    "new RadialDimension(",
    "DynamicCoordinateLinkStore.LinkGeometryPoint(",
    "CreateAutomaticGeometrySchedule(",
    "CivilApplication.ActiveDocument",
    "CogoPoints.Add",
    "SetRawDescription",
    "CreateCrossLinework",
]
for marker in required_markers:
    if marker not in source:
        errors.append(f"Linked coordinate implementation is missing: {marker}")

for forbidden in ('"Y / NORTHING"', '"X / EASTING"', '"Z / ELEVATION"'):
    if forbidden in source or forbidden in legacy_poly:
        errors.append(
            "Coordinate-table wording regressed; use only X, Y and Z: " + forbidden
        )

for marker in (
    'FollowerRecord = "CE_DYNAMIC_COORDINATE_FOLLOWER"',
    'VertexRecord = "CE_DYNAMIC_POLYLINE_VERTEX"',
    'SurfaceRecord = "CE_DYNAMIC_SURFACE_ELEVATION"',
    'GeometryRecord = "CE_DYNAMIC_GEOMETRY_POINT"',
    '"LastX="',
    "TryReadVertex(",
    "TryReadGeometryPoint(",
    "TrySetPoint(",
    "entity.TransformBy(Matrix3d.Displacement(sourcePoint - lastPoint))",
    "UpdateCoordinateContents(",
    "public static int CountLinks(Database database)",
):
    if marker not in dynamic_links:
        errors.append(f"Dynamic coordinate-link implementation is missing: {marker}")

if "DynamicCoordinateLinkStore.Refresh(document);" not in source:
    errors.append("Coordinate-table refresh does not first update dynamic point links")

if "Math.Max(height * 7.0, 18.0)" not in source:
    errors.append("Compact coordinate-table width rule is missing")
if re.search(r"SetColumnWidth\([^\n]*(?:2500|5000)", source):
    errors.append("Oversized coordinate-table width was introduced")

# Existing direction-arrow and legacy survey workflows remain available.
for marker, text, description in (
    ('"CE_PLDIR"', direction, "polyline direction-arrow command"),
    ('"CE_PLDIR ', ribbon, "polyline direction-arrow ribbon command"),
    ('"CE_COORDPOLY"', legacy_poly, "legacy polyline vertex command"),
    ('"CE_COORDPOLY ', ribbon, "legacy polyline vertex ribbon command"),
    ('"CE_COORDPICKX ', ribbon, "legacy picked-coordinate ribbon command"),
    ('"CE_COORDCROSSX ', ribbon, "legacy coordinate-cross ribbon command"),
    ('"CE_BMVERT ', ribbon, "Bellmouth ribbon command"),
    ('"CE_TLENGTH ', ribbon, "Total Length ribbon command"),
    ('"CE_TAREA ', ribbon, "Total Area ribbon command"),
):
    if marker not in text:
        errors.append(f"Existing working utility was removed: {description}")

for name, text in (
    (SOURCE.name, source),
    (RIBBON.name, ribbon),
    (DYNAMIC_LINKS.name, dynamic_links),
):
    if text.count("{") != text.count("}"):
        errors.append(f"Unbalanced braces detected in {name}")

if errors:
    print("CE Tools survey-coordinate validation failed:", file=sys.stderr)
    for error in errors:
        print(f"- {error}", file=sys.stderr)
    raise SystemExit(1)

print("CE Tools linked survey-coordinate source validation passed.")

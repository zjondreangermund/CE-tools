#!/usr/bin/env python3
"""Regression guard for Water/Stormwater production parity, supplementary menus and flood culvert safety."""

from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "src" / "CE.Tools.Civil3D"
errors = []

def read(name):
    p = SRC / name
    if not p.is_file():
        errors.append("Missing " + name)
        return ""
    return p.read_text(encoding="utf-8-sig")

storm = read("StormwaterProductionCommands.cs")
water = read("WaterProductionCommands.cs")
structured = read("August14StructuredDisciplineProductionCentres.cs")
supp = read("August24FieldCompletionCommands.cs")
menu = read("DynamicRefreshContextMenu.cs")
field = read("September11FieldCompletionMenu.cs")
flood = read("FloodProductionCulvertDesignCommands.cs")
centre = read("August11ProductionCentreCommands.cs")

for marker in [
    '"CE Tools — Stormwater Workflow"',
    '"CE_SWSETTINGSPRODUCTIONCENTRE"',
    '"CE_SWLAYOUTPRODUCTIONCENTRE"',
    '"CE_SWDESIGNPRODUCTIONCENTRE"',
    '"CE_STORMWATERFIELDSUPPLEMENTARY"',
]:
    if marker not in storm:
        errors.append("Stormwater Sewer-format production marker missing: " + marker)

for marker in [
    '"CE Tools — Water Workflow"',
    '"CE_WATERSETTINGSPRODUCTIONCENTRE"',
    '"CE_WATERLAYOUTPRODUCTIONCENTRE"',
    '"CE_WATERDESIGNPRODUCTIONCENTRE"',
    '"CE_WATERFIELDSUPPLEMENTARY"',
]:
    if marker not in water:
        errors.append("Water Sewer-format production marker missing: " + marker)

for marker in [
    '"CE_SWSTYLES"',
    'Stormwater gravity-network parts',
    '"CE_WATERSTYLES"',
    'pressure pipes, fittings, appurtenances',
    '"CE_FLOODCULVERTDESIGN"',
]:
    if marker not in structured:
        errors.append("Structured production parts/styles/flood marker missing: " + marker)

for marker in [
    '"CE_STORMWATERFIELDSUPPLEMENTARY"',
    "public void StormwaterSupplementary()",
    '"CE_SWLABELS"',
    '"CE_FLOODCULVERTDESIGN"',
    '"CE_WATERFIELDSUPPLEMENTARY"',
    "public void WaterSupplementary()",
    '"CE_WATERLABELS"',
    '"CE_WATERPROFILESAFE"',
]:
    if marker not in supp:
        errors.append("Supplementary command marker missing: " + marker)

for marker in [
    '"Stormwater Supplementary"',
    '"CE_STORMWATERFIELDSUPPLEMENTARY"',
    '"Water Supplementary"',
    '"CE_WATERFIELDSUPPLEMENTARY"',
]:
    if marker not in menu:
        errors.append("Right-click supplementary marker missing: " + marker)

for marker in [
    '"Recent - CE_STORMWATERFIELDSUPPLEMENTARY"',
    '"Recent - CE_WATERFIELDSUPPLEMENTARY"',
]:
    if marker not in field:
        errors.append("Field-completion supplementary marker missing: " + marker)

for marker in [
    'string stage = "crossing low-point sampling";',
    'stage = "TIN hydrology grid sampling";',
    'LowestFromSurfacePoints(',
    'surface.FindElevationAtXY(',
    'double length = boundary.Length;',
    'bestDistance <= maximumDistance',
    'double verticalMetres =',
    'AutoCAD status=',
]:
    if marker not in flood:
        errors.append("Flood internal-error safety marker missing: " + marker)

# Guard the specific old failure: 2D/flat source Z must not be used as the
# culvert low-point elevation for feature lines or polylines.
for obsolete in [
    'return LowestFromPoints(points, unitsPerMetre, "Feature Line"',
    'CrossingLowPoint low = LowestFromPoints(points, unitsPerMetre, "Polyline"',
    'double length = Math.Max(boundary.Length, spacing);',
]:
    if obsolete in flood:
        errors.append("Obsolete flood low-point/boundary behavior remains: " + obsolete)

if '"CE_FLOODCULVERTDESIGN"' not in centre:
    errors.append("Flood production centre does not expose CE_FLOODCULVERTDESIGN")

repair = (ROOT / "scripts" / "Repair-August24-FloodProductionCulvertMenu-Civil3D2023.ps1").read_text(encoding="utf-8-sig")
for marker in [
    "CE_FLOODCULVERTDESIGN",
    "CE-Flood Catchment & Culvert Design",
    "CE-Culvert Review",
]:
    if marker not in repair:
        errors.append("Flood pre-build culvert menu repair is stale: " + marker)

if errors:
    print("October 7 Water/Stormwater supplementary + Flood fix validation FAILED", file=sys.stderr)
    for error in errors:
        print(" - " + error, file=sys.stderr)
    raise SystemExit(1)

print("October 7 Water/Stormwater supplementary + Flood fix validation passed.")

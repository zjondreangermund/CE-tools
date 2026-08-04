#!/usr/bin/env python3
"""Protect visual launchers for high-use CE Tools workflow families."""

from __future__ import annotations

from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "src" / "CE.Tools.Civil3D"
errors: list[str] = []


def read(name: str) -> str:
    path = SOURCE / name
    if not path.is_file():
        errors.append(f"Missing workflow source: {path.relative_to(ROOT)}")
        return ""
    return path.read_text(encoding="utf-8-sig")


shared = read("DisciplineWorkflowDialogs.cs")
if "public static void SelectAndRun(" not in shared:
    errors.append("Shared workflow dialogs must expose SelectAndRun")

launchers = {
    "ParkingCommands.cs": ("CE Tools - Parking", "CE_PKROW", "Parking tool [Row/DoubleRow"),
    "ParkingDynamicGradingCommands.cs": ("CE Tools - Parking Grading", "CE_PARKGRADECREATE", "Parking grading guides [Create/Refresh"),
    "BillOfQuantitiesCommands.cs": ("CE Tools - Quantity Takeoff and BOQ", "CE_BOQBUILD", "BOQ tool [Build/Refresh"),
    "HydraulicReviewCommands.cs": ("CE Tools - Hydraulic Review", "CE_CULVERTREVIEW", "Hydraulic review tools [Catchment/Rational"),
    "FloodResultReviewCommands.cs": ("CE Tools - Flood Result Review", "CE_FLOODPROPERTYREPORT", "Imported flood-result tools [Properties/Frame"),
    "SurfaceHydrologyCommands.cs": ("CE Tools - Surface Hydrology", "CE_CATCHMENTDELINEATE", "Surface hydrology tools [Flow/Catchment"),
    "DynamicCrossSectionCommands.cs": ("CE Tools - Dynamic Cross Sections", "CE_XSCREATE", "Cross-section tool [Create/Refresh"),
    "PolylineDirectionCommands.cs": ("CE Tools - Polyline Direction", "CE_PLDIRREVERSE", "Polyline direction arrows [Add/Refresh"),
    "CoordinateCommands.cs": ("CE Tools - Coordinate Utilities", "DisciplineWorkflowDialogs.SelectWorkflow", "Coordinate tool [Pick/Cogo"),
    "CoordinateSystemCommands.cs": ("CE Tools - Coordinate Systems", "CE_COORDSYSASSIGN", "Coordinate Systems [Info/Assign"),
    "ProductionReportCommands.cs": ("CE Tools - Reports and Drawing Production", "CE_DRAWINGBOOK", "Report/production tool [Full/Discipline"),
    "ProfileCommands.cs": ("CE Tools - Profile Utilities", "CE_PRREPORT", "Profile tool [Report/Elevation"),
}

for filename, (title, action, legacy_prompt) in launchers.items():
    text = read(filename)
    if title not in text:
        errors.append(f"{filename} is missing visual launcher title: {title}")
    if action not in text:
        errors.append(f"{filename} is missing visual launcher action: {action}")
    if "DisciplineWorkflowDialogs.Select" not in text:
        errors.append(f"{filename} does not call the shared visual workflow dialog")
    if legacy_prompt in text:
        errors.append(f"{filename} restored a legacy parent keyword menu: {legacy_prompt}")
    if text.count("{") != text.count("}"):
        errors.append(f"{filename} has unbalanced braces")

parking_dynamic = read("ParkingDynamicGradingCommands.cs")
for marker in ("CE Tools - Dynamic Parking Monitor", "RefreshNow", "ParkingOptionAutoRefreshManager.Enabled"):
    if marker not in parking_dynamic:
        errors.append(f"Dynamic parking monitor is missing: {marker}")
if "Dynamic parking monitor [On/Off/RefreshNow/Status]" in parking_dynamic:
    errors.append("Dynamic parking monitor restored its command-line keyword menu")

if errors:
    print("CE Tools visual-workflow launcher validation failed:", file=sys.stderr)
    for error in errors:
        print(f"- {error}", file=sys.stderr)
    raise SystemExit(1)

print(
    "CE Tools visual-workflow launcher validation passed: parking, BOQ, hydraulics, "
    "flood, hydrology, cross-section, polyline, coordinate, profile and drawing-production "
    "parent menus use shared windows."
)

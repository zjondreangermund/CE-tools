#!/usr/bin/env python3
"""Regression gate for the user's cross-module CE Tools acceptance comments."""

from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
CIVIL = ROOT / "src" / "CE.Tools.Civil3D"

requirements = {
    "SurveyCoordinateWorkflowCommands.cs": [
        "Select one or more polylines and/or Civil 3D feature lines",
        "const int columns = 4;",
        '"POINT NAME"',
        '"X"',
        '"Y"',
        '"Z"',
        "CellAlignment.MiddleCenter",
        "SetRawDescription",
        "DynamicCoordinateLinkStore.LinkGeometryPoint(",
        '"ArcThreshold", "01 Point Rules", "Long arc/bellmouth length (m)", 10.0',
        '"TangentMidThreshold", "01 Point Rules", "One-point tangent length (m)", 20.0',
        '"TangentThreeThreshold", "01 Point Rules", "Three-point tangent length (m)", 40.0',
        "DrawingUnitsPerMetre(source.Database)",
        "new RadialDimension(",
        "CreateAutomaticGeometrySchedule(",
        "DynamicCoordinateLinkStore.LinkSurfaceElevation(",
        "DynamicCoordinateLinkStore.Refresh(document);",
    ],
    "DynamicCoordinateLinkStore.cs": [
        'FollowerRecord = "CE_DYNAMIC_COORDINATE_FOLLOWER"',
        'VertexRecord = "CE_DYNAMIC_POLYLINE_VERTEX"',
        'SurfaceRecord = "CE_DYNAMIC_SURFACE_ELEVATION"',
        'GeometryRecord = "CE_DYNAMIC_GEOMETRY_POINT"',
        '"LastX="',
        "TryReadVertex(",
        "TryReadGeometryPoint(",
        "TrySetPoint(",
        "UpdateCoordinateContents(",
        "public static int CountLinks(Database database)",
    ],
    "PolylineDirectionCommands.cs": [
        '"CE_PLDIRREFRESH"',
        '"CE_PLDIRREVERSE"',
        "RefreshLinkedArrows(Document document)",
    ],
    "AdvancedParkingPlanningCommands.cs": [
        '"CE_PARKOPTIONS"',
        '"CE_PARKOPTIONSREFRESH"',
        "bay.Closed = true",
    ],
    "ParkingCommands.cs": [
        "bay.Closed = true",
        '"CE_PKNUMBER"',
    ],
    "StormwaterProductionCommands.cs": [
        '"CE_SWALIGN"',
        '"CE_SWPROFILE"',
        '"SelectMain"',
        "ProfileViewBandSetStyles",
        "ProfileStyleLinker.Apply(",
    ],
    "SewerProductionCommands.cs": [
        '"CE_SEWSEQMAIN"',
        '"CE_SEWPROFILE"',
        "ProfileViewBandSetStyles",
    ],
    "SewerBranchAlignmentCommands.cs": [
        '"CE_SEWALIGN"',
        "SewerBranchLabelPlacement.BuildPlacements",
        "SewerBranchLabelPlacement.ConfigureLabel",
    ],
    "SewerBranchLabelPlacement.cs": [
        "DefaultPaperHeight = 3.5",
        "OffsetFactor",
        "MaximumLabelsPerBranch",
    ],
    "WaterProductionCommands.cs": [
        '"CE_WATERALIGN"',
        '"CE_WATERPROFILE"',
        "ProfileViewBandSetStyles",
        "TryAddPressurePartsToProfileView",
    ],
    "ProfileViewBatchCommands.cs": [
        "ProfileViewBatchWindow",
        "ProfileViewStyles",
        "ProfileViewBandSetStyles",
        "Apply profile-view band-set style",
        "Set automatic station/elevation range",
    ],
    "ProfileAnnotationLinkStore.cs": [
        "Moving the",
        "point in plan changes station",
        "profile.ElevationAt(station)",
        "profile.GradeAt(station)",
    ],
    "AnnotationScaleSyncCommands.cs": [
        "AnnotationScaleSyncManager",
        '"CE_ANNOSCALESYNC"',
    ],
    "PluginEntry.cs": [
        "ParkingOptionAutoRefreshManager.Initialize();",
        "AnnotationScaleSyncManager.Initialize();",
        "LinkedTableAutoRefreshManager.Initialize();",
        "FloatingToolsCommands.OpenAtFirstStartup();",
    ],
    "FloatingToolsWindow.cs": [
        "OpenAtFirstStartup()",
        "ModifierKeys.Control",
        '"all", "All", "All CE Tools Commands"',
        '"roads", "Roads", "Roads Workflow"',
        '"stormwater", "Stormwater", "Stormwater Workflow"',
        '"sewer", "Sewer", "Sewer Workflow"',
        '"water", "Water", "Water Workflow"',
        '"bulkwater", "Bulk Water", "Bulk Water Workflow"',
        '"flood", "Flood", "Flood Workflow"',
    ],
    "RefreshAllCommands.cs": [
        '"CE_REFRESHALL"',
        "SurveyCoordinateWorkflowCommands.RefreshAll(document)",
        "BillOfQuantitiesCommands.RefreshAll(document)",
        "WaterSewerCostEstimateCommands.RefreshAll(document)",
        "PolylineDirectionCommands.RefreshLinkedArrows(document)",
        "ParkingReportLinkStore.RefreshAll(document)",
    ],
    "ProductionReportCommands.cs": [
        '"CE-CLIENT-A4"',
        '"CE-CLIENT-A3"',
        '"CE-CONSTRUCTION-A1"',
        '"CE-CONSTRUCTION-A0"',
    ],
    "FloodResultReviewCommands.cs": [
        '"CE_FLOODPROPERTYREPORT"',
        '"CE_FLOODANIMATIONHTML"',
        "Point samples are not continuous flood surfaces",
    ],
}

errors: list[str] = []
for name, markers in requirements.items():
    path = CIVIL / name
    if not path.exists():
        errors.append(f"Missing acceptance source: {path.relative_to(ROOT)}")
        continue
    text = path.read_text(encoding="utf-8-sig")
    for marker in markers:
        if marker not in text:
            errors.append(f"{name} is missing acceptance marker: {marker}")
    if text.count("{") != text.count("}"):
        errors.append(f"Unbalanced braces detected in {name}")

survey_text = (CIVIL / "SurveyCoordinateWorkflowCommands.cs").read_text(
    encoding="utf-8-sig"
)
for forbidden in ('"X / EASTING"', '"Y / NORTHING"', '"Z / ELEVATION"'):
    if forbidden in survey_text:
        errors.append(f"Survey wording must use only X, Y and Z: {forbidden}")

installer = ROOT / "scripts" / "Install-VerifiedCivil3D2023Bundle.ps1"
if not installer.exists():
    errors.append("Verified Civil 3D 2023 bundle installer is missing")
else:
    installer_text = installer.read_text(encoding="utf-8-sig")
    for marker in (
        "Get-FileHash -LiteralPath $builtDll -Algorithm SHA256",
        "Get-FileHash -LiteralPath $installedDll -Algorithm SHA256",
        "Move-Item -LiteralPath $target -Destination $rollback",
        "previous CE Tools bundle was restored",
        "SourceCommit=",
        "Verification=PASS",
    ):
        if marker not in installer_text:
            errors.append(f"Verified installer is missing: {marker}")

for build_name in (
    "Build-Install-Civil3D2023.ps1",
    "Build-Install-Civil3D2023-DotNet.ps1",
):
    text = (ROOT / "scripts" / build_name).read_text(encoding="utf-8-sig")
    for marker in (
        "Install-VerifiedCivil3D2023Bundle.ps1",
        "-SourceCommit $SourceCommit",
        "-BuildLogPath $buildLog",
        "'/p:Platform=x64'",
        "AeccDbMgd.dll",
    ):
        if marker not in text:
            errors.append(f"{build_name} is missing: {marker}")

if errors:
    print("CE Tools user-comment coverage validation failed:", file=sys.stderr)
    for error in errors:
        print(f"- {error}", file=sys.stderr)
    raise SystemExit(1)

print("CE Tools user-comment coverage validation passed.")

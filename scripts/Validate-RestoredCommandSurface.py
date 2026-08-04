#!/usr/bin/env python3
"""Validate the reconciled V54/V60 command and support-source restoration."""

from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
CIVIL = ROOT / "src" / "CE.Tools.Civil3D"
CORE = ROOT / "src" / "CE.Tools.Core"
PLUGIN = CIVIL / "PluginEntry.cs"
REFRESH = CIVIL / "RefreshAllCommands.cs"
RECOVERY = ROOT / "scripts" / "Restore-V60-ChunkedSources.ps1"

required_civil = (
    "AdvancedParkingPlanningCommands.cs",
    "AnnotationScaleSyncCommands.cs",
    "BackgroundXrefManagementCommands.cs",
    "CommentPresentationCommands.cs",
    "DetailedSectionAnnotationCommands.cs",
    "DynamicIntersectionCommands.cs",
    "DynamicTypicalDetailEngine.cs",
    "EngineeringAssetLibraryCommands.cs",
    "FloodResultReviewCommands.cs",
    "FlowNetworkCulvertCommands.cs",
    "GradingDrainageDiagnosticCommands.cs",
    "HydraulicReviewCommands.cs",
    "ModelDesignAuditCommands.cs",
    "NetworkAssetScheduleCommands.cs",
    "ParkingDynamicGradingCommands.cs",
    "ParkingOptimiserCommands.cs",
    "ProfileViewBatchCommands.cs",
    "RoadCrossSectionScheduleCommands.cs",
    "SewerExcavationCommentCommands.cs",
    "SewerLabelLayoutCommands.cs",
    "SewerProductionCommands.cs",
    "StandardQuantityTemplateCommands.cs",
    "StormwaterProductionCommands.cs",
    "StormwaterSequenceCommands.cs",
    "SurfaceCorrectionCommands.cs",
    "SurfaceHydrologyCommands.cs",
    "SurfacePondingCommands.cs",
    "SurfaceSpikeHoleRepairCommands.cs",
    "SurveyCorrectionComparisonCommands.cs",
    "TypicalDetailsCommands.cs",
    "WaterProductionCommands.cs",
    "XrefProjectManagementCommands.cs",
)
required_core = (
    "EngineeringAssetCatalog.cs",
    "FloodResultAnalysis.cs",
    "HydrologyGrid.cs",
    "ParkingLayoutOptimizer.cs",
    "PumpSystemCurve.cs",
    "SimplePresentationPackage.cs",
)
errors: list[str] = []
for name in required_civil:
    if not (CIVIL / name).exists():
        errors.append(f"Restored Civil 3D source is missing: {name}")
for name in required_core:
    if not (CORE / name).exists():
        errors.append(f"Restored core source is missing: {name}")
if not (ROOT / "assets/engineering-library/engineering-assets.csv").exists():
    errors.append("The preserved engineering asset catalogue is missing")

for path in (PLUGIN, REFRESH, RECOVERY):
    if not path.exists():
        errors.append(f"Required integration file is missing: {path.relative_to(ROOT)}")
if errors:
    print("\n".join(errors), file=sys.stderr)
    raise SystemExit(1)

plugin = PLUGIN.read_text(encoding="utf-8")
refresh = REFRESH.read_text(encoding="utf-8")
recovery = RECOVERY.read_text(encoding="utf-8")

for marker in (
    "ParkingOptionAutoRefreshManager.Initialize();",
    "DynamicIntersectionUpdateManager.Initialize();",
    "AnnotationScaleSyncManager.Initialize();",
    "ParkingOptionAutoRefreshManager.Terminate();",
    "DynamicIntersectionUpdateManager.Terminate();",
    "AnnotationScaleSyncManager.Terminate();",
):
    if marker not in plugin:
        errors.append(f"Restored manager lifecycle is missing: {marker}")

for command in (
    "CE_COORDPICKCONTINUOUS",
    "CE_PLDIRREFRESH",
    "CE_PLDIRREVERSE",
    "CE_INTTOOLS",
    "CE_PARKOPTIMIZERTOOLS",
    "CE_SWTOOLS",
    "CE_SEWTOOLS",
    "CE_WATERTOOLS",
    "CE_NETWORKSCHEDULETOOLS",
    "CE_SURFCTOOLS",
    "CE_HYDRAULICTOOLS",
    "CE_FLOODRESULTTOOLS",
    "CE_XREFPROJECTTOOLS",
    "CE_ASSETLIBTOOLS",
    "CE_DETAILTOOLS",
    "CE_MODELREPORTTOOLS",
):
    if f'"{command} "' not in plugin:
        errors.append(f"Restored ribbon/workflow launcher is missing: {command}")

for marker in (
    "AlignmentAnnotationLinkStore.RefreshAll(document)",
    "ProfileAnnotationLinkStore.RefreshAll(document)",
    "CorridorAnnotationLinkStore.RefreshAll(document)",
    "NetworkAssetScheduleCommands.RefreshAll(document)",
    "RoadCrossSectionScheduleCommands.RefreshAll(document)",
    "StandardQuantityTemplateCommands.RefreshAll(document)",
    "SewerExcavationCommentCommands.RefreshAll(document)",
    "ParkingReportLinkStore.RefreshAll(document)",
):
    if marker not in refresh:
        errors.append(f"Shared refresh does not include restored link family: {marker}")

for marker in (
    "Retained active source; recovery fallback not required",
    "V54 recovery fallback not required",
    "continue",
):
    if marker not in recovery:
        errors.append(f"Recovery script can overwrite reconciled active source: {marker}")

for path in tuple(CIVIL.glob("*.cs")) + tuple(CORE.glob("*.cs")):
    text = path.read_text(encoding="utf-8")
    if text.count("{") != text.count("}"):
        errors.append(f"Unbalanced braces detected in {path.relative_to(ROOT)}")

if errors:
    print("CE Tools restored-command validation failed:", file=sys.stderr)
    for error in errors:
        print(f"- {error}", file=sys.stderr)
    raise SystemExit(1)

print("CE Tools restored 380+ command and support-source validation passed.")

#!/usr/bin/env python3
"""Validate Phase 4 exchange, pump-system and road-drive source."""

from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
EXCHANGE = ROOT / "src" / "CE.Tools.Civil3D" / "SpecialistModelExchangeCommands.cs"
PUMP_CORE = ROOT / "src" / "CE.Tools.Core" / "PumpSystemCurve.cs"
PUMP_CIVIL = ROOT / "src" / "CE.Tools.Civil3D" / "PumpSystemReviewCommands.cs"
ROAD_CORE = ROOT / "src" / "CE.Tools.Core" / "RoadDriveReview.cs"
ROAD_CIVIL = ROOT / "src" / "CE.Tools.Civil3D" / "RoadDriveReviewCommands.cs"
TESTS = ROOT / "tests" / "CE.Tools.Core.Tests" / "Program.cs"
RIBBON = ROOT / "src" / "CE.Tools.Civil3D" / "PluginEntry.cs"
NORMALIZER = ROOT / "scripts" / "Apply-Master-Items-Phase4.ps1"


def require(path: Path, *needles: str) -> None:
    if not path.exists():
        raise SystemExit(f"Missing required file: {path.relative_to(ROOT)}")
    text = path.read_text(encoding="utf-8")
    for needle in needles:
        if needle not in text:
            raise SystemExit(f"Missing {needle!r} in {path.relative_to(ROOT)}")


require(
    EXCHANGE,
    '"CE_MODELEXCHANGETOOLS"',
    '"CE_MODELEXPORTPACKAGE"',
    '"CE_MODELRESULTTEMPLATE"',
    '"CE_MODELRESULTIMPORT"',
    '"CE_MODELRESULTINFO"',
    '"CE_MODELRESULTCLEAR"',
    'private const string ResultRegApp = "CE_MODEL_RESULT_IMPORT"',
    'private const int MaximumImportRows = 250000',
    'private const int MaximumExportVertices = 1000000',
    'Existing package files will not be overwritten',
    'File.Exists(manifestPath) || File.Exists(geometryPath)',
    'SafeDelete(geometryPath)',
    'SafeDelete(manifestPath)',
    'SHA256.Create()',
    '"sourceDrawingSha256"',
    '"drawingUnitsPerMetre"',
    '"coordinateSystemCode"',
    '"geometrySha256"',
    '"curveSampleSpacingDrawingUnits"',
    'CivilApplication.ActiveDocument',
    '"FeatureId,PartIndex,VertexIndex,ObjectType,Layer,Handle,X,Y,Z,Role"',
    '"X,Y,Z,Depth,Velocity,WaterLevel,Scenario,Time',
    'The result CSV must contain X and Y columns.',
    'CSV coordinate units per metre',
    'Drawing units per metre',
    'coordinateScale = drawingUnitsPerMetre / coordinateUnitsPerMetre',
    'GetOrCreateResultLayer(',
    'ResultCategory(row.Depth, row.Velocity)',
    'HazardIndex(row.Depth, row.Velocity)',
    'entity.XData = new ResultBuffer(',
    'DxfCode.ExtendedDataRegAppName',
    'GetXDataForApplication(ResultRegApp)',
    'if (!HasResultRecord(entity)) continue;',
    'Source files and unrelated drawing objects were unchanged.',
    'Verify CRS, datum, units, scenario/time, interpolation and specialist-model assumptions',
    'The package is vendor-neutral.',
)

require(
    PUMP_CORE,
    'public static class PumpSystemCurve',
    '10.67 * definition.PipeLengthMetres',
    'Math.Pow(flowCubicMetresPerSecond, 1.852)',
    'Math.Pow(definition.InternalDiameterMetres, 4.8704)',
    'definition.MinorLossCoefficient * velocity * velocity',
    'public static PumpDutyPoint FindDutyPoint(',
    'firstDifference /',
    'firstDifference - secondDifference',
    'InterpolateOptional(first.EfficiencyPercent',
    'InterpolateOptional(first.PowerKilowatts',
    'InterpolateOptional(first.NpshRequiredMetres',
    'public static PumpSuitabilityReview Review(',
    'npshAvailableMetres.Value - duty.NpshRequiredMetres.Value',
    'No duty-point intersection occurs inside the supplied pump curve.',
    'At least two pump-curve points are required.',
    'Pump-curve flow values must be strictly increasing.',
    'Final pump selection requires manufacturer and engineer review.',
)

require(
    PUMP_CIVIL,
    '"CE_PUMPSYSTEMTOOLS"',
    '"CE_PUMPCURVETEMPLATE"',
    '"CE_PUMPSYSTEMREVIEW"',
    '"CE_PUMPFOLDERREVIEW"',
    'private const int MaximumCurveFiles = 100',
    'private const int MaximumCurveRows = 10000',
    '"FlowLps,HeadM,EfficiencyPercent,PowerKw,NpshRequiredM',
    'Pump curve must contain FlowLps and HeadM columns.',
    'new SystemCurveDefinition(',
    'PumpSystemCurve.Review(',
    'PumpSystemCurve.BuildSystemCurve(',
    'Target design flow for ranking',
    '.ThenBy(item => item.Review != null && item.Review.NpshPass ? 0 : 1)',
    '.ThenBy(item => item.TargetFlowDifferenceLitresPerSecond ?? double.MaxValue)',
    '.ThenByDescending(item => item.Review == null || item.Review.DutyPoint == null',
    'GridReportPresenter.ShowReportAndOfferTable(',
    'SimpleXlsxWriter.Write(',
    '"FLOW (L/s)"',
    '"PUMP HEAD (m)"',
    '"SYSTEM HEAD (m)"',
    '"NPSH MARGIN (m)"',
    'Ranking is preliminary; verify complete manufacturer operating envelopes.',
    'does not replace transient analysis, motor/electrical checks',
)

require(
    ROAD_CORE,
    'public static class RoadDriveReviewer',
    'public static RoadDriveAnalysis Analyse(',
    'speedMetresPerSecond * speedMetresPerSecond /',
    'StoppingSightDistanceMetres',
    'Circumradius(previous, current, next)',
    'RoadDriveIssueType.Grade',
    'RoadDriveIssueType.GradeChange',
    'RoadDriveIssueType.HorizontalRadius',
    'RoadDriveIssueType.LateralAcceleration',
    'BuildCameraFrames(ordered)',
    'At least three road-drive samples are required.',
    'Road-drive sample stations must be strictly increasing.',
    'does not replace formal road',
)

require(
    ROAD_CIVIL,
    '"CE_ROADDRIVETOOLS"',
    '"CE_ROADDRIVEREVIEW"',
    '"CE_ROADDRIVEEXPORT"',
    '"CE_ROADDRIVEINFO"',
    '"CE_ROADDRIVECLEAR"',
    'private const string RegAppName = "CE_ROAD_DRIVE_REVIEW"',
    'private const string ReviewLayer = "CE-ROAD-DRIVE-REVIEW"',
    'private const int MaximumSamples = 100000',
    'private const int MaximumIssueLabels = 500',
    'typeof(CivilAlignment)',
    'alignment.GetProfileIds()',
    'alignment.PointLocation(station, 0.0, ref easting, ref northing)',
    'profile.ElevationAt(station)',
    'RoadDriveReviewer.Analyse(',
    'new Polyline3d(Poly3dType.SimplePoly, points, false)',
    '"Station,X,Y,Z,HeadingDegrees,PitchDegrees,Alignment,Profile"',
    'GridReportPresenter.ShowReportAndOfferTable(',
    'SimpleXlsxWriter.Write(',
    'GetXDataForApplication(RegAppName)',
    'if (ReadTag(entity) == null) continue;',
    'Alignments, profiles, corridors and unrelated objects were unchanged.',
    'terrain/obstruction visibility not modelled',
    'does not',
    'replace formal geometric design, sight-distance, superelevation, collision',
)

require(
    TESTS,
    'SystemCurveIncreasesWithFlow();',
    'PumpDutyPointFindsIntersection();',
    'PumpReviewChecksNpshMargin();',
    'Near(8.0, duty.FlowLitresPerSecond);',
    'Near(12.0, duty.SystemHeadMetres);',
    'Near(74.0, duty.EfficiencyPercent.Value);',
    'Near(1.2, pass.NpshMarginMetres.Value);',
    'True(!fail.NpshPass);',
    'StraightRoadPassesScreening();',
    'SteepRoadFlagsGrade();',
    'TightCurveFlagsRadius();',
    'CameraPathHasHeadingAndPitch();',
    'Near(20.0, analysis.MaximumAbsoluteGradePercent);',
    'Near(Math.Sqrt(50.0), analysis.MinimumHorizontalRadiusMetres.Value);',
    'Near(45.0, analysis.CameraFrames[0].HeadingDegrees);',
)

require(
    NORMALIZER,
    'use backing fields for specialist-result summary ranges',
    'AddIntegrationPanel(tab);',
    'CE_TOOLS_CATEGORY_INTEGRATION',
    'Cmd("Specialist Model Exchange Tools", "CE_MODELEXCHANGETOOLS "',
    'Cmd("Export Specialist Model Package", "CE_MODELEXPORTPACKAGE "',
    'Cmd("Create Result CSV Template", "CE_MODELRESULTTEMPLATE "',
    'Cmd("Import Specialist Model Results", "CE_MODELRESULTIMPORT "',
    'Cmd("Imported Result Information", "CE_MODELRESULTINFO "',
    'Cmd("Clear Imported Model Results", "CE_MODELRESULTCLEAR "',
    'Cmd("Pump and System Curve Tools", "CE_PUMPSYSTEMTOOLS "',
    'Cmd("Create Pump Curve CSV Template", "CE_PUMPCURVETEMPLATE "',
    'Cmd("Review One Pump and System Curve", "CE_PUMPSYSTEMREVIEW "',
    'Cmd("Rank Pump Curves in a Folder", "CE_PUMPFOLDERREVIEW "',
    'Cmd("Road Drive Review Tools", "CE_ROADDRIVETOOLS "',
    'Cmd("Review Road Drive and Design", "CE_ROADDRIVEREVIEW "',
    'Cmd("Export Road Drive Camera Path", "CE_ROADDRIVEEXPORT "',
    'Cmd("Road Drive Review Information", "CE_ROADDRIVEINFO "',
    'Cmd("Clear Road Drive Review", "CE_ROADDRIVECLEAR "',
    'run road-drive core tests',
    'add deterministic road-drive geometry tests',
)

require(
    RIBBON,
    'CE_MODELEXCHANGETOOLS ',
    'CE_MODELEXPORTPACKAGE ',
    'CE_MODELRESULTTEMPLATE ',
    'CE_MODELRESULTIMPORT ',
    'CE_MODELRESULTINFO ',
    'CE_MODELRESULTCLEAR ',
    'CE_PUMPSYSTEMTOOLS ',
    'CE_PUMPCURVETEMPLATE ',
    'CE_PUMPSYSTEMREVIEW ',
    'CE_PUMPFOLDERREVIEW ',
    'CE_ROADDRIVETOOLS ',
    'CE_ROADDRIVEREVIEW ',
    'CE_ROADDRIVEEXPORT ',
    'CE_ROADDRIVEINFO ',
    'CE_ROADDRIVECLEAR ',
)

for path in (EXCHANGE, PUMP_CORE, PUMP_CIVIL, ROAD_CORE, ROAD_CIVIL):
    text = path.read_text(encoding="utf-8")
    if text.count("{") != text.count("}"):
        raise SystemExit(f"Unbalanced braces in {path.name}")

combined = "\n".join(path.read_text(encoding="utf-8") for path in (EXCHANGE, PUMP_CIVIL, ROAD_CIVIL))
if "Microsoft.Office.Interop" in combined:
    raise SystemExit("Phase 4 must not introduce Office COM automation")
if "ref MinimumDepth" in combined or "ref MaximumDepth" in combined:
    raise SystemExit("Result summary properties must not be passed by ref")
if "File.Copy(" in EXCHANGE.read_text(encoding="utf-8") or "File.Move(" in EXCHANGE.read_text(encoding="utf-8"):
    raise SystemExit("Specialist exchange must not silently copy or move source model files")
if "entity.Erase();" not in EXCHANGE.read_text(encoding="utf-8") or "HasResultRecord(entity)" not in EXCHANGE.read_text(encoding="utf-8"):
    raise SystemExit("Specialist result clear workflow must erase only tagged imported graphics")
if "entity.Erase();" not in ROAD_CIVIL.read_text(encoding="utf-8") or "ReadTag(entity) == null" not in ROAD_CIVIL.read_text(encoding="utf-8"):
    raise SystemExit("Road-drive clear workflow must erase only tagged review graphics")
if "alignment.UpgradeOpen" in ROAD_CIVIL.read_text(encoding="utf-8") or "profile.UpgradeOpen" in ROAD_CIVIL.read_text(encoding="utf-8"):
    raise SystemExit("Road-drive review must keep source alignment and profile read-only")

print("Master Items Phase 4 exchange, pump-system and road-drive validation passed.")

#!/usr/bin/env python3
"""Validate Phase 4 exchange, result-import and pump-system source."""

from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
EXCHANGE = ROOT / "src" / "CE.Tools.Civil3D" / "SpecialistModelExchangeCommands.cs"
PUMP_CORE = ROOT / "src" / "CE.Tools.Core" / "PumpSystemCurve.cs"
PUMP_CIVIL = ROOT / "src" / "CE.Tools.Civil3D" / "PumpSystemReviewCommands.cs"
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
    TESTS,
    'SystemCurveIncreasesWithFlow();',
    'PumpDutyPointFindsIntersection();',
    'PumpReviewChecksNpshMargin();',
    'Near(8.0, duty.FlowLitresPerSecond);',
    'Near(12.0, duty.SystemHeadMetres);',
    'Near(74.0, duty.EfficiencyPercent.Value);',
    'Near(1.2, pass.NpshMarginMetres.Value);',
    'True(!fail.NpshPass);',
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
)

for path in (EXCHANGE, PUMP_CORE, PUMP_CIVIL):
    text = path.read_text(encoding="utf-8")
    if text.count("{") != text.count("}"):
        raise SystemExit(f"Unbalanced braces in {path.name}")

combined = "\n".join(path.read_text(encoding="utf-8") for path in (EXCHANGE, PUMP_CIVIL))
if "Microsoft.Office.Interop" in combined:
    raise SystemExit("Phase 4 must not introduce Office COM automation")
if "ref MinimumDepth" in combined or "ref MaximumDepth" in combined:
    raise SystemExit("Result summary properties must not be passed by ref")
if "File.Copy(" in EXCHANGE.read_text(encoding="utf-8") or "File.Move(" in EXCHANGE.read_text(encoding="utf-8"):
    raise SystemExit("Specialist exchange must not silently copy or move source model files")
if "entity.Erase();" not in combined or "HasResultRecord(entity)" not in combined:
    raise SystemExit("Clear workflow must erase only tagged imported result graphics")

print("Master Items Phase 4 exchange and pump-system validation passed.")

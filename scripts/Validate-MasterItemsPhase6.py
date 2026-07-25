#!/usr/bin/env python3
"""Validate Phase 6 imported flood-result animation and property review source."""

from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CORE = ROOT / "src" / "CE.Tools.Core" / "FloodResultAnalysis.cs"
CIVIL = ROOT / "src" / "CE.Tools.Civil3D" / "FloodResultReviewCommands.cs"
TESTS = ROOT / "tests" / "CE.Tools.Flood.Tests" / "Program.cs"
TEST_PROJECT = ROOT / "tests" / "CE.Tools.Flood.Tests" / "CE.Tools.Flood.Tests.csproj"
RIBBON = ROOT / "src" / "CE.Tools.Civil3D" / "PluginEntry.cs"
NORMALIZER = ROOT / "scripts" / "Apply-Master-Items-Phase6.ps1"


def require(path: Path, *needles: str) -> None:
    if not path.exists():
        raise SystemExit(f"Missing required file: {path.relative_to(ROOT)}")
    text = path.read_text(encoding="utf-8")
    for needle in needles:
        if needle not in text:
            raise SystemExit(f"Missing {needle!r} in {path.relative_to(ROOT)}")


require(
    CORE,
    'public static class FloodResultAnalyzer',
    'public static FloodAnalysisResult Analyse(',
    'new FloodFrameKey(item.Scenario, item.Time)',
    '.GroupBy(item => new FloodFrameKey(item.Scenario, item.Time))',
    'property.Polygon.Contains(new ParkingPoint(point.X, point.Y), true)',
    'point.DepthMetres.Value >= minimumDepthMetres',
    'Maximum(wet.Select(item => item.DepthMetres))',
    'Maximum(wet.Select(item => item.VelocityMetresPerSecond))',
    'Maximum(wet.Select(item => item.WaterLevelMetres))',
    'Maximum(wet.Select(item => item.HazardIndex))',
    'Average(wet.Select(item => item.DepthMetres))',
    'FirstAffectedFrame',
    'PeakFrame',
    '.OrderBy(item => item.Key.Scenario',
    '.ThenBy(item => item.Key.SortTime)',
    'Point samples do not define a continuous',
    'legal flood lines or certified hazard',
)

require(
    CIVIL,
    '"CE_FLOODRESULTTOOLS"',
    '"CE_FLOODPROPERTYREPORT"',
    '"CE_FLOODFRAMESET"',
    '"CE_FLOODFRAMERESET"',
    '"CE_FLOODANIMATIONHTML"',
    'private const string ImportRegApp = "CE_MODEL_RESULT_IMPORT"',
    'private const int MaximumResultPoints = 250000',
    'private const int MaximumProperties = 5000',
    'private const int MaximumHtmlPoints = 200000',
    'GetXDataForApplication(ImportRegApp)',
    'new FloodResultPoint(',
    'new FloodProperty(',
    'FloodResultAnalyzer.Analyse(',
    'GridReportPresenter.ShowReportAndOfferTable(',
    'SimpleXlsxWriter.Write(',
    'entity.Visibility = show ? Visibility.Visible : Visibility.Invisible',
    'Only CE Tools imported result markers are hidden/shown.',
    'All imported specialist-result markers restored',
    '<canvas id=\'map\'></canvas>',
    "id='frame' type='range'",
    "id='play'>Play",
    'const frames=',
    'setInterval(',
    'The HTML is an interactive point-sample animation, not a solved 2D hydraulic model',
    'Do not use as a legal flood line, property damage assessment or certified hazard result.',
    'OpenMode.ForRead',
)

require(
    TEST_PROJECT,
    '<ProjectReference Include="..\\..\\src\\CE.Tools.Core\\CE.Tools.Core.csproj" />',
)
require(
    TESTS,
    'PropertyAssignmentWorks();',
    'DepthThresholdFiltersDrySamples();',
    'FramesSortByScenarioAndTime();',
    'PropertyPeakAndFirstFrameAreReported();',
    'Equal(1, result.PropertyFrames.Single(item => item.PropertyId == "A").WetPointCount',
    'Equal(1, summary.WetPointCount',
    'Equal("00:05:00", result.Frames[0].Key.Time',
    'Equal("00:20:00", summary.PeakFrame.Time',
)

require(
    NORMALIZER,
    'Cmd("Imported Flood Result Tools", "CE_FLOODRESULTTOOLS "',
    'Cmd("Affected Property Flood Review", "CE_FLOODPROPERTYREPORT "',
    'Cmd("Show One Flood Result Frame", "CE_FLOODFRAMESET "',
    'Cmd("Reset Flood Result Frames", "CE_FLOODFRAMERESET "',
    'Cmd("Export Flood Result Animation", "CE_FLOODANIMATIONHTML "',
)
require(
    RIBBON,
    'CE_FLOODRESULTTOOLS ',
    'CE_FLOODPROPERTYREPORT ',
    'CE_FLOODFRAMESET ',
    'CE_FLOODFRAMERESET ',
    'CE_FLOODANIMATIONHTML ',
)

for path in (CORE, CIVIL, TESTS):
    text = path.read_text(encoding="utf-8")
    if text.count("{") != text.count("}"):
        raise SystemExit(f"Unbalanced braces in {path.name}")

civil_text = CIVIL.read_text(encoding="utf-8")
if "Microsoft.Office.Interop" in civil_text:
    raise SystemExit("Flood result review must not introduce Office COM automation")
if "polyline.UpgradeOpen" in civil_text:
    raise SystemExit("Flood property boundaries must remain read-only")
if "entity.Erase();" in civil_text:
    raise SystemExit("Flood result frame/review tools must not erase imported markers")
if "File.WriteAllText(path, html" not in civil_text:
    raise SystemExit("Flood animation must write a self-contained HTML file")
if "cdn" in civil_text.lower() or "https://" in civil_text.lower():
    raise SystemExit("Flood HTML animation must not depend on external web assets")

print("Master Items Phase 6 flood-result animation and property review validation passed.")

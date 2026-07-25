#!/usr/bin/env python3
"""Validate the Phase 3 grid-hydrology core and Civil 3D bridges."""

from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CORE = ROOT / "src" / "CE.Tools.Core" / "HydrologyGrid.cs"
CIVIL = ROOT / "src" / "CE.Tools.Civil3D" / "SurfaceHydrologyCommands.cs"
PONDING = ROOT / "src" / "CE.Tools.Civil3D" / "SurfacePondingCommands.cs"
PERIODS = ROOT / "src" / "CE.Tools.Civil3D" / "ReturnPeriodHydrographCommands.cs"
TESTS = ROOT / "tests" / "CE.Tools.Core.Tests" / "Program.cs"
RIBBON = ROOT / "src" / "CE.Tools.Civil3D" / "PluginEntry.cs"
NORMALIZER = ROOT / "scripts" / "Apply-Master-Items-Phase3.ps1"


def require(path: Path, *needles: str) -> None:
    if not path.exists():
        raise SystemExit(f"Missing required file: {path.relative_to(ROOT)}")
    text = path.read_text(encoding="utf-8")
    for needle in needles:
        if needle not in text:
            raise SystemExit(f"Missing {needle!r} in {path.relative_to(ROOT)}")


require(
    CORE,
    "public sealed class HydrologyGrid",
    "FillDepressions()",
    "BuildFlowDirections(",
    "Accumulate(",
    "public IReadOnlyList<GridCell> TraceRoute",
    "public IReadOnlyList<GridCell> DelineateCatchment",
    "public int FindMaximumAccumulationCell()",
    "PriorityFloodResult",
    "HydrologyMinHeap",
    "flatTowardOutlet",
    "The flow-direction graph contains a cycle.",
    "public static class ModifiedRationalHydrograph",
    "runoffCoefficient *",
    "rainfallIntensityMillimetresPerHour * areaHectares / 360.0",
    "not calibrated hydrological design",
)
require(
    CIVIL,
    '"CE_HYDROLOGYTOOLS"',
    '"CE_SURFACEFLOW"',
    '"CE_CATCHMENTDELINEATE"',
    '"CE_HYDROGRAPHCOMPARE"',
    '"CE_HYDROLOGYCLEAR"',
    'private const string RegAppName = "CE_HYDROLOGY_REVIEW"',
    'private const int MaximumCells = 250000',
    'typeof(TinSurface)',
    'surface.FindElevationAtXY(x, y)',
    'new HydrologyGrid(',
    '.Analyse()',
    '.TraceRoute(',
    '.DelineateCatchment(',
    'ModifiedRationalHydrograph.Create(',
    'SimpleXlsxWriter.Write(',
    'OpenMode.ForRead',
    '"Grid catchment screening — verify against surveyed terrain and approved hydrology"',
    '"Regular-grid D8 screening — not a calibrated 1D/2D flood model"',
    'Only CE-generated route, catchment-perimeter, marker and label graphics will be erased.',
    'MaximumCells',
    'Drawing units per metre',
    'CE-HYDROLOGY-REVIEW',
)
require(
    PONDING,
    '"CE_PONDINGREVIEW"',
    'private const int MaximumGeneratedEdges = 20000',
    'SurfaceHydrologyCommands.PromptAnalysisInput(',
    'SurfaceHydrologyCommands.SampleAndAnalyse(',
    'sample.Analysis.FillDepth(index) / unitsPerMetre',
    'cellAreaSquareMetres = sample.CellArea /',
    'volume += depthMetres * cellAreaSquareMetres',
    'cells.Count * cellAreaSquareMetres / 10000.0',
    'BuildZones(',
    'Queue<int>()',
    'CountExposedEdges(',
    'MaximumGeneratedEdges',
    'SimpleXlsxWriter.Write(',
    '"AREA (ha)"',
    '"MAX DEPTH (m)"',
    '"STORAGE (m3)"',
    '"Depression-to-spill storage screen — not flood depth, duration or hazard"',
    'This is not a dynamic flood model.',
    'Source surface/boundary changed", "No"',
    '"PondingPerimeter"',
    '"PondingDeepestPoint"',
    '"PondingLabel"',
)
require(
    PERIODS,
    '"CE_HYDROGRAPHPERIODS"',
    '2, 5, 10, 20, 25, 50, 100',
    '"1:" + ReturnPeriods[index] + " rainfall intensity (mm/h)"',
    'ModifiedRationalHydrograph.Create(',
    'IntegrateVolume(',
    'volume += (first.FlowCubicMetresPerSecond +',
    '"RETURN PERIOD"',
    '"PRE PEAK (m3/s)"',
    '"POST PEAK (m3/s)"',
    '"PRE VOLUME (m3)"',
    '"POST VOLUME (m3)"',
    '"P2/P5/P10/P20/P25/P50/P100/None"',
    'CombinedTimes(',
    'Interpolate(',
    'SimpleXlsxWriter.Write(',
    'Intensities are user-entered project inputs; results are not calibrated hydrographs.',
)
require(
    TESTS,
    "PriorityFloodFillsEnclosedPit();",
    "FlowRouteTerminatesWithoutCycle();",
    "AccumulationReachesSingleOutlet();",
    "CatchmentContainsUpstreamCells();",
    "ModifiedRationalHydrographMatchesPeak();",
    "Near(9.0, analysis.FillDepth(centre));",
    "Near(9.0, analysis.AccumulationArea[outlet]);",
)
require(
    NORMALIZER,
    'Cmd("Surface Hydrology Tools", "CE_HYDROLOGYTOOLS "',
    'Cmd("Trace Surface Flow Route", "CE_SURFACEFLOW "',
    'Cmd("Delineate Outlet Catchment", "CE_CATCHMENTDELINEATE "',
    'Cmd("Compare Pre/Post Hydrographs", "CE_HYDROGRAPHCOMPARE "',
    'Cmd("Return-Period Pre/Post Hydrographs", "CE_HYDROGRAPHPERIODS "',
    'Cmd("Depression Storage and Affected Area", "CE_PONDINGREVIEW "',
    'Cmd("Clear Surface Hydrology Review", "CE_HYDROLOGYCLEAR "',
    "use a net48-compatible catchment-edge reference type",
    "share bounded Civil 3D hydrology input with ponding review",
    "share tested surface sampling and grid analysis",
    "share sampled-cell coordinates with affected-area mapping",
)
require(
    RIBBON,
    "CE_HYDROLOGYTOOLS ",
    "CE_SURFACEFLOW ",
    "CE_CATCHMENTDELINEATE ",
    "CE_HYDROGRAPHCOMPARE ",
    "CE_HYDROGRAPHPERIODS ",
    "CE_PONDINGREVIEW ",
    "CE_HYDROLOGYCLEAR ",
)

for path in (CORE, CIVIL, PONDING, PERIODS):
    text = path.read_text(encoding="utf-8")
    if text.count("{") != text.count("}"):
        raise SystemExit(f"Unbalanced braces in {path.name}")

combined = "\n".join(path.read_text(encoding="utf-8") for path in (CIVIL, PONDING, PERIODS))
if "Microsoft.Office.Interop" in combined:
    raise SystemExit("Surface hydrology must not introduce Office COM automation")
if "surface.UpgradeOpen" in combined or "boundary.UpgradeOpen" in combined:
    raise SystemExit("Surface hydrology must not open source surface or boundary for write")

print("Master Items Phase 3 grid hydrology, ponding and return-period validation passed.")

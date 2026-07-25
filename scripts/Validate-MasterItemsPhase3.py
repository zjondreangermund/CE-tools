#!/usr/bin/env python3
"""Validate the Phase 3 grid-hydrology core and Civil 3D bridge."""

from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CORE = ROOT / "src" / "CE.Tools.Core" / "HydrologyGrid.cs"
CIVIL = ROOT / "src" / "CE.Tools.Civil3D" / "SurfaceHydrologyCommands.cs"
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
    'Cmd("Clear Surface Hydrology Review", "CE_HYDROLOGYCLEAR "',
    "use a net48-compatible catchment-edge reference type",
)
require(
    RIBBON,
    "CE_HYDROLOGYTOOLS ",
    "CE_SURFACEFLOW ",
    "CE_CATCHMENTDELINEATE ",
    "CE_HYDROGRAPHCOMPARE ",
    "CE_HYDROLOGYCLEAR ",
)

for path in (CORE, CIVIL):
    text = path.read_text(encoding="utf-8")
    if text.count("{") != text.count("}"):
        raise SystemExit(f"Unbalanced braces in {path.name}")

civil_text = CIVIL.read_text(encoding="utf-8")
if "Microsoft.Office.Interop" in civil_text:
    raise SystemExit("Surface hydrology must not introduce Office COM automation")
if "surface.UpgradeOpen" in civil_text or "boundary.UpgradeOpen" in civil_text:
    raise SystemExit("Surface hydrology must not open source surface or boundary for write")

print("Master Items Phase 3 grid hydrology validation passed.")

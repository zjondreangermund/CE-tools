#!/usr/bin/env python3
"""Validate Master Items Phase 1 native Civil 3D source."""

from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PARKING = ROOT / "src" / "CE.Tools.Civil3D" / "AdvancedParkingPlanningCommands.cs"
GRADING = ROOT / "src" / "CE.Tools.Civil3D" / "GradingDrainageDiagnosticCommands.cs"
RIBBON = ROOT / "src" / "CE.Tools.Civil3D" / "PluginEntry.cs"
NORMALIZER = ROOT / "scripts" / "Apply-Master-Items-Phase1.ps1"
WRAPPER = ROOT / "scripts" / "Invoke-Comments-Normalizer.ps1"
REGISTER = ROOT / "docs" / "MASTER_ITEMS_REGISTER.md"


def require(path: Path, *needles: str) -> None:
    if not path.exists():
        raise SystemExit(f"Missing required file: {path.relative_to(ROOT)}")
    text = path.read_text(encoding="utf-8")
    for needle in needles:
        if needle not in text:
            raise SystemExit(f"Missing {needle!r} in {path.relative_to(ROOT)}")


require(
    PARKING,
    '"CE_PARKOPTIONS"',
    '"CE_PARKOPTIONSREFRESH"',
    '"CE_PARKOPTIONSINFO"',
    '"CE_PARKOPTIONSCLEAR"',
    'private const string RegAppName = "CE_PARK_OPTIONS"',
    'BuildOption(boundary, settings, 90.0)',
    'BuildOption(boundary, settings, 60.0)',
    'BuildOption(boundary, settings, 45.0)',
    'bay.Closed = true;',
    '"Boundary=" + boundaryHandle',
    'PointInPolygon(',
    'PopupTablePresenter.ShowReview(',
    'PopupTablePresenter.ShowReportAndOfferTable(',
    'internal sealed class ParkingOptionsWindow',
)
require(
    GRADING,
    '"CE_GRADINGDIAGNOSTICS"',
    '"CE_LOWSLOPE"',
    '"CE_LOWPOINTS"',
    '"CE_GRADINGREVIEWCLEAR"',
    'private const string RegAppName = "CE_GRADING_REVIEW"',
    '"Minimum acceptable absolute grade (%)"',
    'Math.Abs(item.GradePercent) < threshold',
    'FeatureLinePointType.AllPoints',
    'Color.FromColorIndex(ColorMethod.ByAci, 1)',
    '"Global minimum"',
    '"Local minimum"',
    'Source geometry changed',
)
require(
    NORMALIZER,
    'Cmd("Boundary Parking Alternatives", "CE_PARKOPTIONS "',
    'Cmd("Refresh Boundary Parking Option", "CE_PARKOPTIONSREFRESH "',
    'Cmd("Boundary Parking Information", "CE_PARKOPTIONSINFO "',
    'Cmd("Clear Boundary Parking Option", "CE_PARKOPTIONSCLEAR "',
    'Cmd("Grading Diagnostic Tools", "CE_GRADINGDIAGNOSTICS "',
    'Cmd("Highlight Grades Below Limit", "CE_LOWSLOPE "',
    'Cmd("Identify Candidate Low Points", "CE_LOWPOINTS "',
    'Cmd("Clear Grading Review Graphics", "CE_GRADINGREVIEWCLEAR "',
    'avoid version-specific FeatureLine closed property',
)
require(
    WRAPPER,
    'Apply-Master-Items-Phase1.ps1',
    'Applying Master Items Phase 1 corrections',
)
require(
    REGISTER,
    '## Phase 1 — native Civil 3D productivity',
    'Present 90°, 60° and 45° parking alternatives.',
    'Highlight selected segments/areas below the configured minimum grade, default 0.5%.',
    'External dependency',
)

# The normalizer executes before this validator in CI.
require(
    RIBBON,
    'CE_PARKOPTIONS ',
    'CE_PARKOPTIONSREFRESH ',
    'CE_PARKOPTIONSINFO ',
    'CE_PARKOPTIONSCLEAR ',
    'CE_GRADINGDIAGNOSTICS ',
    'CE_LOWSLOPE ',
    'CE_LOWPOINTS ',
    'CE_GRADINGREVIEWCLEAR ',
)

for path in (PARKING, GRADING):
    text = path.read_text(encoding="utf-8")
    if text.count("{") != text.count("}"):
        raise SystemExit(f"Unbalanced braces in {path.name}")

print("Master Items Phase 1 parking and grading validation passed.")

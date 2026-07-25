#!/usr/bin/env python3
"""Validate Master Items Phase 1 native Civil 3D source."""

from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PARKING = ROOT / "src" / "CE.Tools.Civil3D" / "AdvancedParkingPlanningCommands.cs"
GRADING = ROOT / "src" / "CE.Tools.Civil3D" / "GradingDrainageDiagnosticCommands.cs"
BACKGROUND = ROOT / "src" / "CE.Tools.Civil3D" / "BackgroundXrefManagementCommands.cs"
SETTING = ROOT / "src" / "CE.Tools.Civil3D" / "SettingOutScheduleCommands.cs"
PRESENTATION = ROOT / "src" / "CE.Tools.Civil3D" / "CommentPresentationCommands.cs"
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
    BACKGROUND,
    '"CE_BACKGROUNDTOOLS"',
    '"CE_BACKGROUNDREVIEW"',
    '"CE_BACKGROUNDLIGHT"',
    '"CE_XREFSPLIT"',
    '"CE_XREFINFO"',
    '"CE_XREFBACKUP"',
    'database.Wblock(ids, basePoint)',
    'database.AttachXref(path, xrefName)',
    'BackgroundPrefix = "CE-BG-"',
    'BackgroundColour = 253',
    'editor.SetImpliedSelection(resultIds)',
    'Path.Combine(',
    '"Revisions"',
    'File.Copy(resolvedPath, backupPath, false)',
)
require(
    SETTING,
    '"CE_SETTINGOUTTOOLS"',
    '"CE_SETTINGOUTPOINTS"',
    '"CE_SETTINGOUTREFRESH"',
    '"CE_SETTINGOUTEXPORT"',
    '"CE_SETTINGOUTINFO"',
    'private const string LinkRecordName = "CE_SETTING_OUT_LINKS"',
    '"POINT DESCRIPTION"',
    '"X COORDINATE"',
    '"Y COORDINATE"',
    '"GROUND ELEVATION"',
    '"DESIGN ELEVATION"',
    '"DIFFERENCE"',
    'surface.FindElevationAtXY(point.X, point.Y)',
    'SimpleXlsxWriter.Write(path, "Setting Out", cells)',
    'internal static int RefreshAll(Document document)',
    'internal sealed class SettingOutConfigurationWindow',
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
    'Cmd("Background and XREF Tools", "CE_BACKGROUNDTOOLS "',
    'Cmd("Audit Background Drawing", "CE_BACKGROUNDREVIEW "',
    'Cmd("Create Controlled Light Background", "CE_BACKGROUNDLIGHT "',
    'Cmd("Split Selection to XREF", "CE_XREFSPLIT "',
    'Cmd("XREF Information", "CE_XREFINFO "',
    'Cmd("Create XREF Revision Backup", "CE_XREFBACKUP "',
    'Cmd("Setting-Out Schedule Tools", "CE_SETTINGOUTTOOLS "',
    'Cmd("Create Linked Setting-Out Schedule", "CE_SETTINGOUTPOINTS "',
    'Cmd("Refresh Setting-Out Schedule", "CE_SETTINGOUTREFRESH "',
    'Cmd("Export Setting-Out Schedule", "CE_SETTINGOUTEXPORT "',
    'Cmd("Setting-Out Schedule Information", "CE_SETTINGOUTINFO "',
    'include linked setting-out schedules in CE_REFRESHALL',
    'avoid version-specific FeatureLine closed property',
)
require(
    PRESENTATION,
    'summary.CoordinateTables += SettingOutScheduleCommands.RefreshAll(document);',
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
    'Export selected discipline groups to separate DWGs and attach them as XREFs.',
    'Platform point schedule: description, X, Y, ground, design and difference.',
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
    'CE_BACKGROUNDTOOLS ',
    'CE_BACKGROUNDREVIEW ',
    'CE_BACKGROUNDLIGHT ',
    'CE_XREFSPLIT ',
    'CE_XREFINFO ',
    'CE_XREFBACKUP ',
    'CE_SETTINGOUTTOOLS ',
    'CE_SETTINGOUTPOINTS ',
    'CE_SETTINGOUTREFRESH ',
    'CE_SETTINGOUTEXPORT ',
    'CE_SETTINGOUTINFO ',
)

for path in (PARKING, GRADING, BACKGROUND, SETTING):
    text = path.read_text(encoding="utf-8")
    if text.count("{") != text.count("}"):
        raise SystemExit(f"Unbalanced braces in {path.name}")

print("Master Items Phase 1 parking, grading, background/XREF and setting-out validation passed.")

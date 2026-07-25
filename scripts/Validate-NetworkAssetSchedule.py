#!/usr/bin/env python3
"""Validate linked network asset schedules and BOQ handoff."""

from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "src" / "CE.Tools.Civil3D" / "NetworkAssetScheduleCommands.cs"
RIBBON = ROOT / "src" / "CE.Tools.Civil3D" / "PluginEntry.cs"
PRESENTATION = ROOT / "src" / "CE.Tools.Civil3D" / "CommentPresentationCommands.cs"
NORMALIZER = ROOT / "scripts" / "Apply-Master-Items-Phase1-NetworkSchedule.ps1"
WRAPPER = ROOT / "scripts" / "Invoke-Master-Items-Phase1.ps1"


def require(path: Path, *needles: str) -> None:
    if not path.exists():
        raise SystemExit(f"Missing required file: {path.relative_to(ROOT)}")
    text = path.read_text(encoding="utf-8")
    for needle in needles:
        if needle not in text:
            raise SystemExit(f"Missing {needle!r} in {path.relative_to(ROOT)}")


require(
    SOURCE,
    '"CE_NETWORKSCHEDULETOOLS"',
    '"CE_NETWORKSCHEDULE"',
    '"CE_NETWORKSCHEDULEREFRESH"',
    '"CE_NETWORKSCHEDULEEXPORT"',
    '"CE_NETWORKSCHEDULEINFO"',
    '"CE_NETWORKSCHEDULEBOQ"',
    'private const string LinkRecordName = "CE_NETWORK_ASSET_SCHEDULE"',
    '"Stormwater"',
    '"Sewer"',
    '"Water"',
    'upper.Contains("PIPE")',
    'upper.Contains("STRUCTURE")',
    'upper.Contains("FITTING")',
    'upper.Contains("APPURTENANCE")',
    '"BEND ANGLE (deg)"',
    '"SOURCE HANDLE"',
    'SimpleXlsxWriter.Write(path, "Network Assets", cells)',
    'document.Editor.SetImpliedSelection(ids.ToArray())',
    'document.SendStringToExecute("CE_BOQBUILD "',
    'internal static int RefreshAll(Document document)',
)
require(
    NORMALIZER,
    'Cmd("Network Asset Schedule Tools", "CE_NETWORKSCHEDULETOOLS "',
    'Cmd("Create Linked Network Asset Schedule", "CE_NETWORKSCHEDULE "',
    'Cmd("Refresh Network Asset Schedule", "CE_NETWORKSCHEDULEREFRESH "',
    'Cmd("Export Network Asset Schedule", "CE_NETWORKSCHEDULEEXPORT "',
    'Cmd("Network Asset Schedule Information", "CE_NETWORKSCHEDULEINFO "',
    'Cmd("Build BOQ from Network Schedule", "CE_NETWORKSCHEDULEBOQ "',
    'summary.BoqTables += NetworkAssetScheduleCommands.RefreshAll(document);',
)
require(
    WRAPPER,
    'Apply-Master-Items-Phase1-NetworkSchedule.ps1',
)
# Normalizers execute before this validator in CI.
require(
    RIBBON,
    'CE_NETWORKSCHEDULETOOLS ',
    'CE_NETWORKSCHEDULE ',
    'CE_NETWORKSCHEDULEREFRESH ',
    'CE_NETWORKSCHEDULEEXPORT ',
    'CE_NETWORKSCHEDULEINFO ',
    'CE_NETWORKSCHEDULEBOQ ',
)
require(
    PRESENTATION,
    'summary.BoqTables += NetworkAssetScheduleCommands.RefreshAll(document);',
)

text = SOURCE.read_text(encoding="utf-8")
if text.count("{") != text.count("}"):
    raise SystemExit("Unbalanced braces in NetworkAssetScheduleCommands.cs")

print("Network asset schedule and BOQ handoff validation passed.")

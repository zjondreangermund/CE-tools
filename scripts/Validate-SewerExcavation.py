#!/usr/bin/env python3
"""Validate linked sewer excavation quantity comments."""

from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "src" / "CE.Tools.Civil3D" / "SewerExcavationCommentCommands.cs"
PRODUCTION = ROOT / "src" / "CE.Tools.Civil3D" / "ProductionCommentCommands.cs"
PRESENTATION = ROOT / "src" / "CE.Tools.Civil3D" / "CommentPresentationCommands.cs"
RIBBON = ROOT / "src" / "CE.Tools.Civil3D" / "PluginEntry.cs"
NORMALIZER = ROOT / "scripts" / "Apply-Comments-Quantities.ps1"
BUILD = ROOT / "scripts" / "Build-CE-Tools.ps1"


def require(path: Path, *needles: str) -> None:
    if not path.exists():
        raise SystemExit(f"Missing required file: {path.relative_to(ROOT)}")
    text = path.read_text(encoding="utf-8")
    for needle in needles:
        if needle not in text:
            raise SystemExit(f"Missing {needle!r} in {path.relative_to(ROOT)}")


require(
    SOURCE,
    '"CE_SEWEREXCAVATION"',
    '"CE_SEWEREXCAVATIONREFRESH"',
    '"CE_SEWEREXCAVATIONINFO"',
    '"CE_SEWEREXCAVATIONEXPORT"',
    'private const string LinkRecordName = "CE_SEWER_EXCAVATION_LINKS"',
    "internal static int RefreshAll(Document document)",
    "CE_SEWEREXCAVATIONREFRESH stopped",
    "SideAllowance",
    "MinimumWidth",
    "BeddingThickness",
    "FallbackCover",
    "pipeDisplacement",
    "SimpleXlsxWriter.Write(",
    "GridReportPresenter.ShowReportAndOfferTable(",
)
require(
    NORMALIZER,
    "include linked sewer excavation schedules in CE_REFRESHALL",
    "add sewer excavation workflows to the BOQ centre",
    "add linked sewer excavation commands to the Quantity ribbon menu",
)
require(
    PRODUCTION,
    '"CE_SEWEREXCAVATION "',
    '"CE_SEWEREXCAVATIONREFRESH "',
    '"CE_SEWEREXCAVATIONINFO "',
    '"CE_SEWEREXCAVATIONEXPORT "',
)
require(
    PRESENTATION,
    "summary.BoqTables += SewerExcavationCommentCommands.RefreshAll(document);",
)
require(
    RIBBON,
    '"CE_SEWEREXCAVATION "',
    '"CE_SEWEREXCAVATIONREFRESH "',
    '"CE_SEWEREXCAVATIONINFO "',
    '"CE_SEWEREXCAVATIONEXPORT "',
)
require(
    BUILD,
    "Apply-Comments-Quantities.ps1",
    "Applying linked sewer excavation quantity corrections",
)

print("Linked sewer excavation quantity validation passed.")

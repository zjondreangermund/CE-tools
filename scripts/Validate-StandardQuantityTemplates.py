#!/usr/bin/env python3
"""Validate linked parking/driveway and sidewalk quantity templates."""

from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "src" / "CE.Tools.Civil3D" / "StandardQuantityTemplateCommands.cs"
RIBBON = ROOT / "src" / "CE.Tools.Civil3D" / "PluginEntry.cs"
PRESENTATION = ROOT / "src" / "CE.Tools.Civil3D" / "CommentPresentationCommands.cs"
NORMALIZER = ROOT / "scripts" / "Apply-Master-Items-Phase2-Quantities.ps1"


def require(path: Path, *needles: str) -> None:
    if not path.exists():
        raise SystemExit(f"Missing required file: {path.relative_to(ROOT)}")
    text = path.read_text(encoding="utf-8")
    for needle in needles:
        if needle not in text:
            raise SystemExit(f"Missing {needle!r} in {path.relative_to(ROOT)}")


require(
    SOURCE,
    '"CE_STANDARDQTYTOOLS"',
    '"CE_STANDARDQTY"',
    '"CE_STANDARDQTYREFRESH"',
    '"CE_STANDARDQTYEXPORT"',
    '"CE_STANDARDQTYINFO"',
    'private const string LinkRecordName = "CE_STANDARD_QUANTITY_TEMPLATE"',
    'StandardQuantityTemplate.ParkingDriveway',
    'StandardQuantityTemplate.Sidewalk',
    '"80 mm 35 MPa interlocks"',
    '"60 mm 25 MPa brick paving"',
    '"20 mm sand"',
    '"150 mm subbase - G5 @ 95% MOD AASHTO"',
    '"150 mm selected subgrade - G6 @ 93% MOD AASHTO"',
    '"150 mm roadbed - rip and recompact to 90% MOD AASHTO"',
    '"Fill - G9 @ 90% MOD AASHTO (up to assembly datum)"',
    '"Kerbs and channels"',
    '"V-drains"',
    '"Road markings"',
    '"Road signs"',
    'area * link.SandThickness',
    'area * link.SubbaseThickness',
    'SimpleXlsxWriter.Write(path, "Standard Quantities", cells)',
    'internal static int RefreshAll(Document document)',
    'OFFICE TEMPLATE — VERIFY PROJECT SPECIFICATION',
)
require(
    NORMALIZER,
    '"CE_STANDARDQTYTOOLS "',
    '"CE_STANDARDQTY "',
    '"CE_STANDARDQTYREFRESH "',
    '"CE_STANDARDQTYEXPORT "',
    '"CE_STANDARDQTYINFO "',
    'summary.BoqTables += StandardQuantityTemplateCommands.RefreshAll(document);',
)
# Extended Phase 2 CI applies the normalizer before validation.
require(
    RIBBON,
    'CE_STANDARDQTYTOOLS ',
    'CE_STANDARDQTY ',
    'CE_STANDARDQTYREFRESH ',
    'CE_STANDARDQTYEXPORT ',
    'CE_STANDARDQTYINFO ',
)
require(
    PRESENTATION,
    'summary.BoqTables += StandardQuantityTemplateCommands.RefreshAll(document);',
)

text = SOURCE.read_text(encoding="utf-8")
if text.count("{") != text.count("}"):
    raise SystemExit("Unbalanced braces in StandardQuantityTemplateCommands.cs")

print("Standard quantity template validation passed.")

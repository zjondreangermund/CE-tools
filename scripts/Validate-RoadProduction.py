#!/usr/bin/env python3
"""Validate batch road alignment, profile and corridor production source."""

from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "src" / "CE.Tools.Civil3D" / "RoadProductionCommentCommands.cs"
RIBBON = ROOT / "src" / "CE.Tools.Civil3D" / "PluginEntry.cs"
NORMALIZER = ROOT / "scripts" / "Apply-Comments-Road.ps1"
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
    '"CE_ROADPRODUCTION"',
    '"CE_ROADALIGN"',
    '"CE_ROADPROFILES"',
    '"CE_ROADCORRIDORS"',
    '"CE_ROADPRODUCTIONINFO"',
    'private const string RegAppName = "CE_ROAD_PRODUCTION"',
    'private const string StyleRecordName = "PROJECT_STYLE_SELECTION"',
    "CivilAlignment.Create(",
    'item.Name == "CreateFromSurface"',
    'item.Name == "Create"',
    'ReadProperty(civilDocument, "CorridorCollection")',
    'item.Name == "Add"',
    'new CivilObjectPickerWindow(',
    'new ProductionChoiceWindow(',
    'GridReportPresenter.ShowReportAndOfferTable(',
    'WriteTag(alignment, "Alignment"',
    'WriteTag(profile, "Profile"',
    'WriteTag(view, "ProfileView"',
    'WriteTag(corridor, "Corridor"',
    'candidate = value + "-" + suffix++.ToString(CultureInfo.InvariantCulture);',
)
require(
    NORMALIZER,
    "fix sequential road-name suffix formatting",
    "add batch road alignment profile and corridor commands to the Alignment ribbon menu",
    'Cmd("Road Production Centre", "CE_ROADPRODUCTION "',
    'Cmd("Create Sequential Road Alignments", "CE_ROADALIGN "',
    'Cmd("Create Road EG Profiles and Views", "CE_ROADPROFILES "',
    'Cmd("Create Road Corridors", "CE_ROADCORRIDORS "',
    'Cmd("Road Production Information", "CE_ROADPRODUCTIONINFO "',
)
require(
    RIBBON,
    '"CE_ROADPRODUCTION "',
    '"CE_ROADALIGN "',
    '"CE_ROADPROFILES "',
    '"CE_ROADCORRIDORS "',
    '"CE_ROADPRODUCTIONINFO "',
)
require(
    BUILD,
    "Apply-Comments-Road.ps1",
    "Applying batch road-production corrections",
)

print("Batch road-production validation passed.")

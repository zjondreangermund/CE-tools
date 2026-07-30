#!/usr/bin/env python3
"""Validate Phase 2 linked detailed-section annotation source."""

from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "src" / "CE.Tools.Civil3D" / "DetailedSectionAnnotationCommands.cs"
RIBBON = ROOT / "src" / "CE.Tools.Civil3D" / "PluginEntry.cs"
NORMALIZER = ROOT / "scripts" / "Apply-Master-Items-Phase2-DetailedSections.ps1"


def require(path: Path, *needles: str) -> None:
    if not path.exists():
        raise SystemExit(f"Missing required file: {path.relative_to(ROOT)}")
    text = path.read_text(encoding="utf-8")
    for needle in needles:
        if needle not in text:
            raise SystemExit(f"Missing {needle!r} in {path.relative_to(ROOT)}")


require(
    SOURCE,
    '"CE_SECTIONDETAILTOOLS"',
    '"CE_SECTIONDETAILCREATE"',
    '"CE_SECTIONDETAILREFRESH"',
    '"CE_SECTIONDETAILINFO"',
    '"CE_SECTIONDETAILCLEAR"',
    'private const string RegAppName = "CE_SECTION_DETAIL"',
    'private const string AnnotationLayer = "CE-SECTION-DETAIL-ANNO"',
    'SectionDetailDiscipline.Road',
    'SectionDetailDiscipline.Parking',
    'SectionDetailDiscipline.Stormwater',
    'SectionDetailDiscipline.Sewer',
    'SectionDetailDiscipline.Water',
    'new RotatedDimension(',
    'CreateComponentTable(',
    '"LINKED SECTION COMPONENT REGISTER"',
    '"Source=" + handle',
    '"Set=" + settings.SetId',
    'BuildSnapshot(',
    'ReadSet(',
    'EraseSet(',
    'entity is Circle',
    'entity is Polyline3d',
    'Source geometry changed',
    'Drafting automation — verify dimensions, notes and project standards',
    'The command creates linked annotation only.',
)
require(
    NORMALIZER,
    'Cmd("Detailed Section Tools", "CE_SECTIONDETAILTOOLS "',
    'Cmd("Create Detailed Section Annotation", "CE_SECTIONDETAILCREATE "',
    'Cmd("Refresh Detailed Section Annotation", "CE_SECTIONDETAILREFRESH "',
    'Cmd("Detailed Section Information", "CE_SECTIONDETAILINFO "',
    'Cmd("Clear Detailed Section Annotation", "CE_SECTIONDETAILCLEAR "',
)
require(
    RIBBON,
    'CE_SECTIONDETAILTOOLS ',
    'CE_SECTIONDETAILCREATE ',
    'CE_SECTIONDETAILREFRESH ',
    'CE_SECTIONDETAILINFO ',
    'CE_SECTIONDETAILCLEAR ',
)

text = SOURCE.read_text(encoding="utf-8")
if text.count("{") != text.count("}"):
    raise SystemExit("Unbalanced braces in DetailedSectionAnnotationCommands.cs")
if "Microsoft.Office.Interop" in text:
    raise SystemExit("Detailed-section annotations must not introduce Office COM automation")

print("Master Items Phase 2 detailed-section annotation validation passed.")

#!/usr/bin/env python3
"""Validate Batch 8 ribbon presentation and linked client-book source shape."""

from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "src" / "CE.Tools.Civil3D"

files = {
    "ribbon": SRC / "PluginEntry.cs",
    "visuals": SRC / "RibbonVisuals.cs",
    "book": SRC / "ClientBookCommands.cs",
}

errors: list[str] = []
texts: dict[str, str] = {}
for key, path in files.items():
    if not path.exists():
        errors.append(f"Missing Batch 8 file: {path.relative_to(ROOT)}")
        texts[key] = ""
    else:
        texts[key] = path.read_text(encoding="utf-8")

ribbon = texts["ribbon"]
visuals = texts["visuals"]
book = texts["book"]

for fragment in (
    'Title = "CE TOOLS"',
    '"PROJECT"',
    '"SURVEY"',
    '"DRAWINGS"',
    '"GEOMETRY"',
    '"CORRIDORS"',
    '"SITE DESIGN"',
    '"UTILITIES"',
    '"STANDARDS & DETAILS"',
    '"ANALYSIS"',
    '"PRODUCTION"',
    "RibbonItemSize.Large",
    "ShowImage = true",
    "RibbonVisuals.Small(id)",
    "RibbonVisuals.Large(id)",
    '"CE_PROJECTCLOSEOUT "',
    '"CE_CLIENTBOOK "',
    '"CE_CLIENTBOOKREFRESH "',
    '"CE_CLIENTBOOKINFO "',
    '"CE_CLIENTBOOKINDEX "',
    '"CE_BMVERT "',
    '"CE_TLENGTH "',
    '"CE_TAREA "',
):
    if fragment not in ribbon:
        errors.append(f"Ribbon source is missing: {fragment}")

if ribbon.count("AddPanel(") < 10:
    errors.append("The polished ribbon must retain at least ten engineering panels")

for fragment in (
    "RenderTargetBitmap",
    "FormattedText",
    "Segoe UI",
    "ResolveStyle",
    "CE_TOOLS_CLIENT_BOOK_MENU",
    "PixelFormats.Pbgra32",
):
    if fragment not in visuals:
        errors.append(f"Ribbon visual source is missing: {fragment}")

for command in (
    "CE_PROJECTCLOSEOUT",
    "CE_CLIENTBOOK",
    "CE_CLIENTBOOKREFRESH",
    "CE_CLIENTBOOKINFO",
    "CE_CLIENTBOOKINDEX",
):
    if f'"{command}"' not in book:
        errors.append(f"Client-book source is missing command: {command}")

for fragment in (
    'LinkRecordName = "CE_CLIENT_BOOK_PAGE"',
    '"A4", 297.0, 210.0',
    '"A3", 420.0, 297.0',
    '"00", "Cover and Issue Information"',
    '"01", "Project Summary"',
    '"02", "Design Discipline Summary"',
    '"03", "Quantity Summary"',
    '"04", "Drawing Register"',
    '"05", "Cross-Section Register"',
    '"06", "Typical Detail Schedule"',
    "WriteClientPageLink",
    "ReadAllClientPageLinks",
    "GeneratedHandles",
    "CE_BOQ_LINKS",
    "DynamicCrossSectionCommands.LinkRecordName",
    "SimpleXlsxWriter.Write",
    "Use only office-approved, engineer-reviewed DWG detail blocks",
):
    if fragment not in book:
        errors.append(f"Client-book source is missing: {fragment}")

for detail in (
    "Railway Track Section",
    "Airport Runway Layout and Sections",
    "Airport Taxiway Layout and Sections",
    "Roundabout Layout and Sections",
    "RCC Box Culvert Assembly",
    "Valve Assembly Details",
    "Parking Plan and Bay Details",
    "Headwall and Wingwall Detail",
    "Manhole Detail",
    "Underground Water Tank Detail",
):
    if detail not in book:
        errors.append(f"Typical-detail schedule is missing: {detail}")

if "PublishExecute" in book or "PlotToFile" in book:
    errors.append(
        "Client-book creation must not silently publish with unverified workstation plot settings"
    )

if errors:
    print("Batch 8 presentation/client-book validation failed:", file=sys.stderr)
    for error in errors:
        print(f"- {error}", file=sys.stderr)
    raise SystemExit(1)

print("Batch 8 polished ribbon and linked A4/A3 client-book source passed.")

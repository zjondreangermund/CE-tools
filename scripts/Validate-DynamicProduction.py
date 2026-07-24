#!/usr/bin/env python3
"""Source-shape checks for review-comments Batch 7.

GitHub Actions cannot load Autodesk/Civil 3D assemblies. These checks verify
that linked cross-section persistence, deferred automatic refresh, reports,
summary sheets, drawing-book layouts and established commands remain present.
Civil 3D 2023/2024 compilation and runtime validation remain mandatory.
"""

from pathlib import Path
import re
import sys

ROOT = Path(__file__).resolve().parents[1]
SECTION = ROOT / "src" / "CE.Tools.Civil3D" / "DynamicCrossSectionCommands.cs"
PRODUCTION = ROOT / "src" / "CE.Tools.Civil3D" / "ProductionReportCommands.cs"
RIBBON = ROOT / "src" / "CE.Tools.Civil3D" / "PluginEntry.cs"
BOQ = ROOT / "src" / "CE.Tools.Civil3D" / "BillOfQuantitiesCommands.cs"
QUANTITY = ROOT / "src" / "CE.Tools.Civil3D" / "QuantityCommands.cs"

errors: list[str] = []
for path in (SECTION, PRODUCTION, RIBBON, BOQ, QUANTITY):
    if not path.exists():
        errors.append(f"Missing required file: {path.relative_to(ROOT)}")

if errors:
    print("\n".join(errors), file=sys.stderr)
    raise SystemExit(1)

section = SECTION.read_text(encoding="utf-8")
production = PRODUCTION.read_text(encoding="utf-8")
ribbon = RIBBON.read_text(encoding="utf-8")
boq = BOQ.read_text(encoding="utf-8")
quantity = QUANTITY.read_text(encoding="utf-8")

section_commands = [
    "CE_XSTOOLS",
    "CE_XSCREATE",
    "CE_XSREFRESH",
    "CE_XSINFO",
    "CE_XSDETACH",
    "CE_XSMONITOR",
]
production_commands = [
    "CE_REPORTTOOLS",
    "CE_REPORTFULL",
    "CE_REPORTDISC",
    "CE_REPORTEXPORT",
    "CE_REPORTROAD",
    "CE_REPORTPLATFORM",
    "CE_REPORTSTORM",
    "CE_REPORTSEWER",
    "CE_REPORTWATER",
    "CE_REPORTBULKWATER",
    "CE_SUMMARYSHEET",
    "CE_SUMMARYREFRESH",
    "CE_SUMMARYINFO",
    "CE_DRAWINGBOOK",
    "CE_BOOKINDEX",
]

for command in section_commands:
    if f'"{command}"' not in section:
        errors.append(f"Dynamic-section command is not declared: {command}")
    if f'"{command} "' not in ribbon:
        errors.append(f"Ribbon is not linked to dynamic-section command: {command}")

for command in production_commands:
    if f'"{command}"' not in production:
        errors.append(f"Production command is not declared: {command}")
    if f'"{command} "' not in ribbon:
        errors.append(f"Ribbon is not linked to production command: {command}")

section_markers = [
    'LinkRecordName = "CE_DYNAMIC_SECTION"',
    'GeneratedRecordName = "CE_DYNAMIC_SECTION_GENERATED"',
    '"Generated=" + handle',
    '"Source=" + sourceHandle',
    "FindElevationAtXY",
    "IntersectWith",
    "BuildFeatureSchedule",
    "AlignedDimension",
    "DynamicSectionUpdateManager.Initialize()",
    "Database.ObjectModified",
    "Database.ObjectAppended",
    "Editor.IsQuiescent",
    "document.LockDocument()",
    "BeginInternalUpdate",
    "No intersected design elements were found; the existing generated section was left unchanged.",
]
for marker in section_markers:
    if marker not in section and marker not in ribbon:
        errors.append(f"Dynamic-section implementation is missing: {marker}")

production_markers = [
    'SummaryRecordName = "CE_PROJECT_SUMMARY_SHEET"',
    'BookRecordName = "CE_DRAWING_BOOK_LAYOUT"',
    '"CE_TOOLS"',
    '"PROJECT_METADATA"',
    "GridReportPresenter.ShowReportAndOfferTable",
    "SimpleXlsxWriter.Write",
    '"CE-CLIENT-A4"',
    '"CE-CLIENT-A3"',
    '"CE-CONSTRUCTION-A1"',
    '"CE-CONSTRUCTION-A0"',
    '297.0, 210.0',
    '420.0, 297.0',
    '841.0, 594.0',
    '1189.0, 841.0',
    "LayoutManager.Current.CreateLayout",
    "Run CE_SUMMARYREFRESH after design or layout changes",
]
for marker in production_markers:
    if marker not in production:
        errors.append(f"Production implementation is missing: {marker}")

preserved = [
    ('"CE_TLENGTH"', quantity, "Total Length command declaration"),
    ('"CE_TAREA"', quantity, "Total Area command declaration"),
    ('"CE_TLENGTH ', ribbon, "Total Length ribbon entry"),
    ('"CE_TAREA ', ribbon, "Total Area ribbon entry"),
    ('"CE_BMVERT ', ribbon, "Bellmouth Densifier ribbon entry"),
    ('"CE_COORDPOLY2 ', ribbon, "Batch 5 linked polyline points"),
    ('"CE_BOQBUILD ', ribbon, "Batch 6 linked BOQ"),
    ('"CE_CORREBUILDX ', ribbon, "Batch 4 corridor rebuild"),
    ('"CE_ANNOTSETTINGS ', ribbon, "Batch 3 annotation settings"),
    ('SimpleXlsxWriter', boq, "Batch 6 dependency-free Excel writer"),
]
for marker, text, description in preserved:
    if marker not in text:
        errors.append(f"Existing working utility was removed: {description}")

if "Microsoft.Office.Interop" in production:
    errors.append("Production reports must not introduce Excel COM automation")

for name, text in (
    (SECTION.name, section),
    (PRODUCTION.name, production),
    (RIBBON.name, ribbon),
):
    if text.count("{") != text.count("}"):
        errors.append(f"Unbalanced braces detected in {name}")

if re.search(r"(?:TextHeight|Height)\s*=\s*(?:2500|5000)", section + production):
    errors.append("Oversized legacy annotation height was introduced")

if errors:
    print("CE Tools dynamic-section and production validation failed:", file=sys.stderr)
    for error in errors:
        print(f"- {error}", file=sys.stderr)
    raise SystemExit(1)

print("CE Tools dynamic-section, reporting and drawing-book source validation passed.")

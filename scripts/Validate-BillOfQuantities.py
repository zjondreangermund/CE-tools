#!/usr/bin/env python3
"""Source-shape checks for review-comments Batch 6.

Autodesk/Civil 3D assemblies are unavailable in GitHub Actions. These checks
verify the linked BOQ contract, discipline exports, dependency-free Open XML
writer, ribbon wiring and preservation of established commands. Civil 3D
2023/2024 compilation and drawing validation remain mandatory before release.
"""

from pathlib import Path
import re
import sys

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "src" / "CE.Tools.Civil3D" / "BillOfQuantitiesCommands.cs"
RIBBON = ROOT / "src" / "CE.Tools.Civil3D" / "PluginEntry.cs"
PROJECT = ROOT / "src" / "CE.Tools.Civil3D" / "CE.Tools.Civil3D.csproj"
QUICK = ROOT / "src" / "CE.Tools.Civil3D" / "QuantityCommands.cs"

errors: list[str] = []
for path in (SOURCE, RIBBON, PROJECT, QUICK):
    if not path.exists():
        errors.append(f"Missing required file: {path.relative_to(ROOT)}")

if errors:
    print("\n".join(errors), file=sys.stderr)
    raise SystemExit(1)

source = SOURCE.read_text(encoding="utf-8")
ribbon = RIBBON.read_text(encoding="utf-8")
project = PROJECT.read_text(encoding="utf-8")
quick = QUICK.read_text(encoding="utf-8")

commands = [
    "CE_BOQTOOLS",
    "CE_BOQBUILD",
    "CE_BOQREFRESH",
    "CE_BOQINFO",
    "CE_BOQEXPORT",
    "CE_BOQROAD",
    "CE_BOQPLATFORM",
    "CE_BOQSTORM",
    "CE_BOQSEWER",
    "CE_BOQWATER",
    "CE_BOQBULKWATER",
]
for command in commands:
    if f'"{command}"' not in source:
        errors.append(f"BOQ command is not declared: {command}")
    if f'"{command} "' not in ribbon:
        errors.append(f"Ribbon is not linked to BOQ command: {command}")

required_source_markers = [
    'LinkRecordName = "CE_BOQ_LINKS"',
    '"Schema=" + LinkSchema',
    '"Discipline=" + link.Discipline',
    '"UnitsPerMetre="',
    '"Handle=" + handle',
    "database.GetObjectId",
    "ReadRateMap",
    "preserving matching rates",
    "A BOQ table cannot be populated with zero rows.",
    '"QUANTITY", "RATE", "AMOUNT"',
    '"m³"',
    '"m²"',
    '"No."',
    'BoqDiscipline.Road',
    'BoqDiscipline.Platform',
    'BoqDiscipline.Stormwater',
    'BoqDiscipline.Sewer',
    'BoqDiscipline.Water',
    'BoqDiscipline.BulkWater',
    '"Parking and driveway layerworks"',
    '"Sidewalk layerworks"',
    '"Kerbs and channels"',
    '"Road markings"',
    '"Road signs"',
    'new ZipArchive(',
    'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml',
    '"xl/worksheets/sheet1.xml"',
]
for marker in required_source_markers:
    if marker not in source:
        errors.append(f"Linked BOQ implementation is missing: {marker}")

if "Microsoft.Office.Interop" in source or "Excel.Application" in source:
    errors.append("Excel COM automation was introduced; Batch 6 must remain dependency-free")

for reference in (
    '<Reference Include="System.IO.Compression" />',
    '<Reference Include="System.IO.Compression.FileSystem" />',
):
    if reference not in project:
        errors.append(f"Required framework reference is missing: {reference}")

preserved = [
    ('"CE_TLENGTH"', quick, "Total Length command declaration"),
    ('"CE_TAREA"', quick, "Total Area command declaration"),
    ('"CE_TLENGTH ', ribbon, "Total Length ribbon entry"),
    ('"CE_TAREA ', ribbon, "Total Area ribbon entry"),
    ('"CE_BMVERT ', ribbon, "Bellmouth Densifier ribbon entry"),
    ('"CE_COORDPOLY2 ', ribbon, "Batch 5 linked polyline points"),
    ('"CE_CORREBUILDX ', ribbon, "Batch 4 corridor rebuild"),
    ('"CE_ANNOTSETTINGS ', ribbon, "Batch 3 annotation settings"),
]
for marker, text, description in preserved:
    if marker not in text:
        errors.append(f"Existing working utility was removed: {description}")

if re.search(r"SetColumnWidth\([^\n]*(?:2500|5000)", source):
    errors.append("Oversized BOQ table column width was introduced")

for name, text in (
    (SOURCE.name, source),
    (RIBBON.name, ribbon),
):
    if text.count("{") != text.count("}"):
        errors.append(f"Unbalanced braces detected in {name}")

if errors:
    print("CE Tools linked BOQ validation failed:", file=sys.stderr)
    for error in errors:
        print(f"- {error}", file=sys.stderr)
    raise SystemExit(1)

print("CE Tools linked BOQ and dependency-free Excel source validation passed.")

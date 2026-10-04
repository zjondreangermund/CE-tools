#!/usr/bin/env python3
"""Regression guards for Oct 4 table export, BOQ bellmouth and sewer branch labels."""

from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "src" / "CE.Tools.Civil3D"

files = {
    "menu": SRC / "DynamicRefreshContextMenu.cs",
    "excel": SRC / "TableExcelExportCommands.cs",
    "boq": SRC / "August13RoadConstructionBoqCommands.cs",
    "placement": SRC / "SewerBranchLabelPlacement.cs",
    "align": SRC / "SewerBranchAlignmentCommands.cs",
    "settings": SRC / "SewerProductionCommands.cs",
}

texts = {}
errors = []
for key, path in files.items():
    if not path.is_file():
        errors.append("Missing source: " + str(path))
        texts[key] = ""
    else:
        texts[key] = path.read_text(encoding="utf-8-sig")

menu = texts["menu"]
excel = texts["excel"]
boq = texts["boq"]
placement = texts["placement"]
align = texts["align"]
settings = texts["settings"]

for marker in [
    '"Export Selected Table(s) to Excel"',
    '"CE_TABLEEXPORTEXCEL"',
    '"Field Completion"',
    '"CE_FIELDCOMPLETION"',
    '"CE_SEWERFIELDSUPPLEMENTARY"',
    '"CE_ROADFIELDSUPPLEMENTARY"',
    '"CE_CADSUPPLEMENTARY"',
    '"CE_PLATFORMFIELDSUPPLEMENTARY"',
    '"CE_SURVEYFIELDSUPPLEMENTARY"',
    'AddObjectContextMenuExtension(',
    'RXObject.GetClass(typeof(Table))',
    '"Export This Table to Excel"',
]:
    if marker not in menu:
        errors.append("Missing context-menu marker: " + marker)

for marker in [
    '"CE_TABLEEXPORTEXCEL"',
    'CommandFlags.UsePickSet',
    'SaveFileDialog',
    '"Excel Workbook (*.xlsx)|*.xlsx"',
    'ZipArchive',
    '"[Content_Types].xml"',
    '"xl/workbook.xml"',
    'WorksheetXml(',
    'ReadImpliedTableIds(',
]:
    if marker not in excel:
        errors.append("Missing Excel-export marker: " + marker)

for marker in [
    '"Junction bellmouths"',
    'JunctionBellmouthLength',
    'ReadJunctionBellmouthLength(',
    'IsJunctionBellmouthCurve(',
    '"CE-ROAD-JUNCTION"',
    'BellmouthGeometryKey(',
]:
    if marker not in boq:
        errors.append("Missing bellmouth-BOQ marker: " + marker)

for marker in [
    'BuildStraightRuns(',
    'runCentreDistance',
    '"Every pipe"',
    '"Every second pipe"',
    'pipeCentreDistance',
    'SameStraightDirection(',
]:
    if marker not in placement:
        errors.append("Missing branch-placement marker: " + marker)

for marker in [
    'productionSettings.BranchLabelLongSectionLength',
    'productionSettings.BranchLabelLongSectionFrequency',
]:
    if marker not in align:
        errors.append("Missing sewer-alignment integration marker: " + marker)

for marker in [
    '"BranchLabelLongSectionLength"',
    '"BranchLabelLongSectionFrequency"',
    'public double BranchLabelLongSectionLength',
    'public string BranchLabelLongSectionFrequency',
    'Value("BranchLabelLongSectionLength"',
    'Value("BranchLabelLongSectionFrequency"',
]:
    if marker not in settings:
        errors.append("Missing sewer-setting persistence marker: " + marker)

if errors:
    print("October 4 table/bellmouth/sewer-label validation FAILED", file=sys.stderr)
    for error in errors:
        print(" - " + error, file=sys.stderr)
    raise SystemExit(1)

print(
    "October 4 table/bellmouth/sewer-label validation passed: table object "
    "right-click Excel export, supplementary context actions, junction bellmouth "
    "BOQ totals, and straight-run sewer branch label density are guarded."
)

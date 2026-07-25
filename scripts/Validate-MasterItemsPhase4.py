#!/usr/bin/env python3
"""Validate Phase 4 specialist-model exchange and result-import source."""

from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "src" / "CE.Tools.Civil3D" / "SpecialistModelExchangeCommands.cs"
RIBBON = ROOT / "src" / "CE.Tools.Civil3D" / "PluginEntry.cs"
NORMALIZER = ROOT / "scripts" / "Apply-Master-Items-Phase4.ps1"


def require(path: Path, *needles: str) -> None:
    if not path.exists():
        raise SystemExit(f"Missing required file: {path.relative_to(ROOT)}")
    text = path.read_text(encoding="utf-8")
    for needle in needles:
        if needle not in text:
            raise SystemExit(f"Missing {needle!r} in {path.relative_to(ROOT)}")


require(
    SOURCE,
    '"CE_MODELEXCHANGETOOLS"',
    '"CE_MODELEXPORTPACKAGE"',
    '"CE_MODELRESULTTEMPLATE"',
    '"CE_MODELRESULTIMPORT"',
    '"CE_MODELRESULTINFO"',
    '"CE_MODELRESULTCLEAR"',
    'private const string ResultRegApp = "CE_MODEL_RESULT_IMPORT"',
    'private const int MaximumImportRows = 250000',
    'private const int MaximumExportVertices = 1000000',
    'Existing package files will not be overwritten',
    'File.Exists(manifestPath) || File.Exists(geometryPath)',
    'SafeDelete(geometryPath)',
    'SafeDelete(manifestPath)',
    'SHA256.Create()',
    '"sourceDrawingSha256"',
    '"drawingUnitsPerMetre"',
    '"coordinateSystemCode"',
    '"geometrySha256"',
    '"curveSampleSpacingDrawingUnits"',
    'CivilApplication.ActiveDocument',
    '"FeatureId,PartIndex,VertexIndex,ObjectType,Layer,Handle,X,Y,Z,Role"',
    '"X,Y,Z,Depth,Velocity,WaterLevel,Scenario,Time',
    'The result CSV must contain X and Y columns.',
    'CSV coordinate units per metre',
    'Drawing units per metre',
    'coordinateScale = drawingUnitsPerMetre / coordinateUnitsPerMetre',
    'GetOrCreateResultLayer(',
    'ResultCategory(row.Depth, row.Velocity)',
    'HazardIndex(row.Depth, row.Velocity)',
    'entity.XData = new ResultBuffer(',
    'DxfCode.ExtendedDataRegAppName',
    'GetXDataForApplication(ResultRegApp)',
    'if (!HasResultRecord(entity)) continue;',
    'Source files and unrelated drawing objects were unchanged.',
    'Verify CRS, datum, units, scenario/time, interpolation and specialist-model assumptions',
    'The package is vendor-neutral.',
)

require(
    NORMALIZER,
    'use backing fields for specialist-result summary ranges',
    'AddIntegrationPanel(tab);',
    'CE_TOOLS_CATEGORY_INTEGRATION',
    'Cmd("Specialist Model Exchange Tools", "CE_MODELEXCHANGETOOLS "',
    'Cmd("Export Specialist Model Package", "CE_MODELEXPORTPACKAGE "',
    'Cmd("Create Result CSV Template", "CE_MODELRESULTTEMPLATE "',
    'Cmd("Import Specialist Model Results", "CE_MODELRESULTIMPORT "',
    'Cmd("Imported Result Information", "CE_MODELRESULTINFO "',
    'Cmd("Clear Imported Model Results", "CE_MODELRESULTCLEAR "',
)

require(
    RIBBON,
    'CE_MODELEXCHANGETOOLS ',
    'CE_MODELEXPORTPACKAGE ',
    'CE_MODELRESULTTEMPLATE ',
    'CE_MODELRESULTIMPORT ',
    'CE_MODELRESULTINFO ',
    'CE_MODELRESULTCLEAR ',
)

source_text = SOURCE.read_text(encoding="utf-8")
if source_text.count("{") != source_text.count("}"):
    raise SystemExit("Unbalanced braces in SpecialistModelExchangeCommands.cs")
if "Microsoft.Office.Interop" in source_text:
    raise SystemExit("Specialist exchange must not introduce Office COM automation")
if "ref MinimumDepth" in source_text or "ref MaximumDepth" in source_text:
    raise SystemExit("Result summary properties must not be passed by ref")
if "File.Copy(" in source_text or "File.Move(" in source_text:
    raise SystemExit("Specialist exchange must not silently copy or move source model files")
if "entity.Erase();" not in source_text or "HasResultRecord(entity)" not in source_text:
    raise SystemExit("Clear workflow must erase only tagged imported result graphics")

print("Master Items Phase 4 specialist-model exchange validation passed.")

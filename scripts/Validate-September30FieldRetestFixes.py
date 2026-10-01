#!/usr/bin/env python3
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
errors = []

def read(relative):
    path = ROOT / relative
    if not path.is_file():
        errors.append("missing file: " + relative)
        return ""
    return path.read_text(encoding="utf-8-sig")

break_engine = read("src/CE.Tools.Civil3D/August25CadSupplementaryBreakEngine.cs")
bands = read("src/CE.Tools.Civil3D/September14AlignmentBandStyleCommands.cs")
assembly = read("src/CE.Tools.Civil3D/CivilAssemblyTemplateImporter.cs")
sewer = read("src/CE.Tools.Civil3D/SewerBranchAlignmentCommands.cs")

for marker in [
    "September04VerifiedJunctionBreakRuntime.BreakPolylinesAtJunctions(document);",
    "eDegenerateGeometry",
]:
    if marker not in break_engine:
        errors.append("junction break fix missing: " + marker)
if "September04FieldGeometryCompletionCommands.BreakAtJunctions(document);" in break_engine:
    errors.append("junction break engine still routes to native GetSplitCurves runtime")

for marker in [
    "nativeImportFailure",
    "rowsBeforeImport",
    "EnsureImportedBandRows(profileView, bandSetStyle)",
    "continued with {1} existing/materialized native band row(s)",
]:
    if marker not in bands:
        errors.append("road band fallback missing: " + marker)

for marker in [
    "DiscoverySync",
    "_discoveryCache",
    '"enu", "Tool Palettes"',
    "if (templates.Count == 0)",
]:
    if marker not in assembly:
        errors.append("assembly discovery performance guard missing: " + marker)

for marker in [
    "ResolveSequencedBranchStart(",
    'string expectedStartName = "MH"',
    ".OrderBy(record => record.SequenceNumber)",
    "Run CE_SEWSEQ before CE_SEWALIGN",
    "alignment starts at sequenced start manhole",
    "ForceAlignmentStartStationZero(",
    '"StartingStation"',
    '"ReferencePointStation"',
    "Every branch starts at MH#.1 with station 0+000",
]:
    if marker not in sewer:
        errors.append("sewer alignment sequence-start guard missing: " + marker)
if ".OrderByDescending(id => GetRimElevation(id, transaction))" in sewer:
    errors.append("sewer alignment still re-derives branch start from rim elevation")

if errors:
    print("September 30 field retest validation FAILED")
    for error in errors:
        print(" -", error)
    raise SystemExit(1)

print("September 30 field retest validation passed.")

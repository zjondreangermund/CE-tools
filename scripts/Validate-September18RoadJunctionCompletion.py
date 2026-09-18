#!/usr/bin/env python3
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

checks = {
    "src/CE.Tools.Civil3D/September18RoadJunctionCompletionCommands.cs": [
        'CE_ROADALIGNREVERSEMULTI',
        'CE_ROADSURFACENAMES',
        'CE_ROADTOPBOTTOMPROFILE',
        'CE_ROADTJUNCTIONASSEMBLYLIMITS',
        'CE_JUNCTIONENDPOINTSTOTOPSURFACES',
        'CE_PIPESLOPETOOUTLET',
        'TryStationOffset',
        'CreateSurfaceProfile',
        'TOP-RD-',
        'BOTTOM-RD-',
    ],
    "src/CE.Tools.Civil3D/September16FieldCommentCompletionCommands.cs": [
        '"Feature Lines"',
        '"T-LIMIT"',
        '"X-LIMIT"',
        'ColorMethod.ByAci, 6',
        '"Feature-line site"',
        '"Feature-line weed distance"',
        'CE_ROADTJUNCTIONASSEMBLYLIMITS',
    ],
    "src/CE.Tools.Civil3D/RoadCorridorCompletionCommands.cs": [
        'RoadNumberedSurfaceNames',
        'ResolveRoadSurfaceName',
        '"TOP-RD-01, BOTTOM-RD-01, TOP-RD-02, BOTTOM-RD-02',
    ],
    "src/CE.Tools.Civil3D/August17ProductionFeatureLineCommands.cs": [
        'normal independent Civil 3D feature lines',
        '"Output feature-line site"',
        '"Normal feature-line weed distance"',
        'ApplyFeatureLineWeeding',
    ],
    "src/CE.Tools.Civil3D/September11FieldCompletionMenu.cs": [
        'CE_ROADALIGNREVERSEMULTI',
        'CE_ROADTJUNCTIONASSEMBLYLIMITS',
        'CE_ROADSURFACENAMES',
        'CE_ROADTOPBOTTOMPROFILE',
        'CE_JUNCTIONENDPOINTSTOTOPSURFACES',
        'CE_PIPESLOPETOOUTLET',
    ],
    "src/CE.Tools.Civil3D/PluginEntry.cs": [
        'CE_ROADALIGNREVERSEMULTI ',
        'CE_ROADTJUNCTIONASSEMBLYLIMITS ',
        'CE_ROADSURFACENAMES ',
        'CE_ROADTOPBOTTOMPROFILE ',
        'CE_JUNCTIONENDPOINTSTOTOPSURFACES ',
        'CE_PIPESLOPETOOUTLET ',
    ],
}

errors = []
for relative, markers in checks.items():
    path = ROOT / relative
    if not path.exists():
        errors.append(f"missing file: {relative}")
        continue
    text = path.read_text(encoding="utf-8")
    for marker in markers:
        if marker not in text:
            errors.append(f"{relative}: missing marker {marker!r}")

# Guard the key behavioral distinctions from the September 18 field request.
junction = (ROOT / "src/CE.Tools.Civil3D/September16FieldCommentCompletionCommands.cs").read_text(encoding="utf-8")
if 'candidate.IsCross' not in junction or 'T-LIMIT' not in junction:
    errors.append("T-junction-only limit generation guard is missing")
if 'X-LIMIT' not in junction:
    errors.append("cross-junction closure generation is missing")

road = (ROOT / "src/CE.Tools.Civil3D/September18RoadJunctionCompletionCommands.cs").read_text(encoding="utf-8")
if 'Cross junctions are not trimmed' not in road:
    errors.append("T-junction corridor trimming must explicitly preserve cross junctions")
if 'outletElevation <= upstreamElevation' not in road:
    errors.append("pipe slope command must leave already-correct outlet slopes unchanged")

if errors:
    print("September 18 road/junction validation FAILED")
    for error in errors:
        print(" -", error)
    raise SystemExit(1)

print("September 18 road/junction validation passed.")

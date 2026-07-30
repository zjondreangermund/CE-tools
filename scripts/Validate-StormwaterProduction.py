#!/usr/bin/env python3
"""Validate the stormwater network-production source shape.

Autodesk assemblies are unavailable on the Linux GitHub runner. This check is
therefore deliberately limited to command registration, traceability markers,
workflow boundaries and preservation of the Civil 3D 2023-compatible ribbon.
"""

from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "src" / "CE.Tools.Civil3D"
FILES = {
    "sequence": SRC / "StormwaterSequenceCommands.cs",
    "production": SRC / "StormwaterProductionCommands.cs",
    "ribbon": SRC / "PluginEntry.cs",
}

errors: list[str] = []
texts: dict[str, str] = {}
for key, path in FILES.items():
    if not path.exists():
        errors.append(f"Missing stormwater source: {path.relative_to(ROOT)}")
        texts[key] = ""
    else:
        texts[key] = path.read_text(encoding="utf-8")

sequence = texts["sequence"]
production = texts["production"]
ribbon = texts["ribbon"]
combined = sequence + "\n" + production

for command in (
    "CE_SWTOOLS",
    "CE_SWSETTINGS",
    "CE_SWSEQ",
    "CE_SWALIGN",
    "CE_SWREFRESH",
    "CE_SWPROFILE",
    "CE_SWINFO",
):
    if f'"{command}"' not in combined:
        errors.append(f"Stormwater command is missing: {command}")

for marker in (
    'SequenceRegAppName = "CE_TOOLS_SWSEQ"',
    'AlignmentRegAppName = "CE_TOOLS_SWALIGN"',
    'ProfileRegAppName = "CE_TOOLS_SWPROFILE"',
    '"SW-MAIN"',
    '"SW-B"',
    '"Automatic"',
    '"SelectMain"',
    "FindAutomaticMainPath",
    "ExtractBranches",
    "ApplyTemporaryNames" if "ApplyTemporaryNames" in sequence else "CE-TMP-SW-P-",
    "StormwaterMetadata.WriteTag",
):
    if marker not in sequence:
        errors.append(f"Stormwater sequencing marker is missing: {marker}")

for marker in (
    "CivilAlignment.Create",
    "CivilPolylineOptions",
    "CreateFromSurface",
    "ProfileView",
    "AddToProfileView",
    "STORMWATER_PRODUCTION_SETTINGS",
    "ResolveStyleId",
    "RemoveGeneratedAlignmentObjects",
    "RemoveGeneratedProfileObjectsForBranch",
    "WriteProductionTag",
    '"Network"',
    '"Polylines"',
):
    if marker not in production:
        errors.append(f"Stormwater production marker is missing: {marker}")

for marker in (
    "CE_TOOLS_STORMWATER_MENU",
    '"CE_SWTOOLS "',
    '"CE_SWSEQ "',
    '"CE_SWALIGN "',
    '"CE_SWREFRESH "',
    '"CE_SWPROFILE "',
    '"CE_SWSETTINGS "',
    '"CE_SWINFO "',
    "private static RibbonItem[] Row",
    "source.Items.Add(item)",
    "RibbonMenuItem",
):
    if marker not in ribbon:
        errors.append(f"Stormwater ribbon marker is missing: {marker}")

for forbidden in (
    "private static RibbonRow Row",
    "private static RibbonButton CreateCommandButton",
):
    if forbidden in ribbon:
        errors.append(f"Incompatible ribbon pattern reintroduced: {forbidden}")

for name, text in texts.items():
    if text.count("{") != text.count("}"):
        errors.append(f"Unbalanced braces in {FILES[name].name}")

if "Confirm(" not in sequence or "Confirm(" not in production:
    errors.append("Stormwater modification workflows must retain preview confirmation")

if errors:
    print("Stormwater network-production validation failed:", file=sys.stderr)
    for error in errors:
        print(f"- {error}", file=sys.stderr)
    raise SystemExit(1)

print("Stormwater network-production source validation passed.")

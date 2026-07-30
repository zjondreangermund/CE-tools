#!/usr/bin/env python3
"""Validate sewer network-production source shape without Autodesk assemblies."""

from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "src" / "CE.Tools.Civil3D"
FILES = {
    "sequence": SRC / "SewerSequenceCommands.cs",
    "alignment": SRC / "SewerBranchAlignmentCommands.cs",
    "production": SRC / "SewerProductionCommands.cs",
    "ribbon": SRC / "PluginEntry.cs",
}

errors: list[str] = []
texts: dict[str, str] = {}
for key, path in FILES.items():
    if not path.exists():
        errors.append(f"Missing sewer source: {path.relative_to(ROOT)}")
        texts[key] = ""
    else:
        texts[key] = path.read_text(encoding="utf-8")

sequence = texts["sequence"]
alignment = texts["alignment"]
production = texts["production"]
ribbon = texts["ribbon"]
combined = sequence + "\n" + alignment + "\n" + production

for command in (
    "CE_SEWTOOLS",
    "CE_SEWSEQ",
    "CE_SEWSEQMAIN",
    "CE_SEWALIGN",
    "CE_SEWREFRESH",
    "CE_SEWFORMAT",
    "CE_SEWPROFILE",
    "CE_SEWSETTINGS",
    "CE_SEWINFO",
):
    if f'"{command}"' not in combined:
        errors.append(f"Sewer command is missing: {command}")

for marker in (
    '"EntireNetwork"',
    '"SelectedPath"',
    '"Branch-"',
    '"MH"',
    '"P"',
    "ApplyTemporaryNames",
    "ApplyBranchNames",
):
    if marker not in sequence:
        errors.append(f"Existing sewer sequence marker is missing: {marker}")

for marker in (
    'RegAppName = "CE_TOOLS_SEWALIGN"',
    "CivilAlignment.Create",
    "BuildBranchPlan",
    "RemoveExistingGeneratedObjects",
):
    if marker not in alignment:
        errors.append(f"Sewer alignment marker is missing: {marker}")

for marker in (
    'ProfileRegAppName = "CE_TOOLS_SEWPROFILE"',
    'SettingsRecordName = "SEWER_PRODUCTION_SETTINGS"',
    '"CE_SEWSEQMAIN"',
    "ExtractBranches",
    "ApplySelectedMainPlan",
    "CreateFromSurface",
    "ProfileView",
    "AddToProfileView",
    "CE_TOOLS_SEWALIGN",
    "SetStyleByReflection",
    "RemoveProfileObjects",
    "Confirm(",
):
    if marker not in production:
        errors.append(f"Sewer production marker is missing: {marker}")

for marker in (
    "CE_TOOLS_SEWER_PRODUCTION_MENU",
    '"CE_SEWTOOLS "',
    '"CE_SEWSEQ "',
    '"CE_SEWSEQMAIN "',
    '"CE_SEWALIGN "',
    '"CE_SEWREFRESH "',
    '"CE_SEWFORMAT "',
    '"CE_SEWPROFILE "',
    '"CE_SEWSETTINGS "',
    '"CE_SEWINFO "',
    "private static RibbonItem[] Row",
    "RibbonMenuItem",
    "source.Items.Add(item)",
):
    if marker not in ribbon:
        errors.append(f"Sewer ribbon marker is missing: {marker}")

for forbidden in (
    "private static RibbonRow Row",
    "private static RibbonButton CreateCommandButton",
):
    if forbidden in ribbon:
        errors.append(f"Incompatible ribbon pattern reintroduced: {forbidden}")

for name, text in texts.items():
    if text.count("{") != text.count("}"):
        errors.append(f"Unbalanced braces in {FILES[name].name}")

if errors:
    print("Sewer network-production validation failed:", file=sys.stderr)
    for error in errors:
        print(f"- {error}", file=sys.stderr)
    raise SystemExit(1)

print("Sewer network-production source validation passed.")

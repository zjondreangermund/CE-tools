#!/usr/bin/env python3
"""Source-shape validation for the CE Tools shared annotation batch.

This runs without Autodesk assemblies. Civil 3D 2023/2024 compilation and
manual drawing validation remain mandatory before release.
"""

from pathlib import Path
import re
import sys

ROOT = Path(__file__).resolve().parents[1]
ANNOTATION = ROOT / "src" / "CE.Tools.Civil3D" / "AnnotationCommands.cs"
RIBBON = ROOT / "src" / "CE.Tools.Civil3D" / "PluginEntry.cs"
ALIASES = ROOT / "src" / "CE.Tools.Civil3D" / "AutoCADTypeAliases.cs"

required_commands = [
    "CE_ANNOTSETTINGS",
    "CE_ALLABELX",
    "CE_PRLABELX",
    "CE_SFLABELX",
    "CE_COORDPICKX",
    "CE_COORDCROSSX",
    "CE_FLLABELX",
    "CE_CORLABELX",
    "CE_PKNUMBERX",
]

errors: list[str] = []
for path in (ANNOTATION, RIBBON, ALIASES):
    if not path.exists():
        errors.append(f"Missing required file: {path.relative_to(ROOT)}")

if errors:
    print("\n".join(errors), file=sys.stderr)
    raise SystemExit(1)

annotation_text = ANNOTATION.read_text(encoding="utf-8")
ribbon_text = RIBBON.read_text(encoding="utf-8")
alias_text = ALIASES.read_text(encoding="utf-8")

for command in required_commands:
    if f'"{command}"' not in annotation_text:
        errors.append(f"Annotation command is not declared: {command}")
    if f'"{command} "' not in ribbon_text:
        errors.append(f"Ribbon is not linked to annotation command: {command}")

for marker in (
    'RootDictionaryName = "CE_TOOLS"',
    'RecordName = "ANNOTATION_SETTINGS"',
    "AnnotationOutput.MLeader",
    "AnnotationOutput.MText",
    "AnnotationOutput.Cogo",
    "new Circle(",
    "CivilApplication.ActiveDocument",
    "CogoPoints.Add",
    "SetRawDescription",
):
    if marker not in annotation_text:
        errors.append(f"Shared annotation implementation is missing: {marker}")

for allowed_height in ("return 1.8", "return 2.0", "return 5.0"):
    if allowed_height not in annotation_text:
        errors.append(f"Discrete annotation height is missing: {allowed_height}")

if re.search(r"TextHeight\s*=\s*(2500|5000)(?:\.0)?\b", annotation_text):
    errors.append("Oversized 2500/5000 annotation text height was introduced")

if "global using Entity = Autodesk.AutoCAD.DatabaseServices.Entity;" not in alias_text:
    errors.append("Explicit AutoCAD Entity alias is missing")
if "global using Baseline = Autodesk.Civil.DatabaseServices.Baseline;" not in alias_text:
    errors.append("Explicit Civil Baseline alias is missing")

for name, text in (
    ("AnnotationCommands.cs", annotation_text),
    ("PluginEntry.cs", ribbon_text),
):
    if text.count("{") != text.count("}"):
        errors.append(f"Unbalanced braces detected in {name}")

preserved_commands = [
    "CE_BMVERT ",
    "CE_TLENGTH ",
    "CE_TAREA ",
    "CE_CORREBUILD ",
    "CE_FLRAISE ",
    "CE_COORDPOLY ",
]
for command in preserved_commands:
    if command not in ribbon_text:
        errors.append(f"Existing working ribbon command was removed: {command.strip()}")

if errors:
    print("CE Tools shared annotation validation failed:", file=sys.stderr)
    for error in errors:
        print(f"- {error}", file=sys.stderr)
    raise SystemExit(1)

print("CE Tools shared annotation source validation passed.")

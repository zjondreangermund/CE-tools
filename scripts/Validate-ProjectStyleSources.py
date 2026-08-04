#!/usr/bin/env python3
"""Validate bundled owner-supplied Civil 3D style sources and import wiring."""

from hashlib import sha256
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
SOURCE_ROOT = (
    ROOT
    / "bundle"
    / "CE Tools.bundle"
    / "Contents"
    / "Resources"
    / "ProjectStyles"
)
COMMANDS = ROOT / "src" / "CE.Tools.Civil3D" / "ProjectStyleCenterCommands.cs"
RIBBON = ROOT / "src" / "CE.Tools.Civil3D" / "PluginEntry.cs"
SETTINGS = ROOT / "src" / "CE.Tools.Civil3D" / "SettingsCenterCommands.cs"

EXPECTED = {
    "CE-Project-Styles-01.dwg": "da5f10aa0c362fd6102843ee798bf5f300539f200fb95f119bc5283d7a2fc44a",
    "CE-Project-Styles-02.dwg": "db11f756fed41c212521b155fc5cff612520da73ea01f8da845ae00b1590f212",
    "CE-Project-Styles-03.dwg": "6d3f658d532522f045f7bc7fe4344744329bc5cac88c2f40ef57b1361752baa2",
}

errors: list[str] = []
for name, expected_hash in EXPECTED.items():
    path = SOURCE_ROOT / name
    if not path.exists():
        errors.append(f"Missing bundled style source: {path.relative_to(ROOT)}")
        continue
    data = path.read_bytes()
    if len(data) < 1_000_000:
        errors.append(f"Bundled style source is unexpectedly small: {name}")
    if not data.startswith(b"AC1032"):
        errors.append(f"Bundled style source is not an AutoCAD 2018-format DWG: {name}")
    actual_hash = sha256(data).hexdigest()
    if actual_hash != expected_hash:
        errors.append(
            f"Bundled style source checksum mismatch: {name} ({actual_hash})"
        )

for path in (COMMANDS, RIBBON, SETTINGS):
    if not path.exists():
        errors.append(f"Missing source file: {path.relative_to(ROOT)}")

commands = COMMANDS.read_text(encoding="utf-8") if COMMANDS.exists() else ""
ribbon = RIBBON.read_text(encoding="utf-8") if RIBBON.exists() else ""
settings = SETTINGS.read_text(encoding="utf-8") if SETTINGS.exists() else ""

for marker in (
    '"CE_PROJECTSTYLEIMPORT"',
    "CivilDocument.GetCivilDocument(source)",
    "StyleBase.ExportTo(",
    "StyleConflictResolverType.Override",
    "StyleConflictResolverType.Rename",
    '"All supplied CE style sources (01 to 03)"',
    "foreach (string sourcePath in sourcePaths)",
    '"Resources",\n                "ProjectStyles"',
    "Import Source Styles...",
):
    if marker not in commands:
        errors.append(f"Project style import source is missing marker: {marker}")

if 'Cmd("Import Source Styles", "CE_PROJECTSTYLEIMPORT "' not in ribbon:
    errors.append("Project style import is not wired into the CE Tools ribbon")
if '"CE_PROJECTSTYLEIMPORT"' not in settings:
    errors.append("Project style import is not wired into Settings Centre")

for name, text in (
    (COMMANDS.name, commands),
    (RIBBON.name, ribbon),
    (SETTINGS.name, settings),
):
    if text.count("{") != text.count("}"):
        errors.append(f"Unbalanced braces detected in {name}")
    if text.count("(") != text.count(")"):
        errors.append(f"Unbalanced parentheses detected in {name}")

if errors:
    print("CE Tools project-style source validation failed:", file=sys.stderr)
    for error in errors:
        print(f"- {error}", file=sys.stderr)
    raise SystemExit(1)

print(
    "CE Tools project-style sources passed: 3 verified DWGs and supported "
    "StyleBase.ExportTo wiring are present."
)

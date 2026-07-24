#!/usr/bin/env python3
"""Audit AutoCAD command declarations across CE Tools Civil 3D sources.

This validator deliberately parses source text instead of loading Autodesk
assemblies, which are unavailable in GitHub Actions. It catches accidental
command-name collisions before the plugin is loaded into Civil 3D.
"""

from __future__ import annotations

from collections import defaultdict
from pathlib import Path
import re
import sys

ROOT = Path(__file__).resolve().parents[1]
SOURCE_DIR = ROOT / "src" / "CE.Tools.Civil3D"

COMMAND_PATTERN = re.compile(
    r"\[\s*CommandMethod\s*\((?P<arguments>.*?)\)\s*\]",
    re.IGNORECASE | re.DOTALL,
)
STRING_PATTERN = re.compile(r'"((?:[^"\\]|\\.)*)"')

# CommandMethod overloads in this repository use either:
#   [CommandMethod("COMMAND", flags)]
# or
#   [CommandMethod("GROUP", "COMMAND", flags)]
# The last quoted argument is therefore the global command name.
commands: dict[str, list[tuple[Path, int, str]]] = defaultdict(list)
errors: list[str] = []

if not SOURCE_DIR.exists():
    print(f"Missing source directory: {SOURCE_DIR}", file=sys.stderr)
    raise SystemExit(1)

for path in sorted(SOURCE_DIR.glob("*.cs")):
    text = path.read_text(encoding="utf-8")
    for match in COMMAND_PATTERN.finditer(text):
        strings = STRING_PATTERN.findall(match.group("arguments"))
        if not strings:
            continue
        command = bytes(strings[-1], "utf-8").decode("unicode_escape").strip()
        if not command:
            errors.append(f"Empty CommandMethod name in {path.relative_to(ROOT)}")
            continue
        line = text.count("\n", 0, match.start()) + 1
        commands[command.upper()].append((path, line, command))

for command, declarations in sorted(commands.items()):
    if len(declarations) <= 1:
        continue
    locations = ", ".join(
        f"{path.relative_to(ROOT)}:{line}" for path, line, _ in declarations
    )
    errors.append(f"Duplicate AutoCAD command '{command}': {locations}")

required_commands = {
    "CE_BMVERT",
    "CE_TLENGTH",
    "CE_TAREA",
    "CE_PROJECTSETUP",
    "CE_ALREPORTUI",
    "CE_ANNOTSETTINGS",
    "CE_CORREBUILDX",
    "CE_COORDPOLY2",
    "CE_BOQBUILD",
    "CE_BOQEXPORT",
    "CE_XSCREATE",
    "CE_REPORTFULL",
    "CE_SUMMARYSHEET",
    "CE_DRAWINGBOOK",
    "CE_PROJECTCLOSEOUT",
    "CE_CLIENTBOOK",
    "CE_CLIENTBOOKREFRESH",
    "CE_CLIENTBOOKINFO",
    "CE_CLIENTBOOKINDEX",
}
missing = sorted(required_commands - set(commands))
for command in missing:
    errors.append(f"Required command is missing from the registry: {command}")

if len(commands) < 45:
    errors.append(
        f"Only {len(commands)} command names were discovered; source parsing may have regressed"
    )

if errors:
    print("CE Tools command-registry validation failed:", file=sys.stderr)
    for error in errors:
        print(f"- {error}", file=sys.stderr)
    raise SystemExit(1)

print(f"CE Tools command registry passed: {len(commands)} unique commands discovered.")

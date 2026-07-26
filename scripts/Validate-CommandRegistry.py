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
    "CE_BMVERT", "CE_TLENGTH", "CE_TAREA", "CE_COLOR250", "COLOR250",
    "CE_PROJECTSETUP", "CE_ALREPORTUI", "CE_ANNOTSETTINGS", "CE_CORREBUILDX",
    "CE_COORDPOLY2", "CE_BOQBUILD", "CE_BOQEXPORT", "CE_XSCREATE",
    "CE_REPORTFULL", "CE_SUMMARYSHEET", "CE_DRAWINGBOOK", "CE_PROJECTCLOSEOUT",
    "CE_CLIENTBOOK", "CE_CLIENTBOOKREFRESH", "CE_CLIENTBOOKINFO",
    "CE_CLIENTBOOKINDEX", "CE_DETAILTOOLS", "CE_DETAILSETROOT",
    "CE_DETAILSEARCH", "CE_DETAILINSERT", "CE_DETAILINFO",
    "CE_SWTOOLS", "CE_SWSETTINGS", "CE_SWSEQ", "CE_SWALIGN", "CE_SWREFRESH",
    "CE_SWPROFILE", "CE_SWINFO", "CE_SEWTOOLS", "CE_SEWSEQ", "CE_SEWSEQMAIN",
    "CE_SEWALIGN", "CE_SEWREFRESH", "CE_SEWFORMAT", "CE_SEWPROFILE",
    "CE_SEWSETTINGS", "CE_SEWINFO", "CE_WATERTOOLS", "CE_WATERSETTINGS",
    "CE_WATERSEQ", "CE_WATERALIGN", "CE_WATERREFRESH", "CE_WATERPROFILE",
    "CE_WATERPLACE", "CE_WATERPLACEREFRESH", "CE_WATERINFO", "CE_SURFCTOOLS",
    "CE_SURFCSETTINGS", "CE_SURFAUDIT", "CE_SURFCORRECT", "CE_SURFSIMPLIFY",
    "CE_SURFCRESTORE", "CE_SURFCINFO", "CE_INTTOOLS", "CE_INTSETTINGS",
    "CE_INTCREATE", "CE_INTREFRESH", "CE_INTINFO", "CE_INTDETACH",
    "CE_INTMONITOR", "CE_PKSKTOOLS", "CE_PKSKSETTINGS", "CE_PKSKVALIDATE",
    "CE_PKSKCORRECT", "CE_PKSKCLEAR", "CE_PKSKINFO",
    "CE_DETAILREVIEWTOOLS", "CE_DETAILREVIEWSETTINGS", "CE_DETAILREVIEW",
    "CE_DETAILREVIEWLIB", "CE_DETAILREVIEWREPORT", "CE_DETAILREVIEWINFO",
    "CE_DETAILPARAMTOOLS", "CE_DETAILPARAMSETTINGS", "CE_DETAILPARAMCREATE",
    "CE_DETAILPARAMEDIT", "CE_DETAILPARAMREFRESH", "CE_DETAILPARAMBOQ",
    "CE_DETAILPARAMBOQEXPORT", "CE_DETAILPARAMREVIEW", "CE_DETAILPARAMINFO",
    "CE_DETAILPARAMDETACH", "CE_DETAILPARAMCLEAR", "CE_RIBBONICONS",
    "CE_PROJECTPRESENTATIONTOOLS", "CE_PRESENTATIONPREVIEW", "CE_PRESENTATIONCREATE",
    "CE_ASSETLIBTOOLS", "CE_ASSETLIBSETTINGS", "CE_ASSETCATALOGTEMPLATE",
    "CE_ASSETCATALOGAUDIT", "CE_ASSETSEARCH", "CE_ASSETINSERT",
    "CE_ASSETINFO", "CE_ASSETREVISIONCHECK",
    "CE_SURVEYCOMPARETOOLS", "CE_SURVEYCHANGES", "CE_SURVEYCHANGEEXPORT",
    "CE_SURFSPIKEHOLEFIX", "CE_BLOCKEDITFAST",
}
missing = sorted(required_commands - set(commands))
for command in missing:
    errors.append(f"Required command is missing from the registry: {command}")

if len(commands) < 115:
    errors.append(
        f"Only {len(commands)} command names were discovered; source parsing may have regressed"
    )

if errors:
    print("CE Tools command-registry validation failed:", file=sys.stderr)
    for error in errors:
        print(f"- {error}", file=sys.stderr)
    raise SystemExit(1)

print(f"CE Tools command registry passed: {len(commands)} unique commands discovered.")

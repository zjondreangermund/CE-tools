#!/usr/bin/env python3
"""Validate dynamic-manager startup and annotation-scale synchronization wiring."""

from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "src" / "CE.Tools.Civil3D"
PLUGIN = SRC / "PluginEntry.cs"
SCALE = SRC / "AnnotationScaleSyncCommands.cs"


def require(path: Path, *needles: str) -> str:
    if not path.exists():
        raise SystemExit(f"Missing required file: {path.relative_to(ROOT)}")
    text = path.read_text(encoding="utf-8")
    for needle in needles:
        if needle not in text:
            raise SystemExit(
                f"Missing {needle!r} in {path.relative_to(ROOT)}")
    return text


plugin = require(
    PLUGIN,
    "CommentAutoRefreshManager.Initialize();",
    "ParkingOptionAutoRefreshManager.Initialize();",
    "DynamicSectionUpdateManager.Initialize();",
    "DynamicIntersectionUpdateManager.Initialize();",
    "AnnotationScaleSyncManager.Initialize();",
    "AnnotationScaleSyncManager.Terminate();",
    "ParkingOptionAutoRefreshManager.Terminate();",
    "CommentAutoRefreshManager.Terminate();",
    '"CE_ANNOSCALESYNC "',
)

scale = require(
    SCALE,
    '"CE_ANNOSCALESYNC"',
    'GetSystemVariable("CANNOSCALE")',
    '"ACDB_ANNOTATIONSCALES"',
    '"AddContext"',
    '"Annotative"',
    "DocumentToBeDestroyed",
    "AcApplication.Idle += OnIdle;",
    "Editor.Regen();",
)

all_source = "\n".join(
    path.read_text(encoding="utf-8", errors="ignore")
    for path in SRC.glob("*.cs")
)
commands = set()
for match in re.finditer(r"\[CommandMethod\((.*?)\)\]", all_source, re.S):
    strings = re.findall(r'"([A-Za-z_][A-Za-z0-9_]*)"', match.group(1))
    if strings:
        commands.add(strings[-1].upper())

ribbon_commands = {
    value.upper()
    for value in re.findall(
        r'Cmd\(\s*"(?:[^"\\]|\\.)*"\s*,\s*"([A-Za-z_][A-Za-z0-9_]*)\s',
        plugin,
        re.S,
    )
}
missing = sorted(ribbon_commands - commands)
if missing:
    raise SystemExit(
        "Ribbon commands without CommandMethod registrations: " +
        ", ".join(missing))

if scale.count("{") != scale.count("}"):
    raise SystemExit("AnnotationScaleSyncCommands.cs has unbalanced braces")

print(
    f"Dynamic integrations validated; "
    f"{len(ribbon_commands)} ribbon commands resolve.")

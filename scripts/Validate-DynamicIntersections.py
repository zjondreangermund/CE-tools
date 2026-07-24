#!/usr/bin/env python3
"""Source-shape validation for linked dynamic intersections."""

from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "src" / "CE.Tools.Civil3D" / "DynamicIntersectionCommands.cs"
PLUGIN = ROOT / "src" / "CE.Tools.Civil3D" / "PluginEntry.cs"

errors: list[str] = []
for path in (SOURCE, PLUGIN):
    if not path.exists():
        errors.append(f"Missing required source: {path.relative_to(ROOT)}")

if not errors:
    source = SOURCE.read_text(encoding="utf-8")
    plugin = PLUGIN.read_text(encoding="utf-8")

    commands = (
        "CE_INTTOOLS",
        "CE_INTSETTINGS",
        "CE_INTCREATE",
        "CE_INTREFRESH",
        "CE_INTINFO",
        "CE_INTDETACH",
        "CE_INTMONITOR",
    )
    for command in commands:
        if f'"{command}"' not in source:
            errors.append(f"DynamicIntersectionCommands.cs missing command: {command}")
        if f'"{command} "' not in plugin:
            errors.append(f"PluginEntry.cs missing ribbon command: {command}")

    source_markers = (
        "CE_DYNAMIC_INTERSECTION_SET",
        "CE_DYNAMIC_INTERSECTION_GENERATED",
        "DYNAMIC_INTERSECTION_SETTINGS",
        "ExtractIntersections",
        "TryIntersectSegments",
        "ReadFeatureLinePoints",
        "CollectCorridorPaths",
        "CorridorCodeFilter",
        "ElevationWarning",
        "GenerateOutput",
        "BuildRegister",
        "RefreshLinkedSet",
        "FindLinkedAnchors",
        "Missing source handle",
        "Source/design changes are refreshed",
        "DynamicIntersectionUpdateManager",
        "ObjectModified",
        "ObjectAppended",
        "Application.Idle",
        "IsQuiescent",
        "LockDocument",
        "Sources will remain unchanged",
    )
    for marker in source_markers:
        if marker not in source:
            errors.append(f"DynamicIntersectionCommands.cs missing marker: {marker}")

    ribbon_markers = (
        "CE_TOOLS_DYNAMIC_INTERSECTION_MENU",
        "Dynamic\\nIntersections",
        "Create Linked Intersection Set",
        "Refresh Linked Intersection Set",
        "Detach Intersection Set",
    )
    for marker in ribbon_markers:
        if marker not in plugin:
            errors.append(f"PluginEntry.cs missing dynamic-intersection ribbon marker: {marker}")

    for marker in (
        "DynamicIntersectionUpdateManager.Initialize();",
        "DynamicIntersectionUpdateManager.Terminate();",
    ):
        if marker not in plugin:
            errors.append(f"PluginEntry.cs missing monitor lifecycle marker: {marker}")

    unsafe_claims = (
        "native autodesk intersection object",
        "automatically approved",
        "no engineering review required",
        "guaranteed collision free",
    )
    lower_source = source.lower()
    for claim in unsafe_claims:
        if claim in lower_source:
            errors.append(f"Dynamic intersection source contains unsafe claim: {claim}")

    for path, text in ((SOURCE, source), (PLUGIN, plugin)):
        if text.count("{") != text.count("}"):
            errors.append(f"Unbalanced braces in {path.name}")
        if text.count("(") != text.count(")"):
            errors.append(f"Unbalanced parentheses in {path.name}")

if errors:
    print("Dynamic intersection source validation failed:", file=sys.stderr)
    for error in errors:
        print(f"- {error}", file=sys.stderr)
    raise SystemExit(1)

print("Dynamic intersection source validation passed.")

#!/usr/bin/env python3
"""Source-shape validation for the Civil 3D ribbon and details follow-up."""

from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
PLUGIN = ROOT / "src" / "CE.Tools.Civil3D" / "PluginEntry.cs"
COLOUR = ROOT / "src" / "CE.Tools.Civil3D" / "ColourCommands.cs"
DETAILS = ROOT / "src" / "CE.Tools.Civil3D" / "TypicalDetailsCommands.cs"

errors: list[str] = []
for path in (PLUGIN, COLOUR, DETAILS):
    if not path.exists():
        errors.append(f"Missing required source: {path.relative_to(ROOT)}")

if not errors:
    plugin = PLUGIN.read_text(encoding="utf-8")
    colour = COLOUR.read_text(encoding="utf-8")
    details = DETAILS.read_text(encoding="utf-8")

    for marker in (
        "private static RibbonItem[] Row",
        "params RibbonItem[][] rows",
        "source.Items.Add(item)",
        "private static RibbonMenuItem CreateCommandMenuItem",
        "var menuItem = parameter as RibbonMenuItem",
        "CE Tools ribbon error:",
        "CE_TOOLS_TYPICAL_DETAILS_MENU",
        "CE_DETAILINSERT ",
    ):
        if marker not in plugin:
            errors.append(f"PluginEntry.cs missing compatibility marker: {marker}")

    for forbidden in (
        "private static RibbonRow Row",
        "private static RibbonButton CreateCommandButton",
        "menu.Items.Add(new RibbonButton",
    ):
        if forbidden in plugin:
            errors.append(f"PluginEntry.cs retained incompatible ribbon pattern: {forbidden}")

    for marker in (
        '"GeometryOnly"',
        '"IncludeAnnotation"',
        "IsAnnotationEntity",
        "ApplyAnnotationColourOverrides",
        "ChangeBlockAttributes",
        "DimensionLineColor",
        "ExtensionLineColor",
        '"Dimclrd"',
        '"Dimclre"',
        '"Dimclrt"',
        "<IncludeAnnotation>",
        "modeResult.Status == PromptStatus.None",
        "LeaderLineColor",
        "Civil 3D label components",
    ):
        if marker not in colour:
            errors.append(f"ColourCommands.cs missing marker: {marker}")

    for command in (
        "CE_DETAILTOOLS",
        "CE_DETAILSETROOT",
        "CE_DETAILSEARCH",
        "CE_DETAILINSERT",
        "CE_DETAILINFO",
    ):
        if f'"{command}"' not in details:
            errors.append(f"TypicalDetailsCommands.cs missing command: {command}")

    for category in (
        "Roadworks",
        "Stormwater",
        "Sewer",
        "Water",
        "Earthworks",
        "Parking",
        "Landscaping",
        "Structures",
        "Standard Construction Notes",
        "General Details",
    ):
        if f'"{category}"' not in details:
            errors.append(f"Typical-details category missing: {category}")

    for marker in (
        '".dwg"',
        '".dxf"',
        '".pdf"',
        "database.Insert",
        "CE_TYPICAL_DETAIL_LINK",
        "Only office-approved, engineer-reviewed details",
    ):
        if marker not in details:
            errors.append(f"TypicalDetailsCommands.cs missing implementation marker: {marker}")

    for name, text in (
        (PLUGIN.name, plugin),
        (COLOUR.name, colour),
        (DETAILS.name, details),
    ):
        if text.count("{") != text.count("}"):
            errors.append(f"Unbalanced braces in {name}")

if errors:
    print("Ribbon/details follow-up validation failed:", file=sys.stderr)
    for error in errors:
        print(f"- {error}", file=sys.stderr)
    raise SystemExit(1)

print("Ribbon/details follow-up source validation passed.")

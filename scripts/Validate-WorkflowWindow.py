#!/usr/bin/env python3
"""Source-shape checks for the Civil 3D workflow command centre."""

from pathlib import Path
import re
import sys

ROOT = Path(__file__).resolve().parents[1]
WINDOW = ROOT / "src" / "CE.Tools.Civil3D" / "FloatingToolsWindow.cs"
PLUGIN = ROOT / "src" / "CE.Tools.Civil3D" / "PluginEntry.cs"
CATALOGUE = ROOT / "src" / "CE.Tools.Civil3D" / "CommandCatalogueCommands.cs"

errors: list[str] = []
for path in (WINDOW, PLUGIN, CATALOGUE):
    if not path.exists():
        errors.append(f"Missing required file: {path.relative_to(ROOT)}")

if errors:
    print("\n".join(errors), file=sys.stderr)
    raise SystemExit(1)

window = WINDOW.read_text(encoding="utf-8")
plugin = PLUGIN.read_text(encoding="utf-8")
catalogue = CATALOGUE.read_text(encoding="utf-8")

required_window_markers = [
    '"CE_TOOLSPALETTE"',
    '"CE_WORKFLOWS"',
    "ComponentManager.Ribbon as UIElement",
    "args.Key != Key.F",
    "ModifierKeys.Control",
    "OpenAtFirstStartup()",
    "Assembly.GetExecutingAssembly().GetTypes()",
    "typeof(CommandMethodAttribute)",
    'ReadAttributeText(attribute, "GlobalName")',
    "MergeDeclaredCommands(result)",
    '"all", "All", "All CE Tools Commands"',
    'Text = definition.Command.Trim()',
    'available.ToString() + " commands',
    '"general", "General", "General Workflow"',
    '"survey", "Survey", "Survey Workflow"',
    '"roads", "Roads", "Roads Workflow"',
    '"stormwater", "Stormwater", "Stormwater Workflow"',
    '"sewer", "Sewer", "Sewer Workflow"',
    '"water", "Water", "Water Workflow"',
    '"bulkwater", "Bulk Water", "Bulk Water Workflow"',
    '"flood", "Flood", "Flood Workflow"',
]
for marker in required_window_markers:
    if marker not in window:
        errors.append(f"Workflow window is missing: {marker}")

for command in (
    "CE_COORDSYSASSIGN",
    "CE_COORDPICK2",
    "CE_COORDCROSS2",
    "CE_COORDPOLY2",
    "CE_COORDTABLE2",
    "CE_COORDREFRESH",
    "CE_PLDIR",
):
    if f'Step("' not in window or f'"{command}")' not in window:
        errors.append(f"Survey workflow step is missing: {command}")

for command in ("CE_REFRESHALL", "CE_REFRESHSTATUS", "CE_AUTOREFRESH"):
    if f'"{command}")' not in window:
        errors.append(f"General workflow step is missing: {command}")

for marker in (
    "FloatingToolsCommands.Initialize();",
    "FloatingToolsCommands.Terminate();",
    "FloatingToolsCommands.OpenAtFirstStartup();",
):
    if marker not in plugin:
        errors.append(f"Plugin workflow lifecycle is missing: {marker}")

for command in (
    "CE_COMMANDCENTER",
    "CE_COMMANDREPORT",
    "CE_COMMANDAUDIT",
    "CE_COMMANDEXPORT",
    "CE_COMMANDHTML",
    "CE_RIBBONREFRESH",
):
    if f'"{command}"' not in catalogue:
        errors.append(f"Command catalogue implementation is missing: {command}")
    if f'"{command} "' not in plugin:
        errors.append(f"Command catalogue ribbon launcher is missing: {command}")

for marker in (
    "GridReportPresenter.ShowReportAndOfferTable(",
    "File.WriteAllLines(path, lines, new UTF8Encoding(true));",
    "File.WriteAllText(path, html, new UTF8Encoding(true));",
    "RibbonBuilder.EnsureCreated();",
    "FloatingToolsCommands.ReloadWindow();",
):
    if marker not in catalogue:
        errors.append(f"Command catalogue implementation is missing: {marker}")

command_pattern = re.compile(
    r'\[CommandMethod\((.*?)\)\]',
    flags=re.DOTALL,
)
quoted_pattern = re.compile(r'"([^"]+)"')
declared_commands: set[str] = set()
for source in (ROOT / "src" / "CE.Tools.Civil3D").glob("*.cs"):
    for attribute in command_pattern.findall(source.read_text(encoding="utf-8-sig")):
        values = quoted_pattern.findall(attribute)
        if values:
            declared_commands.add(values[-1].upper())
if len(declared_commands) < 397:
    errors.append(
        "The all-command workflow catalogue is backed by fewer than 397 "
        f"declared commands ({len(declared_commands)} found)."
    )

for name, text in (
    (WINDOW.name, window),
    (PLUGIN.name, plugin),
    (CATALOGUE.name, catalogue),
):
    if text.count("{") != text.count("}"):
        errors.append(f"Unbalanced braces detected in {name}")

if errors:
    print("CE Tools workflow-window validation failed:", file=sys.stderr)
    for error in errors:
        print(f"- {error}", file=sys.stderr)
    raise SystemExit(1)

print("CE Tools workflow command-centre validation passed.")

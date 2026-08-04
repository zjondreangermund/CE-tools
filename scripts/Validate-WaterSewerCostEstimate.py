#!/usr/bin/env python3
"""Source-shape checks for linked water/sewer cost estimates."""

from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "src" / "CE.Tools.Civil3D" / "WaterSewerCostEstimateCommands.cs"
PLUGIN = ROOT / "src" / "CE.Tools.Civil3D" / "PluginEntry.cs"

errors: list[str] = []
for path in (SOURCE, PLUGIN):
    if not path.exists():
        errors.append(f"Missing required file: {path.relative_to(ROOT)}")
if errors:
    print("\n".join(errors), file=sys.stderr)
    raise SystemExit(1)

source = SOURCE.read_text(encoding="utf-8")
plugin = PLUGIN.read_text(encoding="utf-8")

for command in (
    "CE_WSCOSTTOOLS",
    "CE_WSCOSTCREATE",
    "CE_WSCOSTREFRESH",
    "CE_WSCOSTINFO",
    "CE_WSCOSTAUTO",
):
    if f'"{command}"' not in source:
        errors.append(f"Cost-estimate command is not declared: {command}")
    if f'"{command} "' not in plugin:
        errors.append(f"Cost-estimate ribbon command is missing: {command}")

for marker in (
    "WaterSewerCostAutoRefreshManager.Initialize();",
    "WaterSewerCostAutoRefreshManager.Terminate();",
):
    if marker not in plugin:
        errors.append(f"Cost-estimate lifecycle is missing: {marker}")

for marker in (
    "DocumentToBeDestroyed += OnDocumentToBeDestroyed",
    "DocumentToBeDestroyed -= OnDocumentToBeDestroyed",
    "document.CommandEnded += OnCommandEnded",
    "document.CommandCancelled += OnCommandEnded",
    "document.CommandFailed += OnCommandEnded",
    "if (!WaterSewerCostEstimateCommands.IsAutomatic(document.Database)) return;",
    "WaterSewerCostEstimateCommands.RefreshAll(document);",
):
    if marker not in source:
        errors.append(f"Safe automatic cost refresh is missing: {marker}")

# Database event handlers must only queue work. Reading the NOD or updating the
# workbook occurs after the command has ended.
modified = source.split("private static void OnObjectModified", 1)[-1].split(
    "private static void OnObjectErased", 1
)[0]
erased = source.split("private static void OnObjectErased", 1)[-1].split(
    "private static void OnCommandEnded", 1
)[0]
for name, handler in (("ObjectModified", modified), ("ObjectErased", erased)):
    if "IsAutomatic(" in handler or "RefreshAll(" in handler:
        errors.append(f"{name} performs refresh work inside a database event")

for name, text in ((SOURCE.name, source), (PLUGIN.name, plugin)):
    if text.count("{") != text.count("}"):
        errors.append(f"Unbalanced braces detected in {name}")

if errors:
    print("CE Tools water/sewer cost-estimate validation failed:", file=sys.stderr)
    for error in errors:
        print(f"- {error}", file=sys.stderr)
    raise SystemExit(1)

print("CE Tools linked water/sewer cost-estimate validation passed.")

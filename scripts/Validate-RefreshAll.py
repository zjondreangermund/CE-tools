#!/usr/bin/env python3
"""Source-shape checks for the shared linked-output refresh commands."""

from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
SOURCE_DIR = ROOT / "src" / "CE.Tools.Civil3D"
REFRESH = SOURCE_DIR / "RefreshAllCommands.cs"
PLUGIN = SOURCE_DIR / "PluginEntry.cs"
WINDOW = SOURCE_DIR / "FloatingToolsWindow.cs"

required_files = {
    "refresh coordinator": REFRESH,
    "ribbon": PLUGIN,
    "workflow window": WINDOW,
    "coordinate tables": SOURCE_DIR / "SurveyCoordinateWorkflowCommands.cs",
    "setting-out schedules": SOURCE_DIR / "SettingOutScheduleCommands.cs",
    "parking labels": SOURCE_DIR / "ParkingNumberLinkCommands.cs",
    "surface comparisons": SOURCE_DIR / "SurfaceComparisonLinkStore.cs",
    "linked BOQs": SOURCE_DIR / "BillOfQuantitiesCommands.cs",
    "cost estimates": SOURCE_DIR / "WaterSewerCostEstimateCommands.cs",
    "dynamic cross sections": SOURCE_DIR / "DynamicCrossSectionCommands.cs",
}
errors: list[str] = []
for description, path in required_files.items():
    if not path.exists():
        errors.append(f"Missing {description} source: {path.relative_to(ROOT)}")

if errors:
    print("\n".join(errors), file=sys.stderr)
    raise SystemExit(1)

texts = {name: path.read_text(encoding="utf-8") for name, path in required_files.items()}
refresh = texts["refresh coordinator"]
plugin = texts["ribbon"]
window = texts["workflow window"]

for command in ("CE_REFRESHALL", "CE_REFRESHSTATUS", "CE_AUTOREFRESH"):
    if f'"{command}"' not in refresh:
        errors.append(f"Shared refresh command is not declared: {command}")
    if f'"{command} "' not in plugin:
        errors.append(f"Shared refresh ribbon entry is missing: {command}")
    if f'"{command}")' not in window:
        errors.append(f"General workflow step is missing: {command}")

refresh_markers = (
    "SurveyCoordinateWorkflowCommands.RefreshAll(document)",
    "SettingOutScheduleCommands.RefreshAll(document)",
    "ParkingNumberLinkCommands.Refresh(document, false)",
    "SurfaceComparisonLinkStore.RefreshAll(document)",
    "BillOfQuantitiesCommands.RefreshAll(document)",
    "WaterSewerCostEstimateCommands.RefreshAll(document)",
    "DynamicCrossSectionCommands.RefreshLinkedSection(",
    "document.Editor.Regen();",
    "Other linked outputs were still processed.",
    "LinkedTableAutoRefreshManager.BeginInternalUpdate();",
    "LinkedTableAutoRefreshManager.EndInternalUpdate();",
)
for marker in refresh_markers:
    if marker not in refresh:
        errors.append(f"Shared refresh coordinator is missing: {marker}")

count_markers = {
    "coordinate tables": "internal static int CountLinkedTables(Database database)",
    "setting-out schedules": "internal static int CountLinkedTables(Database database)",
    "parking labels": "internal static int CountLinkedLabels(Database database)",
    "surface comparisons": "public static int CountLinkedEntities(Database database)",
    "linked BOQs": "internal static int CountLinkedTables(Database database)",
}
for source_name, marker in count_markers.items():
    if marker not in texts[source_name]:
        errors.append(f"Refresh-status count API is missing from {source_name}: {marker}")

for marker in (
    "LinkedTableAutoRefreshManager.Initialize();",
    "LinkedTableAutoRefreshManager.Terminate();",
):
    if marker not in plugin:
        errors.append(f"Linked-table automatic-refresh lifecycle is missing: {marker}")

for marker in (
    "DocumentToBeDestroyed += OnDocumentToBeDestroyed",
    "DocumentToBeDestroyed -= OnDocumentToBeDestroyed",
    "document.Database.ObjectModified += OnObjectChanged",
    "document.Database.ObjectAppended += OnObjectChanged",
    "document.Database.ObjectErased += OnObjectErased",
    "document.CommandEnded += OnCommandEnded",
    "SurveyCoordinateWorkflowCommands.RefreshAll(document);",
    "SettingOutScheduleCommands.RefreshAll(document);",
    "BillOfQuantitiesCommands.RefreshAll(document);",
):
    if marker not in refresh:
        errors.append(f"Safe automatic linked-table refresh is missing: {marker}")

modified_handler = refresh.split("private static void OnObjectChanged", 1)[-1].split(
    "private static void OnObjectErased", 1
)[0]
erased_handler = refresh.split("private static void OnObjectErased", 1)[-1].split(
    "private static void MarkPending", 1
)[0]
for name, handler in (("ObjectModified/ObjectAppended", modified_handler), ("ObjectErased", erased_handler)):
    for unsafe_marker in ("StartTransaction", "RefreshAll("):
        if unsafe_marker in handler:
            errors.append(f"{name} performs unsafe work inside a database event: {unsafe_marker}")

# Commands that can rebuild issue deliverables stay separate because they require
# a deliberate review/confirmation workflow.
for unsafe_command in ("CE_CLIENTBOOK", "CE_PROJECTSUMMARY"):
    if unsafe_command in refresh:
        errors.append(f"Shared refresh unexpectedly invokes issue workflow: {unsafe_command}")

for name, text in texts.items():
    if text.count("{") != text.count("}"):
        errors.append(f"Unbalanced braces detected in {required_files[name].name}")

if errors:
    print("CE Tools shared-refresh validation failed:", file=sys.stderr)
    for error in errors:
        print(f"- {error}", file=sys.stderr)
    raise SystemExit(1)

print("CE Tools shared linked-output refresh validation passed.")

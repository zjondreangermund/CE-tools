from pathlib import Path
import re

root = Path(__file__).resolve().parents[1]
source_root = root / "src/CE.Tools.Civil3D"
feature_lines = (source_root / "August17ProductionFeatureLineCommands.cs").read_text(encoding="utf-8")
ribbon = (source_root / "PluginEntry.cs").read_text(encoding="utf-8")
road_menu = (source_root / "RoadProductionCommentCommands.cs").read_text(encoding="utf-8")
field_menu = (source_root / "September11FieldCompletionMenu.cs").read_text(encoding="utf-8")

required = [
    '"CE_CORRIDORFEATURELINES"',
    'settings.AddText("ExactCodes"',
    'settings.Text("ExactCodes")',
    'ObjectId exportSiteId = ResolveExportSite()',
    'line.ExportAsGradingFeatureLine(exportSiteId, dynamic)',
    '"CE-CORRIDOR-FEATURE-LINES"',
    'ClassifyCode(code)',
    'GridReportPresenter.ShowReportAndOfferTable(',
    '"Corridor", "Baseline", "Point Code", "Group", "Exported Feature Line", "Handle", "Dynamic", "Status"',
    '"CE TOOLS CORRIDOR FEATURE LINES"',
]
for token in required:
    if token not in feature_lines:
        raise SystemExit(f"Corridor feature-line completion marker missing: {token}")

command = re.search(
    r'\[CommandMethod\("CE_TOOLS", "CE_CORRIDORFEATURELINES".*?'
    r'(?=\n\s*\[CommandMethod\("CE_TOOLS", "CE_PLATFORMFEATURELINESLOPE")',
    feature_lines,
    re.DOTALL,
)
if not command:
    raise SystemExit("Could not isolate CE_CORRIDORFEATURELINES.")
for forbidden in ["PromptStringOptions", "GetString(", ".Erase(", "corridor.UpgradeOpen()"]:
    if forbidden in command.group(0):
        raise SystemExit(f"Corridor feature-line workflow regressed: {forbidden}")

registration = re.compile(r'\[CommandMethod\(\s*"CE_TOOLS"\s*,\s*"CE_CORRIDORFEATURELINES"')
owners = []
for path in source_root.glob("*.cs"):
    owners.extend([path.name] * len(registration.findall(path.read_text(encoding="utf-8"))))
if owners != ["August17ProductionFeatureLineCommands.cs"]:
    raise SystemExit(f"CE_CORRIDORFEATURELINES must have one canonical owner; found {owners}")

menu_markers = [
    (ribbon, 'Cmd("Extract Corridor Feature Lines", "CE_CORRIDORFEATURELINES "'),
    (road_menu, 'RoadAction("Extract corridor feature lines", "CE_CORRIDORFEATURELINES"'),
    (field_menu, '"Corridor Feature Lines - Select Codes"'),
    (field_menu, '"CE_CORRIDORFEATURELINES"'),
]
for text, token in menu_markers:
    if token not in text:
        raise SystemExit(f"Corridor feature-line menu marker missing: {token}")

print("September 15 corridor feature-line completion regression checks passed.")

from pathlib import Path
import re

root = Path(__file__).resolve().parents[1]
source_root = root / "src/CE.Tools.Civil3D"
annotations = (source_root / "AnnotationCommands.cs").read_text(encoding="utf-8")
links = (source_root / "CorridorAnnotationLinkStore.cs").read_text(encoding="utf-8")
ribbon = (source_root / "PluginEntry.cs").read_text(encoding="utf-8")
road_menu = (source_root / "RoadProductionCommentCommands.cs").read_text(encoding="utf-8")
field_menu = (source_root / "September11FieldCompletionMenu.cs").read_text(encoding="utf-8")

command = re.search(
    r'\[CommandMethod\("CE_TOOLS", "CE_CORLABELX".*?'
    r'(?=\n\s*\[CommandMethod\(\s*"CE_TOOLS",\s*"CE_PKNUMBERX")',
    annotations,
    re.DOTALL,
)
if not command:
    raise SystemExit("Could not isolate CE_CORLABELX.")

required_command = [
    "AnnotationSettingsStore.Prepare(document, true, out settings)",
    "var generatedIds = new List<ObjectId>();",
    "plainDescription,",
    "settings,\n                true,\n                generatedIds",
    "CorridorAnnotationLinkStore.Link(",
    "ObjectId.Null,",
    '"Corridor reference"',
    "CorridorAnnotationLinkStore.RefreshAll(document);",
    "settings.TextHeight",
    "settings.DrawMarker",
]
for token in required_command:
    if token not in command.group(0):
        raise SystemExit(f"Dynamic corridor annotation marker missing: {token}")
for forbidden in ["corridor.UpgradeOpen()", ".Erase("]:
    if forbidden in command.group(0):
        raise SystemExit(f"Corridor annotation became destructive: {forbidden}")

required_shared_settings = [
    'model.AddPaperHeight(',
    '"Standard choices are 1.8, 2.0, 2.5, 3.5 and 5.0 mm."',
    'allowCogo\n                    ? new[] { "MLeader", "MText", "COGO" }',
    'var marker = new Circle(',
    'leader.AddFirstVertex(leaderLineIndex, target);',
]
for token in required_shared_settings:
    if token not in annotations:
        raise SystemExit(f"Shared corridor annotation presentation marker missing: {token}")

required_links = [
    "if (database == null || corridorId.IsNull || outputIds == null) return;",
    "if (!sourcePointId.IsNull)",
    "sourcePointId.IsNull\n                ? storedPoint\n                : ReadPoint(transaction, sourcePointId)",
    '"Source=" + (sourcePointId.IsNull ? string.Empty : sourcePointId.Handle.ToString())',
    "ObjectId sourceId = ObjectId.Null;",
    "(!string.IsNullOrWhiteSpace(sourceText) && !Resolve(database, sourceText, out sourceId))",
]
for token in required_links:
    if token not in links:
        raise SystemExit(f"Static-reference corridor link marker missing: {token}")

for text, token in [
    (ribbon, 'Cmd("Dynamic Corridor Annotation", "CE_CORLABELX "'),
    (road_menu, 'RoadAction("Dynamic corridor annotation", "CE_CORLABELX"'),
    (field_menu, '"Dynamic Corridor Annotation"'),
    (field_menu, '"CE_CORLABELX"'),
]:
    if token not in text:
        raise SystemExit(f"Corridor annotation menu marker missing: {token}")

print("September 15 corridor annotation completion regression checks passed.")

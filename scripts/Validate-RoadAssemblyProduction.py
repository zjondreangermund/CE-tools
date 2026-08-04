from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "src" / "CE.Tools.Civil3D"

dialogs = (SRC / "DisciplineWorkflowDialogs.cs").read_text(encoding="utf-8")
overlap = (SRC / "CommentPresentationCommands.cs").read_text(encoding="utf-8")
sewer = (SRC / "SewerProductionCommands.cs").read_text(encoding="utf-8")
assembly = (SRC / "CeAssemblyCommands.cs").read_text(encoding="utf-8")
roads = (SRC / "RoadProductionCommentCommands.cs").read_text(encoding="utf-8")
ribbon = (SRC / "PluginEntry.cs").read_text(encoding="utf-8")

required = {
    "reflection import": "using System.Reflection;" in dialogs,
    "AutoCAD 2023 zero vector": "PlacementCandidate.From(original, new Vector3d(0.0, 0.0, 0.0))" in overlap,
    "sewer pipe write-open": "transaction.GetObject(pipeId, OpenMode.ForWrite, false)" in sewer,
    "sewer structure write-open": "transaction.GetObject(structureId, OpenMode.ForWrite, false)" in sewer,
    "assembly workflow command": '"CE_ASSEMBLYTOOLS"' in assembly,
    "assembly create command": '"CE_ASSEMBLYCREATE"' in assembly,
    "assembly report command": '"CE_ASSEMBLYREPORT"' in assembly,
    "road assembly recovery": "CreateRoadAssemblyInteractively" in roads,
    "road ordered workflow": "new List<DisciplineWorkflowAction>" in roads,
    "assembly ribbon panel": '"CE-ASSEMBLY"' in ribbon,
    "road production ribbon panel": '"ROAD PRODUCTION"' in ribbon,
}

missing = [name for name, present in required.items() if not present]
if missing:
    raise SystemExit("Road/assembly production validation failed: " + ", ".join(missing))

print("CE Tools road/assembly production and sewer profile write-state validation passed.")

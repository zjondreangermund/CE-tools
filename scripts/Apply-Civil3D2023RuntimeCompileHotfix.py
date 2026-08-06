#!/usr/bin/env python3
"""Apply Civil 3D 2023 compile fixes found by the Windows installer build."""

from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "src" / "CE.Tools.Civil3D"


def replace_once(path: Path, old: str, new: str, label: str) -> None:
    text = path.read_text(encoding="utf-8")
    if new in text:
        return
    if old not in text:
        raise SystemExit(f"Could not apply {label} in {path.name}.")
    path.write_text(text.replace(old, new, 1), encoding="utf-8")


runtime = SRC / "PreBuildRuntimeCompletionCommands.cs"
replace_once(
    runtime,
    '''            KeepLabelNearFeature(
                label.Database,
                transaction,
                label,
                featureId,
                pipe);''',
    '''            DBObject databaseObject = label as DBObject;
            if (databaseObject == null) return;
            KeepLabelNearFeature(
                databaseObject.Database,
                transaction,
                label,
                featureId,
                pipe);''',
    "DBObject database access",
)
replace_once(
    runtime,
    '''            foreach (ObjectId id in network.GetPipeIds()
                .Concat(network.GetStructureIds()))''',
    '''            var partIds = new List<ObjectId>();
            foreach (ObjectId id in network.GetPipeIds()) partIds.Add(id);
            foreach (ObjectId id in network.GetStructureIds()) partIds.Add(id);
            foreach (ObjectId id in partIds)''',
    "ObjectIdCollection concatenation",
)

sequence = SRC / "SewerNetworkDynamicSequenceManager.cs"
replace_once(
    sequence,
    '''            foreach (ObjectId pipeId in graph.Pipes.Keys.OrderBy(id => id.Handle.Value))''',
    '''            var pipeIds = new List<ObjectId>();
            foreach (ObjectId pipeId in network.GetPipeIds()) pipeIds.Add(pipeId);
            foreach (ObjectId pipeId in pipeIds.OrderBy(id => id.Handle.Value))''',
    "temporary sewer pipe enumeration",
)
replace_once(
    sequence,
    '''            foreach (ObjectId structureId in graph.Structures.Keys.OrderBy(id => id.Handle.Value))''',
    '''            var structureIds = new List<ObjectId>();
            foreach (ObjectId structureId in network.GetStructureIds()) structureIds.Add(structureId);
            foreach (ObjectId structureId in structureIds.OrderBy(id => id.Handle.Value))''',
    "temporary sewer structure enumeration",
)

print("Civil 3D 2023 runtime completion compile hotfix applied.")

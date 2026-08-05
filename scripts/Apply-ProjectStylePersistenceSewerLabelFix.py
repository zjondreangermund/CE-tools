#!/usr/bin/env python3
"""Wire project-style presets and sewer-label synchronization into CE Tools."""

from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PLUGIN = ROOT / "src" / "CE.Tools.Civil3D" / "PluginEntry.cs"
PROJECT = ROOT / "src" / "CE.Tools.Civil3D" / "ProjectStyleCenterCommands.cs"
LABELS = ROOT / "src" / "CE.Tools.Civil3D" / "SewerNetworkLabelCommands.cs"


def replace_once(path: Path, old: str, new: str) -> None:
    text = path.read_text(encoding="utf-8-sig")
    if new in text:
        return
    if old not in text:
        raise SystemExit(f"Expected integration marker was not found in {path}")
    path.write_text(text.replace(old, new, 1), encoding="utf-8")


replace_once(
    PLUGIN,
    "            FloatingToolsCommands.Initialize();\n            AcApplication.Idle += OnApplicationIdle;",
    "            FloatingToolsCommands.Initialize();\n"
    "            ProjectStylePresetManager.Initialize();\n"
    "            AcApplication.Idle += OnApplicationIdle;",
)
replace_once(
    PLUGIN,
    "            AcApplication.Idle -= OnApplicationIdle;\n            FloatingToolsCommands.Terminate();",
    "            AcApplication.Idle -= OnApplicationIdle;\n"
    "            ProjectStylePresetManager.Terminate();\n"
    "            FloatingToolsCommands.Terminate();",
)
replace_once(
    PROJECT,
    "                WriteSelection(document.Database, selection);\n                document.Editor.WriteMessage(",
    "                WriteSelection(document.Database, selection);\n"
    "                ProjectStylePresetManager.SaveFromDrawing(document);\n"
    "                document.Editor.WriteMessage(",
)
replace_once(
    LABELS,
    "            try\n            {\n                return EnsureLabelsCore(document, networkIds);\n            }",
    "            try\n            {\n"
    "                SewerNetworkLabelResult result = EnsureLabelsCore(\n"
    "                    document, networkIds);\n"
    "                SewerLabelStyleSyncCommands.ApplySelectedStyles(document);\n"
    "                return result;\n"
    "            }",
)

print("Applied project-style persistence and sewer-label synchronization wiring.")

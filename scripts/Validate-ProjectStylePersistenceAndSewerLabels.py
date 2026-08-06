#!/usr/bin/env python3
"""Validate cross-drawing style presets, sewer label sync and safe reset."""

from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
CIVIL = ROOT / "src" / "CE.Tools.Civil3D"
PLUGIN = CIVIL / "PluginEntry.cs"
PROJECT = CIVIL / "ProjectStyleCenterCommands.cs"
PRESETS = CIVIL / "ProjectStylePresetManager.cs"
LABEL_COMMANDS = CIVIL / "SewerNetworkLabelCommands.cs"
LABEL_SYNC = CIVIL / "SewerLabelStyleSyncCommands.cs"
RESET = CIVIL / "DrawingResetCommands.cs"
WORKFLOW = ROOT / ".github" / "workflows" / "core-tests.yml"

errors: list[str] = []
for path in (PLUGIN, PROJECT, PRESETS, LABEL_COMMANDS, LABEL_SYNC, RESET, WORKFLOW):
    if not path.exists():
        errors.append(f"Missing required file: {path.relative_to(ROOT)}")

texts = {
    path: path.read_text(encoding="utf-8-sig") if path.exists() else ""
    for path in (PLUGIN, PROJECT, PRESETS, LABEL_COMMANDS, LABEL_SYNC, RESET, WORKFLOW)
}

required = {
    PLUGIN: (
        "ProjectStylePresetManager.Initialize();",
        "ProjectStylePresetManager.Terminate();",
    ),
    PROJECT: (
        "ProjectStylePresetManager.SaveFromDrawing(document);",
    ),
    PRESETS: (
        '"CE_PROJECTSTYLESAVE"',
        '"CE_PROJECTSTYLEAPPLY"',
        '"CE_PROJECTSTYLEOPENPROMPT"',
        "Keep existing drawing project styles",
        "Use saved project styles",
        "DocumentCreated += OnDocumentCreated",
        "DocumentActivated += OnDocumentActivated",
        "PROJECT_STYLE_SELECTION_",
        "SynchronizeDisciplineSettings",
        "document.LockDocument()",
        'GetSystemVariable("CMDNAMES")',
        'GetSystemVariable("CMDACTIVE")',
    ),
    LABEL_COMMANDS: (
        "SewerLabelStyleSyncCommands.ApplySelectedStyles(document);",
    ),
    LABEL_SYNC: (
        '"CE_SEWLABELSYNC"',
        '"CE_SEWLABELCLEAN"',
        '"Pipe Label Style"',
        '"Structure Label Style"',
        '"Pipe Style"',
        '"Structure Style"',
        '"StyleId", "LabelStyleId"',
        '"LabelLocation", "Location"',
        "ResolveOverlaps(document)",
        "ApplyPipeLabelPresentation(value, transaction);",
        "SewerPlanLabelRuntimeManager.ConfigureLabel(label, transaction);",
    ),
    RESET: (
        '"CE_DRAWINGRESETALL"',
        '"DELETE ALL"',
        "CreateBackup(database, backupPath)",
        '"Wblock"',
        '"DetachXref"',
        "value.Erase(true)",
    ),
    WORKFLOW: (
        "Validate-ProjectStylePersistenceAndSewerLabels.py",
    ),
}

for path, markers in required.items():
    text = texts[path]
    for marker in markers:
        if marker not in text:
            errors.append(f"{path.name} is missing marker: {marker}")

for path in (PLUGIN, PROJECT, PRESETS, LABEL_COMMANDS, LABEL_SYNC, RESET):
    text = texts[path]
    if text.count("{") != text.count("}"):
        errors.append(f"Unbalanced braces in {path.name}")
    if text.count("(") != text.count(")"):
        errors.append(f"Unbalanced parentheses in {path.name}")

if errors:
    print("Project style persistence / sewer label validation failed:", file=sys.stderr)
    for error in errors:
        print(f"- {error}", file=sys.stderr)
    raise SystemExit(1)

print(
    "Project style persistence and sewer labels passed: cross-drawing presets, "
    "quiescent locked preset application, existing-label style synchronization, "
    "selected Civil label presentation, bounded overlap cleanup and protected drawing reset are wired."
)

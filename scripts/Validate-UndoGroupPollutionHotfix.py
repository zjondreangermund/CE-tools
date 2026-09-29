#!/usr/bin/env python3
"""Validate that CE background bookkeeping cannot pollute AutoCAD undo history."""

from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
telemetry = (ROOT / "src" / "CE.Tools.Civil3D" / "CeInteractionTelemetryCommands.cs").read_text(encoding="utf-8")
refresh = (ROOT / "src" / "CE.Tools.Civil3D" / "UniversalDynamicRefreshCommands.cs").read_text(encoding="utf-8")

required_telemetry = [
    "SaveUserProfile(document, state);",
    "document.Database.DisableUndoRecording(true);",
    "document.Database.DisableUndoRecording(false);",
    "causes the Undo dropdown to fill with \"Group of commands\" rows",
]
required_refresh = [
    "_document.CommandWillStart += OnCommandWillStart;",
    "_undoRedoActive = true;",
    "_suppressQueueUntilUtc",
    "DateTime.UtcNow < _suppressQueueUntilUtc",
    "HasCeLink(value)",
    "Do not queue a universal full-model pass after every CE command",
    "RefreshNow(active, true);",
    "bool suppressUndoRecording",
    "DisableUndoRecording(true)",
    "IsUndoRedo(command)",
]

missing = [f"telemetry:{item}" for item in required_telemetry if item not in telemetry]
missing += [f"refresh:{item}" for item in required_refresh if item not in refresh]

finish_start = telemetry.index("        private static void Finish(Document document")
finish_end = telemetry.index("        private static void FinishElapsed", finish_start)
finish_block = telemetry[finish_start:finish_end]
if "Save(document, state);" in finish_block:
    missing.append("telemetry:Finish still writes an Xrecord after every command")

if 'command.StartsWith("CE_", StringComparison.OrdinalIgnoreCase)' in refresh:
    missing.append("refresh:blanket CE command-ended universal refresh returned")
if 'if (value is Entity || value is Xrecord || value is DBDictionary ||' in refresh:
    missing.append("refresh:broad all-entity DBObject refresh trigger returned")

if missing:
    raise SystemExit("Undo group pollution regression failed:\n- " + "\n- ".join(missing))

print("Undo/Redo regression passed: telemetry stays out of command history, post-Undo events are suppressed, only CE-linked objects queue universal refresh, and idle refresh is excluded from AutoCAD undo recording.")

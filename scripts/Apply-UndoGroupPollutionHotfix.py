#!/usr/bin/env python3
"""Apply the CE Tools AutoCAD undo-stack pollution hotfix."""

from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
TELEMETRY = ROOT / "src" / "CE.Tools.Civil3D" / "CeInteractionTelemetryCommands.cs"
REFRESH = ROOT / "src" / "CE.Tools.Civil3D" / "UniversalDynamicRefreshCommands.cs"


def replace_once(text: str, old: str, new: str, label: str) -> str:
    if old in text:
        return text.replace(old, new, 1)
    if new in text:
        return text
    raise SystemExit(f"Could not patch {label}.")


telemetry = TELEMETRY.read_text(encoding="utf-8")
telemetry = replace_once(
    telemetry,
    """            state.Dirty = true;\n            Save(document, state);\n        }\n\n        private static void FinishElapsed""",
    """            state.Dirty = true;\n            // Persist routine command activity to the user profile only. Writing\n            // an Xrecord after every command creates a new AutoCAD undo item and\n            // causes the Undo dropdown to fill with \"Group of commands\" rows.\n            SaveUserProfile(document, state);\n        }\n\n        private static void FinishElapsed""",
    "telemetry command completion",
)

save_start = telemetry.index("        private static void Save(Document document, CeDocumentTelemetry state)")
save_end = telemetry.index("        private static void SaveUserProfile", save_start)
old_save = telemetry[save_start:save_end]
new_save = """        private static void Save(Document document, CeDocumentTelemetry state)\n        {\n            if (document == null || state == null || !state.Dirty) return;\n            bool undoRecordingDisabled = false;\n            try\n            {\n                // Explicit telemetry persistence must never create an AutoCAD\n                // undo record. Normal CE design commands remain fully undoable.\n                document.Database.DisableUndoRecording(true);\n                undoRecordingDisabled = true;\n                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())\n                {\n                    DBDictionary named = transaction.GetObject(document.Database.NamedObjectsDictionaryId, OpenMode.ForWrite, false) as DBDictionary;\n                    DBDictionary root;\n                    if (named.Contains(RootName)) root = transaction.GetObject(named.GetAt(RootName), OpenMode.ForWrite, false) as DBDictionary;\n                    else\n                    {\n                        root = new DBDictionary();\n                        named.SetAt(RootName, root);\n                        transaction.AddNewlyCreatedDBObject(root, true);\n                    }\n                    Xrecord record;\n                    if (root.Contains(RecordName)) record = transaction.GetObject(root.GetAt(RecordName), OpenMode.ForWrite, false) as Xrecord;\n                    else\n                    {\n                        record = new Xrecord();\n                        root.SetAt(RecordName, record);\n                        transaction.AddNewlyCreatedDBObject(record, true);\n                    }\n                    record.Data = new ResultBuffer(state.Stats.Values.OrderBy(item => item.Command).Select(item => new TypedValue((int)DxfCode.Text, item.Serialize())).ToArray());\n                    transaction.Commit();\n                }\n                SaveUserProfile(document, state);\n                state.Dirty = false;\n            }\n            catch { }\n            finally\n            {\n                if (undoRecordingDisabled)\n                {\n                    try { document.Database.DisableUndoRecording(false); }\n                    catch { }\n                }\n            }\n        }\n\n"""
if "DisableUndoRecording(true)" not in old_save:
    telemetry = telemetry[:save_start] + new_save + telemetry[save_end:]
TELEMETRY.write_text(telemetry, encoding="utf-8")

refresh = REFRESH.read_text(encoding="utf-8")
refresh = replace_once(
    refresh,
    """        private static bool _pending;\n        private static bool _busy;\n        private static DateTime _lastChangeUtc""",
    """        private static bool _pending;\n        private static bool _busy;\n        private static bool _undoRedoActive;\n        private static DateTime _lastChangeUtc""",
    "refresh undo state",
)
refresh = replace_once(
    refresh,
    """        internal static void Queue()\n        {\n            if (_busy) return;""",
    """        internal static void Queue()\n        {\n            if (_busy || _undoRedoActive) return;""",
    "queue undo guard",
)

refresh_start = refresh.index("        internal static UniversalRefreshResult RefreshNow(Document document)")
refresh_end = refresh.index("        private static void OnDocumentActivated", refresh_start)
old_refresh = refresh[refresh_start:refresh_end]
new_refresh = """        internal static UniversalRefreshResult RefreshNow(Document document)\n        {\n            return RefreshNow(document, false);\n        }\n\n        private static UniversalRefreshResult RefreshNow(\n            Document document,\n            bool suppressUndoRecording)\n        {\n            var result = new UniversalRefreshResult();\n            if (document == null || _busy || _undoRedoActive) return result;\n            bool undoRecordingDisabled = false;\n            _busy = true;\n            try\n            {\n                if (suppressUndoRecording)\n                {\n                    document.Database.DisableUndoRecording(true);\n                    undoRecordingDisabled = true;\n                }\n                try\n                {\n                    LinkedRefreshEngine.Refresh(document, false);\n                    result.LinkedEngineRuns++;\n                }\n                catch { result.Warnings++; }\n                try { result.VertexTables += VertexSettingOutCommands.RefreshAll(document); }\n                catch { result.Warnings++; }\n                try { SurveyCoordinateWorkflowCommands.RefreshAll(document); }\n                catch { result.Warnings++; }\n                try { CogoPointProjectStyleCommands.ApplySelectedStyles(document, true); }\n                catch { result.Warnings++; }\n                try { SewerNetworkDynamicSequenceCommands.ResequenceAll(document, false); }\n                catch { result.Warnings++; }\n                try { result.JunctionLabels += RoadJunctionCompletionCommands.RefreshAll(document); }\n                catch { result.Warnings++; }\n                try { result.MetadataAttributes += ProductionMetadataDynamicManager.Refresh(document); }\n                catch { result.Warnings++; }\n                _pending = false;\n                _lastRefreshUtc = DateTime.UtcNow;\n            }\n            finally\n            {\n                if (undoRecordingDisabled)\n                {\n                    try { document.Database.DisableUndoRecording(false); }\n                    catch { }\n                }\n                _busy = false;\n            }\n            return result;\n        }\n\n"""
if "bool suppressUndoRecording" not in old_refresh:
    refresh = refresh[:refresh_start] + new_refresh + refresh[refresh_end:]

refresh = replace_once(
    refresh,
    """            _document.CommandEnded += OnCommandEnded;\n            _document.CommandCancelled += OnCommandEnded;""",
    """            _document.CommandWillStart += OnCommandWillStart;\n            _document.CommandEnded += OnCommandEnded;\n            _document.CommandCancelled += OnCommandEnded;""",
    "attach undo command listener",
)
refresh = replace_once(
    refresh,
    """                _document.CommandEnded -= OnCommandEnded;\n                _document.CommandCancelled -= OnCommandEnded;""",
    """                _document.CommandWillStart -= OnCommandWillStart;\n                _document.CommandEnded -= OnCommandEnded;\n                _document.CommandCancelled -= OnCommandEnded;""",
    "detach undo command listener",
)
refresh = replace_once(
    refresh,
    """            _database = null;\n            _document = null;\n        }\n\n        private static void OnCommandEnded""",
    """            _database = null;\n            _document = null;\n            _undoRedoActive = false;\n        }\n\n        private static void OnCommandWillStart(object sender, CommandEventArgs e)\n        {\n            if (_busy || e == null) return;\n            string command = NormalizeCommand(e.GlobalCommandName);\n            if (!IsUndoRedo(command)) return;\n            _undoRedoActive = true;\n            _pending = false;\n        }\n\n        private static void OnCommandEnded""",
    "undo start handler",
)
refresh = replace_once(
    refresh,
    """            string command = (e.GlobalCommandName ?? string.Empty).Trim().TrimStart('.', '_');\n            if (command.StartsWith("CE_", StringComparison.OrdinalIgnoreCase) ||""",
    """            string command = NormalizeCommand(e.GlobalCommandName);\n            if (IsUndoRedo(command))\n            {\n                // Object events raised while AutoCAD is undoing must not queue a\n                // background CE refresh, otherwise the refresh becomes a new\n                // undo item immediately after the user's undo.\n                _undoRedoActive = false;\n                _pending = false;\n                _lastChangeUtc = DateTime.UtcNow;\n                return;\n            }\n            if (command.StartsWith("CE_", StringComparison.OrdinalIgnoreCase) ||""",
    "undo end handling",
)
refresh = replace_once(
    refresh,
    """        private static void OnObjectChanged(object sender, ObjectEventArgs e)\n        {\n            if (_busy || e == null || e.DBObject == null) return;""",
    """        private static string NormalizeCommand(string value)\n        {\n            return (value ?? string.Empty).Trim().TrimStart('.', '_').ToUpperInvariant();\n        }\n\n        private static bool IsUndoRedo(string command)\n        {\n            return string.Equals(command, "U", StringComparison.OrdinalIgnoreCase) ||\n                string.Equals(command, "UNDO", StringComparison.OrdinalIgnoreCase) ||\n                string.Equals(command, "REDO", StringComparison.OrdinalIgnoreCase) ||\n                string.Equals(command, "MREDO", StringComparison.OrdinalIgnoreCase) ||\n                command.IndexOf("UNDO", StringComparison.OrdinalIgnoreCase) >= 0 ||\n                command.IndexOf("REDO", StringComparison.OrdinalIgnoreCase) >= 0;\n        }\n\n        private static void OnObjectChanged(object sender, ObjectEventArgs e)\n        {\n            if (_busy || _undoRedoActive || e == null || e.DBObject == null) return;""",
    "object-change undo guard",
)
refresh = replace_once(
    refresh,
    """        private static void OnObjectErased(object sender, ObjectErasedEventArgs e)\n        {\n            if (_busy || e == null || e.DBObject == null) return;""",
    """        private static void OnObjectErased(object sender, ObjectErasedEventArgs e)\n        {\n            if (_busy || _undoRedoActive || e == null || e.DBObject == null) return;""",
    "object-erased undo guard",
)
refresh = replace_once(
    refresh,
    """            if (!Enabled || !_pending || _busy || active == null) return;""",
    """            if (!Enabled || !_pending || _busy || _undoRedoActive || active == null) return;""",
    "idle undo guard",
)
refresh = replace_once(
    refresh,
    """            RefreshNow(active);\n        }\n    }""",
    """            // Idle/background refresh must not create an undo item.\n            RefreshNow(active, true);\n        }\n    }""",
    "automatic non-undo refresh",
)
REFRESH.write_text(refresh, encoding="utf-8")

print("Undo group pollution hotfix applied.")

from pathlib import Path
import re

root = Path(__file__).resolve().parents[1]
source_root = root / "src/CE.Tools.Civil3D"
audit = (source_root / "August24FieldCompletionCommands.cs").read_text(encoding="utf-8")
recalc = (source_root / "September11FieldCompletionCommands.cs").read_text(encoding="utf-8")
runtime = (source_root / "September10SewerAuditRuntime.cs").read_text(encoding="utf-8")
menu = (source_root / "September11FieldCompletionMenu.cs").read_text(encoding="utf-8")
ribbon = (source_root / "PluginEntry.cs").read_text(encoding="utf-8")

for token in [
    'audit.OpenPipes',
    'audit.OpenEndpoints',
    'audit.OpenStructures',
    'audit.TerminalStructures',
    'audit.RuleConfigurationViolations',
    'issues.Add("open start")',
    'issues.Add("open end")',
    'issues.Add("no connected pipes")',
    'pipe.RuleSetStyleId',
    'structure.RuleSetStyleId',
    'pipe.RefSurfaceId',
    'structure.RefSurfaceId',
    '"Connections", "Start Structure"',
    '"Rule Set", "Reference Surface"',
]:
    if token not in audit:
        raise SystemExit(f"Sewer integrity audit marker missing: {token}")

method = re.search(
    r"private static SewerAuditResult AuditNetwork\(.*?\n        \}\n\n        private static void AddEndpoint",
    audit,
    re.DOTALL,
)
if not method:
    raise SystemExit("Could not isolate the read-only sewer integrity audit.")
for forbidden in (
    ".ApplyRules(",
    ".ConnectToStructure(",
    ".UpgradeOpen(",
    ".Erase(",
    "OpenMode.ForWrite",
):
    if forbidden in method.group(0):
        raise SystemExit(f"Read-only sewer integrity audit regressed: {forbidden}")

for token in [
    'out List<IList<string>> reportRows',
    'bool applied = pipe.ApplyRules();',
    'bool applied = structure.ApplyRules();',
    'Civil 3D returned false; review the rule DLL/Event Viewer',
    '"Failed before rule evaluation: "',
]:
    if token not in runtime:
        raise SystemExit(f"Per-part sewer rule result marker missing: {token}")

for token in [
    'GridReportPresenter.ShowReportAndOfferTable(',
    '"CE Tools - Sewer Pipe / Structure Rule Results"',
    '"CE SEWER PIPE / STRUCTURE RULE RESULTS"',
    'out ruleRows',
]:
    if token not in recalc:
        raise SystemExit(f"Sewer recalculation result marker missing: {token}")

for text, label, tokens in [
    (menu, "Field Completion", [
        '"Sewer Integrity Audit - Connections / Rules / Levels"',
        '"Connect Open Sewer Pipe Ends"',
        '"CE_SEWCONNECTPARTS"',
    ]),
    (ribbon, "Sewer ribbon", ['Cmd("Integrity / Engineering Audit", "CE_SEWAUDITLIMITS "']),
]:
    for token in tokens:
        if token not in text:
            raise SystemExit(f"{label} marker missing: {token}")

print("September 15 sewer integrity and per-part rule-result checks passed.")

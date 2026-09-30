#!/usr/bin/env python3
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
source = (ROOT / "src/CE.Tools.Civil3D/SewerRulesBatchCommands.cs").read_text(encoding="utf-8-sig")
workflow = (ROOT / ".github/workflows/core-tests.yml").read_text(encoding="utf-8-sig")

errors = []

for marker in [
    '"Keep sewer pipe slopes downhill by branch"',
    "EnforceRuleProducedDownhillSlopes(",
    "PipeSequencePattern",
    "ApplyRules() is committed separately for each selected part",
    "record.Slope * record.Run",
    "downstreamZ =",
    "upstreamZ - record.Slope * record.Run",
    "MH",
    "TrySetPipePoint(",
    "Civil 3D did not accept the corrected pipe endpoint elevations",
    "downhill pipe grades enforced",
]:
    if marker not in source:
        errors.append("missing batch sewer rule/downhill marker: " + marker)

# Guard against restoring one giant transaction around the complete selected set.
if "foreach (SewerPartSelection selected in parts)" not in source or    "using (Transaction transaction =" not in source:
    errors.append("per-part rule application transaction is missing")

if "Validate-September30ApplyRulesDownhill.py" not in workflow:
    errors.append("core workflow does not run Apply Rules downhill regression")

if errors:
    print("September 30 sewer Apply Rules downhill validation FAILED")
    for error in errors:
        print(" -", error)
    raise SystemExit(1)

print("September 30 sewer Apply Rules downhill validation passed.")

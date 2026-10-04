#!/usr/bin/env python3
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "src" / "CE.Tools.Civil3D"

boq = (SRC / "SewerExcavationCommentCommands.cs").read_text(encoding="utf-8-sig")
ribbon = (SRC / "August11ProductionCentreCommands.cs").read_text(encoding="utf-8-sig")

errors = []

for marker in [
    '"CE_BOQSEWER"',
    '"AllNetworkParts"',
    '"Select"',
    'sourceIds = selection.Value.GetObjectIds().ToList();',
    'ReadAllSupportedSewerParts(document.Database)',
    'grossBlanketZone -\n                    pipeVolume',
    'averageCover -\n                    settings.BlanketAbovePipe',
    '"Pipe volume deducted from blanket fill (m³)"',
    '"Blanket fill, net of pipe volume (m³)"',
    'public string BranchName { get; set; }',
    'ResolveBranchName(',
    'OrderRowsByBranch(',
    'branch.ToUpperInvariant()',
    'BranchSortNumber(',
]:
    if marker not in boq:
        errors.append("Missing sewer BOQ marker: " + marker)

# The fill above blanket calculation must not subtract pipe volume.
fill_anchor = boq.find("double fillAboveBlanket")
if fill_anchor < 0:
    errors.append("Missing fillAboveBlanket calculation.")
else:
    next_anchor = boq.find("double excavatedMaterialNet", fill_anchor)
    snippet = boq[fill_anchor:next_anchor if next_anchor > fill_anchor else fill_anchor + 350]
    if "pipeVolume" in snippet:
        errors.append("Pipe volume is still being deducted from fill above blanket.")

for marker in [
    '"CE_PROD_QUANTITY"',
    '"Quantity Production"',
    '"CE_BOQCENTER "',
    '"CE_PROD_REPORT"',
    '"Report Production"',
    '"CE_REPORTCENTER "',
]:
    if marker not in ribbon:
        errors.append("Missing production-ribbon marker: " + marker)

if errors:
    print("October 4 sewer BOQ/production validation FAILED", file=sys.stderr)
    for error in errors:
        print(" - " + error, file=sys.stderr)
    raise SystemExit(1)

print(
    "October 4 sewer BOQ/production validation passed: CE_BOQSEWER all/selected "
    "network-part scope, blanket-only pipe-volume deduction, branch grouping, "
    "and Quantity/Report production ribbon entries are guarded."
)

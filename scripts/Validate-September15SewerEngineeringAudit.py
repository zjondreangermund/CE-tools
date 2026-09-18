from pathlib import Path
import re

root = Path(__file__).resolve().parents[1]
source_root = root / "src/CE.Tools.Civil3D"
audit_source = (source_root / "August24FieldCompletionCommands.cs").read_text(encoding="utf-8")
menu = (source_root / "September11FieldCompletionMenu.cs").read_text(encoding="utf-8")
ribbon = (source_root / "PluginEntry.cs").read_text(encoding="utf-8")
core_finalizer = (root / "scripts/Repair-September10-SewerAuditSequenceDynamicMultiDimension-Core-Civil3D2023.ps1").read_text(encoding="utf-8")
compile_finalizer = (root / "scripts/Repair-September10-CompileFix-SewerAuditSequence-Civil3D2023.ps1").read_text(encoding="utf-8")

required_audit = [
    '"CE_SEWAUDITLIMITS"',
    'ReadPipeDiameter(pipe, false)',
    'ReadPipeDiameter(pipe, true)',
    'pipe.StartPoint.Z - insideDiameter * 0.5',
    'pipe.EndPoint.Z - insideDiameter * 0.5',
    'surface.FindElevationAtXY(point.X, point.Y) - (point.Z + outsideRadius)',
    'double rim = ReadFiniteDouble(structure, "RimElevation")',
    'double sump = ResolveAbsoluteSumpElevation(structure, lowInvert, out sumpDepth)',
    'ReadFiniteDouble(structure, "SumpDepth")',
    'double depth = IsFinite(rim) && IsFinite(sump) ? rim - sump : double.NaN',
    'Math.Max(0.0, lowInvert - sump)',
    'result.Rows.Add(new List<string>',
    'GridReportPresenter.ShowReportAndOfferTable(',
    '"CE TOOLS SEWER ENGINEERING AUDIT"',
    '"Sump Clearance"',
    'The network is not modified.',
]
for token in required_audit:
    if token not in audit_source:
        raise SystemExit(f"Sewer engineering audit marker missing: {token}")

method = re.search(
    r"private static SewerAuditResult AuditNetwork\(.*?\n        \}\n\n        private static void AddEndpoint",
    audit_source,
    re.DOTALL,
)
if not method:
    raise SystemExit("Could not isolate the sewer engineering AuditNetwork implementation.")
audit_method = method.group(0)

for forbidden in [
    "LinkExistingPartsToSurface(",
    ".ApplyRules(",
    ".UpgradeOpen(",
    ".Erase(",
    "pipe.StartPoint.Z);",
    "pipe.EndPoint.Z);",
]:
    if forbidden in audit_method:
        raise SystemExit(f"Read-only sewer audit regressed: {forbidden}")

# A pipe with several failing samples is still one failing pipe. The increment must
# remain outside the sampling loop and occur exactly once in the audit method.
if audit_method.count("result.CoverViolations++;") != 1:
    raise SystemExit("Cover violations must be counted once per failing pipe.")
sample_loop = re.search(
    r"for \(int index = 0; index <= samples; index\+\+\).*?\n                        \}",
    audit_method,
    re.DOTALL,
)
if not sample_loop or "result.CoverViolations++;" in sample_loop.group(0):
    raise SystemExit("Cover violation counting moved inside the per-pipe sample loop.")

# Inside inverts must not silently fall back to the outside diameter.
inside_names = re.search(
    r"string\[\] names = outside.*?: new\[\] \{ \"InnerDiameterOrWidth\" \};",
    audit_source,
    re.DOTALL,
)
if not inside_names:
    raise SystemExit("Inside-diameter lookup can no longer be verified as inside-only.")

for text, label, tokens in [
    (menu, "Field Completion", ['"Sewer Integrity Audit - Connections / Rules / Levels"', '"CE_SEWAUDITLIMITS"']),
    (ribbon, "Sewer ribbon", ['Cmd("Integrity / Engineering Audit", "CE_SEWAUDITLIMITS "']),
]:
    for token in tokens:
        if token not in text:
            raise SystemExit(f"{label} marker missing: {token}")

for text, label in [(core_finalizer, "core finalizer"), (compile_finalizer, "compile finalizer")]:
    if "CE TOOLS SEWER ENGINEERING AUDIT" not in text:
        raise SystemExit(f"The {label} does not recognize the current read-only audit.")

print("September 15 sewer engineering audit regression checks passed.")

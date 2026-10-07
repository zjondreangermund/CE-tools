"""Regression guard for the Oct 7 Water/Stormwater + field geometry follow-up."""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "src" / "CE.Tools.Civil3D"
errors = []

production = (SRC / "August11ProductionCentreCommands.cs").read_text(encoding="utf-8-sig")
fillet = (SRC / "September04FieldGeometryCompletionCommands.cs").read_text(encoding="utf-8-sig")
grading = (SRC / "August23PlatformDynamicGradingCommands.cs").read_text(encoding="utf-8-sig")
junction = (SRC / "September16FieldCommentCompletionCommands.cs").read_text(encoding="utf-8-sig")
flood = (SRC / "FloodProductionCulvertDesignCommands.cs").read_text(encoding="utf-8-sig")

for marker in [
    'RunCentre("STORMWATER PRODUCTION"',
    '"Stormwater Supplementary", "CE_STORMWATERFIELDSUPPLEMENTARY"',
    'RunCentre("WATER PRODUCTION"',
    '"Water Supplementary", "CE_WATERFIELDSUPPLEMENTARY"',
]:
    if marker not in production:
        errors.append("water/storm production-centre parity missing: " + marker)

for marker in [
    "TrySlotJunctionDistance(",
    "actual terminal support-line junction",
    "linked/connected terminal support-line junctions",
]:
    if marker not in fillet:
        errors.append("connected MultiFillet behavior missing: " + marker)

for marker in [
    "CE bounded TIN infill surface created directly",
    "GradingGroup API is unavailable",
    "TryCreateFallbackInfillSurface(",
]:
    if marker not in grading:
        errors.append("GradingGroup-independent infill fallback missing: " + marker)

for marker in [
    '"FeatureLineColor"',
    "FeatureLineColourChoices()",
    "FeatureLineColourService.Prepare(",
    "ApplySelectedFeatureLineColour(",
]:
    if marker not in junction:
        errors.append("junction feature-line colour choice missing: " + marker)

# Existing flood fix must stay in the same field release.
for marker in [
    "CE_FLOODCULVERTDESIGN",
    "OpenMode.ForWrite",
]:
    if marker not in flood:
        errors.append("flood culvert write-safety regression marker missing: " + marker)

if errors:
    print("October 7 field follow-up regression FAILED")
    for error in errors:
        print(" -", error)
    raise SystemExit(1)

print("October 7 field follow-up regression passed.")

from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
repair = (ROOT / "scripts" / "Repair-August12PersistentProductionUi-Civil3D2023.ps1").read_text(encoding="utf-8")
road = (ROOT / "src" / "CE.Tools.Civil3D" / "August13RoadProductionCentres.cs").read_text(encoding="utf-8")
styles = (ROOT / "src" / "CE.Tools.Civil3D" / "August12DisciplineStyleCommands.cs").read_text(encoding="utf-8")

errors = []
if 'CommandMethod("CE_TOOLS", "CE_ROADSTYLES"' not in styles:
    errors.append("CE_ROADSTYLES command owner is missing")
if 'A("CE-Road Styles","CE_ROADSTYLES"' not in road:
    errors.append("Road V2 Settings Centre does not expose CE_ROADSTYLES")
if "$roadProductionV2 = Required 'August13RoadProductionCentres.cs'" not in repair:
    errors.append("persistent Production UI repair does not inspect Road V2 source")
if "Road V2 discipline style command wiring missing: CE_ROADSTYLES" not in repair:
    errors.append("Road V2-specific staging guard is missing")
if "'CE_PLATFORMSTYLES', 'CE_SURVEYSTYLES', 'CE_SWSTYLES'" not in repair:
    errors.append("legacy discipline style validation list was not retained")

if errors:
    print("CE Tools Road V2 staging validation failed:")
    for error in errors:
        print("- " + error)
    raise SystemExit(1)

print("CE Tools Road V2 style-centre staging validation passed.")

from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "src" / "CE.Tools.Civil3D"
SCRIPTS = ROOT / "scripts"


def text(path: Path) -> str:
    if not path.is_file():
        raise SystemExit(f"CONSOLIDATION VALIDATION FAILED: missing {path.relative_to(ROOT)}")
    return path.read_text(encoding="utf-8", errors="replace")


def require(condition: bool, message: str) -> None:
    if not condition:
        raise SystemExit(f"CONSOLIDATION VALIDATION FAILED: {message}")


prep = text(SRC / "BackgroundPreparationCommands.cs")
legacy_bg = text(SRC / "BackgroundXrefManagementCommands.cs")
latest = text(SRC / "August17LatestMainSync.cs")
final_repair = text(SCRIPTS / "Repair-August17-ProjectProductionComments-Civil3D2023.ps1")
late_followup = text(SCRIPTS / "Repair-August14-RuntimeFieldTestFollowup-Civil3D2023.ps1")
installer = text(ROOT / "BUILD-INSTALL-CIVIL3D-2023.cmd")

# One established CE_BACKGROUNDTOOLS owner only. The Survey preparation launcher is
# deliberately unique and hands off to the established XREF/background manager.
require('"CE_BACKGROUNDTOOLS"' in legacy_bg, "existing CE_BACKGROUNDTOOLS owner is missing")
registrations = []
for path in SRC.glob("*.cs"):
    source = text(path)
    if re.search(r'CommandMethod\([^\n]*"CE_BACKGROUNDTOOLS"', source):
        registrations.append(path.name)
require(registrations == ["BackgroundXrefManagementCommands.cs"],
        f"CE_BACKGROUNDTOOLS must have exactly one owner; found {registrations}")

for command in (
    "CE_BACKGROUNDPREPTOOLS",
    "CE_BGBURSTALL",
    "CE_BGCOLOR250",
    "CE_BGCLEAN",
    "CE_BGFREEZESOLIDHATCH",
    "CE_BGFREEZEDIMS",
    "CE_BGSCALECORRECTION",
):
    require(f'"{command}"' in prep, f"missing Background preparation command {command}")
require('"CE_BACKGROUNDTOOLS"' in prep,
        "Survey Background preparation launcher does not hand off to existing Background/XREF utilities")

# User-requested operations must remain explicit in source.
for token in (
    "Blocks burst",
    "Color.FromColorIndex(ColorMethod.ByAci, 250)",
    'editor.Command("_.AUDIT"',
    'editor.Command("_.-OVERKILL"',
    'editor.Command("_.-PURGE"',
    "CE-BG-SOLID-HATCH-FROZEN",
    "CE-BG-DIMENSIONS-FROZEN",
    "Parking reference 2500 -> 2.5 m",
    "Parking reference 5000 -> 2.5 m",
    "External wall reference 220 -> 0.220 m",
    "External wall reference 440 -> 0.220 m",
    "Double-check two reference lengths",
    "UnitsValue.Meters",
):
    require(token in prep, f"Background requirement token missing: {token}")

# The final Project/Survey repair must continue to enforce the requested one-page
# page ownership and style order after older August repairs.
require("Project Production still exposes Survey Location or Namibia LO/WGS84" in final_repair,
        "final Project Production exclusion guard is missing")
require("Discipline Style Presets is not above Project Style Centre" in final_repair,
        "Project style-order guard is missing")
require("CE_PROJECTPRODUCTIONSTRUCTURED" in final_repair and "CE_SURVEYPRODUCTIONSTRUCTURED" in final_repair,
        "Project/Survey structured front-door repair is missing")

# This follow-up runs after the older August compatibility repairs and must reapply
# the final contract, then insert the Survey Background preparation row.
require("Repair-August17-ProjectProductionComments-Civil3D2023.ps1" in late_followup,
        "late staging does not reapply final Project/Survey contract")
require('A("CE-Background Tools", "CE_BACKGROUNDPREPTOOLS"' in late_followup,
        "Survey PREPARE does not expose the unique Background preparation launcher")

require('SyncId = "2026-08-17-background-consolidation-4"' in latest,
        "latest source sync marker is not the Background consolidation marker")
require("BackgroundToolsUnderSurveyPrepare = true" in latest,
        "latest source marker does not record Survey Background Tools")
require("2026-08-17-background-consolidation-4" in installer,
        "installer does not reject pre-consolidation source copies")

print("August 17 Background / Project / Survey consolidation validation passed.")

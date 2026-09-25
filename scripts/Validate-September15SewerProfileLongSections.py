from pathlib import Path
import re

root = Path(__file__).resolve().parents[1]
source_root = root / "src/CE.Tools.Civil3D"
sewer = (source_root / "SewerProductionCommands.cs").read_text(encoding="utf-8")
runtime = (source_root / "PreBuildRuntimeCompletionCommands.cs").read_text(encoding="utf-8")
menu = (source_root / "September11FieldCompletionMenu.cs").read_text(encoding="utf-8")
ribbon = (source_root / "PluginEntry.cs").read_text(encoding="utf-8")
finalizer = (root / "scripts/Repair-UtilityProfileIsolation-Civil3D2023.ps1").read_text(encoding="utf-8")

required_sewer = [
    'new List<SewerAlignmentRecord> { record }',
    '"\\nCE_SEWPROFILE skipped {0}: {1}"',
    'out int bandItemsLinked',
    'out int bandBindingWarnings',
    'localBandItems = ProfileViewBandDataBinder.Bind(',
    'one or more band links were skipped safely',
    'returns a non-writable band item (eNotOpenForWrite)',
    'Do not run a second global band refresh here.',
    'database.TransactionManager.QueueForGraphicsFlush();',
    'AcApplication.UpdateScreen();',
    'GridReportPresenter.ShowReportAndOfferTable(',
    '"CE Tools - Sewer Profiles / Long Sections"',
    '"CE SEWER PROFILE / LONG-SECTION REGISTER"',
    '"Created; review network-part descriptions"',
]
for token in required_sewer:
    if token not in sewer:
        raise SystemExit(f"Sewer profile/long-section marker missing: {token}")

command = re.search(
    r'\[CommandMethod\("CE_SEWPROFILE".*?\n        \[CommandMethod\("CE_SEWINFO"',
    sewer,
    re.DOTALL,
)
if not command:
    raise SystemExit("Could not isolate CE_SEWPROFILE for safety checks.")
command_text = command.group(0)

if command_text.count("CreateProfileObjects(") != 1:
    raise SystemExit("CE_SEWPROFILE must create each branch through one isolated call site.")
if "for (int index = 0; index < records.Count; index++)" not in command_text:
    raise SystemExit("CE_SEWPROFILE no longer iterates branches independently.")
if "ProfileViewBandRuntimeManager.RefreshAll(document)" in command_text:
    raise SystemExit("CE_SEWPROFILE still performs an unsafe global band refresh.")
if "Do not run a second global band refresh here." not in command_text:
    raise SystemExit("CE_SEWPROFILE safety boundary marker is missing.")

apply_band = re.search(
    r"private static bool ApplyBandSet\(.*?\n        \}\n\n        private static int LinkBandItems",
    runtime,
    re.DOTALL,
)
if not apply_band:
    raise SystemExit("Could not isolate profile-view band application.")
for forbidden in ('InvokeNoArgument(bands,\n                    "Clear"', '"RemoveAll"', '"EraseAll"'):
    if forbidden in apply_band.group(0):
        raise SystemExit("Profile band refresh can erase valid band rows before import succeeds.")

for text, label, tokens in [
    (menu, "Field Completion", ['"Sewer Profiles / Long Sections"', '"CE_SEWPROFILE"']),
    (ribbon, "Sewer ribbon", ['Cmd("Create Profiles / Long Sections", "CE_SEWPROFILE "']),
    (finalizer, "utility profile finalizer", ["$sewerText.Contains('CE_SEWPROFILE skipped {0}: {1}')"]),
]:
    for token in tokens:
        if token not in text:
            raise SystemExit(f"{label} marker missing: {token}")

for forbidden in (
    "AcApplication.Idle +=",
    "new SewerProductionCommands().CreateProfiles",
    "document.SendStringToExecute(\"CE_SEWPROFILE \"",
):
    if forbidden in command_text:
        raise SystemExit(f"Unsafe automatic/nested sewer profile execution found: {forbidden}")

print("September 15 sewer profile/long-section regression checks passed.")

from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
commands = (ROOT / "src/CE.Tools.Civil3D/September24FieldCompletionCommands.cs").read_text(encoding="utf-8")
bands = (ROOT / "src/CE.Tools.Civil3D/ProfileViewBandDataBinder.cs").read_text(encoding="utf-8")
menu = (ROOT / "src/CE.Tools.Civil3D/September11FieldCompletionMenu.cs").read_text(encoding="utf-8")
workflow = (ROOT / ".github/workflows/core-tests.yml").read_text(encoding="utf-8")

command = commands.split("public void AddBandLabelsToMultipleProfileViews()", 1)[1].split(
    '[CommandMethod("CE_TOOLS", "CE_PROFILEMOVEVERTICAL"', 1
)[0]

for marker in [
    "foreach (ObjectId id in selection.Value.GetObjectIds())",
    "using (Transaction transaction = document.Database.TransactionManager.StartTransaction())",
    "OpenMode.ForRead,",
    "profileView.UpgradeOpen();",
    "ProfileViewBandDataBinder.HasRoadBandRoles(",
    "August13RoadProfileViewFinalizerCommands.ResolveRoadProfiles(",
    "ProfileViewBandDataBinder.BindRoad(",
    "EnableProfileViewBandLabels(",
    "non-profile objects ignored={4}",
    "views without band items={6}",
    "Import the required band set to those views",
    "band-link warnings={8}",
]:
    if marker not in command:
        raise SystemExit(f"Multiple profile-view band-label repair missing: {marker}")

ordered = [
    command.find("StartTransaction()"),
    command.find("HasRoadBandRoles("),
    command.find("ResolveRoadProfiles("),
    command.find("BindRoad("),
    command.find("EnableProfileViewBandLabels("),
    command.find("transaction.Commit();"),
]
if min(ordered) < 0 or ordered != sorted(ordered):
    raise SystemExit("Each view must rebind road sources and enable labels before committing independently.")

for marker in [
    "internal static bool HasRoadBandRoles(",
    '"GetBottomBandItems"',
    '"GetTopBandItems"',
    'identity.Contains("LEFT")',
    'identity.Contains("RIGHT")',
    'identity.Contains("CENTRE")',
    "hasLeft && hasRight && hasCentre",
]:
    if marker not in bands:
        raise SystemExit(f"Road profile-view band detection missing: {marker}")

if "Validate September 25 profile-view band labels" not in workflow or "Validate-September25ProfileViewBandLabels.py" not in workflow:
    raise SystemExit("Core tests do not run the multiple profile-view band-label regression check.")
if '"CE_PROFILEBANDLABELSMULTI"' not in menu or "utility network links intact" not in menu:
    raise SystemExit("The band-label workflow description does not describe safe road relinking.")

print("September 25 multiple profile-view band-label validation passed.")

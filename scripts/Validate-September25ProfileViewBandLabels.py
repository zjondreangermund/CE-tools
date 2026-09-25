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
    "native profile-band label values={4}",
    "band source assignment(s) did not verify after write",
    "non-profile objects ignored={6}",
    "views without band items={8}",
    "Import the required band set to those views",
    "band-link warnings={10}",
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
    "TrySetObjectIdProperty(item, property, source)",
    "ProfileDataBandLabelGroup.GetAvailableLabelGroupIds(",
    "group.SubEntityCount",
    "CountBandStylesWithoutLabelComponents(",
    "CountRoadSourceProfilesAlreadyInView(",
    "sourceFieldsLinked == sourceFieldsExpected",
]:
    if marker not in bands:
        raise SystemExit(f"Road profile-view band detection missing: {marker}")

for marker in [
    "item.ShowLabels = false;",
    "item.ShowLabels = true;",
    "verified road band sources={3}",
    "native profile-band label values={4}",
    "road source profiles already in graph={11}",
]:
    if marker not in command:
        raise SystemExit(f"Road band label regeneration/verification missing: {marker}")

road_import = (ROOT / "src/CE.Tools.Civil3D/September14AlignmentBandStyleCommands.cs").read_text(encoding="utf-8")
import_command = road_import.split("public void ApplyRoadBandSetAndShowLabels()", 1)[1].split(
    "private static int EnableBandLabels(", 1
)[0]
for marker in [
    "out localLinkWarnings",
    "ProfileViewBandDataBinder.CountProfileDataBandLabelSubentities(",
    "styles without label components={5}",
]:
    if marker not in import_command:
        raise SystemExit(f"Road band-set import label verification missing: {marker}")
for marker in ["item.ShowLabels = false;", "item.ShowLabels = true;"]:
    if marker not in road_import:
        raise SystemExit(f"Road band-set label regeneration missing: {marker}")

finalizer = (ROOT / "src/CE.Tools.Civil3D/August13RoadProfileViewFinalizerCommands.cs").read_text(encoding="utf-8")
finalizer_flow = finalizer.split("ResolveRoadProfiles(", 1)[1].split(
    "settings.ProfileViewStyle = actualView;", 1
)[0]
if finalizer_flow.find("ProfileViewBandDataBinder.BindRoad(") > finalizer_flow.find("EnsureProfilesInProfileView("):
    raise SystemExit("New road profile views must link band sources before adding those profiles to the graph.")

corridor = (ROOT / "src/CE.Tools.Civil3D/RoadCorridorCompletionCommands.cs").read_text(encoding="utf-8")
design_view_flow = corridor.split("private static int BindDesignToProfileViews", 1)[1].split(
    "private static ObjectId FindRoadRoleProfile", 1
)[0]
if design_view_flow.find("ProfileViewBandDataBinder.BindRoad(") > design_view_flow.find("EnsureProfilesInProfileView("):
    raise SystemExit("Corridor completion must link profile band sources before graph profiles are added.")

if "Validate September 25 profile-view band labels" not in workflow or "Validate-September25ProfileViewBandLabels.py" not in workflow:
    raise SystemExit("Core tests do not run the multiple profile-view band-label regression check.")
if '"CE_PROFILEBANDLABELSMULTI"' not in menu or "utility network links intact" not in menu:
    raise SystemExit("The band-label workflow description does not describe safe road relinking.")

print("September 25 multiple profile-view band-label validation passed.")

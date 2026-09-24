from pathlib import Path

root = Path(__file__).resolve().parents[1]
appearance_source = (root / "src/CE.Tools.Civil3D/FeatureProfileSurfaceCommentCommands.cs").read_text(encoding="utf-8")
band_source = (root / "src/CE.Tools.Civil3D/September14AlignmentBandStyleCommands.cs").read_text(encoding="utf-8")
launcher_source = (root / "src/CE.Tools.Civil3D/ProfileViewBatchCommands.cs").read_text(encoding="utf-8")
menu_source = (root / "src/CE.Tools.Civil3D/September11FieldCompletionMenu.cs").read_text(encoding="utf-8")

colour_window = appearance_source.split("internal sealed class FeatureLineAppearanceWindow", 1)[1]
for marker in [
    "private readonly ComboBox _colour;",
    "ItemsSource = colourChoices",
    "CreateColourChoices()",
    "for (int index = 1; index <= 255; index++)",
    "FeatureLineColourChoice colour = _colour.SelectedItem as FeatureLineColourChoice;",
    "ColourIndex = colour.Index;",
]:
    if marker not in colour_window:
        raise SystemExit(f"Feature-line ACI dropdown marker missing: {marker}")

appearance_command = appearance_source.split("public void FeatureLineAppearance()", 1)[1].split(
    '[CommandMethod("CE_TOOLS", "CE_FLVERTEXLABELS"', 1
)[0]
style_assignment = appearance_command.find("TrySetFeatureLineStyleId(featureLine, colourStyleId)")
entity_colour = appearance_command.find("featureLine.Color = requestedColour;")
aci_colour = appearance_command.find("featureLine.ColorIndex = window.ColourIndex;")
if min(style_assignment, entity_colour, aci_colour) < 0 or not style_assignment < entity_colour < aci_colour:
    raise SystemExit("Feature-line display style and entity ACI colour are not applied in sequence.")
for marker in ["ResolveFeatureLineColourStyle(", "ApplyFeatureLineStyleColour(", "featureLine.RecordGraphicsModified(true)"]:
    if marker not in appearance_source:
        raise SystemExit(f"Feature-line visible colour marker missing: {marker}")

band_command = band_source.split("public void ApplyRoadBandSetAndShowLabels()", 1)[1].split(
    "private static int EnableBandLabels", 1
)[0]
for marker in [
    "var seen = new HashSet<ObjectId>();",
    "using (DocumentLock documentLock = document.LockDocument())",
    "Import and commit each view independently.",
    "profileView.Bands.ImportBandSetStyle(choice.Id);",
    "ProfileViewBandDataBinder.BindRoad(",
    "bandsEnabled += EnableBandLabels(profileView);",
    "QueueForGraphicsFlush()",
    "document.Editor.Regen()",
]:
    if marker not in band_command:
        raise SystemExit(f"Multiple profile-view band import marker missing: {marker}")

import_at = band_command.find("profileView.Bands.ImportBandSetStyle(choice.Id);")
commit_at = band_command.find("transaction.Commit();", import_at)
second_pass_at = band_command.find("foreach (ObjectId id in profileViewIds)", import_at)
label_enable_at = band_command.find("bandsEnabled += EnableBandLabels(profileView);", second_pass_at)
if min(import_at, commit_at, second_pass_at, label_enable_at) < 0 or not import_at < commit_at < second_pass_at < label_enable_at:
    raise SystemExit("Band imports are not committed before profile sources and labels are applied.")

for marker in [
    '"Import Road Band Set + Show Labels"',
    '"CE_ROADBANDLABELS "',
]:
    if marker not in launcher_source:
        raise SystemExit(f"Profile-view launcher entry missing: {marker}")
if '"Import Road Band Set to Multiple Profile Views"' not in menu_source:
    raise SystemExit("Road workflow menu does not expose the multi-view band-set import.")

print("Feature-line color dropdown and multi-view band import validation passed.")

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
style_prepared = appearance_command.find(
    "colourStylesByFeatureLine[featureLine.ObjectId] = colourStyleId;"
)
entity_colour = appearance_command.find("featureLine.Color = requestedColour;")
aci_colour = appearance_command.find("featureLine.ColorIndex = window.ColourIndex;")
first_commit = appearance_command.find("transaction.Commit();", aci_colour)
style_apply_pass = appearance_command.find(
    "TrySetFeatureLineStyleId(\n                                featureLine,\n                                pair.Value,\n                                transaction)"
)
style_readback = appearance_command.find("string actualName = ReadText(featureLine, \"StyleName\", string.Empty);")
verified_style_count = appearance_command.find("styleChanged++;", style_readback)
if min(style_prepared, entity_colour, aci_colour, first_commit, style_apply_pass, style_readback, verified_style_count) < 0:
    raise SystemExit("Feature-line color style preparation, committed assignment, or read-back verification is missing.")
if not style_prepared < entity_colour < aci_colour < first_commit < style_apply_pass < style_readback < verified_style_count:
    raise SystemExit("Feature-line color styles must be committed before assignment and counted only after read-back.")
for marker in ["ResolveFeatureLineColourStyle(", "ApplyFeatureLineStyleColour(", "featureLine.RecordGraphicsModified(true)"]:
    if marker not in appearance_source:
        raise SystemExit(f"Feature-line visible colour marker missing: {marker}")

style_resolution = appearance_source.split(
    "private static ObjectId ResolveFeatureLineColourStyle(", 1
)[1].split("private static ObjectId FindFeatureLineStyleId(", 1)[0]
if 'ReadText(\n                featureLine,\n                "StyleName",' not in style_resolution:
    raise SystemExit("Feature-line colour style lookup must use the readable StyleName property.")
if 'ReadObjectIdProperty(\n                featureLine,\n                "StyleId"' in style_resolution:
    raise SystemExit("Feature-line colour style lookup must not read setter-only StyleId.")
if "FindFeatureLineStyleId(\n                    civilDocument,\n                    currentName,\n                    transaction)" not in style_resolution:
    raise SystemExit("Feature-line current style name is not resolved through the Civil style collection.")

style_assignment_helper = appearance_source.split(
    "private static bool TrySetFeatureLineStyleId(", 1
)[1].split("private static bool TrySetObjectIdProperty(", 1)[0]
for marker in [
    "featureLine.StyleId = styleId",
    'featureLine, "StyleName", string.Empty',
    "expectedStyleName",
]:
    if marker not in style_assignment_helper:
        raise SystemExit(f"Feature-line style assignment read-back check missing: {marker}")

if "OpenMode.ForWrite" not in style_resolution:
    raise SystemExit("Feature-line source style is not opened writable before CopyAsSibling.")

style_colour = appearance_source.split(
    "private static void ApplyFeatureLineStyleColour(", 1
)[1].split("private static bool TrySetFeatureLineStyleId(", 1)[0]
typed_style_write = style_colour.find("TryApplyTypedFeatureLineStyleColour(typedStyle, colour)")
display_style_writeback = style_colour.find('foreach (string methodName in new[]')
if typed_style_write < 0 or display_style_writeback < typed_style_write:
    raise SystemExit("Feature-line display-style wrapper write-back is skipped after typed style updates.")
if "return;" in style_colour[typed_style_write:display_style_writeback]:
    raise SystemExit("Feature-line display-style update returns before trying the wrapper write-back path.")

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

"""Source integration gates; executable API contract tests live under tests/."""
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]
def code(path):
    return re.sub(r"/\*[\s\S]*?\*/|//[^\n]*", "", (ROOT / path).read_text(encoding="utf-8"))
def require(source, *markers):
    for marker in markers:
        if marker not in source:
            raise SystemExit(f"Missing colour/band integration: {marker}")

appearance = code("src/CE.Tools.Civil3D/FeatureProfileSurfaceCommentCommands.cs")
colour = code("src/CE.Tools.Civil3D/FeatureLineColourService.cs")
names = code("src/CE.Tools.Civil3D/CivilStyleNames.cs")
bands = code("src/CE.Tools.Civil3D/September14AlignmentBandStyleCommands.cs")
band_dialog = code("src/CE.Tools.Civil3D/ProfileViewBandImportDialog.cs")
persistence = code("src/CE.Tools.Civil3D/ProfileViewBandPersistence.cs")

require(appearance, "ItemsSource = colourChoices", "for (int index = 1; index <= 255; index++)",
        "FeatureLineColourService.Prepare(", "FeatureLineColourService.Assign(",
        "FeatureLineColourService.ReadDisplayColour(", "CivilStyleNames.Get(style as FeatureLineStyle)",
        "string actualName = featureLine.StyleName;", "featureLine.Color = requestedColour;",
        "style assignment failed:", "styleChanged++;")
require(names, "((Autodesk.Civil.DatabaseServices.DBObject)style).Name")
require(colour, "civilDocument.Styles.FeatureLineStyles", "featureLine.StyleName",
        "styles.Contains(currentName)", "current.CopyAsSibling(targetName)", "styles.Add(targetName)",
        "GetFeatureLineDisplayStylePlan()", "GetFeatureLineDisplayStyleModel()",
        "plan.Color = colour;", "model.Color = colour;", "featureLine.StyleId = styleId;",
        "CivilStyleNames.Get(style)", "OpenMode.ForWrite")
if "ReadText(" in colour or "GetProperty(" in colour or "catch { }" in colour:
    raise SystemExit("Colour repair must not reflect setter-only names or swallow API failures.")
require(bands, "profileView.Bands.ImportBandSetStyle(choice.Id);",
        "ProfileViewBandDataBinder.BindRoad(", "ProfileViewBandPersistence.EnableLabels(")
require(bands,
        "document.Editor.SetImpliedSelection(profileViewIds.Distinct().ToArray());",
        "batchDialog.OpenNativeDialog && profileViewIds.Count == 1",
        "batchDialog.OpenBandDataSources && profileViewIds.Count > 1",
        '"CE_PROFILEVIEWDATASOURCES "',
        "batchDialog.OpenEditMatch && profileViewIds.Count > 1",
        '"CE_PROFILEVIEWEDITMATCH "')
if "SetImpliedSelection(new[] { profileViewIds[0] })" in bands:
    raise SystemExit("Multi-profile band import must not collapse selection to the first profile view.")
require(band_dialog,
        "private static bool _lastOpenNative = false;",
        "IsEnabled = true",
        "Finish batch and keep all selected",
        "Open Band Data Sources for selected views",
        "Edit first selected in native properties, apply to all others",
        "OpenBandDataSources = _viewNames.Count > 1 && postAction == 1",
        "OpenEditMatch = _viewNames.Count > 1 && postAction == 2")
require(persistence, "!view.IsWriteEnabled", "bands.SetTopBandItems(top);",
        "bands.SetBottomBandItems(bottom);", "item.ShowLabels = true", "verify(top[index])", "verify(bottom[index])")
if "catch" in persistence:
    raise SystemExit("Band persistence must propagate native write failures.")
# Require actual executable calls, not commented-out validator markers.
for location in ("Top", "Bottom"):
    setter = persistence.index(f"bands.Set{location}BandItems(")
    if persistence.find(f"bands.Get{location}BandItems()", setter) < 0:
        raise SystemExit("Verify a fresh band collection after persistence.")
print("Colour assignment and persisted band integration checks passed.")

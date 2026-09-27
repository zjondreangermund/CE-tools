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

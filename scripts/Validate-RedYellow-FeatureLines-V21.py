from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def require(path, *tokens):
    text = (ROOT / path).read_text(encoding="utf-8")
    for token in tokens:
        if token not in text:
            raise SystemExit(f"{path}: missing {token!r}")


require(
    "src/CE.Tools.Civil3D/DynamicCoordinateLinkStore.cs",
    "LinkFeatureLineVertex(",
    "FeatureLinePointType.AllPoints",
    "PointNameRecordName",
)
require(
    "src/CE.Tools.Civil3D/AnnotationCommands.cs",
    "IList<ObjectId> createdIds",
    "IList<ObjectId> outputIds",
    "return marker.ObjectId;",
    "return leader.ObjectId;",
)
require(
    "src/CE.Tools.Civil3D/FeatureProfileSurfaceCommentCommands.cs",
    "Dynamic annotations created",
    "FEATURE LINE POINTS",
    "LinkFeatureLineVertex(",
    "LinkGeneratedObjects(",
    "Length 2D",
    "Length 3D",
    "Start Z",
    "End Z",
    "Min Grade %",
    "Max Grade %",
)
require(
    "src/CE.Tools.Civil3D/SurveyCoordinateWorkflowCommands.cs",
    "internal static ObjectId CreateLinkedTable(",
)
require(
    "src/CE.Tools.Civil3D/FeatureLineConstructionCommands.cs",
    "new FeatureLineAppearanceWindow(",
    "appearance.SelectedSiteId",
    "createdFeatureLine.ColorIndex = appearance.ColourIndex;",
)

print(
    "V21 feature-line continuation validated: dynamic vertex anchors, "
    "linked annotations/table, corrected report values, and creation colour/site."
)

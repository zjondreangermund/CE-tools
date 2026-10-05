"""Ensure the screenshot fixes remain connected to the shipped command paths.

Numerical regressions run in Core.Tests; Civil display-style contracts run in
Civil3D.ApiContract.Tests. This guard also runs against historically staged code.
"""
from pathlib import Path

root = Path(__file__).resolve().parents[1]
src = root / "src/CE.Tools.Civil3D"
checks = {
    "October03ViewportPlotProfileCommands.cs": [
        "AddViewportCreationSettings(settings)", "ReadFixedViewportScale(settings)",
        "CreateProfileViewports(document, transaction", "slots.Count == 0 && !createViewports",
        "(!splitLong && fixedScale == 0)", "viewport.Locked = false;",
        "viewport.CustomScale = scale;", "viewport creation was rolled back",
    ],
    "ProfileViewportCreation.cs": [
        '"Use existing viewports"', '"Create profile viewports"', '"Specified scale"',
        "ProfileViewportMath.CustomScale(", "ProfileViewportMath.RequiredSlots(",
        "paper.AppendEntity(viewport)", "viewport.On = true;",
    ],
    "August13RoadConstructionBoqCommands.cs": [
        '"Full road alignments"', '"Selected road alignments"',
        "new HashSet<ObjectId>()", "roadAlignmentIds.Add(baseline.AlignmentId)",
        "selectedAlignments.Contains(alignmentId)", "AlignmentType.Centerline",
        "alignment.Length / unitsPerMetre", '"Full Civil 3D alignment length"',
    ],
    "August23PlatformDynamicGradingCommands.cs": [
        "GradingSlopeTicks.TryShortTick(", "toePoints, source.Closed, sample.Cut",
        "ApplyGradingColour(database, featureLineId, colorIndex)",
        "ApplyGradingColour(document.Database, featureLineId, toeColorIndex)",
        "FeatureLineColourService.Prepare(", "FeatureLineColourService.Assign(",
    ],
    "FeatureProfileSurfaceCommentCommands.cs": [
        "CeGlobalProductionSettingsStore.Load(_defaults)",
        "CeGlobalProductionSettingsStore.Save(_defaults)",
        '_defaults.Text("Site")', '_defaults.Text("Layer")', '_defaults.Integer("Colour", 7)',
    ],
    "FeatureLineColourService.cs": [
        "colourIndex == 256 ? ColorMethod.ByLayer : ColorMethod.ByAci",
        'if (colourIndex == 256) plan.Layer = "0";',
        'if (colourIndex == 256) model.Layer = "0";',
    ],
}
for name, markers in checks.items():
    text = (src / name).read_text(encoding="utf-8-sig")
    for marker in markers:
        if marker not in text:
            raise SystemExit(f"October 5 command integration missing in {name}: {marker}")
print("October 5 viewport, alignment BOQ, grading colours/defaults and slope-tick integration passed.")

#!/usr/bin/env python3
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

checks = {
    "src/CE.Tools.Civil3D/RoadCorridorCompletionCommands.cs": [
        "ReadCorridorIds(",
        "CreateMissingRoadCorridors(",
        "AddCorridorByReflection(",
        "CivilProfile.CreateFromFeatureLine(",
        "ResolveRoadEdgeFeatureLines(",
        "BindRoadRolesToProfileViews(",
        "ProfileViewBindings",
    ],
    "src/CE.Tools.Civil3D/ProfileViewBandDataBinder.cs": [
        'identity.Contains("LEFT")',
        'identity.Contains("RIGHT")',
        'identity.Contains("CENTRE")',
        'identity.Contains("VERTICAL")',
        '"ShowLabels"',
        '"LabelsVisible"',
        "horizontalBand",
    ],
    "src/CE.Tools.Civil3D/August17ProductionFeatureLineCommands.cs": [
        '"SDWK"',
        '"SWLK"',
        '"WALKEDGE"',
        '"FOOTPATH"',
        '"FOOTWAY"',
        '"VERGEEDGE"',
        "sidewalkMatched",
    ],
    "src/CE.Tools.Civil3D/August21SurfaceSafety.cs": [
        "ClosestPlanPointIndex(",
        "VerifyAppliedElevations(",
        "RetryAbsoluteElevations(",
        "TryLinkRelativeToSurface(",
        "Surface elevation verification failed",
    ],
    "src/CE.Tools.Civil3D/FeatureProfileSurfaceCommentCommands.cs": [
        "ResolveFeatureLineColourStyle(",
        "ApplyFeatureLineStyleColour(",
        '"GetFeatureLineDisplayStylePlan"',
        '"GetFeatureLineDisplayStyleModel"',
        '"GetDisplayStyleProfile"',
        "visible colour styles",
    ],
    "src/CE.Tools.Civil3D/August23PlatformDynamicGradingCommands.cs": [
        "TryCreateEditableDrapeCopy(",
        "read-only/Auto feature lines cloned",
        "CivilFeatureLine.Create(",
        "normal feature-line copy",
    ],
}

errors = []
for relative, markers in checks.items():
    path = ROOT / relative
    if not path.exists():
        errors.append(f"missing file: {relative}")
        continue
    text = path.read_text(encoding="utf-8")
    for marker in markers:
        if marker not in text:
            errors.append(f"{relative}: missing marker {marker!r}")

road = (ROOT / "src/CE.Tools.Civil3D/RoadCorridorCompletionCommands.cs").read_text(encoding="utf-8")
if "IEnumerable values = collection as IEnumerable" in road:
    errors.append("road corridor completion still depends on CorridorCollection being IEnumerable")
if 'result.Corridors++' not in road:
    errors.append("road corridor completion no longer counts actual corridors")
if "FindDesignProfile(alignment, transaction) ??" not in road:
    errors.append("missing design-profile preference when creating corridors")

bands = (ROOT / "src/CE.Tools.Civil3D/ProfileViewBandDataBinder.cs").read_text(encoding="utf-8")
if 'identity.Contains("CURVE")' in bands:
    errors.append("generic CURVE matching can still overwrite horizontal-curve band data")

surface = (ROOT / "src/CE.Tools.Civil3D/August21SurfaceSafety.cs").read_text(encoding="utf-8")
if "ApplyPointElevations(featureLine, currentPi, sampled, count)" in surface:
    errors.append("surface elevations still use positional PI/sample indexing")
if "TryLinkRelativeToSurface(featureLine, surfaceId)" in surface:
    errors.append("relative-to-surface link is still attempted in the same feature-line write transaction")

appearance = (ROOT / "src/CE.Tools.Civil3D/FeatureProfileSurfaceCommentCommands.cs").read_text(encoding="utf-8")
if "featureLine.ColorIndex = window.ColourIndex;" not in appearance:
    errors.append("entity colour assignment was lost while adding visible Civil-style colour")

drape = (ROOT / "src/CE.Tools.Civil3D/August23PlatformDynamicGradingCommands.cs").read_text(encoding="utf-8")
if 'if (!IsEditableFeatureLine(document.Database, featureLineId, out error))' in drape:
    errors.append("multi-drape still rejects every non-editable Auto corridor feature line without clone fallback")

if errors:
    print("September 18 corridor/profile/feature-line retest validation FAILED")
    for error in errors:
        print(" -", error)
    raise SystemExit(1)

print("September 18 corridor/profile/feature-line retest validation passed.")

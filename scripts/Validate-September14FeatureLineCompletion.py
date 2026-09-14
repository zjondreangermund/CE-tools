from pathlib import Path

root = Path(__file__).resolve().parents[1]
completion = (root / "src/CE.Tools.Civil3D/September14FeatureLineCompletionCommands.cs").read_text(encoding="utf-8")
menu = (root / "src/CE.Tools.Civil3D/September11FieldCompletionMenu.cs").read_text(encoding="utf-8")
fatal = (root / "src/CE.Tools.Civil3D/August21PlatformRelativeFatalSafety.cs").read_text(encoding="utf-8")
dynamic = (root / "src/CE.Tools.Civil3D/August23PlatformDynamicGradingCommands.cs").read_text(encoding="utf-8")
relative = (root / "src/CE.Tools.Civil3D/FeatureLineRelativeCommands.cs").read_text(encoding="utf-8")

required_completion = [
    '"CE_FLRELLINKEXISTING"',
    '"CE_FLSTEPSSAFE"',
    '"CE_FLSURFACELINKEXISTING"',
    'private const string RelationKey = "CE_FLREL";',
    'TryMeasureConstantOffset(source, child',
    'Plan offsets are not constant enough to rebuild safely as a CE_FLREL child.',
    'Elevation differences are not constant enough to rebuild safely as a CE_FLREL child.',
    'new TypedValue((int)DxfCode.Text, sourceHandle)',
    'new TypedValue((int)DxfCode.Real, horizontalOffset)',
    'new TypedValue((int)DxfCode.Real, verticalOffset)',
    'new TypedValue((int)DxfCode.Int32, sequence)',
    'August21PlatformRelativeFatalSafety.CreatePlatformSteps(',
    'new August23PlatformDynamicGradingCommands().DrapeMultipleFeatureLines();',
    'Geometry was not recreated or erased.',
]
for token in required_completion:
    if token not in completion:
        raise SystemExit(f"Feature-line completion marker missing: {token}")

# Linking existing feature lines must remain metadata-only. It may add/update the
# CE_FLREL Xrecord, but it must never erase or replace the user's selected geometry.
for forbidden in [
    '.Erase(',
    'CivilFeatureLine.Create(',
    'FeatureLine.Create(',
    'Delete(',
]:
    if forbidden in completion:
        raise SystemExit(f"Existing feature-line link path contains destructive/create marker: {forbidden}")

required_menu = [
    '"CE_FIELDCOMPLETION"',
    '"Link Existing Relative Feature Lines"',
    '"CE_FLRELLINKEXISTING"',
    '"Safe Stepped Offsets - Multiple Feature Lines"',
    '"CE_FLSTEPSSAFE"',
    '"Dynamic Surface Link - Existing Feature Lines"',
    '"CE_FLSURFACELINKEXISTING"',
    '"Feature Line Appearance / Site"',
    '"CE_FLAPPEARANCE"',
    'safe/dynamic feature-line linking and stepped offsets',
]
for token in required_menu:
    if token not in menu:
        raise SystemExit(f"Field-completion feature-line marker missing: {token}")

required_fatal = [
    'internal static PlatformStepResult CreatePlatformSteps(',
    'TryCreateOffsetCandidate(',
    'WriteRelation(document, childId, new Relation',
    'VerifyFeatureLine(document, childId);',
    'Cleanup(document, childId);',
    'Native FeatureLine.Create is invoked only from committed temporary geometry.',
    'an existing linked child is never erased until',
]
for token in required_fatal:
    if token not in fatal:
        raise SystemExit(f"August 21 feature-line safety marker missing: {token}")

required_dynamic = [
    '"CE_PLATFORMDRAPEMULTI"',
    'public void DrapeMultipleFeatureLines()',
    'August21SurfaceSafety.TryApplyFeatureLineElevations(',
    'WriteDirectDrapeLink(document.Database, featureLineId',
    'PlatformDynamicRefreshManager.Queue();',
]
for token in required_dynamic:
    if token not in dynamic:
        raise SystemExit(f"August 23 dynamic drape marker missing: {token}")

# Keep the new relation record byte-for-byte compatible in shape with the canonical
# CE_FLREL reader/writer so existing CE_FLRELUPDATE / CE_FLRELUPDATEMULTI can rebuild it.
canonical_relation_tokens = [
    'private const string RecordKey = "CE_FLREL";',
    'new TypedValue((int)DxfCode.Text, sourceHandle)',
    'new TypedValue((int)DxfCode.Real, horizontalOffset)',
    'new TypedValue((int)DxfCode.Real, verticalOffset)',
    'new TypedValue((int)DxfCode.Int32, sequence)',
]
for token in canonical_relation_tokens:
    if token not in relative:
        raise SystemExit(f"Canonical CE_FLREL record marker missing: {token}")

print("September 14 feature-line completion regression checks passed.")

from pathlib import Path
import re

root = Path(__file__).resolve().parents[1]
source_root = root / "src/CE.Tools.Civil3D"
completion = (source_root / "September14FeatureLineCompletionCommands.cs").read_text(encoding="utf-8")
menu = (source_root / "September11FieldCompletionMenu.cs").read_text(encoding="utf-8")
field_pack = (source_root / "August24FieldCompletionCommands.cs").read_text(encoding="utf-8")
fatal = (source_root / "August21PlatformRelativeFatalSafety.cs").read_text(encoding="utf-8")
dynamic = (source_root / "August23PlatformDynamicGradingCommands.cs").read_text(encoding="utf-8")
relative = (source_root / "FeatureLineRelativeCommands.cs").read_text(encoding="utf-8")

required_completion = [
    '"CE_FLSTEPSSAFE"',
    '"CE_FLSURFACELINKEXISTING"',
    'August21PlatformRelativeFatalSafety.CreatePlatformSteps(',
    'new August23PlatformDynamicGradingCommands().DrapeMultipleFeatureLines();',
    'Existing source feature lines were kept.',
]
for token in required_completion:
    if token not in completion:
        raise SystemExit(f"Feature-line completion marker missing: {token}")

# CE_FLRELLINKEXISTING is an established August 24 command. Keep exactly one
# registration so old buttons/macros remain compatible while CE_FIELDCOMPLETION
# can surface the same canonical command without creating a duplicate command.
command_pattern = re.compile(
    r'\[CommandMethod\(\s*"CE_TOOLS"\s*,\s*"CE_FLRELLINKEXISTING"',
    re.MULTILINE,
)
registrations = []
for path in source_root.glob("*.cs"):
    text = path.read_text(encoding="utf-8")
    count = len(command_pattern.findall(text))
    registrations.extend([path.name] * count)
if registrations != ["August24FieldCompletionCommands.cs"]:
    raise SystemExit(
        "CE_FLRELLINKEXISTING must have exactly one canonical registration in "
        f"August24FieldCompletionCommands.cs; found {registrations}"
    )

required_field_pack = [
    '"CE_FLRELLINKEXISTING"',
    'public void LinkExistingFeatureLines()',
    'MeasureFeatureRelation(source, child, out horizontal, out vertical)',
    'WriteFeatureRelation(child, transaction, source.Handle.ToString(), horizontal, vertical, sequence++)',
    'Existing CE linked-feature-line refresh now owns these relationships.',
]
for token in required_field_pack:
    if token not in field_pack:
        raise SystemExit(f"Canonical existing-feature-line link marker missing: {token}")

link_match = re.search(
    r'\[CommandMethod\("CE_TOOLS", "CE_FLRELLINKEXISTING".*?'
    r'(?=\n\s*\[CommandMethod\("CE_TOOLS", "CE_FLRELADOPT")',
    field_pack,
    re.DOTALL,
)
if not link_match:
    raise SystemExit("Could not isolate the canonical CE_FLRELLINKEXISTING command body.")
for forbidden in ['.Erase(', 'CivilFeatureLine.Create(', 'FeatureLine.Create(', 'Delete(']:
    if forbidden in link_match.group(0):
        raise SystemExit(
            f"Canonical existing-feature-line link path became destructive: {forbidden}"
        )

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

# Keep the canonical CE_FLREL record shape intact so existing CE_FLRELUPDATE /
# CE_FLRELUPDATEMULTI continue to rebuild stored linked offsets.
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

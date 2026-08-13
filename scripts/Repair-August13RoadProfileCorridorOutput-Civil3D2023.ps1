[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$src = Join-Path $root 'src\CE.Tools.Civil3D'

function Required([string]$name) {
    $path = Join-Path $src $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "August 13 road profile/corridor source missing: $path"
    }
    return $path
}
function ReadText([string]$path) { [System.IO.File]::ReadAllText($path) }
function WriteText([string]$path,[string]$text) {
    [System.IO.File]::WriteAllText($path,$text,[System.Text.UTF8Encoding]::new($false))
}

$finalizer = Required 'August13RoadProfileCorridorOutputFixCommands.cs'
$finalizerText = ReadText $finalizer
foreach ($marker in @(
    '"CE_ROADVERTICALCURVESFINAL"',
    'AddFreeSymmetricParabolaByPVIAndCurveLength',
    'AddFreeSymmetricParabolaByLength',
    '"CE_ROADCORRIDOROUTPUTFIX"',
    '"CE-TOP"',
    '"CE-BOTTOM"',
    'OverhangCorrectionType.TopLinks',
    'OverhangCorrectionType.BottomLinks',
    'surface.IsBuild = true;',
    'SetLinkCodeAsBreakLine',
    'CorridorSlopePattern pattern = patterns.Add(',
    'FeatureLineCollectionMap')) {
    if (-not $finalizerText.Contains($marker)) {
        throw "Road profile/corridor finalizer marker missing: $marker"
    }
}

# Outside road/sidewalk offsets must use the CE-generated parent-handle chain
# instead of whichever centreline happens to be nearest after an offset is made.
$outsideResolver = Required 'August13RoadOutsideOffsetResolver.cs'
$outsideResolverText = ReadText $outsideResolver
foreach ($marker in @(
    'CE_ROAD_LAYOUT',
    'ResolveParentCentreline',
    'ReadParentHandle',
    'AverageDistanceToCentre',
    'SameSideFraction',
    'gain > minimumGain')) {
    if (-not $outsideResolverText.Contains($marker)) {
        throw "Outside-road-offset resolver marker missing: $marker"
    }
}

$roadCompletion = Required 'August11RoadCompletionCommands.cs'
$text = ReadText $roadCompletion
$text = $text.Replace(
    'Curve best = ChooseOutsideOffset(source, distance, centres);',
    'Curve best = August13RoadOutsideOffsetResolver.ChooseOutsideOffset(source, distance, transaction, document.Database, centres);')
$text = $text.Replace(
    '"Create only the offset side that is farther away from the nearest CE road centreline. This removes the need to guess Positive or Negative offset direction.");',
    '"Create only the offset side that is farther away from the linked parent CE road centreline. Road edges and shoulder/sidewalk edges follow their stored CE parent chain so both sides always move away from the carriageway.");')
WriteText $roadCompletion $text

$roadCorridor = Required 'RoadCorridorCompletionCommands.cs'
$text = ReadText $roadCorridor

# The complete road profile must always finish with the robust final vertical-curve pass.
# Accept the older source variants so downloaded/staged copies are repaired consistently.
$profileTarget = 'document.SendStringToExecute("CE_ROADPROFILES CE_ROADDESIGNPROFILE CE_ROADVERTICALCURVESFINAL ", true, false, true);'
$profileVariants = @(
    'document.SendStringToExecute("CE_ROADPROFILES CE_ROADDESIGNPROFILE ", true, false, true);',
    'document.SendStringToExecute("CE_ROADPROFILES CE_ROADDESIGNPROFILE CE_ROADVERTICALCURVES ", true, false, true);',
    'document.SendStringToExecute("CE_ROADPROFILES CE_ROADVERTICALCURVES CE_ROADDESIGNPROFILE ", true, false, true);'
)
foreach ($variant in $profileVariants) { $text = $text.Replace($variant, $profileTarget) }

# Complete Corridor uses the typed finalizer after the existing baseline/target pass.
$corridorTarget = 'document.SendStringToExecute("CE_ROADCORRIDORS CE_ROADCORRIDORCOMPLETE CE_ROADCORRIDOROUTPUTFIX ", true, false, true);'
$corridorVariants = @(
    'document.SendStringToExecute("CE_ROADCORRIDORS CE_ROADDESIGNPROFILE ", true, false, true);',
    'document.SendStringToExecute("CE_ROADCORRIDORS CE_ROADCORRIDORCOMPLETE ", true, false, true);'
)
foreach ($variant in $corridorVariants) { $text = $text.Replace($variant, $corridorTarget) }

# Match the requested Civil 3D surface definitions exactly.
$text = $text.Replace(
    'model.AddText("TopCodes", "01 Corridor Surfaces", "Top link codes", "Top,Pave", "Comma-separated corridor link codes included in the top surface.");',
    'model.AddText("TopCodes", "01 Corridor Surfaces", "Top link codes", "Top", "CE-TOP uses the Top link code. The finalizer also sets Top Links overhang correction and builds the surface.");')
$text = $text.Replace(
    'model.AddText("BottomCodes", "01 Corridor Surfaces", "Bottom link codes", "Datum,Subgrade", "Comma-separated corridor link codes included in the bottom surface.");',
    'model.AddText("BottomCodes", "01 Corridor Surfaces", "Bottom link codes", "Datum", "CE-BOTTOM uses the Datum link code. The finalizer also sets Bottom Links overhang correction and builds the surface.");')
$text = $text.Replace(
    'TopCodes = SplitCodes(model.Text("TopCodes"), new[] { "Top", "Pave" }),',
    'TopCodes = SplitCodes(model.Text("TopCodes"), new[] { "Top" }),')
$text = $text.Replace(
    'BottomCodes = SplitCodes(model.Text("BottomCodes"), new[] { "Datum", "Subgrade" }),',
    'BottomCodes = SplitCodes(model.Text("BottomCodes"), new[] { "Datum" }),')

# CivilChoice is bound through WPF DisplayMemberPath. The display property must
# be public; an internal Name property produces blank rows even when the IDs exist.
$text = $text.Replace(
    'internal string Name { get; private set; }',
    'public string Name { get; private set; }')
WriteText $roadCorridor $text

$production = Required 'August11ProductionCentreCommands.cs'
$text = ReadText $production
$text = $text.Replace(
    'Action("Complete Corridor", "CE_ROADCORRIDORCOMPLETE", "Create/rebuild baselines, regions, targets and corridor surfaces.", "04 DESIGN"),',
    'Action("Complete Corridor", "CE_ROADCORRIDORFULL", "Create/rebuild the road corridor, apply the selected target surface, build CE-TOP/CE-BOTTOM and add corridor slope patterns.", "04 DESIGN"),')
WriteText $production $text

# Same-build guards. Validate each marker in the file that actually owns it.
$roadCompletionText = ReadText $roadCompletion
if (-not $roadCompletionText.Contains('August13RoadOutsideOffsetResolver.ChooseOutsideOffset(source, distance, transaction, document.Database, centres)')) {
    throw 'CE_ROADOUTSIDEOFFSET is not routed through the linked-parent outside-offset resolver.'
}

$roadText = ReadText $roadCorridor
foreach ($marker in @(
    'CE_ROADPROFILES CE_ROADDESIGNPROFILE CE_ROADVERTICALCURVESFINAL',
    'CE_ROADCORRIDORS CE_ROADCORRIDORCOMPLETE CE_ROADCORRIDOROUTPUTFIX')) {
    if (-not $roadText.Contains($marker)) {
        throw "Road profile/corridor command-chain marker missing in RoadCorridorCompletionCommands.cs: $marker"
    }
}
foreach ($marker in @(
    '"Top link codes", "Top"',
    '"Bottom link codes", "Datum"',
    'public string Name { get; private set; }')) {
    if (-not $roadText.Contains($marker)) {
        throw "Road profile/corridor source marker missing in RoadCorridorCompletionCommands.cs: $marker"
    }
}

$productionText = ReadText $production
if (-not $productionText.Contains('Action("Complete Corridor", "CE_ROADCORRIDORFULL"')) {
    throw 'Road Production Complete Corridor is not routed through CE_ROADCORRIDORFULL.'
}

Write-Host 'Outside road/sidewalk offsets now follow the stored CE parent chain and always move away from their road centreline.' -ForegroundColor Green
Write-Host 'Road final profile now runs the robust vertical-curve pass automatically.' -ForegroundColor Green
Write-Host 'Corridor Target Surface popup now displays Civil 3D surface names.' -ForegroundColor Green
Write-Host 'CE-TOP is Top links/breaklines with Top Links overhang correction and IsBuild enabled.' -ForegroundColor Green
Write-Host 'CE-BOTTOM is Datum links/breaklines with Bottom Links overhang correction and IsBuild enabled.' -ForegroundColor Green
Write-Host 'Road Complete Corridor now adds/repairs actual corridor slope patterns after the base corridor pass.' -ForegroundColor Green
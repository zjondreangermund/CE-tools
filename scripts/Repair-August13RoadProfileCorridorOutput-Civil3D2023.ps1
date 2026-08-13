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

$roadCorridor = Required 'RoadCorridorCompletionCommands.cs'
$text = ReadText $roadCorridor

# The complete road profile must finish with the robust final vertical-curve pass.
$text = $text.Replace(
    'document.SendStringToExecute("CE_ROADPROFILES CE_ROADDESIGNPROFILE ", true, false, true);',
    'document.SendStringToExecute("CE_ROADPROFILES CE_ROADDESIGNPROFILE CE_ROADVERTICALCURVESFINAL ", true, false, true);')
$text = $text.Replace(
    'document.SendStringToExecute("CE_ROADPROFILES CE_ROADDESIGNPROFILE CE_ROADVERTICALCURVES ", true, false, true);',
    'document.SendStringToExecute("CE_ROADPROFILES CE_ROADDESIGNPROFILE CE_ROADVERTICALCURVESFINAL ", true, false, true);')

# Complete Corridor uses the typed finalizer after the existing baseline/target pass.
$text = $text.Replace(
    'document.SendStringToExecute("CE_ROADCORRIDORS CE_ROADCORRIDORCOMPLETE ", true, false, true);',
    'document.SendStringToExecute("CE_ROADCORRIDORS CE_ROADCORRIDORCOMPLETE CE_ROADCORRIDOROUTPUTFIX ", true, false, true);')

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
# be public; an internal Name property produces the blank rows seen in the user's
# Corridor Target Surface popup even though the ObjectIds are present.
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

# Same-build guards. Fail before compilation if an old/partial road source is staged.
$roadText = ReadText $roadCorridor
$productionText = ReadText $production
foreach ($marker in @(
    'CE_ROADPROFILES CE_ROADDESIGNPROFILE CE_ROADVERTICALCURVESFINAL',
    'CE_ROADCORRIDORS CE_ROADCORRIDORCOMPLETE CE_ROADCORRIDOROUTPUTFIX',
    '"Top link codes", "Top"',
    '"Bottom link codes", "Datum"',
    'public string Name { get; private set; }')) {
    if (-not $roadText.Contains($marker)) {
        throw "Road profile/corridor integration marker missing: $marker"
    }
}
if (-not $productionText.Contains('Action("Complete Corridor", "CE_ROADCORRIDORFULL"')) {
    throw 'Road Production Complete Corridor is not routed through CE_ROADCORRIDORFULL.'
}

Write-Host 'Road final profile now runs the robust vertical-curve pass automatically.' -ForegroundColor Green
Write-Host 'Corridor Target Surface popup now displays Civil 3D surface names.' -ForegroundColor Green
Write-Host 'CE-TOP is Top links/breaklines with Top Links overhang correction and IsBuild enabled.' -ForegroundColor Green
Write-Host 'CE-BOTTOM is Datum links/breaklines with Bottom Links overhang correction and IsBuild enabled.' -ForegroundColor Green
Write-Host 'Road Complete Corridor now adds/repairs actual corridor slope patterns after the base corridor pass.' -ForegroundColor Green

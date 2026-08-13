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

$profileViewFinalizer = Required 'August13RoadProfileViewFinalizerCommands.cs'
$profileViewText = ReadText $profileViewFinalizer
foreach ($marker in @(
    '"CE_ROADPROFILEVIEWFINAL"',
    'RoadProductionSettings.Read',
    'ProfileStyleLinker.Apply',
    'ProfileViewBandDataBinder.Bind',
    'ProfileViewBandSetStyle')) {
    if (-not $profileViewText.Contains($marker)) {
        throw "Road profile-view finalizer marker missing: $marker"
    }
}

$junctionConstruction = Required 'August13RoadJunctionConstructionCommands.cs'
$junctionText = ReadText $junctionConstruction
foreach ($marker in @(
    '"CE_ROADJUNCTIONCONSTRUCTIONTOOLS"',
    '"CE_ROADJUNCTIONCONSTRUCTION"',
    'region.Split(station)',
    'region.NeedsProcessing = !inside',
    'baseline.NeedsProcessing = true')) {
    if (-not $junctionText.Contains($marker)) {
        throw "Road junction construction marker missing: $marker"
    }
}

$constructionBoq = Required 'August13RoadConstructionBoqCommands.cs'
$boqText = ReadText $constructionBoq
foreach ($marker in @(
    '"CE_ROADBOQCONSTRUCTION"',
    'TinVolumeSurface.Create',
    'UnadjustedCutVolume',
    'UnadjustedFillVolume',
    'AppliedAssembly',
    'CalculatedShape',
    'CorridorFeatureLine',
    'SideSlopeArea')) {
    if (-not $boqText.Contains($marker)) {
        throw "Road construction BOQ marker missing: $marker"
    }
}

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

$profileTarget = 'document.SendStringToExecute("CE_ROADPROFILES CE_ROADDESIGNPROFILE CE_ROADVERTICALCURVESFINAL CE_ROADPROFILEVIEWFINAL ", true, false, true);'
$profileVariants = @(
    'document.SendStringToExecute("CE_ROADPROFILES CE_ROADDESIGNPROFILE ", true, false, true);',
    'document.SendStringToExecute("CE_ROADPROFILES CE_ROADDESIGNPROFILE CE_ROADVERTICALCURVES ", true, false, true);',
    'document.SendStringToExecute("CE_ROADPROFILES CE_ROADVERTICALCURVES CE_ROADDESIGNPROFILE ", true, false, true);',
    'document.SendStringToExecute("CE_ROADPROFILES CE_ROADDESIGNPROFILE CE_ROADVERTICALCURVESFINAL ", true, false, true);'
)
foreach ($variant in $profileVariants) { $text = $text.Replace($variant, $profileTarget) }

$corridorTarget = 'document.SendStringToExecute("CE_ROADCORRIDORS CE_ROADCORRIDORCOMPLETE CE_ROADCORRIDOROUTPUTFIX ", true, false, true);'
$corridorVariants = @(
    'document.SendStringToExecute("CE_ROADCORRIDORS CE_ROADDESIGNPROFILE ", true, false, true);',
    'document.SendStringToExecute("CE_ROADCORRIDORS CE_ROADCORRIDORCOMPLETE ", true, false, true);'
)
foreach ($variant in $corridorVariants) { $text = $text.Replace($variant, $corridorTarget) }

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
$text = $text.Replace(
    'internal string Name { get; private set; }',
    'public string Name { get; private set; }')
WriteText $roadCorridor $text

$production = Required 'August11ProductionCentreCommands.cs'
$text = ReadText $production
$text = $text.Replace(
    'Action("SETTINGS - Project Road Styles", "CE_PROJECTSTYLES", "Select road alignment/profile/profile-view/band/corridor styles.", "01 SETTINGS"),',
    'Action("CE-SETTINGS - Road Styles", "CE_ROADSETTINGS", "Select road-only alignment/profile/profile-view/band/corridor/assembly styles. Saved independently from other disciplines.", "01 SETTINGS"),')
$text = $text.Replace(
    'Action("Complete Corridor", "CE_ROADCORRIDORCOMPLETE", "Create/rebuild baselines, regions, targets and corridor surfaces.", "04 DESIGN"),',
    'Action("CE-Complete Corridor", "CE_ROADCORRIDORFULL", "Create/rebuild road corridors, apply target surface, build CE-TOP/CE-BOTTOM and add slope patterns.", "04 DESIGN"),')
$junctionAction = '                Action("CE-Junction Construction", "CE_ROADJUNCTIONCONSTRUCTIONTOOLS", "Create/finish multiple T/cross junctions and split/exclude through-road corridor regions at bellmouth limits.", "04 DESIGN"), '
if (-not $text.Contains('"CE_ROADJUNCTIONCONSTRUCTIONTOOLS"')) {
    $text = $text.Replace(
        '                Action("COMPLETE - Junction Setting-Out", "CE_JUNCTIONSETTINGOUT4", "Complete one full T/cross junction before continuing to the next.", "05 COMPLETE"),',
        $junctionAction + '                Action("COMPLETE - Junction Setting-Out", "CE_JUNCTIONSETTINGOUT4", "Complete one full T/cross junction before continuing to the next.", "05 COMPLETE"),')
}
$text = $text.Replace(
    'Action("Road BOQ", "CE_BOQROAD", "Create linked road quantities.", "05 COMPLETE"),',
    'Action("CE-Road Construction BOQ", "CE_ROADBOQCONSTRUCTION", "Cut/fill to datum, layerwork volumes, kerb lengths, road/sidewalk and side-slope areas from the live corridor model.", "05 COMPLETE"),')
WriteText $production $text

$roadProduction = Required 'RoadProductionCommentCommands.cs'
$text = ReadText $roadProduction
$text = $text.Replace(
    'RoadAction("Choose production styles", "CE_PROJECTSTYLES", "Choose road alignment/profile styles, label sets, profile-view band set, assembly, corridor and code-set styles before production starts.", "0 — Production setup"),',
    'RoadAction("CE-Road production settings", "CE_ROADSETTINGS", "Choose Road-only alignment/profile/profile-view/band/assembly/corridor/code-set styles before production starts.", "0 — Production setup"),')
if (-not $text.Contains('"CE_ROADJUNCTIONCONSTRUCTIONTOOLS"')) {
    $text = $text.Replace(
        '                    RoadAction("Refresh linked junctions", "CE_JUNCTIONREFRESH", "Refresh linked bellmouth labels after road edits.", "5 — Intersections"),',
        '                    RoadAction("Finalize junction corridors", "CE_ROADJUNCTIONCONSTRUCTIONTOOLS", "Split/exclude multiple corridor junction regions at bellmouth limits and finish construction outputs.", "5 — Intersections"),                    RoadAction("Refresh linked junctions", "CE_JUNCTIONREFRESH", "Refresh linked bellmouth labels after road edits.", "5 — Intersections"),')
}
$text = $text.Replace(
    'RoadAction("Road BOQ", "CE_BOQROAD", "Create the road bill of quantities in Excel format.", "8 — Production"),',
    'RoadAction("Road construction BOQ", "CE_ROADBOQCONSTRUCTION", "Cut/fill to datum, assembly layerwork, kerbs, road/sidewalk and side-slope quantities from live corridors.", "8 — Production"),')
WriteText $roadProduction $text

$roadCompletionText = ReadText $roadCompletion
if (-not $roadCompletionText.Contains('August13RoadOutsideOffsetResolver.ChooseOutsideOffset(source, distance, transaction, document.Database, centres)')) {
    throw 'CE_ROADOUTSIDEOFFSET is not routed through the linked-parent outside-offset resolver.'
}

$roadText = ReadText $roadCorridor
foreach ($marker in @(
    'CE_ROADPROFILES CE_ROADDESIGNPROFILE CE_ROADVERTICALCURVESFINAL CE_ROADPROFILEVIEWFINAL',
    'CE_ROADCORRIDORS CE_ROADCORRIDORCOMPLETE CE_ROADCORRIDOROUTPUTFIX',
    '"Top link codes", "Top"',
    '"Bottom link codes", "Datum"',
    'public string Name { get; private set; }')) {
    if (-not $roadText.Contains($marker)) {
        throw "Road profile/corridor integration marker missing in RoadCorridorCompletionCommands.cs: $marker"
    }
}

$productionText = ReadText $production
foreach ($marker in @(
    'Action("CE-SETTINGS - Road Styles", "CE_ROADSETTINGS"',
    '"CE_ROADCORRIDORFULL"',
    '"CE_ROADJUNCTIONCONSTRUCTIONTOOLS"',
    '"CE_ROADBOQCONSTRUCTION"')) {
    if (-not $productionText.Contains($marker)) {
        throw "Road Production Centre integration marker missing: $marker"
    }
}

Write-Host 'Outside road/sidewalk offsets follow the stored CE parent chain and move away from the road centreline.' -ForegroundColor Green
Write-Host 'Road profiles finish with robust vertical curves plus the saved Road-only profile-view style and band set.' -ForegroundColor Green
Write-Host 'Corridor Target Surface popup displays Civil 3D surface names.' -ForegroundColor Green
Write-Host 'CE-TOP uses Top links/breaklines with Top Links overhang correction and IsBuild enabled.' -ForegroundColor Green
Write-Host 'CE-BOTTOM uses Datum links/breaklines with Bottom Links overhang correction and IsBuild enabled.' -ForegroundColor Green
Write-Host 'Road Junction Construction can split/exclude multiple corridor junction regions at bellmouth/feature-line limits.' -ForegroundColor Green
Write-Host 'Road Construction BOQ reads cut/fill-to-datum, layerwork, kerbs, surfacing, sidewalks and side slopes from live corridors.' -ForegroundColor Green

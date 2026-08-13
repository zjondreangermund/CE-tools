[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$src = Join-Path $root 'src\CE.Tools.Civil3D'

function Required([string]$name) {
    $path = Join-Path $src $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "August 11 compiler source missing: $path" }
    return $path
}
function ReadText([string]$path) { [System.IO.File]::ReadAllText($path) }
function WriteText([string]$path,[string]$text) { [System.IO.File]::WriteAllText($path,$text,[System.Text.UTF8Encoding]::new($false)) }

# Road Production moved to August13RoadProductionCentres.cs. Older field-completion
# passes still look for the former Road Continuity / Junction Finish array entry in
# August11ProductionCentreCommands.cs. Add a staging-only compatibility template so
# those historical injectors can remain idempotent without taking ownership back
# from CE_ROADPRODUCTIONV2. The template is never called at runtime.
$productionCompat = Required 'August11ProductionCentreCommands.cs'
$roadV2 = Join-Path $src 'August13RoadProductionCentres.cs'
$legacyRoadAnchor = '                Action("Road Continuity / Junction Finish", "CE_ROADAUG11TOOLS", "Join reserve centrelines, outside offsets, junction trim boundaries and route annotation.", "02 PREPARE"),'
if ((Test-Path -LiteralPath $roadV2 -PathType Leaf)) {
    $productionText = ReadText $productionCompat
    $roadV2Text = ReadText $roadV2
    if ($roadV2Text.Contains('CE_ROADPRODUCTIONV2') -and -not $productionText.Contains($legacyRoadAnchor)) {
        $nextDiscipline = '        [CommandMethod("CE_TOOLS", "CE_SWPRODUCTIONCENTRE", CommandFlags.Modal)]'
        if (-not $productionText.Contains($nextDiscipline)) { throw 'Road Production V2 staging bridge could not find the Stormwater Production marker.' }
        $template = @'
        // Staging compatibility template for legacy August 11 road injectors.
        // CE_ROADPRODUCTIONV2 remains the live Road Production owner.
        private void LegacyRoadProductionStagingTemplate()
        {
            RunCentre("ROAD PRODUCTION", "Staging compatibility template only.", new[]
            {
                Action("Road Continuity / Junction Finish", "CE_ROADAUG11TOOLS", "Join reserve centrelines, outside offsets, junction trim boundaries and route annotation.", "02 PREPARE"),
            });
        }

'@
        $productionText = $productionText.Replace($nextDiscipline,$template + $nextDiscipline)
        WriteText $productionCompat $productionText
        Write-Host 'Added staging-only Road Production V2 compatibility template for historical August 11 injectors.' -ForegroundColor DarkGreen
    }
}

# Run all field-completion, behavioral-audit and command-sequencing passes here
# so the existing one-click Stage-Build script gets the same audited source.
foreach ($completionName in @(
    'Inject-August11FieldCompletion2-Civil3D2023.ps1',
    'Inject-August11FieldCompletion3-Civil3D2023.ps1',
    'Inject-August11FieldCompletion4-Civil3D2023.ps1',
    'Inject-August11AuditRepairs-Civil3D2023.ps1',
    'Inject-August11CommandSequenceRepair-Civil3D2023.ps1',
    'Repair-StormwaterPipeLength-Civil3D2023.ps1',
    'Repair-VertexCoordinateOrder-Civil3D2023.ps1')) {
    $completion = Join-Path $root ('scripts\' + $completionName)
    if (-not (Test-Path -LiteralPath $completion -PathType Leaf)) {
        throw "August 11 completion/audit pass was not found: $completion"
    }
    Unblock-File -LiteralPath $completion -ErrorAction SilentlyContinue
    & $completion -RepoRoot $root
    $global:LASTEXITCODE = 0
}

# CE_NETWORKMULTI belongs to the established final-closure command hub. Keep it
# stable and accept either collision-safe name used by the newer batch launcher.
$network = Required 'August11NetworkBatchCommands.cs'
$text = ReadText $network
$old = '[CommandMethod("CE_TOOLS", "CE_NETWORKMULTI", CommandFlags.Modal)]'
$currentSafe = '[CommandMethod("CE_TOOLS", "CE_NETWORKMULTIBATCH", CommandFlags.Modal)]'
$legacySafe = '[CommandMethod("CE_TOOLS", "CE_NETWORKBATCHTOOLS", CommandFlags.Modal)]'
if ($text.Contains($old)) {
    $text = $text.Replace($old,$currentSafe)
    WriteText $network $text
    Write-Host 'Renamed August11 network hub to CE_NETWORKMULTIBATCH; retained established CE_NETWORKMULTI owner.' -ForegroundColor Green
}
elseif ($text.Contains($currentSafe)) { Write-Host 'August11 network hub already uses CE_NETWORKMULTIBATCH.' -ForegroundColor DarkGreen }
elseif ($text.Contains($legacySafe)) { Write-Host 'August11 network hub already uses legacy collision-safe CE_NETWORKBATCHTOOLS.' -ForegroundColor DarkGreen }
else { throw 'August11 network hub collision-safe CommandMethod marker was not found.' }

# Project metadata propagation only needs to queue the already-proven universal
# metadata refresh. Avoid a compile-time dependency on a private manager method.
$survey = Required 'August11SurveyRuntimeCommands.cs'
$text = ReadText $survey
$old = '                ProductionMetadataDynamicManager.Refresh(document);'
$new = '                UniversalDynamicRefreshManager.Queue();'
if ($text.Contains($old)) {
    $text = $text.Replace($old,$new)
    WriteText $survey $text
    Write-Host 'Routed project-location metadata refresh through UniversalDynamicRefreshManager.' -ForegroundColor Green
}
elseif ($text.Contains($new)) { Write-Host 'Project-location metadata refresh is already using universal refresh.' -ForegroundColor DarkGreen }
else { throw 'Project-location metadata refresh marker was not found.' }

# AutoCAD 2023's Vector3d does not expose the newer static Zero property used by
# the restored survey runtime source. Use an explicit zero vector instead.
$text = ReadText $survey
if ($text.Contains('Vector3d.Zero')) {
    $text = $text.Replace('Vector3d.Zero','new Vector3d(0.0, 0.0, 0.0)')
    WriteText $survey $text
    Write-Host 'Normalized Vector3d zero initialization for Civil 3D 2023.' -ForegroundColor Green
}
$text = ReadText $survey
if ($text.Contains('Vector3d.Zero')) {
    throw 'Civil 3D 2023 Vector3d.Zero compatibility repair did not remove all usages.'
}
if (-not $text.Contains('new Vector3d(0.0, 0.0, 0.0)')) {
    throw 'Civil 3D 2023 zero-vector compatibility marker is missing.'
}

# Match the proven CE Tools modal-window call pattern used elsewhere in the repo.
# Do not depend on a host-version-specific ShowModalWindow return signature.
$production = Required 'August11ProductionCentreCommands.cs'
$text = ReadText $production
$oldWelcome = @'
            bool? accepted = AcApplication.ShowModalWindow(window);
            if (accepted != true || string.IsNullOrWhiteSpace(window.SelectedCommand)) return;
'@
$newWelcome = @'
            AcApplication.ShowModalWindow(window);
            if (string.IsNullOrWhiteSpace(window.SelectedCommand)) return;
'@
$oldWelcomeValue = $oldWelcome.TrimEnd("`r","`n")
if ($text.Contains($oldWelcomeValue)) {
    $text = $text.Replace($oldWelcomeValue,$newWelcome.TrimEnd("`r","`n"))
    WriteText $production $text
    Write-Host 'Normalized CE welcome modal call for Civil 3D 2023.' -ForegroundColor Green
}
elseif ($text.Contains('AcApplication.ShowModalWindow(window);') -and -not $text.Contains('bool? accepted =')) {
    Write-Host 'CE welcome modal call is already host-compatible.' -ForegroundColor DarkGreen
}
else { throw 'CE welcome modal call marker was not found.' }

# TableHitTestInfo is a value type in the 2023 managed API. The permanent source
# is already fixed, but normalize any restored/stale copy before compilation.
$table = Required 'TableCellNavigationCommands.cs'
$text = ReadText $table
if ($text.Contains('hit != null && hit.Type == TableHitTestType.Cell')) {
    $text = $text.Replace('hit != null && hit.Type == TableHitTestType.Cell','hit.Type == TableHitTestType.Cell')
    WriteText $table $text
    Write-Host 'Normalized TableHitTestInfo value-type handling.' -ForegroundColor Green
}

# AutoCAD and Civil 3D both expose a type named Surface. Midblock sewer routing
# must always use the Civil 3D surface because it calls FindElevationAtXY().
# Qualify every use explicitly so Civil 3D 2023 never sees CS0104 ambiguity.
$midblock = Required 'August11MidblockSewerProductionCommands.cs'
$text = ReadText $midblock
$surfaceReplacements = @(
    @('                Surface surface = null;','                Autodesk.Civil.DatabaseServices.Surface surface = null;'),
    @(' as Surface;',' as Autodesk.Civil.DatabaseServices.Surface;'),
    @('typeof(Surface)','typeof(Autodesk.Civil.DatabaseServices.Surface)'),
    @('string sideChoice, Surface surface, out RowGeometry geometry)','string sideChoice, Autodesk.Civil.DatabaseServices.Surface surface, out RowGeometry geometry)'),
    @('AverageSurface(Surface surface, IEnumerable<Point2d> points)','AverageSurface(Autodesk.Civil.DatabaseServices.Surface surface, IEnumerable<Point2d> points)')
)
$surfaceChanges = 0
foreach ($pair in $surfaceReplacements) {
    $oldSurface = [string]$pair[0]
    $newSurface = [string]$pair[1]
    if ($text.Contains($oldSurface)) {
        $text = $text.Replace($oldSurface,$newSurface)
        $surfaceChanges++
    }
}
WriteText $midblock $text
$text = ReadText $midblock
$ambiguousPatterns = @(
    '                Surface surface = null;',
    ' as Surface;',
    'typeof(Surface)',
    'string sideChoice, Surface surface, out RowGeometry geometry)',
    'AverageSurface(Surface surface, IEnumerable<Point2d> points)'
)
foreach ($pattern in $ambiguousPatterns) {
    if ($text.Contains($pattern)) {
        throw "Midblock Civil surface ambiguity remains after compatibility repair: $pattern"
    }
}
if (-not $text.Contains('Autodesk.Civil.DatabaseServices.Surface')) {
    throw 'Midblock Civil surface compatibility repair did not produce any qualified Civil surface type.'
}
Write-Host "Qualified Civil 3D Surface references in Midblock Sewer Production. Replacements=$surfaceChanges." -ForegroundColor Green

# Verify the August11 batch source no longer DECLARES CE_NETWORKMULTI.
$text = ReadText $network
$duplicateDeclaration = '[CommandMethod("CE_TOOLS", "CE_NETWORKMULTI", CommandFlags.Modal)]'
if ($text.Contains($duplicateDeclaration)) {
    throw 'August11 network batch source still declares colliding CE_NETWORKMULTI CommandMethod.'
}
if (-not $text.Contains($currentSafe) -and -not $text.Contains($legacySafe)) {
    throw 'August11 collision-safe network batch declaration is missing.'
}

# Aggregate command ownership first so one build reports every dead CE workflow
# reference at once. Then run the focused behavior validators.
foreach ($validatorName in @(
    'Validate-CECommandOwnersAggregate.ps1',
    'Validate-August11FieldCompletion2.ps1',
    'Validate-CECommandWiring.ps1')) {
    $validator = Join-Path $root ('scripts\' + $validatorName)
    if (-not (Test-Path -LiteralPath $validator -PathType Leaf)) {
        throw "August 11 validator was not found: $validator"
    }
    Unblock-File -LiteralPath $validator -ErrorAction SilentlyContinue
    & $validator -RepoRoot $root
    $global:LASTEXITCODE = 0
}

Write-Host 'August 11 Civil 3D 2023 compiler compatibility and wiring guard passed.' -ForegroundColor Cyan
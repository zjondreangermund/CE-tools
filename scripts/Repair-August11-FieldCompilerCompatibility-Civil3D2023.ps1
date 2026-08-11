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

# CE_NETWORKMULTI already belongs to the permanent final-closure command hub.
# Keep that public command stable; expose this new source's optional hub under a
# different command so AutoCAD never receives duplicate CommandMethod names.
$network = Required 'August11NetworkBatchCommands.cs'
$text = ReadText $network
$old = '[CommandMethod("CE_TOOLS", "CE_NETWORKMULTI", CommandFlags.Modal)]'
$new = '[CommandMethod("CE_TOOLS", "CE_NETWORKBATCHTOOLS", CommandFlags.Modal)]'
if ($text.Contains($old)) {
    $text = $text.Replace($old,$new)
    WriteText $network $text
    Write-Host 'Renamed August11 network hub to CE_NETWORKBATCHTOOLS; retained established CE_NETWORKMULTI owner.' -ForegroundColor Green
}
elseif ($text.Contains($new)) { Write-Host 'August11 network hub command name is already collision-safe.' -ForegroundColor DarkGreen }
else { throw 'August11 network hub CommandMethod marker was not found.' }

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

# TableHitTestInfo is a value type in the 2023 managed API. The permanent source
# is already fixed, but normalize any restored/stale copy before compilation.
$table = Required 'TableCellNavigationCommands.cs'
$text = ReadText $table
if ($text.Contains('hit != null && hit.Type == TableHitTestType.Cell')) {
    $text = $text.Replace('hit != null && hit.Type == TableHitTestType.Cell','hit.Type == TableHitTestType.Cell')
    WriteText $table $text
    Write-Host 'Normalized TableHitTestInfo value-type handling.' -ForegroundColor Green
}

# Autodesk/Civil command registries are case-insensitive. Validate that the new
# source itself no longer declares a duplicate CE_NETWORKMULTI command.
$allAugust11 = @(
    'August11ProductionCentreCommands.cs',
    'August11NetworkBatchCommands.cs',
    'August11MidblockSewerProductionCommands.cs',
    'August11RoadCompletionCommands.cs',
    'August11SurveyRuntimeCommands.cs'
) | ForEach-Object { ReadText (Required $_) }
$combined = $allAugust11 -join "`n"
if ([regex]::Matches($combined,'"CE_NETWORKMULTI"',[System.Text.RegularExpressions.RegexOptions]::IgnoreCase).Count -gt 0) {
    throw 'August11 source still declares/references the colliding CE_NETWORKMULTI command name unexpectedly.'
}

Write-Host 'August 11 Civil 3D 2023 compiler compatibility guard passed.' -ForegroundColor Cyan

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

# Verify the August11 batch source itself no longer DECLARES CE_NETWORKMULTI.
# Other production-centre source may legitimately reference the established hub.
$text = ReadText $network
$duplicateDeclaration = '[CommandMethod("CE_TOOLS", "CE_NETWORKMULTI", CommandFlags.Modal)]'
if ($text.Contains($duplicateDeclaration)) {
    throw 'August11 network batch source still declares colliding CE_NETWORKMULTI CommandMethod.'
}
if (-not $text.Contains('[CommandMethod("CE_TOOLS", "CE_NETWORKBATCHTOOLS", CommandFlags.Modal)]')) {
    throw 'August11 collision-safe CE_NETWORKBATCHTOOLS declaration is missing.'
}

Write-Host 'August 11 Civil 3D 2023 compiler compatibility guard passed.' -ForegroundColor Cyan

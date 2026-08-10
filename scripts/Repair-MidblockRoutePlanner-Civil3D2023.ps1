[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$path = Join-Path $root 'src\CE.Tools.Civil3D\RoutePlannerExpansionCommands.cs'
if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Route planner source missing: $path" }
$text = [System.IO.File]::ReadAllText($path)
$old = 'new DisciplineWorkflowAction("Create Midblock sewer route", "CE_UTILITYROUTES", "Use the Midblock sewer centreline option to generate open preliminary centre routes through selected blocks/erfs.", "03 Utility Option 2")'
$new = 'new DisciplineWorkflowAction("Create Midblock sewer route + visible offsets", "CE_MIDBLOCKSEWERLAYOUT", "Create the open midblock sewer centre route plus both visible side-offset guides through all/selected cadastral blocks.", "03 Utility Option 2")'
if ($text.Contains($new)) {
    Write-Host 'Route planner already uses the final Midblock sewer layout.' -ForegroundColor DarkGreen
}
elseif ($text.Contains($old)) {
    $text = $text.Replace($old,$new)
    [System.IO.File]::WriteAllText($path,$text,[System.Text.UTF8Encoding]::new($false))
    Write-Host 'Updated Route Planner Option 2 to Midblock centre + visible offsets.' -ForegroundColor Green
}
else {
    throw 'Could not find the Midblock Route Planner action marker.'
}
if (-not ([System.IO.File]::ReadAllText($path)).Contains('"CE_MIDBLOCKSEWERLAYOUT"')) { throw 'Midblock route-planner verification failed.' }
Write-Host 'Midblock route-planner repair passed.' -ForegroundColor Cyan

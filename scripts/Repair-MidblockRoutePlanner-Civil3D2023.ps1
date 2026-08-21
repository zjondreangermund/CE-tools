[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$path = Join-Path $root 'src\CE.Tools.Civil3D\RoutePlannerExpansionCommands.cs'
if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Route planner source missing: $path" }

$text = [System.IO.File]::ReadAllText($path)
$finalToken = '"CE_MIDBLOCKSEWERLAYOUT"'

if ($text.Contains($finalToken)) {
    Write-Host 'Route planner already uses the final Midblock sewer layout.' -ForegroundColor DarkGreen
}
else {
    # Historical recovery/sanitizer passes can restore either of the retired
    # Route Planner Option 2 command targets. Repair only the Midblock action's
    # command argument so unrelated workflow actions are never changed.
    $pattern = '(?i)(new\s+DisciplineWorkflowAction\(\s*"[^"]*Midblock sewer route[^"]*"\s*,\s*)"CE_(?:UTILITYROUTES|MIDBLOCKSEWERPRODUCTION)"'
    $routeAction = [regex]::new($pattern)
    if (-not $routeAction.IsMatch($text)) {
        throw 'Could not find a supported Midblock Route Planner action marker after staged recovery repairs.'
    }

    $text = $routeAction.Replace($text, '$1"CE_MIDBLOCKSEWERLAYOUT"', 1)
    [System.IO.File]::WriteAllText($path,$text,[System.Text.UTF8Encoding]::new($false))
    Write-Host 'Updated Route Planner Option 2 to the final Midblock centre + visible-offset command.' -ForegroundColor Green
}

$verified = [System.IO.File]::ReadAllText($path)
if (-not $verified.Contains($finalToken)) {
    throw 'Midblock route-planner verification failed.'
}
if ($verified -match '(?i)new\s+DisciplineWorkflowAction\(\s*"[^"]*Midblock sewer route[^"]*"\s*,\s*"CE_(?:UTILITYROUTES|MIDBLOCKSEWERPRODUCTION)"') {
    throw 'Midblock route-planner verification failed: a retired Option 2 command target remains.'
}

Write-Host 'Midblock route-planner repair passed.' -ForegroundColor Cyan

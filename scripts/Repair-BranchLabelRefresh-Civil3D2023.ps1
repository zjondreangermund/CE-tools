[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$path = Join-Path $root 'src\CE.Tools.Civil3D\UniversalDynamicRefreshCommands.cs'
if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Universal refresh source missing: $path" }
$text = [System.IO.File]::ReadAllText($path)

$old = @'
                try { SewerPlanLabelRuntimeManager.Apply(document); }
                catch { result.Warnings++; }
                try { ProfileViewBandRuntimeManager.RefreshAll(document); }
'@
$new = @'
                try { SewerPlanLabelRuntimeManager.Apply(document); }
                catch { result.Warnings++; }
                try { BranchLabelLayerRuntime.Apply(document); }
                catch { result.Warnings++; }
                try { ProfileViewBandRuntimeManager.RefreshAll(document); }
'@
if ($text.Contains($new)) {
    Write-Host 'Branch label auto-layer refresh is already integrated.' -ForegroundColor DarkGreen
}
elseif ($text.Contains($old)) {
    $text = $text.Replace($old, $new)
    Write-Host 'Integrated dedicated branch-label layer into universal linked refresh.' -ForegroundColor Green
}
else {
    throw 'Could not find the universal refresh branch-label insertion marker.'
}

if (-not $text.Contains('BranchLabelLayerRuntime.Apply(document);')) { throw 'Branch label refresh verification failed.' }
[System.IO.File]::WriteAllText($path, $text, [System.Text.UTF8Encoding]::new($false))
Write-Host 'Branch-label refresh compatibility repair passed.' -ForegroundColor Cyan

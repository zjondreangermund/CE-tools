[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$path = Join-Path $root 'src\CE.Tools.Civil3D\August18DynamicGridSettingOutCommands.cs'
if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
    throw "August 23 setting-out idempotence prerequisite missing: $path"
}
$utf8 = New-Object System.Text.UTF8Encoding($false)
$text = [System.IO.File]::ReadAllText($path) -replace "`r?`n","`r`n"
$linePattern = '(?m)^(\s*SyncGridLines\(document\.Database, transaction, sources, link\); // CE_AUG23_GRID_SYNC\r\n)(?:\s*SyncGridLines\(document\.Database, transaction, sources, link\); // CE_AUG23_GRID_SYNC\r\n)+'
$text = [regex]::Replace($text,$linePattern,'$1')
[System.IO.File]::WriteAllText($path,$text,$utf8)
$count = ([regex]::Matches($text,'SyncGridLines\(document\.Database, transaction, sources, link\); // CE_AUG23_GRID_SYNC')).Count
if ($count -ne 2) {
    throw "August 23 dynamic-grid synchronization expected exactly two create/refresh calls after normalization; found $count."
}
Write-Host 'August 23 setting-out finalizer idempotence normalized.' -ForegroundColor Green

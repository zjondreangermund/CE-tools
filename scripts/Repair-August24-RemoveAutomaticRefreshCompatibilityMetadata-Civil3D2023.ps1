[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$path = Join-Path $root 'src\CE.Tools.Civil3D\AugustAutomaticRefreshManager.cs'
if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
    throw "August 24 automatic-refresh compatibility source missing: $path"
}

$text = [System.IO.File]::ReadAllText($path) -replace "`r?`n", "`r`n"
$marker = 'August 18 staged-repair compatibility anchor.'
$markerIndex = $text.IndexOf($marker,[StringComparison]::Ordinal)
if ($markerIndex -ge 0) {
    $commentStart = $text.LastIndexOf('/*',$markerIndex,[StringComparison]::Ordinal)
    $commentEnd = $text.IndexOf('*/',$markerIndex,[StringComparison]::Ordinal)
    if ($commentStart -lt 0 -or $commentEnd -lt 0 -or $commentEnd -lt $commentStart) {
        throw 'August 24 automatic-refresh compatibility metadata block is malformed.'
    }
    $removeEnd = $commentEnd + 2
    while ($removeEnd -lt $text.Length -and ($text[$removeEnd] -eq "`r" -or $text[$removeEnd] -eq "`n")) {
        $removeEnd++
    }
    $text = $text.Remove($commentStart,$removeEnd - $commentStart)
}

if ($text.Contains($marker)) {
    throw 'August 24 automatic-refresh compatibility metadata was not removed.'
}
if (-not $text.Contains('internal static bool ShouldQueueRefresh(string commandName)')) {
    throw 'August 24 automatic-refresh live policy was unexpectedly removed.'
}
foreach ($required in @('"CE_SITEGRID"','"CE_SITEGRIDREFRESH"','"CE_SITEGRIDREMOVE"','"PRODUCTIONCENTRE"','"WORKFLOW"','"SETTINGS"')) {
    if (-not $text.Contains($required)) {
        throw "August 24 automatic-refresh live policy marker missing after metadata cleanup: $required"
    }
}

[System.IO.File]::WriteAllText($path,$text,(New-Object System.Text.UTF8Encoding($false)))
Write-Host 'August 24 automatic-refresh compatibility metadata removed before the source-only finalizer.' -ForegroundColor Green
Write-Host 'Live Site Grid and non-mutating launcher exclusions remain intact.' -ForegroundColor Green

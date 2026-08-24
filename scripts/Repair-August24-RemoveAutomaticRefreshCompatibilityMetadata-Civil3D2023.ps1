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

$policyMarker = '        internal static bool ShouldQueueRefresh(string commandName)'
$policyStart = $text.IndexOf($policyMarker,[StringComparison]::Ordinal)
if ($policyStart -lt 0) {
    throw 'August 24 automatic-refresh live policy was unexpectedly removed.'
}
$policyOpen = $text.IndexOf('{',$policyStart)
if ($policyOpen -lt 0) {
    throw 'August 24 automatic-refresh live policy opening brace was not found.'
}
$depth = 0
$policyClose = -1
for ($i = $policyOpen; $i -lt $text.Length; $i++) {
    if ($text[$i] -eq '{') { $depth++ }
    elseif ($text[$i] -eq '}') {
        $depth--
        if ($depth -eq 0) { $policyClose = $i; break }
    }
}
if ($policyClose -lt 0) {
    throw 'August 24 automatic-refresh live policy closing brace was not found.'
}
$policyBody = $text.Substring($policyOpen + 1,$policyClose - $policyOpen - 1)
if (-not [regex]::IsMatch($policyBody,'(?m)^\s*return\s+false\s*;\s*$')) {
    throw 'August 24 automatic-refresh live policy no longer blocks blanket command-ended refresh.'
}
if ([regex]::IsMatch($policyBody,'(?m)^\s*return\s+true\s*;\s*$')) {
    throw 'August 24 automatic-refresh live policy can still enable blanket command-ended refresh.'
}

[System.IO.File]::WriteAllText($path,$text,(New-Object System.Text.UTF8Encoding($false)))
Write-Host 'August 24 automatic-refresh compatibility metadata removed before the source-only finalizer.' -ForegroundColor Green
Write-Host 'Live policy still blocks blanket command-ended universal/platform refresh for every command.' -ForegroundColor Green

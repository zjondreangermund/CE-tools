[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$path = Join-Path $root 'src\CE.Tools.Civil3D\PluginEntry.cs'
if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
    throw "PluginEntry source missing for August 21 manager compatibility: $path"
}

$utf8 = New-Object System.Text.UTF8Encoding($false)
$text = [System.IO.File]::ReadAllText($path) -replace "`r?`n", "`r`n"

function Normalize-MethodManagers(
    [string]$source,
    [string]$signature,
    [string]$parkingCall,
    [string[]]$august21Calls,
    [string]$label) {

    $start = $source.IndexOf($signature,[StringComparison]::Ordinal)
    if ($start -lt 0) { throw "August 21 plugin manager compatibility method missing: $label" }
    $second = $source.IndexOf($signature,$start + $signature.Length,[StringComparison]::Ordinal)
    if ($second -ge 0) { throw "August 21 plugin manager compatibility method ambiguous: $label" }
    $open = $source.IndexOf('{',$start)
    if ($open -lt 0) { throw "August 21 plugin manager compatibility opening brace missing: $label" }
    $depth = 0
    $close = -1
    for ($i=$open; $i -lt $source.Length; $i++) {
        if ($source[$i] -eq '{') { $depth++ }
        elseif ($source[$i] -eq '}') {
            $depth--
            if ($depth -eq 0) { $close = $i; break }
        }
    }
    if ($close -lt 0) { throw "August 21 plugin manager compatibility closing brace missing: $label" }

    $body = $source.Substring($open + 1,$close - $open - 1)
    foreach ($call in $august21Calls) {
        $pattern = '(?m)^\s*' + [regex]::Escape($call) + '\s*$\r?\n?'
        $body = [regex]::Replace($body,$pattern,'')
    }

    $parkingPattern = '(?m)^\s*' + [regex]::Escape($parkingCall) + '\s*$'
    $parkingMatches = [regex]::Matches($body,$parkingPattern)
    if ($parkingMatches.Count -gt 1) {
        throw "August 21 plugin manager compatibility found duplicate parking manager calls in $label."
    }

    if ($parkingMatches.Count -eq 1) {
        $replacementLines = @('            ' + $parkingCall)
        foreach ($call in $august21Calls) { $replacementLines += ('            ' + $call) }
        $replacement = $replacementLines -join "`r`n"
        $body = [regex]::Replace($body,$parkingPattern,$replacement,1)
    }
    else {
        $insertLines = @('            ' + $parkingCall)
        foreach ($call in $august21Calls) { $insertLines += ('            ' + $call) }
        $insert = "`r`n" + ($insertLines -join "`r`n")
        $body = $insert + $body
    }

    return $source.Substring(0,$open + 1) + $body + $source.Substring($close)
}

$text = Normalize-MethodManagers `
    $text `
    '        public void Initialize()' `
    'ParkingOptionAutoRefreshManager.Initialize();' `
    @('August21SimpleParkingRefreshManager.Initialize();','August21GraphicsRefreshManager.Initialize();') `
    'Plugin Initialize'

$text = Normalize-MethodManagers `
    $text `
    '        public void Terminate()' `
    'ParkingOptionAutoRefreshManager.Terminate();' `
    @('August21GraphicsRefreshManager.Terminate();','August21SimpleParkingRefreshManager.Terminate();') `
    'Plugin Terminate'

[System.IO.File]::WriteAllText($path,$text,$utf8)

$check = [System.IO.File]::ReadAllText($path)
$initBlock = @'
            ParkingOptionAutoRefreshManager.Initialize();
            August21SimpleParkingRefreshManager.Initialize();
            August21GraphicsRefreshManager.Initialize();
'@ -replace "`r?`n","`r`n"
$termBlock = @'
            ParkingOptionAutoRefreshManager.Terminate();
            August21GraphicsRefreshManager.Terminate();
            August21SimpleParkingRefreshManager.Terminate();
'@ -replace "`r?`n","`r`n"

# The historical August 21 pass expects the terminate calls in Graphics/Simple/Parking
# order. Reorder the canonical terminate block accordingly if necessary.
$termExpected = @'
            August21GraphicsRefreshManager.Terminate();
            August21SimpleParkingRefreshManager.Terminate();
            ParkingOptionAutoRefreshManager.Terminate();
'@ -replace "`r?`n","`r`n"
if ($check.Contains($termBlock)) {
    $check = $check.Replace($termBlock,$termExpected)
    [System.IO.File]::WriteAllText($path,$check,$utf8)
}

$check = [System.IO.File]::ReadAllText($path)
if (-not $check.Contains($initBlock)) {
    throw 'August 21 plugin manager compatibility did not create the canonical Initialize manager block.'
}
if (-not $check.Contains($termExpected)) {
    throw 'August 21 plugin manager compatibility did not create the canonical Terminate manager block.'
}
foreach ($call in @(
    'August21SimpleParkingRefreshManager.Initialize();',
    'August21GraphicsRefreshManager.Initialize();',
    'August21GraphicsRefreshManager.Terminate();',
    'August21SimpleParkingRefreshManager.Terminate();')) {
    if (([regex]::Matches($check,[regex]::Escape($call))).Count -ne 1) {
        throw "August 21 plugin manager compatibility expected exactly one call: $call"
    }
}

Write-Host 'Plugin August21 manager initialization/termination normalized for the state-safety pass.' -ForegroundColor Green

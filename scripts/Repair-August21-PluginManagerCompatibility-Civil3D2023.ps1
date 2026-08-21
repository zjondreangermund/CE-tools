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

function Normalize-ManagerBlock(
    [string]$source,
    [string]$signature,
    [string]$parkingCall,
    [string[]]$removeCalls,
    [string[]]$canonicalCalls,
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
    foreach ($call in $removeCalls) {
        $pattern = '(?m)^\s*' + [regex]::Escape($call) + '\s*$\r?\n?'
        $body = [regex]::Replace($body,$pattern,'')
    }

    $parkingPattern = '(?m)^\s*' + [regex]::Escape($parkingCall) + '\s*$'
    $parkingMatches = [regex]::Matches($body,$parkingPattern)
    if ($parkingMatches.Count -gt 1) {
        throw "August 21 plugin manager compatibility found duplicate parking-manager calls in $label."
    }

    $canonical = ($canonicalCalls | ForEach-Object { '            ' + $_ }) -join "`r`n"
    if ($parkingMatches.Count -eq 1) {
        $body = [regex]::Replace($body,$parkingPattern,$canonical,1)
    }
    else {
        # Some late recovery/sanitizer variants can remove or wrap the original
        # ParkingOption manager call. Re-establish the complete canonical manager
        # block at method entry instead of depending on one historical text anchor.
        $body = "`r`n" + $canonical + $body
    }

    return $source.Substring(0,$open + 1) + $body + $source.Substring($close)
}

$initRemove = @(
    'August21SimpleParkingRefreshManager.Initialize();',
    'August21GraphicsRefreshManager.Initialize();')
$initCanonical = @(
    'ParkingOptionAutoRefreshManager.Initialize();',
    'August21SimpleParkingRefreshManager.Initialize();',
    'August21GraphicsRefreshManager.Initialize();')
$text = Normalize-ManagerBlock `
    $text `
    '        public void Initialize()' `
    'ParkingOptionAutoRefreshManager.Initialize();' `
    $initRemove `
    $initCanonical `
    'Plugin Initialize'

$termRemove = @(
    'August21GraphicsRefreshManager.Terminate();',
    'August21SimpleParkingRefreshManager.Terminate();')
$termCanonical = @(
    'August21GraphicsRefreshManager.Terminate();',
    'August21SimpleParkingRefreshManager.Terminate();',
    'ParkingOptionAutoRefreshManager.Terminate();')
$text = Normalize-ManagerBlock `
    $text `
    '        public void Terminate()' `
    'ParkingOptionAutoRefreshManager.Terminate();' `
    $termRemove `
    $termCanonical `
    'Plugin Terminate'

[System.IO.File]::WriteAllText($path,$text,$utf8)

$check = [System.IO.File]::ReadAllText($path)
$initExpected = @'
            ParkingOptionAutoRefreshManager.Initialize();
            August21SimpleParkingRefreshManager.Initialize();
            August21GraphicsRefreshManager.Initialize();
'@ -replace "`r?`n","`r`n"
$termExpected = @'
            August21GraphicsRefreshManager.Terminate();
            August21SimpleParkingRefreshManager.Terminate();
            ParkingOptionAutoRefreshManager.Terminate();
'@ -replace "`r?`n","`r`n"

if (-not $check.Contains($initExpected)) {
    throw 'August 21 plugin manager compatibility did not create the exact Initialize block required by the state-safety pass.'
}
if (-not $check.Contains($termExpected)) {
    throw 'August 21 plugin manager compatibility did not create the exact Terminate block required by the state-safety pass.'
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

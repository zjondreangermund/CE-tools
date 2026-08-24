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

if (-not $text.Contains('private static void SyncGridLines(')) {
    throw 'August 23 dynamic-grid SyncGridLines helper is missing before idempotence normalization.'
}

function Normalize-SyncCall(
    [string]$source,
    [string]$methodMarker,
    [string]$nextMethodMarker,
    [string]$label) {

    $methodStart = $source.IndexOf($methodMarker,[StringComparison]::Ordinal)
    if ($methodStart -lt 0) {
        throw "August 23 dynamic-grid $label method marker missing: $methodMarker"
    }
    $methodEnd = $source.IndexOf($nextMethodMarker,$methodStart + $methodMarker.Length,[StringComparison]::Ordinal)
    if ($methodEnd -lt 0) {
        throw "August 23 dynamic-grid $label next-method marker missing: $nextMethodMarker"
    }

    $method = $source.Substring($methodStart,$methodEnd-$methodStart)

    # Historical staging passes can remove the CE_AUG23_GRID_SYNC comment, change
    # whitespace, or duplicate the call. Remove every semantic variant inside this
    # one method, then place exactly one canonical call immediately before the
    # method's linked-table PopulateTable call.
    $syncPattern = '(?m)^[ \t]*SyncGridLines\s*\(\s*document\.Database\s*,\s*transaction\s*,\s*sources\s*,\s*link\s*\)\s*;\s*(?://\s*CE_AUG23_GRID_SYNC)?[ \t]*(?:\r\n)?'
    $method = [regex]::Replace($method,$syncPattern,'')

    $populateMatches = [regex]::Matches($method,'(?m)^(?<indent>[ \t]*)PopulateTable\s*\(')
    if ($populateMatches.Count -ne 1) {
        throw "August 23 dynamic-grid $label expected exactly one PopulateTable call; found $($populateMatches.Count)."
    }

    $populate = $populateMatches[0]
    $indent = $populate.Groups['indent'].Value
    $canonical = $indent + 'SyncGridLines(document.Database, transaction, sources, link); // CE_AUG23_GRID_SYNC' + "`r`n"
    $method = $method.Insert($populate.Index,$canonical)

    return $source.Substring(0,$methodStart) + $method + $source.Substring($methodEnd)
}

$text = Normalize-SyncCall `
    $text `
    '        private static ObjectId CreateGroup(' `
    '        private static void RefreshOne(' `
    'CreateGroup'

$text = Normalize-SyncCall `
    $text `
    '        private static void RefreshOne(' `
    '        private static ObjectId CreateCogo(' `
    'RefreshOne'

[System.IO.File]::WriteAllText($path,$text,$utf8)

$semanticPattern = 'SyncGridLines\s*\(\s*document\.Database\s*,\s*transaction\s*,\s*sources\s*,\s*link\s*\)\s*;'
$count = ([regex]::Matches($text,$semanticPattern)).Count
$canonicalCount = ([regex]::Matches($text,'SyncGridLines\(document\.Database, transaction, sources, link\); // CE_AUG23_GRID_SYNC')).Count
if ($count -ne 2 -or $canonicalCount -ne 2) {
    throw "August 23 dynamic-grid synchronization expected exactly two canonical create/refresh calls after normalization; semantic=$count canonical=$canonicalCount."
}

Write-Host 'August 23 setting-out finalizer idempotence normalized: CreateGroup/RefreshOne each have one Grid sync call.' -ForegroundColor Green

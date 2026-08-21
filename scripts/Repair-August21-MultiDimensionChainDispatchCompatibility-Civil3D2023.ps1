[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$path = Join-Path $root 'src\CE.Tools.Civil3D\MultiDimensionCommands.cs'
if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
    throw "Multiple Dimensions source missing for August 21 chain-dispatch compatibility: $path"
}

$utf8 = New-Object System.Text.UTF8Encoding($false)
$text = [System.IO.File]::ReadAllText($path) -replace "`r?`n", "`r`n"

# If the later August 21 finalizer has already run, this compatibility step is
# intentionally a no-op. This makes repeated staged builds idempotent.
if ($text.Contains('DimensionOpenPolylineChain(') -and
    $text.Contains('string.Equals(mode, chain, StringComparison.OrdinalIgnoreCase)')) {
    Write-Host 'Multi Dimensions chain dispatch is already installed; compatibility normalization skipped.' -ForegroundColor Green
    exit 0
}

$legacyDispatchAnchor = @'
            double leader = PaperAnnotationScale.ModelDistance(
                document.Database,
                settings.Double("ArcLeader", 6.0));

            int sources = 0;
'@ -replace "`r?`n", "`r`n"

# The August 20 circle/unit/property pass inserts these declarations directly
# after leader. The preserved August 21 chain finalizer was authored against the
# pre-unit layout and therefore expects `int sources` immediately after leader.
# Move only the declarations (not the feature) to a later pre-transaction point.
$unitPattern = '(?ms)^\s{12}bool outputMillimetres = string\.Equals\(\r?\n' +
    '\s{16}settings\.Text\("ValueUnits"\),\r?\n' +
    '\s{16}"Millimetres",\r?\n' +
    '\s{16}StringComparison\.OrdinalIgnoreCase\);\r?\n' +
    '\s{12}double measurementFactor = outputMillimetres \? 1000\.0 : 1\.0;\r?\n'
$unitMatches = [regex]::Matches($text,$unitPattern)
if ($unitMatches.Count -gt 1) {
    throw "August 21 Multi Dimensions compatibility found duplicate unit-factor declaration blocks: $($unitMatches.Count)."
}

if ($unitMatches.Count -eq 1) {
    $unitBlock = $unitMatches[0].Value.TrimEnd("`r","`n")
    $text = $text.Remove($unitMatches[0].Index,$unitMatches[0].Length)

    $outputStyleMarker = '            string outputStyleName = string.Empty;'
    $outputMatches = [regex]::Matches($text,[regex]::Escape($outputStyleMarker))
    if ($outputMatches.Count -ne 1) {
        throw "August 21 Multi Dimensions compatibility expected one output-style marker; found $($outputMatches.Count)."
    }
    $insertAt = $outputMatches[0].Index + $outputMatches[0].Length
    $text = $text.Insert($insertAt,"`r`n" + $unitBlock)
}

if (-not $text.Contains($legacyDispatchAnchor)) {
    throw 'August 21 Multi Dimensions compatibility could not restore the chain-dispatch anchor after preserving unit declarations.'
}

# If the August 20 unit feature was present, verify it is still present exactly
# once and still occurs before the transaction where measurementFactor is used.
if ($unitMatches.Count -eq 1) {
    foreach ($marker in @(
        'bool outputMillimetres = string.Equals(',
        'double measurementFactor = outputMillimetres ? 1000.0 : 1.0;')) {
        if (([regex]::Matches($text,[regex]::Escape($marker))).Count -ne 1) {
            throw "August 21 Multi Dimensions compatibility did not preserve exactly one unit marker: $marker"
        }
    }
    $factorIndex = $text.IndexOf('double measurementFactor = outputMillimetres ? 1000.0 : 1.0;',[StringComparison]::Ordinal)
    $tryIndex = $text.IndexOf('            try',[Math]::Max(0,$factorIndex),[StringComparison]::Ordinal)
    if ($factorIndex -lt 0 -or $tryIndex -lt 0 -or $factorIndex -gt $tryIndex) {
        throw 'August 21 Multi Dimensions compatibility moved the measurement factor outside the safe pre-transaction scope.'
    }
}

[System.IO.File]::WriteAllText($path,$text,$utf8)
Write-Host 'Multi Dimensions unit declarations normalized for the August 21 chain-dispatch finalizer.' -ForegroundColor Green

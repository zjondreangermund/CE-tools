[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$path = Join-Path $root 'src\CE.Tools.Civil3D\VertexSettingOutCommands.cs'
if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
    throw "August 23 Vertex Setting-Out source missing for label-guard preflight: $path"
}

$utf8 = New-Object System.Text.UTF8Encoding($false)
$text = [System.IO.File]::ReadAllText($path) -replace "`r?`n", "`r`n"

function Resolve-MethodRange(
    [string]$source,
    [string]$methodMarker,
    [string]$nextMethodMarker,
    [string]$label) {

    $methodStart = $source.IndexOf($methodMarker,[StringComparison]::Ordinal)
    if ($methodStart -lt 0) {
        throw "August 23 Vertex $label method marker missing: $methodMarker"
    }
    $methodEnd = $source.IndexOf($nextMethodMarker,$methodStart + $methodMarker.Length,[StringComparison]::Ordinal)
    if ($methodEnd -lt 0) {
        throw "August 23 Vertex $label next-method marker missing: $nextMethodMarker"
    }
    return [pscustomobject]@{ Start = $methodStart; End = $methodEnd }
}

function Ensure-ResetCall(
    [string]$source,
    [string]$methodMarker,
    [string]$nextMethodMarker,
    [string]$variableName,
    [string]$presence,
    [string]$label) {

    $range = Resolve-MethodRange $source $methodMarker $nextMethodMarker $label
    $methodText = $source.Substring($range.Start,$range.End-$range.Start)
    if ($methodText.Contains($presence)) { return $source }

    $escaped = [regex]::Escape($variableName)

    # Prefer the PointName assignment so the historical August 23 regex sees the
    # reset at the same semantic anchor. Some packaged stages remove or rewrite
    # that assignment, so fall back to RawDescription, which is the stable
    # source-linked COGO anchor.
    $patterns = @(
        ('(?m)^(?<indent>[ \t]*)try[ \t]*\{[ \t]*' + $escaped + '\.PointName[ \t]*=[ \t]*record\.PointName;[ \t]*\}[ \t]*catch[ \t]*\{[ \t]*\}[ \t]*$'),
        ('(?m)^(?<indent>[ \t]*)' + $escaped + '\.RawDescription[ \t]*=[ \t]*record\.PointName;[ \t]*$')
    )

    foreach ($pattern in $patterns) {
        $match = [regex]::Match($methodText,$pattern)
        if (-not $match.Success) { continue }

        $insertAt = $range.Start + $match.Index + $match.Length
        $indent = $match.Groups['indent'].Value
        $call = "`r`n" + $indent + 'ResetCogoLabel(' + $variableName + ');'
        return $source.Insert($insertAt,$call)
    }

    throw "August 23 Vertex $label could not find PointName or RawDescription anchor for $variableName."
}

function Normalize-ResetCallCount(
    [string]$source,
    [string]$methodMarker,
    [string]$nextMethodMarker,
    [string]$variableName,
    [string]$label) {

    $range = Resolve-MethodRange $source $methodMarker $nextMethodMarker $label
    $methodText = $source.Substring($range.Start,$range.End-$range.Start)
    $token = 'ResetCogoLabel(' + $variableName + ');'
    $indices = New-Object System.Collections.Generic.List[int]
    $search = 0
    while ($true) {
        $index = $methodText.IndexOf($token,$search,[StringComparison]::Ordinal)
        if ($index -lt 0) { break }
        $indices.Add($index)
        $search = $index + $token.Length
    }
    if ($indices.Count -eq 0) {
        throw "August 23 Vertex $label reset call disappeared during normalization: $token"
    }
    for ($i = $indices.Count - 1; $i -ge 1; $i--) {
        $source = $source.Remove($range.Start + $indices[$i],$token.Length)
    }
    return $source
}

$text = Ensure-ResetCall `
    $text `
    '        private static ObjectId CreateOutput(' `
    '        private static bool UpdateOutput(' `
    'point' `
    'ResetCogoLabel(point);' `
    'created COGO label reset'

$text = Ensure-ResetCall `
    $text `
    '        private static bool UpdateOutput(' `
    '        private static ObjectId CreateDimension(' `
    'cogo' `
    'ResetCogoLabel(cogo);' `
    'updated COGO label reset'

# The historical repair's trailing \s* negative lookahead can backtrack and add a
# second reset call on a later finalizer pass. Normalize each method to one semantic
# call so both pre- and post-repair execution remain deterministic.
$text = Normalize-ResetCallCount `
    $text `
    '        private static ObjectId CreateOutput(' `
    '        private static bool UpdateOutput(' `
    'point' `
    'created COGO label reset'
$text = Normalize-ResetCallCount `
    $text `
    '        private static bool UpdateOutput(' `
    '        private static ObjectId CreateDimension(' `
    'cogo' `
    'updated COGO label reset'

foreach ($required in @('ResetCogoLabel(point);','ResetCogoLabel(cogo);')) {
    if (-not $text.Contains($required)) {
        throw "August 23 Vertex label-guard preflight failed to establish: $required"
    }
}

[System.IO.File]::WriteAllText($path,$text,$utf8)
Write-Host 'August 23 Vertex COGO label-reset guards normalized semantically.' -ForegroundColor Green

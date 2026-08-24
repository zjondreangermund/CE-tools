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

function Ensure-ResetCall(
    [string]$source,
    [string]$methodMarker,
    [string]$nextMethodMarker,
    [string]$variableName,
    [string]$presence,
    [string]$label) {

    if ($source.Contains($presence)) { return $source }

    $methodStart = $source.IndexOf($methodMarker,[StringComparison]::Ordinal)
    if ($methodStart -lt 0) {
        throw "August 23 Vertex $label method marker missing: $methodMarker"
    }
    $methodEnd = $source.IndexOf($nextMethodMarker,$methodStart + $methodMarker.Length,[StringComparison]::Ordinal)
    if ($methodEnd -lt 0) {
        throw "August 23 Vertex $label next-method marker missing: $nextMethodMarker"
    }

    $methodText = $source.Substring($methodStart,$methodEnd-$methodStart)
    $escaped = [regex]::Escape($variableName)

    # Prefer the PointName assignment so the historical August 23 regex sees the
    # reset immediately after the same semantic anchor and therefore stays
    # idempotent. Some packaged stages remove or rewrite that assignment, so fall
    # back to RawDescription, which is the stable source-linked COGO anchor.
    $patterns = @(
        ('(?m)^(?<indent>[ \t]*)try[ \t]*\{[ \t]*' + $escaped + '\.PointName[ \t]*=[ \t]*record\.PointName;[ \t]*\}[ \t]*catch[ \t]*\{[ \t]*\}[ \t]*$'),
        ('(?m)^(?<indent>[ \t]*)' + $escaped + '\.RawDescription[ \t]*=[ \t]*record\.PointName;[ \t]*$')
    )

    foreach ($pattern in $patterns) {
        $match = [regex]::Match($methodText,$pattern)
        if (-not $match.Success) { continue }

        $insertAt = $methodStart + $match.Index + $match.Length
        $indent = $match.Groups['indent'].Value
        $call = "`r`n" + $indent + 'ResetCogoLabel(' + $variableName + ');'
        return $source.Insert($insertAt,$call)
    }

    throw "August 23 Vertex $label could not find PointName or RawDescription anchor for $variableName."
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

foreach ($required in @('ResetCogoLabel(point);','ResetCogoLabel(cogo);')) {
    if (-not $text.Contains($required)) {
        throw "August 23 Vertex label-guard preflight failed to establish: $required"
    }
}

[System.IO.File]::WriteAllText($path,$text,$utf8)
Write-Host 'August 23 Vertex COGO label-reset guards normalized semantically.' -ForegroundColor Green

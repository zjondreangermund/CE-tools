[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$path = Join-Path $root 'src\CE.Tools.Civil3D\August18DynamicGridSettingOutCommands.cs'
if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
    throw "August 23 Grid Setting-Out source missing for anchor preflight: $path"
}

$utf8 = New-Object System.Text.UTF8Encoding($false)
$text = [System.IO.File]::ReadAllText($path) -replace "`r?`n", "`r`n"

# A fully repaired staged source needs no compatibility normalization.
if ($text.Contains('"GridLines", "01 Grid"')) {
    Write-Host 'August 23 Grid line settings already present; anchor preflight skipped.' -ForegroundColor Green
    exit 0
}

$methodMarker = '        public void CreateDynamicGrid()'
$nextMethodMarker = '        public void RefreshDynamicGrids()'
$methodStart = $text.IndexOf($methodMarker,[StringComparison]::Ordinal)
if ($methodStart -lt 0) {
    throw 'August 23 Grid CreateDynamicGrid method marker missing for setting-anchor preflight.'
}
$methodEnd = $text.IndexOf($nextMethodMarker,$methodStart + $methodMarker.Length,[StringComparison]::Ordinal)
if ($methodEnd -lt 0) {
    throw 'August 23 Grid RefreshDynamicGrids marker missing for setting-anchor preflight.'
}

$methodText = $text.Substring($methodStart,$methodEnd-$methodStart)
$prefixToken = '"Prefix"'
$prefixRelative = $methodText.IndexOf($prefixToken,[StringComparison]::Ordinal)
if ($prefixRelative -lt 0) {
    throw 'August 23 Grid Prefix setting missing inside CreateDynamicGrid.'
}
if ($methodText.IndexOf($prefixToken,$prefixRelative+$prefixToken.Length,[StringComparison]::Ordinal) -ge 0) {
    throw 'August 23 Grid Prefix setting is ambiguous inside CreateDynamicGrid.'
}

$prefixAbsolute = $methodStart + $prefixRelative
$addTextMarker = '            settings.AddText('
$addTextStart = $text.LastIndexOf($addTextMarker,$prefixAbsolute,[StringComparison]::Ordinal)
if ($addTextStart -lt $methodStart) {
    throw 'August 23 Grid Prefix AddText call could not be resolved.'
}
$callEnd = $text.IndexOf(');',$prefixAbsolute,[StringComparison]::Ordinal)
if ($callEnd -lt 0 -or $callEnd -ge $methodEnd) {
    throw 'August 23 Grid Prefix AddText closing marker could not be resolved.'
}
$callEnd += 2

# Earlier staged repairs may change the numbering group/title formatting around the
# Prefix setting. Rebuild only this one settings call into the historical shape that
# the larger August 23 feedback repair expects. The setting key/default/description
# remain semantically identical; unrelated settings and declarations are untouched.
$canonical = @'
            settings.AddText(
                "Prefix", "02 Numbering", "Point prefix", "G",
                "CE logical names are stored in Raw Description and the linked table so background refresh never triggers Civil 3D duplicate Point Name dialogs.");
'@ -replace "`r?`n", "`r`n"
$canonical = $canonical.TrimEnd("`r","`n")
$text = $text.Substring(0,$addTextStart) + $canonical + $text.Substring($callEnd)

$requiredAnchor = @'
            settings.AddText(
                "Prefix", "02 Numbering", "Point prefix", "G",
'@ -replace "`r?`n", "`r`n"
if (-not $text.Contains($requiredAnchor)) {
    throw 'August 23 Grid Prefix anchor normalization failed.'
}

[System.IO.File]::WriteAllText($path,$text,$utf8)
Write-Host 'August 23 Grid line setting insertion anchor normalized semantically.' -ForegroundColor Green

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

# A fully repaired staged source needs no compatibility normalization. Return from
# this script only; never terminate the parent pre-build PowerShell process.
if ($text.Contains('"GridLines", "01 Grid"')) {
    Write-Host 'August 23 Grid line settings already present; anchor preflight skipped.' -ForegroundColor Green
    return
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

# Locate the semantic Prefix *settings.AddText call*, not every later settings.Text
# reference to the Prefix key in the link initializer. Earlier staged builds can
# reformat this call, so scan AddText calls inside CreateDynamicGrid and select the
# unique one that contains the Prefix setting key.
$prefixToken = '"Prefix"'
$addTextMarker = '            settings.AddText('
$candidates = [System.Collections.Generic.List[object]]::new()
$search = $methodStart
while ($true) {
    $start = $text.IndexOf($addTextMarker,$search,[StringComparison]::Ordinal)
    if ($start -lt 0 -or $start -ge $methodEnd) { break }
    $end = $text.IndexOf(');',$start + $addTextMarker.Length,[StringComparison]::Ordinal)
    if ($end -lt 0 -or $end -ge $methodEnd) {
        throw 'August 23 Grid AddText call closing marker could not be resolved.'
    }
    $end += 2
    $call = $text.Substring($start,$end-$start)
    if ($call.Contains($prefixToken)) {
        [void]$candidates.Add([pscustomobject]@{ Start = $start; End = $end })
    }
    $search = $end
}
if ($candidates.Count -ne 1) {
    throw "August 23 Grid Prefix AddText setting expected exactly once inside CreateDynamicGrid; found $($candidates.Count)."
}
$addTextStart = [int]$candidates[0].Start
$callEnd = [int]$candidates[0].End

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

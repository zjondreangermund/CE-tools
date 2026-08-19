[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$coordinateRepair = Join-Path $root 'scripts\Repair-August18-SettingOutCoordinateDisplay-Civil3D2023.ps1'
if (-not (Test-Path -LiteralPath $coordinateRepair -PathType Leaf)) {
    throw "August 19 staged coordinate repair prerequisite was not found: $coordinateRepair"
}

# This script is August 19-only and is executed only inside the temporary staged
# repository. It does not alter the tracked August 18 repair in the source checkout.
$text = [System.IO.File]::ReadAllText($coordinateRepair) -replace "`r?`n", "`r`n"
$oldInvocation = @'
$site = ReplaceRequired $site $oldSiteChoice $newSiteChoice 'Site Grid coordinate popup controls'
'@.Trim()

$robustInvocation = @'
# August 19 staging compatibility: earlier recovery passes may change harmless
# Site Grid popup wording/section labels. Replace the AxisOrder AddChoice block
# structurally instead of requiring the entire historical text block verbatim.
if (-not ($site.Contains('"SwapXY"') -and $site.Contains('"ReverseSigns"'))) {
    $siteChoiceMarker = '            model.AddChoice(' + "`r`n" + '                "AxisOrder",'
    $siteChoiceStart = $site.IndexOf($siteChoiceMarker,[StringComparison]::Ordinal)
    if ($siteChoiceStart -lt 0) {
        throw 'Setting-out coordinate display anchor not found: Site Grid AxisOrder popup block'
    }
    $siteChoiceEnd = $site.IndexOf(');',$siteChoiceStart,[StringComparison]::Ordinal)
    if ($siteChoiceEnd -lt 0) {
        throw 'Setting-out coordinate display closing marker not found: Site Grid AxisOrder popup block'
    }
    $normalizedChoice = ($newSiteChoice -replace "`r?`n","`r`n").TrimEnd()
    $site = $site.Substring(0,$siteChoiceStart) +
        $normalizedChoice + "`r`n" +
        $site.Substring($siteChoiceEnd + 2).TrimStart("`r","`n")
}
'@.Trim()

if ($text.Contains($robustInvocation)) {
    Write-Host 'August 19 staged Site Grid coordinate-repair compatibility is already present.' -ForegroundColor Green
    return
}
if (-not $text.Contains($oldInvocation)) {
    throw 'August 19 could not locate the Site Grid coordinate popup invocation in the staged August 18 repair.'
}

$text = $text.Replace($oldInvocation,$robustInvocation)
[System.IO.File]::WriteAllText(
    $coordinateRepair,
    ($text -replace "`r?`n","`r`n"),
    (New-Object System.Text.UTF8Encoding($false)))

# Parse the staged repair after adapting it. Fail before the actual build if the
# generated PowerShell is not valid.
$tokens = $null
$parseErrors = $null
[System.Management.Automation.Language.Parser]::ParseFile(
    $coordinateRepair,
    [ref]$tokens,
    [ref]$parseErrors) | Out-Null
if ($parseErrors -and $parseErrors.Count -gt 0) {
    $details = ($parseErrors | ForEach-Object {
        'line ' + $_.Extent.StartLineNumber + ': ' + $_.Message
    }) -join ' | '
    throw "August 19 staged coordinate repair became invalid PowerShell: $details"
}

Write-Host 'August 19 staged Site Grid coordinate-repair compatibility applied.' -ForegroundColor Green
Write-Host 'The tracked August 18 coordinate repair remains unchanged; only the temporary staged copy was adapted.' -ForegroundColor Green

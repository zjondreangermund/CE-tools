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
# repository. It never alters the tracked August 18 repair in the source checkout.
$text = [System.IO.File]::ReadAllText($coordinateRepair) -replace "`r?`n", "`r`n"

function ReplaceInvocation(
    [string]$value,
    [string]$oldInvocation,
    [string]$robustInvocation,
    [string]$label)
{
    $oldInvocation = $oldInvocation.Trim()
    $robustInvocation = $robustInvocation.Trim()
    if ($value.Contains($robustInvocation)) { return $value }
    if (-not $value.Contains($oldInvocation)) {
        throw "August 19 could not locate staged Site Grid coordinate-repair invocation: $label"
    }
    return $value.Replace($oldInvocation,$robustInvocation)
}

$oldPopupInvocation = @'
$site = ReplaceRequired $site $oldSiteChoice $newSiteChoice 'Site Grid coordinate popup controls'
'@
$robustPopupInvocation = @'
# August 19 staging compatibility: earlier recovery passes may change harmless
# Site Grid popup wording/section labels. Replace the AxisOrder AddChoice block
# structurally instead of requiring the complete historical text block verbatim.
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
'@
$text = ReplaceInvocation $text $oldPopupInvocation $robustPopupInvocation 'popup controls'

$oldSettingsInvocation = @'
$site = ReplaceRequired $site $oldSiteSettings $newSiteSettings 'Site Grid save coordinate transform'
'@
$robustSettingsInvocation = @'
if (-not ($site.Contains('model.Text("SwapXY")') -and $site.Contains('model.Text("ReverseSigns")'))) {
    $siteSettingsMarker = '                ReverseXY = string.Equals(' + "`r`n" +
        '                    model.Text("AxisOrder"),'
    $siteSettingsStart = $site.IndexOf($siteSettingsMarker,[StringComparison]::Ordinal)
    if ($siteSettingsStart -lt 0) {
        throw 'Setting-out coordinate display anchor not found: Site Grid saved AxisOrder setting'
    }
    $paperHeightMarker = '                PaperTextHeight = ParsePaperHeight('
    $siteSettingsEnd = $site.IndexOf(
        $paperHeightMarker,
        $siteSettingsStart,
        [StringComparison]::Ordinal)
    if ($siteSettingsEnd -lt 0) {
        throw 'Setting-out coordinate display anchor not found: Site Grid PaperTextHeight setting'
    }
    $normalizedSettings = ($newSiteSettings -replace "`r?`n","`r`n").TrimEnd()
    $site = $site.Substring(0,$siteSettingsStart) +
        $normalizedSettings +
        $site.Substring($siteSettingsEnd + $paperHeightMarker.Length)
}
'@
$text = ReplaceInvocation $text $oldSettingsInvocation $robustSettingsInvocation 'saved coordinate transform'

$oldHeightInvocation = @'
$site = ReplaceRequired $site $oldSiteHeight $newSiteHeight 'Site Grid visible text-height floor'
'@
$robustHeightInvocation = @'
if (-not $site.Contains('double siteGridTextFloor = Math.Max(')) {
    $insideOffsetMarker = '            double insideOffset = Math.Max(modelTextHeight * 1.35, 0.001);'
    $insideOffsetIndex = $site.IndexOf($insideOffsetMarker,[StringComparison]::Ordinal)
    if ($insideOffsetIndex -lt 0) {
        throw 'Setting-out coordinate display anchor not found: Site Grid inside text offset'
    }
    $heightFloor = '            // Site-grid coordinate text must remain visible even when a drawing has' + "`r`n" +
        '            // an unusual or missing annotation-scale conversion. Use a modest floor' + "`r`n" +
        '            // tied to the selected grid spacing, never to the true coordinate values.' + "`r`n" +
        '            double siteGridTextFloor = Math.Max(' + "`r`n" +
        '                Math.Min(settings.SpacingX, settings.SpacingY) * 0.08,' + "`r`n" +
        '                0.001);' + "`r`n" +
        '            modelTextHeight = Math.Max(modelTextHeight, siteGridTextFloor);' + "`r`n"
    $site = $site.Insert($insideOffsetIndex,$heightFloor)
}
'@
$text = ReplaceInvocation $text $oldHeightInvocation $robustHeightInvocation 'visible text-height floor'

$oldXInvocation = @'
$site = ReplaceRequired $site $oldSiteXLabel $newSiteXLabel 'Site Grid vertical-axis transformed labels'
'@
$robustXInvocation = @'
$xValueMarker = '                    prefix + xValues[xIndex].ToString("0.###", CultureInfo.InvariantCulture),'
if ($site.Contains($xValueMarker)) {
    $xPrefixMarker = '                string prefix = settings.ReverseXY ? "Y: " : "X: ";'
    if (-not $site.Contains($xPrefixMarker)) {
        throw 'Setting-out coordinate display anchor not found: Site Grid X prefix'
    }
    $xPrefixReplacement = $xPrefixMarker + "`r`n" +
        '                double displayValue = settings.ReverseSigns' + "`r`n" +
        '                    ? -xValues[xIndex]' + "`r`n" +
        '                    : xValues[xIndex];'
    $site = $site.Replace($xPrefixMarker,$xPrefixReplacement)
    $site = $site.Replace(
        $xValueMarker,
        '                    prefix + displayValue.ToString("0.###", CultureInfo.InvariantCulture),')
}
elseif (-not $site.Contains('? -xValues[xIndex]')) {
    throw 'Setting-out coordinate display anchor not found: Site Grid vertical-axis value'
}
'@
$text = ReplaceInvocation $text $oldXInvocation $robustXInvocation 'vertical-axis transformed labels'

$oldYInvocation = @'
$site = ReplaceRequired $site $oldSiteYLabel $newSiteYLabel 'Site Grid horizontal-axis transformed labels'
'@
$robustYInvocation = @'
$yValueMarker = '                    prefix + yValues[yIndex].ToString("0.###", CultureInfo.InvariantCulture),'
if ($site.Contains($yValueMarker)) {
    $yPrefixMarker = '                string prefix = settings.ReverseXY ? "X: " : "Y: ";'
    if (-not $site.Contains($yPrefixMarker)) {
        throw 'Setting-out coordinate display anchor not found: Site Grid Y prefix'
    }
    $yPrefixReplacement = $yPrefixMarker + "`r`n" +
        '                double displayValue = settings.ReverseSigns' + "`r`n" +
        '                    ? -yValues[yIndex]' + "`r`n" +
        '                    : yValues[yIndex];'
    $site = $site.Replace($yPrefixMarker,$yPrefixReplacement)
    $site = $site.Replace(
        $yValueMarker,
        '                    prefix + displayValue.ToString("0.###", CultureInfo.InvariantCulture),')
}
elseif (-not $site.Contains('? -yValues[yIndex]')) {
    throw 'Setting-out coordinate display anchor not found: Site Grid horizontal-axis value'
}
'@
$text = ReplaceInvocation $text $oldYInvocation $robustYInvocation 'horizontal-axis transformed labels'

$oldWriteInvocation = @'
$site = ReplaceRequired $site $oldSiteWrite $newSiteWrite 'Site Grid persist reverse signs'
'@
$robustWriteInvocation = @'
if (-not $site.Contains('settings.ReverseSigns ? 1 : 0')) {
    $createPointsMarker = '                    new TypedValue((int)DxfCode.Int16, settings.CreatePoints ? 1 : 0)'
    $createPointsIndex = $site.IndexOf($createPointsMarker,[StringComparison]::Ordinal)
    if ($createPointsIndex -lt 0) {
        throw 'Setting-out coordinate display anchor not found: Site Grid CreatePoints persistence'
    }
    $createPointsReplacement = $createPointsMarker + ',' + "`r`n" +
        '                    new TypedValue((int)DxfCode.Int16, settings.ReverseSigns ? 1 : 0)'
    $site = $site.Remove($createPointsIndex,$createPointsMarker.Length).Insert(
        $createPointsIndex,
        $createPointsReplacement)
}
'@
$text = ReplaceInvocation $text $oldWriteInvocation $robustWriteInvocation 'reverse-sign persistence'

$oldReadInvocation = @'
$site = ReplaceRequired $site $oldSiteRead $newSiteRead 'Site Grid backward-compatible reverse sign read'
'@
$robustReadInvocation = @'
if (-not $site.Contains('settings.ReverseSigns = values.Length >= 7')) {
    $createReadStartMarker = '                settings.CreatePoints = Convert.ToInt32('
    $createReadStart = $site.IndexOf($createReadStartMarker,[StringComparison]::Ordinal)
    if ($createReadStart -lt 0) {
        throw 'Setting-out coordinate display anchor not found: Site Grid CreatePoints read'
    }
    $createReadEnd = $site.IndexOf(';',$createReadStart,[StringComparison]::Ordinal)
    if ($createReadEnd -lt 0) {
        throw 'Setting-out coordinate display closing marker not found: Site Grid CreatePoints read'
    }
    $reverseRead = "`r`n" +
        '                settings.ReverseSigns = values.Length >= 7 && Convert.ToInt32(' + "`r`n" +
        '                    values[6].Value,' + "`r`n" +
        '                    CultureInfo.InvariantCulture) != 0;'
    $site = $site.Insert($createReadEnd + 1,$reverseRead)
}
'@
$text = ReplaceInvocation $text $oldReadInvocation $robustReadInvocation 'backward-compatible reverse-sign read'

$oldFieldsInvocation = @'
$site = ReplaceRequired $site $oldSiteFields $newSiteFields 'Site Grid settings reverse signs field'
'@
$robustFieldsInvocation = @'
if (-not $site.Contains('            internal bool ReverseSigns;')) {
    $reverseXYField = '            internal bool ReverseXY;'
    $reverseXYFieldIndex = $site.IndexOf($reverseXYField,[StringComparison]::Ordinal)
    if ($reverseXYFieldIndex -lt 0) {
        throw 'Setting-out coordinate display anchor not found: Site Grid ReverseXY field'
    }
    $site = $site.Insert(
        $reverseXYFieldIndex + $reverseXYField.Length,
        "`r`n" + '            internal bool ReverseSigns;')
}
'@
$text = ReplaceInvocation $text $oldFieldsInvocation $robustFieldsInvocation 'ReverseSigns settings field'

$oldDefaultInvocation = @'
$site = ReplaceRequired $site $oldSiteDefault $newSiteDefault 'Site Grid reverse signs default'
'@
$robustDefaultInvocation = @'
if (-not $site.Contains('                    ReverseSigns = false,')) {
    $reverseXYDefault = '                    ReverseXY = false,'
    $reverseXYDefaultIndex = $site.IndexOf($reverseXYDefault,[StringComparison]::Ordinal)
    if ($reverseXYDefaultIndex -lt 0) {
        throw 'Setting-out coordinate display anchor not found: Site Grid ReverseXY default'
    }
    $site = $site.Insert(
        $reverseXYDefaultIndex + $reverseXYDefault.Length,
        "`r`n" + '                    ReverseSigns = false,')
}
'@
$text = ReplaceInvocation $text $oldDefaultInvocation $robustDefaultInvocation 'ReverseSigns default'

[System.IO.File]::WriteAllText(
    $coordinateRepair,
    ($text -replace "`r?`n","`r`n"),
    (New-Object System.Text.UTF8Encoding($false)))

# Parse the staged repair after adapting all Site Grid anchors. Fail before the
# actual build if the generated PowerShell is not valid.
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

Write-Host 'August 19 staged Site Grid coordinate-repair compatibility applied to all Site Grid anchors.' -ForegroundColor Green
Write-Host 'The tracked August 18 coordinate repair remains unchanged; only the temporary staged copy was adapted.' -ForegroundColor Green

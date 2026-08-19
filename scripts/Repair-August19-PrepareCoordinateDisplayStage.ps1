[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$sitePath = Join-Path $root 'src\CE.Tools.Civil3D\August12SurveySiteGridCommands.cs'
$coordinateRepair = Join-Path $root 'scripts\Repair-August18-SettingOutCoordinateDisplay-Civil3D2023.ps1'
$utf8 = New-Object System.Text.UTF8Encoding($false)

foreach ($required in @($sitePath,$coordinateRepair)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "August 19 staged Site Grid prerequisite was not found: $required"
    }
}

function ReadText([string]$path) {
    return [System.IO.File]::ReadAllText($path) -replace "`r?`n", "`r`n"
}
function WriteText([string]$path,[string]$text) {
    [System.IO.File]::WriteAllText($path,($text -replace "`r?`n","`r`n"),$utf8)
}

# -----------------------------------------------------------------------------
# August 19 applies the final Site Grid display changes directly to the temporary
# staged C# source. The tracked August 18 source/repair files are never edited.
# This avoids depending on exact wording left behind by historical recovery passes.
# -----------------------------------------------------------------------------
$site = ReadText $sitePath

if (-not ($site.Contains('"SwapXY"') -and $site.Contains('"ReverseSigns"'))) {
    $popupMarker = '            model.AddChoice(' + "`r`n" + '                "AxisOrder",'
    $popupStart = $site.IndexOf($popupMarker,[StringComparison]::Ordinal)
    if ($popupStart -lt 0) {
        throw 'August 19 Site Grid staging could not locate the AxisOrder popup block.'
    }
    $popupEnd = $site.IndexOf(');',$popupStart,[StringComparison]::Ordinal)
    if ($popupEnd -lt 0) {
        throw 'August 19 Site Grid staging could not locate the AxisOrder popup closing marker.'
    }
    $newPopup = @'
            model.AddChoice(
                "SwapXY",
                "Coordinate Display",
                "Swap X / Y values",
                current.ReverseXY ? "Yes" : "No",
                "Yes swaps displayed X/Y values only. Site-grid geometry remains unchanged.",
                new[] { "No", "Yes" });
            model.AddChoice(
                "ReverseSigns",
                "Coordinate Display",
                "Reverse coordinate signs",
                current.ReverseSigns ? "Yes" : "No",
                "Yes reverses both displayed coordinate signs after any X/Y swap. Site-grid geometry remains unchanged.",
                new[] { "No", "Yes" });
'@ -replace "`n","`r`n"
    $site = $site.Substring(0,$popupStart) + $newPopup.TrimEnd() + "`r`n" +
        $site.Substring($popupEnd + 2).TrimStart("`r","`n")
}

if (-not ($site.Contains('model.Text("SwapXY")') -and $site.Contains('model.Text("ReverseSigns")'))) {
    $settingsStartMarker = '                ReverseXY = string.Equals('
    $settingsStart = $site.IndexOf($settingsStartMarker,[StringComparison]::Ordinal)
    if ($settingsStart -lt 0) {
        throw 'August 19 Site Grid staging could not locate the ReverseXY settings assignment.'
    }
    $paperMarker = '                PaperTextHeight = ParsePaperHeight('
    $settingsEnd = $site.IndexOf($paperMarker,$settingsStart,[StringComparison]::Ordinal)
    if ($settingsEnd -lt 0) {
        throw 'August 19 Site Grid staging could not locate the PaperTextHeight settings assignment.'
    }
    $newSettings = @'
                ReverseXY = string.Equals(
                    model.Text("SwapXY"),
                    "Yes",
                    StringComparison.OrdinalIgnoreCase),
                ReverseSigns = string.Equals(
                    model.Text("ReverseSigns"),
                    "Yes",
                    StringComparison.OrdinalIgnoreCase),
                PaperTextHeight = ParsePaperHeight(
'@ -replace "`n","`r`n"
    $site = $site.Substring(0,$settingsStart) + $newSettings.TrimEnd() +
        $site.Substring($settingsEnd + $paperMarker.Length)
}

if (-not $site.Contains('double siteGridTextFloor = Math.Max(')) {
    $rebuildStart = $site.IndexOf('        private static int RebuildOne(',[StringComparison]::Ordinal)
    if ($rebuildStart -lt 0) {
        throw 'August 19 Site Grid staging could not locate RebuildOne.'
    }
    $heightStart = $site.IndexOf(
        '            double modelTextHeight = ModelTextHeight(',
        $rebuildStart,
        [StringComparison]::Ordinal)
    if ($heightStart -lt 0) {
        throw 'August 19 Site Grid staging could not locate the model text-height calculation.'
    }
    $heightEnd = $site.IndexOf(';',$heightStart,[StringComparison]::Ordinal)
    if ($heightEnd -lt 0) {
        throw 'August 19 Site Grid staging could not locate the model text-height calculation terminator.'
    }
    $heightFloor = @'

            // Keep site-grid coordinate text visible even when annotation-scale
            // conversion is unusually small. This changes display only.
            double siteGridTextFloor = Math.Max(
                Math.Min(settings.SpacingX, settings.SpacingY) * 0.08,
                0.001);
            modelTextHeight = Math.Max(modelTextHeight, siteGridTextFloor);
'@ -replace "`n","`r`n"
    $site = $site.Insert($heightEnd + 1,$heightFloor)
}

$xOld = '                    prefix + xValues[xIndex].ToString("0.###", CultureInfo.InvariantCulture),'
if ($site.Contains($xOld)) {
    $xPrefix = '                string prefix = settings.ReverseXY ? "Y: " : "X: ";'
    if (-not $site.Contains($xPrefix)) {
        throw 'August 19 Site Grid staging could not locate the vertical-grid coordinate prefix.'
    }
    $xPrefixNew = $xPrefix + "`r`n" +
        '                double displayValue = settings.ReverseSigns' + "`r`n" +
        '                    ? -xValues[xIndex]' + "`r`n" +
        '                    : xValues[xIndex];'
    $site = $site.Replace($xPrefix,$xPrefixNew)
    $site = $site.Replace(
        $xOld,
        '                    prefix + displayValue.ToString("0.###", CultureInfo.InvariantCulture),')
}
elseif (-not $site.Contains('? -xValues[xIndex]')) {
    throw 'August 19 Site Grid staging could not verify transformed vertical-grid labels.'
}

$yOld = '                    prefix + yValues[yIndex].ToString("0.###", CultureInfo.InvariantCulture),'
if ($site.Contains($yOld)) {
    $yPrefix = '                string prefix = settings.ReverseXY ? "X: " : "Y: ";'
    if (-not $site.Contains($yPrefix)) {
        throw 'August 19 Site Grid staging could not locate the horizontal-grid coordinate prefix.'
    }
    $yPrefixNew = $yPrefix + "`r`n" +
        '                double displayValue = settings.ReverseSigns' + "`r`n" +
        '                    ? -yValues[yIndex]' + "`r`n" +
        '                    : yValues[yIndex];'
    $site = $site.Replace($yPrefix,$yPrefixNew)
    $site = $site.Replace(
        $yOld,
        '                    prefix + displayValue.ToString("0.###", CultureInfo.InvariantCulture),')
}
elseif (-not $site.Contains('? -yValues[yIndex]')) {
    throw 'August 19 Site Grid staging could not verify transformed horizontal-grid labels.'
}

if (-not $site.Contains('settings.ReverseSigns ? 1 : 0')) {
    $persistMarker = '                    new TypedValue((int)DxfCode.Int16, settings.CreatePoints ? 1 : 0)'
    $persistIndex = $site.IndexOf($persistMarker,[StringComparison]::Ordinal)
    if ($persistIndex -lt 0) {
        throw 'August 19 Site Grid staging could not locate CreatePoints persistence.'
    }
    $persistNew = $persistMarker + ',' + "`r`n" +
        '                    new TypedValue((int)DxfCode.Int16, settings.ReverseSigns ? 1 : 0)'
    $site = $site.Remove($persistIndex,$persistMarker.Length).Insert($persistIndex,$persistNew)
}

if (-not $site.Contains('settings.ReverseSigns = values.Length >= 7')) {
    $readMarker = '                settings.CreatePoints = Convert.ToInt32('
    $readStart = $site.IndexOf($readMarker,[StringComparison]::Ordinal)
    if ($readStart -lt 0) {
        throw 'August 19 Site Grid staging could not locate CreatePoints readback.'
    }
    $readEnd = $site.IndexOf(';',$readStart,[StringComparison]::Ordinal)
    if ($readEnd -lt 0) {
        throw 'August 19 Site Grid staging could not locate CreatePoints readback terminator.'
    }
    $reverseRead = "`r`n" +
        '                settings.ReverseSigns = values.Length >= 7 && Convert.ToInt32(' + "`r`n" +
        '                    values[6].Value,' + "`r`n" +
        '                    CultureInfo.InvariantCulture) != 0;'
    $site = $site.Insert($readEnd + 1,$reverseRead)
}

if (-not $site.Contains('            internal bool ReverseSigns;')) {
    $fieldMarker = '            internal bool ReverseXY;'
    $fieldIndex = $site.IndexOf($fieldMarker,[StringComparison]::Ordinal)
    if ($fieldIndex -lt 0) {
        throw 'August 19 Site Grid staging could not locate the ReverseXY settings field.'
    }
    $site = $site.Insert(
        $fieldIndex + $fieldMarker.Length,
        "`r`n" + '            internal bool ReverseSigns;')
}

if (-not $site.Contains('                    ReverseSigns = false,')) {
    $defaultMarker = '                    ReverseXY = false,'
    $defaultIndex = $site.IndexOf($defaultMarker,[StringComparison]::Ordinal)
    if ($defaultIndex -lt 0) {
        throw 'August 19 Site Grid staging could not locate the ReverseXY default.'
    }
    $site = $site.Insert(
        $defaultIndex + $defaultMarker.Length,
        "`r`n" + '                    ReverseSigns = false,')
}

WriteText $sitePath $site

# -----------------------------------------------------------------------------
# The staged Site Grid source is now final. Remove only the Site Grid mutation
# block from the staged August 18 coordinate repair so that repair can still apply
# its original Vertex + Dynamic Grid changes and then validate the final Site Grid.
# -----------------------------------------------------------------------------
$repair = ReadText $coordinateRepair
$siteSectionStart = $repair.IndexOf(
    '# 3. Site Grid: expose Swap X/Y + Reverse signs, persist them, and ensure border',
    [StringComparison]::Ordinal)
$finalGuardsStart = $repair.IndexOf(
    '# Final guards: fail before MSBuild if any setting-out display path did not receive',
    [StringComparison]::Ordinal)
if ($siteSectionStart -lt 0 -or $finalGuardsStart -lt 0 -or $finalGuardsStart -le $siteSectionStart) {
    throw 'August 19 could not isolate the staged Site Grid block inside the August 18 coordinate repair.'
}
$sectionHeaderStart = $repair.LastIndexOf(
    '# -----------------------------------------------------------------------------',
    $siteSectionStart,
    [StringComparison]::Ordinal)
$guardsHeaderStart = $repair.LastIndexOf(
    '# -----------------------------------------------------------------------------',
    $finalGuardsStart,
    [StringComparison]::Ordinal)
if ($sectionHeaderStart -lt 0 -or $guardsHeaderStart -lt 0) {
    throw 'August 19 could not locate the Site Grid/final-guard section headers in the staged coordinate repair.'
}
$replacement = @'
# -----------------------------------------------------------------------------
# 3. Site Grid was finalized by the August 19 staged compatibility layer before
#    this repair runs. Keep the original August 18 final guards below.
# -----------------------------------------------------------------------------
$sitePath = Required 'August12SurveySiteGridCommands.cs'
$site = ReadText $sitePath

'@ -replace "`n","`r`n"
$repair = $repair.Substring(0,$sectionHeaderStart) + $replacement + $repair.Substring($guardsHeaderStart)
WriteText $coordinateRepair $repair

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

$site = ReadText $sitePath
foreach ($marker in @(
    '"SwapXY",',
    '"ReverseSigns",',
    'double siteGridTextFloor = Math.Max(',
    '? -xValues[xIndex]',
    '? -yValues[yIndex]',
    'settings.ReverseSigns ? 1 : 0',
    'settings.ReverseSigns = values.Length >= 7',
    'internal bool ReverseSigns;')) {
    if (-not $site.Contains($marker)) {
        throw "August 19 staged Site Grid final marker missing: $marker"
    }
}
if ($site.Contains('"Reverse X / Y labels"')) {
    throw 'August 19 staged Site Grid still contains the obsolete headings-only X/Y option.'
}

Write-Host 'August 19 staged Site Grid source finalized without legacy text anchors.' -ForegroundColor Green
Write-Host 'The staged August 18 coordinate repair will now handle Vertex/Dynamic Grid and only validate Site Grid.' -ForegroundColor Green
Write-Host 'Tracked August 18 source and repair files remain unchanged in the source checkout.' -ForegroundColor Green

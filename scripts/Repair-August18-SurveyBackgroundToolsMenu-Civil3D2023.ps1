[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$path = Join-Path $root 'src\CE.Tools.Civil3D\August14StructuredDisciplineProductionCentres.cs'
if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
    throw "Survey Production source missing: $path"
}

$utf8 = New-Object System.Text.UTF8Encoding($false)
$text = [System.IO.File]::ReadAllText($path) -replace "`r?`n", "`r`n"

$surveyStart = $text.IndexOf('public void SurveyProduction()', [StringComparison]::Ordinal)
if ($surveyStart -lt 0) { throw 'SurveyProduction() method was not found.' }
$open = $text.IndexOf('{',$surveyStart)
if ($open -lt 0) { throw 'SurveyProduction() opening brace was not found.' }
$depth = 0
$close = -1
for ($i=$open; $i -lt $text.Length; $i++) {
    if ($text[$i] -eq '{') { $depth++ }
    elseif ($text[$i] -eq '}') {
        $depth--
        if ($depth -eq 0) { $close = $i; break }
    }
}
if ($close -lt 0) { throw 'SurveyProduction() closing brace was not found.' }

$survey = $text.Substring($surveyStart,$close-$surveyStart+1)
$newAction = '                    A("CE-Background Tools", "CE_BACKGROUNDPREPTOOLS", "Prepare imported/background DWGs: burst blocks, colour 250, audit/overkill/purge, freeze solid hatches/dimensions and correct scale to metres.", "02 PREPARE"),'

# Replace any direct legacy Background/XREF entry in the one-page Survey Production menu.
$legacyPattern = '(?m)^\s*A\("[^"]*(?:Background|XREF)[^"]*",\s*"CE_BACKGROUNDTOOLS",[^\r\n]*\),\s*$'
if ([regex]::IsMatch($survey,$legacyPattern,[System.Text.RegularExpressions.RegexOptions]::IgnoreCase)) {
    $survey = [regex]::Replace(
        $survey,
        $legacyPattern,
        $newAction,
        [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
}

# If no legacy entry exists, place CE-Background Tools at the start of PREPARE,
# immediately after the LandXML import/export action.
if (-not $survey.Contains('"CE_BACKGROUNDPREPTOOLS"')) {
    $landXml = '                    A("CE-LandXML Import / Export", "CE_LANDXMLTOOLS", "Import/export Civil survey data.", "02 PREPARE"),'
    if (-not $survey.Contains($landXml)) {
        throw 'Survey Production LandXML PREPARE anchor was not found for CE-Background Tools insertion.'
    }
    $survey = $survey.Replace($landXml,$landXml + "`r`n" + $newAction)
}

# Survey Production must expose only the preparation launcher. The established
# CE_BACKGROUNDTOOLS XREF manager remains available from inside that launcher.
if ($survey.Contains('"CE_BACKGROUNDTOOLS"')) {
    throw 'Survey Production still exposes the old Background/XREF manager directly.'
}
if (-not $survey.Contains('A("CE-Background Tools", "CE_BACKGROUNDPREPTOOLS"')) {
    throw 'Survey Production does not expose CE-Background Tools.'
}
if (-not $survey.Contains('"02 PREPARE"')) {
    throw 'CE-Background Tools is not placed in Survey Production PREPARE.'
}

$text = $text.Substring(0,$surveyStart) + $survey + $text.Substring($close+1)
[System.IO.File]::WriteAllText($path,$text,$utf8)

# Final command-owner guard: the requested popup must really exist and must retain
# the existing Background/XREF utilities as its final nested option.
$backgroundPath = Join-Path $root 'src\CE.Tools.Civil3D\BackgroundPreparationCommands.cs'
if (-not (Test-Path -LiteralPath $backgroundPath -PathType Leaf)) {
    throw "Background Tools source missing: $backgroundPath"
}
$background = [System.IO.File]::ReadAllText($backgroundPath)
foreach ($required in @(
    '"CE_BACKGROUNDPREPTOOLS"',
    '"CE Tools - Background Tools"',
    '"CE-Burst All Blocks"',
    '"CE-Background Colour 250"',
    '"CE-Audit / Overkill / Purge"',
    '"CE-Freeze Solid Hatches"',
    '"CE-Freeze Dimensions"',
    '"CE-Scale Correction / Convert to Metres"',
    '"CE-Existing Background / XREF Utilities", "CE_BACKGROUNDTOOLS"')) {
    if (-not $background.Contains($required)) {
        throw "CE-Background Tools popup marker missing: $required"
    }
}

Write-Host 'Survey Production PREPARE now opens CE-Background Tools directly.' -ForegroundColor Green
Write-Host 'The older Background/XREF Utilities remain available only inside CE-Background Tools.' -ForegroundColor Green

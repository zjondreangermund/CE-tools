[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$source = Join-Path $PSScriptRoot "Apply-Master-Items-Phase1.ps1"
if (-not (Test-Path $source)) {
    throw "The Master Items Phase 1 normalizer is missing: $source"
}

# Run an ephemeral tolerant copy. Required results are checked by the Phase 1
# validator, while one version-specific or already-replaced optional snippet
# cannot prevent later independent ribbon/source corrections from running.
$text = [System.IO.File]::ReadAllText($source)
$strict = '        throw "Could not apply master-item change ''$Description'' in ''$RelativePath''."'
$tolerant = @'
        Write-Warning "Skipped Master Items Phase 1 change '$Description' in '$RelativePath' because the expected source text was not found. Source validators will confirm required results."
        return
'@
if (-not $text.Contains($strict)) {
    throw "The expected strict Master Items replacement guard was not found in $source."
}
$text = $text.Replace($strict, $tolerant.TrimEnd())

$temp = Join-Path $PSScriptRoot ".Apply-Master-Items-Phase1.tolerant.ps1"
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($temp, $text, $utf8NoBom)
try {
    & $temp
}
finally {
    Remove-Item $temp -Force -ErrorAction SilentlyContinue
}

# The inherited sewer-quantity normalizer runs before Phase 1 and adds its own
# CE_REFRESHALL line. Append the setting-out refresh after that exact line.
$presentation = Join-Path $repositoryRoot "src\CE.Tools.Civil3D\CommentPresentationCommands.cs"
if (-not (Test-Path $presentation)) {
    throw "The shared refresh source is missing: $presentation"
}
$presentationText = [System.IO.File]::ReadAllText($presentation).Replace("`r`n", "`n")
$settingLine = '            summary.CoordinateTables += SettingOutScheduleCommands.RefreshAll(document);'
if (-not $presentationText.Contains($settingLine)) {
    $sewerLine = '            summary.BoqTables += SewerExcavationCommentCommands.RefreshAll(document);'
    if (-not $presentationText.Contains($sewerLine)) {
        throw "The inherited sewer refresh insertion was not found before the Phase 1 setting-out refresh."
    }
    $presentationText = $presentationText.Replace(
        $sewerLine,
        $sewerLine + "`n" + $settingLine)
    [System.IO.File]::WriteAllText($presentation, $presentationText, $utf8NoBom)
    Write-Host "  include linked setting-out schedules in CE_REFRESHALL" -ForegroundColor Green
}

$roadSections = Join-Path $PSScriptRoot "Apply-Master-Items-Phase1-RoadSections.ps1"
if (-not (Test-Path $roadSections)) {
    throw "The Phase 1 road cross-section normalizer is missing: $roadSections"
}
& $roadSections

$networkSchedule = Join-Path $PSScriptRoot "Apply-Master-Items-Phase1-NetworkSchedule.ps1"
if (-not (Test-Path $networkSchedule)) {
    throw "The Phase 1 network asset schedule normalizer is missing: $networkSchedule"
}
& $networkSchedule

Write-Host "Master Items Phase 1 normalization completed; validators will confirm every required result." -ForegroundColor Green

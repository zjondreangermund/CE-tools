[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

function Replace-ExactText {
    param(
        [Parameter(Mandatory = $true)][string]$RelativePath,
        [Parameter(Mandatory = $true)][string]$OldText,
        [Parameter(Mandatory = $true)][string]$NewText,
        [Parameter(Mandatory = $true)][string]$Description
    )

    $path = Join-Path $repositoryRoot $RelativePath
    if (-not (Test-Path $path)) { throw "Phase 6 source was not found: $RelativePath" }
    $text = [System.IO.File]::ReadAllText($path).Replace("`r`n", "`n")
    $oldNormalised = $OldText.Replace("`r`n", "`n")
    $newNormalised = $NewText.Replace("`r`n", "`n")
    if ($text.Contains($newNormalised) -and -not $text.Contains($oldNormalised)) { return }
    if (-not $text.Contains($oldNormalised)) {
        throw "Could not apply Phase 6 change '$Description' in '$RelativePath'."
    }
    [System.IO.File]::WriteAllText(
        $path,
        $text.Replace($oldNormalised, $newNormalised),
        $utf8NoBom)
    Write-Host "  $Description" -ForegroundColor Green
}

$ribbonFile = "src\CE.Tools.Civil3D\PluginEntry.cs"
$oldHydrologyTail = @'
                    Cmd("Depression Storage and Affected Area", "CE_PONDINGREVIEW ", "Map connected priority-filled depression zones and report affected area, maximum depth and estimated terrain-storage volume."),
                    Cmd("Clear Surface Hydrology Review", "CE_HYDROLOGYCLEAR ", "Erase only CE-generated surface-flow, catchment, ponding, outlet and label graphics.")),
'@
$newHydrologyTail = @'
                    Cmd("Depression Storage and Affected Area", "CE_PONDINGREVIEW ", "Map connected priority-filled depression zones and report affected area, maximum depth and estimated terrain-storage volume."),
                    Cmd("Clear Surface Hydrology Review", "CE_HYDROLOGYCLEAR ", "Erase only CE-generated surface-flow, catchment, ponding, outlet and label graphics."),
                    Cmd("Imported Flood Result Tools", "CE_FLOODRESULTTOOLS ", "Open affected-property, scenario/time frame, reset and browser-animation workflows for imported specialist result points."),
                    Cmd("Affected Property Flood Review", "CE_FLOODPROPERTYREPORT ", "Summarise imported depth, velocity, water level and screening hazard point samples inside selected property boundaries."),
                    Cmd("Show One Flood Result Frame", "CE_FLOODFRAMESET ", "Show one imported scenario/time frame and hide other CE Tools imported result markers."),
                    Cmd("Reset Flood Result Frames", "CE_FLOODFRAMERESET ", "Restore visibility of all CE Tools imported specialist-result markers."),
                    Cmd("Export Flood Result Animation", "CE_FLOODANIMATIONHTML ", "Create a self-contained browser animation with scenario selection, frame slider, play/pause and optional property outlines.")),
'@
Replace-ExactText `
    -RelativePath $ribbonFile `
    -OldText $oldHydrologyTail `
    -NewText $newHydrologyTail `
    -Description "add imported flood-result review and animation commands"

Write-Host "Master Items Phase 6 flood-result source is wired." -ForegroundColor Green

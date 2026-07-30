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
        [Parameter(Mandatory = $true)][string]$Description,
        [string]$AlreadyPresentText = ""
    )
    $path = Join-Path $repositoryRoot $RelativePath
    if (-not (Test-Path $path)) { throw "Road-section source was not found: $RelativePath" }
    $text = [System.IO.File]::ReadAllText($path).Replace("`r`n", "`n")
    $oldNormalised = $OldText.Replace("`r`n", "`n")
    $newNormalised = $NewText.Replace("`r`n", "`n")
    $alreadyPresentNormalised = $AlreadyPresentText.Replace("`r`n", "`n")
    if ($alreadyPresentNormalised -and $text.Contains($alreadyPresentNormalised)) { return }
    if ($text.Contains($newNormalised) -and -not $text.Contains($oldNormalised)) { return }
    if (-not $text.Contains($oldNormalised)) {
        throw "Could not apply road-section change '$Description' in '$RelativePath'."
    }
    [System.IO.File]::WriteAllText($path, $text.Replace($oldNormalised, $newNormalised), $utf8NoBom)
    Write-Host "  $Description" -ForegroundColor Green
}

$ribbonFile = "src\CE.Tools.Civil3D\PluginEntry.cs"
$oldSettingTail = @'
                    Cmd("Export Setting-Out Schedule", "CE_SETTINGOUTEXPORT ", "Refresh and export a linked setting-out table to Excel."),
                    Cmd("Setting-Out Schedule Information", "CE_SETTINGOUTINFO ", "Review schedule type, source handles, surface links and missing values."))));
'@
$newSettingTail = @'
                    Cmd("Export Setting-Out Schedule", "CE_SETTINGOUTEXPORT ", "Refresh and export a linked setting-out table to Excel."),
                    Cmd("Setting-Out Schedule Information", "CE_SETTINGOUTINFO ", "Review schedule type, source handles, surface links and missing values."),
                    Cmd("Road Cross-Section Data Tools", "CE_ROADSECTIONDATATOOLS ", "Open create, refresh, export and information workflows for left-edge, centreline and right-edge road data."),
                    Cmd("Create Road Cross-Section Data", "CE_ROADSECTIONDATA ", "Create a linked road cross-section setting-out schedule at 5 m, 10 m or 20 m intervals."),
                    Cmd("Refresh Road Cross-Section Data", "CE_ROADSECTIONDATAREFRESH ", "Recalculate a linked road section schedule from the current alignment and surfaces."),
                    Cmd("Export Road Cross-Section Data", "CE_ROADSECTIONDATAEXPORT ", "Refresh and export linked road cross-section setting-out data to Excel."),
                    Cmd("Road Cross-Section Data Information", "CE_ROADSECTIONDATAINFO ", "Review linked alignment, surfaces, offsets, interval and sample status."))));
'@
Replace-ExactText `
    -RelativePath $ribbonFile `
    -OldText $oldSettingTail `
    -NewText $newSettingTail `
    -Description "add linked road cross-section setting-out schedules"

$presentationFile = "src\CE.Tools.Civil3D\CommentPresentationCommands.cs"
Replace-ExactText `
    -RelativePath $presentationFile `
    -OldText @'
            summary.CoordinateTables += SettingOutScheduleCommands.RefreshAll(document);

            List<LinkedTableItem> tables = ReadLinkedTables(document.Database);
'@ `
    -NewText @'
            summary.CoordinateTables += SettingOutScheduleCommands.RefreshAll(document);
            summary.CoordinateTables += RoadCrossSectionScheduleCommands.RefreshAll(document);

            List<LinkedTableItem> tables = ReadLinkedTables(document.Database);
'@ `
    -Description "include linked road cross-section schedules in CE_REFRESHALL" `
    -AlreadyPresentText "summary.CoordinateTables += RoadCrossSectionScheduleCommands.RefreshAll(document);"

Write-Host "Road cross-section setting-out schedules are wired." -ForegroundColor Green

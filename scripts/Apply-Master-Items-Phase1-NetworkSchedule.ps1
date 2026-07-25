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
    if (-not (Test-Path $path)) { throw "Network-schedule source was not found: $RelativePath" }
    $text = [System.IO.File]::ReadAllText($path).Replace("`r`n", "`n")
    $oldNormalised = $OldText.Replace("`r`n", "`n")
    $newNormalised = $NewText.Replace("`r`n", "`n")
    if ($text.Contains($newNormalised) -and -not $text.Contains($oldNormalised)) { return }
    if (-not $text.Contains($oldNormalised)) {
        throw "Could not apply network-schedule change '$Description' in '$RelativePath'."
    }
    [System.IO.File]::WriteAllText($path, $text.Replace($oldNormalised, $newNormalised), $utf8NoBom)
    Write-Host "  $Description" -ForegroundColor Green
}

$ribbonFile = "src\CE.Tools.Civil3D\PluginEntry.cs"
$oldNetworkTail = @'
                    Cmd("Selected Network Part Data", "CE_NETWORKPARTREPORT2 ", "Show selected pipe, structure, fitting and appurtenance details and optionally place a table."),
                    Cmd("Service Alignment and Profile Production Window", "CE_SERVICEPROFILES ", "Open one window for stormwater, sewer and water sequencing, alignments, profiles and water asset markers."),
                    Cmd("Network Data and Refresh Window", "CE_NETWORKDATA ", "Open network reports, production information and shared refresh workflows."))));
'@
$newNetworkTail = @'
                    Cmd("Selected Network Part Data", "CE_NETWORKPARTREPORT2 ", "Show selected pipe, structure, fitting and appurtenance details and optionally place a table."),
                    Cmd("Service Alignment and Profile Production Window", "CE_SERVICEPROFILES ", "Open one window for stormwater, sewer and water sequencing, alignments, profiles and water asset markers."),
                    Cmd("Network Data and Refresh Window", "CE_NETWORKDATA ", "Open network reports, production information and shared refresh workflows."),
                    Cmd("Network Asset Schedule Tools", "CE_NETWORKSCHEDULETOOLS ", "Open create, refresh, export, information and BOQ handoff workflows."),
                    Cmd("Create Linked Network Asset Schedule", "CE_NETWORKSCHEDULE ", "Create an all-network or discipline-filtered schedule of pipes, structures, fittings, bends and appurtenances."),
                    Cmd("Refresh Network Asset Schedule", "CE_NETWORKSCHEDULEREFRESH ", "Refresh a linked network schedule from current Civil 3D part properties."),
                    Cmd("Export Network Asset Schedule", "CE_NETWORKSCHEDULEEXPORT ", "Refresh and export a linked network asset schedule to Excel."),
                    Cmd("Network Asset Schedule Information", "CE_NETWORKSCHEDULEINFO ", "Review scope, source handles, current asset count and missing objects."),
                    Cmd("Build BOQ from Network Schedule", "CE_NETWORKSCHEDULEBOQ ", "Select the schedule's live source assets and open the existing linked CE BOQ builder."))));
'@
Replace-ExactText `
    -RelativePath $ribbonFile `
    -OldText $oldNetworkTail `
    -NewText $newNetworkTail `
    -Description "add linked network asset schedules and BOQ handoff"

$presentationFile = "src\CE.Tools.Civil3D\CommentPresentationCommands.cs"
Replace-ExactText `
    -RelativePath $presentationFile `
    -OldText @'
            summary.CoordinateTables += RoadCrossSectionScheduleCommands.RefreshAll(document);

            List<LinkedTableItem> tables = ReadLinkedTables(document.Database);
'@ `
    -NewText @'
            summary.CoordinateTables += RoadCrossSectionScheduleCommands.RefreshAll(document);
            summary.BoqTables += NetworkAssetScheduleCommands.RefreshAll(document);

            List<LinkedTableItem> tables = ReadLinkedTables(document.Database);
'@ `
    -Description "include linked network asset schedules in CE_REFRESHALL"

Write-Host "Network asset schedules and BOQ handoff are wired." -ForegroundColor Green

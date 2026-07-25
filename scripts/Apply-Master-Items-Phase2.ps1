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
    if (-not (Test-Path $path)) { throw "Phase 2 source was not found: $RelativePath" }
    $text = [System.IO.File]::ReadAllText($path).Replace("`r`n", "`n")
    $oldNormalised = $OldText.Replace("`r`n", "`n")
    $newNormalised = $NewText.Replace("`r`n", "`n")
    if ($text.Contains($newNormalised) -and -not $text.Contains($oldNormalised)) { return }
    if (-not $text.Contains($oldNormalised)) {
        throw "Could not apply Phase 2 change '$Description' in '$RelativePath'."
    }
    [System.IO.File]::WriteAllText($path, $text.Replace($oldNormalised, $newNormalised), $utf8NoBom)
    Write-Host "  $Description" -ForegroundColor Green
}

$ribbonFile = "src\CE.Tools.Civil3D\PluginEntry.cs"
$oldParkingTail = @'
                    Cmd("Boundary Parking Information", "CE_PARKOPTIONSINFO ", "Review current linked parking settings, capacity and source-boundary state."),
                    Cmd("Clear Boundary Parking Option", "CE_PARKOPTIONSCLEAR ", "Remove only CE parking bays linked to the selected source boundary.")),
'@
$newParkingTail = @'
                    Cmd("Boundary Parking Information", "CE_PARKOPTIONSINFO ", "Review current linked parking settings, capacity and source-boundary state."),
                    Cmd("Clear Boundary Parking Option", "CE_PARKOPTIONSCLEAR ", "Remove only CE parking bays linked to the selected source boundary."),
                    Cmd("Dynamic Parking Monitor", "CE_PARKAUTOMONITOR ", "Enable, disable, refresh or inspect automatic linked parking and grading refresh after boundary grip edits."),
                    Cmd("Refresh All Linked Parking", "CE_PARKAUTOREFRESHALL ", "Immediately refresh all linked boundary parking options and parking grading guides."),
                    Cmd("Dynamic Parking Status", "CE_PARKAUTOSTATUS ", "Show linked boundaries, pending updates, last refresh and last failure.")),
'@
Replace-ExactText `
    -RelativePath $ribbonFile `
    -OldText $oldParkingTail `
    -NewText $newParkingTail `
    -Description "add automatic linked parking refresh controls"

$oldGradingTail = @'
                    Cmd("Identify Candidate Low Points", "CE_LOWPOINTS ", "Mark local and global low points on selected feature lines and polylines."),
                    Cmd("Clear Grading Review Graphics", "CE_GRADINGREVIEWCLEAR ", "Erase only CE-generated low-slope and low-point review graphics."))));
'@
$newGradingTail = @'
                    Cmd("Identify Candidate Low Points", "CE_LOWPOINTS ", "Mark local and global low points on selected feature lines and polylines."),
                    Cmd("Clear Grading Review Graphics", "CE_GRADINGREVIEWCLEAR ", "Erase only CE-generated low-slope and low-point review graphics."),
                    Cmd("Parking Grading Guide Tools", "CE_PARKGRADETOOLS ", "Open create, refresh, information and clear workflows for linked parking grading guides."),
                    Cmd("Create Parking Grading Guides", "CE_PARKGRADECREATE ", "Create linked 3D guides for a selected low point, centre crown or centre valley at a specified slope."),
                    Cmd("Refresh Parking Grading Guides", "CE_PARKGRADEREFRESH ", "Recalculate linked grading guides from the current parking boundary."),
                    Cmd("Parking Grading Guide Information", "CE_PARKGRADEINFO ", "Review mode, slope, reference elevation, spacing, low point and automatic-refresh state."),
                    Cmd("Clear Parking Grading Guides", "CE_PARKGRADECLEAR ", "Erase only linked CE parking grading guide geometry."))));
'@
Replace-ExactText `
    -RelativePath $ribbonFile `
    -OldText $oldGradingTail `
    -NewText $newGradingTail `
    -Description "add linked parking grading guide alternatives"

$oldInitialize = @'
            DynamicSectionUpdateManager.Initialize();
            DynamicIntersectionUpdateManager.Initialize();
            CommentAutoRefreshManager.Initialize();
            AcApplication.Idle += OnApplicationIdle;
'@
$newInitialize = @'
            DynamicSectionUpdateManager.Initialize();
            DynamicIntersectionUpdateManager.Initialize();
            CommentAutoRefreshManager.Initialize();
            ParkingOptionAutoRefreshManager.Initialize();
            AcApplication.Idle += OnApplicationIdle;
'@
Replace-ExactText `
    -RelativePath $ribbonFile `
    -OldText $oldInitialize `
    -NewText $newInitialize `
    -Description "initialise dynamic parking boundary monitor"

$oldTerminate = @'
            AcApplication.Idle -= OnApplicationIdle;
            CommentAutoRefreshManager.Terminate();
            DynamicIntersectionUpdateManager.Terminate();
            DynamicSectionUpdateManager.Terminate();
'@
$newTerminate = @'
            AcApplication.Idle -= OnApplicationIdle;
            ParkingOptionAutoRefreshManager.Terminate();
            CommentAutoRefreshManager.Terminate();
            DynamicIntersectionUpdateManager.Terminate();
            DynamicSectionUpdateManager.Terminate();
'@
Replace-ExactText `
    -RelativePath $ribbonFile `
    -OldText $oldTerminate `
    -NewText $newTerminate `
    -Description "terminate dynamic parking boundary monitor cleanly"

Write-Host "Master Items Phase 2 dynamic parking and grading source is wired." -ForegroundColor Green

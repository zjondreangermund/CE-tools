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
    if (-not (Test-Path $path)) { throw "Quantity comment source was not found: $RelativePath" }
    $text = [System.IO.File]::ReadAllText($path).Replace("`r`n", "`n")
    $oldNormalised = $OldText.Replace("`r`n", "`n")
    $newNormalised = $NewText.Replace("`r`n", "`n")
    if ($text.Contains($newNormalised) -and -not $text.Contains($oldNormalised)) { return }
    if (-not $text.Contains($oldNormalised)) {
        throw "Could not apply quantity comment '$Description' in '$RelativePath'."
    }
    [System.IO.File]::WriteAllText($path, $text.Replace($oldNormalised, $newNormalised), $utf8NoBom)
    Write-Host "  $Description" -ForegroundColor Green
}

$presentationFile = "src\CE.Tools.Civil3D\CommentPresentationCommands.cs"
$oldRefreshStart = @'
            try
            {
                summary.CoordinateFollowers +=
                    PolylineDirectionCommands.RefreshLinkedArrows(document);
            }
            catch
            {
                summary.Failed++;
            }

            List<LinkedTableItem> tables = ReadLinkedTables(document.Database);
'@
$newRefreshStart = @'
            try
            {
                summary.CoordinateFollowers +=
                    PolylineDirectionCommands.RefreshLinkedArrows(document);
            }
            catch
            {
                summary.Failed++;
            }
            summary.BoqTables += SewerExcavationCommentCommands.RefreshAll(document);

            List<LinkedTableItem> tables = ReadLinkedTables(document.Database);
'@
Replace-ExactText `
    -RelativePath $presentationFile `
    -OldText $oldRefreshStart `
    -NewText $newRefreshStart `
    -Description "include linked sewer excavation schedules in CE_REFRESHALL"

$productionFile = "src\CE.Tools.Civil3D\ProductionCommentCommands.cs"
$oldBoqChoice = @'
                    new ProductionChoice("Export Bulk-water BOQ to Excel", "CE_BOQBULKWATER "),
                    new ProductionChoice("Refresh all linked coordinates, BOQs, surfaces and corridors", "CE_REFRESHALL "),
'@
$newBoqChoice = @'
                    new ProductionChoice("Export Bulk-water BOQ to Excel", "CE_BOQBULKWATER "),
                    new ProductionChoice("Build linked sewer excavation, bedding and backfill schedule", "CE_SEWEREXCAVATION "),
                    new ProductionChoice("Refresh linked sewer excavation schedule", "CE_SEWEREXCAVATIONREFRESH "),
                    new ProductionChoice("Review sewer excavation source links and assumptions", "CE_SEWEREXCAVATIONINFO "),
                    new ProductionChoice("Export sewer excavation schedule to Excel", "CE_SEWEREXCAVATIONEXPORT "),
                    new ProductionChoice("Refresh all linked coordinates, BOQs, excavation schedules, surfaces and corridors", "CE_REFRESHALL "),
'@
Replace-ExactText `
    -RelativePath $productionFile `
    -OldText $oldBoqChoice `
    -NewText $newBoqChoice `
    -Description "add sewer excavation workflows to the BOQ centre"

$ribbonFile = "src\CE.Tools.Civil3D\PluginEntry.cs"
$oldBoqCentre = @'
                    Cmd("Dynamic BOQ and Quantity Centre", "CE_BOQCENTER ", "Open all linked BOQ build, refresh, discipline export, total and refresh workflows in one window.")),
'@
$newBoqCentre = @'
                    Cmd("Dynamic BOQ and Quantity Centre", "CE_BOQCENTER ", "Open all linked BOQ build, refresh, discipline export, total and refresh workflows in one window."),
                    Cmd("Linked Sewer Excavation Schedule", "CE_SEWEREXCAVATION ", "Calculate pipe length, trench width/depth, excavation, bedding and backfill from selected sewer pipes."),
                    Cmd("Refresh Sewer Excavation Schedule", "CE_SEWEREXCAVATIONREFRESH ", "Recalculate a linked sewer excavation schedule from current pipe lengths, sizes and cover."),
                    Cmd("Sewer Excavation Information", "CE_SEWEREXCAVATIONINFO ", "Review stored source handles and trench calculation assumptions."),
                    Cmd("Export Sewer Excavation to Excel", "CE_SEWEREXCAVATIONEXPORT ", "Refresh and export a linked sewer excavation schedule to an Excel workbook.")),
'@
Replace-ExactText `
    -RelativePath $ribbonFile `
    -OldText $oldBoqCentre `
    -NewText $newBoqCentre `
    -Description "add linked sewer excavation commands to the Quantity ribbon menu"

Write-Host "Linked sewer excavation quantity comments are wired." -ForegroundColor Green

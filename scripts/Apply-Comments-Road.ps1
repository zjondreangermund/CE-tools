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
    if (-not (Test-Path $path)) {
        throw "Road comment source file was not found: $RelativePath"
    }

    $text = [System.IO.File]::ReadAllText($path).Replace("`r`n", "`n")
    $oldNormalised = $OldText.Replace("`r`n", "`n")
    $newNormalised = $NewText.Replace("`r`n", "`n")
    if ($text.Contains($newNormalised) -and -not $text.Contains($oldNormalised)) {
        return
    }
    if (-not $text.Contains($oldNormalised)) {
        throw "Could not apply road comment '$Description' in '$RelativePath'."
    }

    [System.IO.File]::WriteAllText(
        $path,
        $text.Replace($oldNormalised, $newNormalised),
        $utf8NoBom)
    Write-Host "  $Description" -ForegroundColor Green
}

$roadFile = "src\CE.Tools.Civil3D\RoadProductionCommentCommands.cs"
Replace-ExactText `
    -RelativePath $roadFile `
    -OldText '            while (reserved.Contains(candidate)) candidate = value + "-" + suffix++.ToString(CultureInfo.InvariantCulture);' `
    -NewText @'
            while (reserved.Contains(candidate))
                candidate = value + "-" + (suffix++).ToString(CultureInfo.InvariantCulture);
'@ `
    -Description "fix sequential road-name suffix formatting"

$ribbonFile = "src\CE.Tools.Civil3D\PluginEntry.cs"
$oldAlignmentTail = @'
                    Cmd("Station and Offset", "CE_ALSTOFF ", "Report station and signed offset."),
                    Cmd("Station-Offset Annotation", "CE_ALLABELX ", "Create an MLeader, MText or COGO point using shared annotation settings.")),
'@
$newAlignmentTail = @'
                    Cmd("Station and Offset", "CE_ALSTOFF ", "Report station and signed offset."),
                    Cmd("Station-Offset Annotation", "CE_ALLABELX ", "Create an MLeader, MText or COGO point using shared annotation settings."),
                    Cmd("Road Production Centre", "CE_ROADPRODUCTION ", "Open sequential road alignment, EG profile, corridor, style, report, BOQ and refresh workflows in one window."),
                    Cmd("Create Sequential Road Alignments", "CE_ROADALIGN ", "Create named Civil 3D road alignments from selected open polylines using Project Style Centre choices."),
                    Cmd("Create Road EG Profiles and Views", "CE_ROADPROFILES ", "Create an existing-ground profile and styled profile view for every CE road alignment."),
                    Cmd("Create Road Corridors", "CE_ROADCORRIDORS ", "Create corridors from CE road alignment/profile pairs and a selected Civil 3D assembly."),
                    Cmd("Road Production Information", "CE_ROADPRODUCTIONINFO ", "Show generated road alignments, profiles, corridors and resolved project styles and optionally place a table.")),
'@
Replace-ExactText `
    -RelativePath $ribbonFile `
    -OldText $oldAlignmentTail `
    -NewText $newAlignmentTail `
    -Description "add batch road alignment profile and corridor commands to the Alignment ribbon menu"

Write-Host "Batch road-production active comments are wired." -ForegroundColor Green

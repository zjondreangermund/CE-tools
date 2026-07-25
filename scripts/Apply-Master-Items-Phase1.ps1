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
        throw "Master-item source file was not found: $RelativePath"
    }

    $text = [System.IO.File]::ReadAllText($path).Replace("`r`n", "`n")
    $oldNormalised = $OldText.Replace("`r`n", "`n")
    $newNormalised = $NewText.Replace("`r`n", "`n")
    if ($text.Contains($newNormalised) -and -not $text.Contains($oldNormalised)) {
        return
    }
    if (-not $text.Contains($oldNormalised)) {
        throw "Could not apply master-item change '$Description' in '$RelativePath'."
    }

    [System.IO.File]::WriteAllText(
        $path,
        $text.Replace($oldNormalised, $newNormalised),
        $utf8NoBom)
    Write-Host "  $Description" -ForegroundColor Green
}

$ribbonFile = "src\CE.Tools.Civil3D\PluginEntry.cs"
$oldParking = @'
                    Cmd("Validate and Number Bays", "CE_PKNUMBER2 ", "Validate objects and number accepted bays using the shared annotation height."),
                    Cmd("Number Bays (Legacy Shared)", "CE_PKNUMBERX ", "Run the shared-height parking numbering command.")),
'@
$newParking = @'
                    Cmd("Validate and Number Bays", "CE_PKNUMBER2 ", "Validate objects and number accepted bays using the shared annotation height."),
                    Cmd("Number Bays (Legacy Shared)", "CE_PKNUMBERX ", "Run the shared-height parking numbering command."),
                    Cmd("Boundary Parking Alternatives", "CE_PARKOPTIONS ", "Compare 90, 60 and 45 degree parking alternatives inside a selected closed boundary and create the chosen option."),
                    Cmd("Refresh Boundary Parking Option", "CE_PARKOPTIONSREFRESH ", "Regenerate linked parking bays after the source boundary changes."),
                    Cmd("Boundary Parking Information", "CE_PARKOPTIONSINFO ", "Review current linked parking settings, capacity and source-boundary state."),
                    Cmd("Clear Boundary Parking Option", "CE_PARKOPTIONSCLEAR ", "Remove only CE parking bays linked to the selected source boundary.")),
'@
Replace-ExactText `
    -RelativePath $ribbonFile `
    -OldText $oldParking `
    -NewText $newParking `
    -Description "add boundary-driven parking alternatives and refresh commands"

Write-Host "Master Items Phase 1 source normalisation completed." -ForegroundColor Green

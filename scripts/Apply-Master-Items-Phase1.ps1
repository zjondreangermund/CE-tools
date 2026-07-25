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

$oldSkewTail = @'
                    Cmd("Parking Skew Settings", "CE_PKSKSETTINGS ", "Store the 2500 mm standard, units conversion, tolerance, layers and annotation sizes."),
                    Cmd("Parking Skew Information", "CE_PKSKINFO ", "Review generated objects, live source handles and current width settings."))));
'@
$newSkewTail = @'
                    Cmd("Parking Skew Settings", "CE_PKSKSETTINGS ", "Store the 2500 mm standard, units conversion, tolerance, layers and annotation sizes."),
                    Cmd("Parking Skew Information", "CE_PKSKINFO ", "Review generated objects, live source handles and current width settings.")),
                Menu("CE_TOOLS_GRADING_DIAGNOSTICS_MENU", "Grading &\nDrainage Review", "Highlight low grades and candidate low points without changing source design geometry.",
                    Cmd("Grading Diagnostic Tools", "CE_GRADINGDIAGNOSTICS ", "Open low-slope, low-point and clear-review workflows."),
                    Cmd("Highlight Grades Below Limit", "CE_LOWSLOPE ", "Create removable review lines and labels where the absolute grade is below the selected threshold, default 0.5 percent."),
                    Cmd("Identify Candidate Low Points", "CE_LOWPOINTS ", "Mark local and global low points on selected feature lines and polylines."),
                    Cmd("Clear Grading Review Graphics", "CE_GRADINGREVIEWCLEAR ", "Erase only CE-generated low-slope and low-point review graphics."))));
'@
Replace-ExactText `
    -RelativePath $ribbonFile `
    -OldText $oldSkewTail `
    -NewText $newSkewTail `
    -Description "add low-slope and low-point grading diagnostics"

$oldHatchTail = @'
                    Cmd("Send Hatches Behind Linework", "CE_HATCHBACK ", "Move selected hatches to the back of draw order."),
                    Cmd("Hatch Settings Window", "CE_HATCHUI ", "Choose hatch creation and editing actions in a CE Tools popup window."))));
'@
$newHatchTail = @'
                    Cmd("Send Hatches Behind Linework", "CE_HATCHBACK ", "Move selected hatches to the back of draw order."),
                    Cmd("Hatch Settings Window", "CE_HATCHUI ", "Choose hatch creation and editing actions in a CE Tools popup window.")),
                Menu("CE_TOOLS_BACKGROUND_XREF_MENU", "Background &\nXREF Tools", "Audit messy architectural/survey backgrounds, create controlled light copies, split selections into XREF files and create revision backups.",
                    Cmd("Background and XREF Tools", "CE_BACKGROUNDTOOLS ", "Open background audit, light-copy, XREF split, information and backup workflows."),
                    Cmd("Audit Background Drawing", "CE_BACKGROUNDREVIEW ", "Report selected background layer, type, colour, locked-layer and XREF concentration without modifying objects."),
                    Cmd("Create Controlled Light Background", "CE_BACKGROUNDLIGHT ", "Copy or move selected objects to CE light-background layers and keep the result selected for Properties inspection."),
                    Cmd("Split Selection to XREF", "CE_XREFSPLIT ", "Write selected objects to a separate DWG, attach it as an XREF and optionally replace the source objects."),
                    Cmd("XREF Information", "CE_XREFINFO ", "Report attached XREF names, paths and AutoCAD states."),
                    Cmd("Create XREF Revision Backup", "CE_XREFBACKUP ", "Create a timestamped Revisions-folder copy of the selected XREF source drawing."))));
'@
Replace-ExactText `
    -RelativePath $ribbonFile `
    -OldText $oldHatchTail `
    -NewText $newHatchTail `
    -Description "add background cleanup and XREF project management commands"

# FeatureLine closure differs between Civil 3D API versions. The point sequence
# is sufficient for diagnostics and avoids depending on a version-specific
# Closed/IsClosed property.
$gradingFile = "src\CE.Tools.Civil3D\GradingDrainageDiagnosticCommands.cs"
Replace-ExactText `
    -RelativePath $gradingFile `
    -OldText @'
                if (featureLine.Closed && points.Count > 1)
                    points.Add(points[0]);
'@ `
    -NewText @'
                if (points.Count > 2 &&
                    PlanDistance(points[0], points[points.Count - 1]) <= GeometryTolerance)
                {
                    points[points.Count - 1] = points[0];
                }
'@ `
    -Description "avoid version-specific FeatureLine closed property"

Write-Host "Master Items Phase 1 source normalisation completed." -ForegroundColor Green

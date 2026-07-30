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
        [switch]$Optional
    )

    $path = Join-Path $repositoryRoot $RelativePath
    if (-not (Test-Path $path)) {
        throw "Discipline comment source file was not found: $RelativePath"
    }

    $text = [System.IO.File]::ReadAllText($path).Replace("`r`n", "`n")
    $oldNormalised = $OldText.Replace("`r`n", "`n")
    $newNormalised = $NewText.Replace("`r`n", "`n")
    if ($text.Contains($newNormalised) -and -not $text.Contains($oldNormalised)) {
        return
    }
    if (-not $text.Contains($oldNormalised)) {
        if ($Optional) {
            Write-Warning "Skipped optional discipline comment change '$Description' in '$RelativePath' because the ribbon layout has already changed."
            return
        }
        throw "Could not apply discipline comment change '$Description' in '$RelativePath'."
    }
    [System.IO.File]::WriteAllText(
        $path,
        $text.Replace($oldNormalised, $newNormalised),
        $utf8NoBom)
    Write-Host "  $Description" -ForegroundColor Green
}

$ribbonFile = "src\CE.Tools.Civil3D\PluginEntry.cs"

$oldFeatureLineTail = @'
                    Cmd("Linked Offset Information", "CE_FLRELINFO ", "Report a linked offset relationship."),
                    Cmd("Detach Linked Offset", "CE_FLRELDETACH ", "Keep geometry but remove the CE relationship.")),
'@
$newFeatureLineTail = @'
                    Cmd("Linked Offset Information", "CE_FLRELINFO ", "Report a linked offset relationship."),
                    Cmd("Detach Linked Offset", "CE_FLRELDETACH ", "Keep geometry but remove the CE relationship."),
                    Cmd("Detailed Feature Line Popup Report", "CE_FLREPORT2 ", "Show feature-line site, style, colour, length and elevation information and optionally place a table."),
                    Cmd("Feature Line Colour and Site", "CE_FLAPPEARANCE ", "Assign an AutoCAD colour and Civil 3D site to selected feature lines in one window."),
                    Cmd("Annotate Every Feature Line Vertex", "CE_FLVERTEXLABELS ", "Create Point Name, X, Y and Z annotations at every selected feature-line vertex using shared settings.")),
'@
Replace-ExactText `
    -RelativePath $ribbonFile `
    -OldText $oldFeatureLineTail `
    -NewText $newFeatureLineTail `
    -Description "add feature-line popup, colour/site and vertex annotation commands"

$oldProfileTail = @'
                    Cmd("Profile Report", "CE_PRREPORTUI ", "Show profile details in a pop-up and optionally place a table."),
                    Cmd("Station Elevation", "CE_PRELEV ", "Report elevation and grade at a station."),
                    Cmd("Profile Annotation", "CE_PRLABELX ", "Create an MLeader, MText or COGO point using shared annotation settings.")),
'@
$newProfileTail = @'
                    Cmd("Profile Report", "CE_PRREPORTUI ", "Show profile details in a pop-up and optionally place a table."),
                    Cmd("Station Elevation", "CE_PRELEV ", "Report elevation and grade at a station."),
                    Cmd("Profile Annotation", "CE_PRLABELX ", "Create an MLeader, MText or COGO point using shared annotation settings."),
                    Cmd("All Profiles Popup Report", "CE_PROFILEREPORT2 ", "Show every profile, alignment, style, station range and endpoint elevations and optionally place a table."),
                    Cmd("Profile Elevation Popup and Annotation", "CE_PROFILEELEVATION2 ", "Select a profile from a window, report elevation and grade and optionally create an annotation.")),
'@
Replace-ExactText `
    -RelativePath $ribbonFile `
    -OldText $oldProfileTail `
    -NewText $newProfileTail `
    -Description "add profile inventory and elevation popup commands"

$oldSurfaceTail = @'
                    Cmd("Surface Elevation", "CE_SFELEV ", "Report an elevation at a point."),
                    Cmd("Surface Annotation", "CE_SFLABELX ", "Create an MLeader, MText or COGO point using shared annotation settings."),
                    Cmd("Compare Surfaces", "CE_SFCOMPARE ", "Compare two surface elevations.")),
'@
$newSurfaceTail = @'
                    Cmd("Surface Elevation", "CE_SFELEV ", "Report an elevation at a point."),
                    Cmd("Surface Annotation", "CE_SFLABELX ", "Create an MLeader, MText or COGO point using shared annotation settings."),
                    Cmd("Compare Surfaces", "CE_SFCOMPARE ", "Compare two surface elevations."),
                    Cmd("All Surfaces Popup Report", "CE_SURFACEREPORT2 ", "Show surface type, style, elevation range and rebuild state and optionally place a table."),
                    Cmd("Surface Elevation Popup and Annotation", "CE_SURFACEELEVATION2 ", "Select a surface in a window, report X, Y and Z and optionally create an annotation."),
                    Cmd("Surface Comparison Popup and Annotation", "CE_SURFACECOMPARE2 ", "Select base and final surfaces, show elevation difference in a popup/table and optionally annotate it."),
                    Cmd("Rebuild All Surfaces and Corridors", "CE_REBUILDALL ", "Rebuild all accessible Civil 3D surfaces and corridors.")),
'@
Replace-ExactText `
    -RelativePath $ribbonFile `
    -OldText $oldSurfaceTail `
    -NewText $newSurfaceTail `
    -Description "add surface inventory, elevation, comparison and rebuild commands"

$oldWaterTail = @'
                    Cmd("Refresh Asset Review Markers", "CE_WATERPLACEREFRESH ", "Recalculate linked water-asset review locations from current alignment geometry."),
                    Cmd("Water Production Settings", "CE_WATERSETTINGS ", "Store styles, layers, valve spacing, hydrant spacing and marker size."),
                    Cmd("Water Production Information", "CE_WATERINFO ", "Report water links, generated output, settings and refresh status."))))
'@
$newWaterTail = @'
                    Cmd("Refresh Asset Review Markers", "CE_WATERPLACEREFRESH ", "Recalculate linked water-asset review locations from current alignment geometry."),
                    Cmd("Water Production Settings", "CE_WATERSETTINGS ", "Store styles, layers, valve spacing, hydrant spacing and marker size."),
                    Cmd("Water Production Information", "CE_WATERINFO ", "Report water links, generated output, settings and refresh status."),
                    Cmd("All Network Summary Popup", "CE_NETWORKREPORT2 ", "Show gravity and pressure network pipe/run, structure, fitting, appurtenance and length totals and optionally place a table."),
                    Cmd("Selected Network Part Data", "CE_NETWORKPARTREPORT2 ", "Show selected pipe, structure, fitting and appurtenance details and optionally place a table."),
                    Cmd("Service Alignment and Profile Production Window", "CE_SERVICEPROFILES ", "Open one window for stormwater, sewer and water sequencing, alignments, profiles and water asset markers."),
                    Cmd("Network Data and Refresh Window", "CE_NETWORKDATA ", "Open network reports, production information and shared refresh workflows."))))
'@
Replace-ExactText `
    -RelativePath $ribbonFile `
    -OldText $oldWaterTail `
    -NewText $newWaterTail `
    -Description "add shared network reports and service production launchers"

$oldBoqTail = @'
                    Cmd("Bulk-water BOQ Excel", "CE_BOQBULKWATER ", "Export bulk pipeline, storage, pump and fitting quantities."),
                    Cmd("Total Length", "CE_TLENGTH ", "Preserved quick total of selected curve lengths by layer."),
                    Cmd("Total Area", "CE_TAREA ", "Preserved quick total of selected areas by layer.")),
'@
$newBoqTail = @'
                    Cmd("Bulk-water BOQ Excel", "CE_BOQBULKWATER ", "Export bulk pipeline, storage, pump and fitting quantities."),
                    Cmd("Total Length", "CE_TLENGTH ", "Preserved quick total of selected curve lengths by layer."),
                    Cmd("Total Area", "CE_TAREA ", "Preserved quick total of selected areas by layer."),
                    Cmd("Dynamic BOQ and Quantity Centre", "CE_BOQCENTER ", "Open all linked BOQ build, refresh, discipline export, total and refresh workflows in one window.")),
'@
Replace-ExactText `
    -RelativePath $ribbonFile `
    -OldText $oldBoqTail `
    -NewText $newBoqTail `
    -Description "add the dynamic BOQ and quantity centre"

$oldReportTail = @'
                    Cmd("Water Report", "CE_REPORTWATER ", "Generate the water design report."),
                    Cmd("Bulk-water Report", "CE_REPORTBULKWATER ", "Generate the bulk-water design report."),
                    Cmd("Export Design Report", "CE_REPORTEXPORT ", "Export a full or discipline design inventory as an .xlsx workbook.")),
'@
$newReportTail = @'
                    Cmd("Water Report", "CE_REPORTWATER ", "Generate the water design report."),
                    Cmd("Bulk-water Report", "CE_REPORTBULKWATER ", "Generate the bulk-water design report."),
                    Cmd("Export Design Report", "CE_REPORTEXPORT ", "Export a full or discipline design inventory as an .xlsx workbook."),
                    Cmd("Design Report Centre", "CE_REPORTCENTER ", "Open all full, discipline, network and shared refresh reports in one window.")),
'@
Replace-ExactText `
    -RelativePath $ribbonFile `
    -OldText $oldReportTail `
    -NewText $newReportTail `
    -Description "add the consolidated design report centre"

$oldProductionTail = @'
                    Cmd("Create A-Series Drawing Books", "CE_DRAWINGBOOK ", "Create or refresh A4/A3 client and A1/A0 construction layouts."),
                    Cmd("Export Drawing Book Index", "CE_BOOKINDEX ", "Export the standard and existing layout register to Excel."))))
'@
$newProductionTail = @'
                    Cmd("Create A-Series Drawing Books", "CE_DRAWINGBOOK ", "Create or refresh A4/A3 client and A1/A0 construction layouts."),
                    Cmd("Export Drawing Book Index", "CE_BOOKINDEX ", "Export the standard and existing layout register to Excel."),
                    Cmd("Plan Production and Project Books", "CE_PRODUCTIONCENTER ", "Open summary, client-book, drawing-book, index, publish and output-location workflows in one window."),
                    Cmd("Print and Publish Centre", "CE_PRINTCENTER ", "Prepare A-series/client layouts and open native AutoCAD Plot or Publish."),
                    Cmd("Batch Publish to PDF", "CE_BATCHPUBLISH ", "Open AutoCAD Publish for generated A1/A0 and A4/A3 layouts."),
                    Cmd("Output Locations", "CE_OUTPUTLOCATION ", "Show where linked books, Excel exports and published PDFs are stored."))))
'@
Replace-ExactText `
    -RelativePath $ribbonFile `
    -OldText $oldProductionTail `
    -NewText $newProductionTail `
    -Description "add plan production, print and output-location centres" `
    -Optional

Write-Host "Feature-line, profile, surface, network, BOQ, report and production active comments are wired." -ForegroundColor Green

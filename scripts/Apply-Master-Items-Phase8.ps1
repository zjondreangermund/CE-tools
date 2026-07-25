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
    if (-not (Test-Path $path)) { throw "Phase 8 source was not found: $RelativePath" }
    $text = [System.IO.File]::ReadAllText($path).Replace("`r`n", "`n")
    $oldNormalised = $OldText.Replace("`r`n", "`n")
    $newNormalised = $NewText.Replace("`r`n", "`n")
    if ($text.Contains($newNormalised) -and -not $text.Contains($oldNormalised)) { return }
    if (-not $text.Contains($oldNormalised)) {
        throw "Could not apply Phase 8 change '$Description' in '$RelativePath'."
    }
    [System.IO.File]::WriteAllText(
        $path,
        $text.Replace($oldNormalised, $newNormalised),
        $utf8NoBom)
    Write-Host "  $Description" -ForegroundColor Green
}

$ribbonFile = "src\CE.Tools.Civil3D\PluginEntry.cs"
$oldIntegrationTail = @'
                    Cmd("Project Presentation Tools", "CE_PRESENTATIONTOOLS ", "Open preview and create workflows for the automatic project presentation."),
                    Cmd("Preview Project Presentation", "CE_PRESENTATIONPREVIEW ", "Review the planned slide titles, metrics and bullet counts before creating the PowerPoint file."),
                    Cmd("Create Project Presentation", "CE_PRESENTATIONCREATE ", "Create a non-overwriting PowerPoint presentation without PowerPoint/Office automation."))));
'@
$newIntegrationTail = @'
                    Cmd("Project Presentation Tools", "CE_PRESENTATIONTOOLS ", "Open preview and create workflows for the automatic project presentation."),
                    Cmd("Preview Project Presentation", "CE_PRESENTATIONPREVIEW ", "Review the planned slide titles, metrics and bullet counts before creating the PowerPoint file."),
                    Cmd("Create Project Presentation", "CE_PRESENTATIONCREATE ", "Create a non-overwriting PowerPoint presentation without PowerPoint/Office automation.")),
                Menu("CE_TOOLS_ENGINEERING_ASSET_MENU", "Engineering Asset\nLibrary", "Manage standards, typical details, symbols and civil/furniture assets through approval metadata, SHA-256 integrity, search and controlled DWG insertion.",
                    Cmd("Engineering Asset Library Tools", "CE_ASSETLIBTOOLS ", "Open settings, template, audit, search, insertion, information and revision-check workflows."),
                    Cmd("Engineering Asset Library Settings", "CE_ASSETLIBSETTINGS ", "Store the catalog path, drawing-units-per-metre value and default approval visibility."),
                    Cmd("Create Engineering Asset Catalog", "CE_ASSETCATALOGTEMPLATE ", "Create a non-overwriting CSV catalog and standard library folders for details, standards, symbols, furniture and specifications."),
                    Cmd("Audit Engineering Asset Catalog", "CE_ASSETCATALOGAUDIT ", "Check paths, revisions, approval records, SHA-256 identities, duplicates, formats and active/superseded state."),
                    Cmd("Search Engineering Asset Library", "CE_ASSETSEARCH ", "Search active assets by ID, title, category, discipline, tags and approval visibility."),
                    Cmd("Insert Controlled DWG Asset", "CE_ASSETINSERT ", "Insert a checksum-matched DWG asset with explicit units scaling and traceable XData; source files remain read-only."),
                    Cmd("Inserted Asset Information", "CE_ASSETINFO ", "Review the selected inserted block's catalog, source, revision, approval status, checksum, scale and current source state."),
                    Cmd("Check Inserted Asset Revisions", "CE_ASSETREVISIONCHECK ", "Compare tagged inserted assets with current catalog/source identities without automatic replacement."))));
'@
Replace-ExactText `
    -RelativePath $ribbonFile `
    -OldText $oldIntegrationTail `
    -NewText $newIntegrationTail `
    -Description "add controlled engineering asset library commands"

Write-Host "Master Items Phase 8 engineering asset library source is wired." -ForegroundColor Green

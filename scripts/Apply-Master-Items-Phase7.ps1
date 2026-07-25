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
    if (-not (Test-Path $path)) { throw "Phase 7 source was not found: $RelativePath" }
    $text = [System.IO.File]::ReadAllText($path).Replace("`r`n", "`n")
    $oldNormalised = $OldText.Replace("`r`n", "`n")
    $newNormalised = $NewText.Replace("`r`n", "`n")
    if ($text.Contains($newNormalised) -and -not $text.Contains($oldNormalised)) { return }
    if (-not $text.Contains($oldNormalised)) {
        throw "Could not apply Phase 7 change '$Description' in '$RelativePath'."
    }
    [System.IO.File]::WriteAllText(path: $path, contents: $text.Replace($oldNormalised, $newNormalised), encoding: $utf8NoBom)
    Write-Host "  $Description" -ForegroundColor Green
}

$ribbonFile = "src\CE.Tools.Civil3D\PluginEntry.cs"
$oldIntegrationTail = @'
                    Cmd("Export Road Drive Camera Path", "CE_ROADDRIVEEXPORT ", "Export station/X/Y/Z/heading/pitch frames for external visualisation after verifying coordinate and camera conventions."),
                    Cmd("Road Drive Review Information", "CE_ROADDRIVEINFO ", "Review stored alignment, profile, criteria, issue and source-handle metadata."),
                    Cmd("Clear Road Drive Review", "CE_ROADDRIVECLEAR ", "Erase only tagged CE Tools road-drive paths, issue markers and labels."))));
'@
$newIntegrationTail = @'
                    Cmd("Export Road Drive Camera Path", "CE_ROADDRIVEEXPORT ", "Export station/X/Y/Z/heading/pitch frames for external visualisation after verifying coordinate and camera conventions."),
                    Cmd("Road Drive Review Information", "CE_ROADDRIVEINFO ", "Review stored alignment, profile, criteria, issue and source-handle metadata."),
                    Cmd("Clear Road Drive Review", "CE_ROADDRIVECLEAR ", "Erase only tagged CE Tools road-drive paths, issue markers and labels.")),
                Menu("CE_TOOLS_PRESENTATION_MENU", "Project\nPresentation", "Generate a dependency-free 16:9 PowerPoint project-review deck from current drawing metadata, Civil inventory, production status and model-health findings.",
                    Cmd("Project Presentation Tools", "CE_PRESENTATIONTOOLS ", "Open preview and create workflows for the automatic project presentation."),
                    Cmd("Preview Project Presentation", "CE_PRESENTATIONPREVIEW ", "Review the planned slide titles, metrics and bullet counts before creating the PowerPoint file."),
                    Cmd("Create Project Presentation", "CE_PRESENTATIONCREATE ", "Create a non-overwriting PowerPoint presentation without PowerPoint/Office automation."))));
'@
Replace-ExactText `
    -RelativePath $ribbonFile `
    -OldText $oldIntegrationTail `
    -NewText $newIntegrationTail `
    -Description "add automatic project presentation commands"

Write-Host "Master Items Phase 7 presentation source is wired." -ForegroundColor Green

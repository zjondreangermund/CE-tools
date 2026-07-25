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
    if (-not (Test-Path $path)) { throw "Detailed-section source was not found: $RelativePath" }
    $text = [System.IO.File]::ReadAllText($path).Replace("`r`n", "`n")
    $oldNormalised = $OldText.Replace("`r`n", "`n")
    $newNormalised = $NewText.Replace("`r`n", "`n")
    if ($text.Contains($newNormalised) -and -not $text.Contains($oldNormalised)) { return }
    if (-not $text.Contains($oldNormalised)) {
        throw "Could not apply detailed-section change '$Description' in '$RelativePath'."
    }
    [System.IO.File]::WriteAllText(
        $path,
        $text.Replace($oldNormalised, $newNormalised),
        $utf8NoBom)
    Write-Host "  $Description" -ForegroundColor Green
}

$ribbonFile = "src\CE.Tools.Civil3D\PluginEntry.cs"
$oldTail = @'
                    Cmd("Dynamic-section Monitor", "CE_XSMONITOR ", "Report automatic update-manager and pending-refresh status."),
                    Cmd("Refresh All Dynamic Data", "CE_REFRESHALL ", "Refresh linked coordinate followers, coordinate tables and BOQs and rebuild Civil surfaces and corridors."),
                    Cmd("Rebuild All Civil Objects", "CE_REBUILDALL ", "Rebuild all accessible surfaces and corridors in the current Civil 3D drawing."),
                    Cmd("Automatic Linked Refresh", "CE_AUTOREFRESH ", "Turn automatic linked coordinate and BOQ refresh on or off and show its status."),
                    Cmd("Dynamic Refresh Status", "CE_REFRESHSTATUS ", "Show linked table, follower, pending and last-refresh information."))));
'@
$newTail = @'
                    Cmd("Dynamic-section Monitor", "CE_XSMONITOR ", "Report automatic update-manager and pending-refresh status."),
                    Cmd("Refresh All Dynamic Data", "CE_REFRESHALL ", "Refresh linked coordinate followers, coordinate tables and BOQs and rebuild Civil surfaces and corridors."),
                    Cmd("Rebuild All Civil Objects", "CE_REBUILDALL ", "Rebuild all accessible surfaces and corridors in the current Civil 3D drawing."),
                    Cmd("Automatic Linked Refresh", "CE_AUTOREFRESH ", "Turn automatic linked coordinate and BOQ refresh on or off and show its status."),
                    Cmd("Dynamic Refresh Status", "CE_REFRESHSTATUS ", "Show linked table, follower, pending and last-refresh information."),
                    Cmd("Detailed Section Tools", "CE_SECTIONDETAILTOOLS ", "Open linked detailed-section creation, refresh, information and clear workflows."),
                    Cmd("Create Detailed Section Annotation", "CE_SECTIONDETAILCREATE ", "Create overall dimensions, component labels, discipline notes and a linked component register from selected section geometry."),
                    Cmd("Refresh Detailed Section Annotation", "CE_SECTIONDETAILREFRESH ", "Rebuild a linked detailed-section annotation set from current source geometry."),
                    Cmd("Detailed Section Information", "CE_SECTIONDETAILINFO ", "Review discipline, live and missing sources, settings and generated-object state."),
                    Cmd("Clear Detailed Section Annotation", "CE_SECTIONDETAILCLEAR ", "Remove only CE-generated detailed-section dimensions, labels, notes and register objects."))));
'@
Replace-ExactText `
    -RelativePath $ribbonFile `
    -OldText $oldTail `
    -NewText $newTail `
    -Description "add linked detailed-section annotation and dimensioning commands"

Write-Host "Master Items Phase 2 detailed-section source is wired." -ForegroundColor Green

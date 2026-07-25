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
    if (-not (Test-Path $path)) { throw "Project-XREF source was not found: $RelativePath" }
    $text = [System.IO.File]::ReadAllText($path).Replace("`r`n", "`n")
    $oldNormalised = $OldText.Replace("`r`n", "`n")
    $newNormalised = $NewText.Replace("`r`n", "`n")
    if ($text.Contains($newNormalised) -and -not $text.Contains($oldNormalised)) { return }
    if (-not $text.Contains($oldNormalised)) {
        throw "Could not apply project-XREF change '$Description' in '$RelativePath'."
    }
    [System.IO.File]::WriteAllText(
        $path,
        $text.Replace($oldNormalised, $newNormalised),
        $utf8NoBom)
    Write-Host "  $Description" -ForegroundColor Green
}

$ribbonFile = "src\CE.Tools.Civil3D\PluginEntry.cs"
$oldTail = @'
                    Cmd("XREF Information", "CE_XREFINFO ", "Report attached XREF names, paths and AutoCAD states."),
                    Cmd("Create XREF Revision Backup", "CE_XREFBACKUP ", "Create a timestamped Revisions-folder copy of the selected XREF source drawing."))));
'@
$newTail = @'
                    Cmd("XREF Information", "CE_XREFINFO ", "Report attached XREF names, paths and AutoCAD states."),
                    Cmd("Create XREF Revision Backup", "CE_XREFBACKUP ", "Create a timestamped Revisions-folder copy of the selected XREF source drawing."),
                    Cmd("Project XREF Management Tools", "CE_XREFPROJECTTOOLS ", "Open discipline splitting, revision dashboard, backup-all and controlled restore workflows."),
                    Cmd("Split Project by XREF Discipline", "CE_XREFDISCIPLINESPLIT ", "Group editable model-space objects by layer discipline, write new non-overwriting DWGs and attach them as XREFs."),
                    Cmd("XREF Revision Dashboard", "CE_XREFREVISIONDASH ", "Compare current XREF source hashes, timestamps and sizes with files in their Revisions folders."),
                    Cmd("Backup All XREF Sources", "CE_XREFBACKUPALL ", "Create one timestamped backup for every resolved unique XREF source file."),
                    Cmd("Restore XREF Revision", "CE_XREFRESTORE ", "Restore a selected revision only after creating a pre-restore backup and confirming the external-file change."))));
'@
Replace-ExactText `
    -RelativePath $ribbonFile `
    -OldText $oldTail `
    -NewText $newTail `
    -Description "add project-wide XREF splitting, comparison, backup and rollback commands"

Write-Host "Master Items Phase 2 project-XREF source is wired." -ForegroundColor Green

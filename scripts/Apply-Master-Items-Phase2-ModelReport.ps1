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
    if (-not (Test-Path $path)) { throw "Model-report source was not found: $RelativePath" }
    $text = [System.IO.File]::ReadAllText($path).Replace("`r`n", "`n")
    $oldNormalised = $OldText.Replace("`r`n", "`n")
    $newNormalised = $NewText.Replace("`r`n", "`n")
    if ($text.Contains($newNormalised) -and -not $text.Contains($oldNormalised)) { return }
    if (-not $text.Contains($oldNormalised)) {
        throw "Could not apply model-report change '$Description' in '$RelativePath'."
    }
    [System.IO.File]::WriteAllText(
        $path,
        $text.Replace($oldNormalised, $newNormalised),
        $utf8NoBom)
    Write-Host "  $Description" -ForegroundColor Green
}

$ribbonFile = "src\CE.Tools.Civil3D\PluginEntry.cs"
$oldTail = @'
                    Cmd("Export Design Report", "CE_REPORTEXPORT ", "Export a full or discipline design inventory as an .xlsx workbook."),
                    Cmd("Design Report Centre", "CE_REPORTCENTER ", "Open all full, discipline, network and shared refresh reports in one window.")),
'@
$newTail = @'
                    Cmd("Export Design Report", "CE_REPORTEXPORT ", "Export a full or discipline design inventory as an .xlsx workbook."),
                    Cmd("Design Report Centre", "CE_REPORTCENTER ", "Open all full, discipline, network and shared refresh reports in one window."),
                    Cmd("Civil 3D Model Audit Tools", "CE_MODELREPORTTOOLS ", "Open drawing-wide model report, summary and Excel export workflows."),
                    Cmd("Civil 3D Design Model Audit", "CE_MODELREPORT ", "Report Civil-object inventory, model health, layers, XREFs, layouts, CE links and prioritised corrective actions."),
                    Cmd("Civil 3D Model Health Summary", "CE_MODELREPORTINFO ", "Show the model-audit summary and prioritised findings without the full inventory."),
                    Cmd("Export Civil 3D Model Audit", "CE_MODELREPORTEXPORT ", "Export the complete Civil 3D model inventory and health audit to Excel.")),
'@
Replace-ExactText `
    -RelativePath $ribbonFile `
    -OldText $oldTail `
    -NewText $newTail `
    -Description "add comprehensive Civil 3D design-model audit commands"

Write-Host "Master Items Phase 2 model-report source is wired." -ForegroundColor Green

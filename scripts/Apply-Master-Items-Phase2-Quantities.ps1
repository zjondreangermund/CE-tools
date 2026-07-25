[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

function Insert-BeforeCommand {
    param(
        [Parameter(Mandatory = $true)][string]$RelativePath,
        [Parameter(Mandatory = $true)][string]$AnchorCommand,
        [Parameter(Mandatory = $true)][string]$UniqueCommand,
        [Parameter(Mandatory = $true)][string[]]$Lines,
        [Parameter(Mandatory = $true)][string]$Description
    )
    $path = Join-Path $repositoryRoot $RelativePath
    if (-not (Test-Path $path)) { throw "Quantity ribbon source was not found: $RelativePath" }
    $text = [System.IO.File]::ReadAllText($path).Replace("`r`n", "`n")
    if ($text.Contains($UniqueCommand)) { return }
    $sourceLines = [System.Collections.Generic.List[string]]($text -split "`n")
    $index = -1
    for ($i = 0; $i -lt $sourceLines.Count; $i++) {
        if ($sourceLines[$i].Contains($AnchorCommand)) {
            $index = $i
            break
        }
    }
    if ($index -lt 0) { throw "Could not find quantity ribbon anchor '$AnchorCommand'." }
    for ($i = $Lines.Length - 1; $i -ge 0; $i--) {
        $sourceLines.Insert($index, $Lines[$i])
    }
    [System.IO.File]::WriteAllText($path, ($sourceLines -join "`n"), $utf8NoBom)
    Write-Host "  $Description" -ForegroundColor Green
}

function Insert-AfterLine {
    param(
        [Parameter(Mandatory = $true)][string]$RelativePath,
        [Parameter(Mandatory = $true)][string]$Anchor,
        [Parameter(Mandatory = $true)][string]$Insertion,
        [Parameter(Mandatory = $true)][string]$Description
    )
    $path = Join-Path $repositoryRoot $RelativePath
    if (-not (Test-Path $path)) { throw "Quantity refresh source was not found: $RelativePath" }
    $text = [System.IO.File]::ReadAllText($path).Replace("`r`n", "`n")
    if ($text.Contains($Insertion)) { return }
    if (-not $text.Contains($Anchor)) { throw "Could not find refresh anchor '$Anchor'." }
    $text = $text.Replace($Anchor, $Anchor + "`n" + $Insertion)
    [System.IO.File]::WriteAllText($path, $text, $utf8NoBom)
    Write-Host "  $Description" -ForegroundColor Green
}

Insert-BeforeCommand `
    -RelativePath "src\CE.Tools.Civil3D\PluginEntry.cs" `
    -AnchorCommand '"CE_BOQBUILD "' `
    -UniqueCommand '"CE_STANDARDQTYTOOLS "' `
    -Lines @(
        '                    Cmd("Standard Quantity Template Tools", "CE_STANDARDQTYTOOLS ", "Open create, refresh, export and information workflows for linked parking/driveway and sidewalk quantity templates."),',
        '                    Cmd("Create Standard Quantity Schedule", "CE_STANDARDQTY ", "Create a linked parking/driveway or sidewalk schedule from selected area and linear source geometry."),',
        '                    Cmd("Refresh Standard Quantity Schedule", "CE_STANDARDQTYREFRESH ", "Recalculate a linked standards-based quantity schedule from current source geometry."),',
        '                    Cmd("Export Standard Quantity Schedule", "CE_STANDARDQTYEXPORT ", "Refresh and export a standard quantity schedule to Excel."),',
        '                    Cmd("Standard Quantity Information", "CE_STANDARDQTYINFO ", "Review template, source counts, thickness assumptions, cut/fill allowances and sign count."),'
    ) `
    -Description "add linked parking and sidewalk standard quantity templates"

Insert-AfterLine `
    -RelativePath "src\CE.Tools.Civil3D\CommentPresentationCommands.cs" `
    -Anchor '            summary.BoqTables += NetworkAssetScheduleCommands.RefreshAll(document);' `
    -Insertion '            summary.BoqTables += StandardQuantityTemplateCommands.RefreshAll(document);' `
    -Description "include standard quantity templates in CE_REFRESHALL"

Write-Host "Master Items Phase 2 standard quantity templates are wired." -ForegroundColor Green

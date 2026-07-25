[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

$path = Join-Path $repositoryRoot "src\CE.Tools.Civil3D\PluginEntry.cs"
if (-not (Test-Path $path)) { throw "Profile-view ribbon source was not found: $path" }
$text = [System.IO.File]::ReadAllText($path).Replace("`r`n", "`n")
if (-not $text.Contains('"CE_PROFILEVIEWBATCHTOOLS "')) {
    $lines = [System.Collections.Generic.List[string]]($text -split "`n")
    $index = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i].Contains('"CE_PRTOOLS "')) {
            $index = $i
            break
        }
    }
    if ($index -lt 0) { throw "Could not find the Profile Tools ribbon anchor." }
    $insert = @(
        '                    Cmd("Batch Profile View Cleanup Tools", "CE_PROFILEVIEWBATCHTOOLS ", "Open batch cleanup, fit and information workflows for multiple Civil 3D profile views."),',
        '                    Cmd("Batch Profile View Cleanup", "CE_PROFILEVIEWBATCH ", "Apply profile-view style, band set, automatic fit and rebuild options to selected views."),',
        '                    Cmd("Fit All / Selected Profile Views", "CE_PROFILEVIEWFITALL ", "Set automatic station/elevation ranges and update profile views where the host API supports it."),',
        '                    Cmd("Profile View Batch Information", "CE_PROFILEVIEWBATCHINFO ", "Report profile-view names, alignments, styles, band sets, ranges and update state."),'
    )
    for ($i = $insert.Length - 1; $i -ge 0; $i--) {
        $lines.Insert($index, $insert[$i])
    }
    [System.IO.File]::WriteAllText($path, ($lines -join "`n"), $utf8NoBom)
    Write-Host "  add batch profile-view cleanup and style controls" -ForegroundColor Green
}

Write-Host "Master Items Phase 2 profile-view cleanup is wired." -ForegroundColor Green

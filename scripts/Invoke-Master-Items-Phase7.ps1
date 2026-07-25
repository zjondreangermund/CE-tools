[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$scriptPath = Join-Path $PSScriptRoot "Apply-Master-Items-Phase7.ps1"
if (-not (Test-Path $scriptPath)) {
    throw "Phase 7 normalizer is missing: $scriptPath"
}

$text = [System.IO.File]::ReadAllText($scriptPath).Replace("`r`n", "`n")
$oldLine = '[System.IO.File]::WriteAllText(path: $path, contents: $text.Replace($oldNormalised, $newNormalised), encoding: $utf8NoBom)'
$newLine = '[System.IO.File]::WriteAllText($path, $text.Replace($oldNormalised, $newNormalised), $utf8NoBom)'
if ($text.Contains($oldLine)) {
    $text = $text.Replace($oldLine, $newLine)
    [System.IO.File]::WriteAllText($scriptPath, $text, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "  corrected the Phase 7 WriteAllText call for PowerShell compatibility" -ForegroundColor Green
}

& $scriptPath

[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

$relativePaths = @(
    "src\CE.Tools.Civil3D\SewerProductionCommands.cs",
    "src\CE.Tools.Civil3D\StormwaterProductionCommands.cs"
)

$totalReplacements = 0
foreach ($relativePath in $relativePaths) {
    $path = Join-Path $repositoryRoot $relativePath
    if (-not (Test-Path $path)) {
        throw "MText escape-fix source file is missing: $relativePath"
    }

    $text = [System.IO.File]::ReadAllText($path).Replace("`r`n", "`n")
    $matches = [regex]::Matches($text, '(?<!\\)\\P').Count
    if ($matches -eq 0) {
        Write-Host "  No unescaped MText paragraph codes found in $relativePath" -ForegroundColor DarkGray
        continue
    }

    $fixed = [regex]::Replace($text, '(?<!\\)\\P', '\\P')
    [System.IO.File]::WriteAllText($path, $fixed, $utf8NoBom)
    $totalReplacements += $matches
    Write-Host "  Escaped $matches MText paragraph code(s) in $relativePath" -ForegroundColor Green
}

Write-Host "C# MText escape normalization completed. Replacements: $totalReplacements" -ForegroundColor Green

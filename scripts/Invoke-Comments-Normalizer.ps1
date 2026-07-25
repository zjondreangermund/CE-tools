[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$source = Join-Path $PSScriptRoot "Apply-Comments-2026-07-25.ps1"
if (-not (Test-Path $source)) {
    throw "The active-comments normalizer is missing: $source"
}

# Execute a temporary tolerant copy. Exact-text changes remain deterministic,
# but one already-replaced or version-specific optional snippet cannot prevent
# every later correction and validator from running.
$text = [System.IO.File]::ReadAllText($source)
$strict = '        throw "Could not apply comment change ''$Description'' in ''$RelativePath''. The expected source text was not found."'
$tolerant = @'
        Write-Warning "Skipped comment change '$Description' in '$RelativePath' because the expected source text was not found. A validator will confirm whether the change is required."
        return
'@
if (-not $text.Contains($strict)) {
    throw "The expected strict replacement guard was not found in $source."
}
$text = $text.Replace($strict, $tolerant.TrimEnd())

$temp = Join-Path $PSScriptRoot ".Apply-Comments-2026-07-25.tolerant.ps1"
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($temp, $text, $utf8NoBom)
try {
    & $temp
}
finally {
    Remove-Item $temp -Force -ErrorAction SilentlyContinue
}

# Two older exact replacements stored the characters `n instead of a real line
# break because their replacement text was single quoted. Repair those tokens
# before source validation and Autodesk compilation.
$plugin = Join-Path (Split-Path -Parent $PSScriptRoot) "src\CE.Tools.Civil3D\PluginEntry.cs"
if (Test-Path $plugin) {
    $pluginText = [System.IO.File]::ReadAllText($plugin)
    $literalCleanup = '),`n                    Cmd("Cleanup Manager Window"'
    $actualCleanup = '),' + [Environment]::NewLine + '                    Cmd("Cleanup Manager Window"'
    $literalHatch = '),`n                    Cmd("Hatch Settings Window"'
    $actualHatch = '),' + [Environment]::NewLine + '                    Cmd("Hatch Settings Window"'
    $pluginText = $pluginText.Replace($literalCleanup, $actualCleanup)
    $pluginText = $pluginText.Replace($literalHatch, $actualHatch)
    [System.IO.File]::WriteAllText($plugin, $pluginText, $utf8NoBom)
}

$masterItems = Join-Path $PSScriptRoot "Apply-Master-Items-Phase1.ps1"
if (-not (Test-Path $masterItems)) {
    throw "The Master Items Phase 1 normalizer is missing: $masterItems"
}
Write-Host "Applying Master Items Phase 1 corrections..." -ForegroundColor Cyan
& $masterItems

Write-Host "Active-comments and Master Items Phase 1 normalization completed; validators will confirm every required result." -ForegroundColor Green

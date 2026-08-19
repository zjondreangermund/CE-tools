[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$coordinateRepair = Join-Path $root 'scripts\Repair-August18-SettingOutCoordinateDisplay-Civil3D2023.ps1'
if (-not (Test-Path -LiteralPath $coordinateRepair -PathType Leaf)) {
    throw "August 19 staged coordinate validator prerequisite was not found: $coordinateRepair"
}

$text = [System.IO.File]::ReadAllText($coordinateRepair) -replace "`r?`n", "`r`n"
$oldLine = "    'double displayValue = settings.ReverseSigns',"
$newLines = "    '? -xValues[xIndex]',`r`n    '? -yValues[yIndex]',"

if ($text.Contains($oldLine)) {
    $text = $text.Replace($oldLine,$newLines)
}
elif (-not ($text.Contains("'? -xValues[xIndex]'") -and $text.Contains("'? -yValues[yIndex]'"))) {
    throw 'August 19 could not adapt the staged Site Grid display validator to the finalized source shape.'
}

[System.IO.File]::WriteAllText(
    $coordinateRepair,
    $text,
    (New-Object System.Text.UTF8Encoding($false)))

$tokens = $null
$parseErrors = $null
[System.Management.Automation.Language.Parser]::ParseFile(
    $coordinateRepair,
    [ref]$tokens,
    [ref]$parseErrors) | Out-Null
if ($parseErrors -and $parseErrors.Count -gt 0) {
    $details = ($parseErrors | ForEach-Object {
        'line ' + $_.Extent.StartLineNumber + ': ' + $_.Message
    }) -join ' | '
    throw "August 19 staged coordinate validator became invalid PowerShell: $details"
}

Write-Host 'August 19 staged Site Grid validator now checks both X and Y sign-transform behavior instead of a historical local-variable name.' -ForegroundColor Green

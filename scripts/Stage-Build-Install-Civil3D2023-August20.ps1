[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)]
    [string]$SourceRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$source = [System.IO.Path]::GetFullPath($SourceRoot.Trim('"'))
$august19Stage = Join-Path $PSScriptRoot 'Stage-Build-Install-Civil3D2023-August19.ps1'
$compatBuild = Join-Path $PSScriptRoot 'Build-Install-Civil3D2023-August20-Compat.ps1'
$preflight = Join-Path $PSScriptRoot 'Repair-August20-SurveyProductionMenuPreflight.ps1'
$runtime = Join-Path $PSScriptRoot '.Stage-Build-Install-Civil3D2023-August20.runtime.ps1'

foreach ($required in @($august19Stage,$compatBuild,$preflight)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "August 20 staging prerequisite was not found: $required"
    }
}

$text = [System.IO.File]::ReadAllText($august19Stage) -replace "`r?`n","`r`n"
$old = 'Build-Install-Civil3D2023-August19.ps1'
$new = 'Build-Install-Civil3D2023-August20-Compat.ps1'
$count = ([regex]::Matches($text,[regex]::Escape($old))).Count
if ($count -lt 1) {
    throw 'August 20 staging could not locate the August 19 build assignment to wrap.'
}
$text = $text.Replace($old,$new)

[System.IO.File]::WriteAllText($runtime,$text,(New-Object System.Text.UTF8Encoding($false)))
$tokens = $null
$parseErrors = $null
[System.Management.Automation.Language.Parser]::ParseFile($runtime,[ref]$tokens,[ref]$parseErrors) | Out-Null
if ($parseErrors -and $parseErrors.Count -gt 0) {
    $details = ($parseErrors | ForEach-Object { 'line ' + $_.Extent.StartLineNumber + ': ' + $_.Message }) -join ' | '
    Remove-Item -LiteralPath $runtime -Force -ErrorAction SilentlyContinue
    throw "August 20 generated stage script has a PowerShell syntax error: $details"
}

try {
    Write-Host "`nRunning the August 19 staged pipeline with the August 20 menu compatibility preflight..." -ForegroundColor Cyan
    & $runtime -SourceRoot $source
    if ($LASTEXITCODE -ne 0) {
        throw "August 20 stage/build/install failed with exit code $LASTEXITCODE."
    }
}
finally {
    Remove-Item -LiteralPath $runtime -Force -ErrorAction SilentlyContinue
}

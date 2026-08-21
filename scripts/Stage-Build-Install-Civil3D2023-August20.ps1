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

# The preserved August 18 stage performs several historical recovery/sanitizer
# passes after its first Midblock Route Planner repair. Those late passes can
# restore a retired Option 2 command target before the old closure gate runs.
# Patch only the temporary August 19 stage wrapper so the Midblock repair is
# re-applied after all late recovery work and immediately before closure
# validation. No tracked source file is edited by staging.
$stageRuntimeAnchor = '$runtimeText = $legacyStageText.Replace($oldBuildAssignment,$newBuildAssignment)'
$stageRuntimeInjection = @'
$runtimeText = $legacyStageText.Replace($oldBuildAssignment,$newBuildAssignment)

$lateMidblockAnchor = 'Write-Host "`nValidating previous comment closure on final staged sources..." -ForegroundColor Cyan' + "`r`n" +
                      '& $closureValidation -RepoRoot $stageRoot'
$lateMidblockReplacement = 'Write-Host "`nReapplying final Midblock Route Planner handoff after late recovery repairs..." -ForegroundColor Cyan' + "`r`n" +
                           '& $midblockRepair -RepoRoot $stageRoot' + "`r`n" +
                           '$global:LASTEXITCODE = 0' + "`r`n`r`n" +
                           'Write-Host "`nValidating previous comment closure on final staged sources..." -ForegroundColor Cyan' + "`r`n" +
                           '& $closureValidation -RepoRoot $stageRoot'
if (-not $runtimeText.Contains($lateMidblockAnchor)) {
    throw 'August 20 stage wrapper could not locate the post-sanitize closure-validation anchor.'
}
$runtimeText = $runtimeText.Replace($lateMidblockAnchor,$lateMidblockReplacement)
'@ -replace "`r?`n","`r`n"
if (-not $text.Contains($stageRuntimeAnchor)) {
    throw 'August 20 staging could not locate the August 19 runtime-text construction anchor.'
}
$text = $text.Replace($stageRuntimeAnchor,$stageRuntimeInjection)

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

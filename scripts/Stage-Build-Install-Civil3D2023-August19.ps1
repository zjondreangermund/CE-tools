[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)]
    [string]$SourceRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$source = [System.IO.Path]::GetFullPath($SourceRoot.Trim('"'))
$legacyStage = Join-Path $PSScriptRoot 'Stage-Build-Install-Civil3D2023.ps1'
$legacyBuild = Join-Path $PSScriptRoot 'Build-Install-Civil3D2023-DotNet.ps1'
$august19Build = Join-Path $PSScriptRoot 'Build-Install-Civil3D2023-August19.ps1'
$august19Repair = Join-Path $PSScriptRoot 'Repair-August19-VertexSettingOutIntervalsAndAlignments-Civil3D2023.ps1'
$runtime = Join-Path $PSScriptRoot '.Stage-Build-Install-Civil3D2023-August19.runtime.ps1'

foreach ($required in @($legacyStage,$legacyBuild,$august19Build,$august19Repair)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "August 19 staging prerequisite was not found: $required"
    }
}
if (-not (Test-Path -LiteralPath (Join-Path $source 'CE.Tools.sln') -PathType Leaf)) {
    throw "CE Tools repository was not found at: $source"
}

$legacyStageText = [System.IO.File]::ReadAllText($legacyStage) -replace "`r?`n","`r`n"
$legacyBuildText = [System.IO.File]::ReadAllText($legacyBuild) -replace "`r?`n","`r`n"

# Prove that the established August 18 pipeline is still present before August 19
# is allowed to create a runtime wrapper around it.
$requiredStageMarkers = @(
    'Repair-August18-SurveyGoogleEarthAndVertexDynamics-Civil3D2023.ps1',
    'Repair-August18-SurveyDynamicsHotfix2-Civil3D2023.ps1',
    'Repair-August18-SurveyBackgroundToolsMenu-Civil3D2023.ps1',
    'Repair-August18-SurveyDynamicStability-Hotfix3-Civil3D2023.ps1',
    'Repair-August18-SurveyPostSanitizeStability-Civil3D2023.ps1',
    'Applying final August 18 Survey grid/vertex refresh stability after late recovery repairs...',
    'Applying post-sanitize Survey/Grid stability guard...'
)
foreach ($marker in $requiredStageMarkers) {
    if (-not $legacyStageText.Contains($marker)) {
        throw "August 19 staging refused to run because an August 18 stage marker is missing: $marker"
    }
}
$requiredBuildMarkers = @(
    'Repair-August18-DisableLegacyBackgroundWatchers-Civil3D2023.ps1',
    'Repair-August18-SourceOnlySettingOutRefresh-Civil3D2023.ps1',
    'Repair-August18-SettingOutCoordinateDisplay-Civil3D2023.ps1'
)
foreach ($marker in $requiredBuildMarkers) {
    if (-not $legacyBuildText.Contains($marker)) {
        throw "August 19 staging refused to run because an August 18 final-build marker is missing: $marker"
    }
}

function Snapshot-PreservedFiles([string]$repoRoot) {
    $snapshot = @{}
    foreach ($folderName in @('src','scripts')) {
        $folder = Join-Path $repoRoot $folderName
        if (-not (Test-Path -LiteralPath $folder -PathType Container)) { continue }
        foreach ($file in Get-ChildItem -LiteralPath $folder -File -Recurse) {
            if ($file.Name -like '*.runtime.ps1') { continue }
            $relative = $file.FullName.Substring($repoRoot.Length).TrimStart('\','/')
            $snapshot[$relative] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        }
    }
    return $snapshot
}

function Assert-PreservedFiles([string]$repoRoot,[hashtable]$before) {
    $after = Snapshot-PreservedFiles $repoRoot
    $changed = New-Object System.Collections.Generic.List[string]
    foreach ($key in $before.Keys) {
        if (-not $after.ContainsKey($key) -or $after[$key] -ne $before[$key]) {
            $changed.Add($key)
        }
    }
    foreach ($key in $after.Keys) {
        if (-not $before.ContainsKey($key)) {
            $changed.Add($key)
        }
    }
    if ($changed.Count -gt 0) {
        throw ('August 19 preservation guard detected changes in the source checkout. The staged build is invalid. Changed files: ' + (($changed | Sort-Object -Unique) -join ', '))
    }
}

$preservedBefore = Snapshot-PreservedFiles $source
Write-Host 'August 18-and-earlier source snapshot captured. No tracked source file will be edited by August 19 staging.' -ForegroundColor Green

$oldBuildAssignment = @'
$build = Join-Path $stageRoot 'scripts\Build-Install-Civil3D2023-DotNet.ps1'
'@.Trim()
$newBuildAssignment = @'
$build = Join-Path $stageRoot 'scripts\Build-Install-Civil3D2023-August19.ps1'
'@.Trim()
$count = ([regex]::Matches($legacyStageText,[regex]::Escape($oldBuildAssignment))).Count
if ($count -ne 1) {
    throw "August 19 expected exactly one August 18 build assignment in the stage script; found $count."
}
$runtimeText = $legacyStageText.Replace($oldBuildAssignment,$newBuildAssignment)

# The runtime copy sits beside the original only long enough to execute so
# $PSScriptRoot behaves exactly as in the established stage script. The original
# Stage-Build-Install-Civil3D2023.ps1 remains byte-for-byte unchanged.
[System.IO.File]::WriteAllText($runtime,$runtimeText,(New-Object System.Text.UTF8Encoding($false)))
$tokens = $null
$parseErrors = $null
[System.Management.Automation.Language.Parser]::ParseFile(
    $runtime,
    [ref]$tokens,
    [ref]$parseErrors) | Out-Null
if ($parseErrors -and $parseErrors.Count -gt 0) {
    $details = ($parseErrors | ForEach-Object {
        'line ' + $_.Extent.StartLineNumber + ': ' + $_.Message
    }) -join ' | '
    Remove-Item -LiteralPath $runtime -Force -ErrorAction SilentlyContinue
    throw "August 19 generated stage script has a PowerShell syntax error: $details"
}

try {
    Write-Host "`nRunning the COMPLETE existing August 18 stage pipeline first..." -ForegroundColor Cyan
    & $runtime -SourceRoot $source
    if ($LASTEXITCODE -ne 0) {
        throw "August 19 stage/build/install failed with exit code $LASTEXITCODE."
    }
}
finally {
    Remove-Item -LiteralPath $runtime -Force -ErrorAction SilentlyContinue
}

Assert-PreservedFiles $source $preservedBefore
Write-Host "`nAugust 19 preservation check passed." -ForegroundColor Green
Write-Host 'All src/ and scripts/ files in the source checkout remained byte-for-byte unchanged during staging.' -ForegroundColor Green
Write-Host 'August 19 was applied only inside the temporary Civil 3D build stage after August 18 completed.' -ForegroundColor Green

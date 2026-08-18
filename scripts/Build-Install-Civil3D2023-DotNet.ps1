[CmdletBinding()]
param(
    [switch]$SkipInstall,
    [switch]$Clean,
    [string]$Configuration = 'Release',
    [string]$SourceCommit = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'src\CE.Tools.Civil3D\CE.Tools.Civil3D.csproj'
$verifiedInstaller = Join-Path $repo 'scripts\Install-VerifiedCivil3D2023Bundle.ps1'
$packager = Join-Path $repo 'scripts\New-CE-ToolsReleasePackage.ps1'
$surveyCommentRepair = Join-Path $repo 'scripts\Repair-August17-SurveyProductionComments-Civil3D2023.ps1'
$august18SurveyVertexRepair = Join-Path $repo 'scripts\Repair-August18-SurveyGoogleEarthAndVertexDynamics-Civil3D2023.ps1'
$disableLegacyBackgroundWatchers = Join-Path $repo 'scripts\Repair-August18-DisableLegacyBackgroundWatchers-Civil3D2023.ps1'
$sourceOnlySettingOutRefresh = Join-Path $repo 'scripts\Repair-August18-SourceOnlySettingOutRefresh-Civil3D2023.ps1'
$settingOutCoordinateDisplay = Join-Path $repo 'scripts\Repair-August18-SettingOutCoordinateDisplay-Civil3D2023.ps1'
$universalRefreshSource = Join-Path $repo 'src\CE.Tools.Civil3D\UniversalDynamicRefreshCommands.cs'
$finalGridSource = Join-Path $repo 'src\CE.Tools.Civil3D\FinalAllCommentsCompletionCommands.cs'
$autoCadRoot = 'C:\Program Files\Autodesk\AutoCAD 2023'
$civil3DRoot = if (Test-Path (Join-Path $autoCadRoot 'AeccDbMgd.dll')) { $autoCadRoot } else { Join-Path $autoCadRoot 'C3D' }
$aecRoot = if (Test-Path (Join-Path $civil3DRoot 'AecBaseMgd.dll')) { $civil3DRoot } else { $autoCadRoot }

$dotnet = Get-Command dotnet.exe -ErrorAction SilentlyContinue
if (-not $dotnet) {
    throw 'The .NET SDK command dotnet.exe was not found. Install the .NET 8 SDK and run again.'
}
if (-not (Test-Path -LiteralPath $project)) {
    throw "Project not found: $project"
}
if (-not (Test-Path -LiteralPath $verifiedInstaller)) {
    throw "Verified installer not found: $verifiedInstaller"
}
if (-not (Test-Path -LiteralPath $packager)) {
    throw "Release packager not found: $packager"
}
if (-not (Test-Path -LiteralPath $surveyCommentRepair)) {
    throw "Final Survey Production repair not found: $surveyCommentRepair"
}
if (-not (Test-Path -LiteralPath $august18SurveyVertexRepair)) {
    throw "August 18 Survey/vertex repair not found: $august18SurveyVertexRepair"
}
if (-not (Test-Path -LiteralPath $disableLegacyBackgroundWatchers)) {
    throw "Legacy background watcher repair not found: $disableLegacyBackgroundWatchers"
}
if (-not (Test-Path -LiteralPath $sourceOnlySettingOutRefresh)) {
    throw "Source-only setting-out refresh repair not found: $sourceOnlySettingOutRefresh"
}
if (-not (Test-Path -LiteralPath $settingOutCoordinateDisplay)) {
    throw "Setting-out coordinate display repair not found: $settingOutCoordinateDisplay"
}
if ([string]::IsNullOrWhiteSpace($SourceCommit)) {
    try { $SourceCommit = (& git -C $repo rev-parse HEAD 2>$null).Trim() }
    catch { $SourceCommit = 'UNKNOWN' }
    if ([string]::IsNullOrWhiteSpace($SourceCommit)) { $SourceCommit = 'UNKNOWN' }
}

$required = @(
    (Join-Path $autoCadRoot 'AcMgd.dll'),
    (Join-Path $autoCadRoot 'AcDbMgd.dll'),
    (Join-Path $autoCadRoot 'AcCoreMgd.dll'),
    (Join-Path $autoCadRoot 'AdWindows.dll'),
    (Join-Path $civil3DRoot 'AeccDbMgd.dll'),
    (Join-Path $aecRoot 'AecBaseMgd.dll')
)
$missing = $required | Where-Object { -not (Test-Path -LiteralPath $_) }
if ($missing) {
    throw "Civil 3D 2023 SDK files are missing:`n$($missing -join "`n")"
}

# Stage-Build-Install-Civil3D2023.ps1 already applies the historical Survey
# repairs and then the final August 18 stability pass. Replaying the older
# August 17/18 transforms here would undo or collide with that final state.
# A direct standalone build from raw repository source still receives the
# historical precompile repairs for backward compatibility.
$finalSurveyStateDetected = $false
try {
    if ((Test-Path -LiteralPath $universalRefreshSource -PathType Leaf) -and
        (Test-Path -LiteralPath $finalGridSource -PathType Leaf)) {
        $universalText = [System.IO.File]::ReadAllText($universalRefreshSource)
        $gridText = [System.IO.File]::ReadAllText($finalGridSource)
        $finalSurveyStateDetected =
            $universalText.Contains('RefreshBackground(Document document)') -and
            $universalText.Contains('DelaySeconds { get; set; } = 0.35;') -and
            $universalText.Contains('IsSelfRefreshingSurveyCommand(string command)') -and
            $gridText.Contains('CE_GRIDSETTINGOUTDYNAMIC')
    }
}
catch {
    $finalSurveyStateDetected = $false
}

if ($finalSurveyStateDetected) {
    Write-Host 'Final staged Survey/Grid stability markers detected; skipping legacy August 17/18 precompile source repairs.' -ForegroundColor Green
}
else {
    Write-Host "Applying final 17-08-2026 Survey Production order/grid comments immediately before compilation..." -ForegroundColor Cyan
    & $surveyCommentRepair -RepoRoot $repo
    $global:LASTEXITCODE = 0

    Write-Host "Applying 18-08-2026 Google Earth boundary and automatic vertex-follow repairs..." -ForegroundColor Cyan
    & $august18SurveyVertexRepair -RepoRoot $repo
    $global:LASTEXITCODE = 0
}

# These are intentionally the final source mutations before MSBuild. First strip
# dormant legacy watcher subscriptions, then enforce one source-owner-only
# setting-out refresh path, then apply the shared display-only coordinate rules.
Write-Host "Removing legacy background watcher subscriptions before final refresh policy..." -ForegroundColor Cyan
& $disableLegacyBackgroundWatchers -RepoRoot $repo
$global:LASTEXITCODE = 0

Write-Host "Applying source-owner-only Vertex/Grid automatic refresh policy..." -ForegroundColor Cyan
& $sourceOnlySettingOutRefresh -RepoRoot $repo
$global:LASTEXITCODE = 0

Write-Host "Applying final setting-out X/Y display and Site Grid text fixes..." -ForegroundColor Cyan
& $settingOutCoordinateDisplay -RepoRoot $repo
$global:LASTEXITCODE = 0

Push-Location $repo
try {
    $sdkVersion = (& $dotnet.Source --version).Trim()
    if (-not $sdkVersion.StartsWith('8.')) {
        throw "CE Tools requires the .NET 8 SDK for this build, but dotnet selected $sdkVersion. Ensure .NET SDK 8.0.423 is installed."
    }

    Write-Host "Using pinned .NET SDK $sdkVersion for Civil 3D compilation." -ForegroundColor Cyan

    $common = @(
        'msbuild',
        $project,
        ("/p:Configuration=$Configuration"),
        '/p:Platform=x64',
        '/p:AutoCADVersion=2023',
        ("/p:AutoCADRoot=$autoCadRoot"),
        ("/p:Civil3DRoot=$civil3DRoot"),
        ("/p:AecRoot=$aecRoot"),
        '/p:UseSharedCompilation=false',
        '/p:BuildInParallel=false',
        '/p:RunAnalyzers=false',
        '/p:RunAnalyzersDuringBuild=false',
        '/p:RunAnalyzersDuringLiveAnalysis=false',
        '/p:Deterministic=false',
        '/p:DeterministicSourcePaths=false',
        '/p:ContinuousIntegrationBuild=false',
        '/m:1',
        '/nr:false',
        '/v:minimal'
    )

    if ($Clean) {
        & $dotnet.Source @common '/t:Clean'
        if ($LASTEXITCODE -ne 0) { throw "Clean failed with exit code $LASTEXITCODE" }
    }

    & $dotnet.Source @common '/t:Restore,Build'
    if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE" }
}
finally {
    Pop-Location
}

$bundle = Join-Path $repo 'bundle\CE Tools.bundle'
$dll = Join-Path $bundle 'Contents\Windows\2023\CE.Tools.Civil3D.dll'
$coreDll = Join-Path $bundle 'Contents\Windows\2023\CE.Tools.Core.dll'
foreach ($file in @($dll, $coreDll)) {
    if (-not (Test-Path -LiteralPath $file)) { throw "Expected build output missing: $file" }
}

$releaseDir = Join-Path $repo 'artifacts\release'
New-Item -ItemType Directory -Force -Path $releaseDir | Out-Null
$releasePackage = & $packager `
    -BundlePath $bundle `
    -OutputDirectory $releaseDir `
    -SourceCommit $SourceCommit
$zip = $releasePackage.ZipPath

if (-not $SkipInstall) {
    $buildLog = Join-Path $releaseDir ('build-install-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.log')
    & $verifiedInstaller `
        -SourceBundle $bundle `
        -SourceCommit $SourceCommit `
        -BuildLogPath $buildLog
}

Write-Host 'Build succeeded with the pinned .NET 8 SDK compiler.' -ForegroundColor Green
Write-Host "Package: $zip"
Write-Host "Civil 3D DLL: $dll"
Write-Host "Source commit: $SourceCommit"

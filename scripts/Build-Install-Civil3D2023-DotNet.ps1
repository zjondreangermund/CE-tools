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
$zip = Join-Path $releaseDir 'CE-Tools-Civil3D-2023-Preserved.zip'
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
Compress-Archive -Path $bundle -DestinationPath $zip -CompressionLevel Optimal

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

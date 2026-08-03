[CmdletBinding()]
param(
    [switch]$SkipInstall,
    [switch]$Clean,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'src\CE.Tools.Civil3D\CE.Tools.Civil3D.csproj'
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

Write-Host "Using .NET SDK MSBuild instead of the crashing Visual Studio Roslyn host:" -ForegroundColor Cyan
& $dotnet.Source --info

# Each MSBuild property must be one complete argument. Without parentheses,
# PowerShell expands expressions such as '/p:Configuration=' + $Configuration
# into separate arguments, causing MSB1008 (Only one project can be specified).
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
    $targetRoot = Join-Path $env:ProgramData 'Autodesk\ApplicationPlugins'
    $target = Join-Path $targetRoot 'CE Tools.bundle'
    New-Item -ItemType Directory -Force -Path $targetRoot | Out-Null
    if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
    Copy-Item -LiteralPath $bundle -Destination $targetRoot -Recurse -Force
    Write-Host "Installed to: $target" -ForegroundColor Green
}

Write-Host 'Build succeeded with the .NET SDK compiler.' -ForegroundColor Green
Write-Host "Package: $zip"
Write-Host "Civil 3D DLL: $dll"

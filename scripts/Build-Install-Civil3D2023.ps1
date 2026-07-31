[CmdletBinding()]
param(
    [switch]$SkipInstall,
    [switch]$Clean,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Find-MSBuild {
    $candidates = @(
        "$env:ProgramFiles\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe",
        "$env:ProgramFiles\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe",
        "$env:ProgramFiles\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe",
        "$env:ProgramFiles(x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
    )
    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path $candidate)) { return $candidate }
    }
    $cmd = Get-Command msbuild.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    throw 'MSBuild was not found. Install Visual Studio 2022 Build Tools with .NET Framework 4.8 development tools.'
}

$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'src\CE.Tools.Civil3D\CE.Tools.Civil3D.csproj'
$autoCadRoot = 'C:\Program Files\Autodesk\AutoCAD 2023'
$civil3DRoot = if (Test-Path (Join-Path $autoCadRoot 'AeccDbMgd.dll')) { $autoCadRoot } else { Join-Path $autoCadRoot 'C3D' }
$aecRoot = if (Test-Path (Join-Path $civil3DRoot 'AecBaseMgd.dll')) { $civil3DRoot } else { $autoCadRoot }

$required = @(
    (Join-Path $autoCadRoot 'AcMgd.dll'),
    (Join-Path $autoCadRoot 'AcDbMgd.dll'),
    (Join-Path $autoCadRoot 'AcCoreMgd.dll'),
    (Join-Path $autoCadRoot 'AdWindows.dll'),
    (Join-Path $civil3DRoot 'AeccDbMgd.dll'),
    (Join-Path $aecRoot 'AecBaseMgd.dll')
)
$missing = $required | Where-Object { -not (Test-Path $_) }
if ($missing) {
    throw "Civil 3D 2023 SDK files are missing:`n$($missing -join "`n")"
}
if (-not (Test-Path $project)) { throw "Project not found: $project" }

$msbuild = Find-MSBuild
if ($Clean) {
    & $msbuild $project /t:Clean /p:Configuration=$Configuration /p:Platform=x64 /p:AutoCADVersion=2023 /p:AutoCADRoot="$autoCadRoot" /p:Civil3DRoot="$civil3DRoot" /p:AecRoot="$aecRoot" /m
    if ($LASTEXITCODE -ne 0) { throw "Clean failed with exit code $LASTEXITCODE" }
}

& $msbuild $project /t:Restore,Build /p:Configuration=$Configuration /p:Platform=x64 /p:AutoCADVersion=2023 /p:AutoCADRoot="$autoCadRoot" /p:Civil3DRoot="$civil3DRoot" /p:AecRoot="$aecRoot" /p:ContinuousIntegrationBuild=true /m
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE" }

$bundle = Join-Path $repo 'bundle\CE Tools.bundle'
$dll = Join-Path $bundle 'Contents\Windows\2023\CE.Tools.Civil3D.dll'
$coreDll = Join-Path $bundle 'Contents\Windows\2023\CE.Tools.Core.dll'
foreach ($file in @($dll, $coreDll)) {
    if (-not (Test-Path $file)) { throw "Expected build output missing: $file" }
}

$releaseDir = Join-Path $repo 'artifacts\release'
New-Item -ItemType Directory -Force -Path $releaseDir | Out-Null
$zip = Join-Path $releaseDir 'CE-Tools-Civil3D-2023-Preserved.zip'
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $bundle -DestinationPath $zip -CompressionLevel Optimal

if (-not $SkipInstall) {
    $targetRoot = Join-Path $env:ProgramData 'Autodesk\ApplicationPlugins'
    $target = Join-Path $targetRoot 'CE Tools.bundle'
    New-Item -ItemType Directory -Force -Path $targetRoot | Out-Null
    if (Test-Path $target) { Remove-Item $target -Recurse -Force }
    Copy-Item $bundle $targetRoot -Recurse -Force
    Write-Host "Installed to: $target" -ForegroundColor Green
}

Write-Host "Build succeeded." -ForegroundColor Green
Write-Host "Package: $zip"
Write-Host "Civil 3D DLL: $dll"

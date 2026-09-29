[CmdletBinding()]
param([switch]$SkipInstall, [switch]$Clean)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'src\CE.Tools.Civil3D\CE.Tools.Civil3D.csproj'
$autoCadRoot = 'C:\Program Files\Autodesk\AutoCAD 2023'
$civilRoot = if (Test-Path (Join-Path $autoCadRoot 'AeccDbMgd.dll')) { $autoCadRoot } else { Join-Path $autoCadRoot 'C3D' }
$aecRoot = if (Test-Path (Join-Path $civilRoot 'AecBaseMgd.dll')) { $civilRoot } else { $autoCadRoot }
$required = @(
    (Join-Path $autoCadRoot 'AcMgd.dll'),
    (Join-Path $autoCadRoot 'AcDbMgd.dll'),
    (Join-Path $civilRoot 'AeccDbMgd.dll'),
    (Join-Path $aecRoot 'AecBaseMgd.dll'))
$missing = @($required | Where-Object { -not (Test-Path -LiteralPath $_) })
if ($missing.Count -gt 0) { throw "Civil 3D 2023 assemblies are missing: $($missing -join ', ')" }

$dotnet = Get-Command dotnet.exe -ErrorAction Stop
$sdk = (& $dotnet.Source --version).Trim()
if ($LASTEXITCODE -ne 0 -or $sdk -notmatch '^(\d+)\.') {
    throw 'Could not resolve an installed .NET SDK. Install .NET SDK 8 or newer.'
}
if ([int]$Matches[1] -lt 8) { throw "The .NET 8 SDK or newer is required. Selected SDK: $sdk" }
Write-Host "Using .NET SDK $sdk for the Civil 3D 2023 net48 build." -ForegroundColor Cyan
$sourceCommit = 'UNKNOWN'
if (Get-Command git -ErrorAction SilentlyContinue) {
    try {
        $detectedCommit = (& git -C $repo rev-parse HEAD 2>$null).Trim()
        if ($LASTEXITCODE -eq 0 -and $detectedCommit -match '^[0-9a-fA-F]{40}$') {
            $sourceCommit = $detectedCommit
        }
    }
    catch { }
}

$productionValidation = Join-Path $PSScriptRoot 'Validate-CurrentProductionWiring-Civil3D2023.ps1'
if (-not (Test-Path -LiteralPath $productionValidation -PathType Leaf)) {
    throw "Production wiring validator is missing: $productionValidation"
}
& $productionValidation -RepoRoot $repo

$args = @('msbuild', $project, '/p:Configuration=Release', '/p:Platform=x64',
    '/p:AutoCADVersion=2023', "/p:AutoCADRoot=$autoCadRoot", "/p:Civil3DRoot=$civilRoot",
    "/p:AecRoot=$aecRoot", '/p:UseSharedCompilation=false', '/p:BuildInParallel=false',
    '/p:RunAnalyzers=false', '/m:1', '/nr:false', '/v:minimal')
Push-Location $repo
try {
    if ($Clean) {
        & $dotnet.Source @args '/t:Clean'
        if ($LASTEXITCODE -ne 0) { throw "Clean failed with exit code $LASTEXITCODE" }
    }
    & $dotnet.Source @args '/t:Restore,Build'
    if ($LASTEXITCODE -ne 0) { throw "Civil 3D build failed with exit code $LASTEXITCODE" }
}
finally { Pop-Location }

$bundle = Join-Path $repo 'bundle\CE Tools.bundle'
foreach ($name in @('CE.Tools.Civil3D.dll', 'CE.Tools.Core.dll')) {
    $output = Join-Path $bundle "Contents\Windows\2023\$name"
    if (-not (Test-Path -LiteralPath $output)) { throw "Build output missing: $output" }
}
$releaseDir = Join-Path $repo 'artifacts\release'
New-Item -ItemType Directory -Force -Path $releaseDir | Out-Null
$package = & (Join-Path $PSScriptRoot 'New-CE-ToolsReleasePackage.ps1') `
    -BundlePath $bundle -OutputDirectory $releaseDir -SourceCommit $sourceCommit
if ($null -eq $package -or -not (Test-Path -LiteralPath $package.ZipPath)) {
    throw 'The verified release ZIP was not created.'
}
Write-Host "Release ZIP: $($package.ZipPath)" -ForegroundColor Green
if (-not $SkipInstall) {
    & (Join-Path $PSScriptRoot 'Install-VerifiedCivil3D2023Bundle.ps1') `
        -SourceBundle $bundle -SourceCommit $sourceCommit
}

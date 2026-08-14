[CmdletBinding()]
param(
    [ValidateSet('2023','2024','2025','2026','2027')]
    [string[]]$Versions = @('2023','2024','2025','2026','2027'),
    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Release',
    [switch]$Strict
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).ProviderPath
$project = Join-Path $repoRoot 'src\CE.Tools.Civil3D\CE.Tools.Civil3D.csproj'
if (-not (Test-Path -LiteralPath $project -PathType Leaf)) {
    throw "CE Tools Civil 3D project not found: $project"
}

function Resolve-CivilRoots([string]$version) {
    $autoCadRoot = "C:\Program Files\Autodesk\AutoCAD $version"
    if (-not (Test-Path -LiteralPath $autoCadRoot -PathType Container)) { return $null }

    $civilRoot = $null
    foreach ($candidate in @($autoCadRoot, (Join-Path $autoCadRoot 'C3D'))) {
        if (Test-Path -LiteralPath (Join-Path $candidate 'AeccDbMgd.dll') -PathType Leaf) {
            $civilRoot = $candidate
            break
        }
    }
    if (-not $civilRoot) { return $null }

    $aecRoot = $null
    foreach ($candidate in @($civilRoot, $autoCadRoot)) {
        if (Test-Path -LiteralPath (Join-Path $candidate 'AecBaseMgd.dll') -PathType Leaf) {
            $aecRoot = $candidate
            break
        }
    }
    if (-not $aecRoot) { return $null }

    return [pscustomobject]@{
        AutoCADRoot = $autoCadRoot
        Civil3DRoot = $civilRoot
        AecRoot = $aecRoot
    }
}

function Expected-Framework([string]$version) {
    if ($version -in @('2023','2024')) { return 'net48' }
    if ($version -in @('2025','2026')) { return 'net8.0-windows' }
    return 'net10.0-windows'
}

$results = @()
foreach ($version in $Versions) {
    $framework = Expected-Framework $version
    $roots = Resolve-CivilRoots $version
    if (-not $roots) {
        $message = "Civil 3D/AutoCAD $version managed references were not found under C:\Program Files\Autodesk\AutoCAD $version."
        if ($Strict) { throw $message }
        Write-Warning "$message Skipping this adapter on this machine."
        $results += [pscustomobject]@{ Version=$version; Framework=$framework; Result='SKIPPED - host not installed' }
        continue
    }

    Write-Host "" 
    Write-Host "=== CE Tools Civil 3D $version ($framework) ===" -ForegroundColor Cyan
    $arguments = @(
        'build', $project,
        '-c', $Configuration,
        "-p:AutoCADVersion=$version",
        "-p:AutoCADRoot=$($roots.AutoCADRoot)",
        "-p:Civil3DRoot=$($roots.Civil3DRoot)",
        "-p:AecRoot=$($roots.AecRoot)"
    )
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "CE Tools Civil 3D $version build failed with exit code $LASTEXITCODE."
    }
    $bundle = Join-Path $repoRoot "bundle\CE Tools.bundle\Contents\Windows\$version\CE.Tools.Civil3D.dll"
    if (-not (Test-Path -LiteralPath $bundle -PathType Leaf)) {
        throw "Build reported success but the $version adapter was not copied to the bundle: $bundle"
    }
    $results += [pscustomobject]@{ Version=$version; Framework=$framework; Result='BUILT' }
}

Write-Host ""
Write-Host 'CE Tools multi-version build summary' -ForegroundColor Green
$results | Format-Table -AutoSize
Write-Host ""
Write-Host 'Note: a successful compile validates the adapter against the installed Autodesk managed assemblies. Each built adapter must still be smoke-tested inside its matching Civil 3D host before release.' -ForegroundColor Yellow

[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)]
    [string]$SourceBundle,

    [string]$SourceCommit = 'UNKNOWN',

    [string]$BuildLogPath = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$bundle = [System.IO.Path]::GetFullPath($SourceBundle.Trim('"'))
$builtDll = Join-Path $bundle 'Contents\Windows\2023\CE.Tools.Civil3D.dll'
$builtCoreDll = Join-Path $bundle 'Contents\Windows\2023\CE.Tools.Core.dll'
foreach ($requiredFile in @($builtDll, $builtCoreDll, (Join-Path $bundle 'PackageContents.xml'))) {
    if (-not (Test-Path -LiteralPath $requiredFile)) {
        throw "A required Civil 3D 2023 bundle file was not found: $requiredFile"
    }
}

function Assert-ReleaseManifest {
    param(
        [Parameter(Mandatory=$true)][string]$BundleRoot,
        [Parameter(Mandatory=$true)][string]$Phase
    )

    $root = [System.IO.Path]::GetFullPath($BundleRoot)
    $manifestPath = Join-Path $root 'Contents\Resources\release-manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath)) {
        throw "$Phase verification failed: release-manifest.json is missing."
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($null -eq $manifest.Files -or @($manifest.Files).Count -lt 3) {
        throw "$Phase verification failed: release manifest does not contain the required packaged files."
    }

    $rootPrefix = $root.TrimEnd('\') + '\'
    foreach ($file in @($manifest.Files)) {
        $relative = ([string]$file.Path).Replace('/', '\')
        if ([string]::IsNullOrWhiteSpace($relative) -or
            [System.IO.Path]::IsPathRooted($relative) -or
            $relative.Split('\') -contains '..') {
            throw "$Phase verification failed: unsafe manifest path '$relative'."
        }
        $fullPath = [System.IO.Path]::GetFullPath((Join-Path $root $relative))
        if (-not $fullPath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "$Phase verification failed: manifest path escapes the bundle: '$relative'."
        }
        if (-not (Test-Path -LiteralPath $fullPath)) {
            throw "$Phase verification failed: manifest file is missing: '$relative'."
        }
        $actual = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash
        $expected = ([string]$file.SHA256).ToUpperInvariant()
        if ($actual -ne $expected) {
            throw "$Phase verification failed for '$relative'. Expected=$expected; Actual=$actual"
        }
    }
    return $manifest
}

$releaseManifest = Assert-ReleaseManifest -BundleRoot $bundle -Phase 'Source bundle'
if (($SourceCommit -eq 'UNKNOWN' -or [string]::IsNullOrWhiteSpace($SourceCommit)) -and
    -not [string]::IsNullOrWhiteSpace([string]$releaseManifest.SourceCommit)) {
    $SourceCommit = [string]$releaseManifest.SourceCommit
}
elseif (-not [string]::IsNullOrWhiteSpace([string]$releaseManifest.SourceCommit) -and
        [string]$releaseManifest.SourceCommit -ne 'UNKNOWN' -and
        $SourceCommit -ne [string]$releaseManifest.SourceCommit) {
    throw "Source commit mismatch. Requested=$SourceCommit; Manifest=$([string]$releaseManifest.SourceCommit)"
}

$targetRoot = Join-Path $env:ProgramData 'Autodesk\ApplicationPlugins'
$target = Join-Path $targetRoot 'CE Tools.bundle'
$operationId = [Guid]::NewGuid().ToString('N')
$installStage = Join-Path $targetRoot ('.CE-Tools.installing-' + $operationId)
$rollback = Join-Path $targetRoot ('.CE-Tools.rollback-' + $operationId)
$rollbackAvailable = $false
$installed = $false

New-Item -ItemType Directory -Force -Path $targetRoot | Out-Null

try {
    Copy-Item -LiteralPath $bundle -Destination $installStage -Recurse -Force
    $stagedDll = Join-Path $installStage 'Contents\Windows\2023\CE.Tools.Civil3D.dll'
    if (-not (Test-Path -LiteralPath $stagedDll)) {
        throw "The staged Civil 3D 2023 DLL was not found: $stagedDll"
    }
    $null = Assert-ReleaseManifest -BundleRoot $installStage -Phase 'Staged bundle'

    $builtHash = (Get-FileHash -LiteralPath $builtDll -Algorithm SHA256).Hash
    $builtCoreHash = (Get-FileHash -LiteralPath $builtCoreDll -Algorithm SHA256).Hash
    $stagedHash = (Get-FileHash -LiteralPath $stagedDll -Algorithm SHA256).Hash
    if ($builtHash -ne $stagedHash) {
        throw "Staged DLL verification failed. Built=$builtHash; staged=$stagedHash"
    }

    if (Test-Path -LiteralPath $target) {
        Move-Item -LiteralPath $target -Destination $rollback
        $rollbackAvailable = $true
    }

    Move-Item -LiteralPath $installStage -Destination $target
    $installed = $true
    $installedDll = Join-Path $target 'Contents\Windows\2023\CE.Tools.Civil3D.dll'
    if (-not (Test-Path -LiteralPath $installedDll)) {
        throw "The installed Civil 3D 2023 DLL was not found: $installedDll"
    }
    $null = Assert-ReleaseManifest -BundleRoot $target -Phase 'Installed bundle'

    $installedHash = (Get-FileHash -LiteralPath $installedDll -Algorithm SHA256).Hash
    $installedCoreDll = Join-Path $target 'Contents\Windows\2023\CE.Tools.Core.dll'
    $installedCoreHash = (Get-FileHash -LiteralPath $installedCoreDll -Algorithm SHA256).Hash
    if ($builtHash -ne $installedHash) {
        throw "Installed DLL verification failed. Built=$builtHash; installed=$installedHash"
    }
    if ($builtCoreHash -ne $installedCoreHash) {
        throw "Installed core DLL verification failed. Built=$builtCoreHash; installed=$installedCoreHash"
    }

    if ([string]::IsNullOrWhiteSpace($BuildLogPath)) {
        $logRoot = Join-Path (Split-Path -Parent (Split-Path -Parent $bundle)) 'artifacts\install-logs'
        $BuildLogPath = Join-Path $logRoot ('install-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.log')
    }
    $logDirectory = Split-Path -Parent $BuildLogPath
    New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null
    @(
        'CE Tools Civil 3D 2023 verified installation',
        'Timestamp=' + (Get-Date).ToString('o'),
        'SourceCommit=' + $SourceCommit,
        'Version=' + [string]$releaseManifest.Version,
        'SourceBundle=' + $bundle,
        'TargetBundle=' + $target,
        'BuiltDll=' + $builtDll,
        'InstalledDll=' + $installedDll,
        'BuiltSHA256=' + $builtHash,
        'InstalledSHA256=' + $installedHash,
        'BuiltCoreSHA256=' + $builtCoreHash,
        'InstalledCoreSHA256=' + $installedCoreHash,
        'ManifestFiles=' + @($releaseManifest.Files).Count,
        'Verification=PASS'
    ) | Set-Content -LiteralPath $BuildLogPath -Encoding UTF8

    if ($rollbackAvailable -and (Test-Path -LiteralPath $rollback)) {
        Remove-Item -LiteralPath $rollback -Recurse -Force
        $rollbackAvailable = $false
    }

    Write-Host "Installed to: $target" -ForegroundColor Green
    Write-Host "Verified DLL SHA-256: $installedHash" -ForegroundColor Green
    Write-Host "Install log: $BuildLogPath" -ForegroundColor Green
}
catch {
    if ($installed -and (Test-Path -LiteralPath $target)) {
        Remove-Item -LiteralPath $target -Recurse -Force
    }
    if ($rollbackAvailable -and (Test-Path -LiteralPath $rollback)) {
        Move-Item -LiteralPath $rollback -Destination $target
        Write-Warning 'The previous CE Tools bundle was restored after installation failure.'
    }
    if (Test-Path -LiteralPath $installStage) {
        Remove-Item -LiteralPath $installStage -Recurse -Force
    }
    throw
}
finally {
    if (Test-Path -LiteralPath $installStage) {
        Remove-Item -LiteralPath $installStage -Recurse -Force
    }
}

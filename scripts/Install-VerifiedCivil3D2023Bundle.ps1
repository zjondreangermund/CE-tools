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
if (-not (Test-Path -LiteralPath $builtDll)) {
    throw "The built Civil 3D 2023 DLL was not found: $builtDll"
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

    $builtHash = (Get-FileHash -LiteralPath $builtDll -Algorithm SHA256).Hash
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

    $installedHash = (Get-FileHash -LiteralPath $installedDll -Algorithm SHA256).Hash
    if ($builtHash -ne $installedHash) {
        throw "Installed DLL verification failed. Built=$builtHash; installed=$installedHash"
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
        'SourceBundle=' + $bundle,
        'TargetBundle=' + $target,
        'BuiltDll=' + $builtDll,
        'InstalledDll=' + $installedDll,
        'BuiltSHA256=' + $builtHash,
        'InstalledSHA256=' + $installedHash,
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

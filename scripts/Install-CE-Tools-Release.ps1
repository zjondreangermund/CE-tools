[CmdletBinding()]
param(
    [string]$SourceCommit = '',
    [string]$BuildLogPath = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $arguments = '-NoProfile -ExecutionPolicy Bypass -File "' + $PSCommandPath + '"'
    if (-not [string]::IsNullOrWhiteSpace($SourceCommit)) {
        $arguments += ' -SourceCommit "' + $SourceCommit.Replace('"', '') + '"'
    }
    $elevated = Start-Process `
        -FilePath 'powershell.exe' `
        -Verb RunAs `
        -ArgumentList $arguments `
        -Wait `
        -PassThru
    exit $elevated.ExitCode
}

$packageRoot = Split-Path -Parent $PSScriptRoot
$bundle = Join-Path $packageRoot 'CE Tools.bundle'
$installer = Join-Path $PSScriptRoot 'Install-VerifiedCivil3D2023Bundle.ps1'
$manifest = Join-Path $bundle 'Contents\Resources\release-manifest.json'
if (-not (Test-Path -LiteralPath $bundle)) { throw "Release bundle not found: $bundle" }
if (-not (Test-Path -LiteralPath $installer)) { throw "Verified installer not found: $installer" }
if (-not (Test-Path -LiteralPath $manifest)) { throw "Release manifest not found: $manifest" }

if (Get-Process -Name acad -ErrorAction SilentlyContinue) {
    throw 'Civil 3D or AutoCAD is running. Close every acad.exe process, then run INSTALL-CE-TOOLS.cmd again.'
}

if ([string]::IsNullOrWhiteSpace($SourceCommit)) {
    $release = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
    $SourceCommit = [string]$release.SourceCommit
}
if ([string]::IsNullOrWhiteSpace($BuildLogPath)) {
    $logRoot = Join-Path $env:ProgramData 'CE Tools\InstallLogs'
    $BuildLogPath = Join-Path $logRoot ('install-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.log')
}

& $installer -SourceBundle $bundle -SourceCommit $SourceCommit -BuildLogPath $BuildLogPath
Write-Host ''
Write-Host 'CE Tools is installed and verified for Civil 3D 2023.' -ForegroundColor Green
Write-Host 'Start Civil 3D 2023 and run CE_INSTALLVERIFY.' -ForegroundColor Green

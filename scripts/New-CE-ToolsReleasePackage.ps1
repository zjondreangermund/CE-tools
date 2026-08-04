[CmdletBinding()]
param(
    [string]$BundlePath = '',
    [string]$OutputDirectory = '',
    [string]$SourceCommit = '',
    [string]$Version = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($BundlePath)) {
    $BundlePath = Join-Path $repo 'bundle\CE Tools.bundle'
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repo 'artifacts\release'
}
$bundle = [System.IO.Path]::GetFullPath($BundlePath.Trim('"'))
$output = [System.IO.Path]::GetFullPath($OutputDirectory.Trim('"'))
$packageXml = Join-Path $bundle 'PackageContents.xml'

if (-not (Test-Path -LiteralPath $packageXml)) {
    throw "PackageContents.xml was not found: $packageXml"
}

[xml]$package = Get-Content -LiteralPath $packageXml -Raw
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = [string]$package.ApplicationPackage.AppVersion
}
if ([string]::IsNullOrWhiteSpace($Version)) {
    throw 'The CE Tools release version is empty.'
}
if ([string]::IsNullOrWhiteSpace($SourceCommit)) {
    try { $SourceCommit = (& git -C $repo rev-parse HEAD 2>$null).Trim() }
    catch { $SourceCommit = 'UNKNOWN' }
}
if ([string]::IsNullOrWhiteSpace($SourceCommit)) { $SourceCommit = 'UNKNOWN' }

$requiredRelativePaths = @(
    'PackageContents.xml',
    'Contents\Windows\2023\CE.Tools.Civil3D.dll',
    'Contents\Windows\2023\CE.Tools.Core.dll'
)
foreach ($relativePath in $requiredRelativePaths) {
    $path = Join-Path $bundle $relativePath
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Release file missing: $path"
    }
}

$resources = Join-Path $bundle 'Contents\Resources'
New-Item -ItemType Directory -Force -Path $resources | Out-Null
$manifestPath = Join-Path $resources 'release-manifest.json'
$hashRelativePaths = Get-ChildItem -LiteralPath $bundle -File -Recurse |
    Where-Object { $_.FullName -ne $manifestPath } |
    Sort-Object FullName |
    ForEach-Object { $_.FullName.Substring($bundle.Length).TrimStart('\') }
$files = foreach ($relativePath in $hashRelativePaths) {
    $path = Join-Path $bundle $relativePath
    [pscustomobject][ordered]@{
        Path = ($relativePath -replace '\\', '/')
        SHA256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    }
}

$manifest = [pscustomobject][ordered]@{
    SchemaVersion = 1
    Product = 'CE Tools'
    Host = 'Autodesk Civil 3D 2023'
    Version = $Version
    SourceCommit = $SourceCommit
    CreatedUtc = (Get-Date).ToUniversalTime().ToString('o')
    Files = @($files)
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

[Version]$versionObject = $null
if (-not [Version]::TryParse($Version, [ref]$versionObject)) {
    throw "Invalid release version: $Version"
}
$releaseLabel = if ($versionObject.Major -eq 0) {
    'V' + $versionObject.Minor
} else {
    'V' + $versionObject.Major
}
$commitLabel = if ($SourceCommit -match '^[0-9a-fA-F]{8,}$') {
    $SourceCommit.Substring(0, 8).ToLowerInvariant()
} else {
    'unknown'
}
$packageName = "CE-Tools-$releaseLabel-Civil3D-2023-$commitLabel"
$stage = Join-Path $output $packageName
$zip = Join-Path $output ($packageName + '.zip')

New-Item -ItemType Directory -Force -Path $output | Out-Null
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
New-Item -ItemType Directory -Path $stage | Out-Null
Copy-Item -LiteralPath $bundle -Destination $stage -Recurse -Force

$stageScripts = Join-Path $stage 'scripts'
New-Item -ItemType Directory -Path $stageScripts | Out-Null
foreach ($scriptName in @(
    'Install-CE-Tools-Release.ps1',
    'Install-VerifiedCivil3D2023Bundle.ps1'
)) {
    $scriptPath = Join-Path $PSScriptRoot $scriptName
    if (-not (Test-Path -LiteralPath $scriptPath)) {
        throw "Release installer component missing: $scriptPath"
    }
    Copy-Item -LiteralPath $scriptPath -Destination $stageScripts -Force
}
Copy-Item -LiteralPath (Join-Path $repo 'INSTALL-CE-TOOLS.cmd') -Destination $stage -Force
Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $stage 'RELEASE-MANIFEST.json') -Force

@(
    'CE Tools Civil 3D 2023 verified release',
    '',
    '1. Close Civil 3D 2023.',
    '2. Double-click INSTALL-CE-TOOLS.cmd.',
    '3. Accept the Windows administrator prompt.',
    '4. Start Civil 3D 2023 and run CE_INSTALLVERIFY.',
    '',
    "Version: $Version",
    "Source commit: $SourceCommit",
    'Install target: C:\ProgramData\Autodesk\ApplicationPlugins\CE Tools.bundle'
) | Set-Content -LiteralPath (Join-Path $stage 'INSTALLATION.txt') -Encoding UTF8

$sumLines = Get-ChildItem -LiteralPath $stage -File -Recurse |
    Sort-Object FullName |
    ForEach-Object {
        $relative = $_.FullName.Substring($stage.Length).TrimStart('\') -replace '\\', '/'
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        "$hash  $relative"
    }
$sumLines | Set-Content -LiteralPath (Join-Path $stage 'SHA256SUMS.txt') -Encoding ASCII
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal

Write-Host "Versioned release package created: $zip" -ForegroundColor Green
[pscustomobject]@{
    Version = $Version
    ReleaseLabel = $releaseLabel
    SourceCommit = $SourceCommit
    StagePath = $stage
    ZipPath = $zip
    ManifestPath = $manifestPath
}

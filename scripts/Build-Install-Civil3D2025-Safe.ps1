[CmdletBinding()]
param(
    [ValidateSet('User','AllUsers')][string]$Scope = 'User',
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [string]$AutoCADRoot = 'C:\Program Files\Autodesk\AutoCAD 2025',
    [string]$Civil3DRoot = '',
    [string]$AecRoot = '',
    [switch]$Install
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).ProviderPath
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Install .NET 8 SDK or later to compile CE Tools for Civil 3D 2025.' }
if (-not (Test-Path (Join-Path $AutoCADRoot 'AcMgd.dll'))) { throw "Civil 3D/AutoCAD 2025 assemblies missing under $AutoCADRoot" }
function Find-DllRoot([string]$root, [string]$dll) {
    foreach ($p in @($root, (Join-Path $root 'C3D'))) {
        if (Test-Path (Join-Path $p $dll)) { return $p }
    }
    throw "Could not locate $dll. Make sure Civil 3D 2025 (not only AutoCAD) is installed."
}
if (-not $Civil3DRoot) { $Civil3DRoot = Find-DllRoot $AutoCADRoot 'AeccDbMgd.dll' }
if (-not $AecRoot) { $AecRoot = Find-DllRoot $AutoCADRoot 'AecBaseMgd.dll' }

# Compile a .NET 8 Civil 3D 2025 adapter. Existing version DLLs are not built or replaced.
$project = Join-Path $repo 'src\CE.Tools.Civil3D\CE.Tools.Civil3D.csproj'
& dotnet build $project -c $Configuration '-p:AutoCADVersion=2025' `
    "-p:AutoCADRoot=$AutoCADRoot" "-p:Civil3DRoot=$Civil3DRoot" "-p:AecRoot=$AecRoot" --nologo
if ($LASTEXITCODE -ne 0) { throw 'Civil 3D 2025 compilation failed. No installation changes made.' }

$assemblyDir = Join-Path $repo 'bundle\CE Tools.bundle\Contents\Windows\2025'
$dll = Join-Path $assemblyDir 'CE.Tools.Civil3D.dll'
if (-not (Test-Path $dll)) { throw "2025 DLL is missing: $dll" }
$bundle = Join-Path $repo 'artifacts\CE Tools 2025.bundle'
$contents = Join-Path $bundle 'Contents\Windows\2025'
New-Item -ItemType Directory -Force $contents | Out-Null
Copy-Item -Path (Join-Path $assemblyDir '*') -Destination $contents -Force
# Install the approved project style drawings alongside the 2025 adapter.
# The style importer resolves ../../Resources/ProjectStyles from Windows/2025.
$styleSource = Join-Path $repo 'bundle\CE Tools.bundle\Contents\Resources\ProjectStyles'
$styleDestination = Join-Path $bundle 'Contents\Resources\ProjectStyles'
$styleFiles = @('CE-Project-Styles-01.dwg','CE-Project-Styles-02.dwg','CE-Project-Styles-03.dwg')
foreach ($name in $styleFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $styleSource $name))) {
        throw "Required project style source missing from repository: $name"
    }
}
New-Item -ItemType Directory -Force -Path $styleDestination | Out-Null
foreach ($name in $styleFiles) {
    Copy-Item -LiteralPath (Join-Path $styleSource $name) -Destination (Join-Path $styleDestination $name) -Force
}
Write-Host 'Included all 3 approved CE project style DWGs in 2025 bundle.' -ForegroundColor Green
$manifest = @'
<?xml version="1.0" encoding="utf-8"?>
<ApplicationPackage SchemaVersion="1.0" AutodeskProduct="AutoCAD" ProductType="Application"
  Name="CE Tools 2025" AppVersion="0.61.0.0"
  Description="Separate Civil 3D 2025 CE Tools adapter" Author="CE Tools"
  ProductCode="{D836A50C-8E20-4E30-B77D-214C918B46DA}"
  UpgradeCode="{F12041B9-736C-456C-AECD-98335924178C}">
  <Components Description="CE Tools Civil 3D 2025 only">
    <RuntimeRequirements OS="Win64" Platform="Civil3D" SeriesMin="R25.0" SeriesMax="R25.0" />
    <ComponentEntry AppName="CE Tools 2025" ModuleName="./Contents/Windows/2025/CE.Tools.Civil3D.dll"
      LoadReasons="LoadOnAutoCADStartup" />
  </Components>
</ApplicationPackage>
'@
[System.IO.File]::WriteAllText((Join-Path $bundle 'PackageContents.xml'), $manifest, [System.Text.UTF8Encoding]::new($false))
Write-Host "2025-only bundle ready: $bundle" -ForegroundColor Green
if (-not $Install) {
    Write-Host 'Preview only. Re-run with -Install after validation in a test drawing.' -ForegroundColor Yellow
    return
}

$plugins = if ($Scope -eq 'AllUsers') { Join-Path $env:ProgramData 'Autodesk\ApplicationPlugins' }
           else { Join-Path $env:APPDATA 'Autodesk\ApplicationPlugins' }
$destination = Join-Path $plugins 'CE Tools 2025.bundle'
# A separately named Autodesk bundle avoids removal or overwrite of CE Tools.bundle (2023).
# Avoid double loading if the older combined bundle already contains a 2025 component.
$legacyManifest = Join-Path $plugins 'CE Tools.bundle\PackageContents.xml'
if (Test-Path $legacyManifest) {
    $legacy = Get-Content $legacyManifest -Raw
    if ($legacy -match 'SeriesMin="R24\.3"' -and
        (Test-Path (Join-Path $plugins 'CE Tools.bundle\Contents\Windows\2025\CE.Tools.Civil3D.dll'))) {
        throw 'Existing shared CE Tools.bundle already includes an installed 2025 adapter. To avoid duplicate commands, installation is blocked. The 2025 preview bundle remains available.'
    }
}
New-Item -ItemType Directory -Force $plugins | Out-Null
# Never swap a loaded .NET plugin. Ask the user to close Civil 3D first.
if (Get-Process -Name 'acad' -ErrorAction SilentlyContinue) {
    throw 'AutoCAD/Civil 3D is running. Close it before updating the 2025 bundle.'
}
# Keep historical backups OUTSIDE ApplicationPlugins so AutoCAD cannot
# discover and load old copies as additional bundles.
$backup = $null
if (Test-Path -LiteralPath $destination) {
    $backupRoot = Join-Path $env:LOCALAPPDATA 'CE Tools\BundleBackups\2025'
    New-Item -ItemType Directory -Force -Path $backupRoot | Out-Null
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $backup = Join-Path $backupRoot ("CE Tools 2025.bundle-$stamp-" + [guid]::NewGuid().ToString('N').Substring(0,8))
    Move-Item -LiteralPath $destination -Destination $backup -ErrorAction Stop
    Write-Host "Previous 2025 bundle backed up to: $backup" -ForegroundColor Yellow
}
try {
    Copy-Item -LiteralPath $bundle -Destination $destination -Recurse -ErrorAction Stop
    if (-not (Test-Path -LiteralPath (Join-Path $destination 'PackageContents.xml')) -or
        -not (Test-Path -LiteralPath (Join-Path $destination 'Contents\Windows\2025\CE.Tools.Civil3D.dll'))) {
        throw 'New 2025 bundle is incomplete; restoring the previous bundle.'
    }
    Get-ChildItem -LiteralPath $destination -File -Recurse | Unblock-File -ErrorAction SilentlyContinue
}
catch {
    $installError = $_
    if (Test-Path -LiteralPath $destination) {
        Remove-Item -LiteralPath $destination -Recurse -Force -ErrorAction Stop
    }
    if ($backup -and (Test-Path -LiteralPath $backup)) {
        Move-Item -LiteralPath $backup -Destination $destination -ErrorAction Stop
        Write-Warning 'New installation failed; previous 2025 bundle was restored.'
    }
    throw $installError
}
Write-Host "Civil 3D 2025 bundle installed at: $destination" -ForegroundColor Green
if ($backup) { Write-Host "Previous bundle backup retained at: $backup" }
Write-Host 'Existing 2023, 2024 and other Civil 3D adapters were not modified.'

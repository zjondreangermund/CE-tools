[CmdletBinding()]
param(
    [ValidateSet('User','AllUsers')][string]$Scope = 'User',
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [string]$AutoCADRoot = 'C:\Program Files\Autodesk\AutoCAD 2024',
    [string]$Civil3DRoot = '',
    [string]$AecRoot = '',
    [switch]$Install
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).ProviderPath
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Install a .NET SDK to compile CE Tools.' }
if (-not (Test-Path (Join-Path $AutoCADRoot 'AcMgd.dll'))) { throw "Civil 3D/AutoCAD 2024 assemblies missing under $AutoCADRoot" }
function Find-DllRoot([string]$root, [string]$dll) {
    foreach ($p in @($root, (Join-Path $root 'C3D'))) {
        if (Test-Path (Join-Path $p $dll)) { return $p }
    }
    throw "Could not locate $dll. Make sure Civil 3D 2024 (not only AutoCAD) is installed."
}
if (-not $Civil3DRoot) { $Civil3DRoot = Find-DllRoot $AutoCADRoot 'AeccDbMgd.dll' }
if (-not $AecRoot) { $AecRoot = Find-DllRoot $AutoCADRoot 'AecBaseMgd.dll' }

# Compile a 2024-specific adapter. The current 2023 DLL is never built or replaced.
$project = Join-Path $repo 'src\CE.Tools.Civil3D\CE.Tools.Civil3D.csproj'
& dotnet build $project -c $Configuration '-p:AutoCADVersion=2024' `
    "-p:AutoCADRoot=$AutoCADRoot" "-p:Civil3DRoot=$Civil3DRoot" "-p:AecRoot=$AecRoot" --nologo
if ($LASTEXITCODE -ne 0) { throw 'Civil 3D 2024 compilation failed. No installation changes made.' }

$assemblyDir = Join-Path $repo 'bundle\CE Tools.bundle\Contents\Windows\2024'
$dll = Join-Path $assemblyDir 'CE.Tools.Civil3D.dll'
if (-not (Test-Path $dll)) { throw "2024 DLL is missing: $dll" }
$bundle = Join-Path $repo 'artifacts\CE Tools 2024.bundle'
$contents = Join-Path $bundle 'Contents\Windows\2024'
New-Item -ItemType Directory -Force $contents | Out-Null
Copy-Item -Path (Join-Path $assemblyDir '*') -Destination $contents -Force
$manifest = @'
<?xml version="1.0" encoding="utf-8"?>
<ApplicationPackage SchemaVersion="1.0" AutodeskProduct="AutoCAD" ProductType="Application"
  Name="CE Tools 2024" AppVersion="0.61.0.0"
  Description="Separate Civil 3D 2024 CE Tools adapter" Author="CE Tools"
  ProductCode="{9187D8E8-E314-4D7C-BF26-EB52A8B48324}"
  UpgradeCode="{78FD21A6-CC0A-49B5-9460-6D2E01DC3B24}">
  <Components Description="CE Tools Civil 3D 2024 only">
    <RuntimeRequirements OS="Win64" Platform="Civil3D" SeriesMin="R24.3" SeriesMax="R24.3" />
    <ComponentEntry AppName="CE Tools 2024" ModuleName="./Contents/Windows/2024/CE.Tools.Civil3D.dll"
      LoadReasons="LoadOnAutoCADStartup" />
  </Components>
</ApplicationPackage>
'@
[System.IO.File]::WriteAllText((Join-Path $bundle 'PackageContents.xml'), $manifest, [System.Text.UTF8Encoding]::new($false))
Write-Host "2024-only bundle ready: $bundle" -ForegroundColor Green
if (-not $Install) {
    Write-Host 'Preview only. Re-run with -Install after validation in a test drawing.' -ForegroundColor Yellow
    return
}

$plugins = if ($Scope -eq 'AllUsers') { Join-Path $env:ProgramData 'Autodesk\ApplicationPlugins' }
           else { Join-Path $env:APPDATA 'Autodesk\ApplicationPlugins' }
$destination = Join-Path $plugins 'CE Tools 2024.bundle'
# A separately named Autodesk bundle avoids removal or overwrite of CE Tools.bundle (2023).
# Avoid double loading if the older combined bundle already contains a 2024 component.
$legacyManifest = Join-Path $plugins 'CE Tools.bundle\PackageContents.xml'
if (Test-Path $legacyManifest) {
    $legacy = Get-Content $legacyManifest -Raw
    if ($legacy -match 'SeriesMin="R24\.3"' -and
        (Test-Path (Join-Path $plugins 'CE Tools.bundle\Contents\Windows\2024\CE.Tools.Civil3D.dll'))) {
        throw 'Existing shared CE Tools.bundle already includes an installed 2024 adapter. To avoid duplicate commands, installation is blocked. The 2024 preview bundle remains available.'
    }
}
New-Item -ItemType Directory -Force $plugins | Out-Null
if (Test-Path $destination) {
    throw "A 2024-specific bundle already exists at $destination. Back it up and remove it manually before replacing."
}
Copy-Item -LiteralPath $bundle -Destination $destination -Recurse
Get-ChildItem $destination -File -Recurse | Unblock-File -ErrorAction SilentlyContinue
Write-Host "Civil 3D 2024 bundle installed at: $destination" -ForegroundColor Green
Write-Host 'CE Tools 2023 installation was not touched.'

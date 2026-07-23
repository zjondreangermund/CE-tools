[CmdletBinding()]
param(
    [ValidateSet("User", "AllUsers")]
    [string]$Scope = "User"
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$source = Join-Path $repositoryRoot "bundle\CE Tools.bundle"

$compiledDlls = Get-ChildItem -Path $source -Filter "CE.Tools.Civil3D.dll" -Recurse -ErrorAction SilentlyContinue
if (-not $compiledDlls) {
    throw "No compiled CE.Tools.Civil3D.dll was found. Run Build-CE-Tools.ps1 first."
}

$applicationPlugins = if ($Scope -eq "AllUsers") {
    Join-Path $env:ProgramData "Autodesk\ApplicationPlugins"
}
else {
    Join-Path $env:APPDATA "Autodesk\ApplicationPlugins"
}

$destination = Join-Path $applicationPlugins "CE Tools.bundle"
New-Item -ItemType Directory -Path $applicationPlugins -Force | Out-Null

if (Test-Path $destination) {
    Remove-Item $destination -Recurse -Force
}

Copy-Item $source $destination -Recurse -Force
Get-ChildItem $destination -File -Recurse | Unblock-File -ErrorAction SilentlyContinue

Write-Host "CE Tools installed at: $destination" -ForegroundColor Green
Write-Host "Restart Civil 3D, then use the CE Tools ribbon or type CE_BMVERT."

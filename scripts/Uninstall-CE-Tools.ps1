[CmdletBinding()]
param(
    [ValidateSet("User", "AllUsers")]
    [string]$Scope = "User"
)

$ErrorActionPreference = "Stop"
$applicationPlugins = if ($Scope -eq "AllUsers") {
    Join-Path $env:ProgramData "Autodesk\ApplicationPlugins"
}
else {
    Join-Path $env:APPDATA "Autodesk\ApplicationPlugins"
}

$destination = Join-Path $applicationPlugins "CE Tools.bundle"
if (Test-Path $destination) {
    Remove-Item $destination -Recurse -Force
    Write-Host "CE Tools removed from: $destination" -ForegroundColor Green
}
else {
    Write-Host "CE Tools is not installed at: $destination"
}

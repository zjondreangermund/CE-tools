[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

Write-Host "Checking CE Tools build prerequisites..." -ForegroundColor Cyan

$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
if ($null -ne $dotnetCommand) {
    $sdkList = & dotnet --list-sdks 2>$null
    $hasNet8Sdk = $sdkList | Where-Object { $_ -match '^8\.' }

    if ($hasNet8Sdk) {
        Write-Host "The .NET 8 SDK is already installed:" -ForegroundColor Green
        $hasNet8Sdk | ForEach-Object { Write-Host "  $_" }
        Write-Host "You can now run the CE Tools build script."
        exit 0
    }
}

$wingetCommand = Get-Command winget -ErrorAction SilentlyContinue
if ($null -eq $wingetCommand) {
    throw @"
Windows Package Manager (winget) was not found.
Install the Microsoft .NET 8 SDK manually from the official Microsoft .NET download page,
then close and reopen PowerShell.
"@
}

Write-Host "Installing Microsoft .NET 8 SDK..." -ForegroundColor Cyan
& winget install `
    --id Microsoft.DotNet.SDK.8 `
    --exact `
    --source winget `
    --accept-package-agreements `
    --accept-source-agreements

if ($LASTEXITCODE -ne 0) {
    throw "The .NET 8 SDK installation did not complete successfully."
}

Write-Host "The .NET 8 SDK installation completed." -ForegroundColor Green
Write-Host "Close this PowerShell window, open a new one, return to the CE-tools folder, and run:"
Write-Host "  .\scripts\Build-CE-Tools.ps1 -Version 2024 -Configuration Release" -ForegroundColor Yellow

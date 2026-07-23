[CmdletBinding()]
param(
    [ValidateSet("2023", "2024", "All")]
    [string]$Version = "2024",

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string]$AutoCADRoot
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repositoryRoot "src\CE.Tools.Civil3D\CE.Tools.Civil3D.csproj"
$tests = Join-Path $repositoryRoot "tests\CE.Tools.Core.Tests\CE.Tools.Core.Tests.csproj"

function Assert-DotNetSdk {
    $dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -eq $dotnetCommand) {
        throw @"
The .NET SDK is not installed or is not available in PATH.

Install the .NET 8 SDK, close PowerShell, open a new PowerShell window,
and run this build command again.

Automatic prerequisite installer:
  .\scripts\Install-Prerequisites.ps1

Direct Windows Package Manager command:
  winget install --id Microsoft.DotNet.SDK.8 --exact --source winget
"@
    }

    $sdkList = & dotnet --list-sdks 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace(($sdkList -join "`n"))) {
        throw @"
The dotnet command exists, but no .NET SDK is installed.

Run:
  .\scripts\Install-Prerequisites.ps1

Then close PowerShell, open a new PowerShell window, return to the CE-tools
folder, and run this build command again.
"@
    }

    $hasNet8Sdk = $sdkList | Where-Object { $_ -match '^8\.' }
    if (-not $hasNet8Sdk) {
        throw @"
CE Tools requires the .NET 8 SDK for its build and automated tests.
Installed SDKs:
$($sdkList -join "`n")

Run:
  .\scripts\Install-Prerequisites.ps1
"@
    }
}

Assert-DotNetSdk

Write-Host "Running CE Tools host-independent tests..." -ForegroundColor Cyan
& dotnet run --project $tests -c Release
if ($LASTEXITCODE -ne 0) {
    throw "Core tests failed."
}

function Build-Version {
    param(
        [string]$Year,
        [string]$ExplicitRoot
    )

    $root = $ExplicitRoot
    if ([string]::IsNullOrWhiteSpace($root)) {
        $root = "C:\Program Files\Autodesk\AutoCAD $Year"
    }

    if (-not (Test-Path (Join-Path $root "AcMgd.dll"))) {
        throw "AcMgd.dll was not found in '$root'. Use -AutoCADRoot to specify the Civil 3D/AutoCAD installation folder."
    }

    Write-Host "Building CE Tools for Civil 3D $Year..." -ForegroundColor Cyan
    & dotnet build $project `
        -c $Configuration `
        "-p:AutoCADVersion=$Year" `
        "-p:AutoCADRoot=$root"

    if ($LASTEXITCODE -ne 0) {
        throw "Civil 3D $Year build failed."
    }
}

if ($Version -eq "All") {
    if (-not [string]::IsNullOrWhiteSpace($AutoCADRoot)) {
        throw "-AutoCADRoot can only be used when building one Civil 3D version."
    }

    Build-Version -Year "2023" -ExplicitRoot ""
    Build-Version -Year "2024" -ExplicitRoot ""
}
else {
    Build-Version -Year $Version -ExplicitRoot $AutoCADRoot
}

Write-Host "Build complete. DLLs were copied into the CE Tools application bundle." -ForegroundColor Green

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
        throw "The dotnet command exists, but no .NET SDK is installed. Run .\scripts\Install-Prerequisites.ps1."
    }
    $hasNet8Sdk = $sdkList | Where-Object { $_ -match '^8\.' }
    if (-not $hasNet8Sdk) {
        throw "CE Tools requires the .NET 8 SDK. Run .\scripts\Install-Prerequisites.ps1."
    }
}

function Find-ManagedAssembly {
    param(
        [Parameter(Mandatory = $true)][string]$SearchRoot,
        [Parameter(Mandatory = $true)][string]$FileName
    )
    $directPath = Join-Path $SearchRoot $FileName
    if (Test-Path $directPath) { return Get-Item $directPath }
    return Get-ChildItem `
        -Path $SearchRoot `
        -Filter $FileName `
        -File `
        -Recurse `
        -ErrorAction SilentlyContinue |
        Sort-Object { $_.FullName.Length } |
        Select-Object -First 1
}

function Invoke-RequiredScript {
    param(
        [Parameter(Mandatory = $true)][string]$FileName,
        [Parameter(Mandatory = $true)][string]$Description
    )
    $path = Join-Path $PSScriptRoot $FileName
    if (-not (Test-Path $path)) { throw "Required build normalizer is missing: $path" }
    Write-Host $Description -ForegroundColor Cyan
    & $path
}

Assert-DotNetSdk

Invoke-RequiredScript `
    -FileName "Invoke-Comments-Normalizer.ps1" `
    -Description "Applying active 25 July 2026 comment corrections..."
Invoke-RequiredScript `
    -FileName "Apply-Comments-Discipline.ps1" `
    -Description "Applying feature-line, profile, surface and network comment corrections..."
Invoke-RequiredScript `
    -FileName "Apply-Comments-Quantities.ps1" `
    -Description "Applying linked sewer excavation quantity corrections..."
Invoke-RequiredScript `
    -FileName "Apply-Comments-Road.ps1" `
    -Description "Applying batch road-production corrections..."
Invoke-RequiredScript `
    -FileName "Invoke-Master-Items-Phase1.ps1" `
    -Description "Applying Master Items Phase 1 corrections..."
Invoke-RequiredScript `
    -FileName "Apply-Master-Items-Phase2.ps1" `
    -Description "Applying Master Items Phase 2 dynamic parking and grading corrections..."
Invoke-RequiredScript `
    -FileName "Apply-Civil3D-Compatibility.ps1" `
    -Description "Normalising Civil 3D 2023/2024 source compatibility..."

Write-Host "Running CE Tools host-independent tests..." -ForegroundColor Cyan
& dotnet run --project $tests -c Release
if ($LASTEXITCODE -ne 0) { throw "Core tests failed." }

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

    $civilDbAssembly = Find-ManagedAssembly -SearchRoot $root -FileName "AeccDbMgd.dll"
    if ($null -eq $civilDbAssembly) {
        throw "AeccDbMgd.dll was not found below '$root'. Confirm that Civil 3D $Year is installed."
    }
    $aecBaseAssembly = Find-ManagedAssembly -SearchRoot $root -FileName "AecBaseMgd.dll"
    if ($null -eq $aecBaseAssembly) {
        throw "AecBaseMgd.dll was not found below '$root'. Repair or update Civil 3D $Year."
    }

    $civilRoot = $civilDbAssembly.DirectoryName
    $aecRoot = $aecBaseAssembly.DirectoryName
    Write-Host "Building CE Tools Phase 2 for Civil 3D $Year..." -ForegroundColor Cyan
    Write-Host "  AutoCAD API: $root"
    Write-Host "  Civil 3D API: $civilRoot"
    Write-Host "  AEC Base API: $aecRoot"

    & dotnet build $project `
        -c $Configuration `
        "-p:AutoCADVersion=$Year" `
        "-p:AutoCADRoot=$root" `
        "-p:Civil3DRoot=$civilRoot" `
        "-p:AecRoot=$aecRoot"
    if ($LASTEXITCODE -ne 0) {
        throw "Civil 3D $Year Phase 2 build failed."
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

Write-Host "Phase 2 build complete. DLLs were copied into the CE Tools application bundle." -ForegroundColor Green

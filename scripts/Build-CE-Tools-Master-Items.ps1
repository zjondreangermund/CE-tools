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
        throw "The .NET 8 SDK is required. Run .\scripts\Install-Prerequisites.ps1."
    }
    $sdkList = & dotnet --list-sdks 2>$null
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
    $direct = Join-Path $SearchRoot $FileName
    if (Test-Path $direct) { return Get-Item $direct }
    return Get-ChildItem `
        -Path $SearchRoot `
        -Filter $FileName `
        -File `
        -Recurse `
        -ErrorAction SilentlyContinue |
        Sort-Object { $_.FullName.Length } |
        Select-Object -First 1
}

function Invoke-Normalizer {
    param(
        [Parameter(Mandatory = $true)][string]$FileName,
        [Parameter(Mandatory = $true)][string]$Title
    )
    $path = Join-Path $PSScriptRoot $FileName
    if (-not (Test-Path $path)) { throw "Required normalizer is missing: $path" }
    Write-Host $Title -ForegroundColor Cyan
    & $path
}

Assert-DotNetSdk

Invoke-Normalizer "Invoke-Comments-Normalizer.ps1" "Applying active comment corrections..."
Invoke-Normalizer "Apply-Comments-Discipline.ps1" "Applying discipline comment corrections..."
Invoke-Normalizer "Apply-Comments-Quantities.ps1" "Applying sewer quantity corrections..."
Invoke-Normalizer "Apply-Comments-Road.ps1" "Applying road-production corrections..."
Invoke-Normalizer "Invoke-Master-Items-Phase1.ps1" "Applying Master Items Phase 1..."
Invoke-Normalizer "Apply-Master-Items-Phase2.ps1" "Applying Master Items Phase 2 dynamic parking and grading..."
Invoke-Normalizer "Apply-Master-Items-Phase2-Quantities.ps1" "Applying Master Items Phase 2 standard quantities..."
Invoke-Normalizer "Apply-Civil3D-Compatibility.ps1" "Normalising Civil 3D 2023/2024 compatibility..."

Write-Host "Running host-independent tests..." -ForegroundColor Cyan
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
        throw "AcMgd.dll was not found in '$root'. Use -AutoCADRoot when required."
    }
    $civilDb = Find-ManagedAssembly -SearchRoot $root -FileName "AeccDbMgd.dll"
    $aecBase = Find-ManagedAssembly -SearchRoot $root -FileName "AecBaseMgd.dll"
    if ($null -eq $civilDb) {
        throw "AeccDbMgd.dll was not found below '$root'. Confirm that Civil 3D $Year is installed."
    }
    if ($null -eq $aecBase) {
        throw "AecBaseMgd.dll was not found below '$root'. Repair Civil 3D $Year."
    }

    Write-Host "Building current CE Tools master-items branch for Civil 3D $Year..." -ForegroundColor Cyan
    Write-Host "  AutoCAD API: $root"
    Write-Host "  Civil 3D API: $($civilDb.DirectoryName)"
    Write-Host "  AEC Base API: $($aecBase.DirectoryName)"

    & dotnet build $project `
        -c $Configuration `
        "-p:AutoCADVersion=$Year" `
        "-p:AutoCADRoot=$root" `
        "-p:Civil3DRoot=$($civilDb.DirectoryName)" `
        "-p:AecRoot=$($aecBase.DirectoryName)"
    if ($LASTEXITCODE -ne 0) {
        throw "Civil 3D $Year master-items build failed."
    }
}

if ($Version -eq "All") {
    if (-not [string]::IsNullOrWhiteSpace($AutoCADRoot)) {
        throw "-AutoCADRoot can only be supplied when building one Civil 3D version."
    }
    Build-Version "2023" ""
    Build-Version "2024" ""
}
else {
    Build-Version $Version $AutoCADRoot
}

Write-Host "Current master-items build complete. DLLs were copied into the CE Tools application bundle." -ForegroundColor Green

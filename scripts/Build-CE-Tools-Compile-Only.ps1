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

Assert-DotNetSdk

Write-Host "Running host-independent tests against the already-normalized source..." -ForegroundColor Cyan
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

    Write-Host "Building normalized CE Tools source for Civil 3D $Year..." -ForegroundColor Cyan
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
        throw "Civil 3D $Year compile-only build failed."
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

Write-Host "Compile-only build complete. DLLs were copied into the CE Tools application bundle." -ForegroundColor Green

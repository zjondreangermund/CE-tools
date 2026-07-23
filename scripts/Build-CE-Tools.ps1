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

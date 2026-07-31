param(
    [Parameter(Mandatory = $true)]
    [string]$ArchivePath
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$target = Join-Path $repoRoot 'src\CE.Tools.Civil3D'
$temp = Join-Path ([System.IO.Path]::GetTempPath()) ('ce-tools-v55-' + [guid]::NewGuid().ToString('N'))

$required = @(
    'CivilObjectBatchStyleCommands.cs',
    'FeatureProfileSurfaceCommentCommands.cs',
    'FloatingToolsWindow.cs',
    'ParkingSkewValidationCommands.cs',
    'RoadDriveReviewCommands.cs',
    'RoadProductionCommentCommands.cs',
    'DynamicTypicalDetailCommands.cs',
    'DynamicTypicalDetailStorage.cs',
    'WaterSewerCostEstimateCommands.cs',
    'TypicalDetailsReviewCommands.cs',
    'SettingOutScheduleCommands.cs'
)

try {
    if (-not (Test-Path -LiteralPath $ArchivePath)) {
        throw "Archive not found: $ArchivePath"
    }

    New-Item -ItemType Directory -Path $temp -Force | Out-Null
    Expand-Archive -LiteralPath $ArchivePath -DestinationPath $temp -Force

    $nested = Get-ChildItem -LiteralPath $temp -Recurse -File |
        Where-Object { $_.Name -eq 'CE-Tools-Source-v55-preserved.zip' } |
        Select-Object -First 1

    if ($nested) {
        $nestedTarget = Join-Path $temp 'source'
        Expand-Archive -LiteralPath $nested.FullName -DestinationPath $nestedTarget -Force
        $searchRoot = $nestedTarget
    }
    else {
        $searchRoot = $temp
    }

    New-Item -ItemType Directory -Path $target -Force | Out-Null
    $restored = @()

    foreach ($name in $required) {
        $source = Get-ChildItem -LiteralPath $searchRoot -Recurse -File |
            Where-Object { $_.Name -eq $name } |
            Select-Object -First 1

        if (-not $source) {
            throw "Required preserved source is missing from the archive: $name"
        }

        $destination = Join-Path $target $name
        Copy-Item -LiteralPath $source.FullName -Destination $destination -Force
        $restored += $destination
    }

    Write-Host "Restored $($restored.Count) preserved V55 source files:" -ForegroundColor Green
    $restored | ForEach-Object { Write-Host " - $_" }
    Write-Host ''
    Write-Host 'Next run:' -ForegroundColor Cyan
    Write-Host '  python scripts/Validate-PreservedRegressionGate.py'
    Write-Host '  dotnet test tests/CE.Tools.Core.Tests/CE.Tools.Core.Tests.csproj -c Release'
}
finally {
    if (Test-Path -LiteralPath $temp) {
        Remove-Item -LiteralPath $temp -Recurse -Force
    }
}

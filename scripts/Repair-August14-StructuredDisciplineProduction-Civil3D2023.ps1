[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$source = Join-Path $root 'src\CE.Tools.Civil3D\August11ProductionCentreCommands.cs'
$structured = Join-Path $root 'src\CE.Tools.Civil3D\August14StructuredDisciplineProductionCentres.cs'
$utf8 = New-Object System.Text.UTF8Encoding($false)

if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
    throw "Production Centre source was not found: $source"
}
if (-not (Test-Path -LiteralPath $structured -PathType Leaf)) {
    throw "Structured discipline Production Centre source was not found: $structured"
}

$text = [System.IO.File]::ReadAllText($source)
$structuredText = [System.IO.File]::ReadAllText($structured)

function Replace-CommandMethodBody {
    param(
        [Parameter(Mandatory=$true)][string]$Command,
        [Parameter(Mandatory=$true)][string]$MethodCall
    )

    # Search the actual command declaration, not menu/ribbon references to the
    # same command string elsewhere in the source file.
    $marker = '[CommandMethod("CE_TOOLS", "' + $Command + '"'
    $markerIndex = $text.IndexOf($marker, [StringComparison]::Ordinal)
    if ($markerIndex -lt 0) {
        throw "Production CommandMethod declaration was not found: $Command"
    }

    $methodStart = $text.IndexOf('public void ', $markerIndex, [StringComparison]::Ordinal)
    if ($methodStart -lt 0) {
        throw "Production command method declaration was not found after: $Command"
    }

    $openBrace = $text.IndexOf('{', $methodStart)
    if ($openBrace -lt 0) {
        throw "Production command method opening brace was not found: $Command"
    }

    $depth = 0
    $closeBrace = -1
    for ($index = $openBrace; $index -lt $text.Length; $index++) {
        $character = $text[$index]
        if ($character -eq '{') { $depth++ }
        elseif ($character -eq '}') {
            $depth--
            if ($depth -eq 0) {
                $closeBrace = $index
                break
            }
        }
    }
    if ($closeBrace -lt 0) {
        throw "Production command method closing brace was not found: $Command"
    }

    $replacement = "{`r`n            new August14StructuredDisciplineProductionCentres().$MethodCall();`r`n        }"
    $script:text = $text.Substring(0, $openBrace) + $replacement + $text.Substring($closeBrace + 1)
}

$routes = @(
    @('CE_PROJECTPRODUCTIONCENTRE', 'ProjectProduction'),
    @('CE_SURVEYPRODUCTIONCENTRE', 'SurveyProduction'),
    @('CE_PLATFORMPRODUCTIONCENTRE', 'PlatformProduction'),
    @('CE_SWPRODUCTIONCENTRE', 'StormwaterProduction'),
    @('CE_SEWERPRODUCTIONCENTRE', 'SewerProduction'),
    @('CE_WATERPRODUCTIONCENTRE', 'WaterProduction'),
    @('CE_BULKWATERPRODUCTIONCENTRE', 'BulkWaterProduction'),
    @('CE_PARKINGPRODUCTIONCENTRE', 'ParkingProduction'),
    @('CE_FLOODPRODUCTIONCENTRE', 'FloodProduction')
)

foreach ($route in $routes) {
    $command = [string]$route[0]
    $method = [string]$route[1]
    if (-not $structuredText.Contains('public void ' + $method + '()')) {
        throw "Structured discipline public method is missing: $method"
    }
    Replace-CommandMethodBody -Command $command -MethodCall $method
}

[System.IO.File]::WriteAllText($source, $text, $utf8)

$verify = [System.IO.File]::ReadAllText($source)
foreach ($route in $routes) {
    $method = [string]$route[1]
    $expected = 'new August14StructuredDisciplineProductionCentres().' + $method + '();'
    if (-not $verify.Contains($expected)) {
        throw "Structured production routing verification failed: $expected"
    }
}

# Roads deliberately remains on the already-approved Road V2 hierarchy.
if (-not $verify.Contains('CE_ROADPRODUCTIONV2')) {
    throw 'Road Production V2 routing disappeared while structuring the other disciplines.'
}

Write-Host 'All non-road CE Production disciplines now use Road-style progressive sub-centres.' -ForegroundColor Green
Write-Host 'Project: Settings -> Coordination Production -> Delivery Production.' -ForegroundColor Green
Write-Host 'Survey: Settings -> Surface Production -> Setting-Out / Delivery Production.' -ForegroundColor Green
Write-Host 'Platform/Parking: Settings -> Layout Production -> Design Production.' -ForegroundColor Green
Write-Host 'Stormwater/Sewer/Water/Bulk Water: Settings -> Network / Layout Production -> Design Production.' -ForegroundColor Green
Write-Host 'Flood: Settings -> Analysis Production -> Output / Delivery Production.' -ForegroundColor Green

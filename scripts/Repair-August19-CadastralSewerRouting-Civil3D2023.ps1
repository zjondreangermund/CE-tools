[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$src = Join-Path $root 'src\CE.Tools.Civil3D'
function Need([string]$name) {
    $path = Join-Path $src $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "August 19 cadastral sewer source missing: $path" }
    return $path
}
function ReadText([string]$path) { [System.IO.File]::ReadAllText($path) }
function WriteText([string]$path,[string]$text) { [System.IO.File]::WriteAllText($path,$text,[System.Text.UTF8Encoding]::new($false)) }

$cadastral = Need 'August19CadastralSewerRouteCommands.cs'
$structured = Need 'August14StructuredDisciplineProductionCentres.cs'
$routePlanner = Need 'RoutePlannerExpansionCommands.cs'

$cadastralText = ReadText $cadastral
foreach ($marker in @(
    '"CE_SEWERFROMCADASTRAL"',
    '"CE_SEWERFROMROADRESERVE"',
    'Offset from shared erf boundary',
    'Offset from outer erf boundary',
    'SAMPLED SITE LOW POINT',
    'Shortest practical route')) {
    if (-not $cadastralText.Contains($marker)) { throw "August 19 cadastral sewer command marker missing: $marker" }
}

# Sewer Production must expose cadastral, Midblock and Road-Reserve sewer routing
# as three explicit workflows.  The old combined Midblock/Road-Reserve action is
# intentionally removed from the staged copy only.
$text = ReadText $structured
$oldDescription = 'A("CE-Sewer Network / Layout Production", "CE_SEWERLAYOUTPRODUCTIONCENTRE", "Midblock/road-reserve routing, networks and sequencing.", "01 Sewer Production"),'
$newDescription = 'A("CE-Sewer Network / Layout Production", "CE_SEWERLAYOUTPRODUCTIONCENTRE", "Cadastral, Midblock and Road-Reserve routing, networks and sequencing.", "01 Sewer Production"),'
if ($text.Contains($oldDescription)) {
    $text = $text.Replace($oldDescription,$newDescription)
}
elseif (-not $text.Contains($newDescription)) {
    throw 'Sewer Production layout-description marker was not found.'
}

$oldAction = '                    A("CE-Midblock / Road-Reserve Sewer Route", "CE_MIDBLOCKSEWERPRODUCTION", "Create continuous selected-side/low-side sewer routes and planning manholes.", "02 PREPARE"),'
$newActions = @'
                    A("CE-Sewer Route from Cadastral Data", "CE_SEWERFROMCADASTRAL", "Analyse a selected surface and create the shortest connected cadastral sewer route toward the site low point, with separate shared-erf and outer-erf offsets.", "02 PREPARE"),
                    A("CE-Midblock Sewer Route", "CE_MIDBLOCKSEWERPRODUCTION", "Create the dedicated continuous Midblock sewer route and planning manholes from cadastral erfs.", "02 PREPARE"),
                    A("CE-Road Reserve Sewer Route", "CE_SEWERFROMROADRESERVE", "Open the separate Road-Reserve sewer route workflow using CE road-reserve centreline geometry.", "02 PREPARE"),
'@.TrimEnd("`r","`n")
if ($text.Contains($oldAction)) {
    $text = $text.Replace($oldAction,$newActions)
}
elseif (-not ($text.Contains('"CE_SEWERFROMCADASTRAL"') -and $text.Contains('"CE_SEWERFROMROADRESERVE"'))) {
    throw 'Combined Midblock/Road-Reserve Sewer action marker was not found.'
}
WriteText $structured $text

# Add the automatic cadastral sewer planner to the broad Route Planner without
# removing the existing generic road-reserve utility route used by SW/Water/Bulk Water.
$text = ReadText $routePlanner
$genericRoadReserve = '                    new DisciplineWorkflowAction("Create utility route from road-reserve centrelines", "CE_UTILITYFROMROADRESERVE", "Create Sewer/SW/Water/Bulk Water preliminary routes on or offset from existing CE road-reserve centrelines.", "02 Utility Option 1"),'
$cadastralAction = '                    new DisciplineWorkflowAction("Create sewer route from cadastral data", "CE_SEWERFROMCADASTRAL", "Analyse a selected surface and create the shortest connected sewer route directly from cadastral erf boundaries, with separate Midblock and Road-Reserve offsets.", "02 Sewer Cadastral"),'
if (-not $text.Contains('new DisciplineWorkflowAction("Create sewer route from cadastral data", "CE_SEWERFROMCADASTRAL"')) {
    if (-not $text.Contains($genericRoadReserve)) { throw 'Route Planner generic road-reserve action marker was not found.' }
    $text = $text.Replace($genericRoadReserve,$cadastralAction + "`r`n" + $genericRoadReserve)
}
WriteText $routePlanner $text

# Final staged guards.
$structuredText = ReadText $structured
$routeText = ReadText $routePlanner
if ($structuredText.Contains('CE-Midblock / Road-Reserve Sewer Route')) {
    throw 'August 19 cadastral sewer repair failed: the combined Sewer route action still exists.'
}
foreach ($marker in @('"CE_SEWERFROMCADASTRAL"','"CE_MIDBLOCKSEWERPRODUCTION"','"CE_SEWERFROMROADRESERVE"')) {
    if (-not $structuredText.Contains($marker)) { throw "August 19 separate Sewer workflow marker missing: $marker" }
}
if (-not $routeText.Contains('"CE_SEWERFROMCADASTRAL"')) {
    throw 'August 19 Route Planner did not receive the cadastral sewer workflow.'
}

Write-Host 'August 19 cadastral Sewer routing is ready.' -ForegroundColor Green
Write-Host 'Sewer Production now exposes separate Cadastral, Midblock and Road-Reserve route workflows.' -ForegroundColor Green
Write-Host 'Cadastral Sewer uses a selected surface, low-point routing and separate shared/outer erf offsets.' -ForegroundColor Green

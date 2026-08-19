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
function EnsureCivilSurfaceAlias([string]$path) {
    $text = ReadText $path
    $alias = 'using Surface = Autodesk.Civil.DatabaseServices.Surface;'
    if (-not $text.Contains($alias)) {
        $anchor = 'using Autodesk.Civil.DatabaseServices;'
        if (-not $text.Contains($anchor)) {
            throw "August 19 Civil Surface alias anchor missing in: $path"
        }
        $text = $text.Replace($anchor,$anchor + "`r`n" + $alias)
        WriteText $path $text
    }
    $check = ReadText $path
    if (-not $check.Contains($alias)) {
        throw "August 19 Civil Surface alias was not applied in: $path"
    }
}

$cadastral = Need 'August19CadastralSewerRouteCommands.cs'
$roadReserve = Need 'August19RoadReserveSewerAndSafetyCommands.cs'
$structured = Need 'August14StructuredDisciplineProductionCentres.cs'
$routePlanner = Need 'RoutePlannerExpansionCommands.cs'
$roadLayout = Need 'RoadLayoutProductionCommands.cs'

# Both AutoCAD and Civil 3D expose a type named Surface.  The August 19 sewer
# sources use terrain surfaces exclusively, so force the unqualified Surface name
# to Autodesk.Civil.DatabaseServices.Surface in the staged copy before compilation.
EnsureCivilSurfaceAlias $cadastral
EnsureCivilSurfaceAlias $roadReserve

$cadastralText = ReadText $cadastral
foreach ($marker in @(
    '"CE_SEWERFROMCADASTRAL"',
    'Offset from shared erf boundary',
    'Offset from outer erf boundary',
    'SAMPLED SITE LOW POINT',
    'Shortest practical route',
    'using Surface = Autodesk.Civil.DatabaseServices.Surface;')) {
    if (-not $cadastralText.Contains($marker)) { throw "August 19 cadastral sewer command marker missing: $marker" }
}

$roadReserveText = ReadText $roadReserve
foreach ($marker in @(
    '"CE_SEWERROADRESERVE"',
    '"CE_ROADRESERVECENTERLINESSAFE"',
    'Offset from erf boundary into road reserve',
    'Minimum road reserve width',
    'Maximum road reserve width',
    'Maximum opposing-edge angle difference',
    'Minimum overlapping edge length (%)',
    'Minimum usable reserve-edge length',
    'Starting manhole setback from erf boundary',
    'FindElevationAtXY',
    'SelfIntersects',
    'SplitAtJunctionsAndSpacing',
    'using Surface = Autodesk.Civil.DatabaseServices.Surface;')) {
    if (-not $roadReserveText.Contains($marker)) { throw "August 19 Road Reserve sewer/safety marker missing: $marker" }
}

# Sewer Production exposes Cadastral, Midblock and Road-Reserve sewer routing as
# three explicit workflows.  Road Reserve now opens a Sewer-only command with no
# discipline selector and with road-reserve-specific geometry conditions.
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
                    A("CE-Road Reserve Sewer Route", "CE_SEWERROADRESERVE", "Create Sewer-only road-reserve lines directly from outer erf boundaries using reserve width/angle/overlap conditions and selected-surface flow toward the site low point.", "02 PREPARE"),
'@.TrimEnd("`r","`n")
if ($text.Contains($oldAction)) {
    $text = $text.Replace($oldAction,$newActions)
}
else {
    # Idempotent rerun / previous August 19 staged shape: upgrade the earlier
    # Road-Reserve launcher to the dedicated Sewer-only command.
    $text = $text.Replace(
        'A("CE-Road Reserve Sewer Route", "CE_SEWERFROMROADRESERVE", "Open the separate Road-Reserve sewer route workflow using CE road-reserve centreline geometry.", "02 PREPARE"),',
        'A("CE-Road Reserve Sewer Route", "CE_SEWERROADRESERVE", "Create Sewer-only road-reserve lines directly from outer erf boundaries using reserve width/angle/overlap conditions and selected-surface flow toward the site low point.", "02 PREPARE"),')
    if (-not ($text.Contains('"CE_SEWERFROMCADASTRAL"') -and $text.Contains('"CE_SEWERROADRESERVE"'))) {
        throw 'Combined/previous Road-Reserve Sewer action marker was not found.'
    }
}
WriteText $structured $text

# Add the automatic cadastral sewer planner to the broad Route Planner without
# removing the existing generic road-reserve utility route used by SW/Water/Bulk Water.
$text = ReadText $routePlanner
$genericRoadReserve = '                    new DisciplineWorkflowAction("Create utility route from road-reserve centrelines", "CE_UTILITYFROMROADRESERVE", "Create Sewer/SW/Water/Bulk Water preliminary routes on or offset from existing CE road-reserve centrelines.", "02 Utility Option 1"),'
$cadastralAction = '                    new DisciplineWorkflowAction("Create sewer route from cadastral data", "CE_SEWERFROMCADASTRAL", "Analyse a selected surface and create the shortest connected sewer route directly from cadastral erf boundaries, with separate Midblock and Road-Reserve offsets.", "02 Sewer Cadastral"),'
$dedicatedRoadReserveAction = '                    new DisciplineWorkflowAction("Create Sewer route in road reserves", "CE_SEWERROADRESERVE", "Create Sewer-only road-reserve routes directly from cadastral outer erf boundaries using reserve conditions and selected-surface flow direction.", "02 Sewer Road Reserve"),'
if (-not $text.Contains('new DisciplineWorkflowAction("Create sewer route from cadastral data", "CE_SEWERFROMCADASTRAL"')) {
    if (-not $text.Contains($genericRoadReserve)) { throw 'Route Planner generic road-reserve action marker was not found.' }
    $text = $text.Replace($genericRoadReserve,$cadastralAction + "`r`n" + $dedicatedRoadReserveAction + "`r`n" + $genericRoadReserve)
}
elseif (-not $text.Contains('new DisciplineWorkflowAction("Create Sewer route in road reserves", "CE_SEWERROADRESERVE"')) {
    if (-not $text.Contains($genericRoadReserve)) { throw 'Route Planner generic road-reserve action marker was not found for Road Reserve insertion.' }
    $text = $text.Replace($genericRoadReserve,$dedicatedRoadReserveAction + "`r`n" + $genericRoadReserve)
}
WriteText $routePlanner $text

# The Roads production menu now uses the safe reserve-centreline engine.  The old
# implementation remains untouched in tracked source but is no longer the staged
# menu entry used on Civil 3D 2023 field builds.
$text = ReadText $roadLayout
$oldRoadAction = 'new DisciplineWorkflowAction("Road reserve centrelines", "CE_ROADRESERVECENTERLINES", "Create road-centre polylines between opposing cadastral/reserve boundaries, including mixed reserve widths.", "01 Reserve geometry")'
$newRoadAction = 'new DisciplineWorkflowAction("Road reserve centrelines", "CE_ROADRESERVECENTERLINESSAFE", "Create road-centre polylines only after closed-polygon, width, angle, overlap and edge-length safety checks.", "01 Reserve geometry")'
if ($text.Contains($oldRoadAction)) {
    $text = $text.Replace($oldRoadAction,$newRoadAction)
}
elseif (-not $text.Contains($newRoadAction)) {
    throw 'Road Layout safe reserve-centreline menu marker was not found.'
}
WriteText $roadLayout $text

# Final staged guards.
$structuredText = ReadText $structured
$routeText = ReadText $routePlanner
$roadLayoutText = ReadText $roadLayout
if ($structuredText.Contains('CE-Midblock / Road-Reserve Sewer Route')) {
    throw 'August 19 cadastral sewer repair failed: the combined Sewer route action still exists.'
}
foreach ($marker in @('"CE_SEWERFROMCADASTRAL"','"CE_MIDBLOCKSEWERPRODUCTION"','"CE_SEWERROADRESERVE"')) {
    if (-not $structuredText.Contains($marker)) { throw "August 19 separate Sewer workflow marker missing: $marker" }
}
if (-not ($routeText.Contains('"CE_SEWERFROMCADASTRAL"') -and $routeText.Contains('"CE_SEWERROADRESERVE"'))) {
    throw 'August 19 Route Planner did not receive both dedicated Sewer routing workflows.'
}
if (-not $roadLayoutText.Contains('"CE_ROADRESERVECENTERLINESSAFE"')) {
    throw 'August 19 Road Layout did not route reserve-centreline creation through the safe engine.'
}

Write-Host 'August 19 Sewer routing / Road Reserve safety is ready.' -ForegroundColor Green
Write-Host 'Civil 3D Surface alias applied to August 19 cadastral and road-reserve sewer sources.' -ForegroundColor Green
Write-Host 'Sewer Production exposes separate Cadastral, Midblock and Sewer-only Road-Reserve workflows.' -ForegroundColor Green
Write-Host 'Road Reserve Sewer offsets from outer erf boundaries toward the road centre and follows selected-surface low-point flow.' -ForegroundColor Green
Write-Host 'Road-reserve width, opposing-angle, overlap, minimum-edge and polygon safety conditions are enforced before output.' -ForegroundColor Green
Write-Host 'Road Layout now opens CE_ROADRESERVECENTERLINESSAFE instead of the legacy unsafe reserve-centreline engine.' -ForegroundColor Green

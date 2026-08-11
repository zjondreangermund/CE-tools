[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$src = Join-Path $root 'src\CE.Tools.Civil3D'

function Text([string]$name) {
    $path = Join-Path $src $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "AUGUST11 VALIDATION FAILED: missing $path"
    }
    return [System.IO.File]::ReadAllText($path)
}
function Require([bool]$condition,[string]$message) {
    if (-not $condition) { throw "AUGUST11 VALIDATION FAILED: $message" }
}

$production = Text 'August11ProductionCentreCommands.cs'
$presets = Text 'August11DisciplineStylePresetCommands.cs'
$network = Text 'August11NetworkBatchCommands.cs'
$midblock = Text 'August11MidblockSewerProductionCommands.cs'
$road = Text 'August11RoadCompletionCommands.cs'
$roadExtra = Text 'August11RoadNamingCurveCommands.cs'
$vertical = Text 'August11RoadVerticalCurveCommands.cs'
$roadCorridor = Text 'RoadCorridorCompletionCommands.cs'
$roadLayout = Text 'RoadLayoutProductionCommands.cs'
$survey = Text 'August11SurveyRuntimeCommands.cs'
$table = Text 'TableCellNavigationCommands.cs'
$plugin = Text 'PluginEntry.cs'
$legacyNetwork = Text 'FinalWorkflowGapCommands.cs'
$routePlanner = Text 'RoutePlannerExpansionCommands.cs'
$closure = Text 'August10CommentClosureCommands.cs'
$cogo = Text 'CogoPointProjectStyleCommands.cs'
$universal = Text 'UniversalDynamicRefreshCommands.cs'
$projectCoordination = Text 'ProjectCoordinationCommands.cs'
$platform = Text 'PlatformProductionCommands.cs'
$sequence = Text 'CeSequentialCommandRunner.cs'
$styleCentre = Text 'ProjectStyleCenterCommands.cs'

# Production / welcome / guided discipline structure.
foreach ($command in @(
    'CE_WELCOME','CE_CETHEME','CE_PRODUCTIONCENTRE','CE_ENGINEERINGINTELLIGENCECENTRE',
    'CE_PROJECTPRODUCTIONCENTRE','CE_SURVEYPRODUCTIONCENTRE','CE_PLATFORMPRODUCTIONCENTRE',
    'CE_ROADPRODUCTIONCENTRE','CE_SWPRODUCTIONCENTRE','CE_SEWERPRODUCTIONCENTRE',
    'CE_WATERPRODUCTIONCENTRE','CE_BULKWATERPRODUCTIONCENTRE','CE_PARKINGPRODUCTIONCENTRE','CE_FLOODPRODUCTIONCENTRE')) {
    Require ($production.Contains($command)) "$command missing from Production Centre source"
}
foreach ($stage in @('01 SETTINGS','02 PREPARE','03 CREATE','04 DESIGN','05 COMPLETE','06 DELIVER','99 RUN COMPLETE')) {
    Require ($production.Contains($stage)) "guided production stage '$stage' missing"
}
Require ($production.Contains('CE_TOOLS_PRODUCTION_WORKFLOW_TAB')) 'dedicated CE PRODUCTION ribbon tab missing'
Require ($production.Contains('CE-PRODUCTION CENTRE') -and $production.Contains('CE-ENGINEERING INTELLIGENCE CENTRE')) 'two-centre welcome screen missing'
Require ($production.Contains('Dark') -and $production.Contains('Light')) 'CE dark/light preference missing'
Require ($plugin.Contains('ProductionWorkflowRibbonBuilder.EnsureCreated()')) 'dedicated Production ribbon is not wired into PluginEntry'
Require (-not $production.Contains('bool? accepted = AcApplication.ShowModalWindow')) 'welcome screen still uses host-specific modal return signature'

# Per-discipline styles: safe production activation must never leak a previous discipline.
foreach ($command in @('CE_DISCIPLINESTYLEPRESETS','CE_DISCIPLINESTYLEINFO')) {
    Require ($presets.Contains($command)) "$command missing from per-discipline style preset source"
}
Require ($presets.Contains('PROJECT_STYLE_PRESET_')) 'per-discipline style preset records missing'
Require ($presets.Contains('ActivateForProduction')) 'safe production style activation helper missing'
Require ($presets.Contains('var clean = new ProjectStyleSelection')) 'clean drawing-default discipline fallback missing'
Require ($styleCentre.Contains('August11DisciplineStylePresetManager.SavePreset(document.Database, selection);')) 'Project Style Centre does not snapshot discipline presets'
foreach ($discipline in @('Platforms','Roads','Stormwater','Sewer','Water','Bulk Water','Parking','Flood')) {
    $token = 'August11DisciplineStylePresetManager.ActivateForProduction(Active() == null ? null : Active().Database, "' + $discipline + '")'
    Require ($production.Contains($token)) "$discipline Production Centre does not safely activate its discipline preset"
}
Require ($production.Contains('CE_DISCIPLINESTYLEPRESETS')) 'Production Centre does not expose discipline style preset management'

# Network batch / duplicate prevention / legacy handoff.
foreach ($command in @('CE_NETWORKFROMPOLYLINESBATCH','CE_NETWORKCONNECTSELECTED','CE_NETWORKBATCHTOOLS','CE_NETWORKSOURCEMARKERSCLEAR')) {
    Require ($network.Contains($command)) "$command missing from August11 network source"
}
Require ($network.Contains('CE_NETWORK_SOURCE_CREATED')) 'network duplicate-source marker missing'
Require ($network.Contains('Queue<ObjectId>')) 'network-from-object batch queue missing'
Require ($legacyNetwork.Contains('new August11NetworkBatchCommands().CreateNetworksBatch();')) 'legacy network-from-polylines command is not routed to batch workflow'
Require ($legacyNetwork.Contains('new August11NetworkBatchCommands().ConnectSelectedParts();')) 'legacy network-connect command is not routed to selected multi-part workflow'
Require ($roadExtra.Contains('CE_CLOSEPIPESONLY')) 'separate Close Pipes Only command missing'

# Continuous Midblock sewer production.
foreach ($token in @('CE_MIDBLOCKSEWERPRODUCTION','Automatic low side from surface','60 m','80 m','Planning manhole diameter','1.2','Preferred offset from erf corner','ClusterRows','BuildManholeStations')) {
    Require ($midblock.Contains($token)) "midblock production token '$token' missing"
}
Require ($routePlanner.Contains('CE_MIDBLOCKSEWERPRODUCTION')) 'Route Planner Option 2 does not use continuous Midblock Sewer Production'

# Road completion / junctions / naming / utility offsets.
foreach ($command in @('CE_ROADCONTINUITYFIX','CE_ROADOUTSIDEOFFSET','CE_JUNCTIONTRIMBOUNDARIES','CE_JUNCTIONSETTINGOUT4','CE_ROUTEANNOTATIONSTYLE','CE_ROUTESHIFTANNOTATION','CE_POLYLINEARCS')) {
    Require ($road.Contains($command)) "$command missing from road completion source"
}
foreach ($command in @('CE_ROUTEHORIZONTALCURVES','CE_ROADNAMESYNC','CE_UTILITYROUTEOFFSET','CE_CLOSEPIPESONLY')) {
    Require ($roadExtra.Contains($command)) "$command missing from final road/network field source"
}
Require ($road.Contains('CE_TRIMINSIDEMULTI')) 'junction trim workflow is not handed to multi-boundary Trim Inside'
Require ($roadLayout.Contains('new August11RoadCompletionCommands().JunctionSettingOutFourQuadrants();')) 'legacy junction setting-out is not routed to the four-quadrant workflow'
Require (-not $roadLayout.Contains('List<Arc> arcs = ResolveGeneratedJunctions')) 'legacy junction setting-out still resolves only arcs'
Require ($roadExtra.Contains('CE_ROAD_NAME_LINK') -and $roadExtra.Contains('SyncRoadNames')) 'ROAD-n name linkage engine missing'
Require ($universal.Contains('August11RoadNamingCurveCommands.SyncRoadNames(document, false);')) 'ROAD-n names are not dynamically synchronized'

# Safe interactive command sequencing replaces the older SendStringToExecute chains.
foreach ($token in @('CommandEnded += OnCommandEnded','CommandCancelled += OnCommandCancelled','CommandFailed += OnCommandFailed','AcApplication.Idle += OnIdle')) {
    Require ($sequence.Contains($token)) "sequential command runner missing '$token'"
}
Require ($roadCorridor.Contains('CeSequentialCommandRunner.Start')) 'road full-profile/corridor workflows do not use safe sequential execution'
Require ($roadCorridor.Contains('new[] { "CE_ROADPROFILES", "CE_ROADDESIGNPROFILE", "CE_ROADVERTICALCURVES" }')) 'complete road-profile step list is missing'
Require ($roadCorridor.Contains('new[] { "CE_ROADCORRIDORS", "CE_ROADCORRIDORCOMPLETE" }')) 'complete road-corridor step list is missing'
Require (-not $roadCorridor.Contains('SendStringToExecute("CE_ROADPROFILES CE_ROADDESIGNPROFILE')) 'unsafe multi-command road-profile string remains'
Require (-not $roadCorridor.Contains('SendStringToExecute("CE_ROADCORRIDORS CE_ROADCORRIDORCOMPLETE')) 'unsafe multi-command corridor string remains'
Require ($vertical.Contains('CE_ROADPROFILEBESTFIT') -and $vertical.Contains('CE_ROADVERTICALCURVES')) 'final road vertical-curve commands missing'
Require ($vertical.Contains('AddFreeSymmetricParabolaByPVIAndCurveLength')) 'PVI-based parabolic vertical-curve creation missing'
Require ($roadCorridor.Contains('PropertyInfo visibleProperty = corridor.GetType().GetProperty("Visible"')) 'corridor visibility repair missing'
Require ($roadCorridor.Contains('RecordGraphicsModified')) 'corridor graphics refresh missing'

# Survey / COGO / linked table dynamics.
foreach ($command in @('CE_COGOLABELRESTOREINITIAL','CE_COORDMULTISURFACETABLE','CE_COORDMULTISURFACEREFRESH')) {
    Require ($survey.Contains($command)) "$command missing from August11 survey runtime source"
}
Require ($survey.Contains('CE_COGO_LABEL_INITIAL_OFFSET')) 'initial COGO label position storage missing'
Require ($survey.Contains('CogoPointProjectStyleCommands.ApplySelectedStyles')) 'post-setting-out COGO style sync missing'
Require ($survey.Contains('VertexSettingOutCommands.RefreshAll')) 'post-setting-out vertex refresh missing'
Require ($plugin.Contains('August11SurveyRuntimeManager.Initialize();')) 'August11 survey runtime manager is not started'
Require ($plugin.Contains('August11SurveyRuntimeManager.Terminate();')) 'August11 survey runtime manager is not terminated'
Require ($projectCoordination.Contains('August11SurveyRuntimeCommands.SyncProjectLocation(document, town, code);')) 'town/coordinate system is not linked into Project Information'
Require ($closure.Contains('August11SurveyRuntimeCommands.CaptureCogoInitialOffsets(document);')) 'generic overlap does not preserve initial COGO label position'
Require ($cogo.Contains('August11SurveyRuntimeCommands.CaptureCogoInitialOffsets(document);')) 'COGO overlap does not preserve initial label position'
Require ($universal.Contains('August11SurveyRuntimeCommands.RefreshMultiSurfaceTables(document);')) 'multi-surface tables are not part of universal refresh'

# Robust table source navigation: source type is discovered dynamically from the live Entity,
# so validation must not require a hard-coded Civil class token such as "FeatureLine".
foreach ($token in @('CE_TABLECELLZOOM','Table Source Navigation','All linked source objects','Handle')) {
    Require ($table.Contains($token)) "table source navigation token '$token' missing"
}
Require ($table.Contains('pipe, structure, feature line, alignment, profile')) 'table source navigator does not advertise feature-line/design-object support'
Require ($table.Contains('entity.GetType().Name')) 'table source navigator does not identify source types dynamically'
Require ($table.Contains('Describe(entity, index + 1)')) 'table source popup does not build per-source descriptions'
Require (-not $table.Contains('hit != null &&')) 'TableHitTestInfo still treated as nullable/reference type'
Require ($closure.Contains('new TableCellNavigationCommands().TableCellZoom();')) 'legacy table-source zoom is not routed to robust source navigation'

# Earlier field-review behaviour must remain active.
Require ($universal.Contains('FinalFeatureLineReportCommands.RefreshAll(document)')) 'feature-line report is not universally refreshed'
Require ($universal.Contains('CogoPointProjectStyleCommands.ApplySelectedStyles')) 'COGO styles are not universally synchronized'
Require ($cogo.Contains('restrictedPointIds') -or $cogo.Contains('restricted')) 'selected COGO overlap scope missing'
Require (-not $cogo.Contains('return bestDistance == double.MaxValue ? candidates.Last() : best;')) 'old farthest-candidate COGO overlap fallback remains'
Require (-not $platform.Contains('else featureLine.SetPointElevation(index, elevation);')) 'Platform slope still uses unsafe numeric AllPoints index setter'
Require (-not $platform.Contains('child.SetPointElevation(index, sourcePoint.Z + dz);')) 'Platform stepped-offset transfer still uses unsafe numeric point index'

Write-Host 'August 11 field completion validation passed.' -ForegroundColor Green

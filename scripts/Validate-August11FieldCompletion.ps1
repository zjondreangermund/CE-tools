[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$src = Join-Path $root 'src\CE.Tools.Civil3D'
function Text([string]$name) {
    $path = Join-Path $src $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "AUGUST11 VALIDATION FAILED: missing $path" }
    return [System.IO.File]::ReadAllText($path)
}
function Require([bool]$condition,[string]$message) { if (-not $condition) { throw "AUGUST11 VALIDATION FAILED: $message" } }

$production = Text 'August11ProductionCentreCommands.cs'
$network = Text 'August11NetworkBatchCommands.cs'
$midblock = Text 'August11MidblockSewerProductionCommands.cs'
$road = Text 'August11RoadCompletionCommands.cs'
$roadExtra = Text 'August11RoadNamingCurveCommands.cs'
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
Require (-not $production.Contains('bool? accepted = AcApplication.ShowModalWindow')) 'welcome screen still depends on host-specific modal return signature'
foreach ($obsoleteAlias in @('CE_BOQSTORMWATER','CE_REPORTSTORMWATER','CE_PARKGRADINGTOOLS','CE_PARKQTYTOOLS','CE_STANDARDS','CE_HYDROLOGYREVIEW','CE_FLOODQUICK')) {
    Require (-not $production.Contains($obsoleteAlias)) "obsolete Production Centre command alias remains: $obsoleteAlias"
}

# Network batch / duplicate prevention / legacy handoff.
foreach ($command in @('CE_NETWORKFROMPOLYLINESBATCH','CE_NETWORKCONNECTSELECTED','CE_NETWORKBATCHTOOLS','CE_NETWORKSOURCEMARKERSCLEAR')) {
    Require ($network.Contains($command)) "$command missing from August11 network source"
}
Require ($network.Contains('CE_NETWORK_SOURCE_CREATED')) 'network duplicate source marker missing'
Require ($network.Contains('Queue<ObjectId>')) 'network-from-object batch queue missing'
Require ($legacyNetwork.Contains('new August11NetworkBatchCommands().CreateNetworksBatch();')) 'legacy CE_NETWORKFROMPOLYLINES is not routed to batch source selection'
Require ($legacyNetwork.Contains('new August11NetworkBatchCommands().ConnectSelectedParts();')) 'legacy CE_NETWORKCONNECT is not routed to selected multi-part workflow'
Require ($roadExtra.Contains('CE_CLOSEPIPESONLY')) 'separate Close Pipes Only command missing'
Require ($roadExtra.Contains('never calls CE_BOQREFRESH')) 'Close Pipes Only does not explicitly separate itself from BOQ refresh'
$allSourceText = (Get-ChildItem -LiteralPath $src -Filter '*.cs' -File | ForEach-Object { [System.IO.File]::ReadAllText($_.FullName) }) -join "`n"
$badClosePattern = '(?is)"[^"\r\n]*Close\s+Pipe(?:s|\s+Ends)?[^"\r\n]*"\s*,\s*"CE_BOQREFRESH'
Require (-not [regex]::IsMatch($allSourceText,$badClosePattern)) 'a Close Pipes action is still mapped to CE_BOQREFRESH'

# Continuous midblock sewer production.
foreach ($token in @('CE_MIDBLOCKSEWERPRODUCTION','Automatic low side from surface','60 m','80 m','Planning manhole diameter','1.2','Preferred offset from erf corner','ClusterRows','BuildManholeStations')) {
    Require ($midblock.Contains($token)) "midblock production token '$token' missing"
}
Require ($routePlanner.Contains('CE_MIDBLOCKSEWERPRODUCTION')) 'Route Planner Option 2 does not use continuous Midblock Sewer Production'

# Road finishing / junction sequencing / annotation.
foreach ($command in @('CE_ROADCONTINUITYFIX','CE_ROADOUTSIDEOFFSET','CE_JUNCTIONTRIMBOUNDARIES','CE_JUNCTIONSETTINGOUT4','CE_ROUTEANNOTATIONSTYLE','CE_ROUTESHIFTANNOTATION','CE_POLYLINEARCS')) {
    Require ($road.Contains($command)) "$command missing from road completion source"
}
foreach ($command in @('CE_ROUTEHORIZONTALCURVES','CE_ROADNAMESYNC','CE_UTILITYROUTEOFFSET','CE_CLOSEPIPESONLY')) {
    Require ($roadExtra.Contains($command)) "$command missing from final road/network field source"
}
Require ($road.Contains('IsPlottable = plottable')) 'junction trim non-plot layer control missing'
Require ($road.Contains('CE_TRIMINSIDEMULTI')) 'junction trim boundary workflow is not handed to multi-boundary Trim Inside'
Require ($road.Contains('1.8') -and $road.Contains('2.0') -and $road.Contains('2.5') -and $road.Contains('3.5') -and $road.Contains('5.0')) 'route annotation paper text choices incomplete'
Require ($road.Contains('Show metre suffix')) 'route dimensions do not expose metre display'
Require ($road.Contains('PaperAnnotationScale.ModelDistance')) 'route arrow-size paper scaling missing'
Require ($roadExtra.Contains('Horizontal curve radius') -and $roadExtra.Contains('BuildFilletedPolyline')) 'multiple route horizontal curves/radii implementation missing'
Require ($roadExtra.Contains('CE_ROAD_NAME_LINK') -and $roadExtra.Contains('SyncRoadNames')) 'ROAD-n name linkage engine missing'
Require ($roadExtra.Contains('Stormwater') -and $roadExtra.Contains('Sewer') -and $roadExtra.Contains('Water') -and $roadExtra.Contains('Bulk Water')) 'utility route offsets are not discipline-aware'
Require ($universal.Contains('August11RoadNamingCurveCommands.SyncRoadNames(document, false);')) 'ROAD-n names are not dynamically synchronized in universal refresh'
Require ($plugin.Contains('Cmd("Create / Rebuild Baselines and Regions", "CE_ROADCORRIDORCOMPLETE ')) 'Corridor Baselines/Regions still points to a report instead of production'
Require ($plugin.Contains('Cmd("Baseline / Region Report", "CE_CORBASEUI ')) 'Corridor baseline/region report was not preserved separately'
Require ($plugin.Contains('CE_NETWORKFROMPOLYLINESBATCH') -and $plugin.Contains('CE_UTILITYROUTEOFFSET') -and $plugin.Contains('CE_CLOSEPIPESONLY')) 'new field network/utility tools are not exposed in Utilities ribbon'
Require ($production.Contains('CE_ROUTEHORIZONTALCURVES') -and $production.Contains('CE_ROADNAMESYNC')) 'Road Production Centre does not expose horizontal curves and road-name sync'
Require ($production.Contains('CE_UTILITYROUTEOFFSET')) 'guided utility production still lacks explicit erf/reserve offset workflow'

# Survey / COGO / linked table dynamics.
foreach ($command in @('CE_COGOLABELRESTOREINITIAL','CE_COORDMULTISURFACETABLE','CE_COORDMULTISURFACEREFRESH')) {
    Require ($survey.Contains($command)) "$command missing from August11 survey runtime source"
}
Require ($survey.Contains('CE_COGO_LABEL_INITIAL_OFFSET')) 'initial COGO label position storage missing'
Require ($survey.Contains('CogoPointProjectStyleCommands.ApplySelectedStyles')) 'immediate post-setting-out COGO style sync missing'
Require ($survey.Contains('VertexSettingOutCommands.RefreshAll')) 'immediate vertex setting-out refresh missing'
Require ($survey.Contains('August11SurveyRuntimeManager')) 'immediate survey runtime manager missing'
Require ($plugin.Contains('August11SurveyRuntimeManager.Initialize();')) 'August11 survey runtime manager is not started'
Require ($plugin.Contains('August11SurveyRuntimeManager.Terminate();')) 'August11 survey runtime manager is not terminated'
Require ($projectCoordination.Contains('August11SurveyRuntimeCommands.SyncProjectLocation(document, town, code);')) 'survey town/coordinate system is not linked into Project Information'
Require ($closure.Contains('restored += August11SurveyRuntimeCommands.RestoreCogoLabels(document, selected);')) 'generic annotation Restore does not restore COGO labels'
Require ($closure.Contains('August11SurveyRuntimeCommands.CaptureCogoInitialOffsets(document);')) 'smart overlap does not preserve initial COGO label position'
Require ($cogo.Contains('August11SurveyRuntimeCommands.CaptureCogoInitialOffsets(document);')) 'COGO overlap command does not preserve initial label position'
Require ($universal.Contains('August11SurveyRuntimeCommands.RefreshMultiSurfaceTables(document);')) 'multi-surface coordinate tables are not part of universal refresh'

# Robust table source navigation.
foreach ($token in @('CE_TABLECELLZOOM','Table Source Navigation','All linked source objects','FeatureLine','Handle')) {
    Require ($table.Contains($token)) "table source navigation token '$token' missing"
}
Require (-not $table.Contains('hit != null &&')) 'TableHitTestInfo still treated as nullable/reference type'
Require ($closure.Contains('new TableCellNavigationCommands().TableCellZoom();')) 'legacy CE_TABLESOURCEZOOM is not routed to robust source popup'

# Existing field-review items that must remain active from the earlier closure pass.
Require ($universal.Contains('FinalFeatureLineReportCommands.RefreshAll(document)')) 'feature-line report is not universally refreshed'
Require ($universal.Contains('CogoPointProjectStyleCommands.ApplySelectedStyles')) 'COGO styles are not universally synchronized'
Require ($cogo.Contains('restrictedPointIds') -or $cogo.Contains('restricted')) 'selected COGO overlap scope missing'
Require (-not $cogo.Contains('return bestDistance == double.MaxValue ? candidates.Last() : best;')) 'old farthest-candidate COGO overlap fallback remains after compatibility repair'
Require (-not $platform.Contains('else featureLine.SetPointElevation(index, elevation);')) 'Platform slope still uses unsafe AllPoints numeric index setter'
Require (-not $platform.Contains('child.SetPointElevation(index, sourcePoint.Z + dz);')) 'Platform stepped-offset transfer still uses unsafe numeric point index'

# PDF popup and dynamic linked-output systems from prior field closure must remain.
$final = Text 'FinalAllCommentsCompletionCommands.cs'
Require ($final.Contains('CE_PDFTODWG')) 'PDF-to-DWG workflow missing'
Require ($final.Contains('PromptOpenFileOptions')) 'PDF-to-DWG file picker popup missing'
Require ($universal.Contains('Automatic') -or $universal.Contains('CommandEnded')) 'automatic linked-output refresh runtime missing'

Write-Host 'August 11 field completion validation passed.' -ForegroundColor Green

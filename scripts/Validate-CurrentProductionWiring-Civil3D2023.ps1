[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$src = Join-Path $root 'src\CE.Tools.Civil3D'

function Read-RequiredSource([string]$name) {
    $path = Join-Path $src $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "CURRENT PRODUCTION WIRING VALIDATION FAILED: missing source file $path"
    }
    return [System.IO.File]::ReadAllText($path)
}

function Require-Token([string]$text,[string]$token,[string]$description) {
    if (-not $text.Contains($token)) {
        throw "CURRENT PRODUCTION WIRING VALIDATION FAILED: $description [$token]"
    }
}

$plugin = Read-RequiredSource 'PluginEntry.cs'
$production = Read-RequiredSource 'August11ProductionCentreCommands.cs'
$dialogs = Read-RequiredSource 'DisciplineWorkflowDialogs.cs'
$routePlanner = Read-RequiredSource 'RoutePlannerExpansionCommands.cs'
$legacyNetwork = Read-RequiredSource 'FinalWorkflowGapCommands.cs'
$universal = Read-RequiredSource 'UniversalDynamicRefreshCommands.cs'
$projectCoordination = Read-RequiredSource 'ProjectCoordinationCommands.cs'
$closure = Read-RequiredSource 'August10CommentClosureCommands.cs'
$cogo = Read-RequiredSource 'CogoPointProjectStyleCommands.cs'
$styleCentre = Read-RequiredSource 'ProjectStyleCenterCommands.cs'
$roadLayout = Read-RequiredSource 'RoadLayoutProductionCommands.cs'
$roadCompletion = Read-RequiredSource 'August11RoadCompletionCommands.cs'
$platformSafety = Read-RequiredSource 'August21PlatformRelativeFatalSafety.cs'
Read-RequiredSource 'AugustGlobalShortcutManager.cs' | Out-Null

# Dedicated CE PRODUCTION startup and current runtime managers.
foreach ($token in @(
    'ProductionWorkflowRibbonBuilder.EnsureCreated()',
    'August11SurveyRuntimeManager.Initialize();',
    'August11SurveyRuntimeManager.Terminate();',
    'AugustGlobalShortcutManager.Initialize();',
    'AugustGlobalShortcutManager.Terminate();',
    'SendStringToExecute("CE_WELCOME "'
)) {
    Require-Token $plugin $token 'PluginEntry production/runtime startup wiring is incomplete'
}

# Main CE TOOLS ribbon items that were previously supplied only by staged injectors.
foreach ($token in @(
    'CE_ROUTEPLANNER',
    'CE_ROADLAYOUTTOOLS',
    'CE_PLATFORMTOOLS',
    'CE_NETWORKMULTI',
    'CE_SETTINGSMODE',
    'CE_PROFILEBATCHSAFE',
    'CE_COMMENTCLOSURE',
    'CE_NAMIBIALO',
    'CE_LANDXMLTOOLS',
    'CE_TOOLS_PLATFORM_PRODUCTION_MENU',
    'Cmd("CE Tools Home", "CE_WELCOME ',
    'Cmd("Discipline Style Presets", "CE_DISCIPLINESTYLEPRESETS ',
    'Cmd("Complete Final Road Profile", "CE_ROADPROFILEFULL '
)) {
    Require-Token $plugin $token 'Main CE TOOLS production ribbon entry is missing'
}

# Dedicated CE PRODUCTION disciplines and independent style centres.
foreach ($token in @(
    'CE_TOOLS_PRODUCTION_WORKFLOW_TAB',
    'CE_PRODUCTIONCENTRE',
    'CE_PROJECTPRODUCTIONCENTRE',
    'CE_SURVEYPRODUCTIONCENTRE',
    'CE_PLATFORMPRODUCTIONCENTRE',
    'CE_ROADPRODUCTIONCENTRE',
    'CE_SWPRODUCTIONCENTRE',
    'CE_SEWERPRODUCTIONCENTRE',
    'CE_WATERPRODUCTIONCENTRE',
    'CE_BULKWATERPRODUCTIONCENTRE',
    'CE_PARKINGPRODUCTIONCENTRE',
    'CE_FLOODPRODUCTIONCENTRE',
    'CE_SURVEYSTYLES',
    'CE_PLATFORMSTYLES',
    'CE_SWSTYLES',
    'CE_SEWERSTYLES',
    'CE_WATERSTYLES',
    'CE_BULKWATERSTYLES',
    'CE_PARKINGSTYLES',
    'CE_FLOODSTYLES'
)) {
    Require-Token $production $token 'Dedicated CE PRODUCTION discipline/style wiring is missing'
}

# Production centres must remain open and dispatch safely outside the WPF click.
foreach ($token in @(
    'KeepOpenOnAction = true',
    'AcApplication.ShowModelessWindow(window);',
    'ResolveStyleDiscipline(Title)',
    'Dispatcher.BeginInvoke(new Action(delegate',
    'using (DocumentLock documentLock = document.LockDocument())',
    'Width = new GridLength(330)',
    'Width = new GridLength(28)',
    '!CrossDrawingSettingsPreference.UseSavedProjectSettings'
)) {
    Require-Token $dialogs $token 'Persistent Production Centre behavior is incomplete'
}

# Previously staged field-completion handoffs that current-source builds require.
Require-Token $routePlanner 'CE_MIDBLOCKSEWERPRODUCTION' 'Route Planner no longer uses continuous Midblock Sewer Production'
Require-Token $legacyNetwork 'new August11NetworkBatchCommands().CreateNetworksBatch();' 'Legacy network-from-object is not routed to multi-source batch'
Require-Token $legacyNetwork 'new August11NetworkBatchCommands().ConnectSelectedParts();' 'Legacy network connect is not routed to selected multi-part workflow'
Require-Token $universal 'August11RoadNamingCurveCommands.SyncRoadNames(document, false);' 'ROAD-n dynamic synchronization is missing'
Require-Token $universal 'August11SurveyRuntimeCommands.RefreshMultiSurfaceTables(document);' 'Linked multi-surface table refresh is missing'
Require-Token $projectCoordination 'August11SurveyRuntimeCommands.SyncProjectLocation(document, town, code);' 'Survey location is not synchronized to Project Information'
Require-Token $closure 'new TableCellNavigationCommands().TableCellZoom();' 'Robust linked-table source navigation handoff is missing'
Require-Token $closure 'August11SurveyRuntimeCommands.CaptureCogoInitialOffsets(document);' 'Smart-overlap COGO initial-position capture is missing'
Require-Token $cogo 'August11SurveyRuntimeCommands.CaptureCogoInitialOffsets(document);' 'COGO initial-position capture is missing'
Require-Token $styleCentre '"Bulk Water"' 'Bulk Water is missing from Project Style Centre'
Require-Token $styleCentre '"Parking"' 'Parking is missing from Project Style Centre'
Require-Token $styleCentre '"Flood"' 'Flood is missing from Project Style Centre'
Require-Token $styleCentre 'August11DisciplineStylePresetManager.SavePreset(document.Database, selection);' 'Project Style Centre does not snapshot the active discipline preset'
Require-Token $roadLayout 'new August11RoadCompletionCommands().JunctionSettingOutFourQuadrants();' 'Legacy road junction setting-out is not routed to the four-quadrant workflow'
Require-Token $roadCompletion '"CE_BELLMOUTHTRIMEDGES"' 'Bellmouth tangent trim is missing from Road Completion'

# Do not require obsolete PlatformProductionCommands literal markers here.
# The current Civil 3D 2023 build intentionally replaces StepOffsets/Drape/Refresh
# through Repair-August21-PlatformRelativeFatalSafety-Civil3D2023.ps1.
foreach ($token in @(
    'CreatePlatformSteps(',
    'DrapeSelection(',
    'RefreshPlatformDrapes('
)) {
    Require-Token $platformSafety $token 'Current August 21 platform fatal-safety runtime is incomplete'
}

Write-Host 'Current CE Tools production wiring validation passed.' -ForegroundColor Green

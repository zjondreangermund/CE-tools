[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$src = Join-Path $root 'src\CE.Tools.Civil3D'

function Read-Source([string]$name) {
    $path = Join-Path $src $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing required source: $path" }
    return [System.IO.File]::ReadAllText($path)
}

function Require([string]$text,[string]$token,[string]$label) {
    if (-not $text.Contains($token)) { throw "Final comment validation failed: missing $label -> $token" }
}

function Forbid([string]$text,[string]$token,[string]$label) {
    if ($text.Contains($token)) { throw "Final comment validation failed: forbidden $label remains -> $token" }
}

function Require-File([string]$relative) {
    $path = Join-Path $root $relative
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Final comment validation failed: missing file $relative" }
    return [System.IO.File]::ReadAllText($path)
}

$closure = Read-Source 'August10CommentClosureCommands.cs'
$namibia = Read-Source 'NamibiaCoordinateRuntimeCommands.cs'
$route = Read-Source 'RoutePlannerExpansionCommands.cs'
$midblock = Read-Source 'MidblockSewerLayoutCommands.cs'
$behavior = Read-Source 'AugustBehaviorCompletionCommands.cs'
$automatic = Read-Source 'AugustAutomaticRefreshManager.cs'
$tableCell = Read-Source 'TableCellNavigationCommands.cs'
$tablePresentation = Read-Source 'TablePresentationRepairCommands.cs'
$selectedFeatureRefresh = Read-Source 'SelectedFeatureLineRefreshCommands.cs'
$leaderPlacement = Read-Source 'AnnotationLeaderPlacementCommands.cs'
$branchLabels = Read-Source 'BranchLabelLayerCommands.cs'
$annotationReview = Read-Source 'FinalAnnotationReviewCommands.cs'
$roadStyleDefaults = Read-Source 'AugustRoadStyleDefaults.cs'
$platform = Read-Source 'PlatformProductionCommands.cs'
$road = Read-Source 'RoadLayoutProductionCommands.cs'
$drawing = Read-Source 'MultiBoundaryEditCommands.cs'
$dialogs = Read-Source 'DisciplineWorkflowDialogs.cs'
$plugin = Read-Source 'PluginEntry.cs'
$surface = Read-Source 'SurfaceSpikeHoleRepairCommands.cs'
$roadProduction = Read-Source 'RoadProductionCommentCommands.cs'
$cogo = Read-Source 'CogoPointProjectStyleCommands.cs'
$universal = Read-Source 'UniversalDynamicRefreshCommands.cs'
$sewerProduction = Read-Source 'SewerProductionCommands.cs'
$stormwaterProduction = Read-Source 'StormwaterProductionCommands.cs'
$waterProduction = Read-Source 'WaterProductionCommands.cs'
$sewerExcavation = Read-Source 'SewerExcavationCommentCommands.cs'

$requiredCommands = @(
    'CE_COMMENTCLOSURE','CE_ANNOTATIONREVIEW','CE_OVERLAPSMART','CE_ANNOTATIONRESTORE','CE_ANNOTATIONMASK','CE_ANNOTATIONDRAWORDER','CE_MLEADERTEXTABOVE','CE_BRANCHLABELLAYER',
    'CE_TABLESOURCEZOOM','CE_TABLECELLZOOM','CE_TABLEPRESENTATIONFIX','CE_FLANNOTREFRESH','CE_FLANNOTREFRESHSELECTED','CE_LANDXMLTOOLS','CE_LANDXMLIMPORT','CE_LANDXMLEXPORT','CE_EXPORTCADCOPY',
    'CE_NETWORKMULTI','CE_SETTINGSMODE','CE_PROFILEBATCHSAFE','CE_COMMENTREFRESHALL',
    'CE_NAMIBIALO','CE_COORDPICKMAP','CE_ROUTEPLANNER','CE_UTILITYFROMROADRESERVE','CE_MIDBLOCKSEWERLAYOUT',
    'CE_JUNCTIONRETURNTYPE','CE_SWSEQPRODUCTION','CE_WATERSEQPRODUCTION','CE_ASSEMBLYMARKERS',
    'CE_ROADLAYOUTTOOLS','CE_ROADRESERVECENTERLINES','CE_ROADEDGES','CE_ROADSHOULDERS','CE_ROADJUNCTIONBULK','CE_ROADJUNCTIONTRIM',
    'CE_ROADNAMES','CE_ROADDIMENSIONS','CE_ROADJUNCTIONSETTINGOUT','CE_ROADLAYOUTREFRESH',
    'CE_PLATFORMTOOLS','CE_PLATFORMSLOPE','CE_PLATFORMSTEPOFFSETS','CE_PLATFORMDRAPE','CE_PLATFORMSURFACE',
    'CE_PLATFORMSETTINGOUT','CE_PLATFORMNAMES','CE_PLATFORMTABLE','CE_PLATFORMCUTFILL','CE_PLATFORMDRAWINGS','CE_PLATFORMREFRESH',
    'CE_BOUNDARYEDITTOOLS','CE_TRIMOUTSIDEMULTI','CE_TRIMINSIDEMULTI','CE_TRIMDELETEOUTSIDEMULTI','CE_TRIMDELETEINSIDEMULTI','CE_EXTENDOUTSIDEMULTI','CE_EXTENDINSIDEMULTI'
)
$combinedNew = $closure + "`n" + $namibia + "`n" + $route + "`n" + $midblock + "`n" + $behavior + "`n" + $automatic + "`n" + $tableCell + "`n" + $tablePresentation + "`n" + $selectedFeatureRefresh + "`n" + $leaderPlacement + "`n" + $branchLabels + "`n" + $annotationReview + "`n" + $platform + "`n" + $road + "`n" + $drawing
foreach ($command in $requiredCommands) { Require $combinedNew ('"' + $command + '"') ('command ' + $command) }

# Critical annotation behaviour.
foreach ($token in @('CE_OVERLAP_ORIGINAL','ResolveOverlaps(document, restricted)','MoveToTop(ids)','MoveToBottom(ids)','BackgroundFill','UseBackgroundColor')) { Require $closure $token 'selective/restorable annotation behaviour' }
foreach ($token in @('GetLastVertex','TextLocation','CE_OVERLAP_ORIGINAL','leader/reference vertices unchanged')) { Require $leaderPlacement $token 'MLeader text-above-leader behaviour' }
foreach ($token in @('CE-BRANCH-LABELS','LooksLikeBranchLabel','ContainsBranch')) { Require $branchLabels $token 'dedicated branch-label layer' }
foreach ($token in @('CE_MLEADERTEXTABOVE','CE_TABLEPRESENTATIONFIX','CE_TABLECELLZOOM','CE_FLANNOTREFRESHSELECTED','CE_BRANCHLABELLAYER')) { Require $annotationReview $token 'final annotation review workflow' }

# Staged COGO resolver must now preserve clear labels and never use the old far candidate fallback.
Require $cogo 'if (!occupied.Any(existing => existing.Intersects(currentBox)))' 'COGO non-overlap keep-position guard'
Require $cogo 'return bestDistance == double.MaxValue ? item.LabelLocation : best;' 'COGO bounded safe fallback'
Forbid $cogo 'return bestDistance == double.MaxValue ? candidates.Last() : best;' 'COGO farthest-candidate fallback'

# Table behaviour.
foreach ($token in @('table.HitTest(picked.PickedPoint','hit.Row','hit.Column','LinkedTableSourceNavigator.Discover')) { Require $tableCell $token 'linked table cell source navigation' }
foreach ($token in @('SetCellGridVisible','GridLineType.AllGridLines','CellAlignment.MiddleCenter','GenerateLayout')) { Require $tablePresentation $token 'table grid/spacing presentation repair' }
foreach ($token in @('FindTablesLinkedToSources','BindingFlags.NonPublic | BindingFlags.Static','CE_FLRELUPDATEMULTI')) { Require $selectedFeatureRefresh $token 'true selected feature-line linked refresh' }

# Namibia survey-grid transformation.
foreach ($token in @('SchwarzeckA = 6377483.86528042','SchwarzeckInvF = 299.1528128','Dx = 616.0','Dy = 97.0','Dz = -251.0','LatitudeOriginDegrees = -22.0','GermanLegalMetre = 1.0000135965','TryParseAngle','FormatDms')) { Require $namibia $token 'Namibia LO transformation' }

# Route-planner behaviour: road-reserve option and explicit Midblock centre + visible side offsets.
foreach ($token in @('CE-ROAD-CENTERLINE','CE-SEWER-ROUTE','CE-SW-ROUTE','CE-WATER-ROUTE','CE-BULK-WATER-ROUTE','Signed lateral offset')) { Require $route $token 'route planner road-reserve handoff' }
Require $route '"CE_MIDBLOCKSEWERLAYOUT"' 'Route Planner Option 2 Midblock final handoff'
foreach ($token in @('CE-SEWER-MIDBLOCK-CENTER','CE-SEWER-MIDBLOCK-OFFSET','SideOffset','GetOffsetCurves','visible side offsets')) { Require $midblock $token 'Midblock sewer centre and parallel offset output' }

# Road / assembly / utility behaviour.
foreach ($token in @('CopyExtensionRecords','CE_ASSEMBLY_VISIBLE_MARKER','CE_SWSEQPRODUCTION','CE_WATERSEQPRODUCTION')) { Require $behavior $token 'junction/assembly/utility behaviour' }
Require $road 'model.Text("Geometry")' 'junction Arc/Polyline output choice'
Require $roadProduction 'AugustRoadProfileDefaults.PreferredBandSet' 'requested default road profile band set'
Require $roadProduction 'AugustRoadStyleDefaults.Resolve(' 'road-only style fallback'
foreach ($token in @('text.Contains("pipe")','text.Contains("sewer")','text.Contains("water")','DefaultBandSet')) { Require $roadStyleDefaults $token 'road style preference/utility-style rejection' }

# Utility profiles must be isolated per alignment/branch so one bad object cannot roll back the whole batch.
foreach ($token in @('CE_SEWPROFILE skipped {0}: {1}','new List<SewerAlignmentRecord> { record }','skipped branches: {3}')) { Require $sewerProduction $token 'sewer per-branch profile isolation' }
foreach ($token in @('new List<StormwaterAlignmentRecord> { record }','skipped alignments: {3}','CE_SWPROFILE band refresh warning')) { Require $stormwaterProduction $token 'stormwater per-alignment profile isolation' }
foreach ($token in @('CE_WATERPROFILE skipped {0}: {1}','skipped alignments: {2}','CE_WATERPROFILE band refresh warning')) { Require $waterProduction $token 'water per-alignment profile isolation' }

# Sewer excavation must display standard nominal pipe diameters rather than raw host values.
foreach ($token in @('110','160','200','250','300','OrderBy(value => Math.Abs(value - millimetres)).First()')) { Require $sewerExcavation $token 'sewer nominal diameter normalization' }

# Automatic linked refresh and branch label enforcement.
foreach ($token in @('document.CommandEnded += OnCommandEnded','UniversalDynamicRefreshManager.Queue()','PlatformDynamicRefreshManager.Queue()','ReadCommandName(args)')) { Require $automatic $token 'automatic linked refresh' }
Require $universal 'BranchLabelLayerRuntime.Apply(document);' 'automatic branch-label layer refresh'

# The staged source must have all startup/ribbon integrations before validation runs.
foreach ($token in @(
    'AugustGlobalShortcutManager.Initialize();','AugustAutomaticRefreshManager.Initialize();',
    'CE_COMMENTCLOSURE','CE_ANNOTATIONREVIEW','CE_MLEADERTEXTABOVE','CE_TABLEPRESENTATIONFIX','CE_TABLECELLZOOM','CE_FLANNOTREFRESHSELECTED','CE_BRANCHLABELLAYER',
    'CE_NAMIBIALO','CE_ROUTEPLANNER','CE_NETWORKMULTI','CE_SETTINGSMODE','CE_PROFILEBATCHSAFE','CE_ASSEMBLYMARKERS','CE_JUNCTIONRETURNTYPE','CE_SWSEQPRODUCTION','CE_WATERSEQPRODUCTION')) {
    Require $plugin $token 'staged ribbon/startup integration'
}
Require $dialogs '!CrossDrawingSettingsPreference.UseSavedProjectSettings' 'saved-vs-drawing settings priority'
Require $surface 'Internal holes only' 'surface internal-holes-only mode'

# Native Civil 3D interoperability command names requested by comments.
foreach ($token in @('_LANDXMLIN','_LANDXMLOUT','_EXPORTC3DDRAWING')) { Require $closure $token 'native Civil 3D interoperability command' }

# Verify the one-click build runs every closure injector/repair before this validator.
$stage = Require-File 'scripts\Stage-Build-Install-Civil3D2023.ps1'
foreach ($token in @(
    'Inject-ProductionExpansion-Civil3D2023.ps1',
    'Inject-August10BehaviorFixes-Civil3D2023.ps1',
    'Inject-FinalAnnotationReview2-Civil3D2023.ps1',
    'Repair-CogoOverlap-Civil3D2023.ps1',
    'Repair-RoadStyleFallback-Civil3D2023.ps1',
    'Repair-BranchLabelRefresh-Civil3D2023.ps1',
    'Repair-MidblockRoutePlanner-Civil3D2023.ps1',
    'Repair-UtilityProfileIsolation-Civil3D2023.ps1',
    'Validate-August10CommentClosure.ps1')) {
    Require $stage $token 'Civil 3D 2023 stage build gate'
}

# Command declarations in the final command classes must remain unique.
$newCommandFiles = @(
    'August10CommentClosureCommands.cs','NamibiaCoordinateRuntimeCommands.cs','RoutePlannerExpansionCommands.cs','MidblockSewerLayoutCommands.cs',
    'AugustBehaviorCompletionCommands.cs','TableCellNavigationCommands.cs','TablePresentationRepairCommands.cs',
    'SelectedFeatureLineRefreshCommands.cs','AnnotationLeaderPlacementCommands.cs','BranchLabelLayerCommands.cs','FinalAnnotationReviewCommands.cs',
    'RoadLayoutProductionCommands.cs','PlatformProductionCommands.cs','MultiBoundaryEditCommands.cs'
)
$declarations = @{}
foreach ($name in $newCommandFiles) {
    $text = Read-Source $name
    foreach ($match in [regex]::Matches($text, '\[CommandMethod\(\s*"CE_TOOLS"\s*,\s*"(?<name>CE_[A-Z0-9_]+)"')) {
        $command = $match.Groups['name'].Value
        if (-not $declarations.ContainsKey($command)) { $declarations[$command] = @() }
        $declarations[$command] += $name
    }
}
foreach ($command in $requiredCommands) {
    if (-not $declarations.ContainsKey($command)) { throw "Final comment validation failed: no CommandMethod declaration for $command" }
    if ($declarations[$command].Count -ne 1) { throw "Final comment validation failed: duplicate CommandMethod declaration for $command in $($declarations[$command] -join ', ')" }
}

Write-Host 'Final CE Tools comment closure validation passed.' -ForegroundColor Green
Write-Host ('Validated commands: ' + $requiredCommands.Count) -ForegroundColor DarkGreen

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

function Require-File([string]$relative) {
    $path = Join-Path $root $relative
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Final comment validation failed: missing file $relative" }
    return [System.IO.File]::ReadAllText($path)
}

$closure = Read-Source 'August10CommentClosureCommands.cs'
$namibia = Read-Source 'NamibiaCoordinateRuntimeCommands.cs'
$route = Read-Source 'RoutePlannerExpansionCommands.cs'
$behavior = Read-Source 'AugustBehaviorCompletionCommands.cs'
$automatic = Read-Source 'AugustAutomaticRefreshManager.cs'
$platform = Read-Source 'PlatformProductionCommands.cs'
$road = Read-Source 'RoadLayoutProductionCommands.cs'
$drawing = Read-Source 'MultiBoundaryEditCommands.cs'
$dialogs = Read-Source 'DisciplineWorkflowDialogs.cs'
$plugin = Read-Source 'PluginEntry.cs'
$surface = Read-Source 'SurfaceSpikeHoleRepairCommands.cs'
$roadProduction = Read-Source 'RoadProductionCommentCommands.cs'

$requiredCommands = @(
    'CE_COMMENTCLOSURE','CE_OVERLAPSMART','CE_ANNOTATIONRESTORE','CE_ANNOTATIONMASK','CE_ANNOTATIONDRAWORDER',
    'CE_TABLESOURCEZOOM','CE_FLANNOTREFRESH','CE_LANDXMLTOOLS','CE_LANDXMLIMPORT','CE_LANDXMLEXPORT','CE_EXPORTCADCOPY',
    'CE_NETWORKMULTI','CE_SETTINGSMODE','CE_PROFILEBATCHSAFE','CE_COMMENTREFRESHALL',
    'CE_NAMIBIALO','CE_COORDPICKMAP','CE_ROUTEPLANNER','CE_UTILITYFROMROADRESERVE',
    'CE_JUNCTIONRETURNTYPE','CE_SWSEQPRODUCTION','CE_WATERSEQPRODUCTION','CE_ASSEMBLYMARKERS',
    'CE_ROADLAYOUTTOOLS','CE_ROADRESERVECENTERLINES','CE_ROADEDGES','CE_ROADSHOULDERS','CE_ROADJUNCTIONBULK','CE_ROADJUNCTIONTRIM',
    'CE_ROADNAMES','CE_ROADDIMENSIONS','CE_ROADJUNCTIONSETTINGOUT','CE_ROADLAYOUTREFRESH',
    'CE_PLATFORMTOOLS','CE_PLATFORMSLOPE','CE_PLATFORMSTEPOFFSETS','CE_PLATFORMDRAPE','CE_PLATFORMSURFACE',
    'CE_PLATFORMSETTINGOUT','CE_PLATFORMNAMES','CE_PLATFORMTABLE','CE_PLATFORMCUTFILL','CE_PLATFORMDRAWINGS','CE_PLATFORMREFRESH',
    'CE_BOUNDARYEDITTOOLS','CE_TRIMOUTSIDEMULTI','CE_TRIMINSIDEMULTI','CE_TRIMDELETEOUTSIDEMULTI','CE_TRIMDELETEINSIDEMULTI','CE_EXTENDOUTSIDEMULTI','CE_EXTENDINSIDEMULTI'
)
$combinedNew = $closure + "`n" + $namibia + "`n" + $route + "`n" + $behavior + "`n" + $platform + "`n" + $road + "`n" + $drawing
foreach ($command in $requiredCommands) { Require $combinedNew ('"' + $command + '"') ('command ' + $command) }

# Critical behaviour markers.
foreach ($token in @('CE_OVERLAP_ORIGINAL','ResolveOverlaps(document, restricted)','MoveToTop(ids)','MoveToBottom(ids)','BackgroundFill','UseBackgroundColor')) { Require $closure $token 'selective/restorable annotation behaviour' }
foreach ($token in @('SchwarzeckA = 6377483.86528042','SchwarzeckInvF = 299.1528128','Dx = 616.0','Dy = 97.0','Dz = -251.0','LatitudeOriginDegrees = -22.0','GermanLegalMetre = 1.0000135965','TryParseAngle','FormatDms')) { Require $namibia $token 'Namibia LO transformation' }
foreach ($token in @('CE-ROAD-CENTERLINE','CE-SEWER-ROUTE','CE-SW-ROUTE','CE-WATER-ROUTE','CE-BULK-WATER-ROUTE','Signed lateral offset')) { Require $route $token 'route planner road-reserve handoff' }
foreach ($token in @('CopyExtensionRecords','CE_ASSEMBLY_VISIBLE_MARKER','CE_SWSEQPRODUCTION','CE_WATERSEQPRODUCTION')) { Require $behavior $token 'junction/assembly/utility behaviour' }
foreach ($token in @('Document.CommandEnded += OnCommandEnded','UniversalDynamicRefreshManager.Queue()','PlatformDynamicRefreshManager.Queue()','ReadCommandName(args)')) { Require $automatic $token 'automatic linked refresh' }

# The staged source must have the integration edits before this validator runs.
foreach ($token in @('AugustGlobalShortcutManager.Initialize();','AugustAutomaticRefreshManager.Initialize();','CE_COMMENTCLOSURE','CE_NAMIBIALO','CE_ROUTEPLANNER','CE_NETWORKMULTI','CE_SETTINGSMODE','CE_PROFILEBATCHSAFE','CE_ASSEMBLYMARKERS','CE_JUNCTIONRETURNTYPE','CE_SWSEQPRODUCTION','CE_WATERSEQPRODUCTION')) { Require $plugin $token 'staged ribbon/startup integration' }
Require $dialogs '!CrossDrawingSettingsPreference.UseSavedProjectSettings' 'saved-vs-drawing settings priority'
Require $surface 'Internal holes only' 'surface internal-holes-only mode'
Require $roadProduction 'AugustRoadProfileDefaults.PreferredBandSet' 'requested default road profile band set'
Require $road 'model.Text("Geometry")' 'junction Arc/Polyline output choice'

# Native command names requested by the comments.
foreach ($token in @('_LANDXMLIN','_LANDXMLOUT','_EXPORTC3DDRAWING')) { Require $closure $token 'native Civil 3D interoperability command' }

# Verify the build pipeline contains both closure injectors and this validator.
$stage = Require-File 'scripts\Stage-Build-Install-Civil3D2023.ps1'
foreach ($token in @('Inject-ProductionExpansion-Civil3D2023.ps1','Inject-August10BehaviorFixes-Civil3D2023.ps1','Validate-August10CommentClosure.ps1')) { Require $stage $token 'Civil 3D 2023 stage build gate' }

# Command declarations in the final new command classes must remain unique. We
# intentionally look for the grouped CommandMethod form, not ribbon/menu strings.
$newCommandFiles = @(
    'August10CommentClosureCommands.cs','NamibiaCoordinateRuntimeCommands.cs','RoutePlannerExpansionCommands.cs',
    'AugustBehaviorCompletionCommands.cs','RoadLayoutProductionCommands.cs','PlatformProductionCommands.cs','MultiBoundaryEditCommands.cs'
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

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$RepoRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Replace-Once {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Old,
        [Parameter(Mandatory = $true)][string]$New,
        [Parameter(Mandatory = $true)][string]$Description
    )

    $text = [System.IO.File]::ReadAllText($Path)
    if ($text.Contains($New)) {
        Write-Host "Already integrated: $Description" -ForegroundColor DarkGreen
        return
    }
    if (-not $text.Contains($Old)) {
        throw "Could not integrate '$Description'. Expected source marker was not found in $Path"
    }
    [System.IO.File]::WriteAllText(
        $Path,
        $text.Replace($Old, $New),
        [System.Text.UTF8Encoding]::new($false))
    Write-Host "Integrated: $Description" -ForegroundColor Green
}

function Replace-AllLiteral {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Old,
        [Parameter(Mandatory = $true)][string]$New,
        [Parameter(Mandatory = $true)][string]$Description
    )
    $text = [System.IO.File]::ReadAllText($Path)
    if (-not $text.Contains($Old)) {
        if ($text.Contains($New)) {
            Write-Host "Already integrated: $Description" -ForegroundColor DarkGreen
            return
        }
        throw "Could not integrate '$Description'. Expected source token was not found in $Path"
    }
    $text = $text.Replace($Old, $New)
    [System.IO.File]::WriteAllText($Path, $text, [System.Text.UTF8Encoding]::new($false))
    Write-Host "Integrated: $Description" -ForegroundColor Green
}

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$src = Join-Path $root 'src\CE.Tools.Civil3D'
$plugin = Join-Path $src 'PluginEntry.cs'
$road = Join-Path $src 'RoadLayoutProductionCommands.cs'
$platform = Join-Path $src 'PlatformProductionCommands.cs'
$drawing = Join-Path $src 'MultiBoundaryEditCommands.cs'
$closure = Join-Path $src 'August10CommentClosureCommands.cs'
$namibia = Join-Path $src 'NamibiaCoordinateRuntimeCommands.cs'
$routePlanner = Join-Path $src 'RoutePlannerExpansionCommands.cs'
$dialogs = Join-Path $src 'DisciplineWorkflowDialogs.cs'
$coordinateCommands = Join-Path $src 'FinalAllCommentsCompletionCommands.cs'
$coordination = Join-Path $src 'ProjectCoordinationCommands.cs'

foreach ($required in @($plugin, $road, $platform, $drawing, $closure, $namibia, $routePlanner, $dialogs, $coordinateCommands, $coordination)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Production/comment closure source is missing: $required"
    }
}

# Start the platform surface/feature-line monitor with the rest of the CE Tools
# runtime managers. The manager is intentionally self-idempotent.
$oldInit = @'
            UniversalDynamicRefreshManager.Initialize();
            AcApplication.Idle += OnApplicationIdle;
'@
$newInit = @'
            UniversalDynamicRefreshManager.Initialize();
            PlatformDynamicRefreshManager.EnsureInitialized();
            AcApplication.Idle += OnApplicationIdle;
'@
Replace-Once -Path $plugin -Old $oldInit -New $newInit -Description 'start platform dynamic refresh at CE Tools startup'

# AutoCAD/Civil 3D can consume Ctrl+F before WPF PreviewKeyDown. Register a
# WinForms message filter as an earlier Windows-message interception layer.
$oldShortcutInit = @'
            PlatformDynamicRefreshManager.EnsureInitialized();
            AcApplication.Idle += OnApplicationIdle;
'@
$newShortcutInit = @'
            PlatformDynamicRefreshManager.EnsureInitialized();
            AugustGlobalShortcutManager.Initialize();
            AcApplication.Idle += OnApplicationIdle;
'@
Replace-Once -Path $plugin -Old $oldShortcutInit -New $newShortcutInit -Description 'capture Ctrl+F before AutoCAD OSNAP handling'

$oldShortcutTerminate = @'
            FloatingToolsCommands.Terminate();
            CommandUsageTracker.Terminate();
'@
$newShortcutTerminate = @'
            AugustGlobalShortcutManager.Terminate();
            FloatingToolsCommands.Terminate();
            CommandUsageTracker.Terminate();
'@
Replace-Once -Path $plugin -Old $oldShortcutTerminate -New $newShortcutTerminate -Description 'remove global CE shortcut filter at termination'

# Give the explicit saved-project/drawing-settings mode real effect. Shared
# defaults are always available; drawing overrides are skipped when the user
# selects Use saved project settings.
$oldSettingsLoad = @'
            if (document != null)
                ProductionSettingsPersistenceStore.Load(document.Database, model);
'@
$newSettingsLoad = @'
            if (document != null && !CrossDrawingSettingsPreference.UseSavedProjectSettings)
                ProductionSettingsPersistenceStore.Load(document.Database, model);
'@
Replace-Once -Path $dialogs -Old $oldSettingsLoad -New $newSettingsLoad -Description 'honour saved-project versus existing-drawing settings mode'

# Namibia Schwarzeck/Lo drawings need survey Y/X coordinates, not an arbitrary
# GeoLocationData drawing frame. Route existing map/coordinate commands through
# the LO-aware runtime, which falls back to GeoLocationData for other systems.
Replace-AllLiteral -Path $coordinateCommands -Old 'GeoCoordinateTransform.TryDrawingToWgs84(' -New 'NamibiaCoordinateRuntime.TryDrawingToWgs84(' -Description 'use Namibia LO-aware drawing-to-WGS84 conversion'
Replace-AllLiteral -Path $coordinateCommands -Old 'GeoCoordinateTransform.TryWgs84ToDrawing(' -New 'NamibiaCoordinateRuntime.TryWgs84ToDrawing(' -Description 'use Namibia LO-aware WGS84-to-drawing conversion'
Replace-AllLiteral -Path $coordination -Old 'GeoCoordinateTransform.TryDrawingToWgs84(' -New 'NamibiaCoordinateRuntime.TryDrawingToWgs84(' -Description 'use Namibia LO-aware map drawing conversion'
Replace-AllLiteral -Path $coordination -Old 'GeoCoordinateTransform.TryWgs84ToDrawing(' -New 'NamibiaCoordinateRuntime.TryWgs84ToDrawing(' -Description 'use Namibia LO-aware map WGS84 conversion'

# Put preliminary road-layout production and the combined route planner at the
# front of the existing Road Production flyout.
$oldRoadMenu = @'
                        Cmd("Road Production Workflow", "CE_ROADPRODUCTION ", "Open the complete ordered road-production window."),
                        Cmd("Create Road Alignments", "CE_ROADALIGN ", "Create sequential linked road alignments from selected polylines."),
'@
$newRoadMenu = @'
                        Cmd("Road Production Workflow", "CE_ROADPRODUCTION ", "Open the complete ordered road-production window."),
                        Cmd("Road / Utility Route Planner", "CE_ROUTEPLANNER ", "Generate road-reserve and midblock preliminary routes before native Civil 3D production."),
                        Cmd("Preliminary Road Layout Production", "CE_ROADLAYOUTTOOLS ", "Create road-reserve centrelines, road edges, shoulders, bulk T/cross junctions, road names, dimensions and junction setting-out."),
                        Cmd("Create Road Alignments", "CE_ROADALIGN ", "Create sequential linked road alignments from selected polylines."),
'@
Replace-Once -Path $plugin -Old $oldRoadMenu -New $newRoadMenu -Description 'add route planner and preliminary road layout to Road Production ribbon'

# Add final annotation/interoperability tools under Drawing Tools.
$oldDrawingMenu = @'
                        Cmd("Change Objects to Colour 250", "CE_COLOR250 ", "Change selected objects to colour 250."),
                        Cmd("Polyline Direction Arrows", "CE_PLDIR ", "Add, replace or clear linked direction arrows.")),
'@
$newDrawingMenu = @'
                        Cmd("Change Objects to Colour 250", "CE_COLOR250 ", "Change selected objects to colour 250."),
                        Cmd("Polyline Direction Arrows", "CE_PLDIR ", "Add, replace or clear linked direction arrows."),
                        Cmd("Final Comment Closure Centre", "CE_COMMENTCLOSURE ", "Open overlap/restore, table navigation, interoperability, settings and final refresh tools."),
                        Cmd("Smart Annotation Overlap", "CE_OVERLAPSMART ", "Resolve only conflicting annotations with All/Selected scope and restorable original positions."),
                        Cmd("Restore Annotation Positions", "CE_ANNOTATIONRESTORE ", "Restore all/selected CE-overlap annotation positions."),
                        Cmd("Annotation Draw Order", "CE_ANNOTATIONDRAWORDER ", "Bring supported design labels to front or send them to back."),
                        Cmd("Export Civil Design to CAD Copy", "CE_EXPORTCADCOPY ", "Create a separate AutoCAD-compatible copy without changing the current design drawing."),
                        Cmd("Multiple Boundary Trim / Extend", "CE_BOUNDARYEDITTOOLS ", "Trim, trim-and-delete or extend all/selected drawing curves against multiple closed boundaries.")),
'@
Replace-Once -Path $plugin -Old $oldDrawingMenu -New $newDrawingMenu -Description 'add final comment and multi-boundary tools to Drawing ribbon'

# Add Namibia coordinate and LandXML access directly to Survey.
$oldSurveyInsert = @'
                        Cmd("Grid Setting-Out", "CE_GRIDSETTINGOUT ", "Create unique perimeter/full-grid COGO setting-out points."),
                        Cmd("Annotation Scale Sync", "CE_ANNOTATIONSCALESYNC ", "Synchronize CE annotation objects/tables to the current annotation scale."),
'@
$newSurveyInsert = @'
                        Cmd("Grid Setting-Out", "CE_GRIDSETTINGOUT ", "Create unique perimeter/full-grid COGO setting-out points."),
                        Cmd("Namibia LO / WGS84 Conversion", "CE_NAMIBIALO ", "Convert correct Schwarzeck Lo22 survey Y/X coordinates, decimal/DMS WGS84 and drawing XY."),
                        Cmd("Pick Point Coordinate Review", "CE_COORDPICKMAP ", "Pick any drawing point and review drawing XY, LO and WGS84 values."),
                        Cmd("LandXML Import / Export", "CE_LANDXMLTOOLS ", "Open Civil 3D native LandXML import/export workflows."),
                        Cmd("Annotation Scale Sync", "CE_ANNOTATIONSCALESYNC ", "Synchronize CE annotation objects/tables to the current annotation scale."),
'@
Replace-Once -Path $plugin -Old $oldSurveyInsert -New $newSurveyInsert -Description 'add Namibia coordinate and LandXML tools to Survey ribbon'

# Add multi-network and road-reserve-route handoff to Utilities.
$oldUtilityInsert = @'
                        Cmd("Cadastral Utility Planner", "CE_UTILITYPLANNER ", "Open cadastral route preparation and downstream network workflows."),
                        Cmd("Create Linked Cadastral Routes", "CE_UTILITYROUTES ", "Create inward-offset utility planning routes, manhole planning points and a constraint report."),
'@
$newUtilityInsert = @'
                        Cmd("Cadastral Utility Planner", "CE_UTILITYPLANNER ", "Open cadastral route preparation and downstream network workflows."),
                        Cmd("Road / Utility Route Planner", "CE_ROUTEPLANNER ", "Generate preliminary Roads/SW/Sewer/Water/Bulk Water route geometry."),
                        Cmd("Utility Route from Road Reserve", "CE_UTILITYFROMROADRESERVE ", "Create Sewer/SW/Water/Bulk Water routes from connected CE road-reserve centrelines."),
                        Cmd("Multiple Pipe / Structure Tools", "CE_NETWORKMULTI ", "Create, connect and schedule multiple network objects."),
                        Cmd("Create Linked Cadastral Routes", "CE_UTILITYROUTES ", "Create road-reserve/midblock utility planning routes, manhole planning points and a constraint report."),
'@
Replace-Once -Path $plugin -Old $oldUtilityInsert -New $newUtilityInsert -Description 'add route and multi-network tools to Utilities ribbon'

# Add a dedicated Platform Production flyout at the beginning of Site Design.
$oldSiteStart = @'
                "Site Design",
                Row(
                    Menu(
                        "CE_TOOLS_PARKING_MENU",
'@
$newSiteStart = @'
                "Site Design",
                Row(
                    Menu(
                        "CE_TOOLS_PLATFORM_PRODUCTION_MENU",
                        "Platform\nProduction",
                        "Create linked platform feature lines, slopes, stepped offsets, surfaces, setting-out, quantities and drawings.",
                        Cmd("Platform Production Workflow", "CE_PLATFORMTOOLS ", "Open the complete linked platform-production workflow."),
                        Cmd("Create Feature Lines", "CE_FLCREATE ", "Create feature lines from multiple polylines with popup surface selection."),
                        Cmd("Platform Slopes / Levels", "CE_PLATFORMSLOPE ", "Apply constant high-low slope, fixed slope or flatten to highest elevation."),
                        Cmd("Multiple Stepped Offsets", "CE_PLATFORMSTEPOFFSETS ", "Create linked stepped offsets for multiple platform feature lines."),
                        Cmd("Drape Steps to Surface", "CE_PLATFORMDRAPE ", "Drape linked steps to a selected surface and dynamically drive platform elevations."),
                        Cmd("Platform Site / Surface / Infill", "CE_PLATFORMSURFACE ", "Assign platforms to a site, build a separate surface and create grading infill where supported."),
                        Cmd("Platform Setting-Out", "CE_PLATFORMSETTINGOUT ", "Open vertex and grid setting-out workflows."),
                        Cmd("Platform Names", "CE_PLATFORMNAMES ", "Place PLATFORM-n labels with final platform elevations."),
                        Cmd("Linked Platform Register", "CE_PLATFORMTABLE ", "Create a linked annotative platform area/elevation register."),
                        Cmd("Platform Cut / Fill", "CE_PLATFORMCUTFILL ", "Create linked NG-versus-design cut/fill quantities."),
                        Cmd("Platform Drawings / Sections", "CE_PLATFORMDRAWINGS ", "Create platform layouts and section source lines."),
                        Cmd("Refresh Linked Platforms", "CE_PLATFORMREFRESH ", "Refresh surface links, stepped offsets, labels and tables.")),
                    Menu(
                        "CE_TOOLS_PARKING_MENU",
'@
Replace-Once -Path $plugin -Old $oldSiteStart -New $newSiteStart -Description 'add Platform Production flyout to Site Design ribbon'

# Expose the shared-settings mode and safe profile/band repair in existing menus.
$oldSettingsMenu = @'
                        Cmd("Settings Centre", "CE_SETTINGS ", "Open every discipline configuration workflow in one searchable window."),
                        Cmd("Settings Coverage Audit", "CE_SETTINGSAUDIT ", "Review the configuration workflows exposed by the settings centre."),
'@
$newSettingsMenu = @'
                        Cmd("Settings Centre", "CE_SETTINGS ", "Open every discipline configuration workflow in one searchable window."),
                        Cmd("Saved vs Drawing Settings", "CE_SETTINGSMODE ", "Choose Keep existing drawing settings or Use saved project settings for popup workflows."),
                        Cmd("Settings Coverage Audit", "CE_SETTINGSAUDIT ", "Review the configuration workflows exposed by the settings centre."),
'@
Replace-Once -Path $plugin -Old $oldSettingsMenu -New $newSettingsMenu -Description 'add saved-versus-drawing settings choice to Project ribbon'

$oldProfileMenu = @'
                        Cmd("Batch Profile Views", "CE_PROFILEVIEWBATCHTOOLS ", "Apply profile-view styles, band sets, automatic fit and rebuild options."),
                        Cmd("Profile Report", "CE_PRREPORTUI ", "Show profile details in a pop-up and optionally place a table."),
'@
$newProfileMenu = @'
                        Cmd("Batch Profile Views", "CE_PROFILEVIEWBATCHTOOLS ", "Apply profile-view styles, band sets, automatic fit and rebuild options."),
                        Cmd("Safe Profile / Band Batch", "CE_PROFILEBATCHSAFE ", "Run profile style/band repair stages independently to isolate incompatible profile views."),
                        Cmd("Profile Report", "CE_PRREPORTUI ", "Show profile details in a pop-up and optionally place a table."),
'@
Replace-Once -Path $plugin -Old $oldProfileMenu -New $newProfileMenu -Description 'add safe profile/band repair workflow'

# Verify every command expected by the inserted ribbon actually exists in the
# staged source tree before MSBuild starts.
$combined = [System.IO.File]::ReadAllText($road) + "`n" +
            [System.IO.File]::ReadAllText($platform) + "`n" +
            [System.IO.File]::ReadAllText($drawing) + "`n" +
            [System.IO.File]::ReadAllText($closure) + "`n" +
            [System.IO.File]::ReadAllText($namibia) + "`n" +
            [System.IO.File]::ReadAllText($routePlanner)
$requiredCommands = @(
    'CE_ROADLAYOUTTOOLS', 'CE_ROADRESERVECENTERLINES', 'CE_ROADEDGES', 'CE_ROADSHOULDERS', 'CE_ROADOFFSET',
    'CE_ROADJUNCTIONBULK', 'CE_ROADJUNCTIONTRIM', 'CE_ROADNAMES', 'CE_ROADDIMENSIONS', 'CE_ROADJUNCTIONSETTINGOUT', 'CE_ROADLAYOUTREFRESH',
    'CE_PLATFORMTOOLS', 'CE_PLATFORMSLOPE', 'CE_PLATFORMSTEPOFFSETS', 'CE_PLATFORMDRAPE', 'CE_PLATFORMSURFACE', 'CE_PLATFORMSETTINGOUT', 'CE_PLATFORMNAMES', 'CE_PLATFORMTABLE', 'CE_PLATFORMCUTFILL', 'CE_PLATFORMDRAWINGS', 'CE_PLATFORMREFRESH',
    'CE_BOUNDARYEDITTOOLS', 'CE_TRIMOUTSIDEMULTI', 'CE_TRIMINSIDEMULTI', 'CE_TRIMDELETEOUTSIDEMULTI', 'CE_TRIMDELETEINSIDEMULTI', 'CE_EXTENDOUTSIDEMULTI', 'CE_EXTENDINSIDEMULTI',
    'CE_COMMENTCLOSURE', 'CE_OVERLAPSMART', 'CE_ANNOTATIONRESTORE', 'CE_ANNOTATIONMASK', 'CE_ANNOTATIONDRAWORDER', 'CE_TABLESOURCEZOOM', 'CE_FLANNOTREFRESH',
    'CE_LANDXMLTOOLS', 'CE_LANDXMLIMPORT', 'CE_LANDXMLEXPORT', 'CE_EXPORTCADCOPY', 'CE_NETWORKMULTI', 'CE_SETTINGSMODE', 'CE_PROFILEBATCHSAFE', 'CE_COMMENTREFRESHALL',
    'CE_NAMIBIALO', 'CE_COORDPICKMAP', 'CE_ROUTEPLANNER', 'CE_UTILITYFROMROADRESERVE'
)
foreach ($command in $requiredCommands) {
    if (-not $combined.Contains('"' + $command + '"')) {
        throw "Production/comment closure verification failed. Missing command declaration: $command"
    }
}

$pluginText = [System.IO.File]::ReadAllText($plugin)
foreach ($ribbonCommand in @('CE_ROADLAYOUTTOOLS', 'CE_PLATFORMTOOLS', 'CE_BOUNDARYEDITTOOLS', 'CE_COMMENTCLOSURE', 'CE_NAMIBIALO', 'CE_ROUTEPLANNER', 'CE_NETWORKMULTI', 'CE_SETTINGSMODE', 'CE_PROFILEBATCHSAFE')) {
    if (-not $pluginText.Contains($ribbonCommand)) {
        throw "Production/comment closure ribbon verification failed. Missing staged ribbon command: $ribbonCommand"
    }
}
if (-not $pluginText.Contains('AugustGlobalShortcutManager.Initialize();')) { throw 'Global shortcut integration is missing.' }
if (-not ([System.IO.File]::ReadAllText($dialogs)).Contains('!CrossDrawingSettingsPreference.UseSavedProjectSettings')) { throw 'Saved-vs-drawing settings integration is missing.' }
if (-not ([System.IO.File]::ReadAllText($coordination)).Contains('NamibiaCoordinateRuntime.TryWgs84ToDrawing(')) { throw 'Namibia LO map conversion integration is missing.' }

Write-Host 'Road, platform, route-planner and final comment closure integration is ready for Civil 3D 2023 compilation.' -ForegroundColor Cyan

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

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$src = Join-Path $root 'src\CE.Tools.Civil3D'
$plugin = Join-Path $src 'PluginEntry.cs'
$road = Join-Path $src 'RoadLayoutProductionCommands.cs'
$platform = Join-Path $src 'PlatformProductionCommands.cs'
$drawing = Join-Path $src 'MultiBoundaryEditCommands.cs'

foreach ($required in @($plugin, $road, $platform, $drawing)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Production expansion source is missing: $required"
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

# Put preliminary road-layout production at the front of the existing Road
# Production flyout, before alignments/profiles/corridors.
$oldRoadMenu = @'
                        Cmd("Road Production Workflow", "CE_ROADPRODUCTION ", "Open the complete ordered road-production window."),
                        Cmd("Create Road Alignments", "CE_ROADALIGN ", "Create sequential linked road alignments from selected polylines."),
'@
$newRoadMenu = @'
                        Cmd("Road Production Workflow", "CE_ROADPRODUCTION ", "Open the complete ordered road-production window."),
                        Cmd("Preliminary Road Layout Production", "CE_ROADLAYOUTTOOLS ", "Create road-reserve centrelines, road edges, shoulders, bulk T/cross junctions, road names, dimensions and junction setting-out."),
                        Cmd("Create Road Alignments", "CE_ROADALIGN ", "Create sequential linked road alignments from selected polylines."),
'@
Replace-Once -Path $plugin -Old $oldRoadMenu -New $newRoadMenu -Description 'add preliminary road layout to Road Production ribbon'

# Add the requested multi-boundary trim/delete/extend family under Drawing Tools.
$oldDrawingMenu = @'
                        Cmd("Change Objects to Colour 250", "CE_COLOR250 ", "Change selected objects to colour 250."),
                        Cmd("Polyline Direction Arrows", "CE_PLDIR ", "Add, replace or clear linked direction arrows.")),
'@
$newDrawingMenu = @'
                        Cmd("Change Objects to Colour 250", "CE_COLOR250 ", "Change selected objects to colour 250."),
                        Cmd("Polyline Direction Arrows", "CE_PLDIR ", "Add, replace or clear linked direction arrows."),
                        Cmd("Multiple Boundary Trim / Extend", "CE_BOUNDARYEDITTOOLS ", "Trim, trim-and-delete or extend all/selected drawing curves against multiple closed boundaries.")),
'@
Replace-Once -Path $plugin -Old $oldDrawingMenu -New $newDrawingMenu -Description 'add multi-boundary editing to Drawing Tools ribbon'

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

# Verify every command expected by the inserted ribbon actually exists in the
# staged source tree before MSBuild starts. This catches accidental file loss or
# command renaming immediately instead of producing a ribbon with dead buttons.
$combined = [System.IO.File]::ReadAllText($road) + "`n" +
            [System.IO.File]::ReadAllText($platform) + "`n" +
            [System.IO.File]::ReadAllText($drawing)
$requiredCommands = @(
    'CE_ROADLAYOUTTOOLS',
    'CE_ROADRESERVECENTERLINES',
    'CE_ROADEDGES',
    'CE_ROADSHOULDERS',
    'CE_ROADOFFSET',
    'CE_ROADJUNCTIONBULK',
    'CE_ROADJUNCTIONTRIM',
    'CE_ROADNAMES',
    'CE_ROADDIMENSIONS',
    'CE_ROADJUNCTIONSETTINGOUT',
    'CE_ROADLAYOUTREFRESH',
    'CE_PLATFORMTOOLS',
    'CE_PLATFORMSLOPE',
    'CE_PLATFORMSTEPOFFSETS',
    'CE_PLATFORMDRAPE',
    'CE_PLATFORMSURFACE',
    'CE_PLATFORMSETTINGOUT',
    'CE_PLATFORMNAMES',
    'CE_PLATFORMTABLE',
    'CE_PLATFORMCUTFILL',
    'CE_PLATFORMDRAWINGS',
    'CE_PLATFORMREFRESH',
    'CE_BOUNDARYEDITTOOLS',
    'CE_TRIMOUTSIDEMULTI',
    'CE_TRIMINSIDEMULTI',
    'CE_TRIMDELETEOUTSIDEMULTI',
    'CE_TRIMDELETEINSIDEMULTI',
    'CE_EXTENDOUTSIDEMULTI',
    'CE_EXTENDINSIDEMULTI'
)
foreach ($command in $requiredCommands) {
    if (-not $combined.Contains('"' + $command + '"')) {
        throw "Production expansion verification failed. Missing command declaration: $command"
    }
}

$pluginText = [System.IO.File]::ReadAllText($plugin)
foreach ($ribbonCommand in @('CE_ROADLAYOUTTOOLS', 'CE_PLATFORMTOOLS', 'CE_BOUNDARYEDITTOOLS')) {
    if (-not $pluginText.Contains($ribbonCommand)) {
        throw "Production expansion ribbon verification failed. Missing staged ribbon command: $ribbonCommand"
    }
}

Write-Host 'Road, platform and multi-boundary production integration is ready for Civil 3D 2023 compilation.' -ForegroundColor Cyan

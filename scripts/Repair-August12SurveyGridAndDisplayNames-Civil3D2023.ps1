[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$src = Join-Path $root 'src\CE.Tools.Civil3D'

function Required([string]$name) {
    $path = Join-Path $src $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "August 12 survey-grid source missing: $path"
    }
    return $path
}
function ReadText([string]$path) {
    return [System.IO.File]::ReadAllText($path)
}
function WriteText([string]$path,[string]$text) {
    [System.IO.File]::WriteAllText($path,$text,[System.Text.UTF8Encoding]::new($false))
}
function ReplaceOnce([string]$text,[string]$old,[string]$new,[string]$description) {
    if ($text.Contains($new)) { return $text }
    if (-not $text.Contains($old)) {
        throw "Could not integrate $description. Marker not found."
    }
    return $text.Replace($old,$new)
}

# Guided Production/discipline windows: every visible command title gets CE-.
$dialogs = Required 'DisciplineWorkflowDialogs.cs'
$text = ReadText $dialogs
$old = '                Text = action.Title,'
$new = '                Text = CeCommandDisplayNames.Prefix(action.Title),'
$text = ReplaceOnce $text $old $new 'CE- prefixes in guided workflow windows'
WriteText $dialogs $text

# Full Workflow Centre: workflow-step buttons are commands too, so prefix them.
$floating = Required 'FloatingToolsWindow.cs'
$text = ReadText $floating
$old = '                        Text = (index + 1).ToString() + ". " + step.Title,'
$new = '                        Text = (index + 1).ToString() + ". " + CeCommandDisplayNames.Prefix(step.Title),'
$text = ReplaceOnce $text $old $new 'CE- prefixes on workflow-step command buttons'
WriteText $floating $text

# Dedicated Production ribbon and Survey Production window.
$production = Required 'August11ProductionCentreCommands.cs'
$text = ReadText $production
$old = '                Text = text,'
$new = '                Text = CeCommandDisplayNames.Prefix(text),'
$text = ReplaceOnce $text $old $new 'CE- prefixes on Production ribbon buttons'

$gridAnchor = '                Action("Grid Setting-Out", "CE_GRIDSETTINGOUT", "Generate linked grid/perimeter setting-out points.", "05 COMPLETE"),'
$gridInsert = @'
                Action("Grid Setting-Out", "CE_GRIDSETTINGOUT", "Generate linked grid/perimeter setting-out points.", "05 COMPLETE"),
                Action("Site Grid / Coordinate Grid", "CE_SITEGRID", "Create a linked rectangular site grid with X/Y spacing, reversible coordinate labels, paper text heights, polyline grid lines and movable linked grid points.", "05 COMPLETE"),
'@.TrimEnd("`r","`n")
if (-not $text.Contains('"CE_SITEGRID"')) {
    if (-not $text.Contains($gridAnchor)) {
        throw 'Could not add CE-Site Grid under Survey Production. Grid Setting-Out marker was not found.'
    }
    $text = $text.Replace($gridAnchor,$gridInsert)
}
WriteText $production $text

# Main CE Tools Survey ribbon and startup/termination dynamic refresh wiring.
$plugin = Required 'PluginEntry.cs'
$text = ReadText $plugin
$surveyAnchor = '                        Cmd("Grid Setting-Out", "CE_GRIDSETTINGOUT ", "Create unique perimeter/full-grid COGO setting-out points."),'
$surveyInsert = @'
                        Cmd("Grid Setting-Out", "CE_GRIDSETTINGOUT ", "Create unique perimeter/full-grid COGO setting-out points."),
                        Cmd("Site Grid / Coordinate Grid", "CE_SITEGRID ", "Create a linked site-frame coordinate grid with X/Y spacing, reversible X/Y display, selectable text height, polyline grid lines and linked movable intersections."),
                        Cmd("Refresh Site Grids", "CE_SITEGRIDREFRESH ", "Rebuild all linked CE site grids from their current frames and saved settings."),
'@.TrimEnd("`r","`n")
if (-not $text.Contains('Cmd("Site Grid / Coordinate Grid", "CE_SITEGRID "')) {
    if (-not $text.Contains($surveyAnchor)) {
        throw 'Could not expose Site Grid in the Survey ribbon. Grid Setting-Out menu marker was not found.'
    }
    $text = $text.Replace($surveyAnchor,$surveyInsert)
}

$initAnchor = '            UniversalDynamicRefreshManager.Initialize();'
$initLine = '            August12SiteGridRuntimeManager.Initialize();'
if (-not $text.Contains($initLine)) {
    if (-not $text.Contains($initAnchor)) {
        throw 'Could not wire August12SiteGridRuntimeManager.Initialize().'
    }
    $text = $text.Replace($initAnchor,$initAnchor + "`r`n" + $initLine)
}

$termAnchor = '            UniversalDynamicRefreshManager.Terminate();'
$termLine = '            August12SiteGridRuntimeManager.Terminate();'
if (-not $text.Contains($termLine)) {
    if (-not $text.Contains($termAnchor)) {
        throw 'Could not wire August12SiteGridRuntimeManager.Terminate().'
    }
    $text = $text.Replace($termAnchor,$termLine + "`r`n" + $termAnchor)
}
WriteText $plugin $text

# Final integration guard before the existing validation/build chain continues.
$siteGrid = ReadText (Required 'August12SurveySiteGridCommands.cs')
$displayNames = ReadText (Required 'CeCommandDisplayNames.cs')
$dialogsText = ReadText $dialogs
$floatingText = ReadText $floating
$productionText = ReadText $production
$pluginText = ReadText $plugin

foreach ($marker in @(
    '[CommandMethod("CE_TOOLS", "CE_SITEGRID"',
    '[CommandMethod("CE_TOOLS", "CE_SITEGRIDREFRESH"',
    'CE_SITE_GRID_PARENT',
    'CE_SITE_GRID_CHILD',
    'SpacingX',
    'SpacingY',
    'Reverse X / Y labels',
    'Paper text height',
    'new Polyline(2)',
    'new DBPoint(',
    'ObjectModified += OnObjectModified',
    'Matrix3d.Displacement(shift)')) {
    if (-not $siteGrid.Contains($marker)) {
        throw "Site-grid implementation marker missing: $marker"
    }
}
if (-not $displayNames.Contains('return "CE-" + value;')) {
    throw 'CE command display-name prefix helper is incomplete.'
}
if (-not $dialogsText.Contains('CeCommandDisplayNames.Prefix(action.Title)')) {
    throw 'Guided workflow command labels are not CE-prefixed.'
}
if (-not $floatingText.Contains('CeCommandDisplayNames.Prefix(step.Title)')) {
    throw 'Workflow Centre step commands are not CE-prefixed.'
}
if (-not $productionText.Contains('CeCommandDisplayNames.Prefix(text)') -or
    -not $productionText.Contains('"CE_SITEGRID"')) {
    throw 'Production ribbon/Survey Production integration is incomplete.'
}
foreach ($marker in @(
    'Cmd("Site Grid / Coordinate Grid", "CE_SITEGRID "',
    'Cmd("Refresh Site Grids", "CE_SITEGRIDREFRESH "',
    'August12SiteGridRuntimeManager.Initialize();',
    'August12SiteGridRuntimeManager.Terminate();')) {
    if (-not $pluginText.Contains($marker)) {
        throw "Survey ribbon/runtime integration marker missing: $marker"
    }
}

# Surface comparison workflows use one drawing-local CE popup instead of asking
# the operator to click both Civil 3D surfaces one-by-one in model space.
$surfacePopupRepair = Join-Path $root 'scripts\Repair-August12SurfaceSelectionPopup-Civil3D2023.ps1'
if (-not (Test-Path -LiteralPath $surfacePopupRepair -PathType Leaf)) {
    throw "August 12 surface-selection popup repair was not found: $surfacePopupRepair"
}
& $surfacePopupRepair -RepoRoot $root
$global:LASTEXITCODE = 0

Write-Host 'CE- prefixes now cover Production ribbon buttons, guided workflow actions and Workflow Centre steps.' -ForegroundColor Green
Write-Host 'Survey Site Grid is wired into Survey Production and the main Survey ribbon.' -ForegroundColor Green
Write-Host 'Site Grid uses linked polyline grid lines, reversible X/Y labels, spacing/text-height settings and deferred auto-refresh.' -ForegroundColor Green
Write-Host 'Surface comparison commands now choose Civil 3D surfaces from CE popup dropdowns.' -ForegroundColor Green
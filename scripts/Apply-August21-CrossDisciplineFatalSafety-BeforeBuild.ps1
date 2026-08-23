[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$utf8 = New-Object System.Text.UTF8Encoding($false)
$src = Join-Path $root 'src\CE.Tools.Civil3D'

function Required([string]$name) {
    $path = Join-Path $src $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Final fatal-safety bootstrap prerequisite missing: $path"
    }
    return $path
}
function ReadText([string]$path) {
    return [System.IO.File]::ReadAllText($path) -replace "`r?`n", "`r`n"
}
function WriteText([string]$path,[string]$text) {
    [System.IO.File]::WriteAllText($path,($text -replace "`r?`n","`r`n"),$utf8)
}
function NormalizeCommandToken(
    [string]$path,
    [string]$oldToken,
    [string]$newToken,
    [string]$label) {

    $text = ReadText $path
    if ($text.Contains($newToken)) {
        if ($text.Contains($oldToken)) {
            throw "Final fatal-safety bridge state is ambiguous for $label; old and new tokens both exist."
        }
        return
    }
    $count = ([regex]::Matches($text,[regex]::Escape($oldToken))).Count
    if ($count -ne 1) {
        throw "Final fatal-safety bridge expected one $label token but found $count."
    }
    WriteText $path ($text.Replace($oldToken,$newToken))
}
function InvokeFinalizer([string]$name,[string]$label) {
    $path = Join-Path $root ('scripts\' + $name)
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Final fatal-safety pass not found: $path"
    }
    & $path -RepoRoot $root
    if ($LASTEXITCODE -ne 0) {
        throw "$label failed with exit code $LASTEXITCODE."
    }
    $global:LASTEXITCODE = 0
}

$midblock = Required 'August11MidblockSewerProductionCommands.cs'
$road = Required 'August19RoadReserveSewerAndSafetyCommands.cs'
$geometry = Required 'August20GeometryFirstSewerCommands.cs'
$automaticRefresh = Required 'AugustAutomaticRefreshManager.cs'

# A direct developer MSBuild starts from the repository's raw source, while the
# packaged installer has already applied the historical August 20 command bridge.
# Normalize ONLY those six command tokens here. Do not call the old combined
# GeometryFirst+SiteGrid repair: its Site Grid guards intentionally target an older
# source state and must never block this final pre-compile fatal-safety boundary.
NormalizeCommandToken `
    $midblock `
    '"CE_MIDBLOCKSEWERPRODUCTION", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw' `
    '"CE_MIDBLOCKSEWERPRODUCTIONLEGACY", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw' `
    'legacy Midblock command'
NormalizeCommandToken `
    $road `
    '"CE_SEWERROADRESERVE", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw' `
    '"CE_SEWERROADRESERVELEGACY", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw' `
    'legacy Road Reserve sewer command'
NormalizeCommandToken `
    $road `
    '"CE_ROADRESERVECENTERLINESSAFE", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw' `
    '"CE_ROADRESERVECENTERLINESSAFELEGACY", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw' `
    'legacy Road Reserve centreline command'
NormalizeCommandToken `
    $geometry `
    '"CE_AUG20MIDBLOCKBRIDGE", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw' `
    '"CE_MIDBLOCKSEWERPRODUCTION", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw' `
    'geometry-first Midblock public bridge'
NormalizeCommandToken `
    $geometry `
    '"CE_AUG20ROADRESERVEBRIDGE", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw' `
    '"CE_SEWERROADRESERVE", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw' `
    'geometry-first Road Reserve sewer public bridge'
NormalizeCommandToken `
    $geometry `
    '"CE_AUG20ROADCENTERBRIDGE", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw' `
    '"CE_ROADRESERVECENTERLINESSAFE", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw' `
    'geometry-first Road Reserve centreline public bridge'

$midCheck = ReadText $midblock
$roadCheck = ReadText $road
$geometryCheck = ReadText $geometry
foreach ($required in @(
    'CE_MIDBLOCKSEWERPRODUCTIONLEGACY')) {
    if (-not $midCheck.Contains($required)) {
        throw "Final fatal-safety bridge verification missing: $required"
    }
}
foreach ($required in @(
    'CE_SEWERROADRESERVELEGACY',
    'CE_ROADRESERVECENTERLINESSAFELEGACY')) {
    if (-not $roadCheck.Contains($required)) {
        throw "Final fatal-safety bridge verification missing: $required"
    }
}
foreach ($required in @(
    '"CE_MIDBLOCKSEWERPRODUCTION"',
    '"CE_SEWERROADRESERVE"',
    '"CE_ROADRESERVECENTERLINESSAFE"')) {
    if (-not $geometryCheck.Contains($required)) {
        throw "Final fatal-safety public geometry-first bridge missing: $required"
    }
}
foreach ($legacyBridge in @(
    'CE_AUG20MIDBLOCKBRIDGE',
    'CE_AUG20ROADRESERVEBRIDGE',
    'CE_AUG20ROADCENTERBRIDGE')) {
    if ($geometryCheck.Contains($legacyBridge)) {
        throw "Final fatal-safety raw bridge token survived: $legacyBridge"
    }
}

# Historical raw source uses 'args' while one older staged repair uses 'e'. The
# Platform/relative finalizer is intentionally strict, so normalize this harmless
# parameter name at the final build boundary before that repair scans the method.
$automaticText = ReadText $automaticRefresh
$automaticText = $automaticText.Replace(
    '        private static void OnCommandEnded(object sender, CommandEventArgs args)',
    '        private static void OnCommandEnded(object sender, CommandEventArgs e)')
WriteText $automaticRefresh $automaticText
$automaticCheck = ReadText $automaticRefresh
if (-not $automaticCheck.Contains('private static void OnCommandEnded(object sender, CommandEventArgs e)')) {
    throw 'Final fatal-safety bootstrap could not normalize AugustAutomaticRefreshManager.OnCommandEnded.'
}

# Order matters: first restore the cross-discipline Sewer/Cadastral/FeatureLine
# boundary, then apply the stricter Platform/relative-feature-line/Idle boundary.
# Both repairs are idempotent and the regression executes this complete bootstrap
# twice against staged source before syntax validation.
InvokeFinalizer `
    'Repair-August21-CrossDisciplineFatalSafety-Civil3D2023.ps1' `
    'Cross-discipline fatal-safety pass'
InvokeFinalizer `
    'Repair-August21-PlatformRelativeFatalSafety-Civil3D2023.ps1' `
    'Platform/relative fatal-safety pass'

# Late compatibility passes run after every historical source mutation and are
# deliberately idempotent so the packaged installer and direct MSBuild see the
# same final Civil 3D 2023 source shape.
InvokeFinalizer `
    'Repair-August23-SiteGridRefreshReturnCompatibility-Civil3D2023.ps1' `
    'Site Grid RefreshAll return compatibility pass'
InvokeFinalizer `
    'Repair-August23-FieldGeometryFeedback-Civil3D2023.ps1' `
    'Road centreline / break-at-junction field geometry pass'

# Chain mode is part of the current Multiple Dimensions product surface. Direct
# developer MSBuild starts from raw source, while the packaged installer has
# already run this repair. Running it here is intentionally idempotent and keeps
# both build paths on the same source shape before the August 23 mixed-source pass.
InvokeFinalizer `
    'Repair-August21-PlatformPageOrderMultiDimensionTrim-Civil3D2023.ps1' `
    'Platform/page-order/chain-dimension compatibility pass'
InvokeFinalizer `
    'Repair-August23-SettingOutCadFeedback-Civil3D2023.ps1' `
    'Setting-Out / Multiple Dimensions / CAD Production feedback pass'

Write-Host 'Final Civil 3D fatal-safety boundary applied immediately before compilation.' -ForegroundColor Green
Write-Host 'Platform, linked feature-line, surface-drape, automatic-refresh, field-geometry and setting-out/CAD feedback safety are included in the final boundary.' -ForegroundColor Green

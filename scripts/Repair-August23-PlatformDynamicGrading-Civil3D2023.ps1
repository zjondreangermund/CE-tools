[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$src = Join-Path $root 'src\CE.Tools.Civil3D'
$platformPath = Join-Path $src 'PlatformProductionCommands.cs'
$dynamicPath = Join-Path $src 'August23PlatformDynamicGradingCommands.cs'
$gradeGapPath = Join-Path $src 'August23PlatformGradeAndGapCommands.cs'
$registrationPath = Join-Path $src 'August23PlatformCommandRegistration.cs'
$utf8 = New-Object System.Text.UTF8Encoding($false)

foreach ($path in @($platformPath,$dynamicPath,$gradeGapPath,$registrationPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "August 23 Platform source missing: $path"
    }
}

function ReadText([string]$path) {
    return [System.IO.File]::ReadAllText($path) -replace "`r?`n","`r`n"
}
function WriteText([string]$path,[string]$text) {
    [System.IO.File]::WriteAllText($path,($text -replace "`r?`n","`r`n"),$utf8)
}
function MethodRange([string]$text,[string]$marker,[string]$label) {
    $start = $text.IndexOf($marker,[StringComparison]::Ordinal)
    if ($start -lt 0) { throw "August 23 Platform method marker missing: $label" }
    $second = $text.IndexOf($marker,$start + $marker.Length,[StringComparison]::Ordinal)
    if ($second -ge 0) { throw "August 23 Platform method marker ambiguous: $label" }
    $open = $text.IndexOf('{',$start)
    if ($open -lt 0) { throw "August 23 Platform opening brace missing: $label" }
    $depth = 0
    $close = -1
    for ($i=$open; $i -lt $text.Length; $i++) {
        if ($text[$i] -eq '{') { $depth++ }
        elseif ($text[$i] -eq '}') {
            $depth--
            if ($depth -eq 0) { $close = $i; break }
        }
    }
    if ($close -lt 0) { throw "August 23 Platform closing brace missing: $label" }
    return @($start,$open,$close)
}
function InsertWorkflowAction(
    [string]$text,
    [string]$command,
    [string]$line,
    [string]$beforeCommand) {

    if ($text.Contains('"' + $command + '"')) { return $text }
    $needle = '"' + $beforeCommand + '"'
    $index = $text.IndexOf($needle,[StringComparison]::Ordinal)
    if ($index -lt 0) {
        throw "August 23 Platform workflow anchor missing before $beforeCommand while adding $command."
    }
    $lineStart = $text.LastIndexOf("`r`n",$index,[StringComparison]::Ordinal)
    if ($lineStart -lt 0) { $lineStart = 0 } else { $lineStart += 2 }
    return $text.Insert($lineStart,$line + "`r`n")
}

$platform = ReadText $platformPath

# Keep the existing workflow order, but expose every requested August 23 Platform
# operation in the same popup page. These insertions are semantic/idempotent and do
# not depend on one historical whitespace layout.
$platform = InsertWorkflowAction $platform 'CE_PLATFORMCONSTANTGRADE' `
    '                    new DisciplineWorkflowAction("Constant grade between fixed endpoints", "CE_PLATFORMCONSTANTGRADE", "Apply one constant grade through each selected open feature line while preserving its first and last endpoint levels.", "02 Levels"),' `
    'CE_PLATFORMSTEPOFFSETS'
$platform = InsertWorkflowAction $platform 'CE_PLATFORMCLOSEGAPS' `
    '                    new DisciplineWorkflowAction("Close stepped feature-line gaps - keep sources fixed", "CE_PLATFORMCLOSEGAPS", "Join multiple open stepped pieces into a new verified feature line without moving the source endpoints.", "03 Steps"),' `
    'CE_PLATFORMDRAPE'
$platform = InsertWorkflowAction $platform 'CE_FLCLOSEGAP' `
    '                    new DisciplineWorkflowAction("Snap one endpoint gap to a fixed anchor", "CE_FLCLOSEGAP", "Pick the endpoint that must stay fixed, then move only the selected endpoint of another feature line exactly onto it.", "03 Steps"),' `
    'CE_PLATFORMDRAPE'
$platform = InsertWorkflowAction $platform 'CE_PLATFORMDRAPEMULTI' `
    '                    new DisciplineWorkflowAction("Drape multiple feature lines to surface - dynamic", "CE_PLATFORMDRAPEMULTI", "Select one Civil 3D surface and persist safe dynamic surface links for multiple feature lines.", "04 Surface"),' `
    'CE_PLATFORMSURFACE'
$platform = InsertWorkflowAction $platform 'CE_PLATFORMGRADETOSURFACE' `
    '                    new DisciplineWorkflowAction("Grade / daylight multiple platforms to surface", "CE_PLATFORMGRADETOSURFACE", "Create dynamic cut/fill daylight feature lines to a selected surface with optional native grading infill.", "04 Surface"),' `
    'CE_PLATFORMSURFACE'

# The established PlatformDynamicRefreshManager already watches Surface and
# FeatureLine modifications and calls PlatformProductionCommands.RefreshAll(). Add
# the August 23 persistent links to that exact refresh boundary so no second event
# manager or duplicate command reactor is needed.
$refreshMarker = '        internal static int RefreshAll(Document document)'
$range = MethodRange $platform $refreshMarker 'PlatformProductionCommands.RefreshAll'
$start = [int]$range[0]
$close = [int]$range[2]
$length = $close - $start + 1
$method = $platform.Substring($start,$length)
$refreshCall = '            refreshed += August23PlatformDynamicGradingCommands.RefreshAll(document);'
if (-not $method.Contains($refreshCall)) {
    $returnMarker = '            return refreshed;'
    $returnIndex = $method.LastIndexOf($returnMarker,[StringComparison]::Ordinal)
    if ($returnIndex -lt 0) {
        throw 'August 23 Platform refresh integration could not locate the final return refreshed statement.'
    }
    $method = $method.Insert($returnIndex,$refreshCall + "`r`n")
    $platform = $platform.Substring(0,$start) + $method + $platform.Substring($start+$length)
}

WriteText $platformPath $platform

# Final semantic guards: command implementations, workflow routes and the automatic
# refresh bridge must all survive the complete historical staging pipeline.
$dynamic = ReadText $dynamicPath
$gradeGap = ReadText $gradeGapPath
$registration = ReadText $registrationPath
$finalPlatform = ReadText $platformPath

$requiredDynamic = @(
    '"CE_PLATFORMDRAPEMULTI"',
    '"CE_PLATFORMGRADETOSURFACE"',
    'internal static int RefreshAll(Document document)',
    'August21SurfaceSafety.TryApplyFeatureLineElevations',
    'TryCreateGradingGroup',
    'TryCreateInfill')
foreach ($marker in $requiredDynamic) {
    if (-not $dynamic.Contains($marker)) { throw "August 23 dynamic Platform guard failed: $marker" }
}
foreach ($marker in @(
    '"CE_PLATFORMCONSTANTGRADE"',
    '"CE_PLATFORMCLOSEGAPS"',
    'August21PlatformRelativeFatalSafety.CreateJoinedFeatureLine')) {
    if (-not $gradeGap.Contains($marker)) { throw "August 23 grade/gap Platform guard failed: $marker" }
}
if (-not $registration.Contains('CommandClass(typeof(CETools.Civil3D.August23PlatformDynamicGradingCommands))')) {
    throw 'August 23 Platform command registration guard failed.'
}
foreach ($command in @(
    'CE_PLATFORMCONSTANTGRADE',
    'CE_PLATFORMCLOSEGAPS',
    'CE_FLCLOSEGAP',
    'CE_PLATFORMDRAPEMULTI',
    'CE_PLATFORMGRADETOSURFACE')) {
    $count = ([regex]::Matches($finalPlatform,[regex]::Escape('"' + $command + '"'))).Count
    if ($count -ne 1) { throw "August 23 Platform workflow route count for $command is $count instead of 1." }
}
if (-not $finalPlatform.Contains($refreshCall)) {
    throw 'August 23 Platform automatic dynamic-refresh bridge was not installed.'
}
foreach ($legacy in @('"CE_PLATFORMDRAPE"','"CE_PLATFORMSURFACE"','"CE_PLATFORMREFRESH"')) {
    if (-not $finalPlatform.Contains($legacy)) { throw "August 23 Platform repair lost existing workflow route: $legacy" }
}

Write-Host 'August 23 Platform dynamic grading finalizer passed:' -ForegroundColor Green
Write-Host ' - multi-feature-line safe surface draping is exposed and persistent.' -ForegroundColor Green
Write-Host ' - dynamic cut/fill daylight-to-surface with optional native infill is exposed.' -ForegroundColor Green
Write-Host ' - constant grade preserves first/last endpoint levels for multiple feature lines.' -ForegroundColor Green
Write-Host ' - stepped gaps can be bridged without moving source endpoints; fixed-anchor endpoint snap remains available.' -ForegroundColor Green
Write-Host ' - existing PlatformDynamicRefreshManager now refreshes August 23 links automatically.' -ForegroundColor Green

[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$src = Join-Path $root 'src\CE.Tools.Civil3D'
$utf8 = New-Object System.Text.UTF8Encoding($false)

function Path([string]$name) {
    $value = Join-Path $src $name
    if (-not (Test-Path -LiteralPath $value -PathType Leaf)) { throw "August 27 source missing: $value" }
    return $value
}
function Read([string]$path) { [System.IO.File]::ReadAllText($path) -replace "`r?`n", "`r`n" }
function WriteFile([string]$path,[string]$text) { [System.IO.File]::WriteAllText($path,($text -replace "`r?`n","`r`n"),$utf8) }
function ReplaceMethodBody([string]$text,[string]$marker,[string]$body) {
    $start = $text.IndexOf($marker,[StringComparison]::Ordinal)
    if ($start -lt 0) { throw "Method marker missing: $marker" }
    $open = $text.IndexOf('{',$start)
    if ($open -lt 0) { throw "Opening brace missing: $marker" }
    $depth = 0; $close = -1
    for ($i=$open; $i -lt $text.Length; $i++) {
        if ($text[$i] -eq '{') { $depth++ }
        elseif ($text[$i] -eq '}') {
            $depth--
            if ($depth -eq 0) { $close=$i; break }
        }
    }
    if ($close -lt 0) { throw "Closing brace missing: $marker" }
    return $text.Substring(0,$open+1) + "`r`n" + ($body -replace "`r?`n","`r`n").Trim("`r","`n") + "`r`n        " + $text.Substring($close)
}
function EnsureStatementBeforeReturnInMethod(
    [string]$text,
    [string]$methodMarker,
    [string]$returnMarker,
    [string]$statement) {
    $start = $text.IndexOf($methodMarker,[StringComparison]::Ordinal)
    if ($start -lt 0) { throw "Method marker missing: $methodMarker" }
    $open = $text.IndexOf('{',$start)
    if ($open -lt 0) { throw "Opening brace missing: $methodMarker" }
    $depth = 0; $close = -1
    for ($i=$open; $i -lt $text.Length; $i++) {
        if ($text[$i] -eq '{') { $depth++ }
        elseif ($text[$i] -eq '}') {
            $depth--
            if ($depth -eq 0) { $close=$i; break }
        }
    }
    if ($close -lt 0) { throw "Closing brace missing: $methodMarker" }

    $methodText = $text.Substring($open+1,$close-$open-1)
    if ($methodText.Contains($statement)) { return $text }

    $returnOffset = $methodText.LastIndexOf($returnMarker,[StringComparison]::Ordinal)
    if ($returnOffset -lt 0) { throw "Return marker missing inside $methodMarker : $returnMarker" }
    $returnAbsolute = $open + 1 + $returnOffset
    $lineStart = $text.LastIndexOf("`n",$returnAbsolute)
    if ($lineStart -lt 0) { $lineStart = $open } else { $lineStart++ }
    $indentLength = 0
    while ($lineStart + $indentLength -lt $text.Length -and
           ($text[$lineStart + $indentLength] -eq ' ' -or $text[$lineStart + $indentLength] -eq "`t")) {
        $indentLength++
    }
    $indent = $text.Substring($lineStart,$indentLength)
    return $text.Insert($lineStart,$indent + $statement + "`r`n")
}
function EnsureStatementBeforeMarkerInMethod(
    [string]$text,
    [string]$methodMarker,
    [string]$beforeMarker,
    [string]$statement) {
    $start = $text.IndexOf($methodMarker,[StringComparison]::Ordinal)
    if ($start -lt 0) { throw "Method marker missing: $methodMarker" }
    $open = $text.IndexOf('{',$start)
    if ($open -lt 0) { throw "Opening brace missing: $methodMarker" }
    $depth = 0; $close = -1
    for ($i=$open; $i -lt $text.Length; $i++) {
        if ($text[$i] -eq '{') { $depth++ }
        elseif ($text[$i] -eq '}') {
            $depth--
            if ($depth -eq 0) { $close=$i; break }
        }
    }
    if ($close -lt 0) { throw "Closing brace missing: $methodMarker" }

    $methodText = $text.Substring($open+1,$close-$open-1)
    if ($methodText.Contains($statement)) { return $text }

    $beforeOffset = $methodText.IndexOf($beforeMarker,[StringComparison]::Ordinal)
    if ($beforeOffset -lt 0) { throw "Insertion marker missing inside $methodMarker : $beforeMarker" }
    $beforeAbsolute = $open + 1 + $beforeOffset
    $lineStart = $text.LastIndexOf("`n",$beforeAbsolute)
    if ($lineStart -lt 0) { $lineStart = $open + 1 } else { $lineStart++ }
    $indentLength = 0
    while ($lineStart + $indentLength -lt $text.Length -and
           ($text[$lineStart + $indentLength] -eq ' ' -or $text[$lineStart + $indentLength] -eq "`t")) {
        $indentLength++
    }
    $indent = $text.Substring($lineStart,$indentLength)
    return $text.Insert($lineStart,$indent + $statement + "`r`n")
}

# Route the three existing public menu commands to the August 27 field runtime.
$fieldPath = Path 'August24FieldCompletionCommands.cs'
$field = Read $fieldPath
$field = ReplaceMethodBody $field '        public void FeatureLineSlopeArrows()' @'
            Document document = Active();
            if (document == null) return;
            August27DynamicSlopeGridHatchCommands.FeatureLineSlopeArrows(document);
'@
$field = ReplaceMethodBody $field '        public void SurfaceSlopeArrows()' @'
            Document document = Active();
            if (document == null) return;
            August27DynamicSlopeGridHatchCommands.SurfaceSlopeArrows(document);
'@
$field = ReplaceMethodBody $field '        public void RoadHatchSides()' @'
            Document document = Active();
            if (document == null) return;
            August27DynamicSlopeGridHatchCommands.RoadHatchSides(document);
'@

# Survey Production: add crossfall-between-two-feature-lines and endpoint connection.
$surveyAnchor = '                    A("CE-Surface Slope Arrows", "CE_SURFACESLOPEARROWS", "Sample a surface and show downhill slope arrows/values.", "02 Slopes"),'
$surveyAdd = $surveyAnchor + "`r`n" +
'                    A("CE-Slope Between Two Feature Lines", "CE_FEATURELINECROSSFALLARROWS", "Dynamic leader arrows and slope values between two feature lines.", "02 Slopes"),' + "`r`n" +
'                    A("CE-Connect Selected Endpoints", "CE_CONNECTENDPOINTS", "Pick the endpoint side and connect endpoints of multiple polylines/feature lines with one polyline.", "03 Construction"),'
if (-not $field.Contains('"CE_FEATURELINECROSSFALLARROWS"')) {
    if (-not $field.Contains($surveyAnchor)) { throw 'Survey slope menu insertion anchor missing.' }
    $field = $field.Replace($surveyAnchor,$surveyAdd)
}
WriteFile $fieldPath $field

# Site-grid labels: historical staging passes can reformat/rebuild CreateLabel, so
# insert the annotative call semantically inside the method instead of depending
# on one exact three-line text anchor.
$gridPath = Path 'August12SurveySiteGridCommands.cs'
$grid = Read $gridPath
$grid = EnsureStatementBeforeReturnInMethod $grid 'private static MText CreateLabel(' 'return label;' 'PaperAnnotationScale.SetAnnotative(label);'
WriteFile $gridPath $grid

# Historical staging passes can add startup/shutdown calls between the original
# PluginEntry statements. Insert both dynamic managers semantically inside the
# actual Initialize/Terminate methods instead of requiring adjacent text anchors.
$pluginPath = Path 'PluginEntry.cs'
$plugin = Read $pluginPath
$plugin = EnsureStatementBeforeMarkerInMethod $plugin 'public void Initialize()' 'AcApplication.Idle += OnApplicationIdle;' 'August12SiteGridRuntimeManager.Initialize();'
$plugin = EnsureStatementBeforeMarkerInMethod $plugin 'public void Initialize()' 'AcApplication.Idle += OnApplicationIdle;' 'August27DynamicSlopeManager.Initialize();'
$plugin = EnsureStatementBeforeMarkerInMethod $plugin 'public void Terminate()' 'UniversalDynamicRefreshManager.Terminate();' 'August27DynamicSlopeManager.Terminate();'
$plugin = EnsureStatementBeforeMarkerInMethod $plugin 'public void Terminate()' 'UniversalDynamicRefreshManager.Terminate();' 'August12SiteGridRuntimeManager.Terminate();'
WriteFile $pluginPath $plugin

# Strict final guards. Fail the build rather than silently shipping an older staged
# body after historical August repair scripts have run.
$field = Read $fieldPath
$grid = Read $gridPath
$plugin = Read $pluginPath
$runtime = Read (Path 'August27DynamicSlopeGridHatchCommands.cs')
foreach ($token in @(
    'August27DynamicSlopeGridHatchCommands.FeatureLineSlopeArrows(document);',
    'August27DynamicSlopeGridHatchCommands.SurfaceSlopeArrows(document);',
    'August27DynamicSlopeGridHatchCommands.RoadHatchSides(document);',
    '"CE_FEATURELINECROSSFALLARROWS"',
    '"CE_CONNECTENDPOINTS"')) {
    if (-not $field.Contains($token)) { throw "August 27 staged field marker missing: $token" }
}
foreach ($token in @(
    'new Leader()',
    'leader.HasArrowHead = true;',
    'PaperAnnotationScale.SetAnnotative(leader);',
    'PaperAnnotationScale.SetAnnotative(text);',
    'CANNOSCALE',
    'ObjectModified',
    'hatch.Associative = false;',
    'settings.AddChoice("Pattern"',
    'settings.AddChoice("Colour"')) {
    if (-not $runtime.Contains($token)) { throw "August 27 dynamic runtime marker missing: $token" }
}
if (-not $grid.Contains('PaperAnnotationScale.SetAnnotative(label);')) { throw 'Site Grid label annotative guard missing.' }
foreach ($token in @(
    'August12SiteGridRuntimeManager.Initialize();',
    'August27DynamicSlopeManager.Initialize();',
    'August27DynamicSlopeManager.Terminate();',
    'August12SiteGridRuntimeManager.Terminate();')) {
    if (-not $plugin.Contains($token)) { throw "Dynamic manager startup/shutdown guard missing: $token" }
}

Write-Host 'August 27 dynamic slope/grid/road-hatch finalization complete.' -ForegroundColor Green
Write-Host 'Leader slopes, scale/elevation refresh, annotative Site Grid, safe hatch dropdowns and endpoint connector are on final field routes.' -ForegroundColor Green

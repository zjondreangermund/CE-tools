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

# Run after every older staged repair. Existing command names stay stable while the
# field routes move to the August 27 Leader/dynamic/fatal-safe implementation.
$fieldPath = Path 'August24FieldCompletionCommands.cs'
$field = Read $fieldPath
$field = ReplaceMethodBody $field '        public void FeatureLineSlopeArrows()' @'
            Document document = Active();
            if (document == null) return;
            August27DynamicSurveyGraphicsRuntime.FeatureLineSlopeArrows(document);
'@
$field = ReplaceMethodBody $field '        public void SurfaceSlopeArrows()' @'
            Document document = Active();
            if (document == null) return;
            August27DynamicSurveyGraphicsRuntime.SurfaceSlopeArrows(document);
'@
$field = ReplaceMethodBody $field '        public void RefreshSlopeArrows()' @'
            Document document = Active();
            if (document == null) return;
            August27DynamicSurveyGraphicsRuntime.RefreshAll(document);
'@
$field = ReplaceMethodBody $field '        public void RoadHatchSides()' @'
            Document document = Active();
            if (document == null) return;
            August27DynamicSurveyGraphicsRuntime.RoadSideHatch(document);
'@

# Add the new Survey Production commands without duplicating them when the finalizer
# is deliberately run more than once by regression/build staging.
if (-not $field.Contains('"CE_FEATURELINECROSSSLOPE"')) {
    $anchor = '                    A("CE-Feature-Line Dynamic Slope Arrows", "CE_FEATURELINESLOPEARROWS", "Slope arrows/values follow edited feature-line elevations.", "02 Slopes"),'
    if (-not $field.Contains($anchor)) { throw 'Survey cross-slope menu anchor missing.' }
    $insert = $anchor + "`r`n" + '                    A("CE-Feature-Line Cross Slope Leaders", "CE_FEATURELINECROSSSLOPE", "Dynamic annotative slope leaders/values between exactly two feature lines.", "02 Slopes"),'
    $field = $field.Replace($anchor,$insert)
}
if (-not $field.Contains('"CE_CONNECTSELECTEDENDPOINTS"')) {
    $anchor = '                    A("CE-Centre Construction Lines", "CE_SURVEYMIDCONSTRUCTION", "Create centre construction lines between selected curve pairs.", "03 Construction")'
    if (-not $field.Contains($anchor)) { throw 'Survey endpoint-connector menu anchor missing.' }
    $insert = $anchor + ",`r`n" + '                    A("CE-Connect Selected Endpoints", "CE_CONNECTSELECTEDENDPOINTS", "Draw one polyline through the nearest endpoints of multiple selected polylines/feature lines.", "03 Construction")'
    $field = $field.Replace($anchor,$insert)
}
WriteFile $fieldPath $field

# Site Grid is already boundary-linked and rebuilt by its dynamic manager. Make every
# newly rebuilt label genuinely annotative so moving the source boundary keeps the
# label relationship and changing annotation scale keeps the plotted text height.
$gridPath = Path 'August12SurveySiteGridCommands.cs'
$grid = Read $gridPath
if (-not $grid.Contains('PaperAnnotationScale.SetAnnotative(label);')) {
    $anchor = '            label.TextHeight = Math.Max(textHeight, 0.001);'
    if (-not $grid.Contains($anchor)) { throw 'Site Grid CreateLabel text-height anchor missing.' }
    $grid = $grid.Replace($anchor,$anchor + "`r`n            PaperAnnotationScale.SetAnnotative(label);")
}
WriteFile $gridPath $grid

# Strict release gates: fail the installer/build before compilation if an older
# repair has restored a tracked body or removed one of the new field requirements.
$field = Read $fieldPath
$grid = Read $gridPath
$runtime = Read (Path 'August27DynamicSurveyGraphicsCommands.cs')
foreach ($token in @(
    'August27DynamicSurveyGraphicsRuntime.FeatureLineSlopeArrows(document);',
    'August27DynamicSurveyGraphicsRuntime.SurfaceSlopeArrows(document);',
    'August27DynamicSurveyGraphicsRuntime.RefreshAll(document);',
    'August27DynamicSurveyGraphicsRuntime.RoadSideHatch(document);',
    '"CE_FEATURELINECROSSSLOPE"',
    '"CE_CONNECTSELECTEDENDPOINTS"')) {
    if (-not $field.Contains($token)) { throw "August 27 final field route missing: $token" }
}
foreach ($token in @(
    'new Leader()',
    'PaperAnnotationScale.SetAnnotative(leader);',
    'PaperAnnotationScale.SetAnnotative(text);',
    'document.Database.ObjectModified += OnObjectModified;',
    'document.CommandEnded += OnCommandEnded;',
    'DynamicKind.Surface',
    'DynamicKind.CrossSlope',
    'settings.AddChoice("Pattern"',
    'settings.AddChoice("Colour"',
    'TryCreateHatchStripIsolated',
    'CE_FEATURELINECROSSSLOPE',
    'CE_CONNECTSELECTEDENDPOINTS')) {
    if (-not $runtime.Contains($token)) { throw "August 27 runtime marker missing: $token" }
}
if (-not $grid.Contains('PaperAnnotationScale.SetAnnotative(label);')) {
    throw 'Site Grid label annotative final route missing.'
}

Write-Host 'August 27 dynamic Survey graphics finalization complete.' -ForegroundColor Green
Write-Host 'Feature/surface/cross slopes use dynamic annotative Leaders; Site Grid labels are annotative; road hatch is isolated and dropdown-controlled.' -ForegroundColor Green

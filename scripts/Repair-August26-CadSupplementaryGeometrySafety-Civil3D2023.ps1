[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$src = Join-Path $root 'src\CE.Tools.Civil3D'
$utf8 = New-Object System.Text.UTF8Encoding($false)

function Path([string]$name) {
    $value = Join-Path $src $name
    if (-not (Test-Path -LiteralPath $value -PathType Leaf)) { throw "August 26 CAD Supplementary source missing: $value" }
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

# This target intentionally runs after the August 25 finalizer. It is the final
# Civil 3D 2023 route for the field failures reported on August 26.
$geometryPath = Path 'August24FieldGeometryCommands.cs'
$geometry = Read $geometryPath
$geometry = ReplaceMethodBody $geometry '        public void CloseOpenMultiple()' @'
            Document document = Active();
            if (document == null) return;
            August26CadSupplementaryFieldRuntime.CloseOpenMultiple(document);
'@
$geometry = ReplaceMethodBody $geometry '        public void StretchMultipleFeatureLines()' @'
            Document document = Active();
            if (document == null) return;
            August26CadSupplementaryFieldRuntime.StretchFeatureLines(document);
'@
$geometry = ReplaceMethodBody $geometry '        public void ConstructionOffsets()' @'
            Document document = Active();
            if (document == null) return;
            August26CadSupplementaryFieldRuntime.ConstructionOffsets(document);
'@
$geometry = ReplaceMethodBody $geometry '        public void MiddleConstructionLines()' @'
            Document document = Active();
            if (document == null) return;
            August26CadSupplementaryFieldRuntime.MiddleConstructionLines(document);
'@
WriteFile $geometryPath $geometry

$runtimePath = Path 'August26CadSupplementaryFieldRuntime.cs'
$runtime = Read $runtimePath
foreach ($token in @(
    'CreateClosedFeatureLineReplacement',
    'featureLine.GetGripPoints',
    'featureLine.MoveGripPointsAt',
    'featureLine.MoveStretchPointsAt',
    'MergeCollinearSections',
    'JoinZeroFillet',
    'construction LINE entities / zero-fillet')) {
    if (-not $runtime.Contains($token)) { throw "August 26 geometry runtime marker missing: $token" }
}

$breakEngine = Read (Path 'August25CadSupplementaryBreakEngine.cs')
if (-not $breakEngine.Contains('August26CadSupplementaryBreakReplacement.TryReplaceBatch')) {
    throw 'Final Break command is not routed through August 26 all-or-none batch replacement.'
}
$breakReplacement = Read (Path 'August26CadSupplementaryBreakReplacement.cs')
foreach ($token in @(
    'Phase 1: create every candidate replacement',
    'VerifyPersistedReplacement',
    'Phase 3: erase all originals in one transaction',
    'CleanupPersisted')) {
    if (-not $breakReplacement.Contains($token)) { throw "August 26 Break safety marker missing: $token" }
}

$geometry = Read $geometryPath
foreach ($token in @(
    'August26CadSupplementaryFieldRuntime.CloseOpenMultiple(document);',
    'August26CadSupplementaryFieldRuntime.StretchFeatureLines(document);',
    'August26CadSupplementaryFieldRuntime.ConstructionOffsets(document);',
    'August26CadSupplementaryFieldRuntime.MiddleConstructionLines(document);')) {
    if (-not $geometry.Contains($token)) { throw "Final August 26 geometry route missing: $token" }
}

Write-Host 'August 26 CAD Supplementary geometry safety finalization complete.' -ForegroundColor Green
Write-Host 'Close, FeatureLine stretch, construction offsets, centre construction and multi-polyline Break are on guarded field routes.' -ForegroundColor Green

[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$src = Join-Path $root 'src\CE.Tools.Civil3D'
$utf8 = New-Object System.Text.UTF8Encoding($false)

function Path([string]$name) {
    $value = Join-Path $src $name
    if (-not (Test-Path -LiteralPath $value -PathType Leaf)) { throw "September 04 source missing: $value" }
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

# Older August finalizers deliberately remain intact for regression history. This
# final boundary runs after them and owns only the three field routes corrected on
# September 04.
$fieldPath = Path 'August24FieldGeometryCommands.cs'
$field = Read $fieldPath
$field = ReplaceMethodBody $field '        public void ConstructionOffsets()' @'
            Document document = Active();
            if (document == null) return;
            September04CadSupplementaryRuntime.ConstructionOffsets(document);
'@
$field = ReplaceMethodBody $field '        public void MiddleConstructionLines()' @'
            Document document = Active();
            if (document == null) return;
            September04CadSupplementaryRuntime.MiddleConstructionLines(document);
'@
WriteFile $fieldPath $field

$breakPath = Path 'August25CadSupplementaryBreakEngine.cs'
$break = Read $breakPath
$break = ReplaceMethodBody $break '        internal static void Run(Document document)' @'
            September04CadSupplementaryRuntime.BreakPolylinesAtJunctions(document);
'@
WriteFile $breakPath $break

# Fail the build rather than allowing an earlier staged implementation back onto
# the final Civil 3D 2023 route.
$field = Read $fieldPath
$break = Read $breakPath
$runtime = Read (Path 'September04CadSupplementaryRuntime.cs')
foreach ($token in @(
    'September04CadSupplementaryRuntime.ConstructionOffsets(document);',
    'September04CadSupplementaryRuntime.MiddleConstructionLines(document);')) {
    if (-not $field.Contains($token)) { throw "September 04 field-route marker missing: $token" }
}
if (-not $break.Contains('September04CadSupplementaryRuntime.BreakPolylinesAtJunctions(document);')) {
    throw 'September 04 break-route marker missing.'
}
foreach ($token in @(
    'new Xline()',
    'xline.BasePoint =',
    'xline.UnitDir =',
    'ReplacePolylineGeometry(source, pieces[0]);',
    'LWPOLYLINE',
    'CollectEndpointTJunctions',
    'CollectPlanStraightIntersections')) {
    if (-not $runtime.Contains($token)) { throw "September 04 runtime marker missing: $token" }
}
if ($runtime.Contains('source.Erase()') -or $runtime.Contains('target.Erase()')) {
    throw 'September 04 junction break must never erase the selected source polyline.'
}

Write-Host 'September 04 construction XLINE / junction-break finalization complete.' -ForegroundColor Green
Write-Host 'Construction offsets and centre lines are true XLINE entities; T/X breaks preserve the source polyline handle and keep every split span as a polyline.' -ForegroundColor Green

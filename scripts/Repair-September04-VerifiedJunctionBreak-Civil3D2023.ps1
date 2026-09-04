[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$src = Join-Path $root 'src\CE.Tools.Civil3D'
$core = Join-Path $root 'src\CE.Tools.Core'
$utf8 = New-Object System.Text.UTF8Encoding($false)

function Required([string]$path) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "September 04 verified-junction prerequisite missing: $path"
    }
    return $path
}

function ReadText([string]$path) {
    return [System.IO.File]::ReadAllText($path) -replace "`r?`n", "`r`n"
}

function WriteText([string]$path,[string]$text) {
    [System.IO.File]::WriteAllText($path,($text -replace "`r?`n","`r`n"),$utf8)
}

function ReplaceMethodBody(
    [string]$text,
    [string]$signature,
    [string]$body,
    [string]$label) {

    $signatureIndex = $text.IndexOf($signature,[StringComparison]::Ordinal)
    if ($signatureIndex -lt 0) { throw "$label signature not found: $signature" }
    $open = $text.IndexOf('{',$signatureIndex + $signature.Length)
    if ($open -lt 0) { throw "$label opening brace not found." }

    $depth = 0
    $close = -1
    for ($index = $open; $index -lt $text.Length; $index++) {
        $char = $text[$index]
        if ($char -eq '{') { $depth++ }
        elseif ($char -eq '}') {
            $depth--
            if ($depth -eq 0) {
                $close = $index
                break
            }
        }
    }
    if ($close -lt 0) { throw "$label closing brace not found." }

    $replacement = "{`r`n" + (($body.Trim()) -replace "`r?`n","`r`n") + "`r`n        }"
    return $text.Substring(0,$open) + $replacement + $text.Substring($close + 1)
}

$enginePath = Required (Join-Path $src 'August25CadSupplementaryBreakEngine.cs')
$runtimePath = Required (Join-Path $src 'September04VerifiedJunctionBreakRuntime.cs')
$corePath = Required (Join-Path $core 'PlanJunctionPlanner.cs')

$engine = ReadText $enginePath
$engine = ReplaceMethodBody `
    $engine `
    '        internal static void Run(Document document)' `
    '            September04VerifiedJunctionBreakRuntime.BreakPolylinesAtJunctions(document);' `
    'August25 break engine'
WriteText $enginePath $engine

$engine = ReadText $enginePath
$runtime = ReadText $runtimePath
$planner = ReadText $corePath

if (-not $engine.Contains('September04VerifiedJunctionBreakRuntime.BreakPolylinesAtJunctions(document);')) {
    throw 'Final CE_PLBREAKJUNCTIONS route is not the tested September 04 runtime.'
}
foreach ($token in @(
    'PlanJunctionPlanner.Build(',
    'August25StraightPolylineSplitter.TryBuild(',
    'SplitLineAndKeepSource(',
    'SplitPolylineAndKeepSource(',
    'Original source handles were kept',
    'LWPOLYLINE,LINE')) {
    if (-not $runtime.Contains($token)) {
        throw "Verified T/X runtime guard missing: $token"
    }
}
foreach ($token in @(
    'TrySegmentIntersection(',
    'AddProjectedEndpoint(',
    'TryStation(',
    'shared endpoint only')) {
    if (-not $planner.Contains($token)) {
        throw "Plan junction planner guard missing: $token"
    }
}
if ($runtime.Contains('.Erase(')) {
    throw 'Verified T/X runtime must not erase selected source entities.'
}

Write-Host 'September 04 verified T/X junction break finalization complete.' -ForegroundColor Green
Write-Host 'Crossings and T-junctions use tested plan geometry; selected source LINE/LWPOLYLINE handles are retained.' -ForegroundColor Green

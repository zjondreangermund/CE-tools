[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$src = Join-Path $root 'src\CE.Tools.Civil3D'
$utf8 = New-Object System.Text.UTF8Encoding($false)

function Required([string]$name) {
    $path = Join-Path $src $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "September 04 grid/break stability source missing: $path"
    }
    return $path
}
function ReadText([string]$path) {
    return [System.IO.File]::ReadAllText($path) -replace "`r?`n", "`r`n"
}
function WriteText([string]$path,[string]$text) {
    [System.IO.File]::WriteAllText($path,($text -replace "`r?`n","`r`n"),$utf8)
}
function MethodBounds([string]$text,[string]$marker) {
    $start = $text.IndexOf($marker,[StringComparison]::Ordinal)
    if ($start -lt 0) { throw "Method marker missing: $marker" }
    $open = $text.IndexOf('{',$start)
    if ($open -lt 0) { throw "Opening brace missing: $marker" }
    $depth = 0; $close = -1
    for ($i=$open; $i -lt $text.Length; $i++) {
        if ($text[$i] -eq '{') { $depth++ }
        elseif ($text[$i] -eq '}') {
            $depth--
            if ($depth -eq 0) { $close = $i; break }
        }
    }
    if ($close -lt 0) { throw "Closing brace missing: $marker" }
    return [pscustomobject]@{ Start=$start; Open=$open; Close=$close }
}
function ReplaceMethodBody([string]$text,[string]$marker,[string]$body) {
    $bounds = MethodBounds $text $marker
    $normalized = ($body -replace "`r?`n","`r`n").Trim("`r","`n")
    return $text.Substring(0,$bounds.Open+1) + "`r`n" + $normalized + "`r`n        " + $text.Substring($bounds.Close)
}
function EnsureBeforeLastTokenInMethod(
    [string]$text,
    [string]$methodMarker,
    [string]$token,
    [string]$statement,
    [string]$presence) {
    $bounds = MethodBounds $text $methodMarker
    $methodText = $text.Substring($bounds.Open+1,$bounds.Close-$bounds.Open-1)
    if ($methodText.Contains($presence)) { return $text }
    $offset = $methodText.LastIndexOf($token,[StringComparison]::Ordinal)
    if ($offset -lt 0) { throw "Insertion token missing in $methodMarker : $token" }
    $absolute = $bounds.Open + 1 + $offset
    $lineStart = $text.LastIndexOf("`n",$absolute)
    if ($lineStart -lt 0) { $lineStart = $bounds.Open + 1 } else { $lineStart++ }
    $indentLength = 0
    while ($lineStart + $indentLength -lt $text.Length -and
           ($text[$lineStart+$indentLength] -eq ' ' -or $text[$lineStart+$indentLength] -eq "`t")) {
        $indentLength++
    }
    $indent = $text.Substring($lineStart,$indentLength)
    $lines = ($statement -replace "`r?`n","`n").Split("`n")
    $rendered = ($lines | ForEach-Object { $indent + $_ }) -join "`r`n"
    return $text.Insert($lineStart,$rendered + "`r`n")
}

# -----------------------------------------------------------------------------
# 1. Universal dynamic refresh: the Grid Setting-Out command stores its source
#    boundary handles, but its own class intentionally has no ObjectModified/Idle
#    watcher. Make the universal manager actually refresh those linked grid groups.
#    Also stop CE_DYNAMICREFRESHALL and other refresh-only commands from queuing
#    themselves again after REGEN/command completion.
# -----------------------------------------------------------------------------
$universalPath = Required 'UniversalDynamicRefreshCommands.cs'
$universal = ReadText $universalPath
$universal = EnsureBeforeLastTokenInMethod `
    $universal `
    '        private static UniversalRefreshResult RefreshNow(' `
    '                _pending = false;' `
    @'
try { August18DynamicGridSettingOutCommands.RefreshAll(document); }
catch { result.Warnings++; }
'@ `
    'August18DynamicGridSettingOutCommands.RefreshAll(document);'

$universal = ReplaceMethodBody $universal '        private static void OnCommandEnded(' @'
            if (_busy || e == null) return;
            string command = NormalizeCommand(e.GlobalCommandName);
            if (IsUndoRedo(command))
            {
                // Object events raised while AutoCAD is undoing must not queue a
                // background CE refresh, otherwise the refresh becomes a new
                // undo item immediately after the user's undo.
                _undoRedoActive = false;
                _pending = false;
                _lastChangeUtc = DateTime.UtcNow;
                return;
            }

            // Refresh-only commands must terminate the queue. In particular,
            // CE_DYNAMICREFRESHALL performs its own REGEN; allowing its command-end
            // event to Queue() again creates the repeated refresh cycle seen during
            // Grid Setting-Out work.
            if (string.Equals(command, "CE_DYNAMICREFRESHALL", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command, "CE_GRIDSETTINGOUTREFRESH", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command, "CE_SITEGRIDREFRESH", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command, "CE_REFRESHALL", StringComparison.OrdinalIgnoreCase))
            {
                _pending = false;
                _lastChangeUtc = DateTime.UtcNow;
                return;
            }

            if (command.StartsWith("CE_", StringComparison.OrdinalIgnoreCase) ||
                command.StartsWith("CETOOLS", StringComparison.OrdinalIgnoreCase) ||
                command.IndexOf("GRIP", StringComparison.OrdinalIgnoreCase) >= 0 ||
                command.IndexOf("MOVE", StringComparison.OrdinalIgnoreCase) >= 0 ||
                command.IndexOf("STRETCH", StringComparison.OrdinalIgnoreCase) >= 0)
                Queue();
'@
WriteText $universalPath $universal

# -----------------------------------------------------------------------------
# 2. Break at T/X junctions: route the final command to the plan-XY runtime that
#    keeps every selected source polyline object/handle and never Erase()s it.
# -----------------------------------------------------------------------------
$breakPath = Required 'August25CadSupplementaryBreakEngine.cs'
$break = ReadText $breakPath
$break = ReplaceMethodBody $break '        internal static void Run(Document document)' @'
            September04GridBreakStabilityRuntime.BreakPolylinesAtJunctions(document);
'@
WriteText $breakPath $break

# -----------------------------------------------------------------------------
# 3. Strict final guards. This script runs after the September 04 XLINE finalizer,
#    so historical August staging cannot put the older refresh/break routes back.
# -----------------------------------------------------------------------------
$universal = ReadText $universalPath
$break = ReadText $breakPath
$siteGrid = ReadText (Required 'August12SurveySiteGridCommands.cs')
$runtime = ReadText (Required 'September04GridBreakStabilityRuntime.cs')

foreach ($token in @(
    'August18DynamicGridSettingOutCommands.RefreshAll(document);',
    '"CE_DYNAMICREFRESHALL"',
    '"CE_GRIDSETTINGOUTREFRESH"',
    '"CE_SITEGRIDREFRESH"',
    '_pending = false;')) {
    if (-not $universal.Contains($token)) {
        throw "September 04 universal-refresh guard missing: $token"
    }
}
if (-not $siteGrid.Contains('August12SurveySiteGridCommands.RefreshAll(') -or
    -not $siteGrid.Contains('DirtyIds.Add(value.ObjectId);')) {
    throw 'Dedicated Site Grid boundary/point dynamic manager is not present.'
}
if (-not $break.Contains('September04GridBreakStabilityRuntime.BreakPolylinesAtJunctions(document);')) {
    throw 'Final CE_PLBREAKJUNCTIONS route is not the September 04 keep-polyline runtime.'
}
foreach ($token in @(
    'BuildPlanPolyline',
    'Intersect.OnBothOperands',
    'CollectStraightSegmentIntersections',
    'CollectEndpointTouches',
    'ReplacePolylineGeometry(source, firstPiece);',
    'Source handles were retained')) {
    if (-not $runtime.Contains($token)) {
        throw "September 04 keep-polyline runtime guard missing: $token"
    }
}
if ($runtime.Contains('source.Erase()') -or $runtime.Contains('target.Erase()')) {
    throw 'Final T/X break runtime must never erase a selected source polyline.'
}

Write-Host 'September 04 grid refresh / T-X break stability finalization complete.' -ForegroundColor Green
Write-Host 'Dynamic Grid Setting-Out follows moved source boundaries without refresh recursion; T/X breaks preserve the selected source polyline objects.' -ForegroundColor Green

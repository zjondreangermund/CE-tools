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
        throw "September 04 Site Grid/no-loop source missing: $path"
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

$universalPath = Required 'UniversalDynamicRefreshCommands.cs'
$gridPath = Required 'August18DynamicGridSettingOutCommands.cs'
$siteGridPath = Required 'August12SurveySiteGridCommands.cs'
$universal = ReadText $universalPath

# The historical September 04 pass already links Dynamic Grid Setting-Out to the
# universal manager. Re-assert that route here because this is the last build pass.
$gridRefreshStatement = @'
try { August18DynamicGridSettingOutCommands.RefreshAll(document); }
catch { result.Warnings++; }
'@
$universal = EnsureBeforeLastTokenInMethod `
    $universal `
    '        private static UniversalRefreshResult RefreshNow(' `
    '_pending = false;' `
    $gridRefreshStatement `
    'August18DynamicGridSettingOutCommands.RefreshAll(document);'

# Add an anti-feedback window. Database events caused by CE's own grid/table/COGO
# rebuilds can arrive after a transaction/REGEN has completed; those events must
# not schedule another refresh. Real geometry commands bypass this window below.
if (-not $universal.Contains('private static DateTime _suppressQueueUntilUtc = DateTime.MinValue;')) {
    $fieldAnchor = '        private static DateTime _lastRefreshUtc = DateTime.MinValue;'
    if (-not $universal.Contains($fieldAnchor)) { throw 'Universal refresh timestamp field anchor missing.' }
    $universal = $universal.Replace(
        $fieldAnchor,
        $fieldAnchor + "`r`n        private static DateTime _suppressQueueUntilUtc = DateTime.MinValue;")
}

$queueBody = @'
            if (_busy || _undoRedoActive) return;
            DateTime now = DateTime.UtcNow;
            if (now < _suppressQueueUntilUtc) return;
            _pending = true;
            _lastChangeUtc = now;
'@
$universal = ReplaceMethodBody $universal '        internal static void Queue()' $queueBody

$commandEndedBody = @'
            if (_busy || e == null) return;
            string command = NormalizeCommand(e.GlobalCommandName);
            DateTime now = DateTime.UtcNow;

            if (IsUndoRedo(command))
            {
                _undoRedoActive = false;
                _pending = false;
                _lastChangeUtc = now;
                _suppressQueueUntilUtc = now.AddMilliseconds(750.0);
                return;
            }

            // These commands already perform their own refresh. Their command-end
            // and REGEN events must terminate the queue rather than start it again.
            if (string.Equals(command, "CE_DYNAMICREFRESHALL", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command, "CE_GRIDSETTINGOUTREFRESH", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command, "CE_SITEGRIDREFRESH", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command, "CE_REFRESHALL", StringComparison.OrdinalIgnoreCase))
            {
                _pending = false;
                _lastChangeUtc = now;
                _suppressQueueUntilUtc = now.AddMilliseconds(1000.0);
                return;
            }

            // A genuine user geometry edit must win over the anti-feedback window.
            // This is what makes a moved Site Grid boundary schedule exactly one
            // refresh even when the MOVE happens immediately after grid creation.
            bool geometryEdit =
                string.Equals(command, "MOVE", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command, "STRETCH", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command, "PEDIT", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command, "SCALE", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command, "ROTATE", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command, "ALIGN", StringComparison.OrdinalIgnoreCase) ||
                command.IndexOf("GRIP", StringComparison.OrdinalIgnoreCase) >= 0;
            if (geometryEdit)
            {
                _pending = true;
                _lastChangeUtc = now;
            }
'@
$universal = ReplaceMethodBody $universal '        private static void OnCommandEnded(' $commandEndedBody

# Keep Idle refresh internal. It must call RefreshNow directly and must never send
# CE_DYNAMICREFRESHALL back through AutoCAD's command queue.
$idleBody = @'
            Document active = AcApplication.DocumentManager.MdiActiveDocument;
            Attach(active);
            if (!Enabled || !_pending || _busy || _undoRedoActive || active == null) return;
            if ((DateTime.UtcNow - _lastChangeUtc).TotalSeconds < DelaySeconds) return;
            if ((DateTime.UtcNow - _lastRefreshUtc).TotalSeconds < 0.75) return;
            string commands = Convert.ToString(AcApplication.GetSystemVariable("CMDNAMES"), CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(commands)) return;
            int commandActive = Convert.ToInt32(
                AcApplication.GetSystemVariable("CMDACTIVE"),
                CultureInfo.InvariantCulture);
            if (commandActive != 0) return;
            RefreshNow(active, true);
'@
$universal = ReplaceMethodBody $universal '        private static void OnIdle(object sender, EventArgs e)' $idleBody

# Always leave a completed refresh with no pending work and a short suppression
# interval so delayed ObjectModified/SystemVariable/graphics events cannot recurse.
if (-not $universal.Contains('_suppressQueueUntilUtc = refreshCompletedUtc.AddMilliseconds(1000.0);')) {
    $cleanupAnchor = '                _busy = false;'
    if (-not $universal.Contains($cleanupAnchor)) { throw 'Universal refresh finally cleanup anchor missing.' }
    $cleanup = @'
                DateTime refreshCompletedUtc = DateTime.UtcNow;
                _pending = false;
                _lastChangeUtc = refreshCompletedUtc;
                _lastRefreshUtc = refreshCompletedUtc;
                _suppressQueueUntilUtc = refreshCompletedUtc.AddMilliseconds(1000.0);
'@ -replace "`r?`n","`r`n"
    $universal = $universal.Replace($cleanupAnchor,$cleanup + "`r`n" + $cleanupAnchor)
}

WriteText $universalPath $universal

# Strict guards against the exact field failure: recursive command dispatch, broad
# CE_* command-end scheduling, and a missing linked-grid refresh route.
$universal = ReadText $universalPath
$grid = ReadText $gridPath
$siteGrid = ReadText $siteGridPath

$onEndedBounds = MethodBounds $universal '        private static void OnCommandEnded('
$onEnded = $universal.Substring($onEndedBounds.Open+1,$onEndedBounds.Close-$onEndedBounds.Open-1)
if ($onEnded.Contains('command.StartsWith("CE_"') -or $onEnded.Contains('command.StartsWith("CETOOLS"')) {
    throw 'Broad CE command-end scheduling is still present and can recreate the refresh loop.'
}
foreach ($token in @(
    'private static DateTime _suppressQueueUntilUtc = DateTime.MinValue;',
    '_suppressQueueUntilUtc = now.AddMilliseconds(1000.0);',
    '_suppressQueueUntilUtc = refreshCompletedUtc.AddMilliseconds(1000.0);',
    'August18DynamicGridSettingOutCommands.RefreshAll(document);',
    'RefreshNow(active, true);')) {
    if (-not $universal.Contains($token)) { throw "Site Grid/no-loop universal guard missing: $token" }
}

# Only the private RefreshNow pass must contain exactly one Dynamic Grid refresh.
# Earlier/later staged code may legitimately expose another explicit/manual refresh
# route elsewhere in this file; counting the entire file caused a false build stop.
$refreshBounds = MethodBounds $universal '        private static UniversalRefreshResult RefreshNow('
$refreshBody = $universal.Substring($refreshBounds.Open+1,$refreshBounds.Close-$refreshBounds.Open-1)
$gridRefreshCount = ([regex]::Matches(
    $refreshBody,
    [regex]::Escape('August18DynamicGridSettingOutCommands.RefreshAll(document);'))).Count
if ($gridRefreshCount -ne 1) {
    throw "Dynamic Grid Setting-Out must be refreshed exactly once inside Universal RefreshNow; found $gridRefreshCount."
}

foreach ($token in @('RefreshOne(', 'SourceHandles', 'PointHandles')) {
    if (-not $grid.Contains($token)) { throw "Dynamic Grid linked-source guard missing: $token" }
}
foreach ($token in @('ParentKey', 'ChildKey', 'August12SiteGridRuntimeManager.Initialize();', 'DirtyIds.Add(value.ObjectId);')) {
    if (-not $siteGrid.Contains($token)) { throw "Dedicated Site Grid dynamic guard missing: $token" }
}

$recursiveCommandFiles = Get-ChildItem -LiteralPath $src -Filter '*.cs' -File | Where-Object {
    $text = ReadText $_.FullName
    $text -match '(?is)SendStringToExecute\s*\([^;]{0,500}CE_DYNAMICREFRESHALL'
}
if ($recursiveCommandFiles.Count -gt 0) {
    throw ('Automatic CE_DYNAMICREFRESHALL command dispatch remains in: ' + (($recursiveCommandFiles | Select-Object -ExpandProperty Name) -join ', '))
}

Write-Host 'September 04 Site Grid dynamic/no-loop finalization complete.' -ForegroundColor Green
Write-Host 'Boundary MOVE/STRETCH/PEDIT/SCALE/ROTATE/ALIGN schedules one linked-grid refresh; CE refresh commands and their REGEN events cannot requeue themselves.' -ForegroundColor Green

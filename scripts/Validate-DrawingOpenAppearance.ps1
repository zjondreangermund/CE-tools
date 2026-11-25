param([string]$RepoRoot = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
$source = Join-Path $RepoRoot 'src\CE.Tools.Civil3D'
# Historical 2023 staging replaces the universal object-event methods. Reapply
# only the passive-command gate after that chain; retain its source-only logic.
$universalPath = Join-Path $source 'UniversalDynamicRefreshCommands.cs'
$universal = [System.IO.File]::ReadAllText($universalPath)
if ($universal -notmatch 'DrawingOpenAppearancePolicy.IsUserEditCommand') {
    $pattern = '(private static void OnObject(?:Changed|Erased)\s*\([^)]*\)\s*\{)'
    $matches = [regex]::Matches($universal, $pattern)
    if ($matches.Count -ne 2) { throw 'Cannot safely locate both staged universal object-event handlers.' }
    $gate = @'

            if (!DrawingOpenAppearancePolicy.IsUserEditCommand(Convert.ToString(
                AcApplication.GetSystemVariable("CMDNAMES"), CultureInfo.InvariantCulture))) return;
'@
    $universal = [regex]::Replace($universal, $pattern, [System.Text.RegularExpressions.MatchEvaluator]{
        param($match)
        return $match.Value + $gate
    })
    [System.IO.File]::WriteAllText($universalPath, $universal, (New-Object System.Text.UTF8Encoding($false)))
}
$guard = Get-Content -LiteralPath (Join-Path $source 'PaperSpaceTextAnnotationGuard.cs') -Raw
if ($guard -match '(Idle|DocumentCreated|DocumentActivated)\s*\+=') {
    throw 'A legacy build repair restored automatic paper-space annotation changes. Do not install this build.'
}
$cogo = Get-Content -LiteralPath (Join-Path $source 'CogoPointProjectStyleCommands.cs') -Raw
if ($cogo -match 'private static bool _pending\s*=\s*true' -or $cogo -notmatch 'DrawingOpenAppearancePolicy.IsUserEditCommand') {
    throw 'A legacy build repair restored automatic drawing-open style refresh. Do not install this build.'
}
foreach ($name in @('UniversalDynamicRefreshCommands.cs', 'SewerNetworkDynamicSequenceManager.cs', 'CommentPresentationCommands.cs')) {
    $text = Get-Content -LiteralPath (Join-Path $source $name) -Raw
    if ($text -notmatch 'DrawingOpenAppearancePolicy.IsUserEditCommand') { throw "Missing passive-input protection in $name" }
}
Write-Host 'Saved drawing appearance startup protections verified.'

param([string]$RepoRoot = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
$source = Join-Path $RepoRoot 'src\CE.Tools.Civil3D'
# Historical staging replaces object-event handlers. Restore the passive gate
# on each handler independently while retaining the staged source-only logic.
foreach ($name in @('UniversalDynamicRefreshCommands.cs', 'SewerNetworkDynamicSequenceManager.cs', 'CommentPresentationCommands.cs')) {
    $path = Join-Path $source $name
    $text = [System.IO.File]::ReadAllText($path)
    $pattern = '(private static void OnObject(?:Changed|Erased|Modified)\s*\([^)]*\)\s*\{)(?!\s*if\s*\(!DrawingOpenAppearancePolicy)'
    $gate = @'

            if (!DrawingOpenAppearancePolicy.IsUserEditCommand(System.Convert.ToString(
                Autodesk.AutoCAD.ApplicationServices.Core.Application.GetSystemVariable("CMDNAMES"),
                System.Globalization.CultureInfo.InvariantCulture))) return;
'@
    $text = [regex]::Replace($text, $pattern, [System.Text.RegularExpressions.MatchEvaluator]{
        param($match)
        return $match.Value + $gate
    })
    [System.IO.File]::WriteAllText($path, $text, (New-Object System.Text.UTF8Encoding($false)))
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

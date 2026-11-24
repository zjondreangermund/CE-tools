param([string]$RepoRoot = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
$source = Join-Path $RepoRoot 'src\CE.Tools.Civil3D'
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

[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$src = Join-Path $root 'src\CE.Tools.Civil3D'
$utf8 = New-Object System.Text.UTF8Encoding($false)

function Patch([string]$relative,[scriptblock]$edit) {
    $path = Join-Path $src $relative
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required staged source was not found: $path" }
    $text = [System.IO.File]::ReadAllText($path)
    $updated = & $edit $text
    if ($null -eq $updated) { throw "Patch returned no content for $relative" }
    [System.IO.File]::WriteAllText($path, (($updated.ToString()) -replace "`r?`n", "`r`n"), $utf8)
}

Patch 'SurfaceComparisonLinkStore.cs' {
    param($text)
    $text.Replace('Vector3d.Zero', 'new Vector3d(0.0, 0.0, 0.0)')
}

Patch 'August14SurveySurfaceCommands.cs' {
    param($text)
    $text = $text.Replace('IEnumerable<Point3d> points)', 'Point3dCollection points)')
    $text
}

Patch 'August14SurveyFieldReviewCommands.cs' {
    param($text)
    # Keep the original XY identity in the table link. PopulateModel receives the
    # live moved XY, but the stored identity must still resolve the same DBPoint
    # on the following refresh.
    $text = $text.Replace(
        'WriteLink(table, transaction, baseId, comparisonId, refreshed);',
        'WriteLink(table, transaction, baseId, comparisonId, stored);')
    $text
}

$comparison = [System.IO.File]::ReadAllText((Join-Path $src 'SurfaceComparisonLinkStore.cs'))
if ($comparison.Contains('Vector3d.Zero')) { throw 'Civil 3D 2023 SurfaceComparisonLinkStore still contains Vector3d.Zero.' }
$surface = [System.IO.File]::ReadAllText((Join-Path $src 'August14SurveySurfaceCommands.cs'))
if (-not $surface.Contains('Point3dCollection points)')) { throw 'Survey source-point record helper was not normalized to Point3dCollection.' }
$field = [System.IO.File]::ReadAllText((Join-Path $src 'August14SurveyFieldReviewCommands.cs'))
if (-not $field.Contains('WriteLink(table, transaction, baseId, comparisonId, stored);')) { throw 'Surface comparison table did not preserve its original point identity.' }

# Older August14 compatibility passes run late from Sanitize-RestoredCSharpSources.ps1
# and can recreate the retired Project/Survey Production menus. Reapply the final
# August17 page/front-door contract here, after those legacy passes, so the staged
# source that reaches the compiler is always the approved one-page implementation.
$finalProjectSurvey = Join-Path $root 'scripts\Repair-August17-ProjectProductionComments-Civil3D2023.ps1'
if (-not (Test-Path -LiteralPath $finalProjectSurvey -PathType Leaf)) {
    throw "Final August17 Project/Survey consolidation repair was not found: $finalProjectSurvey"
}
Unblock-File -LiteralPath $finalProjectSurvey -ErrorAction SilentlyContinue
& $finalProjectSurvey -RepoRoot $root
$global:LASTEXITCODE = 0

Write-Host 'Final Survey runtime field-test compatibility follow-up passed; August17 Project/Survey contract reapplied last.' -ForegroundColor Cyan

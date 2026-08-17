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

$finalProjectSurvey = Join-Path $root 'scripts\Repair-August17-ProjectProductionComments-Civil3D2023.ps1'
if (-not (Test-Path -LiteralPath $finalProjectSurvey -PathType Leaf)) {
    throw "Final August17 Project/Survey consolidation repair was not found: $finalProjectSurvey"
}
Unblock-File -LiteralPath $finalProjectSurvey -ErrorAction SilentlyContinue
& $finalProjectSurvey -RepoRoot $root
$global:LASTEXITCODE = 0

# Final Survey PREPARE addition. The established CE_BACKGROUNDTOOLS command remains
# the existing Background/XREF manager. CE_BACKGROUNDPREPTOOLS is the new Survey
# preparation launcher and includes a handoff to that established manager.
$centresPath = Join-Path $src 'August14StructuredDisciplineProductionCentres.cs'
$centres = [System.IO.File]::ReadAllText($centresPath)
if (-not $centres.Contains('"CE_BACKGROUNDPREPTOOLS"')) {
    $landXml = '                    A("CE-LandXML Import / Export", "CE_LANDXMLTOOLS", "Import/export Civil survey data.", "02 PREPARE"),'
    $background = '                    A("CE-Background Tools", "CE_BACKGROUNDPREPTOOLS", "Burst/clean background DWGs, colour 250, freeze hatches/dimensions, verify scale to metres and open existing XREF utilities.", "02 PREPARE"),'
    if (-not $centres.Contains($landXml)) { throw 'Survey PREPARE LandXML anchor was not found for CE-Background Tools.' }
    $centres = $centres.Replace($landXml, $background + "`r`n" + $landXml)
    [System.IO.File]::WriteAllText($centresPath,($centres -replace "`r?`n","`r`n"),$utf8)
}
$centresCheck = [System.IO.File]::ReadAllText($centresPath)
if (-not $centresCheck.Contains('A("CE-Background Tools", "CE_BACKGROUNDPREPTOOLS"')) {
    throw 'CE-Background Tools preparation launcher is not exposed under Survey Production PREPARE.'
}
$backgroundSource = Join-Path $src 'BackgroundPreparationCommands.cs'
if (-not (Test-Path -LiteralPath $backgroundSource -PathType Leaf)) { throw 'BackgroundPreparationCommands.cs is missing.' }
$backgroundText = [System.IO.File]::ReadAllText($backgroundSource)
foreach ($command in @('CE_BACKGROUNDPREPTOOLS','CE_BGBURSTALL','CE_BGCOLOR250','CE_BGCLEAN','CE_BGFREEZESOLIDHATCH','CE_BGFREEZEDIMS','CE_BGSCALECORRECTION')) {
    if (-not $backgroundText.Contains($command)) { throw "Background preparation command is missing: $command" }
}
$existingBackground = Join-Path $src 'BackgroundXrefManagementCommands.cs'
if (-not (Test-Path -LiteralPath $existingBackground -PathType Leaf)) { throw 'Existing Background/XREF manager source is missing.' }
$existingText = [System.IO.File]::ReadAllText($existingBackground)
if (-not $existingText.Contains('"CE_BACKGROUNDTOOLS"')) { throw 'Existing CE_BACKGROUNDTOOLS owner is missing.' }
if (-not $backgroundText.Contains('"CE_BACKGROUNDTOOLS"')) { throw 'Survey background preparation launcher does not hand off to existing Background/XREF utilities.' }

Write-Host 'Final Survey runtime field-test compatibility follow-up passed; August17 Project/Survey contract reapplied last.' -ForegroundColor Cyan
Write-Host 'CE-Background Tools preparation is exposed under Survey Production - 02 PREPARE without duplicating the existing XREF manager.' -ForegroundColor Green

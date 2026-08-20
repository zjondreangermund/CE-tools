[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$finalizer = Join-Path $root 'scripts\Repair-August20-RuntimeStabilityWorkflowPass-Civil3D2023.ps1'
if (-not (Test-Path -LiteralPath $finalizer -PathType Leaf)) {
    throw "August 20 runtime-stability finalizer missing: $finalizer"
}

$utf8 = New-Object System.Text.UTF8Encoding($false)
$text = [System.IO.File]::ReadAllText($finalizer) -replace "`r?`n", "`r`n"

$old = @'
$grid = ReadText $gridPath
foreach ($required in @(
    'new List<string> { "<None>" }',
    'if (surface == null) return false;',
    'table.Cells[row, 2].TextString = displayX.ToString',
    'table.Cells[row, 3].TextString = displayY.ToString',
    'table.Cells[row, 5].TextString = hasBase',
    'table.Cells[row, 6].TextString = hasComparison')) {
    if (-not $grid.Contains($required)) {
        throw "Site/Grid optional-surface guard missing after staged repairs: $required"
    }
}
'@ -replace "`r?`n", "`r`n"

$new = @'
$grid = ReadText $gridPath
# Validate behavior rather than one exact C# list-construction spelling. Earlier
# staged repairs are free to use var/List/array syntax as long as Base and
# Comparison surfaces both expose <None>, X/Y are always populated, and surface
# levels remain optional additions to the linked table.
foreach ($required in @(
    '"BaseSurface"',
    '"ComparisonSurface"',
    '"<None>"',
    'if (surface == null) return false;',
    'table.Cells[row, 2].TextString = displayX.ToString',
    'table.Cells[row, 3].TextString = displayY.ToString',
    'table.Cells[row, 5].TextString = hasBase',
    'table.Cells[row, 6].TextString = hasComparison')) {
    if (-not $grid.Contains($required)) {
        throw "Site/Grid optional-surface semantic guard missing after staged repairs: $required"
    }
}
if (-not $grid.Contains('"BaseSurface", "02 Surfaces"') -or
    -not $grid.Contains('"ComparisonSurface", "02 Surfaces"')) {
    throw 'Site/Grid optional-surface semantic guard missing Base/Comparison surface popup choices.'
}
'@ -replace "`r?`n", "`r`n"

if ($text.Contains($new)) {
    Write-Host 'August 20 Site/Grid optional-surface semantic guard is already normalized.' -ForegroundColor Green
    exit 0
}

if (-not $text.Contains($old)) {
    throw 'August 20 Site/Grid optional-surface brittle validator block was not found and the semantic replacement is not present.'
}

$text = $text.Replace($old,$new)
[System.IO.File]::WriteAllText($finalizer,$text,$utf8)

$check = [System.IO.File]::ReadAllText($finalizer)
foreach ($required in @(
    'Site/Grid optional-surface semantic guard',
    '"BaseSurface"',
    '"ComparisonSurface"',
    'table.Cells[row, 2].TextString = displayX.ToString',
    'table.Cells[row, 3].TextString = displayY.ToString')) {
    if (-not $check.Contains($required)) {
        throw "August 20 Site/Grid guard normalization failed: $required"
    }
}
if ($check.Contains('Site/Grid optional-surface guard missing after staged repairs: $required')) {
    throw 'August 20 brittle Site/Grid guard survived normalization.'
}

Write-Host 'August 20 Site/Grid optional-surface validator normalized to semantic checks.' -ForegroundColor Green

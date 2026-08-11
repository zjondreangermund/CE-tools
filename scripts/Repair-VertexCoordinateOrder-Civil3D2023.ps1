[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$path = Join-Path $root 'src\CE.Tools.Civil3D\VertexSettingOutCommands.cs'
if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
    throw "Vertex setting-out source was not found: $path"
}

$text = [System.IO.File]::ReadAllText($path)

$oldTable = @'
                // Keep the numeric coordinate columns fixed and swap only their
                // displayed X/Y headings when requested. Drawing coordinates never change.
                table.Cells[row, 4].TextString = displayX
                    .ToString("N3", CultureInfo.CurrentCulture);
                table.Cells[row, 5].TextString = displayY
                    .ToString("N3", CultureInfo.CurrentCulture);
'@.TrimEnd("`r","`n")
$newTable = @'
                // The value must follow the displayed heading. Coordinate order is
                // presentation-only; the underlying Point3d / COGO XY remains unchanged.
                table.Cells[row, 4].TextString = (yFirst ? displayY : displayX)
                    .ToString("N3", CultureInfo.CurrentCulture);
                table.Cells[row, 5].TextString = (yFirst ? displayX : displayY)
                    .ToString("N3", CultureInfo.CurrentCulture);
'@.TrimEnd("`r","`n")

if ($text.Contains($oldTable)) {
    $text = $text.Replace($oldTable,$newTable)
    Write-Host 'Corrected setting-out table Y-then-X value order.' -ForegroundColor Green
}
elseif ($text.Contains('table.Cells[row, 4].TextString = (yFirst ? displayY : displayX)') -and
        $text.Contains('table.Cells[row, 5].TextString = (yFirst ? displayX : displayY)')) {
    Write-Host 'Setting-out table coordinate order is already correct.' -ForegroundColor DarkGreen
}
else {
    throw 'Setting-out table coordinate-order marker was not found.'
}

$oldLabels = @'
            string first = (yFirst ? "Y=" : "X=") +
                displayX.ToString("N3", CultureInfo.CurrentCulture);
            string second = (yFirst ? "X=" : "Y=") +
                displayY.ToString("N3", CultureInfo.CurrentCulture);
'@.TrimEnd("`r","`n")
$newLabels = @'
            string first = (yFirst ? "Y=" : "X=") +
                (yFirst ? displayY : displayX)
                    .ToString("N3", CultureInfo.CurrentCulture);
            string second = (yFirst ? "X=" : "Y=") +
                (yFirst ? displayX : displayY)
                    .ToString("N3", CultureInfo.CurrentCulture);
'@.TrimEnd("`r","`n")

if ($text.Contains($oldLabels)) {
    $text = $text.Replace($oldLabels,$newLabels)
    Write-Host 'Corrected MText/MLeader Y-then-X label value order.' -ForegroundColor Green
}
elseif ($text.Contains('(yFirst ? displayY : displayX)') -and
        $text.Contains('(yFirst ? displayX : displayY)')) {
    Write-Host 'Setting-out annotation coordinate order is already correct.' -ForegroundColor DarkGreen
}
else {
    throw 'Setting-out annotation coordinate-order marker was not found.'
}

[System.IO.File]::WriteAllText($path,$text,[System.Text.UTF8Encoding]::new($false))

# Final exact guards: labels/headings and values must use the same axis order.
$final = [System.IO.File]::ReadAllText($path)
foreach ($required in @(
    'yFirst ? "Y" : "X"',
    'yFirst ? "X" : "Y"',
    'table.Cells[row, 4].TextString = (yFirst ? displayY : displayX)',
    'table.Cells[row, 5].TextString = (yFirst ? displayX : displayY)',
    'string first = (yFirst ? "Y=" : "X=") +',
    '(yFirst ? displayY : displayX)',
    'string second = (yFirst ? "X=" : "Y=") +',
    '(yFirst ? displayX : displayY)')) {
    if (-not $final.Contains($required)) {
        throw "Vertex coordinate-order repair validation failed: missing $required"
    }
}

Write-Host 'Vertex setting-out coordinate-order repair passed.' -ForegroundColor Cyan

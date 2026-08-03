[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$RepoRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = [System.IO.Path]::GetFullPath($RepoRoot.Trim().Trim('"'))
$parking = Join-Path $root 'src\CE.Tools.Civil3D\ParkingSkewValidationCommands.cs'
if (-not (Test-Path -LiteralPath $parking -PathType Leaf)) {
    throw "Parking source was not found: $parking"
}

$text = [System.IO.File]::ReadAllText($parking)
$text = $text.Replace(
    '                        Vector2d longAxis = candidate.LongAxis;',
    '                        Point2d longAxis = candidate.LongAxis;')
$text = $text.Replace('center - longAxis * halfLength', 'center - longAxis.GetAsVector() * halfLength')
$text = $text.Replace('center + longAxis * halfLength', 'center + longAxis.GetAsVector() * halfLength')
$text = $text.Replace('shortAxis.GetAsVector()', 'shortAxis')
[System.IO.File]::WriteAllText($parking, $text, [System.Text.UTF8Encoding]::new($false))

Write-Host 'Applied final V60 parking-axis compatibility repair.' -ForegroundColor Green

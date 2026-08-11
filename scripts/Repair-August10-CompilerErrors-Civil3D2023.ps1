[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$src = Join-Path $root 'src\CE.Tools.Civil3D'

function Read-Required([string]$name) {
    $path = Join-Path $src $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Compiler compatibility source missing: $path"
    }
    return $path
}

# Civil 3D 2023 exposes FeatureLinePointType in Autodesk.Civil, not
# Autodesk.Civil.DatabaseServices. Qualify every unqualified use so this
# source compiles regardless of local using directives.
$platform = Read-Required 'PlatformProductionCommands.cs'
$platformText = [System.IO.File]::ReadAllText($platform)
$platformPattern = '(?<!Autodesk\.Civil\.)\bFeatureLinePointType\.'
$platformMatches = [regex]::Matches($platformText, $platformPattern).Count
if ($platformMatches -gt 0) {
    $platformText = [regex]::Replace(
        $platformText,
        $platformPattern,
        'Autodesk.Civil.FeatureLinePointType.')
    [System.IO.File]::WriteAllText($platform, $platformText, [System.Text.UTF8Encoding]::new($false))
    Write-Host "Qualified Civil 3D FeatureLinePointType references: $platformMatches" -ForegroundColor Green
}
else {
    Write-Host 'Platform FeatureLinePointType references are already qualified.' -ForegroundColor DarkGreen
}
$platformVerify = [System.IO.File]::ReadAllText($platform)
if ([regex]::IsMatch($platformVerify, $platformPattern)) {
    throw 'Platform FeatureLinePointType compatibility repair verification failed.'
}

# Civil 3D feature lines can expose PI points, elevation points and a closing
# point through FeatureLinePointType.AllPoints. Using that collection index as
# the SetPointElevation(index, ...) index can throw ArgumentOutOfRangeException.
# Normalize platform elevation edits to the point-based API instead.
$platformText = [System.IO.File]::ReadAllText($platform)
$oldAbsoluteElevation = @'
        private static void SetAbsoluteElevation(CivilFeatureLine featureLine, Point3d point, int index, double elevation)
        {
            if (featureLine.IsElevationRelativeToSurface(point)) featureLine.SetPointRelativeElevation(point, false, elevation);
            else featureLine.SetPointElevation(index, elevation);
        }
'@
$newAbsoluteElevation = @'
        private static void SetAbsoluteElevation(CivilFeatureLine featureLine, Point3d point, int index, double elevation)
        {
            if (featureLine == null) return;
            // Use the point-based setter for PI, elevation and closing points.
            // This also intentionally converts a surface-relative point to an
            // absolute elevation when a production level/slope is applied.
            featureLine.SetPointRelativeElevation(point, false, elevation);
        }
'@
$oldAbsoluteElevationValue = $oldAbsoluteElevation.TrimEnd("`r", "`n")
$newAbsoluteElevationValue = $newAbsoluteElevation.TrimEnd("`r", "`n")
$platformRuntimeChanged = $false
if ($platformText.Contains($oldAbsoluteElevationValue)) {
    $platformText = $platformText.Replace($oldAbsoluteElevationValue, $newAbsoluteElevationValue)
    $platformRuntimeChanged = $true
}

$oldChildElevation = '                child.SetPointElevation(index, sourcePoint.Z + dz);'
$newChildElevation = '                child.SetPointRelativeElevation(point, false, sourcePoint.Z + dz);'
if ($platformText.Contains($oldChildElevation)) {
    $platformText = $platformText.Replace($oldChildElevation, $newChildElevation)
    $platformRuntimeChanged = $true
}

if ($platformRuntimeChanged) {
    [System.IO.File]::WriteAllText($platform, $platformText, [System.Text.UTF8Encoding]::new($false))
    Write-Host 'Repaired platform FeatureLine elevation updates to avoid AllPoints index errors.' -ForegroundColor Green
}
else {
    Write-Host 'Platform point-based elevation runtime repair is already applied.' -ForegroundColor DarkGreen
}
$platformVerify = [System.IO.File]::ReadAllText($platform)
if ($platformVerify.Contains('else featureLine.SetPointElevation(index, elevation);') -or
    $platformVerify.Contains('child.SetPointElevation(index, sourcePoint.Z + dz);')) {
    throw 'Platform point-based elevation runtime repair verification failed.'
}

# Autodesk.AutoCAD.Runtime also defines Exception. Keep the runtime namespace
# for CommandMethod/CommandFlags but explicitly use System.Exception here.
$profile = Read-Required 'ProfileStyleAutoImportRuntime.cs'
$profileText = [System.IO.File]::ReadAllText($profile)
$profileText = [regex]::Replace(
    $profileText,
    '(?m)(?<![A-Za-z0-9_.])Exception\s+inner\s*=',
    'System.Exception inner =')
$profileText = [regex]::Replace(
    $profileText,
    'catch\s*\(\s*Exception\s+exception\s*\)',
    'catch (System.Exception exception)')
[System.IO.File]::WriteAllText($profile, $profileText, [System.Text.UTF8Encoding]::new($false))
if ([regex]::IsMatch($profileText, '(?m)(?<![A-Za-z0-9_.])Exception\s+inner\s*=') -or
    [regex]::IsMatch($profileText, 'catch\s*\(\s*Exception\s+exception\s*\)')) {
    throw 'Profile style System.Exception compatibility repair verification failed.'
}
Write-Host 'Qualified System.Exception references in profile style auto-import runtime.' -ForegroundColor Green

# AutoCAD/Civil 3D 2023 exposes Table cell TextHeight as Nullable<double>.
# Accept both the original Math.Max form and the first non-nullable repair form,
# then normalize them to nullable-safe handling before MSBuild.
$table = Read-Required 'TablePresentationRepairCommands.cs'
$tableText = [System.IO.File]::ReadAllText($table)
$oldMathMax = 'try { value = Math.Max(value, table.Cells[row, column].TextHeight); }'
$oldNonNullable = @'
try
                    {
                        double cellTextHeight = table.Cells[row, column].TextHeight;
                        if (cellTextHeight > value) value = cellTextHeight;
                    }
'@
$nullableSafe = @'
try
                    {
                        double? cellTextHeight = table.Cells[row, column].TextHeight;
                        if (cellTextHeight.HasValue && cellTextHeight.Value > value)
                            value = cellTextHeight.Value;
                    }
'@

$replacement = $nullableSafe.TrimEnd("`r", "`n")
$changedTableText = $false
if ($tableText.Contains($oldMathMax)) {
    $tableText = $tableText.Replace($oldMathMax, $replacement)
    $changedTableText = $true
}
$oldNonNullableValue = $oldNonNullable.TrimEnd("`r", "`n")
if ($tableText.Contains($oldNonNullableValue)) {
    $tableText = $tableText.Replace($oldNonNullableValue, $replacement)
    $changedTableText = $true
}
if ($changedTableText) {
    [System.IO.File]::WriteAllText($table, $tableText, [System.Text.UTF8Encoding]::new($false))
    Write-Host 'Repaired nullable Table TextHeight handling for Civil 3D 2023.' -ForegroundColor Green
}
else {
    Write-Host 'Nullable Table TextHeight compatibility repair is already applied.' -ForegroundColor DarkGreen
}
if (-not $tableText.Contains('double? cellTextHeight = table.Cells[row, column].TextHeight;') -or
    -not $tableText.Contains('cellTextHeight.HasValue') -or
    -not $tableText.Contains('cellTextHeight.Value')) {
    throw 'Nullable Table TextHeight compatibility repair verification failed.'
}

Write-Host 'Final August Civil 3D 2023 compiler compatibility repair passed.' -ForegroundColor Cyan

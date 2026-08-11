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

# Platform production must support BOTH ordinary absolute feature lines and
# feature lines that are genuinely relative to a Civil surface. Autodesk's
# SetPointRelativeElevation(point, false, elevation) throws when no relative
# surface exists, while SetPointElevation(index, elevation) is the correct API
# for normal absolute feature points. Closed AllPoints collections may also
# expose a duplicate closing point after PointsCount, so map that duplicate back
# to the matching real feature point instead of passing an invalid index.
$platformText = [System.IO.File]::ReadAllText($platform)
$helperPattern = '(?s)        private static void SetAbsoluteElevation\(CivilFeatureLine featureLine, Point3d point, int index, double elevation\)\s*        \{.*?\n        \}\s*(?=\n        private static void WriteStep)'
$safeHelper = @'
        private static void SetAbsoluteElevation(CivilFeatureLine featureLine, Point3d point, int index, double elevation)
        {
            if (featureLine == null) return;

            ObjectId relativeSurfaceId = ObjectId.Null;
            try { relativeSurfaceId = featureLine.RelativeSurfaceId; } catch { }

            if (!relativeSurfaceId.IsNull)
            {
                try
                {
                    // This is only valid when Civil 3D confirms the feature line
                    // actually has a relative surface. Setting relative=false
                    // intentionally converts the edited point to an absolute level.
                    featureLine.SetPointRelativeElevation(point, false, elevation);
                    return;
                }
                catch
                {
                    // Fall back to the indexed absolute API below. Civil 3D can
                    // reject geometrically equivalent closing points here.
                }
            }

            int pointCount = 0;
            try { pointCount = featureLine.PointsCount; } catch { }

            if (index >= 0 && index < pointCount)
            {
                featureLine.SetPointElevation(index, elevation);
                return;
            }

            // A closed FeatureLinePointType.AllPoints collection can expose the
            // closing point after PointsCount. Map that point to the matching
            // real feature point (normally the first vertex) instead of throwing.
            Point3dCollection allPoints = featureLine.GetPoints(Autodesk.Civil.FeatureLinePointType.AllPoints);
            int limit = Math.Min(pointCount, allPoints == null ? 0 : allPoints.Count);
            for (int candidate = 0; candidate < limit; candidate++)
            {
                Point3d existing = allPoints[candidate];
                double dx = existing.X - point.X;
                double dy = existing.Y - point.Y;
                if (Math.Sqrt(dx * dx + dy * dy) <= Tol)
                {
                    featureLine.SetPointElevation(candidate, elevation);
                    return;
                }
            }
        }
'@
$helperRegex = [regex]::new($helperPattern,[System.Text.RegularExpressions.RegexOptions]::Singleline)
if (-not $helperRegex.IsMatch($platformText)) {
    throw 'Platform SetAbsoluteElevation helper could not be isolated for runtime repair.'
}
$platformText = $helperRegex.Replace($platformText,$safeHelper.TrimEnd("`r","`n"),1)

# Reuse the same safe helper for new stepped-offset feature lines. Historical
# repairs changed this to SetPointRelativeElevation unconditionally and caused
# the same "Featureline is not associated with surface" runtime exception.
$platformText = $platformText.Replace(
    '                child.SetPointRelativeElevation(point, false, sourcePoint.Z + dz);',
    '                SetAbsoluteElevation(child, point, index, sourcePoint.Z + dz);')
$platformText = $platformText.Replace(
    '                child.SetPointElevation(index, sourcePoint.Z + dz);',
    '                SetAbsoluteElevation(child, point, index, sourcePoint.Z + dz);')

[System.IO.File]::WriteAllText($platform, $platformText, [System.Text.UTF8Encoding]::new($false))
$platformVerify = [System.IO.File]::ReadAllText($platform)
if (-not $platformVerify.Contains('relativeSurfaceId = featureLine.RelativeSurfaceId;') -or
    -not $platformVerify.Contains('pointCount = featureLine.PointsCount;') -or
    -not $platformVerify.Contains('SetAbsoluteElevation(child, point, index, sourcePoint.Z + dz);')) {
    throw 'Platform absolute/relative elevation runtime repair verification failed.'
}
if ($platformVerify.Contains('child.SetPointRelativeElevation(point, false, sourcePoint.Z + dz);')) {
    throw 'Unsafe surface-relative stepped-offset elevation call remains after repair.'
}
Write-Host 'Repaired platform elevations for both absolute and surface-relative feature lines without closing-point index errors.' -ForegroundColor Green

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

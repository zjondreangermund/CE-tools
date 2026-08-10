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

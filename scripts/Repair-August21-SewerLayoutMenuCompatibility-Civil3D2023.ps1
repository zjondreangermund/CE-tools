[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$path = Join-Path $root 'src\CE.Tools.Civil3D\August14StructuredDisciplineProductionCentres.cs'
if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
    throw "Sewer Layout Production source missing for geometry-only menu compatibility repair: $path"
}

$utf8 = New-Object System.Text.UTF8Encoding($false)
$text = [System.IO.File]::ReadAllText($path) -replace "`r?`n", "`r`n"

$midSafePattern = 'A\(\s*"CE-Midblock Sewer Route"\s*,\s*"CE_SEWERLAYOUTMIDBLOCK"'
$roadSafePattern = 'A\(\s*"CE-Road Reserve Sewer Route"\s*,\s*"CE_SEWERLAYOUTROADRESERVE"'
$legacyCombinedPattern = '(?m)^\s*A\(\s*"CE-Midblock / Road-Reserve Sewer Route"\s*,\s*"[^"]+"\s*,[^\r\n]*\),\s*$'

$midSafe = [regex]::Matches($text,$midSafePattern).Count
$roadSafe = [regex]::Matches($text,$roadSafePattern).Count
$legacyCombined = [regex]::Matches($text,$legacyCombinedPattern).Count

if ($midSafe -eq 1 -and $roadSafe -eq 1 -and $legacyCombined -eq 0) {
    # Already semantically safe. Exact formatting is normalized below.
}
elseif ($midSafe -eq 0 -and $roadSafe -eq 0 -and $legacyCombined -eq 1) {
    $replacement = @'
                    A("CE-Midblock Sewer Route", "CE_SEWERLAYOUTMIDBLOCK", "Create geometry-only Midblock sewer planning polylines/manhole circles. Civil network conversion is separate.", "02 PREPARE"),
                    A("CE-Road Reserve Sewer Route", "CE_SEWERLAYOUTROADRESERVE", "Create geometry-only Road Reserve sewer planning polylines/manhole circles. Civil network conversion is separate.", "02 PREPARE"),
'@ -replace "`r?`n", "`r`n"
    $text = [regex]::Replace($text,$legacyCombinedPattern,$replacement.TrimEnd("`r","`n"))
}
else {
    # A late staged repair can leave separate actions with an older command target.
    # Normalize the action commands by title before deciding the state is invalid.
    $text = [regex]::Replace(
        $text,
        'A\(\s*"CE-Midblock Sewer Route"\s*,\s*"[^"]+"',
        'A("CE-Midblock Sewer Route", "CE_SEWERLAYOUTMIDBLOCK"')
    $text = [regex]::Replace(
        $text,
        'A\(\s*"CE-Road Reserve Sewer Route"\s*,\s*"[^"]+"',
        'A("CE-Road Reserve Sewer Route", "CE_SEWERLAYOUTROADRESERVE"')

    $midSafe = [regex]::Matches($text,$midSafePattern).Count
    $roadSafe = [regex]::Matches($text,$roadSafePattern).Count
    $legacyCombined = [regex]::Matches($text,$legacyCombinedPattern).Count
    if ($midSafe -ne 1 -or $roadSafe -ne 1 -or $legacyCombined -ne 0) {
        throw "Sewer Layout menu compatibility found unexpected state: midblock=$midSafe roadReserve=$roadSafe legacyCombined=$legacyCombined."
    }
}

# Force the exact quoted command tokens used by the preserved field-recovery guard.
$text = [regex]::Replace(
    $text,
    'A\(\s*"CE-Midblock Sewer Route"\s*,\s*"CE_SEWERLAYOUTMIDBLOCK"',
    'A("CE-Midblock Sewer Route", "CE_SEWERLAYOUTMIDBLOCK"')
$text = [regex]::Replace(
    $text,
    'A\(\s*"CE-Road Reserve Sewer Route"\s*,\s*"CE_SEWERLAYOUTROADRESERVE"',
    'A("CE-Road Reserve Sewer Route", "CE_SEWERLAYOUTROADRESERVE"')

[System.IO.File]::WriteAllText($path,$text,$utf8)

$check = [System.IO.File]::ReadAllText($path)
foreach ($required in @('"CE_SEWERLAYOUTMIDBLOCK"','"CE_SEWERLAYOUTROADRESERVE"')) {
    if (-not $check.Contains($required)) {
        throw "Sewer Layout geometry-only compatibility verification failed: $required"
    }
}
if ([regex]::IsMatch($check,'A\(\s*"CE-Midblock / Road-Reserve Sewer Route"')) {
    throw 'Legacy combined Midblock/Road-Reserve route survived Sewer Layout compatibility normalization.'
}

Write-Host 'Sewer Layout geometry-only Midblock/Road Reserve routes normalized for field recovery.' -ForegroundColor Green

[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$path = Join-Path $root 'src\CE.Tools.Civil3D\August12SurveySiteGridCommands.cs'
if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
    throw "Site Grid source missing for append compatibility repair: $path"
}

$utf8 = New-Object System.Text.UTF8Encoding($false)
$text = [System.IO.File]::ReadAllText($path) -replace "`r?`n", "`r`n"
$markers = @(
    '        private static ObjectId Append(',
    '        private static void Append('
)
$found = @($markers | Where-Object { $text.Contains($_) })
if ($found.Count -eq 0) {
    throw 'Site Grid append compatibility marker not found. Expected ObjectId or void Append helper.'
}
if ($found.Count -gt 1) {
    throw 'Site Grid append compatibility marker is ambiguous; both ObjectId and void Append helpers are present.'
}

$start = $text.IndexOf($found[0],[StringComparison]::Ordinal)
$open = $text.IndexOf('{',$start)
if ($open -lt 0) {
    throw 'Site Grid append compatibility opening brace not found.'
}
$depth = 0
$close = -1
for ($i=$open; $i -lt $text.Length; $i++) {
    if ($text[$i] -eq '{') { $depth++ }
    elseif ($text[$i] -eq '}') {
        $depth--
        if ($depth -eq 0) { $close = $i; break }
    }
}
if ($close -lt 0) {
    throw 'Site Grid append compatibility closing brace not found.'
}

$replacement = @'
        private static ObjectId Append(
            BlockTableRecord modelSpace,
            Transaction transaction,
            Entity entity)
        {
            ObjectId id = modelSpace.AppendEntity(entity);
            transaction.AddNewlyCreatedDBObject(entity, true);
            try { entity.RecordGraphicsModified(true); }
            catch { }
            return id;
        }
'@ -replace "`r?`n", "`r`n"

$text = $text.Substring(0,$start) + $replacement.TrimEnd("`r","`n") + $text.Substring($close + 1)
[System.IO.File]::WriteAllText($path,$text,$utf8)

$check = [System.IO.File]::ReadAllText($path)
foreach ($required in @(
    'private static ObjectId Append(',
    'ObjectId id = modelSpace.AppendEntity(entity);',
    'entity.RecordGraphicsModified(true);',
    'return id;')) {
    if (-not $check.Contains($required)) {
        throw "Site Grid append compatibility verification failed: $required"
    }
}
if ($check.Contains('private static void Append(')) {
    throw 'Site Grid void Append helper survived compatibility normalization.'
}

Write-Host 'Site Grid Append compatibility normalized for the August 20 field-recovery pass.' -ForegroundColor Green

# The historical field-recovery pass that follows this compatibility bridge also
# expects CE_SEWSEQ to already contain deferred CE_SEWLABELS queue points. Normalize
# both the older direct EnsureLabels form and the August 20 sequence-only safety form
# before handing control back to that preserved field-recovery script.
$sewerCompat = Join-Path $root 'scripts\Repair-August21-SewerSequenceDeferredLabelsCompatibility-Civil3D2023.ps1'
if (-not (Test-Path -LiteralPath $sewerCompat -PathType Leaf)) {
    throw "Sewer Sequence deferred-label compatibility repair missing: $sewerCompat"
}
& $sewerCompat -RepoRoot $root
$global:LASTEXITCODE = 0

# Road Production was historically generated with compact argument formatting
# (no spaces after commas), while the August 20 field-recovery pass checks an
# exact spaced CE_ROADCENTERLINEPOLY menu string. Normalize the semantic route
# before field recovery so either formatting/legacy command shape is accepted.
$roadCompat = Join-Path $root 'scripts\Repair-August21-RoadCentrelineMenuCompatibility-Civil3D2023.ps1'
if (-not (Test-Path -LiteralPath $roadCompat -PathType Leaf)) {
    throw "Road Reserve centreline menu compatibility repair missing: $roadCompat"
}
& $roadCompat -RepoRoot $root
$global:LASTEXITCODE = 0

# Sewer Layout can likewise arrive here as the older combined Midblock/Road-Reserve
# action or as separate actions with different formatting/targets. Normalize it to
# the two geometry-only public commands required by the preserved field-recovery guard.
$sewerLayoutCompat = Join-Path $root 'scripts\Repair-August21-SewerLayoutMenuCompatibility-Civil3D2023.ps1'
if (-not (Test-Path -LiteralPath $sewerLayoutCompat -PathType Leaf)) {
    throw "Sewer Layout geometry-only menu compatibility repair missing: $sewerLayoutCompat"
}
& $sewerLayoutCompat -RepoRoot $root
$global:LASTEXITCODE = 0

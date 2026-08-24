[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$sourcePath = Join-Path $root 'scripts\Repair-August23-SettingOutCadFeedback-Civil3D2023.ps1'
$tempPath = Join-Path $root 'scripts\.Repair-August23-SettingOutCadFeedback.runtime.ps1'
$cadRoute = Join-Path $root 'scripts\Repair-August23-CadProductionRouteCompatibility-Civil3D2023.ps1'
$gridAnchor = Join-Path $root 'scripts\Repair-August23-GridSettingAnchorPreflight-Civil3D2023.ps1'
$vertexLabelGuard = Join-Path $root 'scripts\Repair-August23-VertexLabelGuardPreflight-Civil3D2023.ps1'
if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
    throw "August 23 setting-out feedback repair missing: $sourcePath"
}
if (-not (Test-Path -LiteralPath $cadRoute -PathType Leaf)) {
    throw "August 23 CAD Production route compatibility repair missing: $cadRoute"
}
if (-not (Test-Path -LiteralPath $gridAnchor -PathType Leaf)) {
    throw "August 23 Grid Setting-Out anchor preflight missing: $gridAnchor"
}
if (-not (Test-Path -LiteralPath $vertexLabelGuard -PathType Leaf)) {
    throw "August 23 Vertex Setting-Out label-guard preflight missing: $vertexLabelGuard"
}

# Production Centre titles have evolved across the staged August repairs. Normalize
# the semantic CAD discipline route first so the larger feedback pass does not rely
# on a historical exact-text insertion marker.
& $cadRoute -RepoRoot $root
if ($LASTEXITCODE -ne 0) {
    throw "August 23 CAD Production route compatibility repair failed with exit code $LASTEXITCODE."
}
$global:LASTEXITCODE = 0

# The packaged installer may already have changed the Prefix setting's group/title
# formatting before this final feedback pass. The authored August 23 repair still
# uses that setting as its insertion point for GridLines/NG/Design controls, so
# restore only that one semantic settings call to the canonical anchor first.
& $gridAnchor -RepoRoot $root
if ($LASTEXITCODE -ne 0) {
    throw "August 23 Grid Setting-Out anchor preflight failed with exit code $LASTEXITCODE."
}
$global:LASTEXITCODE = 0

# The packaged August 18/19 mutation chain can remove or reformat one Vertex COGO
# PointName assignment before this final pass. Establish the created/updated COGO
# label-reset calls semantically so the final guard does not depend on that older
# exact source shape. The larger repair still owns the helper implementation.
& $vertexLabelGuard -RepoRoot $root
if ($LASTEXITCODE -ne 0) {
    throw "August 23 Vertex Setting-Out label-guard preflight failed with exit code $LASTEXITCODE."
}
$global:LASTEXITCODE = 0

# PowerShell 7 can bind a here-string followed by -replace inside String.Replace(...)
# to the three-argument StringComparison overload. The authored repair is kept
# readable, while this runtime bridge parenthesizes those replacement expressions
# before invocation. The transformation is deterministic and does not touch C#.
$lines = [System.IO.File]::ReadAllLines($sourcePath)
$pendingClose = $false
for ($index = 0; $index -lt $lines.Length; $index++) {
    $line = $lines[$index]
    if ($line.Contains("Replace(`$anchor,@'") -or $line.Contains("Replace(`$marker,@'")) {
        $line = $line.Replace(",@'",",(@'")
        $pendingClose = $true
    }
    if ($pendingClose -and $line.Contains("'@.TrimEnd() -replace")) {
        $line = $line + ')'
        $pendingClose = $false
    }
    $lines[$index] = $line
}
if ($pendingClose) {
    throw 'August 23 setting-out runtime bridge found an unterminated parenthesized replacement expression.'
}
$utf8 = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($tempPath,($lines -join "`r`n"),$utf8)

$tokens=$null; $errors=$null
[System.Management.Automation.Language.Parser]::ParseFile($tempPath,[ref]$tokens,[ref]$errors) | Out-Null
if ($errors -and $errors.Count -gt 0) {
    $details = ($errors | ForEach-Object { 'line ' + $_.Extent.StartLineNumber + ': ' + $_.Message }) -join ' | '
    Remove-Item -LiteralPath $tempPath -Force -ErrorAction SilentlyContinue
    throw "August 23 setting-out runtime repair has a PowerShell syntax error: $details"
}

try {
    & $tempPath -RepoRoot $root
    if ($LASTEXITCODE -ne 0) {
        throw "August 23 setting-out runtime repair failed with exit code $LASTEXITCODE."
    }
}
finally {
    Remove-Item -LiteralPath $tempPath -Force -ErrorAction SilentlyContinue
}
$global:LASTEXITCODE = 0

# Normalize again after the historical repair. Its old trailing-whitespace regex can
# backtrack around an already-present reset call and duplicate it. This postflight
# keeps CreateOutput and UpdateOutput at exactly one reset call each before compile.
& $vertexLabelGuard -RepoRoot $root
if ($LASTEXITCODE -ne 0) {
    throw "August 23 Vertex Setting-Out label-guard postflight failed with exit code $LASTEXITCODE."
}
$global:LASTEXITCODE = 0

[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Windows PowerShell defines `write` as an alias for Write-Output. The field
# finalizer intentionally has a helper named Write(path,text). Remove the alias and
# dot-source the finalizer into this same scope so the helper cannot be shadowed by
# a fresh child-script AllScope alias.
if (Test-Path -LiteralPath Alias:Write) {
    Remove-Item -LiteralPath Alias:Write -Force
}

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath

# The real installer runs several historical repairs before this last boundary.
# Normalize AddDimension semantically before the legacy finalizer executes.
$dimensionPath = Join-Path $root 'src\CE.Tools.Civil3D\MultiDimensionCommands.cs'
if (-not (Test-Path -LiteralPath $dimensionPath -PathType Leaf)) {
    throw "MultiDimension source missing: $dimensionPath"
}
$utf8 = New-Object System.Text.UTF8Encoding($false)
$dimension = [System.IO.File]::ReadAllText($dimensionPath) -replace "`r?`n", "`r`n"
$marker = 'private static void AddDimension('
$start = $dimension.IndexOf($marker,[StringComparison]::Ordinal)
if ($start -lt 0) { throw 'MultiDimension AddDimension method marker missing.' }
$open = $dimension.IndexOf('{',$start)
if ($open -lt 0) { throw 'MultiDimension AddDimension opening brace missing.' }
$depth = 0
$close = -1
for ($i=$open; $i -lt $dimension.Length; $i++) {
    if ($dimension[$i] -eq '{') { $depth++ }
    elseif ($dimension[$i] -eq '}') {
        $depth--
        if ($depth -eq 0) { $close=$i; break }
    }
}
if ($close -lt 0) { throw 'MultiDimension AddDimension closing brace missing.' }
$methodLength = $close - $start + 1
$method = $dimension.Substring($start,$methodLength)
if (-not $method.Contains('dimension.Dimlfac = 1000.0;')) {
    $created = $method.LastIndexOf('created++;',[StringComparison]::Ordinal)
    if ($created -lt 0) { throw 'MultiDimension AddDimension semantic insertion point missing: created++.' }
    $lineStart = $method.LastIndexOf("`n",$created)
    $indentStart = if ($lineStart -lt 0) { 0 } else { $lineStart + 1 }
    $indent = $method.Substring($indentStart,$created-$indentStart)
    $insert = $indent + '// Drawing geometry is in metres; display linear values as millimetres per dimension.' + "`r`n" +
              $indent + 'dimension.Dimlfac = 1000.0;' + "`r`n"
    $method = $method.Insert($indentStart,$insert)
    $dimension = $dimension.Substring(0,$start) + $method + $dimension.Substring($close+1)
    [System.IO.File]::WriteAllText($dimensionPath,$dimension,$utf8)
    Write-Host 'MultiDimension millimetre factor normalized semantically inside AddDimension.' -ForegroundColor Green
}
else {
    Write-Host 'MultiDimension millimetre factor already present inside AddDimension.' -ForegroundColor Green
}

$dimension = [System.IO.File]::ReadAllText($dimensionPath) -replace "`r?`n", "`r`n"
$start = $dimension.IndexOf($marker,[StringComparison]::Ordinal)
$open = $dimension.IndexOf('{',$start)
$depth = 0
$close = -1
for ($i=$open; $i -lt $dimension.Length; $i++) {
    if ($dimension[$i] -eq '{') { $depth++ }
    elseif ($dimension[$i] -eq '}') {
        $depth--
        if ($depth -eq 0) { $close=$i; break }
    }
}
if ($close -lt 0) { throw 'MultiDimension AddDimension validation boundary missing.' }
$method = $dimension.Substring($start,$close-$start+1)
$count = ([regex]::Matches($method,[regex]::Escape('dimension.Dimlfac = 1000.0;'))).Count
if ($count -ne 1) {
    throw "MultiDimension AddDimension must contain exactly one Dimlfac=1000 assignment; found $count."
}

$finalizer = Join-Path $root 'scripts\Repair-August25-CadSupplementaryFieldMultiSelect-Civil3D2023.ps1'
if (-not (Test-Path -LiteralPath $finalizer -PathType Leaf)) {
    throw "CAD Supplementary finalizer missing: $finalizer"
}

. $finalizer -RepoRoot $root

[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$civilSourceRoot = Join-Path $root 'src\CE.Tools.Civil3D'
$platformPath = Join-Path $civilSourceRoot 'PlatformProductionCommands.cs'
$scriptsRoot = Join-Path $root 'scripts'
if (-not (Test-Path -LiteralPath $civilSourceRoot -PathType Container)) {
    throw "Civil 3D source folder missing for API compatibility: $civilSourceRoot"
}
if (-not (Test-Path -LiteralPath $platformPath -PathType Leaf)) {
    throw "Platform Production source missing for Civil 3D 2023 API compatibility: $platformPath"
}
if (-not (Test-Path -LiteralPath $scriptsRoot -PathType Container)) {
    throw "Scripts folder missing for Civil 3D 2023 API compatibility: $scriptsRoot"
}

$utf8 = New-Object System.Text.UTF8Encoding($false)
$pattern = '(?<!Autodesk\.Civil\.)\bFeatureLinePointType\.'
$self = $MyInvocation.MyCommand.Path
$targets = New-Object System.Collections.Generic.List[string]
Get-ChildItem -LiteralPath $civilSourceRoot -Filter '*.cs' -File | ForEach-Object {
    $targets.Add($_.FullName)
}
Get-ChildItem -LiteralPath $scriptsRoot -Filter '*.ps1' -File | ForEach-Object {
    if (-not [string]::Equals($_.FullName,$self,[StringComparison]::OrdinalIgnoreCase)) {
        $targets.Add($_.FullName)
    }
}

# Civil 3D 2023 defines FeatureLinePointType in Autodesk.Civil, not in
# Autodesk.Civil.DatabaseServices. Qualify every live Civil 3D C# source file
# plus late staged repair templates that could re-inject the old unqualified
# token. This protects newer field-completion commands as well as Platform code
# from the same CS0103 failure during the final Windows build.
$changedFiles = 0
$qualifiedReferences = 0
foreach ($target in $targets) {
    $text = [System.IO.File]::ReadAllText($target) -replace "`r?`n", "`r`n"
    $matches = [regex]::Matches($text,$pattern)
    if ($matches.Count -gt 0) {
        $text = [regex]::Replace($text,$pattern,'Autodesk.Civil.FeatureLinePointType.')
        [System.IO.File]::WriteAllText($target,$text,$utf8)
        $changedFiles++
    }
    $qualifiedReferences += [regex]::Matches($text,'Autodesk\.Civil\.FeatureLinePointType\.').Count
}

# Verify every live Civil 3D source file and repair template can no longer
# reintroduce the CS0103 token during later staged passes.
foreach ($target in $targets) {
    $check = [System.IO.File]::ReadAllText($target)
    if ([regex]::IsMatch($check,$pattern)) {
        throw "Civil 3D 2023 API compatibility failed: unqualified FeatureLinePointType remains in $target"
    }
}
$platformCheck = [System.IO.File]::ReadAllText($platformPath)
if (-not $platformCheck.Contains('Autodesk.Civil.FeatureLinePointType.')) {
    throw 'Civil 3D 2023 Platform API compatibility expected at least one FeatureLinePointType use in PlatformProductionCommands.cs.'
}

Write-Host ("Civil 3D 2023 FeatureLinePointType compatibility passed across live sources and staged templates. Files changed={0}; qualified references={1}." -f $changedFiles,$qualifiedReferences) -ForegroundColor Green

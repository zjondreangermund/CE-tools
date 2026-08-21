[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$path = Join-Path $root 'src\CE.Tools.Civil3D\PlatformProductionCommands.cs'
if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
    throw "Platform Production source missing for Civil 3D 2023 API compatibility: $path"
}

$utf8 = New-Object System.Text.UTF8Encoding($false)
$text = [System.IO.File]::ReadAllText($path) -replace "`r?`n", "`r`n"

# Civil 3D 2023 defines FeatureLinePointType in Autodesk.Civil, not in
# Autodesk.Civil.DatabaseServices. The staged source historically imported only
# DatabaseServices, so the real AeccDbMgd compile fails with CS0103 even though
# syntax-only CI passes. Fully qualify every unqualified enum reference so the
# staged build is independent of using-directive drift.
$pattern = '(?<!Autodesk\.Civil\.)\bFeatureLinePointType\.'
$matches = [regex]::Matches($text,$pattern)
if ($matches.Count -gt 0) {
    $text = [regex]::Replace($text,$pattern,'Autodesk.Civil.FeatureLinePointType.')
    [System.IO.File]::WriteAllText($path,$text,$utf8)
}

$check = [System.IO.File]::ReadAllText($path)
if ([regex]::IsMatch($check,$pattern)) {
    throw 'Civil 3D 2023 Platform API compatibility failed: unqualified FeatureLinePointType remains.'
}
if (-not $check.Contains('Autodesk.Civil.FeatureLinePointType.')) {
    throw 'Civil 3D 2023 Platform API compatibility expected at least one FeatureLinePointType use in PlatformProductionCommands.cs.'
}

Write-Host ("Platform Production Civil 3D 2023 FeatureLinePointType compatibility passed. Qualified references={0}." -f ([regex]::Matches($check,'Autodesk\.Civil\.FeatureLinePointType\.').Count)) -ForegroundColor Green

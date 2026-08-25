[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Windows PowerShell defines `write` as an alias for Write-Output. The field
# finalizer intentionally has a helper named Write(path,text); if the alias remains,
# PowerShell resolves the alias first and merely prints the path/text instead of
# persisting the staged source. Remove it only in this invocation scope, then run
# the finalizer normally.
if (Test-Path -LiteralPath Alias:Write) {
    Remove-Item -LiteralPath Alias:Write -Force
}

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$finalizer = Join-Path $root 'scripts\Repair-August25-CadSupplementaryFieldMultiSelect-Civil3D2023.ps1'
if (-not (Test-Path -LiteralPath $finalizer -PathType Leaf)) {
    throw "CAD Supplementary finalizer missing: $finalizer"
}

& $finalizer -RepoRoot $root

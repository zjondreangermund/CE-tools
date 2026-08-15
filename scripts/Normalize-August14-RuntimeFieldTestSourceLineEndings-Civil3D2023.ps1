[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$sourceRoot = Join-Path $root 'src\CE.Tools.Civil3D'
if (-not (Test-Path -LiteralPath $sourceRoot -PathType Container)) {
    throw "Civil 3D source folder was not found: $sourceRoot"
}

$utf8 = New-Object System.Text.UTF8Encoding($false)
$changed = 0

# GitHub ZIP/source checkout can contain LF-only files. Several late Civil 3D
# staging repairs intentionally use exact multi-line anchors, so normalize the
# staged C# sources to Windows CRLF immediately before those late repairs run.
foreach ($file in Get-ChildItem -LiteralPath $sourceRoot -Filter '*.cs' -File) {
    $text = [System.IO.File]::ReadAllText($file.FullName)
    $normalized = $text -replace "`r?`n", "`r`n"
    if ($normalized -ne $text) {
        [System.IO.File]::WriteAllText($file.FullName, $normalized, $utf8)
        $changed++
    }
}

Write-Host "Runtime field-test source line endings normalized. Files changed=$changed." -ForegroundColor Green

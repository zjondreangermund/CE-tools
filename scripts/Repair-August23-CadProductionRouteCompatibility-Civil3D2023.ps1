[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$src = Join-Path $root 'src\CE.Tools.Civil3D'
$utf8 = New-Object System.Text.UTF8Encoding($false)

function NormalizeRoute([string]$file,[string]$factory,[string]$label) {
    $path = Join-Path $src $file
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "August 23 CAD Production route prerequisite missing: $path"
    }
    $text = [System.IO.File]::ReadAllText($path) -replace "`r?`n","`r`n"
    if ($text.Contains('"CAD PRODUCTION", "CE_CADPRODUCTION"')) {
        return
    }

    $pattern = '(?m)^(\s*' + [regex]::Escape($factory) + '\("FLOOD PRODUCTION",\s*"CE_FLOODPRODUCTIONCENTRE"[^\r\n]*\),\s*)$'
    $matches = [regex]::Matches($text,$pattern)
    if ($matches.Count -ne 1) {
        throw "August 23 CAD Production route expected one $label Flood-production anchor but found $($matches.Count)."
    }
    $indent = [regex]::Match($matches[0].Value,'^\s*').Value
    $route = $indent + $factory + '("CAD PRODUCTION", "CE_CADPRODUCTION", "Open grouped CAD production tools.", "01 Disciplines"),'
    $replacement = $matches[0].Value.TrimEnd() + "`r`n" + $route
    $text = $text.Substring(0,$matches[0].Index) + $replacement + $text.Substring($matches[0].Index + $matches[0].Length)
    [System.IO.File]::WriteAllText($path,$text,$utf8)

    $check = [System.IO.File]::ReadAllText($path)
    if (-not $check.Contains('"CAD PRODUCTION", "CE_CADPRODUCTION"')) {
        throw "August 23 CAD Production route verification failed: $path"
    }
}

NormalizeRoute 'August11ProductionCentreCommands.cs' 'Action' 'August 11 Production Centre'
NormalizeRoute 'August14ProductionCentres.cs' 'A' 'August 14 Production V3'
Write-Host 'CAD Production is routed as a discipline from both Production Centre surfaces.' -ForegroundColor Green

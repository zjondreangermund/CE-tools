[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$vertexPath = Join-Path $root 'src\CE.Tools.Civil3D\VertexSettingOutCommands.cs'
if (-not (Test-Path -LiteralPath $vertexPath -PathType Leaf)) {
    throw "August 19 Vertex compiler-fix source missing: $vertexPath"
}

$utf8 = New-Object System.Text.UTF8Encoding($false)
$text = [System.IO.File]::ReadAllText($vertexPath) -replace "`r?`n", "`r`n"

# The final August 18 display architecture keeps table columns and label prefixes
# fixed as X/Y. DisplayX/DisplayY own the numeric X/Y swap and sign transformation.
# Historical staged variants can retain old yFirst expressions after their local
# yFirst declaration has already been removed, which produces CS0103 at compile.
$text = $text.Replace('yFirst ? "Y" : "X"','"X"')
$text = $text.Replace('yFirst ? "X" : "Y"','"Y"')
$text = $text.Replace('(yFirst ? "Y=" : "X=")','"X="')
$text = $text.Replace('(yFirst ? "X=" : "Y=")','"Y="')

[System.IO.File]::WriteAllText($vertexPath,$text,$utf8)

$check = [System.IO.File]::ReadAllText($vertexPath)
if ($check -match '\byFirst\b') {
    throw 'August 19 Vertex compiler fix failed: at least one stale yFirst reference remains in VertexSettingOutCommands.cs.'
}
if (-not ($check.Contains('"X",') -and $check.Contains('"Y",'))) {
    throw 'August 19 Vertex compiler fix failed: fixed X/Y table headings were not verified.'
}
if (-not ($check.Contains('"X=" + displayX.ToString') -and
          $check.Contains('"Y=" + displayY.ToString'))) {
    throw 'August 19 Vertex compiler fix failed: fixed X/Y label prefixes were not verified.'
}

Write-Host 'August 19 Vertex display finalizer removed all stale yFirst heading/label references before compilation.' -ForegroundColor Green
Write-Host 'DisplayX/DisplayY remain responsible for the saved Swap X/Y and Reverse signs behavior.' -ForegroundColor Green

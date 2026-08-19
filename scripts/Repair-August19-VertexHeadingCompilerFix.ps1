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

# The final August 18 display architecture keeps the table columns fixed as X/Y;
# DisplayX/DisplayY own the numeric swap/sign transformation. Historical staged
# source can still retain these two heading expressions after an earlier repair
# removes their local yFirst declaration, which produces CS0103 at compile time.
$text = $text.Replace('yFirst ? "Y" : "X"','"X"')
$text = $text.Replace('yFirst ? "X" : "Y"','"Y"')

[System.IO.File]::WriteAllText($vertexPath,$text,$utf8)

$check = [System.IO.File]::ReadAllText($vertexPath)
if ($check.Contains('yFirst ? "Y" : "X"') -or
    $check.Contains('yFirst ? "X" : "Y"')) {
    throw 'August 19 Vertex compiler fix failed: stale yFirst table-heading expression remains.'
}
if (-not ($check.Contains('"X",') -and $check.Contains('"Y",'))) {
    throw 'August 19 Vertex compiler fix failed: fixed X/Y table headings were not verified.'
}

Write-Host 'August 19 Vertex table headings finalized for compilation; stale yFirst references removed.' -ForegroundColor Green
Write-Host 'DisplayX/DisplayY remain responsible for the saved Swap X/Y and Reverse signs behavior.' -ForegroundColor Green

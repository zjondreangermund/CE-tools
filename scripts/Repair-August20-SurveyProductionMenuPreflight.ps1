[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$path = Join-Path $root 'src\CE.Tools.Civil3D\August14StructuredDisciplineProductionCentres.cs'
if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
    throw "August 20 Survey Production preflight source missing: $path"
}

$utf8 = New-Object System.Text.UTF8Encoding($false)
$text = [System.IO.File]::ReadAllText($path) -replace "`r?`n", "`r`n"
$signature = 'public void SurveyProduction()'
$methodStart = $text.IndexOf($signature,[StringComparison]::Ordinal)
if ($methodStart -lt 0) {
    throw 'August 20 Survey Production preflight could not find SurveyProduction().'
}
$open = $text.IndexOf('{',$methodStart)
if ($open -lt 0) {
    throw 'August 20 Survey Production preflight could not find the SurveyProduction() opening brace.'
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
    throw 'August 20 Survey Production preflight could not find the SurveyProduction() closing brace.'
}

$method = $text.Substring($methodStart,$close-$methodStart+1)
if (-not $method.Contains('"CE_MULTIDIM"')) {
    # Do not depend on any historical command token or description. Find the first
    # live DisciplineWorkflowAction A(...) row inside SurveyProduction() and place
    # Multiple Dimensions immediately before it. The inserted row carries a comma,
    # so the existing first/last action punctuation remains untouched.
    $matches = [regex]::Matches($method,'(?m)^[ \t]*A\("')
    if ($matches.Count -lt 1) {
        throw 'August 20 Survey Production preflight found no live A(...) action row in SurveyProduction().'
    }
    $insertAt = $matches[0].Index
    $indentMatch = [regex]::Match($method.Substring($insertAt),'^[ \t]*')
    $indent = if ($indentMatch.Success) { $indentMatch.Value } else { '                    ' }
    $action = $indent + 'A("CE-Multiple Dimensions", "CE_MULTIDIM", "Annotative aligned/linear/angular/radius/arc-length dimensions for multiple polylines and feature lines.", "01 Survey Production"),' + "`r`n"
    $method = $method.Substring(0,$insertAt) + $action + $method.Substring($insertAt)
    $text = $text.Substring(0,$methodStart) + $method + $text.Substring($close+1)
    [System.IO.File]::WriteAllText($path,($text -replace "`r?`n","`r`n"),$utf8)
}

$check = [System.IO.File]::ReadAllText($path)
$checkStart = $check.IndexOf($signature,[StringComparison]::Ordinal)
$checkOpen = $check.IndexOf('{',$checkStart)
$depth = 0
$checkClose = -1
for ($i=$checkOpen; $i -lt $check.Length; $i++) {
    if ($check[$i] -eq '{') { $depth++ }
    elseif ($check[$i] -eq '}') {
        $depth--
        if ($depth -eq 0) { $checkClose = $i; break }
    }
}
if ($checkClose -lt 0) { throw 'August 20 Survey Production preflight validation could not isolate SurveyProduction().' }
$checkMethod = $check.Substring($checkStart,$checkClose-$checkStart+1)
if ([regex]::Matches($checkMethod,'"CE_MULTIDIM"').Count -ne 1) {
    throw 'August 20 Survey Production preflight did not leave exactly one CE_MULTIDIM action in SurveyProduction().'
}

Write-Host 'August 20 Survey Production menu preflight passed: CE_MULTIDIM is present exactly once without relying on a historical command anchor.' -ForegroundColor Green

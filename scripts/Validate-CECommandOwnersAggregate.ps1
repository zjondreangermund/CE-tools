[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$src = Join-Path $root 'src\CE.Tools.Civil3D'
if (-not (Test-Path -LiteralPath $src -PathType Container)) {
    throw "CE command-owner validation source folder missing: $src"
}

$owners = @{}
$references = @{}
$files = Get-ChildItem -LiteralPath $src -Filter '*.cs' -File
$declarationPattern = 'CommandMethod\s*\(\s*(?:"[^"]+"\s*,\s*)?"(?<cmd>CE_[A-Z0-9_]+)"'
$referencePatterns = @(
    '\b(?:Cmd|Action|RoadAction|WorkflowAction)\s*\(\s*"[^"]*"\s*,\s*"(?<cmd>CE_[A-Z0-9_]+)\b',
    '(?:new\s+)?DisciplineWorkflowAction\s*\(\s*"[^"]*"\s*,\s*"(?<cmd>CE_[A-Z0-9_]+)\b')
$sendPattern = 'SendStringToExecute\s*\(\s*"(?<body>(?:[^"\\]|\\.)*)"'
$ceTokenPattern = '\bCE_[A-Z0-9_]+\b'

foreach ($file in $files) {
    $text = [System.IO.File]::ReadAllText($file.FullName)
    foreach ($match in [regex]::Matches($text,$declarationPattern,[System.Text.RegularExpressions.RegexOptions]::IgnoreCase)) {
        $cmd = $match.Groups['cmd'].Value.ToUpperInvariant()
        if (-not $owners.ContainsKey($cmd)) { $owners[$cmd] = New-Object System.Collections.Generic.List[string] }
        $owners[$cmd].Add($file.Name)
    }
    foreach ($pattern in $referencePatterns) {
        foreach ($match in [regex]::Matches($text,$pattern,[System.Text.RegularExpressions.RegexOptions]::IgnoreCase)) {
            $cmd = $match.Groups['cmd'].Value.ToUpperInvariant()
            if (-not $references.ContainsKey($cmd)) { $references[$cmd] = New-Object System.Collections.Generic.List[string] }
            $references[$cmd].Add($file.Name)
        }
    }
    foreach ($match in [regex]::Matches($text,$sendPattern,[System.Text.RegularExpressions.RegexOptions]::IgnoreCase)) {
        foreach ($token in [regex]::Matches($match.Groups['body'].Value,$ceTokenPattern,[System.Text.RegularExpressions.RegexOptions]::IgnoreCase)) {
            $cmd = $token.Value.ToUpperInvariant()
            if (-not $references.ContainsKey($cmd)) { $references[$cmd] = New-Object System.Collections.Generic.List[string] }
            $references[$cmd].Add($file.Name)
        }
    }
}

$problems = New-Object System.Collections.Generic.List[string]
foreach ($cmd in ($owners.Keys | Sort-Object)) {
    $uniqueOwners = @($owners[$cmd] | Select-Object -Unique)
    if ($uniqueOwners.Count -ne 1) {
        $problems.Add("Duplicate owner: $cmd -> " + ($uniqueOwners -join ', '))
    }
}
foreach ($cmd in ($references.Keys | Sort-Object)) {
    if ($cmd -eq 'CE_TOOLS') { continue }
    if (-not $owners.ContainsKey($cmd)) {
        $from = @($references[$cmd] | Select-Object -Unique)
        $problems.Add("Missing owner: $cmd <- " + ($from -join ', '))
    }
}

if ($problems.Count -gt 0) {
    Write-Host 'CE command-owner audit found the following wiring problems:' -ForegroundColor Red
    foreach ($problem in $problems) { Write-Host ('  - ' + $problem) -ForegroundColor Red }
    throw "CE command-owner aggregate validation failed with $($problems.Count) problem(s)."
}

Write-Host ("CE command-owner aggregate validation passed. Owners={0}; references={1}; source files={2}." -f $owners.Count,$references.Count,$files.Count) -ForegroundColor Green

[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$sourcePath = Join-Path $root 'scripts\Repair-August23-SettingOutCadFeedback-Civil3D2023.ps1'
$tempPath = Join-Path $root 'scripts\.Repair-August23-SettingOutCadFeedback.runtime.ps1'
if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
    throw "August 23 setting-out feedback repair missing: $sourcePath"
}

# PowerShell 7 can bind a here-string followed by -replace inside String.Replace(...)
# to the three-argument StringComparison overload. The authored repair is kept
# readable, while this runtime bridge parenthesizes those replacement expressions
# before invocation. The transformation is deterministic and does not touch C#.
$lines = [System.IO.File]::ReadAllLines($sourcePath)
$pendingClose = $false
for ($index = 0; $index -lt $lines.Length; $index++) {
    $line = $lines[$index]
    if ($line.Contains("Replace(`$anchor,@'") -or $line.Contains("Replace(`$marker,@'")) {
        $line = $line.Replace(",@'",",(@'")
        $pendingClose = $true
    }
    if ($pendingClose -and $line.Contains("'@.TrimEnd() -replace")) {
        $line = $line + ')'
        $pendingClose = $false
    }
    $lines[$index] = $line
}
if ($pendingClose) {
    throw 'August 23 setting-out runtime bridge found an unterminated parenthesized replacement expression.'
}
$utf8 = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($tempPath,($lines -join "`r`n"),$utf8)

$tokens=$null; $errors=$null
[System.Management.Automation.Language.Parser]::ParseFile($tempPath,[ref]$tokens,[ref]$errors) | Out-Null
if ($errors -and $errors.Count -gt 0) {
    $details = ($errors | ForEach-Object { 'line ' + $_.Extent.StartLineNumber + ': ' + $_.Message }) -join ' | '
    Remove-Item -LiteralPath $tempPath -Force -ErrorAction SilentlyContinue
    throw "August 23 setting-out runtime repair has a PowerShell syntax error: $details"
}

try {
    & $tempPath -RepoRoot $root
    if ($LASTEXITCODE -ne 0) {
        throw "August 23 setting-out runtime repair failed with exit code $LASTEXITCODE."
    }
}
finally {
    Remove-Item -LiteralPath $tempPath -Force -ErrorAction SilentlyContinue
}
$global:LASTEXITCODE = 0

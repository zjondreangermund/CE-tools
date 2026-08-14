[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)]
    [string]$RepoRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = [System.IO.Path]::GetFullPath($RepoRoot.Trim().Trim('"'))
$sourceRoot = Join-Path $root 'src\CE.Tools.Civil3D'
if (-not (Test-Path -LiteralPath $sourceRoot -PathType Container)) {
    throw "Civil 3D source folder was not found: $sourceRoot"
}

# This is intentionally late in the staged 2023 build. The normal Project
# Production centre is generated/restored by earlier compatibility passes, so
# route it to the Last Saved / Standard Blank workflow only after those passes.
$projectSetupRepair = Join-Path $root 'scripts\Repair-August14ProjectSetupPersistence-Civil3D2023.ps1'
if (-not (Test-Path -LiteralPath $projectSetupRepair -PathType Leaf)) {
    throw "Project Setup persistence repair was not found: $projectSetupRepair"
}
Unblock-File -LiteralPath $projectSetupRepair -ErrorAction SilentlyContinue
& $projectSetupRepair -RepoRoot $root
$global:LASTEXITCODE = 0

# This is intentionally the final compatibility pass in the staged 2023 build.
# Earlier August injectors can change API-facing method signatures/source shapes;
# repair those final staged sources immediately before sanitizing and compiling.
$finalCompileRepair = Join-Path $root 'scripts\Repair-August14-Civil3D2023-CompileErrors.ps1'
if (-not (Test-Path -LiteralPath $finalCompileRepair -PathType Leaf)) {
    throw "Final Civil 3D 2023 compiler repair was not found: $finalCompileRepair"
}
Unblock-File -LiteralPath $finalCompileRepair -ErrorAction SilentlyContinue
& $finalCompileRepair -RepoRoot $root
$global:LASTEXITCODE = 0

$utf8 = New-Object System.Text.UTF8Encoding($false)
$changed = New-Object System.Collections.Generic.List[string]

foreach ($file in Get-ChildItem -LiteralPath $sourceRoot -Filter '*.cs' -File) {
    $text = [System.IO.File]::ReadAllText($file.FullName)
    $builder = New-Object System.Text.StringBuilder

    foreach ($ch in $text.ToCharArray()) {
        $code = [int][char]$ch
        $category = [Globalization.CharUnicodeInfo]::GetUnicodeCategory($ch)

        # Preserve normal whitespace. Remove embedded control/format characters
        # that can corrupt Roslyn source-span bookkeeping after archive recovery.
        if ($ch -eq "`r" -or $ch -eq "`n" -or $ch -eq "`t") {
            [void]$builder.Append($ch)
            continue
        }
        if ($category -eq [Globalization.UnicodeCategory]::Control -or
            $category -eq [Globalization.UnicodeCategory]::Format -or
            $code -eq 0xFEFF) {
            continue
        }
        [void]$builder.Append($ch)
    }

    $normalized = $builder.ToString() -replace "`r?`n", "`r`n"
    if ($normalized -ne $text) {
        [System.IO.File]::WriteAllText($file.FullName, $normalized, $utf8)
        $changed.Add($file.Name)
        Write-Host "Sanitized restored source: $($file.Name)" -ForegroundColor Green
    }
}

if ($changed.Count -eq 0) {
    Write-Host 'No invalid control or format characters were found in restored C# sources.' -ForegroundColor Yellow
}
else {
    Write-Host "Sanitized $($changed.Count) restored C# source file(s)." -ForegroundColor Cyan
}

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

# Keep the approved Road hierarchy unchanged and route every other discipline to
# the same progressive Production Centre pattern only after older staging passes
# have finished editing August11ProductionCentreCommands.cs.
$structuredProductionRepair = Join-Path $root 'scripts\Repair-August14-StructuredDisciplineProduction-Civil3D2023.ps1'
if (-not (Test-Path -LiteralPath $structuredProductionRepair -PathType Leaf)) {
    throw "Structured discipline Production Centre repair was not found: $structuredProductionRepair"
}
Unblock-File -LiteralPath $structuredProductionRepair -ErrorAction SilentlyContinue
& $structuredProductionRepair -RepoRoot $root
$global:LASTEXITCODE = 0

# Earlier staging passes can reformat or partially rewrite RefreshNow(), so make
# the required Survey hooks present before the larger Project/Survey final repair.
# This deliberately avoids relying on one historical multi-line text anchor.
$surveyHookRepair = Join-Path $root 'scripts\Repair-August14-UniversalDynamicSurveyHooks-Civil3D2023.ps1'
if (-not (Test-Path -LiteralPath $surveyHookRepair -PathType Leaf)) {
    throw "Universal Dynamic Survey hook repair was not found: $surveyHookRepair"
}
Unblock-File -LiteralPath $surveyHookRepair -ErrorAction SilentlyContinue
& $surveyHookRepair -RepoRoot $root
$global:LASTEXITCODE = 0

# Survey Site Grid is touched by several older staging passes. Normalize its
# label loops by structure before the final Project/Survey field repair so a
# formatting or edge-label expansion cannot break an exact historical anchor.
$siteGridCompatRepair = Join-Path $root 'scripts\Repair-August14-SiteGridLabelAnchorCompat-Civil3D2023.ps1'
if (-not (Test-Path -LiteralPath $siteGridCompatRepair -PathType Leaf)) {
    throw "Survey Site Grid structural compatibility repair was not found: $siteGridCompatRepair"
}
Unblock-File -LiteralPath $siteGridCompatRepair -ErrorAction SilentlyContinue
& $siteGridCompatRepair -RepoRoot $root
$global:LASTEXITCODE = 0

# Apply the latest Project + Survey field-review comments after every older
# production/UI repair, so Project Info remains the single metadata source and
# Survey dynamic/annotation behaviour cannot be restored to an older version.
$projectSurveyRepair = Join-Path $root 'scripts\Repair-August14-ProjectSurveyFieldComments-Civil3D2023.ps1'
if (-not (Test-Path -LiteralPath $projectSurveyRepair -PathType Leaf)) {
    throw "Project/Survey field-comment repair was not found: $projectSurveyRepair"
}
Unblock-File -LiteralPath $projectSurveyRepair -ErrorAction SilentlyContinue
& $projectSurveyRepair -RepoRoot $root
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

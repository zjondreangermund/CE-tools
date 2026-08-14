[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$path = Join-Path $root 'src\CE.Tools.Civil3D\UniversalDynamicRefreshCommands.cs'
if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
    throw "Universal Dynamic source was not found: $path"
}

$utf8 = New-Object System.Text.UTF8Encoding($false)
$text = [System.IO.File]::ReadAllText($path)

function Find-LineStart([string]$Text, [int]$Index) {
    $lineStart = $Text.LastIndexOf("`n", [Math]::Max(0, $Index - 1))
    if ($lineStart -lt 0) { return 0 }
    return $lineStart + 1
}

function Find-LineEnd([string]$Text, [int]$Index) {
    $lineEnd = $Text.IndexOf("`n", $Index)
    if ($lineEnd -lt 0) { return $Text.Length }
    return $lineEnd + 1
}

function Line-Indent([string]$Text, [int]$LineStart, [int]$TokenIndex) {
    if ($TokenIndex -le $LineStart) { return '' }
    $prefix = $Text.Substring($LineStart, $TokenIndex - $LineStart)
    $match = [regex]::Match($prefix, '^\s*')
    return $match.Value
}

function Insert-BeforeStatement {
    param([string]$Text,[string]$Statement,[string[]]$Lines)
    $index = $Text.IndexOf($Statement, [StringComparison]::Ordinal)
    if ($index -lt 0) { throw "Universal Dynamic statement was not found: $Statement" }
    $start = Find-LineStart $Text $index
    $indent = Line-Indent $Text $start $index
    $block = (($Lines | ForEach-Object { $indent + $_ }) -join "`r`n") + "`r`n"
    return $Text.Insert($start, $block)
}

function Insert-AfterStatement {
    param([string]$Text,[string]$Statement,[string[]]$Lines)
    $index = $Text.IndexOf($Statement, [StringComparison]::Ordinal)
    if ($index -lt 0) { throw "Universal Dynamic statement was not found: $Statement" }
    $start = Find-LineStart $Text $index
    $end = Find-LineEnd $Text $index
    $indent = Line-Indent $Text $start $index
    $block = (($Lines | ForEach-Object { $indent + $_ }) -join "`r`n") + "`r`n"
    return $Text.Insert($end, $block)
}

function Insert-AfterTryCatchContainingStatement {
    param([string]$Text,[string]$Statement,[string[]]$Lines)

    $index = $Text.IndexOf($Statement, [StringComparison]::Ordinal)
    if ($index -lt 0) { throw "Universal Dynamic try/catch statement was not found: $Statement" }

    $statementStart = Find-LineStart $Text $index
    $statementEnd = Find-LineEnd $Text $index
    $indent = Line-Indent $Text $statementStart $index

    # The current CE refresh calls are normally one-line try blocks followed by
    # a one-line catch. Insert only after that catch. This avoids producing:
    #     try { ExistingCall(); }
    #     try { NewCall(); } ...
    #     catch { ... }   // orphaned catch / CS1524
    $cursor = $statementEnd
    while ($cursor -lt $Text.Length) {
        $lineEnd = Find-LineEnd $Text $cursor
        $line = $Text.Substring($cursor, $lineEnd - $cursor)
        $trimmed = $line.Trim()
        if ($trimmed.Length -eq 0) {
            $cursor = $lineEnd
            continue
        }
        if ($trimmed.StartsWith('catch', [StringComparison]::Ordinal)) {
            $block = (($Lines | ForEach-Object { $indent + $_ }) -join "`r`n") + "`r`n"
            return $Text.Insert($lineEnd, $block)
        }
        break
    }

    # Fallback for a multiline try block: find the first catch belonging to the
    # statement's enclosing refresh try before the next sibling refresh call.
    $catchIndex = $Text.IndexOf('catch', $statementEnd, [StringComparison]::Ordinal)
    if ($catchIndex -lt 0) {
        throw "Universal Dynamic catch was not found after: $Statement"
    }
    $catchStart = Find-LineStart $Text $catchIndex
    $catchEnd = Find-LineEnd $Text $catchIndex
    $between = $Text.Substring($statementEnd, $catchStart - $statementEnd)
    if ($between.IndexOf('try {', [StringComparison]::Ordinal) -ge 0) {
        throw "Another try block appeared before the catch for: $Statement"
    }
    $block = (($Lines | ForEach-Object { $indent + $_ }) -join "`r`n") + "`r`n"
    return $Text.Insert($catchEnd, $block)
}

# Capture the original Civil 3D COGO label offset before any dynamic engine can
# reposition labels. This prevents cumulative label drift on repeated refreshes.
if (-not $text.Contains('August11SurveyRuntimeCommands.CaptureCogoInitialOffsets(document);')) {
    $text = Insert-BeforeStatement -Text $text -Statement 'LinkedRefreshEngine.Refresh(document, false);' -Lines @(
        'try { August11SurveyRuntimeCommands.CaptureCogoInitialOffsets(document); }',
        'catch { result.Warnings++; }'
    )
}

# Ensure every source-linked Survey system participates in Universal Dynamic.
if (-not $text.Contains('MultiSurfaceComparisonTableStore.RefreshAll(document);')) {
    $lines = New-Object System.Collections.Generic.List[string]
    if (-not $text.Contains('DynamicCoordinateLinkStore.Refresh(document);')) {
        $lines.Add('try { DynamicCoordinateLinkStore.Refresh(document); }')
        $lines.Add('catch { result.Warnings++; }')
    }
    if (-not $text.Contains('SurfaceComparisonLinkStore.RefreshAll(document);')) {
        $lines.Add('try { SurfaceComparisonLinkStore.RefreshAll(document); }')
        $lines.Add('catch { result.Warnings++; }')
    }
    $lines.Add('try { MultiSurfaceComparisonTableStore.RefreshAll(document); }')
    $lines.Add('catch { result.Warnings++; }')
    if (-not $text.Contains('LinkedSurfaceReportTableStore.RefreshAll(document);')) {
        $lines.Add('try { LinkedSurfaceReportTableStore.RefreshAll(document); }')
        $lines.Add('catch { result.Warnings++; }')
    }
    $text = Insert-AfterTryCatchContainingStatement -Text $text -Statement 'SurveyCoordinateWorkflowCommands.RefreshAll(document);' -Lines $lines.ToArray()
}

# Restore the original label offset and current annotation context only after all
# linked Survey geometry/tables have been refreshed.
if (-not $text.Contains('August11SurveyRuntimeCommands.RestoreCogoLabels(document, null);')) {
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add('try { August11SurveyRuntimeCommands.RestoreCogoLabels(document, null); }')
    $lines.Add('catch { result.Warnings++; }')
    if (-not $text.Contains('AnnotationScaleSyncManager.ApplyCurrentScale(document);')) {
        $lines.Add('try { AnnotationScaleSyncManager.ApplyCurrentScale(document); }')
        $lines.Add('catch { result.Warnings++; }')
    }
    $text = Insert-BeforeStatement -Text $text -Statement 'CeTablePresentationManager.CenterCeTables(document);' -Lines $lines.ToArray()
}

$required = @(
    'August11SurveyRuntimeCommands.CaptureCogoInitialOffsets(document);',
    'DynamicCoordinateLinkStore.Refresh(document);',
    'SurfaceComparisonLinkStore.RefreshAll(document);',
    'MultiSurfaceComparisonTableStore.RefreshAll(document);',
    'LinkedSurfaceReportTableStore.RefreshAll(document);',
    'August11SurveyRuntimeCommands.RestoreCogoLabels(document, null);',
    'AnnotationScaleSyncManager.ApplyCurrentScale(document);'
)
foreach ($marker in $required) {
    if (-not $text.Contains($marker)) {
        throw "Universal Dynamic Survey hook verification failed: $marker"
    }
}

# Guard the exact compiler failure seen during the Civil 3D 2023 staged build.
# The SurveyCoordinate try must be followed by its catch before any injected try.
$malformedPattern = 'try\s*\{\s*SurveyCoordinateWorkflowCommands\.RefreshAll\(document\);\s*\}\s*try\s*\{'
if ([regex]::IsMatch($text, $malformedPattern, [System.Text.RegularExpressions.RegexOptions]::Singleline)) {
    throw 'Universal Dynamic Survey hook repair produced a try block without its catch.'
}

[System.IO.File]::WriteAllText($path, ($text -replace "`r?`n", "`r`n"), $utf8)
Write-Host 'Universal Dynamic Survey hooks are present and preserve every existing try/catch pair.' -ForegroundColor Green

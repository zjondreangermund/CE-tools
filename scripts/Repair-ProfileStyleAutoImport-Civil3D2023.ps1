[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Ensure-ProfileAutoImport {
    param(
        [Parameter(Mandatory=$true)][string]$Path,
        [Parameter(Mandatory=$true)][string]$Command,
        [Parameter(Mandatory=$true)][string]$Label
    )

    $text = [System.IO.File]::ReadAllText($Path)
    $escapedCommand = [regex]::Escape($Command)
    $methodPattern = '\[CommandMethod\([^\]]*"' + $escapedCommand + '"[^\]]*\)\]\s*public\s+void\s+[A-Za-z0-9_]+\s*\(\s*\)\s*\{'
    $method = [regex]::Match(
        $text,
        $methodPattern,
        [System.Text.RegularExpressions.RegexOptions]::Singleline)
    if (-not $method.Success) {
        throw "Could not locate profile command $Command in $Path"
    }

    $methodStart = $method.Index
    $bodyStart = $method.Index + $method.Length
    $nextCommand = $text.IndexOf('[CommandMethod(', $bodyStart, [System.StringComparison]::Ordinal)
    if ($nextCommand -lt 0) { $methodEnd = $text.Length } else { $methodEnd = $nextCommand }
    $segment = $text.Substring($methodStart, $methodEnd - $methodStart)

    $callToken = 'ProfileStyleAutoImportRuntime.EnsureBundledProfileStyles'
    if ($segment.Contains($callToken)) {
        Write-Host "Already integrated: auto-import bundled profile/band styles before $Label profiles" -ForegroundColor DarkGreen
        return
    }

    $civilToken = 'CivilDocument civilDocument = CivilApplication.ActiveDocument;'
    $civilIndex = $segment.IndexOf($civilToken, [System.StringComparison]::Ordinal)
    if ($civilIndex -lt 0) {
        throw "Could not locate CivilDocument initialization in $Command ($Path)"
    }

    $tailStart = $civilIndex + $civilToken.Length
    $tail = $segment.Substring($tailStart)
    $guardPatterns = @(
        '^\s*if\s*\(\s*document\s*==\s*null\s*\|\|\s*civilDocument\s*==\s*null\s*\)\s*(?:return;|\{.*?return;\s*\})',
        '^\s*if\s*\(\s*civilDocument\s*==\s*null\s*\)\s*(?:return;|\{.*?return;\s*\})'
    )

    $guard = $null
    foreach ($pattern in $guardPatterns) {
        $candidate = [regex]::Match(
            $tail,
            $pattern,
            [System.Text.RegularExpressions.RegexOptions]::Singleline)
        if ($candidate.Success) {
            $guard = $candidate
            break
        }
    }
    if ($null -eq $guard) {
        throw "Could not locate the CivilDocument safety guard in $Command ($Path)"
    }

    $insertAt = $methodStart + $tailStart + $guard.Index + $guard.Length
    $injection = @"

            int autoImportedStyles;
            string autoImportMessage;
            ProfileStyleAutoImportRuntime.EnsureBundledProfileStyles(document, out autoImportedStyles, out autoImportMessage);
            if (!string.IsNullOrWhiteSpace(autoImportMessage))
                document.Editor.WriteMessage("\nCE $Label profile style check: " + autoImportMessage);
"@

    $text = $text.Insert($insertAt, $injection)
    [System.IO.File]::WriteAllText($Path, $text, [System.Text.UTF8Encoding]::new($false))

    $verifyText = [System.IO.File]::ReadAllText($Path)
    $verifyMethod = [regex]::Match(
        $verifyText,
        $methodPattern,
        [System.Text.RegularExpressions.RegexOptions]::Singleline)
    if (-not $verifyMethod.Success) {
        throw "Profile style auto-import verification could not re-locate $Command in $Path"
    }
    $verifyStart = $verifyMethod.Index
    $verifyBody = $verifyMethod.Index + $verifyMethod.Length
    $verifyNext = $verifyText.IndexOf('[CommandMethod(', $verifyBody, [System.StringComparison]::Ordinal)
    if ($verifyNext -lt 0) { $verifyEnd = $verifyText.Length } else { $verifyEnd = $verifyNext }
    $verifySegment = $verifyText.Substring($verifyStart, $verifyEnd - $verifyStart)
    if (-not $verifySegment.Contains($callToken)) {
        throw "Profile style auto-import verification failed for $Label ($Command)."
    }

    Write-Host "Integrated: auto-import bundled profile/band styles before $Label profiles" -ForegroundColor Green
}

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$src = Join-Path $root 'src\CE.Tools.Civil3D'
$road = Join-Path $src 'RoadProductionCommentCommands.cs'
$sewer = Join-Path $src 'SewerProductionCommands.cs'
$storm = Join-Path $src 'StormwaterProductionCommands.cs'
$water = Join-Path $src 'WaterProductionCommands.cs'
$runtime = Join-Path $src 'ProfileStyleAutoImportRuntime.cs'
foreach ($path in @($road,$sewer,$storm,$water,$runtime)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Profile style auto-import source missing: $path"
    }
}

Ensure-ProfileAutoImport -Path $road -Command 'CE_ROADPROFILES' -Label 'road'
Ensure-ProfileAutoImport -Path $sewer -Command 'CE_SEWPROFILE' -Label 'sewer'
Ensure-ProfileAutoImport -Path $storm -Command 'CE_SWPROFILE' -Label 'stormwater'
Ensure-ProfileAutoImport -Path $water -Command 'CE_WATERPROFILE' -Label 'water'

Write-Host 'Automatic profile/band style import integration passed.' -ForegroundColor Cyan

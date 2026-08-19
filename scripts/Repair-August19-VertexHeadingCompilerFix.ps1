[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$vertexPath = Join-Path $root 'src\CE.Tools.Civil3D\VertexSettingOutCommands.cs'
$gridPath = Join-Path $root 'src\CE.Tools.Civil3D\August18DynamicGridSettingOutCommands.cs'
$sitePath = Join-Path $root 'src\CE.Tools.Civil3D\August12SurveySiteGridCommands.cs'
foreach ($required in @($vertexPath,$gridPath,$sitePath)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "August 19 compiler-fix source missing: $required"
    }
}

$utf8 = New-Object System.Text.UTF8Encoding($false)
$text = [System.IO.File]::ReadAllText($vertexPath) -replace "`r?`n", "`r`n"

# The final August 18 display architecture keeps table columns and label prefixes
# fixed as X/Y. DisplayX/DisplayY own the numeric X/Y swap and sign transformation.
# Historical staged variants can retain old yFirst expressions and/or the now-unused
# local yFirst declaration, which can either cause CS0103 or trip the final guard.
$text = $text.Replace('yFirst ? "Y" : "X"','"X"')
$text = $text.Replace('yFirst ? "X" : "Y"','"Y"')
$text = $text.Replace('(yFirst ? "Y=" : "X=")','"X="')
$text = $text.Replace('(yFirst ? "X=" : "Y=")','"Y="')

# DisplayX/DisplayY already contain the persisted Swap X/Y and Reverse-sign logic.
# Any old yFirst ternary around those values would apply the swap a second time.
# Collapse those historical expressions regardless of whitespace or parentheses.
$text = [regex]::Replace(
    $text,
    '\byFirst\s*\?\s*displayY\s*:\s*displayX\b',
    'displayX')
$text = [regex]::Replace(
    $text,
    '\byFirst\s*\?\s*displayX\s*:\s*displayY\b',
    'displayY')

# Remove any remaining local declaration regardless of indentation/line wrapping.
$yFirstDeclarationPattern = '(?ms)^\s*bool\s+yFirst\s*=\s*string\.Equals\(\s*link\.CoordinateOrder\s*,\s*"Y then X"\s*,\s*StringComparison\.OrdinalIgnoreCase\s*\)\s*;\s*'
$text = [regex]::Replace($text,$yFirstDeclarationPattern,'')

[System.IO.File]::WriteAllText($vertexPath,$text,$utf8)

$check = [System.IO.File]::ReadAllText($vertexPath)
if ($check -match '\byFirst\b') {
    $matches = [regex]::Matches($check,'(?m)^.*\byFirst\b.*$') |
        ForEach-Object { $_.Value.Trim() } |
        Select-Object -First 5
    throw ('August 19 Vertex compiler fix failed: stale yFirst text remains: ' + ($matches -join ' | '))
}
if (-not ($check.Contains('"X",') -and $check.Contains('"Y",'))) {
    throw 'August 19 Vertex compiler fix failed: fixed X/Y table headings were not verified.'
}
if (-not ($check.Contains('"X=" + displayX.ToString') -and
          $check.Contains('"Y=" + displayY.ToString'))) {
    throw 'August 19 Vertex compiler fix failed: fixed X/Y label prefixes were not verified.'
}
if ($check -match '\byFirst\s*\?\s*display[XY]') {
    throw 'August 19 Vertex compiler fix failed: a legacy double-swap expression still survives.'
}

# The August 19 Grid surface popup is applied after several historical staging
# transforms. Those transforms can move the popup ahead of a local choice-list
# declaration, producing CS0841 even though the raw August 18 source is ordered
# correctly. Do not rely on a local variable at all: build each dropdown list
# inline from the live Civil 3D surface catalogue, then remove the obsolete locals.
$grid = [System.IO.File]::ReadAllText($gridPath) -replace "`r?`n", "`r`n"
$inlineSurfaceChoices = 'new[] { "<None>" }.Concat(ReadGridSurfaceNames(document.Database, civil)).ToList()'
$grid = [regex]::Replace(
    $grid,
    '\bgridSurfaceChoices\s*\)',
    $inlineSurfaceChoices + ')')

$surfaceChoiceDeclarationPattern = '(?ms)^\s*List<string>\s+gridSurfaceNames\s*=\s*ReadGridSurfaceNames\(\s*document\.Database\s*,\s*civil\s*\)\s*;\s*\r?\n\s*var\s+gridSurfaceChoices\s*=\s*new\s+List<string>\s*\{\s*"<None>"\s*\}\s*;\s*\r?\n\s*gridSurfaceChoices\.AddRange\(\s*gridSurfaceNames\s*\)\s*;\s*\r?\n?'
$grid = [regex]::Replace($grid,$surfaceChoiceDeclarationPattern,'')

[System.IO.File]::WriteAllText($gridPath,$grid,$utf8)

$gridCheck = [System.IO.File]::ReadAllText($gridPath)
if ($gridCheck -match '\bgridSurfaceChoices\b') {
    throw 'August 19 Grid compiler fix failed: gridSurfaceChoices still survives in the final staged C#.'
}
if ($gridCheck -match '\bgridSurfaceNames\b') {
    throw 'August 19 Grid compiler fix failed: obsolete gridSurfaceNames local still survives in the final staged C#.'
}
if (-not ($gridCheck.Contains('"BaseSurface"') -and
          $gridCheck.Contains('"ComparisonSurface"'))) {
    throw 'August 19 Grid compiler fix failed: Base/Comparison surface popup controls were not verified.'
}
if (-not $gridCheck.Contains($inlineSurfaceChoices)) {
    throw 'August 19 Grid compiler fix failed: inline live surface choices were not verified.'
}
if (-not $gridCheck.Contains('using System.Linq;')) {
    throw 'August 19 Grid compiler fix failed: System.Linq is required for inline surface choices.'
}

# Finalize Site Grid presentation after every August 18/19 source transform. The
# linked frame may use an explicit entity colour while its layer colour is dark;
# children that only copy LayerId can therefore exist successfully but be invisible
# on AutoCAD's black model-space background. Copy the visible frame colour to every
# generated child, and keep coordinate labels inside the selected frame even when
# the active annotation scale is unusually large.
$site = [System.IO.File]::ReadAllText($sitePath) -replace "`r?`n", "`r`n"

if (-not $site.Contains('double siteGridTextCeiling = Math.Max(')) {
    # Earlier August 19 staging has used more than one readable-height formula
    # (including 0.08 and an older 0.40 spacing floor). Do not anchor to either
    # historical formula. Locate the current Site Grid floor structurally and
    # replace everything through the insideOffset assignment with the final
    # clamped presentation block.
    $rebuildStart = $site.IndexOf('        private static int RebuildOne(',[StringComparison]::Ordinal)
    if ($rebuildStart -lt 0) {
        throw 'August 19 Site Grid display fix failed: RebuildOne was not found.'
    }
    $heightStart = $site.IndexOf(
        '            double siteGridTextFloor = Math.Max(',
        $rebuildStart,
        [StringComparison]::Ordinal)
    if ($heightStart -lt 0) {
        throw 'August 19 Site Grid display fix failed: current siteGridTextFloor block was not found.'
    }
    $insideStart = $site.IndexOf(
        '            double insideOffset =',
        $heightStart,
        [StringComparison]::Ordinal)
    if ($insideStart -lt 0) {
        throw 'August 19 Site Grid display fix failed: current insideOffset assignment was not found.'
    }
    $insideEnd = $site.IndexOf(';',$insideStart,[StringComparison]::Ordinal)
    if ($insideEnd -lt 0) {
        throw 'August 19 Site Grid display fix failed: current insideOffset terminator was not found.'
    }

    $newSiteHeight = @'
            double siteGridMinimumSpacing = Math.Max(
                0.001,
                Math.Min(settings.SpacingX, settings.SpacingY));
            double siteGridFrameSpan = Math.Max(
                0.001,
                Math.Min(
                    Math.Abs(bounds.MaxX - bounds.MinX),
                    Math.Abs(bounds.MaxY - bounds.MinY)));
            double siteGridTextFloor = Math.Max(
                Math.Min(siteGridMinimumSpacing * 0.04, siteGridFrameSpan * 0.01),
                0.01);
            double siteGridTextCeiling = Math.Max(
                siteGridTextFloor,
                Math.Min(siteGridMinimumSpacing * 0.16, siteGridFrameSpan * 0.025));
            modelTextHeight = Math.Max(
                siteGridTextFloor,
                Math.Min(modelTextHeight, siteGridTextCeiling));
            double insideOffsetLimit = Math.Max(
                0.01,
                Math.Min(siteGridMinimumSpacing * 0.35, siteGridFrameSpan * 0.08));
            double insideOffset = Math.Min(
                Math.Max(modelTextHeight * 1.35, 0.01),
                insideOffsetLimit);
'@ -replace "`n","`r`n"

    $site = $site.Substring(0,$heightStart) +
        $newSiteHeight.TrimEnd() +
        $site.Substring($insideEnd + 1)
}

$oldLinePresentation = @'
            line.SetDatabaseDefaults(database);
            line.LayerId = boundary.LayerId;
            line.Elevation = boundary.Elevation;
'@ -replace "`n","`r`n"
$newLinePresentation = @'
            line.SetDatabaseDefaults(database);
            line.LayerId = boundary.LayerId;
            line.Color = boundary.Color;
            line.LineWeight = boundary.LineWeight;
            line.Elevation = boundary.Elevation;
'@ -replace "`n","`r`n"
if (-not $site.Contains('line.Color = boundary.Color;')) {
    if (-not $site.Contains($oldLinePresentation)) {
        throw 'August 19 Site Grid display fix failed: grid-line presentation anchor was not found.'
    }
    $site = $site.Replace($oldLinePresentation,$newLinePresentation)
}

$oldLabelPresentation = @'
            label.SetDatabaseDefaults(database);
            label.LayerId = boundary.LayerId;
            label.Location = location;
'@ -replace "`n","`r`n"
$newLabelPresentation = @'
            label.SetDatabaseDefaults(database);
            label.LayerId = boundary.LayerId;
            label.Color = boundary.Color;
            label.TextStyleId = database.Textstyle;
            label.Normal = Vector3d.ZAxis;
            label.Location = location;
'@ -replace "`n","`r`n"
if (-not $site.Contains('label.Color = boundary.Color;')) {
    if (-not $site.Contains($oldLabelPresentation)) {
        throw 'August 19 Site Grid display fix failed: coordinate-label presentation anchor was not found.'
    }
    $site = $site.Replace($oldLabelPresentation,$newLabelPresentation)
}

$oldPointPresentation = @'
                        point.SetDatabaseDefaults(database);
                        point.LayerId = boundary.LayerId;
                        Append(modelSpace, transaction, point);
'@ -replace "`n","`r`n"
$newPointPresentation = @'
                        point.SetDatabaseDefaults(database);
                        point.LayerId = boundary.LayerId;
                        point.Color = boundary.Color;
                        Append(modelSpace, transaction, point);
'@ -replace "`n","`r`n"
if (-not $site.Contains('point.Color = boundary.Color;')) {
    if (-not $site.Contains($oldPointPresentation)) {
        throw 'August 19 Site Grid display fix failed: grid-point presentation anchor was not found.'
    }
    $site = $site.Replace($oldPointPresentation,$newPointPresentation)
}

[System.IO.File]::WriteAllText($sitePath,$site,$utf8)
$siteCheck = [System.IO.File]::ReadAllText($sitePath)
foreach ($marker in @(
    'double siteGridMinimumSpacing = Math.Max(',
    'double siteGridFrameSpan = Math.Max(',
    'double siteGridTextCeiling = Math.Max(',
    'double insideOffsetLimit = Math.Max(',
    'line.Color = boundary.Color;',
    'line.LineWeight = boundary.LineWeight;',
    'label.Color = boundary.Color;',
    'label.TextStyleId = database.Textstyle;',
    'label.Normal = Vector3d.ZAxis;',
    'point.Color = boundary.Color;')) {
    if (-not $siteCheck.Contains($marker)) {
        throw "August 19 Site Grid display fix failed: final marker missing: $marker"
    }
}
if ($siteCheck.Contains('siteGridMinimumSpacing * 0.40')) {
    throw 'August 19 Site Grid display fix failed: the oversized 40-percent text floor still survives.'
}

Write-Host 'August 19 Vertex display finalizer removed all obsolete yFirst expressions, value swaps and declarations before compilation.' -ForegroundColor Green
Write-Host 'DisplayX/DisplayY remain solely responsible for the saved Swap X/Y and Reverse signs behavior.' -ForegroundColor Green
Write-Host 'August 19 Grid surface dropdowns now build their choices inline; no local choice variable can be referenced before declaration.' -ForegroundColor Green
Write-Host 'August 19 Site Grid finalizer now accepts the current staged height formula structurally and clamps labels to a readable in-frame size.' -ForegroundColor Green
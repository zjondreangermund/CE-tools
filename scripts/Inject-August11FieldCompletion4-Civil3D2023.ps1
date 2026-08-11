[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$src = Join-Path $root 'src\CE.Tools.Civil3D'
function Need([string]$name) {
    $path = Join-Path $src $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "August11 completion-4 source missing: $path" }
    return $path
}
function ReadText([string]$path) { [System.IO.File]::ReadAllText($path) }
function WriteText([string]$path,[string]$text) { [System.IO.File]::WriteAllText($path,$text,[System.Text.UTF8Encoding]::new($false)) }

$roadLayout = Need 'RoadLayoutProductionCommands.cs'
$roadHub = Need 'August11RoadCompletionCommands.cs'
$bellmouth = Need 'August11BellmouthTrimCommands.cs'
$production = Need 'August11ProductionCentreCommands.cs'
$survey = Need 'August11SurveyRuntimeCommands.cs'
$plugin = Need 'PluginEntry.cs'

# Fix the displaced bellmouth Arc geometry. The old code selected hard-coded
# global angle quadrants after the local Y axis could be flipped by the side-road
# tangent. Build the tangent endpoints first, then derive the arc angles from the
# actual local basis so radius/half-width and drawn geometry stay consistent.
$text = ReadText $roadLayout
$oldArc = @'
        private static Arc CreateLocalQuarterArc(Database database, Point3d centre, Vector2d x, Vector2d y, int sx, int sy, double halfWidth, double radius)
        {
            Vector2d ux = x.GetNormal();
            Vector2d uy = y.GetNormal();
            Vector2d local = ux.MultiplyBy(sx * (halfWidth + radius)) + uy.MultiplyBy(sy * (halfWidth + radius));
            Point3d arcCentre = centre + new Vector3d(local.X, local.Y, 0.0);
            double baseAngle = Math.Atan2(ux.Y, ux.X);
            double start;
            double end;
            if (sx > 0 && sy > 0) { start = baseAngle + Math.PI; end = baseAngle + Math.PI * 1.5; }
            else if (sx < 0 && sy > 0) { start = baseAngle + Math.PI * 1.5; end = baseAngle + Math.PI * 2.0; }
            else if (sx < 0 && sy < 0) { start = baseAngle; end = baseAngle + Math.PI * 0.5; }
            else { start = baseAngle + Math.PI * 0.5; end = baseAngle + Math.PI; }
            var arc = new Arc(arcCentre, Vector3d.ZAxis, radius, NormalizeAngle(start), NormalizeAngle(end));
            arc.SetDatabaseDefaults(database);
            return arc;
        }
'@
$newArc = @'
        private static Arc CreateLocalQuarterArc(Database database, Point3d centre, Vector2d x, Vector2d y, int sx, int sy, double halfWidth, double radius)
        {
            Vector2d ux = x.GetNormal();
            Vector2d uy = y.GetNormal();
            Vector2d centreOffset = ux.MultiplyBy(sx * (halfWidth + radius)) + uy.MultiplyBy(sy * (halfWidth + radius));
            Point3d arcCentre = centre + new Vector3d(centreOffset.X, centreOffset.Y, 0.0);

            Vector2d firstOffset = ux.MultiplyBy(sx * (halfWidth + radius)) + uy.MultiplyBy(sy * halfWidth);
            Vector2d secondOffset = ux.MultiplyBy(sx * halfWidth) + uy.MultiplyBy(sy * (halfWidth + radius));
            Point3d first = centre + new Vector3d(firstOffset.X, firstOffset.Y, 0.0);
            Point3d second = centre + new Vector3d(secondOffset.X, secondOffset.Y, 0.0);

            Vector2d r1 = new Vector2d(first.X - arcCentre.X, first.Y - arcCentre.Y);
            Vector2d r2 = new Vector2d(second.X - arcCentre.X, second.Y - arcCentre.Y);
            double a1 = NormalizeAngle(Math.Atan2(r1.Y, r1.X));
            double a2 = NormalizeAngle(Math.Atan2(r2.Y, r2.X));
            double cross = r1.X * r2.Y - r1.Y * r2.X;
            double start = cross >= 0.0 ? a1 : a2;
            double end = cross >= 0.0 ? a2 : a1;
            if (end <= start) end += Math.PI * 2.0;

            var arc = new Arc(arcCentre, Vector3d.ZAxis, radius, start, end);
            arc.SetDatabaseDefaults(database);
            return arc;
        }
'@
$oldArcValue = $oldArc.TrimEnd("`r","`n")
if ($text.Contains($oldArcValue)) {
    $text = $text.Replace($oldArcValue,$newArc.TrimEnd("`r","`n"))
    WriteText $roadLayout $text
    Write-Host 'Repaired bellmouth quarter arcs from actual local tangent endpoints.' -ForegroundColor Green
}
elseif ($text.Contains('Vector2d firstOffset = ux.MultiplyBy')) { Write-Host 'Bellmouth endpoint-derived arc geometry is already applied.' -ForegroundColor DarkGreen }
else { throw 'Road bellmouth CreateLocalQuarterArc helper marker was not found.' }

# Make the tangent-edge trim command visible in Road Completion and Road Production.
$text = ReadText $roadHub
$anchor = '                    new DisciplineWorkflowAction("Create junction trim boundaries", "CE_JUNCTIONTRIMBOUNDARIES", "Create non-plot closed boundaries around multiple junctions and continue into trim-inside.", "03 Junctions"),'
if (-not $text.Contains('"CE_BELLMOUTHTRIMEDGES"')) {
    if (-not $text.Contains($anchor)) { throw 'Road Completion junction trim marker not found.' }
    $insert = '                    new DisciplineWorkflowAction("Trim road / shoulder edges to bellmouth tangent points", "CE_BELLMOUTHTRIMEDGES", "Use corrected bellmouth start/end tangencies to trim multiple road-edge and sidewalk/shoulder curves.", "03 Junctions"),'
    $text = $text.Replace($anchor,$anchor + "`r`n" + $insert)
    WriteText $roadHub $text
    Write-Host 'Added exact bellmouth tangent trim to Road Completion.' -ForegroundColor Green
}

$text = ReadText $production
$anchor = '                Action("Road Continuity / Junction Finish", "CE_ROADAUG11TOOLS", "Join reserve centrelines, outside offsets, junction trim boundaries and route annotation.", "02 PREPARE"),'
if (-not $text.Contains('Action("Bellmouth Tangent Trim", "CE_BELLMOUTHTRIMEDGES"')) {
    if (-not $text.Contains($anchor)) { throw 'Road Production road-completion marker not found.' }
    $insert = '                Action("Bellmouth Tangent Trim", "CE_BELLMOUTHTRIMEDGES", "Trim road edges and sidewalk/shoulder edges exactly to generated bellmouth tangent stations.", "02 PREPARE"),'
    $text = $text.Replace($anchor,$anchor + "`r`n" + $insert)
    WriteText $production $text
    Write-Host 'Added Bellmouth Tangent Trim to Road Production Centre.' -ForegroundColor Green
}

# Capture initial COGO label offsets immediately after setting-out is first
# refreshed, so Restore means the generated/original CE position rather than the
# position at the first later overlap command.
$text = ReadText $survey
$anchor = '                    try { VertexSettingOutCommands.RefreshAll(_document); } catch { }'
if (-not $text.Contains('try { August11SurveyRuntimeCommands.CaptureCogoInitialOffsets(_document); } catch { }')) {
    if (-not $text.Contains($anchor)) { throw 'Immediate vertex-setting-out refresh marker not found.' }
    $text = $text.Replace($anchor,$anchor + "`r`n                    try { August11SurveyRuntimeCommands.CaptureCogoInitialOffsets(_document); } catch { }")
    WriteText $survey $text
    Write-Host 'Integrated initial COGO label-position capture immediately after setting-out.' -ForegroundColor Green
}

# Put a clear CE Tools Home / welcome entry on the main CE TOOLS tab as well as
# the dedicated CE PRODUCTION tab. Also expose discipline presets and final road
# profile/bellmouth tools next to the established expert commands.
$text = ReadText $plugin
$projectAnchor = '                        Cmd("Phase 1 Utilities", "CE_PHASE1 ", "Open every original CE Tools Phase 1 utility family in one visual hub."),'
if (-not $text.Contains('Cmd("CE Tools Home", "CE_WELCOME ')) {
    if (-not $text.Contains($projectAnchor)) { throw 'Main Project menu Phase 1 marker not found.' }
    $text = $text.Replace($projectAnchor,'                        Cmd("CE Tools Home", "CE_WELCOME ", "Open the CE Tools welcome screen with Production Centre and Engineering Intelligence Centre."),' + "`r`n" + $projectAnchor)
    Write-Host 'Added CE Tools Home / welcome button to main ribbon.' -ForegroundColor Green
}
$stylesAnchor = '                        Cmd("Project Style Centre", "CE_PROJECTSTYLES ", "Select alignment, profile, corridor, point and network styles."),'
if (-not $text.Contains('Cmd("Discipline Style Presets", "CE_DISCIPLINESTYLEPRESETS ')) {
    if (-not $text.Contains($stylesAnchor)) { throw 'Project Styles menu marker not found.' }
    $text = $text.Replace($stylesAnchor,$stylesAnchor + "`r`n                        Cmd(\"Discipline Style Presets\", \"CE_DISCIPLINESTYLEPRESETS \", \"Store and activate independent Roads/SW/Sewer/Water/Platform style selections from the shared style library.\"),")
}
$roadProfileAnchor = '                        Cmd("Create Road Profiles", "CE_ROADPROFILES ", "Create existing-ground profiles and ordered profile views."),'
if (-not $text.Contains('Cmd("Complete Final Road Profile", "CE_ROADPROFILEFULL ')) {
    if (-not $text.Contains($roadProfileAnchor)) { throw 'Road Production profile menu marker not found.' }
    $insert = @'
                        Cmd("Complete Final Road Profile", "CE_ROADPROFILEFULL ", "Create EG profile/profile view, editable final design profile and parabolic vertical curves."),
                        Cmd("Add / Repair Vertical Curves", "CE_ROADVERTICALCURVES ", "Add symmetric parabolic vertical curves to eligible final-road PVIs."),
                        Cmd("Trim Edges to Bellmouths", "CE_BELLMOUTHTRIMEDGES ", "Trim multiple road/shoulder edges to corrected bellmouth tangent points."),
'@
    $text = $text.Replace($roadProfileAnchor,$roadProfileAnchor + "`r`n" + $insert.TrimEnd("`r","`n"))
}
WriteText $plugin $text

Write-Host 'August 11 field completion pass 4 is ready for validation.' -ForegroundColor Cyan

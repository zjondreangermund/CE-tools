[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$src = Join-Path $root 'src\CE.Tools.Civil3D'
$utf8 = New-Object System.Text.UTF8Encoding($false)

function Required([string]$name) {
    $path = Join-Path $src $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "August 20 field-recovery source missing: $path"
    }
    return $path
}
function ReadText([string]$path) {
    return [System.IO.File]::ReadAllText($path) -replace "`r?`n", "`r`n"
}
function WriteText([string]$path,[string]$text) {
    [System.IO.File]::WriteAllText($path,($text -replace "`r?`n","`r`n"),$utf8)
}
function ReplaceMethodBody([string]$text,[string]$marker,[string]$body,[string]$label) {
    $start = $text.IndexOf($marker,[StringComparison]::Ordinal)
    if ($start -lt 0) { throw "August 20 field-recovery method marker not found: $label" }
    $second = $text.IndexOf($marker,$start + $marker.Length,[StringComparison]::Ordinal)
    if ($second -ge 0) { throw "August 20 field-recovery method marker ambiguous: $label" }
    $open = $text.IndexOf('{',$start)
    if ($open -lt 0) { throw "August 20 field-recovery opening brace not found: $label" }
    $depth = 0
    $close = -1
    for ($i=$open; $i -lt $text.Length; $i++) {
        if ($text[$i] -eq '{') { $depth++ }
        elseif ($text[$i] -eq '}') {
            $depth--
            if ($depth -eq 0) { $close = $i; break }
        }
    }
    if ($close -lt 0) { throw "August 20 field-recovery closing brace not found: $label" }
    $normalized = $body -replace "`r?`n","`r`n"
    return $text.Substring(0,$open+1) + "`r`n" + $normalized.Trim("`r","`n") + "`r`n        " + $text.Substring($close)
}
function ReplaceRequired([string]$text,[string]$old,[string]$new,[string]$label) {
    $old = $old -replace "`r?`n","`r`n"
    $new = $new -replace "`r?`n","`r`n"
    if ($text.Contains($new)) { return $text }
    if (-not $text.Contains($old)) { throw "August 20 field-recovery anchor missing: $label" }
    return $text.Replace($old,$new)
}

$backgroundPath = Required 'BackgroundPreparationCommands.cs'
$siteGridPath = Required 'August12SurveySiteGridCommands.cs'
$breakPath = Required 'PolylineNetworkPreparationCommands.cs'
$sequencePath = Required 'SewerSequenceCommands.cs'
$alignmentPath = Required 'SewerBranchAlignmentCommands.cs'
$geometryPath = Required 'August20GeometryFirstSewerCommands.cs'
$roadMenuPath = Required 'August13RoadProductionCentres.cs'
$sewerMenuPath = Required 'August14StructuredDisciplineProductionCentres.cs'

# -----------------------------------------------------------------------------
# 1. Background Tools field fix.
# The previous finalizer checked for the menu command string after adding the menu,
# so the actual CommandMethod declarations were skipped. Check the real command
# declarations instead and add only what AutoCAD needs to register.
# -----------------------------------------------------------------------------
$background = ReadText $backgroundPath
if (-not $background.Contains('[CommandMethod("CE_TOOLS", "CE_BGFREEZEALLHATCH"')) {
    $marker = '        [CommandMethod("CE_TOOLS", "CE_BGSCALECORRECTION", CommandFlags.Modal | CommandFlags.Redraw)]'
    if (-not $background.Contains($marker)) { throw 'Background field command insertion marker missing.' }
    foreach ($helper in @(
        'private static void MoveSelectedOrAllTextToFrozenLayer(',
        'private static void MoveSelectedOrAllAttributesToFrozenLayer(')) {
        if (-not $background.Contains($helper)) {
            throw "Background field helper missing after prior staged repairs: $helper"
        }
    }
    $commands = @'
        [CommandMethod("CE_TOOLS", "CE_BGFREEZEALLHATCH", CommandFlags.Modal | CommandFlags.Redraw)]
        public void FreezeAllHatches()
        {
            MoveToFrozenLayer(
                "CE-BG-ALL-HATCHES-FROZEN",
                delegate(Entity entity) { return entity is Hatch; },
                "hatch");
        }

        [CommandMethod("CE_TOOLS", "CE_BGFREEZETEXT", CommandFlags.Modal | CommandFlags.Redraw)]
        public void FreezeAllOrSelectedText()
        {
            MoveSelectedOrAllTextToFrozenLayer("CE-BG-TEXT-FROZEN");
        }

        [CommandMethod("CE_TOOLS", "CE_BGFREEZEATTRIBUTES", CommandFlags.Modal | CommandFlags.Redraw)]
        public void FreezeAllOrSelectedAttributes()
        {
            MoveSelectedOrAllAttributesToFrozenLayer("CE-BG-ATTRIBUTES-FROZEN");
        }

'@
    $background = $background.Replace($marker,$commands + $marker)
}
WriteText $backgroundPath $background

# -----------------------------------------------------------------------------
# 2. Site Grid field visibility.
# Labels already exist when AUDIT later makes them appear, so mark every appended
# child entity graphics-dirty and explicitly update the screen after REGEN. This
# leaves coordinate labels independent from any optional surfaces.
# -----------------------------------------------------------------------------
$site = ReadText $siteGridPath
$appendBody = @'
            ObjectId id = modelSpace.AppendEntity(entity);
            transaction.AddNewlyCreatedDBObject(entity, true);
            try { entity.RecordGraphicsModified(true); }
            catch { }
            return id;
'@
$site = ReplaceMethodBody $site '        private static ObjectId Append(' $appendBody 'Site Grid child graphics registration'
if (-not $site.Contains('AcApplication.UpdateScreen(); // CE_FIELD_RECOVERY_SITEGRID')) {
    $site = $site.Replace(
        '            document.Editor.Regen();',
        "            document.Editor.Regen();`r`n            try { AcApplication.UpdateScreen(); } catch { } // CE_FIELD_RECOVERY_SITEGRID")
}
WriteText $siteGridPath $site

# -----------------------------------------------------------------------------
# 3. Break at Crossings / Junctions field safety.
# Phase A commits fully validated replacement curves while the original remains.
# Phase B reopens the committed replacements, verifies total curve length, then
# erases only that original. If Phase B fails, replacements are cleaned up and the
# original survives. This prevents a bad multi-crossing source disappearing.
# -----------------------------------------------------------------------------
$breaks = ReadText $breakPath
$splitBody = @'
            replaced = 0;
            created = 0;

            foreach (KeyValuePair<ObjectId, List<Point3d>> item in splitPoints)
            {
                if (item.Value == null || item.Value.Count == 0) continue;

                var replacementIds = new List<ObjectId>();
                DBObjectCollection transientSegments = null;
                try
                {
                    double originalLength;
                    using (Transaction createTransaction =
                        database.TransactionManager.StartTransaction())
                    {
                        Curve source = createTransaction.GetObject(
                            item.Key,
                            OpenMode.ForRead,
                            false) as Curve;
                        if (source == null || source.IsErased) continue;

                        originalLength = source.GetDistanceAtParameter(source.EndParam);
                        if (originalLength <= Tolerance) continue;

                        Point3dCollection points = new Point3dCollection(
                            item.Value
                                .Select(point => source.GetClosestPointTo(point, false))
                                .OrderBy(point => source.GetDistAtPoint(point))
                                .ToArray());

                        transientSegments = source.GetSplitCurves(points);
                        if (transientSegments == null || transientSegments.Count < 2)
                            throw new InvalidOperationException(
                                "A selected polyline could not be split safely at all calculated junctions.");

                        var validated = new List<Curve>();
                        double candidateLength = 0.0;
                        foreach (DBObject value in transientSegments)
                        {
                            Curve segment = value as Curve;
                            if (segment == null)
                                throw new InvalidOperationException(
                                    "A split result was not a curve.");
                            double length = segment.GetDistanceAtParameter(segment.EndParam);
                            if (length <= Tolerance ||
                                double.IsNaN(length) ||
                                double.IsInfinity(length))
                                throw new InvalidOperationException(
                                    "A zero/invalid-length split result was rejected.");
                            candidateLength += length;
                            validated.Add(segment);
                        }

                        double lengthTolerance = Math.Max(
                            0.001,
                            originalLength * 0.00001);
                        if (Math.Abs(candidateLength - originalLength) >
                            lengthTolerance * Math.Max(2, validated.Count))
                            throw new InvalidOperationException(
                                "Split replacements failed the source-length validation.");

                        BlockTableRecord space = createTransaction.GetObject(
                            database.CurrentSpaceId,
                            OpenMode.ForWrite,
                            false) as BlockTableRecord;
                        if (space == null)
                            throw new InvalidOperationException(
                                "The current drawing space is unavailable.");

                        foreach (Curve segment in validated)
                        {
                            space.AppendEntity(segment);
                            createTransaction.AddNewlyCreatedDBObject(segment, true);
                            replacementIds.Add(segment.ObjectId);
                        }

                        createTransaction.Commit();
                        transientSegments = null;
                    }

                    bool verified = false;
                    using (Transaction eraseTransaction =
                        database.TransactionManager.StartTransaction())
                    {
                        Curve source = eraseTransaction.GetObject(
                            item.Key,
                            OpenMode.ForRead,
                            false) as Curve;
                        if (source == null || source.IsErased)
                            throw new InvalidOperationException(
                                "The original source changed before split verification completed.");

                        double sourceLength = source.GetDistanceAtParameter(source.EndParam);
                        double committedLength = 0.0;
                        int committedCount = 0;
                        foreach (ObjectId replacementId in replacementIds)
                        {
                            Curve replacement = eraseTransaction.GetObject(
                                replacementId,
                                OpenMode.ForRead,
                                false) as Curve;
                            if (replacement == null || replacement.IsErased)
                                throw new InvalidOperationException(
                                    "A committed split replacement is unavailable.");
                            double length = replacement.GetDistanceAtParameter(
                                replacement.EndParam);
                            if (length <= Tolerance ||
                                double.IsNaN(length) ||
                                double.IsInfinity(length))
                                throw new InvalidOperationException(
                                    "A committed split replacement is invalid.");
                            committedLength += length;
                            committedCount++;
                        }

                        double lengthTolerance = Math.Max(
                            0.001,
                            sourceLength * 0.00001);
                        if (committedCount < 2 ||
                            Math.Abs(committedLength - sourceLength) >
                                lengthTolerance * Math.Max(2, committedCount))
                            throw new InvalidOperationException(
                                "Committed split replacements failed final verification.");

                        source.UpgradeOpen();
                        source.Erase();
                        eraseTransaction.Commit();
                        verified = true;
                    }

                    if (verified)
                    {
                        replaced++;
                        created += replacementIds.Count;
                    }
                }
                catch
                {
                    if (transientSegments != null)
                    {
                        foreach (DBObject value in transientSegments)
                        {
                            try
                            {
                                if (value != null && value.ObjectId.IsNull)
                                    value.Dispose();
                            }
                            catch { }
                        }
                    }

                    if (replacementIds.Count > 0)
                    {
                        try
                        {
                            using (Transaction cleanupTransaction =
                                database.TransactionManager.StartTransaction())
                            {
                                foreach (ObjectId replacementId in replacementIds)
                                {
                                    if (replacementId.IsNull || replacementId.IsErased)
                                        continue;
                                    try
                                    {
                                        Entity replacement = cleanupTransaction.GetObject(
                                            replacementId,
                                            OpenMode.ForWrite,
                                            false) as Entity;
                                        if (replacement != null && !replacement.IsErased)
                                            replacement.Erase();
                                    }
                                    catch { }
                                }
                                cleanupTransaction.Commit();
                            }
                        }
                        catch { }
                    }
                }
            }
'@
$breaks = ReplaceMethodBody $breaks '        private static void ApplySplits(' $splitBody 'Break-at-crossings two-phase source preservation'
WriteText $breakPath $breaks

# -----------------------------------------------------------------------------
# 4. Sewer Sequence labels.
# Keep the fatal-safety separation introduced earlier, but queue CE_SEWLABELS as
# a separate command after sequencing instead of synchronously re-entering Civil
# label creation inside the sequence transaction/call stack.
# -----------------------------------------------------------------------------
$sequence = ReadText $sequencePath
$wholeOld = @'
                editor.WriteMessage(
                    "\nCE_SEWSEQ safety: sequencing/renaming is complete. Run sewer labels, alignments and linked refresh as separate commands after reviewing the network.");
'@
$wholeNew = @'
                editor.WriteMessage(
                    "\nCE_SEWSEQ complete. Sequencing/renaming is committed; sewer pipe/structure labels are queued as a separate safe command.");
                document.SendStringToExecute("CE_SEWLABELS ", true, false, false);
'@
if ($sequence.Contains($wholeOld)) { $sequence = $sequence.Replace($wholeOld,$wholeNew) }
$selectedOld = @'
                    editor.WriteMessage(
                        "\nCE_SEWSEQ safety: selected-path sequencing/renaming is complete. Run sewer labels, alignments and linked refresh separately.");
'@
$selectedNew = @'
                    editor.WriteMessage(
                        "\nCE_SEWSEQ selected path complete. Sewer pipe/structure labels are queued as a separate safe command.");
                    document.SendStringToExecute("CE_SEWLABELS ", true, false, false);
'@
if ($sequence.Contains($selectedOld)) { $sequence = $sequence.Replace($selectedOld,$selectedNew) }
if (-not $sequence.Contains('document.SendStringToExecute("CE_SEWLABELS ", true, false, false);')) {
    throw 'Sewer Sequence field recovery could not install deferred CE_SEWLABELS.'
}
WriteText $sequencePath $sequence

# -----------------------------------------------------------------------------
# 5. CE_SEWALIGN eKeyNotFound field fix.
# CivilDocument.GetAlignmentIds() can temporarily retain an erased/stale id in the
# same transaction. Never let one stale Civil alignment/model-space id abort the
# complete branch-alignment transaction.
# -----------------------------------------------------------------------------
$alignment = ReadText $alignmentPath
$resolveOld = @'
                var alignment = transaction.GetObject(
                    alignmentId,
                    OpenMode.ForRead,
                    false) as CivilAlignment;
                if (alignment != null)
                {
                    existing.Add(alignment.Name);
                }
'@
$resolveNew = @'
                if (alignmentId.IsNull || alignmentId.IsErased) continue;
                CivilAlignment alignment = null;
                try
                {
                    alignment = transaction.GetObject(
                        alignmentId,
                        OpenMode.ForRead,
                        false) as CivilAlignment;
                }
                catch
                {
                    continue; // CE_FIELD_RECOVERY: stale Civil alignment id
                }
                if (alignment != null)
                {
                    existing.Add(alignment.Name);
                }
'@
$alignment = ReplaceRequired $alignment $resolveOld $resolveNew 'CE_SEWALIGN ResolveAlignmentName stale-id guard'
$removeAlignmentOld = @'
                DBObject alignment = transaction.GetObject(
                    alignmentId,
                    OpenMode.ForRead,
                    false);
                if (HasTag(alignment, branchKey, "Alignment"))
                {
                    alignment.UpgradeOpen();
                    alignment.Erase();
                }
'@
$removeAlignmentNew = @'
                if (alignmentId.IsNull || alignmentId.IsErased) continue;
                DBObject alignment = null;
                try
                {
                    alignment = transaction.GetObject(
                        alignmentId,
                        OpenMode.ForRead,
                        false);
                }
                catch
                {
                    continue; // CE_FIELD_RECOVERY: stale Civil alignment id
                }
                if (alignment != null && HasTag(alignment, branchKey, "Alignment"))
                {
                    alignment.UpgradeOpen();
                    alignment.Erase();
                }
'@
$alignment = ReplaceRequired $alignment $removeAlignmentOld $removeAlignmentNew 'CE_SEWALIGN generated alignment stale-id guard'
$removeEntityOld = @'
                DBObject entity = transaction.GetObject(
                    entityId,
                    OpenMode.ForRead,
                    false);
                if (HasTag(entity, branchKey, "Label"))
                {
                    entity.UpgradeOpen();
                    entity.Erase();
                }
'@
$removeEntityNew = @'
                if (entityId.IsNull || entityId.IsErased) continue;
                DBObject entity = null;
                try
                {
                    entity = transaction.GetObject(
                        entityId,
                        OpenMode.ForRead,
                        false);
                }
                catch
                {
                    continue; // CE_FIELD_RECOVERY: stale model-space id
                }
                if (entity != null && HasTag(entity, branchKey, "Label"))
                {
                    entity.UpgradeOpen();
                    entity.Erase();
                }
'@
$alignment = ReplaceRequired $alignment $removeEntityOld $removeEntityNew 'CE_SEWALIGN generated label stale-id guard'
WriteText $alignmentPath $alignment

# -----------------------------------------------------------------------------
# 6. Midblock / Road Reserve fatal-error containment.
# Field testing shows that even read-only Civil Surface access during these layout
# planners can terminate Civil 3D 2023. Force the planning commands to geometry-
# only mode: no surface catalogue lookup, no surface resolution, no OpenSurface,
# no TryElevation/FindElevationAtXY path and no downhill orientation. Flow can be
# corrected afterwards with CE_FLOWTOOUTLET.
# -----------------------------------------------------------------------------
$geometry = ReadText $geometryPath
$geometry = $geometry.Replace(
    '            List<string> surfaceChoices = SurfaceChoices(document);',
    '            List<string> surfaceChoices = new List<string> { August20SurfaceChoice.None }; // CE_FIELD_RECOVERY_GEOMETRY_ONLY')
$resolveSurfacePattern = 'ObjectId\s+surfaceId\s*=\s*August20SurfaceChoice\.ResolveSurfaceId\(\s*document,\s*model\.Text\("Surface"\)\s*\);'
$geometry = [regex]::Replace(
    $geometry,
    $resolveSurfacePattern,
    'ObjectId surfaceId = ObjectId.Null; // CE_FIELD_RECOVERY: no Civil Surface access',
    [System.Text.RegularExpressions.RegexOptions]::Singleline)
$openSurfacePattern = 'CivilSurface\s+surface\s*=\s*OpenSurface\(\s*(?:planning|transaction),\s*surfaceId\s*\);'
$geometry = [regex]::Replace(
    $geometry,
    $openSurfacePattern,
    'CivilSurface surface = null; // CE_FIELD_RECOVERY: geometry-only planning',
    [System.Text.RegularExpressions.RegexOptions]::Singleline)
$geometry = $geometry.Replace(
    '                            OrientDownhill(ref routeLine, surface);',
    '                            // CE_FIELD_RECOVERY: flow orientation is deferred to CE_FLOWTOOUTLET.')
$geometry = $geometry.Replace(
    '                        OrientDownhill(ref routeLine, surface);',
    '                        // CE_FIELD_RECOVERY: flow orientation is deferred to CE_FLOWTOOUTLET.')
WriteText $geometryPath $geometry

# Route production menus directly to the unique geometry-only public commands so
# field users do not enter older legacy road-reserve/combined command owners.
$roadMenu = ReadText $roadMenuPath
$roadMenu = $roadMenu.Replace(
    'A("CE-Road Reserve Centrelines", "CE_ROADRESERVECENTERLINES"',
    'A("CE-Road Reserve Centrelines", "CE_ROADCENTERLINEPOLY"')
WriteText $roadMenuPath $roadMenu

$sewerMenu = ReadText $sewerMenuPath
$combinedPattern = '(?m)^\s*A\("CE-Midblock / Road-Reserve Sewer Route",\s*"CE_MIDBLOCKSEWERPRODUCTION",[^\r\n]*\),\s*$'
if ([regex]::IsMatch($sewerMenu,$combinedPattern)) {
    $replacement = @'
                    A("CE-Midblock Sewer Route", "CE_SEWERLAYOUTMIDBLOCK", "Create geometry-only Midblock sewer planning polylines/manhole circles. Civil network conversion is separate.", "02 PREPARE"),
                    A("CE-Road Reserve Sewer Route", "CE_SEWERLAYOUTROADRESERVE", "Create geometry-only Road Reserve sewer planning polylines/manhole circles. Civil network conversion is separate.", "02 PREPARE"),
'@
    $sewerMenu = [regex]::Replace($sewerMenu,$combinedPattern,$replacement.TrimEnd("`r","`n"))
}
WriteText $sewerMenuPath $sewerMenu

# -----------------------------------------------------------------------------
# Final field guards. Refuse compilation if a screenshot-reported regression is
# still present after every staged repair.
# -----------------------------------------------------------------------------
$backgroundCheck = ReadText $backgroundPath
foreach ($command in @('CE_BGFREEZEALLHATCH','CE_BGFREEZETEXT','CE_BGFREEZEATTRIBUTES')) {
    if (-not $backgroundCheck.Contains('[CommandMethod("CE_TOOLS", "' + $command + '"')) {
        throw "Background registered CommandMethod missing after field recovery: $command"
    }
}

$siteCheck = ReadText $siteGridPath
if (-not $siteCheck.Contains('entity.RecordGraphicsModified(true);') -or
    -not $siteCheck.Contains('CE_FIELD_RECOVERY_SITEGRID')) {
    throw 'Site Grid graphics/label visibility field guard missing.'
}

$breakCheck = ReadText $breakPath
foreach ($required in @(
    'replacementIds = new List<ObjectId>()',
    'using (Transaction createTransaction =',
    'using (Transaction eraseTransaction =',
    'source.Erase();',
    'cleanupTransaction')) {
    if (-not $breakCheck.Contains($required)) {
        throw "Break-at-crossings two-phase guard missing: $required"
    }
}

$sequenceCheck = ReadText $sequencePath
if (-not $sequenceCheck.Contains('SendStringToExecute("CE_SEWLABELS ", true, false, false)')) {
    throw 'CE_SEWSEQ deferred sewer-label command is missing.'
}

$alignmentCheck = ReadText $alignmentPath
if (-not $alignmentCheck.Contains('CE_FIELD_RECOVERY: stale Civil alignment id') -or
    -not $alignmentCheck.Contains('CE_FIELD_RECOVERY: stale model-space id')) {
    throw 'CE_SEWALIGN stale-id/eKeyNotFound guards are missing.'
}

$geometryCheck = ReadText $geometryPath
foreach ($method in @('RunMidblock(bool legacyEntry)','RunRoadReserve(bool legacyEntry)')) {
    $start = $geometryCheck.IndexOf($method,[StringComparison]::Ordinal)
    if ($start -lt 0) { throw "Geometry-only field method missing: $method" }
    $next = $geometryCheck.IndexOf('        private static ',$start + $method.Length,[StringComparison]::Ordinal)
    if ($next -lt 0) { $next = $geometryCheck.Length }
    $methodText = $geometryCheck.Substring($start,$next-$start)
    foreach ($unsafe in @('SurfaceChoices(document)','ResolveSurfaceId(','OpenSurface(','OrientDownhill(ref routeLine, surface)')) {
        if ($methodText.Contains($unsafe)) {
            throw "Civil Surface access survived field recovery in $method : $unsafe"
        }
    }
}

$roadMenuCheck = ReadText $roadMenuPath
if (-not $roadMenuCheck.Contains('A("CE-Road Reserve Centrelines", "CE_ROADCENTERLINEPOLY"')) {
    throw 'Road Production still routes Road Reserve centrelines to the legacy command.'
}
$sewerMenuCheck = ReadText $sewerMenuPath
foreach ($required in @('"CE_SEWERLAYOUTMIDBLOCK"','"CE_SEWERLAYOUTROADRESERVE"')) {
    if (-not $sewerMenuCheck.Contains($required)) {
        throw "Sewer Layout geometry-only route missing: $required"
    }
}

Write-Host 'August 20 Civil 3D field-recovery pass applied.' -ForegroundColor Green
Write-Host ' - Background Freeze All Hatches/Text/Attributes are real registered AutoCAD commands.' -ForegroundColor Green
Write-Host ' - Site Grid marks generated labels/children graphics-dirty and updates the screen after REGEN.' -ForegroundColor Green
Write-Host ' - Break at Crossings/Junctions uses committed replacements before erasing a verified source.' -ForegroundColor Green
Write-Host ' - CE_SEWSEQ queues CE_SEWLABELS as a separate post-sequence command.' -ForegroundColor Green
Write-Host ' - CE_SEWALIGN ignores stale/erased Civil ids that previously raised eKeyNotFound.' -ForegroundColor Green
Write-Host ' - Midblock/Road Reserve planners run without Civil Surface access; flow orientation is a later step.' -ForegroundColor Green
Write-Host ' - Road Reserve centreline and Sewer Layout menus route directly to geometry-only public commands.' -ForegroundColor Green

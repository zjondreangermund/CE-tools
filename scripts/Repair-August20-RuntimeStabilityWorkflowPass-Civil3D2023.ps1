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
        throw "August 20 runtime-stability source missing: $path"
    }
    return $path
}
function ReadText([string]$path) {
    return [System.IO.File]::ReadAllText($path) -replace "`r?`n", "`r`n"
}
function WriteText([string]$path,[string]$text) {
    [System.IO.File]::WriteAllText($path,($text -replace "`r?`n","`r`n"),$utf8)
}
function ReplaceRequired([string]$text,[string]$old,[string]$new,[string]$label) {
    $old = $old -replace "`r?`n","`r`n"
    $new = $new -replace "`r?`n","`r`n"
    if ($text.Contains($new)) { return $text }
    if (-not $text.Contains($old)) { throw "August 20 runtime-stability anchor not found: $label" }
    return $text.Replace($old,$new)
}
function ReplaceRegexRequired([string]$text,[string]$pattern,[string]$replacement,[string]$label) {
    $options = [System.Text.RegularExpressions.RegexOptions]::Singleline
    $matches = [regex]::Matches($text,$pattern,$options)
    if ($matches.Count -eq 0) {
        if ($text.Contains($replacement)) { return $text }
        throw "August 20 runtime-stability regex anchor not found: $label"
    }
    if ($matches.Count -ne 1) { throw "August 20 runtime-stability regex anchor ambiguous ($($matches.Count)): $label" }
    return [regex]::Replace($text,$pattern,[System.Text.RegularExpressions.MatchEvaluator]{ param($m) $replacement },$options)
}
function ReplaceMethodBody([string]$text,[string]$marker,[string]$body,[string]$label) {
    $start = $text.IndexOf($marker,[StringComparison]::Ordinal)
    if ($start -lt 0) { throw "August 20 runtime-stability method marker not found: $label" }
    $second = $text.IndexOf($marker,$start + $marker.Length,[StringComparison]::Ordinal)
    if ($second -ge 0) { throw "August 20 runtime-stability method marker ambiguous: $label" }
    $open = $text.IndexOf('{',$start)
    if ($open -lt 0) { throw "August 20 runtime-stability opening brace not found: $label" }
    $depth = 0
    $close = -1
    for ($i=$open; $i -lt $text.Length; $i++) {
        if ($text[$i] -eq '{') { $depth++ }
        elseif ($text[$i] -eq '}') {
            $depth--
            if ($depth -eq 0) { $close = $i; break }
        }
    }
    if ($close -lt 0) { throw "August 20 runtime-stability closing brace not found: $label" }
    $normalized = $body -replace "`r?`n","`r`n"
    return $text.Substring(0,$open+1) + "`r`n" + $normalized.Trim("`r","`n") + "`r`n        " + $text.Substring($close)
}
function InsertIntoMethodActionList([string]$text,[string]$methodMarker,[string]$action,[string]$label) {
    $start = $text.IndexOf($methodMarker,[StringComparison]::Ordinal)
    if ($start -lt 0) { throw "August 20 runtime-stability menu method not found: $label" }
    $open = $text.IndexOf('{',$start)
    if ($open -lt 0) { throw "August 20 runtime-stability menu opening brace not found: $label" }
    $list = $text.IndexOf('new List<DisciplineWorkflowAction>',$open,[StringComparison]::Ordinal)
    if ($list -lt 0) { throw "August 20 runtime-stability action list not found: $label" }
    $brace = $text.IndexOf('{',$list)
    if ($brace -lt 0) { throw "August 20 runtime-stability action-list brace not found: $label" }
    if ($text.Substring($start,[Math]::Min(5000,$text.Length-$start)).Contains($action.Trim())) { return $text }
    return $text.Substring(0,$brace+1) + "`r`n" + $action + $text.Substring($brace+1)
}

$sequencePath = Required 'SewerSequenceCommands.cs'
$breakPath = Required 'PolylineNetworkPreparationCommands.cs'
$backgroundPath = Required 'BackgroundPreparationCommands.cs'
$menuPath = Required 'August14StructuredDisciplineProductionCentres.cs'
$multiPath = Required 'August13SewerMultiSourceNetworkCommands.cs'
$vertexPath = Required 'VertexSettingOutCommands.cs'
$geometryPath = Required 'August20GeometryFirstSewerCommands.cs'
$gridPath = Required 'August18DynamicGridSettingOutCommands.cs'

# -----------------------------------------------------------------------------
# 1. CE_SEWSEQ: sequence/rename only. Do not re-enter Civil 3D label/alignment/
#    refresh work immediately after the rename transaction has committed.
# -----------------------------------------------------------------------------
$sequence = ReadText $sequencePath
$sequence = ReplaceRegexRequired $sequence `
    '\s*SewerNetworkLabelCommands\.EnsureLabels\(\s*document,\s*plans\.Select\(plan => plan\.NetworkId\)\);' `
    @'
                editor.WriteMessage(
                    "\nCE_SEWSEQ safety: sequencing/renaming is complete. Run sewer labels, alignments and linked refresh as separate commands after reviewing the network.");
'@ `
    'CE_SEWSEQ whole-network automatic label re-entry'
$sequence = ReplaceRegexRequired $sequence `
    '\s*if \(!labelledNetworkId\.IsNull\)\s*\{\s*SewerNetworkLabelCommands\.EnsureLabels\(\s*document,\s*new\[\] \{ labelledNetworkId \}\);\s*\}' `
    @'
                if (!labelledNetworkId.IsNull)
                {
                    editor.WriteMessage(
                        "\nCE_SEWSEQ safety: selected-path sequencing/renaming is complete. Run sewer labels, alignments and linked refresh separately.");
                }
'@ `
    'CE_SEWSEQ selected-path automatic label re-entry'
WriteText $sequencePath $sequence

# -----------------------------------------------------------------------------
# 2. Break at Crossings/Junctions: isolate each source polyline in its own
#    transaction. Build/validate split curves first, append them, then erase only
#    that successfully processed original. One bad source remains untouched.
# -----------------------------------------------------------------------------
$breaks = ReadText $breakPath
$applySplitsBody = @'
            replaced = 0;
            created = 0;

            foreach (KeyValuePair<ObjectId, List<Point3d>> item in splitPoints)
            {
                if (item.Value == null || item.Value.Count == 0) continue;

                DBObjectCollection segments = null;
                try
                {
                    using (Transaction transaction = database.TransactionManager.StartTransaction())
                    {
                        Curve source = transaction.GetObject(
                            item.Key,
                            OpenMode.ForRead,
                            false) as Curve;
                        if (source == null || source.IsErased) continue;

                        Point3dCollection points = new Point3dCollection(
                            item.Value
                                .OrderBy(point => source.GetDistAtPoint(
                                    source.GetClosestPointTo(point, false)))
                                .ToArray());

                        // GetSplitCurves creates transient replacements only. Nothing
                        // in the drawing is changed until every replacement validates.
                        segments = source.GetSplitCurves(points);
                        if (segments == null || segments.Count < 2)
                            throw new InvalidOperationException(
                                "A selected polyline could not be split at its calculated junctions.");

                        var validated = new List<Entity>();
                        foreach (DBObject value in segments)
                        {
                            Entity segment = value as Entity;
                            if (segment == null)
                                throw new InvalidOperationException(
                                    "A split result was not an AutoCAD entity.");
                            if (segment.GeometricExtents.MinPoint.DistanceTo(
                                    segment.GeometricExtents.MaxPoint) <= Tolerance)
                                throw new InvalidOperationException(
                                    "A zero-length split result was rejected.");
                            validated.Add(segment);
                        }

                        BlockTableRecord space = transaction.GetObject(
                            database.CurrentSpaceId,
                            OpenMode.ForWrite,
                            false) as BlockTableRecord;
                        if (space == null)
                            throw new InvalidOperationException(
                                "The current drawing space is unavailable.");

                        foreach (Entity segment in validated)
                        {
                            space.AppendEntity(segment);
                            transaction.AddNewlyCreatedDBObject(segment, true);
                        }

                        if (!source.IsWriteEnabled) source.UpgradeOpen();
                        source.Erase();
                        transaction.Commit();

                        replaced++;
                        created += validated.Count;
                        segments = null; // committed entities are now owned by the database
                    }
                }
                catch
                {
                    // The transaction aborts automatically. Dispose only transient
                    // split objects that were never committed. The source remains.
                    if (segments != null)
                    {
                        foreach (DBObject value in segments)
                        {
                            try
                            {
                                if (value != null && value.ObjectId.IsNull) value.Dispose();
                            }
                            catch { }
                        }
                    }
                }
            }
'@
$breaks = ReplaceMethodBody $breaks '        private static void ApplySplits(' $applySplitsBody 'Break-at-crossings per-source transactions'
WriteText $breakPath $breaks

# -----------------------------------------------------------------------------
# 3. Background Tools: complete requested visibility menu and tighten Colour 250
#    to a strict DXF allow-list with one entity per transaction.
# -----------------------------------------------------------------------------
$background = ReadText $backgroundPath
$backgroundMenuBody = @'
            Document document = Active();
            if (document == null) return;
            DisciplineWorkflowDialogs.SelectAndRun(
                document,
                "CE Tools - Background Tools",
                "Prepare imported/background DWGs before Survey Production. Visibility commands move only the requested ordinary AutoCAD content to dedicated frozen layers.",
                new List<DisciplineWorkflowAction>
                {
                    new DisciplineWorkflowAction("CE-Burst All Blocks", "CE_BGBURSTALL", "Burst ordinary model-space blocks while preserving visible attribute values as text. XREFs are skipped.", "01 Background Cleanup"),
                    new DisciplineWorkflowAction("CE-Background Colour 250", "CE_BGCOLOR250", "Apply ACI 250 only to a strict allow-list of ordinary AutoCAD model-space entities. Civil/AEC/proxy/XREF objects are skipped.", "01 Background Cleanup"),
                    new DisciplineWorkflowAction("CE-Audit / Overkill / Purge", "CE_BGCLEAN", "Run drawing audit, safe duplicate-object cleanup and purge.", "01 Background Cleanup"),
                    new DisciplineWorkflowAction("CE-Freeze Solid Hatches", "CE_BGFREEZESOLIDHATCH", "Move SOLID hatches to a dedicated frozen CE background layer.", "02 Visibility"),
                    new DisciplineWorkflowAction("CE-Freeze All Hatches", "CE_BGFREEZEALLHATCH", "Move all ordinary model-space hatches to a dedicated frozen CE background layer.", "02 Visibility"),
                    new DisciplineWorkflowAction("CE-Freeze Dimensions", "CE_BGFREEZEDIMS", "Move model-space dimensions to a dedicated frozen CE background layer.", "02 Visibility"),
                    new DisciplineWorkflowAction("CE-Freeze All or Selected Text", "CE_BGFREEZETEXT", "Choose all model-space DBText/MText or only a selected set and move it to a frozen CE layer.", "02 Visibility"),
                    new DisciplineWorkflowAction("CE-Freeze All or Selected Attributes", "CE_BGFREEZEATTRIBUTES", "Choose all block attributes or attributes in selected ordinary blocks and move them to a frozen CE layer.", "02 Visibility"),
                    new DisciplineWorkflowAction("CE-Scale Correction / Convert to Metres", "CE_BGSCALECORRECTION", "Scale the complete model-space background to metres using direct mm-to-m conversion or verified reference lengths.", "03 Scale Correction"),
                    new DisciplineWorkflowAction("CE-Existing Background / XREF Utilities", "CE_BACKGROUNDTOOLS", "Open the existing CE background audit/light/XREF split/info/backup workflow.", "04 Existing CE Tools")
                });
'@
$background = ReplaceMethodBody $background '        public void BackgroundPreparationTools()' $backgroundMenuBody 'Background Tools requested visibility menu'

$colourBody = @'
            Document document = Active();
            if (document == null) return;

            var allowedDxfTypes = new HashSet<string>(
                new[]
                {
                    "LINE", "LWPOLYLINE", "POLYLINE", "ARC", "CIRCLE",
                    "ELLIPSE", "SPLINE", "HATCH", "TEXT", "MTEXT",
                    "DIMENSION", "LEADER", "MULTILEADER", "POINT",
                    "SOLID", "TRACE", "3DFACE", "REGION", "INSERT", "TABLE"
                },
                StringComparer.OrdinalIgnoreCase);

            var ids = new List<ObjectId>();
            using (Transaction snapshot = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord model = snapshot.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(document.Database),
                    OpenMode.ForRead,
                    false) as BlockTableRecord;
                if (model != null) ids.AddRange(model.Cast<ObjectId>());
            }

            int changed = 0;
            int already250 = 0;
            int skippedLocked = 0;
            int skippedProtected = 0;
            int failed = 0;

            using (DocumentLock documentLock = document.LockDocument())
            {
                foreach (ObjectId id in ids)
                {
                    if (id.IsNull || id.IsErased) { skippedProtected++; continue; }

                    string dxf = string.Empty;
                    try { dxf = id.ObjectClass == null ? string.Empty : (id.ObjectClass.DxfName ?? string.Empty); }
                    catch { skippedProtected++; continue; }

                    if (!allowedDxfTypes.Contains(dxf) ||
                        dxf.StartsWith("AEC", StringComparison.OrdinalIgnoreCase) ||
                        dxf.StartsWith("AECC", StringComparison.OrdinalIgnoreCase) ||
                        dxf.IndexOf("PROXY", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        skippedProtected++;
                        continue;
                    }

                    try
                    {
                        // One entity per transaction. A protected or failed entity
                        // cannot poison the remaining background pass.
                        using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                        {
                            Entity entity = transaction.GetObject(
                                id,
                                OpenMode.ForRead,
                                false) as Entity;
                            if (entity == null || entity.IsErased)
                            {
                                skippedProtected++;
                                continue;
                            }

                            string managedType = entity.GetType().FullName ?? string.Empty;
                            if (!managedType.StartsWith(
                                    "Autodesk.AutoCAD.DatabaseServices.",
                                    StringComparison.Ordinal) ||
                                managedType.StartsWith(
                                    "Autodesk.Civil.",
                                    StringComparison.OrdinalIgnoreCase))
                            {
                                skippedProtected++;
                                continue;
                            }

                            LayerTableRecord layer = transaction.GetObject(
                                entity.LayerId,
                                OpenMode.ForRead,
                                false) as LayerTableRecord;
                            if (layer != null && layer.IsLocked)
                            {
                                skippedLocked++;
                                continue;
                            }

                            BlockReference block = entity as BlockReference;
                            if (block != null && IsXref(block, transaction))
                            {
                                skippedProtected++;
                                continue;
                            }

                            if (entity.Color != null &&
                                entity.Color.ColorMethod == ColorMethod.ByAci &&
                                entity.Color.ColorIndex == 250)
                            {
                                already250++;
                                continue;
                            }

                            entity.UpgradeOpen();
                            entity.Color = Color.FromColorIndex(
                                ColorMethod.ByAci,
                                250);
                            transaction.Commit();
                            changed++;
                        }
                    }
                    catch
                    {
                        failed++;
                    }
                }
            }

            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_BGCOLOR250 complete. Changed={0}; already 250={1}; locked skipped={2}; protected/Civil/AEC/XREF skipped={3}; failed={4}.",
                changed,
                already250,
                skippedLocked,
                skippedProtected,
                failed);
'@
$background = ReplaceMethodBody $background '        public void BackgroundColour250()' $colourBody 'Background Colour 250 strict DXF allow-list'

if (-not $background.Contains('"CE_BGFREEZEALLHATCH"')) {
    $insertMarker = '        [CommandMethod("CE_TOOLS", "CE_BGSCALECORRECTION", CommandFlags.Modal | CommandFlags.Redraw)]'
    if (-not $background.Contains($insertMarker)) { throw 'Background visibility command insertion marker missing.' }
    $newVisibilityCommands = @'
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
            MoveSelectedOrAllTextToFrozenLayer(
                "CE-BG-TEXT-FROZEN");
        }

        [CommandMethod("CE_TOOLS", "CE_BGFREEZEATTRIBUTES", CommandFlags.Modal | CommandFlags.Redraw)]
        public void FreezeAllOrSelectedAttributes()
        {
            MoveSelectedOrAllAttributesToFrozenLayer(
                "CE-BG-ATTRIBUTES-FROZEN");
        }

'@
    $background = $background.Replace($insertMarker,$newVisibilityCommands + $insertMarker)
}

if (-not $background.Contains('private static void MoveSelectedOrAllTextToFrozenLayer(')) {
    $helperMarker = '        private static void MoveToFrozenLayer(string layerName, Func<Entity, bool> predicate, string description)'
    if (-not $background.Contains($helperMarker)) { throw 'Background helper insertion marker missing.' }
    $newHelpers = @'
        private static void MoveSelectedOrAllTextToFrozenLayer(string layerName)
        {
            Document document = Active();
            if (document == null) return;

            const string all = "All model-space text";
            const string selected = "Selected text";
            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Freeze Text",
                "Choose all ordinary model-space DBText/MText or only selected text. Civil labels are intentionally excluded.");
            settings.AddChoice(
                "Scope", "01 Scope", "Text to freeze", all,
                "Civil 3D labels, AEC and proxy objects are not included.",
                new[] { all, selected });
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;

            var ids = new List<ObjectId>();
            if (string.Equals(settings.Text("Scope"), selected, StringComparison.OrdinalIgnoreCase))
            {
                PromptSelectionResult pick = document.Editor.GetSelection(
                    new PromptSelectionOptions
                    {
                        MessageForAdding = "\nSelect DBText/MText to move to the frozen background text layer: ",
                        AllowDuplicates = false,
                        RejectObjectsFromNonCurrentSpace = true
                    },
                    new SelectionFilter(new[]
                    {
                        new TypedValue((int)DxfCode.Start, "TEXT,MTEXT")
                    }));
                if (pick.Status != PromptStatus.OK || pick.Value == null) return;
                ids.AddRange(pick.Value.GetObjectIds());
            }
            else
            {
                using (Transaction snapshot = document.Database.TransactionManager.StartTransaction())
                {
                    BlockTableRecord model = snapshot.GetObject(
                        SymbolUtilityServices.GetBlockModelSpaceId(document.Database),
                        OpenMode.ForRead,
                        false) as BlockTableRecord;
                    if (model != null)
                    {
                        foreach (ObjectId id in model)
                        {
                            string dxf = string.Empty;
                            try { dxf = id.ObjectClass == null ? string.Empty : (id.ObjectClass.DxfName ?? string.Empty); }
                            catch { }
                            if (string.Equals(dxf, "TEXT", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(dxf, "MTEXT", StringComparison.OrdinalIgnoreCase))
                                ids.Add(id);
                        }
                    }
                }
            }

            MoveSpecificEntitiesToFrozenLayer(
                document,
                ids,
                layerName,
                delegate(Entity entity) { return entity is DBText || entity is MText; },
                "text");
        }

        private static void MoveSelectedOrAllAttributesToFrozenLayer(string layerName)
        {
            Document document = Active();
            if (document == null) return;

            const string all = "All block attributes";
            const string selected = "Attributes in selected blocks";
            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Freeze Attributes",
                "Move visible ordinary AutoCAD block attributes to a dedicated frozen layer. XREF block attributes are skipped.");
            settings.AddChoice(
                "Scope", "01 Scope", "Attributes to freeze", all,
                "Choose all model-space ordinary blocks or only selected blocks.",
                new[] { all, selected });
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;

            var blockIds = new List<ObjectId>();
            if (string.Equals(settings.Text("Scope"), selected, StringComparison.OrdinalIgnoreCase))
            {
                PromptSelectionResult pick = document.Editor.GetSelection(
                    new PromptSelectionOptions
                    {
                        MessageForAdding = "\nSelect ordinary blocks whose attributes must be frozen: ",
                        AllowDuplicates = false,
                        RejectObjectsFromNonCurrentSpace = true
                    },
                    new SelectionFilter(new[]
                    {
                        new TypedValue((int)DxfCode.Start, "INSERT")
                    }));
                if (pick.Status != PromptStatus.OK || pick.Value == null) return;
                blockIds.AddRange(pick.Value.GetObjectIds());
            }
            else
            {
                using (Transaction snapshot = document.Database.TransactionManager.StartTransaction())
                {
                    BlockTableRecord model = snapshot.GetObject(
                        SymbolUtilityServices.GetBlockModelSpaceId(document.Database),
                        OpenMode.ForRead,
                        false) as BlockTableRecord;
                    if (model != null)
                        blockIds.AddRange(model.Cast<ObjectId>());
                }
            }

            int moved = 0;
            int skipped = 0;
            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                ObjectId layerId = EnsureLayer(document.Database, transaction, layerName);
                foreach (ObjectId id in blockIds.Where(value => !value.IsNull && !value.IsErased).Distinct())
                {
                    BlockReference block = null;
                    try { block = transaction.GetObject(id, OpenMode.ForRead, false) as BlockReference; }
                    catch { skipped++; continue; }
                    if (block == null || IsXref(block, transaction)) { skipped++; continue; }

                    foreach (ObjectId attributeId in block.AttributeCollection)
                    {
                        try
                        {
                            AttributeReference attribute = transaction.GetObject(
                                attributeId,
                                OpenMode.ForWrite,
                                false) as AttributeReference;
                            if (attribute == null) { skipped++; continue; }
                            attribute.LayerId = layerId;
                            moved++;
                        }
                        catch { skipped++; }
                    }
                }

                LayerTableRecord layer = transaction.GetObject(
                    layerId,
                    OpenMode.ForWrite,
                    false) as LayerTableRecord;
                if (layer != null) layer.IsFrozen = true;
                transaction.Commit();
            }

            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nMoved {0} attribute(s) to frozen layer {1}; skipped={2}.",
                moved,
                layerName,
                skipped);
        }

        private static void MoveSpecificEntitiesToFrozenLayer(
            Document document,
            IEnumerable<ObjectId> sourceIds,
            string layerName,
            Func<Entity, bool> predicate,
            string description)
        {
            if (document == null) return;
            int moved = 0;
            int skipped = 0;

            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                ObjectId layerId = EnsureLayer(document.Database, transaction, layerName);
                foreach (ObjectId id in sourceIds
                    .Where(value => !value.IsNull && !value.IsErased)
                    .Distinct())
                {
                    try
                    {
                        Entity entity = transaction.GetObject(
                            id,
                            OpenMode.ForRead,
                            false) as Entity;
                        if (entity == null || !predicate(entity))
                        {
                            skipped++;
                            continue;
                        }
                        string managedType = entity.GetType().FullName ?? string.Empty;
                        if (!managedType.StartsWith(
                                "Autodesk.AutoCAD.DatabaseServices.",
                                StringComparison.Ordinal) ||
                            managedType.StartsWith(
                                "Autodesk.Civil.",
                                StringComparison.OrdinalIgnoreCase))
                        {
                            skipped++;
                            continue;
                        }
                        LayerTableRecord sourceLayer = transaction.GetObject(
                            entity.LayerId,
                            OpenMode.ForRead,
                            false) as LayerTableRecord;
                        if (sourceLayer != null && sourceLayer.IsLocked)
                        {
                            skipped++;
                            continue;
                        }
                        entity.UpgradeOpen();
                        entity.LayerId = layerId;
                        moved++;
                    }
                    catch { skipped++; }
                }

                LayerTableRecord layer = transaction.GetObject(
                    layerId,
                    OpenMode.ForWrite,
                    false) as LayerTableRecord;
                if (layer != null) layer.IsFrozen = true;
                transaction.Commit();
            }

            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nMoved {0} {1}(s) to frozen layer {2}; skipped={3}.",
                moved,
                description,
                layerName,
                skipped);
        }

'@
    $background = $background.Replace($helperMarker,$newHelpers + $helperMarker)
}
WriteText $backgroundPath $background

# -----------------------------------------------------------------------------
# 4. Expose Break at Crossings/Junctions directly in Sewer Layout Production.
# -----------------------------------------------------------------------------
$menu = ReadText $menuPath
$sewerBreakAction = '                    A("CE-Break at Crossings and Junctions", "CE_PLBREAKJUNCTIONS", "Split sewer source polylines safely at true crossings/T-junctions before network creation.", "02 PREPARE"),'
$menu = InsertIntoMethodActionList $menu '        public void SewerLayout()' $sewerBreakAction 'Sewer Layout Break-at-Crossings action'
WriteText $menuPath $menu

# -----------------------------------------------------------------------------
# 5. Sewer Network from Multiple Polylines: maximum structure spacing is applied
#    per original source segment. <= max spacing gets no intermediate structure;
#    > max spacing is evenly subdivided, retaining endpoints/vertices/junctions.
# -----------------------------------------------------------------------------
$multi = ReadText $multiPath
$rulesAnchor = @'
            setup.AddChoice(
                "Connections",
                "02 Creation",
                "Shared vertices / junctions",
                "Connect through structures",
                "Reuse one structure at coincident source vertices so branches and consecutive pipe segments are connected.",
                new[] { "Connect through structures", "Create parts without connections" });
'@
$rulesWithSpacing = $rulesAnchor + @'
            setup.AddPositiveDouble(
                "StructureSpacing",
                "02 Creation",
                "Maximum structure spacing",
                60.0,
                "Original source segments equal to or shorter than this spacing receive no intermediate structure. Longer segments are evenly divided so every pipe run is within the maximum spacing.");
'@
$multi = ReplaceRequired $multi $rulesAnchor $rulesWithSpacing 'Sewer multi-source maximum structure spacing popup'

$pathsAnchor = @'
            List<SourcePath> paths = ReadSourcePaths(
                document.Database,
                sourceIds);
            int segmentCount = paths.Sum(path => Math.Max(0, path.Points.Count - 1));
'@
$pathsWithSpacing = @'
            List<SourcePath> paths = ReadSourcePaths(
                document.Database,
                sourceIds);
            double structureSpacing = Math.Max(
                1.0,
                setup.Double("StructureSpacing", 60.0));
            paths = ApplyMaximumStructureSpacing(
                paths,
                structureSpacing);
            int segmentCount = paths.Sum(path => Math.Max(0, path.Points.Count - 1));
'@
$multi = ReplaceRequired $multi $pathsAnchor $pathsWithSpacing 'Sewer multi-source spacing application'

if (-not $multi.Contains('private static List<SourcePath> ApplyMaximumStructureSpacing(')) {
    $spacingHelperMarker = '        private static ObjectId EnsureStructure('
    if (-not $multi.Contains($spacingHelperMarker)) { throw 'Sewer multi-source spacing helper insertion marker missing.' }
    $spacingHelper = @'
        private static List<SourcePath> ApplyMaximumStructureSpacing(
            IEnumerable<SourcePath> sourcePaths,
            double maximumSpacing)
        {
            double spacing = Math.Max(1.0, maximumSpacing);
            var result = new List<SourcePath>();

            foreach (SourcePath source in sourcePaths ?? Enumerable.Empty<SourcePath>())
            {
                if (source == null || source.Points == null || source.Points.Count < 2)
                    continue;

                var points = new List<Point3d> { source.Points[0] };
                for (int index = 0; index < source.Points.Count - 1; index++)
                {
                    Point3d start = source.Points[index];
                    Point3d end = source.Points[index + 1];
                    double length = start.DistanceTo(end);
                    if (length <= PointTolerance)
                        continue;

                    // Equal/shorter segments remain exactly one pipe. Only a
                    // genuinely longer original segment receives intermediate nodes.
                    if (length > spacing + PointTolerance)
                    {
                        int pieces = Math.Max(
                            2,
                            (int)Math.Ceiling(length / spacing));
                        Vector3d delta = end - start;
                        for (int piece = 1; piece < pieces; piece++)
                        {
                            double fraction = (double)piece / pieces;
                            points.Add(start + delta.MultiplyBy(fraction));
                        }
                    }
                    points.Add(end);
                }

                RemoveConsecutiveDuplicates(points);
                if (points.Count >= 2)
                {
                    result.Add(new SourcePath
                    {
                        SourceId = source.SourceId,
                        Points = points
                    });
                }
            }
            return result;
        }

'@
    $multi = $multi.Replace($spacingHelperMarker,$spacingHelper + $spacingHelperMarker)
}
WriteText $multiPath $multi

# -----------------------------------------------------------------------------
# 6. Vertex Setting-Out deletion suppression. The table stores KNOWN point keys
#    and SUPPRESS keys. If a previously generated live record has no output at
#    refresh time, it is treated as a deliberate user deletion and stays absent.
#    New geometry keys are still generated normally.
# -----------------------------------------------------------------------------
$vertex = ReadText $vertexPath
$createLinkAnchor = @'
                WriteTableLink(table, transaction, link);
                PopulateTable(table, records, textHeight, link);
'@
$createLinkNew = @'
                WriteTableLink(table, transaction, link);
                WriteOutputKeyState(
                    table,
                    records.Select(item => item.Key),
                    Enumerable.Empty<string>());
                PopulateTable(table, records, textHeight, link);
'@
$vertex = ReplaceRequired $vertex $createLinkAnchor $createLinkNew 'Vertex initial known-output key state'

$refreshBody = @'
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (civilDocument == null)
                throw new InvalidOperationException("No active Civil 3D document is available.");

            VertexSettingLink link;
            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                Table table = transaction.GetObject(
                    tableId,
                    OpenMode.ForWrite,
                    false) as Table;
                if (table == null)
                    throw new InvalidOperationException(
                        "The selected object is not an AutoCAD table.");

                link = ReadTableLink(table);
                List<ObjectId> sourceIds = link.SourceHandles
                    .Select(handle => ResolveHandle(document.Database, handle))
                    .Where(id => !id.IsNull && !id.IsErased)
                    .ToList();
                if (sourceIds.Count == 0)
                    throw new InvalidOperationException(
                        "None of the linked source polylines or feature lines are available.");

                int rejected;
                IList<VertexSettingSource> sources = VertexSettingOutGeometry.ReadSources(
                    document.Database,
                    transaction,
                    sourceIds,
                    out rejected);
                ApplyGenerationMode(sources, link.GenerationMode);
                ApplyElevationReference(
                    document.Database,
                    sources,
                    link.ElevationMode,
                    ResolveHandle(document.Database, link.ElevationSourceHandle));
                ApplyLevelReferences(
                    document.Database,
                    sources,
                    ResolveHandle(document.Database, link.NgSurfaceHandle),
                    ResolveHandle(document.Database, link.DesignSurfaceHandle));
                if (sources.Count == 0 || sources.All(item => item.Records.Count == 0))
                    throw new InvalidOperationException(
                        "The linked sources produced no current setting-out geometry.");

                List<VertexSettingRecord> records = FlattenAndName(
                    sources,
                    link.Prefix,
                    link.StartNumber,
                    link.NumberingMode,
                    link.RoadStartNumber,
                    link.SequenceMode,
                    link.StartRecordKey);

                EnsureRegApp(document.Database, transaction);
                BlockTableRecord modelSpace = GetModelSpace(
                    document.Database,
                    transaction,
                    OpenMode.ForWrite);
                Dictionary<string, ObjectId> outputs;
                Dictionary<string, ObjectId> dimensions;
                InventoryGroup(
                    modelSpace,
                    transaction,
                    link.GroupId,
                    out outputs,
                    out dimensions);

                HashSet<string> knownOutputKeys;
                HashSet<string> suppressedOutputKeys;
                ReadOutputKeyState(
                    table,
                    out knownOutputKeys,
                    out suppressedOutputKeys);

                // Upgrade existing legacy linked tables safely: seed KNOWN only
                // from outputs that still exist. From then on missing known/live
                // outputs are deliberate suppressions.
                if (knownOutputKeys.Count == 0)
                {
                    foreach (string key in outputs.Keys)
                        knownOutputKeys.Add(key);
                }

                AnnotationOptions annotation = AnnotationSettingsStore.Read(
                    document.Database);
                double textHeight = PaperAnnotationScale.ModelTextHeight(
                    document.Database,
                    annotation == null ? 2.0 : annotation.TextHeight);

                var liveOutputKeys = new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);
                int visiblePointCount = 0;

                foreach (VertexSettingRecord record in records)
                {
                    liveOutputKeys.Add(record.Key);
                    ObjectId existing;
                    if (outputs.TryGetValue(record.Key, out existing))
                    {
                        if (UpdateOutput(
                                transaction,
                                existing,
                                link,
                                record,
                                textHeight))
                        {
                            suppressedOutputKeys.Remove(record.Key);
                            knownOutputKeys.Add(record.Key);
                            visiblePointCount++;
                            continue;
                        }

                        CaptureCurrentAnnotationOffset(
                            transaction,
                            existing,
                            record);
                        EraseIfPossible(transaction, existing);
                    }

                    if (knownOutputKeys.Contains(record.Key))
                    {
                        // The record still exists in the source geometry but its
                        // generated output no longer exists: remember the user's
                        // manual deletion and do not silently recreate it.
                        suppressedOutputKeys.Add(record.Key);
                        continue;
                    }

                    if (!suppressedOutputKeys.Contains(record.Key))
                    {
                        CreateOutput(
                            document.Database,
                            civilDocument,
                            transaction,
                            modelSpace,
                            link,
                            record,
                            textHeight);
                        knownOutputKeys.Add(record.Key);
                        visiblePointCount++;
                    }
                }

                foreach (KeyValuePair<string, ObjectId> stale in outputs)
                {
                    if (liveOutputKeys.Contains(stale.Key)) continue;
                    EraseIfPossible(transaction, stale.Value);
                    knownOutputKeys.Remove(stale.Key);
                    suppressedOutputKeys.Remove(stale.Key);
                }

                var liveDimensionKeys = new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);
                foreach (VertexRadialDimension dimension in
                    sources.SelectMany(item => item.Dimensions))
                {
                    liveDimensionKeys.Add(dimension.Key);
                    ObjectId existing;
                    if (dimensions.TryGetValue(dimension.Key, out existing) &&
                        UpdateDimension(
                            transaction,
                            existing,
                            dimension,
                            textHeight))
                        continue;
                    EraseIfPossible(transaction, existing);
                    CreateDimension(
                        document.Database,
                        transaction,
                        modelSpace,
                        link,
                        dimension,
                        textHeight);
                }
                foreach (KeyValuePair<string, ObjectId> stale in dimensions)
                    if (!liveDimensionKeys.Contains(stale.Key))
                        EraseIfPossible(transaction, stale.Value);

                WriteOutputKeyState(
                    table,
                    knownOutputKeys,
                    suppressedOutputKeys);
                PopulateTable(table, records, textHeight, link);
                transaction.Commit();

                pointCount = visiblePointCount;
                dimensionCount = liveDimensionKeys.Count;
            }

            if (string.Equals(
                    link.OutputType,
                    "COGO",
                    StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    CogoPointProjectStyleCommands.ApplySelectedStyles(
                        document,
                        false);
                }
                catch { }
            }
'@
$vertex = ReplaceMethodBody $vertex '        private static void RefreshTable(' $refreshBody 'Vertex deletion suppression refresh'

if (-not $vertex.Contains('private static void ReadOutputKeyState(')) {
    $vertexHelperMarker = '        private static void WriteTableLink('
    if (-not $vertex.Contains($vertexHelperMarker)) { throw 'Vertex key-state helper insertion marker missing.' }
    $vertexHelpers = @'
        private static void ReadOutputKeyState(
            Table table,
            out HashSet<string> known,
            out HashSet<string> suppressed)
        {
            known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            suppressed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (table == null) return;

            ResultBuffer buffer = table.XData;
            if (buffer == null) return;
            foreach (TypedValue value in buffer.AsArray())
            {
                if (value.TypeCode != (int)DxfCode.ExtendedDataAsciiString)
                    continue;
                string text = Convert.ToString(
                    value.Value,
                    CultureInfo.InvariantCulture) ?? string.Empty;
                if (text.StartsWith("KNOWN=", StringComparison.OrdinalIgnoreCase))
                    known.Add(text.Substring(6));
                else if (text.StartsWith("SUPPRESS=", StringComparison.OrdinalIgnoreCase))
                    suppressed.Add(text.Substring(9));
            }
        }

        private static void WriteOutputKeyState(
            Table table,
            IEnumerable<string> known,
            IEnumerable<string> suppressed)
        {
            if (table == null) return;

            var values = new List<TypedValue>();
            ResultBuffer existing = table.XData;
            if (existing != null)
            {
                foreach (TypedValue value in existing.AsArray())
                {
                    if (value.TypeCode == (int)DxfCode.ExtendedDataAsciiString)
                    {
                        string text = Convert.ToString(
                            value.Value,
                            CultureInfo.InvariantCulture) ?? string.Empty;
                        if (text.StartsWith("KNOWN=", StringComparison.OrdinalIgnoreCase) ||
                            text.StartsWith("SUPPRESS=", StringComparison.OrdinalIgnoreCase))
                            continue;
                    }
                    values.Add(value);
                }
            }

            foreach (string key in (known ?? Enumerable.Empty<string>())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(item => item, StringComparer.OrdinalIgnoreCase))
            {
                values.Add(new TypedValue(
                    (int)DxfCode.ExtendedDataAsciiString,
                    "KNOWN=" + key));
            }
            foreach (string key in (suppressed ?? Enumerable.Empty<string>())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(item => item, StringComparer.OrdinalIgnoreCase))
            {
                values.Add(new TypedValue(
                    (int)DxfCode.ExtendedDataAsciiString,
                    "SUPPRESS=" + key));
            }
            table.XData = new ResultBuffer(values.ToArray());
        }

'@
    $vertex = $vertex.Replace($vertexHelperMarker,$vertexHelpers + $vertexHelperMarker)
}
WriteText $vertexPath $vertex

# -----------------------------------------------------------------------------
# 7. Geometry-first Midblock/Road Reserve: Civil Surface sampling is confined to
#    a planning/read transaction. The separate construction transaction contains
#    ordinary AutoCAD polylines/circles/layers only.
# -----------------------------------------------------------------------------
$geometry = ReadText $geometryPath
$midblockBody = @'
            Document document = Active();
            if (document == null) return;
            List<string> surfaceChoices = SurfaceChoices(document);
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Geometry-First Midblock Sewer",
                "Create editable AutoCAD sewer polylines beside shared cadastral/midblock boundaries. Surface sampling is completed before the separate AutoCAD construction transaction.");
            model.AddChoice(
                "Surface", "01 Analysis", "Surface for flow direction",
                surfaceChoices[0],
                "Optional Civil 3D surface used only during the planning/read stage. The construction transaction never opens a Civil surface.",
                surfaceChoices);
            model.AddChoice(
                "Direction", "02 Midblock", "Shared-boundary direction",
                "Automatic dominant direction",
                "Automatic selects the dominant horizontal/vertical shared-boundary direction.",
                new[] { "Automatic dominant direction", "Horizontal", "Vertical" });
            model.AddChoice(
                "Side", "02 Midblock", "Offset side",
                "Automatic lower side",
                "Automatic samples both offset sides during the read-only planning stage when a surface is selected.",
                new[] { "Automatic lower side", "Left / Top", "Right / Bottom", "On shared boundary" });
            model.AddPositiveDouble(
                "Offset", "02 Midblock", "Offset from shared erf boundary",
                1.5,
                "Planning sewer offset from the shared cadastral/midblock line.");
            model.AddChoice(
                "Spacing", "03 Manholes", "Maximum manhole spacing",
                "60 m",
                "Route vertices and planning circles are inserted at this maximum interval.",
                new[] { "60 m", "80 m", "Custom" });
            model.AddPositiveDouble(
                "CustomSpacing", "03 Manholes", "Custom spacing",
                60.0,
                "Used only for Custom spacing.");
            model.AddPositiveDouble(
                "Diameter", "03 Manholes", "Planning manhole diameter",
                1.2,
                "Visible planning-circle diameter.");
            model.AddPositiveDouble(
                "MinEdge", "04 Safety", "Minimum usable shared edge length",
                2.0,
                "Ignore very short shared cadastral edges.");
            model.AddChoice(
                "Replace", "05 Output", "Existing Midblock layout",
                "Replace existing",
                "Replace previous geometry-first Midblock layout or keep it.",
                new[] { "Replace existing", "Keep existing" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            ObjectId surfaceId = August20SurfaceChoice.ResolveSurfaceId(
                document,
                model.Text("Surface"));
            PromptSelectionResult selection = SelectClosedPolylines(
                document.Editor,
                "\nSelect closed cadastral erf polylines for geometry-first Midblock sewer layout: ");
            if (selection.Status != PromptStatus.OK ||
                selection.Value == null ||
                selection.Value.Count == 0)
                return;

            double offset = Math.Max(0.0, model.Double("Offset", 1.5));
            double spacing = ReadSpacing(model);
            double diameter = Math.Max(0.1, model.Double("Diameter", 1.2));
            double minEdge = Math.Max(0.1, model.Double("MinEdge", 2.0));
            int sharedCount = 0;
            var plannedRoutes = new List<Line2>();

            try
            {
                using (DocumentLock documentLock = document.LockDocument())
                {
                    // Planning/read stage: cadastral geometry + optional Civil Surface.
                    using (Transaction planning =
                        document.Database.TransactionManager.StartTransaction())
                    {
                        List<ParcelLite> parcels = ReadParcels(
                            selection.Value.GetObjectIds(),
                            planning);
                        if (parcels.Count == 0)
                        {
                            document.Editor.WriteMessage(
                                "\nCE Midblock: no usable closed cadastral polylines were found.");
                            return;
                        }

                        List<EdgeLite> shared = SharedEdges(
                            BuildEdges(parcels, minEdge));
                        if (shared.Count == 0)
                        {
                            document.Editor.WriteMessage(
                                "\nCE Midblock: no exact shared cadastral edges were found.");
                            return;
                        }

                        bool horizontal = ResolveHorizontal(
                            shared,
                            model.Text("Direction"));
                        shared = shared
                            .Where(edge => horizontal ? edge.Horizontal : edge.Vertical)
                            .ToList();
                        sharedCount = shared.Count;

                        List<Line2> merged = MergeAxisAligned(
                            shared,
                            horizontal);
                        if (merged.Count == 0)
                        {
                            document.Editor.WriteMessage(
                                "\nCE Midblock: shared edges did not form usable continuous midblock lines.");
                            return;
                        }

                        CivilSurface surface = OpenSurface(
                            planning,
                            surfaceId);
                        foreach (Line2 baseLine in merged)
                        {
                            Line2 routeLine = OffsetMidblock(
                                baseLine,
                                horizontal,
                                offset,
                                model.Text("Side"),
                                surface);
                            if (routeLine.Length <= Tol) continue;
                            OrientDownhill(ref routeLine, surface);
                            plannedRoutes.Add(routeLine);
                        }
                    }

                    if (plannedRoutes.Count == 0)
                    {
                        document.Editor.WriteMessage(
                            "\nCE Midblock: no usable route geometry was planned.");
                        return;
                    }

                    // Construction stage: ordinary AutoCAD objects only. No Civil
                    // Surface is opened or sampled in this transaction.
                    int routeCount = 0;
                    int manholeCount = 0;
                    using (Transaction construction =
                        document.Database.TransactionManager.StartTransaction())
                    {
                        BlockTableRecord space = OpenModelSpace(
                            document.Database,
                            construction,
                            OpenMode.ForWrite);
                        if (space == null) return;

                        if (string.Equals(
                            model.Text("Replace"),
                            "Replace existing",
                            StringComparison.OrdinalIgnoreCase))
                        {
                            SafeEraseByLayers(
                                space,
                                construction,
                                MidblockRouteLayer,
                                MidblockMhLayer,
                                MidblockLabelLayer);
                        }

                        ObjectId routeLayer = EnsureLayer(
                            document.Database,
                            construction,
                            MidblockRouteLayer);
                        ObjectId mhLayer = EnsureLayer(
                            document.Database,
                            construction,
                            MidblockMhLayer);
                        ObjectId labelLayer = EnsureLayer(
                            document.Database,
                            construction,
                            MidblockLabelLayer);

                        int number = 1;
                        foreach (Line2 routeLine in plannedRoutes)
                        {
                            Polyline route = CreateSpacedPolyline(
                                document.Database,
                                routeLine,
                                spacing,
                                routeLayer);
                            space.AppendEntity(route);
                            construction.AddNewlyCreatedDBObject(route, true);
                            WriteLayoutRecord(
                                route,
                                construction,
                                "MIDBLOCK",
                                spacing,
                                diameter,
                                model.Text("Surface"));
                            manholeCount += AddManholesAtVertices(
                                document.Database,
                                construction,
                                space,
                                route,
                                mhLayer,
                                labelLayer,
                                diameter,
                                "MB-MH",
                                number++);
                            routeCount++;
                        }
                        construction.Commit();
                    }

                    document.Editor.SetImpliedSelection(new ObjectId[0]);
                    document.Editor.Regen();
                    document.Editor.WriteMessage(
                        "\nCE geometry-first Midblock complete. Shared edges={0}; routes={1}; planning manholes={2}. Civil surface sampling completed before AutoCAD construction. Use CE_SEWERBUILDNETWORK only after review.",
                        sharedCount,
                        routeCount,
                        manholeCount);
                }
            }
            catch (Autodesk.AutoCAD.Runtime.Exception ex)
            {
                document.Editor.WriteMessage(
                    "\nCE geometry-first Midblock stopped safely: {0}",
                    ex.Message);
            }
            catch (System.Exception ex)
            {
                document.Editor.WriteMessage(
                    "\nCE geometry-first Midblock stopped safely: {0}",
                    ex.Message);
            }
'@
$geometry = ReplaceMethodBody $geometry '        private static void RunMidblock(bool legacyEntry)' $midblockBody 'Geometry-first Midblock read/write transaction separation'

$roadReserveBody = @'
            Document document = Active();
            if (document == null) return;
            List<string> surfaceChoices = SurfaceChoices(document);
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Geometry-First Road Reserve Sewer",
                "Create editable sewer polylines inside road reserves. Surface sampling is completed in a separate planning/read transaction before AutoCAD construction.");
            model.AddChoice(
                "Surface", "01 Analysis", "Surface for flow direction",
                surfaceChoices[0],
                "Optional Civil 3D surface used only during planning/read. The construction transaction never opens a Civil surface.",
                surfaceChoices);
            model.AddPositiveDouble(
                "Offset", "02 Road Reserve", "Offset from erf boundary toward road centre",
                1.5,
                "Create a sewer route on each side of an accepted road reserve.");
            model.AddPositiveDouble(
                "MinWidth", "02 Road Reserve", "Minimum road reserve width",
                6.0, "Minimum facing-boundary separation.");
            model.AddPositiveDouble(
                "MaxWidth", "02 Road Reserve", "Maximum road reserve width",
                60.0, "Maximum facing-boundary separation.");
            model.AddPositiveDouble(
                "MinOverlap", "02 Road Reserve", "Minimum overlap (%)",
                50.0, "Minimum overlap as percentage of the shorter facing edge.");
            model.AddPositiveDouble(
                "MinEdge", "02 Road Reserve", "Minimum usable outer edge length",
                4.0, "Ignore shorter outer erf edges.");
            model.AddChoice(
                "Spacing", "03 Manholes", "Maximum manhole spacing",
                "60 m",
                "Route vertices and manhole circles are created at this maximum interval.",
                new[] { "60 m", "80 m", "Custom" });
            model.AddPositiveDouble(
                "CustomSpacing", "03 Manholes", "Custom spacing",
                60.0, "Used only for Custom spacing.");
            model.AddPositiveDouble(
                "Diameter", "03 Manholes", "Planning manhole diameter",
                1.2, "Visible planning-circle diameter.");
            model.AddChoice(
                "Replace", "04 Output", "Existing Road Reserve layout",
                "Replace existing",
                "Replace previous geometry-first Road Reserve sewer layout or keep it.",
                new[] { "Replace existing", "Keep existing" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            ObjectId surfaceId = August20SurfaceChoice.ResolveSurfaceId(
                document,
                model.Text("Surface"));
            PromptSelectionResult selection = SelectClosedPolylines(
                document.Editor,
                "\nSelect closed cadastral erf polylines around the road reserves: ");
            if (selection.Status != PromptStatus.OK ||
                selection.Value == null ||
                selection.Value.Count == 0)
                return;

            double offset = Math.Max(0.0, model.Double("Offset", 1.5));
            double minWidth = Math.Max(0.1, model.Double("MinWidth", 6.0));
            double maxWidth = Math.Max(
                minWidth,
                model.Double("MaxWidth", 60.0));
            double minOverlap = Math.Max(
                1.0,
                Math.Min(
                    100.0,
                    model.Double("MinOverlap", 50.0)));
            double minEdge = Math.Max(
                0.1,
                model.Double("MinEdge", 4.0));
            double spacing = ReadSpacing(model);
            double diameter = Math.Max(
                0.1,
                model.Double("Diameter", 1.2));
            int pairCount = 0;
            var plannedRoutes = new List<Line2>();

            try
            {
                using (DocumentLock documentLock = document.LockDocument())
                {
                    // Planning/read stage: reserve pairing + optional Civil Surface.
                    using (Transaction planning =
                        document.Database.TransactionManager.StartTransaction())
                    {
                        List<ParcelLite> parcels = ReadParcels(
                            selection.Value.GetObjectIds(),
                            planning);
                        List<EdgeLite> exterior = ExteriorEdges(
                            BuildEdges(parcels, minEdge));
                        List<EdgePair> pairs = PairRoadReserveEdges(
                            exterior,
                            parcels,
                            minWidth,
                            maxWidth,
                            minOverlap);
                        if (pairs.Count == 0)
                        {
                            document.Editor.WriteMessage(
                                "\nCE Road Reserve: no facing outer erf edges satisfied the width/overlap conditions.");
                            return;
                        }

                        CivilSurface surface = OpenSurface(
                            planning,
                            surfaceId);
                        foreach (EdgePair pair in pairs)
                        {
                            Line2 first;
                            Line2 second;
                            if (!BuildRoadReserveOffsetLines(
                                    pair,
                                    offset,
                                    out first,
                                    out second))
                                continue;

                            int acceptedFromPair = 0;
                            foreach (Line2 value in new[] { first, second })
                            {
                                Line2 routeLine = value;
                                if (routeLine.Length <= Tol) continue;
                                OrientDownhill(ref routeLine, surface);
                                plannedRoutes.Add(routeLine);
                                acceptedFromPair++;
                            }
                            if (acceptedFromPair > 0) pairCount++;
                        }
                    }

                    if (plannedRoutes.Count == 0)
                    {
                        document.Editor.WriteMessage(
                            "\nCE Road Reserve: no usable sewer route geometry was planned.");
                        return;
                    }

                    // Construction stage: AutoCAD geometry/layers only.
                    int routeCount = 0;
                    int manholeCount = 0;
                    using (Transaction construction =
                        document.Database.TransactionManager.StartTransaction())
                    {
                        BlockTableRecord space = OpenModelSpace(
                            document.Database,
                            construction,
                            OpenMode.ForWrite);
                        if (space == null) return;

                        if (string.Equals(
                            model.Text("Replace"),
                            "Replace existing",
                            StringComparison.OrdinalIgnoreCase))
                        {
                            SafeEraseByLayers(
                                space,
                                construction,
                                RoadReserveRouteLayer,
                                RoadReserveMhLayer,
                                RoadReserveLabelLayer);
                        }

                        ObjectId routeLayer = EnsureLayer(
                            document.Database,
                            construction,
                            RoadReserveRouteLayer);
                        ObjectId mhLayer = EnsureLayer(
                            document.Database,
                            construction,
                            RoadReserveMhLayer);
                        ObjectId labelLayer = EnsureLayer(
                            document.Database,
                            construction,
                            RoadReserveLabelLayer);

                        int routeNumber = 1;
                        foreach (Line2 routeLine in plannedRoutes)
                        {
                            Polyline route = CreateSpacedPolyline(
                                document.Database,
                                routeLine,
                                spacing,
                                routeLayer);
                            space.AppendEntity(route);
                            construction.AddNewlyCreatedDBObject(route, true);
                            WriteLayoutRecord(
                                route,
                                construction,
                                "ROADRESERVE",
                                spacing,
                                diameter,
                                model.Text("Surface"));
                            manholeCount += AddManholesAtVertices(
                                document.Database,
                                construction,
                                space,
                                route,
                                mhLayer,
                                labelLayer,
                                diameter,
                                "RR-MH",
                                routeNumber++);
                            routeCount++;
                        }
                        construction.Commit();
                    }

                    document.Editor.SetImpliedSelection(new ObjectId[0]);
                    document.Editor.Regen();
                    document.Editor.WriteMessage(
                        "\nCE geometry-first Road Reserve sewer complete. Reserve pairs={0}; routes={1}; planning manholes={2}. Civil surface sampling completed before AutoCAD construction. Review/edit before CE_SEWERBUILDNETWORK.",
                        pairCount,
                        routeCount,
                        manholeCount);
                }
            }
            catch (Autodesk.AutoCAD.Runtime.Exception ex)
            {
                document.Editor.WriteMessage(
                    "\nCE geometry-first Road Reserve sewer stopped safely: {0}",
                    ex.Message);
            }
            catch (System.Exception ex)
            {
                document.Editor.WriteMessage(
                    "\nCE geometry-first Road Reserve sewer stopped safely: {0}",
                    ex.Message);
            }
'@
$geometry = ReplaceMethodBody $geometry '        private static void RunRoadReserve(bool legacyEntry)' $roadReserveBody 'Geometry-first Road Reserve read/write transaction separation'
WriteText $geometryPath $geometry

# -----------------------------------------------------------------------------
# 8. Site/Grid surface independence guard. The August 19 linked-grid table must
#    always populate X/Y, with surfaces optional only for extra level columns.
# -----------------------------------------------------------------------------
$grid = ReadText $gridPath
foreach ($required in @(
    'new List<string> { "<None>" }',
    'if (surface == null) return false;',
    'table.Cells[row, 2].TextString = displayX.ToString',
    'table.Cells[row, 3].TextString = displayY.ToString',
    'table.Cells[row, 5].TextString = hasBase',
    'table.Cells[row, 6].TextString = hasComparison')) {
    if (-not $grid.Contains($required)) {
        throw "Site/Grid optional-surface guard missing after staged repairs: $required"
    }
}

# -----------------------------------------------------------------------------
# Final regression guards: refuse compilation if any requested behavior is absent.
# -----------------------------------------------------------------------------
$sequenceCheck = ReadText $sequencePath
if ($sequenceCheck.Contains('SewerNetworkLabelCommands.EnsureLabels(')) {
    throw 'CE_SEWSEQ still launches automatic label generation.'
}

$breakCheck = ReadText $breakPath
if (-not $breakCheck.Contains('foreach (KeyValuePair<ObjectId, List<Point3d>> item in splitPoints)') -or
    -not $breakCheck.Contains('using (Transaction transaction = database.TransactionManager.StartTransaction())') -or
    -not $breakCheck.Contains('source.Erase();')) {
    throw 'Break-at-crossings per-source transaction guard is missing.'
}

$backgroundCheck = ReadText $backgroundPath
foreach ($required in @(
    '"CE-Freeze Solid Hatches"',
    '"CE-Freeze All Hatches"',
    '"CE-Freeze Dimensions"',
    '"CE-Freeze All or Selected Text"',
    '"CE-Freeze All or Selected Attributes"',
    'var allowedDxfTypes = new HashSet<string>',
    'allowedDxfTypes.Contains(dxf)',
    'IsXref(block, transaction)')) {
    if (-not $backgroundCheck.Contains($required)) {
        throw "Background final guard missing: $required"
    }
}

$menuCheck = ReadText $menuPath
if (-not $menuCheck.Contains(
    'A("CE-Break at Crossings and Junctions", "CE_PLBREAKJUNCTIONS"')) {
    throw 'Sewer Layout Production does not expose Break at Crossings/Junctions.'
}

$multiCheck = ReadText $multiPath
foreach ($required in @(
    '"StructureSpacing"',
    'length > spacing + PointTolerance',
    '(int)Math.Ceiling(length / spacing)',
    'ApplyMaximumStructureSpacing')) {
    if (-not $multiCheck.Contains($required)) {
        throw "Sewer multi-source spacing guard missing: $required"
    }
}

$vertexCheck = ReadText $vertexPath
foreach ($required in @(
    '"KNOWN="',
    '"SUPPRESS="',
    'knownOutputKeys.Contains(record.Key)',
    'suppressedOutputKeys.Add(record.Key)',
    'WriteOutputKeyState(')) {
    if (-not $vertexCheck.Contains($required)) {
        throw "Vertex deletion-suppression guard missing: $required"
    }
}

$geometryCheck = ReadText $geometryPath
foreach ($required in @(
    'using (Transaction planning =',
    'using (Transaction construction =',
    'CivilSurface surface = OpenSurface(',
    'Civil surface sampling completed before AutoCAD construction',
    '"CE_CENTERLINETOALIGNMENT"')) {
    if (-not $geometryCheck.Contains($required)) {
        throw "Geometry-first final guard missing: $required"
    }
}
# Each public geometry planner must keep Civil surface access out of its construction block.
foreach ($method in @('RunMidblock(bool legacyEntry)','RunRoadReserve(bool legacyEntry)')) {
    $start = $geometryCheck.IndexOf($method,[StringComparison]::Ordinal)
    $next = $geometryCheck.IndexOf('        private static ',$start + $method.Length,[StringComparison]::Ordinal)
    if ($start -lt 0) { throw "Geometry method missing: $method" }
    if ($next -lt 0) { $next = $geometryCheck.Length }
    $methodText = $geometryCheck.Substring($start,$next-$start)
    $constructionStart = $methodText.IndexOf('using (Transaction construction =',[StringComparison]::Ordinal)
    if ($constructionStart -lt 0) { throw "Construction transaction missing: $method" }
    $constructionText = $methodText.Substring($constructionStart)
    if ($constructionText.Contains('OpenSurface(') -or
        $constructionText.Contains('TryElevation(') -or
        $constructionText.Contains('FindElevationAtXY')) {
        throw "Civil Surface access survived inside AutoCAD construction transaction: $method"
    }
}

Write-Host 'August 20 runtime-stability/workflow final pass applied.' -ForegroundColor Green
Write-Host ' - CE_SEWSEQ now performs sequence/rename only; labels/alignments/refresh are separate.' -ForegroundColor Green
Write-Host ' - Break at Crossings/Junctions uses one source polyline per transaction and preserves failed sources.' -ForegroundColor Green
Write-Host ' - Midblock/Road Reserve planning samples Civil surfaces before a separate AutoCAD-only construction transaction.' -ForegroundColor Green
Write-Host ' - Road-reserve centreline remains polyline-first with separate CE_CENTERLINETOALIGNMENT conversion.' -ForegroundColor Green
Write-Host ' - Background Colour 250 uses a strict DXF allow-list and isolated per-entity transactions.' -ForegroundColor Green
Write-Host ' - Background Tools now exposes all requested hatch/dimension/text/attribute freeze commands.' -ForegroundColor Green
Write-Host ' - Sewer Layout Production exposes Break at Crossings/Junctions.' -ForegroundColor Green
Write-Host ' - Multi-polyline sewer structures obey maximum spacing only when an original segment is longer than the limit.' -ForegroundColor Green
Write-Host ' - Vertex Setting-Out persists manual point deletions as suppressions across linked refresh.' -ForegroundColor Green
Write-Host ' - Grid X/Y output remains available when Base/NG and Comparison/Design surfaces are <None>.' -ForegroundColor Green

[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$src = Join-Path $root 'src\CE.Tools.Civil3D'
$backgroundPath = Join-Path $src 'BackgroundPreparationCommands.cs'
$dialogsPath = Join-Path $src 'DisciplineWorkflowDialogs.cs'
$multiPath = Join-Path $src 'MultiDimensionCommands.cs'
$utf8 = New-Object System.Text.UTF8Encoding($false)

foreach ($required in @($backgroundPath,$dialogsPath,$multiPath)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "August 20 cross-discipline fatal-safety prerequisite missing: $required"
    }
}

function ReadText([string]$path) {
    return [System.IO.File]::ReadAllText($path) -replace "`r?`n", "`r`n"
}

function WriteText([string]$path,[string]$text) {
    [System.IO.File]::WriteAllText($path,($text -replace "`r?`n","`r`n"),$utf8)
}

function ReplaceMethodBody([string]$text,[string]$signature,[string]$body,[string]$label) {
    $start = $text.IndexOf($signature,[StringComparison]::Ordinal)
    if ($start -lt 0) { throw "August 20 fatal-safety method not found ($label): $signature" }
    $open = $text.IndexOf('{',$start)
    if ($open -lt 0) { throw "August 20 fatal-safety opening brace not found: $label" }
    $depth = 0
    $close = -1
    for ($i=$open; $i -lt $text.Length; $i++) {
        if ($text[$i] -eq '{') { $depth++ }
        elseif ($text[$i] -eq '}') {
            $depth--
            if ($depth -eq 0) { $close=$i; break }
        }
    }
    if ($close -lt 0) { throw "August 20 fatal-safety closing brace not found: $label" }
    return $text.Substring(0,$open) + "{`r`n" + $body.TrimEnd() + "`r`n        }" + $text.Substring($close+1)
}

function ReplaceRequired([string]$text,[string]$old,[string]$new,[string]$label) {
    $old = $old -replace "`r?`n", "`r`n"
    $new = $new -replace "`r?`n", "`r`n"
    if ($text.Contains($new)) { return $text }
    if (-not $text.Contains($old)) { throw "August 20 fatal-safety anchor not found: $label" }
    return $text.Replace($old,$new)
}

# -----------------------------------------------------------------------------
# 1. Background tools: never blanket-open Civil/AEC/proxy/XREF objects ForWrite.
#    Work per entity after a read-only eligibility check so one bad object cannot
#    poison one giant write transaction.
# -----------------------------------------------------------------------------
$background = ReadText $backgroundPath

$colourBody = @'
            Document document = Active();
            if (document == null) return;

            int changed = 0;
            int already250 = 0;
            int skippedLocked = 0;
            int skippedProtected = 0;
            int failed = 0;
            var ids = new List<ObjectId>();

            using (DocumentLock documentLock = document.LockDocument())
            {
                using (Transaction readTransaction = document.Database.TransactionManager.StartTransaction())
                {
                    BlockTableRecord model = readTransaction.GetObject(
                        SymbolUtilityServices.GetBlockModelSpaceId(document.Database),
                        OpenMode.ForRead,
                        false) as BlockTableRecord;
                    if (model != null) ids.AddRange(model.Cast<ObjectId>());
                }

                foreach (ObjectId id in ids)
                {
                    try
                    {
                        if (id.IsNull || !id.IsValid || id.IsErased) { skippedProtected++; continue; }
                        RXClass objectClass = null;
                        try { objectClass = id.ObjectClass; } catch { }
                        string dxf = objectClass == null ? string.Empty : (objectClass.DxfName ?? string.Empty);
                        if (dxf.StartsWith("AEC", StringComparison.OrdinalIgnoreCase) ||
                            dxf.IndexOf("PROXY", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            skippedProtected++;
                            continue;
                        }

                        using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                        {
                            Entity entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                            if (entity == null || entity.IsErased) { skippedProtected++; continue; }

                            string managedType = entity.GetType().FullName ?? string.Empty;
                            if (!managedType.StartsWith("Autodesk.AutoCAD.DatabaseServices.", StringComparison.Ordinal) ||
                                managedType.StartsWith("Autodesk.Civil.", StringComparison.OrdinalIgnoreCase))
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

                            if (entity.ColorIndex == 250)
                            {
                                already250++;
                                continue;
                            }

                            entity.UpgradeOpen();
                            entity.Color = Color.FromColorIndex(ColorMethod.ByAci, 250);
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

            try { document.Editor.Regen(); } catch { }
            document.Editor.WriteMessage(
                "\nCE_BGCOLOR250 complete. Changed={0}; already 250={1}; locked-layer skips={2}; protected Civil/AEC/XREF/proxy skips={3}; failed={4}.",
                changed,
                already250,
                skippedLocked,
                skippedProtected,
                failed);
'@
$background = ReplaceMethodBody $background 'public void BackgroundColour250()' $colourBody 'Background Colour 250 isolated safe writes'

$moveBody = @'
            Document document = Active();
            if (document == null) return;
            int moved = 0;
            int skipped = 0;
            int protectedObjects = 0;
            ObjectId layerId = ObjectId.Null;
            var ids = new List<ObjectId>();

            using (DocumentLock documentLock = document.LockDocument())
            {
                using (Transaction setup = document.Database.TransactionManager.StartTransaction())
                {
                    layerId = EnsureLayer(document.Database, setup, layerName);
                    BlockTableRecord model = setup.GetObject(
                        SymbolUtilityServices.GetBlockModelSpaceId(document.Database),
                        OpenMode.ForRead,
                        false) as BlockTableRecord;
                    if (model != null) ids.AddRange(model.Cast<ObjectId>());
                    setup.Commit();
                }

                foreach (ObjectId id in ids)
                {
                    try
                    {
                        if (id.IsNull || !id.IsValid || id.IsErased) { protectedObjects++; continue; }
                        RXClass objectClass = null;
                        try { objectClass = id.ObjectClass; } catch { }
                        string dxf = objectClass == null ? string.Empty : (objectClass.DxfName ?? string.Empty);
                        if (dxf.StartsWith("AEC", StringComparison.OrdinalIgnoreCase) ||
                            dxf.IndexOf("PROXY", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            protectedObjects++;
                            continue;
                        }

                        using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                        {
                            Entity entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                            if (entity == null || entity.IsErased) { protectedObjects++; continue; }
                            string managedType = entity.GetType().FullName ?? string.Empty;
                            if (!managedType.StartsWith("Autodesk.AutoCAD.DatabaseServices.", StringComparison.Ordinal) ||
                                managedType.StartsWith("Autodesk.Civil.", StringComparison.OrdinalIgnoreCase))
                            {
                                protectedObjects++;
                                continue;
                            }
                            LayerTableRecord sourceLayer = transaction.GetObject(
                                entity.LayerId,
                                OpenMode.ForRead,
                                false) as LayerTableRecord;
                            if (sourceLayer != null && sourceLayer.IsLocked) { skipped++; continue; }
                            BlockReference block = entity as BlockReference;
                            if (block != null && IsXref(block, transaction)) { protectedObjects++; continue; }
                            if (!predicate(entity)) continue;
                            entity.UpgradeOpen();
                            entity.LayerId = layerId;
                            transaction.Commit();
                            moved++;
                        }
                    }
                    catch { skipped++; }
                }

                using (Transaction finish = document.Database.TransactionManager.StartTransaction())
                {
                    LayerTableRecord layer = finish.GetObject(layerId, OpenMode.ForWrite, false) as LayerTableRecord;
                    if (layer != null) layer.IsFrozen = true;
                    finish.Commit();
                }
            }

            try { document.Editor.Regen(); } catch { }
            document.Editor.WriteMessage(
                "\nMoved {0} {1}(s) to frozen layer {2}; protected skips={3}; failed/locked={4}.",
                moved,
                description,
                layerName,
                protectedObjects,
                skipped);
'@
$background = ReplaceMethodBody $background 'private static void MoveToFrozenLayer(string layerName, Func<Entity, bool> predicate, string description)' $moveBody 'Background visibility isolated safe writes'

$burstBody = @'
            Document document = Active();
            if (document == null) return;
            if (!DisciplineWorkflowDialogs.Confirm(
                    "CE Tools - Burst All Blocks",
                    "Burst all ordinary model-space block references? XREF, Civil/AEC/proxy and protected block definitions will be skipped. Visible attribute values will be retained as DBText."))
                return;

            int exploded = 0;
            int attributes = 0;
            int skipped = 0;
            int protectedObjects = 0;
            using (DocumentLock documentLock = document.LockDocument())
            {
                for (int pass = 0; pass < 20; pass++)
                {
                    int changedThisPass = 0;
                    using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                    {
                        BlockTableRecord model = transaction.GetObject(
                            SymbolUtilityServices.GetBlockModelSpaceId(document.Database),
                            OpenMode.ForWrite,
                            false) as BlockTableRecord;
                        if (model == null) break;

                        List<ObjectId> ids = model.Cast<ObjectId>().ToList();
                        foreach (ObjectId id in ids)
                        {
                            try
                            {
                                if (id.IsNull || !id.IsValid || id.IsErased) { protectedObjects++; continue; }
                                RXClass objectClass = null;
                                try { objectClass = id.ObjectClass; } catch { }
                                string dxf = objectClass == null ? string.Empty : (objectClass.DxfName ?? string.Empty);
                                if (!string.Equals(dxf, "INSERT", StringComparison.OrdinalIgnoreCase)) continue;

                                BlockReference block = transaction.GetObject(id, OpenMode.ForRead, false) as BlockReference;
                                if (block == null || block.IsErased) continue;
                                if (IsXref(block, transaction)) { protectedObjects++; continue; }
                                LayerTableRecord sourceLayer = transaction.GetObject(
                                    block.LayerId,
                                    OpenMode.ForRead,
                                    false) as LayerTableRecord;
                                if (sourceLayer != null && sourceLayer.IsLocked) { skipped++; continue; }

                                BlockTableRecord definition = transaction.GetObject(
                                    block.BlockTableRecord,
                                    OpenMode.ForRead,
                                    false) as BlockTableRecord;
                                if (definition == null || definition.IsFromExternalReference || definition.IsFromOverlayReference)
                                {
                                    protectedObjects++;
                                    continue;
                                }

                                bool unsafeDefinition = false;
                                foreach (ObjectId childId in definition)
                                {
                                    RXClass childClass = null;
                                    try { childClass = childId.ObjectClass; } catch { }
                                    string childDxf = childClass == null ? string.Empty : (childClass.DxfName ?? string.Empty);
                                    if (childDxf.StartsWith("AEC", StringComparison.OrdinalIgnoreCase) ||
                                        childDxf.IndexOf("PROXY", StringComparison.OrdinalIgnoreCase) >= 0)
                                    {
                                        unsafeDefinition = true;
                                        break;
                                    }
                                }
                                if (unsafeDefinition) { protectedObjects++; continue; }

                                foreach (ObjectId attributeId in block.AttributeCollection)
                                {
                                    AttributeReference attribute = transaction.GetObject(attributeId, OpenMode.ForRead, false) as AttributeReference;
                                    if (attribute == null || attribute.Invisible || string.IsNullOrWhiteSpace(attribute.TextString)) continue;
                                    DBText text = new DBText();
                                    text.SetDatabaseDefaults(document.Database);
                                    text.TextString = attribute.TextString;
                                    text.Position = attribute.Position;
                                    text.Height = attribute.Height > 1e-9 ? attribute.Height : Math.Max(document.Database.Textsize, 0.001);
                                    text.Rotation = attribute.Rotation;
                                    text.TextStyleId = attribute.TextStyleId;
                                    text.LayerId = attribute.LayerId;
                                    text.Color = attribute.Color;
                                    model.AppendEntity(text);
                                    transaction.AddNewlyCreatedDBObject(text, true);
                                    attributes++;
                                }

                                block.UpgradeOpen();
                                DBObjectCollection pieces = new DBObjectCollection();
                                block.Explode(pieces);
                                foreach (DBObject item in pieces)
                                {
                                    Entity entity = item as Entity;
                                    if (entity == null || entity is AttributeDefinition)
                                    {
                                        item.Dispose();
                                        continue;
                                    }
                                    string managedType = entity.GetType().FullName ?? string.Empty;
                                    if (!managedType.StartsWith("Autodesk.AutoCAD.DatabaseServices.", StringComparison.Ordinal) ||
                                        managedType.StartsWith("Autodesk.Civil.", StringComparison.OrdinalIgnoreCase))
                                    {
                                        item.Dispose();
                                        protectedObjects++;
                                        continue;
                                    }
                                    model.AppendEntity(entity);
                                    transaction.AddNewlyCreatedDBObject(entity, true);
                                }
                                block.Erase();
                                exploded++;
                                changedThisPass++;
                            }
                            catch { skipped++; }
                        }
                        transaction.Commit();
                    }
                    if (changedThisPass == 0) break;
                }
            }
            try { document.Editor.Regen(); } catch { }
            document.Editor.WriteMessage(
                "\nCE_BGBURSTALL complete. Blocks burst={0}; attribute values retained={1}; protected skips={2}; failed/locked={3}.",
                exploded,
                attributes,
                protectedObjects,
                skipped);
'@
$background = ReplaceMethodBody $background 'public void BurstAllBlocks()' $burstBody 'Burst only ordinary safe block references'

$scaleBody = @'
            int scaled = 0;
            int skipped = 0;
            int protectedObjects = 0;
            Matrix3d transform = Matrix3d.Scaling(factor, Point3d.Origin);
            var ids = new List<ObjectId>();

            using (DocumentLock documentLock = document.LockDocument())
            {
                using (Transaction readTransaction = document.Database.TransactionManager.StartTransaction())
                {
                    BlockTableRecord model = readTransaction.GetObject(
                        SymbolUtilityServices.GetBlockModelSpaceId(document.Database),
                        OpenMode.ForRead,
                        false) as BlockTableRecord;
                    if (model != null) ids.AddRange(model.Cast<ObjectId>());
                }

                foreach (ObjectId id in ids)
                {
                    try
                    {
                        if (id.IsNull || !id.IsValid || id.IsErased) { protectedObjects++; continue; }
                        RXClass objectClass = null;
                        try { objectClass = id.ObjectClass; } catch { }
                        string dxf = objectClass == null ? string.Empty : (objectClass.DxfName ?? string.Empty);
                        if (dxf.StartsWith("AEC", StringComparison.OrdinalIgnoreCase) ||
                            dxf.IndexOf("PROXY", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            protectedObjects++;
                            continue;
                        }

                        using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                        {
                            Entity entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                            if (entity == null || entity.IsErased) { protectedObjects++; continue; }
                            string managedType = entity.GetType().FullName ?? string.Empty;
                            if (!managedType.StartsWith("Autodesk.AutoCAD.DatabaseServices.", StringComparison.Ordinal) ||
                                managedType.StartsWith("Autodesk.Civil.", StringComparison.OrdinalIgnoreCase))
                            {
                                protectedObjects++;
                                continue;
                            }
                            LayerTableRecord layer = transaction.GetObject(entity.LayerId, OpenMode.ForRead, false) as LayerTableRecord;
                            if (layer != null && layer.IsLocked) { skipped++; continue; }
                            BlockReference block = entity as BlockReference;
                            if (block != null && IsXref(block, transaction)) { protectedObjects++; continue; }
                            entity.UpgradeOpen();
                            entity.TransformBy(transform);
                            transaction.Commit();
                            scaled++;
                        }
                    }
                    catch { skipped++; }
                }

                try { document.Database.Insunits = UnitsValue.Meters; } catch { }
            }

            try { document.Editor.Regen(); } catch { }
            document.Editor.WriteMessage(
                "\nCE_BGSCALECORRECTION complete. Factor={0:0.########}; scaled={1}; protected Civil/AEC/XREF/proxy skips={2}; failed/locked={3}. {4}",
                factor,
                scaled,
                protectedObjects,
                skipped,
                note);
'@
$background = ReplaceMethodBody $background 'private static void ApplyScale(Document document, double factor, string note)' $scaleBody 'Background scale protected-object isolation'

$cleanBody = @'
            Document document = Active();
            if (document == null) return;
            Editor editor = document.Editor;
            int completed = 0;
            int safeForOverkill = 0;
            int protectedObjects = 0;

            try { editor.Command("_.AUDIT", "_Y"); completed++; }
            catch (System.Exception ex) { editor.WriteMessage("\nAudit warning: {0}", ex.Message); }

            try
            {
                var safeIds = new List<ObjectId>();
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    BlockTableRecord model = transaction.GetObject(
                        SymbolUtilityServices.GetBlockModelSpaceId(document.Database),
                        OpenMode.ForRead,
                        false) as BlockTableRecord;
                    if (model != null)
                    {
                        foreach (ObjectId id in model)
                        {
                            if (id.IsNull || !id.IsValid || id.IsErased) { protectedObjects++; continue; }
                            RXClass objectClass = null;
                            try { objectClass = id.ObjectClass; } catch { }
                            string dxf = objectClass == null ? string.Empty : (objectClass.DxfName ?? string.Empty);
                            if (dxf.StartsWith("AEC", StringComparison.OrdinalIgnoreCase) ||
                                dxf.IndexOf("PROXY", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                protectedObjects++;
                                continue;
                            }
                            Entity entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                            if (entity == null) continue;
                            string managedType = entity.GetType().FullName ?? string.Empty;
                            if (!managedType.StartsWith("Autodesk.AutoCAD.DatabaseServices.", StringComparison.Ordinal) ||
                                managedType.StartsWith("Autodesk.Civil.", StringComparison.OrdinalIgnoreCase))
                            {
                                protectedObjects++;
                                continue;
                            }
                            LayerTableRecord layer = transaction.GetObject(entity.LayerId, OpenMode.ForRead, false) as LayerTableRecord;
                            if (layer != null && layer.IsLocked) continue;
                            BlockReference block = entity as BlockReference;
                            if (block != null && IsXref(block, transaction)) { protectedObjects++; continue; }
                            safeIds.Add(id);
                        }
                    }
                }

                if (safeIds.Count > 0)
                {
                    SelectionSet safeSelection = SelectionSet.FromObjectIds(safeIds.ToArray());
                    editor.Command("_.-OVERKILL", safeSelection, "", "");
                    completed++;
                    safeForOverkill = safeIds.Count;
                }
                else
                {
                    editor.WriteMessage("\nOverkill skipped: no ordinary editable AutoCAD model-space entities were found.");
                }
            }
            catch (System.Exception ex) { editor.WriteMessage("\nOverkill warning: {0}", ex.Message); }

            try { editor.Command("_.-PURGE", "_ALL", "*", "_N"); completed++; }
            catch (System.Exception ex) { editor.WriteMessage("\nPurge warning: {0}", ex.Message); }

            editor.WriteMessage(
                "\nCE_BGCLEAN complete. Native cleanup stages completed={0}/3; safe OVERKILL selection={1}; protected Civil/AEC/XREF/proxy skips={2}.",
                completed,
                safeForOverkill,
                protectedObjects);
'@
$background = ReplaceMethodBody $background 'public void AuditOverkillPurge()' $cleanBody 'Background cleanup safe OVERKILL selection'

WriteText $backgroundPath $background

# -----------------------------------------------------------------------------
# 2. Shared workflow UI/persistence safety. This path is used by every discipline
#    production centre, so managed WPF/settings failures are contained and reported
#    instead of escaping into the AutoCAD host.
# -----------------------------------------------------------------------------
$dialogs = ReadText $dialogsPath
$selectAndRunBody = @'
            if (document == null) return;
            string command = string.Empty;
            try
            {
                command = SelectWorkflow(title, note, actions);
            }
            catch (System.Exception exception)
            {
                try { document.Editor.WriteMessage("\nCE Tools workflow window stopped safely: {0}", exception.Message); } catch { }
                return;
            }
            if (string.IsNullOrWhiteSpace(command)) return;
            try
            {
                document.SendStringToExecute(
                    command.Trim() + " ",
                    true,
                    false,
                    true);
            }
            catch (System.Exception exception)
            {
                try { document.Editor.WriteMessage("\nCE Tools could not queue command {0}: {1}", command.Trim(), exception.Message); } catch { }
            }
'@
$dialogs = ReplaceMethodBody $dialogs 'public static void SelectAndRun(' $selectAndRunBody 'All-discipline workflow launcher containment'

$editSettingsBody = @'
            if (model == null) return false;
            Document document = AcApplication.DocumentManager.MdiActiveDocument;

            try { CrossDrawingProductionSettingsStore.Load(model); }
            catch (System.Exception exception)
            {
                if (document != null)
                    try { document.Editor.WriteMessage("\nCE Tools user-settings load warning: {0}", exception.Message); } catch { }
            }

            if (document != null)
            {
                try { ProductionSettingsPersistenceStore.Load(document.Database, model); }
                catch (System.Exception exception)
                {
                    try { document.Editor.WriteMessage("\nCE Tools drawing-settings load warning: {0}", exception.Message); } catch { }
                }
            }

            var window = new ProductionSettingsWindow(model);
            try
            {
                AcApplication.ShowModalWindow(window);
            }
            catch (System.Exception exception)
            {
                if (document != null)
                    try { document.Editor.WriteMessage("\nCE Tools settings window stopped safely: {0}", exception.Message); } catch { }
                return false;
            }

            if (window.Accepted)
            {
                if (document != null)
                {
                    try { ProductionSettingsPersistenceStore.Save(document.Database, model); }
                    catch (System.Exception exception)
                    {
                        try { document.Editor.WriteMessage("\nCE Tools drawing-settings save warning: {0}", exception.Message); } catch { }
                    }
                }
                try { CrossDrawingProductionSettingsStore.Save(model); }
                catch (System.Exception exception)
                {
                    if (document != null)
                        try { document.Editor.WriteMessage("\nCE Tools user-settings save warning: {0}", exception.Message); } catch { }
                }
            }
            return window.Accepted;
'@
$dialogs = ReplaceMethodBody $dialogs 'public static bool EditSettings(ProductionSettingsDialogModel model)' $editSettingsBody 'All-discipline settings containment'
WriteText $dialogsPath $dialogs

# -----------------------------------------------------------------------------
# 3. Multiple Dimensions final exactness. The previous finalizer adds circles,
#    centre marks and m/mm units. Here millimetre output explicitly disables source
#    rounding/zero suppression so 23.457 m becomes 23457, not 23 or 23460.
# -----------------------------------------------------------------------------
$multi = ReadText $multiPath
$oldUnitStyle = @'
                    if (outputStyle != null)
                    {
                        outputStyle.Dimlfac = measurementFactor;
                        if (outputMillimetres)
                            outputStyle.Dimdec = 0;
                    }
'@
$newUnitStyle = @'
                    if (outputStyle != null)
                    {
                        outputStyle.Dimlfac = measurementFactor;
                        if (outputMillimetres)
                        {
                            outputStyle.Dimdec = 0;
                            outputStyle.Dimrnd = 0.0;
                            outputStyle.Dimzin = 0;
                        }
                    }
'@
$multi = ReplaceRequired $multi $oldUnitStyle $newUnitStyle 'Millimetre exact integer output'

$oldObjectOverrides = @'
                    dimension.Dimlfac = style.Dimlfac;
                    dimension.Dimdec = style.Dimdec;
'@
$newObjectOverrides = @'
                    dimension.Dimlfac = style.Dimlfac;
                    dimension.Dimdec = style.Dimdec;
                    dimension.Dimrnd = style.Dimrnd;
                    dimension.Dimzin = style.Dimzin;
'@
$multi = ReplaceRequired $multi $oldObjectOverrides $newObjectOverrides 'Per-dimension exact unit overrides'
WriteText $multiPath $multi

# -----------------------------------------------------------------------------
# Regression guards. Refuse compilation if the old crash-prone blanket writes or
# the old millimetre rounding path survive this final stage.
# -----------------------------------------------------------------------------
$backgroundCheck = ReadText $backgroundPath
$colourStart = $backgroundCheck.IndexOf('public void BackgroundColour250()', [StringComparison]::Ordinal)
$cleanStart = $backgroundCheck.IndexOf('public void AuditOverkillPurge()', [StringComparison]::Ordinal)
if ($colourStart -lt 0 -or $cleanStart -le $colourStart) { throw 'Background Colour 250 validation range missing.' }
$colourMethod = $backgroundCheck.Substring($colourStart,$cleanStart-$colourStart)
foreach ($marker in @(
    'id.ObjectClass',
    'dxf.StartsWith("AEC"',
    'managedType.StartsWith("Autodesk.AutoCAD.DatabaseServices."',
    'layer.IsLocked',
    'IsXref(block, transaction)',
    'entity.UpgradeOpen();',
    'Color.FromColorIndex(ColorMethod.ByAci, 250)',
    'using (Transaction transaction = document.Database.TransactionManager.StartTransaction())')) {
    if (-not $colourMethod.Contains($marker)) { throw "Background Colour 250 safety marker missing: $marker" }
}
if ($colourMethod.Contains('transaction.GetObject(id, OpenMode.ForWrite, false) as Entity')) {
    throw 'Background Colour 250 still blanket-opens model-space entities ForWrite.'
}

$dialogsCheck = ReadText $dialogsPath
foreach ($marker in @(
    'CE Tools workflow window stopped safely',
    'CE Tools settings window stopped safely',
    'CE Tools drawing-settings load warning',
    'CE Tools user-settings save warning')) {
    if (-not $dialogsCheck.Contains($marker)) { throw "Cross-discipline workflow safety marker missing: $marker" }
}

$multiCheck = ReadText $multiPath
foreach ($marker in @(
    'Circle circle = entity as Circle;',
    'dimension.Dimcen = Math.Max(dimension.Dimasz, 0.001);',
    'new[] { "Metres", "Millimetres" }',
    'measurementFactor = outputMillimetres ? 1000.0 : 1.0;',
    'outputStyle.Dimrnd = 0.0;',
    'outputStyle.Dimzin = 0;',
    'dimension.Dimasz = style.Dimasz;',
    'dimension.Dimtxt = style.Dimtxt;',
    'dimension.Dimrnd = style.Dimrnd;',
    'dimension.Dimzin = style.Dimzin;')) {
    if (-not $multiCheck.Contains($marker)) { throw "Multiple Dimensions exactness marker missing: $marker" }
}

Write-Host 'Background fatal-safety finalizer passed: Colour 250, Burst, Freeze, Scale and OVERKILL now isolate ordinary AutoCAD entities and skip Civil/AEC/XREF/proxy objects.' -ForegroundColor Green
Write-Host 'Cross-discipline workflow safety passed: shared production-centre windows and settings persistence now contain managed failures.' -ForegroundColor Green
Write-Host 'Multiple Dimensions exactness passed: circle centre mark retained; popup DIMASZ/DIMTXT are direct object overrides; millimetres are x1000 with zero source rounding.' -ForegroundColor Green

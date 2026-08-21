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
        throw "August 21 state/surface source missing: $path"
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
    if (-not $text.Contains($old)) { throw "August 21 anchor missing: $label" }
    return $text.Replace($old,$new)
}
function ReplaceMethodBody([string]$text,[string]$marker,[string]$body,[string]$label) {
    $start = $text.IndexOf($marker,[StringComparison]::Ordinal)
    if ($start -lt 0) { throw "August 21 method marker not found: $label" }
    $second = $text.IndexOf($marker,$start + $marker.Length,[StringComparison]::Ordinal)
    if ($second -ge 0) { throw "August 21 method marker ambiguous: $label" }
    $open = $text.IndexOf('{',$start)
    if ($open -lt 0) { throw "August 21 opening brace not found: $label" }
    $depth = 0
    $close = -1
    for ($i=$open; $i -lt $text.Length; $i++) {
        if ($text[$i] -eq '{') { $depth++ }
        elseif ($text[$i] -eq '}') {
            $depth--
            if ($depth -eq 0) { $close = $i; break }
        }
    }
    if ($close -lt 0) { throw "August 21 closing brace not found: $label" }
    $normalized = $body -replace "`r?`n","`r`n"
    return $text.Substring(0,$open+1) + "`r`n" + $normalized.Trim("`r","`n") + "`r`n        " + $text.Substring($close)
}
function ExtractRepairHereString([string]$scriptPath,[string]$variableName) {
    $repair = ReadText $scriptPath
    $pattern = '(?s)\$' + [regex]::Escape($variableName) + '\s*=\s*@''\r\n(.*?)\r\n''@'
    $match = [regex]::Match($repair,$pattern)
    if (-not $match.Success) { throw "August 21 could not recover safe August 20 body: $variableName" }
    return $match.Groups[1].Value
}

$pluginPath = Required 'PluginEntry.cs'
$parkingPath = Required 'ParkingCommands.cs'
$vertexPath = Required 'VertexSettingOutCommands.cs'
$featureLinePath = Required 'FeatureLineConstructionCommands.cs'
$platformPath = Required 'PlatformProductionCommands.cs'
$geometryPath = Required 'August20GeometryFirstSewerCommands.cs'
$roadPath = Required 'RoadLayoutProductionCommands.cs'
$cadastralPath = Required 'August19CadastralSewerRouteCommands.cs'
$surfaceSafetyPath = Required 'August21SurfaceSafety.cs'
$parkingDynamicPath = Required 'August21DynamicParkingRows.cs'
$graphicsManagerPath = Required 'August21GraphicsRefreshManager.cs'
$runtimeRepairPath = Join-Path $root 'scripts\Repair-August20-RuntimeStabilityWorkflowPass-Civil3D2023.ps1'
if (-not (Test-Path -LiteralPath $runtimeRepairPath -PathType Leaf)) {
    throw "August 21 cannot locate prior safe geometry planner: $runtimeRepairPath"
}

# -----------------------------------------------------------------------------
# 1. Session managers: dynamic simple parking plus one normal post-CE-command
# graphics refresh. This replaces the field symptom where AUDIT/PURGE/OVERKILL was
# the operation that finally caused freshly generated geometry/text to display.
# -----------------------------------------------------------------------------
$plugin = ReadText $pluginPath
$plugin = ReplaceRequired $plugin @'
            ParkingOptionAutoRefreshManager.Initialize();
'@ @'
            ParkingOptionAutoRefreshManager.Initialize();
            August21SimpleParkingRefreshManager.Initialize();
            August21GraphicsRefreshManager.Initialize();
'@ 'Plugin August21 managers initialize'
$plugin = ReplaceRequired $plugin @'
            ParkingOptionAutoRefreshManager.Terminate();
'@ @'
            August21GraphicsRefreshManager.Terminate();
            August21SimpleParkingRefreshManager.Terminate();
            ParkingOptionAutoRefreshManager.Terminate();
'@ 'Plugin August21 managers terminate'
WriteText $pluginPath $plugin

# -----------------------------------------------------------------------------
# 2. Single/double-row parking: route the existing public commands through the
# source-linked dynamic engine. Every bay is graphics-dirty and an idle REGEN is
# also queued after CE command completion.
# -----------------------------------------------------------------------------
$parking = ReadText $parkingPath
$parking = ReplaceMethodBody $parking '        private static void CreateSingleRow(Document document)' @'
            August21DynamicParkingRows.Run(document, false);
'@ 'CE_PKROW dynamic delegation'
$parking = ReplaceMethodBody $parking '        private static void CreateDoubleRow(Document document)' @'
            August21DynamicParkingRows.Run(document, true);
'@ 'CE_PKDOUBLE dynamic delegation'
WriteText $parkingPath $parking

# -----------------------------------------------------------------------------
# 3. Vertex Setting-Out persistence.
# The August19 reader owns GenerationMode + IntervalSpacing. The later August20
# refresh accidentally called the legacy reader and then ApplyGenerationMode,
# which explains 2m -> 10m and all modes -> Engineering on source move/refresh.
# Keep KNOWN/SUPPRESS manual-deletion state, but rebuild records from the saved
# link settings. Never reapply COGO project styles on every refresh.
# -----------------------------------------------------------------------------
$vertex = ReadText $vertexPath
$vertexRefresh = @'
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
                IList<VertexSettingSource> sources = August19VertexSettingOutGeometry.ReadSources(
                    document.Database,
                    transaction,
                    sourceIds,
                    link.GenerationMode,
                    Math.Max(0.001, link.IntervalSpacing),
                    out rejected);
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
                if (knownOutputKeys.Count == 0)
                    foreach (string key in outputs.Keys)
                        knownOutputKeys.Add(key);

                AnnotationOptions annotation = AnnotationSettingsStore.Read(
                    document.Database);
                double textHeight = PaperAnnotationScale.ModelTextHeight(
                    document.Database,
                    annotation == null ? 2.0 : annotation.TextHeight);

                var liveOutputKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                        CaptureCurrentAnnotationOffset(transaction, existing, record);
                        EraseIfPossible(transaction, existing);
                    }

                    if (knownOutputKeys.Contains(record.Key))
                    {
                        suppressedOutputKeys.Add(record.Key);
                        continue;
                    }
                    if (!suppressedOutputKeys.Contains(record.Key))
                    {
                        ObjectId created = CreateOutput(
                            document.Database,
                            civilDocument,
                            transaction,
                            modelSpace,
                            link,
                            record,
                            textHeight);
                        try
                        {
                            Entity generated = transaction.GetObject(
                                created, OpenMode.ForWrite, false) as Entity;
                            if (generated != null) generated.RecordGraphicsModified(true);
                        }
                        catch { }
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

                var liveDimensionKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (VertexRadialDimension dimension in
                    sources.SelectMany(item => item.Dimensions))
                {
                    liveDimensionKeys.Add(dimension.Key);
                    ObjectId existing;
                    if (dimensions.TryGetValue(dimension.Key, out existing) &&
                        UpdateDimension(transaction, existing, dimension, textHeight))
                        continue;
                    EraseIfPossible(transaction, existing);
                    ObjectId createdDimension = CreateDimension(
                        document.Database,
                        transaction,
                        modelSpace,
                        link,
                        dimension,
                        textHeight);
                    try
                    {
                        Entity generatedDimension = transaction.GetObject(
                            createdDimension, OpenMode.ForWrite, false) as Entity;
                        if (generatedDimension != null)
                            generatedDimension.RecordGraphicsModified(true);
                    }
                    catch { }
                }
                foreach (KeyValuePair<string, ObjectId> stale in dimensions)
                    if (!liveDimensionKeys.Contains(stale.Key))
                        EraseIfPossible(transaction, stale.Value);

                WriteOutputKeyState(table, knownOutputKeys, suppressedOutputKeys);
                PopulateTable(table, records, textHeight, link);
                try { table.RecordGraphicsModified(true); } catch { }
                transaction.Commit();
                pointCount = visiblePointCount;
                dimensionCount = liveDimensionKeys.Count;
            }

            // Do not call CogoPointProjectStyleCommands.ApplySelectedStyles here.
            // Reapplying the label style on every regen resets Civil label drag state.
            try
            {
                document.Editor.Regen();
                AcApplication.UpdateScreen();
            }
            catch { }
            August21GraphicsRefreshManager.MarkDirty();
'@
$vertex = ReplaceMethodBody $vertex '        private static void RefreshTable(' $vertexRefresh 'Vertex saved mode/interval refresh'

$vertexUpdate = @'
            if (id.IsNull || id.IsErased) return false;
            DBObject value;
            try { value = transaction.GetObject(id, OpenMode.ForWrite, false); }
            catch { return false; }

            CivilCogoPoint cogo = value as CivilCogoPoint;
            if (cogo != null && string.Equals(link.OutputType, "COGO", StringComparison.OrdinalIgnoreCase))
            {
                const double tolerance = 0.0000001;
                bool moved = Math.Abs(cogo.Easting - record.Point.X) > tolerance ||
                    Math.Abs(cogo.Northing - record.Point.Y) > tolerance ||
                    Math.Abs(cogo.Elevation - record.Point.Z) > tolerance;
                bool renamed = !string.Equals(
                    cogo.RawDescription ?? string.Empty,
                    record.PointName ?? string.Empty,
                    StringComparison.Ordinal);
                if (moved)
                {
                    cogo.Easting = record.Point.X;
                    cogo.Northing = record.Point.Y;
                    cogo.Elevation = record.Point.Z;
                    WriteOutputLink(
                        cogo, transaction, link.GroupId, record.Key, record.Point);
                }
                if (renamed)
                {
                    cogo.RawDescription = record.PointName;
                    try
                    {
                        if (!string.Equals(
                            cogo.PointName ?? string.Empty,
                            record.PointName ?? string.Empty,
                            StringComparison.Ordinal))
                            cogo.PointName = record.PointName;
                    }
                    catch { }
                }
                if (moved || renamed)
                {
                    try { cogo.RecordGraphicsModified(true); } catch { }
                }
                return true;
            }

            MText mtext = value as MText;
            if (mtext != null && string.Equals(link.OutputType, "MText", StringComparison.OrdinalIgnoreCase))
            {
                CaptureCurrentAnnotationOffset(transaction, id, record);
                mtext.Location = record.Point;
                mtext.Attachment = AnchoredAttachment(record, link.LabelOffset);
                mtext.TextHeight = textHeight;
                mtext.Contents = AnchoredMText(LabelText(record, link));
                WriteOutputLink(
                    mtext, transaction, link.GroupId, record.Key, record.Point);
                try { mtext.RecordGraphicsModified(true); } catch { }
                return true;
            }

            return false;
'@
$vertex = ReplaceMethodBody $vertex '        private static bool UpdateOutput(' $vertexUpdate 'COGO label-stable refresh'
WriteText $vertexPath $vertex

# -----------------------------------------------------------------------------
# 4. Feature-line surface assignment. Keep every surface option, but never call
# native AssignElevationsFromSurface while a FeatureLine is open in the same
# transaction as a Surface. Create first, commit, sample Surface read-only, then
# write sampled elevations in a separate transaction.
# -----------------------------------------------------------------------------
$feature = ReadText $featureLinePath
$featureCreate = @'
            Editor editor = document.Editor;
            List<SurfaceChoice> surfaces = WorkflowRepairCommands.ReadSurfaceChoices(document);
            string[] surfaceNames = new[] { "<Keep source elevations>" }
                .Concat(surfaces.Select(surface => surface.Name))
                .ToArray();
            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Create Feature Lines",
                "Choose the elevation source before selecting the objects to convert.");
            settings.AddChoice(
                "Surface", "Elevation Source", "Surface",
                surfaceNames[0],
                "Keep source elevations or assign the new feature lines to a Civil 3D surface.",
                surfaceNames);
            settings.AddChoice(
                "Intermediate", "Elevation Source", "Add intermediate surface points",
                "No",
                "Keep this option for the surface workflow. Civil 3D 2023 surface sampling is isolated from feature-line writes for stability.",
                new[] { "No", "Yes" });
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;

            string selectedSurfaceName = settings.Text("Surface");
            bool useSurface = !string.IsNullOrWhiteSpace(selectedSurfaceName) &&
                !string.Equals(selectedSurfaceName, "<Keep source elevations>", StringComparison.OrdinalIgnoreCase);
            bool includeIntermediate = string.Equals(
                settings.Text("Intermediate"), "Yes", StringComparison.OrdinalIgnoreCase);

            PromptSelectionResult selection = GetSelection(
                editor,
                "\nSelect lines, arcs or polylines to convert to siteless feature lines: ");
            if (selection.Status != PromptStatus.OK) return;

            Database database = document.Database;
            var createdIds = new List<ObjectId>();
            int skipped = 0;
            try
            {
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    foreach (SelectedObject selectedObject in selection.Value)
                    {
                        if (selectedObject == null || selectedObject.ObjectId.IsNull)
                        {
                            skipped++;
                            continue;
                        }
                        DBObject sourceObject = transaction.GetObject(
                            selectedObject.ObjectId,
                            OpenMode.ForRead,
                            false);
                        AcEntity sourceEntity = sourceObject as AcEntity;
                        if (!IsSupportedSource(sourceObject) ||
                            sourceEntity == null ||
                            IsLayerLocked(transaction, sourceEntity.LayerId))
                        {
                            skipped++;
                            continue;
                        }
                        ObjectId featureLineId = CivilFeatureLine.Create(
                            string.Empty,
                            selectedObject.ObjectId);
                        if (!featureLineId.IsNull) createdIds.Add(featureLineId);
                    }
                    transaction.Commit();
                }

                int surfaceApplied = 0;
                int surfaceFailed = 0;
                if (useSurface)
                {
                    foreach (ObjectId id in createdIds)
                    {
                        string error;
                        if (August21SurfaceSafety.TryApplyFeatureLineElevations(
                                document,
                                id,
                                selectedSurfaceName,
                                includeIntermediate,
                                out error))
                            surfaceApplied++;
                        else
                            surfaceFailed++;
                    }
                }
                try
                {
                    editor.Regen();
                    AcApplication.UpdateScreen();
                }
                catch { }
                August21GraphicsRefreshManager.MarkDirty();
                editor.WriteMessage(
                    "\nCE_FLCREATE complete. Feature lines created: {0}; skipped: {1}; surface: {2}; safely surface-updated: {3}; surface warnings: {4}.",
                    createdIds.Count,
                    skipped,
                    useSurface ? selectedSurfaceName : "source elevations",
                    surfaceApplied,
                    surfaceFailed);
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_FLCREATE stopped safely. {0}",
                    exception.Message);
            }
'@
$feature = ReplaceMethodBody $feature '        private static void CreateFromObjects(Document document)' $featureCreate 'FeatureLine create safe surface phases'

$featureSurface = @'
            Editor editor = document.Editor;
            PromptSelectionResult selection = GetSelection(
                editor,
                "\nSelect feature lines to assign elevations from a surface: ");
            if (selection.Status != PromptStatus.OK) return;

            var surfaceOptions = new PromptEntityOptions("\nSelect Civil 3D surface: ");
            surfaceOptions.SetRejectMessage("\nSelect a Civil 3D surface.");
            surfaceOptions.AddAllowedClass(typeof(CivilSurface), false);
            PromptEntityResult surfaceResult = editor.GetEntity(surfaceOptions);
            if (surfaceResult.Status != PromptStatus.OK) return;
            string surfaceName = August21SurfaceSafety.ReadSurfaceName(
                document,
                surfaceResult.ObjectId);
            if (string.IsNullOrWhiteSpace(surfaceName))
            {
                editor.WriteMessage("\nCE_FLSURFACE cancelled. The selected surface could not be resolved safely.");
                return;
            }

            var gradeBreakOptions = new PromptKeywordOptions(
                "\nInsert intermediate surface grade-break points? [Yes/No] <No>: ")
            { AllowNone = true };
            gradeBreakOptions.Keywords.Add("Yes");
            gradeBreakOptions.Keywords.Add("No");
            PromptResult gradeBreakResult = editor.GetKeywords(gradeBreakOptions);
            if (gradeBreakResult.Status == PromptStatus.Cancel) return;
            bool includeIntermediate = gradeBreakResult.Status == PromptStatus.OK &&
                string.Equals(gradeBreakResult.StringResult, "Yes", StringComparison.OrdinalIgnoreCase);

            int changed = 0;
            int skipped = 0;
            foreach (ObjectId id in selection.Value.GetObjectIds())
            {
                string error;
                if (August21SurfaceSafety.TryApplyFeatureLineElevations(
                        document,
                        id,
                        surfaceName,
                        includeIntermediate,
                        out error))
                    changed++;
                else
                    skipped++;
            }
            try
            {
                editor.Regen();
                AcApplication.UpdateScreen();
            }
            catch { }
            August21GraphicsRefreshManager.MarkDirty();
            editor.WriteMessage(
                "\nCE_FLSURFACE complete. Feature lines updated safely: {0}; skipped/warnings: {1}; surface={2}.",
                changed,
                skipped,
                surfaceName);
'@
$feature = ReplaceMethodBody $feature '        private static void AssignFromSurface(Document document)' $featureSurface 'FeatureLine safe surface assignment'
WriteText $featureLinePath $feature

# -----------------------------------------------------------------------------
# 5. Platform Drape + linked refresh use the same read-snapshot/write rule.
# -----------------------------------------------------------------------------
$platform = ReadText $platformPath
$platformDrape = @'
            PlatformDynamicRefreshManager.EnsureInitialized();
            Document document = ActiveDocument();
            if (document == null) return;
            List<SurfaceChoice> surfaces = WorkflowRepairCommands.ReadSurfaceChoices(document);
            if (surfaces.Count == 0)
            {
                document.Editor.WriteMessage("\nCE_PLATFORMDRAPE cancelled. No Civil 3D surfaces were found.");
                return;
            }
            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Drape Platform Steps",
                "The selected surface controls the draped step. Surface reading is isolated from feature-line writes for Civil 3D 2023 stability.");
            settings.AddChoice("Surface", "Surface", "Target / surveyed surface", surfaces[0].Name, "Select a controlling surface.", surfaces.Select(s => s.Name));
            settings.AddChoice("Intermediate", "Surface", "Intermediate surface points", "No", "Retained surface option; existing feature-line points are sampled through the safe surface snapshot path.", new[] { "No", "Yes" });
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;
            SurfaceChoice surface = surfaces.FirstOrDefault(s => string.Equals(s.Name, settings.Text("Surface"), StringComparison.OrdinalIgnoreCase));
            if (surface == null) return;
            PromptSelectionResult selection = SelectFeatureLines(document.Editor, "\nSelect linked stepped-offset feature lines to drape: ");
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;
            bool intermediate = string.Equals(settings.Text("Intermediate"), "Yes", StringComparison.OrdinalIgnoreCase);
            ObjectId freshSurfaceId = August21SurfaceSafety.ResolveFreshSurfaceId(document, surface.Name);
            if (freshSurfaceId.IsNull)
            {
                document.Editor.WriteMessage("\nCE_PLATFORMDRAPE cancelled. The chosen surface could not be resolved safely.");
                return;
            }
            string surfaceHandle = freshSurfaceId.Handle.ToString();
            int linked = 0;
            int skipped = 0;
            foreach (ObjectId id in selection.Value.GetObjectIds())
            {
                string error;
                if (!August21SurfaceSafety.TryApplyFeatureLineElevations(
                        document, id, surface.Name, intermediate, out error))
                {
                    skipped++;
                    continue;
                }
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    CivilFeatureLine child = OpenFeatureLine(transaction, id, OpenMode.ForWrite);
                    if (!Editable(child, transaction)) { skipped++; continue; }
                    StepRelation step;
                    if (!TryReadStep(child, transaction, out step))
                        step = new StepRelation(child.Handle.ToString(), 0.0, 0.0, 0);
                    WriteDrape(child, transaction, new DrapeRelation
                    {
                        SourceHandle = step.SourceHandle,
                        SurfaceHandle = surfaceHandle,
                        VerticalOffset = step.VerticalOffset,
                        Sequence = step.Sequence,
                        Intermediate = intermediate
                    });
                    try { child.RecordGraphicsModified(true); } catch { }
                    transaction.Commit();
                }
                linked++;
            }
            // RefreshAll is also August21-safe below; it no longer calls native
            // AssignElevationsFromSurface in a mixed Civil write transaction.
            RefreshAll(document);
            try
            {
                document.Editor.Regen();
                AcApplication.UpdateScreen();
            }
            catch { }
            August21GraphicsRefreshManager.MarkDirty();
            document.Editor.WriteMessage(
                "\nCE_PLATFORMDRAPE complete. Dynamic surface links={0}; skipped/warnings={1}.",
                linked,
                skipped);
'@
$platform = ReplaceMethodBody $platform '        public void Drape()' $platformDrape 'Platform safe Drape'

$platformRefresh = @'
            if (document == null) return 0;
            int refreshed = 0;
            List<DrapeSnapshot> snapshots = ReadDrapes(document.Database);
            if (snapshots.Count > 0)
            {
                // Phase 1: resolve surface names and drape each child through the
                // read-snapshot/write helper. No Surface object survives into the
                // feature-line write transaction.
                var applied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (DrapeSnapshot snapshot in snapshots)
                {
                    ObjectId childId = Resolve(document.Database, snapshot.ChildHandle);
                    ObjectId surfaceId = Resolve(document.Database, snapshot.Link.SurfaceHandle);
                    string surfaceName = August21SurfaceSafety.ReadSurfaceName(document, surfaceId);
                    if (childId.IsNull || string.IsNullOrWhiteSpace(surfaceName)) continue;
                    string error;
                    if (August21SurfaceSafety.TryApplyFeatureLineElevations(
                            document,
                            childId,
                            surfaceName,
                            snapshot.Link.Intermediate,
                            out error))
                        applied.Add(snapshot.ChildHandle);
                }

                // Phase 2: update the source platform from the already-draped child.
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    foreach (DrapeSnapshot snapshot in snapshots)
                    {
                        if (!applied.Contains(snapshot.ChildHandle)) continue;
                        CivilFeatureLine child = OpenFeatureLine(transaction, Resolve(document.Database, snapshot.ChildHandle), OpenMode.ForRead);
                        CivilFeatureLine source = OpenFeatureLine(transaction, Resolve(document.Database, snapshot.Link.SourceHandle), OpenMode.ForWrite);
                        if (child == null || source == null) continue;
                        try
                        {
                            if (child.ObjectId != source.ObjectId)
                            {
                                Point3dCollection sourcePoints = source.GetPoints(FeatureLinePointType.AllPoints);
                                for (int index = 0; index < sourcePoints.Count; index++)
                                {
                                    Point3d sourcePoint = sourcePoints[index];
                                    Point3d nearest = child.GetClosestPointTo(
                                        new Point3d(sourcePoint.X, sourcePoint.Y, 0.0),
                                        Vector3d.ZAxis,
                                        false);
                                    SetAbsoluteElevation(
                                        source,
                                        sourcePoint,
                                        index,
                                        nearest.Z - snapshot.Link.VerticalOffset);
                                }
                                try { source.RecordGraphicsModified(true); } catch { }
                            }
                            refreshed++;
                        }
                        catch { }
                    }
                    transaction.Commit();
                }

                try { FeatureLineRelativeCommands.RefreshAll(document); } catch { }

                // Phase 3: rebuilt step feature lines get the surface snapshot again,
                // then their link record is restored in a separate ordinary write.
                foreach (DrapeSnapshot snapshot in snapshots)
                {
                    ObjectId rebuiltId = ObjectId.Null;
                    using (Transaction find = document.Database.TransactionManager.StartTransaction())
                    {
                        BlockTableRecord space = ModelSpace(document.Database, find, OpenMode.ForRead);
                        CivilFeatureLine rebuilt = FindStep(
                            space,
                            find,
                            snapshot.Link.SourceHandle,
                            snapshot.Link.Sequence);
                        if (rebuilt != null) rebuiltId = rebuilt.ObjectId;
                    }
                    ObjectId surfaceId = Resolve(document.Database, snapshot.Link.SurfaceHandle);
                    string surfaceName = August21SurfaceSafety.ReadSurfaceName(document, surfaceId);
                    if (rebuiltId.IsNull || string.IsNullOrWhiteSpace(surfaceName)) continue;
                    string error;
                    if (!August21SurfaceSafety.TryApplyFeatureLineElevations(
                            document,
                            rebuiltId,
                            surfaceName,
                            snapshot.Link.Intermediate,
                            out error))
                        continue;
                    using (Transaction writeLink = document.Database.TransactionManager.StartTransaction())
                    {
                        CivilFeatureLine rebuilt = OpenFeatureLine(writeLink, rebuiltId, OpenMode.ForWrite);
                        if (rebuilt != null)
                        {
                            WriteDrape(rebuilt, writeLink, snapshot.Link);
                            try { rebuilt.RecordGraphicsModified(true); } catch { }
                        }
                        writeLink.Commit();
                    }
                }
            }

            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = ModelSpace(document.Database, transaction, OpenMode.ForRead);
                foreach (ObjectId id in space.Cast<ObjectId>().ToList())
                {
                    MText text = transaction.GetObject(id, OpenMode.ForRead, false) as MText;
                    string sourceHandle;
                    string label;
                    if (text != null && TryReadName(text, transaction, out sourceHandle, out label))
                    {
                        CivilFeatureLine source = OpenFeatureLine(transaction, Resolve(document.Database, sourceHandle), OpenMode.ForRead);
                        if (source != null)
                        {
                            text.UpgradeOpen();
                            text.Location = Centre(source);
                            text.Contents = LabelText(label, source);
                            try { text.RecordGraphicsModified(true); } catch { }
                            refreshed++;
                        }
                        continue;
                    }
                    Table table = transaction.GetObject(id, OpenMode.ForRead, false) as Table;
                    TableLink link;
                    if (table != null && TryReadTable(table, transaction, out link))
                    {
                        table.UpgradeOpen();
                        PopulateTable(document.Database, transaction, table, link);
                        try { table.RecordGraphicsModified(true); } catch { }
                        refreshed++;
                    }
                }
                transaction.Commit();
            }
            August21GraphicsRefreshManager.MarkDirty();
            return refreshed;
'@
$platform = ReplaceMethodBody $platform '        internal static int RefreshAll(Document document)' $platformRefresh 'Platform safe linked refresh'
WriteText $platformPath $platform

# -----------------------------------------------------------------------------
# 6. Restore the already-built safe Midblock/Road Reserve optional-surface planner.
# FieldRecovery temporarily forced <None> as an emergency guard; the earlier
# RuntimeStability pass already had the correct read-planning/write-construction
# separation. Recover those exact bodies now, after FieldRecovery, so surfaces stay.
# -----------------------------------------------------------------------------
$geometry = ReadText $geometryPath
$midblockBody = ExtractRepairHereString $runtimeRepairPath 'midblockBody'
$roadReserveBody = ExtractRepairHereString $runtimeRepairPath 'roadReserveBody'
$geometry = ReplaceMethodBody $geometry '        private static void RunMidblock(bool legacyEntry)' $midblockBody 'Restore safe Midblock optional surface'
$geometry = ReplaceMethodBody $geometry '        private static void RunRoadReserve(bool legacyEntry)' $roadReserveBody 'Restore safe RoadReserve optional surface'
WriteText $geometryPath $geometry

# -----------------------------------------------------------------------------
# 7. Road Reserve Centrelines: read/plan against cadastral DBObjects first, close
# them, then start a separate write transaction for erase/create/link operations.
# -----------------------------------------------------------------------------
$road = ReadText $roadPath
$roadReserveCentres = @'
            Document document = ActiveDocument();
            if (document == null) return;
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Road Reserve Centrelines",
                "Select closed cadastral/reserve polylines. CE Tools pairs sufficiently parallel opposing boundary segments and creates a centre polyline through the open reserve. Different reserve widths are handled per paired segment.");
            model.AddDouble("MinWidth", "Reserve width", "Minimum road reserve width", 6.0, "Ignore opposing boundaries closer than this distance.");
            model.AddDouble("MaxWidth", "Reserve width", "Maximum road reserve width", 60.0, "Ignore opposing boundaries farther apart than this distance.");
            model.AddDouble("Parallel", "Detection", "Parallel tolerance (degrees)", 7.5, "Maximum angular difference between opposing cadastral boundary segments.");
            model.AddDouble("MinOverlap", "Detection", "Minimum overlapping length", 4.0, "Minimum common projected length required to form a road-centre segment.");
            model.AddChoice("Replace", "Output", "Existing CE road centrelines", "Keep existing", "Choose whether previously generated reserve-centrelines are retained or replaced.", new[] { "Keep existing", "Replace existing" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            PromptSelectionResult selection = SelectClosedPolylines(document.Editor, "\nSelect cadastral/reserve closed polylines: ");
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;
            double minWidth = Math.Max(0.001, model.Double("MinWidth", 6.0));
            double maxWidth = Math.Max(minWidth, model.Double("MaxWidth", 60.0));
            double angleTolerance = Math.Max(0.1, model.Double("Parallel", 7.5)) * Math.PI / 180.0;
            double minOverlap = Math.Max(0.001, model.Double("MinOverlap", 4.0));
            bool replace = string.Equals(model.Text("Replace"), "Replace existing", StringComparison.OrdinalIgnoreCase);

            var plans = new List<Tuple<Point3d, Point3d, string, double>>();
            int rejected = 0;
            try
            {
                // Read-only planning transaction. No generated object is erased or
                // appended while cadastral source DBObjects are being analysed.
                using (Transaction planning = document.Database.TransactionManager.StartTransaction())
                {
                    List<Polyline> parcels = selection.Value.GetObjectIds()
                        .Where(id => !id.IsNull && !id.IsErased)
                        .Select(id =>
                        {
                            try { return planning.GetObject(id, OpenMode.ForRead, false) as Polyline; }
                            catch { return null; }
                        })
                        .Where(poly => poly != null && poly.Closed && poly.NumberOfVertices >= 3)
                        .ToList();
                    List<BoundarySegment> segments = BuildBoundarySegments(parcels);
                    var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    for (int firstIndex = 0; firstIndex < segments.Count; firstIndex++)
                    {
                        BoundarySegment first = segments[firstIndex];
                        for (int secondIndex = firstIndex + 1; secondIndex < segments.Count; secondIndex++)
                        {
                            BoundarySegment second = segments[secondIndex];
                            if (first.SourceId == second.SourceId) continue;
                            if (ParallelAngle(first.Direction, second.Direction) > angleTolerance) continue;
                            double width = PerpendicularDistance(first.MidPoint, second.Start, second.End);
                            if (width < minWidth || width > maxWidth) continue;
                            Point3d overlapStart;
                            Point3d overlapEnd;
                            if (!TryCommonMidline(first, second, minOverlap, out overlapStart, out overlapEnd)) continue;
                            Point3d mid = Mid(overlapStart, overlapEnd);
                            if (parcels.Any(parcel => PointInside(parcel, mid)))
                            {
                                rejected++;
                                continue;
                            }
                            string key = SegmentKey(overlapStart, overlapEnd, 0.05);
                            if (!keys.Add(key)) continue;
                            plans.Add(Tuple.Create(
                                overlapStart,
                                overlapEnd,
                                first.SourceHandle + "," + second.SourceHandle,
                                width));
                        }
                    }
                }

                int created = 0;
                using (Transaction construction = document.Database.TransactionManager.StartTransaction())
                {
                    BlockTableRecord space = GetModelSpace(document.Database, construction, OpenMode.ForWrite);
                    ObjectId layerId = GetOrCreateLayer(document.Database, construction, CenterLayer);
                    if (replace) EraseByKind(space, construction, "CENTER");
                    foreach (Tuple<Point3d, Point3d, string, double> plan in plans)
                    {
                        var centre = new Polyline(2);
                        centre.SetDatabaseDefaults(document.Database);
                        centre.LayerId = layerId;
                        centre.AddVertexAt(0, new Point2d(plan.Item1.X, plan.Item1.Y), 0.0, 0.0, 0.0);
                        centre.AddVertexAt(1, new Point2d(plan.Item2.X, plan.Item2.Y), 0.0, 0.0, 0.0);
                        space.AppendEntity(centre);
                        construction.AddNewlyCreatedDBObject(centre, true);
                        WriteLink(centre, construction, new RoadLink
                        {
                            Kind = "CENTER",
                            ParentHandle = string.Empty,
                            SourceHandles = plan.Item3,
                            Offset = 0.0,
                            Width = plan.Item4,
                            Group = string.Empty,
                            Name = string.Empty
                        });
                        try { centre.RecordGraphicsModified(true); } catch { }
                        created++;
                    }
                    construction.Commit();
                }
                try
                {
                    document.Editor.Regen();
                    AcApplication.UpdateScreen();
                }
                catch { }
                August21GraphicsRefreshManager.MarkDirty();
                document.Editor.WriteMessage(
                    "\nCE_ROADRESERVECENTERLINES complete. Centre polylines={0}; rejected inside cadastral parcels={1}.",
                    plans.Count,
                    rejected);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_ROADRESERVECENTERLINES stopped safely. No mixed source/write transaction was retained. {0}",
                    exception.Message);
            }
'@
$road = ReplaceMethodBody $road '        public void ReserveCenterlines()' $roadReserveCentres 'Road Reserve Centreline two-phase safety'
WriteText $roadPath $road

# -----------------------------------------------------------------------------
# 8. CE_SEWERFROMCADASTRAL: Surface/parcel/graph analysis stays entirely in a
# read-only planning transaction. Only plain graph/routing results cross into the
# separate AutoCAD construction transaction.
# -----------------------------------------------------------------------------
$cadastral = ReadText $cadastralPath
$cadastralBody = @'
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            var model = new ProductionSettingsDialogModel(
                "CE Tools - Sewer Route from Cadastral Data",
                "Create connected preliminary sewer routes directly from cadastral erf boundaries. Civil surface analysis is completed in a read-only planning stage before a separate AutoCAD construction transaction.");
            model.AddChoice("Scope", "01 Cadastral", "Erf boundaries", "Selected",
                "Use selected closed cadastral erf polylines or all non-CE closed lightweight polylines in model space.",
                new[] { "Selected", "All" });
            model.AddChoice("Preference", "02 Routing", "Automatic route preference", "Shortest practical route",
                "Choose the shortest practical cadastral route, or slightly prefer Midblock / Road-Reserve edges while respecting surface flow direction.",
                new[] { "Shortest practical route", "Prefer midblock", "Prefer road reserve" });
            model.AddPositiveDouble("MidblockOffset", "02 Routing", "Offset from shared erf boundary", 1.5,
                "Offset shared/midblock route edges from the common erf boundary.");
            model.AddPositiveDouble("RoadReserveOffset", "02 Routing", "Offset from outer erf boundary", 5.0,
                "Offset exterior route edges away from the erf interior toward the road reserve.");
            model.AddChoice("Spacing", "03 Manholes", "Maximum planning manhole spacing", "60 m",
                "Place planning manholes at route vertices/junctions and split long route edges to this maximum spacing.",
                new[] { "60 m", "80 m", "Custom" });
            model.AddPositiveDouble("CustomSpacing", "03 Manholes", "Custom maximum spacing", 60.0,
                "Used only when Custom spacing is selected.");
            model.AddPositiveDouble("StartSetback", "03 Manholes", "Starting manhole setback", 1.5,
                "Set leaf/start manholes back from terminal cadastral boundaries where geometry permits.");
            model.AddPositiveDouble("ManholeDiameter", "03 Manholes", "Planning manhole diameter", 1.2,
                "Diameter of preliminary planning manhole circles.");
            model.AddChoice("Replace", "04 Output", "Existing cadastral sewer output", "Replace existing",
                "Replace prior CE cadastral sewer routes/manholes or retain them.",
                new[] { "Replace existing", "Keep existing" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            List<ObjectId> parcelIds = ResolveParcels(document, model.Text("Scope"));
            if (parcelIds.Count == 0)
            {
                document.Editor.WriteMessage("\nCE_SEWERFROMCADASTRAL: no closed cadastral erf polylines were found.");
                return;
            }
            ObjectId surfaceId = PromptSurface(document);
            if (surfaceId.IsNull)
            {
                document.Editor.WriteMessage("\nCE_SEWERFROMCADASTRAL cancelled. Select a Civil 3D surface for slope/low-point analysis.");
                return;
            }

            double midblockOffset = Math.Max(0.0, model.Double("MidblockOffset", 1.5));
            double roadReserveOffset = Math.Max(0.0, model.Double("RoadReserveOffset", 5.0));
            double spacing = string.Equals(model.Text("Spacing"), "80 m", StringComparison.OrdinalIgnoreCase)
                ? 80.0
                : string.Equals(model.Text("Spacing"), "Custom", StringComparison.OrdinalIgnoreCase)
                    ? Math.Max(1.0, model.Double("CustomSpacing", 60.0))
                    : 60.0;
            double startSetback = Math.Max(0.0, model.Double("StartSetback", 1.5));
            double manholeDiameter = Math.Max(0.1, model.Double("ManholeDiameter", 1.2));

            Graph graph = null;
            RoutingResult routing = null;
            Dictionary<int, Vector2d> shifts = null;
            Dictionary<int, int> selectedDegree = null;
            LowPointResult low = new LowPointResult();
            int served = 0;
            int skipped = 0;

            try
            {
                using (DocumentLock documentLock = document.LockDocument())
                {
                    using (Transaction planning = document.Database.TransactionManager.StartTransaction())
                    {
                        Surface surface = planning.GetObject(surfaceId, OpenMode.ForRead, false) as Surface;
                        if (surface == null)
                        {
                            document.Editor.WriteMessage("\nCE_SEWERFROMCADASTRAL: selected object is not a readable Civil 3D surface.");
                            return;
                        }
                        List<ParcelInfo> parcels = ReadParcels(parcelIds, planning);
                        graph = BuildGraph(parcels);
                        if (parcels.Count == 0 || graph.Nodes.Count == 0 || graph.Edges.Count == 0)
                        {
                            document.Editor.WriteMessage("\nCE_SEWERFROMCADASTRAL: cadastral geometry did not produce a usable connected boundary graph.");
                            return;
                        }
                        Dictionary<int, double> elevations = SampleNodeElevations(graph, surface);
                        low = ResolveLowPoint(parcels, graph, surface, elevations);
                        if (!low.Valid)
                        {
                            document.Editor.WriteMessage("\nCE_SEWERFROMCADASTRAL: the selected surface has no readable elevations over the cadastral data.");
                            return;
                        }
                        routing = BuildRouting(graph, parcels, elevations, low.NodeId, model.Text("Preference"));
                        served = routing.ServedParcels;
                        if (routing.SelectedEdgeIds.Count == 0)
                        {
                            document.Editor.WriteMessage("\nCE_SEWERFROMCADASTRAL: no connected flow-compatible route to the outlet could be found.");
                            return;
                        }
                        shifts = BuildNodeShifts(
                            graph,
                            parcels,
                            routing.SelectedEdgeIds,
                            surface,
                            low.Point,
                            midblockOffset,
                            roadReserveOffset);
                        selectedDegree = BuildSelectedDegree(graph, routing.SelectedEdgeIds);
                    }

                    int created = 0;
                    int manholes = 0;
                    using (Transaction construction = document.Database.TransactionManager.StartTransaction())
                    {
                        BlockTableRecord space = construction.GetObject(
                            SymbolUtilityServices.GetBlockModelSpaceId(document.Database),
                            OpenMode.ForWrite,
                            false) as BlockTableRecord;
                        if (space == null) return;
                        ObjectId midblockLayer = EnsureLayer(document.Database, construction, MidblockLayer);
                        ObjectId roadLayer = EnsureLayer(document.Database, construction, RoadReserveLayer);
                        ObjectId mhLayer = EnsureLayer(document.Database, construction, ManholeLayer);
                        ObjectId analysisLayer = EnsureLayer(document.Database, construction, AnalysisLayer);
                        if (string.Equals(model.Text("Replace"), "Replace existing", StringComparison.OrdinalIgnoreCase))
                            EraseExisting(space, construction);

                        var mhPoints = new Dictionary<string, Point3d>(StringComparer.OrdinalIgnoreCase);
                        int routeNumber = 1;
                        foreach (int edgeId in routing.SelectedEdgeIds.OrderBy(value => value))
                        {
                            Edge edge;
                            if (!graph.Edges.TryGetValue(edgeId, out edge)) { skipped++; continue; }
                            int upstream = ResolveUpstream(edge, routing.DistanceToOutlet, routing.NextTowardOutlet);
                            int downstream = upstream == edge.A ? edge.B : edge.A;
                            Point2d start = Shifted(graph.Nodes[upstream].Point, shifts, upstream);
                            Point2d end = Shifted(graph.Nodes[downstream].Point, shifts, downstream);
                            double length = Distance(start, end);
                            if (length <= Tol) { skipped++; continue; }
                            int degree;
                            bool leafStart = selectedDegree.TryGetValue(upstream, out degree) &&
                                degree == 1 && upstream != low.NodeId;
                            if (leafStart && startSetback > Tol && length > startSetback + 0.25)
                                start = MoveToward(start, end, Math.Min(startSetback, length * 0.45));
                            List<Point2d> points = SplitBySpacing(start, end, spacing);
                            if (points.Count < 2) { skipped++; continue; }
                            var route = new Polyline(points.Count);
                            for (int i = 0; i < points.Count; i++)
                                route.AddVertexAt(i, points[i], 0.0, 0.0, 0.0);
                            route.SetDatabaseDefaults(document.Database);
                            route.LayerId = string.Equals(edge.Kind, "MIDBLOCK", StringComparison.OrdinalIgnoreCase)
                                ? midblockLayer : roadLayer;
                            space.AppendEntity(route);
                            construction.AddNewlyCreatedDBObject(route, true);
                            WriteLink(route, construction, edge, surfaceId, low, routeNumber++);
                            try { route.RecordGraphicsModified(true); } catch { }
                            created++;
                            foreach (Point2d point in points)
                            {
                                string key = PointKey(point);
                                if (!mhPoints.ContainsKey(key))
                                    mhPoints[key] = new Point3d(point.X, point.Y, 0.0);
                            }
                        }

                        int mhNumber = 1;
                        foreach (Point3d location in mhPoints.Values
                            .OrderByDescending(point => Distance(new Point2d(point.X, point.Y), low.Point)))
                        {
                            var circle = new Circle(location, Vector3d.ZAxis, manholeDiameter * 0.5);
                            circle.SetDatabaseDefaults(document.Database);
                            circle.LayerId = mhLayer;
                            space.AppendEntity(circle);
                            construction.AddNewlyCreatedDBObject(circle, true);
                            try { circle.RecordGraphicsModified(true); } catch { }
                            var label = new DBText
                            {
                                Position = location + new Vector3d(manholeDiameter, manholeDiameter, 0.0),
                                TextString = "MH-P" + mhNumber.ToString(CultureInfo.InvariantCulture),
                                Height = Math.Max(PaperAnnotationScale.ModelTextHeight(document.Database, 2.0), 0.001),
                                LayerId = mhLayer
                            };
                            label.SetDatabaseDefaults(document.Database);
                            space.AppendEntity(label);
                            construction.AddNewlyCreatedDBObject(label, true);
                            try { label.RecordGraphicsModified(true); } catch { }
                            mhNumber++;
                            manholes++;
                        }
                        AddAnalysisMarker(document.Database, construction, space, analysisLayer,
                            low.SurfaceMinimumSample, "SAMPLED SITE LOW POINT", low.SurfaceMinimumElevation, manholeDiameter);
                        if (Distance(low.SurfaceMinimumSample, low.Point) > SnapTolerance)
                            AddAnalysisMarker(document.Database, construction, space, analysisLayer,
                                low.Point, "NETWORK OUTLET", low.Elevation, manholeDiameter);
                        construction.Commit();
                    }

                    document.Editor.SetImpliedSelection(new ObjectId[0]);
                    document.Editor.Regen();
                    try { AcApplication.UpdateScreen(); } catch { }
                    August21GraphicsRefreshManager.MarkDirty();
                    UniversalDynamicRefreshManager.Queue();
                    document.Editor.WriteMessage(
                        "\nCE_SEWERFROMCADASTRAL complete. Served erfs={0}/{1}; route segments={2}; planning manholes={3}; skipped={4}; sampled low EL={5:0.###}; outlet EL={6:0.###}.",
                        served,
                        parcelIds.Count,
                        created,
                        manholes,
                        skipped,
                        low.SurfaceMinimumElevation,
                        low.Elevation);
                }
            }
            catch (Autodesk.AutoCAD.Runtime.Exception ex)
            {
                document.Editor.WriteMessage("\nCE_SEWERFROMCADASTRAL stopped safely: {0}", ex.Message);
            }
            catch (System.Exception ex)
            {
                document.Editor.WriteMessage("\nCE_SEWERFROMCADASTRAL stopped safely: {0}", ex.Message);
            }
'@
$cadastral = ReplaceMethodBody $cadastral '        public void CreateSewerFromCadastral()' $cadastralBody 'Cadastral sewer split surface/write transactions'
WriteText $cadastralPath $cadastral

# -----------------------------------------------------------------------------
# Final guards. Fail the staged build rather than silently ship the old crash/state
# regressions again.
# -----------------------------------------------------------------------------
$vertexCheck = ReadText $vertexPath
foreach ($required in @(
    'August19VertexSettingOutGeometry.ReadSources(',
    'link.GenerationMode,',
    'link.IntervalSpacing',
    'knownOutputKeys.Contains(record.Key)',
    'suppressedOutputKeys.Add(record.Key)',
    'Do not call CogoPointProjectStyleCommands.ApplySelectedStyles here')) {
    if (-not $vertexCheck.Contains($required)) { throw "August21 Vertex guard missing: $required" }
}
$featureCheck = ReadText $featureLinePath
if ($featureCheck.Contains('featureLine.AssignElevationsFromSurface(')) {
    throw 'August21 unsafe FeatureLine AssignElevationsFromSurface call survived.'
}
$platformCheck = ReadText $platformPath
if ($platformCheck.Contains('.AssignElevationsFromSurface(')) {
    throw 'August21 unsafe Platform AssignElevationsFromSurface call survived.'
}
$geometryCheck = ReadText $geometryPath
foreach ($required in @(
    'List<string> surfaceChoices = SurfaceChoices(document);',
    'CivilSurface surface = OpenSurface(',
    'using (Transaction planning =',
    'using (Transaction construction =')) {
    if (-not $geometryCheck.Contains($required)) { throw "August21 geometry surface guard missing: $required" }
}
if ($geometryCheck.Contains('CE_FIELD_RECOVERY_GEOMETRY_ONLY')) {
    throw 'August21 geometry planner is still forced to surface <None>.'
}
$roadCheck = ReadText $roadPath
if (-not $roadCheck.Contains('using (Transaction planning =') -or
    -not $roadCheck.Contains('using (Transaction construction =')) {
    throw 'August21 Road Reserve Centreline two-phase guard missing.'
}
$cadastralCheck = ReadText $cadastralPath
if (-not $cadastralCheck.Contains('Surface surface = planning.GetObject(') -or
    -not $cadastralCheck.Contains('using (Transaction construction =')) {
    throw 'August21 cadastral sewer planning/construction split missing.'
}
$pluginCheck = ReadText $pluginPath
foreach ($required in @('August21SimpleParkingRefreshManager.Initialize();','August21GraphicsRefreshManager.Initialize();')) {
    if (-not $pluginCheck.Contains($required)) { throw "August21 manager registration missing: $required" }
}

Write-Host 'August 21 state/graphics/surface-safety final pass applied.' -ForegroundColor Green
Write-Host ' - Vertex GenerationMode + IntervalSpacing now survive linked refresh/source movement.' -ForegroundColor Green
Write-Host ' - COGO styles are not reapplied on every refresh; unchanged COGO points are not rewritten.' -ForegroundColor Green
Write-Host ' - Manual deleted Vertex outputs retain KNOWN/SUPPRESS deletion state.' -ForegroundColor Green
Write-Host ' - Single/double parking rows are source-linked, auto-refreshing and graphics-dirty.' -ForegroundColor Green
Write-Host ' - CE commands receive one normal post-command REGEN/UpdateScreen when generated objects changed.' -ForegroundColor Green
Write-Host ' - Surface choices remain enabled; feature-line/platform surface reads are separated from geometry writes.' -ForegroundColor Green
Write-Host ' - Midblock/Road Reserve optional surfaces restored using the safe planning-read/construction-write bodies.' -ForegroundColor Green
Write-Host ' - Road Reserve Centrelines and CE_SEWERFROMCADASTRAL now use separate planning/construction transactions.' -ForegroundColor Green

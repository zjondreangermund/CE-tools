[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Replace-Once {
    param([string]$Path,[string]$Old,[string]$New,[string]$Description)
    $text = [System.IO.File]::ReadAllText($Path)
    if ($text.Contains($New)) { Write-Host "Already integrated: $Description" -ForegroundColor DarkGreen; return }
    if (-not $text.Contains($Old)) { throw "Could not integrate '$Description'. Marker not found in $Path" }
    $text = $text.Replace($Old,$New)
    [System.IO.File]::WriteAllText($Path,$text,[System.Text.UTF8Encoding]::new($false))
    Write-Host "Integrated: $Description" -ForegroundColor Green
}

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$src = Join-Path $root 'src\CE.Tools.Civil3D'
$sewer = Join-Path $src 'SewerProductionCommands.cs'
$storm = Join-Path $src 'StormwaterProductionCommands.cs'
$water = Join-Path $src 'WaterProductionCommands.cs'
foreach ($path in @($sewer,$storm,$water)) { if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Profile source missing: $path" } }

$oldSewer = @'
            try
            {
                int profiles;
                int views;
                int parts;
                CreateProfileObjects(
                    database,
                    civilDocument,
                    settings,
                    selectedSurface.ObjectId,
                    records,
                    pointResult.Value,
                    settings.ProfileColumns,
                    settings.ProfileHorizontalSpacing,
                    settings.ProfileVerticalSpacing,
                    out profiles,
                    out views,
                    out parts);

                ProfileViewBandRuntimeManager.RefreshAll(document);
                editor.WriteMessage(
                    "\nCE_SEWPROFILE complete. Surface profiles: {0}; profile views: {1}; network parts added where supported: {2}.",
                    profiles,
                    views,
                    parts);
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_SEWPROFILE cancelled. The transaction was not committed: " +
                    exception.Message);
            }
'@
$newSewer = @'
            int profiles = 0;
            int views = 0;
            int parts = 0;
            int skipped = 0;
            for (int index = 0; index < records.Count; index++)
            {
                SewerAlignmentRecord record = records[index];
                int row = index / Math.Max(1, settings.ProfileColumns);
                int column = index % Math.Max(1, settings.ProfileColumns);
                Point3d branchPoint = new Point3d(
                    pointResult.Value.X + column * settings.ProfileHorizontalSpacing,
                    pointResult.Value.Y - row * settings.ProfileVerticalSpacing,
                    pointResult.Value.Z);
                try
                {
                    int branchProfiles;
                    int branchViews;
                    int branchParts;
                    CreateProfileObjects(
                        database,
                        civilDocument,
                        settings,
                        selectedSurface.ObjectId,
                        new List<SewerAlignmentRecord> { record },
                        branchPoint,
                        1,
                        settings.ProfileHorizontalSpacing,
                        settings.ProfileVerticalSpacing,
                        out branchProfiles,
                        out branchViews,
                        out branchParts);
                    profiles += branchProfiles;
                    views += branchViews;
                    parts += branchParts;
                }
                catch (System.Exception exception)
                {
                    skipped++;
                    editor.WriteMessage(
                        "\nCE_SEWPROFILE skipped {0}: {1}",
                        record.BranchName,
                        exception.Message);
                }
            }
            try { ProfileViewBandRuntimeManager.RefreshAll(document); }
            catch (System.Exception exception)
            {
                editor.WriteMessage("\nCE_SEWPROFILE band refresh warning: " + exception.Message);
            }
            editor.WriteMessage(
                "\nCE_SEWPROFILE complete. Surface profiles: {0}; profile views: {1}; network parts added where supported: {2}; skipped branches: {3}.",
                profiles,
                views,
                parts,
                skipped);
'@
Replace-Once -Path $sewer -Old $oldSewer -New $newSewer -Description 'isolate sewer profile creation per branch'

$oldStorm = @'
            try
            {
                int profilesCreated;
                int viewsCreated;
                int partsAdded;
                CreateProfiles(
                    database,
                    civilDocument,
                    settings,
                    selectedSurface.ObjectId,
                    alignments,
                    insertionResult.Value,
                    settings.ProfileColumns,
                    settings.ProfileHorizontalSpacing,
                    settings.ProfileVerticalSpacing,
                    out profilesCreated,
                    out viewsCreated,
                    out partsAdded);

                editor.WriteMessage(
                    "\nCE_SWPROFILE complete. Surface profiles: {0}; profile views: {1}; network parts added where supported: {2}.",
                    profilesCreated,
                    viewsCreated,
                    partsAdded);
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_SWPROFILE cancelled. The transaction was not committed: " +
                    exception.Message);
            }
'@
$newStorm = @'
            int profilesCreated = 0;
            int viewsCreated = 0;
            int partsAdded = 0;
            int skipped = 0;
            for (int index = 0; index < alignments.Count; index++)
            {
                StormwaterAlignmentRecord record = alignments[index];
                int row = index / Math.Max(1, settings.ProfileColumns);
                int column = index % Math.Max(1, settings.ProfileColumns);
                Point3d branchPoint = new Point3d(
                    insertionResult.Value.X + column * settings.ProfileHorizontalSpacing,
                    insertionResult.Value.Y - row * settings.ProfileVerticalSpacing,
                    insertionResult.Value.Z);
                try
                {
                    int branchProfiles;
                    int branchViews;
                    int branchParts;
                    CreateProfiles(
                        database,
                        civilDocument,
                        settings,
                        selectedSurface.ObjectId,
                        new List<StormwaterAlignmentRecord> { record },
                        branchPoint,
                        1,
                        settings.ProfileHorizontalSpacing,
                        settings.ProfileVerticalSpacing,
                        out branchProfiles,
                        out branchViews,
                        out branchParts);
                    profilesCreated += branchProfiles;
                    viewsCreated += branchViews;
                    partsAdded += branchParts;
                }
                catch (System.Exception exception)
                {
                    skipped++;
                    editor.WriteMessage(
                        "\nCE_SWPROFILE skipped one alignment: {0}",
                        exception.Message);
                }
            }
            try { ProfileViewBandRuntimeManager.RefreshAll(document); }
            catch (System.Exception exception)
            {
                editor.WriteMessage("\nCE_SWPROFILE band refresh warning: " + exception.Message);
            }
            editor.WriteMessage(
                "\nCE_SWPROFILE complete. Surface profiles: {0}; profile views: {1}; network parts added where supported: {2}; skipped alignments: {3}.",
                profilesCreated,
                viewsCreated,
                partsAdded,
                skipped);
'@
Replace-Once -Path $storm -Old $oldStorm -New $newStorm -Description 'isolate stormwater profile creation per alignment'

# Water does not use a reusable outer helper, so replace only the transaction
# body with one transaction per AlignmentRecord. All existing private creation,
# tagging and band-binding helpers are retained.
$waterText = [System.IO.File]::ReadAllText($water)
if (-not $waterText.Contains('CE_WATERPROFILE skipped {0}: {1}')) {
    $pattern = '(?s)            try\s*\{\s*int profiles = 0;\s*int views = 0;\s*using \(Transaction transaction = document\.Database\.TransactionManager\.StartTransaction\(\)\)\s*\{.*?\n                    transaction\.Commit\(\);\s*\}\s*\n\s*editor\.WriteMessage\(\s*"\\nCE_WATERPROFILE complete\. Profiles: \{0\}; profile views: \{1\}\.".*?\n            \}\s*catch \(System\.Exception exception\)\s*\{.*?\n            \}'
    $matches = [regex]::Matches($waterText,$pattern)
    if ($matches.Count -ne 1) { throw "Could not isolate CE_WATERPROFILE transaction block. Matches=$($matches.Count)" }
    $replacement = @'
            int profiles = 0;
            int views = 0;
            int skipped = 0;
            for (int index = 0; index < records.Count; index++)
            {
                AlignmentRecord record = records[index];
                int column = index % Math.Max(1, settings.ProfileColumns);
                int row = index / Math.Max(1, settings.ProfileColumns);
                Point3d viewPoint = insertionResult.Value + new Vector3d(
                    column * settings.ProfileHorizontalSpacing,
                    -row * settings.ProfileVerticalSpacing,
                    0.0);
                try
                {
                    using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                    {
                        EnsureRegApp(document.Database, transaction);
                        ObjectId profileLayerId = GetOrCreateLayer(
                            document.Database,
                            transaction,
                            settings.ProfileLayer,
                            ProfileLayerDefault);
                        ObjectId profileStyleId = ResolveStyleId(
                            civilDocument.Styles.ProfileStyles,
                            settings.ProfileStyle,
                            "profile style",
                            transaction);
                        ObjectId profileLabelSetId = ResolveStyleId(
                            civilDocument.Styles.LabelSetStyles.ProfileLabelSetStyles,
                            settings.ProfileLabelSetStyle,
                            "profile label-set style",
                            transaction);
                        ObjectId profileViewStyleId = ResolveStyleId(
                            civilDocument.Styles.ProfileViewStyles,
                            settings.ProfileViewStyle,
                            "profile-view style",
                            transaction);
                        ObjectId profileViewBandSetId = ResolveStyleId(
                            civilDocument.Styles.ProfileViewBandSetStyles,
                            settings.ProfileViewBandSetStyle,
                            "profile-view band-set style",
                            transaction);

                        RemoveProfileObjects(document.Database, record.SourceHandle, transaction);
                        string profileName = UniqueName(
                            record.RouteName + " - EG",
                            ReadCivilNames(GetAlignmentProfileIds(civilDocument, transaction, false), transaction));
                        ObjectId profileId = InvokeCreateProfileFromSurface(
                            profileName,
                            record.AlignmentId,
                            selectedSurface.ObjectId,
                            profileLayerId,
                            profileStyleId,
                            profileLabelSetId);
                        DBObject profile = transaction.GetObject(profileId, OpenMode.ForWrite, false);
                        profile.XData = BuildTag(
                            "Profile",
                            record.RouteName,
                            record.SourceHandle,
                            selectedSurface.ObjectId.Handle.ToString());

                        string viewName = UniqueName(
                            record.RouteName + " - PROFILE VIEW",
                            ReadCivilNames(GetAlignmentProfileIds(civilDocument, transaction, true), transaction));
                        ObjectId viewId = InvokeCreateProfileView(
                            viewName,
                            record.AlignmentId,
                            viewPoint,
                            profileViewBandSetId,
                            profileViewStyleId);
                        DBObject view = transaction.GetObject(viewId, OpenMode.ForWrite, false);
                        view.XData = BuildTag(
                            "ProfileView",
                            record.RouteName,
                            record.SourceHandle,
                            selectedSurface.ObjectId.Handle.ToString());
                        TryAddPressurePartsToProfileView(record, viewId, transaction);
                        ObjectId bandNetworkId = ObjectId.Null;
                        if (!string.IsNullOrWhiteSpace(record.SourceHandle))
                        {
                            ObjectId routeSourceId;
                            if (TryGetObjectId(document.Database, record.SourceHandle, out routeSourceId))
                            {
                                DBObject routeSource = transaction.GetObject(routeSourceId, OpenMode.ForRead, false);
                                object networkIdValue = ReadProperty(routeSource, "NetworkId") ??
                                                        ReadProperty(routeSource, "PressureNetworkId");
                                if (networkIdValue is ObjectId) bandNetworkId = (ObjectId)networkIdValue;
                            }
                        }
                        ProfileViewBandDataBinder.Bind(view, profileId, ObjectId.Null, bandNetworkId);
                        transaction.Commit();
                    }
                    profiles++;
                    views++;
                }
                catch (System.Exception exception)
                {
                    skipped++;
                    editor.WriteMessage(
                        "\nCE_WATERPROFILE skipped {0}: {1}",
                        record.RouteName,
                        exception.Message);
                }
            }
            try { ProfileViewBandRuntimeManager.RefreshAll(document); }
            catch (System.Exception exception)
            {
                editor.WriteMessage("\nCE_WATERPROFILE band refresh warning: " + exception.Message);
            }
            editor.WriteMessage(
                "\nCE_WATERPROFILE complete. Profiles: {0}; profile views: {1}; skipped alignments: {2}.",
                profiles,
                views,
                skipped);
'@
    $waterText = [regex]::Replace($waterText,$pattern,[System.Text.RegularExpressions.MatchEvaluator]{ param($m) $replacement },1)
    [System.IO.File]::WriteAllText($water,$waterText,[System.Text.UTF8Encoding]::new($false))
    Write-Host 'Integrated water profile creation isolation per alignment.' -ForegroundColor Green
}
else { Write-Host 'Water profile isolation is already integrated.' -ForegroundColor DarkGreen }

foreach ($check in @(
    @{ Path=$sewer; Token='CE_SEWPROFILE skipped {0}: {1}'; Name='sewer' },
    @{ Path=$storm; Token='skipped alignments: {3}'; Name='stormwater' },
    @{ Path=$water; Token='CE_WATERPROFILE skipped {0}: {1}'; Name='water' }
)) {
    $value = [System.IO.File]::ReadAllText($check.Path)
    if (-not $value.Contains($check.Token)) { throw "Profile isolation verification failed for $($check.Name)." }
}
Write-Host 'Sewer, Stormwater and Water profile isolation repairs passed.' -ForegroundColor Cyan

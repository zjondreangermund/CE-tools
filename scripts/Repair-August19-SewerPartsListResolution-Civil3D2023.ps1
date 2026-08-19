[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$sewerPath = Join-Path $root 'src\CE.Tools.Civil3D\August13SewerMultiSourceNetworkCommands.cs'
if (-not (Test-Path -LiteralPath $sewerPath -PathType Leaf)) {
    throw "August 19 Sewer parts-list source missing: $sewerPath"
}

$utf8 = New-Object System.Text.UTF8Encoding($false)
$text = [System.IO.File]::ReadAllText($sewerPath) -replace "`r?`n", "`r`n"

function ReplaceMethod([string]$source,[string]$marker,[string]$replacement,[string]$label) {
    $replacement = $replacement -replace "`r?`n", "`r`n"
    $start = $source.IndexOf($marker,[StringComparison]::Ordinal)
    if ($start -lt 0) { throw "August 19 Sewer parts-list method marker not found: $label" }
    $open = $source.IndexOf('{',$start)
    if ($open -lt 0) { throw "August 19 Sewer parts-list opening brace not found: $label" }
    $depth = 0
    $close = -1
    for ($i=$open; $i -lt $source.Length; $i++) {
        if ($source[$i] -eq '{') { $depth++ }
        elseif ($source[$i] -eq '}') {
            $depth--
            if ($depth -eq 0) { $close = $i; break }
        }
    }
    if ($close -lt 0) { throw "August 19 Sewer parts-list closing brace not found: $label" }
    return $source.Substring(0,$start) + $replacement + $source.Substring($close + 1)
}

# Civil 3D 2023 drawings can contain a valid Parts List whose domain-filtered
# family collection is incomplete even though the list itself exposes the family
# through PartsList.Item[]. Resolve from BOTH paths, then pass the actual family
# and size ObjectIds directly to Network.AddLinePipe/AddStructure. A PartSize
# wrapper is used only to improve the display label; a valid size ObjectId must
# not be discarded just because the managed PartSize wrapper does not materialize.
$readPartChoices = @'
        private static List<PartChoice> ReadPartChoices(
            Database database,
            ObjectId partsListId,
            DomainType domain,
            bool skipNullStructure)
        {
            var result = new List<PartChoice>();
            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                PartsList list = transaction.GetObject(
                    partsListId,
                    OpenMode.ForRead,
                    false) as PartsList;
                if (list == null) return result;

                var familyIds = new List<ObjectId>();

                // Primary Civil 3D API path.
                try
                {
                    ObjectIdCollection domainFamilyIds =
                        list.GetPartFamilyIdsByDomain(domain);
                    if (domainFamilyIds != null)
                    {
                        foreach (ObjectId familyId in domainFamilyIds)
                        {
                            if (!familyId.IsNull &&
                                !familyId.IsErased &&
                                !familyIds.Contains(familyId))
                                familyIds.Add(familyId);
                        }
                    }
                }
                catch
                {
                    // Continue into the full-list fallback below.
                }

                // Civil 3D 2023 fallback: enumerate every family in the selected
                // Parts List and verify its actual Domain property.
                int familyCount = 0;
                try { familyCount = list.PartFamilyCount; }
                catch { familyCount = 0; }
                for (int familyIndex = 0; familyIndex < familyCount; familyIndex++)
                {
                    ObjectId familyId;
                    try { familyId = list[familyIndex]; }
                    catch { continue; }
                    if (familyId.IsNull || familyId.IsErased ||
                        familyIds.Contains(familyId))
                        continue;

                    PartFamily family;
                    try
                    {
                        family = transaction.GetObject(
                            familyId,
                            OpenMode.ForRead,
                            false) as PartFamily;
                    }
                    catch
                    {
                        continue;
                    }
                    if (family != null && family.Domain == domain)
                        familyIds.Add(familyId);
                }

                foreach (ObjectId familyId in familyIds)
                {
                    PartFamily family;
                    try
                    {
                        family = transaction.GetObject(
                            familyId,
                            OpenMode.ForRead,
                            false) as PartFamily;
                    }
                    catch
                    {
                        continue;
                    }
                    if (family == null || family.Domain != domain)
                        continue;

                    // Do not identify Null Structure by its editable display name.
                    // Civil 3D exposes an explicit StructNull part type.
                    if (skipNullStructure &&
                        family.PartType == Autodesk.Civil.DatabaseServices.PartType.StructNull)
                        continue;

                    int sizeCount = 0;
                    try { sizeCount = family.PartSizeCount; }
                    catch { sizeCount = 0; }
                    for (int sizeIndex = 0; sizeIndex < sizeCount; sizeIndex++)
                    {
                        ObjectId sizeId;
                        try { sizeId = family[sizeIndex]; }
                        catch { continue; }
                        if (sizeId.IsNull || sizeId.IsErased)
                            continue;

                        string sizeName =
                            "Size " + (sizeIndex + 1).ToString(CultureInfo.InvariantCulture);
                        try
                        {
                            PartSize size = transaction.GetObject(
                                sizeId,
                                OpenMode.ForRead,
                                false) as PartSize;
                            if (size != null && !string.IsNullOrWhiteSpace(size.Name))
                                sizeName = size.Name;
                        }
                        catch
                        {
                            // The ObjectId itself is what Network.AddLinePipe /
                            // AddStructure requires; keep it even if the wrapper
                            // cannot be opened only for its display name.
                        }

                        string familyName = string.IsNullOrWhiteSpace(family.Name)
                            ? "Part Family"
                            : family.Name;
                        result.Add(new PartChoice
                        {
                            FamilyId = familyId,
                            SizeId = sizeId,
                            FamilyName = familyName,
                            SizeName = sizeName,
                            Label = familyName + " | " + sizeName
                        });
                    }
                }
            }

            return result
                .GroupBy(
                    item => item.FamilyId.ToString() + "|" + item.SizeId.ToString(),
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(item => item.Label, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
'@
$text = ReplaceMethod $text '        private static List<PartChoice> ReadPartChoices(' $readPartChoices 'robust Civil 3D 2023 Parts List enumeration'

$oldFailure = @'
                editor.WriteMessage(
                    "\nCE_SEWERNETWORKMULTI: parts list '{0}' must contain at least one pipe size and one non-null structure size.",
                    partsList.Name);
'@ -replace "`n","`r`n"
$newFailure = @'
                editor.WriteMessage(
                    "\nCE_SEWERNETWORKMULTI: parts list '{0}' could not expose the required usable sizes (pipe sizes={1}; non-null structure sizes={2}). CE Tools checked both the Civil 3D domain collection and every family stored in the selected Parts List.",
                    partsList.Name,
                    pipeChoices.Count,
                    structureChoices.Count);
'@ -replace "`n","`r`n"
if ($text.Contains($oldFailure)) {
    $text = $text.Replace($oldFailure,$newFailure)
}
elseif (-not $text.Contains('CE Tools checked both the Civil 3D domain collection')) {
    throw 'August 19 Sewer parts-list diagnostic message anchor was not found.'
}

[System.IO.File]::WriteAllText($sewerPath,$text,$utf8)
$check = [System.IO.File]::ReadAllText($sewerPath)
foreach ($marker in @(
    'list.PartFamilyCount',
    'family.Domain == domain',
    'family.Domain != domain',
    'Autodesk.Civil.DatabaseServices.PartType.StructNull',
    'PartSize size = transaction.GetObject(',
    'The ObjectId itself is what Network.AddLinePipe',
    'CE Tools checked both the Civil 3D domain collection')) {
    if (-not $check.Contains($marker)) {
        throw "August 19 Sewer parts-list resolver marker missing: $marker"
    }
}
if ($check.Contains('family.Name.IndexOf(' + "`r`n" + '                            "null"')) {
    throw 'August 19 Sewer parts-list resolver failed: editable-name Null Structure filtering still survives.'
}

Write-Host 'August 19 Sewer Parts List resolution now checks domain families plus the full selected Parts List family collection.' -ForegroundColor Green
Write-Host 'Null Structure is filtered by Civil 3D PartType.StructNull, and valid family/size ObjectIds are retained even if a PartSize display wrapper cannot be opened.' -ForegroundColor Green

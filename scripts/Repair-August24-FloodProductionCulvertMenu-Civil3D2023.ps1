[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RepoRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepoRoot.Trim().Trim('"')).ProviderPath
$src = Join-Path $root 'src\CE.Tools.Civil3D'
$menuPath = Join-Path $src 'August14StructuredDisciplineProductionCentres.cs'
$floodPath = Join-Path $src 'FloodProductionCulvertDesignCommands.cs'
$catchmentBridgePath = Join-Path $src 'FloodNativeCatchmentBridge.cs'
$lowPointBridgePath = Join-Path $src 'FloodLowPointSamplingBridge.cs'
foreach ($required in @($menuPath,$floodPath,$catchmentBridgePath,$lowPointBridgePath)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Flood Production prerequisite missing: $required"
    }
}

$utf8 = New-Object System.Text.UTF8Encoding($false)

# Historical structured-production repairs can overwrite the Flood Analysis menu.
$menu = [System.IO.File]::ReadAllText($menuPath) -replace "`r?`n", "`r`n"
$command = 'CE_FLOODCULVERTDESIGN'
if (-not $menu.Contains($command)) {
    $anchor = '                    A("CE-Quick Flood / Rational Review", "CE_FLOODQUICK", "Pre/post return-period peak-flow and preliminary culvert screen.", "03 CREATE"),'
    $index = $menu.IndexOf($anchor,[StringComparison]::Ordinal)
    if ($index -lt 0) {
        throw 'August 24 Flood Production insertion anchor missing.'
    }
    $entry = '                    A("CE-Catchment + Culvert Hydraulic Design", "CE_FLOODCULVERTDESIGN", "Low point, longest watercourse, native catchment, Q2-Q100, culvert sizing, water levels and Hydraflow snapshot review.", "03 CREATE"),' + "`r`n"
    $menu = $menu.Insert($index,$entry)
}
if (-not $menu.Contains('A("CE-Catchment + Culvert Hydraulic Design", "CE_FLOODCULVERTDESIGN"')) {
    throw 'August 24 Flood Production culvert design menu guard missing.'
}
[System.IO.File]::WriteAllText($menuPath,$menu,$utf8)

# Civil 3D 2023 exposes FeatureLinePointType in Autodesk.Civil (not in the
# DatabaseServices namespace). Normalize the authored alias after all old repair
# packs have run, immediately before CoreCompile.
$flood = [System.IO.File]::ReadAllText($floodPath) -replace "`r?`n", "`r`n"
$flood = $flood.Replace(
    'using CivilFeatureLinePointType = Autodesk.Civil.DatabaseServices.FeatureLinePointType;',
    'using CivilFeatureLinePointType = Autodesk.Civil.FeatureLinePointType;')

# Flood Production uses WPF Image for the Hydraflow snapshot while importing
# AutoCAD DatabaseServices, which also exposes Image. Qualify the WPF type with a
# using alias so the real Civil 3D 2023 compiler cannot report CS0104 ambiguity.
$wpfImageAlias = 'using Image = System.Windows.Controls.Image;'
if (-not $flood.Contains($wpfImageAlias)) {
    $imageAnchor = 'using System.Windows.Controls;'
    $imageIndex = $flood.IndexOf($imageAnchor,[StringComparison]::Ordinal)
    if ($imageIndex -lt 0) {
        throw 'August 24 Flood Production WPF Image alias insertion anchor missing.'
    }
    $insertAt = $imageIndex + $imageAnchor.Length
    $flood = $flood.Insert($insertAt,"`r`n" + $wpfImageAlias)
}

# Route the active command through the dedicated surface-aware low-point sampler.
# It samples Alignments and 2D polylines against the selected TIN surface and uses
# all Civil FeatureLine control/elevation points, while preserving the old private
# helper as harmless historical source.
$oldLowPointCall = '                CrossingLowPoint lowPoint = FindLowestPoint('
$newLowPointCall = '                CrossingLowPoint lowPoint = FloodLowPointSamplingBridge.FindLowestPoint('
if ($flood.Contains($oldLowPointCall)) {
    $flood = $flood.Replace($oldLowPointCall,$newLowPointCall)
}

# Create a native Civil 3D Catchment as part of the same CE command. Keep CE plan
# graphics even when a drawing has no catchment style; the bridge reports that
# condition without corrupting source terrain or centreline objects.
$nativeCall = '                FloodNativeCatchmentBridge.TryCreate(document.Database, result);'
if (-not $flood.Contains($nativeCall)) {
    $anchor = '                CreateDrawingOutput(document.Database, result);'
    $index = $flood.IndexOf($anchor,[StringComparison]::Ordinal)
    if ($index -lt 0) {
        throw 'August 24 Flood Production native Catchment insertion anchor missing.'
    }
    $insertAt = $index + $anchor.Length
    $flood = $flood.Insert($insertAt,"`r`n" + $nativeCall)
}

if ($flood.Contains('Autodesk.Civil.DatabaseServices.FeatureLinePointType')) {
    throw 'August 24 Flood Production Civil 3D 2023 FeatureLinePointType compatibility failed.'
}
if (-not $flood.Contains('using CivilFeatureLinePointType = Autodesk.Civil.FeatureLinePointType;')) {
    throw 'August 24 Flood Production FeatureLinePointType alias missing.'
}
if (-not $flood.Contains($wpfImageAlias)) {
    throw 'August 24 Flood Production WPF Image compile alias missing.'
}
if (-not $flood.Contains($newLowPointCall)) {
    throw 'August 24 Flood Production surface-aware low-point routing missing.'
}
if (([regex]::Matches($flood,[regex]::Escape($nativeCall))).Count -ne 1) {
    throw 'August 24 Flood Production expected exactly one native Catchment bridge call.'
}
[System.IO.File]::WriteAllText($floodPath,$flood,$utf8)

# GridCell is defined in CE.Tools.Core. The native catchment bridge lives in the
# Civil 3D assembly, so make the cross-project type import explicit after all
# historical source transforms and before the real Autodesk/Civil compile.
$catchmentBridge = [System.IO.File]::ReadAllText($catchmentBridgePath) -replace "`r?`n", "`r`n"
$coreUsing = 'using CETools.Core;'
if (-not $catchmentBridge.Contains($coreUsing)) {
    $coreAnchor = 'using System.Linq;'
    $coreIndex = $catchmentBridge.IndexOf($coreAnchor,[StringComparison]::Ordinal)
    if ($coreIndex -lt 0) {
        throw 'August 24 Flood Production GridCell namespace insertion anchor missing.'
    }
    $insertAt = $coreIndex + $coreAnchor.Length
    $catchmentBridge = $catchmentBridge.Insert($insertAt,"`r`n" + $coreUsing)
}
if (-not $catchmentBridge.Contains($coreUsing) -or
    -not $catchmentBridge.Contains('IReadOnlyList<GridCell> catchment')) {
    throw 'August 24 Flood Production GridCell compile qualification failed.'
}

# The user's Civil 3D 2023 AeccDbMgd does not expose CivilDocument.GetCatchmentGroups
# at compile time even though newer API documentation lists it. Keep this boundary
# compatible with both API shapes: reflection when a collection accessor exists,
# otherwise collision-safe CatchmentGroup.Create without a hard method reference.
$legacyGroupBlock = @'
                ObjectId groupId;
                var groups = civil.GetCatchmentGroups();
                if (groups.Contains(GroupName)) groupId = groups[GroupName];
                else groupId = CatchmentGroup.Create(database, GroupName);
'@ -replace "`r?`n", "`r`n"
$compatGroupBlock = @'
                ObjectId groupId = ResolveOrCreateCatchmentGroup(database, civil);
                if (groupId.IsNull)
                {
                    Write(document, "\nCE Flood: native Civil 3D Catchment group could not be resolved; CE plan graphics were retained.");
                    return ObjectId.Null;
                }
'@ -replace "`r?`n", "`r`n"
if ($catchmentBridge.Contains($legacyGroupBlock)) {
    $catchmentBridge = $catchmentBridge.Replace($legacyGroupBlock,$compatGroupBlock)
}

if (-not $catchmentBridge.Contains('private static ObjectId ResolveOrCreateCatchmentGroup(')) {
    $helperAnchor = '        private static string BuildUniqueName(Database database, ObjectId groupId, string description)'
    $helperIndex = $catchmentBridge.IndexOf($helperAnchor,[StringComparison]::Ordinal)
    if ($helperIndex -lt 0) {
        throw 'August 24 Flood Production CatchmentGroup compatibility helper anchor missing.'
    }
    $helpers = @'
        private static ObjectId ResolveOrCreateCatchmentGroup(Database database, CivilDocument civil)
        {
            ObjectId existing = TryGetCatchmentGroupId(civil, database, GroupName);
            if (!existing.IsNull) return existing;

            try
            {
                return CatchmentGroup.Create(database, GroupName);
            }
            catch
            {
                existing = TryGetCatchmentGroupId(civil, database, GroupName);
                if (!existing.IsNull) return existing;
                for (int suffix = 2; suffix < 10000; suffix++)
                {
                    try
                    {
                        return CatchmentGroup.Create(
                            database,
                            GroupName + " " + suffix.ToString(CultureInfo.InvariantCulture));
                    }
                    catch
                    {
                    }
                }
            }
            return ObjectId.Null;
        }

        private static ObjectId TryGetCatchmentGroupId(CivilDocument civil, Database database, string name)
        {
            object collection = null;
            try
            {
                if (civil != null)
                {
                    System.Reflection.MethodInfo instanceMethod = civil.GetType().GetMethod(
                        "GetCatchmentGroups",
                        Type.EmptyTypes);
                    if (instanceMethod != null)
                        collection = instanceMethod.Invoke(civil, null);
                }
            }
            catch
            {
            }

            if (collection == null)
            {
                try
                {
                    Type collectionType = typeof(CatchmentGroup).Assembly.GetType(
                        "Autodesk.Civil.DatabaseServices.CatchmentGroupCollection");
                    if (collectionType != null)
                    {
                        System.Reflection.MethodInfo staticMethod = collectionType.GetMethod(
                            "GetCatchmentGroups",
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                            null,
                            new[] { typeof(Database) },
                            null);
                        if (staticMethod != null)
                            collection = staticMethod.Invoke(null, new object[] { database });
                    }
                }
                catch
                {
                }
            }

            return TryReadCatchmentGroupId(collection, name);
        }

        private static ObjectId TryReadCatchmentGroupId(object collection, string name)
        {
            if (collection == null || string.IsNullOrWhiteSpace(name)) return ObjectId.Null;
            try
            {
                Type collectionType = collection.GetType();
                System.Reflection.MethodInfo contains = collectionType.GetMethod(
                    "Contains",
                    new[] { typeof(string) });
                if (contains != null)
                {
                    object hasValue = contains.Invoke(collection, new object[] { name });
                    if (hasValue is bool && !(bool)hasValue) return ObjectId.Null;
                }

                System.Reflection.MethodInfo getter = collectionType.GetMethod(
                    "get_Item",
                    new[] { typeof(string) });
                if (getter != null)
                {
                    object value = getter.Invoke(collection, new object[] { name });
                    if (value is ObjectId) return (ObjectId)value;
                }
            }
            catch
            {
            }
            return ObjectId.Null;
        }

'@ -replace "`r?`n", "`r`n"
    $catchmentBridge = $catchmentBridge.Insert($helperIndex,$helpers)
}

if ($catchmentBridge.Contains('civil.GetCatchmentGroups()')) {
    throw 'August 24 Flood Production CS1061 compatibility failed: direct CivilDocument.GetCatchmentGroups call remains.'
}
if (-not $catchmentBridge.Contains('ResolveOrCreateCatchmentGroup(database, civil)') -or
    -not $catchmentBridge.Contains('private static ObjectId ResolveOrCreateCatchmentGroup(') -or
    -not $catchmentBridge.Contains('CatchmentGroup.Create(database, GroupName)')) {
    throw 'August 24 Flood Production CatchmentGroup Civil 3D 2023 compatibility failed.'
}
[System.IO.File]::WriteAllText($catchmentBridgePath,$catchmentBridge,$utf8)

Write-Host 'August 24 Flood Production catchment/culvert workflow finalized for Civil 3D 2023.' -ForegroundColor Green
Write-Host ' - integrated Flood menu entry present.' -ForegroundColor Green
Write-Host ' - Alignment/polyline/FeatureLine low-point sampler routed.' -ForegroundColor Green
Write-Host ' - FeatureLinePointType qualified for Civil 3D 2023.' -ForegroundColor Green
Write-Host ' - WPF Hydraflow Image and Core GridCell types qualified for compilation.' -ForegroundColor Green
Write-Host ' - CatchmentGroup lookup is compatible with Civil 3D 2023 API variants.' -ForegroundColor Green
Write-Host ' - native Civil 3D Catchment bridge chained after CE plan output.' -ForegroundColor Green

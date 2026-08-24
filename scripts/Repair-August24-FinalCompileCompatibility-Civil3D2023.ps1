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
        throw "August 24 final compile prerequisite missing: $path"
    }
    return $path
}
function ReadText([string]$path) {
    return [System.IO.File]::ReadAllText($path) -replace "`r?`n", "`r`n"
}
function WriteText([string]$path,[string]$text) {
    [System.IO.File]::WriteAllText($path,($text -replace "`r?`n","`r`n"),$utf8)
}

# -----------------------------------------------------------------------------
# Dynamic Grid: normalize the late August 18/23 merged settings/table surface.
# The packaged chain can leave an obsolete ReadGridSurfaceNames call, move the
# surfaceChoices declaration below its first use, and retain an older six-argument
# PopulateTable invocation after the method itself has been replaced with four args.
# -----------------------------------------------------------------------------
$gridPath = Required 'August18DynamicGridSettingOutCommands.cs'
$grid = ReadText $gridPath
$grid = $grid.Replace('ReadGridSurfaceNames(', 'ReadSurfaceNames(')

$createMarker = '        public void CreateDynamicGrid()'
$nextMarker = '        [CommandMethod('
$createStart = $grid.IndexOf($createMarker,[StringComparison]::Ordinal)
if ($createStart -lt 0) { throw 'August 24 final compile Grid CreateDynamicGrid marker missing.' }
$createEnd = $grid.IndexOf($nextMarker,$createStart + $createMarker.Length,[StringComparison]::Ordinal)
if ($createEnd -lt 0) { throw 'August 24 final compile Grid CreateDynamicGrid end marker missing.' }
$create = $grid.Substring($createStart,$createEnd-$createStart)

# Remove every local surfaceChoices declaration/Insert produced by historical
# staging and re-establish one canonical declaration before the settings model.
$create = [regex]::Replace(
    $create,
    '(?m)^[ \t]*List<string>[ \t]+surfaceChoices[ \t]*=[^;]+;[ \t]*\r\n?',
    '')
$create = [regex]::Replace(
    $create,
    '(?m)^[ \t]*surfaceChoices\.Insert\(0,[ \t]*"<None>"\);[ \t]*\r\n?',
    '')
$sourceGuard = '            if (sourceIds.Count == 0) return;'
$guardIndex = $create.IndexOf($sourceGuard,[StringComparison]::Ordinal)
if ($guardIndex -lt 0) { throw 'August 24 final compile Grid source guard missing.' }
$insertAt = $guardIndex + $sourceGuard.Length
$surfaceDecl = "`r`n`r`n            List<string> surfaceChoices = ReadSurfaceNames(document.Database, civil);`r`n            surfaceChoices.Insert(0, `"<None>`");"
$create = $create.Insert($insertAt,$surfaceDecl)
$grid = $grid.Substring(0,$createStart) + $create + $grid.Substring($createEnd)

# Normalize only executable PopulateTable statements, never the method declaration.
$populatePattern = '(?ms)^(?<indent>[ \t]*)PopulateTable\s*\([^;]*?\);'
$grid = [regex]::Replace(
    $grid,
    $populatePattern,
    '${indent}PopulateTable(document.Database, table, records, link);')

if ($grid.Contains('ReadGridSurfaceNames(')) {
    throw 'August 24 final compile Grid obsolete ReadGridSurfaceNames call survived.'
}
$declIndex = $grid.IndexOf('List<string> surfaceChoices = ReadSurfaceNames(document.Database, civil);',[StringComparison]::Ordinal)
$firstUseIndex = $grid.IndexOf('surfaceChoices.ToArray()',[StringComparison]::Ordinal)
if ($declIndex -lt 0 -or ($firstUseIndex -ge 0 -and $declIndex -gt $firstUseIndex)) {
    throw 'August 24 final compile Grid surfaceChoices declaration is still after first use.'
}
$populateCount = ([regex]::Matches($grid,'(?m)^[ \t]*PopulateTable\(document\.Database, table, records, link\);')).Count
if ($populateCount -ne 2) {
    throw "August 24 final compile Grid expected two canonical PopulateTable calls; found $populateCount."
}
WriteText $gridPath $grid

# -----------------------------------------------------------------------------
# Multiple Boundary Edit: C# forbids the later List<Entity> keep local when an
# earlier branch in the same enclosing scope already declares bool keep.
# -----------------------------------------------------------------------------
$boundaryPath = Required 'MultiBoundaryEditCommands.cs'
$boundary = ReadText $boundaryPath
$oldWhole = @'
                                        bool keep = keepInside ? inside : !inside;
                                        if (!keep)
'@ -replace "`r?`n","`r`n"
$newWhole = @'
                                        bool keepWhole = keepInside ? inside : !inside;
                                        if (!keepWhole)
'@ -replace "`r?`n","`r`n"
if ($boundary.Contains($oldWhole)) {
    $boundary = $boundary.Replace($oldWhole,$newWhole)
}
if ($boundary.Contains('bool keep = keepInside ? inside : !inside;')) {
    throw 'August 24 final compile MultiBoundary local-name collision survived.'
}
WriteText $boundaryPath $boundary

# -----------------------------------------------------------------------------
# Multiple Dimensions: August 21 may leave a four-argument chain dispatch while
# the August 23 mixed-source replacement intentionally exposes the current
# three-argument DimensionOpenPolylineChain method.
# -----------------------------------------------------------------------------
$dimensionPath = Required 'MultiDimensionCommands.cs'
$dimension = ReadText $dimensionPath
$chainCallPattern = '(?ms)^(?<indent>[ \t]*)DimensionOpenPolylineChain\s*\(\s*document\s*,\s*selection\s*,\s*requestedStyle(?:\s*,\s*[^;]*?)?\s*\);'
$dimension = [regex]::Replace(
    $dimension,
    $chainCallPattern,
    '${indent}DimensionOpenPolylineChain(document, selection, requestedStyle);')
$chainCallCount = ([regex]::Matches($dimension,'(?m)^[ \t]*DimensionOpenPolylineChain\(document, selection, requestedStyle\);')).Count
if ($chainCallCount -ne 1) {
    throw "August 24 final compile Multiple Dimensions expected one canonical chain call; found $chainCallCount."
}
WriteText $dimensionPath $dimension

# -----------------------------------------------------------------------------
# Vertex Setting-Out: Database.GetBlockTableRecordId is not part of the AutoCAD
# 2023 Database API. Replace it with a small BlockTable lookup helper.
# -----------------------------------------------------------------------------
$vertexPath = Required 'VertexSettingOutCommands.cs'
$vertex = ReadText $vertexPath
$vertex = $vertex.Replace('database.GetBlockTableRecordId(name)', 'ResolveDimensionArrowBlock(database, name)')
if (-not $vertex.Contains('private static ObjectId ResolveDimensionArrowBlock(')) {
    $marker = '        private static void SetClosedFilledDimensionArrow('
    $markerIndex = $vertex.IndexOf($marker,[StringComparison]::Ordinal)
    if ($markerIndex -lt 0) { throw 'August 24 final compile Vertex arrow helper insertion marker missing.' }
    $helper = @'
        private static ObjectId ResolveDimensionArrowBlock(Database database, string name)
        {
            if (database == null || string.IsNullOrWhiteSpace(name)) return ObjectId.Null;
            try
            {
                using (Transaction transaction = database.TransactionManager.StartOpenCloseTransaction())
                {
                    BlockTable table = transaction.GetObject(
                        database.BlockTableId,
                        OpenMode.ForRead,
                        false) as BlockTable;
                    if (table == null || !table.Has(name)) return ObjectId.Null;
                    return table[name];
                }
            }
            catch
            {
                return ObjectId.Null;
            }
        }

'@ -replace "`r?`n","`r`n"
    $vertex = $vertex.Insert($markerIndex,$helper)
}
if ($vertex.Contains('.GetBlockTableRecordId(')) {
    throw 'August 24 final compile Vertex unsupported Database.GetBlockTableRecordId call survived.'
}
WriteText $vertexPath $vertex

Write-Host 'August 24 final Civil 3D compile compatibility normalized.' -ForegroundColor Green
Write-Host 'Grid settings/table calls, boundary locals, chain dispatch and Vertex arrow lookup are compile-safe.' -ForegroundColor Green

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
        throw "August 23 setting-out/CAD prerequisite missing: $path"
    }
    return $path
}
function ReadText([string]$path) {
    return [System.IO.File]::ReadAllText($path) -replace "`r?`n", "`r`n"
}
function WriteText([string]$path,[string]$text) {
    [System.IO.File]::WriteAllText($path,($text -replace "`r?`n","`r`n"),$utf8)
}
function ReplaceBetween(
    [string]$text,
    [string]$startMarker,
    [string]$nextMarker,
    [string]$replacement,
    [string]$label) {

    $start = $text.IndexOf($startMarker,[StringComparison]::Ordinal)
    if ($start -lt 0) { throw "August 23 $label start marker not found: $startMarker" }
    $next = $text.IndexOf($nextMarker,$start + $startMarker.Length,[StringComparison]::Ordinal)
    if ($next -lt 0) { throw "August 23 $label next marker not found: $nextMarker" }
    return $text.Substring(0,$start) + ($replacement.TrimEnd() -replace "`r?`n","`r`n") + "`r`n`r`n" + $text.Substring($next)
}
function InsertOnceBefore(
    [string]$text,
    [string]$marker,
    [string]$insert,
    [string]$presence,
    [string]$label) {
    if ($text.Contains($presence)) { return $text }
    $index = $text.IndexOf($marker,[StringComparison]::Ordinal)
    if ($index -lt 0) { throw "August 23 $label insertion marker not found: $marker" }
    return $text.Substring(0,$index) + ($insert.TrimEnd() -replace "`r?`n","`r`n") + "`r`n`r`n" + $text.Substring($index)
}

$vertexPath = Required 'VertexSettingOutCommands.cs'
$gridPath = Required 'August18DynamicGridSettingOutCommands.cs'
$dimensionPath = Required 'MultiDimensionCommands.cs'
$productionPath = Required 'August11ProductionCentreCommands.cs'
$productionV3Path = Required 'August14ProductionCentres.cs'

# -----------------------------------------------------------------------------
# 1. Vertex Setting-Out field visibility, anchored COGO labels and radial sizes.
# -----------------------------------------------------------------------------
$vertex = ReadText $vertexPath
if (-not $vertex.Contains('using System.Reflection;')) {
    $vertex = $vertex.Replace('using System.Linq;','using System.Linq;`r`nusing System.Reflection;'.Replace('`r`n',"`r`n"))
}

if (-not $vertex.Contains('private static void ResetCogoLabel(CivilCogoPoint point)')) {
    $helper = @'
        private static void ResetCogoLabel(CivilCogoPoint point)
        {
            if (point == null) return;
            try
            {
                MethodInfo reset = point.GetType().GetMethod(
                    "ResetLabel",
                    BindingFlags.Instance | BindingFlags.Public,
                    null,
                    Type.EmptyTypes,
                    null);
                if (reset != null) reset.Invoke(point, null);
            }
            catch { }
            try { point.RecordGraphicsModified(true); } catch { }
        }

        private static void SetDimensionDoubleOverride(
            Dimension dimension,
            string propertyName,
            double value)
        {
            if (dimension == null || string.IsNullOrWhiteSpace(propertyName)) return;
            try
            {
                PropertyInfo property = dimension.GetType().GetProperty(
                    propertyName,
                    BindingFlags.Instance | BindingFlags.Public);
                if (property != null && property.CanWrite && property.PropertyType == typeof(double))
                    property.SetValue(dimension, value, null);
            }
            catch { }
        }
'@
    $vertex = InsertOnceBefore `
        $vertex `
        '        private static void SetClosedFilledDimensionArrow(' `
        $helper `
        'private static void ResetCogoLabel(CivilCogoPoint point)' `
        'Vertex COGO/dimension helper'
}

# Reset COGO labels after source-linked point creation/update. This deliberately
# resets only Civil point labels: source geometry moves the COGO point and its
# default label anchor together instead of preserving a stale dragged label.
$vertex = [regex]::Replace(
    $vertex,
    '(?s)(try\s*\{\s*point\.PointName\s*=\s*record\.PointName;\s*\}\s*catch\s*\{\s*\}\s*)(?!ResetCogoLabel\(point\);)',
    '$1                ResetCogoLabel(point);`r`n'.Replace('`r`n',"`r`n"),
    1)
$vertex = [regex]::Replace(
    $vertex,
    '(?s)(try\s*\{\s*cogo\.PointName\s*=\s*record\.PointName;\s*\}\s*catch\s*\{\s*\}\s*)(?!ResetCogoLabel\(cogo\);)',
    '$1                ResetCogoLabel(cogo);`r`n'.Replace('`r`n',"`r`n"),
    1)

$vertexArrow = @'
        private static void SetClosedFilledDimensionArrow(Dimension dimension, Database database)
        {
            if (dimension == null || database == null) return;
            ObjectId arrow = ObjectId.Null;
            foreach (string name in new[] { "_CLOSEDFILLED", "CLOSEDFILLED" })
            {
                try
                {
                    ObjectId candidate = database.GetBlockTableRecordId(name);
                    if (!candidate.IsNull)
                    {
                        arrow = candidate;
                        break;
                    }
                }
                catch { }
            }

            if (!arrow.IsNull)
            {
                try { dimension.Dimblk1 = arrow; } catch { }
                try { dimension.Dimblk2 = arrow; } catch { }
            }

            // Tester requirement: radial dimension properties must expose the
            // actual requested values, not inherited 0.18-size DIMSTYLE values.
            SetDimensionDoubleOverride(dimension, "Dimasz", 3.0);
            SetDimensionDoubleOverride(dimension, "Dimtxt", 1.8);
            try { dimension.RecordGraphicsModified(true); } catch { }
        }
'@
$vertex = ReplaceBetween `
    $vertex `
    '        private static void SetClosedFilledDimensionArrow(' `
    '        private static void SetDimensionTextMovementNoLeader(' `
    $vertexArrow `
    'Vertex radial dimension override'

# A committed Civil point/table group must become visible without requiring
# AUDIT/OVERKILL/PURGE or a second user command.
$vertex = [regex]::Replace(
    $vertex,
    'document\.Editor\.Regen\(\);(?!\s*try\s*\{\s*AcApplication\.UpdateScreen\(\);)',
    'document.Editor.Regen();`r`n                try { AcApplication.UpdateScreen(); } catch { }'.Replace('`r`n',"`r`n"))
WriteText $vertexPath $vertex

# -----------------------------------------------------------------------------
# 2. Dynamic Grid Setting-Out: full grid lines, colour, two surface levels,
#    anchored point labels and persistent refresh state.
# -----------------------------------------------------------------------------
$grid = ReadText $gridPath
if (-not $grid.Contains('using System.Reflection;')) {
    $grid = $grid.Replace('using System.Linq;','using System.Linq;`r`nusing System.Reflection;'.Replace('`r`n',"`r`n"))
}
$grid = $grid.Replace('private const string Version = "1";','private const string Version = "2";')

if (-not $grid.Contains('List<string> surfaceChoices = ReadSurfaceNames(')) {
    $anchor = '            if (sourceIds.Count == 0) return;'
    if (-not $grid.Contains($anchor)) { throw 'August 23 Grid surface-choice anchor missing.' }
    $grid = $grid.Replace($anchor,@'
            if (sourceIds.Count == 0) return;

            List<string> surfaceChoices = ReadSurfaceNames(document.Database, civil);
            surfaceChoices.Insert(0, "<None>");
'@.TrimEnd() -replace "`r?`n","`r`n")
}

if (-not $grid.Contains('"GridLines", "01 Grid"')) {
    $marker = @'
            settings.AddText(
                "Prefix", "02 Numbering", "Point prefix", "G",
'@ -replace "`r?`n","`r`n"
    if (-not $grid.Contains($marker)) { throw 'August 23 Grid line setting insertion marker missing.' }
    $insert = @'
            settings.AddChoice(
                "GridLines", "01 Grid", "Full-grid line objects", "Points only",
                "When Full grid is selected, optionally create clipped horizontal/vertical grid-line objects that remain linked to the source boundary.",
                new[] { "Points only", "Points + full grid lines" });
            settings.AddPositiveInteger(
                "GridColor", "01 Grid", "Grid line colour index", 3,
                "AutoCAD colour index 1-255 for generated full-grid line objects.");
            settings.AddChoice(
                "NGSurface", "02 Levels", "NG surface", "<None>",
                "Surface sampled dynamically into the NG LEVEL table column.",
                surfaceChoices.ToArray());
            settings.AddChoice(
                "DesignSurface", "02 Levels", "Design surface", "<None>",
                "Surface sampled dynamically into the DESIGN LEVEL table column.",
                surfaceChoices.ToArray());
'@ -replace "`r?`n","`r`n"
    $grid = $grid.Replace($marker,$insert + $marker)
}

if (-not $grid.Contains('DrawGridLines = string.Equals(settings.Text("GridLines")')) {
    $anchor = '                Dy = settings.Double("DY", 10.0),'
    if (-not $grid.Contains($anchor)) { throw 'August 23 Grid link initializer anchor missing.' }
    $grid = $grid.Replace($anchor,@'
                Dy = settings.Double("DY", 10.0),
                DrawGridLines = string.Equals(settings.Text("GridLines"), "Points + full grid lines", StringComparison.OrdinalIgnoreCase),
                GridColorIndex = Math.Max(1, Math.Min(255, settings.Integer("GridColor", 3))),
                NgSurfaceHandle = ResolveSurfaceHandle(document.Database, civil, settings.Text("NGSurface")),
                DesignSurfaceHandle = ResolveSurfaceHandle(document.Database, civil, settings.Text("DesignSurface")),
'@.TrimEnd() -replace "`r?`n","`r`n")
}

# Apply level sampling immediately after each record rebuild.
$grid = [regex]::Replace(
    $grid,
    '(List<GridRecord> records = BuildRecords\(sources, link\);)(?!\s*ApplySurfaceLevels)',
    '$1`r`n                ApplySurfaceLevels(document.Database, transaction, records, link);'.Replace('`r`n',"`r`n"))

# Keep generated line objects in the same transaction and refresh them before the table link is rewritten.
$grid = [regex]::Replace(
    $grid,
    '(\s+)(PopulateTable\(document\.Database, table, records, link\);)(?!\s*// CE_AUG23_GRID_SYNC)',
    '$1SyncGridLines(document.Database, transaction, sources, link); // CE_AUG23_GRID_SYNC$1$2')

$gridCreateCogo = @'
        private static ObjectId CreateCogo(
            Database database,
            CivilDocument civil,
            Transaction transaction,
            GridRecord record)
        {
            ObjectId id = civil.CogoPoints.Add(
                record.Point,
                record.Name,
                true);
            CivilCogoPoint point = transaction.GetObject(
                id,
                OpenMode.ForWrite,
                false) as CivilCogoPoint;
            if (point == null)
                throw new InvalidOperationException("Civil 3D did not return a COGO point.");
            point.RawDescription = record.Name;
            ResetCogoLabel(point);
            return id;
        }
'@
$grid = ReplaceBetween `
    $grid `
    '        private static ObjectId CreateCogo(' `
    '        private static bool UpdateCogo(' `
    $gridCreateCogo `
    'Grid CreateCogo'

$gridUpdateCogo = @'
        private static bool UpdateCogo(
            Transaction transaction,
            ObjectId id,
            GridRecord record)
        {
            if (id.IsNull || id.IsErased) return false;
            CivilCogoPoint point;
            try
            {
                point = transaction.GetObject(
                    id,
                    OpenMode.ForWrite,
                    false) as CivilCogoPoint;
            }
            catch
            {
                return false;
            }
            if (point == null) return false;
            point.Easting = record.Point.X;
            point.Northing = record.Point.Y;
            point.Elevation = record.Point.Z;
            point.RawDescription = record.Name;
            ResetCogoLabel(point);
            return true;
        }
'@
$grid = ReplaceBetween `
    $grid `
    '        private static bool UpdateCogo(' `
    '        private static List<ObjectId> SelectBoundaries(' `
    $gridUpdateCogo `
    'Grid UpdateCogo'

$gridPopulate = @'
        private static void PopulateTable(
            Database database,
            Table table,
            IList<GridRecord> records,
            DynamicGridLink link)
        {
            double textHeight = PaperAnnotationScale.ModelTextHeight(
                database,
                link.PaperHeight > 0.0 ? link.PaperHeight : 2.0);
            table.SetSize(records.Count + 2, 7);
            table.SetRowHeight(Math.Max(textHeight * 1.8, 0.001));
            table.SetColumnWidth(Math.Max(textHeight * 9.0, 0.001));
            table.Columns[1].Width = Math.Max(textHeight * 14.0, 0.001);
            table.Columns[4].Width = Math.Max(textHeight * 11.0, 0.001);
            table.Columns[5].Width = Math.Max(textHeight * 13.0, 0.001);
            table.Cells[0, 0].TextString =
                "CE GRID SETTING-OUT - " + link.Mode.ToUpperInvariant();
            table.MergeCells(CellRange.Create(table, 0, 0, 0, 6));
            string[] headings =
            {
                "POINT", "SOURCE", "X", "Y", "NG LEVEL", "DESIGN LEVEL", "MODE"
            };
            for (int column = 0; column < headings.Length; column++)
                table.Cells[1, column].TextString = headings[column];

            for (int index = 0; index < records.Count; index++)
            {
                GridRecord record = records[index];
                int row = index + 2;
                table.Cells[row, 0].TextString = record.Name;
                table.Cells[row, 1].TextString = record.Source;
                table.Cells[row, 2].TextString = record.Point.X.ToString("N3", CultureInfo.CurrentCulture);
                table.Cells[row, 3].TextString = record.Point.Y.ToString("N3", CultureInfo.CurrentCulture);
                table.Cells[row, 4].TextString = FormatLevel(record.NgLevel);
                table.Cells[row, 5].TextString = FormatLevel(record.DesignLevel);
                table.Cells[row, 6].TextString = link.Mode;
            }

            for (int row = 0; row < table.Rows.Count; row++)
                for (int column = 0; column < table.Columns.Count; column++)
                {
                    table.Cells[row, column].Alignment = CellAlignment.MiddleCenter;
                    table.Cells[row, column].TextHeight = textHeight;
                }
            try { table.GenerateLayout(); } catch { }
            try { table.RecordGraphicsModified(true); } catch { }
        }

        private static string FormatLevel(double? value)
        {
            return value.HasValue
                ? value.Value.ToString("N3", CultureInfo.CurrentCulture)
                : string.Empty;
        }

        private static void ResetCogoLabel(CivilCogoPoint point)
        {
            if (point == null) return;
            try
            {
                MethodInfo reset = point.GetType().GetMethod(
                    "ResetLabel",
                    BindingFlags.Instance | BindingFlags.Public,
                    null,
                    Type.EmptyTypes,
                    null);
                if (reset != null) reset.Invoke(point, null);
            }
            catch { }
            try { point.RecordGraphicsModified(true); } catch { }
        }

        private static List<string> ReadSurfaceNames(
            Database database,
            CivilDocument civil)
        {
            var result = new List<string>();
            if (database == null || civil == null) return result;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in civil.GetSurfaceIds())
                {
                    try
                    {
                        Surface surface = transaction.GetObject(id, OpenMode.ForRead, false) as Surface;
                        if (surface != null && !string.IsNullOrWhiteSpace(surface.Name))
                            result.Add(surface.Name);
                    }
                    catch { }
                }
            }
            return result
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string ResolveSurfaceHandle(
            Database database,
            CivilDocument civil,
            string surfaceName)
        {
            if (database == null || civil == null ||
                string.IsNullOrWhiteSpace(surfaceName) ||
                string.Equals(surfaceName, "<None>", StringComparison.OrdinalIgnoreCase))
                return string.Empty;

            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in civil.GetSurfaceIds())
                {
                    try
                    {
                        Surface surface = transaction.GetObject(id, OpenMode.ForRead, false) as Surface;
                        if (surface != null &&
                            string.Equals(surface.Name, surfaceName, StringComparison.OrdinalIgnoreCase))
                            return id.Handle.ToString();
                    }
                    catch { }
                }
            }
            return string.Empty;
        }

        private static Surface OpenSurface(
            Database database,
            Transaction transaction,
            string handle)
        {
            ObjectId id = ResolveHandle(database, handle);
            if (id.IsNull || id.IsErased) return null;
            try { return transaction.GetObject(id, OpenMode.ForRead, false) as Surface; }
            catch { return null; }
        }

        private static void ApplySurfaceLevels(
            Database database,
            Transaction transaction,
            IEnumerable<GridRecord> records,
            DynamicGridLink link)
        {
            Surface ng = OpenSurface(database, transaction, link.NgSurfaceHandle);
            Surface design = OpenSurface(database, transaction, link.DesignSurfaceHandle);
            foreach (GridRecord record in records ?? Enumerable.Empty<GridRecord>())
            {
                record.NgLevel = SampleSurface(ng, record.Point);
                record.DesignLevel = SampleSurface(design, record.Point);
            }
        }

        private static double? SampleSurface(Surface surface, Point3d point)
        {
            if (surface == null) return null;
            try { return surface.FindElevationAtXY(point.X, point.Y); }
            catch { return null; }
        }

        private sealed class GridLineSpec
        {
            internal string Key;
            internal Point3d Start;
            internal Point3d End;
        }

        private static void SyncGridLines(
            Database database,
            Transaction transaction,
            IList<GridSource> sources,
            DynamicGridLink link)
        {
            List<GridLineSpec> specs = BuildGridLineSpecs(sources, link);
            var updated = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            BlockTableRecord model = OpenModelSpace(database, transaction, OpenMode.ForWrite);
            if (model == null) return;

            foreach (GridLineSpec spec in specs)
            {
                live.Add(spec.Key);
                Line line = null;
                string handle;
                if (link.LineHandles.TryGetValue(spec.Key, out handle))
                {
                    ObjectId id = ResolveHandle(database, handle);
                    if (!id.IsNull && !id.IsErased)
                    {
                        try { line = transaction.GetObject(id, OpenMode.ForWrite, false) as Line; }
                        catch { line = null; }
                    }
                }
                if (line == null)
                {
                    line = new Line(spec.Start, spec.End);
                    line.SetDatabaseDefaults(database);
                    ObjectId id = model.AppendEntity(line);
                    transaction.AddNewlyCreatedDBObject(line, true);
                }
                else
                {
                    line.StartPoint = spec.Start;
                    line.EndPoint = spec.End;
                }
                line.ColorIndex = (short)Math.Max(1, Math.Min(255, link.GridColorIndex));
                try { line.RecordGraphicsModified(true); } catch { }
                updated[spec.Key] = line.ObjectId.Handle.ToString();
            }

            foreach (KeyValuePair<string, string> stale in link.LineHandles)
            {
                if (live.Contains(stale.Key)) continue;
                EraseIfPossible(transaction, ResolveHandle(database, stale.Value));
            }
            link.LineHandles = updated;
        }

        private static List<GridLineSpec> BuildGridLineSpecs(
            IList<GridSource> sources,
            DynamicGridLink link)
        {
            var result = new List<GridLineSpec>();
            if (link == null || !link.DrawGridLines ||
                !string.Equals(link.Mode, "Full grid", StringComparison.OrdinalIgnoreCase))
                return result;

            foreach (GridSource source in sources ?? new List<GridSource>())
            {
                List<double> xs = Axis(source.MinX, source.MaxX, link.Dx);
                List<double> ys = Axis(source.MinY, source.MaxY, link.Dy);
                for (int index = 0; index < xs.Count; index++)
                {
                    List<double[]> segments = ClipVertical(source.Vertices, xs[index]);
                    for (int segment = 0; segment < segments.Count; segment++)
                    {
                        result.Add(new GridLineSpec
                        {
                            Key = "S" + source.Index + "|V|" + index + "|" + segment,
                            Start = new Point3d(xs[index], segments[segment][0], source.Z),
                            End = new Point3d(xs[index], segments[segment][1], source.Z)
                        });
                    }
                }
                for (int index = 0; index < ys.Count; index++)
                {
                    List<double[]> segments = ClipHorizontal(source.Vertices, ys[index]);
                    for (int segment = 0; segment < segments.Count; segment++)
                    {
                        result.Add(new GridLineSpec
                        {
                            Key = "S" + source.Index + "|H|" + index + "|" + segment,
                            Start = new Point3d(segments[segment][0], ys[index], source.Z),
                            End = new Point3d(segments[segment][1], ys[index], source.Z)
                        });
                    }
                }
            }
            return result;
        }

        private static List<double[]> ClipVertical(IList<Point3d> vertices, double x)
        {
            var crossings = new List<double>();
            for (int index = 0; index < vertices.Count; index++)
            {
                Point3d a = vertices[index];
                Point3d b = vertices[(index + 1) % vertices.Count];
                double dx = b.X - a.X;
                if (Math.Abs(dx) <= Tolerance)
                {
                    if (Math.Abs(x - a.X) <= Tolerance)
                    {
                        AddUnique(crossings, a.Y);
                        AddUnique(crossings, b.Y);
                    }
                    continue;
                }
                double t = (x - a.X) / dx;
                if (t < -Tolerance || t > 1.0 + Tolerance) continue;
                AddUnique(crossings, a.Y + t * (b.Y - a.Y));
            }
            crossings.Sort();
            return BuildInsideIntervals(crossings, value => InsideOrOnBoundary(vertices, x, value));
        }

        private static List<double[]> ClipHorizontal(IList<Point3d> vertices, double y)
        {
            var crossings = new List<double>();
            for (int index = 0; index < vertices.Count; index++)
            {
                Point3d a = vertices[index];
                Point3d b = vertices[(index + 1) % vertices.Count];
                double dy = b.Y - a.Y;
                if (Math.Abs(dy) <= Tolerance)
                {
                    if (Math.Abs(y - a.Y) <= Tolerance)
                    {
                        AddUnique(crossings, a.X);
                        AddUnique(crossings, b.X);
                    }
                    continue;
                }
                double t = (y - a.Y) / dy;
                if (t < -Tolerance || t > 1.0 + Tolerance) continue;
                AddUnique(crossings, a.X + t * (b.X - a.X));
            }
            crossings.Sort();
            return BuildInsideIntervals(crossings, value => InsideOrOnBoundary(vertices, value, y));
        }

        private static List<double[]> BuildInsideIntervals(
            IList<double> values,
            Func<double, bool> midpointInside)
        {
            var result = new List<double[]>();
            for (int index = 0; index + 1 < values.Count; index++)
            {
                double first = values[index];
                double second = values[index + 1];
                if (second - first <= Tolerance) continue;
                if (!midpointInside((first + second) * 0.5)) continue;
                result.Add(new[] { first, second });
            }
            return result;
        }

        private static void AddUnique(ICollection<double> values, double value)
        {
            foreach (double existing in values)
                if (Math.Abs(existing - value) <= Tolerance * 10.0)
                    return;
            values.Add(value);
        }
'@
$grid = ReplaceBetween `
    $grid `
    '        private static void PopulateTable(' `
    '        private static List<ObjectId> FindLinkedTables(' `
    $gridPopulate `
    'Grid table/level/line helpers'

# Persist the added dynamic state without invalidating pre-August-23 tables.
if (-not $grid.Contains('"DrawGridLines=" + link.DrawGridLines')) {
    $anchor = '                new TypedValue((int)DxfCode.Text, "PaperHeight=" + link.PaperHeight.ToString("R", CultureInfo.InvariantCulture))'
    if (-not $grid.Contains($anchor)) { throw 'August 23 Grid Xrecord write anchor missing.' }
    $grid = $grid.Replace($anchor,@'
                new TypedValue((int)DxfCode.Text, "PaperHeight=" + link.PaperHeight.ToString("R", CultureInfo.InvariantCulture)),
                new TypedValue((int)DxfCode.Text, "DrawGridLines=" + link.DrawGridLines.ToString(CultureInfo.InvariantCulture)),
                new TypedValue((int)DxfCode.Text, "GridColor=" + link.GridColorIndex.ToString(CultureInfo.InvariantCulture)),
                new TypedValue((int)DxfCode.Text, "NGSurface=" + (link.NgSurfaceHandle ?? string.Empty)),
                new TypedValue((int)DxfCode.Text, "DesignSurface=" + (link.DesignSurfaceHandle ?? string.Empty))
'@.TrimEnd() -replace "`r?`n","`r`n")
    $pointLoop = '            foreach (KeyValuePair<string, string> point in link.PointHandles.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))`r`n                values.Add(new TypedValue((int)DxfCode.Text, "Point=" + point.Key + "|" + point.Value));'.Replace('`r`n',"`r`n")
    if (-not $grid.Contains($pointLoop)) { throw 'August 23 Grid point-handle Xrecord loop missing.' }
    $grid = $grid.Replace($pointLoop,$pointLoop + "`r`n" + @'
            foreach (KeyValuePair<string, string> line in link.LineHandles.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
                values.Add(new TypedValue((int)DxfCode.Text, "Line=" + line.Key + "|" + line.Value));
'@.TrimEnd())
}

if (-not $grid.Contains('text.StartsWith("DrawGridLines="')) {
    $anchor = '                else if (text.StartsWith("Source=", StringComparison.OrdinalIgnoreCase))'
    if (-not $grid.Contains($anchor)) { throw 'August 23 Grid Xrecord read anchor missing.' }
    $insert = @'
                else if (text.StartsWith("DrawGridLines=", StringComparison.OrdinalIgnoreCase))
                    bool.TryParse(text.Substring(14), out link.DrawGridLines);
                else if (text.StartsWith("GridColor=", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(text.Substring(10), NumberStyles.Integer, CultureInfo.InvariantCulture, out link.GridColorIndex);
                else if (text.StartsWith("NGSurface=", StringComparison.OrdinalIgnoreCase))
                    link.NgSurfaceHandle = text.Substring(10);
                else if (text.StartsWith("DesignSurface=", StringComparison.OrdinalIgnoreCase))
                    link.DesignSurfaceHandle = text.Substring(14);
'@ -replace "`r?`n","`r`n"
    $grid = $grid.Replace($anchor,$insert + $anchor)
    $pointEnd = @'
                        link.PointHandles[body.Substring(0, separator)] = body.Substring(separator + 1);
                }
'@ -replace "`r?`n","`r`n"
    if (-not $grid.Contains($pointEnd)) { throw 'August 23 Grid point-handle Xrecord read block missing.' }
    $grid = $grid.Replace($pointEnd,$pointEnd + @'
                else if (text.StartsWith("Line=", StringComparison.OrdinalIgnoreCase))
                {
                    string body = text.Substring(5);
                    int separator = body.LastIndexOf('|');
                    if (separator > 0 && separator < body.Length - 1)
                        link.LineHandles[body.Substring(0, separator)] = body.Substring(separator + 1);
                }
'@)
}

# Extend state and row records.
if (-not $grid.Contains('internal bool DrawGridLines;')) {
    $anchor = '            internal double PaperHeight = 2.0;'
    if (-not $grid.Contains($anchor)) { throw 'August 23 Grid link field marker missing.' }
    $grid = $grid.Replace($anchor,@'
            internal double PaperHeight = 2.0;
            internal bool DrawGridLines;
            internal int GridColorIndex = 3;
            internal string NgSurfaceHandle = string.Empty;
            internal string DesignSurfaceHandle = string.Empty;
'@.TrimEnd() -replace "`r?`n","`r`n")
    $anchor = @'
            internal Dictionary<string, string> PointHandles =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
'@ -replace "`r?`n","`r`n"
    if (-not $grid.Contains($anchor)) { throw 'August 23 Grid point dictionary field marker missing.' }
    $grid = $grid.Replace($anchor,$anchor + @'
            internal Dictionary<string, string> LineHandles =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
'@)
}
if (-not $grid.Contains('internal double? NgLevel;')) {
    $anchor = '            internal Point3d Point;'
    if (-not $grid.Contains($anchor)) { throw 'August 23 Grid record field marker missing.' }
    $grid = $grid.Replace($anchor,@'
            internal Point3d Point;
            internal double? NgLevel;
            internal double? DesignLevel;
'@.TrimEnd() -replace "`r?`n","`r`n")
}

# Existing Xrecords default to safe point-only mode and colour 3.
$grid = $grid.Replace(
    '            if (!(link.PaperHeight > 0.0)) link.PaperHeight = 2.0;',
    '            if (!(link.PaperHeight > 0.0)) link.PaperHeight = 2.0;`r`n            if (link.GridColorIndex < 1 || link.GridColorIndex > 255) link.GridColorIndex = 3;'.Replace('`r`n',"`r`n"))
$grid = [regex]::Replace(
    $grid,
    'document\.Editor\.Regen\(\);(?!\s*try\s*\{\s*AcApplication\.UpdateScreen\(\);)',
    'document.Editor.Regen();`r`n            try { AcApplication.UpdateScreen(); } catch { }'.Replace('`r`n',"`r`n"))
WriteText $gridPath $grid

# -----------------------------------------------------------------------------
# 3. Multiple Dimensions: multiple FeatureLines, finite construction Lines and
#    mixed chain selection of Polyline / FeatureLine / Line / Xline sources.
# -----------------------------------------------------------------------------
$dimension = ReadText $dimensionPath
$dimension = $dimension.Replace(
    'if (featureLine != null && featureLine.GetType() == typeof(CivilFeatureLine))',
    'if (featureLine != null)')

if (-not $dimension.Contains('ProcessConstructionLine(')) {
    $featureMarker = '                        CivilFeatureLine featureLine = entity as CivilFeatureLine;'
    if (-not $dimension.Contains($featureMarker)) { throw 'August 23 Multiple Dimensions FeatureLine dispatch marker missing.' }
    $lineDispatch = @'
                        Line constructionLine = entity as Line;
                        if (constructionLine != null)
                        {
                            sources++;
                            ProcessConstructionLine(
                                document.Database,
                                transaction,
                                space,
                                constructionLine,
                                mode,
                                styleId,
                                offset,
                                ref dimensions,
                                ref skippedGeometry,
                                ref failed);
                            continue;
                        }

'@
    $dimension = $dimension.Replace($featureMarker,$lineDispatch + $featureMarker)

    $lineHelper = @'
        private static void ProcessConstructionLine(
            Database database,
            Transaction transaction,
            BlockTableRecord space,
            Line line,
            string mode,
            ObjectId styleId,
            double offset,
            ref int created,
            ref int skipped,
            ref int failed)
        {
            if (line == null) { skipped++; return; }
            Point3d start = Plan(line.StartPoint);
            Point3d end = Plan(line.EndPoint);
            if (start.DistanceTo(end) <= GeometryTolerance) { skipped++; return; }
            Point3d centroid = new Point3d(
                (start.X + end.X) * 0.5,
                (start.Y + end.Y) * 0.5,
                0.0);
            bool doAll = string.Equals(mode, "All applicable geometry", StringComparison.OrdinalIgnoreCase);
            try
            {
                if (doAll || string.Equals(mode, "Aligned - straight segments", StringComparison.OrdinalIgnoreCase))
                {
                    Point3d dimPoint = OffsetLinePoint(start, end, centroid, offset);
                    AddDimension(database, transaction, space,
                        new AlignedDimension(start, end, dimPoint, "<>", styleId), ref created);
                }
                else if (string.Equals(mode, "Linear - horizontal", StringComparison.OrdinalIgnoreCase))
                {
                    Point3d dimPoint = HorizontalDimPoint(start, end, centroid, offset);
                    AddDimension(database, transaction, space,
                        new RotatedDimension(0.0, start, end, dimPoint, "<>", styleId), ref created);
                }
                else if (string.Equals(mode, "Linear - vertical", StringComparison.OrdinalIgnoreCase))
                {
                    Point3d dimPoint = VerticalDimPoint(start, end, centroid, offset);
                    AddDimension(database, transaction, space,
                        new RotatedDimension(Math.PI * 0.5, start, end, dimPoint, "<>", styleId), ref created);
                }
                else skipped++;
            }
            catch { failed++; }
        }
'@
    $dimension = InsertOnceBefore `
        $dimension `
        '        private static void ProcessFeatureLine(' `
        $lineHelper `
        'private static void ProcessConstructionLine(' `
        'Multiple Dimensions construction-line helper'
}

if ($dimension.Contains('private static void DimensionOpenPolylineChain(')) {
$chainMethod = @'
        private static void DimensionOpenPolylineChain(
            Document document,
            PromptSelectionResult selection,
            string requestedStyle)
        {
            if (document == null || selection.Status != PromptStatus.OK ||
                selection.Value == null)
                return;

            PromptPointResult pointResult = document.Editor.GetPoint(
                "\nPick the common dimension-line location for the selected chain sources: ");
            if (pointResult.Status != PromptStatus.OK) return;

            Point3d dimensionLinePoint = pointResult.Value.TransformBy(
                document.Editor.CurrentUserCoordinateSystem);
            int selectedCount = selection.Value.Count;
            int acceptedCount = 0;
            int skippedCount = 0;
            int created = 0;
            string outputStyleName = string.Empty;

            try
            {
                using (Transaction transaction =
                    document.Database.TransactionManager.StartTransaction())
                {
                    ObjectId styleId = EnsureAnnotativeDimensionStyle(
                        document.Database,
                        transaction,
                        requestedStyle,
                        out outputStyleName);
                    if (styleId.IsNull)
                    {
                        document.Editor.WriteMessage(
                            "\nCE_MULTIDIM chain stopped. The selected dimension style could not be prepared.");
                        return;
                    }

                    var candidates = new List<ChainCandidate>();
                    foreach (ObjectId id in selection.Value.GetObjectIds().Distinct())
                    {
                        Entity source;
                        try
                        {
                            source = transaction.GetObject(
                                id,
                                OpenMode.ForRead,
                                false) as Entity;
                        }
                        catch
                        {
                            skippedCount++;
                            continue;
                        }

                        ChainCandidate candidate;
                        if (source == null ||
                            !TryBuildChainCandidate(
                                source,
                                dimensionLinePoint,
                                out candidate))
                        {
                            skippedCount++;
                            continue;
                        }
                        candidates.Add(candidate);
                    }

                    if (candidates.Count < 2)
                    {
                        document.Editor.WriteMessage(
                            "\nCE_MULTIDIM chain stopped. Select at least two usable polylines, FeatureLines or construction lines/XLINEs.");
                        return;
                    }

                    Vector3d reference = candidates[0].Direction.GetNormal();
                    Vector3d sum = new Vector3d(0.0, 0.0, 0.0);
                    var accepted = new List<ChainCandidate>();
                    double minimumParallelDot = Math.Cos(15.0 * Math.PI / 180.0);

                    foreach (ChainCandidate candidate in candidates)
                    {
                        Vector3d direction = candidate.Direction.GetNormal();
                        double dot = direction.DotProduct(reference);
                        if (Math.Abs(dot) < minimumParallelDot)
                        {
                            skippedCount++;
                            continue;
                        }
                        if (dot < 0.0) direction = -direction;
                        candidate.Direction = direction;
                        sum += direction;
                        accepted.Add(candidate);
                    }

                    if (accepted.Count < 2)
                    {
                        document.Editor.WriteMessage(
                            "\nCE_MULTIDIM chain stopped. Fewer than two selected sources are approximately parallel.");
                        return;
                    }

                    Vector3d commonDirection = sum.Length > GeometryTolerance
                        ? sum.GetNormal()
                        : reference;
                    Vector3d dimensionAxis = new Vector3d(
                        -commonDirection.Y,
                        commonDirection.X,
                        0.0);
                    if (dimensionAxis.Length <= GeometryTolerance)
                    {
                        document.Editor.WriteMessage(
                            "\nCE_MULTIDIM chain stopped. A common plan direction could not be resolved.");
                        return;
                    }
                    dimensionAxis = dimensionAxis.GetNormal();

                    foreach (ChainCandidate candidate in accepted)
                    {
                        candidate.Coordinate =
                            candidate.Anchor.X * dimensionAxis.X +
                            candidate.Anchor.Y * dimensionAxis.Y;
                    }
                    accepted = accepted
                        .OrderBy(item => item.Coordinate)
                        .ToList();
                    acceptedCount = accepted.Count;

                    BlockTableRecord space = transaction.GetObject(
                        document.Database.CurrentSpaceId,
                        OpenMode.ForWrite,
                        false) as BlockTableRecord;
                    if (space == null) return;

                    double rotation = Math.Atan2(
                        dimensionAxis.Y,
                        dimensionAxis.X);
                    for (int index = 0; index < accepted.Count - 1; index++)
                    {
                        ChainCandidate first = accepted[index];
                        ChainCandidate second = accepted[index + 1];
                        if (Math.Abs(second.Coordinate - first.Coordinate) <= GeometryTolerance)
                        {
                            skippedCount++;
                            continue;
                        }

                        var dimension = new RotatedDimension(
                            rotation,
                            Plan(first.Anchor),
                            Plan(second.Anchor),
                            Plan(dimensionLinePoint),
                            "<>",
                            styleId);
                        AddDimension(
                            document.Database,
                            transaction,
                            space,
                            dimension,
                            ref created);
                    }

                    transaction.Commit();
                }
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_MULTIDIM chain stopped. {0}",
                    exception.Message);
                return;
            }

            document.Editor.Regen();
            try { AcApplication.UpdateScreen(); } catch { }
            document.Editor.WriteMessage(
                "\nCE_MULTIDIM chain complete. Selected={0}; parallel sources={1}; dimensions={2}; skipped={3}; style={4}.",
                selectedCount,
                acceptedCount,
                created,
                skippedCount,
                outputStyleName);
        }
'@
    $dimension = ReplaceBetween `
        $dimension `
        '        private static void DimensionOpenPolylineChain(' `
        '        private static bool TryBuildChainCandidate(' `
        $chainMethod `
        'Multiple Dimensions mixed chain method'

$chainCandidate = @'
        private static bool TryBuildChainCandidate(
            Entity source,
            Point3d dimensionLinePoint,
            out ChainCandidate candidate)
        {
            candidate = null;
            if (source == null) return false;

            Point3d start;
            Point3d end;
            Point3d anchor;
            Polyline polyline = source as Polyline;
            Line line = source as Line;
            Xline xline = source as Xline;
            CivilFeatureLine featureLine = source as CivilFeatureLine;
            try
            {
                if (polyline != null)
                {
                    if (polyline.Closed || polyline.NumberOfVertices < 2) return false;
                    start = Plan(polyline.StartPoint);
                    end = Plan(polyline.EndPoint);
                    anchor = Plan(polyline.GetClosestPointTo(dimensionLinePoint, false));
                }
                else if (line != null)
                {
                    start = Plan(line.StartPoint);
                    end = Plan(line.EndPoint);
                    anchor = Plan(line.GetClosestPointTo(dimensionLinePoint, false));
                }
                else if (xline != null)
                {
                    start = Plan(xline.BasePoint);
                    Vector3d unit = new Vector3d(xline.UnitDir.X, xline.UnitDir.Y, 0.0);
                    if (unit.Length <= GeometryTolerance) return false;
                    end = start + unit.GetNormal();
                    anchor = Plan(xline.GetClosestPointTo(dimensionLinePoint, false));
                }
                else if (featureLine != null)
                {
                    Point3dCollection points = featureLine.GetPoints(FeatureLinePointType.AllPoints);
                    if (points == null || points.Count < 2) return false;
                    start = Plan(points[0]);
                    end = Plan(points[points.Count - 1]);
                    if (start.DistanceTo(end) <= GeometryTolerance)
                    {
                        double longest = 0.0;
                        for (int index = 0; index + 1 < points.Count; index++)
                        {
                            Point3d first = Plan(points[index]);
                            Point3d second = Plan(points[index + 1]);
                            double length = first.DistanceTo(second);
                            if (length > longest)
                            {
                                longest = length;
                                start = first;
                                end = second;
                            }
                        }
                    }
                    anchor = Plan(points[0]);
                    double best = double.MaxValue;
                    for (int index = 0; index < points.Count; index++)
                    {
                        Point3d point = Plan(points[index]);
                        double distance = point.DistanceTo(Plan(dimensionLinePoint));
                        if (distance < best)
                        {
                            best = distance;
                            anchor = point;
                        }
                    }
                }
                else
                {
                    return false;
                }
            }
            catch
            {
                return false;
            }

            Vector3d direction = end - start;
            if (direction.Length <= GeometryTolerance) return false;
            candidate = new ChainCandidate
            {
                Anchor = anchor,
                Direction = direction.GetNormal()
            };
            return true;
        }
'@
    $dimension = ReplaceBetween `
        $dimension `
        '        private static bool TryBuildChainCandidate(' `
        '        private static void ProcessPolyline(' `
        $chainCandidate `
        'Multiple Dimensions mixed chain candidate'
}
WriteText $dimensionPath $dimension

# -----------------------------------------------------------------------------
# 4. CAD Production remains grouped into folders and is now reachable directly
#    from both production-centre surfaces.
# -----------------------------------------------------------------------------
$production = ReadText $productionPath
if (-not $production.Contains('"CAD PRODUCTION", "CE_CADPRODUCTION"')) {
    $marker = '                    Action("Engineering Intelligence",'
    if (-not $production.Contains($marker)) { throw 'August 23 Production Centre CAD insertion marker missing.' }
    $production = $production.Replace($marker,@'
                    Action("CAD PRODUCTION", "CE_CADPRODUCTION", "Open grouped CAD production tools.", "02 DISCIPLINE PRODUCTION"),
                    Action("Engineering Intelligence",
'@.TrimEnd() -replace "`r?`n","`r`n")
}
WriteText $productionPath $production

$productionV3 = ReadText $productionV3Path
if (-not $productionV3.Contains('"CAD PRODUCTION", "CE_CADPRODUCTION"')) {
    $marker = '                    A("Engineering Intelligence",'
    if (-not $productionV3.Contains($marker)) { throw 'August 23 Production V3 CAD insertion marker missing.' }
    $productionV3 = $productionV3.Replace($marker,@'
                    A("CAD PRODUCTION", "CE_CADPRODUCTION", "Open grouped CAD production tools.", "02 DISCIPLINE PRODUCTION"),
                    A("Engineering Intelligence",
'@.TrimEnd() -replace "`r?`n","`r`n")
}
WriteText $productionV3Path $productionV3

# -----------------------------------------------------------------------------
# Final semantic guards. These mirror the exact tester requirements so a future
# historical repair cannot silently regress the final compile-stage source.
# -----------------------------------------------------------------------------
$vertexCheck = ReadText $vertexPath
foreach ($required in @(
    'ResetCogoLabel(point);',
    'ResetCogoLabel(cogo);',
    'SetDimensionDoubleOverride(dimension, "Dimasz", 3.0);',
    'SetDimensionDoubleOverride(dimension, "Dimtxt", 1.8);',
    'AcApplication.UpdateScreen()')) {
    if (-not $vertexCheck.Contains($required)) { throw "August 23 Vertex Setting-Out guard missing: $required" }
}

$gridCheck = ReadText $gridPath
foreach ($required in @(
    '"Points + full grid lines"',
    'GridColorIndex',
    '"NG LEVEL"',
    '"DESIGN LEVEL"',
    'FindElevationAtXY',
    'LineHandles',
    'SyncGridLines(',
    'ResetCogoLabel(point);')) {
    if (-not $gridCheck.Contains($required)) { throw "August 23 Dynamic Grid guard missing: $required" }
}
if ($gridCheck.Contains('"POINT", "SOURCE", "X", "Y", "Z"')) {
    throw 'August 23 Dynamic Grid still exposes the old Z table column.'
}

$dimensionCheck = ReadText $dimensionPath
foreach ($required in @(
    'ProcessConstructionLine(',
    'TryBuildChainCandidate(',
    'Entity source,',
    'Xline xline = source as Xline;',
    'CivilFeatureLine featureLine = source as CivilFeatureLine;')) {
    if (-not $dimensionCheck.Contains($required)) { throw "August 23 Multiple Dimensions guard missing: $required" }
}
if ($dimensionCheck.Contains('featureLine.GetType() == typeof(CivilFeatureLine)')) {
    throw 'August 23 Multiple Dimensions still rejects compatible/derived FeatureLines.'
}

foreach ($path in @($productionPath,$productionV3Path)) {
    $check = ReadText $path
    if (-not $check.Contains('"CAD PRODUCTION", "CE_CADPRODUCTION"')) {
        throw "August 23 Production Centre CAD route missing: $path"
    }
}

Write-Host 'August 23 Setting-Out / Multiple Dimensions / CAD Production field feedback applied.' -ForegroundColor Green
Write-Host 'Vertex/grid graphics, radial sizes, surface-level tables, dynamic grid lines, mixed dimension sources and CAD centre routing are normalized.' -ForegroundColor Green

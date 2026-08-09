from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "src" / "CE.Tools.Civil3D"

def read(name):
    return (SRC / name).read_text(encoding="utf-8")

def write(name, text):
    (SRC / name).write_text(text, encoding="utf-8", newline="\n")

def replace_once(text, old, new, label):
    if old not in text:
        raise RuntimeError(f"marker not found: {label}")
    return text.replace(old, new, 1)

def replace_all_required(text, old, new, label):
    count = text.count(old)
    if count == 0:
        raise RuntimeError(f"marker not found: {label}")
    return text.replace(old, new), count

# 1. Production settings: global defaults first, DWG overrides second, save both.
name = "DisciplineWorkflowDialogs.cs"
text = read(name)
old = '''            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document != null)
                ProductionSettingsPersistenceStore.Load(document.Database, model);
            var window = new ProductionSettingsWindow(model);
            AcApplication.ShowModalWindow(window);
            if (window.Accepted && document != null)
                ProductionSettingsPersistenceStore.Save(document.Database, model);
            return window.Accepted;'''
new = '''            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            // User-local defaults are loaded first so Road/Sewer/SW/Water/Bulk
            // Water settings remain available when another DWG is opened.
            CrossDrawingProductionSettingsStore.Load(model);
            // A drawing can still keep its own override of the shared defaults.
            if (document != null)
                ProductionSettingsPersistenceStore.Load(document.Database, model);
            var window = new ProductionSettingsWindow(model);
            AcApplication.ShowModalWindow(window);
            if (window.Accepted)
            {
                if (document != null)
                    ProductionSettingsPersistenceStore.Save(document.Database, model);
                CrossDrawingProductionSettingsStore.Save(model);
            }
            return window.Accepted;'''
text = replace_once(text, old, new, "cross-drawing popup persistence")
write(name, text)

# 2. Project Setup: infer the preferred LO display value from Town when blank.
name = "ProjectSetupCommands.cs"
text = read(name)
old = '''            var proposed = new ProjectMetadata();
            foreach (string field in FieldOrder)
                proposed.Set(field, window.GetValue(field));'''
new = '''            var proposed = new ProjectMetadata();
            foreach (string field in FieldOrder)
            {
                string value = window.GetValue(field);
                if (string.Equals(field, "Coordinate System", StringComparison.OrdinalIgnoreCase) &&
                    string.IsNullOrWhiteSpace(value))
                {
                    string preferred = NamibiaCoordinateSystemCatalog.PreferredLoName(
                        window.GetValue("Town"));
                    if (!string.IsNullOrWhiteSpace(preferred)) value = preferred;
                }
                proposed.Set(field, value);
            }'''
text = replace_once(text, old, new, "project town coordinate default")
write(name, text)

# 3. Existing WGS84 map popup: add true DWG/WGS84 conversion choices.
name = "ProjectCoordinationCommands.cs"
text = read(name)
old = '''            model.AddChoice("Action", "03 Action", "Action", "Open WGS84 in map", "Open the WGS84 point or convert the survey/drawing coordinate labels without changing geometry.", new[] { "Open WGS84 in map", "Northing / Easting -> Y / X", "Y / X -> Northing / Easting" });'''
new = '''            model.AddChoice("Action", "03 Action", "Action", "Open WGS84 in map", "Open the WGS84 point, convert survey/drawing labels, or transform between drawing XY and WGS84 using the GeoLocationData stored in this DWG.", new[] { "Open WGS84 in map", "Northing / Easting -> Y / X", "Y / X -> Northing / Easting", "Drawing X / Y -> WGS84 Lat / Long", "WGS84 Lat / Long -> Drawing X / Y" });'''
text = replace_once(text, old, new, "map action choices")
old = '''            string action = model.Text("Action");
            if (!string.Equals(action, "Open WGS84 in map", StringComparison.OrdinalIgnoreCase))
            {
                double first;
                double second;
                bool neToYx = action.StartsWith("Northing", StringComparison.OrdinalIgnoreCase);
                string firstKey = neToYx ? "Northing" : "Y";
                string secondKey = neToYx ? "Easting" : "X";
                if (!TryParseSignedNumber(model.Text(firstKey), out first) || !TryParseSignedNumber(model.Text(secondKey), out second))
                {
                    document.Editor.WriteMessage("\\nCE_MAPLOCATION stopped. Enter valid signed coordinate values for the selected conversion.");
                    return;
                }
                string result = neToYx
                    ? string.Format(CultureInfo.CurrentCulture, "Northing {0:N3} -> Y {0:N3}\\nEasting {1:N3} -> X {1:N3}", first, second)
                    : string.Format(CultureInfo.CurrentCulture, "Y {0:N3} -> Northing {0:N3}\\nX {1:N3} -> Easting {1:N3}", first, second);
                MessageBox.Show(result, "CE Tools - NE / YX Conversion", MessageBoxButton.OK, MessageBoxImage.Information);
                document.Editor.WriteMessage("\\nCE_MAPLOCATION coordinate conversion: {0}", result.Replace("\\n", "; "));
                return;
            }'''
new = '''            string action = model.Text("Action");
            if (string.Equals(action, "Drawing X / Y -> WGS84 Lat / Long", StringComparison.OrdinalIgnoreCase))
            {
                double xValue;
                double yValue;
                if (!TryParseSignedNumber(model.Text("X"), out xValue) ||
                    !TryParseSignedNumber(model.Text("Y"), out yValue))
                {
                    document.Editor.WriteMessage("\\nCE_MAPLOCATION stopped. Enter valid Drawing X and Y values.");
                    return;
                }
                Point3d geo;
                string transformError;
                if (!GeoCoordinateTransform.TryDrawingToWgs84(
                        document.Database,
                        new Point3d(xValue, yValue, 0.0),
                        out geo,
                        out transformError))
                {
                    document.Editor.WriteMessage("\\nCE_MAPLOCATION transformation stopped. {0}", transformError);
                    return;
                }
                string result = string.Format(
                    CultureInfo.CurrentCulture,
                    "Drawing X {0:N3} / Y {1:N3}\\nLatitude {2:0.00000000}\\nLongitude {3:0.00000000}",
                    xValue,
                    yValue,
                    geo.Y,
                    geo.X);
                MessageBox.Show(result, "CE Tools - Drawing XY / WGS84", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (string.Equals(action, "WGS84 Lat / Long -> Drawing X / Y", StringComparison.OrdinalIgnoreCase))
            {
                double transformLatitude;
                double transformLongitude;
                if (!TryParseCoordinate(model.Text("Latitude"), -90.0, 90.0, out transformLatitude) ||
                    !TryParseCoordinate(model.Text("Longitude"), -180.0, 180.0, out transformLongitude))
                {
                    document.Editor.WriteMessage("\\nCE_MAPLOCATION stopped. Enter valid WGS84 latitude/longitude.");
                    return;
                }
                Point3d drawingPoint;
                string transformError;
                if (!GeoCoordinateTransform.TryWgs84ToDrawing(
                        document.Database,
                        transformLatitude,
                        transformLongitude,
                        0.0,
                        out drawingPoint,
                        out transformError))
                {
                    document.Editor.WriteMessage("\\nCE_MAPLOCATION transformation stopped. {0}", transformError);
                    return;
                }
                string result = string.Format(
                    CultureInfo.CurrentCulture,
                    "Latitude {0:0.00000000} / Longitude {1:0.00000000}\\nDrawing X {2:N3}\\nDrawing Y {3:N3}",
                    transformLatitude,
                    transformLongitude,
                    drawingPoint.X,
                    drawingPoint.Y);
                MessageBox.Show(result, "CE Tools - WGS84 / Drawing XY", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (!string.Equals(action, "Open WGS84 in map", StringComparison.OrdinalIgnoreCase))
            {
                double first;
                double second;
                bool neToYx = action.StartsWith("Northing", StringComparison.OrdinalIgnoreCase);
                string firstKey = neToYx ? "Northing" : "Y";
                string secondKey = neToYx ? "Easting" : "X";
                if (!TryParseSignedNumber(model.Text(firstKey), out first) || !TryParseSignedNumber(model.Text(secondKey), out second))
                {
                    document.Editor.WriteMessage("\\nCE_MAPLOCATION stopped. Enter valid signed coordinate values for the selected conversion.");
                    return;
                }
                string result = neToYx
                    ? string.Format(CultureInfo.CurrentCulture, "Northing {0:N3} -> Y {0:N3}\\nEasting {1:N3} -> X {1:N3}", first, second)
                    : string.Format(CultureInfo.CurrentCulture, "Y {0:N3} -> Northing {0:N3}\\nX {1:N3} -> Easting {1:N3}", first, second);
                MessageBox.Show(result, "CE Tools - NE / YX Conversion", MessageBoxButton.OK, MessageBoxImage.Information);
                document.Editor.WriteMessage("\\nCE_MAPLOCATION coordinate conversion: {0}", result.Replace("\\n", "; "));
                return;
            }'''
text = replace_once(text, old, new, "map geodetic runtime")
write(name, text)

# 4. Vertex setting-out: separate NG and Design surfaces, remove redundant Z table column.
name = "VertexSettingOutCommands.cs"
text = read(name)
old = '''            settings.AddChoice(
                "NGSurface", "01 Output", "Existing / NG level surface", "<None>",
                "Optional existing-ground surface used for NG Level and Difference columns. It does not change X/Y or the design point elevation.",
                ngSurfaceChoices);'''
new = '''            settings.AddChoice(
                "NGSurface", "01 Output", "Existing / NG level surface", "<None>",
                "Optional existing-ground/base surface used for the NG Level column. It never changes X/Y.",
                ngSurfaceChoices);
            settings.AddChoice(
                "DesignSurface", "01 Output", "Design / comparison level surface", "<Use setting-out point elevation>",
                "Optional design/comparison surface. When selected, Design Level is sampled independently and Difference = Design - NG.",
                new[] { "<Use setting-out point elevation>" }.Concat(surfaceChoices).Distinct(StringComparer.OrdinalIgnoreCase));'''
text = replace_once(text, old, new, "design surface popup")
text = replace_once(text,
'''            string ngSurface = settings.Text("NGSurface");''',
'''            string ngSurface = settings.Text("NGSurface");
            string designSurface = settings.Text("DesignSurface");''',
"read design surface")
old = '''            AnnotationOptions annotation;
            if (!AnnotationSettingsStore.Prepare(document, false, out annotation)) return;'''
new = '''            ObjectId designSurfaceId = ObjectId.Null;
            if (!string.IsNullOrWhiteSpace(designSurface) &&
                !string.Equals(designSurface, "<Use setting-out point elevation>", StringComparison.OrdinalIgnoreCase))
            {
                if (!PromptElevationSource(
                        document,
                        civilDocument,
                        "Select Civil 3D surface",
                        designSurface,
                        out designSurfaceId)) return;
            }

            AnnotationOptions annotation;
            if (!AnnotationSettingsStore.Prepare(document, false, out annotation)) return;'''
text = replace_once(text, old, new, "resolve design surface")
text = replace_once(text,
'''            ApplyLevelReferences(document.Database, sources, ngSurfaceId);''',
'''            ApplyLevelReferences(document.Database, sources, ngSurfaceId, designSurfaceId);''',
"initial level references")
text = replace_once(text,
'''                NgSurfaceHandle = ngSurfaceId.IsNull
                    ? string.Empty
                    : ngSurfaceId.Handle.ToString(),
                SourceHandles = linkedHandles''',
'''                NgSurfaceHandle = ngSurfaceId.IsNull
                    ? string.Empty
                    : ngSurfaceId.Handle.ToString(),
                DesignSurfaceHandle = designSurfaceId.IsNull
                    ? string.Empty
                    : designSurfaceId.Handle.ToString(),
                SourceHandles = linkedHandles''',
"link design surface")
text = replace_once(text,
'''                ApplyLevelReferences(
                    document.Database,
                    sources,
                    ResolveHandle(document.Database, link.NgSurfaceHandle));''',
'''                ApplyLevelReferences(
                    document.Database,
                    sources,
                    ResolveHandle(document.Database, link.NgSurfaceHandle),
                    ResolveHandle(document.Database, link.DesignSurfaceHandle));''',
"refresh level references")

# Replace ApplyLevelReferences method body/signature by regex.
pattern = re.compile(r'''        private static void ApplyLevelReferences\(\n            Database database,\n            IEnumerable<VertexSettingSource> sources,\n            ObjectId ngSurfaceId\)\n        \{.*?\n        \}\n\n        private static''', re.S)
match = pattern.search(text)
if not match:
    raise RuntimeError("marker not found: ApplyLevelReferences method")
replacement = '''        private static void ApplyLevelReferences(
            Database database,
            IEnumerable<VertexSettingSource> sources,
            ObjectId ngSurfaceId,
            ObjectId designSurfaceId)
        {
            Autodesk.Civil.DatabaseServices.Surface ngSurface = null;
            Autodesk.Civil.DatabaseServices.Surface designSurface = null;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                if (!ngSurfaceId.IsNull && !ngSurfaceId.IsErased)
                    ngSurface = transaction.GetObject(ngSurfaceId, OpenMode.ForRead, false) as Autodesk.Civil.DatabaseServices.Surface;
                if (!designSurfaceId.IsNull && !designSurfaceId.IsErased)
                    designSurface = transaction.GetObject(designSurfaceId, OpenMode.ForRead, false) as Autodesk.Civil.DatabaseServices.Surface;

                foreach (VertexSettingRecord record in (sources ?? Enumerable.Empty<VertexSettingSource>()).SelectMany(item => item.Records))
                {
                    record.NgLevel = SampleSurfaceLevel(ngSurface, record.Point);
                    double sampledDesign = SampleSurfaceLevel(designSurface, record.Point);
                    record.DesignLevel = double.IsNaN(sampledDesign) ? record.Point.Z : sampledDesign;
                }
            }
        }

        private static'''
text = text[:match.start()] + replacement + text[match.end():]

# Add DesignSurfaceHandle property next to NgSurfaceHandle.
text = replace_once(text,
'''        public string NgSurfaceHandle { get; set; }
        public IList<string> SourceHandles''',
'''        public string NgSurfaceHandle { get; set; }
        public string DesignSurfaceHandle { get; set; }
        public IList<string> SourceHandles''',
"design surface link property")

# Serialize/deserialize design handle using the same key/value Xrecord contract.
old = '''                "NGSURFACE=" + (link.NgSurfaceHandle ?? string.Empty),'''
new = '''                "NGSURFACE=" + (link.NgSurfaceHandle ?? string.Empty),
                "DESIGNSURFACE=" + (link.DesignSurfaceHandle ?? string.Empty),'''
text = replace_once(text, old, new, "write design surface link")
old = '''                NgSurfaceHandle = ReadLinkValue(values, "NGSURFACE"),'''
new = '''                NgSurfaceHandle = ReadLinkValue(values, "NGSURFACE"),
                DesignSurfaceHandle = ReadLinkValue(values, "DESIGNSURFACE"),'''
text = replace_once(text, old, new, "read design surface link")

# Remove Z column from linked setting-out table while keeping NG/Design/Difference.
# Current table has 12 columns and known heading strings.
text = replace_once(text, '''            table.SetSize(records.Count + 2, 12);''', '''            table.SetSize(records.Count + 2, 11);''', "vertex table column count")
text = replace_once(text,
'''                "POINT NAME", "TYPE", "SOURCE", "SEGMENT", xHeading, yHeading, "Z", "NG LEVEL", "DESIGN LEVEL", "DIFFERENCE", "RADIUS", "SEGMENT LENGTH"''',
'''                "POINT NAME", "TYPE", "SOURCE", "SEGMENT", xHeading, yHeading, "NG LEVEL", "DESIGN LEVEL", "DIFFERENCE", "RADIUS", "SEGMENT LENGTH"''',
"vertex table headings")
# Replace value array with no Z when exact current sequence exists.
text = replace_once(text,
'''                    DisplayCoordinate(record.Point.X, link.XSign),
                    DisplayCoordinate(record.Point.Y, link.YSign),
                    record.Point.Z.ToString("0.000", CultureInfo.CurrentCulture),
                    LevelText(record.NgLevel),
                    LevelText(record.DesignLevel),
                    DifferenceText(record.NgLevel, record.DesignLevel),
                    record.Radius > 0.0 ? record.Radius.ToString("0.000", CultureInfo.CurrentCulture) : string.Empty,
                    record.SegmentLength > 0.0 ? record.SegmentLength.ToString("0.000", CultureInfo.CurrentCulture) : string.Empty''',
'''                    DisplayCoordinate(record.Point.X, link.XSign),
                    DisplayCoordinate(record.Point.Y, link.YSign),
                    LevelText(record.NgLevel),
                    LevelText(record.DesignLevel),
                    DifferenceText(record.NgLevel, record.DesignLevel),
                    record.Radius > 0.0 ? record.Radius.ToString("0.000", CultureInfo.CurrentCulture) : string.Empty,
                    record.SegmentLength > 0.0 ? record.SegmentLength.ToString("0.000", CultureInfo.CurrentCulture) : string.Empty''',
"vertex table row values")
write(name, text)

# 5. Sewer excavation: physical endpoints before suspicious host properties,
# nominal diameter mm presentation, all cells centered, force table graphics.
name = "SewerExcavationCommentCommands.cs"
text = read(name)
# Replace TryGetLength function fully.
pattern = re.compile(r'''        private static bool TryGetLength\(object value, out double length\)\n        \{.*?\n        \}\n\n        private static bool LooksLikePipe''', re.S)
match = pattern.search(text)
if not match:
    raise RuntimeError("marker not found: sewer excavation TryGetLength")
replacement = '''        private static bool TryGetLength(object value, out double length)
        {
            length = 0.0;
            // Civil 3D network objects can expose a stale 1.000 length. Prefer
            // actual endpoint geometry first so excavation and BOQs use the
            // physical pipe distance.
            string[,] pairs =
            {
                { "StartPoint", "EndPoint" },
                { "StartPointLocation", "EndPointLocation" },
                { "StartLocation", "EndLocation" }
            };
            for (int index = 0; index < pairs.GetLength(0); index++)
            {
                Point3d start;
                Point3d end;
                if (TryReadPoint(value, pairs[index, 0], out start) &&
                    TryReadPoint(value, pairs[index, 1], out end))
                {
                    double endpointLength = start.DistanceTo(end);
                    if (Positive(endpointLength) && Math.Abs(endpointLength - 1.0) > 0.0001)
                    {
                        length = endpointLength;
                        return true;
                    }
                }
            }
            Curve curve = value as Curve;
            if (curve != null)
            {
                try
                {
                    length = Math.Abs(
                        curve.GetDistanceAtParameter(curve.EndParam) -
                        curve.GetDistanceAtParameter(curve.StartParam));
                    if (Positive(length) && Math.Abs(length - 1.0) > 0.0001) return true;
                }
                catch { }
            }
            if (TryReadNumber(
                value,
                out length,
                "Length3DCenterToCenter",
                "Length2DCenterToCenter",
                "Length3D",
                "Length2D",
                "Length"))
            {
                if (Positive(length) && Math.Abs(length - 1.0) > 0.0001) return true;
            }
            // Final fallback: accept any positive endpoint length rather than
            // silently forcing every network part to exactly 1 m.
            for (int index = 0; index < pairs.GetLength(0); index++)
            {
                Point3d start;
                Point3d end;
                if (TryReadPoint(value, pairs[index, 0], out start) &&
                    TryReadPoint(value, pairs[index, 1], out end))
                {
                    length = start.DistanceTo(end);
                    if (Positive(length)) return true;
                }
            }
            return Positive(length);
        }

        private static bool LooksLikePipe'''
text = text[:match.start()] + replacement + text[match.end():]
# Add nominal helper before PopulateTable.
marker = '''        private static void PopulateTable(
            Database database,'''
helper = '''        private static int NominalDiameterMm(double diameterMetres)
        {
            double millimetres = diameterMetres > 10.0 ? diameterMetres : diameterMetres * 1000.0;
            int[] nominal = { 50, 63, 75, 90, 100, 110, 125, 140, 160, 180, 200, 225, 250, 280, 300, 315, 355, 400, 450, 500, 560, 600, 630, 710, 800, 900, 1000, 1200, 1500 };
            return nominal.OrderBy(value => Math.Abs(value - millimetres)).First();
        }

'''
if marker not in text:
    raise RuntimeError("marker not found: excavation PopulateTable")
text = text.replace(marker, helper + marker, 1)
text = replace_once(text, '"PIPE", "LAYER", "LENGTH m", "DIAMETER m", "AVG COVER m",', '"PIPE", "LAYER", "LENGTH m", "NOMINAL Ø mm", "AVG COVER m",', "excavation diameter heading")
text = replace_once(text,
'''                    row.Diameter.ToString("N3", CultureInfo.CurrentCulture),''',
'''                    NominalDiameterMm(row.Diameter).ToString(CultureInfo.CurrentCulture),''',
"excavation nominal diameter value")
text = replace_once(text,
'''                    table.Cells[tableRow, column].Alignment = column < 2
                        ? CellAlignment.MiddleLeft
                        : CellAlignment.MiddleCenter;''',
'''                    table.Cells[tableRow, column].Alignment = CellAlignment.MiddleCenter;''',
"excavation all cells centered")
# Force graphics after PopulateTable where linked table created.
text = replace_once(text,
'''                PopulateTable(database, table, rows, settings);
                ObjectId tableId = currentSpace.AppendEntity(table);''',
'''                PopulateTable(database, table, rows, settings);
                table.GenerateLayout();
                table.RecordGraphicsModified(true);
                try { table.RecomputeTableBlock(true); } catch { }
                ObjectId tableId = currentSpace.AppendEntity(table);''',
"excavation table graphics")
write(name, text)

# 6. Surface spike/low detection: nearest-neighbour fallback when radius yields
# too few points, so isolated high/low vertices are still screened.
name = "SurfaceSpikeHoleRepairCommands.cs"
text = read(name)
old = '''                if (neighbours.Count < minimumNeighbours) continue;
                double median = Median(neighbours.Select(item => vertices[item].Z));'''
new = '''                if (neighbours.Count < minimumNeighbours)
                {
                    neighbours = Enumerable.Range(0, vertices.Count)
                        .Where(item => item != index)
                        .OrderBy(item => PlanDistanceSquared(vertices[index], vertices[item]))
                        .Take(Math.Max(minimumNeighbours, 8))
                        .ToList();
                }
                if (neighbours.Count < Math.Min(3, minimumNeighbours)) continue;
                double median = Median(neighbours.Select(item => vertices[item].Z));'''
text = replace_once(text, old, new, "surface spike nearest-neighbour fallback")
write(name, text)

# 7. Floating command centre: Ctrl+F at WPF input-manager level, overall tab,
# books in every workflow, CE-prefixed visible card names.
name = "FloatingToolsWindow.cs"
text = read(name)
# Global input hook.
text = replace_once(text,
'''            _shortcutTarget.PreviewKeyDown += OnApplicationPreviewKeyDown;
            _shortcutAttached = true;''',
'''            _shortcutTarget.PreviewKeyDown += OnApplicationPreviewKeyDown;
            try { InputManager.Current.PreProcessInput += OnGlobalPreProcessInput; } catch { }
            _shortcutAttached = true;''',
"global ctrl-f attach")
text = replace_once(text,
'''            _shortcutTarget.PreviewKeyDown -= OnApplicationPreviewKeyDown;
            _shortcutTarget = null;''',
'''            _shortcutTarget.PreviewKeyDown -= OnApplicationPreviewKeyDown;
            try { InputManager.Current.PreProcessInput -= OnGlobalPreProcessInput; } catch { }
            _shortcutTarget = null;''',
"global ctrl-f detach")
marker = '''        private static void OnApplicationPreviewKeyDown(object sender, KeyEventArgs args)
        {'''
insert = '''        private static void OnGlobalPreProcessInput(object sender, PreProcessInputEventArgs args)
        {
            KeyEventArgs key = args == null ? null : args.StagingItem.Input as KeyEventArgs;
            if (key == null || !key.IsDown || key.Key != Key.F ||
                (Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
                return;
            key.Handled = true;
            ShowWindow();
        }

'''
if marker not in text:
    raise RuntimeError("marker not found: ctrl-f method")
text = text.replace(marker, insert + marker, 1)
# Overall usage tab.
text = replace_once(text,
'''            AddUsageTab("favorites", "⭐ Favorites");
            AddUsageTab("mostused", "🔥 Most Used");''',
'''            AddUsageTab("favorites", "⭐ Favorites");
            AddUsageTab("overallmostused", "🔥 Overall Most Used");
            AddUsageTab("mostused", "🔥 Drawing Most Used");''',
"overall usage tab")
text = replace_once(text,
'''            _buttons.RemoveAll(item =>
                string.Equals(item.WorkflowKey, "favorites", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.WorkflowKey, "mostused", StringComparison.OrdinalIgnoreCase) ||''',
'''            _buttons.RemoveAll(item =>
                string.Equals(item.WorkflowKey, "favorites", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.WorkflowKey, "overallmostused", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.WorkflowKey, "mostused", StringComparison.OrdinalIgnoreCase) ||''',
"clear overall usage buttons")
text = replace_once(text,
'''            BuildUsageTab("favorites", CommandUsageTracker.Favorites(projectKey),
                "Right-click any command and choose Add to Favorites.");
            BuildUsageTab("mostused", CommandUsageTracker.MostUsed(projectKey, 24),''',
'''            BuildUsageTab("favorites", CommandUsageTracker.Favorites(projectKey),
                "Right-click any command and choose Add to Favorites.");
            BuildUsageTab("overallmostused", CommandUsageTracker.MostUsedOverall(36),
                "Commands ranked across every saved CE Tools drawing in this user profile.");
            BuildUsageTab("mostused", CommandUsageTracker.MostUsed(projectKey, 24),''',
"build overall usage")
# Prefix card title visible in workflow centre.
text = replace_once(text,
'''                Text = definition.Text,
                FontWeight = FontWeights.SemiBold,''',
'''                Text = definition.Text.StartsWith("CE-", StringComparison.OrdinalIgnoreCase)
                    ? definition.Text
                    : "CE-" + definition.Text,
                FontWeight = FontWeights.SemiBold,''',
"CE prefix command cards")
# Add book commands to every non-all workflow via Build required commands.
text = replace_once(text,
'''            var requiredCommands = new HashSet<string>(
                steps.Select(step => step.Command),
                StringComparer.OrdinalIgnoreCase);''',
'''            var requiredCommands = new HashSet<string>(
                steps.Select(step => step.Command),
                StringComparer.OrdinalIgnoreCase);
            if (!string.Equals(key, "all", StringComparison.OrdinalIgnoreCase))
            {
                requiredCommands.UnionWith(new[]
                {
                    "CE_BOOKTOOLS", "CE_DRAWINGBOOK", "CE_BOOKINDEX",
                    "CE_CLIENTBOOK", "CE_CLIENTBOOKREFRESH", "CE_CLIENTBOOKINDEX"
                });
            }''',
"books in every workflow")
write(name, text)

# 8. Command usage: aggregate Most Used across all drawings.
name = "CommandUsageTracker.cs"
text = read(name)
marker = '''        public static IList<CommandUsageRecord> Recent(string projectKey, int maximum)
        {'''
insert = '''        public static IList<CommandUsageRecord> MostUsedOverall(int maximum)
        {
            lock (SyncRoot)
            {
                var aggregate = new Dictionary<string, CommandUsageRecord>(StringComparer.OrdinalIgnoreCase);
                foreach (ProjectUsageRecord project in ProjectsByKey.Values)
                {
                    foreach (CommandUsageRecord source in project.Commands.Values)
                    {
                        CommandUsageRecord target;
                        if (!aggregate.TryGetValue(source.Command, out target))
                        {
                            target = new CommandUsageRecord { Command = source.Command };
                            aggregate[source.Command] = target;
                        }
                        target.Clicks += source.Clicks;
                        target.TotalSeconds += source.TotalSeconds;
                        target.EstimatedClicksSaved += source.EstimatedClicksSaved;
                        target.EstimatedSecondsSaved += source.EstimatedSecondsSaved;
                        target.IsFavorite = target.IsFavorite || source.IsFavorite;
                        if (source.LastUsedUtc > target.LastUsedUtc) target.LastUsedUtc = source.LastUsedUtc;
                    }
                }
                return aggregate.Values
                    .Where(item => item.Clicks > 0)
                    .OrderByDescending(item => item.Clicks)
                    .ThenByDescending(item => item.TotalSeconds)
                    .Take(Math.Max(1, maximum))
                    .Select(item => item.Clone())
                    .ToList();
            }
        }

'''
if marker not in text:
    raise RuntimeError("marker not found: command usage recent")
text = text.replace(marker, insert + marker, 1)
write(name, text)

# 9. Ribbon: expose new commands and keep CE- visible naming.
name = "PluginEntry.cs"
text = read(name)
# Project coordinate menu additions.
old = '''                        Cmd("Latitude / Longitude Map Tools", "CE_MAPLOCATION ", "Open entered WGS84 latitude/longitude in Google Maps or Google Earth without changing drawing geometry."),'''
new = '''                        Cmd("Latitude / Longitude Map Tools", "CE_MAPLOCATION ", "Open/convert WGS84 and drawing coordinates without changing drawing geometry."),
                        Cmd("Coordinate Transformation", "CE_COORDTRANSFORM ", "Transform drawing XY to WGS84 latitude/longitude and vice versa through the DWG geographic transformation."),
                        Cmd("Bulk Coordinate Conversion", "CE_COORDTRANSFORMBULK ", "Convert CSV/XLSX survey coordinate lists to/from WGS84 using the active DWG."),'''
text = replace_once(text, old, new, "ribbon coordinate tools")
# Project setup menu additions for books/pdf.
needle = '''                        Cmd("Restore Cleared Information", "CE_PROJECTRESTORE ", "Restore the values saved before the last project clear.")),'''
replacement = '''                        Cmd("Restore Cleared Information", "CE_PROJECTRESTORE ", "Restore the values saved before the last project clear."),
                        Cmd("Drawing and Client Books", "CE_BOOKTOOLS ", "Open drawing-book/client-book production from any discipline."),
                        Cmd("PDF to DWG", "CE_PDFTODWG ", "Create a new DWG from a selected PDF page using AutoCAD native PDFIMPORT.")),'''
text = replace_once(text, needle, replacement, "ribbon book pdf")
# Survey menu: add point/circle/grid. Use a reliable existing coordinate menu command marker.
needle = '''                        Cmd("Picked Coordinate Annotation (Legacy)", "CE_COORDPICKX ",'''
if needle in text:
    text = text.replace(needle,
'''                        Cmd("Point / Circle Conversion", "CE_POINTCIRCLE ", "Convert survey points to circles or circle centres to styled COGO points."),
                        Cmd("Grid Setting-Out", "CE_GRIDSETTINGOUT ", "Create unique perimeter/full-grid COGO setting-out points."),
                        Cmd("Annotation Scale Sync", "CE_ANNOTATIONSCALESYNC ", "Synchronize CE annotation objects/tables to the current annotation scale."),
''' + needle, 1)
else:
    # Alternate marker used by current ribbon.
    marker2 = '''Cmd("Coordinate Tools", "CE_COORDINATE '''
    if marker2 not in text:
        print("warning: survey menu marker not found; commands remain discoverable in workflow catalogue")
# Surface duplicate: append next to surface repair if marker exists.
needle = '''Cmd("Surface Spike / Hole Repair", "CE_SURFSPIKEHOLEFIX '''
if needle in text:
    text = text.replace(needle,
'''Cmd("Duplicate Surface", "CE_SURFACEDUPLICATE ", "Create an independent TIN copy of a selected Civil 3D surface."),
                        ''' + needle, 1)
# Most used overall in advanced/project tools where usage command marker exists.
needle = '''Cmd("Click Statistics", "CE_CLICKSTATS '''
if needle in text:
    text = text.replace(needle,
'''Cmd("Overall Most Used", "CE_MOSTUSEDOVERALL ", "Review CE command usage aggregated across all saved drawings."),
                        ''' + needle, 1)
write(name, text)

# 10. Assembly popup: distinguish useful roadway presets from the raw Civil enum.
name = "CeAssemblyCommands.cs"
text = read(name)
old = '''            model.AddChoice(
                "Type",
                "Assembly",
                "Road assembly type",
                "UndividedCrownedRoad",
                "Choose the Civil 3D assembly classification.",
                new[] { "UndividedCrownedRoad", "UndividedPlanarRoad", "Other" });'''
new = '''            model.AddChoice(
                "Preset",
                "Assembly",
                "Road assembly preset / use",
                "Urban kerbed road",
                "Choose the intended road assembly use. CE Tools creates the Civil assembly container and opens Civil 3D Tool Palettes for the compatible lane/kerb/shoulder/daylight subassemblies.",
                new[] { "Urban kerbed road", "Primary crowned road", "Secondary road", "Rural shoulder road", "Divided road", "Planar road", "Custom" });
            model.AddChoice(
                "Type",
                "Assembly",
                "Civil 3D assembly classification",
                "UndividedCrownedRoad",
                "Choose the supported Civil 3D assembly classification. Roadway subassemblies are selected from Autodesk Tool Palettes after creation.",
                new[] { "UndividedCrownedRoad", "UndividedPlanarRoad", "Other" });'''
text = replace_once(text, old, new, "assembly presets")
write(name, text)

print("Final all-comments source integration applied successfully.")

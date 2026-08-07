#!/usr/bin/env python3
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "src" / "CE.Tools.Civil3D"


def read(path):
    return path.read_text(encoding="utf-8-sig")


def write(path, text):
    path.write_text(text, encoding="utf-8")


def replace_once(text, old, new, label):
    if old not in text:
        raise SystemExit("Missing patch marker: " + label)
    if text.count(old) != 1:
        raise SystemExit("Non-unique patch marker: " + label)
    return text.replace(old, new, 1)


def replace_regex(text, pattern, replacement, label):
    updated, count = re.subn(pattern, replacement, text, count=1, flags=re.S)
    if count != 1:
        raise SystemExit("Regex patch failed: " + label + " count=" + str(count))
    return updated

# ---------------------------------------------------------------------------
# Persist every shared ProductionSettingsDialogModel popup in the active DWG.
# ---------------------------------------------------------------------------
path = SRC / "DisciplineWorkflowDialogs.cs"
text = read(path)
old = '''        public static bool EditSettings(ProductionSettingsDialogModel model)\n        {\n            if (model == null) return false;\n            var window = new ProductionSettingsWindow(model);\n            AcApplication.ShowModalWindow(window);\n            return window.Accepted;\n        }'''
new = '''        public static bool EditSettings(ProductionSettingsDialogModel model)\n        {\n            if (model == null) return false;\n            Document document = AcApplication.DocumentManager.MdiActiveDocument;\n            if (document != null)\n                ProductionSettingsPersistenceStore.Load(document.Database, model);\n            var window = new ProductionSettingsWindow(model);\n            AcApplication.ShowModalWindow(window);\n            if (window.Accepted && document != null)\n                ProductionSettingsPersistenceStore.Save(document.Database, model);\n            return window.Accepted;\n        }'''
text = replace_once(text, old, new, "persistent EditSettings")
marker = '''    internal static class ProductionStyleCatalog\n    {'''
persist = r'''    internal static class ProductionSettingsPersistenceStore
    {
        private const string RootName = "CE_TOOLS";
        private const string StoreName = "POPUP_SETTINGS";

        internal static void Load(Database database, ProductionSettingsDialogModel model)
        {
            if (database == null || model == null) return;
            try
            {
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    DBDictionary named = transaction.GetObject(database.NamedObjectsDictionaryId, OpenMode.ForRead, false) as DBDictionary;
                    if (named == null || !named.Contains(RootName)) return;
                    DBDictionary root = transaction.GetObject(named.GetAt(RootName), OpenMode.ForRead, false) as DBDictionary;
                    if (root == null || !root.Contains(StoreName)) return;
                    DBDictionary store = transaction.GetObject(root.GetAt(StoreName), OpenMode.ForRead, false) as DBDictionary;
                    string key = Key(model.Title);
                    if (store == null || !store.Contains(key)) return;
                    Xrecord record = transaction.GetObject(store.GetAt(key), OpenMode.ForRead, false) as Xrecord;
                    if (record == null || record.Data == null) return;
                    var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (TypedValue item in record.Data)
                    {
                        if (item.TypeCode != (int)DxfCode.Text) continue;
                        string value = Convert.ToString(item.Value, CultureInfo.InvariantCulture) ?? string.Empty;
                        int split = value.IndexOf('\t');
                        if (split <= 0) continue;
                        values[value.Substring(0, split)] = value.Substring(split + 1);
                    }
                    foreach (ProductionSettingsField field in model.Fields)
                    {
                        string value;
                        if (values.TryGetValue(field.Key, out value)) field.Value = value;
                    }
                }
            }
            catch
            {
                // Settings persistence must never prevent a production command from opening.
            }
        }

        internal static void Save(Database database, ProductionSettingsDialogModel model)
        {
            if (database == null || model == null) return;
            try
            {
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    DBDictionary named = transaction.GetObject(database.NamedObjectsDictionaryId, OpenMode.ForWrite, false) as DBDictionary;
                    if (named == null) return;
                    DBDictionary root;
                    if (named.Contains(RootName))
                        root = transaction.GetObject(named.GetAt(RootName), OpenMode.ForWrite, false) as DBDictionary;
                    else
                    {
                        root = new DBDictionary();
                        named.SetAt(RootName, root);
                        transaction.AddNewlyCreatedDBObject(root, true);
                    }
                    DBDictionary store;
                    if (root.Contains(StoreName))
                        store = transaction.GetObject(root.GetAt(StoreName), OpenMode.ForWrite, false) as DBDictionary;
                    else
                    {
                        store = new DBDictionary();
                        root.SetAt(StoreName, store);
                        transaction.AddNewlyCreatedDBObject(store, true);
                    }
                    string key = Key(model.Title);
                    Xrecord record;
                    if (store.Contains(key))
                        record = transaction.GetObject(store.GetAt(key), OpenMode.ForWrite, false) as Xrecord;
                    else
                    {
                        record = new Xrecord();
                        store.SetAt(key, record);
                        transaction.AddNewlyCreatedDBObject(record, true);
                    }
                    record.Data = new ResultBuffer(model.Fields
                        .Select(field => new TypedValue(
                            (int)DxfCode.Text,
                            field.Key + "\t" + (field.Value ?? string.Empty)))
                        .ToArray());
                    transaction.Commit();
                }
            }
            catch
            {
                // Commands remain usable even if a read-only drawing blocks persistence.
            }
        }

        private static string Key(string title)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (char value in title ?? string.Empty)
                {
                    hash ^= char.ToUpperInvariant(value);
                    hash *= 16777619;
                }
                return "POPUP_" + hash.ToString("X8", CultureInfo.InvariantCulture);
            }
        }
    }

'''
text = replace_once(text, marker, persist + marker, "popup persistence store")
write(path, text)

# ---------------------------------------------------------------------------
# Junction numbering: deterministic road/grid order + optional picked start.
# ---------------------------------------------------------------------------
path = SRC / "RoadJunctionCompletionCommands.cs"
text = read(path)
text = text.replace(
    'new DisciplineWorkflowAction("Number selected junction bellmouths", "CE_JUNCTIONNUMBER", "Group junctions top-left to bottom-right and number each group clockwise.", "02 Number")',
    'new DisciplineWorkflowAction("Number selected junction bellmouths", "CE_JUNCTIONNUMBER", "Choose left-to-right, top-to-bottom or top-left-to-bottom-right group order, with an optional picked start junction/return.", "02 Number")')

pattern = r'''        \[CommandMethod\("CE_TOOLS", "CE_JUNCTIONNUMBER".*?\n        \[CommandMethod\("CE_TOOLS", "CE_JUNCTIONREFRESH"'''
replacement = r'''        [CommandMethod("CE_TOOLS", "CE_JUNCTIONNUMBER", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void NumberBellmouths()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            var model = NumberingSettings();
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;
            PromptSelectionResult selected = SelectCurves(document.Editor, "\nSelect all bellmouth/return curves to number: ");
            if (selected.Status != PromptStatus.OK || selected.Value == null) return;

            string prefix = CleanPrefix(model.Text("Prefix"), "J");
            int start = model.Integer("Start", 1);
            double cluster = Math.Max(model.Double("Cluster", 35.0), 0.001);
            double textPaper = Math.Max(model.Double("TextHeight", 2.5), 0.5);
            bool clockwise = !string.Equals(model.Text("Direction"), "Counter-clockwise", StringComparison.OrdinalIgnoreCase);
            string groupOrder = model.Text("GroupOrder");
            bool pickStart = string.Equals(model.Text("StartMode"), "Pick start junction / return", StringComparison.OrdinalIgnoreCase);
            Point3d? pickedStart = null;
            if (pickStart)
            {
                PromptPointResult picked = document.Editor.GetPoint("\nPick the junction or return that must receive the first number: ");
                if (picked.Status != PromptStatus.OK) return;
                pickedStart = picked.Value.TransformBy(document.Editor.CurrentUserCoordinateSystem);
            }
            int labels = NumberSelection(
                document,
                selected.Value.GetObjectIds(),
                prefix,
                start,
                cluster,
                textPaper,
                clockwise,
                groupOrder,
                pickedStart);
            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_JUNCTIONNUMBER complete. Labels={0}; group order={1}; picked start={2}.",
                labels,
                groupOrder,
                pickedStart.HasValue ? "Yes" : "No");
        }

        [CommandMethod("CE_TOOLS", "CE_JUNCTIONREFRESH"'''
text = replace_regex(text, pattern, replacement, "junction number command")

pattern = r'''        private static int NumberSelection\(.*?\n        private static List<List<JunctionCurveItem>> Cluster'''
replacement = r'''        private static int NumberSelection(
            Document document,
            IEnumerable<ObjectId> ids,
            string prefix,
            int start,
            double clusterDistance,
            double textPaper,
            bool clockwise,
            string groupOrder,
            Point3d? pickedStart)
        {
            var items = new List<JunctionCurveItem>();
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids.Distinct())
                {
                    Curve curve;
                    try { curve = transaction.GetObject(id, OpenMode.ForRead, false) as Curve; }
                    catch { continue; }
                    if (curve == null) continue;
                    Point3d midpoint;
                    try { midpoint = curve.GetPointAtParameter((curve.StartParam + curve.EndParam) * 0.5); }
                    catch { continue; }
                    items.Add(new JunctionCurveItem(id, midpoint));
                }
            }
            if (items.Count == 0) return 0;

            List<List<JunctionCurveItem>> groups = OrderGroups(
                Cluster(items, clusterDistance),
                groupOrder,
                clusterDistance,
                pickedStart);
            int created = 0;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                EnsureRegApp(document.Database, transaction);
                ObjectId layerId = GetOrCreateLayer(document.Database, transaction);
                BlockTableRecord space = transaction.GetObject(document.Database.CurrentSpaceId, OpenMode.ForWrite, false) as BlockTableRecord;
                if (space == null) return 0;
                EraseExistingLabels(transaction, space, new HashSet<ObjectId>(items.Select(item => item.Id)));
                for (int groupIndex = 0; groupIndex < groups.Count; groupIndex++)
                {
                    List<JunctionCurveItem> group = groups[groupIndex];
                    Point3d centre = GroupCentre(group);
                    List<JunctionCurveItem> ordered = group
                        .OrderBy(item => ClockwiseKey(item.Anchor, centre))
                        .ToList();
                    if (!clockwise) ordered.Reverse();
                    if (pickedStart.HasValue && groupIndex == 0)
                        ordered = RotateToNearest(ordered, pickedStart.Value);
                    int returnIndex = 1;
                    foreach (JunctionCurveItem item in ordered)
                    {
                        string label = prefix + (start + groupIndex).ToString(CultureInfo.InvariantCulture) + "." + returnIndex.ToString(CultureInfo.InvariantCulture);
                        CreateLabel(document.Database, transaction, space, layerId, item.Anchor, label, textPaper, item.Id, centre);
                        returnIndex++;
                        created++;
                    }
                }
                transaction.Commit();
            }
            return created;
        }

        private static List<List<JunctionCurveItem>> OrderGroups(
            IList<List<JunctionCurveItem>> groups,
            string order,
            double clusterDistance,
            Point3d? pickedStart)
        {
            double rowBand = Math.Max(clusterDistance * 0.50, 0.001);
            IEnumerable<List<JunctionCurveItem>> ordered;
            if (string.Equals(order, "Left to right", StringComparison.OrdinalIgnoreCase))
            {
                ordered = groups.OrderBy(group => GroupCentre(group).X)
                    .ThenByDescending(group => GroupCentre(group).Y);
            }
            else if (string.Equals(order, "Top to bottom", StringComparison.OrdinalIgnoreCase))
            {
                ordered = groups.OrderByDescending(group => GroupCentre(group).Y)
                    .ThenBy(group => GroupCentre(group).X);
            }
            else
            {
                ordered = groups
                    .OrderByDescending(group => Math.Round(GroupCentre(group).Y / rowBand))
                    .ThenBy(group => GroupCentre(group).X)
                    .ThenByDescending(group => GroupCentre(group).Y);
            }
            var result = ordered.ToList();
            if (!pickedStart.HasValue || result.Count < 2) return result;
            int startIndex = 0;
            double best = double.MaxValue;
            for (int index = 0; index < result.Count; index++)
            {
                double distance = GroupCentre(result[index]).DistanceTo(pickedStart.Value);
                if (distance < best) { best = distance; startIndex = index; }
            }
            return result.Skip(startIndex).Concat(result.Take(startIndex)).ToList();
        }

        private static Point3d GroupCentre(IList<JunctionCurveItem> group)
        {
            return new Point3d(
                group.Average(item => item.Anchor.X),
                group.Average(item => item.Anchor.Y),
                group.Average(item => item.Anchor.Z));
        }

        private static List<JunctionCurveItem> RotateToNearest(
            IList<JunctionCurveItem> items,
            Point3d picked)
        {
            if (items == null || items.Count == 0) return new List<JunctionCurveItem>();
            int start = 0;
            double best = double.MaxValue;
            for (int index = 0; index < items.Count; index++)
            {
                double distance = items[index].Anchor.DistanceTo(picked);
                if (distance < best) { best = distance; start = index; }
            }
            return items.Skip(start).Concat(items.Take(start)).ToList();
        }

        private static void EraseExistingLabels(
            Transaction transaction,
            BlockTableRecord space,
            ISet<ObjectId> sourceIds)
        {
            foreach (ObjectId id in space.Cast<ObjectId>().ToList())
            {
                MText label;
                try { label = transaction.GetObject(id, OpenMode.ForWrite, false) as MText; }
                catch { continue; }
                if (label == null) continue;
                JunctionLink link;
                if (!TryReadLink(label, space.Database, out link) || !sourceIds.Contains(link.SourceId)) continue;
                try { label.Erase(); } catch { }
            }
        }

        private static List<List<JunctionCurveItem>> Cluster'''
text = replace_regex(text, pattern, replacement, "junction ordering")

old = '''        private static ProductionSettingsDialogModel NumberingSettings()\n        {\n            var model = new ProductionSettingsDialogModel("CE Tools - Junction Bellmouth Numbering", "Junction groups are sorted from top-left to bottom-right. Within each junction, numbering starts at the top-left return and continues clockwise.");\n            model.AddText("Prefix", "01 Numbering", "Junction prefix", "J", "J creates J1.1, J1.2, J2.1...");\n            model.AddPositiveInteger("Start", "01 Numbering", "Starting junction number", 1, "First automatic junction group number.");\n            model.AddChoice("Direction", "01 Numbering", "Return direction", "Clockwise", "Number around each junction.", new[] { "Clockwise", "Counter-clockwise" });\n            model.AddDouble("Cluster", "02 Grouping", "Junction grouping distance", 35.0, "Bellmouth midpoints within this distance are treated as one junction.");\n            model.AddDouble("TextHeight", "03 Annotation", "Paper text height", 2.5, "Annotative label paper height.");\n            return model;\n        }'''
new = '''        private static ProductionSettingsDialogModel NumberingSettings()\n        {\n            var model = new ProductionSettingsDialogModel(\n                "CE Tools - Junction Bellmouth Numbering",\n                "Choose the junction sequence explicitly. Top-left to bottom-right is the default; horizontal roads can run left to right and vertical roads can run top to bottom. A picked start rotates the sequence to the selected junction/return.");\n            model.AddText("Prefix", "01 Numbering", "Junction prefix", "J", "J creates J1.1, J1.2, J2.1...");\n            model.AddPositiveInteger("Start", "01 Numbering", "Starting junction number", 1, "First automatic junction group number.");\n            model.AddChoice("GroupOrder", "01 Numbering", "Junction group direction", "Top-left to bottom-right", "Use left-to-right for horizontal roads and top-to-bottom for vertical roads.", new[] { "Top-left to bottom-right", "Left to right", "Top to bottom" });\n            model.AddChoice("StartMode", "01 Numbering", "Sequence start", "Automatic start", "Pick a junction/return when numbering must start from a specific existing point.", new[] { "Automatic start", "Pick start junction / return" });\n            model.AddChoice("Direction", "01 Numbering", "Return direction inside each junction", "Clockwise", "The automatic corner is top-left; a picked first return overrides the first junction start.", new[] { "Clockwise", "Counter-clockwise" });\n            model.AddDouble("Cluster", "02 Grouping", "Junction grouping distance", 35.0, "Bellmouth midpoints within this distance are treated as one junction.");\n            model.AddDouble("TextHeight", "03 Annotation", "Paper text height", 2.5, "Annotative label paper height.");\n            return model;\n        }'''
text = replace_once(text, old, new, "junction settings")
write(path, text)

# ---------------------------------------------------------------------------
# Vertex setting-out sequencing, surface dropdown, table continuation and dims.
# ---------------------------------------------------------------------------
path = SRC / "VertexSettingOutCommands.cs"
text = read(path)
old = '''            var settings = new ProductionSettingsDialogModel(\n                "CE Tools - Vertex Setting-Out Settings",'''
new = '''            List<string> surfaceChoices = ReadSurfaceNames(document.Database, civilDocument);\n            surfaceChoices.Insert(0, "<Pick surface in drawing>");\n\n            var settings = new ProductionSettingsDialogModel(\n                "CE Tools - Vertex Setting-Out Settings",'''
text = replace_once(text, old, new, "surface choices")

old = '''            settings.AddChoice(\n                "Elevation", "01 Output", "XYZ elevation source", "Source geometry",\n                "Read Z from the selected source geometry, a Civil 3D surface, or a separate feature line. The reference remains linked on refresh.",\n                new[] { "Source geometry", "Select Civil 3D surface", "Select feature line" });'''
new = old + '''\n            settings.AddChoice(\n                "ElevationSurface", "01 Output", "Civil 3D elevation surface", "<Pick surface in drawing>",\n                "Choose an existing surface by name or keep the pick option to select it in the drawing after saving the popup.",\n                surfaceChoices);'''
text = replace_once(text, old, new, "surface dropdown")

old = '''            settings.AddPositiveInteger(\n                "Start", "02 Numbering", "Starting number", 1,\n                "First generated point number/name.");'''
new = old + '''\n            settings.AddChoice(\n                "NumberingMode", "02 Numbering", "Numbering layout", "Single sequence",\n                "Use one sequence such as P1, P2... or number each selected road/source as J1.1, J1.2... then J2.1, J2.2....",\n                new[] { "Single sequence", "Road grouped sequence" });\n            settings.AddPositiveInteger(\n                "RoadStart", "02 Numbering", "Starting road number", 1,\n                "Road grouped sequence starts with this road number, for example J1.1.");\n            settings.AddChoice(\n                "SequenceMode", "02 Numbering", "Sequence direction", "Auto by road orientation",\n                "Horizontal sources sequence left to right; vertical sources sequence top to bottom. You can force either direction or preserve source geometry order.",\n                new[] { "Auto by road orientation", "Left to right", "Top to bottom", "Source geometry order" });\n            settings.AddChoice(\n                "StartMode", "02 Numbering", "Sequence start point", "Automatic start",\n                "Pick any generated/reference point to rotate numbering so that point becomes the start of the sequence.",\n                new[] { "Automatic start", "Pick start point" });'''
text = replace_once(text, old, new, "vertex numbering settings")

old = '''            settings.AddChoice(\n                "YSign", "04 Coordinate Display", "Displayed Y sign", "Keep Y sign",\n                "Keep or reverse the displayed Y sign without changing the COGO point or source geometry.",\n                new[] { "Keep Y sign", "Reverse Y sign" });'''
new = old + '''\n            settings.AddChoice(\n                "TableMode", "05 Linked Table", "Linked table action", "Create new linked table",\n                "Create a new table or add the selected sources to an existing CE vertex setting-out table and continue its linked sequence.",\n                new[] { "Create new linked table", "Continue existing linked table" });'''
text = replace_once(text, old, new, "table mode")

pattern = r'''            string outputType = settings.Text\("Output"\);.*?            var link = new VertexSettingLink\n            \{.*?            \};\n\n            try\n            \{\n                ObjectId tableId = CreateGroup\('''
replacement = r'''            string outputType = settings.Text("Output");
            string prefix = string.IsNullOrWhiteSpace(settings.Text("Prefix"))
                ? "P"
                : settings.Text("Prefix").Trim();
            int startNumber = settings.Integer("Start", 1);
            int roadStartNumber = settings.Integer("RoadStart", 1);
            double labelOffset = settings.Double("Offset", 3.0);
            string generationMode = settings.Text("Generation");
            string elevationMode = settings.Text("Elevation");
            string elevationSurface = settings.Text("ElevationSurface");
            string coordinateOrder = settings.Text("CoordinateOrder");
            string xSign = settings.Text("XSign");
            string ySign = settings.Text("YSign");
            string numberingMode = settings.Text("NumberingMode");
            string sequenceMode = settings.Text("SequenceMode");
            string startMode = settings.Text("StartMode");
            string tableMode = settings.Text("TableMode");
            ObjectId elevationSourceId;
            if (!PromptElevationSource(
                    document,
                    civilDocument,
                    elevationMode,
                    elevationSurface,
                    out elevationSourceId)) return;

            AnnotationOptions annotation;
            if (!AnnotationSettingsStore.Prepare(document, false, out annotation)) return;

            IList<VertexSettingSource> sources;
            int geometryRejected;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                sources = VertexSettingOutGeometry.ReadSources(
                    document.Database,
                    transaction,
                    sourceIds,
                    out geometryRejected);
            }
            rejected += geometryRejected;
            ApplyGenerationMode(sources, generationMode);
            ApplyElevationReference(
                document.Database,
                sources,
                elevationMode,
                elevationSourceId);
            if (sources.Count == 0 || sources.All(item => item.Records.Count == 0))
            {
                document.Editor.WriteMessage("\nCE_VERTEXSETTINGOUT cancelled. The selected objects produced no setting-out geometry.");
                return;
            }

            string startRecordKey = string.Empty;
            if (string.Equals(startMode, "Pick start point", StringComparison.OrdinalIgnoreCase))
            {
                PromptPointResult picked = document.Editor.GetPoint(
                    "\nPick the setting-out point/location that must receive the first number: ");
                if (picked.Status != PromptStatus.OK) return;
                Point3d world = picked.Value.TransformBy(document.Editor.CurrentUserCoordinateSystem);
                startRecordKey = FindNearestRecordKey(sources, world);
            }

            ObjectId existingTableId = ObjectId.Null;
            VertexSettingLink existingLink = null;
            Point3d tablePoint = Point3d.Origin;
            bool continueExisting = string.Equals(
                tableMode,
                "Continue existing linked table",
                StringComparison.OrdinalIgnoreCase);
            if (continueExisting)
            {
                PromptEntityResult table = PromptLinkedTable(
                    document.Editor,
                    "\nSelect the existing CE vertex setting-out table to continue: ");
                if (table.Status != PromptStatus.OK) return;
                existingTableId = table.ObjectId;
                existingLink = ReadLink(document.Database, existingTableId);
                if (string.IsNullOrWhiteSpace(startRecordKey))
                    startRecordKey = existingLink.StartRecordKey;
            }
            else
            {
                PromptPointResult insertion = document.Editor.GetPoint(
                    "\nPick insertion point for the linked setting-out table: ");
                if (insertion.Status != PromptStatus.OK) return;
                tablePoint = insertion.Value;
            }

            List<VertexSettingRecord> records = FlattenAndName(
                sources,
                prefix,
                startNumber,
                numberingMode,
                roadStartNumber,
                sequenceMode,
                startRecordKey);
            int radialDimensions = sources.Sum(item => item.Dimensions.Count);
            var review = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("Accepted sources", sources.Count.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("Rejected selections", rejected.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("Point output", outputType),
                new KeyValuePair<string, string>("Numbering layout", numberingMode),
                new KeyValuePair<string, string>("Sequence direction", sequenceMode),
                new KeyValuePair<string, string>("Picked start", string.IsNullOrWhiteSpace(startRecordKey) ? "Automatic" : "Yes"),
                new KeyValuePair<string, string>("Linked table action", tableMode),
                new KeyValuePair<string, string>("Generated point rows", records.Count.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("Radius dimensions", radialDimensions.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("Automatic linked refresh", "Yes"),
                new KeyValuePair<string, string>("Excel export", "Linked table")
            };
            if (!PopupTablePresenter.ShowReview(
                    "CE Tools - Vertex Setting-Out Preview",
                    "Horizontal road sources run left-to-right and vertical sources run top-to-bottom in Auto mode. Arc centres are numbered after their on-curve setting-out points. Existing linked tables can be extended without losing their group link.",
                    review,
                    continueExisting ? "Continue Setting-Out" : "Create Setting-Out"))
                return;

            IList<string> linkedHandles = existingLink == null
                ? sources.Select(item => item.Handle).ToList()
                : existingLink.SourceHandles
                    .Concat(sources.Select(item => item.Handle))
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            var link = new VertexSettingLink
            {
                GroupId = existingLink == null ? Guid.NewGuid().ToString("N") : existingLink.GroupId,
                OutputType = outputType,
                Prefix = prefix,
                StartNumber = startNumber,
                RoadStartNumber = roadStartNumber,
                NumberingMode = numberingMode,
                SequenceMode = sequenceMode,
                StartRecordKey = startRecordKey,
                LabelOffset = labelOffset,
                GenerationMode = generationMode,
                ElevationMode = elevationMode,
                CoordinateOrder = coordinateOrder,
                XSign = xSign,
                YSign = ySign,
                ElevationSourceHandle = elevationSourceId.IsNull
                    ? string.Empty
                    : elevationSourceId.Handle.ToString(),
                SourceHandles = linkedHandles
            };

            try
            {
                if (continueExisting)
                {
                    UpdateTableLink(document.Database, existingTableId, link);
                    int continuedPoints;
                    int continuedDimensions;
                    RefreshTable(document, existingTableId, out continuedPoints, out continuedDimensions);
                    document.Editor.SetImpliedSelection(new[] { existingTableId });
                    RuntimeAnnotationLinkManager.ClampLinkedAnnotations(document, true);
                    document.Editor.Regen();
                    document.Editor.WriteMessage(
                        "\nCE_VERTEXSETTINGOUT continued existing table. Total linked sources={0}; points={1}; radius dimensions={2}.",
                        linkedHandles.Count,
                        continuedPoints,
                        continuedDimensions);
                    return;
                }
                ObjectId tableId = CreateGroup('''
text = replace_regex(text, pattern, replacement, "vertex create flow")

text = text.replace(
    '''                    tablePoint.Value,\n                    annotation.TextHeight);''',
    '''                    tablePoint,\n                    annotation.TextHeight);''', 1)

text = replace_once(
    text,
    '''                List<VertexSettingRecord> records = FlattenAndName(sources, link.Prefix, link.StartNumber);''',
    '''                List<VertexSettingRecord> records = FlattenAndName(\n                    sources,\n                    link.Prefix,\n                    link.StartNumber,\n                    link.NumberingMode,\n                    link.RoadStartNumber,\n                    link.SequenceMode,\n                    link.StartRecordKey);''',
    "refresh sequencing")

pattern = r'''        private static List<VertexSettingRecord> FlattenAndName\(.*?\n        private static ObjectId CreateOutput'''
replacement = r'''        private static List<VertexSettingRecord> FlattenAndName(
            IEnumerable<VertexSettingSource> sources,
            string prefix,
            int startNumber,
            string numberingMode,
            int roadStartNumber,
            string sequenceMode,
            string startRecordKey)
        {
            var result = new List<VertexSettingRecord>();
            List<VertexSettingSource> orderedSources = OrderSources(
                sources,
                sequenceMode,
                startRecordKey);
            bool roadGrouped = string.Equals(
                numberingMode,
                "Road grouped sequence",
                StringComparison.OrdinalIgnoreCase);
            int sequence = startNumber;
            int road = roadStartNumber;
            foreach (VertexSettingSource source in orderedSources)
            {
                List<VertexSettingRecord> orderedRecords = OrderRecords(
                    source,
                    sequenceMode,
                    startRecordKey);
                int roadPoint = 1;
                foreach (VertexSettingRecord record in orderedRecords)
                {
                    record.PointName = roadGrouped
                        ? prefix + road.ToString(CultureInfo.InvariantCulture) + "." + roadPoint.ToString(CultureInfo.InvariantCulture)
                        : prefix + sequence.ToString(CultureInfo.InvariantCulture);
                    roadPoint++;
                    sequence++;
                    result.Add(record);
                }
                road++;
            }
            return result;
        }

        private static List<VertexSettingSource> OrderSources(
            IEnumerable<VertexSettingSource> sources,
            string sequenceMode,
            string startRecordKey)
        {
            var values = (sources ?? Enumerable.Empty<VertexSettingSource>()).ToList();
            IEnumerable<VertexSettingSource> ordered;
            if (string.Equals(sequenceMode, "Left to right", StringComparison.OrdinalIgnoreCase))
                ordered = values.OrderBy(item => SourceCentre(item).X).ThenByDescending(item => SourceCentre(item).Y);
            else if (string.Equals(sequenceMode, "Top to bottom", StringComparison.OrdinalIgnoreCase))
                ordered = values.OrderByDescending(item => SourceCentre(item).Y).ThenBy(item => SourceCentre(item).X);
            else
                ordered = values.OrderByDescending(item => SourceCentre(item).Y).ThenBy(item => SourceCentre(item).X);
            var result = ordered.ToList();
            if (string.IsNullOrWhiteSpace(startRecordKey)) return result;
            int start = result.FindIndex(item => item.Records.Any(record => string.Equals(record.Key, startRecordKey, StringComparison.OrdinalIgnoreCase)));
            return start <= 0 ? result : result.Skip(start).Concat(result.Take(start)).ToList();
        }

        private static Point3d SourceCentre(VertexSettingSource source)
        {
            IList<VertexSettingRecord> records = source == null ? null : source.Records;
            if (records == null || records.Count == 0) return Point3d.Origin;
            return new Point3d(
                records.Average(record => record.Point.X),
                records.Average(record => record.Point.Y),
                records.Average(record => record.Point.Z));
        }

        private static List<VertexSettingRecord> OrderRecords(
            VertexSettingSource source,
            string sequenceMode,
            string startRecordKey)
        {
            var records = source == null || source.Records == null
                ? new List<VertexSettingRecord>()
                : source.Records.ToList();
            var centres = records.Where(record => string.Equals(record.Kind, "ARC CENTER", StringComparison.OrdinalIgnoreCase)).ToList();
            var onGeometry = records.Where(record => !string.Equals(record.Kind, "ARC CENTER", StringComparison.OrdinalIgnoreCase)).ToList();
            string mode = sequenceMode ?? string.Empty;
            if (string.Equals(mode, "Auto by road orientation", StringComparison.OrdinalIgnoreCase))
            {
                double width = onGeometry.Count == 0 ? 0.0 : onGeometry.Max(record => record.Point.X) - onGeometry.Min(record => record.Point.X);
                double height = onGeometry.Count == 0 ? 0.0 : onGeometry.Max(record => record.Point.Y) - onGeometry.Min(record => record.Point.Y);
                mode = width >= height ? "Left to right" : "Top to bottom";
            }
            if (string.Equals(mode, "Left to right", StringComparison.OrdinalIgnoreCase))
                onGeometry = onGeometry.OrderBy(record => record.Point.X).ThenByDescending(record => record.Point.Y).ToList();
            else if (string.Equals(mode, "Top to bottom", StringComparison.OrdinalIgnoreCase))
                onGeometry = onGeometry.OrderByDescending(record => record.Point.Y).ThenBy(record => record.Point.X).ToList();
            // Source geometry order deliberately keeps the extracted source order.
            if (!string.IsNullOrWhiteSpace(startRecordKey))
            {
                int start = onGeometry.FindIndex(record => string.Equals(record.Key, startRecordKey, StringComparison.OrdinalIgnoreCase));
                if (start > 0) onGeometry = onGeometry.Skip(start).Concat(onGeometry.Take(start)).ToList();
            }

            foreach (VertexSettingRecord centre in centres.OrderBy(item => item.SegmentIndex))
            {
                int segment = Math.Max(centre.SegmentIndex - 1, 0);
                string startKey = centre.SourceHandle + "|V" + segment.ToString(CultureInfo.InvariantCulture);
                string endKey = centre.SourceHandle + "|V" + (segment + 1).ToString(CultureInfo.InvariantCulture);
                int insertAfter = -1;
                for (int index = 0; index < onGeometry.Count; index++)
                {
                    VertexSettingRecord candidate = onGeometry[index];
                    if (candidate.SegmentIndex == centre.SegmentIndex ||
                        string.Equals(candidate.Key, startKey, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(candidate.Key, endKey, StringComparison.OrdinalIgnoreCase))
                        insertAfter = Math.Max(insertAfter, index);
                }
                int insertion = Math.Min(Math.Max(insertAfter + 1, 0), onGeometry.Count);
                onGeometry.Insert(insertion, centre);
            }
            return onGeometry;
        }

        private static string FindNearestRecordKey(
            IEnumerable<VertexSettingSource> sources,
            Point3d picked)
        {
            VertexSettingRecord nearest = null;
            double best = double.MaxValue;
            foreach (VertexSettingRecord record in (sources ?? Enumerable.Empty<VertexSettingSource>()).SelectMany(item => item.Records))
            {
                double distance = record.Point.DistanceTo(picked);
                if (distance < best) { best = distance; nearest = record; }
            }
            return nearest == null ? string.Empty : nearest.Key;
        }

        private static ObjectId CreateOutput'''
text = replace_regex(text, pattern, replacement, "vertex flatten sequence")

# Radial text in the centre of the dimension line and DIMTMOVE=2.
old = '''            double offset = Math.Max(textHeight * 4.0, dimension.Radius * 0.20);\n            try\n            {\n                radial.TextPosition = dimension.Center +\n                    direction * (dimension.Radius + offset);\n            }\n            catch { }'''
new = '''            try\n            {\n                radial.TextPosition = dimension.Center +\n                    direction * (dimension.Radius * 0.50);\n                SetDimensionTextMovementNoLeader(radial);\n            }\n            catch { }'''
text = replace_once(text, old, new, "radial text centre")

marker = '''        private static Point3d LabelLocation(Point3d point, double offset)'''
helper = r'''        private static void SetDimensionTextMovementNoLeader(Dimension dimension)
        {
            if (dimension == null) return;
            try
            {
                PropertyInfo property = dimension.GetType().GetProperty(
                    "Dimtmove",
                    BindingFlags.Public | BindingFlags.Instance);
                if (property == null || !property.CanWrite) return;
                object value = property.PropertyType.IsEnum
                    ? Enum.ToObject(property.PropertyType, 2)
                    : Convert.ChangeType(2, property.PropertyType, CultureInfo.InvariantCulture);
                property.SetValue(dimension, value, null);
            }
            catch { }
        }

'''
text = replace_once(text, marker, helper + marker, "dim text movement helper")

# Surface selection by name or entity pick.
pattern = r'''        private static bool PromptElevationSource\(.*?\n        private static void ApplyElevationReference'''
replacement = r'''        private static bool PromptElevationSource(
            Document document,
            CivilDocument civilDocument,
            string mode,
            string surfaceName,
            out ObjectId sourceId)
        {
            sourceId = ObjectId.Null;
            if (document == null || string.IsNullOrWhiteSpace(mode) ||
                string.Equals(mode, "Source geometry", StringComparison.OrdinalIgnoreCase))
                return true;

            if (string.Equals(mode, "Select Civil 3D surface", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(surfaceName) &&
                !surfaceName.StartsWith("<Pick", StringComparison.OrdinalIgnoreCase))
            {
                sourceId = ResolveSurfaceByName(document.Database, civilDocument, surfaceName);
                if (!sourceId.IsNull) return true;
            }

            var options = new PromptEntityOptions(
                string.Equals(mode, "Select Civil 3D surface", StringComparison.OrdinalIgnoreCase)
                    ? "\nSelect the Civil 3D surface used for all setting-out Z values: "
                    : "\nSelect the feature line used for all setting-out Z values: ");
            PromptEntityResult selected = document.Editor.GetEntity(options);
            if (selected.Status != PromptStatus.OK) return false;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                DBObject value = transaction.GetObject(selected.ObjectId, OpenMode.ForRead, false);
                bool valid = string.Equals(mode, "Select Civil 3D surface", StringComparison.OrdinalIgnoreCase)
                    ? value is Autodesk.Civil.DatabaseServices.Surface
                    : value is Autodesk.Civil.DatabaseServices.FeatureLine;
                if (!valid)
                {
                    document.Editor.WriteMessage("\nThe selected object is not the required Civil 3D elevation source.");
                    return false;
                }
            }
            sourceId = selected.ObjectId;
            return true;
        }

        private static List<string> ReadSurfaceNames(Database database, CivilDocument civilDocument)
        {
            var names = new List<string>();
            if (database == null || civilDocument == null) return names;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in civilDocument.GetSurfaceIds())
                {
                    Autodesk.Civil.DatabaseServices.Surface surface;
                    try { surface = transaction.GetObject(id, OpenMode.ForRead, false) as Autodesk.Civil.DatabaseServices.Surface; }
                    catch { continue; }
                    if (surface != null && !string.IsNullOrWhiteSpace(surface.Name)) names.Add(surface.Name);
                }
            }
            return names.Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(item => item, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private static ObjectId ResolveSurfaceByName(
            Database database,
            CivilDocument civilDocument,
            string name)
        {
            if (database == null || civilDocument == null || string.IsNullOrWhiteSpace(name)) return ObjectId.Null;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in civilDocument.GetSurfaceIds())
                {
                    Autodesk.Civil.DatabaseServices.Surface surface;
                    try { surface = transaction.GetObject(id, OpenMode.ForRead, false) as Autodesk.Civil.DatabaseServices.Surface; }
                    catch { continue; }
                    if (surface != null && string.Equals(surface.Name, name, StringComparison.OrdinalIgnoreCase)) return id;
                }
            }
            return ObjectId.Null;
        }

        private static void ApplyElevationReference'''
text = replace_regex(text, pattern, replacement, "surface picker")

# Table XData stores sequencing fields.
old = '''            values.Add(new TypedValue(\n                (int)DxfCode.ExtendedDataAsciiString,\n                "YSIGN=" + (link.YSign ?? "Keep Y sign")));'''
new = old + '''\n            values.Add(new TypedValue(\n                (int)DxfCode.ExtendedDataAsciiString,\n                "NUMMODE=" + (link.NumberingMode ?? "Single sequence")));\n            values.Add(new TypedValue(\n                (int)DxfCode.ExtendedDataAsciiString,\n                "ROADSTART=" + link.RoadStartNumber.ToString(CultureInfo.InvariantCulture)));\n            values.Add(new TypedValue(\n                (int)DxfCode.ExtendedDataAsciiString,\n                "SEQ=" + (link.SequenceMode ?? "Auto by road orientation")));\n            values.Add(new TypedValue(\n                (int)DxfCode.ExtendedDataAsciiString,\n                "STARTKEY=" + (link.StartRecordKey ?? string.Empty)));'''
text = replace_once(text, old, new, "link sequence write")

old = '''                YSign = "Keep Y sign",\n                SourceHandles = new List<string>()'''
new = '''                YSign = "Keep Y sign",\n                NumberingMode = "Single sequence",\n                RoadStartNumber = 1,\n                SequenceMode = "Auto by road orientation",\n                StartRecordKey = string.Empty,\n                SourceHandles = new List<string>()'''
text = replace_once(text, old, new, "link sequence defaults")

old = '''                else if (value.StartsWith("YSIGN=", StringComparison.OrdinalIgnoreCase))\n                    link.YSign = value.Substring(6);\n                else if (value.StartsWith("SRC=", StringComparison.OrdinalIgnoreCase))'''
new = '''                else if (value.StartsWith("YSIGN=", StringComparison.OrdinalIgnoreCase))\n                    link.YSign = value.Substring(6);\n                else if (value.StartsWith("NUMMODE=", StringComparison.OrdinalIgnoreCase))\n                    link.NumberingMode = value.Substring(8);\n                else if (value.StartsWith("ROADSTART=", StringComparison.OrdinalIgnoreCase))\n                {\n                    int roadStart;\n                    if (int.TryParse(value.Substring(10), NumberStyles.Integer, CultureInfo.InvariantCulture, out roadStart) && roadStart > 0)\n                        link.RoadStartNumber = roadStart;\n                }\n                else if (value.StartsWith("SEQ=", StringComparison.OrdinalIgnoreCase))\n                    link.SequenceMode = value.Substring(4);\n                else if (value.StartsWith("STARTKEY=", StringComparison.OrdinalIgnoreCase))\n                    link.StartRecordKey = value.Substring(9);\n                else if (value.StartsWith("SRC=", StringComparison.OrdinalIgnoreCase))'''
text = replace_once(text, old, new, "link sequence read")

# Helpers to read/update an existing linked table.
marker = '''        private static void WriteOutputLink(\n            Entity entity,'''
helpers = r'''        private static VertexSettingLink ReadLink(Database database, ObjectId tableId)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                Table table = transaction.GetObject(tableId, OpenMode.ForRead, false) as Table;
                return ReadTableLink(table);
            }
        }

        private static void UpdateTableLink(Database database, ObjectId tableId, VertexSettingLink link)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                EnsureRegApp(database, transaction);
                Table table = transaction.GetObject(tableId, OpenMode.ForWrite, false) as Table;
                if (table == null) throw new InvalidOperationException("The selected existing table is unavailable.");
                WriteTableLink(table, transaction, link);
                ForceTableGraphics(table);
                transaction.Commit();
            }
        }

'''
text = replace_once(text, marker, helpers + marker, "existing table helpers")

old = '''            public string YSign { get; set; }\n            public IList<string> SourceHandles { get; set; }'''
new = '''            public string YSign { get; set; }\n            public string NumberingMode { get; set; }\n            public int RoadStartNumber { get; set; }\n            public string SequenceMode { get; set; }\n            public string StartRecordKey { get; set; }\n            public IList<string> SourceHandles { get; set; }'''
text = replace_once(text, old, new, "link sequence properties")
write(path, text)

# ---------------------------------------------------------------------------
# Add the permanent validator to the normal core suite.
# ---------------------------------------------------------------------------
path = ROOT / ".github" / "workflows" / "core-tests.yml"
text = read(path)
old = '''      - name: Validate latest runtime and design comments\n        run: python3 scripts/Validate-LatestRuntimeDesignComments.py\n\n      - name: Set up .NET'''
new = '''      - name: Validate latest runtime and design comments\n        run: python3 scripts/Validate-LatestRuntimeDesignComments.py\n\n      - name: Validate junction sequencing and persistent popups\n        run: python3 scripts/Validate-JunctionSequencePersistence.py\n\n      - name: Set up .NET'''
text = replace_once(text, old, new, "core validator step")
write(path, text)

print("Junction sequencing, persistent popup, vertex sequence/table and radial dimension patches applied.")

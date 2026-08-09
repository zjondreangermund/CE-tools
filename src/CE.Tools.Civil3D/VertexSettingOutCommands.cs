using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilCogoPoint = Autodesk.Civil.DatabaseServices.CogoPoint;

[assembly: CommandClass(typeof(CETools.Civil3D.VertexSettingOutCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Creates and maintains setting-out points for multiple polylines and feature
    /// lines. Every source vertex is included; long tangents and arcs receive the
    /// requested additional points, every arc receives a radius dimension, and the
    /// linked table can be refreshed and exported after source geometry changes.
    /// </summary>
    public sealed class VertexSettingOutCommands
    {
        private const string AppName = "CE_VERTEX_SETTINGOUT";
        private const string SchemaVersion = "2";

        [CommandMethod("CE_TOOLS", "CE_VERTEXSETTINGOUTTOOLS", CommandFlags.Modal)]
        public void Menu()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            DisciplineWorkflowDialogs.SelectAndRun(
                document,
                "CE Tools - Vertex Setting-Out",
                "Create dynamic COGO, MText or MLeader setting-out points from multiple polylines and feature lines.",
                new List<DisciplineWorkflowAction>
                {
                    new DisciplineWorkflowAction("Create multi-source setting-out", "CE_VERTEXSETTINGOUT", "Add vertices, long-tangent points, arc midpoints, arc centres and radius dimensions.", "01 Create"),
                    new DisciplineWorkflowAction("Refresh linked setting-out", "CE_VERTEXSETTINGOUTREFRESH", "Recalculate names, coordinates, annotations, radius dimensions and the linked table.", "02 Maintain"),
                    new DisciplineWorkflowAction("Export linked setting-out", "CE_VERTEXSETTINGOUTEXPORT", "Refresh and export the selected linked table to Excel.", "03 Export")
                });
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_VERTEXSETTINGOUT",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void Create()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (civilDocument == null)
            {
                document.Editor.WriteMessage("\nCE_VERTEXSETTINGOUT cancelled. No active Civil 3D document is available.");
                return;
            }

            PromptSelectionResult selection = GetSelection(
                document.Editor,
                "\nSelect multiple polylines and/or Civil 3D feature lines: ");
            if (selection.Status != PromptStatus.OK) return;

            var sourceIds = new List<ObjectId>();
            int rejected = 0;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject selected in selection.Value)
                {
                    if (selected == null || selected.ObjectId.IsNull)
                    {
                        rejected++;
                        continue;
                    }
                    Entity entity;
                    try
                    {
                        entity = transaction.GetObject(
                            selected.ObjectId,
                            OpenMode.ForRead,
                            false) as Entity;
                    }
                    catch
                    {
                        rejected++;
                        continue;
                    }
                    if (VertexSettingOutGeometry.IsSupported(entity))
                        sourceIds.Add(selected.ObjectId);
                    else
                        rejected++;
                }
            }
            sourceIds = sourceIds.Distinct().ToList();
            if (sourceIds.Count == 0)
            {
                document.Editor.WriteMessage("\nCE_VERTEXSETTINGOUT cancelled. No supported source geometry was selected.");
                return;
            }

            List<string> surfaceChoices = ReadSurfaceNames(document.Database, civilDocument);
            surfaceChoices.Insert(0, "<Pick surface in drawing>");
            var ngSurfaceChoices = new List<string> { "<None>" };
            ngSurfaceChoices.AddRange(surfaceChoices);

            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Vertex Setting-Out Settings",
                "All vertices are included. Arcs longer than 10 m receive a midpoint; every arc receives a centre point and radius dimension. Tangents longer than 20 m receive a midpoint, and tangents longer than 40 m receive three equally spaced points.");
            settings.AddChoice(
                "Output", "01 Output", "Point output", "COGO",
                "Choose one dynamic point/annotation output for every generated setting-out location.",
                new[] { "COGO", "MText", "MLeader" });
            settings.AddChoice(
                "Generation", "01 Output", "Point generation", "Engineering setting-out points",
                "Choose the complete arc/tangent engineering rules or only the original polyline/feature-line vertices.",
                new[] { "Engineering setting-out points", "Polyline vertices only" });
            settings.AddChoice(
                "Elevation", "01 Output", "XYZ elevation source", "Source geometry",
                "Read Z from the selected source geometry, a Civil 3D surface, or a separate feature line. The reference remains linked on refresh.",
                new[] { "Source geometry", "Select Civil 3D surface", "Select feature line" });
            settings.AddChoice(
                "ElevationSurface", "01 Output", "Civil 3D elevation surface", "<Pick surface in drawing>",
                "Choose an existing surface by name or keep the pick option to select it in the drawing after saving the popup.",
                surfaceChoices);
            settings.AddChoice(
                "NGSurface", "01 Output", "Existing / NG level surface", "<None>",
                "Optional existing-ground/base surface used for the NG Level column. It never changes X/Y.",
                ngSurfaceChoices);
            settings.AddChoice(
                "DesignSurface", "01 Output", "Design / comparison level surface", "<Use setting-out point elevation>",
                "Optional design/comparison surface. When selected, Design Level is sampled independently and Difference = Design - NG.",
                new[] { "<Use setting-out point elevation>" }.Concat(surfaceChoices).Distinct(StringComparer.OrdinalIgnoreCase));
            settings.AddText(
                "Prefix", "02 Numbering", "Point name prefix", "P",
                "Names are generated as P1, P2, P3 and are resequenced when linked geometry changes.");
            settings.AddPositiveInteger(
                "Start", "02 Numbering", "Starting number", 1,
                "First generated point number/name.");
            settings.AddChoice(
                "NumberingMode", "02 Numbering", "Numbering layout", "Single sequence",
                "Use one sequence such as P1, P2... or number each selected road/source as J1.1, J1.2... then J2.1, J2.2....",
                new[] { "Single sequence", "Road grouped sequence" });
            settings.AddPositiveInteger(
                "RoadStart", "02 Numbering", "Starting road number", 1,
                "Road grouped sequence starts with this road number, for example J1.1.");
            settings.AddChoice(
                "SequenceMode", "02 Numbering", "Sequence direction", "Auto by road orientation",
                "Horizontal sources sequence left to right; vertical sources sequence top to bottom. You can force either direction or preserve source geometry order.",
                new[] { "Auto by road orientation", "Left to right", "Top to bottom", "Source geometry order" });
            settings.AddChoice(
                "StartMode", "02 Numbering", "Sequence start point", "Automatic start",
                "Pick any generated/reference point to rotate numbering so that point becomes the start of the sequence.",
                new[] { "Automatic start", "Pick start point" });
            settings.AddPositiveDouble(
                "Offset", "03 Annotation", "MText/MLeader offset", 3.0,
                "Drawing-unit offset from each setting-out point to its annotation.");
            settings.AddChoice(
                "CoordinateOrder", "04 Coordinate Display", "Coordinate order", "X then Y",
                "Swap only the displayed X/Y letters/headings. Numeric coordinate values and true drawing coordinates remain unchanged.",
                new[] { "X then Y", "Y then X" });
            settings.AddChoice(
                "XSign", "04 Coordinate Display", "Displayed X sign", "Keep X sign",
                "Keep or reverse the displayed X sign without changing the COGO point or source geometry.",
                new[] { "Keep X sign", "Reverse X sign" });
            settings.AddChoice(
                "YSign", "04 Coordinate Display", "Displayed Y sign", "Keep Y sign",
                "Keep or reverse the displayed Y sign without changing the COGO point or source geometry.",
                new[] { "Keep Y sign", "Reverse Y sign" });
            settings.AddChoice(
                "TableMode", "05 Linked Table", "Linked table action", "Create new linked table",
                "Create a new table or add the selected sources to an existing CE vertex setting-out table and continue its linked sequence.",
                new[] { "Create new linked table", "Continue existing linked table" });
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;

            string outputType = settings.Text("Output");
            string prefix = string.IsNullOrWhiteSpace(settings.Text("Prefix"))
                ? "P"
                : settings.Text("Prefix").Trim();
            int startNumber = settings.Integer("Start", 1);
            int roadStartNumber = settings.Integer("RoadStart", 1);
            double labelOffset = settings.Double("Offset", 3.0);
            string generationMode = settings.Text("Generation");
            string elevationMode = settings.Text("Elevation");
            string elevationSurface = settings.Text("ElevationSurface");
            string ngSurface = settings.Text("NGSurface");
            string designSurface = settings.Text("DesignSurface");
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
            ObjectId ngSurfaceId = ObjectId.Null;
            if (!string.IsNullOrWhiteSpace(ngSurface) && !string.Equals(ngSurface, "<None>", StringComparison.OrdinalIgnoreCase))
            {
                if (!PromptElevationSource(
                        document,
                        civilDocument,
                        "Select Civil 3D surface",
                        ngSurface,
                        out ngSurfaceId)) return;
            }

            ObjectId designSurfaceId = ObjectId.Null;
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
            ApplyLevelReferences(document.Database, sources, ngSurfaceId, designSurfaceId);
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
                NgSurfaceHandle = ngSurfaceId.IsNull
                    ? string.Empty
                    : ngSurfaceId.Handle.ToString(),
                DesignSurfaceHandle = designSurfaceId.IsNull
                    ? string.Empty
                    : designSurfaceId.Handle.ToString(),
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
                ObjectId tableId = CreateGroup(
                    document,
                    civilDocument,
                    link,
                    sources,
                    records,
                    tablePoint,
                    annotation.TextHeight);
                document.Editor.SetImpliedSelection(new[] { tableId });
                RuntimeAnnotationLinkManager.ClampLinkedAnnotations(document, true);
                document.Editor.Regen();
                document.Editor.WriteMessage(
                    "\nCE_VERTEXSETTINGOUT complete. Sources={0}; points={1}; radius dimensions={2}; linked table handle={3}.",
                    sources.Count,
                    records.Count,
                    radialDimensions,
                    tableId.Handle);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_VERTEXSETTINGOUT failed. No complete setting-out group was committed. {0}",
                    exception.Message);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_VERTEXSETTINGOUTREFRESH", CommandFlags.Modal | CommandFlags.Redraw)]
        public void RefreshSelected()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            PromptEntityResult selected = PromptLinkedTable(
                document.Editor,
                "\nSelect a linked CE vertex setting-out table to refresh: ");
            if (selected.Status != PromptStatus.OK) return;
            try
            {
                int points;
                int dimensions;
                RefreshTable(document, selected.ObjectId, out points, out dimensions);
                RuntimeAnnotationLinkManager.ClampLinkedAnnotations(document, true);
                document.Editor.Regen();
                document.Editor.WriteMessage(
                    "\nCE_VERTEXSETTINGOUTREFRESH complete. Points={0}; radius dimensions={1}.",
                    points,
                    dimensions);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage("\nCE_VERTEXSETTINGOUTREFRESH stopped. " + exception.Message);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_VERTEXSETTINGOUTEXPORT", CommandFlags.Modal)]
        public void ExportSelected()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            PromptEntityResult selected = PromptLinkedTable(
                document.Editor,
                "\nSelect a linked CE vertex setting-out table to export: ");
            if (selected.Status != PromptStatus.OK) return;
            try
            {
                int points;
                int dimensions;
                RefreshTable(document, selected.ObjectId, out points, out dimensions);
                IList<IList<string>> cells;
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    Table table = transaction.GetObject(
                        selected.ObjectId,
                        OpenMode.ForRead,
                        false) as Table;
                    cells = ReadTableCells(table);
                }

                var save = new PromptSaveFileOptions(
                    "\nSelect vertex setting-out Excel workbook path: ")
                {
                    Filter = "Excel Workbook (*.xlsx)|*.xlsx",
                    DialogCaption = "Export CE Vertex Setting-Out",
                    InitialFileName = "CE-Vertex-Setting-Out.xlsx"
                };
                PromptFileNameResult result = document.Editor.GetFileNameForSave(save);
                if (result.Status != PromptStatus.OK) return;
                string path = result.StringResult;
                if (!path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)) path += ".xlsx";
                SimpleXlsxWriter.Write(path, "Setting Out", cells);
                document.Editor.WriteMessage(
                    "\nCE_VERTEXSETTINGOUTEXPORT complete. Points={0}; file={1}",
                    points,
                    path);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage("\nCE_VERTEXSETTINGOUTEXPORT stopped. " + exception.Message);
            }
        }

        internal static int RefreshAll(Document document)
        {
            if (document == null) return 0;
            List<ObjectId> tables = FindLinkedTables(document.Database);
            int refreshed = 0;
            foreach (ObjectId tableId in tables)
            {
                try
                {
                    int points;
                    int dimensions;
                    RefreshTable(document, tableId, out points, out dimensions);
                    refreshed++;
                }
                catch
                {
                    // One stale group must not prevent other linked groups from refreshing.
                }
            }
            return refreshed;
        }

        private static ObjectId CreateGroup(
            Document document,
            CivilDocument civilDocument,
            VertexSettingLink link,
            IList<VertexSettingSource> sources,
            IList<VertexSettingRecord> records,
            Point3d tablePoint,
            double paperTextHeight)
        {
            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                EnsureRegApp(document.Database, transaction);
                BlockTableRecord modelSpace = GetModelSpace(document.Database, transaction, OpenMode.ForWrite);
                double textHeight = PaperAnnotationScale.ModelTextHeight(
                    document.Database,
                    paperTextHeight);

                foreach (VertexSettingRecord record in records)
                    CreateOutput(document.Database, civilDocument, transaction, modelSpace, link, record, textHeight);
                foreach (VertexRadialDimension dimension in sources.SelectMany(item => item.Dimensions))
                    CreateDimension(document.Database, transaction, modelSpace, link, dimension, textHeight);

                var table = new Table();
                table.SetDatabaseDefaults(document.Database);
                table.TableStyle = document.Database.Tablestyle;
                table.Position = tablePoint;
                PaperAnnotationScale.SetAnnotative(table);
                ObjectId tableId = modelSpace.AppendEntity(table);
                transaction.AddNewlyCreatedDBObject(table, true);
                WriteTableLink(table, transaction, link);
                PopulateTable(table, records, textHeight, link);
                transaction.Commit();
                return tableId;
            }
        }

        private static void RefreshTable(
            Document document,
            ObjectId tableId,
            out int pointCount,
            out int dimensionCount)
        {
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (civilDocument == null)
                throw new InvalidOperationException("No active Civil 3D document is available.");

            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                Table table = transaction.GetObject(
                    tableId,
                    OpenMode.ForWrite,
                    false) as Table;
                if (table == null) throw new InvalidOperationException("The selected object is not an AutoCAD table.");
                VertexSettingLink link = ReadTableLink(table);
                List<ObjectId> sourceIds = link.SourceHandles
                    .Select(handle => ResolveHandle(document.Database, handle))
                    .Where(id => !id.IsNull && !id.IsErased)
                    .ToList();
                if (sourceIds.Count == 0)
                    throw new InvalidOperationException("None of the linked source polylines or feature lines are available.");

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
                    throw new InvalidOperationException("The linked sources produced no current setting-out geometry.");
                List<VertexSettingRecord> records = FlattenAndName(
                    sources,
                    link.Prefix,
                    link.StartNumber,
                    link.NumberingMode,
                    link.RoadStartNumber,
                    link.SequenceMode,
                    link.StartRecordKey);

                EnsureRegApp(document.Database, transaction);
                BlockTableRecord modelSpace = GetModelSpace(document.Database, transaction, OpenMode.ForWrite);
                Dictionary<string, ObjectId> outputs;
                Dictionary<string, ObjectId> dimensions;
                InventoryGroup(modelSpace, transaction, link.GroupId, out outputs, out dimensions);
                AnnotationOptions annotation = AnnotationSettingsStore.Read(document.Database);
                double textHeight = PaperAnnotationScale.ModelTextHeight(
                    document.Database,
                    annotation == null ? 2.0 : annotation.TextHeight);

                var liveOutputKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (VertexSettingRecord record in records)
                {
                    liveOutputKeys.Add(record.Key);
                    ObjectId existing;
                    if (outputs.TryGetValue(record.Key, out existing) &&
                        UpdateOutput(transaction, existing, link, record, textHeight))
                        continue;
                    CaptureCurrentAnnotationOffset(
                        transaction,
                        existing,
                        record);
                    EraseIfPossible(transaction, existing);
                    CreateOutput(document.Database, civilDocument, transaction, modelSpace, link, record, textHeight);
                }
                foreach (KeyValuePair<string, ObjectId> stale in outputs)
                    if (!liveOutputKeys.Contains(stale.Key)) EraseIfPossible(transaction, stale.Value);

                var liveDimensionKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (VertexRadialDimension dimension in sources.SelectMany(item => item.Dimensions))
                {
                    liveDimensionKeys.Add(dimension.Key);
                    ObjectId existing;
                    if (dimensions.TryGetValue(dimension.Key, out existing) &&
                        UpdateDimension(transaction, existing, dimension, textHeight))
                        continue;
                    EraseIfPossible(transaction, existing);
                    CreateDimension(document.Database, transaction, modelSpace, link, dimension, textHeight);
                }
                foreach (KeyValuePair<string, ObjectId> stale in dimensions)
                    if (!liveDimensionKeys.Contains(stale.Key)) EraseIfPossible(transaction, stale.Value);

                PopulateTable(table, records, textHeight, link);
                transaction.Commit();
                pointCount = records.Count;
                dimensionCount = liveDimensionKeys.Count;
            }
            if (string.Equals(link.OutputType, "COGO", StringComparison.OrdinalIgnoreCase))
            {
                try { CogoPointProjectStyleCommands.ApplySelectedStyles(document, false); }
                catch { }
            }
        }

        private static List<VertexSettingRecord> FlattenAndName(
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
                : RemoveDuplicateClosingVertices(source.Records);
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

        private static List<VertexSettingRecord> RemoveDuplicateClosingVertices(
            IEnumerable<VertexSettingRecord> values)
        {
            var result = new List<VertexSettingRecord>();
            foreach (VertexSettingRecord record in values ?? Enumerable.Empty<VertexSettingRecord>())
            {
                if (string.Equals(record.Kind, "VERTEX", StringComparison.OrdinalIgnoreCase) &&
                    result.Any(existing =>
                        string.Equals(existing.Kind, "VERTEX", StringComparison.OrdinalIgnoreCase) &&
                        existing.Point.DistanceTo(record.Point) <= 1e-7))
                    continue;
                result.Add(record);
            }
            return result;
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

        private static ObjectId CreateOutput(
            Database database,
            CivilDocument civilDocument,
            Transaction transaction,
            BlockTableRecord modelSpace,
            VertexSettingLink link,
            VertexSettingRecord record,
            double textHeight)
        {
            if (string.Equals(link.OutputType, "COGO", StringComparison.OrdinalIgnoreCase))
            {
                ObjectId id = civilDocument.CogoPoints.Add(
                    record.Point,
                    record.PointName,
                    true);
                CivilCogoPoint point = transaction.GetObject(id, OpenMode.ForWrite, false) as CivilCogoPoint;
                if (point == null) throw new InvalidOperationException("Civil 3D did not return the created COGO point.");
                point.RawDescription = record.PointName;
                try { point.PointName = record.PointName; } catch { }
                WriteOutputLink(
                    point, transaction, link.GroupId, record.Key, record.Point);
                return id;
            }

            if (string.Equals(link.OutputType, "MLeader", StringComparison.OrdinalIgnoreCase))
            {
                var leader = new MLeader();
                leader.SetDatabaseDefaults(database);
                leader.MLeaderStyle = database.MLeaderstyle;
                // ObjectId.Null is AutoCAD's native closed-filled arrow. Do not
                // inherit DIMBLK because a project DIMSTYLE may use architectural ticks.
                leader.ArrowSymbolId = ObjectId.Null;
                leader.ContentType = ContentType.MTextContent;
                var text = new MText();
                text.SetDatabaseDefaults(database);
                text.TextHeight = textHeight;
                text.Attachment = AttachmentPoint.BottomLeft;
                text.Contents = LabelText(record, link);
                Point3d location = OutputLocation(record, link.LabelOffset);
                text.Location = location;
                leader.MText = text;
                leader.TextLocation = location;
                PaperAnnotationScale.SetAnnotative(leader);
                ObjectId id = modelSpace.AppendEntity(leader);
                transaction.AddNewlyCreatedDBObject(leader, true);
                leader.AddLeaderLine(record.Point);
                WriteOutputLink(
                    leader, transaction, link.GroupId, record.Key, record.Point);
                return id;
            }

            var mtext = new MText();
            mtext.SetDatabaseDefaults(database);
            mtext.Location = record.Point;
            mtext.Attachment = AnchoredAttachment(record, link.LabelOffset);
            mtext.TextHeight = textHeight;
            mtext.Contents = AnchoredMText(LabelText(record, link));
            PaperAnnotationScale.SetAnnotative(mtext);
            ObjectId textId = modelSpace.AppendEntity(mtext);
            transaction.AddNewlyCreatedDBObject(mtext, true);
            WriteOutputLink(
                mtext, transaction, link.GroupId, record.Key, record.Point);
            return textId;
        }

        private static bool UpdateOutput(
            Transaction transaction,
            ObjectId id,
            VertexSettingLink link,
            VertexSettingRecord record,
            double textHeight)
        {
            if (id.IsNull || id.IsErased) return false;
            DBObject value;
            try { value = transaction.GetObject(id, OpenMode.ForWrite, false); }
            catch { return false; }

            CivilCogoPoint cogo = value as CivilCogoPoint;
            if (cogo != null && string.Equals(link.OutputType, "COGO", StringComparison.OrdinalIgnoreCase))
            {
                cogo.Easting = record.Point.X;
                cogo.Northing = record.Point.Y;
                cogo.Elevation = record.Point.Z;
                cogo.RawDescription = record.PointName;
                try { cogo.PointName = record.PointName; } catch { }
                WriteOutputLink(
                    cogo, transaction, link.GroupId, record.Key, record.Point);
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
                return true;
            }

            // Recreating MLeaders on refresh is intentional: it guarantees the
            // arrowhead remains attached to the recalculated source point after
            // vertices are inserted, deleted or reordered.
            return false;
        }

        private static ObjectId CreateDimension(
            Database database,
            Transaction transaction,
            BlockTableRecord modelSpace,
            VertexSettingLink link,
            VertexRadialDimension dimension,
            double textHeight)
        {
            var radial = new RadialDimension(
                dimension.Center,
                dimension.ChordPoint,
                Math.Max(textHeight * 3.0, dimension.Radius * 0.15),
                string.Empty,
                database.Dimstyle);
            radial.SetDatabaseDefaults(database);
            PositionRadialText(radial, dimension, textHeight);
            SetClosedFilledDimensionArrow(radial, database);
            PaperAnnotationScale.SetAnnotative(radial);
            ObjectId id = modelSpace.AppendEntity(radial);
            transaction.AddNewlyCreatedDBObject(radial, true);
            WriteDimensionLink(radial, transaction, link.GroupId, dimension.Key);
            return id;
        }

        private static bool UpdateDimension(
            Transaction transaction,
            ObjectId id,
            VertexRadialDimension dimension,
            double textHeight)
        {
            if (id.IsNull || id.IsErased) return false;
            RadialDimension radial;
            try
            {
                radial = transaction.GetObject(id, OpenMode.ForWrite, false) as RadialDimension;
            }
            catch
            {
                return false;
            }
            if (radial == null) return false;
            radial.Center = dimension.Center;
            radial.ChordPoint = dimension.ChordPoint;
            radial.LeaderLength = Math.Max(textHeight * 3.0, dimension.Radius * 0.15);
            PositionRadialText(radial, dimension, textHeight);
            SetClosedFilledDimensionArrow(radial, radial.Database);
            return true;
        }

        private static void PositionRadialText(
            RadialDimension radial,
            VertexRadialDimension dimension,
            double textHeight)
        {
            if (radial == null || dimension == null) return;
            Vector3d direction = dimension.ChordPoint - dimension.Center;
            if (direction.Length <= 1e-8) direction = Vector3d.XAxis;
            direction = direction.GetNormal();
            try
            {
                radial.TextPosition = dimension.Center +
                    direction * (dimension.Radius * 0.50);
                SetDimensionTextMovementNoLeader(radial);
            }
            catch { }
        }

        private static void PopulateTable(
            Table table,
            IList<VertexSettingRecord> records,
            double textHeight,
            VertexSettingLink link)
        {
            table.SetSize(records.Count + 2, 11);
            table.SetRowHeight(Math.Max(textHeight * 1.8, 0.001));
            table.SetColumnWidth(Math.Max(textHeight * 8.0, 0.001));
            table.Columns[0].Width = Math.Max(textHeight * 9.0, 0.001);
            table.Columns[1].Width = Math.Max(textHeight * 18.0, 0.001);
            table.Columns[2].Width = Math.Max(textHeight * 14.0, 0.001);
            table.Columns[8].Width = Math.Max(textHeight * 11.0, 0.001);
            table.Columns[10].Width = Math.Max(textHeight * 12.0, 0.001);
            table.Cells[0, 0].TextString = "CE VERTEX SETTING-OUT - " + (link.OutputType ?? string.Empty).ToUpperInvariant();
            table.MergeCells(CellRange.Create(table, 0, 0, 0, 10));
            bool yFirst = string.Equals(
                link.CoordinateOrder,
                "Y then X",
                StringComparison.OrdinalIgnoreCase);
            string[] headings =
            {
                "POINT NAME", "TYPE", "SOURCE", "SEGMENT",
                yFirst ? "Y" : "X",
                yFirst ? "X" : "Y",
                "NG LEVEL", "DESIGN LEVEL", "DIFFERENCE", "RADIUS", "SEGMENT LENGTH"
            };
            for (int column = 0; column < headings.Length; column++)
                table.Cells[1, column].TextString = headings[column];

            for (int index = 0; index < records.Count; index++)
            {
                VertexSettingRecord record = records[index];
                int row = index + 2;
                table.Cells[row, 0].TextString = record.PointName;
                table.Cells[row, 1].TextString = record.Kind;
                table.Cells[row, 2].TextString = record.SourceName;
                table.Cells[row, 3].TextString = record.SegmentIndex.ToString(CultureInfo.InvariantCulture);
                double displayX = DisplayX(record.Point, link);
                double displayY = DisplayY(record.Point, link);
                // Keep the numeric coordinate columns fixed and swap only their
                // displayed X/Y headings when requested. Drawing coordinates never change.
                table.Cells[row, 4].TextString = displayX
                    .ToString("N3", CultureInfo.CurrentCulture);
                table.Cells[row, 5].TextString = displayY
                    .ToString("N3", CultureInfo.CurrentCulture);
                table.Cells[row, 6].TextString = record.NgLevel.HasValue
                    ? record.NgLevel.Value.ToString("N3", CultureInfo.CurrentCulture)
                    : string.Empty;
                table.Cells[row, 7].TextString = record.DesignLevel.HasValue
                    ? record.DesignLevel.Value.ToString("N3", CultureInfo.CurrentCulture)
                    : record.Point.Z.ToString("N3", CultureInfo.CurrentCulture);
                table.Cells[row, 8].TextString = record.NgLevel.HasValue
                    ? ((record.DesignLevel ?? record.Point.Z) - record.NgLevel.Value).ToString("+0.000;-0.000;0.000", CultureInfo.CurrentCulture)
                    : string.Empty;
                table.Cells[row, 9].TextString = record.Radius.HasValue
                    ? record.Radius.Value.ToString("N3", CultureInfo.CurrentCulture)
                    : string.Empty;
                table.Cells[row, 10].TextString = record.SegmentLength > 0.0
                    ? record.SegmentLength.ToString("N3", CultureInfo.CurrentCulture)
                    : string.Empty;
            }

            for (int row = 0; row < table.Rows.Count; row++)
                for (int column = 0; column < table.Columns.Count; column++)
                {
                    table.Cells[row, column].Alignment = CellAlignment.MiddleCenter;
                    table.Cells[row, column].TextHeight = textHeight;
                }
            ForceTableGraphics(table);
        }

        private static void ForceTableGraphics(Table table)
        {
            if (table == null) return;
            try { table.GenerateLayout(); } catch { }
            try { table.RecordGraphicsModified(true); } catch { }
            try
            {
                MethodInfo method = table.GetType().GetMethod(
                    "RecomputeTableBlock",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new[] { typeof(bool) },
                    null);
                if (method != null) method.Invoke(table, new object[] { true });
            }
            catch { }
        }

        private static string LabelText(
            VertexSettingRecord record,
            VertexSettingLink link)
        {
            double displayX = DisplayX(record.Point, link);
            double displayY = DisplayY(record.Point, link);
            bool yFirst = string.Equals(
                link.CoordinateOrder,
                "Y then X",
                StringComparison.OrdinalIgnoreCase);
            string first = (yFirst ? "Y=" : "X=") +
                displayX.ToString("N3", CultureInfo.CurrentCulture);
            string second = (yFirst ? "X=" : "Y=") +
                displayY.ToString("N3", CultureInfo.CurrentCulture);
            return string.Join(
                "\\P",
                record.PointName,
                first,
                second,
                "Z=" + record.Point.Z.ToString("N3", CultureInfo.CurrentCulture));
        }

        private static double DisplayX(
            Point3d point,
            VertexSettingLink link)
        {
            return string.Equals(
                link.XSign,
                "Reverse X sign",
                StringComparison.OrdinalIgnoreCase)
                ? -point.X
                : point.X;
        }

        private static double DisplayY(
            Point3d point,
            VertexSettingLink link)
        {
            return string.Equals(
                link.YSign,
                "Reverse Y sign",
                StringComparison.OrdinalIgnoreCase)
                ? -point.Y
                : point.Y;
        }

        private static void SetClosedFilledDimensionArrow(
            Dimension dimension,
            Database database)
        {
            if (dimension == null || database == null) return;
            // Force the AutoCAD closed-filled default independently of DIMSTYLE.
            ObjectId arrow = ObjectId.Null;
            foreach (string name in new[] { "Dimblk", "Dimblk1", "Dimblk2" })
            {
                try
                {
                    PropertyInfo property = dimension.GetType().GetProperty(
                        name,
                        BindingFlags.Public | BindingFlags.Instance);
                    if (property == null || !property.CanWrite ||
                        property.PropertyType != typeof(ObjectId)) continue;
                    property.SetValue(dimension, arrow, null);
                }
                catch { }
            }
        }

        private static void SetDimensionTextMovementNoLeader(Dimension dimension)
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

        private static Point3d LabelLocation(Point3d point, double offset)
        {
            return point + new Vector3d(offset, offset, 0.0);
        }

        private static Point3d OutputLocation(
            VertexSettingRecord record,
            double defaultOffset)
        {
            Vector3d offset = record.AnnotationOffset ??
                new Vector3d(defaultOffset, defaultOffset, 0.0);
            double maximum = Math.Max(defaultOffset * 3.0, defaultOffset);
            if (offset.Length > maximum)
                offset = offset.GetNormal() * maximum;
            return record.Point + offset;
        }

        private static void CaptureCurrentAnnotationOffset(
            Transaction transaction,
            ObjectId id,
            VertexSettingRecord record)
        {
            if (transaction == null || record == null || id.IsNull || id.IsErased)
                return;
            Entity entity;
            try
            {
                entity = transaction.GetObject(
                    id,
                    OpenMode.ForRead,
                    false) as Entity;
            }
            catch
            {
                return;
            }
            if (entity == null) return;
            Point3d anchor;
            if (!TryReadOutputAnchor(entity, out anchor)) return;
            MText mtext = entity as MText;
            if (mtext != null)
            {
                record.AnnotationOffset = mtext.Location - anchor;
                return;
            }
            MLeader leader = entity as MLeader;
            if (leader != null)
            {
                try
                {
                    record.AnnotationOffset = leader.TextLocation - anchor;
                }
                catch
                {
                    // Keep the default offset when a proxy leader blocks access.
                }
            }
        }

        private static bool TryReadOutputAnchor(
            Entity entity,
            out Point3d anchor)
        {
            anchor = Point3d.Origin;
            if (entity == null) return false;
            ResultBuffer buffer = entity.GetXDataForApplication(AppName);
            if (buffer == null) return false;
            TypedValue[] values = buffer.AsArray();
            if (values.Length < 8) return false;
            try
            {
                anchor = new Point3d(
                    Convert.ToDouble(values[5].Value, CultureInfo.InvariantCulture),
                    Convert.ToDouble(values[6].Value, CultureInfo.InvariantCulture),
                    Convert.ToDouble(values[7].Value, CultureInfo.InvariantCulture));
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void ApplyGenerationMode(
            IList<VertexSettingSource> sources,
            string mode)
        {
            if (sources == null ||
                !string.Equals(
                    mode,
                    "Polyline vertices only",
                    StringComparison.OrdinalIgnoreCase))
                return;
            foreach (VertexSettingSource source in sources)
            {
                source.Records = source.Records
                    .Where(record => string.Equals(
                        record.Kind,
                        "VERTEX",
                        StringComparison.OrdinalIgnoreCase))
                    .ToList();
                source.Dimensions = new List<VertexRadialDimension>();
            }
        }

        private static bool PromptElevationSource(
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

        private static void ApplyElevationReference(
            Database database,
            IList<VertexSettingSource> sources,
            string mode,
            ObjectId sourceId)
        {
            if (database == null || sources == null || sourceId.IsNull ||
                string.IsNullOrWhiteSpace(mode) ||
                string.Equals(
                    mode,
                    "Source geometry",
                    StringComparison.OrdinalIgnoreCase))
                return;
            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                DBObject reference;
                try
                {
                    reference = transaction.GetObject(
                        sourceId,
                        OpenMode.ForRead,
                        false);
                }
                catch
                {
                    return;
                }
                Autodesk.Civil.DatabaseServices.Surface surface =
                    reference as Autodesk.Civil.DatabaseServices.Surface;
                Autodesk.Civil.DatabaseServices.FeatureLine featureLine =
                    reference as Autodesk.Civil.DatabaseServices.FeatureLine;
                if (surface == null && featureLine == null) return;

                foreach (VertexSettingSource source in sources)
                {
                    foreach (VertexSettingRecord record in source.Records)
                    {
                        double elevation;
                        if (TryReadReferenceElevation(
                                surface,
                                featureLine,
                                record.Point,
                                out elevation))
                        {
                            record.Point = new Point3d(
                                record.Point.X,
                                record.Point.Y,
                                elevation);
                        }
                    }
                    foreach (VertexRadialDimension dimension in source.Dimensions)
                    {
                        double centerElevation;
                        if (TryReadReferenceElevation(
                                surface,
                                featureLine,
                                dimension.Center,
                                out centerElevation))
                        {
                            dimension.Center = new Point3d(
                                dimension.Center.X,
                                dimension.Center.Y,
                                centerElevation);
                        }
                        double chordElevation;
                        if (TryReadReferenceElevation(
                                surface,
                                featureLine,
                                dimension.ChordPoint,
                                out chordElevation))
                        {
                            dimension.ChordPoint = new Point3d(
                                dimension.ChordPoint.X,
                                dimension.ChordPoint.Y,
                                chordElevation);
                        }
                    }
                }
            }
        }

        private static bool TryReadReferenceElevation(
            Autodesk.Civil.DatabaseServices.Surface surface,
            Autodesk.Civil.DatabaseServices.FeatureLine featureLine,
            Point3d point,
            out double elevation)
        {
            elevation = point.Z;
            try
            {
                if (surface != null)
                {
                    elevation = surface.FindElevationAtXY(point.X, point.Y);
                    return !double.IsNaN(elevation) &&
                           !double.IsInfinity(elevation);
                }
                if (featureLine != null)
                {
                    Point3d closest = featureLine.GetClosestPointTo(
                        new Point3d(point.X, point.Y, point.Z),
                        false);
                    elevation = closest.Z;
                    return !double.IsNaN(elevation) &&
                           !double.IsInfinity(elevation);
                }
            }
            catch
            {
                return false;
            }
            return false;
        }

        private static void ApplyLevelReferences(
            Database database,
            IList<VertexSettingSource> sources,
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

        private static AttachmentPoint AnchoredAttachment(VertexSettingRecord record, double offset)
        {
            Vector3d direction = record != null && record.AnnotationOffset.HasValue
                ? record.AnnotationOffset.Value
                : new Vector3d(offset, offset, 0.0);
            if (direction.X < 0.0 && direction.Y >= 0.0) return AttachmentPoint.BottomRight;
            if (direction.X < 0.0 && direction.Y < 0.0) return AttachmentPoint.TopRight;
            if (direction.X >= 0.0 && direction.Y < 0.0) return AttachmentPoint.TopLeft;
            return AttachmentPoint.BottomLeft;
        }

        private static string AnchoredMText(string contents)
        {
            string pad = "\\~\\~";
            return pad + (contents ?? string.Empty).Replace("\\P", "\\P" + pad);
        }

        private static void InventoryGroup(
            BlockTableRecord modelSpace,
            Transaction transaction,
            string groupId,
            out Dictionary<string, ObjectId> outputs,
            out Dictionary<string, ObjectId> dimensions)
        {
            outputs = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);
            dimensions = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);
            foreach (ObjectId id in modelSpace)
            {
                Entity entity;
                try { entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity; }
                catch { continue; }
                if (entity == null) continue;
                string type;
                string group;
                string key;
                if (!TryReadEntityLink(entity, out type, out group, out key) ||
                    !string.Equals(group, groupId, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (string.Equals(type, "OUTPUT", StringComparison.OrdinalIgnoreCase))
                    outputs[key] = id;
                else if (string.Equals(type, "DIM", StringComparison.OrdinalIgnoreCase))
                    dimensions[key] = id;
            }
        }

        private static void WriteTableLink(
            Table table,
            Transaction transaction,
            VertexSettingLink link)
        {
            var values = new List<TypedValue>
            {
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, AppName),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, "TABLE"),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, SchemaVersion),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, link.GroupId),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, link.OutputType),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, link.Prefix),
                new TypedValue((int)DxfCode.ExtendedDataInteger32, link.StartNumber),
                new TypedValue((int)DxfCode.ExtendedDataReal, link.LabelOffset)
            };
            values.Add(new TypedValue(
                (int)DxfCode.ExtendedDataAsciiString,
                "GEN=" + (link.GenerationMode ?? string.Empty)));
            values.Add(new TypedValue(
                (int)DxfCode.ExtendedDataAsciiString,
                "ELEV=" + (link.ElevationMode ?? string.Empty)));
            values.Add(new TypedValue(
                (int)DxfCode.ExtendedDataAsciiString,
                "ELEVHANDLE=" + (link.ElevationSourceHandle ?? string.Empty)));
            values.Add(new TypedValue(
                (int)DxfCode.ExtendedDataAsciiString,
                "NGHANDLE=" + (link.NgSurfaceHandle ?? string.Empty)));
            values.Add(new TypedValue(
                (int)DxfCode.ExtendedDataAsciiString,
                "DESIGNHANDLE=" + (link.DesignSurfaceHandle ?? string.Empty)));
            values.Add(new TypedValue(
                (int)DxfCode.ExtendedDataAsciiString,
                "ORDER=" + (link.CoordinateOrder ?? "X then Y")));
            values.Add(new TypedValue(
                (int)DxfCode.ExtendedDataAsciiString,
                "XSIGN=" + (link.XSign ?? "Keep X sign")));
            values.Add(new TypedValue(
                (int)DxfCode.ExtendedDataAsciiString,
                "YSIGN=" + (link.YSign ?? "Keep Y sign")));
            values.Add(new TypedValue(
                (int)DxfCode.ExtendedDataAsciiString,
                "NUMMODE=" + (link.NumberingMode ?? "Single sequence")));
            values.Add(new TypedValue(
                (int)DxfCode.ExtendedDataAsciiString,
                "ROADSTART=" + link.RoadStartNumber.ToString(CultureInfo.InvariantCulture)));
            values.Add(new TypedValue(
                (int)DxfCode.ExtendedDataAsciiString,
                "SEQ=" + (link.SequenceMode ?? "Auto by road orientation")));
            values.Add(new TypedValue(
                (int)DxfCode.ExtendedDataAsciiString,
                "STARTKEY=" + (link.StartRecordKey ?? string.Empty)));
            foreach (string handle in link.SourceHandles)
                values.Add(new TypedValue(
                    (int)DxfCode.ExtendedDataAsciiString,
                    "SRC=" + handle));
            table.XData = new ResultBuffer(values.ToArray());
        }

        private static VertexSettingLink ReadTableLink(Table table)
        {
            if (table == null) throw new InvalidOperationException("The selected table is unavailable.");
            ResultBuffer buffer = table.GetXDataForApplication(AppName);
            if (buffer == null) throw new InvalidOperationException("The selected table is not linked by CE vertex setting-out.");
            TypedValue[] values = buffer.AsArray();
            if (values.Length < 8 || !string.Equals(Convert.ToString(values[1].Value), "TABLE", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The selected table has invalid CE vertex setting-out link data.");
            var link = new VertexSettingLink
            {
                GroupId = Convert.ToString(values[3].Value),
                OutputType = Convert.ToString(values[4].Value),
                Prefix = Convert.ToString(values[5].Value),
                StartNumber = Convert.ToInt32(values[6].Value, CultureInfo.InvariantCulture),
                LabelOffset = Convert.ToDouble(values[7].Value, CultureInfo.InvariantCulture),
                GenerationMode = "Engineering setting-out points",
                ElevationMode = "Source geometry",
                ElevationSourceHandle = string.Empty,
                NgSurfaceHandle = string.Empty,
                DesignSurfaceHandle = string.Empty,
                CoordinateOrder = "X then Y",
                XSign = "Keep X sign",
                YSign = "Keep Y sign",
                NumberingMode = "Single sequence",
                RoadStartNumber = 1,
                SequenceMode = "Auto by road orientation",
                StartRecordKey = string.Empty,
                SourceHandles = new List<string>()
            };
            for (int index = 8; index < values.Length; index++)
            {
                string value = Convert.ToString(values[index].Value);
                if (string.IsNullOrWhiteSpace(value)) continue;
                if (value.StartsWith("GEN=", StringComparison.OrdinalIgnoreCase))
                    link.GenerationMode = value.Substring(4);
                else if (value.StartsWith("ELEV=", StringComparison.OrdinalIgnoreCase))
                    link.ElevationMode = value.Substring(5);
                else if (value.StartsWith("ELEVHANDLE=", StringComparison.OrdinalIgnoreCase))
                    link.ElevationSourceHandle = value.Substring(11);
                else if (value.StartsWith("NGHANDLE=", StringComparison.OrdinalIgnoreCase))
                    link.NgSurfaceHandle = value.Substring(9);
                else if (value.StartsWith("DESIGNHANDLE=", StringComparison.OrdinalIgnoreCase))
                    link.DesignSurfaceHandle = value.Substring(13);
                else if (value.StartsWith("ORDER=", StringComparison.OrdinalIgnoreCase))
                    link.CoordinateOrder = value.Substring(6);
                else if (value.StartsWith("XSIGN=", StringComparison.OrdinalIgnoreCase))
                    link.XSign = value.Substring(6);
                else if (value.StartsWith("YSIGN=", StringComparison.OrdinalIgnoreCase))
                    link.YSign = value.Substring(6);
                else if (value.StartsWith("NUMMODE=", StringComparison.OrdinalIgnoreCase))
                    link.NumberingMode = value.Substring(8);
                else if (value.StartsWith("ROADSTART=", StringComparison.OrdinalIgnoreCase))
                {
                    int roadStart;
                    if (int.TryParse(value.Substring(10), NumberStyles.Integer, CultureInfo.InvariantCulture, out roadStart) && roadStart > 0)
                        link.RoadStartNumber = roadStart;
                }
                else if (value.StartsWith("SEQ=", StringComparison.OrdinalIgnoreCase))
                    link.SequenceMode = value.Substring(4);
                else if (value.StartsWith("STARTKEY=", StringComparison.OrdinalIgnoreCase))
                    link.StartRecordKey = value.Substring(9);
                else if (value.StartsWith("SRC=", StringComparison.OrdinalIgnoreCase))
                    link.SourceHandles.Add(value.Substring(4));
                else
                    link.SourceHandles.Add(value);
            }
            return link;
        }

        private static VertexSettingLink ReadLink(Database database, ObjectId tableId)
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

        private static void WriteOutputLink(
            Entity entity,
            Transaction transaction,
            string groupId,
            string key,
            Point3d anchor)
        {
            entity.XData = LinkBuffer("OUTPUT", groupId, key, anchor);
        }

        private static void WriteDimensionLink(
            Entity entity,
            Transaction transaction,
            string groupId,
            string key)
        {
            entity.XData = LinkBuffer("DIM", groupId, key, null);
        }

        private static ResultBuffer LinkBuffer(
            string type,
            string groupId,
            string key,
            Point3d? anchor)
        {
            var values = new List<TypedValue>
            {
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, AppName),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, type),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, SchemaVersion),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, groupId),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, key)
            };
            if (anchor.HasValue)
            {
                values.Add(new TypedValue(
                    (int)DxfCode.ExtendedDataReal,
                    anchor.Value.X));
                values.Add(new TypedValue(
                    (int)DxfCode.ExtendedDataReal,
                    anchor.Value.Y));
                values.Add(new TypedValue(
                    (int)DxfCode.ExtendedDataReal,
                    anchor.Value.Z));
            }
            return new ResultBuffer(values.ToArray());
        }

        private static bool TryReadEntityLink(
            Entity entity,
            out string type,
            out string groupId,
            out string key)
        {
            type = string.Empty;
            groupId = string.Empty;
            key = string.Empty;
            ResultBuffer buffer = entity.GetXDataForApplication(AppName);
            if (buffer == null) return false;
            TypedValue[] values = buffer.AsArray();
            if (values.Length < 5) return false;
            type = Convert.ToString(values[1].Value);
            groupId = Convert.ToString(values[3].Value);
            key = Convert.ToString(values[4].Value);
            return !string.IsNullOrWhiteSpace(groupId) && !string.IsNullOrWhiteSpace(key);
        }

        private static List<ObjectId> FindLinkedTables(Database database)
        {
            var result = new List<ObjectId>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTableRecord modelSpace = GetModelSpace(database, transaction, OpenMode.ForRead);
                foreach (ObjectId id in modelSpace)
                {
                    Table table;
                    try { table = transaction.GetObject(id, OpenMode.ForRead, false) as Table; }
                    catch { continue; }
                    if (table == null) continue;
                    ResultBuffer data = table.GetXDataForApplication(AppName);
                    if (data == null) continue;
                    TypedValue[] values = data.AsArray();
                    if (values.Length > 1 && string.Equals(Convert.ToString(values[1].Value), "TABLE", StringComparison.OrdinalIgnoreCase))
                        result.Add(id);
                }
            }
            return result;
        }

        private static PromptEntityResult PromptLinkedTable(Editor editor, string message)
        {
            var options = new PromptEntityOptions(message);
            options.SetRejectMessage("\nSelect an AutoCAD table created by CE_VERTEXSETTINGOUT.");
            options.AddAllowedClass(typeof(Table), true);
            return editor.GetEntity(options);
        }

        private static PromptSelectionResult GetSelection(Editor editor, string message)
        {
            PromptSelectionResult implied = editor.SelectImplied();
            if (implied.Status == PromptStatus.OK && implied.Value != null && implied.Value.Count > 0)
                return implied;
            return editor.GetSelection(new PromptSelectionOptions
            {
                MessageForAdding = message,
                AllowDuplicates = false,
                RejectObjectsFromNonCurrentSpace = true
            });
        }

        private static IList<IList<string>> ReadTableCells(Table table)
        {
            var result = new List<IList<string>>();
            if (table == null) return result;
            for (int row = 0; row < table.Rows.Count; row++)
            {
                var values = new List<string>();
                for (int column = 0; column < table.Columns.Count; column++)
                    values.Add(table.Cells[row, column].TextString ?? string.Empty);
                result.Add(values);
            }
            return result;
        }

        private static void EnsureRegApp(Database database, Transaction transaction)
        {
            RegAppTable table = transaction.GetObject(
                database.RegAppTableId,
                OpenMode.ForRead,
                false) as RegAppTable;
            if (table == null || table.Has(AppName)) return;
            table.UpgradeOpen();
            var record = new RegAppTableRecord { Name = AppName };
            table.Add(record);
            transaction.AddNewlyCreatedDBObject(record, true);
        }

        private static BlockTableRecord GetModelSpace(
            Database database,
            Transaction transaction,
            OpenMode mode)
        {
            BlockTable table = transaction.GetObject(
                database.BlockTableId,
                OpenMode.ForRead,
                false) as BlockTable;
            if (table == null) throw new InvalidOperationException("The drawing block table is unavailable.");
            BlockTableRecord modelSpace = transaction.GetObject(
                table[BlockTableRecord.ModelSpace],
                mode,
                false) as BlockTableRecord;
            if (modelSpace == null) throw new InvalidOperationException("Model space is unavailable.");
            return modelSpace;
        }

        private static ObjectId ResolveHandle(Database database, string handleText)
        {
            if (string.IsNullOrWhiteSpace(handleText)) return ObjectId.Null;
            try
            {
                long value = long.Parse(handleText, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                return database.GetObjectId(false, new Handle(value), 0);
            }
            catch
            {
                return ObjectId.Null;
            }
        }

        private static void EraseIfPossible(Transaction transaction, ObjectId id)
        {
            if (id.IsNull || id.IsErased) return;
            try
            {
                DBObject value = transaction.GetObject(id, OpenMode.ForWrite, false);
                if (value != null && !value.IsErased) value.Erase();
            }
            catch
            {
                // A locked or referenced stale entity is left untouched.
            }
        }

        private static Document ActiveDocument()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
        }

        private sealed class VertexSettingLink
        {
            public string GroupId { get; set; }
            public string OutputType { get; set; }
            public string Prefix { get; set; }
            public int StartNumber { get; set; }
            public double LabelOffset { get; set; }
            public string GenerationMode { get; set; }
            public string ElevationMode { get; set; }
            public string ElevationSourceHandle { get; set; }
            public string NgSurfaceHandle { get; set; }
            public string DesignSurfaceHandle { get; set; }
            public string CoordinateOrder { get; set; }
            public string XSign { get; set; }
            public string YSign { get; set; }
            public string NumberingMode { get; set; }
            public int RoadStartNumber { get; set; }
            public string SequenceMode { get; set; }
            public string StartRecordKey { get; set; }
            public IList<string> SourceHandles { get; set; }
        }
    }
}

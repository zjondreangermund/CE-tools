using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
        private const string SchemaVersion = "1";

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

            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Vertex Setting-Out Settings",
                "All vertices are included. Arcs longer than 10 m receive a midpoint; every arc receives a centre point and radius dimension. Tangents longer than 20 m receive a midpoint, and tangents longer than 40 m receive three equally spaced points.");
            settings.AddChoice(
                "Output", "01 Output", "Point output", "COGO",
                "Choose one dynamic point/annotation output for every generated setting-out location.",
                new[] { "COGO", "MText", "MLeader" });
            settings.AddText(
                "Prefix", "02 Numbering", "Point name prefix", "P",
                "Names are generated as P1, P2, P3 and are resequenced when linked geometry changes.");
            settings.AddPositiveInteger(
                "Start", "02 Numbering", "Starting number", 1,
                "First generated point number/name.");
            settings.AddPositiveDouble(
                "Offset", "03 Annotation", "MText/MLeader offset", 3.0,
                "Drawing-unit offset from each setting-out point to its annotation.");
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;

            string outputType = settings.Text("Output");
            string prefix = string.IsNullOrWhiteSpace(settings.Text("Prefix"))
                ? "P"
                : settings.Text("Prefix").Trim();
            int startNumber = settings.Integer("Start", 1);
            double labelOffset = settings.Double("Offset", 3.0);

            PromptPointResult tablePoint = document.Editor.GetPoint(
                "\nPick insertion point for the linked setting-out table: ");
            if (tablePoint.Status != PromptStatus.OK) return;

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
            if (sources.Count == 0)
            {
                document.Editor.WriteMessage("\nCE_VERTEXSETTINGOUT cancelled. The selected objects produced no setting-out geometry.");
                return;
            }

            List<VertexSettingRecord> records = FlattenAndName(sources, prefix, startNumber);
            int radialDimensions = sources.Sum(item => item.Dimensions.Count);
            var review = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("Accepted sources", sources.Count.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("Rejected selections", rejected.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("Point output", outputType),
                new KeyValuePair<string, string>("Generated point rows", records.Count.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("Radius dimensions", radialDimensions.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("Automatic linked refresh", "Yes"),
                new KeyValuePair<string, string>("Excel export", "Linked table")
            };
            if (!PopupTablePresenter.ShowReview(
                    "CE Tools - Vertex Setting-Out Preview",
                    "The source handles and generation rules are stored on the table. CE_REFRESHALL and the dedicated refresh command rebuild the outputs from current geometry.",
                    review,
                    "Create Setting-Out"))
                return;

            var link = new VertexSettingLink
            {
                GroupId = Guid.NewGuid().ToString("N"),
                OutputType = outputType,
                Prefix = prefix,
                StartNumber = startNumber,
                LabelOffset = labelOffset,
                SourceHandles = sources.Select(item => item.Handle).ToList()
            };

            try
            {
                ObjectId tableId = CreateGroup(
                    document,
                    civilDocument,
                    link,
                    sources,
                    records,
                    tablePoint.Value,
                    annotation.TextHeight);
                document.Editor.SetImpliedSelection(new[] { tableId });
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
                PopulateTable(table, records, textHeight, link.OutputType);
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
                if (sources.Count == 0)
                    throw new InvalidOperationException("The linked sources produced no current setting-out geometry.");
                List<VertexSettingRecord> records = FlattenAndName(sources, link.Prefix, link.StartNumber);

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

                PopulateTable(table, records, textHeight, link.OutputType);
                transaction.Commit();
                pointCount = records.Count;
                dimensionCount = liveDimensionKeys.Count;
            }
        }

        private static List<VertexSettingRecord> FlattenAndName(
            IEnumerable<VertexSettingSource> sources,
            string prefix,
            int startNumber)
        {
            var result = new List<VertexSettingRecord>();
            int sequence = startNumber;
            foreach (VertexSettingSource source in sources)
            {
                foreach (VertexSettingRecord record in source.Records)
                {
                    record.PointName = prefix + sequence.ToString(CultureInfo.InvariantCulture);
                    sequence++;
                    result.Add(record);
                }
            }
            return result;
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
                WriteOutputLink(point, transaction, link.GroupId, record.Key);
                return id;
            }

            if (string.Equals(link.OutputType, "MLeader", StringComparison.OrdinalIgnoreCase))
            {
                var leader = new MLeader();
                leader.SetDatabaseDefaults(database);
                leader.ContentType = ContentType.MTextContent;
                var text = new MText();
                text.SetDatabaseDefaults(database);
                text.TextHeight = textHeight;
                text.Contents = LabelText(record);
                Point3d location = LabelLocation(record.Point, link.LabelOffset);
                text.Location = location;
                leader.MText = text;
                leader.TextLocation = location;
                PaperAnnotationScale.SetAnnotative(leader);
                ObjectId id = modelSpace.AppendEntity(leader);
                transaction.AddNewlyCreatedDBObject(leader, true);
                leader.AddLeaderLine(record.Point);
                WriteOutputLink(leader, transaction, link.GroupId, record.Key);
                return id;
            }

            var mtext = new MText();
            mtext.SetDatabaseDefaults(database);
            mtext.Location = LabelLocation(record.Point, link.LabelOffset);
            mtext.Attachment = AttachmentPoint.BottomLeft;
            mtext.TextHeight = textHeight;
            mtext.Contents = LabelText(record);
            PaperAnnotationScale.SetAnnotative(mtext);
            ObjectId textId = modelSpace.AppendEntity(mtext);
            transaction.AddNewlyCreatedDBObject(mtext, true);
            WriteOutputLink(mtext, transaction, link.GroupId, record.Key);
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
                return true;
            }

            MText mtext = value as MText;
            if (mtext != null && string.Equals(link.OutputType, "MText", StringComparison.OrdinalIgnoreCase))
            {
                mtext.Location = LabelLocation(record.Point, link.LabelOffset);
                mtext.TextHeight = textHeight;
                mtext.Contents = LabelText(record);
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
            return true;
        }

        private static void PopulateTable(
            Table table,
            IList<VertexSettingRecord> records,
            double textHeight,
            string outputType)
        {
            table.SetSize(records.Count + 2, 9);
            table.SetRowHeight(Math.Max(textHeight * 1.8, 0.001));
            table.SetColumnWidth(Math.Max(textHeight * 8.0, 0.001));
            table.Cells[0, 0].TextString = "CE VERTEX SETTING-OUT - " + outputType.ToUpperInvariant();
            table.MergeCells(CellRange.Create(table, 0, 0, 0, 8));
            string[] headings =
            {
                "POINT NAME", "TYPE", "SOURCE", "SEGMENT", "X", "Y", "Z", "RADIUS", "SEGMENT LENGTH"
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
                table.Cells[row, 4].TextString = record.Point.X.ToString("N3", CultureInfo.CurrentCulture);
                table.Cells[row, 5].TextString = record.Point.Y.ToString("N3", CultureInfo.CurrentCulture);
                table.Cells[row, 6].TextString = record.Point.Z.ToString("N3", CultureInfo.CurrentCulture);
                table.Cells[row, 7].TextString = record.Radius.HasValue
                    ? record.Radius.Value.ToString("N3", CultureInfo.CurrentCulture)
                    : string.Empty;
                table.Cells[row, 8].TextString = record.SegmentLength > 0.0
                    ? record.SegmentLength.ToString("N3", CultureInfo.CurrentCulture)
                    : string.Empty;
            }

            for (int row = 0; row < table.Rows.Count; row++)
                for (int column = 0; column < table.Columns.Count; column++)
                {
                    table.Cells[row, column].Alignment = CellAlignment.MiddleCenter;
                    table.Cells[row, column].TextHeight = textHeight;
                }
            table.GenerateLayout();
        }

        private static string LabelText(VertexSettingRecord record)
        {
            return string.Join(
                "\\P",
                record.PointName,
                "X=" + record.Point.X.ToString("N3", CultureInfo.CurrentCulture),
                "Y=" + record.Point.Y.ToString("N3", CultureInfo.CurrentCulture),
                "Z=" + record.Point.Z.ToString("N3", CultureInfo.CurrentCulture));
        }

        private static Point3d LabelLocation(Point3d point, double offset)
        {
            return point + new Vector3d(offset, offset, 0.0);
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
            foreach (string handle in link.SourceHandles)
                values.Add(new TypedValue((int)DxfCode.ExtendedDataAsciiString, handle));
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
                SourceHandles = new List<string>()
            };
            for (int index = 8; index < values.Length; index++)
            {
                string handle = Convert.ToString(values[index].Value);
                if (!string.IsNullOrWhiteSpace(handle)) link.SourceHandles.Add(handle);
            }
            return link;
        }

        private static void WriteOutputLink(
            Entity entity,
            Transaction transaction,
            string groupId,
            string key)
        {
            entity.XData = LinkBuffer("OUTPUT", groupId, key);
        }

        private static void WriteDimensionLink(
            Entity entity,
            Transaction transaction,
            string groupId,
            string key)
        {
            entity.XData = LinkBuffer("DIM", groupId, key);
        }

        private static ResultBuffer LinkBuffer(string type, string groupId, string key)
        {
            return new ResultBuffer(
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, AppName),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, type),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, SchemaVersion),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, groupId),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, key));
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
            public IList<string> SourceHandles { get; set; }
        }
    }
}

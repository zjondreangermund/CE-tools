using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.ProductionReportCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Produces project/discipline reports, linked summary sheets and four standard
    /// drawing-book layouts. Reports are derived from current model-space objects,
    /// CE Tools project metadata, linked BOQs and linked cross sections.
    /// </summary>
    public sealed class ProductionReportCommands
    {
        private const string SummaryRecordName = "CE_PROJECT_SUMMARY_SHEET";
        private const string SummaryGeneratedRecordName = "CE_PROJECT_SUMMARY_GENERATED";
        private const string BookRecordName = "CE_DRAWING_BOOK_LAYOUT";
        private const string SchemaVersion = "1";

        [CommandMethod(
            "CE_TOOLS",
            "CE_REPORTTOOLS",
            CommandFlags.Modal | CommandFlags.Redraw)]
        public void ReportTools()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            var options = new PromptKeywordOptions(
                "\nReport/production tool [Full/Discipline/Export/Summary/RefreshSummary/DrawingBook/BookIndex] <Full>: ")
            {
                AllowNone = true
            };
            foreach (string keyword in new[]
            {
                "Full", "Discipline", "Export", "Summary", "RefreshSummary",
                "DrawingBook", "BookIndex"
            })
                options.Keywords.Add(keyword);

            PromptResult result = document.Editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel) return;
            string mode = result.Status == PromptStatus.None ? "Full" : result.StringResult;

            if (Equal(mode, "Discipline")) DisciplineReport();
            else if (Equal(mode, "Export")) ExportReport();
            else if (Equal(mode, "Summary")) CreateSummarySheet();
            else if (Equal(mode, "RefreshSummary")) RefreshSummarySheet();
            else if (Equal(mode, "DrawingBook")) CreateDrawingBook();
            else if (Equal(mode, "BookIndex")) ExportDrawingBookIndex();
            else FullReport();
        }

        [CommandMethod("CE_TOOLS", "CE_REPORTFULL", CommandFlags.Modal | CommandFlags.Redraw)]
        public void FullReport()
        {
            ShowReport(ActiveDocument(), ReportDiscipline.All, true);
        }

        [CommandMethod("CE_TOOLS", "CE_REPORTDISC", CommandFlags.Modal | CommandFlags.Redraw)]
        public void DisciplineReport()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            ReportDiscipline discipline;
            if (!PromptDiscipline(document.Editor, false, out discipline)) return;
            ShowReport(document, discipline, true);
        }

        [CommandMethod("CE_TOOLS", "CE_REPORTROAD", CommandFlags.Modal | CommandFlags.Redraw)]
        public void RoadReport() { ShowReport(ActiveDocument(), ReportDiscipline.Road, true); }

        [CommandMethod("CE_TOOLS", "CE_REPORTPLATFORM", CommandFlags.Modal | CommandFlags.Redraw)]
        public void PlatformReport() { ShowReport(ActiveDocument(), ReportDiscipline.Platform, true); }

        [CommandMethod("CE_TOOLS", "CE_REPORTSTORM", CommandFlags.Modal | CommandFlags.Redraw)]
        public void StormReport() { ShowReport(ActiveDocument(), ReportDiscipline.Stormwater, true); }

        [CommandMethod("CE_TOOLS", "CE_REPORTSEWER", CommandFlags.Modal | CommandFlags.Redraw)]
        public void SewerReport() { ShowReport(ActiveDocument(), ReportDiscipline.Sewer, true); }

        [CommandMethod("CE_TOOLS", "CE_REPORTWATER", CommandFlags.Modal | CommandFlags.Redraw)]
        public void WaterReport() { ShowReport(ActiveDocument(), ReportDiscipline.Water, true); }

        [CommandMethod("CE_TOOLS", "CE_REPORTBULKWATER", CommandFlags.Modal | CommandFlags.Redraw)]
        public void BulkWaterReport() { ShowReport(ActiveDocument(), ReportDiscipline.BulkWater, true); }

        [CommandMethod("CE_TOOLS", "CE_REPORTEXPORT", CommandFlags.Modal | CommandFlags.Redraw)]
        public void ExportReport()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            ReportDiscipline discipline;
            if (!PromptDiscipline(document.Editor, true, out discipline)) return;

            ProjectSnapshot snapshot = BuildSnapshot(document.Database, discipline);
            string defaultName = "CE-Tools-" + discipline + "-Design-Report.xlsx";
            string path;
            if (!PromptExcelPath(document.Editor, defaultName, out path)) return;

            try
            {
                SimpleXlsxWriter.Write(
                    path,
                    discipline + " Report",
                    BuildReportExportRows(snapshot));
                document.Editor.WriteMessage(
                    "\nCE_REPORTEXPORT complete. Discipline={0}; report groups={1}; workbook={2}",
                    discipline,
                    snapshot.Groups.Count,
                    path);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_REPORTEXPORT failed. {0}",
                    exception.Message);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_SUMMARYSHEET", CommandFlags.Modal | CommandFlags.Redraw)]
        public void CreateSummarySheet()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            PromptPointResult pointResult = document.Editor.GetPoint(
                "\nPick lower-left insertion point for the project summary sheet: ");
            if (pointResult.Status != PromptStatus.OK) return;
            Point3d insertion = pointResult.Value.TransformBy(
                document.Editor.CurrentUserCoordinateSystem);

            ProjectSnapshot snapshot = BuildSnapshot(
                document.Database,
                ReportDiscipline.All);
            WriteSnapshotPreview(document.Editor, snapshot);
            if (!Confirm(document.Editor, "Create the linked project summary sheet"))
                return;

            try
            {
                SummaryLink link = CreateSummary(
                    document.Database,
                    insertion,
                    snapshot,
                    null);
                document.Editor.WriteMessage(
                    "\nCE_SUMMARYSHEET complete. Anchor={0}; generated objects={1}; report groups={2}.",
                    link.AnchorHandle,
                    link.GeneratedHandles.Count,
                    snapshot.Groups.Count);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_SUMMARYSHEET failed. No summary sheet was committed. {0}",
                    exception.Message);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_SUMMARYREFRESH", CommandFlags.Modal | CommandFlags.Redraw)]
        public void RefreshSummarySheet()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            ObjectId anchorId;
            if (!PromptForSummaryAnchor(document, out anchorId)) return;
            ProjectSnapshot snapshot = BuildSnapshot(
                document.Database,
                ReportDiscipline.All);
            WriteSnapshotPreview(document.Editor, snapshot);
            if (!Confirm(document.Editor, "Refresh the linked project summary sheet"))
                return;

            try
            {
                SummaryLink oldLink;
                using (Transaction transaction =
                    document.Database.TransactionManager.StartTransaction())
                {
                    Entity anchor = transaction.GetObject(
                        anchorId,
                        OpenMode.ForRead,
                        false) as Entity;
                    oldLink = ReadSummaryLink(anchor, transaction);
                }

                CreateSummary(
                    document.Database,
                    oldLink.InsertionPoint,
                    snapshot,
                    oldLink);
                document.Editor.WriteMessage(
                    "\nCE_SUMMARYREFRESH complete. Report groups={0}; layouts={1}; linked BOQs={2}; linked sections={3}.",
                    snapshot.Groups.Count,
                    snapshot.Layouts.Count,
                    snapshot.LinkedBoqCount,
                    snapshot.LinkedSectionCount);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_SUMMARYREFRESH failed. Existing summary geometry was retained. {0}",
                    exception.Message);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_SUMMARYINFO", CommandFlags.Modal | CommandFlags.Redraw)]
        public void SummaryInformation()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            ObjectId anchorId;
            if (!PromptForSummaryAnchor(document, out anchorId)) return;

            try
            {
                SummaryLink link;
                int valid = 0;
                int stale = 0;
                using (Transaction transaction =
                    document.Database.TransactionManager.StartTransaction())
                {
                    Entity anchor = transaction.GetObject(
                        anchorId,
                        OpenMode.ForRead,
                        false) as Entity;
                    link = ReadSummaryLink(anchor, transaction);
                }
                foreach (string handle in link.GeneratedHandles)
                {
                    ObjectId id;
                    if (TryResolveHandle(document.Database, handle, out id)) valid++;
                    else stale++;
                }

                var rows = new List<IList<string>>
                {
                    new List<string> { "Schema", link.Schema },
                    new List<string> { "Anchor", link.AnchorHandle },
                    new List<string> { "Insertion X", link.InsertionPoint.X.ToString("N3", CultureInfo.CurrentCulture) },
                    new List<string> { "Insertion Y", link.InsertionPoint.Y.ToString("N3", CultureInfo.CurrentCulture) },
                    new List<string> { "Valid generated objects", valid.ToString(CultureInfo.InvariantCulture) },
                    new List<string> { "Stale generated handles", stale.ToString(CultureInfo.InvariantCulture) },
                    new List<string> { "Refresh model", "Explicit CE_SUMMARYREFRESH" }
                };
                GridReportPresenter.ShowReportAndOfferTable(
                    document,
                    "CE Tools Project Summary Link",
                    "The summary is linked to current drawing contents through an explicit refresh command.",
                    new List<string> { "Property", "Value" },
                    rows,
                    "CE TOOLS SUMMARY LINK");
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_SUMMARYINFO cancelled. {0}",
                    exception.Message);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_DRAWINGBOOK", CommandFlags.Modal | CommandFlags.Redraw)]
        public void CreateDrawingBook()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            ProjectSnapshot snapshot = BuildSnapshot(
                document.Database,
                ReportDiscipline.All);
            var packages = StandardBookPackages();
            document.Editor.WriteMessage(
                "\nCE_DRAWINGBOOK preview. Layout packages: {0}. Existing layouts: {1}.",
                packages.Count,
                snapshot.Layouts.Count);
            foreach (BookPackage package in packages)
            {
                document.Editor.WriteMessage(
                    "\n  {0}: {1:N0} x {2:N0} mm, {3}.",
                    package.LayoutName,
                    package.Width,
                    package.Height,
                    package.Purpose);
            }

            if (!Confirm(
                document.Editor,
                "Create or refresh the A4/A3 client and A1/A0 construction-book layouts"))
                return;

            try
            {
                int created = 0;
                int refreshed = 0;
                foreach (BookPackage package in packages)
                {
                    bool wasCreated = CreateOrRefreshBookLayout(
                        document.Database,
                        package,
                        snapshot);
                    if (wasCreated) created++;
                    else refreshed++;
                }
                document.Editor.WriteMessage(
                    "\nCE_DRAWINGBOOK complete. Layouts created={0}; refreshed={1}. " +
                    "Frames use true millimetre A-series dimensions; plot-device/media assignment remains workstation-specific.",
                    created,
                    refreshed);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_DRAWINGBOOK failed. {0}",
                    exception.Message);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_BOOKINDEX", CommandFlags.Modal | CommandFlags.Redraw)]
        public void ExportDrawingBookIndex()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            ProjectSnapshot snapshot = BuildSnapshot(
                document.Database,
                ReportDiscipline.All);
            string path;
            if (!PromptExcelPath(
                document.Editor,
                "CE-Tools-Drawing-Book-Index.xlsx",
                out path)) return;

            var rows = new List<IList<string>>
            {
                new List<string>
                {
                    "CE TOOLS DRAWING BOOK INDEX", string.Empty, string.Empty,
                    string.Empty, string.Empty, string.Empty
                },
                new List<string>
                {
                    "NO.", "LAYOUT", "PURPOSE", "PAPER FAMILY", "STATUS", "PROJECT"
                }
            };

            int index = 1;
            foreach (BookPackage package in StandardBookPackages())
            {
                bool exists = snapshot.Layouts.Any(
                    item => Equal(item.Name, package.LayoutName));
                rows.Add(new List<string>
                {
                    index++.ToString(CultureInfo.InvariantCulture),
                    package.LayoutName,
                    package.Purpose,
                    package.PaperName,
                    exists ? "Available" : "Missing",
                    snapshot.Project.Get("Project Name")
                });
            }
            foreach (LayoutSnapshot layout in snapshot.Layouts
                .Where(item => !StandardBookPackages().Any(
                    standard => Equal(standard.LayoutName, item.Name))))
            {
                rows.Add(new List<string>
                {
                    index++.ToString(CultureInfo.InvariantCulture),
                    layout.Name,
                    "Project drawing",
                    "Existing layout",
                    "Available",
                    snapshot.Project.Get("Project Name")
                });
            }

            try
            {
                SimpleXlsxWriter.Write(path, "Drawing Book Index", rows);
                document.Editor.WriteMessage(
                    "\nCE_BOOKINDEX complete. Layouts listed={0}; workbook={1}",
                    rows.Count - 2,
                    path);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_BOOKINDEX failed. {0}",
                    exception.Message);
            }
        }

        private static void ShowReport(
            Document document,
            ReportDiscipline discipline,
            bool offerTable)
        {
            if (document == null) return;
            ProjectSnapshot snapshot = BuildSnapshot(document.Database, discipline);
            WriteSnapshotPreview(document.Editor, snapshot);

            var columns = new List<string>
            {
                "Discipline", "Layer", "Object Type", "Count",
                "Length", "Area", "Volume", "Status / Detail"
            };
            var rows = new List<IList<string>>();
            foreach (ReportGroup group in snapshot.Groups)
            {
                rows.Add(new List<string>
                {
                    group.Discipline.ToString(),
                    group.Layer,
                    group.TypeName,
                    group.Count.ToString(CultureInfo.InvariantCulture),
                    group.Length > 0.0
                        ? group.Length.ToString("N3", CultureInfo.CurrentCulture)
                        : string.Empty,
                    group.Area > 0.0
                        ? group.Area.ToString("N3", CultureInfo.CurrentCulture)
                        : string.Empty,
                    group.Volume > 0.0
                        ? group.Volume.ToString("N3", CultureInfo.CurrentCulture)
                        : string.Empty,
                    group.Detail
                });
            }

            if (rows.Count == 0)
            {
                rows.Add(new List<string>
                {
                    discipline.ToString(), string.Empty, string.Empty, "0",
                    string.Empty, string.Empty, string.Empty,
                    "No matching model-space design objects"
                });
            }

            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Tools " + discipline + " Design Report",
                BuildReportNote(snapshot),
                columns,
                rows,
                "CE TOOLS " + discipline.ToString().ToUpperInvariant() + " DESIGN REPORT");
        }

        private static ProjectSnapshot BuildSnapshot(
            Database database,
            ReportDiscipline filter)
        {
            var snapshot = new ProjectSnapshot(filter);
            snapshot.Project = ReadProjectMetadata(database);

            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTable blockTable = transaction.GetObject(
                    database.BlockTableId,
                    OpenMode.ForRead,
                    false) as BlockTable;
                BlockTableRecord modelSpace = blockTable == null
                    ? null
                    : transaction.GetObject(
                        blockTable[BlockTableRecord.ModelSpace],
                        OpenMode.ForRead,
                        false) as BlockTableRecord;

                if (modelSpace != null)
                {
                    var map = new SortedDictionary<string, ReportGroup>(
                        StringComparer.OrdinalIgnoreCase);
                    foreach (ObjectId id in modelSpace)
                    {
                        DBObject value;
                        try
                        {
                            value = transaction.GetObject(id, OpenMode.ForRead, false);
                        }
                        catch
                        {
                            snapshot.Rejected++;
                            continue;
                        }

                        Entity entity = value as Entity;
                        if (entity == null)
                        {
                            snapshot.Rejected++;
                            continue;
                        }

                        string layer = string.IsNullOrWhiteSpace(entity.Layer)
                            ? "0"
                            : entity.Layer;
                        string typeName = value.GetType().Name;
                        string name = ReadObjectName(value, transaction);
                        ReportDiscipline discipline = ClassifyDiscipline(
                            layer + " " + typeName + " " + name);
                        if (filter != ReportDiscipline.All && discipline != filter)
                            continue;

                        string key = discipline + "|" + layer + "|" + typeName;
                        ReportGroup group;
                        if (!map.TryGetValue(key, out group))
                        {
                            group = new ReportGroup(
                                discipline,
                                layer,
                                FriendlyTypeName(typeName));
                            map.Add(key, group);
                        }

                        group.Count++;
                        double length;
                        if (TryGetLength(value, out length)) group.Length += length;
                        double area;
                        if (TryGetArea(entity, out area)) group.Area += area;
                        double volume;
                        if (TryGetVolume(value, out volume)) group.Volume += volume;
                        group.Detail = BuildGroupDetail(
                            discipline,
                            layer,
                            typeName,
                            name);

                        if (HasRecord(entity, transaction, "CE_BOQ_LINKS"))
                            snapshot.LinkedBoqCount++;
                        if (HasRecord(
                            entity,
                            transaction,
                            DynamicCrossSectionCommands.LinkRecordName))
                            snapshot.LinkedSectionCount++;
                    }
                    snapshot.Groups = map.Values.ToList();
                }

                DBDictionary layouts = transaction.GetObject(
                    database.LayoutDictionaryId,
                    OpenMode.ForRead,
                    false) as DBDictionary;
                if (layouts != null)
                {
                    foreach (DBDictionaryEntry entry in layouts)
                    {
                        Layout layout = transaction.GetObject(
                            entry.Value,
                            OpenMode.ForRead,
                            false) as Layout;
                        if (layout == null || layout.ModelType) continue;
                        snapshot.Layouts.Add(new LayoutSnapshot(
                            layout.LayoutName,
                            layout.TabOrder));
                    }
                }
            }

            snapshot.Layouts = snapshot.Layouts
                .OrderBy(item => item.TabOrder)
                .ThenBy(item => item.Name)
                .ToList();
            return snapshot;
        }

        private static SummaryLink CreateSummary(
            Database database,
            Point3d insertion,
            ProjectSnapshot snapshot,
            SummaryLink oldLink)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTableRecord currentSpace = transaction.GetObject(
                    database.CurrentSpaceId,
                    OpenMode.ForWrite,
                    false) as BlockTableRecord;
                if (currentSpace == null)
                    throw new InvalidOperationException("The current drawing space could not be opened.");

                if (oldLink != null)
                {
                    foreach (string handle in oldLink.GeneratedHandles)
                    {
                        ObjectId id;
                        if (!TryResolveHandle(database, handle, out id)) continue;
                        Entity old = transaction.GetObject(
                            id,
                            OpenMode.ForWrite,
                            false) as Entity;
                        if (old != null && !old.IsErased) old.Erase();
                    }
                }

                double textHeight = ResolveTextHeight(database);
                double width = textHeight * 95.0;
                double height = textHeight * 65.0;
                var generatedHandles = new List<string>();

                var frame = new Polyline();
                frame.SetDatabaseDefaults(database);
                frame.AddVertexAt(0, new Point2d(insertion.X, insertion.Y), 0.0, 0.0, 0.0);
                frame.AddVertexAt(1, new Point2d(insertion.X + width, insertion.Y), 0.0, 0.0, 0.0);
                frame.AddVertexAt(2, new Point2d(insertion.X + width, insertion.Y + height), 0.0, 0.0, 0.0);
                frame.AddVertexAt(3, new Point2d(insertion.X, insertion.Y + height), 0.0, 0.0, 0.0);
                frame.Closed = true;
                currentSpace.AppendEntity(frame);
                transaction.AddNewlyCreatedDBObject(frame, true);
                frame.CreateExtensionDictionary();
                generatedHandles.Add(frame.Handle.ToString());

                var title = new MText();
                title.SetDatabaseDefaults(database);
                title.Location = new Point3d(
                    insertion.X + textHeight * 3.0,
                    insertion.Y + height - textHeight * 4.0,
                    insertion.Z);
                title.TextHeight = textHeight * 1.8;
                title.Contents = string.Join(
                    "\\P",
                    string.IsNullOrWhiteSpace(snapshot.Project.Get("Project Name"))
                        ? "CE TOOLS PROJECT SUMMARY"
                        : snapshot.Project.Get("Project Name").ToUpperInvariant(),
                    "CLIENT: " + ValueOrNotSet(snapshot.Project.Get("Client")),
                    "LOCATION: " + ValueOrNotSet(snapshot.Project.Get("Town")) +
                        ", " + ValueOrNotSet(snapshot.Project.Get("Country")));
                AddSummaryGenerated(
                    transaction,
                    currentSpace,
                    title,
                    frame.Handle.ToString(),
                    generatedHandles);

                Table projectTable = BuildProjectTable(
                    database,
                    new Point3d(
                        insertion.X + textHeight * 3.0,
                        insertion.Y + height - textHeight * 15.0,
                        insertion.Z),
                    textHeight,
                    snapshot);
                AddSummaryGenerated(
                    transaction,
                    currentSpace,
                    projectTable,
                    frame.Handle.ToString(),
                    generatedHandles);
                projectTable.GenerateLayout();

                Table disciplineTable = BuildDisciplineTable(
                    database,
                    new Point3d(
                        insertion.X + textHeight * 3.0,
                        insertion.Y + height - textHeight * 32.0,
                        insertion.Z),
                    textHeight,
                    snapshot);
                AddSummaryGenerated(
                    transaction,
                    currentSpace,
                    disciplineTable,
                    frame.Handle.ToString(),
                    generatedHandles);
                disciplineTable.GenerateLayout();

                Table productionTable = BuildProductionTable(
                    database,
                    new Point3d(
                        insertion.X + textHeight * 3.0,
                        insertion.Y + textHeight * 16.0,
                        insertion.Z),
                    textHeight,
                    snapshot);
                AddSummaryGenerated(
                    transaction,
                    currentSpace,
                    productionTable,
                    frame.Handle.ToString(),
                    generatedHandles);
                productionTable.GenerateLayout();

                var note = new MText();
                note.SetDatabaseDefaults(database);
                note.Location = new Point3d(
                    insertion.X + textHeight * 3.0,
                    insertion.Y + textHeight * 3.0,
                    insertion.Z);
                note.TextHeight = textHeight * 0.85;
                note.Contents =
                    "SUMMARY STATUS: Model-space inventory and CE links are current at refresh time. " +
                    "Run CE_SUMMARYREFRESH after design or layout changes. Classification is based on layer, object name and runtime type.";
                note.Width = width - textHeight * 6.0;
                AddSummaryGenerated(
                    transaction,
                    currentSpace,
                    note,
                    frame.Handle.ToString(),
                    generatedHandles);

                WriteSummaryLink(
                    frame,
                    transaction,
                    new SummaryLink(
                        SchemaVersion,
                        frame.Handle.ToString(),
                        insertion,
                        generatedHandles));
                transaction.Commit();
                return new SummaryLink(
                    SchemaVersion,
                    frame.Handle.ToString(),
                    insertion,
                    generatedHandles);
            }
        }

        private static Table BuildProjectTable(
            Database database,
            Point3d position,
            double textHeight,
            ProjectSnapshot snapshot)
        {
            string[] fields =
            {
                "Project Name", "Client", "Country", "Town",
                "Coordinate System", "Standards", "Drawing Template", "Units"
            };
            var table = new Table();
            table.SetDatabaseDefaults(database);
            table.TableStyle = database.Tablestyle;
            table.Position = position;
            table.SetSize(fields.Length + 1, 2);
            table.SetRowHeight(textHeight * 1.8);
            table.Columns[0].Width = textHeight * 15.0;
            table.Columns[1].Width = textHeight * 36.0;
            table.MergeCells(CellRange.Create(table, 0, 0, 0, 1));
            table.Cells[0, 0].TextString = "PROJECT INFORMATION";
            table.Cells[0, 0].Alignment = CellAlignment.MiddleCenter;
            for (int index = 0; index < fields.Length; index++)
            {
                int row = index + 1;
                table.Cells[row, 0].TextString = fields[index];
                table.Cells[row, 1].TextString = ValueOrNotSet(
                    snapshot.Project.Get(fields[index]));
                table.Cells[row, 0].TextHeight = textHeight * 0.8;
                table.Cells[row, 1].TextHeight = textHeight * 0.8;
            }
            return table;
        }

        private static Table BuildDisciplineTable(
            Database database,
            Point3d position,
            double textHeight,
            ProjectSnapshot snapshot)
        {
            List<DisciplineSummary> summaries = BuildDisciplineSummaries(snapshot);
            var table = new Table();
            table.SetDatabaseDefaults(database);
            table.TableStyle = database.Tablestyle;
            table.Position = position;
            table.SetSize(summaries.Count + 2, 5);
            table.SetRowHeight(textHeight * 1.8);
            double[] widths =
            {
                textHeight * 13.0,
                textHeight * 9.0,
                textHeight * 10.0,
                textHeight * 10.0,
                textHeight * 10.0
            };
            for (int column = 0; column < widths.Length; column++)
                table.Columns[column].Width = widths[column];
            table.MergeCells(CellRange.Create(table, 0, 0, 0, 4));
            table.Cells[0, 0].TextString = "DISCIPLINE DESIGN SUMMARY";
            string[] headings =
            {
                "DISCIPLINE", "OBJECTS", "LENGTH", "AREA", "VOLUME"
            };
            for (int column = 0; column < headings.Length; column++)
                table.Cells[1, column].TextString = headings[column];
            for (int index = 0; index < summaries.Count; index++)
            {
                DisciplineSummary summary = summaries[index];
                int row = index + 2;
                table.Cells[row, 0].TextString = summary.Discipline.ToString();
                table.Cells[row, 1].TextString = summary.Count.ToString(CultureInfo.InvariantCulture);
                table.Cells[row, 2].TextString = summary.Length.ToString("N3", CultureInfo.CurrentCulture);
                table.Cells[row, 3].TextString = summary.Area.ToString("N3", CultureInfo.CurrentCulture);
                table.Cells[row, 4].TextString = summary.Volume.ToString("N3", CultureInfo.CurrentCulture);
            }
            for (int row = 0; row < table.Rows.Count; row++)
                for (int column = 0; column < table.Columns.Count; column++)
                    table.Cells[row, column].TextHeight = textHeight * 0.8;
            return table;
        }

        private static Table BuildProductionTable(
            Database database,
            Point3d position,
            double textHeight,
            ProjectSnapshot snapshot)
        {
            var values = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("Linked BOQ tables", snapshot.LinkedBoqCount.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("Linked dynamic cross sections", snapshot.LinkedSectionCount.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("Paper-space layouts", snapshot.Layouts.Count.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("Model report groups", snapshot.Groups.Count.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("Rejected/unreadable objects", snapshot.Rejected.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("A4 client layout", LayoutStatus(snapshot, "CE-CLIENT-A4")),
                new KeyValuePair<string, string>("A3 client layout", LayoutStatus(snapshot, "CE-CLIENT-A3")),
                new KeyValuePair<string, string>("A1 construction layout", LayoutStatus(snapshot, "CE-CONSTRUCTION-A1")),
                new KeyValuePair<string, string>("A0 construction layout", LayoutStatus(snapshot, "CE-CONSTRUCTION-A0"))
            };

            var table = new Table();
            table.SetDatabaseDefaults(database);
            table.TableStyle = database.Tablestyle;
            table.Position = position;
            table.SetSize(values.Count + 1, 2);
            table.SetRowHeight(textHeight * 1.8);
            table.Columns[0].Width = textHeight * 28.0;
            table.Columns[1].Width = textHeight * 15.0;
            table.MergeCells(CellRange.Create(table, 0, 0, 0, 1));
            table.Cells[0, 0].TextString = "PRODUCTION READINESS";
            for (int index = 0; index < values.Count; index++)
            {
                int row = index + 1;
                table.Cells[row, 0].TextString = values[index].Key;
                table.Cells[row, 1].TextString = values[index].Value;
            }
            for (int row = 0; row < table.Rows.Count; row++)
                for (int column = 0; column < table.Columns.Count; column++)
                    table.Cells[row, column].TextHeight = textHeight * 0.8;
            return table;
        }

        private static void AddSummaryGenerated(
            Transaction transaction,
            BlockTableRecord space,
            Entity entity,
            string anchorHandle,
            ICollection<string> handles)
        {
            space.AppendEntity(entity);
            transaction.AddNewlyCreatedDBObject(entity, true);
            entity.CreateExtensionDictionary();
            DBDictionary dictionary = transaction.GetObject(
                entity.ExtensionDictionary,
                OpenMode.ForWrite,
                false) as DBDictionary;
            Xrecord record = OpenOrCreateRecord(
                dictionary,
                SummaryGeneratedRecordName,
                transaction);
            record.Data = new ResultBuffer(
                new TypedValue((int)DxfCode.Text, "Anchor=" + anchorHandle));
            handles.Add(entity.Handle.ToString());
        }

        private static bool CreateOrRefreshBookLayout(
            Database database,
            BookPackage package,
            ProjectSnapshot snapshot)
        {
            bool created = false;
            ObjectId layoutId = FindLayoutId(database, package.LayoutName);
            if (layoutId.IsNull)
            {
                layoutId = LayoutManager.Current.CreateLayout(package.LayoutName);
                created = true;
            }

            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                Layout layout = transaction.GetObject(
                    layoutId,
                    OpenMode.ForWrite,
                    false) as Layout;
                if (layout == null)
                    throw new InvalidOperationException(
                        "Layout could not be opened: " + package.LayoutName);

                BookLink oldLink = ReadBookLinkIfPresent(layout, transaction);
                if (oldLink != null)
                {
                    foreach (string handle in oldLink.GeneratedHandles)
                    {
                        ObjectId id;
                        if (!TryResolveHandle(database, handle, out id)) continue;
                        Entity old = transaction.GetObject(
                            id,
                            OpenMode.ForWrite,
                            false) as Entity;
                        if (old != null && !old.IsErased) old.Erase();
                    }
                }

                BlockTableRecord paperSpace = transaction.GetObject(
                    layout.BlockTableRecordId,
                    OpenMode.ForWrite,
                    false) as BlockTableRecord;
                if (paperSpace == null)
                    throw new InvalidOperationException(
                        "Paper space could not be opened for " + package.LayoutName);

                double margin = package.Width >= 800.0 ? 20.0 : 10.0;
                double titleHeight = package.Width >= 800.0 ? 7.0 : 4.0;
                var generated = new List<string>();

                var frame = new Polyline();
                frame.SetDatabaseDefaults(database);
                frame.AddVertexAt(0, new Point2d(margin, margin), 0.0, 0.0, 0.0);
                frame.AddVertexAt(1, new Point2d(package.Width - margin, margin), 0.0, 0.0, 0.0);
                frame.AddVertexAt(2, new Point2d(package.Width - margin, package.Height - margin), 0.0, 0.0, 0.0);
                frame.AddVertexAt(3, new Point2d(margin, package.Height - margin), 0.0, 0.0, 0.0);
                frame.Closed = true;
                AddBookGenerated(transaction, paperSpace, frame, package.LayoutName, generated);

                var title = new MText();
                title.SetDatabaseDefaults(database);
                title.Location = new Point3d(
                    margin * 1.5,
                    package.Height - margin * 1.8,
                    0.0);
                title.TextHeight = titleHeight;
                title.Width = package.Width - margin * 3.0;
                title.Contents = string.Join(
                    "\\P",
                    ValueOrNotSet(snapshot.Project.Get("Project Name")),
                    package.Purpose.ToUpperInvariant(),
                    package.PaperName + " | " + package.Width.ToString("N0", CultureInfo.InvariantCulture) +
                        " x " + package.Height.ToString("N0", CultureInfo.InvariantCulture) + " mm");
                AddBookGenerated(transaction, paperSpace, title, package.LayoutName, generated);

                Table register = BuildBookRegister(
                    database,
                    new Point3d(
                        margin * 1.5,
                        package.Height - margin * 4.8,
                        0.0),
                    package,
                    snapshot,
                    titleHeight * 0.5);
                AddBookGenerated(transaction, paperSpace, register, package.LayoutName, generated);
                register.GenerateLayout();

                var note = new MText();
                note.SetDatabaseDefaults(database);
                note.Location = new Point3d(margin * 1.5, margin * 1.8, 0.0);
                note.TextHeight = titleHeight * 0.45;
                note.Width = package.Width - margin * 3.0;
                note.Contents =
                    "CE Tools created the true-size A-series paper-space frame and drawing register. " +
                    "Assign the office-approved PC3, CTB/STB and canonical media before publishing. " +
                    "Client books use A4/A3; construction sets use A1/A0.";
                AddBookGenerated(transaction, paperSpace, note, package.LayoutName, generated);

                WriteBookLink(
                    layout,
                    transaction,
                    new BookLink(
                        SchemaVersion,
                        package.LayoutName,
                        package.PaperName,
                        package.Purpose,
                        package.Width,
                        package.Height,
                        generated));
                transaction.Commit();
            }
            return created;
        }

        private static Table BuildBookRegister(
            Database database,
            Point3d position,
            BookPackage package,
            ProjectSnapshot snapshot,
            double textHeight)
        {
            List<LayoutSnapshot> layouts = snapshot.Layouts
                .Where(item => !Equal(item.Name, package.LayoutName))
                .Take(package.PaperName == "A4" ? 10 : 20)
                .ToList();
            if (layouts.Count == 0)
                layouts.Add(new LayoutSnapshot("No project layouts detected", 0));

            var table = new Table();
            table.SetDatabaseDefaults(database);
            table.TableStyle = database.Tablestyle;
            table.Position = position;
            table.SetSize(layouts.Count + 2, 4);
            table.SetRowHeight(textHeight * 2.0);
            double available = package.Width * 0.72;
            table.Columns[0].Width = available * 0.10;
            table.Columns[1].Width = available * 0.42;
            table.Columns[2].Width = available * 0.28;
            table.Columns[3].Width = available * 0.20;
            table.MergeCells(CellRange.Create(table, 0, 0, 0, 3));
            table.Cells[0, 0].TextString = "DRAWING BOOK REGISTER";
            string[] headings = { "NO.", "LAYOUT / DRAWING", "PURPOSE", "STATUS" };
            for (int column = 0; column < headings.Length; column++)
                table.Cells[1, column].TextString = headings[column];
            for (int index = 0; index < layouts.Count; index++)
            {
                int row = index + 2;
                table.Cells[row, 0].TextString = (index + 1).ToString(CultureInfo.InvariantCulture);
                table.Cells[row, 1].TextString = layouts[index].Name;
                table.Cells[row, 2].TextString = package.Purpose;
                table.Cells[row, 3].TextString = "For review";
            }
            for (int row = 0; row < table.Rows.Count; row++)
                for (int column = 0; column < table.Columns.Count; column++)
                    table.Cells[row, column].TextHeight = textHeight;
            return table;
        }

        private static void AddBookGenerated(
            Transaction transaction,
            BlockTableRecord paperSpace,
            Entity entity,
            string layoutName,
            ICollection<string> handles)
        {
            paperSpace.AppendEntity(entity);
            transaction.AddNewlyCreatedDBObject(entity, true);
            entity.CreateExtensionDictionary();
            DBDictionary dictionary = transaction.GetObject(
                entity.ExtensionDictionary,
                OpenMode.ForWrite,
                false) as DBDictionary;
            Xrecord record = OpenOrCreateRecord(
                dictionary,
                BookRecordName,
                transaction);
            record.Data = new ResultBuffer(
                new TypedValue((int)DxfCode.Text, "Layout=" + layoutName));
            handles.Add(entity.Handle.ToString());
        }

        private static void WriteSummaryLink(
            Entity anchor,
            Transaction transaction,
            SummaryLink link)
        {
            DBDictionary dictionary = transaction.GetObject(
                anchor.ExtensionDictionary,
                OpenMode.ForWrite,
                false) as DBDictionary;
            Xrecord record = OpenOrCreateRecord(
                dictionary,
                SummaryRecordName,
                transaction);
            var values = new List<TypedValue>
            {
                new TypedValue((int)DxfCode.Text, "Schema=" + SchemaVersion),
                new TypedValue((int)DxfCode.Text, "Anchor=" + link.AnchorHandle),
                new TypedValue((int)DxfCode.Text, "InsertionX=" + link.InsertionPoint.X.ToString("R", CultureInfo.InvariantCulture)),
                new TypedValue((int)DxfCode.Text, "InsertionY=" + link.InsertionPoint.Y.ToString("R", CultureInfo.InvariantCulture)),
                new TypedValue((int)DxfCode.Text, "InsertionZ=" + link.InsertionPoint.Z.ToString("R", CultureInfo.InvariantCulture))
            };
            foreach (string handle in link.GeneratedHandles)
                values.Add(new TypedValue((int)DxfCode.Text, "Generated=" + handle));
            record.Data = new ResultBuffer(values.ToArray());
        }

        private static SummaryLink ReadSummaryLink(
            Entity anchor,
            Transaction transaction)
        {
            if (anchor == null || anchor.ExtensionDictionary.IsNull)
                throw new InvalidOperationException("The selected object is not a linked summary anchor.");
            DBDictionary dictionary = transaction.GetObject(
                anchor.ExtensionDictionary,
                OpenMode.ForRead,
                false) as DBDictionary;
            if (dictionary == null || !dictionary.Contains(SummaryRecordName))
                throw new InvalidOperationException("The selected object is not a linked summary anchor.");
            Xrecord record = transaction.GetObject(
                dictionary.GetAt(SummaryRecordName),
                OpenMode.ForRead,
                false) as Xrecord;
            if (record == null || record.Data == null)
                throw new InvalidOperationException("The summary link record is empty.");

            string schema = SchemaVersion;
            string anchorHandle = anchor.Handle.ToString();
            double x = 0.0;
            double y = 0.0;
            double z = 0.0;
            var generated = new List<string>();
            foreach (TypedValue value in record.Data)
            {
                string text = value.Value as string;
                if (string.IsNullOrWhiteSpace(text)) continue;
                if (text.StartsWith("Schema=", StringComparison.OrdinalIgnoreCase))
                    schema = text.Substring("Schema=".Length);
                else if (text.StartsWith("Anchor=", StringComparison.OrdinalIgnoreCase))
                    anchorHandle = text.Substring("Anchor=".Length);
                else if (text.StartsWith("InsertionX=", StringComparison.OrdinalIgnoreCase))
                    ParseInvariant(text.Substring("InsertionX=".Length), out x);
                else if (text.StartsWith("InsertionY=", StringComparison.OrdinalIgnoreCase))
                    ParseInvariant(text.Substring("InsertionY=".Length), out y);
                else if (text.StartsWith("InsertionZ=", StringComparison.OrdinalIgnoreCase))
                    ParseInvariant(text.Substring("InsertionZ=".Length), out z);
                else if (text.StartsWith("Generated=", StringComparison.OrdinalIgnoreCase))
                    generated.Add(text.Substring("Generated=".Length));
            }
            return new SummaryLink(
                schema,
                anchorHandle,
                new Point3d(x, y, z),
                generated);
        }

        private static void WriteBookLink(
            Layout layout,
            Transaction transaction,
            BookLink link)
        {
            if (layout.ExtensionDictionary.IsNull)
                layout.CreateExtensionDictionary();
            DBDictionary dictionary = transaction.GetObject(
                layout.ExtensionDictionary,
                OpenMode.ForWrite,
                false) as DBDictionary;
            Xrecord record = OpenOrCreateRecord(
                dictionary,
                BookRecordName,
                transaction);
            var values = new List<TypedValue>
            {
                new TypedValue((int)DxfCode.Text, "Schema=" + SchemaVersion),
                new TypedValue((int)DxfCode.Text, "Layout=" + link.LayoutName),
                new TypedValue((int)DxfCode.Text, "Paper=" + link.PaperName),
                new TypedValue((int)DxfCode.Text, "Purpose=" + link.Purpose),
                new TypedValue((int)DxfCode.Text, "Width=" + link.Width.ToString("R", CultureInfo.InvariantCulture)),
                new TypedValue((int)DxfCode.Text, "Height=" + link.Height.ToString("R", CultureInfo.InvariantCulture))
            };
            foreach (string handle in link.GeneratedHandles)
                values.Add(new TypedValue((int)DxfCode.Text, "Generated=" + handle));
            record.Data = new ResultBuffer(values.ToArray());
        }

        private static BookLink ReadBookLinkIfPresent(
            Layout layout,
            Transaction transaction)
        {
            if (layout == null || layout.ExtensionDictionary.IsNull) return null;
            DBDictionary dictionary = transaction.GetObject(
                layout.ExtensionDictionary,
                OpenMode.ForRead,
                false) as DBDictionary;
            if (dictionary == null || !dictionary.Contains(BookRecordName)) return null;
            Xrecord record = transaction.GetObject(
                dictionary.GetAt(BookRecordName),
                OpenMode.ForRead,
                false) as Xrecord;
            if (record == null || record.Data == null) return null;

            string schema = SchemaVersion;
            string layoutName = layout.LayoutName;
            string paper = string.Empty;
            string purpose = string.Empty;
            double width = 0.0;
            double height = 0.0;
            var generated = new List<string>();
            foreach (TypedValue value in record.Data)
            {
                string text = value.Value as string;
                if (string.IsNullOrWhiteSpace(text)) continue;
                if (text.StartsWith("Schema=", StringComparison.OrdinalIgnoreCase))
                    schema = text.Substring("Schema=".Length);
                else if (text.StartsWith("Layout=", StringComparison.OrdinalIgnoreCase))
                    layoutName = text.Substring("Layout=".Length);
                else if (text.StartsWith("Paper=", StringComparison.OrdinalIgnoreCase))
                    paper = text.Substring("Paper=".Length);
                else if (text.StartsWith("Purpose=", StringComparison.OrdinalIgnoreCase))
                    purpose = text.Substring("Purpose=".Length);
                else if (text.StartsWith("Width=", StringComparison.OrdinalIgnoreCase))
                    ParseInvariant(text.Substring("Width=".Length), out width);
                else if (text.StartsWith("Height=", StringComparison.OrdinalIgnoreCase))
                    ParseInvariant(text.Substring("Height=".Length), out height);
                else if (text.StartsWith("Generated=", StringComparison.OrdinalIgnoreCase))
                    generated.Add(text.Substring("Generated=".Length));
            }
            return new BookLink(
                schema,
                layoutName,
                paper,
                purpose,
                width,
                height,
                generated);
        }

        private static bool PromptForSummaryAnchor(
            Document document,
            out ObjectId anchorId)
        {
            anchorId = ObjectId.Null;
            PromptEntityResult result = document.Editor.GetEntity(
                "\nSelect a linked summary frame or generated summary object: ");
            if (result.Status != PromptStatus.OK) return false;

            using (Transaction transaction =
                document.Database.TransactionManager.StartTransaction())
            {
                Entity selected = transaction.GetObject(
                    result.ObjectId,
                    OpenMode.ForRead,
                    false) as Entity;
                if (selected == null) return false;
                if (HasRecord(selected, transaction, SummaryRecordName))
                {
                    anchorId = result.ObjectId;
                    return true;
                }

                string anchorHandle;
                if (TryReadSingleTextRecord(
                    selected,
                    transaction,
                    SummaryGeneratedRecordName,
                    "Anchor=",
                    out anchorHandle) &&
                    TryResolveHandle(document.Database, anchorHandle, out anchorId))
                    return true;
            }

            document.Editor.WriteMessage(
                "\nThe selected object is not part of a CE Tools linked summary sheet.");
            return false;
        }

        private static bool TryReadSingleTextRecord(
            Entity entity,
            Transaction transaction,
            string recordName,
            string prefix,
            out string result)
        {
            result = string.Empty;
            if (entity == null || entity.ExtensionDictionary.IsNull) return false;
            DBDictionary dictionary = transaction.GetObject(
                entity.ExtensionDictionary,
                OpenMode.ForRead,
                false) as DBDictionary;
            if (dictionary == null || !dictionary.Contains(recordName)) return false;
            Xrecord record = transaction.GetObject(
                dictionary.GetAt(recordName),
                OpenMode.ForRead,
                false) as Xrecord;
            if (record == null || record.Data == null) return false;
            foreach (TypedValue value in record.Data)
            {
                string text = value.Value as string;
                if (text != null && text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    result = text.Substring(prefix.Length);
                    return !string.IsNullOrWhiteSpace(result);
                }
            }
            return false;
        }

        private static ProjectMetadataSnapshot ReadProjectMetadata(Database database)
        {
            var metadata = new ProjectMetadataSnapshot();
            try
            {
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    DBDictionary named = transaction.GetObject(
                        database.NamedObjectsDictionaryId,
                        OpenMode.ForRead,
                        false) as DBDictionary;
                    if (named == null || !named.Contains("CE_TOOLS")) return metadata;
                    DBDictionary root = transaction.GetObject(
                        named.GetAt("CE_TOOLS"),
                        OpenMode.ForRead,
                        false) as DBDictionary;
                    if (root == null || !root.Contains("PROJECT_METADATA")) return metadata;
                    Xrecord record = transaction.GetObject(
                        root.GetAt("PROJECT_METADATA"),
                        OpenMode.ForRead,
                        false) as Xrecord;
                    if (record == null || record.Data == null) return metadata;

                    string pending = null;
                    foreach (TypedValue value in record.Data)
                    {
                        string text = value.Value as string;
                        if (text == null) continue;
                        if (pending == null) pending = text;
                        else
                        {
                            if (!Equal(pending, "Schema")) metadata.Set(pending, text);
                            pending = null;
                        }
                    }
                }
            }
            catch
            {
                // Metadata is optional; reports continue with drawing inventory.
            }
            return metadata;
        }

        private static ReportDiscipline ClassifyDiscipline(string source)
        {
            string text = (source ?? string.Empty).ToUpperInvariant();
            if (ContainsAny(text, "BULK WATER", "BULK-WATER", "BULKWATER", "RESERVOIR", "PUMP STATION"))
                return ReportDiscipline.BulkWater;
            if (ContainsAny(text, "STORM", "DRAIN", "CULVERT", "CATCHPIT", "INLET", "OUTLET"))
                return ReportDiscipline.Stormwater;
            if (ContainsAny(text, "SEWER", "SANITARY", "WASTEWATER"))
                return ReportDiscipline.Sewer;
            if (ContainsAny(text, "WATER", "HYDRANT", "VALVE", "METER", "PRESSURE"))
                return ReportDiscipline.Water;
            if (ContainsAny(text, "PLATFORM", "GRADING", "EARTHWORK", "CUT", "FILL", "PAD"))
                return ReportDiscipline.Platform;
            if (ContainsAny(
                text,
                "ROAD", "KERB", "CURB", "SURFACING", "ASPHALT", "PAVEMENT",
                "SIDEWALK", "FOOTWAY", "PARKING", "DRIVEWAY", "MARKING", "SIGN"))
                return ReportDiscipline.Road;
            return ReportDiscipline.General;
        }

        private static bool TryGetLength(DBObject value, out double length)
        {
            length = 0.0;
            var curve = value as Curve;
            if (curve != null)
            {
                try
                {
                    length = Math.Abs(
                        curve.GetDistanceAtParameter(curve.EndParam) -
                        curve.GetDistanceAtParameter(curve.StartParam));
                    if (IsFinitePositive(length)) return true;
                }
                catch
                {
                    // Continue to runtime properties.
                }
            }
            return TryReadDoubleProperty(
                value,
                out length,
                "Length3DCenterToCenter",
                "Length2DCenterToCenter",
                "Length3D",
                "Length2D",
                "Length");
        }

        private static bool TryGetArea(Entity entity, out double area)
        {
            area = 0.0;
            try
            {
                var hatch = entity as Hatch;
                if (hatch != null)
                {
                    area = Math.Abs(hatch.Area);
                    return IsFinitePositive(area);
                }
                var region = entity as Region;
                if (region != null)
                {
                    area = Math.Abs(region.Area);
                    return IsFinitePositive(area);
                }
                var curve = entity as Curve;
                if (curve != null && curve.Closed)
                {
                    area = Math.Abs(curve.Area);
                    return IsFinitePositive(area);
                }
            }
            catch
            {
                // Continue to runtime area properties.
            }
            return TryReadDoubleProperty(entity, out area, "Area2D", "SurfaceArea", "Area");
        }

        private static bool TryGetVolume(DBObject value, out double volume)
        {
            volume = 0.0;
            if (TryReadDoubleProperty(value, out volume, "Volume")) return true;
            try
            {
                PropertyInfo property = value.GetType().GetProperty(
                    "MassProperties",
                    BindingFlags.Public | BindingFlags.Instance);
                object mass = property == null ? null : property.GetValue(value, null);
                return mass != null && TryReadDoubleProperty(mass, out volume, "Volume");
            }
            catch
            {
                return false;
            }
        }

        private static bool TryReadDoubleProperty(
            object value,
            out double number,
            params string[] names)
        {
            number = 0.0;
            if (value == null) return false;
            foreach (string name in names)
            {
                try
                {
                    PropertyInfo property = value.GetType().GetProperty(
                        name,
                        BindingFlags.Public | BindingFlags.Instance);
                    if (property == null || property.GetIndexParameters().Length != 0)
                        continue;
                    object raw = property.GetValue(value, null);
                    if (raw == null) continue;
                    number = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
                    if (IsFinitePositive(number)) return true;
                }
                catch
                {
                    // Try the next property.
                }
            }
            return false;
        }

        private static string ReadObjectName(DBObject value, Transaction transaction)
        {
            var block = value as BlockReference;
            if (block != null)
            {
                try
                {
                    ObjectId recordId = block.IsDynamicBlock
                        ? block.DynamicBlockTableRecord
                        : block.BlockTableRecord;
                    BlockTableRecord record = transaction.GetObject(
                        recordId,
                        OpenMode.ForRead,
                        false) as BlockTableRecord;
                    if (record != null) return record.Name;
                }
                catch
                {
                    return "BlockReference";
                }
            }
            try
            {
                PropertyInfo property = value.GetType().GetProperty(
                    "Name",
                    BindingFlags.Public | BindingFlags.Instance);
                string name = property == null
                    ? null
                    : property.GetValue(value, null) as string;
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }
            catch
            {
                // Fall through.
            }
            return value.GetType().Name;
        }

        private static string BuildGroupDetail(
            ReportDiscipline discipline,
            string layer,
            string type,
            string name)
        {
            string source = (layer + " " + type + " " + name).ToUpperInvariant();
            if (ContainsAny(source, "SURFACE")) return "Surface / terrain model";
            if (ContainsAny(source, "ALIGNMENT")) return "Alignment / route geometry";
            if (ContainsAny(source, "PROFILE")) return "Longitudinal profile";
            if (ContainsAny(source, "CORRIDOR")) return "Road corridor model";
            if (ContainsAny(source, "FEATURELINE")) return "Grading feature line";
            if (ContainsAny(source, "PIPE")) return discipline + " pipe";
            if (ContainsAny(source, "STRUCTURE", "MANHOLE", "CATCHPIT"))
                return discipline + " structure";
            if (ContainsAny(source, "HATCH")) return "Area/material representation";
            if (ContainsAny(source, "TABLE")) return "Drawing table / schedule";
            return string.IsNullOrWhiteSpace(name) ? FriendlyTypeName(type) : name;
        }

        private static List<IList<string>> BuildReportExportRows(ProjectSnapshot snapshot)
        {
            var rows = new List<IList<string>>
            {
                new List<string>
                {
                    "CE TOOLS " + snapshot.Filter.ToString().ToUpperInvariant() + " DESIGN REPORT",
                    string.Empty, string.Empty, string.Empty,
                    string.Empty, string.Empty, string.Empty, string.Empty
                },
                new List<string>
                {
                    "DISCIPLINE", "LAYER", "OBJECT TYPE", "COUNT",
                    "LENGTH", "AREA", "VOLUME", "STATUS / DETAIL"
                }
            };
            foreach (ReportGroup group in snapshot.Groups)
            {
                rows.Add(new List<string>
                {
                    group.Discipline.ToString(),
                    group.Layer,
                    group.TypeName,
                    group.Count.ToString(CultureInfo.InvariantCulture),
                    group.Length.ToString("0.###", CultureInfo.InvariantCulture),
                    group.Area.ToString("0.###", CultureInfo.InvariantCulture),
                    group.Volume.ToString("0.###", CultureInfo.InvariantCulture),
                    group.Detail
                });
            }
            rows.Add(new List<string>
            {
                "PROJECT", snapshot.Project.Get("Project Name"),
                "CLIENT", snapshot.Project.Get("Client"),
                "LAYOUTS", snapshot.Layouts.Count.ToString(CultureInfo.InvariantCulture),
                "LINKED SECTIONS", snapshot.LinkedSectionCount.ToString(CultureInfo.InvariantCulture)
            });
            return rows;
        }

        private static string BuildReportNote(ProjectSnapshot snapshot)
        {
            return string.Format(
                CultureInfo.CurrentCulture,
                "Project: {0}; Client: {1}; groups: {2}; linked BOQs: {3}; linked cross sections: {4}; layouts: {5}. " +
                "Classification uses layer, object name and runtime type and must be checked against project standards.",
                ValueOrNotSet(snapshot.Project.Get("Project Name")),
                ValueOrNotSet(snapshot.Project.Get("Client")),
                snapshot.Groups.Count,
                snapshot.LinkedBoqCount,
                snapshot.LinkedSectionCount,
                snapshot.Layouts.Count);
        }

        private static void WriteSnapshotPreview(Editor editor, ProjectSnapshot snapshot)
        {
            editor.WriteMessage(
                "\nCE Tools production snapshot. Filter={0}; groups={1}; layouts={2}; linked BOQs={3}; linked sections={4}; rejected={5}.",
                snapshot.Filter,
                snapshot.Groups.Count,
                snapshot.Layouts.Count,
                snapshot.LinkedBoqCount,
                snapshot.LinkedSectionCount,
                snapshot.Rejected);
        }

        private static List<DisciplineSummary> BuildDisciplineSummaries(
            ProjectSnapshot snapshot)
        {
            var map = new SortedDictionary<ReportDiscipline, DisciplineSummary>();
            foreach (ReportGroup group in snapshot.Groups)
            {
                DisciplineSummary summary;
                if (!map.TryGetValue(group.Discipline, out summary))
                {
                    summary = new DisciplineSummary(group.Discipline);
                    map.Add(group.Discipline, summary);
                }
                summary.Count += group.Count;
                summary.Length += group.Length;
                summary.Area += group.Area;
                summary.Volume += group.Volume;
            }
            if (map.Count == 0)
                map.Add(ReportDiscipline.General, new DisciplineSummary(ReportDiscipline.General));
            return map.Values.ToList();
        }

        private static bool PromptDiscipline(
            Editor editor,
            bool includeAll,
            out ReportDiscipline discipline)
        {
            string allText = includeAll ? "All/" : string.Empty;
            var options = new PromptKeywordOptions(
                "\nReport discipline [" + allText +
                "General/Road/Platform/Stormwater/Sewer/Water/BulkWater] <" +
                (includeAll ? "All" : "General") + ">: ")
            {
                AllowNone = true
            };
            if (includeAll) options.Keywords.Add("All");
            foreach (string keyword in new[]
            {
                "General", "Road", "Platform", "Stormwater", "Sewer", "Water", "BulkWater"
            })
                options.Keywords.Add(keyword);
            PromptResult result = editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel)
            {
                discipline = ReportDiscipline.General;
                return false;
            }
            string selected = result.Status == PromptStatus.None
                ? (includeAll ? "All" : "General")
                : result.StringResult;
            return Enum.TryParse(selected, true, out discipline);
        }

        private static bool PromptExcelPath(
            Editor editor,
            string defaultName,
            out string path)
        {
            var options = new PromptSaveFileOptions(
                "\nSelect Excel workbook output path: ")
            {
                Filter = "Excel Workbook (*.xlsx)|*.xlsx",
                DialogCaption = "Export CE Tools Design Report",
                InitialFileName = defaultName
            };
            PromptFileNameResult result = editor.GetFileNameForSave(options);
            if (result.Status != PromptStatus.OK)
            {
                path = string.Empty;
                return false;
            }
            path = result.StringResult;
            if (!path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
                path += ".xlsx";
            return true;
        }

        private static bool Confirm(Editor editor, string message)
        {
            var options = new PromptKeywordOptions(
                "\n" + message + "? [Yes/No] <No>: ")
            {
                AllowNone = true
            };
            options.Keywords.Add("Yes");
            options.Keywords.Add("No");
            PromptResult result = editor.GetKeywords(options);
            return result.Status == PromptStatus.OK && Equal(result.StringResult, "Yes");
        }

        private static ObjectId FindLayoutId(Database database, string layoutName)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                DBDictionary layouts = transaction.GetObject(
                    database.LayoutDictionaryId,
                    OpenMode.ForRead,
                    false) as DBDictionary;
                if (layouts != null && layouts.Contains(layoutName))
                    return layouts.GetAt(layoutName);
            }
            return ObjectId.Null;
        }

        private static bool HasRecord(
            DBObject value,
            Transaction transaction,
            string recordName)
        {
            if (value == null || value.ExtensionDictionary.IsNull) return false;
            DBDictionary dictionary = transaction.GetObject(
                value.ExtensionDictionary,
                OpenMode.ForRead,
                false) as DBDictionary;
            return dictionary != null && dictionary.Contains(recordName);
        }

        private static Xrecord OpenOrCreateRecord(
            DBDictionary dictionary,
            string name,
            Transaction transaction)
        {
            if (dictionary == null)
                throw new InvalidOperationException("The CE Tools extension dictionary is unavailable.");
            if (dictionary.Contains(name))
                return transaction.GetObject(
                    dictionary.GetAt(name),
                    OpenMode.ForWrite,
                    false) as Xrecord;
            var record = new Xrecord();
            dictionary.SetAt(name, record);
            transaction.AddNewlyCreatedDBObject(record, true);
            return record;
        }

        private static bool TryResolveHandle(
            Database database,
            string handleText,
            out ObjectId objectId)
        {
            return DynamicCrossSectionCommands.TryResolveHandle(
                database,
                handleText,
                out objectId);
        }

        private static bool ParseInvariant(string text, out double value)
        {
            return double.TryParse(
                text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out value);
        }

        private static string LayoutStatus(ProjectSnapshot snapshot, string name)
        {
            return snapshot.Layouts.Any(item => Equal(item.Name, name))
                ? "Available"
                : "Missing";
        }

        private static string ValueOrNotSet(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "<Not set>" : value;
        }

        private static double ResolveTextHeight(Database database)
        {
            double height = database == null ? 2.0 : database.Textsize;
            if (Math.Abs(height - 1.8) < 0.05) return 1.8;
            if (Math.Abs(height - 5.0) < 0.05) return 5.0;
            return 2.0;
        }

        private static string FriendlyTypeName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "Design element";
            var result = new List<char>();
            for (int index = 0; index < value.Length; index++)
            {
                char character = value[index];
                if (index > 0 && char.IsUpper(character) && !char.IsUpper(value[index - 1]))
                    result.Add(' ');
                result.Add(character);
            }
            return new string(result.ToArray());
        }

        private static bool ContainsAny(string source, params string[] values)
        {
            if (string.IsNullOrWhiteSpace(source)) return false;
            foreach (string value in values)
            {
                if (source.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        private static bool IsFinitePositive(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value) && value > 0.0;
        }

        private static bool Equal(string left, string right)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        private static Document ActiveDocument()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
        }

        private static List<BookPackage> StandardBookPackages()
        {
            return new List<BookPackage>
            {
                new BookPackage("CE-CLIENT-A4", "A4", "Client drawing book / issue set", 297.0, 210.0),
                new BookPackage("CE-CLIENT-A3", "A3", "Client drawing book / presentation set", 420.0, 297.0),
                new BookPackage("CE-CONSTRUCTION-A1", "A1", "Construction drawing set", 841.0, 594.0),
                new BookPackage("CE-CONSTRUCTION-A0", "A0", "Large-format construction drawing set", 1189.0, 841.0)
            };
        }

        private enum ReportDiscipline
        {
            All,
            General,
            Road,
            Platform,
            Stormwater,
            Sewer,
            Water,
            BulkWater
        }

        private sealed class ProjectSnapshot
        {
            public ProjectSnapshot(ReportDiscipline filter)
            {
                Filter = filter;
                Project = new ProjectMetadataSnapshot();
                Groups = new List<ReportGroup>();
                Layouts = new List<LayoutSnapshot>();
            }

            public ReportDiscipline Filter { get; }
            public ProjectMetadataSnapshot Project { get; set; }
            public List<ReportGroup> Groups { get; set; }
            public List<LayoutSnapshot> Layouts { get; set; }
            public int LinkedBoqCount { get; set; }
            public int LinkedSectionCount { get; set; }
            public int Rejected { get; set; }
        }

        private sealed class ProjectMetadataSnapshot
        {
            private readonly Dictionary<string, string> _values =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            public string Get(string name)
            {
                string value;
                return _values.TryGetValue(name, out value) ? value : string.Empty;
            }

            public void Set(string name, string value)
            {
                _values[name] = value ?? string.Empty;
            }
        }

        private sealed class ReportGroup
        {
            public ReportGroup(
                ReportDiscipline discipline,
                string layer,
                string typeName)
            {
                Discipline = discipline;
                Layer = layer;
                TypeName = typeName;
                Detail = string.Empty;
            }

            public ReportDiscipline Discipline { get; }
            public string Layer { get; }
            public string TypeName { get; }
            public int Count { get; set; }
            public double Length { get; set; }
            public double Area { get; set; }
            public double Volume { get; set; }
            public string Detail { get; set; }
        }

        private sealed class DisciplineSummary
        {
            public DisciplineSummary(ReportDiscipline discipline)
            {
                Discipline = discipline;
            }

            public ReportDiscipline Discipline { get; }
            public int Count { get; set; }
            public double Length { get; set; }
            public double Area { get; set; }
            public double Volume { get; set; }
        }

        private sealed class LayoutSnapshot
        {
            public LayoutSnapshot(string name, int tabOrder)
            {
                Name = name;
                TabOrder = tabOrder;
            }

            public string Name { get; }
            public int TabOrder { get; }
        }

        private sealed class SummaryLink
        {
            public SummaryLink(
                string schema,
                string anchorHandle,
                Point3d insertionPoint,
                IEnumerable<string> generatedHandles)
            {
                Schema = schema;
                AnchorHandle = anchorHandle;
                InsertionPoint = insertionPoint;
                GeneratedHandles = generatedHandles == null
                    ? new List<string>()
                    : generatedHandles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }

            public string Schema { get; }
            public string AnchorHandle { get; }
            public Point3d InsertionPoint { get; }
            public List<string> GeneratedHandles { get; }
        }

        private sealed class BookPackage
        {
            public BookPackage(
                string layoutName,
                string paperName,
                string purpose,
                double width,
                double height)
            {
                LayoutName = layoutName;
                PaperName = paperName;
                Purpose = purpose;
                Width = width;
                Height = height;
            }

            public string LayoutName { get; }
            public string PaperName { get; }
            public string Purpose { get; }
            public double Width { get; }
            public double Height { get; }
        }

        private sealed class BookLink
        {
            public BookLink(
                string schema,
                string layoutName,
                string paperName,
                string purpose,
                double width,
                double height,
                IEnumerable<string> generatedHandles)
            {
                Schema = schema;
                LayoutName = layoutName;
                PaperName = paperName;
                Purpose = purpose;
                Width = width;
                Height = height;
                GeneratedHandles = generatedHandles == null
                    ? new List<string>()
                    : generatedHandles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }

            public string Schema { get; }
            public string LayoutName { get; }
            public string PaperName { get; }
            public string Purpose { get; }
            public double Width { get; }
            public double Height { get; }
            public List<string> GeneratedHandles { get; }
        }
    }
}

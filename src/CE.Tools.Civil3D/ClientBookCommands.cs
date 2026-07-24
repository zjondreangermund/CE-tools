using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.ClientBookCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Creates a presentation-ready, linked A4/A3 client book at project closeout.
    /// Pages are regenerated from current project metadata, model-space inventory,
    /// linked BOQs, linked dynamic sections and existing project layouts.
    /// </summary>
    public sealed class ClientBookCommands
    {
        private const string LinkRecordName = "CE_CLIENT_BOOK_PAGE";
        private const string SchemaVersion = "1";
        private const string ProjectRootDictionary = "CE_TOOLS";
        private const string ProjectMetadataRecord = "PROJECT_METADATA";

        [CommandMethod(
            "CE_TOOLS",
            "CE_PROJECTCLOSEOUT",
            CommandFlags.Modal | CommandFlags.Redraw)]
        public void ProjectCloseout()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            CreateClientBook(document, ClientPaperSelection.Both, true);
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_CLIENTBOOK",
            CommandFlags.Modal | CommandFlags.Redraw)]
        public void ClientBook()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            ClientPaperSelection selection;
            if (!PromptPaperSelection(document.Editor, out selection)) return;
            CreateClientBook(document, selection, false);
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_CLIENTBOOKREFRESH",
            CommandFlags.Modal | CommandFlags.Redraw)]
        public void RefreshClientBook()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            List<ClientPageLink> existing = ReadAllClientPageLinks(document.Database);
            if (existing.Count == 0)
            {
                document.Editor.WriteMessage(
                    "\nCE_CLIENTBOOKREFRESH: no linked CE Tools client-book pages were found.");
                return;
            }

            ClientSnapshot snapshot = BuildSnapshot(document.Database);
            WriteSnapshotPreview(document.Editor, snapshot);
            if (!Confirm(
                document.Editor,
                "Refresh all linked A4/A3 client-book pages from the current project"))
                return;

            int refreshed = 0;
            int failed = 0;
            foreach (ClientPageLink link in existing)
            {
                ClientPageDefinition page = FindPageDefinition(
                    link.Paper,
                    link.PageKey);
                if (page == null)
                {
                    failed++;
                    continue;
                }

                try
                {
                    CreateOrRefreshPage(
                        document.Database,
                        page,
                        link.Stage,
                        link.Revision,
                        snapshot);
                    refreshed++;
                }
                catch (System.Exception exception)
                {
                    failed++;
                    document.Editor.WriteMessage(
                        "\n  Failed to refresh {0}: {1}",
                        link.LayoutName,
                        exception.Message);
                }
            }

            document.Editor.WriteMessage(
                "\nCE_CLIENTBOOKREFRESH complete. Refreshed={0}; failed={1}.",
                refreshed,
                failed);
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_CLIENTBOOKINFO",
            CommandFlags.Modal | CommandFlags.Redraw)]
        public void ClientBookInformation()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            List<ClientPageLink> links = ReadAllClientPageLinks(document.Database);
            var rows = new List<IList<string>>();
            foreach (ClientPageLink link in links
                .OrderBy(item => item.Paper)
                .ThenBy(item => item.PageNumber))
            {
                int valid = 0;
                int stale = 0;
                foreach (string handle in link.GeneratedHandles)
                {
                    ObjectId id;
                    if (TryResolveHandle(document.Database, handle, out id)) valid++;
                    else stale++;
                }

                rows.Add(new List<string>
                {
                    link.Paper,
                    link.PageNumber,
                    link.Title,
                    link.LayoutName,
                    link.Stage,
                    link.Revision,
                    valid.ToString(CultureInfo.InvariantCulture),
                    stale.ToString(CultureInfo.InvariantCulture)
                });
            }

            if (rows.Count == 0)
            {
                rows.Add(new List<string>
                {
                    string.Empty,
                    string.Empty,
                    "No linked client-book pages",
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    "0",
                    "0"
                });
            }

            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Tools Client Book",
                "A4/A3 client-book pages are linked to current drawing information through CE_CLIENTBOOKREFRESH.",
                new List<string>
                {
                    "Paper", "Sheet", "Title", "Layout", "Stage", "Revision",
                    "Valid Objects", "Stale Handles"
                },
                rows,
                "CE TOOLS CLIENT BOOK REGISTER");
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_CLIENTBOOKINDEX",
            CommandFlags.Modal | CommandFlags.Redraw)]
        public void ExportClientBookIndex()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            List<ClientPageLink> links = ReadAllClientPageLinks(document.Database);
            if (links.Count == 0)
            {
                document.Editor.WriteMessage(
                    "\nCE_CLIENTBOOKINDEX: create a client book before exporting its index.");
                return;
            }

            string path;
            if (!PromptExcelPath(
                document.Editor,
                "CE-Tools-Client-Book-Index.xlsx",
                out path)) return;

            var rows = new List<IList<string>>
            {
                new List<string>
                {
                    "CE TOOLS CLIENT BOOK INDEX", string.Empty, string.Empty,
                    string.Empty, string.Empty, string.Empty
                },
                new List<string>
                {
                    "PAPER", "SHEET", "TITLE", "LAYOUT", "STAGE", "REVISION"
                }
            };
            foreach (ClientPageLink link in links
                .OrderBy(item => item.Paper)
                .ThenBy(item => item.PageNumber))
            {
                rows.Add(new List<string>
                {
                    link.Paper,
                    link.PageNumber,
                    link.Title,
                    link.LayoutName,
                    link.Stage,
                    link.Revision
                });
            }

            try
            {
                SimpleXlsxWriter.Write(path, "Client Book Index", rows);
                document.Editor.WriteMessage(
                    "\nCE_CLIENTBOOKINDEX complete. Pages={0}; workbook={1}",
                    links.Count,
                    path);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_CLIENTBOOKINDEX failed. {0}",
                    exception.Message);
            }
        }

        private static void CreateClientBook(
            Document document,
            ClientPaperSelection initialSelection,
            bool closeoutMode)
        {
            ClientPaperSelection selection = initialSelection;
            if (closeoutMode)
            {
                document.Editor.WriteMessage(
                    "\nCE Project Closeout creates or refreshes both the A4 and A3 client summary books.");
            }

            string stage;
            if (!PromptStage(document.Editor, out stage)) return;
            string revision;
            if (!PromptRevision(document.Editor, out revision)) return;

            ClientSnapshot snapshot = BuildSnapshot(document.Database);
            WriteSnapshotPreview(document.Editor, snapshot);
            List<ClientPageDefinition> pages = PageDefinitions(selection);
            document.Editor.WriteMessage(
                "\nClient-book preview. Paper={0}; pages={1}; stage={2}; revision={3}.",
                selection,
                pages.Count,
                stage,
                revision);
            foreach (IGrouping<string, ClientPageDefinition> group in pages.GroupBy(item => item.Paper))
            {
                document.Editor.WriteMessage(
                    "\n  {0}: {1} linked summary sheets.",
                    group.Key,
                    group.Count());
            }

            if (!Confirm(
                document.Editor,
                "Create or refresh the linked client summary book"))
                return;

            int created = 0;
            int refreshed = 0;
            int failed = 0;
            foreach (ClientPageDefinition page in pages)
            {
                try
                {
                    bool wasCreated = CreateOrRefreshPage(
                        document.Database,
                        page,
                        stage,
                        revision,
                        snapshot);
                    if (wasCreated) created++;
                    else refreshed++;
                }
                catch (System.Exception exception)
                {
                    failed++;
                    document.Editor.WriteMessage(
                        "\n  Failed to generate {0}: {1}",
                        page.LayoutName,
                        exception.Message);
                }
            }

            document.Editor.WriteMessage(
                "\n{0} complete. Pages created={1}; refreshed={2}; failed={3}. " +
                "Run CE_CLIENTBOOKREFRESH after project, quantity, section or layout changes.",
                closeoutMode ? "CE_PROJECTCLOSEOUT" : "CE_CLIENTBOOK",
                created,
                refreshed,
                failed);
        }

        private static bool CreateOrRefreshPage(
            Database database,
            ClientPageDefinition page,
            string stage,
            string revision,
            ClientSnapshot snapshot)
        {
            bool created = false;
            ObjectId layoutId = FindLayoutId(database, page.LayoutName);
            if (layoutId.IsNull)
            {
                layoutId = LayoutManager.Current.CreateLayout(page.LayoutName);
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
                        "Layout could not be opened: " + page.LayoutName);

                ClientPageLink oldLink = ReadClientPageLinkIfPresent(layout, transaction);
                if (oldLink != null)
                    EraseGenerated(database, transaction, oldLink.GeneratedHandles);

                BlockTableRecord paperSpace = transaction.GetObject(
                    layout.BlockTableRecordId,
                    OpenMode.ForWrite,
                    false) as BlockTableRecord;
                if (paperSpace == null)
                    throw new InvalidOperationException(
                        "Paper space could not be opened: " + page.LayoutName);

                var generated = new List<string>();
                double margin = page.Paper == "A4" ? 8.0 : 10.0;
                double titleBlockHeight = page.Paper == "A4" ? 27.0 : 32.0;
                double bodyText = page.Paper == "A4" ? 2.2 : 2.8;
                double headingText = page.Paper == "A4" ? 4.2 : 5.5;

                Polyline frame = Rectangle(
                    database,
                    margin,
                    margin,
                    page.Width - margin,
                    page.Height - margin,
                    8);
                AddGenerated(transaction, paperSpace, frame, generated);

                Line headerRule = new Line(
                    new Point3d(margin, page.Height - margin - 17.0, 0.0),
                    new Point3d(page.Width - margin, page.Height - margin - 17.0, 0.0));
                headerRule.SetDatabaseDefaults(database);
                headerRule.ColorIndex = 4;
                AddGenerated(transaction, paperSpace, headerRule, generated);

                MText brand = Text(
                    database,
                    new Point3d(margin + 3.0, page.Height - margin - 4.0, 0.0),
                    headingText,
                    "CE TOOLS  |  CLIENT PROJECT BOOK",
                    page.Width - margin * 2.0 - 6.0,
                    4);
                AddGenerated(transaction, paperSpace, brand, generated);

                MText pageTitle = Text(
                    database,
                    new Point3d(margin + 3.0, page.Height - margin - 10.0, 0.0),
                    bodyText * 1.15,
                    page.PageNumber + "  " + page.Title.ToUpperInvariant(),
                    page.Width - margin * 2.0 - 6.0,
                    2);
                AddGenerated(transaction, paperSpace, pageTitle, generated);

                double contentTop = page.Height - margin - 21.0;
                double contentBottom = margin + titleBlockHeight + 4.0;
                CreatePageContent(
                    database,
                    transaction,
                    paperSpace,
                    generated,
                    page,
                    stage,
                    revision,
                    snapshot,
                    margin + 3.0,
                    contentTop,
                    page.Width - margin * 2.0 - 6.0,
                    contentTop - contentBottom,
                    bodyText);

                Table titleBlock = BuildTitleBlock(
                    database,
                    new Point3d(margin, margin + titleBlockHeight, 0.0),
                    page,
                    stage,
                    revision,
                    snapshot,
                    bodyText);
                AddGenerated(transaction, paperSpace, titleBlock, generated);
                titleBlock.GenerateLayout();

                WriteClientPageLink(
                    layout,
                    transaction,
                    new ClientPageLink(
                        SchemaVersion,
                        page.LayoutName,
                        page.Paper,
                        page.PageKey,
                        page.PageNumber,
                        page.Title,
                        stage,
                        revision,
                        page.Width,
                        page.Height,
                        generated));
                transaction.Commit();
            }

            return created;
        }

        private static void CreatePageContent(
            Database database,
            Transaction transaction,
            BlockTableRecord paperSpace,
            ICollection<string> generated,
            ClientPageDefinition page,
            string stage,
            string revision,
            ClientSnapshot snapshot,
            double x,
            double top,
            double width,
            double availableHeight,
            double textHeight)
        {
            if (page.Kind == ClientPageKind.Cover)
            {
                CreateCoverContent(
                    database,
                    transaction,
                    paperSpace,
                    generated,
                    page,
                    stage,
                    revision,
                    snapshot,
                    x,
                    top,
                    width,
                    textHeight);
                return;
            }

            Table table;
            if (page.Kind == ClientPageKind.ProjectSummary)
                table = BuildProjectSummaryTable(database, new Point3d(x, top, 0.0), snapshot, textHeight, width);
            else if (page.Kind == ClientPageKind.DesignSummary)
                table = BuildDesignSummaryTable(database, new Point3d(x, top, 0.0), snapshot, textHeight, width);
            else if (page.Kind == ClientPageKind.QuantitySummary)
                table = BuildQuantitySummaryTable(database, new Point3d(x, top, 0.0), snapshot, textHeight, width, page.Paper);
            else if (page.Kind == ClientPageKind.DrawingRegister)
                table = BuildDrawingRegisterTable(database, new Point3d(x, top, 0.0), snapshot, textHeight, width, page.Paper);
            else if (page.Kind == ClientPageKind.SectionRegister)
                table = BuildSectionRegisterTable(database, new Point3d(x, top, 0.0), snapshot, textHeight, width, page.Paper);
            else
                table = BuildTypicalDetailsTable(database, new Point3d(x, top, 0.0), snapshot, textHeight, width, page.Paper);

            AddGenerated(transaction, paperSpace, table, generated);
            table.GenerateLayout();

            MText note = Text(
                database,
                new Point3d(x, Math.Max(8.0, top - availableHeight + textHeight * 1.5), 0.0),
                textHeight * 0.75,
                PageNote(page.Kind),
                width,
                8);
            AddGenerated(transaction, paperSpace, note, generated);
        }

        private static void CreateCoverContent(
            Database database,
            Transaction transaction,
            BlockTableRecord paperSpace,
            ICollection<string> generated,
            ClientPageDefinition page,
            string stage,
            string revision,
            ClientSnapshot snapshot,
            double x,
            double top,
            double width,
            double textHeight)
        {
            string project = ValueOrNotSet(snapshot.Project.Get("Project Name"));
            string client = ValueOrNotSet(snapshot.Project.Get("Client"));
            string location = JoinLocation(
                snapshot.Project.Get("Town"),
                snapshot.Project.Get("Country"));

            MText title = Text(
                database,
                new Point3d(x + width * 0.08, top - 18.0, 0.0),
                page.Paper == "A4" ? 8.0 : 12.0,
                project.ToUpperInvariant() +
                    "\\P\\PCLIENT DESIGN SUMMARY BOOK",
                width * 0.84,
                4);
            AddGenerated(transaction, paperSpace, title, generated);

            MText details = Text(
                database,
                new Point3d(x + width * 0.08, top - (page.Paper == "A4" ? 62.0 : 82.0), 0.0),
                textHeight * 1.15,
                "CLIENT: " + client +
                    "\\PLOCATION: " + location +
                    "\\PPROJECT STAGE: " + stage.ToUpperInvariant() +
                    "\\PREVISION: " + revision +
                    "\\PISSUE DATE: " + DateTime.Now.ToString("dd MMMM yyyy", CultureInfo.CurrentCulture),
                width * 0.84,
                2);
            AddGenerated(transaction, paperSpace, details, generated);

            var readiness = new List<IList<string>>
            {
                new List<string> { "Current model report groups", snapshot.Groups.Count.ToString(CultureInfo.InvariantCulture) },
                new List<string> { "Linked BOQ tables", snapshot.LinkedBoqCount.ToString(CultureInfo.InvariantCulture) },
                new List<string> { "Linked dynamic sections", snapshot.Sections.Count.ToString(CultureInfo.InvariantCulture) },
                new List<string> { "Project layouts", snapshot.Layouts.Count.ToString(CultureInfo.InvariantCulture) },
                new List<string> { "Drawing units", ValueOrNotSet(snapshot.Project.Get("Units")) },
                new List<string> { "Coordinate system", ValueOrNotSet(snapshot.Project.Get("Coordinate System")) }
            };
            Table table = BuildTable(
                database,
                new Point3d(x + width * 0.08, top - (page.Paper == "A4" ? 96.0 : 125.0), 0.0),
                "PROJECT BOOK STATUS",
                new[] { "ITEM", "VALUE" },
                readiness,
                new[] { width * 0.58, width * 0.26 },
                textHeight,
                2.0);
            AddGenerated(transaction, paperSpace, table, generated);
            table.GenerateLayout();

            MText slogan = Text(
                database,
                new Point3d(x + width * 0.08, page.Paper == "A4" ? 47.0 : 63.0, 0.0),
                textHeight,
                "LESS CLICKING, MORE ENGINEERING.\\PGenerated from the current CE Tools project snapshot.",
                width * 0.84,
                3);
            AddGenerated(transaction, paperSpace, slogan, generated);
        }

        private static Table BuildProjectSummaryTable(
            Database database,
            Point3d position,
            ClientSnapshot snapshot,
            double textHeight,
            double width)
        {
            string[] fields =
            {
                "Project Name", "Client", "Town", "Country", "Coordinate System",
                "Standards", "Drawing Template", "Units"
            };
            var rows = new List<IList<string>>();
            foreach (string field in fields)
                rows.Add(new List<string> { field, ValueOrNotSet(snapshot.Project.Get(field)) });
            rows.Add(new List<string> { "Model report groups", snapshot.Groups.Count.ToString(CultureInfo.InvariantCulture) });
            rows.Add(new List<string> { "Linked BOQ tables", snapshot.LinkedBoqCount.ToString(CultureInfo.InvariantCulture) });
            rows.Add(new List<string> { "Linked dynamic sections", snapshot.Sections.Count.ToString(CultureInfo.InvariantCulture) });
            rows.Add(new List<string> { "Paper-space layouts", snapshot.Layouts.Count.ToString(CultureInfo.InvariantCulture) });
            return BuildTable(
                database,
                position,
                "PROJECT AND PRODUCTION SUMMARY",
                new[] { "PROPERTY", "CURRENT VALUE" },
                rows,
                new[] { width * 0.34, width * 0.66 },
                textHeight,
                2.0);
        }

        private static Table BuildDesignSummaryTable(
            Database database,
            Point3d position,
            ClientSnapshot snapshot,
            double textHeight,
            double width)
        {
            List<ClientDisciplineSummary> summaries = BuildDisciplineSummaries(snapshot);
            var rows = new List<IList<string>>();
            foreach (ClientDisciplineSummary summary in summaries)
            {
                rows.Add(new List<string>
                {
                    summary.Discipline,
                    summary.Count.ToString(CultureInfo.InvariantCulture),
                    FormatMeasure(summary.Length),
                    FormatMeasure(summary.Area),
                    FormatMeasure(summary.Volume)
                });
            }
            return BuildTable(
                database,
                position,
                "DESIGN DISCIPLINE SUMMARY",
                new[] { "DISCIPLINE", "OBJECTS", "LENGTH", "AREA", "VOLUME" },
                rows,
                new[] { width * 0.25, width * 0.12, width * 0.21, width * 0.21, width * 0.21 },
                textHeight,
                2.0);
        }

        private static Table BuildQuantitySummaryTable(
            Database database,
            Point3d position,
            ClientSnapshot snapshot,
            double textHeight,
            double width,
            string paper)
        {
            int limit = paper == "A4" ? 12 : 22;
            List<ClientReportGroup> groups = snapshot.Groups
                .OrderBy(item => item.Discipline)
                .ThenByDescending(item => item.Count)
                .ThenBy(item => item.Layer)
                .Take(limit)
                .ToList();
            var rows = new List<IList<string>>();
            foreach (ClientReportGroup group in groups)
            {
                rows.Add(new List<string>
                {
                    group.Discipline,
                    group.Layer,
                    group.TypeName,
                    group.Count.ToString(CultureInfo.InvariantCulture),
                    FormatMeasure(group.Length),
                    FormatMeasure(group.Area),
                    FormatMeasure(group.Volume)
                });
            }
            if (rows.Count == 0)
                rows.Add(new List<string> { "General", "", "No reportable model objects", "0", "", "", "" });
            return BuildTable(
                database,
                position,
                "CURRENT QUANTITY SUMMARY",
                new[] { "DISCIPLINE", "LAYER", "TYPE", "COUNT", "LENGTH", "AREA", "VOLUME" },
                rows,
                new[]
                {
                    width * 0.14, width * 0.20, width * 0.20, width * 0.08,
                    width * 0.13, width * 0.12, width * 0.13
                },
                textHeight * 0.82,
                1.85);
        }

        private static Table BuildDrawingRegisterTable(
            Database database,
            Point3d position,
            ClientSnapshot snapshot,
            double textHeight,
            double width,
            string paper)
        {
            int limit = paper == "A4" ? 14 : 26;
            List<ClientLayoutSnapshot> layouts = snapshot.Layouts
                .Where(item => !item.Name.StartsWith("CE-CLIENT-", StringComparison.OrdinalIgnoreCase))
                .OrderBy(item => item.TabOrder)
                .ThenBy(item => item.Name)
                .Take(limit)
                .ToList();
            var rows = new List<IList<string>>();
            for (int index = 0; index < layouts.Count; index++)
            {
                rows.Add(new List<string>
                {
                    (index + 1).ToString("D2", CultureInfo.InvariantCulture),
                    layouts[index].Name,
                    "Project drawing / layout",
                    "Available"
                });
            }
            if (rows.Count == 0)
                rows.Add(new List<string> { "01", "No project layouts detected", "", "Missing" });
            return BuildTable(
                database,
                position,
                "PROJECT DRAWING REGISTER",
                new[] { "NO.", "LAYOUT / DRAWING", "PURPOSE", "STATUS" },
                rows,
                new[] { width * 0.10, width * 0.45, width * 0.30, width * 0.15 },
                textHeight,
                1.9);
        }

        private static Table BuildSectionRegisterTable(
            Database database,
            Point3d position,
            ClientSnapshot snapshot,
            double textHeight,
            double width,
            string paper)
        {
            int limit = paper == "A4" ? 14 : 26;
            var rows = new List<IList<string>>();
            int index = 1;
            foreach (ClientSectionSnapshot section in snapshot.Sections.Take(limit))
            {
                rows.Add(new List<string>
                {
                    index++.ToString("D2", CultureInfo.InvariantCulture),
                    section.Handle,
                    section.Layer,
                    section.Name,
                    "Linked / refreshable"
                });
            }
            if (rows.Count == 0)
                rows.Add(new List<string> { "01", "", "", "No linked dynamic sections", "Not created" });
            return BuildTable(
                database,
                position,
                "DYNAMIC CROSS-SECTION REGISTER",
                new[] { "NO.", "SOURCE HANDLE", "LAYER", "NAME / TYPE", "STATUS" },
                rows,
                new[] { width * 0.08, width * 0.18, width * 0.25, width * 0.30, width * 0.19 },
                textHeight * 0.92,
                1.9);
        }

        private static Table BuildTypicalDetailsTable(
            Database database,
            Point3d position,
            ClientSnapshot snapshot,
            double textHeight,
            double width,
            string paper)
        {
            List<TypicalDetail> details = TypicalDetails();
            int limit = paper == "A4" ? 17 : details.Count;
            var rows = new List<IList<string>>();
            for (int index = 0; index < Math.Min(limit, details.Count); index++)
            {
                TypicalDetail detail = details[index];
                rows.Add(new List<string>
                {
                    (index + 1).ToString("D2", CultureInfo.InvariantCulture),
                    detail.Title,
                    detail.Discipline,
                    DetailStatus(snapshot, detail)
                });
            }
            return BuildTable(
                database,
                position,
                "TYPICAL DETAIL SCHEDULE",
                new[] { "NO.", "DETAIL", "DISCIPLINE", "BOOK STATUS" },
                rows,
                new[] { width * 0.08, width * 0.47, width * 0.25, width * 0.20 },
                textHeight * 0.88,
                1.8);
        }

        private static Table BuildTitleBlock(
            Database database,
            Point3d position,
            ClientPageDefinition page,
            string stage,
            string revision,
            ClientSnapshot snapshot,
            double textHeight)
        {
            var table = new Table();
            table.SetDatabaseDefaults(database);
            table.TableStyle = database.Tablestyle;
            table.Position = position;
            table.SetSize(4, 4);
            table.SetRowHeight(page.Paper == "A4" ? 6.2 : 7.3);
            double total = page.Width - (page.Paper == "A4" ? 16.0 : 20.0);
            table.Columns[0].Width = total * 0.18;
            table.Columns[1].Width = total * 0.47;
            table.Columns[2].Width = total * 0.15;
            table.Columns[3].Width = total * 0.20;
            table.MergeCells(CellRange.Create(table, 0, 0, 0, 3));
            table.Cells[0, 0].TextString =
                ValueOrNotSet(snapshot.Project.Get("Project Name")) +
                "  |  " + page.Title;
            table.Cells[0, 0].Alignment = CellAlignment.MiddleCenter;
            string[,] values =
            {
                { "CLIENT", ValueOrNotSet(snapshot.Project.Get("Client")), "SHEET", page.PageNumber },
                { "LOCATION", JoinLocation(snapshot.Project.Get("Town"), snapshot.Project.Get("Country")), "STAGE", stage },
                { "ISSUE DATE", DateTime.Now.ToString("dd MMM yyyy", CultureInfo.CurrentCulture), "REVISION", revision }
            };
            for (int row = 0; row < 3; row++)
            {
                for (int column = 0; column < 4; column++)
                    table.Cells[row + 1, column].TextString = values[row, column];
            }
            for (int row = 0; row < table.Rows.Count; row++)
            {
                for (int column = 0; column < table.Columns.Count; column++)
                {
                    table.Cells[row, column].TextHeight = textHeight * 0.78;
                    table.Cells[row, column].Alignment = column % 2 == 0
                        ? CellAlignment.MiddleCenter
                        : CellAlignment.MiddleLeft;
                }
            }
            table.ColorIndex = 8;
            return table;
        }

        private static Table BuildTable(
            Database database,
            Point3d position,
            string title,
            string[] headings,
            IList<IList<string>> rows,
            double[] widths,
            double textHeight,
            double rowFactor)
        {
            var table = new Table();
            table.SetDatabaseDefaults(database);
            table.TableStyle = database.Tablestyle;
            table.Position = position;
            table.SetSize(rows.Count + 2, headings.Length);
            table.SetRowHeight(textHeight * rowFactor);
            for (int column = 0; column < headings.Length; column++)
                table.Columns[column].Width = widths[column];
            table.MergeCells(CellRange.Create(table, 0, 0, 0, headings.Length - 1));
            table.Cells[0, 0].TextString = title;
            table.Cells[0, 0].Alignment = CellAlignment.MiddleCenter;
            for (int column = 0; column < headings.Length; column++)
            {
                table.Cells[1, column].TextString = headings[column];
                table.Cells[1, column].Alignment = CellAlignment.MiddleCenter;
            }
            for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
            {
                IList<string> row = rows[rowIndex];
                for (int column = 0; column < headings.Length; column++)
                {
                    table.Cells[rowIndex + 2, column].TextString =
                        column < row.Count ? row[column] ?? string.Empty : string.Empty;
                    table.Cells[rowIndex + 2, column].Alignment =
                        column == 0 ? CellAlignment.MiddleCenter : CellAlignment.MiddleLeft;
                }
            }
            for (int row = 0; row < table.Rows.Count; row++)
                for (int column = 0; column < table.Columns.Count; column++)
                    table.Cells[row, column].TextHeight = textHeight;
            table.ColorIndex = 8;
            return table;
        }

        private static MText Text(
            Database database,
            Point3d position,
            double height,
            string contents,
            double width,
            short colorIndex)
        {
            var text = new MText();
            text.SetDatabaseDefaults(database);
            text.Location = position;
            text.TextHeight = height;
            text.Contents = contents ?? string.Empty;
            text.Width = width;
            text.ColorIndex = colorIndex;
            return text;
        }

        private static Polyline Rectangle(
            Database database,
            double left,
            double bottom,
            double right,
            double top,
            short colorIndex)
        {
            var polyline = new Polyline();
            polyline.SetDatabaseDefaults(database);
            polyline.AddVertexAt(0, new Point2d(left, bottom), 0.0, 0.0, 0.0);
            polyline.AddVertexAt(1, new Point2d(right, bottom), 0.0, 0.0, 0.0);
            polyline.AddVertexAt(2, new Point2d(right, top), 0.0, 0.0, 0.0);
            polyline.AddVertexAt(3, new Point2d(left, top), 0.0, 0.0, 0.0);
            polyline.Closed = true;
            polyline.ColorIndex = colorIndex;
            return polyline;
        }

        private static void AddGenerated(
            Transaction transaction,
            BlockTableRecord space,
            Entity entity,
            ICollection<string> handles)
        {
            space.AppendEntity(entity);
            transaction.AddNewlyCreatedDBObject(entity, true);
            handles.Add(entity.Handle.ToString());
        }

        private static void EraseGenerated(
            Database database,
            Transaction transaction,
            IEnumerable<string> handles)
        {
            foreach (string handle in handles ?? Enumerable.Empty<string>())
            {
                ObjectId id;
                if (!TryResolveHandle(database, handle, out id)) continue;
                Entity entity = transaction.GetObject(
                    id,
                    OpenMode.ForWrite,
                    false) as Entity;
                if (entity != null && !entity.IsErased) entity.Erase();
            }
        }

        private static void WriteClientPageLink(
            Layout layout,
            Transaction transaction,
            ClientPageLink link)
        {
            if (layout.ExtensionDictionary.IsNull)
                layout.CreateExtensionDictionary();
            DBDictionary dictionary = transaction.GetObject(
                layout.ExtensionDictionary,
                OpenMode.ForWrite,
                false) as DBDictionary;
            Xrecord record = OpenOrCreateRecord(dictionary, LinkRecordName, transaction);
            record.Data = new ResultBuffer(
                new TypedValue((int)DxfCode.Text, "Schema=" + link.Schema),
                new TypedValue((int)DxfCode.Text, "Layout=" + link.LayoutName),
                new TypedValue((int)DxfCode.Text, "Paper=" + link.Paper),
                new TypedValue((int)DxfCode.Text, "PageKey=" + link.PageKey),
                new TypedValue((int)DxfCode.Text, "PageNumber=" + link.PageNumber),
                new TypedValue((int)DxfCode.Text, "Title=" + link.Title),
                new TypedValue((int)DxfCode.Text, "Stage=" + link.Stage),
                new TypedValue((int)DxfCode.Text, "Revision=" + link.Revision),
                new TypedValue((int)DxfCode.Text, "Width=" + link.Width.ToString("R", CultureInfo.InvariantCulture)),
                new TypedValue((int)DxfCode.Text, "Height=" + link.Height.ToString("R", CultureInfo.InvariantCulture)),
                new TypedValue((int)DxfCode.Text, "Generated=" + string.Join(";", link.GeneratedHandles)));
        }

        private static ClientPageLink ReadClientPageLinkIfPresent(
            Layout layout,
            Transaction transaction)
        {
            if (layout == null || layout.ExtensionDictionary.IsNull) return null;
            DBDictionary dictionary = transaction.GetObject(
                layout.ExtensionDictionary,
                OpenMode.ForRead,
                false) as DBDictionary;
            if (dictionary == null || !dictionary.Contains(LinkRecordName)) return null;
            Xrecord record = transaction.GetObject(
                dictionary.GetAt(LinkRecordName),
                OpenMode.ForRead,
                false) as Xrecord;
            if (record == null || record.Data == null) return null;

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (TypedValue item in record.Data)
            {
                string text = item.Value as string;
                if (string.IsNullOrWhiteSpace(text)) continue;
                int equals = text.IndexOf('=');
                if (equals <= 0) continue;
                values[text.Substring(0, equals)] = text.Substring(equals + 1);
            }

            string generatedText = Get(values, "Generated");
            IEnumerable<string> generated = string.IsNullOrWhiteSpace(generatedText)
                ? Enumerable.Empty<string>()
                : generatedText.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            double width;
            double height;
            double.TryParse(Get(values, "Width"), NumberStyles.Float, CultureInfo.InvariantCulture, out width);
            double.TryParse(Get(values, "Height"), NumberStyles.Float, CultureInfo.InvariantCulture, out height);
            return new ClientPageLink(
                Get(values, "Schema"),
                string.IsNullOrWhiteSpace(Get(values, "Layout")) ? layout.LayoutName : Get(values, "Layout"),
                Get(values, "Paper"),
                Get(values, "PageKey"),
                Get(values, "PageNumber"),
                Get(values, "Title"),
                Get(values, "Stage"),
                Get(values, "Revision"),
                width,
                height,
                generated);
        }

        private static List<ClientPageLink> ReadAllClientPageLinks(Database database)
        {
            var links = new List<ClientPageLink>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                DBDictionary layouts = transaction.GetObject(
                    database.LayoutDictionaryId,
                    OpenMode.ForRead,
                    false) as DBDictionary;
                if (layouts == null) return links;
                foreach (DBDictionaryEntry entry in layouts)
                {
                    Layout layout = transaction.GetObject(
                        entry.Value,
                        OpenMode.ForRead,
                        false) as Layout;
                    if (layout == null || layout.ModelType) continue;
                    ClientPageLink link = ReadClientPageLinkIfPresent(layout, transaction);
                    if (link != null) links.Add(link);
                }
            }
            return links;
        }

        private static ClientSnapshot BuildSnapshot(Database database)
        {
            var snapshot = new ClientSnapshot();
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
                var groups = new SortedDictionary<string, ClientReportGroup>(StringComparer.OrdinalIgnoreCase);
                if (modelSpace != null)
                {
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
                        if (entity == null) continue;

                        string layer = string.IsNullOrWhiteSpace(entity.Layer) ? "0" : entity.Layer;
                        string typeName = value.GetType().Name;
                        string objectName = ReadObjectName(value);
                        string discipline = ClassifyDiscipline(layer + " " + typeName + " " + objectName);
                        string key = discipline + "|" + layer + "|" + typeName;
                        ClientReportGroup group;
                        if (!groups.TryGetValue(key, out group))
                        {
                            group = new ClientReportGroup(discipline, layer, FriendlyTypeName(typeName));
                            groups.Add(key, group);
                        }
                        group.Count++;
                        double length;
                        if (TryGetLength(value, out length)) group.Length += length;
                        double area;
                        if (TryGetArea(entity, out area)) group.Area += area;
                        double volume;
                        if (TryGetVolume(value, out volume)) group.Volume += volume;

                        if (HasRecord(entity, transaction, "CE_BOQ_LINKS"))
                            snapshot.LinkedBoqCount++;
                        if (HasRecord(entity, transaction, DynamicCrossSectionCommands.LinkRecordName))
                        {
                            snapshot.Sections.Add(new ClientSectionSnapshot(
                                entity.Handle.ToString(),
                                layer,
                                string.IsNullOrWhiteSpace(objectName) ? FriendlyTypeName(typeName) : objectName));
                        }
                    }
                }
                snapshot.Groups = groups.Values.ToList();

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
                        snapshot.Layouts.Add(new ClientLayoutSnapshot(
                            layout.LayoutName,
                            layout.TabOrder));
                    }
                }
            }

            snapshot.Layouts = snapshot.Layouts
                .OrderBy(item => item.TabOrder)
                .ThenBy(item => item.Name)
                .ToList();
            snapshot.Sections = snapshot.Sections
                .OrderBy(item => item.Layer)
                .ThenBy(item => item.Handle)
                .ToList();
            return snapshot;
        }

        private static ClientProjectMetadata ReadProjectMetadata(Database database)
        {
            var metadata = new ClientProjectMetadata();
            try
            {
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    DBDictionary namedObjects = transaction.GetObject(
                        database.NamedObjectsDictionaryId,
                        OpenMode.ForRead,
                        false) as DBDictionary;
                    if (namedObjects == null || !namedObjects.Contains(ProjectRootDictionary))
                        return metadata;
                    DBDictionary root = transaction.GetObject(
                        namedObjects.GetAt(ProjectRootDictionary),
                        OpenMode.ForRead,
                        false) as DBDictionary;
                    if (root == null || !root.Contains(ProjectMetadataRecord))
                        return metadata;
                    Xrecord record = transaction.GetObject(
                        root.GetAt(ProjectMetadataRecord),
                        OpenMode.ForRead,
                        false) as Xrecord;
                    if (record == null || record.Data == null) return metadata;

                    string pending = null;
                    foreach (TypedValue item in record.Data)
                    {
                        string text = item.Value as string;
                        if (text == null) continue;
                        if (pending == null) pending = text;
                        else
                        {
                            if (!string.Equals(pending, "Schema", StringComparison.OrdinalIgnoreCase))
                                metadata.Set(pending, text);
                            pending = null;
                        }
                    }
                }
            }
            catch
            {
                return metadata;
            }
            return metadata;
        }

        private static List<ClientDisciplineSummary> BuildDisciplineSummaries(ClientSnapshot snapshot)
        {
            var map = new SortedDictionary<string, ClientDisciplineSummary>(StringComparer.OrdinalIgnoreCase);
            foreach (ClientReportGroup group in snapshot.Groups)
            {
                ClientDisciplineSummary summary;
                if (!map.TryGetValue(group.Discipline, out summary))
                {
                    summary = new ClientDisciplineSummary(group.Discipline);
                    map.Add(group.Discipline, summary);
                }
                summary.Count += group.Count;
                summary.Length += group.Length;
                summary.Area += group.Area;
                summary.Volume += group.Volume;
            }
            if (map.Count == 0)
                map.Add("General", new ClientDisciplineSummary("General"));
            return map.Values.ToList();
        }

        private static string ClassifyDiscipline(string text)
        {
            if (ContainsAny(text, "storm", "culvert", "catchpit", "headwall", "drain", "u-hi", "uhi"))
                return "Stormwater";
            if (ContainsAny(text, "sewer", "manhole", "inspection chamber", "wastewater"))
                return "Sewer";
            if (ContainsAny(text, "bulk water", "bulk-water", "reservoir", "water tank", "pump station"))
                return "Bulk Water";
            if (ContainsAny(text, "water", "valve", "hydrant", "pressure pipe"))
                return "Water";
            if (ContainsAny(text, "platform", "grading", "earthwork", "earthworks", "cut", "fill"))
                return "Platform";
            if (ContainsAny(
                text,
                "road", "parking", "kerb", "curb", "roundabout", "traffic island",
                "taxiway", "runway", "railway", "bridge", "pavement", "alignment",
                "profile", "corridor"))
                return "Road / Transport";
            return "General";
        }

        private static string ReadObjectName(DBObject value)
        {
            if (value == null) return string.Empty;
            try
            {
                PropertyInfo property = value.GetType().GetProperty(
                    "Name",
                    BindingFlags.Instance | BindingFlags.Public);
                object result = property == null ? null : property.GetValue(value, null);
                return result == null ? string.Empty : Convert.ToString(result, CultureInfo.InvariantCulture);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static bool TryGetLength(DBObject value, out double length)
        {
            length = 0.0;
            Curve curve = value as Curve;
            if (curve != null)
            {
                try
                {
                    length = Math.Abs(
                        curve.GetDistanceAtParameter(curve.EndParam) -
                        curve.GetDistanceAtParameter(curve.StartParam));
                    return IsFinitePositive(length);
                }
                catch
                {
                    length = 0.0;
                }
            }
            return TryReadDouble(value, out length, "Length3D", "Length2D", "Length");
        }

        private static bool TryGetArea(Entity entity, out double area)
        {
            area = 0.0;
            Hatch hatch = entity as Hatch;
            if (hatch != null)
            {
                try
                {
                    area = hatch.Area;
                    return IsFinitePositive(area);
                }
                catch
                {
                    area = 0.0;
                }
            }
            Region region = entity as Region;
            if (region != null)
            {
                try
                {
                    area = region.Area;
                    return IsFinitePositive(area);
                }
                catch
                {
                    area = 0.0;
                }
            }
            return TryReadDouble(entity, out area, "Area", "SurfaceArea");
        }

        private static bool TryGetVolume(DBObject value, out double volume)
        {
            return TryReadDouble(
                value,
                out volume,
                "Volume",
                "TotalVolume",
                "NetVolume",
                "CutVolume",
                "FillVolume");
        }

        private static bool TryReadDouble(
            object value,
            out double number,
            params string[] propertyNames)
        {
            number = 0.0;
            if (value == null) return false;
            foreach (string propertyName in propertyNames)
            {
                try
                {
                    PropertyInfo property = value.GetType().GetProperty(
                        propertyName,
                        BindingFlags.Instance | BindingFlags.Public);
                    if (property == null || !property.CanRead) continue;
                    object raw = property.GetValue(value, null);
                    if (raw == null) continue;
                    double candidate = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
                    if (!IsFinitePositive(candidate)) continue;
                    number = candidate;
                    return true;
                }
                catch
                {
                    // Continue to the next compatible property.
                }
            }
            return false;
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
                throw new InvalidOperationException("The client-book extension dictionary is unavailable.");
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

        private static List<ClientPageDefinition> PageDefinitions(ClientPaperSelection selection)
        {
            var pages = new List<ClientPageDefinition>();
            if (selection == ClientPaperSelection.A4 || selection == ClientPaperSelection.Both)
                pages.AddRange(PagesForPaper("A4", 297.0, 210.0));
            if (selection == ClientPaperSelection.A3 || selection == ClientPaperSelection.Both)
                pages.AddRange(PagesForPaper("A3", 420.0, 297.0));
            return pages;
        }

        private static List<ClientPageDefinition> PagesForPaper(
            string paper,
            double width,
            double height)
        {
            return new List<ClientPageDefinition>
            {
                Page(paper, "COVER", "00", "Cover and Issue Information", ClientPageKind.Cover, width, height),
                Page(paper, "PROJECT", "01", "Project Summary", ClientPageKind.ProjectSummary, width, height),
                Page(paper, "DESIGN", "02", "Design Discipline Summary", ClientPageKind.DesignSummary, width, height),
                Page(paper, "QUANTITIES", "03", "Quantity Summary", ClientPageKind.QuantitySummary, width, height),
                Page(paper, "DRAWINGS", "04", "Drawing Register", ClientPageKind.DrawingRegister, width, height),
                Page(paper, "SECTIONS", "05", "Cross-Section Register", ClientPageKind.SectionRegister, width, height),
                Page(paper, "DETAILS", "06", "Typical Detail Schedule", ClientPageKind.TypicalDetails, width, height)
            };
        }

        private static ClientPageDefinition Page(
            string paper,
            string key,
            string number,
            string title,
            ClientPageKind kind,
            double width,
            double height)
        {
            return new ClientPageDefinition(
                "CE-CLIENT-" + paper + "-" + number + "-" + key,
                paper,
                key,
                number,
                title,
                kind,
                width,
                height);
        }

        private static ClientPageDefinition FindPageDefinition(string paper, string key)
        {
            return PageDefinitions(ClientPaperSelection.Both).FirstOrDefault(
                item => Equal(item.Paper, paper) && Equal(item.PageKey, key));
        }

        private static List<TypicalDetail> TypicalDetails()
        {
            return new List<TypicalDetail>
            {
                new TypicalDetail("Railway Track Section", "Rail / Transport", "railway"),
                new TypicalDetail("Airport Runway Layout and Sections", "Airport / Transport", "runway"),
                new TypicalDetail("Airport Taxiway Layout and Sections", "Airport / Transport", "taxiway"),
                new TypicalDetail("Roundabout Layout and Sections", "Road", "roundabout"),
                new TypicalDetail("RCC Bridge Deck", "Bridge", "bridge"),
                new TypicalDetail("Bridge General Arrangement", "Bridge", "bridge"),
                new TypicalDetail("RCC Box Culvert Assembly", "Stormwater", "culvert"),
                new TypicalDetail("Valve Assembly Details", "Water", "water"),
                new TypicalDetail("Parking Plan and Bay Details", "Site / Road", "parking"),
                new TypicalDetail("Traffic Island Detail", "Road", "traffic island"),
                new TypicalDetail("Kerb Stone Detail", "Road", "kerb"),
                new TypicalDetail("Side Drain Detail", "Stormwater", "side drain"),
                new TypicalDetail("Headwall and Wingwall Detail", "Stormwater", "headwall"),
                new TypicalDetail("Stormwater Drain / UHI Detail", "Stormwater", "storm"),
                new TypicalDetail("Manhole Detail", "Sewer", "sewer"),
                new TypicalDetail("Inspection Chamber Detail", "Sewer", "sewer"),
                new TypicalDetail("Underground Water Tank Detail", "Water / Bulk Water", "water tank")
            };
        }

        private static string DetailStatus(ClientSnapshot snapshot, TypicalDetail detail)
        {
            bool relevant = snapshot.Groups.Any(
                group => ContainsAny(
                    group.Discipline + " " + group.Layer + " " + group.TypeName,
                    detail.Keyword));
            return relevant ? "Suggested - insert approved DWG" : "Library reference";
        }

        private static string PageNote(ClientPageKind kind)
        {
            if (kind == ClientPageKind.QuantitySummary)
                return "Quantities are an inventory summary, not a certified payment BOQ. Refresh the linked BOQ before issue.";
            if (kind == ClientPageKind.TypicalDetails)
                return "Use only office-approved, engineer-reviewed DWG detail blocks. Reference images and example dimensions are not design authority.";
            if (kind == ClientPageKind.SectionRegister)
                return "Linked sections are current at the last CE Tools refresh. Confirm section labels, levels and scales before issue.";
            return "This sheet is generated from the current CE Tools project snapshot. Run CE_CLIENTBOOKREFRESH before every client issue.";
        }

        private static bool PromptPaperSelection(
            Editor editor,
            out ClientPaperSelection selection)
        {
            var options = new PromptKeywordOptions(
                "\nClient book paper [A4/A3/Both] <Both>: ")
            {
                AllowNone = true
            };
            options.Keywords.Add("A4");
            options.Keywords.Add("A3");
            options.Keywords.Add("Both");
            PromptResult result = editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel)
            {
                selection = ClientPaperSelection.Both;
                return false;
            }
            string value = result.Status == PromptStatus.None ? "Both" : result.StringResult;
            return Enum.TryParse(value, true, out selection);
        }

        private static bool PromptStage(Editor editor, out string stage)
        {
            var options = new PromptKeywordOptions(
                "\nProject issue stage [Concept/Preliminary/Tender/Construction/AsBuilt] <Preliminary>: ")
            {
                AllowNone = true
            };
            foreach (string keyword in new[]
            {
                "Concept", "Preliminary", "Tender", "Construction", "AsBuilt"
            })
                options.Keywords.Add(keyword);
            PromptResult result = editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel)
            {
                stage = string.Empty;
                return false;
            }
            stage = result.Status == PromptStatus.None ? "Preliminary" : result.StringResult;
            if (Equal(stage, "AsBuilt")) stage = "As Built";
            return true;
        }

        private static bool PromptRevision(Editor editor, out string revision)
        {
            var options = new PromptStringOptions(
                "\nClient-book revision <0>: ")
            {
                AllowSpaces = true,
                UseDefaultValue = true,
                DefaultValue = "0"
            };
            PromptResult result = editor.GetString(options);
            if (result.Status != PromptStatus.OK)
            {
                revision = string.Empty;
                return false;
            }
            revision = string.IsNullOrWhiteSpace(result.StringResult)
                ? "0"
                : result.StringResult.Trim();
            return true;
        }

        private static bool PromptExcelPath(
            Editor editor,
            string defaultName,
            out string path)
        {
            var options = new PromptSaveFileOptions(
                "\nSelect client-book index workbook output path: ")
            {
                Filter = "Excel Workbook (*.xlsx)|*.xlsx",
                DialogCaption = "Export CE Tools Client Book Index",
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

        private static void WriteSnapshotPreview(Editor editor, ClientSnapshot snapshot)
        {
            editor.WriteMessage(
                "\nCE Tools client-book snapshot. Project={0}; report groups={1}; linked BOQs={2}; linked sections={3}; layouts={4}; rejected={5}.",
                ValueOrNotSet(snapshot.Project.Get("Project Name")),
                snapshot.Groups.Count,
                snapshot.LinkedBoqCount,
                snapshot.Sections.Count,
                snapshot.Layouts.Count,
                snapshot.Rejected);
        }

        private static string JoinLocation(string town, string country)
        {
            var values = new List<string>();
            if (!string.IsNullOrWhiteSpace(town)) values.Add(town.Trim());
            if (!string.IsNullOrWhiteSpace(country)) values.Add(country.Trim());
            return values.Count == 0 ? "<Not set>" : string.Join(", ", values);
        }

        private static string FormatMeasure(double value)
        {
            return IsFinitePositive(value)
                ? value.ToString("N3", CultureInfo.CurrentCulture)
                : string.Empty;
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

        private static string ValueOrNotSet(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "<Not set>" : value;
        }

        private static string Get(IDictionary<string, string> values, string key)
        {
            string value;
            return values.TryGetValue(key, out value) ? value : string.Empty;
        }

        private static bool ContainsAny(string source, params string[] values)
        {
            if (string.IsNullOrWhiteSpace(source)) return false;
            foreach (string value in values)
            {
                if (!string.IsNullOrWhiteSpace(value) &&
                    source.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0)
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

        private enum ClientPaperSelection
        {
            A4,
            A3,
            Both
        }

        private enum ClientPageKind
        {
            Cover,
            ProjectSummary,
            DesignSummary,
            QuantitySummary,
            DrawingRegister,
            SectionRegister,
            TypicalDetails
        }

        private sealed class ClientPageDefinition
        {
            public ClientPageDefinition(
                string layoutName,
                string paper,
                string pageKey,
                string pageNumber,
                string title,
                ClientPageKind kind,
                double width,
                double height)
            {
                LayoutName = layoutName;
                Paper = paper;
                PageKey = pageKey;
                PageNumber = pageNumber;
                Title = title;
                Kind = kind;
                Width = width;
                Height = height;
            }

            public string LayoutName { get; }
            public string Paper { get; }
            public string PageKey { get; }
            public string PageNumber { get; }
            public string Title { get; }
            public ClientPageKind Kind { get; }
            public double Width { get; }
            public double Height { get; }
        }

        private sealed class ClientPageLink
        {
            public ClientPageLink(
                string schema,
                string layoutName,
                string paper,
                string pageKey,
                string pageNumber,
                string title,
                string stage,
                string revision,
                double width,
                double height,
                IEnumerable<string> generatedHandles)
            {
                Schema = schema;
                LayoutName = layoutName;
                Paper = paper;
                PageKey = pageKey;
                PageNumber = pageNumber;
                Title = title;
                Stage = stage;
                Revision = revision;
                Width = width;
                Height = height;
                GeneratedHandles = generatedHandles == null
                    ? new List<string>()
                    : generatedHandles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }

            public string Schema { get; }
            public string LayoutName { get; }
            public string Paper { get; }
            public string PageKey { get; }
            public string PageNumber { get; }
            public string Title { get; }
            public string Stage { get; }
            public string Revision { get; }
            public double Width { get; }
            public double Height { get; }
            public List<string> GeneratedHandles { get; }
        }

        private sealed class ClientSnapshot
        {
            public ClientSnapshot()
            {
                Project = new ClientProjectMetadata();
                Groups = new List<ClientReportGroup>();
                Sections = new List<ClientSectionSnapshot>();
                Layouts = new List<ClientLayoutSnapshot>();
            }

            public ClientProjectMetadata Project { get; set; }
            public List<ClientReportGroup> Groups { get; set; }
            public List<ClientSectionSnapshot> Sections { get; set; }
            public List<ClientLayoutSnapshot> Layouts { get; set; }
            public int LinkedBoqCount { get; set; }
            public int Rejected { get; set; }
        }

        private sealed class ClientProjectMetadata
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

        private sealed class ClientReportGroup
        {
            public ClientReportGroup(string discipline, string layer, string typeName)
            {
                Discipline = discipline;
                Layer = layer;
                TypeName = typeName;
            }

            public string Discipline { get; }
            public string Layer { get; }
            public string TypeName { get; }
            public int Count { get; set; }
            public double Length { get; set; }
            public double Area { get; set; }
            public double Volume { get; set; }
        }

        private sealed class ClientDisciplineSummary
        {
            public ClientDisciplineSummary(string discipline)
            {
                Discipline = discipline;
            }

            public string Discipline { get; }
            public int Count { get; set; }
            public double Length { get; set; }
            public double Area { get; set; }
            public double Volume { get; set; }
        }

        private sealed class ClientSectionSnapshot
        {
            public ClientSectionSnapshot(string handle, string layer, string name)
            {
                Handle = handle;
                Layer = layer;
                Name = name;
            }

            public string Handle { get; }
            public string Layer { get; }
            public string Name { get; }
        }

        private sealed class ClientLayoutSnapshot
        {
            public ClientLayoutSnapshot(string name, int tabOrder)
            {
                Name = name;
                TabOrder = tabOrder;
            }

            public string Name { get; }
            public int TabOrder { get; }
        }

        private sealed class TypicalDetail
        {
            public TypicalDetail(string title, string discipline, string keyword)
            {
                Title = title;
                Discipline = discipline;
                Keyword = keyword;
            }

            public string Title { get; }
            public string Discipline { get; }
            public string Keyword { get; }
        }
    }
}

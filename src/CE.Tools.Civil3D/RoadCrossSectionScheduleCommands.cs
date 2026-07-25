using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

[assembly: CommandClass(typeof(CETools.Civil3D.RoadCrossSectionScheduleCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Linked road cross-section setting-out schedules at a configurable interval.
    /// Each station produces left-edge, centreline and right-edge rows with X, Y,
    /// ground elevation, design elevation and elevation difference.
    /// </summary>
    public sealed class RoadCrossSectionScheduleCommands
    {
        private const string LinkRecordName = "CE_ROAD_SECTION_SCHEDULE";
        private const string SchemaVersion = "1";

        [CommandMethod("CE_TOOLS", "CE_ROADSECTIONDATATOOLS", CommandFlags.Modal)]
        public void RoadSectionDataTools()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            var options = new PromptKeywordOptions(
                "\nRoad section data [Create/Refresh/Export/Info] <Create>: ")
            {
                AllowNone = true
            };
            options.Keywords.Add("Create");
            options.Keywords.Add("Refresh");
            options.Keywords.Add("Export");
            options.Keywords.Add("Info");
            PromptResult result = document.Editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel) return;
            string choice = result.Status == PromptStatus.OK
                ? result.StringResult
                : "Create";
            string command;
            if (string.Equals(choice, "Refresh", StringComparison.OrdinalIgnoreCase))
                command = "CE_ROADSECTIONDATAREFRESH ";
            else if (string.Equals(choice, "Export", StringComparison.OrdinalIgnoreCase))
                command = "CE_ROADSECTIONDATAEXPORT ";
            else if (string.Equals(choice, "Info", StringComparison.OrdinalIgnoreCase))
                command = "CE_ROADSECTIONDATAINFO ";
            else
                command = "CE_ROADSECTIONDATA ";
            document.SendStringToExecute(command, true, false, true);
        }

        [CommandMethod("CE_TOOLS", "CE_ROADSECTIONDATA", CommandFlags.Modal | CommandFlags.Redraw)]
        public void CreateRoadSectionData()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            Editor editor = document.Editor;

            PromptEntityResult alignmentResult = PromptAlignment(
                editor,
                "\nSelect the road alignment for cross-section setting-out data: ");
            if (alignmentResult.Status != PromptStatus.OK) return;

            List<RoadSectionSurfaceChoice> surfaces = ReadSurfaceChoices(document);
            var window = new RoadSectionConfigurationWindow(surfaces);
            AcApplication.ShowModalWindow(window);
            if (!window.Accepted)
            {
                editor.WriteMessage("\nCE_ROADSECTIONDATA cancelled.");
                return;
            }

            PromptPointResult insertion = editor.GetPoint(
                "\nPick insertion point for the linked road cross-section data table: ");
            if (insertion.Status != PromptStatus.OK) return;
            AnnotationOptions annotation;
            if (!AnnotationSettingsStore.Prepare(document, false, out annotation))
                return;

            var link = new RoadSectionLink(
                alignmentResult.ObjectId.Handle.ToString(),
                window.GroundChoice,
                window.DesignChoice,
                window.Interval,
                window.LeftOffset,
                window.RightOffset);
            int failed;
            List<RoadSectionRow> rows;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                rows = ReadRows(document.Database, transaction, link, out failed);
            }
            if (rows.Count == 0)
            {
                editor.WriteMessage(
                    "\nCE_ROADSECTIONDATA stopped. No usable alignment stations were produced.");
                return;
            }

            var review = new List<KeyValuePair<string, string>>
            {
                Pair("Alignment", link.AlignmentHandle),
                Pair("Interval", link.Interval.ToString("N3", CultureInfo.CurrentCulture)),
                Pair("Left offset", link.LeftOffset.ToString("N3", CultureInfo.CurrentCulture)),
                Pair("Right offset", link.RightOffset.ToString("N3", CultureInfo.CurrentCulture)),
                Pair("Ground surface", link.Ground.DisplayName),
                Pair("Design surface", link.Design.DisplayName),
                Pair("Output rows", rows.Count.ToString(CultureInfo.InvariantCulture)),
                Pair("Failed samples", failed.ToString(CultureInfo.InvariantCulture)),
                Pair("Linked refresh", "Yes")
            };
            if (!PopupTablePresenter.ShowReview(
                    "CE Tools - Road Cross-Section Data",
                    "The schedule samples left edge, centreline and right edge at every interval and stores alignment/surface handles for refresh.",
                    review,
                    "Create Schedule"))
            {
                editor.WriteMessage("\nCE_ROADSECTIONDATA cancelled.");
                return;
            }

            ObjectId tableId = CreateLinkedTable(
                document.Database,
                insertion.Value,
                rows,
                link,
                annotation.TextHeight);
            editor.SetImpliedSelection(new[] { tableId });
            editor.Regen();
            editor.WriteMessage(
                "\nCE_ROADSECTIONDATA complete. Rows={0}; failed samples={1}.",
                rows.Count,
                failed);
            if (PromptYesNo(editor, "Export this road cross-section schedule to Excel now", false))
                ExportTable(document, tableId);
        }

        [CommandMethod("CE_TOOLS", "CE_ROADSECTIONDATAREFRESH", CommandFlags.Modal | CommandFlags.Redraw)]
        public void RefreshRoadSectionData()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            PromptEntityResult result = PromptTable(
                document.Editor,
                "\nSelect a linked CE road cross-section data table: ");
            if (result.Status != PromptStatus.OK) return;
            try
            {
                int rows;
                int failed;
                RefreshTable(document.Database, result.ObjectId, out rows, out failed);
                document.Editor.Regen();
                document.Editor.WriteMessage(
                    "\nCE_ROADSECTIONDATAREFRESH complete. Rows={0}; failed samples={1}.",
                    rows,
                    failed);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_ROADSECTIONDATAREFRESH stopped. {0}",
                    exception.Message);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_ROADSECTIONDATAEXPORT", CommandFlags.Modal)]
        public void ExportRoadSectionData()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            PromptEntityResult result = PromptTable(
                document.Editor,
                "\nSelect a linked CE road cross-section data table to export: ");
            if (result.Status != PromptStatus.OK) return;
            ExportTable(document, result.ObjectId);
        }

        [CommandMethod("CE_TOOLS", "CE_ROADSECTIONDATAINFO", CommandFlags.Modal | CommandFlags.Redraw)]
        public void RoadSectionDataInformation()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            PromptEntityResult result = PromptTable(
                document.Editor,
                "\nSelect a linked CE road cross-section data table for information: ");
            if (result.Status != PromptStatus.OK) return;
            try
            {
                RoadSectionLink link;
                int existingRows;
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    Table table = transaction.GetObject(
                        result.ObjectId,
                        OpenMode.ForRead,
                        false) as Table;
                    link = ReadLink(table, transaction);
                    existingRows = table == null ? 0 : Math.Max(0, table.Rows.Count - 2);
                }
                int failed;
                int currentRows;
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    currentRows = ReadRows(document.Database, transaction, link, out failed).Count;
                }
                var rows = new List<KeyValuePair<string, string>>
                {
                    Pair("Alignment handle", link.AlignmentHandle),
                    Pair("Interval", link.Interval.ToString("N3", CultureInfo.CurrentCulture)),
                    Pair("Left / right offsets", link.LeftOffset.ToString("N3", CultureInfo.CurrentCulture) + " / " + link.RightOffset.ToString("N3", CultureInfo.CurrentCulture)),
                    Pair("Ground surface", link.Ground.DisplayName),
                    Pair("Design surface", link.Design.DisplayName),
                    Pair("Existing table rows", existingRows.ToString(CultureInfo.InvariantCulture)),
                    Pair("Current calculated rows", currentRows.ToString(CultureInfo.InvariantCulture)),
                    Pair("Failed samples", failed.ToString(CultureInfo.InvariantCulture))
                };
                PopupTablePresenter.ShowReportAndOfferTable(
                    document,
                    "CE Tools - Road Cross-Section Data Information",
                    "The selected schedule is linked to its road alignment and selected Civil 3D surfaces.",
                    rows,
                    "CE TOOLS ROAD CROSS-SECTION DATA INFORMATION");
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_ROADSECTIONDATAINFO stopped. {0}",
                    exception.Message);
            }
        }

        internal static int RefreshAll(Document document)
        {
            if (document == null) return 0;
            int refreshed = 0;
            foreach (ObjectId tableId in FindLinkedTables(document.Database))
            {
                try
                {
                    int rows;
                    int failed;
                    RefreshTable(document.Database, tableId, out rows, out failed);
                    refreshed++;
                }
                catch
                {
                    // Continue refreshing independent schedules.
                }
            }
            return refreshed;
        }

        private static List<RoadSectionRow> ReadRows(
            Database database,
            Transaction transaction,
            RoadSectionLink link,
            out int failed)
        {
            failed = 0;
            ObjectId alignmentId;
            if (!TryResolveHandle(database, link.AlignmentHandle, out alignmentId))
                throw new InvalidOperationException("The linked road alignment is missing.");
            CivilAlignment alignment = transaction.GetObject(
                alignmentId,
                OpenMode.ForRead,
                false) as CivilAlignment;
            if (alignment == null)
                throw new InvalidOperationException("The linked object is not a Civil 3D alignment.");
            CivilSurface ground = OpenSurface(database, transaction, link.Ground);
            CivilSurface design = OpenSurface(database, transaction, link.Design);

            double start = alignment.StartingStation;
            double end = alignment.EndingStation;
            if (end < start)
            {
                double swap = start;
                start = end;
                end = swap;
            }
            var stations = BuildStations(start, end, link.Interval);
            var rows = new List<RoadSectionRow>();
            foreach (double station in stations)
            {
                AddRow(alignment, ground, design, station, -Math.Abs(link.LeftOffset), "LEFT EDGE", rows, ref failed);
                AddRow(alignment, ground, design, station, 0.0, "ROAD CENTERLINE", rows, ref failed);
                AddRow(alignment, ground, design, station, Math.Abs(link.RightOffset), "RIGHT EDGE", rows, ref failed);
            }
            return rows;
        }

        private static void AddRow(
            CivilAlignment alignment,
            CivilSurface ground,
            CivilSurface design,
            double station,
            double offset,
            string position,
            ICollection<RoadSectionRow> rows,
            ref int failed)
        {
            double x = 0.0;
            double y = 0.0;
            try
            {
                alignment.PointLocation(station, offset, ref x, ref y);
            }
            catch
            {
                failed++;
                return;
            }
            double? groundElevation = Sample(ground, x, y);
            double? designElevation = Sample(design, x, y);
            if ((ground != null && !groundElevation.HasValue) ||
                (design != null && !designElevation.HasValue))
                failed++;
            double? difference = groundElevation.HasValue && designElevation.HasValue
                ? designElevation.Value - groundElevation.Value
                : (double?)null;
            rows.Add(new RoadSectionRow(
                station,
                position,
                offset,
                x,
                y,
                groundElevation,
                designElevation,
                difference));
        }

        private static List<double> BuildStations(double start, double end, double interval)
        {
            var result = new List<double>();
            double safeInterval = Math.Max(interval, 0.001);
            result.Add(start);
            double next = Math.Ceiling(start / safeInterval) * safeInterval;
            if (Math.Abs(next - start) < 0.000001) next += safeInterval;
            for (double station = next; station < end - 0.000001; station += safeInterval)
                result.Add(station);
            if (end > start + 0.000001) result.Add(end);
            return result;
        }

        private static double? Sample(CivilSurface surface, double x, double y)
        {
            if (surface == null) return null;
            try
            {
                return surface.FindElevationAtXY(x, y);
            }
            catch
            {
                return null;
            }
        }

        private static ObjectId CreateLinkedTable(
            Database database,
            Autodesk.AutoCAD.Geometry.Point3d insertion,
            IList<RoadSectionRow> rows,
            RoadSectionLink link,
            double textHeight)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTableRecord currentSpace = transaction.GetObject(
                    database.CurrentSpaceId,
                    OpenMode.ForWrite,
                    false) as BlockTableRecord;
                if (currentSpace == null)
                    throw new InvalidOperationException("The current drawing space could not be opened.");
                var table = new Table();
                table.SetDatabaseDefaults(database);
                table.TableStyle = database.Tablestyle;
                table.Position = insertion;
                ObjectId id = currentSpace.AppendEntity(table);
                transaction.AddNewlyCreatedDBObject(table, true);
                WriteLink(table, transaction, link);
                PopulateTable(table, rows, textHeight);
                transaction.Commit();
                return id;
            }
        }

        private static void RefreshTable(
            Database database,
            ObjectId tableId,
            out int rowCount,
            out int failed)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                Table table = transaction.GetObject(
                    tableId,
                    OpenMode.ForWrite,
                    false) as Table;
                if (table == null)
                    throw new InvalidOperationException("The selected object is not an AutoCAD table.");
                RoadSectionLink link = ReadLink(table, transaction);
                List<RoadSectionRow> rows = ReadRows(database, transaction, link, out failed);
                if (rows.Count == 0)
                    throw new InvalidOperationException("The linked alignment produced no current cross-section rows.");
                PopulateTable(table, rows, database.Textsize);
                rowCount = rows.Count;
                transaction.Commit();
            }
        }

        private static void PopulateTable(
            Table table,
            IList<RoadSectionRow> rows,
            double textHeight)
        {
            if (rows == null || rows.Count == 0)
                throw new InvalidOperationException("A road cross-section data table cannot contain zero rows.");
            const int columns = 9;
            double height = NormalizeHeight(textHeight);
            table.SetSize(rows.Count + 2, columns);
            table.SetRowHeight(Math.Max(height * 1.8, 0.001));
            table.SetColumnWidth(Math.Max(height * 7.0, 0.001));
            table.Cells[0, 0].TextString = "CE TOOLS ROAD CROSS-SECTION SETTING-OUT DATA";
            table.MergeCells(CellRange.Create(table, 0, 0, 0, columns - 1));
            string[] headings =
            {
                "CHAINAGE",
                "POSITION",
                "OFFSET",
                "X COORDINATE",
                "Y COORDINATE",
                "GROUND ELEVATION",
                "DESIGN ELEVATION",
                "DIFFERENCE",
                "STATUS"
            };
            for (int column = 0; column < columns; column++)
            {
                table.Cells[1, column].TextString = headings[column];
                table.Cells[1, column].TextHeight = height;
            }
            for (int index = 0; index < rows.Count; index++)
            {
                RoadSectionRow row = rows[index];
                int tableRow = index + 2;
                table.Cells[tableRow, 0].TextString = FormatStation(row.Station);
                table.Cells[tableRow, 1].TextString = row.Position;
                table.Cells[tableRow, 2].TextString = row.Offset.ToString("N3", CultureInfo.CurrentCulture);
                table.Cells[tableRow, 3].TextString = row.X.ToString("N3", CultureInfo.CurrentCulture);
                table.Cells[tableRow, 4].TextString = row.Y.ToString("N3", CultureInfo.CurrentCulture);
                table.Cells[tableRow, 5].TextString = FormatNullable(row.GroundElevation);
                table.Cells[tableRow, 6].TextString = FormatNullable(row.DesignElevation);
                table.Cells[tableRow, 7].TextString = FormatNullable(row.Difference);
                table.Cells[tableRow, 8].TextString = row.GroundElevation.HasValue && row.DesignElevation.HasValue
                    ? "Complete"
                    : "Outside/unavailable surface";
                for (int column = 0; column < columns; column++)
                    table.Cells[tableRow, column].TextHeight = height;
            }
            table.GenerateLayout();
        }

        private static void WriteLink(Table table, Transaction transaction, RoadSectionLink link)
        {
            if (table.ExtensionDictionary.IsNull)
                table.CreateExtensionDictionary();
            DBDictionary dictionary = transaction.GetObject(
                table.ExtensionDictionary,
                OpenMode.ForWrite,
                false) as DBDictionary;
            if (dictionary == null)
                throw new InvalidOperationException("The table extension dictionary could not be opened.");
            Xrecord record;
            if (dictionary.Contains(LinkRecordName))
            {
                record = transaction.GetObject(
                    dictionary.GetAt(LinkRecordName),
                    OpenMode.ForWrite,
                    false) as Xrecord;
            }
            else
            {
                record = new Xrecord();
                dictionary.SetAt(LinkRecordName, record);
                transaction.AddNewlyCreatedDBObject(record, true);
            }
            record.Data = new ResultBuffer(
                new TypedValue((int)DxfCode.Text, "Schema=" + SchemaVersion),
                new TypedValue((int)DxfCode.Text, "Alignment=" + link.AlignmentHandle),
                new TypedValue((int)DxfCode.Text, "Ground=" + link.Ground.Serialize()),
                new TypedValue((int)DxfCode.Text, "Design=" + link.Design.Serialize()),
                new TypedValue((int)DxfCode.Text, "Interval=" + link.Interval.ToString("R", CultureInfo.InvariantCulture)),
                new TypedValue((int)DxfCode.Text, "Left=" + link.LeftOffset.ToString("R", CultureInfo.InvariantCulture)),
                new TypedValue((int)DxfCode.Text, "Right=" + link.RightOffset.ToString("R", CultureInfo.InvariantCulture)));
        }

        private static RoadSectionLink ReadLink(Table table, Transaction transaction)
        {
            if (table == null || table.ExtensionDictionary.IsNull)
                throw new InvalidOperationException("The selected table is not a linked CE road cross-section schedule.");
            DBDictionary dictionary = transaction.GetObject(
                table.ExtensionDictionary,
                OpenMode.ForRead,
                false) as DBDictionary;
            if (dictionary == null || !dictionary.Contains(LinkRecordName))
                throw new InvalidOperationException("The selected table has no CE road-section link record.");
            Xrecord record = transaction.GetObject(
                dictionary.GetAt(LinkRecordName),
                OpenMode.ForRead,
                false) as Xrecord;
            if (record == null || record.Data == null)
                throw new InvalidOperationException("The CE road-section link record is empty.");

            string alignment = string.Empty;
            RoadSectionSurfaceChoice ground = RoadSectionSurfaceChoice.Blank();
            RoadSectionSurfaceChoice design = RoadSectionSurfaceChoice.Blank();
            double interval = 10.0;
            double left = 3.5;
            double right = 3.5;
            foreach (TypedValue value in record.Data)
            {
                string text = value.Value as string;
                if (string.IsNullOrWhiteSpace(text)) continue;
                if (text.StartsWith("Alignment=", StringComparison.OrdinalIgnoreCase))
                    alignment = text.Substring("Alignment=".Length);
                else if (text.StartsWith("Ground=", StringComparison.OrdinalIgnoreCase))
                    ground = RoadSectionSurfaceChoice.Parse(text.Substring("Ground=".Length));
                else if (text.StartsWith("Design=", StringComparison.OrdinalIgnoreCase))
                    design = RoadSectionSurfaceChoice.Parse(text.Substring("Design=".Length));
                else if (text.StartsWith("Interval=", StringComparison.OrdinalIgnoreCase))
                    double.TryParse(text.Substring("Interval=".Length), NumberStyles.Float, CultureInfo.InvariantCulture, out interval);
                else if (text.StartsWith("Left=", StringComparison.OrdinalIgnoreCase))
                    double.TryParse(text.Substring("Left=".Length), NumberStyles.Float, CultureInfo.InvariantCulture, out left);
                else if (text.StartsWith("Right=", StringComparison.OrdinalIgnoreCase))
                    double.TryParse(text.Substring("Right=".Length), NumberStyles.Float, CultureInfo.InvariantCulture, out right);
            }
            if (string.IsNullOrWhiteSpace(alignment))
                throw new InvalidOperationException("The linked road alignment handle is missing.");
            return new RoadSectionLink(alignment, ground, design, interval, left, right);
        }

        private static void ExportTable(Document document, ObjectId tableId)
        {
            Editor editor = document.Editor;
            try
            {
                int rows;
                int failed;
                RefreshTable(document.Database, tableId, out rows, out failed);
                IList<IList<string>> cells;
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    Table table = transaction.GetObject(tableId, OpenMode.ForRead, false) as Table;
                    cells = ReadCells(table);
                }
                var options = new PromptSaveFileOptions(
                    "\nSelect road cross-section data Excel workbook path: ")
                {
                    Filter = "Excel Workbook (*.xlsx)|*.xlsx",
                    DialogCaption = "Export CE Tools Road Cross-Section Data",
                    InitialFileName = "CE-Road-Cross-Section-Data.xlsx"
                };
                PromptFileNameResult result = editor.GetFileNameForSave(options);
                if (result.Status != PromptStatus.OK) return;
                string path = result.StringResult;
                if (!path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
                    path += ".xlsx";
                SimpleXlsxWriter.Write(path, "Road Section Data", cells);
                editor.WriteMessage(
                    "\nCE_ROADSECTIONDATAEXPORT complete. Rows={0}; failed samples={1}; file={2}",
                    rows,
                    failed,
                    path);
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_ROADSECTIONDATAEXPORT stopped. {0}",
                    exception.Message);
            }
        }

        private static IList<IList<string>> ReadCells(Table table)
        {
            var rows = new List<IList<string>>();
            if (table == null) return rows;
            for (int row = 0; row < table.Rows.Count; row++)
            {
                var values = new List<string>();
                for (int column = 0; column < table.Columns.Count; column++)
                    values.Add(table.Cells[row, column].TextString ?? string.Empty);
                rows.Add(values);
            }
            return rows;
        }

        private static List<RoadSectionSurfaceChoice> ReadSurfaceChoices(Document document)
        {
            var choices = new List<RoadSectionSurfaceChoice> { RoadSectionSurfaceChoice.Blank() };
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (civilDocument == null) return choices;
            try
            {
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId id in civilDocument.GetSurfaceIds())
                    {
                        CivilSurface surface = transaction.GetObject(id, OpenMode.ForRead, false) as CivilSurface;
                        if (surface != null)
                            choices.Add(RoadSectionSurfaceChoice.Surface(surface.Name, id.Handle.ToString()));
                    }
                }
            }
            catch
            {
                // Blank remains available.
            }
            return choices;
        }

        private static CivilSurface OpenSurface(
            Database database,
            Transaction transaction,
            RoadSectionSurfaceChoice choice)
        {
            if (choice == null || string.IsNullOrWhiteSpace(choice.HandleText)) return null;
            ObjectId id;
            if (!TryResolveHandle(database, choice.HandleText, out id)) return null;
            return transaction.GetObject(id, OpenMode.ForRead, false) as CivilSurface;
        }

        private static List<ObjectId> FindLinkedTables(Database database)
        {
            var result = new List<ObjectId>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTableRecord currentSpace = transaction.GetObject(
                    database.CurrentSpaceId,
                    OpenMode.ForRead,
                    false) as BlockTableRecord;
                if (currentSpace == null) return result;
                foreach (ObjectId id in currentSpace)
                {
                    Table table = transaction.GetObject(id, OpenMode.ForRead, false) as Table;
                    if (table == null || table.ExtensionDictionary.IsNull) continue;
                    DBDictionary dictionary = transaction.GetObject(
                        table.ExtensionDictionary,
                        OpenMode.ForRead,
                        false) as DBDictionary;
                    if (dictionary != null && dictionary.Contains(LinkRecordName)) result.Add(id);
                }
            }
            return result;
        }

        private static PromptEntityResult PromptAlignment(Editor editor, string message)
        {
            var options = new PromptEntityOptions(message);
            options.SetRejectMessage("\nSelect a Civil 3D alignment.");
            options.AddAllowedClass(typeof(CivilAlignment), false);
            return editor.GetEntity(options);
        }

        private static PromptEntityResult PromptTable(Editor editor, string message)
        {
            var options = new PromptEntityOptions(message);
            options.SetRejectMessage("\nSelect an AutoCAD table.");
            options.AddAllowedClass(typeof(Table), false);
            return editor.GetEntity(options);
        }

        private static bool TryResolveHandle(Database database, string text, out ObjectId id)
        {
            id = ObjectId.Null;
            long value;
            if (!long.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)) return false;
            try
            {
                id = database.GetObjectId(false, new Handle(value), 0);
                return !id.IsNull && !id.IsErased;
            }
            catch
            {
                return false;
            }
        }

        private static bool PromptYesNo(Editor editor, string message, bool defaultValue)
        {
            var options = new PromptKeywordOptions(
                "\n" + message + " [Yes/No] <" + (defaultValue ? "Yes" : "No") + ">: ")
            {
                AllowNone = true
            };
            options.Keywords.Add("Yes");
            options.Keywords.Add("No");
            PromptResult result = editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel) return false;
            return result.Status == PromptStatus.None
                ? defaultValue
                : string.Equals(result.StringResult, "Yes", StringComparison.OrdinalIgnoreCase);
        }

        private static double NormalizeHeight(double value)
        {
            if (Math.Abs(value - 1.8) < 0.05) return 1.8;
            if (Math.Abs(value - 5.0) < 0.05) return 5.0;
            return 2.0;
        }

        private static string FormatStation(double station)
        {
            int kilometres = (int)Math.Floor(station / 1000.0);
            double remainder = station - (kilometres * 1000.0);
            return kilometres.ToString(CultureInfo.InvariantCulture) + "+" +
                remainder.ToString("000.000", CultureInfo.CurrentCulture);
        }

        private static string FormatNullable(double? value)
        {
            return value.HasValue ? value.Value.ToString("N3", CultureInfo.CurrentCulture) : string.Empty;
        }

        private static KeyValuePair<string, string> Pair(string key, string value)
        {
            return new KeyValuePair<string, string>(key, value);
        }

        private static Document ActiveDocument()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
        }
    }

    internal sealed class RoadSectionLink
    {
        public RoadSectionLink(
            string alignmentHandle,
            RoadSectionSurfaceChoice ground,
            RoadSectionSurfaceChoice design,
            double interval,
            double leftOffset,
            double rightOffset)
        {
            AlignmentHandle = alignmentHandle;
            Ground = ground ?? RoadSectionSurfaceChoice.Blank();
            Design = design ?? RoadSectionSurfaceChoice.Blank();
            Interval = Math.Max(interval, 0.001);
            LeftOffset = Math.Abs(leftOffset);
            RightOffset = Math.Abs(rightOffset);
        }

        public string AlignmentHandle { get; private set; }
        public RoadSectionSurfaceChoice Ground { get; private set; }
        public RoadSectionSurfaceChoice Design { get; private set; }
        public double Interval { get; private set; }
        public double LeftOffset { get; private set; }
        public double RightOffset { get; private set; }
    }

    internal sealed class RoadSectionSurfaceChoice
    {
        private RoadSectionSurfaceChoice(string displayName, string handleText)
        {
            DisplayName = displayName;
            HandleText = handleText ?? string.Empty;
        }

        public string DisplayName { get; private set; }
        public string HandleText { get; private set; }

        public static RoadSectionSurfaceChoice Blank()
        {
            return new RoadSectionSurfaceChoice("<Leave blank>", string.Empty);
        }

        public static RoadSectionSurfaceChoice Surface(string name, string handle)
        {
            return new RoadSectionSurfaceChoice(name, handle);
        }

        public string Serialize()
        {
            return HandleText + "|" + DisplayName;
        }

        public static RoadSectionSurfaceChoice Parse(string value)
        {
            string[] parts = (value ?? string.Empty).Split(new[] { '|' }, 2);
            if (parts.Length == 0 || string.IsNullOrWhiteSpace(parts[0])) return Blank();
            return Surface(parts.Length > 1 ? parts[1] : "<Surface>", parts[0]);
        }

        public override string ToString()
        {
            return DisplayName;
        }
    }

    internal sealed class RoadSectionRow
    {
        public RoadSectionRow(
            double station,
            string position,
            double offset,
            double x,
            double y,
            double? groundElevation,
            double? designElevation,
            double? difference)
        {
            Station = station;
            Position = position;
            Offset = offset;
            X = x;
            Y = y;
            GroundElevation = groundElevation;
            DesignElevation = designElevation;
            Difference = difference;
        }

        public double Station { get; private set; }
        public string Position { get; private set; }
        public double Offset { get; private set; }
        public double X { get; private set; }
        public double Y { get; private set; }
        public double? GroundElevation { get; private set; }
        public double? DesignElevation { get; private set; }
        public double? Difference { get; private set; }
    }

    internal sealed class RoadSectionConfigurationWindow : Window
    {
        private readonly ComboBox _interval;
        private readonly TextBox _left;
        private readonly TextBox _right;
        private readonly ComboBox _ground;
        private readonly ComboBox _design;

        public RoadSectionConfigurationWindow(IList<RoadSectionSurfaceChoice> surfaces)
        {
            Accepted = false;
            Title = "CE Tools - Road Cross-Section Data";
            Width = 680;
            Height = 520;
            MinWidth = 560;
            MinHeight = 420;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.CanResize;

            var root = new DockPanel { Margin = new Thickness(16) };
            Content = root;
            var heading = new TextBlock
            {
                Text = "Road cross-section setting-out schedule",
                FontSize = 20,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 6)
            };
            DockPanel.SetDock(heading, Dock.Top);
            root.Children.Add(heading);
            var note = new TextBlock
            {
                Text = "Choose 5 m, 10 m or 20 m intervals, left/right offsets and ground/design surfaces. The schedule includes left edge, road centreline and right edge at every station.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            };
            DockPanel.SetDock(note, Dock.Top);
            root.Children.Add(note);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            var accept = new Button
            {
                Content = "Continue",
                MinWidth = 100,
                Padding = new Thickness(10, 6, 10, 6)
            };
            accept.Click += delegate
            {
                double left;
                double right;
                if (!TryPositive(_left.Text, out left) || !TryPositive(_right.Text, out right))
                {
                    MessageBox.Show(this, "Left and right offsets must be positive numbers.", "CE Tools", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                Accepted = true;
                DialogResult = true;
            };
            var cancel = new Button
            {
                Content = "Cancel",
                MinWidth = 90,
                Margin = new Thickness(6, 0, 0, 0),
                Padding = new Thickness(10, 6, 10, 6)
            };
            cancel.Click += delegate
            {
                Accepted = false;
                DialogResult = false;
            };
            buttons.Children.Add(accept);
            buttons.Children.Add(cancel);
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(buttons);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int index = 0; index < 5; index++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _interval = new ComboBox
            {
                ItemsSource = new[] { 5.0, 10.0, 20.0 },
                SelectedIndex = 1,
                Margin = new Thickness(0, 4, 0, 4)
            };
            _left = new TextBox { Text = "3.5", Margin = new Thickness(0, 4, 0, 4) };
            _right = new TextBox { Text = "3.5", Margin = new Thickness(0, 4, 0, 4) };
            _ground = new ComboBox
            {
                ItemsSource = surfaces,
                SelectedIndex = surfaces != null && surfaces.Count > 1 ? 1 : 0,
                IsTextSearchEnabled = true,
                Margin = new Thickness(0, 4, 0, 4)
            };
            _design = new ComboBox
            {
                ItemsSource = surfaces,
                SelectedIndex = surfaces != null && surfaces.Count > 2 ? 2 : (surfaces != null && surfaces.Count > 1 ? 1 : 0),
                IsTextSearchEnabled = true,
                Margin = new Thickness(0, 4, 0, 4)
            };
            AddRow(grid, 0, "Station interval", _interval);
            AddRow(grid, 1, "Left edge offset", _left);
            AddRow(grid, 2, "Right edge offset", _right);
            AddRow(grid, 3, "Ground surface", _ground);
            AddRow(grid, 4, "Design surface", _design);
            root.Children.Add(grid);
        }

        public bool Accepted { get; private set; }
        public double Interval { get { return (double)_interval.SelectedItem; } }
        public double LeftOffset { get { double value; TryPositive(_left.Text, out value); return value; } }
        public double RightOffset { get { double value; TryPositive(_right.Text, out value); return value; } }
        public RoadSectionSurfaceChoice GroundChoice { get { return _ground.SelectedItem as RoadSectionSurfaceChoice; } }
        public RoadSectionSurfaceChoice DesignChoice { get { return _design.SelectedItem as RoadSectionSurfaceChoice; } }

        private static bool TryPositive(string text, out double value)
        {
            return (double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out value) ||
                    double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) &&
                   value > 0.0;
        }

        private static void AddRow(Grid grid, int row, string label, Control control)
        {
            var text = new TextBlock
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 4, 10, 4)
            };
            Grid.SetRow(text, row);
            grid.Children.Add(text);
            Grid.SetRow(control, row);
            Grid.SetColumn(control, 1);
            grid.Children.Add(control);
        }
    }
}

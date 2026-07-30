using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilCogoPoint = Autodesk.Civil.DatabaseServices.CogoPoint;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

[assembly: CommandClass(typeof(CETools.Civil3D.SettingOutScheduleCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Linked platform/road/junction setting-out schedules. Point coordinates are
    /// read from COGO or AutoCAD points and optional ground/design elevations are
    /// sampled from selected Civil 3D surfaces. Tables store source handles and can
    /// be refreshed or exported to Excel without COM automation.
    /// </summary>
    public sealed class SettingOutScheduleCommands
    {
        private const string LinkRecordName = "CE_SETTING_OUT_LINKS";
        private const string SchemaVersion = "1";

        [CommandMethod("CE_TOOLS", "CE_SETTINGOUTTOOLS", CommandFlags.Modal)]
        public void SettingOutTools()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            var options = new PromptKeywordOptions(
                "\nSetting-out tools [Create/Refresh/Export/Info] <Create>: ")
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
                command = "CE_SETTINGOUTREFRESH ";
            else if (string.Equals(choice, "Export", StringComparison.OrdinalIgnoreCase))
                command = "CE_SETTINGOUTEXPORT ";
            else if (string.Equals(choice, "Info", StringComparison.OrdinalIgnoreCase))
                command = "CE_SETTINGOUTINFO ";
            else
                command = "CE_SETTINGOUTPOINTS ";
            document.SendStringToExecute(command, true, false, true);
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_SETTINGOUTPOINTS",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void CreateSettingOutSchedule()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            Editor editor = document.Editor;

            PromptSelectionResult selection = GetSelection(
                editor,
                "\nSelect Civil 3D COGO points and/or AutoCAD points for the setting-out schedule: ");
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
                    DBObject value = transaction.GetObject(
                        selected.ObjectId,
                        OpenMode.ForRead,
                        false);
                    if (value is CivilCogoPoint || value is DBPoint)
                        sourceIds.Add(selected.ObjectId);
                    else
                        rejected++;
                }
            }
            if (sourceIds.Count == 0)
            {
                editor.WriteMessage(
                    "\nCE_SETTINGOUTPOINTS cancelled. No supported COGO or AutoCAD points were selected.");
                return;
            }

            List<SettingOutSurfaceChoice> surfaces = ReadSurfaceChoices(document);
            var window = new SettingOutConfigurationWindow(surfaces);
            AcApplication.ShowModalWindow(window);
            if (!window.Accepted)
            {
                editor.WriteMessage("\nCE_SETTINGOUTPOINTS cancelled.");
                return;
            }

            PromptPointResult insertion = editor.GetPoint(
                "\nPick insertion point for the linked setting-out table: ");
            if (insertion.Status != PromptStatus.OK) return;

            AnnotationOptions annotation;
            if (!AnnotationSettingsStore.Prepare(document, false, out annotation))
                return;

            var link = new SettingOutLink(
                window.ScheduleType,
                window.GroundChoice,
                window.DesignChoice,
                sourceIds.Select(id => id.Handle.ToString()));
            List<SettingOutRow> rows;
            int missing;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                rows = ReadRows(document.Database, transaction, link, out missing);
            }
            if (rows.Count == 0)
            {
                editor.WriteMessage(
                    "\nCE_SETTINGOUTPOINTS cancelled. No usable setting-out rows were produced.");
                return;
            }

            var review = new List<KeyValuePair<string, string>>
            {
                Pair("Schedule type", link.ScheduleType),
                Pair("Accepted points", sourceIds.Count.ToString(CultureInfo.InvariantCulture)),
                Pair("Rejected selections", rejected.ToString(CultureInfo.InvariantCulture)),
                Pair("Ground elevation source", link.Ground.DisplayName),
                Pair("Design elevation source", link.Design.DisplayName),
                Pair("Table columns", "Description, X, Y, Ground, Design, Difference"),
                Pair("Linked refresh", "Yes")
            };
            if (!PopupTablePresenter.ShowReview(
                    "CE Tools - Setting-Out Schedule",
                    "The table stores source point and surface handles so it can be refreshed when points or surfaces change.",
                    review,
                    "Create Schedule"))
            {
                editor.WriteMessage("\nCE_SETTINGOUTPOINTS cancelled.");
                return;
            }

            try
            {
                ObjectId tableId = CreateLinkedTable(
                    document.Database,
                    insertion.Value,
                    rows,
                    link,
                    annotation.TextHeight);
                editor.SetImpliedSelection(new[] { tableId });
                editor.Regen();
                editor.WriteMessage(
                    "\nCE_SETTINGOUTPOINTS complete. Rows={0}; missing sources={1}; linked table created.",
                    rows.Count,
                    missing);

                if (PromptYesNo(editor, "Export this setting-out schedule to Excel now", false))
                    ExportTable(document, tableId);
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_SETTINGOUTPOINTS cancelled. No table was committed. {0}",
                    exception.Message);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_SETTINGOUTREFRESH", CommandFlags.Modal | CommandFlags.Redraw)]
        public void RefreshSettingOutSchedule()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            PromptEntityResult result = PromptForLinkedTable(
                document.Editor,
                "\nSelect a linked CE setting-out table to refresh: ");
            if (result.Status != PromptStatus.OK) return;

            int rows;
            int missing;
            try
            {
                RefreshTable(document.Database, result.ObjectId, out rows, out missing);
                document.Editor.Regen();
                document.Editor.WriteMessage(
                    "\nCE_SETTINGOUTREFRESH complete. Rows={0}; missing/unavailable sources={1}.",
                    rows,
                    missing);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_SETTINGOUTREFRESH stopped. {0}",
                    exception.Message);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_SETTINGOUTEXPORT", CommandFlags.Modal)]
        public void ExportSettingOutSchedule()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            PromptEntityResult result = PromptForLinkedTable(
                document.Editor,
                "\nSelect a linked CE setting-out table to export: ");
            if (result.Status != PromptStatus.OK) return;
            ExportTable(document, result.ObjectId);
        }

        [CommandMethod("CE_TOOLS", "CE_SETTINGOUTINFO", CommandFlags.Modal | CommandFlags.Redraw)]
        public void SettingOutInformation()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            PromptEntityResult result = PromptForLinkedTable(
                document.Editor,
                "\nSelect a linked CE setting-out table for information: ");
            if (result.Status != PromptStatus.OK) return;

            try
            {
                SettingOutLink link;
                int existingRows;
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    Table table = transaction.GetObject(
                        result.ObjectId,
                        OpenMode.ForRead,
                        false) as Table;
                    link = ReadLink(document.Database, table, transaction);
                    existingRows = table == null ? 0 : Math.Max(0, table.Rows.Count - 2);
                }

                int missing;
                List<SettingOutRow> current;
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    current = ReadRows(document.Database, transaction, link, out missing);
                }
                var rows = new List<KeyValuePair<string, string>>
                {
                    Pair("Schedule type", link.ScheduleType),
                    Pair("Linked point handles", link.SourceHandles.Count.ToString(CultureInfo.InvariantCulture)),
                    Pair("Existing table rows", existingRows.ToString(CultureInfo.InvariantCulture)),
                    Pair("Current usable rows", current.Count.ToString(CultureInfo.InvariantCulture)),
                    Pair("Missing/unavailable sources", missing.ToString(CultureInfo.InvariantCulture)),
                    Pair("Ground elevation source", link.Ground.DisplayName),
                    Pair("Design elevation source", link.Design.DisplayName)
                };
                PopupTablePresenter.ShowReportAndOfferTable(
                    document,
                    "CE Tools - Setting-Out Information",
                    "The selected table is linked to point and surface handles in the current DWG.",
                    rows,
                    "CE TOOLS SETTING-OUT INFORMATION");
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_SETTINGOUTINFO stopped. {0}",
                    exception.Message);
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
                    int rows;
                    int missing;
                    RefreshTable(document.Database, tableId, out rows, out missing);
                    refreshed++;
                }
                catch
                {
                    // One stale table must not block other linked schedules.
                }
            }
            return refreshed;
        }

        private static ObjectId CreateLinkedTable(
            Database database,
            Point3d insertionPoint,
            IList<SettingOutRow> rows,
            SettingOutLink link,
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
                table.Position = insertionPoint;
                ObjectId tableId = currentSpace.AppendEntity(table);
                transaction.AddNewlyCreatedDBObject(table, true);
                WriteLink(table, transaction, link);
                PopulateTable(table, rows, textHeight, link.ScheduleType);
                transaction.Commit();
                return tableId;
            }
        }

        private static void RefreshTable(
            Database database,
            ObjectId tableId,
            out int rowCount,
            out int missing)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                Table table = transaction.GetObject(
                    tableId,
                    OpenMode.ForWrite,
                    false) as Table;
                if (table == null)
                    throw new InvalidOperationException("The selected object is not an AutoCAD table.");
                SettingOutLink link = ReadLink(database, table, transaction);
                List<SettingOutRow> rows = ReadRows(database, transaction, link, out missing);
                if (rows.Count == 0)
                    throw new InvalidOperationException("The linked schedule has no usable current point sources.");
                PopulateTable(table, rows, database.Textsize, link.ScheduleType);
                rowCount = rows.Count;
                transaction.Commit();
            }
        }

        private static void ExportTable(Document document, ObjectId tableId)
        {
            Editor editor = document.Editor;
            try
            {
                int rows;
                int missing;
                RefreshTable(document.Database, tableId, out rows, out missing);
                IList<IList<string>> cells;
                string type;
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    Table table = transaction.GetObject(
                        tableId,
                        OpenMode.ForRead,
                        false) as Table;
                    SettingOutLink link = ReadLink(document.Database, table, transaction);
                    type = link.ScheduleType;
                    cells = ReadTableCells(table);
                }

                var saveOptions = new PromptSaveFileOptions(
                    "\nSelect setting-out Excel workbook path: ")
                {
                    Filter = "Excel Workbook (*.xlsx)|*.xlsx",
                    DialogCaption = "Export CE Tools Setting-Out Schedule",
                    InitialFileName = "CE-" + SanitizeFileName(type) + "-Setting-Out.xlsx"
                };
                PromptFileNameResult result = editor.GetFileNameForSave(saveOptions);
                if (result.Status != PromptStatus.OK) return;
                string path = result.StringResult;
                if (!path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
                    path += ".xlsx";
                SimpleXlsxWriter.Write(path, "Setting Out", cells);
                editor.WriteMessage(
                    "\nCE_SETTINGOUTEXPORT complete. Rows={0}; missing sources={1}; file={2}",
                    rows,
                    missing,
                    path);
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_SETTINGOUTEXPORT stopped. {0}",
                    exception.Message);
            }
        }

        private static List<SettingOutRow> ReadRows(
            Database database,
            Transaction transaction,
            SettingOutLink link,
            out int missing)
        {
            var rows = new List<SettingOutRow>();
            missing = 0;
            CivilSurface ground = OpenSurface(database, transaction, link.Ground);
            CivilSurface design = OpenSurface(database, transaction, link.Design);
            int fallback = 1;
            foreach (string handleText in link.SourceHandles)
            {
                ObjectId id;
                if (!TryResolveHandle(database, handleText, out id))
                {
                    missing++;
                    continue;
                }
                DBObject value;
                try
                {
                    value = transaction.GetObject(id, OpenMode.ForRead, false);
                }
                catch
                {
                    missing++;
                    continue;
                }

                string description;
                Point3d point;
                CivilCogoPoint cogo = value as CivilCogoPoint;
                if (cogo != null)
                {
                    description = string.IsNullOrWhiteSpace(cogo.PointName)
                        ? cogo.RawDescription
                        : cogo.PointName;
                    if (string.IsNullOrWhiteSpace(description))
                        description = "P" + cogo.PointNumber.ToString(CultureInfo.InvariantCulture);
                    point = new Point3d(cogo.Easting, cogo.Northing, cogo.Elevation);
                }
                else
                {
                    DBPoint dbPoint = value as DBPoint;
                    if (dbPoint == null)
                    {
                        missing++;
                        continue;
                    }
                    description = "P" + fallback.ToString("D3", CultureInfo.InvariantCulture);
                    point = dbPoint.Position;
                    fallback++;
                }

                double? groundElevation = ResolveElevation(link.Ground, ground, point);
                double? designElevation = ResolveElevation(link.Design, design, point);
                double? difference = groundElevation.HasValue && designElevation.HasValue
                    ? designElevation.Value - groundElevation.Value
                    : (double?)null;
                rows.Add(new SettingOutRow(
                    description,
                    point.X,
                    point.Y,
                    groundElevation,
                    designElevation,
                    difference));
            }
            return rows.OrderBy(item => item.Description, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        private static CivilSurface OpenSurface(
            Database database,
            Transaction transaction,
            SettingOutSurfaceChoice choice)
        {
            if (choice == null || choice.Mode != SettingOutElevationMode.Surface)
                return null;
            ObjectId id;
            if (!TryResolveHandle(database, choice.HandleText, out id))
                return null;
            return transaction.GetObject(id, OpenMode.ForRead, false) as CivilSurface;
        }

        private static double? ResolveElevation(
            SettingOutSurfaceChoice choice,
            CivilSurface surface,
            Point3d point)
        {
            if (choice == null || choice.Mode == SettingOutElevationMode.Blank)
                return null;
            if (choice.Mode == SettingOutElevationMode.Point)
                return point.Z;
            if (surface == null) return null;
            try
            {
                return surface.FindElevationAtXY(point.X, point.Y);
            }
            catch
            {
                return null;
            }
        }

        private static void PopulateTable(
            Table table,
            IList<SettingOutRow> rows,
            double textHeight,
            string scheduleType)
        {
            if (rows == null || rows.Count == 0)
                throw new InvalidOperationException("A setting-out table cannot be populated with zero rows.");
            const int columns = 6;
            double height = NormalizeHeight(textHeight);
            table.SetSize(rows.Count + 2, columns);
            table.SetRowHeight(Math.Max(height * 1.8, 0.001));
            table.SetColumnWidth(Math.Max(height * 8.0, 0.001));
            table.Cells[0, 0].TextString = "CE TOOLS " + scheduleType.ToUpperInvariant() + " SETTING-OUT SCHEDULE";
            table.MergeCells(CellRange.Create(table, 0, 0, 0, columns - 1));
            string[] headings =
            {
                "POINT DESCRIPTION",
                "X COORDINATE",
                "Y COORDINATE",
                "GROUND ELEVATION",
                "DESIGN ELEVATION",
                "DIFFERENCE"
            };
            for (int column = 0; column < columns; column++)
            {
                table.Cells[1, column].TextString = headings[column];
                table.Cells[1, column].TextHeight = height;
            }
            for (int index = 0; index < rows.Count; index++)
            {
                SettingOutRow row = rows[index];
                int tableRow = index + 2;
                table.Cells[tableRow, 0].TextString = row.Description;
                table.Cells[tableRow, 1].TextString = row.X.ToString("N3", CultureInfo.CurrentCulture);
                table.Cells[tableRow, 2].TextString = row.Y.ToString("N3", CultureInfo.CurrentCulture);
                table.Cells[tableRow, 3].TextString = FormatNullable(row.GroundElevation);
                table.Cells[tableRow, 4].TextString = FormatNullable(row.DesignElevation);
                table.Cells[tableRow, 5].TextString = FormatNullable(row.Difference);
                for (int column = 0; column < columns; column++)
                    table.Cells[tableRow, column].TextHeight = height;
            }
            table.GenerateLayout();
        }

        private static void WriteLink(
            Table table,
            Transaction transaction,
            SettingOutLink link)
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
            var values = new List<TypedValue>
            {
                new TypedValue((int)DxfCode.Text, "Schema=" + SchemaVersion),
                new TypedValue((int)DxfCode.Text, "Type=" + link.ScheduleType),
                new TypedValue((int)DxfCode.Text, "Ground=" + link.Ground.Serialize()),
                new TypedValue((int)DxfCode.Text, "Design=" + link.Design.Serialize())
            };
            foreach (string handle in link.SourceHandles)
                values.Add(new TypedValue((int)DxfCode.Text, "Handle=" + handle));
            record.Data = new ResultBuffer(values.ToArray());
        }

        private static SettingOutLink ReadLink(
            Database database,
            Table table,
            Transaction transaction)
        {
            if (table == null || table.ExtensionDictionary.IsNull)
                throw new InvalidOperationException("The selected table is not a linked CE setting-out schedule.");
            DBDictionary dictionary = transaction.GetObject(
                table.ExtensionDictionary,
                OpenMode.ForRead,
                false) as DBDictionary;
            if (dictionary == null || !dictionary.Contains(LinkRecordName))
                throw new InvalidOperationException("The selected table has no CE setting-out link record.");
            Xrecord record = transaction.GetObject(
                dictionary.GetAt(LinkRecordName),
                OpenMode.ForRead,
                false) as Xrecord;
            if (record == null || record.Data == null)
                throw new InvalidOperationException("The CE setting-out link record is empty.");

            string type = "General";
            SettingOutSurfaceChoice ground = SettingOutSurfaceChoice.PointElevation();
            SettingOutSurfaceChoice design = SettingOutSurfaceChoice.PointElevation();
            var handles = new List<string>();
            foreach (TypedValue value in record.Data)
            {
                string text = value.Value as string;
                if (string.IsNullOrWhiteSpace(text)) continue;
                if (text.StartsWith("Type=", StringComparison.OrdinalIgnoreCase))
                    type = text.Substring("Type=".Length);
                else if (text.StartsWith("Ground=", StringComparison.OrdinalIgnoreCase))
                    ground = SettingOutSurfaceChoice.Parse(text.Substring("Ground=".Length));
                else if (text.StartsWith("Design=", StringComparison.OrdinalIgnoreCase))
                    design = SettingOutSurfaceChoice.Parse(text.Substring("Design=".Length));
                else if (text.StartsWith("Handle=", StringComparison.OrdinalIgnoreCase))
                    handles.Add(text.Substring("Handle=".Length));
            }
            if (handles.Count == 0)
                throw new InvalidOperationException("The linked setting-out schedule contains no point handles.");
            return new SettingOutLink(type, ground, design, handles);
        }

        private static List<SettingOutSurfaceChoice> ReadSurfaceChoices(Document document)
        {
            var choices = new List<SettingOutSurfaceChoice>
            {
                SettingOutSurfaceChoice.PointElevation(),
                SettingOutSurfaceChoice.Blank()
            };
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (civilDocument == null) return choices;
            try
            {
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId surfaceId in civilDocument.GetSurfaceIds())
                    {
                        CivilSurface surface = transaction.GetObject(
                            surfaceId,
                            OpenMode.ForRead,
                            false) as CivilSurface;
                        if (surface == null) continue;
                        choices.Add(SettingOutSurfaceChoice.Surface(
                            surface.Name,
                            surfaceId.Handle.ToString()));
                    }
                }
            }
            catch
            {
                // Keep point-elevation and blank options when the Civil API cannot enumerate surfaces.
            }
            return choices;
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
                foreach (ObjectId objectId in currentSpace)
                {
                    Table table = transaction.GetObject(
                        objectId,
                        OpenMode.ForRead,
                        false) as Table;
                    if (table == null || table.ExtensionDictionary.IsNull) continue;
                    DBDictionary dictionary = transaction.GetObject(
                        table.ExtensionDictionary,
                        OpenMode.ForRead,
                        false) as DBDictionary;
                    if (dictionary != null && dictionary.Contains(LinkRecordName))
                        result.Add(objectId);
                }
            }
            return result;
        }

        private static IList<IList<string>> ReadTableCells(Table table)
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

        private static PromptEntityResult PromptForLinkedTable(Editor editor, string message)
        {
            var options = new PromptEntityOptions(message);
            options.SetRejectMessage("\nSelect an AutoCAD table.");
            options.AddAllowedClass(typeof(Table), false);
            return editor.GetEntity(options);
        }

        private static PromptSelectionResult GetSelection(Editor editor, string message)
        {
            PromptSelectionResult implied = editor.SelectImplied();
            if (implied.Status == PromptStatus.OK && implied.Value.Count > 0)
            {
                editor.SetImpliedSelection(new ObjectId[0]);
                return implied;
            }
            return editor.GetSelection(new PromptSelectionOptions
            {
                MessageForAdding = message,
                AllowDuplicates = false,
                RejectObjectsFromNonCurrentSpace = true
            });
        }

        private static bool PromptYesNo(Editor editor, string message, bool defaultValue)
        {
            var options = new PromptKeywordOptions(
                "\n" + message + " [Yes/No] <" +
                (defaultValue ? "Yes" : "No") + ">: ")
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

        private static bool TryResolveHandle(Database database, string handleText, out ObjectId objectId)
        {
            objectId = ObjectId.Null;
            long value;
            if (!long.TryParse(
                    handleText,
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out value))
                return false;
            try
            {
                objectId = database.GetObjectId(false, new Handle(value), 0);
                return !objectId.IsNull && !objectId.IsErased;
            }
            catch
            {
                return false;
            }
        }

        private static double NormalizeHeight(double value)
        {
            if (Math.Abs(value - 1.8) < 0.05) return 1.8;
            if (Math.Abs(value - 5.0) < 0.05) return 5.0;
            return 2.0;
        }

        private static string FormatNullable(double? value)
        {
            return value.HasValue
                ? value.Value.ToString("N3", CultureInfo.CurrentCulture)
                : string.Empty;
        }

        private static string SanitizeFileName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "General";
            foreach (char invalid in System.IO.Path.GetInvalidFileNameChars())
                value = value.Replace(invalid, '-');
            return value.Replace(' ', '-');
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

    internal enum SettingOutElevationMode
    {
        Point,
        Blank,
        Surface
    }

    internal sealed class SettingOutSurfaceChoice
    {
        private SettingOutSurfaceChoice(
            SettingOutElevationMode mode,
            string displayName,
            string handleText)
        {
            Mode = mode;
            DisplayName = displayName;
            HandleText = handleText ?? string.Empty;
        }

        public SettingOutElevationMode Mode { get; private set; }
        public string DisplayName { get; private set; }
        public string HandleText { get; private set; }

        public static SettingOutSurfaceChoice PointElevation()
        {
            return new SettingOutSurfaceChoice(
                SettingOutElevationMode.Point,
                "<Use point elevation>",
                string.Empty);
        }

        public static SettingOutSurfaceChoice Blank()
        {
            return new SettingOutSurfaceChoice(
                SettingOutElevationMode.Blank,
                "<Leave blank>",
                string.Empty);
        }

        public static SettingOutSurfaceChoice Surface(string name, string handle)
        {
            return new SettingOutSurfaceChoice(
                SettingOutElevationMode.Surface,
                name,
                handle);
        }

        public string Serialize()
        {
            return Mode + "|" + HandleText + "|" + DisplayName;
        }

        public static SettingOutSurfaceChoice Parse(string value)
        {
            string[] parts = (value ?? string.Empty).Split(new[] { '|' }, 3);
            SettingOutElevationMode mode;
            if (parts.Length == 0 || !Enum.TryParse(parts[0], true, out mode))
                return PointElevation();
            string handle = parts.Length > 1 ? parts[1] : string.Empty;
            string name = parts.Length > 2 ? parts[2] : string.Empty;
            if (mode == SettingOutElevationMode.Blank) return Blank();
            if (mode == SettingOutElevationMode.Point) return PointElevation();
            return Surface(string.IsNullOrWhiteSpace(name) ? "<Surface>" : name, handle);
        }

        public override string ToString()
        {
            return DisplayName;
        }
    }

    internal sealed class SettingOutLink
    {
        public SettingOutLink(
            string scheduleType,
            SettingOutSurfaceChoice ground,
            SettingOutSurfaceChoice design,
            IEnumerable<string> sourceHandles)
        {
            ScheduleType = string.IsNullOrWhiteSpace(scheduleType)
                ? "General"
                : scheduleType;
            Ground = ground ?? SettingOutSurfaceChoice.PointElevation();
            Design = design ?? SettingOutSurfaceChoice.PointElevation();
            SourceHandles = sourceHandles == null
                ? new List<string>()
                : sourceHandles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        public string ScheduleType { get; private set; }
        public SettingOutSurfaceChoice Ground { get; private set; }
        public SettingOutSurfaceChoice Design { get; private set; }
        public List<string> SourceHandles { get; private set; }
    }

    internal sealed class SettingOutRow
    {
        public SettingOutRow(
            string description,
            double x,
            double y,
            double? groundElevation,
            double? designElevation,
            double? difference)
        {
            Description = description;
            X = x;
            Y = y;
            GroundElevation = groundElevation;
            DesignElevation = designElevation;
            Difference = difference;
        }

        public string Description { get; private set; }
        public double X { get; private set; }
        public double Y { get; private set; }
        public double? GroundElevation { get; private set; }
        public double? DesignElevation { get; private set; }
        public double? Difference { get; private set; }
    }

    internal sealed class SettingOutConfigurationWindow : Window
    {
        private readonly ComboBox _type;
        private readonly ComboBox _ground;
        private readonly ComboBox _design;

        public SettingOutConfigurationWindow(IList<SettingOutSurfaceChoice> choices)
        {
            Accepted = false;
            Title = "CE Tools - Setting-Out Schedule";
            Width = 650;
            Height = 390;
            MinWidth = 540;
            MinHeight = 330;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.CanResize;

            var root = new DockPanel { Margin = new Thickness(16) };
            Content = root;
            var heading = new TextBlock
            {
                Text = "Setting-out schedule configuration",
                FontSize = 20,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 6)
            };
            DockPanel.SetDock(heading, Dock.Top);
            root.Children.Add(heading);
            var note = new TextBlock
            {
                Text = "Choose the schedule purpose and the elevation sources. Surface samples outside a surface boundary remain blank rather than inventing a value.",
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
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int index = 0; index < 3; index++)
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            _type = new ComboBox
            {
                ItemsSource = new[]
                {
                    "Platform",
                    "Road Horizontal",
                    "Road Vertical",
                    "Junction",
                    "General"
                },
                SelectedIndex = 0,
                Margin = new Thickness(0, 4, 0, 4)
            };
            _ground = new ComboBox
            {
                ItemsSource = choices,
                SelectedIndex = 0,
                IsTextSearchEnabled = true,
                Margin = new Thickness(0, 4, 0, 4)
            };
            _design = new ComboBox
            {
                ItemsSource = choices,
                SelectedIndex = choices != null && choices.Count > 2 ? 2 : 0,
                IsTextSearchEnabled = true,
                Margin = new Thickness(0, 4, 0, 4)
            };
            AddRow(grid, 0, "Schedule type", _type);
            AddRow(grid, 1, "Ground elevation source", _ground);
            AddRow(grid, 2, "Design elevation source", _design);
            root.Children.Add(grid);
        }

        public bool Accepted { get; private set; }
        public string ScheduleType
        {
            get { return Convert.ToString(_type.SelectedItem, CultureInfo.CurrentCulture); }
        }
        public SettingOutSurfaceChoice GroundChoice
        {
            get { return _ground.SelectedItem as SettingOutSurfaceChoice; }
        }
        public SettingOutSurfaceChoice DesignChoice
        {
            get { return _design.SelectedItem as SettingOutSurfaceChoice; }
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

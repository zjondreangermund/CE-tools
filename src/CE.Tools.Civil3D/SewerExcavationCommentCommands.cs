using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.SewerExcavationCommentCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Linked sewer excavation schedule. Pipe handles and engineering assumptions
    /// are stored on the table so trench excavation, bedding and backfill can be
    /// refreshed after pipe lengths or sizes change.
    /// </summary>
    public sealed class SewerExcavationCommentCommands
    {
        private const string LinkRecordName = "CE_SEWER_EXCAVATION_LINKS";
        private const string LinkSchema = "1";
        private const int ColumnCount = 10;

        [CommandMethod("CE_TOOLS", "CE_SEWEREXCAVATION", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void Build()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            PromptSelectionResult selection = GetSelection(
                document.Editor,
                "\nSelect sewer pipe objects for the linked excavation schedule: ");
            if (selection.Status != PromptStatus.OK) return;

            var settingsWindow = new SewerExcavationSettingsWindow(new SewerExcavationSettings());
            AcApplication.ShowModalWindow(settingsWindow);
            if (!settingsWindow.Accepted) return;
            SewerExcavationSettings settings = settingsWindow.Settings;

            List<ObjectId> sourceIds = selection.Value.GetObjectIds().ToList();
            ExtractionResult extraction = Extract(document.Database, sourceIds, settings);
            if (extraction.Rows.Count == 0)
            {
                document.Editor.WriteMessage(
                    "\nCE_SEWEREXCAVATION stopped. No supported pipe lengths and diameters were found. Rejected={0}.",
                    extraction.Rejections.Count);
                foreach (string reason in extraction.Rejections.Take(8))
                    document.Editor.WriteMessage("\n  REJECTED: {0}", reason);
                return;
            }

            ShowPreview(document, extraction, settings);
            if (!Confirm(document.Editor, "Create the linked sewer excavation table")) return;
            PromptPointResult insertion = document.Editor.GetPoint(
                "\nPick insertion point for the linked sewer excavation table: ");
            if (insertion.Status != PromptStatus.OK) return;

            Point3d position = insertion.Value.TransformBy(document.Editor.CurrentUserCoordinateSystem);
            try
            {
                ObjectId tableId = CreateLinkedTable(
                    document.Database,
                    position,
                    extraction.Rows,
                    settings,
                    extraction.UsableHandles);
                document.Editor.WriteMessage(
                    "\nCE_SEWEREXCAVATION complete. Pipes={0}; rejected={1}; table={2}; excavation={3:N3} m³.",
                    extraction.Rows.Count,
                    extraction.Rejections.Count,
                    tableId.Handle,
                    extraction.Rows.Sum(row => row.Excavation));
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_SEWEREXCAVATION failed. No linked table was committed. {0}",
                    exception.Message);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_SEWEREXCAVATIONREFRESH", CommandFlags.Modal | CommandFlags.Redraw)]
        public void Refresh()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            PromptEntityResult tableResult = PromptForLinkedTable(
                document.Editor,
                "\nSelect a linked CE sewer excavation table to refresh: ");
            if (tableResult.Status != PromptStatus.OK) return;
            RefreshTable(document, tableResult.ObjectId, true);
        }

        [CommandMethod("CE_TOOLS", "CE_SEWEREXCAVATIONINFO", CommandFlags.Modal | CommandFlags.Redraw)]
        public void Information()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            PromptEntityResult tableResult = PromptForLinkedTable(
                document.Editor,
                "\nSelect a linked CE sewer excavation table for information: ");
            if (tableResult.Status != PromptStatus.OK) return;

            try
            {
                SewerExcavationLink link;
                int displayedRows;
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    Table table = transaction.GetObject(tableResult.ObjectId, OpenMode.ForRead, false) as Table;
                    link = ReadLink(table, transaction);
                    displayedRows = table == null ? 0 : Math.Max(0, table.Rows.Count - 3);
                }

                int active = 0;
                int missing = 0;
                foreach (string handle in link.Handles)
                {
                    ObjectId id;
                    if (TryResolveHandle(document.Database, handle, out id)) active++;
                    else missing++;
                }

                GridReportPresenter.ShowReportAndOfferTable(
                    document,
                    "CE Tools - Sewer Excavation Link Information",
                    "Stored source handles and engineering assumptions used by CE_SEWEREXCAVATIONREFRESH.",
                    new List<string> { "Property", "Value" },
                    new List<IList<string>>
                    {
                        new List<string> { "Schema", link.Schema },
                        new List<string> { "Stored pipe handles", link.Handles.Count.ToString(CultureInfo.InvariantCulture) },
                        new List<string> { "Resolvable pipes", active.ToString(CultureInfo.InvariantCulture) },
                        new List<string> { "Missing pipes", missing.ToString(CultureInfo.InvariantCulture) },
                        new List<string> { "Displayed pipe rows", displayedRows.ToString(CultureInfo.InvariantCulture) },
                        new List<string> { "Drawing units per metre", link.Settings.UnitsPerMetre.ToString("N6", CultureInfo.CurrentCulture) },
                        new List<string> { "Side allowance each side", link.Settings.SideAllowance.ToString("N3", CultureInfo.CurrentCulture) + " m" },
                        new List<string> { "Minimum trench width", link.Settings.MinimumWidth.ToString("N3", CultureInfo.CurrentCulture) + " m" },
                        new List<string> { "Bedding thickness", link.Settings.BeddingThickness.ToString("N3", CultureInfo.CurrentCulture) + " m" },
                        new List<string> { "Fallback average cover", link.Settings.FallbackCover.ToString("N3", CultureInfo.CurrentCulture) + " m" },
                        new List<string> { "Refresh command", "CE_SEWEREXCAVATIONREFRESH" }
                    },
                    "CE TOOLS SEWER EXCAVATION INFORMATION");
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage("\nCE_SEWEREXCAVATIONINFO failed. {0}", exception.Message);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_SEWEREXCAVATIONEXPORT", CommandFlags.Modal | CommandFlags.Redraw)]
        public void Export()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            PromptEntityResult tableResult = PromptForLinkedTable(
                document.Editor,
                "\nSelect a linked CE sewer excavation table to export: ");
            if (tableResult.Status != PromptStatus.OK) return;

            if (Confirm(document.Editor, "Refresh sewer excavation quantities before export"))
            {
                if (!RefreshTable(document, tableResult.ObjectId, false)) return;
            }

            var options = new PromptSaveFileOptions("\nSelect sewer excavation Excel workbook output path: ")
            {
                Filter = "Excel Workbook (*.xlsx)|*.xlsx",
                DialogCaption = "Export CE Tools Sewer Excavation Schedule",
                InitialFileName = "CE-Tools-Sewer-Excavation.xlsx"
            };
            PromptFileNameResult pathResult = document.Editor.GetFileNameForSave(options);
            if (pathResult.Status != PromptStatus.OK) return;
            string path = pathResult.StringResult;
            if (!path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)) path += ".xlsx";

            try
            {
                List<IList<string>> rows;
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    Table table = transaction.GetObject(tableResult.ObjectId, OpenMode.ForRead, false) as Table;
                    ReadLink(table, transaction);
                    rows = ReadTableCells(table);
                }
                SimpleXlsxWriter.Write(path, "Sewer Excavation", rows);
                document.Editor.WriteMessage(
                    "\nCE_SEWEREXCAVATIONEXPORT complete. Workbook: {0}",
                    path);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_SEWEREXCAVATIONEXPORT failed. {0}",
                    exception.Message);
            }
        }

        internal static int RefreshAll(Document document)
        {
            if (document == null) return 0;
            List<ObjectId> tableIds = FindLinkedTables(document.Database);
            int refreshed = 0;
            foreach (ObjectId tableId in tableIds)
            {
                if (RefreshTable(document, tableId, false)) refreshed++;
            }
            return refreshed;
        }

        private static bool RefreshTable(Document document, ObjectId tableId, bool askConfirmation)
        {
            try
            {
                SewerExcavationLink link;
                Point3d position;
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    Table table = transaction.GetObject(tableId, OpenMode.ForRead, false) as Table;
                    link = ReadLink(table, transaction);
                    position = table.Position;
                }

                var ids = new List<ObjectId>();
                int stale = 0;
                foreach (string handle in link.Handles)
                {
                    ObjectId id;
                    if (TryResolveHandle(document.Database, handle, out id)) ids.Add(id);
                    else stale++;
                }
                ExtractionResult extraction = Extract(document.Database, ids, link.Settings);
                if (extraction.Rows.Count == 0)
                {
                    document.Editor.WriteMessage(
                        "\nCE_SEWEREXCAVATIONREFRESH stopped. No live source pipe produces a usable quantity; the existing table remains unchanged.");
                    return false;
                }

                document.Editor.WriteMessage(
                    "\nCE_SEWEREXCAVATIONREFRESH preview. Pipes={0}; stale handles={1}; rejected={2}; excavation={3:N3} m³.",
                    extraction.Rows.Count,
                    stale,
                    extraction.Rejections.Count,
                    extraction.Rows.Sum(row => row.Excavation));
                if (askConfirmation && !Confirm(document.Editor, "Replace the displayed excavation quantities"))
                    return false;

                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    Table table = transaction.GetObject(tableId, OpenMode.ForWrite, false) as Table;
                    if (table == null) throw new InvalidOperationException("The selected object is not a table.");
                    table.Position = position;
                    PopulateTable(document.Database, table, extraction.Rows, link.Settings);
                    WriteLink(
                        table,
                        transaction,
                        new SewerExcavationLink(
                            LinkSchema,
                            link.Settings,
                            extraction.UsableHandles));
                    table.GenerateLayout();
                    transaction.Commit();
                }

                document.Editor.WriteMessage(
                    "\nCE_SEWEREXCAVATIONREFRESH complete. Pipes={0}; stale removed={1}.",
                    extraction.Rows.Count,
                    stale);
                return true;
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_SEWEREXCAVATIONREFRESH failed. The table was not changed. {0}",
                    exception.Message);
                return false;
            }
        }

        private static ExtractionResult Extract(
            Database database,
            IEnumerable<ObjectId> objectIds,
            SewerExcavationSettings settings)
        {
            var result = new ExtractionResult();
            if (objectIds == null) return result;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId objectId in objectIds)
                {
                    if (objectId.IsNull || objectId.IsErased)
                    {
                        result.Rejections.Add("Null or erased source object.");
                        continue;
                    }
                    DBObject value;
                    try
                    {
                        value = transaction.GetObject(objectId, OpenMode.ForRead, false);
                    }
                    catch (System.Exception exception)
                    {
                        result.Rejections.Add(objectId.Handle + ": cannot open - " + exception.Message);
                        continue;
                    }
                    Entity entity = value as Entity;
                    if (entity == null || !LooksLikePipe(value))
                    {
                        result.Rejections.Add(objectId.Handle + ": object is not a supported sewer pipe.");
                        continue;
                    }

                    double rawLength;
                    if (!TryGetLength(value, out rawLength))
                    {
                        result.Rejections.Add(objectId.Handle + ": no usable pipe length was found.");
                        continue;
                    }
                    double rawDiameter;
                    if (!TryReadNumber(
                        value,
                        out rawDiameter,
                        "InnerDiameterOrWidth",
                        "NominalDiameter",
                        "Diameter",
                        "OutsideDiameter"))
                    {
                        result.Rejections.Add(objectId.Handle + ": no usable pipe diameter was found.");
                        continue;
                    }

                    double length = rawLength / settings.UnitsPerMetre;
                    double diameter = rawDiameter / settings.UnitsPerMetre;
                    if (!Positive(length) || !Positive(diameter))
                    {
                        result.Rejections.Add(objectId.Handle + ": converted length or diameter is invalid.");
                        continue;
                    }
                    double cover = ReadAverageCover(value, settings);
                    double width = Math.Max(settings.MinimumWidth, diameter + (2.0 * settings.SideAllowance));
                    double depth = cover + diameter + settings.BeddingThickness;
                    double excavation = length * width * depth;
                    double bedding = length * width * settings.BeddingThickness;
                    double pipeDisplacement = Math.PI * Math.Pow(diameter / 2.0, 2.0) * length;
                    double backfill = Math.Max(0.0, excavation - bedding - pipeDisplacement);
                    result.Rows.Add(new PipeExcavationRow
                    {
                        Handle = objectId.Handle.ToString(),
                        Name = ReadText(value, "Name", value.GetType().Name),
                        Layer = entity.Layer,
                        Length = length,
                        Diameter = diameter,
                        AverageCover = cover,
                        TrenchWidth = width,
                        TrenchDepth = depth,
                        Excavation = excavation,
                        Bedding = bedding,
                        Backfill = backfill
                    });
                    result.UsableHandles.Add(objectId.Handle.ToString());
                }
            }
            return result;
        }

        private static double ReadAverageCover(object value, SewerExcavationSettings settings)
        {
            double raw;
            if (TryReadNumber(value, out raw, "AverageCover", "Cover"))
                return Math.Max(0.0, raw / settings.UnitsPerMetre);
            double start;
            double end;
            bool hasStart = TryReadNumber(value, out start, "StartCover", "CoverAtStart");
            bool hasEnd = TryReadNumber(value, out end, "EndCover", "CoverAtEnd");
            if (hasStart && hasEnd)
                return Math.Max(0.0, ((start + end) / 2.0) / settings.UnitsPerMetre);
            if (hasStart) return Math.Max(0.0, start / settings.UnitsPerMetre);
            if (hasEnd) return Math.Max(0.0, end / settings.UnitsPerMetre);
            return settings.FallbackCover;
        }

        private static bool TryGetLength(object value, out double length)
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
                    if (Positive(length)) return true;
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
                return Positive(length);
            }
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
                    length = start.DistanceTo(end);
                    if (Positive(length)) return true;
                }
            }
            return false;
        }

        private static bool LooksLikePipe(object value)
        {
            if (value == null) return false;
            string name = value.GetType().Name.ToUpperInvariant();
            return name.Contains("PIPE") &&
                   !name.Contains("NETWORK") &&
                   !name.Contains("STYLE") &&
                   !name.Contains("LABEL");
        }

        private static ObjectId CreateLinkedTable(
            Database database,
            Point3d position,
            IList<PipeExcavationRow> rows,
            SewerExcavationSettings settings,
            IList<string> handles)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTableRecord currentSpace = transaction.GetObject(
                    database.CurrentSpaceId,
                    OpenMode.ForWrite,
                    false) as BlockTableRecord;
                if (currentSpace == null) throw new InvalidOperationException("Current drawing space could not be opened.");
                var table = new Table();
                table.SetDatabaseDefaults(database);
                table.TableStyle = database.Tablestyle;
                table.Position = position;
                PopulateTable(database, table, rows, settings);
                ObjectId tableId = currentSpace.AppendEntity(table);
                transaction.AddNewlyCreatedDBObject(table, true);
                table.CreateExtensionDictionary();
                WriteLink(table, transaction, new SewerExcavationLink(LinkSchema, settings, handles));
                table.GenerateLayout();
                transaction.Commit();
                return tableId;
            }
        }

        private static void PopulateTable(
            Database database,
            Table table,
            IList<PipeExcavationRow> rows,
            SewerExcavationSettings settings)
        {
            table.SetSize(rows.Count + 3, ColumnCount);
            double height = ResolveTextHeight(database);
            table.SetRowHeight(height * 1.8);
            double[] widths =
            {
                height * 8.0, height * 8.0, height * 5.0, height * 5.0, height * 5.0,
                height * 5.0, height * 5.0, height * 6.0, height * 6.0, height * 6.0
            };
            for (int column = 0; column < ColumnCount; column++)
                table.Columns[column].Width = widths[column];

            table.MergeCells(CellRange.Create(table, 0, 0, 0, ColumnCount - 1));
            table.Cells[0, 0].TextString = string.Format(
                CultureInfo.CurrentCulture,
                "CE TOOLS LINKED SEWER EXCAVATION - UNITS/M {0:N6}",
                settings.UnitsPerMetre);
            table.Cells[0, 0].Alignment = CellAlignment.MiddleCenter;
            table.Cells[0, 0].TextHeight = height * 1.15;
            string[] headings =
            {
                "PIPE", "LAYER", "LENGTH m", "DIAMETER m", "AVG COVER m",
                "WIDTH m", "DEPTH m", "EXCAVATION m³", "BEDDING m³", "BACKFILL m³"
            };
            for (int column = 0; column < headings.Length; column++)
            {
                table.Cells[1, column].TextString = headings[column];
                table.Cells[1, column].Alignment = CellAlignment.MiddleCenter;
                table.Cells[1, column].TextHeight = height;
            }

            for (int index = 0; index < rows.Count; index++)
            {
                PipeExcavationRow row = rows[index];
                int tableRow = index + 2;
                string[] values =
                {
                    row.Name,
                    row.Layer,
                    row.Length.ToString("N3", CultureInfo.CurrentCulture),
                    row.Diameter.ToString("N3", CultureInfo.CurrentCulture),
                    row.AverageCover.ToString("N3", CultureInfo.CurrentCulture),
                    row.TrenchWidth.ToString("N3", CultureInfo.CurrentCulture),
                    row.TrenchDepth.ToString("N3", CultureInfo.CurrentCulture),
                    row.Excavation.ToString("N3", CultureInfo.CurrentCulture),
                    row.Bedding.ToString("N3", CultureInfo.CurrentCulture),
                    row.Backfill.ToString("N3", CultureInfo.CurrentCulture)
                };
                for (int column = 0; column < ColumnCount; column++)
                {
                    table.Cells[tableRow, column].TextString = values[column];
                    table.Cells[tableRow, column].TextHeight = height;
                    table.Cells[tableRow, column].Alignment = column < 2
                        ? CellAlignment.MiddleLeft
                        : CellAlignment.MiddleCenter;
                }
            }

            int totalRow = rows.Count + 2;
            table.Cells[totalRow, 0].TextString = "TOTAL";
            table.Cells[totalRow, 2].TextString = rows.Sum(row => row.Length).ToString("N3", CultureInfo.CurrentCulture);
            table.Cells[totalRow, 7].TextString = rows.Sum(row => row.Excavation).ToString("N3", CultureInfo.CurrentCulture);
            table.Cells[totalRow, 8].TextString = rows.Sum(row => row.Bedding).ToString("N3", CultureInfo.CurrentCulture);
            table.Cells[totalRow, 9].TextString = rows.Sum(row => row.Backfill).ToString("N3", CultureInfo.CurrentCulture);
            for (int column = 0; column < ColumnCount; column++)
            {
                table.Cells[totalRow, column].TextHeight = height;
                table.Cells[totalRow, column].Alignment = CellAlignment.MiddleCenter;
            }
        }

        private static void WriteLink(
            Table table,
            Transaction transaction,
            SewerExcavationLink link)
        {
            if (table.ExtensionDictionary.IsNull) table.CreateExtensionDictionary();
            DBDictionary dictionary = transaction.GetObject(
                table.ExtensionDictionary,
                OpenMode.ForWrite,
                false) as DBDictionary;
            if (dictionary == null) throw new InvalidOperationException("Excavation extension dictionary could not be opened.");
            Xrecord record;
            if (dictionary.Contains(LinkRecordName))
            {
                record = transaction.GetObject(dictionary.GetAt(LinkRecordName), OpenMode.ForWrite, false) as Xrecord;
            }
            else
            {
                record = new Xrecord();
                dictionary.SetAt(LinkRecordName, record);
                transaction.AddNewlyCreatedDBObject(record, true);
            }
            var values = new List<TypedValue>
            {
                TextValue("Schema", link.Schema),
                NumberValue("UnitsPerMetre", link.Settings.UnitsPerMetre),
                NumberValue("SideAllowance", link.Settings.SideAllowance),
                NumberValue("MinimumWidth", link.Settings.MinimumWidth),
                NumberValue("BeddingThickness", link.Settings.BeddingThickness),
                NumberValue("FallbackCover", link.Settings.FallbackCover)
            };
            foreach (string handle in link.Handles.Distinct(StringComparer.OrdinalIgnoreCase))
                values.Add(TextValue("Handle", handle));
            record.Data = new ResultBuffer(values.ToArray());
        }

        private static SewerExcavationLink ReadLink(Table table, Transaction transaction)
        {
            if (table == null) throw new InvalidOperationException("The selected object is not a table.");
            if (table.ExtensionDictionary.IsNull) throw new InvalidOperationException("The table has no CE sewer excavation link.");
            DBDictionary dictionary = transaction.GetObject(table.ExtensionDictionary, OpenMode.ForRead, false) as DBDictionary;
            if (dictionary == null || !dictionary.Contains(LinkRecordName))
                throw new InvalidOperationException("The table is not a linked CE sewer excavation schedule.");
            Xrecord record = transaction.GetObject(dictionary.GetAt(LinkRecordName), OpenMode.ForRead, false) as Xrecord;
            if (record == null || record.Data == null) throw new InvalidOperationException("The sewer excavation link is empty.");

            var settings = new SewerExcavationSettings();
            string schema = LinkSchema;
            var handles = new List<string>();
            foreach (TypedValue typedValue in record.Data)
            {
                string text = typedValue.Value as string;
                if (string.IsNullOrWhiteSpace(text)) continue;
                int equals = text.IndexOf('=');
                if (equals <= 0) continue;
                string key = text.Substring(0, equals);
                string value = text.Substring(equals + 1);
                double number;
                if (key.Equals("Schema", StringComparison.OrdinalIgnoreCase)) schema = value;
                else if (key.Equals("Handle", StringComparison.OrdinalIgnoreCase)) handles.Add(value);
                else if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                {
                    if (key.Equals("UnitsPerMetre", StringComparison.OrdinalIgnoreCase)) settings.UnitsPerMetre = number;
                    else if (key.Equals("SideAllowance", StringComparison.OrdinalIgnoreCase)) settings.SideAllowance = number;
                    else if (key.Equals("MinimumWidth", StringComparison.OrdinalIgnoreCase)) settings.MinimumWidth = number;
                    else if (key.Equals("BeddingThickness", StringComparison.OrdinalIgnoreCase)) settings.BeddingThickness = number;
                    else if (key.Equals("FallbackCover", StringComparison.OrdinalIgnoreCase)) settings.FallbackCover = number;
                }
            }
            if (handles.Count == 0) throw new InvalidOperationException("The sewer excavation link has no pipe handles.");
            settings.Validate();
            return new SewerExcavationLink(schema, settings, handles);
        }

        private static TypedValue TextValue(string key, string value)
        {
            return new TypedValue((int)DxfCode.Text, key + "=" + (value ?? string.Empty));
        }

        private static TypedValue NumberValue(string key, double value)
        {
            return TextValue(key, value.ToString("R", CultureInfo.InvariantCulture));
        }

        private static List<ObjectId> FindLinkedTables(Database database)
        {
            var result = new List<ObjectId>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTable blockTable = transaction.GetObject(database.BlockTableId, OpenMode.ForRead, false) as BlockTable;
                if (blockTable == null) return result;
                foreach (ObjectId blockId in blockTable)
                {
                    BlockTableRecord block = transaction.GetObject(blockId, OpenMode.ForRead, false) as BlockTableRecord;
                    if (block == null || block.IsFromExternalReference) continue;
                    foreach (ObjectId entityId in block)
                    {
                        Table table = transaction.GetObject(entityId, OpenMode.ForRead, false) as Table;
                        if (table == null || table.ExtensionDictionary.IsNull) continue;
                        DBDictionary dictionary = transaction.GetObject(table.ExtensionDictionary, OpenMode.ForRead, false) as DBDictionary;
                        if (dictionary != null && dictionary.Contains(LinkRecordName)) result.Add(entityId);
                    }
                }
            }
            return result;
        }

        private static void ShowPreview(
            Document document,
            ExtractionResult extraction,
            SewerExcavationSettings settings)
        {
            var rows = new List<IList<string>>();
            foreach (PipeExcavationRow row in extraction.Rows)
            {
                rows.Add(new List<string>
                {
                    row.Name,
                    row.Layer,
                    row.Length.ToString("N3", CultureInfo.CurrentCulture),
                    row.Diameter.ToString("N3", CultureInfo.CurrentCulture),
                    row.AverageCover.ToString("N3", CultureInfo.CurrentCulture),
                    row.TrenchWidth.ToString("N3", CultureInfo.CurrentCulture),
                    row.TrenchDepth.ToString("N3", CultureInfo.CurrentCulture),
                    row.Excavation.ToString("N3", CultureInfo.CurrentCulture),
                    row.Bedding.ToString("N3", CultureInfo.CurrentCulture),
                    row.Backfill.ToString("N3", CultureInfo.CurrentCulture)
                });
            }
            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - Sewer Excavation Preview",
                string.Format(
                    CultureInfo.CurrentCulture,
                    "Pipes={0}; rejected={1}; side allowance={2:N3} m; bedding={3:N3} m; fallback cover={4:N3} m.",
                    extraction.Rows.Count,
                    extraction.Rejections.Count,
                    settings.SideAllowance,
                    settings.BeddingThickness,
                    settings.FallbackCover),
                new List<string>
                {
                    "Pipe", "Layer", "Length m", "Diameter m", "Cover m", "Width m", "Depth m", "Excavation m³", "Bedding m³", "Backfill m³"
                },
                rows,
                "CE TOOLS SEWER EXCAVATION PREVIEW");
        }

        private static List<IList<string>> ReadTableCells(Table table)
        {
            var result = new List<IList<string>>();
            if (table == null) return result;
            for (int row = 0; row < table.Rows.Count; row++)
            {
                var values = new List<string>();
                for (int column = 0; column < table.Columns.Count; column++)
                {
                    try { values.Add(table.Cells[row, column].TextString ?? string.Empty); }
                    catch { values.Add(string.Empty); }
                }
                result.Add(values);
            }
            return result;
        }

        private static bool TryResolveHandle(Database database, string text, out ObjectId objectId)
        {
            objectId = ObjectId.Null;
            long value;
            if (!long.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)) return false;
            try
            {
                objectId = database.GetObjectId(false, new Handle(value), 0);
                return !objectId.IsNull && !objectId.IsErased;
            }
            catch { return false; }
        }

        private static bool TryReadNumber(object value, out double number, params string[] propertyNames)
        {
            number = 0.0;
            if (value == null) return false;
            foreach (string propertyName in propertyNames)
            {
                try
                {
                    PropertyInfo property = value.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                    if (property == null || property.GetIndexParameters().Length != 0) continue;
                    object raw = property.GetValue(value, null);
                    if (raw == null) continue;
                    number = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
                    if (Positive(number)) return true;
                }
                catch { }
            }
            return false;
        }

        private static bool TryReadPoint(object value, string propertyName, out Point3d point)
        {
            point = Point3d.Origin;
            try
            {
                PropertyInfo property = value.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                object raw = property == null ? null : property.GetValue(value, null);
                if (!(raw is Point3d)) return false;
                point = (Point3d)raw;
                return true;
            }
            catch { return false; }
        }

        private static string ReadText(object value, string propertyName, string fallback)
        {
            try
            {
                PropertyInfo property = value.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                object raw = property == null ? null : property.GetValue(value, null);
                string text = Convert.ToString(raw, CultureInfo.CurrentCulture);
                return string.IsNullOrWhiteSpace(text) ? fallback : text;
            }
            catch { return fallback; }
        }

        private static double ResolveTextHeight(Database database)
        {
            double height = database == null ? 2.0 : database.Textsize;
            if (Math.Abs(height - 1.8) < 0.05) return 1.8;
            if (Math.Abs(height - 5.0) < 0.05) return 5.0;
            return 2.0;
        }

        private static bool Positive(double value)
        {
            return value > 0.0 && !double.IsNaN(value) && !double.IsInfinity(value);
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

        private static PromptEntityResult PromptForLinkedTable(Editor editor, string message)
        {
            var options = new PromptEntityOptions(message);
            options.SetRejectMessage("\nSelect an AutoCAD table.");
            options.AddAllowedClass(typeof(Table), false);
            return editor.GetEntity(options);
        }

        private static bool Confirm(Editor editor, string message)
        {
            var options = new PromptKeywordOptions("\n" + message + "? [Yes/No] <No>: ")
            {
                AllowNone = true
            };
            options.Keywords.Add("Yes");
            options.Keywords.Add("No");
            PromptResult result = editor.GetKeywords(options);
            return result.Status == PromptStatus.OK &&
                   string.Equals(result.StringResult, "Yes", StringComparison.OrdinalIgnoreCase);
        }

        private static Document ActiveDocument()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
        }

        private sealed class PipeExcavationRow
        {
            public string Handle { get; set; }
            public string Name { get; set; }
            public string Layer { get; set; }
            public double Length { get; set; }
            public double Diameter { get; set; }
            public double AverageCover { get; set; }
            public double TrenchWidth { get; set; }
            public double TrenchDepth { get; set; }
            public double Excavation { get; set; }
            public double Bedding { get; set; }
            public double Backfill { get; set; }
        }

        private sealed class ExtractionResult
        {
            public ExtractionResult()
            {
                Rows = new List<PipeExcavationRow>();
                UsableHandles = new List<string>();
                Rejections = new List<string>();
            }
            public List<PipeExcavationRow> Rows { get; }
            public List<string> UsableHandles { get; }
            public List<string> Rejections { get; }
        }

        private sealed class SewerExcavationLink
        {
            public SewerExcavationLink(
                string schema,
                SewerExcavationSettings settings,
                IEnumerable<string> handles)
            {
                Schema = string.IsNullOrWhiteSpace(schema) ? LinkSchema : schema;
                Settings = settings;
                Handles = handles == null
                    ? new List<string>()
                    : handles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
            public string Schema { get; }
            public SewerExcavationSettings Settings { get; }
            public List<string> Handles { get; }
        }
    }

    internal sealed class SewerExcavationSettings
    {
        public SewerExcavationSettings()
        {
            UnitsPerMetre = 1.0;
            SideAllowance = 0.30;
            MinimumWidth = 0.60;
            BeddingThickness = 0.15;
            FallbackCover = 1.20;
        }
        public double UnitsPerMetre { get; set; }
        public double SideAllowance { get; set; }
        public double MinimumWidth { get; set; }
        public double BeddingThickness { get; set; }
        public double FallbackCover { get; set; }
        public void Validate()
        {
            if (!IsPositive(UnitsPerMetre)) UnitsPerMetre = 1.0;
            if (SideAllowance < 0.0 || double.IsNaN(SideAllowance) || double.IsInfinity(SideAllowance)) SideAllowance = 0.30;
            if (!IsPositive(MinimumWidth)) MinimumWidth = 0.60;
            if (!IsPositive(BeddingThickness)) BeddingThickness = 0.15;
            if (!IsPositive(FallbackCover)) FallbackCover = 1.20;
        }
        private static bool IsPositive(double value)
        {
            return value > 0.0 && !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }

    internal sealed class SewerExcavationSettingsWindow : Window
    {
        private readonly Dictionary<string, TextBox> _values;
        public SewerExcavationSettingsWindow(SewerExcavationSettings initial)
        {
            Title = "CE Tools - Sewer Excavation Settings";
            Width = 580;
            Height = 410;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            _values = new Dictionary<string, TextBox>(StringComparer.OrdinalIgnoreCase);
            var root = new DockPanel { Margin = new Thickness(18) };
            Content = root;
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(buttons);
            var cancel = new Button { Content = "Cancel", Width = 90, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
            cancel.Click += delegate { Close(); };
            buttons.Children.Add(cancel);
            var apply = new Button { Content = "Review Quantities", Width = 130, IsDefault = true };
            apply.Click += delegate
            {
                SewerExcavationSettings parsed;
                string error;
                if (!TryReadSettings(out parsed, out error))
                {
                    MessageBox.Show(error, "CE Tools", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                Settings = parsed;
                Accepted = true;
                Close();
            };
            buttons.Children.Add(apply);

            var panel = new StackPanel();
            root.Children.Add(panel);
            panel.Children.Add(new TextBlock
            {
                Text = "Values are in metres after applying drawing units per metre. Pipe cover is read from Civil 3D when available; otherwise the fallback cover is used.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            });
            AddField(panel, "UnitsPerMetre", "Drawing units per metre", initial.UnitsPerMetre);
            AddField(panel, "SideAllowance", "Side allowance each side (m)", initial.SideAllowance);
            AddField(panel, "MinimumWidth", "Minimum trench width (m)", initial.MinimumWidth);
            AddField(panel, "BeddingThickness", "Bedding thickness (m)", initial.BeddingThickness);
            AddField(panel, "FallbackCover", "Fallback average cover (m)", initial.FallbackCover);
        }

        public bool Accepted { get; private set; }
        public SewerExcavationSettings Settings { get; private set; }

        private void AddField(Panel parent, string key, string label, double value)
        {
            var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(text, 0);
            grid.Children.Add(text);
            var box = new TextBox
            {
                Text = value.ToString("0.###", CultureInfo.InvariantCulture),
                MinWidth = 180,
                Margin = new Thickness(8, 0, 0, 0)
            };
            Grid.SetColumn(box, 1);
            grid.Children.Add(box);
            _values[key] = box;
            parent.Children.Add(grid);
        }

        private bool TryReadSettings(out SewerExcavationSettings settings, out string error)
        {
            settings = new SewerExcavationSettings();
            error = string.Empty;
            double units;
            double side;
            double width;
            double bedding;
            double cover;
            if (!TryRead("UnitsPerMetre", out units) || units <= 0.0)
                error = "Drawing units per metre must be greater than zero.";
            else if (!TryRead("SideAllowance", out side) || side < 0.0)
                error = "Side allowance cannot be negative.";
            else if (!TryRead("MinimumWidth", out width) || width <= 0.0)
                error = "Minimum trench width must be greater than zero.";
            else if (!TryRead("BeddingThickness", out bedding) || bedding <= 0.0)
                error = "Bedding thickness must be greater than zero.";
            else if (!TryRead("FallbackCover", out cover) || cover <= 0.0)
                error = "Fallback cover must be greater than zero.";
            if (!string.IsNullOrEmpty(error)) return false;
            settings.UnitsPerMetre = units;
            settings.SideAllowance = side;
            settings.MinimumWidth = width;
            settings.BeddingThickness = bedding;
            settings.FallbackCover = cover;
            return true;
        }

        private bool TryRead(string key, out double value)
        {
            string text = _values[key].Text;
            return double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
                   double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
    }
}

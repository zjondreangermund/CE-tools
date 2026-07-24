using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.BillOfQuantitiesCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Linked bill-of-quantities tables and dependency-free Excel exports.
    ///
    /// Source object handles, discipline and drawing-units-per-metre are stored
    /// on each CE BOQ table. CE_BOQREFRESH explicitly recalculates the table from
    /// current drawing geometry while preserving entered rates where item keys
    /// remain unchanged.
    /// </summary>
    public sealed class BillOfQuantitiesCommands
    {
        private const string LinkRecordName = "CE_BOQ_LINKS";
        private const string LinkSchema = "1";
        private const int ColumnCount = 10;

        [CommandMethod(
            "CE_TOOLS",
            "CE_BOQTOOLS",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void BoqTools()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            var options = new PromptKeywordOptions(
                "\nBOQ tool [Build/Refresh/Information/Export/Road/Platform/Stormwater/Sewer/Water/BulkWater] <Build>: ")
            {
                AllowNone = true
            };
            foreach (string keyword in new[]
            {
                "Build", "Refresh", "Information", "Export", "Road", "Platform",
                "Stormwater", "Sewer", "Water", "BulkWater"
            })
            {
                options.Keywords.Add(keyword);
            }

            PromptResult result = document.Editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel) return;
            string mode = result.Status == PromptStatus.None ? "Build" : result.StringResult;

            if (Equal(mode, "Refresh")) Refresh();
            else if (Equal(mode, "Information")) Information();
            else if (Equal(mode, "Export")) ExportLinked();
            else if (Equal(mode, "Road")) ExportDiscipline(document, BoqDiscipline.Road);
            else if (Equal(mode, "Platform")) ExportDiscipline(document, BoqDiscipline.Platform);
            else if (Equal(mode, "Stormwater")) ExportDiscipline(document, BoqDiscipline.Stormwater);
            else if (Equal(mode, "Sewer")) ExportDiscipline(document, BoqDiscipline.Sewer);
            else if (Equal(mode, "Water")) ExportDiscipline(document, BoqDiscipline.Water);
            else if (Equal(mode, "BulkWater")) ExportDiscipline(document, BoqDiscipline.BulkWater);
            else Build();
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_BOQBUILD",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void Build()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            BoqDiscipline discipline;
            if (!PromptDiscipline(document.Editor, out discipline)) return;

            PromptSelectionResult selection = GetSelection(
                document.Editor,
                "\nSelect design objects to include in the linked BOQ: ");
            if (selection.Status != PromptStatus.OK) return;

            double unitsPerMetre;
            if (!PromptUnitsPerMetre(document.Editor, out unitsPerMetre)) return;

            ExtractionResult extraction = ExtractSelection(
                document.Database,
                selection.Value.GetObjectIds(),
                discipline,
                unitsPerMetre);

            if (extraction.Observations.Count == 0)
            {
                WriteNoQuantities(document.Editor, "CE_BOQBUILD", extraction);
                return;
            }

            List<BoqLine> lines = Aggregate(extraction.Observations, null);
            WritePreview(document.Editor, discipline, unitsPerMetre, lines, extraction);
            if (!Confirm(document.Editor, "Create the linked BOQ table")) return;

            PromptPointResult pointResult = document.Editor.GetPoint(
                "\nPick insertion point for the linked BOQ table: ");
            if (pointResult.Status != PromptStatus.OK) return;

            Point3d position = pointResult.Value.TransformBy(
                document.Editor.CurrentUserCoordinateSystem);

            try
            {
                ObjectId tableId = CreateLinkedTable(
                    document.Database,
                    position,
                    discipline,
                    unitsPerMetre,
                    lines,
                    extraction.UsableHandles);

                document.Editor.WriteMessage(
                    "\nCE_BOQBUILD complete. BOQ items={0}; source objects={1}; rejected={2}; table={3}.",
                    lines.Count,
                    extraction.UsableHandles.Count,
                    extraction.Rejections.Count,
                    tableId.Handle);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_BOQBUILD cancelled. No linked table was committed. {0}",
                    exception.Message);
            }
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_BOQREFRESH",
            CommandFlags.Modal | CommandFlags.Redraw)]
        public void Refresh()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            PromptEntityResult result = PromptForLinkedTable(
                document.Editor,
                "\nSelect linked CE Tools BOQ table to refresh: ");
            if (result.Status != PromptStatus.OK) return;

            RefreshTable(document, result.ObjectId, true);
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_BOQINFO",
            CommandFlags.Modal | CommandFlags.Redraw)]
        public void Information()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            PromptEntityResult result = PromptForLinkedTable(
                document.Editor,
                "\nSelect linked CE Tools BOQ table for information: ");
            if (result.Status != PromptStatus.OK) return;

            try
            {
                BoqLink link;
                int existingRows;
                using (Transaction transaction =
                    document.Database.TransactionManager.StartTransaction())
                {
                    Table table = transaction.GetObject(
                        result.ObjectId,
                        OpenMode.ForRead,
                        false) as Table;
                    link = ReadLink(table, transaction);
                    existingRows = table == null ? 0 : Math.Max(0, table.Rows.Count - 2);
                }

                int resolvable = 0;
                int stale = 0;
                foreach (string handle in link.Handles)
                {
                    ObjectId id;
                    if (TryResolveHandle(document.Database, handle, out id)) resolvable++;
                    else stale++;
                }

                var columns = new List<string>
                {
                    "Property", "Value"
                };
                var rows = new List<IList<string>>
                {
                    new List<string> { "Schema", link.Schema },
                    new List<string> { "Discipline", link.Discipline.ToString() },
                    new List<string>
                    {
                        "Drawing units per metre",
                        link.UnitsPerMetre.ToString("N6", CultureInfo.CurrentCulture)
                    },
                    new List<string> { "Stored source handles", link.Handles.Count.ToString(CultureInfo.InvariantCulture) },
                    new List<string> { "Resolvable sources", resolvable.ToString(CultureInfo.InvariantCulture) },
                    new List<string> { "Stale sources", stale.ToString(CultureInfo.InvariantCulture) },
                    new List<string> { "Current BOQ rows", existingRows.ToString(CultureInfo.InvariantCulture) },
                    new List<string> { "Refresh model", "Explicit CE_BOQREFRESH" }
                };

                GridReportPresenter.ShowReportAndOfferTable(
                    document,
                    "CE Tools Linked BOQ Information",
                    "The selected table stores source handles and can be explicitly recalculated.",
                    columns,
                    rows,
                    "CE TOOLS BOQ INFORMATION");
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_BOQINFO cancelled. {0}",
                    exception.Message);
            }
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_BOQEXPORT",
            CommandFlags.Modal | CommandFlags.Redraw)]
        public void ExportLinked()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            PromptEntityResult result = PromptForLinkedTable(
                document.Editor,
                "\nSelect linked CE Tools BOQ table to export to Excel: ");
            if (result.Status != PromptStatus.OK) return;

            var refreshOptions = new PromptKeywordOptions(
                "\nRefresh linked quantities before export? [Yes/No] <Yes>: ")
            {
                AllowNone = true
            };
            refreshOptions.Keywords.Add("Yes");
            refreshOptions.Keywords.Add("No");
            PromptResult refreshResult = document.Editor.GetKeywords(refreshOptions);
            if (refreshResult.Status == PromptStatus.Cancel) return;

            bool refresh = refreshResult.Status == PromptStatus.None ||
                Equal(refreshResult.StringResult, "Yes");
            if (refresh && !RefreshTable(document, result.ObjectId, false)) return;

            string path;
            if (!PromptExcelPath(document.Editor, "CE-Tools-BOQ.xlsx", out path)) return;

            try
            {
                List<IList<string>> cells;
                string title;
                using (Transaction transaction =
                    document.Database.TransactionManager.StartTransaction())
                {
                    Table table = transaction.GetObject(
                        result.ObjectId,
                        OpenMode.ForRead,
                        false) as Table;
                    BoqLink link = ReadLink(table, transaction);
                    title = link.Discipline + " BOQ";
                    cells = ReadTableCells(table);
                }

                SimpleXlsxWriter.Write(path, title, cells);
                document.Editor.WriteMessage(
                    "\nCE_BOQEXPORT complete. Excel workbook: {0}",
                    path);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_BOQEXPORT failed. {0}",
                    exception.Message);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_BOQROAD", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void ExportRoad() { ExportDiscipline(ActiveDocument(), BoqDiscipline.Road); }

        [CommandMethod("CE_TOOLS", "CE_BOQPLATFORM", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void ExportPlatform() { ExportDiscipline(ActiveDocument(), BoqDiscipline.Platform); }

        [CommandMethod("CE_TOOLS", "CE_BOQSTORM", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void ExportStormwater() { ExportDiscipline(ActiveDocument(), BoqDiscipline.Stormwater); }

        [CommandMethod("CE_TOOLS", "CE_BOQSEWER", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void ExportSewer() { ExportDiscipline(ActiveDocument(), BoqDiscipline.Sewer); }

        [CommandMethod("CE_TOOLS", "CE_BOQWATER", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void ExportWater() { ExportDiscipline(ActiveDocument(), BoqDiscipline.Water); }

        [CommandMethod("CE_TOOLS", "CE_BOQBULKWATER", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void ExportBulkWater() { ExportDiscipline(ActiveDocument(), BoqDiscipline.BulkWater); }

        private static void ExportDiscipline(Document document, BoqDiscipline discipline)
        {
            if (document == null) return;

            PromptSelectionResult selection = GetSelection(
                document.Editor,
                "\nSelect " + DisciplineTitle(discipline) + " design objects to export: ");
            if (selection.Status != PromptStatus.OK) return;

            double unitsPerMetre;
            if (!PromptUnitsPerMetre(document.Editor, out unitsPerMetre)) return;

            ExtractionResult extraction = ExtractSelection(
                document.Database,
                selection.Value.GetObjectIds(),
                discipline,
                unitsPerMetre);
            if (extraction.Observations.Count == 0)
            {
                WriteNoQuantities(document.Editor, "CE_BOQ" + discipline, extraction);
                return;
            }

            List<BoqLine> lines = Aggregate(extraction.Observations, null);
            WritePreview(document.Editor, discipline, unitsPerMetre, lines, extraction);

            string defaultName = "CE-Tools-" + discipline + "-BOQ.xlsx";
            string path;
            if (!PromptExcelPath(document.Editor, defaultName, out path)) return;

            try
            {
                SimpleXlsxWriter.Write(
                    path,
                    discipline + " BOQ",
                    BuildExportCells(discipline, unitsPerMetre, lines));
                document.Editor.WriteMessage(
                    "\n{0} BOQ export complete. Items={1}; sources={2}; rejected={3}; workbook={4}",
                    discipline,
                    lines.Count,
                    extraction.UsableHandles.Count,
                    extraction.Rejections.Count,
                    path);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\n{0} BOQ export failed. {1}",
                    discipline,
                    exception.Message);
            }
        }

        private static bool RefreshTable(
            Document document,
            ObjectId tableId,
            bool askForConfirmation)
        {
            try
            {
                BoqLink link;
                Dictionary<string, double> existingRates;
                using (Transaction transaction =
                    document.Database.TransactionManager.StartTransaction())
                {
                    Table table = transaction.GetObject(
                        tableId,
                        OpenMode.ForRead,
                        false) as Table;
                    link = ReadLink(table, transaction);
                    existingRates = ReadRateMap(table);
                }

                var resolvedIds = new List<ObjectId>();
                int stale = 0;
                foreach (string handle in link.Handles)
                {
                    ObjectId id;
                    if (TryResolveHandle(document.Database, handle, out id))
                        resolvedIds.Add(id);
                    else
                        stale++;
                }

                ExtractionResult extraction = ExtractSelection(
                    document.Database,
                    resolvedIds,
                    link.Discipline,
                    link.UnitsPerMetre);
                if (extraction.Observations.Count == 0)
                {
                    document.Editor.WriteMessage(
                        "\nCE_BOQREFRESH stopped. No linked source currently produces a usable quantity; the existing table was left unchanged.");
                    return false;
                }

                List<BoqLine> lines = Aggregate(extraction.Observations, existingRates);
                document.Editor.WriteMessage(
                    "\nCE_BOQREFRESH preview. Items={0}; usable sources={1}; stale handles={2}; rejected sources={3}.",
                    lines.Count,
                    extraction.UsableHandles.Count,
                    stale,
                    extraction.Rejections.Count);

                if (askForConfirmation &&
                    !Confirm(document.Editor, "Replace displayed quantities while preserving matching rates"))
                {
                    return false;
                }

                using (Transaction transaction =
                    document.Database.TransactionManager.StartTransaction())
                {
                    Table table = transaction.GetObject(
                        tableId,
                        OpenMode.ForWrite,
                        false) as Table;
                    if (table == null)
                        throw new InvalidOperationException("The selected object is not an AutoCAD table.");

                    WriteTableContents(
                        document.Database,
                        table,
                        link.Discipline,
                        link.UnitsPerMetre,
                        lines);
                    WriteLink(
                        table,
                        transaction,
                        new BoqLink(
                            LinkSchema,
                            link.Discipline,
                            link.UnitsPerMetre,
                            extraction.UsableHandles));
                    table.GenerateLayout();
                    transaction.Commit();
                }

                document.Editor.WriteMessage(
                    "\nCE_BOQREFRESH complete. Items={0}; linked sources={1}; stale removed={2}.",
                    lines.Count,
                    extraction.UsableHandles.Count,
                    stale);
                return true;
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_BOQREFRESH failed. The table was not changed. {0}",
                    exception.Message);
                return false;
            }
        }

        private static ObjectId CreateLinkedTable(
            Database database,
            Point3d position,
            BoqDiscipline discipline,
            double unitsPerMetre,
            IList<BoqLine> lines,
            IList<string> handles)
        {
            if (lines == null || lines.Count == 0)
                throw new InvalidOperationException("A BOQ table cannot be populated with zero rows.");

            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTableRecord currentSpace = (BlockTableRecord)transaction.GetObject(
                    database.CurrentSpaceId,
                    OpenMode.ForWrite,
                    false);

                var table = new Table();
                table.SetDatabaseDefaults(database);
                table.TableStyle = database.Tablestyle;
                table.Position = position;
                WriteTableContents(database, table, discipline, unitsPerMetre, lines);

                currentSpace.AppendEntity(table);
                transaction.AddNewlyCreatedDBObject(table, true);
                table.CreateExtensionDictionary();
                WriteLink(
                    table,
                    transaction,
                    new BoqLink(LinkSchema, discipline, unitsPerMetre, handles));
                table.GenerateLayout();
                transaction.Commit();
                return table.ObjectId;
            }
        }

        private static void WriteTableContents(
            Database database,
            Table table,
            BoqDiscipline discipline,
            double unitsPerMetre,
            IList<BoqLine> lines)
        {
            table.SetSize(lines.Count + 2, ColumnCount);
            double height = ResolveTableTextHeight(database);
            table.SetRowHeight(height * 2.15);

            double[] widths =
            {
                height * 4.0, height * 9.0, height * 11.0, height * 22.0,
                height * 5.5, height * 8.0, height * 8.0, height * 9.0,
                height * 7.0, height * 18.0
            };
            for (int column = 0; column < ColumnCount; column++)
                table.Columns[column].Width = widths[column];

            table.MergeCells(CellRange.Create(table, 0, 0, 0, ColumnCount - 1));
            table.Cells[0, 0].TextString = string.Format(
                CultureInfo.CurrentCulture,
                "CE TOOLS {0} BILL OF QUANTITIES — {1:N6} DRAWING UNITS/M",
                discipline.ToString().ToUpperInvariant(),
                unitsPerMetre);
            table.Cells[0, 0].Alignment = CellAlignment.MiddleCenter;
            table.Cells[0, 0].TextHeight = height * 1.15;

            string[] headings =
            {
                "ITEM", "DISCIPLINE", "SECTION", "DESCRIPTION", "UNIT",
                "QUANTITY", "RATE", "AMOUNT", "SOURCE COUNT", "SOURCE / SIZE"
            };
            for (int column = 0; column < headings.Length; column++)
            {
                table.Cells[1, column].TextString = headings[column];
                table.Cells[1, column].Alignment = CellAlignment.MiddleCenter;
                table.Cells[1, column].TextHeight = height;
            }

            for (int index = 0; index < lines.Count; index++)
            {
                BoqLine line = lines[index];
                int row = index + 2;
                table.Cells[row, 0].TextString = (index + 1).ToString(CultureInfo.InvariantCulture);
                table.Cells[row, 1].TextString = line.Discipline.ToString();
                table.Cells[row, 2].TextString = line.Section;
                table.Cells[row, 3].TextString = line.Description;
                table.Cells[row, 4].TextString = line.Unit;
                table.Cells[row, 5].TextString = line.Quantity.ToString("N3", CultureInfo.CurrentCulture);
                table.Cells[row, 6].TextString = line.Rate > 0.0
                    ? line.Rate.ToString("N2", CultureInfo.CurrentCulture)
                    : string.Empty;
                table.Cells[row, 7].TextString = line.Rate > 0.0
                    ? (line.Quantity * line.Rate).ToString("N2", CultureInfo.CurrentCulture)
                    : string.Empty;
                table.Cells[row, 8].TextString = line.SourceCount.ToString(CultureInfo.InvariantCulture);
                table.Cells[row, 9].TextString = line.SourceSummary;

                for (int column = 0; column < ColumnCount; column++)
                {
                    table.Cells[row, column].Alignment =
                        column == 3 || column == 9
                            ? CellAlignment.MiddleLeft
                            : CellAlignment.MiddleCenter;
                    table.Cells[row, column].TextHeight = height;
                }
            }
        }

        private static ExtractionResult ExtractSelection(
            Database database,
            IEnumerable<ObjectId> objectIds,
            BoqDiscipline discipline,
            double unitsPerMetre)
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

                    DBObject databaseObject;
                    try
                    {
                        databaseObject = transaction.GetObject(
                            objectId,
                            OpenMode.ForRead,
                            false);
                    }
                    catch (System.Exception exception)
                    {
                        result.Rejections.Add(
                            objectId.Handle + ": could not open — " + exception.Message);
                        continue;
                    }

                    QuantityObservation observation;
                    string reason;
                    if (!TryObserve(
                        databaseObject,
                        transaction,
                        discipline,
                        unitsPerMetre,
                        out observation,
                        out reason))
                    {
                        result.Rejections.Add(
                            objectId.Handle + ": " + reason);
                        continue;
                    }

                    result.Observations.Add(observation);
                    result.UsableHandles.Add(objectId.Handle.ToString());
                }
            }

            return result;
        }

        private static bool TryObserve(
            DBObject databaseObject,
            Transaction transaction,
            BoqDiscipline discipline,
            double unitsPerMetre,
            out QuantityObservation observation,
            out string reason)
        {
            observation = null;
            reason = string.Empty;

            Entity entity = databaseObject as Entity;
            if (entity == null)
            {
                reason = "Object is not a drawable entity.";
                return false;
            }

            if (!IsFinitePositive(unitsPerMetre))
            {
                reason = "Drawing-units-per-metre value is invalid.";
                return false;
            }

            string typeName = databaseObject.GetType().Name;
            string objectName = GetObjectName(databaseObject, transaction);
            string layer = string.IsNullOrWhiteSpace(entity.Layer) ? "0" : entity.Layer;
            string search = (layer + " " + objectName + " " + typeName).ToUpperInvariant();

            QuantityKind preferred = ClassifyKind(databaseObject, search, typeName);
            double raw;
            string unit;
            double quantity;

            if (preferred == QuantityKind.Volume && TryGetVolume(databaseObject, out raw))
            {
                unit = "m³";
                quantity = raw / Math.Pow(unitsPerMetre, 3.0);
            }
            else if (preferred == QuantityKind.Area && TryGetArea(entity, out raw))
            {
                unit = "m²";
                quantity = raw / Math.Pow(unitsPerMetre, 2.0);
            }
            else if (preferred == QuantityKind.Length && TryGetLength(databaseObject, out raw))
            {
                unit = "m";
                quantity = raw / unitsPerMetre;
            }
            else if (preferred == QuantityKind.Count)
            {
                unit = "No.";
                quantity = 1.0;
            }
            else if (TryGetArea(entity, out raw))
            {
                unit = "m²";
                quantity = raw / Math.Pow(unitsPerMetre, 2.0);
            }
            else if (TryGetLength(databaseObject, out raw))
            {
                unit = "m";
                quantity = raw / unitsPerMetre;
            }
            else if (IsCountable(databaseObject, search, typeName))
            {
                unit = "No.";
                quantity = 1.0;
            }
            else
            {
                reason = "No supported length, area, volume or count quantity was found.";
                return false;
            }

            if (!IsFinitePositive(quantity))
            {
                reason = "Calculated quantity is zero, NaN or infinite.";
                return false;
            }

            string section = ClassifySection(discipline, search);
            string description = ClassifyDescription(
                discipline,
                section,
                search,
                typeName,
                objectName);
            string size = ReadSize(databaseObject, unitsPerMetre);
            string sourceSummary = layer;
            if (!string.IsNullOrWhiteSpace(size))
                sourceSummary += " | " + size;

            observation = new QuantityObservation(
                discipline,
                section,
                description,
                unit,
                quantity,
                sourceSummary);
            return true;
        }

        private static QuantityKind ClassifyKind(
            DBObject databaseObject,
            string search,
            string typeName)
        {
            if (ContainsAny(search, "VOLUME", "EARTHWORK", "CUT", "FILL") ||
                typeName.IndexOf("Solid3d", StringComparison.OrdinalIgnoreCase) >= 0)
                return QuantityKind.Volume;

            if (ContainsAny(
                search,
                "SURFACING", "ASPHALT", "PAVEMENT", "SIDEWALK", "FOOTWAY",
                "PLATFORM", "GRADING", "LAYERWORK", "SUBBASE", "BASECOURSE",
                "SUBGRADE", "PARKING", "DRIVEWAY"))
                return QuantityKind.Area;

            if (ContainsAny(
                search,
                "KERB", "CURB", "CHANNEL", "V-DRAIN", "VDRAIN", "ROAD MARK",
                "CENTERLINE", "CENTRELINE", "PIPE", "CULVERT", "MAIN", "SEWER",
                "WATER", "STORM"))
                return QuantityKind.Length;

            if (IsCountable(databaseObject, search, typeName))
                return QuantityKind.Count;

            var curve = databaseObject as Curve;
            if (curve != null && curve.Closed)
                return QuantityKind.Area;
            if (curve != null)
                return QuantityKind.Length;

            return QuantityKind.Unknown;
        }

        private static string ClassifySection(BoqDiscipline discipline, string search)
        {
            switch (discipline)
            {
                case BoqDiscipline.Road:
                    if (ContainsAny(search, "PARKING", "DRIVEWAY")) return "Parking and driveways";
                    if (ContainsAny(search, "SIDEWALK", "FOOTWAY")) return "Sidewalks";
                    if (ContainsAny(search, "KERB", "CURB", "CHANNEL")) return "Kerbs and drainage";
                    if (ContainsAny(search, "V-DRAIN", "VDRAIN")) return "V-drains";
                    if (ContainsAny(search, "MARKING")) return "Road markings";
                    if (ContainsAny(search, "SIGN")) return "Road signs";
                    if (ContainsAny(search, "LAYERWORK", "SUBBASE", "BASECOURSE", "SUBGRADE"))
                        return "Layerworks";
                    if (ContainsAny(search, "SURFACING", "ASPHALT", "PAVEMENT"))
                        return "Surfacing";
                    return "Roadworks";

                case BoqDiscipline.Platform:
                    if (ContainsAny(search, "CUT", "FILL", "EARTHWORK")) return "Earthworks";
                    if (ContainsAny(search, "LAYERWORK", "SUBBASE", "BASECOURSE", "SUBGRADE"))
                        return "Layerworks";
                    if (ContainsAny(search, "SURFACING", "PAVEMENT")) return "Surfacing";
                    return "Platform and grading";

                case BoqDiscipline.Stormwater:
                    if (ContainsAny(search, "PIPE", "CULVERT")) return "Pipes and culverts";
                    if (ContainsAny(search, "MANHOLE", "CATCHPIT", "INLET", "OUTLET", "STRUCTURE"))
                        return "Stormwater structures";
                    if (ContainsAny(search, "CHANNEL", "DRAIN")) return "Open drainage";
                    return "Stormwater";

                case BoqDiscipline.Sewer:
                    if (ContainsAny(search, "PIPE", "SEWER")) return "Sewer pipes";
                    if (ContainsAny(search, "MANHOLE", "STRUCTURE")) return "Sewer structures";
                    return "Sewer";

                case BoqDiscipline.Water:
                    if (ContainsAny(search, "PIPE", "MAIN")) return "Water pipes";
                    if (ContainsAny(search, "VALVE", "HYDRANT", "METER", "FITTING"))
                        return "Valves and fittings";
                    return "Water";

                case BoqDiscipline.BulkWater:
                    if (ContainsAny(search, "PIPE", "MAIN")) return "Bulk pipelines";
                    if (ContainsAny(search, "RESERVOIR", "TANK")) return "Storage";
                    if (ContainsAny(search, "PUMP")) return "Pump stations";
                    if (ContainsAny(search, "VALVE", "FITTING")) return "Valves and fittings";
                    return "Bulk water";

                default:
                    if (ContainsAny(search, "PIPE", "CULVERT", "MAIN")) return "Linear services";
                    if (ContainsAny(search, "SURFACING", "PAVEMENT", "PLATFORM")) return "Areas";
                    if (ContainsAny(search, "STRUCTURE", "BLOCK", "SIGN")) return "Scheduled items";
                    return "General quantities";
            }
        }

        private static string ClassifyDescription(
            BoqDiscipline discipline,
            string section,
            string search,
            string typeName,
            string objectName)
        {
            string description;
            if (ContainsAny(search, "KERB", "CURB") && ContainsAny(search, "CHANNEL"))
                description = "Kerbs and channels";
            else if (ContainsAny(search, "KERB", "CURB"))
                description = "Kerbs";
            else if (ContainsAny(search, "V-DRAIN", "VDRAIN"))
                description = "V-drains";
            else if (ContainsAny(search, "MARKING"))
                description = "Road markings";
            else if (ContainsAny(search, "SIGN"))
                description = "Road signs";
            else if (ContainsAny(search, "SIDEWALK", "FOOTWAY") &&
                     ContainsAny(search, "LAYERWORK", "SUBBASE", "BASECOURSE"))
                description = "Sidewalk layerworks";
            else if (ContainsAny(search, "SIDEWALK", "FOOTWAY"))
                description = "Sidewalk surfacing";
            else if (ContainsAny(search, "PARKING", "DRIVEWAY") &&
                     ContainsAny(search, "LAYERWORK", "SUBBASE", "BASECOURSE"))
                description = "Parking and driveway layerworks";
            else if (ContainsAny(search, "PARKING", "DRIVEWAY"))
                description = "Parking and driveway surfacing";
            else if (ContainsAny(search, "ASPHALT", "SURFACING"))
                description = "Surfacing";
            else if (ContainsAny(search, "SUBBASE"))
                description = "Subbase layer";
            else if (ContainsAny(search, "BASECOURSE", "BASE COURSE"))
                description = "Basecourse layer";
            else if (ContainsAny(search, "SUBGRADE"))
                description = "Subgrade preparation";
            else if (ContainsAny(search, "CUT"))
                description = "Cut earthworks";
            else if (ContainsAny(search, "FILL"))
                description = "Fill earthworks";
            else if (ContainsAny(search, "CULVERT"))
                description = "Culverts";
            else if (ContainsAny(search, "MANHOLE"))
                description = "Manholes";
            else if (ContainsAny(search, "CATCHPIT"))
                description = "Catchpits";
            else if (ContainsAny(search, "INLET"))
                description = "Inlets";
            else if (ContainsAny(search, "OUTLET"))
                description = "Outlets";
            else if (ContainsAny(search, "HYDRANT"))
                description = "Fire hydrants";
            else if (ContainsAny(search, "VALVE"))
                description = "Valves";
            else if (ContainsAny(search, "METER"))
                description = "Meters";
            else if (ContainsAny(search, "RESERVOIR", "TANK"))
                description = "Reservoirs / tanks";
            else if (ContainsAny(search, "PUMP"))
                description = "Pumps / pump stations";
            else if (ContainsAny(search, "PIPE", "MAIN", "SEWER", "WATER", "STORM"))
                description = DisciplineTitle(discipline) + " pipes";
            else
                description = section + " — " + FriendlyTypeName(typeName);

            if (!string.IsNullOrWhiteSpace(objectName) &&
                !Equal(objectName, typeName) &&
                objectName.Length <= 48)
            {
                description += " (" + objectName + ")";
            }

            return description;
        }

        private static bool TryGetLength(DBObject databaseObject, out double length)
        {
            length = 0.0;

            var curve = databaseObject as Curve;
            if (curve != null)
            {
                try
                {
                    double start = curve.GetDistanceAtParameter(curve.StartParam);
                    double end = curve.GetDistanceAtParameter(curve.EndParam);
                    length = Math.Abs(end - start);
                    if (IsFinitePositive(length)) return true;
                }
                catch
                {
                    // Continue to reflection-based Civil 3D properties.
                }
            }

            return TryReadDoubleProperty(
                databaseObject,
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
                // Continue to reflected area values.
            }

            return TryReadDoubleProperty(
                entity,
                out area,
                "Area2D",
                "SurfaceArea",
                "Area");
        }

        private static bool TryGetVolume(DBObject databaseObject, out double volume)
        {
            volume = 0.0;
            if (TryReadDoubleProperty(databaseObject, out volume, "Volume"))
                return true;

            try
            {
                PropertyInfo massProperties = databaseObject.GetType().GetProperty(
                    "MassProperties",
                    BindingFlags.Public | BindingFlags.Instance);
                object mass = massProperties == null
                    ? null
                    : massProperties.GetValue(databaseObject, null);
                if (mass != null &&
                    TryReadDoubleProperty(mass, out volume, "Volume"))
                    return true;
            }
            catch
            {
                return false;
            }

            return false;
        }

        private static bool TryReadDoubleProperty(
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
                    // Try the next API property.
                }
            }

            return false;
        }

        private static string ReadSize(DBObject databaseObject, double unitsPerMetre)
        {
            double raw;
            if (!TryReadDoubleProperty(
                databaseObject,
                out raw,
                "NominalDiameter",
                "InnerDiameterOrWidth",
                "Diameter",
                "OutsideDiameter"))
                return string.Empty;

            double metres = raw / unitsPerMetre;
            if (!IsFinitePositive(metres)) return string.Empty;
            return metres < 2.0
                ? (metres * 1000.0).ToString("N0", CultureInfo.CurrentCulture) + " mm"
                : metres.ToString("N3", CultureInfo.CurrentCulture) + " m";
        }

        private static string GetObjectName(DBObject databaseObject, Transaction transaction)
        {
            var block = databaseObject as BlockReference;
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
                PropertyInfo property = databaseObject.GetType().GetProperty(
                    "Name",
                    BindingFlags.Public | BindingFlags.Instance);
                object value = property == null
                    ? null
                    : property.GetValue(databaseObject, null);
                string text = value as string;
                if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
            }
            catch
            {
                // Fall back to type name.
            }

            return databaseObject.GetType().Name;
        }

        private static bool IsCountable(
            DBObject databaseObject,
            string search,
            string typeName)
        {
            if (databaseObject is BlockReference || databaseObject is DBPoint)
                return true;

            if (ContainsAny(
                search,
                "SIGN", "MANHOLE", "CATCHPIT", "INLET", "OUTLET", "STRUCTURE",
                "VALVE", "HYDRANT", "METER", "FITTING", "RESERVOIR", "TANK",
                "PUMP"))
                return true;

            return ContainsAny(
                typeName.ToUpperInvariant(),
                "STRUCTURE",
                "COGOPOINT",
                "PART");
        }

        private static List<BoqLine> Aggregate(
            IEnumerable<QuantityObservation> observations,
            IDictionary<string, double> rates)
        {
            var map = new SortedDictionary<string, BoqLine>(
                StringComparer.OrdinalIgnoreCase);

            foreach (QuantityObservation observation in observations)
            {
                string key = BuildLineKey(
                    observation.Discipline,
                    observation.Section,
                    observation.Description,
                    observation.Unit,
                    observation.SourceSummary);

                BoqLine line;
                if (!map.TryGetValue(key, out line))
                {
                    double rate = 0.0;
                    if (rates != null) rates.TryGetValue(key, out rate);
                    line = new BoqLine(
                        key,
                        observation.Discipline,
                        observation.Section,
                        observation.Description,
                        observation.Unit,
                        observation.SourceSummary,
                        rate);
                    map.Add(key, line);
                }

                line.Quantity += observation.Quantity;
                line.SourceCount++;
            }

            return map.Values.ToList();
        }

        private static Dictionary<string, double> ReadRateMap(Table table)
        {
            var rates = new Dictionary<string, double>(
                StringComparer.OrdinalIgnoreCase);
            if (table == null || table.Rows.Count <= 2 || table.Columns.Count < ColumnCount)
                return rates;

            for (int row = 2; row < table.Rows.Count; row++)
            {
                string key = BuildLineKey(
                    ParseDiscipline(GetCell(table, row, 1)),
                    GetCell(table, row, 2),
                    GetCell(table, row, 3),
                    GetCell(table, row, 4),
                    GetCell(table, row, 9));

                double rate;
                if (TryParseNumber(GetCell(table, row, 6), out rate) && rate >= 0.0)
                    rates[key] = rate;
            }

            return rates;
        }

        private static string BuildLineKey(
            BoqDiscipline discipline,
            string section,
            string description,
            string unit,
            string sourceSummary)
        {
            return string.Join(
                "|",
                discipline,
                section ?? string.Empty,
                description ?? string.Empty,
                unit ?? string.Empty,
                sourceSummary ?? string.Empty);
        }

        private static void WriteLink(
            Table table,
            Transaction transaction,
            BoqLink link)
        {
            if (table == null)
                throw new InvalidOperationException("BOQ link target is not a table.");

            if (table.ExtensionDictionary.IsNull)
                table.CreateExtensionDictionary();

            DBDictionary dictionary = transaction.GetObject(
                table.ExtensionDictionary,
                OpenMode.ForWrite,
                false) as DBDictionary;
            if (dictionary == null)
                throw new InvalidOperationException("The BOQ extension dictionary could not be opened.");

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
                new TypedValue((int)DxfCode.Text, "Schema=" + LinkSchema),
                new TypedValue((int)DxfCode.Text, "Discipline=" + link.Discipline),
                new TypedValue(
                    (int)DxfCode.Text,
                    "UnitsPerMetre=" + link.UnitsPerMetre.ToString(
                        "R",
                        CultureInfo.InvariantCulture))
            };
            foreach (string handle in link.Handles.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                values.Add(new TypedValue((int)DxfCode.Text, "Handle=" + handle));
            }

            record.Data = new ResultBuffer(values.ToArray());
        }

        private static BoqLink ReadLink(Table table, Transaction transaction)
        {
            if (table == null)
                throw new InvalidOperationException("The selected object is not an AutoCAD table.");
            if (table.ExtensionDictionary.IsNull)
                throw new InvalidOperationException("The selected table is not linked to CE Tools BOQ sources.");

            DBDictionary dictionary = transaction.GetObject(
                table.ExtensionDictionary,
                OpenMode.ForRead,
                false) as DBDictionary;
            if (dictionary == null || !dictionary.Contains(LinkRecordName))
                throw new InvalidOperationException("The selected table has no CE Tools BOQ link record.");

            Xrecord record = transaction.GetObject(
                dictionary.GetAt(LinkRecordName),
                OpenMode.ForRead,
                false) as Xrecord;
            if (record == null || record.Data == null)
                throw new InvalidOperationException("The CE Tools BOQ link record is empty.");

            string schema = string.Empty;
            BoqDiscipline discipline = BoqDiscipline.General;
            double unitsPerMetre = 1.0;
            var handles = new List<string>();

            foreach (TypedValue typedValue in record.Data)
            {
                string text = typedValue.Value as string;
                if (string.IsNullOrWhiteSpace(text)) continue;

                if (text.StartsWith("Schema=", StringComparison.OrdinalIgnoreCase))
                    schema = text.Substring("Schema=".Length);
                else if (text.StartsWith("Discipline=", StringComparison.OrdinalIgnoreCase))
                    discipline = ParseDiscipline(text.Substring("Discipline=".Length));
                else if (text.StartsWith("UnitsPerMetre=", StringComparison.OrdinalIgnoreCase))
                {
                    double parsed;
                    if (double.TryParse(
                        text.Substring("UnitsPerMetre=".Length),
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out parsed) &&
                        IsFinitePositive(parsed))
                        unitsPerMetre = parsed;
                }
                else if (text.StartsWith("Handle=", StringComparison.OrdinalIgnoreCase))
                    handles.Add(text.Substring("Handle=".Length));
            }

            if (handles.Count == 0)
                throw new InvalidOperationException("The linked BOQ contains no source handles.");

            return new BoqLink(schema, discipline, unitsPerMetre, handles);
        }

        private static bool TryResolveHandle(
            Database database,
            string handleText,
            out ObjectId objectId)
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

        private static List<IList<string>> ReadTableCells(Table table)
        {
            if (table == null)
                throw new InvalidOperationException("The selected object is not a table.");

            var rows = new List<IList<string>>();
            for (int row = 0; row < table.Rows.Count; row++)
            {
                var values = new List<string>();
                for (int column = 0; column < table.Columns.Count; column++)
                    values.Add(GetCell(table, row, column));
                rows.Add(values);
            }
            return rows;
        }

        private static List<IList<string>> BuildExportCells(
            BoqDiscipline discipline,
            double unitsPerMetre,
            IList<BoqLine> lines)
        {
            var rows = new List<IList<string>>
            {
                new List<string>
                {
                    "CE TOOLS " + discipline.ToString().ToUpperInvariant() + " BILL OF QUANTITIES",
                    string.Empty, string.Empty, string.Empty, string.Empty,
                    string.Empty, string.Empty, string.Empty, string.Empty, string.Empty
                },
                new List<string>
                {
                    "ITEM", "DISCIPLINE", "SECTION", "DESCRIPTION", "UNIT",
                    "QUANTITY", "RATE", "AMOUNT", "SOURCE COUNT", "SOURCE / SIZE"
                }
            };

            for (int index = 0; index < lines.Count; index++)
            {
                BoqLine line = lines[index];
                rows.Add(new List<string>
                {
                    (index + 1).ToString(CultureInfo.InvariantCulture),
                    line.Discipline.ToString(),
                    line.Section,
                    line.Description,
                    line.Unit,
                    line.Quantity.ToString("0.###", CultureInfo.InvariantCulture),
                    line.Rate > 0.0 ? line.Rate.ToString("0.##", CultureInfo.InvariantCulture) : string.Empty,
                    line.Rate > 0.0
                        ? (line.Quantity * line.Rate).ToString("0.##", CultureInfo.InvariantCulture)
                        : string.Empty,
                    line.SourceCount.ToString(CultureInfo.InvariantCulture),
                    line.SourceSummary
                });
            }

            rows.Add(new List<string>
            {
                "Drawing units per metre",
                unitsPerMetre.ToString("0.######", CultureInfo.InvariantCulture),
                string.Empty, string.Empty, string.Empty,
                string.Empty, string.Empty, string.Empty, string.Empty, string.Empty
            });
            return rows;
        }

        private static void WritePreview(
            Editor editor,
            BoqDiscipline discipline,
            double unitsPerMetre,
            IList<BoqLine> lines,
            ExtractionResult extraction)
        {
            editor.WriteMessage(
                "\nCE Tools BOQ preview — Discipline={0}; items={1}; usable sources={2}; rejected={3}; units/m={4:N6}.",
                discipline,
                lines.Count,
                extraction.UsableHandles.Count,
                extraction.Rejections.Count,
                unitsPerMetre);

            int shown = Math.Min(lines.Count, 20);
            for (int index = 0; index < shown; index++)
            {
                BoqLine line = lines[index];
                editor.WriteMessage(
                    "\n  {0}. {1} / {2}: {3:N3} {4} ({5} source{6})",
                    index + 1,
                    line.Section,
                    line.Description,
                    line.Quantity,
                    line.Unit,
                    line.SourceCount,
                    line.SourceCount == 1 ? string.Empty : "s");
            }
            if (lines.Count > shown)
                editor.WriteMessage("\n  ... {0} additional BOQ items.", lines.Count - shown);

            int rejectionShown = Math.Min(extraction.Rejections.Count, 8);
            for (int index = 0; index < rejectionShown; index++)
                editor.WriteMessage("\n  REJECTED: {0}", extraction.Rejections[index]);
            if (extraction.Rejections.Count > rejectionShown)
                editor.WriteMessage(
                    "\n  ... {0} additional rejected objects.",
                    extraction.Rejections.Count - rejectionShown);
        }

        private static void WriteNoQuantities(
            Editor editor,
            string command,
            ExtractionResult extraction)
        {
            editor.WriteMessage(
                "\n{0} stopped. No usable quantities were extracted; rejected objects={1}.",
                command,
                extraction.Rejections.Count);
            foreach (string reason in extraction.Rejections.Take(8))
                editor.WriteMessage("\n  REJECTED: {0}", reason);
        }

        private static PromptSelectionResult GetSelection(Editor editor, string message)
        {
            PromptSelectionResult implied = editor.SelectImplied();
            if (implied.Status == PromptStatus.OK && implied.Value.Count > 0)
            {
                editor.SetImpliedSelection(new ObjectId[0]);
                return implied;
            }

            return editor.GetSelection(
                new PromptSelectionOptions
                {
                    MessageForAdding = message,
                    AllowDuplicates = false,
                    RejectObjectsFromNonCurrentSpace = true
                });
        }

        private static PromptEntityResult PromptForLinkedTable(
            Editor editor,
            string message)
        {
            var options = new PromptEntityOptions(message);
            options.SetRejectMessage("\nSelect an AutoCAD table.");
            options.AddAllowedClass(typeof(Table), false);
            return editor.GetEntity(options);
        }

        private static bool PromptDiscipline(Editor editor, out BoqDiscipline discipline)
        {
            var options = new PromptKeywordOptions(
                "\nBOQ discipline [General/Road/Platform/Stormwater/Sewer/Water/BulkWater] <General>: ")
            {
                AllowNone = true
            };
            foreach (string keyword in new[]
            {
                "General", "Road", "Platform", "Stormwater", "Sewer", "Water", "BulkWater"
            })
                options.Keywords.Add(keyword);

            PromptResult result = editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel)
            {
                discipline = BoqDiscipline.General;
                return false;
            }

            discipline = result.Status == PromptStatus.None
                ? BoqDiscipline.General
                : ParseDiscipline(result.StringResult);
            return true;
        }

        private static bool PromptUnitsPerMetre(Editor editor, out double unitsPerMetre)
        {
            var options = new PromptDoubleOptions(
                "\nDrawing units per metre <1.0>: ")
            {
                AllowNone = true,
                AllowNegative = false,
                AllowZero = false,
                DefaultValue = 1.0,
                UseDefaultValue = true
            };
            PromptDoubleResult result = editor.GetDouble(options);
            unitsPerMetre = result.Status == PromptStatus.OK
                ? result.Value
                : 1.0;
            return result.Status == PromptStatus.OK && IsFinitePositive(unitsPerMetre);
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
                DialogCaption = "Export CE Tools Bill of Quantities",
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
            return result.Status == PromptStatus.OK &&
                Equal(result.StringResult, "Yes");
        }

        private static double ResolveTableTextHeight(Database database)
        {
            double height = database == null ? 2.0 : database.Textsize;
            if (Math.Abs(height - 1.8) < 0.05) return 1.8;
            if (Math.Abs(height - 5.0) < 0.05) return 5.0;
            return 2.0;
        }

        private static string GetCell(Table table, int row, int column)
        {
            try
            {
                return table.Cells[row, column].TextString ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static bool TryParseNumber(string text, out double value)
        {
            return double.TryParse(
                       text,
                       NumberStyles.Float | NumberStyles.AllowThousands,
                       CultureInfo.CurrentCulture,
                       out value) ||
                   double.TryParse(
                       text,
                       NumberStyles.Float | NumberStyles.AllowThousands,
                       CultureInfo.InvariantCulture,
                       out value);
        }

        private static bool ContainsAny(string source, params string[] values)
        {
            if (string.IsNullOrEmpty(source)) return false;
            foreach (string value in values)
            {
                if (source.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        private static string FriendlyTypeName(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName)) return "Design item";
            var builder = new StringBuilder(typeName.Length + 8);
            for (int index = 0; index < typeName.Length; index++)
            {
                char character = typeName[index];
                if (index > 0 &&
                    char.IsUpper(character) &&
                    !char.IsUpper(typeName[index - 1]))
                    builder.Append(' ');
                builder.Append(character);
            }
            return builder.ToString();
        }

        private static BoqDiscipline ParseDiscipline(string value)
        {
            BoqDiscipline discipline;
            return Enum.TryParse(value, true, out discipline)
                ? discipline
                : BoqDiscipline.General;
        }

        private static string DisciplineTitle(BoqDiscipline discipline)
        {
            return discipline == BoqDiscipline.BulkWater
                ? "Bulk-water"
                : discipline.ToString();
        }

        private static bool Equal(string left, string right)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsFinitePositive(double value)
        {
            return !double.IsNaN(value) &&
                !double.IsInfinity(value) &&
                value > 0.0;
        }

        private static Document ActiveDocument()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
        }

        private enum QuantityKind
        {
            Unknown,
            Length,
            Area,
            Volume,
            Count
        }

        private enum BoqDiscipline
        {
            General,
            Road,
            Platform,
            Stormwater,
            Sewer,
            Water,
            BulkWater
        }

        private sealed class QuantityObservation
        {
            public QuantityObservation(
                BoqDiscipline discipline,
                string section,
                string description,
                string unit,
                double quantity,
                string sourceSummary)
            {
                Discipline = discipline;
                Section = section ?? string.Empty;
                Description = description ?? string.Empty;
                Unit = unit ?? string.Empty;
                Quantity = quantity;
                SourceSummary = sourceSummary ?? string.Empty;
            }

            public BoqDiscipline Discipline { get; }
            public string Section { get; }
            public string Description { get; }
            public string Unit { get; }
            public double Quantity { get; }
            public string SourceSummary { get; }
        }

        private sealed class BoqLine
        {
            public BoqLine(
                string key,
                BoqDiscipline discipline,
                string section,
                string description,
                string unit,
                string sourceSummary,
                double rate)
            {
                Key = key;
                Discipline = discipline;
                Section = section;
                Description = description;
                Unit = unit;
                SourceSummary = sourceSummary;
                Rate = rate;
            }

            public string Key { get; }
            public BoqDiscipline Discipline { get; }
            public string Section { get; }
            public string Description { get; }
            public string Unit { get; }
            public string SourceSummary { get; }
            public double Quantity { get; set; }
            public int SourceCount { get; set; }
            public double Rate { get; }
        }

        private sealed class ExtractionResult
        {
            public ExtractionResult()
            {
                Observations = new List<QuantityObservation>();
                UsableHandles = new List<string>();
                Rejections = new List<string>();
            }

            public List<QuantityObservation> Observations { get; }
            public List<string> UsableHandles { get; }
            public List<string> Rejections { get; }
        }

        private sealed class BoqLink
        {
            public BoqLink(
                string schema,
                BoqDiscipline discipline,
                double unitsPerMetre,
                IEnumerable<string> handles)
            {
                Schema = string.IsNullOrWhiteSpace(schema) ? LinkSchema : schema;
                Discipline = discipline;
                UnitsPerMetre = unitsPerMetre;
                Handles = handles == null
                    ? new List<string>()
                    : handles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }

            public string Schema { get; }
            public BoqDiscipline Discipline { get; }
            public double UnitsPerMetre { get; }
            public List<string> Handles { get; }
        }
    }

    /// <summary>
    /// Minimal Open XML workbook writer. It uses only .NET Framework classes,
    /// avoiding Excel COM automation and third-party deployment dependencies.
    /// </summary>
    internal static class SimpleXlsxWriter
    {
        public static void Write(
            string path,
            string sheetName,
            IList<IList<string>> rows)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Excel path is empty.", nameof(path));
            if (rows == null || rows.Count == 0)
                throw new InvalidOperationException("Excel export contains no rows.");

            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            using (var stream = new FileStream(
                path,
                FileMode.Create,
                FileAccess.ReadWrite,
                FileShare.None))
            using (var archive = new ZipArchive(
                stream,
                ZipArchiveMode.Create,
                false,
                Encoding.UTF8))
            {
                AddText(
                    archive,
                    "[Content_Types].xml",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                    "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                    "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                    "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                    "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
                    "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
                    "</Types>");

                AddText(
                    archive,
                    "_rels/.rels",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                    "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
                    "</Relationships>");

                AddText(
                    archive,
                    "xl/workbook.xml",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" " +
                    "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
                    "<sheets><sheet name=\"" + EscapeXml(SanitizeSheetName(sheetName)) +
                    "\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");

                AddText(
                    archive,
                    "xl/_rels/workbook.xml.rels",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                    "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
                    "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
                    "</Relationships>");

                AddText(
                    archive,
                    "xl/styles.xml",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
                    "<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font>" +
                    "<font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>" +
                    "<fills count=\"1\"><fill><patternFill patternType=\"none\"/></fill></fills>" +
                    "<borders count=\"1\"><border/></borders>" +
                    "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
                    "<cellXfs count=\"2\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
                    "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/></cellXfs>" +
                    "</styleSheet>");

                AddText(
                    archive,
                    "xl/worksheets/sheet1.xml",
                    BuildWorksheet(rows));
            }
        }

        private static string BuildWorksheet(IList<IList<string>> rows)
        {
            var xml = new StringBuilder();
            xml.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            xml.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
            xml.Append("<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"2\" topLeftCell=\"A3\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>");
            xml.Append("<sheetData>");

            for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
            {
                IList<string> row = rows[rowIndex] ?? new List<string>();
                int excelRow = rowIndex + 1;
                xml.Append("<row r=\"").Append(excelRow).Append("\">");
                for (int columnIndex = 0; columnIndex < row.Count; columnIndex++)
                {
                    string reference = ColumnName(columnIndex + 1) +
                        excelRow.ToString(CultureInfo.InvariantCulture);
                    string value = row[columnIndex] ?? string.Empty;
                    bool numeric = rowIndex >= 2 &&
                        (columnIndex == 0 || columnIndex == 5 ||
                         columnIndex == 6 || columnIndex == 7 ||
                         columnIndex == 8) &&
                        IsInvariantNumber(value);

                    if (numeric)
                    {
                        xml.Append("<c r=\"").Append(reference).Append("\"><v>")
                            .Append(value.Replace(",", string.Empty))
                            .Append("</v></c>");
                    }
                    else
                    {
                        int style = rowIndex <= 1 ? 1 : 0;
                        xml.Append("<c r=\"").Append(reference)
                            .Append("\" t=\"inlineStr\" s=\"").Append(style)
                            .Append("\"><is><t xml:space=\"preserve\">")
                            .Append(EscapeXml(value))
                            .Append("</t></is></c>");
                    }
                }
                xml.Append("</row>");
            }

            xml.Append("</sheetData>");
            xml.Append("<autoFilter ref=\"A2:J")
                .Append(rows.Count.ToString(CultureInfo.InvariantCulture))
                .Append("\"/>");
            xml.Append("</worksheet>");
            return xml.ToString();
        }

        private static bool IsInvariantNumber(string value)
        {
            double number;
            return double.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out number);
        }

        private static void AddText(
            ZipArchive archive,
            string path,
            string contents)
        {
            ZipArchiveEntry entry = archive.CreateEntry(
                path,
                CompressionLevel.Optimal);
            using (Stream stream = entry.Open())
            using (var writer = new StreamWriter(
                stream,
                new UTF8Encoding(false)))
            {
                writer.Write(contents);
            }
        }

        private static string ColumnName(int column)
        {
            var name = new StringBuilder();
            int value = column;
            while (value > 0)
            {
                value--;
                name.Insert(0, (char)('A' + (value % 26)));
                value /= 26;
            }
            return name.ToString();
        }

        private static string SanitizeSheetName(string value)
        {
            string name = string.IsNullOrWhiteSpace(value)
                ? "BOQ"
                : value.Trim();
            foreach (char invalid in new[] { ':', '\\', '/', '?', '*', '[', ']' })
                name = name.Replace(invalid, '-');
            return name.Length > 31 ? name.Substring(0, 31) : name;
        }

        private static string EscapeXml(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&apos;");
        }
    }
}

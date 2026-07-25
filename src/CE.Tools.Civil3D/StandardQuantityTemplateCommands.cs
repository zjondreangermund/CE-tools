using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.StandardQuantityTemplateCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Linked office quantity templates for parking/driveway and sidewalk works.
    /// Source geometry and user-entered allowances are stored on the table and can
    /// be refreshed. Template descriptions remain editable office standards and
    /// require project-specific specification/measurement review before issue.
    /// </summary>
    public sealed class StandardQuantityTemplateCommands
    {
        private const string LinkRecordName = "CE_STANDARD_QUANTITY_TEMPLATE";
        private const string SchemaVersion = "1";

        [CommandMethod("CE_TOOLS", "CE_STANDARDQTYTOOLS", CommandFlags.Modal)]
        public void StandardQuantityTools()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            var options = new PromptKeywordOptions(
                "\nStandard quantity tools [Create/Refresh/Export/Info] <Create>: ")
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
                command = "CE_STANDARDQTYREFRESH ";
            else if (string.Equals(choice, "Export", StringComparison.OrdinalIgnoreCase))
                command = "CE_STANDARDQTYEXPORT ";
            else if (string.Equals(choice, "Info", StringComparison.OrdinalIgnoreCase))
                command = "CE_STANDARDQTYINFO ";
            else
                command = "CE_STANDARDQTY ";
            document.SendStringToExecute(command, true, false, true);
        }

        [CommandMethod("CE_TOOLS", "CE_STANDARDQTY", CommandFlags.Modal | CommandFlags.Redraw)]
        public void CreateStandardQuantitySchedule()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            Editor editor = document.Editor;

            var templateOptions = new PromptKeywordOptions(
                "\nQuantity template [ParkingDriveway/Sidewalk] <ParkingDriveway>: ")
            {
                AllowNone = true
            };
            templateOptions.Keywords.Add("ParkingDriveway");
            templateOptions.Keywords.Add("Sidewalk");
            PromptResult templateResult = editor.GetKeywords(templateOptions);
            if (templateResult.Status == PromptStatus.Cancel) return;
            StandardQuantityTemplate template =
                templateResult.Status == PromptStatus.OK &&
                string.Equals(templateResult.StringResult, "Sidewalk", StringComparison.OrdinalIgnoreCase)
                    ? StandardQuantityTemplate.Sidewalk
                    : StandardQuantityTemplate.ParkingDriveway;

            double unitsPerMetre;
            if (!PromptPositiveDouble(editor, "Drawing units per metre", 1.0, out unitsPerMetre))
                return;

            List<string> areaHandles = PromptRequiredSelection(
                editor,
                "\nSelect closed parking/sidewalk area boundaries or supported area objects: ");
            if (areaHandles.Count == 0)
            {
                editor.WriteMessage(
                    "\nCE_STANDARDQTY cancelled. No supported area sources were selected.");
                return;
            }

            var categories = new Dictionary<string, List<string>>(
                StringComparer.OrdinalIgnoreCase);
            if (template == StandardQuantityTemplate.ParkingDriveway)
            {
                categories["Kerbs"] = PromptOptionalSelection(
                    editor,
                    "Select kerb linework for this schedule",
                    "\nSelect kerb curves: ");
                categories["KerbsChannels"] = PromptOptionalSelection(
                    editor,
                    "Select kerb-and-channel linework for this schedule",
                    "\nSelect kerb-and-channel curves: ");
                categories["VDrains"] = PromptOptionalSelection(
                    editor,
                    "Select V-drain linework for this schedule",
                    "\nSelect V-drain curves: ");
                categories["Markings"] = PromptOptionalSelection(
                    editor,
                    "Select road-marking linework for this schedule",
                    "\nSelect road-marking curves: ");
            }
            else
            {
                categories["Kerbs"] = PromptOptionalSelection(
                    editor,
                    "Select sidewalk kerb/edge linework for this schedule",
                    "\nSelect sidewalk kerb/edge curves: ");
            }

            double cutVolume = 0.0;
            double fillVolume = 0.0;
            int signCount = 0;
            if (template == StandardQuantityTemplate.ParkingDriveway)
            {
                if (!PromptNonNegativeDouble(editor, "Cut volume allowance (m³)", 0.0, out cutVolume)) return;
                if (!PromptNonNegativeDouble(editor, "Fill G9 volume allowance (m³)", 0.0, out fillVolume)) return;
                if (!PromptNonNegativeInteger(editor, "Road sign count", 0, out signCount)) return;
            }

            double surfaceThickness = template == StandardQuantityTemplate.ParkingDriveway
                ? 0.080
                : 0.060;
            double sandThickness = 0.020;
            double subbaseThickness = 0.150;
            double selectedThickness = template == StandardQuantityTemplate.ParkingDriveway
                ? 0.150
                : 0.0;
            double roadbedThickness = template == StandardQuantityTemplate.ParkingDriveway
                ? 0.150
                : 0.0;

            var link = new StandardQuantityLink(
                template,
                unitsPerMetre,
                surfaceThickness,
                sandThickness,
                subbaseThickness,
                selectedThickness,
                roadbedThickness,
                cutVolume,
                fillVolume,
                signCount,
                areaHandles,
                categories);

            int rejected;
            StandardQuantityTotals totals;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                totals = ReadTotals(document.Database, transaction, link, out rejected);
            }
            if (totals.AreaSquareMetres <= 0.0)
            {
                editor.WriteMessage(
                    "\nCE_STANDARDQTY stopped. The selected area sources produced zero usable area.");
                return;
            }
            List<StandardQuantityLine> lines = BuildLines(link, totals);
            PromptPointResult insertion = editor.GetPoint(
                "\nPick insertion point for the linked standard quantity schedule: ");
            if (insertion.Status != PromptStatus.OK) return;
            AnnotationOptions annotation;
            if (!AnnotationSettingsStore.Prepare(document, false, out annotation)) return;

            var review = new List<KeyValuePair<string, string>>
            {
                Pair("Template", FriendlyTemplate(template)),
                Pair("Area", totals.AreaSquareMetres.ToString("N3", CultureInfo.CurrentCulture) + " m²"),
                Pair("Quantity lines", lines.Count.ToString(CultureInfo.InvariantCulture)),
                Pair("Rejected/unavailable sources", rejected.ToString(CultureInfo.InvariantCulture)),
                Pair("Drawing units per metre", unitsPerMetre.ToString("N6", CultureInfo.CurrentCulture)),
                Pair("Linked refresh", "Yes"),
                Pair("Measurement status", "Office template — verify project specification, thicknesses and measurement rules")
            };
            if (!PopupTablePresenter.ShowReview(
                    "CE Tools - Standard Quantity Template",
                    "The schedule calculates quantities from selected geometry and stored assumptions. It is not a substitute for project-specific BOQ specification and engineer/QS review.",
                    review,
                    "Create Schedule"))
            {
                editor.WriteMessage("\nCE_STANDARDQTY cancelled.");
                return;
            }

            ObjectId tableId = CreateLinkedTable(
                document.Database,
                insertion.Value,
                lines,
                link,
                annotation.TextHeight);
            editor.SetImpliedSelection(new[] { tableId });
            editor.Regen();
            editor.WriteMessage(
                "\nCE_STANDARDQTY complete. Template={0}; area={1:N3} m²; lines={2}; rejected={3}.",
                FriendlyTemplate(template),
                totals.AreaSquareMetres,
                lines.Count,
                rejected);
            if (PromptYesNo(editor, "Export this quantity schedule to Excel now", false))
                ExportTable(document, tableId);
        }

        [CommandMethod("CE_TOOLS", "CE_STANDARDQTYREFRESH", CommandFlags.Modal | CommandFlags.Redraw)]
        public void RefreshStandardQuantitySchedule()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            PromptEntityResult result = PromptTable(
                document.Editor,
                "\nSelect a linked CE standard quantity table: ");
            if (result.Status != PromptStatus.OK) return;
            try
            {
                int lines;
                int rejected;
                RefreshTable(document.Database, result.ObjectId, out lines, out rejected);
                document.Editor.Regen();
                document.Editor.WriteMessage(
                    "\nCE_STANDARDQTYREFRESH complete. Quantity lines={0}; rejected/unavailable sources={1}.",
                    lines,
                    rejected);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_STANDARDQTYREFRESH stopped. {0}",
                    exception.Message);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_STANDARDQTYEXPORT", CommandFlags.Modal)]
        public void ExportStandardQuantitySchedule()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            PromptEntityResult result = PromptTable(
                document.Editor,
                "\nSelect a linked CE standard quantity table to export: ");
            if (result.Status != PromptStatus.OK) return;
            ExportTable(document, result.ObjectId);
        }

        [CommandMethod("CE_TOOLS", "CE_STANDARDQTYINFO", CommandFlags.Modal | CommandFlags.Redraw)]
        public void StandardQuantityInformation()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            PromptEntityResult result = PromptTable(
                document.Editor,
                "\nSelect a linked CE standard quantity table for information: ");
            if (result.Status != PromptStatus.OK) return;
            try
            {
                StandardQuantityLink link;
                int existing;
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    Table table = transaction.GetObject(result.ObjectId, OpenMode.ForRead, false) as Table;
                    link = ReadLink(table, transaction);
                    existing = table == null ? 0 : Math.Max(0, table.Rows.Count - 2);
                }
                int rejected;
                StandardQuantityTotals totals;
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    totals = ReadTotals(document.Database, transaction, link, out rejected);
                }
                var rows = new List<KeyValuePair<string, string>>
                {
                    Pair("Template", FriendlyTemplate(link.Template)),
                    Pair("Area sources", link.AreaHandles.Count.ToString(CultureInfo.InvariantCulture)),
                    Pair("Current area", totals.AreaSquareMetres.ToString("N3", CultureInfo.CurrentCulture) + " m²"),
                    Pair("Existing table lines", existing.ToString(CultureInfo.InvariantCulture)),
                    Pair("Rejected/unavailable sources", rejected.ToString(CultureInfo.InvariantCulture)),
                    Pair("Drawing units per metre", link.UnitsPerMetre.ToString("N6", CultureInfo.CurrentCulture)),
                    Pair("Surface / sand / subbase thickness", string.Format(CultureInfo.CurrentCulture, "{0:N3} / {1:N3} / {2:N3} m", link.SurfaceThickness, link.SandThickness, link.SubbaseThickness)),
                    Pair("Selected / roadbed thickness", string.Format(CultureInfo.CurrentCulture, "{0:N3} / {1:N3} m", link.SelectedThickness, link.RoadbedThickness)),
                    Pair("Cut / fill allowances", string.Format(CultureInfo.CurrentCulture, "{0:N3} / {1:N3} m³", link.CutVolume, link.FillVolume)),
                    Pair("Road signs", link.SignCount.ToString(CultureInfo.InvariantCulture))
                };
                PopupTablePresenter.ShowReportAndOfferTable(
                    document,
                    "CE Tools - Standard Quantity Information",
                    "This table is linked to selected geometry and stored office-template assumptions.",
                    rows,
                    "CE TOOLS STANDARD QUANTITY INFORMATION");
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_STANDARDQTYINFO stopped. {0}",
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
                    int lines;
                    int rejected;
                    RefreshTable(document.Database, tableId, out lines, out rejected);
                    refreshed++;
                }
                catch
                {
                    // Continue with independent linked schedules.
                }
            }
            return refreshed;
        }

        private static StandardQuantityTotals ReadTotals(
            Database database,
            Transaction transaction,
            StandardQuantityLink link,
            out int rejected)
        {
            rejected = 0;
            var totals = new StandardQuantityTotals();
            foreach (string handle in link.AreaHandles)
            {
                ObjectId id;
                if (!TryResolveHandle(database, handle, out id))
                {
                    rejected++;
                    continue;
                }
                Entity entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                double area;
                if (entity == null || !TryGetArea(entity, out area))
                {
                    rejected++;
                    continue;
                }
                totals.AreaSquareMetres += Math.Abs(area) /
                    (link.UnitsPerMetre * link.UnitsPerMetre);
            }
            foreach (KeyValuePair<string, List<string>> category in link.CategoryHandles)
            {
                double totalLength = 0.0;
                foreach (string handle in category.Value)
                {
                    ObjectId id;
                    if (!TryResolveHandle(database, handle, out id))
                    {
                        rejected++;
                        continue;
                    }
                    Entity entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                    double length;
                    if (entity == null || !TryGetLength(entity, out length))
                    {
                        rejected++;
                        continue;
                    }
                    totalLength += Math.Abs(length) / link.UnitsPerMetre;
                }
                totals.Lengths[category.Key] = totalLength;
            }
            return totals;
        }

        private static List<StandardQuantityLine> BuildLines(
            StandardQuantityLink link,
            StandardQuantityTotals totals)
        {
            var lines = new List<StandardQuantityLine>();
            double area = totals.AreaSquareMetres;
            if (link.Template == StandardQuantityTemplate.ParkingDriveway)
            {
                lines.Add(Line("Surfacing", "80 mm 35 MPa interlocking paving", "m²", area, "Area sources"));
                lines.Add(Line("Bedding", "20 mm bedding sand", "m³", area * link.SandThickness, "Area × thickness"));
                lines.Add(Line("Layerworks", "150 mm G5 subbase at project-specified compaction", "m³", area * link.SubbaseThickness, "Area × thickness"));
                lines.Add(Line("Layerworks", "150 mm G6 selected subgrade at project-specified compaction", "m³", area * link.SelectedThickness, "Area × thickness"));
                lines.Add(Line("Layerworks", "150 mm roadbed rip and recompact", "m³", area * link.RoadbedThickness, "Area × thickness"));
                lines.Add(Line("Earthworks", "Cut", "m³", link.CutVolume, "Entered allowance"));
                lines.Add(Line("Earthworks", "Fill - G9 or project-approved material", "m³", link.FillVolume, "Entered allowance"));
                AddLengthLine(lines, totals, "Kerbs", "Ancillaries", "Kerbs");
                AddLengthLine(lines, totals, "KerbsChannels", "Drainage", "Kerbs and channels");
                AddLengthLine(lines, totals, "VDrains", "Drainage", "V-drains");
                AddLengthLine(lines, totals, "Markings", "Road furniture", "Road markings");
                lines.Add(Line("Road furniture", "Road signs", "No.", link.SignCount, "Entered count"));
            }
            else
            {
                lines.Add(Line("Surfacing", "60 mm 25 MPa brick paving", "m²", area, "Area sources"));
                lines.Add(Line("Bedding", "20 mm bedding sand", "m³", area * link.SandThickness, "Area × thickness"));
                lines.Add(Line("Layerworks", "150 mm G5 subbase at project-specified compaction", "m³", area * link.SubbaseThickness, "Area × thickness"));
                AddLengthLine(lines, totals, "Kerbs", "Edges", "Sidewalk kerb / edge restraint");
            }
            return lines;
        }

        private static void AddLengthLine(
            ICollection<StandardQuantityLine> lines,
            StandardQuantityTotals totals,
            string key,
            string section,
            string description)
        {
            double length;
            totals.Lengths.TryGetValue(key, out length);
            lines.Add(Line(section, description, "m", length, "Selected linework"));
        }

        private static StandardQuantityLine Line(
            string section,
            string description,
            string unit,
            double quantity,
            string source)
        {
            return new StandardQuantityLine(section, description, unit, quantity, source);
        }

        private static ObjectId CreateLinkedTable(
            Database database,
            Autodesk.AutoCAD.Geometry.Point3d insertion,
            IList<StandardQuantityLine> lines,
            StandardQuantityLink link,
            double textHeight)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTableRecord currentSpace = transaction.GetObject(database.CurrentSpaceId, OpenMode.ForWrite, false) as BlockTableRecord;
                if (currentSpace == null) throw new InvalidOperationException("The current drawing space could not be opened.");
                var table = new Table();
                table.SetDatabaseDefaults(database);
                table.TableStyle = database.Tablestyle;
                table.Position = insertion;
                ObjectId id = currentSpace.AppendEntity(table);
                transaction.AddNewlyCreatedDBObject(table, true);
                WriteLink(table, transaction, link);
                PopulateTable(table, lines, textHeight, link.Template);
                transaction.Commit();
                return id;
            }
        }

        private static void RefreshTable(
            Database database,
            ObjectId tableId,
            out int lineCount,
            out int rejected)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                Table table = transaction.GetObject(tableId, OpenMode.ForWrite, false) as Table;
                if (table == null) throw new InvalidOperationException("The selected object is not an AutoCAD table.");
                StandardQuantityLink link = ReadLink(table, transaction);
                StandardQuantityTotals totals = ReadTotals(database, transaction, link, out rejected);
                if (totals.AreaSquareMetres <= 0.0) throw new InvalidOperationException("The linked quantity schedule has no current usable area.");
                List<StandardQuantityLine> lines = BuildLines(link, totals);
                PopulateTable(table, lines, database.Textsize, link.Template);
                lineCount = lines.Count;
                transaction.Commit();
            }
        }

        private static void PopulateTable(
            Table table,
            IList<StandardQuantityLine> lines,
            double textHeight,
            StandardQuantityTemplate template)
        {
            const int columns = 6;
            double height = NormalizeHeight(textHeight);
            table.SetSize(lines.Count + 3, columns);
            table.SetRowHeight(Math.Max(height * 1.8, 0.001));
            table.SetColumnWidth(Math.Max(height * 8.0, 0.001));
            table.Cells[0, 0].TextString = "CE TOOLS " + FriendlyTemplate(template).ToUpperInvariant() + " STANDARD QUANTITY SCHEDULE";
            table.MergeCells(CellRange.Create(table, 0, 0, 0, columns - 1));
            table.Cells[1, 0].TextString = "OFFICE TEMPLATE — VERIFY PROJECT SPECIFICATION, THICKNESSES, COMPACTION AND MEASUREMENT RULES";
            table.MergeCells(CellRange.Create(table, 1, 0, 1, columns - 1));
            string[] headings = { "ITEM", "SECTION", "DESCRIPTION", "UNIT", "QUANTITY", "SOURCE / BASIS" };
            for (int column = 0; column < columns; column++)
            {
                table.Cells[2, column].TextString = headings[column];
                table.Cells[2, column].TextHeight = height;
            }
            for (int index = 0; index < lines.Count; index++)
            {
                StandardQuantityLine line = lines[index];
                int row = index + 3;
                string[] values =
                {
                    (index + 1).ToString(CultureInfo.InvariantCulture),
                    line.Section,
                    line.Description,
                    line.Unit,
                    line.Quantity.ToString("N3", CultureInfo.CurrentCulture),
                    line.Source
                };
                for (int column = 0; column < columns; column++)
                {
                    table.Cells[row, column].TextString = values[column];
                    table.Cells[row, column].TextHeight = height;
                }
            }
            table.GenerateLayout();
        }

        private static void WriteLink(Table table, Transaction transaction, StandardQuantityLink link)
        {
            if (table.ExtensionDictionary.IsNull) table.CreateExtensionDictionary();
            DBDictionary dictionary = transaction.GetObject(table.ExtensionDictionary, OpenMode.ForWrite, false) as DBDictionary;
            if (dictionary == null) throw new InvalidOperationException("The table extension dictionary could not be opened.");
            Xrecord record;
            if (dictionary.Contains(LinkRecordName))
                record = transaction.GetObject(dictionary.GetAt(LinkRecordName), OpenMode.ForWrite, false) as Xrecord;
            else
            {
                record = new Xrecord();
                dictionary.SetAt(LinkRecordName, record);
                transaction.AddNewlyCreatedDBObject(record, true);
            }
            var values = new List<TypedValue>
            {
                Text("Schema", SchemaVersion),
                Text("Template", link.Template.ToString()),
                Text("UnitsPerMetre", link.UnitsPerMetre.ToString("R", CultureInfo.InvariantCulture)),
                Text("SurfaceThickness", link.SurfaceThickness.ToString("R", CultureInfo.InvariantCulture)),
                Text("SandThickness", link.SandThickness.ToString("R", CultureInfo.InvariantCulture)),
                Text("SubbaseThickness", link.SubbaseThickness.ToString("R", CultureInfo.InvariantCulture)),
                Text("SelectedThickness", link.SelectedThickness.ToString("R", CultureInfo.InvariantCulture)),
                Text("RoadbedThickness", link.RoadbedThickness.ToString("R", CultureInfo.InvariantCulture)),
                Text("Cut", link.CutVolume.ToString("R", CultureInfo.InvariantCulture)),
                Text("Fill", link.FillVolume.ToString("R", CultureInfo.InvariantCulture)),
                Text("Signs", link.SignCount.ToString(CultureInfo.InvariantCulture))
            };
            foreach (string handle in link.AreaHandles)
                values.Add(Text("Area", handle));
            foreach (KeyValuePair<string, List<string>> category in link.CategoryHandles)
                foreach (string handle in category.Value)
                    values.Add(Text("Category", category.Key + "|" + handle));
            record.Data = new ResultBuffer(values.ToArray());
        }

        private static TypedValue Text(string key, string value)
        {
            return new TypedValue((int)DxfCode.Text, key + "=" + value);
        }

        private static StandardQuantityLink ReadLink(Table table, Transaction transaction)
        {
            if (table == null || table.ExtensionDictionary.IsNull)
                throw new InvalidOperationException("The selected table is not a linked CE standard quantity schedule.");
            DBDictionary dictionary = transaction.GetObject(table.ExtensionDictionary, OpenMode.ForRead, false) as DBDictionary;
            if (dictionary == null || !dictionary.Contains(LinkRecordName))
                throw new InvalidOperationException("The selected table has no CE standard quantity link record.");
            Xrecord record = transaction.GetObject(dictionary.GetAt(LinkRecordName), OpenMode.ForRead, false) as Xrecord;
            if (record == null || record.Data == null) throw new InvalidOperationException("The standard quantity link record is empty.");

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var areas = new List<string>();
            var categories = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (TypedValue value in record.Data)
            {
                string text = value.Value as string;
                if (string.IsNullOrWhiteSpace(text)) continue;
                int equals = text.IndexOf('=');
                if (equals <= 0) continue;
                string key = text.Substring(0, equals);
                string raw = text.Substring(equals + 1);
                if (string.Equals(key, "Area", StringComparison.OrdinalIgnoreCase)) areas.Add(raw);
                else if (string.Equals(key, "Category", StringComparison.OrdinalIgnoreCase))
                {
                    int pipe = raw.IndexOf('|');
                    if (pipe <= 0) continue;
                    string category = raw.Substring(0, pipe);
                    string handle = raw.Substring(pipe + 1);
                    List<string> list;
                    if (!categories.TryGetValue(category, out list))
                    {
                        list = new List<string>();
                        categories[category] = list;
                    }
                    list.Add(handle);
                }
                else values[key] = raw;
            }
            StandardQuantityTemplate template;
            if (!Enum.TryParse(Value(values, "Template"), true, out template))
                template = StandardQuantityTemplate.ParkingDriveway;
            return new StandardQuantityLink(
                template,
                ParseDouble(values, "UnitsPerMetre", 1.0),
                ParseDouble(values, "SurfaceThickness", template == StandardQuantityTemplate.ParkingDriveway ? 0.080 : 0.060),
                ParseDouble(values, "SandThickness", 0.020),
                ParseDouble(values, "SubbaseThickness", 0.150),
                ParseDouble(values, "SelectedThickness", template == StandardQuantityTemplate.ParkingDriveway ? 0.150 : 0.0),
                ParseDouble(values, "RoadbedThickness", template == StandardQuantityTemplate.ParkingDriveway ? 0.150 : 0.0),
                ParseDouble(values, "Cut", 0.0),
                ParseDouble(values, "Fill", 0.0),
                ParseInteger(values, "Signs", 0),
                areas,
                categories);
        }

        private static void ExportTable(Document document, ObjectId tableId)
        {
            Editor editor = document.Editor;
            try
            {
                int lines;
                int rejected;
                RefreshTable(document.Database, tableId, out lines, out rejected);
                IList<IList<string>> cells;
                StandardQuantityTemplate template;
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    Table table = transaction.GetObject(tableId, OpenMode.ForRead, false) as Table;
                    template = ReadLink(table, transaction).Template;
                    cells = ReadCells(table);
                }
                var options = new PromptSaveFileOptions("\nSelect standard quantity Excel workbook path: ")
                {
                    Filter = "Excel Workbook (*.xlsx)|*.xlsx",
                    DialogCaption = "Export CE Tools Standard Quantity Schedule",
                    InitialFileName = "CE-" + template + "-Quantities.xlsx"
                };
                PromptFileNameResult result = editor.GetFileNameForSave(options);
                if (result.Status != PromptStatus.OK) return;
                string path = result.StringResult;
                if (!path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)) path += ".xlsx";
                SimpleXlsxWriter.Write(path, "Standard Quantities", cells);
                editor.WriteMessage(
                    "\nCE_STANDARDQTYEXPORT complete. Lines={0}; rejected={1}; file={2}",
                    lines,
                    rejected,
                    path);
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage("\nCE_STANDARDQTYEXPORT stopped. {0}", exception.Message);
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

        private static List<ObjectId> FindLinkedTables(Database database)
        {
            var result = new List<ObjectId>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTableRecord currentSpace = transaction.GetObject(database.CurrentSpaceId, OpenMode.ForRead, false) as BlockTableRecord;
                if (currentSpace == null) return result;
                foreach (ObjectId id in currentSpace)
                {
                    Table table = transaction.GetObject(id, OpenMode.ForRead, false) as Table;
                    if (table == null || table.ExtensionDictionary.IsNull) continue;
                    DBDictionary dictionary = transaction.GetObject(table.ExtensionDictionary, OpenMode.ForRead, false) as DBDictionary;
                    if (dictionary != null && dictionary.Contains(LinkRecordName)) result.Add(id);
                }
            }
            return result;
        }

        private static List<string> PromptRequiredSelection(Editor editor, string message)
        {
            PromptSelectionResult result = editor.GetSelection(new PromptSelectionOptions
            {
                MessageForAdding = message,
                AllowDuplicates = false,
                RejectObjectsFromNonCurrentSpace = true
            });
            return result.Status == PromptStatus.OK
                ? result.Value.GetObjectIds().Select(id => id.Handle.ToString()).ToList()
                : new List<string>();
        }

        private static List<string> PromptOptionalSelection(Editor editor, string question, string message)
        {
            if (!PromptYesNo(editor, question, false)) return new List<string>();
            return PromptRequiredSelection(editor, message);
        }

        private static PromptEntityResult PromptTable(Editor editor, string message)
        {
            var options = new PromptEntityOptions(message);
            options.SetRejectMessage("\nSelect an AutoCAD table.");
            options.AddAllowedClass(typeof(Table), false);
            return editor.GetEntity(options);
        }

        private static bool TryGetArea(Entity entity, out double area)
        {
            area = 0.0;
            Polyline polyline = entity as Polyline;
            if (polyline != null && polyline.Closed)
            {
                area = polyline.Area;
                return Math.Abs(area) > 0.0;
            }
            Circle circle = entity as Circle;
            if (circle != null)
            {
                area = Math.PI * circle.Radius * circle.Radius;
                return area > 0.0;
            }
            Region region = entity as Region;
            if (region != null)
            {
                area = region.Area;
                return Math.Abs(area) > 0.0;
            }
            Hatch hatch = entity as Hatch;
            if (hatch != null)
            {
                try
                {
                    area = hatch.Area;
                    return Math.Abs(area) > 0.0;
                }
                catch
                {
                    return false;
                }
            }
            return TryReadDoubleProperty(entity, "Area", out area) && Math.Abs(area) > 0.0;
        }

        private static bool TryGetLength(Entity entity, out double length)
        {
            length = 0.0;
            Curve curve = entity as Curve;
            if (curve != null)
            {
                try
                {
                    length = curve.GetDistanceAtParameter(curve.EndParam) -
                        curve.GetDistanceAtParameter(curve.StartParam);
                    return Math.Abs(length) > 0.0;
                }
                catch
                {
                    // Reflection fallback below.
                }
            }
            return TryReadDoubleProperty(entity, "Length", out length) && Math.Abs(length) > 0.0;
        }

        private static bool TryReadDoubleProperty(object value, string name, out double result)
        {
            result = 0.0;
            if (value == null) return false;
            try
            {
                PropertyInfo property = value.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (property == null || property.GetIndexParameters().Length != 0) return false;
                object raw = property.GetValue(value, null);
                if (raw == null) return false;
                result = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
                return !double.IsNaN(result) && !double.IsInfinity(result);
            }
            catch
            {
                return false;
            }
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

        private static bool PromptPositiveDouble(Editor editor, string name, double defaultValue, out double value)
        {
            var options = new PromptDoubleOptions("\n" + name + " <" + defaultValue.ToString("0.###", CultureInfo.InvariantCulture) + ">: ")
            {
                AllowNone = true,
                AllowNegative = false,
                AllowZero = false,
                DefaultValue = defaultValue,
                UseDefaultValue = true
            };
            PromptDoubleResult result = editor.GetDouble(options);
            value = result.Status == PromptStatus.OK ? result.Value : defaultValue;
            return result.Status == PromptStatus.OK;
        }

        private static bool PromptNonNegativeDouble(Editor editor, string name, double defaultValue, out double value)
        {
            var options = new PromptDoubleOptions("\n" + name + " <" + defaultValue.ToString("0.###", CultureInfo.InvariantCulture) + ">: ")
            {
                AllowNone = true,
                AllowNegative = false,
                AllowZero = true,
                DefaultValue = defaultValue,
                UseDefaultValue = true
            };
            PromptDoubleResult result = editor.GetDouble(options);
            value = result.Status == PromptStatus.OK ? result.Value : defaultValue;
            return result.Status == PromptStatus.OK;
        }

        private static bool PromptNonNegativeInteger(Editor editor, string name, int defaultValue, out int value)
        {
            var options = new PromptIntegerOptions("\n" + name + " <" + defaultValue.ToString(CultureInfo.InvariantCulture) + ">: ")
            {
                AllowNone = true,
                AllowNegative = false,
                AllowZero = true,
                DefaultValue = defaultValue,
                UseDefaultValue = true
            };
            PromptIntegerResult result = editor.GetInteger(options);
            value = result.Status == PromptStatus.OK ? result.Value : defaultValue;
            return result.Status == PromptStatus.OK;
        }

        private static bool PromptYesNo(Editor editor, string message, bool defaultValue)
        {
            var options = new PromptKeywordOptions("\n" + message + " [Yes/No] <" + (defaultValue ? "Yes" : "No") + ">: ")
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

        private static double ParseDouble(IDictionary<string, string> values, string key, double fallback)
        {
            double value;
            return double.TryParse(Value(values, key), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                ? value
                : fallback;
        }

        private static int ParseInteger(IDictionary<string, string> values, string key, int fallback)
        {
            int value;
            return int.TryParse(Value(values, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
                ? value
                : fallback;
        }

        private static string Value(IDictionary<string, string> values, string key)
        {
            string value;
            return values.TryGetValue(key, out value) ? value : string.Empty;
        }

        private static string FriendlyTemplate(StandardQuantityTemplate template)
        {
            return template == StandardQuantityTemplate.ParkingDriveway
                ? "Parking / Driveway"
                : "Sidewalk";
        }

        private static double NormalizeHeight(double value)
        {
            if (Math.Abs(value - 1.8) < 0.05) return 1.8;
            if (Math.Abs(value - 5.0) < 0.05) return 5.0;
            return 2.0;
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

    internal enum StandardQuantityTemplate
    {
        ParkingDriveway,
        Sidewalk
    }

    internal sealed class StandardQuantityLink
    {
        public StandardQuantityLink(
            StandardQuantityTemplate template,
            double unitsPerMetre,
            double surfaceThickness,
            double sandThickness,
            double subbaseThickness,
            double selectedThickness,
            double roadbedThickness,
            double cutVolume,
            double fillVolume,
            int signCount,
            IEnumerable<string> areaHandles,
            IDictionary<string, List<string>> categoryHandles)
        {
            Template = template;
            UnitsPerMetre = unitsPerMetre;
            SurfaceThickness = surfaceThickness;
            SandThickness = sandThickness;
            SubbaseThickness = subbaseThickness;
            SelectedThickness = selectedThickness;
            RoadbedThickness = roadbedThickness;
            CutVolume = cutVolume;
            FillVolume = fillVolume;
            SignCount = signCount;
            AreaHandles = areaHandles == null
                ? new List<string>()
                : areaHandles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            CategoryHandles = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (categoryHandles != null)
            {
                foreach (KeyValuePair<string, List<string>> item in categoryHandles)
                {
                    CategoryHandles[item.Key] = item.Value == null
                        ? new List<string>()
                        : item.Value.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                }
            }
        }

        public StandardQuantityTemplate Template { get; private set; }
        public double UnitsPerMetre { get; private set; }
        public double SurfaceThickness { get; private set; }
        public double SandThickness { get; private set; }
        public double SubbaseThickness { get; private set; }
        public double SelectedThickness { get; private set; }
        public double RoadbedThickness { get; private set; }
        public double CutVolume { get; private set; }
        public double FillVolume { get; private set; }
        public int SignCount { get; private set; }
        public List<string> AreaHandles { get; private set; }
        public Dictionary<string, List<string>> CategoryHandles { get; private set; }
    }

    internal sealed class StandardQuantityTotals
    {
        public StandardQuantityTotals()
        {
            Lengths = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        }

        public double AreaSquareMetres { get; set; }
        public Dictionary<string, double> Lengths { get; private set; }
    }

    internal sealed class StandardQuantityLine
    {
        public StandardQuantityLine(
            string section,
            string description,
            string unit,
            double quantity,
            string source)
        {
            Section = section;
            Description = description;
            Unit = unit;
            Quantity = quantity;
            Source = source;
        }

        public string Section { get; private set; }
        public string Description { get; private set; }
        public string Unit { get; private set; }
        public double Quantity { get; private set; }
        public string Source { get; private set; }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Windows;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.WaterSewerCostEstimateCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Preserves the client's water/sewer estimate workbook and refreshes only
    /// model-derived quantities. Existing rates, formatting, page layout and
    /// formulas remain editable in Excel.
    /// </summary>
    public sealed class WaterSewerCostEstimateCommands
    {
        private const string RecordName = "CE_WATER_SEWER_COST_ESTIMATE";
        private const string Schema = "1";

        [CommandMethod("CE_TOOLS", "CE_WSCOSTTOOLS", CommandFlags.Modal | CommandFlags.Redraw)]
        public void Tools()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            DisciplineWorkflowDialogs.SelectAndRun(
                document,
                "CE Tools - Water and Sewer Cost Estimate",
                "Create and maintain an Excel estimate linked to current drawing quantities.",
                new List<DisciplineWorkflowAction>
                {
                    new DisciplineWorkflowAction("Create linked estimate", "CE_WSCOSTCREATE", "Copy the approved template and populate model-derived quantities.", "01 Estimate"),
                    new DisciplineWorkflowAction("Refresh estimate", "CE_WSCOSTREFRESH", "Update quantities while preserving workbook rates and formatting.", "01 Estimate"),
                    new DisciplineWorkflowAction("Estimate information", "CE_WSCOSTINFO", "Inspect workbook linkage, source counts and automatic-refresh state.", "02 Review"),
                    new DisciplineWorkflowAction("Automatic refresh", "CE_WSCOSTAUTO", "Configure deferred workbook updates after drawing changes.", "03 Automation")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_WSCOSTCREATE", CommandFlags.Modal | CommandFlags.Redraw)]
        public void Create()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            string template = FindInstalledTemplate();
            if (string.IsNullOrWhiteSpace(template) || !File.Exists(template))
            {
                var open = new OpenFileDialog(
                    "Select CE Tools water and sewer cost-estimate template",
                    string.Empty,
                    "xlsx",
                    "CE_WSCOSTCREATE",
                    OpenFileDialog.OpenFileDialogFlags.NoUrls);
                if (open.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                template = open.Filename;
            }

            string outputExtension = string.Equals(
                Path.GetExtension(template),
                ".xlsm",
                StringComparison.OrdinalIgnoreCase) ? "xlsm" : "xlsx";
            var save = new SaveFileDialog(
                "Create linked water and sewer cost estimate",
                DefaultOutputPath(document, outputExtension),
                outputExtension,
                "CE_WSCOSTCREATE",
                SaveFileDialog.SaveFileDialogFlags.DoNotTransferRemoteFiles);
            if (save.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

            double unitsPerMetre;
            if (!PromptUnitsPerMetre(document.Editor, out unitsPerMetre)) return;

            try
            {
                File.Copy(template, save.Filename, true);
                CostEstimateSnapshot snapshot = CostEstimateCollector.Read(
                    document.Database,
                    unitsPerMetre);
                WaterSewerWorkbookUpdater.Update(save.Filename, snapshot);
                WriteLink(document.Database, new CostEstimateLink(
                    Schema,
                    save.Filename,
                    unitsPerMetre,
                    true));
                document.Editor.WriteMessage(
                    "\nCE_WSCOSTCREATE complete. Water assets={0}; sewer assets={1}; workbook={2}",
                    snapshot.WaterSourceCount,
                    snapshot.SewerSourceCount,
                    save.Filename);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_WSCOSTCREATE failed. {0}",
                    exception.Message);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_WSCOSTREFRESH", CommandFlags.Modal | CommandFlags.Redraw)]
        public void Refresh()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            int refreshed = RefreshAll(document, true);
            if (refreshed == 0)
                document.Editor.WriteMessage(
                    "\nCE_WSCOSTREFRESH stopped. Create a linked estimate first with CE_WSCOSTCREATE.");
        }

        [CommandMethod("CE_TOOLS", "CE_WSCOSTINFO", CommandFlags.Modal | CommandFlags.Redraw)]
        public void Information()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            CostEstimateLink link = ReadLink(document.Database);
            if (link == null)
            {
                document.Editor.WriteMessage("\nNo linked water/sewer cost estimate is stored.");
                return;
            }
            CostEstimateSnapshot snapshot = CostEstimateCollector.Read(
                document.Database,
                link.UnitsPerMetre);
            var rows = new List<KeyValuePair<string, string>>
            {
                Pair("Workbook", link.Path),
                Pair("Workbook exists", File.Exists(link.Path) ? "Yes" : "No"),
                Pair("Automatic refresh", link.Automatic ? "On" : "Off"),
                Pair("Drawing units per metre", link.UnitsPerMetre.ToString("N6", CultureInfo.CurrentCulture)),
                Pair("Water design assets", snapshot.WaterSourceCount.ToString(CultureInfo.InvariantCulture)),
                Pair("Sewer design assets", snapshot.SewerSourceCount.ToString(CultureInfo.InvariantCulture)),
                Pair("Water pipe length", snapshot.WaterLength.ToString("N2", CultureInfo.CurrentCulture) + " m"),
                Pair("Sewer pipe length", snapshot.SewerLength.ToString("N2", CultureInfo.CurrentCulture) + " m"),
                Pair("Refresh command", "CE_WSCOSTREFRESH / CE_REFRESHALL")
            };
            PopupTablePresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - Water & Sewer Cost Estimate",
                "Quantities are linked to current drawing assets; workbook rates remain user-editable.",
                rows,
                "CE TOOLS WATER AND SEWER COST ESTIMATE");
        }

        [CommandMethod("CE_TOOLS", "CE_WSCOSTAUTO", CommandFlags.Modal)]
        public void ToggleAuto()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            CostEstimateLink link = ReadLink(document.Database);
            if (link == null)
            {
                document.Editor.WriteMessage(
                    "\nCE_WSCOSTAUTO stopped. Create a linked estimate first.");
                return;
            }
            var options = new PromptKeywordOptions(
                "\nAutomatic water/sewer estimate refresh [On/Off] <" +
                (link.Automatic ? "On" : "Off") + ">: ")
            {
                AllowNone = true
            };
            options.Keywords.Add("On");
            options.Keywords.Add("Off");
            PromptResult result = document.Editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel) return;
            bool enabled = result.Status == PromptStatus.None
                ? link.Automatic
                : Equal(result.StringResult, "On");
            WriteLink(document.Database, new CostEstimateLink(
                link.Schema, link.Path, link.UnitsPerMetre, enabled));
            document.Editor.WriteMessage(
                "\nAutomatic water/sewer cost-estimate refresh is {0}.",
                enabled ? "ON" : "OFF");
        }

        internal static int RefreshAll(Document document)
        {
            return RefreshAll(document, false);
        }

        internal static int RefreshAll(Document document, bool report)
        {
            if (document == null) return 0;
            CostEstimateLink link = ReadLink(document.Database);
            if (link == null || string.IsNullOrWhiteSpace(link.Path)) return 0;
            if (!File.Exists(link.Path))
            {
                if (report)
                    document.Editor.WriteMessage(
                        "\nLinked cost-estimate workbook was not found: {0}",
                        link.Path);
                return 0;
            }
            try
            {
                CostEstimateSnapshot snapshot = CostEstimateCollector.Read(
                    document.Database,
                    link.UnitsPerMetre);
                WaterSewerWorkbookUpdater.Update(link.Path, snapshot);
                if (report)
                    document.Editor.WriteMessage(
                        "\nCE_WSCOSTREFRESH complete. Water length={0:N2} m; sewer length={1:N2} m; workbook={2}",
                        snapshot.WaterLength,
                        snapshot.SewerLength,
                        link.Path);
                return 1;
            }
            catch (System.Exception exception)
            {
                if (report)
                    document.Editor.WriteMessage(
                        "\nCE_WSCOSTREFRESH failed; workbook was left recoverable. {0}",
                        exception.Message);
                return 0;
            }
        }

        internal static bool IsAutomatic(Database database)
        {
            CostEstimateLink link = ReadLink(database);
            return link != null && link.Automatic && File.Exists(link.Path);
        }

        private static string FindInstalledTemplate()
        {
            string selectedTemplate = CostEstimateTemplateStore.Read();
            if (!string.IsNullOrWhiteSpace(selectedTemplate) && File.Exists(selectedTemplate))
                return selectedTemplate;
            string assembly = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string[] candidates =
            {
                Path.Combine(assembly ?? string.Empty, "Templates", "CE TOOLS - COST ESTIMATE WATER & SEWER - FORMAT.xlsx"),
                Path.Combine(assembly ?? string.Empty, "..", "..", "Templates", "CE TOOLS - COST ESTIMATE WATER & SEWER - FORMAT.xlsx"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CE Tools", "Templates", "CE TOOLS - COST ESTIMATE WATER & SEWER - FORMAT.xlsx"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CE Tools", "Templates", "CE TOOLS - COST ESTIMATE WATER & SEWER - FORMAT.xlsx")
            };
            return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
        }

        private static string DefaultOutputPath(Document document, string extension)
        {
            string drawing = document == null ? string.Empty : document.Name;
            string folder = string.IsNullOrWhiteSpace(drawing)
                ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                : Path.GetDirectoryName(drawing);
            if (string.IsNullOrWhiteSpace(folder))
                folder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string suffix = string.Equals(extension, "xlsm", StringComparison.OrdinalIgnoreCase) ? ".xlsm" : ".xlsx";
            string name = string.IsNullOrWhiteSpace(drawing)
                ? "CE Tools - Water & Sewer Cost Estimate" + suffix
                : Path.GetFileNameWithoutExtension(drawing) + " - Water & Sewer Cost Estimate" + suffix;
            return Path.Combine(folder, name);
        }

        private static bool PromptUnitsPerMetre(Editor editor, out double value)
        {
            value = 1.0;
            var options = new PromptDoubleOptions(
                "\nDrawing units per metre <1>: ")
            {
                AllowNone = true,
                AllowNegative = false,
                AllowZero = false,
                DefaultValue = 1.0,
                UseDefaultValue = true
            };
            PromptDoubleResult result = editor.GetDouble(options);
            if (result.Status == PromptStatus.Cancel) return false;
            value = result.Status == PromptStatus.None ? 1.0 : result.Value;
            return value > 0.0 && !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static CostEstimateLink ReadLink(Database database)
        {
            if (database == null) return null;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                DBDictionary dictionary = transaction.GetObject(
                    database.NamedObjectsDictionaryId,
                    OpenMode.ForRead,
                    false) as DBDictionary;
                if (dictionary == null || !dictionary.Contains(RecordName)) return null;
                Xrecord record = transaction.GetObject(
                    dictionary.GetAt(RecordName),
                    OpenMode.ForRead,
                    false) as Xrecord;
                string[] fields = record == null || record.Data == null
                    ? new string[0]
                    : record.Data.AsArray()
                        .Where(item => item.TypeCode == (int)DxfCode.Text)
                        .Select(item => Convert.ToString(item.Value, CultureInfo.InvariantCulture))
                        .ToArray();
                double units;
                if (fields.Length < 4 ||
                    !double.TryParse(fields[2], NumberStyles.Float, CultureInfo.InvariantCulture, out units))
                    return null;
                return new CostEstimateLink(
                    fields[0],
                    fields[1],
                    units,
                    string.Equals(fields[3], "1", StringComparison.Ordinal));
            }
        }

        private static void WriteLink(Database database, CostEstimateLink link)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                DBDictionary dictionary = transaction.GetObject(
                    database.NamedObjectsDictionaryId,
                    OpenMode.ForWrite,
                    false) as DBDictionary;
                Xrecord record;
                if (dictionary.Contains(RecordName))
                {
                    record = transaction.GetObject(
                        dictionary.GetAt(RecordName),
                        OpenMode.ForWrite,
                        false) as Xrecord;
                }
                else
                {
                    record = new Xrecord();
                    dictionary.SetAt(RecordName, record);
                    transaction.AddNewlyCreatedDBObject(record, true);
                }
                record.Data = new ResultBuffer(
                    new TypedValue((int)DxfCode.Text, link.Schema),
                    new TypedValue((int)DxfCode.Text, link.Path),
                    new TypedValue((int)DxfCode.Text, link.UnitsPerMetre.ToString("R", CultureInfo.InvariantCulture)),
                    new TypedValue((int)DxfCode.Text, link.Automatic ? "1" : "0"));
                transaction.Commit();
            }
        }

        private static KeyValuePair<string, string> Pair(string key, string value)
        {
            return new KeyValuePair<string, string>(key, value ?? string.Empty);
        }

        private static bool Equal(string left, string right)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        private static Document ActiveDocument()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
        }

        private sealed class CostEstimateLink
        {
            public CostEstimateLink(string schema, string path, double unitsPerMetre, bool automatic)
            {
                Schema = schema;
                Path = path;
                UnitsPerMetre = unitsPerMetre;
                Automatic = automatic;
            }
            public string Schema { get; }
            public string Path { get; }
            public double UnitsPerMetre { get; }
            public bool Automatic { get; }
        }
    }

    internal sealed class CostEstimateSnapshot
    {
        private readonly Dictionary<int, double> _rows = new Dictionary<int, double>();
        public int WaterSourceCount { get; set; }
        public int SewerSourceCount { get; set; }
        public double WaterLength { get; set; }
        public double SewerLength { get; set; }
        public IDictionary<int, double> Rows { get { return _rows; } }
        public void Add(int row, double quantity)
        {
            double current;
            _rows.TryGetValue(row, out current);
            _rows[row] = current + Math.Max(0.0, quantity);
        }
        public double Read(int row)
        {
            double value;
            return _rows.TryGetValue(row, out value) ? value : 0.0;
        }
        public void Set(int row, double quantity)
        {
            _rows[row] = Math.Max(0.0, quantity);
        }
    }

    internal static class CostEstimateCollector
    {
        public static CostEstimateSnapshot Read(Database database, double unitsPerMetre)
        {
            var snapshot = new CostEstimateSnapshot();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTable blocks = transaction.GetObject(
                    database.BlockTableId,
                    OpenMode.ForRead,
                    false) as BlockTable;
                if (blocks == null) return snapshot;
                foreach (ObjectId blockId in blocks)
                {
                    BlockTableRecord block = transaction.GetObject(
                        blockId,
                        OpenMode.ForRead,
                        false) as BlockTableRecord;
                    if (block == null || block.IsFromExternalReference || block.IsDependent) continue;
                    foreach (ObjectId id in block)
                    {
                        DBObject value;
                        try { value = transaction.GetObject(id, OpenMode.ForRead, false); }
                        catch { continue; }
                        ReadObject(value, transaction, unitsPerMetre, snapshot);
                    }
                }
            }
            ApplyDerivedQuantities(snapshot);
            return snapshot;
        }

        private static void ReadObject(
            DBObject value,
            Transaction transaction,
            double unitsPerMetre,
            CostEstimateSnapshot snapshot)
        {
            Entity entity = value as Entity;
            if (entity == null) return;
            string type = value.GetType().Name;
            string name = ReadString(value, "Name");
            string description = First(
                ReadString(value, "Description"),
                ReadString(value, "RawDescription"),
                ReadString(value, "PartDescription"),
                ReadString(value, "PartSizeName"));
            string owner = ReadOwnerName(value, transaction);
            string search = (type + " " + name + " " + description + " " + owner + " " + entity.Layer)
                .ToUpperInvariant();
            if (ContainsAny(search, "LIFT STATION", "PUMP STATION", "SEWER PUMP", "RISING MAIN"))
            {
                snapshot.SewerSourceCount++;
                if (ContainsAny(search, "CONTROL BOARD", "CONTROL PANEL"))
                    snapshot.Add(551, 1.0);
                else if (ContainsAny(search, "NON RETURN", "CHECK VALVE"))
                    snapshot.Add(600, 1.0);
                else if (ContainsAny(search, "RAG CATCH", "SCREEN BASKET"))
                    snapshot.Add(604, 1.0);
                else if (ContainsAny(search, "DAVIT", "HOIST"))
                    snapshot.Add(607, 1.0);
                else if (ContainsAny(search, "PUMP") && !ContainsAny(search, "PIPE"))
                    snapshot.Add(549, 1.0);
                else if (ContainsAny(search, "RISING MAIN") && type.IndexOf("Pipe", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    double length = ReadLength(value) / unitsPerMetre;
                    snapshot.Add(623, length);
                    snapshot.Add(643, length);
                    snapshot.Add(661, length);
                }
                return;
            }
            bool water = ContainsAny(search, "WATER", "PRESSURE", "BULK WATER", "POTABLE", "HYDRANT", "VALVE");
            bool sewer = ContainsAny(search, "SEWER", "SEW", "SANITARY", "FOUL", "MANHOLE");
            if (!water && !sewer) return;

            bool pipe = type.IndexOf("Pipe", StringComparison.OrdinalIgnoreCase) >= 0 &&
                type.IndexOf("Profile", StringComparison.OrdinalIgnoreCase) < 0;
            bool structure = type.IndexOf("Structure", StringComparison.OrdinalIgnoreCase) >= 0 ||
                ContainsAny(search, "MANHOLE", "MH ");
            bool fitting = type.IndexOf("Fitting", StringComparison.OrdinalIgnoreCase) >= 0 ||
                ContainsAny(search, "TEE", "BEND", "REDUCER");
            bool appurtenance = type.IndexOf("Appurtenance", StringComparison.OrdinalIgnoreCase) >= 0 ||
                ContainsAny(search, "VALVE", "HYDRANT");
            double diameter = ReadDiameterMillimetres(value, unitsPerMetre, search);

            if (water)
            {
                snapshot.WaterSourceCount++;
                if (pipe)
                {
                    double length = ReadLength(value) / unitsPerMetre;
                    if (length <= 0.0) return;
                    snapshot.WaterLength += length;
                    snapshot.Add(ClosestRow(diameter, new[] { 90.0, 110.0 }, new[] { 214, 216 }), length);
                }
                else if (fitting) AddWaterFitting(snapshot, search, diameter);
                else if (appurtenance) AddWaterAppurtenance(snapshot, search, diameter);
                else if (ContainsAny(search, "HOUSE CONNECTION", "ERF CONNECTION", "WATER CONNECTION"))
                    AddConnection(snapshot, search, true);
                return;
            }

            snapshot.SewerSourceCount++;
            if (pipe)
            {
                double length = ReadLength(value) / unitsPerMetre;
                if (length <= 0.0) return;
                snapshot.SewerLength += length;
                snapshot.Add(ClosestRow(diameter, new[] { 110.0, 160.0, 200.0 }, new[] { 456, 454, 452 }), length);
                double depth = ReadAverageDepth(value);
                int excavationRow = depth < 1.0 ? 393 : depth < 2.0 ? 395 : depth < 3.0 ? 397 : depth < 4.0 ? 399 : 401;
                snapshot.Add(excavationRow, length);
            }
            else if (structure)
            {
                double depth = ReadStructureDepth(value);
                int row = depth < 0.5 ? 467 : depth < 1.0 ? 469 : depth < 1.5 ? 471 :
                    depth < 2.0 ? 473 : depth < 2.5 ? 482 : depth < 3.0 ? 484 :
                    depth < 3.5 ? 486 : 488;
                snapshot.Add(row, 1.0);
            }
            else if (ContainsAny(search, "HOUSE CONNECTION", "ERF CONNECTION", "SEWER CONNECTION"))
                AddConnection(snapshot, search, false);
            else if (ContainsAny(search, "CONNECT EXIST", "BREAK IN", "TIE IN"))
                snapshot.Add(536, 1.0);
        }

        private static void ApplyDerivedQuantities(CostEstimateSnapshot snapshot)
        {
            int[] dynamicRows =
            {
                165, 200, 202, 208, 210, 214, 216, 222, 224, 228, 230,
                234, 236, 238, 240, 242, 244, 246, 248, 260, 262, 265,
                271, 273, 275, 277, 285, 287, 295, 297, 299, 301, 315,
                393, 395, 397, 399, 401, 415, 423, 425, 438, 440, 452,
                454, 456, 467, 469, 471, 473, 482, 484, 486, 488, 496,
                504, 506, 510, 512, 516, 518, 522, 524, 528, 530, 532,
                536, 549, 551, 600, 604, 607, 623, 643, 661
            };
            foreach (int row in dynamicRows)
                if (!snapshot.Rows.ContainsKey(row)) snapshot.Set(row, 0.0);

            // Trench-material quantities remain driven by live pipe lengths.
            // Factors are explicit and can later be promoted to project settings.
            snapshot.Set(165, snapshot.WaterLength);
            snapshot.Set(200, snapshot.WaterLength * 0.105);
            snapshot.Set(202, snapshot.WaterLength * 0.073);
            snapshot.Set(208, snapshot.Read(200));
            snapshot.Set(210, snapshot.Read(202));
            snapshot.Set(423, snapshot.SewerLength * 0.127);
            snapshot.Set(425, snapshot.SewerLength * 0.076);
            snapshot.Set(438, snapshot.Read(423));
            snapshot.Set(440, snapshot.Read(425));
            snapshot.Set(415, snapshot.SewerLength * 0.766);
            snapshot.Set(496,
                snapshot.Read(467) + snapshot.Read(469) + snapshot.Read(471) +
                snapshot.Read(473) + snapshot.Read(482) + snapshot.Read(484) +
                snapshot.Read(486) + snapshot.Read(488));
            snapshot.Set(532,
                snapshot.Read(504) + snapshot.Read(506) + snapshot.Read(510) +
                snapshot.Read(512) + snapshot.Read(516) + snapshot.Read(518) +
                snapshot.Read(522) + snapshot.Read(524) + snapshot.Read(528) +
                snapshot.Read(530));
            snapshot.Set(315,
                snapshot.Read(295) + snapshot.Read(297) + snapshot.Read(299) + snapshot.Read(301));
            snapshot.Set(275, snapshot.Read(271) + snapshot.Read(273));
            snapshot.Set(277, snapshot.Read(275));
        }

        private static void AddWaterFitting(CostEstimateSnapshot snapshot, string search, double diameter)
        {
            if (ContainsAny(search, "HYDRANT TEE"))
                snapshot.Add(diameter < 100.0 ? 260 : 262, 1.0);
            else if (ContainsAny(search, "REDUCING TEE", "REDUCER TEE"))
                snapshot.Add(diameter < 140.0 ? 228 : 230, 1.0);
            else if (ContainsAny(search, "TEE"))
            {
                snapshot.Add(diameter < 100.0 ? 222 : 224, 1.0);
                snapshot.Add(287, 1.0);
            }
            else if (ContainsAny(search, "BEND", "ELBOW"))
            {
                double angle = ReadNumber(search, 11.25, 22.5, 45.0, 90.0);
                int row = diameter < 100.0
                    ? (angle <= 12.0 ? 234 : angle <= 23.0 ? 236 : angle <= 46.0 ? 238 : 240)
                    : (angle <= 12.0 ? 242 : angle <= 23.0 ? 244 : angle <= 46.0 ? 246 : 248);
                snapshot.Add(row, 1.0);
                snapshot.Add(285, 1.0);
            }
        }

        private static void AddWaterAppurtenance(CostEstimateSnapshot snapshot, string search, double diameter)
        {
            if (ContainsAny(search, "HYDRANT")) snapshot.Add(265, 1.0);
            else if (ContainsAny(search, "VALVE"))
                snapshot.Add(diameter < 100.0 ? 271 : 273, 1.0);
        }

        private static void AddConnection(CostEstimateSnapshot snapshot, string search, bool water)
        {
            bool far = ContainsAny(search, "FAR", "TYPE 3", "TYPE 4");
            bool doubled = ContainsAny(search, "DOUBLE", "TYPE 2", "TYPE 4");
            if (water)
                snapshot.Add(!doubled && !far ? 295 : doubled && !far ? 297 : !doubled ? 299 : 301, 1.0);
            else
            {
                int type = ContainsAny(search, "TYPE 5") ? 5 :
                    ContainsAny(search, "TYPE 4") ? 4 :
                    ContainsAny(search, "TYPE 3") ? 3 :
                    ContainsAny(search, "TYPE 2") ? 2 : 1;
                int[] near = { 504, 510, 516, 522, 528 };
                int[] distant = { 506, 512, 518, 524, 530 };
                snapshot.Add((far ? distant : near)[type - 1], 1.0);
            }
        }

        private static int ClosestRow(double value, double[] sizes, int[] rows)
        {
            if (value <= 0.0) return rows[0];
            int best = 0;
            for (int index = 1; index < sizes.Length; index++)
                if (Math.Abs(sizes[index] - value) < Math.Abs(sizes[best] - value)) best = index;
            return rows[best];
        }

        private static double ReadDiameterMillimetres(object value, double unitsPerMetre, string search)
        {
            double diameter = ReadDouble(value,
                "NominalDiameter", "InnerDiameterOrWidth", "InnerDiameter", "Diameter");
            if (diameter > 0.0)
            {
                double metres = diameter / unitsPerMetre;
                if (metres < 10.0) return metres * 1000.0;
                return diameter;
            }
            Match match = Regex.Match(search, @"(?<!\d)(\d{2,4})\s*MM");
            double parsed;
            return match.Success &&
                double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)
                ? parsed
                : 0.0;
        }

        private static double ReadLength(object value)
        {
            return ReadDouble(value,
                "Length3DCenterToCenter", "Length2DCenterToCenter", "Length3D", "Length2D", "Length");
        }

        private static double ReadAverageDepth(object value)
        {
            double startRim = ReadDouble(value, "StartRimElevation", "StartSurfaceElevation");
            double endRim = ReadDouble(value, "EndRimElevation", "EndSurfaceElevation");
            double startInvert = ReadDouble(value, "StartInvertElevation");
            double endInvert = ReadDouble(value, "EndInvertElevation");
            var depths = new List<double>();
            if (startRim != 0.0 && startInvert != 0.0) depths.Add(Math.Abs(startRim - startInvert));
            if (endRim != 0.0 && endInvert != 0.0) depths.Add(Math.Abs(endRim - endInvert));
            return depths.Count == 0 ? 1.5 : depths.Average();
        }

        private static double ReadStructureDepth(object value)
        {
            double rim = ReadDouble(value, "RimElevation", "SurfaceElevation", "InsertionPointElevation");
            double sump = ReadDouble(value, "SumpElevation", "LowestInvertElevation");
            return rim != 0.0 && sump != 0.0 ? Math.Abs(rim - sump) : 1.5;
        }

        private static string ReadOwnerName(DBObject value, Transaction transaction)
        {
            try
            {
                if (value.OwnerId.IsNull) return string.Empty;
                return ReadString(transaction.GetObject(value.OwnerId, OpenMode.ForRead, false), "Name");
            }
            catch { return string.Empty; }
        }

        private static string ReadString(object value, string property)
        {
            try
            {
                PropertyInfo info = value.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance);
                object result = info == null ? null : info.GetValue(value, null);
                return result == null ? string.Empty : Convert.ToString(result, CultureInfo.CurrentCulture);
            }
            catch { return string.Empty; }
        }

        private static double ReadDouble(object value, params string[] properties)
        {
            foreach (string property in properties)
            {
                try
                {
                    PropertyInfo info = value.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance);
                    object result = info == null ? null : info.GetValue(value, null);
                    if (result != null) return Convert.ToDouble(result, CultureInfo.InvariantCulture);
                }
                catch { }
            }
            return 0.0;
        }

        private static string First(params string[] values)
        {
            return values.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item)) ?? string.Empty;
        }

        private static bool ContainsAny(string source, params string[] values)
        {
            return values.Any(value => source.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static double ReadNumber(string source, params double[] candidates)
        {
            foreach (double candidate in candidates)
                if (source.Contains(candidate.ToString("0.##", CultureInfo.InvariantCulture))) return candidate;
            return candidates[0];
        }
    }

    internal static class WaterSewerWorkbookUpdater
    {
        private static readonly XNamespace Main =
            "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

        public static void Update(string path, CostEstimateSnapshot snapshot)
        {
            string temporary = path + ".ce-tools.tmp";
            File.Copy(path, temporary, true);
            try
            {
                using (ZipArchive archive = ZipFile.Open(temporary, ZipArchiveMode.Update))
                {
                    ZipArchiveEntry worksheet = archive.GetEntry("xl/worksheets/sheet1.xml");
                    if (worksheet == null)
                        throw new InvalidOperationException("The Schedule worksheet could not be found.");
                    XDocument document;
                    using (Stream stream = worksheet.Open()) document = XDocument.Load(stream);
                    foreach (KeyValuePair<int, double> item in snapshot.Rows)
                        WriteNumericCell(document, "F" + item.Key, item.Value);
                    SetFullCalculation(archive);
                    worksheet.Delete();
                    worksheet = archive.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.Optimal);
                    using (Stream stream = worksheet.Open()) document.Save(stream);
                }
                ReplaceFile(temporary, path);
            }
            catch
            {
                if (File.Exists(temporary)) File.Delete(temporary);
                throw;
            }
        }

        private static void WriteNumericCell(XDocument document, string address, double value)
        {
            XElement sheetData = document.Root.Element(Main + "sheetData");
            int rowNumber = int.Parse(Regex.Match(address, @"\d+").Value, CultureInfo.InvariantCulture);
            XElement row = sheetData.Elements(Main + "row")
                .FirstOrDefault(item => (int?)item.Attribute("r") == rowNumber);
            if (row == null)
            {
                row = new XElement(Main + "row", new XAttribute("r", rowNumber));
                XElement next = sheetData.Elements(Main + "row")
                    .FirstOrDefault(item => (int?)item.Attribute("r") > rowNumber);
                if (next == null) sheetData.Add(row); else next.AddBeforeSelf(row);
            }
            XElement cell = row.Elements(Main + "c")
                .FirstOrDefault(item => string.Equals((string)item.Attribute("r"), address, StringComparison.OrdinalIgnoreCase));
            if (cell == null)
            {
                cell = new XElement(Main + "c", new XAttribute("r", address));
                row.Add(cell);
            }
            cell.Attribute("t")?.Remove();
            cell.Element(Main + "f")?.Remove();
            XElement current = cell.Element(Main + "v");
            if (current == null)
            {
                current = new XElement(Main + "v");
                cell.Add(current);
            }
            current.Value = value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static void SetFullCalculation(ZipArchive archive)
        {
            ZipArchiveEntry workbook = archive.GetEntry("xl/workbook.xml");
            if (workbook == null) return;
            XDocument document;
            using (Stream stream = workbook.Open()) document = XDocument.Load(stream);
            XElement calculation = document.Root.Element(Main + "calcPr");
            if (calculation == null)
            {
                calculation = new XElement(Main + "calcPr");
                document.Root.Add(calculation);
            }
            calculation.SetAttributeValue("calcMode", "auto");
            calculation.SetAttributeValue("fullCalcOnLoad", "1");
            calculation.SetAttributeValue("forceFullCalc", "1");
            workbook.Delete();
            workbook = archive.CreateEntry("xl/workbook.xml", CompressionLevel.Optimal);
            using (Stream stream = workbook.Open()) document.Save(stream);
            ZipArchiveEntry chain = archive.GetEntry("xl/calcChain.xml");
            if (chain != null) chain.Delete();
        }

        private static void ReplaceFile(string temporary, string destination)
        {
            string backup = destination + ".ce-tools.backup";
            if (File.Exists(backup)) File.Delete(backup);
            File.Replace(temporary, destination, backup);
            if (File.Exists(backup)) File.Delete(backup);
        }
    }

    internal static class WaterSewerCostAutoRefreshManager
    {
        private static readonly HashSet<Document> Documents = new HashSet<Document>();
        private static readonly HashSet<Document> Pending = new HashSet<Document>();
        private static bool _refreshing;
        private static bool _initialized;

        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;
            AcApplication.DocumentManager.DocumentCreated += OnDocumentCreated;
            AcApplication.DocumentManager.DocumentActivated += OnDocumentActivated;
            AcApplication.DocumentManager.DocumentToBeDestroyed += OnDocumentToBeDestroyed;
            foreach (Document document in AcApplication.DocumentManager)
                Attach(document);
        }

        public static void Terminate()
        {
            if (!_initialized) return;
            AcApplication.DocumentManager.DocumentCreated -= OnDocumentCreated;
            AcApplication.DocumentManager.DocumentActivated -= OnDocumentActivated;
            AcApplication.DocumentManager.DocumentToBeDestroyed -= OnDocumentToBeDestroyed;
            foreach (Document document in Documents.ToArray()) Detach(document);
            Documents.Clear();
            Pending.Clear();
            _initialized = false;
        }

        private static void OnDocumentCreated(object sender, DocumentCollectionEventArgs e) { Attach(e.Document); }
        private static void OnDocumentActivated(object sender, DocumentCollectionEventArgs e) { Attach(e.Document); }
        private static void OnDocumentToBeDestroyed(object sender, DocumentCollectionEventArgs e) { Detach(e.Document); }

        private static void Attach(Document document)
        {
            if (document == null || Documents.Contains(document)) return;
            Documents.Add(document);
            document.Database.ObjectModified += OnObjectModified;
            document.Database.ObjectAppended += OnObjectModified;
            document.Database.ObjectErased += OnObjectErased;
            document.CommandEnded += OnCommandEnded;
            document.CommandCancelled += OnCommandEnded;
            document.CommandFailed += OnCommandEnded;
        }

        private static void Detach(Document document)
        {
            if (document == null) return;
            document.Database.ObjectModified -= OnObjectModified;
            document.Database.ObjectAppended -= OnObjectModified;
            document.Database.ObjectErased -= OnObjectErased;
            document.CommandEnded -= OnCommandEnded;
            document.CommandCancelled -= OnCommandEnded;
            document.CommandFailed -= OnCommandEnded;
            Documents.Remove(document);
            Pending.Remove(document);
        }

        private static void OnObjectModified(object sender, ObjectEventArgs e)
        {
            if (_refreshing) return;
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document != null && ReferenceEquals(document.Database, sender))
                Pending.Add(document);
        }

        private static void OnObjectErased(object sender, ObjectErasedEventArgs e)
        {
            if (_refreshing) return;
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document != null && ReferenceEquals(document.Database, sender))
                Pending.Add(document);
        }

        private static void OnCommandEnded(object sender, CommandEventArgs e)
        {
            Document document = sender as Document;
            if (document == null || !Pending.Remove(document) || _refreshing) return;
            if (!WaterSewerCostEstimateCommands.IsAutomatic(document.Database)) return;
            try
            {
                _refreshing = true;
                WaterSewerCostEstimateCommands.RefreshAll(document);
            }
            finally { _refreshing = false; }
        }
    }
}

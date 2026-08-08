using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.NetworkAssetScheduleCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Linked stormwater, sewer and pressure-network asset schedules. Values are
    /// read from available Civil 3D properties by reflection so unsupported fields
    /// remain blank rather than receiving invented values. Source handles can be
    /// handed to the existing CE linked BOQ builder.
    /// </summary>
    public sealed class NetworkAssetScheduleCommands
    {
        private const string LinkRecordName = "CE_NETWORK_ASSET_SCHEDULE";
        private const string SchemaVersion = "1";

        [CommandMethod("CE_TOOLS", "CE_NETWORKSCHEDULETOOLS", CommandFlags.Modal)]
        public void NetworkScheduleTools()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            var options = new PromptKeywordOptions(
                "\nNetwork schedule tools [Create/Refresh/Export/Info/BOQ] <Create>: ")
            {
                AllowNone = true
            };
            options.Keywords.Add("Create");
            options.Keywords.Add("Refresh");
            options.Keywords.Add("Export");
            options.Keywords.Add("Info");
            options.Keywords.Add("BOQ");
            PromptResult result = document.Editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel) return;
            string choice = result.Status == PromptStatus.OK ? result.StringResult : "Create";
            string command;
            if (string.Equals(choice, "Refresh", StringComparison.OrdinalIgnoreCase))
                command = "CE_NETWORKSCHEDULEREFRESH ";
            else if (string.Equals(choice, "Export", StringComparison.OrdinalIgnoreCase))
                command = "CE_NETWORKSCHEDULEEXPORT ";
            else if (string.Equals(choice, "Info", StringComparison.OrdinalIgnoreCase))
                command = "CE_NETWORKSCHEDULEINFO ";
            else if (string.Equals(choice, "BOQ", StringComparison.OrdinalIgnoreCase))
                command = "CE_NETWORKSCHEDULEBOQ ";
            else
                command = "CE_NETWORKSCHEDULE ";
            document.SendStringToExecute(command, true, false, true);
        }

        [CommandMethod("CE_TOOLS", "CE_NETWORKSCHEDULE", CommandFlags.Modal | CommandFlags.Redraw)]
        public void CreateNetworkSchedule()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            Editor editor = document.Editor;

            var disciplineOptions = new PromptKeywordOptions(
                "\nNetwork asset scope [All/Stormwater/Sewer/Water] <All>: ")
            {
                AllowNone = true
            };
            disciplineOptions.Keywords.Add("All");
            disciplineOptions.Keywords.Add("Stormwater");
            disciplineOptions.Keywords.Add("Sewer");
            disciplineOptions.Keywords.Add("Water");
            PromptResult disciplineResult = editor.GetKeywords(disciplineOptions);
            if (disciplineResult.Status == PromptStatus.Cancel) return;
            string scope = disciplineResult.Status == PromptStatus.OK
                ? disciplineResult.StringResult
                : "All";

            var sourceOptions = new PromptKeywordOptions(
                "\nAsset source [EntireDrawing/Select] <EntireDrawing>: ")
            {
                AllowNone = true
            };
            sourceOptions.Keywords.Add("EntireDrawing");
            sourceOptions.Keywords.Add("Select");
            PromptResult sourceResult = editor.GetKeywords(sourceOptions);
            if (sourceResult.Status == PromptStatus.Cancel) return;
            bool selectedOnly = sourceResult.Status == PromptStatus.OK &&
                string.Equals(sourceResult.StringResult, "Select", StringComparison.OrdinalIgnoreCase);

            List<ObjectId> sourceIds;
            if (selectedOnly)
            {
                PromptSelectionResult selection = editor.GetSelection(new PromptSelectionOptions
                {
                    MessageForAdding = "\nSelect network pipes, structures, fittings, bends and appurtenances: ",
                    AllowDuplicates = false,
                    RejectObjectsFromNonCurrentSpace = true
                });
                if (selection.Status != PromptStatus.OK) return;
                sourceIds = selection.Value.GetObjectIds().ToList();
            }
            else
            {
                sourceIds = ReadAllDatabaseObjectIds(document.Database);
            }

            var link = new NetworkScheduleLink(scope, sourceIds.Select(id => id.Handle.ToString()));
            int rejected;
            List<NetworkAssetRow> rows;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                rows = ReadRows(document.Database, transaction, link, out rejected);
            }
            if (rows.Count == 0)
            {
                editor.WriteMessage(
                    "\nCE_NETWORKSCHEDULE stopped. No supported network assets matched the selected scope.");
                return;
            }

            PromptPointResult insertion = editor.GetPoint(
                "\nPick insertion point for the linked network asset schedule: ");
            if (insertion.Status != PromptStatus.OK) return;
            AnnotationOptions annotation;
            if (!AnnotationSettingsStore.Prepare(document, false, out annotation)) return;

            var review = new List<KeyValuePair<string, string>>
            {
                Pair("Scope", scope),
                Pair("Source mode", selectedOnly ? "Selected objects" : "Entire drawing"),
                Pair("Supported assets", rows.Count.ToString(CultureInfo.InvariantCulture)),
                Pair("Rejected/non-network objects", rejected.ToString(CultureInfo.InvariantCulture)),
                Pair("Columns", "Discipline, network, type, name, description, family, size, length, slope, bend angle, start/end levels"),
                Pair("Linked refresh", "Yes"),
                Pair("BOQ handoff", "Yes")
            };
            if (!PopupTablePresenter.ShowReview(
                    "CE Tools - Network Asset Schedule",
                    "Only values exposed by the current Civil 3D object are written. Missing fields remain blank and can be reviewed before issue.",
                    review,
                    "Create Schedule"))
            {
                editor.WriteMessage("\nCE_NETWORKSCHEDULE cancelled.");
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
                "\nCE_NETWORKSCHEDULE complete. Assets={0}; rejected={1}.",
                rows.Count,
                rejected);
            if (PromptYesNo(editor, "Export this network asset schedule to Excel now", false))
                ExportTable(document, tableId);
        }

        [CommandMethod("CE_TOOLS", "CE_NETWORKSCHEDULEREFRESH", CommandFlags.Modal | CommandFlags.Redraw)]
        public void RefreshNetworkSchedule()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            PromptEntityResult result = PromptTable(
                document.Editor,
                "\nSelect a linked CE network asset schedule: ");
            if (result.Status != PromptStatus.OK) return;
            try
            {
                int rows;
                int rejected;
                RefreshTable(document.Database, result.ObjectId, out rows, out rejected);
                document.Editor.Regen();
                document.Editor.WriteMessage(
                    "\nCE_NETWORKSCHEDULEREFRESH complete. Assets={0}; missing/rejected={1}.",
                    rows,
                    rejected);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_NETWORKSCHEDULEREFRESH stopped. {0}",
                    exception.Message);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_NETWORKSCHEDULEEXPORT", CommandFlags.Modal)]
        public void ExportNetworkSchedule()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            PromptEntityResult result = PromptTable(
                document.Editor,
                "\nSelect a linked CE network asset schedule to export: ");
            if (result.Status != PromptStatus.OK) return;
            ExportTable(document, result.ObjectId);
        }

        [CommandMethod("CE_TOOLS", "CE_NETWORKSCHEDULEINFO", CommandFlags.Modal | CommandFlags.Redraw)]
        public void NetworkScheduleInformation()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            PromptEntityResult result = PromptTable(
                document.Editor,
                "\nSelect a linked CE network asset schedule for information: ");
            if (result.Status != PromptStatus.OK) return;
            try
            {
                NetworkScheduleLink link;
                int existing;
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    Table table = transaction.GetObject(result.ObjectId, OpenMode.ForRead, false) as Table;
                    link = ReadLink(table, transaction);
                    existing = table == null ? 0 : Math.Max(0, table.Rows.Count - 2);
                }
                int rejected;
                int current;
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    current = ReadRows(document.Database, transaction, link, out rejected).Count;
                }
                var rows = new List<KeyValuePair<string, string>>
                {
                    Pair("Scope", link.Scope),
                    Pair("Stored source handles", link.SourceHandles.Count.ToString(CultureInfo.InvariantCulture)),
                    Pair("Existing table rows", existing.ToString(CultureInfo.InvariantCulture)),
                    Pair("Current supported assets", current.ToString(CultureInfo.InvariantCulture)),
                    Pair("Missing/rejected handles", rejected.ToString(CultureInfo.InvariantCulture)),
                    Pair("BOQ handoff command", "CE_NETWORKSCHEDULEBOQ")
                };
                PopupTablePresenter.ShowReportAndOfferTable(
                    document,
                    "CE Tools - Network Asset Schedule Information",
                    "The source handles can be handed directly to the existing linked CE BOQ builder.",
                    rows,
                    "CE TOOLS NETWORK ASSET SCHEDULE INFORMATION");
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_NETWORKSCHEDULEINFO stopped. {0}",
                    exception.Message);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_NETWORKSCHEDULEBOQ", CommandFlags.Modal | CommandFlags.Redraw)]
        public void BuildBoqFromNetworkSchedule()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            PromptEntityResult result = PromptTable(
                document.Editor,
                "\nSelect a linked CE network asset schedule to send to the BOQ builder: ");
            if (result.Status != PromptStatus.OK) return;
            try
            {
                NetworkScheduleLink link;
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    Table table = transaction.GetObject(result.ObjectId, OpenMode.ForRead, false) as Table;
                    link = ReadLink(table, transaction);
                }
                var ids = new List<ObjectId>();
                foreach (string handle in link.SourceHandles)
                {
                    ObjectId id;
                    if (TryResolveHandle(document.Database, handle, out id)) ids.Add(id);
                }
                if (ids.Count == 0)
                {
                    document.Editor.WriteMessage(
                        "\nCE_NETWORKSCHEDULEBOQ stopped. No live source assets remain.");
                    return;
                }
                document.Editor.SetImpliedSelection(ids.ToArray());
                document.Editor.WriteMessage(
                    "\nCE_NETWORKSCHEDULEBOQ selected {0} live assets and is opening the linked BOQ builder.",
                    ids.Count);
                document.SendStringToExecute("CE_BOQBUILD ", true, false, true);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_NETWORKSCHEDULEBOQ stopped. {0}",
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
                    int rejected;
                    RefreshTable(document.Database, tableId, out rows, out rejected);
                    refreshed++;
                }
                catch
                {
                    // Continue refreshing independent schedules.
                }
            }
            return refreshed;
        }

        private static List<ObjectId> ReadAllDatabaseObjectIds(Database database)
        {
            var ids = new List<ObjectId>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTable blocks = transaction.GetObject(database.BlockTableId, OpenMode.ForRead, false) as BlockTable;
                if (blocks == null) return ids;
                foreach (ObjectId blockId in blocks)
                {
                    BlockTableRecord block = transaction.GetObject(blockId, OpenMode.ForRead, false) as BlockTableRecord;
                    if (block == null || block.IsFromExternalReference || block.IsDependent) continue;
                    foreach (ObjectId objectId in block)
                        ids.Add(objectId);
                }
            }
            return ids.Distinct().ToList();
        }

        private static List<NetworkAssetRow> ReadRows(
            Database database,
            Transaction transaction,
            NetworkScheduleLink link,
            out int rejected)
        {
            rejected = 0;
            var rows = new List<NetworkAssetRow>();
            foreach (string handle in link.SourceHandles)
            {
                ObjectId id;
                if (!TryResolveHandle(database, handle, out id))
                {
                    rejected++;
                    continue;
                }
                DBObject value;
                try
                {
                    value = transaction.GetObject(id, OpenMode.ForRead, false);
                }
                catch
                {
                    rejected++;
                    continue;
                }
                NetworkAssetRow row;
                if (!TryBuildRow(value, transaction, out row) || !MatchesScope(row, link.Scope))
                {
                    rejected++;
                    continue;
                }
                rows.Add(row);
            }
            return rows
                .OrderBy(row => row.Discipline, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(row => row.Network, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(row => row.AssetType, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private static bool TryBuildRow(
            DBObject value,
            Transaction transaction,
            out NetworkAssetRow row)
        {
            row = null;
            if (value == null) return false;
            string typeName = value.GetType().Name;
            string upper = typeName.ToUpperInvariant();
            bool supported =
                upper.Contains("PIPE") ||
                upper.Contains("STRUCTURE") ||
                upper.Contains("FITTING") ||
                upper.Contains("APPURTENANCE");
            if (!supported || upper.Contains("PROFILE")) return false;

            string assetType = FriendlyType(typeName);
            string name = ReadString(value, "Name");
            string description = FirstNonBlank(
                ReadString(value, "Description"),
                ReadString(value, "RawDescription"),
                ReadString(value, "PartDescription"));
            string family = FirstNonBlank(
                ReadString(value, "PartFamilyName"),
                ReadNestedString(value, "PartData", "PartFamilyName"),
                ReadNestedString(value, "PartData", "PartFamily"));
            string size = FirstNonBlank(
                ReadString(value, "PartSizeName"),
                ReadNestedString(value, "PartData", "PartSizeName"),
                FormatDiameter(value));
            string network = FirstNonBlank(
                ReadString(value, "NetworkName"),
                ReadOwnerName(value, transaction),
                "<Unresolved network>");
            string discipline = InferDiscipline(value, network, description);
            double? length = ReadDouble(value,
                "Length3DCenterToCenter",
                "Length2DCenterToCenter",
                "Length3D",
                "Length2D",
                "Length");
            double? geometricLength = ReadGeometricLength(value, transaction);
            if (geometricLength.HasValue && geometricLength.Value > 0.001 &&
                (!length.HasValue || length.Value <= 0.0 ||
                 (Math.Abs(length.Value - 1.0) < 0.001 && geometricLength.Value > 1.01)))
                length = geometricLength;
            double? slope = ReadDouble(value, "Slope", "SlopePercent", "Grade");
            if (slope.HasValue && Math.Abs(slope.Value) <= 1.0 && !HasProperty(value, "SlopePercent"))
                slope *= 100.0;
            double? bendAngle = ReadDouble(value,
                "DeflectionAngle",
                "BendAngle",
                "Angle");
            if (bendAngle.HasValue && Math.Abs(bendAngle.Value) <= (Math.PI * 2.0 + 0.001))
                bendAngle = bendAngle.Value * 180.0 / Math.PI;
            double? startLevel = ReadDouble(value,
                "StartInvertElevation",
                "StartElevation",
                "StartPointElevation");
            double? endLevel = ReadDouble(value,
                "EndInvertElevation",
                "EndElevation",
                "EndPointElevation");

            row = new NetworkAssetRow(
                value.ObjectId.Handle.ToString(),
                discipline,
                network,
                assetType,
                string.IsNullOrWhiteSpace(name) ? typeName + " " + value.ObjectId.Handle : name,
                description,
                family,
                size,
                length,
                slope,
                bendAngle,
                startLevel,
                endLevel);
            return true;
        }

        private static bool MatchesScope(NetworkAssetRow row, string scope)
        {
            return string.Equals(scope, "All", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(row.Discipline, scope, StringComparison.OrdinalIgnoreCase);
        }

        private static string InferDiscipline(DBObject value, string network, string description)
        {
            string combined = (value.GetType().Name + " " + network + " " + description).ToUpperInvariant();
            if (combined.Contains("PRESSURE") || combined.Contains("WATER") || combined.Contains("W-")) return "Water";
            if (combined.Contains("SEWER") || combined.Contains("SEW") || combined.Contains("SANITARY")) return "Sewer";
            return "Stormwater";
        }

        private static string ReadOwnerName(DBObject value, Transaction transaction)
        {
            try
            {
                if (value.OwnerId.IsNull) return string.Empty;
                DBObject owner = transaction.GetObject(value.OwnerId, OpenMode.ForRead, false);
                return ReadString(owner, "Name");
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string FormatDiameter(object value)
        {
            double? diameter = ReadDouble(value,
                "InnerDiameterOrWidth",
                "InnerDiameter",
                "NominalDiameter",
                "Diameter");
            double? height = ReadDouble(value,
                "InnerHeight",
                "InnerDiameterOrHeight",
                "Height");
            if (!diameter.HasValue) return string.Empty;
            double widthMm = ToNominalMillimetres(diameter.Value);
            if (height.HasValue && Math.Abs(height.Value - diameter.Value) > 0.000001)
                return widthMm.ToString("N0", CultureInfo.CurrentCulture) + " × " +
                    ToNominalMillimetres(height.Value).ToString("N0", CultureInfo.CurrentCulture) + " mm";
            return widthMm.ToString("N0", CultureInfo.CurrentCulture) + " mm";
        }

        private static double ToNominalMillimetres(double value)
        {
            double absolute = Math.Abs(value);
            double millimetres = absolute > 0.0 && absolute < 10.0 ? absolute * 1000.0 : absolute;
            double[] nominal = { 20, 25, 32, 40, 50, 63, 75, 90, 110, 125, 140, 160, 180, 200, 225, 250, 280, 300, 315, 355, 400, 450, 500, 560, 600, 630, 710, 800, 900, 1000, 1200, 1500, 1800, 2000 };
            foreach (double candidate in nominal)
                if (millimetres <= candidate + 0.5) return candidate;
            return Math.Round(millimetres, 0);
        }

        private static double? ReadGeometricLength(object value, Transaction transaction)
        {
            if (value == null) return null;
            Autodesk.AutoCAD.Geometry.Point3d firstPoint;
            Autodesk.AutoCAD.Geometry.Point3d secondPoint;
            if (TryReadPointProperty(value, "StartPoint", out firstPoint) &&
                TryReadPointProperty(value, "EndPoint", out secondPoint))
                return firstPoint.DistanceTo(secondPoint);
            try
            {
                PropertyInfo startProperty = value.GetType().GetProperty("StartStructureId", BindingFlags.Public | BindingFlags.Instance);
                PropertyInfo endProperty = value.GetType().GetProperty("EndStructureId", BindingFlags.Public | BindingFlags.Instance);
                if (transaction != null && startProperty != null && endProperty != null)
                {
                    object startRaw = startProperty.GetValue(value, null);
                    object endRaw = endProperty.GetValue(value, null);
                    if (startRaw is ObjectId && endRaw is ObjectId)
                    {
                        DBObject start = transaction.GetObject((ObjectId)startRaw, OpenMode.ForRead, false);
                        DBObject end = transaction.GetObject((ObjectId)endRaw, OpenMode.ForRead, false);
                        if ((TryReadPointProperty(start, "Position", out firstPoint) || TryReadPointProperty(start, "Location", out firstPoint)) &&
                            (TryReadPointProperty(end, "Position", out secondPoint) || TryReadPointProperty(end, "Location", out secondPoint)))
                            return firstPoint.DistanceTo(secondPoint);
                    }
                }
            }
            catch { }
            try
            {
                MethodInfo method = value.GetType().GetMethod(
                    "GetPointAtParam",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new[] { typeof(double) },
                    null);
                if (method == null) return null;
                object first = method.Invoke(value, new object[] { 0.0 });
                object second = method.Invoke(value, new object[] { 1.0 });
                if (!(first is Autodesk.AutoCAD.Geometry.Point3d) || !(second is Autodesk.AutoCAD.Geometry.Point3d)) return null;
                return ((Autodesk.AutoCAD.Geometry.Point3d)first).DistanceTo((Autodesk.AutoCAD.Geometry.Point3d)second);
            }
            catch { return null; }
        }

        private static bool TryReadPointProperty(object value, string name, out Autodesk.AutoCAD.Geometry.Point3d point)
        {
            point = Autodesk.AutoCAD.Geometry.Point3d.Origin;
            if (value == null) return false;
            try
            {
                PropertyInfo property = value.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                object raw = property == null ? null : property.GetValue(value, null);
                if (raw is Autodesk.AutoCAD.Geometry.Point3d) { point = (Autodesk.AutoCAD.Geometry.Point3d)raw; return true; }
            }
            catch { }
            return false;
        }

        private static ObjectId CreateLinkedTable(
            Database database,
            Autodesk.AutoCAD.Geometry.Point3d insertion,
            IList<NetworkAssetRow> rows,
            NetworkScheduleLink link,
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
                PopulateTable(table, rows, textHeight, link.Scope);
                transaction.Commit();
                return id;
            }
        }

        private static void RefreshTable(Database database, ObjectId tableId, out int rows, out int rejected)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                Table table = transaction.GetObject(tableId, OpenMode.ForWrite, false) as Table;
                if (table == null) throw new InvalidOperationException("The selected object is not an AutoCAD table.");
                NetworkScheduleLink link = ReadLink(table, transaction);
                List<NetworkAssetRow> current = ReadRows(database, transaction, link, out rejected);
                if (current.Count == 0) throw new InvalidOperationException("The linked network schedule has no live supported assets.");
                PopulateTable(table, current, database.Textsize, link.Scope);
                rows = current.Count;
                transaction.Commit();
            }
        }

        private static void PopulateTable(Table table, IList<NetworkAssetRow> rows, double textHeight, string scope)
        {
            const int columns = 13;
            double height = NormalizeHeight(textHeight);
            table.SetSize(rows.Count + 2, columns);
            table.SetRowHeight(Math.Max(height * 1.8, 0.001));
            table.SetColumnWidth(Math.Max(height * 7.0, 0.001));
            table.Cells[0, 0].TextString = "CE TOOLS " + scope.ToUpperInvariant() + " NETWORK ASSET SCHEDULE";
            table.MergeCells(CellRange.Create(table, 0, 0, 0, columns - 1));
            string[] headings =
            {
                "DISCIPLINE", "NETWORK", "ASSET TYPE", "NAME", "DESCRIPTION", "PART FAMILY", "SIZE",
                "LENGTH", "SLOPE (%)", "BEND ANGLE (deg)", "START LEVEL", "END LEVEL", "SOURCE HANDLE"
            };
            for (int column = 0; column < columns; column++)
            {
                table.Cells[1, column].TextString = headings[column];
                table.Cells[1, column].TextHeight = height;
            }
            for (int index = 0; index < rows.Count; index++)
            {
                NetworkAssetRow row = rows[index];
                int tableRow = index + 2;
                string[] values =
                {
                    row.Discipline, row.Network, row.AssetType, row.Name, row.Description, row.Family, row.Size,
                    FormatNullable(row.Length), FormatNullable(row.SlopePercent), FormatNullable(row.BendAngleDegrees),
                    FormatNullable(row.StartLevel), FormatNullable(row.EndLevel), row.SourceHandle
                };
                for (int column = 0; column < columns; column++)
                {
                    table.Cells[tableRow, column].TextString = values[column] ?? string.Empty;
                    table.Cells[tableRow, column].TextHeight = height;
                }
            }
            table.GenerateLayout();
        }

        private static void WriteLink(Table table, Transaction transaction, NetworkScheduleLink link)
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
                new TypedValue((int)DxfCode.Text, "Schema=" + SchemaVersion),
                new TypedValue((int)DxfCode.Text, "Scope=" + link.Scope)
            };
            foreach (string handle in link.SourceHandles)
                values.Add(new TypedValue((int)DxfCode.Text, "Handle=" + handle));
            record.Data = new ResultBuffer(values.ToArray());
        }

        private static NetworkScheduleLink ReadLink(Table table, Transaction transaction)
        {
            if (table == null || table.ExtensionDictionary.IsNull)
                throw new InvalidOperationException("The selected table is not a linked CE network schedule.");
            DBDictionary dictionary = transaction.GetObject(table.ExtensionDictionary, OpenMode.ForRead, false) as DBDictionary;
            if (dictionary == null || !dictionary.Contains(LinkRecordName))
                throw new InvalidOperationException("The selected table has no CE network schedule link record.");
            Xrecord record = transaction.GetObject(dictionary.GetAt(LinkRecordName), OpenMode.ForRead, false) as Xrecord;
            if (record == null || record.Data == null) throw new InvalidOperationException("The network schedule link record is empty.");
            string scope = "All";
            var handles = new List<string>();
            foreach (TypedValue value in record.Data)
            {
                string text = value.Value as string;
                if (string.IsNullOrWhiteSpace(text)) continue;
                if (text.StartsWith("Scope=", StringComparison.OrdinalIgnoreCase)) scope = text.Substring("Scope=".Length);
                else if (text.StartsWith("Handle=", StringComparison.OrdinalIgnoreCase)) handles.Add(text.Substring("Handle=".Length));
            }
            if (handles.Count == 0) throw new InvalidOperationException("The linked network schedule contains no source handles.");
            return new NetworkScheduleLink(scope, handles);
        }

        private static void ExportTable(Document document, ObjectId tableId)
        {
            Editor editor = document.Editor;
            try
            {
                int rows;
                int rejected;
                RefreshTable(document.Database, tableId, out rows, out rejected);
                IList<IList<string>> cells;
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    Table table = transaction.GetObject(tableId, OpenMode.ForRead, false) as Table;
                    cells = ReadCells(table);
                }
                var options = new PromptSaveFileOptions("\nSelect network asset Excel workbook path: ")
                {
                    Filter = "Excel Workbook (*.xlsx)|*.xlsx",
                    DialogCaption = "Export CE Tools Network Asset Schedule",
                    InitialFileName = "CE-Network-Asset-Schedule.xlsx"
                };
                PromptFileNameResult result = editor.GetFileNameForSave(options);
                if (result.Status != PromptStatus.OK) return;
                string path = result.StringResult;
                if (!path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)) path += ".xlsx";
                SimpleXlsxWriter.Write(path, "Network Assets", cells);
                editor.WriteMessage(
                    "\nCE_NETWORKSCHEDULEEXPORT complete. Assets={0}; missing/rejected={1}; file={2}",
                    rows,
                    rejected,
                    path);
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage("\nCE_NETWORKSCHEDULEEXPORT stopped. {0}", exception.Message);
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

        private static PromptEntityResult PromptTable(Editor editor, string message)
        {
            var options = new PromptEntityOptions(message);
            options.SetRejectMessage("\nSelect an AutoCAD table.");
            options.AddAllowedClass(typeof(Table), false);
            return editor.GetEntity(options);
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

        private static string ReadString(object value, string propertyName)
        {
            object raw = ReadProperty(value, propertyName);
            return Convert.ToString(raw, CultureInfo.CurrentCulture) ?? string.Empty;
        }

        private static string ReadNestedString(object value, string parent, string child)
        {
            return ReadString(ReadProperty(value, parent), child);
        }

        private static double? ReadDouble(object value, params string[] names)
        {
            foreach (string name in names)
            {
                object raw = ReadProperty(value, name);
                if (raw == null) continue;
                try
                {
                    double result = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
                    if (!double.IsNaN(result) && !double.IsInfinity(result)) return result;
                }
                catch
                {
                    // Try next property.
                }
            }
            return null;
        }

        private static bool HasProperty(object value, string name)
        {
            return value != null && value.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance) != null;
        }

        private static object ReadProperty(object value, string propertyName)
        {
            if (value == null) return null;
            try
            {
                PropertyInfo property = value.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                return property == null || property.GetIndexParameters().Length != 0
                    ? null
                    : property.GetValue(value, null);
            }
            catch
            {
                return null;
            }
        }

        private static string FirstNonBlank(params string[] values)
        {
            foreach (string value in values)
                if (!string.IsNullOrWhiteSpace(value)) return value;
            return string.Empty;
        }

        private static string FriendlyType(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName)) return "Network asset";
            var characters = new List<char>();
            for (int index = 0; index < typeName.Length; index++)
            {
                char value = typeName[index];
                if (index > 0 && char.IsUpper(value) && !char.IsUpper(typeName[index - 1])) characters.Add(' ');
                characters.Add(value);
            }
            return new string(characters.ToArray());
        }

        private static string FormatNullable(double? value)
        {
            return value.HasValue ? value.Value.ToString("N3", CultureInfo.CurrentCulture) : string.Empty;
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

    internal sealed class NetworkScheduleLink
    {
        public NetworkScheduleLink(string scope, IEnumerable<string> sourceHandles)
        {
            Scope = string.IsNullOrWhiteSpace(scope) ? "All" : scope;
            SourceHandles = sourceHandles == null
                ? new List<string>()
                : sourceHandles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        public string Scope { get; private set; }
        public List<string> SourceHandles { get; private set; }
    }

    internal sealed class NetworkAssetRow
    {
        public NetworkAssetRow(
            string sourceHandle,
            string discipline,
            string network,
            string assetType,
            string name,
            string description,
            string family,
            string size,
            double? length,
            double? slopePercent,
            double? bendAngleDegrees,
            double? startLevel,
            double? endLevel)
        {
            SourceHandle = sourceHandle;
            Discipline = discipline;
            Network = network;
            AssetType = assetType;
            Name = name;
            Description = description;
            Family = family;
            Size = size;
            Length = length;
            SlopePercent = slopePercent;
            BendAngleDegrees = bendAngleDegrees;
            StartLevel = startLevel;
            EndLevel = endLevel;
        }

        public string SourceHandle { get; private set; }
        public string Discipline { get; private set; }
        public string Network { get; private set; }
        public string AssetType { get; private set; }
        public string Name { get; private set; }
        public string Description { get; private set; }
        public string Family { get; private set; }
        public string Size { get; private set; }
        public double? Length { get; private set; }
        public double? SlopePercent { get; private set; }
        public double? BendAngleDegrees { get; private set; }
        public double? StartLevel { get; private set; }
        public double? EndLevel { get; private set; }
    }
}

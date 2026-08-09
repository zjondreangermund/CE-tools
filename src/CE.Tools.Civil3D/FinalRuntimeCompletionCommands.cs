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
using Autodesk.Civil.ApplicationServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.FinalFeatureLineReportCommands))]
[assembly: CommandClass(typeof(CETools.Civil3D.SewerSequenceWorkflowCommands))]
[assembly: CommandClass(typeof(CETools.Civil3D.JunctionSettingOutCommands))]
[assembly: CommandClass(typeof(CETools.Civil3D.NetworkAutoConnectCommands))]

namespace CETools.Civil3D
{
    public sealed class FinalFeatureLineReportCommands
    {
        private const string LinkName = "CE_FL_DYNAMIC_REPORT";

        [CommandMethod("CE_TOOLS", "CE_FLDYNAMICREPORT", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void CreateOrRefresh()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            PromptSelectionResult selection = document.Editor.GetSelection(
                new PromptSelectionOptions { MessageForAdding = "\nSelect feature lines for the linked report: ", AllowDuplicates = false });
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;
            PromptPointResult point = document.Editor.GetPoint("\nPick linked feature-line report table insertion point: ");
            if (point.Status != PromptStatus.OK) return;
            List<ObjectId> sources = selection.Value.GetObjectIds().Where(IsUsable).Distinct().ToList();
            if (sources.Count == 0) return;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = transaction.GetObject(document.Database.CurrentSpaceId, OpenMode.ForWrite, false) as BlockTableRecord;
                if (space == null) return;
                var table = new Table();
                table.SetDatabaseDefaults(document.Database);
                table.Position = point.Value.TransformBy(document.Editor.CurrentUserCoordinateSystem);
                ObjectId id = space.AppendEntity(table);
                transaction.AddNewlyCreatedDBObject(table, true);
                WriteLink(table, transaction, sources);
                Populate(document.Database, transaction, table, sources);
                transaction.Commit();
            }
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_FLDYNAMICREPORT complete. Sources={0}.", sources.Count);
        }

        [CommandMethod("CE_TOOLS", "CE_FLREPORTREFRESH", CommandFlags.Modal | CommandFlags.Redraw)]
        public void RefreshCommand()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            int count = RefreshAll(document);
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_FLREPORTREFRESH complete. Linked reports refreshed={0}.", count);
        }

        internal static int RefreshAll(Document document)
        {
            if (document == null) return 0;
            int refreshed = 0;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord model = transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(document.Database), OpenMode.ForRead, false) as BlockTableRecord;
                if (model == null) return 0;
                foreach (ObjectId id in model)
                {
                    Table table;
                    try { table = transaction.GetObject(id, OpenMode.ForWrite, false) as Table; }
                    catch { continue; }
                    if (table == null) continue;
                    List<ObjectId> sources;
                    if (!TryReadLink(table, transaction, document.Database, out sources)) continue;
                    Populate(document.Database, transaction, table, sources);
                    refreshed++;
                }
                transaction.Commit();
            }
            return refreshed;
        }

        private static void Populate(Database database, Transaction transaction, Table table, IList<ObjectId> sourceIds)
        {
            double text = Math.Max(PaperAnnotationScale.ModelTextHeight(database, 2.0), 0.001);
            var rows = new List<FeatureLineRow>();
            foreach (ObjectId id in sourceIds.Where(IsUsable))
            {
                DBObject value;
                try { value = transaction.GetObject(id, OpenMode.ForRead, false); }
                catch { continue; }
                if (value == null) continue;
                rows.Add(ReadRow(value));
            }
            table.SetSize(rows.Count + 2, 6);
            table.SetRowHeight(text * 1.65);
            for (int column = 0; column < 6; column++) table.SetColumnWidth(column, text * (column == 0 ? 12.0 : 9.0));
            table.MergeCells(CellRange.Create(table, 0, 0, 0, 5));
            table.Cells[0, 0].TextString = "CE DYNAMIC FEATURE LINE REPORT";
            string[] headers = { "NAME", "LENGTH", "MIN ELEV", "MAX ELEV", "MIN GRADE %", "MAX GRADE %" };
            for (int column = 0; column < headers.Length; column++) table.Cells[1, column].TextString = headers[column];
            for (int row = 0; row < rows.Count; row++)
            {
                FeatureLineRow item = rows[row];
                string[] values =
                {
                    item.Name,
                    Format(item.Length),
                    Format(item.MinElevation),
                    Format(item.MaxElevation),
                    Format(item.MinGrade),
                    Format(item.MaxGrade)
                };
                for (int column = 0; column < values.Length; column++) table.Cells[row + 2, column].TextString = values[column];
            }
            for (int row = 0; row < table.Rows.Count; row++)
                for (int column = 0; column < table.Columns.Count; column++)
                {
                    table.Cells[row, column].Alignment = CellAlignment.MiddleCenter;
                    table.Cells[row, column].TextHeight = text;
                }
            try { table.GenerateLayout(); } catch { }
            table.RecordGraphicsModified(true);
            try { table.RecomputeTableBlock(true); } catch { }
        }

        private static FeatureLineRow ReadRow(DBObject value)
        {
            Extents3d extents;
            bool hasExtents = false;
            try { extents = ((Entity)value).GeometricExtents; hasExtents = true; }
            catch { extents = new Extents3d(); }
            double length = ReadDouble(value, "Length3D", "Length2D", "Length");
            Curve curve = value as Curve;
            if (!(length > 0.0) && curve != null)
            {
                try { length = Math.Abs(curve.GetDistanceAtParameter(curve.EndParam) - curve.GetDistanceAtParameter(curve.StartParam)); } catch { }
            }
            return new FeatureLineRow
            {
                Name = ReadText(value, "Name", "DisplayName", "Description"),
                Length = length,
                MinElevation = ReadDoubleOr(value, hasExtents ? extents.MinPoint.Z : double.NaN, "MinElevation", "MinimumElevation"),
                MaxElevation = ReadDoubleOr(value, hasExtents ? extents.MaxPoint.Z : double.NaN, "MaxElevation", "MaximumElevation"),
                MinGrade = ReadDouble(value, "MinGrade", "MinimumGrade"),
                MaxGrade = ReadDouble(value, "MaxGrade", "MaximumGrade")
            };
        }

        private static string ReadText(object value, params string[] names)
        {
            foreach (string name in names)
            {
                try
                {
                    PropertyInfo property = value.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                    object result = property == null ? null : property.GetValue(value, null);
                    if (result != null && !string.IsNullOrWhiteSpace(Convert.ToString(result, CultureInfo.CurrentCulture)))
                        return Convert.ToString(result, CultureInfo.CurrentCulture);
                }
                catch { }
            }
            DBObject databaseObject = value as DBObject;
            return databaseObject == null ? value.GetType().Name : value.GetType().Name + " " + databaseObject.Handle;
        }

        private static double ReadDouble(object value, params string[] names) { return ReadDoubleOr(value, double.NaN, names); }
        private static double ReadDoubleOr(object value, double fallback, params string[] names)
        {
            foreach (string name in names)
            {
                try
                {
                    PropertyInfo property = value.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                    object result = property == null ? null : property.GetValue(value, null);
                    if (result == null) continue;
                    double parsed = Convert.ToDouble(result, CultureInfo.InvariantCulture);
                    if (!double.IsNaN(parsed) && !double.IsInfinity(parsed)) return parsed;
                }
                catch { }
            }
            return fallback;
        }

        private static string Format(double value) { return double.IsNaN(value) || double.IsInfinity(value) ? string.Empty : value.ToString("N3", CultureInfo.CurrentCulture); }
        private static bool IsUsable(ObjectId id) { return !id.IsNull && !id.IsErased; }

        private static void WriteLink(Table table, Transaction transaction, IEnumerable<ObjectId> sources)
        {
            if (table.ExtensionDictionary.IsNull) table.CreateExtensionDictionary();
            DBDictionary dictionary = transaction.GetObject(table.ExtensionDictionary, OpenMode.ForWrite, false) as DBDictionary;
            if (dictionary == null) return;
            Xrecord record;
            if (dictionary.Contains(LinkName)) record = transaction.GetObject(dictionary.GetAt(LinkName), OpenMode.ForWrite, false) as Xrecord;
            else
            {
                record = new Xrecord();
                dictionary.SetAt(LinkName, record);
                transaction.AddNewlyCreatedDBObject(record, true);
            }
            record.Data = new ResultBuffer(sources.Where(IsUsable).Select(id => new TypedValue((int)DxfCode.Text, id.Handle.ToString())).ToArray());
        }

        private static bool TryReadLink(Table table, Transaction transaction, Database database, out List<ObjectId> sources)
        {
            sources = new List<ObjectId>();
            if (table == null || table.ExtensionDictionary.IsNull) return false;
            DBDictionary dictionary = transaction.GetObject(table.ExtensionDictionary, OpenMode.ForRead, false) as DBDictionary;
            if (dictionary == null || !dictionary.Contains(LinkName)) return false;
            Xrecord record = transaction.GetObject(dictionary.GetAt(LinkName), OpenMode.ForRead, false) as Xrecord;
            if (record == null || record.Data == null) return false;
            foreach (TypedValue item in record.Data)
            {
                string handleText = Convert.ToString(item.Value, CultureInfo.InvariantCulture);
                long handleValue;
                if (!long.TryParse(handleText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out handleValue)) continue;
                try
                {
                    ObjectId id = database.GetObjectId(false, new Handle(handleValue), 0);
                    if (IsUsable(id)) sources.Add(id);
                }
                catch { }
            }
            return true;
        }

        private sealed class FeatureLineRow
        {
            public string Name { get; set; }
            public double Length { get; set; }
            public double MinElevation { get; set; }
            public double MaxElevation { get; set; }
            public double MinGrade { get; set; }
            public double MaxGrade { get; set; }
        }
    }

    public sealed class SewerSequenceWorkflowCommands
    {
        [CommandMethod("CE_TOOLS", "CE_SEWSEQWORKFLOW", CommandFlags.Modal)]
        public void Automatic() { Run(false); }

        [CommandMethod("CE_TOOLS", "CE_SEWSEQMAINWORKFLOW", CommandFlags.Modal)]
        public void SelectedMain() { Run(true); }

        [CommandMethod("CE_TOOLS", "CE_SEWPOSTSEQUENCE", CommandFlags.Modal | CommandFlags.Redraw)]
        public void PostSequence()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            ProductionSettingsDialogModel model = SettingsModel();
            CrossDrawingProductionSettingsStore.Load(model);
            string branchLayer = model.Text("BranchLayer");
            bool freezeAlignments = model.Text("FreezeAlignments").StartsWith("Yes", StringComparison.OrdinalIgnoreCase);
            bool freezeBranch = model.Text("FreezeBranch").StartsWith("Yes", StringComparison.OrdinalIgnoreCase);
            var alignmentLayers = new HashSet<ObjectId>();
            ObjectId branchLayerId = ObjectId.Null;
            int moved = 0;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                branchLayerId = EnsureLayer(document.Database, transaction, branchLayer);
                BlockTableRecord modelSpace = transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(document.Database), OpenMode.ForRead, false) as BlockTableRecord;
                if (modelSpace != null)
                {
                    foreach (ObjectId id in modelSpace)
                    {
                        Entity entity;
                        try { entity = transaction.GetObject(id, OpenMode.ForWrite, false) as Entity; } catch { continue; }
                        if (entity == null) continue;
                        string type = entity.GetType().Name;
                        string description = ReadPropertyText(entity, "Description");
                        if (type.IndexOf("Alignment", StringComparison.OrdinalIgnoreCase) >= 0 &&
                            description.IndexOf("CE sewer alignment", StringComparison.OrdinalIgnoreCase) >= 0)
                            alignmentLayers.Add(entity.LayerId);
                        MText text = entity as MText;
                        if (text != null && (text.Contents ?? string.Empty).IndexOf("Branch-", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            if (!branchLayerId.IsNull) text.LayerId = branchLayerId;
                            moved++;
                        }
                    }
                }
                if (freezeAlignments)
                    foreach (ObjectId layer in alignmentLayers) FreezeLayer(document.Database, transaction, layer);
                if (freezeBranch && !branchLayerId.IsNull) FreezeLayer(document.Database, transaction, branchLayerId);
                transaction.Commit();
            }
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_SEWPOSTSEQUENCE complete. Branch labels moved={0}; alignment layers frozen={1}; branch layer frozen={2}.", moved, freezeAlignments ? "Yes" : "No", freezeBranch ? "Yes" : "No");
        }

        private static void Run(bool selectedMain)
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            ProductionSettingsDialogModel model = SettingsModel();
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;
            bool align = model.Text("AutoAlign").StartsWith("Yes", StringComparison.OrdinalIgnoreCase);
            string command = selectedMain ? "CE_SEWSEQMAIN " : "CE_SEWSEQ ";
            if (align) command += "CE_SEWALIGN ";
            command += "CE_SEWPOSTSEQUENCE ";
            document.Editor.WriteMessage("\nCE sewer sequence workflow queued. Complete each Civil/CE prompt; the configured post-sequence layer actions run last.");
            document.SendStringToExecute(command, true, false, true);
        }

        private static ProductionSettingsDialogModel SettingsModel()
        {
            var model = new ProductionSettingsDialogModel("CE Tools - Sewer Sequence Options", "Sequence the network, optionally create alignments immediately, move Branch-* names to a dedicated layer and freeze completed alignment/branch-label layers.");
            model.AddChoice("AutoAlign", "Sequence", "Auto create / refresh alignments", "Yes", "Run CE_SEWALIGN immediately after sequence completes.", new[] { "Yes", "No" });
            model.AddText("BranchLayer", "Layers", "Branch-name layer", "CE-SEWER-BRANCH-NAMES", "Dedicated layer for generated Branch-* names.");
            model.AddChoice("FreezeAlignments", "Layers", "Freeze generated sewer alignment layers after production", "No", "Freeze the actual layers containing generated CE sewer alignments after post-processing.", new[] { "No", "Yes" });
            model.AddChoice("FreezeBranch", "Layers", "Freeze branch-name layer after production", "No", "Freeze the dedicated branch-name layer when production is complete.", new[] { "No", "Yes" });
            return model;
        }

        private static ObjectId EnsureLayer(Database database, Transaction transaction, string name)
        {
            string layerName = string.IsNullOrWhiteSpace(name) ? "CE-SEWER-BRANCH-NAMES" : name.Trim();
            LayerTable layers = transaction.GetObject(database.LayerTableId, OpenMode.ForRead, false) as LayerTable;
            if (layers == null) return ObjectId.Null;
            if (layers.Has(layerName)) return layers[layerName];
            layers.UpgradeOpen();
            var layer = new LayerTableRecord { Name = layerName };
            ObjectId id = layers.Add(layer);
            transaction.AddNewlyCreatedDBObject(layer, true);
            return id;
        }

        private static void FreezeLayer(Database database, Transaction transaction, ObjectId id)
        {
            if (id.IsNull || id == database.Clayer) return;
            try
            {
                LayerTableRecord layer = transaction.GetObject(id, OpenMode.ForWrite, false) as LayerTableRecord;
                if (layer != null) layer.IsFrozen = true;
            }
            catch { }
        }

        private static string ReadPropertyText(object value, string name)
        {
            try
            {
                PropertyInfo property = value.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                object result = property == null ? null : property.GetValue(value, null);
                return result == null ? string.Empty : Convert.ToString(result, CultureInfo.CurrentCulture) ?? string.Empty;
            }
            catch { return string.Empty; }
        }
    }

    public sealed class JunctionSettingOutCommands
    {
        [CommandMethod("CE_TOOLS", "CE_JUNCTIONSETTINGOUT", CommandFlags.Modal | CommandFlags.UsePickSet)]
        public void Run()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            var model = new ProductionSettingsDialogModel("CE Tools - Junction Setting-Out", "Choose one junction or all selected T/cross-junction return curves, then continue into the multi-source vertex setting-out popup.");
            model.AddChoice("Scope", "Setting-Out", "Intersection scope", "All selected junctions", "Use all selected return curves or only the four/two curves nearest one picked junction.", new[] { "All selected junctions", "Single picked junction" });
            model.AddPositiveDouble("Cluster", "Setting-Out", "Junction cluster distance", 35.0, "Maximum plan distance used to isolate the picked junction returns.");
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;
            PromptSelectionResult selection = document.Editor.GetSelection(new PromptSelectionOptions { MessageForAdding = "\nSelect T/cross-junction bellmouth/return curves: ", AllowDuplicates = false });
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;
            ObjectId[] ids = selection.Value.GetObjectIds();
            if (model.Text("Scope").StartsWith("Single", StringComparison.OrdinalIgnoreCase))
            {
                PromptPointResult picked = document.Editor.GetPoint("\nPick the junction centre to set out: ");
                if (picked.Status != PromptStatus.OK) return;
                Point3d point = picked.Value.TransformBy(document.Editor.CurrentUserCoordinateSystem);
                double cluster = model.Double("Cluster", 35.0);
                var nearest = new List<Tuple<ObjectId, double>>();
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId id in ids)
                    {
                        Curve curve;
                        try { curve = transaction.GetObject(id, OpenMode.ForRead, false) as Curve; } catch { continue; }
                        if (curve == null) continue;
                        Point3d mid;
                        try { mid = curve.GetPointAtParameter((curve.StartParam + curve.EndParam) * 0.5); } catch { continue; }
                        double distance = new Point2d(mid.X, mid.Y).GetDistanceTo(new Point2d(point.X, point.Y));
                        if (distance <= cluster) nearest.Add(Tuple.Create(id, distance));
                    }
                }
                ids = nearest.OrderBy(item => item.Item2).Take(4).Select(item => item.Item1).ToArray();
            }
            if (ids.Length == 0) return;
            document.Editor.SetImpliedSelection(ids);
            document.Editor.WriteMessage("\nCE_JUNCTIONSETTINGOUT prepared {0} return curves. In Vertex Setting-Out choose road-grouped numbering / desired surface and table options.", ids.Length);
            document.SendStringToExecute("CE_VERTEXSETTINGOUT ", true, false, true);
        }
    }

    public sealed class NetworkAutoConnectCommands
    {
        [CommandMethod("CE_TOOLS", "CE_NETWORKCONNECTALL", CommandFlags.Modal | CommandFlags.Redraw)]
        public void ConnectAll()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            var model = new ProductionSettingsDialogModel("CE Tools - Auto Connect Network Parts", "Best-effort Civil 3D connection repair: connect open pipe ends to the nearest structure in the same network within the selected plan tolerance. No pipes or structures are deleted.");
            model.AddPositiveDouble("Tolerance", "Connection", "Connection tolerance", 0.25, "Maximum plan distance from an open pipe end to a structure insertion point.");
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;
            double tolerance = model.Double("Tolerance", 0.25);
            int connected = 0;
            int considered = 0;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord modelSpace = transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(document.Database), OpenMode.ForRead, false) as BlockTableRecord;
                if (modelSpace == null) return;
                var structures = new List<DBObject>();
                var pipes = new List<DBObject>();
                foreach (ObjectId id in modelSpace)
                {
                    DBObject value;
                    try { value = transaction.GetObject(id, OpenMode.ForWrite, false); } catch { continue; }
                    if (value == null) continue;
                    string type = value.GetType().Name;
                    if (type.Equals("Structure", StringComparison.OrdinalIgnoreCase)) structures.Add(value);
                    else if (type.Equals("Pipe", StringComparison.OrdinalIgnoreCase)) pipes.Add(value);
                }
                foreach (DBObject pipe in pipes)
                {
                    considered++;
                    connected += TryConnectEnd(pipe, structures, "Start", tolerance) ? 1 : 0;
                    connected += TryConnectEnd(pipe, structures, "End", tolerance) ? 1 : 0;
                }
                transaction.Commit();
            }
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_NETWORKCONNECTALL complete. Pipes considered={0}; open ends connected={1}.", considered, connected);
        }

        private static bool TryConnectEnd(DBObject pipe, IList<DBObject> structures, string endName, double tolerance)
        {
            ObjectId networkId = ReadObjectId(pipe, "NetworkId");
            ObjectId existing = ReadObjectId(pipe, endName + "StructureId");
            if (!existing.IsNull && !existing.IsErased) return false;
            Point3d endpoint;
            if (!ReadPoint(pipe, endName + "Point", out endpoint) && !ReadPoint(pipe, endName + "PointLocation", out endpoint)) return false;
            DBObject nearest = null;
            double best = tolerance;
            foreach (DBObject structure in structures)
            {
                ObjectId structureNetwork = ReadObjectId(structure, "NetworkId");
                if (!networkId.IsNull && !structureNetwork.IsNull && networkId != structureNetwork) continue;
                Point3d location;
                if (!ReadPoint(structure, "Position", out location) && !ReadPoint(structure, "Location", out location)) continue;
                double distance = new Point2d(endpoint.X, endpoint.Y).GetDistanceTo(new Point2d(location.X, location.Y));
                if (distance <= best) { best = distance; nearest = structure; }
            }
            if (nearest == null) return false;
            MethodInfo method = pipe.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(candidate => candidate.Name.IndexOf("ConnectToStructure", StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderByDescending(candidate => candidate.GetParameters().Length)
                .FirstOrDefault();
            if (method == null) return false;
            try
            {
                ParameterInfo[] parameters = method.GetParameters();
                object[] args = new object[parameters.Length];
                for (int index = 0; index < parameters.Length; index++)
                {
                    Type type = parameters[index].ParameterType;
                    if (type == typeof(ObjectId)) args[index] = nearest.ObjectId;
                    else if (type == typeof(bool)) args[index] = true;
                    else if (type.IsEnum)
                    {
                        string[] names = Enum.GetNames(type);
                        string match = names.FirstOrDefault(name => name.IndexOf(endName, StringComparison.OrdinalIgnoreCase) >= 0) ?? names.FirstOrDefault();
                        args[index] = string.IsNullOrWhiteSpace(match) ? Activator.CreateInstance(type) : Enum.Parse(type, match);
                    }
                    else args[index] = type.IsValueType ? Activator.CreateInstance(type) : null;
                }
                method.Invoke(pipe, args);
                return true;
            }
            catch { return false; }
        }

        private static ObjectId ReadObjectId(object value, string name)
        {
            try
            {
                PropertyInfo property = value.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                object result = property == null ? null : property.GetValue(value, null);
                return result is ObjectId ? (ObjectId)result : ObjectId.Null;
            }
            catch { return ObjectId.Null; }
        }

        private static bool ReadPoint(object value, string name, out Point3d point)
        {
            point = Point3d.Origin;
            try
            {
                PropertyInfo property = value.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                object result = property == null ? null : property.GetValue(value, null);
                if (result is Point3d) { point = (Point3d)result; return true; }
            }
            catch { }
            return false;
        }
    }
}
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Windows;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace CETools.Civil3D
{
    public sealed partial class DynamicTypicalDetailCommands
    {
        private static void AppendGenerated(BlockTableRecord space, Entity entity, Transaction transaction, string ownerHandle, ICollection<string> handles)
        {
            space.AppendEntity(entity);
            transaction.AddNewlyCreatedDBObject(entity, true);
            entity.CreateExtensionDictionary();
            WriteGeneratedOwner(entity, transaction, ownerHandle);
            handles.Add(entity.Handle.ToString());
        }

        private static void WriteGeneratedOwner(Entity entity, Transaction transaction, string ownerHandle)
        {
            DBDictionary dictionary = transaction.GetObject(entity.ExtensionDictionary, OpenMode.ForWrite, false) as DBDictionary;
            Xrecord record = OpenOrCreateRecord(dictionary, GeneratedRecordName, transaction);
            record.Data = new ResultBuffer(new TypedValue((int)DxfCode.Text, "Owner=" + ownerHandle));
        }

        private static void WriteBoqLink(Table table, Transaction transaction, string ownerHandle, DynamicDetailLink link, IEnumerable<QuantityItem> quantities)
        {
            if (table.ExtensionDictionary.IsNull)
                table.CreateExtensionDictionary();
            DBDictionary dictionary = transaction.GetObject(table.ExtensionDictionary, OpenMode.ForWrite, false) as DBDictionary;
            Xrecord record = OpenOrCreateRecord(dictionary, BoqLinkRecordName, transaction);
            var values = new List<TypedValue>
            {
                Text("Schema", SchemaVersion),
                Text("Owner", ownerHandle),
                Text("DetailId", link.DetailId),
                Text("DetailType", link.Parameters.DetailType),
                Text("ReviewStatus", Encode(link.Parameters.ReviewStatus)),
                Text("SourcePath", Encode(link.SourcePath)),
                Text("SourceHash", link.SourceHash)
            };
            foreach (QuantityItem item in quantities)
            {
                values.Add(Text(
                    "Item",
                    Encode(item.Key + "|" + item.Description + "|" + item.Unit + "|" +
                           item.Quantity.ToString("R", CultureInfo.InvariantCulture) + "|" +
                           item.Rate.ToString("R", CultureInfo.InvariantCulture))));
            }
            record.Data = new ResultBuffer(values.ToArray());
        }

        private static void WriteLink(Entity anchor, Transaction transaction, DynamicDetailLink link)
        {
            if (anchor.ExtensionDictionary.IsNull)
                anchor.CreateExtensionDictionary();
            DBDictionary dictionary = transaction.GetObject(anchor.ExtensionDictionary, OpenMode.ForWrite, false) as DBDictionary;
            Xrecord record = OpenOrCreateRecord(dictionary, LinkRecordName, transaction);
            var values = new List<TypedValue>
            {
                Text("Schema", link.Schema),
                Text("DetailId", link.DetailId),
                Text("InsertionX", link.InsertionPoint.X.ToString("R", CultureInfo.InvariantCulture)),
                Text("InsertionY", link.InsertionPoint.Y.ToString("R", CultureInfo.InvariantCulture)),
                Text("InsertionZ", link.InsertionPoint.Z.ToString("R", CultureInfo.InvariantCulture)),
                Text("DetailType", link.Parameters.DetailType),
                Text("WidthMm", link.Parameters.WidthMillimetres.ToString("R", CultureInfo.InvariantCulture)),
                Text("DepthMm", link.Parameters.DepthMillimetres.ToString("R", CultureInfo.InvariantCulture)),
                Text("LengthM", link.Parameters.LengthMetres.ToString("R", CultureInfo.InvariantCulture)),
                Text("WallMm", link.Parameters.WallThicknessMillimetres.ToString("R", CultureInfo.InvariantCulture)),
                Text("PipeMm", link.Parameters.PipeDiameterMillimetres.ToString("R", CultureInfo.InvariantCulture)),
                Text("BeddingMm", link.Parameters.BeddingDepthMillimetres.ToString("R", CultureInfo.InvariantCulture)),
                Text("Concrete", Encode(link.Parameters.ConcreteStrength)),
                Text("Reinforcement", Encode(link.Parameters.Reinforcement)),
                Text("Grating", Encode(link.Parameters.GratingType)),
                Text("ReviewStatus", Encode(link.Parameters.ReviewStatus)),
                Text("Reviewer", Encode(link.Parameters.Reviewer)),
                Text("ReviewedAt", link.Parameters.ReviewedAtUtc),
                Text("SourcePath", Encode(link.SourcePath)),
                Text("SourceHash", link.SourceHash),
                Text("SourceModified", link.SourceModifiedUtc),
                Text("UnitsPerMetre", link.Settings.DrawingUnitsPerMetre.ToString("R", CultureInfo.InvariantCulture)),
                Text("TextHeight", link.Settings.TextHeight.ToString("R", CultureInfo.InvariantCulture)),
                Text("DimensionOffset", link.Settings.DimensionOffset.ToString("R", CultureInfo.InvariantCulture)),
                Text("ScheduleOffset", link.Settings.ScheduleOffset.ToString("R", CultureInfo.InvariantCulture)),
                Text("DetailLayer", Encode(link.Settings.DetailLayer)),
                Text("BoqLayer", Encode(link.Settings.BoqLayer)),
                Text("BoqTable", link.BoqTableHandle)
            };
            foreach (string handle in link.GeneratedHandles.Distinct(StringComparer.OrdinalIgnoreCase))
                values.Add(Text("Generated", handle));
            record.Data = new ResultBuffer(values.ToArray());
        }

        private static DynamicDetailLink ReadLink(Entity anchor, Transaction transaction)
        {
            if (anchor == null || anchor.ExtensionDictionary.IsNull)
                throw new InvalidOperationException("The selected object has no dynamic-detail link.");
            DBDictionary dictionary = transaction.GetObject(anchor.ExtensionDictionary, OpenMode.ForRead, false) as DBDictionary;
            if (dictionary == null || !dictionary.Contains(LinkRecordName))
                throw new InvalidOperationException("The selected object is not a CE dynamic-detail anchor.");
            Xrecord record = transaction.GetObject(dictionary.GetAt(LinkRecordName), OpenMode.ForRead, false) as Xrecord;
            if (record == null || record.Data == null)
                throw new InvalidOperationException("The CE dynamic-detail link is empty.");

            var data = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (TypedValue value in record.Data.AsArray().Where(item => item.TypeCode == (int)DxfCode.Text))
            {
                string text = Convert.ToString(value.Value, CultureInfo.InvariantCulture) ?? string.Empty;
                int split = text.IndexOf('=');
                if (split <= 0)
                    continue;
                string key = text.Substring(0, split);
                string item = text.Substring(split + 1);
                List<string> values;
                if (!data.TryGetValue(key, out values))
                {
                    values = new List<string>();
                    data[key] = values;
                }
                values.Add(item);
            }

            var parameters = new DetailParameters
            {
                DetailType = Get(data, "DetailType", "TrenchDrain"),
                WidthMillimetres = GetDouble(data, "WidthMm", 1000.0),
                DepthMillimetres = GetDouble(data, "DepthMm", 1000.0),
                LengthMetres = GetDouble(data, "LengthM", 1.0),
                WallThicknessMillimetres = GetDouble(data, "WallMm", 150.0),
                PipeDiameterMillimetres = GetDouble(data, "PipeMm", 300.0),
                BeddingDepthMillimetres = GetDouble(data, "BeddingMm", 150.0),
                ConcreteStrength = Decode(Get(data, "Concrete", Encode("30 MPa"))),
                Reinforcement = Decode(Get(data, "Reinforcement", Encode("Engineer designed"))),
                GratingType = Decode(Get(data, "Grating", Encode("Heavy-duty grating / cover"))),
                ReviewStatus = Decode(Get(data, "ReviewStatus", Encode("Draft"))),
                Reviewer = Decode(Get(data, "Reviewer", string.Empty)),
                ReviewedAtUtc = Get(data, "ReviewedAt", string.Empty)
            };
            var settings = new DynamicDetailSettings
            {
                DrawingUnitsPerMetre = GetDouble(data, "UnitsPerMetre", 1000.0),
                TextHeight = GetDouble(data, "TextHeight", 100.0),
                DimensionOffset = GetDouble(data, "DimensionOffset", 300.0),
                ScheduleOffset = GetDouble(data, "ScheduleOffset", 1200.0),
                DetailLayer = Decode(Get(data, "DetailLayer", Encode(DefaultDetailLayer))),
                BoqLayer = Decode(Get(data, "BoqLayer", Encode(DefaultBoqLayer)))
            };
            settings.Normalize();
            parameters.Normalize();
            return new DynamicDetailLink(
                Get(data, "Schema", "1"),
                Get(data, "DetailId", "DD-" + anchor.Handle),
                new Point3d(GetDouble(data, "InsertionX", 0.0), GetDouble(data, "InsertionY", 0.0), GetDouble(data, "InsertionZ", 0.0)),
                parameters,
                settings,
                Decode(Get(data, "SourcePath", string.Empty)),
                Get(data, "SourceHash", string.Empty),
                Get(data, "SourceModified", string.Empty),
                GetList(data, "Generated"),
                Get(data, "BoqTable", string.Empty));
        }

        private static bool PromptLinkedDetail(Document document, out ObjectId anchorId, out DynamicDetailLink link)
        {
            anchorId = ObjectId.Null;
            link = null;
            PromptEntityResult result = document.Editor.GetEntity("\nSelect a CE dynamic detail anchor, generated geometry or linked schedule: ");
            if (result.Status != PromptStatus.OK)
                return false;
            try
            {
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    Entity selected = transaction.GetObject(result.ObjectId, OpenMode.ForRead, false) as Entity;
                    if (selected == null)
                        return false;
                    if (HasExtensionRecord(selected, transaction, LinkRecordName))
                        anchorId = selected.ObjectId;
                    else
                    {
                        string ownerHandle = ReadGeneratedOwner(selected, transaction);
                        if (string.IsNullOrWhiteSpace(ownerHandle) || !TryResolveHandle(document.Database, ownerHandle, out anchorId))
                        {
                            document.Editor.WriteMessage("\nThe selected object is not linked to a CE dynamic typical detail.");
                            return false;
                        }
                    }
                    Entity anchor = transaction.GetObject(anchorId, OpenMode.ForRead, false) as Entity;
                    link = ReadLink(anchor, transaction);
                    return true;
                }
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage("\nThe selected dynamic-detail link could not be read. " + exception.Message);
                return false;
            }
        }

        private static string ReadGeneratedOwner(Entity entity, Transaction transaction)
        {
            if (entity == null || entity.ExtensionDictionary.IsNull)
                return string.Empty;
            DBDictionary dictionary = transaction.GetObject(entity.ExtensionDictionary, OpenMode.ForRead, false) as DBDictionary;
            if (dictionary == null || !dictionary.Contains(GeneratedRecordName))
                return string.Empty;
            Xrecord record = transaction.GetObject(dictionary.GetAt(GeneratedRecordName), OpenMode.ForRead, false) as Xrecord;
            if (record == null || record.Data == null)
                return string.Empty;
            string value = record.Data.AsArray()
                .Where(item => item.TypeCode == (int)DxfCode.Text)
                .Select(item => item.Value as string)
                .FirstOrDefault(item => item != null && item.StartsWith("Owner=", StringComparison.OrdinalIgnoreCase));
            return value == null ? string.Empty : value.Substring("Owner=".Length);
        }

        private static bool HasExtensionRecord(Entity entity, Transaction transaction, string name)
        {
            if (entity == null || entity.ExtensionDictionary.IsNull)
                return false;
            DBDictionary dictionary = transaction.GetObject(entity.ExtensionDictionary, OpenMode.ForRead, false) as DBDictionary;
            return dictionary != null && dictionary.Contains(name);
        }

        private static void RemoveExtensionRecord(Entity entity, Transaction transaction, string name)
        {
            if (entity == null || entity.ExtensionDictionary.IsNull)
                return;
            DBDictionary dictionary = transaction.GetObject(entity.ExtensionDictionary, OpenMode.ForWrite, false) as DBDictionary;
            if (dictionary == null || !dictionary.Contains(name))
                return;
            DBObject record = transaction.GetObject(dictionary.GetAt(name), OpenMode.ForWrite, false);
            dictionary.Remove(name);
            record.Erase();
        }

        private static Xrecord OpenOrCreateRecord(DBDictionary dictionary, string name, Transaction transaction)
        {
            if (dictionary.Contains(name))
                return transaction.GetObject(dictionary.GetAt(name), OpenMode.ForWrite, false) as Xrecord;
            var record = new Xrecord();
            dictionary.SetAt(name, record);
            transaction.AddNewlyCreatedDBObject(record, true);
            return record;
        }

        private static ObjectId GetOrCreateLayer(Database database, Transaction transaction, string requested, string fallback)
        {
            string name = string.IsNullOrWhiteSpace(requested) ? fallback : requested.Trim();
            LayerTable layers = transaction.GetObject(database.LayerTableId, OpenMode.ForRead, false) as LayerTable;
            if (layers.Has(name))
            {
                ObjectId id = layers[name];
                LayerTableRecord existing = transaction.GetObject(id, OpenMode.ForRead, false) as LayerTableRecord;
                if (existing != null && existing.IsLocked)
                    throw new InvalidOperationException("Layer '" + name + "' is locked.");
                return id;
            }
            layers.UpgradeOpen();
            var layer = new LayerTableRecord { Name = name };
            ObjectId layerId = layers.Add(layer);
            transaction.AddNewlyCreatedDBObject(layer, true);
            return layerId;
        }

        private static bool PromptNewParameters(Editor editor, out DetailParameters parameters)
        {
            parameters = new DetailParameters();
            var options = new PromptKeywordOptions("\nDynamic detail type [TrenchDrain/PipeTrench/ValveChamber/Kerb/Headwall] <TrenchDrain>: ") { AllowNone = true };
            foreach (string type in SupportedTypes)
                options.Keywords.Add(type);
            PromptResult result = editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel)
                return false;
            parameters.DetailType = result.Status == PromptStatus.OK ? result.StringResult : "TrenchDrain";
            return PromptEditableParameters(editor, parameters);
        }

        private static bool PromptEditableParameters(Editor editor, DetailParameters parameters)
        {
            if (!PromptPositiveDouble(editor, "Overall width in millimetres", parameters.WidthMillimetres, out parameters.WidthMillimetres)) return false;
            if (!PromptPositiveDouble(editor, "Overall depth/height in millimetres", parameters.DepthMillimetres, out parameters.DepthMillimetres)) return false;
            string lengthLabel = parameters.DetailType.Equals("ValveChamber", StringComparison.OrdinalIgnoreCase)
                ? "Plan length in metres"
                : parameters.DetailType.Equals("Headwall", StringComparison.OrdinalIgnoreCase)
                    ? "Headwall plan thickness in metres"
                    : "Scheduled detail length in metres";
            if (!PromptPositiveDouble(editor, lengthLabel, parameters.LengthMetres, out parameters.LengthMetres)) return false;
            if (!PromptPositiveDouble(editor, "Wall/base/slab thickness in millimetres", parameters.WallThicknessMillimetres, out parameters.WallThicknessMillimetres)) return false;
            if (!PromptPositiveDouble(editor, "Pipe diameter in millimetres", parameters.PipeDiameterMillimetres, out parameters.PipeDiameterMillimetres)) return false;
            if (!PromptPositiveDouble(editor, "Bedding depth in millimetres", parameters.BeddingDepthMillimetres, out parameters.BeddingDepthMillimetres)) return false;
            if (!PromptText(editor, "Concrete strength/specification", parameters.ConcreteStrength, out parameters.ConcreteStrength)) return false;
            if (!PromptText(editor, "Reinforcement specification", parameters.Reinforcement, out parameters.Reinforcement)) return false;
            if (!PromptText(editor, "Grating/cover type", parameters.GratingType, out parameters.GratingType)) return false;
            parameters.Normalize();
            return true;
        }

        private static string PromptOptionalSourceTemplate(Editor editor)
        {
            var options = new PromptKeywordOptions("\nReference an approved source DWG template [Select/None] <None>: ") { AllowNone = true };
            options.Keywords.Add("Select");
            options.Keywords.Add("None");
            PromptResult result = editor.GetKeywords(options);
            if (result.Status != PromptStatus.OK || !result.StringResult.Equals("Select", StringComparison.OrdinalIgnoreCase))
                return string.Empty;
            var dialog = new OpenFileDialog(
                "Select approved source DWG template (read-only identity reference)",
                string.Empty,
                "dwg",
                "CE_DETAILPARAMCREATE",
                OpenFileDialog.OpenFileDialogFlags.DoNotTransferRemoteFiles);
            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                return string.Empty;
            return Path.GetFullPath(dialog.Filename);
        }

        private static void WritePreview(Editor editor, DetailParameters parameters, string sourcePath, DynamicDetailSettings settings)
        {
            editor.WriteMessage(
                "\nCE dynamic-detail preview" +
                "\n  Type: " + DisplayType(parameters.DetailType) +
                "\n  Width x depth/height: " + parameters.WidthMillimetres.ToString("0.###", CultureInfo.InvariantCulture) + " x " + parameters.DepthMillimetres.ToString("0.###", CultureInfo.InvariantCulture) + " mm" +
                "\n  Length / plan thickness: " + parameters.LengthMetres.ToString("0.###", CultureInfo.InvariantCulture) + " m" +
                "\n  Wall/base/slab thickness: " + parameters.WallThicknessMillimetres.ToString("0.###", CultureInfo.InvariantCulture) + " mm" +
                "\n  Pipe diameter: " + parameters.PipeDiameterMillimetres.ToString("0.###", CultureInfo.InvariantCulture) + " mm" +
                "\n  Bedding: " + parameters.BeddingDepthMillimetres.ToString("0.###", CultureInfo.InvariantCulture) + " mm" +
                "\n  Concrete: " + parameters.ConcreteStrength +
                "\n  Reinforcement: " + parameters.Reinforcement +
                "\n  Grating/cover: " + parameters.GratingType +
                "\n  Source template: " + (string.IsNullOrWhiteSpace(sourcePath) ? "<None / built-in schematic>" : sourcePath) +
                "\n  Drawing units per metre: " + settings.DrawingUnitsPerMetre.ToString("0.###", CultureInfo.InvariantCulture) +
                "\n  Source templates remain external/read-only. Generated geometry and quantities require engineer/authority review.");
        }

        private static List<ObjectId> PromptAnchorSelection(Document document)
        {
            var ids = new List<ObjectId>();
            var options = new PromptSelectionOptions
            {
                MessageForAdding = "\nSelect CE dynamic-detail anchors, generated geometry or linked schedules: "
            };
            PromptSelectionResult selection = document.Editor.GetSelection(options);
            if (selection.Status != PromptStatus.OK)
                return ids;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject item in selection.Value)
                {
                    if (item == null)
                        continue;
                    Entity entity = transaction.GetObject(item.ObjectId, OpenMode.ForRead, false) as Entity;
                    if (entity == null)
                        continue;
                    if (HasExtensionRecord(entity, transaction, LinkRecordName))
                    {
                        ids.Add(item.ObjectId);
                        continue;
                    }
                    string ownerHandle = ReadGeneratedOwner(entity, transaction);
                    ObjectId ownerId;
                    if (!string.IsNullOrWhiteSpace(ownerHandle) &&
                        TryResolveHandle(document.Database, ownerHandle, out ownerId))
                        ids.Add(ownerId);
                }
            }
            return ids.Distinct().ToList();
        }

        private static List<ObjectId> FindAnchorsInCurrentSpace(Database database)
        {
            var ids = new List<ObjectId>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = transaction.GetObject(database.CurrentSpaceId, OpenMode.ForRead, false) as BlockTableRecord;
                foreach (ObjectId id in space)
                {
                    Entity entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                    if (entity != null && HasExtensionRecord(entity, transaction, LinkRecordName))
                        ids.Add(id);
                }
            }
            return ids;
        }

        private static int CountGenerated(Database database, IEnumerable<ObjectId> anchors)
        {
            int count = 0;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in anchors)
                {
                    Entity anchor = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                    if (anchor == null || !HasExtensionRecord(anchor, transaction, LinkRecordName))
                        continue;
                    count += ReadLink(anchor, transaction).GeneratedHandles.Count;
                }
            }
            return count;
        }

        private static void ResetReview(DetailParameters parameters)
        {
            parameters.ReviewStatus = "Draft";
            parameters.Reviewer = string.Empty;
            parameters.ReviewedAtUtc = string.Empty;
        }

        private static void ValidateSection(double width, double depth, double wall)
        {
            if (width <= GeometryTolerance || depth <= GeometryTolerance || wall <= GeometryTolerance)
                throw new InvalidOperationException("Width, depth and wall thickness must be positive.");
            if (width <= 2.0 * wall || depth <= 2.0 * wall)
                throw new InvalidOperationException("Overall width/depth must exceed twice the wall thickness.");
        }

        private static double ToDrawingUnits(double millimetres, DynamicDetailSettings settings)
        {
            return millimetres * settings.DrawingUnitsPerMetre / 1000.0;
        }

        private static string DisplayType(string value)
        {
            if (value.Equals("TrenchDrain", StringComparison.OrdinalIgnoreCase)) return "Trench Drain";
            if (value.Equals("PipeTrench", StringComparison.OrdinalIgnoreCase)) return "Pipe Trench";
            if (value.Equals("ValveChamber", StringComparison.OrdinalIgnoreCase)) return "Valve Chamber";
            if (value.Equals("Kerb", StringComparison.OrdinalIgnoreCase)) return "Kerb";
            if (value.Equals("Headwall", StringComparison.OrdinalIgnoreCase)) return "Headwall";
            return value ?? string.Empty;
        }

        private static string StatusKeyword(string status)
        {
            if (status == null) return "Draft";
            if (status.StartsWith("Approved", StringComparison.OrdinalIgnoreCase)) return "ApprovedRecord";
            if (status.Equals("For Review", StringComparison.OrdinalIgnoreCase)) return "ForReview";
            if (status.Equals("Reviewed", StringComparison.OrdinalIgnoreCase)) return "Reviewed";
            return "Draft";
        }

        private static string ComputeSha256(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return string.Empty;
            try
            {
                using (FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (SHA256 algorithm = SHA256.Create())
                {
                    return string.Concat(algorithm.ComputeHash(stream).Select(value => value.ToString("x2", CultureInfo.InvariantCulture)));
                }
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string ReadSourceModifiedUtc(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return string.Empty;
            try
            {
                return File.GetLastWriteTimeUtc(path).ToString("o", CultureInfo.InvariantCulture);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static bool TryResolveHandle(Database database, string handleText, out ObjectId objectId)
        {
            objectId = ObjectId.Null;
            long value;
            if (!long.TryParse(handleText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value))
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

        private static TypedValue Text(string key, string value)
        {
            return new TypedValue((int)DxfCode.Text, key + "=" + (value ?? string.Empty));
        }

        private static string Get(IDictionary<string, List<string>> data, string key, string fallback)
        {
            List<string> values;
            return data.TryGetValue(key, out values) && values.Count > 0 ? values[0] : fallback;
        }

        private static List<string> GetList(IDictionary<string, List<string>> data, string key)
        {
            List<string> values;
            return data.TryGetValue(key, out values) ? new List<string>(values) : new List<string>();
        }

        private static double GetDouble(IDictionary<string, List<string>> data, string key, double fallback)
        {
            double value;
            return double.TryParse(Get(data, key, string.Empty), NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value : fallback;
        }

        private static string Encode(string value)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? string.Empty));
        }

        private static string Decode(string value)
        {
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(value ?? string.Empty)); }
            catch { return string.Empty; }
        }

        private static KeyValuePair<string, string> Pair(string key, string value)
        {
            return new KeyValuePair<string, string>(key, value ?? string.Empty);
        }

        private static IList<string> Row(string key, string value)
        {
            return new List<string> { key, value ?? string.Empty };
        }

        private static bool PromptText(Editor editor, string label, string current, out string value)
        {
            var options = new PromptStringOptions("\n" + label + " <" + (current ?? string.Empty) + ">: ") { AllowSpaces = true };
            PromptResult result = editor.GetString(options);
            if (result.Status == PromptStatus.Cancel)
            {
                value = current;
                return false;
            }
            value = result.Status == PromptStatus.None ? current : result.StringResult.Trim();
            return true;
        }

        private static bool PromptPositiveDouble(Editor editor, string label, double current, out double value)
        {
            var options = new PromptDoubleOptions("\n" + label + " <" + current.ToString("0.###", CultureInfo.InvariantCulture) + ">: ")
            {
                AllowNegative = false,
                AllowZero = false,
                UseDefaultValue = true,
                DefaultValue = current
            };
            PromptDoubleResult result = editor.GetDouble(options);
            value = result.Status == PromptStatus.OK ? result.Value : current;
            return result.Status == PromptStatus.OK;
        }

        private static bool Confirm(Editor editor, string message)
        {
            var options = new PromptKeywordOptions("\n" + message + "? [Yes/No] <No>: ") { AllowNone = true };
            options.Keywords.Add("Yes");
            options.Keywords.Add("No");
            PromptResult result = editor.GetKeywords(options);
            return result.Status == PromptStatus.OK && result.StringResult.Equals("Yes", StringComparison.OrdinalIgnoreCase);
        }

        private static Document ActiveDocument()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
        }

        private sealed class GeneratedSet
        {
            public GeneratedSet(List<string> handles, string boqTableHandle)
            {
                Handles = handles;
                BoqTableHandle = boqTableHandle;
            }
            public List<string> Handles { get; private set; }
            public string BoqTableHandle { get; private set; }
        }

        private sealed class QuantityItem
        {
            public QuantityItem(string key, string description, string unit, double quantity, double rate)
            {
                Key = key;
                Description = description;
                Unit = unit;
                Quantity = quantity;
                Rate = rate;
            }
            public string Key { get; private set; }
            public string Description { get; private set; }
            public string Unit { get; private set; }
            public double Quantity { get; private set; }
            public double Rate { get; private set; }
            public double Amount { get { return Quantity * Rate; } }
        }

        private sealed class DetailParameters
        {
            public string DetailType = "TrenchDrain";
            public double WidthMillimetres = 1000.0;
            public double DepthMillimetres = 1000.0;
            public double LengthMetres = 1.0;
            public double WallThicknessMillimetres = 150.0;
            public double PipeDiameterMillimetres = 300.0;
            public double BeddingDepthMillimetres = 150.0;
            public string ConcreteStrength = "30 MPa";
            public string Reinforcement = "Engineer designed";
            public string GratingType = "Heavy-duty grating / cover";
            public string ReviewStatus = "Draft";
            public string Reviewer = string.Empty;
            public string ReviewedAtUtc = string.Empty;

            public DetailParameters Clone()
            {
                return (DetailParameters)MemberwiseClone();
            }

            public void Normalize()
            {
                if (!SupportedTypes.Contains(DetailType, StringComparer.OrdinalIgnoreCase)) DetailType = "TrenchDrain";
                if (WidthMillimetres <= 0.0) WidthMillimetres = 1000.0;
                if (DepthMillimetres <= 0.0) DepthMillimetres = 1000.0;
                if (LengthMetres <= 0.0) LengthMetres = 1.0;
                if (WallThicknessMillimetres <= 0.0) WallThicknessMillimetres = 150.0;
                if (PipeDiameterMillimetres <= 0.0) PipeDiameterMillimetres = 300.0;
                if (BeddingDepthMillimetres <= 0.0) BeddingDepthMillimetres = 150.0;
                if (ConcreteStrength == null) ConcreteStrength = string.Empty;
                if (Reinforcement == null) Reinforcement = string.Empty;
                if (GratingType == null) GratingType = string.Empty;
                if (string.IsNullOrWhiteSpace(ReviewStatus)) ReviewStatus = "Draft";
                if (Reviewer == null) Reviewer = string.Empty;
                if (ReviewedAtUtc == null) ReviewedAtUtc = string.Empty;
            }
        }

        private sealed class DynamicDetailSettings
        {
            public double DrawingUnitsPerMetre = 1000.0;
            public double TextHeight = 100.0;
            public double DimensionOffset = 300.0;
            public double ScheduleOffset = 1200.0;
            public string DetailLayer = DefaultDetailLayer;
            public string BoqLayer = DefaultBoqLayer;

            public static DynamicDetailSettings Read(Database database)
            {
                var settings = new DynamicDetailSettings();
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    DBDictionary nod = transaction.GetObject(database.NamedObjectsDictionaryId, OpenMode.ForRead, false) as DBDictionary;
                    if (nod == null || !nod.Contains(CeDictionaryName))
                        return settings;
                    DBDictionary ce = transaction.GetObject(nod.GetAt(CeDictionaryName), OpenMode.ForRead, false) as DBDictionary;
                    if (ce == null || !ce.Contains(SettingsRecordName))
                        return settings;
                    Xrecord record = transaction.GetObject(ce.GetAt(SettingsRecordName), OpenMode.ForRead, false) as Xrecord;
                    string[] values = record == null || record.Data == null
                        ? new string[0]
                        : record.Data.AsArray()
                            .Where(value => value.TypeCode == (int)DxfCode.Text)
                            .Select(value => Convert.ToString(value.Value, CultureInfo.InvariantCulture))
                            .ToArray();
                    if (values.Length >= 6)
                    {
                        double.TryParse(values[0], NumberStyles.Float, CultureInfo.InvariantCulture, out settings.DrawingUnitsPerMetre);
                        double.TryParse(values[1], NumberStyles.Float, CultureInfo.InvariantCulture, out settings.TextHeight);
                        double.TryParse(values[2], NumberStyles.Float, CultureInfo.InvariantCulture, out settings.DimensionOffset);
                        double.TryParse(values[3], NumberStyles.Float, CultureInfo.InvariantCulture, out settings.ScheduleOffset);
                        settings.DetailLayer = values[4];
                        settings.BoqLayer = values[5];
                    }
                }
                settings.Normalize();
                return settings;
            }

            public void Write(Database database)
            {
                Normalize();
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    DBDictionary nod = transaction.GetObject(database.NamedObjectsDictionaryId, OpenMode.ForWrite, false) as DBDictionary;
                    DBDictionary ce;
                    if (nod.Contains(CeDictionaryName))
                        ce = transaction.GetObject(nod.GetAt(CeDictionaryName), OpenMode.ForWrite, false) as DBDictionary;
                    else
                    {
                        ce = new DBDictionary();
                        nod.SetAt(CeDictionaryName, ce);
                        transaction.AddNewlyCreatedDBObject(ce, true);
                    }
                    Xrecord record = OpenOrCreateRecord(ce, SettingsRecordName, transaction);
                    string[] values =
                    {
                        DrawingUnitsPerMetre.ToString("R", CultureInfo.InvariantCulture),
                        TextHeight.ToString("R", CultureInfo.InvariantCulture),
                        DimensionOffset.ToString("R", CultureInfo.InvariantCulture),
                        ScheduleOffset.ToString("R", CultureInfo.InvariantCulture),
                        DetailLayer,
                        BoqLayer
                    };
                    record.Data = new ResultBuffer(values.Select(value => new TypedValue((int)DxfCode.Text, value)).ToArray());
                    transaction.Commit();
                }
            }

            public void Normalize()
            {
                if (DrawingUnitsPerMetre <= 0.0) DrawingUnitsPerMetre = 1000.0;
                if (TextHeight <= 0.0) TextHeight = DrawingUnitsPerMetre * 0.1;
                if (DimensionOffset <= 0.0) DimensionOffset = DrawingUnitsPerMetre * 0.3;
                if (ScheduleOffset <= 0.0) ScheduleOffset = DrawingUnitsPerMetre * 1.2;
                if (string.IsNullOrWhiteSpace(DetailLayer)) DetailLayer = DefaultDetailLayer;
                if (string.IsNullOrWhiteSpace(BoqLayer)) BoqLayer = DefaultBoqLayer;
            }
        }

        private sealed class DynamicDetailLink
        {
            public DynamicDetailLink(
                string schema,
                string detailId,
                Point3d insertionPoint,
                DetailParameters parameters,
                DynamicDetailSettings settings,
                string sourcePath,
                string sourceHash,
                string sourceModifiedUtc,
                IEnumerable<string> generatedHandles,
                string boqTableHandle)
            {
                Schema = schema ?? SchemaVersion;
                DetailId = detailId ?? string.Empty;
                InsertionPoint = insertionPoint;
                Parameters = parameters ?? new DetailParameters();
                Settings = settings ?? new DynamicDetailSettings();
                SourcePath = sourcePath ?? string.Empty;
                SourceHash = sourceHash ?? string.Empty;
                SourceModifiedUtc = sourceModifiedUtc ?? string.Empty;
                GeneratedHandles = generatedHandles == null
                    ? new List<string>()
                    : generatedHandles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                BoqTableHandle = boqTableHandle ?? string.Empty;
            }

            public string Schema { get; private set; }
            public string DetailId { get; private set; }
            public Point3d InsertionPoint { get; private set; }
            public DetailParameters Parameters { get; private set; }
            public DynamicDetailSettings Settings { get; private set; }
            public string SourcePath { get; private set; }
            public string SourceHash { get; private set; }
            public string SourceModifiedUtc { get; private set; }
            public List<string> GeneratedHandles { get; private set; }
            public string BoqTableHandle { get; private set; }

            public DynamicDetailLink WithParameters(DetailParameters parameters)
            {
                return new DynamicDetailLink(
                    Schema, DetailId, InsertionPoint, parameters, Settings,
                    SourcePath, SourceHash, SourceModifiedUtc, GeneratedHandles, BoqTableHandle);
            }

            public DynamicDetailLink WithGenerated(IEnumerable<string> handles, string boqTableHandle)
            {
                return new DynamicDetailLink(
                    SchemaVersion, DetailId, InsertionPoint, Parameters, Settings,
                    SourcePath, SourceHash, SourceModifiedUtc, handles, boqTableHandle);
            }
        }
    }
}

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

[assembly: CommandClass(typeof(CETools.Civil3D.AugustBehaviorCompletionCommands))]

namespace CETools.Civil3D
{
    public sealed class AugustBehaviorCompletionCommands
    {
        [CommandMethod("CE_TOOLS", "CE_JUNCTIONRETURNTYPE", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void JunctionReturnType()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Junction Return Geometry",
                "Convert generated CE T/cross-junction bellmouth return arcs to true lightweight-polyline arc segments while preserving CE link records and setting-out grouping.");
            model.AddChoice("Scope", "Geometry", "Junction returns", "All", "Convert all generated CE junction returns or only selected arcs.", new[] { "All", "Selected" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;
            HashSet<ObjectId> selected = null;
            if (string.Equals(model.Text("Scope"), "Selected", StringComparison.OrdinalIgnoreCase))
            {
                PromptSelectionResult selection = document.Editor.GetSelection(new PromptSelectionOptions { MessageForAdding = "\nSelect CE junction return arcs to convert: ", AllowDuplicates = false });
                if (selection.Status != PromptStatus.OK || selection.Value == null) return;
                selected = new HashSet<ObjectId>(selection.Value.GetObjectIds());
            }
            int converted = AugustJunctionReturnRuntime.ConvertGenerated(document, selected);
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_JUNCTIONRETURNTYPE complete. Junction return polylines={0}.", converted);
        }

        [CommandMethod("CE_TOOLS", "CE_SWSEQPRODUCTION", CommandFlags.Modal)]
        public void StormwaterSequenceProduction() { QueueUtilityProduction("Stormwater", "CE_SWSEQ ", "CE_SWALIGN ", "CE_SWPROFILE "); }

        [CommandMethod("CE_TOOLS", "CE_WATERSEQPRODUCTION", CommandFlags.Modal)]
        public void WaterSequenceProduction() { QueueUtilityProduction("Water", "CE_WATERSEQ ", "CE_WATERALIGN ", "CE_WATERPROFILE "); }

        [CommandMethod("CE_TOOLS", "CE_ASSEMBLYMARKERS", CommandFlags.Modal | CommandFlags.Redraw)]
        public void AssemblyMarkers()
        {
            Document document = ActiveDocument();
            CivilDocument civil = CivilApplication.ActiveDocument;
            if (document == null || civil == null) return;
            int count = AugustAssemblyVisibility.EnsureAllMarkers(document, civil);
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_ASSEMBLYMARKERS complete. Visible assembly location markers={0}.", count);
        }

        private static void QueueUtilityProduction(string discipline, string sequence, string align, string profile)
        {
            Document document = ActiveDocument();
            if (document == null) return;
            var model = new ProductionSettingsDialogModel(
                "CE Tools - " + discipline + " Sequence + Production",
                "Sequence the network and automatically continue into alignment production. Profile creation is optional because profile-view placement may still require a drawing pick.");
            model.AddChoice("Profiles", "Production", "Create profiles after alignments", "No", "Queue the discipline profile command after sequencing/alignment.", new[] { "No", "Yes" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;
            string command = sequence + align;
            if (string.Equals(model.Text("Profiles"), "Yes", StringComparison.OrdinalIgnoreCase)) command += profile;
            document.Editor.WriteMessage("\nCE {0} production queued: sequence -> alignments{1}.", discipline, model.Text("Profiles") == "Yes" ? " -> profiles" : string.Empty);
            document.SendStringToExecute(command, true, false, true);
        }

        private static Document ActiveDocument() { return AcApplication.DocumentManager.MdiActiveDocument; }
    }

    internal static class AugustJunctionReturnRuntime
    {
        private const string RoadLinkKey = "CE_ROAD_LAYOUT";

        internal static int ConvertGenerated(Document document, ISet<ObjectId> restricted)
        {
            if (document == null) return 0;
            int converted = 0;
            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(document.Database), OpenMode.ForWrite, false) as BlockTableRecord;
                if (space == null) return 0;
                List<ObjectId> ids = space.Cast<ObjectId>().ToList();
                foreach (ObjectId id in ids)
                {
                    if (restricted != null && !restricted.Contains(id)) continue;
                    Arc arc;
                    try { arc = transaction.GetObject(id, OpenMode.ForWrite, false) as Arc; } catch { continue; }
                    if (arc == null || arc.IsErased || !IsGeneratedReturn(arc, transaction)) continue;
                    double sweep = arc.EndAngle - arc.StartAngle;
                    while (sweep <= 0.0) sweep += Math.PI * 2.0;
                    if (sweep >= Math.PI * 2.0 - 1e-8) continue;
                    double bulge = Math.Tan(sweep / 4.0) * (arc.Normal.Z < 0.0 ? -1.0 : 1.0);
                    var polyline = new Polyline(2);
                    polyline.SetDatabaseDefaults(document.Database);
                    polyline.LayerId = arc.LayerId;
                    polyline.Color = arc.Color;
                    polyline.LinetypeId = arc.LinetypeId;
                    polyline.LineWeight = arc.LineWeight;
                    polyline.AddVertexAt(0, new Point2d(arc.StartPoint.X, arc.StartPoint.Y), bulge, 0.0, 0.0);
                    polyline.AddVertexAt(1, new Point2d(arc.EndPoint.X, arc.EndPoint.Y), 0.0, 0.0, 0.0);
                    polyline.Elevation = arc.StartPoint.Z;
                    space.AppendEntity(polyline);
                    transaction.AddNewlyCreatedDBObject(polyline, true);
                    CopyExtensionRecords(arc, polyline, transaction);
                    arc.Erase();
                    converted++;
                }
                transaction.Commit();
            }
            return converted;
        }

        private static bool IsGeneratedReturn(Arc arc, Transaction transaction)
        {
            if (arc == null) return false;
            if (string.Equals(arc.Layer, "CE-ROAD-JUNCTION", StringComparison.OrdinalIgnoreCase)) return true;
            if (arc.ExtensionDictionary.IsNull) return false;
            try
            {
                DBDictionary dictionary = transaction.GetObject(arc.ExtensionDictionary, OpenMode.ForRead, false) as DBDictionary;
                return dictionary != null && dictionary.Contains(RoadLinkKey);
            }
            catch { return false; }
        }

        private static void CopyExtensionRecords(DBObject source, DBObject target, Transaction transaction)
        {
            if (source == null || target == null || source.ExtensionDictionary.IsNull) return;
            DBDictionary sourceDictionary;
            try { sourceDictionary = transaction.GetObject(source.ExtensionDictionary, OpenMode.ForRead, false) as DBDictionary; } catch { return; }
            if (sourceDictionary == null) return;
            if (target.ExtensionDictionary.IsNull) target.CreateExtensionDictionary();
            DBDictionary targetDictionary = transaction.GetObject(target.ExtensionDictionary, OpenMode.ForWrite, false) as DBDictionary;
            foreach (DBDictionaryEntry entry in sourceDictionary)
            {
                Xrecord sourceRecord;
                try { sourceRecord = transaction.GetObject(entry.Value, OpenMode.ForRead, false) as Xrecord; } catch { continue; }
                if (sourceRecord == null || sourceRecord.Data == null) continue;
                var record = new Xrecord { Data = new ResultBuffer(sourceRecord.Data.AsArray()) };
                if (targetDictionary.Contains(entry.Key))
                {
                    Xrecord existing = transaction.GetObject(targetDictionary.GetAt(entry.Key), OpenMode.ForWrite, false) as Xrecord;
                    if (existing != null) existing.Data = new ResultBuffer(sourceRecord.Data.AsArray());
                }
                else
                {
                    targetDictionary.SetAt(entry.Key, record);
                    transaction.AddNewlyCreatedDBObject(record, true);
                }
            }
        }
    }

    internal static class AugustAssemblyVisibility
    {
        private const string MarkerKey = "CE_ASSEMBLY_VISIBLE_MARKER";

        internal static void EnsureMarker(Document document, ObjectId assemblyId, Point3d point)
        {
            if (document == null || assemblyId.IsNull) return;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                if (MarkerExists(document.Database, transaction, assemblyId.Handle.ToString())) return;
                BlockTableRecord space = transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(document.Database), OpenMode.ForWrite, false) as BlockTableRecord;
                if (space == null) return;
                ObjectId layerId = EnsureLayer(document.Database, transaction, "CE-ASSEMBLY-MARKER");
                double radius = Math.Max(PaperAnnotationScale.ModelDistance(document.Database, 5.0), 1.0);
                var circle = new Circle(point, Vector3d.ZAxis, radius);
                circle.SetDatabaseDefaults(document.Database);
                circle.LayerId = layerId;
                space.AppendEntity(circle);
                transaction.AddNewlyCreatedDBObject(circle, true);
                WriteMarker(circle, transaction, assemblyId.Handle.ToString());
                var text = new MText();
                text.SetDatabaseDefaults(document.Database);
                text.LayerId = layerId;
                text.Location = point + new Vector3d(radius * 1.4, radius * 1.4, 0.0);
                text.TextHeight = Math.Max(PaperAnnotationScale.ModelTextHeight(document.Database, 2.5), 0.1);
                text.Contents = "CE ROAD ASSEMBLY\\P" + ReadName(transaction, assemblyId) + "\\PAdd/verify roadway subassemblies in Tool Palettes.";
                space.AppendEntity(text);
                transaction.AddNewlyCreatedDBObject(text, true);
                WriteMarker(text, transaction, assemblyId.Handle.ToString());
                transaction.Commit();
            }
        }

        internal static int EnsureAllMarkers(Document document, CivilDocument civil)
        {
            if (document == null || civil == null) return 0;
            List<ObjectId> ids = new List<ObjectId>();
            object collection = ReadProperty(civil, "AssemblyCollection") ?? ReadProperty(civil, "Assemblies");
            foreach (object value in CivilStyleDiscovery.Enumerate(collection))
            {
                if (value is ObjectId) ids.Add((ObjectId)value);
                else if (value is DBObject) ids.Add(((DBObject)value).ObjectId);
            }
            int count = 0;
            foreach (ObjectId id in ids.Where(value => !value.IsNull && !value.IsErased))
            {
                Point3d point = Point3d.Origin;
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    DBObject assembly;
                    try { assembly = transaction.GetObject(id, OpenMode.ForRead, false); } catch { continue; }
                    point = ReadPoint(assembly, "Location", "InsertionPoint", "Origin");
                }
                EnsureMarker(document, id, point);
                count++;
            }
            return count;
        }

        private static bool MarkerExists(Database database, Transaction transaction, string handle)
        {
            BlockTableRecord space = transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(database), OpenMode.ForRead, false) as BlockTableRecord;
            if (space == null) return false;
            foreach (ObjectId id in space)
            {
                Entity entity;
                try { entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity; } catch { continue; }
                if (entity == null || entity.ExtensionDictionary.IsNull) continue;
                try
                {
                    DBDictionary dictionary = transaction.GetObject(entity.ExtensionDictionary, OpenMode.ForRead, false) as DBDictionary;
                    if (dictionary == null || !dictionary.Contains(MarkerKey)) continue;
                    Xrecord record = transaction.GetObject(dictionary.GetAt(MarkerKey), OpenMode.ForRead, false) as Xrecord;
                    TypedValue[] values = record == null || record.Data == null ? null : record.Data.AsArray();
                    if (values != null && values.Length > 0 && string.Equals(Convert.ToString(values[0].Value, CultureInfo.InvariantCulture), handle, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch { }
            }
            return false;
        }

        private static void WriteMarker(Entity entity, Transaction transaction, string handle)
        {
            if (entity.ExtensionDictionary.IsNull) entity.CreateExtensionDictionary();
            DBDictionary dictionary = transaction.GetObject(entity.ExtensionDictionary, OpenMode.ForWrite, false) as DBDictionary;
            var record = new Xrecord { Data = new ResultBuffer(new TypedValue((int)DxfCode.Text, handle)) };
            dictionary.SetAt(MarkerKey, record);
            transaction.AddNewlyCreatedDBObject(record, true);
        }

        private static ObjectId EnsureLayer(Database database, Transaction transaction, string name)
        {
            LayerTable layers = transaction.GetObject(database.LayerTableId, OpenMode.ForRead, false) as LayerTable;
            if (layers.Has(name)) return layers[name];
            layers.UpgradeOpen();
            var layer = new LayerTableRecord { Name = name };
            ObjectId id = layers.Add(layer);
            transaction.AddNewlyCreatedDBObject(layer, true);
            return id;
        }

        private static string ReadName(Transaction transaction, ObjectId id)
        {
            try
            {
                DBObject value = transaction.GetObject(id, OpenMode.ForRead, false);
                PropertyInfo property = value.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
                return property == null ? id.Handle.ToString() : Convert.ToString(property.GetValue(value, null), CultureInfo.CurrentCulture);
            }
            catch { return id.Handle.ToString(); }
        }

        private static object ReadProperty(object value, string name)
        {
            if (value == null) return null;
            try { PropertyInfo property = value.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance); return property == null ? null : property.GetValue(value, null); }
            catch { return null; }
        }

        private static Point3d ReadPoint(object value, params string[] names)
        {
            foreach (string name in names)
            {
                try
                {
                    PropertyInfo property = value.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                    object point = property == null ? null : property.GetValue(value, null);
                    if (point is Point3d) return (Point3d)point;
                }
                catch { }
            }
            return Point3d.Origin;
        }
    }
}

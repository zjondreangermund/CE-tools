using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilNetwork = Autodesk.Civil.DatabaseServices.Network;

[assembly: CommandClass(typeof(CETools.Civil3D.SewerNetworkLabelCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Adds the drawing's Civil 3D plan pipe and structure labels after sewer
    /// sequencing. Reflection keeps the creation calls compatible with the
    /// slightly different Civil 3D 2023/2024 label overloads.
    /// </summary>
    public sealed class SewerNetworkLabelCommands
    {
        [CommandMethod("CE_SEWLABELS", CommandFlags.Modal | CommandFlags.Redraw)]
        public void CreateMissingPlanLabels()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (document == null || civilDocument == null) return;

            SewerNetworkLabelResult result = EnsureLabels(
                document,
                civilDocument.GetPipeNetworkIds().Cast<ObjectId>());
            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_SEWLABELS complete. Pipe labels added={0}; structure labels added={1}; existing labels kept={2}; skipped={3}.{4}",
                result.PipeLabelsAdded,
                result.StructureLabelsAdded,
                result.ExistingLabels,
                result.Skipped,
                string.IsNullOrWhiteSpace(result.Warning)
                    ? string.Empty
                    : " " + result.Warning);
        }

        internal static IList<string> ReadPipeLabelStyleNames(Document document)
        {
            return ReadLabelStyleNames(document, true);
        }

        internal static IList<string> ReadStructureLabelStyleNames(Document document)
        {
            return ReadLabelStyleNames(document, false);
        }

        internal static SewerNetworkLabelResult EnsureLabels(
            Document document,
            IEnumerable<ObjectId> networkIds)
        {
            try
            {
                return EnsureLabelsCore(document, networkIds);
            }
            catch (System.Exception exception)
            {
                return new SewerNetworkLabelResult
                {
                    Warning = "Automatic Civil label creation was skipped: " +
                        exception.Message
                };
            }
        }

        private static SewerNetworkLabelResult EnsureLabelsCore(
            Document document,
            IEnumerable<ObjectId> networkIds)
        {
            var result = new SewerNetworkLabelResult();
            if (document == null || networkIds == null) return result;

            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (civilDocument == null)
            {
                result.Warning = "The active Civil 3D document was unavailable.";
                return result;
            }

            SewerProductionSettings settings = SewerProductionSettings.Read(document.Database);
            object pipeStyles = ReadLabelStyleCollection(civilDocument, true);
            object structureStyles = ReadLabelStyleCollection(civilDocument, false);

            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                ObjectId pipeStyleId = ResolveStyleId(
                    pipeStyles, settings.PipePlanLabelStyle, transaction);
                ObjectId structureStyleId = ResolveStyleId(
                    structureStyles, settings.StructurePlanLabelStyle, transaction);
                if (pipeStyleId.IsNull || structureStyleId.IsNull)
                {
                    result.Warning = "Choose compatible pipe and structure plan-label styles in Sewer Settings.";
                    return result;
                }

                HashSet<ObjectId> labelledParts = ReadExistingLabelledParts(
                    document.Database, transaction);
                Type pipeLabelType = FindCivilType("PipeLabel");
                Type structureLabelType = FindCivilType("StructureLabel");

                foreach (ObjectId networkId in networkIds
                    .Where(id => !id.IsNull && !id.IsErased)
                    .Distinct())
                {
                    CivilNetwork network;
                    try
                    {
                        network = transaction.GetObject(
                            networkId, OpenMode.ForRead, false) as CivilNetwork;
                    }
                    catch
                    {
                        result.Skipped++;
                        continue;
                    }
                    if (network == null || network.IsReferenceObject) continue;

                    foreach (ObjectId pipeId in network.GetPipeIds())
                    {
                        if (labelledParts.Contains(pipeId))
                        {
                            result.ExistingLabels++;
                            continue;
                        }
                        if (TryCreateLabel(pipeLabelType, pipeId, pipeStyleId, transaction))
                        {
                            result.PipeLabelsAdded++;
                            labelledParts.Add(pipeId);
                        }
                        else result.Skipped++;
                    }

                    foreach (ObjectId structureId in network.GetStructureIds())
                    {
                        if (labelledParts.Contains(structureId))
                        {
                            result.ExistingLabels++;
                            continue;
                        }
                        if (TryCreateLabel(
                                structureLabelType,
                                structureId,
                                structureStyleId,
                                transaction))
                        {
                            result.StructureLabelsAdded++;
                            labelledParts.Add(structureId);
                        }
                        else result.Skipped++;
                    }
                }
                transaction.Commit();
            }
            return result;
        }

        private static IList<string> ReadLabelStyleNames(
            Document document,
            bool pipe)
        {
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            return document == null || civilDocument == null
                ? new List<string>()
                : ProductionStyleCatalog.ReadNames(
                    document.Database,
                    ReadLabelStyleCollection(civilDocument, pipe));
        }

        private static object ReadLabelStyleCollection(
            CivilDocument civilDocument,
            bool pipe)
        {
            object styles = ReadProperty(civilDocument, "Styles");
            object labelStyles = ReadProperty(styles, "LabelStyles");
            object family = ReadProperty(
                labelStyles,
                pipe ? "PipeLabelStyles" : "StructureLabelStyles");
            return ReadProperty(family, "PlanProfileLabelStyles") ??
                ReadProperty(family, "PlanLabelStyles") ?? family;
        }

        private static HashSet<ObjectId> ReadExistingLabelledParts(
            Database database,
            Transaction transaction)
        {
            var result = new HashSet<ObjectId>();
            BlockTableRecord modelSpace = transaction.GetObject(
                SymbolUtilityServices.GetBlockModelSpaceId(database),
                OpenMode.ForRead,
                false) as BlockTableRecord;
            if (modelSpace == null) return result;

            foreach (ObjectId id in modelSpace)
            {
                DBObject value;
                try { value = transaction.GetObject(id, OpenMode.ForRead, false); }
                catch { continue; }
                string typeName = value == null ? string.Empty : value.GetType().Name;
                if (typeName.IndexOf("PipeLabel", StringComparison.OrdinalIgnoreCase) < 0 &&
                    typeName.IndexOf("StructureLabel", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                ObjectId partId = ReadObjectIdProperty(
                    value,
                    "FeatureId", "PipeId", "StructureId", "ParentEntityId", "EntityId");
                if (!partId.IsNull) result.Add(partId);
            }
            return result;
        }

        private static bool TryCreateLabel(
            Type labelType,
            ObjectId featureId,
            ObjectId styleId,
            Transaction transaction)
        {
            if (labelType == null || featureId.IsNull || styleId.IsNull) return false;
            Point3d featurePoint = ReadFeaturePoint(featureId, transaction);
            foreach (MethodInfo method in labelType.GetMethods(
                BindingFlags.Public | BindingFlags.Static)
                .Where(candidate => string.Equals(
                    candidate.Name, "Create", StringComparison.Ordinal))
                .OrderBy(candidate => candidate.GetParameters().Length))
            {
                ParameterInfo[] parameters = method.GetParameters();
                int objectIdIndex = 0;
                var arguments = new object[parameters.Length];
                bool supported = true;
                for (int index = 0; index < parameters.Length; index++)
                {
                    Type type = parameters[index].ParameterType;
                    if (type == typeof(ObjectId))
                    {
                        arguments[index] = objectIdIndex++ == 0
                            ? featureId
                            : styleId;
                    }
                    else if (type == typeof(double)) arguments[index] = 0.5;
                    else if (type == typeof(int)) arguments[index] = 0;
                    else if (type == typeof(bool)) arguments[index] = false;
                    else if (type == typeof(Point3d)) arguments[index] = featurePoint;
                    else if (type == typeof(Point2d))
                        arguments[index] = new Point2d(featurePoint.X, featurePoint.Y);
                    else
                    {
                        supported = false;
                        break;
                    }
                }
                if (!supported || objectIdIndex != 2) continue;
                try
                {
                    object created = method.Invoke(null, arguments);
                    ObjectId id = created is ObjectId
                        ? (ObjectId)created
                        : ReadObjectIdProperty(created, "ObjectId", "Id");
                    if (!id.IsNull)
                    {
                        // Opening the result verifies that Civil 3D attached it
                        // to the active database before the transaction commits.
                        transaction.GetObject(id, OpenMode.ForRead, false);
                        return true;
                    }
                }
                catch
                {
                    // Try the next Civil-version overload.
                }
            }
            return false;
        }

        private static Point3d ReadFeaturePoint(
            ObjectId featureId,
            Transaction transaction)
        {
            try
            {
                DBObject feature = transaction.GetObject(
                    featureId, OpenMode.ForRead, false);
                object value = ReadProperty(feature, "Position") ??
                    ReadProperty(feature, "Location");
                if (value is Point3d) return (Point3d)value;
                Entity entity = feature as Entity;
                if (entity != null)
                {
                    Extents3d extents = entity.GeometricExtents;
                    return new Point3d(
                        (extents.MinPoint.X + extents.MaxPoint.X) * 0.5,
                        (extents.MinPoint.Y + extents.MaxPoint.Y) * 0.5,
                        (extents.MinPoint.Z + extents.MaxPoint.Z) * 0.5);
                }
            }
            catch { }
            return Point3d.Origin;
        }

        private static ObjectId ResolveStyleId(
            object collection,
            string requested,
            Transaction transaction)
        {
            ObjectId first = ObjectId.Null;
            foreach (object item in Enumerate(collection))
            {
                ObjectId id = item is ObjectId
                    ? (ObjectId)item
                    : ReadObjectIdProperty(item, "ObjectId", "Id");
                if (id.IsNull || id.IsErased) continue;
                if (first.IsNull) first = id;
                if (string.IsNullOrWhiteSpace(requested)) continue;
                try
                {
                    DBObject style = transaction.GetObject(id, OpenMode.ForRead, false);
                    string name = Convert.ToString(
                        ReadProperty(style, "Name"), CultureInfo.CurrentCulture);
                    if (string.Equals(name, requested, StringComparison.OrdinalIgnoreCase))
                        return id;
                }
                catch { }
            }
            return first;
        }

        private static IEnumerable<object> Enumerate(object collection)
        {
            var result = new List<object>();
            if (collection == null) return result;
            IEnumerable enumerable = collection as IEnumerable;
            if (enumerable != null)
            {
                try { foreach (object item in enumerable) result.Add(item); }
                catch { result.Clear(); }
                if (result.Count > 0) return result;
            }
            try
            {
                MethodInfo method = collection.GetType().GetMethod(
                    "GetObjectIds", BindingFlags.Public | BindingFlags.Instance,
                    null, Type.EmptyTypes, null);
                IEnumerable ids = method == null
                    ? null
                    : method.Invoke(collection, null) as IEnumerable;
                if (ids != null) foreach (object id in ids) result.Add(id);
            }
            catch { result.Clear(); }
            return result;
        }

        private static Type FindCivilType(string typeName)
        {
            Assembly assembly = typeof(CivilNetwork).Assembly;
            Type direct = assembly.GetType(
                "Autodesk.Civil.DatabaseServices." + typeName, false);
            if (direct != null) return direct;
            try
            {
                return assembly.GetTypes().FirstOrDefault(type =>
                    string.Equals(type.Name, typeName, StringComparison.Ordinal));
            }
            catch (ReflectionTypeLoadException exception)
            {
                return exception.Types.FirstOrDefault(type => type != null &&
                    string.Equals(type.Name, typeName, StringComparison.Ordinal));
            }
        }

        private static object ReadProperty(object value, string name)
        {
            if (value == null) return null;
            try
            {
                PropertyInfo property = value.GetType().GetProperty(
                    name, BindingFlags.Public | BindingFlags.Instance);
                return property == null ? null : property.GetValue(value, null);
            }
            catch { return null; }
        }

        private static ObjectId ReadObjectIdProperty(
            object value,
            params string[] names)
        {
            foreach (string name in names)
            {
                object result = ReadProperty(value, name);
                if (result is ObjectId) return (ObjectId)result;
            }
            return ObjectId.Null;
        }
    }

    internal sealed class SewerNetworkLabelResult
    {
        public int PipeLabelsAdded { get; set; }
        public int StructureLabelsAdded { get; set; }
        public int ExistingLabels { get; set; }
        public int Skipped { get; set; }
        public string Warning { get; set; }
    }
}

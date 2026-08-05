using System;
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
using CivilPipe = Autodesk.Civil.DatabaseServices.Pipe;
using CivilStructure = Autodesk.Civil.DatabaseServices.Structure;

[assembly: CommandClass(typeof(CETools.Civil3D.SewerLabelStyleSyncCommands))]

namespace CETools.Civil3D
{
    public sealed class SewerLabelStyleSyncCommands
    {
        [CommandMethod("CE_SEWLABELSYNC", CommandFlags.Modal | CommandFlags.Redraw)]
        public void SynchronizeLabels()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            SewerLabelSyncResult result = ApplySelectedStyles(document);
            document.Editor.WriteMessage(
                "\nCE_SEWLABELSYNC complete. Pipe labels updated={0}; structure labels updated={1}; pipe parts updated={2}; structure parts updated={3}; overlaps moved={4}; skipped={5}.{6}",
                result.PipeLabelsUpdated,
                result.StructureLabelsUpdated,
                result.PipePartsUpdated,
                result.StructurePartsUpdated,
                result.OverlapsMoved,
                result.Skipped,
                string.IsNullOrWhiteSpace(result.Warning)
                    ? string.Empty
                    : " " + result.Warning);
        }

        [CommandMethod("CE_SEWLABELCLEAN", CommandFlags.Modal | CommandFlags.Redraw)]
        public void CleanOverlaps()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            int moved = ResolveOverlaps(document);
            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_SEWLABELCLEAN complete. Overlapping pipe labels moved={0}.",
                moved);
        }

        internal static SewerLabelSyncResult ApplySelectedStyles(Document document)
        {
            var result = new SewerLabelSyncResult();
            if (document == null) return result;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (civilDocument == null)
            {
                result.Warning = "No active Civil 3D document was available.";
                return result;
            }

            Database database = document.Database;
            SewerProductionSettings settings = SewerProductionSettings.Read(database);
            ProjectStyleSelection project =
                ProjectStyleCenterCommands.ReadSelection(database);

            string pipeLabelRequested = FirstChoice(
                settings.PipePlanLabelStyle,
                Read(project, "Pipe Label Style"));
            string structureLabelRequested = FirstChoice(
                settings.StructurePlanLabelStyle,
                Read(project, "Structure Label Style"));
            string pipePartRequested = Read(project, "Pipe Style");
            string structurePartRequested = Read(project, "Structure Style");

            try
            {
                using (Transaction transaction =
                    database.TransactionManager.StartTransaction())
                {
                    string pipeLabelResolved;
                    ObjectId pipeLabelStyleId = CivilStyleCatalogV2.ResolveStyleId(
                        database,
                        civilDocument,
                        "Pipe Label Style",
                        pipeLabelRequested,
                        transaction,
                        out pipeLabelResolved);
                    string structureLabelResolved;
                    ObjectId structureLabelStyleId = CivilStyleCatalogV2.ResolveStyleId(
                        database,
                        civilDocument,
                        "Structure Label Style",
                        structureLabelRequested,
                        transaction,
                        out structureLabelResolved);
                    string pipePartResolved;
                    ObjectId pipePartStyleId = CivilStyleCatalogV2.ResolveStyleId(
                        database,
                        civilDocument,
                        "Pipe Style",
                        pipePartRequested,
                        transaction,
                        out pipePartResolved);
                    string structurePartResolved;
                    ObjectId structurePartStyleId = CivilStyleCatalogV2.ResolveStyleId(
                        database,
                        civilDocument,
                        "Structure Style",
                        structurePartRequested,
                        transaction,
                        out structurePartResolved);

                    foreach (ObjectId networkId in civilDocument.GetPipeNetworkIds())
                    {
                        CivilNetwork network;
                        try
                        {
                            network = transaction.GetObject(
                                networkId,
                                OpenMode.ForRead,
                                false) as CivilNetwork;
                        }
                        catch
                        {
                            result.Skipped++;
                            continue;
                        }
                        if (network == null || network.IsReferenceObject) continue;

                        foreach (ObjectId pipeId in network.GetPipeIds())
                        {
                            CivilPipe pipe = transaction.GetObject(
                                pipeId,
                                OpenMode.ForWrite,
                                false) as CivilPipe;
                            if (pipe == null || pipePartStyleId.IsNull) continue;
                            if (TrySetObjectIdProperty(pipe, pipePartStyleId,
                                    "StyleId", "PipeStyleId"))
                                result.PipePartsUpdated++;
                            else
                                result.Skipped++;
                        }

                        foreach (ObjectId structureId in network.GetStructureIds())
                        {
                            CivilStructure structure = transaction.GetObject(
                                structureId,
                                OpenMode.ForWrite,
                                false) as CivilStructure;
                            if (structure == null || structurePartStyleId.IsNull) continue;
                            if (TrySetObjectIdProperty(structure, structurePartStyleId,
                                    "StyleId", "StructureStyleId"))
                                result.StructurePartsUpdated++;
                            else
                                result.Skipped++;
                        }
                    }

                    BlockTableRecord modelSpace = transaction.GetObject(
                        SymbolUtilityServices.GetBlockModelSpaceId(database),
                        OpenMode.ForRead,
                        false) as BlockTableRecord;
                    if (modelSpace != null)
                    {
                        foreach (ObjectId id in modelSpace)
                        {
                            DBObject value;
                            try
                            {
                                value = transaction.GetObject(
                                    id,
                                    OpenMode.ForWrite,
                                    false);
                            }
                            catch
                            {
                                continue;
                            }
                            if (value == null) continue;
                            string typeName = value.GetType().Name;
                            bool isPipe = typeName.IndexOf(
                                "PipeLabel",
                                StringComparison.OrdinalIgnoreCase) >= 0;
                            bool isStructure = typeName.IndexOf(
                                "StructureLabel",
                                StringComparison.OrdinalIgnoreCase) >= 0;
                            if (!isPipe && !isStructure) continue;

                            ObjectId target = isPipe
                                ? pipeLabelStyleId
                                : structureLabelStyleId;
                            if (target.IsNull)
                            {
                                result.Skipped++;
                                continue;
                            }
                            TrySetBooleanProperty(value, false,
                                "Pinned", "IsLabelPinned");
                            ClearTextOverrides(value);
                            if (TrySetObjectIdProperty(value, target,
                                    "StyleId", "LabelStyleId"))
                            {
                                if (isPipe)
                                {
                                    result.PipeLabelsUpdated++;
                                    ApplyPipeLabelPresentation(value, transaction);
                                }
                                else result.StructureLabelsUpdated++;
                            }
                            else
                            {
                                result.Skipped++;
                            }
                        }
                    }
                    transaction.Commit();
                }

                document.Editor.Regen();
                result.OverlapsMoved = ResolveOverlaps(document);
                document.Editor.Regen();
            }
            catch (System.Exception exception)
            {
                result.Warning = "Style synchronization stopped: " + exception.Message;
            }
            return result;
        }

        private static int ResolveOverlaps(Document document)
        {
            if (document == null) return 0;
            Database database = document.Database;
            var labels = new List<LabelBox>();

            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                BlockTableRecord modelSpace = transaction.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(database),
                    OpenMode.ForRead,
                    false) as BlockTableRecord;
                if (modelSpace == null) return 0;
                foreach (ObjectId id in modelSpace)
                {
                    DBObject value;
                    try
                    {
                        value = transaction.GetObject(id, OpenMode.ForRead, false);
                    }
                    catch
                    {
                        continue;
                    }
                    Entity entity = value as Entity;
                    if (entity == null) continue;
                    string typeName = value.GetType().Name;
                    bool isPipe = typeName.IndexOf(
                        "PipeLabel",
                        StringComparison.OrdinalIgnoreCase) >= 0;
                    bool isStructure = typeName.IndexOf(
                        "StructureLabel",
                        StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!isPipe && !isStructure) continue;
                    Extents3d extents;
                    try { extents = entity.GeometricExtents; }
                    catch { continue; }
                    labels.Add(new LabelBox(
                        id,
                        isStructure,
                        ReadObjectIdProperty(value,
                            "FeatureId", "PipeId", "StructureId", "ParentEntityId"),
                        extents.MinPoint,
                        extents.MaxPoint));
                }
            }

            double padding = Math.Max(
                0.001,
                PaperAnnotationScale.ModelDistance(database, 1.5));
            double step = Math.Max(
                padding * 2.0,
                PaperAnnotationScale.ModelDistance(database, 6.0));
            var accepted = new List<LabelBox>();
            int moved = 0;

            foreach (LabelBox item in labels
                .OrderByDescending(label => label.IsStructure)
                .ThenBy(label => label.Id.Handle.Value))
            {
                if (item.IsStructure || !accepted.Any(existing =>
                    existing.Intersects(item, padding)))
                {
                    accepted.Add(item);
                    continue;
                }

                Vector3d normal = ReadPipeNormal(
                    database,
                    item.FeatureId);
                if (normal.Length < 1e-8)
                    normal = Vector3d.YAxis;
                normal = normal.GetNormal();

                bool placed = false;
                for (int attempt = 1; attempt <= 8; attempt++)
                {
                    double signed = attempt % 2 == 1 ? 1.0 : -1.0;
                    int ring = (attempt + 1) / 2;
                    Vector3d displacement = normal * (signed * ring * step);
                    LabelBox candidate = item.Translate(displacement);
                    if (accepted.Any(existing =>
                        existing.Intersects(candidate, padding)))
                        continue;
                    if (TryMoveLabel(database, item.Id, displacement))
                    {
                        accepted.Add(candidate);
                        moved++;
                        placed = true;
                        break;
                    }
                }
                if (!placed) accepted.Add(item);
            }
            return moved;
        }

        private static bool TryMoveLabel(
            Database database,
            ObjectId labelId,
            Vector3d displacement)
        {
            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                DBObject label;
                try
                {
                    label = transaction.GetObject(
                        labelId,
                        OpenMode.ForWrite,
                        false);
                }
                catch
                {
                    return false;
                }
                TrySetBooleanProperty(label, false,
                    "Pinned", "IsLabelPinned");
                Point3d location;
                if (!TryReadPointProperty(label, out location,
                        "LabelLocation", "Location"))
                    return false;
                if (!TrySetPointProperty(
                        label,
                        location + displacement,
                        "LabelLocation", "Location"))
                    return false;
                transaction.Commit();
                return true;
            }
        }

        private static Vector3d ReadPipeNormal(
            Database database,
            ObjectId featureId)
        {
            if (featureId.IsNull || featureId.IsErased) return Vector3d.YAxis;
            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                CivilPipe pipe;
                try
                {
                    pipe = transaction.GetObject(
                        featureId,
                        OpenMode.ForRead,
                        false) as CivilPipe;
                }
                catch
                {
                    return Vector3d.YAxis;
                }
                if (pipe == null) return Vector3d.YAxis;
                try
                {
                    Point3d start = pipe.GetPointAtParam(0.0);
                    Point3d end = pipe.GetPointAtParam(1.0);
                    Vector3d direction = end - start;
                    return direction.Length < 1e-8
                        ? Vector3d.YAxis
                        : new Vector3d(-direction.Y, direction.X, 0.0);
                }
                catch
                {
                    return Vector3d.YAxis;
                }
            }
        }

        private static string FirstChoice(string primary, string fallback)
        {
            return IsUseful(primary) ? primary : fallback;
        }

        private static string Read(ProjectStyleSelection selection, string key)
        {
            if (selection == null || !selection.Exists) return string.Empty;
            string value;
            return selection.Values.TryGetValue(key, out value) && IsUseful(value)
                ? value
                : string.Empty;
        }

        private static bool IsUseful(string value)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                !string.Equals(
                    value,
                    "<Use drawing default>",
                    StringComparison.OrdinalIgnoreCase);
        }


        private static void ApplyPipeLabelPresentation(
            object label,
            Transaction transaction)
        {
            if (label == null || transaction == null) return;
            ObjectId featureId = ReadObjectIdProperty(
                label,
                "FeatureId", "PipeId", "ParentEntityId");
            if (featureId.IsNull || featureId.IsErased) return;

            CivilPipe pipe;
            try
            {
                pipe = transaction.GetObject(
                    featureId,
                    OpenMode.ForRead,
                    false) as CivilPipe;
            }
            catch
            {
                return;
            }
            if (pipe == null) return;

            string description = string.IsNullOrWhiteSpace(pipe.Description)
                ? pipe.Name
                : pipe.Description;
            double length = ReadDoubleProperty(
                pipe,
                "Length2D", "Length3D", "Length");
            if (length <= 0.0)
            {
                try
                {
                    length = pipe.GetPointAtParam(0.0).DistanceTo(
                        pipe.GetPointAtParam(1.0));
                }
                catch { }
            }
            double slope = ReadDoubleProperty(pipe, "Slope");
            if (Math.Abs(slope) <= 1.0) slope *= 100.0;

            string contents = (description ?? string.Empty) +
                "\\P" + length.ToString("0.00", CultureInfo.CurrentCulture) +
                " m\\P@ " + slope.ToString("0.00", CultureInfo.CurrentCulture) + "%";
            List<ObjectId> components = ReadTextComponentIds(label);
            for (int index = 0; index < components.Count; index++)
                TrySetTextOverride(
                    label,
                    components[index],
                    index == 0 ? contents : string.Empty);
        }

        private static List<ObjectId> ReadTextComponentIds(object label)
        {
            var result = new List<ObjectId>();
            if (label == null) return result;
            foreach (string name in new[]
            {
                "GetTextComponentIds",
                "GetLabelTextComponentIds"
            })
            {
                try
                {
                    MethodInfo method = label.GetType().GetMethod(
                        name,
                        BindingFlags.Public | BindingFlags.Instance,
                        null,
                        Type.EmptyTypes,
                        null);
                    System.Collections.IEnumerable values = method == null
                        ? null
                        : method.Invoke(label, null) as System.Collections.IEnumerable;
                    if (values == null) continue;
                    foreach (object value in values)
                    {
                        if (value is ObjectId) result.Add((ObjectId)value);
                    }
                    if (result.Count > 0) return result;
                }
                catch
                {
                    result.Clear();
                }
            }
            return result;
        }

        private static void TrySetTextOverride(
            object label,
            ObjectId componentId,
            string contents)
        {
            if (label == null || componentId.IsNull) return;
            foreach (string name in new[]
            {
                "SetTextComponentOverride",
                "SetLabelTextComponentOverride"
            })
            {
                foreach (MethodInfo method in label.GetType().GetMethods(
                    BindingFlags.Public | BindingFlags.Instance)
                    .Where(candidate => string.Equals(
                        candidate.Name,
                        name,
                        StringComparison.Ordinal)))
                {
                    ParameterInfo[] parameters = method.GetParameters();
                    if (parameters.Length < 2 ||
                        parameters[0].ParameterType != typeof(ObjectId) ||
                        parameters[1].ParameterType != typeof(string))
                        continue;
                    var arguments = new object[parameters.Length];
                    arguments[0] = componentId;
                    arguments[1] = contents ?? string.Empty;
                    bool supported = true;
                    for (int index = 2; index < parameters.Length; index++)
                    {
                        Type type = parameters[index].ParameterType;
                        if (type.IsEnum)
                            arguments[index] = Enum.GetValues(type).GetValue(0);
                        else
                        {
                            supported = false;
                            break;
                        }
                    }
                    if (!supported) continue;
                    try
                    {
                        method.Invoke(label, arguments);
                        return;
                    }
                    catch
                    {
                        // Try the next compatible Civil 3D overload.
                    }
                }
            }
        }

        private static double ReadDoubleProperty(
            object value,
            params string[] names)
        {
            if (value == null) return 0.0;
            foreach (string name in names)
            {
                try
                {
                    PropertyInfo property = value.GetType().GetProperty(
                        name,
                        BindingFlags.Public | BindingFlags.Instance);
                    if (property == null || !property.CanRead) continue;
                    object current = property.GetValue(value, null);
                    if (current == null) continue;
                    return Convert.ToDouble(current, CultureInfo.InvariantCulture);
                }
                catch
                {
                    // Try another property name.
                }
            }
            return 0.0;
        }

        private static void ClearTextOverrides(object value)
        {
            foreach (string name in new[]
            {
                "ClearAllTextComponentOverrides",
                "ClearAllLabelTextComponentOverrides"
            })
            {
                try
                {
                    MethodInfo method = value.GetType().GetMethod(
                        name,
                        BindingFlags.Public | BindingFlags.Instance,
                        null,
                        Type.EmptyTypes,
                        null);
                    if (method != null) method.Invoke(value, null);
                }
                catch
                {
                    // Continue with style assignment even if a Civil version
                    // does not expose the override-clear method.
                }
            }
        }

        private static bool TrySetObjectIdProperty(
            object value,
            ObjectId objectId,
            params string[] names)
        {
            if (value == null || objectId.IsNull) return false;
            foreach (string name in names)
            {
                try
                {
                    PropertyInfo property = value.GetType().GetProperty(
                        name,
                        BindingFlags.Public | BindingFlags.Instance);
                    if (property == null || !property.CanWrite ||
                        property.PropertyType != typeof(ObjectId))
                        continue;
                    ObjectId current = property.CanRead
                        ? (ObjectId)property.GetValue(value, null)
                        : ObjectId.Null;
                    if (current == objectId) return true;
                    property.SetValue(value, objectId, null);
                    return true;
                }
                catch
                {
                    // Try the next compatible property name.
                }
            }
            return false;
        }

        private static void TrySetBooleanProperty(
            object value,
            bool setting,
            params string[] names)
        {
            if (value == null) return;
            foreach (string name in names)
            {
                try
                {
                    PropertyInfo property = value.GetType().GetProperty(
                        name,
                        BindingFlags.Public | BindingFlags.Instance);
                    if (property == null || !property.CanWrite ||
                        property.PropertyType != typeof(bool))
                        continue;
                    property.SetValue(value, setting, null);
                    return;
                }
                catch
                {
                    // Try another name.
                }
            }
        }

        private static bool TryReadPointProperty(
            object value,
            out Point3d point,
            params string[] names)
        {
            point = Point3d.Origin;
            if (value == null) return false;
            foreach (string name in names)
            {
                try
                {
                    PropertyInfo property = value.GetType().GetProperty(
                        name,
                        BindingFlags.Public | BindingFlags.Instance);
                    if (property == null || !property.CanRead) continue;
                    object current = property.GetValue(value, null);
                    if (current is Point3d)
                    {
                        point = (Point3d)current;
                        return true;
                    }
                    if (current is Point2d)
                    {
                        Point2d point2d = (Point2d)current;
                        point = new Point3d(point2d.X, point2d.Y, 0.0);
                        return true;
                    }
                }
                catch
                {
                    // Try another property.
                }
            }
            return false;
        }

        private static bool TrySetPointProperty(
            object value,
            Point3d point,
            params string[] names)
        {
            if (value == null) return false;
            foreach (string name in names)
            {
                try
                {
                    PropertyInfo property = value.GetType().GetProperty(
                        name,
                        BindingFlags.Public | BindingFlags.Instance);
                    if (property == null || !property.CanWrite) continue;
                    if (property.PropertyType == typeof(Point3d))
                    {
                        property.SetValue(value, point, null);
                        return true;
                    }
                    if (property.PropertyType == typeof(Point2d))
                    {
                        property.SetValue(
                            value,
                            new Point2d(point.X, point.Y),
                            null);
                        return true;
                    }
                }
                catch
                {
                    // Try another property.
                }
            }
            return false;
        }

        private static ObjectId ReadObjectIdProperty(
            object value,
            params string[] names)
        {
            if (value == null) return ObjectId.Null;
            foreach (string name in names)
            {
                try
                {
                    PropertyInfo property = value.GetType().GetProperty(
                        name,
                        BindingFlags.Public | BindingFlags.Instance);
                    if (property == null || !property.CanRead ||
                        property.PropertyType != typeof(ObjectId))
                        continue;
                    return (ObjectId)property.GetValue(value, null);
                }
                catch
                {
                    // Try another property.
                }
            }
            return ObjectId.Null;
        }

        private sealed class LabelBox
        {
            public LabelBox(
                ObjectId id,
                bool isStructure,
                ObjectId featureId,
                Point3d minimum,
                Point3d maximum)
            {
                Id = id;
                IsStructure = isStructure;
                FeatureId = featureId;
                Minimum = minimum;
                Maximum = maximum;
            }

            public ObjectId Id { get; private set; }
            public bool IsStructure { get; private set; }
            public ObjectId FeatureId { get; private set; }
            public Point3d Minimum { get; private set; }
            public Point3d Maximum { get; private set; }

            public bool Intersects(LabelBox other, double padding)
            {
                return Minimum.X - padding <= other.Maximum.X &&
                    Maximum.X + padding >= other.Minimum.X &&
                    Minimum.Y - padding <= other.Maximum.Y &&
                    Maximum.Y + padding >= other.Minimum.Y;
            }

            public LabelBox Translate(Vector3d displacement)
            {
                return new LabelBox(
                    Id,
                    IsStructure,
                    FeatureId,
                    Minimum + displacement,
                    Maximum + displacement);
            }
        }
    }

    internal sealed class SewerLabelSyncResult
    {
        public int PipeLabelsUpdated { get; set; }
        public int StructureLabelsUpdated { get; set; }
        public int PipePartsUpdated { get; set; }
        public int StructurePartsUpdated { get; set; }
        public int OverlapsMoved { get; set; }
        public int Skipped { get; set; }
        public string Warning { get; set; }
    }
}

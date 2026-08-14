using System;
using System.Collections;
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
using CivilNetwork = Autodesk.Civil.DatabaseServices.Network;

[assembly: CommandClass(typeof(CETools.Civil3D.August14UtilityLabelCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Discipline-specific plan label entry points requested for Stormwater,
    /// Water and Bulk Water. Gravity labels use the same Civil label families as
    /// Sewer but do not read Sewer settings. Pressure labels are discovered and
    /// created through guarded reflection so the 2023 build does not take a hard
    /// compile-time dependency on AeccPressurePipesMgd.
    /// </summary>
    public sealed class August14UtilityLabelCommands
    {
        [CommandMethod("CE_TOOLS", "CE_SWLABELS", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void StormwaterLabels()
        {
            CreateGravityLabels("Stormwater");
        }

        [CommandMethod("CE_TOOLS", "CE_WATERLABELS", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void WaterLabels()
        {
            CreatePressureLabels("Water");
        }

        [CommandMethod("CE_TOOLS", "CE_BULKWATERLABELS", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void BulkWaterLabels()
        {
            CreatePressureLabels("Bulk Water");
        }

        [CommandMethod("CE_TOOLS", "CE_UTILITYLABELCENTRE", CommandFlags.Modal)]
        public void LabelCentre()
        {
            Document document = Active();
            if (document == null) return;
            DisciplineWorkflowDialogs.SelectAndRun(
                document,
                "CE Tools - Utility Labels",
                "Apply Civil 3D plan labels per production discipline. Each command reads styles from the active drawing and can use imported project styles.",
                new List<DisciplineWorkflowAction>
                {
                    new DisciplineWorkflowAction("Stormwater pipe / structure labels", "CE_SWLABELS", "Label selected or all gravity stormwater networks.", "01 Stormwater"),
                    new DisciplineWorkflowAction("Sewer pipe / structure labels", "CE_SEWLABELS", "Existing sewer label workflow.", "02 Sewer"),
                    new DisciplineWorkflowAction("Water pressure-part labels", "CE_WATERLABELS", "Label selected/all pressure pipes, fittings and appurtenances.", "03 Water"),
                    new DisciplineWorkflowAction("Bulk Water pressure-part labels", "CE_BULKWATERLABELS", "Label selected/all pressure pipes, fittings and appurtenances.", "04 Bulk Water"),
                    new DisciplineWorkflowAction("Import missing project styles", "CE_PROJECTSTYLEIMPORT", "Import a source Civil 3D style drawing when dropdowns are empty.", "05 Styles"),
                    new DisciplineWorkflowAction("Discipline style presets", "CE_DISCIPLINESTYLEPRESETS", "Save or activate reusable style choices by discipline.", "05 Styles")
                });
        }

        private static void CreateGravityLabels(string discipline)
        {
            Document document = Active();
            CivilDocument civil = CivilApplication.ActiveDocument;
            if (document == null || civil == null) return;

            IList<string> pipeNames = SewerNetworkLabelCommands.ReadPipeLabelStyleNames(document);
            IList<string> structureNames = SewerNetworkLabelCommands.ReadStructureLabelStyleNames(document);
            string[] pipeChoices = pipeNames.Count == 0 ? new[] { "<No compatible style>" } : pipeNames.ToArray();
            string[] structureChoices = structureNames.Count == 0 ? new[] { "<No compatible style>" } : structureNames.ToArray();
            var model = new ProductionSettingsDialogModel(
                "CE Tools - " + discipline + " Plan Labels",
                "Choose the Civil 3D plan label styles for this discipline. Use CE_PROJECTSTYLEIMPORT first when the drawing contains no compatible styles.");
            model.AddChoice("PipeStyle", "Styles", "Pipe plan-label style", pipeChoices[0], "Style used at the centre of each unlabelled pipe.", pipeChoices);
            model.AddChoice("StructureStyle", "Styles", "Structure plan-label style", structureChoices[0], "Style used for each unlabelled structure.", structureChoices);
            model.AddChoice("Scope", "Scope", "Networks", "Selected networks", "Label selected gravity networks, or every gravity network whose name looks like the chosen discipline.", new[] { "Selected networks", "All matching networks" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            object pipeCollection = ReadGravityLabelStyleCollection(civil, true);
            object structureCollection = ReadGravityLabelStyleCollection(civil, false);
            if (pipeCollection == null || structureCollection == null)
            {
                document.Editor.WriteMessage("\nCE_SWLABELS cancelled. Compatible Civil 3D pipe/structure label style collections are unavailable. Import the project styles first.");
                return;
            }

            List<ObjectId> networks = string.Equals(model.Text("Scope"), "Selected networks", StringComparison.OrdinalIgnoreCase)
                ? SelectGravityNetworks(document)
                : ReadMatchingGravityNetworks(document, civil, discipline);
            if (networks.Count == 0)
            {
                document.Editor.WriteMessage("\nCE_SWLABELS: no matching gravity networks were found/selected.");
                return;
            }

            int pipeAdded = 0;
            int structureAdded = 0;
            int kept = 0;
            int skipped = 0;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                ObjectId pipeStyle = ResolveStyleId(document.Database, transaction, pipeCollection, model.Text("PipeStyle"));
                ObjectId structureStyle = ResolveStyleId(document.Database, transaction, structureCollection, model.Text("StructureStyle"));
                if (pipeStyle.IsNull || structureStyle.IsNull)
                    throw new InvalidOperationException("The selected pipe/structure label styles could not be resolved. Import/select project styles and retry.");

                HashSet<ObjectId> already = ReadExistingLabelledParts(document.Database, transaction, new[] { "PipeLabel", "StructureLabel" });
                Type pipeLabelType = FindType("Autodesk.Civil.DatabaseServices.PipeLabel");
                Type structureLabelType = FindType("Autodesk.Civil.DatabaseServices.StructureLabel");
                foreach (ObjectId networkId in networks.Distinct())
                {
                    CivilNetwork network = transaction.GetObject(networkId, OpenMode.ForRead, false) as CivilNetwork;
                    if (network == null || network.IsReferenceObject) continue;
                    foreach (ObjectId pipeId in network.GetPipeIds())
                    {
                        if (already.Contains(pipeId)) { kept++; continue; }
                        if (TryCreateLabel(pipeLabelType, pipeId, pipeStyle, transaction, false)) { pipeAdded++; already.Add(pipeId); }
                        else skipped++;
                    }
                    foreach (ObjectId structureId in network.GetStructureIds())
                    {
                        if (already.Contains(structureId)) { kept++; continue; }
                        if (TryCreateLabel(structureLabelType, structureId, structureStyle, transaction, true)) { structureAdded++; already.Add(structureId); }
                        else skipped++;
                    }
                }
                transaction.Commit();
            }
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_SWLABELS complete. Pipe labels={0}; structure labels={1}; existing kept={2}; skipped={3}.", pipeAdded, structureAdded, kept, skipped);
        }

        private static void CreatePressureLabels(string discipline)
        {
            Document document = Active();
            CivilDocument civil = CivilApplication.ActiveDocument;
            if (document == null || civil == null) return;

            object labelRoot = ReadProperty(ReadProperty(civil, "Styles"), "LabelStyles");
            object pipeStyles = ReadPressureLabelStyles(labelRoot, "Pipe");
            object fittingStyles = ReadPressureLabelStyles(labelRoot, "Fitting");
            object appurtenanceStyles = ReadPressureLabelStyles(labelRoot, "Appurtenance");
            IList<string> pipeNames = CivilStyleCatalogV2.ReadNames(document.Database, pipeStyles, "Pressure Pipe Label Style");
            IList<string> fittingNames = CivilStyleCatalogV2.ReadNames(document.Database, fittingStyles, "Pressure Fitting Label Style");
            IList<string> appNames = CivilStyleCatalogV2.ReadNames(document.Database, appurtenanceStyles, "Pressure Appurtenance Label Style");
            string[] p = pipeNames.Count == 0 ? new[] { "<No compatible style>" } : pipeNames.ToArray();
            string[] f = fittingNames.Count == 0 ? new[] { "<No compatible style>" } : fittingNames.ToArray();
            string[] a = appNames.Count == 0 ? new[] { "<No compatible style>" } : appNames.ToArray();

            var model = new ProductionSettingsDialogModel(
                "CE Tools - " + discipline + " Pressure Labels",
                "Apply plan labels to pressure pipes, fittings and appurtenances. Pressure API types are resolved at runtime so the common CE Tools source remains compatible with the Civil 3D 2023 build.");
            model.AddChoice("PipeStyle", "Styles", "Pressure pipe label style", p[0], "Plan/profile pressure-pipe label style.", p);
            model.AddChoice("FittingStyle", "Styles", "Fitting label style", f[0], "Pressure fitting label style.", f);
            model.AddChoice("AppStyle", "Styles", "Appurtenance label style", a[0], "Pressure appurtenance label style.", a);
            model.AddChoice("Scope", "Scope", "Pressure parts", "Selected parts", "Label the selected pressure parts or all compatible pressure parts in model space.", new[] { "Selected parts", "All model-space parts" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            List<ObjectId> parts = string.Equals(model.Text("Scope"), "Selected parts", StringComparison.OrdinalIgnoreCase)
                ? SelectPressureParts(document)
                : ReadAllPressureParts(document);
            if (parts.Count == 0)
            {
                document.Editor.WriteMessage("\nCE_" + discipline.Replace(" ", string.Empty).ToUpperInvariant() + "LABELS: no pressure parts were found/selected.");
                return;
            }

            int added = 0;
            int existing = 0;
            int skipped = 0;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                ObjectId pipeStyle = ResolveStyleId(document.Database, transaction, pipeStyles, model.Text("PipeStyle"));
                ObjectId fittingStyle = ResolveStyleId(document.Database, transaction, fittingStyles, model.Text("FittingStyle"));
                ObjectId appStyle = ResolveStyleId(document.Database, transaction, appurtenanceStyles, model.Text("AppStyle"));
                HashSet<ObjectId> labelled = ReadExistingLabelledParts(document.Database, transaction, new[] { "PressurePipeLabel", "PressureFittingLabel", "PressureAppurtenanceLabel" });
                Type pipeLabelType = FindType("Autodesk.Civil.DatabaseServices.PressurePipeLabel");
                Type fittingLabelType = FindType("Autodesk.Civil.DatabaseServices.PressureFittingLabel");
                Type appLabelType = FindType("Autodesk.Civil.DatabaseServices.PressureAppurtenanceLabel");

                foreach (ObjectId id in parts.Distinct())
                {
                    if (labelled.Contains(id)) { existing++; continue; }
                    DBObject part;
                    try { part = transaction.GetObject(id, OpenMode.ForRead, false); }
                    catch { skipped++; continue; }
                    string name = part == null ? string.Empty : part.GetType().Name;
                    bool ok = false;
                    if (name.IndexOf("PressurePipe", StringComparison.OrdinalIgnoreCase) >= 0 && name.IndexOf("Label", StringComparison.OrdinalIgnoreCase) < 0)
                        ok = TryCreatePressurePipeLabel(pipeLabelType, id, pipeStyle, transaction);
                    else if (name.IndexOf("Fitting", StringComparison.OrdinalIgnoreCase) >= 0 && name.IndexOf("Label", StringComparison.OrdinalIgnoreCase) < 0)
                        ok = TryCreatePressurePointLabel(fittingLabelType, id, fittingStyle, transaction);
                    else if (name.IndexOf("Appurtenance", StringComparison.OrdinalIgnoreCase) >= 0 && name.IndexOf("Label", StringComparison.OrdinalIgnoreCase) < 0)
                        ok = TryCreatePressurePointLabel(appLabelType, id, appStyle, transaction);
                    if (ok) { added++; labelled.Add(id); } else skipped++;
                }
                transaction.Commit();
            }
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE {0} labels complete. Added={1}; existing kept={2}; skipped={3}.", discipline, added, existing, skipped);
        }

        private static List<ObjectId> SelectGravityNetworks(Document document)
        {
            PromptSelectionResult selection = document.Editor.GetSelection(new PromptSelectionOptions
            {
                MessageForAdding = "\nSelect Civil 3D gravity pipe network objects/parts: ",
                AllowDuplicates = false,
                RejectObjectsFromNonCurrentSpace = true
            });
            if (selection.Status != PromptStatus.OK || selection.Value == null) return new List<ObjectId>();
            var result = new HashSet<ObjectId>();
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in selection.Value.GetObjectIds())
                {
                    DBObject value;
                    try { value = transaction.GetObject(id, OpenMode.ForRead, false); }
                    catch { continue; }
                    CivilNetwork network = value as CivilNetwork;
                    if (network != null) { result.Add(network.ObjectId); continue; }
                    ObjectId networkId = ReadObjectIdProperty(value, "NetworkId", "PipeNetworkId");
                    if (!networkId.IsNull) result.Add(networkId);
                }
            }
            return result.ToList();
        }

        private static List<ObjectId> ReadMatchingGravityNetworks(Document document, CivilDocument civil, string discipline)
        {
            var result = new List<ObjectId>();
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in civil.GetPipeNetworkIds())
                {
                    CivilNetwork network = transaction.GetObject(id, OpenMode.ForRead, false) as CivilNetwork;
                    if (network == null) continue;
                    string name = network.Name ?? string.Empty;
                    if (discipline.Equals("Stormwater", StringComparison.OrdinalIgnoreCase))
                    {
                        if (name.IndexOf("SW", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("STORM", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("DRAIN", StringComparison.OrdinalIgnoreCase) >= 0)
                            result.Add(id);
                    }
                    else result.Add(id);
                }
            }
            return result;
        }

        private static object ReadGravityLabelStyleCollection(CivilDocument civil, bool pipe)
        {
            object styles = ReadProperty(civil, "Styles");
            object labelStyles = ReadProperty(styles, "LabelStyles");
            object family = ReadProperty(labelStyles, pipe ? "PipeLabelStyles" : "StructureLabelStyles");
            if (family == null) return null;
            return ReadProperty(family, pipe ? "PlanProfileLabelStyles" : "LabelStyles") ?? ReadProperty(family, "PlanLabelStyles") ?? family;
        }

        private static object ReadPressureLabelStyles(object labelRoot, string family)
        {
            if (labelRoot == null) return null;
            Assembly pressure = FindOrLoadAssembly("AeccPressurePipesMgd");
            if (pressure == null) return null;
            Type extension = pressure.GetType("Autodesk.Civil.DatabaseServices.Styles.LabelStylesRootPressurePipesExtension", false);
            if (extension == null) return null;
            string methodName = "GetPressure" + family + "LabelStyles";
            MethodInfo method = extension.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
            if (method == null) return null;
            try
            {
                object root = method.Invoke(null, new[] { labelRoot });
                if (root == null) return null;
                if (family.Equals("Pipe", StringComparison.OrdinalIgnoreCase))
                    return ReadProperty(root, "PlanProfileLabelStyles") ?? ReadProperty(root, "LabelStyles") ?? root;
                return ReadProperty(root, "LabelStyles") ?? root;
            }
            catch { return null; }
        }

        private static List<ObjectId> SelectPressureParts(Document document)
        {
            PromptSelectionResult selection = document.Editor.GetSelection(new PromptSelectionOptions
            {
                MessageForAdding = "\nSelect pressure pipes, fittings and/or appurtenances: ",
                AllowDuplicates = false,
                RejectObjectsFromNonCurrentSpace = true
            });
            if (selection.Status != PromptStatus.OK || selection.Value == null) return new List<ObjectId>();
            var result = new List<ObjectId>();
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in selection.Value.GetObjectIds())
                {
                    DBObject value;
                    try { value = transaction.GetObject(id, OpenMode.ForRead, false); }
                    catch { continue; }
                    if (IsPressurePart(value)) result.Add(id);
                }
            }
            return result;
        }

        private static List<ObjectId> ReadAllPressureParts(Document document)
        {
            var result = new List<ObjectId>();
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord model = transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(document.Database), OpenMode.ForRead, false) as BlockTableRecord;
                if (model == null) return result;
                foreach (ObjectId id in model)
                {
                    DBObject value;
                    try { value = transaction.GetObject(id, OpenMode.ForRead, false); }
                    catch { continue; }
                    if (IsPressurePart(value)) result.Add(id);
                }
            }
            return result;
        }

        private static bool IsPressurePart(DBObject value)
        {
            if (value == null) return false;
            string name = value.GetType().Name;
            if (name.IndexOf("Label", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            return name.IndexOf("PressurePipe", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("PressureFitting", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("PressureAppurtenance", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.Equals("Fitting", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("Appurtenance", StringComparison.OrdinalIgnoreCase);
        }

        private static ObjectId ResolveStyleId(Database database, Transaction transaction, object collection, string requested)
        {
            IList<ObjectId> ids = CivilStyleCatalogV2.ReadObjectIds(collection, transaction);
            if (ids.Count == 0) return ObjectId.Null;
            if (string.IsNullOrWhiteSpace(requested) || requested.StartsWith("<", StringComparison.Ordinal)) return ids[0];
            foreach (ObjectId id in ids)
            {
                try
                {
                    DBObject style = transaction.GetObject(id, OpenMode.ForRead, false);
                    string name = Convert.ToString(ReadProperty(style, "Name"), CultureInfo.CurrentCulture);
                    if (string.Equals(name, requested, StringComparison.OrdinalIgnoreCase)) return id;
                }
                catch { }
            }
            return ids[0];
        }

        private static HashSet<ObjectId> ReadExistingLabelledParts(Database database, Transaction transaction, IEnumerable<string> labelTypeNames)
        {
            var accepted = new HashSet<string>(labelTypeNames, StringComparer.OrdinalIgnoreCase);
            var result = new HashSet<ObjectId>();
            BlockTableRecord model = transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(database), OpenMode.ForRead, false) as BlockTableRecord;
            if (model == null) return result;
            foreach (ObjectId id in model)
            {
                DBObject value;
                try { value = transaction.GetObject(id, OpenMode.ForRead, false); }
                catch { continue; }
                if (value == null || !accepted.Contains(value.GetType().Name)) continue;
                ObjectId feature = ReadObjectIdProperty(value, "FeatureId", "PipeId", "StructureId", "PartId", "ParentEntityId", "EntityId");
                if (!feature.IsNull) result.Add(feature);
            }
            return result;
        }

        private static bool TryCreateLabel(Type labelType, ObjectId featureId, ObjectId styleId, Transaction transaction, bool pointFeature)
        {
            if (labelType == null || featureId.IsNull || styleId.IsNull) return false;
            Point3d location = FeaturePoint(featureId, transaction);
            foreach (MethodInfo method in labelType.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(item => item.Name == "Create").OrderBy(item => item.GetParameters().Length))
            {
                ParameterInfo[] parameters = method.GetParameters();
                var args = new object[parameters.Length];
                int objectIds = 0;
                bool supported = true;
                for (int index = 0; index < parameters.Length; index++)
                {
                    Type type = parameters[index].ParameterType;
                    if (type == typeof(ObjectId)) args[index] = objectIds++ == 0 ? featureId : styleId;
                    else if (type == typeof(double)) args[index] = 0.5;
                    else if (type == typeof(Point3d)) args[index] = location;
                    else if (type == typeof(Point2d)) args[index] = new Point2d(location.X, location.Y);
                    else if (type == typeof(bool)) args[index] = false;
                    else if (type == typeof(int)) args[index] = 0;
                    else { supported = false; break; }
                }
                if (!supported || objectIds < 1 || objectIds > 2) continue;
                if (objectIds == 1 && parameters.Count(p => p.ParameterType == typeof(ObjectId)) == 1 && !styleId.IsNull)
                {
                    // Default-style overload. Use only after styled overloads have been attempted.
                }
                try
                {
                    object created = method.Invoke(null, args);
                    ObjectId createdId = created is ObjectId ? (ObjectId)created : ReadObjectIdProperty(created, "ObjectId", "Id");
                    if (!createdId.IsNull)
                    {
                        transaction.GetObject(createdId, OpenMode.ForRead, false);
                        return true;
                    }
                }
                catch { }
            }
            return false;
        }

        private static bool TryCreatePressurePipeLabel(Type type, ObjectId partId, ObjectId styleId, Transaction transaction)
        {
            if (type == null || styleId.IsNull) return false;
            MethodInfo method = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(item => item.Name == "Create" && item.GetParameters().Length == 3 && item.GetParameters()[0].ParameterType == typeof(ObjectId) && item.GetParameters()[1].ParameterType == typeof(double) && item.GetParameters()[2].ParameterType == typeof(ObjectId));
            return TryInvoke(method, new object[] { partId, 0.5, styleId }, transaction);
        }

        private static bool TryCreatePressurePointLabel(Type type, ObjectId partId, ObjectId styleId, Transaction transaction)
        {
            if (type == null || styleId.IsNull) return false;
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(item => item.Name == "Create"))
            {
                ParameterInfo[] p = method.GetParameters();
                if (p.Length == 4 && p[0].ParameterType == typeof(ObjectId) && p[1].ParameterType == typeof(ObjectId) && p[2].ParameterType == typeof(double) && p[3].ParameterType == typeof(Vector3d))
                    if (TryInvoke(method, new object[] { partId, styleId, 0.5, Vector3d.XAxis }, transaction)) return true;
                if (p.Length == 2 && p[0].ParameterType == typeof(ObjectId) && p[1].ParameterType == typeof(ObjectId))
                    if (TryInvoke(method, new object[] { partId, styleId }, transaction)) return true;
            }
            return false;
        }

        private static bool TryInvoke(MethodInfo method, object[] args, Transaction transaction)
        {
            if (method == null) return false;
            try
            {
                object created = method.Invoke(null, args);
                ObjectId id = created is ObjectId ? (ObjectId)created : ReadObjectIdProperty(created, "ObjectId", "Id");
                if (id.IsNull) return false;
                transaction.GetObject(id, OpenMode.ForRead, false);
                return true;
            }
            catch { return false; }
        }

        private static Point3d FeaturePoint(ObjectId id, Transaction transaction)
        {
            try
            {
                DBObject value = transaction.GetObject(id, OpenMode.ForRead, false);
                object point = ReadProperty(value, "Position") ?? ReadProperty(value, "Location");
                if (point is Point3d) return (Point3d)point;
                Entity entity = value as Entity;
                if (entity != null)
                {
                    Extents3d extents = entity.GeometricExtents;
                    return new Point3d((extents.MinPoint.X + extents.MaxPoint.X) * 0.5, (extents.MinPoint.Y + extents.MaxPoint.Y) * 0.5, (extents.MinPoint.Z + extents.MaxPoint.Z) * 0.5);
                }
            }
            catch { }
            return Point3d.Origin;
        }

        private static Assembly FindOrLoadAssembly(string name)
        {
            Assembly assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(item => string.Equals(item.GetName().Name, name, StringComparison.OrdinalIgnoreCase));
            if (assembly != null) return assembly;
            try { return Assembly.Load(name); } catch { return null; }
        }

        private static Type FindType(string fullName)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = null;
                try { type = assembly.GetType(fullName, false); } catch { }
                if (type != null) return type;
            }
            Assembly pressure = FindOrLoadAssembly("AeccPressurePipesMgd");
            if (pressure != null)
            {
                try { return pressure.GetType(fullName, false); } catch { }
            }
            return null;
        }

        private static object ReadProperty(object value, string name)
        {
            if (value == null) return null;
            try
            {
                PropertyInfo property = value.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                return property == null ? null : property.GetValue(value, null);
            }
            catch { return null; }
        }

        private static ObjectId ReadObjectIdProperty(object value, params string[] names)
        {
            foreach (string name in names)
            {
                object result = ReadProperty(value, name);
                if (result is ObjectId && !((ObjectId)result).IsNull) return (ObjectId)result;
            }
            return ObjectId.Null;
        }

        private static Document Active()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
        }
    }
}

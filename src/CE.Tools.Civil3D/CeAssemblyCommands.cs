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

[assembly: CommandClass(typeof(CETools.Civil3D.CeAssemblyCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Civil 3D 2023-safe assembly creation and reporting. Reflection keeps the
    /// command compatible with the AssemblyCollection overloads exposed by both
    /// Civil 3D 2023 and 2024.
    /// </summary>
    public sealed class CeAssemblyCommands
    {
        [CommandMethod("CE_TOOLS", "CE_ASSEMBLYTOOLS", CommandFlags.Modal)]
        public void AssemblyTools()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            DisciplineWorkflowDialogs.SelectAndRun(
                document,
                "CE Tools - Assembly Workflow",
                "Create and review Civil 3D assemblies before generating road corridors.",
                new List<DisciplineWorkflowAction>
                {
                    Action("Create CE road assembly", "CE_ASSEMBLYCREATE", "Create a named Civil 3D roadway assembly at a selected location.", "1 — Create"),
                    Action("Assembly register", "CE_ASSEMBLYREPORT", "Review all assemblies, styles and subassembly counts.", "2 — Review"),
                    Action("Create road corridors", "CE_ROADCORRIDORS", "Use a selected assembly with CE road alignment/profile pairs.", "3 — Corridors"),
                    Action("Road production workflow", "CE_ROADPRODUCTION", "Open the complete ordered road workflow.", "3 — Corridors"),
                    Action("Project Style Centre", "CE_PROJECTSTYLES", "Select the project assembly style and corridor styles.", "4 — Styles")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_ASSEMBLYCREATE", CommandFlags.Modal | CommandFlags.Redraw)]
        public void CreateAssembly()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            try
            {
                ObjectId id = CreateRoadAssemblyInteractively(document);
                if (!id.IsNull)
                    document.Editor.WriteMessage(
                        "\nCE_ASSEMBLYCREATE complete. Assembly handle={0}. Add the required lane, kerb, shoulder and daylight subassemblies before detailed corridor modelling.",
                        id.Handle);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_ASSEMBLYCREATE failed. No assembly was committed. {0}",
                    exception.Message);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_ASSEMBLYREPORT", CommandFlags.Modal | CommandFlags.Redraw)]
        public void AssemblyReport()
        {
            Document document = ActiveDocument();
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (document == null || civilDocument == null) return;
            var rows = new List<IList<string>>();
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ReadAssemblyIds(civilDocument))
                {
                    DBObject assembly = transaction.GetObject(id, OpenMode.ForRead, false);
                    rows.Add(new List<string>
                    {
                        Text(ReadProperty(assembly, "Name")),
                        Text(ReadProperty(assembly, "StyleName")),
                        CountValues(ReadProperty(assembly, "Subassemblies")).ToString(CultureInfo.CurrentCulture),
                        id.Handle.ToString()
                    });
                }
            }
            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - Assembly Register",
                rows.Count == 0
                    ? "No Civil 3D assemblies exist. Choose Create CE Road Assembly from the CE-ASSEMBLY panel."
                    : rows.Count.ToString(CultureInfo.CurrentCulture) + " Civil 3D assembly/assemblies found.",
                new List<string> { "ASSEMBLY", "STYLE", "SUBASSEMBLIES", "HANDLE" },
                rows,
                "CE TOOLS ASSEMBLY REGISTER");
        }

        internal static ObjectId CreateRoadAssemblyInteractively(Document document)
        {
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (document == null || civilDocument == null) return ObjectId.Null;
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Create Road Assembly",
                "Create the Civil 3D assembly container used by Road Production. Subassemblies can then be added from the Civil 3D Tool Palettes.");
            model.AddText("Name", "Assembly", "Assembly name", "CE-ROAD-ASSEMBLY", "The name is made unique when an assembly with the same name already exists.");
            model.AddChoice(
                "Type",
                "Assembly",
                "Road assembly type",
                "UndividedCrownedRoad",
                "Choose the Civil 3D assembly classification.",
                new[] { "UndividedCrownedRoad", "UndividedPlanarRoad", "Other" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return ObjectId.Null;
            PromptPointResult point = document.Editor.GetPoint(
                "\nSelect the insertion point for the CE road assembly: ");
            if (point.Status != PromptStatus.OK) return ObjectId.Null;

            string name = UniqueAssemblyName(
                civilDocument,
                document.Database,
                model.Text("Name"));
            ObjectId assemblyId;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                object collection = ReadProperty(civilDocument, "AssemblyCollection") ??
                                    ReadProperty(civilDocument, "Assemblies");
                if (collection == null)
                    throw new InvalidOperationException(
                        "CivilDocument.AssemblyCollection is unavailable in this Civil 3D build.");
                ObjectId styleId = ResolveAssemblyStyleId(
                    civilDocument,
                    document.Database,
                    transaction);
                assemblyId = AddAssembly(
                    collection,
                    name,
                    model.Text("Type"),
                    point.Value,
                    styleId);
                DBObject assembly = transaction.GetObject(
                    assemblyId,
                    OpenMode.ForWrite,
                    false);
                TrySetProperty(
                    assembly,
                    "Description",
                    "CE Tools road assembly. Add project subassemblies before detailed corridor generation.");
                if (!styleId.IsNull) TrySetProperty(assembly, "StyleId", styleId);
                transaction.Commit();
            }
            document.Editor.Regen();
            return assemblyId;
        }

        private static ObjectId AddAssembly(
            object collection,
            string name,
            string requestedType,
            Point3d point,
            ObjectId styleId)
        {
            System.Exception lastError = null;
            foreach (MethodInfo method in collection.GetType()
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(item => item.Name == "Add")
                .OrderBy(item => item.GetParameters().Length))
            {
                object[] arguments = new object[method.GetParameters().Length];
                bool supported = true;
                for (int index = 0; index < arguments.Length; index++)
                {
                    ParameterInfo parameter = method.GetParameters()[index];
                    if (parameter.ParameterType == typeof(string)) arguments[index] = name;
                    else if (parameter.ParameterType == typeof(Point3d)) arguments[index] = point;
                    else if (parameter.ParameterType == typeof(ObjectId) && !styleId.IsNull)
                        arguments[index] = styleId;
                    else if (parameter.ParameterType.IsEnum)
                        arguments[index] = ParseEnum(parameter.ParameterType, requestedType);
                    else if (parameter.HasDefaultValue) arguments[index] = parameter.DefaultValue;
                    else { supported = false; break; }
                }
                if (!supported) continue;
                try
                {
                    object result = method.Invoke(collection, arguments);
                    if (result is ObjectId) return (ObjectId)result;
                    DBObject value = result as DBObject;
                    if (value != null) return value.ObjectId;
                }
                catch (TargetInvocationException exception)
                {
                    lastError = exception.InnerException ?? exception;
                }
            }
            throw new InvalidOperationException(
                "No compatible Civil 3D AssemblyCollection.Add overload succeeded." +
                (lastError == null ? string.Empty : " " + lastError.Message));
        }

        private static object ParseEnum(Type type, string requested)
        {
            string[] names = Enum.GetNames(type);
            string match = names.FirstOrDefault(name =>
                string.Equals(name, requested, StringComparison.OrdinalIgnoreCase));
            return Enum.Parse(type, match ?? names.First(), true);
        }

        private static ObjectId ResolveAssemblyStyleId(
            CivilDocument civilDocument,
            Database database,
            Transaction transaction)
        {
            ProjectStyleSelection selection = ProjectStyleCenterCommands.ReadSelection(database);
            string requested = string.Empty;
            if (selection != null)
                selection.Values.TryGetValue("Assembly Style", out requested);
            object collection = ReadProperty(civilDocument.Styles, "AssemblyStyles");
            IEnumerable values = collection as IEnumerable;
            if (values == null) return ObjectId.Null;
            ObjectId first = ObjectId.Null;
            foreach (object value in values)
            {
                ObjectId id = value is ObjectId ? (ObjectId)value : ObjectId.Null;
                if (id.IsNull || id.IsErased) continue;
                if (first.IsNull) first = id;
                DBObject style = transaction.GetObject(id, OpenMode.ForRead, false);
                if (!string.IsNullOrWhiteSpace(requested) &&
                    string.Equals(Text(ReadProperty(style, "Name")), requested,
                        StringComparison.OrdinalIgnoreCase))
                    return id;
            }
            return first;
        }

        private static string UniqueAssemblyName(
            CivilDocument civilDocument,
            Database database,
            string requested)
        {
            string root = string.IsNullOrWhiteSpace(requested)
                ? "CE-ROAD-ASSEMBLY"
                : requested.Trim();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ReadAssemblyIds(civilDocument))
                {
                    DBObject value = transaction.GetObject(id, OpenMode.ForRead, false);
                    names.Add(Text(ReadProperty(value, "Name")));
                }
            }
            string candidate = root;
            int suffix = 2;
            while (names.Contains(candidate))
                candidate = root + "-" + suffix++.ToString(CultureInfo.InvariantCulture);
            return candidate;
        }

        internal static IList<ObjectId> ReadAssemblyIds(CivilDocument civilDocument)
        {
            var result = new List<ObjectId>();
            if (civilDocument == null) return result;
            try
            {
                MethodInfo method = civilDocument.GetType().GetMethod(
                    "GetAssemblyIds",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    Type.EmptyTypes,
                    null);
                IEnumerable values = method == null
                    ? null
                    : method.Invoke(civilDocument, null) as IEnumerable;
                if (values != null)
                    foreach (object value in values)
                        if (value is ObjectId) result.Add((ObjectId)value);
            }
            catch { }
            return result;
        }

        private static int CountValues(object value)
        {
            IEnumerable values = value as IEnumerable;
            if (values == null) return 0;
            int count = 0;
            foreach (object ignored in values) count++;
            return count;
        }

        private static object ReadProperty(object value, string name)
        {
            if (value == null) return null;
            try
            {
                PropertyInfo property = value.GetType().GetProperty(
                    name,
                    BindingFlags.Public | BindingFlags.Instance);
                return property == null ? null : property.GetValue(value, null);
            }
            catch { return null; }
        }

        private static void TrySetProperty(object value, string name, object propertyValue)
        {
            if (value == null) return;
            try
            {
                PropertyInfo property = value.GetType().GetProperty(
                    name,
                    BindingFlags.Public | BindingFlags.Instance);
                if (property != null && property.CanWrite)
                    property.SetValue(value, propertyValue, null);
            }
            catch { }
        }

        private static string Text(object value)
        {
            return Convert.ToString(value, CultureInfo.CurrentCulture) ?? string.Empty;
        }

        private static DisciplineWorkflowAction Action(
            string title,
            string command,
            string description,
            string group)
        {
            return new DisciplineWorkflowAction(title, command, description, group);
        }

        private static Document ActiveDocument()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
        }
    }
}

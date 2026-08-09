using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.FinalFeatureLineBreaklineCommands))]
[assembly: CommandClass(typeof(CETools.Civil3D.FinalNetworkCreationCommands))]
[assembly: CommandClass(typeof(CETools.Civil3D.CostEstimateTemplateCommands))]

namespace CETools.Civil3D
{
    public sealed class FinalFeatureLineBreaklineCommands
    {
        [CommandMethod("CE_TOOLS", "CE_FLBREAKLINE", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void AddFeatureLinesAsBreaklines()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Feature Line Surface Breakline",
                "Add selected feature lines/3D curves as standard breaklines to a Civil 3D TIN surface. The source feature lines remain editable; rebuild the surface after source elevation edits.");
            settings.AddPositiveDouble("MidOrdinate", "01 Breakline", "Mid-ordinate distance", 1.0, "Arc tessellation tolerance for the Civil 3D standard-breakline definition.");
            settings.AddPositiveDouble("MaxDistance", "01 Breakline", "Maximum distance", 100.0, "Maximum supplemental breakline distance.");
            settings.AddPositiveDouble("Weeding", "01 Breakline", "Weeding distance", 0.001, "Small positive value preserves nearly all source vertices.");
            settings.AddPositiveDouble("Supplement", "01 Breakline", "Supplementing distance", 1.0, "Supplementing interval along long breakline segments.");
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;

            var surfaceOptions = new PromptEntityOptions("\nSelect Civil 3D TIN surface to receive breaklines: ");
            surfaceOptions.SetRejectMessage("\nSelect a Civil 3D TIN surface.");
            surfaceOptions.AddAllowedClass(typeof(TinSurface), true);
            PromptEntityResult surfaceResult = document.Editor.GetEntity(surfaceOptions);
            if (surfaceResult.Status != PromptStatus.OK) return;

            PromptSelectionResult sourceResult = document.Editor.GetSelection(
                new PromptSelectionOptions
                {
                    MessageForAdding = "\nSelect feature lines / 3D breakline source curves: ",
                    AllowDuplicates = false,
                    RejectObjectsFromNonCurrentSpace = true
                });
            if (sourceResult.Status != PromptStatus.OK || sourceResult.Value == null) return;

            ObjectIdCollection sources = new ObjectIdCollection(
                sourceResult.Value.GetObjectIds()
                    .Where(id => !id.IsNull && !id.IsErased)
                    .ToArray());
            if (sources.Count == 0) return;

            try
            {
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    TinSurface surface = transaction.GetObject(surfaceResult.ObjectId, OpenMode.ForWrite, false) as TinSurface;
                    if (surface == null) throw new InvalidOperationException("The selected surface is unavailable.");
                    object definition = ReadProperty(surface, "BreaklinesDefinition");
                    if (definition == null) throw new InvalidOperationException("Civil 3D did not expose the surface breakline definition.");
                    MethodInfo method = definition.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .Where(candidate => string.Equals(candidate.Name, "AddStandardBreaklines", StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(candidate => candidate.GetParameters().Length)
                        .FirstOrDefault();
                    if (method == null) throw new MissingMethodException("Civil 3D AddStandardBreaklines API was not found.");
                    method.Invoke(definition, BuildBreaklineArguments(
                        method,
                        sources,
                        settings.Double("MidOrdinate", 1.0),
                        settings.Double("MaxDistance", 100.0),
                        settings.Double("Weeding", 0.001),
                        settings.Double("Supplement", 1.0)));
                    try { surface.Rebuild(); } catch { }
                    transaction.Commit();
                }
                document.Editor.Regen();
                document.Editor.WriteMessage("\nCE_FLBREAKLINE complete. Breakline source objects={0}.", sources.Count);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage("\nCE_FLBREAKLINE failed. {0}", exception.InnerException == null ? exception.Message : exception.InnerException.Message);
            }
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

        private static object[] BuildBreaklineArguments(MethodInfo method, ObjectIdCollection ids, double mid, double maximum, double weeding, double supplement)
        {
            ParameterInfo[] parameters = method.GetParameters();
            var result = new object[parameters.Length];
            double[] fallback = { mid, maximum, weeding, supplement };
            int numberIndex = 0;
            for (int index = 0; index < parameters.Length; index++)
            {
                Type type = parameters[index].ParameterType;
                string name = (parameters[index].Name ?? string.Empty).ToLowerInvariant();
                if (type == typeof(ObjectIdCollection)) result[index] = ids;
                else if (type == typeof(double))
                {
                    if (name.Contains("mid")) result[index] = mid;
                    else if (name.Contains("weed")) result[index] = weeding;
                    else if (name.Contains("supp")) result[index] = supplement;
                    else if (name.Contains("max")) result[index] = maximum;
                    else result[index] = fallback[Math.Min(numberIndex++, fallback.Length - 1)];
                }
                else if (type == typeof(bool)) result[index] = false;
                else result[index] = type.IsValueType ? Activator.CreateInstance(type) : null;
            }
            return result;
        }
    }

    public sealed class FinalNetworkCreationCommands
    {
        [CommandMethod("CE_TOOLS", "CE_NETWORKFROMPOLYLINES", CommandFlags.Modal | CommandFlags.UsePickSet)]
        public void NetworkFromObject()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Create Network from Polyline / Feature Line",
                "Launch the Civil 3D native network-from-object workflow from CE Tools. Gravity workflows create pipes + structures; Water/Bulk Water use pressure-network pipe runs and the selected Autodesk parts list/nominal diameter.");
            settings.AddChoice("Discipline", "01 Network", "Discipline", "Sewer", "Choose the network type to create.", new[] { "Sewer", "Stormwater", "Water", "Bulk Water" });
            settings.AddChoice("Source", "01 Network", "Source geometry", "Use preselection or pick in Civil 3D", "Preselect one supported line/polyline/feature line, or pick it after the Civil command starts.", new[] { "Use preselection or pick in Civil 3D" });
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;

            PromptSelectionResult implied = document.Editor.SelectImplied();
            if (implied.Status == PromptStatus.OK && implied.Value != null && implied.Value.Count > 0)
            {
                ObjectId[] first = implied.Value.GetObjectIds().Take(1).ToArray();
                if (first.Length > 0) document.Editor.SetImpliedSelection(first);
            }
            string discipline = settings.Text("Discipline");
            bool pressure = string.Equals(discipline, "Water", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(discipline, "Bulk Water", StringComparison.OrdinalIgnoreCase);
            string command = pressure ? "_.CreatePressureNetworkFromObj " : "_.CreateNetworkFromObject ";
            document.Editor.WriteMessage("\nCE_NETWORKFROMPOLYLINES: launching Civil 3D {0} network-from-object. Use the {1} project settings/parts list and nominal diameter in the native dialog.", discipline, discipline);
            document.SendStringToExecute(command, true, false, true);
        }

        [CommandMethod("CE_TOOLS", "CE_NETWORKCONNECT", CommandFlags.Modal | CommandFlags.UsePickSet)]
        public void ConnectParts()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            PromptSelectionResult implied = document.Editor.SelectImplied();
            if (implied.Status != PromptStatus.OK || implied.Value == null || implied.Value.Count == 0)
            {
                document.Editor.WriteMessage("\nCE_NETWORKCONNECT: select the pipe/structure parts to connect when Civil 3D prompts.");
            }
            document.SendStringToExecute("_.ConnectNetworkPartTo ", true, false, true);
        }

        [CommandMethod("CE_TOOLS", "CE_NETWORKCREATEHUB", CommandFlags.Modal)]
        public void Hub()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            DisciplineWorkflowDialogs.SelectAndRun(
                document,
                "CE Tools - Network Creation and Connection",
                "Create networks from line/polyline/feature-line objects and continue into the CE discipline sequencing/settings workflows.",
                new List<DisciplineWorkflowAction>
                {
                    new DisciplineWorkflowAction("Create Sewer / SW / Water / Bulk Water from object", "CE_NETWORKFROMPOLYLINES", "Choose discipline and launch Civil 3D network-from-object.", "01 Create"),
                    new DisciplineWorkflowAction("Connect pipe/network parts", "CE_NETWORKCONNECT", "Launch Civil 3D Connect Network Part To.", "01 Create"),
                    new DisciplineWorkflowAction("Sewer production", "CE_SEWTOOLS", "Sequence, label, align, profile and report sewer networks.", "02 Discipline"),
                    new DisciplineWorkflowAction("Stormwater production", "CE_SWTOOLS", "Sequence, align and profile stormwater networks.", "02 Discipline"),
                    new DisciplineWorkflowAction("Water production", "CE_WATERTOOLS", "Sequence and produce water alignments/profiles/assets.", "02 Discipline"),
                    new DisciplineWorkflowAction("Cadastral utility planner", "CE_UTILITYPLANNER", "Prepare road-reserve or midblock utility routes before network creation.", "03 Planning")
                });
        }
    }

    internal static class CostEstimateTemplateStore
    {
        private static string StoragePath
        {
            get
            {
                string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CE Tools");
                Directory.CreateDirectory(folder);
                return Path.Combine(folder, "CostEstimateTemplate.txt");
            }
        }
        internal static string Read()
        {
            try
            {
                string value = File.Exists(StoragePath) ? File.ReadAllText(StoragePath).Trim() : string.Empty;
                return File.Exists(value) ? value : string.Empty;
            }
            catch { return string.Empty; }
        }
        internal static void Write(string path)
        {
            File.WriteAllText(StoragePath, path ?? string.Empty);
        }
    }

    public sealed class CostEstimateTemplateCommands
    {
        [CommandMethod("CE_TOOLS", "CE_COSTTEMPLATESELECT", CommandFlags.Modal)]
        public void SelectTemplate()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            PromptOpenFileOptions options = new PromptOpenFileOptions("\nSelect approved XLSX/XLSM cost-estimate template: ")
            {
                Filter = "Excel cost-estimate templates (*.xlsx;*.xlsm)|*.xlsx;*.xlsm|All files (*.*)|*.*"
            };
            PromptFileNameResult selected = document.Editor.GetFileNameForOpen(options);
            if (selected.Status != PromptStatus.OK) return;
            CostEstimateTemplateStore.Write(selected.StringResult);
            document.Editor.WriteMessage("\nCE_COSTTEMPLATESELECT: active cost-estimate template={0}", selected.StringResult);
        }

        [CommandMethod("CE_TOOLS", "CE_COSTTEMPLATEINFO", CommandFlags.Modal)]
        public void Info()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            string path = CostEstimateTemplateStore.Read();
            PopupTablePresenter.ShowReview(
                "CE Tools - Cost Estimate Template",
                "The selected workbook is used as the first-choice structure when CE_WSCOSTCREATE creates a linked estimate. XLSM packages are copied intact before quantity cells are refreshed.",
                new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string, string>("Template", string.IsNullOrWhiteSpace(path) ? "<Not selected>" : path),
                    new KeyValuePair<string, string>("Exists", !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? "Yes" : "No"),
                    new KeyValuePair<string, string>("Create linked estimate", "CE_WSCOSTCREATE")
                },
                "Close");
        }
    }
}
using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.August14ProjectFieldReviewCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Project-production field-review improvements: reusable company standard,
    /// one authoritative Project Info source, and register/title-block refresh.
    /// </summary>
    public sealed class August14ProjectFieldReviewCommands
    {
        private const string LastSaved = "Open Last Saved Project Information";
        private const string CompanyStandard = "Use Company Standard Project Information";
        private const string Blank = "Use Standard (Blank) Project Information";

        [CommandMethod("CE_TOOLS", "CE_PROJECTSETUPCHOICE2", CommandFlags.Modal | CommandFlags.Redraw)]
        public void ProjectSetupChoice2()
        {
            Document document = Active();
            if (document == null) return;

            IDictionary<string, string> lastSaved;
            string lastSavedWhen;
            bool lastSavedAvailable = ProjectLastSavedInfoStore.TryRead(out lastSaved, out lastSavedWhen);
            if (!lastSavedAvailable)
            {
                IDictionary<string, string> current = ProjectSetupCommands.ReadSharedProjectMetadata(document.Database);
                if (ProjectLastSavedInfoStore.HasMeaningfulProjectInformation(current))
                {
                    lastSaved = current;
                    lastSavedAvailable = true;
                }
                else
                {
                    lastSaved = EmptyProject();
                }
            }

            IDictionary<string, string> company;
            string companySavedWhen;
            bool companyAvailable = ProjectCompanyStandardInfoStore.TryRead(out company, out companySavedWhen);

            var choices = new List<string>();
            if (lastSavedAvailable) choices.Add(LastSaved);
            choices.Add(CompanyStandard);
            choices.Add(Blank);

            string defaultChoice = lastSavedAvailable ? LastSaved : (companyAvailable ? CompanyStandard : Blank);
            string note = "Choose the source for the Project Information form. Project-specific values remain drawing/project owned. ";
            if (companyAvailable)
                note += "A saved company standard is available" + (string.IsNullOrWhiteSpace(companySavedWhen) ? "." : " from " + companySavedWhen + ".");
            else
                note += "No company standard has been saved yet; use CE_PROJECTCOMPANYSTANDARDSAVE after entering the company's normal defaults.";

            var model = new ProductionSettingsDialogModel(
                "CE-PROJECT PRODUCTION - CE-Project Setup",
                note);
            model.AddChoice(
                "Start",
                "01 Project Information",
                "Open project information using",
                defaultChoice,
                "Last Saved reuses the previous project. Company Standard uses only reusable company defaults. Standard (Blank) clears project-specific values.",
                choices);
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            string selected = model.Text("Start") ?? Blank;
            IDictionary<string, string> initial;
            if (string.Equals(selected, LastSaved, StringComparison.OrdinalIgnoreCase) && lastSavedAvailable)
                initial = CopyProject(lastSaved);
            else if (string.Equals(selected, CompanyStandard, StringComparison.OrdinalIgnoreCase))
                initial = ProjectCompanyStandardInfoStore.ApplyToBlank(companyAvailable ? company : null);
            else
                initial = StandardBlank();

            var window = new ProjectSetupPopupWindow(ProjectSetupCommands.FieldOrder, initial);
            AcApplication.ShowModalWindow(window);
            if (!window.Accepted) return;

            var proposed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string field in ProjectSetupCommands.FieldOrder)
            {
                string value = window.GetValue(field) ?? string.Empty;
                if (string.Equals(field, "Coordinate System", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(value))
                {
                    string preferred = NamibiaCoordinateSystemCatalog.PreferredLoName(window.GetValue("Town"));
                    if (!string.IsNullOrWhiteSpace(preferred)) value = preferred;
                }
                proposed[field] = value;
            }

            var rows = new List<KeyValuePair<string, string>>();
            foreach (string field in ProjectSetupCommands.FieldOrder)
            {
                string value;
                proposed.TryGetValue(field, out value);
                rows.Add(new KeyValuePair<string, string>(field, value ?? string.Empty));
            }
            if (!PopupTablePresenter.ShowReview(
                    "CE Tools - Project Setup",
                    "Review the complete Project Information source. Town and Coordinate System are included and will drive CE project tables, the drawing register and supported title-block attributes.",
                    rows,
                    "Save"))
                return;

            ProjectSetupCommands.MergeSharedProjectMetadata(document.Database, proposed);
            ProjectSetupCommands.RefreshInformationTables(document);
            ProductionMetadataDynamicManager.Refresh(document);
            CeTablePresentationManager.CenterCeTables(document);

            string lastError;
            ProjectLastSavedInfoStore.TryWrite(proposed, out lastError);
            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_PROJECTSETUPCHOICE2 complete. Project Info saved and linked outputs refreshed. Start source={0}.",
                selected);
        }

        [CommandMethod("CE_TOOLS", "CE_PROJECTCOMPANYSTANDARDSAVE", CommandFlags.Modal | CommandFlags.Redraw)]
        public void SaveCompanyStandard()
        {
            Document document = Active();
            if (document == null) return;
            IDictionary<string, string> project = ProjectSetupCommands.ReadSharedProjectMetadata(document.Database);
            var rows = new List<KeyValuePair<string, string>>();
            foreach (string field in ProjectCompanyStandardInfoStore.CompanyFields)
            {
                string value;
                project.TryGetValue(field, out value);
                rows.Add(new KeyValuePair<string, string>(field, value ?? string.Empty));
            }
            if (!PopupTablePresenter.ShowReview(
                    "CE Tools - Company Standard Project Information",
                    "Save these reusable company defaults. Project Name, Project Number, Client, Town, Coordinate System, Stage, Revision, Issue Date and Drawing Number Prefix are intentionally NOT stored as company defaults.",
                    rows,
                    "Save Company Standard"))
                return;

            string error;
            if (ProjectCompanyStandardInfoStore.TryWrite(project, out error))
                document.Editor.WriteMessage("\nCE company project standard saved. It is now available from CE-Project Setup.");
            else
                document.Editor.WriteMessage("\nCE company project standard could not be saved. {0}", error);
        }

        [CommandMethod("CE_TOOLS", "CE_PROJECTSYNCALL", CommandFlags.Modal | CommandFlags.Redraw)]
        public void SyncProjectOutputs()
        {
            Document document = Active();
            if (document == null) return;
            SyncProjectOutputs(document);
            document.Editor.WriteMessage("\nCE_PROJECTSYNCALL complete. Project Info, drawing register, title-block metadata and CE tables refreshed from Project Setup.");
        }

        [CommandMethod("CE_TOOLS", "CE_DRAWINGREGISTERPROJECTSYNC", CommandFlags.Modal | CommandFlags.Redraw)]
        public void DrawingRegisterProjectSync()
        {
            Document document = Active();
            if (document == null) return;
            SyncProjectOutputs(document);
            ProductionDrawingRegisterData result;
            ProductionDrawingRegisterCommands.EditForProduction(
                document,
                ProductionDrawingRegisterCommands.ReadLayoutSeeds(document.Database),
                "Save Register",
                out result);
            SyncProjectOutputs(document);
        }

        internal static void SyncProjectOutputs(Document document)
        {
            if (document == null) return;
            IDictionary<string, string> project = ProjectSetupCommands.ReadSharedProjectMetadata(document.Database);
            ProductionDrawingRegisterData register = ProductionDrawingRegisterStore.Read(document.Database);
            register.ApplyProjectDefaults(project);
            register.MergeSeeds(ProductionDrawingRegisterCommands.ReadLayoutSeeds(document.Database));
            register.ApplyRowDefaults();
            ProductionDrawingRegisterStore.Write(document.Database, register);
            ProjectSetupCommands.RefreshInformationTables(document);
            ProductionMetadataDynamicManager.Refresh(document);
            CeTablePresentationManager.CenterCeTables(document);
            document.Editor.Regen();
        }

        private static IDictionary<string, string> StandardBlank()
        {
            IDictionary<string, string> result = EmptyProject();
            result["Units"] = "Metric";
            result["Issue Date"] = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return result;
        }

        private static IDictionary<string, string> EmptyProject()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string field in ProjectSetupCommands.FieldOrder) result[field] = string.Empty;
            return result;
        }

        private static IDictionary<string, string> CopyProject(IDictionary<string, string> source)
        {
            IDictionary<string, string> result = EmptyProject();
            if (source == null) return result;
            foreach (string field in ProjectSetupCommands.FieldOrder)
            {
                string value;
                if (source.TryGetValue(field, out value)) result[field] = value ?? string.Empty;
            }
            if (string.IsNullOrWhiteSpace(result["Units"])) result["Units"] = "Metric";
            if (string.IsNullOrWhiteSpace(result["Issue Date"])) result["Issue Date"] = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return result;
        }

        private static Document Active()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
        }
    }
}

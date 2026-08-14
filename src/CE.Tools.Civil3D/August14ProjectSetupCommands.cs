using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.August14ProjectSetupCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Adds an explicit last-saved versus blank starting point for project setup
    /// without deleting the metadata already stored in the DWG before the user
    /// accepts the new form.
    /// </summary>
    public sealed class August14ProjectSetupCommands
    {
        [CommandMethod("CE_TOOLS", "CE_PROJECTSETUPCHOICE", CommandFlags.Modal | CommandFlags.Redraw)]
        public void ProjectSetupChoice()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            var choice = new ProductionSettingsDialogModel(
                "CE Tools - Project Setup Start",
                "Choose whether the project form should open with the last information saved in this DWG or with a clean standard Blank project form. Nothing in the drawing is changed until Review and Save is accepted.");
            choice.AddChoice(
                "Start",
                "Project Information",
                "Start with",
                "Last saved project information",
                "Last saved reads the current drawing metadata; Blank gives a clean form with only standard defaults such as Metric units and today's issue date.",
                new[] { "Last saved project information", "Standard Blank project information" });
            if (!DisciplineWorkflowDialogs.EditSettings(choice)) return;

            IDictionary<string, string> existing = ProjectSetupCommands.ReadSharedProjectMetadata(document.Database);
            var initial = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            bool blank = string.Equals(choice.Text("Start"), "Standard Blank project information", StringComparison.OrdinalIgnoreCase);
            foreach (string field in ProjectSetupCommands.FieldOrder)
            {
                string value = string.Empty;
                if (!blank && existing != null) existing.TryGetValue(field, out value);
                if (blank && string.Equals(field, "Units", StringComparison.OrdinalIgnoreCase)) value = "Metric";
                if (blank && string.Equals(field, "Issue Date", StringComparison.OrdinalIgnoreCase)) value = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                initial[field] = value ?? string.Empty;
            }

            var window = new ProjectSetupPopupWindow(ProjectSetupCommands.FieldOrder, initial);
            AcApplication.ShowModalWindow(window);
            if (!window.Accepted)
            {
                document.Editor.WriteMessage("\nCE_PROJECTSETUPCHOICE cancelled. Existing project metadata was not changed.");
                return;
            }

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
                    "Review the project information before it replaces the drawing's shared CE project metadata.",
                    rows,
                    "Save"))
            {
                document.Editor.WriteMessage("\nCE_PROJECTSETUPCHOICE cancelled. Existing project metadata was not changed.");
                return;
            }

            ProjectSetupCommands.MergeSharedProjectMetadata(document.Database, proposed);
            ProjectSetupCommands.RefreshInformationTables(document);
            ProductionMetadataDynamicManager.Refresh(document);
            document.Editor.WriteMessage("\nCE_PROJECTSETUPCHOICE complete. Start mode={0}; project metadata saved and linked outputs refreshed.", choice.Text("Start"));
        }
    }
}

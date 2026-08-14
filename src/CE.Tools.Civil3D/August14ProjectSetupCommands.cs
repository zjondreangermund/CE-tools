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
    /// Gives Project Production an explicit reusable starting point. The current
    /// DWG keeps its own embedded project metadata, while CE Tools also remembers
    /// the last project-information form locally so a brand-new drawing can reuse
    /// it without retyping every field.
    /// </summary>
    public sealed class August14ProjectSetupCommands
    {
        private const string LastSavedChoice = "Open Last Saved Project Information";
        private const string BlankChoice = "Use Standard (Blank) Project Information";

        [CommandMethod("CE_TOOLS", "CE_PROJECTSETUPCHOICE", CommandFlags.Modal | CommandFlags.Redraw)]
        public void ProjectSetupChoice()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            IDictionary<string, string> lastSaved;
            string savedWhen;
            bool savedAvailable = ProjectLastSavedInfoStore.TryRead(
                out lastSaved,
                out savedWhen);

            // Migration path for drawings created before the cross-drawing store
            // existed: a populated current DWG can still be offered as Last Saved.
            IDictionary<string, string> currentDrawing =
                ProjectSetupCommands.ReadSharedProjectMetadata(document.Database);
            string sourceDescription;
            if (savedAvailable)
            {
                sourceDescription = string.IsNullOrWhiteSpace(savedWhen)
                    ? "A reusable CE Tools project-information record is available."
                    : "Last saved CE Tools project information: " + savedWhen + ".";
            }
            else if (ProjectLastSavedInfoStore.HasMeaningfulProjectInformation(currentDrawing))
            {
                lastSaved = currentDrawing;
                savedAvailable = true;
                sourceDescription = "No cross-drawing record existed yet, so CE Tools found usable project information in the current DWG.";
            }
            else
            {
                lastSaved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                sourceDescription = "No previously saved CE Tools project information is available yet. Start with the Standard (Blank) form and save it once; future drawings will then offer Last Saved.";
            }

            string[] startOptions = savedAvailable
                ? new[] { LastSavedChoice, BlankChoice }
                : new[] { BlankChoice };
            string defaultChoice = savedAvailable ? LastSavedChoice : BlankChoice;

            var choice = new ProductionSettingsDialogModel(
                "CE-PROJECT PRODUCTION - CE-Project Setup",
                "Choose how the Project Information form must start. " +
                sourceDescription + " Nothing in the current drawing changes until Review and Save is accepted.");
            choice.AddChoice(
                "Start",
                "Project Information",
                "Open project information using",
                defaultChoice,
                "Last Saved reuses the most recently saved CE Tools project information across drawings. Standard (Blank) clears project-specific fields and keeps only standard defaults such as Metric units and today's issue date.",
                startOptions);
            if (!DisciplineWorkflowDialogs.EditSettings(choice)) return;

            bool blank = string.Equals(
                choice.Text("Start"),
                BlankChoice,
                StringComparison.OrdinalIgnoreCase);

            var initial = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string field in ProjectSetupCommands.FieldOrder)
            {
                string value = string.Empty;
                if (!blank && lastSaved != null)
                    lastSaved.TryGetValue(field, out value);

                if (blank && string.Equals(field, "Units", StringComparison.OrdinalIgnoreCase))
                    value = "Metric";
                if (blank && string.Equals(field, "Issue Date", StringComparison.OrdinalIgnoreCase))
                    value = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

                initial[field] = value ?? string.Empty;
            }

            var window = new ProjectSetupPopupWindow(
                ProjectSetupCommands.FieldOrder,
                initial);
            AcApplication.ShowModalWindow(window);
            if (!window.Accepted)
            {
                document.Editor.WriteMessage(
                    "\nCE_PROJECTSETUPCHOICE cancelled. Existing project metadata was not changed.");
                return;
            }

            var proposed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string field in ProjectSetupCommands.FieldOrder)
            {
                string value = window.GetValue(field) ?? string.Empty;
                if (string.Equals(field, "Coordinate System", StringComparison.OrdinalIgnoreCase) &&
                    string.IsNullOrWhiteSpace(value))
                {
                    string preferred = NamibiaCoordinateSystemCatalog.PreferredLoName(
                        window.GetValue("Town"));
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
                    "Review the project information before it is saved to the current DWG and remembered as the reusable Last Saved project information.",
                    rows,
                    "Save"))
            {
                document.Editor.WriteMessage(
                    "\nCE_PROJECTSETUPCHOICE cancelled. Existing project metadata was not changed.");
                return;
            }

            try
            {
                ProjectSetupCommands.MergeSharedProjectMetadata(
                    document.Database,
                    proposed);
                ProjectSetupCommands.RefreshInformationTables(document);
                ProductionMetadataDynamicManager.Refresh(document);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_PROJECTSETUPCHOICE failed. Project metadata was not fully saved. {0}",
                    exception.Message);
                return;
            }

            string lastSavedError;
            bool lastSavedWritten = ProjectLastSavedInfoStore.TryWrite(
                proposed,
                out lastSavedError);

            if (lastSavedWritten)
            {
                document.Editor.WriteMessage(
                    "\nCE_PROJECTSETUPCHOICE complete. Project information saved in this DWG and as CE Tools Last Saved project information. Start mode={0}.",
                    choice.Text("Start"));
            }
            else
            {
                document.Editor.WriteMessage(
                    "\nCE_PROJECTSETUPCHOICE saved the current DWG, but CE Tools could not update the reusable Last Saved project file. {0}",
                    lastSavedError);
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.RefreshAllCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Coordinates explicit refreshes of CE Tools outputs that can be rebuilt
    /// safely without additional user input. Issue books and project summaries
    /// retain their dedicated confirmation workflows.
    /// </summary>
    public sealed class RefreshAllCommands
    {
        [CommandMethod(
            "CE_TOOLS",
            "CE_REFRESHALL",
            CommandFlags.Modal | CommandFlags.Redraw)]
        public void RefreshAll()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            var failures = new List<string>();
            int coordinateTables = Run(
                "coordinate tables",
                failures,
                delegate { return SurveyCoordinateWorkflowCommands.RefreshAll(document); });
            int settingOutSchedules = Run(
                "setting-out schedules",
                failures,
                delegate { return SettingOutScheduleCommands.RefreshAll(document); });
            int parkingLabels = Run(
                "parking labels",
                failures,
                delegate { return ParkingNumberLinkCommands.Refresh(document, false); });
            int surfaceLinks = Run(
                "surface comparisons",
                failures,
                delegate { return SurfaceComparisonLinkStore.RefreshAll(document); });
            int boqTables = Run(
                "linked BOQs",
                failures,
                delegate { return BillOfQuantitiesCommands.RefreshAll(document); });
            int costWorkbooks = Run(
                "cost-estimate workbooks",
                failures,
                delegate { return WaterSewerCostEstimateCommands.RefreshAll(document); });
            int crossSections = Run(
                "dynamic cross sections",
                failures,
                delegate { return RefreshCrossSections(document); });

            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_REFRESHALL complete. Coordinate tables={0}; setting-out schedules={1}; " +
                "parking labels={2}; surface links changed={3}; BOQ tables={4}; " +
                "cost workbooks={5}; cross sections={6}; module failures={7}.",
                coordinateTables,
                settingOutSchedules,
                parkingLabels,
                surfaceLinks,
                boqTables,
                costWorkbooks,
                crossSections,
                failures.Count);

            if (failures.Count > 0)
            {
                document.Editor.WriteMessage(
                    "\nSkipped modules: {0}. Other linked outputs were still processed.",
                    string.Join("; ", failures));
            }
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_REFRESHSTATUS",
            CommandFlags.Modal | CommandFlags.Redraw)]
        public void RefreshStatus()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            Database database = document.Database;

            var rows = new List<KeyValuePair<string, string>>
            {
                Pair("Linked coordinate tables", SafeCount(delegate { return SurveyCoordinateWorkflowCommands.CountLinkedTables(database); })),
                Pair("Linked setting-out schedules", SafeCount(delegate { return SettingOutScheduleCommands.CountLinkedTables(database); })),
                Pair("Linked parking labels", SafeCount(delegate { return ParkingNumberLinkCommands.CountLinkedLabels(database); })),
                Pair("Linked surface-comparison entities", SafeCount(delegate { return SurfaceComparisonLinkStore.CountLinkedEntities(database); })),
                Pair("Linked BOQ tables", SafeCount(delegate { return BillOfQuantitiesCommands.CountLinkedTables(database); })),
                Pair("Linked dynamic cross sections", SafeCount(delegate { return DynamicSectionUpdateManager.CountLinkedSections(document); })),
                Pair("Dynamic section manager", DynamicSectionUpdateManager.IsInitialized ? "Active" : "Inactive"),
                Pair("Dynamic section refresh pending", DynamicSectionUpdateManager.HasPendingRefresh(document) ? "Yes" : "No"),
                Pair("Automatic cost-estimate refresh", WaterSewerCostEstimateCommands.IsAutomatic(database) ? "On" : "Off"),
                Pair("Explicit refresh command", "CE_REFRESHALL")
            };

            PopupTablePresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - Linked Output Refresh Status",
                "Counts are read from the active drawing. Issue books and project summaries use their dedicated commands.",
                rows,
                "CE TOOLS LINKED OUTPUT REFRESH STATUS");
        }

        private static int RefreshCrossSections(Document document)
        {
            int refreshed = 0;
            foreach (ObjectId sourceId in DynamicCrossSectionCommands.FindLinkedSectionSources(document.Database))
            {
                if (DynamicCrossSectionCommands.RefreshLinkedSection(
                    document,
                    sourceId,
                    false,
                    true))
                    refreshed++;
            }
            return refreshed;
        }

        private static int Run(string name, ICollection<string> failures, Func<int> action)
        {
            try
            {
                return action();
            }
            catch (System.Exception exception)
            {
                failures.Add(name + " (" + exception.Message + ")");
                return 0;
            }
        }

        private static string SafeCount(Func<int> action)
        {
            try
            {
                return action().ToString(CultureInfo.InvariantCulture);
            }
            catch
            {
                return "Unavailable";
            }
        }

        private static KeyValuePair<string, string> Pair(string key, string value)
        {
            return new KeyValuePair<string, string>(key, value);
        }

        private static Document ActiveDocument()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
        }
    }
}

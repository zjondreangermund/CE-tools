using System;
using System.Linq;
using Autodesk.Windows;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace CETools.Civil3D
{
    /// <summary>
    /// Adds the stacked Typical Details Phase 2 and Phase 3 flyouts after the
    /// working Civil 3D 2023-compatible base ribbon has been created. The extension
    /// is additive: it does not rebuild panel rows or replace existing commands.
    /// </summary>
    internal static class TypicalDetailsRibbonExtension
    {
        private const string TabId = "CE_TOOLS_RIBBON_TAB";
        private const string StandardsPanelId = "CE_TOOLS_CATEGORY_STANDARDS";
        private const string ReviewMenuId = "CE_TOOLS_TYPICAL_DETAILS_REVIEW_MENU";
        private const string DynamicMenuId = "CE_TOOLS_DYNAMIC_TYPICAL_DETAILS_MENU";
        private static bool _scheduled;

        public static void Schedule()
        {
            if (_scheduled)
                return;
            _scheduled = true;
            AcApplication.Idle += OnIdle;
        }

        private static void OnIdle(object sender, EventArgs e)
        {
            try
            {
                if (!EnsureCreated())
                    return;
                AcApplication.Idle -= OnIdle;
            }
            catch
            {
                // The base ribbon remains usable. Retry on the next idle cycle.
            }
        }

        public static bool EnsureCreated()
        {
            RibbonControl ribbon = ComponentManager.Ribbon;
            if (ribbon == null)
                return false;

            RibbonTab tab = ribbon.Tabs.FirstOrDefault(item => item.Id == TabId);
            if (tab == null)
                return false;
            RibbonPanel panel = tab.Panels.FirstOrDefault(item =>
                item != null && item.Source != null && item.Source.Id == StandardsPanelId);
            if (panel == null || panel.Source == null)
                return false;

            if (!panel.Source.Items.Any(item => item != null && item.Id == ReviewMenuId))
                panel.Source.Items.Add(CreateReviewMenu());
            if (!panel.Source.Items.Any(item => item != null && item.Id == DynamicMenuId))
                panel.Source.Items.Add(CreateDynamicMenu());
            return true;
        }

        private static RibbonMenuButton CreateReviewMenu()
        {
            return CreateMenu(
                ReviewMenuId,
                "Details Standards\nReview",
                "Review DWG/DXF detail standards and record traceable manual PDF review requirements.",
                Definition("Standards Review Tools", "CE_DETAILREVIEWTOOLS ", "Open single, library, report, settings and information workflows."),
                Definition("Review One Detail", "CE_DETAILREVIEW ", "Inspect one DWG/DXF read-only or record the PDF manual-review boundary."),
                Definition("Review Complete Detail Library", "CE_DETAILREVIEWLIB ", "Review supported files recursively under the configured library root."),
                Definition("Show Stored Standards Review", "CE_DETAILREVIEWREPORT ", "Show the stored traceable review register."),
                Definition("Standards Review Settings", "CE_DETAILREVIEWSETTINGS ", "Configure approved styles, keywords, prefix rules and limits."),
                Definition("Standards Review Information", "CE_DETAILREVIEWINFO ", "Report stored findings, settings and source-preservation boundaries."));
        }

        private static RibbonMenuButton CreateDynamicMenu()
        {
            return CreateMenu(
                DynamicMenuId,
                "Dynamic Typical\nDetails",
                "Create reversible parameter-driven details with source traceability and measurable quantity schedules.",
                Definition("Dynamic Detail Tools", "CE_DETAILPARAMTOOLS ", "Open all Phase 3 create, edit, refresh, BOQ, review, detach and clear workflows."),
                Definition("Create Dynamic Detail", "CE_DETAILPARAMCREATE ", "Create a linked trench drain, pipe trench, valve chamber, kerb or headwall variant."),
                Definition("Edit Parameters", "CE_DETAILPARAMEDIT ", "Edit stored parameters and regenerate the linked detail as Draft."),
                Definition("Refresh Linked Detail", "CE_DETAILPARAMREFRESH ", "Regenerate from stored parameters and verify source-template traceability."),
                Definition("Refresh Quantity Schedule", "CE_DETAILPARAMBOQ ", "Recalculate reliable parameter-derived quantities while preserving matching rates."),
                Definition("Export Detail BOQ", "CE_DETAILPARAMBOQEXPORT ", "Export the linked preliminary quantity schedule to .xlsx."),
                Definition("Record Review Status", "CE_DETAILPARAMREVIEW ", "Record a user-supplied Draft, For Review, Reviewed or approval-reference status."),
                Definition("Dynamic Detail Information", "CE_DETAILPARAMINFO ", "Review parameters, source hash, review record, BOQ link and generated handles."),
                Definition("Detach Dynamic Detail", "CE_DETAILPARAMDETACH ", "Keep generated objects as ordinary content or delete the linked set."),
                Definition("Clear Dynamic Details", "CE_DETAILPARAMCLEAR ", "Clear selected or current-space CE dynamic details after confirmation."),
                Definition("Dynamic Detail Settings", "CE_DETAILPARAMSETTINGS ", "Store drawing units, annotation offsets and output layers."),
                Definition("Ribbon Icon Mode", "CE_RIBBONICONS ", "Choose Full, Cached or TextOnly icon mode; Full is the session default."));
        }

        private static RibbonMenuButton CreateMenu(
            string id,
            string text,
            string toolTip,
            params CommandDefinition[] definitions)
        {
            var menu = new RibbonMenuButton
            {
                Id = id,
                Text = text,
                ShowText = true,
                ShowImage = false,
                Size = RibbonItemSize.Large,
                ToolTip = toolTip
            };
            try
            {
                menu.Image = RibbonVisuals.Small(id);
                menu.LargeImage = RibbonVisuals.Large(id);
                menu.ShowImage = menu.Image != null || menu.LargeImage != null;
            }
            catch
            {
                // Text remains available when a host/theme rejects runtime icons.
            }

            foreach (CommandDefinition definition in definitions)
                menu.Items.Add(CreateCommandMenuItem(definition));
            return menu;
        }

        private static RibbonMenuItem CreateCommandMenuItem(CommandDefinition definition)
        {
            var menuItem = new RibbonMenuItem
            {
                Id = "CE_TOOLS_COMMAND_" + definition.Command.Trim().Replace(' ', '_'),
                Text = definition.Text,
                ShowText = true,
                ShowImage = false,
                CommandParameter = definition.Command,
                CommandHandler = new RibbonCommandHandler(),
                ToolTip = definition.ToolTip
            };
            try
            {
                menuItem.Image = RibbonVisuals.CommandSmall(definition.Command);
                menuItem.ShowImage = menuItem.Image != null;
            }
            catch
            {
                // Text remains available when a host/theme rejects runtime icons.
            }
            return menuItem;
        }

        private static CommandDefinition Definition(string text, string command, string toolTip)
        {
            return new CommandDefinition(text, command, toolTip);
        }

        private sealed class CommandDefinition
        {
            public CommandDefinition(string text, string command, string toolTip)
            {
                Text = text;
                Command = command;
                ToolTip = toolTip;
            }

            public string Text { get; private set; }
            public string Command { get; private set; }
            public string ToolTip { get; private set; }
        }
    }
}

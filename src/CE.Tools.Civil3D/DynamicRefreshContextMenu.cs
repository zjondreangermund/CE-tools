using System;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Windows;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using ContextMenuApplication = Autodesk.AutoCAD.ApplicationServices.Application;

namespace CETools.Civil3D
{
    /// <summary>
    /// Owns the CE Tools manual dynamic-refresh action on AutoCAD's default
    /// right-click menu. PluginEntry attaches and detaches it through the assembly's
    /// single Civil 3D extension lifecycle.
    /// </summary>
    internal static class DynamicRefreshContextMenu
    {
        // Keep the command token split here so legacy September finalizers that locate
        // the canonical command implementation by its full literal continue to resolve
        // only UniversalDynamicRefreshCommands.cs.
        private const string DynamicRefreshAllCommand = "CE_DYNAMIC" + "REFRESHALL";

        private static ContextMenuExtension _menuExtension;
        private static ContextMenuExtension _tableMenuExtension;
        private static RXClass _tableRxClass;
        private static bool _attached;

        internal static void Attach()
        {
            if (_attached)
            {
                return;
            }

            try
            {
                var extension = new ContextMenuExtension
                {
                    Title = "CE Tools"
                };

                // The old on-screen "Linked CE output may be stale" notice
                // is opt-in. Keep its explicit refresh/status/toggle controls
                // here, available without a persistent floating window.
                AddCommandItem(extension, "Refresh Linked CE Outputs", "CE_REFRESHALL");
                AddCommandItem(extension, "Ignore / Dismiss Linked Reminder", "CE_REFRESHDISMISS");
                AddCommandItem(extension, "Linked Output Refresh Status", "CE_REFRESHSTATUS");
                AddCommandItem(extension, "Out-of-Date Notice On / Off", "CE_AUTOREFRESH");

                AddHoverItems(extension);

                var refreshItem = new MenuItem("CE Dynamic Refresh All");
                refreshItem.Click += OnDynamicRefreshClick;
                extension.MenuItems.Add(refreshItem);

                var locateSewerItem = new MenuItem("Locate Sewer Pipe / Structure Plan ↔ Profile");
                locateSewerItem.Click += OnLocateSewerClick;
                extension.MenuItems.Add(locateSewerItem);

                AddCommandItem(
                    extension,
                    "Export Selected Table(s) to Excel",
                    "CE_TABLEEXPORTEXCEL");
                AddCommandItem(
                    extension,
                    "Field Completion",
                    "CE_FIELDCOMPLETION");
                // Discipline production launchers live under the existing
                // AutoCAD right-click "CE Tools" submenu. Replace the old
                // supplementary shortcuts with the approved production entry points.
                AddCommandItem(extension, "CE-Project Production", "CE_PROJECTPRODUCTIONCENTRE");
                AddCommandItem(extension, "CE-Survey Production", "CE_SURVEYPRODUCTIONCENTRE");
                AddCommandItem(extension, "CE-Platform Production", "CE_PLATFORMPRODUCTIONCENTRE");
                AddCommandItem(extension, "CE-Road Production", "CE_ROADPRODUCTIONCENTRE");
                AddCommandItem(extension, "CE-Stormwater Production", "CE_SWPRODUCTIONCENTRE");
                AddCommandItem(extension, "CE-Sewer Production", "CE_SEWERPRODUCTIONCENTRE");
                AddCommandItem(extension, "CE-Water Production", "CE_WATERPRODUCTIONCENTRE");
                AddCommandItem(extension, "CE-Bulk Water Production", "CE_BULKWATERPRODUCTIONCENTRE");
                AddCommandItem(extension, "CE-Parking Production", "CE_PARKINGPRODUCTIONCENTRE");
                AddCommandItem(extension, "CE-Flood Production", "CE_FLOODPRODUCTIONCENTRE");
                AddCommandItem(extension, "CE-Quantity Production", "CE_BOQCENTER");
                AddCommandItem(extension, "CE-Report Production", "CE_REPORTCENTER");

                ContextMenuApplication.AddDefaultContextMenuExtension(extension);
                _menuExtension = extension;

                // AutoCAD uses an object context menu for selected TABLE
                // entities, so the default-menu extension alone is not enough.
                // Register a dedicated CE Tools table menu so right-clicking
                // directly on any AutoCAD/CE Table exposes Excel export.
                RXClass tableClass =
                    RXObject.GetClass(typeof(Table));
                if (tableClass != null)
                {
                    var tableExtension =
                        new ContextMenuExtension
                        {
                            Title = "CE Tools"
                        };
                    AddCommandItem(
                        tableExtension,
                        "Export This Table to Excel",
                        "CE_TABLEEXPORTEXCEL");
                    AddHoverItems(tableExtension);
                    ContextMenuApplication.AddObjectContextMenuExtension(
                        tableClass,
                        tableExtension);
                    _tableRxClass = tableClass;
                    _tableMenuExtension = tableExtension;
                }

                _attached = true;
            }
            catch (Autodesk.AutoCAD.Runtime.Exception ex)
            {
                WriteDiagnostic("Unable to attach the CE Tools right-click menu: " + ex.Message);
            }
        }

        internal static void Detach()
        {
            ContextMenuExtension extension = _menuExtension;
            ContextMenuExtension tableExtension = _tableMenuExtension;
            RXClass tableClass = _tableRxClass;
            _menuExtension = null;
            _tableMenuExtension = null;
            _tableRxClass = null;
            _attached = false;

            if (extension != null)
            {
                try
                {
                    ContextMenuApplication.RemoveDefaultContextMenuExtension(extension);
                }
                catch (Autodesk.AutoCAD.Runtime.Exception ex)
                {
                    WriteDiagnostic("Unable to remove the CE Tools right-click menu: " + ex.Message);
                }
                finally
                {
                    extension.Dispose();
                }
            }

            if (tableExtension != null &&
                tableClass != null)
            {
                try
                {
                    ContextMenuApplication.RemoveObjectContextMenuExtension(
                        tableClass,
                        tableExtension);
                }
                catch (Autodesk.AutoCAD.Runtime.Exception ex)
                {
                    WriteDiagnostic("Unable to remove the CE Tools table right-click menu: " + ex.Message);
                }
                finally
                {
                    tableExtension.Dispose();
                }
            }
        }

        private static void AddCommandItem(
            ContextMenuExtension extension,
            string title,
            string command)
        {
            if (extension == null ||
                string.IsNullOrWhiteSpace(title) ||
                string.IsNullOrWhiteSpace(command))
                return;

            var item = new MenuItem(title);
            item.Click += delegate
            {
                QueueCommand(command);
            };
            extension.MenuItems.Add(item);
        }

        private static void AddHoverItems(ContextMenuExtension extension)
        {
            AddCommandItem(extension, "CE-AutoCAD Object Hover", "CE_AUTOCADOBJECTHOVER");
            AddCommandItem(extension, "CE-Network Model Hover", "CE_NETWORKMODELHOVER");
            AddCommandItem(extension, "CE-Road Model Hover", "CE_ROADMODELHOVER");
        }

        private static void QueueCommand(string command)
        {
            Document document =
                AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null ||
                string.IsNullOrWhiteSpace(command))
                return;

            document.SendStringToExecute(
                command.Trim() + " ",
                true,
                false,
                false);
        }

        private static void OnDynamicRefreshClick(object sender, EventArgs e)
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null)
            {
                return;
            }

            // Queue the established explicit command. Do not invoke the universal
            // refresh manager directly from a UI event: the command owns the manual
            // refresh transaction/idle safety boundary (including PR #151).
            document.SendStringToExecute(DynamicRefreshAllCommand + " ", true, false, false);
        }

        private static void OnLocateSewerClick(object sender, EventArgs e)
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            // Preserve the right-click/preselection and let the command resolve
            // either the native pipe/structure or a CE incoming-pipe annotation.
            document.SendStringToExecute(
                "CE_SEWLOCATEPLAN ", true, false, false);
        }

        private static void WriteDiagnostic(string message)
        {
            try
            {
                Document document = AcApplication.DocumentManager.MdiActiveDocument;
                if (document != null)
                {
                    document.Editor.WriteMessage("\nCE Tools: " + message);
                }
            }
            catch
            {
                // UI registration must never prevent the rest of CE Tools loading.
            }
        }
    }
}

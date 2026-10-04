using System;
using Autodesk.AutoCAD.ApplicationServices;
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
                AddCommandItem(
                    extension,
                    "Sewer Supplementary",
                    "CE_SEWERFIELDSUPPLEMENTARY");
                AddCommandItem(
                    extension,
                    "Road Supplementary",
                    "CE_ROADFIELDSUPPLEMENTARY");
                AddCommandItem(
                    extension,
                    "CAD Supplementary",
                    "CE_CADSUPPLEMENTARY");
                AddCommandItem(
                    extension,
                    "Platform Supplementary",
                    "CE_PLATFORMFIELDSUPPLEMENTARY");
                AddCommandItem(
                    extension,
                    "Survey Supplementary",
                    "CE_SURVEYFIELDSUPPLEMENTARY");

                ContextMenuApplication.AddDefaultContextMenuExtension(extension);
                _menuExtension = extension;
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
            _menuExtension = null;
            _attached = false;

            if (extension == null)
            {
                return;
            }

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

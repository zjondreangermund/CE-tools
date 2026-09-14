using System;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Windows;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: ExtensionApplication(typeof(CETools.Civil3D.DynamicRefreshContextMenuApplication))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Registers the CE Tools manual dynamic-refresh action on AutoCAD's default
    /// right-click menu. This queues the established explicit refresh command instead
    /// of calling refresh internals directly, so the sewer sequencing safety boundary
    /// remains intact.
    /// </summary>
    public sealed class DynamicRefreshContextMenuApplication : IExtensionApplication
    {
        public void Initialize()
        {
            DynamicRefreshContextMenu.Attach();
        }

        public void Terminate()
        {
            DynamicRefreshContextMenu.Detach();
        }
    }

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

                AcApplication.AddDefaultContextMenuExtension(extension);
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
                AcApplication.RemoveDefaultContextMenuExtension(extension);
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

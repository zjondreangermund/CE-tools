using System;
using System.Linq;
using System.Windows.Input;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Windows;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: ExtensionApplication(typeof(CETools.Civil3D.PluginEntry))]
[assembly: CommandClass(typeof(CETools.Civil3D.BellmouthDensifierCommand))]

namespace CETools.Civil3D
{
    /// <summary>
    /// CE Tools application entry point and ribbon bootstrapper.
    /// </summary>
    public sealed class PluginEntry : IExtensionApplication
    {
        private static bool _ribbonCreated;

        public void Initialize()
        {
            AcApplication.Idle += OnApplicationIdle;
        }

        public void Terminate()
        {
            AcApplication.Idle -= OnApplicationIdle;
        }

        private static void OnApplicationIdle(object sender, EventArgs e)
        {
            if (_ribbonCreated)
            {
                AcApplication.Idle -= OnApplicationIdle;
                return;
            }

            try
            {
                _ribbonCreated = RibbonBuilder.EnsureCreated();
                if (_ribbonCreated)
                {
                    AcApplication.Idle -= OnApplicationIdle;
                }
            }
            catch
            {
                // Civil 3D can raise Idle before the ribbon is fully initialized.
                // Leave the handler attached so a later Idle event can retry.
            }
        }
    }

    internal static class RibbonBuilder
    {
        private const string TabId = "CE_TOOLS_RIBBON_TAB";
        private const string RoadsPanelId = "CE_TOOLS_ROADS_PANEL";
        private const string BellmouthButtonId = "CE_TOOLS_BMVERT_BUTTON";

        public static bool EnsureCreated()
        {
            RibbonControl ribbon = ComponentManager.Ribbon;
            if (ribbon == null)
            {
                return false;
            }

            RibbonTab tab = ribbon.Tabs.FirstOrDefault(item => item.Id == TabId);
            if (tab == null)
            {
                tab = new RibbonTab
                {
                    Id = TabId,
                    Title = "CE Tools",
                    Name = "CE Tools"
                };
                ribbon.Tabs.Add(tab);
            }

            bool panelExists = tab.Panels.Any(
                item => item.Source != null && item.Source.Id == RoadsPanelId);

            if (!panelExists)
            {
                var panelSource = new RibbonPanelSource
                {
                    Id = RoadsPanelId,
                    Title = "Roads"
                };

                var button = new RibbonButton
                {
                    Id = BellmouthButtonId,
                    Text = "Bellmouth\nDensifier",
                    ShowText = true,
                    ShowImage = false,
                    Size = RibbonItemSize.Large,
                    CommandParameter = "CE_BMVERT ",
                    CommandHandler = new RibbonCommandHandler(),
                    ToolTip = "Insert equal-chainage vertices into multiple line-and-arc polylines."
                };

                panelSource.Items.Add(button);
                tab.Panels.Add(new RibbonPanel { Source = panelSource });
            }

            return true;
        }
    }

    internal sealed class RibbonCommandHandler : ICommand
    {
#pragma warning disable 67
        public event EventHandler CanExecuteChanged;
#pragma warning restore 67

        public bool CanExecute(object parameter)
        {
            return AcApplication.DocumentManager.MdiActiveDocument != null;
        }

        public void Execute(object parameter)
        {
            var button = parameter as RibbonButton;
            string command = button?.CommandParameter as string;

            if (string.IsNullOrWhiteSpace(command))
            {
                command = "CE_BMVERT ";
            }

            AcApplication.DocumentManager.MdiActiveDocument?.SendStringToExecute(
                command,
                true,
                false,
                true);
        }
    }
}

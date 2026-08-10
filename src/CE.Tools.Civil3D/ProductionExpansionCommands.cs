using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.ProductionExpansionCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Single entry point for the August 2026 road-layout, platform-production and
    /// multi-boundary drawing expansion. The command centre discovers this command
    /// automatically even before a user rebuilds the staged Civil 3D 2023 ribbon.
    /// </summary>
    public sealed class ProductionExpansionCommands
    {
        [CommandMethod("CE_TOOLS", "CE_PRODUCTIONEXPANSION", CommandFlags.Modal)]
        public void Open()
        {
            PlatformDynamicRefreshManager.EnsureInitialized();
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            DisciplineWorkflowDialogs.SelectAndRun(
                document,
                "CE Tools - Road / Platform / Drawing Production",
                "Open the new preliminary road layout, linked platform production or multi-boundary drawing workflow.",
                new List<DisciplineWorkflowAction>
                {
                    new DisciplineWorkflowAction(
                        "Road layout production",
                        "CE_ROADLAYOUTTOOLS",
                        "Road-reserve centrelines, road edges, shoulders, bulk T/cross junctions, road names, dimensions and junction-only setting-out.",
                        "01 Roads"),
                    new DisciplineWorkflowAction(
                        "Civil 3D road production",
                        "CE_ROADPRODUCTION",
                        "Continue accepted preliminary road geometry into alignments, profiles, assemblies and corridors.",
                        "01 Roads"),
                    new DisciplineWorkflowAction(
                        "Platform production",
                        "CE_PLATFORMTOOLS",
                        "Feature lines, platform levels, stepped offsets, surface links, setting-out, quantities, layouts and sections.",
                        "02 Platforms"),
                    new DisciplineWorkflowAction(
                        "Multiple-boundary trim / extend",
                        "CE_BOUNDARYEDITTOOLS",
                        "Trim, trim-and-delete or extend drawing curves against multiple closed boundaries.",
                        "03 Drawing Tools"),
                    new DisciplineWorkflowAction(
                        "Refresh linked platform data",
                        "CE_PLATFORMREFRESH",
                        "Refresh platform surface links, stepped offsets, labels and tables.",
                        "04 Refresh"),
                    new DisciplineWorkflowAction(
                        "Refresh linked road layout",
                        "CE_ROADLAYOUTREFRESH",
                        "Refresh linked road-layout annotations and maintained outputs.",
                        "04 Refresh"),
                    new DisciplineWorkflowAction(
                        "Universal linked refresh",
                        "CE_DYNAMICREFRESHALL",
                        "Refresh the wider CE Tools linked-output ecosystem.",
                        "04 Refresh")
                });
        }
    }
}

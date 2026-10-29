using System.Collections.Generic;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
[assembly: CommandClass(typeof(CETools.Civil3D.NetworkLayoutToolsCommands))]
namespace CETools.Civil3D
{
    public sealed class NetworkLayoutToolsCommands
    {
        [CommandMethod("CE_TOOLS", "CE_NETWORKLAYOUTTOOLS", CommandFlags.Modal)]
        public void Show()
        {
            var doc = AcApplication.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            DisciplineWorkflowDialogs.SelectAndRun(doc, "CE Tools - Network Layout Checks / Labels",
                "Check source geometry and complete Sewer/Water plan presentation.", new List<DisciplineWorkflowAction> {
                    new DisciplineWorkflowAction("Check polyline vertices against structure centres", "CE_NETWORKVERTEXCHECK", "All or multiple selected sources; centre tolerance and maximum search distance.", "01 Check"),
                    new DisciplineWorkflowAction("Length above / diameter and class below", "CE_PIPELABELSIDES", "Arrange native pipe label components with plan-readable sides.", "02 Labels"),
                    new DisciplineWorkflowAction("Apply current viewport scales to all names", "CE_NAMESVIEWPORTSCALES", "Current model and actual layout viewport annotation scales only.", "02 Labels"),
                    new DisciplineWorkflowAction("Sort branch tree numerically", "CE_BRANCHSORT", "BRANCH-1, BRANCH-2, ... without renaming or recreating alignments. Expand Toolspace first.", "03 Order"),
                    new DisciplineWorkflowAction("Automatic rebuild ON / OFF", "CE_AUTOMATICREBUILD", "Independent controls for all corridors and all surfaces.", "04 Rebuild")
                });
        }
    }
}

using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.FinalAnnotationReviewCommands))]

namespace CETools.Civil3D
{
    public sealed class FinalAnnotationReviewCommands
    {
        [CommandMethod("CE_TOOLS", "CE_ANNOTATIONREVIEW", CommandFlags.Modal)]
        public void Open()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            DisciplineWorkflowDialogs.SelectAndRun(
                document,
                "CE Tools - Final Annotation Review",
                "One place for the final COGO, MText, MLeader, table, branch-label, setting-out and draw-order comments.",
                new List<DisciplineWorkflowAction>
                {
                    new DisciplineWorkflowAction("Smart overlap - COGO / MText / MLeader / dimensions", "CE_OVERLAPSMART", "Move only annotations that actually overlap; All/Selected; keep source/reference points fixed.", "01 Overlap"),
                    new DisciplineWorkflowAction("COGO overlap only", "CE_COGOOVERLAPFIX", "Resolve COGO labels only. Clear labels stay fixed, movement is bounded close to the survey point, and no far-away fallback is used.", "01 Overlap"),
                    new DisciplineWorkflowAction("MLeader text above leader", "CE_MLEADERTEXTABOVE", "Move only MLeader text above the leader tail; All/Selected; preserve arrow/reference vertices.", "02 Leaders"),
                    new DisciplineWorkflowAction("Restore original annotation positions", "CE_ANNOTATIONRESTORE", "Restore all or selected CE-moved annotation locations.", "03 Restore"),
                    new DisciplineWorkflowAction("Background masks", "CE_ANNOTATIONMASK", "Turn MText/MLeader background masks on or off; All/Selected.", "04 Presentation"),
                    new DisciplineWorkflowAction("Bring to front / send to back", "CE_ANNOTATIONDRAWORDER", "Change draw order for supported design annotations without editing geometry.", "04 Presentation"),
                    new DisciplineWorkflowAction("Branch labels on separate layer", "CE_BRANCHLABELLAYER", "Put detected Sewer/SW/Water BRANCH labels on CE-BRANCH-LABELS or another selected layer.", "04 Presentation"),
                    new DisciplineWorkflowAction("Repair table grid lines / spacing", "CE_TABLEPRESENTATIONFIX", "Restore visible grid lines, centred text and readable row/column spacing on selected/all tables.", "05 Tables"),
                    new DisciplineWorkflowAction("Click linked table cell -> source", "CE_TABLECELLZOOM", "Click a data row/cell and select/zoom its linked source object where the table carries source handles.", "05 Tables"),
                    new DisciplineWorkflowAction("Linked table -> all/source number", "CE_TABLESOURCEZOOM", "Select a linked table and zoom all discoverable sources or one source number.", "05 Tables"),
                    new DisciplineWorkflowAction("Grid setting-out", "CE_GRIDSETTINGOUT", "Create perimeter or full-grid setting-out points inside the selected boundary.", "06 Setting-Out"),
                    new DisciplineWorkflowAction("Repair all linked annotations", "CE_ANNOTATIONLINKREPAIR", "Re-anchor and clamp linked COGO/MText/MLeader/table/radial-dimension output close to true source points.", "07 Refresh"),
                    new DisciplineWorkflowAction("Refresh selected feature-line annotations/tables", "CE_FLANNOTREFRESHSELECTED", "Refresh only linked vertex tables and stepped-offset sets belonging to the selected source feature lines.", "07 Refresh"),
                    new DisciplineWorkflowAction("Refresh all feature-line annotations/tables", "CE_FLANNOTREFRESH", "Refresh linked feature-line/vertex annotation and table output across the drawing.", "07 Refresh"),
                    new DisciplineWorkflowAction("Universal linked refresh", "CE_COMMENTREFRESHALL", "Refresh the complete CE linked-output ecosystem after final annotation changes.", "07 Refresh")
                });
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;

namespace CETools.Civil3D
{
    internal sealed class August25BreakPlan
    {
        internal ObjectId SourceId;
        internal readonly List<double> Distances = new List<double>();
    }

    internal static class August25CadSupplementaryBreakEngine
    {
        internal static void Run(Document document)
        {
            if (document == null || document.Database == null) return;

            PromptSelectionResult selection = document.Editor.GetSelection(
                new PromptSelectionOptions
                {
                    MessageForAdding = "\nSelect all 2D/3D polylines to break at crossings and T-junctions: ",
                    AllowDuplicates = false,
                    RejectObjectsFromNonCurrentSpace = true
                },
                new SelectionFilter(new[]
                {
                    new TypedValue((int)DxfCode.Start, "LWPOLYLINE,POLYLINE")
                }));
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;

            List<ObjectId> ids = selection.Value.GetObjectIds()
                .Where(id => !id.IsNull && !id.IsErased)
                .Distinct()
                .ToList();
            if (ids.Count < 2)
            {
                document.Editor.WriteMessage("\nCE_PLBREAKJUNCTIONS: select at least two route polylines.");
                return;
            }

            Dictionary<ObjectId, August25BreakPlan> plans;
            int junctions;
            try
            {
                plans = August25CadSupplementaryBreakAnalysis.Analyse(document.Database, ids, out junctions);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_PLBREAKJUNCTIONS stopped during read-only junction analysis. Originals were not changed. {0}",
                    exception.Message);
                return;
            }

            List<August25BreakPlan> affected = plans.Values
                .Where(plan => plan.Distances.Count > 0)
                .ToList();
            if (junctions == 0 || affected.Count == 0)
            {
                document.Editor.WriteMessage(
                    "\nCE_PLBREAKJUNCTIONS: no internal crossings/T-junctions found. Shared route endpoints were retained.");
                return;
            }

            int expectedSegments = affected.Sum(plan => plan.Distances.Count + 1);
            if (!PopupTablePresenter.ShowReview(
                "CE Tools - Non-Destructive Route Break Preview",
                "All replacement spans for all affected selected polylines are created and verified first. Originals are erased together only after the complete batch is proven. Any failure keeps every original selected object.",
                new List<KeyValuePair<string, string>>
                {
                    Pair("Selected polylines", ids.Count),
                    Pair("Unique crossing/junction locations", junctions),
                    Pair("Polylines requiring a split", affected.Count),
                    Pair("Expected replacement segments", expectedSegments)
                },
                "Break and Verify Batch"))
                return;

            int replaced;
            int created;
            string failure;
            if (!August26CadSupplementaryBreakReplacement.TryReplaceBatch(
                    document.Database,
                    affected,
                    out replaced,
                    out created,
                    out failure))
            {
                August21DisplayRefresh.Flush(document);
                document.Editor.WriteMessage(
                    "\nCE_PLBREAKJUNCTIONS stopped safely. No original selected polylines were erased. {0}",
                    string.IsNullOrWhiteSpace(failure) ? "The replacement batch could not be verified." : failure);
                return;
            }

            August21DisplayRefresh.Flush(document);
            document.Editor.WriteMessage(
                "\nCE_PLBREAKJUNCTIONS complete. Sources replaced={0}; verified segments created={1}; plan junctions={2}. Batch replacement was all-or-none.",
                replaced,
                created,
                junctions);
        }

        private static KeyValuePair<string, string> Pair(string name, int value)
        {
            return new KeyValuePair<string, string>(
                name,
                value.ToString(CultureInfo.CurrentCulture));
        }
    }
}

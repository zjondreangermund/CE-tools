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
                "Each source is replaced only after every first/intermediate/last span is verified. A failed source transaction is rolled back and its original polyline remains untouched.",
                new List<KeyValuePair<string, string>>
                {
                    Pair("Selected polylines", ids.Count),
                    Pair("Unique crossing/junction locations", junctions),
                    Pair("Polylines requiring a split", affected.Count),
                    Pair("Expected replacement segments", expectedSegments)
                },
                "Break and Verify"))
                return;

            int replaced = 0;
            int created = 0;
            int preserved = 0;
            foreach (August25BreakPlan plan in affected)
            {
                int createdForSource;
                if (August25CadSupplementaryBreakReplacement.TryReplaceOneAtomic(
                        document.Database,
                        plan,
                        out createdForSource))
                {
                    replaced++;
                    created += createdForSource;
                }
                else
                {
                    preserved++;
                }
            }

            August21DisplayRefresh.Flush(document);
            document.Editor.WriteMessage(
                "\nCE_PLBREAKJUNCTIONS complete. Sources replaced={0}; verified segments created={1}; sources rolled back/preserved={2}; plan junctions={3}.",
                replaced,
                created,
                preserved,
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

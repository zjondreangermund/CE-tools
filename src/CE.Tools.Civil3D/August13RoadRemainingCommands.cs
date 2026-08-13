using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.August13RoadRemainingCommands))]

namespace CETools.Civil3D
{
    public sealed class August13RoadRemainingCommands
    {
        [CommandMethod("CE_TOOLS", "CE_ROADPROFILEREPORT", CommandFlags.Modal | CommandFlags.Redraw)]
        public void RoadProfileReport()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            document.SendStringToExecute("CE_PROFILEVIEWBATCHINFO ", true, false, true);
        }

        [CommandMethod("CE_TOOLS", "CE_CORSPLIT", CommandFlags.Modal | CommandFlags.Redraw | CommandFlags.UsePickSet)]
        public void SplitCorridorRegions()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            List<ObjectId> corridorIds = SelectCorridors(document);
            if (corridorIds.Count == 0)
            {
                document.Editor.WriteMessage("\nCE_CORSPLIT cancelled. No Civil 3D corridors selected.");
                return;
            }

            var model = new ProductionSettingsDialogModel(
                "CE Tools - Split Corridor Regions",
                "Split every selected corridor baseline into regular production regions. Existing region boundaries are preserved and duplicate split stations are ignored.");
            model.AddPositiveDouble("Interval", "01 Stations", "Split interval", 750.0,
                "Regular distance between corridor-region split stations.");
            model.AddDouble("Start", "01 Stations", "First split station", 750.0,
                "First desired station. For each baseline the value is clamped to its valid station range.");
            model.AddDouble("End", "01 Stations", "Last split station (0 = baseline end)", 0.0,
                "Use 0 to continue to each baseline end, or specify a last station.");
            model.AddChoice("Rebuild", "02 Corridor", "Rebuild after splitting", "Yes",
                "Rebuild each changed corridor after all baseline regions have been split.",
                new[] { "Yes", "No" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            double interval = Math.Max(0.001, model.Double("Interval", 750.0));
            double requestedStart = model.Double("Start", 750.0);
            double requestedEnd = model.Double("End", 0.0);
            bool rebuild = string.Equals(model.Text("Rebuild"), "Yes", StringComparison.OrdinalIgnoreCase);

            int corridorsChanged = 0;
            int baselinesChanged = 0;
            int splits = 0;
            var rows = new List<IList<string>>();

            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId corridorId in corridorIds)
                {
                    Corridor corridor = null;
                    try { corridor = transaction.GetObject(corridorId, OpenMode.ForWrite, false) as Corridor; }
                    catch { }
                    if (corridor == null) continue;

                    int corridorSplits = 0;
                    int corridorBaselines = 0;
                    foreach (Baseline baseline in corridor.Baselines)
                    {
                        if (baseline == null) continue;
                        double baselineStart = baseline.StartStation;
                        double baselineEnd = baseline.EndStation;
                        if (baselineEnd - baselineStart <= 0.02) continue;

                        double start = Math.Max(baselineStart, requestedStart);
                        if (requestedStart <= baselineStart + 0.011)
                            start = baselineStart + interval;
                        double end = requestedEnd <= 0.0 ? baselineEnd : Math.Min(baselineEnd, requestedEnd);
                        if (end <= baselineStart + 0.011 || start >= end - 0.011) continue;

                        int baselineSplits = 0;
                        for (double station = start; station < end - 0.011; station += interval)
                            baselineSplits += SplitAtStation(baseline, station);

                        if (baselineSplits > 0)
                        {
                            baseline.NeedsProcessing = true;
                            corridorBaselines++;
                            corridorSplits += baselineSplits;
                        }
                    }

                    if (corridorSplits > 0)
                    {
                        if (rebuild)
                        {
                            try { corridor.Rebuild(); }
                            catch { }
                        }
                        corridorsChanged++;
                        baselinesChanged += corridorBaselines;
                        splits += corridorSplits;
                        rows.Add(new List<string>
                        {
                            corridor.Name,
                            corridorBaselines.ToString(CultureInfo.InvariantCulture),
                            corridorSplits.ToString(CultureInfo.InvariantCulture),
                            rebuild ? "Rebuilt" : "Split only"
                        });
                    }
                }
                transaction.Commit();
            }

            document.Editor.Regen();
            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - Corridor Region Split",
                "Selected corridor baselines were split at the requested regular station interval.",
                new[] { "CORRIDOR", "BASELINES", "REGION SPLITS", "STATUS" },
                rows,
                "CE ROAD CORRIDOR REGION SPLIT REGISTER");
            document.Editor.WriteMessage(
                "\nCE_CORSPLIT complete. Corridors={0}; baselines={1}; region splits={2}.",
                corridorsChanged, baselinesChanged, splits);
        }

        private static List<ObjectId> SelectCorridors(Document document)
        {
            PromptSelectionResult selected = document.Editor.SelectImplied();
            if (selected.Status != PromptStatus.OK || selected.Value == null || selected.Value.Count == 0)
            {
                selected = document.Editor.GetSelection(new PromptSelectionOptions
                {
                    MessageForAdding = "\nSelect Civil 3D corridors to split: ",
                    AllowDuplicates = false,
                    RejectObjectsFromNonCurrentSpace = true
                });
            }
            if (selected.Status != PromptStatus.OK || selected.Value == null)
                return new List<ObjectId>();

            var result = new List<ObjectId>();
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in selected.Value.GetObjectIds())
                {
                    Corridor corridor = null;
                    try { corridor = transaction.GetObject(id, OpenMode.ForRead, false) as Corridor; }
                    catch { }
                    if (corridor != null && !result.Contains(id)) result.Add(id);
                }
            }
            return result;
        }

        private static int SplitAtStation(Baseline baseline, double station)
        {
            if (baseline == null) return 0;
            for (int index = 0; index < baseline.BaselineRegions.Count; index++)
            {
                BaselineRegion region = baseline.BaselineRegions[index];
                if (region == null) continue;
                if (station <= region.StartStation + 0.011 || station >= region.EndStation - 0.011)
                    continue;
                try
                {
                    region.Split(station);
                    return 1;
                }
                catch { return 0; }
            }
            return 0;
        }
    }
}

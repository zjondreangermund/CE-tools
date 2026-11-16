using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;

[assembly: CommandClass(typeof(CETools.Civil3D.CorridorNameSyncCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Renames one or more corridors to the name of the unique alignment
    /// referenced by their Civil 3D baselines. Shared/multi-alignment corridors
    /// are deliberately skipped because they have no single authoritative road name.
    /// </summary>
    public sealed class CorridorNameSyncCommands
    {
        private sealed class RenamePlan
        {
            public ObjectId CorridorId;
            public string CurrentName;
            public string TargetName;
            public string Reason;
        }

        [CommandMethod("CE_TOOLS", "CE_CORRIDORNAMESYNC",
            CommandFlags.Modal | CommandFlags.Redraw)]
        public void SyncCorridorNames()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            Editor editor = document.Editor;
            PromptKeywordOptions modeOptions =
                new PromptKeywordOptions("\nSynchronise corridor names [All/Selected] <Selected>: ");
            modeOptions.AllowNone = true;
            modeOptions.Keywords.Add("All");
            modeOptions.Keywords.Add("Selected");
            PromptResult modeResult = editor.GetKeywords(modeOptions);
            if (modeResult.Status == PromptStatus.Cancel) return;
            bool all = modeResult.Status == PromptStatus.OK &&
                string.Equals(modeResult.StringResult, "All", StringComparison.OrdinalIgnoreCase);

            var selectedIds = new HashSet<ObjectId>();
            if (!all)
            {
                PromptSelectionResult selection = editor.GetSelection(
                    new PromptSelectionOptions { MessageForAdding = "\nSelect corridors to synchronise: " });
                if (selection.Status != PromptStatus.OK || selection.Value == null) return;
                foreach (SelectedObject item in selection.Value)
                    if (item != null && !item.ObjectId.IsNull) selectedIds.Add(item.ObjectId);
            }

            var plans = new List<RenamePlan>();
            var allCorridorNames = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);
            int ignoredNonCorridors = 0;

            using (Transaction tr = document.Database.TransactionManager.StartTransaction())
            {
                CivilDocument civil = CivilDocument.GetCivilDocument(document.Database);
                var corridorIds = new HashSet<ObjectId>();
                foreach (ObjectId id in civil.CorridorCollection)
                {
                    if (!id.IsNull) corridorIds.Add(id);
                    DBObject obj = null;
                    try { obj = tr.GetObject(id, OpenMode.ForRead, false); } catch { }
                    Corridor existing = obj as Corridor;
                    if (existing != null && !string.IsNullOrWhiteSpace(existing.Name))
                        allCorridorNames[existing.Name] = id;
                }

                IEnumerable<ObjectId> candidates = all
                    ? corridorIds
                    : selectedIds;

                foreach (ObjectId id in candidates)
                {
                    Corridor corridor = null;
                    try { corridor = tr.GetObject(id, OpenMode.ForRead, false) as Corridor; }
                    catch { }
                    if (corridor == null)
                    {
                        if (!all) ignoredNonCorridors++;
                        continue;
                    }

                    var alignmentIds = new HashSet<ObjectId>();
                    try
                    {
                        foreach (Baseline baseline in corridor.Baselines)
                        {
                            if (baseline == null) continue;
                            ObjectId alignmentId = baseline.AlignmentId;
                            if (!alignmentId.IsNull) alignmentIds.Add(alignmentId);
                        }
                    }
                    catch { }

                    if (alignmentIds.Count == 0)
                    {
                        plans.Add(new RenamePlan {
                            CorridorId = id, CurrentName = corridor.Name,
                            TargetName = null, Reason = "No alignment-linked baseline found"
                        });
                        continue;
                    }
                    if (alignmentIds.Count != 1)
                    {
                        plans.Add(new RenamePlan {
                            CorridorId = id, CurrentName = corridor.Name,
                            TargetName = null, Reason = "Multiple distinct alignment baselines; ambiguous owner"
                        });
                        continue;
                    }

                    ObjectId ownerId = alignmentIds.First();
                    CivilAlignment alignment = null;
                    try { alignment = tr.GetObject(ownerId, OpenMode.ForRead, false) as CivilAlignment; }
                    catch { }
                    if (alignment == null || string.IsNullOrWhiteSpace(alignment.Name))
                    {
                        plans.Add(new RenamePlan {
                            CorridorId = id, CurrentName = corridor.Name,
                            TargetName = null, Reason = "Associated alignment could not be resolved"
                        });
                        continue;
                    }

                    plans.Add(new RenamePlan {
                        CorridorId = id,
                        CurrentName = corridor.Name ?? string.Empty,
                        TargetName = alignment.Name,
                        Reason = null
                    });
                }
            }

            var renameCandidates = plans.Where(p => p.TargetName != null &&
                !string.Equals(p.CurrentName, p.TargetName, StringComparison.Ordinal)).ToList();

            // Refuse collisions with corridors outside the proposed rename target.
            var targetGroups = renameCandidates.GroupBy(p => p.TargetName,
                StringComparer.OrdinalIgnoreCase);
            var duplicateTargets = new HashSet<string>(
                targetGroups.Where(g => g.Count() > 1).Select(g => g.Key),
                StringComparer.OrdinalIgnoreCase);

            foreach (RenamePlan plan in renameCandidates)
            {
                if (duplicateTargets.Contains(plan.TargetName))
                {
                    plan.Reason = "More than one selected corridor resolves to this alignment name";
                    continue;
                }
                ObjectId occupiedBy;
                if (allCorridorNames.TryGetValue(plan.TargetName, out occupiedBy) &&
                    occupiedBy != plan.CorridorId)
                {
                    plan.Reason = "Target name is already used by another corridor";
                }
            }

            var ready = renameCandidates.Where(p => p.Reason == null).ToList();
            int skipped = plans.Count(p => p.Reason != null);
            int alreadyCorrect = plans.Count(p => p.TargetName != null &&
                string.Equals(p.CurrentName, p.TargetName, StringComparison.Ordinal));
            if (ready.Count == 0)
            {
                editor.WriteMessage("\nCE_CORRIDORNAMESYNC: no safe corridor renames found. Already correct={0}, skipped={1}, non-corridor selections={2}.",
                    alreadyCorrect, skipped, ignoredNonCorridors);
                foreach (RenamePlan p in plans.Where(x => x.Reason != null).Take(30))
                    editor.WriteMessage("\n  {0}: {1}", p.CurrentName, p.Reason);
                return;
            }

            editor.WriteMessage("\nCE_CORRIDORNAMESYNC preview:");
            foreach (RenamePlan p in ready.Take(50))
                editor.WriteMessage("\n  {0}  ->  {1}", p.CurrentName, p.TargetName);
            if (ready.Count > 50)
                editor.WriteMessage("\n  ... and {0} more.", ready.Count - 50);
            editor.WriteMessage("\nAlready correct={0}; skipped/ambiguous={1}; non-corridor selections={2}.",
                alreadyCorrect, skipped, ignoredNonCorridors);

            PromptKeywordOptions confirm = new PromptKeywordOptions("\nApply these corridor name changes? [Yes/No] <No>: ");
            confirm.AllowNone = true;
            confirm.Keywords.Add("Yes");
            confirm.Keywords.Add("No");
            PromptResult answer = editor.GetKeywords(confirm);
            if (answer.Status != PromptStatus.OK ||
                !string.Equals(answer.StringResult, "Yes", StringComparison.OrdinalIgnoreCase))
            {
                editor.WriteMessage("\nCorridor names were not changed.");
                return;
            }

            int renamed = 0;
            int failed = 0;
            using (Transaction tr = document.Database.TransactionManager.StartTransaction())
            {
                foreach (RenamePlan plan in ready)
                {
                    try
                    {
                        Corridor corridor = tr.GetObject(plan.CorridorId, OpenMode.ForWrite, false) as Corridor;
                        if (corridor == null) { failed++; continue; }

                        // Use the Civil 3D Corridor.Name property through reflection
                        // to avoid binding to version-specific wrapper metadata.
                        PropertyInfo nameProperty = corridor.GetType().GetProperty("Name",
                            BindingFlags.Instance | BindingFlags.Public);
                        if (nameProperty == null || !nameProperty.CanWrite)
                        {
                            failed++;
                            continue;
                        }
                        nameProperty.SetValue(corridor, plan.TargetName, null);
                        renamed++;
                    }
                    catch
                    {
                        failed++;
                    }
                }
                tr.Commit();
            }

            editor.WriteMessage("\nCE_CORRIDORNAMESYNC completed: renamed={0}, failed={1}, already correct={2}, skipped/ambiguous={3}.",
                renamed, failed, alreadyCorrect, skipped);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.AlignmentSortPopupCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Road alignment spatial sorting and safe optional station reset.
    /// Alignment.Reverse() destroys equations/design speeds and can harm
    /// dependent profiles; this utility deliberately refuses that operation.
    /// </summary>
    public sealed class AlignmentSortPopupCommands
    {
        private sealed class Item
        {
            public ObjectId Id;
            public string Name;
            public Point2d Start;
            public Point2d End;
            public double Station;
            public int Profiles;
            public bool Reference;
        }

        [CommandMethod("CE_TOOLS", "CE_ALIGNMENTSORT",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void SortRoadAlignments()
        {
            Document doc = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civil = CivilApplication.ActiveDocument;
            if (doc == null || civil == null) return;
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Alignment Sort and Zero Chainage",
                "Sort road alignments spatially. Optional zero station resets only safe, editable alignments. Geometry reversal is never automatic.");
            model.AddChoice("Scope", "01 Selection", "Road alignments", "All",
                "Use all model-space road alignments or select multiple alignments in the drawing.",
                new[] { "All", "Selected" });
            model.AddChoice("Sort", "02 Sequence", "Sort direction", "TopLeft",
                "Choose the starting corner for the alignment list.",
                new[] { "TopLeft", "BottomLeft", "TopRight", "BottomRight" });
            model.AddChoice("Reading", "02 Sequence", "Preferred road orientation", "Left to right / Top to bottom",
                "Horizontal roads run left to right; vertical roads run top to bottom. Reversal is only reported, not performed on existing geometry.",
                new[] { "Left to right / Top to bottom" });
            model.AddChoice("Reverse", "03 Chainage", "Reverse opposite-direction alignments",
                "Preview only (no changes)",
                "Native Civil 3D reversal may reprocess or damage profiles, corridors and station-dependent objects. Backup drawing before use.",
                new[] { "Preview only (no changes)", "Reverse safe alignments without profiles", "Reverse including profile-dependent roads (high risk)" });
            model.AddChoice("Zero", "03 Chainage", "Starting chainage", "Keep existing",
                "Set station 0+000 at the CURRENT geometric start. Existing profiles and station equations are excluded for safety.",
                new[] { "Keep existing", "Reset safe alignments to 0+000" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            HashSet<ObjectId> selected = null;
            if (string.Equals(model.Text("Scope"), "Selected", StringComparison.OrdinalIgnoreCase))
            {
                PromptSelectionResult result = doc.Editor.SelectImplied();
                if (result.Status != PromptStatus.OK || result.Value == null || result.Value.Count == 0)
                    result = doc.Editor.GetSelection(new PromptSelectionOptions {
                        MessageForAdding = "\nSelect multiple road alignments: ",
                        AllowDuplicates = false
                    });
                doc.Editor.SetImpliedSelection(new ObjectId[0]);
                if (result.Status != PromptStatus.OK || result.Value == null) return;
                selected = new HashSet<ObjectId>(result.Value.GetObjectIds());
            }

            var items = new List<Item>();
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                BlockTable bt = tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead) as BlockTable;
                BlockTableRecord space = bt == null ? null :
                    tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead) as BlockTableRecord;
                if (space == null) return;
                foreach (ObjectId id in space)
                {
                    if (selected != null && !selected.Contains(id)) continue;
                    Alignment a = tr.GetObject(id, OpenMode.ForRead, false) as Alignment;
                    if (a == null || a.AlignmentType != AlignmentType.Centerline) continue;
                    double sx = 0, sy = 0, ex = 0, ey = 0;
                    try {
                        a.PointLocation(a.StartingStation, 0.0, ref sx, ref sy);
                        a.PointLocation(a.EndingStation, 0.0, ref ex, ref ey);
                    } catch { continue; }
                    items.Add(new Item {
                        Id = id, Name = a.Name,
                        Start = new Point2d(sx, sy), End = new Point2d(ex, ey),
                        Station = a.StartingStation, Profiles = a.GetProfileIds().Count,
                        Reference = a.IsReferenceObject
                    });
                }
            }
            if (items.Count == 0) {
                doc.Editor.WriteMessage("\nCE_ALIGNMENTSORT: No road centreline alignments found.");
                return;
            }

            bool fromTop = model.Text("Sort").StartsWith("Top", StringComparison.OrdinalIgnoreCase);
            bool fromLeft = model.Text("Sort").EndsWith("Left", StringComparison.OrdinalIgnoreCase);
            items = (fromTop
                    ? items.OrderByDescending(x => (x.Start.Y + x.End.Y) / 2.0)
                    : items.OrderBy(x => (x.Start.Y + x.End.Y) / 2.0))
                .ThenBy(x => fromLeft ? (x.Start.X + x.End.X) / 2.0 : -(x.Start.X + x.End.X) / 2.0)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();

            doc.Editor.WriteMessage("\nCE_ALIGNMENTSORT preview: {0} road alignments (sort: {1}).",
                items.Count, model.Text("Sort"));
            int reverseNeeded = 0;
            foreach (Item item in items)
            {
                bool horizontal = Math.Abs(item.End.X - item.Start.X) >= Math.Abs(item.End.Y - item.Start.Y);
                bool reverse = horizontal ? item.End.X < item.Start.X : item.End.Y > item.Start.Y;
                if (reverse) reverseNeeded++;
                doc.Editor.WriteMessage("\n  {0}. {1}: current start {2:0.###}, profiles={3}{4}",
                    items.IndexOf(item) + 1, item.Name, item.Station, item.Profiles,
                    reverse ? " [opposite preferred direction; unchanged]" : "");
            }
            if (reverseNeeded > 0)
                doc.Editor.WriteMessage("\n{0} alignments point opposite the preferred direction; no geometry was reversed.", reverseNeeded);
            bool reverseSafe = string.Equals(model.Text("Reverse"),
                "Reverse safe alignments without profiles", StringComparison.OrdinalIgnoreCase);
            bool reverseWithDependencies = string.Equals(model.Text("Reverse"),
                "Reverse including profile-dependent roads (high risk)", StringComparison.OrdinalIgnoreCase);
            bool resetZero = string.Equals(model.Text("Zero"),
                "Reset safe alignments to 0+000", StringComparison.OrdinalIgnoreCase);
            if (!reverseSafe && !reverseWithDependencies && !resetZero)
            {
                RoadNameAuditCommands.AuditActive(false);
                return;
            }
            if (reverseWithDependencies)
            {
                doc.Editor.WriteMessage(
                    "\\nWARNING: Native alignment reversal reprocesses profiles, corridor dependencies, sections and labels. " +
                    "It can remove design speeds and station equations. Save a complete backup DWG first.");
                PromptStringResult typed = doc.Editor.GetString(
                    new PromptStringOptions("\\nType REVERSE to allow changing roads with dependent profiles: ") { AllowSpaces = false });
                if (typed.Status != PromptStatus.OK || !string.Equals(typed.StringResult, "REVERSE", StringComparison.Ordinal))
                    return;
            }

            var confirmation = new PromptKeywordOptions(
                "\nApply requested alignment direction/chainage changes to eligible roads? [Yes/No] <No>: ");
            confirmation.Keywords.Add("Yes"); confirmation.Keywords.Add("No");
            confirmation.Keywords.Default = "No"; confirmation.AllowNone = true;
            PromptResult answer = doc.Editor.GetKeywords(confirmation);
            if (answer.Status != PromptStatus.OK ||
                !string.Equals(answer.StringResult, "Yes", StringComparison.OrdinalIgnoreCase)) return;

            int changed = 0, skipped = 0;
            using (doc.LockDocument())
            {
                foreach (Item item in items)
                {
                    // No invasive alteration on referenced alignments, profiles,
                    // or previously equation-based station systems.
                    if (item.Reference || (item.Profiles != 0 && !reverseWithDependencies)) { skipped++; continue; }
                    try
                    {
                        using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
                        {
                            Alignment a = tr.GetObject(item.Id, OpenMode.ForWrite, false) as Alignment;
                            if (a == null || a.IsReferenceObject ||
                                (a.GetProfileIds().Count != 0 && !reverseWithDependencies) ||
                                a.StationEquations.Count != 0) { skipped++; continue; }
                            bool horizontal = Math.Abs(item.End.X - item.Start.X) >=
                                Math.Abs(item.End.Y - item.Start.Y);
                            bool wrongDirection = horizontal
                                ? item.End.X < item.Start.X : item.End.Y > item.Start.Y;
                            if ((reverseSafe || reverseWithDependencies) && wrongDirection)
                            {
                                // Autodesk warns that Reverse may affect dependents.
                                // Only reverse an editable alignment with no profiles
                                // and no station equations.
                                a.Reverse();
                                changed++;
                            }
                            if (resetZero && Math.Abs(a.StartingStation) > 0.000001)
                            {
                                // StartingStation is read-only. Move the native
                                // reference station to the geometric start.
                                a.ReferencePoint = (reverseSafe || reverseWithDependencies) && wrongDirection ? item.End : item.Start;
                                a.ReferencePointStation = 0.0;
                                if (Math.Abs(a.StartingStation) > 0.001)
                                    throw new InvalidOperationException("Station reset validation failed");
                                changed++;
                            }
                            tr.Commit();
                        }
                    }
                    catch (System.Exception ex)
                    {
                        skipped++;
                        doc.Editor.WriteMessage("\n  Skipped {0}: {1}", item.Name, ex.Message);
                    }
                }
            }
            doc.Editor.WriteMessage("\nCE_ALIGNMENTSORT: zero-station updates={0}; skipped/protected={1}.",
                changed, skipped);
            if (changed > 0) doc.Editor.Regen();
            RoadNameAuditCommands.AuditActive(false);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivilNetwork = Autodesk.Civil.DatabaseServices.Network;
using CivilPipe = Autodesk.Civil.DatabaseServices.Pipe;
using CivilStructure = Autodesk.Civil.DatabaseServices.Structure;

[assembly: CommandClass(typeof(CETools.Civil3D.WaterNetworkPartNumberingCommands))]

namespace CETools.Civil3D
{
    public sealed class WaterNetworkPartNumberingCommands
    {
        private sealed class PartRoute
        {
            internal ObjectId PartId;
            internal string Route;
            internal double Station;
            internal bool Pipe;
            internal string NewName;
        }

        [CommandMethod("CE_TOOLS", "CE_WATERPARTNUMBER", CommandFlags.Modal | CommandFlags.Redraw | CommandFlags.UsePickSet)]
        public void NumberWaterParts()
        {
            Document doc = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civil = CivilApplication.ActiveDocument;
            if (doc == null || civil == null) return;
            var options = new ProductionSettingsDialogModel(
                "CE Tools - Number Water Pipes and Structures by Branch",
                "Assign P1.1, P1.2 and N1.1, N1.2 using Water branch alignments. " +
                "Shared structures receive one number on their closest branch. " +
                "No pipe geometry, slope, invert or rule will be changed.");
            options.AddChoice("Scope", "01 Selection", "Water network parts",
                "All Water networks", "Select Water pipe networks or individual ordinary pipes/structures.",
                new[] { "All Water networks", "Selected Water network parts" });
            options.AddPositiveDouble("Tolerance", "02 Matching", "Maximum alignment offset (drawing units)", 5.0,
                "Parts farther from any Water branch alignment are skipped to prevent incorrect numbers.");
            options.AddChoice("Action", "03 Safety", "Numbering action", "Preview only",
                "Preview all proposed name changes before choosing the rename action.",
                new[] { "Preview only", "Apply names to Water pipe and structure objects" });
            if (!DisciplineWorkflowDialogs.EditSettings(options)) return;

            HashSet<ObjectId> chosen = null;
            if (options.Text("Scope") == "Selected Water network parts")
            {
                PromptSelectionResult selection = doc.Editor.GetSelection(new PromptSelectionOptions
                {
                    MessageForAdding = "\nSelect Water networks, pipes and structures: ",
                    AllowDuplicates = false, RejectObjectsFromNonCurrentSpace = true
                });
                if (selection.Status != PromptStatus.OK || selection.Value == null) return;
                chosen = new HashSet<ObjectId>(selection.Value.GetObjectIds());
            }

            var assignments = new List<PartRoute>();
            int unmatched = 0;
            double maxOffset = options.Double("Tolerance", 5.0);
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                var alignments = new List<CivilAlignment>();
                foreach (ObjectId id in civil.GetAlignmentIds())
                {
                    CivilAlignment a = tr.GetObject(id, OpenMode.ForRead, false) as CivilAlignment;
                    if (a != null && !a.IsReferenceObject &&
                        (a.Name.StartsWith("BRANCH-", StringComparison.OrdinalIgnoreCase) ||
                         a.Name.StartsWith("W-", StringComparison.OrdinalIgnoreCase) ||
                         a.Name.IndexOf("Water", StringComparison.OrdinalIgnoreCase) >= 0))
                        alignments.Add(a);
                }
                if (alignments.Count == 0)
                {
                    MessageBox.Show("No Water route alignments exist. Run CE_WATERSEQ and CE_WATERALIGN first.",
                        "CE Tools - Water Numbering"); return;
                }
                foreach (ObjectId networkId in civil.GetPipeNetworkIds())
                {
                    CivilNetwork network = tr.GetObject(networkId, OpenMode.ForRead, false) as CivilNetwork;
                    if (network == null || network.IsReferenceObject ||
                        !(network.Name.IndexOf("Water", StringComparison.OrdinalIgnoreCase) >= 0 ||
                          network.Name.StartsWith("W-", StringComparison.OrdinalIgnoreCase))) continue;
                    foreach (ObjectId pipeId in network.GetPipeIds())
                    {
                        if (chosen != null && !chosen.Contains(pipeId) && !chosen.Contains(networkId)) continue;
                        CivilPipe pipe = tr.GetObject(pipeId, OpenMode.ForRead, false) as CivilPipe;
                        if (pipe == null) continue;
                        var point = pipe.StartPoint + (pipe.EndPoint - pipe.StartPoint) * 0.5;
                        PartRoute route = Match(alignments, point.X, point.Y, maxOffset, pipeId, true);
                        if (route != null) assignments.Add(route); else unmatched++;
                    }
                    foreach (ObjectId structureId in network.GetStructureIds())
                    {
                        if (chosen != null && !chosen.Contains(structureId) && !chosen.Contains(networkId)) continue;
                        CivilStructure structure = tr.GetObject(structureId, OpenMode.ForRead, false) as CivilStructure;
                        if (structure == null) continue;
                        PartRoute route = Match(alignments, structure.Position.X, structure.Position.Y,
                            maxOffset, structureId, false);
                        if (route != null) assignments.Add(route); else unmatched++;
                    }
                }
            }
            foreach (var group in assignments.GroupBy(a => a.Route, StringComparer.OrdinalIgnoreCase))
            {
                int pipeIndex = 0, structureIndex = 0;
                foreach (PartRoute part in group.OrderBy(a => a.Station)
                    .ThenBy(a => a.PartId.Handle.ToString(), StringComparer.OrdinalIgnoreCase))
                {
                    int n = part.Pipe ? ++pipeIndex : ++structureIndex;
                    part.NewName = (part.Pipe ? "P" : "N") + group.Key + "." + n.ToString(CultureInfo.InvariantCulture);
                }
            }
            if (assignments.Count == 0)\n            {\n                MessageBox.Show("None of the " + unmatched + " Water parts matched a numbered Water branch alignment.\n" +\n                    "Check that CE_WATERALIGN generated Water alignments on the same pipe routes, " +\n                    "or increase the maximum alignment offset in the popup. No part names were changed.",\n                    "CE Tools - Water Numbering");\n                return;\n            }\n            if (options.Text("Action") == "Preview only")
            {
                doc.Editor.WriteMessage("\nWater numbering preview: matched={0}; unmatched={1}.",
                    assignments.Count, unmatched);
                foreach (PartRoute part in assignments.Take(80))
                    doc.Editor.WriteMessage("\n  {0} -> {1}", part.PartId.Handle, part.NewName);
                MessageBox.Show("Numbering preview written to command history.\nMatched parts: " +
                    assignments.Count + "\nUnmatched parts: " + unmatched +
                    "\nChoose Apply in this command to rename the objects.",
                    "CE Tools - Water Numbering");
                return;
            }
            int renamed = 0, failed = 0;
            using (doc.LockDocument())
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                foreach (PartRoute part in assignments)
                {
                    try
                    {
                        var obj = tr.GetObject(part.PartId, OpenMode.ForWrite, false);
                        var pipe = obj as CivilPipe;
                        var structure = obj as CivilStructure;
                        if (pipe != null) pipe.Name = part.NewName;
                        else if (structure != null) structure.Name = part.NewName;
                        else { failed++; continue; }
                        renamed++;
                    }
                    catch (System.Exception ex)
                    {
                        failed++;
                        doc.Editor.WriteMessage("\nWater part numbering failed for {0}: {1}",
                            part.PartId.Handle, ex.Message);
                    }
                }
                tr.Commit();
            }
            doc.Editor.Regen();
            MessageBox.Show("Water pipes/structures renamed: " + renamed +
                "\nUnmatched parts: " + unmatched + "\nFailed: " + failed +
                "\nThe selected Civil 3D label style must display Part Name to show the new numbers.",
                "CE Tools - Water Numbering");
        }

        private static PartRoute Match(IEnumerable<CivilAlignment> alignments,
            double x, double y, double maxOffset, ObjectId partId, bool pipe)
        {
            CivilAlignment best = null;
            double bestStation = 0, bestOffset = maxOffset;
            foreach (CivilAlignment alignment in alignments)
            {
                try
                {
                    double station = 0, offset = 0;
                    alignment.StationOffset(x, y, ref station, ref offset);
                    double distance = Math.Abs(offset);
                    if (station < alignment.StartingStation - 0.01 ||
                        station > alignment.EndingStation + 0.01 || distance > bestOffset)
                        continue;
                    best = alignment; bestStation = station; bestOffset = distance;
                }
                catch (System.Exception) { }
            }
            if (best == null) return null;
            string name = best.Name ?? string.Empty;
            string description = best.Description ?? string.Empty;
            // Alignments created before BRANCH-N naming retain their original
            // names. Their CE route identity may be stored in Description.
            int position = description.IndexOf("route=", StringComparison.OrdinalIgnoreCase);
            if (position >= 0)
            {
                string tagged = description.Substring(position + 6).Split('|')[0].Trim();
                if (tagged.Length > 0) name = tagged;
            }
            int branchNumber = 0;
            if (name.StartsWith("BRANCH-", StringComparison.OrdinalIgnoreCase))
                int.TryParse(name.Substring(7), out branchNumber);
            else if (name.StartsWith("W-B", StringComparison.OrdinalIgnoreCase))
                int.TryParse(name.Substring(3), out branchNumber);
            else if (name.StartsWith("W-MAIN", StringComparison.OrdinalIgnoreCase) ||
                     name.Equals("MAIN", StringComparison.OrdinalIgnoreCase))
                branchNumber = 0;
            else
                return null; // Never silently number against an unknown route.
            return new PartRoute {
                PartId = partId, Route = branchNumber.ToString(CultureInfo.InvariantCulture),
                Station = bestStation, Pipe = pipe
            };
        }
    }
}

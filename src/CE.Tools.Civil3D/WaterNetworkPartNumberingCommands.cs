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
            internal ObjectId AlignmentId;
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
                "Shared junctions belong to the larger-pipe / longer priority branch; branches run high to low. " +
                "No pipe geometry, slope, invert or rule will be changed.");
            options.AddChoice("Scope", "01 Selection", "Water network parts",
                "All Water networks", "Select Water pipe networks or individual ordinary pipes/structures.",
                new[] { "All Water networks", "Selected Water network parts" });
            options.AddPositiveDouble("Tolerance", "02 Matching", "Maximum alignment offset (drawing units)", 5.0,
                "Parts farther from any Water branch alignment are skipped to prevent incorrect numbers.");
            options.AddChoice("Action", "03 Safety", "Numbering action", "Preview only",
                "Preview all proposed name changes before choosing the rename action.",
                new[] { "Preview only", "Apply names to Water pipe and structure objects" });
            var surfaces = WorkflowRepairCommands.ReadSurfaceChoices(doc);
            options.AddChoice("TargetSurface", "02 Matching", "Target elevation surface", "<Alignment direction>",
                "Number from high to low using endpoint surface elevations; preserve alignment and pipe geometry.",
                new[] { "<Alignment direction>" }.Concat(surfaces.Select(x => x.Name)).ToArray());
            var pipeStyles = SewerNetworkLabelCommands.ReadPipeLabelStyleNames(doc);
            var structureStyles = SewerNetworkLabelCommands.ReadStructureLabelStyleNames(doc);
            options.AddChoice("PipeStyle", "04 Layout labels", "Pipe label style", "<Keep existing>",
                "Apply this plan label style to the numbered pipes.", new[] { "<Keep existing>" }.Concat(pipeStyles).ToArray());
            options.AddChoice("StructureStyle", "04 Layout labels", "Structure label style", "<Keep existing>",
                "Apply this plan label style to the numbered structures.", new[] { "<Keep existing>" }.Concat(structureStyles).ToArray());
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
            var networks = new HashSet<ObjectId>();
            var selectedParts = new HashSet<ObjectId>();
            var alignmentNames = new Dictionary<ObjectId, string>();
            var reverse = new HashSet<ObjectId>();
            var elevations = new Dictionary<ObjectId, double>();
            int unmatched = 0;
            double maxOffset = options.Double("Tolerance", 5.0);
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                var alignments = new List<CivilAlignment>();
                foreach (ObjectId id in civil.GetAlignmentIds())
                {
                    CivilAlignment a = tr.GetObject(id, OpenMode.ForRead, false) as CivilAlignment;
                    if (a != null && !a.IsReferenceObject &&
                        ((a.Name.StartsWith("BRANCH-", StringComparison.OrdinalIgnoreCase) &&
                          (a.Description ?? "").IndexOf("water", StringComparison.OrdinalIgnoreCase) >= 0) ||
                         a.Name.StartsWith("W-", StringComparison.OrdinalIgnoreCase) ||
                         a.Name.IndexOf("Water", StringComparison.OrdinalIgnoreCase) >= 0))
                        alignments.Add(a);
                }
                if (alignments.Count == 0)
                {
                    MessageBox.Show("No Water route alignments exist. Run CE_WATERSEQ and CE_WATERALIGN first.",
                        "CE Tools - Water Numbering"); return;
                }
                var diameters = alignments.ToDictionary(a => a.ObjectId, a => 0.0);
                // Rate branches using actual pipes, not selection order. Shared nodes are
                // matched against this priority order, so a small branch cannot steal a trunk node.
                foreach (ObjectId id in civil.GetPipeNetworkIds())
                {
                    var network = tr.GetObject(id, OpenMode.ForRead) as CivilNetwork;
                    if (network == null || network.IsReferenceObject ||
                        !(network.Name.IndexOf("water", StringComparison.OrdinalIgnoreCase) >= 0 || network.Name.StartsWith("W-", StringComparison.OrdinalIgnoreCase))) continue;
                    foreach (ObjectId pipeId in network.GetPipeIds())
                    {
                        var pipe = tr.GetObject(pipeId, OpenMode.ForRead) as CivilPipe;
                        if (pipe == null) continue;
                        var mid = pipe.StartPoint + (pipe.EndPoint - pipe.StartPoint) * 0.5;
                        var route = Match(alignments, mid.X, mid.Y, maxOffset, pipeId, true);
                        if (route != null) diameters[route.AlignmentId] = Math.Max(diameters[route.AlignmentId], pipe.InnerDiameterOrWidth);
                    }
                }

                var selectedSurface = surfaces.FirstOrDefault(x => x.Name == options.Text("TargetSurface"));
                var surface = selectedSurface == null ? null : tr.GetObject(selectedSurface.ObjectId, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Surface;
                for (int i = 0; i < alignments.Count; i++)
                {
                    var a = alignments[i];
                    elevations[a.ObjectId] = 0;
                    if (surface == null) continue;
                    try
                    {
                        double x1 = 0, y1 = 0, x2 = 0, y2 = 0;
                        a.PointLocation(a.StartingStation, 0, ref x1, ref y1);
                        a.PointLocation(a.EndingStation, 0, ref x2, ref y2);
                        double first = surface.FindElevationAtXY(x1, y1), last = surface.FindElevationAtXY(x2, y2);
                        elevations[a.ObjectId] = Math.Max(first, last);
                        if (last > first) reverse.Add(a.ObjectId);
                    }
                    catch (System.Exception)
                    {
                        MessageBox.Show("Branch " + a.Name + " is outside the target surface. No names changed.", "CE Tools - Water Numbering");
                        return;
                    }
                }
                alignments = alignments.OrderBy(a => a, Comparer<CivilAlignment>.Create((a, b) =>
                    WaterBranchPriority.Compare(diameters[a.ObjectId], a.Length, elevations[a.ObjectId], a.ObjectId.Handle.ToString(),
                        diameters[b.ObjectId], b.Length, elevations[b.ObjectId], b.ObjectId.Handle.ToString(), "Larger pipes, then longer branches"))).ToList();
                for (int i = 0; i < alignments.Count; i++)
                    alignmentNames[alignments[i].ObjectId] = "BRANCH-" + (i + 1).ToString(CultureInfo.InvariantCulture);
                foreach (ObjectId networkId in civil.GetPipeNetworkIds())
                {
                    CivilNetwork network = tr.GetObject(networkId, OpenMode.ForRead, false) as CivilNetwork;
                    if (network == null || network.IsReferenceObject ||
                        !(network.Name.IndexOf("Water", StringComparison.OrdinalIgnoreCase) >= 0 ||
                          network.Name.StartsWith("W-", StringComparison.OrdinalIgnoreCase))) continue;
                    networks.Add(networkId);
                    foreach (ObjectId pipeId in network.GetPipeIds())
                    {
                        if (chosen == null || chosen.Contains(pipeId) || chosen.Contains(networkId)) selectedParts.Add(pipeId);
                        CivilPipe pipe = tr.GetObject(pipeId, OpenMode.ForRead, false) as CivilPipe;
                        if (pipe == null) continue;
                        var point = pipe.StartPoint + (pipe.EndPoint - pipe.StartPoint) * 0.5;
                        PartRoute route = Match(alignments, point.X, point.Y, maxOffset, pipeId, true);
                        if (route != null) assignments.Add(route); else unmatched++;
                    }
                    foreach (ObjectId structureId in network.GetStructureIds())
                    {
                        if (chosen == null || chosen.Contains(structureId) || chosen.Contains(networkId)) selectedParts.Add(structureId);
                        CivilStructure structure = tr.GetObject(structureId, OpenMode.ForRead, false) as CivilStructure;
                        if (structure == null) continue;
                        var connectedRoutes = new HashSet<ObjectId>();
                        foreach (ObjectId pipeId in network.GetPipeIds())
                        {
                            var connected = tr.GetObject(pipeId, OpenMode.ForRead) as CivilPipe;
                            if (connected == null || (connected.StartStructureId != structureId && connected.EndStructureId != structureId)) continue;
                            var mid = connected.StartPoint + (connected.EndPoint - connected.StartPoint) * 0.5;
                            var owner = Match(alignments, mid.X, mid.Y, maxOffset, pipeId, true);
                            if (owner != null) connectedRoutes.Add(owner.AlignmentId);
                        }
                        PartRoute route = Match(connectedRoutes.Count == 0 ? alignments :
                            alignments.Where(a => connectedRoutes.Contains(a.ObjectId)),
                            structure.Position.X, structure.Position.Y, maxOffset, structureId, false);
                        if (route != null) assignments.Add(route); else unmatched++;
                    }
                }
            }
            foreach (var part in assignments)
            {
                part.Route = alignmentNames[part.AlignmentId].Substring(7);
                if (reverse.Contains(part.AlignmentId)) part.Station = -part.Station;
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
            assignments = assignments.Where(a => selectedParts.Contains(a.PartId)).ToList();
            if (assignments.Count == 0)
            {
                MessageBox.Show("None of the " + unmatched +
                    " Water parts matched a numbered Water branch alignment.\\n" +
                    "Check that CE_WATERALIGN generated Water alignments on the same pipe routes, " +
                    "or increase the maximum alignment offset in the popup. No part names were changed.",
                    "CE Tools - Water Numbering");
                return;
            }
            if (options.Text("Action") == "Preview only")
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
                // Rename via temporary names to avoid collisions when branch priority changes.
                var affectedAlignments = assignments.Select(a => a.AlignmentId).Distinct().ToList();
                var targetNames = new HashSet<string>(affectedAlignments.Select(id => alignmentNames[id]), StringComparer.OrdinalIgnoreCase);
                foreach (ObjectId id in civil.GetAlignmentIds())
                {
                    if (affectedAlignments.Contains(id)) continue;
                    var other = tr.GetObject(id, OpenMode.ForRead) as CivilAlignment;
                    if (other != null && targetNames.Contains(other.Name))
                    {
                        MessageBox.Show("Branch name " + other.Name + " is already used by an unselected alignment. Select all affected Water parts, or rename the unrelated alignment, then retry. No changes made.", "CE Tools - Water Numbering");
                        return;
                    }
                }
                foreach (var id in affectedAlignments)
                    ((CivilAlignment)tr.GetObject(id, OpenMode.ForWrite)).Name = "CE-WATER-TEMP-" + Guid.NewGuid().ToString("N");
                foreach (var id in affectedAlignments)
                {
                    var alignment = (CivilAlignment)tr.GetObject(id, OpenMode.ForWrite);
                    alignment.Name = alignmentNames[id];
                    string description = alignment.Description ?? "";
                    if (description.IndexOf("route=", StringComparison.OrdinalIgnoreCase) >= 0)
                        description = System.Text.RegularExpressions.Regex.Replace(description, @"route=[^|]*", "route=" + alignmentNames[id] + " ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    else description += " | CE water alignment | route=" + alignmentNames[id];
                    alignment.Description = description;
                    WaterProductionCommands.SyncBranchIdentity(doc.Database, tr, alignment, alignmentNames[id]);
                }
                foreach (PartRoute part in assignments)
                {
                    var obj = tr.GetObject(part.PartId, OpenMode.ForWrite, false);
                    string temporary = "CE-WATER-PART-" + Guid.NewGuid().ToString("N");
                    if (obj is CivilPipe) ((CivilPipe)obj).Name = temporary;
                    else if (obj is CivilStructure) ((CivilStructure)obj).Name = temporary;
                }
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
                        MessageBox.Show("Water numbering rolled back for " + part.PartId.Handle + ": " + ex.Message,
                            "CE Tools - Water Numbering");
                        return;
                    }
                }
                tr.Commit();
            }
            string labelWarning = "";
            if (options.Text("PipeStyle") != "<Keep existing>" || options.Text("StructureStyle") != "<Keep existing>")
            {
                var labelled = new HashSet<ObjectId>(assignments.Where(p => p.Pipe ?
                    options.Text("PipeStyle") != "<Keep existing>" : options.Text("StructureStyle") != "<Keep existing>").Select(p => p.PartId));
                var labels = SewerNetworkLabelCommands.EnsureWaterLabels(doc, networks,
                    options.Text("PipeStyle") == "<Keep existing>" ? null : options.Text("PipeStyle"),
                    options.Text("StructureStyle") == "<Keep existing>" ? null : options.Text("StructureStyle"), labelled);
                labelWarning = labels.Warning;
            }
            doc.Editor.Regen();
            MessageBox.Show("Water pipes/structures renamed: " + renamed +
                "\nUnmatched parts: " + unmatched + "\nFailed: " + failed +
                "\nThe selected Civil 3D label style must display Part Name to show the new numbers.\n" + labelWarning,
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
                        station > alignment.EndingStation + 0.01 || !WaterBranchPriority.PreferCandidate(distance, bestOffset, best != null))
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
            return new PartRoute {
                PartId = partId, Route = name, AlignmentId = best.ObjectId,
                Station = bestStation, Pipe = pipe
            };
        }
    }
}

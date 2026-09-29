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
using CivilNetwork = Autodesk.Civil.DatabaseServices.Network;
using CivilPipe = Autodesk.Civil.DatabaseServices.Pipe;
using CivilStructure = Autodesk.Civil.DatabaseServices.Structure;

[assembly: CommandClass(typeof(CETools.Civil3D.SewerFlowDirectionCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Sets gravity-pipe FlowDirectionMethod from the actual structure/pipe
    /// topology toward one operator-selected downstream structure (outlet/low
    /// point). No alignment direction is required and no pipe endpoint, invert,
    /// part, rule-set style or structure rule assignment is changed.
    /// </summary>
    public sealed class SewerFlowDirectionCommands
    {
        [CommandMethod("CE_TOOLS", "CE_SEWFLOWTOOUTLET",
            CommandFlags.Modal | CommandFlags.Redraw | CommandFlags.UsePickSet)]
        public void ApplyFlowToOutlet()
        {
            ApplyFlowToOutletCore("CE_SEWFLOWTOOUTLET");
        }

        // Compatibility alias retained for existing buttons/workflows. Its field
        // behaviour is intentionally the same structure-sequence/outlet workflow.
        [CommandMethod("CE_TOOLS", "CE_SEWFLOWBYALIGNMENT",
            CommandFlags.Modal | CommandFlags.Redraw | CommandFlags.UsePickSet)]
        public void ApplyFlowByAlignmentCompatibility()
        {
            ApplyFlowToOutletCore("CE_SEWFLOWBYALIGNMENT");
        }

        private static void ApplyFlowToOutletCore(string commandName)
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civil = CivilApplication.ActiveDocument;
            if (document == null || civil == null) return;

            ObjectId outletId = PromptOutletStructure(document);
            if (outletId.IsNull) return;

            ObjectId networkId;
            string outletName;
            Point3d outletPoint;
            using (Transaction read =
                document.Database.TransactionManager.StartTransaction())
            {
                CivilStructure outlet = read.GetObject(
                    outletId, OpenMode.ForRead, false) as CivilStructure;
                if (outlet == null || outlet.IsReferenceObject)
                {
                    document.Editor.WriteMessage(
                        "\n{0}: select an editable gravity-network structure.",
                        commandName);
                    return;
                }

                networkId = outlet.NetworkId;
                outletName = outlet.Name ?? outletId.Handle.ToString();
                outletPoint = outlet.Position;
            }

            if (networkId.IsNull)
            {
                document.Editor.WriteMessage(
                    "\n{0}: the selected structure is not assigned to a gravity network.",
                    commandName);
                return;
            }

            int updated = 0;
            int already = 0;
            int disconnected = 0;
            int tieBreaks = 0;
            int rulesPreserved = 0;
            int geometryPreserved = 0;
            int connectedStructures = 0;

            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction tr =
                document.Database.TransactionManager.StartTransaction())
            {
                CivilNetwork network = tr.GetObject(
                    networkId, OpenMode.ForRead, false) as CivilNetwork;
                if (network == null)
                {
                    document.Editor.WriteMessage(
                        "\n{0}: the selected sewer network is unavailable.",
                        commandName);
                    return;
                }

                var pipes = new List<PipeLink>();
                var adjacency = new Dictionary<ObjectId, List<PipeLink>>();

                foreach (ObjectId pipeId in network.GetPipeIds())
                {
                    CivilPipe pipe;
                    try
                    {
                        pipe = tr.GetObject(
                            pipeId, OpenMode.ForRead, false) as CivilPipe;
                    }
                    catch { continue; }
                    if (pipe == null || pipe.IsReferenceObject) continue;

                    ObjectId startStructureId = ObjectId.Null;
                    ObjectId endStructureId = ObjectId.Null;
                    try { startStructureId = pipe.StartStructureId; }
                    catch { }
                    try { endStructureId = pipe.EndStructureId; }
                    catch { }

                    if (startStructureId.IsNull || endStructureId.IsNull)
                    {
                        disconnected++;
                        continue;
                    }

                    var link = new PipeLink
                    {
                        PipeId = pipeId,
                        StartStructureId = startStructureId,
                        EndStructureId = endStructureId
                    };
                    pipes.Add(link);
                    Add(adjacency, startStructureId, link);
                    Add(adjacency, endStructureId, link);
                }

                Dictionary<ObjectId, int> distance =
                    DistanceFromOutlet(outletId, adjacency);
                connectedStructures = distance.Count;

                foreach (PipeLink link in pipes)
                {
                    int startDistance;
                    int endDistance;
                    if (!distance.TryGetValue(link.StartStructureId, out startDistance) ||
                        !distance.TryGetValue(link.EndStructureId, out endDistance))
                    {
                        disconnected++;
                        continue;
                    }

                    CivilPipe pipe;
                    try
                    {
                        pipe = tr.GetObject(
                            link.PipeId, OpenMode.ForWrite, false) as CivilPipe;
                    }
                    catch { continue; }
                    if (pipe == null) continue;

                    FlowDirectionMethodType wanted;
                    if (startDistance > endDistance)
                    {
                        wanted = FlowDirectionMethodType.StartToEnd;
                    }
                    else if (endDistance > startDistance)
                    {
                        wanted = FlowDirectionMethodType.EndToStart;
                    }
                    else
                    {
                        // A loop/cycle can put both structures at the same number
                        // of graph steps from the outlet. Prefer the endpoint
                        // physically nearer the selected low point; if still tied,
                        // fall back to the lower endpoint elevation.
                        tieBreaks++;
                        Point3d startPoint = pipe.StartPoint;
                        Point3d endPoint = pipe.EndPoint;
                        double startPlan = PlanDistance(startPoint, outletPoint);
                        double endPlan = PlanDistance(endPoint, outletPoint);
                        if (Math.Abs(startPlan - endPlan) > 0.001)
                            wanted = startPlan > endPlan
                                ? FlowDirectionMethodType.StartToEnd
                                : FlowDirectionMethodType.EndToStart;
                        else
                            wanted = startPoint.Z >= endPoint.Z
                                ? FlowDirectionMethodType.StartToEnd
                                : FlowDirectionMethodType.EndToStart;
                    }

                    ObjectId originalRule = pipe.RuleSetStyleId;
                    Point3d originalStart = pipe.StartPoint;
                    Point3d originalEnd = pipe.EndPoint;

                    if (pipe.FlowDirectionMethod == wanted)
                        already++;
                    else
                    {
                        pipe.FlowDirectionMethod = wanted;
                        updated++;
                    }

                    if (pipe.RuleSetStyleId == originalRule)
                        rulesPreserved++;
                    if (pipe.StartPoint.DistanceTo(originalStart) <= 1e-9 &&
                        pipe.EndPoint.DistanceTo(originalEnd) <= 1e-9)
                        geometryPreserved++;
                }

                tr.Commit();
            }

            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\n{0} complete. Outlet/low point='{1}'. Flow now follows connected structure sequence toward that structure. Pipes updated={2}; already correct={3}; disconnected/open pipes={4}; loop tie-breaks={5}; connected structures={6}; pipe rule sets preserved={7}; endpoint geometry preserved={8}.",
                commandName,
                outletName,
                updated,
                already,
                disconnected,
                tieBreaks,
                connectedStructures,
                rulesPreserved,
                geometryPreserved);
        }

        private static ObjectId PromptOutletStructure(Document document)
        {
            var options = new PromptEntityOptions(
                "\nSelect the downstream outlet / low-point structure that all connected sewer flow must run toward: ");
            PromptEntityResult result = document.Editor.GetEntity(options);
            if (result.Status != PromptStatus.OK) return ObjectId.Null;

            try
            {
                using (Transaction tr =
                    document.Database.TransactionManager.StartTransaction())
                {
                    CivilStructure structure = tr.GetObject(
                        result.ObjectId,
                        OpenMode.ForRead,
                        false) as CivilStructure;
                    return structure == null ? ObjectId.Null : result.ObjectId;
                }
            }
            catch { return ObjectId.Null; }
        }

        private static Dictionary<ObjectId, int> DistanceFromOutlet(
            ObjectId outletId,
            IDictionary<ObjectId, List<PipeLink>> adjacency)
        {
            var result = new Dictionary<ObjectId, int>();
            var queue = new Queue<ObjectId>();
            result[outletId] = 0;
            queue.Enqueue(outletId);

            while (queue.Count > 0)
            {
                ObjectId current = queue.Dequeue();
                int nextDistance = result[current] + 1;
                List<PipeLink> links;
                if (!adjacency.TryGetValue(current, out links)) continue;

                foreach (PipeLink link in links)
                {
                    ObjectId next = link.StartStructureId == current
                        ? link.EndStructureId
                        : link.StartStructureId;
                    if (next.IsNull || result.ContainsKey(next)) continue;
                    result[next] = nextDistance;
                    queue.Enqueue(next);
                }
            }
            return result;
        }

        private static void Add(
            IDictionary<ObjectId, List<PipeLink>> adjacency,
            ObjectId structureId,
            PipeLink link)
        {
            List<PipeLink> values;
            if (!adjacency.TryGetValue(structureId, out values))
            {
                values = new List<PipeLink>();
                adjacency[structureId] = values;
            }
            values.Add(link);
        }

        private static double PlanDistance(Point3d first, Point3d second)
        {
            double dx = first.X - second.X;
            double dy = first.Y - second.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private sealed class PipeLink
        {
            internal ObjectId PipeId;
            internal ObjectId StartStructureId;
            internal ObjectId EndStructureId;
        }
    }
}

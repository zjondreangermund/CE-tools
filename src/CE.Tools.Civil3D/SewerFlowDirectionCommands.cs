using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivilNetwork = Autodesk.Civil.DatabaseServices.Network;
using CivilPipe = Autodesk.Civil.DatabaseServices.Pipe;
using CivilStructure = Autodesk.Civil.DatabaseServices.Structure;

[assembly: CommandClass(typeof(CETools.Civil3D.SewerFlowDirectionCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Sets gravity-pipe flow metadata from branch-alignment stationing without
    /// changing pipe geometry. Pipe/structure rule-set ObjectIds and endpoint
    /// coordinates are deliberately preserved.
    /// </summary>
    public sealed class SewerFlowDirectionCommands
    {
        [CommandMethod("CE_TOOLS", "CE_SEWFLOWBYALIGNMENT",
            CommandFlags.Modal | CommandFlags.Redraw | CommandFlags.UsePickSet)]
        public void ApplyFlowByAlignment()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civil = CivilApplication.ActiveDocument;
            if (document == null || civil == null) return;

            ObjectId outletId = ObjectId.Null;
            var outletChoice = new PromptKeywordOptions(
                "\nValidate flow toward a selected outlet/outflow structure? [Select/Skip] <Select>: ")
            {
                AllowNone = true
            };
            outletChoice.Keywords.Add("Select");
            outletChoice.Keywords.Add("Skip");
            PromptResult choice = document.Editor.GetKeywords(outletChoice);
            if (choice.Status == PromptStatus.Cancel) return;
            if (choice.Status == PromptStatus.None ||
                string.Equals(choice.StringResult, "Select", StringComparison.OrdinalIgnoreCase))
            {
                var entityOptions = new PromptEntityOptions(
                    "\nSelect downstream outlet/outflow sewer structure: ");
                PromptEntityResult entity = document.Editor.GetEntity(entityOptions);
                if (entity.Status != PromptStatus.OK) return;
                using (Transaction read = document.Database.TransactionManager.StartTransaction())
                {
                    CivilStructure structure = read.GetObject(
                        entity.ObjectId, OpenMode.ForRead, false) as CivilStructure;
                    if (structure == null)
                    {
                        document.Editor.WriteMessage(
                            "\nCE_SEWFLOWBYALIGNMENT: selected object is not a gravity-network structure.");
                        return;
                    }
                    outletId = structure.ObjectId;
                }
            }

            List<AlignmentRecord> alignments = ReadSewerAlignments(
                document.Database, civil);
            if (alignments.Count == 0)
            {
                document.Editor.WriteMessage(
                    "\nCE_SEWFLOWBYALIGNMENT: no sewer branch alignments were found.");
                return;
            }

            int updated = 0, already = 0, unmatched = 0;
            int rulesPreserved = 0, geometryPreserved = 0, outletWarnings = 0;

            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction tr = document.Database.TransactionManager.StartTransaction())
            {
                Point3d? outletPoint = null;
                if (!outletId.IsNull)
                {
                    CivilStructure outlet = tr.GetObject(
                        outletId, OpenMode.ForRead, false) as CivilStructure;
                    if (outlet != null) outletPoint = outlet.Position;
                }

                foreach (ObjectId networkId in civil.GetPipeNetworkIds())
                {
                    CivilNetwork network;
                    try { network = tr.GetObject(networkId, OpenMode.ForRead, false) as CivilNetwork; }
                    catch { continue; }
                    if (network == null || network.IsReferenceObject) continue;

                    foreach (ObjectId pipeId in network.GetPipeIds())
                    {
                        CivilPipe pipe;
                        try { pipe = tr.GetObject(pipeId, OpenMode.ForWrite, false) as CivilPipe; }
                        catch { continue; }
                        if (pipe == null || pipe.IsReferenceObject) continue;

                        AlignmentRecord match = FindAlignment(
                            alignments, tr, pipe.StartPoint, pipe.EndPoint);
                        if (match == null)
                        {
                            unmatched++;
                            continue;
                        }

                        CivilAlignment alignment = tr.GetObject(
                            match.Id, OpenMode.ForRead, false) as CivilAlignment;
                        if (alignment == null) { unmatched++; continue; }

                        double startStation, startOffset, endStation, endOffset;
                        if (!StationOffset(alignment, pipe.StartPoint,
                                out startStation, out startOffset) ||
                            !StationOffset(alignment, pipe.EndPoint,
                                out endStation, out endOffset))
                        {
                            unmatched++;
                            continue;
                        }

                        // Snapshot invariants before changing flow metadata.
                        ObjectId originalPipeRule = pipe.RuleSetStyleId;
                        Point3d originalStart = pipe.StartPoint;
                        Point3d originalEnd = pipe.EndPoint;

                        FlowDirectionMethodType wanted =
                            startStation <= endStation
                                ? FlowDirectionMethodType.StartToEnd
                                : FlowDirectionMethodType.EndToStart;

                        if (pipe.FlowDirectionMethod == wanted) already++;
                        else
                        {
                            pipe.FlowDirectionMethod = wanted;
                            updated++;
                        }

                        if (pipe.RuleSetStyleId == originalPipeRule)
                            rulesPreserved++;
                        if (pipe.StartPoint.DistanceTo(originalStart) <= 1e-9 &&
                            pipe.EndPoint.DistanceTo(originalEnd) <= 1e-9)
                            geometryPreserved++;
                    }
                }

                if (outletPoint.HasValue)
                {
                    // The main/downstream alignment should terminate nearest the
                    // selected outlet. Do not reverse it automatically because
                    // Alignment.Reverse() can affect dependent profiles/labels.
                    AlignmentRecord nearest = alignments
                        .OrderBy(item => Math.Min(
                            item.Start.DistanceTo(outletPoint.Value),
                            item.End.DistanceTo(outletPoint.Value)))
                        .FirstOrDefault();
                    if (nearest != null &&
                        nearest.End.DistanceTo(outletPoint.Value) >
                        nearest.Start.DistanceTo(outletPoint.Value) + 0.01)
                    {
                        outletWarnings++;
                        document.Editor.WriteMessage(
                            "\nCE_SEWFLOWBYALIGNMENT warning: alignment '{0}' starts nearer the selected outlet than it ends. Flow metadata was not allowed to reverse the alignment; reverse/fix that alignment direction first.",
                            nearest.Name);
                    }
                }

                tr.Commit();
            }

            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_SEWFLOWBYALIGNMENT complete. Pipes updated={0}; already correct={1}; unmatched={2}; pipe rule sets preserved={3}; pipe endpoint geometry preserved={4}; outlet-direction warnings={5}. Flow follows each branch alignment start -> end toward the main branch/outlet.",
                updated, already, unmatched, rulesPreserved, geometryPreserved, outletWarnings);
        }

        private static List<AlignmentRecord> ReadSewerAlignments(
            Database database,
            CivilDocument civil)
        {
            var result = new List<AlignmentRecord>();
            using (Transaction tr = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in civil.GetAlignmentIds())
                {
                    CivilAlignment alignment;
                    try { alignment = tr.GetObject(id, OpenMode.ForRead, false) as CivilAlignment; }
                    catch { continue; }
                    if (alignment == null) continue;
                    string description = alignment.Description ?? string.Empty;
                    string name = alignment.Name ?? string.Empty;
                    if (description.IndexOf("CE sewer alignment", StringComparison.OrdinalIgnoreCase) < 0 &&
                        name.IndexOf("Branch-", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    Point3d start, end;
                    if (!PointAt(alignment, alignment.StartingStation, out start) ||
                        !PointAt(alignment, alignment.EndingStation, out end))
                        continue;
                    result.Add(new AlignmentRecord
                    {
                        Id = id,
                        Name = name,
                        Start = start,
                        End = end
                    });
                }
            }
            return result;
        }

        private static AlignmentRecord FindAlignment(
            IEnumerable<AlignmentRecord> records,
            Transaction tr,
            Point3d a,
            Point3d b)
        {
            AlignmentRecord best = null;
            double bestOffset = double.MaxValue;
            foreach (AlignmentRecord record in records)
            {
                CivilAlignment alignment;
                try { alignment = tr.GetObject(record.Id, OpenMode.ForRead, false) as CivilAlignment; }
                catch { continue; }
                if (alignment == null) continue;
                double sa, oa, sb, ob;
                if (!StationOffset(alignment, a, out sa, out oa) ||
                    !StationOffset(alignment, b, out sb, out ob))
                    continue;
                double maxOffset = Math.Max(Math.Abs(oa), Math.Abs(ob));
                if (maxOffset > 1.0) continue;
                if (sa < alignment.StartingStation - 0.05 ||
                    sa > alignment.EndingStation + 0.05 ||
                    sb < alignment.StartingStation - 0.05 ||
                    sb > alignment.EndingStation + 0.05)
                    continue;
                if (maxOffset < bestOffset)
                {
                    best = record;
                    bestOffset = maxOffset;
                }
            }
            return best;
        }

        private static bool StationOffset(
            CivilAlignment alignment,
            Point3d point,
            out double station,
            out double offset)
        {
            station = 0.0;
            offset = 0.0;
            try
            {
                alignment.StationOffset(
                    point.X, point.Y, ref station, ref offset);
                return true;
            }
            catch { return false; }
        }

        private static bool PointAt(
            CivilAlignment alignment,
            double station,
            out Point3d point)
        {
            point = Point3d.Origin;
            try
            {
                double x = 0.0, y = 0.0;
                alignment.PointLocation(station, 0.0, ref x, ref y);
                point = new Point3d(x, y, 0.0);
                return true;
            }
            catch { return false; }
        }

        private sealed class AlignmentRecord
        {
            internal ObjectId Id;
            internal string Name;
            internal Point3d Start;
            internal Point3d End;
        }
    }
}

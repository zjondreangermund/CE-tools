using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.DatabaseServices;

namespace CETools.Civil3D
{
    internal static class SewerPipeConnections
    {
        private static readonly Regex PipeName = new Regex(@"^P(?<branch>\d+)\.(?<sequence>\d+)$", RegexOptions.IgnoreCase);

        internal static List<ObjectId> PipeIds(Structure structure)
        {
            var ids = new List<ObjectId>();
            // Civil 3D exposes an indexed ConnectedPipe property, not a
            // ConnectedPipeIds/GetConnectedPipeIds collection.
            for (int index = 0; index < structure.ConnectedPipesCount; index++)
                ids.Add(structure.get_ConnectedPipe(index));
            return ids;
        }

        internal static bool TryForward(Pipe pipe, Transaction tr, out bool forward)
        {
            forward = true;
            Match match = PipeName.Match(pipe.Name ?? string.Empty);
            if (match.Success)
            {
                string upstream = "MH" + match.Groups["branch"].Value + "." + match.Groups["sequence"].Value;
                if (string.Equals(StructureName(pipe.StartStructureId, tr), upstream, StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(StructureName(pipe.EndStructureId, tr), upstream, StringComparison.OrdinalIgnoreCase))
                { forward = false; return true; }
            }
            if (pipe.FlowDirectionMethod == FlowDirectionMethodType.StartToEnd) return true;
            if (pipe.FlowDirectionMethod == FlowDirectionMethodType.EndToStart) { forward = false; return true; }
            // The existing pipe grade is the last fallback for legacy unsequenced
            // incoming parts. Never use the natural-ground or sump elevation.
            double difference = pipe.StartPoint.Z - pipe.EndPoint.Z;
            if (double.IsNaN(difference) || double.IsInfinity(difference) || Math.Abs(difference) <= 1e-9) return false;
            forward = difference > 0;
            return true;
        }

        internal static double Invert(Pipe pipe, bool atStart)
        {
            return (atStart ? pipe.StartPoint.Z : pipe.EndPoint.Z) - pipe.InnerHeight * 0.5;
        }

        private static string StructureName(ObjectId id, Transaction tr)
        {
            if (id.IsNull || id.IsErased) return string.Empty;
            return (tr.GetObject(id, OpenMode.ForRead, false) as Structure)?.Name ?? string.Empty;
        }
    }
}

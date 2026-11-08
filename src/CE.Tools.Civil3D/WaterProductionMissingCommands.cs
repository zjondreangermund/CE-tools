using System;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.WaterProductionMissingCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Restores water production command entry points. Native workflows operate on
    /// selections; delegates use existing Civil 3D production implementations
    /// rather than creating empty/placeholder commands.
    /// </summary>
    public sealed class WaterProductionMissingCommands
    {
        private static void RunExisting(string command)
        {
            Document doc = AcApplication.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            doc.SendStringToExecute("_." + command + "\n", true, false, false);
        }

        [CommandMethod("CE_TOOLS", "CE_WATERALIGNLABELS", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void AlignmentLabels()
        {
            RunExisting("CE_ALIGNLABELSETMULTI");
        }

        [CommandMethod("CE_TOOLS", "CE_WATERPROFILEVIEWSTANDARDS", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void ProfileViewStandards()
        {
            RunExisting("CE_PROFILEVIEWBATCH");
        }

        [CommandMethod("CE_TOOLS", "CE_WATERPROFILELABELSTYLES", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void ProfilePartLabelStyles()
        {
            // The established native profile-label editor operates on the chosen
            // profile views; the "sewer" name does not restrict the selected parts.
            RunExisting("CE_SEWPROFILELABELSTYLESMULTI");
        }

        [CommandMethod("CE_TOOLS", "CE_WATERPROFILEBANDMANAGEMENT", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void ProfileBands()
        {
            RunExisting("CE_PROFILEVIEWDATASOURCES");
        }

        [CommandMethod("CE_TOOLS", "CE_WATERPARTSWAPMULTI", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void SwapParts()
        {
            // The shared gravity-network part swapper edits the selected parts,
            // preserving source network, family list and associated model geometry.
            RunExisting("CE_SEWPARTSWAPMULTI");
        }

        [CommandMethod("CE_TOOLS", "CE_WATERLOCATEPLAN", CommandFlags.Modal | CommandFlags.Redraw)]
        public void LocatePlanAndProfile()
        {
            RunExisting("CE_ALPROFILEJUMP");
        }

        [CommandMethod("CE_TOOLS", "CE_WATERVIEWPORTLOCK", CommandFlags.Modal | CommandFlags.Redraw)]
        public void LockWaterViewports()
        {
            October03ViewportPlotProfileCommands.SetViewportLock(true);
        }

        [CommandMethod("CE_TOOLS", "CE_WATERVIEWPORTUNLOCK", CommandFlags.Modal | CommandFlags.Redraw)]
        public void UnlockWaterViewports()
        {
            October03ViewportPlotProfileCommands.SetViewportLock(false);
        }

        [CommandMethod("CE_TOOLS", "CE_WATERVIEWPORTREGEN", CommandFlags.Modal | CommandFlags.Redraw)]
        public void RegenerateWaterViewports()
        {
            new October03ViewportPlotProfileCommands().RegenerateViewports();
        }

        [CommandMethod("CE_TOOLS", "CE_WATERINTEGRITYAUDIT", CommandFlags.Modal | CommandFlags.Redraw)]
        public void AuditWaterNetworks()
        {
            Document doc = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civil = CivilApplication.ActiveDocument;
            if (doc == null || civil == null) return;
            int networks = 0, pipes = 0, structures = 0, problems = 0;
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId networkId in civil.GetPipeNetworkIds())
                {
                    Network network = tr.GetObject(networkId, OpenMode.ForRead, false) as Network;
                    if (network == null) continue;
                    string name = network.Name ?? string.Empty;
                    if (name.IndexOf("water", StringComparison.OrdinalIgnoreCase) < 0 &&
                        !name.StartsWith("W-", StringComparison.OrdinalIgnoreCase)) continue;
                    networks++;
                    foreach (ObjectId partId in network.GetPipeIds())
                    {
                        Pipe pipe = tr.GetObject(partId, OpenMode.ForRead, false) as Pipe;
                        if (pipe == null) continue;
                        pipes++;
                        if ((pipe.EndPoint - pipe.StartPoint).Length < 0.001)
                        {
                            problems++;
                            doc.Editor.WriteMessage("\nWater pipe with zero/invalid length: " + pipe.Name);
                        }
                    }
                    foreach (ObjectId partId in network.GetStructureIds())
                    {
                        Structure structure = tr.GetObject(partId, OpenMode.ForRead, false) as Structure;
                        if (structure != null) structures++;
                    }
                }
            }
            doc.Editor.WriteMessage(
                "\nWater network geometry audit: networks={0}, pipes={1}, structures={2}, invalid lengths={3}. " +
                "Review connections, cover and hydraulic design separately before issue.",
                networks, pipes, structures, problems);
            System.Windows.Forms.MessageBox.Show(
                "Water networks: " + networks + "\nPipes: " + pipes +
                "\nStructures: " + structures + "\nZero-length pipes: " + problems +
                "\n\nThis is a geometry preflight; it does not certify hydraulic or cover design.",
                "CE Tools - Water Network Geometry Audit");
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.EditorInput;
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
            Document doc = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civil = CivilApplication.ActiveDocument;
            if (doc == null || civil == null) return;
            IList<string> bandSets = CivilStyleCatalogV2.ReadNames(
                doc.Database, civil, "Profile View Band Set Style");
            if (bandSets.Count == 0)
            {
                System.Windows.Forms.MessageBox.Show(
                    "No profile-view band sets exist in this drawing.",
                    "CE Tools - Water Profile Bands");
                return;
            }
            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Water Profile Band Management",
                "Apply and materialise native profile band sets in selected Water profile views, then bind each view's own existing-ground profile and matching Water network.");
            settings.AddChoice("BandSet", "01 Styles", "Water profile band set",
                bandSets[0], "Choose an existing native Civil 3D band-set style.",
                bandSets);
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;

            PromptSelectionResult selection = doc.Editor.GetSelection(new PromptSelectionOptions
            {
                MessageForAdding = "\nSelect Water profile views for band-set management: ",
                AllowDuplicates = false,
                RejectObjectsFromNonCurrentSpace = true
            });
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;
            var views = new List<ObjectId>();
            ObjectId bandStyle;
            string resolved;
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                bandStyle = CivilStyleCatalogV2.ResolveStyleId(
                    doc.Database, civil, "Profile View Band Set Style",
                    settings.Text("BandSet"), tr, out resolved);
                foreach (ObjectId id in selection.Value.GetObjectIds().Distinct())
                {
                    var view = tr.GetObject(id, OpenMode.ForRead, false)
                        as Autodesk.Civil.DatabaseServices.ProfileView;
                    if (view != null && !view.IsReferenceObject) views.Add(id);
                }
            }
            if (bandStyle.IsNull || views.Count == 0) return;
            int done = 0, rows = 0, failed = 0;
            using (doc.LockDocument())
            {
                foreach (ObjectId viewId in views)
                {
                    try
                    {
                        SewerLongSectionContext context =
                            SewerLongSectionBandService.ResolveContext(doc.Database, civil, viewId);
                        if (context.NetworkId.IsNull)
                        {
                            doc.Editor.WriteMessage(
                                "\nWater band management skipped view {0}: no matching gravity network.",
                                viewId.Handle);
                            failed++;
                            continue;
                        }
                        var result = SewerLongSectionBandService.Apply(doc.Database,
                            viewId, bandStyle, ObjectId.Null,
                            context.GroundProfileId, context.NetworkId);
                        rows += result.BandRows;
                        done++;
                    }
                    catch (System.Exception exception)
                    {
                        failed++;
                        doc.Editor.WriteMessage(
                            "\nWater profile band setup failed for view {0}: {1}",
                            viewId.Handle, exception.Message);
                    }
                }
            }
            doc.Editor.Regen();
            System.Windows.Forms.MessageBox.Show(
                "Water profile views updated: " + done +
                "\nBand rows materialised: " + rows +
                "\nSkipped/failed: " + failed,
                "CE Tools - Water Profile Bands");
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

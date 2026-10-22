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
using CivilNetwork = Autodesk.Civil.DatabaseServices.Network;
using CivilPipe = Autodesk.Civil.DatabaseServices.Pipe;
using CivilStructure = Autodesk.Civil.DatabaseServices.Structure;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;

[assembly: CommandClass(typeof(CETools.Civil3D.WaterGravityProfilePartsCommands))]

namespace CETools.Civil3D
{
    public sealed class WaterGravityProfilePartsCommands
    {
        [CommandMethod("CE_TOOLS", "CE_WATERGRAVITYPROFILEPARTS",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void DrawParts()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civil = CivilApplication.ActiveDocument;
            if (document == null || civil == null) return;
            var sources = new List<ObjectId>();
            using (Transaction tr = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in civil.GetPipeNetworkIds())
                {
                    var network = tr.GetObject(id, OpenMode.ForRead, false) as CivilNetwork;
                    if (network == null || network.IsReferenceObject ||
                        !(network.Name.IndexOf("Water", StringComparison.OrdinalIgnoreCase) >= 0 ||
                          network.Name.StartsWith("W-", StringComparison.OrdinalIgnoreCase))) continue;
                    sources.AddRange(network.GetPipeIds().Cast<ObjectId>());
                    sources.AddRange(network.GetStructureIds().Cast<ObjectId>());
                }
            }
            if (sources.Count == 0)
            {
                document.Editor.WriteMessage("\nNo normal gravity Water pipes or structures were found.");
                return;
            }
            PromptSelectionResult choice = document.Editor.SelectImplied();
            var scope = new PromptKeywordOptions(
                "\nWater gravity parts [All/Selected] <All>: ");
            scope.Keywords.Add("All");
            scope.Keywords.Add("Selected");
            scope.AllowNone = true;
            bool preselected = choice.Status == PromptStatus.OK &&
                choice.Value != null && choice.Value.Count > 0;
            if (!preselected)
            {
                PromptResult answer = document.Editor.GetKeywords(scope);
                if (answer.Status == PromptStatus.Cancel) return;
                if (answer.Status == PromptStatus.OK && answer.StringResult == "Selected")
                    choice = document.Editor.GetSelection(new PromptSelectionOptions
                    {
                        MessageForAdding = "\nSelect Water gravity pipes and structures: ",
                        AllowDuplicates = false,
                        RejectObjectsFromNonCurrentSpace = true
                    });
            }
            if (choice.Status == PromptStatus.OK && choice.Value != null &&
                (preselected || choice.Value.Count > 0))
            {
                var requested = new HashSet<ObjectId>(choice.Value.GetObjectIds());
                sources = sources.Where(requested.Contains).ToList();
                document.Editor.SetImpliedSelection(new ObjectId[0]);
            }
            if (sources.Count == 0) return;

            var viewScope = new PromptKeywordOptions(
                "\nDestination Water profile views [All/Selected] <All>: ");
            viewScope.Keywords.Add("All");
            viewScope.Keywords.Add("Selected");
            viewScope.AllowNone = true;
            PromptResult viewAnswer = document.Editor.GetKeywords(viewScope);
            if (viewAnswer.Status == PromptStatus.Cancel) return;
            HashSet<ObjectId> selectedViews = null;
            if (viewAnswer.Status == PromptStatus.OK && viewAnswer.StringResult == "Selected")
            {
                PromptSelectionResult viewSelection = document.Editor.GetSelection(new PromptSelectionOptions
                {
                    MessageForAdding = "\nSelect matching Water profile views: ",
                    AllowDuplicates = false, RejectObjectsFromNonCurrentSpace = true
                });
                if (viewSelection.Status != PromptStatus.OK || viewSelection.Value == null) return;
                selectedViews = new HashSet<ObjectId>(viewSelection.Value.GetObjectIds());
            }
            var views = new List<ObjectId>();
            using (Transaction tr = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId alignmentId in civil.GetAlignmentIds())
                {
                    var alignment = tr.GetObject(alignmentId, OpenMode.ForRead, false) as CivilAlignment;
                    if (alignment == null || alignment.IsReferenceObject ||
                        !(alignment.Name.IndexOf("Water", StringComparison.OrdinalIgnoreCase) >= 0 ||
                          alignment.Name.StartsWith("W-", StringComparison.OrdinalIgnoreCase))) continue;
                    foreach (ObjectId id in alignment.GetProfileViewIds())
                        if (selectedViews == null || selectedViews.Contains(id)) views.Add(id);
                }
            }
            if (views.Count == 0)
            {
                document.Editor.WriteMessage("\nNo eligible Water profile views were found.");
                return;
            }

            int added = 0, skipped = 0, unmatched = 0;
            using (DocumentLock locked = document.LockDocument())
            foreach (ObjectId partId in sources.Distinct())
            foreach (ObjectId viewId in views.Distinct())
            {
                bool match = false;
                using (Transaction read = document.Database.TransactionManager.StartTransaction())
                {
                    var view = read.GetObject(viewId, OpenMode.ForRead, false) as ProfileView;
                    DBObject part = read.GetObject(partId, OpenMode.ForRead, false);
                    if (view == null || part == null) continue;
                    var alignment = read.GetObject(view.AlignmentId, OpenMode.ForRead, false) as CivilAlignment;
                    if (alignment == null) continue;
                    var pipe = part as CivilPipe;
                    if (pipe != null)
                    {
                        if (!pipe.RefAlignmentId.IsNull)
                            match = pipe.RefAlignmentId == view.AlignmentId;
                        else match = OnAlignment(alignment, pipe.StartPoint) &&
                            OnAlignment(alignment, pipe.EndPoint);
                    }
                    var structure = part as CivilStructure;
                    if (structure != null)
                        match = OnAlignment(alignment, structure.Position);
                }
                if (!match) { unmatched++; continue; }
                try
                {
                    using (Transaction edit = document.Database.TransactionManager.StartTransaction())
                    {
                        DBObject part = edit.GetObject(partId, OpenMode.ForWrite, false);
                        MethodInfo add = part.GetType().GetMethod("AddToProfileView",
                            new[] { typeof(ObjectId) });
                        if (add == null) { skipped++; continue; }
                        add.Invoke(part, new object[] { viewId });
                        edit.Commit();
                        added++;
                    }
                }
                catch (System.Exception) { skipped++; }
            }
            document.Editor.Regen();
            System.Windows.Forms.MessageBox.Show(
                "Water parts considered: " + sources.Count +
                "\nWater profile views: " + views.Count +
                "\nAdded to views: " + added +
                "\nUnmatched branches: " + unmatched +
                "\nAlready displayed or skipped: " + skipped +
                "\n\nNo sewer views or pipe elevations were modified.",
                "CE Tools - Water Gravity Parts to Profiles");
        }

        private static bool OnAlignment(CivilAlignment alignment,
            Autodesk.AutoCAD.Geometry.Point3d point)
        {
            try
            {
                double station = 0, offset = 0;
                alignment.StationOffset(point.X, point.Y, ref station, ref offset);
                return station >= alignment.StartingStation - 0.01 &&
                    station <= alignment.EndingStation + 0.01 &&
                    Math.Abs(offset) <= 1.0;
            }
            catch (System.Exception) { return false; }
        }
    }
}

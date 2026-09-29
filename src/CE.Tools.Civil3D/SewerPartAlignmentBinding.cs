using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivilPart = Autodesk.Civil.DatabaseServices.Part;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.SewerPartAlignmentBinding))]

namespace CETools.Civil3D
{
    public sealed class SewerPartAlignmentBinding
    {
        internal static void BindBranch(Transaction transaction, IEnumerable<ObjectId> pipeIds,
            IEnumerable<ObjectId> structureIds, ObjectId alignmentId)
        {
            foreach (ObjectId id in pipeIds.Distinct()) SetReference(transaction, id, alignmentId, false);
            // A shared junction structure can belong to several branches. Keep
            // its existing reference instead of overwriting it on every branch.
            foreach (ObjectId id in structureIds.Distinct()) SetReference(transaction, id, alignmentId, true);
        }

        private static bool SetReference(Transaction transaction, ObjectId id, ObjectId alignmentId, bool onlyIfBlank)
        {
            var part = transaction.GetObject(id, OpenMode.ForRead, false) as CivilPart;
            if (part == null || part.IsReferenceObject) return false;
            if (onlyIfBlank && !part.RefAlignmentId.IsNull && !part.RefAlignmentId.IsErased) return false;
            if (!part.IsWriteEnabled) part.UpgradeOpen();
            part.RefAlignmentId = alignmentId;
            if (part.RefAlignmentId != alignmentId) throw new InvalidOperationException("The part reference alignment did not persist.");
            return true;
        }

        [CommandMethod("CE_TOOLS", "CE_PIPEALIGNMENTMULTI", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void AssignReferenceAlignment()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civil = CivilApplication.ActiveDocument;
            if (document == null || civil == null) return;
            PromptSelectionResult selection = document.Editor.SelectImplied();
            if (selection.Status != PromptStatus.OK || selection.Value == null || selection.Value.Count == 0)
                selection = document.Editor.GetSelection(new PromptSelectionOptions
                { MessageForAdding = "\nSelect pipes and structures for one reference alignment: ", AllowDuplicates = false });
            document.Editor.SetImpliedSelection(new ObjectId[0]);
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;
            var choices = new List<CivilChoice>();
            using (Transaction tr = document.Database.TransactionManager.StartTransaction())
                foreach (ObjectId id in civil.GetAlignmentIds())
                {
                    var alignment = tr.GetObject(id, OpenMode.ForRead, false) as CivilAlignment;
                    if (alignment != null) choices.Add(new CivilChoice(id, alignment.Name + " [" + id.Handle + "]"));
                }
            if (choices.Count == 0) { document.Editor.WriteMessage("\nCreate the branch alignment first."); return; }
            var settings = new ProductionSettingsDialogModel("CE Tools - Pipe Reference Alignment",
                "Assign the selected branch alignment to all selected pipes and structures. Pipe geometry and surface references are retained.");
            settings.AddChoice("Alignment", "Reference", "Reference alignment", choices[0].Name,
                "Choose the branch alignment these selected parts belong to.", choices.Select(item => item.Name));
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;
            CivilChoice choice = choices.FirstOrDefault(item => item.Name == settings.Text("Alignment"));
            if (choice == null) return;
            int changed = 0;
            using (Transaction tr = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in selection.Value.GetObjectIds().Distinct())
                    if (SetReference(tr, id, choice.Id, false)) changed++;
                tr.Commit();
            }
            document.Editor.Regen();
            document.Editor.WriteMessage("\nReference alignment assigned and verified on {0} selected parts.", changed);
        }
    }
}

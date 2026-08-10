using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.AnnotationLeaderPlacementCommands))]

namespace CETools.Civil3D
{
    public sealed class AnnotationLeaderPlacementCommands
    {
        private const string OriginalKey = "CE_OVERLAP_ORIGINAL";

        [CommandMethod("CE_TOOLS", "CE_MLEADERTEXTABOVE", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void MoveTextAboveLeaders()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            var model = new ProductionSettingsDialogModel(
                "CE Tools - MLeader Text Above Leader",
                "Keep MLeader arrow/reference vertices fixed and move only the MText content above the leader tail. Original text positions are stored so CE_ANNOTATIONRESTORE can return them.");
            model.AddChoice("Scope", "01 Selection", "MLeaders", "Selected", "Process selected MLeaders or every MLeader in current space.", new[] { "Selected", "All" });
            model.AddPositiveDouble("Offset", "02 Placement", "Minimum text offset above leader (paper mm)", 1.5, "Minimum vertical paper-space gap between the leader tail and MText location.");
            model.AddChoice("Mask", "03 Presentation", "Background mask", "Keep current", "Optionally switch the MText background mask on or off.", new[] { "Keep current", "On", "Off" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            HashSet<ObjectId> selected = null;
            if (string.Equals(model.Text("Scope"), "Selected", StringComparison.OrdinalIgnoreCase))
            {
                PromptSelectionResult result = document.Editor.SelectImplied();
                if (result.Status != PromptStatus.OK || result.Value == null || result.Value.Count == 0)
                    result = document.Editor.GetSelection(new PromptSelectionOptions { MessageForAdding = "\nSelect MLeaders: ", AllowDuplicates = false, RejectObjectsFromNonCurrentSpace = true });
                if (result.Status != PromptStatus.OK || result.Value == null) return;
                selected = new HashSet<ObjectId>(result.Value.GetObjectIds());
            }

            double offset = Math.Max(PaperAnnotationScale.ModelDistance(document.Database, model.Double("Offset", 1.5)), 0.001);
            int changed = 0;
            int skipped = 0;
            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = transaction.GetObject(document.Database.CurrentSpaceId, OpenMode.ForRead, false) as BlockTableRecord;
                if (space == null) return;
                foreach (ObjectId id in space)
                {
                    if (selected != null && !selected.Contains(id)) continue;
                    MLeader leader;
                    try { leader = transaction.GetObject(id, OpenMode.ForWrite, false) as MLeader; }
                    catch { skipped++; continue; }
                    if (leader == null) continue;
                    Point3d tail;
                    if (!TryGetTail(leader, out tail)) { skipped++; continue; }
                    Point3d current;
                    try { current = leader.TextLocation; }
                    catch { skipped++; continue; }
                    SaveOriginal(leader, transaction, current);
                    double targetY = tail.Y + offset;
                    Point3d target = current.Y < targetY
                        ? new Point3d(current.X, targetY, current.Z)
                        : current;
                    try
                    {
                        if (target.DistanceTo(current) > 1e-9)
                        {
                            leader.TextLocation = target;
                            changed++;
                        }
                        ApplyMask(leader, model.Text("Mask"));
                    }
                    catch { skipped++; }
                }
                transaction.Commit();
            }
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_MLEADERTEXTABOVE complete. Text positions changed={0}; skipped={1}; leader/reference vertices unchanged.", changed, skipped);
        }

        private static bool TryGetTail(MLeader leader, out Point3d tail)
        {
            tail = Point3d.Origin;
            try
            {
                ArrayList leaders = leader.GetLeaderIndexes();
                if (leaders == null || leaders.Count == 0) return false;
                foreach (object leaderIndexValue in leaders)
                {
                    int leaderIndex = Convert.ToInt32(leaderIndexValue, CultureInfo.InvariantCulture);
                    ArrayList lines = leader.GetLeaderLineIndexes(leaderIndex);
                    if (lines == null || lines.Count == 0) continue;
                    int lineIndex = Convert.ToInt32(lines[0], CultureInfo.InvariantCulture);
                    tail = leader.GetLastVertex(lineIndex);
                    return true;
                }
            }
            catch { }
            return false;
        }

        private static void ApplyMask(MLeader leader, string mode)
        {
            if (leader == null || string.Equals(mode, "Keep current", StringComparison.OrdinalIgnoreCase)) return;
            bool enabled = string.Equals(mode, "On", StringComparison.OrdinalIgnoreCase);
            try
            {
                MText text = leader.MText;
                if (text == null) return;
                text.BackgroundFill = enabled;
                text.UseBackgroundColor = enabled;
                leader.MText = text;
            }
            catch { }
        }

        private static void SaveOriginal(Entity entity, Transaction transaction, Point3d point)
        {
            if (entity.ExtensionDictionary.IsNull) entity.CreateExtensionDictionary();
            DBDictionary dictionary = transaction.GetObject(entity.ExtensionDictionary, OpenMode.ForWrite, false) as DBDictionary;
            if (dictionary == null || dictionary.Contains(OriginalKey)) return;
            var record = new Xrecord
            {
                Data = new ResultBuffer(
                    new TypedValue((int)DxfCode.Real, point.X),
                    new TypedValue((int)DxfCode.Real, point.Y),
                    new TypedValue((int)DxfCode.Real, point.Z))
            };
            dictionary.SetAt(OriginalKey, record);
            transaction.AddNewlyCreatedDBObject(record, true);
        }
    }
}

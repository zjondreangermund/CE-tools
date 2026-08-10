using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.BranchLabelLayerCommands))]

namespace CETools.Civil3D
{
    public sealed class BranchLabelLayerCommands
    {
        [CommandMethod("CE_TOOLS", "CE_BRANCHLABELLAYER", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void ApplyBranchLayer()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Branch Label Layer",
                "Move Sewer, Stormwater and Water branch names/labels onto one dedicated annotation layer without changing label text, source alignments or network geometry.");
            model.AddChoice("Scope", "01 Selection", "Branch labels", "All detected", "Apply to all detected BRANCH labels or selected label objects only.", new[] { "All detected", "Selected" });
            model.AddText("Layer", "02 Output", "Branch label layer", "CE-BRANCH-LABELS", "Dedicated layer for branch names/labels across utility disciplines.");
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            HashSet<ObjectId> selected = null;
            if (string.Equals(model.Text("Scope"), "Selected", StringComparison.OrdinalIgnoreCase))
            {
                PromptSelectionResult result = document.Editor.GetSelection(new PromptSelectionOptions { MessageForAdding = "\nSelect branch label objects: ", AllowDuplicates = false });
                if (result.Status != PromptStatus.OK || result.Value == null) return;
                selected = new HashSet<ObjectId>(result.Value.GetObjectIds());
            }
            int changed = BranchLabelLayerRuntime.Apply(document, selected, model.Text("Layer"));
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_BRANCHLABELLAYER complete. Branch labels on dedicated layer={0}.", changed);
        }
    }

    internal static class BranchLabelLayerRuntime
    {
        internal static int Apply(Document document, ISet<ObjectId> restricted = null, string requestedLayer = "CE-BRANCH-LABELS")
        {
            if (document == null) return 0;
            string layerName = string.IsNullOrWhiteSpace(requestedLayer) ? "CE-BRANCH-LABELS" : requestedLayer.Trim();
            int changed = 0;
            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                ObjectId layerId = EnsureLayer(document.Database, transaction, layerName);
                BlockTableRecord space = transaction.GetObject(document.Database.CurrentSpaceId, OpenMode.ForRead, false) as BlockTableRecord;
                if (space == null) return 0;
                foreach (ObjectId id in space)
                {
                    if (restricted != null && !restricted.Contains(id)) continue;
                    Entity entity;
                    try { entity = transaction.GetObject(id, OpenMode.ForWrite, false) as Entity; }
                    catch { continue; }
                    if (entity == null || !LooksLikeBranchLabel(entity)) continue;
                    if (entity.LayerId != layerId)
                    {
                        entity.LayerId = layerId;
                        changed++;
                    }
                }
                transaction.Commit();
            }
            return changed;
        }

        private static bool LooksLikeBranchLabel(Entity entity)
        {
            if (entity == null) return false;
            if ((entity.Layer ?? string.Empty).IndexOf("BRANCH", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            MText mtext = entity as MText;
            if (mtext != null) return ContainsBranch(mtext.Contents);
            DBText text = entity as DBText;
            if (text != null) return ContainsBranch(text.TextString);
            MLeader leader = entity as MLeader;
            if (leader != null)
            {
                try { return leader.MText != null && ContainsBranch(leader.MText.Contents); }
                catch { }
            }
            string typeName = entity.GetType().Name;
            if (typeName.IndexOf("Label", StringComparison.OrdinalIgnoreCase) < 0) return false;
            foreach (string propertyName in new[] { "Text", "LabelText", "Name", "Description", "Contents" })
            {
                try
                {
                    PropertyInfo property = entity.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                    if (property == null || !property.CanRead || property.PropertyType != typeof(string)) continue;
                    if (ContainsBranch(Convert.ToString(property.GetValue(entity, null), CultureInfo.CurrentCulture))) return true;
                }
                catch { }
            }
            return false;
        }

        private static bool ContainsBranch(string value)
        {
            return !string.IsNullOrWhiteSpace(value) && value.IndexOf("BRANCH", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static ObjectId EnsureLayer(Database database, Transaction transaction, string name)
        {
            LayerTable table = transaction.GetObject(database.LayerTableId, OpenMode.ForRead, false) as LayerTable;
            if (table == null) return ObjectId.Null;
            if (table.Has(name)) return table[name];
            table.UpgradeOpen();
            var layer = new LayerTableRecord { Name = name };
            ObjectId id = table.Add(layer);
            transaction.AddNewlyCreatedDBObject(layer, true);
            return id;
        }
    }
}

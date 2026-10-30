using System;
using System.Linq;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.BranchViewportScaleCommands))]
namespace CETools.Civil3D
{
    public sealed class BranchViewportScaleCommands
    {
        [CommandMethod("CE_TOOLS", "CE_NAMESVIEWPORTSCALES", CommandFlags.Modal | CommandFlags.Redraw)]
        public void Update()
        {
            var doc = AcApplication.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            int count = 0;
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var scales = AnnotationScaleSyncManager.ReadUsedDrawingScaleContexts(doc.Database, tr);
                var model = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(doc.Database), OpenMode.ForRead);
                foreach (ObjectId id in model)
                {
                    var text = tr.GetObject(id, OpenMode.ForRead) as MText;
                    if (text == null || !IsName(text)) continue;
                    text.UpgradeOpen(); PaperAnnotationScale.SetAnnotative(text);
                    AnnotationScaleSyncManager.AddUsedDrawingScaleContexts(text, scales); count++;
                }
                tr.Commit();
            }
            doc.Editor.Regen(); doc.Editor.WriteMessage("\nRoad/Sewer/Water names assigned current and used viewport scales: {0}.", count);
        }
        [CommandMethod("CE_TOOLS", "CE_NAMESCALEROTATION", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void RepairRotation()
        {
            var doc = AcApplication.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            doc.Editor.WriteMessage("\nUse the annotation scale showing the CORRECT name angle and position. This copies that representation to all scales of selected CE names.");
            var selection = doc.Editor.GetSelection(new PromptSelectionOptions {
                MessageForAdding = "\nSelect CE road/branch names to synchronize: ", AllowDuplicates = false
            }, new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, "MTEXT") }));
            if (selection.Status != PromptStatus.OK) return;
            int changed = 0, skipped = 0;
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var scales = AnnotationScaleSyncManager.ReadUsedDrawingScaleContexts(doc.Database, tr);
                foreach (ObjectId id in selection.Value.GetObjectIds())
                {
                    var text = tr.GetObject(id, OpenMode.ForRead) as MText;
                    if (text == null || !IsName(text)) { skipped++; continue; }
                    text.UpgradeOpen();
                    PaperAnnotationScale.SetAnnotative(text);
                    NameAnnotationScaleOrientation.Synchronize(text, scales);
                    changed++;
                }
                tr.Commit();
            }
            doc.Editor.Regen();
            doc.Editor.WriteMessage("\nName scale representations synchronized: {0}; unrelated objects skipped: {1}.", changed, skipped);
        }

        internal static bool IsName(MText text)
        {
            foreach (string app in new[] { "CE_TOOLS_SEWALIGN", "CE_WATER_BRANCH_LABEL", "CE_TOOLS_WATER", "CE_ROAD_NAME_LINK", "CE_ROAD_NAME_ANNOTATION" })
                using (var data = text.GetXDataForApplication(app)) if (data != null) return true;
            // Road annotation links also live in extension dictionaries.
            if (!text.ExtensionDictionary.IsNull)
            {
                var tr = text.Database.TransactionManager.TopTransaction;
                var dictionary = tr.GetObject(text.ExtensionDictionary, OpenMode.ForRead) as DBDictionary;
                if (dictionary != null && dictionary.Contains("CE_ROAD_NAME_LINK")) return true;
                if (dictionary != null && dictionary.Contains("CE_ROAD_LAYOUT"))
                {
                    var record = tr.GetObject(dictionary.GetAt("CE_ROAD_LAYOUT"), OpenMode.ForRead) as Xrecord;
                    var values = record?.Data?.AsArray();
                    if (values != null && values.Length > 0 && string.Equals(Convert.ToString(values[0].Value), "ROAD_NAME", StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            return false;
        }
    }
}

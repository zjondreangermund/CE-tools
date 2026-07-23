using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.ColourCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Drawing cleanup commands for applying standard object colours.
    /// </summary>
    public sealed class ColourCommands
    {
        private const short TargetColourIndex = 250;

        [CommandMethod(
            "CE_TOOLS",
            "CE_COLOR250",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        [CommandMethod(
            "COLOR250",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void SetColour250()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null)
            {
                return;
            }

            Editor editor = document.Editor;
            Database database = document.Database;

            PromptSelectionResult selection = editor.SelectImplied();
            if (selection.Status != PromptStatus.OK || selection.Value.Count == 0)
            {
                selection = editor.GetSelection(
                    new PromptSelectionOptions
                    {
                        MessageForAdding = "\nSelect objects to change to colour 250: ",
                        AllowDuplicates = false,
                        RejectObjectsFromNonCurrentSpace = true
                    });
            }

            if (selection.Status != PromptStatus.OK)
            {
                return;
            }

            int changed = 0;
            int alreadyColour250 = 0;
            int skippedLockedLayer = 0;
            int skippedUnsupported = 0;

            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                LayerTable layerTable = (LayerTable)transaction.GetObject(
                    database.LayerTableId,
                    OpenMode.ForRead,
                    false);

                foreach (SelectedObject selectedObject in selection.Value)
                {
                    if (selectedObject == null || selectedObject.ObjectId.IsNull)
                    {
                        skippedUnsupported++;
                        continue;
                    }

                    Entity entity = transaction.GetObject(
                        selectedObject.ObjectId,
                        OpenMode.ForRead,
                        false) as Entity;

                    if (entity == null)
                    {
                        skippedUnsupported++;
                        continue;
                    }

                    LayerTableRecord layer = transaction.GetObject(
                        entity.LayerId,
                        OpenMode.ForRead,
                        false) as LayerTableRecord;

                    if (layer != null && layer.IsLocked)
                    {
                        skippedLockedLayer++;
                        continue;
                    }

                    if (entity.ColorIndex == TargetColourIndex)
                    {
                        alreadyColour250++;
                        continue;
                    }

                    entity.UpgradeOpen();
                    entity.ColorIndex = TargetColourIndex;
                    changed++;
                }

                transaction.Commit();
            }

            editor.SetImpliedSelection(System.Array.Empty<ObjectId>());
            editor.WriteMessage(
                $"\nCE_COLOR250 complete. Changed: {changed}; " +
                $"already 250: {alreadyColour250}; " +
                $"locked-layer skips: {skippedLockedLayer}; " +
                $"unsupported skips: {skippedUnsupported}.");
        }
    }
}

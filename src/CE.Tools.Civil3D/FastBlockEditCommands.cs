using System;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.FastBlockEditCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Opens ordinary blocks in AutoCAD's normal Block Editor rather than the much
    /// heavier in-place reference editor. XREFs are routed to XOPEN. The selected
    /// source reference is not modified before AutoCAD opens the appropriate editor.
    /// </summary>
    public sealed class FastBlockEditCommands
    {
        [CommandMethod(
            "CE_TOOLS",
            "CE_BLOCKEDITFAST",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void EditBlockFast()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            PromptEntityResult result = PromptForBlock(document.Editor);
            if (result.Status != PromptStatus.OK) return;

            string blockName;
            bool isExternalReference;
            try
            {
                ReadBlockIdentity(
                    document.Database,
                    result.ObjectId,
                    out blockName,
                    out isExternalReference);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_BLOCKEDITFAST stopped. {0}",
                    exception.Message);
                return;
            }

            if (string.IsNullOrWhiteSpace(blockName))
            {
                document.Editor.WriteMessage(
                    "\nCE_BLOCKEDITFAST stopped. The selected reference has no editable block name.");
                return;
            }

            if (isExternalReference)
            {
                document.Editor.SetImpliedSelection(new[] { result.ObjectId });
                document.Editor.WriteMessage(
                    "\nCE_BLOCKEDITFAST: '{0}' is an XREF. Opening its source drawing with XOPEN instead of REFEDIT.",
                    blockName);
                document.SendStringToExecute("_.XOPEN ", true, false, true);
                return;
            }

            document.Editor.WriteMessage(
                "\nCE_BLOCKEDITFAST: opening block '{0}' in the normal Block Editor. REFEDIT is not used.",
                blockName);
            document.SendStringToExecute(
                "_.BEDIT " + QuoteCommandArgument(blockName) + " ",
                true,
                false,
                true);
        }

        private static PromptEntityResult PromptForBlock(Editor editor)
        {
            PromptSelectionResult implied = editor.SelectImplied();
            if (implied.Status == PromptStatus.OK &&
                implied.Value != null &&
                implied.Value.Count == 1)
            {
                ObjectId id = implied.Value.GetObjectIds()[0];
                using (Transaction transaction =
                    id.Database.TransactionManager.StartTransaction())
                {
                    if (transaction.GetObject(id, OpenMode.ForRead, false) is BlockReference)
                        return new PromptEntityResult(id, Point3d.Origin);
                }
            }

            var options = new PromptEntityOptions(
                "\nSelect a block reference to open in the normal Block Editor: ");
            options.SetRejectMessage("\nSelect an ordinary block or XREF reference.");
            options.AddAllowedClass(typeof(BlockReference), false);
            return editor.GetEntity(options);
        }

        private static void ReadBlockIdentity(
            Database database,
            ObjectId referenceId,
            out string blockName,
            out bool isExternalReference)
        {
            blockName = string.Empty;
            isExternalReference = false;

            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                BlockReference reference = transaction.GetObject(
                    referenceId,
                    OpenMode.ForRead,
                    false) as BlockReference;
                if (reference == null)
                    throw new InvalidOperationException(
                        "The selected object is not a block reference.");

                ObjectId definitionId = reference.BlockTableRecord;
                if (reference.IsDynamicBlock &&
                    !reference.DynamicBlockTableRecord.IsNull)
                {
                    definitionId = reference.DynamicBlockTableRecord;
                }

                BlockTableRecord definition = transaction.GetObject(
                    definitionId,
                    OpenMode.ForRead,
                    false) as BlockTableRecord;
                if (definition == null)
                    throw new InvalidOperationException(
                        "The selected block definition could not be opened.");

                blockName = definition.Name;
                isExternalReference =
                    definition.IsFromExternalReference ||
                    definition.IsFromOverlayReference;
            }
        }

        private static string QuoteCommandArgument(string value)
        {
            string safe = (value ?? string.Empty).Replace("\"", "\"\"");
            return "\"" + safe + "\"";
        }
    }
}

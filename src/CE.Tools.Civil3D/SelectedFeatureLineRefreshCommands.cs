using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilFeatureLine = Autodesk.Civil.DatabaseServices.FeatureLine;

[assembly: CommandClass(typeof(CETools.Civil3D.SelectedFeatureLineRefreshCommands))]

namespace CETools.Civil3D
{
    public sealed class SelectedFeatureLineRefreshCommands
    {
        [CommandMethod("CE_TOOLS", "CE_FLANNOTREFRESHSELECTED", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void RefreshSelectedFeatureLines()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            PromptSelectionResult selection = document.Editor.SelectImplied();
            if (selection.Status != PromptStatus.OK || selection.Value == null || selection.Value.Count == 0)
                selection = document.Editor.GetSelection(new PromptSelectionOptions
                {
                    MessageForAdding = "\nSelect feature lines whose linked annotation/tables must refresh: ",
                    AllowDuplicates = false,
                    RejectObjectsFromNonCurrentSpace = true
                });
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;

            HashSet<ObjectId> sources = new HashSet<ObjectId>();
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in selection.Value.GetObjectIds())
                {
                    try
                    {
                        if (transaction.GetObject(id, OpenMode.ForRead, false) is CivilFeatureLine)
                            sources.Add(id);
                    }
                    catch { }
                }
            }
            if (sources.Count == 0)
            {
                document.Editor.WriteMessage("\nCE_FLANNOTREFRESHSELECTED: no Civil 3D feature lines were selected.");
                return;
            }

            List<ObjectId> linkedTables = FindTablesLinkedToSources(document.Database, sources);
            int refreshedTables = 0;
            int failedTables = 0;
            foreach (ObjectId tableId in linkedTables)
            {
                if (TryRefreshVertexTable(document, tableId)) refreshedTables++;
                else failedTables++;
            }

            // Rebuild only stepped-offset families rooted in the selected source
            // set by using the existing multi-source updater with the pickfirst set.
            try
            {
                document.Editor.SetImpliedSelection(sources.ToArray());
                document.SendStringToExecute("CE_FLRELUPDATEMULTI ", true, false, true);
            }
            catch { }

            RuntimeAnnotationLinkManager.ClampLinkedAnnotations(document, true);
            UniversalDynamicRefreshManager.Queue();
            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_FLANNOTREFRESHSELECTED complete. Selected feature lines={0}; linked vertex tables refreshed={1}; table failures={2}. Linked stepped-offset sets were queued for selected-source update.",
                sources.Count,
                refreshedTables,
                failedTables);
        }

        private static List<ObjectId> FindTablesLinkedToSources(Database database, ISet<ObjectId> sources)
        {
            var tableIds = new List<ObjectId>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(database), OpenMode.ForRead, false) as BlockTableRecord;
                if (space == null) return tableIds;
                foreach (ObjectId id in space)
                {
                    try
                    {
                        if (transaction.GetObject(id, OpenMode.ForRead, false) is Table)
                            tableIds.Add(id);
                    }
                    catch { }
                }
            }

            var result = new List<ObjectId>();
            foreach (ObjectId id in tableIds)
            {
                List<ObjectId> discovered = LinkedTableSourceNavigator.Discover(database, id);
                if (discovered.Any(source => sources.Contains(source))) result.Add(id);
            }
            return result;
        }

        private static bool TryRefreshVertexTable(Document document, ObjectId tableId)
        {
            try
            {
                MethodInfo method = typeof(VertexSettingOutCommands).GetMethod(
                    "RefreshTable",
                    BindingFlags.NonPublic | BindingFlags.Static,
                    null,
                    new[] { typeof(Document), typeof(ObjectId), typeof(int).MakeByRefType(), typeof(int).MakeByRefType() },
                    null);
                if (method == null) return false;
                object[] args = { document, tableId, 0, 0 };
                method.Invoke(null, args);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}

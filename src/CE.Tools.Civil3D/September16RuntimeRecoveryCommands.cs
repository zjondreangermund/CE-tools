using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using Autodesk.Civil.DatabaseServices.Styles;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

[assembly: CommandClass(typeof(CETools.Civil3D.September16RuntimeRecoveryCommands))]

namespace CETools.Civil3D
{
    public sealed class September16RuntimeRecoveryCommands
    {
        [CommandMethod("CE_TOOLS", "CE_ALIGNREVERSEMULTI", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void ReverseMultipleAlignments()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            PromptSelectionResult selection = Selection(document.Editor, "\nSelect alignments to reverse: ");
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;

            int reversed = 0;
            int failed = 0;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in selection.Value.GetObjectIds().Distinct())
                {
                    CivilAlignment alignment = transaction.GetObject(id, OpenMode.ForWrite, false) as CivilAlignment;
                    if (alignment == null || alignment.IsReferenceObject) { failed++; continue; }
                    MethodInfo reverse = alignment.GetType().GetMethod("Reverse", Type.EmptyTypes);
                    try
                    {
                        if (reverse == null) throw new MissingMethodException("Alignment.Reverse");
                        reverse.Invoke(alignment, null);
                        reversed++;
                    }
                    catch { failed++; }
                }
                transaction.Commit();
            }
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_ALIGNREVERSEMULTI complete. Reversed={0}; failed/skipped={1}.", reversed, failed);
        }

        [CommandMethod("CE_TOOLS", "CE_SURFACESTYLEMULTI", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void ChangeMultipleSurfaceStyles()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (document == null || civilDocument == null) return;

            var choices = new List<StyleChoice>();
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                SurfaceStyleCollection styles = civilDocument.Styles.SurfaceStyles;
                for (int index = 0; index < styles.Count; index++)
                {
                    ObjectId id = styles[index];
                    SurfaceStyle style = transaction.GetObject(id, OpenMode.ForRead, false) as SurfaceStyle;
                    if (style != null) choices.Add(new StyleChoice(style.Name, id));
                }
            }
            if (choices.Count == 0) return;
            choices.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name));

            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Multiple Surface Styles",
                "Apply one existing Civil 3D surface style to every selected editable surface.");
            settings.AddChoice("Style", "01 Style", "Surface style", choices[0].Name,
                "Choose a style from the active drawing.", choices.Select(item => item.Name).ToArray());
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;
            StyleChoice choice = choices.FirstOrDefault(item => string.Equals(item.Name, settings.Text("Style"), StringComparison.CurrentCultureIgnoreCase));
            if (choice == null) return;

            PromptSelectionResult selection = Selection(document.Editor, "\nSelect multiple Civil 3D surfaces: ");
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;
            int changed = 0;
            int skipped = 0;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in selection.Value.GetObjectIds().Distinct())
                {
                    CivilSurface surface = transaction.GetObject(id, OpenMode.ForWrite, false) as CivilSurface;
                    if (surface == null || surface.IsReferenceObject) { skipped++; continue; }
                    surface.StyleId = choice.Id;
                    try { surface.Rebuild(); } catch { }
                    changed++;
                }
                transaction.Commit();
            }
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_SURFACESTYLEMULTI complete. Style='{0}'; changed={1}; skipped={2}.", choice.Name, changed, skipped);
        }

        [CommandMethod("CE_TOOLS", "CE_ASSEMBLYCOPYSAFE", CommandFlags.Modal | CommandFlags.Redraw)]
        public void CopyAssemblyWithoutClipboard()
        {
            Document target = AcApplication.DocumentManager.MdiActiveDocument;
            if (target == null) return;
            List<Document> sources = AcApplication.DocumentManager.Cast<Document>()
                .Where(item => !ReferenceEquals(item, target)).ToList();
            if (sources.Count == 0)
            {
                target.Editor.WriteMessage("\nCE_ASSEMBLYCOPYSAFE requires the source and destination drawings to be open. Run it in the destination drawing.");
                return;
            }

            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Safe Assembly Copy",
                "Clone an assembly through a detached side database instead of clipboard copy/paste or a live cross-document clone. This keeps both open Civil 3D documents out of the native deep-clone lock path that can freeze the application.");
            settings.AddChoice("Drawing", "01 Source", "Source drawing", sources[0].Name,
                "Open source drawing containing the assembly. Save that drawing before copying so the detached reader receives the current assembly.",
                sources.Select(item => item.Name).ToArray());
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;
            Document source = sources.FirstOrDefault(item => string.Equals(
                item.Name,
                settings.Text("Drawing"),
                StringComparison.CurrentCultureIgnoreCase));
            if (source == null) return;

            List<NamedId> assemblies = ReadAssemblies(source);
            if (assemblies.Count == 0)
            {
                target.Editor.WriteMessage("\nNo Civil 3D assemblies were found in the selected source drawing.");
                return;
            }

            var assemblySettings = new ProductionSettingsDialogModel(
                "CE Tools - Safe Assembly Copy",
                "Select the assembly to clone from the saved source DWG through a detached side database.");
            assemblySettings.AddChoice("Assembly", "01 Source", "Assembly", assemblies[0].Name,
                "Assembly from the source drawing.", assemblies.Select(item => item.Name).ToArray());
            if (!DisciplineWorkflowDialogs.EditSettings(assemblySettings)) return;
            NamedId assembly = assemblies.FirstOrDefault(item => string.Equals(
                item.Name,
                assemblySettings.Text("Assembly"),
                StringComparison.CurrentCultureIgnoreCase));
            if (assembly == null) return;

            string sourceFile = source.Database.Filename;
            if (string.IsNullOrWhiteSpace(sourceFile))
            {
                target.Editor.WriteMessage(
                    "\nCE_ASSEMBLYCOPYSAFE stopped safely. Save the source drawing first; the detached side-database copy does not read an unsaved live document.");
                return;
            }

            Handle assemblyHandle = assembly.Id.Handle;
            try
            {
                // Do not lock or clone from the live source Document. Civil 3D's
                // deep-clone dependency graph can re-enter the UI/document manager
                // and freeze when two live drawing databases are involved.
                using (var detached = new Database(false, true))
                {
                    detached.ReadDwgFile(
                        sourceFile,
                        FileOpenMode.OpenForReadAndAllShare,
                        true,
                        null);
                    detached.CloseInput(true);

                    ObjectId detachedAssemblyId = detached.GetObjectId(
                        false,
                        assemblyHandle,
                        0);
                    if (detachedAssemblyId.IsNull || detachedAssemblyId.IsErased)
                        throw new InvalidOperationException(
                            "The selected assembly was not found in the saved source DWG. Save the source drawing and retry.");

                    Database staging = detached.Wblock(
                        new ObjectIdCollection { detachedAssemblyId },
                        Point3d.Origin);
                    using (staging)
                    {
                        ObjectIdCollection stagedAssemblies = ReadAssemblyIds(staging);
                        if (stagedAssemblies.Count == 0)
                            throw new InvalidOperationException(
                                "The detached source assembly could not be staged safely.");

                        using (DocumentLock targetLock = target.LockDocument())
                        {
                            var mapping = new IdMapping();
                            staging.WblockCloneObjects(
                                stagedAssemblies,
                                target.Database.CurrentSpaceId,
                                mapping,
                                DuplicateRecordCloning.Ignore,
                                false);
                        }
                    }
                }

                target.Editor.Regen();
                target.Editor.WriteMessage(
                    "\nCE_ASSEMBLYCOPYSAFE complete. Assembly '{0}' cloned through a detached side database; no clipboard or live source-database clone was used.",
                    assembly.Name);
            }
            catch (System.Exception exception)
            {
                target.Editor.WriteMessage(
                    "\nCE_ASSEMBLYCOPYSAFE stopped safely without a live cross-document clone: {0}",
                    exception.Message);
            }
        }

        private static List<NamedId> ReadAssemblies(Document document)
        {
            var result = new List<NamedId>();
            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = transaction.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(document.Database),
                    OpenMode.ForRead,
                    false) as BlockTableRecord;
                if (space == null) return result;
                foreach (ObjectId id in space)
                {
                    DBObject value = transaction.GetObject(id, OpenMode.ForRead, false);
                    if (value == null || value.GetType().Name.IndexOf("Assembly", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    PropertyInfo name = value.GetType().GetProperty("Name");
                    string text = name == null ? id.Handle.ToString() : Convert.ToString(name.GetValue(value, null));
                    result.Add(new NamedId(string.IsNullOrWhiteSpace(text) ? id.Handle.ToString() : text, id));
                }
            }
            return result.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        private static ObjectIdCollection ReadAssemblyIds(Database database)
        {
            var result = new ObjectIdCollection();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = transaction.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(database),
                    OpenMode.ForRead,
                    false) as BlockTableRecord;
                if (space == null) return result;
                foreach (ObjectId id in space)
                {
                    DBObject value = transaction.GetObject(id, OpenMode.ForRead, false);
                    if (value != null && value.GetType().Name.IndexOf(
                            "Assembly", StringComparison.OrdinalIgnoreCase) >= 0)
                        result.Add(id);
                }
            }
            return result;
        }

        private static PromptSelectionResult Selection(Editor editor, string message)
        {
            PromptSelectionResult implied = editor.SelectImplied();
            if (implied.Status == PromptStatus.OK && implied.Value != null && implied.Value.Count > 0)
            {
                editor.SetImpliedSelection(new ObjectId[0]);
                return implied;
            }
            return editor.GetSelection(new PromptSelectionOptions
            {
                MessageForAdding = message,
                AllowDuplicates = false,
                RejectObjectsFromNonCurrentSpace = true
            });
        }

        private sealed class StyleChoice
        {
            internal StyleChoice(string name, ObjectId id) { Name = name; Id = id; }
            internal string Name { get; private set; }
            internal ObjectId Id { get; private set; }
        }

        private sealed class NamedId
        {
            internal NamedId(string name, ObjectId id) { Name = name; Id = id; }
            internal string Name { get; private set; }
            internal ObjectId Id { get; private set; }
        }
    }
}

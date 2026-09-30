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
using Autodesk.Civil.DatabaseServices.Styles;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.SewerProfileLabelStyleBatchCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Restyles native gravity labels and creates missing profile labels for
    /// already drawn parts. Network parts and engineering rules are never
    /// opened for write.
    /// </summary>
    public sealed class SewerProfileLabelStyleBatchCommands
    {
        [CommandMethod("CE_TOOLS", "CE_SEWPROFILELABELSTYLESMULTI",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void ApplyToSelectedProfileViews()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civil = CivilApplication.ActiveDocument;
            if (document == null || civil == null) return;

            Editor editor = document.Editor;
            PromptSelectionResult selection = editor.SelectImplied();
            if (selection.Status == PromptStatus.OK && selection.Value != null && selection.Value.Count > 0)
                editor.SetImpliedSelection(new ObjectId[0]);
            else
                selection = editor.GetSelection(new PromptSelectionOptions
                {
                    MessageForAdding = "\nSelect the Civil 3D profile views whose pipe/structure profile label styles should change: ",
                    AllowDuplicates = false,
                    RejectObjectsFromNonCurrentSpace = true
                });
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;

            var views = new List<ObjectId>();
            using (Transaction tr = document.Database.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject selected in selection.Value)
                {
                    if (selected == null || selected.ObjectId.IsNull || selected.ObjectId.IsErased) continue;
                    try
                    {
                        ProfileView view = tr.GetObject(selected.ObjectId, OpenMode.ForRead, false) as ProfileView;
                        if (view != null && !view.IsReferenceObject) views.Add(selected.ObjectId);
                    }
                    catch { }
                }
            }
            views = views.Distinct().ToList();
            if (views.Count == 0)
            {
                editor.WriteMessage("\nCE_SEWPROFILELABELSTYLESMULTI: no editable Civil 3D profile views selected.");
                return;
            }

            object labelStyles = Property(civil.Styles, "LabelStyles");
            object pipeRoot = Property(labelStyles, "PipeLabelStyles");
            object structureRoot = Property(labelStyles, "StructureLabelStyles");
            List<NamedStyle> pipeStyles = ReadStyles(document.Database,
                Property(pipeRoot, "PlanProfileLabelStyles"));
            List<NamedStyle> structureStyles = ReadStyles(document.Database,
                Property(structureRoot, "LabelStyles"));
            if (pipeStyles.Count == 0 && structureStyles.Count == 0)
            {
                editor.WriteMessage("\nCE_SEWPROFILELABELSTYLESMULTI: no gravity pipe or structure profile label styles exist in this drawing.");
                return;
            }

            const string keep = "<Keep current style>";
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Pipe and Structure Profile Labels (Multiple Views)",
                "Apply the selected Civil 3D profile label styles to pipe and structure labels in " +
                views.Count + " selected profile view(s). Pipe slopes, inverts, cover, surfaces and rule sets are preserved.");
            model.AddChoice("PipeStyle", "01 Pipe profile labels", "Pipe profile label style",
                pipeStyles.Count == 0 ? keep : pipeStyles[0].Name,
                "Changes native Pipe Profile Label objects in the selected views only.",
                new[] { keep }.Concat(pipeStyles.Select(item => item.Name)).ToList());
            model.AddChoice("StructureStyle", "02 Structure profile labels", "Structure profile label style",
                structureStyles.Count == 0 ? keep : structureStyles[0].Name,
                "Changes native Structure Profile Label objects in the selected views only.",
                new[] { keep }.Concat(structureStyles.Select(item => item.Name)).ToList());
            model.AddChoice("Missing", "03 Missing labels", "Add missing pipe and structure labels", "Yes",
                "Creates native labels for gravity network parts already drawn in each selected profile view.",
                new[] { "Yes", "No" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            ObjectId pipeStyleId = Find(pipeStyles, model.Text("PipeStyle"));
            ObjectId structureStyleId = Find(structureStyles, model.Text("StructureStyle"));
            if (pipeStyleId.IsNull && structureStyleId.IsNull)
            {
                editor.WriteMessage("\nCE_SEWPROFILELABELSTYLESMULTI: no new label style selected.");
                return;
            }

            bool addMissing = string.Equals(model.Text("Missing"), "Yes", StringComparison.OrdinalIgnoreCase);
            int pipeCount = 0, structureCount = 0, pipeAdded = 0, structureAdded = 0, failed = 0;
            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction tr = document.Database.TransactionManager.StartTransaction())
            {
                List<ObjectId> drawnParts = addMissing
                    ? ReadDrawnProfileParts(document.Database, tr)
                    : new List<ObjectId>();
                foreach (ObjectId viewId in views)
                {
                    var pipeLabelsInView = new HashSet<ObjectId>(
                        AvailableLabels(viewId, true));
                    var structureLabelsInView = new HashSet<ObjectId>(
                        AvailableLabels(viewId, false));

                    if (!pipeStyleId.IsNull)
                    {
                        foreach (ObjectId labelId in pipeLabelsInView.ToArray())
                        {
                            try
                            {
                                PipeProfileLabel label = tr.GetObject(labelId, OpenMode.ForWrite, false) as PipeProfileLabel;
                                if (label == null || label.IsReferenceObject) continue;
                                label.StyleId = pipeStyleId;
                                pipeCount++;
                            }
                            catch { failed++; }
                        }
                    }
                    if (!structureStyleId.IsNull)
                    {
                        foreach (ObjectId labelId in structureLabelsInView.ToArray())
                        {
                            try
                            {
                                StructureProfileLabel label = tr.GetObject(labelId, OpenMode.ForWrite, false) as StructureProfileLabel;
                                if (label == null || label.IsReferenceObject) continue;
                                label.StyleId = structureStyleId;
                                structureCount++;
                            }
                            catch { failed++; }
                        }
                    }

                    foreach (ObjectId partId in drawnParts)
                    {
                        ProfileViewPart part = tr.GetObject(partId, OpenMode.ForRead, false) as ProfileViewPart;
                        if (part == null || part.ModelPartId.IsNull) continue;
                        DBObject modelPart;
                        try { modelPart = tr.GetObject(part.ModelPartId, OpenMode.ForRead, false); }
                        catch { continue; }

                        // A profile part can legitimately be drawn in several
                        // profile views. GetPartProfileLabelIds() returns labels
                        // across all of those views, so the old global check could
                        // suppress a missing label in this view merely because the
                        // same part was labelled somewhere else. Check only label
                        // ids Civil 3D reports for the current profile view.
                        if (modelPart is Pipe && !pipeStyleId.IsNull)
                        {
                            if (HasPartLabelInView(
                                    part,
                                    tr,
                                    true,
                                    pipeLabelsInView))
                                continue;
                            try
                            {
                                ObjectId created = PipeProfileLabel.Create(
                                    partId,
                                    viewId,
                                    0.5,
                                    pipeStyleId);
                                if (!created.IsNull)
                                {
                                    pipeLabelsInView.Add(created);
                                    pipeAdded++;
                                }
                            }
                            catch { failed++; }
                        }
                        else if (modelPart is Structure && !structureStyleId.IsNull)
                        {
                            if (HasPartLabelInView(
                                    part,
                                    tr,
                                    false,
                                    structureLabelsInView))
                                continue;
                            try
                            {
                                ObjectId created = StructureProfileLabel.Create(
                                    viewId,
                                    partId,
                                    structureStyleId);
                                if (!created.IsNull)
                                {
                                    structureLabelsInView.Add(created);
                                    structureAdded++;
                                }
                            }
                            catch { failed++; }
                        }
                    }
                }
                tr.Commit();
            }
            editor.Regen();
            editor.WriteMessage(
                "\nCE_SEWPROFILELABELSTYLESMULTI complete. Profile views={0}; pipe labels restyled={1}; structure labels restyled={2}; pipe labels added={3}; structure labels added={4}; failed={5}. Slopes and cover rules unchanged.{6}",
                views.Count, pipeCount, structureCount, pipeAdded, structureAdded, failed,
                pipeCount + structureCount + pipeAdded + structureAdded == 0
                    ? " No native profile parts were found in these views; draw the network parts in profile first."
                    : string.Empty);
        }

        private static List<ObjectId> ReadDrawnProfileParts(Database database, Transaction tr)
        {
            var result = new List<ObjectId>();
            BlockTableRecord model = tr.GetObject(
                SymbolUtilityServices.GetBlockModelSpaceId(database), OpenMode.ForRead, false) as BlockTableRecord;
            if (model == null) return result;
            foreach (ObjectId id in model)
            {
                try
                {
                    if (tr.GetObject(id, OpenMode.ForRead, false) is ProfileViewPart)
                        result.Add(id);
                }
                catch { }
            }
            return result;
        }

        private static bool HasPartLabelInView(
            ProfileViewPart part,
            Transaction tr,
            bool pipe,
            ISet<ObjectId> labelsInView)
        {
            if (part == null || labelsInView == null || labelsInView.Count == 0)
                return false;
            try
            {
                foreach (ObjectId id in part.GetPartProfileLabelIds())
                {
                    if (!labelsInView.Contains(id)) continue;
                    DBObject label = tr.GetObject(id, OpenMode.ForRead, false);
                    if (pipe ? label is PipeProfileLabel : label is StructureProfileLabel)
                        return true;
                }
            }
            catch { }
            return false;
        }

        private static object Property(object owner, string name)
        {
            if (owner == null) return null;
            PropertyInfo property = owner.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            return property == null ? null : property.GetValue(owner, null);
        }

        private static IEnumerable<ObjectId> AvailableLabels(ObjectId viewId, bool pipe)
        {
            try
            {
                return (pipe
                    ? PipeProfileLabel.GetAvailableLabelIds(viewId)
                    : StructureProfileLabel.GetAvailableLabelIds(viewId))
                    .Cast<ObjectId>().ToList();
            }
            catch
            {
                return Enumerable.Empty<ObjectId>();
            }
        }

        private static List<NamedStyle> ReadStyles(Database database, object collection)
        {
            var styles = new List<NamedStyle>();
            if (collection == null) return styles;
            using (Transaction tr = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in CivilStyleCatalogV2.ReadObjectIds(collection, tr))
                {
                    try
                    {
                        StyleBase style = tr.GetObject(id, OpenMode.ForRead, false) as StyleBase;
                        if (style != null && !string.IsNullOrWhiteSpace(style.Name))
                            styles.Add(new NamedStyle(id, style.Name));
                    }
                    catch { }
                }
            }
            return styles.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        private static ObjectId Find(IEnumerable<NamedStyle> styles, string name)
        {
            NamedStyle style = styles.FirstOrDefault(item =>
                string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
            return style == null ? ObjectId.Null : style.Id;
        }

        private sealed class NamedStyle
        {
            internal NamedStyle(ObjectId id, string name) { Id = id; Name = name; }
            internal readonly ObjectId Id;
            internal readonly string Name;
        }
    }
}

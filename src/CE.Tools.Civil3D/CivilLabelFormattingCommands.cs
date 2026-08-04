using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.CivilLabelFormattingCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Normalises CE and Civil 3D label presentation from one popup. Civil label
    /// styles are edited through their text components; ordinary CE text and
    /// leaders are made annotative and assigned the equivalent model height.
    /// </summary>
    public sealed class CivilLabelFormattingCommands
    {
        [CommandMethod("CE_TOOLS", "CE_CIVILLABELFORMAT", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void FormatLabels()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            AnnotationOptions current = AnnotationSettingsStore.Read(document.Database);
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Automatic Civil Label Formatting",
                "Format alignment, profile, corridor, pipe, structure and CE annotation text using one paper height.");
            model.AddPaperHeight(
                "Height", "Presentation", "Paper text height (mm)",
                current.TextHeight,
                "Applied to Civil label-style text components and CE annotative objects.");
            model.AddChoice(
                "Scope", "Presentation", "Objects to format", "Entire current drawing",
                "Format every supported label in the current space or only objects you select.",
                new[] { "Entire current drawing", "Select objects" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            double paperHeight = model.Double("Height", current.TextHeight);
            List<ObjectId> ids;
            if (string.Equals(model.Text("Scope"), "Select objects", StringComparison.OrdinalIgnoreCase))
            {
                PromptSelectionResult selection = document.Editor.GetSelection(
                    new PromptSelectionOptions
                    {
                        MessageForAdding = "\nSelect Civil labels and CE annotations to format: ",
                        AllowDuplicates = false
                    });
                if (selection.Status != PromptStatus.OK) return;
                ids = selection.Value.GetObjectIds().ToList();
            }
            else
            {
                ids = ReadCurrentSpaceIds(document.Database);
            }

            int objects = 0;
            int styles = 0;
            int components = 0;
            var formattedStyles = new HashSet<ObjectId>();
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids)
                {
                    Entity entity;
                    try { entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity; }
                    catch { continue; }
                    if (entity == null) continue;

                    bool changed = FormatEntityText(
                        entity,
                        document.Database,
                        paperHeight);
                    ObjectId styleId = ReadStyleId(entity);
                    if (!styleId.IsNull && formattedStyles.Add(styleId))
                    {
                        DBObject style;
                        try { style = transaction.GetObject(styleId, OpenMode.ForWrite, false); }
                        catch { style = null; }
                        int count = FormatLabelStyle(style, transaction, paperHeight);
                        if (count > 0)
                        {
                            styles++;
                            components += count;
                            changed = true;
                        }
                    }
                    if (changed) objects++;
                }
                transaction.Commit();
            }

            document.Editor.Regen();
            System.Windows.MessageBox.Show(
                string.Format(
                    CultureInfo.CurrentCulture,
                    "Paper height: {0:0.0} mm\nObjects formatted: {1}\nCivil label styles formatted: {2}\nText components updated: {3}",
                    paperHeight, objects, styles, components),
                "CE Tools - Automatic Label Formatting",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
        }

        private static bool FormatEntityText(Entity entity, Database database, double paperHeight)
        {
            double height = PaperAnnotationScale.ModelTextHeight(database, paperHeight);
            MText mtext = entity as MText;
            if (mtext != null)
            {
                mtext.UpgradeOpen();
                mtext.TextHeight = height;
                PaperAnnotationScale.SetAnnotative(mtext);
                return true;
            }
            DBText text = entity as DBText;
            if (text != null)
            {
                text.UpgradeOpen();
                text.Height = height;
                PaperAnnotationScale.SetAnnotative(text);
                return true;
            }
            MLeader leader = entity as MLeader;
            if (leader != null && leader.ContentType == ContentType.MTextContent)
            {
                leader.UpgradeOpen();
                MText contents = leader.MText;
                if (contents != null)
                {
                    contents.TextHeight = height;
                    leader.MText = contents;
                }
                PaperAnnotationScale.SetAnnotative(leader);
                return true;
            }
            return false;
        }

        private static ObjectId ReadStyleId(object value)
        {
            foreach (string name in new[] { "LabelStyleId", "StyleId" })
            {
                try
                {
                    PropertyInfo property = value.GetType().GetProperty(
                        name, BindingFlags.Public | BindingFlags.Instance);
                    object result = property == null ? null : property.GetValue(value, null);
                    if (result is ObjectId && !((ObjectId)result).IsNull)
                        return (ObjectId)result;
                }
                catch
                {
                    // Try the alternate style property.
                }
            }
            return ObjectId.Null;
        }

        private static int FormatLabelStyle(
            object style,
            Transaction transaction,
            double paperHeight)
        {
            if (style == null) return 0;
            int changed = 0;
            foreach (MethodInfo method in style.GetType().GetMethods(
                BindingFlags.Public | BindingFlags.Instance))
            {
                if (!string.Equals(method.Name, "GetComponents", StringComparison.Ordinal) ||
                    method.GetParameters().Length != 1 ||
                    !method.GetParameters()[0].ParameterType.IsEnum)
                    continue;
                Type enumType = method.GetParameters()[0].ParameterType;
                foreach (string name in Enum.GetNames(enumType)
                    .Where(item => item.IndexOf("Text", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    object result;
                    try { result = method.Invoke(style, new[] { Enum.Parse(enumType, name) }); }
                    catch { continue; }
                    IEnumerable values = result as IEnumerable;
                    if (values == null) continue;
                    foreach (object value in values)
                    {
                        object component = value;
                        if (value is ObjectId)
                        {
                            try { component = transaction.GetObject((ObjectId)value, OpenMode.ForWrite, false); }
                            catch { component = null; }
                        }
                        if (SetHeight(component, paperHeight, 0,
                                new HashSet<object>(ReferenceEqualityComparer.Instance)))
                            changed++;
                    }
                }
            }
            return changed;
        }

        private static bool SetHeight(
            object value,
            double height,
            int depth,
            ISet<object> visited)
        {
            if (value == null || depth > 2 || visited.Contains(value)) return false;
            visited.Add(value);
            bool changed = false;
            foreach (PropertyInfo property in value.GetType().GetProperties(
                BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length != 0 || !property.CanRead) continue;
                if ((string.Equals(property.Name, "Height", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(property.Name, "TextHeight", StringComparison.OrdinalIgnoreCase)) &&
                    property.CanWrite && property.PropertyType == typeof(double))
                {
                    try { property.SetValue(value, height, null); changed = true; }
                    catch { }
                    continue;
                }
                if (depth >= 2 ||
                    (property.Name.IndexOf("Text", StringComparison.OrdinalIgnoreCase) < 0 &&
                     property.Name.IndexOf("Height", StringComparison.OrdinalIgnoreCase) < 0))
                    continue;
                object child;
                try { child = property.GetValue(value, null); }
                catch { continue; }
                changed = SetHeight(child, height, depth + 1, visited) || changed;
            }
            return changed;
        }

        private static List<ObjectId> ReadCurrentSpaceIds(Database database)
        {
            var ids = new List<ObjectId>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = transaction.GetObject(
                    database.CurrentSpaceId, OpenMode.ForRead, false) as BlockTableRecord;
                if (space != null)
                    foreach (ObjectId id in space) ids.Add(id);
            }
            return ids;
        }
    }
}

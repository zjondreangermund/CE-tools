using System;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
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
                return;

            Editor editor = document.Editor;
            Database database = document.Database;

            PromptKeywordOptions modeOptions = new PromptKeywordOptions(
                "\nColour 250 scope [GeometryOnly/IncludeAnnotation] <GeometryOnly>: ")
            {
                AllowNone = true
            };
            modeOptions.Keywords.Add("GeometryOnly");
            modeOptions.Keywords.Add("IncludeAnnotation");

            PromptResult modeResult = editor.GetKeywords(modeOptions);
            if (modeResult.Status == PromptStatus.Cancel)
                return;

            bool includeAnnotation =
                modeResult.Status == PromptStatus.OK &&
                modeResult.StringResult.Equals(
                    "IncludeAnnotation",
                    StringComparison.OrdinalIgnoreCase);

            PromptSelectionResult selection = editor.SelectImplied();
            if (selection.Status != PromptStatus.OK || selection.Value.Count == 0)
            {
                selection = editor.GetSelection(
                    new PromptSelectionOptions
                    {
                        MessageForAdding = includeAnnotation
                            ? "\nSelect geometry and annotation to change to colour 250: "
                            : "\nSelect geometry to change to colour 250: ",
                        AllowDuplicates = false,
                        RejectObjectsFromNonCurrentSpace = true
                    });
            }

            if (selection.Status != PromptStatus.OK)
                return;

            int changedGeometry = 0;
            int changedAnnotation = 0;
            int changedNestedAttributes = 0;
            int alreadyColour250 = 0;
            int skippedAnnotation = 0;
            int skippedLockedLayer = 0;
            int skippedUnsupported = 0;
            int annotationOverrides = 0;

            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
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

                    bool annotation = IsAnnotationEntity(entity);
                    if (annotation && !includeAnnotation)
                    {
                        skippedAnnotation++;
                        continue;
                    }

                    bool entityChanged = entity.ColorIndex != TargetColourIndex;
                    if (entityChanged)
                    {
                        entity.UpgradeOpen();
                        entity.ColorIndex = TargetColourIndex;
                    }

                    if (includeAnnotation && entity is BlockReference attributedBlock)
                    {
                        changedNestedAttributes += ChangeBlockAttributes(
                            attributedBlock,
                            transaction,
                            ref alreadyColour250,
                            ref skippedLockedLayer);
                    }

                    if (annotation && includeAnnotation)
                    {
                        if (!entity.IsWriteEnabled)
                            entity.UpgradeOpen();

                        int entityOverrides = ApplyAnnotationColourOverrides(entity);
                        annotationOverrides += entityOverrides;

                        if (entityChanged || entityOverrides > 0)
                            changedAnnotation++;
                        else
                            alreadyColour250++;
                    }
                    else
                    {
                        if (entityChanged)
                            changedGeometry++;
                        else
                            alreadyColour250++;
                    }
                }

                transaction.Commit();
            }

            editor.SetImpliedSelection(Array.Empty<ObjectId>());
            editor.WriteMessage(
                $"\nCE_COLOR250 complete ({(includeAnnotation ? "geometry + annotation" : "geometry only")}). " +
                $"Geometry changed: {changedGeometry}; annotation changed: {changedAnnotation}; " +
                $"attribute references changed: {changedNestedAttributes}; annotation colour overrides: {annotationOverrides}; " +
                $"already 250: {alreadyColour250}; annotation excluded: {skippedAnnotation}; " +
                $"locked-layer skips: {skippedLockedLayer}; unsupported skips: {skippedUnsupported}.");

            if (includeAnnotation)
            {
                editor.WriteMessage(
                    "\nNote: Civil 3D label components that are explicitly controlled by a label style may still display the style colour. " +
                    "Edit or apply the approved label style when a component-level style override remains visible.");
            }
        }

        private static bool IsAnnotationEntity(Entity entity)
        {
            if (entity is DBText ||
                entity is MText ||
                entity is Dimension ||
                entity is Leader ||
                entity is MLeader ||
                entity is Table ||
                entity is AttributeReference)
                return true;

            string typeName = entity.GetType().FullName ?? entity.GetType().Name;
            return ContainsIgnoreCase(typeName, "Label") ||
                   ContainsIgnoreCase(typeName, "Annotation") ||
                   ContainsIgnoreCase(typeName, "Text") ||
                   ContainsIgnoreCase(typeName, "Note");
        }

        private static int ApplyAnnotationColourOverrides(Entity entity)
        {
            int changed = 0;
            Color target = Color.FromColorIndex(ColorMethod.ByAci, TargetColourIndex);

            foreach (string propertyName in new[]
            {
                "TextColor",
                "DimensionLineColor",
                "ExtensionLineColor",
                "Dimclrd",
                "Dimclre",
                "Dimclrt",
                "LeaderLineColor",
                "ContentColor"
            })
            {
                PropertyInfo property = entity.GetType().GetProperty(
                    propertyName,
                    BindingFlags.Instance | BindingFlags.Public);

                if (property == null ||
                    !property.CanRead ||
                    !property.CanWrite ||
                    !typeof(Color).IsAssignableFrom(property.PropertyType))
                    continue;

                try
                {
                    Color current = property.GetValue(entity, null) as Color;
                    if (current != null && current.ColorIndex == TargetColourIndex)
                        continue;

                    property.SetValue(entity, target, null);
                    changed++;
                }
                catch
                {
                    // Some Civil 3D wrappers expose a property but reject direct
                    // assignment when the component is controlled by its style.
                }
            }

            return changed;
        }

        private static int ChangeBlockAttributes(
            BlockReference blockReference,
            Transaction transaction,
            ref int alreadyColour250,
            ref int skippedLockedLayer)
        {
            int changed = 0;

            foreach (ObjectId attributeId in blockReference.AttributeCollection)
            {
                if (attributeId.IsNull)
                    continue;

                AttributeReference attribute = transaction.GetObject(
                    attributeId,
                    OpenMode.ForRead,
                    false) as AttributeReference;

                if (attribute == null)
                    continue;

                LayerTableRecord layer = transaction.GetObject(
                    attribute.LayerId,
                    OpenMode.ForRead,
                    false) as LayerTableRecord;

                if (layer != null && layer.IsLocked)
                {
                    skippedLockedLayer++;
                    continue;
                }

                if (attribute.ColorIndex == TargetColourIndex)
                {
                    alreadyColour250++;
                    continue;
                }

                attribute.UpgradeOpen();
                attribute.ColorIndex = TargetColourIndex;
                changed++;
            }

            return changed;
        }

        private static bool ContainsIgnoreCase(string value, string token)
        {
            return value.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}

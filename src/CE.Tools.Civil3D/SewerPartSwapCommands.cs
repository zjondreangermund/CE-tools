using System;
using System.Collections.Generic;
using System.Globalization;
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
using CivilNetwork = Autodesk.Civil.DatabaseServices.Network;
using CivilPart = Autodesk.Civil.DatabaseServices.Part;
using CivilPipe = Autodesk.Civil.DatabaseServices.Pipe;
using CivilStructure = Autodesk.Civil.DatabaseServices.Structure;

[assembly: CommandClass(typeof(CETools.Civil3D.SewerPartSwapCommands))]

namespace CETools.Civil3D
{
    public sealed class SewerPartSwapCommands
    {
        [CommandMethod("CE_TOOLS", "CE_SEWPARTSWAPMULTI",
            CommandFlags.Modal | CommandFlags.Redraw | CommandFlags.UsePickSet)]
        public void SwapMultipleSewerParts()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civil = CivilApplication.ActiveDocument;
            if (document == null || civil == null) return;

            PromptSelectionResult selection = document.Editor.SelectImplied();
            if (selection.Status != PromptStatus.OK ||
                selection.Value == null ||
                selection.Value.Count == 0)
            {
                selection = document.Editor.GetSelection(new PromptSelectionOptions
                {
                    MessageForAdding = "\nSelect multiple sewer pipes/structures in plan or profile views: ",
                    MessageForRemoval = "\nRemove sewer parts: ",
                    AllowDuplicates = false,
                    RejectObjectsFromNonCurrentSpace = false
                });
            }
            if (selection.Status != PromptStatus.OK ||
                selection.Value == null ||
                selection.Value.Count == 0) return;

            List<ObjectId> partIds = ResolveSelectedParts(
                document.Database,
                selection.Value.GetObjectIds());
            if (partIds.Count == 0)
            {
                document.Editor.WriteMessage(
                    "\nCE_SEWPARTSWAPMULTI: no gravity-network pipes or structures were resolved from the selection.");
                return;
            }

            ObjectId partsListId = ObjectId.Null;
            int pipeCount = 0;
            int structureCount = 0;
            string preflightError = string.Empty;
            using (Transaction transaction =
                document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId partId in partIds)
                {
                    CivilPart part = transaction.GetObject(
                        partId,
                        OpenMode.ForRead,
                        false) as CivilPart;
                    if (part == null || part.IsReferenceObject) continue;

                    CivilNetwork network = part.NetworkId.IsNull
                        ? null
                        : transaction.GetObject(
                            part.NetworkId,
                            OpenMode.ForRead,
                            false) as CivilNetwork;
                    if (network == null) continue;

                    if (partsListId.IsNull)
                        partsListId = network.PartsListId;
                    else if (network.PartsListId != partsListId)
                    {
                        preflightError =
                            "Selected parts use more than one Parts List. Run the command once per Parts List.";
                        break;
                    }

                    if (part is CivilPipe) pipeCount++;
                    else if (part is CivilStructure) structureCount++;
                }
            }

            if (!string.IsNullOrWhiteSpace(preflightError))
            {
                document.Editor.WriteMessage(
                    "\nCE_SEWPARTSWAPMULTI cancelled. " + preflightError);
                return;
            }
            if (partsListId.IsNull)
            {
                document.Editor.WriteMessage(
                    "\nCE_SEWPARTSWAPMULTI: selected parts do not expose an editable gravity-network Parts List.");
                return;
            }

            List<SwapChoice> pipeChoices = ReadChoices(
                document.Database,
                partsListId,
                DomainType.Pipe,
                false);
            List<SwapChoice> structureChoices = ReadChoices(
                document.Database,
                partsListId,
                DomainType.Structure,
                true);

            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Swap Multiple Sewer Parts",
                "Swap multiple selected gravity-network parts once. Civil 3D uses the same network part in plan and profile views, so both representations update together.");
            settings.AddChoice(
                "PipePart",
                "01 Pipes",
                "Pipe family / size",
                "<Keep selected pipe parts>",
                "Applied to every selected pipe. Names, reference surface and rule-set assignment are retained.",
                new[] { "<Keep selected pipe parts>" }
                    .Concat(pipeChoices.Select(choice => choice.Label))
                    .ToArray());
            settings.AddChoice(
                "StructurePart",
                "02 Structures",
                "Structure / manhole family / size",
                "<Keep selected structure parts>",
                "Applied to every selected structure/manhole. Names, reference surface and rule-set assignment are retained.",
                new[] { "<Keep selected structure parts>" }
                    .Concat(structureChoices.Select(choice => choice.Label))
                    .ToArray());
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;

            SwapChoice selectedPipe = FindChoice(
                pipeChoices,
                settings.Text("PipePart"));
            SwapChoice selectedStructure = FindChoice(
                structureChoices,
                settings.Text("StructurePart"));

            if (selectedPipe == null && selectedStructure == null)
            {
                document.Editor.WriteMessage(
                    "\nCE_SEWPARTSWAPMULTI: both choices are Keep; nothing was changed.");
                return;
            }

            int swappedPipes = 0;
            int swappedStructures = 0;
            int skipped = 0;
            try
            {
                using (DocumentLock documentLock = document.LockDocument())
                using (Transaction transaction =
                    document.Database.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId partId in partIds)
                    {
                        CivilPart part = transaction.GetObject(
                            partId,
                            OpenMode.ForWrite,
                            false) as CivilPart;
                        if (part == null || part.IsReferenceObject)
                        {
                            skipped++;
                            continue;
                        }

                        SwapChoice choice =
                            part is CivilPipe ? selectedPipe :
                            part is CivilStructure ? selectedStructure :
                            null;
                        if (choice == null)
                        {
                            skipped++;
                            continue;
                        }

                        string name = string.Empty;
                        string description = string.Empty;
                        ObjectId ruleSetId = ObjectId.Null;
                        ObjectId surfaceId = ObjectId.Null;
                        try { name = part.Name ?? string.Empty; } catch { }
                        try { description = part.Description ?? string.Empty; } catch { }
                        try { ruleSetId = part.RuleSetStyleId; } catch { }
                        try { surfaceId = part.RefSurfaceId; } catch { }

                        if (!TrySwapPart(
                                part,
                                choice.FamilyId,
                                choice.SizeId))
                            throw new InvalidOperationException(
                                "Civil 3D did not expose SwapPartFamilyAndSize for " +
                                (string.IsNullOrWhiteSpace(name)
                                    ? partId.Handle.ToString()
                                    : name) + ".");

                        try
                        {
                            if (!string.IsNullOrWhiteSpace(name))
                                part.Name = name;
                        }
                        catch { }
                        try { part.Description = description; } catch { }
                        try
                        {
                            if (!ruleSetId.IsNull)
                                part.RuleSetStyleId = ruleSetId;
                        }
                        catch { }
                        try
                        {
                            if (!surfaceId.IsNull)
                                part.RefSurfaceId = surfaceId;
                        }
                        catch { }

                        if (part is CivilPipe) swappedPipes++;
                        else swappedStructures++;
                    }

                    transaction.Commit();
                }
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_SEWPARTSWAPMULTI cancelled; no batch changes were committed. " +
                    exception.Message);
                return;
            }

            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_SEWPARTSWAPMULTI complete. Pipes swapped={0}; structures swapped={1}; kept/skipped={2}. Plan and profile views reference the same changed Civil 3D parts.",
                swappedPipes,
                swappedStructures,
                skipped);
        }

        private static List<ObjectId> ResolveSelectedParts(
            Database database,
            IEnumerable<ObjectId> selectedIds)
        {
            var result = new HashSet<ObjectId>();
            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId selectedId in selectedIds
                    .Where(id => !id.IsNull && !id.IsErased)
                    .Distinct())
                {
                    DBObject selected = null;
                    try
                    {
                        selected = transaction.GetObject(
                            selectedId,
                            OpenMode.ForRead,
                            false);
                    }
                    catch { }
                    if (selected == null) continue;

                    if (selected is CivilPipe ||
                        selected is CivilStructure)
                    {
                        result.Add(selectedId);
                        continue;
                    }

                    foreach (string propertyName in new[]
                    {
                        "PartId",
                        "PipeId",
                        "StructureId",
                        "SourceEntityId",
                        "SourceId",
                        "EntityId"
                    })
                    {
                        ObjectId linkedId = ReadObjectId(
                            selected,
                            propertyName);
                        if (linkedId.IsNull || linkedId.IsErased)
                            continue;
                        DBObject linked = null;
                        try
                        {
                            linked = transaction.GetObject(
                                linkedId,
                                OpenMode.ForRead,
                                false);
                        }
                        catch { }
                        if (linked is CivilPipe ||
                            linked is CivilStructure)
                        {
                            result.Add(linkedId);
                            break;
                        }
                    }
                }
            }

            return result
                .OrderBy(id => id.Handle.Value)
                .ToList();
        }

        private static ObjectId ReadObjectId(
            object value,
            string propertyName)
        {
            try
            {
                PropertyInfo property = value.GetType().GetProperty(
                    propertyName,
                    BindingFlags.Public | BindingFlags.Instance);
                object raw = property == null
                    ? null
                    : property.GetValue(value, null);
                return raw is ObjectId
                    ? (ObjectId)raw
                    : ObjectId.Null;
            }
            catch
            {
                return ObjectId.Null;
            }
        }

        private static List<SwapChoice> ReadChoices(
            Database database,
            ObjectId partsListId,
            DomainType domain,
            bool skipNull)
        {
            var result = new List<SwapChoice>();
            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                PartsList list = transaction.GetObject(
                    partsListId,
                    OpenMode.ForRead,
                    false) as PartsList;
                if (list == null) return result;

                foreach (ObjectId familyId in
                    list.GetPartFamilyIdsByDomain(domain))
                {
                    PartFamily family = transaction.GetObject(
                        familyId,
                        OpenMode.ForRead,
                        false) as PartFamily;
                    if (family == null) continue;
                    if (skipNull &&
                        (family.Name ?? string.Empty)
                            .IndexOf("null", StringComparison.OrdinalIgnoreCase) >= 0)
                        continue;

                    for (int index = 0;
                         index < family.PartSizeCount;
                         index++)
                    {
                        ObjectId sizeId = family[index];
                        PartSize size = transaction.GetObject(
                            sizeId,
                            OpenMode.ForRead,
                            false) as PartSize;
                        if (size == null) continue;
                        string familyName =
                            string.IsNullOrWhiteSpace(family.Name)
                                ? "Part Family"
                                : family.Name;
                        string sizeName =
                            string.IsNullOrWhiteSpace(size.Name)
                                ? "Size " + (index + 1).ToString(CultureInfo.InvariantCulture)
                                : size.Name;
                        result.Add(new SwapChoice(
                            familyId,
                            sizeId,
                            familyName + " | " + sizeName));
                    }
                }
            }

            return result
                .OrderBy(choice => choice.Label,
                    StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private static SwapChoice FindChoice(
            IEnumerable<SwapChoice> choices,
            string label)
        {
            if (string.IsNullOrWhiteSpace(label) ||
                label.StartsWith("<Keep", StringComparison.OrdinalIgnoreCase))
                return null;

            return choices.FirstOrDefault(choice =>
                string.Equals(
                    choice.Label,
                    label,
                    StringComparison.OrdinalIgnoreCase));
        }

        private static bool TrySwapPart(
            CivilPart part,
            ObjectId familyId,
            ObjectId sizeId)
        {
            if (part == null ||
                familyId.IsNull ||
                sizeId.IsNull) return false;

            try
            {
                MethodInfo method = part.GetType().GetMethod(
                    "SwapPartFamilyAndSize",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new[] { typeof(ObjectId), typeof(ObjectId) },
                    null);
                if (method == null) return false;
                method.Invoke(
                    part,
                    new object[] { familyId, sizeId });
                return true;
            }
            catch (TargetInvocationException exception)
            {
                throw new InvalidOperationException(
                    exception.InnerException == null
                        ? exception.Message
                        : exception.InnerException.Message,
                    exception.InnerException ?? exception);
            }
        }

        private sealed class SwapChoice
        {
            internal SwapChoice(
                ObjectId familyId,
                ObjectId sizeId,
                string label)
            {
                FamilyId = familyId;
                SizeId = sizeId;
                Label = label ?? string.Empty;
            }

            internal ObjectId FamilyId { get; private set; }
            internal ObjectId SizeId { get; private set; }
            internal string Label { get; private set; }
        }
    }
}

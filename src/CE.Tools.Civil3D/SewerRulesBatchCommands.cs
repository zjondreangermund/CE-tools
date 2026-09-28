using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilPipe = Autodesk.Civil.DatabaseServices.Pipe;
using CivilStructure = Autodesk.Civil.DatabaseServices.Structure;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;
using StyleBase = Autodesk.Civil.DatabaseServices.Styles.StyleBase;

[assembly: CommandClass(typeof(CETools.Civil3D.SewerRulesBatchCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Applies the same Civil 3D surface/rule choices and ApplyRules operation to
    /// several already-selected sewer pipes and structures. Civil 3D's ribbon
    /// Apply Rules menu is a one-part command; this keeps its native rule engine
    /// while making the selection and choices batch-safe.
    /// </summary>
    public sealed class SewerRulesBatchCommands
    {
        [CommandMethod("CE_TOOLS", "CE_SEWERAPPLYRULESMULTI", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void ApplyRulesToMultipleSewerParts()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (document == null || civilDocument == null) return;

            PromptSelectionResult selection = GetSelection(
                document.Editor,
                "\nSelect multiple Civil 3D sewer pipes and structures for Apply Rules: ");
            if (selection.Status != PromptStatus.OK || selection.Value == null)
                return;

            List<SewerPartSelection> parts = ReadSelectedParts(document.Database, selection);
            if (parts.Count == 0)
            {
                document.Editor.WriteMessage(
                    "\nCE_SEWERAPPLYRULESMULTI: select at least one editable Civil 3D gravity-network pipe or structure.");
                return;
            }

            List<NamedId> surfaces = ReadSurfaces(
                document.Database,
                civilDocument.GetSurfaceIds().Cast<ObjectId>());
            List<NamedId> pipeRules = ReadStyles(
                document.Database,
                civilDocument.Styles.PipeRuleSetStyles.Cast<ObjectId>());
            List<NamedId> structureRules = ReadStyles(
                document.Database,
                civilDocument.Styles.StructureRuleSetStyles.Cast<ObjectId>());

            var surfaceChoices = new List<string> { "<Keep current surface>" };
            surfaceChoices.AddRange(surfaces.Select(item => item.Name));
            var pipeRuleChoices = new List<string> { "<Keep current pipe rule set>" };
            pipeRuleChoices.AddRange(pipeRules.Select(item => item.Name));
            var structureRuleChoices = new List<string> { "<Keep current structure rule set>" };
            structureRuleChoices.AddRange(structureRules.Select(item => item.Name));

            var model = new ProductionSettingsDialogModel(
                "CE Tools - Civil 3D Apply Rules (Multiple Sewer Parts)",
                "This is the batch equivalent of the Civil 3D Pipes ribbon > Apply Rules... command. The selected surface and rule sets are assigned to every selected editable pipe/structure, then Civil 3D ApplyRules() is called once per part in one committed transaction.");
            model.AddChoice(
                "Surface",
                "01 Reference surface",
                "Reference surface",
                surfaceChoices[0],
                "Optional surface assigned to the selected sewer parts before rules are evaluated.",
                surfaceChoices);
            model.AddChoice(
                "PipeAction",
                "02 Pipe rules",
                "Pipe rule-set action",
                "Apply selected pipe rule set",
                "Choose whether selected pipes receive the chosen Civil 3D Pipe Rule Set.",
                new[] { "Apply selected pipe rule set", "Keep current pipe rule set" });
            model.AddChoice(
                "PipeRuleSet",
                "02 Pipe rules",
                "Pipe rule set",
                pipeRuleChoices[0],
                "Civil 3D Pipe Rule Set used by ApplyRules() for selected pipes.",
                pipeRuleChoices);
            model.AddChoice(
                "StructureAction",
                "03 Structure rules",
                "Structure rule-set action",
                "Apply selected structure rule set",
                "Choose whether selected structures receive the chosen Civil 3D Structure Rule Set.",
                new[] { "Apply selected structure rule set", "Keep current structure rule set" });
            model.AddChoice(
                "StructureRuleSet",
                "03 Structure rules",
                "Structure rule set",
                structureRuleChoices[0],
                "Civil 3D Structure Rule Set used by ApplyRules() for selected structures.",
                structureRuleChoices);
            model.AddChoice(
                "ApplyRules",
                "04 Civil 3D operation",
                "Run Civil 3D Apply Rules",
                "Yes",
                "Calls the native Civil 3D ApplyRules() operation separately for every selected part.",
                new[] { "Yes", "No" });
            model.AddChoice("DrawInProfiles", "04 Civil 3D operation",
                "Draw selected parts in profile views", "Yes",
                "After applying rules, select multiple profile views. Only matching pipes and structures will be drawn.",
                new[] { "Yes", "No" });

            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            NamedId surface = Find(surfaces, model.Text("Surface"));
            ObjectId pipeRuleId = Resolve(pipeRules, model.Text("PipeRuleSet"));
            ObjectId structureRuleId = Resolve(structureRules, model.Text("StructureRuleSet"));
            bool assignPipeRule = string.Equals(
                model.Text("PipeAction"),
                "Apply selected pipe rule set",
                StringComparison.OrdinalIgnoreCase);
            bool assignStructureRule = string.Equals(
                model.Text("StructureAction"),
                "Apply selected structure rule set",
                StringComparison.OrdinalIgnoreCase);
            bool applyRules = string.Equals(
                model.Text("ApplyRules"),
                "Yes",
                StringComparison.OrdinalIgnoreCase);

            int pipes = 0;
            int structures = 0;
            int rulesApplied = 0;
            int skipped = 0;
            int failed = 0;
            var rows = new List<IList<string>>();
            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (SewerPartSelection selected in parts)
                {
                    string kind = selected.Kind;
                    string partName = selected.Name;
                    string networkName = selected.NetworkId.IsNull
                        ? "<no network>"
                        : selected.NetworkId.Handle.ToString();
                    string ruleName = "<current>";
                    string result = string.Empty;
                    try
                    {
                        DBObject value = transaction.GetObject(
                            selected.Id,
                            OpenMode.ForWrite,
                            false);
                        CivilPipe pipe = value as CivilPipe;
                        CivilStructure structure = value as CivilStructure;
                        if ((pipe == null && structure == null) ||
                            (pipe != null && pipe.IsReferenceObject) ||
                            (structure != null && structure.IsReferenceObject))
                        {
                            skipped++;
                            rows.Add(Row(kind, partName, networkName, "<reference/read-only>", "Skipped"));
                            continue;
                        }

                        if (surface != null && !surface.Id.IsNull)
                        {
                            if (pipe != null) pipe.RefSurfaceId = surface.Id;
                            else structure.RefSurfaceId = surface.Id;
                        }

                        if (pipe != null)
                        {
                            pipes++;
                            if (assignPipeRule && !pipeRuleId.IsNull)
                            {
                                pipe.RuleSetStyleId = pipeRuleId;
                                ruleName = FindName(pipeRules, pipeRuleId);
                            }
                            if (applyRules)
                            {
                                try
                                {
                                    if (pipe.ApplyRules())
                                    {
                                        rulesApplied++;
                                        result = "Applied";
                                    }
                                    else
                                    {
                                        failed++;
                                        result = "Civil 3D returned false";
                                    }
                                }
                                catch (System.Exception exception)
                                {
                                    failed++;
                                    result = "Failed: " + exception.Message;
                                }
                            }
                            else result = "Assigned; Apply Rules not run";
                        }
                        else
                        {
                            structures++;
                            if (assignStructureRule && !structureRuleId.IsNull)
                            {
                                structure.RuleSetStyleId = structureRuleId;
                                ruleName = FindName(structureRules, structureRuleId);
                            }
                            if (applyRules)
                            {
                                try
                                {
                                    if (structure.ApplyRules())
                                    {
                                        rulesApplied++;
                                        result = "Applied";
                                    }
                                    else
                                    {
                                        failed++;
                                        result = "Civil 3D returned false";
                                    }
                                }
                                catch (System.Exception exception)
                                {
                                    failed++;
                                    result = "Failed: " + exception.Message;
                                }
                            }
                            else result = "Assigned; Apply Rules not run";
                        }
                        rows.Add(Row(kind, partName, networkName,
                            surface == null ? "<current>" : surface.Name,
                            ruleName + " - " + result));
                    }
                    catch (System.Exception exception)
                    {
                        skipped++;
                        failed++;
                        rows.Add(Row(kind, partName, networkName, "<unchanged>", "Failed: " + exception.Message));
                    }
                }
                transaction.Commit();
            }

            try { document.Editor.Regen(); } catch { }
            document.Editor.WriteMessage(
                "\nCE_SEWERAPPLYRULESMULTI complete. Selected parts={0}; pipes={1}; structures={2}; Civil 3D rules applied={3}; skipped={4}; failed={5}; surface='{6}'.",
                parts.Count,
                pipes,
                structures,
                rulesApplied,
                skipped,
                failed,
                surface == null ? "<current per part>" : surface.Name);
            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - Civil 3D Apply Rules Results",
                applyRules
                    ? "Civil 3D ApplyRules() was called for each selected pipe and structure. Review any returned false/failed rows before creating or refreshing profile views."
                    : "The selected surface and rule-set assignments were saved; Civil 3D ApplyRules() was not run.",
                new List<string> { "Part", "Name", "Network", "Reference Surface", "Rule / Result" },
                rows,
                "CE CIVIL 3D APPLY RULES RESULTS");
            if (string.Equals(model.Text("DrawInProfiles"), "Yes", StringComparison.OrdinalIgnoreCase))
            {
                document.Editor.SetImpliedSelection(parts.Select(item => item.Id).ToArray());
                document.SendStringToExecute("CE_SEWSELECTEDPARTSPROFILEMULTI ", true, false, true);
            }
        }

        private static PromptSelectionResult GetSelection(Editor editor, string message)
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

        private static List<SewerPartSelection> ReadSelectedParts(
            Database database,
            PromptSelectionResult selection)
        {
            var result = new List<SewerPartSelection>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject selected in selection.Value)
                {
                    if (selected == null || selected.ObjectId.IsNull || selected.ObjectId.IsErased)
                        continue;
                    try
                    {
                        DBObject value = transaction.GetObject(selected.ObjectId, OpenMode.ForRead, false);
                        CivilPipe pipe = value as CivilPipe;
                        CivilStructure structure = value as CivilStructure;
                        if (pipe != null)
                        {
                            result.Add(new SewerPartSelection(
                                selected.ObjectId,
                                "Pipe",
                                pipe.Name,
                                pipe.NetworkId));
                        }
                        else if (structure != null)
                        {
                            result.Add(new SewerPartSelection(
                                selected.ObjectId,
                                "Structure",
                                structure.Name,
                                structure.NetworkId));
                        }
                    }
                    catch { }
                }
            }
            return result
                .GroupBy(item => item.Id)
                .Select(item => item.First())
                .ToList();
        }

        private static List<NamedId> ReadSurfaces(Database database, IEnumerable<ObjectId> ids)
        {
            var result = new List<NamedId>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids ?? Enumerable.Empty<ObjectId>())
                {
                    try
                    {
                        CivilSurface surface = transaction.GetObject(id, OpenMode.ForRead, false) as CivilSurface;
                        if (surface != null && !string.IsNullOrWhiteSpace(surface.Name))
                            result.Add(new NamedId(id, surface.Name));
                    }
                    catch { }
                }
            }
            return result.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        private static List<NamedId> ReadStyles(Database database, IEnumerable<ObjectId> ids)
        {
            var result = new List<NamedId>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids ?? Enumerable.Empty<ObjectId>())
                {
                    try
                    {
                        StyleBase style = transaction.GetObject(id, OpenMode.ForRead, false) as StyleBase;
                        if (style != null && !string.IsNullOrWhiteSpace(style.Name))
                            result.Add(new NamedId(id, style.Name));
                    }
                    catch { }
                }
            }
            return result.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        private static NamedId Find(IEnumerable<NamedId> values, string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.StartsWith("<", StringComparison.Ordinal))
                return null;
            return (values ?? Enumerable.Empty<NamedId>())
                .FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        private static ObjectId Resolve(IEnumerable<NamedId> values, string name)
        {
            NamedId item = Find(values, name);
            return item == null ? ObjectId.Null : item.Id;
        }

        private static string FindName(IEnumerable<NamedId> values, ObjectId id)
        {
            NamedId item = (values ?? Enumerable.Empty<NamedId>())
                .FirstOrDefault(value => value.Id == id);
            return item == null ? "<selected>" : item.Name;
        }

        private static IList<string> Row(
            string kind,
            string name,
            string network,
            string surface,
            string result)
        {
            return new List<string>
            {
                kind ?? string.Empty,
                name ?? string.Empty,
                network ?? string.Empty,
                surface ?? string.Empty,
                result ?? string.Empty
            };
        }

        private sealed class NamedId
        {
            internal NamedId(ObjectId id, string name)
            {
                Id = id;
                Name = name ?? string.Empty;
            }

            internal ObjectId Id;
            internal string Name;
        }

        private sealed class SewerPartSelection
        {
            internal SewerPartSelection(ObjectId id, string kind, string name, ObjectId networkId)
            {
                Id = id;
                Kind = kind ?? string.Empty;
                Name = string.IsNullOrWhiteSpace(name)
                    ? id.Handle.ToString()
                    : name;
                NetworkId = networkId;
            }

            internal ObjectId Id;
            internal string Kind;
            internal string Name;
            internal ObjectId NetworkId;
        }
    }
}

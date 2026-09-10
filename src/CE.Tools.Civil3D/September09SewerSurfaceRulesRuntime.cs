using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivilFeatureLine = Autodesk.Civil.DatabaseServices.FeatureLine;
using CivilNetwork = Autodesk.Civil.DatabaseServices.Network;
using CivilPart = Autodesk.Civil.DatabaseServices.Part;
using CivilPipe = Autodesk.Civil.DatabaseServices.Pipe;
using CivilStructure = Autodesk.Civil.DatabaseServices.Structure;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;
using CivilPolylineOptions = Autodesk.Civil.DatabaseServices.PolylineOptions;
using ConnectorPositionType = Autodesk.Civil.DatabaseServices.ConnectorPositionType;
using DomainType = Autodesk.Civil.DatabaseServices.DomainType;
using PartFamily = Autodesk.Civil.DatabaseServices.Styles.PartFamily;
using PartsList = Autodesk.Civil.DatabaseServices.Styles.PartsList;
using PartSize = Autodesk.Civil.DatabaseServices.Styles.PartSize;
using StyleBase = Autodesk.Civil.DatabaseServices.Styles.StyleBase;

[assembly: CommandClass(typeof(CETools.Civil3D.September09SewerSurfaceRulesCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Final Civil 3D 2023 sewer field runtime.
    /// Surface references are assigned to gravity-network parts before rule sets
    /// are applied. Existing parts can be relinked in one batch. The alignment
    /// path commits its temporary source polyline before calling CivilAlignment.Create
    /// so Civil 3D is never asked to consume an uncommitted source entity.
    /// </summary>
    internal static class September09SewerSurfaceRulesRuntime
    {
        private const double PointTolerance = 0.001;
        private static readonly Regex PipeNamePattern = new Regex(
            @"^P(?<branch>\d+)\.(?<sequence>\d+)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex BranchNamePattern = new Regex(
            @"^Branch\s*-\s*(?<branch>\d+)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        internal static void CreateNetworkFromMultipleSources(
            Document document,
            CivilDocument civilDocument)
        {
            if (document == null || civilDocument == null) return;
            Editor editor = document.Editor;
            Database database = document.Database;
            editor.SetImpliedSelection(new ObjectId[0]);

            PromptSelectionResult selected = editor.GetSelection(
                new PromptSelectionOptions
                {
                    MessageForAdding = "\nSelect ALL sewer source lines/polylines/feature lines for ONE sewer network: ",
                    MessageForRemoval = "\nRemove sewer source objects: ",
                    AllowDuplicates = false,
                    RejectObjectsFromNonCurrentSpace = true
                });
            if (selected.Status != PromptStatus.OK || selected.Value == null || selected.Value.Count == 0)
                return;

            List<ObjectId> sourceIds = FilterSupportedSources(database, selected.Value.GetObjectIds());
            if (sourceIds.Count == 0)
            {
                editor.WriteMessage("\nCE_SEWERNETWORKMULTI: no supported line/polyline/feature-line sources were selected.");
                return;
            }

            List<NamedId> partsLists = ReadPartsLists(database, civilDocument);
            if (partsLists.Count == 0)
            {
                editor.WriteMessage("\nCE_SEWERNETWORKMULTI: this drawing has no Civil 3D gravity-network Parts List.");
                return;
            }

            List<NamedId> surfaces = ReadSurfaces(database, civilDocument);
            if (surfaces.Count == 0)
            {
                editor.WriteMessage("\nCE_SEWERNETWORKMULTI: no Civil 3D surface exists in this drawing. Create/import the design/reference surface first.");
                return;
            }

            List<NamedId> pipeRuleSets = ReadRuleSets(database, civilDocument.Styles.PipeRuleSetStyles.Cast<ObjectId>());
            List<NamedId> structureRuleSets = ReadRuleSets(database, civilDocument.Styles.StructureRuleSetStyles.Cast<ObjectId>());
            string[] pipeRuleChoices = RuleChoiceNames(pipeRuleSets);
            string[] structureRuleChoices = RuleChoiceNames(structureRuleSets);

            string preferredPartsList = PreferredName(partsLists, new[] { "sanitary", "sewer" });
            string preferredSurface = PreferredName(surfaces, new[] { "design", "finished", "ground", "ng", "surface" });

            var setup = new ProductionSettingsDialogModel(
                "CE Tools - Sewer Network from Multiple Polylines",
                "Creates ONE Civil 3D gravity sewer network from all selected sources. The selected surface is linked to every new pipe and structure BEFORE the selected Civil 3D rule sets are applied.");
            setup.AddText("NetworkName", "01 Network", "Network name", "CE-Sewer Network",
                "Civil 3D will make the name unique if the drawing already contains it.");
            setup.AddChoice("PartsList", "01 Network", "Network parts list", preferredPartsList,
                "Choose the gravity-network parts list used for all selected sources.",
                partsLists.Select(item => item.Name).ToArray());
            setup.AddChoice("Surface", "02 Surface and rules", "Reference surface", preferredSurface,
                "Every generated pipe and structure is linked to this Civil 3D surface before rules are evaluated.",
                surfaces.Select(item => item.Name).ToArray());
            setup.AddChoice("PipeRules", "02 Surface and rules", "Pipe rules", "Apply selected rule set",
                "Choose whether Civil 3D pipe rules are applied after the surface reference is assigned.",
                new[] { "Apply selected rule set", "Do not apply pipe rules" });
            setup.AddChoice("PipeRuleSet", "02 Surface and rules", "Pipe rule set", pipeRuleChoices[0],
                "Named Civil 3D Pipe Rule Set assigned to every generated pipe before ApplyRules().",
                pipeRuleChoices);
            setup.AddChoice("StructureRules", "02 Surface and rules", "Structure rules", "Apply selected rule set",
                "Choose whether Civil 3D structure rules are applied after the surface reference is assigned.",
                new[] { "Apply selected rule set", "Do not apply structure rules" });
            setup.AddChoice("StructureRuleSet", "02 Surface and rules", "Structure rule set", structureRuleChoices[0],
                "Named Civil 3D Structure Rule Set assigned to every generated structure before ApplyRules().",
                structureRuleChoices);
            setup.AddChoice("Connections", "03 Creation", "Shared vertices / junctions", "Connect through structures",
                "Reuse one structure at coincident source vertices so branches and consecutive pipe segments are connected.",
                new[] { "Connect through structures", "Create parts without connections" });
            setup.AddPositiveDouble("MaxSpacing", "03 Creation", "Maximum structure spacing", 60.0,
                "Long source segments are divided evenly so every pipe run is within this maximum spacing.");
            setup.AddChoice("SourceObjects", "04 Sources", "Source objects after creation", "Keep source objects",
                "Keep design strings for later editing/refresh, or erase them after successful network creation.",
                new[] { "Keep source objects", "Erase source objects" });
            setup.AddChoice("PreviouslyCreated", "04 Sources", "Previously completed CE source", "Process again",
                "Skip sources already tagged as completed for Sewer, or deliberately process them again.",
                new[] { "Skip previously completed", "Process again" });
            if (!DisciplineWorkflowDialogs.EditSettings(setup)) return;

            bool skipCompleted = string.Equals(setup.Text("PreviouslyCreated"), "Skip previously completed", StringComparison.OrdinalIgnoreCase);
            if (skipCompleted)
            {
                sourceIds = sourceIds.Where(id => !NetworkSourceMarker.IsCompleted(database, id, "Sewer")).ToList();
                if (sourceIds.Count == 0)
                {
                    editor.WriteMessage("\nCE_SEWERNETWORKMULTI: all selected sources were already completed for Sewer.");
                    return;
                }
            }

            NamedId partsList = FindNamed(partsLists, setup.Text("PartsList")) ?? partsLists[0];
            NamedId surface = FindNamed(surfaces, setup.Text("Surface"));
            if (surface == null || surface.Id.IsNull)
            {
                editor.WriteMessage("\nCE_SEWERNETWORKMULTI: select a valid Civil 3D reference surface.");
                return;
            }

            List<PartChoice> pipeChoices = ReadPartChoices(database, partsList.Id, DomainType.Pipe, false);
            List<PartChoice> structureChoices = ReadPartChoices(database, partsList.Id, DomainType.Structure, true);
            if (pipeChoices.Count == 0 || structureChoices.Count == 0)
            {
                editor.WriteMessage("\nCE_SEWERNETWORKMULTI: parts list '{0}' must contain a pipe size and a non-null structure size.", partsList.Name);
                return;
            }

            var parts = new ProductionSettingsDialogModel(
                "CE Tools - Sewer Pipe and Structure Parts",
                "Choose the pipe and manhole/structure once. These parts are used for the complete multi-source network.");
            parts.AddChoice("PipePart", "01 Pipes", "Pipe family / size",
                PreferredPart(pipeChoices, new[] { "sewer", "pvc", "concrete" }),
                "Pipe family and size used for every generated sewer pipe.",
                pipeChoices.Select(item => item.Label).ToArray());
            parts.AddChoice("StructurePart", "02 Structures", "Structure / manhole family / size",
                PreferredPart(structureChoices, new[] { "manhole", "junction", "structure" }),
                "Structure used at source endpoints, spacing points and shared branch junctions.",
                structureChoices.Select(item => item.Label).ToArray());
            if (!DisciplineWorkflowDialogs.EditSettings(parts)) return;

            PartChoice pipeChoice = FindPart(pipeChoices, parts.Text("PipePart"));
            PartChoice structureChoice = FindPart(structureChoices, parts.Text("StructurePart"));
            if (pipeChoice == null || structureChoice == null) return;

            List<SourcePath> paths = ReadSourcePaths(database, sourceIds);
            double maxSpacing = Math.Max(0.001, setup.Double("MaxSpacing", 60.0));
            foreach (SourcePath path in paths)
                path.Points = Densify(path.Points, maxSpacing);
            if (paths.Count == 0 || paths.Sum(path => Math.Max(0, path.Points.Count - 1)) == 0)
            {
                editor.WriteMessage("\nCE_SEWERNETWORKMULTI: selected objects do not contain usable source segments.");
                return;
            }

            bool connect = string.Equals(setup.Text("Connections"), "Connect through structures", StringComparison.OrdinalIgnoreCase);
            bool eraseSources = string.Equals(setup.Text("SourceObjects"), "Erase source objects", StringComparison.OrdinalIgnoreCase);
            bool applyPipeRules = string.Equals(setup.Text("PipeRules"), "Apply selected rule set", StringComparison.OrdinalIgnoreCase);
            bool applyStructureRules = string.Equals(setup.Text("StructureRules"), "Apply selected rule set", StringComparison.OrdinalIgnoreCase);
            ObjectId pipeRuleSetId = ResolveRuleChoice(pipeRuleSets, setup.Text("PipeRuleSet"));
            ObjectId structureRuleSetId = ResolveRuleChoice(structureRuleSets, setup.Text("StructureRuleSet"));

            string networkName = string.IsNullOrWhiteSpace(setup.Text("NetworkName")) ? "CE-Sewer Network" : setup.Text("NetworkName");
            ObjectId networkId = ObjectId.Null;
            int pipesCreated = 0;
            int structuresCreated = 0;
            int pipeRulesApplied = 0;
            int structureRulesApplied = 0;

            try
            {
                using (DocumentLock documentLock = document.LockDocument())
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    networkId = CivilNetwork.Create(civilDocument, ref networkName);
                    CivilNetwork network = transaction.GetObject(networkId, OpenMode.ForWrite, false) as CivilNetwork;
                    if (network == null) throw new InvalidOperationException("Civil 3D did not return the new gravity network.");
                    network.PartsListId = partsList.Id;
                    network.Description = "CE Tools sewer network linked to surface '" + surface.Name + "'.";
                    var structures = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);

                    foreach (SourcePath path in paths)
                    {
                        for (int index = 0; index < path.Points.Count - 1; index++)
                        {
                            Point3d start = path.Points[index];
                            Point3d end = path.Points[index + 1];
                            if (start.DistanceTo(end) <= PointTolerance) continue;

                            ObjectId startStructure = ObjectId.Null;
                            ObjectId endStructure = ObjectId.Null;
                            if (connect)
                            {
                                startStructure = EnsureStructure(network, transaction, structures, structureChoice, start,
                                    surface.Id, structureRuleSetId, applyStructureRules,
                                    ref structuresCreated, ref structureRulesApplied);
                                endStructure = EnsureStructure(network, transaction, structures, structureChoice, end,
                                    surface.Id, structureRuleSetId, applyStructureRules,
                                    ref structuresCreated, ref structureRulesApplied);
                            }

                            ObjectId pipeId = ObjectId.Null;
                            // Create without rules. Surface + named rule set are assigned first below.
                            network.AddLinePipe(pipeChoice.FamilyId, pipeChoice.SizeId,
                                new LineSegment3d(start, end), ref pipeId, false);
                            if (pipeId.IsNull) continue;

                            CivilPipe pipe = transaction.GetObject(pipeId, OpenMode.ForWrite, false) as CivilPipe;
                            if (pipe == null) continue;
                            pipe.RefSurfaceId = surface.Id;
                            if (!pipeRuleSetId.IsNull) pipe.RuleSetStyleId = pipeRuleSetId;
                            if (connect && !startStructure.IsNull && !endStructure.IsNull)
                            {
                                pipe.ConnectToStructure(ConnectorPositionType.Start, startStructure, true);
                                pipe.ConnectToStructure(ConnectorPositionType.End, endStructure, true);
                            }
                            if (applyPipeRules)
                            {
                                try { if (pipe.ApplyRules()) pipeRulesApplied++; }
                                catch { }
                            }
                            pipesCreated++;
                        }
                    }

                    if (pipesCreated == 0) throw new InvalidOperationException("No sewer pipes could be created from the selected sources.");
                    if (eraseSources)
                    {
                        foreach (ObjectId sourceId in sourceIds)
                        {
                            try
                            {
                                DBObject source = transaction.GetObject(sourceId, OpenMode.ForWrite, false);
                                if (source != null && !source.IsErased) source.Erase();
                            }
                            catch { }
                        }
                    }
                    transaction.Commit();
                }
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage("\nCE_SEWERNETWORKMULTI failed. No partial network edit was committed. {0}", exception.Message);
                return;
            }

            if (!eraseSources)
                foreach (ObjectId sourceId in sourceIds) NetworkSourceMarker.Mark(document, sourceId, "Sewer");

            editor.Regen();
            editor.WriteMessage(
                "\nCE_SEWERNETWORKMULTI complete. Network='{0}'; surface='{1}'; pipes={2}; structures={3}; pipe rules applied={4}; structure rules applied={5}.",
                networkName, surface.Name, pipesCreated, structuresCreated, pipeRulesApplied, structureRulesApplied);
            if (!networkId.IsNull)
            {
                try { editor.SetImpliedSelection(new[] { networkId }); } catch { }
            }
        }

        internal static void LinkExistingPartsToSurface(Document document, CivilDocument civilDocument)
        {
            if (document == null || civilDocument == null) return;
            Editor editor = document.Editor;
            Database database = document.Database;
            PromptSelectionResult selection = editor.GetSelection(
                new PromptSelectionOptions
                {
                    MessageForAdding = "\nSelect sewer pipes and structures to link to one surface: ",
                    MessageForRemoval = "\nRemove network parts: ",
                    AllowDuplicates = false,
                    RejectObjectsFromNonCurrentSpace = true
                });
            if (selection.Status != PromptStatus.OK || selection.Value == null || selection.Value.Count == 0) return;

            List<ObjectId> partIds = FilterGravityParts(database, selection.Value.GetObjectIds());
            if (partIds.Count == 0)
            {
                editor.WriteMessage("\nCE_SEWLINKSURFACE: select one or more Civil 3D gravity-network pipes/structures.");
                return;
            }

            List<NamedId> surfaces = ReadSurfaces(database, civilDocument);
            if (surfaces.Count == 0)
            {
                editor.WriteMessage("\nCE_SEWLINKSURFACE: this drawing contains no Civil 3D surface.");
                return;
            }
            List<NamedId> pipeRuleSets = ReadRuleSets(database, civilDocument.Styles.PipeRuleSetStyles.Cast<ObjectId>());
            List<NamedId> structureRuleSets = ReadRuleSets(database, civilDocument.Styles.StructureRuleSetStyles.Cast<ObjectId>());
            string[] pipeChoices = RuleChoiceNames(pipeRuleSets);
            string[] structureChoices = RuleChoiceNames(structureRuleSets);

            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Link Sewer Parts to Surface",
                "Links all selected gravity-network parts to one Civil 3D surface. Pipe and structure rule sets are independent and are applied only after the surface reference is assigned.");
            settings.AddChoice("Surface", "01 Surface", "Reference surface", surfaces[0].Name,
                "Surface assigned to RefSurfaceId on every selected pipe and structure.",
                surfaces.Select(item => item.Name).ToArray());
            settings.AddChoice("PipeRules", "02 Pipe rules", "Apply pipe rules", "Apply selected rule set",
                "Apply the selected pipe rule set after assigning the surface.",
                new[] { "Apply selected rule set", "Do not apply pipe rules" });
            settings.AddChoice("PipeRuleSet", "02 Pipe rules", "Pipe rule set", pipeChoices[0],
                "Named Civil 3D Pipe Rule Set for selected pipes.", pipeChoices);
            settings.AddChoice("StructureRules", "03 Structure rules", "Apply structure rules", "Apply selected rule set",
                "Apply the selected structure rule set after assigning the surface.",
                new[] { "Apply selected rule set", "Do not apply structure rules" });
            settings.AddChoice("StructureRuleSet", "03 Structure rules", "Structure rule set", structureChoices[0],
                "Named Civil 3D Structure Rule Set for selected structures.", structureChoices);
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;

            NamedId surface = FindNamed(surfaces, settings.Text("Surface"));
            if (surface == null) return;
            ObjectId pipeRuleSetId = ResolveRuleChoice(pipeRuleSets, settings.Text("PipeRuleSet"));
            ObjectId structureRuleSetId = ResolveRuleChoice(structureRuleSets, settings.Text("StructureRuleSet"));
            bool applyPipeRules = string.Equals(settings.Text("PipeRules"), "Apply selected rule set", StringComparison.OrdinalIgnoreCase);
            bool applyStructureRules = string.Equals(settings.Text("StructureRules"), "Apply selected rule set", StringComparison.OrdinalIgnoreCase);

            int pipes = 0;
            int structures = 0;
            int pipeRules = 0;
            int structureRules = 0;
            int skipped = 0;
            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in partIds)
                {
                    try
                    {
                        CivilPart part = transaction.GetObject(id, OpenMode.ForWrite, false) as CivilPart;
                        if (part == null || part.IsReferenceObject) { skipped++; continue; }
                        part.RefSurfaceId = surface.Id;
                        CivilPipe pipe = part as CivilPipe;
                        if (pipe != null)
                        {
                            if (!pipeRuleSetId.IsNull) pipe.RuleSetStyleId = pipeRuleSetId;
                            if (applyPipeRules) { try { if (pipe.ApplyRules()) pipeRules++; } catch { } }
                            pipes++;
                            continue;
                        }
                        CivilStructure structure = part as CivilStructure;
                        if (structure != null)
                        {
                            if (!structureRuleSetId.IsNull) structure.RuleSetStyleId = structureRuleSetId;
                            if (applyStructureRules) { try { if (structure.ApplyRules()) structureRules++; } catch { } }
                            structures++;
                            continue;
                        }
                        skipped++;
                    }
                    catch { skipped++; }
                }
                transaction.Commit();
            }

            editor.Regen();
            editor.WriteMessage(
                "\nCE_SEWLINKSURFACE complete. Surface='{0}'; pipes linked={1}; structures linked={2}; pipe rules applied={3}; structure rules applied={4}; skipped={5}.",
                surface.Name, pipes, structures, pipeRules, structureRules, skipped);
        }

        internal static void CreateBranchAlignmentsSafe(Document document, CivilDocument civilDocument)
        {
            if (document == null || civilDocument == null) return;
            Editor editor = document.Editor;
            Database database = document.Database;
            PromptSelectionResult selection = editor.SelectImplied();
            if (selection.Status != PromptStatus.OK || selection.Value == null || selection.Value.Count == 0)
            {
                selection = editor.GetSelection(new PromptSelectionOptions
                {
                    MessageForAdding = "\nSelect one or more CE-sequenced sewer pipes or structures: ",
                    AllowDuplicates = false,
                    RejectObjectsFromNonCurrentSpace = true
                });
            }
            if (selection.Status != PromptStatus.OK || selection.Value == null || selection.Value.Count == 0) return;

            List<ObjectId> networkIds = ReadSelectedNetworkIds(database, selection.Value.GetObjectIds());
            if (networkIds.Count == 0)
            {
                editor.WriteMessage("\nCE_SEWALIGN: select at least one Civil 3D gravity-network pipe or structure.");
                return;
            }

            List<BranchPath> branches;
            try { branches = ReadBranchPaths(database, networkIds); }
            catch (System.Exception exception)
            {
                editor.WriteMessage("\nCE_SEWALIGN cancelled during geometry preflight: {0}", exception.Message);
                return;
            }
            if (branches.Count == 0)
            {
                editor.WriteMessage("\nCE_SEWALIGN: no CE-sequenced P#.## sewer branches were found. Run CE_SEWSEQ first.");
                return;
            }

            int missingSurfaceParts = CountPartsWithoutSurface(database, branches);
            if (missingSurfaceParts > 0)
            {
                editor.WriteMessage(
                    "\nCE_SEWALIGN preflight: {0} branch part(s) have no reference surface. Alignment creation does not require a surface, but use CE_SEWLINKSURFACE before profiles/rule-driven network work.",
                    missingSurfaceParts);
            }

            if (!DisciplineWorkflowDialogs.Confirm(
                "CE Tools - Sewer Alignments",
                "Create/refresh " + branches.Count.ToString(CultureInfo.InvariantCulture) +
                " sewer branch alignment(s)? Each temporary source polyline is committed before Civil 3D alignment creation for crash-safe processing."))
                return;

            int created = 0;
            int failed = 0;
            foreach (BranchPath branch in branches)
            {
                try
                {
                    if (CreateOneAlignmentSafely(database, civilDocument, branch)) created++;
                    else failed++;
                }
                catch (System.Exception exception)
                {
                    failed++;
                    editor.WriteMessage("\nCE_SEWALIGN skipped {0}: {1}", branch.BranchName, exception.Message);
                }
            }
            editor.Regen();
            editor.WriteMessage("\nCE_SEWALIGN complete. Alignments created/refreshed={0}; skipped/failed={1}.", created, failed);
        }

        private static bool CreateOneAlignmentSafely(Database database, CivilDocument civilDocument, BranchPath branch)
        {
            if (branch == null || branch.Points == null || branch.Points.Count < 2) return false;
            ObjectId layerId;
            ObjectId styleId;
            ObjectId labelSetId;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                layerId = EnsureLayer(database, transaction, "CE-SEWER-ALIGNMENT");
                styleId = civilDocument.Styles.AlignmentStyles.Cast<ObjectId>().FirstOrDefault();
                labelSetId = civilDocument.Styles.LabelSetStyles.AlignmentLabelSetStyles.Cast<ObjectId>().FirstOrDefault();
                if (styleId.IsNull || labelSetId.IsNull)
                    throw new InvalidOperationException("Drawing requires at least one Alignment Style and Alignment Label Set Style.");
                transaction.Commit();
            }

            string temporaryName = "CE-TMP-SEWALIGN-" + Guid.NewGuid().ToString("N");
            ObjectId sourcePolylineId = ObjectId.Null;
            // Critical Civil 3D safety boundary: commit the source entity first.
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTable blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
                BlockTableRecord modelSpace = (BlockTableRecord)transaction.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                var polyline = new Polyline(branch.Points.Count);
                polyline.SetDatabaseDefaults(database);
                polyline.LayerId = layerId;
                for (int index = 0; index < branch.Points.Count; index++)
                    polyline.AddVertexAt(index, new Point2d(branch.Points[index].X, branch.Points[index].Y), 0.0, 0.0, 0.0);
                modelSpace.AppendEntity(polyline);
                transaction.AddNewlyCreatedDBObject(polyline, true);
                sourcePolylineId = polyline.ObjectId;
                transaction.Commit();
            }

            ObjectId newAlignmentId = ObjectId.Null;
            try
            {
                var options = new CivilPolylineOptions
                {
                    AddCurvesBetweenTangents = false,
                    EraseExistingEntities = true,
                    PlineId = sourcePolylineId
                };
                newAlignmentId = CivilAlignment.Create(
                    civilDocument, options, temporaryName, ObjectId.Null, layerId, styleId, labelSetId);
            }
            catch
            {
                TryErase(database, sourcePolylineId);
                throw;
            }
            if (newAlignmentId.IsNull) return false;

            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                CivilAlignment created = transaction.GetObject(newAlignmentId, OpenMode.ForWrite, false) as CivilAlignment;
                if (created == null) throw new InvalidOperationException("Civil 3D did not return the created alignment.");

                string desiredName = branch.BranchName;
                ObjectId oldId = FindCeAlignmentByName(civilDocument, transaction, desiredName, newAlignmentId);
                if (!oldId.IsNull)
                {
                    DBObject old = transaction.GetObject(oldId, OpenMode.ForWrite, false);
                    if (old != null && !old.IsErased) old.Erase();
                }
                if (AlignmentNameExists(civilDocument, transaction, desiredName, newAlignmentId))
                    desiredName = branch.NetworkName + " - " + branch.BranchName;
                desiredName = MakeUniqueAlignmentName(civilDocument, transaction, desiredName, newAlignmentId);
                created.Name = desiredName;
                created.Description = "CE sewer alignment - " + branch.BranchName;
                transaction.Commit();
            }
            return true;
        }

        private static ObjectId EnsureStructure(
            CivilNetwork network, Transaction transaction, IDictionary<string, ObjectId> structures,
            PartChoice choice, Point3d point, ObjectId surfaceId, ObjectId ruleSetId,
            bool applyRules, ref int created, ref int rulesApplied)
        {
            string key = NodeKey(point);
            ObjectId existing;
            if (structures.TryGetValue(key, out existing) && !existing.IsNull && !existing.IsErased)
                return existing;
            ObjectId id = ObjectId.Null;
            network.AddStructure(choice.FamilyId, choice.SizeId, point, 0.0, ref id, false);
            if (id.IsNull) return id;
            CivilStructure structure = transaction.GetObject(id, OpenMode.ForWrite, false) as CivilStructure;
            if (structure != null)
            {
                structure.RefSurfaceId = surfaceId;
                if (!ruleSetId.IsNull) structure.RuleSetStyleId = ruleSetId;
                if (applyRules) { try { if (structure.ApplyRules()) rulesApplied++; } catch { } }
            }
            structures[key] = id;
            created++;
            return id;
        }

        private static List<NamedId> ReadSurfaces(Database database, CivilDocument civil)
        {
            var result = new List<NamedId>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in civil.GetSurfaceIds())
                {
                    CivilSurface surface = transaction.GetObject(id, OpenMode.ForRead, false) as CivilSurface;
                    if (surface != null && !string.IsNullOrWhiteSpace(surface.Name)) result.Add(new NamedId(id, surface.Name));
                }
            }
            return result.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        private static List<NamedId> ReadRuleSets(Database database, IEnumerable<ObjectId> ids)
        {
            var result = new List<NamedId>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids)
                {
                    StyleBase style = transaction.GetObject(id, OpenMode.ForRead, false) as StyleBase;
                    if (style != null && !string.IsNullOrWhiteSpace(style.Name)) result.Add(new NamedId(id, style.Name));
                }
            }
            return result.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        private static string[] RuleChoiceNames(IList<NamedId> choices)
        {
            var names = new List<string> { "<Keep part/default rule set>" };
            names.AddRange(choices.Select(item => item.Name));
            return names.ToArray();
        }

        private static ObjectId ResolveRuleChoice(IList<NamedId> choices, string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.StartsWith("<", StringComparison.Ordinal)) return ObjectId.Null;
            NamedId value = FindNamed(choices, name);
            return value == null ? ObjectId.Null : value.Id;
        }

        private static List<NamedId> ReadPartsLists(Database database, CivilDocument civil)
        {
            var result = new List<NamedId>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                var lists = civil.Styles.PartsListSet;
                for (int index = 0; index < lists.Count; index++)
                {
                    ObjectId id = lists[index];
                    PartsList list = transaction.GetObject(id, OpenMode.ForRead, false) as PartsList;
                    if (list != null && !string.IsNullOrWhiteSpace(list.Name)) result.Add(new NamedId(id, list.Name));
                }
            }
            return result.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        private static List<PartChoice> ReadPartChoices(Database database, ObjectId partsListId, DomainType domain, bool skipNull)
        {
            var result = new List<PartChoice>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                PartsList list = transaction.GetObject(partsListId, OpenMode.ForRead, false) as PartsList;
                if (list == null) return result;
                foreach (ObjectId familyId in list.GetPartFamilyIdsByDomain(domain))
                {
                    PartFamily family = transaction.GetObject(familyId, OpenMode.ForRead, false) as PartFamily;
                    if (family == null) continue;
                    if (skipNull && (family.Name ?? string.Empty).IndexOf("null", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    for (int index = 0; index < family.PartSizeCount; index++)
                    {
                        ObjectId sizeId = family[index];
                        PartSize size = transaction.GetObject(sizeId, OpenMode.ForRead, false) as PartSize;
                        if (size == null) continue;
                        string familyName = string.IsNullOrWhiteSpace(family.Name) ? "Part Family" : family.Name;
                        string sizeName = string.IsNullOrWhiteSpace(size.Name) ? "Size " + (index + 1).ToString(CultureInfo.InvariantCulture) : size.Name;
                        result.Add(new PartChoice(familyId, sizeId, familyName + " | " + sizeName));
                    }
                }
            }
            return result.OrderBy(item => item.Label, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        private static List<ObjectId> FilterSupportedSources(Database database, IEnumerable<ObjectId> ids)
        {
            var result = new List<ObjectId>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids.Where(value => !value.IsNull && !value.IsErased).Distinct())
                {
                    DBObject value;
                    try { value = transaction.GetObject(id, OpenMode.ForRead, false); } catch { continue; }
                    if (value is Line || value is Polyline || value is Polyline2d || value is Polyline3d || value is CivilFeatureLine) result.Add(id);
                }
            }
            return result;
        }

        private static List<ObjectId> FilterGravityParts(Database database, IEnumerable<ObjectId> ids)
        {
            var result = new List<ObjectId>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids.Where(value => !value.IsNull && !value.IsErased).Distinct())
                {
                    DBObject value;
                    try { value = transaction.GetObject(id, OpenMode.ForRead, false); } catch { continue; }
                    if (value is CivilPipe || value is CivilStructure) result.Add(id);
                }
            }
            return result;
        }

        private static List<SourcePath> ReadSourcePaths(Database database, IEnumerable<ObjectId> ids)
        {
            var result = new List<SourcePath>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids)
                {
                    DBObject value;
                    try { value = transaction.GetObject(id, OpenMode.ForRead, false); } catch { continue; }
                    List<Point3d> points = ReadPoints(value, transaction);
                    RemoveConsecutiveDuplicates(points);
                    if (points.Count >= 2) result.Add(new SourcePath { SourceId = id, Points = points });
                }
            }
            return result;
        }

        private static List<Point3d> ReadPoints(DBObject value, Transaction transaction)
        {
            var points = new List<Point3d>();
            Line line = value as Line;
            if (line != null) { points.Add(line.StartPoint); points.Add(line.EndPoint); return points; }
            Polyline polyline = value as Polyline;
            if (polyline != null)
            {
                for (int i = 0; i < polyline.NumberOfVertices; i++) points.Add(polyline.GetPoint3dAt(i));
                if (polyline.Closed && points.Count > 2) points.Add(points[0]);
                return points;
            }
            Polyline3d polyline3d = value as Polyline3d;
            if (polyline3d != null)
            {
                foreach (ObjectId vertexId in polyline3d)
                {
                    PolylineVertex3d vertex = transaction.GetObject(vertexId, OpenMode.ForRead, false) as PolylineVertex3d;
                    if (vertex != null) points.Add(vertex.Position);
                }
                if (polyline3d.Closed && points.Count > 2) points.Add(points[0]);
                return points;
            }
            Polyline2d polyline2d = value as Polyline2d;
            if (polyline2d != null)
            {
                foreach (ObjectId vertexId in polyline2d)
                {
                    Vertex2d vertex = transaction.GetObject(vertexId, OpenMode.ForRead, false) as Vertex2d;
                    if (vertex != null) points.Add(vertex.Position);
                }
                if (polyline2d.Closed && points.Count > 2) points.Add(points[0]);
                return points;
            }
            CivilFeatureLine featureLine = value as CivilFeatureLine;
            if (featureLine != null)
            {
                foreach (Point3d point in featureLine.GetPoints(Autodesk.Civil.FeatureLinePointType.AllPoints)) points.Add(point);
            }
            return points;
        }

        private static List<Point3d> Densify(IList<Point3d> points, double maxSpacing)
        {
            var result = new List<Point3d>();
            if (points == null || points.Count == 0) return result;
            result.Add(points[0]);
            for (int i = 0; i < points.Count - 1; i++)
            {
                Point3d a = points[i];
                Point3d b = points[i + 1];
                double length = a.DistanceTo(b);
                int runs = Math.Max(1, (int)Math.Ceiling(length / Math.Max(0.001, maxSpacing)));
                for (int j = 1; j <= runs; j++)
                {
                    double t = j / (double)runs;
                    result.Add(new Point3d(
                        a.X + ((b.X - a.X) * t),
                        a.Y + ((b.Y - a.Y) * t),
                        a.Z + ((b.Z - a.Z) * t)));
                }
            }
            RemoveConsecutiveDuplicates(result);
            return result;
        }

        private static List<ObjectId> ReadSelectedNetworkIds(Database database, IEnumerable<ObjectId> ids)
        {
            var result = new HashSet<ObjectId>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids)
                {
                    DBObject value;
                    try { value = transaction.GetObject(id, OpenMode.ForRead, false); } catch { continue; }
                    CivilPipe pipe = value as CivilPipe;
                    if (pipe != null && !pipe.NetworkId.IsNull) { result.Add(pipe.NetworkId); continue; }
                    CivilStructure structure = value as CivilStructure;
                    if (structure != null && !structure.NetworkId.IsNull) result.Add(structure.NetworkId);
                }
            }
            return result.ToList();
        }

        private static List<BranchPath> ReadBranchPaths(Database database, IEnumerable<ObjectId> networkIds)
        {
            var result = new List<BranchPath>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId networkId in networkIds)
                {
                    CivilNetwork network = transaction.GetObject(networkId, OpenMode.ForRead, false) as CivilNetwork;
                    if (network == null || network.IsReferenceObject) continue;
                    var groups = new Dictionary<int, List<PipeRecord>>();
                    foreach (ObjectId pipeId in network.GetPipeIds())
                    {
                        CivilPipe pipe = transaction.GetObject(pipeId, OpenMode.ForRead, false) as CivilPipe;
                        if (pipe == null || pipe.StartStructureId.IsNull || pipe.EndStructureId.IsNull) continue;
                        int branch;
                        int sequence;
                        if (!TryReadBranch(pipe, out branch, out sequence)) continue;
                        List<PipeRecord> list;
                        if (!groups.TryGetValue(branch, out list)) { list = new List<PipeRecord>(); groups[branch] = list; }
                        list.Add(new PipeRecord(pipeId, pipe.StartStructureId, pipe.EndStructureId, sequence));
                    }
                    foreach (KeyValuePair<int, List<PipeRecord>> group in groups.OrderBy(item => item.Key))
                    {
                        BranchPath path = BuildBranchPath(network.Name, group.Key, group.Value, transaction);
                        if (path != null) result.Add(path);
                    }
                }
            }
            return result;
        }

        private static BranchPath BuildBranchPath(string networkName, int branchNumber, IList<PipeRecord> pipes, Transaction transaction)
        {
            var adjacency = new Dictionary<ObjectId, List<PipeRecord>>();
            foreach (PipeRecord pipe in pipes)
            {
                AddAdjacency(adjacency, pipe.StartStructureId, pipe);
                AddAdjacency(adjacency, pipe.EndStructureId, pipe);
            }
            if (adjacency.Any(item => item.Value.Count > 2)) return null;
            List<ObjectId> endpoints = adjacency.Where(item => item.Value.Count == 1).Select(item => item.Key).ToList();
            if (endpoints.Count != 2) return null;

            ObjectId current = endpoints.OrderBy(id => id.Handle.Value).First();
            var unused = new HashSet<ObjectId>(pipes.Select(item => item.PipeId));
            var orderedPipes = new List<ObjectId>();
            var orderedStructures = new List<ObjectId>();
            while (true)
            {
                orderedStructures.Add(current);
                PipeRecord next = adjacency[current]
                    .Where(item => unused.Contains(item.PipeId))
                    .OrderBy(item => item.Sequence)
                    .ThenBy(item => item.PipeId.Handle.Value)
                    .FirstOrDefault();
                if (next == null) break;
                unused.Remove(next.PipeId);
                orderedPipes.Add(next.PipeId);
                current = next.Other(current);
            }
            if (unused.Count != 0 || orderedPipes.Count == 0) return null;

            var points = new List<Point3d>();
            for (int i = 0; i < orderedPipes.Count; i++)
            {
                CivilPipe pipe = transaction.GetObject(orderedPipes[i], OpenMode.ForRead, false) as CivilPipe;
                if (pipe == null) continue;
                bool forward = pipe.StartStructureId == orderedStructures[i];
                Point3d a = pipe.GetPointAtParam(forward ? 0.0 : 1.0);
                Point3d b = pipe.GetPointAtParam(forward ? 1.0 : 0.0);
                AddPlanPoint(points, a);
                AddPlanPoint(points, b);
            }
            if (points.Count < 2) return null;
            return new BranchPath
            {
                NetworkName = string.IsNullOrWhiteSpace(networkName) ? "Sewer Network" : networkName,
                BranchName = "Branch-" + branchNumber.ToString(CultureInfo.InvariantCulture),
                PipeIds = orderedPipes,
                StructureIds = orderedStructures,
                Points = points
            };
        }

        private static bool TryReadBranch(CivilPipe pipe, out int branch, out int sequence)
        {
            branch = 0;
            sequence = int.MaxValue;
            Match match = PipeNamePattern.Match(pipe.Name ?? string.Empty);
            if (match.Success)
                return int.TryParse(match.Groups["branch"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out branch) &&
                       int.TryParse(match.Groups["sequence"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out sequence);
            Match description = BranchNamePattern.Match(pipe.Description ?? string.Empty);
            return description.Success && int.TryParse(description.Groups["branch"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out branch);
        }

        private static int CountPartsWithoutSurface(Database database, IEnumerable<BranchPath> branches)
        {
            var ids = new HashSet<ObjectId>();
            foreach (BranchPath branch in branches)
            {
                foreach (ObjectId id in branch.PipeIds) ids.Add(id);
                foreach (ObjectId id in branch.StructureIds) ids.Add(id);
            }
            int count = 0;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids)
                {
                    CivilPart part;
                    try { part = transaction.GetObject(id, OpenMode.ForRead, false) as CivilPart; } catch { continue; }
                    if (part != null && part.RefSurfaceId.IsNull) count++;
                }
            }
            return count;
        }

        private static ObjectId EnsureLayer(Database database, Transaction transaction, string name)
        {
            LayerTable table = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
            if (table.Has(name)) return table[name];
            table.UpgradeOpen();
            var layer = new LayerTableRecord { Name = name };
            ObjectId id = table.Add(layer);
            transaction.AddNewlyCreatedDBObject(layer, true);
            return id;
        }

        private static ObjectId FindCeAlignmentByName(CivilDocument civil, Transaction transaction, string name, ObjectId exceptId)
        {
            foreach (ObjectId id in civil.GetAlignmentIds())
            {
                if (id == exceptId) continue;
                CivilAlignment alignment = transaction.GetObject(id, OpenMode.ForRead, false) as CivilAlignment;
                if (alignment != null && string.Equals(alignment.Name, name, StringComparison.OrdinalIgnoreCase) &&
                    (alignment.Description ?? string.Empty).StartsWith("CE sewer alignment", StringComparison.OrdinalIgnoreCase)) return id;
            }
            return ObjectId.Null;
        }

        private static bool AlignmentNameExists(CivilDocument civil, Transaction transaction, string name, ObjectId exceptId)
        {
            foreach (ObjectId id in civil.GetAlignmentIds())
            {
                if (id == exceptId) continue;
                CivilAlignment alignment = transaction.GetObject(id, OpenMode.ForRead, false) as CivilAlignment;
                if (alignment != null && string.Equals(alignment.Name, name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static string MakeUniqueAlignmentName(CivilDocument civil, Transaction transaction, string baseName, ObjectId exceptId)
        {
            string value = string.IsNullOrWhiteSpace(baseName) ? "Sewer Alignment" : baseName;
            if (!AlignmentNameExists(civil, transaction, value, exceptId)) return value;
            for (int index = 2; index < 10000; index++)
            {
                string candidate = value + " (" + index.ToString(CultureInfo.InvariantCulture) + ")";
                if (!AlignmentNameExists(civil, transaction, candidate, exceptId)) return candidate;
            }
            return value + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        private static void TryErase(Database database, ObjectId id)
        {
            if (id.IsNull || id.IsErased) return;
            try
            {
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    DBObject value = transaction.GetObject(id, OpenMode.ForWrite, false);
                    if (value != null && !value.IsErased) value.Erase();
                    transaction.Commit();
                }
            }
            catch { }
        }

        private static void AddAdjacency(IDictionary<ObjectId, List<PipeRecord>> map, ObjectId id, PipeRecord pipe)
        {
            List<PipeRecord> list;
            if (!map.TryGetValue(id, out list)) { list = new List<PipeRecord>(); map[id] = list; }
            list.Add(pipe);
        }

        private static void AddPlanPoint(ICollection<Point3d> points, Point3d point)
        {
            Point3d plan = new Point3d(point.X, point.Y, 0.0);
            if (points.Count == 0 || points.Last().DistanceTo(plan) > PointTolerance) points.Add(plan);
        }

        private static void RemoveConsecutiveDuplicates(IList<Point3d> points)
        {
            for (int i = points.Count - 1; i > 0; i--)
                if (points[i].DistanceTo(points[i - 1]) <= PointTolerance) points.RemoveAt(i);
        }

        private static string NodeKey(Point3d point)
        {
            long x = (long)Math.Round(point.X / PointTolerance);
            long y = (long)Math.Round(point.Y / PointTolerance);
            return x.ToString(CultureInfo.InvariantCulture) + "|" + y.ToString(CultureInfo.InvariantCulture);
        }

        private static string PreferredName(IList<NamedId> choices, IEnumerable<string> keywords)
        {
            foreach (string keyword in keywords)
            {
                NamedId value = choices.FirstOrDefault(item => item.Name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0);
                if (value != null) return value.Name;
            }
            return choices.Count == 0 ? string.Empty : choices[0].Name;
        }

        private static NamedId FindNamed(IEnumerable<NamedId> choices, string name)
        {
            return choices.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        private static string PreferredPart(IList<PartChoice> choices, IEnumerable<string> keywords)
        {
            foreach (string keyword in keywords)
            {
                PartChoice value = choices.FirstOrDefault(item => item.Label.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0);
                if (value != null) return value.Label;
            }
            return choices[0].Label;
        }

        private static PartChoice FindPart(IEnumerable<PartChoice> choices, string label)
        {
            return choices.FirstOrDefault(item => string.Equals(item.Label, label, StringComparison.OrdinalIgnoreCase));
        }

        private sealed class NamedId
        {
            internal NamedId(ObjectId id, string name) { Id = id; Name = name ?? string.Empty; }
            internal ObjectId Id;
            internal string Name;
        }

        private sealed class PartChoice
        {
            internal PartChoice(ObjectId familyId, ObjectId sizeId, string label) { FamilyId = familyId; SizeId = sizeId; Label = label; }
            internal ObjectId FamilyId;
            internal ObjectId SizeId;
            internal string Label;
        }

        private sealed class SourcePath
        {
            internal ObjectId SourceId;
            internal List<Point3d> Points = new List<Point3d>();
        }

        private sealed class PipeRecord
        {
            internal PipeRecord(ObjectId pipeId, ObjectId startId, ObjectId endId, int sequence)
            { PipeId = pipeId; StartStructureId = startId; EndStructureId = endId; Sequence = sequence; }
            internal ObjectId PipeId;
            internal ObjectId StartStructureId;
            internal ObjectId EndStructureId;
            internal int Sequence;
            internal ObjectId Other(ObjectId id) { return id == StartStructureId ? EndStructureId : StartStructureId; }
        }

        private sealed class BranchPath
        {
            internal string NetworkName;
            internal string BranchName;
            internal List<ObjectId> PipeIds = new List<ObjectId>();
            internal List<ObjectId> StructureIds = new List<ObjectId>();
            internal List<Point3d> Points = new List<Point3d>();
        }
    }

    public sealed class September09SewerSurfaceRulesCommands
    {
        [CommandMethod("CE_TOOLS", "CE_SEWLINKSURFACE", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void LinkSewerPartsToSurface()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (document == null || civilDocument == null) return;
            September09SewerSurfaceRulesRuntime.LinkExistingPartsToSurface(document, civilDocument);
        }
    }
}

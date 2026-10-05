using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using CETools.Core;
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
                foreach (ObjectId sourceId in sourceIds) NetworkSourceMarker.Mark(database, sourceId, "Sewer");

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
            List<ObjectId> partIds;
            var scope = new PromptKeywordOptions(
                "\nSewer surface/rules scope [AllNetworkParts/Select] <AllNetworkParts>: ")
            {
                AllowNone = true
            };
            scope.Keywords.Add("AllNetworkParts");
            scope.Keywords.Add("Select");
            PromptResult scopeResult = editor.GetKeywords(scope);
            bool selectManually =
                scopeResult.Status == PromptStatus.OK &&
                string.Equals(
                    scopeResult.StringResult,
                    "Select",
                    StringComparison.OrdinalIgnoreCase);

            if (selectManually)
            {
                PromptSelectionResult selection = editor.GetSelection(
                    new PromptSelectionOptions
                    {
                        MessageForAdding = "\nSelect multiple sewer pipes and structures to link to one surface: ",
                        MessageForRemoval = "\nRemove network parts: ",
                        AllowDuplicates = false,
                        RejectObjectsFromNonCurrentSpace = true
                    });
                if (selection.Status != PromptStatus.OK ||
                    selection.Value == null ||
                    selection.Value.Count == 0)
                    return;
                partIds = FilterGravityParts(
                    database,
                    selection.Value.GetObjectIds());
            }
            else
            {
                partIds = ReadAllGravityParts(database);
                editor.WriteMessage(
                    "\nCE_SEWLINKSURFACE: all gravity-network pipes/structures selected. Parts={0}.",
                    partIds.Count);
            }
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

            const string gravityMode = "Gravity - constant downhill slope by branch";
            const string groundMode = "Follow natural ground at specified depth";
            const string civilMode = "Use Civil 3D rule sets";

            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Link Sewer Parts to Surface",
                "Links selected gravity-network parts to one Civil 3D surface. Gravity mode continues each outgoing pipe from the lowest incoming pipe invert at its manhole, then falls at the specified slope even when natural ground rises. Natural-ground mode is a separate option for selected pipes that must remain at a specified cover depth.");
            settings.AddChoice("Surface", "01 Surface", "Reference surface", surfaces[0].Name,
                "Surface assigned to RefSurfaceId on every selected pipe and structure.",
                surfaces.Select(item => item.Name).ToArray());
            settings.AddChoice("RuleMode", "02 Pipe geometry", "Pipe elevation mode", gravityMode,
                "Gravity mode uses the lowest incoming invert, including other branches and unselected incoming pipes. Natural-ground mode follows the surface at the specified crown depth. Civil 3D mode uses the selected installed rule set.",
                new[] { gravityMode, groundMode, civilMode });
            settings.AddPositiveDouble("MinStartSlope", "02 Pipe geometry", "Starting minimum pipe slope (%)", 1.0,
                "Minimum downhill slope for the first pipe of every sequenced branch (P#.1). CE Tools may steepen it only as needed to respect the selected surface and minimum depth/cover, never beyond Maximum pipe slope.");
            settings.AddPositiveDouble("MinSlope", "02 Pipe geometry", "Minimum downhill pipe slope (%)", 0.65,
                "Normal downhill slope for P#.2 onward. CE Tools keeps this exact minimum grade unless a lower incoming connection or the specified minimum depth/cover requires the pipe to steepen. It never exceeds Maximum pipe slope.");
            settings.AddPositiveDouble("GroundDepth", "02 Pipe geometry", "Natural-ground depth to pipe crown (m)", 0.834,
                "Used only in Follow natural ground mode. Each selected pipe crown is kept this depth below the selected surface at both endpoints.");
            settings.AddPositiveDouble("MaxSlope", "02 Pipe geometry", "Maximum pipe slope (%)", 2.5,
                "Hard maximum downhill slope in Gravity mode. CE Tools only steepens above the minimum when required by a lower incoming invert or minimum depth/cover, and never beyond this value. Impossible combinations are reported.");
            settings.AddPositiveDouble("MinCover", "02 Pipe geometry", "Minimum depth / cover at structures (m)", 1.0,
                "Minimum depth from the selected surface to pipe crown at each connected structure in Gravity mode. This constraint is used when choosing the actual pipe slope between the minimum and maximum values.");
            settings.AddPositiveDouble("MaxCover", "02 Pipe geometry", "Maximum cover (m)", 10.0,
                "Cover constraint in Gravity mode. If the exact downhill grade and cover range cannot both be satisfied, CE Tools preserves gravity slope and reports a warning instead of making a pipe run uphill.");
            settings.AddChoice("RaiseDeepRuns", "02 Pipe geometry", "Raise pipes when structures become too deep", "Yes",
                "When enabled, CE Tools first checks the existing gravity branch depth. A branch is raised only when its deepest pipe crown exceeds the trigger depth, and is then lifted toward the requested post-raise depth while preserving downhill slope, connections and the normal minimum cover.",
                new[] { "Yes", "No" });
            settings.AddPositiveDouble("DeepRaiseThreshold", "02 Pipe geometry", "Deep structure trigger depth (m)", 3.5,
                "Actual manhole-depth trigger. CE Tools estimates rim-to-sump depth from the selected surface, connected pipe invert and Sump depth. If any structure in the branch exceeds this value, the branch is raised where gravity continuity allows.");
            settings.AddPositiveDouble("DeepRaiseTarget", "02 Pipe geometry", "Minimum depth after raising (m)", 1.0,
                "Target pipe-crown cover after a deep-structure raise. This is clamped to the normal Minimum depth / cover setting; pipe slopes, incoming connections and maximum slope still take precedence.");
            settings.AddPositiveDouble("MinLength", "02 Pipe geometry", "Minimum pipe length (m)", 2.440,
                "Short pipes are reported because structure positions are preserved.");
            settings.AddPositiveDouble("MaxLength", "02 Pipe geometry", "Maximum pipe length (m)", 100.0,
                "Long pipes are reported because adding structures is a topology edit.");
            settings.AddChoice("PipeRules", "02 Pipe rules", "Apply pipe rules", "Apply selected rule set",
                "Used only in Civil 3D rule-set mode.",
                new[] { "Apply selected rule set", "Do not apply pipe rules" });
            settings.AddChoice("PipeRuleSet", "02 Pipe rules", "Pipe rule set", pipeChoices[0],
                "Named Civil 3D Pipe Rule Set for selected pipes.", pipeChoices);
            settings.AddChoice("StructureRules", "03 Structure rules", "Apply structure rules", "Apply selected rule set",
                "Civil 3D structure rules are used only in Civil 3D rule-set mode. In the two CE geometry modes, rims/sumps are updated without changing the pipe grades.",
                new[] { "Apply selected rule set", "Do not apply structure rules" });
            settings.AddChoice("StructureRuleSet", "03 Structure rules", "Structure rule set", structureChoices[0],
                "Named Civil 3D Structure Rule Set for selected structures.", structureChoices);
            settings.AddDouble("SumpDepth", "03 Structure rules", "Sump depth (m)", 0.500,
                "Manual depth below the lowest connected pipe invert; absolute elevation control is used when Civil 3D exposes it.");
            settings.AddChoice("DropHandling", "03 Structure rules", "Drop handling", "Report only",
                "In CE gravity/ground-follow modes pipe endpoint grades are preserved. Drop conditions are reported, not corrected by moving pipe ends.",
                new[] { "Report only", "Apply drop corrections" });
            settings.AddChoice("DropReference", "03 Structure rules", "Drop reference location", "Crown",
                "Reference for structure drop checks and corrections.", new[] { "Crown", "Invert" });
            settings.AddPositiveDouble("DropValue", "03 Structure rules", "Minimum drop value (m)", 0.050,
                "Minimum desired drop through a structure.");
            settings.AddPositiveDouble("MaxDrop", "03 Structure rules", "Maximum drop value (m)", 3.0,
                "Maximum allowed drop; larger values are reported.");
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;

            NamedId surface = FindNamed(surfaces, settings.Text("Surface"));
            if (surface == null) return;
            ObjectId pipeRuleSetId = ResolveRuleChoice(pipeRuleSets, settings.Text("PipeRuleSet"));
            ObjectId structureRuleSetId = ResolveRuleChoice(structureRuleSets, settings.Text("StructureRuleSet"));
            bool applyPipeRules = string.Equals(settings.Text("PipeRules"), "Apply selected rule set", StringComparison.OrdinalIgnoreCase);
            bool applyStructureRules = string.Equals(settings.Text("StructureRules"), "Apply selected rule set", StringComparison.OrdinalIgnoreCase);
            string mode = settings.Text("RuleMode");
            bool gravity = string.Equals(mode, gravityMode, StringComparison.OrdinalIgnoreCase);
            bool followGround = string.Equals(mode, groundMode, StringComparison.OrdinalIgnoreCase);
            bool civilRules = string.Equals(mode, civilMode, StringComparison.OrdinalIgnoreCase);

            int pipes = 0;
            int structures = 0;
            int pipeRules = 0;
            int structureRules = 0;
            int manualAdjusted = 0;
            int manualWarnings = 0;
            int skipped = 0;
            var connectionStructures = new HashSet<ObjectId>();

            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                CivilSurface selectedSurface = transaction.GetObject(surface.Id, OpenMode.ForRead, false) as CivilSurface;
                if (selectedSurface == null)
                {
                    editor.WriteMessage("\nCE_SEWLINKSURFACE: selected Civil 3D surface is unavailable.");
                    return;
                }

                var selectedPipes = new List<CivilPipe>();
                var selectedStructures = new List<CivilStructure>();
                foreach (ObjectId id in partIds)
                {
                    try
                    {
                        CivilPart part = transaction.GetObject(id, OpenMode.ForWrite, false) as CivilPart;
                        if (part == null || part.IsReferenceObject) { skipped++; continue; }
                        part.RefSurfaceId = surface.Id;
                        SewerManholeConnectionCommands.AddSelection(part, connectionStructures);
                        CivilPipe pipe = part as CivilPipe;
                        if (pipe != null)
                        {
                            selectedPipes.Add(pipe);
                            pipes++;
                            continue;
                        }
                        CivilStructure structure = part as CivilStructure;
                        if (structure != null)
                        {
                            selectedStructures.Add(structure);
                            structures++;
                            continue;
                        }
                        skipped++;
                    }
                    catch { skipped++; }
                }

                if (gravity)
                {
                    try
                    {
                        ApplyConstantGravitySlopes(
                            selectedPipes,
                            selectedSurface,
                            transaction,
                            settings,
                            ref manualAdjusted,
                            ref manualWarnings);
                    }
                    catch (System.Exception exception)
                    {
                        editor.WriteMessage("\nGravity grading cancelled; no changes were saved: {0}", exception.Message);
                        return;
                    }
                }
                else if (followGround)
                {
                    foreach (CivilPipe pipe in selectedPipes)
                        ApplyNaturalGroundDepth(
                            pipe,
                            selectedSurface,
                            settings,
                            ref manualAdjusted,
                            ref manualWarnings);
                }
                else if (civilRules)
                {
                    foreach (CivilPipe pipe in selectedPipes)
                    {
                        if (!pipeRuleSetId.IsNull) pipe.RuleSetStyleId = pipeRuleSetId;
                        if (applyPipeRules)
                        {
                            try { if (pipe.ApplyRules()) pipeRules++; }
                            catch { manualWarnings++; }
                        }
                    }
                }

                foreach (CivilStructure structure in selectedStructures)
                {
                    if (civilRules)
                    {
                        if (!structureRuleSetId.IsNull) structure.RuleSetStyleId = structureRuleSetId;
                        if (applyStructureRules)
                        {
                            try { if (structure.ApplyRules()) structureRules++; }
                            catch { manualWarnings++; }
                        }
                    }
                    else
                    {
                        // Update rim/sump and report drop/cover issues only.
                        // Preserve the pipe endpoint elevations established above.
                        ApplyManualStructureRules(
                            structure,
                            transaction,
                            selectedSurface,
                            settings,
                            ref manualAdjusted,
                            ref manualWarnings,
                            true);
                    }
                }

                transaction.Commit();
            }

            editor.Regen();
            editor.WriteMessage(
                "\nCE_SEWLINKSURFACE complete. Mode='{0}'; surface='{1}'; pipes linked={2}; structures linked={3}; pipe rules applied={4}; structure rules applied={5}; geometry/structure adjustments={6}; review warnings={7}; skipped={8}.",
                mode, surface.Name, pipes, structures, pipeRules, structureRules, manualAdjusted, manualWarnings, skipped);
            if (gravity)
                editor.WriteMessage(
                    "\nGravity mode continues from the lowest incoming invert at each manhole, including other branches. Exact downhill slopes are preserved; cover conflicts and unresolved connections are reported.");
            else if (followGround)
                editor.WriteMessage(
                    "\nNatural-ground mode follows the selected surface at the specified depth to pipe crown; uphill ground is therefore allowed in this mode by design.");
            using (document.LockDocument())
                SewerManholeConnectionCommands.EnableAndReport(document, connectionStructures, gravity);
        }

        private static List<ObjectId> ReadAllGravityParts(
            Database database)
        {
            var result = new List<ObjectId>();
            if (database == null) return result;
            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                BlockTableRecord model = transaction.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(database),
                    OpenMode.ForRead,
                    false) as BlockTableRecord;
                if (model == null) return result;
                foreach (ObjectId id in model)
                {
                    CivilPart part = null;
                    try
                    {
                        part = transaction.GetObject(
                            id,
                            OpenMode.ForRead,
                            false) as CivilPart;
                    }
                    catch { }
                    if (!(part is CivilPipe) &&
                        !(part is CivilStructure))
                        continue;

                    string partName = string.Empty;
                    try { partName = part.Name ?? string.Empty; }
                    catch { }
                    bool sequencedPipe =
                        part is CivilPipe &&
                        PipeNamePattern.IsMatch(partName);
                    bool sequencedStructure =
                        part is CivilStructure &&
                        Regex.IsMatch(
                            partName,
                            @"^MH\d+\.\d+$",
                            RegexOptions.IgnoreCase |
                            RegexOptions.CultureInvariant);
                    if (sequencedPipe || sequencedStructure)
                        result.Add(id);
                }
            }
            return result;
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

        internal static bool ApplyManualRulesToNetwork(
            Document document,
            ObjectId networkId,
            ObjectId surfaceId,
            ProductionSettingsDialogModel settings,
            out int pipes,
            out int structures,
            out int warnings,
            out string error,
            out List<IList<string>> rows)
        {
            pipes = 0;
            structures = 0;
            warnings = 0;
            error = string.Empty;
            rows = new List<IList<string>>();
            if (document == null || networkId.IsNull || surfaceId.IsNull || settings == null)
            { error = "A network, surface and manual-rule settings are required."; return false; }
            try
            {
                using (DocumentLock documentLock = document.LockDocument())
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    CivilNetwork network = transaction.GetObject(networkId, OpenMode.ForRead, false) as CivilNetwork;
                    CivilSurface surface = transaction.GetObject(surfaceId, OpenMode.ForRead, false) as CivilSurface;
                    if (network == null || surface == null) throw new InvalidOperationException("The selected network or surface is no longer available.");
                    foreach (ObjectId pipeId in network.GetPipeIds())
                    {
                        CivilPipe pipe = transaction.GetObject(pipeId, OpenMode.ForWrite, false) as CivilPipe;
                        if (pipe == null || pipe.IsReferenceObject) continue;
                        pipe.RefSurfaceId = surfaceId;
                        int beforeWarnings = warnings;
                        int adjusted = 0;
                        ApplyManualPipeRules(pipe, surface, settings, ref adjusted, ref warnings);
                        pipes++;
                        rows.Add(new List<string>
                        {
                            "Pipe", pipe.Name ?? pipeId.Handle.ToString(), "Manual CE values",
                            surface.Name, adjusted > 0 ? (warnings == beforeWarnings ? "Applied" : "Applied; review warning") : "Review required"
                        });
                    }
                    foreach (ObjectId structureId in network.GetStructureIds())
                    {
                        CivilStructure structure = transaction.GetObject(structureId, OpenMode.ForWrite, false) as CivilStructure;
                        if (structure == null || structure.IsReferenceObject) continue;
                        structure.RefSurfaceId = surfaceId;
                        int beforeWarnings = warnings;
                        int adjusted = 0;
                        ApplyManualStructureRules(structure, transaction, surface, settings, ref adjusted, ref warnings);
                        structures++;
                        rows.Add(new List<string>
                        {
                            "Structure", structure.Name ?? structureId.Handle.ToString(), "Manual CE values",
                            surface.Name, adjusted > 0 ? (warnings == beforeWarnings ? "Applied" : "Applied; review warning") : "Review required"
                        });
                    }
                    transaction.Commit();
                }
                return true;
            }
            catch (System.Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }

        private static void ApplyConstantGravitySlopes(
            IList<CivilPipe> pipes,
            CivilSurface surface,
            Transaction transaction,
            ProductionSettingsDialogModel settings,
            ref int adjusted,
            ref int warnings)
        {
            if (pipes == null || surface == null || transaction == null) return;

            var sequenced = new List<GravityPipeSeed>();
            var segments = new List<List<GravityPipeStep>>();
            foreach (CivilPipe pipe in pipes)
            {
                if (pipe == null) continue;
                Match match = PipeNamePattern.Match(pipe.Name ?? string.Empty);
                int branch;
                int sequence;
                if (!match.Success ||
                    !int.TryParse(match.Groups["branch"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out branch) ||
                    !int.TryParse(match.Groups["sequence"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out sequence))
                {
                    // Gravity direction must come from the CE branch sequence, not
                    // from natural-ground elevation. Leave unsequenced pipes alone
                    // rather than guessing a direction that could make them uphill.
                    warnings++;
                    continue;
                }
                sequenced.Add(new GravityPipeSeed
                {
                    Pipe = pipe,
                    Branch = branch,
                    Sequence = sequence
                });
            }

            foreach (var branchGroup in sequenced
                .GroupBy(item => new { item.Pipe.NetworkId, item.Branch })
                .OrderBy(item => item.Key.NetworkId.Handle.Value).ThenBy(item => item.Key.Branch))
            {
                var segment = new List<GravityPipeStep>();
                ObjectId previousDownstream = ObjectId.Null;
                int previousSequence = int.MinValue;

                foreach (GravityPipeSeed seed in branchGroup
                    .OrderBy(item => item.Sequence)
                    .ThenBy(item => item.Pipe.ObjectId.Handle.Value))
                {
                    if (segment.Count > 0 && seed.Sequence != previousSequence + 1)
                    {
                        segments.Add(segment.ToList());
                        segment.Clear();
                        previousDownstream = ObjectId.Null;
                    }

                    GravityPipeStep step;
                    if (!TryBuildGravityStep(
                            seed,
                            previousDownstream,
                            transaction,
                            surface,
                            settings,
                            out step))
                    {
                        if (segment.Count > 0)
                        {
                            segments.Add(segment.ToList());
                            segment.Clear();
                        }
                        previousDownstream = ObjectId.Null;
                        warnings++;
                        previousSequence = seed.Sequence;
                        continue;
                    }

                    // If the next sequential pipe is not actually connected to the
                    // previous downstream manhole, start a new independent segment.
                    if (segment.Count > 0 &&
                        !previousDownstream.IsNull &&
                        step.UpstreamStructureId != previousDownstream)
                    {
                        segments.Add(segment.ToList());
                        segment.Clear();
                        previousDownstream = ObjectId.Null;

                        if (!TryBuildGravityStep(
                                seed,
                                ObjectId.Null,
                                transaction,
                                surface,
                                settings,
                                out step))
                        {
                            warnings++;
                            previousSequence = seed.Sequence;
                            continue;
                        }
                    }

                    segment.Add(step);
                    previousDownstream = step.DownstreamStructureId;
                    previousSequence = seed.Sequence;
                }

                if (segment.Count > 0)
                    segments.Add(segment.ToList());
            }

            foreach (var segment in segments)
                PrepareGravitySegment(segment, settings, ref warnings);
            ApplyGravitySegment(segments.SelectMany(segment => segment).ToList(),
                transaction, settings, ref adjusted, ref warnings);
        }

        private static bool TryBuildGravityStep(
            GravityPipeSeed seed,
            ObjectId previousDownstream,
            Transaction transaction,
            CivilSurface surface,
            ProductionSettingsDialogModel settings,
            out GravityPipeStep step)
        {
            step = null;
            if (seed == null || seed.Pipe == null) return false;

            Point3d start;
            Point3d end;
            if (!TryReadPoint(seed.Pipe, "StartPoint", out start) ||
                !TryReadPoint(seed.Pipe, "EndPoint", out end))
                return false;

            double run = seed.Pipe.Length2DCenterToCenter;
            if (!Finite(run) || run <= PointTolerance) return false;

            bool forward = true;
            bool directionResolved = false;
            string expectedUpstream = "MH" +
                seed.Branch.ToString(CultureInfo.InvariantCulture) + "." +
                seed.Sequence.ToString(CultureInfo.InvariantCulture);

            string startStructureName = ReadStructureName(
                seed.Pipe.StartStructureId,
                transaction);
            string endStructureName = ReadStructureName(
                seed.Pipe.EndStructureId,
                transaction);

            if (string.Equals(startStructureName, expectedUpstream, StringComparison.OrdinalIgnoreCase))
            {
                forward = true;
                directionResolved = true;
            }
            else if (string.Equals(endStructureName, expectedUpstream, StringComparison.OrdinalIgnoreCase))
            {
                forward = false;
                directionResolved = true;
            }
            else if (!previousDownstream.IsNull)
            {
                if (seed.Pipe.StartStructureId == previousDownstream)
                {
                    forward = true;
                    directionResolved = true;
                }
                else if (seed.Pipe.EndStructureId == previousDownstream)
                {
                    forward = false;
                    directionResolved = true;
                }
            }

            if (!directionResolved)
            {
                // Retain explicit flow direction for legacy manhole names.
                // A flat/unknown connection is reported instead of guessed.
                if (!SewerPipeConnections.TryForward(seed.Pipe, transaction, out forward))
                    return false;
            }

            Point3d upstream = forward ? start : end;
            Point3d downstream = forward ? end : start;
            ObjectId upstreamStructure = forward
                ? seed.Pipe.StartStructureId
                : seed.Pipe.EndStructureId;
            ObjectId downstreamStructure = forward
                ? seed.Pipe.EndStructureId
                : seed.Pipe.StartStructureId;

            double upstreamGround;
            double downstreamGround;
            try
            {
                upstreamGround = surface.FindElevationAtXY(upstream.X, upstream.Y);
                downstreamGround = surface.FindElevationAtXY(downstream.X, downstream.Y);
            }
            catch
            {
                return false;
            }
            if (!Finite(upstreamGround) || !Finite(downstreamGround))
                return false;

            double startSlope = Math.Abs(settings.Double("MinStartSlope", 1.0)) / 100.0;
            double regularSlope = Math.Abs(settings.Double("MinSlope", 0.65)) / 100.0;
            double slope = seed.Sequence == 1 ? startSlope : regularSlope;
            if (slope <= 0.0) return false;

            double diameter = seed.Pipe.OuterHeight;
            double innerHeight = seed.Pipe.InnerHeight;
            if (!Finite(diameter) || !Finite(innerHeight) || diameter <= 0 || innerHeight <= 0)
                return false;

            step = new GravityPipeStep
            {
                Pipe = seed.Pipe,
                Forward = forward,
                Upstream = upstream,
                Downstream = downstream,
                UpstreamStructureId = upstreamStructure,
                DownstreamStructureId = downstreamStructure,
                Run = run,
                Radius = diameter * 0.5,
                InnerRadius = innerHeight * 0.5,
                MinimumSlope = slope,
                Slope = slope,
                UpstreamGround = upstreamGround,
                DownstreamGround = downstreamGround
            };
            return true;
        }

        private static void PrepareGravitySegment(
            IList<GravityPipeStep> segment,
            ProductionSettingsDialogModel settings,
            ref int warnings)
        {
            if (segment == null || segment.Count == 0) return;

            bool raiseDeepRuns = string.Equals(
                settings.Text("RaiseDeepRuns"),
                "Yes",
                StringComparison.OrdinalIgnoreCase);
            double minimumCover = Math.Max(
                0.0,
                settings.Double("MinCover", 1.0));
            double maximumCover = Math.Max(
                minimumCover,
                settings.Double("MaxCover", 10.0));
            double deepRaiseTrigger = Math.Max(
                minimumCover,
                settings.Double("DeepRaiseThreshold", 3.5));
            double deepRaiseTarget = Math.Max(
                minimumCover,
                settings.Double("DeepRaiseTarget", 1.0));
            double sumpDepth = Math.Max(
                0.0,
                settings.Double("SumpDepth", 0.500));
            double maximumSlope = Math.Abs(
                settings.Double("MaxSlope", 2.5)) / 100.0;
            double minLength = Math.Max(
                0.0,
                settings.Double("MinLength", 2.440));
            double maxLength = Math.Max(
                minLength,
                settings.Double("MaxLength", 100.0));

            if (maximumSlope <= 0.0)
                throw new InvalidOperationException(
                    "Maximum pipe slope must be greater than zero.");

            // Start every branch pipe at its configured minimum grade.
            // Do NOT follow the natural-ground fall merely because the surface is
            // steeper. A pipe is only allowed to steepen later when a fixed/lower
            // incoming invert or a minimum-cover constraint would otherwise make
            // the downstream structure too shallow.
            foreach (GravityPipeStep step in segment)
            {
                double minimumSlope = Math.Max(
                    0.0,
                    step.MinimumSlope);
                if (minimumSlope > maximumSlope + 1e-9)
                    throw new InvalidOperationException(
                        "Minimum pipe slope exceeds Maximum pipe slope for " +
                        (step.Pipe == null ? "a sewer pipe" : step.Pipe.Name) + ".");

                step.Slope = minimumSlope;

                if (step.Run < minLength ||
                    step.Run > maxLength)
                    warnings++;
            }

            double cumulativeDrop = 0.0;
            double rootMinimum = double.NegativeInfinity;
            double rootMaximum = double.PositiveInfinity;

            foreach (GravityPipeStep step in segment)
            {
                step.CumulativeStartDrop = cumulativeDrop;
                step.CumulativeEndDrop =
                    cumulativeDrop +
                    step.Slope * step.Run;
                cumulativeDrop = step.CumulativeEndDrop;

                double upstreamMinimum =
                    step.UpstreamGround -
                    maximumCover -
                    step.Radius -
                    step.InnerRadius +
                    step.CumulativeStartDrop;
                double upstreamMaximum =
                    step.UpstreamGround -
                    minimumCover -
                    step.Radius -
                    step.InnerRadius +
                    step.CumulativeStartDrop;
                double downstreamMinimum =
                    step.DownstreamGround -
                    maximumCover -
                    step.Radius -
                    step.InnerRadius +
                    step.CumulativeEndDrop;
                double downstreamMaximum =
                    step.DownstreamGround -
                    minimumCover -
                    step.Radius -
                    step.InnerRadius +
                    step.CumulativeEndDrop;

                rootMinimum = Math.Max(
                    rootMinimum,
                    Math.Max(
                        upstreamMinimum,
                        downstreamMinimum));
                rootMaximum = Math.Min(
                    rootMaximum,
                    Math.Min(
                        upstreamMaximum,
                        downstreamMaximum));
            }

            GravityPipeStep first = segment[0];
            double rootElevation;
            if (rootMinimum <= rootMaximum)
            {
                // Normal gravity mode uses the shallowest valid root. Deep-run
                // raising is different: preserve the existing branch root until
                // the configured trigger is actually exceeded, then lift the
                // complete branch toward the requested post-raise cover.
                rootElevation = rootMaximum;

                if (raiseDeepRuns)
                {
                    double existingRoot =
                        first.Upstream.Z -
                        first.InnerRadius;
                    if (Finite(existingRoot))
                    {
                        double boundedExistingRoot = Math.Max(
                            rootMinimum,
                            Math.Min(
                                rootMaximum,
                                existingRoot));
                        rootElevation = boundedExistingRoot;

                        double deepestExistingStructureDepth =
                            DeepestSegmentStructureDepth(
                                segment,
                                boundedExistingRoot,
                                sumpDepth);
                        if (deepestExistingStructureDepth >
                            deepRaiseTrigger + 1e-6)
                        {
                            // The trigger is a MANHOLE depth, matching the D= value
                            // shown in profile views. Once triggered, raise the branch
                            // toward the requested pipe-crown cover target.
                            double deepestExistingCover =
                                DeepestSegmentCover(
                                    segment,
                                    boundedExistingRoot);
                            double requestedRaise = Math.Max(
                                0.0,
                                deepestExistingCover -
                                deepRaiseTarget);
                            rootElevation = Math.Min(
                                rootMaximum,
                                boundedExistingRoot +
                                requestedRaise);

                            double remainingDeepestCover =
                                DeepestSegmentCover(
                                    segment,
                                    rootElevation);
                            double remainingStructureDepth =
                                DeepestSegmentStructureDepth(
                                    segment,
                                    rootElevation,
                                    sumpDepth);
                            if (remainingStructureDepth >
                                    deepRaiseTrigger + 1e-6 ||
                                remainingDeepestCover >
                                    deepRaiseTarget + 0.01)
                                warnings++;
                        }
                    }
                }
            }
            else
            {
                // Minimum depth/cover is the safety constraint. Lower the branch
                // enough to meet it and report any resulting excessive depth.
                rootElevation = Finite(rootMaximum)
                    ? rootMaximum
                    : first.UpstreamGround -
                        minimumCover -
                        first.Radius -
                        first.InnerRadius;
                warnings++;
            }

            foreach (GravityPipeStep step in segment)
                step.HeadwaterInvert =
                    rootElevation -
                    step.CumulativeStartDrop;
        }

        private static double DeepestSegmentCover(
            IEnumerable<GravityPipeStep> segment,
            double rootInvert)
        {
            double deepest = double.NegativeInfinity;
            foreach (GravityPipeStep step in segment)
            {
                double upstreamInvert =
                    rootInvert -
                    step.CumulativeStartDrop;
                double downstreamInvert =
                    rootInvert -
                    step.CumulativeEndDrop;
                double upstreamCover =
                    step.UpstreamGround -
                    (upstreamInvert +
                     step.InnerRadius +
                     step.Radius);
                double downstreamCover =
                    step.DownstreamGround -
                    (downstreamInvert +
                     step.InnerRadius +
                     step.Radius);

                if (Finite(upstreamCover))
                    deepest = Math.Max(
                        deepest,
                        upstreamCover);
                if (Finite(downstreamCover))
                    deepest = Math.Max(
                        deepest,
                        downstreamCover);
            }

            return Finite(deepest)
                ? deepest
                : double.PositiveInfinity;
        }

        private static double DeepestSegmentStructureDepth(
            IEnumerable<GravityPipeStep> segment,
            double rootInvert,
            double sumpDepth)
        {
            double deepest = double.NegativeInfinity;
            foreach (GravityPipeStep step in segment)
            {
                double upstreamInvert =
                    rootInvert -
                    step.CumulativeStartDrop;
                double downstreamInvert =
                    rootInvert -
                    step.CumulativeEndDrop;

                // Rim is controlled from the selected surface in this workflow.
                // Structure sump is set below the lowest connected inside invert.
                // This mirrors the D= manhole depth shown in Civil 3D profiles.
                double upstreamDepth =
                    step.UpstreamGround -
                    (upstreamInvert - sumpDepth);
                double downstreamDepth =
                    step.DownstreamGround -
                    (downstreamInvert - sumpDepth);

                if (Finite(upstreamDepth))
                    deepest = Math.Max(deepest, upstreamDepth);
                if (Finite(downstreamDepth))
                    deepest = Math.Max(deepest, downstreamDepth);
            }

            return Finite(deepest)
                ? deepest
                : double.PositiveInfinity;
        }

        private static void ApplyGravitySegment(
            IList<GravityPipeStep> steps, Transaction transaction,
            ProductionSettingsDialogModel settings, ref int adjusted, ref int warnings)
        {
            if (steps.Count == 0) return;
            var byId = steps.ToDictionary(step => step.Pipe.ObjectId.Handle.ToString());
            var selectedIds = new HashSet<ObjectId>(steps.Select(step => step.Pipe.ObjectId));
            var fixedInlets = new List<SewerFixedInlet>();
            var blockedNodes = new HashSet<string>();
            foreach (ObjectId node in steps.Select(step => step.UpstreamStructureId)
                .Where(id => !id.IsNull).Distinct())
            {
                var structure = transaction.GetObject(node, OpenMode.ForRead, false) as CivilStructure;
                if (structure == null) { blockedNodes.Add(node.Handle.ToString()); continue; }
                foreach (ObjectId pipeId in SewerPipeConnections.PipeIds(structure))
                {
                    if (selectedIds.Contains(pipeId)) continue;
                    var incoming = transaction.GetObject(pipeId, OpenMode.ForRead, false) as CivilPipe;
                    bool forward;
                    if (incoming == null || !SewerPipeConnections.TryForward(incoming, transaction, out forward))
                    { blockedNodes.Add(node.Handle.ToString()); continue; }
                    ObjectId downstream = forward ? incoming.EndStructureId : incoming.StartStructureId;
                    if (downstream != node) continue;
                    double invert = SewerPipeConnections.Invert(incoming, !forward);
                    if (!Finite(invert)) { blockedNodes.Add(node.Handle.ToString()); continue; }
                    fixedInlets.Add(new SewerFixedInlet
                    { PipeId = pipeId.Handle.ToString(), Node = node.Handle.ToString(), Invert = invert });
                }
            }

            double maximumSlope = Math.Abs(
                settings.Double("MaxSlope", 2.5)) / 100.0;
            bool raiseDeepRuns = string.Equals(
                settings.Text("RaiseDeepRuns"),
                "Yes",
                StringComparison.OrdinalIgnoreCase);
            double minimumCover = Math.Max(
                0.0,
                settings.Double("MinCover", 1.0));
            double deepRaiseTrigger = Math.Max(
                minimumCover,
                settings.Double("DeepRaiseThreshold", 3.5));
            double deepRaiseTarget = Math.Max(
                minimumCover,
                settings.Double("DeepRaiseTarget", 1.0));
            double sumpDepth = Math.Max(
                0.0,
                settings.Double("SumpDepth", 0.500));

            SewerGravityPlan plan = null;
            for (int pass = 0; pass < 6; pass++)
            {
                plan = SewerGravityGradeSolver.Solve(
                    steps.Select(step => new SewerGravityPipe
                    {
                        Id = step.Pipe.ObjectId.Handle.ToString(),
                        // Unconnected endpoints must not join through ObjectId.Null.
                        UpstreamNode = step.UpstreamStructureId.IsNull
                            ? "UP-" + step.Pipe.ObjectId.Handle
                            : step.UpstreamStructureId.Handle.ToString(),
                        DownstreamNode = step.DownstreamStructureId.IsNull
                            ? "DOWN-" + step.Pipe.ObjectId.Handle
                            : step.DownstreamStructureId.Handle.ToString(),
                        Length = step.Run,
                        Slope = step.Slope,
                        HeadwaterInvert = step.HeadwaterInvert
                    }),
                    fixedInlets,
                    blockedNodes);

                bool changed = false;
                foreach (SewerGravityGrade grade in plan.Grades)
                {
                    GravityPipeStep step = byId[grade.Pipe.Id];
                    double upstreamCentre =
                        grade.UpstreamInvert + step.InnerRadius;
                    double downstreamCentre =
                        grade.DownstreamInvert + step.InnerRadius;
                    double upstreamCover =
                        step.UpstreamGround -
                        (upstreamCentre + step.Radius);
                    double downstreamCover =
                        step.DownstreamGround -
                        (downstreamCentre + step.Radius);

                    // A fixed/lower incoming connection controls the upstream
                    // invert. If the downstream end would then have less than the
                    // specified minimum cover, steepen only this pipe enough to
                    // recover the missing depth, never beyond MaxSlope.
                    if (downstreamCover < minimumCover - 1e-6)
                    {
                        double requiredIncrease =
                            (minimumCover - downstreamCover) /
                            Math.Max(step.Run, PointTolerance);
                        double requested =
                            step.Slope + requiredIncrease;
                        double nextSlope =
                            Math.Min(maximumSlope, requested);
                        if (nextSlope > step.Slope + 1e-9)
                        {
                            step.Slope = nextSlope;
                            changed = true;
                        }
                        if (requested > maximumSlope + 1e-9)
                            warnings++;
                    }

                    // An upstream cover failure cannot be repaired by changing the
                    // current pipe slope because the upstream invert is already
                    // controlled by the incoming connection. Report it instead.
                    if (upstreamCover < minimumCover - 1e-6)
                        warnings++;
                }

                if (!changed)
                    break;
            }

            if (plan == null)
                return;

            if (raiseDeepRuns)
            {
                // Branch preparation raises each branch independently. A connected
                // selected network can still be pulled back down at a junction by
                // the lowest incoming branch. Resolve that case by lifting all
                // unconstrained selected headwaters together, then re-solving the
                // complete directed network. Fixed/unselected incoming pipes remain
                // hard boundaries and are never moved.
                for (int liftPass = 0; liftPass < 6; liftPass++)
                {
                    double deepestSolvedCover;
                    double deepestSolvedStructureDepth;
                    double availableRaiseByCover;
                    ReadDeepRaiseMetrics(
                        plan,
                        byId,
                        sumpDepth,
                        minimumCover,
                        out deepestSolvedCover,
                        out deepestSolvedStructureDepth,
                        out availableRaiseByCover);

                    if (!Finite(deepestSolvedStructureDepth) ||
                        deepestSolvedStructureDepth <=
                            deepRaiseTrigger + 1e-6)
                        break;

                    double requestedRaise = Math.Max(
                        0.0,
                        deepestSolvedCover - deepRaiseTarget);
                    double raise = Math.Min(
                        requestedRaise,
                        Math.Max(0.0, availableRaiseByCover));
                    if (!Finite(raise) || raise <= 1e-6)
                        break;

                    HashSet<string> movableHeadwaters =
                        new HashSet<string>(
                            plan.Grades
                                .Where(grade =>
                                    grade.IncomingCount == 0 &&
                                    grade.Pipe != null)
                                .Select(grade => grade.Pipe.Id),
                            StringComparer.Ordinal);

                    bool shifted = false;
                    foreach (GravityPipeStep step in steps)
                    {
                        string id =
                            step.Pipe.ObjectId.Handle.ToString();
                        if (!movableHeadwaters.Contains(id))
                            continue;

                        step.HeadwaterInvert += raise;
                        shifted = true;
                    }

                    if (!shifted)
                        break;

                    plan = SewerGravityGradeSolver.Solve(
                        steps.Select(step => new SewerGravityPipe
                        {
                            Id = step.Pipe.ObjectId.Handle.ToString(),
                            UpstreamNode = step.UpstreamStructureId.IsNull
                                ? "UP-" + step.Pipe.ObjectId.Handle
                                : step.UpstreamStructureId.Handle.ToString(),
                            DownstreamNode = step.DownstreamStructureId.IsNull
                                ? "DOWN-" + step.Pipe.ObjectId.Handle
                                : step.DownstreamStructureId.Handle.ToString(),
                            Length = step.Run,
                            Slope = step.Slope,
                            HeadwaterInvert = step.HeadwaterInvert
                        }),
                        fixedInlets,
                        blockedNodes);
                }

                double finalDeepestCover;
                double finalDeepestStructureDepth;
                double finalRaiseRoom;
                ReadDeepRaiseMetrics(
                    plan,
                    byId,
                    sumpDepth,
                    minimumCover,
                    out finalDeepestCover,
                    out finalDeepestStructureDepth,
                    out finalRaiseRoom);

                if (Finite(finalDeepestStructureDepth) &&
                    finalDeepestStructureDepth >
                        deepRaiseTrigger + 1e-6)
                {
                    AcApplication.DocumentManager.MdiActiveDocument.Editor.WriteMessage(
                        "\nDeep-structure raise remains constrained: deepest manhole depth={0:N3} m; trigger={1:N3} m; crown-cover target={2:N3} m; deepest crown cover={3:N3} m. Fixed incoming invert, minimum slope, minimum cover or another connected branch may control the result.",
                        finalDeepestStructureDepth,
                        deepRaiseTrigger,
                        deepRaiseTarget,
                        finalDeepestCover);
                }
            }

            // Re-solve once with the final slope set so the grade written to
            // Civil 3D always matches the last depth-driven adjustment.
            plan = SewerGravityGradeSolver.Solve(
                steps.Select(step => new SewerGravityPipe
                {
                    Id = step.Pipe.ObjectId.Handle.ToString(),
                    UpstreamNode = step.UpstreamStructureId.IsNull
                        ? "UP-" + step.Pipe.ObjectId.Handle
                        : step.UpstreamStructureId.Handle.ToString(),
                    DownstreamNode = step.DownstreamStructureId.IsNull
                        ? "DOWN-" + step.Pipe.ObjectId.Handle
                        : step.DownstreamStructureId.Handle.ToString(),
                    Length = step.Run,
                    Slope = step.Slope,
                    HeadwaterInvert = step.HeadwaterInvert
                }),
                fixedInlets,
                blockedNodes);

            warnings += plan.UnresolvedPipeIds.Count;
            if (plan.UnresolvedPipeIds.Count > 0)
                AcApplication.DocumentManager.MdiActiveDocument.Editor.WriteMessage(
                    "\nGravity grade not changed (cycle or unresolved incoming connection): {0}",
                    string.Join(", ", plan.UnresolvedPipeIds.Select(id => byId[id].Pipe.Name)));

            double maximumCover = Math.Max(
                minimumCover,
                settings.Double("MaxCover", 10.0));
            foreach (SewerGravityGrade grade in plan.Grades)
            {
                GravityPipeStep step = byId[grade.Pipe.Id];
                // Match INSIDE inverts, then convert back to each pipe's own
                // centreline. Different diameters must not create an invert step.
                double upstreamZ = grade.UpstreamInvert + step.InnerRadius;
                double downstreamZ = grade.DownstreamInvert + step.InnerRadius;
                if (downstreamZ >= upstreamZ - 1e-9)
                    throw new InvalidOperationException("Invalid downhill grade for " + step.Pipe.Name);
                Point3d newUpstream = new Point3d(step.Upstream.X, step.Upstream.Y, upstreamZ);
                Point3d newDownstream = new Point3d(step.Downstream.X, step.Downstream.Y, downstreamZ);
                step.Pipe.StartPoint = step.Forward ? newUpstream : newDownstream;
                step.Pipe.EndPoint = step.Forward ? newDownstream : newUpstream;
                step.Pipe.FlowDirectionMethod = step.Forward
                    ? Autodesk.Civil.DatabaseServices.FlowDirectionMethodType.StartToEnd
                    : Autodesk.Civil.DatabaseServices.FlowDirectionMethodType.EndToStart;
                // A setter/native rule failure aborts the transaction: downstream
                // pipes must never use an incoming level that was not saved.
                double actualUp = SewerPipeConnections.Invert(step.Pipe, step.Forward);
                double actualDown = SewerPipeConnections.Invert(step.Pipe, !step.Forward);
                if (Math.Abs(actualUp - grade.UpstreamInvert) > 1e-6 ||
                    Math.Abs(actualDown - grade.DownstreamInvert) > 1e-6)
                    throw new InvalidOperationException("Civil 3D did not retain the connected invert grade for " + step.Pipe.Name);

                double upstreamCover = step.UpstreamGround - (upstreamZ + step.Radius);
                double downstreamCover = step.DownstreamGround - (downstreamZ + step.Radius);
                if (upstreamCover < minimumCover - 1e-6 || upstreamCover > maximumCover + 1e-6 ||
                    downstreamCover < minimumCover - 1e-6 || downstreamCover > maximumCover + 1e-6)
                    warnings++; // Fixed incoming inverts take precedence over the hard cover envelope.

                if (raiseDeepRuns)
                {
                    double upstreamStructureDepth =
                        step.UpstreamGround -
                        (grade.UpstreamInvert - sumpDepth);
                    double downstreamStructureDepth =
                        step.DownstreamGround -
                        (grade.DownstreamInvert - sumpDepth);
                    if (upstreamStructureDepth >
                            deepRaiseTrigger + 1e-6 ||
                        downstreamStructureDepth >
                            deepRaiseTrigger + 1e-6)
                    {
                        // The actual manhole-depth trigger is still exceeded after
                        // grading. Report it without silently changing the requested
                        // gravity direction/maximum slope.
                        warnings++;
                    }
                }
                adjusted++;
            }
            // Recheck the complete network after every setter has run: a native
            // change on a later connected pipe must not invalidate an earlier one.
            foreach (SewerGravityGrade grade in plan.Grades)
            {
                GravityPipeStep step = byId[grade.Pipe.Id];
                if (Math.Abs(SewerPipeConnections.Invert(step.Pipe, step.Forward) - grade.UpstreamInvert) > 1e-6 ||
                    Math.Abs(SewerPipeConnections.Invert(step.Pipe, !step.Forward) - grade.DownstreamInvert) > 1e-6)
                    throw new InvalidOperationException("Connected pipe levels changed while grading " + step.Pipe.Name);
            }
        }

        private static void ReadDeepRaiseMetrics(
            SewerGravityPlan plan,
            IDictionary<string, GravityPipeStep> byId,
            double sumpDepth,
            double minimumCover,
            out double deepestCover,
            out double deepestStructureDepth,
            out double availableRaiseByCover)
        {
            deepestCover = double.NegativeInfinity;
            deepestStructureDepth = double.NegativeInfinity;
            availableRaiseByCover = double.PositiveInfinity;

            if (plan == null || byId == null)
                return;

            foreach (SewerGravityGrade grade in plan.Grades)
            {
                GravityPipeStep step;
                if (grade == null ||
                    grade.Pipe == null ||
                    !byId.TryGetValue(grade.Pipe.Id, out step) ||
                    step == null)
                    continue;

                double upstreamCentre =
                    grade.UpstreamInvert + step.InnerRadius;
                double downstreamCentre =
                    grade.DownstreamInvert + step.InnerRadius;
                double upstreamCover =
                    step.UpstreamGround -
                    (upstreamCentre + step.Radius);
                double downstreamCover =
                    step.DownstreamGround -
                    (downstreamCentre + step.Radius);
                double upstreamStructureDepth =
                    step.UpstreamGround -
                    (grade.UpstreamInvert - sumpDepth);
                double downstreamStructureDepth =
                    step.DownstreamGround -
                    (grade.DownstreamInvert - sumpDepth);

                if (Finite(upstreamCover))
                {
                    deepestCover = Math.Max(
                        deepestCover,
                        upstreamCover);
                    availableRaiseByCover = Math.Min(
                        availableRaiseByCover,
                        upstreamCover - minimumCover);
                }
                if (Finite(downstreamCover))
                {
                    deepestCover = Math.Max(
                        deepestCover,
                        downstreamCover);
                    availableRaiseByCover = Math.Min(
                        availableRaiseByCover,
                        downstreamCover - minimumCover);
                }
                if (Finite(upstreamStructureDepth))
                    deepestStructureDepth = Math.Max(
                        deepestStructureDepth,
                        upstreamStructureDepth);
                if (Finite(downstreamStructureDepth))
                    deepestStructureDepth = Math.Max(
                        deepestStructureDepth,
                        downstreamStructureDepth);
            }

            if (!Finite(availableRaiseByCover))
                availableRaiseByCover = 0.0;
            else
                availableRaiseByCover =
                    Math.Max(0.0, availableRaiseByCover);
        }

        private static void ApplyNaturalGroundDepth(
            CivilPipe pipe,
            CivilSurface surface,
            ProductionSettingsDialogModel settings,
            ref int adjusted,
            ref int warnings)
        {
            Point3d start;
            Point3d end;
            if (pipe == null || surface == null ||
                !TryReadPoint(pipe, "StartPoint", out start) ||
                !TryReadPoint(pipe, "EndPoint", out end))
            {
                warnings++;
                return;
            }

            double run = PlanRun(start, end);
            if (run <= PointTolerance)
            {
                warnings++;
                return;
            }
            if (run < settings.Double("MinLength", 2.440) ||
                run > settings.Double("MaxLength", 100.0))
                warnings++;

            double groundStart;
            double groundEnd;
            try
            {
                groundStart = surface.FindElevationAtXY(start.X, start.Y);
                groundEnd = surface.FindElevationAtXY(end.X, end.Y);
            }
            catch
            {
                warnings++;
                return;
            }
            if (!Finite(groundStart) || !Finite(groundEnd))
            {
                warnings++;
                return;
            }

            double diameter = Math.Max(0.0, ReadDouble(
                pipe,
                "OuterDiameterOrWidth",
                "InnerDiameterOrWidth",
                "Diameter"));
            double radius = diameter * 0.5;
            double crownDepth = Math.Max(
                0.0,
                settings.Double("GroundDepth", 0.834));

            Point3d newStart = new Point3d(
                start.X,
                start.Y,
                groundStart - crownDepth - radius);
            Point3d newEnd = new Point3d(
                end.X,
                end.Y,
                groundEnd - crownDepth - radius);

            bool startSet = TrySetPoint(pipe, "StartPoint", newStart);
            bool endSet = TrySetPoint(pipe, "EndPoint", newEnd);
            if (startSet && endSet)
            {
                adjusted++;
                return;
            }

            if (startSet) TrySetPoint(pipe, "StartPoint", start);
            if (endSet) TrySetPoint(pipe, "EndPoint", end);
            warnings++;
        }

        private static double PlanRun(Point3d first, Point3d second)
        {
            double dx = second.X - first.X;
            double dy = second.Y - first.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static bool Finite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static string ReadStructureName(
            ObjectId structureId,
            Transaction transaction)
        {
            if (structureId.IsNull || transaction == null) return string.Empty;
            try
            {
                CivilStructure structure = transaction.GetObject(
                    structureId,
                    OpenMode.ForRead,
                    false) as CivilStructure;
                return structure == null
                    ? string.Empty
                    : structure.Name ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private sealed class GravityPipeSeed
        {
            internal CivilPipe Pipe;
            internal int Branch;
            internal int Sequence;
        }

        private sealed class GravityPipeStep
        {
            internal CivilPipe Pipe;
            internal bool Forward;
            internal Point3d Upstream;
            internal Point3d Downstream;
            internal ObjectId UpstreamStructureId;
            internal ObjectId DownstreamStructureId;
            internal double Run;
            internal double Radius;
            internal double InnerRadius;
            internal double HeadwaterInvert;
            internal double MinimumSlope;
            internal double Slope;
            internal double UpstreamGround;
            internal double DownstreamGround;
            internal double CumulativeStartDrop;
            internal double CumulativeEndDrop;
        }

        private static void ApplyManualPipeRules(
            CivilPipe pipe,
            CivilSurface surface,
            ProductionSettingsDialogModel settings,
            ref int adjusted,
            ref int warnings)
        {
            Point3d start;
            Point3d end;
            if (pipe == null || surface == null ||
                !TryReadPoint(pipe, "StartPoint", out start) ||
                !TryReadPoint(pipe, "EndPoint", out end))
            {
                warnings++;
                return;
            }

            double run = Math.Sqrt(Math.Pow(end.X - start.X, 2.0) + Math.Pow(end.Y - start.Y, 2.0));
            if (run < settings.Double("MinLength", 2.440) || run > settings.Double("MaxLength", 100.0))
                warnings++;
            if (run <= PointTolerance) { warnings++; return; }

            double overallMinimumSlope = Math.Abs(settings.Double("MinSlope", 1.0)) / 100.0;
            double startingMinimumSlope = Math.Abs(settings.Double("MinStartSlope", 1.5)) / 100.0;
            double minimumSlope = IsStartingBranchPipe(pipe)
                ? Math.Max(overallMinimumSlope, startingMinimumSlope)
                : overallMinimumSlope;
            double maximumSlope = Math.Max(minimumSlope, Math.Abs(settings.Double("MaxSlope", 12.0)) / 100.0);
            double minimumCover = Math.Max(0.0, settings.Double("MinCover", 0.834));
            double maximumCover = Math.Max(minimumCover, settings.Double("MaxCover", 10.0));
            double diameter = Math.Max(0.0, ReadDouble(pipe,
                "OuterDiameterOrWidth", "InnerDiameterOrWidth", "Diameter"));
            double radius = diameter * 0.5;
            double startSurface;
            double endSurface;
            try
            {
                startSurface = surface.FindElevationAtXY(start.X, start.Y);
                endSurface = surface.FindElevationAtXY(end.X, end.Y);
            }
            catch { warnings++; return; }

            if (double.IsNaN(startSurface) || double.IsInfinity(startSurface) ||
                double.IsNaN(endSurface) || double.IsInfinity(endSurface))
            {
                warnings++;
                return;
            }

            double startMinimumZ = startSurface - maximumCover - radius;
            double startMaximumZ = startSurface - minimumCover - radius;
            double endMinimumZ = endSurface - maximumCover - radius;
            double endMaximumZ = endSurface - minimumCover - radius;
            double naturalStartZ = startMaximumZ;
            double naturalEndZ = endMaximumZ;
            double direction = naturalEndZ <= naturalStartZ ? -1.0 : 1.0;

            var candidates = new List<double>();
            AddPipeRuleCandidate(candidates, startMinimumZ, startMinimumZ, startMaximumZ);
            AddPipeRuleCandidate(candidates, startMaximumZ, startMinimumZ, startMaximumZ);
            AddPipeRuleCandidate(candidates, naturalStartZ, startMinimumZ, startMaximumZ);
            foreach (double endBoundary in new[] { endMinimumZ, endMaximumZ })
                foreach (double slopeBoundary in new[] { minimumSlope, maximumSlope })
                    AddPipeRuleCandidate(
                        candidates,
                        endBoundary - direction * slopeBoundary * run,
                        startMinimumZ,
                        startMaximumZ);

            double bestStartZ = naturalStartZ;
            double bestEndZ = Math.Max(endMinimumZ, Math.Min(endMaximumZ, naturalEndZ));
            double bestPenalty = double.PositiveInfinity;
            foreach (double candidateStartZ in candidates.Distinct())
            {
                double requestedSlope = Math.Abs(naturalEndZ - candidateStartZ) / run;
                double slope = Math.Max(minimumSlope, Math.Min(maximumSlope, requestedSlope));
                double candidateEndZ = candidateStartZ + direction * slope * run;
                candidateEndZ = Math.Max(endMinimumZ, Math.Min(endMaximumZ, candidateEndZ));
                double actualSlope = Math.Abs(candidateEndZ - candidateStartZ) / run;
                double penalty =
                    RangePenalty(actualSlope, minimumSlope, maximumSlope) * 100.0 +
                    Math.Abs(candidateStartZ - naturalStartZ) +
                    Math.Abs(candidateEndZ - naturalEndZ);
                if (penalty < bestPenalty)
                {
                    bestPenalty = penalty;
                    bestStartZ = candidateStartZ;
                    bestEndZ = candidateEndZ;
                }
            }

            double finalSlope = Math.Abs(bestEndZ - bestStartZ) / run;
            if (finalSlope < minimumSlope - 1e-6 || finalSlope > maximumSlope + 1e-6)
                warnings++;

            Point3d newStart = new Point3d(start.X, start.Y, bestStartZ);
            Point3d newEnd = new Point3d(end.X, end.Y, bestEndZ);
            bool startSet = TrySetPoint(pipe, "StartPoint", newStart);
            bool endSet = TrySetPoint(pipe, "EndPoint", newEnd);
            if (startSet && endSet)
                adjusted++;
            else
            {
                if (startSet) TrySetPoint(pipe, "StartPoint", start);
                if (endSet) TrySetPoint(pipe, "EndPoint", end);
                warnings++;
            }
        }

        private static bool IsStartingBranchPipe(CivilPipe pipe)
        {
            if (pipe == null) return false;
            Match name = PipeNamePattern.Match(pipe.Name ?? string.Empty);
            if (name.Success)
            {
                int sequence;
                return int.TryParse(
                    name.Groups["sequence"].Value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out sequence) && sequence == 1;
            }
            string description = pipe.Description ?? string.Empty;
            return description.IndexOf("Pipe-1", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void AddPipeRuleCandidate(
            IList<double> candidates,
            double value,
            double minimum,
            double maximum)
        {
            if (candidates == null || double.IsNaN(value) || double.IsInfinity(value))
                return;
            candidates.Add(Math.Max(minimum, Math.Min(maximum, value)));
        }

        private static double RangePenalty(double value, double minimum, double maximum)
        {
            if (value < minimum) return minimum - value;
            if (value > maximum) return value - maximum;
            return 0.0;
        }

        private static void ApplyManualStructureRules(
            CivilStructure structure,
            Transaction transaction,
            CivilSurface surface,
            ProductionSettingsDialogModel settings,
            ref int adjusted,
            ref int warnings,
            bool preservePipeEndpoints = false)
        {
            List<ObjectId> pipeIds = SewerPipeConnections.PipeIds(structure);

            double lowestInvert = double.PositiveInfinity;
            var dropLevels = new List<double>();
            var endpoints = new List<StructurePipeEndpoint>();
            bool useCrown = string.Equals(settings.Text("DropReference"), "Crown", StringComparison.OrdinalIgnoreCase);
            foreach (ObjectId id in pipeIds)
            {
                CivilPipe pipe = transaction.GetObject(id, OpenMode.ForRead, false) as CivilPipe;
                Point3d point;
                bool atStart = pipe != null && pipe.StartStructureId == structure.ObjectId;
                bool atEnd = pipe != null && pipe.EndStructureId == structure.ObjectId;
                if (!atStart && !atEnd) continue;
                if (TryReadPoint(pipe, atStart ? "StartPoint" : "EndPoint", out point))
                {
                    double diameter = pipe.InnerHeight;
                    double radius = diameter * 0.5;
                    double invert = point.Z - radius;
                    lowestInvert = Math.Min(lowestInvert, invert);
                    double referenceLevel = useCrown ? point.Z + radius : invert;
                    dropLevels.Add(referenceLevel);
                    endpoints.Add(new StructurePipeEndpoint
                    {
                        PipeId = id,
                        AtStart = atStart,
                        Point = point,
                        Radius = radius,
                        ReferenceLevel = referenceLevel
                    });
                }
            }
            if (double.IsInfinity(lowestInvert)) { warnings++; return; }

            double minimumCover = Math.Max(0.0, settings.Double("MinCover", 0.834));
            double maximumCover = Math.Max(minimumCover, settings.Double("MaxCover", 10.0));
            double surfaceRim = double.NaN;
            Point3d structurePoint;
            if (surface != null &&
                (TryReadPoint(structure, "Position", out structurePoint) ||
                 TryReadPoint(structure, "InsertionPoint", out structurePoint)))
            {
                try { surfaceRim = surface.FindElevationAtXY(structurePoint.X, structurePoint.Y); }
                catch { surfaceRim = double.NaN; }
            }

            double sumpDepth = Math.Max(0.0, settings.Double("SumpDepth", 0.500));
            double absoluteSumpElevation = lowestInvert - sumpDepth;

            // Civil 3D 2023 can expose SumpElevation as the signed relative
            // sump offset while ControlSumpBy is Depth.  That produced values such
            // as -0.080 in the audit and, more importantly, could leave profile
            // view structure extents hundreds of metres below the network datum.
            // Use absolute-elevation control first so the stored geometry is
            // unambiguous.  Fall back to a positive depth only when this host does
            // not expose an editable SumpElevation property.
            bool elevationMode = TrySetEnumProperty(
                structure,
                "ControlSumpBy",
                "Elevation",
                "ByElevation",
                "SumpElevation");
            bool elevationSet = elevationMode &&
                TrySetDoubleProperty(structure, "SumpElevation", absoluteSumpElevation);

            bool depthSet = false;
            if (!elevationSet)
            {
                TrySetEnumProperty(structure, "ControlSumpBy", "Depth", "ByDepth", "SumpDepth");
                depthSet = TrySetDoubleProperty(structure, "SumpDepth", Math.Abs(sumpDepth));
            }

            if (elevationSet || depthSet) adjusted++;
            else warnings++;

            // Keep the manhole rim tied to the selected surface where Civil 3D
            // exposes a writable rim elevation. The resulting depth is then
            // calculated from rim to the absolute sump, while pipe covers are
            // checked again at every connected endpoint.
            if (!double.IsNaN(surfaceRim) && !double.IsInfinity(surfaceRim))
            {
                if (!TrySetDoubleProperty(structure, "RimElevation", surfaceRim))
                    warnings++;
                double manholeDepth = surfaceRim - absoluteSumpElevation;
                if (manholeDepth < -1e-6)
                    warnings++;
                else
                    TrySetDoubleProperty(structure, "Depth", Math.Max(0.0, manholeDepth));

                foreach (ObjectId id in pipeIds)
                {
                    CivilPipe pipe = transaction.GetObject(id, OpenMode.ForRead, false) as CivilPipe;
                    Point3d point;
                    bool atStart = pipe != null && pipe.StartStructureId == structure.ObjectId;
                    bool atEnd = pipe != null && pipe.EndStructureId == structure.ObjectId;
                    if (!atStart && !atEnd) continue;
                    if (!TryReadPoint(pipe, atStart ? "StartPoint" : "EndPoint", out point))
                    {
                        warnings++;
                        continue;
                    }
                    double diameter = Math.Max(0.0, ReadDouble(pipe,
                        "OuterDiameterOrWidth", "InnerDiameterOrWidth", "Diameter"));
                    double cover = surfaceRim - (point.Z + diameter * 0.5);
                    if (cover < minimumCover - 1e-6 ||
                        cover > maximumCover + 1e-6)
                        warnings++;
                }
            }
            else
            {
                warnings++;
            }

            // Length/topology and excessive-drop conditions are intentionally
            // review warnings; CE Tools does not move structures or add/delete
            // pipes behind the user's back.
            double minimumDrop = Math.Max(0.0, settings.Double("DropValue", 0.050));
            double maximumDrop = Math.Max(minimumDrop, settings.Double("MaxDrop", 3.0));
            if (endpoints.Count >= 2)
            {
                double high = endpoints.Max(item => item.ReferenceLevel);
                double low = endpoints.Min(item => item.ReferenceLevel);
                double drop = high - low;
                if (drop < minimumDrop - 1e-6 || drop > maximumDrop + 1e-6)
                {
                    bool applyDrop =
                        !preservePipeEndpoints &&
                        string.Equals(
                            settings.Text("DropHandling"),
                            "Apply drop corrections",
                            StringComparison.OrdinalIgnoreCase);
                    if (applyDrop)
                    {
                        double targetLow = drop < minimumDrop
                            ? high - minimumDrop
                            : high - maximumDrop;
                        StructurePipeEndpoint endpoint = endpoints
                            .OrderBy(item => item.ReferenceLevel)
                            .First();
                        double delta = targetLow - endpoint.ReferenceLevel;
                        if (TryAdjustStructureEndpoint(
                            endpoint,
                            transaction,
                            surface,
                            minimumCover,
                            maximumCover,
                            delta))
                        {
                            adjusted++;
                            endpoint.ReferenceLevel += delta;
                            low = endpoint.ReferenceLevel;
                            drop = high - low;
                        }
                    }
                    if (drop < minimumDrop - 1e-6 ||
                        drop > maximumDrop + 1e-6)
                        warnings++;
                }
            }
        }

        private static bool TryAdjustStructureEndpoint(
            StructurePipeEndpoint endpoint,
            Transaction transaction,
            CivilSurface surface,
            double minimumCover,
            double maximumCover,
            double delta)
        {
            if (endpoint == null || transaction == null || surface == null ||
                endpoint.PipeId.IsNull)
                return false;
            try
            {
                CivilPipe pipe = transaction.GetObject(
                    endpoint.PipeId,
                    OpenMode.ForWrite,
                    false) as CivilPipe;
                if (pipe == null) return false;
                Point3d point = endpoint.AtStart
                    ? pipe.StartPoint
                    : pipe.EndPoint;
                Point3d proposed = new Point3d(
                    point.X,
                    point.Y,
                    point.Z + delta);
                double ground = surface.FindElevationAtXY(point.X, point.Y);
                double cover = ground - (proposed.Z + endpoint.Radius);
                if (double.IsNaN(cover) || double.IsInfinity(cover) ||
                    cover < minimumCover - 1e-6 ||
                    cover > maximumCover + 1e-6)
                    return false;
                return endpoint.AtStart
                    ? TrySetPoint(pipe, "StartPoint", proposed)
                    : TrySetPoint(pipe, "EndPoint", proposed);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryReadPoint(object target, string name, out Point3d point)
        {
            point = Point3d.Origin;
            object value = ReadReflectedProperty(target, name);
            if (!(value is Point3d)) return false;
            point = (Point3d)value;
            return true;
        }

        private static bool TrySetPoint(object target, string name, Point3d value)
        {
            try
            {
                PropertyInfo property = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (property == null || !property.CanWrite || property.PropertyType != typeof(Point3d)) return false;
                property.SetValue(target, value, null);
                return true;
            }
            catch { return false; }
        }

        private static bool TrySetDoubleProperty(object target, string name, double value)
        {
            try
            {
                PropertyInfo property = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (property == null || !property.CanWrite || property.PropertyType != typeof(double)) return false;
                property.SetValue(target, value, null);
                return true;
            }
            catch { return false; }
        }

        private static bool TrySetEnumProperty(
            object target,
            string name,
            params string[] values)
        {
            try
            {
                PropertyInfo property = target.GetType().GetProperty(
                    name,
                    BindingFlags.Public | BindingFlags.Instance);
                if (property == null || !property.CanWrite || !property.PropertyType.IsEnum)
                    return false;
                foreach (string value in values)
                {
                    try
                    {
                        object parsed = Enum.Parse(property.PropertyType, value, true);
                        property.SetValue(target, parsed, null);
                        return true;
                    }
                    catch { }
                }
            }
            catch { }
            return false;
        }

        private static double ReadDouble(object target, params string[] names)
        {
            foreach (string name in names)
            {
                object value = ReadReflectedProperty(target, name);
                if (value == null) continue;
                try { return Convert.ToDouble(value, CultureInfo.InvariantCulture); } catch { }
            }
            return 0.0;
        }

        private static object ReadReflectedProperty(object target, string name)
        {
            if (target == null) return null;
            try
            {
                PropertyInfo property = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                return property == null ? null : property.GetValue(target, null);
            }
            catch { return null; }
        }

        private static object InvokeReflected(object target, string name)
        {
            if (target == null) return null;
            try
            {
                MethodInfo method = target.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                return method == null ? null : method.Invoke(target, null);
            }
            catch { return null; }
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
                EnsureRegApp(database, transaction, "CE_TOOLS_SEWALIGN");
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
                SewerPartAlignmentBinding.BindBranch(transaction, branch.PipeIds, branch.StructureIds, newAlignmentId);
                string networkHandle = string.Empty;
                if (branch.PipeIds.Count > 0)
                {
                    CivilPipe sourcePipe = transaction.GetObject(branch.PipeIds[0], OpenMode.ForRead, false) as CivilPipe;
                    if (sourcePipe != null && !sourcePipe.NetworkId.IsNull)
                        networkHandle = sourcePipe.NetworkId.Handle.ToString();
                }
                created.XData = new ResultBuffer(
                    new TypedValue((int)DxfCode.ExtendedDataRegAppName, "CE_TOOLS_SEWALIGN"),
                    new TypedValue((int)DxfCode.ExtendedDataAsciiString, networkHandle + "|" + branch.BranchName),
                    new TypedValue((int)DxfCode.ExtendedDataAsciiString, "Alignment"));
                transaction.Commit();
            }
            return true;
        }

        private static void EnsureRegApp(Database database, Transaction transaction, string name)
        {
            RegAppTable applications = transaction.GetObject(database.RegAppTableId, OpenMode.ForRead, false) as RegAppTable;
            if (applications == null || applications.Has(name)) return;
            applications.UpgradeOpen();
            var record = new RegAppTableRecord { Name = name };
            applications.Add(record);
            transaction.AddNewlyCreatedDBObject(record, true);
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

        private sealed class StructurePipeEndpoint
        {
            internal ObjectId PipeId;
            internal bool AtStart;
            internal Point3d Point;
            internal double Radius;
            internal double ReferenceLevel;
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

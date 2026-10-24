using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CETools.Core;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using CivilNetwork = Autodesk.Civil.DatabaseServices.Network;
using CivilPipe = Autodesk.Civil.DatabaseServices.Pipe;
using CivilStructure = Autodesk.Civil.DatabaseServices.Structure;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;
using CivilCorridor = Autodesk.Civil.DatabaseServices.Corridor;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.AutoDesignCommands))]
namespace CETools.Civil3D
{
    /// <summary>Explicit, bounded automation; never runs engineering commands from Idle.</summary>
    public sealed class AutoDesignCommands
    {
        private const string Journal = "CE_AUTODESIGN_V1";
        private static bool busy;

        [CommandMethod("CE_TOOLS", "CE_AUTODESIGN", CommandFlags.Modal | CommandFlags.Redraw)]
        public void AutoDesign()
        {
            Document doc = AcApplication.DocumentManager.MdiActiveDocument;
            if (doc == null || CivilApplication.ActiveDocument == null || busy) return;
            busy = true;
            string stage = "initialising";
            var diagnostics = new AutoDesignDiagnostics();
            diagnostics.Start(doc.Name);
            Action<string> setStage = value => { stage = value; diagnostics.Write("STAGE " + value); };
            try
            {
                doc.Editor.WriteMessage("\nCE_AUTODESIGN safety build 2 - lightweight revision tracking.");
                AutoDesignDrawingChanges.For(doc.Database);
                if (diagnostics.LogPath != null) doc.Editor.WriteMessage("\nAutoDesign diagnostic log: " + diagnostics.LogPath);
                Run(doc, setStage);
                diagnostics.Write("FINISHED (including user cancellation)");
            }
            catch (System.Exception ex)
            {
                diagnostics.Write("FAILED during " + stage + "\r\n" + ex.ToString());
                doc.Editor.WriteMessage("\nAutoDesign stopped during " + stage + ": " + ex.GetType().Name + ": " + ex.Message + ". Rerun CE_AUTODESIGN to resume.");
                doc.Editor.WriteMessage("\nAutoDesign diagnostic: " + ex.ToString());
            }
            finally { busy = false; }
        }

        private static void Run(Document doc, Action<string> setStage)
        {
            // Ask for the discipline first. A Roads-only run must never ask
            // the user to select a Sewer network or traverse Sewer networks
            // before the user has selected that workflow.
            setStage("opening discipline and mode options");
            var workflow = new ProductionSettingsDialogModel("CE Tools - AutoDesign",
                "Select an AutoDesign mode and discipline. A second popup shows only the " +
                "inputs relevant to your selected discipline.");
            workflow.AddChoice("Mode", "01 AutoDesign", "Mode", "Analyse Only",
                "Analyse Only does not modify drawing objects or checkpoints.",
                new[] { "Analyse Only", "Complete Existing Design", "Full AutoDesign" });
            workflow.AddChoice("Scope", "01 AutoDesign", "Workflow", "Sewer",
                "Select Sewer, Roads or the combined workflow.",
                new[] { "Sewer", "Roads", "Sewer and Roads" });
            var workflowWindow = new ProductionSettingsWindow(workflow);
            AcApplication.ShowModalWindow(workflowWindow);
            if (!workflowWindow.Accepted) return;

            string scope = workflow.Text("Scope");
            bool sewer = scope != "Roads", roads = scope != "Sewer";
            AutoDesignMode mode = workflow.Text("Mode") == "Analyse Only" ? AutoDesignMode.AnalyseOnly :
                workflow.Text("Mode") == "Full AutoDesign" ? AutoDesignMode.FullAutoDesign : AutoDesignMode.CompleteExistingDesign;

            const string none = "Select an existing sewer network";
            const string allRoads = "All road assets (drawing-wide refresh)";
            var networks = new Dictionary<string, ObjectId>();
            var roadChoices = new List<string> { allRoads };
            ObjectId network = ObjectId.Null;
            string selectedChoice = none;
            if (sewer)
            {
                setStage("reading Sewer networks");
                using (var tr = doc.Database.TransactionManager.StartTransaction())
                    foreach (ObjectId id in CivilApplication.ActiveDocument.GetPipeNetworkIds())
                    {
                        setStage("reading gravity network " + id.Handle);
                        var n = tr.GetObject(id, OpenMode.ForRead) as CivilNetwork;
                        if (n != null && !n.IsReferenceObject)
                            networks[n.Name + " [" + id.Handle + "]"] = id;
                    }
                string preferred = AutoDesignDrawingChanges.For(doc.Database).PreferredSewerNetworkHandle;
                selectedChoice = networks.Where(pair => pair.Value.Handle.ToString() == preferred)
                    .Select(pair => pair.Key).FirstOrDefault() ?? none;
            }
            if (roads)
            {
                setStage("reading Road alignment choices");
                using (var tr = doc.Database.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId id in CivilApplication.ActiveDocument.GetAlignmentIds())
                    {
                        var alignment = tr.GetObject(id, OpenMode.ForRead)
                            as Autodesk.Civil.DatabaseServices.Alignment;
                        if (alignment == null || alignment.IsReferenceObject) continue;
                        string name = alignment.Name ?? string.Empty;
                        if (name.StartsWith("ROAD", StringComparison.OrdinalIgnoreCase) ||
                            name.StartsWith("RD-", StringComparison.OrdinalIgnoreCase) ||
                            name.IndexOf(" ROAD ", StringComparison.OrdinalIgnoreCase) >= 0)
                            roadChoices.Add("Review focus: " + name + " [" + id.Handle + "]");
                    }
                }
            }

            var inputs = new ProductionSettingsDialogModel(
                sewer && roads ? "CE Tools - AutoDesign: Sewer and Road Inputs" :
                sewer ? "CE Tools - AutoDesign: Sewer Inputs" : "CE Tools - AutoDesign: Road Inputs",
                roads ? "Road-linked elevations, names and linked-output refresh are currently DRAWING-WIDE. " +
                    "Selecting one Road provides a review focus, not isolated execution; you must explicitly " +
                    "confirm before running drawing-wide updates." :
                    "Select your existing Sewer network. Missing native profile views need an approved " +
                    "Civil 3D profile style, datum and placement before creation.");
            if (sewer)
            {
                inputs.AddChoice("Network", "01 Sewer", "Sewer network", selectedChoice,
                    "Select an existing Sewer gravity network. Water gravity networks must not be used.",
                    new[] { none }.Concat(networks.Keys.OrderBy(x => x)).ToArray());
                inputs.AddChoice("CreateAlignments", "01 Sewer", "Missing branch alignments", "Review only",
                    "Full AutoDesign may create missing approved CE-sequenced branch alignments using saved styles. " +
                    "It never replaces existing alignments silently.",
                    new[] { "Review only", "Create missing using saved styles" });
            }
            if (roads)
                inputs.AddChoice("RoadFocus", "02 Roads", "Road alignment / review focus", allRoads,
                    "Choose a Road to inspect, or all Road assets. Existing Road refresh adapters remain drawing-wide.",
                    roadChoices.OrderBy(x => x == allRoads ? 0 : 1).ThenBy(x => x).ToArray());

            setStage("opening discipline-specific inputs");
            var inputsWindow = new ProductionSettingsWindow(inputs);
            AcApplication.ShowModalWindow(inputsWindow);
            if (!inputsWindow.Accepted) return;
            if (sewer)
            {
                networks.TryGetValue(inputs.Text("Network"), out network);
                AutoDesignDrawingChanges.For(doc.Database).PreferredSewerNetworkHandle =
                    network.IsNull ? null : network.Handle.ToString();
            }
            string roadFocus = roads ? inputs.Text("RoadFocus") : string.Empty;
            if (roads && roadFocus != allRoads && mode != AutoDesignMode.AnalyseOnly &&
                !DisciplineWorkflowDialogs.Confirm("CE AutoDesign - Road scope",
                    "You selected " + roadFocus + " for REVIEW ONLY. Road elevation, label and linked-output " +
                    "refresh services currently run drawing-wide, not just for this alignment. " +
                    "Continue with drawing-wide refresh of all eligible existing Road outputs?"))
                return;
            setStage("validating source geometry");
            var issues = Validate(doc, sewer, roads, network);
            bool createAlignments = sewer && mode == AutoDesignMode.FullAutoDesign && inputs.Text("CreateAlignments") == "Create missing using saved styles";
            setStage("building dependencies and reading saved styles");
            var steps = BuildSteps(doc, sewer, roads, network, issues, createAlignments, setStage);
            string key = scope + ":" + (network.IsNull ? "none" : network.Handle.ToString()) + ":";
            // Scope-specific IDs prevent a successful road run from blessing sewer outputs.
            foreach (var step in steps)
            {
                step.Id = key + step.Id;
                step.Dependencies = step.Dependencies.Select(x => key + x).ToArray();
            }
            setStage("reading resume checkpoints");
            var checkpoints = ReadJournal(doc.Database);
            var engine = new AutoDesignEngine();
            Func<string> fingerprint = () =>
            {
                setStage("reading drawing change revision");
                return AutoDesignDrawingChanges.For(doc.Database).Stamp;
            };
            var plan = engine.Plan(steps, mode, fingerprint(), checkpoints);
            setStage("reporting drawing inventory");
            doc.Editor.WriteMessage("\nAutoDesign analysis: " + scope + "; " + mode);
            if (roads) doc.Editor.WriteMessage("\n  ROAD SCOPE: " + roadFocus + " (drawing-wide refresh adapters).");
            foreach (var line in Inventory(doc, sewer, roads, roadFocus)) doc.Editor.WriteMessage("\n  EXISTING: " + line);
            WriteReport(doc, plan, issues);
            setStage("showing analysis report");
            if (mode == AutoDesignMode.AnalyseOnly)
            {
                ShowReport(plan, issues, Inventory(doc, sewer, roads, roadFocus), true);
                return;
            }
            ShowReport(plan, issues, Inventory(doc, sewer, roads, roadFocus));
            if (!DisciplineWorkflowDialogs.Confirm("CE AutoDesign - Run previewed work",
                "Run the ready compatible steps shown in the report? Engineering review gates remain blocked.")) return;
            var meter = new ProgressMeter();
            meter.Start("CE AutoDesign"); meter.SetLimit(Math.Max(1, plan.Count * 2));
            LinkedTableAutoRefreshManager.BeginInternalUpdate();
            try
            {
                engine.Run(plan, mode, checkpoints, fingerprint, () =>
                    {
                        setStage("saving AutoDesign checkpoint");
                        WriteJournal(doc.Database, checkpoints);
                    },
                    (item, index, total) =>
                    {
                        setStage(item.Step.Description);
                        doc.Editor.WriteMessage("\n[{0}/{1}] {2}: {3}", index, total, item.Status, item.Step.Description);
                        meter.MeterProgress();
                    }, () => HostApplicationServices.Current.UserBreak());
            }
            finally { LinkedTableAutoRefreshManager.EndInternalUpdate(); meter.Stop(); }
            LinkedTableAutoRefreshManager.ClearPending(doc);
            setStage("regenerating display");
            doc.Editor.Regen();
            setStage("validating completed work");
            var finalIssues = Validate(doc, sewer, roads, network, true);
            WriteReport(doc, plan, finalIssues);
            setStage("showing final review report");
            bool openSewerProfiles = ShowReport(plan, finalIssues, Inventory(doc, sewer, roads, roadFocus), true, sewer);
            if (openSewerProfiles)
            {
                // Defer to the existing manual CE_SEWPROFILE popup for approval
                // of style, datum and placement. This is an explicit user click
                // after the AutoDesign run, not silent engineering creation.
                doc.Editor.WriteMessage("\nOpening CE_SEWPROFILE for user-approved profile creation. Rerun AutoDesign after profiles are created.");
                doc.SendStringToExecute("_.CE_SEWPROFILE\n", true, false, false);
            }
            doc.Editor.WriteMessage("\nAutoDesign pass finished. This is not engineering sign-off. Save the DWG to retain checkpoints; rerun to resume/recheck.");
        }

        private static List<AutoDesignStep> BuildSteps(Document doc, bool sewer, bool roads, ObjectId network, List<string> issues, bool createAlignments, Action<string> setStage)
        {
            var steps = new List<AutoDesignStep>();
            Action<string, string, string[], Action, string> add = (id, description, dependencies, action, approval) =>
                steps.Add(new AutoDesignStep { Id = id, Description = description, Dependencies = dependencies, Execute = action, ApprovalReason = approval });
            add("sources", "Rebuild only stale editable surfaces/corridors", new string[0], () => RebuildStale(doc, roads, setStage), null);
            if (sewer)
            {
                string gate = network.IsNull ? "Select the existing Sewer network in AutoDesign options. The inventory lists networks already present; no new network needs to be created just to select one." :
                    issues.FirstOrDefault(x => x.StartsWith("PROTECTED:", StringComparison.Ordinal));
                add("sewer", "Existing sewer source", new[] { "sources" }, () => { }, gate);
                add("sequence", "Refresh existing CE sewer numbering without replacing alignments", new[] { "sewer" }, () =>
                {
                    var result = SewerNetworkDynamicSequenceCommands.Resequence(doc, new[] { network }, false, false, false);
                    Require(result.Warning, result.RefreshFailures + result.SkippedNetworks, "Sewer sequencing (network must carry CE sequence names)");
                }, null);
                // Do not report missing-alignment approval unless a read-only
                // comparison against existing tagged CE alignments found work.
                // Existing alignments are NEVER replaced automatically.
                int missingBranches = -1;
                string alignmentAssessmentError = null;
                if (!network.IsNull)
                {
                    try { missingBranches = SewerBranchAlignmentCommands.CountMissingForAutoDesign(doc, network); }
                    catch (System.Exception ex) { alignmentAssessmentError = ex.Message; }
                }
                string alignmentApproval = network.IsNull ? "Select an existing Sewer network first." :
                    alignmentAssessmentError != null ? "Review branch alignments: " + alignmentAssessmentError :
                    missingBranches == 0 ? null :
                    createAlignments ? null :
                    "Detected " + missingBranches + " missing CE branch alignment(s). Select Full AutoDesign and Create missing using saved styles to create only these missing approved branches.";
                add("alignments",
                    missingBranches == 0 ? "Existing CE branch alignments already present" :
                    "Create " + (missingBranches < 0 ? "missing" : missingBranches.ToString(CultureInfo.InvariantCulture) + " missing") + " approved CE branch alignments",
                    new[] { "sequence" },
                    () => { if (missingBranches > 0) SewerBranchAlignmentCommands.CreateMissingForAutoDesign(doc, new[] { network }); },
                    alignmentApproval);
                // An existing alignment may have changed, but there is no
                // verified geometry-difference adapter yet. Do not display a
                // fabricated 'outdated alignment' finding on EVERY run.
                var settings = SewerProductionSettings.Read(doc.Database);
                string labelApproval = string.IsNullOrWhiteSpace(settings.PipePlanLabelStyle) ||
                    string.IsNullOrWhiteSpace(settings.StructurePlanLabelStyle) ||
                    settings.PipePlanLabelStyle == CivilStyleCatalogV2.DrawingDefault || settings.StructurePlanLabelStyle == CivilStyleCatalogV2.DrawingDefault
                    ? "Select and save explicit pipe and structure plan-label styles in Sewer Settings first; AutoDesign will not choose fallback styles." : null;
                add("labels", "Ensure missing sewer plan labels using saved styles", new[] { "sequence" }, () =>
                {
                    setStage("checking selected Sewer plan-label styles");
                    bool pipeStyleExists = SewerNetworkLabelCommands.ReadPipeLabelStyleNames(doc)
                        .Any(x => string.Equals(x, settings.PipePlanLabelStyle, StringComparison.OrdinalIgnoreCase));
                    bool structureStyleExists = SewerNetworkLabelCommands.ReadStructureLabelStyleNames(doc)
                        .Any(x => string.Equals(x, settings.StructurePlanLabelStyle, StringComparison.OrdinalIgnoreCase));
                    if (!pipeStyleExists || !structureStyleExists)
                        throw new InvalidOperationException("A saved Sewer label style is unavailable. Select an installed style in Sewer Settings.");
                    setStage("creating missing Sewer plan labels");
                    var result = SewerNetworkLabelCommands.EnsureLabels(doc, new[] { network });
                    Require(result.Warning, result.Skipped, "Sewer plan labels");
                }, labelApproval);
                var profileViews = ProfileViewBandRuntimeManager.ReadAutoDesignViews(doc, network);
                add("profiles", profileViews.Count == 0 ? "Create/link missing Sewer profile views" : "Refresh linked CE sewer profile parts and bands", new[] { "sequence" }, () =>
                {
                    var result = ProfileViewBandRuntimeManager.RefreshForAutoDesign(doc, profileViews);
                    Require(result.Warning, result.Skipped, "Sewer profiles");
                }, AutoDesignWorkflowPolicy.ProfileApproval(!network.IsNull, profileViews.Count));
            }
            if (roads)
            {
                add("roads", "Refresh existing linked road feature-line elevations", new[] { "sources" },
                    () => { August24RoadElevationDynamicManager.RefreshAllLinked(doc); }, null);
                // Linked elevation writes can dirty surfaces/corridors again: one bounded second source pass.
                add("road-sources", "Rebuild sources dirtied by road elevation updates", new[] { "roads" }, () => RebuildStale(doc, true, setStage), null);
                add("road-labels", "Refresh linked junction labels and road names", new[] { "road-sources" }, () =>
                {
                    RoadJunctionCompletionCommands.RefreshAll(doc);
                    August11RoadNamingCurveCommands.SyncRoadNames(doc, false);
                }, null);
            }
            add("outputs", "Refresh existing linked quantities, schedules and drawing annotations", AutoDesignWorkflowPolicy.OutputDependencies(sewer, roads), () =>
            {
                var result = LinkedRefreshEngine.RefreshForAutoDesign(doc);
                Require(null, result.Failed, "Linked outputs");
            }, null);
            add("validation", "Validate completed design for review", AutoDesignWorkflowPolicy.ValidationDependencies(sewer, roads), () =>
            {
                var findings = Validate(doc, sewer, roads, network, true);
                if (findings.Count > 0) throw new InvalidOperationException(string.Join("; ", findings));
            }, null);
            var creation = new AutoDesignStep
            {
                Id = "engineering", Description = "Full design engineering decisions", FullDesignOnly = true,
                ApprovalReason = "V1 completes existing linked designs. New routes, grading, sizes/slopes, assemblies, outfalls, profile/sheet placement and hydraulic/road criteria require review in the existing manual commands. No generic defaults are approved by choosing Full AutoDesign."
            };
            steps.Add(creation);
            // Missing deliverables must remain visible in EVERY mode, not silently reported as complete.
            // Missing native profile views already have a dedicated approval
            // stage. Do not count the same issue twice as another approval.
            var separateIssues = issues.Where(x => !x.StartsWith(
                "No CE-linked native profile views", StringComparison.OrdinalIgnoreCase)).ToList();
            if (separateIssues.Count > 0)
                add("review", "Resolve other design validation findings", new string[0],
                    null, string.Join("; ", separateIssues));
            return steps;
        }

        private static void Require(string warning, int failures, string stage)
        {
            if (!string.IsNullOrWhiteSpace(warning) || failures > 0)
                throw new InvalidOperationException(stage + ": " + warning + " Skipped/failed=" + failures);
        }

        private static List<string> Validate(Document doc, bool sewer, bool roads, ObjectId networkId, bool checkStale = false)
        {
            var issues = new List<string>();
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                if (sewer)
                {
                    var network = networkId.IsNull ? null : tr.GetObject(networkId, OpenMode.ForRead) as CivilNetwork;
                    if (network == null) issues.Add("No Sewer network selected. Choose the existing network in AutoDesign options.");
                    else
                    {
                        var connections = new List<AutoDesignConnection>();
                        int pipes = 0;
                        foreach (ObjectId id in network.GetPipeIds())
                        {
                            var pipe = tr.GetObject(id, OpenMode.ForRead) as CivilPipe;
                            if (pipe == null) continue;
                            pipes++;
                            connections.Add(new AutoDesignConnection { Id = id.Handle.ToString(),
                                Start = pipe.StartStructureId.IsNull ? null : pipe.StartStructureId.Handle.ToString(),
                                End = pipe.EndStructureId.IsNull ? null : pipe.EndStructureId.Handle.ToString() });
                            if (double.IsNaN(pipe.StartPoint.Z) || double.IsInfinity(pipe.StartPoint.Z) || double.IsNaN(pipe.EndPoint.Z) || double.IsInfinity(pipe.EndPoint.Z))
                                issues.Add("PROTECTED: invalid pipe geometry " + pipe.Name);
                            if (pipe.IsReferenceObject || ((LayerTableRecord)tr.GetObject(pipe.LayerId, OpenMode.ForRead)).IsLocked)
                                issues.Add("PROTECTED: pipe " + pipe.Name + " is referenced or on a locked layer.");
                            if (pipe.StartStructureId.IsNull || pipe.EndStructureId.IsNull)
                                issues.Add("Pipe " + pipe.Name + " has an unconnected end; confirm outlet intent.");
                            if (pipe.RefSurfaceId.IsNull) issues.Add("Pipe " + pipe.Name + " has no reference surface.");
                            if (pipe.StartPoint.DistanceTo(pipe.EndPoint) < 0.001) issues.Add("Zero-length pipe " + pipe.Name);
                            if (Math.Abs(pipe.StartPoint.Z - pipe.EndPoint.Z) < 0.000001) issues.Add("Flat sewer pipe " + pipe.Name + ": confirm gravity grade.");
                        }
                        foreach (string finding in AutoDesignTopology.Validate(network.GetStructureIds().Cast<ObjectId>().Select(id => id.Handle.ToString()), connections))
                            issues.Add("PROTECTED: " + finding);
                        if (pipes == 0) issues.Add("Selected sewer network has no pipes.");
                        foreach (ObjectId id in network.GetStructureIds())
                        {
                            var structure = tr.GetObject(id, OpenMode.ForRead) as CivilStructure;
                            if (structure != null && (structure.IsReferenceObject || ((LayerTableRecord)tr.GetObject(structure.LayerId, OpenMode.ForRead)).IsLocked))
                                issues.Add("PROTECTED: structure " + structure.Name + " is referenced or on a locked layer.");
                            if (structure != null && structure.SumpElevation >= structure.RimElevation)
                                issues.Add("Structure " + structure.Name + " has sump at/above rim.");
                        }
                    }
                    if (!networkId.IsNull && ProfileViewBandRuntimeManager.ReadAutoDesignViews(doc, networkId).Count == 0)
                        issues.Add("No CE-linked native profile views for the selected Sewer network. Approve styles, datum and placement with CE_SEWPROFILE; drawn grids are not detected as native views.");
                }
                if (checkStale)
                {
                    foreach (ObjectId id in CivilApplication.ActiveDocument.GetSurfaceIds())
                    {
                        var surface = tr.GetObject(id, OpenMode.ForRead) as CivilSurface;
                        if (surface != null && surface.IsOutOfDate) issues.Add("Surface " + surface.Name + " remains out of date; review its dependencies.");
                    }
                    if (roads) foreach (var corridor in ModelObjects(doc.Database, tr).OfType<CivilCorridor>())
                        if (corridor.IsOutOfDate) issues.Add("Corridor " + corridor.Name + " remains out of date; review targets/dependencies.");
                }
                if (roads && !ModelObjects(doc.Database, tr).Any(x => x is CivilCorridor))
                    issues.Add("Missing road corridor: approve alignments, vertical design, assembly and targets in manual road production.");
            }
            return issues;
        }

        private static IEnumerable<DBObject> ModelObjects(Database db, Transaction tr)
        {
            var model = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
            var corridorClass = RXObject.GetClass(typeof(CivilCorridor));
            var profileViewClass = RXObject.GetClass(typeof(Autodesk.Civil.DatabaseServices.ProfileView));
            foreach (ObjectId id in model)
            {
                if (id.IsNull || id.IsErased) continue;
                var objectClass = id.ObjectClass;
                // Analysis only needs these two native model types. Do not open
                // arbitrary proxy/custom/block objects merely to inspect their properties.
                bool corridor = objectClass != null && corridorClass != null && objectClass.IsDerivedFrom(corridorClass);
                bool profileView = objectClass != null && profileViewClass != null && objectClass.IsDerivedFrom(profileViewClass);
                if (!corridor && !profileView) continue;
                yield return tr.GetObject(id, OpenMode.ForRead);
            }
        }

        private static void RebuildStale(Document doc, bool roads, Action<string> setStage)
        {
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var ids = CivilApplication.ActiveDocument.GetSurfaceIds().Cast<ObjectId>().ToList();
                if (roads) ids.AddRange(ModelObjects(doc.Database, tr).OfType<CivilCorridor>().Select(x => x.ObjectId));
                foreach (ObjectId id in ids.Distinct())
                {
                    setStage("checking native source [" + id.Handle + "]");
                    var obj = tr.GetObject(id, OpenMode.ForRead);
                    var surface = obj as CivilSurface;
                    var corridor = obj as CivilCorridor;
                    bool stale = surface != null ? surface.IsOutOfDate : corridor != null && corridor.IsOutOfDate;
                    if (!stale) continue;
                    var civilEntity = obj as Autodesk.Civil.DatabaseServices.Entity;
                    if (civilEntity != null && civilEntity.IsReferenceObject)
                        throw new InvalidOperationException("Stale reference " + id.Handle + " must be refreshed from its source drawing.");
                    var entity = obj as Autodesk.AutoCAD.DatabaseServices.Entity;
                    if (entity != null && ((LayerTableRecord)tr.GetObject(entity.LayerId, OpenMode.ForRead)).IsLocked)
                        throw new InvalidOperationException("Stale object " + id.Handle + " is on a locked layer; review/unlock it first.");
                    setStage("native rebuild " + obj.GetType().Name + " [" + id.Handle + "]");
                    obj.UpgradeOpen();
                    // Same native rebuild APIs used by the existing CE surface/corridor commands.
                    if (surface != null) surface.Rebuild(); else corridor.Rebuild();
                }
                tr.Commit();
            }
        }

        private static List<string> Inventory(Document doc, bool sewer, bool roads, string roadFocus)
        {
            var result = new List<string>();
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var civil = CivilApplication.ActiveDocument;
                int networks = 0, pipes = 0, structures = 0;
                foreach (ObjectId id in civil.GetPipeNetworkIds())
                {
                    var network = tr.GetObject(id, OpenMode.ForRead) as CivilNetwork;
                    if (network == null) continue;
                    networks++;
                    pipes += network.GetPipeIds().Count;
                    structures += network.GetStructureIds().Count;
                }
                result.Add("Gravity networks: " + networks + "; pipes: " + pipes + "; structures: " + structures);
                result.Add("Alignments: " + civil.GetAlignmentIds().Count + "; surfaces: " + civil.GetSurfaceIds().Count);
                var objects = ModelObjects(doc.Database, tr).ToList();
                var corridors = objects.OfType<CivilCorridor>().ToList();
                var nativeViews = new HashSet<ObjectId>();
                foreach (ObjectId alignmentId in civil.GetAlignmentIds())
                {
                    var alignment = tr.GetObject(alignmentId, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Alignment;
                    if (alignment == null) continue;
                    foreach (ObjectId viewId in alignment.GetProfileViewIds()) if (!viewId.IsNull && !viewId.IsErased) nativeViews.Add(viewId);
                }
                result.Add("Corridors: " + corridors.Count + "; native profile views: " + nativeViews.Count);
                int staleSurfaces = 0;
                foreach (ObjectId id in civil.GetSurfaceIds())
                {
                    var surface = tr.GetObject(id, OpenMode.ForRead) as CivilSurface;
                    if (surface != null && surface.IsOutOfDate) staleSurfaces++;
                }
                result.Add("Outdated surfaces: " + staleSurfaces + "; outdated corridors: " + corridors.Count(x => x.IsOutOfDate));
            }
            return result;
        }

        private static bool ShowReport(IList<AutoDesignItem> plan, IList<string> issues, IList<string> inventory,
            bool showAttention = false, bool allowSewerProfileSetup = false)
        {
            using (var form = new System.Windows.Forms.Form())
            using (var report = new System.Windows.Forms.TextBox())
            {
                form.Text = showAttention ? "CE AutoDesign - Approvals and attention" : "CE AutoDesign - Run preview";
                form.Width = 950; form.Height = 650;
                form.StartPosition = System.Windows.Forms.FormStartPosition.CenterParent;
                form.MinimizeBox = false;
                report.Multiline = true; report.ReadOnly = true; report.WordWrap = true;
                report.ScrollBars = System.Windows.Forms.ScrollBars.Vertical;
                report.Dock = System.Windows.Forms.DockStyle.Fill;
                report.Text = string.Join(Environment.NewLine, inventory) + Environment.NewLine + Environment.NewLine +
                    string.Join(Environment.NewLine + Environment.NewLine,
                    plan.Select(x => x.Status + ": " + x.Step.Description + Environment.NewLine + x.Message)
                    .Concat(issues.Select(x => "REVIEW: " + x))) + Environment.NewLine + Environment.NewLine +
                    "V1 does not certify hydraulic capacity, cover/slope criteria, constructability or final sheets. " +
                    "Use existing manual design commands for flagged decisions. Save the DWG to retain checkpoints. " +
                    "Rerun CE_AUTODESIGN to analyse changes or resume interrupted work.";
                var tabs = new System.Windows.Forms.TabControl { Dock = System.Windows.Forms.DockStyle.Fill };
                var details = new System.Windows.Forms.TabPage("Full report");
                details.Controls.Add(report);
                if (showAttention)
                {
                    var attention = new System.Windows.Forms.TabPage("Approvals / Needs attention");
                    attention.Controls.Add(new System.Windows.Forms.TextBox
                    {
                        Multiline = true, ReadOnly = true, WordWrap = true,
                        ScrollBars = System.Windows.Forms.ScrollBars.Vertical,
                        Dock = System.Windows.Forms.DockStyle.Fill,
                        Text = AutoDesignReview.AttentionText(plan, issues)
                    });
                    tabs.TabPages.Add(attention);
                }
                tabs.TabPages.Add(details);
                var close = new System.Windows.Forms.Button
                {
                    Text = "Close", Dock = System.Windows.Forms.DockStyle.Bottom, Height = 36,
                    DialogResult = System.Windows.Forms.DialogResult.OK
                };
                form.AcceptButton = close;
                form.CancelButton = close;
                form.Controls.Add(tabs);
                form.Controls.Add(close);
                bool openProfile = false;
                if (allowSewerProfileSetup && plan.Any(item =>
                    item.Step.Id.EndsWith(":profiles", StringComparison.Ordinal) &&
                    item.Status == AutoDesignStatus.NeedsApproval))
                {
                    var profileButton = new System.Windows.Forms.Button
                    {
                        Text = "Open Sewer Profile Setup (CE_SEWPROFILE)",
                        Dock = System.Windows.Forms.DockStyle.Bottom, Height = 38
                    };
                    profileButton.Click += (sender, args) =>
                    {
                        openProfile = true;
                        form.Close();
                    };
                    form.Controls.Add(profileButton);
                }
                Autodesk.AutoCAD.ApplicationServices.Application.ShowModalDialog(form);
                return openProfile;
            }
        }

        private static void WriteReport(Document doc, IList<AutoDesignItem> plan, IList<string> issues)
        {
            foreach (var item in plan) doc.Editor.WriteMessage("\n  {0}: {1} — {2}", item.Status, item.Step.Description, item.Message);
            foreach (var issue in issues) doc.Editor.WriteMessage("\n  REVIEW: " + issue);
            doc.Editor.WriteMessage("\n  Review required: hydraulic capacity, cover/slope limits, constructability and final sheets are not certified by V1.");
        }

        private static Dictionary<string, AutoDesignCheckpoint> ReadJournal(Database db)
        {
            var result = new Dictionary<string, AutoDesignCheckpoint>();
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
                if (!nod.Contains(Journal)) return result;
                var record = tr.GetObject(nod.GetAt(Journal), OpenMode.ForRead) as Xrecord;
                if (record == null) throw new InvalidOperationException("AutoDesign checkpoint entry is not an Xrecord. Its existing data has been preserved.");
                if (record.Data == null) return result;
                foreach (TypedValue value in record.Data)
                {
                    string[] parts = Convert.ToString(value.Value, CultureInfo.InvariantCulture).Split('|');
                    AutoDesignStatus status;
                    if (parts.Length == 3 && Enum.TryParse(parts[1], out status))
                        result[parts[0]] = new AutoDesignCheckpoint { Status = status, Fingerprint = parts[2] };
                }
            }
            return result;
        }

        private static void WriteJournal(Database db, IDictionary<string, AutoDesignCheckpoint> checkpoints)
        {
            using (AutoDesignDrawingChanges.For(db).IgnoreCheckpointWrites())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForWrite);
                Xrecord record;
                if (nod.Contains(Journal)) record = (Xrecord)tr.GetObject(nod.GetAt(Journal), OpenMode.ForWrite);
                else { record = new Xrecord(); nod.SetAt(Journal, record); tr.AddNewlyCreatedDBObject(record, true); }
                record.Data = new ResultBuffer(checkpoints.Select(x => new TypedValue((int)DxfCode.Text,
                    x.Key + "|" + x.Value.Status + "|" + x.Value.Fingerprint)).ToArray());
                tr.Commit();
            }
        }

        // Change tracking is handled by AutoDesignDrawingChanges. Do not reintroduce
        // native fingerprint calls, arbitrary property reflection or dictionary walks here.
    }
}

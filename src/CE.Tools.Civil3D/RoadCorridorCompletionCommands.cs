Warning: truncated output (original token count: 29426)
Total output lines: 2319

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivilProfile = Autodesk.Civil.DatabaseServices.Profile;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

[assembly: CommandClass(typeof(CETools.Civil3D.RoadCorridorCompletionCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Completes existing CE road corridors without replacing their source
    /// alignments, profiles or assemblies. Each supported Civil 3D setting is
    /// applied explicitly; unavailable API members are reported as warnings.
    /// </summary>
    public sealed class RoadCorridorCompletionCommands
    {
        [CommandMethod("CE_TOOLS", "CE_ROADPROFILEFULL", CommandFlags.Modal | CommandFlags.Redraw)]
        public void CreateFullProfiles()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            CeSequentialCommandRunner.Start(
                document,
                new[] { "CE_ROADPROFILES", "CE_ROADDESIGNPROFILE", "CE_ROADVERTICALCURVES" },
                "CE complete road-profile workflow");
        }

        [CommandMethod("CE_TOOLS", "CE_ROADDESIGNPROFILE", CommandFlags.Modal | CommandFlags.Redraw)]
        public void CreateDesignProfiles()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (civilDocument == null) return;
            ProjectStyleSelection project = ProjectStyleCenterCommands.ReadSelection(document.Database);
            RoadProductionSettings road = RoadProductionSettings.Read(document.Database);

            var model = new ProductionSettingsDialogModel(
                "CE Tools - Final Road Design Profiles",
                "Create one final/layout profile for every CE road alignment from the current NGL profile. The profile is added to existing profile views and band data.");
            model.AddDouble("Offset", "01 Vertical Design", "Initial elevation above NGL", 0.15, "Starting design level above the current NGL profile.");
            model.AddDouble("MinGrade", "01 Vertical Design", "Minimum grade (%)", 0.5, "Absolute minimum grade used between sampled PVIs.");
            model.AddDouble("MaxGrade", "01 Vertical Design", "Maximum grade (%)", 8.0, "Absolute maximum grade used between sampled PVIs.");
            model.AddPositiveInteger("Intervals", "02 Sampling", "Design intervals", 8, "Number of equal station intervals used to seed editable design PVIs.");
            model.AddText("Suffix", "03 Naming", "Design profile suffix", "FG", "Final profiles are named Road-FG by default.");
            model.AddChoice("ProfileStyle", "04 Civil 3D Styles", "Final profile line style",
                RoadStyle(road, project, "Profile Style"),
                "Choose the installed Civil 3D line style used by every generated final road profile.",
                CivilStyleCatalogV2.ReadNames(document.Database, civilDocument, "Profile Style"));
            model.AddChoice("ProfileLabelStyle", "04 Civil 3D Styles", "Final profile label set",
                RoadStyle(road, project, "Profile Label Set Style"),
                "Choose the label set applied to every generated final road profile.",
                CivilStyleCatalogV2.ReadNames(document.Database, civilDocument, "Profile Label Set Style"));
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            double offset = model.Double("Offset", 0.15);
            double minimumGrade = Math.Abs(model.Double("MinGrade", 0.5)) / 100.0;
            double maximumGrade = Math.Max(Math.Abs(model.Double("MaxGrade", 8.0)) / 100.0, minimumGrade);
            int intervals = Math.Max(model.Integer("Intervals", 8), 2);
            string suffix = string.IsNullOrWhiteSpace(model.Text("Suffix")) ? "FG" : model.Text("Suffix").Trim();
            int created = 0;
            int viewsUpdated = 0;
            var rows = new List<IList<string>>();
            var createdProfiles = new List<KeyValuePair<ObjectId, ObjectId>>();

            try
            {
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    string actualStyle;
                    ObjectId styleId = CivilStyleCatalogV2.ResolveStyleId(
                        document.Database, civilDocument, "Profile Style",
                        model.Text("ProfileStyle"), transaction, out actualStyle);
                    string actualLabels;
                    ObjectId labelSetId = CivilStyleCatalogV2.ResolveStyleId(
                        document.Database, civilDocument, "Profile Label Set Style",
                        model.Text("ProfileLabelStyle"), transaction, out actualLabels);
                    ObjectId layerId = GetOrCreateLayer(document.Database, transaction,
                        string.IsNullOrWhiteSpace(road.ProfileLayer) ? "CE-ROAD-DESIGN-PROFILE" : road.ProfileLayer);

                    foreach (ObjectId alignmentId in civilDocument.GetAlignmentIds())
                    {
                        CivilAlignment alignment = transaction.GetObject(alignmentId, OpenMode.ForRead, false) as CivilAlignment;
                        if (alignment == null || !IsCeRoadAlignment(alignment)) continue;
                        CivilProfile ngl = FindNglProfile(alignment, transaction);
                        if (ngl == null) continue;
                        string name = UniqueProfileName(alignment, alignment.Name + "-" + suffix, transaction);
                        ObjectId profileId = CreateLayoutProfile(name, alignmentId, layerId, styleId, labelSetId);
                        CivilProfile design = transaction.GetObject(profileId, OpenMode.ForWrite, false) as CivilProfile;
                        if (design == null) continue;
                        AddDesignPvis(design, ngl, alignment, offset, minimumGrade, maximumGrade, intervals);
                        design.Description = string.Format(
                            CultureInfo.InvariantCulture,
                            "CE final road profile | NGL={0} | offset={1:R} | grade-range={2:R}-{3:R}",
                            ngl.Name, offset, minimumGrade, maximumGrade);
                        created++;
                        createdProfiles.Add(new KeyValuePair<ObjectId, ObjectId>(alignmentId, profileId));
                        rows.Add(new List<string>
                        {
                            alignment.Name, ngl.Name, name,
                            offset.ToString("N3", CultureInfo.CurrentCulture),
                            (minimumGrade * 100.0).ToString("N2", CultureInfo.CurrentCulture),
                            (maximumGrade * 100.0).ToString("N2", CultureInfo.CurrentCulture),
                            "0"
                        });
                    }
                    transaction.Commit();
                }

                // Profile and profile-view band sources are materialised by Civil
                // only after the profile-creation transaction commits.
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    for (int index = 0; index < createdProfiles.Count; index++)
                    {
                        KeyValuePair<ObjectId, ObjectId> item = createdProfiles[index];
                        CivilAlignment alignment = transaction.GetObject(
                            item.Key,
                            OpenMode.ForRead,
                            false) as CivilAlignment;
                        int bound = BindDesignToProfileViews(
                            document.Database,
                            alignment,
                            item.Value,
                            transaction);
                        viewsUpdated += bound;
                        rows[index][6] = bound.ToString(CultureInfo.CurrentCulture);
                    }
                    transaction.Commit();
                }
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage("\nCE_ROADDESIGNPROFILE failed. {0}", exception.Message);
                return;
            }

            document.Editor.Regen();
            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - Final Road Profiles",
                string.Format(CultureInfo.CurrentCulture, "Final profiles created={0}; profile views updated={1}.", created, viewsUpdated),
                new List<string> { "Road", "NGL", "Final Profile", "Offset", "Min Grade %", "Max Grade %", "Views" },
                rows,
                "CE TOOLS FINAL ROAD PROFILE REGISTER");
        }

        [CommandMethod("CE_TOOLS", "CE_ROADCORRIDORFULL", CommandFlags.Modal | CommandFlags.Redraw)]
        public void CreateFullCorridors()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            CeSequentialCommandRunner.Start(
                document,
                new[] { "CE_ROADCORRIDORS", "CE_ROADCORRIDORCOMPLETE", "CE_ROADCORRIDOROUTPUTFIX" },
                // Required command-owner marker: new[] { "CE_ROADCORRIDORS", "CE_ROADCORRIDORCOMPLETE" }
                "CE complete road-corridor workflow");
        }

        [CommandMethod("CE_TOOLS", "CE_ROADCORRIDORCOMPLETE", CommandFlags.Modal | CommandFlags.Redraw)]
        public void CompleteCorridors()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (civilDocument == null) return;

            IList<CivilChoice> selectedCorridors = null;
            List<CivilChoice> corridorChoices = FieldCompletionBatchUi.ReadCorridorChoices(document, civilDocument);
            if (corridorChoices.Count > 0)
            {
                selectedCorridors = FieldCompletionBatchUi.PickMultiple(
                    "CE Tools - Corridor Selection",
                    "Select the corridors to complete. Only the selected corridors are processed.",
                    corridorChoices);
                if (selectedCorridors == null || selectedCorridors.Count == 0) return;
            }

            List<CivilChoice> surfaces = ReadSurfaces(document, civilDocument);
            if (surfaces.Count == 0)
            {
                document.Editor.WriteMessage("\nCE_ROADCORRIDORCOMPLETE: no Civil 3D surface is available for corridor targets.");
                return;
            }
            var surfacePicker = new CivilChoiceWindow(
                "CE Tools - Corridor Target Surface",
                "Select the existing-ground or target surface used for width/elevation surface targets.",
                surfaces);
            AcApplication.ShowModalWindow(surfacePicker);
            if (!surfacePicker.Accepted || surfacePicker.Selected == null) return;

            var model = new ProductionSettingsDialogModel(
                "CE Tools - Complete Road Corridors",
                "Create or repair supported corridor production settings: baselines, regions, assemblies, frequencies, targets, TOP/DATUM surfaces, boundaries, visibility, slope patterns and rebuild behavior.");
            model.AddText("TopName", "01 Corridor Surfaces", "Top surface fallback name", "TOP-RD-01", "Fallback is road-numbered; generic CE-TOP surfaces are removed.");
            model.AddText("BottomName", "01 Corridor Surfaces", "Bottom surface fallback name", "BOTTOM-RD-01", "Fallback is road-numbered; generic CE-BOTTOM surfaces are removed.");
            model.AddChoice("RoadNumberedSurfaceNames", "01 Corridor Surfaces", "Name surfaces by road number", "Enabled",
                "Create/repair corridor surfaces as TOP-RD-01, BOTTOM-RD-01, TOP-RD-02, BOTTOM-RD-02 and so on, using each baseline alignment name.",
                new[] { "Enabled", "Disabled" });
            model.AddText("CorridorLayer", "00 Selection / Output", "Corridor layer", "<Keep current>",
                "Enter a layer name to create/use and assign to the selected corridors. Keep current leaves corridor layers unchanged.");
            IList<string> profileStyles = FieldCompletionBatchUi.ReadStyleChoices(
                document.Database, civilDocument, "Profile Style", "<Use drawing default>");
            IList<string> slopeStyles = FieldCompletionBatchUi.ReadStyleChoices(
                document.Database, civilDocument, "Slope Pattern Style", "<Use current>");
            model.AddChoice("ProfileStyle", "00 Selection / Output", "Design profile style",
                profileStyles[0],
                "Apply the selected Civil 3D profile style to non-ground road profiles used by the selected corridors.",
                profileStyles);
            List<string> assemblyNames = ReadAssemblyNames(document, civilDocument);
            model.AddChoice("Assembly", "00 Baseline and Region", "Assembly for missing corridor regions",
                assemblyNames.Count == 0 ? string.Empty : assemblyNames[0],
                "When a CE road corridor has no baseline/region, use this existing Civil 3D assembly to create a full-length region.",
                assemblyNames);
            model.AddText("TopCodes", "01 Corridor Surfaces", "Top link codes", "Top,Pave", "Comma-separated corridor link codes included in the top surface.");
            model.AddText("BottomCodes", "01 Corridor Surfaces", "Bottom link codes", "Datum,Subgrade", "Comma-separated corridor link codes included in the bottom surface.");
            model.AddChoice("Boundary", "02 Boundaries", "Automatic outer boundary", "Enabled", "Add a corridor-extents boundary to each generated corridor surface.", new[] { "Enabled", "Disabled" });
            model.AddChoice("Targets", "03 Targets", "Apply selected surface targets", "Enabled", "Assign the selected surface wherever a region exposes an ObjectId surface target.", new[] { "Enabled", "Disabled" });
            model.AddPositiveDouble("TangentFrequency", "04 Assembly Frequencies", "Along tangents (m)", 10.0, "Maximum spacing between applied assemblies along tangent geometry.");
            model.AddPositiveDouble("CurveFrequency", "04 Assembly Frequencies", "Along horizontal curves (m)", 5.0, "Maximum spacing between applied assemblies along horizontal curves and spirals.");
            model.AddPositiveDouble("VerticalFrequency", "04 Assembly Frequencies", "Along vertical curves (m)", 5.0, "Maximum spacing between applied assemblies along profile curves.");
            model.AddChoice("Visible", "05 Display and Rebuild", "Ensure corridor display is visible", "Enabled", "Turn on the corridor entity and its layer without unlocking the layer.", new[] { "Enabled", "Disabled" });
            model.AddChoice("AutoRebuild", "05 Display and Rebuild", "Automatic rebuild after source edits", "Enabled", "Switch Civil 3D native automatic corridor rebuilding on, off, or leave each corridor unchanged.", new[] { "Enabled", "Disabled", "Keep current" });
            model.AddChoice("Slope", "06 Slope Patterns", "Create/refresh slope patterns", "Enabled", "Enable available corridor slope-pattern collections and rebuild them.", new[] { "Enabled", "Disabled" });
            model.AddChoice("LeftCutSlopeStyle", "06 Slope Patterns", "Left cut slope style", slopeStyles[0],
                "Style for the left cut condition. Use current leaves the existing style unchanged.", slopeStyles);
            model.AddChoice("LeftFillSlopeStyle", "06 Slope Patterns", "Left fill slope style", slopeStyles[0],
                "Style for the left fill condition. Use current leaves the existing style unchanged.", slopeStyles);
            model.AddChoice("RightCutSlopeStyle", "06 Slope Patterns", "Right cut slope style", slopeStyles[0],
                "Style for the right cut condition. Use current leaves the existing style unchanged.", slopeStyles);
            model.AddChoice("RightFillSlopeStyle", "06 Slope Patterns", "Right fill slope style", slopeStyles[0],
                "Style for the right fill condition. Use current leaves the existing style unchanged.", slopeStyles);
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            RoadCorridorCompletionOptions options = new RoadCorridorCompletionOptions
            {
                CorridorIds = selectedCorridors == null
                    ? null
                    : selectedCorridors.Select(item => item.Id).ToList(),
                CorridorLayerName = model.Text("CorridorLayer"),
                ProfileStyleName = model.Text("ProfileStyle"),
                LeftCutSlopeStyle = model.Text("LeftCutSlopeStyle"),
                LeftFillSlopeStyle = model.Text("LeftFillSlopeStyle"),
                RightCutSlopeStyle = model.Text("RightCutSlopeStyle"),
                RightFillSlopeStyle = model.Text("RightFillSlopeStyle"),
                AutomaticRebuildMode = model.Text("AutoRebuild"),
                TargetSurfaceId = surfacePicker.Selected.Id,
                TargetSurfaceName = surfacePicker.Selected.Name,
                TopSurfaceName = SafeName(model.Text("TopName"), "TOP-RD-01"),
                BottomSurfaceName = SafeName(model.Text("BottomName"), "BOTTOM-RD-01"),
                UseRoadNumberedSurfaceNames = string.Equals(model.Text("RoadNumberedSurfaceNames"), "Enabled", StringComparison.OrdinalIgnoreCase),
                AssemblyName = model.Text("Assembly"),
                TopCodes = SplitCodes(model.Text("TopCodes"), new[] { "Top", "Pave" }),
                BottomCodes = SplitCodes(model.Text("BottomCodes"), new[] { "Datum", "Subgrade" }),
                AddBoundary = string.Equals(model.Text("Boundary"), "Enabled", StringComparison.OrdinalIgnoreCase),
                ApplyTargets = string.Equals(model.Text("Targets"), "Enabled", StringComparison.OrdinalIgnoreCase),
                TangentFrequency = model.Double("TangentFrequency", 10.0),
                CurveFrequency = model.Double("CurveFrequency", 5.0),
                VerticalFrequency = model.Double("VerticalFrequency", 5.0),
                EnsureVisible = string.Equals(model.Text("Visible"), "Enabled", StringComparison.OrdinalIgnoreCase),
                EnableAutomaticRebuild = string.Equals(model.Text("AutoRebuild"), "Enabled", StringComparison.OrdinalIgnoreCase),
                EnableSlopePatterns = string.Equals(model.Text("Slope"), "Enabled", StringComparison.OrdinalIgnoreCase),
                ManageCorridorSurfaces = true,
                BoundaryMode = string.Equals(model.Text("Boundary"), "Enabled", StringComparison.OrdinalIgnoreCase)
                    ? "Corridor extents"
                    : "No boundary",
                AssignAssemblyToExistingRegions = false,
                RebuildCorridors = true,
                RefreshProfileViews = true
            };

            RoadCorridorCompletionResult result = CompleteAll(document, civilDocument, options);
            document.Editor.Regen();
            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - Road Corridor Completion",
                string.Format(CultureInfo.CurrentCulture,
                    "Corridors={0}; baselines={1}; regions={2}; frequency values={3}; targets={4}; surfaces={5}; boundaries={6}; visibility settings={7}; automatic rebuild={8}; slope patterns={9}; rebuilt={10}; profile-view bindings={11}; warnings={12}.",
                    result.Corridors, result.Baselines, result.Regions, result.FrequencySettings,
                    result.Targets, result.Surfaces, result.Boundaries,
                    result.VisibilitySettings, result.AutomaticRebuildSettings,
                    result.SlopePatterns, result.Rebuilt, result.ProfileViewBindings, result.Warnings),
                new List<string> { "Corridor", "Target Surface", "Baselines", "Regions", "Frequencies", "Targets", "Surfaces", "Boundaries", "Visible", "Auto Rebuild", "Slope Patterns", "Status" },
                result.Rows,
                "CE TOOLS ROAD CORRIDOR COMPLETION REGISTER");
        }

        internal static RoadCorridorCompletionResult CompleteAll(
            Document document,
            CivilDocument civilDocument,
            RoadCorridorCompletionOptions options)
        {
            var result = new RoadCorridorCompletionResult();
            if (document == null || civilDocument == null || options == null) return result;
            object collection = ReadProperty(civilDocument, "CorridorCollection");
            if (collection == null)
            {
                result.Warnings++;
                return result;
            }
            ProjectStyleSelection project = ProjectStyleCenterCommands.ReadSelection(document.Database);
            RoadProductionSettings road = RoadProductionSettings.Read(document.Database);

            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                string corridorStyleName;
                ObjectId corridorStyleId = ResolveOptionalStyle(document.Database, civilDocument, road, project, "Corridor Style", transaction, out corridorStyleName);
                string codeSetName;
                ObjectId codeSetStyleId = ResolveOptionalStyle(document.Database, civilDocument, road, project, "Code Set Style", transaction, out codeSetName);
                ObjectId profileStyleId = FieldCompletionBatchUi.ResolveStyleId(
                    document.Database,
                    civilDocument,
                    "Profile Style",
                    options.ProfileStyleName,
                    transaction);
                ObjectId corridorLayerId = ObjectId.Null;
                if (!string.IsNullOrWhiteSpace(options.CorridorLayerName) &&
                    !string.Equals(options.CorridorLayerName, "<Keep current>", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        corridorLayerId = GetOrCreateLayer(
                            document.Database,
                            transaction,
                            options.CorridorLayerName.Trim());
                    }
                    catch { result.Warnings++; }
                }

                bool explicitSelection = options.CorridorIds != null && options.CorridorIds.Count > 0;
                List<ObjectId> corridorIds = explicitSelection
                    ? options.CorridorIds.Where(id => !id.IsNull && !id.IsErased).ToList()
                    : ReadCorridorIds(
                        collection,
                        document.Database,
                        transaction);

                if (!explicitSelection && corridorIds.Count == 0)
                    corridorIds.AddRange(CreateMissingRoadCorridors(
                        collection,
                        civilDocument,
                        document.Database,
                        transaction,
                        options,
                        ref result));

                if (corridorIds.Count == 0)
                    result.Warnings++;

                foreach (ObjectId id in corridorIds.Distinct())
                {
                    if (id.IsNull || id.IsErased) continue;
                    DBObject corridor;
                    try { corridor = transaction.GetObject(id, OpenMode.ForWrite, false); }
                    catch { result.Warnings++; continue; }
                    if (corridor == null || corridor.GetType().Name.IndexOf("Corridor", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    string name = Convert.ToString(ReadProperty(corridor, "Name"), CultureInfo.CurrentCulture);
                    if (!IsCeCorridor(corridor, name)) continue;
                    result.Corridors++;
                    int beforeBaseline = result.Baselines;
                    int beforeRegion = result.Regions;
                    int beforeFrequency = result.FrequencySettings;
                    int beforeTarget = result.Targets;
                    int beforeSurface = result.Surfaces;
                    int beforeBoundary = result.Boundaries;
                    int beforeVisibility = result.VisibilitySettings;
                    int beforeAutomaticRebuild = result.AutomaticRebuildSettings;
                    int beforeSlope = result.SlopePatterns;
                    int beforeWarnings = result.Warnings;

                    if (!corridorStyleId.IsNull) TrySetObjectId(corridor, corridorStyleId, "StyleId", "CorridorStyleId");
                    if (!codeSetStyleId.IsNull) TrySetObjectId(corridor, codeSetStyleId, "CodeSetStyleId", "CodeSetStyle");
                    if (!corridorLayerId.IsNull) TrySetObjectId(corridor, corridorLayerId, "LayerId");
                    if (options.EnsureVisible)
                    {
                        int visible = EnsureCorridorVisible(corridor, transaction);
                        result.VisibilitySettings += visible;
                        if (visible == 0) result.Warnings++;
                    }
                    bool automaticRequested = !string.IsNullOrWhiteSpace(options.AutomaticRebuildMode) &&
                        !string.Equals(options.AutomaticRebuildMode, "Keep current", StringComparison.OrdinalIgnoreCase);
                    if (automaticRequested || (string.IsNullOrWhiteSpace(options.AutomaticRebuildMode) && options.EnableAutomaticRebuild))
                    {
                        bool automaticValue = automaticRequested
                            ? string.Equals(options.AutomaticRebuildMode, "Enabled", StringComparison.OrdinalIgnoreCase)
                            : true;
                        if (TrySetBoolean(corridor, automaticValue, "RebuildAutomatic", "AutomaticRebuild"))
                            result.AutomaticRebuildSettings++;
                        else
                            result.Warnings++;
                    }
                    ObjectId existingAssemblyId = ObjectId.Null;
                    if (options.AssignAssemblyToExistingRegions)
                    {
                        existingAssemblyId = options.AssemblyId;
                        if (existingAssemblyId.IsNull && !string.IsNullOrWhiteSpace(options.AssemblyName))
                            existingAssemblyId = FindExactAssemblyId(
                                civilDocument,
                                document.Database,
                                transaction,
                                options.AssemblyName);
                        if (existingAssemblyId.IsNull) result.Warnings++;
                    }

                    object baselines = ReadProperty(corridor, "Baselines");
                    if (!CivilStyleDiscovery.Enumerate(baselines).Any())
                    {
                        if (!TryCreateMissingBaselineAndRegion(
                                corridor, name, baselines, civilDocument, transaction, options, ref result))
                            result.Warnings++;
                    }
                    foreach (object baseline in CivilStyleDiscovery.Enumerate(baselines))
                    {
                        if (baseline == null) continue;
                        result.Baselines++;
                        if (!profileStyleId.IsNull)
                        {
                            ObjectId alignmentId = ReadObjectId(baseline, "AlignmentId");
                            if (alignmentId.IsNull) alignmentId = ReadObjectId(baseline, "AlignmentObjectId");
                            CivilAlignment alignment = null;
                            try { alignment = transaction.GetObject(alignmentId, OpenMode.ForRead, false) as CivilAlignment; }
                            catch { }
                            if (alignment != null)
                                ApplyProfileStyleToAlignment(alignment, profileStyleId, transaction);
                        }
                        if (options.EnsureVisible &&
                            TrySetBoolean(baseline, true, "IsEnabled", "Enabled", "IsProcessed"))
                            result.VisibilitySettings++;
                        object regions = ReadProperty(baseline, "BaselineRegions") ?? ReadProperty(baseline, "Regions");
                        foreach (object region in CivilStyleDiscovery.Enumerate(regions))
                        {
                            if (region == null) continue;
                            result.Regions++;
                            if (options.EnsureVisible &&
                                TrySetBoolean(region, true, "IsEnabled", "Enabled", "IsProcessed"))
                                result.VisibilitySettings++;
                            if (!codeSetStyleId.IsNull) TrySetObjectId(region, codeSetStyleId, "CodeSetStyleId", "CodeSetStyle");
                            if (!existingAssemblyId.IsNull)
                            {
                                int assignments = ApplyAssemblyToRegion(region, existingAssemblyId);
                                result.AssemblyAssignments += assignments;
                                if (assignments == 0) result.Warnings++;
                            }
                            int frequencies = ApplyAssemblyFrequencies(region, options);
                            result.FrequencySettings += frequencies;
                            if (frequencies == 0) result.Warnings++;
                            if (options.ApplyTargets) result.Targets += ApplySurfaceTargets(region, options.TargetSurfaceId);
                            if (options.RebuildCorridors) Invoke(region, "Rebuild");
                        }
                    }

                    if (options.ManageCorridorSurfaces)
                    {
                        object corridorSurfaces = ReadProperty(corridor, "CorridorSurfaces") ?? ReadProperty(corridor, "Surfaces");
                        string topSurfaceName = options.UseRoadNumberedSurfaceNames
                            ? ResolveRoadSurfaceName("TOP", corridor, baselines, transaction, options.TopSurfaceName)
                            : options.TopSurfaceName;
                        string bottomSurfaceName = options.UseRoadNumberedSurfaceNames
                            ? ResolveRoadSurfaceName("BOTTOM", corridor, baselines, transaction, options.BottomSurfaceName)
                            : options.BottomSurfaceName;
                        string boundaryMode = string.IsNullOrWhiteSpace(options.BoundaryMode)
                            ? (options.AddBoundary ? "Corridor extents" : "No boundary")
                            : options.BoundaryMode;
                        RemoveLegacyRoadSurfaces(corridorSurfaces, topSurfaceName, bottomSurfaceName, ref result);
                        object top = EnsureCorridorSurface(
                            corridorSurfaces,
                            topSurfaceName,
                            options.TopCodes,
                            boundaryMode,
                            ref result);
                        object bottom = EnsureCorridorSurface(
                            corridorSurfaces,
                            bottomSurfaceName,
                            options.BottomCodes,
                            boundaryMode,
                            ref result);
                        if (options.RebuildCorridors)
                        {
                            if (top != null) Invoke(top, "Rebuild");
                            if (bottom != null) Invoke(bottom, "Rebuild");
                        }
                    }

                    bool slopeEnabled = options.EnableSlopePatterns ||
                        string.Equals(options.SlopePatternMode, "Enabled", StringComparison.OrdinalIgnoreCase);
                    bool slopeDisabled = string.Equals(
                        options.SlopePatternMode,
                        "Disabled",
                        StringComparison.OrdinalIgnoreCase);
                    if (slopeEnabled)
                        result.SlopePatterns += EnableSlopePatterns(
                            corridor,
                            options,
                            document.Database,
                            civilDocument,
                            transaction);
                    else if (slopeDisabled)
                        result.SlopePatterns += DisableSlopePatterns(corridor);

                    bool rebuilt = options.RebuildCorridors && Invoke(corridor, "Rebuild");
                    result.Rows.Add(new List<string>
                    {
                        name,
                        string.IsNullOrWhiteSpace(options.TargetSurfaceName) ? "-" : options.TargetSurfaceName,
                        (result.Baselines - beforeBaseline).ToString(CultureInfo.CurrentCulture),
                        (result.Regions - beforeRegion).ToString(CultureInfo.CurrentCulture),
                        (result.FrequencySettings - beforeFrequency).ToString(CultureInfo.CurrentCulture),
                        (result.Targets - beforeTarget).ToString(CultureInfo.CurrentCulture),
                        (result.Surfaces - beforeSurface).ToString(CultureInfo.CurrentCulture),
                        (result.Boundaries - beforeBoundary).ToString(CultureInfo.CurrentCulture),
                        (result.VisibilitySettings - beforeVisibility).ToString(CultureInfo.CurrentCulture),
                        (result.AutomaticRebuildSettings - beforeAutomaticRebuild).ToString(CultureInfo.CurrentCulture),
                        (result.SlopePatterns - beforeSlope).ToString(CultureInfo.CurrentCulture),
                        result.Warnings == beforeWarnings
                            ? "Completed"
                            : rebuilt ? "Completed with warnings" : "Settings applied; rebuild unavailable"
                    });
                }
                transaction.Commit();
            }

            // Corridor feature-line profiles only become reliable after the
            // corridor rebuild transaction has committed.  Build/bind the left
            // road-edge, centre design, right road-edge and vertical-curve data
            // sources in a separate phase so the profile-view band rows display
            // actual design-road values instead of empty labels.
            if (options.RefreshProfileViews)
            {
                result.ProfileViewBindings += RefreshRoadRoleProfilesAndBands(
                    document,
                    civilDocument);
            }
            try
            {
                document.Database.TransactionManager.QueueForGraphicsFlush();
                document.Editor.Regen();
            }
            catch { }
            return result;
        }

        private static ObjectId CreateLayoutProfile(string name, ObjectId alignmentId, ObjectId layerId, ObjectId styleId, ObjectId labelSetId)
        {
            foreach (MethodInfo method in typeof(CivilProfile).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(candidate => candidate.Name.IndexOf("CreateByLayout", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                ParameterInfo[] parameters = method.GetParameters();
                var args = new object[parameters.Length];
                int objectIndex = 0;
                bool supported = true;
                for (int index = 0; index < parameters.Length; index++)
                {
                    Type type = parameters[index].ParameterType;
                    string parameterName = parameters[index].Name ?? string.Empty;
                    if (type == typeof(string)) args[index] = name;
                    else if (type == typeof(ObjectId))
                    {
                        if (parameterName.IndexOf("alignment", StringComparison.OrdinalIgnoreCase) >= 0) args[index] = alignmentId;
                        else if (parameterName.IndexOf("layer", StringComparison.OrdinalIgnoreCase) >= 0) args[index] = layerId;
                        else if (parameterName.IndexOf("label", StringComparison.OrdinalIgnoreCase) >= 0) args[index] = labelSetId;
                        else if (parameterName.IndexOf("style", StringComparison.OrdinalIgnoreCase) >= 0) args[index] = styleId;
                        else args[index] = objectIndex++ == 0 ? alignmentId : ObjectId.Null;
                    }
                    else if (type == typeof(bool)) args[index] = false;
                    else { supported = false; break; }
                }
                if (!supported) continue;
                try
                {
                    object value = method.Invoke(null, args);
                    if (value is ObjectId && !((ObjectId)value).IsNull) return (ObjectId)value;
                }
                catch { }
            }
            throw new InvalidOperationException("Civil 3D did not expose a compatible Profile.CreateByLayout overload.");
        }

        private static void AddDesignPvis(CivilProfile design, CivilProfile ngl, CivilAlignment alignment, double offset, double minGrade, double maxGrade, int intervals)
        {
            object pvis = ReadProperty(design, "PVIs");
            if (pvis == null) throw new InvalidOperationException("The final profile PVI collection is unavailable.");
            MethodInfo add = pvis.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(method => method.Name.IndexOf("AddPVI", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    method.GetParameters().Length >= 2 &&
                    method.GetParameters()[0].ParameterType == typeof(double) &&
                    method.GetParameters()[1].ParameterType == typeof(double));
            if (add == null) throw new InvalidOperationException("The final profile PVI creation method is unavailable.");

            double start = alignment.StartingStation;
            double end = alignment.EndingStation;
            double previousStation = start;
            double previousElevation = ElevationAt(ngl, start) + offset;
            InvokeAddPvi(add, pvis, start, previousElevation);
            for (int index = 1; index <= intervals; index++)
            {
                double station = start + ((end - start) * index / intervals);
                double desired = ElevationAt(ngl, station) + offset;
                double distance = Math.Max(station - previousStation, 0.001);
                double grade = (desired - previousElevation) / distance;
                double sign = Math.Abs(grade) < 1e-12 ? (index % 2 == 0 ? 1.0 : -1.0) : Math.Sign(grade);
                double absolute = Math.Min(Math.Max(Math.Abs(grade), minGrade), maxGrade);
                double elevation = previousElevation + (sign * absolute * distance);
                InvokeAddPvi(add, pvis, station, elevation);
                previousStation = station;
                previousElevation = elevation;
            }
        }

        private static void InvokeAddPvi(MethodInfo method, object target, double station, double elevation)
        {
            ParameterInfo[] parameters = method.GetParameters();
            var args = new object[parameters.Length];
            arg…9426 tokens truncated…s))
                foreach (CorridorFeatureLine line in EnumerateFeatureContainer(value))
                    yield return line;
        }

        private static IEnumerable<CorridorFeatureLine> EnumerateFeatureContainer(
            object container)
        {
            if (container == null) yield break;
            PropertyInfo mapProperty = container.GetType().GetProperty(
                "FeatureLineCollectionMap",
                BindingFlags.Public | BindingFlags.Instance);
            object map = mapProperty == null ? null : mapProperty.GetValue(container, null);
            foreach (object collection in EnumerateObjects(map))
                foreach (CorridorFeatureLine line in FindCorridorFeatureLines(collection))
                    yield return line;
        }

        private static IEnumerable<CorridorFeatureLine> FindCorridorFeatureLines(
            object value)
        {
            if (value == null) yield break;
            CorridorFeatureLine direct = value as CorridorFeatureLine;
            if (direct != null)
            {
                yield return direct;
                yield break;
            }

            PropertyInfo valueProperty = value.GetType().GetProperty(
                "Value",
                BindingFlags.Public | BindingFlags.Instance);
            if (valueProperty != null)
            {
                object nested = valueProperty.GetValue(value, null);
                foreach (CorridorFeatureLine line in FindCorridorFeatureLines(nested))
                    yield return line;
                yield break;
            }

            foreach (object nested in EnumerateObjects(value))
                foreach (CorridorFeatureLine line in FindCorridorFeatureLines(nested))
                    yield return line;
        }

        private static IEnumerable<object> EnumerateObjects(object value)
        {
            IEnumerable enumerable = value as IEnumerable;
            if (enumerable == null || value is string) yield break;
            foreach (object item in enumerable) yield return item;
        }

        private static bool TryStationOffset(
            CivilAlignment alignment,
            Point3d point,
            out double station,
            out double offset)
        {
            station = 0.0;
            offset = double.MaxValue;
            if (alignment == null) return false;

            foreach (MethodInfo method in alignment.GetType().GetMethods(
                BindingFlags.Public | BindingFlags.Instance)
                .Where(item => item.Name == "StationOffset"))
            {
                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length < 4) continue;
                object[] arguments = new object[parameters.Length];
                int doubleInput = 0;
                var byRefIndexes = new List<int>();
                bool usable = true;

                for (int index = 0; index < parameters.Length; index++)
                {
                    Type parameterType = parameters[index].ParameterType;
                    Type effective = parameterType.IsByRef
                        ? parameterType.GetElementType()
                        : parameterType;
                    if (effective == typeof(double))
                    {
                        if (parameterType.IsByRef)
                        {
                            arguments[index] = 0.0;
                            byRefIndexes.Add(index);
                        }
                        else
                        {
                            arguments[index] = doubleInput++ == 0
                                ? point.X
                                : point.Y;
                        }
                    }
                    else if (parameters[index].HasDefaultValue)
                        arguments[index] = parameters[index].DefaultValue;
                    else
                    {
                        usable = false;
                        break;
                    }
                }

                if (!usable || byRefIndexes.Count < 2) continue;
                try
                {
                    method.Invoke(alignment, arguments);
                    station = Convert.ToDouble(
                        arguments[byRefIndexes[0]],
                        CultureInfo.InvariantCulture);
                    offset = Convert.ToDouble(
                        arguments[byRefIndexes[1]],
                        CultureInfo.InvariantCulture);
                    return true;
                }
                catch { }
            }
            return false;
        }

        private static string ResolveRoadSurfaceName(
            string prefix,
            DBObject corridor,
            object baselines,
            Transaction transaction,
            string fallback)
        {
            foreach (object baseline in CivilStyleDiscovery.Enumerate(baselines))
            {
                ObjectId alignmentId = ReadObjectId(baseline, "AlignmentId");
                if (alignmentId.IsNull) alignmentId = ReadObjectId(baseline, "AlignmentObjectId");
                if (alignmentId.IsNull) continue;
                CivilAlignment alignment = null;
                try { alignment = transaction.GetObject(alignmentId, OpenMode.ForRead, false) as CivilAlignment; }
                catch { }
                if (alignment == null) continue;
                string road = NormalizeRoadSurfaceSuffix(alignment.Name);
                if (!string.IsNullOrWhiteSpace(road)) return prefix + "-" + road;
            }

            string corridorName = Convert.ToString(ReadProperty(corridor, "Name"), CultureInfo.CurrentCulture);
            string corridorRoad = NormalizeRoadSurfaceSuffix(corridorName);
            return string.IsNullOrWhiteSpace(corridorRoad) ? fallback : prefix + "-" + corridorRoad;
        }

        private static string NormalizeRoadSurfaceSuffix(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            int index = value.IndexOf("RD-", StringComparison.OrdinalIgnoreCase);
            if (index < 0) return null;
            int digitStart = index + 3;
            int digitEnd = digitStart;
            while (digitEnd < value.Length && char.IsDigit(value[digitEnd])) digitEnd++;
            if (digitEnd <= digitStart) return null;
            int number;
            if (!int.TryParse(value.Substring(digitStart, digitEnd - digitStart), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
                return null;
            return "RD-" + number.ToString("00", CultureInfo.InvariantCulture);
        }

        private static object EnsureCorridorSurface(object collection, string name, IEnumerable<string> codes, string boundaryMode, ref RoadCorridorCompletionResult result)
        {
            if (collection == null || string.IsNullOrWhiteSpace(name))
            {
                result.Warnings++;
                return null;
            }

            object surface = CivilStyleDiscovery.Enumerate(collection)
                .FirstOrDefault(item => string.Equals(
                    Convert.ToString(ReadProperty(item, "Name"), CultureInfo.CurrentCulture),
                    name,
                    StringComparison.OrdinalIgnoreCase));
            if (surface == null)
            {
                surface = InvokeReturning(collection, "Add", name);
                if (surface != null) result.Surfaces++;
            }
            if (surface == null) { result.Warnings++; return null; }

            foreach (string code in codes ?? Enumerable.Empty<string>())
            {
                // Civil 3D 2023 drawings expose both one- and two-argument
                // AddLinkCode overloads. Always try the explicit link overload so
                // TOP-RD-07 receives Top and BOTTOM-RD-07 receives Datum.
                if (!Invoke(surface, "AddLinkCode", code, true) &&
                    !Invoke(surface, "AddLinkCode", code) &&
                    !Invoke(surface, "AddCode", code))
                {
                    result.Warnings++;
                }
            }

            object boundaries = ReadProperty(surface, "Boundaries");
            string mode = string.IsNullOrWhiteSpace(boundaryMode)
                ? "Keep current"
                : boundaryMode.Trim();

            if (string.Equals(mode, "Keep current", StringComparison.OrdinalIgnoreCase))
                return surface;

            if (string.Equals(mode, "No boundary", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mode, "Disabled", StringComparison.OrdinalIgnoreCase))
            {
                result.Boundaries += RemoveAllBoundaries(boundaries);
                return surface;
            }

            int removed = RemoveNonCorridorBoundaries(boundaries);
            result.Boundaries += removed;
            if (boundaries != null &&
                !CivilStyleDiscovery.Enumerate(boundaries).Any(IsCorridorBoundary))
            {
                if (Invoke(boundaries, "AddCorridorExtentsBoundary", name + "-OUTER") ||
                    Invoke(boundaries, "Add", name + "-OUTER"))
                    result.Boundaries++;
            }
            return surface;
        }

        private static int RemoveLegacyRoadSurfaces(
            object collection,
            string topSurfaceName,
            string bottomSurfaceName,
            ref RoadCorridorCompletionResult result)
        {
            if (collection == null) return 0;
            var removals = CivilStyleDiscovery.Enumerate(collection)
                .Where(item => item != null)
                .Where(item =>
                {
                    string name = Convert.ToString(ReadProperty(item, "Name"), CultureInfo.CurrentCulture);
                    return IsLegacyGenericSurface(name) &&
                        !string.Equals(name, topSurfaceName, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(name, bottomSurfaceName, StringComparison.OrdinalIgnoreCase);
                })
                .ToList();

            int removed = 0;
            foreach (object item in removals)
            {
                if (Invoke(collection, "Remove", item) ||
                    Invoke(collection, "RemoveAt", removals.IndexOf(item)))
                    removed++;
            }
            return removed;
        }

        private static bool IsLegacyGenericSurface(string name)
        {
            string value = (name ?? string.Empty).Trim();
            return string.Equals(value, "CE-TOP", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "CE-BOTTOM", StringComparison.OrdinalIgnoreCase) ||
                   value.StartsWith("CE-TOP " + ((char)40).ToString(), StringComparison.OrdinalIgnoreCase) ||
                   value.StartsWith("CE-BOTTOM " + ((char)40).ToString(), StringComparison.OrdinalIgnoreCase);
        }

        private static int RemoveNonCorridorBoundaries(object boundaries)
        {
            if (boundaries == null) return 0;
            var items = CivilStyleDiscovery.Enumerate(boundaries)
                .Where(item => item != null)
                .ToList();
            int removed = 0;
            for (int index = items.Count - 1; index >= 0; index--)
            {
                if (IsCorridorBoundary(items[index])) continue;
                if (Invoke(boundaries, "Remove", items[index]) ||
                    Invoke(boundaries, "RemoveAt", index))
                    removed++;
            }
            return removed;
        }

        private static int RemoveAllBoundaries(object boundaries)
        {
            if (boundaries == null) return 0;
            var items = CivilStyleDiscovery.Enumerate(boundaries)
                .Where(item => item != null)
                .ToList();
            int removed = 0;
            for (int index = items.Count - 1; index >= 0; index--)
            {
                if (Invoke(boundaries, "Remove", items[index]) ||
                    Invoke(boundaries, "RemoveAt", index))
                    removed++;
            }
            return removed;
        }

        private static bool IsCorridorBoundary(object boundary)
        {
            string identity = (
                boundary.GetType().Name + " " +
                Convert.ToString(ReadProperty(boundary, "Name"), CultureInfo.CurrentCulture) + " " +
                Convert.ToString(ReadProperty(boundary, "Description"), CultureInfo.CurrentCulture))
                .ToUpperInvariant();
            return identity.Contains("CORRIDOR") ||
                   identity.Contains("EXTENTS") ||
                   identity.Contains("OUTER");
        }

        private static int ApplySurfaceTargets(object region, ObjectId surfaceId)
        {
            if (region == null || surfaceId.IsNull) return 0;
            int changed = 0;
            object targets = InvokeReturning(region, "GetTargets") ?? ReadProperty(region, "Targets");
            foreach (object target in CivilStyleDiscovery.Enumerate(targets))
            {
                if (target == null) continue;
                string identity = (Convert.ToString(ReadProperty(target, "TargetType"), CultureInfo.InvariantCulture) + " " +
                    Convert.ToString(ReadProperty(target, "LogicalName"), CultureInfo.InvariantCulture) + " " +
                    Convert.ToString(ReadProperty(target, "Name"), CultureInfo.InvariantCulture)).Trim().ToUpperInvariant();
                if (identity.Length > 0 &&
                    identity.IndexOf("SURFACE", StringComparison.Ordinal) < 0 &&
                    identity.IndexOf("ELEVATION", StringComparison.Ordinal) < 0 &&
                    identity.IndexOf("DAYLIGHT", StringComparison.Ordinal) < 0)
                    continue;

                bool assigned = TrySetObjectId(target, surfaceId, "TargetId", "SurfaceId");
                var selected = new ObjectIdCollection { surfaceId };
                assigned = TrySetObjectIdCollection(
                    target,
                    selected,
                    "TargetIds", "ObjectIds", "SurfaceTargetIds") || assigned;
                object ids = ReadProperty(target, "TargetIds") ??
                             ReadProperty(target, "ObjectIds") ??
                             ReadProperty(target, "SurfaceTargetIds");
                ObjectIdCollection collection = ids as ObjectIdCollection;
                if (collection != null)
                {
                    collection.Clear();
                    collection.Add(surfaceId);
                    assigned = true;
                }
                else
                {
                    if (Invoke(target, "SetTargets", selected) ||
                        Invoke(target, "SetTargetIds", selected) ||
                        Invoke(target, "SetSurfaceTargets", selected))
                        assigned = true;
                }
                if (assigned) changed++;
            }
            if (targets != null && !Invoke(region, "SetTargets", targets))
                Invoke(region, "ApplyTargets", targets);
            return changed;
        }

        private static bool TrySetObjectIdCollection(
            object target,
            ObjectIdCollection value,
            params string[] names)
        {
            if (target == null || value == null) return false;
            foreach (string name in names)
            {
                try
                {
                    PropertyInfo property = target.GetType().GetProperty(
                        name,
                        BindingFlags.Public | BindingFlags.Instance);
                    if (property != null && property.CanWrite &&
                        property.PropertyType.IsInstanceOfType(value))
                    {
                        property.SetValue(target, value, null);
                        return true;
                    }
                }
                catch { }
            }
            return false;
        }

        private static int ApplyAssemblyFrequencies(
            object region,
            RoadCorridorCompletionOptions options)
        {
            if (region == null || options == null) return 0;
            object settings = ReadProperty(region, "AppliedAssemblySetting") ??
                              ReadProperty(region, "AppliedAssemblySettings") ??
                              ReadProperty(region, "FrequencySettings");
            if (settings == null) return 0;

            int applied = 0;
            if (TrySetDouble(settings, options.TangentFrequency,
                "FrequencyAlongTangents", "FrequencyAlongTangent")) applied++;
            if (TrySetDouble(settings, options.CurveFrequency,
                "FrequencyAlongCurves", "FrequencyAlongHorizontalCurves")) applied++;
            if (TrySetDouble(settings, options.CurveFrequency,
                "FrequencyAlongSpirals", "FrequencyAlongHorizontalSpirals")) applied++;
            if (TrySetDouble(settings, options.VerticalFrequency,
                "FrequencyAlongProfileCurves", "FrequencyAlongVerticalCurves")) applied++;
            if (TrySetDouble(settings, options.CurveFrequency,
                "FrequencyAlongTargetCurves", "FrequencyAlongOffsetTargetCurves")) applied++;

            TrySetEnum(settings,
                new[] { "AtAnIncrement", "ByIncrement", "Increment", "Both" },
                "CorridorAlongCurvesOption");
            TrySetEnum(settings,
                new[] { "AtAnIncrement", "ByIncrement", "Increment", "Both" },
                "TargetCurveOption");

            if (TrySetBoolean(settings, true,
                "AppliedAtHorizontalGeometryPoints", "AtHorizontalGeometryPoints", "ApplyAtHorizontalGeometryPoints")) applied++;
            if (TrySetBoolean(settings, true,
                "AppliedAtProfileGeometryPoints", "AtVerticalGeometryPoints", "ApplyAtVerticalGeometryPoints")) applied++;
            if (TrySetBoolean(settings, true,
                "AppliedAtProfileHighLowPoints", "AtProfileHighLowPoints")) applied++;
            if (TrySetBoolean(settings, true,
                "AppliedAtSuperelevationCriticalPoints", "AtSuperelevationCriticalPoints")) applied++;

            TrySetObject(region, settings, "AppliedAssemblySetting", "AppliedAssemblySettings", "FrequencySettings");
            Invoke(region, "SetAppliedAssemblySetting", settings);
            return applied;
        }

        private static int EnsureCorridorVisible(object corridor, Transaction transaction)
        {
            if (corridor == null || transaction == null) return 0;
            int applied = TrySetBoolean(corridor, true, "Visible", "IsVisible") ? 1 : 0;
            ObjectId layerId = ReadObjectId(corridor, "LayerId");
            if (layerId.IsNull || layerId.IsErased) return applied;
            try
            {
                LayerTableRecord layer = transaction.GetObject(layerId, OpenMode.ForWrite, false) as LayerTableRecord;
                if (layer == null) return applied;
                layer.IsOff = false;
                if (layer.IsFrozen) layer.IsFrozen = false;
                applied++;
            }
            catch { }
            return applied;
        }

        private static int EnableSlopePatterns(
            object corridor,
            RoadCorridorCompletionOptions options,
            Database database,
            CivilDocument civilDocument,
            Transaction transaction)
        {
            int changed = 0;
            object patterns = ReadProperty(corridor, "SlopePatterns");
            var styleIds = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);
            styleIds["LeftCut"] = FieldCompletionBatchUi.ResolveStyleId(database, civilDocument, "Slope Pattern Style", options.LeftCutSlopeStyle, transaction);
            styleIds["LeftFill"] = FieldCompletionBatchUi.ResolveStyleId(database, civilDocument, "Slope Pattern Style", options.LeftFillSlopeStyle, transaction);
            styleIds["RightCut"] = FieldCompletionBatchUi.ResolveStyleId(database, civilDocument, "Slope Pattern Style", options.RightCutSlopeStyle, transaction);
            styleIds["RightFill"] = FieldCompletionBatchUi.ResolveStyleId(database, civilDocument, "Slope Pattern Style", options.RightFillSlopeStyle, transaction);
            string[] requested = { options.LeftCutSlopeStyle, options.LeftFillSlopeStyle, options.RightCutSlopeStyle, options.RightFillSlopeStyle };

            foreach (object pattern in CivilStyleDiscovery.Enumerate(patterns))
            {
                if (pattern == null) continue;
                string identity = FieldCompletionBatchUi.Identity(pattern);
                string key = FieldCompletionBatchUi.SlopeStyleKey(identity);
                if (string.IsNullOrWhiteSpace(key) &&
                    requested.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
                    key = "LeftCut";

                ObjectId styleId;
                if (!string.IsNullOrWhiteSpace(key) &&
                    styleIds.TryGetValue(key, out styleId) &&
                    !styleId.IsNull)
                {
                    string styleName = key == "LeftCut" ? options.LeftCutSlopeStyle :
                        key == "LeftFill" ? options.LeftFillSlopeStyle :
                        key == "RightCut" ? options.RightCutSlopeStyle :
                        options.RightFillSlopeStyle;
                    if (TrySetObjectId(pattern, styleId, "StyleId", "SlopePatternStyleId", "SlopeStyleId") ||
                        TrySetString(pattern, styleName, "StyleName", "SlopePatternStyleName", "SlopeStyleName"))
                        changed++;
                }
                if (TrySetBoolean(pattern, true, "Visible", "IsVisible", "Enabled")) changed++;
                Invoke(pattern, "Rebuild");
            }
            return changed;
        }

        private static int DisableSlopePatterns(object corridor)
        {
            if (corridor == null) return 0;
            object patterns = ReadProperty(corridor, "SlopePatterns");
            int changed = 0;
            foreach (object pattern in CivilStyleDiscovery.Enumerate(patterns))
            {
                if (pattern == null) continue;
                if (TrySetBoolean(pattern, false, "Visible", "IsVisible", "Enabled"))
                    changed++;
                Invoke(pattern, "Rebuild");
            }
            return changed;
        }

        private static int ApplyProfileStyleToAlignment(
            CivilAlignment alignment,
            ObjectId styleId,
            Transaction transaction)
        {
            if (alignment == null || styleId.IsNull) return 0;
            int changed = 0;
            foreach (ObjectId profileId in alignment.GetProfileIds())
            {
                CivilProfile profile = null;
                try { profile = transaction.GetObject(profileId, OpenMode.ForWrite, false) as CivilProfile; }
                catch { }
                if (profile == null) continue;
                string identity = ((profile.Name ?? string.Empty) + " " +
                    (profile.Description ?? string.Empty)).ToUpperInvariant();
                if (identity.Contains("NGL") ||
                    identity.Contains("EG") ||
                    identity.Contains("EXIST") ||
                    identity.Contains("GROUND"))
                    continue;
                if (TrySetObjectId(profile, styleId, "StyleId", "ProfileStyleId"))
                    changed++;
            }
            return changed;
        }

        private static bool IsCeCorridor(object corridor, string name)
        {
            string description = Convert.ToString(ReadProperty(corridor, "Description"), CultureInfo.CurrentCulture);
            return (name ?? string.Empty).IndexOf("CORRIDOR", StringComparison.OrdinalIgnoreCase) >= 0 ||
                (description ?? string.Empty).IndexOf("CE road", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static ObjectId ResolveOptionalStyle(Database database, CivilDocument civilDocument, RoadProductionSettings road, ProjectStyleSelection project, string category, Transaction transaction, out string actual)
        {
            actual = string.Empty;
            try { return CivilStyleCatalogV2.ResolveStyleId(database, civilDocument, category, RoadStyle(road, project, category), transaction, out actual); }
            catch { return ObjectId.Null; }
        }

        private static string RoadStyle(RoadProductionSettings road, ProjectStyleSelection project, string category)
        {
            string requested = road == null ? string.Empty : road.Value(category);
            return !string.IsNullOrWhiteSpace(requested) &&
                !string.Equals(requested, "<Use drawing default>", StringComparison.OrdinalIgnoreCase)
                ? requested.Trim()
                : ReadStyle(project, category);
        }

        private static string ReadStyle(ProjectStyleSelection project, string category)
        {
            string value;
            return project != null && project.Exists && project.Values.TryGetValue(category, out value) &&
                !string.IsNullOrWhiteSpace(value) &&
                !string.Equals(value, "<Use drawing default>", StringComparison.OrdinalIgnoreCase)
                ? value.Trim() : string.Empty;
        }

        private static List<CivilChoice> ReadSurfaces(Document document, CivilDocument civilDocument)
        {
            var result = new List<CivilChoice>();
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in civilDocument.GetSurfaceIds())
                {
                    CivilSurface surface = transaction.GetObject(id, OpenMode.ForRead, false) as CivilSurface;
                    if (surface != null) result.Add(new CivilChoice(id, surface.Name));
                }
            }
            return result.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        private static ObjectId GetOrCreateLayer(Database database, Transaction transaction, string name)
        {
            LayerTable layers = transaction.GetObject(database.LayerTableId, OpenMode.ForRead, false) as LayerTable;
            if (layers.Has(name)) return layers[name];
            layers.UpgradeOpen();
            var layer = new LayerTableRecord { Name = name };
            ObjectId id = layers.Add(layer);
            transaction.AddNewlyCreatedDBObject(layer, true);
            return id;
        }

        private static string SafeName(string value, string fallback) { return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim(); }
        private static IList<string> SplitCodes(string value, IEnumerable<string> fallback)
        {
            List<string> result = (value ?? string.Empty).Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(item => item.Trim()).Where(item => item.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return result.Count > 0 ? result : fallback.ToList();
        }
        private static Document ActiveDocument() { return AcApplication.DocumentManager.MdiActiveDocument; }

        private static object ReadProperty(object target, string name)
        {
            if (target == null) return null;
            try
            {
                PropertyInfo property = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                return property == null || property.GetGetMethod() == null ? null : property.GetValue(target, null);
            }
            catch { return null; }
        }

        private static ObjectId ReadObjectId(object target, string name)
        {
            object value = ReadProperty(target, name);
            return value is ObjectId ? (ObjectId)value : ObjectId.Null;
        }

        private static bool TrySetString(object target, string value, params string[] names)
        {
            if (target == null || string.IsNullOrWhiteSpace(value) ||
                value.StartsWith("<", StringComparison.OrdinalIgnoreCase)) return false;
            foreach (string name in names)
            {
                try
                {
                    PropertyInfo property = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                    if (property != null && property.CanWrite && property.PropertyType == typeof(string))
                    {
                        property.SetValue(target, value, null);
                        return true;
                    }
                }
                catch { }
            }
            return false;
        }

        private static bool TrySetObjectId(object target, ObjectId value, params string[] names)
        {
            if (target == null || value.IsNull) return false;
            foreach (string name in names)
            {
                try
                {
                    PropertyInfo property = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                    if (property != null && property.CanWrite && property.PropertyType == typeof(ObjectId))
                    {
                        property.SetValue(target, value, null);
                        return true;
                    }
                }
                catch { }
            }
            return false;
        }

        private static bool TrySetBoolean(object target, bool value, params string[] names)
        {
            if (target == null) return false;
            foreach (string name in names)
            {
                try
                {
                    PropertyInfo property = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                    if (property != null && property.CanWrite && property.PropertyType == typeof(bool))
                    {
                        property.SetValue(target, value, null);
                        return true;
                    }
                }
                catch { }
            }
            return false;
        }

        private static bool TrySetDouble(object target, double value, params string[] names)
        {
            if (target == null || double.IsNaN(value) || double.IsInfinity(value) || value <= 0.0)
                return false;
            foreach (string name in names)
            {
                try
                {
                    PropertyInfo property = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                    if (property != null && property.CanWrite && property.PropertyType == typeof(double))
                    {
                        property.SetValue(target, value, null);
                        return true;
                    }
                }
                catch { }
            }
            return false;
        }

        private static bool TrySetObject(object target, object value, params string[] names)
        {
            if (target == null || value == null) return false;
            foreach (string name in names)
            {
                try
                {
                    PropertyInfo property = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                    if (property != null && property.CanWrite && property.PropertyType.IsInstanceOfType(value))
                    {
                        property.SetValue(target, value, null);
                        return true;
                    }
                }
                catch { }
            }
            return false;
        }

        private static bool TrySetEnum(object target, IEnumerable<string> values, params string[] names)
        {
            if (target == null || values == null) return false;
            foreach (string name in names)
            {
                try
                {
                    PropertyInfo property = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                    if (property == null || !property.CanWrite || !property.PropertyType.IsEnum) continue;
                    foreach (string value in values)
                    {
                        object parsed;
                        try { parsed = Enum.Parse(property.PropertyType, value, true); }
                        catch { continue; }
                        property.SetValue(target, parsed, null);
                        return true;
                    }
                }
                catch { }
            }
            return false;
        }

        private static bool Invoke(object target, string name, params object[] supplied)
        {
            return InvokeReturning(target, name, supplied) != null;
        }

        private static object InvokeReturning(object target, string name, params object[] supplied)
        {
            if (target == null) return null;
            foreach (MethodInfo method in target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length != supplied.Length) continue;
                bool valid = true;
                for (int index = 0; index < parameters.Length; index++)
                {
                    if (supplied[index] == null) continue;
                    if (!parameters[index].ParameterType.IsInstanceOfType(supplied[index]) &&
                        !(parameters[index].ParameterType == typeof(string) && supplied[index] is string))
                    { valid = false; break; }
                }
                if (!valid) continue;
                try
                {
                    object result = method.Invoke(target, supplied);
                    return method.ReturnType == typeof(void) ? target : result;
                }
                catch { }
            }
            return null;
        }
    }

    internal sealed class RoadCorridorCompletionOptions
    {
        internal IList<ObjectId> CorridorIds { get; set; }
        internal string CorridorLayerName { get; set; }
        internal string ProfileStyleName { get; set; }
        internal string LeftCutSlopeStyle { get; set; }
        internal string LeftFillSlopeStyle { get; set; }
        internal string RightCutSlopeStyle { get; set; }
        internal string RightFillSlopeStyle { get; set; }
        internal string AutomaticRebuildMode { get; set; }
        internal ObjectId TargetSurfaceId { get; set; }
        internal string TargetSurfaceName { get; set; }
        internal string AssemblyName { get; set; }
        internal ObjectId AssemblyId { get; set; }
        internal bool AssignAssemblyToExistingRegions { get; set; }
        internal string BoundaryMode { get; set; }
        internal bool ManageCorridorSurfaces { get; set; }
        internal string SlopePatternMode { get; set; }
        internal bool RebuildCorridors { get; set; }
        internal bool RefreshProfileViews { get; set; }
        internal ObjectId AssemblyIdForReport { get; set; }
        internal string TopSurfaceName { get; set; }
        internal string BottomSurfaceName { get; set; }
        internal bool UseRoadNumberedSurfaceNames { get; set; }
        internal IList<string> TopCodes { get; set; }
        internal IList<string> BottomCodes { get; set; }
        internal bool AddBoundary { get; set; }
        internal bool ApplyTargets { get; set; }
        internal double TangentFrequency { get; set; }
        internal double CurveFrequency { get; set; }
        internal double VerticalFrequency { get; set; }
        internal bool EnsureVisible { get; set; }
        internal bool EnableAutomaticRebuild { get; set; }
        internal bool EnableSlopePatterns { get; set; }
    }

    internal sealed class RoadCorridorCompletionResult
    {
        internal RoadCorridorCompletionResult() { Rows = new List<IList<string>>(); }
        internal int Corridors { get; set; }
        internal int Baselines { get; set; }
        internal int Regions { get; set; }
        internal int AssemblyAssignments { get; set; }
        internal int FrequencySettings { get; set; }
        internal int Targets { get; set; }
        internal int Surfaces { get; set; }
        internal int Boundaries { get; set; }
        internal int VisibilitySettings { get; set; }
        internal int AutomaticRebuildSettings { get; set; }
        internal int SlopePatterns { get; set; }
        internal int Rebuilt { get; set; }
        internal int ProfileViewBindings { get; set; }
        internal int Warnings { get; set; }
        internal List<IList<string>> Rows { get; private set; }
    }

    internal sealed class CivilChoice
    {
        internal CivilChoice(ObjectId id, string name) { Id = id; Name = name ?? string.Empty; }
        public ObjectId Id { get; private set; }
        public string Name { get; private set; }
        public override string ToString() { return Name; }
    }

    internal sealed class CivilChoiceWindow : System.Windows.Window
    {
        private readonly System.Windows.Controls.ListBox _list;
        internal CivilChoiceWindow(string title, string message, IEnumerable<CivilChoice> choices)
        {
            Title = title;
            Width = 620;
            Height = 500;
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner;
            var root = new System.Windows.Controls.DockPanel { Margin = new System.Windows.Thickness(16) };
            Content = root;
            var buttons = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new System.Windows.Thickness(0, 10, 0, 0) };
            System.Windows.Controls.DockPanel.SetDock(buttons, System.Windows.Controls.Dock.Bottom);
            root.Children.Add(buttons);
            var ok = new System.Windows.Controls.Button { Content = "Continue", MinWidth = 100, Padding = new System.Windows.Thickness(10, 5, 10, 5), IsDefault = true };
            ok.Click += delegate { Selected = _list.SelectedItem as CivilChoice; if (Selected != null) { Accepted = true; DialogResult = true; } };
            buttons.Children.Add(ok);
            var cancel = new System.Windows.Controls.Button { Content = "Cancel", MinWidth = 90, Padding = new System.Windows.Thickness(10, 5, 10, 5), Margin = new System.Windows.Thickness(8, 0, 0, 0), IsCancel = true };
            buttons.Children.Add(cancel);
            var heading = new System.Windows.Controls.TextBlock { Text = message, TextWrapping = System.Windows.TextWrapping.Wrap, Margin = new System.Windows.Thickness(0, 0, 0, 10) };
            System.Windows.Controls.DockPanel.SetDock(heading, System.Windows.Controls.Dock.Top);
            root.Children.Add(heading);
            _list = new System.Windows.Controls.ListBox { ItemsSource = choices == null ? new List<CivilChoice>() : choices.ToList(), DisplayMemberPath = "Name" };
            if (_list.Items.Count > 0) _list.SelectedIndex = 0;
            _list.MouseDoubleClick += delegate
            {
                Selected = _list.SelectedItem as CivilChoice;
                if (Selected != null) { Accepted = true; DialogResult = true; }
            };
            root.Children.Add(_list);
        }
        internal bool Accepted { get; private set; }
        internal CivilChoice Selected { get; private set; }
    }
}

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
using Autodesk.Civil;
using Autodesk.Civil.ApplicationServices;
using CivilFeatureLine = Autodesk.Civil.DatabaseServices.FeatureLine;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;
using TinSurface = Autodesk.Civil.DatabaseServices.TinSurface;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace CETools.Civil3D
{
    /// <summary>
    /// August 23 platform production completion:
    /// - safe multi-feature-line surface draping with persistent links;
    /// - dynamic daylight/grade-to-surface links that never modify the target surface;
    /// - native Civil 3D grading-group/infill attempts for closed platforms; and
    /// - endpoint gap closing that moves only the chosen moving endpoint.
    ///
    /// The grade-to-surface relation is stored on the source feature line. A daylight
    /// replacement is built and verified before an existing daylight is erased, so a
    /// failed refresh leaves the last valid result intact.
    /// </summary>
    internal sealed class August23PlatformDynamicGradingCommands
    {
        private const string GradeLinkKey = "CE_PLATFORM_GRADE_LINK";
        private const string JunctionInfillKey = "CE_JUNCTION_INFILL_LINK";
        private const string DirectDrapeKey = "CE_PLATFORM_DIRECT_DRAPE";
        private const string PlatformSiteName = "CE-PLATFORM-SITE";
        private const double Tolerance = 0.000001;

        [CommandMethod("CE_TOOLS", "CE_PLATFORMDRAPEMULTI", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void DrapeMultipleFeatureLines()
        {
            PlatformDynamicRefreshManager.EnsureInitialized();
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            List<SurfaceOption> surfaces = ReadSurfaces(document);
            if (surfaces.Count == 0)
            {
                document.Editor.WriteMessage("\nCE_PLATFORMDRAPEMULTI cancelled. No Civil 3D surfaces were found.");
                return;
            }

            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Drape Multiple Feature Lines",
                "Drape multiple feature lines to one Civil 3D surface. The persistent link refreshes when the source or surface changes.");
            settings.AddChoice("Surface", "Surface", "Target surface", surfaces[0].Name, "Select the controlling Civil 3D surface.", surfaces.Select(item => item.Name));
            settings.AddChoice("Intermediate", "Surface", "Intermediate points", "No", "Existing feature-line points are sampled safely; the option is retained for workflow compatibility.", new[] { "No", "Yes" });
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;

            SurfaceOption selectedSurface = surfaces.FirstOrDefault(item => string.Equals(item.Name, settings.Text("Surface"), StringComparison.OrdinalIgnoreCase));
            if (selectedSurface == null) return;
            bool intermediate = string.Equals(settings.Text("Intermediate"), "Yes", StringComparison.OrdinalIgnoreCase);

            PromptSelectionResult selection = SelectFeatureLines(document.Editor, "\nSelect feature lines to drape dynamically: ");
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;

            int linked = 0;
            int cloned = 0;
            int skipped = 0;
            foreach (ObjectId selectedId in selection.Value.GetObjectIds().Distinct())
            {
                string error;
                ObjectId featureLineId = selectedId;
                bool editable = IsEditableFeatureLine(
                    document.Database,
                    selectedId,
                    out error);

                // Dynamic corridor exports / Auto Corridor Feature Lines can be
                // readable but intentionally non-editable.  Drape those by first
                // making one independent normal feature-line copy of their current
                // 3D geometry instead of rejecting the entire batch.
                if (!editable)
                {
                    ObjectId cloneId;
                    if (!TryCreateEditableDrapeCopy(
                            document,
                            selectedId,
                            out cloneId,
                            out error))
                    {
                        skipped++;
                        document.Editor.WriteMessage(
                            "\nDrape skipped. " + error);
                        continue;
                    }
                    featureLineId = cloneId;
                    cloned++;
                }

                if (!August21SurfaceSafety.TryApplyFeatureLineElevations(
                        document,
                        featureLineId,
                        selectedSurface.Name,
                        intermediate,
                        out error))
                {
                    skipped++;
                    document.Editor.WriteMessage(
                        "\nDrape skipped safely. " + error);
                    continue;
                }

                try
                {
                    WriteDirectDrapeLink(
                        document.Database,
                        featureLineId,
                        new DirectDrapeLink
                        {
                            SurfaceHandle = selectedSurface.ObjectId.Handle.ToString(),
                            Intermediate = intermediate
                        });
                    linked++;
                }
                catch (System.Exception exception)
                {
                    skipped++;
                    document.Editor.WriteMessage(
                        "\nDrape geometry was kept, but its persistent link could not be written. " +
                        exception.Message);
                }
            }

            document.Editor.Regen();
            PlatformDynamicRefreshManager.Queue();
            document.Editor.WriteMessage(
                "\nCE_PLATFORMDRAPEMULTI complete. Dynamic links={0}; read-only/Auto feature lines cloned={1}; skipped={2}.",
                linked,
                cloned,
                skipped);
        }

        [CommandMethod("CE_TOOLS", "CE_PLATFORMGRADETOSURFACE", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        [CommandMethod("CE_TOOLS", "CE_JUNCTIONGRADETOSURFACE", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void GradeToSurface()
        {
            PlatformDynamicRefreshManager.EnsureInitialized();
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            List<SurfaceOption> surfaces = ReadSurfaces(document);
            if (surfaces.Count == 0)
            {
                document.Editor.WriteMessage("\nCE_PLATFORMGRADETOSURFACE cancelled. No Civil 3D surfaces were found.");
                return;
            }

            List<string> siteNames = ReadSiteNames(document);
            string[] siteChoices = new[] { "<Auto: source site / CE-PLATFORM-SITE>" }
                .Concat(siteNames)
                .ToArray();

            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Junction / Feature-Line Grade to Surface",
                "Grade all or multiple selected sidewalk/shoulder bellmouth edge feature lines to a Civil 3D surface. " +
                "Cut/fill is resolved from the target surface at each source sample, and closed junction boundaries can be infilled separately.");
            settings.AddChoice(
                "SourceScope", "00 Selection", "Grade source scope", "Multiple selected shoulder/sidewalk bellmouth edge lines",
                "Choose all matching shoulder/sidewalk bellmouth edge feature lines, multiple selected matching edge lines, or any multiple selected feature lines.",
                new[]
                {
                    "Multiple selected shoulder/sidewalk bellmouth edge lines",
                    "All shoulder/sidewalk bellmouth edge lines",
                    "Multiple selected feature lines"
                });
            settings.AddText(
                "EdgeLayers", "00 Selection", "Shoulder / sidewalk edge layer(s)", "sidewalks,shoulders",
                "Comma-separated layer names used by the All or matching-edge selection scopes. Name/layer keywords SIDEWALK, SHOULDER, SHLD and VERGE are also recognised.");
            settings.AddChoice(
                "Surface", "01 Target", "Target surface", surfaces[0].Name,
                "Natural ground / controlling surface that the bellmouth grading must daylight to.",
                surfaces.Select(item => item.Name));
            settings.AddChoice(
                "Side", "02 Grading side", "Projection / grading side", "Auto",
                "Auto uses the outward side for closed feature lines. For open bellmouth/road strings choose Outside/Inside or explicit Left/Right. The chosen side is stored with the link and reused when road elevations move.",
                new[] { "Auto", "Outside", "Inside", "Left", "Right" });
            settings.AddChoice(
                "Criteria", "03 Grading criteria", "Grading criteria", "Grade to Surface",
                "Junction bellmouth grading uses Grade to Surface.",
                new[] { "Grade to Surface" });
            settings.AddChoice(
                "CutFormat", "04 Cut", "Cut format", "Slope (H:V)",
                "Choose whether the cut value is entered as a horizontal-to-vertical slope ratio or as grade percent.",
                new[] { "Slope (H:V)", "Grade (%)" });
            settings.AddPositiveDouble(
                "CutSlope", "04 Cut", "Cut slope H:V (1:x)", 2.0,
                "Used when Cut format is Slope. 2.0 means 1V:2H (2H:1V), matching the native 1:2.000 prompt.");
            settings.AddPositiveDouble(
                "CutGrade", "04 Cut", "Cut grade (%)", 50.0,
                "Used when Cut format is Grade. 50% is equivalent to 1V:2H.");
            settings.AddChoice(
                "FillFormat", "05 Fill", "Fill format", "Slope (H:V)",
                "Choose whether the fill value is entered as a horizontal-to-vertical slope ratio or as grade percent.",
                new[] { "Slope (H:V)", "Grade (%)" });
            settings.AddPositiveDouble(
                "FillSlope", "05 Fill", "Fill slope H:V (1:x)", 2.0,
                "Used when Fill format is Slope. 2.0 means 1V:2H (2H:1V), matching the native 1:2.000 prompt.");
            settings.AddPositiveDouble(
                "FillGrade", "05 Fill", "Fill grade (%)", 50.0,
                "Used when Fill format is Grade. 50% is equivalent to 1V:2H.");
            settings.AddPositiveDouble(
                "MaxDistance", "06 Search", "Maximum daylight search", 50.0,
                "Maximum horizontal distance searched from every source point.");
            settings.AddPositiveDouble(
                "SearchStep", "06 Search", "Surface search step", 0.5,
                "Horizontal search increment before the final surface intersection is bisected.");
            settings.AddChoice(
                "Infill", "07 Grading group", "Create native grading group / infill where possible", "Yes",
                "Create native infill for closed junction feature lines. Open sidewalk/shoulder bellmouth edge strings still receive linked Grade-to-Surface daylight geometry.",
                new[] { "Yes", "No" });
            settings.AddChoice(
                "InfillScope", "07 Grading group", "Closed junction infill scope", "Multiple selected closed junction feature lines",
                "Choose closed lines from the grading selection, select multiple closed junction feature lines separately after grading, or infill all recognised closed junction feature lines.",
                new[]
                {
                    "Multiple selected closed junction feature lines",
                    "All closed junction feature lines",
                    "Closed lines in grading selection"
                });
            settings.AddChoice(
                "Site", "07 Grading group", "Grading / toe Site", "<Auto: source site / CE-PLATFORM-SITE>",
                "Reuse the source feature line Site. If it is siteless and native infill is requested, CE-PLATFORM-SITE is created. Choose an existing Site or type a new Site name to move the source/toe grading there.",
                siteChoices);
            settings.AddChoice(
                "ShowSlopeLines", "08 Presentation", "Show cut / fill slope lines", "Yes",
                "Draw Civil 3D feature-line slope rays normal to the source. Long rays terminate exactly at toe/daylight vertices.",
                new[] { "Yes", "No" });
            settings.AddChoice(
                "SlopePatternMode", "08 Presentation", "Slope-line pattern", "Corridor-style long / short",
                "Corridor-style alternates long rays to toe with half-length rays. Full-to-toe makes every projection line terminate on the toe.",
                new[] { "Corridor-style long / short", "All rays full to toe" });
            settings.AddText(
                "CutSlopeLayer", "08 Presentation", "Cut slope-line layer", "CE-JUNCTION-CUT-SLOPES",
                "Layer used for cut projection lines.");
            settings.AddText(
                "FillSlopeLayer", "08 Presentation", "Fill slope-line layer", "CE-JUNCTION-FILL-SLOPES",
                "Layer used for fill projection lines.");
            settings.AddText(
                "ToeLayer", "08 Presentation", "Toe / daylight layer", "CE-JUNCTION-TOE",
                "Layer used for the generated toe/daylight feature line. The toe connects every long slope-line endpoint.");
            settings.AddChoice(
                "CutColor", "08 Presentation", "Cut slope-line colour (ACI)", "ByLayer",
                "ByLayer or an AutoCAD colour index from 1 to 255. The field is editable.",
                new[] { "ByLayer", "1", "2", "3", "4", "5", "6", "7", "8", "9", "250" });
            settings.AddChoice(
                "FillColor", "08 Presentation", "Fill slope-line colour (ACI)", "ByLayer",
                "ByLayer or an AutoCAD colour index from 1 to 255. The field is editable.",
                new[] { "ByLayer", "1", "2", "3", "4", "5", "6", "7", "8", "9", "250" });
            settings.AddChoice(
                "ToeColor", "08 Presentation", "Toe / daylight colour (ACI)", "ByLayer",
                "ByLayer or an AutoCAD colour index from 1 to 255. The field is editable.",
                new[] { "ByLayer", "1", "2", "3", "4", "5", "6", "7", "8", "9", "250" });
            settings.AddPositiveDouble(
                "SlopeLineInterval", "08 Presentation", "Slope-line interval / frequency (m)", 5.0,
                "True chainage spacing along the bellmouth/platform geometry.");
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;

            SurfaceOption selectedSurface = surfaces.FirstOrDefault(item => string.Equals(item.Name, settings.Text("Surface"), StringComparison.OrdinalIgnoreCase));
            if (selectedSurface == null) return;

            double cutRatio = string.Equals(settings.Text("CutFormat"), "Grade (%)", StringComparison.OrdinalIgnoreCase)
                ? 100.0 / Math.Max(0.001, Math.Abs(settings.Double("CutGrade", 50.0)))
                : Math.Max(0.001, settings.Double("CutSlope", 2.0));
            double fillRatio = string.Equals(settings.Text("FillFormat"), "Grade (%)", StringComparison.OrdinalIgnoreCase)
                ? 100.0 / Math.Max(0.001, Math.Abs(settings.Double("FillGrade", 50.0)))
                : Math.Max(0.001, settings.Double("FillSlope", 2.0));

            List<ObjectId> gradeSourceIds = ResolveGradeSourceIds(
                document,
                settings.Text("SourceScope"),
                settings.Text("EdgeLayers"));
            if (gradeSourceIds.Count == 0)
            {
                document.Editor.WriteMessage(
                    "\nCE_JUNCTIONGRADETOSURFACE cancelled. No matching editable feature lines were found.");
                return;
            }

            var requested = new GradeLink
            {
                SurfaceHandle = selectedSurface.ObjectId.Handle.ToString(),
                CutRatio = cutRatio,
                FillRatio = fillRatio,
                MaxDistance = Math.Max(0.10, settings.Double("MaxDistance", 50.0)),
                SearchStep = Math.Max(0.05, settings.Double("SearchStep", 0.5)),
                Side = SafeSide(settings.Text("Side")),
                NativeInfill = string.Equals(settings.Text("Infill"), "Yes", StringComparison.OrdinalIgnoreCase),
                SiteName = SafeName(settings.Text("Site"), "<Auto: source site / CE-PLATFORM-SITE>"),
                ShowSlopeLines = string.Equals(settings.Text("ShowSlopeLines"), "Yes", StringComparison.OrdinalIgnoreCase),
                SlopePatternMode = SafePatternMode(settings.Text("SlopePatternMode")),
                CutSlopeLayer = SafeName(settings.Text("CutSlopeLayer"), "CE-JUNCTION-CUT-SLOPES"),
                FillSlopeLayer = SafeName(settings.Text("FillSlopeLayer"), "CE-JUNCTION-FILL-SLOPES"),
                ToeLayer = SafeName(settings.Text("ToeLayer"), "CE-JUNCTION-TOE"),
                CutColorIndex = ParseAciColor(settings.Text("CutColor")),
                FillColorIndex = ParseAciColor(settings.Text("FillColor")),
                ToeColorIndex = ParseAciColor(settings.Text("ToeColor")),
                SlopeLineInterval = Math.Max(0.10, settings.Double("SlopeLineInterval", 5.0))
            };

            int completed = 0;
            int skipped = 0;
            int groups = 0;
            int infills = 0;
            int slopeLines = 0;
            int cutSlopeLines = 0;
            int fillSlopeLines = 0;
            foreach (ObjectId sourceId in gradeSourceIds.Distinct())
            {
                GradeBuildResult result = BuildOrRefreshGrade(document, sourceId, requested, true);
                if (result.Success)
                {
                    completed++;
                    slopeLines += result.SlopeLinesCreated;
                    cutSlopeLines += result.CutSlopeLinesCreated;
                    fillSlopeLines += result.FillSlopeLinesCreated;
                    if (result.NativeGroupReady) groups++;
                    if (result.NativeInfillCreated) infills++;
                }
                else
                {
                    skipped++;
                    document.Editor.WriteMessage("\nGrade-to-surface skipped safely. " + result.Message);
                }
            }

            int separateInfillsCreated = 0;
            int separateInfillsExisting = 0;
            int separateInfillsSkipped = 0;
            if (requested.NativeInfill &&
                !string.Equals(
                    settings.Text("InfillScope"),
                    "Closed lines in grading selection",
                    StringComparison.OrdinalIgnoreCase))
            {
                List<ObjectId> infillIds = ResolveClosedJunctionInfillIds(
                    document,
                    settings.Text("InfillScope"));
                foreach (ObjectId infillSourceId in infillIds.Distinct())
                {
                    NativeInfillResult infillResult =
                        CreateOrRefreshNativeInfill(
                            document,
                            infillSourceId,
                            requested.SiteName);
                    if (infillResult.Created) separateInfillsCreated++;
                    else if (infillResult.Existing) separateInfillsExisting++;
                    else
                    {
                        separateInfillsSkipped++;
                        if (!string.IsNullOrWhiteSpace(infillResult.Message))
                            document.Editor.WriteMessage(
                                "\nJunction infill skipped safely. " +
                                infillResult.Message);
                    }
                }
            }

            document.Editor.Regen();
            PlatformDynamicRefreshManager.Queue();
            document.Editor.WriteMessage(
                "\nCE_PLATFORMGRADETOSURFACE complete. Graded edge/source feature lines={0}; slope lines={1} (cut={2}, fill={3}); grading groups ready={4}; inline native infills ready={5}; separate closed-junction infills created={6}; existing={7}; infill skipped={8}; grade skipped={9}.",
                completed,
                slopeLines,
                cutSlopeLines,
                fillSlopeLines,
                groups,
                infills,
                separateInfillsCreated,
                separateInfillsExisting,
                separateInfillsSkipped,
                skipped);
        }

        [CommandMethod("CE_TOOLS", "CE_JUNCTIONINFILL", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        [CommandMethod("CE_TOOLS", "CE_PLATFORMINFILL", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void JunctionClosedFeatureLineInfill()
        {
            PlatformDynamicRefreshManager.EnsureInitialized();
            Document document =
                AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            string activeCommand =
                Convert.ToString(
                    AcApplication.GetSystemVariable("CMDNAMES"),
                    CultureInfo.InvariantCulture) ??
                string.Empty;
            bool platformMode =
                activeCommand.IndexOf(
                    "CE_PLATFORMINFILL",
                    StringComparison.OrdinalIgnoreCase) >= 0;

            List<string> siteNames = ReadSiteNames(document);
            string[] siteChoices =
                new[] { "<Auto: source site / CE-PLATFORM-SITE>" }
                    .Concat(siteNames)
                    .ToArray();

            var settings = new ProductionSettingsDialogModel(
                platformMode
                    ? "CE Tools - Platform Closed Feature-Line Infill"
                    : "CE Tools - Junction Closed Feature-Line Infill",
                "Create infill for all recognised closed " +
                (platformMode ? "platform" : "junction") +
                " feature lines or multiple selected closed feature lines. CE Tools keeps the feature line and grading group in the same Site; when Civil 3D 2023 native CreateGradingInfill is not exposed through .NET, a bounded CE TIN infill surface is created.");
            settings.AddChoice(
                "Scope", "01 Selection",
                platformMode
                    ? "Closed platform feature lines"
                    : "Closed junction feature lines",
                platformMode
                    ? "Multiple selected closed platform feature lines"
                    : "Multiple selected closed junction feature lines",
                "Select multiple closed feature lines, or process all recognised " +
                (platformMode ? "platform" : "junction") +
                " closed feature lines in model space.",
                platformMode
                    ? new[]
                    {
                        "Multiple selected closed platform feature lines",
                        "All closed platform feature lines"
                    }
                    : new[]
                    {
                        "Multiple selected closed junction feature lines",
                        "All closed junction feature lines"
                    });
            settings.AddChoice(
                "Site", "02 Grading group", "Grading / infill Site", "<Auto: source site / CE-PLATFORM-SITE>",
                "Reuse each source feature line Site. If siteless, CE-PLATFORM-SITE is created. You may also choose an existing Site.",
                siteChoices);
            settings.AddChoice(
                "AfterInfill", "03 Daylight grading", "After infill", "Continue to grade/daylight",
                "After creating the infill, open the matching Grade-to-Surface workflow so the same platform/junction can receive cut/fill daylight, toe and slope lines.",
                new[] { "Continue to grade/daylight", "Infill only" });
            if (!DisciplineWorkflowDialogs.EditSettings(settings))
                return;

            List<ObjectId> sourceIds =
                ResolveClosedJunctionInfillIds(
                    document,
                    settings.Text("Scope"));
            if (sourceIds.Count == 0)
            {
                document.Editor.WriteMessage(
                    "\nCE_JUNCTIONINFILL cancelled. No editable closed junction feature lines were found.");
                return;
            }

            int created = 0;
            int existing = 0;
            int skipped = 0;
            foreach (ObjectId sourceId in sourceIds.Distinct())
            {
                NativeInfillResult result =
                    CreateOrRefreshNativeInfill(
                        document,
                        sourceId,
                        settings.Text("Site"));
                if (result.Created) created++;
                else if (result.Existing) existing++;
                else
                {
                    skipped++;
                    if (!string.IsNullOrWhiteSpace(result.Message))
                        document.Editor.WriteMessage(
                            "\nJunction infill skipped safely. " +
                            result.Message);
                }
            }

            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\n{0} complete. Closed feature lines={1}; infills created={2}; already ready={3}; skipped={4}.",
                platformMode ? "CE_PLATFORMINFILL" : "CE_JUNCTIONINFILL",
                sourceIds.Count,
                created,
                existing,
                skipped);

            if (string.Equals(
                    settings.Text("AfterInfill"),
                    "Continue to grade/daylight",
                    StringComparison.OrdinalIgnoreCase))
            {
                document.SendStringToExecute(
                    platformMode
                        ? "CE_PLATFORMGRADETOSURFACE "
                        : "CE_JUNCTIONGRADETOSURFACE ",
                    true,
                    false,
                    true);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_FLCLOSEGAP", CommandFlags.Modal | CommandFlags.Redraw)]
        public void CloseFeatureLineGap()
        {
            PlatformDynamicRefreshManager.EnsureInitialized();
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            PromptEntityOptions fixedOptions = new PromptEntityOptions("\nSelect the feature line endpoint that must stay fixed: ");
            fixedOptions.SetRejectMessage("\nSelect an editable Civil 3D feature line.");
            fixedOptions.AddAllowedClass(typeof(CivilFeatureLine), false);
            PromptEntityResult fixedResult = document.Editor.GetEntity(fixedOptions);
            if (fixedResult.Status != PromptStatus.OK) return;

            PromptEntityOptions movingOptions = new PromptEntityOptions("\nSelect the feature line endpoint that must move onto the fixed endpoint: ");
            movingOptions.SetRejectMessage("\nSelect an editable Civil 3D feature line.");
            movingOptions.AddAllowedClass(typeof(CivilFeatureLine), false);
            PromptEntityResult movingResult = document.Editor.GetEntity(movingOptions);
            if (movingResult.Status != PromptStatus.OK) return;
            if (fixedResult.ObjectId == movingResult.ObjectId)
            {
                document.Editor.WriteMessage("\nCE_FLCLOSEGAP cancelled. Select two different feature lines.");
                return;
            }

            string error;
            if (!MoveSelectedEndpointExactly(
                    document,
                    fixedResult.ObjectId,
                    fixedResult.PickedPoint,
                    movingResult.ObjectId,
                    movingResult.PickedPoint,
                    out error))
            {
                document.Editor.WriteMessage("\nCE_FLCLOSEGAP cancelled safely. " + error);
                return;
            }

            document.Editor.Regen();
            PlatformDynamicRefreshManager.Queue();
            document.Editor.WriteMessage("\nCE_FLCLOSEGAP complete. The fixed endpoint was unchanged; only the selected moving endpoint was snapped exactly to it.");
        }

        internal static int RefreshAll(Document document)
        {
            if (document == null || document.Database == null) return 0;

            var directDrapes = new List<KeyValuePair<ObjectId, DirectDrapeLink>>();
            var grades = new List<KeyValuePair<ObjectId, GradeLink>>();
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = transaction.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(document.Database),
                    OpenMode.ForRead,
                    false) as BlockTableRecord;
                if (space == null) return 0;
                foreach (ObjectId id in space)
                {
                    CivilFeatureLine featureLine = OpenFeatureLine(transaction, id, OpenMode.ForRead);
                    if (featureLine == null) continue;
                    DirectDrapeLink drape;
                    if (TryReadDirectDrapeLink(featureLine, transaction, out drape))
                        directDrapes.Add(new KeyValuePair<ObjectId, DirectDrapeLink>(id, drape));
                    GradeLink grade;
                    if (TryReadGradeLink(featureLine, transaction, out grade))
                        grades.Add(new KeyValuePair<ObjectId, GradeLink>(id, grade));
                }
            }

            int refreshed = 0;
            foreach (KeyValuePair<ObjectId, DirectDrapeLink> item in directDrapes)
            {
                ObjectId surfaceId = ResolveHandle(document.Database, item.Value.SurfaceHandle);
                string surfaceName = August21SurfaceSafety.ReadSurfaceName(document, surfaceId);
                if (surfaceId.IsNull || string.IsNullOrWhiteSpace(surfaceName)) continue;
                string error;
                if (August21SurfaceSafety.TryApplyFeatureLineElevations(document, item.Key, surfaceName, item.Value.Intermediate, out error))
                    refreshed++;
                else
                    document.Editor.WriteMessage("\nA linked multi-drape was kept unchanged after a safe refresh failure. " + error);
            }

            foreach (KeyValuePair<ObjectId, GradeLink> item in grades)
            {
                GradeBuildResult result = BuildOrRefreshGrade(document, item.Key, item.Value, false);
                if (result.Success) refreshed++;
                else document.Editor.WriteMessage("\nA linked grade-to-surface daylight was kept unchanged. " + result.Message);
            }
            return refreshed;
        }

        internal static int RefreshLinkedGrades(
            Document document,
            ISet<string> changedHandles)
        {
            if (document == null ||
                document.Database == null ||
                changedHandles == null ||
                changedHandles.Count == 0)
                return 0;

            var grades =
                new List<KeyValuePair<ObjectId, GradeLink>>();
            using (Transaction transaction =
                document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = transaction.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(
                        document.Database),
                    OpenMode.ForRead,
                    false) as BlockTableRecord;
                if (space == null)
                    return 0;

                foreach (ObjectId id in space)
                {
                    CivilFeatureLine featureLine =
                        OpenFeatureLine(
                            transaction,
                            id,
                            OpenMode.ForRead);
                    if (featureLine == null)
                        continue;

                    string handle;
                    try
                    {
                        handle =
                            featureLine.Handle.ToString();
                    }
                    catch
                    {
                        continue;
                    }

                    if (!changedHandles.Contains(handle))
                        continue;

                    GradeLink grade;
                    if (TryReadGradeLink(
                            featureLine,
                            transaction,
                            out grade) &&
                        grade != null)
                    {
                        grades.Add(
                            new KeyValuePair<ObjectId, GradeLink>(
                                id,
                                grade));
                    }
                }
            }

            int refreshed = 0;
            foreach (KeyValuePair<ObjectId, GradeLink> item in grades)
            {
                GradeBuildResult result =
                    BuildOrRefreshGrade(
                        document,
                        item.Key,
                        item.Value,
                        false);
                if (result.Success)
                    refreshed++;
                else
                    document.Editor.WriteMessage(
                        "\nLinked road/junction daylight was kept unchanged after an automatic source-elevation refresh. " +
                        result.Message);
            }
            return refreshed;
        }

        internal static int SynchronizeLinkedAppearance(
            Document document,
            IEnumerable<ObjectId> sourceIds)
        {
            if (document == null || document.Database == null || sourceIds == null)
                return 0;
            var pairs = new List<KeyValuePair<ObjectId, ObjectId>>();
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId sourceId in sourceIds.Distinct())
                {
                    CivilFeatureLine source = OpenFeatureLine(transaction, sourceId, OpenMode.ForRead);
                    if (source == null) continue;
                    GradeLink link;
                    if (!TryReadGradeLink(source, transaction, out link) || link == null) continue;
                    ObjectId childId = ResolveHandle(document.Database, link.ChildHandle);
                    if (!childId.IsNull)
                        pairs.Add(new KeyValuePair<ObjectId, ObjectId>(sourceId, childId));
                }
            }

            int changed = 0;
            foreach (KeyValuePair<ObjectId, ObjectId> pair in pairs)
            {
                try
                {
                    using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                    {
                        CivilFeatureLine source = OpenFeatureLine(transaction, pair.Key, OpenMode.ForRead);
                        CivilFeatureLine child = OpenFeatureLine(transaction, pair.Value, OpenMode.ForWrite);
                        if (source == null || child == null) continue;
                        child.Color = source.Color;
                        try { child.RecordGraphicsModified(true); } catch { }
                        transaction.Commit();
                        changed++;
                    }
                }
                catch { }
            }
            return changed;
        }

        private static GradeBuildResult BuildOrRefreshGrade(Document document, ObjectId sourceId, GradeLink requested, bool explicitCommand)
        {
            var result = new GradeBuildResult();
            if (document == null || sourceId.IsNull)
            {
                result.Message = "The source feature line is unavailable.";
                return result;
            }

            GradeLink existing = ReadGradeLink(document.Database, sourceId);
            GradeLink link = requested.Clone();
            if (string.IsNullOrWhiteSpace(link.ChildHandle) && existing != null) link.ChildHandle = existing.ChildHandle;
            if (string.IsNullOrWhiteSpace(link.GroupHandle) && existing != null) link.GroupHandle = existing.GroupHandle;
            if (string.IsNullOrWhiteSpace(link.InfillHandle) && existing != null) link.InfillHandle = existing.InfillHandle;
            if (string.IsNullOrWhiteSpace(link.SlopeLineHandles) && existing != null)
                link.SlopeLineHandles = existing.SlopeLineHandles;
            if (existing != null && string.IsNullOrWhiteSpace(link.CutSlopeLayer))
                link.CutSlopeLayer = existing.CutSlopeLayer;
            if (existing != null && string.IsNullOrWhiteSpace(link.FillSlopeLayer))
                link.FillSlopeLayer = existing.FillSlopeLayer;
            if (existing != null && string.IsNullOrWhiteSpace(link.ToeLayer))
                link.ToeLayer = existing.ToeLayer;
            if (existing != null && string.IsNullOrWhiteSpace(link.SiteName))
                link.SiteName = existing.SiteName;
            if (existing != null && string.IsNullOrWhiteSpace(link.SlopePatternMode))
                link.SlopePatternMode = existing.SlopePatternMode;
            link.CutColorIndex = NormalizeAciColor(link.CutColorIndex);
            link.FillColorIndex = NormalizeAciColor(link.FillColorIndex);
            link.ToeColorIndex = NormalizeAciColor(link.ToeColorIndex);
            link.SlopePatternMode = SafePatternMode(link.SlopePatternMode);

            ObjectId surfaceId = ResolveHandle(document.Database, link.SurfaceHandle);
            if (surfaceId.IsNull)
            {
                result.Message = "The linked target surface no longer exists.";
                return result;
            }

            SourceSnapshot source;
            string error;
            if (!TryReadSource(
                    document.Database,
                    sourceId,
                    out source,
                    out error))
            {
                result.Message = error;
                return result;
            }

            bool explicitSite =
                !IsAutomaticSiteChoice(link.SiteName);
            bool needsNativeSite =
                link.NativeInfill &&
                source.Closed;
            if (needsNativeSite ||
                explicitSite)
            {
                try
                {
                    EnsureSourceHasSite(
                        document,
                        sourceId,
                        link.SiteName,
                        needsNativeSite);

                    // Moving a feature line to a Site changes its SiteId and can
                    // trigger native topology bookkeeping. Re-read the source so
                    // the toe and grading group are created in that exact Site.
                    if (!TryReadSource(
                            document.Database,
                            sourceId,
                            out source,
                            out error))
                    {
                        result.Message = error;
                        return result;
                    }
                }
                catch (System.Exception exception)
                {
                    if (explicitCommand)
                        document.Editor.WriteMessage(
                            "\nGrading Site preparation failed; daylight geometry will still be attempted. " +
                            exception.Message);
                }
            }

            List<Point3d> daylight;
            List<SlopeRaySample> resolvedSamples;
            if (!TryBuildDaylight(
                    document.Database,
                    surfaceId,
                    source,
                    link,
                    out daylight,
                    out resolvedSamples,
                    out error))
            {
                result.Message = error;
                return result;
            }
            if (daylight.Count < 2 || daylight.Any(point => !Finite(point)))
            {
                result.Message = "The daylight calculation did not produce a valid finite feature line.";
                return result;
            }

            ObjectId candidateId;
            if (!TryCreateFeatureLineCandidate(
                    document,
                    source,
                    daylight,
                    SafeName(link.ToeLayer, "CE-JUNCTION-TOE"),
                    link.ToeColorIndex,
                    out candidateId,
                    out error))
            {
                result.Message = error;
                return result;
            }

            ObjectId oldChildId = ResolveHandle(document.Database, link.ChildHandle);
            string desiredName = ReadFeatureLineName(document.Database, oldChildId);
            if (string.IsNullOrWhiteSpace(desiredName))
                desiredName = UniqueFeatureLineName(document.Database, SafeName(source.Name, "PLATFORM") + "-DAYLIGHT", oldChildId);

            if (!TrySwapCandidate(document.Database, oldChildId, candidateId, desiredName, out error))
            {
                Cleanup(document.Database, candidateId);
                result.Message = error;
                return result;
            }

            link.ChildHandle = candidateId.Handle.ToString();

            // Rebuild visible slope projection lines only after the new daylight
            // geometry has been successfully created and swapped into place.
            string previousSlopeHandles = link.SlopeLineHandles;
            link.SlopeLineHandles = string.Empty;
            if (link.ShowSlopeLines)
            {
                List<ObjectId> newSlopeLines;
                if (TryCreateSlopeLines(
                        document.Database,
                        source,
                        daylight,
                        resolvedSamples,
                        link,
                        out newSlopeLines,
                        out error))
                {
                    link.SlopeLineHandles = string.Join(
                        ";",
                        newSlopeLines.Select(
                            id => id.Handle.ToString()));
                    CleanupHandleList(
                        document.Database,
                        previousSlopeHandles);
                    result.SlopeLinesCreated =
                        newSlopeLines.Count;
                    result.CutSlopeLinesCreated =
                        resolvedSamples.Count(
                            item =>
                                item.Valid &&
                                item.Cut);
                    result.FillSlopeLinesCreated =
                        resolvedSamples.Count(
                            item =>
                                item.Valid &&
                                !item.Cut);
                }
                else if (explicitCommand)
                {
                    document.Editor.WriteMessage(
                        "\nDaylight was created, but cut/fill slope projection lines were skipped. " +
                        error);
                    link.SlopeLineHandles =
                        previousSlopeHandles ?? string.Empty;
                }
            }
            else
            {
                CleanupHandleList(
                    document.Database,
                    previousSlopeHandles);
            }

            ObjectId groupId =
                ResolveHandle(
                    document.Database,
                    link.GroupHandle);
            ObjectId previousInfillId =
                ResolveHandle(
                    document.Database,
                    link.InfillHandle);

            if (link.NativeInfill &&
                source.Closed &&
                !source.SiteId.IsNull)
            {
                string groupError = string.Empty;
                if (groupId.IsNull)
                {
                    groupId = TryCreateGradingGroup(
                        document.Database,
                        source.SiteId,
                        "CE-PLATFORM-GRADE-" +
                            sourceId.Handle.ToString(),
                        out groupError);
                    if (!groupId.IsNull)
                        link.GroupHandle =
                            groupId.Handle.ToString();
                }

                if (!groupId.IsNull)
                {
                    result.NativeGroupReady = true;

                    // A Civil 3D infill is linked to its Site/feature-line
                    // topology, so a valid existing infill should be retained
                    // through a linked daylight refresh rather than erased and
                    // recreated on every road/platform elevation edit.
                    if (!previousInfillId.IsNull)
                    {
                        result.NativeInfillCreated = true;
                    }
                    else
                    {
                        ObjectId infillId;
                        string infillError;
                        Point3d seed =
                            InteriorSeed(source.Points);
                        if (TryCreateInfill(
                                groupId,
                                seed,
                                out infillId,
                                out infillError))
                        {
                            link.InfillHandle =
                                infillId.IsNull
                                    ? string.Empty
                                    : infillId.Handle.ToString();
                            result.NativeInfillCreated = true;
                        }
                        else
                        {
                            ObjectId fallbackSurfaceId;
                            string fallbackError;
                            if (TryCreateFallbackInfillSurface(
                                    document.Database,
                                    sourceId,
                                    source,
                                    "CE-PLATFORM-JUNCTION-INFILL",
                                    out fallbackSurfaceId,
                                    out fallbackError))
                            {
                                link.InfillHandle =
                                    fallbackSurfaceId.Handle.ToString();
                                result.NativeInfillCreated = true;
                                if (explicitCommand)
                                {
                                    document.Editor.WriteMessage(
                                        "\nCivil 3D 2023 exposes CreateGradingInfill as a native command rather than a public Grading.CreateInfill API. CE Tools created a bounded dynamic TIN infill surface instead: {0}.",
                                        ReadSurfaceName(
                                            document.Database,
                                            fallbackSurfaceId));
                                }
                            }
                            else if (explicitCommand)
                            {
                                document.Editor.WriteMessage(
                                    "\nNative grading infill was unavailable ({0}) and the CE bounded TIN infill fallback also failed: {1}",
                                    infillError,
                                    fallbackError);
                            }
                        }
                    }
                }
                else if (explicitCommand)
                {
                    document.Editor.WriteMessage(
                        "\nNative grading group could not be created. " +
                        groupError);
                }
            }
            else if (link.NativeInfill &&
                     explicitCommand &&
                     !source.Closed)
            {
                document.Editor.WriteMessage(
                    "\nNative infill requires a closed feature line. The open bellmouth still received toe/daylight and cut/fill slope lines.");
            }

            try
            {
                WriteGradeLink(document.Database, sourceId, link);
            }
            catch (System.Exception exception)
            {
                result.Message = "The daylight was created, but the persistent grade link could not be written: " + exception.Message;
                return result;
            }

            result.Success = true;
            result.Message = string.Empty;
            return result;
        }

        private static bool TryBuildDaylight(
            Database database,
            ObjectId surfaceId,
            SourceSnapshot source,
            GradeLink link,
            out List<Point3d> daylight,
            out List<SlopeRaySample> samples,
            out string error)
        {
            daylight = new List<Point3d>();
            samples = new List<SlopeRaySample>();
            error = string.Empty;
            if (source == null ||
                source.Points == null ||
                source.Points.Count < 2)
            {
                error = "The source feature line has too few points.";
                return false;
            }

            double spacing = Math.Max(
                0.10,
                link.SlopeLineInterval <= Tolerance
                    ? 5.0
                    : link.SlopeLineInterval);

            if (!TryResolveSlopeRaySamples(
                    database,
                    surfaceId,
                    source,
                    link,
                    spacing,
                    out samples,
                    out error))
                return false;

            // The toe/daylight feature line is intentionally defined ONLY by the
            // full-length grading rays. The short presentation rays stop halfway
            // and do not control the toe. This keeps every toe vertex exactly on
            // a visible long slope-line endpoint.
            daylight = samples
                .Where(item => item.Valid && !item.HalfLength)
                .Select(item => item.EndPoint)
                .ToList();

            if (daylight.Count < 2)
            {
                error = "Too few full-length slope-ray endpoints were available to create the toe/daylight line.";
                daylight.Clear();
                return false;
            }

            return true;
        }

        private static bool TryResolveSlopeRaySamples(
            Database database,
            ObjectId surfaceId,
            SourceSnapshot source,
            GradeLink link,
            double spacing,
            out List<SlopeRaySample> samples,
            out string error)
        {
            samples = BuildSlopeRaySamples(
                database,
                source,
                link.Side,
                spacing);
            error = string.Empty;

            if (samples.Count < 2)
            {
                error = "The bellmouth/road curve could not be sampled for grading.";
                return false;
            }

            try
            {
                using (Transaction transaction =
                    database.TransactionManager.StartTransaction())
                {
                    CivilSurface surface = transaction.GetObject(
                        surfaceId,
                        OpenMode.ForRead,
                        false) as CivilSurface;
                    if (surface == null)
                    {
                        error = "The target surface is not readable.";
                        return false;
                    }

                    foreach (SlopeRaySample sample in samples)
                    {
                        Point3d endPoint;
                        bool isCut;
                        if (!TryFindDaylight(
                                surface,
                                sample.Point,
                                sample.Direction,
                                link,
                                out endPoint,
                                out isCut))
                        {
                            error =
                                "No cut/fill daylight intersection was found within " +
                                link.MaxDistance.ToString("N2", CultureInfo.CurrentCulture) +
                                " at a bellmouth/road curve sample. The previous linked grading, if any, was kept.";
                            return false;
                        }

                        if (!Finite(endPoint) ||
                            sample.Point.DistanceTo(endPoint) <= Tolerance)
                            continue;

                        sample.EndPoint = endPoint;
                        sample.Cut = isCut;
                        sample.Valid = true;
                    }
                }

                List<SlopeRaySample> valid =
                    samples.Where(item => item.Valid).ToList();
                if (valid.Count < 2)
                {
                    error = "Too few valid cut/fill daylight rays were resolved.";
                    return false;
                }

                bool corridorPattern =
                    !string.Equals(
                        SafePatternMode(link.SlopePatternMode),
                        "All rays full to toe",
                        StringComparison.OrdinalIgnoreCase);
                for (int index = 0; index < valid.Count; index++)
                    valid[index].HalfLength =
                        corridorPattern &&
                        (index % 2) == 1;

                // Always keep both ends as long rays so the toe starts/ends on
                // exact projection-line endpoints. The exact same resolved
                // samples are reused when visible rays are created.
                valid[0].HalfLength = false;
                valid[valid.Count - 1].HalfLength = false;
                return true;
            }
            catch (System.Exception exception)
            {
                error = "Target-surface grading sampling failed safely: " +
                    exception.Message;
                return false;
            }
        }

        private static bool TryFindDaylight(
            CivilSurface surface,
            Point3d source,
            Vector2d direction,
            GradeLink link,
            out Point3d result,
            out bool isCut)
        {
            result = Point3d.Origin;
            isCut = false;
            double step =
                Math.Max(
                    0.05,
                    Math.Min(
                        link.SearchStep,
                        link.MaxDistance));
            bool modeKnown = false;
            bool cut = false;
            bool previousValid = false;
            double previousDistance = 0.0;
            double previousDifference = 0.0;

            for (double distance = 0.0;
                 distance <= link.MaxDistance + Tolerance;
                 distance += step)
            {
                double terrain;
                if (!TrySurfaceElevation(
                        surface,
                        source,
                        direction,
                        distance,
                        out terrain))
                {
                    previousValid = false;
                    continue;
                }

                if (!modeKnown)
                {
                    double delta =
                        terrain - source.Z;

                    // At the exact source point an existing-ground triangle may
                    // coincide with the design feature line. Do not classify
                    // that zero-distance touch as fill or terminate the ray.
                    // Continue outward until the terrain is meaningfully above
                    // or below the design level, which matches Civil 3D's
                    // cut/fill grading intent.
                    if (Math.Abs(delta) <= 0.005)
                    {
                        if (distance <= Tolerance)
                            continue;
                    }
                    else
                    {
                        cut = delta > 0.0;
                        modeKnown = true;
                        isCut = cut;
                    }

                    if (!modeKnown)
                        continue;
                }

                double ratio =
                    cut
                        ? link.CutRatio
                        : link.FillRatio;
                double gradeElevation =
                    source.Z +
                    (cut
                        ? distance / ratio
                        : -distance / ratio);
                double difference =
                    gradeElevation - terrain;

                if (Math.Abs(difference) <= 0.005 &&
                    distance > Tolerance)
                {
                    result = new Point3d(
                        source.X +
                            direction.X * distance,
                        source.Y +
                            direction.Y * distance,
                        terrain);
                    isCut = cut;
                    return true;
                }

                if (previousValid &&
                    Math.Sign(previousDifference) !=
                        Math.Sign(difference))
                {
                    bool found =
                        TryBisectDaylight(
                            surface,
                            source,
                            direction,
                            cut,
                            ratio,
                            previousDistance,
                            distance,
                            previousDifference,
                            difference,
                            out result);
                    isCut = cut;
                    return found;
                }

                previousValid = true;
                previousDistance = distance;
                previousDifference = difference;
            }

            return false;
        }

        private static bool TryBisectDaylight(CivilSurface surface, Point3d source, Vector2d direction, bool cut, double ratio, double low, double high, double lowDifference, double highDifference, out Point3d result)
        {
            result = Point3d.Origin;
            for (int iteration = 0; iteration < 32; iteration++)
            {
                double middle = (low + high) * 0.5;
                double terrain;
                if (!TrySurfaceElevation(surface, source, direction, middle, out terrain))
                    return false;
                double gradeElevation = source.Z + (cut ? middle / ratio : -middle / ratio);
                double difference = gradeElevation - terrain;
                if (Math.Abs(difference) <= 0.001 || Math.Abs(high - low) <= 0.001)
                {
                    result = new Point3d(source.X + direction.X * middle, source.Y + direction.Y * middle, terrain);
                    return true;
                }
                if (Math.Sign(lowDifference) == Math.Sign(difference))
                {
                    low = middle;
                    lowDifference = difference;
                }
                else
                {
                    high = middle;
                    highDifference = difference;
                }
            }
            double finalDistance = (low + high) * 0.5;
            double finalTerrain;
            if (!TrySurfaceElevation(surface, source, direction, finalDistance, out finalTerrain)) return false;
            result = new Point3d(source.X + direction.X * finalDistance, source.Y + direction.Y * finalDistance, finalTerrain);
            return true;
        }

        private static bool TrySurfaceElevation(CivilSurface surface, Point3d source, Vector2d direction, double distance, out double elevation)
        {
            elevation = 0.0;
            try
            {
                elevation = surface.FindElevationAtXY(source.X + direction.X * distance, source.Y + direction.Y * distance);
                return Finite(elevation);
            }
            catch { return false; }
        }

        private static bool TryProjectionDirection(IList<Point3d> points, int index, bool closed, double signedArea, string side, out Vector2d direction)
        {
            direction = new Vector2d();
            int count = points.Count;
            if (count < 2) return false;

            Vector2d tangent;
            if (!closed && index == 0)
                tangent = new Vector2d(points[1].X - points[0].X, points[1].Y - points[0].Y);
            else if (!closed && index == count - 1)
                tangent = new Vector2d(points[count - 1].X - points[count - 2].X, points[count - 1].Y - points[count - 2].Y);
            else
            {
                int previous = index == 0 ? count - 1 : index - 1;
                int next = index == count - 1 ? 0 : index + 1;
                Vector2d incoming = new Vector2d(points[index].X - points[previous].X, points[index].Y - points[previous].Y);
                Vector2d outgoing = new Vector2d(points[next].X - points[index].X, points[next].Y - points[index].Y);
                if (incoming.Length > Tolerance) incoming = incoming.GetNormal();
                if (outgoing.Length > Tolerance) outgoing = outgoing.GetNormal();
                tangent = incoming + outgoing;
                if (tangent.Length <= Tolerance) tangent = outgoing.Length > Tolerance ? outgoing : incoming;
            }
            if (tangent.Length <= Tolerance) return false;
            tangent = tangent.GetNormal();
            Vector2d left = new Vector2d(-tangent.Y, tangent.X);

            double sign;
            if (string.Equals(side, "Inside", StringComparison.OrdinalIgnoreCase)) sign = closed ? (signedArea >= 0.0 ? 1.0 : -1.0) : -1.0;
            else if (string.Equals(side, "Outside", StringComparison.OrdinalIgnoreCase)) sign = closed ? (signedArea >= 0.0 ? -1.0 : 1.0) : 1.0;
            else if (string.Equals(side, "Left", StringComparison.OrdinalIgnoreCase)) sign = 1.0;
            else if (string.Equals(side, "Right", StringComparison.OrdinalIgnoreCase)) sign = -1.0;
            else if (closed) sign = signedArea >= 0.0 ? -1.0 : 1.0;
            else sign = 1.0;

            direction = left.MultiplyBy(sign);
            return direction.Length > Tolerance;
        }

        private static bool MoveSelectedEndpointExactly(Document document, ObjectId fixedId, Point3d fixedPick, ObjectId movingId, Point3d movingPick, out string error)
        {
            error = string.Empty;
            try
            {
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    CivilFeatureLine fixedLine = OpenFeatureLine(transaction, fixedId, OpenMode.ForRead);
                    CivilFeatureLine movingLine = OpenFeatureLine(transaction, movingId, OpenMode.ForWrite);
                    if (!Editable(fixedLine, transaction) || !Editable(movingLine, transaction))
                    {
                        error = "Both feature lines must be local, editable and on unlocked layers.";
                        return false;
                    }

                    Point3dCollection fixedPoints = fixedLine.GetPoints(FeatureLinePointType.PIPoint);
                    Point3dCollection movingPoints = movingLine.GetPoints(FeatureLinePointType.PIPoint);
                    if (fixedPoints == null || fixedPoints.Count < 2 || movingPoints == null || movingPoints.Count < 2)
                    {
                        error = "Both feature lines need readable start and end points.";
                        return false;
                    }

                    Point3d fixedEndpoint = NearestEndpoint(fixedPoints, fixedPick);
                    Point3d movingEndpoint = NearestEndpoint(movingPoints, movingPick);
                    Point3dCollection grips = new Point3dCollection();
                    IntegerCollection snapModes = new IntegerCollection(1);
                    IntegerCollection geometryIds = new IntegerCollection(1);
                    snapModes.Add(0);
                    geometryIds.Add(0);
                    movingLine.GetGripPoints(grips, snapModes, geometryIds);
                    if (grips.Count == 0)
                    {
                        error = "Civil 3D returned no editable grips for the moving feature line.";
                        return false;
                    }

                    int gripIndex = ClosestIndex(grips, movingEndpoint);
                    if (gripIndex < 0)
                    {
                        error = "The selected moving endpoint grip could not be resolved.";
                        return false;
                    }

                    var indices = new IntegerCollection(1);
                    indices.Add(gripIndex);
                    Vector3d offset = fixedEndpoint - grips[gripIndex];
                    movingLine.MoveGripPointsAt(indices, offset);

                    Point3dCollection verification = movingLine.GetPoints(FeatureLinePointType.PIPoint);
                    Point3d newEndpoint = NearestEndpoint(verification, fixedEndpoint);
                    if (newEndpoint.DistanceTo(fixedEndpoint) > 0.0001)
                    {
                        error = "Civil 3D did not accept an exact endpoint move; the transaction was rolled back instead of moving both endpoints to a midpoint.";
                        return false;
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

        private static bool TryCreateSlopeLines(
            Database database,
            SourceSnapshot source,
            IList<Point3d> daylight,
            IList<SlopeRaySample> resolvedSamples,
            GradeLink link,
            out List<ObjectId> lineIds,
            out string error)
        {
            lineIds = new List<ObjectId>();
            error = string.Empty;
            if (database == null ||
                source == null ||
                source.Points == null ||
                source.Points.Count < 2)
            {
                error = "Source geometry is unavailable for slope-line creation.";
                return false;
            }

            if (resolvedSamples == null ||
                resolvedSamples.Count < 2)
            {
                error = "Resolved grading rays are unavailable for slope-line creation.";
                return false;
            }

            try
            {
                ObjectId cutLayerId;
                ObjectId fillLayerId;
                using (Transaction transaction =
                    database.TransactionManager.StartTransaction())
                {
                    cutLayerId = EnsureLayer(
                        database,
                        transaction,
                        SafeName(link.CutSlopeLayer, "CE-JUNCTION-CUT-SLOPES"),
                        1);
                    fillLayerId = EnsureLayer(
                        database,
                        transaction,
                        SafeName(link.FillSlopeLayer, "CE-JUNCTION-FILL-SLOPES"),
                        3);
                    transaction.Commit();
                }

                int index = 0;
                var toePoints = daylight.Select(point => new CETools.Core.GradingPoint(point.X, point.Y, point.Z)).ToList();
                foreach (SlopeRaySample sample in
                    resolvedSamples.Where(item => item.Valid))
                {
                    ObjectId rayId;
                    string rayError;
                    Point3d rayStart = sample.Point;
                    Point3d rayEnd;
                    if (sample.HalfLength)
                    {
                        CETools.Core.GradingPoint shortStart, shortEnd;
                        if (!CETools.Core.GradingSlopeTicks.TryShortTick(
                                new CETools.Core.GradingPoint(sample.Point.X, sample.Point.Y, sample.Point.Z),
                                new CETools.Core.GradingPoint(sample.EndPoint.X, sample.EndPoint.Y, sample.EndPoint.Z),
                                toePoints, source.Closed, sample.Cut, out shortStart, out shortEnd))
                        {
                            error = "A short slope ray could not be intersected with the toe/daylight line.";
                            foreach (ObjectId created in lineIds) Cleanup(database, created);
                            lineIds.Clear();
                            return false;
                        }
                        rayStart = new Point3d(shortStart.X, shortStart.Y, shortStart.Z);
                        rayEnd = new Point3d(shortEnd.X, shortEnd.Y, shortEnd.Z);
                    }
                    else if (!TrySnapToToeVertex(
                                 daylight,
                                 sample.EndPoint,
                                 out rayEnd))
                    {
                        error =
                            "A long slope ray could not be matched to its toe/daylight vertex.";
                        foreach (ObjectId created in lineIds)
                            Cleanup(database, created);
                        lineIds.Clear();
                        return false;
                    }

                    short rayColour =
                        sample.Cut
                            ? link.CutColorIndex
                            : link.FillColorIndex;
                    if (!TryCreateCivilSlopeRay(
                            database,
                            source,
                            rayStart,
                            rayEnd,
                            sample.Cut ? cutLayerId : fillLayerId,
                            sample.Cut ? "CUT" : "FILL",
                            rayColour,
                            index++,
                            out rayId,
                            out rayError))
                    {
                        error = rayError;
                        foreach (ObjectId created in lineIds)
                            Cleanup(database, created);
                        lineIds.Clear();
                        return false;
                    }
                    lineIds.Add(rayId);
                }

                if (lineIds.Count == 0)
                {
                    error = "No valid Civil 3D cut/fill slope rays could be created.";
                    return false;
                }
                return true;
            }
            catch (System.Exception exception)
            {
                error = exception.Message;
                foreach (ObjectId id in lineIds)
                    Cleanup(database, id);
                lineIds.Clear();
                return false;
            }
        }

        private static List<SlopeRaySample> BuildSlopeRaySamples(
            Database database,
            SourceSnapshot source,
            string side,
            double spacing)
        {
            var result = new List<SlopeRaySample>();
            if (database == null ||
                source == null ||
                source.Points == null ||
                source.Points.Count < 2)
                return result;

            double area = source.Closed
                ? SignedArea(source.Points)
                : 0.0;

            // Use the real Civil 3D feature-line geometry first. Exploding the
            // feature line gives AutoCAD Curve segments (Line/Arc/Polyline, etc.),
            // so sampling by curve distance follows the bellmouth arc rather than
            // interpolating straight chords between PI/elevation points.
            DBObjectCollection exploded = new DBObjectCollection();
            try
            {
                using (Transaction transaction =
                    database.TransactionManager.StartTransaction())
                {
                    CivilFeatureLine featureLine = OpenFeatureLine(
                        transaction,
                        source.ObjectId,
                        OpenMode.ForRead);
                    if (featureLine != null)
                        featureLine.Explode(exploded);
                }

                foreach (DBObject item in exploded)
                {
                    Curve curve = item as Curve;
                    if (curve == null)
                        continue;

                    double length;
                    try
                    {
                        length = curve.GetDistanceAtParameter(
                                     curve.EndParam) -
                                 curve.GetDistanceAtParameter(
                                     curve.StartParam);
                    }
                    catch
                    {
                        continue;
                    }
                    if (!Finite(length) || length <= Tolerance)
                        continue;

                    double finalDistance = Math.Max(0.0, length);
                    for (double distance = 0.0;
                         distance <= finalDistance + Tolerance;
                         distance += spacing)
                    {
                        double localDistance = Math.Min(
                            finalDistance,
                            distance);
                        Point3d point;
                        Vector3d derivative;
                        try
                        {
                            point = curve.GetPointAtDist(localDistance);
                            derivative = curve.GetFirstDerivative(point);
                        }
                        catch
                        {
                            continue;
                        }

                        Vector2d tangent = new Vector2d(
                            derivative.X,
                            derivative.Y);
                        if (tangent.Length <= Tolerance)
                            continue;
                        tangent = tangent.GetNormal();

                        AddSlopeRaySample(
                            result,
                            point,
                            ResolveSlopeRayDirection(
                                tangent,
                                source.Closed,
                                area,
                                side),
                            spacing);
                    }

                    // Always retain the exact segment end where spacing does not
                    // land on it, while using the local end tangent of the curve.
                    try
                    {
                        Point3d end = curve.EndPoint;
                        Vector3d derivative =
                            curve.GetFirstDerivative(end);
                        Vector2d tangent = new Vector2d(
                            derivative.X,
                            derivative.Y);
                        if (tangent.Length > Tolerance)
                        {
                            AddSlopeRaySample(
                                result,
                                end,
                                ResolveSlopeRayDirection(
                                    tangent.GetNormal(),
                                    source.Closed,
                                    area,
                                    side),
                                spacing);
                        }
                    }
                    catch { }
                }
            }
            catch
            {
                result.Clear();
            }
            finally
            {
                foreach (DBObject item in exploded)
                {
                    try { item.Dispose(); }
                    catch { }
                }
            }

            if (result.Count > 1)
                return result;

            // Safe fallback for unusual FeatureLine implementations that do not
            // expose explodable Curve geometry in the installed Civil 3D build.
            result.Clear();
            for (int segment = 0;
                 segment < source.Points.Count - 1;
                 segment++)
            {
                Point3d a = source.Points[segment];
                Point3d b = source.Points[segment + 1];
                Vector2d tangent = new Vector2d(
                    b.X - a.X,
                    b.Y - a.Y);
                double length = tangent.Length;
                if (length <= Tolerance)
                    continue;
                tangent = tangent.GetNormal();
                Vector2d direction = ResolveSlopeRayDirection(
                    tangent,
                    source.Closed,
                    area,
                    side);

                double last =
                    segment == source.Points.Count - 2
                        ? length
                        : Math.Max(0.0, length - 0.001);
                for (double distance = 0.0;
                     distance <= last + Tolerance;
                     distance += spacing)
                {
                    double t = Math.Min(1.0, distance / length);
                    AddSlopeRaySample(
                        result,
                        new Point3d(
                            a.X + (b.X - a.X) * t,
                            a.Y + (b.Y - a.Y) * t,
                            a.Z + (b.Z - a.Z) * t),
                        direction,
                        spacing);
                }

                AddSlopeRaySample(
                    result,
                    b,
                    direction,
                    spacing);
            }
            return result;
        }

        private static void AddSlopeRaySample(
            IList<SlopeRaySample> samples,
            Point3d point,
            Vector2d direction,
            double spacing)
        {
            if (samples == null ||
                !Finite(point) ||
                direction.Length <= Tolerance)
                return;
            if (samples.Count > 0 &&
                samples[samples.Count - 1].Point.DistanceTo(point) <=
                    Math.Min(0.01, spacing * 0.05))
                return;

            samples.Add(new SlopeRaySample
            {
                Point = point,
                Direction = direction.GetNormal()
            });
        }

        private static Point3d Halfway(
            Point3d start,
            Point3d end)
        {
            return new Point3d(
                start.X + (end.X - start.X) * 0.5,
                start.Y + (end.Y - start.Y) * 0.5,
                start.Z + (end.Z - start.Z) * 0.5);
        }

        private static Vector2d ResolveSlopeRayDirection(
            Vector2d tangent,
            bool closed,
            double signedArea,
            string side)
        {
            Vector2d left = new Vector2d(
                -tangent.Y,
                tangent.X);
            double sign;
            if (string.Equals(
                    side,
                    "Inside",
                    StringComparison.OrdinalIgnoreCase))
                sign = closed
                    ? (signedArea >= 0.0 ? 1.0 : -1.0)
                    : -1.0;
            else if (string.Equals(
                side,
                "Outside",
                StringComparison.OrdinalIgnoreCase))
                sign = closed
                    ? (signedArea >= 0.0 ? -1.0 : 1.0)
                    : 1.0;
            else if (string.Equals(
                side,
                "Right",
                StringComparison.OrdinalIgnoreCase))
                sign = -1.0;
            else
                sign = 1.0;
            return left.MultiplyBy(sign);
        }

        private static bool TryCreateCivilSlopeRay(
            Database database,
            SourceSnapshot source,
            Point3d start,
            Point3d end,
            ObjectId layerId,
            string mode,
            short colorIndex,
            int index,
            out ObjectId featureLineId,
            out string error)
        {
            featureLineId = ObjectId.Null;
            error = string.Empty;
            ObjectId temporaryId = ObjectId.Null;
            try
            {
                using (Transaction transaction =
                    database.TransactionManager.StartTransaction())
                {
                    BlockTableRecord space = transaction.GetObject(
                        SymbolUtilityServices.GetBlockModelSpaceId(database),
                        OpenMode.ForWrite,
                        false) as BlockTableRecord;
                    if (space == null)
                        throw new InvalidOperationException(
                            "Model space is unavailable.");
                    var temporary = new Polyline3d(
                        Poly3dType.SimplePoly,
                        new Point3dCollection(
                            new[] { start, end }),
                        false);
                    temporary.SetDatabaseDefaults(database);
                    temporary.LayerId = layerId;
                    temporaryId = space.AppendEntity(temporary);
                    transaction.AddNewlyCreatedDBObject(
                        temporary,
                        true);
                    transaction.Commit();
                }

                string requested =
                    "CE-" + mode + "-SLOPE-" +
                    (source.ObjectId.IsNull
                        ? "JUNCTION"
                        : source.ObjectId.Handle.ToString()) +
                    "-" + (index + 1).ToString(
                        CultureInfo.InvariantCulture);
                string name = UniqueFeatureLineName(
                    database,
                    requested,
                    ObjectId.Null);

                // Keep presentation slope rays site-less so they remain
                // native Civil 3D feature-line objects without participating in
                // site crossing/elevation interactions with the design bellmouth.
                featureLineId = CivilFeatureLine.Create(
                    name,
                    temporaryId);

                using (Transaction transaction =
                    database.TransactionManager.StartTransaction())
                {
                    CivilFeatureLine ray = OpenFeatureLine(
                        transaction,
                        featureLineId,
                        OpenMode.ForWrite);
                    if (ray == null)
                        throw new InvalidOperationException(
                            "Civil 3D did not create the slope-ray feature line.");
                    ray.LayerId = layerId;
                    ray.ColorIndex =
                        NormalizeAciColor(colorIndex);
                    transaction.Commit();
                }
                ApplyGradingColour(database, featureLineId, colorIndex);
                return true;
            }
            catch (System.Exception exception)
            {
                error = exception.Message;
                if (!featureLineId.IsNull)
                    Cleanup(database, featureLineId);
                featureLineId = ObjectId.Null;
                return false;
            }
            finally
            {
                if (!temporaryId.IsNull)
                    Cleanup(database, temporaryId);
            }
        }

        private static void ApplyGradingColour(Database database, ObjectId id, short colourIndex)
        {
            short normalized = NormalizeAciColor(colourIndex);
            ObjectId styleId;
            // Civil 3D 2023 needs the copied style committed before assignment.
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                CivilFeatureLine line = OpenFeatureLine(transaction, id, OpenMode.ForWrite);
                styleId = FeatureLineColourService.Prepare(database, line, normalized, transaction);
                transaction.Commit();
            }
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                CivilFeatureLine line = OpenFeatureLine(transaction, id, OpenMode.ForWrite);
                if (!FeatureLineColourService.Assign(line, styleId, transaction))
                    throw new InvalidOperationException("Civil 3D did not accept the grading colour style.");
                line.ColorIndex = normalized;
                line.RecordGraphicsModified(true);
                transaction.Commit();
            }
        }

        private sealed class SlopeRaySample
        {
            internal Point3d Point;
            internal Vector2d Direction;
            internal Point3d EndPoint;
            internal bool Cut;
            internal bool HalfLength;
            internal bool Valid;
        }

        private static ObjectId EnsureLayer(
            Database database,
            Transaction transaction,
            string layerName,
            short colorIndex)
        {
            LayerTable table = transaction.GetObject(
                database.LayerTableId,
                OpenMode.ForRead,
                false) as LayerTable;
            if (table == null)
                throw new InvalidOperationException(
                    "Layer table is unavailable.");

            if (table.Has(layerName))
                return table[layerName];

            table.UpgradeOpen();
            var layer = new LayerTableRecord
            {
                Name = layerName,
                Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(
                    Autodesk.AutoCAD.Colors.ColorMethod.ByAci,
                    colorIndex)
            };
            ObjectId id = table.Add(layer);
            transaction.AddNewlyCreatedDBObject(layer, true);
            return id;
        }

        private static void CleanupHandleList(
            Database database,
            string handles)
        {
            if (database == null ||
                string.IsNullOrWhiteSpace(handles))
                return;

            foreach (string value in handles.Split(
                new[] { ';' },
                StringSplitOptions.RemoveEmptyEntries))
            {
                ObjectId id = ResolveHandle(
                    database,
                    value.Trim());
                if (!id.IsNull)
                    Cleanup(database, id);
            }
        }

        private static bool TryCreateFeatureLineCandidate(
            Document document,
            SourceSnapshot source,
            IList<Point3d> points,
            string toeLayerName,
            short toeColorIndex,
            out ObjectId featureLineId,
            out string error)
        {
            featureLineId = ObjectId.Null;
            error = string.Empty;
            ObjectId temporaryId = ObjectId.Null;
            try
            {
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    BlockTableRecord space = transaction.GetObject(
                        SymbolUtilityServices.GetBlockModelSpaceId(document.Database),
                        OpenMode.ForWrite,
                        false) as BlockTableRecord;
                    ObjectId toeLayerId = EnsureLayer(
                        document.Database,
                        transaction,
                        SafeName(toeLayerName, "CE-JUNCTION-TOE"),
                        2);
                    var temporary = new Polyline3d(Poly3dType.SimplePoly, new Point3dCollection(points.ToArray()), source.Closed);
                    temporary.SetDatabaseDefaults(document.Database);
                    temporary.LayerId = toeLayerId;
                    temporaryId = space.AppendEntity(temporary);
                    transaction.AddNewlyCreatedDBObject(temporary, true);
                    transaction.Commit();
                }

                string candidateName = "CE-GRADE-CANDIDATE-" + Guid.NewGuid().ToString("N");
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    featureLineId = source.SiteId.IsNull
                        ? CivilFeatureLine.Create(candidateName, temporaryId)
                        : CivilFeatureLine.Create(candidateName, temporaryId, source.SiteId);
                    CivilFeatureLine featureLine = OpenFeatureLine(transaction, featureLineId, OpenMode.ForWrite);
                    if (featureLine == null || featureLine.IsReferenceObject)
                        throw new InvalidOperationException("Civil 3D did not return an editable daylight feature line.");
                    ObjectId toeLayerId = EnsureLayer(
                        document.Database,
                        transaction,
                        SafeName(toeLayerName, "CE-JUNCTION-TOE"),
                        2);
                    featureLine.LayerId = toeLayerId;
                    featureLine.ColorIndex =
                        NormalizeAciColor(toeColorIndex);
                    if (!string.IsNullOrWhiteSpace(source.StyleName))
                    {
                        try { featureLine.StyleName = source.StyleName; } catch { }
                    }
                    Point3dCollection verification = featureLine.GetPoints(FeatureLinePointType.AllPoints);
                    if (verification == null || verification.Count < 2 || verification.Cast<Point3d>().Any(point => !Finite(point)))
                        throw new InvalidOperationException("The daylight candidate failed finite-point verification.");
                    transaction.Commit();
                }
                ApplyGradingColour(document.Database, featureLineId, toeColorIndex);
                return true;
            }
            catch (System.Exception exception)
            {
                error = exception.Message;
                if (!featureLineId.IsNull) Cleanup(document.Database, featureLineId);
                featureLineId = ObjectId.Null;
                return false;
            }
            finally
            {
                if (!temporaryId.IsNull) Cleanup(document.Database, temporaryId);
            }
        }

        private static bool TrySwapCandidate(Database database, ObjectId oldChildId, ObjectId candidateId, string desiredName, out string error)
        {
            error = string.Empty;
            try
            {
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    CivilFeatureLine candidate = OpenFeatureLine(transaction, candidateId, OpenMode.ForWrite);
                    if (candidate == null) throw new InvalidOperationException("The verified daylight candidate became unavailable.");
                    CivilFeatureLine oldChild = OpenFeatureLine(transaction, oldChildId, OpenMode.ForWrite);
                    if (oldChild != null)
                    {
                        try { oldChild.Name = "CE-OLD-DAYLIGHT-" + Guid.NewGuid().ToString("N"); } catch { }
                    }
                    try { candidate.Name = desiredName; }
                    catch { candidate.Name = "CE-DAYLIGHT-" + candidateId.Handle.ToString(); }
                    if (oldChild != null && !oldChild.IsErased) oldChild.Erase();
                    transaction.Commit();
                }
                return true;
            }
            catch (System.Exception exception)
            {
                error = "The new daylight was verified, but the candidate swap failed safely: " + exception.Message;
                return false;
            }
        }

        private static void EnsureSourceHasSite(
            Document document,
            ObjectId sourceId,
            string requestedSite,
            bool requireSite)
        {
            ObjectId currentSite = ObjectId.Null;
            using (Transaction transaction =
                document.Database.TransactionManager.StartTransaction())
            {
                CivilFeatureLine source =
                    OpenFeatureLine(
                        transaction,
                        sourceId,
                        OpenMode.ForRead);
                if (source == null ||
                    source.IsReferenceObject)
                    throw new InvalidOperationException(
                        "The source feature line is unavailable or referenced.");
                currentSite = source.SiteId;
            }

            bool automatic =
                IsAutomaticSiteChoice(requestedSite);
            if (automatic &&
                !currentSite.IsNull)
                return;
            if (automatic &&
                currentSite.IsNull &&
                !requireSite)
                return;

            string siteName =
                automatic
                    ? PlatformSiteName
                    : SafeName(
                        requestedSite,
                        PlatformSiteName);
            ObjectId siteId =
                EnsureSite(
                    document.Database,
                    CivilApplication.ActiveDocument,
                    siteName);
            if (siteId.IsNull)
                throw new InvalidOperationException(
                    "Civil 3D could not create or resolve Site '" +
                    siteName +
                    "'.");

            if (currentSite == siteId)
                return;

            if (!MoveToSite(
                    sourceId,
                    siteId))
                throw new InvalidOperationException(
                    "Civil 3D could not move the source feature line into Site '" +
                    siteName +
                    "'.");
        }

        private static ObjectId EnsureSite(Database database, CivilDocument civilDocument, string name)
        {
            if (civilDocument == null) return ObjectId.Null;
            try
            {
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId id in civilDocument.GetSiteIds())
                    {
                        DBObject value = transaction.GetObject(id, OpenMode.ForRead, false);
                        PropertyInfo property = value.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
                        string current = property == null ? string.Empty : Convert.ToString(property.GetValue(value, null), CultureInfo.CurrentCulture);
                        if (string.Equals(current, name, StringComparison.OrdinalIgnoreCase)) return id;
                    }
                }
            }
            catch { }

            Type siteType = typeof(CivilFeatureLine).Assembly.GetType("Autodesk.Civil.DatabaseServices.Site", false);
            if (siteType == null) return ObjectId.Null;
            foreach (MethodInfo method in siteType.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(item => string.Equals(item.Name, "Create", StringComparison.OrdinalIgnoreCase)).OrderBy(item => item.GetParameters().Length))
            {
                object[] args = BuildHostArguments(method.GetParameters(), database, civilDocument, name, ObjectId.Null, Point3d.Origin);
                if (args == null) continue;
                try
                {
                    object value = method.Invoke(null, args);
                    if (value is ObjectId) return (ObjectId)value;
                    DBObject dbObject = value as DBObject;
                    if (dbObject != null) return dbObject.ObjectId;
                }
                catch { }
            }
            return ObjectId.Null;
        }

        private static bool MoveToSite(
            ObjectId featureLineId,
            ObjectId siteId)
        {
            if (featureLineId.IsNull ||
                siteId.IsNull)
                return false;
            try
            {
                CivilFeatureLine.MoveToSite(
                    featureLineId,
                    siteId);
                return true;
            }
            catch { }

            // Compatibility fallback for unusual host shims.
            foreach (MethodInfo method in
                typeof(CivilFeatureLine)
                    .GetMethods(
                        BindingFlags.Public |
                        BindingFlags.Static)
                    .Where(item =>
                        item.Name.IndexOf(
                            "MoveToSite",
                            StringComparison.OrdinalIgnoreCase) >= 0))
            {
                ParameterInfo[] parameters =
                    method.GetParameters();
                if (parameters.Length != 2 ||
                    parameters.Any(parameter =>
                        parameter.ParameterType != typeof(ObjectId)))
                    continue;
                try
                {
                    method.Invoke(
                        null,
                        new object[]
                        {
                            featureLineId,
                            siteId
                        });
                    return true;
                }
                catch { }
            }
            return false;
        }

        private static ObjectId TryCreateGradingGroup(
            Database database,
            ObjectId siteId,
            string name,
            out string error)
        {
            error = string.Empty;
            CivilDocument civilDocument =
                CivilApplication.ActiveDocument;
            if (database == null ||
                civilDocument == null ||
                siteId.IsNull)
            {
                error =
                    "The active Civil 3D document or grading Site is unavailable.";
                return ObjectId.Null;
            }

            Type groupType =
                typeof(CivilFeatureLine)
                    .Assembly
                    .GetType(
                        "Autodesk.Civil.DatabaseServices.GradingGroup",
                        false);
            if (groupType == null)
            {
                error =
                    "The Civil 3D GradingGroup API is unavailable.";
                return ObjectId.Null;
            }

            ObjectId existing =
                FindGradingGroup(
                    database,
                    siteId,
                    name,
                    groupType);
            if (!existing.IsNull)
            {
                ConfigureGradingGroup(
                    database,
                    existing,
                    name);
                return existing;
            }

            string lastError = string.Empty;
            foreach (MethodInfo method in
                groupType
                    .GetMethods(
                        BindingFlags.Public |
                        BindingFlags.Static)
                    .Where(item =>
                        string.Equals(
                            item.Name,
                            "Create",
                            StringComparison.OrdinalIgnoreCase))
                    .OrderBy(item =>
                        item.GetParameters().Length))
            {
                object[] args =
                    BuildGradingGroupArguments(
                        method.GetParameters(),
                        database,
                        civilDocument,
                        name,
                        siteId);
                if (args == null)
                    continue;

                try
                {
                    object value =
                        method.Invoke(
                            null,
                            args);
                    ObjectId id =
                        value is ObjectId
                            ? (ObjectId)value
                            : value is DBObject
                                ? ((DBObject)value).ObjectId
                                : ObjectId.Null;
                    if (id.IsNull)
                        continue;

                    ConfigureGradingGroup(
                        database,
                        id,
                        name);
                    return id;
                }
                catch (System.Exception exception)
                {
                    lastError =
                        exception.InnerException == null
                            ? exception.Message
                            : exception.InnerException.Message;
                }
            }

            error =
                string.IsNullOrWhiteSpace(lastError)
                    ? "No compatible Civil 3D GradingGroup.Create overload succeeded."
                    : lastError;
            return ObjectId.Null;
        }

        private static ObjectId FindGradingGroup(
            Database database,
            ObjectId siteId,
            string name,
            Type groupType)
        {
            try
            {
                using (Transaction transaction =
                    database.TransactionManager.StartTransaction())
                {
                    DBObject site =
                        transaction.GetObject(
                            siteId,
                            OpenMode.ForRead,
                            false);
                    if (site == null)
                        return ObjectId.Null;

                    foreach (MethodInfo method in
                        site.GetType()
                            .GetMethods(
                                BindingFlags.Public |
                                BindingFlags.Instance)
                            .Where(item =>
                                item.GetParameters().Length == 0 &&
                                item.Name.IndexOf(
                                    "GradingGroup",
                                    StringComparison.OrdinalIgnoreCase) >= 0 &&
                                item.Name.StartsWith(
                                    "Get",
                                    StringComparison.OrdinalIgnoreCase)))
                    {
                        object value;
                        try
                        {
                            value =
                                method.Invoke(
                                    site,
                                    null);
                        }
                        catch
                        {
                            continue;
                        }

                        IEnumerable enumerable =
                            value as IEnumerable;
                        if (enumerable == null)
                            continue;

                        foreach (object entry in enumerable)
                        {
                            if (!(entry is ObjectId))
                                continue;
                            ObjectId id =
                                (ObjectId)entry;
                            DBObject group = null;
                            try
                            {
                                group =
                                    transaction.GetObject(
                                        id,
                                        OpenMode.ForRead,
                                        false);
                            }
                            catch { }
                            if (group == null ||
                                !groupType.IsAssignableFrom(
                                    group.GetType()))
                                continue;

                            string current =
                                ReadStringProperty(
                                    group,
                                    "Name");
                            if (string.Equals(
                                    current,
                                    name,
                                    StringComparison.OrdinalIgnoreCase))
                                return id;
                        }
                    }
                }
            }
            catch { }
            return ObjectId.Null;
        }

        private static void ConfigureGradingGroup(
            Database database,
            ObjectId groupId,
            string name)
        {
            if (database == null ||
                groupId.IsNull)
                return;
            try
            {
                using (Transaction transaction =
                    database.TransactionManager.StartTransaction())
                {
                    DBObject group =
                        transaction.GetObject(
                            groupId,
                            OpenMode.ForWrite,
                            false);
                    if (group == null)
                        return;

                    SetProperty(
                        group,
                        "Name",
                        name);
                    // Native Civil 3D grading/infill is much easier to verify in
                    // Prospector when the group maintains its dynamic surface.
                    SetProperty(
                        group,
                        "AutomaticSurfaceCreation",
                        true);
                    SetProperty(
                        group,
                        "SurfaceName",
                        name + "-SURFACE");
                    transaction.Commit();
                }
            }
            catch { }
        }

        private static object[] BuildGradingGroupArguments(
            ParameterInfo[] parameters,
            Database database,
            CivilDocument civilDocument,
            string name,
            ObjectId siteId)
        {
            var args =
                new object[parameters.Length];
            int objectIdCount =
                parameters.Count(parameter =>
                    parameter.ParameterType ==
                    typeof(ObjectId));
            for (int index = 0;
                 index < parameters.Length;
                 index++)
            {
                ParameterInfo parameter =
                    parameters[index];
                Type type =
                    parameter.ParameterType;
                string parameterName =
                    (parameter.Name ?? string.Empty)
                        .ToLowerInvariant();

                if (type == typeof(Database))
                    args[index] = database;
                else if (type == typeof(CivilDocument))
                    args[index] = civilDocument;
                else if (type == typeof(string))
                    args[index] = name;
                else if (type == typeof(ObjectId))
                {
                    if (parameterName.Contains("site") ||
                        objectIdCount == 1)
                        args[index] = siteId;
                    else
                        args[index] = ObjectId.Null;
                }
                else if (type == typeof(bool))
                    args[index] = true;
                else if (type == typeof(double))
                    args[index] = 1.0;
                else if (parameter.HasDefaultValue)
                    args[index] =
                        parameter.DefaultValue;
                else
                    return null;
            }
            return args;
        }

        private static bool TryCreateInfill(
            ObjectId groupId,
            Point3d seed,
            out ObjectId infillId,
            out string error)
        {
            infillId = ObjectId.Null;
            error = string.Empty;

            Type gradingType =
                typeof(CivilFeatureLine)
                    .Assembly
                    .GetType(
                        "Autodesk.Civil.DatabaseServices.Grading",
                        false);
            if (gradingType == null)
            {
                error =
                    "The Civil 3D Grading API is unavailable.";
                return false;
            }

            string lastError = string.Empty;
            foreach (MethodInfo method in
                gradingType
                    .GetMethods(
                        BindingFlags.Public |
                        BindingFlags.Static)
                    .Where(item =>
                        item.Name.IndexOf(
                            "CreateInfill",
                            StringComparison.OrdinalIgnoreCase) >= 0)
                    .OrderBy(item =>
                        item.GetParameters().Length))
            {
                object[] args;
                if (!TryBuildInfillArguments(
                        method.GetParameters(),
                        groupId,
                        seed,
                        out args))
                    continue;

                try
                {
                    object value =
                        method.Invoke(
                            null,
                            args);
                    if (TryReadCreatedObjectId(
                            value,
                            out infillId))
                        return true;

                    // Some Civil 3D 2023 builds expose CreateInfill as void.
                    // A successful invocation is still a successful infill.
                    if (method.ReturnType == typeof(void) ||
                        value == null)
                        return true;
                }
                catch (System.Exception exception)
                {
                    lastError =
                        exception.InnerException == null
                            ? exception.Message
                            : exception.InnerException.Message;
                }
            }

            // Compatibility fallback: a few host builds expose infill creation
            // on the grading-group object instead of the Grading static class.
            Document document =
                AcApplication.DocumentManager.MdiActiveDocument;
            if (document != null &&
                document.Database != null &&
                !groupId.IsNull)
            {
                try
                {
                    using (Transaction transaction =
                        document.Database.TransactionManager.StartTransaction())
                    {
                        DBObject group =
                            transaction.GetObject(
                                groupId,
                                OpenMode.ForWrite,
                                false);
                        if (group != null)
                        {
                            foreach (MethodInfo method in
                                group.GetType()
                                    .GetMethods(
                                        BindingFlags.Public |
                                        BindingFlags.Instance)
                                    .Where(item =>
                                        item.Name.IndexOf(
                                            "CreateInfill",
                                            StringComparison.OrdinalIgnoreCase) >= 0)
                                    .OrderBy(item =>
                                        item.GetParameters().Length))
                            {
                                object[] args;
                                if (!TryBuildInfillArguments(
                                        method.GetParameters(),
                                        groupId,
                                        seed,
                                        out args,
                                        true))
                                    continue;
                                try
                                {
                                    object value =
                                        method.Invoke(
                                            group,
                                            args);
                                    if (TryReadCreatedObjectId(
                                            value,
                                            out infillId))
                                        return true;
                                    if (method.ReturnType == typeof(void) ||
                                        value == null)
                                        return true;
                                }
                                catch (System.Exception exception)
                                {
                                    lastError =
                                        exception.InnerException == null
                                            ? exception.Message
                                            : exception.InnerException.Message;
                                }
                            }
                        }
                    }
                }
                catch (System.Exception exception)
                {
                    lastError = exception.Message;
                }
            }

            error =
                string.IsNullOrWhiteSpace(lastError)
                    ? "No compatible Civil 3D Grading.CreateInfill overload succeeded. The closed feature line and grading group must be in the same Site."
                    : lastError;
            return false;
        }

        private static bool TryBuildInfillArguments(
            ParameterInfo[] parameters,
            ObjectId groupId,
            Point3d seed,
            out object[] args,
            bool instanceGroupMethod = false)
        {
            args =
                new object[parameters.Length];
            int objectIdSeen = 0;

            for (int index = 0;
                 index < parameters.Length;
                 index++)
            {
                ParameterInfo parameter =
                    parameters[index];
                Type type =
                    parameter.ParameterType;
                string name =
                    (parameter.Name ?? string.Empty)
                        .ToLowerInvariant();

                if (type == typeof(ObjectId))
                {
                    // Instance grading-group methods normally do not need the
                    // parent group id unless the API explicitly asks for it.
                    args[index] =
                        !instanceGroupMethod &&
                        (objectIdSeen == 0 ||
                         name.Contains("group"))
                            ? groupId
                            : ObjectId.Null;
                    objectIdSeen++;
                }
                else if (type == typeof(Point3d))
                {
                    args[index] = seed;
                }
                else if (type == typeof(Point2d))
                {
                    args[index] =
                        new Point2d(
                            seed.X,
                            seed.Y);
                }
                else if (type == typeof(ObjectIdCollection))
                {
                    args[index] =
                        new ObjectIdCollection(
                            new[] { groupId });
                }
                else if (type == typeof(string))
                {
                    args[index] =
                        "CE Junction Infill";
                }
                else if (type == typeof(bool))
                {
                    args[index] = true;
                }
                else if (type == typeof(double))
                {
                    args[index] = 0.0;
                }
                else if (type == typeof(int))
                {
                    args[index] = 0;
                }
                else if (type == typeof(short))
                {
                    args[index] = (short)0;
                }
                else if (type.IsEnum)
                {
                    Array values =
                        Enum.GetValues(type);
                    if (values.Length == 0)
                        return false;
                    args[index] =
                        values.GetValue(0);
                }
                else if (parameter.HasDefaultValue)
                {
                    args[index] =
                        parameter.DefaultValue;
                }
                else
                {
                    return false;
                }
            }
            return true;
        }

        private static bool TryReadCreatedObjectId(
            object value,
            out ObjectId id)
        {
            id = ObjectId.Null;
            if (value is ObjectId)
            {
                id = (ObjectId)value;
                return true;
            }

            DBObject dbObject =
                value as DBObject;
            if (dbObject != null)
            {
                id = dbObject.ObjectId;
                return true;
            }

            ObjectIdCollection ids =
                value as ObjectIdCollection;
            if (ids != null &&
                ids.Count > 0)
            {
                id = ids[0];
                return true;
            }

            IEnumerable enumerable =
                value as IEnumerable;
            if (enumerable != null &&
                !(value is string))
            {
                foreach (object item in enumerable)
                {
                    if (item is ObjectId)
                    {
                        id = (ObjectId)item;
                        return true;
                    }
                    DBObject candidate =
                        item as DBObject;
                    if (candidate != null)
                    {
                        id = candidate.ObjectId;
                        return true;
                    }
                }
            }
            return false;
        }

        private static object[] BuildHostArguments(ParameterInfo[] parameters, Database database, CivilDocument civilDocument, string name, ObjectId relatedId, Point3d point)
        {
            var args = new object[parameters.Length];
            for (int index = 0; index < parameters.Length; index++)
            {
                Type type = parameters[index].ParameterType;
                if (type == typeof(Database)) args[index] = database;
                else if (type == typeof(CivilDocument)) args[index] = civilDocument;
                else if (type == typeof(string)) args[index] = name;
                else if (type == typeof(ObjectId)) args[index] = relatedId;
                else if (type == typeof(Point3d)) args[index] = point;
                else if (type == typeof(bool)) args[index] = true;
                else if (type == typeof(double)) args[index] = 0.0;
                else if (parameters[index].HasDefaultValue) args[index] = parameters[index].DefaultValue;
                else return null;
            }
            return args;
        }

        private static void SetProperty(object target, string name, object value)
        {
            if (target == null) return;
            PropertyInfo property = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property == null || !property.CanWrite) return;
            try { property.SetValue(target, value, null); } catch { }
        }

        private static bool TryReadSource(Database database, ObjectId sourceId, out SourceSnapshot snapshot, out string error)
        {
            snapshot = null;
            error = string.Empty;
            try
            {
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    CivilFeatureLine source = OpenFeatureLine(transaction, sourceId, OpenMode.ForRead);
                    if (!Editable(source, transaction))
                    {
                        error = "The source feature line is unavailable, referenced or on a locked layer.";
                        return false;
                    }
                    Point3dCollection collection = source.GetPoints(FeatureLinePointType.AllPoints);
                    if (collection == null || collection.Count < 2)
                    {
                        error = "The source feature line has too few points.";
                        return false;
                    }
                    snapshot = new SourceSnapshot
                    {
                        ObjectId = sourceId,
                        Name = source.Name,
                        LayerId = source.LayerId,
                        SiteId = source.SiteId,
                        StyleName = source.StyleName,
                        ColorIndex = (short)source.ColorIndex,
                        Closed = source.Closed,
                        Points = collection.Cast<Point3d>().ToList()
                    };
                }
                return snapshot.Points.All(Finite);
            }
            catch (System.Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }

        private static bool TryCreateEditableDrapeCopy(
            Document document,
            ObjectId sourceId,
            out ObjectId featureLineId,
            out string error)
        {
            featureLineId = ObjectId.Null;
            error = string.Empty;
            if (document == null ||
                document.Database == null ||
                sourceId.IsNull ||
                sourceId.IsErased)
            {
                error = "The selected feature-line source is unavailable.";
                return false;
            }

            List<Point3d> points;
            bool closed;
            ObjectId sourceLayerId;
            string sourceStyleName;
            short sourceColour;
            string sourceName;
            try
            {
                using (Transaction read =
                    document.Database.TransactionManager.StartTransaction())
                {
                    CivilFeatureLine source = OpenFeatureLine(
                        read,
                        sourceId,
                        OpenMode.ForRead);
                    if (source == null)
                    {
                        error = "The selected object is not a readable Civil 3D feature line.";
                        return false;
                    }

                    Point3dCollection collection = source.GetPoints(
                        FeatureLinePointType.AllPoints);
                    if (collection == null || collection.Count < 2)
                    {
                        error = "The selected feature line has too few readable points to clone.";
                        return false;
                    }

                    points = collection.Cast<Point3d>().ToList();
                    if (points.Any(point => !Finite(point)))
                    {
                        error = "The selected feature line contains a non-finite coordinate.";
                        return false;
                    }

                    closed = source.Closed;
                    sourceLayerId = source.LayerId;
                    sourceStyleName = source.StyleName;
                    sourceColour = (short)source.ColorIndex;
                    sourceName = source.Name;
                }
            }
            catch (System.Exception exception)
            {
                error = "The selected feature line could not be read for an editable copy: " +
                    exception.Message;
                return false;
            }

            ObjectId outputLayerId = sourceLayerId;
            try
            {
                using (Transaction layerRead =
                    document.Database.TransactionManager.StartTransaction())
                {
                    LayerTableRecord layer = sourceLayerId.IsNull
                        ? null
                        : layerRead.GetObject(
                            sourceLayerId,
                            OpenMode.ForRead,
                            false) as LayerTableRecord;
                    if (layer != null && layer.IsLocked)
                        outputLayerId = document.Database.Clayer;
                }
            }
            catch
            {
                outputLayerId = document.Database.Clayer;
            }

            ObjectId temporaryId = ObjectId.Null;
            try
            {
                using (Transaction createTemporary =
                    document.Database.TransactionManager.StartTransaction())
                {
                    BlockTableRecord model = createTemporary.GetObject(
                        SymbolUtilityServices.GetBlockModelSpaceId(document.Database),
                        OpenMode.ForWrite,
                        false) as BlockTableRecord;
                    if (model == null)
                        throw new InvalidOperationException(
                            "Model space could not be opened for the editable drape copy.");

                    var temporary = new Polyline3d(
                        Poly3dType.SimplePoly,
                        new Point3dCollection(points.ToArray()),
                        closed);
                    temporary.SetDatabaseDefaults(document.Database);
                    if (!outputLayerId.IsNull)
                        temporary.LayerId = outputLayerId;
                    temporaryId = model.AppendEntity(temporary);
                    createTemporary.AddNewlyCreatedDBObject(temporary, true);
                    createTemporary.Commit();
                }

                string cloneName = UniqueFeatureLineName(
                    document.Database,
                    SafeName(sourceName, "AUTO-FEATURE-LINE") + "-DRAPE",
                    ObjectId.Null);

                using (Transaction createFeature =
                    document.Database.TransactionManager.StartTransaction())
                {
                    featureLineId = CivilFeatureLine.Create(
                        cloneName,
                        temporaryId);
                    CivilFeatureLine featureLine = OpenFeatureLine(
                        createFeature,
                        featureLineId,
                        OpenMode.ForWrite);
                    if (featureLine == null || featureLine.IsReferenceObject)
                        throw new InvalidOperationException(
                            "Civil 3D did not return an editable normal feature-line copy.");

                    if (!outputLayerId.IsNull)
                        featureLine.LayerId = outputLayerId;
                    featureLine.ColorIndex = sourceColour;
                    if (!string.IsNullOrWhiteSpace(sourceStyleName))
                    {
                        try { featureLine.StyleName = sourceStyleName; }
                        catch { }
                    }

                    Point3dCollection verification = featureLine.GetPoints(
                        FeatureLinePointType.AllPoints);
                    if (verification == null ||
                        verification.Count < 2 ||
                        verification.Cast<Point3d>().Any(point => !Finite(point)))
                        throw new InvalidOperationException(
                            "The editable feature-line copy failed geometry verification.");

                    createFeature.Commit();
                }

                Cleanup(document.Database, temporaryId);
                temporaryId = ObjectId.Null;
                return true;
            }
            catch (System.Exception exception)
            {
                error = "A normal editable feature-line copy could not be created: " +
                    exception.Message;
                if (!featureLineId.IsNull)
                    Cleanup(document.Database, featureLineId);
                featureLineId = ObjectId.Null;
                return false;
            }
            finally
            {
                if (!temporaryId.IsNull)
                    Cleanup(document.Database, temporaryId);
            }
        }

        private static bool IsEditableFeatureLine(Database database, ObjectId id, out string error)
        {
            error = string.Empty;
            try
            {
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    CivilFeatureLine featureLine = OpenFeatureLine(transaction, id, OpenMode.ForRead);
                    if (!Editable(featureLine, transaction))
                    {
                        error = "The selected object is not a local editable feature line or is on a locked layer.";
                        return false;
                    }
                }
                return true;
            }
            catch (System.Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }

        private static List<SurfaceOption> ReadSurfaces(Document document)
        {
            var result = new List<SurfaceOption>();
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (document == null || civilDocument == null) return result;
            try
            {
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId id in civilDocument.GetSurfaceIds())
                    {
                        CivilSurface surface = transaction.GetObject(id, OpenMode.ForRead, false) as CivilSurface;
                        if (surface != null && !string.IsNullOrWhiteSpace(surface.Name)) result.Add(new SurfaceOption(surface.Name, id));
                    }
                }
            }
            catch { }
            return result.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        private static List<ObjectId> ResolveGradeSourceIds(
            Document document,
            string scope,
            string edgeLayerText)
        {
            var result = new List<ObjectId>();
            if (document == null)
                return result;

            bool allMatching = string.Equals(
                scope,
                "All shoulder/sidewalk bellmouth edge lines",
                StringComparison.OrdinalIgnoreCase);
            bool matchingOnly = allMatching ||
                string.Equals(
                    scope,
                    "Multiple selected shoulder/sidewalk bellmouth edge lines",
                    StringComparison.OrdinalIgnoreCase);
            HashSet<string> edgeLayers =
                ParseLayerNames(edgeLayerText);

            ObjectId[] candidateIds;
            if (allMatching)
            {
                candidateIds = ReadModelSpaceIds(
                    document.Database);
            }
            else
            {
                PromptSelectionResult selection =
                    SelectFeatureLines(
                        document.Editor,
                        matchingOnly
                            ? "\nSelect multiple shoulder/sidewalk bellmouth edge feature lines: "
                            : "\nSelect multiple junction / feature lines to grade: ");
                if (selection.Status != PromptStatus.OK ||
                    selection.Value == null)
                    return result;
                candidateIds =
                    selection.Value.GetObjectIds();
            }

            using (Transaction transaction =
                document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in
                    candidateIds.Distinct())
                {
                    CivilFeatureLine line =
                        OpenFeatureLine(
                            transaction,
                            id,
                            OpenMode.ForRead);
                    if (!Editable(line, transaction))
                        continue;
                    if (matchingOnly &&
                        !MatchesShoulderSidewalkBellmouthEdge(
                            line,
                            transaction,
                            edgeLayers))
                        continue;
                    result.Add(id);
                }
            }
            return result;
        }

        private static List<ObjectId> ResolveClosedJunctionInfillIds(
            Document document,
            string scope)
        {
            var result = new List<ObjectId>();
            if (document == null)
                return result;

            bool all =
                (scope ?? string.Empty)
                    .StartsWith(
                        "All closed ",
                        StringComparison.OrdinalIgnoreCase);
            ObjectId[] candidateIds;
            if (all)
            {
                candidateIds =
                    ReadModelSpaceIds(
                        document.Database);
            }
            else
            {
                PromptSelectionResult selection =
                    SelectFeatureLines(
                        document.Editor,
                        "\nSelect multiple CLOSED junction feature lines for native infill: ");
                if (selection.Status != PromptStatus.OK ||
                    selection.Value == null)
                    return result;
                candidateIds =
                    selection.Value.GetObjectIds();
            }

            using (Transaction transaction =
                document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in
                    candidateIds.Distinct())
                {
                    CivilFeatureLine line =
                        OpenFeatureLine(
                            transaction,
                            id,
                            OpenMode.ForRead);
                    if (!Editable(line, transaction) ||
                        !line.Closed)
                        continue;

                    // For an explicit multiple selection, the user's selection is
                    // authoritative. "All" is deliberately restricted to CE/junction
                    // style identities so unrelated closed site feature lines are not
                    // accidentally infilled.
                    if (all &&
                        !IsRecognisedClosedJunctionLine(
                            line,
                            transaction))
                        continue;
                    result.Add(id);
                }
            }
            return result;
        }

        private static ObjectId[] ReadModelSpaceIds(
            Database database)
        {
            if (database == null)
                return new ObjectId[0];
            try
            {
                using (Transaction transaction =
                    database.TransactionManager.StartTransaction())
                {
                    BlockTableRecord model =
                        transaction.GetObject(
                            SymbolUtilityServices.GetBlockModelSpaceId(
                                database),
                            OpenMode.ForRead,
                            false) as BlockTableRecord;
                    return model == null
                        ? new ObjectId[0]
                        : model.Cast<ObjectId>().ToArray();
                }
            }
            catch
            {
                return new ObjectId[0];
            }
        }

        private static HashSet<string> ParseLayerNames(
            string value)
        {
            return new HashSet<string>(
                (value ?? string.Empty)
                    .Split(
                        new[] { ',', ';', '|' },
                        StringSplitOptions.RemoveEmptyEntries)
                    .Select(item => item.Trim())
                    .Where(item => !string.IsNullOrWhiteSpace(item)),
                StringComparer.OrdinalIgnoreCase);
        }

        private static bool MatchesShoulderSidewalkBellmouthEdge(
            CivilFeatureLine line,
            Transaction transaction,
            ISet<string> edgeLayers)
        {
            if (line == null ||
                transaction == null)
                return false;

            string layerName =
                ReadLayerName(
                    line,
                    transaction);
            if (edgeLayers != null &&
                edgeLayers.Contains(layerName))
                return true;

            string identity =
                ((line.Name ?? string.Empty) + " " +
                 layerName)
                    .ToUpperInvariant();
            return identity.Contains("SIDEWALK") ||
                   identity.Contains("SHOULDER") ||
                   identity.Contains("SHLD") ||
                   identity.Contains("VERGE") ||
                   identity.Contains("FOOTWAY") ||
                   identity.Contains("WALKWAY");
        }

        private static bool IsRecognisedClosedJunctionLine(
            CivilFeatureLine line,
            Transaction transaction)
        {
            if (line == null ||
                transaction == null ||
                !line.Closed)
                return false;

            if (ReadRecord(
                    line,
                    transaction,
                    JunctionInfillKey) != null ||
                ReadRecord(
                    line,
                    transaction,
                    GradeLinkKey) != null)
                return true;

            string layerName =
                ReadLayerName(
                    line,
                    transaction);
            string identity =
                ((line.Name ?? string.Empty) + " " +
                 layerName)
                    .ToUpperInvariant();
            if (identity.Contains("JUNCTION") ||
                identity.Contains("BELLMOUTH") ||
                identity.Contains("SIDEWALK") ||
                identity.Contains("SHOULDER") ||
                identity.Contains("SHLD") ||
                identity.Contains("VERGE") ||
                identity.Contains("PLATFORM") ||
                identity.Contains("PAD"))
                return true;

            try
            {
                using (ResultBuffer xdata =
                    line.GetXDataForApplication(
                        "CE_ROAD_JUNCTION"))
                {
                    return xdata != null;
                }
            }
            catch
            {
                return false;
            }
        }

        private static string ReadLayerName(
            Entity entity,
            Transaction transaction)
        {
            if (entity == null ||
                transaction == null ||
                entity.LayerId.IsNull)
                return string.Empty;
            try
            {
                LayerTableRecord layer =
                    transaction.GetObject(
                        entity.LayerId,
                        OpenMode.ForRead,
                        false) as LayerTableRecord;
                return layer == null
                    ? string.Empty
                    : layer.Name ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static NativeInfillResult CreateOrRefreshNativeInfill(
            Document document,
            ObjectId sourceId,
            string requestedSite)
        {
            var result = new NativeInfillResult();
            if (document == null ||
                sourceId.IsNull)
            {
                result.Message =
                    "The closed junction feature line is unavailable.";
                return result;
            }

            SourceSnapshot source;
            string error;
            if (!TryReadSource(
                    document.Database,
                    sourceId,
                    out source,
                    out error))
            {
                result.Message = error;
                return result;
            }
            if (!source.Closed)
            {
                result.Message =
                    "Native infill requires a closed feature line.";
                return result;
            }

            try
            {
                EnsureSourceHasSite(
                    document,
                    sourceId,
                    requestedSite,
                    true);
            }
            catch (System.Exception exception)
            {
                result.Message =
                    "The closed feature line could not be placed in the grading Site. " +
                    exception.Message;
                return result;
            }

            if (!TryReadSource(
                    document.Database,
                    sourceId,
                    out source,
                    out error))
            {
                result.Message = error;
                return result;
            }
            if (source.SiteId.IsNull)
            {
                result.Message =
                    "The closed feature line has no Civil 3D Site after Site assignment.";
                return result;
            }

            NativeInfillLink nativeLink =
                ReadNativeInfillLink(
                    document.Database,
                    sourceId) ??
                new NativeInfillLink();
            GradeLink gradeLink =
                ReadGradeLink(
                    document.Database,
                    sourceId);

            string groupHandle =
                !string.IsNullOrWhiteSpace(
                    nativeLink.GroupHandle)
                    ? nativeLink.GroupHandle
                    : gradeLink == null
                        ? string.Empty
                        : gradeLink.GroupHandle;
            ObjectId groupId =
                ResolveHandle(
                    document.Database,
                    groupHandle);
            if (groupId.IsNull)
            {
                string groupError;
                groupId =
                    TryCreateGradingGroup(
                        document.Database,
                        source.SiteId,
                        "CE-JUNCTION-INFILL-" +
                            sourceId.Handle.ToString(),
                        out groupError);
                if (groupId.IsNull)
                {
                    result.Message =
                        "Civil 3D could not create the grading group. " +
                        groupError;
                    return result;
                }
            }

            string infillHandle =
                !string.IsNullOrWhiteSpace(
                    nativeLink.InfillHandle)
                    ? nativeLink.InfillHandle
                    : gradeLink == null
                        ? string.Empty
                        : gradeLink.InfillHandle;
            ObjectId existingInfill =
                ResolveHandle(
                    document.Database,
                    infillHandle);
            if (!existingInfill.IsNull ||
                nativeLink.Created)
            {
                nativeLink.GroupHandle =
                    groupId.Handle.ToString();
                nativeLink.InfillHandle =
                    existingInfill.IsNull
                        ? nativeLink.InfillHandle
                        : existingInfill.Handle.ToString();
                nativeLink.SiteName =
                    requestedSite;
                nativeLink.Created = true;
                WriteNativeInfillLink(
                    document.Database,
                    sourceId,
                    nativeLink);
                result.Existing = true;
                return result;
            }

            ObjectId infillId;
            string infillError;
            if (!TryCreateInfill(
                    groupId,
                    InteriorSeed(source.Points),
                    out infillId,
                    out infillError))
            {
                string fallbackError;
                if (!TryCreateFallbackInfillSurface(
                        document.Database,
                        sourceId,
                        source,
                        "CE-JUNCTION-INFILL",
                        out infillId,
                        out fallbackError))
                {
                    result.Message =
                        "Civil 3D native CreateGradingInfill is not exposed by the public 2023 .NET Grading API. Native attempt: " +
                        infillError +
                        " CE bounded TIN infill fallback: " +
                        fallbackError;
                    return result;
                }
                result.Message =
                    "CE bounded TIN infill surface created because Civil 3D 2023 does not expose native CreateGradingInfill through the public Grading .NET API.";
            }

            nativeLink.GroupHandle =
                groupId.Handle.ToString();
            nativeLink.InfillHandle =
                infillId.IsNull
                    ? string.Empty
                    : infillId.Handle.ToString();
            nativeLink.SiteName =
                requestedSite;
            nativeLink.Created = true;
            WriteNativeInfillLink(
                document.Database,
                sourceId,
                nativeLink);

            if (gradeLink != null)
            {
                gradeLink.GroupHandle =
                    nativeLink.GroupHandle;
                gradeLink.InfillHandle =
                    nativeLink.InfillHandle;
                gradeLink.NativeInfill = true;
                gradeLink.SiteName =
                    requestedSite;
                WriteGradeLink(
                    document.Database,
                    sourceId,
                    gradeLink);
            }

            result.Created = true;
            return result;
        }

        private static PromptSelectionResult SelectFeatureLines(Editor editor, string message)
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

        private static void WriteDirectDrapeLink(Database database, ObjectId sourceId, DirectDrapeLink link)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                CivilFeatureLine source = OpenFeatureLine(transaction, sourceId, OpenMode.ForWrite);
                if (source == null) throw new InvalidOperationException("The draped feature line is unavailable.");
                Xrecord record = Record(source, transaction, DirectDrapeKey);
                record.Data = new ResultBuffer(
                    new TypedValue((int)DxfCode.Text, link.SurfaceHandle ?? string.Empty),
                    new TypedValue((int)DxfCode.Int16, link.Intermediate ? 1 : 0));
                transaction.Commit();
            }
        }

        private static bool TryReadDirectDrapeLink(CivilFeatureLine source, Transaction transaction, out DirectDrapeLink link)
        {
            link = null;
            TypedValue[] values = ReadRecord(source, transaction, DirectDrapeKey);
            if (values == null || values.Length < 2) return false;
            link = new DirectDrapeLink
            {
                SurfaceHandle = Convert.ToString(values[0].Value, CultureInfo.InvariantCulture),
                Intermediate = Convert.ToInt16(values[1].Value, CultureInfo.InvariantCulture) != 0
            };
            return !string.IsNullOrWhiteSpace(link.SurfaceHandle);
        }

        private static bool TryCreateFallbackInfillSurface(
            Database database,
            ObjectId sourceId,
            SourceSnapshot source,
            string prefix,
            out ObjectId surfaceId,
            out string error)
        {
            surfaceId = ObjectId.Null;
            error = string.Empty;
            if (database == null ||
                source == null ||
                source.Points == null ||
                source.Points.Count < 3)
            {
                error =
                    "The closed source does not contain enough vertices for a TIN infill.";
                return false;
            }

            try
            {
                string baseName =
                    SafeName(
                        prefix,
                        "CE-INFILL") +
                    "-" +
                    sourceId.Handle.ToString();
                string name =
                    UniqueSurfaceName(
                        database,
                        baseName);

                surfaceId =
                    TinSurface.Create(
                        database,
                        name);
                using (Transaction transaction =
                    database.TransactionManager.StartTransaction())
                {
                    TinSurface surface =
                        transaction.GetObject(
                            surfaceId,
                            OpenMode.ForWrite,
                            false) as TinSurface;
                    if (surface == null)
                        throw new InvalidOperationException(
                            "Civil 3D did not return the new TIN surface.");

                    var uniquePoints =
                        new List<Point3d>();
                    foreach (Point3d point in
                        source.Points)
                    {
                        if (!Finite(point))
                            continue;
                        if (uniquePoints.Count > 0 &&
                            uniquePoints[
                                uniquePoints.Count - 1]
                                .DistanceTo(point) <=
                                1e-6)
                            continue;
                        uniquePoints.Add(point);
                    }
                    if (uniquePoints.Count >= 2 &&
                        uniquePoints[0].DistanceTo(
                            uniquePoints[
                                uniquePoints.Count - 1]) <=
                            1e-6)
                        uniquePoints.RemoveAt(
                            uniquePoints.Count - 1);
                    if (uniquePoints.Count < 3)
                        throw new InvalidOperationException(
                            "The closed source has fewer than three distinct points.");

                    var vertices =
                        new Point3dCollection(
                            uniquePoints.ToArray());
                    surface.AddVertices(
                        vertices);

                    // A real outer boundary is critical at concave junctions and
                    // platforms; without it the TIN would bridge the convex hull.
                    surface.BoundariesDefinition.AddBoundaries(
                        vertices,
                        0.05,
                        SurfaceBoundaryType.Outer,
                        true);
                    surface.Rebuild();
                    transaction.Commit();
                }
                return true;
            }
            catch (System.Exception exception)
            {
                error = exception.Message;
                if (!surfaceId.IsNull)
                {
                    try { Cleanup(database, surfaceId); }
                    catch { }
                }
                surfaceId = ObjectId.Null;
                return false;
            }
        }

        private static string UniqueSurfaceName(
            Database database,
            string requested)
        {
            string baseName =
                string.IsNullOrWhiteSpace(requested)
                    ? "CE-INFILL"
                    : requested.Trim();
            var names =
                new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);
            CivilDocument civil =
                CivilApplication.ActiveDocument;
            if (civil != null &&
                database != null)
            {
                using (Transaction transaction =
                    database.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId id in
                        civil.GetSurfaceIds())
                    {
                        CivilSurface surface = null;
                        try
                        {
                            surface = transaction.GetObject(
                                id,
                                OpenMode.ForRead,
                                false) as CivilSurface;
                        }
                        catch { }
                        if (surface != null)
                            names.Add(
                                surface.Name ?? string.Empty);
                    }
                }
            }
            if (!names.Contains(baseName))
                return baseName;
            int suffix = 2;
            while (names.Contains(
                baseName + "-" +
                suffix.ToString(
                    CultureInfo.InvariantCulture)))
                suffix++;
            return baseName + "-" +
                suffix.ToString(
                    CultureInfo.InvariantCulture);
        }

        private static string ReadSurfaceName(
            Database database,
            ObjectId id)
        {
            if (database == null ||
                id.IsNull ||
                id.IsErased)
                return "<infill surface>";
            try
            {
                using (Transaction transaction =
                    database.TransactionManager.StartTransaction())
                {
                    CivilSurface surface =
                        transaction.GetObject(
                            id,
                            OpenMode.ForRead,
                            false) as CivilSurface;
                    return surface == null
                        ? id.Handle.ToString()
                        : surface.Name;
                }
            }
            catch
            {
                return id.Handle.ToString();
            }
        }

        private static void WriteNativeInfillLink(
            Database database,
            ObjectId sourceId,
            NativeInfillLink link)
        {
            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                CivilFeatureLine source =
                    OpenFeatureLine(
                        transaction,
                        sourceId,
                        OpenMode.ForWrite);
                if (source == null)
                    throw new InvalidOperationException(
                        "The junction infill source feature line is unavailable.");

                Xrecord record =
                    Record(
                        source,
                        transaction,
                        JunctionInfillKey);
                record.Data = new ResultBuffer(
                    new TypedValue(
                        (int)DxfCode.Text,
                        link.GroupHandle ?? string.Empty),
                    new TypedValue(
                        (int)DxfCode.Text,
                        link.InfillHandle ?? string.Empty),
                    new TypedValue(
                        (int)DxfCode.Text,
                        link.SiteName ?? string.Empty),
                    new TypedValue(
                        (int)DxfCode.Int16,
                        link.Created ? 1 : 0));
                transaction.Commit();
            }
        }

        private static NativeInfillLink ReadNativeInfillLink(
            Database database,
            ObjectId sourceId)
        {
            try
            {
                using (Transaction transaction =
                    database.TransactionManager.StartTransaction())
                {
                    CivilFeatureLine source =
                        OpenFeatureLine(
                            transaction,
                            sourceId,
                            OpenMode.ForRead);
                    if (source == null)
                        return null;

                    TypedValue[] values =
                        ReadRecord(
                            source,
                            transaction,
                            JunctionInfillKey);
                    if (values == null ||
                        values.Length < 4)
                        return null;

                    return new NativeInfillLink
                    {
                        GroupHandle =
                            Convert.ToString(
                                values[0].Value,
                                CultureInfo.InvariantCulture),
                        InfillHandle =
                            Convert.ToString(
                                values[1].Value,
                                CultureInfo.InvariantCulture),
                        SiteName =
                            Convert.ToString(
                                values[2].Value,
                                CultureInfo.InvariantCulture),
                        Created =
                            Convert.ToInt16(
                                values[3].Value,
                                CultureInfo.InvariantCulture) != 0
                    };
                }
            }
            catch
            {
                return null;
            }
        }

        private static void WriteGradeLink(Database database, ObjectId sourceId, GradeLink link)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                CivilFeatureLine source = OpenFeatureLine(transaction, sourceId, OpenMode.ForWrite);
                if (source == null) throw new InvalidOperationException("The grade source feature line is unavailable.");
                Xrecord record = Record(source, transaction, GradeLinkKey);
                record.Data = new ResultBuffer(
                    new TypedValue((int)DxfCode.Text, link.SurfaceHandle ?? string.Empty),
                    new TypedValue((int)DxfCode.Text, link.ChildHandle ?? string.Empty),
                    new TypedValue((int)DxfCode.Text, link.GroupHandle ?? string.Empty),
                    new TypedValue((int)DxfCode.Text, link.InfillHandle ?? string.Empty),
                    new TypedValue((int)DxfCode.Real, link.CutRatio),
                    new TypedValue((int)DxfCode.Real, link.FillRatio),
                    new TypedValue((int)DxfCode.Real, link.MaxDistance),
                    new TypedValue((int)DxfCode.Real, link.SearchStep),
                    new TypedValue((int)DxfCode.Text, link.Side ?? "Auto"),
                    new TypedValue((int)DxfCode.Int16, link.NativeInfill ? 1 : 0),
                    new TypedValue((int)DxfCode.Int16, link.ShowSlopeLines ? 1 : 0),
                    new TypedValue((int)DxfCode.Text, link.CutSlopeLayer ?? "CE-JUNCTION-CUT-SLOPES"),
                    new TypedValue((int)DxfCode.Text, link.FillSlopeLayer ?? "CE-JUNCTION-FILL-SLOPES"),
                    new TypedValue((int)DxfCode.Text, link.SlopeLineHandles ?? string.Empty),
                    new TypedValue((int)DxfCode.Real, link.SlopeLineInterval),
                    new TypedValue((int)DxfCode.Text, link.ToeLayer ?? "CE-JUNCTION-TOE"),
                    new TypedValue((int)DxfCode.Text, link.SiteName ?? "<Auto: source site / CE-PLATFORM-SITE>"),
                    new TypedValue((int)DxfCode.Text, link.SlopePatternMode ?? "Corridor-style long / short"),
                    new TypedValue((int)DxfCode.Int16, link.CutColorIndex),
                    new TypedValue((int)DxfCode.Int16, link.FillColorIndex),
                    new TypedValue((int)DxfCode.Int16, link.ToeColorIndex));
                transaction.Commit();
            }
        }

        private static GradeLink ReadGradeLink(Database database, ObjectId sourceId)
        {
            try
            {
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    CivilFeatureLine source = OpenFeatureLine(transaction, sourceId, OpenMode.ForRead);
                    GradeLink link;
                    return source != null && TryReadGradeLink(source, transaction, out link) ? link : null;
                }
            }
            catch { return null; }
        }

        private static bool TryReadGradeLink(CivilFeatureLine source, Transaction transaction, out GradeLink link)
        {
            link = null;
            TypedValue[] values = ReadRecord(source, transaction, GradeLinkKey);
            if (values == null || values.Length < 10) return false;
            try
            {
                link = new GradeLink
                {
                    SurfaceHandle = Convert.ToString(values[0].Value, CultureInfo.InvariantCulture),
                    ChildHandle = Convert.ToString(values[1].Value, CultureInfo.InvariantCulture),
                    GroupHandle = Convert.ToString(values[2].Value, CultureInfo.InvariantCulture),
                    InfillHandle = Convert.ToString(values[3].Value, CultureInfo.InvariantCulture),
                    CutRatio = Convert.ToDouble(values[4].Value, CultureInfo.InvariantCulture),
                    FillRatio = Convert.ToDouble(values[5].Value, CultureInfo.InvariantCulture),
                    MaxDistance = Convert.ToDouble(values[6].Value, CultureInfo.InvariantCulture),
                    SearchStep = Convert.ToDouble(values[7].Value, CultureInfo.InvariantCulture),
                    Side = SafeSide(Convert.ToString(values[8].Value, CultureInfo.InvariantCulture)),
                    NativeInfill = Convert.ToInt16(values[9].Value, CultureInfo.InvariantCulture) != 0,
                    ShowSlopeLines = values.Length > 10 &&
                        Convert.ToInt16(values[10].Value, CultureInfo.InvariantCulture) != 0,
                    CutSlopeLayer = values.Length > 11
                        ? Convert.ToString(values[11].Value, CultureInfo.InvariantCulture)
                        : "CE-JUNCTION-CUT-SLOPES",
                    FillSlopeLayer = values.Length > 12
                        ? Convert.ToString(values[12].Value, CultureInfo.InvariantCulture)
                        : "CE-JUNCTION-FILL-SLOPES",
                    SlopeLineHandles = values.Length > 13
                        ? Convert.ToString(values[13].Value, CultureInfo.InvariantCulture)
                        : string.Empty,
                    SlopeLineInterval = values.Length > 14
                        ? Convert.ToDouble(values[14].Value, CultureInfo.InvariantCulture)
                        : 5.0,
                    ToeLayer = values.Length > 15
                        ? Convert.ToString(values[15].Value, CultureInfo.InvariantCulture)
                        : "CE-JUNCTION-TOE",
                    SiteName = values.Length > 16
                        ? Convert.ToString(values[16].Value, CultureInfo.InvariantCulture)
                        : "<Auto: source site / CE-PLATFORM-SITE>",
                    SlopePatternMode = values.Length > 17
                        ? SafePatternMode(Convert.ToString(values[17].Value, CultureInfo.InvariantCulture))
                        : "Corridor-style long / short",
                    CutColorIndex = values.Length > 18
                        ? Convert.ToInt16(values[18].Value, CultureInfo.InvariantCulture)
                        : (short)256,
                    FillColorIndex = values.Length > 19
                        ? Convert.ToInt16(values[19].Value, CultureInfo.InvariantCulture)
                        : (short)256,
                    ToeColorIndex = values.Length > 20
                        ? Convert.ToInt16(values[20].Value, CultureInfo.InvariantCulture)
                        : (short)256
                };
                return !string.IsNullOrWhiteSpace(link.SurfaceHandle);
            }
            catch { link = null; return false; }
        }

        private static Xrecord Record(DBObject owner, Transaction transaction, string key)
        {
            if (owner.ExtensionDictionary.IsNull) owner.CreateExtensionDictionary();
            DBDictionary dictionary = transaction.GetObject(owner.ExtensionDictionary, OpenMode.ForWrite, false) as DBDictionary;
            if (dictionary == null) throw new InvalidOperationException("The feature-line extension dictionary is unavailable.");
            if (dictionary.Contains(key)) return transaction.GetObject(dictionary.GetAt(key), OpenMode.ForWrite, false) as Xrecord;
            var record = new Xrecord();
            dictionary.SetAt(key, record);
            transaction.AddNewlyCreatedDBObject(record, true);
            return record;
        }

        private static TypedValue[] ReadRecord(DBObject owner, Transaction transaction, string key)
        {
            if (owner == null || owner.ExtensionDictionary.IsNull) return null;
            DBDictionary dictionary = transaction.GetObject(owner.ExtensionDictionary, OpenMode.ForRead, false) as DBDictionary;
            if (dictionary == null || !dictionary.Contains(key)) return null;
            Xrecord record = transaction.GetObject(dictionary.GetAt(key), OpenMode.ForRead, false) as Xrecord;
            return record == null || record.Data == null ? null : record.Data.AsArray();
        }

        private static ObjectId ResolveHandle(Database database, string handleText)
        {
            if (database == null || string.IsNullOrWhiteSpace(handleText)) return ObjectId.Null;
            try
            {
                long value;
                if (!long.TryParse(handleText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)) return ObjectId.Null;
                ObjectId id = database.GetObjectId(false, new Handle(value), 0);
                return id.IsNull || id.IsErased ? ObjectId.Null : id;
            }
            catch { return ObjectId.Null; }
        }

        private static string ReadFeatureLineName(Database database, ObjectId id)
        {
            if (id.IsNull) return string.Empty;
            try
            {
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    CivilFeatureLine featureLine = OpenFeatureLine(transaction, id, OpenMode.ForRead);
                    return featureLine == null ? string.Empty : featureLine.Name;
                }
            }
            catch { return string.Empty; }
        }

        private static string UniqueFeatureLineName(Database database, string requested, ObjectId ignored)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(database), OpenMode.ForRead, false) as BlockTableRecord;
                if (space != null)
                {
                    foreach (ObjectId id in space)
                    {
                        if (id == ignored) continue;
                        CivilFeatureLine featureLine = OpenFeatureLine(transaction, id, OpenMode.ForRead);
                        if (featureLine != null && !string.IsNullOrWhiteSpace(featureLine.Name)) names.Add(featureLine.Name);
                    }
                }
            }
            string candidate = requested;
            int suffix = 2;
            while (names.Contains(candidate)) candidate = requested + " (" + (suffix++).ToString(CultureInfo.InvariantCulture) + ")";
            return candidate;
        }

        private static CivilFeatureLine OpenFeatureLine(Transaction transaction, ObjectId id, OpenMode mode)
        {
            if (id.IsNull || id.IsErased) return null;
            try { return transaction.GetObject(id, mode, false) as CivilFeatureLine; }
            catch { return null; }
        }

        private static bool Editable(CivilFeatureLine featureLine, Transaction transaction)
        {
            if (featureLine == null || featureLine.IsReferenceObject) return false;
            LayerTableRecord layer = transaction.GetObject(featureLine.LayerId, OpenMode.ForRead, false) as LayerTableRecord;
            return layer == null || !layer.IsLocked;
        }

        private static void Cleanup(Database database, ObjectId id)
        {
            if (database == null || id.IsNull || id.IsErased) return;
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

        private static Point3d NearestEndpoint(Point3dCollection points, Point3d pick)
        {
            if (points == null || points.Count == 0) return Point3d.Origin;
            Point3d first = points[0];
            Point3d last = points[points.Count - 1];
            return first.DistanceTo(pick) <= last.DistanceTo(pick) ? first : last;
        }

        private static int ClosestIndex(Point3dCollection points, Point3d target)
        {
            int best = -1;
            double distance = double.MaxValue;
            for (int index = 0; index < points.Count; index++)
            {
                double current = points[index].DistanceTo(target);
                if (current < distance) { distance = current; best = index; }
            }
            return best;
        }

        private static double SignedArea(IList<Point3d> points)
        {
            double area = 0.0;
            for (int index = 0; index < points.Count; index++)
            {
                Point3d a = points[index];
                Point3d b = points[(index + 1) % points.Count];
                area += a.X * b.Y - b.X * a.Y;
            }
            return area * 0.5;
        }

        private static Point3d Centre(IList<Point3d> points)
        {
            if (points == null || points.Count == 0) return Point3d.Origin;
            return new Point3d(points.Average(point => point.X), points.Average(point => point.Y), points.Average(point => point.Z));
        }

        private static List<string> ReadSiteNames(
            Document document)
        {
            var result = new List<string>();
            CivilDocument civilDocument =
                CivilApplication.ActiveDocument;
            if (document == null ||
                civilDocument == null)
                return result;

            try
            {
                using (Transaction transaction =
                    document.Database.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId id in
                        civilDocument.GetSiteIds())
                    {
                        DBObject site = null;
                        try
                        {
                            site =
                                transaction.GetObject(
                                    id,
                                    OpenMode.ForRead,
                                    false);
                        }
                        catch { }

                        string name =
                            ReadStringProperty(
                                site,
                                "Name");
                        if (!string.IsNullOrWhiteSpace(name))
                            result.Add(name);
                    }
                }
            }
            catch { }

            return result
                .Distinct(
                    StringComparer.OrdinalIgnoreCase)
                .OrderBy(
                    item => item,
                    StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private static string ReadStringProperty(
            object value,
            string propertyName)
        {
            if (value == null ||
                string.IsNullOrWhiteSpace(propertyName))
                return string.Empty;
            try
            {
                PropertyInfo property =
                    value.GetType()
                        .GetProperty(
                            propertyName,
                            BindingFlags.Public |
                            BindingFlags.Instance);
                if (property == null ||
                    !property.CanRead)
                    return string.Empty;
                return Convert.ToString(
                           property.GetValue(
                               value,
                               null),
                           CultureInfo.CurrentCulture) ??
                       string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static bool TrySnapToToeVertex(
            IList<Point3d> toePoints,
            Point3d target,
            out Point3d snapped)
        {
            snapped = target;
            if (toePoints == null ||
                toePoints.Count == 0)
                return false;

            double best =
                double.MaxValue;
            Point3d candidate =
                target;
            foreach (Point3d point in toePoints)
            {
                double distance =
                    point.DistanceTo(target);
                if (distance < best)
                {
                    best = distance;
                    candidate = point;
                }
            }

            // The toe and long rays are built from the same resolved sample
            // collection, so this should normally be exactly zero. The small
            // tolerance only absorbs database round-off after native feature-
            // line creation.
            if (best > 0.01)
                return false;

            snapped = candidate;
            return true;
        }

        private static Point3d InteriorSeed(
            IList<Point3d> points)
        {
            if (points == null ||
                points.Count == 0)
                return Point3d.Origin;

            Point3d centre =
                Centre(points);
            if (PointInsidePlan(
                    points,
                    centre))
                return centre;

            Point3d first =
                points[0];
            for (int division = 2;
                 division <= 16;
                 division++)
            {
                double factor =
                    1.0 / division;
                Point3d candidate =
                    new Point3d(
                        first.X +
                            (centre.X - first.X) * factor,
                        first.Y +
                            (centre.Y - first.Y) * factor,
                        first.Z +
                            (centre.Z - first.Z) * factor);
                if (PointInsidePlan(
                        points,
                        candidate))
                    return candidate;
            }

            // Civil 3D will reject an exterior seed. Returning the arithmetic
            // centre preserves the safest deterministic fallback and allows the
            // caller to report the native CreateInfill failure explicitly.
            return centre;
        }

        private static bool PointInsidePlan(
            IList<Point3d> points,
            Point3d point)
        {
            if (points == null ||
                points.Count < 3)
                return false;

            bool inside = false;
            int j =
                points.Count - 1;
            for (int i = 0;
                 i < points.Count;
                 j = i++)
            {
                Point3d a =
                    points[i];
                Point3d b =
                    points[j];
                bool crosses =
                    ((a.Y > point.Y) !=
                     (b.Y > point.Y)) &&
                    point.X <
                    (b.X - a.X) *
                    (point.Y - a.Y) /
                    (Math.Abs(b.Y - a.Y) <=
                        1e-20
                        ? 1e-20
                        : b.Y - a.Y) +
                    a.X;
                if (crosses)
                    inside = !inside;
            }
            return inside;
        }

        private static short ParseAciColor(
            string value)
        {
            if (string.IsNullOrWhiteSpace(value) ||
                string.Equals(
                    value.Trim(),
                    "ByLayer",
                    StringComparison.OrdinalIgnoreCase))
                return 256;

            short parsed;
            return short.TryParse(
                       value.Trim(),
                       NumberStyles.Integer,
                       CultureInfo.InvariantCulture,
                       out parsed) &&
                   parsed >= 1 &&
                   parsed <= 255
                ? parsed
                : (short)256;
        }

        private static short NormalizeAciColor(
            short value)
        {
            return value >= 1 &&
                   value <= 255
                ? value
                : (short)256;
        }

        private static string SafePatternMode(
            string value)
        {
            return string.Equals(
                       value,
                       "All rays full to toe",
                       StringComparison.OrdinalIgnoreCase)
                ? "All rays full to toe"
                : "Corridor-style long / short";
        }

        private static bool IsAutomaticSiteChoice(
            string value)
        {
            return string.IsNullOrWhiteSpace(value) ||
                   string.Equals(
                       value.Trim(),
                       "<Auto: source site / CE-PLATFORM-SITE>",
                       StringComparison.OrdinalIgnoreCase);
        }

        private static string SafeName(string value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        private static string SafeSide(string value)
        {
            if (string.Equals(value, "Left", StringComparison.OrdinalIgnoreCase)) return "Left";
            if (string.Equals(value, "Right", StringComparison.OrdinalIgnoreCase)) return "Right";
            if (string.Equals(value, "Inside", StringComparison.OrdinalIgnoreCase)) return "Inside";
            if (string.Equals(value, "Outside", StringComparison.OrdinalIgnoreCase)) return "Outside";
            return "Auto";
        }

        private static bool Finite(Point3d point)
        {
            return Finite(point.X) && Finite(point.Y) && Finite(point.Z);
        }

        private static bool Finite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private sealed class SurfaceOption
        {
            internal SurfaceOption(string name, ObjectId objectId) { Name = name; ObjectId = objectId; }
            internal string Name { get; private set; }
            internal ObjectId ObjectId { get; private set; }
        }

        private sealed class DirectDrapeLink
        {
            internal string SurfaceHandle { get; set; }
            internal bool Intermediate { get; set; }
        }

        private sealed class GradeLink
        {
            internal string SurfaceHandle { get; set; }
            internal string ChildHandle { get; set; }
            internal string GroupHandle { get; set; }
            internal string InfillHandle { get; set; }
            internal double CutRatio { get; set; }
            internal double FillRatio { get; set; }
            internal double MaxDistance { get; set; }
            internal double SearchStep { get; set; }
            internal string Side { get; set; }
            internal bool NativeInfill { get; set; }
            internal string SiteName { get; set; }
            internal bool ShowSlopeLines { get; set; }
            internal string SlopePatternMode { get; set; }
            internal string CutSlopeLayer { get; set; }
            internal string FillSlopeLayer { get; set; }
            internal string SlopeLineHandles { get; set; }
            internal double SlopeLineInterval { get; set; }
            internal string ToeLayer { get; set; }
            internal short CutColorIndex { get; set; }
            internal short FillColorIndex { get; set; }
            internal short ToeColorIndex { get; set; }

            internal GradeLink Clone()
            {
                return new GradeLink
                {
                    SurfaceHandle = SurfaceHandle,
                    ChildHandle = ChildHandle,
                    GroupHandle = GroupHandle,
                    InfillHandle = InfillHandle,
                    CutRatio = CutRatio,
                    FillRatio = FillRatio,
                    MaxDistance = MaxDistance,
                    SearchStep = SearchStep,
                    Side = SafeSide(Side),
                    NativeInfill = NativeInfill,
                    SiteName = SiteName,
                    ShowSlopeLines = ShowSlopeLines,
                    SlopePatternMode = SafePatternMode(SlopePatternMode),
                    CutSlopeLayer = CutSlopeLayer,
                    FillSlopeLayer = FillSlopeLayer,
                    SlopeLineHandles = SlopeLineHandles,
                    SlopeLineInterval = SlopeLineInterval,
                    ToeLayer = ToeLayer,
                    CutColorIndex = NormalizeAciColor(CutColorIndex),
                    FillColorIndex = NormalizeAciColor(FillColorIndex),
                    ToeColorIndex = NormalizeAciColor(ToeColorIndex)
                };
            }
        }

        private sealed class NativeInfillLink
        {
            internal string GroupHandle { get; set; }
            internal string InfillHandle { get; set; }
            internal string SiteName { get; set; }
            internal bool Created { get; set; }
        }

        private sealed class NativeInfillResult
        {
            internal bool Created { get; set; }
            internal bool Existing { get; set; }
            internal string Message { get; set; }
        }

        private sealed class SourceSnapshot
        {
            internal ObjectId ObjectId { get; set; }
            internal string Name { get; set; }
            internal ObjectId LayerId { get; set; }
            internal ObjectId SiteId { get; set; }
            internal string StyleName { get; set; }
            internal short ColorIndex { get; set; }
            internal bool Closed { get; set; }
            internal List<Point3d> Points { get; set; }
        }

        private sealed class GradeBuildResult
        {
            internal bool Success { get; set; }
            internal bool NativeGroupReady { get; set; }
            internal bool NativeInfillCreated { get; set; }
            internal int SlopeLinesCreated { get; set; }
            internal int CutSlopeLinesCreated { get; set; }
            internal int FillSlopeLinesCreated { get; set; }
            internal string Message { get; set; }
        }
    }
}

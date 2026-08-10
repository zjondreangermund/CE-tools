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
using CivilFeatureLine = Autodesk.Civil.DatabaseServices.FeatureLine;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

[assembly: CommandClass(typeof(CETools.Civil3D.PlatformProductionCommands))]

namespace CETools.Civil3D
{
    public sealed class PlatformProductionCommands
    {
        private const string StepKey = "CE_FLREL";
        private const string DrapeKey = "CE_PLATFORM_DRAPE";
        private const string NameKey = "CE_PLATFORM_NAME";
        private const string TableKey = "CE_PLATFORM_TABLE";
        private const string SectionKey = "CE_PLATFORM_SECTION";
        private const string PlatformLayer = "CE-PLATFORM";
        private const double Tol = 1e-7;

        [CommandMethod("CE_TOOLS", "CE_PLATFORMTOOLS", CommandFlags.Modal)]
        public void Tools()
        {
            PlatformDynamicRefreshManager.EnsureInitialized();
            Document document = ActiveDocument();
            if (document == null) return;
            DisciplineWorkflowDialogs.SelectAndRun(
                document,
                "CE Tools - Platform Production",
                "Linked platform source lines, levels, stepped offsets, surface control, setting-out, quantities and drawings.",
                new List<DisciplineWorkflowAction>
                {
                    new DisciplineWorkflowAction("Create feature lines from multiple polylines", "CE_FLCREATE", "Use the existing multi-source popup workflow and optionally assign a selected Civil 3D surface.", "01 Source"),
                    new DisciplineWorkflowAction("Apply platform slope / flatten", "CE_PLATFORMSLOPE", "Constant high-to-low plane, specified fixed slope from the highest point, or flatten to the highest elevation.", "02 Levels"),
                    new DisciplineWorkflowAction("Create stepped offsets - multiple platforms", "CE_PLATFORMSTEPOFFSETS", "Create outward linked CE_FLREL stepped offsets for multiple platform feature lines.", "03 Steps"),
                    new DisciplineWorkflowAction("Drape steps to selected surface", "CE_PLATFORMDRAPE", "Drape selected linked step lines and make the surface drive their source platform levels.", "03 Steps"),
                    new DisciplineWorkflowAction("Platform site / surface / infill", "CE_PLATFORMSURFACE", "Create/assign a platform site and surface, add feature-line breaklines and attempt Civil 3D grading infill.", "04 Surface"),
                    new DisciplineWorkflowAction("Platform setting-out", "CE_PLATFORMSETTINGOUT", "Open vertex or grid setting-out for multiple platform feature lines.", "05 Setting-out"),
                    new DisciplineWorkflowAction("Platform names", "CE_PLATFORMNAMES", "Place PLATFORM-1... labels with final elevation/range.", "06 Annotation"),
                    new DisciplineWorkflowAction("Linked platform register", "CE_PLATFORMTABLE", "Create a dynamic annotative platform area/perimeter/elevation table.", "06 Annotation"),
                    new DisciplineWorkflowAction("Platform cut / fill", "CE_PLATFORMCUTFILL", "Compare NG and design surfaces inside platform boundaries and create a linked table.", "07 Quantities"),
                    new DisciplineWorkflowAction("Platform BOQ", "CE_BOQPLATFORM", "Use the existing platform/grading BOQ workflow.", "07 Quantities"),
                    new DisciplineWorkflowAction("Platform layouts / section lines", "CE_PLATFORMDRAWINGS", "Create platform layouts and one/two centre section lines per platform.", "08 Drawings"),
                    new DisciplineWorkflowAction("Create linked dynamic sections", "CE_XSTOOLS", "Use CE dynamic cross sections on the generated section lines.", "08 Drawings"),
                    new DisciplineWorkflowAction("Platform report", "CE_REPORTPLATFORM", "Generate the existing platform design report.", "08 Drawings"),
                    new DisciplineWorkflowAction("Refresh linked platforms", "CE_PLATFORMREFRESH", "Refresh surface-driven steps, source feature lines, labels and linked tables.", "09 Maintain")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_PLATFORMSLOPE", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void Slope()
        {
            PlatformDynamicRefreshManager.EnsureInitialized();
            Document document = ActiveDocument();
            if (document == null) return;
            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Platform Levels",
                "Each selected feature line is processed independently using its current highest and lowest vertices.");
            settings.AddChoice("Mode", "Level rule", "Method", "Constant slope between highest and lowest", "Select the platform level rule.", new[]
            {
                "Constant slope between highest and lowest",
                "Fixed slope from highest towards lowest",
                "Match all vertices to highest elevation"
            });
            settings.AddDouble("Slope", "Level rule", "Fixed slope (%)", 1.0, "Used only for the fixed-slope option.");
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;
            PromptSelectionResult selection = SelectFeatureLines(document.Editor, "\nSelect multiple platform feature lines: ");
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;
            string mode = settings.Text("Mode");
            double fixedGrade = Math.Abs(settings.Double("Slope", 1.0)) / 100.0;
            int changed = 0;
            int skipped = 0;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in selection.Value.GetObjectIds())
                {
                    CivilFeatureLine featureLine = OpenFeatureLine(transaction, id, OpenMode.ForWrite);
                    if (!Editable(featureLine, transaction)) { skipped++; continue; }
                    Point3dCollection collection = featureLine.GetPoints(FeatureLinePointType.AllPoints);
                    if (collection == null || collection.Count < 2) { skipped++; continue; }
                    List<Point3d> points = collection.Cast<Point3d>().ToList();
                    Point3d high = points.OrderByDescending(p => p.Z).First();
                    Point3d low = points.OrderBy(p => p.Z).First();
                    Vector2d direction = new Vector2d(low.X - high.X, low.Y - high.Y);
                    double run = direction.Length;
                    if (run <= Tol && !string.Equals(mode, "Match all vertices to highest elevation", StringComparison.OrdinalIgnoreCase)) { skipped++; continue; }
                    if (run > Tol) direction = direction.GetNormal();
                    double naturalGrade = run <= Tol ? 0.0 : (low.Z - high.Z) / run;
                    for (int index = 0; index < collection.Count; index++)
                    {
                        Point3d point = collection[index];
                        double z;
                        if (string.Equals(mode, "Match all vertices to highest elevation", StringComparison.OrdinalIgnoreCase))
                            z = high.Z;
                        else
                        {
                            double along = new Vector2d(point.X - high.X, point.Y - high.Y).DotProduct(direction);
                            z = string.Equals(mode, "Fixed slope from highest towards lowest", StringComparison.OrdinalIgnoreCase)
                                ? high.Z - fixedGrade * Math.Max(0.0, along)
                                : high.Z + naturalGrade * along;
                        }
                        SetAbsoluteElevation(featureLine, point, index, z);
                    }
                    changed++;
                }
                transaction.Commit();
            }
            document.Editor.Regen();
            PlatformDynamicRefreshManager.Queue();
            document.Editor.WriteMessage("\nCE_PLATFORMSLOPE complete. Changed={0}; skipped={1}.", changed, skipped);
        }

        [CommandMethod("CE_TOOLS", "CE_PLATFORMSTEPOFFSETS", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void StepOffsets()
        {
            PlatformDynamicRefreshManager.EnsureInitialized();
            Document document = ActiveDocument();
            if (document == null) return;
            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Multiple Platform Stepped Offsets",
                "Closed platforms choose the outward offset side automatically. Open source feature lines use the positive offset side.");
            settings.AddPositiveDouble("Horizontal", "Steps", "Horizontal step", 1.0, "Horizontal offset per step.");
            settings.AddText("Vertical", "Steps", "Vertical step", "-0.500", "Signed vertical difference per step.");
            settings.AddPositiveInteger("Count", "Steps", "Step count", 1, "Number of linked children per source.");
            settings.AddText("Suffix", "Naming", "Child suffix", "STEP", "Used in generated feature-line names.");
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;
            double vertical;
            if (!TryParseDouble(settings.Text("Vertical"), out vertical))
            {
                document.Editor.WriteMessage("\nCE_PLATFORMSTEPOFFSETS cancelled. Enter a valid vertical step.");
                return;
            }
            PromptSelectionResult selection = SelectFeatureLines(document.Editor, "\nSelect multiple platform source feature lines: ");
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;
            double horizontal = Math.Max(0.001, settings.Double("Horizontal", 1.0));
            int count = Math.Max(1, settings.Integer("Count", 1));
            string suffix = string.IsNullOrWhiteSpace(settings.Text("Suffix")) ? "STEP" : settings.Text("Suffix").Trim();
            int created = 0;
            int skipped = 0;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = ModelSpace(document.Database, transaction, OpenMode.ForWrite);
                var names = new HashSet<string>(ReadFeatureLineNames(space, transaction), StringComparer.OrdinalIgnoreCase);
                foreach (ObjectId id in selection.Value.GetObjectIds())
                {
                    CivilFeatureLine source = OpenFeatureLine(transaction, id, OpenMode.ForRead);
                    if (!Editable(source, transaction)) { skipped++; continue; }
                    try
                    {
                        using (Polyline plan = BuildPlan(source))
                        {
                            double sign = plan.Closed ? OutwardSign(plan, horizontal) : 1.0;
                            string baseName = string.IsNullOrWhiteSpace(source.Name) ? "PLATFORM" : source.Name;
                            for (int step = 1; step <= count; step++)
                            {
                                double offset = sign * horizontal * step;
                                double dz = vertical * step;
                                string name = UniqueName(baseName + "-" + suffix + "-" + step.ToString(CultureInfo.InvariantCulture), names);
                                ObjectId childId = CreateOffsetFeatureLine(source, plan, offset, dz, name, space, transaction);
                                CivilFeatureLine child = OpenFeatureLine(transaction, childId, OpenMode.ForWrite);
                                if (child != null) WriteStep(child, transaction, new StepRelation(source.Handle.ToString(), offset, dz, step));
                                created++;
                            }
                        }
                    }
                    catch { skipped++; }
                }
                transaction.Commit();
            }
            document.Editor.Regen();
            PlatformDynamicRefreshManager.Queue();
            document.Editor.WriteMessage("\nCE_PLATFORMSTEPOFFSETS complete. Linked steps={0}; skipped={1}.", created, skipped);
        }

        [CommandMethod("CE_TOOLS", "CE_PLATFORMDRAPE", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void Drape()
        {
            PlatformDynamicRefreshManager.EnsureInitialized();
            Document document = ActiveDocument();
            if (document == null) return;
            List<SurfaceChoice> surfaces = WorkflowRepairCommands.ReadSurfaceChoices(document);
            if (surfaces.Count == 0)
            {
                document.Editor.WriteMessage("\nCE_PLATFORMDRAPE cancelled. No Civil 3D surfaces were found.");
                return;
            }
            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Drape Platform Steps",
                "The selected surface controls the draped step. The linked source platform is then updated and its other stepped offsets are rebuilt.");
            settings.AddChoice("Surface", "Surface", "Target / surveyed surface", surfaces[0].Name, "Select a controlling surface.", surfaces.Select(s => s.Name));
            settings.AddChoice("Intermediate", "Surface", "Intermediate surface points", "No", "Allow Civil 3D to add intermediate surface points.", new[] { "No", "Yes" });
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;
            SurfaceChoice surface = surfaces.FirstOrDefault(s => string.Equals(s.Name, settings.Text("Surface"), StringComparison.OrdinalIgnoreCase));
            if (surface == null) return;
            PromptSelectionResult selection = SelectFeatureLines(document.Editor, "\nSelect linked stepped-offset feature lines to drape: ");
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;
            bool intermediate = string.Equals(settings.Text("Intermediate"), "Yes", StringComparison.OrdinalIgnoreCase);
            int linked = 0;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in selection.Value.GetObjectIds())
                {
                    CivilFeatureLine child = OpenFeatureLine(transaction, id, OpenMode.ForWrite);
                    if (!Editable(child, transaction)) continue;
                    StepRelation step;
                    if (!TryReadStep(child, transaction, out step)) step = new StepRelation(child.Handle.ToString(), 0.0, 0.0, 0);
                    try
                    {
                        child.AssignElevationsFromSurface(surface.ObjectId, intermediate);
                        WriteDrape(child, transaction, new DrapeRelation
                        {
                            SourceHandle = step.SourceHandle,
                            SurfaceHandle = surface.ObjectId.Handle.ToString(),
                            VerticalOffset = step.VerticalOffset,
                            Sequence = step.Sequence,
                            Intermediate = intermediate
                        });
                        linked++;
                    }
                    catch { }
                }
                transaction.Commit();
            }
            RefreshAll(document);
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_PLATFORMDRAPE complete. Dynamic surface links={0}.", linked);
        }

        [CommandMethod("CE_TOOLS", "CE_PLATFORMSURFACE", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void SurfaceAndInfill()
        {
            PlatformDynamicRefreshManager.EnsureInitialized();
            Document document = ActiveDocument();
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (document == null || civilDocument == null) return;
            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Platform Site / Surface / Infill",
                "Assign multiple closed platform feature lines to one site, build a separate TIN surface from them and attempt native grading infill at each platform centre.");
            settings.AddChoice("Scope", "Platforms", "Scope", "Selected", "Process selected closed feature lines or all platform-layer closed feature lines.", new[] { "Selected", "All" });
            settings.AddText("Site", "Civil 3D", "Site name", "CE-PLATFORM-SITE", "Platform site name.");
            settings.AddText("Surface", "Civil 3D", "Surface name", "CE-PLATFORM-SURFACE", "Separate platform surface name.");
            settings.AddChoice("Infill", "Grading", "Create infill", "Yes", "Attempt Civil 3D grading infill for every closed platform.", new[] { "Yes", "No" });
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;
            List<ObjectId> platforms = ResolvePlatforms(document, settings.Text("Scope"));
            if (platforms.Count == 0) return;
            string siteName = Safe(settings.Text("Site"), "CE-PLATFORM-SITE");
            string surfaceName = Safe(settings.Text("Surface"), "CE-PLATFORM-SURFACE");
            ObjectId siteId = EnsureNamedCivilObject(document.Database, civilDocument, "Autodesk.Civil.DatabaseServices.Site", "GetSiteIds", siteName, ObjectId.Null);
            ObjectId surfaceId = EnsureNamedCivilObject(document.Database, civilDocument, "Autodesk.Civil.DatabaseServices.TinSurface", "GetSurfaceIds", surfaceName, ObjectId.Null);
            int siteAssigned = 0;
            int breaklines = 0;
            int infills = 0;
            int warnings = 0;
            if (!siteId.IsNull)
            {
                foreach (ObjectId id in platforms)
                {
                    if (MoveToSite(id, siteId)) siteAssigned++; else warnings++;
                }
            }
            else warnings++;
            if (!surfaceId.IsNull)
            {
                if (AddBreaklines(document, surfaceId, platforms)) breaklines = platforms.Count; else warnings++;
            }
            else warnings++;
            if (string.Equals(settings.Text("Infill"), "Yes", StringComparison.OrdinalIgnoreCase) && !siteId.IsNull)
            {
                ObjectId groupId = CreateGradingGroup(document.Database, civilDocument, siteId, surfaceName + "-GRADING", surfaceId);
                if (!groupId.IsNull)
                {
                    using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                    {
                        foreach (ObjectId id in platforms)
                        {
                            CivilFeatureLine featureLine = OpenFeatureLine(transaction, id, OpenMode.ForRead);
                            if (featureLine != null && featureLine.Closed && CreateInfill(groupId, Centre(featureLine))) infills++;
                        }
                    }
                }
                else warnings++;
            }
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_PLATFORMSURFACE complete. Site assignments={0}; breaklines={1}; infills={2}; host-API warnings={3}.", siteAssigned, breaklines, infills, warnings);
        }

        [CommandMethod("CE_TOOLS", "CE_PLATFORMSETTINGOUT", CommandFlags.Modal)]
        public void SettingOut()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            DisciplineWorkflowDialogs.SelectAndRun(
                document,
                "CE Tools - Platform Setting-Out",
                "Choose linked multi-feature-line vertex setting-out or grid setting-out.",
                new List<DisciplineWorkflowAction>
                {
                    new DisciplineWorkflowAction("Vertex setting-out", "CE_VERTEXSETTINGOUT", "Multiple feature lines, sequence options, COGO/MText/MLeader and linked table.", "01 Vertex"),
                    new DisciplineWorkflowAction("Grid setting-out", "CE_GRIDSETTINGOUT", "Grid/perimeter setting-out inside a platform boundary.", "02 Grid"),
                    new DisciplineWorkflowAction("Refresh vertex setting-out", "CE_VERTEXSETTINGOUTREFRESH", "Refresh linked setting-out outputs.", "03 Maintain")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_PLATFORMNAMES", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void Names()
        {
            PlatformDynamicRefreshManager.EnsureInitialized();
            Document document = ActiveDocument();
            if (document == null) return;
            var settings = new ProductionSettingsDialogModel("CE Tools - Platform Names", "Place linked platform labels at platform centres with final platform level/range.");
            settings.AddChoice("Scope", "Platforms", "Scope", "Selected", "Selected or all platform feature lines.", new[] { "Selected", "All" });
            settings.AddText("Prefix", "Naming", "Prefix", "PLATFORM", "PLATFORM-1, PLATFORM-2, etc.");
            settings.AddPositiveInteger("Start", "Naming", "Start number", 1, "First platform number.");
            settings.AddDouble("Height", "Annotation", "Paper text height", 2.5, "Annotative paper height.");
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;
            List<ObjectId> platforms = ResolvePlatforms(document, settings.Text("Scope"));
            if (platforms.Count == 0) return;
            string prefix = Safe(settings.Text("Prefix"), "PLATFORM");
            int start = settings.Integer("Start", 1);
            double height = PaperAnnotationScale.ModelTextHeight(document.Database, Math.Max(0.5, settings.Double("Height", 2.5)));
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = ModelSpace(document.Database, transaction, OpenMode.ForWrite);
                ObjectId layer = Layer(document.Database, transaction, "CE-PLATFORM-LABEL");
                List<CivilFeatureLine> featureLines = platforms.Select(id => OpenFeatureLine(transaction, id, OpenMode.ForRead)).Where(fl => fl != null && fl.Closed)
                    .OrderByDescending(fl => Centre(fl).Y).ThenBy(fl => Centre(fl).X).ToList();
                for (int index = 0; index < featureLines.Count; index++)
                {
                    CivilFeatureLine featureLine = featureLines[index];
                    EraseName(space, transaction, featureLine.Handle.ToString());
                    string label = prefix + "-" + (start + index).ToString(CultureInfo.InvariantCulture);
                    var text = new MText();
                    text.SetDatabaseDefaults(document.Database);
                    text.LayerId = layer;
                    text.Location = Centre(featureLine);
                    text.Attachment = AttachmentPoint.MiddleCenter;
                    text.TextHeight = height;
                    text.Contents = LabelText(label, featureLine);
                    PaperAnnotationScale.SetAnnotative(text);
                    space.AppendEntity(text);
                    transaction.AddNewlyCreatedDBObject(text, true);
                    WriteName(text, transaction, featureLine.Handle.ToString(), label);
                }
                transaction.Commit();
            }
            document.Editor.Regen();
            PlatformDynamicRefreshManager.Queue();
        }

        [CommandMethod("CE_TOOLS", "CE_PLATFORMTABLE", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void RegisterTable()
        {
            PlatformDynamicRefreshManager.EnsureInitialized();
            Document document = ActiveDocument();
            if (document == null) return;
            var settings = new ProductionSettingsDialogModel("CE Tools - Linked Platform Register", "Create a linked annotative platform area/perimeter/elevation table.");
            settings.AddChoice("Scope", "Platforms", "Scope", "Selected", "Selected or all platforms.", new[] { "Selected", "All" });
            settings.AddDouble("Height", "Table", "Paper text height", 2.0, "Annotative table height.");
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;
            List<ObjectId> platforms = ResolvePlatforms(document, settings.Text("Scope"));
            if (platforms.Count == 0) return;
            PromptPointResult insertion = document.Editor.GetPoint("\nPick insertion point for the linked platform table: ");
            if (insertion.Status != PromptStatus.OK) return;
            double height = PaperAnnotationScale.ModelTextHeight(document.Database, Math.Max(0.5, settings.Double("Height", 2.0)));
            CreateTable(document, insertion.Value, new TableLink("REGISTER", platforms.Select(id => id.Handle.ToString()).ToList(), string.Empty, string.Empty, 0.0, height));
        }

        [CommandMethod("CE_TOOLS", "CE_PLATFORMCUTFILL", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void CutFill()
        {
            PlatformDynamicRefreshManager.EnsureInitialized();
            Document document = ActiveDocument();
            if (document == null) return;
            List<SurfaceChoice> surfaces = WorkflowRepairCommands.ReadSurfaceChoices(document);
            if (surfaces.Count < 2)
            {
                document.Editor.WriteMessage("\nCE_PLATFORMCUTFILL cancelled. At least two surfaces are required.");
                return;
            }
            var settings = new ProductionSettingsDialogModel("CE Tools - Platform Cut / Fill", "Linked grid integration between a selected NG/base surface and design/comparison surface inside platform boundaries.");
            settings.AddChoice("Scope", "Platforms", "Scope", "Selected", "Selected or all platforms.", new[] { "Selected", "All" });
            settings.AddChoice("NG", "Surfaces", "NG / base surface", surfaces[0].Name, "Existing/base surface.", surfaces.Select(s => s.Name));
            settings.AddChoice("Design", "Surfaces", "Design / comparison surface", surfaces[1].Name, "Design surface.", surfaces.Select(s => s.Name));
            settings.AddPositiveDouble("Grid", "Quantities", "Grid spacing", 1.0, "Sampling grid; automatically coarsened above 250000 candidate samples.");
            settings.AddDouble("Height", "Table", "Paper text height", 2.0, "Annotative table height.");
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;
            SurfaceChoice ng = surfaces.FirstOrDefault(s => string.Equals(s.Name, settings.Text("NG"), StringComparison.OrdinalIgnoreCase));
            SurfaceChoice design = surfaces.FirstOrDefault(s => string.Equals(s.Name, settings.Text("Design"), StringComparison.OrdinalIgnoreCase));
            if (ng == null || design == null || ng.ObjectId == design.ObjectId) return;
            List<ObjectId> platforms = ResolvePlatforms(document, settings.Text("Scope"));
            if (platforms.Count == 0) return;
            PromptPointResult insertion = document.Editor.GetPoint("\nPick insertion point for the linked cut/fill table: ");
            if (insertion.Status != PromptStatus.OK) return;
            double height = PaperAnnotationScale.ModelTextHeight(document.Database, Math.Max(0.5, settings.Double("Height", 2.0)));
            CreateTable(document, insertion.Value, new TableLink("CUTFILL", platforms.Select(id => id.Handle.ToString()).ToList(), ng.ObjectId.Handle.ToString(), design.ObjectId.Handle.ToString(), Math.Max(0.01, settings.Double("Grid", 1.0)), height));
        }

        [CommandMethod("CE_TOOLS", "CE_PLATFORMDRAWINGS", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void Drawings()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            var settings = new ProductionSettingsDialogModel("CE Tools - Platform Drawings", "Create platform layouts plus one/two model-space centre section lines compatible with CE_XSCREATE.");
            settings.AddChoice("Scope", "Platforms", "Scope", "Selected", "Selected or all platforms.", new[] { "Selected", "All" });
            settings.AddChoice("Sections", "Sections", "Section lines", "Two axes", "One longest-axis or two orthogonal centre lines.", new[] { "One axis", "Two axes" });
            settings.AddChoice("Layouts", "Drawings", "Create layouts", "Yes", "Create CE-PLATFORM-xx layouts if missing.", new[] { "Yes", "No" });
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;
            List<ObjectId> platforms = ResolvePlatforms(document, settings.Text("Scope"));
            if (platforms.Count == 0) return;
            bool two = string.Equals(settings.Text("Sections"), "Two axes", StringComparison.OrdinalIgnoreCase);
            bool layouts = string.Equals(settings.Text("Layouts"), "Yes", StringComparison.OrdinalIgnoreCase);
            int lines = 0;
            int createdLayouts = 0;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = ModelSpace(document.Database, transaction, OpenMode.ForWrite);
                ObjectId layer = Layer(document.Database, transaction, "CE-PLATFORM-SECTION-LINE");
                List<CivilFeatureLine> featureLines = platforms.Select(id => OpenFeatureLine(transaction, id, OpenMode.ForRead)).Where(fl => fl != null && fl.Closed)
                    .OrderByDescending(fl => Centre(fl).Y).ThenBy(fl => Centre(fl).X).ToList();
                for (int index = 0; index < featureLines.Count; index++)
                {
                    CivilFeatureLine featureLine = featureLines[index];
                    Extents3d extents = featureLine.GeometricExtents;
                    Point3d centre = Centre(featureLine);
                    double width = extents.MaxPoint.X - extents.MinPoint.X;
                    double depth = extents.MaxPoint.Y - extents.MinPoint.Y;
                    double pad = Math.Max(Math.Max(width, depth) * 0.10, 1.0);
                    var generated = new List<Line>();
                    if (width >= depth)
                    {
                        generated.Add(new Line(new Point3d(extents.MinPoint.X - pad, centre.Y, centre.Z), new Point3d(extents.MaxPoint.X + pad, centre.Y, centre.Z)));
                        if (two) generated.Add(new Line(new Point3d(centre.X, extents.MinPoint.Y - pad, centre.Z), new Point3d(centre.X, extents.MaxPoint.Y + pad, centre.Z)));
                    }
                    else
                    {
                        generated.Add(new Line(new Point3d(centre.X, extents.MinPoint.Y - pad, centre.Z), new Point3d(centre.X, extents.MaxPoint.Y + pad, centre.Z)));
                        if (two) generated.Add(new Line(new Point3d(extents.MinPoint.X - pad, centre.Y, centre.Z), new Point3d(extents.MaxPoint.X + pad, centre.Y, centre.Z)));
                    }
                    foreach (Line line in generated)
                    {
                        line.SetDatabaseDefaults(document.Database);
                        line.LayerId = layer;
                        space.AppendEntity(line);
                        transaction.AddNewlyCreatedDBObject(line, true);
                        Xrecord record = Record(line, transaction, SectionKey);
                        record.Data = new ResultBuffer(new TypedValue((int)DxfCode.Text, featureLine.Handle.ToString()));
                        lines++;
                    }
                    if (layouts && EnsureLayout(document.Database, transaction, "CE-PLATFORM-" + (index + 1).ToString("00", CultureInfo.InvariantCulture), "PLATFORM-" + (index + 1).ToString(CultureInfo.InvariantCulture))) createdLayouts++;
                }
                transaction.Commit();
            }
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_PLATFORMDRAWINGS complete. Section lines={0}; new layouts={1}. Run CE_XSCREATE on generated section lines.", lines, createdLayouts);
        }

        [CommandMethod("CE_TOOLS", "CE_PLATFORMREFRESH", CommandFlags.Modal | CommandFlags.Redraw)]
        public void Refresh()
        {
            PlatformDynamicRefreshManager.EnsureInitialized();
            Document document = ActiveDocument();
            if (document == null) return;
            int count = RefreshAll(document);
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_PLATFORMREFRESH complete. Refreshed linked platform items={0}.", count);
        }

        internal static int RefreshAll(Document document)
        {
            if (document == null) return 0;
            int refreshed = 0;
            List<DrapeSnapshot> snapshots = ReadDrapes(document.Database);
            if (snapshots.Count > 0)
            {
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    foreach (DrapeSnapshot snapshot in snapshots)
                    {
                        CivilFeatureLine child = OpenFeatureLine(transaction, Resolve(document.Database, snapshot.ChildHandle), OpenMode.ForWrite);
                        CivilFeatureLine source = OpenFeatureLine(transaction, Resolve(document.Database, snapshot.Link.SourceHandle), OpenMode.ForWrite);
                        ObjectId surfaceId = Resolve(document.Database, snapshot.Link.SurfaceHandle);
                        if (child == null || source == null || surfaceId.IsNull) continue;
                        CivilSurface surface = transaction.GetObject(surfaceId, OpenMode.ForRead, false) as CivilSurface;
                        if (surface == null) continue;
                        try
                        {
                            child.AssignElevationsFromSurface(surfaceId, snapshot.Link.Intermediate);
                            if (child.ObjectId != source.ObjectId)
                            {
                                Point3dCollection sourcePoints = source.GetPoints(FeatureLinePointType.AllPoints);
                                for (int index = 0; index < sourcePoints.Count; index++)
                                {
                                    Point3d sourcePoint = sourcePoints[index];
                                    Point3d nearest = child.GetClosestPointTo(new Point3d(sourcePoint.X, sourcePoint.Y, 0.0), Vector3d.ZAxis, false);
                                    SetAbsoluteElevation(source, sourcePoint, index, nearest.Z - snapshot.Link.VerticalOffset);
                                }
                            }
                            refreshed++;
                        }
                        catch { }
                    }
                    transaction.Commit();
                }
                try { FeatureLineRelativeCommands.RefreshAll(document); } catch { }
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    BlockTableRecord space = ModelSpace(document.Database, transaction, OpenMode.ForRead);
                    foreach (DrapeSnapshot snapshot in snapshots)
                    {
                        CivilFeatureLine rebuilt = FindStep(space, transaction, snapshot.Link.SourceHandle, snapshot.Link.Sequence);
                        ObjectId surfaceId = Resolve(document.Database, snapshot.Link.SurfaceHandle);
                        if (rebuilt == null || surfaceId.IsNull) continue;
                        try
                        {
                            rebuilt.UpgradeOpen();
                            rebuilt.AssignElevationsFromSurface(surfaceId, snapshot.Link.Intermediate);
                            WriteDrape(rebuilt, transaction, snapshot.Link);
                        }
                        catch { }
                    }
                    transaction.Commit();
                }
            }

            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = ModelSpace(document.Database, transaction, OpenMode.ForRead);
                foreach (ObjectId id in space.Cast<ObjectId>().ToList())
                {
                    MText text = transaction.GetObject(id, OpenMode.ForRead, false) as MText;
                    string sourceHandle;
                    string label;
                    if (text != null && TryReadName(text, transaction, out sourceHandle, out label))
                    {
                        CivilFeatureLine source = OpenFeatureLine(transaction, Resolve(document.Database, sourceHandle), OpenMode.ForRead);
                        if (source != null)
                        {
                            text.UpgradeOpen();
                            text.Location = Centre(source);
                            text.Contents = LabelText(label, source);
                            refreshed++;
                        }
                        continue;
                    }
                    Table table = transaction.GetObject(id, OpenMode.ForRead, false) as Table;
                    TableLink link;
                    if (table != null && TryReadTable(table, transaction, out link))
                    {
                        table.UpgradeOpen();
                        PopulateTable(document.Database, transaction, table, link);
                        refreshed++;
                    }
                }
                transaction.Commit();
            }
            return refreshed;
        }

        private static void CreateTable(Document document, Point3d insertion, TableLink link)
        {
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = ModelSpace(document.Database, transaction, OpenMode.ForWrite);
                var table = new Table();
                table.SetDatabaseDefaults(document.Database);
                table.TableStyle = document.Database.Tablestyle;
                table.Position = insertion;
                PaperAnnotationScale.SetAnnotative(table);
                space.AppendEntity(table);
                transaction.AddNewlyCreatedDBObject(table, true);
                WriteTable(table, transaction, link);
                PopulateTable(document.Database, transaction, table, link);
                transaction.Commit();
            }
            document.Editor.Regen();
        }

        private static void PopulateTable(Database database, Transaction transaction, Table table, TableLink link)
        {
            if (string.Equals(link.Type, "CUTFILL", StringComparison.OrdinalIgnoreCase)) PopulateCutFill(database, transaction, table, link);
            else PopulateRegister(database, transaction, table, link);
        }

        private static void PopulateRegister(Database database, Transaction transaction, Table table, TableLink link)
        {
            var rows = new List<PlatformRow>();
            int sequence = 1;
            foreach (string handle in link.SourceHandles)
            {
                CivilFeatureLine featureLine = OpenFeatureLine(transaction, Resolve(database, handle), OpenMode.ForRead);
                if (featureLine == null || !featureLine.Closed) continue;
                using (Polyline plan = BuildPlan(featureLine))
                {
                    rows.Add(new PlatformRow
                    {
                        Name = FindName(database, transaction, handle) ?? "PLATFORM-" + sequence.ToString(CultureInfo.InvariantCulture),
                        Area = Math.Abs(plan.Area),
                        Perimeter = plan.Length,
                        Min = featureLine.MinElevation,
                        Max = featureLine.MaxElevation
                    });
                }
                sequence++;
            }
            table.SetSize(rows.Count + 2, 5);
            table.SetRowHeight(Math.Max(link.TextHeight * 1.8, 0.001));
            table.SetColumnWidth(Math.Max(link.TextHeight * 10.0, 0.001));
            table.Cells[0, 0].TextString = "CE LINKED PLATFORM REGISTER";
            table.MergeCells(CellRange.Create(table, 0, 0, 0, 4));
            string[] headers = { "PLATFORM", "AREA", "PERIMETER", "MIN LEVEL", "MAX / FINAL LEVEL" };
            for (int column = 0; column < headers.Length; column++) table.Cells[1, column].TextString = headers[column];
            for (int index = 0; index < rows.Count; index++)
            {
                int row = index + 2;
                table.Cells[row, 0].TextString = rows[index].Name;
                table.Cells[row, 1].TextString = rows[index].Area.ToString("N2", CultureInfo.CurrentCulture);
                table.Cells[row, 2].TextString = rows[index].Perimeter.ToString("N2", CultureInfo.CurrentCulture);
                table.Cells[row, 3].TextString = rows[index].Min.ToString("N3", CultureInfo.CurrentCulture);
                table.Cells[row, 4].TextString = rows[index].Max.ToString("N3", CultureInfo.CurrentCulture);
            }
            Format(table, link.TextHeight);
        }

        private static void PopulateCutFill(Database database, Transaction transaction, Table table, TableLink link)
        {
            CivilSurface ng = transaction.GetObject(Resolve(database, link.NgHandle), OpenMode.ForRead, false) as CivilSurface;
            CivilSurface design = transaction.GetObject(Resolve(database, link.DesignHandle), OpenMode.ForRead, false) as CivilSurface;
            var rows = new List<CutFillRow>();
            int sequence = 1;
            if (ng != null && design != null)
            {
                foreach (string handle in link.SourceHandles)
                {
                    CivilFeatureLine featureLine = OpenFeatureLine(transaction, Resolve(database, handle), OpenMode.ForRead);
                    if (featureLine == null || !featureLine.Closed) continue;
                    using (Polyline plan = BuildPlan(featureLine))
                    {
                        string name = FindName(database, transaction, handle) ?? "PLATFORM-" + sequence.ToString(CultureInfo.InvariantCulture);
                        rows.Add(Calculate(plan, ng, design, name, link.Grid));
                    }
                    sequence++;
                }
            }
            table.SetSize(rows.Count + 3, 7);
            table.SetRowHeight(Math.Max(link.TextHeight * 1.8, 0.001));
            table.SetColumnWidth(Math.Max(link.TextHeight * 10.0, 0.001));
            table.Cells[0, 0].TextString = "CE LINKED PLATFORM CUT / FILL";
            table.MergeCells(CellRange.Create(table, 0, 0, 0, 6));
            table.Cells[1, 0].TextString = "Grid surface integration: design above NG = fill; NG above design = cut.";
            table.MergeCells(CellRange.Create(table, 1, 0, 1, 6));
            string[] headers = { "PLATFORM", "SAMPLED AREA", "CUT", "FILL", "NET FILL-CUT", "GRID", "SAMPLES" };
            for (int column = 0; column < headers.Length; column++) table.Cells[2, column].TextString = headers[column];
            for (int index = 0; index < rows.Count; index++)
            {
                int row = index + 3;
                CutFillRow value = rows[index];
                table.Cells[row, 0].TextString = value.Name;
                table.Cells[row, 1].TextString = value.Area.ToString("N2", CultureInfo.CurrentCulture);
                table.Cells[row, 2].TextString = value.Cut.ToString("N2", CultureInfo.CurrentCulture);
                table.Cells[row, 3].TextString = value.Fill.ToString("N2", CultureInfo.CurrentCulture);
                table.Cells[row, 4].TextString = (value.Fill - value.Cut).ToString("+0.00;-0.00;0.00", CultureInfo.CurrentCulture);
                table.Cells[row, 5].TextString = value.Grid.ToString("N2", CultureInfo.CurrentCulture);
                table.Cells[row, 6].TextString = value.Samples.ToString(CultureInfo.InvariantCulture);
            }
            Format(table, link.TextHeight);
        }

        private static CutFillRow Calculate(Polyline boundary, CivilSurface ng, CivilSurface design, string name, double requestedGrid)
        {
            Extents3d extents = boundary.GeometricExtents;
            double grid = Math.Max(0.01, requestedGrid);
            double width = Math.Max(0.0, extents.MaxPoint.X - extents.MinPoint.X);
            double depth = Math.Max(0.0, extents.MaxPoint.Y - extents.MinPoint.Y);
            double candidates = Math.Max(1.0, Math.Ceiling(width / grid) * Math.Ceiling(depth / grid));
            if (candidates > 250000.0) grid *= Math.Sqrt(candidates / 250000.0);
            double area = 0.0;
            double cut = 0.0;
            double fill = 0.0;
            int samples = 0;
            double cell = grid * grid;
            for (double x = extents.MinPoint.X + grid * 0.5; x <= extents.MaxPoint.X; x += grid)
            {
                for (double y = extents.MinPoint.Y + grid * 0.5; y <= extents.MaxPoint.Y; y += grid)
                {
                    Point3d point = new Point3d(x, y, 0.0);
                    if (!Inside(boundary, point)) continue;
                    double oldLevel;
                    double newLevel;
                    try { oldLevel = ng.FindElevationAtXY(x, y); newLevel = design.FindElevationAtXY(x, y); }
                    catch { continue; }
                    if (double.IsNaN(oldLevel) || double.IsInfinity(oldLevel) || double.IsNaN(newLevel) || double.IsInfinity(newLevel)) continue;
                    double difference = newLevel - oldLevel;
                    if (difference >= 0.0) fill += difference * cell; else cut += -difference * cell;
                    area += cell;
                    samples++;
                }
            }
            return new CutFillRow(name, area, cut, fill, grid, samples);
        }

        private static void Format(Table table, double height)
        {
            for (int row = 0; row < table.Rows.Count; row++)
                for (int column = 0; column < table.Columns.Count; column++)
                {
                    table.Cells[row, column].Alignment = CellAlignment.MiddleCenter;
                    table.Cells[row, column].TextHeight = height;
                }
            try { table.GenerateLayout(); } catch { }
            try { table.RecordGraphicsModified(true); } catch { }
        }

        private static ObjectId CreateOffsetFeatureLine(CivilFeatureLine source, Polyline plan, double offset, double dz, string name, BlockTableRecord space, Transaction transaction)
        {
            DBObjectCollection collection = plan.GetOffsetCurves(offset);
            if (collection == null || collection.Count != 1)
            {
                Dispose(collection);
                throw new InvalidOperationException("Offset generated multiple/no curves.");
            }
            Curve curve = collection[0] as Curve;
            if (curve == null) { Dispose(collection); throw new InvalidOperationException("Offset is not a usable curve."); }
            curve.SetDatabaseDefaults(source.Database);
            curve.LayerId = source.LayerId;
            space.AppendEntity(curve);
            transaction.AddNewlyCreatedDBObject(curve, true);
            ObjectId id = source.SiteId.IsNull ? CivilFeatureLine.Create(name, curve.ObjectId) : CivilFeatureLine.Create(name, curve.ObjectId, source.SiteId);
            CivilFeatureLine child = OpenFeatureLine(transaction, id, OpenMode.ForWrite);
            if (child == null) throw new InvalidOperationException("Civil 3D did not return the feature line.");
            child.LayerId = source.LayerId;
            if (!string.IsNullOrWhiteSpace(source.StyleName)) child.StyleName = source.StyleName;
            Point3dCollection points = child.GetPoints(FeatureLinePointType.AllPoints);
            for (int index = 0; index < points.Count; index++)
            {
                Point3d point = points[index];
                Point3d sourcePoint = source.GetClosestPointTo(new Point3d(point.X, point.Y, 0.0), Vector3d.ZAxis, false);
                child.SetPointElevation(index, sourcePoint.Z + dz);
            }
            if (!curve.IsErased) curve.Erase();
            return id;
        }

        private static Polyline BuildPlan(CivilFeatureLine source)
        {
            Point3dCollection collection = source.GetPoints(FeatureLinePointType.PIPoint);
            if (collection == null || collection.Count < 2) throw new InvalidOperationException("At least two PI points are required.");
            List<Point3d> points = collection.Cast<Point3d>().ToList();
            if (source.Closed && points.Count > 2 && PlanDistance(points[0], points[points.Count - 1]) <= Tol) points.RemoveAt(points.Count - 1);
            var plan = new Polyline(points.Count) { Closed = source.Closed, Elevation = 0.0, Normal = Vector3d.ZAxis };
            int segments = source.Closed ? points.Count : points.Count - 1;
            for (int index = 0; index < points.Count; index++)
                plan.AddVertexAt(index, new Point2d(points[index].X, points[index].Y), index < segments ? source.GetBulge(index) : 0.0, 0.0, 0.0);
            return plan;
        }

        private static double OutwardSign(Polyline plan, double distance)
        {
            double positive = OffsetArea(plan, distance);
            double negative = OffsetArea(plan, -distance);
            if (double.IsNaN(positive)) return double.IsNaN(negative) ? 1.0 : -1.0;
            if (double.IsNaN(negative)) return 1.0;
            return positive >= negative ? 1.0 : -1.0;
        }

        private static double OffsetArea(Polyline plan, double offset)
        {
            DBObjectCollection values = null;
            try
            {
                values = plan.GetOffsetCurves(offset);
                if (values == null || values.Count != 1) return double.NaN;
                Polyline polyline = values[0] as Polyline;
                return polyline == null || !polyline.Closed ? double.NaN : Math.Abs(polyline.Area);
            }
            catch { return double.NaN; }
            finally { Dispose(values); }
        }

        private static void SetAbsoluteElevation(CivilFeatureLine featureLine, Point3d point, int index, double elevation)
        {
            if (featureLine.IsElevationRelativeToSurface(point)) featureLine.SetPointRelativeElevation(point, false, elevation);
            else featureLine.SetPointElevation(index, elevation);
        }

        private static void WriteStep(CivilFeatureLine child, Transaction transaction, StepRelation relation)
        {
            Record(child, transaction, StepKey).Data = new ResultBuffer(
                new TypedValue((int)DxfCode.Text, relation.SourceHandle),
                new TypedValue((int)DxfCode.Real, relation.HorizontalOffset),
                new TypedValue((int)DxfCode.Real, relation.VerticalOffset),
                new TypedValue((int)DxfCode.Int32, relation.Sequence));
        }

        private static bool TryReadStep(CivilFeatureLine child, Transaction transaction, out StepRelation relation)
        {
            relation = null;
            TypedValue[] values = Read(child, transaction, StepKey);
            if (values == null || values.Length < 4) return false;
            relation = new StepRelation(Convert.ToString(values[0].Value, CultureInfo.InvariantCulture), Convert.ToDouble(values[1].Value, CultureInfo.InvariantCulture), Convert.ToDouble(values[2].Value, CultureInfo.InvariantCulture), Convert.ToInt32(values[3].Value, CultureInfo.InvariantCulture));
            return !string.IsNullOrWhiteSpace(relation.SourceHandle);
        }

        private static void WriteDrape(CivilFeatureLine child, Transaction transaction, DrapeRelation relation)
        {
            Record(child, transaction, DrapeKey).Data = new ResultBuffer(
                new TypedValue((int)DxfCode.Text, relation.SourceHandle ?? string.Empty),
                new TypedValue((int)DxfCode.Text, relation.SurfaceHandle ?? string.Empty),
                new TypedValue((int)DxfCode.Real, relation.VerticalOffset),
                new TypedValue((int)DxfCode.Int32, relation.Sequence),
                new TypedValue((int)DxfCode.Int16, relation.Intermediate ? 1 : 0));
        }

        private static bool TryReadDrape(CivilFeatureLine child, Transaction transaction, out DrapeRelation relation)
        {
            relation = null;
            TypedValue[] values = Read(child, transaction, DrapeKey);
            if (values == null || values.Length < 5) return false;
            relation = new DrapeRelation
            {
                SourceHandle = Convert.ToString(values[0].Value, CultureInfo.InvariantCulture),
                SurfaceHandle = Convert.ToString(values[1].Value, CultureInfo.InvariantCulture),
                VerticalOffset = Convert.ToDouble(values[2].Value, CultureInfo.InvariantCulture),
                Sequence = Convert.ToInt32(values[3].Value, CultureInfo.InvariantCulture),
                Intermediate = Convert.ToInt16(values[4].Value, CultureInfo.InvariantCulture) != 0
            };
            return !string.IsNullOrWhiteSpace(relation.SourceHandle) && !string.IsNullOrWhiteSpace(relation.SurfaceHandle);
        }

        private static List<DrapeSnapshot> ReadDrapes(Database database)
        {
            var result = new List<DrapeSnapshot>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = ModelSpace(database, transaction, OpenMode.ForRead);
                foreach (ObjectId id in space)
                {
                    CivilFeatureLine child = OpenFeatureLine(transaction, id, OpenMode.ForRead);
                    DrapeRelation relation;
                    if (child != null && TryReadDrape(child, transaction, out relation)) result.Add(new DrapeSnapshot(child.Handle.ToString(), relation));
                }
            }
            return result;
        }

        private static CivilFeatureLine FindStep(BlockTableRecord space, Transaction transaction, string sourceHandle, int sequence)
        {
            foreach (ObjectId id in space)
            {
                CivilFeatureLine child = OpenFeatureLine(transaction, id, OpenMode.ForRead);
                StepRelation relation;
                if (child != null && TryReadStep(child, transaction, out relation) && string.Equals(relation.SourceHandle, sourceHandle, StringComparison.OrdinalIgnoreCase) && relation.Sequence == sequence) return child;
            }
            return null;
        }

        private static void WriteName(MText text, Transaction transaction, string sourceHandle, string label)
        {
            Record(text, transaction, NameKey).Data = new ResultBuffer(new TypedValue((int)DxfCode.Text, sourceHandle), new TypedValue((int)DxfCode.Text, label));
        }

        private static bool TryReadName(MText text, Transaction transaction, out string sourceHandle, out string label)
        {
            sourceHandle = string.Empty; label = string.Empty;
            TypedValue[] values = Read(text, transaction, NameKey);
            if (values == null || values.Length < 2) return false;
            sourceHandle = Convert.ToString(values[0].Value, CultureInfo.InvariantCulture);
            label = Convert.ToString(values[1].Value, CultureInfo.InvariantCulture);
            return !string.IsNullOrWhiteSpace(sourceHandle);
        }

        private static void WriteTable(Table table, Transaction transaction, TableLink link)
        {
            Record(table, transaction, TableKey).Data = new ResultBuffer(
                new TypedValue((int)DxfCode.Text, link.Type),
                new TypedValue((int)DxfCode.Text, string.Join(",", link.SourceHandles)),
                new TypedValue((int)DxfCode.Text, link.NgHandle),
                new TypedValue((int)DxfCode.Text, link.DesignHandle),
                new TypedValue((int)DxfCode.Real, link.Grid),
                new TypedValue((int)DxfCode.Real, link.TextHeight));
        }

        private static bool TryReadTable(Table table, Transaction transaction, out TableLink link)
        {
            link = null;
            TypedValue[] values = Read(table, transaction, TableKey);
            if (values == null || values.Length < 6) return false;
            link = new TableLink(
                Convert.ToString(values[0].Value, CultureInfo.InvariantCulture),
                Convert.ToString(values[1].Value, CultureInfo.InvariantCulture).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).ToList(),
                Convert.ToString(values[2].Value, CultureInfo.InvariantCulture),
                Convert.ToString(values[3].Value, CultureInfo.InvariantCulture),
                Convert.ToDouble(values[4].Value, CultureInfo.InvariantCulture),
                Convert.ToDouble(values[5].Value, CultureInfo.InvariantCulture));
            return true;
        }

        private static Xrecord Record(DBObject owner, Transaction transaction, string key)
        {
            if (owner.ExtensionDictionary.IsNull) owner.CreateExtensionDictionary();
            DBDictionary dictionary = transaction.GetObject(owner.ExtensionDictionary, OpenMode.ForWrite, false) as DBDictionary;
            if (dictionary.Contains(key)) return transaction.GetObject(dictionary.GetAt(key), OpenMode.ForWrite, false) as Xrecord;
            var record = new Xrecord();
            dictionary.SetAt(key, record);
            transaction.AddNewlyCreatedDBObject(record, true);
            return record;
        }

        private static TypedValue[] Read(DBObject owner, Transaction transaction, string key)
        {
            if (owner == null || owner.ExtensionDictionary.IsNull) return null;
            DBDictionary dictionary = transaction.GetObject(owner.ExtensionDictionary, OpenMode.ForRead, false) as DBDictionary;
            if (dictionary == null || !dictionary.Contains(key)) return null;
            Xrecord record = transaction.GetObject(dictionary.GetAt(key), OpenMode.ForRead, false) as Xrecord;
            return record == null || record.Data == null ? null : record.Data.AsArray();
        }

        private static string LabelText(string label, CivilFeatureLine featureLine)
        {
            double min = featureLine.MinElevation;
            double max = featureLine.MaxElevation;
            string elevation = Math.Abs(max - min) <= 0.001 ? max.ToString("N3", CultureInfo.CurrentCulture) : min.ToString("N3", CultureInfo.CurrentCulture) + " - " + max.ToString("N3", CultureInfo.CurrentCulture);
            return label + "\\PFINAL PLATFORM ELEVATION: " + elevation;
        }

        private static string FindName(Database database, Transaction transaction, string sourceHandle)
        {
            BlockTableRecord space = ModelSpace(database, transaction, OpenMode.ForRead);
            foreach (ObjectId id in space)
            {
                MText text = transaction.GetObject(id, OpenMode.ForRead, false) as MText;
                string handle; string label;
                if (text != null && TryReadName(text, transaction, out handle, out label) && string.Equals(handle, sourceHandle, StringComparison.OrdinalIgnoreCase)) return label;
            }
            return null;
        }

        private static void EraseName(BlockTableRecord space, Transaction transaction, string sourceHandle)
        {
            foreach (ObjectId id in space.Cast<ObjectId>().ToList())
            {
                MText text = transaction.GetObject(id, OpenMode.ForWrite, false) as MText;
                string handle; string label;
                if (text != null && TryReadName(text, transaction, out handle, out label) && string.Equals(handle, sourceHandle, StringComparison.OrdinalIgnoreCase)) text.Erase();
            }
        }

        private static Point3d Centre(CivilFeatureLine featureLine)
        {
            Point3dCollection points = featureLine.GetPoints(FeatureLinePointType.PIPoint);
            if (points == null || points.Count == 0) return Point3d.Origin;
            return new Point3d(points.Cast<Point3d>().Average(p => p.X), points.Cast<Point3d>().Average(p => p.Y), points.Cast<Point3d>().Average(p => p.Z));
        }

        private static List<ObjectId> ResolvePlatforms(Document document, string scope)
        {
            if (string.Equals(scope, "Selected", StringComparison.OrdinalIgnoreCase))
            {
                PromptSelectionResult selection = SelectFeatureLines(document.Editor, "\nSelect closed platform feature lines: ");
                if (selection.Status != PromptStatus.OK || selection.Value == null) return new List<ObjectId>();
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                    return selection.Value.GetObjectIds().Where(id => { CivilFeatureLine fl = OpenFeatureLine(transaction, id, OpenMode.ForRead); return fl != null && fl.Closed; }).ToList();
            }
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = ModelSpace(document.Database, transaction, OpenMode.ForRead);
                return space.Cast<ObjectId>().Where(id =>
                {
                    CivilFeatureLine fl = OpenFeatureLine(transaction, id, OpenMode.ForRead);
                    return fl != null && fl.Closed && (string.Equals(fl.Layer, PlatformLayer, StringComparison.OrdinalIgnoreCase) || fl.Layer.IndexOf("PLATFORM", StringComparison.OrdinalIgnoreCase) >= 0);
                }).ToList();
            }
        }

        private static PromptSelectionResult SelectFeatureLines(Editor editor, string message)
        {
            PromptSelectionResult implied = editor.SelectImplied();
            if (implied.Status == PromptStatus.OK && implied.Value != null && implied.Value.Count > 0)
            {
                editor.SetImpliedSelection(new ObjectId[0]);
                return implied;
            }
            return editor.GetSelection(new PromptSelectionOptions { MessageForAdding = message, AllowDuplicates = false, RejectObjectsFromNonCurrentSpace = true });
        }

        private static CivilFeatureLine OpenFeatureLine(Transaction transaction, ObjectId id, OpenMode mode)
        {
            if (id.IsNull || id.IsErased) return null;
            try { return transaction.GetObject(id, mode, false) as CivilFeatureLine; } catch { return null; }
        }

        private static bool Editable(CivilFeatureLine featureLine, Transaction transaction)
        {
            if (featureLine == null || featureLine.IsReferenceObject) return false;
            LayerTableRecord layer = transaction.GetObject(featureLine.LayerId, OpenMode.ForRead, false) as LayerTableRecord;
            return layer == null || !layer.IsLocked;
        }

        private static List<string> ReadFeatureLineNames(BlockTableRecord space, Transaction transaction)
        {
            return space.Cast<ObjectId>().Select(id => OpenFeatureLine(transaction, id, OpenMode.ForRead)).Where(fl => fl != null && !string.IsNullOrWhiteSpace(fl.Name)).Select(fl => fl.Name).ToList();
        }

        private static string UniqueName(string requested, ISet<string> names)
        {
            string candidate = requested; int index = 2;
            while (!names.Add(candidate)) candidate = requested + " (" + (index++).ToString(CultureInfo.InvariantCulture) + ")";
            return candidate;
        }

        private static ObjectId EnsureNamedCivilObject(Database database, CivilDocument civilDocument, string typeName, string collectionMethod, string name, ObjectId relatedId)
        {
            ObjectId existing = FindNamed(database, civilDocument, collectionMethod, name);
            if (!existing.IsNull) return existing;
            Type type = typeof(CivilFeatureLine).Assembly.GetType(typeName, false);
            if (type == null) return ObjectId.Null;
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => string.Equals(m.Name, "Create", StringComparison.OrdinalIgnoreCase)).OrderBy(m => m.GetParameters().Length))
            {
                object[] args = BuildArgs(method.GetParameters(), database, civilDocument, name, relatedId, Point3d.Origin, null);
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

        private static ObjectId FindNamed(Database database, CivilDocument civilDocument, string methodName, string name)
        {
            MethodInfo method = civilDocument.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            object result = method == null ? null : method.Invoke(civilDocument, null);
            IEnumerable values = result as IEnumerable;
            if (values == null) return ObjectId.Null;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (object item in values)
                {
                    if (!(item is ObjectId)) continue;
                    ObjectId id = (ObjectId)item;
                    DBObject dbObject = transaction.GetObject(id, OpenMode.ForRead, false);
                    PropertyInfo property = dbObject.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
                    string current = property == null ? string.Empty : Convert.ToString(property.GetValue(dbObject, null), CultureInfo.CurrentCulture);
                    if (string.Equals(current, name, StringComparison.OrdinalIgnoreCase)) return id;
                }
            }
            return ObjectId.Null;
        }

        private static bool MoveToSite(ObjectId featureLineId, ObjectId siteId)
        {
            foreach (MethodInfo method in typeof(CivilFeatureLine).GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name.IndexOf("MoveToSite", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                ParameterInfo[] p = method.GetParameters();
                if (p.Length != 2 || p.Any(x => x.ParameterType != typeof(ObjectId))) continue;
                try { method.Invoke(null, new object[] { featureLineId, siteId }); return true; } catch { }
            }
            return false;
        }

        private static bool AddBreaklines(Document document, ObjectId surfaceId, IList<ObjectId> featureLines)
        {
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                DBObject surface = transaction.GetObject(surfaceId, OpenMode.ForWrite, false);
                PropertyInfo property = surface.GetType().GetProperty("BreaklinesDefinition", BindingFlags.Public | BindingFlags.Instance);
                object definition = property == null ? null : property.GetValue(surface, null);
                if (definition == null) return false;
                var ids = new ObjectIdCollection();
                foreach (ObjectId id in featureLines) ids.Add(id);
                foreach (MethodInfo method in definition.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(m => m.Name.IndexOf("AddStandardBreaklines", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    object[] args = BuildArgs(method.GetParameters(), document.Database, CivilApplication.ActiveDocument, string.Empty, ObjectId.Null, Point3d.Origin, ids);
                    if (args == null) continue;
                    try { method.Invoke(definition, args); transaction.Commit(); return true; } catch { }
                }
            }
            return false;
        }

        private static ObjectId CreateGradingGroup(Database database, CivilDocument civilDocument, ObjectId siteId, string name, ObjectId surfaceId)
        {
            Type type = typeof(CivilFeatureLine).Assembly.GetType("Autodesk.Civil.DatabaseServices.GradingGroup", false);
            if (type == null) return ObjectId.Null;
            ObjectId id = ObjectId.Null;
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => string.Equals(m.Name, "Create", StringComparison.OrdinalIgnoreCase)).OrderBy(m => m.GetParameters().Length))
            {
                object[] args = BuildArgs(method.GetParameters(), database, civilDocument, name, siteId, Point3d.Origin, null);
                if (args == null) continue;
                try
                {
                    object value = method.Invoke(null, args);
                    if (value is ObjectId) { id = (ObjectId)value; break; }
                }
                catch { }
            }
            if (id.IsNull) return id;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                DBObject group = transaction.GetObject(id, OpenMode.ForWrite, false);
                SetProperty(group, "AutomaticSurfaceCreation", true);
                SetProperty(group, "SurfaceName", name.Replace("-GRADING", string.Empty));
                if (!surfaceId.IsNull) SetProperty(group, "SurfaceId", surfaceId);
                transaction.Commit();
            }
            return id;
        }

        private static bool CreateInfill(ObjectId groupId, Point3d point)
        {
            Type type = typeof(CivilFeatureLine).Assembly.GetType("Autodesk.Civil.DatabaseServices.Grading", false);
            if (type == null) return false;
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name.IndexOf("CreateInfill", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                ParameterInfo[] parameters = method.GetParameters();
                var args = new object[parameters.Length];
                bool valid = true;
                for (int i = 0; i < parameters.Length; i++)
                {
                    Type t = parameters[i].ParameterType;
                    if (t == typeof(ObjectId)) args[i] = groupId;
                    else if (t == typeof(Point3d)) args[i] = point;
                    else if (t == typeof(string)) args[i] = "CE Platform Infill";
                    else if (t == typeof(bool)) args[i] = true;
                    else if (parameters[i].HasDefaultValue) args[i] = parameters[i].DefaultValue;
                    else { valid = false; break; }
                }
                if (!valid) continue;
                try { method.Invoke(null, args); return true; } catch { }
            }
            return false;
        }

        private static object[] BuildArgs(ParameterInfo[] parameters, Database database, CivilDocument civilDocument, string name, ObjectId relatedId, Point3d point, ObjectIdCollection ids)
        {
            var args = new object[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                Type t = parameters[i].ParameterType;
                string n = (parameters[i].Name ?? string.Empty).ToLowerInvariant();
                if (t == typeof(Database)) args[i] = database;
                else if (t == typeof(CivilDocument)) args[i] = civilDocument;
                else if (t == typeof(string)) args[i] = name;
                else if (t == typeof(ObjectIdCollection) && ids != null) args[i] = ids;
                else if (t == typeof(ObjectId)) args[i] = relatedId;
                else if (t == typeof(Point3d)) args[i] = point;
                else if (t == typeof(double)) args[i] = n.IndexOf("mid", StringComparison.OrdinalIgnoreCase) >= 0 ? 1.0 : 0.0;
                else if (t == typeof(bool)) args[i] = true;
                else if (parameters[i].HasDefaultValue) args[i] = parameters[i].DefaultValue;
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

        private static bool EnsureLayout(Database database, Transaction transaction, string layoutName, string title)
        {
            LayoutManager manager = LayoutManager.Current;
            ObjectId layoutId;
            bool created = false;
            try { layoutId = manager.GetLayoutId(layoutName); } catch { layoutId = ObjectId.Null; }
            if (layoutId.IsNull)
            {
                try { layoutId = manager.CreateLayout(layoutName); created = true; } catch { return false; }
            }
            Layout layout = transaction.GetObject(layoutId, OpenMode.ForRead, false) as Layout;
            if (layout == null) return created;
            BlockTableRecord paper = transaction.GetObject(layout.BlockTableRecordId, OpenMode.ForWrite, false) as BlockTableRecord;
            if (paper == null) return created;
            bool exists = paper.Cast<ObjectId>().Select(id => transaction.GetObject(id, OpenMode.ForRead, false) as MText).Any(text => text != null && text.Contents.IndexOf("CE PLATFORM DRAWING", StringComparison.OrdinalIgnoreCase) >= 0);
            if (!exists)
            {
                var text = new MText();
                text.SetDatabaseDefaults(database);
                text.Location = new Point3d(20.0, 20.0, 0.0);
                text.TextHeight = 5.0;
                text.Contents = "CE PLATFORM DRAWING\\P" + title + "\\PCreate linked sections from the generated model-space section lines with CE_XSCREATE.";
                paper.AppendEntity(text);
                transaction.AddNewlyCreatedDBObject(text, true);
            }
            return created;
        }

        private static bool Inside(Polyline polygon, Point3d point)
        {
            bool inside = false;
            for (int i = 0, j = polygon.NumberOfVertices - 1; i < polygon.NumberOfVertices; j = i++)
            {
                Point2d a = polygon.GetPoint2dAt(i), b = polygon.GetPoint2dAt(j);
                bool intersect = ((a.Y > point.Y) != (b.Y > point.Y)) && point.X < (b.X - a.X) * (point.Y - a.Y) / (Math.Abs(b.Y - a.Y) <= 1e-20 ? 1e-20 : b.Y - a.Y) + a.X;
                if (intersect) inside = !inside;
            }
            return inside;
        }

        private static bool TryParseDouble(string value, out double result)
        {
            return double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out result) ||
                   double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out result);
        }

        private static string Safe(string value, string fallback) { return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim(); }
        private static double PlanDistance(Point3d a, Point3d b) { double dx = a.X - b.X, dy = a.Y - b.Y; return Math.Sqrt(dx * dx + dy * dy); }
        private static ObjectId Resolve(Database database, string handle) { long value; if (string.IsNullOrWhiteSpace(handle) || !long.TryParse(handle, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)) return ObjectId.Null; try { return database.GetObjectId(false, new Handle(value), 0); } catch { return ObjectId.Null; } }
        private static BlockTableRecord ModelSpace(Database database, Transaction transaction, OpenMode mode) { return transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(database), mode, false) as BlockTableRecord; }
        private static ObjectId Layer(Database database, Transaction transaction, string name) { LayerTable table = transaction.GetObject(database.LayerTableId, OpenMode.ForRead, false) as LayerTable; if (table.Has(name)) return table[name]; table.UpgradeOpen(); var layer = new LayerTableRecord { Name = name }; ObjectId id = table.Add(layer); transaction.AddNewlyCreatedDBObject(layer, true); return id; }
        private static void Dispose(DBObjectCollection values) { if (values == null) return; foreach (DBObject value in values) if (value != null) value.Dispose(); }
        private static Document ActiveDocument() { return AcApplication.DocumentManager.MdiActiveDocument; }

        private sealed class StepRelation
        {
            internal StepRelation(string sourceHandle, double horizontalOffset, double verticalOffset, int sequence) { SourceHandle = sourceHandle; HorizontalOffset = horizontalOffset; VerticalOffset = verticalOffset; Sequence = sequence; }
            internal string SourceHandle { get; private set; }
            internal double HorizontalOffset { get; private set; }
            internal double VerticalOffset { get; private set; }
            internal int Sequence { get; private set; }
        }

        private sealed class DrapeRelation
        {
            internal string SourceHandle { get; set; }
            internal string SurfaceHandle { get; set; }
            internal double VerticalOffset { get; set; }
            internal int Sequence { get; set; }
            internal bool Intermediate { get; set; }
        }

        private sealed class DrapeSnapshot
        {
            internal DrapeSnapshot(string childHandle, DrapeRelation link) { ChildHandle = childHandle; Link = link; }
            internal string ChildHandle { get; private set; }
            internal DrapeRelation Link { get; private set; }
        }

        private sealed class TableLink
        {
            internal TableLink(string type, List<string> sources, string ng, string design, double grid, double height) { Type = type; SourceHandles = sources; NgHandle = ng; DesignHandle = design; Grid = grid; TextHeight = height; }
            internal string Type { get; private set; }
            internal List<string> SourceHandles { get; private set; }
            internal string NgHandle { get; private set; }
            internal string DesignHandle { get; private set; }
            internal double Grid { get; private set; }
            internal double TextHeight { get; private set; }
        }

        private sealed class PlatformRow { internal string Name; internal double Area; internal double Perimeter; internal double Min; internal double Max; }
        private sealed class CutFillRow
        {
            internal CutFillRow(string name, double area, double cut, double fill, double grid, int samples) { Name = name; Area = area; Cut = cut; Fill = fill; Grid = grid; Samples = samples; }
            internal string Name; internal double Area; internal double Cut; internal double Fill; internal double Grid; internal int Samples;
        }
    }

    internal static class PlatformDynamicRefreshManager
    {
        private static bool _initialised;
        private static bool _busy;
        private static bool _pending;
        private static Database _database;
        private static Document _document;
        private static DateTime _lastChangeUtc = DateTime.MinValue;

        internal static void EnsureInitialized()
        {
            if (_initialised) { Attach(AcApplication.DocumentManager.MdiActiveDocument); return; }
            _initialised = true;
            AcApplication.DocumentManager.DocumentActivated += Activated;
            AcApplication.DocumentManager.DocumentCreated += Activated;
            AcApplication.DocumentManager.DocumentToBeDestroyed += Destroyed;
            AcApplication.Idle += Idle;
            Attach(AcApplication.DocumentManager.MdiActiveDocument);
        }

        internal static void Queue() { if (_busy) return; _pending = true; _lastChangeUtc = DateTime.UtcNow; }
        private static void Activated(object sender, DocumentCollectionEventArgs e) { Attach(e == null ? null : e.Document); }
        private static void Destroyed(object sender, DocumentCollectionEventArgs e) { if (e != null && ReferenceEquals(e.Document, _document)) Detach(); }
        private static void Attach(Document document)
        {
            if (ReferenceEquals(document, _document)) return;
            Detach();
            _document = document;
            _database = document == null ? null : document.Database;
            if (_database == null) return;
            _database.ObjectModified += Changed;
            _database.ObjectErased += Erased;
        }
        private static void Detach()
        {
            if (_database != null) { _database.ObjectModified -= Changed; _database.ObjectErased -= Erased; }
            _database = null; _document = null;
        }
        private static void Changed(object sender, ObjectEventArgs e)
        {
            if (_busy || e == null || e.DBObject == null) return;
            if (e.DBObject is CivilSurface || e.DBObject is CivilFeatureLine || e.DBObject is Table) Queue();
        }
        private static void Erased(object sender, ObjectErasedEventArgs e) { if (!_busy) Queue(); }
        private static void Idle(object sender, EventArgs e)
        {
            Document active = AcApplication.DocumentManager.MdiActiveDocument;
            Attach(active);
            if (!_pending || _busy || active == null || (DateTime.UtcNow - _lastChangeUtc).TotalSeconds < 1.5) return;
            string commands = Convert.ToString(AcApplication.GetSystemVariable("CMDNAMES"), CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(commands)) return;
            int activeCommands = Convert.ToInt32(AcApplication.GetSystemVariable("CMDACTIVE"), CultureInfo.InvariantCulture);
            if (activeCommands != 0) return;
            _busy = true;
            bool undoDisabled = false;
            try
            {
                active.Database.DisableUndoRecording(true);
                undoDisabled = true;
                PlatformProductionCommands.RefreshAll(active);
                _pending = false;
            }
            catch { }
            finally
            {
                if (undoDisabled) { try { active.Database.DisableUndoRecording(false); } catch { } }
                _busy = false;
            }
        }
    }
}

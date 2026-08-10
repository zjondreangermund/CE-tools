using System;
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
    /// <summary>
    /// Linked platform-production workflow. It builds on CE Tools feature-line and
    /// setting-out engines instead of duplicating them: multi-object feature-line
    /// creation remains CE_FLCREATE, linked stepped offsets use the established
    /// CE_FLREL relation schema, and platform sections continue into CE_XSCREATE.
    /// </summary>
    public sealed class PlatformProductionCommands
    {
        private const string StepRecordKey = "CE_FLREL";
        private const string DrapeRecordKey = "CE_PLATFORM_DRAPE";
        private const string NameRecordKey = "CE_PLATFORM_NAME";
        private const string TableRecordKey = "CE_PLATFORM_TABLE";
        private const string SectionRecordKey = "CE_PLATFORM_SECTION";
        private const string PlatformLayer = "CE-PLATFORM";
        private const string LabelLayer = "CE-PLATFORM-LABEL";
        private const string SectionLayer = "CE-PLATFORM-SECTION-LINE";
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
                "Create linked platform feature lines, grading relationships, setting-out, names, tables, quantities, layouts and section lines.",
                new List<DisciplineWorkflowAction>
                {
                    new DisciplineWorkflowAction("Create feature lines from multiple polylines", "CE_FLCREATE", "Create multiple feature lines with a popup surface selection and optional intermediate surface points.", "01 Source feature lines"),
                    new DisciplineWorkflowAction("Apply platform slopes / flatten", "CE_PLATFORMSLOPE", "Apply a constant high-to-low plane, a specified fixed slope from the high point, or flatten at the highest elevation.", "02 Platform levels"),
                    new DisciplineWorkflowAction("Create stepped offsets for multiple platforms", "CE_PLATFORMSTEPOFFSETS", "Create outward linked stepped-offset feature lines for multiple selected platform feature lines.", "03 Stepped offsets"),
                    new DisciplineWorkflowAction("Drape stepped offsets to surface", "CE_PLATFORMDRAPE", "Drape selected linked stepped offsets to a selected surface and dynamically drive their source platform feature lines.", "03 Stepped offsets"),
                    new DisciplineWorkflowAction("Create platform site / surface / infill", "CE_PLATFORMSURFACE", "Assign selected closed platform feature lines to a platform site, create a separate TIN surface and attempt grading infill for all or selected platforms.", "04 Surface and grading"),
                    new DisciplineWorkflowAction("Platform setting-out", "CE_PLATFORMSETTINGOUT", "Use multi-feature-line vertex setting-out or grid setting-out with the existing dynamic CE engines.", "05 Setting-out"),
                    new DisciplineWorkflowAction("Platform names", "CE_PLATFORMNAMES", "Place PLATFORM-1, PLATFORM-2 labels at platform centres with final platform elevation/range.", "06 Annotation"),
                    new DisciplineWorkflowAction("Linked platform table", "CE_PLATFORMTABLE", "Create a linked annotative platform area/elevation register.", "06 Annotation"),
                    new DisciplineWorkflowAction("Platform cut / fill quantities", "CE_PLATFORMCUTFILL", "Compare selected NG and design surfaces inside platform boundaries and create a linked cut/fill table.", "07 Quantities"),
                    new DisciplineWorkflowAction("Platform BOQ", "CE_BOQPLATFORM", "Create/export the existing CE platform/grading BOQ.", "07 Quantities"),
                    new DisciplineWorkflowAction("Platform drawings / layouts / section lines", "CE_PLATFORMDRAWINGS", "Create platform layouts and one/two centre section lines per platform for linked CE cross-section production.", "08 Drawings"),
                    new DisciplineWorkflowAction("Dynamic cross sections", "CE_XSTOOLS", "Create/refresh linked sections from the generated platform section lines.", "08 Drawings"),
                    new DisciplineWorkflowAction("Platform report", "CE_REPORTPLATFORM", "Generate the existing CE platform design report.", "08 Drawings"),
                    new DisciplineWorkflowAction("Refresh linked platforms", "CE_PLATFORMREFRESH", "Refresh draped surface links, stepped offsets, platform labels and linked tables.", "09 Maintain")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_PLATFORMSLOPE", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void PlatformSlope()
        {
            PlatformDynamicRefreshManager.EnsureInitialized();
            Document document = ActiveDocument();
            if (document == null) return;
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Platform Slopes",
                "Apply one level rule independently to each selected feature line. The highest and lowest existing vertices define the design direction.");
            model.AddChoice("Mode", "01 Level rule", "Platform level method", "Constant slope between highest and lowest", "Choose a constant high-to-low plane, a specified fixed slope starting at the highest point, or set every vertex to the highest elevation.", new[] { "Constant slope between highest and lowest", "Fixed slope from highest towards lowest", "Match all vertices to highest elevation" });
            model.AddDouble("Slope", "01 Level rule", "Fixed slope (%)", 1.0, "Used only for Fixed slope mode. Positive entry falls from the highest point toward the lowest-point direction.");
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;
            PromptSelectionResult selection = SelectFeatureLines(document.Editor, "\nSelect multiple platform feature lines: ");
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;

            string mode = model.Text("Mode");
            double fixedSlope = Math.Abs(model.Double("Slope", 1.0)) / 100.0;
            int changed = 0;
            int skipped = 0;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in selection.Value.GetObjectIds())
                {
                    CivilFeatureLine featureLine = OpenFeatureLine(transaction, id, OpenMode.ForWrite);
                    if (!Editable(featureLine, transaction)) { skipped++; continue; }
                    Point3dCollection points = featureLine.GetPoints(FeatureLinePointType.AllPoints);
                    if (points == null || points.Count < 2) { skipped++; continue; }
                    List<Point3d> values = points.Cast<Point3d>().ToList();
                    Point3d high = values.OrderByDescending(point => point.Z).First();
                    Point3d low = values.OrderBy(point => point.Z).First();
                    Vector2d direction = new Vector2d(low.X - high.X, low.Y - high.Y);
                    double planLength = direction.Length;
                    if (planLength <= Tol && !string.Equals(mode, "Match all vertices to highest elevation", StringComparison.OrdinalIgnoreCase))
                    {
                        skipped++;
                        continue;
                    }
                    if (planLength > Tol) direction = direction.GetNormal();
                    double constantGrade = planLength <= Tol ? 0.0 : (low.Z - high.Z) / planLength;
                    for (int index = 0; index < points.Count; index++)
                    {
                        Point3d point = points[index];
                        double z;
                        if (string.Equals(mode, "Match all vertices to highest elevation", StringComparison.OrdinalIgnoreCase))
                            z = high.Z;
                        else
                        {
                            double projection = new Vector2d(point.X - high.X, point.Y - high.Y).DotProduct(direction);
                            if (string.Equals(mode, "Fixed slope from highest towards lowest", StringComparison.OrdinalIgnoreCase))
                                z = high.Z - fixedSlope * Math.Max(0.0, projection);
                            else
                                z = high.Z + constantGrade * projection;
                        }
                        SetAbsoluteElevation(featureLine, point, index, z);
                    }
                    changed++;
                }
                transaction.Commit();
            }
            document.Editor.Regen();
            PlatformDynamicRefreshManager.Queue();
            document.Editor.WriteMessage("\nCE_PLATFORMSLOPE complete. Feature lines changed={0}; skipped={1}.", changed, skipped);
        }

        [CommandMethod("CE_TOOLS", "CE_PLATFORMSTEPOFFSETS", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void PlatformStepOffsets()
        {
            PlatformDynamicRefreshManager.EnsureInitialized();
            Document document = ActiveDocument();
            if (document == null) return;
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Multiple Platform Stepped Offsets",
                "Create outward stepped offsets for multiple selected platform feature lines. Closed platforms choose the outward side automatically; open sources use the positive offset side.");
            model.AddPositiveDouble("Horizontal", "01 Steps", "Horizontal step", 1.0, "Horizontal distance per successive step.");
            model.AddText("Vertical", "01 Steps", "Vertical step", "-0.500", "Vertical difference per successive step; negative values step down.");
            model.AddPositiveInteger("Count", "01 Steps", "Number of stepped offsets", 1, "Number of linked children per selected platform.");
            model.AddText("Prefix", "02 Naming", "Platform step suffix", "STEP", "Child names are based on the source name plus this suffix and sequence number.");
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;
            PromptSelectionResult selection = SelectFeatureLines(document.Editor, "\nSelect multiple platform source feature lines: ");
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;
            double horizontal = Math.Max(0.001, model.Double("Horizontal", 1.0));
            double vertical;
            if (!ProductionSettingsDialogModel.TryDouble(model.Text("Vertical"), out vertical))
            {
                document.Editor.WriteMessage("\nCE_PLATFORMSTEPOFFSETS cancelled. Vertical step must be numeric.");
                return;
            }
            int count = Math.Max(1, model.Integer("Count", 1));
            string suffix = string.IsNullOrWhiteSpace(model.Text("Prefix")) ? "STEP" : model.Text("Prefix").Trim();
            int created = 0;
            int skipped = 0;

            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord modelSpace = GetModelSpace(document.Database, transaction, OpenMode.ForWrite);
                HashSet<string> names = ReadFeatureLineNames(modelSpace, transaction);
                foreach (ObjectId id in selection.Value.GetObjectIds())
                {
                    CivilFeatureLine source = OpenFeatureLine(transaction, id, OpenMode.ForRead);
                    if (!Editable(source, transaction)) { skipped++; continue; }
                    using (Polyline plan = BuildPlanPolyline(source))
                    {
                        double sign = plan.Closed ? ResolveOutwardSign(plan, horizontal) : 1.0;
                        string baseName = string.IsNullOrWhiteSpace(source.Name) ? "PLATFORM" : source.Name;
                        for (int index = 1; index <= count; index++)
                        {
                            double offset = sign * horizontal * index;
                            double elevation = vertical * index;
                            string name = UniqueName(baseName + "-" + suffix + "-" + index.ToString(CultureInfo.InvariantCulture), names);
                            ObjectId childId = CreateOffsetChild(source, plan, offset, elevation, name, modelSpace, transaction);
                            CivilFeatureLine child = OpenFeatureLine(transaction, childId, OpenMode.ForWrite);
                            WriteStepRelation(child, transaction, source.Handle.ToString(), offset, elevation, index);
                            created++;
                        }
                    }
                }
                transaction.Commit();
            }
            document.Editor.Regen();
            PlatformDynamicRefreshManager.Queue();
            document.Editor.WriteMessage("\nCE_PLATFORMSTEPOFFSETS complete. Linked stepped feature lines={0}; skipped sources={1}.", created, skipped);
        }

        [CommandMethod("CE_TOOLS", "CE_PLATFORMDRAPE", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void PlatformDrape()
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
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Drape Platform Steps to Surface",
                "Drape selected linked stepped offsets to a selected surface. The draped child drives its source platform elevation; all other CE linked steps then rebuild from that source.");
            model.AddChoice("Surface", "01 Surface", "Survey / target surface", surfaces[0].Name, "Select the surface that controls the stepped-offset elevation.", surfaces.Select(surface => surface.Name));
            model.AddChoice("Intermediate", "01 Surface", "Add intermediate surface points", "No", "Allow Civil 3D to insert intermediate surface grade-break points on the draped feature line.", new[] { "No", "Yes" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;
            SurfaceChoice surfaceChoice = surfaces.FirstOrDefault(surface => string.Equals(surface.Name, model.Text("Surface"), StringComparison.OrdinalIgnoreCase));
            if (surfaceChoice == null) return;
            PromptSelectionResult selection = SelectFeatureLines(document.Editor, "\nSelect linked stepped-offset feature lines to drape: ");
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;
            bool intermediate = string.Equals(model.Text("Intermediate"), "Yes", StringComparison.OrdinalIgnoreCase);
            int linked = 0;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in selection.Value.GetObjectIds())
                {
                    CivilFeatureLine child = OpenFeatureLine(transaction, id, OpenMode.ForWrite);
                    if (!Editable(child, transaction)) continue;
                    StepRelation relation;
                    if (!TryReadStepRelation(child, transaction, out relation))
                        relation = new StepRelation(child.Handle.ToString(), 0.0, 0.0, 0);
                    child.AssignElevationsFromSurface(surfaceChoice.ObjectId, intermediate);
                    WriteDrapeRelation(child, transaction, new DrapeRelation
                    {
                        SourceHandle = relation.SourceHandle,
                        SurfaceHandle = surfaceChoice.ObjectId.Handle.ToString(),
                        VerticalOffset = relation.VerticalOffset,
                        Sequence = relation.Sequence,
                        IncludeIntermediate = intermediate
                    });
                    linked++;
                }
                transaction.Commit();
            }
            RefreshAll(document);
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_PLATFORMDRAPE complete. Dynamic surface links created={0}. Changes to the selected survey/target surface are monitored while CE Tools is loaded.", linked);
        }

        [CommandMethod("CE_TOOLS", "CE_PLATFORMSURFACE", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void PlatformSurface()
        {
            PlatformDynamicRefreshManager.EnsureInitialized();
            Document document = ActiveDocument();
            if (document == null) return;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (civilDocument == null) return;
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Platform Site / Surface / Infill",
                "Assign closed platform feature lines to one platform site, create a separate platform TIN surface from those feature lines, and attempt native Civil 3D grading infill at each selected platform centre.");
            model.AddChoice("Scope", "01 Platforms", "Scope", "Selected", "Use selected feature lines or all closed feature lines on the CE platform layer.", new[] { "Selected", "All" });
            model.AddText("Site", "02 Civil 3D", "Platform site name", "CE-PLATFORM-SITE", "Civil 3D site used for platform feature lines and grading infill.");
            model.AddText("Surface", "02 Civil 3D", "Platform surface name", "CE-PLATFORM-SURFACE", "Separate TIN surface built from the selected platform feature-line breaklines.");
            model.AddChoice("Infill", "03 Grading", "Create grading infill", "Yes", "Attempt native Civil 3D grading infill at the centre of each closed platform.", new[] { "Yes", "No" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;
            List<ObjectId> featureLines = ResolvePlatformScope(document, model.Text("Scope"));
            if (featureLines.Count == 0) return;
            string siteName = SafeName(model.Text("Site"), "CE-PLATFORM-SITE");
            string surfaceName = SafeName(model.Text("Surface"), "CE-PLATFORM-SURFACE");
            bool infillRequested = string.Equals(model.Text("Infill"), "Yes", StringComparison.OrdinalIgnoreCase);
            ObjectId siteId = EnsureSite(document, civilDocument, siteName);
            ObjectId surfaceId = EnsureTinSurface(document, civilDocument, surfaceName);
            int moved = 0;
            int breaklines = 0;
            int infills = 0;
            int warnings = 0;
            if (!siteId.IsNull)
            {
                foreach (ObjectId id in featureLines)
                {
                    if (TryMoveFeatureLineToSite(id, siteId)) moved++; else warnings++;
                }
            }
            if (!surfaceId.IsNull)
            {
                if (TryAddStandardBreaklines(document, surfaceId, featureLines)) breaklines = featureLines.Count; else warnings++;
            }
            else warnings++;
            if (infillRequested && !siteId.IsNull)
            {
                ObjectId gradingGroupId = EnsureGradingGroup(document, civilDocument, siteId, surfaceName + "-GRADING", surfaceId);
                if (!gradingGroupId.IsNull)
                {
                    using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                    {
                        foreach (ObjectId id in featureLines)
                        {
                            CivilFeatureLine featureLine = OpenFeatureLine(transaction, id, OpenMode.ForRead);
                            if (featureLine == null || !featureLine.Closed) continue;
                            if (TryCreateInfill(gradingGroupId, PlatformCentre(featureLine))) infills++; else warnings++;
                        }
                    }
                }
                else warnings++;
            }
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_PLATFORMSURFACE complete. Site assignments={0}; surface breaklines={1}; grading infills={2}; compatibility warnings={3}. Surface/infill creation uses installed Civil 3D API capabilities and leaves source feature lines intact if a host operation is unavailable.", moved, breaklines, infills, warnings);
        }

        [CommandMethod("CE_TOOLS", "CE_PLATFORMSETTINGOUT", CommandFlags.Modal)]
        public void PlatformSettingOut()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            DisciplineWorkflowDialogs.SelectAndRun(
                document,
                "CE Tools - Platform Setting-Out",
                "Choose dynamic vertex setting-out for multiple platform feature lines or grid setting-out inside platform boundaries.",
                new List<DisciplineWorkflowAction>
                {
                    new DisciplineWorkflowAction("Multiple feature-line vertex setting-out", "CE_VERTEXSETTINGOUT", "Create dynamic COGO/MText/MLeader points at vertices, long tangents and arcs with configurable sequence numbering.", "01 Vertex setting-out"),
                    new DisciplineWorkflowAction("Grid setting-out", "CE_GRIDSETTINGOUT", "Create perimeter/full-grid COGO setting-out points inside a selected platform boundary.", "02 Grid setting-out"),
                    new DisciplineWorkflowAction("Refresh vertex setting-out", "CE_VERTEXSETTINGOUTREFRESH", "Refresh linked platform setting-out points and table rows.", "03 Maintain")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_PLATFORMNAMES", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void PlatformNames()
        {
            PlatformDynamicRefreshManager.EnsureInitialized();
            Document document = ActiveDocument();
            if (document == null) return;
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Platform Names",
                "Place linked PLATFORM-1, PLATFORM-2 labels at the centre of all or selected closed feature lines and show final platform elevation/range.");
            model.AddChoice("Scope", "01 Platforms", "Scope", "Selected", "Label selected platforms or all closed platform feature lines.", new[] { "Selected", "All" });
            model.AddText("Prefix", "02 Naming", "Platform prefix", "PLATFORM", "Labels are generated as PLATFORM-1, PLATFORM-2, etc.");
            model.AddPositiveInteger("Start", "02 Naming", "Starting number", 1, "First platform number.");
            model.AddDouble("TextHeight", "03 Annotation", "Paper text height", 2.5, "Annotative label height.");
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;
            List<ObjectId> ids = ResolvePlatformScope(document, model.Text("Scope"));
            if (ids.Count == 0) return;
            string prefix = SafeName(model.Text("Prefix"), "PLATFORM");
            int start = model.Integer("Start", 1);
            double height = PaperAnnotationScale.ModelTextHeight(document.Database, Math.Max(0.5, model.Double("TextHeight", 2.5)));
            int created = 0;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = GetModelSpace(document.Database, transaction, OpenMode.ForWrite);
                ObjectId layerId = GetOrCreateLayer(document.Database, transaction, LabelLayer);
                var featureLines = ids.Select(id => OpenFeatureLine(transaction, id, OpenMode.ForRead))
                    .Where(fl => fl != null && fl.Closed)
                    .OrderByDescending(fl => PlatformCentre(fl).Y)
                    .ThenBy(fl => PlatformCentre(fl).X)
                    .ToList();
                int index = 0;
                foreach (CivilFeatureLine featureLine in featureLines)
                {
                    string label = prefix + "-" + (start + index).ToString(CultureInfo.InvariantCulture);
                    ErasePlatformName(space, transaction, featureLine.Handle.ToString());
                    var text = new MText();
                    text.SetDatabaseDefaults(document.Database);
                    text.LayerId = layerId;
                    text.Location = PlatformCentre(featureLine);
                    text.Attachment = AttachmentPoint.MiddleCenter;
                    text.TextHeight = height;
                    text.Contents = PlatformLabelText(label, featureLine);
                    PaperAnnotationScale.SetAnnotative(text);
                    space.AppendEntity(text);
                    transaction.AddNewlyCreatedDBObject(text, true);
                    WriteNameLink(text, transaction, featureLine.Handle.ToString(), label);
                    created++;
                    index++;
                }
                transaction.Commit();
            }
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_PLATFORMNAMES complete. Linked labels created={0}.", created);
        }

        [CommandMethod("CE_TOOLS", "CE_PLATFORMTABLE", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void PlatformTable()
        {
            PlatformDynamicRefreshManager.EnsureInitialized();
            Document document = ActiveDocument();
            if (document == null) return;
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Linked Platform Register",
                "Create an annotative linked table showing platform name, area, perimeter and final platform elevation/range.");
            model.AddChoice("Scope", "01 Platforms", "Scope", "Selected", "Add selected platforms or all closed platform feature lines.", new[] { "Selected", "All" });
            model.AddDouble("TextHeight", "02 Table", "Paper text height", 2.0, "Annotative table text height.");
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;
            List<ObjectId> ids = ResolvePlatformScope(document, model.Text("Scope"));
            if (ids.Count == 0) return;
            PromptPointResult insertion = document.Editor.GetPoint("\nPick insertion point for the linked platform table: ");
            if (insertion.Status != PromptStatus.OK) return;
            double height = PaperAnnotationScale.ModelTextHeight(document.Database, Math.Max(0.5, model.Double("TextHeight", 2.0)));
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = GetModelSpace(document.Database, transaction, OpenMode.ForWrite);
                var table = new Table();
                table.SetDatabaseDefaults(document.Database);
                table.TableStyle = document.Database.Tablestyle;
                table.Position = insertion.Value;
                PaperAnnotationScale.SetAnnotative(table);
                space.AppendEntity(table);
                transaction.AddNewlyCreatedDBObject(table, true);
                TableLink link = new TableLink
                {
                    Type = "REGISTER",
                    SourceHandles = ids.Select(id => id.Handle.ToString()).ToList(),
                    NgSurfaceHandle = string.Empty,
                    DesignSurfaceHandle = string.Empty,
                    GridSpacing = 0.0,
                    TextHeight = height
                };
                WriteTableLink(table, transaction, link);
                PopulateRegisterTable(document.Database, transaction, table, link);
                transaction.Commit();
            }
            document.Editor.Regen();
        }

        [CommandMethod("CE_TOOLS", "CE_PLATFORMCUTFILL", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void PlatformCutFill()
        {
            PlatformDynamicRefreshManager.EnsureInitialized();
            Document document = ActiveDocument();
            if (document == null) return;
            List<SurfaceChoice> surfaces = WorkflowRepairCommands.ReadSurfaceChoices(document);
            if (surfaces.Count < 2)
            {
                document.Editor.WriteMessage("\nCE_PLATFORMCUTFILL cancelled. At least two Civil 3D surfaces are required.");
                return;
            }
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Platform Cut / Fill",
                "Compare an existing/NG surface with a design/comparison surface inside each selected platform boundary. Volumes are calculated by linked grid-cell sampling and refresh when the surfaces change.");
            model.AddChoice("Scope", "01 Platforms", "Scope", "Selected", "Use selected platforms or all closed platform feature lines.", new[] { "Selected", "All" });
            model.AddChoice("NG", "02 Surfaces", "Base / NG surface", surfaces[0].Name, "Existing/base surface.", surfaces.Select(surface => surface.Name));
            model.AddChoice("Design", "02 Surfaces", "Comparison / design surface", surfaces[1].Name, "Design/comparison surface.", surfaces.Select(surface => surface.Name));
            model.AddPositiveDouble("Grid", "03 Quantity", "Sampling grid spacing", 1.0, "Grid-cell size used for cut/fill volume integration. Very large sample counts are automatically coarsened for safety.");
            model.AddDouble("TextHeight", "04 Table", "Paper text height", 2.0, "Annotative table text height.");
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;
            SurfaceChoice ng = surfaces.FirstOrDefault(surface => string.Equals(surface.Name, model.Text("NG"), StringComparison.OrdinalIgnoreCase));
            SurfaceChoice design = surfaces.FirstOrDefault(surface => string.Equals(surface.Name, model.Text("Design"), StringComparison.OrdinalIgnoreCase));
            if (ng == null || design == null || ng.ObjectId == design.ObjectId)
            {
                document.Editor.WriteMessage("\nCE_PLATFORMCUTFILL cancelled. Select two different valid surfaces.");
                return;
            }
            List<ObjectId> ids = ResolvePlatformScope(document, model.Text("Scope"));
            if (ids.Count == 0) return;
            PromptPointResult insertion = document.Editor.GetPoint("\nPick insertion point for the linked platform cut/fill table: ");
            if (insertion.Status != PromptStatus.OK) return;
            double height = PaperAnnotationScale.ModelTextHeight(document.Database, Math.Max(0.5, model.Double("TextHeight", 2.0)));
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = GetModelSpace(document.Database, transaction, OpenMode.ForWrite);
                var table = new Table();
                table.SetDatabaseDefaults(document.Database);
                table.TableStyle = document.Database.Tablestyle;
                table.Position = insertion.Value;
                PaperAnnotationScale.SetAnnotative(table);
                space.AppendEntity(table);
                transaction.AddNewlyCreatedDBObject(table, true);
                TableLink link = new TableLink
                {
                    Type = "CUTFILL",
                    SourceHandles = ids.Select(id => id.Handle.ToString()).ToList(),
                    NgSurfaceHandle = ng.ObjectId.Handle.ToString(),
                    DesignSurfaceHandle = design.ObjectId.Handle.ToString(),
                    GridSpacing = Math.Max(0.01, model.Double("Grid", 1.0)),
                    TextHeight = height
                };
                WriteTableLink(table, transaction, link);
                PopulateCutFillTable(document.Database, transaction, table, link);
                transaction.Commit();
            }
            document.Editor.Regen();
        }

        [CommandMethod("CE_TOOLS", "CE_PLATFORMDRAWINGS", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void PlatformDrawings()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Platform Drawings / Sections",
                "Create platform-specific layouts and centre section lines. The generated two-point lines are directly compatible with CE_XSCREATE for linked dynamic sections.");
            model.AddChoice("Scope", "01 Platforms", "Scope", "Selected", "Use selected platforms or all closed platform feature lines.", new[] { "Selected", "All" });
            model.AddChoice("Sections", "02 Sections", "Section lines per platform", "Two axes", "Create one longest-axis section or two orthogonal centre sections.", new[] { "One axis", "Two axes" });
            model.AddChoice("Layouts", "03 Drawings", "Create platform layouts", "Yes", "Create one CE-PLATFORM-n paper-space layout per platform if it does not already exist.", new[] { "Yes", "No" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;
            List<ObjectId> ids = ResolvePlatformScope(document, model.Text("Scope"));
            if (ids.Count == 0) return;
            bool two = string.Equals(model.Text("Sections"), "Two axes", StringComparison.OrdinalIgnoreCase);
            bool layouts = string.Equals(model.Text("Layouts"), "Yes", StringComparison.OrdinalIgnoreCase);
            int lines = 0;
            int layoutCount = 0;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = GetModelSpace(document.Database, transaction, OpenMode.ForWrite);
                ObjectId layerId = GetOrCreateLayer(document.Database, transaction, SectionLayer);
                var platforms = ids.Select(id => OpenFeatureLine(transaction, id, OpenMode.ForRead)).Where(fl => fl != null && fl.Closed)
                    .OrderByDescending(fl => PlatformCentre(fl).Y).ThenBy(fl => PlatformCentre(fl).X).ToList();
                for (int index = 0; index < platforms.Count; index++)
                {
                    CivilFeatureLine featureLine = platforms[index];
                    Extents3d extents = featureLine.GeometricExtents;
                    Point3d centre = PlatformCentre(featureLine);
                    double width = Math.Max(extents.MaxPoint.X - extents.MinPoint.X, 0.01);
                    double height = Math.Max(extents.MaxPoint.Y - extents.MinPoint.Y, 0.01);
                    double pad = Math.Max(width, height) * 0.10;
                    Line first;
                    Line second = null;
                    if (width >= height)
                    {
                        first = new Line(new Point3d(extents.MinPoint.X - pad, centre.Y, centre.Z), new Point3d(extents.MaxPoint.X + pad, centre.Y, centre.Z));
                        if (two) second = new Line(new Point3d(centre.X, extents.MinPoint.Y - pad, centre.Z), new Point3d(centre.X, extents.MaxPoint.Y + pad, centre.Z));
                    }
                    else
                    {
                        first = new Line(new Point3d(centre.X, extents.MinPoint.Y - pad, centre.Z), new Point3d(centre.X, extents.MaxPoint.Y + pad, centre.Z));
                        if (two) second = new Line(new Point3d(extents.MinPoint.X - pad, centre.Y, centre.Z), new Point3d(extents.MaxPoint.X + pad, centre.Y, centre.Z));
                    }
                    foreach (Line line in new[] { first, second }.Where(value => value != null))
                    {
                        line.SetDatabaseDefaults(document.Database);
                        line.LayerId = layerId;
                        space.AppendEntity(line);
                        transaction.AddNewlyCreatedDBObject(line, true);
                        WriteSimpleLink(line, transaction, SectionRecordKey, featureLine.Handle.ToString());
                        lines++;
                    }
                    if (layouts)
                    {
                        string layoutName = "CE-PLATFORM-" + (index + 1).ToString("00", CultureInfo.InvariantCulture);
                        if (EnsurePlatformLayout(document.Database, transaction, layoutName, "PLATFORM-" + (index + 1).ToString(CultureInfo.InvariantCulture))) layoutCount++;
                    }
                }
                transaction.Commit();
            }
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_PLATFORMDRAWINGS complete. Section source lines={0}; new layouts={1}. Run CE_XSCREATE on the generated section lines to create linked dynamic section drawings.", lines, layoutCount);
        }

        [CommandMethod("CE_TOOLS", "CE_PLATFORMREFRESH", CommandFlags.Modal | CommandFlags.Redraw)]
        public void PlatformRefresh()
        {
            PlatformDynamicRefreshManager.EnsureInitialized();
            Document document = ActiveDocument();
            if (document == null) return;
            int refreshed = RefreshAll(document);
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_PLATFORMREFRESH complete. Linked platform items refreshed={0}.", refreshed);
        }

        internal static int RefreshAll(Document document)
        {
            if (document == null) return 0;
            List<DrapeSnapshot> drapes = ReadDrapeSnapshots(document.Database);
            int refreshed = 0;
            if (drapes.Count > 0)
            {
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    foreach (DrapeSnapshot snapshot in drapes)
                    {
                        ObjectId childId = ResolveHandle(document.Database, snapshot.ChildHandle);
                        ObjectId sourceId = ResolveHandle(document.Database, snapshot.Relation.SourceHandle);
                        ObjectId surfaceId = ResolveHandle(document.Database, snapshot.Relation.SurfaceHandle);
                        CivilFeatureLine child = OpenFeatureLine(transaction, childId, OpenMode.ForWrite);
                        CivilFeatureLine source = OpenFeatureLine(transaction, sourceId, OpenMode.ForWrite);
                        CivilSurface surface = surfaceId.IsNull ? null : transaction.GetObject(surfaceId, OpenMode.ForRead, false) as CivilSurface;
                        if (child == null || source == null || surface == null) continue;
                        try
                        {
                            child.AssignElevationsFromSurface(surfaceId, snapshot.Relation.IncludeIntermediate);
                            if (child.ObjectId != source.ObjectId)
                            {
                                Point3dCollection sourcePoints = source.GetPoints(FeatureLinePointType.AllPoints);
                                for (int index = 0; index < sourcePoints.Count; index++)
                                {
                                    Point3d point = sourcePoints[index];
                                    Point3d nearest = child.GetClosestPointTo(new Point3d(point.X, point.Y, 0.0), Vector3d.ZAxis, false);
                                    SetAbsoluteElevation(source, point, index, nearest.Z - snapshot.Relation.VerticalOffset);
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
                    BlockTableRecord space = GetModelSpace(document.Database, transaction, OpenMode.ForRead);
                    foreach (DrapeSnapshot snapshot in drapes)
                    {
                        CivilFeatureLine child = FindStepChild(space, transaction, snapshot.Relation.SourceHandle, snapshot.Relation.Sequence);
                        ObjectId surfaceId = ResolveHandle(document.Database, snapshot.Relation.SurfaceHandle);
                        if (child == null || surfaceId.IsNull) continue;
                        child.UpgradeOpen();
                        try
                        {
                            child.AssignElevationsFromSurface(surfaceId, snapshot.Relation.IncludeIntermediate);
                            WriteDrapeRelation(child, transaction, snapshot.Relation);
                        }
                        catch { }
                    }
                    transaction.Commit();
                }
            }

            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = GetModelSpace(document.Database, transaction, OpenMode.ForRead);
                foreach (ObjectId id in space.Cast<ObjectId>().ToList())
                {
                    MText text = transaction.GetObject(id, OpenMode.ForRead, false) as MText;
                    string sourceHandle;
                    string label;
                    if (text != null && TryReadNameLink(text, transaction, out sourceHandle, out label))
                    {
                        ObjectId sourceId = ResolveHandle(document.Database, sourceHandle);
                        CivilFeatureLine source = OpenFeatureLine(transaction, sourceId, OpenMode.ForRead);
                        if (source != null)
                        {
                            text.UpgradeOpen();
                            text.Location = PlatformCentre(source);
                            text.Contents = PlatformLabelText(label, source);
                            refreshed++;
                        }
                        continue;
                    }
                    Table table = transaction.GetObject(id, OpenMode.ForRead, false) as Table;
                    TableLink tableLink;
                    if (table != null && TryReadTableLink(table, transaction, out tableLink))
                    {
                        table.UpgradeOpen();
                        if (string.Equals(tableLink.Type, "CUTFILL", StringComparison.OrdinalIgnoreCase)) PopulateCutFillTable(document.Database, transaction, table, tableLink);
                        else PopulateRegisterTable(document.Database, transaction, table, tableLink);
                        refreshed++;
                    }
                }
                transaction.Commit();
            }
            return refreshed;
        }

        private static void SetAbsoluteElevation(CivilFeatureLine featureLine, Point3d originalPoint, int index, double elevation)
        {
            if (featureLine.IsElevationRelativeToSurface(originalPoint))
                featureLine.SetPointRelativeElevation(originalPoint, false, elevation);
            else
                featureLine.SetPointElevation(index, elevation);
        }

        private static ObjectId CreateOffsetChild(CivilFeatureLine source, Polyline plan, double horizontalOffset, double verticalOffset, string name, BlockTableRecord modelSpace, Transaction transaction)
        {
            DBObjectCollection offsets = plan.GetOffsetCurves(horizontalOffset);
            if (offsets == null || offsets.Count != 1)
            {
                Dispose(offsets);
                throw new InvalidOperationException("A platform stepped offset produced multiple/no curves. Reduce the offset or simplify self-intersections.");
            }
            Curve curve = offsets[0] as Curve;
            if (curve == null) { Dispose(offsets); throw new InvalidOperationException("The platform offset did not return a usable curve."); }
            curve.SetDatabaseDefaults(source.Database);
            curve.LayerId = source.LayerId;
            modelSpace.AppendEntity(curve);
            transaction.AddNewlyCreatedDBObject(curve, true);
            ObjectId childId = source.SiteId.IsNull ? CivilFeatureLine.Create(name, curve.ObjectId) : CivilFeatureLine.Create(name, curve.ObjectId, source.SiteId);
            CivilFeatureLine child = OpenFeatureLine(transaction, childId, OpenMode.ForWrite);
            if (child == null) throw new InvalidOperationException("Civil 3D did not return the created stepped feature line.");
            child.LayerId = source.LayerId;
            if (!string.IsNullOrWhiteSpace(source.StyleName)) child.StyleName = source.StyleName;
            Point3dCollection points = child.GetPoints(FeatureLinePointType.AllPoints);
            for (int index = 0; index < points.Count; index++)
            {
                Point3d point = points[index];
                Point3d sourcePoint = source.GetClosestPointTo(new Point3d(point.X, point.Y, 0.0), Vector3d.ZAxis, false);
                child.SetPointElevation(index, sourcePoint.Z + verticalOffset);
            }
            if (!curve.IsErased) curve.Erase();
            return childId;
        }

        private static Polyline BuildPlanPolyline(CivilFeatureLine source)
        {
            Point3dCollection pi = source.GetPoints(FeatureLinePointType.PIPoint);
            if (pi == null || pi.Count < 2) throw new InvalidOperationException("Feature line requires at least two PI points.");
            List<Point3d> points = pi.Cast<Point3d>().ToList();
            if (source.Closed && points.Count > 2 && PlanDistance(points[0], points[points.Count - 1]) <= Tol) points.RemoveAt(points.Count - 1);
            var polyline = new Polyline(points.Count) { Closed = source.Closed, Elevation = 0.0, Normal = Vector3d.ZAxis };
            int segments = source.Closed ? points.Count : points.Count - 1;
            for (int index = 0; index < points.Count; index++)
            {
                double bulge = index < segments ? source.GetBulge(index) : 0.0;
                polyline.AddVertexAt(index, new Point2d(points[index].X, points[index].Y), bulge, 0.0, 0.0);
            }
            return polyline;
        }

        private static double ResolveOutwardSign(Polyline plan, double distance)
        {
            if (!plan.Closed) return 1.0;
            double positiveArea = OffsetArea(plan, distance);
            double negativeArea = OffsetArea(plan, -distance);
            if (double.IsNaN(positiveArea) && double.IsNaN(negativeArea)) return 1.0;
            if (double.IsNaN(positiveArea)) return -1.0;
            if (double.IsNaN(negativeArea)) return 1.0;
            return positiveArea >= negativeArea ? 1.0 : -1.0;
        }

        private static double OffsetArea(Polyline plan, double distance)
        {
            DBObjectCollection offsets = null;
            try
            {
                offsets = plan.GetOffsetCurves(distance);
                if (offsets == null || offsets.Count != 1) return double.NaN;
                Polyline polyline = offsets[0] as Polyline;
                return polyline == null || !polyline.Closed ? double.NaN : Math.Abs(polyline.Area);
            }
            catch { return double.NaN; }
            finally { Dispose(offsets); }
        }

        private static void WriteStepRelation(CivilFeatureLine child, Transaction transaction, string sourceHandle, double horizontalOffset, double verticalOffset, int sequence)
        {
            Xrecord record = GetOrCreateRecord(child, transaction, StepRecordKey);
            record.Data = new ResultBuffer(
                new TypedValue((int)DxfCode.Text, sourceHandle),
                new TypedValue((int)DxfCode.Real, horizontalOffset),
                new TypedValue((int)DxfCode.Real, verticalOffset),
                new TypedValue((int)DxfCode.Int32, sequence));
        }

        private static bool TryReadStepRelation(CivilFeatureLine child, Transaction transaction, out StepRelation relation)
        {
            relation = null;
            TypedValue[] values = ReadRecord(child, transaction, StepRecordKey);
            if (values == null || values.Length < 4) return false;
            relation = new StepRelation(
                Convert.ToString(values[0].Value, CultureInfo.InvariantCulture),
                Convert.ToDouble(values[1].Value, CultureInfo.InvariantCulture),
                Convert.ToDouble(values[2].Value, CultureInfo.InvariantCulture),
                Convert.ToInt32(values[3].Value, CultureInfo.InvariantCulture));
            return !string.IsNullOrWhiteSpace(relation.SourceHandle);
        }

        private static void WriteDrapeRelation(CivilFeatureLine child, Transaction transaction, DrapeRelation relation)
        {
            Xrecord record = GetOrCreateRecord(child, transaction, DrapeRecordKey);
            record.Data = new ResultBuffer(
                new TypedValue((int)DxfCode.Text, relation.SourceHandle ?? string.Empty),
                new TypedValue((int)DxfCode.Text, relation.SurfaceHandle ?? string.Empty),
                new TypedValue((int)DxfCode.Real, relation.VerticalOffset),
                new TypedValue((int)DxfCode.Int32, relation.Sequence),
                new TypedValue((int)DxfCode.Int16, relation.IncludeIntermediate ? 1 : 0));
        }

        private static bool TryReadDrapeRelation(CivilFeatureLine child, Transaction transaction, out DrapeRelation relation)
        {
            relation = null;
            TypedValue[] values = ReadRecord(child, transaction, DrapeRecordKey);
            if (values == null || values.Length < 5) return false;
            relation = new DrapeRelation
            {
                SourceHandle = Convert.ToString(values[0].Value, CultureInfo.InvariantCulture),
                SurfaceHandle = Convert.ToString(values[1].Value, CultureInfo.InvariantCulture),
                VerticalOffset = Convert.ToDouble(values[2].Value, CultureInfo.InvariantCulture),
                Sequence = Convert.ToInt32(values[3].Value, CultureInfo.InvariantCulture),
                IncludeIntermediate = Convert.ToInt16(values[4].Value, CultureInfo.InvariantCulture) != 0
            };
            return !string.IsNullOrWhiteSpace(relation.SourceHandle) && !string.IsNullOrWhiteSpace(relation.SurfaceHandle);
        }

        private static List<DrapeSnapshot> ReadDrapeSnapshots(Database database)
        {
            var result = new List<DrapeSnapshot>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = GetModelSpace(database, transaction, OpenMode.ForRead);
                foreach (ObjectId id in space)
                {
                    CivilFeatureLine child = OpenFeatureLine(transaction, id, OpenMode.ForRead);
                    DrapeRelation relation;
                    if (child != null && TryReadDrapeRelation(child, transaction, out relation))
                        result.Add(new DrapeSnapshot(child.Handle.ToString(), relation));
                }
            }
            return result;
        }

        private static CivilFeatureLine FindStepChild(BlockTableRecord space, Transaction transaction, string sourceHandle, int sequence)
        {
            foreach (ObjectId id in space)
            {
                CivilFeatureLine child = OpenFeatureLine(transaction, id, OpenMode.ForRead);
                StepRelation relation;
                if (child != null && TryReadStepRelation(child, transaction, out relation) &&
                    string.Equals(relation.SourceHandle, sourceHandle, StringComparison.OrdinalIgnoreCase) && relation.Sequence == sequence)
                    return child;
            }
            return null;
        }

        private static void WriteNameLink(MText text, Transaction transaction, string sourceHandle, string label)
        {
            Xrecord record = GetOrCreateRecord(text, transaction, NameRecordKey);
            record.Data = new ResultBuffer(new TypedValue((int)DxfCode.Text, sourceHandle), new TypedValue((int)DxfCode.Text, label));
        }

        private static bool TryReadNameLink(MText text, Transaction transaction, out string sourceHandle, out string label)
        {
            sourceHandle = string.Empty; label = string.Empty;
            TypedValue[] values = ReadRecord(text, transaction, NameRecordKey);
            if (values == null || values.Length < 2) return false;
            sourceHandle = Convert.ToString(values[0].Value, CultureInfo.InvariantCulture);
            label = Convert.ToString(values[1].Value, CultureInfo.InvariantCulture);
            return !string.IsNullOrWhiteSpace(sourceHandle);
        }

        private static string PlatformLabelText(string label, CivilFeatureLine featureLine)
        {
            if (featureLine == null) return label;
            double min = featureLine.MinElevation;
            double max = featureLine.MaxElevation;
            string elevation = Math.Abs(max - min) <= 0.001
                ? max.ToString("N3", CultureInfo.CurrentCulture)
                : min.ToString("N3", CultureInfo.CurrentCulture) + " - " + max.ToString("N3", CultureInfo.CurrentCulture);
            return label + "\\PFINAL PLATFORM ELEVATION: " + elevation;
        }

        private static Point3d PlatformCentre(CivilFeatureLine featureLine)
        {
            Point3dCollection points = featureLine.GetPoints(FeatureLinePointType.PIPoint);
            if (points == null || points.Count == 0) return Point3d.Origin;
            return new Point3d(points.Cast<Point3d>().Average(point => point.X), points.Cast<Point3d>().Average(point => point.Y), points.Cast<Point3d>().Average(point => point.Z));
        }

        private static void ErasePlatformName(BlockTableRecord space, Transaction transaction, string sourceHandle)
        {
            foreach (ObjectId id in space.Cast<ObjectId>().ToList())
            {
                MText text = transaction.GetObject(id, OpenMode.ForWrite, false) as MText;
                string handle; string label;
                if (text != null && TryReadNameLink(text, transaction, out handle, out label) && string.Equals(handle, sourceHandle, StringComparison.OrdinalIgnoreCase)) text.Erase();
            }
        }

        private static void WriteTableLink(Table table, Transaction transaction, TableLink link)
        {
            Xrecord record = GetOrCreateRecord(table, transaction, TableRecordKey);
            record.Data = new ResultBuffer(
                new TypedValue((int)DxfCode.Text, link.Type ?? "REGISTER"),
                new TypedValue((int)DxfCode.Text, string.Join(",", link.SourceHandles ?? new List<string>())),
                new TypedValue((int)DxfCode.Text, link.NgSurfaceHandle ?? string.Empty),
                new TypedValue((int)DxfCode.Text, link.DesignSurfaceHandle ?? string.Empty),
                new TypedValue((int)DxfCode.Real, link.GridSpacing),
                new TypedValue((int)DxfCode.Real, link.TextHeight));
        }

        private static bool TryReadTableLink(Table table, Transaction transaction, out TableLink link)
        {
            link = null;
            TypedValue[] values = ReadRecord(table, transaction, TableRecordKey);
            if (values == null || values.Length < 6) return false;
            link = new TableLink
            {
                Type = Convert.ToString(values[0].Value, CultureInfo.InvariantCulture),
                SourceHandles = Convert.ToString(values[1].Value, CultureInfo.InvariantCulture).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).ToList(),
                NgSurfaceHandle = Convert.ToString(values[2].Value, CultureInfo.InvariantCulture),
                DesignSurfaceHandle = Convert.ToString(values[3].Value, CultureInfo.InvariantCulture),
                GridSpacing = Convert.ToDouble(values[4].Value, CultureInfo.InvariantCulture),
                TextHeight = Convert.ToDouble(values[5].Value, CultureInfo.InvariantCulture)
            };
            return true;
        }

        private static void PopulateRegisterTable(Database database, Transaction transaction, Table table, TableLink link)
        {
            var rows = new List<PlatformRow>();
            int automatic = 1;
            foreach (string handle in link.SourceHandles)
            {
                ObjectId id = ResolveHandle(database, handle);
                CivilFeatureLine featureLine = OpenFeatureLine(transaction, id, OpenMode.ForRead);
                if (featureLine == null || !featureLine.Closed) continue;
                string label = FindPlatformLabel(database, transaction, handle) ?? "PLATFORM-" + automatic.ToString(CultureInfo.InvariantCulture);
                using (Polyline plan = BuildPlanPolyline(featureLine))
                {
                    rows.Add(new PlatformRow
                    {
                        Name = label,
                        Area = Math.Abs(plan.Area),
                        Perimeter = plan.Length,
                        MinElevation = featureLine.MinElevation,
                        MaxElevation = featureLine.MaxElevation
                    });
                }
                automatic++;
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
                table.Cells[row, 3].TextString = rows[index].MinElevation.ToString("N3", CultureInfo.CurrentCulture);
                table.Cells[row, 4].TextString = rows[index].MaxElevation.ToString("N3", CultureInfo.CurrentCulture);
            }
            FormatTable(table, link.TextHeight);
        }

        private static void PopulateCutFillTable(Database database, Transaction transaction, Table table, TableLink link)
        {
            ObjectId ngId = ResolveHandle(database, link.NgSurfaceHandle);
            ObjectId designId = ResolveHandle(database, link.DesignSurfaceHandle);
            CivilSurface ng = ngId.IsNull ? null : transaction.GetObject(ngId, OpenMode.ForRead, false) as CivilSurface;
            CivilSurface design = designId.IsNull ? null : transaction.GetObject(designId, OpenMode.ForRead, false) as CivilSurface;
            var rows = new List<CutFillRow>();
            int automatic = 1;
            if (ng != null && design != null)
            {
                foreach (string handle in link.SourceHandles)
                {
                    ObjectId id = ResolveHandle(database, handle);
                    CivilFeatureLine featureLine = OpenFeatureLine(transaction, id, OpenMode.ForRead);
                    if (featureLine == null || !featureLine.Closed) continue;
                    using (Polyline plan = BuildPlanPolyline(featureLine))
                    {
                        string label = FindPlatformLabel(database, transaction, handle) ?? "PLATFORM-" + automatic.ToString(CultureInfo.InvariantCulture);
                        rows.Add(CalculateCutFill(plan, ng, design, label, link.GridSpacing));
                    }
                    automatic++;
                }
            }
            table.SetSize(rows.Count + 3, 7);
            table.SetRowHeight(Math.Max(link.TextHeight * 1.8, 0.001));
            table.SetColumnWidth(Math.Max(link.TextHeight * 10.0, 0.001));
            table.Cells[0, 0].TextString = "CE LINKED PLATFORM CUT / FILL QUANTITIES";
            table.MergeCells(CellRange.Create(table, 0, 0, 0, 6));
            table.Cells[1, 0].TextString = "Grid-based linked surface integration. Positive design-NG = fill; positive NG-design = cut.";
            table.MergeCells(CellRange.Create(table, 1, 0, 1, 6));
            string[] headers = { "PLATFORM", "AREA SAMPLED", "CUT VOLUME", "FILL VOLUME", "NET FILL-CUT", "GRID", "SAMPLES" };
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
            FormatTable(table, link.TextHeight);
        }

        private static CutFillRow CalculateCutFill(Polyline boundary, CivilSurface ng, CivilSurface design, string name, double requestedGrid)
        {
            Extents3d extents = boundary.GeometricExtents;
            double grid = Math.Max(0.01, requestedGrid);
            double width = Math.Max(0.0, extents.MaxPoint.X - extents.MinPoint.X);
            double height = Math.Max(0.0, extents.MaxPoint.Y - extents.MinPoint.Y);
            double estimated = Math.Max(1.0, Math.Ceiling(width / grid) * Math.Ceiling(height / grid));
            const double maxSamples = 250000.0;
            if (estimated > maxSamples) grid *= Math.Sqrt(estimated / maxSamples);
            double cell = grid * grid;
            double cut = 0.0, fill = 0.0, area = 0.0;
            int samples = 0;
            for (double x = extents.MinPoint.X + grid * 0.5; x <= extents.MaxPoint.X; x += grid)
            {
                for (double y = extents.MinPoint.Y + grid * 0.5; y <= extents.MaxPoint.Y; y += grid)
                {
                    Point3d point = new Point3d(x, y, 0.0);
                    if (!PointInside(boundary, point)) continue;
                    double existing;
                    double final;
                    try { existing = ng.FindElevationAtXY(x, y); final = design.FindElevationAtXY(x, y); }
                    catch { continue; }
                    if (double.IsNaN(existing) || double.IsInfinity(existing) || double.IsNaN(final) || double.IsInfinity(final)) continue;
                    double difference = final - existing;
                    if (difference >= 0.0) fill += difference * cell; else cut += -difference * cell;
                    area += cell;
                    samples++;
                }
            }
            return new CutFillRow { Name = name, Area = area, Cut = cut, Fill = fill, Grid = grid, Samples = samples };
        }

        private static void FormatTable(Table table, double textHeight)
        {
            for (int row = 0; row < table.Rows.Count; row++)
                for (int column = 0; column < table.Columns.Count; column++)
                {
                    table.Cells[row, column].Alignment = CellAlignment.MiddleCenter;
                    table.Cells[row, column].TextHeight = textHeight;
                }
            try { table.GenerateLayout(); } catch { }
            try { table.RecordGraphicsModified(true); } catch { }
        }

        private static string FindPlatformLabel(Database database, Transaction transaction, string sourceHandle)
        {
            BlockTableRecord space = GetModelSpace(database, transaction, OpenMode.ForRead);
            foreach (ObjectId id in space)
            {
                MText text = transaction.GetObject(id, OpenMode.ForRead, false) as MText;
                string handle; string label;
                if (text != null && TryReadNameLink(text, transaction, out handle, out label) && string.Equals(handle, sourceHandle, StringComparison.OrdinalIgnoreCase)) return label;
            }
            return null;
        }

        private static List<ObjectId> ResolvePlatformScope(Document document, string scope)
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
                BlockTableRecord space = GetModelSpace(document.Database, transaction, OpenMode.ForRead);
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

        private static bool PointInside(Polyline polygon, Point3d point)
        {
            if (polygon == null || !polygon.Closed || polygon.NumberOfVertices < 3) return false;
            bool inside = false;
            for (int i = 0, j = polygon.NumberOfVertices - 1; i < polygon.NumberOfVertices; j = i++)
            {
                Point2d a = polygon.GetPoint2dAt(i), b = polygon.GetPoint2dAt(j);
                bool intersects = ((a.Y > point.Y) != (b.Y > point.Y)) &&
                    point.X < (b.X - a.X) * (point.Y - a.Y) / (Math.Abs(b.Y - a.Y) <= 1e-20 ? 1e-20 : b.Y - a.Y) + a.X;
                if (intersects) inside = !inside;
            }
            return inside;
        }

        private static Xrecord GetOrCreateRecord(DBObject owner, Transaction transaction, string key)
        {
            if (owner.ExtensionDictionary.IsNull) owner.CreateExtensionDictionary();
            DBDictionary dictionary = transaction.GetObject(owner.ExtensionDictionary, OpenMode.ForWrite, false) as DBDictionary;
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

        private static void WriteSimpleLink(DBObject owner, Transaction transaction, string key, string sourceHandle)
        {
            Xrecord record = GetOrCreateRecord(owner, transaction, key);
            record.Data = new ResultBuffer(new TypedValue((int)DxfCode.Text, sourceHandle));
        }

        private static ObjectId EnsureSite(Document document, CivilDocument civilDocument, string name)
        {
            Type type = typeof(CivilFeatureLine).Assembly.GetType("Autodesk.Civil.DatabaseServices.Site", false);
            if (type == null) return ObjectId.Null;
            ObjectId existing = FindNamedCivilObject(document.Database, civilDocument, "GetSiteIds", name);
            if (!existing.IsNull) return existing;
            return InvokeStaticCreate(type, "Create", document.Database, civilDocument, name, ObjectId.Null, Point3d.Origin);
        }

        private static ObjectId EnsureTinSurface(Document document, CivilDocument civilDocument, string name)
        {
            Type type = typeof(CivilSurface).Assembly.GetType("Autodesk.Civil.DatabaseServices.TinSurface", false);
            if (type == null) return ObjectId.Null;
            ObjectId existing = FindNamedCivilObject(document.Database, civilDocument, "GetSurfaceIds", name);
            if (!existing.IsNull) return existing;
            return InvokeStaticCreate(type, "Create", document.Database, civilDocument, name, ObjectId.Null, Point3d.Origin);
        }

        private static ObjectId EnsureGradingGroup(Document document, CivilDocument civilDocument, ObjectId siteId, string name, ObjectId surfaceId)
        {
            Type type = typeof(CivilFeatureLine).Assembly.GetType("Autodesk.Civil.DatabaseServices.GradingGroup", false);
            if (type == null) return ObjectId.Null;
            ObjectId id = InvokeStaticCreate(type, "Create", document.Database, civilDocument, name, siteId, Point3d.Origin);
            if (id.IsNull) return id;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                DBObject group = transaction.GetObject(id, OpenMode.ForWrite, false);
                TrySetProperty(group, "AutomaticSurfaceCreation", true);
                TrySetProperty(group, "SurfaceName", name.Replace("-GRADING", string.Empty));
                if (!surfaceId.IsNull) TrySetProperty(group, "SurfaceId", surfaceId);
                transaction.Commit();
            }
            return id;
        }

        private static bool TryMoveFeatureLineToSite(ObjectId featureLineId, ObjectId siteId)
        {
            foreach (MethodInfo method in typeof(CivilFeatureLine).GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name.IndexOf("MoveToSite", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length != 2 || parameters.Any(p => p.ParameterType != typeof(ObjectId))) continue;
                try { method.Invoke(null, new object[] { featureLineId, siteId }); return true; } catch { }
            }
            return false;
        }

        private static bool TryAddStandardBreaklines(Document document, ObjectId surfaceId, IList<ObjectId> featureLines)
        {
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                DBObject surface = transaction.GetObject(surfaceId, OpenMode.ForWrite, false);
                PropertyInfo property = surface.GetType().GetProperty("BreaklinesDefinition", BindingFlags.Public | BindingFlags.Instance);
                object definition = property == null ? null : property.GetValue(surface, null);
                if (definition == null) return false;
                ObjectIdCollection ids = new ObjectIdCollection(featureLines.ToArray());
                foreach (MethodInfo method in definition.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(m => m.Name.IndexOf("AddStandardBreaklines", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    object[] arguments = BuildReflectionArguments(method.GetParameters(), document.Database, CivilApplication.ActiveDocument, string.Empty, ObjectId.Null, Point3d.Origin, ids);
                    if (arguments == null) continue;
                    try { method.Invoke(definition, arguments); transaction.Commit(); return true; } catch { }
                }
            }
            return false;
        }

        private static bool TryCreateInfill(ObjectId gradingGroupId, Point3d location)
        {
            Type type = typeof(CivilFeatureLine).Assembly.GetType("Autodesk.Civil.DatabaseServices.Grading", false);
            if (type == null) return false;
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name.IndexOf("CreateInfill", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                ParameterInfo[] parameters = method.GetParameters();
                object[] args = new object[parameters.Length];
                bool valid = true;
                for (int index = 0; index < parameters.Length; index++)
                {
                    Type parameterType = parameters[index].ParameterType;
                    if (parameterType == typeof(ObjectId)) args[index] = gradingGroupId;
                    else if (parameterType == typeof(Point3d)) args[index] = location;
                    else if (parameterType == typeof(string)) args[index] = "CE Platform Infill";
                    else if (parameterType == typeof(bool)) args[index] = true;
                    else if (parameters[index].HasDefaultValue) args[index] = parameters[index].DefaultValue;
                    else { valid = false; break; }
                }
                if (!valid) continue;
                try { method.Invoke(null, args); return true; } catch { }
            }
            return false;
        }

        private static ObjectId InvokeStaticCreate(Type type, string methodName, Database database, CivilDocument civilDocument, string name, ObjectId relatedId, Point3d point)
        {
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => string.Equals(m.Name, methodName, StringComparison.OrdinalIgnoreCase)).OrderBy(m => m.GetParameters().Length))
            {
                object[] arguments = BuildReflectionArguments(method.GetParameters(), database, civilDocument, name, relatedId, point, null);
                if (arguments == null) continue;
                try
                {
                    object value = method.Invoke(null, arguments);
                    if (value is ObjectId) return (ObjectId)value;
                    DBObject dbObject = value as DBObject;
                    if (dbObject != null) return dbObject.ObjectId;
                }
                catch { }
            }
            return ObjectId.Null;
        }

        private static object[] BuildReflectionArguments(ParameterInfo[] parameters, Database database, CivilDocument civilDocument, string name, ObjectId relatedId, Point3d point, ObjectIdCollection ids)
        {
            var arguments = new object[parameters.Length];
            for (int index = 0; index < parameters.Length; index++)
            {
                Type type = parameters[index].ParameterType;
                string parameterName = (parameters[index].Name ?? string.Empty).ToLowerInvariant();
                if (type == typeof(Database)) arguments[index] = database;
                else if (type == typeof(CivilDocument)) arguments[index] = civilDocument;
                else if (type == typeof(string)) arguments[index] = name;
                else if (type == typeof(ObjectIdCollection) && ids != null) arguments[index] = ids;
                else if (type == typeof(ObjectId)) arguments[index] = relatedId;
                else if (type == typeof(Point3d)) arguments[index] = point;
                else if (type == typeof(double))
                {
                    if (parameterName.IndexOf("mid", StringComparison.OrdinalIgnoreCase) >= 0) arguments[index] = 1.0;
                    else arguments[index] = 0.0;
                }
                else if (type == typeof(bool)) arguments[index] = true;
                else if (parameters[index].HasDefaultValue) arguments[index] = parameters[index].DefaultValue;
                else return null;
            }
            return arguments;
        }

        private static ObjectId FindNamedCivilObject(Database database, CivilDocument civilDocument, string collectionMethod, string name)
        {
            MethodInfo method = civilDocument.GetType().GetMethod(collectionMethod, BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            object values = method == null ? null : method.Invoke(civilDocument, null);
            IEnumerable<ObjectId> ids = values as IEnumerable<ObjectId>;
            if (ids == null) return ObjectId.Null;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids)
                {
                    DBObject value = transaction.GetObject(id, OpenMode.ForRead, false);
                    PropertyInfo property = value.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
                    string current = property == null ? string.Empty : Convert.ToString(property.GetValue(value, null), CultureInfo.CurrentCulture);
                    if (string.Equals(current, name, StringComparison.OrdinalIgnoreCase)) return id;
                }
            }
            return ObjectId.Null;
        }

        private static void TrySetProperty(object target, string name, object value)
        {
            if (target == null) return;
            PropertyInfo property = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property == null || !property.CanWrite) return;
            try { property.SetValue(target, value, null); } catch { }
        }

        private static bool EnsurePlatformLayout(Database database, Transaction transaction, string layoutName, string title)
        {
            LayoutManager manager = LayoutManager.Current;
            ObjectId layoutId;
            bool created = false;
            try { layoutId = manager.GetLayoutId(layoutName); }
            catch { layoutId = ObjectId.Null; }
            if (layoutId.IsNull)
            {
                try { layoutId = manager.CreateLayout(layoutName); created = true; }
                catch { return false; }
            }
            Layout layout = transaction.GetObject(layoutId, OpenMode.ForRead, false) as Layout;
            if (layout == null) return created;
            BlockTableRecord paper = transaction.GetObject(layout.BlockTableRecordId, OpenMode.ForWrite, false) as BlockTableRecord;
            if (paper == null) return created;
            bool hasTitle = paper.Cast<ObjectId>().Select(id => transaction.GetObject(id, OpenMode.ForRead, false) as MText).Any(text => text != null && text.Contents.IndexOf("CE PLATFORM DRAWING", StringComparison.OrdinalIgnoreCase) >= 0);
            if (!hasTitle)
            {
                var text = new MText();
                text.SetDatabaseDefaults(database);
                text.Location = new Point3d(20.0, 20.0, 0.0);
                text.TextHeight = 5.0;
                text.Contents = "CE PLATFORM DRAWING\\P" + title + "\\PUse CE_XSCREATE on the generated model-space section lines for linked dynamic sections.";
                paper.AppendEntity(text);
                transaction.AddNewlyCreatedDBObject(text, true);
            }
            return created;
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

        private static ObjectId ResolveHandle(Database database, string handle)
        {
            long value;
            if (string.IsNullOrWhiteSpace(handle) || !long.TryParse(handle, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)) return ObjectId.Null;
            try { return database.GetObjectId(false, new Handle(value), 0); } catch { return ObjectId.Null; }
        }

        private static BlockTableRecord GetModelSpace(Database database, Transaction transaction, OpenMode mode)
        {
            return transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(database), mode, false) as BlockTableRecord;
        }

        private static ObjectId GetOrCreateLayer(Database database, Transaction transaction, string name)
        {
            LayerTable table = transaction.GetObject(database.LayerTableId, OpenMode.ForRead, false) as LayerTable;
            if (table.Has(name)) return table[name];
            table.UpgradeOpen();
            var layer = new LayerTableRecord { Name = name };
            ObjectId id = table.Add(layer);
            transaction.AddNewlyCreatedDBObject(layer, true);
            return id;
        }

        private static string SafeName(string value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        private static double PlanDistance(Point3d first, Point3d second)
        {
            double dx = first.X - second.X, dy = first.Y - second.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static void Dispose(DBObjectCollection collection)
        {
            if (collection == null) return;
            foreach (DBObject item in collection) if (item != null) item.Dispose();
        }

        private static Document ActiveDocument()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
        }

        private sealed class StepRelation
        {
            public StepRelation(string sourceHandle, double horizontalOffset, double verticalOffset, int sequence)
            {
                SourceHandle = sourceHandle; HorizontalOffset = horizontalOffset; VerticalOffset = verticalOffset; Sequence = sequence;
            }
            public string SourceHandle { get; private set; }
            public double HorizontalOffset { get; private set; }
            public double VerticalOffset { get; private set; }
            public int Sequence { get; private set; }
        }

        private sealed class DrapeRelation
        {
            public string SourceHandle { get; set; }
            public string SurfaceHandle { get; set; }
            public double VerticalOffset { get; set; }
            public int Sequence { get; set; }
            public bool IncludeIntermediate { get; set; }
        }

        private sealed class DrapeSnapshot
        {
            public DrapeSnapshot(string childHandle, DrapeRelation relation) { ChildHandle = childHandle; Relation = relation; }
            public string ChildHandle { get; private set; }
            public DrapeRelation Relation { get; private set; }
        }

        private sealed class TableLink
        {
            public string Type { get; set; }
            public List<string> SourceHandles { get; set; }
            public string NgSurfaceHandle { get; set; }
            public string DesignSurfaceHandle { get; set; }
            public double GridSpacing { get; set; }
            public double TextHeight { get; set; }
        }

        private sealed class PlatformRow
        {
            public string Name { get; set; }
            public double Area { get; set; }
            public double Perimeter { get; set; }
            public double MinElevation { get; set; }
            public double MaxElevation { get; set; }
        }

        private sealed class CutFillRow
        {
            public string Name { get; set; }
            public double Area { get; set; }
            public double Cut { get; set; }
            public double Fill { get; set; }
            public double Grid { get; set; }
            public int Samples { get; set; }
        }
    }

    /// <summary>
    /// Lightweight deferred platform refresh. It is initialised the first time a
    /// platform workflow is opened and coalesces drawing/surface edits until Civil
    /// 3D is idle, avoiding model changes from database event callbacks.
    /// </summary>
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
            AcApplication.DocumentManager.DocumentActivated += OnDocumentActivated;
            AcApplication.DocumentManager.DocumentCreated += OnDocumentActivated;
            AcApplication.DocumentManager.DocumentToBeDestroyed += OnDocumentDestroyed;
            AcApplication.Idle += OnIdle;
            Attach(AcApplication.DocumentManager.MdiActiveDocument);
        }

        internal static void Queue()
        {
            if (_busy) return;
            _pending = true;
            _lastChangeUtc = DateTime.UtcNow;
        }

        private static void OnDocumentActivated(object sender, DocumentCollectionEventArgs e)
        {
            Attach(e == null ? null : e.Document);
        }

        private static void OnDocumentDestroyed(object sender, DocumentCollectionEventArgs e)
        {
            if (e != null && ReferenceEquals(e.Document, _document)) Detach();
        }

        private static void Attach(Document document)
        {
            if (ReferenceEquals(document, _document)) return;
            Detach();
            _document = document;
            _database = document == null ? null : document.Database;
            if (_database == null) return;
            _database.ObjectModified += OnChanged;
            _database.ObjectErased += OnErased;
        }

        private static void Detach()
        {
            if (_database != null)
            {
                _database.ObjectModified -= OnChanged;
                _database.ObjectErased -= OnErased;
            }
            _database = null;
            _document = null;
        }

        private static void OnChanged(object sender, ObjectEventArgs e)
        {
            if (_busy || e == null || e.DBObject == null) return;
            if (e.DBObject is CivilSurface || e.DBObject is CivilFeatureLine || e.DBObject is Table) Queue();
        }

        private static void OnErased(object sender, ObjectErasedEventArgs e)
        {
            if (_busy) return;
            Queue();
        }

        private static void OnIdle(object sender, EventArgs e)
        {
            Document active = AcApplication.DocumentManager.MdiActiveDocument;
            Attach(active);
            if (!_pending || _busy || active == null || (DateTime.UtcNow - _lastChangeUtc).TotalSeconds < 1.5) return;
            string commands = Convert.ToString(AcApplication.GetSystemVariable("CMDNAMES"), CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(commands)) return;
            int commandActive = Convert.ToInt32(AcApplication.GetSystemVariable("CMDACTIVE"), CultureInfo.InvariantCulture);
            if (commandActive != 0) return;
            _busy = true;
            try
            {
                active.Database.DisableUndoRecording(true);
                PlatformProductionCommands.RefreshAll(active);
                _pending = false;
            }
            catch { }
            finally
            {
                try { active.Database.DisableUndoRecording(false); } catch { }
                _busy = false;
            }
        }
    }
}

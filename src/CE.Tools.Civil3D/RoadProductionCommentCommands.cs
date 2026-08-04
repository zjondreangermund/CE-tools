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
using CivilPolylineOptions = Autodesk.Civil.DatabaseServices.PolylineOptions;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

[assembly: CommandClass(typeof(CETools.Civil3D.RoadProductionCommentCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Batch road-production workflows requested in the active comments. Selected
    /// open polylines become sequential road alignments, generated alignments can
    /// receive EG profiles/profile views from a selected surface, and corridors are
    /// created through the compatible Civil 3D CorridorCollection.Add overload.
    /// Project Style Centre selections are used when they resolve in the drawing.
    /// </summary>
    public sealed class RoadProductionCommentCommands
    {
        private const string RegAppName = "CE_ROAD_PRODUCTION";
        private const string RootDictionaryName = "CE_TOOLS";
        private const string StyleRecordName = "PROJECT_STYLE_SELECTION";
        private const string DefaultAlignmentLayer = "CE-ROAD-ALIGNMENT";
        private const string DefaultProfileLayer = "CE-ROAD-PROFILE";

        [CommandMethod("CE_TOOLS", "CE_ROADPRODUCTION", CommandFlags.Modal | CommandFlags.Redraw)]
        public void RoadProduction()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            DisciplineWorkflowDialogs.SelectAndRun(
                document,
                "CE Tools - Road Production",
                "Create road design objects in sequence using the active drawing styles and CE Project Style Centre choices.",
                new List<DisciplineWorkflowAction>
                {
                    RoadAction("Create road alignments", "CE_ROADALIGN", "Create sequential linked road alignments from selected polylines.", "1 — Alignments"),
                    RoadAction("Create road profiles", "CE_ROADPROFILES", "Create existing-ground profiles and ordered profile views.", "2 — Profiles"),
                    RoadAction("Create CE road assembly", "CE_ASSEMBLYCREATE", "Create the Civil 3D roadway assembly used by corridor regions.", "3 — Assembly"),
                    RoadAction("Assembly workflow", "CE_ASSEMBLYTOOLS", "Create, review and select project assemblies.", "3 — Assembly"),
                    RoadAction("Create road corridors", "CE_ROADCORRIDORS", "Create one corridor for every CE road alignment/profile pair.", "4 — Corridors"),
                    RoadAction("Corridor baselines and regions", "CE_CORBASEUI", "Review generated corridor baselines and regions.", "4 — Corridors"),
                    RoadAction("Rebuild selected corridors", "CE_CORREBUILDX", "Rebuild selected road corridors.", "4 — Corridors"),
                    RoadAction("Create dynamic intersections", "CE_INTCREATE", "Create linked road intersection output.", "5 — Intersections"),
                    RoadAction("Project Style Centre", "CE_PROJECTSTYLES", "Select road alignment, profile, assembly, corridor and code-set styles.", "6 — Configuration"),
                    RoadAction("Road production information", "CE_ROADPRODUCTIONINFO", "Review alignments, profiles, corridors and project styles.", "7 — Review"),
                    RoadAction("All profiles report", "CE_PROFILEREPORT2", "Review all generated profiles and profile views.", "7 — Review"),
                    RoadAction("Road BOQ", "CE_BOQROAD", "Create the road bill of quantities in Excel format.", "8 — Production"),
                    RoadAction("Road design report", "CE_REPORTROAD", "Generate the road design report.", "8 — Production"),
                    RoadAction("Refresh all linked model data", "CE_REFRESHALL", "Refresh road annotations, schedules, BOQs and linked outputs.", "9 — Refresh")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_ROADALIGN", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void CreateRoadAlignments()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (civilDocument == null)
            {
                document.Editor.WriteMessage("\nCE_ROADALIGN cancelled. No active Civil 3D document is available.");
                return;
            }

            PromptSelectionResult selection = GetSelection(
                document.Editor,
                "\nSelect open lightweight polylines for sequential road alignments: ");
            if (selection.Status != PromptStatus.OK) return;
            PromptResult prefixResult = document.Editor.GetString(
                new PromptStringOptions("\nRoad alignment prefix <RD>: ")
                {
                    AllowSpaces = false,
                    DefaultValue = "RD",
                    UseDefaultValue = true
                });
            if (prefixResult.Status != PromptStatus.OK) return;
            PromptIntegerResult startResult = document.Editor.GetInteger(
                new PromptIntegerOptions("\nStarting road number <1>: ")
                {
                    AllowNegative = false,
                    AllowZero = false,
                    DefaultValue = 1,
                    LowerLimit = 1,
                    UseDefaultValue = true
                });
            if (startResult.Status != PromptStatus.OK) return;

            List<RoadPolylineSource> sources = ReadPolylineSources(document.Database, selection);
            if (sources.Count == 0)
            {
                document.Editor.WriteMessage("\nCE_ROADALIGN cancelled. No open lightweight polylines were selected.");
                return;
            }
            ProjectRoadStyles styles = ResolveRoadStyles(document, civilDocument);
            var previewRows = new List<IList<string>>();
            for (int index = 0; index < sources.Count; index++)
            {
                previewRows.Add(new List<string>
                {
                    BuildRoadName(prefixResult.StringResult, startResult.Value + index),
                    sources[index].Layer,
                    sources[index].Length.ToString("N3", CultureInfo.CurrentCulture),
                    sources[index].Handle
                });
            }
            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - Road Alignment Preview",
                string.Format(
                    CultureInfo.CurrentCulture,
                    "Roads={0}; alignment style={1}; label set={2}; source polylines are retained.",
                    sources.Count,
                    styles.AlignmentStyleName,
                    styles.AlignmentLabelSetName),
                new List<string> { "Road", "Source Layer", "Length", "Source Handle" },
                previewRows,
                "CE TOOLS ROAD ALIGNMENT PREVIEW");
            if (!Confirm(document.Editor, "Create these road alignments")) return;

            int created = 0;
            var createdRows = new List<IList<string>>();
            try
            {
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    EnsureRegApp(document.Database, transaction);
                    ObjectId layerId = GetOrCreateLayer(
                        document.Database,
                        transaction,
                        styles.AlignmentLayer,
                        DefaultAlignmentLayer);
                    BlockTable blockTable = transaction.GetObject(
                        document.Database.BlockTableId,
                        OpenMode.ForRead,
                        false) as BlockTable;
                    BlockTableRecord modelSpace = blockTable == null
                        ? null
                        : transaction.GetObject(
                            blockTable[BlockTableRecord.ModelSpace],
                            OpenMode.ForWrite,
                            false) as BlockTableRecord;
                    if (modelSpace == null) throw new InvalidOperationException("Model space could not be opened.");

                    var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (ObjectId alignmentId in civilDocument.GetAlignmentIds())
                    {
                        CivilAlignment existing = transaction.GetObject(alignmentId, OpenMode.ForRead, false) as CivilAlignment;
                        if (existing != null) reserved.Add(existing.Name);
                    }

                    for (int index = 0; index < sources.Count; index++)
                    {
                        RoadPolylineSource source = sources[index];
                        Polyline original = transaction.GetObject(source.ObjectId, OpenMode.ForRead, false) as Polyline;
                        if (original == null || original.Closed || original.NumberOfVertices < 2) continue;
                        Polyline clone = original.Clone() as Polyline;
                        if (clone == null) continue;
                        clone.SetDatabaseDefaults(document.Database);
                        clone.LayerId = layerId;
                        modelSpace.AppendEntity(clone);
                        transaction.AddNewlyCreatedDBObject(clone, true);

                        string requested = BuildRoadName(prefixResult.StringResult, startResult.Value + index);
                        string name = UniqueName(requested, reserved);
                        var options = new CivilPolylineOptions
                        {
                            AddCurvesBetweenTangents = false,
                            EraseExistingEntities = true,
                            PlineId = clone.ObjectId
                        };
                        ObjectId alignmentId = CivilAlignment.Create(
                            civilDocument,
                            options,
                            name,
                            ObjectId.Null,
                            layerId,
                            styles.AlignmentStyleId,
                            styles.AlignmentLabelSetId);
                        CivilAlignment alignment = transaction.GetObject(alignmentId, OpenMode.ForWrite, false) as CivilAlignment;
                        if (alignment == null) throw new InvalidOperationException("Civil 3D did not return the created road alignment.");
                        alignment.Description = string.Format(
                            CultureInfo.InvariantCulture,
                            "CE road | source={0} | alignment-style={1} | labels={2}",
                            source.Handle,
                            styles.AlignmentStyleName,
                            styles.AlignmentLabelSetName);
                        WriteTag(alignment, "Alignment", name, source.Handle);
                        createdRows.Add(new List<string>
                        {
                            name,
                            source.Layer,
                            alignment.Length.ToString("N3", CultureInfo.CurrentCulture),
                            styles.AlignmentStyleName,
                            styles.AlignmentLabelSetName
                        });
                        created++;
                    }
                    transaction.Commit();
                }
                document.Editor.Regen();
                GridReportPresenter.ShowReportAndOfferTable(
                    document,
                    "CE Tools - Road Alignments Created",
                    "Sequential road alignments were created from clones; original source polylines remain in the drawing.",
                    new List<string> { "Road", "Source Layer", "Length", "Alignment Style", "Label Set" },
                    createdRows,
                    "CE TOOLS ROAD ALIGNMENT REGISTER");
                document.Editor.WriteMessage("\nCE_ROADALIGN complete. Road alignments created={0}.", created);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage("\nCE_ROADALIGN failed. No alignment transaction was committed. {0}", exception.Message);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_ROADPROFILES", CommandFlags.Modal | CommandFlags.Redraw)]
        public void CreateRoadProfiles()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (civilDocument == null) return;
            List<RoadAlignmentRecord> alignments = ReadRoadAlignments(document.Database, civilDocument);
            if (alignments.Count == 0)
            {
                document.Editor.WriteMessage("\nCE_ROADPROFILES: no CE road alignments were found. Run CE_ROADALIGN first.");
                return;
            }
            List<CivilObjectChoice> surfaces = ReadSurfaces(document, civilDocument);
            if (surfaces.Count == 0)
            {
                document.Editor.WriteMessage("\nCE_ROADPROFILES: no Civil 3D surfaces were found.");
                return;
            }
            var picker = new CivilObjectPickerWindow(
                "CE Tools - Road Existing-Ground Surface",
                "Select the surface used to create EG profiles for every CE road alignment.",
                surfaces);
            AcApplication.ShowModalWindow(picker);
            if (!picker.Accepted || picker.SelectedChoice == null) return;
            PromptPointResult baseResult = document.Editor.GetPoint(
                "\nPick base point for the first road profile view: ");
            if (baseResult.Status != PromptStatus.OK) return;
            PromptIntegerResult columnsResult = document.Editor.GetInteger(
                new PromptIntegerOptions("\nProfile-view columns <2>: ")
                {
                    AllowNegative = false,
                    AllowZero = false,
                    DefaultValue = 2,
                    LowerLimit = 1,
                    UseDefaultValue = true
                });
            if (columnsResult.Status != PromptStatus.OK) return;
            PromptDoubleResult horizontalResult = document.Editor.GetDouble(
                new PromptDoubleOptions("\nHorizontal profile-view spacing <250>: ")
                {
                    AllowNegative = false,
                    AllowZero = false,
                    DefaultValue = 250.0,
                    UseDefaultValue = true
                });
            if (horizontalResult.Status != PromptStatus.OK) return;
            PromptDoubleResult verticalResult = document.Editor.GetDouble(
                new PromptDoubleOptions("\nVertical profile-view spacing <150>: ")
                {
                    AllowNegative = false,
                    AllowZero = false,
                    DefaultValue = 150.0,
                    UseDefaultValue = true
                });
            if (verticalResult.Status != PromptStatus.OK) return;

            ProjectRoadStyles styles = ResolveRoadStyles(document, civilDocument);
            var preview = alignments.Select(record => (IList<string>)new List<string>
            {
                record.Name,
                picker.SelectedChoice.Name,
                styles.ProfileStyleName,
                styles.ProfileViewStyleName,
                styles.BandSetStyleName
            }).ToList();
            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - Road Profile Preview",
                "An EG profile and profile view will be created for every CE road alignment.",
                new List<string> { "Road", "Surface", "Profile Style", "View Style", "Band Set" },
                preview,
                "CE TOOLS ROAD PROFILE PREVIEW");
            if (!Confirm(document.Editor, "Create these road EG profiles and profile views")) return;

            int profiles = 0;
            int views = 0;
            var rows = new List<IList<string>>();
            Point3d basePoint = baseResult.Value.TransformBy(document.Editor.CurrentUserCoordinateSystem);
            try
            {
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    EnsureRegApp(document.Database, transaction);
                    ObjectId layerId = GetOrCreateLayer(
                        document.Database,
                        transaction,
                        styles.ProfileLayer,
                        DefaultProfileLayer);
                    BlockTable blockTable = transaction.GetObject(document.Database.BlockTableId, OpenMode.ForRead, false) as BlockTable;
                    BlockTableRecord modelSpace = blockTable == null
                        ? null
                        : transaction.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite, false) as BlockTableRecord;
                    if (modelSpace == null) throw new InvalidOperationException("Model space could not be opened.");

                    for (int index = 0; index < alignments.Count; index++)
                    {
                        RoadAlignmentRecord road = alignments[index];
                        string profileName = UniqueCivilName(
                            road.Name + "-EG",
                            road.Name + "-EG-" + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture));
                        ObjectId profileId = CreateSurfaceProfileByReflection(
                            profileName,
                            road.AlignmentId,
                            picker.SelectedChoice.ObjectId,
                            layerId,
                            styles.ProfileStyleId,
                            styles.ProfileLabelSetId);
                        DBObject profile = transaction.GetObject(profileId, OpenMode.ForWrite, false);
                        WriteTag(profile, "Profile", road.Name, road.SourceHandle);
                        profiles++;

                        int row = index / columnsResult.Value;
                        int column = index % columnsResult.Value;
                        Point3d location = new Point3d(
                            basePoint.X + (column * horizontalResult.Value),
                            basePoint.Y - (row * verticalResult.Value),
                            basePoint.Z);
                        string viewName = UniqueCivilName(
                            road.Name + "-PROFILE",
                            road.Name + "-PROFILE-" + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture));
                        ObjectId viewId = CreateProfileViewByReflection(
                            viewName,
                            road.AlignmentId,
                            location,
                            styles.BandSetStyleId,
                            styles.ProfileViewStyleId);
                        DBObject view = transaction.GetObject(viewId, OpenMode.ForWrite, false);
                        ProfileStyleLinker.Apply(
                            view,
                            styles.ProfileViewStyleId,
                            styles.BandSetStyleId);
                        WriteTag(view, "ProfileView", road.Name, road.SourceHandle);
                        views++;

                        var title = new MText();
                        title.SetDatabaseDefaults(document.Database);
                        title.LayerId = layerId;
                        title.Location = location + new Vector3d(0.0, 8.0, 0.0);
                        title.Attachment = AttachmentPoint.BottomLeft;
                        title.TextHeight = 2.0;
                        title.Contents = string.Join(
                            "\\P",
                            road.Name + " ROAD PROFILE",
                            "SURFACE: " + picker.SelectedChoice.Name,
                            "PROFILE: " + styles.ProfileStyleName,
                            "VIEW: " + styles.ProfileViewStyleName,
                            "BANDS: " + styles.BandSetStyleName);
                        WriteTag(title, "ProfileTitle", road.Name, road.SourceHandle);
                        modelSpace.AppendEntity(title);
                        transaction.AddNewlyCreatedDBObject(title, true);
                        rows.Add(new List<string>
                        {
                            road.Name,
                            profileName,
                            viewName,
                            picker.SelectedChoice.Name,
                            location.X.ToString("N3", CultureInfo.CurrentCulture),
                            location.Y.ToString("N3", CultureInfo.CurrentCulture)
                        });
                    }
                    transaction.Commit();
                }
                document.Editor.Regen();
                GridReportPresenter.ShowReportAndOfferTable(
                    document,
                    "CE Tools - Road Profiles Created",
                    string.Format(CultureInfo.CurrentCulture, "Profiles={0}; profile views={1}.", profiles, views),
                    new List<string> { "Road", "EG Profile", "Profile View", "Surface", "View X", "View Y" },
                    rows,
                    "CE TOOLS ROAD PROFILE REGISTER");
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage("\nCE_ROADPROFILES failed. No profile transaction was committed. {0}", exception.Message);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_ROADCORRIDORS", CommandFlags.Modal | CommandFlags.Redraw)]
        public void CreateRoadCorridors()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (civilDocument == null) return;
            List<RoadAlignmentRecord> alignments = ReadRoadAlignments(document.Database, civilDocument);
            if (alignments.Count == 0)
            {
                document.Editor.WriteMessage("\nCE_ROADCORRIDORS: no CE road alignments were found.");
                return;
            }
            List<CivilObjectChoice> assemblies = ReadCivilChoices(
                document,
                civilDocument,
                "GetAssemblyIds",
                "Assembly");
            if (assemblies.Count == 0)
            {
                string choice = DisciplineWorkflowDialogs.SelectWorkflow(
                    "CE Tools - Road Corridor Assembly Required",
                    "No Civil 3D assembly exists. Create a CE road assembly now or cancel without changing the drawing.",
                    new List<DisciplineWorkflowAction>
                    {
                        RoadAction("Create CE road assembly now", "Create", "Choose the assembly type, name and insertion point, then continue corridor creation.", "1 — Assembly"),
                        RoadAction("Cancel corridor creation", "Cancel", "Close without creating an assembly or corridor.", "2 — Cancel")
                    });
                if (!string.Equals(choice, "Create", StringComparison.OrdinalIgnoreCase)) return;
                ObjectId createdAssembly;
                try
                {
                    createdAssembly = CeAssemblyCommands.CreateRoadAssemblyInteractively(document);
                }
                catch (System.Exception exception)
                {
                    document.Editor.WriteMessage(
                        "\nCE_ROADCORRIDORS stopped while creating the required assembly. {0}",
                        exception.Message);
                    return;
                }
                if (createdAssembly.IsNull) return;
                assemblies = ReadCivilChoices(
                    document,
                    civilDocument,
                    "GetAssemblyIds",
                    "Assembly");
                if (assemblies.Count == 0)
                {
                    document.Editor.WriteMessage(
                        "\nCE_ROADCORRIDORS stopped. Civil 3D did not expose the newly created assembly.");
                    return;
                }
            }
            var picker = new CivilObjectPickerWindow(
                "CE Tools - Road Assembly",
                "Select the assembly used for each generated road corridor.",
                assemblies);
            AcApplication.ShowModalWindow(picker);
            if (!picker.Accepted || picker.SelectedChoice == null) return;

            List<RoadCorridorPlan> plans = ReadCorridorPlans(document.Database, alignments);
            if (plans.Count == 0)
            {
                document.Editor.WriteMessage("\nCE_ROADCORRIDORS: no CE road alignment has an EG profile. Run CE_ROADPROFILES first.");
                return;
            }
            var preview = plans.Select(plan => (IList<string>)new List<string>
            {
                plan.RoadName,
                plan.ProfileName,
                picker.SelectedChoice.Name,
                plan.SourceHandle
            }).ToList();
            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - Road Corridor Preview",
                "A corridor will be created for each CE road alignment/profile pair using the selected assembly.",
                new List<string> { "Road", "Profile", "Assembly", "Source Handle" },
                preview,
                "CE TOOLS ROAD CORRIDOR PREVIEW");
            if (!Confirm(document.Editor, "Create these road corridors")) return;

            int created = 0;
            var rows = new List<IList<string>>();
            try
            {
                object corridorCollection = ReadProperty(civilDocument, "CorridorCollection");
                if (corridorCollection == null)
                    throw new InvalidOperationException("CivilDocument.CorridorCollection is unavailable in this Civil 3D build.");
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    EnsureRegApp(document.Database, transaction);
                    foreach (RoadCorridorPlan plan in plans)
                    {
                        string name = UniqueCivilName(
                            plan.RoadName + "-CORRIDOR",
                            plan.RoadName + "-CORRIDOR-" + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture));
                        ObjectId corridorId = AddCorridorByReflection(
                            corridorCollection,
                            name,
                            plan.RoadName + "-BASELINE",
                            plan.AlignmentId,
                            plan.ProfileId,
                            picker.SelectedChoice.ObjectId);
                        DBObject corridor = transaction.GetObject(corridorId, OpenMode.ForWrite, false);
                        WriteTag(corridor, "Corridor", plan.RoadName, plan.SourceHandle);
                        InvokeIfAvailable(corridor, "Rebuild");
                        rows.Add(new List<string>
                        {
                            name,
                            plan.RoadName,
                            plan.ProfileName,
                            picker.SelectedChoice.Name,
                            corridorId.Handle.ToString()
                        });
                        created++;
                    }
                    transaction.Commit();
                }
                document.Editor.Regen();
                GridReportPresenter.ShowReportAndOfferTable(
                    document,
                    "CE Tools - Road Corridors Created",
                    "Road corridors were created through the compatible Civil 3D CorridorCollection.Add overload and rebuilt where supported.",
                    new List<string> { "Corridor", "Road", "Profile", "Assembly", "Handle" },
                    rows,
                    "CE TOOLS ROAD CORRIDOR REGISTER");
                document.Editor.WriteMessage("\nCE_ROADCORRIDORS complete. Corridors created={0}.", created);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage("\nCE_ROADCORRIDORS failed. No corridor transaction was committed. {0}", exception.Message);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_ROADPRODUCTIONINFO", CommandFlags.Modal | CommandFlags.Redraw)]
        public void RoadProductionInformation()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (civilDocument == null) return;
            List<RoadAlignmentRecord> alignments = ReadRoadAlignments(document.Database, civilDocument);
            List<RoadCorridorPlan> profiles = ReadCorridorPlans(document.Database, alignments);
            int corridorCount = CountTaggedObjects(document.Database, "Corridor");
            ProjectRoadStyles styles = ResolveRoadStyles(document, civilDocument);
            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - Road Production Information",
                "Current generated road objects and resolved project styles.",
                new List<string> { "Property", "Value" },
                new List<IList<string>>
                {
                    new List<string> { "CE road alignments", alignments.Count.ToString(CultureInfo.InvariantCulture) },
                    new List<string> { "CE road alignments with EG profiles", profiles.Count.ToString(CultureInfo.InvariantCulture) },
                    new List<string> { "CE road corridors", corridorCount.ToString(CultureInfo.InvariantCulture) },
                    new List<string> { "Alignment style", styles.AlignmentStyleName },
                    new List<string> { "Alignment label set", styles.AlignmentLabelSetName },
                    new List<string> { "Profile style", styles.ProfileStyleName },
                    new List<string> { "Profile label set", styles.ProfileLabelSetName },
                    new List<string> { "Profile view style", styles.ProfileViewStyleName },
                    new List<string> { "Profile view band set", styles.BandSetStyleName },
                    new List<string> { "Alignment layer", styles.AlignmentLayer },
                    new List<string> { "Profile layer", styles.ProfileLayer }
                },
                "CE TOOLS ROAD PRODUCTION INFORMATION");
        }

        private static List<RoadPolylineSource> ReadPolylineSources(
            Database database,
            PromptSelectionResult selection)
        {
            var result = new List<RoadPolylineSource>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject selected in selection.Value)
                {
                    Polyline polyline = selected == null || selected.ObjectId.IsNull
                        ? null
                        : transaction.GetObject(selected.ObjectId, OpenMode.ForRead, false) as Polyline;
                    if (polyline == null || polyline.Closed || polyline.NumberOfVertices < 2) continue;
                    result.Add(new RoadPolylineSource
                    {
                        ObjectId = polyline.ObjectId,
                        Handle = polyline.Handle.ToString(),
                        Layer = polyline.Layer,
                        Length = polyline.Length
                    });
                }
            }
            return result
                .OrderBy(item => item.Layer, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(item => item.ObjectId.Handle.Value)
                .ToList();
        }

        private static List<RoadAlignmentRecord> ReadRoadAlignments(
            Database database,
            CivilDocument civilDocument)
        {
            var result = new List<RoadAlignmentRecord>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId alignmentId in civilDocument.GetAlignmentIds())
                {
                    CivilAlignment alignment = transaction.GetObject(alignmentId, OpenMode.ForRead, false) as CivilAlignment;
                    string type;
                    string road;
                    string source;
                    if (alignment != null && TryReadTag(alignment, out type, out road, out source) && type == "Alignment")
                    {
                        result.Add(new RoadAlignmentRecord
                        {
                            AlignmentId = alignmentId,
                            Name = alignment.Name,
                            SourceHandle = source
                        });
                    }
                }
            }
            return result.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        private static List<RoadCorridorPlan> ReadCorridorPlans(
            Database database,
            IEnumerable<RoadAlignmentRecord> roads)
        {
            var result = new List<RoadCorridorPlan>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (RoadAlignmentRecord road in roads)
                {
                    CivilAlignment alignment = transaction.GetObject(road.AlignmentId, OpenMode.ForRead, false) as CivilAlignment;
                    if (alignment == null) continue;
                    foreach (ObjectId profileId in alignment.GetProfileIds())
                    {
                        DBObject profile = transaction.GetObject(profileId, OpenMode.ForRead, false);
                        string type;
                        string taggedRoad;
                        string source;
                        if (!TryReadTag(profile, out type, out taggedRoad, out source) || type != "Profile") continue;
                        result.Add(new RoadCorridorPlan
                        {
                            RoadName = road.Name,
                            AlignmentId = road.AlignmentId,
                            ProfileId = profileId,
                            ProfileName = Convert.ToString(ReadProperty(profile, "Name"), CultureInfo.CurrentCulture),
                            SourceHandle = road.SourceHandle
                        });
                        break;
                    }
                }
            }
            return result;
        }

        private static List<CivilObjectChoice> ReadSurfaces(Document document, CivilDocument civilDocument)
        {
            var result = new List<CivilObjectChoice>();
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId surfaceId in civilDocument.GetSurfaceIds())
                {
                    CivilSurface surface = transaction.GetObject(surfaceId, OpenMode.ForRead, false) as CivilSurface;
                    if (surface != null)
                        result.Add(new CivilObjectChoice(surfaceId, surface.Name, surface.GetType().Name));
                }
            }
            return result.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        private static List<CivilObjectChoice> ReadCivilChoices(
            Document document,
            CivilDocument civilDocument,
            string methodName,
            string context)
        {
            var result = new List<CivilObjectChoice>();
            IEnumerable ids = InvokeEnumerable(civilDocument, methodName);
            if (ids == null) return result;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (object item in ids)
                {
                    if (!(item is ObjectId)) continue;
                    ObjectId id = (ObjectId)item;
                    if (id.IsNull) continue;
                    DBObject value = transaction.GetObject(id, OpenMode.ForRead, false);
                    result.Add(new CivilObjectChoice(
                        id,
                        Convert.ToString(ReadProperty(value, "Name"), CultureInfo.CurrentCulture),
                        context));
                }
            }
            return result.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        private static ProjectRoadStyles ResolveRoadStyles(
            Document document,
            CivilDocument civilDocument)
        {
            Dictionary<string, string> selection = ReadProjectStyleSelection(document.Database);
            var result = new ProjectRoadStyles
            {
                AlignmentLayer = "CE-ROAD-ALIGNMENT",
                ProfileLayer = "CE-ROAD-PROFILE"
            };
            string actualName;
            result.AlignmentStyleId = ResolveStyle(
                document.Database,
                ReadPropertyPath(civilDocument, "Styles", "AlignmentStyles"),
                Value(selection, "Alignment Style"),
                out actualName);
            result.AlignmentStyleName = actualName;
            result.AlignmentLabelSetId = ResolveStyle(
                document.Database,
                ReadPropertyPath(civilDocument, "Styles", "LabelSetStyles", "AlignmentLabelSetStyles"),
                Value(selection, "Alignment Label Set Style"),
                out actualName);
            result.AlignmentLabelSetName = actualName;
            result.ProfileStyleId = ResolveStyle(
                document.Database,
                ReadPropertyPath(civilDocument, "Styles", "ProfileStyles"),
                Value(selection, "Profile Style"),
                out actualName);
            result.ProfileStyleName = actualName;
            result.ProfileLabelSetId = ResolveStyle(
                document.Database,
                ReadPropertyPath(civilDocument, "Styles", "LabelSetStyles", "ProfileLabelSetStyles"),
                Value(selection, "Profile Label Set Style"),
                out actualName);
            result.ProfileLabelSetName = actualName;
            result.ProfileViewStyleId = ResolveStyle(
                document.Database,
                ReadPropertyPath(civilDocument, "Styles", "ProfileViewStyles"),
                Value(selection, "Profile View Style"),
                out actualName);
            result.ProfileViewStyleName = actualName;
            object bandCollection = ReadPropertyPath(civilDocument, "Styles", "BandSetStyles", "ProfileViewBandSetStyles") ??
                                    ReadPropertyPath(civilDocument, "Styles", "ProfileViewBandSetStyles");
            result.BandSetStyleId = ResolveStyle(
                document.Database,
                bandCollection,
                Value(selection, "Profile View Band Set Style"),
                out actualName);
            result.BandSetStyleName = actualName;
            return result;
        }

        private static ObjectId ResolveStyle(
            Database database,
            object collection,
            string preferred,
            out string actualName)
        {
            actualName = "<Drawing default>";
            IEnumerable enumerable = collection as IEnumerable;
            if (enumerable == null) throw new InvalidOperationException("A required Civil 3D style collection is unavailable.");
            ObjectId first = ObjectId.Null;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (object item in enumerable)
                {
                    if (!(item is ObjectId)) continue;
                    ObjectId id = (ObjectId)item;
                    if (id.IsNull) continue;
                    if (first.IsNull) first = id;
                    DBObject style = transaction.GetObject(id, OpenMode.ForRead, false);
                    string name = Convert.ToString(ReadProperty(style, "Name"), CultureInfo.CurrentCulture);
                    if (!string.IsNullOrWhiteSpace(preferred) &&
                        !preferred.StartsWith("<", StringComparison.Ordinal) &&
                        string.Equals(name, preferred, StringComparison.OrdinalIgnoreCase))
                    {
                        actualName = name;
                        return id;
                    }
                }
                if (!first.IsNull)
                {
                    DBObject style = transaction.GetObject(first, OpenMode.ForRead, false);
                    actualName = Convert.ToString(ReadProperty(style, "Name"), CultureInfo.CurrentCulture);
                }
            }
            if (first.IsNull) throw new InvalidOperationException("The required Civil 3D style collection is empty.");
            return first;
        }

        private static Dictionary<string, string> ReadProjectStyleSelection(Database database)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                DBDictionary namedObjects = transaction.GetObject(database.NamedObjectsDictionaryId, OpenMode.ForRead, false) as DBDictionary;
                if (namedObjects == null || !namedObjects.Contains(RootDictionaryName)) return result;
                DBDictionary root = transaction.GetObject(namedObjects.GetAt(RootDictionaryName), OpenMode.ForRead, false) as DBDictionary;
                if (root == null || !root.Contains(StyleRecordName)) return result;
                Xrecord record = transaction.GetObject(root.GetAt(StyleRecordName), OpenMode.ForRead, false) as Xrecord;
                if (record == null || record.Data == null) return result;
                foreach (TypedValue typedValue in record.Data)
                {
                    string text = typedValue.Value as string;
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    int equals = text.IndexOf('=');
                    if (equals <= 0) continue;
                    result[text.Substring(0, equals)] = text.Substring(equals + 1);
                }
            }
            return result;
        }

        private static string Value(IDictionary<string, string> values, string key)
        {
            string value;
            return values.TryGetValue(key, out value) ? value : string.Empty;
        }

        private static ObjectId GetOrCreateLayer(
            Database database,
            Transaction transaction,
            string requested,
            string fallback)
        {
            string name = string.IsNullOrWhiteSpace(requested) ? fallback : requested.Trim();
            LayerTable layers = transaction.GetObject(database.LayerTableId, OpenMode.ForRead, false) as LayerTable;
            if (layers == null) throw new InvalidOperationException("Layer table is unavailable.");
            if (layers.Has(name)) return layers[name];
            layers.UpgradeOpen();
            var layer = new LayerTableRecord { Name = name };
            ObjectId id = layers.Add(layer);
            transaction.AddNewlyCreatedDBObject(layer, true);
            return id;
        }

        private static void EnsureRegApp(Database database, Transaction transaction)
        {
            RegAppTable table = transaction.GetObject(database.RegAppTableId, OpenMode.ForRead, false) as RegAppTable;
            if (table == null || table.Has(RegAppName)) return;
            table.UpgradeOpen();
            var record = new RegAppTableRecord { Name = RegAppName };
            table.Add(record);
            transaction.AddNewlyCreatedDBObject(record, true);
        }

        private static void WriteTag(DBObject value, string type, string road, string sourceHandle)
        {
            if (value == null) return;
            value.XData = new ResultBuffer(
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, RegAppName),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, "Type=" + type),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, "Road=" + road),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, "Source=" + (sourceHandle ?? string.Empty)));
        }

        private static bool TryReadTag(DBObject value, out string type, out string road, out string source)
        {
            type = string.Empty;
            road = string.Empty;
            source = string.Empty;
            if (value == null) return false;
            ResultBuffer data = value.GetXDataForApplication(RegAppName);
            if (data == null) return false;
            foreach (TypedValue typedValue in data)
            {
                string text = typedValue.Value as string;
                if (string.IsNullOrWhiteSpace(text)) continue;
                if (text.StartsWith("Type=", StringComparison.OrdinalIgnoreCase)) type = text.Substring(5);
                else if (text.StartsWith("Road=", StringComparison.OrdinalIgnoreCase)) road = text.Substring(5);
                else if (text.StartsWith("Source=", StringComparison.OrdinalIgnoreCase)) source = text.Substring(7);
            }
            return !string.IsNullOrWhiteSpace(type);
        }

        private static ObjectId CreateSurfaceProfileByReflection(
            string profileName,
            ObjectId alignmentId,
            ObjectId surfaceId,
            ObjectId layerId,
            ObjectId profileStyleId,
            ObjectId profileLabelSetId)
        {
            Type profileType = typeof(CivilAlignment).Assembly.GetType("Autodesk.Civil.DatabaseServices.Profile", true);
            foreach (MethodInfo method in profileType
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(item => item.Name == "CreateFromSurface")
                .OrderBy(item => item.GetParameters().Length))
            {
                object[] arguments;
                if (!TryBuildProfileArguments(
                    method.GetParameters(),
                    profileName,
                    alignmentId,
                    surfaceId,
                    layerId,
                    profileStyleId,
                    profileLabelSetId,
                    out arguments)) continue;
                try
                {
                    object result = method.Invoke(null, arguments);
                    if (result is ObjectId) return (ObjectId)result;
                }
                catch (TargetInvocationException) { }
            }
            throw new InvalidOperationException("No compatible Profile.CreateFromSurface overload was found.");
        }

        private static bool TryBuildProfileArguments(
            ParameterInfo[] parameters,
            string name,
            ObjectId alignmentId,
            ObjectId surfaceId,
            ObjectId layerId,
            ObjectId styleId,
            ObjectId labelId,
            out object[] arguments)
        {
            arguments = new object[parameters.Length];
            ObjectId[] fallback = { alignmentId, surfaceId, layerId, styleId, labelId };
            int fallbackIndex = 0;
            for (int index = 0; index < parameters.Length; index++)
            {
                ParameterInfo parameter = parameters[index];
                string parameterName = (parameter.Name ?? string.Empty).ToLowerInvariant();
                if (parameter.ParameterType == typeof(string)) arguments[index] = name;
                else if (parameter.ParameterType == typeof(ObjectId))
                {
                    if (parameterName.Contains("alignment")) arguments[index] = alignmentId;
                    else if (parameterName.Contains("surface")) arguments[index] = surfaceId;
                    else if (parameterName.Contains("layer")) arguments[index] = layerId;
                    else if (parameterName.Contains("label")) arguments[index] = labelId;
                    else if (parameterName.Contains("style")) arguments[index] = styleId;
                    else if (fallbackIndex < fallback.Length) arguments[index] = fallback[fallbackIndex++];
                    else return false;
                }
                else if (parameter.HasDefaultValue) arguments[index] = parameter.DefaultValue;
                else return false;
            }
            return true;
        }

        private static ObjectId CreateProfileViewByReflection(
            string name,
            ObjectId alignmentId,
            Point3d point,
            ObjectId bandId,
            ObjectId styleId)
        {
            Type type = typeof(CivilAlignment).Assembly.GetType("Autodesk.Civil.DatabaseServices.ProfileView", true);
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(item => item.Name == "Create")
                .OrderBy(item => item.GetParameters().Length))
            {
                object[] arguments;
                if (!TryBuildProfileViewArguments(method.GetParameters(), name, alignmentId, point, bandId, styleId, out arguments)) continue;
                try
                {
                    object result = method.Invoke(null, arguments);
                    if (result is ObjectId) return (ObjectId)result;
                }
                catch (TargetInvocationException) { }
            }
            throw new InvalidOperationException("No compatible ProfileView.Create overload was found.");
        }

        private static bool TryBuildProfileViewArguments(
            ParameterInfo[] parameters,
            string name,
            ObjectId alignmentId,
            Point3d point,
            ObjectId bandId,
            ObjectId styleId,
            out object[] arguments)
        {
            arguments = new object[parameters.Length];
            ObjectId[] fallback = { alignmentId, bandId, styleId };
            int fallbackIndex = 0;
            for (int index = 0; index < parameters.Length; index++)
            {
                ParameterInfo parameter = parameters[index];
                string parameterName = (parameter.Name ?? string.Empty).ToLowerInvariant();
                if (parameter.ParameterType == typeof(string)) arguments[index] = name;
                else if (parameter.ParameterType == typeof(Point3d)) arguments[index] = point;
                else if (parameter.ParameterType == typeof(ObjectId))
                {
                    if (parameterName.Contains("alignment")) arguments[index] = alignmentId;
                    else if (parameterName.Contains("band")) arguments[index] = bandId;
                    else if (parameterName.Contains("style")) arguments[index] = styleId;
                    else if (fallbackIndex < fallback.Length) arguments[index] = fallback[fallbackIndex++];
                    else return false;
                }
                else if (parameter.HasDefaultValue) arguments[index] = parameter.DefaultValue;
                else return false;
            }
            return true;
        }

        private static ObjectId AddCorridorByReflection(
            object collection,
            string corridorName,
            string baselineName,
            ObjectId alignmentId,
            ObjectId profileId,
            ObjectId assemblyId)
        {
            System.Exception lastError = null;
            foreach (MethodInfo method in collection.GetType()
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(item => item.Name == "Add")
                .OrderBy(item => item.GetParameters().Length))
            {
                object[] arguments;
                if (!TryBuildCorridorArguments(
                    method.GetParameters(),
                    corridorName,
                    baselineName,
                    alignmentId,
                    profileId,
                    assemblyId,
                    out arguments)) continue;
                try
                {
                    object result = method.Invoke(collection, arguments);
                    if (result is ObjectId) return (ObjectId)result;
                }
                catch (TargetInvocationException exception)
                {
                    lastError = exception.InnerException ?? exception;
                }
            }
            throw new InvalidOperationException(
                "No compatible CorridorCollection.Add overload succeeded." +
                (lastError == null ? string.Empty : " " + lastError.Message));
        }

        private static bool TryBuildCorridorArguments(
            ParameterInfo[] parameters,
            string corridorName,
            string baselineName,
            ObjectId alignmentId,
            ObjectId profileId,
            ObjectId assemblyId,
            out object[] arguments)
        {
            arguments = new object[parameters.Length];
            int stringIndex = 0;
            ObjectId[] fallback = { alignmentId, profileId, assemblyId };
            int fallbackIndex = 0;
            for (int index = 0; index < parameters.Length; index++)
            {
                ParameterInfo parameter = parameters[index];
                string parameterName = (parameter.Name ?? string.Empty).ToLowerInvariant();
                if (parameter.ParameterType == typeof(string))
                {
                    if (parameterName.Contains("baseline")) arguments[index] = baselineName;
                    else if (parameterName.Contains("corridor")) arguments[index] = corridorName;
                    else arguments[index] = stringIndex++ == 0 ? corridorName : baselineName;
                }
                else if (parameter.ParameterType == typeof(ObjectId))
                {
                    if (parameterName.Contains("alignment")) arguments[index] = alignmentId;
                    else if (parameterName.Contains("profile")) arguments[index] = profileId;
                    else if (parameterName.Contains("assembly")) arguments[index] = assemblyId;
                    else if (fallbackIndex < fallback.Length) arguments[index] = fallback[fallbackIndex++];
                    else return false;
                }
                else if (parameter.ParameterType == typeof(bool)) arguments[index] = false;
                else if (parameter.HasDefaultValue) arguments[index] = parameter.DefaultValue;
                else return false;
            }
            return true;
        }

        private static string UniqueName(string requested, ISet<string> reserved)
        {
            string value = string.IsNullOrWhiteSpace(requested) ? "RD" : requested.Trim();
            string candidate = value;
            int suffix = 2;
            while (reserved.Contains(candidate)) candidate = value + "-" + suffix++.ToString(CultureInfo.InvariantCulture);
            reserved.Add(candidate);
            return candidate;
        }

        private static string UniqueCivilName(string preferred, string fallback)
        {
            return string.IsNullOrWhiteSpace(preferred) ? fallback : preferred;
        }

        private static string BuildRoadName(string prefix, int number)
        {
            string safe = string.IsNullOrWhiteSpace(prefix) ? "RD" : prefix.Trim();
            return safe + "-" + number.ToString("00", CultureInfo.InvariantCulture);
        }

        private static object ReadPropertyPath(object value, params string[] propertyNames)
        {
            object current = value;
            foreach (string propertyName in propertyNames)
            {
                current = ReadProperty(current, propertyName);
                if (current == null) return null;
            }
            return current;
        }

        private static object ReadProperty(object value, string propertyName)
        {
            if (value == null) return null;
            try
            {
                PropertyInfo property = value.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                return property == null || property.GetIndexParameters().Length != 0
                    ? null
                    : property.GetValue(value, null);
            }
            catch { return null; }
        }

        private static IEnumerable InvokeEnumerable(object value, string methodName)
        {
            if (value == null) return null;
            try
            {
                MethodInfo method = value.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                return method == null ? null : method.Invoke(value, null) as IEnumerable;
            }
            catch { return null; }
        }

        private static void InvokeIfAvailable(object value, string methodName)
        {
            if (value == null) return;
            try
            {
                MethodInfo method = value.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (method != null) method.Invoke(value, null);
            }
            catch { }
        }

        private static int CountTaggedObjects(Database database, string expectedType)
        {
            int count = 0;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTable blockTable = transaction.GetObject(database.BlockTableId, OpenMode.ForRead, false) as BlockTable;
                if (blockTable == null) return count;
                foreach (ObjectId blockId in blockTable)
                {
                    BlockTableRecord block = transaction.GetObject(blockId, OpenMode.ForRead, false) as BlockTableRecord;
                    if (block == null || block.IsFromExternalReference) continue;
                    foreach (ObjectId id in block)
                    {
                        DBObject value = transaction.GetObject(id, OpenMode.ForRead, false);
                        string type;
                        string road;
                        string source;
                        if (TryReadTag(value, out type, out road, out source) && type == expectedType) count++;
                    }
                }
            }
            return count;
        }

        private static bool Confirm(Editor editor, string message)
        {
            var options = new PromptKeywordOptions("\n" + message + "? [Yes/No] <No>: ") { AllowNone = true };
            options.Keywords.Add("Yes");
            options.Keywords.Add("No");
            PromptResult result = editor.GetKeywords(options);
            return result.Status == PromptStatus.OK && string.Equals(result.StringResult, "Yes", StringComparison.OrdinalIgnoreCase);
        }

        private static PromptSelectionResult GetSelection(Editor editor, string message)
        {
            PromptSelectionResult implied = editor.SelectImplied();
            if (implied.Status == PromptStatus.OK && implied.Value.Count > 0)
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

        private static DisciplineWorkflowAction RoadAction(
            string title,
            string command,
            string description,
            string group)
        {
            return new DisciplineWorkflowAction(title, command, description, group);
        }

        private static Document ActiveDocument()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
        }

        private sealed class RoadPolylineSource
        {
            public ObjectId ObjectId { get; set; }
            public string Handle { get; set; }
            public string Layer { get; set; }
            public double Length { get; set; }
        }

        private sealed class RoadAlignmentRecord
        {
            public ObjectId AlignmentId { get; set; }
            public string Name { get; set; }
            public string SourceHandle { get; set; }
        }

        private sealed class RoadCorridorPlan
        {
            public string RoadName { get; set; }
            public ObjectId AlignmentId { get; set; }
            public ObjectId ProfileId { get; set; }
            public string ProfileName { get; set; }
            public string SourceHandle { get; set; }
        }

        private sealed class ProjectRoadStyles
        {
            public ObjectId AlignmentStyleId { get; set; }
            public ObjectId AlignmentLabelSetId { get; set; }
            public ObjectId ProfileStyleId { get; set; }
            public ObjectId ProfileLabelSetId { get; set; }
            public ObjectId ProfileViewStyleId { get; set; }
            public ObjectId BandSetStyleId { get; set; }
            public string AlignmentStyleName { get; set; }
            public string AlignmentLabelSetName { get; set; }
            public string ProfileStyleName { get; set; }
            public string ProfileLabelSetName { get; set; }
            public string ProfileViewStyleName { get; set; }
            public string BandSetStyleName { get; set; }
            public string AlignmentLayer { get; set; }
            public string ProfileLayer { get; set; }
        }
    }
}

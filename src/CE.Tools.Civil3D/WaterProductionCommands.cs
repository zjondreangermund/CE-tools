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
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;
using CivilPolylineOptions = Autodesk.Civil.DatabaseServices.PolylineOptions;

[assembly: CommandClass(typeof(CETools.Civil3D.WaterProductionCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Water and pressure-network production tools. Pressure-network objects are
    /// discovered through guarded reflection so the source can remain compatible
    /// across Civil 3D 2023 and 2024 API variations. Generated alignments, profiles,
    /// profile views and appurtenance review markers remain traceable and refreshable.
    /// </summary>
    public sealed class WaterProductionCommands
    {
        private const string RegAppName = "CE_TOOLS_WATER";
        private const string SettingsDictionary = "CE_TOOLS";
        private const string SettingsRecord = "WATER_PRODUCTION_SETTINGS";
        private const string AlignmentLayerDefault = "CE-WATER-ALIGNMENT";
        private const string ProfileLayerDefault = "CE-WATER-PROFILE";
        private const string AssetLayerDefault = "CE-WATER-ASSETS";
        private const double Tolerance = 1e-8;

        [CommandMethod("CE_WATERTOOLS", CommandFlags.Modal)]
        public void WaterTools()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null)
                return;
            string command = DisciplineWorkflowDialogs.SelectWorkflow(
                "CE Tools — Water Workflow",
                "Use the same production-centre format as Sewer, but with Water pressure-network parts, pressure-pipe/fitting/appurtenance styles, profiles, labels and delivery tools.",
                new List<DisciplineWorkflowAction>
                {
                    new DisciplineWorkflowAction("CE-Water Settings", "CE_WATERSETTINGSPRODUCTIONCENTRE", "Choose Water pressure-network parts/rules plus alignment, profile, profile-view, band and pressure-part label styles.", "01 Water Production"),
                    new DisciplineWorkflowAction("CE-Water Network / Layout Production", "CE_WATERLAYOUTPRODUCTIONCENTRE", "Prepare Water routes/network sources and sequence mains/branches.", "01 Water Production"),
                    new DisciplineWorkflowAction("CE-Water Design Production", "CE_WATERDESIGNPRODUCTIONCENTRE", "Review pressure-network data, then complete alignments, profiles, pressure-part labels/assets, setting-out, BOQ and report.", "01 Water Production"),
                    new DisciplineWorkflowAction("CE-Water Supplementary", "CE_WATERFIELDSUPPLEMENTARY", "Open the Water field supplementary tools.", "02 Supplementary")
                });
            if (!string.IsNullOrWhiteSpace(command))
                document.SendStringToExecute(command.Trim() + " ", true, false, true);
        }

        [CommandMethod("CE_WATERSETTINGS", CommandFlags.Modal)]
        public void ConfigureSettings()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null)
                return;

            WaterSettings settings = WaterSettings.Read(document.Database);
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            var model = new ProductionSettingsDialogModel(
                "CE Tools — Water Settings",
                "Blank style names use the first compatible drawing style. Asset spacing and profile layout are saved in the active DWG.");
            model.AddChoice("AlignmentStyle", "Civil 3D Styles", "Alignment style", settings.AlignmentStyle, "Style for generated water alignments.", ProductionStyleCatalog.ReadNames(document.Database, civilDocument == null ? null : (object)civilDocument.Styles.AlignmentStyles, "Alignment Style"));
            model.AddChoice("AlignmentLabelSetStyle", "Civil 3D Styles", "Alignment label-set style", settings.AlignmentLabelSetStyle, "Label set applied to generated alignments.", ProductionStyleCatalog.ReadNames(document.Database, civilDocument == null ? null : (object)civilDocument.Styles.LabelSetStyles.AlignmentLabelSetStyles, "Alignment Label Set Style"));
            model.AddChoice("ProfileStyle", "Civil 3D Styles", "Profile style", settings.ProfileStyle, "Style for existing-ground profiles.", ProductionStyleCatalog.ReadNames(document.Database, civilDocument == null ? null : (object)civilDocument.Styles.ProfileStyles, "Profile Style"));
            model.AddChoice("ProfileLabelSetStyle", "Civil 3D Styles", "Profile label-set style", settings.ProfileLabelSetStyle, "Label set applied to generated profiles.", ProductionStyleCatalog.ReadNames(document.Database, civilDocument == null ? null : (object)civilDocument.Styles.LabelSetStyles.ProfileLabelSetStyles, "Profile Label Set Style"));
            model.AddChoice("ProfileViewStyle", "Civil 3D Styles", "Profile-view style", settings.ProfileViewStyle, "Style for generated water profile views.", ProductionStyleCatalog.ReadNames(document.Database, civilDocument == null ? null : (object)civilDocument.Styles.ProfileViewStyles, "Profile View Style"));
            model.AddChoice("ProfileViewBandSetStyle", "Civil 3D Styles", "Profile-view band-set style", settings.ProfileViewBandSetStyle, "Band set for generated profile views.", ProductionStyleCatalog.ReadNames(document.Database, civilDocument == null ? null : (object)civilDocument.Styles.ProfileViewBandSetStyles, "Profile View Band Set Style"));
            model.AddText("AlignmentLayer", "Layers and Annotation", "Alignment layer", settings.AlignmentLayer, "Layer for water alignments and route labels.");
            model.AddText("ProfileLayer", "Layers and Annotation", "Profile layer", settings.ProfileLayer, "Layer for water profiles and profile views.");
            model.AddText("AssetLayer", "Layers and Annotation", "Asset review layer", settings.AssetLayer, "Layer for valve and hydrant review markers.");
            model.AddPaperHeight("LabelHeight", "Layers and Annotation", "Plan label paper height", settings.LabelHeight, "Select a standard annotative paper height or enter another positive value.");
            model.AddPositiveDouble("IsolatingValveSpacing", "Asset Spacing", "Maximum isolating-valve spacing", settings.IsolatingValveSpacing, "Maximum route spacing used for isolating-valve review markers.");
            model.AddChoice("HydrantSpacingOption", "Asset Spacing", "Fire hydrant spacing", settings.HydrantSpacing <= 180.0 ? "120 m" : "240 m", "Review hydrant markers at 120 m or 240 m intervals along the Water alignment.", new[] { "120 m", "240 m" });
            model.AddPositiveDouble("AssetRadius", "Asset Spacing", "Asset marker radius", settings.AssetRadius, "Drawing-unit radius of generated review markers.");
            model.AddPositiveInteger("ProfileColumns", "Profile View Layout", "Profile views per row", settings.ProfileColumns, "Number of generated views before wrapping to the next row.");
            model.AddPositiveDouble("ProfileHorizontalSpacing", "Profile View Layout", "Horizontal spacing", settings.ProfileHorizontalSpacing, "Drawing-unit spacing between profile-view columns.");
            model.AddPositiveDouble("ProfileVerticalSpacing", "Profile View Layout", "Vertical spacing", settings.ProfileVerticalSpacing, "Drawing-unit spacing between profile-view rows.");
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            settings.AlignmentStyle = model.Text("AlignmentStyle");
            settings.AlignmentLabelSetStyle = model.Text("AlignmentLabelSetStyle");
            settings.ProfileStyle = model.Text("ProfileStyle");
            settings.ProfileLabelSetStyle = model.Text("ProfileLabelSetStyle");
            settings.ProfileViewStyle = model.Text("ProfileViewStyle");
            settings.ProfileViewBandSetStyle = model.Text("ProfileViewBandSetStyle");
            settings.AlignmentLayer = model.Text("AlignmentLayer");
            settings.ProfileLayer = model.Text("ProfileLayer");
            settings.AssetLayer = model.Text("AssetLayer");
            settings.LabelHeight = model.Double("LabelHeight", settings.LabelHeight);
            settings.IsolatingValveSpacing = model.Double("IsolatingValveSpacing", settings.IsolatingValveSpacing);
            settings.HydrantSpacing = model.Text("HydrantSpacingOption") == "240 m" ? 240.0 : 120.0;
            settings.AssetRadius = model.Double("AssetRadius", settings.AssetRadius);
            settings.ProfileColumns = model.Integer("ProfileColumns", settings.ProfileColumns);
            settings.ProfileHorizontalSpacing = model.Double("ProfileHorizontalSpacing", settings.ProfileHorizontalSpacing);
            settings.ProfileVerticalSpacing = model.Double("ProfileVerticalSpacing", settings.ProfileVerticalSpacing);

            settings.Write(document.Database);
            document.Editor.WriteMessage("\nCE_WATERSETTINGS saved in the current DWG.");
        }

        [CommandMethod("CE_WATERSEQ", CommandFlags.Modal | CommandFlags.Redraw | CommandFlags.UsePickSet)]
        public void SequenceWaterRoutes()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null)
                return;

            Editor editor = document.Editor;
            List<RouteSource> sources;
            try
            {
                sources = ReadSelectedSources(document, true);
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage("\nCE_WATERSEQ cancelled. " + exception.Message);
                return;
            }

            if (sources.Count == 0)
            {
                editor.WriteMessage(
                    "\nCE_WATERSEQ: select polylines (including closed loops), gravity pipes or pressure pipes.");
                return;
            }

            if (!ConfigureRoutes(document, sources, true)) return;
            List<RouteSource> ordered = sources;

            editor.WriteMessage("\nCE_WATERSEQ preview:");
            for (int index = 0; index < ordered.Count; index++)
            {
                string routeName = ordered[index].RouteName;
                editor.WriteMessage(
                    "\n  {0}: source={1}; length={2:0.###}; vertices={3}",
                    routeName,
                    ordered[index].SourceDescription,
                    ordered[index].Length,
                    ordered[index].Points.Count);
            }

            if (!Confirm(editor, "Store this main/branch sequence on the selected source objects"))
                return;

            try
            {
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    EnsureRegApp(document.Database, transaction);
                    for (int index = 0; index < ordered.Count; index++)
                    {
                        RouteSource source = ordered[index];
                        string routeName = source.RouteName;
                        DBObject sourceObject = transaction.GetObject(
                            source.SourceId,
                            OpenMode.ForWrite,
                            false);
                        sourceObject.XData = BuildTag(
                            "Source",
                            routeName,
                            source.SourceHandle,
                            index.ToString(CultureInfo.InvariantCulture));
                        TrySetProperty(sourceObject, "Description", "CE water route " + routeName);
                        TrySetProperty(sourceObject, "Name", routeName);
                        foreach (ObjectId alignmentId in CivilApplication.ActiveDocument.GetAlignmentIds())
                        {
                            var alignment = transaction.GetObject(alignmentId, OpenMode.ForRead) as CivilAlignment;
                            string type, oldRoute, sourceHandle, extra;
                            if (alignment == null || !TryReadTag(alignment, out type, out oldRoute, out sourceHandle, out extra) || sourceHandle != source.SourceHandle) continue;
                            alignment.UpgradeOpen();
                            // Keep naming atomic; alignment creation/numbering owns name collision handling.
                            alignment.Description = "CE water alignment | route=" + routeName + " | source=" + sourceHandle;
                            SyncBranchIdentity(document.Database, transaction, alignment, routeName);
                        }
                    }
                    transaction.Commit();
                }

                editor.WriteMessage(
                    "\nCE_WATERSEQ complete. Routes sequenced: {0}. Branches start at BRANCH-1; diameter/length priority and elevation direction applied.",
                    ordered.Count);
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_WATERSEQ cancelled. The transaction was not committed: " +
                    exception.Message);
            }
        }

        [CommandMethod("CE_WATERALIGN", CommandFlags.Modal | CommandFlags.Redraw | CommandFlags.UsePickSet)]
        public void CreateAlignments()
        {
            CreateOrRefreshAlignments(false);
        }

        [CommandMethod("CE_WATERREFRESH", CommandFlags.Modal | CommandFlags.Redraw)]
        public void RefreshAlignments()
        {
            CreateOrRefreshAlignments(true);
        }

        private static void CreateOrRefreshAlignments(bool linkedOnly)
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (document == null || civilDocument == null)
                return;

            Editor editor = document.Editor;
            List<RouteSource> sources;
            try
            {
                sources = linkedOnly
                    ? ReadLinkedSources(document.Database, civilDocument)
                    : ReadSelectedSources(document, true);
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage("\nCE_WATERALIGN cancelled. " + exception.Message);
                return;
            }

            if (sources.Count == 0)
            {
                editor.WriteMessage(linkedOnly
                    ? "\nCE_WATERREFRESH: no live linked water sources were found."
                    : "\nCE_WATERALIGN: select open polylines or pressure-pipe objects.");
                return;
            }

            if (!ConfigureRoutes(document, sources, !linkedOnly)) return;
            WaterSettings settings = WaterSettings.Read(document.Database);
            editor.WriteMessage(
                "\nCE_WATERALIGN preview: routes={0}; alignment layer={1}.",
                sources.Count,
                settings.AlignmentLayer);
            foreach (RouteSource source in sources)
                editor.WriteMessage(
                    "\n  {0}: source={1}; length={2:0.###}",
                    source.RouteName,
                    source.SourceDescription,
                    source.Length);

            if (!Confirm(editor, "Create or refresh the linked water alignments and route labels"))
                return;

            try
            {
                int created = 0;
                int labels = 0;
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    EnsureRegApp(document.Database, transaction);
                    ObjectId alignmentLayerId = GetOrCreateLayer(
                        document.Database,
                        transaction,
                        settings.AlignmentLayer,
                        AlignmentLayerDefault);
                    ObjectId alignmentStyleId = ResolveStyleId(
                        civilDocument.Styles.AlignmentStyles,
                        settings.AlignmentStyle,
                        "alignment style",
                        transaction);
                    ObjectId alignmentLabelStyleId = ResolveStyleId(
                        civilDocument.Styles.LabelSetStyles.AlignmentLabelSetStyles,
                        settings.AlignmentLabelSetStyle,
                        "alignment label-set style",
                        transaction);

                    BlockTable blocks = (BlockTable)transaction.GetObject(
                        document.Database.BlockTableId,
                        OpenMode.ForRead,
                        false);
                    BlockTableRecord modelSpace = (BlockTableRecord)transaction.GetObject(
                        blocks[BlockTableRecord.ModelSpace],
                        OpenMode.ForWrite,
                        false);

                    var reservedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (RouteSource source in sources)
                    {
                        RemoveGeneratedForSource(
                            civilDocument,
                            modelSpace,
                            source.SourceHandle,
                            transaction,
                            new[] { "Alignment", "RouteLabel" });

                        ObjectId polylineId;
                        var original = transaction.GetObject(source.SourceId, OpenMode.ForRead, false) as Polyline;
                        if (original != null)
                        {
                            var copy = (Polyline)original.Clone();
                            copy.LayerId = alignmentLayerId;
                            if (copy.GetPoint3dAt(0).DistanceTo(source.Points[0]) > 0.001) copy.ReverseCurve();
                            polylineId = modelSpace.AppendEntity(copy);
                            transaction.AddNewlyCreatedDBObject(copy, true);
                        }
                        else polylineId = AddTemporaryPolyline(document.Database, modelSpace,
                            source.Points, alignmentLayerId, transaction);
                        var options = new CivilPolylineOptions
                        {
                            AddCurvesBetweenTangents = false,
                            EraseExistingEntities = true,
                            PlineId = polylineId
                        };

                        string alignmentName = ResolveAlignmentName(
                            civilDocument,
                            source.RouteName,
                            source.SourceHandle,
                            reservedNames,
                            transaction);
                        ObjectId alignmentId = CivilAlignment.Create(
                            civilDocument,
                            options,
                            alignmentName,
                            ObjectId.Null,
                            alignmentLayerId,
                            alignmentStyleId,
                            alignmentLabelStyleId);

                        var alignment = transaction.GetObject(
                            alignmentId,
                            OpenMode.ForWrite,
                            false) as CivilAlignment;
                        if (alignment == null)
                            throw new InvalidOperationException(
                                "Civil 3D did not return the created water alignment.");

                        alignment.Description =
                            "CE water alignment | route=" + source.RouteName +
                            " | source=" + source.SourceHandle;
                        alignment.XData = BuildTag(
                            "Alignment",
                            source.RouteName,
                            source.SourceHandle,
                            source.SourceKind);
                        var sourceObject = transaction.GetObject(source.SourceId, OpenMode.ForWrite, false);
                        sourceObject.XData = BuildTag("Source", source.RouteName, source.SourceHandle, source.SourceKind);
                        created++;

                        var label = new MText();
                        label.SetDatabaseDefaults(document.Database);
                        label.LayerId = alignmentLayerId;
                        label.Location = GetRouteLabelPoint(source.Points, created - 1, settings.LabelHeight);
                        label.Attachment = AttachmentPoint.MiddleCenter;
                        label.TextHeight = PaperAnnotationScale.AnnotativeTextHeight(
                            document.Database,
                            settings.LabelHeight);
                        PaperAnnotationScale.SetAnnotative(label);
                        label.Contents = source.RouteName;
                        label.BackgroundFill = true;
                        label.UseBackgroundColor = true;
                        label.XData = BuildTag(
                            "RouteLabel",
                            source.RouteName,
                            source.SourceHandle,
                            source.SourceKind);
                        modelSpace.AppendEntity(label);
                        transaction.AddNewlyCreatedDBObject(label, true);
                        labels++;
                    }
                    transaction.Commit();
                }

                editor.WriteMessage(
                    "\nCE_WATERALIGN complete. Alignments created/refreshed: {0}; labels: {1}.",
                    created,
                    labels);
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_WATERALIGN cancelled. The transaction was not committed: " +
                    exception.Message);
            }
        }

        [CommandMethod("CE_WATERPROFILE", CommandFlags.Modal | CommandFlags.Redraw)]
        public void CreateProfiles()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (document == null || civilDocument == null)
                return;

            Editor editor = document.Editor;
            List<SurfaceChoice> surfaceChoices = WorkflowRepairCommands.ReadSurfaceChoices(document);
            if (surfaceChoices.Count == 0)
            {
                editor.WriteMessage("\nCE_WATERPROFILE cancelled. The drawing contains no Civil 3D surfaces.");
                return;
            }
            var surfaceWindow = new SurfaceSelectionWindow(
                surfaceChoices,
                "Select the existing-ground surface for the water profiles. Double-clicking a row also selects it.");
            AcApplication.ShowModalWindow(surfaceWindow);
            SurfaceChoice selectedSurface = surfaceWindow.SelectedSurface;
            if (selectedSurface == null) return;

            PromptPointResult insertionResult = editor.GetPoint(
                "\nSelect the upper-left insertion point for the water profile views: ");
            if (insertionResult.Status != PromptStatus.OK)
                return;

            WaterSettings settings = WaterSettings.Read(document.Database);
            List<AlignmentRecord> records;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                records = ReadAlignmentRecords(civilDocument, transaction);

            if (records.Count == 0)
            {
                editor.WriteMessage(
                    "\nCE_WATERPROFILE: no CE water alignments were found. Run CE_WATERALIGN first.");
                return;
            }

            editor.WriteMessage(
                "\nCE_WATERPROFILE preview: alignments={0}; surface={1}.",
                records.Count,
                selectedSurface.ObjectId.Handle);
            if (!Confirm(editor, "Create or refresh the existing-ground profiles and profile views"))
                return;

            try
            {
                int profiles = 0;
                int views = 0;
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    EnsureRegApp(document.Database, transaction);
                    ObjectId profileLayerId = GetOrCreateLayer(
                        document.Database,
                        transaction,
                        settings.ProfileLayer,
                        ProfileLayerDefault);
                    ObjectId profileStyleId = ResolveStyleId(
                        civilDocument.Styles.ProfileStyles,
                        settings.ProfileStyle,
                        "profile style",
                        transaction);
                    ObjectId profileLabelSetId = ResolveStyleId(
                        civilDocument.Styles.LabelSetStyles.ProfileLabelSetStyles,
                        settings.ProfileLabelSetStyle,
                        "profile label-set style",
                        transaction);
                    ObjectId profileViewStyleId = ResolveStyleId(
                        civilDocument.Styles.ProfileViewStyles,
                        settings.ProfileViewStyle,
                        "profile-view style",
                        transaction);
                    ObjectId profileViewBandSetId = ResolveStyleId(
                        civilDocument.Styles.ProfileViewBandSetStyles,
                        settings.ProfileViewBandSetStyle,
                        "profile-view band-set style",
                        transaction);

                    for (int index = 0; index < records.Count; index++)
                    {
                        AlignmentRecord record = records[index];
                        RemoveProfileObjects(
                            document.Database,
                            record.SourceHandle,
                            transaction);

                        string profileName = UniqueName(
                            record.RouteName + " - EG",
                            ReadCivilNames(
                                GetAlignmentProfileIds(civilDocument, transaction, false),
                                transaction));
                        ObjectId profileId = InvokeCreateProfileFromSurface(
                            profileName,
                            record.AlignmentId,
                            selectedSurface.ObjectId,
                            profileLayerId,
                            profileStyleId,
                            profileLabelSetId);
                        DBObject profile = transaction.GetObject(
                            profileId,
                            OpenMode.ForWrite,
                            false);
                        profile.XData = BuildTag(
                            "Profile",
                            record.RouteName,
                            record.SourceHandle,
                            selectedSurface.ObjectId.Handle.ToString());
                        profiles++;

                        int column = index % settings.ProfileColumns;
                        int row = index / settings.ProfileColumns;
                        Point3d viewPoint = insertionResult.Value + new Vector3d(
                            column * settings.ProfileHorizontalSpacing,
                            -row * settings.ProfileVerticalSpacing,
                            0.0);
                        string viewName = UniqueName(
                            record.RouteName + " - PROFILE VIEW",
                            ReadCivilNames(
                                GetAlignmentProfileIds(civilDocument, transaction, true),
                                transaction));
                        ObjectId viewId = InvokeCreateProfileView(
                            viewName,
                            record.AlignmentId,
                            viewPoint,
                            profileViewBandSetId,
                            profileViewStyleId);
                        DBObject view = transaction.GetObject(
                            viewId,
                            OpenMode.ForWrite,
                            false);
                        view.XData = BuildTag(
                            "ProfileView",
                            record.RouteName,
                            record.SourceHandle,
                            selectedSurface.ObjectId.Handle.ToString());
                        TryAddPressurePartsToProfileView(record, viewId, transaction);
                        ObjectId bandNetworkId = ObjectId.Null;
                        if (!string.IsNullOrWhiteSpace(record.SourceHandle))
                        {
                            ObjectId routeSourceId;
                            if (TryGetObjectId(
                                    document.Database,
                                    record.SourceHandle,
                                    out routeSourceId))
                            {
                                DBObject routeSource = transaction.GetObject(
                                    routeSourceId,
                                    OpenMode.ForRead,
                                    false);
                                object networkIdValue = ReadProperty(routeSource, "NetworkId") ??
                                                        ReadProperty(routeSource, "PressureNetworkId");
                                if (networkIdValue is ObjectId)
                                    bandNetworkId = (ObjectId)networkIdValue;
                            }
                        }
                        ProfileViewBandDataBinder.Bind(
                            view,
                            profileId,
                            ObjectId.Null,
                            bandNetworkId);
                        RepairWaterBandProfiles(view, profileId);
                        views++;
                    }
                    transaction.Commit();
                }

                editor.WriteMessage(
                    "\nCE_WATERPROFILE complete. Profiles: {0}; profile views: {1}.",
                    profiles,
                    views);
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_WATERPROFILE cancelled. The installed Civil 3D profile API could not complete the operation: " +
                    exception.Message);
            }
        }

        // The band-set may arrive with invalid source-template profile references.
        // Assign both profile slots to the locally generated water EG profile.
        private static int RepairWaterBandProfiles(DBObject view, ObjectId profileId)
        {
            var profileView = view as Autodesk.Civil.DatabaseServices.ProfileView;
            if (profileView == null || profileId.IsNull) return 0;
            int repaired = 0;
            var bands = profileView.Bands;
            foreach (var items in new[] { bands.GetTopBandItems(), bands.GetBottomBandItems() })
            {
                foreach (Autodesk.Civil.DatabaseServices.ProfileViewBandItem item in items)
                {
                    if (item.BandType == Autodesk.Civil.BandType.PipeNetwork ||
                        item.BandType == Autodesk.Civil.BandType.PressureNetwork ||
                        item.BandType == Autodesk.Civil.BandType.HorizontalGeometry ||
                        item.BandType == Autodesk.Civil.BandType.SuperelevationData) continue;
                    try
                    {
                        item.Profile1Id = profileId;
                        item.Profile2Id = profileId;
                        item.ShowLabels = true;
                        repaired++;
                    }
                    catch (System.Exception) { }
                }
            }
            return repaired;
        }

        [CommandMethod("CE_WATERBANDFIX", CommandFlags.Modal | CommandFlags.Redraw)]
        public void RepairExistingWaterProfileBands()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (document == null || civilDocument == null) return;
            int viewsFixed = 0, bandsFixed = 0, skipped = 0;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                List<AlignmentRecord> records = ReadAlignmentRecords(civilDocument, transaction);
                foreach (AlignmentRecord record in records)
                {
                    try
                    {
                        var alignment = transaction.GetObject(record.AlignmentId, OpenMode.ForRead, false)
                            as CivilAlignment;
                        if (alignment == null) continue;
                        ObjectId ground = ObjectId.Null;
                        foreach (ObjectId id in alignment.GetProfileIds())
                        {
                            var profile = transaction.GetObject(id, OpenMode.ForRead, false)
                                as Autodesk.Civil.DatabaseServices.Profile;
                            if (profile == null) continue;
                            if (ground.IsNull || profile.Name.EndsWith(" - EG", StringComparison.OrdinalIgnoreCase))
                                ground = id;
                        }
                        if (ground.IsNull) { skipped++; continue; }
                        foreach (ObjectId viewId in alignment.GetProfileViewIds())
                        {
                            var view = transaction.GetObject(viewId, OpenMode.ForWrite, false)
                                as Autodesk.Civil.DatabaseServices.ProfileView;
                            if (view == null || view.IsReferenceObject) continue;
                            bandsFixed += RepairWaterBandProfiles(view, ground);
                            viewsFixed++;
                        }
                    }
                    catch (System.Exception ex)
                    {
                        skipped++;
                        document.Editor.WriteMessage("\nWater band repair skipped " +
                            record.RouteName + ": " + ex.Message);
                    }
                }
                transaction.Commit();
            }
            document.Editor.Regen();
            System.Windows.Forms.MessageBox.Show(
                "Water profile views updated: " + viewsFixed +
                "\nProfile band rows updated: " + bandsFixed +
                "\nRoutes skipped: " + skipped +
                "\n\nProfile1/Profile2 now reference the matching water EG profile.",
                "CE Tools - Water Profile Band Repair");
        }

        [CommandMethod("CE_WATERPLACE", CommandFlags.Modal | CommandFlags.Redraw | CommandFlags.UsePickSet)]
        public void PlaceAssets()
        {
            PlaceOrRefreshAssets(false);
        }

        [CommandMethod("CE_WATERPLACEREFRESH", CommandFlags.Modal | CommandFlags.Redraw)]
        public void RefreshAssets()
        {
            PlaceOrRefreshAssets(true);
        }

        private static void PlaceOrRefreshAssets(bool linkedOnly)
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (document == null || civilDocument == null)
                return;

            Editor editor = document.Editor;
            WaterSettings settings = WaterSettings.Read(document.Database);
            List<AlignmentRecord> records;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                records = ReadAlignmentRecords(civilDocument, transaction);
                if (!linkedOnly)
                {
                    PromptSelectionResult selected = editor.GetSelection(
                        new PromptSelectionOptions
                        {
                            MessageForAdding = "\nSelect CE water alignments for asset review placement: ",
                            RejectObjectsFromNonCurrentSpace = true
                        });
                    if (selected.Status != PromptStatus.OK)
                        return;
                    var selectedIds = new HashSet<ObjectId>(selected.Value.GetObjectIds());
                    records = records.Where(record => selectedIds.Contains(record.AlignmentId)).ToList();
                }
            }

            if (records.Count == 0)
            {
                editor.WriteMessage(
                    "\nCE_WATERPLACE: no linked CE water alignments were found or selected.");
                return;
            }

            List<AssetProposal> proposals = BuildAssetProposals(
                document.Database,
                records,
                settings);
            editor.WriteMessage(
                "\nCE_WATERPLACE engineering-review preview: routes={0}; proposals={1}.",
                records.Count,
                proposals.Count);
            foreach (IGrouping<string, AssetProposal> group in proposals.GroupBy(item => item.AssetType))
                editor.WriteMessage("\n  {0}: {1}", group.Key, group.Count());
            editor.WriteMessage(
                "\n  These are review markers only. Final valve/hydrant type, spacing, chamber, cover, thrust restraint and authority approval remain the engineer's responsibility.");

            if (!Confirm(editor, "Create or refresh these controlled water-asset review markers"))
                return;

            try
            {
                int created = 0;
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    EnsureRegApp(document.Database, transaction);
                    ObjectId layerId = GetOrCreateLayer(
                        document.Database,
                        transaction,
                        settings.AssetLayer,
                        AssetLayerDefault);
                    BlockTable blocks = (BlockTable)transaction.GetObject(
                        document.Database.BlockTableId,
                        OpenMode.ForRead,
                        false);
                    BlockTableRecord modelSpace = (BlockTableRecord)transaction.GetObject(
                        blocks[BlockTableRecord.ModelSpace],
                        OpenMode.ForWrite,
                        false);

                    foreach (AlignmentRecord record in records)
                        RemoveGeneratedForSource(
                            civilDocument,
                            modelSpace,
                            record.SourceHandle,
                            transaction,
                            new[] { "AssetMarker", "AssetLabel" });

                    foreach (AssetProposal proposal in proposals)
                    {
                        var circle = new Circle(
                            proposal.Point,
                            Vector3d.ZAxis,
                            settings.AssetRadius);
                        circle.SetDatabaseDefaults(document.Database);
                        circle.LayerId = layerId;
                        circle.XData = BuildTag(
                            "AssetMarker",
                            proposal.RouteName,
                            proposal.SourceHandle,
                            proposal.AssetType + "|" +
                            proposal.Station.ToString("0.###", CultureInfo.InvariantCulture));
                        modelSpace.AppendEntity(circle);
                        transaction.AddNewlyCreatedDBObject(circle, true);

                        var label = new MText();
                        label.SetDatabaseDefaults(document.Database);
                        label.LayerId = layerId;
                        label.Location = proposal.Point + new Vector3d(
                            settings.AssetRadius * 1.8,
                            settings.AssetRadius * 1.8,
                            0.0);
                        label.TextHeight = PaperAnnotationScale.AnnotativeTextHeight(
                            document.Database,
                            settings.LabelHeight);
                        PaperAnnotationScale.SetAnnotative(label);
                        label.Attachment = AttachmentPoint.BottomLeft;
                        label.Contents =
                            proposal.AssetCode + "\n" +
                            proposal.RouteName + " STA " +
                            proposal.Station.ToString("0.###", CultureInfo.InvariantCulture) +
                            "\n" + proposal.Reason;
                        label.BackgroundFill = true;
                        label.UseBackgroundColor = true;
                        label.XData = BuildTag(
                            "AssetLabel",
                            proposal.RouteName,
                            proposal.SourceHandle,
                            proposal.AssetType + "|" +
                            proposal.Station.ToString("0.###", CultureInfo.InvariantCulture));
                        modelSpace.AppendEntity(label);
                        transaction.AddNewlyCreatedDBObject(label, true);
                        created++;
                    }
                    transaction.Commit();
                }

                editor.WriteMessage(
                    "\nCE_WATERPLACE complete. Review markers created/refreshed: {0}.",
                    created);
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_WATERPLACE cancelled. The transaction was not committed: " +
                    exception.Message);
            }
        }

        [CommandMethod("CE_WATERINFO", CommandFlags.Modal)]
        public void WaterInformation()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (document == null || civilDocument == null)
                return;

            WaterSettings settings = WaterSettings.Read(document.Database);
            int alignments;
            int profiles = 0;
            int views = 0;
            int markers = 0;
            int labels = 0;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                alignments = ReadAlignmentRecords(civilDocument, transaction).Count;
                BlockTable blocks = (BlockTable)transaction.GetObject(
                    document.Database.BlockTableId,
                    OpenMode.ForRead,
                    false);
                BlockTableRecord modelSpace = (BlockTableRecord)transaction.GetObject(
                    blocks[BlockTableRecord.ModelSpace],
                    OpenMode.ForRead,
                    false);
                foreach (ObjectId id in modelSpace)
                {
                    DBObject item = transaction.GetObject(id, OpenMode.ForRead, false);
                    string type;
                    string route;
                    string source;
                    string extra;
                    if (!TryReadTag(item, out type, out route, out source, out extra))
                        continue;
                    if (type == "Profile") profiles++;
                    else if (type == "ProfileView") views++;
                    else if (type == "AssetMarker") markers++;
                    else if (type == "AssetLabel") labels++;
                }
            }

            document.Editor.WriteMessage(
                "\nCE Water Production Information" +
                "\n  Linked alignments: " + alignments +
                "\n  Generated profiles: " + profiles +
                "\n  Generated profile views: " + views +
                "\n  Asset markers: " + markers +
                "\n  Asset labels: " + labels +
                "\n  Alignment layer: " + settings.AlignmentLayer +
                "\n  Profile layer: " + settings.ProfileLayer +
                "\n  Asset layer: " + settings.AssetLayer +
                "\n  Isolating-valve spacing: " + settings.IsolatingValveSpacing.ToString("0.###", CultureInfo.InvariantCulture) +
                "\n  Hydrant spacing: " + settings.HydrantSpacing.ToString("0.###", CultureInfo.InvariantCulture) +
                "\n  Refresh is explicit through CE_WATERREFRESH and CE_WATERPLACEREFRESH.");
        }

        private static List<RouteSource> ReadSelectedSources(
            Document document,
            bool useStoredRouteNames)
        {
            Editor editor = document.Editor;
            PromptSelectionResult selection = editor.SelectImplied();
            if (selection.Status != PromptStatus.OK ||
                selection.Value == null ||
                selection.Value.Count == 0)
            {
                var scope = new PromptKeywordOptions("\nWater sources [All/Selected] <Selected>: ");
                scope.Keywords.Add("All");
                scope.Keywords.Add("Selected");
                scope.AllowNone = true;
                PromptResult scopeResult = editor.GetKeywords(scope);
                if (scopeResult.Status == PromptStatus.Cancel) return new List<RouteSource>();
                if (scopeResult.Status == PromptStatus.OK &&
                    string.Equals(scopeResult.StringResult, "All", StringComparison.OrdinalIgnoreCase))
                {
                    var all = new List<ObjectId>();
                    using (Transaction read = document.Database.TransactionManager.StartTransaction())
                    {
                        var model = (BlockTableRecord)read.GetObject(
                            SymbolUtilityServices.GetBlockModelSpaceId(document.Database), OpenMode.ForRead);
                        foreach (ObjectId id in model) all.Add(id);
                    }
                    return ReadRouteSources(document.Database, all, useStoredRouteNames);
                }
                selection = editor.GetSelection(
                    new PromptSelectionOptions
                    {
                        MessageForAdding =
                            "\nSelect water polylines, gravity pipes or pressure pipes: ",
                        AllowDuplicates = false,
                        RejectObjectsFromNonCurrentSpace = true
                    });
            }
            if (selection.Status != PromptStatus.OK || selection.Value == null)
                return new List<RouteSource>();

            return ReadRouteSources(document.Database, selection.Value.GetObjectIds(), useStoredRouteNames);
        }

        private static List<RouteSource> ReadRouteSources(Database database, IEnumerable<ObjectId> sourceIds, bool useStoredRouteNames)
        {
            var result = new List<RouteSource>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                int unnamed = 0;
                foreach (ObjectId id in sourceIds.Distinct())
                {
                    DBObject item = transaction.GetObject(id, OpenMode.ForRead, false);
                    List<Point3d> points;
                    string kind;
                    if (!TryReadSourcePoints(item, out points, out kind))
                        continue;
                    if (points.Count < 2)
                        continue;

                    string storedType;
                    string storedRoute;
                    string storedSource;
                    string storedExtra;
                    bool tagged = TryReadTag(
                        item,
                        out storedType,
                        out storedRoute,
                        out storedSource,
                        out storedExtra);
                    string routeName = useStoredRouteNames && tagged && storedType == "Source"
                        ? storedRoute
                        : "BRANCH-" + (++unnamed).ToString(CultureInfo.InvariantCulture);
                    result.Add(new RouteSource(
                        id,
                        id.Handle.ToString(),
                        routeName,
                        kind,
                        item.GetType().Name,
                        points));
                }
            }

            return result;
        }

        private static bool ConfigureRoutes(Document document, List<RouteSource> sources, bool renumber)
        {
            var surfaces = WorkflowRepairCommands.ReadSurfaceChoices(document);
            var options = new ProductionSettingsDialogModel("CE Tools - Water Branch Sequence / Alignments",
                "One alignment per selected source polyline. Larger pipes take priority, then longer branches. Source pipe geometry is preserved.");
            options.AddChoice("Priority", "01 Sequence", "Priority branches", "Larger pipes, then longer branches",
                "Use diameter first or length first. Elevation breaks ties; all names start at BRANCH-1.",
                new[] { "Larger pipes, then longer branches", "Longer branches, then larger pipes", "Highest elevation first" });
            options.AddChoice("TargetSurface", "02 Direction", "Target surface", "<Source elevations>",
                "Sample branch endpoints on this surface for high-to-low alignment direction. No pipe levels are changed.",
                new[] { "<Source elevations>" }.Concat(surfaces.Select(x => x.Name)).ToArray());
            options.AddChoice("Direction", "02 Direction", "Alignment direction", "Highest to lowest elevation",
                "Reverse the alignment copy when needed; keep the selected source unchanged.",
                new[] { "Highest to lowest elevation", "As selected polylines" });
            if (!DisciplineWorkflowDialogs.EditSettings(options)) return false;
            var selectedSurface = surfaces.FirstOrDefault(x => x.Name == options.Text("TargetSurface"));
            using (var tr = document.Database.TransactionManager.StartTransaction())
            {
                var surface = selectedSurface == null ? null : tr.GetObject(selectedSurface.ObjectId, OpenMode.ForRead) as CivilSurface;
                foreach (var source in sources)
                {
                    var pipe = tr.GetObject(source.SourceId, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Pipe;
                    source.Diameter = pipe == null ? 0.0 : pipe.InnerDiameterOrWidth;
                    var sourceCurve = tr.GetObject(source.SourceId, OpenMode.ForRead) as Curve;
                    if (sourceCurve is Polyline) source.Length = ((Polyline)sourceCurve).Length;
                    if (pipe == null && sourceCurve != null)
                    {
                        foreach (ObjectId networkId in CivilApplication.ActiveDocument.GetPipeNetworkIds())
                        {
                            var network = tr.GetObject(networkId, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Network;
                            if (network == null || !(network.Name.IndexOf("water", StringComparison.OrdinalIgnoreCase) >= 0 || network.Name.StartsWith("W-", StringComparison.OrdinalIgnoreCase))) continue;
                            foreach (ObjectId pipeId in network.GetPipeIds())
                            {
                                var candidate = tr.GetObject(pipeId, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Pipe;
                                if (candidate == null) continue;
                                // Compare in plan: a ground-level polyline need not share pipe invert Z.
                                var start = candidate.StartPoint; var end = candidate.EndPoint;
                                var mid = start + (end - start) * 0.5;
                                bool onRoute = true;
                                foreach (var point in new[] { start, mid, end })
                                {
                                    var nearest = sourceCurve.GetClosestPointTo(point, Vector3d.ZAxis, false);
                                    if (new Point2d(point.X, point.Y).GetDistanceTo(new Point2d(nearest.X, nearest.Y)) > 0.1) { onRoute = false; break; }
                                }
                                if (onRoute) source.Diameter = Math.Max(source.Diameter, candidate.InnerDiameterOrWidth);
                            }
                        }
                    }
                    source.StartElevation = source.Points[0].Z;
                    source.EndElevation = source.Points[source.Points.Count - 1].Z;
                    if (surface != null)
                    {
                        try
                        {
                            source.StartElevation = surface.FindElevationAtXY(source.Points[0].X, source.Points[0].Y);
                            var end = source.Points[source.Points.Count - 1];
                            source.EndElevation = surface.FindElevationAtXY(end.X, end.Y);
                        }
                        catch (System.Exception)
                        {
                            document.Editor.WriteMessage("\nWater sequence cancelled: source {0} is outside the selected surface. No changes made.", source.SourceHandle);
                            return false;
                        }
                    }
                    if (options.Text("Direction") == "Highest to lowest elevation" && source.EndElevation > source.StartElevation)
                        source.Points = source.Points.Reverse().ToList();
                }
            }
            if (renumber)
            {
                var sorted = sources.OrderBy(x => x, Comparer<RouteSource>.Create((a, b) =>
                    WaterBranchPriority.Compare(a.Diameter, a.Length, Math.Max(a.StartElevation, a.EndElevation), a.SourceHandle,
                        b.Diameter, b.Length, Math.Max(b.StartElevation, b.EndElevation), b.SourceHandle, options.Text("Priority")))).ToList();
                sources.Clear(); sources.AddRange(sorted);
                for (int i = 0; i < sources.Count; i++) sources[i].RouteName = "BRANCH-" + (i + 1).ToString(CultureInfo.InvariantCulture);
            }
            return true;
        }

        private static List<RouteSource> ReadLinkedSources(
            Database database,
            CivilDocument civilDocument)
        {
            var result = new List<RouteSource>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (AlignmentRecord record in ReadAlignmentRecords(civilDocument, transaction))
                {
                    ObjectId sourceId;
                    if (!TryGetObjectId(database, record.SourceHandle, out sourceId))
                        continue;
                    DBObject source = transaction.GetObject(sourceId, OpenMode.ForRead, false);
                    List<Point3d> points;
                    string kind;
                    if (!TryReadSourcePoints(source, out points, out kind))
                        continue;
                    result.Add(new RouteSource(
                        sourceId,
                        record.SourceHandle,
                        record.RouteName,
                        kind,
                        source.GetType().Name,
                        points));
                }
            }
            return result;
        }

        private static bool TryReadSourcePoints(
            DBObject item,
            out List<Point3d> points,
            out string kind)
        {
            points = new List<Point3d>();
            kind = string.Empty;

            var polyline = item as Polyline;
            if (polyline != null && polyline.NumberOfVertices >= 2)
            {
                for (int index = 0; index < polyline.NumberOfVertices; index++)
                    AddDistinct(points, polyline.GetPoint3dAt(index));
                if (polyline.Closed && points.Count > 2)
                    points.Add(points[0]);
                kind = polyline.Closed ? "ClosedPolylineLoop" : "Polyline";
                return points.Count >= 2;
            }

            // A Civil 3D gravity water network uses ordinary Pipe entities,
            // not only pressure-pipe entities. Accept both and retain their
            // start/end elevations when creating branch route alignments.
            var gravityPipe = item as Autodesk.Civil.DatabaseServices.Pipe;
            if (gravityPipe != null)
            {
                AddDistinct(points, gravityPipe.StartPoint);
                AddDistinct(points, gravityPipe.EndPoint);
                kind = "GravityPipe";
                return points.Count >= 2;
            }

            string typeName = item.GetType().FullName ?? item.GetType().Name;
            if (typeName.IndexOf("Pressure", StringComparison.OrdinalIgnoreCase) < 0 ||
                typeName.IndexOf("Pipe", StringComparison.OrdinalIgnoreCase) < 0)
                return false;

            Point3d start;
            Point3d end;
            if (TryReadPoint(item, new[] { "StartPoint", "StartPosition" }, out start) &&
                TryReadPoint(item, new[] { "EndPoint", "EndPosition" }, out end))
            {
                AddDistinct(points, start);
                AddDistinct(points, end);
                kind = "PressurePipe";
                return true;
            }

            MethodInfo pointMethod = item.GetType().GetMethod(
                "GetPointAtParam",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { typeof(double) },
                null);
            if (pointMethod != null)
            {
                for (int index = 0; index <= 12; index++)
                {
                    object value = pointMethod.Invoke(item, new object[] { index / 12.0 });
                    if (value is Point3d)
                        AddDistinct(points, (Point3d)value);
                }
            }
            kind = "PressurePipe";
            return points.Count >= 2;
        }

        private static List<AlignmentRecord> ReadAlignmentRecords(
            CivilDocument civilDocument,
            Transaction transaction)
        {
            var result = new List<AlignmentRecord>();
            foreach (ObjectId id in civilDocument.GetAlignmentIds())
            {
                DBObject item = transaction.GetObject(id, OpenMode.ForRead, false);
                string type;
                string route;
                string source;
                string extra;
                if (TryReadTag(item, out type, out route, out source, out extra) &&
                    type == "Alignment")
                    result.Add(new AlignmentRecord(id, route, source, extra));
            }
            return result.OrderBy(item => item.RouteName, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static List<AssetProposal> BuildAssetProposals(
            Database database,
            IReadOnlyList<AlignmentRecord> records,
            WaterSettings settings)
        {
            var result = new List<AssetProposal>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (AlignmentRecord record in records)
                {
                    var alignment = transaction.GetObject(
                        record.AlignmentId,
                        OpenMode.ForRead,
                        false) as CivilAlignment;
                    if (alignment == null)
                        continue;

                    double length = Math.Max(0.0, alignment.EndingStation - alignment.StartingStation);
                    if (length <= Tolerance)
                        continue;

                    AddRegularProposals(
                        result,
                        alignment,
                        record,
                        "Isolating Valve",
                        "IV",
                        settings.IsolatingValveSpacing,
                        "Maximum spacing review");
                    AddRegularProposals(
                        result,
                        alignment,
                        record,
                        "Fire Hydrant",
                        "FH",
                        settings.HydrantSpacing,
                        "Hydrant coverage review");

                    ObjectId sourceId;
                    if (!TryGetObjectId(database, record.SourceHandle, out sourceId))
                        continue;
                    DBObject source = transaction.GetObject(sourceId, OpenMode.ForRead, false);
                    var polyline = source as Polyline;
                    if (polyline == null || polyline.NumberOfVertices < 3)
                        continue;

                    for (int index = 1; index < polyline.NumberOfVertices - 1; index++)
                    {
                        Point3d previous = polyline.GetPoint3dAt(index - 1);
                        Point3d current = polyline.GetPoint3dAt(index);
                        Point3d next = polyline.GetPoint3dAt(index + 1);
                        double station = SafeStation(alignment, current);
                        if (current.Z > previous.Z + Tolerance &&
                            current.Z > next.Z + Tolerance)
                            result.Add(new AssetProposal(
                                record.RouteName,
                                record.SourceHandle,
                                "Air Valve",
                                "AV",
                                station,
                                current,
                                "Local high point review"));
                        else if (current.Z < previous.Z - Tolerance &&
                                 current.Z < next.Z - Tolerance)
                            result.Add(new AssetProposal(
                                record.RouteName,
                                record.SourceHandle,
                                "Scour Valve",
                                "SV",
                                station,
                                current,
                                "Local low point review"));
                    }
                }
            }
            return result
                .GroupBy(item => item.SourceHandle + "|" + item.AssetType + "|" +
                    Math.Round(item.Station, 3).ToString(CultureInfo.InvariantCulture))
                .Select(group => group.First())
                .OrderBy(item => item.RouteName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Station)
                .ToList();
        }

        private static void AddRegularProposals(
            ICollection<AssetProposal> proposals,
            CivilAlignment alignment,
            AlignmentRecord record,
            string assetType,
            string assetCode,
            double spacing,
            string reason)
        {
            double start = alignment.StartingStation;
            double end = alignment.EndingStation;
            double length = Math.Max(0.0, end - start);
            int intervals = Math.Max(1, (int)Math.Ceiling(length / spacing));
            double actual = length / intervals;
            for (int index = 1; index < intervals; index++)
            {
                double station = start + actual * index;
                double x = 0.0;
                double y = 0.0;
                alignment.PointLocation(station, 0.0, ref x, ref y);
                proposals.Add(new AssetProposal(
                    record.RouteName,
                    record.SourceHandle,
                    assetType,
                    assetCode,
                    station,
                    new Point3d(x, y, 0.0),
                    reason));
            }
        }

        private static double SafeStation(CivilAlignment alignment, Point3d point)
        {
            double station = alignment.StartingStation;
            double offset = 0.0;
            try
            {
                alignment.StationOffset(point.X, point.Y, ref station, ref offset);
            }
            catch
            {
                station = alignment.StartingStation;
            }
            return station;
        }

        private static void TryAddPressurePartsToProfileView(
            AlignmentRecord record,
            ObjectId profileViewId,
            Transaction transaction)
        {
            ObjectId sourceId;
            Database database = profileViewId.Database;
            if (!TryGetObjectId(database, record.SourceHandle, out sourceId))
                return;

            DBObject source = transaction.GetObject(sourceId, OpenMode.ForRead, false);
            object networkIdValue = ReadProperty(source, "NetworkId") ??
                                    ReadProperty(source, "PressureNetworkId");
            if (!(networkIdValue is ObjectId) || ((ObjectId)networkIdValue).IsNull)
                return;

            DBObject network = transaction.GetObject((ObjectId)networkIdValue, OpenMode.ForRead, false);
            IEnumerable<ObjectId> partIds = ReadObjectIds(network,
                new[] { "GetPipeIds", "GetPressurePipeIds", "GetFittingIds", "GetAppurtenanceIds" });
            foreach (ObjectId partId in partIds)
            {
                DBObject part = transaction.GetObject(partId, OpenMode.ForRead, false);
                foreach (string methodName in new[] { "AddToProfileView", "AddToProfileViewByParts" })
                {
                    MethodInfo method = part.GetType().GetMethod(
                        methodName,
                        BindingFlags.Public | BindingFlags.Instance,
                        null,
                        new[] { typeof(ObjectId) },
                        null);
                    if (method == null)
                        continue;
                    try
                    {
                        method.Invoke(part, new object[] { profileViewId });
                        break;
                    }
                    catch
                    {
                        // Keep the profile view; exact pressure-part display APIs vary by host.
                    }
                }
            }
        }

        private static IEnumerable<ObjectId> ReadObjectIds(
            object owner,
            IEnumerable<string> methodNames)
        {
            var result = new HashSet<ObjectId>();
            foreach (string methodName in methodNames)
            {
                MethodInfo method = owner.GetType().GetMethod(
                    methodName,
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    Type.EmptyTypes,
                    null);
                if (method == null)
                    continue;
                object value;
                try { value = method.Invoke(owner, null); }
                catch { continue; }
                var enumerable = value as IEnumerable;
                if (enumerable == null)
                    continue;
                foreach (object item in enumerable)
                    if (item is ObjectId && !((ObjectId)item).IsNull)
                        result.Add((ObjectId)item);
            }
            return result;
        }

        private static void RemoveProfileObjects(
            Database database,
            string sourceHandle,
            Transaction transaction)
        {
            BlockTable blocks = (BlockTable)transaction.GetObject(
                database.BlockTableId,
                OpenMode.ForRead,
                false);
            foreach (ObjectId blockId in blocks)
            {
                BlockTableRecord block = (BlockTableRecord)transaction.GetObject(
                    blockId,
                    OpenMode.ForRead,
                    false);
                foreach (ObjectId id in block)
                {
                    DBObject item = transaction.GetObject(id, OpenMode.ForRead, false);
                    string type;
                    string route;
                    string source;
                    string extra;
                    if (TryReadTag(item, out type, out route, out source, out extra) &&
                        source == sourceHandle &&
                        (type == "Profile" || type == "ProfileView"))
                    {
                        item.UpgradeOpen();
                        item.Erase();
                    }
                }
            }
        }

        private static void RemoveGeneratedForSource(
            CivilDocument civilDocument,
            BlockTableRecord modelSpace,
            string sourceHandle,
            Transaction transaction,
            IEnumerable<string> objectTypes)
        {
            var accepted = new HashSet<string>(objectTypes, StringComparer.Ordinal);
            foreach (ObjectId alignmentId in civilDocument.GetAlignmentIds())
            {
                DBObject alignment = transaction.GetObject(
                    alignmentId,
                    OpenMode.ForRead,
                    false);
                string type;
                string route;
                string source;
                string extra;
                if (TryReadTag(alignment, out type, out route, out source, out extra) &&
                    source == sourceHandle && accepted.Contains(type))
                {
                    alignment.UpgradeOpen();
                    alignment.Erase();
                }
            }

            foreach (ObjectId id in modelSpace)
            {
                DBObject item = transaction.GetObject(id, OpenMode.ForRead, false);
                string type;
                string route;
                string source;
                string extra;
                if (TryReadTag(item, out type, out route, out source, out extra) &&
                    source == sourceHandle && accepted.Contains(type))
                {
                    item.UpgradeOpen();
                    item.Erase();
                }
            }
        }

        private static ObjectId InvokeCreateProfileFromSurface(
            string name,
            ObjectId alignmentId,
            ObjectId surfaceId,
            ObjectId layerId,
            ObjectId styleId,
            ObjectId labelSetId)
        {
            Type type = typeof(CivilAlignment).Assembly.GetType(
                "Autodesk.Civil.DatabaseServices.Profile",
                true);
            MethodInfo[] methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(method => method.Name == "CreateFromSurface")
                .ToArray();
            foreach (MethodInfo method in methods)
            {
                object[] values;
                if (!TryBuildProfileArguments(
                        method.GetParameters(),
                        name,
                        alignmentId,
                        surfaceId,
                        layerId,
                        styleId,
                        labelSetId,
                        out values))
                    continue;
                try
                {
                    object result = method.Invoke(null, values);
                    if (result is ObjectId)
                        return (ObjectId)result;
                }
                catch (TargetInvocationException exception)
                {
                    if (exception.InnerException != null)
                        throw exception.InnerException;
                    throw;
                }
            }
            throw new MissingMethodException(
                "No supported Civil 3D Profile.CreateFromSurface overload was found.");
        }

        private static bool TryBuildProfileArguments(
            ParameterInfo[] parameters,
            string name,
            ObjectId alignmentId,
            ObjectId surfaceId,
            ObjectId layerId,
            ObjectId styleId,
            ObjectId labelSetId,
            out object[] values)
        {
            values = new object[parameters.Length];
            int objectIdIndex = 0;
            ObjectId[] ids = { alignmentId, surfaceId, layerId, styleId, labelSetId };
            for (int index = 0; index < parameters.Length; index++)
            {
                Type parameterType = parameters[index].ParameterType;
                if (parameterType == typeof(string))
                    values[index] = name;
                else if (parameterType == typeof(ObjectId) && objectIdIndex < ids.Length)
                    values[index] = ids[objectIdIndex++];
                else if (parameterType == typeof(bool))
                    values[index] = false;
                else
                    return false;
            }
            return objectIdIndex >= 4;
        }

        private static ObjectId InvokeCreateProfileView(
            string name,
            ObjectId alignmentId,
            Point3d point,
            ObjectId bandSetId,
            ObjectId styleId)
        {
            Type type = typeof(CivilAlignment).Assembly.GetType(
                "Autodesk.Civil.DatabaseServices.ProfileView",
                true);
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(item => item.Name == "Create"))
            {
                ParameterInfo[] parameters = method.GetParameters();
                var values = new object[parameters.Length];
                int objectIdIndex = 0;
                ObjectId[] ids = { alignmentId, bandSetId, styleId };
                bool supported = true;
                for (int index = 0; index < parameters.Length; index++)
                {
                    Type parameterType = parameters[index].ParameterType;
                    if (parameterType == typeof(string)) values[index] = name;
                    else if (parameterType == typeof(Point3d)) values[index] = point;
                    else if (parameterType == typeof(ObjectId) && objectIdIndex < ids.Length)
                        values[index] = ids[objectIdIndex++];
                    else if (parameterType == typeof(bool)) values[index] = false;
                    else { supported = false; break; }
                }
                if (!supported)
                    continue;
                try
                {
                    object result = method.Invoke(null, values);
                    if (result is ObjectId)
                        return (ObjectId)result;
                }
                catch (TargetInvocationException exception)
                {
                    if (exception.InnerException != null)
                        throw exception.InnerException;
                    throw;
                }
            }
            throw new MissingMethodException(
                "No supported Civil 3D ProfileView.Create overload was found.");
        }

        private static ObjectId ResolveStyleId(
            IEnumerable<ObjectId> styleIds,
            string requestedName,
            string description,
            Transaction transaction)
        {
            ObjectId first = ObjectId.Null;
            foreach (ObjectId id in styleIds)
            {
                if (first.IsNull)
                    first = id;
                DBObject item = transaction.GetObject(id, OpenMode.ForRead, false);
                string name = Convert.ToString(ReadProperty(item, "Name"), CultureInfo.InvariantCulture);
                if (!string.IsNullOrWhiteSpace(requestedName) &&
                    string.Equals(name, requestedName, StringComparison.OrdinalIgnoreCase))
                    return id;
            }
            if (!first.IsNull)
                return first;
            throw new InvalidOperationException("The drawing contains no " + description + ".");
        }

        internal static bool IsGeneratedRouteLabel(MText text, CivilAlignment alignment)
        {
            string type, route, source, extra, labelType, labelRoute, labelSource, labelExtra;
            return TryReadTag(alignment, out type, out route, out source, out extra) &&
                TryReadTag(text, out labelType, out labelRoute, out labelSource, out labelExtra) &&
                labelType == "RouteLabel" && labelSource == source;
        }

        internal static void LinkBranchLabel(Database database, Transaction transaction, MText text, CivilAlignment alignment)
        {
            string type, route, source, extra;
            if (!TryReadTag(alignment, out type, out route, out source, out extra)) return;
            EnsureRegApp(database, transaction);
            text.XData = BuildTag("RouteLabel", route, source, extra);
        }

        internal static void SyncBranchIdentity(Database database, Transaction transaction, CivilAlignment alignment, string branch)
        {
            EnsureRegApp(database, transaction);
            string type, route, source, extra;
            if (TryReadTag(alignment, out type, out route, out source, out extra))
            {
                alignment.XData = BuildTag(type, branch, source, extra);
                ObjectId sourceId;
                if (TryGetObjectId(database, source, out sourceId) && !sourceId.IsErased)
                {
                    var sourceObject = transaction.GetObject(sourceId, OpenMode.ForWrite, false);
                    sourceObject.XData = BuildTag("Source", branch, source, extra);
                }
                var model = (BlockTableRecord)transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(database), OpenMode.ForRead);
                foreach (ObjectId id in model)
                {
                    var text = transaction.GetObject(id, OpenMode.ForRead, false) as MText;
                    if (text == null) continue;
                    string labelType, labelRoute, labelSource, labelExtra;
                    bool linked = TryReadTag(text, out labelType, out labelRoute, out labelSource, out labelExtra) &&
                        labelType == "RouteLabel" && labelSource == source;
                    using (var tag = text.GetXDataForApplication("CE_WATER_BRANCH_LABEL"))
                        linked |= tag != null && tag.AsArray().Skip(1).Any(v => Convert.ToString(v.Value) == alignment.ObjectId.Handle.ToString());
                    if (!linked) continue;
                    text.UpgradeOpen(); text.Contents = branch;
                    if (labelType == "RouteLabel") text.XData = BuildTag(labelType, branch, labelSource, labelExtra);
                }
            }
        }

        private static string ResolveAlignmentName(
            CivilDocument civilDocument,
            string routeName,
            string sourceHandle,
            ISet<string> reservedNames,
            Transaction transaction)
        {
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ObjectId id in civilDocument.GetAlignmentIds())
            {
                var alignment = transaction.GetObject(id, OpenMode.ForRead, false) as CivilAlignment;
                if (alignment != null)
                    existing.Add(alignment.Name);
            }
            string baseName = routeName;
            string candidate = baseName;
            int suffix = 2;
            while (existing.Contains(candidate) || !reservedNames.Add(candidate))
                candidate = baseName + " (" + suffix++.ToString(CultureInfo.InvariantCulture) + ")";
            return candidate;
        }

        private static ObjectId AddTemporaryPolyline(
            Database database,
            BlockTableRecord modelSpace,
            IReadOnlyList<Point3d> points,
            ObjectId layerId,
            Transaction transaction)
        {
            var polyline = new Polyline(points.Count);
            polyline.SetDatabaseDefaults(database);
            polyline.LayerId = layerId;
            bool closed = points.Count > 3 &&
                points[0].DistanceTo(points[points.Count - 1]) < 0.001;
            int vertexCount = closed ? points.Count - 1 : points.Count;
            for (int index = 0; index < vertexCount; index++)
                polyline.AddVertexAt(
                    index,
                    new Point2d(points[index].X, points[index].Y),
                    0.0, 0.0, 0.0);
            polyline.Closed = closed;
            modelSpace.AppendEntity(polyline);
            transaction.AddNewlyCreatedDBObject(polyline, true);
            return polyline.ObjectId;
        }

        private static ObjectId GetOrCreateLayer(
            Database database,
            Transaction transaction,
            string requested,
            string fallback)
        {
            string name = string.IsNullOrWhiteSpace(requested) ? fallback : requested.Trim();
            LayerTable layers = (LayerTable)transaction.GetObject(
                database.LayerTableId,
                OpenMode.ForRead,
                false);
            if (layers.Has(name))
            {
                ObjectId id = layers[name];
                var layer = transaction.GetObject(id, OpenMode.ForRead, false) as LayerTableRecord;
                if (layer != null && layer.IsLocked)
                    throw new InvalidOperationException("Layer '" + name + "' is locked.");
                return id;
            }
            layers.UpgradeOpen();
            var record = new LayerTableRecord { Name = name };
            ObjectId newId = layers.Add(record);
            transaction.AddNewlyCreatedDBObject(record, true);
            return newId;
        }

        private static Point3d GetRouteLabelPoint(
            IReadOnlyList<Point3d> points,
            int index,
            double height)
        {
            double total = RouteLength(points);
            double target = total * 0.5;
            double travelled = 0.0;
            for (int i = 1; i < points.Count; i++)
            {
                Point3d start = points[i - 1];
                Point3d end = points[i];
                double segment = start.DistanceTo(end);
                if (travelled + segment >= target && segment > Tolerance)
                {
                    double fraction = (target - travelled) / segment;
                    Point3d point = start + ((end - start) * fraction);
                    return point + new Vector3d(0.0, height * (2.0 + index % 4), 0.0);
                }
                travelled += segment;
            }
            return points[points.Count / 2];
        }

        private static double RouteLength(IReadOnlyList<Point3d> points)
        {
            double length = 0.0;
            for (int index = 1; index < points.Count; index++)
                length += points[index - 1].DistanceTo(points[index]);
            return length;
        }

        private static void AddDistinct(ICollection<Point3d> points, Point3d point)
        {
            Point3d flat = new Point3d(point.X, point.Y, point.Z);
            if (points.Count == 0 || points.Last().DistanceTo(flat) > Tolerance)
                points.Add(flat);
        }

        private static bool TryReadPoint(
            object owner,
            IEnumerable<string> names,
            out Point3d point)
        {
            foreach (string name in names)
            {
                object value = ReadProperty(owner, name);
                if (value is Point3d)
                {
                    point = (Point3d)value;
                    return true;
                }
            }
            point = Point3d.Origin;
            return false;
        }

        private static object ReadProperty(object owner, string propertyName)
        {
            PropertyInfo property = owner.GetType().GetProperty(
                propertyName,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (property == null || !property.CanRead)
                return null;
            try { return property.GetValue(owner, null); }
            catch { return null; }
        }

        private static void TrySetProperty(object owner, string propertyName, object value)
        {
            PropertyInfo property = owner.GetType().GetProperty(
                propertyName,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (property == null || !property.CanWrite)
                return;
            try { property.SetValue(owner, value, null); }
            catch { }
        }

        private static bool TryGetObjectId(
            Database database,
            string handleText,
            out ObjectId objectId)
        {
            objectId = ObjectId.Null;
            long value;
            if (!long.TryParse(handleText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value))
                return false;
            try
            {
                objectId = database.GetObjectId(false, new Handle(value), 0);
                return !objectId.IsNull && !objectId.IsErased;
            }
            catch
            {
                return false;
            }
        }

        private static void EnsureRegApp(Database database, Transaction transaction)
        {
            RegAppTable table = (RegAppTable)transaction.GetObject(
                database.RegAppTableId,
                OpenMode.ForRead,
                false);
            if (table.Has(RegAppName))
                return;
            table.UpgradeOpen();
            var record = new RegAppTableRecord { Name = RegAppName };
            table.Add(record);
            transaction.AddNewlyCreatedDBObject(record, true);
        }

        private static ResultBuffer BuildTag(
            string objectType,
            string routeName,
            string sourceHandle,
            string extra)
        {
            return new ResultBuffer(
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, RegAppName),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, objectType ?? string.Empty),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, routeName ?? string.Empty),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, sourceHandle ?? string.Empty),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, extra ?? string.Empty));
        }

        private static bool TryReadTag(
            DBObject item,
            out string objectType,
            out string routeName,
            out string sourceHandle,
            out string extra)
        {
            objectType = routeName = sourceHandle = extra = string.Empty;
            using (ResultBuffer data = item.GetXDataForApplication(RegAppName))
            {
                if (data == null)
                    return false;
                string[] values = data.AsArray()
                    .Where(value => value.TypeCode == (int)DxfCode.ExtendedDataAsciiString)
                    .Select(value => value.Value as string)
                    .Where(value => value != null)
                    .ToArray();
                if (values.Length < 4)
                    return false;
                objectType = values[0];
                routeName = values[1];
                sourceHandle = values[2];
                extra = values[3];
                return true;
            }
        }

        private static bool PromptText(
            Editor editor,
            string label,
            string current,
            out string value)
        {
            var options = new PromptStringOptions(
                "\n" + label + " <" + (current ?? string.Empty) + ">: ")
            {
                AllowSpaces = true
            };
            PromptResult result = editor.GetString(options);
            if (result.Status == PromptStatus.Cancel)
            {
                value = current;
                return false;
            }
            value = result.Status == PromptStatus.None
                ? current
                : result.StringResult.Trim();
            return true;
        }

        private static bool PromptPositiveDouble(
            Editor editor,
            string label,
            double current,
            out double value)
        {
            var options = new PromptDoubleOptions(
                "\n" + label + " <" + current.ToString("0.###", CultureInfo.InvariantCulture) + ">: ")
            {
                AllowNegative = false,
                AllowZero = false,
                UseDefaultValue = true,
                DefaultValue = current
            };
            PromptDoubleResult result = editor.GetDouble(options);
            value = result.Status == PromptStatus.OK ? result.Value : current;
            return result.Status == PromptStatus.OK;
        }

        private static bool Confirm(Editor editor, string message)
        {
            return DisciplineWorkflowDialogs.Confirm("CE Tools — Water", message + "?");
        }

        private static string[] ReadCivilNames(
            IEnumerable<ObjectId> ids,
            Transaction transaction)
        {
            return ids.Select(id => transaction.GetObject(id, OpenMode.ForRead, false))
                .Select(item => Convert.ToString(ReadProperty(item, "Name"), CultureInfo.InvariantCulture))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToArray();
        }

        private static IEnumerable<ObjectId> GetAlignmentProfileIds(
            CivilDocument civilDocument,
            Transaction transaction,
            bool profileViews)
        {
            var result = new List<ObjectId>();
            foreach (ObjectId alignmentId in civilDocument.GetAlignmentIds())
            {
                var alignment = transaction.GetObject(
                    alignmentId,
                    OpenMode.ForRead,
                    false) as CivilAlignment;
                if (alignment == null) continue;
                ObjectIdCollection ids = profileViews
                    ? alignment.GetProfileViewIds()
                    : alignment.GetProfileIds();
                result.AddRange(ids.Cast<ObjectId>());
            }
            return result;
        }

        private static string UniqueName(string preferred, IEnumerable<string> existing)
        {
            var names = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
            string candidate = preferred;
            int suffix = 2;
            while (names.Contains(candidate))
                candidate = preferred + " (" + suffix++.ToString(CultureInfo.InvariantCulture) + ")";
            return candidate;
        }

        private sealed class RouteSource
        {
            public RouteSource(
                ObjectId sourceId,
                string sourceHandle,
                string routeName,
                string sourceKind,
                string sourceDescription,
                IReadOnlyList<Point3d> points)
            {
                SourceId = sourceId;
                SourceHandle = sourceHandle;
                RouteName = routeName;
                SourceKind = sourceKind;
                SourceDescription = sourceDescription;
                Points = points;
                Length = RouteLength(points);
            }

            public ObjectId SourceId { get; }
            public string SourceHandle { get; }
            public string RouteName { get; set; }
            public string SourceKind { get; }
            public string SourceDescription { get; }
            public IReadOnlyList<Point3d> Points { get; set; }
            public double Length { get; set; }
            public double Diameter;
            public double StartElevation;
            public double EndElevation;
        }

        private sealed class AlignmentRecord
        {
            public AlignmentRecord(
                ObjectId alignmentId,
                string routeName,
                string sourceHandle,
                string sourceKind)
            {
                AlignmentId = alignmentId;
                RouteName = routeName;
                SourceHandle = sourceHandle;
                SourceKind = sourceKind;
            }

            public ObjectId AlignmentId { get; }
            public string RouteName { get; set; }
            public string SourceHandle { get; }
            public string SourceKind { get; }
        }

        private sealed class AssetProposal
        {
            public AssetProposal(
                string routeName,
                string sourceHandle,
                string assetType,
                string assetCode,
                double station,
                Point3d point,
                string reason)
            {
                RouteName = routeName;
                SourceHandle = sourceHandle;
                AssetType = assetType;
                AssetCode = assetCode;
                Station = station;
                Point = point;
                Reason = reason;
            }

            public string RouteName { get; set; }
            public string SourceHandle { get; }
            public string AssetType { get; }
            public string AssetCode { get; }
            public double Station { get; }
            public Point3d Point { get; }
            public string Reason { get; }
        }

        private sealed class WaterSettings
        {
            public string AlignmentStyle = string.Empty;
            public string AlignmentLabelSetStyle = string.Empty;
            public string ProfileStyle = string.Empty;
            public string ProfileLabelSetStyle = string.Empty;
            public string ProfileViewStyle = string.Empty;
            public string ProfileViewBandSetStyle = string.Empty;
            public string AlignmentLayer = AlignmentLayerDefault;
            public string ProfileLayer = ProfileLayerDefault;
            public string AssetLayer = AssetLayerDefault;
            public double LabelHeight = 2.0;
            public double IsolatingValveSpacing = 500.0;
            public double HydrantSpacing = 120.0;
            public double AssetRadius = 1.5;
            public int ProfileColumns = 2;
            public double ProfileHorizontalSpacing = 250.0;
            public double ProfileVerticalSpacing = 120.0;

            public static WaterSettings Read(Database database)
            {
                var settings = new WaterSettings();
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    DBDictionary nod = (DBDictionary)transaction.GetObject(
                        database.NamedObjectsDictionaryId,
                        OpenMode.ForRead,
                        false);
                    if (!nod.Contains(SettingsDictionary))
                        return settings;
                    DBDictionary ce = (DBDictionary)transaction.GetObject(
                        nod.GetAt(SettingsDictionary),
                        OpenMode.ForRead,
                        false);
                    if (!ce.Contains(SettingsRecord))
                        return settings;
                    Xrecord record = (Xrecord)transaction.GetObject(
                        ce.GetAt(SettingsRecord),
                        OpenMode.ForRead,
                        false);
                    string[] values = record.Data == null
                        ? new string[0]
                        : record.Data.AsArray()
                            .Where(item => item.TypeCode == (int)DxfCode.Text)
                            .Select(item => Convert.ToString(item.Value, CultureInfo.InvariantCulture))
                            .ToArray();
                    if (values.Length >= 15)
                    {
                        settings.AlignmentStyle = values[0];
                        settings.AlignmentLabelSetStyle = values[1];
                        settings.ProfileStyle = values[2];
                        settings.ProfileLabelSetStyle = values[3];
                        settings.ProfileViewStyle = values[4];
                        settings.ProfileViewBandSetStyle = values[5];
                        settings.AlignmentLayer = values[6];
                        settings.ProfileLayer = values[7];
                        settings.AssetLayer = values[8];
                        double.TryParse(values[9], NumberStyles.Float, CultureInfo.InvariantCulture, out settings.LabelHeight);
                        double.TryParse(values[10], NumberStyles.Float, CultureInfo.InvariantCulture, out settings.IsolatingValveSpacing);
                        double.TryParse(values[11], NumberStyles.Float, CultureInfo.InvariantCulture, out settings.HydrantSpacing);
                        double.TryParse(values[12], NumberStyles.Float, CultureInfo.InvariantCulture, out settings.AssetRadius);
                        int.TryParse(values[13], NumberStyles.Integer, CultureInfo.InvariantCulture, out settings.ProfileColumns);
                        double.TryParse(values[14], NumberStyles.Float, CultureInfo.InvariantCulture, out settings.ProfileHorizontalSpacing);
                        if (values.Length > 15)
                            double.TryParse(values[15], NumberStyles.Float, CultureInfo.InvariantCulture, out settings.ProfileVerticalSpacing);
                    }
                }
                settings.Normalize();
                return settings;
            }

            public void Write(Database database)
            {
                Normalize();
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    DBDictionary nod = (DBDictionary)transaction.GetObject(
                        database.NamedObjectsDictionaryId,
                        OpenMode.ForWrite,
                        false);
                    DBDictionary ce;
                    if (nod.Contains(SettingsDictionary))
                        ce = (DBDictionary)transaction.GetObject(
                            nod.GetAt(SettingsDictionary),
                            OpenMode.ForWrite,
                            false);
                    else
                    {
                        ce = new DBDictionary();
                        nod.SetAt(SettingsDictionary, ce);
                        transaction.AddNewlyCreatedDBObject(ce, true);
                    }

                    Xrecord record;
                    if (ce.Contains(SettingsRecord))
                        record = (Xrecord)transaction.GetObject(
                            ce.GetAt(SettingsRecord),
                            OpenMode.ForWrite,
                            false);
                    else
                    {
                        record = new Xrecord();
                        ce.SetAt(SettingsRecord, record);
                        transaction.AddNewlyCreatedDBObject(record, true);
                    }

                    string[] values =
                    {
                        AlignmentStyle, AlignmentLabelSetStyle, ProfileStyle,
                        ProfileLabelSetStyle, ProfileViewStyle, ProfileViewBandSetStyle,
                        AlignmentLayer, ProfileLayer, AssetLayer,
                        LabelHeight.ToString("R", CultureInfo.InvariantCulture),
                        IsolatingValveSpacing.ToString("R", CultureInfo.InvariantCulture),
                        HydrantSpacing.ToString("R", CultureInfo.InvariantCulture),
                        AssetRadius.ToString("R", CultureInfo.InvariantCulture),
                        ProfileColumns.ToString(CultureInfo.InvariantCulture),
                        ProfileHorizontalSpacing.ToString("R", CultureInfo.InvariantCulture),
                        ProfileVerticalSpacing.ToString("R", CultureInfo.InvariantCulture)
                    };
                    record.Data = new ResultBuffer(values
                        .Select(value => new TypedValue((int)DxfCode.Text, value))
                        .ToArray());
                    transaction.Commit();
                }
            }

            private void Normalize()
            {
                if (string.IsNullOrWhiteSpace(AlignmentLayer)) AlignmentLayer = AlignmentLayerDefault;
                if (string.IsNullOrWhiteSpace(ProfileLayer)) ProfileLayer = ProfileLayerDefault;
                if (string.IsNullOrWhiteSpace(AssetLayer)) AssetLayer = AssetLayerDefault;
                if (LabelHeight <= 0.0) LabelHeight = 2.0;
                if (IsolatingValveSpacing <= 0.0) IsolatingValveSpacing = 500.0;
                if (HydrantSpacing <= 0.0) HydrantSpacing = 120.0;
                if (AssetRadius <= 0.0) AssetRadius = 1.5;
                if (ProfileColumns < 1) ProfileColumns = 2;
                if (ProfileHorizontalSpacing <= 0.0) ProfileHorizontalSpacing = 250.0;
                if (ProfileVerticalSpacing <= 0.0) ProfileVerticalSpacing = 120.0;
            }
        }
    }
}

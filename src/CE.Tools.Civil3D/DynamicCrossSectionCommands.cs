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
using Autodesk.Civil.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

[assembly: CommandClass(typeof(CETools.Civil3D.DynamicCrossSectionCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Creates a linked cross-section drawing from a user-drawn two-point section
    /// line. The source line stores its display settings and generated handles.
    /// Manual refresh is always available; the update manager coalesces source and
    /// design-object modifications and refreshes linked sections when AutoCAD is
    /// quiescent.
    /// </summary>
    public sealed class DynamicCrossSectionCommands
    {
        internal const string LinkRecordName = "CE_DYNAMIC_SECTION";
        internal const string GeneratedRecordName = "CE_DYNAMIC_SECTION_GENERATED";
        private const string SchemaVersion = "1";
        private const double GeometryTolerance = 1e-8;

        [CommandMethod(
            "CE_TOOLS",
            "CE_XSTOOLS",
            CommandFlags.Modal | CommandFlags.Redraw)]
        public void CrossSectionTools()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            var options = new PromptKeywordOptions(
                "\nCross-section tool [Create/Refresh/Information/Detach/Monitor] <Create>: ")
            {
                AllowNone = true
            };
            foreach (string keyword in new[]
            {
                "Create", "Refresh", "Information", "Detach", "Monitor"
            })
            {
                options.Keywords.Add(keyword);
            }

            PromptResult result = document.Editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel) return;
            string mode = result.Status == PromptStatus.None
                ? "Create"
                : result.StringResult;

            if (Equal(mode, "Refresh")) Refresh();
            else if (Equal(mode, "Information")) Information();
            else if (Equal(mode, "Detach")) Detach();
            else if (Equal(mode, "Monitor")) MonitorInformation();
            else Create();
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_XSCREATE",
            CommandFlags.Modal | CommandFlags.Redraw)]
        public void Create()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            PromptEntityResult lineResult = PromptForSectionLine(
                document.Editor,
                "\nSelect a two-point line or open two-vertex polyline for the cross section: ");
            if (lineResult.Status != PromptStatus.OK) return;

            SectionLineGeometry line;
            string existingReason;
            using (Transaction transaction =
                document.Database.TransactionManager.StartTransaction())
            {
                Entity entity = transaction.GetObject(
                    lineResult.ObjectId,
                    OpenMode.ForRead,
                    false) as Entity;
                if (!TryReadSectionLine(entity, out line, out existingReason))
                {
                    document.Editor.WriteMessage(
                        "\nCE_XSCREATE cancelled. {0}",
                        existingReason);
                    return;
                }

                if (HasLink(entity, transaction, LinkRecordName))
                {
                    document.Editor.WriteMessage(
                        "\nCE_XSCREATE cancelled. The selected line is already linked. Use CE_XSREFRESH or CE_XSDETACH.");
                    return;
                }
            }

            PromptPointResult insertionResult = document.Editor.GetPoint(
                "\nPick the lower-left insertion point for the generated cross section: ");
            if (insertionResult.Status != PromptStatus.OK) return;
            Point3d insertionPoint = insertionResult.Value.TransformBy(
                document.Editor.CurrentUserCoordinateSystem);

            double horizontalFactor;
            if (!PromptPositiveDouble(
                document.Editor,
                "\nHorizontal display factor <1.0>: ",
                1.0,
                out horizontalFactor)) return;

            double verticalFactor;
            if (!PromptPositiveDouble(
                document.Editor,
                "\nVertical display factor <1.0>: ",
                1.0,
                out verticalFactor)) return;

            double defaultInterval = Math.Max(line.Length / 40.0, 0.1);
            double sampleInterval;
            if (!PromptPositiveDouble(
                document.Editor,
                "\nSurface sample interval in drawing units <" +
                    defaultInterval.ToString("N3", CultureInfo.CurrentCulture) + ">: ",
                defaultInterval,
                out sampleInterval)) return;

            double defaultCapture = Math.Max(sampleInterval, line.Length * 0.005);
            double captureWidth;
            if (!PromptPositiveDouble(
                document.Editor,
                "\nPlan capture half-width for point/block/utility objects <" +
                    defaultCapture.ToString("N3", CultureInfo.CurrentCulture) + ">: ",
                defaultCapture,
                out captureWidth)) return;

            var provisional = new SectionLink(
                SchemaVersion,
                insertionPoint,
                horizontalFactor,
                verticalFactor,
                sampleInterval,
                captureWidth,
                new List<string>());

            SectionExtraction extraction;
            try
            {
                extraction = ExtractSection(
                    document.Database,
                    lineResult.ObjectId,
                    line,
                    provisional);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_XSCREATE cancelled while reading design elements. {0}",
                    exception.Message);
                return;
            }

            WritePreview(document.Editor, line, provisional, extraction);
            if (!Confirm(document.Editor, "Create and link the generated cross section"))
            {
                document.Editor.WriteMessage(
                    "\nCE_XSCREATE cancelled. No drawing objects were created.");
                return;
            }

            try
            {
                DynamicSectionUpdateManager.BeginInternalUpdate();
                List<string> generated = GenerateSection(
                    document.Database,
                    lineResult.ObjectId,
                    line,
                    provisional,
                    extraction,
                    null);

                DynamicSectionUpdateManager.RegisterLinkedSection(
                    document,
                    lineResult.ObjectId);

                document.Editor.WriteMessage(
                    "\nCE_XSCREATE complete. Profiles={0}; intersected items={1}; generated objects={2}. " +
                    "Grip edits and design-object modifications are monitored while CE Tools is loaded.",
                    extraction.Profiles.Count,
                    extraction.Features.Count,
                    generated.Count);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_XSCREATE failed. No section was committed. {0}",
                    exception.Message);
            }
            finally
            {
                DynamicSectionUpdateManager.EndInternalUpdate();
            }
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_XSREFRESH",
            CommandFlags.Modal | CommandFlags.Redraw)]
        public void Refresh()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            ObjectId sourceId;
            if (!PromptForLinkedSectionSource(
                document,
                "\nSelect a linked section line or generated section object to refresh: ",
                out sourceId)) return;

            RefreshLinkedSection(document, sourceId, true, false);
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_XSINFO",
            CommandFlags.Modal | CommandFlags.Redraw)]
        public void Information()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            ObjectId sourceId;
            if (!PromptForLinkedSectionSource(
                document,
                "\nSelect a linked section line or generated section object for information: ",
                out sourceId)) return;

            try
            {
                SectionLink link;
                SectionLineGeometry line;
                string reason;
                int validGenerated = 0;
                int staleGenerated = 0;

                using (Transaction transaction =
                    document.Database.TransactionManager.StartTransaction())
                {
                    Entity source = transaction.GetObject(
                        sourceId,
                        OpenMode.ForRead,
                        false) as Entity;
                    if (!TryReadSectionLine(source, out line, out reason))
                        throw new InvalidOperationException(reason);
                    link = ReadLink(source, transaction);
                }

                foreach (string handle in link.GeneratedHandles)
                {
                    ObjectId id;
                    if (TryResolveHandle(document.Database, handle, out id))
                        validGenerated++;
                    else
                        staleGenerated++;
                }

                var columns = new List<string> { "Property", "Value" };
                var rows = new List<IList<string>>
                {
                    new List<string> { "Schema", link.Schema },
                    new List<string> { "Source handle", sourceId.Handle.ToString() },
                    new List<string> { "Section-line length", line.Length.ToString("N3", CultureInfo.CurrentCulture) },
                    new List<string> { "Insertion X", link.InsertionPoint.X.ToString("N3", CultureInfo.CurrentCulture) },
                    new List<string> { "Insertion Y", link.InsertionPoint.Y.ToString("N3", CultureInfo.CurrentCulture) },
                    new List<string> { "Horizontal factor", link.HorizontalFactor.ToString("N3", CultureInfo.CurrentCulture) },
                    new List<string> { "Vertical factor", link.VerticalFactor.ToString("N3", CultureInfo.CurrentCulture) },
                    new List<string> { "Surface sample interval", link.SampleInterval.ToString("N3", CultureInfo.CurrentCulture) },
                    new List<string> { "Plan capture half-width", link.CaptureWidth.ToString("N3", CultureInfo.CurrentCulture) },
                    new List<string> { "Valid generated objects", validGenerated.ToString(CultureInfo.InvariantCulture) },
                    new List<string> { "Stale generated handles", staleGenerated.ToString(CultureInfo.InvariantCulture) },
                    new List<string> { "Update mode", "Automatic coalesced refresh + CE_XSREFRESH" }
                };

                GridReportPresenter.ShowReportAndOfferTable(
                    document,
                    "CE Tools Dynamic Cross Section",
                    "The selected source line owns the generated section objects and update settings.",
                    columns,
                    rows,
                    "CE TOOLS DYNAMIC CROSS SECTION");
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_XSINFO cancelled. {0}",
                    exception.Message);
            }
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_XSDETACH",
            CommandFlags.Modal | CommandFlags.Redraw)]
        public void Detach()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            ObjectId sourceId;
            if (!PromptForLinkedSectionSource(
                document,
                "\nSelect a linked section line or generated section object to detach: ",
                out sourceId)) return;

            var options = new PromptKeywordOptions(
                "\nGenerated section geometry [Keep/Delete] <Keep>: ")
            {
                AllowNone = true
            };
            options.Keywords.Add("Keep");
            options.Keywords.Add("Delete");
            PromptResult result = document.Editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel) return;
            bool deleteGenerated = result.Status == PromptStatus.OK &&
                Equal(result.StringResult, "Delete");

            if (!Confirm(
                document.Editor,
                deleteGenerated
                    ? "Detach the link and delete generated geometry"
                    : "Detach the link and keep generated geometry")) return;

            try
            {
                DynamicSectionUpdateManager.BeginInternalUpdate();
                using (Transaction transaction =
                    document.Database.TransactionManager.StartTransaction())
                {
                    Entity source = transaction.GetObject(
                        sourceId,
                        OpenMode.ForWrite,
                        false) as Entity;
                    SectionLink link = ReadLink(source, transaction);

                    if (deleteGenerated)
                    {
                        foreach (string handle in link.GeneratedHandles)
                        {
                            ObjectId id;
                            if (!TryResolveHandle(document.Database, handle, out id))
                                continue;
                            Entity generated = transaction.GetObject(
                                id,
                                OpenMode.ForWrite,
                                false) as Entity;
                            if (generated != null && !generated.IsErased)
                                generated.Erase();
                        }
                    }
                    else
                    {
                        foreach (string handle in link.GeneratedHandles)
                        {
                            ObjectId id;
                            if (!TryResolveHandle(document.Database, handle, out id))
                                continue;
                            Entity generated = transaction.GetObject(
                                id,
                                OpenMode.ForWrite,
                                false) as Entity;
                            if (generated != null)
                                RemoveRecord(generated, transaction, GeneratedRecordName);
                        }
                    }

                    RemoveRecord(source, transaction, LinkRecordName);
                    transaction.Commit();
                }

                DynamicSectionUpdateManager.UnregisterLinkedSection(
                    document,
                    sourceId);
                document.Editor.WriteMessage(
                    "\nCE_XSDETACH complete. Generated geometry was {0}.",
                    deleteGenerated ? "deleted" : "kept as ordinary unlinked objects");
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_XSDETACH failed. No changes were committed. {0}",
                    exception.Message);
            }
            finally
            {
                DynamicSectionUpdateManager.EndInternalUpdate();
            }
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_XSMONITOR",
            CommandFlags.Modal | CommandFlags.Redraw)]
        public void MonitorInformation()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            int linked = DynamicSectionUpdateManager.CountLinkedSections(document);
            document.Editor.WriteMessage(
                "\nCE_XSMONITOR: manager={0}; linked sections in current drawing={1}; pending refresh={2}. " +
                "Updates are coalesced and run only when AutoCAD is quiescent.",
                DynamicSectionUpdateManager.IsInitialized ? "Active" : "Inactive",
                linked,
                DynamicSectionUpdateManager.HasPendingRefresh(document) ? "Yes" : "No");
        }

        internal static bool RefreshLinkedSection(
            Document document,
            ObjectId sourceId,
            bool askForConfirmation,
            bool automatic)
        {
            if (document == null || sourceId.IsNull || sourceId.IsErased)
                return false;

            try
            {
                SectionLink oldLink;
                SectionLineGeometry line;
                string reason;
                using (Transaction transaction =
                    document.Database.TransactionManager.StartTransaction())
                {
                    Entity source = transaction.GetObject(
                        sourceId,
                        OpenMode.ForRead,
                        false) as Entity;
                    if (!TryReadSectionLine(source, out line, out reason))
                        throw new InvalidOperationException(reason);
                    oldLink = ReadLink(source, transaction);
                }

                SectionExtraction extraction = ExtractSection(
                    document.Database,
                    sourceId,
                    line,
                    oldLink);

                if (extraction.Profiles.Count == 0 && extraction.Features.Count == 0)
                {
                    document.Editor.WriteMessage(
                        "\nCE_XSREFRESH stopped. No intersected design elements were found; the existing generated section was left unchanged.");
                    return false;
                }

                if (askForConfirmation)
                {
                    WritePreview(document.Editor, line, oldLink, extraction);
                    if (!Confirm(
                        document.Editor,
                        "Replace the generated cross section from current design geometry"))
                        return false;
                }

                DynamicSectionUpdateManager.BeginInternalUpdate();
                GenerateSection(
                    document.Database,
                    sourceId,
                    line,
                    oldLink,
                    extraction,
                    oldLink.GeneratedHandles);

                if (!automatic)
                {
                    document.Editor.WriteMessage(
                        "\nCE_XSREFRESH complete. Surface profiles={0}; intersected items={1}.",
                        extraction.Profiles.Count,
                        extraction.Features.Count);
                }
                return true;
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    automatic
                        ? "\nCE Tools automatic cross-section refresh skipped. {0}"
                        : "\nCE_XSREFRESH failed. Existing generated geometry was retained. {0}",
                    exception.Message);
                return false;
            }
            finally
            {
                DynamicSectionUpdateManager.EndInternalUpdate();
            }
        }

        private static List<string> GenerateSection(
            Database database,
            ObjectId sourceId,
            SectionLineGeometry line,
            SectionLink settings,
            SectionExtraction extraction,
            IEnumerable<string> oldGeneratedHandles)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                Entity source = transaction.GetObject(
                    sourceId,
                    OpenMode.ForWrite,
                    false) as Entity;
                if (source == null)
                    throw new InvalidOperationException("The linked section line could not be reopened.");

                if (oldGeneratedHandles != null)
                {
                    foreach (string handle in oldGeneratedHandles)
                    {
                        ObjectId oldId;
                        if (!TryResolveHandle(database, handle, out oldId))
                            continue;
                        Entity oldEntity = transaction.GetObject(
                            oldId,
                            OpenMode.ForWrite,
                            false) as Entity;
                        if (oldEntity != null && !oldEntity.IsErased)
                            oldEntity.Erase();
                    }
                }

                BlockTableRecord currentSpace = (BlockTableRecord)transaction.GetObject(
                    database.CurrentSpaceId,
                    OpenMode.ForWrite,
                    false);

                double textHeight = ResolveTextHeight(database);
                double minElevation = extraction.MinimumElevation;
                double maxElevation = extraction.MaximumElevation;
                if (!IsFinite(minElevation) || !IsFinite(maxElevation))
                {
                    minElevation = 0.0;
                    maxElevation = Math.Max(5.0, line.Length * 0.05);
                }

                double range = Math.Max(maxElevation - minElevation, textHeight * 4.0);
                double datumInterval = ResolveDatumInterval(range);
                double datum = Math.Floor(
                    (minElevation - Math.Max(datumInterval, range * 0.08)) /
                    datumInterval) * datumInterval;

                double sectionWidth = line.Length * settings.HorizontalFactor;
                double sectionHeight = Math.Max(
                    (maxElevation - datum) * settings.VerticalFactor,
                    textHeight * 12.0);
                Point3d origin = settings.InsertionPoint;
                string sourceHandle = sourceId.Handle.ToString();
                var generatedHandles = new List<string>();

                var title = new MText();
                title.SetDatabaseDefaults(database);
                title.Location = new Point3d(
                    origin.X,
                    origin.Y + sectionHeight + textHeight * 4.0,
                    origin.Z);
                title.TextHeight = textHeight * 1.4;
                title.Contents = string.Format(
                    CultureInfo.CurrentCulture,
                    "CE TOOLS DYNAMIC CROSS SECTION\\PSource {0} | Width {1:N3} | Datum {2:N3}",
                    sourceHandle,
                    line.Length,
                    datum);
                AddGenerated(
                    database,
                    transaction,
                    currentSpace,
                    title,
                    sourceHandle,
                    generatedHandles);

                var axis = new Line(
                    origin,
                    new Point3d(origin.X + sectionWidth, origin.Y, origin.Z));
                axis.SetDatabaseDefaults(database);
                AddGenerated(
                    database,
                    transaction,
                    currentSpace,
                    axis,
                    sourceHandle,
                    generatedHandles);

                int gridCount = Math.Max(4, Math.Min(20, (int)Math.Ceiling(line.Length / settings.SampleInterval)));
                for (int index = 0; index <= gridCount; index++)
                {
                    double ratio = index / (double)gridCount;
                    double x = origin.X + sectionWidth * ratio;
                    var grid = new Line(
                        new Point3d(x, origin.Y, origin.Z),
                        new Point3d(x, origin.Y + sectionHeight, origin.Z));
                    grid.SetDatabaseDefaults(database);
                    AddGenerated(
                        database,
                        transaction,
                        currentSpace,
                        grid,
                        sourceHandle,
                        generatedHandles);

                    double offset = -line.Length / 2.0 + line.Length * ratio;
                    var label = new DBText();
                    label.SetDatabaseDefaults(database);
                    label.Position = new Point3d(
                        x,
                        origin.Y - textHeight * 1.4,
                        origin.Z);
                    label.Height = textHeight * 0.75;
                    label.TextString = offset.ToString("N2", CultureInfo.CurrentCulture);
                    label.HorizontalMode = TextHorizontalMode.TextCenter;
                    label.AlignmentPoint = label.Position;
                    AddGenerated(
                        database,
                        transaction,
                        currentSpace,
                        label,
                        sourceHandle,
                        generatedHandles);
                }

                int horizontalGridCount = Math.Max(
                    2,
                    Math.Min(15, (int)Math.Ceiling((maxElevation - datum) / datumInterval)));
                for (int index = 0; index <= horizontalGridCount; index++)
                {
                    double elevation = datum + datumInterval * index;
                    double y = origin.Y + (elevation - datum) * settings.VerticalFactor;
                    var grid = new Line(
                        new Point3d(origin.X, y, origin.Z),
                        new Point3d(origin.X + sectionWidth, y, origin.Z));
                    grid.SetDatabaseDefaults(database);
                    AddGenerated(
                        database,
                        transaction,
                        currentSpace,
                        grid,
                        sourceHandle,
                        generatedHandles);

                    var label = new DBText();
                    label.SetDatabaseDefaults(database);
                    label.Position = new Point3d(
                        origin.X - textHeight * 1.2,
                        y,
                        origin.Z);
                    label.Height = textHeight * 0.75;
                    label.TextString = elevation.ToString("N2", CultureInfo.CurrentCulture);
                    label.HorizontalMode = TextHorizontalMode.TextRight;
                    label.AlignmentPoint = label.Position;
                    AddGenerated(
                        database,
                        transaction,
                        currentSpace,
                        label,
                        sourceHandle,
                        generatedHandles);
                }

                foreach (SectionProfile profile in extraction.Profiles)
                {
                    if (profile.Points.Count < 2) continue;
                    var polyline = new Polyline();
                    polyline.SetDatabaseDefaults(database);
                    polyline.Layer = source.Layer;
                    for (int index = 0; index < profile.Points.Count; index++)
                    {
                        SectionPoint point = profile.Points[index];
                        double x = origin.X +
                            (point.Offset + line.Length / 2.0) * settings.HorizontalFactor;
                        double y = origin.Y +
                            (point.Elevation - datum) * settings.VerticalFactor;
                        polyline.AddVertexAt(index, new Point2d(x, y), 0.0, 0.0, 0.0);
                    }
                    AddGenerated(
                        database,
                        transaction,
                        currentSpace,
                        polyline,
                        sourceHandle,
                        generatedHandles);

                    SectionPoint first = profile.Points[0];
                    var profileLabel = new DBText();
                    profileLabel.SetDatabaseDefaults(database);
                    profileLabel.Position = new Point3d(
                        origin.X + textHeight,
                        origin.Y + (first.Elevation - datum) * settings.VerticalFactor + textHeight,
                        origin.Z);
                    profileLabel.Height = textHeight * 0.8;
                    profileLabel.TextString = profile.Name + " [surface]";
                    AddGenerated(
                        database,
                        transaction,
                        currentSpace,
                        profileLabel,
                        sourceHandle,
                        generatedHandles);
                }

                int featureIndex = 0;
                foreach (SectionFeature feature in extraction.Features)
                {
                    double x = origin.X +
                        (feature.Offset + line.Length / 2.0) * settings.HorizontalFactor;
                    double y = origin.Y +
                        (feature.Elevation - datum) * settings.VerticalFactor;

                    double markerRadius = Math.Max(textHeight * 0.55, 0.001);
                    Entity marker;
                    if (feature.IsUtility)
                    {
                        double radius = feature.Diameter > 0.0
                            ? Math.Max(
                                feature.Diameter * settings.VerticalFactor / 2.0,
                                markerRadius)
                            : markerRadius;
                        marker = new Circle(
                            new Point3d(x, y, origin.Z),
                            Vector3d.ZAxis,
                            radius);
                    }
                    else
                    {
                        marker = new Circle(
                            new Point3d(x, y, origin.Z),
                            Vector3d.ZAxis,
                            markerRadius);
                    }
                    marker.SetDatabaseDefaults(database);
                    AddGenerated(
                        database,
                        transaction,
                        currentSpace,
                        marker,
                        sourceHandle,
                        generatedHandles);

                    double labelRise = textHeight * (3.0 + featureIndex % 5 * 1.8);
                    var leader = new Line(
                        new Point3d(x, y + markerRadius, origin.Z),
                        new Point3d(x, y + labelRise, origin.Z));
                    leader.SetDatabaseDefaults(database);
                    AddGenerated(
                        database,
                        transaction,
                        currentSpace,
                        leader,
                        sourceHandle,
                        generatedHandles);

                    var label = new MText();
                    label.SetDatabaseDefaults(database);
                    label.Location = new Point3d(x + textHeight * 0.3, y + labelRise, origin.Z);
                    label.TextHeight = textHeight * 0.72;
                    label.Contents = string.Join(
                        "\\P",
                        feature.Description,
                        "OFF " + feature.Offset.ToString("N3", CultureInfo.CurrentCulture),
                        "Z " + feature.Elevation.ToString("N3", CultureInfo.CurrentCulture),
                        string.IsNullOrWhiteSpace(feature.Size)
                            ? feature.Layer
                            : feature.Layer + " | " + feature.Size);
                    AddGenerated(
                        database,
                        transaction,
                        currentSpace,
                        label,
                        sourceHandle,
                        generatedHandles);
                    featureIndex++;
                }

                var dimension = new AlignedDimension(
                    origin,
                    new Point3d(origin.X + sectionWidth, origin.Y, origin.Z),
                    new Point3d(
                        origin.X + sectionWidth / 2.0,
                        origin.Y - textHeight * 3.8,
                        origin.Z),
                    string.Empty,
                    database.Dimstyle);
                dimension.SetDatabaseDefaults(database);
                dimension.DimensionText = string.Format(
                    CultureInfo.CurrentCulture,
                    "SECTION WIDTH = {0:N3}",
                    line.Length);
                AddGenerated(
                    database,
                    transaction,
                    currentSpace,
                    dimension,
                    sourceHandle,
                    generatedHandles);

                Table schedule = BuildFeatureSchedule(
                    database,
                    origin,
                    sectionWidth,
                    sectionHeight,
                    textHeight,
                    extraction);
                AddGenerated(
                    database,
                    transaction,
                    currentSpace,
                    schedule,
                    sourceHandle,
                    generatedHandles);
                schedule.GenerateLayout();

                WriteLink(
                    source,
                    transaction,
                    new SectionLink(
                        SchemaVersion,
                        settings.InsertionPoint,
                        settings.HorizontalFactor,
                        settings.VerticalFactor,
                        settings.SampleInterval,
                        settings.CaptureWidth,
                        generatedHandles));

                transaction.Commit();
                return generatedHandles;
            }
        }

        private static Table BuildFeatureSchedule(
            Database database,
            Point3d origin,
            double sectionWidth,
            double sectionHeight,
            double textHeight,
            SectionExtraction extraction)
        {
            var rows = new List<SectionScheduleRow>();
            foreach (SectionProfile profile in extraction.Profiles)
            {
                double minimum = profile.Points.Min(item => item.Elevation);
                double maximum = profile.Points.Max(item => item.Elevation);
                rows.Add(new SectionScheduleRow(
                    "SURFACE",
                    profile.Name,
                    string.Empty,
                    minimum.ToString("N3", CultureInfo.CurrentCulture) + " to " +
                        maximum.ToString("N3", CultureInfo.CurrentCulture),
                    profile.Layer,
                    profile.Points.Count.ToString(CultureInfo.InvariantCulture) + " samples"));
            }
            foreach (SectionFeature feature in extraction.Features)
            {
                rows.Add(new SectionScheduleRow(
                    feature.IsUtility ? "UTILITY" : "ELEMENT",
                    feature.Description,
                    feature.Offset.ToString("N3", CultureInfo.CurrentCulture),
                    feature.Elevation.ToString("N3", CultureInfo.CurrentCulture),
                    feature.Layer,
                    feature.Size));
            }

            if (rows.Count == 0)
            {
                rows.Add(new SectionScheduleRow(
                    "NOTE",
                    "No intersected design elements",
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty));
            }

            var table = new Table();
            table.SetDatabaseDefaults(database);
            table.TableStyle = database.Tablestyle;
            table.Position = new Point3d(
                origin.X + sectionWidth + textHeight * 5.0,
                origin.Y + sectionHeight,
                origin.Z);
            table.SetSize(rows.Count + 2, 6);
            table.SetRowHeight(textHeight * 1.9);
            double[] widths =
            {
                textHeight * 6.0,
                textHeight * 18.0,
                textHeight * 7.0,
                textHeight * 10.0,
                textHeight * 12.0,
                textHeight * 12.0
            };
            for (int column = 0; column < widths.Length; column++)
                table.Columns[column].Width = widths[column];

            table.MergeCells(CellRange.Create(table, 0, 0, 0, 5));
            table.Cells[0, 0].TextString = "INTERSECTED DESIGN ELEMENTS";
            table.Cells[0, 0].Alignment = CellAlignment.MiddleCenter;
            table.Cells[0, 0].TextHeight = textHeight;

            string[] headings =
            {
                "CLASS", "DESCRIPTION", "OFFSET", "ELEVATION / RANGE", "LAYER", "SIZE / DETAIL"
            };
            for (int column = 0; column < headings.Length; column++)
            {
                table.Cells[1, column].TextString = headings[column];
                table.Cells[1, column].Alignment = CellAlignment.MiddleCenter;
                table.Cells[1, column].TextHeight = textHeight * 0.8;
            }

            for (int index = 0; index < rows.Count; index++)
            {
                SectionScheduleRow row = rows[index];
                int tableRow = index + 2;
                table.Cells[tableRow, 0].TextString = row.Classification;
                table.Cells[tableRow, 1].TextString = row.Description;
                table.Cells[tableRow, 2].TextString = row.Offset;
                table.Cells[tableRow, 3].TextString = row.Elevation;
                table.Cells[tableRow, 4].TextString = row.Layer;
                table.Cells[tableRow, 5].TextString = row.Detail;
                for (int column = 0; column < 6; column++)
                {
                    table.Cells[tableRow, column].Alignment =
                        column == 1 || column == 4 || column == 5
                            ? CellAlignment.MiddleLeft
                            : CellAlignment.MiddleCenter;
                    table.Cells[tableRow, column].TextHeight = textHeight * 0.75;
                }
            }
            return table;
        }

        private static void AddGenerated(
            Database database,
            Transaction transaction,
            BlockTableRecord currentSpace,
            Entity entity,
            string sourceHandle,
            ICollection<string> generatedHandles)
        {
            currentSpace.AppendEntity(entity);
            transaction.AddNewlyCreatedDBObject(entity, true);
            entity.CreateExtensionDictionary();
            WriteGeneratedOwner(entity, transaction, sourceHandle);
            generatedHandles.Add(entity.Handle.ToString());
        }

        private static SectionExtraction ExtractSection(
            Database database,
            ObjectId sourceId,
            SectionLineGeometry line,
            SectionLink settings)
        {
            var extraction = new SectionExtraction();
            Vector3d axis = line.End - line.Start;
            Vector3d direction = new Vector3d(axis.X, axis.Y, 0.0).GetNormal();

            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTableRecord currentSpace = transaction.GetObject(
                    database.CurrentSpaceId,
                    OpenMode.ForRead,
                    false) as BlockTableRecord;
                if (currentSpace == null)
                    throw new InvalidOperationException("The current drawing space could not be opened.");

                Entity sourceLineEntity = transaction.GetObject(
                    sourceId,
                    OpenMode.ForRead,
                    false) as Entity;
                if (sourceLineEntity == null)
                    throw new InvalidOperationException("The source section line could not be opened.");

                foreach (ObjectId objectId in currentSpace)
                {
                    if (objectId == sourceId || objectId.IsNull || objectId.IsErased)
                        continue;

                    DBObject databaseObject;
                    try
                    {
                        databaseObject = transaction.GetObject(
                            objectId,
                            OpenMode.ForRead,
                            false);
                    }
                    catch
                    {
                        continue;
                    }

                    Entity entity = databaseObject as Entity;
                    if (entity == null || HasLink(entity, transaction, GeneratedRecordName))
                        continue;

                    CivilSurface surface = databaseObject as CivilSurface;
                    if (surface != null)
                    {
                        SectionProfile profile = SampleSurface(
                            surface,
                            line,
                            settings.SampleInterval);
                        if (profile.Points.Count >= 2)
                        {
                            extraction.Profiles.Add(profile);
                            extraction.IncludeElevations(profile.Points.Select(item => item.Elevation));
                        }
                        continue;
                    }

                    var curve = databaseObject as Curve;
                    if (curve != null)
                    {
                        var intersections = new Point3dCollection();
                        try
                        {
                            sourceLineEntity.IntersectWith(
                                curve,
                                Intersect.OnBothOperands,
                                intersections,
                                IntPtr.Zero,
                                IntPtr.Zero);
                        }
                        catch
                        {
                            intersections = new Point3dCollection();
                        }

                        foreach (Point3d intersection in intersections)
                        {
                            SectionFeature feature = CreateFeature(
                                databaseObject,
                                transaction,
                                line,
                                direction,
                                intersection,
                                0.0);
                            if (feature != null)
                            {
                                extraction.Features.Add(feature);
                                extraction.IncludeElevation(feature.Elevation);
                            }
                        }
                        continue;
                    }

                    Point3d representative;
                    if (!TryGetRepresentativePoint(databaseObject, out representative))
                        continue;

                    double parameter;
                    double perpendicular;
                    ProjectToSection(
                        line,
                        direction,
                        representative,
                        out parameter,
                        out perpendicular);
                    if (parameter < -GeometryTolerance ||
                        parameter > line.Length + GeometryTolerance ||
                        perpendicular > settings.CaptureWidth)
                        continue;

                    SectionFeature nearbyFeature = CreateFeature(
                        databaseObject,
                        transaction,
                        line,
                        direction,
                        representative,
                        perpendicular);
                    if (nearbyFeature != null)
                    {
                        extraction.Features.Add(nearbyFeature);
                        extraction.IncludeElevation(nearbyFeature.Elevation);
                    }
                }
            }

            extraction.Features = extraction.Features
                .GroupBy(
                    item => item.SourceHandle + "|" + item.Offset.ToString("F5", CultureInfo.InvariantCulture),
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(item => item.Offset)
                .ToList();
            return extraction;
        }

        private static SectionProfile SampleSurface(
            CivilSurface surface,
            SectionLineGeometry line,
            double sampleInterval)
        {
            string name = string.IsNullOrWhiteSpace(surface.Name)
                ? "Surface " + surface.Handle
                : surface.Name;
            var profile = new SectionProfile(name, surface.Layer);
            int intervals = Math.Max(
                2,
                Math.Min(2000, (int)Math.Ceiling(line.Length / sampleInterval)));

            for (int index = 0; index <= intervals; index++)
            {
                double ratio = index / (double)intervals;
                double distance = line.Length * ratio;
                Point3d plan = Interpolate(line.Start, line.End, ratio);
                try
                {
                    double elevation = surface.FindElevationAtXY(plan.X, plan.Y);
                    if (IsFinite(elevation))
                    {
                        profile.Points.Add(new SectionPoint(
                            distance - line.Length / 2.0,
                            elevation));
                    }
                }
                catch (Autodesk.Civil.PointNotOnEntityException)
                {
                    // Keep sampling; a surface can have holes or partial coverage.
                }
                catch
                {
                    // One failed sample must not abort the complete section.
                }
            }
            return profile;
        }

        private static SectionFeature CreateFeature(
            DBObject databaseObject,
            Transaction transaction,
            SectionLineGeometry line,
            Vector3d direction,
            Point3d representative,
            double planDistance)
        {
            double parameter;
            double perpendicular;
            ProjectToSection(
                line,
                direction,
                representative,
                out parameter,
                out perpendicular);
            if (parameter < -GeometryTolerance || parameter > line.Length + GeometryTolerance)
                return null;

            double elevation = representative.Z;
            double reflected;
            if ((!IsFinite(elevation) || Math.Abs(elevation) <= GeometryTolerance) &&
                TryReadDoubleProperty(
                    databaseObject,
                    out reflected,
                    "Elevation",
                    "RimElevation",
                    "SumpElevation",
                    "InvertElevation",
                    "StartElevation",
                    "EndElevation"))
                elevation = reflected;
            if (!IsFinite(elevation)) elevation = 0.0;

            Entity entity = databaseObject as Entity;
            string layer = entity == null || string.IsNullOrWhiteSpace(entity.Layer)
                ? "0"
                : entity.Layer;
            string typeName = databaseObject.GetType().Name;
            string objectName = GetObjectName(databaseObject, transaction);
            string search = (layer + " " + typeName + " " + objectName).ToUpperInvariant();
            bool utility = ContainsAny(
                search,
                "PIPE", "CULVERT", "SEWER", "STORM", "WATER", "DRAIN",
                "MANHOLE", "CATCHPIT", "VALVE", "HYDRANT", "UTILITY");
            double diameter = 0.0;
            string size = ReadSize(databaseObject, out diameter);
            string description = DescribeFeature(search, typeName, objectName);

            return new SectionFeature(
                databaseObject.ObjectId.Handle.ToString(),
                layer,
                description,
                parameter - line.Length / 2.0,
                elevation,
                utility,
                size,
                diameter,
                Math.Max(planDistance, perpendicular));
        }

        private static string DescribeFeature(
            string search,
            string typeName,
            string objectName)
        {
            if (ContainsAny(search, "KERB", "CURB") && ContainsAny(search, "CHANNEL"))
                return "Kerb and channel";
            if (ContainsAny(search, "KERB", "CURB")) return "Kerb";
            if (ContainsAny(search, "V-DRAIN", "VDRAIN")) return "V-drain";
            if (ContainsAny(search, "SIDEWALK", "FOOTWAY")) return "Sidewalk layer/surface";
            if (ContainsAny(search, "PARKING", "DRIVEWAY")) return "Parking/driveway layer";
            if (ContainsAny(search, "SUBBASE")) return "Subbase layer";
            if (ContainsAny(search, "BASECOURSE", "BASE COURSE")) return "Basecourse layer";
            if (ContainsAny(search, "SUBGRADE")) return "Subgrade";
            if (ContainsAny(search, "SURFACING", "ASPHALT", "PAVEMENT")) return "Surfacing";
            if (ContainsAny(search, "CULVERT")) return "Culvert";
            if (ContainsAny(search, "MANHOLE")) return "Manhole";
            if (ContainsAny(search, "CATCHPIT")) return "Catchpit";
            if (ContainsAny(search, "HYDRANT")) return "Hydrant";
            if (ContainsAny(search, "VALVE")) return "Valve";
            if (ContainsAny(search, "SEWER")) return "Sewer utility";
            if (ContainsAny(search, "STORM", "DRAIN")) return "Stormwater utility";
            if (ContainsAny(search, "WATER", "MAIN")) return "Water utility";
            if (ContainsAny(search, "PIPE")) return "Pipe utility";
            if (ContainsAny(search, "FEATURELINE")) return "Feature line";
            if (ContainsAny(search, "ALIGNMENT")) return "Alignment";
            if (!string.IsNullOrWhiteSpace(objectName) && !Equal(objectName, typeName))
                return objectName;
            return FriendlyTypeName(typeName);
        }

        private static bool TryGetRepresentativePoint(
            DBObject databaseObject,
            out Point3d point)
        {
            point = Point3d.Origin;
            var dbPoint = databaseObject as DBPoint;
            if (dbPoint != null)
            {
                point = dbPoint.Position;
                return true;
            }

            var block = databaseObject as BlockReference;
            if (block != null)
            {
                point = block.Position;
                return true;
            }

            foreach (string propertyName in new[]
            {
                "Location", "Position", "CenterPoint", "InsertionPoint",
                "StartPoint", "EndPoint"
            })
            {
                try
                {
                    PropertyInfo property = databaseObject.GetType().GetProperty(
                        propertyName,
                        BindingFlags.Public | BindingFlags.Instance);
                    if (property == null || property.GetIndexParameters().Length != 0)
                        continue;
                    object value = property.GetValue(databaseObject, null);
                    if (value is Point3d)
                    {
                        point = (Point3d)value;
                        return true;
                    }
                }
                catch
                {
                    // Try the next property.
                }
            }

            Entity entity = databaseObject as Entity;
            if (entity != null)
            {
                try
                {
                    Extents3d extents = entity.GeometricExtents;
                    point = new Point3d(
                        (extents.MinPoint.X + extents.MaxPoint.X) / 2.0,
                        (extents.MinPoint.Y + extents.MaxPoint.Y) / 2.0,
                        (extents.MinPoint.Z + extents.MaxPoint.Z) / 2.0);
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            return false;
        }

        private static string ReadSize(DBObject value, out double diameter)
        {
            diameter = 0.0;
            if (!TryReadDoubleProperty(
                value,
                out diameter,
                "InnerDiameterOrWidth",
                "NominalDiameter",
                "Diameter",
                "OutsideDiameter",
                "Width")) return string.Empty;

            return diameter.ToString("N3", CultureInfo.CurrentCulture) + " drawing units";
        }

        private static bool TryReadDoubleProperty(
            object value,
            out double number,
            params string[] propertyNames)
        {
            number = 0.0;
            if (value == null) return false;

            foreach (string propertyName in propertyNames)
            {
                try
                {
                    PropertyInfo property = value.GetType().GetProperty(
                        propertyName,
                        BindingFlags.Public | BindingFlags.Instance);
                    if (property == null || property.GetIndexParameters().Length != 0)
                        continue;
                    object raw = property.GetValue(value, null);
                    if (raw == null) continue;
                    number = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
                    if (IsFinite(number)) return true;
                }
                catch
                {
                    // Try the next property.
                }
            }
            return false;
        }

        private static string GetObjectName(DBObject value, Transaction transaction)
        {
            var block = value as BlockReference;
            if (block != null)
            {
                try
                {
                    ObjectId recordId = block.IsDynamicBlock
                        ? block.DynamicBlockTableRecord
                        : block.BlockTableRecord;
                    BlockTableRecord record = transaction.GetObject(
                        recordId,
                        OpenMode.ForRead,
                        false) as BlockTableRecord;
                    if (record != null) return record.Name;
                }
                catch
                {
                    return "BlockReference";
                }
            }

            try
            {
                PropertyInfo property = value.GetType().GetProperty(
                    "Name",
                    BindingFlags.Public | BindingFlags.Instance);
                string name = property == null
                    ? null
                    : property.GetValue(value, null) as string;
                if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
            }
            catch
            {
                // Use runtime type.
            }
            return value.GetType().Name;
        }

        private static void ProjectToSection(
            SectionLineGeometry line,
            Vector3d direction,
            Point3d point,
            out double parameter,
            out double perpendicular)
        {
            Vector3d fromStart = new Vector3d(
                point.X - line.Start.X,
                point.Y - line.Start.Y,
                0.0);
            parameter = fromStart.DotProduct(direction);
            Point3d projected = new Point3d(
                line.Start.X + direction.X * parameter,
                line.Start.Y + direction.Y * parameter,
                point.Z);
            perpendicular = new Point2d(point.X, point.Y).GetDistanceTo(
                new Point2d(projected.X, projected.Y));
        }

        private static Point3d Interpolate(Point3d start, Point3d end, double ratio)
        {
            return new Point3d(
                start.X + (end.X - start.X) * ratio,
                start.Y + (end.Y - start.Y) * ratio,
                start.Z + (end.Z - start.Z) * ratio);
        }

        private static bool PromptForLinkedSectionSource(
            Document document,
            string message,
            out ObjectId sourceId)
        {
            sourceId = ObjectId.Null;
            PromptEntityResult result = document.Editor.GetEntity(
                new PromptEntityOptions(message));
            if (result.Status != PromptStatus.OK) return false;

            using (Transaction transaction =
                document.Database.TransactionManager.StartTransaction())
            {
                Entity selected = transaction.GetObject(
                    result.ObjectId,
                    OpenMode.ForRead,
                    false) as Entity;
                if (selected == null)
                {
                    document.Editor.WriteMessage(
                        "\nThe selected object is not a drawing entity.");
                    return false;
                }

                if (HasLink(selected, transaction, LinkRecordName))
                {
                    sourceId = result.ObjectId;
                    return true;
                }

                string ownerHandle;
                if (TryReadGeneratedOwner(selected, transaction, out ownerHandle) &&
                    TryResolveHandle(document.Database, ownerHandle, out sourceId))
                    return true;
            }

            document.Editor.WriteMessage(
                "\nThe selected object is not part of a CE Tools linked cross section.");
            return false;
        }

        private static PromptEntityResult PromptForSectionLine(
            Editor editor,
            string message)
        {
            var options = new PromptEntityOptions(message);
            options.SetRejectMessage(
                "\nSelect an AutoCAD Line or lightweight Polyline.");
            options.AddAllowedClass(typeof(Line), false);
            options.AddAllowedClass(typeof(Polyline), false);
            return editor.GetEntity(options);
        }

        private static bool TryReadSectionLine(
            Entity entity,
            out SectionLineGeometry geometry,
            out string reason)
        {
            geometry = null;
            reason = string.Empty;

            var line = entity as Line;
            if (line != null)
            {
                double length = Distance2d(line.StartPoint, line.EndPoint);
                if (length <= GeometryTolerance)
                {
                    reason = "The selected line has zero plan length.";
                    return false;
                }
                geometry = new SectionLineGeometry(line.StartPoint, line.EndPoint, length);
                return true;
            }

            var polyline = entity as Polyline;
            if (polyline != null)
            {
                if (polyline.Closed || polyline.NumberOfVertices != 2)
                {
                    reason = "Use an open lightweight polyline with exactly two vertices.";
                    return false;
                }
                Point3d start = polyline.GetPoint3dAt(0);
                Point3d end = polyline.GetPoint3dAt(1);
                double length = Distance2d(start, end);
                if (length <= GeometryTolerance)
                {
                    reason = "The selected polyline has zero plan length.";
                    return false;
                }
                geometry = new SectionLineGeometry(start, end, length);
                return true;
            }

            reason = "Select an AutoCAD Line or an open two-vertex lightweight Polyline.";
            return false;
        }

        private static void WritePreview(
            Editor editor,
            SectionLineGeometry line,
            SectionLink settings,
            SectionExtraction extraction)
        {
            editor.WriteMessage(
                "\nCE Tools cross-section preview. Width={0:N3}; surface profiles={1}; intersected elements={2}; " +
                "horizontal factor={3:N3}; vertical factor={4:N3}; sample interval={5:N3}; capture half-width={6:N3}.",
                line.Length,
                extraction.Profiles.Count,
                extraction.Features.Count,
                settings.HorizontalFactor,
                settings.VerticalFactor,
                settings.SampleInterval,
                settings.CaptureWidth);

            foreach (SectionProfile profile in extraction.Profiles.Take(8))
            {
                editor.WriteMessage(
                    "\n  SURFACE {0}: {1} valid samples.",
                    profile.Name,
                    profile.Points.Count);
            }
            foreach (SectionFeature feature in extraction.Features.Take(15))
            {
                editor.WriteMessage(
                    "\n  {0}: offset {1:N3}; elevation {2:N3}; layer {3}{4}.",
                    feature.Description,
                    feature.Offset,
                    feature.Elevation,
                    feature.Layer,
                    string.IsNullOrWhiteSpace(feature.Size) ? string.Empty : "; " + feature.Size);
            }
            if (extraction.Features.Count > 15)
                editor.WriteMessage(
                    "\n  ... {0} additional intersected elements.",
                    extraction.Features.Count - 15);
        }

        private static void WriteLink(
            Entity source,
            Transaction transaction,
            SectionLink link)
        {
            if (source.ExtensionDictionary.IsNull)
                source.CreateExtensionDictionary();
            DBDictionary dictionary = transaction.GetObject(
                source.ExtensionDictionary,
                OpenMode.ForWrite,
                false) as DBDictionary;
            if (dictionary == null)
                throw new InvalidOperationException("The section-line extension dictionary could not be opened.");

            Xrecord record = OpenOrCreateRecord(
                dictionary,
                LinkRecordName,
                transaction);
            var values = new List<TypedValue>
            {
                new TypedValue((int)DxfCode.Text, "Schema=" + SchemaVersion),
                new TypedValue((int)DxfCode.Text, "InsertionX=" + link.InsertionPoint.X.ToString("R", CultureInfo.InvariantCulture)),
                new TypedValue((int)DxfCode.Text, "InsertionY=" + link.InsertionPoint.Y.ToString("R", CultureInfo.InvariantCulture)),
                new TypedValue((int)DxfCode.Text, "InsertionZ=" + link.InsertionPoint.Z.ToString("R", CultureInfo.InvariantCulture)),
                new TypedValue((int)DxfCode.Text, "HorizontalFactor=" + link.HorizontalFactor.ToString("R", CultureInfo.InvariantCulture)),
                new TypedValue((int)DxfCode.Text, "VerticalFactor=" + link.VerticalFactor.ToString("R", CultureInfo.InvariantCulture)),
                new TypedValue((int)DxfCode.Text, "SampleInterval=" + link.SampleInterval.ToString("R", CultureInfo.InvariantCulture)),
                new TypedValue((int)DxfCode.Text, "CaptureWidth=" + link.CaptureWidth.ToString("R", CultureInfo.InvariantCulture))
            };
            foreach (string handle in link.GeneratedHandles.Distinct(StringComparer.OrdinalIgnoreCase))
                values.Add(new TypedValue((int)DxfCode.Text, "Generated=" + handle));
            record.Data = new ResultBuffer(values.ToArray());
        }

        private static SectionLink ReadLink(Entity source, Transaction transaction)
        {
            if (source == null)
                throw new InvalidOperationException("The linked section source is not an entity.");
            if (source.ExtensionDictionary.IsNull)
                throw new InvalidOperationException("The selected section line has no CE Tools link record.");

            DBDictionary dictionary = transaction.GetObject(
                source.ExtensionDictionary,
                OpenMode.ForRead,
                false) as DBDictionary;
            if (dictionary == null || !dictionary.Contains(LinkRecordName))
                throw new InvalidOperationException("The selected section line has no CE Tools link record.");

            Xrecord record = transaction.GetObject(
                dictionary.GetAt(LinkRecordName),
                OpenMode.ForRead,
                false) as Xrecord;
            if (record == null || record.Data == null)
                throw new InvalidOperationException("The CE Tools section link record is empty.");

            string schema = SchemaVersion;
            double x = 0.0;
            double y = 0.0;
            double z = 0.0;
            double horizontal = 1.0;
            double vertical = 1.0;
            double interval = 1.0;
            double capture = 1.0;
            var generated = new List<string>();

            foreach (TypedValue value in record.Data)
            {
                string text = value.Value as string;
                if (string.IsNullOrWhiteSpace(text)) continue;
                if (text.StartsWith("Schema=", StringComparison.OrdinalIgnoreCase))
                    schema = text.Substring("Schema=".Length);
                else if (text.StartsWith("InsertionX=", StringComparison.OrdinalIgnoreCase))
                    TryParseInvariant(text.Substring("InsertionX=".Length), out x);
                else if (text.StartsWith("InsertionY=", StringComparison.OrdinalIgnoreCase))
                    TryParseInvariant(text.Substring("InsertionY=".Length), out y);
                else if (text.StartsWith("InsertionZ=", StringComparison.OrdinalIgnoreCase))
                    TryParseInvariant(text.Substring("InsertionZ=".Length), out z);
                else if (text.StartsWith("HorizontalFactor=", StringComparison.OrdinalIgnoreCase))
                    TryParseInvariant(text.Substring("HorizontalFactor=".Length), out horizontal);
                else if (text.StartsWith("VerticalFactor=", StringComparison.OrdinalIgnoreCase))
                    TryParseInvariant(text.Substring("VerticalFactor=".Length), out vertical);
                else if (text.StartsWith("SampleInterval=", StringComparison.OrdinalIgnoreCase))
                    TryParseInvariant(text.Substring("SampleInterval=".Length), out interval);
                else if (text.StartsWith("CaptureWidth=", StringComparison.OrdinalIgnoreCase))
                    TryParseInvariant(text.Substring("CaptureWidth=".Length), out capture);
                else if (text.StartsWith("Generated=", StringComparison.OrdinalIgnoreCase))
                    generated.Add(text.Substring("Generated=".Length));
            }

            if (!IsFinitePositive(horizontal) || !IsFinitePositive(vertical) ||
                !IsFinitePositive(interval) || !IsFinitePositive(capture))
                throw new InvalidOperationException("The stored cross-section display settings are invalid.");

            return new SectionLink(
                schema,
                new Point3d(x, y, z),
                horizontal,
                vertical,
                interval,
                capture,
                generated);
        }

        private static void WriteGeneratedOwner(
            Entity generated,
            Transaction transaction,
            string sourceHandle)
        {
            DBDictionary dictionary = transaction.GetObject(
                generated.ExtensionDictionary,
                OpenMode.ForWrite,
                false) as DBDictionary;
            Xrecord record = OpenOrCreateRecord(
                dictionary,
                GeneratedRecordName,
                transaction);
            record.Data = new ResultBuffer(
                new TypedValue((int)DxfCode.Text, "Source=" + sourceHandle));
        }

        private static bool TryReadGeneratedOwner(
            Entity generated,
            Transaction transaction,
            out string sourceHandle)
        {
            sourceHandle = string.Empty;
            if (generated == null || generated.ExtensionDictionary.IsNull)
                return false;
            DBDictionary dictionary = transaction.GetObject(
                generated.ExtensionDictionary,
                OpenMode.ForRead,
                false) as DBDictionary;
            if (dictionary == null || !dictionary.Contains(GeneratedRecordName))
                return false;
            Xrecord record = transaction.GetObject(
                dictionary.GetAt(GeneratedRecordName),
                OpenMode.ForRead,
                false) as Xrecord;
            if (record == null || record.Data == null) return false;
            foreach (TypedValue value in record.Data)
            {
                string text = value.Value as string;
                if (text != null && text.StartsWith("Source=", StringComparison.OrdinalIgnoreCase))
                {
                    sourceHandle = text.Substring("Source=".Length);
                    return !string.IsNullOrWhiteSpace(sourceHandle);
                }
            }
            return false;
        }

        internal static bool HasLink(
            Entity entity,
            Transaction transaction,
            string recordName)
        {
            if (entity == null || entity.ExtensionDictionary.IsNull) return false;
            DBDictionary dictionary = transaction.GetObject(
                entity.ExtensionDictionary,
                OpenMode.ForRead,
                false) as DBDictionary;
            return dictionary != null && dictionary.Contains(recordName);
        }

        private static Xrecord OpenOrCreateRecord(
            DBDictionary dictionary,
            string name,
            Transaction transaction)
        {
            if (dictionary.Contains(name))
            {
                return transaction.GetObject(
                    dictionary.GetAt(name),
                    OpenMode.ForWrite,
                    false) as Xrecord;
            }
            var record = new Xrecord();
            dictionary.SetAt(name, record);
            transaction.AddNewlyCreatedDBObject(record, true);
            return record;
        }

        private static void RemoveRecord(
            Entity entity,
            Transaction transaction,
            string name)
        {
            if (entity == null || entity.ExtensionDictionary.IsNull) return;
            DBDictionary dictionary = transaction.GetObject(
                entity.ExtensionDictionary,
                OpenMode.ForWrite,
                false) as DBDictionary;
            if (dictionary == null || !dictionary.Contains(name)) return;
            DBObject record = transaction.GetObject(
                dictionary.GetAt(name),
                OpenMode.ForWrite,
                false);
            dictionary.Remove(name);
            record.Erase();
        }

        internal static List<ObjectId> FindLinkedSectionSources(Database database)
        {
            var ids = new List<ObjectId>();
            if (database == null) return ids;
            try
            {
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    BlockTableRecord currentSpace = transaction.GetObject(
                        database.CurrentSpaceId,
                        OpenMode.ForRead,
                        false) as BlockTableRecord;
                    if (currentSpace == null) return ids;
                    foreach (ObjectId id in currentSpace)
                    {
                        Entity entity = transaction.GetObject(
                            id,
                            OpenMode.ForRead,
                            false) as Entity;
                        if (entity != null && HasLink(entity, transaction, LinkRecordName))
                            ids.Add(id);
                    }
                }
            }
            catch
            {
                // A manager scan must not destabilise AutoCAD.
            }
            return ids;
        }

        internal static bool TryResolveHandle(
            Database database,
            string handleText,
            out ObjectId objectId)
        {
            objectId = ObjectId.Null;
            long value;
            if (!long.TryParse(
                handleText,
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out value)) return false;
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

        private static bool PromptPositiveDouble(
            Editor editor,
            string message,
            double defaultValue,
            out double value)
        {
            var options = new PromptDoubleOptions(message)
            {
                AllowNone = true,
                AllowNegative = false,
                AllowZero = false,
                DefaultValue = defaultValue,
                UseDefaultValue = true
            };
            PromptDoubleResult result = editor.GetDouble(options);
            value = result.Status == PromptStatus.OK ? result.Value : defaultValue;
            return result.Status == PromptStatus.OK && IsFinitePositive(value);
        }

        private static bool Confirm(Editor editor, string message)
        {
            var options = new PromptKeywordOptions(
                "\n" + message + "? [Yes/No] <No>: ")
            {
                AllowNone = true
            };
            options.Keywords.Add("Yes");
            options.Keywords.Add("No");
            PromptResult result = editor.GetKeywords(options);
            return result.Status == PromptStatus.OK &&
                Equal(result.StringResult, "Yes");
        }

        private static bool TryParseInvariant(string text, out double value)
        {
            return double.TryParse(
                text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out value);
        }

        private static double Distance2d(Point3d first, Point3d second)
        {
            return new Point2d(first.X, first.Y).GetDistanceTo(
                new Point2d(second.X, second.Y));
        }

        private static double ResolveTextHeight(Database database)
        {
            double height = database == null ? 2.0 : database.Textsize;
            if (Math.Abs(height - 1.8) < 0.05) return 1.8;
            if (Math.Abs(height - 5.0) < 0.05) return 5.0;
            return 2.0;
        }

        private static double ResolveDatumInterval(double range)
        {
            if (range <= 5.0) return 0.5;
            if (range <= 20.0) return 1.0;
            if (range <= 100.0) return 5.0;
            if (range <= 500.0) return 10.0;
            return 50.0;
        }

        private static bool ContainsAny(string source, params string[] values)
        {
            if (string.IsNullOrWhiteSpace(source)) return false;
            foreach (string value in values)
            {
                if (source.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        private static string FriendlyTypeName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "Design element";
            var characters = new List<char>();
            for (int index = 0; index < value.Length; index++)
            {
                char character = value[index];
                if (index > 0 && char.IsUpper(character) && !char.IsUpper(value[index - 1]))
                    characters.Add(' ');
                characters.Add(character);
            }
            return new string(characters.ToArray());
        }

        private static bool Equal(string left, string right)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsFinitePositive(double value)
        {
            return IsFinite(value) && value > 0.0;
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static Document ActiveDocument()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
        }

        internal sealed class SectionLineGeometry
        {
            public SectionLineGeometry(Point3d start, Point3d end, double length)
            {
                Start = start;
                End = end;
                Length = length;
            }

            public Point3d Start { get; }
            public Point3d End { get; }
            public double Length { get; }
        }

        internal sealed class SectionLink
        {
            public SectionLink(
                string schema,
                Point3d insertionPoint,
                double horizontalFactor,
                double verticalFactor,
                double sampleInterval,
                double captureWidth,
                IEnumerable<string> generatedHandles)
            {
                Schema = string.IsNullOrWhiteSpace(schema) ? SchemaVersion : schema;
                InsertionPoint = insertionPoint;
                HorizontalFactor = horizontalFactor;
                VerticalFactor = verticalFactor;
                SampleInterval = sampleInterval;
                CaptureWidth = captureWidth;
                GeneratedHandles = generatedHandles == null
                    ? new List<string>()
                    : generatedHandles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }

            public string Schema { get; }
            public Point3d InsertionPoint { get; }
            public double HorizontalFactor { get; }
            public double VerticalFactor { get; }
            public double SampleInterval { get; }
            public double CaptureWidth { get; }
            public List<string> GeneratedHandles { get; }
        }

        private sealed class SectionExtraction
        {
            public SectionExtraction()
            {
                Profiles = new List<SectionProfile>();
                Features = new List<SectionFeature>();
                MinimumElevation = double.PositiveInfinity;
                MaximumElevation = double.NegativeInfinity;
            }

            public List<SectionProfile> Profiles { get; }
            public List<SectionFeature> Features { get; set; }
            public double MinimumElevation { get; private set; }
            public double MaximumElevation { get; private set; }

            public void IncludeElevation(double elevation)
            {
                if (!IsFinite(elevation)) return;
                MinimumElevation = Math.Min(MinimumElevation, elevation);
                MaximumElevation = Math.Max(MaximumElevation, elevation);
            }

            public void IncludeElevations(IEnumerable<double> elevations)
            {
                foreach (double elevation in elevations) IncludeElevation(elevation);
            }
        }

        private sealed class SectionProfile
        {
            public SectionProfile(string name, string layer)
            {
                Name = name ?? string.Empty;
                Layer = layer ?? string.Empty;
                Points = new List<SectionPoint>();
            }

            public string Name { get; }
            public string Layer { get; }
            public List<SectionPoint> Points { get; }
        }

        private sealed class SectionPoint
        {
            public SectionPoint(double offset, double elevation)
            {
                Offset = offset;
                Elevation = elevation;
            }

            public double Offset { get; }
            public double Elevation { get; }
        }

        private sealed class SectionFeature
        {
            public SectionFeature(
                string sourceHandle,
                string layer,
                string description,
                double offset,
                double elevation,
                bool isUtility,
                string size,
                double diameter,
                double planDistance)
            {
                SourceHandle = sourceHandle;
                Layer = layer;
                Description = description;
                Offset = offset;
                Elevation = elevation;
                IsUtility = isUtility;
                Size = size;
                Diameter = diameter;
                PlanDistance = planDistance;
            }

            public string SourceHandle { get; }
            public string Layer { get; }
            public string Description { get; }
            public double Offset { get; }
            public double Elevation { get; }
            public bool IsUtility { get; }
            public string Size { get; }
            public double Diameter { get; }
            public double PlanDistance { get; }
        }

        private sealed class SectionScheduleRow
        {
            public SectionScheduleRow(
                string classification,
                string description,
                string offset,
                string elevation,
                string layer,
                string detail)
            {
                Classification = classification;
                Description = description;
                Offset = offset;
                Elevation = elevation;
                Layer = layer;
                Detail = detail;
            }

            public string Classification { get; }
            public string Description { get; }
            public string Offset { get; }
            public string Elevation { get; }
            public string Layer { get; }
            public string Detail { get; }
        }
    }

    /// <summary>
    /// Watches drawing modifications and coalesces linked-section refreshes. The
    /// manager never mutates a drawing from inside ObjectModified. Refresh occurs
    /// later on Application.Idle and only when the editor is quiescent.
    /// </summary>
    internal static class DynamicSectionUpdateManager
    {
        private static readonly Dictionary<Database, Document> Documents =
            new Dictionary<Database, Document>();
        private static readonly HashSet<Database> Pending =
            new HashSet<Database>();
        private static bool _internalUpdate;

        public static bool IsInitialized { get; private set; }

        public static void Initialize()
        {
            if (IsInitialized) return;
            IsInitialized = true;
            AcApplication.DocumentManager.DocumentCreated += OnDocumentCreated;
            AcApplication.DocumentManager.DocumentActivated += OnDocumentActivated;
            AcApplication.DocumentManager.DocumentToBeDestroyed += OnDocumentToBeDestroyed;
            AcApplication.Idle += OnIdle;
            Attach(AcApplication.DocumentManager.MdiActiveDocument);
        }

        public static void Terminate()
        {
            if (!IsInitialized) return;
            AcApplication.DocumentManager.DocumentCreated -= OnDocumentCreated;
            AcApplication.DocumentManager.DocumentActivated -= OnDocumentActivated;
            AcApplication.DocumentManager.DocumentToBeDestroyed -= OnDocumentToBeDestroyed;
            AcApplication.Idle -= OnIdle;

            foreach (KeyValuePair<Database, Document> item in Documents.ToList())
                Detach(item.Value);
            Documents.Clear();
            Pending.Clear();
            IsInitialized = false;
        }

        public static void BeginInternalUpdate()
        {
            _internalUpdate = true;
        }

        public static void EndInternalUpdate()
        {
            _internalUpdate = false;
        }

        public static void RegisterLinkedSection(Document document, ObjectId sourceId)
        {
            Attach(document);
            if (document != null) Pending.Remove(document.Database);
        }

        public static void UnregisterLinkedSection(Document document, ObjectId sourceId)
        {
            if (document != null) Pending.Remove(document.Database);
        }

        public static int CountLinkedSections(Document document)
        {
            return document == null
                ? 0
                : DynamicCrossSectionCommands.FindLinkedSectionSources(document.Database).Count;
        }

        public static bool HasPendingRefresh(Document document)
        {
            return document != null && Pending.Contains(document.Database);
        }

        private static void OnDocumentCreated(object sender, DocumentCollectionEventArgs args)
        {
            Attach(args.Document);
        }

        private static void OnDocumentActivated(object sender, DocumentCollectionEventArgs args)
        {
            Attach(args.Document);
        }

        private static void OnDocumentToBeDestroyed(object sender, DocumentCollectionEventArgs args)
        {
            Detach(args.Document);
        }

        private static void Attach(Document document)
        {
            if (document == null || Documents.ContainsKey(document.Database)) return;
            Documents.Add(document.Database, document);
            document.Database.ObjectModified += OnDatabaseObjectChanged;
            document.Database.ObjectAppended += OnDatabaseObjectChanged;
        }

        private static void Detach(Document document)
        {
            if (document == null || !Documents.ContainsKey(document.Database)) return;
            document.Database.ObjectModified -= OnDatabaseObjectChanged;
            document.Database.ObjectAppended -= OnDatabaseObjectChanged;
            Documents.Remove(document.Database);
            Pending.Remove(document.Database);
        }

        private static void OnDatabaseObjectChanged(object sender, ObjectEventArgs args)
        {
            if (_internalUpdate || args == null || args.DBObject == null) return;
            Database database = args.DBObject.Database;
            if (database == null || !Documents.ContainsKey(database)) return;
            Pending.Add(database);
        }

        private static void OnIdle(object sender, EventArgs args)
        {
            if (_internalUpdate || Pending.Count == 0) return;

            foreach (Database database in Pending.ToList())
            {
                Document document;
                if (!Documents.TryGetValue(database, out document) || document == null)
                {
                    Pending.Remove(database);
                    continue;
                }

                if (document != AcApplication.DocumentManager.MdiActiveDocument ||
                    !document.Editor.IsQuiescent)
                    continue;

                Pending.Remove(database);
                List<ObjectId> linked =
                    DynamicCrossSectionCommands.FindLinkedSectionSources(database);
                if (linked.Count == 0) continue;

                try
                {
                    using (document.LockDocument())
                    {
                        foreach (ObjectId sourceId in linked)
                        {
                            DynamicCrossSectionCommands.RefreshLinkedSection(
                                document,
                                sourceId,
                                false,
                                true);
                        }
                    }
                }
                catch (System.Exception exception)
                {
                    document.Editor.WriteMessage(
                        "\nCE Tools dynamic-section monitor deferred an update. {0}",
                        exception.Message);
                    Pending.Add(database);
                }
            }
        }
    }
}

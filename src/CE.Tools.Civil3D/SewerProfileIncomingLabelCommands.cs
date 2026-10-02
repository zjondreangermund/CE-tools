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
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivilPipe = Autodesk.Civil.DatabaseServices.Pipe;
using CivilStructure = Autodesk.Civil.DatabaseServices.Structure;
using CivilProfileView = Autodesk.Civil.DatabaseServices.ProfileView;
using CivilNetwork = Autodesk.Civil.DatabaseServices.Network;

[assembly: CommandClass(typeof(CETools.Civil3D.SewerProfileIncomingLabelCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Profile-view annotations for incoming sewer connections and plan navigation.
    /// Labels are ordinary CE-owned MText so they can show the source pipe name and
    /// depth from the manhole rim to the pipe inside invert without changing native
    /// Civil 3D pipe/structure label styles.
    /// </summary>
    public sealed class SewerProfileIncomingLabelCommands
    {
        private const string RegAppName = "CE_SEW_INCOMING_PROFILE_LABEL";
        private const string LayerName = "CE-SEWER-INCOMING-LABELS";

        [CommandMethod("CE_TOOLS", "CE_SEWINCOMINGLABELSMULTI",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void ShowIncomingPipeLabelsMultipleViews()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civil = CivilApplication.ActiveDocument;
            if (document == null || civil == null) return;

            List<ObjectId> viewIds = SelectProfileViews(document);
            if (viewIds.Count == 0)
            {
                document.Editor.WriteMessage(
                    "\nCE_SEWINCOMINGLABELSMULTI: no editable profile views selected.");
                return;
            }

            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Incoming Pipe Labels",
                "Show incoming sewer pipe name and depth from manhole rim to inside pipe invert in every selected profile view.");
            settings.AddPositiveDouble(
                "TextHeight", "01 Labels", "Text height", 1.8,
                "Drawing-unit text height for the incoming pipe label.");
            settings.AddDouble(
                "HorizontalOffset", "01 Labels", "Horizontal offset", 1.0,
                "Horizontal drawing-unit offset from the incoming pipe connection.");
            settings.AddDouble(
                "VerticalOffset", "01 Labels", "Vertical offset", 0.5,
                "Vertical drawing-unit offset from the incoming pipe invert.");
            settings.AddChoice(
                "Refresh", "02 Existing", "Existing CE incoming labels", "Replace",
                "Replace removes prior CE incoming-pipe labels for the selected profile views before rebuilding them.",
                new[] { "Replace", "Keep and add" });
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;

            double textHeight = Math.Max(0.01, settings.Double("TextHeight", 1.8));
            double offsetX = settings.Double("HorizontalOffset", 1.0);
            double offsetY = settings.Double("VerticalOffset", 0.5);
            bool replace = string.Equals(
                settings.Text("Refresh"), "Replace", StringComparison.OrdinalIgnoreCase);

            int removed = 0;
            int created = 0;
            int skipped = 0;

            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction tr = document.Database.TransactionManager.StartTransaction())
            {
                ObjectId layerId = EnsureLayer(document.Database, tr);
                EnsureRegApp(document.Database, tr);

                if (replace)
                    removed = RemoveExistingLabels(document.Database, tr, viewIds);

                BlockTableRecord model = tr.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(document.Database),
                    OpenMode.ForWrite, false) as BlockTableRecord;
                if (model == null) return;

                foreach (ObjectId viewId in viewIds)
                {
                    CivilProfileView view = tr.GetObject(
                        viewId, OpenMode.ForRead, false) as CivilProfileView;
                    if (view == null || view.IsReferenceObject) { skipped++; continue; }

                    CivilAlignment alignment = tr.GetObject(
                        view.AlignmentId, OpenMode.ForRead, false) as CivilAlignment;
                    if (alignment == null) { skipped++; continue; }

                    List<ObjectId> structureIds = StructureIdsInView(
                        document.Database,
                        civil,
                        viewId,
                        tr);
                    foreach (ObjectId structureId in structureIds)
                    {
                        CivilStructure structure = tr.GetObject(
                            structureId, OpenMode.ForRead, false) as CivilStructure;
                        if (structure == null) continue;

                        double station = 0.0;
                        double planOffset = 0.0;
                        try
                        {
                            alignment.StationOffset(
                                structure.Position.X,
                                structure.Position.Y,
                                ref station,
                                ref planOffset);
                        }
                        catch { skipped++; continue; }

                        double rim = structure.RimElevation;
                        if (double.IsNaN(rim) || double.IsInfinity(rim))
                            rim = structure.Position.Z;

                        var incomingPipes = new List<IncomingPipeInfo>();
                        var outgoingInverts = new List<double>();
                        foreach (ObjectId pipeId in SewerPipeConnections.PipeIds(structure))
                        {
                            CivilPipe pipe = tr.GetObject(
                                pipeId, OpenMode.ForRead, false) as CivilPipe;
                            if (pipe == null) continue;

                            bool atStart = pipe.StartStructureId == structureId;
                            bool forward;
                            if (!SewerPipeConnections.TryForward(pipe, tr, out forward))
                                continue;

                            bool incoming = atStart != forward;
                            double invert = SewerPipeConnections.Invert(pipe, atStart);
                            if (double.IsNaN(invert) || double.IsInfinity(invert))
                                continue;

                            if (incoming)
                            {
                                bool sideEntry =
                                    !pipe.RefAlignmentId.IsNull &&
                                    !pipe.RefAlignmentId.IsErased &&
                                    pipe.RefAlignmentId != view.AlignmentId;
                                incomingPipes.Add(new IncomingPipeInfo
                                {
                                    Pipe = pipe,
                                    PipeId = pipeId,
                                    Invert = invert,
                                    SideEntry = sideEntry
                                });
                            }
                            else
                            {
                                outgoingInverts.Add(invert);
                            }
                        }

                        double lowestOutgoing = outgoingInverts.Count == 0
                            ? double.NaN
                            : outgoingInverts.Min();
                        int connectionIndex = 0;
                        foreach (IncomingPipeInfo incoming in incomingPipes
                            .OrderByDescending(item => item.Invert))
                        {
                            // Only annotate connections that matter in a long
                            // section: a side/lateral pipe entering this manhole,
                            // or the upper incoming pipe where the outgoing run
                            // drops to a lower invert. Ordinary through-pipes at a
                            // continuous manhole are intentionally not labelled.
                            bool dropsAtStructure =
                                !double.IsNaN(lowestOutgoing) &&
                                incoming.Invert >
                                    lowestOutgoing + 0.001;
                            if (!incoming.SideEntry &&
                                !dropsAtStructure)
                                continue;

                            Point3d graphPoint;
                            if (!TryProfileViewPoint(
                                    view,
                                    station,
                                    incoming.Invert,
                                    out graphPoint))
                            {
                                skipped++;
                                continue;
                            }

                            double depth = rim - incoming.Invert;
                            string pipeName =
                                string.IsNullOrWhiteSpace(
                                    incoming.Pipe.Name)
                                    ? "Pipe " +
                                        incoming.PipeId.Handle
                                    : incoming.Pipe.Name;

                            var label = new MText();
                            label.SetDatabaseDefaults(
                                document.Database);
                            label.LayerId = layerId;
                            label.TextHeight = textHeight;
                            label.Attachment =
                                AttachmentPoint.MiddleLeft;
                            TrySetAnnotative(label);
                            label.Location = new Point3d(
                                graphPoint.X + offsetX,
                                graphPoint.Y + offsetY +
                                    connectionIndex *
                                    textHeight * 1.35,
                                graphPoint.Z);
                            label.Contents =
                                pipeName + "\\P" +
                                "D=" +
                                depth.ToString(
                                    "0.000",
                                    CultureInfo.InvariantCulture) +
                                "m";
                            label.XData = BuildLinkData(
                                viewId,
                                incoming.PipeId,
                                structureId);

                            model.AppendEntity(label);
                            tr.AddNewlyCreatedDBObject(
                                label,
                                true);
                            created++;
                            connectionIndex++;
                        }
                    }
                }
                tr.Commit();
            }

            try
            {
                document.Database.TransactionManager.QueueForGraphicsFlush();
                document.Editor.Regen();
            }
            catch { }

            document.Editor.WriteMessage(
                "\nCE_SEWINCOMINGLABELSMULTI complete. Profile views={0}; labels created={1}; old labels removed={2}; skipped={3}. Annotative labels are limited to side-entry pipes and upper incoming pipes at real structure drops.",
                viewIds.Count, created, removed, skipped);
        }

        [CommandMethod("CE_TOOLS", "CE_SEWLOCATEPLAN",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void LocateSewerPartInPlan()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            ObjectId sourceId = ResolveSelectedSource(document);
            if (sourceId.IsNull)
            {
                document.Editor.WriteMessage(
                    "\nCE_SEWLOCATEPLAN: right-click or select a sewer pipe, structure, or CE incoming-pipe label first.");
                return;
            }

            if (!LocateBidirectional(document, sourceId))
            {
                document.Editor.WriteMessage(
                    "\nCE_SEWLOCATEPLAN: no matching plan/profile representation could be located.");
            }
        }

        private sealed class IncomingPipeInfo
        {
            internal CivilPipe Pipe;
            internal ObjectId PipeId;
            internal double Invert;
            internal bool SideEntry;
        }

        private static void TrySetAnnotative(Entity entity)
        {
            if (entity == null) return;
            try
            {
                PropertyInfo property =
                    entity.GetType().GetProperty(
                        "Annotative",
                        BindingFlags.Public |
                        BindingFlags.Instance);
                if (property == null ||
                    !property.CanWrite)
                    return;

                if (property.PropertyType == typeof(bool))
                {
                    property.SetValue(
                        entity,
                        true,
                        null);
                    return;
                }

                if (property.PropertyType.IsEnum)
                {
                    object value = Enum.Parse(
                        property.PropertyType,
                        "True",
                        true);
                    property.SetValue(
                        entity,
                        value,
                        null);
                }
            }
            catch { }
        }

        private static bool LocateBidirectional(
            Document document,
            ObjectId sourceId)
        {
            if (document == null ||
                sourceId.IsNull ||
                sourceId.IsErased)
                return false;

            Point3d planPoint;
            List<ProfileLocation> profileLocations;
            if (!ReadPlanAndProfileLocations(
                    document,
                    sourceId,
                    out planPoint,
                    out profileLocations))
                return LocateSourceInPlan(
                    document,
                    sourceId);

            if (profileLocations.Count == 0)
                return LocateSourceInPlan(
                    document,
                    sourceId);

            Point2d centre;
            double viewHeight = 100.0;
            try
            {
                using (ViewTableRecord current =
                    document.Editor.GetCurrentView())
                {
                    centre = current.CenterPoint;
                    viewHeight = Math.Max(
                        1.0,
                        current.Height);
                }
            }
            catch
            {
                centre = new Point2d(
                    planPoint.X,
                    planPoint.Y);
            }

            double planDistance =
                Distance2d(
                    centre,
                    new Point2d(
                        planPoint.X,
                        planPoint.Y));
            ProfileLocation nearest =
                profileLocations
                    .OrderBy(item =>
                        Distance2d(
                            centre,
                            new Point2d(
                                item.GraphPoint.X,
                                item.GraphPoint.Y)))
                    .First();
            double profileDistance =
                Distance2d(
                    centre,
                    new Point2d(
                        nearest.GraphPoint.X,
                        nearest.GraphPoint.Y));

            // When the current viewport is closer to the plan geometry, jump
            // to the profile representation. When it is already closer to a
            // profile graph, jump back to the plan representation.
            if (planDistance <= profileDistance)
                return LocateSourceInProfile(
                    document,
                    sourceId,
                    nearest,
                    viewHeight);

            return LocateSourceInPlan(
                document,
                sourceId);
        }

        private sealed class ProfileLocation
        {
            internal ObjectId ViewId;
            internal Point3d GraphPoint;
        }

        private static bool ReadPlanAndProfileLocations(
            Document document,
            ObjectId sourceId,
            out Point3d planPoint,
            out List<ProfileLocation> locations)
        {
            planPoint = Point3d.Origin;
            locations = new List<ProfileLocation>();
            if (document == null ||
                sourceId.IsNull)
                return false;

            using (Transaction tr =
                document.Database.TransactionManager.StartTransaction())
            {
                DBObject source = null;
                try
                {
                    source = tr.GetObject(
                        sourceId,
                        OpenMode.ForRead,
                        false);
                }
                catch { return false; }

                CivilPipe pipe = source as CivilPipe;
                CivilStructure structure =
                    source as CivilStructure;
                double elevation;

                if (pipe != null)
                {
                    Point3d start = pipe.StartPoint;
                    Point3d end = pipe.EndPoint;
                    planPoint = new Point3d(
                        (start.X + end.X) * 0.5,
                        (start.Y + end.Y) * 0.5,
                        0.0);
                    elevation =
                        (start.Z + end.Z) * 0.5;
                }
                else if (structure != null)
                {
                    planPoint = structure.Position;
                    elevation = structure.Position.Z;
                    try
                    {
                        List<double> inverts =
                            SewerPipeConnections.PipeIds(
                                structure)
                                .Select(id =>
                                {
                                    CivilPipe connected =
                                        tr.GetObject(
                                            id,
                                            OpenMode.ForRead,
                                            false) as CivilPipe;
                                    if (connected == null)
                                        return double.NaN;
                                    bool atStart =
                                        connected.StartStructureId ==
                                        sourceId;
                                    return SewerPipeConnections.Invert(
                                        connected,
                                        atStart);
                                })
                                .Where(value =>
                                    !double.IsNaN(value) &&
                                    !double.IsInfinity(value))
                                .ToList();
                        if (inverts.Count > 0)
                            elevation = inverts.Min();
                    }
                    catch { }
                }
                else
                {
                    return false;
                }

                foreach (ObjectId viewId in
                    GetDisplayedProfileViews(
                        source))
                {
                    CivilProfileView view = null;
                    CivilAlignment alignment = null;
                    try
                    {
                        view = tr.GetObject(
                            viewId,
                            OpenMode.ForRead,
                            false) as CivilProfileView;
                        if (view == null)
                            continue;
                        alignment = tr.GetObject(
                            view.AlignmentId,
                            OpenMode.ForRead,
                            false) as CivilAlignment;
                    }
                    catch { }
                    if (view == null ||
                        alignment == null)
                        continue;

                    double station = 0.0;
                    double offset = 0.0;
                    try
                    {
                        alignment.StationOffset(
                            planPoint.X,
                            planPoint.Y,
                            ref station,
                            ref offset);
                    }
                    catch { continue; }

                    Point3d graphPoint;
                    if (TryProfileViewPoint(
                            view,
                            station,
                            elevation,
                            out graphPoint))
                    {
                        locations.Add(
                            new ProfileLocation
                            {
                                ViewId = viewId,
                                GraphPoint = graphPoint
                            });
                    }
                }
            }
            return true;
        }

        private static IEnumerable<ObjectId>
            GetDisplayedProfileViews(DBObject part)
        {
            if (part == null)
                yield break;

            MethodInfo method = null;
            try
            {
                method = part.GetType().GetMethod(
                    "GetProfileViewsDisplayingMe",
                    BindingFlags.Public |
                    BindingFlags.Instance,
                    null,
                    Type.EmptyTypes,
                    null);
            }
            catch { }

            if (method == null)
                yield break;

            object value = null;
            try
            {
                value = method.Invoke(
                    part,
                    null);
            }
            catch { }

            System.Collections.IEnumerable items =
                value as System.Collections.IEnumerable;
            if (items == null)
                yield break;

            foreach (object item in items)
            {
                if (item is ObjectId)
                    yield return (ObjectId)item;
            }
        }

        private static bool LocateSourceInProfile(
            Document document,
            ObjectId sourceId,
            ProfileLocation location,
            double currentViewHeight)
        {
            if (document == null ||
                location == null)
                return false;

            try
            {
                double height = Math.Max(
                    20.0,
                    Math.Min(
                        currentViewHeight * 0.35,
                        250.0));
                document.Editor.Command(
                    "_.ZOOM",
                    "_C",
                    location.GraphPoint,
                    height);
                document.Editor.SetImpliedSelection(
                    new[] { sourceId });
                document.Editor.WriteMessage(
                    "\nCE_SEWLOCATEPLAN: source object {0} selected in its sewer profile view.",
                    sourceId.Handle);
                return true;
            }
            catch
            {
                try
                {
                    document.Editor.SetImpliedSelection(
                        new[] { sourceId });
                    return true;
                }
                catch { return false; }
            }
        }

        private static double Distance2d(
            Point2d first,
            Point2d second)
        {
            double dx = first.X - second.X;
            double dy = first.Y - second.Y;
            return Math.Sqrt(
                dx * dx + dy * dy);
        }

        private static List<ObjectId> SelectProfileViews(Document document)
        {
            PromptSelectionResult selection = document.Editor.SelectImplied();
            if (selection.Status != PromptStatus.OK ||
                selection.Value == null || selection.Value.Count == 0)
            {
                selection = document.Editor.GetSelection(
                    new PromptSelectionOptions
                    {
                        MessageForAdding =
                            "\nSelect multiple sewer profile views for incoming pipe labels: ",
                        AllowDuplicates = false,
                        RejectObjectsFromNonCurrentSpace = true
                    });
            }
            document.Editor.SetImpliedSelection(new ObjectId[0]);
            if (selection.Status != PromptStatus.OK || selection.Value == null)
                return new List<ObjectId>();

            var ids = new List<ObjectId>();
            using (Transaction tr = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in selection.Value.GetObjectIds().Distinct())
                {
                    try
                    {
                        CivilProfileView view = tr.GetObject(
                            id, OpenMode.ForRead, false) as CivilProfileView;
                        if (view != null && !view.IsReferenceObject) ids.Add(id);
                    }
                    catch { }
                }
            }
            return ids;
        }

        private static List<ObjectId> StructureIdsInView(
            Database database,
            CivilDocument civil,
            ObjectId viewId,
            Transaction tr)
        {
            var ids = new HashSet<ObjectId>();
            if (database == null || civil == null || viewId.IsNull || tr == null)
                return ids.ToList();

            // StructureOverrides only contains structures with view-specific
            // overrides in many Civil 3D drawings. It is therefore not a reliable
            // list of every structure actually drawn in the profile view.
            try
            {
                CivilProfileView view = tr.GetObject(
                    viewId, OpenMode.ForRead, false) as CivilProfileView;
                if (view != null)
                {
                    using (StructureOverrideCollection overrides = view.StructureOverrides)
                    {
                        foreach (StructureOverride entry in overrides)
                            if (entry.Draw && !entry.StructId.IsNull)
                                ids.Add(entry.StructId);
                    }
                }
            }
            catch { }

            // Authoritative fallback: ask every sewer structure which profile
            // views currently display it. This covers ordinary, non-overridden
            // structures and fixes the field case where 24 views were selected
            // but StructureOverrides yielded zero rows/labels.
            foreach (ObjectId networkId in civil.GetPipeNetworkIds())
            {
                CivilNetwork network = null;
                try
                {
                    network = tr.GetObject(
                        networkId, OpenMode.ForRead, false) as CivilNetwork;
                }
                catch { }
                if (network == null) continue;

                foreach (ObjectId structureId in network.GetStructureIds())
                {
                    CivilStructure structure = null;
                    try
                    {
                        structure = tr.GetObject(
                            structureId, OpenMode.ForRead, false) as CivilStructure;
                    }
                    catch { }
                    if (structure == null) continue;

                    try
                    {
                        foreach (ObjectId displayedViewId in
                            structure.GetProfileViewsDisplayingMe())
                        {
                            if (displayedViewId == viewId)
                            {
                                ids.Add(structureId);
                                break;
                            }
                        }
                    }
                    catch { }
                }
            }
            return ids.ToList();
        }

        private static bool TryProfileViewPoint(
            CivilProfileView view,
            double station,
            double elevation,
            out Point3d point)
        {
            point = Point3d.Origin;
            if (view == null) return false;

            foreach (string name in new[]
            {
                "FindXYAtStationAndElevation",
                "GetXYAtStationAndElevation"
            })
            {
                try
                {
                    MethodInfo method = view.GetType().GetMethods(
                        BindingFlags.Public | BindingFlags.Instance)
                        .FirstOrDefault(item =>
                            string.Equals(item.Name, name, StringComparison.Ordinal) &&
                            item.GetParameters().Length == 4);
                    if (method == null) continue;

                    object[] args = { station, elevation, 0.0, 0.0 };
                    object result = method.Invoke(view, args);
                    if (result is bool && !(bool)result) continue;

                    double x = Convert.ToDouble(args[2], CultureInfo.InvariantCulture);
                    double y = Convert.ToDouble(args[3], CultureInfo.InvariantCulture);
                    if (double.IsNaN(x) || double.IsInfinity(x) ||
                        double.IsNaN(y) || double.IsInfinity(y))
                        continue;
                    point = new Point3d(x, y, 0.0);
                    return true;
                }
                catch { }
            }
            return false;
        }

        private static ObjectId EnsureLayer(Database database, Transaction tr)
        {
            LayerTable layers = tr.GetObject(
                database.LayerTableId, OpenMode.ForRead, false) as LayerTable;
            if (layers == null) return database.Clayer;
            if (layers.Has(LayerName)) return layers[LayerName];

            layers.UpgradeOpen();
            var layer = new LayerTableRecord { Name = LayerName };
            ObjectId id = layers.Add(layer);
            tr.AddNewlyCreatedDBObject(layer, true);
            return id;
        }

        private static void EnsureRegApp(Database database, Transaction tr)
        {
            RegAppTable apps = tr.GetObject(
                database.RegAppTableId, OpenMode.ForRead, false) as RegAppTable;
            if (apps == null || apps.Has(RegAppName)) return;
            apps.UpgradeOpen();
            var record = new RegAppTableRecord { Name = RegAppName };
            apps.Add(record);
            tr.AddNewlyCreatedDBObject(record, true);
        }

        private static ResultBuffer BuildLinkData(
            ObjectId viewId,
            ObjectId pipeId,
            ObjectId structureId)
        {
            return new ResultBuffer(
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, RegAppName),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString,
                    viewId.Handle.ToString()),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString,
                    pipeId.Handle.ToString()),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString,
                    structureId.Handle.ToString()));
        }

        private static int RemoveExistingLabels(
            Database database,
            Transaction tr,
            IEnumerable<ObjectId> viewIds)
        {
            var handles = new HashSet<string>(
                viewIds.Select(id => id.Handle.ToString()),
                StringComparer.OrdinalIgnoreCase);
            int removed = 0;
            BlockTableRecord model = tr.GetObject(
                SymbolUtilityServices.GetBlockModelSpaceId(database),
                OpenMode.ForRead, false) as BlockTableRecord;
            if (model == null) return 0;

            foreach (ObjectId id in model)
            {
                MText text = null;
                try { text = tr.GetObject(id, OpenMode.ForRead, false) as MText; }
                catch { }
                if (text == null) continue;

                string viewHandle;
                string sourceHandle;
                if (!TryReadLink(text, out viewHandle, out sourceHandle) ||
                    !handles.Contains(viewHandle))
                    continue;

                text.UpgradeOpen();
                text.Erase();
                removed++;
            }
            return removed;
        }

        private static bool TryReadLink(
            Entity entity,
            out string viewHandle,
            out string sourceHandle)
        {
            viewHandle = string.Empty;
            sourceHandle = string.Empty;
            if (entity == null) return false;
            try
            {
                using (ResultBuffer data =
                    entity.GetXDataForApplication(RegAppName))
                {
                    if (data == null) return false;
                    string[] values = data.AsArray()
                        .Where(item => item.TypeCode ==
                            (int)DxfCode.ExtendedDataAsciiString)
                        .Select(item => Convert.ToString(
                            item.Value, CultureInfo.InvariantCulture))
                        .Where(item => !string.IsNullOrWhiteSpace(item))
                        .ToArray();
                    if (values.Length < 2) return false;
                    viewHandle = values[0];
                    sourceHandle = values[1];
                    return true;
                }
            }
            catch { return false; }
        }

        private static ObjectId ResolveSelectedSource(Document document)
        {
            PromptSelectionResult selection = document.Editor.SelectImplied();
            ObjectId selectedId = ObjectId.Null;
            if (selection.Status == PromptStatus.OK &&
                selection.Value != null && selection.Value.Count > 0)
            {
                selectedId = selection.Value.GetObjectIds().Last();
            }
            else
            {
                PromptEntityResult picked = document.Editor.GetEntity(
                    "\nSelect sewer pipe, structure or CE incoming-pipe label: ");
                if (picked.Status != PromptStatus.OK) return ObjectId.Null;
                selectedId = picked.ObjectId;
            }

            using (Transaction tr = document.Database.TransactionManager.StartTransaction())
            {
                DBObject value = null;
                try { value = tr.GetObject(selectedId, OpenMode.ForRead, false); }
                catch { return ObjectId.Null; }

                if (value is CivilPipe || value is CivilStructure)
                    return selectedId;

                Entity entity = value as Entity;
                string viewHandle;
                string sourceHandle;
                if (entity != null &&
                    TryReadLink(entity, out viewHandle, out sourceHandle))
                    return ResolveHandle(document.Database, sourceHandle);

                // Native profile-view pipe/structure graphics and native profile
                // labels expose their source/model part through different ObjectId
                // properties across Civil 3D 2023 object types. Resolve those
                // reflectively and verify the resolved object is a sewer part.
                ObjectId profileSource = ResolveProfileSourcePart(
                    value,
                    document.Database,
                    tr);
                if (!profileSource.IsNull)
                    return profileSource;
            }
            return ObjectId.Null;
        }

        internal static bool LocateSourceInPlan(
            Document document,
            ObjectId sourceId)
        {
            if (document == null || sourceId.IsNull || sourceId.IsErased)
                return false;

            try { AcApplication.SetSystemVariable("CTAB", "Model"); }
            catch { }

            try
            {
                document.Editor.SetImpliedSelection(new[] { sourceId });
                document.Editor.Command("_.ZOOM", "_Object", sourceId, "");
                document.Editor.SetImpliedSelection(new[] { sourceId });
                document.Editor.WriteMessage(
                    "\nCE_SEWLOCATEPLAN: source object {0} selected in plan/model space.",
                    sourceId.Handle);
                return true;
            }
            catch
            {
                try
                {
                    document.Editor.SetImpliedSelection(new[] { sourceId });
                    return true;
                }
                catch { return false; }
            }
        }

        private static ObjectId ResolveProfileSourcePart(
            DBObject selected,
            Database database,
            Transaction tr)
        {
            if (selected == null || database == null || tr == null)
                return ObjectId.Null;

            foreach (string propertyName in new[]
            {
                "PartId", "PipeId", "StructureId", "SourcePartId",
                "SourceId", "ModelPartId", "NetworkPartId", "FeatureId"
            })
            {
                try
                {
                    PropertyInfo property = selected.GetType().GetProperty(
                        propertyName,
                        BindingFlags.Public | BindingFlags.Instance);
                    if (property == null || !property.CanRead) continue;
                    object raw = property.GetValue(selected, null);
                    if (!(raw is ObjectId)) continue;
                    ObjectId candidate = (ObjectId)raw;
                    if (IsSewerPart(candidate, tr))
                        return candidate;
                }
                catch { }
            }

            // Some Civil labels/profile graphics wrap a source object that then
            // exposes the actual PartId. Follow one level of ObjectId indirection.
            foreach (PropertyInfo property in selected.GetType().GetProperties(
                BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.PropertyType != typeof(ObjectId) || !property.CanRead)
                    continue;
                try
                {
                    ObjectId intermediateId = (ObjectId)property.GetValue(selected, null);
                    if (intermediateId.IsNull || intermediateId.IsErased) continue;
                    DBObject intermediate = tr.GetObject(
                        intermediateId, OpenMode.ForRead, false);
                    if (intermediate is CivilPipe || intermediate is CivilStructure)
                        return intermediateId;

                    foreach (string nestedName in new[]
                    {
                        "PartId", "PipeId", "StructureId",
                        "SourcePartId", "ModelPartId"
                    })
                    {
                        PropertyInfo nested = intermediate.GetType().GetProperty(
                            nestedName,
                            BindingFlags.Public | BindingFlags.Instance);
                        if (nested == null || !nested.CanRead ||
                            nested.PropertyType != typeof(ObjectId))
                            continue;
                        ObjectId candidate = (ObjectId)nested.GetValue(
                            intermediate, null);
                        if (IsSewerPart(candidate, tr))
                            return candidate;
                    }
                }
                catch { }
            }

            return ObjectId.Null;
        }

        private static bool IsSewerPart(
            ObjectId id,
            Transaction tr)
        {
            if (id.IsNull || id.IsErased || tr == null) return false;
            try
            {
                DBObject value = tr.GetObject(id, OpenMode.ForRead, false);
                return value is CivilPipe || value is CivilStructure;
            }
            catch { return false; }
        }

        private static ObjectId ResolveHandle(Database database, string handleText)
        {
            try
            {
                long value;
                if (!long.TryParse(
                        handleText,
                        NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture,
                        out value))
                    return ObjectId.Null;
                return database.GetObjectId(false, new Handle(value), 0);
            }
            catch { return ObjectId.Null; }
        }
    }
}

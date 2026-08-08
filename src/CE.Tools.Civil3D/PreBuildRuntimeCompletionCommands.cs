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
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilCogoPoint = Autodesk.Civil.DatabaseServices.CogoPoint;
using CivilNetwork = Autodesk.Civil.DatabaseServices.Network;
using CivilPipe = Autodesk.Civil.DatabaseServices.Pipe;
using CivilStructure = Autodesk.Civil.DatabaseServices.Structure;

[assembly: CommandClass(typeof(CETools.Civil3D.PreBuildRuntimeCompletionCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Final installed-host repair commands for the comment batch that must be
    /// completed before the next Civil 3D 2023 build. The implementation uses
    /// guarded reflection around Civil 3D style/band component APIs so a missing
    /// optional property cannot abort the complete workflow.
    /// </summary>
    public sealed class PreBuildRuntimeCompletionCommands
    {
        [CommandMethod("CE_TOOLS", "CE_RUNTIMEFINISH", CommandFlags.Modal | CommandFlags.Redraw)]
        public void RuntimeFinishCentre()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            DisciplineWorkflowDialogs.SelectAndRun(
                document,
                "CE Tools - Runtime Completion Centre",
                "Repair linked annotations, apply the selected pipe/structure label presentation, and apply profile-view band sets with live Civil data sources.",
                new List<DisciplineWorkflowAction>
                {
                    new DisciplineWorkflowAction(
                        "Repair linked annotations",
                        "CE_ANNOTATIONLINKREPAIR",
                        "Refresh COGO/MText/MLeader/table links, restore visible point descriptions and clamp overlap offsets close to their true source anchors.",
                        "01 Annotation"),
                    new DisciplineWorkflowAction(
                        "Apply sewer plan label presentation",
                        "CE_PIPELABELPRESENTATION",
                        "Apply selected Civil label styles, remove dragged-state markers, show the flow arrow and keep pipe/structure labels close and plan-readable.",
                        "02 Sewer labels"),
                    new DisciplineWorkflowAction(
                        "Apply profile band sets in batch",
                        "CE_PROFILEBANDSBATCH",
                        "Apply one or two selected band-set styles to selected or multiple profile views and link their profiles/network data.",
                        "03 Profile bands"),
                    new DisciplineWorkflowAction(
                        "Refresh all linked profile bands",
                        "CE_PROFILEBANDREFRESH",
                        "Re-link saved sewer band sets, profiles and network parts on every CE profile view.",
                        "03 Profile bands")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_ANNOTATIONLINKREPAIR", CommandFlags.Modal | CommandFlags.Redraw)]
        public void RepairAnnotationLinks()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            RuntimeAnnotationRepairResult result = RuntimeAnnotationLinkManager.Repair(document, true);
            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_ANNOTATIONLINKREPAIR complete. Vertex groups={0}; coordinate groups={1}; COGO labels={2}; bounded annotations={3}; overlaps moved={4}; warnings={5}.",
                result.VertexGroups,
                result.CoordinateGroups,
                result.CogoLabels,
                result.BoundedAnnotations,
                result.OverlapsMoved,
                result.Warnings);
        }

        [CommandMethod("CE_TOOLS", "CE_PIPELABELPRESENTATION", CommandFlags.Modal | CommandFlags.Redraw)]
        public void ApplyPipeLabelPresentation()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            SewerPlanLabelRuntimeResult result = SewerPlanLabelRuntimeManager.Apply(document);
            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_PIPELABELPRESENTATION complete. Styles configured={0}; pipe labels={1}; structure labels={2}; labels returned near source={3}; warnings={4}.",
                result.StylesConfigured,
                result.PipeLabels,
                result.StructureLabels,
                result.LabelsRepositioned,
                result.Warnings);
        }

        [CommandMethod("CE_TOOLS", "CE_PROFILEBANDSBATCH", CommandFlags.Modal | CommandFlags.Redraw)]
        public void ApplyProfileBandsBatch()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            ProfileBandRuntimeResult result = ProfileViewBandRuntimeManager.RunBatch(document);
            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_PROFILEBANDSBATCH complete. Profile views={0}; band sets applied={1}; band items linked={2}; network parts added={3}; styles imported={4}; skipped={5}.{6}",
                result.ProfileViews,
                result.BandSetsApplied,
                result.BandItemsLinked,
                result.NetworkPartsAdded,
                result.StylesImported,
                result.Skipped,
                string.IsNullOrWhiteSpace(result.Warning) ? string.Empty : " " + result.Warning);
        }

        [CommandMethod("CE_TOOLS", "CE_PROFILEBANDREFRESH", CommandFlags.Modal | CommandFlags.Redraw)]
        public void RefreshProfileBands()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            ProfileBandRuntimeResult result = ProfileViewBandRuntimeManager.RefreshAll(document);
            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_PROFILEBANDREFRESH complete. Profile views={0}; band sets applied={1}; band items linked={2}; network parts added={3}; skipped={4}.{5}",
                result.ProfileViews,
                result.BandSetsApplied,
                result.BandItemsLinked,
                result.NetworkPartsAdded,
                result.Skipped,
                string.IsNullOrWhiteSpace(result.Warning) ? string.Empty : " " + result.Warning);
        }

        private static Document ActiveDocument()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
        }
    }

    internal sealed class RuntimeAnnotationRepairResult
    {
        internal int VertexGroups { get; set; }
        internal int CoordinateGroups { get; set; }
        internal int CogoLabels { get; set; }
        internal int BoundedAnnotations { get; set; }
        internal int OverlapsMoved { get; set; }
        internal int Warnings { get; set; }
    }

    internal static class RuntimeAnnotationLinkManager
    {
        private const string VertexApp = "CE_VERTEX_SETTINGOUT";

        internal static RuntimeAnnotationRepairResult Repair(
            Document document,
            bool solveOverlaps)
        {
            var result = new RuntimeAnnotationRepairResult();
            if (document == null) return result;
            try { result.VertexGroups = VertexSettingOutCommands.RefreshAll(document); }
            catch { result.Warnings++; }
            try
            {
                SurveyCoordinateWorkflowCommands.RefreshAll(document);
                result.CoordinateGroups++;
            }
            catch { result.Warnings++; }
            try
            {
                CogoPointStyleResult cogo = CogoPointProjectStyleCommands.ApplySelectedStyles(
                    document,
                    solveOverlaps);
                result.CogoLabels = cogo.LabelStylesApplied + cogo.StoredOffsetsRestored;
                result.OverlapsMoved += cogo.OverlapsMoved;
            }
            catch { result.Warnings++; }
            try
            {
                result.BoundedAnnotations = ClampLinkedAnnotations(document, solveOverlaps);
            }
            catch { result.Warnings++; }
            return result;
        }

        internal static int ClampLinkedAnnotations(
            Document document,
            bool solveOverlaps)
        {
            if (document == null) return 0;
            Database database = document.Database;
            double minimum = Math.Max(
                PaperAnnotationScale.ModelDistance(database, 2.5),
                0.001);
            double maximum = Math.Max(
                PaperAnnotationScale.ModelDistance(database, 8.0),
                minimum * 2.0);
            double overlapGap = Math.Max(
                PaperAnnotationScale.ModelDistance(database, 1.5),
                minimum * 0.25);
            var linked = new List<RuntimeLinkedAnnotation>();

            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                BlockTableRecord modelSpace = transaction.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(database),
                    OpenMode.ForRead,
                    false) as BlockTableRecord;
                if (modelSpace == null) return 0;

                foreach (ObjectId id in modelSpace)
                {
                    Entity entity;
                    try
                    {
                        entity = transaction.GetObject(id, OpenMode.ForWrite, false) as Entity;
                    }
                    catch
                    {
                        continue;
                    }
                    if (entity == null) continue;
                    Point3d anchor;
                    if (!TryReadVertexAnchor(entity, out anchor)) continue;

                    Point3d location;
                    if (!TryReadAnnotationLocation(entity, out location)) continue;
                    MText anchoredText = entity as MText;
                    if (anchoredText != null)
                    {
                        if (anchoredText.Location.DistanceTo(anchor) > 1e-8)
                            anchoredText.Location = anchor;
                        EnsureVisible(anchoredText);
                        continue;
                    }
                    Vector3d offset = location - anchor;
                    if (!IsFinite(offset) || offset.Length < minimum * 0.2)
                        offset = new Vector3d(minimum, minimum, 0.0);
                    if (offset.Length > maximum)
                        offset = offset.GetNormal() * maximum;
                    Point3d bounded = anchor + offset;
                    bool changed = bounded.DistanceTo(location) > 1e-8;
                    if (changed) SetAnnotationLocation(entity, bounded);
                    ApplyClosedFilledLeader(database, entity);
                    EnsureVisible(entity);

                    Extents3d extents;
                    try { extents = entity.GeometricExtents; }
                    catch
                    {
                        extents = new Extents3d(
                            bounded - new Vector3d(minimum, minimum * 0.5, 0.0),
                            bounded + new Vector3d(minimum * 2.0, minimum * 0.5, 0.0));
                    }
                    linked.Add(new RuntimeLinkedAnnotation(
                        entity.ObjectId,
                        anchor,
                        bounded,
                        extents));
                }

                int adjusted = linked.Count(item =>
                    item.Location.DistanceTo(item.OriginalLocation) > 1e-8);
                if (solveOverlaps)
                    adjusted += ResolveBoundedOverlaps(
                        linked,
                        transaction,
                        minimum,
                        maximum,
                        overlapGap);
                transaction.Commit();
                return adjusted;
            }
        }

        private static int ResolveBoundedOverlaps(
            IList<RuntimeLinkedAnnotation> items,
            Transaction transaction,
            double minimum,
            double maximum,
            double gap)
        {
            var accepted = new List<Extents3d>();
            int moved = 0;
            foreach (RuntimeLinkedAnnotation item in items
                .OrderBy(value => value.Anchor.X)
                .ThenBy(value => value.Anchor.Y)
                .ThenBy(value => value.Id.Handle.Value))
            {
                RuntimePlacement best = FindBoundedPlacement(
                    item,
                    accepted,
                    minimum,
                    maximum,
                    gap);
                accepted.Add(best.Extents);
                if (best.Location.DistanceTo(item.Location) <= 1e-8) continue;
                Entity entity;
                try
                {
                    entity = transaction.GetObject(
                        item.Id,
                        OpenMode.ForWrite,
                        false) as Entity;
                }
                catch
                {
                    continue;
                }
                if (entity == null) continue;
                SetAnnotationLocation(entity, best.Location);
                item.Location = best.Location;
                moved++;
            }
            return moved;
        }

        private static RuntimePlacement FindBoundedPlacement(
            RuntimeLinkedAnnotation item,
            IList<Extents3d> accepted,
            double minimum,
            double maximum,
            double gap)
        {
            Point3d originalCentre = Centre(item.Extents);
            var candidates = new List<Point3d> { item.Location };
            Vector2d[] directions =
            {
                new Vector2d(1.0, 1.0),
                new Vector2d(-1.0, 1.0),
                new Vector2d(1.0, -1.0),
                new Vector2d(-1.0, -1.0),
                new Vector2d(1.0, 0.0),
                new Vector2d(-1.0, 0.0),
                new Vector2d(0.0, 1.0),
                new Vector2d(0.0, -1.0)
            };
            for (int ring = 1; ring <= 4; ring++)
            {
                double radius = Math.Min(
                    maximum,
                    minimum * (1.0 + ring * 1.75));
                foreach (Vector2d direction in directions)
                {
                    Vector2d unit = direction.GetNormal();
                    candidates.Add(new Point3d(
                        item.Anchor.X + unit.X * radius,
                        item.Anchor.Y + unit.Y * radius,
                        item.Anchor.Z));
                }
            }

            RuntimePlacement best = null;
            double bestScore = double.MaxValue;
            foreach (Point3d candidate in candidates)
            {
                Vector3d movement = candidate - item.Location;
                Extents3d translated = Translate(item.Extents, movement);
                int collisions = accepted.Count(existing => Intersects(
                    translated,
                    existing,
                    gap));
                double sourceDistance = candidate.DistanceTo(item.Anchor);
                double movementDistance = movement.Length;
                double score = collisions * 1000000.0 +
                    sourceDistance * 10.0 + movementDistance;
                if (score >= bestScore) continue;
                best = new RuntimePlacement(candidate, translated);
                bestScore = score;
            }
            return best ?? new RuntimePlacement(item.Location, item.Extents);
        }

        private static bool TryReadVertexAnchor(
            Entity entity,
            out Point3d anchor)
        {
            anchor = Point3d.Origin;
            if (entity == null) return false;
            ResultBuffer buffer = entity.GetXDataForApplication(VertexApp);
            if (buffer == null) return false;
            TypedValue[] values = buffer.AsArray();
            if (values.Length < 8 ||
                !string.Equals(
                    Convert.ToString(values[1].Value, CultureInfo.InvariantCulture),
                    "OUTPUT",
                    StringComparison.OrdinalIgnoreCase))
                return false;
            try
            {
                anchor = new Point3d(
                    Convert.ToDouble(values[5].Value, CultureInfo.InvariantCulture),
                    Convert.ToDouble(values[6].Value, CultureInfo.InvariantCulture),
                    Convert.ToDouble(values[7].Value, CultureInfo.InvariantCulture));
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryReadAnnotationLocation(
            Entity entity,
            out Point3d location)
        {
            location = Point3d.Origin;
            MText text = entity as MText;
            if (text != null)
            {
                location = text.Location;
                return true;
            }
            MLeader leader = entity as MLeader;
            if (leader != null)
            {
                try
                {
                    location = leader.TextLocation;
                    return true;
                }
                catch
                {
                    return false;
                }
            }
            return false;
        }

        private static void SetAnnotationLocation(
            Entity entity,
            Point3d location)
        {
            MText text = entity as MText;
            if (text != null)
            {
                text.Location = location;
                return;
            }
            MLeader leader = entity as MLeader;
            if (leader != null)
            {
                try { leader.TextLocation = location; }
                catch { }
            }
        }

        private static void ApplyClosedFilledLeader(
            Database database,
            Entity entity)
        {
            MLeader leader = entity as MLeader;
            if (leader == null) return;
            try
            {
                // Force a closed-filled arrow even when the drawing DIMSTYLE uses ticks.
                leader.ArrowSymbolId = ObjectId.Null;
            }
            catch { }
            TrySetDouble(leader, Math.Max(
                PaperAnnotationScale.ModelDistance(database, 2.5),
                0.001), "ArrowSize", "ArrowHeadSize");
        }

        private static void EnsureVisible(object value)
        {
            TrySetBoolean(value, true,
                "Visible", "Visibility", "LabelVisibility", "ShowLabel");
            TrySetBoolean(value, false,
                "Pinned", "IsLabelPinned", "LabelIsPinned");
        }

        private static bool IsFinite(Vector3d value)
        {
            return !(double.IsNaN(value.X) || double.IsInfinity(value.X) ||
                     double.IsNaN(value.Y) || double.IsInfinity(value.Y) ||
                     double.IsNaN(value.Z) || double.IsInfinity(value.Z));
        }

        private static Point3d Centre(Extents3d value)
        {
            return new Point3d(
                (value.MinPoint.X + value.MaxPoint.X) * 0.5,
                (value.MinPoint.Y + value.MaxPoint.Y) * 0.5,
                (value.MinPoint.Z + value.MaxPoint.Z) * 0.5);
        }

        private static Extents3d Translate(
            Extents3d value,
            Vector3d movement)
        {
            return new Extents3d(
                value.MinPoint + movement,
                value.MaxPoint + movement);
        }

        private static bool Intersects(
            Extents3d first,
            Extents3d second,
            double gap)
        {
            return !(first.MaxPoint.X + gap < second.MinPoint.X ||
                     first.MinPoint.X - gap > second.MaxPoint.X ||
                     first.MaxPoint.Y + gap < second.MinPoint.Y ||
                     first.MinPoint.Y - gap > second.MaxPoint.Y);
        }

        private static void TrySetBoolean(
            object value,
            bool requested,
            params string[] names)
        {
            if (value == null) return;
            foreach (string name in names)
            {
                try
                {
                    PropertyInfo property = value.GetType().GetProperty(
                        name,
                        BindingFlags.Public | BindingFlags.Instance);
                    if (property == null || !property.CanWrite) continue;
                    if (property.PropertyType == typeof(bool))
                    {
                        property.SetValue(value, requested, null);
                        return;
                    }
                    if (!property.PropertyType.IsEnum) continue;
                    string enumName = Enum.GetNames(property.PropertyType)
                        .FirstOrDefault(candidate => requested
                            ? candidate.IndexOf("True", StringComparison.OrdinalIgnoreCase) >= 0 ||
                              candidate.IndexOf("Visible", StringComparison.OrdinalIgnoreCase) >= 0 ||
                              candidate.IndexOf("On", StringComparison.OrdinalIgnoreCase) >= 0 ||
                              candidate.IndexOf("Yes", StringComparison.OrdinalIgnoreCase) >= 0
                            : candidate.IndexOf("False", StringComparison.OrdinalIgnoreCase) >= 0 ||
                              candidate.IndexOf("Off", StringComparison.OrdinalIgnoreCase) >= 0 ||
                              candidate.IndexOf("No", StringComparison.OrdinalIgnoreCase) >= 0 ||
                              candidate.IndexOf("Unpin", StringComparison.OrdinalIgnoreCase) >= 0);
                    if (string.IsNullOrWhiteSpace(enumName)) continue;
                    property.SetValue(
                        value,
                        Enum.Parse(property.PropertyType, enumName),
                        null);
                    return;
                }
                catch
                {
                    // Try another property name.
                }
            }
        }

        private static void TrySetDouble(
            object value,
            double requested,
            params string[] names)
        {
            if (value == null) return;
            foreach (string name in names)
            {
                try
                {
                    PropertyInfo property = value.GetType().GetProperty(
                        name,
                        BindingFlags.Public | BindingFlags.Instance);
                    if (property == null || !property.CanWrite ||
                        property.PropertyType != typeof(double)) continue;
                    property.SetValue(value, requested, null);
                    return;
                }
                catch
                {
                    // Try another property name.
                }
            }
        }

        private sealed class RuntimeLinkedAnnotation
        {
            internal RuntimeLinkedAnnotation(
                ObjectId id,
                Point3d anchor,
                Point3d location,
                Extents3d extents)
            {
                Id = id;
                Anchor = anchor;
                Location = location;
                OriginalLocation = location;
                Extents = extents;
            }
            internal ObjectId Id { get; private set; }
            internal Point3d Anchor { get; private set; }
            internal Point3d Location { get; set; }
            internal Point3d OriginalLocation { get; private set; }
            internal Extents3d Extents { get; private set; }
        }

        private sealed class RuntimePlacement
        {
            internal RuntimePlacement(Point3d location, Extents3d extents)
            {
                Location = location;
                Extents = extents;
            }
            internal Point3d Location { get; private set; }
            internal Extents3d Extents { get; private set; }
        }
    }

    internal sealed class SewerPlanLabelRuntimeResult
    {
        internal int StylesConfigured { get; set; }
        internal int PipeLabels { get; set; }
        internal int StructureLabels { get; set; }
        internal int LabelsRepositioned { get; set; }
        internal int Warnings { get; set; }
    }

    internal static class SewerPlanLabelRuntimeManager
    {
        internal static SewerPlanLabelRuntimeResult Apply(Document document)
        {
            var result = new SewerPlanLabelRuntimeResult();
            if (document == null) return result;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (civilDocument == null)
            {
                result.Warnings++;
                return result;
            }
            Database database = document.Database;
            SewerProductionSettings settings = SewerProductionSettings.Read(database);
            ProjectStyleSelection project = ProjectStyleCenterCommands.ReadSelection(database);
            string pipeRequested = Useful(settings.PipePlanLabelStyle)
                ? settings.PipePlanLabelStyle
                : Read(project, "Pipe Label Style");
            string structureRequested = Useful(settings.StructurePlanLabelStyle)
                ? settings.StructurePlanLabelStyle
                : Read(project, "Structure Label Style");

            try
            {
                using (DocumentLock documentLock = document.LockDocument())
                using (Transaction transaction =
                    database.TransactionManager.StartTransaction())
                {
                    string resolved;
                    ObjectId pipeStyleId = CivilStyleCatalogV2.ResolveStyleId(
                        database,
                        civilDocument,
                        "Pipe Label Style",
                        pipeRequested,
                        transaction,
                        out resolved);
                    ObjectId structureStyleId = CivilStyleCatalogV2.ResolveStyleId(
                        database,
                        civilDocument,
                        "Structure Label Style",
                        structureRequested,
                        transaction,
                        out resolved);
                    if (ConfigureLabelStyle(
                            transaction,
                            pipeStyleId,
                            true,
                            database)) result.StylesConfigured++;
                    if (ConfigureLabelStyle(
                            transaction,
                            structureStyleId,
                            false,
                            database)) result.StylesConfigured++;

                    BlockTableRecord modelSpace = transaction.GetObject(
                        SymbolUtilityServices.GetBlockModelSpaceId(database),
                        OpenMode.ForRead,
                        false) as BlockTableRecord;
                    if (modelSpace != null)
                    {
                        foreach (ObjectId id in modelSpace)
                        {
                            DBObject label;
                            try
                            {
                                label = transaction.GetObject(
                                    id,
                                    OpenMode.ForWrite,
                                    false);
                            }
                            catch
                            {
                                continue;
                            }
                            if (label == null) continue;
                            string typeName = label.GetType().Name;
                            bool pipeLabel = typeName.IndexOf(
                                "PipeLabel",
                                StringComparison.OrdinalIgnoreCase) >= 0;
                            bool structureLabel = typeName.IndexOf(
                                "StructureLabel",
                                StringComparison.OrdinalIgnoreCase) >= 0;
                            if (!pipeLabel && !structureLabel) continue;
                            ObjectId styleId = pipeLabel ? pipeStyleId : structureStyleId;
                            SetObjectId(label, styleId, "StyleId", "LabelStyleId");
                            ClearTextOverrides(label);
                            ResetDraggedState(label);
                            ObjectId featureId = ReadObjectId(
                                label,
                                "FeatureId", "PipeId", "StructureId", "ParentEntityId");
                            if (KeepLabelNearFeature(
                                    database,
                                    transaction,
                                    label,
                                    featureId,
                                    pipeLabel)) result.LabelsRepositioned++;
                            if (pipeLabel) result.PipeLabels++;
                            else result.StructureLabels++;
                        }
                    }
                    transaction.Commit();
                }
            }
            catch
            {
                result.Warnings++;
            }
            return result;
        }

        internal static void ConfigureLabel(
            object label,
            Transaction transaction)
        {
            if (label == null || transaction == null) return;
            ClearTextOverrides(label);
            ResetDraggedState(label);
            ObjectId featureId = ReadObjectId(
                label,
                "FeatureId", "PipeId", "StructureId", "ParentEntityId");
            bool pipe = label.GetType().Name.IndexOf(
                "PipeLabel",
                StringComparison.OrdinalIgnoreCase) >= 0;
            DBObject databaseObject = label as DBObject;
            if (databaseObject == null) return;
            KeepLabelNearFeature(
                databaseObject.Database,
                transaction,
                label,
                featureId,
                pipe);
        }

        private static bool ConfigureLabelStyle(
            Transaction transaction,
            ObjectId styleId,
            bool pipe,
            Database database)
        {
            if (styleId.IsNull || styleId.IsErased) return false;
            DBObject style;
            try
            {
                style = transaction.GetObject(
                    styleId,
                    OpenMode.ForWrite,
                    false);
            }
            catch
            {
                return false;
            }
            if (style == null) return false;
            ConfigurePlanReadable(style);
            foreach (object component in ReadComponents(style, transaction))
            {
                string name = ReadName(component);
                bool lengthSlope = name.IndexOf(
                    "Length and Slope",
                    StringComparison.OrdinalIgnoreCase) >= 0;
                bool flowArrow = name.IndexOf(
                    "Flow Direction",
                    StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf(
                        "Direction Arrow",
                        StringComparison.OrdinalIgnoreCase) >= 0;
                if (pipe && lengthSlope)
                    SetVisibility(component, false);
                else if (pipe && flowArrow)
                {
                    SetVisibility(component, true);
                    SetDouble(component, 10.0,
                        "Length", "ArrowLength", "ArrowHeadSize", "ArrowSize");
                }
                else
                {
                    ConfigurePlanReadable(component);
                }
            }
            return true;
        }

        private static IList<object> ReadComponents(
            object style,
            Transaction transaction)
        {
            var result = new List<object>();
            if (style == null) return result;
            foreach (MethodInfo method in style.GetType().GetMethods(
                BindingFlags.Public | BindingFlags.Instance)
                .Where(value => value.GetParameters().Length == 0 &&
                    value.Name.IndexOf("Component", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                object returned;
                try { returned = method.Invoke(style, null); }
                catch { continue; }
                AppendComponents(returned, transaction, result);
            }
            foreach (PropertyInfo property in style.GetType().GetProperties(
                BindingFlags.Public | BindingFlags.Instance)
                .Where(value => value.CanRead &&
                    value.GetIndexParameters().Length == 0 &&
                    value.Name.IndexOf("Component", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                object returned;
                try { returned = property.GetValue(style, null); }
                catch { continue; }
                AppendComponents(returned, transaction, result);
            }
            return result.Distinct(ReferenceEqualityComparer.Instance).ToList();
        }

        private static void AppendComponents(
            object returned,
            Transaction transaction,
            IList<object> result)
        {
            if (returned == null || returned is string) return;
            IEnumerable enumerable = returned as IEnumerable;
            if (enumerable == null)
            {
                result.Add(returned);
                return;
            }
            foreach (object value in enumerable)
            {
                if (value is ObjectId)
                {
                    ObjectId id = (ObjectId)value;
                    if (id.IsNull || id.IsErased) continue;
                    try
                    {
                        DBObject component = transaction.GetObject(
                            id,
                            OpenMode.ForWrite,
                            false);
                        if (component != null) result.Add(component);
                    }
                    catch { }
                }
                else if (value != null)
                {
                    result.Add(value);
                }
            }
        }

        private static bool KeepLabelNearFeature(
            Database database,
            Transaction transaction,
            object label,
            ObjectId featureId,
            bool pipe)
        {
            if (database == null || transaction == null || label == null ||
                featureId.IsNull || featureId.IsErased) return false;
            DBObject feature;
            try
            {
                feature = transaction.GetObject(
                    featureId,
                    OpenMode.ForRead,
                    false);
            }
            catch
            {
                return false;
            }
            Point3d anchor;
            Vector3d normal = Vector3d.YAxis;
            CivilPipe pipeObject = feature as CivilPipe;
            CivilStructure structure = feature as CivilStructure;
            if (pipe && pipeObject != null)
            {
                try
                {
                    Point3d start = pipeObject.GetPointAtParam(0.0);
                    Point3d end = pipeObject.GetPointAtParam(1.0);
                    anchor = new Point3d(
                        (start.X + end.X) * 0.5,
                        (start.Y + end.Y) * 0.5,
                        (start.Z + end.Z) * 0.5);
                    Vector3d direction = end - start;
                    if (direction.Length > 1e-8)
                        normal = new Vector3d(-direction.Y, direction.X, 0.0).GetNormal();
                }
                catch
                {
                    return false;
                }
            }
            else if (structure != null)
            {
                anchor = structure.Position;
            }
            else
            {
                return false;
            }

            Point3d current;
            if (!ReadPoint(label, out current, "LabelLocation", "Location"))
                return false;
            double desiredOffset = Math.Max(
                PaperAnnotationScale.ModelDistance(database, pipe ? 4.0 : 3.0),
                0.001);
            double maximumOffset = Math.Max(
                PaperAnnotationScale.ModelDistance(database, 12.0),
                desiredOffset * 2.0);
            Point3d desired = anchor + normal * desiredOffset;
            if (current.DistanceTo(anchor) <= maximumOffset) return false;
            return SetPoint(label, desired, "LabelLocation", "Location");
        }

        private static void ResetDraggedState(object label)
        {
            SetBoolean(label, false,
                "Pinned", "IsLabelPinned", "LabelIsPinned");
            foreach (string name in new[]
            {
                "DraggedState", "LabelDraggedState", "Dragged"
            })
            {
                try
                {
                    PropertyInfo property = label.GetType().GetProperty(
                        name,
                        BindingFlags.Public | BindingFlags.Instance);
                    if (property == null || !property.CanWrite) continue;
                    if (property.PropertyType == typeof(bool))
                    {
                        property.SetValue(label, false, null);
                        continue;
                    }
                    if (!property.PropertyType.IsEnum) continue;
                    string value = Enum.GetNames(property.PropertyType)
                        .FirstOrDefault(item =>
                            item.IndexOf("Not", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            item.IndexOf("None", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            item.IndexOf("False", StringComparison.OrdinalIgnoreCase) >= 0);
                    if (!string.IsNullOrWhiteSpace(value))
                        property.SetValue(
                            label,
                            Enum.Parse(property.PropertyType, value),
                            null);
                }
                catch { }
            }
            SetBoolean(label, false,
                "ShowAnchorMarker", "AnchorMarkerVisible", "ShowDraggedStateMarker");
        }

        private static void ClearTextOverrides(object label)
        {
            foreach (string name in new[]
            {
                "ClearAllTextComponentOverrides",
                "ClearAllLabelTextComponentOverrides"
            })
            {
                try
                {
                    MethodInfo method = label.GetType().GetMethod(
                        name,
                        BindingFlags.Public | BindingFlags.Instance,
                        null,
                        Type.EmptyTypes,
                        null);
                    if (method != null) method.Invoke(label, null);
                }
                catch { }
            }
        }

        private static void ConfigurePlanReadable(object value)
        {
            SetBoolean(value, true,
                "PlanReadability", "PlanReadable", "IsPlanReadable", "Visibility");
            foreach (string name in new[]
            {
                "TextOrientation", "Orientation", "RotationType"
            })
            {
                try
                {
                    PropertyInfo property = value.GetType().GetProperty(
                        name,
                        BindingFlags.Public | BindingFlags.Instance);
                    if (property == null || !property.CanWrite ||
                        !property.PropertyType.IsEnum) continue;
                    string selected = Enum.GetNames(property.PropertyType)
                        .FirstOrDefault(item =>
                            item.IndexOf("View", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            item.IndexOf("Plan", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            item.IndexOf("Readable", StringComparison.OrdinalIgnoreCase) >= 0);
                    if (!string.IsNullOrWhiteSpace(selected))
                        property.SetValue(
                            value,
                            Enum.Parse(property.PropertyType, selected),
                            null);
                }
                catch { }
            }
        }

        private static void SetVisibility(object value, bool visible)
        {
            SetBoolean(value, visible,
                "Visibility", "Visible", "IsVisible", "Show");
        }

        private static string ReadName(object value)
        {
            if (value == null) return string.Empty;
            try
            {
                PropertyInfo property = value.GetType().GetProperty(
                    "Name",
                    BindingFlags.Public | BindingFlags.Instance);
                return property == null
                    ? string.Empty
                    : Convert.ToString(
                        property.GetValue(value, null),
                        CultureInfo.InvariantCulture) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string Read(
            ProjectStyleSelection selection,
            string key)
        {
            if (selection == null || !selection.Exists) return string.Empty;
            string value;
            return selection.Values.TryGetValue(key, out value) && Useful(value)
                ? value
                : string.Empty;
        }

        private static bool Useful(string value)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                !string.Equals(
                    value,
                    "<Use drawing default>",
                    StringComparison.OrdinalIgnoreCase);
        }

        private static ObjectId ReadObjectId(
            object value,
            params string[] names)
        {
            if (value == null) return ObjectId.Null;
            foreach (string name in names)
            {
                try
                {
                    PropertyInfo property = value.GetType().GetProperty(
                        name,
                        BindingFlags.Public | BindingFlags.Instance);
                    if (property == null || !property.CanRead ||
                        property.PropertyType != typeof(ObjectId)) continue;
                    return (ObjectId)property.GetValue(value, null);
                }
                catch { }
            }
            return ObjectId.Null;
        }

        private static bool SetObjectId(
            object value,
            ObjectId id,
            params string[] names)
        {
            if (value == null || id.IsNull) return false;
            foreach (string name in names)
            {
                try
                {
                    PropertyInfo property = value.GetType().GetProperty(
                        name,
                        BindingFlags.Public | BindingFlags.Instance);
                    if (property == null || !property.CanWrite ||
                        property.PropertyType != typeof(ObjectId)) continue;
                    property.SetValue(value, id, null);
                    return true;
                }
                catch { }
            }
            return false;
        }

        private static bool ReadPoint(
            object value,
            out Point3d point,
            params string[] names)
        {
            point = Point3d.Origin;
            if (value == null) return false;
            foreach (string name in names)
            {
                try
                {
                    PropertyInfo property = value.GetType().GetProperty(
                        name,
                        BindingFlags.Public | BindingFlags.Instance);
                    if (property == null || !property.CanRead ||
                        property.PropertyType != typeof(Point3d)) continue;
                    point = (Point3d)property.GetValue(value, null);
                    return true;
                }
                catch { }
            }
            return false;
        }

        private static bool SetPoint(
            object value,
            Point3d point,
            params string[] names)
        {
            if (value == null) return false;
            foreach (string name in names)
            {
                try
                {
                    PropertyInfo property = value.GetType().GetProperty(
                        name,
                        BindingFlags.Public | BindingFlags.Instance);
                    if (property == null || !property.CanWrite ||
                        property.PropertyType != typeof(Point3d)) continue;
                    property.SetValue(value, point, null);
                    return true;
                }
                catch { }
            }
            return false;
        }

        private static void SetBoolean(
            object value,
            bool requested,
            params string[] names)
        {
            if (value == null) return;
            foreach (string name in names)
            {
                try
                {
                    PropertyInfo property = value.GetType().GetProperty(
                        name,
                        BindingFlags.Public | BindingFlags.Instance);
                    if (property == null || !property.CanWrite) continue;
                    if (property.PropertyType == typeof(bool))
                    {
                        property.SetValue(value, requested, null);
                        return;
                    }
                    if (!property.PropertyType.IsEnum) continue;
                    string selected = Enum.GetNames(property.PropertyType)
                        .FirstOrDefault(item => requested
                            ? item.IndexOf("True", StringComparison.OrdinalIgnoreCase) >= 0 ||
                              item.IndexOf("Yes", StringComparison.OrdinalIgnoreCase) >= 0 ||
                              item.IndexOf("On", StringComparison.OrdinalIgnoreCase) >= 0 ||
                              item.IndexOf("Visible", StringComparison.OrdinalIgnoreCase) >= 0
                            : item.IndexOf("False", StringComparison.OrdinalIgnoreCase) >= 0 ||
                              item.IndexOf("No", StringComparison.OrdinalIgnoreCase) >= 0 ||
                              item.IndexOf("Off", StringComparison.OrdinalIgnoreCase) >= 0 ||
                              item.IndexOf("Hidden", StringComparison.OrdinalIgnoreCase) >= 0);
                    if (string.IsNullOrWhiteSpace(selected)) continue;
                    property.SetValue(
                        value,
                        Enum.Parse(property.PropertyType, selected),
                        null);
                    return;
                }
                catch { }
            }
        }

        private static void SetDouble(
            object value,
            double requested,
            params string[] names)
        {
            if (value == null) return;
            foreach (string name in names)
            {
                try
                {
                    PropertyInfo property = value.GetType().GetProperty(
                        name,
                        BindingFlags.Public | BindingFlags.Instance);
                    if (property == null || !property.CanWrite ||
                        property.PropertyType != typeof(double)) continue;
                    property.SetValue(value, requested, null);
                    return;
                }
                catch { }
            }
        }
    }

    internal sealed class ProfileBandRuntimeResult
    {
        internal int ProfileViews { get; set; }
        internal int BandSetsApplied { get; set; }
        internal int BandItemsLinked { get; set; }
        internal int NetworkPartsAdded { get; set; }
        internal int StylesImported { get; set; }
        internal int Skipped { get; set; }
        internal string Warning { get; set; }
    }

    internal static class ProfileViewBandRuntimeManager
    {
        private const string SewerProfileApp = "CE_TOOLS_SEWPROFILE";

        internal static ProfileBandRuntimeResult RunBatch(Document document)
        {
            var result = new ProfileBandRuntimeResult();
            if (document == null) return result;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (civilDocument == null)
            {
                result.Warning = "No active Civil 3D document is available.";
                return result;
            }

            List<string> choices = ReadBandStyleNames(document);
            if (choices.Count == 0)
            {
                result.StylesImported += ImportBundledStyles(document);
                choices = ReadBandStyleNames(document);
            }
            if (choices.Count == 0)
            {
                result.Warning = "No profile-view band-set styles are installed.";
                return result;
            }

            SewerProductionSettings sewer = SewerProductionSettings.Read(document.Database);
            ProjectStyleSelection selection = ProjectStyleCenterCommands.ReadSelection(document.Database);
            string preferred = Useful(sewer.ProfileViewBandSetStyle)
                ? sewer.ProfileViewBandSetStyle
                : Read(selection, "Profile View Band Set Style");
            if (!choices.Any(item => string.Equals(
                    item,
                    preferred,
                    StringComparison.OrdinalIgnoreCase)))
                preferred = choices[0];

            var model = new ProductionSettingsDialogModel(
                "CE Tools - Batch Profile View Band Sets",
                "Apply one or two band-set styles to multiple profile views. CE Tools links the live alignment profiles and gravity network so structure/manhole data can populate at each station.");
            model.AddChoice(
                "Primary", "01 Band Sets", "Primary/bottom band set", preferred,
                "The primary style is applied first and linked to the profile view's current profiles and sewer network.",
                choices);
            var secondary = new List<string> { "<None>" };
            secondary.AddRange(choices);
            model.AddChoice(
                "Secondary", "01 Band Sets", "Secondary/top band set", "<None>",
                "Optionally apply a second band set to the same profile views.",
                secondary);
            model.AddChoice(
                "Target", "02 Profile Views", "Profile views to update", "Select multiple profile views",
                "Select profile views in the drawing, update all CE sewer profile views, or update every profile view.",
                new[]
                {
                    "Select multiple profile views",
                    "All CE sewer profile views",
                    "All profile views"
                });
            model.AddChoice(
                "Existing", "03 Existing Bands", "Existing band handling", "Replace existing bands",
                "Replace the current band collection or append the selected band set where the installed Civil 3D API supports it.",
                new[] { "Replace existing bands", "Append selected bands" });
            model.AddChoice(
                "Import", "04 Missing Styles", "Import supplied CE styles when missing", "Yes",
                "Automatically import the supplied CE project style sources when a requested band set is not yet installed.",
                new[] { "Yes", "No" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return result;

            string primary = model.Text("Primary");
            string secondaryName = model.Text("Secondary");
            bool replace = string.Equals(
                model.Text("Existing"),
                "Replace existing bands",
                StringComparison.OrdinalIgnoreCase);
            bool autoImport = string.Equals(
                model.Text("Import"),
                "Yes",
                StringComparison.OrdinalIgnoreCase);
            List<ObjectId> views = ResolveTargetViews(
                document,
                model.Text("Target"));
            return Apply(
                document,
                views,
                primary,
                string.Equals(secondaryName, "<None>", StringComparison.OrdinalIgnoreCase)
                    ? string.Empty
                    : secondaryName,
                replace,
                autoImport,
                result);
        }

        internal static ProfileBandRuntimeResult RefreshAll(Document document)
        {
            var result = new ProfileBandRuntimeResult();
            if (document == null) return result;
            SewerProductionSettings sewer = SewerProductionSettings.Read(document.Database);
            ProjectStyleSelection selection = ProjectStyleCenterCommands.ReadSelection(document.Database);
            string style = Useful(sewer.ProfileViewBandSetStyle)
                ? sewer.ProfileViewBandSetStyle
                : Read(selection, "Profile View Band Set Style");
            if (!Useful(style))
            {
                result.Warning = "No saved profile-view band-set style is selected.";
                return result;
            }
            return Apply(
                document,
                ResolveTargetViews(document, "All CE sewer profile views"),
                style,
                string.Empty,
                true,
                true,
                result);
        }

        private static ProfileBandRuntimeResult Apply(
            Document document,
            IList<ObjectId> viewIds,
            string primaryName,
            string secondaryName,
            bool replace,
            bool autoImport,
            ProfileBandRuntimeResult result)
        {
            if (document == null || result == null) return result;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (civilDocument == null) return result;
            if (viewIds == null || viewIds.Count == 0)
            {
                result.Warning = "No matching profile views were selected or found.";
                return result;
            }

            try
            {
                using (DocumentLock documentLock = document.LockDocument())
                using (Transaction transaction =
                    document.Database.TransactionManager.StartTransaction())
                {
                    string resolved;
                    ObjectId primaryId = CivilStyleCatalogV2.ResolveStyleId(
                        document.Database,
                        civilDocument,
                        "Profile View Band Set Style",
                        primaryName,
                        transaction,
                        out resolved);
                    if (primaryId.IsNull && autoImport)
                    {
                        transaction.Abort();
                        result.StylesImported += ImportBundledStyles(document);
                        return Apply(
                            document,
                            viewIds,
                            primaryName,
                            secondaryName,
                            replace,
                            false,
                            result);
                    }
                    ObjectId secondaryId = ObjectId.Null;
                    if (Useful(secondaryName))
                    {
                        secondaryId = CivilStyleCatalogV2.ResolveStyleId(
                            document.Database,
                            civilDocument,
                            "Profile View Band Set Style",
                            secondaryName,
                            transaction,
                            out resolved);
                    }

                    foreach (ObjectId viewId in viewIds.Distinct())
                    {
                        DBObject view;
                        try
                        {
                            view = transaction.GetObject(
                                viewId,
                                OpenMode.ForWrite,
                                false);
                        }
                        catch
                        {
                            result.Skipped++;
                            continue;
                        }
                        if (view == null || view.GetType().Name.IndexOf(
                                "ProfileView",
                                StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            result.Skipped++;
                            continue;
                        }
                        ObjectId alignmentId = ReadObjectId(
                            view,
                            "AlignmentId", "ParentAlignmentId");
                        List<ObjectId> profiles = ReadProfiles(
                            transaction,
                            alignmentId);
                        ObjectId networkId = ReadLinkedNetwork(
                            document.Database,
                            view);
                        result.ProfileViews++;
                        if (ApplyBandSet(
                                view,
                                primaryId,
                                replace,
                                false)) result.BandSetsApplied++;
                        if (!secondaryId.IsNull && ApplyBandSet(
                                view,
                                secondaryId,
                                false,
                                true)) result.BandSetsApplied++;
                        result.BandItemsLinked += LinkBandItems(
                            view,
                            alignmentId,
                            profiles,
                            networkId);
                        result.NetworkPartsAdded += AddNetworkParts(
                            transaction,
                            networkId,
                            viewId,
                            ReadBranchName(view));
                    }
                    transaction.Commit();
                }
            }
            catch (System.Exception exception)
            {
                result.Warning = exception.Message;
            }
            return result;
        }

        private static List<string> ReadBandStyleNames(Document document)
        {
            Dictionary<string, List<string>> catalogue =
                CivilStyleCatalogV2.ReadProjectCatalogue(
                    document,
                    new[] { "Profile View Band Set Style" });
            List<string> values;
            if (!catalogue.TryGetValue(
                    "Profile View Band Set Style",
                    out values) || values == null)
                return new List<string>();
            return values
                .Where(Useful)
                .Where(value => !string.Equals(
                    value,
                    "<Use drawing default>",
                    StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private static int ImportBundledStyles(Document document)
        {
            if (document == null) return 0;
            try
            {
                Type type = typeof(ProjectStyleCenterCommands);
                MethodInfo find = type.GetMethod(
                    "FindBundledStyleSources",
                    BindingFlags.NonPublic | BindingFlags.Static);
                MethodInfo export = type.GetMethod(
                    "ExportStylesFromSource",
                    BindingFlags.NonPublic | BindingFlags.Static);
                IEnumerable sources = find == null
                    ? null
                    : find.Invoke(null, null) as IEnumerable;
                if (sources == null || export == null) return 0;
                int imported = 0;
                foreach (object source in sources)
                {
                    string path = Convert.ToString(
                        ReadProperty(source, "FilePath"),
                        CultureInfo.InvariantCulture);
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    object value = export.Invoke(
                        null,
                        new object[]
                        {
                            path,
                            document.Database,
                            StyleConflictResolverType.Rename
                        });
                    imported += Convert.ToInt32(
                        value,
                        CultureInfo.InvariantCulture);
                }
                return imported;
            }
            catch
            {
                return 0;
            }
        }

        private static List<ObjectId> ResolveTargetViews(
            Document document,
            string target)
        {
            if (document == null) return new List<ObjectId>();
            if (string.Equals(
                    target,
                    "Select multiple profile views",
                    StringComparison.OrdinalIgnoreCase))
            {
                PromptSelectionResult selected = document.Editor.GetSelection(
                    new PromptSelectionOptions
                    {
                        MessageForAdding = "\nSelect multiple Civil 3D profile views: ",
                        AllowDuplicates = false,
                        RejectObjectsFromNonCurrentSpace = true
                    });
                if (selected.Status != PromptStatus.OK || selected.Value == null)
                    return new List<ObjectId>();
                using (Transaction transaction =
                    document.Database.TransactionManager.StartTransaction())
                {
                    return selected.Value.GetObjectIds()
                        .Where(id => IsProfileView(transaction, id))
                        .Distinct()
                        .ToList();
                }
            }

            bool ceOnly = string.Equals(
                target,
                "All CE sewer profile views",
                StringComparison.OrdinalIgnoreCase);
            var result = new List<ObjectId>();
            using (Transaction transaction =
                document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord modelSpace = transaction.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(document.Database),
                    OpenMode.ForRead,
                    false) as BlockTableRecord;
                if (modelSpace == null) return result;
                foreach (ObjectId id in modelSpace)
                {
                    DBObject value;
                    try
                    {
                        value = transaction.GetObject(
                            id,
                            OpenMode.ForRead,
                            false);
                    }
                    catch { continue; }
                    if (value == null || value.GetType().Name.IndexOf(
                            "ProfileView",
                            StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (ceOnly && value.GetXDataForApplication(
                            SewerProfileApp) == null) continue;
                    result.Add(id);
                }
            }
            return result;
        }

        private static bool IsProfileView(
            Transaction transaction,
            ObjectId id)
        {
            if (id.IsNull || id.IsErased) return false;
            try
            {
                DBObject value = transaction.GetObject(
                    id,
                    OpenMode.ForRead,
                    false);
                return value != null && value.GetType().Name.IndexOf(
                    "ProfileView",
                    StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch
            {
                return false;
            }
        }

        private static bool ApplyBandSet(
            object view,
            ObjectId styleId,
            bool replace,
            bool top)
        {
            if (view == null || styleId.IsNull) return false;
            object bands = ReadProperty(view, "Bands");
            bool applied = SetObjectId(
                view,
                styleId,
                "BandSetStyleId", "ProfileViewBandSetStyleId");
            if (bands == null) return applied;
            if (replace)
            {
                InvokeNoArgument(bands,
                    "Clear", "RemoveAll", "ClearAll", "EraseAll");
            }
            foreach (string name in new[]
            {
                top ? "ImportTopBandSetStyle" : "ImportBandSetStyle",
                top ? "AddTopBandSetStyle" : "AddBandSetStyle",
                "SetBandSetStyle"
            })
            {
                if (InvokeObjectId(bands, name, styleId))
                    return true;
            }
            return applied;
        }

        private static int LinkBandItems(
            object view,
            ObjectId alignmentId,
            IList<ObjectId> profiles,
            ObjectId networkId)
        {
            object bands = ReadProperty(view, "Bands");
            if (bands == null) return 0;
            int linked = 0;
            foreach (string methodName in new[]
            {
                "GetBottomBandItems", "GetTopBandItems", "GetBandItems"
            })
            {
                object returned;
                try
                {
                    MethodInfo method = bands.GetType().GetMethod(
                        methodName,
                        BindingFlags.Public | BindingFlags.Instance,
                        null,
                        Type.EmptyTypes,
                        null);
                    returned = method == null ? null : method.Invoke(bands, null);
                }
                catch
                {
                    continue;
                }
                IEnumerable items = returned as IEnumerable;
                if (items == null) continue;
                foreach (object item in items)
                {
                    if (item == null) continue;
                    bool changed = false;
                    changed = SetObjectId(item, alignmentId,
                        "AlignmentId", "ParentAlignmentId") || changed;
                    if (profiles.Count > 0)
                        changed = SetObjectId(item, profiles[0],
                            "Profile1Id", "ProfileId", "DataProfileId") || changed;
                    if (profiles.Count > 1)
                        changed = SetObjectId(item, profiles[1],
                            "Profile2Id", "ComparisonProfileId") || changed;
                    else if (profiles.Count > 0)
                        changed = SetObjectId(item, profiles[0],
                            "Profile2Id", "ComparisonProfileId") || changed;
                    if (!networkId.IsNull)
                    {
                        changed = SetObjectId(item, networkId,
                            "NetworkId", "PipeNetworkId", "DataSourceId", "SourceId") || changed;
                    }
                    if (changed) linked++;
                }
            }
            InvokeNoArgument(bands,
                "Update", "Rebuild", "Refresh", "CommitChanges");
            return linked;
        }

        private static List<ObjectId> ReadProfiles(
            Transaction transaction,
            ObjectId alignmentId)
        {
            var result = new List<ObjectId>();
            if (alignmentId.IsNull || alignmentId.IsErased) return result;
            object alignment;
            try
            {
                alignment = transaction.GetObject(
                    alignmentId,
                    OpenMode.ForRead,
                    false);
            }
            catch
            {
                return result;
            }
            MethodInfo method = alignment.GetType().GetMethod(
                "GetProfileIds",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                Type.EmptyTypes,
                null);
            IEnumerable values = method == null
                ? null
                : method.Invoke(alignment, null) as IEnumerable;
            if (values == null) return result;
            foreach (object value in values)
            {
                if (value is ObjectId)
                {
                    ObjectId id = (ObjectId)value;
                    if (!id.IsNull && !id.IsErased) result.Add(id);
                }
            }
            return result;
        }

        private static ObjectId ReadLinkedNetwork(
            Database database,
            object view)
        {
            DBObject databaseObject = view as DBObject;
            if (database == null || databaseObject == null) return ObjectId.Null;
            ResultBuffer buffer = databaseObject.GetXDataForApplication(
                SewerProfileApp);
            if (buffer == null) return ObjectId.Null;
            string[] values = buffer.AsArray()
                .Where(value => value.TypeCode ==
                    (int)DxfCode.ExtendedDataAsciiString)
                .Select(value => Convert.ToString(
                    value.Value,
                    CultureInfo.InvariantCulture))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray();
            if (values.Length == 0) return ObjectId.Null;
            string branchKey = values[0];
            string handle = branchKey.Split('|')[0];
            try
            {
                long number = long.Parse(
                    handle,
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture);
                ObjectId id = database.GetObjectId(
                    false,
                    new Handle(number),
                    0);
                return !id.IsNull && !id.IsErased
                    ? id
                    : ObjectId.Null;
            }
            catch
            {
                return ObjectId.Null;
            }
        }

        private static string ReadBranchName(object view)
        {
            DBObject databaseObject = view as DBObject;
            ResultBuffer buffer = databaseObject == null
                ? null
                : databaseObject.GetXDataForApplication(SewerProfileApp);
            if (buffer == null) return string.Empty;
            string value = buffer.AsArray()
                .Where(item => item.TypeCode ==
                    (int)DxfCode.ExtendedDataAsciiString)
                .Select(item => Convert.ToString(
                    item.Value,
                    CultureInfo.InvariantCulture))
                .FirstOrDefault(item => item != null && item.Contains("|"));
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            string[] parts = value.Split(new[] { '|' }, 2);
            return parts.Length == 2 ? parts[1] : string.Empty;
        }

        private static int AddNetworkParts(
            Transaction transaction,
            ObjectId networkId,
            ObjectId viewId,
            string branchName)
        {
            if (transaction == null || networkId.IsNull || viewId.IsNull)
                return 0;
            CivilNetwork network;
            try
            {
                network = transaction.GetObject(
                    networkId,
                    OpenMode.ForRead,
                    false) as CivilNetwork;
            }
            catch
            {
                return 0;
            }
            if (network == null) return 0;
            int added = 0;
            var partIds = new List<ObjectId>();
            foreach (ObjectId id in network.GetPipeIds()) partIds.Add(id);
            foreach (ObjectId id in network.GetStructureIds()) partIds.Add(id);
            foreach (ObjectId id in partIds)
            {
                DBObject part;
                try
                {
                    part = transaction.GetObject(
                        id,
                        OpenMode.ForRead,
                        false);
                }
                catch { continue; }
                if (part == null) continue;
                if (Useful(branchName))
                {
                    string description = Convert.ToString(
                        ReadProperty(part, "Description"),
                        CultureInfo.InvariantCulture);
                    if (!string.Equals(
                            description,
                            branchName,
                            StringComparison.OrdinalIgnoreCase)) continue;
                }
                MethodInfo method = part.GetType().GetMethod(
                    "AddToProfileView",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new[] { typeof(ObjectId) },
                    null);
                if (method == null) continue;
                try
                {
                    method.Invoke(part, new object[] { viewId });
                    added++;
                }
                catch
                {
                    // A part already in the view is treated as current.
                }
            }
            return added;
        }

        private static object ReadProperty(
            object value,
            string name)
        {
            if (value == null) return null;
            try
            {
                PropertyInfo property = value.GetType().GetProperty(
                    name,
                    BindingFlags.Public | BindingFlags.Instance);
                return property == null || property.GetIndexParameters().Length != 0
                    ? null
                    : property.GetValue(value, null);
            }
            catch
            {
                return null;
            }
        }

        private static ObjectId ReadObjectId(
            object value,
            params string[] names)
        {
            if (value == null) return ObjectId.Null;
            foreach (string name in names)
            {
                object current = ReadProperty(value, name);
                if (current is ObjectId) return (ObjectId)current;
            }
            return ObjectId.Null;
        }

        private static bool SetObjectId(
            object value,
            ObjectId id,
            params string[] names)
        {
            if (value == null || id.IsNull) return false;
            foreach (string name in names)
            {
                try
                {
                    PropertyInfo property = value.GetType().GetProperty(
                        name,
                        BindingFlags.Public | BindingFlags.Instance);
                    if (property == null || !property.CanWrite ||
                        property.PropertyType != typeof(ObjectId)) continue;
                    ObjectId current = property.CanRead
                        ? (ObjectId)property.GetValue(value, null)
                        : ObjectId.Null;
                    if (current == id) return true;
                    property.SetValue(value, id, null);
                    return true;
                }
                catch { }
            }
            return false;
        }

        private static bool InvokeObjectId(
            object value,
            string methodName,
            ObjectId id)
        {
            if (value == null || id.IsNull) return false;
            foreach (MethodInfo method in value.GetType().GetMethods(
                BindingFlags.Public | BindingFlags.Instance)
                .Where(item => string.Equals(
                    item.Name,
                    methodName,
                    StringComparison.Ordinal)))
            {
                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length != 1 ||
                    parameters[0].ParameterType != typeof(ObjectId)) continue;
                try
                {
                    method.Invoke(value, new object[] { id });
                    return true;
                }
                catch { }
            }
            return false;
        }

        private static void InvokeNoArgument(
            object value,
            params string[] methodNames)
        {
            if (value == null) return;
            foreach (string name in methodNames)
            {
                try
                {
                    MethodInfo method = value.GetType().GetMethod(
                        name,
                        BindingFlags.Public | BindingFlags.Instance,
                        null,
                        Type.EmptyTypes,
                        null);
                    if (method == null) continue;
                    method.Invoke(value, null);
                    return;
                }
                catch { }
            }
        }

        private static string Read(
            ProjectStyleSelection selection,
            string key)
        {
            if (selection == null || !selection.Exists) return string.Empty;
            string value;
            return selection.Values.TryGetValue(key, out value)
                ? value
                : string.Empty;
        }

        private static bool Useful(string value)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                !string.Equals(
                    value,
                    "<Use drawing default>",
                    StringComparison.OrdinalIgnoreCase);
        }
    }
}
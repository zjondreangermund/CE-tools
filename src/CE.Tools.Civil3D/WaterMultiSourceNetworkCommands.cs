using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilFeatureLine = Autodesk.Civil.DatabaseServices.FeatureLine;
using CivilNetwork = Autodesk.Civil.DatabaseServices.Network;
using CivilPipe = Autodesk.Civil.DatabaseServices.Pipe;
using ConnectorPositionType = Autodesk.Civil.DatabaseServices.ConnectorPositionType;
using DomainType = Autodesk.Civil.DatabaseServices.DomainType;
using PartFamily = Autodesk.Civil.DatabaseServices.Styles.PartFamily;
using PartsList = Autodesk.Civil.DatabaseServices.Styles.PartsList;
using PartSize = Autodesk.Civil.DatabaseServices.Styles.PartSize;

[assembly: CommandClass(typeof(CETools.Civil3D.WaterMultiSourceNetworkCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Creates one Civil 3D gravity water network directly from a complete set of
    /// selected source lines/polylines/feature lines. Unlike Civil 3D's native
    /// CreateNetworkFromObject command, this workflow never falls back to a second
    /// one-object selection prompt. Shared source vertices reuse one structure so
    /// branches become part of the same connected network.
    /// </summary>
    public sealed class WaterMultiSourceNetworkCommands
    {
        private const double PointTolerance = 0.001;

        [CommandMethod(
            "CE_TOOLS",
            "CE_WATERNETWORKMULTI",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void CreateWaterNetworkFromMultipleSources()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (document == null || civilDocument == null) return;

            Editor editor = document.Editor;
            PromptSelectionResult selected = editor.SelectImplied();
            List<ObjectId> sourceIds;
            if (selected.Status == PromptStatus.OK && selected.Value != null && selected.Value.Count > 0)
            {
                sourceIds = FilterSupportedSources(document.Database, selected.Value.GetObjectIds());
                editor.SetImpliedSelection(new ObjectId[0]);
            }
            else
            {
                var scope = new PromptKeywordOptions("\nWater source selection [All/Selected] <Selected>: ");
                scope.Keywords.Add("All");
                scope.Keywords.Add("Selected");
                scope.AllowNone = true;
                PromptResult choice = editor.GetKeywords(scope);
                if (choice.Status == PromptStatus.Cancel) return;
                if (choice.Status == PromptStatus.OK &&
                    string.Equals(choice.StringResult, "All", StringComparison.OrdinalIgnoreCase))
                {
                    var allIds = new List<ObjectId>();
                    using (Transaction read = document.Database.TransactionManager.StartTransaction())
                    {
                        var model = (BlockTableRecord)read.GetObject(
                            SymbolUtilityServices.GetBlockModelSpaceId(document.Database), OpenMode.ForRead);
                        foreach (ObjectId id in model) allIds.Add(id);
                    }
                    sourceIds = FilterSupportedSources(document.Database, allIds);
                }
                else
                {
                    selected = editor.GetSelection(new PromptSelectionOptions
                    {
                        MessageForAdding = "\nSelect water source lines/polylines/feature lines: ",
                        MessageForRemoval = "\nRemove source objects: ",
                        AllowDuplicates = false,
                        RejectObjectsFromNonCurrentSpace = true
                    });
                    if (selected.Status != PromptStatus.OK ||
                        selected.Value == null || selected.Value.Count == 0) return;
                    sourceIds = FilterSupportedSources(document.Database, selected.Value.GetObjectIds());
                }
            }
            if (sourceIds.Count == 0)
            {
                ShowMessage(
                    "\nCE_WATERNETWORKMULTI: no supported line/polyline/feature-line sources were selected.");
                return;
            }

            List<PartsListChoice> partsLists = ReadPartsLists(
                document.Database,
                civilDocument).Where(item => IsWaterName(item.Name)).ToList();
            if (partsLists.Count == 0)
            {
                ShowMessage(
                    "\nCE_WATERNETWORKMULTI: this drawing has no Civil 3D gravity-network Parts List. Import/create a water parts list first.");
                return;
            }

            string preferredPartsList = PreferredPartsList(partsLists);
            var setup = new ProductionSettingsDialogModel(
                "CE Tools - Water Network from Multiple Polylines",
                "Creates ONE Civil 3D gravity water network directly from every selected source. " +
                "There is no second single-object Create Network From Object prompt. Shared vertices reuse the same structure/water node.");
            setup.AddText(
                "NetworkName",
                "01 Network",
                "Network name",
                "CE-Water Network",
                "Civil 3D will make the name unique if the drawing already contains it.");
            setup.AddChoice(
                "PartsList",
                "01 Network",
                "Network parts list",
                preferredPartsList,
                "Choose the installed gravity-network parts list used for every selected source.",
                partsLists.Select(item => item.Name).ToArray());
            // Persist a reference ground surface on the gravity network so water
            // structures, pipe rules and subsequent profile workflows can find it.
            var surfaceChoices = ReadTargetSurfaces(document.Database, civilDocument);
            setup.AddChoice(
                "TargetSurface", "01 Network", "Target ground surface", "<None>",
                "Civil 3D reference surface for the new gravity water network; source pipe elevations are preserved.",
                new[] { "<None>" }.Concat(surfaceChoices.Select(item => item.Name)).ToArray());
            setup.AddChoice(
                "Rules",
                "02 Creation",
                "Pipe/structure rules",
                "Do not apply rules",
                "Apply Civil 3D part rules while creating the network, or preserve the selected source geometry without rules.",
                new[] { "Do not apply rules", "Apply rules" });
            setup.AddChoice(
                "Connections",
                "02 Creation",
                "Shared vertices / junctions",
                "Connect through structures",
                "Reuse one structure at coincident source vertices so branches and consecutive pipe segments are connected.",
                new[] { "Connect through structures", "Create parts without connections" });
            setup.AddChoice(
                "SourceObjects",
                "03 Sources",
                "Source objects after creation",
                "Keep source objects",
                "Keep the selected design strings for later editing/refresh, or erase them after the Civil 3D network is created.",
                new[] { "Keep source objects", "Erase source objects" });
            setup.AddChoice(
                "PreviouslyCreated",
                "03 Sources",
                "Previously completed CE source",
                "Skip previously completed",
                "Skip sources already tagged as completed for Water, or deliberately process them again.",
                new[] { "Skip previously completed", "Process again" });

            setup.AddChoice("CreateAlignments", "02 Creation", "Branch alignments from selected polylines", "Create one per selected polyline",
                "Preserve source polylines as linked alignment sources. Configure branch priority and elevation direction after network creation.",
                new[] { "Create one per selected polyline", "Network only" });
            if (!DisciplineWorkflowDialogs.EditSettings(setup)) return;

            bool skipCompleted = string.Equals(
                setup.Text("PreviouslyCreated"),
                "Skip previously completed",
                StringComparison.OrdinalIgnoreCase);
            if (skipCompleted)
            {
                sourceIds = sourceIds
                    .Where(id => !NetworkSourceMarker.IsCompleted(
                        document.Database,
                        id,
                        "Water"))
                    .ToList();
            }
            if (sourceIds.Count == 0)
            {
                ShowMessage(
                    "\nCE_WATERNETWORKMULTI: all selected sources were already completed for Water. Choose 'Process again' to intentionally recreate them.");
                return;
            }

            PartsListChoice partsList = partsLists.FirstOrDefault(
                item => string.Equals(
                    item.Name,
                    setup.Text("PartsList"),
                    StringComparison.OrdinalIgnoreCase));
            if (partsList == null)
                partsList = partsLists[0];

            ObjectId targetSurfaceId = surfaceChoices
                .Where(item => item.Name == setup.Text("TargetSurface"))
                .Select(item => item.Id).FirstOrDefault();

            bool connect = string.Equals(
                setup.Text("Connections"),
                "Connect through structures",
                StringComparison.OrdinalIgnoreCase);

            List<PartChoice> pipeChoices = ReadPartChoices(
                document.Database,
                partsList.ObjectId,
                DomainType.Pipe,
                false);
            List<PartChoice> structureChoices = ReadPartChoices(
                document.Database,
                partsList.ObjectId,
                DomainType.Structure,
                false);
            if (pipeChoices.Count == 0)
            {
                ShowMessage("Parts list '{0}' contains no pipe sizes usable by this pipe/structure workflow. Add a water pipe size to this Pipe Network parts list. Pressure Network catalogues are a separate workflow.", partsList.Name);
                return;
            }
            if (connect && structureChoices.Count == 0)
            {
                ShowMessage("Parts list '{0}' contains no node/structure sizes. Add a Null Structure or a water node part to its Structures tab, then run again. A manhole is not required. To deliberately create pipes only, choose 'Create parts without connections'.", partsList.Name);
                return;
            }

            var parts = new ProductionSettingsDialogModel(
                "CE Tools - Water Pipe and Structure Parts",
                "Choose the pipe and water node/structure once. The selected parts are used for the complete multi-polyline network.");
            parts.AddChoice(
                "PipePart",
                "01 Pipes",
                "Pipe family / size",
                PreferredPart(pipeChoices, new[] { "water", "hdpe", "pvc" }),
                "Pipe family and size used for every generated water pipe.",
                pipeChoices.Select(item => item.Label).ToArray());
            if (connect) parts.AddChoice(
                "StructurePart",
                "02 Structures",
                "Structure / water node family / size",
                PreferredPart(structureChoices, new[] { "null", "water", "valve", "junction" }),
                "A Null Structure is allowed as a connection node. The selected water node style controls its display at source vertices only.",
                structureChoices.Select(item => item.Label).ToArray());
            var pipeStyles = ReadWaterStyles(document.Database, civilDocument.Styles.PipeStyles.Cast<ObjectId>());
            var structureStyles = ReadWaterStyles(document.Database, civilDocument.Styles.StructureStyles.Cast<ObjectId>());
            if (pipeStyles.Count == 0 || (connect && structureStyles.Count == 0))
            {
                ShowMessage("\nImport water pipe and water node/structure styles before creating the network.");
                return;
            }
            parts.AddChoice("PipeStyle", "01 Pipes", "Water pipe style", pipeStyles[0].Name,
                "Water style applied to every pipe.", pipeStyles.Select(s => s.Name).ToArray());
            if (connect) parts.AddChoice("StructureStyle", "02 Structures", "Water node style", structureStyles[0].Name,
                "Water style applied only at source vertices.", structureStyles.Select(s => s.Name).ToArray());
            if (!DisciplineWorkflowDialogs.EditSettings(parts)) return;

            PartChoice pipeChoice = FindPart(pipeChoices, parts.Text("PipePart"));
            PartChoice structureChoice = FindPart(
                structureChoices,
                parts.Text("StructurePart"));
            if (pipeChoice == null || (connect && structureChoice == null))
            {
                ShowMessage(
                    "\nCE_WATERNETWORKMULTI: the selected pipe/structure part is no longer available.");
                return;
            }

            pipeChoice.StyleId = pipeStyles.First(s => s.Name == parts.Text("PipeStyle")).ObjectId;
            if (connect) structureChoice.StyleId = structureStyles.First(s => s.Name == parts.Text("StructureStyle")).ObjectId;

            List<SourcePath> paths = ReadSourcePaths(
                document.Database,
                sourceIds);
            int segmentCount = paths.Sum(path => Math.Max(0, path.Points.Count - 1));
            if (paths.Count == 0 || segmentCount == 0)
            {
                ShowMessage(
                    "\nCE_WATERNETWORKMULTI: the selected objects do not contain usable source segments.");
                return;
            }

            bool applyRules = string.Equals(
                setup.Text("Rules"),
                "Apply rules",
                StringComparison.OrdinalIgnoreCase);
            bool eraseSources = string.Equals(
                setup.Text("SourceObjects"),
                "Erase source objects",
                StringComparison.OrdinalIgnoreCase);

            bool createAlignments = setup.Text("CreateAlignments") == "Create one per selected polyline";
            if (createAlignments && eraseSources)
            {
                eraseSources = false;
                editor.WriteMessage("\nSource objects retained because linked branch alignments were requested.");
            }
            string networkName = setup.Text("NetworkName");
            if (string.IsNullOrWhiteSpace(networkName))
                networkName = "CE-Water Network";

            ObjectId networkId = ObjectId.Null;
            int pipesCreated = 0;
            int structuresCreated = 0;
            try
            {
                using (DocumentLock documentLock = document.LockDocument())
                using (Transaction transaction =
                    document.Database.TransactionManager.StartTransaction())
                {
                    networkId = CivilNetwork.Create(
                        civilDocument,
                        ref networkName);
                    CivilNetwork network = transaction.GetObject(
                        networkId,
                        OpenMode.ForWrite,
                        false) as CivilNetwork;
                    if (network == null)
                        throw new InvalidOperationException(
                            "Civil 3D did not return the newly created gravity network.");

                    network.PartsListId = partsList.ObjectId;
                    if (!targetSurfaceId.IsNull)
                    {
                        // ReferenceSurfaceId is available for gravity networks;
                        // using reflection also tolerates host API variations.
                        var referenceSurface = network.GetType().GetProperty("ReferenceSurfaceId");
                        if (referenceSurface == null || !referenceSurface.CanWrite)
                            throw new InvalidOperationException("This Civil 3D version cannot assign a reference surface to the water gravity network.");
                        referenceSurface.SetValue(network, targetSurfaceId, null);
                    }
                    network.Description =
                        "CE Tools water network created from " +
                        paths.Count.ToString(CultureInfo.InvariantCulture) +
                        " selected source object(s).";

                    var structures = new Dictionary<string, ObjectId>(
                        StringComparer.OrdinalIgnoreCase);

                    foreach (SourcePath path in paths)
                    {
                        for (int index = 0; index < path.Points.Count - 1; index++)
                        {
                            Point3d start = path.Points[index];
                            Point3d end = path.Points[index + 1];
                            if (start.DistanceTo(end) <= PointTolerance)
                                continue;

                            ObjectId startStructure = ObjectId.Null;
                            ObjectId endStructure = ObjectId.Null;
                            if (connect)
                            {
                                startStructure = EnsureStructure(
                                    network,
                                    transaction,
                                    structures,
                                    structureChoice,
                                    start,
                                    applyRules,
                                    ref structuresCreated);
                                endStructure = EnsureStructure(
                                    network,
                                    transaction,
                                    structures,
                                    structureChoice,
                                    end,
                                    applyRules,
                                    ref structuresCreated);
                            }

                            ObjectId pipeId = ObjectId.Null;
                            network.AddLinePipe(
                                pipeChoice.FamilyId,
                                pipeChoice.SizeId,
                                new LineSegment3d(start, end),
                                ref pipeId,
                                applyRules);
                            if (pipeId.IsNull)
                                throw new InvalidOperationException("A water pipe could not be created; source completion was not saved.");

                            var styledPipe = transaction.GetObject(pipeId, OpenMode.ForWrite, false) as CivilPipe;
                            if (styledPipe == null) throw new InvalidOperationException("Created water pipe is unavailable.");
                            styledPipe.StyleId = pipeChoice.StyleId;
                            pipesCreated++;
                            if (connect &&
                                !startStructure.IsNull &&
                                !endStructure.IsNull)
                            {
                                CivilPipe pipe = transaction.GetObject(
                                    pipeId,
                                    OpenMode.ForWrite,
                                    false) as CivilPipe;
                                if (pipe != null)
                                {
                                    pipe.ConnectToStructure(
                                        ConnectorPositionType.Start,
                                        startStructure,
                                        true);
                                    pipe.ConnectToStructure(
                                        ConnectorPositionType.End,
                                        endStructure,
                                        true);
                                }
                            }
                        }
                    }

                    if (pipesCreated == 0)
                        throw new InvalidOperationException(
                            "No water pipes could be created from the selected sources.");

                    if (eraseSources)
                    {
                        foreach (ObjectId sourceId in sourceIds)
                        {
                            try
                            {
                                DBObject source = transaction.GetObject(
                                    sourceId,
                                    OpenMode.ForWrite,
                                    false);
                                if (source != null && !source.IsErased)
                                    source.Erase();
                            }
                            catch { }
                        }
                    }

                    transaction.Commit();
                }
            }
            catch (System.Exception exception)
            {
                ShowMessage(
                    "\nCE_WATERNETWORKMULTI failed. No partial network edit was committed. {0}",
                    exception.Message);
                return;
            }

            if (!eraseSources)
            {
                foreach (ObjectId sourceId in sourceIds)
                    NetworkSourceMarker.Mark(
                        document.Database,
                        sourceId,
                        "Water");
            }

            if (createAlignments)
            {
                var polylineIds = new List<ObjectId>();
                using (var tr = document.Database.TransactionManager.StartTransaction())
                    foreach (var id in sourceIds)
                        if (tr.GetObject(id, OpenMode.ForRead) is Polyline) polylineIds.Add(id);
                if (polylineIds.Count > 0)
                {
                    editor.SetImpliedSelection(polylineIds.ToArray());
                    new WaterProductionCommands().CreateAlignments();
                    editor.SetImpliedSelection(new ObjectId[0]);
                }
                else editor.WriteMessage("\nNo 2D source polylines for alignments. Use CE_WATERALIGN for the other source types.");
            }
            editor.Regen();
            ShowMessage(
                "\nCE_WATERNETWORKMULTI complete. Network='{0}'; selected sources={1}; pipes={2}; structures={3}. No single-object CreateNetworkFromObject prompt was used.",
                networkName,
                paths.Count,
                pipesCreated,
                structuresCreated);

            if (!networkId.IsNull)
            {
                try { editor.SetImpliedSelection(new[] { networkId }); }
                catch { }
            }
        }

        private sealed class TargetSurfaceChoice
        {
            internal string Name;
            internal ObjectId Id;
        }

        private static List<TargetSurfaceChoice> ReadTargetSurfaces(Database database, CivilDocument civil)
        {
            var results = new List<TargetSurfaceChoice>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in civil.GetSurfaceIds())
                {
                    var surface = transaction.GetObject(id, OpenMode.ForRead, false)
                        as Autodesk.Civil.DatabaseServices.Surface;
                    if (surface != null)
                        results.Add(new TargetSurfaceChoice { Name = surface.Name, Id = id });
                }
            }
            return results.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static void ShowMessage(string format, params object[] values)
        {
            System.Windows.Forms.MessageBox.Show(
                string.Format(CultureInfo.CurrentCulture, format, values).Trim(),
                "CE Tools - Water Network from Multiple Polylines",
                System.Windows.Forms.MessageBoxButtons.OK,
                System.Windows.Forms.MessageBoxIcon.Information);
        }

        private static ObjectId EnsureStructure(
            CivilNetwork network,
            Transaction transaction,
            IDictionary<string, ObjectId> structures,
            PartChoice structureChoice,
            Point3d point,
            bool applyRules,
            ref int created)
        {
            string key = NodeKey(point);
            ObjectId existing;
            if (structures.TryGetValue(key, out existing) &&
                !existing.IsNull &&
                !existing.IsErased)
                return existing;

            ObjectId structureId = ObjectId.Null;
            network.AddStructure(
                structureChoice.FamilyId,
                structureChoice.SizeId,
                point,
                0.0,
                ref structureId,
                applyRules);
            if (!structureId.IsNull)
            {
                var structure = transaction.GetObject(structureId, OpenMode.ForWrite, false)
                    as Autodesk.Civil.DatabaseServices.Structure;
                if (structure == null) throw new InvalidOperationException("Created water node is unavailable.");
                structure.StyleId = structureChoice.StyleId;
                structures[key] = structureId;
                created++;
            }
            if (structureId.IsNull) throw new InvalidOperationException("A water node could not be created.");
            return structureId;
        }

        private static string NodeKey(Point3d point)
        {
            long x = (long)Math.Round(point.X / PointTolerance);
            long y = (long)Math.Round(point.Y / PointTolerance);
            return x.ToString(CultureInfo.InvariantCulture) + "|" +
                   y.ToString(CultureInfo.InvariantCulture);
        }

        private static List<ObjectId> FilterSupportedSources(
            Database database,
            IEnumerable<ObjectId> ids)
        {
            var result = new List<ObjectId>();
            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids
                    .Where(value => !value.IsNull && !value.IsErased)
                    .Distinct())
                {
                    DBObject value;
                    try
                    {
                        value = transaction.GetObject(
                            id,
                            OpenMode.ForRead,
                            false);
                    }
                    catch
                    {
                        continue;
                    }

                    if (value is Line ||
                        value is Polyline ||
                        value is Polyline2d ||
                        value is Polyline3d ||
                        value is CivilFeatureLine)
                    {
                        result.Add(id);
                    }
                }
            }
            return result;
        }

        private static List<SourcePath> ReadSourcePaths(
            Database database,
            IEnumerable<ObjectId> sourceIds)
        {
            var result = new List<SourcePath>();
            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in sourceIds)
                {
                    DBObject value;
                    try
                    {
                        value = transaction.GetObject(
                            id,
                            OpenMode.ForRead,
                            false);
                    }
                    catch
                    {
                        continue;
                    }

                    List<Point3d> points = ReadPoints(
                        value,
                        transaction);
                    RemoveConsecutiveDuplicates(points);
                    if (points.Count < 2) continue;
                    result.Add(new SourcePath
                    {
                        SourceId = id,
                        Points = points
                    });
                }
            }
            return result;
        }

        private static List<Point3d> ReadPoints(
            DBObject value,
            Transaction transaction)
        {
            var points = new List<Point3d>();

            Line line = value as Line;
            if (line != null)
            {
                points.Add(line.StartPoint);
                points.Add(line.EndPoint);
                return points;
            }

            Polyline polyline = value as Polyline;
            if (polyline != null)
            {
                for (int index = 0; index < polyline.NumberOfVertices; index++)
                    points.Add(polyline.GetPoint3dAt(index));
                if (polyline.Closed && points.Count > 2)
                    points.Add(points[0]);
                return points;
            }

            Polyline3d polyline3d = value as Polyline3d;
            if (polyline3d != null)
            {
                foreach (ObjectId vertexId in polyline3d)
                {
                    PolylineVertex3d vertex = transaction.GetObject(
                        vertexId,
                        OpenMode.ForRead,
                        false) as PolylineVertex3d;
                    if (vertex != null)
                        points.Add(vertex.Position);
                }
                if (polyline3d.Closed && points.Count > 2)
                    points.Add(points[0]);
                return points;
            }

            Polyline2d polyline2d = value as Polyline2d;
            if (polyline2d != null)
            {
                foreach (ObjectId vertexId in polyline2d)
                {
                    Vertex2d vertex = transaction.GetObject(
                        vertexId,
                        OpenMode.ForRead,
                        false) as Vertex2d;
                    if (vertex != null)
                        points.Add(vertex.Position);
                }
                if (polyline2d.Closed && points.Count > 2)
                    points.Add(points[0]);
                return points;
            }

            CivilFeatureLine featureLine = value as CivilFeatureLine;
            if (featureLine != null)
            {
                Point3dCollection featurePoints = featureLine.GetPoints(
                    Autodesk.Civil.FeatureLinePointType.PIPoint);
                foreach (Point3d point in featurePoints)
                    points.Add(point);
                if (featureLine.Closed && points.Count > 2) points.Add(points[0]);
                return points;
            }

            return points;
        }

        private static void RemoveConsecutiveDuplicates(
            IList<Point3d> points)
        {
            for (int index = points.Count - 1; index > 0; index--)
            {
                if (points[index].DistanceTo(points[index - 1]) <= PointTolerance)
                    points.RemoveAt(index);
            }
        }

        private static List<PartsListChoice> ReadPartsLists(
            Database database,
            CivilDocument civilDocument)
        {
            var result = new List<PartsListChoice>();
            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                Autodesk.Civil.DatabaseServices.Styles.PartsListCollection lists =
                    civilDocument.Styles.PartsListSet;
                for (int index = 0; index < lists.Count; index++)
                {
                    ObjectId id = lists[index];
                    PartsList list = transaction.GetObject(
                        id,
                        OpenMode.ForRead,
                        false) as PartsList;
                    if (list == null || string.IsNullOrWhiteSpace(list.Name))
                        continue;
                    result.Add(new PartsListChoice
                    {
                        ObjectId = id,
                        Name = list.Name
                    });
                }
            }
            return result
                .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private static List<PartChoice> ReadPartChoices(
            Database database,
            ObjectId partsListId,
            DomainType domain,
            bool skipNullStructure)
        {
            var result = new List<PartChoice>();
            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                PartsList list = transaction.GetObject(
                    partsListId,
                    OpenMode.ForRead,
                    false) as PartsList;
                if (list == null) return result;

                ObjectIdCollection familyIds =
                    list.GetPartFamilyIdsByDomain(domain);
                foreach (ObjectId familyId in familyIds)
                {
                    PartFamily family = transaction.GetObject(
                        familyId,
                        OpenMode.ForRead,
                        false) as PartFamily;
                    if (family == null) continue;
                    if (skipNullStructure &&
                        family.Name != null &&
                        family.Name.IndexOf(
                            "null",
                            StringComparison.OrdinalIgnoreCase) >= 0)
                        continue;

                    for (int sizeIndex = 0;
                        sizeIndex < family.PartSizeCount;
                        sizeIndex++)
                    {
                        ObjectId sizeId = family[sizeIndex];
                        PartSize size = transaction.GetObject(
                            sizeId,
                            OpenMode.ForRead,
                            false) as PartSize;
                        if (size == null) continue;
                        string sizeName = string.IsNullOrWhiteSpace(size.Name)
                            ? "Size " + (sizeIndex + 1).ToString(CultureInfo.InvariantCulture)
                            : size.Name;
                        string familyName = string.IsNullOrWhiteSpace(family.Name)
                            ? "Part Family"
                            : family.Name;
                        result.Add(new PartChoice
                        {
                            FamilyId = familyId,
                            SizeId = sizeId,
                            FamilyName = familyName,
                            SizeName = sizeName,
                            Label = familyName + " | " + sizeName
                        });
                    }
                }
            }
            return result
                .OrderBy(item => item.Label, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private static bool IsWaterName(string name)
        {
            string value = (name ?? string.Empty).ToLowerInvariant();
            return (value.Contains("water") || value.Contains("potable")) &&
                !value.Contains("storm") && !value.Contains("waste") && !value.Contains("sewer") && !value.Contains("sanitary");
        }

        private static List<PartsListChoice> ReadWaterStyles(Database database, IEnumerable<ObjectId> ids)
        {
            var result = new List<PartsListChoice>();
            using (Transaction tr = database.TransactionManager.StartTransaction())
                foreach (ObjectId id in ids)
                {
                    var style = tr.GetObject(id, OpenMode.ForRead, false) as Autodesk.Civil.DatabaseServices.Styles.StyleBase;
                    if (style != null && IsWaterName(style.Name))
                        result.Add(new PartsListChoice { ObjectId = id, Name = style.Name });
                }
            return result.OrderBy(s => s.Name).ToList();
        }

        private static string PreferredPartsList(
            IList<PartsListChoice> choices)
        {
            PartsListChoice preferred = choices.FirstOrDefault(
                item => item.Name.IndexOf(
                    "water",
                    StringComparison.OrdinalIgnoreCase) >= 0 ||
                        item.Name.IndexOf(
                            "potable",
                            StringComparison.OrdinalIgnoreCase) >= 0);
            return preferred == null ? choices[0].Name : preferred.Name;
        }

        private static string PreferredPart(
            IList<PartChoice> choices,
            IEnumerable<string> keywords)
        {
            foreach (string keyword in keywords)
            {
                PartChoice preferred = choices.FirstOrDefault(
                    item => item.Label.IndexOf(
                        keyword,
                        StringComparison.OrdinalIgnoreCase) >= 0);
                if (preferred != null)
                    return preferred.Label;
            }
            return choices[0].Label;
        }

        private static PartChoice FindPart(
            IEnumerable<PartChoice> choices,
            string label)
        {
            return choices.FirstOrDefault(
                item => string.Equals(
                    item.Label,
                    label,
                    StringComparison.OrdinalIgnoreCase));
        }

        private sealed class SourcePath
        {
            internal ObjectId SourceId;
            internal List<Point3d> Points = new List<Point3d>();
        }

        private sealed class PartsListChoice
        {
            internal ObjectId ObjectId;
            internal string Name;
        }

        private sealed class PartChoice
        {
            internal ObjectId StyleId;
            internal ObjectId FamilyId;
            internal ObjectId SizeId;
            internal string FamilyName;
            internal string SizeName;
            internal string Label;
        }
    }
}

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
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.DynamicIntersectionCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Creates a linked set of plan intersections from selected feature lines,
    /// corridors and AutoCAD curves. Source design objects are never edited.
    /// Generated markers, labels and the register can be explicitly refreshed,
    /// inspected or detached; the deferred manager coalesces drawing changes and
    /// refreshes linked sets only while AutoCAD is idle and quiescent.
    /// </summary>
    public sealed class DynamicIntersectionCommands
    {
        internal const string LinkRecordName = "CE_DYNAMIC_INTERSECTION_SET";
        internal const string GeneratedRecordName = "CE_DYNAMIC_INTERSECTION_GENERATED";
        private const string SettingsDictionary = "CE_TOOLS";
        private const string SettingsRecord = "DYNAMIC_INTERSECTION_SETTINGS";
        private const string SchemaVersion = "1";
        private const string DefaultLayer = "CE-DYNAMIC-INTERSECTIONS";
        private const double GeometryTolerance = 1e-9;

        [CommandMethod("CE_INTTOOLS", CommandFlags.Modal | CommandFlags.Redraw)]
        public void IntersectionTools()
        {
            Document document = ActiveDocument();
            if (document == null)
                return;

            var options = new PromptKeywordOptions(
                "\nDynamic intersection tools [Create/Refresh/Information/Detach/Settings/Monitor] <Create>: ")
            {
                AllowNone = true
            };
            foreach (string keyword in new[]
            {
                "Create", "Refresh", "Information", "Detach", "Settings", "Monitor"
            })
                options.Keywords.Add(keyword);

            PromptResult result = document.Editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel)
                return;

            string choice = result.Status == PromptStatus.OK
                ? result.StringResult
                : "Create";
            if (choice.Equals("Refresh", StringComparison.OrdinalIgnoreCase))
                Refresh();
            else if (choice.Equals("Information", StringComparison.OrdinalIgnoreCase))
                Information();
            else if (choice.Equals("Detach", StringComparison.OrdinalIgnoreCase))
                Detach();
            else if (choice.Equals("Settings", StringComparison.OrdinalIgnoreCase))
                Settings();
            else if (choice.Equals("Monitor", StringComparison.OrdinalIgnoreCase))
                Monitor();
            else
                Create();
        }

        [CommandMethod("CE_INTSETTINGS", CommandFlags.Modal)]
        public void Settings()
        {
            Document document = ActiveDocument();
            if (document == null)
                return;

            Editor editor = document.Editor;
            IntersectionSettings settings = IntersectionSettings.Read(document.Database);
            if (!PromptText(editor, "Output layer", settings.Layer, out settings.Layer))
                return;
            if (!PromptPositiveDouble(editor, "Marker radius", settings.MarkerRadius, out settings.MarkerRadius))
                return;
            if (!PromptPositiveDouble(editor, "Label height", settings.LabelHeight, out settings.LabelHeight))
                return;
            if (!PromptPositiveDouble(editor, "XY intersection tolerance", settings.XyTolerance, out settings.XyTolerance))
                return;
            if (!PromptNonNegativeDouble(editor, "Elevation warning difference", settings.ElevationWarning, out settings.ElevationWarning))
                return;
            if (!PromptPositiveDouble(editor, "Maximum curve sampling segment", settings.CurveSampleLength, out settings.CurveSampleLength))
                return;
            if (!PromptPositiveInteger(editor, "Maximum generated intersections", settings.MaximumIntersections, out settings.MaximumIntersections))
                return;
            if (!PromptText(editor, "Corridor feature-code filter (blank = all)", settings.CorridorCodeFilter, out settings.CorridorCodeFilter))
                return;

            settings.Write(document.Database);
            editor.WriteMessage("\nCE_INTSETTINGS saved in the current DWG.");
        }

        [CommandMethod("CE_INTCREATE", CommandFlags.Modal | CommandFlags.Redraw | CommandFlags.UsePickSet)]
        public void Create()
        {
            Document document = ActiveDocument();
            if (document == null)
                return;

            PromptSelectionResult selection = document.Editor.SelectImplied();
            if (selection.Status != PromptStatus.OK ||
                selection.Value == null ||
                selection.Value.Count < 2)
            {
                selection = document.Editor.GetSelection(
                    new PromptSelectionOptions
                    {
                        MessageForAdding =
                            "\nSelect at least two feature lines, corridors or AutoCAD curves: ",
                        AllowDuplicates = false,
                        RejectObjectsFromNonCurrentSpace = true
                    });
            }
            if (selection.Status != PromptStatus.OK ||
                selection.Value == null ||
                selection.Value.Count < 2)
            {
                document.Editor.WriteMessage(
                    "\nCE_INTCREATE cancelled. Select at least two source design objects.");
                return;
            }

            PromptPointResult insertion = document.Editor.GetPoint(
                "\nPick the insertion point for the linked intersection register: ");
            if (insertion.Status != PromptStatus.OK)
                return;
            Point3d insertionPoint = insertion.Value.TransformBy(
                document.Editor.CurrentUserCoordinateSystem);

            IntersectionSettings settings = IntersectionSettings.Read(document.Database);
            List<SourceRecord> sources;
            ExtractionResult extraction;
            try
            {
                sources = BuildSources(
                    document.Database,
                    selection.Value.GetObjectIds(),
                    settings);
                if (sources.Count < 2)
                    throw new InvalidOperationException(
                        "Fewer than two selected objects exposed usable design paths.");
                extraction = ExtractIntersections(sources, settings);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage("\nCE_INTCREATE cancelled. " + exception.Message);
                return;
            }

            WritePreview(document.Editor, sources, extraction, settings);
            if (!Confirm(document.Editor, "Create and link this dynamic intersection set"))
            {
                document.Editor.WriteMessage(
                    "\nCE_INTCREATE cancelled. No drawing objects were created.");
                return;
            }

            try
            {
                DynamicIntersectionUpdateManager.BeginInternalUpdate();
                ObjectId anchorId = GenerateNewSet(
                    document.Database,
                    insertionPoint,
                    sources,
                    extraction,
                    settings);
                DynamicIntersectionUpdateManager.RegisterLinkedSet(document, anchorId);
                document.Editor.WriteMessage(
                    "\nCE_INTCREATE complete. Sources={0}; paths={1}; intersections={2}. " +
                    "Source/design changes are refreshed while CE Tools is loaded and the editor is idle.",
                    sources.Count,
                    sources.Sum(source => source.Paths.Count),
                    extraction.Intersections.Count);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_INTCREATE failed. No linked set was committed. " + exception.Message);
            }
            finally
            {
                DynamicIntersectionUpdateManager.EndInternalUpdate();
            }
        }

        [CommandMethod("CE_INTREFRESH", CommandFlags.Modal | CommandFlags.Redraw)]
        public void Refresh()
        {
            Document document = ActiveDocument();
            if (document == null)
                return;

            ObjectId anchorId;
            if (!PromptLinkedAnchor(document.Editor, document.Database, out anchorId))
                return;
            RefreshLinkedSet(document, anchorId, true, false);
        }

        [CommandMethod("CE_INTINFO", CommandFlags.Modal)]
        public void Information()
        {
            Document document = ActiveDocument();
            if (document == null)
                return;

            ObjectId anchorId;
            if (!PromptLinkedAnchor(document.Editor, document.Database, out anchorId))
                return;

            IntersectionLink link;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                Entity anchor = transaction.GetObject(
                    anchorId,
                    OpenMode.ForRead,
                    false) as Entity;
                link = ReadLink(anchor, transaction);
            }

            var rows = new List<IList<string>>();
            foreach (SourceLink source in link.Sources)
            {
                ObjectId sourceId;
                bool live = TryResolveHandle(document.Database, source.Handle, out sourceId);
                string type = live ? ReadObjectType(document.Database, sourceId) : "Missing";
                rows.Add(new List<string>
                {
                    source.Handle,
                    source.Name,
                    type,
                    live ? "Live" : "Missing"
                });
            }
            int liveGenerated = link.GeneratedHandles.Count(handle =>
            {
                ObjectId id;
                return TryResolveHandle(document.Database, handle, out id);
            });

            string note =
                "Schema=" + link.Schema +
                " | set=" + link.SetName +
                " | sources=" + link.Sources.Count +
                " | generated handles=" + link.GeneratedHandles.Count +
                " | live generated=" + liveGenerated +
                " | automatic monitor=" +
                (DynamicIntersectionUpdateManager.IsInitialized ? "Active" : "Inactive") +
                ". Refresh is also available explicitly through CE_INTREFRESH.";
            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Dynamic Intersection Information",
                note,
                new List<string> { "Source Handle", "Stored Name", "Current Type", "State" },
                rows,
                "CE Dynamic Intersection Sources - " + link.SetName);
        }

        [CommandMethod("CE_INTDETACH", CommandFlags.Modal | CommandFlags.Redraw)]
        public void Detach()
        {
            Document document = ActiveDocument();
            if (document == null)
                return;

            ObjectId anchorId;
            if (!PromptLinkedAnchor(document.Editor, document.Database, out anchorId))
                return;

            IntersectionLink link;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                Entity anchor = transaction.GetObject(anchorId, OpenMode.ForRead, false) as Entity;
                link = ReadLink(anchor, transaction);
            }

            var options = new PromptKeywordOptions(
                "\nDetach generated objects [Keep/Delete] <Keep>: ")
            {
                AllowNone = true
            };
            options.Keywords.Add("Keep");
            options.Keywords.Add("Delete");
            PromptResult result = document.Editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel)
                return;
            bool deleteGenerated = result.Status == PromptStatus.OK &&
                result.StringResult.Equals("Delete", StringComparison.OrdinalIgnoreCase);

            try
            {
                DynamicIntersectionUpdateManager.BeginInternalUpdate();
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    foreach (string handle in link.GeneratedHandles)
                    {
                        ObjectId id;
                        if (!TryResolveHandle(document.Database, handle, out id))
                            continue;
                        Entity generated = transaction.GetObject(id, OpenMode.ForWrite, false) as Entity;
                        if (generated == null)
                            continue;
                        if (deleteGenerated)
                            generated.Erase();
                        else
                            RemoveRecord(generated, transaction, GeneratedRecordName);
                    }
                    Entity anchor = transaction.GetObject(anchorId, OpenMode.ForWrite, false) as Entity;
                    if (anchor != null)
                        anchor.Erase();
                    transaction.Commit();
                }
                DynamicIntersectionUpdateManager.UnregisterLinkedSet(document, anchorId);
                document.Editor.WriteMessage(deleteGenerated
                    ? "\nCE_INTDETACH complete. Link anchor and generated intersection objects were removed."
                    : "\nCE_INTDETACH complete. Link anchor was removed and generated geometry was kept as ordinary drawing objects.");
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage("\nCE_INTDETACH failed. " + exception.Message);
            }
            finally
            {
                DynamicIntersectionUpdateManager.EndInternalUpdate();
            }
        }

        [CommandMethod("CE_INTMONITOR", CommandFlags.Modal)]
        public void Monitor()
        {
            Document document = ActiveDocument();
            if (document == null)
                return;
            document.Editor.WriteMessage(
                "\nCE Dynamic Intersection Monitor" +
                "\n  Initialised: " + DynamicIntersectionUpdateManager.IsInitialized +
                "\n  Linked sets in current space: " + FindLinkedAnchors(document.Database).Count +
                "\n  Pending refresh: " + DynamicIntersectionUpdateManager.HasPendingRefresh(document) +
                "\n  Updates are coalesced and run on Application.Idle only when the active editor is quiescent.");
        }

        internal static bool RefreshLinkedSet(
            Document document,
            ObjectId anchorId,
            bool reportResult,
            bool automatic)
        {
            if (document == null || anchorId.IsNull || anchorId.IsErased)
                return false;

            IntersectionLink link;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                Entity anchor = transaction.GetObject(anchorId, OpenMode.ForRead, false) as Entity;
                if (anchor == null || !HasRecord(anchor, transaction, LinkRecordName))
                    return false;
                link = ReadLink(anchor, transaction);
            }

            IntersectionSettings settings = IntersectionSettings.Read(document.Database);
            List<ObjectId> liveSourceIds = new List<ObjectId>();
            List<string> missing = new List<string>();
            foreach (SourceLink source in link.Sources)
            {
                ObjectId id;
                if (TryResolveHandle(document.Database, source.Handle, out id))
                    liveSourceIds.Add(id);
                else
                    missing.Add(source.Handle);
            }
            if (missing.Count > 0 || liveSourceIds.Count < 2)
            {
                if (reportResult || automatic)
                {
                    document.Editor.WriteMessage(
                        "\nCE dynamic-intersection refresh deferred. Missing source handle(s): {0}. Existing output was kept.",
                        string.Join(", ", missing));
                }
                return false;
            }

            List<SourceRecord> sources;
            ExtractionResult extraction;
            try
            {
                sources = BuildSources(document.Database, liveSourceIds, settings);
                if (sources.Count < 2)
                    throw new InvalidOperationException(
                        "Fewer than two linked sources expose usable design paths.");
                extraction = ExtractIntersections(sources, settings);
            }
            catch (System.Exception exception)
            {
                if (reportResult || automatic)
                    document.Editor.WriteMessage(
                        "\nCE dynamic-intersection refresh deferred. Existing output was kept. " +
                        exception.Message);
                return false;
            }

            try
            {
                DynamicIntersectionUpdateManager.BeginInternalUpdate();
                RegenerateSet(
                    document.Database,
                    anchorId,
                    link,
                    sources,
                    extraction,
                    settings);
                if (reportResult)
                {
                    document.Editor.WriteMessage(
                        "\nCE_INTREFRESH complete. Sources={0}; paths={1}; intersections={2}.",
                        sources.Count,
                        sources.Sum(source => source.Paths.Count),
                        extraction.Intersections.Count);
                }
                return true;
            }
            catch (System.Exception exception)
            {
                if (reportResult || automatic)
                    document.Editor.WriteMessage(
                        "\nCE dynamic-intersection refresh failed. Existing output may require explicit review. " +
                        exception.Message);
                return false;
            }
            finally
            {
                DynamicIntersectionUpdateManager.EndInternalUpdate();
            }
        }

        private static ObjectId GenerateNewSet(
            Database database,
            Point3d insertionPoint,
            IReadOnlyList<SourceRecord> sources,
            ExtractionResult extraction,
            IntersectionSettings settings)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                ObjectId layerId = GetOrCreateLayer(database, transaction, settings.Layer);
                BlockTableRecord currentSpace = transaction.GetObject(
                    database.CurrentSpaceId,
                    OpenMode.ForWrite,
                    false) as BlockTableRecord;
                if (currentSpace == null)
                    throw new InvalidOperationException("The current drawing space could not be opened.");

                var anchor = new DBPoint(insertionPoint);
                anchor.SetDatabaseDefaults(database);
                anchor.LayerId = layerId;
                currentSpace.AppendEntity(anchor);
                transaction.AddNewlyCreatedDBObject(anchor, true);
                anchor.CreateExtensionDictionary();

                string setName = "INT-" + anchor.Handle.ToString();
                List<string> generatedHandles = GenerateOutput(
                    database,
                    currentSpace,
                    anchor,
                    insertionPoint,
                    setName,
                    sources,
                    extraction,
                    settings,
                    layerId,
                    transaction);
                WriteLink(
                    anchor,
                    transaction,
                    new IntersectionLink(
                        SchemaVersion,
                        setName,
                        insertionPoint,
                        sources.Select(source => new SourceLink(
                            source.SourceHandle,
                            source.SourceName)),
                        generatedHandles));
                transaction.Commit();
                return anchor.ObjectId;
            }
        }

        private static void RegenerateSet(
            Database database,
            ObjectId anchorId,
            IntersectionLink previous,
            IReadOnlyList<SourceRecord> sources,
            ExtractionResult extraction,
            IntersectionSettings settings)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                Entity anchor = transaction.GetObject(anchorId, OpenMode.ForWrite, false) as Entity;
                if (anchor == null)
                    throw new InvalidOperationException("The linked intersection anchor no longer exists.");

                foreach (string handle in previous.GeneratedHandles)
                {
                    ObjectId id;
                    if (!TryResolveHandle(database, handle, out id))
                        continue;
                    Entity generated = transaction.GetObject(id, OpenMode.ForWrite, false) as Entity;
                    if (generated != null && HasRecord(generated, transaction, GeneratedRecordName))
                        generated.Erase();
                }

                ObjectId layerId = GetOrCreateLayer(database, transaction, settings.Layer);
                BlockTableRecord currentSpace = transaction.GetObject(
                    database.CurrentSpaceId,
                    OpenMode.ForWrite,
                    false) as BlockTableRecord;
                if (currentSpace == null)
                    throw new InvalidOperationException("The current drawing space could not be opened.");

                List<string> generatedHandles = GenerateOutput(
                    database,
                    currentSpace,
                    anchor,
                    previous.InsertionPoint,
                    previous.SetName,
                    sources,
                    extraction,
                    settings,
                    layerId,
                    transaction);
                WriteLink(
                    anchor,
                    transaction,
                    new IntersectionLink(
                        previous.Schema,
                        previous.SetName,
                        previous.InsertionPoint,
                        sources.Select(source => new SourceLink(
                            source.SourceHandle,
                            source.SourceName)),
                        generatedHandles));
                transaction.Commit();
            }
        }

        private static List<string> GenerateOutput(
            Database database,
            BlockTableRecord space,
            Entity anchor,
            Point3d insertionPoint,
            string setName,
            IReadOnlyList<SourceRecord> sources,
            ExtractionResult extraction,
            IntersectionSettings settings,
            ObjectId layerId,
            Transaction transaction)
        {
            var generated = new List<string>();
            string anchorHandle = anchor.Handle.ToString();

            var title = new MText();
            title.SetDatabaseDefaults(database);
            title.LayerId = layerId;
            title.Location = insertionPoint + new Vector3d(
                settings.MarkerRadius * 1.5,
                settings.MarkerRadius * 1.5,
                0.0);
            title.TextHeight = settings.LabelHeight * 1.2;
            title.Attachment = AttachmentPoint.BottomLeft;
            title.Contents =
                "CE DYNAMIC INTERSECTION SET " + setName +
                "\nSources: " + sources.Count +
                " | Intersections: " + extraction.Intersections.Count;
            title.BackgroundFill = true;
            title.UseBackgroundColor = true;
            AppendGenerated(space, title, transaction, anchorHandle, generated);

            for (int index = 0; index < extraction.Intersections.Count; index++)
            {
                IntersectionHit hit = extraction.Intersections[index];
                Point3d markerPoint = new Point3d(
                    hit.X,
                    hit.Y,
                    (hit.ElevationA + hit.ElevationB) * 0.5);

                var circle = new Circle(markerPoint, Vector3d.ZAxis, settings.MarkerRadius);
                circle.SetDatabaseDefaults(database);
                circle.LayerId = layerId;
                AppendGenerated(space, circle, transaction, anchorHandle, generated);

                var diagonalOne = new Line(
                    markerPoint + new Vector3d(-settings.MarkerRadius, -settings.MarkerRadius, 0.0),
                    markerPoint + new Vector3d(settings.MarkerRadius, settings.MarkerRadius, 0.0));
                diagonalOne.SetDatabaseDefaults(database);
                diagonalOne.LayerId = layerId;
                AppendGenerated(space, diagonalOne, transaction, anchorHandle, generated);

                var diagonalTwo = new Line(
                    markerPoint + new Vector3d(-settings.MarkerRadius, settings.MarkerRadius, 0.0),
                    markerPoint + new Vector3d(settings.MarkerRadius, -settings.MarkerRadius, 0.0));
                diagonalTwo.SetDatabaseDefaults(database);
                diagonalTwo.LayerId = layerId;
                AppendGenerated(space, diagonalTwo, transaction, anchorHandle, generated);

                var label = new MText();
                label.SetDatabaseDefaults(database);
                label.LayerId = layerId;
                label.Location = markerPoint + new Vector3d(
                    settings.MarkerRadius * 1.6,
                    settings.MarkerRadius * (1.6 + (index % 3) * 1.1),
                    0.0);
                label.TextHeight = settings.LabelHeight;
                label.Attachment = AttachmentPoint.BottomLeft;
                label.Contents =
                    "INT-" + (index + 1).ToString("000", CultureInfo.InvariantCulture) +
                    "\n" + hit.SourceA + " / " + hit.PathA +
                    "\n" + hit.SourceB + " / " + hit.PathB +
                    "\nZA=" + hit.ElevationA.ToString("0.###", CultureInfo.InvariantCulture) +
                    " ZB=" + hit.ElevationB.ToString("0.###", CultureInfo.InvariantCulture) +
                    " Δ=" + hit.ElevationDifference.ToString("0.###", CultureInfo.InvariantCulture) +
                    (hit.IsElevationWarning ? " [CHECK]" : string.Empty);
                label.BackgroundFill = true;
                label.UseBackgroundColor = true;
                AppendGenerated(space, label, transaction, anchorHandle, generated);
            }

            Table table = BuildRegister(
                database,
                insertionPoint + new Vector3d(0.0, -settings.LabelHeight * 4.0, 0.0),
                setName,
                extraction.Intersections,
                settings);
            table.LayerId = layerId;
            AppendGenerated(space, table, transaction, anchorHandle, generated);
            return generated;
        }

        private static Table BuildRegister(
            Database database,
            Point3d position,
            string setName,
            IReadOnlyList<IntersectionHit> hits,
            IntersectionSettings settings)
        {
            const int columns = 10;
            var table = new Table
            {
                TableStyle = database.Tablestyle,
                Position = position
            };
            table.SetSize(Math.Max(1, hits.Count) + 2, columns);
            table.SetRowHeight(settings.LabelHeight * 2.2);
            double[] widths =
            {
                9, 17, 17, 17, 17, 13, 13, 13, 13, 13
            };
            for (int index = 0; index < widths.Length; index++)
                table.Columns[index].Width = settings.LabelHeight * widths[index];

            table.MergeCells(CellRange.Create(table, 0, 0, 0, columns - 1));
            table.Cells[0, 0].TextString = "CE Dynamic Intersection Register - " + setName;
            table.Cells[0, 0].Alignment = CellAlignment.MiddleCenter;
            table.Cells[0, 0].TextHeight = settings.LabelHeight * 1.1;
            string[] headings =
            {
                "ID", "Source A", "Path A", "Source B", "Path B",
                "X", "Y", "Z A", "Z B", "Delta Z"
            };
            for (int column = 0; column < headings.Length; column++)
            {
                table.Cells[1, column].TextString = headings[column];
                table.Cells[1, column].Alignment = CellAlignment.MiddleCenter;
                table.Cells[1, column].TextHeight = settings.LabelHeight;
            }

            if (hits.Count == 0)
            {
                table.Cells[2, 0].TextString = "No plan intersections found";
                table.MergeCells(CellRange.Create(table, 2, 0, 2, columns - 1));
                table.Cells[2, 0].Alignment = CellAlignment.MiddleCenter;
                table.Cells[2, 0].TextHeight = settings.LabelHeight;
            }
            else
            {
                for (int index = 0; index < hits.Count; index++)
                {
                    IntersectionHit hit = hits[index];
                    string[] values =
                    {
                        "INT-" + (index + 1).ToString("000", CultureInfo.InvariantCulture),
                        hit.SourceA,
                        hit.PathA,
                        hit.SourceB,
                        hit.PathB,
                        hit.X.ToString("0.###", CultureInfo.InvariantCulture),
                        hit.Y.ToString("0.###", CultureInfo.InvariantCulture),
                        hit.ElevationA.ToString("0.###", CultureInfo.InvariantCulture),
                        hit.ElevationB.ToString("0.###", CultureInfo.InvariantCulture),
                        hit.ElevationDifference.ToString("0.###", CultureInfo.InvariantCulture) +
                        (hit.IsElevationWarning ? " CHECK" : string.Empty)
                    };
                    for (int column = 0; column < values.Length; column++)
                    {
                        table.Cells[index + 2, column].TextString = values[column];
                        table.Cells[index + 2, column].Alignment = CellAlignment.MiddleLeft;
                        table.Cells[index + 2, column].TextHeight = settings.LabelHeight;
                    }
                }
            }
            table.GenerateLayout();
            return table;
        }

        private static void AppendGenerated(
            BlockTableRecord space,
            Entity entity,
            Transaction transaction,
            string anchorHandle,
            ICollection<string> generated)
        {
            space.AppendEntity(entity);
            transaction.AddNewlyCreatedDBObject(entity, true);
            entity.CreateExtensionDictionary();
            WriteGeneratedOwner(entity, transaction, anchorHandle);
            generated.Add(entity.Handle.ToString());
        }

        private static List<SourceRecord> BuildSources(
            Database database,
            IEnumerable<ObjectId> sourceIds,
            IntersectionSettings settings)
        {
            var result = new List<SourceRecord>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in sourceIds.Distinct())
                {
                    Entity source = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                    if (source == null)
                        continue;
                    List<DesignPath> paths = ExtractPaths(source, settings);
                    if (paths.Count == 0)
                        continue;
                    result.Add(new SourceRecord(
                        id,
                        id.Handle.ToString(),
                        ResolveSourceName(source),
                        source.GetType().Name,
                        paths));
                }
            }
            return result;
        }

        private static List<DesignPath> ExtractPaths(
            Entity source,
            IntersectionSettings settings)
        {
            var paths = new List<DesignPath>();
            Curve curve = source as Curve;
            if (curve != null)
            {
                List<Point3d> points = SampleCurve(curve, settings.CurveSampleLength);
                if (points.Count >= 2)
                    paths.Add(new DesignPath(ResolveSourceName(source), points));
                return paths;
            }

            string typeName = source.GetType().FullName ?? source.GetType().Name;
            if (typeName.IndexOf("FeatureLine", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                List<Point3d> points = ReadFeatureLinePoints(source);
                if (points.Count >= 2)
                    paths.Add(new DesignPath(ResolveSourceName(source), points));
                return paths;
            }

            if (typeName.IndexOf("Corridor", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                CollectCorridorPaths(
                    source,
                    paths,
                    settings,
                    new HashSet<object>(ReferenceEqualityComparer.Instance),
                    0,
                    ResolveSourceName(source));
                return paths
                    .Where(path => path.Points.Count >= 2)
                    .GroupBy(path => BuildPathKey(path.Points))
                    .Select(group => group.First())
                    .Take(1000)
                    .ToList();
            }

            return paths;
        }

        private static void CollectCorridorPaths(
            object owner,
            ICollection<DesignPath> paths,
            IntersectionSettings settings,
            ISet<object> visited,
            int depth,
            string prefix)
        {
            if (owner == null || depth > 5 || paths.Count >= 1000 || !visited.Add(owner))
                return;

            Type type = owner.GetType();
            string typeName = type.FullName ?? type.Name;
            if (typeName.IndexOf("FeatureLine", StringComparison.OrdinalIgnoreCase) >= 0 &&
                typeName.IndexOf("Collection", StringComparison.OrdinalIgnoreCase) < 0)
            {
                string code = Convert.ToString(
                    ReadProperty(owner, "CodeName") ?? ReadProperty(owner, "Code"),
                    CultureInfo.InvariantCulture);
                if (string.IsNullOrWhiteSpace(settings.CorridorCodeFilter) ||
                    (code ?? string.Empty).IndexOf(
                        settings.CorridorCodeFilter,
                        StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    List<Point3d> points = ReadFeatureLinePoints(owner);
                    if (points.Count >= 2)
                    {
                        string name = string.IsNullOrWhiteSpace(code)
                            ? prefix + " / " + type.Name
                            : prefix + " / " + code;
                        paths.Add(new DesignPath(name, points));
                    }
                }
            }

            foreach (PropertyInfo property in type.GetProperties(
                BindingFlags.Public | BindingFlags.Instance))
            {
                if (!property.CanRead || property.GetIndexParameters().Length != 0)
                    continue;
                string propertyName = property.Name;
                if (!ContainsAny(
                        propertyName,
                        "Baseline", "FeatureLine", "Code", "Region"))
                    continue;
                object value;
                try { value = property.GetValue(owner, null); }
                catch { continue; }
                TraverseCorridorValue(
                    value,
                    paths,
                    settings,
                    visited,
                    depth + 1,
                    prefix + " / " + propertyName);
            }

            foreach (MethodInfo method in type.GetMethods(
                BindingFlags.Public | BindingFlags.Instance))
            {
                if (method.GetParameters().Length != 0 ||
                    !ContainsAny(method.Name, "GetFeatureLines", "GetBaselineFeatureLines"))
                    continue;
                object value;
                try { value = method.Invoke(owner, null); }
                catch { continue; }
                TraverseCorridorValue(
                    value,
                    paths,
                    settings,
                    visited,
                    depth + 1,
                    prefix + " / " + method.Name);
            }
        }

        private static void TraverseCorridorValue(
            object value,
            ICollection<DesignPath> paths,
            IntersectionSettings settings,
            ISet<object> visited,
            int depth,
            string prefix)
        {
            if (value == null)
                return;
            string typeName = value.GetType().FullName ?? value.GetType().Name;
            if (value is string || value.GetType().IsPrimitive || value is ObjectId)
                return;

            IEnumerable enumerable = value as IEnumerable;
            if (enumerable != null &&
                typeName.IndexOf("Point", StringComparison.OrdinalIgnoreCase) < 0)
            {
                int count = 0;
                foreach (object item in enumerable)
                {
                    CollectCorridorPaths(
                        item,
                        paths,
                        settings,
                        visited,
                        depth,
                        prefix + "[" + count.ToString(CultureInfo.InvariantCulture) + "]");
                    if (++count >= 1000 || paths.Count >= 1000)
                        break;
                }
                return;
            }
            CollectCorridorPaths(value, paths, settings, visited, depth, prefix);
        }

        private static List<Point3d> ReadFeatureLinePoints(object featureLine)
        {
            var result = new List<Point3d>();
            Type type = featureLine.GetType();

            foreach (MethodInfo method in type.GetMethods(
                BindingFlags.Public | BindingFlags.Instance)
                .Where(item => item.Name == "GetPoints"))
            {
                ParameterInfo[] parameters = method.GetParameters();
                object[] arguments;
                if (parameters.Length == 0)
                    arguments = new object[0];
                else if (parameters.Length == 1 && parameters[0].ParameterType.IsEnum)
                {
                    object enumValue;
                    try
                    {
                        enumValue = Enum.Parse(
                            parameters[0].ParameterType,
                            "AllPoints",
                            true);
                    }
                    catch
                    {
                        Array values = Enum.GetValues(parameters[0].ParameterType);
                        if (values.Length == 0)
                            continue;
                        enumValue = values.GetValue(values.Length - 1);
                    }
                    arguments = new[] { enumValue };
                }
                else
                    continue;

                object value;
                try { value = method.Invoke(featureLine, arguments); }
                catch { continue; }
                AddPointsFromValue(result, value);
                if (result.Count >= 2)
                    return result;
            }

            object points = ReadProperty(featureLine, "Points") ??
                            ReadProperty(featureLine, "Vertices");
            AddPointsFromValue(result, points);
            return result;
        }

        private static void AddPointsFromValue(
            ICollection<Point3d> points,
            object value)
        {
            IEnumerable enumerable = value as IEnumerable;
            if (enumerable == null)
                return;
            foreach (object item in enumerable)
            {
                Point3d point;
                if (TryReadPoint(item, out point))
                    AddDistinct(points, point);
            }
        }

        private static List<Point3d> SampleCurve(Curve curve, double maximumSegment)
        {
            var points = new List<Point3d>();
            Polyline polyline = curve as Polyline;
            if (polyline != null)
            {
                for (int index = 0; index < polyline.NumberOfVertices; index++)
                    AddDistinct(points, polyline.GetPoint3dAt(index));
                if (polyline.Closed && points.Count > 0)
                    AddDistinct(points, points[0]);
                return points;
            }

            Polyline3d polyline3d = curve as Polyline3d;
            if (polyline3d != null)
            {
                using (Transaction transaction = curve.Database.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId vertexId in polyline3d)
                    {
                        PolylineVertex3d vertex = transaction.GetObject(
                            vertexId,
                            OpenMode.ForRead,
                            false) as PolylineVertex3d;
                        if (vertex != null)
                            AddDistinct(points, vertex.Position);
                    }
                }
                return points;
            }

            double start = curve.StartParam;
            double end = curve.EndParam;
            double length;
            try
            {
                length = Math.Abs(
                    curve.GetDistanceAtParameter(end) -
                    curve.GetDistanceAtParameter(start));
            }
            catch
            {
                length = curve.StartPoint.DistanceTo(curve.EndPoint);
            }
            int segments = Math.Max(1, Math.Min(10000,
                (int)Math.Ceiling(Math.Max(length, maximumSegment) / maximumSegment)));
            for (int index = 0; index <= segments; index++)
            {
                double parameter = start + (end - start) * index / segments;
                try { AddDistinct(points, curve.GetPointAtParameter(parameter)); }
                catch { }
            }
            return points;
        }

        private static ExtractionResult ExtractIntersections(
            IReadOnlyList<SourceRecord> sources,
            IntersectionSettings settings)
        {
            var hits = new List<IntersectionHit>();
            int testedSegments = 0;
            for (int sourceAIndex = 0; sourceAIndex < sources.Count; sourceAIndex++)
            {
                SourceRecord sourceA = sources[sourceAIndex];
                for (int sourceBIndex = sourceAIndex + 1; sourceBIndex < sources.Count; sourceBIndex++)
                {
                    SourceRecord sourceB = sources[sourceBIndex];
                    foreach (DesignPath pathA in sourceA.Paths)
                    {
                        foreach (DesignPath pathB in sourceB.Paths)
                        {
                            for (int a = 1; a < pathA.Points.Count; a++)
                            {
                                Point3d a1 = pathA.Points[a - 1];
                                Point3d a2 = pathA.Points[a];
                                for (int b = 1; b < pathB.Points.Count; b++)
                                {
                                    testedSegments++;
                                    Point3d b1 = pathB.Points[b - 1];
                                    Point3d b2 = pathB.Points[b];
                                    double t;
                                    double u;
                                    Point2d intersection;
                                    if (!TryIntersectSegments(
                                            a1,
                                            a2,
                                            b1,
                                            b2,
                                            settings.XyTolerance,
                                            out t,
                                            out u,
                                            out intersection))
                                        continue;
                                    double elevationA = a1.Z + (a2.Z - a1.Z) * t;
                                    double elevationB = b1.Z + (b2.Z - b1.Z) * u;
                                    double difference = Math.Abs(elevationA - elevationB);
                                    var hit = new IntersectionHit(
                                        sourceA.SourceName,
                                        pathA.Name,
                                        sourceB.SourceName,
                                        pathB.Name,
                                        intersection.X,
                                        intersection.Y,
                                        elevationA,
                                        elevationB,
                                        difference,
                                        difference > settings.ElevationWarning);
                                    if (!ContainsEquivalent(hits, hit, settings.XyTolerance))
                                        hits.Add(hit);
                                    if (hits.Count > settings.MaximumIntersections)
                                        throw new InvalidOperationException(
                                            "The result exceeds the configured maximum of " +
                                            settings.MaximumIntersections.ToString(CultureInfo.InvariantCulture) +
                                            " intersections. Refine the source selection or corridor code filter.");
                                }
                            }
                        }
                    }
                }
            }
            return new ExtractionResult(hits, testedSegments);
        }

        private static bool TryIntersectSegments(
            Point3d a1,
            Point3d a2,
            Point3d b1,
            Point3d b2,
            double tolerance,
            out double t,
            out double u,
            out Point2d point)
        {
            double rx = a2.X - a1.X;
            double ry = a2.Y - a1.Y;
            double sx = b2.X - b1.X;
            double sy = b2.Y - b1.Y;
            double denominator = Cross(rx, ry, sx, sy);
            t = u = 0.0;
            point = Point2d.Origin;
            if (Math.Abs(denominator) <= tolerance)
                return false;

            double qpx = b1.X - a1.X;
            double qpy = b1.Y - a1.Y;
            t = Cross(qpx, qpy, sx, sy) / denominator;
            u = Cross(qpx, qpy, rx, ry) / denominator;
            double parameterTolerance = Math.Max(GeometryTolerance, tolerance);
            if (t < -parameterTolerance || t > 1.0 + parameterTolerance ||
                u < -parameterTolerance || u > 1.0 + parameterTolerance)
                return false;
            t = Math.Max(0.0, Math.Min(1.0, t));
            u = Math.Max(0.0, Math.Min(1.0, u));
            point = new Point2d(a1.X + t * rx, a1.Y + t * ry);
            return true;
        }

        private static double Cross(double ax, double ay, double bx, double by)
        {
            return ax * by - ay * bx;
        }

        private static bool ContainsEquivalent(
            IEnumerable<IntersectionHit> hits,
            IntersectionHit candidate,
            double tolerance)
        {
            double squared = tolerance * tolerance;
            return hits.Any(hit =>
                ((hit.X - candidate.X) * (hit.X - candidate.X) +
                 (hit.Y - candidate.Y) * (hit.Y - candidate.Y)) <= squared &&
                EqualPair(hit, candidate));
        }

        private static bool EqualPair(IntersectionHit first, IntersectionHit second)
        {
            bool same =
                first.SourceA == second.SourceA && first.PathA == second.PathA &&
                first.SourceB == second.SourceB && first.PathB == second.PathB;
            bool reverse =
                first.SourceA == second.SourceB && first.PathA == second.PathB &&
                first.SourceB == second.SourceA && first.PathB == second.PathA;
            return same || reverse;
        }

        private static void WritePreview(
            Editor editor,
            IReadOnlyList<SourceRecord> sources,
            ExtractionResult extraction,
            IntersectionSettings settings)
        {
            editor.WriteMessage(
                "\nCE dynamic-intersection preview: sources={0}; paths={1}; segment pairs tested={2}; intersections={3}; elevation warnings={4}.",
                sources.Count,
                sources.Sum(source => source.Paths.Count),
                extraction.SegmentPairsTested,
                extraction.Intersections.Count,
                extraction.Intersections.Count(hit => hit.IsElevationWarning));
            foreach (SourceRecord source in sources)
                editor.WriteMessage(
                    "\n  {0}: type={1}; paths={2}; handle={3}",
                    source.SourceName,
                    source.SourceType,
                    source.Paths.Count,
                    source.SourceHandle);
            foreach (IntersectionHit hit in extraction.Intersections.Take(20))
                editor.WriteMessage(
                    "\n  X={0:0.###}; Y={1:0.###}; {2}/{3} vs {4}/{5}; ZA={6:0.###}; ZB={7:0.###}; delta={8:0.###}{9}",
                    hit.X,
                    hit.Y,
                    hit.SourceA,
                    hit.PathA,
                    hit.SourceB,
                    hit.PathB,
                    hit.ElevationA,
                    hit.ElevationB,
                    hit.ElevationDifference,
                    hit.IsElevationWarning ? " CHECK" : string.Empty);
            if (extraction.Intersections.Count > 20)
                editor.WriteMessage(
                    "\n  ... {0} additional intersections.",
                    extraction.Intersections.Count - 20);
            editor.WriteMessage(
                "\n  Sources will remain unchanged. Parallel/collinear overlaps are reported neither as single points nor as native Autodesk intersection objects.");
        }

        private static bool PromptLinkedAnchor(
            Editor editor,
            Database database,
            out ObjectId anchorId)
        {
            var options = new PromptEntityOptions(
                "\nSelect a CE dynamic intersection set anchor point: ");
            options.SetRejectMessage("\nSelect the linked DBPoint anchor created by CE_INTCREATE.");
            options.AddAllowedClass(typeof(DBPoint), false);
            PromptEntityResult result = editor.GetEntity(options);
            anchorId = result.Status == PromptStatus.OK ? result.ObjectId : ObjectId.Null;
            if (result.Status != PromptStatus.OK)
                return false;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                Entity anchor = transaction.GetObject(anchorId, OpenMode.ForRead, false) as Entity;
                if (!HasRecord(anchor, transaction, LinkRecordName))
                {
                    editor.WriteMessage(
                        "\nThe selected point is not a CE dynamic intersection anchor.");
                    return false;
                }
            }
            return true;
        }

        private static ObjectId GetOrCreateLayer(
            Database database,
            Transaction transaction,
            string requested)
        {
            string name = string.IsNullOrWhiteSpace(requested)
                ? DefaultLayer
                : requested.Trim();
            LayerTable layers = transaction.GetObject(
                database.LayerTableId,
                OpenMode.ForRead,
                false) as LayerTable;
            if (layers == null)
                throw new InvalidOperationException("The layer table could not be opened.");
            if (layers.Has(name))
            {
                ObjectId id = layers[name];
                LayerTableRecord existing = transaction.GetObject(
                    id,
                    OpenMode.ForRead,
                    false) as LayerTableRecord;
                if (existing != null && existing.IsLocked)
                    throw new InvalidOperationException("Layer '" + name + "' is locked.");
                return id;
            }
            layers.UpgradeOpen();
            var layer = new LayerTableRecord { Name = name };
            ObjectId layerId = layers.Add(layer);
            transaction.AddNewlyCreatedDBObject(layer, true);
            return layerId;
        }

        private static string ResolveSourceName(Entity source)
        {
            string name = Convert.ToString(ReadProperty(source, "Name"), CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(name))
                return name;
            return source.GetType().Name + " " + source.Handle;
        }

        private static string ReadObjectType(Database database, ObjectId id)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                DBObject item = transaction.GetObject(id, OpenMode.ForRead, false);
                return item == null ? "Missing" : item.GetType().Name;
            }
        }

        private static string BuildPathKey(IReadOnlyList<Point3d> points)
        {
            if (points.Count == 0)
                return string.Empty;
            Point3d first = points[0];
            Point3d last = points[points.Count - 1];
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0:0.###},{1:0.###},{2:0.###}|{3:0.###},{4:0.###},{5:0.###}|{6}",
                first.X,
                first.Y,
                first.Z,
                last.X,
                last.Y,
                last.Z,
                points.Count);
        }

        private static bool TryReadPoint(object value, out Point3d point)
        {
            if (value is Point3d)
            {
                point = (Point3d)value;
                return true;
            }
            foreach (string name in new[] { "Location", "Position", "Point" })
            {
                object property = ReadProperty(value, name);
                if (property is Point3d)
                {
                    point = (Point3d)property;
                    return true;
                }
            }
            point = Point3d.Origin;
            return false;
        }

        private static object ReadProperty(object owner, string propertyName)
        {
            if (owner == null)
                return null;
            PropertyInfo property = owner.GetType().GetProperty(
                propertyName,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (property == null || !property.CanRead)
                return null;
            try { return property.GetValue(owner, null); }
            catch { return null; }
        }

        private static bool ContainsAny(string source, params string[] values)
        {
            if (string.IsNullOrWhiteSpace(source))
                return false;
            return values.Any(value =>
                source.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static void AddDistinct(ICollection<Point3d> points, Point3d point)
        {
            if (points.Count == 0 || points.Last().DistanceTo(point) > GeometryTolerance)
                points.Add(point);
        }

        private static void WriteLink(
            Entity anchor,
            Transaction transaction,
            IntersectionLink link)
        {
            if (anchor.ExtensionDictionary.IsNull)
                anchor.CreateExtensionDictionary();
            DBDictionary dictionary = transaction.GetObject(
                anchor.ExtensionDictionary,
                OpenMode.ForWrite,
                false) as DBDictionary;
            if (dictionary == null)
                throw new InvalidOperationException("The link anchor dictionary could not be opened.");

            Xrecord record = OpenOrCreateRecord(dictionary, LinkRecordName, transaction);
            var values = new List<TypedValue>
            {
                new TypedValue((int)DxfCode.Text, "Schema=" + link.Schema),
                new TypedValue((int)DxfCode.Text, "SetName=" + link.SetName),
                new TypedValue((int)DxfCode.Text, "InsertionX=" + link.InsertionPoint.X.ToString("R", CultureInfo.InvariantCulture)),
                new TypedValue((int)DxfCode.Text, "InsertionY=" + link.InsertionPoint.Y.ToString("R", CultureInfo.InvariantCulture)),
                new TypedValue((int)DxfCode.Text, "InsertionZ=" + link.InsertionPoint.Z.ToString("R", CultureInfo.InvariantCulture))
            };
            foreach (SourceLink source in link.Sources)
                values.Add(new TypedValue(
                    (int)DxfCode.Text,
                    "Source=" + source.Handle + "|" + Escape(source.Name)));
            foreach (string handle in link.GeneratedHandles.Distinct(StringComparer.OrdinalIgnoreCase))
                values.Add(new TypedValue((int)DxfCode.Text, "Generated=" + handle));
            record.Data = new ResultBuffer(values.ToArray());
        }

        private static IntersectionLink ReadLink(Entity anchor, Transaction transaction)
        {
            if (anchor == null || anchor.ExtensionDictionary.IsNull)
                throw new InvalidOperationException("The selected point has no dynamic intersection link.");
            DBDictionary dictionary = transaction.GetObject(
                anchor.ExtensionDictionary,
                OpenMode.ForRead,
                false) as DBDictionary;
            if (dictionary == null || !dictionary.Contains(LinkRecordName))
                throw new InvalidOperationException("The selected point has no dynamic intersection link.");
            Xrecord record = transaction.GetObject(
                dictionary.GetAt(LinkRecordName),
                OpenMode.ForRead,
                false) as Xrecord;
            if (record == null || record.Data == null)
                throw new InvalidOperationException("The dynamic intersection link record is empty.");

            string schema = SchemaVersion;
            string setName = "INT-" + anchor.Handle;
            double x = 0.0;
            double y = 0.0;
            double z = 0.0;
            var sources = new List<SourceLink>();
            var generated = new List<string>();
            foreach (TypedValue value in record.Data)
            {
                string text = value.Value as string;
                if (string.IsNullOrWhiteSpace(text))
                    continue;
                if (text.StartsWith("Schema=", StringComparison.OrdinalIgnoreCase))
                    schema = text.Substring("Schema=".Length);
                else if (text.StartsWith("SetName=", StringComparison.OrdinalIgnoreCase))
                    setName = text.Substring("SetName=".Length);
                else if (text.StartsWith("InsertionX=", StringComparison.OrdinalIgnoreCase))
                    double.TryParse(text.Substring("InsertionX=".Length), NumberStyles.Float, CultureInfo.InvariantCulture, out x);
                else if (text.StartsWith("InsertionY=", StringComparison.OrdinalIgnoreCase))
                    double.TryParse(text.Substring("InsertionY=".Length), NumberStyles.Float, CultureInfo.InvariantCulture, out y);
                else if (text.StartsWith("InsertionZ=", StringComparison.OrdinalIgnoreCase))
                    double.TryParse(text.Substring("InsertionZ=".Length), NumberStyles.Float, CultureInfo.InvariantCulture, out z);
                else if (text.StartsWith("Source=", StringComparison.OrdinalIgnoreCase))
                {
                    string payload = text.Substring("Source=".Length);
                    int divider = payload.IndexOf('|');
                    sources.Add(divider < 0
                        ? new SourceLink(payload, payload)
                        : new SourceLink(
                            payload.Substring(0, divider),
                            Unescape(payload.Substring(divider + 1))));
                }
                else if (text.StartsWith("Generated=", StringComparison.OrdinalIgnoreCase))
                    generated.Add(text.Substring("Generated=".Length));
            }
            return new IntersectionLink(
                schema,
                setName,
                new Point3d(x, y, z),
                sources,
                generated);
        }

        private static void WriteGeneratedOwner(
            Entity generated,
            Transaction transaction,
            string anchorHandle)
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
                new TypedValue((int)DxfCode.Text, "Anchor=" + anchorHandle));
        }

        private static bool HasRecord(
            Entity entity,
            Transaction transaction,
            string recordName)
        {
            if (entity == null || entity.ExtensionDictionary.IsNull)
                return false;
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
                return transaction.GetObject(
                    dictionary.GetAt(name),
                    OpenMode.ForWrite,
                    false) as Xrecord;
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
            if (entity == null || entity.ExtensionDictionary.IsNull)
                return;
            DBDictionary dictionary = transaction.GetObject(
                entity.ExtensionDictionary,
                OpenMode.ForWrite,
                false) as DBDictionary;
            if (dictionary == null || !dictionary.Contains(name))
                return;
            DBObject record = transaction.GetObject(
                dictionary.GetAt(name),
                OpenMode.ForWrite,
                false);
            dictionary.Remove(name);
            record.Erase();
        }

        internal static List<ObjectId> FindLinkedAnchors(Database database)
        {
            var result = new List<ObjectId>();
            if (database == null)
                return result;
            try
            {
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    BlockTableRecord currentSpace = transaction.GetObject(
                        database.CurrentSpaceId,
                        OpenMode.ForRead,
                        false) as BlockTableRecord;
                    if (currentSpace == null)
                        return result;
                    foreach (ObjectId id in currentSpace)
                    {
                        DBPoint anchor = transaction.GetObject(id, OpenMode.ForRead, false) as DBPoint;
                        if (anchor != null && HasRecord(anchor, transaction, LinkRecordName))
                            result.Add(id);
                    }
                }
            }
            catch
            {
                // Manager scans must not destabilise AutoCAD.
            }
            return result;
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
                    out value))
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

        private static string Escape(string value)
        {
            return (value ?? string.Empty).Replace("%", "%25").Replace("|", "%7C");
        }

        private static string Unescape(string value)
        {
            return (value ?? string.Empty).Replace("%7C", "|").Replace("%25", "%");
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

        private static bool PromptNonNegativeDouble(
            Editor editor,
            string label,
            double current,
            out double value)
        {
            var options = new PromptDoubleOptions(
                "\n" + label + " <" + current.ToString("0.###", CultureInfo.InvariantCulture) + ">: ")
            {
                AllowNegative = false,
                AllowZero = true,
                UseDefaultValue = true,
                DefaultValue = current
            };
            PromptDoubleResult result = editor.GetDouble(options);
            value = result.Status == PromptStatus.OK ? result.Value : current;
            return result.Status == PromptStatus.OK;
        }

        private static bool PromptPositiveInteger(
            Editor editor,
            string label,
            int current,
            out int value)
        {
            var options = new PromptIntegerOptions(
                "\n" + label + " <" + current.ToString(CultureInfo.InvariantCulture) + ">: ")
            {
                AllowNegative = false,
                AllowZero = false,
                UseDefaultValue = true,
                DefaultValue = current
            };
            PromptIntegerResult result = editor.GetInteger(options);
            value = result.Status == PromptStatus.OK ? result.Value : current;
            return result.Status == PromptStatus.OK;
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
                   result.StringResult.Equals("Yes", StringComparison.OrdinalIgnoreCase);
        }

        private static Document ActiveDocument()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
        }

        private sealed class SourceRecord
        {
            public SourceRecord(
                ObjectId sourceId,
                string sourceHandle,
                string sourceName,
                string sourceType,
                IReadOnlyList<DesignPath> paths)
            {
                SourceId = sourceId;
                SourceHandle = sourceHandle;
                SourceName = sourceName;
                SourceType = sourceType;
                Paths = paths;
            }
            public ObjectId SourceId { get; }
            public string SourceHandle { get; }
            public string SourceName { get; }
            public string SourceType { get; }
            public IReadOnlyList<DesignPath> Paths { get; }
        }

        private sealed class DesignPath
        {
            public DesignPath(string name, IReadOnlyList<Point3d> points)
            {
                Name = name ?? string.Empty;
                Points = points;
            }
            public string Name { get; }
            public IReadOnlyList<Point3d> Points { get; }
        }

        private sealed class ExtractionResult
        {
            public ExtractionResult(List<IntersectionHit> intersections, int segmentPairsTested)
            {
                Intersections = intersections;
                SegmentPairsTested = segmentPairsTested;
            }
            public List<IntersectionHit> Intersections { get; }
            public int SegmentPairsTested { get; }
        }

        private sealed class IntersectionHit
        {
            public IntersectionHit(
                string sourceA,
                string pathA,
                string sourceB,
                string pathB,
                double x,
                double y,
                double elevationA,
                double elevationB,
                double elevationDifference,
                bool isElevationWarning)
            {
                SourceA = sourceA;
                PathA = pathA;
                SourceB = sourceB;
                PathB = pathB;
                X = x;
                Y = y;
                ElevationA = elevationA;
                ElevationB = elevationB;
                ElevationDifference = elevationDifference;
                IsElevationWarning = isElevationWarning;
            }
            public string SourceA { get; }
            public string PathA { get; }
            public string SourceB { get; }
            public string PathB { get; }
            public double X { get; }
            public double Y { get; }
            public double ElevationA { get; }
            public double ElevationB { get; }
            public double ElevationDifference { get; }
            public bool IsElevationWarning { get; }
        }

        private sealed class SourceLink
        {
            public SourceLink(string handle, string name)
            {
                Handle = handle ?? string.Empty;
                Name = name ?? string.Empty;
            }
            public string Handle { get; }
            public string Name { get; }
        }

        private sealed class IntersectionLink
        {
            public IntersectionLink(
                string schema,
                string setName,
                Point3d insertionPoint,
                IEnumerable<SourceLink> sources,
                IEnumerable<string> generatedHandles)
            {
                Schema = string.IsNullOrWhiteSpace(schema) ? SchemaVersion : schema;
                SetName = string.IsNullOrWhiteSpace(setName) ? "INT-SET" : setName;
                InsertionPoint = insertionPoint;
                Sources = sources == null
                    ? new List<SourceLink>()
                    : sources.ToList();
                GeneratedHandles = generatedHandles == null
                    ? new List<string>()
                    : generatedHandles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
            public string Schema { get; }
            public string SetName { get; }
            public Point3d InsertionPoint { get; }
            public List<SourceLink> Sources { get; }
            public List<string> GeneratedHandles { get; }
        }

        private sealed class IntersectionSettings
        {
            public string Layer = DefaultLayer;
            public double MarkerRadius = 1.5;
            public double LabelHeight = 2.0;
            public double XyTolerance = 0.01;
            public double ElevationWarning = 0.05;
            public double CurveSampleLength = 2.0;
            public int MaximumIntersections = 500;
            public string CorridorCodeFilter = string.Empty;

            public static IntersectionSettings Read(Database database)
            {
                var settings = new IntersectionSettings();
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    DBDictionary nod = transaction.GetObject(
                        database.NamedObjectsDictionaryId,
                        OpenMode.ForRead,
                        false) as DBDictionary;
                    if (nod == null || !nod.Contains(SettingsDictionary))
                        return settings;
                    DBDictionary ce = transaction.GetObject(
                        nod.GetAt(SettingsDictionary),
                        OpenMode.ForRead,
                        false) as DBDictionary;
                    if (ce == null || !ce.Contains(SettingsRecord))
                        return settings;
                    Xrecord record = transaction.GetObject(
                        ce.GetAt(SettingsRecord),
                        OpenMode.ForRead,
                        false) as Xrecord;
                    string[] values = record == null || record.Data == null
                        ? new string[0]
                        : record.Data.AsArray()
                            .Where(item => item.TypeCode == (int)DxfCode.Text)
                            .Select(item => Convert.ToString(item.Value, CultureInfo.InvariantCulture))
                            .ToArray();
                    if (values.Length >= 8)
                    {
                        settings.Layer = values[0];
                        double.TryParse(values[1], NumberStyles.Float, CultureInfo.InvariantCulture, out settings.MarkerRadius);
                        double.TryParse(values[2], NumberStyles.Float, CultureInfo.InvariantCulture, out settings.LabelHeight);
                        double.TryParse(values[3], NumberStyles.Float, CultureInfo.InvariantCulture, out settings.XyTolerance);
                        double.TryParse(values[4], NumberStyles.Float, CultureInfo.InvariantCulture, out settings.ElevationWarning);
                        double.TryParse(values[5], NumberStyles.Float, CultureInfo.InvariantCulture, out settings.CurveSampleLength);
                        int.TryParse(values[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out settings.MaximumIntersections);
                        settings.CorridorCodeFilter = values[7];
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
                    DBDictionary nod = transaction.GetObject(
                        database.NamedObjectsDictionaryId,
                        OpenMode.ForWrite,
                        false) as DBDictionary;
                    DBDictionary ce;
                    if (nod.Contains(SettingsDictionary))
                        ce = transaction.GetObject(
                            nod.GetAt(SettingsDictionary),
                            OpenMode.ForWrite,
                            false) as DBDictionary;
                    else
                    {
                        ce = new DBDictionary();
                        nod.SetAt(SettingsDictionary, ce);
                        transaction.AddNewlyCreatedDBObject(ce, true);
                    }
                    Xrecord record;
                    if (ce.Contains(SettingsRecord))
                        record = transaction.GetObject(
                            ce.GetAt(SettingsRecord),
                            OpenMode.ForWrite,
                            false) as Xrecord;
                    else
                    {
                        record = new Xrecord();
                        ce.SetAt(SettingsRecord, record);
                        transaction.AddNewlyCreatedDBObject(record, true);
                    }
                    string[] values =
                    {
                        Layer,
                        MarkerRadius.ToString("R", CultureInfo.InvariantCulture),
                        LabelHeight.ToString("R", CultureInfo.InvariantCulture),
                        XyTolerance.ToString("R", CultureInfo.InvariantCulture),
                        ElevationWarning.ToString("R", CultureInfo.InvariantCulture),
                        CurveSampleLength.ToString("R", CultureInfo.InvariantCulture),
                        MaximumIntersections.ToString(CultureInfo.InvariantCulture),
                        CorridorCodeFilter ?? string.Empty
                    };
                    record.Data = new ResultBuffer(values
                        .Select(value => new TypedValue((int)DxfCode.Text, value))
                        .ToArray());
                    transaction.Commit();
                }
            }

            private void Normalize()
            {
                if (string.IsNullOrWhiteSpace(Layer)) Layer = DefaultLayer;
                if (MarkerRadius <= 0.0) MarkerRadius = 1.5;
                if (LabelHeight <= 0.0) LabelHeight = 2.0;
                if (XyTolerance <= 0.0) XyTolerance = 0.01;
                if (ElevationWarning < 0.0) ElevationWarning = 0.05;
                if (CurveSampleLength <= 0.0) CurveSampleLength = 2.0;
                if (MaximumIntersections < 1) MaximumIntersections = 500;
                if (CorridorCodeFilter == null) CorridorCodeFilter = string.Empty;
            }
        }

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance =
                new ReferenceEqualityComparer();
            public new bool Equals(object x, object y) { return ReferenceEquals(x, y); }
            public int GetHashCode(object obj)
            {
                return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
            }
        }
    }

    /// <summary>
    /// Defers dynamic-intersection regeneration until Application.Idle. Database
    /// events only queue a database; they never mutate drawings from inside an
    /// ObjectModified/ObjectAppended callback.
    /// </summary>
    internal static class DynamicIntersectionUpdateManager
    {
        private static readonly Dictionary<Database, Document> Documents =
            new Dictionary<Database, Document>();
        private static readonly HashSet<Database> Pending =
            new HashSet<Database>();
        private static bool _internalUpdate;

        public static bool IsInitialized { get; private set; }

        public static void Initialize()
        {
            if (IsInitialized)
                return;
            IsInitialized = true;
            AcApplication.DocumentManager.DocumentCreated += OnDocumentCreated;
            AcApplication.DocumentManager.DocumentActivated += OnDocumentActivated;
            AcApplication.DocumentManager.DocumentToBeDestroyed += OnDocumentToBeDestroyed;
            AcApplication.Idle += OnIdle;
            Attach(AcApplication.DocumentManager.MdiActiveDocument);
        }

        public static void Terminate()
        {
            if (!IsInitialized)
                return;
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

        public static void BeginInternalUpdate() { _internalUpdate = true; }
        public static void EndInternalUpdate() { _internalUpdate = false; }

        public static void RegisterLinkedSet(Document document, ObjectId anchorId)
        {
            Attach(document);
            if (document != null)
                Pending.Remove(document.Database);
        }

        public static void UnregisterLinkedSet(Document document, ObjectId anchorId)
        {
            if (document != null)
                Pending.Remove(document.Database);
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
            if (document == null || Documents.ContainsKey(document.Database))
                return;
            Documents.Add(document.Database, document);
            document.Database.ObjectModified += OnDatabaseObjectChanged;
            document.Database.ObjectAppended += OnDatabaseObjectChanged;
        }

        private static void Detach(Document document)
        {
            if (document == null || !Documents.ContainsKey(document.Database))
                return;
            document.Database.ObjectModified -= OnDatabaseObjectChanged;
            document.Database.ObjectAppended -= OnDatabaseObjectChanged;
            Documents.Remove(document.Database);
            Pending.Remove(document.Database);
        }

        private static void OnDatabaseObjectChanged(object sender, ObjectEventArgs args)
        {
            if (_internalUpdate || args == null || args.DBObject == null)
                return;
            Database database = args.DBObject.Database;
            if (database != null && Documents.ContainsKey(database))
                Pending.Add(database);
        }

        private static void OnIdle(object sender, EventArgs args)
        {
            if (_internalUpdate || Pending.Count == 0)
                return;

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
                List<ObjectId> anchors = DynamicIntersectionCommands.FindLinkedAnchors(database);
                if (anchors.Count == 0)
                    continue;

                try
                {
                    using (document.LockDocument())
                    {
                        foreach (ObjectId anchorId in anchors)
                            DynamicIntersectionCommands.RefreshLinkedSet(
                                document,
                                anchorId,
                                false,
                                true);
                    }
                }
                catch (System.Exception exception)
                {
                    document.Editor.WriteMessage(
                        "\nCE Tools dynamic-intersection monitor deferred an update. " +
                        exception.Message);
                    Pending.Add(database);
                }
            }
        }
    }
}

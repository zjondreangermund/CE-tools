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
using Autodesk.Civil.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilCogoPoint = Autodesk.Civil.DatabaseServices.CogoPoint;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

[assembly: CommandClass(typeof(CETools.Civil3D.SurveyCoordinateWorkflowCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Linked survey-coordinate workflows. Compact coordinate registers store
    /// source handles in the table extension dictionary so rows can be refreshed
    /// after source COGO points or AutoCAD points move.
    /// </summary>
    public sealed class SurveyCoordinateWorkflowCommands
    {
        private const string LinkRecordName = "CE_COORDINATE_LINKS";
        private const string SchemaVersion = "1";
        private const double PointTolerance = 1e-8;

        [CommandMethod("CE_TOOLS", "CE_COORDPICK2", CommandFlags.Modal | CommandFlags.Redraw)]
        public void CoordinatePick()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            AnnotationOptions settings;
            if (!AnnotationSettingsStore.Prepare(document, true, out settings)) return;

            Editor editor = document.Editor;
            PromptPointResult pointResult = editor.GetPoint("\nPick coordinate point: ");
            if (pointResult.Status != PromptStatus.OK) return;

            string pointName;
            if (!PromptPointName(editor, "P", 1, out pointName)) return;

            CoordinateRegisterTarget register = PromptForRegisterTarget(document);
            if (register.Cancelled) return;

            ObjectId surfaceId;
            if (!PromptOptionalSurface(document, out surfaceId)) return;
            Point3d target = SampleSurface(
                document.Database,
                surfaceId,
                ToWorld(editor, pointResult.Value));
            Point3d labelPoint = Point3d.Origin;
            if (settings.Output != AnnotationOutput.Cogo &&
                !AnnotationWriter.PromptLabelPoint(
                    editor,
                    pointResult.Value,
                    settings,
                    out labelPoint))
            {
                return;
            }

            var created = new List<ObjectId>();
            try
            {
                ObjectId sourceId;
                if (settings.Output == AnnotationOutput.Cogo)
                {
                    sourceId = CreateCogoPoint(
                        document,
                        target,
                        BuildPlainCoordinate(target),
                        created);
                    ApplyPointNames(
                        document.Database,
                        new List<ObjectId> { sourceId },
                        new List<string> { pointName });
                    if (settings.DrawMarker)
                    {
                        CreateMarker(document.Database, target, settings.TextHeight, created);
                    }
                }
                else
                {
                    sourceId = CreateAnchor(document.Database, target, created);
                    DynamicCoordinateLinkStore.SetPointName(
                        document.Database,
                        sourceId,
                        pointName);
                    CreateAnnotation(
                        document.Database,
                        target,
                        labelPoint,
                        BuildNamedMTextCoordinate(pointName, target),
                        settings,
                        true,
                        created);
                }

                ObjectId tableId = ApplyRegisterTarget(
                    document,
                    register,
                    new List<ObjectId> { sourceId },
                    settings.TextHeight);
                DynamicCoordinateLinkStore.LinkGeneratedObjects(
                    document.Database,
                    sourceId,
                    created);
                if (!surfaceId.IsNull)
                    DynamicCoordinateLinkStore.LinkSurfaceElevation(
                        document.Database,
                        surfaceId,
                        new[] { sourceId });

                editor.WriteMessage(
                    "\nCE_COORDPICK2 complete. Output={0}; linked table={1}.",
                    settings.Output,
                    tableId.IsNull ? "No" : "Yes");
            }
            catch (System.Exception exception)
            {
                TryErase(document.Database, created);
                editor.WriteMessage(
                    "\nCE_COORDPICK2 cancelled. Created objects were removed where possible. {0}",
                    exception.Message);
            }
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_COORDPICKCONTINUOUS",
            CommandFlags.Modal | CommandFlags.Redraw)]
        public void CoordinatePickContinuous()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            AnnotationOptions settings;
            if (!AnnotationSettingsStore.Prepare(document, true, out settings)) return;
            CoordinateRegisterTarget register = PromptForRegisterTarget(document);
            if (register.Cancelled) return;
            ObjectId surfaceId;
            if (!PromptOptionalSurface(document, out surfaceId)) return;

            Editor editor = document.Editor;
            string pointPrefix;
            int pointStart;
            if (!PromptPointSequence(editor, "P", 1, out pointPrefix, out pointStart)) return;

            int sequence = pointStart;
            int placed = 0;
            ObjectId tableId = ObjectId.Null;
            while (true)
            {
                var pointOptions = new PromptPointOptions(
                    "\nPick coordinate point or press Enter to finish: ")
                {
                    AllowNone = true
                };
                PromptPointResult pointResult = editor.GetPoint(pointOptions);
                if (pointResult.Status == PromptStatus.None ||
                    pointResult.Status == PromptStatus.Cancel)
                {
                    break;
                }
                if (pointResult.Status != PromptStatus.OK) break;

                string pointName = FormatPointName(
                    pointPrefix,
                    sequence++);
                Point3d target = SampleSurface(
                    document.Database,
                    surfaceId,
                    ToWorld(editor, pointResult.Value));
                var created = new List<ObjectId>();
                ObjectId sourceId;
                try
                {
                    if (settings.Output == AnnotationOutput.Cogo)
                    {
                        sourceId = CreateCogoPoint(
                            document,
                            target,
                            pointName,
                            created);
                        ApplyPointNames(
                            document.Database,
                            new List<ObjectId> { sourceId },
                            new List<string> { pointName });
                        if (settings.DrawMarker)
                            CreateMarker(
                                document.Database,
                                target,
                                settings.TextHeight,
                                created);
                    }
                    else
                    {
                        sourceId = CreateAnchor(document.Database, target, created);
                        DynamicCoordinateLinkStore.SetPointName(
                            document.Database,
                            sourceId,
                            pointName);
                        double offset = PaperAnnotationScale.ModelDistance(
                            document.Database,
                            settings.TextHeight * 2.5);
                        Point3d labelPoint = new Point3d(
                            target.X + offset,
                            target.Y + offset,
                            target.Z);
                        CreateAnnotation(
                            document.Database,
                            target,
                            labelPoint,
                            BuildNamedMTextCoordinate(pointName, target),
                            settings,
                            true,
                            created);
                    }

                    DynamicCoordinateLinkStore.LinkGeneratedObjects(
                        document.Database,
                        sourceId,
                        created);
                    if (!surfaceId.IsNull)
                        DynamicCoordinateLinkStore.LinkSurfaceElevation(
                            document.Database,
                            surfaceId,
                            new[] { sourceId });

                    CoordinateRegisterTarget currentRegister = register;
                    if (!tableId.IsNull)
                        currentRegister = CoordinateRegisterTarget.Existing(tableId);
                    tableId = ApplyRegisterTarget(
                        document,
                        currentRegister,
                        new List<ObjectId> { sourceId },
                        settings.TextHeight);
                    placed++;
                }
                catch (System.Exception exception)
                {
                    TryErase(document.Database, created);
                    editor.WriteMessage(
                        "\nPoint {0} was not committed: {1}",
                        pointName,
                        exception.Message);
                }
            }

            editor.WriteMessage(
                "\nCE_COORDPICKCONTINUOUS complete. Points placed={0}; linked table={1}.",
                placed,
                tableId.IsNull ? "No" : tableId.Handle.ToString());
        }

        [CommandMethod("CE_TOOLS", "CE_COORDCROSS2", CommandFlags.Modal | CommandFlags.Redraw)]
        public void CoordinateCross()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            AnnotationOptions settings;
            if (!AnnotationSettingsStore.Prepare(document, true, out settings)) return;

            Editor editor = document.Editor;
            PromptPointResult pointResult = editor.GetPoint("\nPick coordinate-cross point: ");
            if (pointResult.Status != PromptStatus.OK) return;

            string pointName;
            if (!PromptPointName(editor, "P", 1, out pointName)) return;

            bool createCogo = PromptYesNo(
                editor,
                "Create a Civil 3D COGO point",
                settings.Output == AnnotationOutput.Cogo);
            if (settings.Output == AnnotationOutput.Cogo && !createCogo)
            {
                createCogo = true;
                editor.WriteMessage(
                    "\nCOGO annotation output requires a COGO point; COGO creation was enabled.");
            }

            bool createCross = PromptYesNo(editor, "Create coordinate-cross linework", true);
            bool createLabel = PromptYesNo(editor, "Create the selected annotation output", true);
            CoordinateRegisterTarget register = PromptForRegisterTarget(document);
            if (register.Cancelled) return;

            ObjectId surfaceId;
            if (!PromptOptionalSurface(document, out surfaceId)) return;
            Point3d target = SampleSurface(
                document.Database,
                surfaceId,
                ToWorld(editor, pointResult.Value));
            Point3d labelPoint = Point3d.Origin;
            if (createLabel && settings.Output != AnnotationOutput.Cogo &&
                !AnnotationWriter.PromptLabelPoint(
                    editor,
                    pointResult.Value,
                    settings,
                    out labelPoint))
            {
                return;
            }

            var created = new List<ObjectId>();
            try
            {
                ObjectId sourceId = createCogo
                    ? CreateCogoPoint(
                        document,
                        target,
                        pointName,
                        created)
                    : CreateAnchor(document.Database, target, created);
                DynamicCoordinateLinkStore.SetPointName(
                    document.Database,
                    sourceId,
                    pointName);
                if (createCogo)
                    ApplyPointNames(
                        document.Database,
                        new[] { sourceId },
                        new[] { pointName });

                if (createCross)
                {
                    CreateCrossLinework(
                        document.Database,
                        target,
                        settings.TextHeight,
                        created);
                }

                if (settings.DrawMarker)
                {
                    CreateMarker(document.Database, target, settings.TextHeight, created);
                }

                if (createLabel && settings.Output != AnnotationOutput.Cogo)
                {
                    CreateAnnotation(
                        document.Database,
                        target,
                        labelPoint,
                        BuildNamedMTextCoordinate(pointName, target),
                        settings,
                        false,
                        created);
                }

                ObjectId tableId = ApplyRegisterTarget(
                    document,
                    register,
                    new List<ObjectId> { sourceId },
                    settings.TextHeight);
                DynamicCoordinateLinkStore.LinkGeneratedObjects(
                    document.Database,
                    sourceId,
                    created);
                if (!surfaceId.IsNull)
                    DynamicCoordinateLinkStore.LinkSurfaceElevation(
                        document.Database,
                        surfaceId,
                        new[] { sourceId });

                editor.WriteMessage(
                    "\nCE_COORDCROSS2 complete. COGO={0}; cross={1}; label={2}; linked table={3}.",
                    createCogo ? "Yes" : "No",
                    createCross ? "Yes" : "No",
                    createLabel ? "Yes" : "No",
                    tableId.IsNull ? "No" : "Yes");
            }
            catch (System.Exception exception)
            {
                TryErase(document.Database, created);
                editor.WriteMessage(
                    "\nCE_COORDCROSS2 cancelled. Created objects were removed where possible. {0}",
                    exception.Message);
            }
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_COORDTABLE2",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void CoordinateTable()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            Editor editor = document.Editor;
            PromptSelectionResult selection = GetSelection(
                editor,
                "\nSelect Civil 3D COGO points and/or AutoCAD points: ");
            if (selection.Status != PromptStatus.OK) return;

            var sourceIds = new List<ObjectId>();
            int rejected = 0;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject selected in selection.Value)
                {
                    if (selected == null || selected.ObjectId.IsNull)
                    {
                        rejected++;
                        continue;
                    }

                    DBObject value = transaction.GetObject(
                        selected.ObjectId,
                        OpenMode.ForRead,
                        false);
                    if (value is CivilCogoPoint || value is DBPoint)
                    {
                        sourceIds.Add(selected.ObjectId);
                    }
                    else
                    {
                        rejected++;
                    }
                }
            }

            if (sourceIds.Count == 0)
            {
                editor.WriteMessage(
                    "\nCE_COORDTABLE2 cancelled. No supported coordinate points were selected.");
                return;
            }

            if (PromptYesNo(editor, "Assign CE point names to the selected points", true))
            {
                string prefix;
                int start;
                if (!PromptPointSequence(editor, "P", 1, out prefix, out start)) return;
                var names = new List<string>();
                for (int index = 0; index < sourceIds.Count; index++)
                    names.Add(FormatPointName(prefix, start + index));
                ApplySourcePointNames(document.Database, sourceIds, names);
            }

            PromptPointResult insertion = editor.GetPoint(
                "\nPick insertion point for the compact linked coordinate table: ");
            if (insertion.Status != PromptStatus.OK) return;

            AnnotationOptions settings;
            if (!AnnotationSettingsStore.Prepare(document, true, out settings)) return;

            ObjectId tableId = CreateLinkedTable(
                document.Database,
                ToWorld(editor, insertion.Value),
                sourceIds,
                settings.TextHeight,
                "LINKED COORDINATE REGISTER");

            editor.WriteMessage(
                "\nCE_COORDTABLE2 complete. Rows={0}; rejected={1}; table={2}.",
                sourceIds.Count,
                rejected,
                tableId.Handle);
        }

        [CommandMethod("CE_TOOLS", "CE_COORDREFRESH", CommandFlags.Modal | CommandFlags.Redraw)]
        public void RefreshCoordinateTable()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            var options = new PromptEntityOptions(
                "\nSelect a CE Tools linked coordinate table to refresh: ");
            options.SetRejectMessage("\nSelect an AutoCAD table.");
            options.AddAllowedClass(typeof(Table), false);
            PromptEntityResult result = document.Editor.GetEntity(options);
            if (result.Status != PromptStatus.OK) return;

            try
            {
                DynamicCoordinateLinkStore.Refresh(document);
                int active;
                int missing;
                RefreshLinkedTable(
                    document.Database,
                    result.ObjectId,
                    out active,
                    out missing);
                document.Editor.WriteMessage(
                    "\nCE_COORDREFRESH complete. Active rows={0}; missing sources={1}.",
                    active,
                    missing);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_COORDREFRESH cancelled. {0}",
                    exception.Message);
            }
        }

        internal static int RefreshAll(Document document)
        {
            if (document == null) return 0;
            List<ObjectId> tables = FindLinkedTables(document.Database);
            int refreshed = 0;
            foreach (ObjectId tableId in tables)
            {
                try
                {
                    int active;
                    int missing;
                    RefreshLinkedTable(
                        document.Database,
                        tableId,
                        out active,
                        out missing);
                    refreshed++;
                }
                catch
                {
                    // One stale register must not block the remaining tables.
                }
            }
            return refreshed;
        }

        internal static int CountLinkedTables(Database database)
        {
            return database == null ? 0 : FindLinkedTables(database).Count;
        }

        [CommandMethod("CE_TOOLS", "CE_COORDPOLY2", CommandFlags.Modal | CommandFlags.Redraw)]
        public void PolylineVertexPoints()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (civilDocument == null)
            {
                document.Editor.WriteMessage(
                    "\nCE_COORDPOLY2 cancelled. No active Civil 3D document is available.");
                return;
            }

            Editor editor = document.Editor;
            var selectionOptions = new PromptSelectionOptions
            {
                MessageForAdding =
                    "\nSelect one or more lightweight, 2D or 3D polylines. " +
                    "Point order follows each stored polyline direction: ",
                AllowDuplicates = false,
                RejectObjectsFromNonCurrentSpace = true
            };
            var selectionFilter = new SelectionFilter(new[]
            {
                new TypedValue((int)DxfCode.Start, "LWPOLYLINE,POLYLINE")
            });
            PromptSelectionResult selectionResult = editor.GetSelection(
                selectionOptions,
                selectionFilter);
            if (selectionResult.Status != PromptStatus.OK) return;
            ObjectId[] sourceObjectIds = selectionResult.Value.GetObjectIds()
                .Distinct()
                .ToArray();
            if (sourceObjectIds.Length == 0) return;

            var vertices = new List<Point3d>();
            var vertexCounts = new List<int>();
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId sourceObjectId in sourceObjectIds)
                {
                    Entity source = transaction.GetObject(
                        sourceObjectId,
                        OpenMode.ForRead,
                        false) as Entity;
                    if (source == null)
                    {
                        vertexCounts.Add(0);
                        continue;
                    }

                    List<Point3d> sourceVertices = ReadPolylineVertices(
                        source,
                        transaction);
                    vertexCounts.Add(sourceVertices.Count);
                    vertices.AddRange(sourceVertices);
                }
            }

            if (vertices.Count == 0)
            {
                editor.WriteMessage("\nCE_COORDPOLY2 cancelled. No usable vertices were found.");
                return;
            }

            ObjectId surfaceId;
            if (!PromptOptionalSurface(document, out surfaceId)) return;
            if (!surfaceId.IsNull)
            {
                for (int index = 0; index < vertices.Count; index++)
                    vertices[index] = SampleSurface(
                        document.Database,
                        surfaceId,
                        vertices[index]);
            }

            string defaultPrefix = BuildPrefix(string.Empty);
            string pointPrefix;
            int pointStart;
            if (!PromptPointSequence(
                    editor,
                    defaultPrefix,
                    1,
                    out pointPrefix,
                    out pointStart)) return;

            PromptPointResult insertion = editor.GetPoint(
                "\nPick insertion point for the compact X-Y-Z vertex table: ");
            if (insertion.Status != PromptStatus.OK) return;

            AnnotationOptions settings;
            if (!AnnotationSettingsStore.Prepare(document, false, out settings)) return;

            var pointNames = new List<string>();
            for (int index = 0; index < vertices.Count; index++)
            {
                pointNames.Add(FormatPointName(
                    pointPrefix,
                    pointStart + index));
            }

            var createdIds = new List<ObjectId>();
            try
            {
                var sourceIds = new List<ObjectId>();
                if (settings.Output == AnnotationOutput.Cogo)
                {
                    var locations = new Point3dCollection();
                    foreach (Point3d vertex in vertices) locations.Add(vertex);
                    ObjectIdCollection added = civilDocument.CogoPoints.Add(locations, true);
                    foreach (ObjectId id in added)
                    {
                        createdIds.Add(id);
                        sourceIds.Add(id);
                    }
                    if (sourceIds.Count != vertices.Count)
                    {
                        throw new InvalidOperationException(
                            "Civil 3D did not create one COGO point for every polyline vertex.");
                    }

                    ObjectIdCollection described = civilDocument.CogoPoints.SetRawDescription(
                        sourceIds,
                        pointNames);
                    if (described.Count != sourceIds.Count)
                    {
                        throw new InvalidOperationException(
                            "Civil 3D could not apply every sequential point name/description.");
                    }
                    ApplyPointNames(
                        document.Database,
                        sourceIds,
                        pointNames);
                }
                else
                {
                    for (int index = 0; index < vertices.Count; index++)
                    {
                        Point3d vertex = vertices[index];
                        ObjectId anchorId = CreateAnchor(
                            document.Database,
                            vertex,
                            createdIds);
                        sourceIds.Add(anchorId);
                        DynamicCoordinateLinkStore.SetPointName(
                            document.Database,
                            anchorId,
                            pointNames[index]);

                        double offset = PaperAnnotationScale.ModelDistance(
                            document.Database,
                            settings.TextHeight * 2.5);
                        Point3d labelPoint = new Point3d(
                            vertex.X + offset,
                            vertex.Y + (index % 2 == 0 ? offset : -offset),
                            vertex.Z);
                        var annotationIds = new List<ObjectId>();
                        CreateAnnotation(
                            document.Database,
                            vertex,
                            labelPoint,
                            BuildNamedMTextCoordinate(pointNames[index], vertex),
                            settings,
                            true,
                            annotationIds);
                        foreach (ObjectId annotationId in annotationIds)
                            createdIds.Add(annotationId);
                        DynamicCoordinateLinkStore.LinkGeneratedObjects(
                            document.Database,
                            anchorId,
                            annotationIds);
                    }
                }

                int sourceCursor = 0;
                for (int sourceIndex = 0;
                    sourceIndex < sourceObjectIds.Length;
                    sourceIndex++)
                {
                    int count = vertexCounts[sourceIndex];
                    if (count <= 0) continue;
                    DynamicCoordinateLinkStore.LinkPolylineVertices(
                        document.Database,
                        sourceObjectIds[sourceIndex],
                        sourceIds.Skip(sourceCursor).Take(count).ToList());
                    sourceCursor += count;
                }
                if (!surfaceId.IsNull)
                    DynamicCoordinateLinkStore.LinkSurfaceElevation(
                        document.Database,
                        surfaceId,
                        sourceIds);

                ObjectId tableId = CreateLinkedTable(
                    document.Database,
                    ToWorld(editor, insertion.Value),
                    sourceIds,
                    settings.TextHeight,
                    "POLYLINE VERTEX POINTS — X / Y / Z");

                editor.WriteMessage(
                    "\nCE_COORDPOLY2 complete. Polylines={0}; vertices={1}; first={2}; last={3}; table={4}.",
                    sourceObjectIds.Length,
                    sourceIds.Count,
                    pointNames[0],
                    pointNames[pointNames.Count - 1],
                    tableId.Handle);
            }
            catch (System.Exception exception)
            {
                TryErase(document.Database, createdIds);
                editor.WriteMessage(
                    "\nCE_COORDPOLY2 cancelled. Created COGO points were removed where possible. {0}",
                    exception.Message);
            }
        }

        private static ObjectId CreateAnchor(
            Database database,
            Point3d target,
            ICollection<ObjectId> created)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTableRecord currentSpace = OpenCurrentSpace(database, transaction);
                var point = new DBPoint(target);
                point.SetDatabaseDefaults(database);
                ObjectId id = currentSpace.AppendEntity(point);
                transaction.AddNewlyCreatedDBObject(point, true);
                transaction.Commit();
                created.Add(id);
                return id;
            }
        }

        private static void CreateAnnotation(
            Database database,
            Point3d target,
            Point3d labelPoint,
            string contents,
            AnnotationOptions settings,
            bool includeMarker,
            ICollection<ObjectId> created)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTableRecord currentSpace = OpenCurrentSpace(database, transaction);
                if (includeMarker && settings.DrawMarker)
                {
                    AppendMarker(
                        database,
                        currentSpace,
                        transaction,
                        target,
                        settings.TextHeight,
                        created);
                }

                if (settings.Output == AnnotationOutput.MText)
                {
                    var text = new MText();
                    text.SetDatabaseDefaults(database);
                    text.Location = labelPoint;
                    text.Attachment = AttachmentPoint.MiddleLeft;
                    text.TextHeight = PaperAnnotationScale.AnnotativeTextHeight(
                        database,
                        settings.TextHeight);
                    text.Contents = contents;
                    PaperAnnotationScale.SetAnnotative(text);
                    ObjectId textId = currentSpace.AppendEntity(text);
                    transaction.AddNewlyCreatedDBObject(text, true);
                    created.Add(textId);
                }
                else
                {
                    var text = new MText();
                    text.SetDatabaseDefaults(database);
                    text.Location = labelPoint;
                    text.TextHeight = PaperAnnotationScale.AnnotativeTextHeight(
                        database,
                        settings.TextHeight);
                    text.Contents = contents;

                    var leader = new MLeader();
                    leader.SetDatabaseDefaults(database);
                    leader.ContentType = ContentType.MTextContent;
                    leader.MText = text;
                    PaperAnnotationScale.SetAnnotative(leader);
                    int leaderIndex = leader.AddLeader();
                    int leaderLineIndex = leader.AddLeaderLine(leaderIndex);
                    leader.AddFirstVertex(leaderLineIndex, target);
                    leader.AddLastVertex(leaderLineIndex, labelPoint);
                    ObjectId leaderId = currentSpace.AppendEntity(leader);
                    transaction.AddNewlyCreatedDBObject(leader, true);
                    created.Add(leaderId);
                }

                transaction.Commit();
            }
        }

        private static void CreateMarker(
            Database database,
            Point3d target,
            double textHeight,
            ICollection<ObjectId> created)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTableRecord currentSpace = OpenCurrentSpace(database, transaction);
                AppendMarker(
                    database,
                    currentSpace,
                    transaction,
                    target,
                    textHeight,
                    created);
                transaction.Commit();
            }
        }

        private static void AppendMarker(
            Database database,
            BlockTableRecord currentSpace,
            Transaction transaction,
            Point3d target,
            double textHeight,
            ICollection<ObjectId> created)
        {
            var marker = new Circle(
                target,
                Vector3d.ZAxis,
                Math.Max(
                    PaperAnnotationScale.ModelDistance(database, textHeight * 0.25),
                    0.001));
            marker.SetDatabaseDefaults(database);
            ObjectId markerId = currentSpace.AppendEntity(marker);
            transaction.AddNewlyCreatedDBObject(marker, true);
            created.Add(markerId);
        }

        private static ObjectId CreateCogoPoint(
            Document document,
            Point3d target,
            string description,
            ICollection<ObjectId> created)
        {
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (civilDocument == null)
            {
                throw new InvalidOperationException(
                    "No active Civil 3D document is available for COGO point creation.");
            }

            var locations = new Point3dCollection { target };
            ObjectIdCollection added = civilDocument.CogoPoints.Add(locations, true);
            if (added.Count != 1)
            {
                throw new InvalidOperationException(
                    "Civil 3D did not create the expected COGO point.");
            }

            ObjectId id = added[0];
            created.Add(id);
            var ids = new List<ObjectId> { id };
            ObjectIdCollection described = civilDocument.CogoPoints.SetRawDescription(
                ids,
                new List<string> { description });
            if (described.Count != 1)
            {
                throw new InvalidOperationException(
                    "Civil 3D could not assign the coordinate point description.");
            }

            return id;
        }

        private static void CreateCrossLinework(
            Database database,
            Point3d target,
            double textHeight,
            ICollection<ObjectId> created)
        {
            double halfSize = Math.Max(
                PaperAnnotationScale.ModelDistance(database, textHeight * 1.5),
                0.001);
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTableRecord currentSpace = OpenCurrentSpace(database, transaction);
                var horizontal = new Line(
                    new Point3d(target.X - halfSize, target.Y, target.Z),
                    new Point3d(target.X + halfSize, target.Y, target.Z));
                horizontal.SetDatabaseDefaults(database);
                ObjectId horizontalId = currentSpace.AppendEntity(horizontal);
                transaction.AddNewlyCreatedDBObject(horizontal, true);
                created.Add(horizontalId);

                var vertical = new Line(
                    new Point3d(target.X, target.Y - halfSize, target.Z),
                    new Point3d(target.X, target.Y + halfSize, target.Z));
                vertical.SetDatabaseDefaults(database);
                ObjectId verticalId = currentSpace.AppendEntity(vertical);
                transaction.AddNewlyCreatedDBObject(vertical, true);
                created.Add(verticalId);
                transaction.Commit();
            }
        }

        private static CoordinateRegisterTarget PromptForRegisterTarget(Document document)
        {
            Editor editor = document.Editor;
            string choice = DisciplineWorkflowDialogs.SelectWorkflow(
                "CE Tools - Coordinate Register",
                "Choose how the selected or created coordinate points connect to a dynamic register.",
                new List<DisciplineWorkflowAction>
                {
                    new DisciplineWorkflowAction("Create a new linked register", "New", "Pick its insertion point in the drawing after this window closes.", "01 Register"),
                    new DisciplineWorkflowAction("Append to an existing register", "Existing", "Select an existing CE Tools linked coordinate table in the drawing.", "01 Register"),
                    new DisciplineWorkflowAction("Do not create a register", "None", "Create only the selected point and annotation outputs.", "02 Skip")
                });
            if (string.IsNullOrWhiteSpace(choice))
                return CoordinateRegisterTarget.Cancel();
            if (string.Equals(choice, "None", StringComparison.OrdinalIgnoreCase))
            {
                return CoordinateRegisterTarget.None();
            }

            if (string.Equals(choice, "Existing", StringComparison.OrdinalIgnoreCase))
            {
                var entityOptions = new PromptEntityOptions(
                    "\nSelect an existing CE Tools linked coordinate table: ");
                entityOptions.SetRejectMessage("\nSelect an AutoCAD table.");
                entityOptions.AddAllowedClass(typeof(Table), false);
                PromptEntityResult tableResult = editor.GetEntity(entityOptions);
                return tableResult.Status == PromptStatus.OK
                    ? CoordinateRegisterTarget.Existing(tableResult.ObjectId)
                    : CoordinateRegisterTarget.Cancel();
            }

            PromptPointResult insertion = editor.GetPoint(
                "\nPick insertion point for the new linked coordinate table: ");
            return insertion.Status == PromptStatus.OK
                ? CoordinateRegisterTarget.New(ToWorld(editor, insertion.Value))
                : CoordinateRegisterTarget.Cancel();
        }

        private static ObjectId ApplyRegisterTarget(
            Document document,
            CoordinateRegisterTarget target,
            IList<ObjectId> newSources,
            double textHeight)
        {
            if (target.Mode == CoordinateRegisterMode.None) return ObjectId.Null;
            if (target.Mode == CoordinateRegisterMode.New)
            {
                return CreateLinkedTable(
                    document.Database,
                    target.InsertionPoint,
                    newSources,
                    textHeight,
                    "LINKED COORDINATE REGISTER");
            }

            AppendSourcesAndRefresh(
                document.Database,
                target.TableId,
                newSources,
                textHeight);
            return target.TableId;
        }

        internal static ObjectId CreateLinkedTable(
            Database database,
            Point3d insertionPoint,
            IList<ObjectId> sourceIds,
            double textHeight,
            string title)
        {
            if (sourceIds == null || sourceIds.Count == 0)
            {
                throw new InvalidOperationException(
                    "A linked coordinate table cannot be created without source points.");
            }

            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTableRecord currentSpace = OpenCurrentSpace(database, transaction);
                var table = new Table();
                table.SetDatabaseDefaults(database);
                table.TableStyle = database.Tablestyle;
                table.Position = insertionPoint;
                PaperAnnotationScale.SetAnnotative(table);
                ObjectId tableId = currentSpace.AppendEntity(table);
                transaction.AddNewlyCreatedDBObject(table, true);

                WriteLinkRecord(table, transaction, sourceIds);
                int missing;
                List<CoordinateRow> rows = ReadRows(transaction, sourceIds, out missing);
                PopulateTable(
                    table,
                    rows,
                    ResolveScaleAwareTableHeight(database, textHeight),
                    title);
                transaction.Commit();
                return tableId;
            }
        }

        private static void AppendSourcesAndRefresh(
            Database database,
            ObjectId tableId,
            IList<ObjectId> newSources,
            double textHeight)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                Table table = transaction.GetObject(
                    tableId,
                    OpenMode.ForWrite,
                    false) as Table;
                if (table == null)
                {
                    throw new InvalidOperationException("The selected object is not a table.");
                }

                List<ObjectId> links = ReadLinkRecord(database, table, transaction);
                foreach (ObjectId id in newSources)
                {
                    if (!id.IsNull && !links.Contains(id)) links.Add(id);
                }
                if (links.Count == 0)
                {
                    throw new InvalidOperationException(
                        "The selected table has no usable linked point sources.");
                }

                WriteLinkRecord(table, transaction, links);
                int missing;
                List<CoordinateRow> rows = ReadRows(transaction, links, out missing);
                PopulateTable(
                    table,
                    rows,
                    ResolveScaleAwareTableHeight(database, textHeight),
                    "LINKED COORDINATE REGISTER");
                transaction.Commit();
            }
        }

        private static void RefreshLinkedTable(
            Database database,
            ObjectId tableId,
            out int active,
            out int missing)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                Table table = transaction.GetObject(
                    tableId,
                    OpenMode.ForWrite,
                    false) as Table;
                if (table == null)
                {
                    throw new InvalidOperationException("The selected object is not a table.");
                }

                List<ObjectId> links = ReadLinkRecord(database, table, transaction);
                if (links.Count == 0)
                {
                    throw new InvalidOperationException(
                        "This table is not a CE Tools linked coordinate register.");
                }

                List<CoordinateRow> rows = ReadRows(transaction, links, out missing);
                active = rows.Count;
                if (active == 0)
                {
                    throw new InvalidOperationException(
                        "All linked coordinate sources are missing; the table was not cleared.");
                }

                double currentHeight = ReadCurrentTableTextHeight(
                    table,
                    database.Textsize);
                string currentTitle = ReadCurrentTableTitle(
                    table,
                    "LINKED COORDINATE REGISTER");
                PopulateTable(
                    table,
                    rows,
                    currentHeight,
                    currentTitle);
                transaction.Commit();
            }
        }

        private static List<CoordinateRow> ReadRows(
            Transaction transaction,
            IList<ObjectId> sourceIds,
            out int missing)
        {
            var rows = new List<CoordinateRow>();
            missing = 0;
            int fallbackNumber = 1;
            foreach (ObjectId id in sourceIds)
            {
                if (id.IsNull || id.IsErased)
                {
                    missing++;
                    continue;
                }

                DBObject value;
                try
                {
                    value = transaction.GetObject(id, OpenMode.ForRead, false);
                }
                catch
                {
                    missing++;
                    continue;
                }

                CivilCogoPoint cogo = value as CivilCogoPoint;
                if (cogo != null)
                {
                    string fallbackName = string.IsNullOrWhiteSpace(cogo.RawDescription)
                        ? cogo.PointName
                        : cogo.RawDescription;
                    string pointName = DynamicCoordinateLinkStore.ReadPointName(
                        cogo,
                        transaction,
                        fallbackName);
                    if (string.IsNullOrWhiteSpace(pointName))
                    {
                        pointName = "P" + cogo.PointNumber.ToString(CultureInfo.InvariantCulture);
                    }

                    rows.Add(new CoordinateRow(
                        cogo.PointNumber.ToString(CultureInfo.InvariantCulture),
                        pointName,
                        cogo.Northing,
                        cogo.Easting,
                        cogo.Elevation));
                    continue;
                }

                DBPoint point = value as DBPoint;
                if (point != null)
                {
                    string pointName = DynamicCoordinateLinkStore.ReadPointName(
                        point,
                        transaction,
                        "P" + fallbackNumber.ToString(CultureInfo.InvariantCulture));
                    rows.Add(new CoordinateRow(
                        fallbackNumber.ToString(CultureInfo.InvariantCulture),
                        pointName,
                        point.Position.Y,
                        point.Position.X,
                        point.Position.Z));
                    fallbackNumber++;
                    continue;
                }

                missing++;
            }

            return rows;
        }

        private static void PopulateTable(
            Table table,
            IList<CoordinateRow> rows,
            double textHeight,
            string title)
        {
            if (rows == null || rows.Count == 0)
            {
                throw new InvalidOperationException(
                    "A coordinate table cannot be populated with zero rows.");
            }

            const int columns = 4;
            table.SetSize(rows.Count + 2, columns);
            double height = NormalizeHeight(textHeight);
            table.SetRowHeight(Math.Max(height * 2.4, 5.0));
            table.SetColumnWidth(Math.Max(height * 7.0, 18.0));
            table.Columns[0].Width = Math.Max(height * 7.5, 20.0);
            table.Cells[0, 0].TextString = title;
            table.MergeCells(CellRange.Create(table, 0, 0, 0, columns - 1));

            string[] headings =
            {
                "POINT NAME",
                "X",
                "Y",
                "Z"
            };
            for (int column = 0; column < headings.Length; column++)
            {
                table.Cells[1, column].TextString = headings[column];
                table.Cells[1, column].TextHeight = height;
                table.Cells[1, column].Alignment = CellAlignment.MiddleCenter;
            }

            table.Cells[0, 0].TextHeight = height * 1.15;
            table.Cells[0, 0].Alignment = CellAlignment.MiddleCenter;

            for (int index = 0; index < rows.Count; index++)
            {
                CoordinateRow row = rows[index];
                int tableRow = index + 2;
                string[] values =
                {
                    row.PointName,
                    row.X.ToString("N3", CultureInfo.CurrentCulture),
                    row.Y.ToString("N3", CultureInfo.CurrentCulture),
                    row.Z.ToString("N3", CultureInfo.CurrentCulture)
                };
                for (int column = 0; column < values.Length; column++)
                {
                    table.Cells[tableRow, column].TextString = values[column];
                    table.Cells[tableRow, column].TextHeight = height;
                    table.Cells[tableRow, column].Alignment = CellAlignment.MiddleCenter;
                }
            }

            table.GenerateLayout();
        }

        private static void WriteLinkRecord(
            Table table,
            Transaction transaction,
            IList<ObjectId> sourceIds)
        {
            if (table.ExtensionDictionary.IsNull)
            {
                table.CreateExtensionDictionary();
            }

            DBDictionary dictionary = (DBDictionary)transaction.GetObject(
                table.ExtensionDictionary,
                OpenMode.ForWrite,
                false);
            Xrecord record;
            if (dictionary.Contains(LinkRecordName))
            {
                record = (Xrecord)transaction.GetObject(
                    dictionary.GetAt(LinkRecordName),
                    OpenMode.ForWrite,
                    false);
            }
            else
            {
                record = new Xrecord();
                dictionary.SetAt(LinkRecordName, record);
                transaction.AddNewlyCreatedDBObject(record, true);
            }

            var values = new List<TypedValue>
            {
                new TypedValue((int)DxfCode.Text, "Schema=" + SchemaVersion)
            };
            foreach (ObjectId id in sourceIds)
            {
                if (!id.IsNull)
                {
                    values.Add(new TypedValue(
                        (int)DxfCode.Text,
                        "Handle=" + id.Handle.ToString()));
                }
            }

            record.Data = new ResultBuffer(values.ToArray());
        }

        private static List<ObjectId> ReadLinkRecord(
            Database database,
            Table table,
            Transaction transaction)
        {
            var ids = new List<ObjectId>();
            if (table.ExtensionDictionary.IsNull) return ids;
            DBDictionary dictionary = transaction.GetObject(
                table.ExtensionDictionary,
                OpenMode.ForRead,
                false) as DBDictionary;
            if (dictionary == null || !dictionary.Contains(LinkRecordName)) return ids;
            Xrecord record = transaction.GetObject(
                dictionary.GetAt(LinkRecordName),
                OpenMode.ForRead,
                false) as Xrecord;
            if (record == null || record.Data == null) return ids;

            foreach (TypedValue value in record.Data)
            {
                string text = value.Value as string;
                if (string.IsNullOrWhiteSpace(text) ||
                    !text.StartsWith("Handle=", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string handleText = text.Substring("Handle=".Length);
                long handleValue;
                if (!long.TryParse(
                    handleText,
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out handleValue))
                {
                    continue;
                }

                try
                {
                    ObjectId id = database.GetObjectId(false, new Handle(handleValue), 0);
                    if (!id.IsNull) ids.Add(id);
                }
                catch
                {
                    // Missing source handles are ignored without clearing valid rows.
                }
            }

            return ids;
        }

        private static List<ObjectId> FindLinkedTables(Database database)
        {
            var result = new List<ObjectId>();
            if (database == null) return result;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTableRecord currentSpace = transaction.GetObject(
                    database.CurrentSpaceId,
                    OpenMode.ForRead,
                    false) as BlockTableRecord;
                if (currentSpace == null) return result;
                foreach (ObjectId objectId in currentSpace)
                {
                    Table table = transaction.GetObject(
                        objectId,
                        OpenMode.ForRead,
                        false) as Table;
                    if (table == null || table.ExtensionDictionary.IsNull) continue;
                    DBDictionary dictionary = transaction.GetObject(
                        table.ExtensionDictionary,
                        OpenMode.ForRead,
                        false) as DBDictionary;
                    if (dictionary != null && dictionary.Contains(LinkRecordName))
                        result.Add(objectId);
                }
            }
            return result;
        }

        private static List<Point3d> ReadPolylineVertices(
            Entity source,
            Transaction transaction)
        {
            var points = new List<Point3d>();
            Polyline lightweight = source as Polyline;
            if (lightweight != null)
            {
                for (int index = 0; index < lightweight.NumberOfVertices; index++)
                {
                    AddDistinct(points, lightweight.GetPoint3dAt(index));
                }
                return points;
            }

            Polyline2d polyline2d = source as Polyline2d;
            if (polyline2d != null)
            {
                foreach (ObjectId vertexId in polyline2d)
                {
                    Vertex2d vertex = transaction.GetObject(
                        vertexId,
                        OpenMode.ForRead,
                        false) as Vertex2d;
                    if (vertex != null) AddDistinct(points, vertex.Position);
                }
                RemoveClosingDuplicate(points);
                return points;
            }

            Polyline3d polyline3d = source as Polyline3d;
            if (polyline3d != null)
            {
                foreach (ObjectId vertexId in polyline3d)
                {
                    PolylineVertex3d vertex = transaction.GetObject(
                        vertexId,
                        OpenMode.ForRead,
                        false) as PolylineVertex3d;
                    if (vertex != null) AddDistinct(points, vertex.Position);
                }
                RemoveClosingDuplicate(points);
            }

            return points;
        }

        private static void AddDistinct(IList<Point3d> points, Point3d point)
        {
            if (points.Count == 0 ||
                points[points.Count - 1].DistanceTo(point) > PointTolerance)
            {
                points.Add(point);
            }
        }

        private static void RemoveClosingDuplicate(IList<Point3d> points)
        {
            if (points.Count > 1 &&
                points[0].DistanceTo(points[points.Count - 1]) <= PointTolerance)
            {
                points.RemoveAt(points.Count - 1);
            }
        }

        private static string BuildPrefix(string layer)
        {
            return "P";
        }

        private static string FormatPointName(string prefix, int sequence)
        {
            string safe = string.IsNullOrWhiteSpace(prefix) ? "P" : prefix.Trim();
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}{1}",
                safe,
                sequence);
        }

        private static bool PromptPointName(
            Editor editor,
            string defaultPrefix,
            int defaultSequence,
            out string pointName)
        {
            string prefix;
            int sequence;
            if (!PromptPointSequence(
                editor,
                defaultPrefix,
                defaultSequence,
                out prefix,
                out sequence))
            {
                pointName = string.Empty;
                return false;
            }
            pointName = FormatPointName(prefix, sequence);
            return true;
        }

        private static bool PromptPointSequence(
            Editor editor,
            string defaultPrefix,
            int defaultSequence,
            out string prefix,
            out int sequence)
        {
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Point Naming",
                "Set one raw-description sequence for the COGO labels and linked coordinate table.");
            model.AddText(
                "Prefix",
                "Point Names",
                "Raw-description prefix",
                string.IsNullOrWhiteSpace(defaultPrefix) ? "P" : defaultPrefix,
                "Use P for P1, P2, P3... labels.");
            model.AddPositiveInteger(
                "Start",
                "Point Names",
                "Starting number",
                Math.Max(defaultSequence, 1),
                "The first selected point receives this number.");
            if (!DisciplineWorkflowDialogs.EditSettings(model))
            {
                prefix = string.Empty;
                sequence = 0;
                return false;
            }

            prefix = model.Text("Prefix");
            if (string.IsNullOrWhiteSpace(prefix)) prefix = "P";
            sequence = model.Integer("Start", Math.Max(defaultSequence, 1));
            return true;
        }

        private static void ApplySourcePointNames(
            Database database,
            IList<ObjectId> sourceIds,
            IList<string> pointNames)
        {
            int count = Math.Min(sourceIds.Count, pointNames.Count);
            for (int index = 0; index < count; index++)
            {
                DynamicCoordinateLinkStore.SetPointName(
                    database,
                    sourceIds[index],
                    pointNames[index]);
            }
        }

        private static void ApplyPointNames(
            Database database,
            IList<ObjectId> pointIds,
            IList<string> pointNames)
        {
            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                int count = Math.Min(pointIds.Count, pointNames.Count);
                for (int index = 0; index < count; index++)
                {
                    CivilCogoPoint point = transaction.GetObject(
                        pointIds[index],
                        OpenMode.ForWrite,
                        false) as CivilCogoPoint;
                    if (point != null)
                    {
                        point.PointName = pointNames[index];
                        point.RawDescription = pointNames[index];
                    }
                }
                transaction.Commit();
            }
        }

        private static double ResolveScaleAwareTableHeight(
            Database database,
            double paperHeightMillimetres)
        {
            return PaperAnnotationScale.ModelTextHeight(
                database,
                paperHeightMillimetres);
        }

        private static double ReadCurrentTableTextHeight(Table table, double fallback)
        {
            try
            {
                if (table != null && table.Rows.Count > 1 && table.Columns.Count > 0)
                {
                    double value = table.Cells[1, 0].TextHeight ?? fallback;
                    if (value > 0.0 && !double.IsNaN(value) && !double.IsInfinity(value))
                        return value;
                }
            }
            catch
            {
                // Use the existing drawing fallback.
            }
            return Math.Max(fallback, 0.001);
        }

        private static string ReadCurrentTableTitle(Table table, string fallback)
        {
            try
            {
                string title = table == null ? string.Empty : table.Cells[0, 0].TextString;
                return string.IsNullOrWhiteSpace(title) ? fallback : title;
            }
            catch
            {
                return fallback;
            }
        }

        private static string BuildMTextCoordinate(Point3d point)
        {
            return string.Join(
                "\\P",
                "X: " + point.X.ToString("N3", CultureInfo.CurrentCulture),
                "Y: " + point.Y.ToString("N3", CultureInfo.CurrentCulture),
                "Z: " + point.Z.ToString("N3", CultureInfo.CurrentCulture));
        }

        private static string BuildNamedMTextCoordinate(
            string pointName,
            Point3d point)
        {
            return string.Join(
                "\\P",
                string.IsNullOrWhiteSpace(pointName) ? "<UNNAMED>" : pointName.Trim(),
                "X: " + point.X.ToString("N3", CultureInfo.CurrentCulture),
                "Y: " + point.Y.ToString("N3", CultureInfo.CurrentCulture),
                "Z: " + point.Z.ToString("N3", CultureInfo.CurrentCulture));
        }

        private static string BuildPlainCoordinate(Point3d point)
        {
            return string.Format(
                CultureInfo.CurrentCulture,
                "X {0:N3}; Y {1:N3}; Z {2:N3}",
                point.X,
                point.Y,
                point.Z);
        }

        private static bool PromptYesNo(Editor editor, string message, bool defaultValue)
        {
            System.Windows.MessageBoxResult result = System.Windows.MessageBox.Show(
                message,
                "CE Tools - Coordinate Utilities",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question,
                defaultValue
                    ? System.Windows.MessageBoxResult.Yes
                    : System.Windows.MessageBoxResult.No);
            return result == System.Windows.MessageBoxResult.Yes;
        }

        private static bool PromptOptionalSurface(
            Document document,
            out ObjectId surfaceId)
        {
            surfaceId = ObjectId.Null;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (document == null || civilDocument == null) return true;

            var choices = new List<SurfaceChoice>();
            try
            {
                using (Transaction transaction =
                    document.Database.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId id in civilDocument.GetSurfaceIds())
                    {
                        CivilSurface surface = transaction.GetObject(
                            id,
                            OpenMode.ForRead,
                            false) as CivilSurface;
                        if (surface == null) continue;
                        string minimum = "<Unavailable>";
                        string maximum = "<Unavailable>";
                        try
                        {
                            var properties = surface.GetGeneralProperties();
                            minimum = properties.MinimumElevation.ToString(
                                "N3",
                                CultureInfo.CurrentCulture);
                            maximum = properties.MaximumElevation.ToString(
                                "N3",
                                CultureInfo.CurrentCulture);
                        }
                        catch
                        {
                            // Keep unavailable limits.
                        }
                        choices.Add(new SurfaceChoice(
                            id,
                            surface.Name,
                            surface.GetType().Name,
                            surface.StyleName,
                            minimum,
                            maximum,
                            surface.IsOutOfDate ? "Out of date" : "Current"));
                    }
                }
            }
            catch
            {
                choices.Clear();
            }
            if (choices.Count == 0) return true;
            if (!PromptYesNo(
                document.Editor,
                "Link point Z values dynamically to a Civil 3D surface",
                true))
            {
                return true;
            }

            choices.Sort(delegate(SurfaceChoice left, SurfaceChoice right)
            {
                return string.Compare(
                    left.Name,
                    right.Name,
                    StringComparison.OrdinalIgnoreCase);
            });
            var window = new SurfaceSelectionWindow(choices);
            AcApplication.ShowModalWindow(window);
            if (window.SelectedSurface == null)
            {
                document.Editor.WriteMessage(
                    "\nCoordinate command cancelled. No controlling surface was selected.");
                return false;
            }
            surfaceId = window.SelectedSurface.ObjectId;
            return true;
        }

        private static Point3d SampleSurface(
            Database database,
            ObjectId surfaceId,
            Point3d point)
        {
            if (database == null || surfaceId.IsNull) return point;
            try
            {
                using (Transaction transaction =
                    database.TransactionManager.StartTransaction())
                {
                    CivilSurface surface = transaction.GetObject(
                        surfaceId,
                        OpenMode.ForRead,
                        false) as CivilSurface;
                    if (surface == null) return point;
                    double elevation = surface.FindElevationAtXY(point.X, point.Y);
                    return double.IsNaN(elevation) || double.IsInfinity(elevation)
                        ? point
                        : new Point3d(point.X, point.Y, elevation);
                }
            }
            catch
            {
                return point;
            }
        }

        private static PromptSelectionResult GetSelection(Editor editor, string message)
        {
            PromptSelectionResult implied = editor.SelectImplied();
            if (implied.Status == PromptStatus.OK && implied.Value.Count > 0)
            {
                editor.SetImpliedSelection(new ObjectId[0]);
                return implied;
            }

            return editor.GetSelection(
                new PromptSelectionOptions
                {
                    MessageForAdding = message,
                    AllowDuplicates = false,
                    RejectObjectsFromNonCurrentSpace = true
                });
        }

        private static double NormalizeHeight(double value)
        {
            if (Math.Abs(value - 1.8) < 0.05) return 1.8;
            if (Math.Abs(value - 2.0) < 0.05) return 2.0;
            if (Math.Abs(value - 2.5) < 0.05) return 2.5;
            if (Math.Abs(value - 3.5) < 0.05) return 3.5;
            if (Math.Abs(value - 5.0) < 0.05) return 5.0;
            return 2.0;
        }

        private static Point3d ToWorld(Editor editor, Point3d point)
        {
            return point.TransformBy(editor.CurrentUserCoordinateSystem);
        }

        private static BlockTableRecord OpenCurrentSpace(
            Database database,
            Transaction transaction)
        {
            return (BlockTableRecord)transaction.GetObject(
                database.CurrentSpaceId,
                OpenMode.ForWrite,
                false);
        }

        private static Document ActiveDocument()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
        }

        private static void TryErase(Database database, IEnumerable<ObjectId> ids)
        {
            try
            {
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId id in ids)
                    {
                        if (id.IsNull || id.IsErased) continue;
                        DBObject value = transaction.GetObject(id, OpenMode.ForWrite, false);
                        value.Erase();
                    }
                    transaction.Commit();
                }
            }
            catch
            {
                // Best-effort rollback for objects created through mixed APIs.
            }
        }

        private sealed class CoordinateRow
        {
            public CoordinateRow(string point, string pointName, double y, double x, double z)
            {
                Point = point;
                PointName = pointName;
                Y = y;
                X = x;
                Z = z;
            }

            public string Point { get; }
            public string PointName { get; }
            public double Y { get; }
            public double X { get; }
            public double Z { get; }
        }

        private enum CoordinateRegisterMode
        {
            None,
            New,
            Existing,
            Cancelled
        }

        private sealed class CoordinateRegisterTarget
        {
            private CoordinateRegisterTarget(
                CoordinateRegisterMode mode,
                Point3d insertionPoint,
                ObjectId tableId)
            {
                Mode = mode;
                InsertionPoint = insertionPoint;
                TableId = tableId;
            }

            public CoordinateRegisterMode Mode { get; }
            public Point3d InsertionPoint { get; }
            public ObjectId TableId { get; }
            public bool Cancelled => Mode == CoordinateRegisterMode.Cancelled;

            public static CoordinateRegisterTarget None()
            {
                return new CoordinateRegisterTarget(
                    CoordinateRegisterMode.None,
                    Point3d.Origin,
                    ObjectId.Null);
            }

            public static CoordinateRegisterTarget New(Point3d insertionPoint)
            {
                return new CoordinateRegisterTarget(
                    CoordinateRegisterMode.New,
                    insertionPoint,
                    ObjectId.Null);
            }

            public static CoordinateRegisterTarget Existing(ObjectId tableId)
            {
                return new CoordinateRegisterTarget(
                    CoordinateRegisterMode.Existing,
                    Point3d.Origin,
                    tableId);
            }

            public static CoordinateRegisterTarget Cancel()
            {
                return new CoordinateRegisterTarget(
                    CoordinateRegisterMode.Cancelled,
                    Point3d.Origin,
                    ObjectId.Null);
            }
        }
    }
}

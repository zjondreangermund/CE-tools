using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilCogoPoint = Autodesk.Civil.DatabaseServices.CogoPoint;

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

            CoordinateRegisterTarget register = PromptForRegisterTarget(document);
            if (register.Cancelled) return;

            Point3d target = ToWorld(editor, pointResult.Value);
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
                    if (settings.DrawMarker)
                    {
                        CreateMarker(document.Database, target, settings.TextHeight, created);
                    }
                }
                else
                {
                    sourceId = CreateAnchor(document.Database, target, created);
                    CreateAnnotation(
                        document.Database,
                        target,
                        labelPoint,
                        BuildMTextCoordinate(target),
                        settings,
                        true,
                        created);
                }

                ObjectId tableId = ApplyRegisterTarget(
                    document,
                    register,
                    new List<ObjectId> { sourceId },
                    settings.TextHeight);

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

            Point3d target = ToWorld(editor, pointResult.Value);
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
                        "Coordinate cross; " + BuildPlainCoordinate(target),
                        created)
                    : CreateAnchor(document.Database, target, created);

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
                        BuildMTextCoordinate(target),
                        settings,
                        false,
                        created);
                }

                ObjectId tableId = ApplyRegisterTarget(
                    document,
                    register,
                    new List<ObjectId> { sourceId },
                    settings.TextHeight);

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

            PromptPointResult insertion = editor.GetPoint(
                "\nPick insertion point for the compact linked coordinate table: ");
            if (insertion.Status != PromptStatus.OK) return;

            AnnotationOptions settings;
            if (!AnnotationSettingsStore.Prepare(document, false, out settings)) return;

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
            var entityOptions = new PromptEntityOptions(
                "\nSelect a polyline. Point order follows the stored polyline direction: ");
            entityOptions.SetRejectMessage(
                "\nSelect an AutoCAD lightweight, 2D or 3D polyline.");
            entityOptions.AddAllowedClass(typeof(Polyline), false);
            entityOptions.AddAllowedClass(typeof(Polyline2d), false);
            entityOptions.AddAllowedClass(typeof(Polyline3d), false);
            PromptEntityResult entityResult = editor.GetEntity(entityOptions);
            if (entityResult.Status != PromptStatus.OK) return;

            List<Point3d> vertices;
            string layer;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                Entity source = transaction.GetObject(
                    entityResult.ObjectId,
                    OpenMode.ForRead,
                    false) as Entity;
                if (source == null)
                {
                    editor.WriteMessage("\nCE_COORDPOLY2 cancelled. The polyline could not be opened.");
                    return;
                }

                layer = source.Layer;
                vertices = ReadPolylineVertices(source, transaction);
            }

            if (vertices.Count == 0)
            {
                editor.WriteMessage("\nCE_COORDPOLY2 cancelled. No usable vertices were found.");
                return;
            }

            string defaultPrefix = BuildPrefix(layer);
            PromptResult prefixResult = editor.GetString(
                new PromptStringOptions("\nPoint-name prefix <" + defaultPrefix + ">: ")
                {
                    AllowSpaces = false,
                    DefaultValue = defaultPrefix,
                    UseDefaultValue = true
                });
            if (prefixResult.Status != PromptStatus.OK) return;

            PromptIntegerResult startResult = editor.GetInteger(
                new PromptIntegerOptions("\nStarting point-name sequence <1>: ")
                {
                    AllowNegative = false,
                    AllowZero = false,
                    DefaultValue = 1,
                    LowerLimit = 1,
                    UseDefaultValue = true
                });
            if (startResult.Status != PromptStatus.OK) return;

            PromptPointResult insertion = editor.GetPoint(
                "\nPick insertion point for the compact Y-X-Z vertex table: ");
            if (insertion.Status != PromptStatus.OK) return;

            AnnotationOptions settings;
            if (!AnnotationSettingsStore.Prepare(document, false, out settings)) return;

            var locations = new Point3dCollection();
            var pointNames = new List<string>();
            for (int index = 0; index < vertices.Count; index++)
            {
                locations.Add(vertices[index]);
                pointNames.Add(FormatPointName(
                    prefixResult.StringResult,
                    startResult.Value + index));
            }

            var createdIds = new List<ObjectId>();
            try
            {
                ObjectIdCollection added = civilDocument.CogoPoints.Add(locations, true);
                foreach (ObjectId id in added) createdIds.Add(id);
                if (createdIds.Count != vertices.Count)
                {
                    throw new InvalidOperationException(
                        "Civil 3D did not create one COGO point for every polyline vertex.");
                }

                ObjectIdCollection described = civilDocument.CogoPoints.SetRawDescription(
                    createdIds,
                    pointNames);
                if (described.Count != createdIds.Count)
                {
                    throw new InvalidOperationException(
                        "Civil 3D could not apply every sequential point name/description.");
                }

                ObjectId tableId = CreateLinkedTable(
                    document.Database,
                    ToWorld(editor, insertion.Value),
                    createdIds,
                    settings.TextHeight,
                    "POLYLINE VERTEX POINTS — Y / X / Z");

                editor.WriteMessage(
                    "\nCE_COORDPOLY2 complete. Vertices={0}; first={1}; last={2}; table={3}.",
                    createdIds.Count,
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
                    text.TextHeight = settings.TextHeight;
                    text.Contents = contents;
                    ObjectId textId = currentSpace.AppendEntity(text);
                    transaction.AddNewlyCreatedDBObject(text, true);
                    created.Add(textId);
                }
                else
                {
                    var text = new MText();
                    text.SetDatabaseDefaults(database);
                    text.Location = labelPoint;
                    text.TextHeight = settings.TextHeight;
                    text.Contents = contents;

                    var leader = new MLeader();
                    leader.SetDatabaseDefaults(database);
                    leader.ContentType = ContentType.MTextContent;
                    leader.MText = text;
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
                Math.Max(textHeight * 0.75, 0.001));
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
            double halfSize = Math.Max(textHeight * 1.5, 0.001);
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
            var options = new PromptKeywordOptions(
                "\nCoordinate register [New/Existing/None] <None>: ")
            {
                AllowNone = true
            };
            options.Keywords.Add("New");
            options.Keywords.Add("Existing");
            options.Keywords.Add("None");
            PromptResult result = editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel)
            {
                return CoordinateRegisterTarget.Cancel();
            }

            string choice = result.Status == PromptStatus.None
                ? "None"
                : result.StringResult;
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

        private static ObjectId CreateLinkedTable(
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
                ObjectId tableId = currentSpace.AppendEntity(table);
                transaction.AddNewlyCreatedDBObject(table, true);

                WriteLinkRecord(table, transaction, sourceIds);
                int missing;
                List<CoordinateRow> rows = ReadRows(transaction, sourceIds, out missing);
                PopulateTable(table, rows, NormalizeHeight(textHeight), title);
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
                    NormalizeHeight(textHeight),
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

                PopulateTable(
                    table,
                    rows,
                    NormalizeHeight(database.Textsize),
                    "LINKED COORDINATE REGISTER");
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
                    string pointName = string.IsNullOrWhiteSpace(cogo.PointName)
                        ? cogo.RawDescription
                        : cogo.PointName;
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
                    rows.Add(new CoordinateRow(
                        fallbackNumber.ToString(CultureInfo.InvariantCulture),
                        "P" + fallbackNumber.ToString("D3", CultureInfo.InvariantCulture),
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

            const int columns = 5;
            table.SetSize(rows.Count + 2, columns);
            double height = NormalizeHeight(textHeight);
            table.SetRowHeight(Math.Max(height * 1.65, 3.0));
            table.SetColumnWidth(Math.Max(height * 5.5, 12.0));
            table.Cells[0, 0].TextString = title;
            table.MergeCells(CellRange.Create(table, 0, 0, 0, columns - 1));

            string[] headings =
            {
                "POINT",
                "POINT NAME",
                "Y / NORTHING",
                "X / EASTING",
                "Z / ELEVATION"
            };
            for (int column = 0; column < headings.Length; column++)
            {
                table.Cells[1, column].TextString = headings[column];
            }

            for (int index = 0; index < rows.Count; index++)
            {
                CoordinateRow row = rows[index];
                int tableRow = index + 2;
                table.Cells[tableRow, 0].TextString = row.Point;
                table.Cells[tableRow, 1].TextString = row.PointName;
                table.Cells[tableRow, 2].TextString = row.Y.ToString("N3", CultureInfo.CurrentCulture);
                table.Cells[tableRow, 3].TextString = row.X.ToString("N3", CultureInfo.CurrentCulture);
                table.Cells[tableRow, 4].TextString = row.Z.ToString("N3", CultureInfo.CurrentCulture);
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
            string value = string.IsNullOrWhiteSpace(layer) ? "VTX" : layer.Trim();
            value = value.Replace(' ', '-');
            return value.Length > 18 ? value.Substring(0, 18) : value;
        }

        private static string FormatPointName(string prefix, int sequence)
        {
            string safe = string.IsNullOrWhiteSpace(prefix) ? "VTX" : prefix.Trim();
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}-{1:D3}",
                safe,
                sequence);
        }

        private static string BuildMTextCoordinate(Point3d point)
        {
            return string.Join(
                "\\P",
                "Y / N: " + point.Y.ToString("N3", CultureInfo.CurrentCulture),
                "X / E: " + point.X.ToString("N3", CultureInfo.CurrentCulture),
                "Z: " + point.Z.ToString("N3", CultureInfo.CurrentCulture));
        }

        private static string BuildPlainCoordinate(Point3d point)
        {
            return string.Format(
                CultureInfo.CurrentCulture,
                "Y {0:N3}; X {1:N3}; Z {2:N3}",
                point.Y,
                point.X,
                point.Z);
        }

        private static bool PromptYesNo(Editor editor, string message, bool defaultValue)
        {
            var options = new PromptKeywordOptions(
                "\n" + message + " [Yes/No] <" + (defaultValue ? "Yes" : "No") + ">: ")
            {
                AllowNone = true
            };
            options.Keywords.Add("Yes");
            options.Keywords.Add("No");
            PromptResult result = editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel) return false;
            return result.Status == PromptStatus.None
                ? defaultValue
                : string.Equals(result.StringResult, "Yes", StringComparison.OrdinalIgnoreCase);
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

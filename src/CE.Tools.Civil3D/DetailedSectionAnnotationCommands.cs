using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.DetailedSectionAnnotationCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Creates linked, reversible detailed-section annotations for road, parking,
    /// stormwater, sewer and water section linework. Source geometry is never
    /// modified. Generated dimensions, labels and the component register can be
    /// refreshed or cleared as one linked set.
    /// </summary>
    public sealed class DetailedSectionAnnotationCommands
    {
        private const string RegAppName = "CE_SECTION_DETAIL";
        private const string AnnotationLayer = "CE-SECTION-DETAIL-ANNO";
        private const double Tolerance = 0.000001;

        [CommandMethod("CE_TOOLS", "CE_SECTIONDETAILTOOLS", CommandFlags.Modal)]
        public void SectionDetailTools()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            var options = new PromptKeywordOptions(
                "\nDetailed section tools [Create/Refresh/Information/Clear] <Create>: ")
            {
                AllowNone = true
            };
            options.Keywords.Add("Create");
            options.Keywords.Add("Refresh");
            options.Keywords.Add("Information");
            options.Keywords.Add("Clear");
            PromptResult result = document.Editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel) return;

            string choice = result.Status == PromptStatus.OK
                ? result.StringResult
                : "Create";
            string command;
            if (Equal(choice, "Refresh")) command = "CE_SECTIONDETAILREFRESH ";
            else if (Equal(choice, "Information")) command = "CE_SECTIONDETAILINFO ";
            else if (Equal(choice, "Clear")) command = "CE_SECTIONDETAILCLEAR ";
            else command = "CE_SECTIONDETAILCREATE ";
            document.SendStringToExecute(command, true, false, true);
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_SECTIONDETAILCREATE",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void CreateSectionDetail()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            Editor editor = document.Editor;

            PromptSelectionResult selection = PromptSources(editor);
            if (selection.Status != PromptStatus.OK) return;

            var sourceIds = new List<ObjectId>();
            int rejected;
            DetailedSectionSnapshot snapshot = BuildSnapshot(
                document.Database,
                selection.Value.GetObjectIds(),
                sourceIds,
                out rejected);
            if (snapshot == null || sourceIds.Count == 0)
            {
                editor.WriteMessage(
                    "\nCE_SECTIONDETAILCREATE stopped. No supported editable section geometry was selected.");
                return;
            }

            SectionDetailDiscipline discipline;
            if (!PromptDiscipline(editor, out discipline)) return;

            double defaultHeight = document.Database.Textsize > Tolerance
                ? document.Database.Textsize
                : 2.5;
            double textHeight;
            if (!PromptPositiveDouble(
                    editor,
                    "Annotation text height",
                    defaultHeight,
                    out textHeight))
                return;

            double dimensionOffset;
            if (!PromptPositiveDouble(
                    editor,
                    "Dimension offset from section geometry",
                    Math.Max(textHeight * 4.0, 1.0),
                    out dimensionOffset))
                return;

            PromptPointResult insertion = editor.GetPoint(
                "\nPick insertion point for the section title and component register: ");
            if (insertion.Status != PromptStatus.OK) return;
            Point3d insertionPoint = insertion.Value.TransformBy(
                editor.CurrentUserCoordinateSystem);

            var settings = new DetailedSectionSettings(
                Guid.NewGuid().ToString("N"),
                discipline,
                textHeight,
                dimensionOffset,
                insertionPoint,
                sourceIds.Select(id => id.Handle.ToString()).ToList());

            var review = new List<KeyValuePair<string, string>>
            {
                Pair("Discipline", DisciplineTitle(discipline)),
                Pair("Accepted source objects", sourceIds.Count.ToString(CultureInfo.InvariantCulture)),
                Pair("Rejected source objects", rejected.ToString(CultureInfo.InvariantCulture)),
                Pair("Overall width", snapshot.Width.ToString("N3", CultureInfo.CurrentCulture)),
                Pair("Overall height", snapshot.Height.ToString("N3", CultureInfo.CurrentCulture)),
                Pair("Circular elements", snapshot.Sources.Count(item => item.Diameter > Tolerance)
                    .ToString(CultureInfo.InvariantCulture)),
                Pair("Text height", textHeight.ToString("N3", CultureInfo.CurrentCulture)),
                Pair("Dimension offset", dimensionOffset.ToString("N3", CultureInfo.CurrentCulture)),
                Pair("Source geometry changed", "No"),
                Pair("Engineering status", "Drafting automation — verify dimensions, notes and project standards")
            };
            if (!PopupTablePresenter.ShowReview(
                    "CE Tools - Detailed Section Annotation",
                    "The command creates linked annotation only. It does not alter selected section geometry or certify the engineering detail.",
                    review,
                    "Create Detail"))
            {
                editor.WriteMessage("\nCE_SECTIONDETAILCREATE cancelled.");
                return;
            }

            try
            {
                int generated = GenerateSet(
                    document.Database,
                    settings,
                    snapshot);
                editor.Regen();
                editor.WriteMessage(
                    "\nCE_SECTIONDETAILCREATE complete. Discipline={0}; sources={1}; generated objects={2}.",
                    discipline,
                    sourceIds.Count,
                    generated);
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_SECTIONDETAILCREATE stopped. No completed annotation set was committed. {0}",
                    exception.Message);
            }
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_SECTIONDETAILREFRESH",
            CommandFlags.Modal | CommandFlags.Redraw)]
        public void RefreshSectionDetail()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            string setId;
            if (!PromptLinkedSet(document, out setId)) return;

            DetailedSectionSettings settings;
            List<ObjectId> sources;
            int missing;
            if (!ReadSet(document.Database, setId, out settings, out sources, out missing))
            {
                document.Editor.WriteMessage(
                    "\nCE_SECTIONDETAILREFRESH stopped. The linked annotation anchor or settings are missing.");
                return;
            }
            if (sources.Count == 0)
            {
                document.Editor.WriteMessage(
                    "\nCE_SECTIONDETAILREFRESH stopped. Every linked source object is missing; existing annotation was retained.");
                return;
            }

            var accepted = new List<ObjectId>();
            int rejected;
            DetailedSectionSnapshot snapshot = BuildSnapshot(
                document.Database,
                sources,
                accepted,
                out rejected);
            if (snapshot == null || accepted.Count == 0)
            {
                document.Editor.WriteMessage(
                    "\nCE_SECTIONDETAILREFRESH stopped. Live linked objects no longer provide usable section extents; existing annotation was retained.");
                return;
            }
            settings = settings.WithSources(
                accepted.Select(id => id.Handle.ToString()).ToList());

            try
            {
                EraseSet(document.Database, setId);
                int generated = GenerateSet(
                    document.Database,
                    settings,
                    snapshot);
                document.Editor.Regen();
                document.Editor.WriteMessage(
                    "\nCE_SECTIONDETAILREFRESH complete. Live sources={0}; missing={1}; rejected={2}; generated objects={3}.",
                    accepted.Count,
                    missing,
                    rejected,
                    generated);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_SECTIONDETAILREFRESH failed. {0}",
                    exception.Message);
            }
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_SECTIONDETAILINFO",
            CommandFlags.Modal | CommandFlags.Redraw)]
        public void SectionDetailInformation()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            string setId;
            if (!PromptLinkedSet(document, out setId)) return;

            DetailedSectionSettings settings;
            List<ObjectId> sources;
            int missing;
            if (!ReadSet(document.Database, setId, out settings, out sources, out missing))
            {
                document.Editor.WriteMessage(
                    "\nCE_SECTIONDETAILINFO: the selected object is not part of a complete CE detailed-section annotation set.");
                return;
            }
            int generated = ReadSetEntityIds(document.Database, setId).Count;
            var rows = new List<KeyValuePair<string, string>>
            {
                Pair("Set ID", settings.SetId),
                Pair("Discipline", DisciplineTitle(settings.Discipline)),
                Pair("Live source objects", sources.Count.ToString(CultureInfo.InvariantCulture)),
                Pair("Missing source objects", missing.ToString(CultureInfo.InvariantCulture)),
                Pair("Generated annotation objects", generated.ToString(CultureInfo.InvariantCulture)),
                Pair("Text height", settings.TextHeight.ToString("N3", CultureInfo.CurrentCulture)),
                Pair("Dimension offset", settings.DimensionOffset.ToString("N3", CultureInfo.CurrentCulture)),
                Pair("Title/register point", FormatPoint(settings.InsertionPoint)),
                Pair("Refresh command", "CE_SECTIONDETAILREFRESH"),
                Pair("Engineering status", "Verify every generated dimension, description and standard note before issue")
            };
            PopupTablePresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - Detailed Section Information",
                "Linked annotation is generated from source geometry handles and can be refreshed or cleared without changing the section linework.",
                rows,
                "CE TOOLS DETAILED SECTION INFORMATION");
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_SECTIONDETAILCLEAR",
            CommandFlags.Modal | CommandFlags.Redraw)]
        public void ClearSectionDetail()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            string setId;
            if (!PromptLinkedSet(document, out setId)) return;
            List<ObjectId> generated = ReadSetEntityIds(document.Database, setId);
            if (generated.Count == 0)
            {
                document.Editor.WriteMessage(
                    "\nCE_SECTIONDETAILCLEAR: no linked generated objects were found.");
                return;
            }

            var rows = new List<KeyValuePair<string, string>>
            {
                Pair("Annotation set", setId),
                Pair("Generated objects to remove", generated.Count.ToString(CultureInfo.InvariantCulture)),
                Pair("Source section geometry retained", "Yes")
            };
            if (!PopupTablePresenter.ShowReview(
                    "CE Tools - Clear Detailed Section",
                    "Only CE-generated dimensions, labels, notes and the component register will be erased.",
                    rows,
                    "Clear Annotation"))
                return;

            int erased = EraseSet(document.Database, setId);
            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_SECTIONDETAILCLEAR complete. Generated objects removed={0}.",
                erased);
        }

        private static PromptSelectionResult PromptSources(Editor editor)
        {
            var options = new PromptSelectionOptions
            {
                MessageForAdding = "\nSelect road, parking, stormwater, sewer or water section geometry: "
            };
            return editor.GetSelection(options);
        }

        private static DetailedSectionSnapshot BuildSnapshot(
            Database database,
            IEnumerable<ObjectId> candidateIds,
            ICollection<ObjectId> acceptedIds,
            out int rejected)
        {
            rejected = 0;
            var sources = new List<DetailedSectionSource>();
            Extents3d combined = new Extents3d();
            bool hasExtents = false;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in candidateIds)
                {
                    if (id.IsNull || id.IsErased)
                    {
                        rejected++;
                        continue;
                    }
                    Entity entity;
                    try
                    {
                        entity = transaction.GetObject(
                            id,
                            OpenMode.ForRead,
                            false) as Entity;
                    }
                    catch
                    {
                        rejected++;
                        continue;
                    }
                    if (!IsSupportedSource(entity))
                    {
                        rejected++;
                        continue;
                    }
                    LayerTableRecord layer = transaction.GetObject(
                        entity.LayerId,
                        OpenMode.ForRead,
                        false) as LayerTableRecord;
                    if (layer != null && layer.IsLocked)
                    {
                        rejected++;
                        continue;
                    }
                    Extents3d extents;
                    try
                    {
                        extents = entity.GeometricExtents;
                    }
                    catch
                    {
                        rejected++;
                        continue;
                    }
                    if (!hasExtents)
                    {
                        combined = extents;
                        hasExtents = true;
                    }
                    else
                    {
                        combined.AddExtents(extents);
                    }
                    acceptedIds.Add(id);
                    sources.Add(ReadSource(entity, extents));
                }
            }
            return !hasExtents || sources.Count == 0
                ? null
                : new DetailedSectionSnapshot(combined, sources);
        }

        private static bool IsSupportedSource(Entity entity)
        {
            return entity is Line ||
                   entity is Polyline ||
                   entity is Polyline2d ||
                   entity is Polyline3d ||
                   entity is Circle ||
                   entity is Arc ||
                   entity is Ellipse ||
                   entity is Spline;
        }

        private static DetailedSectionSource ReadSource(
            Entity entity,
            Extents3d extents)
        {
            double diameter = 0.0;
            Circle circle = entity as Circle;
            if (circle != null) diameter = circle.Radius * 2.0;
            Arc arc = entity as Arc;
            if (arc != null) diameter = arc.Radius * 2.0;

            string measure;
            Polyline lightweight = entity as Polyline;
            if (lightweight != null && lightweight.Closed)
            {
                measure = string.Format(
                    CultureInfo.CurrentCulture,
                    "Area {0:N3}; perimeter {1:N3}",
                    Math.Abs(lightweight.Area),
                    lightweight.Length);
            }
            else if (diameter > Tolerance)
            {
                measure = "Diameter " + diameter.ToString("N3", CultureInfo.CurrentCulture);
            }
            else
            {
                Curve curve = entity as Curve;
                double length;
                if (curve != null && TryCurveLength(curve, out length))
                    measure = "Length " + length.ToString("N3", CultureInfo.CurrentCulture);
                else
                    measure = string.Format(
                        CultureInfo.CurrentCulture,
                        "Extent {0:N3} x {1:N3}",
                        extents.MaxPoint.X - extents.MinPoint.X,
                        extents.MaxPoint.Y - extents.MinPoint.Y);
            }

            Point3d centre = new Point3d(
                (extents.MinPoint.X + extents.MaxPoint.X) / 2.0,
                (extents.MinPoint.Y + extents.MaxPoint.Y) / 2.0,
                0.0);
            return new DetailedSectionSource(
                entity.ObjectId,
                entity.ObjectId.Handle.ToString(),
                entity.GetType().Name,
                entity.Layer,
                measure,
                diameter,
                centre);
        }

        private static bool TryCurveLength(Curve curve, out double length)
        {
            length = 0.0;
            try
            {
                length = Math.Abs(
                    curve.GetDistanceAtParameter(curve.EndParam) -
                    curve.GetDistanceAtParameter(curve.StartParam));
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static int GenerateSet(
            Database database,
            DetailedSectionSettings settings,
            DetailedSectionSnapshot snapshot)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                EnsureRegApp(database, transaction);
                ObjectId layerId = GetOrCreateLayer(
                    database,
                    transaction,
                    AnnotationLayer);
                BlockTableRecord currentSpace = transaction.GetObject(
                    database.CurrentSpaceId,
                    OpenMode.ForWrite,
                    false) as BlockTableRecord;
                if (currentSpace == null)
                    throw new InvalidOperationException(
                        "The current drawing space could not be opened.");

                int created = 0;
                MText title = CreateMText(
                    database,
                    settings.InsertionPoint,
                    settings.TextHeight * 1.35,
                    DisciplineTitle(settings.Discipline),
                    AttachmentPoint.BottomLeft);
                title.LayerId = layerId;
                WriteLink(title, settings, "Anchor", true);
                Append(currentSpace, transaction, title);
                created++;

                MText note = CreateMText(
                    database,
                    settings.InsertionPoint + new Vector3d(0.0, -settings.TextHeight * 2.0, 0.0),
                    settings.TextHeight,
                    DisciplineNote(settings.Discipline),
                    AttachmentPoint.TopLeft);
                note.LayerId = layerId;
                WriteLink(note, settings, "Note", false);
                Append(currentSpace, transaction, note);
                created++;

                Point3d min = snapshot.Extents.MinPoint;
                Point3d max = snapshot.Extents.MaxPoint;
                double z = 0.0;
                if (snapshot.Width > Tolerance)
                {
                    var horizontal = new RotatedDimension(
                        0.0,
                        new Point3d(min.X, min.Y, z),
                        new Point3d(max.X, min.Y, z),
                        new Point3d(
                            (min.X + max.X) / 2.0,
                            min.Y - settings.DimensionOffset,
                            z),
                        string.Empty,
                        database.Dimstyle);
                    horizontal.SetDatabaseDefaults(database);
                    horizontal.LayerId = layerId;
                    WriteLink(horizontal, settings, "OverallWidth", false);
                    Append(currentSpace, transaction, horizontal);
                    created++;
                }
                if (snapshot.Height > Tolerance)
                {
                    var vertical = new RotatedDimension(
                        Math.PI / 2.0,
                        new Point3d(min.X, min.Y, z),
                        new Point3d(min.X, max.Y, z),
                        new Point3d(
                            min.X - settings.DimensionOffset,
                            (min.Y + max.Y) / 2.0,
                            z),
                        string.Empty,
                        database.Dimstyle);
                    vertical.SetDatabaseDefaults(database);
                    vertical.LayerId = layerId;
                    WriteLink(vertical, settings, "OverallHeight", false);
                    Append(currentSpace, transaction, vertical);
                    created++;
                }

                for (int index = 0; index < snapshot.Sources.Count; index++)
                {
                    DetailedSectionSource source = snapshot.Sources[index];
                    string label = "D" + (index + 1).ToString(CultureInfo.InvariantCulture);
                    if (source.Diameter > Tolerance)
                    {
                        label += "  " + PipeLabel(settings.Discipline) + " Ø" +
                            source.Diameter.ToString("N3", CultureInfo.CurrentCulture);
                    }
                    MText marker = CreateMText(
                        database,
                        source.Centre + new Vector3d(
                            settings.TextHeight * 0.5,
                            settings.TextHeight * 0.5,
                            0.0),
                        settings.TextHeight,
                        label,
                        AttachmentPoint.BottomLeft);
                    marker.LayerId = layerId;
                    WriteLink(marker, settings, "ComponentLabel", false);
                    Append(currentSpace, transaction, marker);
                    created++;
                }

                Table table = CreateComponentTable(
                    database,
                    settings,
                    snapshot.Sources);
                table.LayerId = layerId;
                WriteLink(table, settings, "ComponentRegister", false);
                Append(currentSpace, transaction, table);
                table.GenerateLayout();
                created++;

                transaction.Commit();
                return created;
            }
        }

        private static Table CreateComponentTable(
            Database database,
            DetailedSectionSettings settings,
            IList<DetailedSectionSource> sources)
        {
            var table = new Table();
            table.SetDatabaseDefaults(database);
            table.TableStyle = database.Tablestyle;
            table.Position = settings.InsertionPoint + new Vector3d(
                0.0,
                -settings.TextHeight * 7.0,
                0.0);
            const int columns = 5;
            table.SetSize(sources.Count + 2, columns);
            table.SetRowHeight(Math.Max(settings.TextHeight * 1.7, 2.5));
            table.SetColumnWidth(Math.Max(settings.TextHeight * 7.0, 12.0));
            table.Cells[0, 0].TextString = "LINKED SECTION COMPONENT REGISTER";
            table.MergeCells(CellRange.Create(table, 0, 0, 0, columns - 1));
            string[] headings = { "ITEM", "TYPE", "LAYER", "MEASURE", "HANDLE" };
            for (int column = 0; column < columns; column++)
                table.Cells[1, column].TextString = headings[column];
            for (int index = 0; index < sources.Count; index++)
            {
                DetailedSectionSource source = sources[index];
                int row = index + 2;
                table.Cells[row, 0].TextString = "D" +
                    (index + 1).ToString(CultureInfo.InvariantCulture);
                table.Cells[row, 1].TextString = source.TypeName;
                table.Cells[row, 2].TextString = source.Layer;
                table.Cells[row, 3].TextString = source.Measure;
                table.Cells[row, 4].TextString = source.Handle;
            }
            return table;
        }

        private static MText CreateMText(
            Database database,
            Point3d location,
            double height,
            string contents,
            AttachmentPoint attachment)
        {
            var text = new MText();
            text.SetDatabaseDefaults(database);
            text.Location = location;
            text.TextHeight = Math.Max(height, Tolerance);
            text.Contents = contents;
            text.Attachment = attachment;
            return text;
        }

        private static void Append(
            BlockTableRecord currentSpace,
            Transaction transaction,
            Entity entity)
        {
            currentSpace.AppendEntity(entity);
            transaction.AddNewlyCreatedDBObject(entity, true);
        }

        private static void WriteLink(
            Entity entity,
            DetailedSectionSettings settings,
            string role,
            bool includeSources)
        {
            var values = new List<TypedValue>
            {
                new TypedValue(
                    (int)DxfCode.ExtendedDataRegAppName,
                    RegAppName),
                new TypedValue(
                    (int)DxfCode.ExtendedDataAsciiString,
                    "Set=" + settings.SetId),
                new TypedValue(
                    (int)DxfCode.ExtendedDataAsciiString,
                    "Role=" + role),
                new TypedValue(
                    (int)DxfCode.ExtendedDataAsciiString,
                    "Discipline=" + settings.Discipline),
                new TypedValue(
                    (int)DxfCode.ExtendedDataAsciiString,
                    "TextHeight=" + settings.TextHeight.ToString(
                        "R",
                        CultureInfo.InvariantCulture)),
                new TypedValue(
                    (int)DxfCode.ExtendedDataAsciiString,
                    "Offset=" + settings.DimensionOffset.ToString(
                        "R",
                        CultureInfo.InvariantCulture)),
                new TypedValue(
                    (int)DxfCode.ExtendedDataAsciiString,
                    "InsertX=" + settings.InsertionPoint.X.ToString(
                        "R",
                        CultureInfo.InvariantCulture)),
                new TypedValue(
                    (int)DxfCode.ExtendedDataAsciiString,
                    "InsertY=" + settings.InsertionPoint.Y.ToString(
                        "R",
                        CultureInfo.InvariantCulture)),
                new TypedValue(
                    (int)DxfCode.ExtendedDataAsciiString,
                    "InsertZ=" + settings.InsertionPoint.Z.ToString(
                        "R",
                        CultureInfo.InvariantCulture))
            };
            if (includeSources)
            {
                foreach (string handle in settings.SourceHandles)
                {
                    values.Add(new TypedValue(
                        (int)DxfCode.ExtendedDataAsciiString,
                        "Source=" + handle));
                }
            }
            entity.XData = new ResultBuffer(values.ToArray());
        }

        private static bool PromptLinkedSet(
            Document document,
            out string setId)
        {
            setId = string.Empty;
            var options = new PromptEntityOptions(
                "\nSelect any CE detailed-section annotation object: ");
            options.SetRejectMessage(
                "\nSelect a linked CE detailed-section dimension, label, note or table.");
            PromptEntityResult result = document.Editor.GetEntity(options);
            if (result.Status != PromptStatus.OK) return false;
            using (Transaction transaction =
                document.Database.TransactionManager.StartTransaction())
            {
                Entity entity = transaction.GetObject(
                    result.ObjectId,
                    OpenMode.ForRead,
                    false) as Entity;
                if (entity == null || !TryReadSetId(entity, out setId))
                {
                    document.Editor.WriteMessage(
                        "\nThe selected object is not linked to a CE detailed-section annotation set.");
                    return false;
                }
            }
            return true;
        }

        private static bool ReadSet(
            Database database,
            string setId,
            out DetailedSectionSettings settings,
            out List<ObjectId> liveSources,
            out int missing)
        {
            settings = null;
            liveSources = new List<ObjectId>();
            missing = 0;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTableRecord currentSpace = transaction.GetObject(
                    database.CurrentSpaceId,
                    OpenMode.ForRead,
                    false) as BlockTableRecord;
                if (currentSpace == null) return false;
                Entity anchor = null;
                foreach (ObjectId objectId in currentSpace)
                {
                    Entity entity = transaction.GetObject(
                        objectId,
                        OpenMode.ForRead,
                        false) as Entity;
                    string candidateSet;
                    string role;
                    if (entity == null ||
                        !TryReadSetId(entity, out candidateSet) ||
                        !Equal(candidateSet, setId) ||
                        !TryReadValue(entity, "Role=", out role) ||
                        !Equal(role, "Anchor"))
                        continue;
                    anchor = entity;
                    break;
                }
                if (anchor == null) return false;
                settings = ReadSettings(anchor);
                if (settings == null) return false;
                foreach (string handle in settings.SourceHandles)
                {
                    ObjectId id;
                    if (TryResolveHandle(database, handle, out id))
                        liveSources.Add(id);
                    else
                        missing++;
                }
            }
            return true;
        }

        private static DetailedSectionSettings ReadSettings(Entity anchor)
        {
            string setId;
            string disciplineText;
            string textHeightText;
            string offsetText;
            string insertXText;
            string insertYText;
            string insertZText;
            if (!TryReadSetId(anchor, out setId) ||
                !TryReadValue(anchor, "Discipline=", out disciplineText) ||
                !TryReadValue(anchor, "TextHeight=", out textHeightText) ||
                !TryReadValue(anchor, "Offset=", out offsetText) ||
                !TryReadValue(anchor, "InsertX=", out insertXText) ||
                !TryReadValue(anchor, "InsertY=", out insertYText) ||
                !TryReadValue(anchor, "InsertZ=", out insertZText))
                return null;

            SectionDetailDiscipline discipline;
            double textHeight;
            double offset;
            double x;
            double y;
            double z;
            if (!Enum.TryParse(disciplineText, true, out discipline) ||
                !TryParse(textHeightText, out textHeight) ||
                !TryParse(offsetText, out offset) ||
                !TryParse(insertXText, out x) ||
                !TryParse(insertYText, out y) ||
                !TryParse(insertZText, out z) ||
                textHeight <= Tolerance || offset <= Tolerance)
                return null;

            var sources = new List<string>();
            ResultBuffer data = anchor.GetXDataForApplication(RegAppName);
            if (data != null)
            {
                foreach (TypedValue value in data)
                {
                    string text = value.Value as string;
                    if (!string.IsNullOrWhiteSpace(text) &&
                        text.StartsWith("Source=", StringComparison.OrdinalIgnoreCase))
                        sources.Add(text.Substring("Source=".Length));
                }
            }
            return sources.Count == 0
                ? null
                : new DetailedSectionSettings(
                    setId,
                    discipline,
                    textHeight,
                    offset,
                    new Point3d(x, y, z),
                    sources);
        }

        private static List<ObjectId> ReadSetEntityIds(
            Database database,
            string setId)
        {
            var result = new List<ObjectId>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTableRecord currentSpace = transaction.GetObject(
                    database.CurrentSpaceId,
                    OpenMode.ForRead,
                    false) as BlockTableRecord;
                if (currentSpace == null) return result;
                foreach (ObjectId objectId in currentSpace)
                {
                    Entity entity = transaction.GetObject(
                        objectId,
                        OpenMode.ForRead,
                        false) as Entity;
                    string candidateSet;
                    if (entity != null &&
                        TryReadSetId(entity, out candidateSet) &&
                        Equal(candidateSet, setId))
                        result.Add(objectId);
                }
            }
            return result;
        }

        private static int EraseSet(
            Database database,
            string setId)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTableRecord currentSpace = transaction.GetObject(
                    database.CurrentSpaceId,
                    OpenMode.ForWrite,
                    false) as BlockTableRecord;
                if (currentSpace == null) return 0;
                int erased = 0;
                foreach (ObjectId objectId in currentSpace.Cast<ObjectId>().ToList())
                {
                    Entity entity = transaction.GetObject(
                        objectId,
                        OpenMode.ForRead,
                        false) as Entity;
                    string candidateSet;
                    if (entity == null ||
                        !TryReadSetId(entity, out candidateSet) ||
                        !Equal(candidateSet, setId))
                        continue;
                    entity.UpgradeOpen();
                    entity.Erase();
                    erased++;
                }
                transaction.Commit();
                return erased;
            }
        }

        private static bool TryReadSetId(
            Entity entity,
            out string setId)
        {
            return TryReadValue(entity, "Set=", out setId);
        }

        private static bool TryReadValue(
            Entity entity,
            string prefix,
            out string value)
        {
            value = string.Empty;
            ResultBuffer data = entity.GetXDataForApplication(RegAppName);
            if (data == null) return false;
            foreach (TypedValue item in data)
            {
                string text = item.Value as string;
                if (!string.IsNullOrWhiteSpace(text) &&
                    text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    value = text.Substring(prefix.Length);
                    return true;
                }
            }
            return false;
        }

        private static bool TryResolveHandle(
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
                objectId = database.GetObjectId(
                    false,
                    new Handle(value),
                    0);
                return !objectId.IsNull && !objectId.IsErased;
            }
            catch
            {
                return false;
            }
        }

        private static bool PromptDiscipline(
            Editor editor,
            out SectionDetailDiscipline discipline)
        {
            discipline = SectionDetailDiscipline.Road;
            var options = new PromptKeywordOptions(
                "\nDetailed section discipline [Road/Parking/Stormwater/Sewer/Water] <Road>: ")
            {
                AllowNone = true
            };
            options.Keywords.Add("Road");
            options.Keywords.Add("Parking");
            options.Keywords.Add("Stormwater");
            options.Keywords.Add("Sewer");
            options.Keywords.Add("Water");
            PromptResult result = editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel) return false;
            string value = result.Status == PromptStatus.OK
                ? result.StringResult
                : "Road";
            return Enum.TryParse(value, true, out discipline);
        }

        private static bool PromptPositiveDouble(
            Editor editor,
            string label,
            double defaultValue,
            out double value)
        {
            var options = new PromptDoubleOptions(
                "\n" + label + " <" +
                defaultValue.ToString("0.###", CultureInfo.InvariantCulture) +
                ">: ")
            {
                AllowNone = true,
                AllowNegative = false,
                AllowZero = false,
                DefaultValue = defaultValue,
                UseDefaultValue = true
            };
            PromptDoubleResult result = editor.GetDouble(options);
            if (result.Status == PromptStatus.Cancel)
            {
                value = defaultValue;
                return false;
            }
            value = result.Status == PromptStatus.OK
                ? result.Value
                : defaultValue;
            return result.Status == PromptStatus.OK ||
                   result.Status == PromptStatus.None;
        }

        private static string DisciplineTitle(
            SectionDetailDiscipline discipline)
        {
            switch (discipline)
            {
                case SectionDetailDiscipline.Parking:
                    return "PARKING / DRIVEWAY TYPICAL SECTION";
                case SectionDetailDiscipline.Stormwater:
                    return "STORMWATER TRENCH / PIPE TYPICAL SECTION";
                case SectionDetailDiscipline.Sewer:
                    return "SEWER TRENCH / PIPE TYPICAL SECTION";
                case SectionDetailDiscipline.Water:
                    return "WATER TRENCH / PIPE TYPICAL SECTION";
                default:
                    return "ROAD TYPICAL SECTION";
            }
        }

        private static string DisciplineNote(
            SectionDetailDiscipline discipline)
        {
            switch (discipline)
            {
                case SectionDetailDiscipline.Parking:
                    return "VERIFY PAVING/LAYERWORKS, CROSSFALL, KERBS, ACCESSIBILITY, DRAINAGE AND TIE-INS.";
                case SectionDetailDiscipline.Stormwater:
                    return "VERIFY PIPE/CULVERT CLASS, BEDDING, COVER, TRENCH WIDTH, STRUCTURES, INLET/OUTLET CONTROL AND HYDRAULIC DESIGN.";
                case SectionDetailDiscipline.Sewer:
                    return "VERIFY PIPE CLASS, GRADE, BEDDING, COVER, TRENCH SUPPORT, BACKFILL AND MANHOLE CONNECTIONS.";
                case SectionDetailDiscipline.Water:
                    return "VERIFY PIPE CLASS, BEDDING, COVER, THRUST RESTRAINT, VALVES, FITTINGS AND TESTING REQUIREMENTS.";
                default:
                    return "VERIFY PAVEMENT LAYERS, CROSSFALL, KERBS, SHOULDERS, DRAINAGE, BATTERS AND TIE-INS.";
            }
        }

        private static string PipeLabel(
            SectionDetailDiscipline discipline)
        {
            switch (discipline)
            {
                case SectionDetailDiscipline.Stormwater:
                    return "STORMWATER PIPE";
                case SectionDetailDiscipline.Sewer:
                    return "SEWER PIPE";
                case SectionDetailDiscipline.Water:
                    return "WATER PIPE";
                default:
                    return "CIRCULAR ELEMENT";
            }
        }

        private static void EnsureRegApp(
            Database database,
            Transaction transaction)
        {
            RegAppTable table = transaction.GetObject(
                database.RegAppTableId,
                OpenMode.ForRead,
                false) as RegAppTable;
            if (table == null || table.Has(RegAppName)) return;
            table.UpgradeOpen();
            var record = new RegAppTableRecord { Name = RegAppName };
            table.Add(record);
            transaction.AddNewlyCreatedDBObject(record, true);
        }

        private static ObjectId GetOrCreateLayer(
            Database database,
            Transaction transaction,
            string name)
        {
            LayerTable table = transaction.GetObject(
                database.LayerTableId,
                OpenMode.ForRead,
                false) as LayerTable;
            if (table == null)
                throw new InvalidOperationException(
                    "The layer table could not be opened.");
            if (table.Has(name)) return table[name];
            table.UpgradeOpen();
            var layer = new LayerTableRecord { Name = name };
            ObjectId id = table.Add(layer);
            transaction.AddNewlyCreatedDBObject(layer, true);
            return id;
        }

        private static bool TryParse(
            string text,
            out double value)
        {
            return double.TryParse(
                text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out value);
        }

        private static string FormatPoint(Point3d point)
        {
            return string.Format(
                CultureInfo.CurrentCulture,
                "X {0:N3}; Y {1:N3}; Z {2:N3}",
                point.X,
                point.Y,
                point.Z);
        }

        private static bool Equal(string first, string second)
        {
            return string.Equals(
                first,
                second,
                StringComparison.OrdinalIgnoreCase);
        }

        private static KeyValuePair<string, string> Pair(
            string key,
            string value)
        {
            return new KeyValuePair<string, string>(key, value);
        }

        private static Document ActiveDocument()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
        }
    }

    internal enum SectionDetailDiscipline
    {
        Road,
        Parking,
        Stormwater,
        Sewer,
        Water
    }

    internal sealed class DetailedSectionSettings
    {
        public DetailedSectionSettings(
            string setId,
            SectionDetailDiscipline discipline,
            double textHeight,
            double dimensionOffset,
            Point3d insertionPoint,
            IList<string> sourceHandles)
        {
            SetId = setId;
            Discipline = discipline;
            TextHeight = textHeight;
            DimensionOffset = dimensionOffset;
            InsertionPoint = insertionPoint;
            SourceHandles = sourceHandles == null
                ? new List<string>()
                : new List<string>(sourceHandles);
        }

        public string SetId { get; private set; }
        public SectionDetailDiscipline Discipline { get; private set; }
        public double TextHeight { get; private set; }
        public double DimensionOffset { get; private set; }
        public Point3d InsertionPoint { get; private set; }
        public IList<string> SourceHandles { get; private set; }

        public DetailedSectionSettings WithSources(IList<string> sourceHandles)
        {
            return new DetailedSectionSettings(
                SetId,
                Discipline,
                TextHeight,
                DimensionOffset,
                InsertionPoint,
                sourceHandles);
        }
    }

    internal sealed class DetailedSectionSnapshot
    {
        public DetailedSectionSnapshot(
            Extents3d extents,
            IList<DetailedSectionSource> sources)
        {
            Extents = extents;
            Sources = sources == null
                ? new List<DetailedSectionSource>()
                : new List<DetailedSectionSource>(sources);
        }

        public Extents3d Extents { get; private set; }
        public IList<DetailedSectionSource> Sources { get; private set; }
        public double Width
        {
            get { return Extents.MaxPoint.X - Extents.MinPoint.X; }
        }
        public double Height
        {
            get { return Extents.MaxPoint.Y - Extents.MinPoint.Y; }
        }
    }

    internal sealed class DetailedSectionSource
    {
        public DetailedSectionSource(
            ObjectId objectId,
            string handle,
            string typeName,
            string layer,
            string measure,
            double diameter,
            Point3d centre)
        {
            ObjectId = objectId;
            Handle = handle;
            TypeName = typeName;
            Layer = layer;
            Measure = measure;
            Diameter = diameter;
            Centre = centre;
        }

        public ObjectId ObjectId { get; private set; }
        public string Handle { get; private set; }
        public string TypeName { get; private set; }
        public string Layer { get; private set; }
        public string Measure { get; private set; }
        public double Diameter { get; private set; }
        public Point3d Centre { get; private set; }
    }
}

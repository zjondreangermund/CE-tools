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

[assembly: CommandClass(typeof(CETools.Civil3D.August12SurveySiteGridCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Creates a linked survey/site coordinate grid from a closed rectangular
    /// polyline. The selected boundary remains the frame. Generated grid lines
    /// are lightweight polylines, labels remain inside the frame and optional
    /// DBPoint intersections act as movable linked grid controls.
    /// </summary>
    public sealed class August12SurveySiteGridCommands
    {
        internal const string ParentKey = "CE_SITE_GRID_PARENT";
        internal const string ChildKey = "CE_SITE_GRID_CHILD";

        [CommandMethod("CE_TOOLS", "CE_SITEGRID", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void CreateOrUpdateSiteGrid()
        {
            August12SiteGridRuntimeManager.Initialize();
            Document document = Active();
            if (document == null) return;

            var options = new PromptEntityOptions(
                "\nSelect the closed site rectangle/polyline to use as the CE site-grid frame: ");
            options.SetRejectMessage("\nSelect a closed lightweight polyline.");
            options.AddAllowedClass(typeof(Polyline), true);
            PromptEntityResult selected = document.Editor.GetEntity(options);
            if (selected.Status != PromptStatus.OK) return;

            SiteGridSettings current = SiteGridSettings.Default();
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                Polyline preview = transaction.GetObject(
                    selected.ObjectId,
                    OpenMode.ForRead,
                    false) as Polyline;
                if (!ValidateBoundary(preview, document.Editor)) return;
                SiteGridSettings stored;
                if (TryReadParentLink(preview, transaction, out stored))
                    current = stored;
            }

            var model = new ProductionSettingsDialogModel(
                "CE Tools - Survey Site Grid",
                "The selected closed rectangle remains the linked frame. Grid lines are polylines. " +
                "Move the frame, a generated grid line or a generated grid point and CE Tools refreshes the complete linked grid.");
            model.AddPositiveDouble(
                "SpacingX",
                "Grid",
                "Horizontal / X spacing",
                current.SpacingX,
                "Drawing-unit spacing between vertical grid lines.");
            model.AddPositiveDouble(
                "SpacingY",
                "Grid",
                "Vertical / Y spacing",
                current.SpacingY,
                "Drawing-unit spacing between horizontal grid lines.");
            model.AddChoice(
                "AxisOrder",
                "Grid",
                "Coordinate display",
                current.ReverseXY
                    ? "Reverse X / Y labels"
                    : "Normal X / Y labels",
                "Normal: vertical lines show X and horizontal lines show Y. Reverse swaps the X/Y label convention without changing geometry.",
                new[] { "Normal X / Y labels", "Reverse X / Y labels" });
            model.AddChoice(
                "TextHeight",
                "Annotation",
                "Paper text height",
                current.PaperTextHeight.ToString("0.0##", CultureInfo.InvariantCulture) + " mm",
                "Coordinate values are placed just inside the bottom and right frame edges.",
                new[] { "1.8 mm", "2.0 mm", "2.5 mm", "3.5 mm", "5.0 mm" });
            model.AddChoice(
                "Points",
                "Grid",
                "Linked grid intersection points",
                current.CreatePoints ? "Create points" : "Lines only",
                "When points are enabled, moving any generated grid point moves the complete linked grid.",
                new[] { "Create points", "Lines only" });

            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            var settings = new SiteGridSettings
            {
                SpacingX = model.Double("SpacingX", current.SpacingX),
                SpacingY = model.Double("SpacingY", current.SpacingY),
                ReverseXY = string.Equals(
                    model.Text("AxisOrder"),
                    "Reverse X / Y labels",
                    StringComparison.OrdinalIgnoreCase),
                PaperTextHeight = ParsePaperHeight(
                    model.Text("TextHeight"),
                    current.PaperTextHeight),
                CreatePoints = string.Equals(
                    model.Text("Points"),
                    "Create points",
                    StringComparison.OrdinalIgnoreCase)
            };

            int generated = 0;
            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                Polyline boundary = transaction.GetObject(
                    selected.ObjectId,
                    OpenMode.ForWrite,
                    false) as Polyline;
                if (!ValidateBoundary(boundary, document.Editor)) return;
                WriteParentLink(boundary, transaction, settings);
                generated = RebuildOne(
                    document.Database,
                    transaction,
                    boundary,
                    settings,
                    null);
                transaction.Commit();
            }

            August21DisplayRefresh.Flush(document);
            document.Editor.WriteMessage(
                "\nCE_SITEGRID complete. Linked objects regenerated={0}. Move the frame/grid line/grid point to auto-refresh.",
                generated);
        }

        [CommandMethod("CE_TOOLS", "CE_SITEGRIDREFRESH", CommandFlags.Modal | CommandFlags.Redraw)]
        public void RefreshSiteGrids()
        {
            August12SiteGridRuntimeManager.Initialize();
            Document document = Active();
            if (document == null) return;
            int refreshed = RefreshAll(document, null);
            August21DisplayRefresh.Flush(document);
            document.Editor.WriteMessage(
                "\nCE_SITEGRIDREFRESH complete. Linked site grids refreshed={0}.",
                refreshed);
        }

        [CommandMethod("CE_TOOLS", "CE_SITEGRIDREMOVE", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void RemoveSiteGrid()
        {
            Document document = Active();
            if (document == null) return;

            var options = new PromptEntityOptions(
                "\nSelect the CE site-grid frame to remove its generated grid objects: ");
            options.SetRejectMessage("\nSelect the linked closed polyline frame.");
            options.AddAllowedClass(typeof(Polyline), true);
            PromptEntityResult selected = document.Editor.GetEntity(options);
            if (selected.Status != PromptStatus.OK) return;

            int removed = 0;
            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                Polyline boundary = transaction.GetObject(
                    selected.ObjectId,
                    OpenMode.ForWrite,
                    false) as Polyline;
                SiteGridSettings settings;
                if (boundary == null || !TryReadParentLink(boundary, transaction, out settings))
                {
                    document.Editor.WriteMessage("\nCE_SITEGRIDREMOVE: the selected polyline is not a linked CE site grid.");
                    return;
                }

                removed = EraseChildren(
                    document.Database,
                    transaction,
                    boundary.Handle.ToString());
                RemoveRecord(boundary, transaction, ParentKey);
                transaction.Commit();
            }

            August21DisplayRefresh.Flush(document);
            document.Editor.WriteMessage(
                "\nCE_SITEGRIDREMOVE complete. Generated grid objects removed={0}; source frame retained.",
                removed);
        }

        internal static int RefreshAll(Document document, ISet<ObjectId> dirtyIds)
        {
            if (document == null) return 0;
            int refreshed = 0;
            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord modelSpace = OpenModelSpace(
                    document.Database,
                    transaction,
                    OpenMode.ForRead);
                if (modelSpace == null) return 0;

                List<ObjectId> ids = modelSpace.Cast<ObjectId>().ToList();
                foreach (ObjectId id in ids)
                {
                    Polyline boundary;
                    try
                    {
                        boundary = transaction.GetObject(
                            id,
                            OpenMode.ForRead,
                            false) as Polyline;
                    }
                    catch
                    {
                        continue;
                    }
                    if (boundary == null || boundary.IsErased) continue;

                    SiteGridSettings settings;
                    if (!TryReadParentLink(boundary, transaction, out settings))
                        continue;

                    if (!boundary.IsWriteEnabled)
                        boundary.UpgradeOpen();
                    RebuildOne(
                        document.Database,
                        transaction,
                        boundary,
                        settings,
                        dirtyIds);
                    refreshed++;
                }
                transaction.Commit();
            }
            return refreshed;
        }

        private static int RebuildOne(
            Database database,
            Transaction transaction,
            Polyline boundary,
            SiteGridSettings settings,
            ISet<ObjectId> dirtyIds)
        {
            if (database == null || transaction == null || boundary == null)
                return 0;

            string parentHandle = boundary.Handle.ToString();
            GridBounds bounds = ReadBounds(boundary);
            if (!bounds.IsValid) return 0;

            Vector3d shift;
            if (dirtyIds != null &&
                !dirtyIds.Contains(boundary.ObjectId) &&
                TryReadChildTranslation(
                    database,
                    transaction,
                    parentHandle,
                    bounds,
                    settings,
                    dirtyIds,
                    out shift) &&
                shift.Length > 1e-8)
            {
                boundary.TransformBy(Matrix3d.Displacement(shift));
                bounds = ReadBounds(boundary);
            }

            EraseChildren(database, transaction, parentHandle);

            List<double> xValues = BuildPositions(
                bounds.MinX,
                bounds.MaxX,
                settings.SpacingX);
            List<double> yValues = BuildPositions(
                bounds.MinY,
                bounds.MaxY,
                settings.SpacingY);
            if (xValues.Count < 2 || yValues.Count < 2)
                return 0;

            long pointCount = (long)xValues.Count * (long)yValues.Count;
            if (pointCount > 10000L)
                throw new InvalidOperationException(
                    "The requested site grid would create more than 10,000 grid intersections. Increase the X/Y spacing.");

            BlockTableRecord modelSpace = OpenModelSpace(
                database,
                transaction,
                OpenMode.ForWrite);
            if (modelSpace == null) return 0;

            int created = 0;
            for (int xIndex = 1; xIndex < xValues.Count - 1; xIndex++)
            {
                Polyline line = CreateLine(
                    database,
                    boundary,
                    new Point2d(xValues[xIndex], bounds.MinY),
                    new Point2d(xValues[xIndex], bounds.MaxY));
                Append(modelSpace, transaction, line);
                WriteChildLink(
                    line,
                    transaction,
                    parentHandle,
                    "V",
                    xIndex,
                    -1);
                created++;
            }

            for (int yIndex = 1; yIndex < yValues.Count - 1; yIndex++)
            {
                Polyline line = CreateLine(
                    database,
                    boundary,
                    new Point2d(bounds.MinX, yValues[yIndex]),
                    new Point2d(bounds.MaxX, yValues[yIndex]));
                Append(modelSpace, transaction, line);
                WriteChildLink(
                    line,
                    transaction,
                    parentHandle,
                    "H",
                    -1,
                    yIndex);
                created++;
            }

            double modelTextHeight = ModelTextHeight(
                database,
                settings.PaperTextHeight);
            double insideOffset = Math.Max(modelTextHeight * 1.35, 0.001);

            for (int xIndex = 0; xIndex < xValues.Count; xIndex++)
            {
                string prefix = settings.ReverseXY ? "Y: " : "X: ";
                MText label = CreateLabel(
                    database,
                    boundary,
                    prefix + xValues[xIndex].ToString("0.###", CultureInfo.InvariantCulture),
                    new Point3d(
                        xValues[xIndex],
                        bounds.MinY + insideOffset,
                        bounds.Elevation),
                    modelTextHeight,
                    0.0);
                Append(modelSpace, transaction, label);
                WriteChildLink(
                    label,
                    transaction,
                    parentHandle,
                    "LX",
                    xIndex,
                    -1);
                created++;
            }

            for (int yIndex = 0; yIndex < yValues.Count; yIndex++)
            {
                string prefix = settings.ReverseXY ? "X: " : "Y: ";
                MText label = CreateLabel(
                    database,
                    boundary,
                    prefix + yValues[yIndex].ToString("0.###", CultureInfo.InvariantCulture),
                    new Point3d(
                        bounds.MaxX - insideOffset,
                        yValues[yIndex],
                        bounds.Elevation),
                    modelTextHeight,
                    Math.PI / 2.0);
                Append(modelSpace, transaction, label);
                WriteChildLink(
                    label,
                    transaction,
                    parentHandle,
                    "LY",
                    -1,
                    yIndex);
                created++;
            }

            if (settings.CreatePoints)
            {
                for (int xIndex = 0; xIndex < xValues.Count; xIndex++)
                {
                    for (int yIndex = 0; yIndex < yValues.Count; yIndex++)
                    {
                        var point = new DBPoint(
                            new Point3d(
                                xValues[xIndex],
                                yValues[yIndex],
                                bounds.Elevation));
                        point.SetDatabaseDefaults(database);
                        point.LayerId = boundary.LayerId;
                        Append(modelSpace, transaction, point);
                        WriteChildLink(
                            point,
                            transaction,
                            parentHandle,
                            "P",
                            xIndex,
                            yIndex);
                        created++;
                    }
                }
            }

            try { boundary.RecordGraphicsModified(true); } catch { }
            return created;
        }

        private static bool TryReadChildTranslation(
            Database database,
            Transaction transaction,
            string parentHandle,
            GridBounds bounds,
            SiteGridSettings settings,
            ISet<ObjectId> dirtyIds,
            out Vector3d shift)
        {
            shift = new Vector3d(0.0, 0.0, 0.0);
            if (dirtyIds == null || dirtyIds.Count == 0)
                return false;

            List<double> xValues = BuildPositions(
                bounds.MinX,
                bounds.MaxX,
                settings.SpacingX);
            List<double> yValues = BuildPositions(
                bounds.MinY,
                bounds.MaxY,
                settings.SpacingY);

            foreach (ObjectId id in dirtyIds)
            {
                if (id.IsNull || id.IsErased) continue;
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
                    continue;
                }
                if (entity == null) continue;

                SiteGridChildLink link;
                if (!TryReadChildLink(entity, transaction, out link) ||
                    !string.Equals(
                        link.ParentHandle,
                        parentHandle,
                        StringComparison.OrdinalIgnoreCase))
                    continue;

                Point3d expected;
                Point3d actual;
                if (string.Equals(link.Role, "P", StringComparison.OrdinalIgnoreCase))
                {
                    if (link.XIndex < 0 || link.XIndex >= xValues.Count ||
                        link.YIndex < 0 || link.YIndex >= yValues.Count)
                        continue;
                    DBPoint point = entity as DBPoint;
                    if (point == null) continue;
                    expected = new Point3d(
                        xValues[link.XIndex],
                        yValues[link.YIndex],
                        bounds.Elevation);
                    actual = point.Position;
                }
                else if (string.Equals(link.Role, "V", StringComparison.OrdinalIgnoreCase))
                {
                    if (link.XIndex <= 0 || link.XIndex >= xValues.Count - 1)
                        continue;
                    Polyline line = entity as Polyline;
                    if (line == null || line.NumberOfVertices < 1) continue;
                    expected = new Point3d(
                        xValues[link.XIndex],
                        bounds.MinY,
                        bounds.Elevation);
                    actual = line.GetPoint3dAt(0);
                }
                else if (string.Equals(link.Role, "H", StringComparison.OrdinalIgnoreCase))
                {
                    if (link.YIndex <= 0 || link.YIndex >= yValues.Count - 1)
                        continue;
                    Polyline line = entity as Polyline;
                    if (line == null || line.NumberOfVertices < 1) continue;
                    expected = new Point3d(
                        bounds.MinX,
                        yValues[link.YIndex],
                        bounds.Elevation);
                    actual = line.GetPoint3dAt(0);
                }
                else
                {
                    continue;
                }

                Vector3d candidate = actual - expected;
                if (candidate.Length > 1e-8)
                {
                    shift = new Vector3d(
                        candidate.X,
                        candidate.Y,
                        0.0);
                    return true;
                }
            }
            return false;
        }

        private static bool ValidateBoundary(
            Polyline boundary,
            Editor editor)
        {
            if (boundary == null || !boundary.Closed || boundary.NumberOfVertices < 4)
            {
                if (editor != null)
                    editor.WriteMessage(
                        "\nCE_SITEGRID requires a closed rectangular/site-boundary polyline with at least four vertices.");
                return false;
            }

            GridBounds bounds = ReadBounds(boundary);
            if (!bounds.IsValid)
            {
                if (editor != null)
                    editor.WriteMessage(
                        "\nCE_SITEGRID: the selected boundary has no usable X/Y size.");
                return false;
            }
            return true;
        }

        private static GridBounds ReadBounds(Polyline boundary)
        {
            var result = new GridBounds();
            try
            {
                Extents3d extents = boundary.GeometricExtents;
                result.MinX = extents.MinPoint.X;
                result.MinY = extents.MinPoint.Y;
                result.MaxX = extents.MaxPoint.X;
                result.MaxY = extents.MaxPoint.Y;
                result.Elevation = boundary.Elevation;
                result.IsValid =
                    result.MaxX - result.MinX > 1e-8 &&
                    result.MaxY - result.MinY > 1e-8;
            }
            catch
            {
                result.IsValid = false;
            }
            return result;
        }

        private static List<double> BuildPositions(
            double minimum,
            double maximum,
            double spacing)
        {
            var result = new List<double>();
            if (maximum <= minimum || spacing <= 0.0)
                return result;

            result.Add(minimum);
            double value = minimum + spacing;
            int guard = 0;
            while (value < maximum - 1e-8 && guard++ < 100000)
            {
                result.Add(value);
                value += spacing;
            }
            if (Math.Abs(result[result.Count - 1] - maximum) > 1e-8)
                result.Add(maximum);
            return result;
        }

        private static Polyline CreateLine(
            Database database,
            Polyline boundary,
            Point2d first,
            Point2d second)
        {
            var line = new Polyline(2);
            line.SetDatabaseDefaults(database);
            line.LayerId = boundary.LayerId;
            line.Elevation = boundary.Elevation;
            line.AddVertexAt(0, first, 0.0, 0.0, 0.0);
            line.AddVertexAt(1, second, 0.0, 0.0, 0.0);
            return line;
        }

        private static MText CreateLabel(
            Database database,
            Polyline boundary,
            string contents,
            Point3d location,
            double textHeight,
            double rotation)
        {
            var label = new MText();
            label.SetDatabaseDefaults(database);
            label.LayerId = boundary.LayerId;
            label.Location = location;
            label.TextHeight = Math.Max(textHeight, 0.001);
            label.Contents = contents ?? string.Empty;
            label.Attachment = AttachmentPoint.MiddleCenter;
            label.Rotation = rotation;
            return label;
        }

        private static double ModelTextHeight(
            Database database,
            double paperHeight)
        {
            try
            {
                return Math.Max(
                    PaperAnnotationScale.ModelTextHeight(
                        database,
                        paperHeight),
                    0.001);
            }
            catch
            {
                return Math.Max(paperHeight, 0.001);
            }
        }

        private static double ParsePaperHeight(
            string text,
            double fallback)
        {
            string value = (text ?? string.Empty)
                .Replace("mm", string.Empty)
                .Trim();
            double result;
            return ProductionSettingsDialogModel.TryDouble(value, out result) &&
                   result > 0.0
                ? result
                : fallback;
        }

        private static void Append(
            BlockTableRecord space,
            Transaction transaction,
            Entity entity)
        {
            space.AppendEntity(entity);
            transaction.AddNewlyCreatedDBObject(entity, true);
            entity.RecordGraphicsModified(true);
        }

        private static BlockTableRecord OpenModelSpace(
            Database database,
            Transaction transaction,
            OpenMode mode)
        {
            BlockTable table = transaction.GetObject(
                database.BlockTableId,
                OpenMode.ForRead,
                false) as BlockTable;
            if (table == null) return null;
            return transaction.GetObject(
                table[BlockTableRecord.ModelSpace],
                mode,
                false) as BlockTableRecord;
        }

        private static int EraseChildren(
            Database database,
            Transaction transaction,
            string parentHandle)
        {
            BlockTableRecord modelSpace = OpenModelSpace(
                database,
                transaction,
                OpenMode.ForRead);
            if (modelSpace == null) return 0;

            List<ObjectId> ids = modelSpace.Cast<ObjectId>().ToList();
            int removed = 0;
            foreach (ObjectId id in ids)
            {
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
                    continue;
                }
                if (entity == null || entity.IsErased) continue;

                SiteGridChildLink link;
                if (!TryReadChildLink(entity, transaction, out link) ||
                    !string.Equals(
                        link.ParentHandle,
                        parentHandle,
                        StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!entity.IsWriteEnabled)
                    entity.UpgradeOpen();
                entity.Erase();
                removed++;
            }
            return removed;
        }

        private static void WriteParentLink(
            Polyline boundary,
            Transaction transaction,
            SiteGridSettings settings)
        {
            WriteRecord(
                boundary,
                transaction,
                ParentKey,
                new[]
                {
                    new TypedValue((int)DxfCode.Text, "V1"),
                    new TypedValue((int)DxfCode.Real, settings.SpacingX),
                    new TypedValue((int)DxfCode.Real, settings.SpacingY),
                    new TypedValue((int)DxfCode.Int16, settings.ReverseXY ? 1 : 0),
                    new TypedValue((int)DxfCode.Real, settings.PaperTextHeight),
                    new TypedValue((int)DxfCode.Int16, settings.CreatePoints ? 1 : 0)
                });
        }

        private static bool TryReadParentLink(
            Polyline boundary,
            Transaction transaction,
            out SiteGridSettings settings)
        {
            settings = SiteGridSettings.Default();
            TypedValue[] values;
            if (!TryReadRecord(
                boundary,
                transaction,
                ParentKey,
                out values) ||
                values.Length < 6)
                return false;
            try
            {
                settings.SpacingX = Convert.ToDouble(
                    values[1].Value,
                    CultureInfo.InvariantCulture);
                settings.SpacingY = Convert.ToDouble(
                    values[2].Value,
                    CultureInfo.InvariantCulture);
                settings.ReverseXY = Convert.ToInt32(
                    values[3].Value,
                    CultureInfo.InvariantCulture) != 0;
                settings.PaperTextHeight = Convert.ToDouble(
                    values[4].Value,
                    CultureInfo.InvariantCulture);
                settings.CreatePoints = Convert.ToInt32(
                    values[5].Value,
                    CultureInfo.InvariantCulture) != 0;
                return settings.SpacingX > 0.0 &&
                       settings.SpacingY > 0.0 &&
                       settings.PaperTextHeight > 0.0;
            }
            catch
            {
                settings = SiteGridSettings.Default();
                return false;
            }
        }

        private static void WriteChildLink(
            Entity entity,
            Transaction transaction,
            string parentHandle,
            string role,
            int xIndex,
            int yIndex)
        {
            WriteRecord(
                entity,
                transaction,
                ChildKey,
                new[]
                {
                    new TypedValue((int)DxfCode.Text, parentHandle ?? string.Empty),
                    new TypedValue((int)DxfCode.Text, role ?? string.Empty),
                    new TypedValue((int)DxfCode.Text, xIndex.ToString(CultureInfo.InvariantCulture)),
                    new TypedValue((int)DxfCode.Text, yIndex.ToString(CultureInfo.InvariantCulture))
                });
        }

        private static bool TryReadChildLink(
            Entity entity,
            Transaction transaction,
            out SiteGridChildLink link)
        {
            link = new SiteGridChildLink();
            TypedValue[] values;
            if (!TryReadRecord(
                entity,
                transaction,
                ChildKey,
                out values) ||
                values.Length < 4)
                return false;

            link.ParentHandle = Convert.ToString(
                values[0].Value,
                CultureInfo.InvariantCulture);
            link.Role = Convert.ToString(
                values[1].Value,
                CultureInfo.InvariantCulture);
            int.TryParse(
                Convert.ToString(values[2].Value, CultureInfo.InvariantCulture),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out link.XIndex);
            int.TryParse(
                Convert.ToString(values[3].Value, CultureInfo.InvariantCulture),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out link.YIndex);
            return !string.IsNullOrWhiteSpace(link.ParentHandle) &&
                   !string.IsNullOrWhiteSpace(link.Role);
        }

        private static void WriteRecord(
            DBObject owner,
            Transaction transaction,
            string key,
            IEnumerable<TypedValue> values)
        {
            if (owner.ExtensionDictionary.IsNull)
                owner.CreateExtensionDictionary();
            DBDictionary dictionary = transaction.GetObject(
                owner.ExtensionDictionary,
                OpenMode.ForWrite,
                false) as DBDictionary;
            if (dictionary == null) return;

            Xrecord record;
            if (dictionary.Contains(key))
            {
                record = transaction.GetObject(
                    dictionary.GetAt(key),
                    OpenMode.ForWrite,
                    false) as Xrecord;
            }
            else
            {
                record = new Xrecord();
                dictionary.SetAt(key, record);
                transaction.AddNewlyCreatedDBObject(record, true);
            }
            if (record != null)
                record.Data = new ResultBuffer(values.ToArray());
        }

        private static bool TryReadRecord(
            DBObject owner,
            Transaction transaction,
            string key,
            out TypedValue[] values)
        {
            values = new TypedValue[0];
            if (owner == null || owner.ExtensionDictionary.IsNull)
                return false;
            try
            {
                DBDictionary dictionary = transaction.GetObject(
                    owner.ExtensionDictionary,
                    OpenMode.ForRead,
                    false) as DBDictionary;
                if (dictionary == null || !dictionary.Contains(key))
                    return false;
                Xrecord record = transaction.GetObject(
                    dictionary.GetAt(key),
                    OpenMode.ForRead,
                    false) as Xrecord;
                if (record == null || record.Data == null)
                    return false;
                values = record.Data.AsArray();
                return values.Length > 0;
            }
            catch
            {
                return false;
            }
        }

        private static void RemoveRecord(
            DBObject owner,
            Transaction transaction,
            string key)
        {
            if (owner == null || owner.ExtensionDictionary.IsNull)
                return;
            DBDictionary dictionary = transaction.GetObject(
                owner.ExtensionDictionary,
                OpenMode.ForWrite,
                false) as DBDictionary;
            if (dictionary == null || !dictionary.Contains(key))
                return;
            DBObject record = transaction.GetObject(
                dictionary.GetAt(key),
                OpenMode.ForWrite,
                false);
            record.Erase();
            dictionary.Remove(key);
        }

        private static Document Active()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
        }

        internal sealed class SiteGridSettings
        {
            internal double SpacingX;
            internal double SpacingY;
            internal bool ReverseXY;
            internal double PaperTextHeight;
            internal bool CreatePoints;

            internal static SiteGridSettings Default()
            {
                return new SiteGridSettings
                {
                    SpacingX = 20.0,
                    SpacingY = 20.0,
                    ReverseXY = false,
                    PaperTextHeight = 2.0,
                    CreatePoints = true
                };
            }
        }

        private struct GridBounds
        {
            internal double MinX;
            internal double MinY;
            internal double MaxX;
            internal double MaxY;
            internal double Elevation;
            internal bool IsValid;
        }

        private struct SiteGridChildLink
        {
            internal string ParentHandle;
            internal string Role;
            internal int XIndex;
            internal int YIndex;
        }
    }

    /// <summary>
    /// Deferred site-grid refresh. Linked objects are rebuilt only after Civil 3D
    /// finishes the editing command, which avoids modifying the database from an
    /// ObjectModified callback.
    /// </summary>
    internal static class August12SiteGridRuntimeManager
    {
        private static Document _document;
        private static bool _initialized;
        private static bool _busy;
        private static bool _pending;
        private static readonly HashSet<ObjectId> DirtyIds =
            new HashSet<ObjectId>();

        internal static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;
            AcApplication.DocumentManager.DocumentActivated += OnDocumentActivated;
            AcApplication.DocumentManager.DocumentCreated += OnDocumentActivated;
            AcApplication.DocumentManager.DocumentToBeDestroyed += OnDocumentDestroyed;
            AcApplication.Idle += OnIdle;
            Attach(AcApplication.DocumentManager.MdiActiveDocument);
        }

        internal static void Terminate()
        {
            if (!_initialized) return;
            _initialized = false;
            AcApplication.DocumentManager.DocumentActivated -= OnDocumentActivated;
            AcApplication.DocumentManager.DocumentCreated -= OnDocumentActivated;
            AcApplication.DocumentManager.DocumentToBeDestroyed -= OnDocumentDestroyed;
            AcApplication.Idle -= OnIdle;
            Detach();
        }

        private static void OnDocumentActivated(
            object sender,
            DocumentCollectionEventArgs args)
        {
            Attach(args == null ? null : args.Document);
        }

        private static void OnDocumentDestroyed(
            object sender,
            DocumentCollectionEventArgs args)
        {
            if (args != null && ReferenceEquals(args.Document, _document))
                Detach();
        }

        private static void Attach(Document document)
        {
            if (ReferenceEquals(document, _document))
                return;
            Detach();
            _document = document;
            if (_document == null) return;

            _document.Database.ObjectModified += OnObjectModified;
            _document.CommandEnded += OnCommandEnded;
        }

        private static void Detach()
        {
            if (_document != null)
            {
                try
                {
                    _document.Database.ObjectModified -= OnObjectModified;
                    _document.CommandEnded -= OnCommandEnded;
                }
                catch { }
            }
            _document = null;
            DirtyIds.Clear();
            _pending = false;
        }

        private static void OnObjectModified(
            object sender,
            ObjectEventArgs args)
        {
            if (_busy || _document == null || args == null || args.DBObject == null)
                return;

            DBObject value = args.DBObject;
            if (value.ObjectId.IsNull || value.IsErased || value.ExtensionDictionary.IsNull)
                return;

            DirtyIds.Add(value.ObjectId);
            _pending = true;
        }

        private static void OnCommandEnded(
            object sender,
            CommandEventArgs args)
        {
            if (_busy || DirtyIds.Count == 0)
                return;
            _pending = true;
        }

        private static void OnIdle(object sender, EventArgs args)
        {
            if (_busy || !_pending || _document == null || DirtyIds.Count == 0)
                return;
            if (!ReferenceEquals(
                AcApplication.DocumentManager.MdiActiveDocument,
                _document))
                return;

            string commands = Convert.ToString(
                AcApplication.GetSystemVariable("CMDNAMES"),
                CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(commands))
                return;

            var dirty = new HashSet<ObjectId>(DirtyIds);
            DirtyIds.Clear();
            _pending = false;
            _busy = true;
            try
            {
                int refreshed =
                    August12SurveySiteGridCommands.RefreshAll(
                        _document,
                        dirty);
                if (refreshed > 0)
                    August21DisplayRefresh.Flush(_document);
            }
            catch
            {
                // Dynamic refresh must never interrupt the active Civil 3D session.
            }
            finally
            {
                _busy = false;
            }
        }
    }
}

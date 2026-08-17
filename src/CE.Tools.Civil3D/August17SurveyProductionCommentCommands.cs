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

[assembly: CommandClass(typeof(CETools.Civil3D.August17SurveyProductionCommentCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Final 17 August 2026 Survey Production comment closure.
    /// Grid Setting-Out is a small front door with the two requested choices:
    /// normal grid setting-out for selected/multiple source polylines and a
    /// linked Site Grid that accepts preselection, individual selection, multiple
    /// selection or AutoCAD window/crossing-window selection.
    /// </summary>
    public sealed class August17SurveyProductionCommentCommands
    {
        [CommandMethod("CE_TOOLS", "CE_SURVEYGRIDSETTINGOUT", CommandFlags.Modal)]
        public void SurveyGridSettingOut()
        {
            Document document = Active();
            if (document == null) return;

            DisciplineWorkflowDialogs.SelectAndRun(
                document,
                "CE Tools - Grid Setting-Out",
                "Choose normal grid setting-out for selected/multiple polylines or create linked Site Grids from selected/multiple/window-selected closed polylines.",
                new List<DisciplineWorkflowAction>
                {
                    new DisciplineWorkflowAction(
                        "CE-Grid Setting-Out - Multiple / Selected Polylines",
                        "CE_GRIDSETTINGOUT",
                        "Use the existing linked grid setting-out engine on one or more selected polylines/feature lines with continuous numbering.",
                        "01 GRID SETTING-OUT"),
                    new DisciplineWorkflowAction(
                        "CE-Site Grid - Selected / Multiple Polylines / Window Selection",
                        "CE_SITEGRIDMULTI",
                        "Create or update linked site grids from preselected, individually selected, multiple or window-selected closed polylines.",
                        "02 SITE GRID")
                });
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_SITEGRIDMULTI",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void SiteGridMultiple()
        {
            August12SiteGridRuntimeManager.Initialize();
            Document document = Active();
            if (document == null) return;

            Editor editor = document.Editor;
            ObjectId[] candidates = ReadSelection(editor);
            if (candidates == null || candidates.Length == 0) return;

            var boundaries = new List<ObjectId>();
            int rejected = 0;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in candidates.Distinct())
                {
                    Polyline boundary = null;
                    try
                    {
                        boundary = transaction.GetObject(id, OpenMode.ForRead, false) as Polyline;
                    }
                    catch
                    {
                        rejected++;
                        continue;
                    }

                    if (IsUsableBoundary(boundary)) boundaries.Add(id);
                    else rejected++;
                }
            }

            if (boundaries.Count == 0)
            {
                editor.WriteMessage(
                    "\nCE_SITEGRIDMULTI cancelled. Select one or more closed site-boundary polylines with usable X/Y extents.");
                return;
            }

            August12SurveySiteGridCommands.SiteGridSettings current =
                August12SurveySiteGridCommands.SiteGridSettings.Default();
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                Polyline first = transaction.GetObject(
                    boundaries[0],
                    OpenMode.ForRead,
                    false) as Polyline;
                August12SurveySiteGridCommands.SiteGridSettings stored;
                if (TryReadParentSettings(first, transaction, out stored)) current = stored;
            }

            var model = new ProductionSettingsDialogModel(
                "CE Tools - Multiple / Window Site Grid",
                "One settings set is applied to every selected closed polyline. Preselection, individual selection, multiple selection and AutoCAD window/crossing-window selection are supported. Every boundary remains linked for automatic refresh.");
            model.AddPositiveDouble(
                "SpacingX", "01 Grid", "Horizontal / X spacing", current.SpacingX,
                "Drawing-unit spacing between vertical grid lines.");
            model.AddPositiveDouble(
                "SpacingY", "01 Grid", "Vertical / Y spacing", current.SpacingY,
                "Drawing-unit spacing between horizontal grid lines.");
            model.AddChoice(
                "AxisOrder", "01 Grid", "Coordinate display",
                current.ReverseXY ? "Reverse X / Y labels" : "Normal X / Y labels",
                "Normal shows X on vertical grid lines and Y on horizontal grid lines. Reverse swaps only the displayed convention.",
                new[] { "Normal X / Y labels", "Reverse X / Y labels" });
            model.AddChoice(
                "TextHeight", "02 Annotation", "Paper text height",
                current.PaperTextHeight.ToString("0.0##", CultureInfo.InvariantCulture) + " mm",
                "Absolute paper text height for the linked site-grid coordinate labels.",
                new[] { "1.8 mm", "2.0 mm", "2.5 mm", "3.5 mm", "5.0 mm" });
            model.AddChoice(
                "Points", "01 Grid", "Linked grid intersection points",
                current.CreatePoints ? "Create points" : "Lines only",
                "Create linked DBPoint intersections or grid lines/labels only.",
                new[] { "Create points", "Lines only" });

            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            var settings = new August12SurveySiteGridCommands.SiteGridSettings
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

            if (settings.SpacingX <= 0.0 || settings.SpacingY <= 0.0)
            {
                editor.WriteMessage("\nCE_SITEGRIDMULTI cancelled. X and Y spacing must be greater than zero.");
                return;
            }

            int completed = 0;
            int generated = 0;
            int failed = 0;

            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in boundaries)
                {
                    Polyline boundary = null;
                    try
                    {
                        boundary = transaction.GetObject(id, OpenMode.ForWrite, false) as Polyline;
                        if (!IsUsableBoundary(boundary))
                        {
                            failed++;
                            continue;
                        }

                        WriteParentSettings(boundary, transaction, settings);
                        generated += RebuildOne(
                            document.Database,
                            transaction,
                            boundary,
                            settings);
                        completed++;
                    }
                    catch
                    {
                        failed++;
                    }
                }
                transaction.Commit();
            }

            editor.Regen();
            editor.WriteMessage(
                "\nCE_SITEGRIDMULTI complete. Site grids={0}; generated linked objects={1}; rejected selections={2}; failed={3}. " +
                "Move a linked frame/grid line/grid point and CE Tools will refresh the grid.",
                completed,
                generated,
                rejected,
                failed);
        }

        private static ObjectId[] ReadSelection(Editor editor)
        {
            PromptSelectionResult selection = editor.SelectImplied();
            if (selection.Status == PromptStatus.OK &&
                selection.Value != null &&
                selection.Value.Count > 0)
            {
                return selection.Value.GetObjectIds();
            }

            var options = new PromptSelectionOptions
            {
                MessageForAdding =
                    "\nSelect one or more closed site-boundary polylines. Pick individually, select multiple, or use Window/Crossing: ",
                AllowDuplicates = false,
                RejectObjectsFromNonCurrentSpace = true
            };
            selection = editor.GetSelection(options);
            return selection.Status == PromptStatus.OK && selection.Value != null
                ? selection.Value.GetObjectIds()
                : new ObjectId[0];
        }

        private static bool IsUsableBoundary(Polyline boundary)
        {
            if (boundary == null || boundary.IsErased || !boundary.Closed || boundary.NumberOfVertices < 4)
                return false;
            try
            {
                Extents3d extents = boundary.GeometricExtents;
                return extents.MaxPoint.X - extents.MinPoint.X > 1e-8 &&
                       extents.MaxPoint.Y - extents.MinPoint.Y > 1e-8;
            }
            catch
            {
                return false;
            }
        }

        private static int RebuildOne(
            Database database,
            Transaction transaction,
            Polyline boundary,
            August12SurveySiteGridCommands.SiteGridSettings settings)
        {
            GridBounds bounds = ReadBounds(boundary);
            if (!bounds.IsValid) return 0;

            string parentHandle = boundary.Handle.ToString();
            EraseChildren(database, transaction, parentHandle);

            List<double> xValues = BuildPositions(bounds.MinX, bounds.MaxX, settings.SpacingX);
            List<double> yValues = BuildPositions(bounds.MinY, bounds.MaxY, settings.SpacingY);
            if (xValues.Count < 2 || yValues.Count < 2) return 0;

            long intersections = (long)xValues.Count * (long)yValues.Count;
            if (intersections > 10000L)
                throw new InvalidOperationException(
                    "A selected Site Grid would create more than 10,000 intersections. Increase the X/Y spacing.");

            BlockTableRecord modelSpace = OpenModelSpace(
                database,
                transaction,
                OpenMode.ForWrite);
            if (modelSpace == null) return 0;

            int created = 0;
            int xIndex;
            int yIndex;

            for (xIndex = 1; xIndex < xValues.Count - 1; xIndex++)
            {
                Polyline line = CreateLine(
                    database,
                    boundary,
                    new Point2d(xValues[xIndex], bounds.MinY),
                    new Point2d(xValues[xIndex], bounds.MaxY));
                Append(modelSpace, transaction, line);
                WriteChildLink(line, transaction, parentHandle, "V", xIndex, -1);
                created++;
            }

            for (yIndex = 1; yIndex < yValues.Count - 1; yIndex++)
            {
                Polyline line = CreateLine(
                    database,
                    boundary,
                    new Point2d(bounds.MinX, yValues[yIndex]),
                    new Point2d(bounds.MaxX, yValues[yIndex]));
                Append(modelSpace, transaction, line);
                WriteChildLink(line, transaction, parentHandle, "H", -1, yIndex);
                created++;
            }

            double textHeight = ModelTextHeight(database, settings.PaperTextHeight);
            double insideOffset = Math.Max(textHeight * 1.35, 0.001);

            for (xIndex = 0; xIndex < xValues.Count; xIndex++)
            {
                string prefix = settings.ReverseXY ? "Y: " : "X: ";
                MText label = CreateLabel(
                    database,
                    boundary,
                    prefix + xValues[xIndex].ToString("0.###", CultureInfo.InvariantCulture),
                    new Point3d(xValues[xIndex], bounds.MinY + insideOffset, bounds.Elevation),
                    textHeight,
                    0.0);
                Append(modelSpace, transaction, label);
                WriteChildLink(label, transaction, parentHandle, "LX", xIndex, -1);
                created++;
            }

            for (yIndex = 0; yIndex < yValues.Count; yIndex++)
            {
                string prefix = settings.ReverseXY ? "X: " : "Y: ";
                MText label = CreateLabel(
                    database,
                    boundary,
                    prefix + yValues[yIndex].ToString("0.###", CultureInfo.InvariantCulture),
                    new Point3d(bounds.MaxX - insideOffset, yValues[yIndex], bounds.Elevation),
                    textHeight,
                    Math.PI / 2.0);
                Append(modelSpace, transaction, label);
                WriteChildLink(label, transaction, parentHandle, "LY", -1, yIndex);
                created++;
            }

            if (settings.CreatePoints)
            {
                for (xIndex = 0; xIndex < xValues.Count; xIndex++)
                {
                    for (yIndex = 0; yIndex < yValues.Count; yIndex++)
                    {
                        var point = new DBPoint(
                            new Point3d(
                                xValues[xIndex],
                                yValues[yIndex],
                                bounds.Elevation));
                        point.SetDatabaseDefaults(database);
                        point.LayerId = boundary.LayerId;
                        Append(modelSpace, transaction, point);
                        WriteChildLink(point, transaction, parentHandle, "P", xIndex, yIndex);
                        created++;
                    }
                }
            }

            try { boundary.RecordGraphicsModified(true); } catch { }
            return created;
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

        private static List<double> BuildPositions(double minimum, double maximum, double spacing)
        {
            var result = new List<double>();
            if (maximum <= minimum || spacing <= 0.0) return result;

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

        private static double ModelTextHeight(Database database, double paperHeight)
        {
            try
            {
                return Math.Max(PaperAnnotationScale.ModelTextHeight(database, paperHeight), 0.001);
            }
            catch
            {
                return Math.Max(paperHeight, 0.001);
            }
        }

        private static double ParsePaperHeight(string text, double fallback)
        {
            string value = (text ?? string.Empty).Replace("mm", string.Empty).Trim();
            double result;
            if (double.TryParse(
                    value,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out result) && result > 0.0)
                return result;
            if (double.TryParse(
                    value,
                    NumberStyles.Float,
                    CultureInfo.CurrentCulture,
                    out result) && result > 0.0)
                return result;
            return fallback;
        }

        private static void WriteParentSettings(
            Polyline boundary,
            Transaction transaction,
            August12SurveySiteGridCommands.SiteGridSettings settings)
        {
            WriteRecord(
                boundary,
                transaction,
                August12SurveySiteGridCommands.ParentKey,
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

        private static bool TryReadParentSettings(
            Polyline boundary,
            Transaction transaction,
            out August12SurveySiteGridCommands.SiteGridSettings settings)
        {
            settings = August12SurveySiteGridCommands.SiteGridSettings.Default();
            if (boundary == null) return false;

            TypedValue[] values;
            if (!TryReadRecord(
                    boundary,
                    transaction,
                    August12SurveySiteGridCommands.ParentKey,
                    out values) || values.Length < 6)
                return false;

            try
            {
                settings.SpacingX = Convert.ToDouble(values[1].Value, CultureInfo.InvariantCulture);
                settings.SpacingY = Convert.ToDouble(values[2].Value, CultureInfo.InvariantCulture);
                settings.ReverseXY = Convert.ToInt32(values[3].Value, CultureInfo.InvariantCulture) != 0;
                settings.PaperTextHeight = Convert.ToDouble(values[4].Value, CultureInfo.InvariantCulture);
                settings.CreatePoints = Convert.ToInt32(values[5].Value, CultureInfo.InvariantCulture) != 0;
                return settings.SpacingX > 0.0 && settings.SpacingY > 0.0 && settings.PaperTextHeight > 0.0;
            }
            catch
            {
                settings = August12SurveySiteGridCommands.SiteGridSettings.Default();
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
                August12SurveySiteGridCommands.ChildKey,
                new[]
                {
                    new TypedValue((int)DxfCode.Text, parentHandle ?? string.Empty),
                    new TypedValue((int)DxfCode.Text, role ?? string.Empty),
                    new TypedValue((int)DxfCode.Text, xIndex.ToString(CultureInfo.InvariantCulture)),
                    new TypedValue((int)DxfCode.Text, yIndex.ToString(CultureInfo.InvariantCulture))
                });
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

            int removed = 0;
            foreach (ObjectId id in modelSpace.Cast<ObjectId>().ToList())
            {
                Entity entity = null;
                try
                {
                    entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                }
                catch
                {
                    continue;
                }
                if (entity == null || entity.IsErased) continue;

                TypedValue[] values;
                if (!TryReadRecord(
                        entity,
                        transaction,
                        August12SurveySiteGridCommands.ChildKey,
                        out values) || values.Length < 1)
                    continue;

                string linkedParent = Convert.ToString(values[0].Value, CultureInfo.InvariantCulture);
                if (!string.Equals(linkedParent, parentHandle, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!entity.IsWriteEnabled) entity.UpgradeOpen();
                entity.Erase();
                removed++;
            }
            return removed;
        }

        private static void WriteRecord(
            DBObject owner,
            Transaction transaction,
            string key,
            IEnumerable<TypedValue> values)
        {
            if (owner.ExtensionDictionary.IsNull) owner.CreateExtensionDictionary();
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

            if (record != null) record.Data = new ResultBuffer(values.ToArray());
        }

        private static bool TryReadRecord(
            DBObject owner,
            Transaction transaction,
            string key,
            out TypedValue[] values)
        {
            values = new TypedValue[0];
            if (owner == null || owner.ExtensionDictionary.IsNull) return false;
            try
            {
                DBDictionary dictionary = transaction.GetObject(
                    owner.ExtensionDictionary,
                    OpenMode.ForRead,
                    false) as DBDictionary;
                if (dictionary == null || !dictionary.Contains(key)) return false;
                Xrecord record = transaction.GetObject(
                    dictionary.GetAt(key),
                    OpenMode.ForRead,
                    false) as Xrecord;
                if (record == null || record.Data == null) return false;
                values = record.Data.AsArray();
                return values.Length > 0;
            }
            catch
            {
                return false;
            }
        }

        private static void Append(
            BlockTableRecord space,
            Transaction transaction,
            Entity entity)
        {
            space.AppendEntity(entity);
            transaction.AddNewlyCreatedDBObject(entity, true);
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

        private static Document Active()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
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
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using CETools.Core;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.ParkingOptimiserCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Obstacle-aware parking concept optimiser. Generated alternatives remain
    /// drafting/design assistance and require review against governing parking,
    /// accessibility, traffic, fire, drainage and swept-path standards.
    /// </summary>
    public sealed class ParkingOptimiserCommands
    {
        private const string RegAppName = "CE_PARK_OPTIMISER";
        private const string SchemaVersion = "1";
        private const string LayerPrefix = "CE-PARK-OPT-";
        private const int MaximumObstacles = 500;
        private const double Tolerance = 1e-8;

        [CommandMethod("CE_TOOLS", "CE_PARKOPTIMIZERTOOLS", CommandFlags.Modal)]
        public void ParkingOptimiserTools()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            var options = new PromptKeywordOptions(
                "\nFull parking optimiser [Create/Refresh/Info/Export/Clear] <Create>: ")
            {
                AllowNone = true
            };
            foreach (string keyword in new[] { "Create", "Refresh", "Info", "Export", "Clear" })
                options.Keywords.Add(keyword);
            PromptResult result = document.Editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel) return;
            string choice = result.Status == PromptStatus.OK ? result.StringResult : "Create";
            string command = Equal(choice, "Refresh") ? "CE_PARKOPTREFRESH " :
                Equal(choice, "Info") ? "CE_PARKOPTINFO " :
                Equal(choice, "Export") ? "CE_PARKOPTEXPORT " :
                Equal(choice, "Clear") ? "CE_PARKOPTCLEAR " : "CE_PARKOPTIMIZE ";
            document.SendStringToExecute(command, true, false, true);
        }

        [CommandMethod("CE_TOOLS", "CE_PARKOPTIMIZE", CommandFlags.Modal | CommandFlags.Redraw)]
        public void OptimiseParking()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            ParkingOptimiserInput input;
            if (!PromptNewInput(document, out input)) return;

            try
            {
                IReadOnlyList<ParkingLayoutOption> options = RunOptimiser(input);
                ShowOptions(document, options, input.Settings.TargetBayCount);
                int selectedIndex;
                if (!PromptOptionIndex(document.Editor, options.Count, out selectedIndex)) return;
                ParkingLayoutOption selected = options[selectedIndex];
                if (!ConfirmOption(document.Editor, selected)) return;
                int created = ReplaceLinkedLayout(document.Database, input, selected);
                document.Editor.Regen();
                document.Editor.WriteMessage(
                    "\nCE_PARKOPTIMIZE complete. Option={0}; angle={1:N0}; orientation={2:N1}; standard={3}; accessible={4}; islands={5}; graphics={6}.",
                    selectedIndex + 1,
                    selected.ParkingAngleDegrees,
                    selected.OrientationDegrees,
                    selected.StandardBayCount,
                    selected.AccessibleBayCount,
                    selected.Islands.Count,
                    created);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_PARKOPTIMIZE failed. No optimiser transaction was committed. {0}",
                    exception.Message);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_PARKOPTREFRESH", CommandFlags.Modal | CommandFlags.Redraw)]
        public void RefreshOptimisedParking()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            ParkingOptimiserLink link;
            if (!PromptLinkedSet(document, out link)) return;
            ParkingOptimiserInput input;
            if (!RebuildInput(document.Database, link, out input))
            {
                document.Editor.WriteMessage(
                    "\nCE_PARKOPTREFRESH stopped. One or more source boundaries are missing or invalid.");
                return;
            }

            try
            {
                IReadOnlyList<ParkingLayoutOption> options = RunOptimiser(input);
                ParkingLayoutOption selected = options
                    .OrderBy(item => Math.Abs(item.ParkingAngleDegrees - link.ParkingAngleDegrees) +
                        Math.Abs(item.OrientationDegrees - link.OrientationDegrees))
                    .First();
                if (!ConfirmOption(document.Editor, selected)) return;
                int created = ReplaceLinkedLayout(document.Database, input, selected);
                document.Editor.Regen();
                document.Editor.WriteMessage(
                    "\nCE_PARKOPTREFRESH complete. Standard={0}; accessible={1}; islands={2}; graphics={3}.",
                    selected.StandardBayCount,
                    selected.AccessibleBayCount,
                    selected.Islands.Count,
                    created);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_PARKOPTREFRESH failed. Existing linked geometry was retained. {0}",
                    exception.Message);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_PARKOPTINFO", CommandFlags.Modal | CommandFlags.Redraw)]
        public void OptimisedParkingInformation()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            ParkingOptimiserLink link;
            if (!PromptLinkedSet(document, out link)) return;
            List<IList<string>> rows = BuildLinkRows(link);
            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - Optimised Parking Information",
                "Stored source handles, concept criteria and selected-option outcome. Re-run refresh to recalculate from current source geometry.",
                new List<string> { "Property", "Value" },
                rows,
                "CE TOOLS OPTIMISED PARKING INFORMATION");
        }

        [CommandMethod("CE_TOOLS", "CE_PARKOPTEXPORT", CommandFlags.Modal)]
        public void ExportOptimisedParking()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            ParkingOptimiserLink link;
            if (!PromptLinkedSet(document, out link)) return;

            var options = new PromptSaveFileOptions(
                "\nChoose the optimised-parking Excel path: ")
            {
                Filter = "Excel Workbook (*.xlsx)|*.xlsx",
                DialogCaption = "Export Optimised Parking",
                InitialFileName = "CE-Tools-Optimised-Parking.xlsx"
            };
            PromptFileNameResult result = document.Editor.GetFileNameForSave(options);
            if (result.Status != PromptStatus.OK) return;
            string path = result.StringResult.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)
                ? result.StringResult : result.StringResult + ".xlsx";

            List<IList<string>> rows = BuildExportRows(document.Database, link);
            SimpleXlsxWriter.Write(path, "Parking Optimiser", rows);
            document.Editor.WriteMessage(
                "\nCE_PARKOPTEXPORT complete. Elements={0}; file={1}.",
                Math.Max(0, rows.Count - 2),
                path);
        }

        [CommandMethod("CE_TOOLS", "CE_PARKOPTCLEAR", CommandFlags.Modal | CommandFlags.Redraw)]
        public void ClearOptimisedParking()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            ParkingOptimiserLink link;
            if (!PromptLinkedSet(document, out link)) return;
            if (!PromptYesNo(
                    document.Editor,
                    "Erase only optimiser-generated elements linked to boundary " + link.BoundaryHandle,
                    false))
                return;
            int erased = EraseLinked(document.Database, link.BoundaryHandle);
            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_PARKOPTCLEAR complete. Erased optimiser elements={0}. Source boundary and obstacles were unchanged.",
                erased);
        }

        private static bool PromptNewInput(Document document, out ParkingOptimiserInput input)
        {
            input = null;
            Editor editor = document.Editor;
            ObjectId boundaryId;
            ParkingPolygon boundary;
            double elevation;
            string boundaryHandle;
            if (!PromptClosedPolyline(
                    document,
                    "\nSelect the closed parking-area boundary: ",
                    out boundaryId,
                    out boundary,
                    out elevation,
                    out boundaryHandle))
                return false;

            PromptSelectionOptions selectionOptions = new PromptSelectionOptions
            {
                MessageForAdding = "\nSelect closed obstacle/island/building polylines or press Enter for none: ",
                AllowDuplicates = false,
                RejectObjectsFromNonCurrentSpace = true
            };
            var filter = new SelectionFilter(new[]
            {
                new TypedValue((int)DxfCode.Start, "LWPOLYLINE")
            });
            PromptSelectionResult selected = editor.GetSelection(selectionOptions, filter);
            if (selected.Status == PromptStatus.Cancel) return false;
            ObjectId[] obstacleIds = selected.Status == PromptStatus.OK
                ? selected.Value.GetObjectIds().Where(id => id != boundaryId).Distinct().ToArray()
                : new ObjectId[0];
            if (obstacleIds.Length > MaximumObstacles)
            {
                editor.WriteMessage(
                    "\nCE_PARKOPTIMIZE stopped. Obstacles exceed the {0}-object safety limit.",
                    MaximumObstacles);
                return false;
            }

            List<ParkingPolygon> obstacles;
            List<string> obstacleHandles;
            if (!ReadPolygons(document.Database, obstacleIds, out obstacles, out obstacleHandles))
                return false;

            PromptPointResult entranceResult = editor.GetPoint(
                "\nPick the preferred parking entrance/access point inside the boundary: ");
            if (entranceResult.Status != PromptStatus.OK) return false;
            Point3d entranceWorld = entranceResult.Value.TransformBy(editor.CurrentUserCoordinateSystem);
            var entrance = new ParkingPoint(entranceWorld.X, entranceWorld.Y);
            if (!boundary.Contains(entrance, true))
            {
                editor.WriteMessage("\nCE_PARKOPTIMIZE stopped. The entrance point must be inside the boundary.");
                return false;
            }

            ParkingLayoutSettings settings;
            if (!PromptSettings(editor, out settings)) return false;
            input = new ParkingOptimiserInput(
                boundaryId,
                boundaryHandle,
                boundary,
                elevation,
                obstacleIds,
                obstacleHandles,
                obstacles,
                entrance,
                settings);
            return true;
        }

        private static bool PromptSettings(Editor editor, out ParkingLayoutSettings settings)
        {
            settings = null;
            int target, accessible, islandInterval;
            double width, accessibleWidth, accessAisle, depth, aisle, entranceWidth, islandWidth;
            if (!PromptPositiveInteger(editor, "Target bay count", 120, out target) ||
                !PromptNonNegativeInteger(editor, "Required accessible bay count", 3, out accessible) ||
                !PromptPositiveDouble(editor, "Standard bay width (drawing units)", 2.5, out width) ||
                !PromptPositiveDouble(editor, "Accessible bay width (drawing units)", 3.6, out accessibleWidth) ||
                !PromptPositiveDouble(editor, "Accessible access-aisle width (drawing units)", 1.5, out accessAisle) ||
                !PromptPositiveDouble(editor, "Bay depth (drawing units)", 5.0, out depth) ||
                !PromptPositiveDouble(editor, "Traffic aisle width (drawing units)", 6.0, out aisle) ||
                !PromptPositiveDouble(editor, "Entrance connection width (drawing units)", 6.0, out entranceWidth) ||
                !PromptNonNegativeInteger(editor, "Landscape island interval in bays (0=None)", 10, out islandInterval) ||
                !PromptNonNegativeDouble(editor, "Landscape island width (drawing units)", 2.0, out islandWidth))
                return false;
            settings = new ParkingLayoutSettings(
                target,
                accessible,
                width,
                accessibleWidth,
                accessAisle,
                depth,
                aisle,
                entranceWidth,
                islandInterval,
                islandWidth,
                new[] { 90.0, 60.0, 45.0 },
                new[] { 0.0, 90.0 });
            return true;
        }

        private static IReadOnlyList<ParkingLayoutOption> RunOptimiser(ParkingOptimiserInput input)
        {
            return ParkingLayoutOptimizer.Optimise(
                input.Boundary,
                input.Obstacles,
                input.Entrance,
                input.Settings);
        }

        private static void ShowOptions(
            Document document,
            IReadOnlyList<ParkingLayoutOption> options,
            int target)
        {
            var rows = new List<IList<string>>();
            for (int index = 0; index < options.Count; index++)
            {
                ParkingLayoutOption option = options[index];
                rows.Add(new List<string>
                {
                    (index + 1).ToString(CultureInfo.InvariantCulture),
                    option.ParkingAngleDegrees.ToString("0", CultureInfo.CurrentCulture),
                    option.OrientationDegrees.ToString("0.###", CultureInfo.CurrentCulture),
                    option.StandardBayCount.ToString(CultureInfo.InvariantCulture),
                    option.AccessibleBayCount.ToString(CultureInfo.InvariantCulture),
                    option.TotalBayCount.ToString(CultureInfo.InvariantCulture),
                    option.Islands.Count.ToString(CultureInfo.InvariantCulture),
                    option.Aisles.Count.ToString(CultureInfo.InvariantCulture),
                    option.TargetShortfall.ToString(CultureInfo.InvariantCulture),
                    option.MissingAccessibleBays.ToString(CultureInfo.InvariantCulture),
                    option.Rejections.Total.ToString(CultureInfo.InvariantCulture),
                    option.Score.ToString("0", CultureInfo.CurrentCulture),
                    option.Notes
                });
            }
            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - Full Parking Optimiser Alternatives",
                "Alternatives are ranked by capacity, target/accessible compliance, entrance connection and rejected geometry checks. Target=" + target + ". Concept screening only.",
                new List<string>
                {
                    "OPTION", "ANGLE", "ORIENTATION", "STANDARD", "ACCESSIBLE",
                    "TOTAL", "ISLANDS", "AISLES", "TARGET SHORT", "ACCESS SHORT",
                    "REJECTIONS", "SCORE", "NOTES"
                },
                rows,
                "CE TOOLS PARKING OPTIMISER ALTERNATIVES");
        }

        private static bool PromptOptionIndex(Editor editor, int count, out int index)
        {
            var options = new PromptIntegerOptions(
                "\nSelect parking option number <1>: ")
            {
                AllowNone = true,
                AllowNegative = false,
                AllowZero = false,
                LowerLimit = 1,
                UpperLimit = count,
                DefaultValue = 1,
                UseDefaultValue = true
            };
            PromptIntegerResult result = editor.GetInteger(options);
            if (result.Status == PromptStatus.Cancel)
            {
                index = -1;
                return false;
            }
            index = (result.Status == PromptStatus.OK ? result.Value : 1) - 1;
            return index >= 0 && index < count;
        }

        private static bool ConfirmOption(Editor editor, ParkingLayoutOption option)
        {
            editor.WriteMessage(
                "\nSelected parking option: angle={0:N0}; orientation={1:N1}; total={2}; accessible={3}; islands={4}; target shortfall={5}; accessible shortfall={6}; score={7:N0}.",
                option.ParkingAngleDegrees,
                option.OrientationDegrees,
                option.TotalBayCount,
                option.AccessibleBayCount,
                option.Islands.Count,
                option.TargetShortfall,
                option.MissingAccessibleBays,
                option.Score);
            return PromptYesNo(editor, "Create/replace this linked parking option", false);
        }

        private static int ReplaceLinkedLayout(
            Database database,
            ParkingOptimiserInput input,
            ParkingLayoutOption option)
        {
            int created = 0;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                EnsureRegApp(database, transaction);
                BlockTableRecord space = transaction.GetObject(
                    database.CurrentSpaceId,
                    OpenMode.ForWrite,
                    false) as BlockTableRecord;
                if (space == null)
                    throw new InvalidOperationException("The current drawing space could not be opened.");
                EraseLinked(space, transaction, input.BoundaryHandle);

                var all = option.Bays.Concat(option.Aisles).Concat(option.Islands).ToList();
                int index = 0;
                foreach (ParkingElement element in all)
                {
                    ObjectId layerId = GetOrCreateLayer(database, transaction, element.Type);
                    Polyline polyline = CreatePolyline(element.Polygon, input.Elevation);
                    polyline.SetDatabaseDefaults(database);
                    polyline.LayerId = layerId;
                    polyline.ColorIndex = 256;
                    WriteLink(polyline, input, option, element, ++index);
                    space.AppendEntity(polyline);
                    transaction.AddNewlyCreatedDBObject(polyline, true);
                    created++;
                }
                transaction.Commit();
            }
            return created;
        }

        private static Polyline CreatePolyline(ParkingPolygon polygon, double elevation)
        {
            var result = new Polyline(polygon.Vertices.Count);
            result.Elevation = elevation;
            for (int index = 0; index < polygon.Vertices.Count; index++)
            {
                ParkingPoint point = polygon.Vertices[index];
                result.AddVertexAt(index, new Point2d(point.X, point.Y), 0.0, 0.0, 0.0);
            }
            result.Closed = true;
            return result;
        }

        private static void WriteLink(
            Entity entity,
            ParkingOptimiserInput input,
            ParkingLayoutOption option,
            ParkingElement element,
            int index)
        {
            var values = new List<TypedValue>
            {
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, RegAppName),
                Text("Schema=" + SchemaVersion),
                Text("Boundary=" + input.BoundaryHandle),
                Text("ElementType=" + element.Type),
                Text("ElementName=" + element.Name),
                Text("Index=" + index.ToString(CultureInfo.InvariantCulture)),
                Text("Angle=" + option.ParkingAngleDegrees.ToString("R", CultureInfo.InvariantCulture)),
                Text("Orientation=" + option.OrientationDegrees.ToString("R", CultureInfo.InvariantCulture)),
                Text("Score=" + option.Score.ToString("R", CultureInfo.InvariantCulture)),
                Text("Standard=" + option.StandardBayCount.ToString(CultureInfo.InvariantCulture)),
                Text("Accessible=" + option.AccessibleBayCount.ToString(CultureInfo.InvariantCulture)),
                Text("Islands=" + option.Islands.Count.ToString(CultureInfo.InvariantCulture)),
                Text("TargetShort=" + option.TargetShortfall.ToString(CultureInfo.InvariantCulture)),
                Text("AccessibleShort=" + option.MissingAccessibleBays.ToString(CultureInfo.InvariantCulture)),
                Text("EntranceX=" + input.Entrance.X.ToString("R", CultureInfo.InvariantCulture)),
                Text("EntranceY=" + input.Entrance.Y.ToString("R", CultureInfo.InvariantCulture)),
                Text("Target=" + input.Settings.TargetBayCount.ToString(CultureInfo.InvariantCulture)),
                Text("RequiredAccessible=" + input.Settings.RequiredAccessibleBayCount.ToString(CultureInfo.InvariantCulture)),
                Text("BayWidth=" + input.Settings.StandardBayWidthMetres.ToString("R", CultureInfo.InvariantCulture)),
                Text("AccessibleWidth=" + input.Settings.AccessibleBayWidthMetres.ToString("R", CultureInfo.InvariantCulture)),
                Text("AccessAisle=" + input.Settings.AccessAisleWidthMetres.ToString("R", CultureInfo.InvariantCulture)),
                Text("BayDepth=" + input.Settings.BayDepthMetres.ToString("R", CultureInfo.InvariantCulture)),
                Text("TrafficAisle=" + input.Settings.AisleWidthMetres.ToString("R", CultureInfo.InvariantCulture)),
                Text("EntranceWidth=" + input.Settings.EntranceWidthMetres.ToString("R", CultureInfo.InvariantCulture)),
                Text("IslandInterval=" + input.Settings.IslandIntervalBays.ToString(CultureInfo.InvariantCulture)),
                Text("IslandWidth=" + input.Settings.IslandWidthMetres.ToString("R", CultureInfo.InvariantCulture)),
                Text("Rejections=" + option.Rejections.Total.ToString(CultureInfo.InvariantCulture))
            };
            foreach (string handle in input.ObstacleHandles)
                values.Add(Text("Obstacle=" + handle));
            entity.XData = new ResultBuffer(values.ToArray());
        }

        private static TypedValue Text(string value)
        {
            return new TypedValue((int)DxfCode.ExtendedDataAsciiString, value);
        }

        private static bool PromptLinkedSet(Document document, out ParkingOptimiserLink link)
        {
            link = null;
            PromptEntityResult result = document.Editor.GetEntity(
                "\nSelect an optimiser-generated parking element: ");
            if (result.Status != PromptStatus.OK) return false;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                Entity entity = transaction.GetObject(result.ObjectId, OpenMode.ForRead, false) as Entity;
                link = ReadLink(entity);
            }
            if (link == null)
                document.Editor.WriteMessage("\nThe selected object is not linked to the full parking optimiser.");
            return link != null;
        }

        private static ParkingOptimiserLink ReadLink(Entity entity)
        {
            if (entity == null) return null;
            ResultBuffer data = entity.GetXDataForApplication(RegAppName);
            if (data == null) return null;
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var obstacles = new List<string>();
            foreach (TypedValue item in data)
            {
                string text = item.Value as string;
                if (string.IsNullOrWhiteSpace(text)) continue;
                int equals = text.IndexOf('=');
                if (equals <= 0) continue;
                string key = text.Substring(0, equals);
                string value = text.Substring(equals + 1);
                if (Equal(key, "Obstacle")) obstacles.Add(value);
                else values[key] = value;
            }
            string boundary;
            if (!values.TryGetValue("Boundary", out boundary) || string.IsNullOrWhiteSpace(boundary))
                return null;
            return ParkingOptimiserLink.From(values, obstacles);
        }

        private static bool RebuildInput(
            Database database,
            ParkingOptimiserLink link,
            out ParkingOptimiserInput input)
        {
            input = null;
            ObjectId boundaryId;
            if (!TryResolveHandle(database, link.BoundaryHandle, out boundaryId)) return false;
            ParkingPolygon boundary;
            double elevation;
            string handle;
            if (!ReadClosedPolyline(database, boundaryId, out boundary, out elevation, out handle)) return false;
            var obstacleIds = new List<ObjectId>();
            foreach (string obstacleHandle in link.ObstacleHandles)
            {
                ObjectId id;
                if (TryResolveHandle(database, obstacleHandle, out id)) obstacleIds.Add(id);
            }
            List<ParkingPolygon> obstacles;
            List<string> handles;
            if (!ReadPolygons(database, obstacleIds.ToArray(), out obstacles, out handles)) return false;
            input = new ParkingOptimiserInput(
                boundaryId,
                handle,
                boundary,
                elevation,
                obstacleIds.ToArray(),
                handles,
                obstacles,
                new ParkingPoint(link.EntranceX, link.EntranceY),
                link.Settings);
            return true;
        }

        private static List<IList<string>> BuildLinkRows(ParkingOptimiserLink link)
        {
            return new List<IList<string>>
            {
                Row("Boundary handle", link.BoundaryHandle),
                Row("Obstacle count", link.ObstacleHandles.Count.ToString(CultureInfo.InvariantCulture)),
                Row("Parking angle", link.ParkingAngleDegrees.ToString("0.###", CultureInfo.CurrentCulture)),
                Row("Orientation", link.OrientationDegrees.ToString("0.###", CultureInfo.CurrentCulture)),
                Row("Score", link.Score.ToString("0", CultureInfo.CurrentCulture)),
                Row("Standard bays", link.StandardBayCount.ToString(CultureInfo.InvariantCulture)),
                Row("Accessible bays", link.AccessibleBayCount.ToString(CultureInfo.InvariantCulture)),
                Row("Landscape islands", link.IslandCount.ToString(CultureInfo.InvariantCulture)),
                Row("Target shortfall", link.TargetShortfall.ToString(CultureInfo.InvariantCulture)),
                Row("Accessible shortfall", link.AccessibleShortfall.ToString(CultureInfo.InvariantCulture)),
                Row("Rejected checks", link.RejectionCount.ToString(CultureInfo.InvariantCulture)),
                Row("Entrance", string.Format(CultureInfo.CurrentCulture, "X {0:N3}; Y {1:N3}", link.EntranceX, link.EntranceY)),
                Row("Target bays", link.Settings.TargetBayCount.ToString(CultureInfo.InvariantCulture)),
                Row("Required accessible", link.Settings.RequiredAccessibleBayCount.ToString(CultureInfo.InvariantCulture)),
                Row("Bay size", string.Format(CultureInfo.CurrentCulture, "{0:N3} x {1:N3}", link.Settings.StandardBayWidthMetres, link.Settings.BayDepthMetres)),
                Row("Traffic aisle", link.Settings.AisleWidthMetres.ToString("0.###", CultureInfo.CurrentCulture)),
                Row("Island interval", link.Settings.IslandIntervalBays.ToString(CultureInfo.InvariantCulture)),
                Row("Engineering boundary", "Concept screening only; verify accessibility, circulation, swept paths, fire access, gradients, drainage, markings and governing standards.")
            };
        }

        private static List<IList<string>> BuildExportRows(Database database, ParkingOptimiserLink link)
        {
            var rows = new List<IList<string>>
            {
                new List<string> { "PROPERTY", "VALUE", "TYPE", "NAME", "LAYER", "HANDLE", "AREA" }
            };
            foreach (IList<string> item in BuildLinkRows(link))
                rows.Add(new List<string> { item[0], item[1], string.Empty, string.Empty, string.Empty, string.Empty, string.Empty });
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = transaction.GetObject(database.CurrentSpaceId, OpenMode.ForRead, false) as BlockTableRecord;
                if (space == null) return rows;
                foreach (ObjectId id in space)
                {
                    Entity entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                    ParkingOptimiserLink item = ReadLink(entity);
                    if (item == null || !Equal(item.BoundaryHandle, link.BoundaryHandle)) continue;
                    Polyline polyline = entity as Polyline;
                    rows.Add(new List<string>
                    {
                        "Element", string.Empty, item.ElementType, item.ElementName,
                        entity.Layer, entity.Handle.ToString(),
                        polyline == null ? string.Empty : Math.Abs(polyline.Area).ToString("0.###", CultureInfo.CurrentCulture)
                    });
                }
            }
            return rows;
        }

        private static int EraseLinked(Database database, string boundaryHandle)
        {
            int erased;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = transaction.GetObject(database.CurrentSpaceId, OpenMode.ForWrite, false) as BlockTableRecord;
                erased = space == null ? 0 : EraseLinked(space, transaction, boundaryHandle);
                transaction.Commit();
            }
            return erased;
        }

        private static int EraseLinked(BlockTableRecord space, Transaction transaction, string boundaryHandle)
        {
            int erased = 0;
            foreach (ObjectId id in space.Cast<ObjectId>().ToList())
            {
                Entity entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                ParkingOptimiserLink link = ReadLink(entity);
                if (link == null || !Equal(link.BoundaryHandle, boundaryHandle)) continue;
                entity.UpgradeOpen();
                entity.Erase();
                erased++;
            }
            return erased;
        }

        private static bool PromptClosedPolyline(
            Document document,
            string message,
            out ObjectId id,
            out ParkingPolygon polygon,
            out double elevation,
            out string handle)
        {
            var options = new PromptEntityOptions(message);
            options.SetRejectMessage("\nSelect a closed lightweight polyline.");
            options.AddAllowedClass(typeof(Polyline), false);
            PromptEntityResult result = document.Editor.GetEntity(options);
            id = result.Status == PromptStatus.OK ? result.ObjectId : ObjectId.Null;
            if (result.Status != PromptStatus.OK)
            {
                polygon = null; elevation = 0.0; handle = string.Empty; return false;
            }
            if (!ReadClosedPolyline(document.Database, id, out polygon, out elevation, out handle))
            {
                document.Editor.WriteMessage("\nThe selected polyline is not a valid closed non-zero-area boundary.");
                return false;
            }
            return true;
        }

        private static bool ReadClosedPolyline(
            Database database,
            ObjectId id,
            out ParkingPolygon polygon,
            out double elevation,
            out string handle)
        {
            polygon = null; elevation = 0.0; handle = string.Empty;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                Polyline polyline = transaction.GetObject(id, OpenMode.ForRead, false) as Polyline;
                if (polyline == null || !polyline.Closed || polyline.NumberOfVertices < 3 || Math.Abs(polyline.Area) <= Tolerance)
                    return false;
                polygon = new ParkingPolygon(Enumerable.Range(0, polyline.NumberOfVertices)
                    .Select(index => polyline.GetPoint2dAt(index))
                    .Select(point => new ParkingPoint(point.X, point.Y)));
                try { polygon.Validate("polyline"); }
                catch { polygon = null; return false; }
                elevation = polyline.Elevation;
                handle = polyline.Handle.ToString();
                return true;
            }
        }

        private static bool ReadPolygons(
            Database database,
            ObjectId[] ids,
            out List<ParkingPolygon> polygons,
            out List<string> handles)
        {
            polygons = new List<ParkingPolygon>();
            handles = new List<string>();
            foreach (ObjectId id in ids)
            {
                ParkingPolygon polygon;
                double elevation;
                string handle;
                if (!ReadClosedPolyline(database, id, out polygon, out elevation, out handle)) continue;
                polygons.Add(polygon);
                handles.Add(handle);
            }
            return true;
        }

        private static bool TryResolveHandle(Database database, string text, out ObjectId id)
        {
            id = ObjectId.Null;
            long value;
            if (!long.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)) return false;
            try
            {
                id = database.GetObjectId(false, new Handle(value), 0);
                return !id.IsNull && !id.IsErased;
            }
            catch { return false; }
        }

        private static void EnsureRegApp(Database database, Transaction transaction)
        {
            RegAppTable table = transaction.GetObject(database.RegAppTableId, OpenMode.ForRead, false) as RegAppTable;
            if (table == null || table.Has(RegAppName)) return;
            table.UpgradeOpen();
            var record = new RegAppTableRecord { Name = RegAppName };
            table.Add(record);
            transaction.AddNewlyCreatedDBObject(record, true);
        }

        private static ObjectId GetOrCreateLayer(Database database, Transaction transaction, ParkingElementType type)
        {
            string name = LayerPrefix + type.ToString().ToUpperInvariant();
            LayerTable table = transaction.GetObject(database.LayerTableId, OpenMode.ForRead, false) as LayerTable;
            if (table == null) throw new InvalidOperationException("The layer table could not be opened.");
            if (table.Has(name)) return table[name];
            table.UpgradeOpen();
            short colour = type == ParkingElementType.AccessibleBay ? (short)5 :
                type == ParkingElementType.AccessAisle ? (short)4 :
                type == ParkingElementType.LandscapeIsland ? (short)3 :
                type == ParkingElementType.TrafficAisle || type == ParkingElementType.EntranceConnection ? (short)8 : (short)7;
            var layer = new LayerTableRecord
            {
                Name = name,
                Color = Color.FromColorIndex(ColorMethod.ByAci, colour),
                IsPlottable = true
            };
            ObjectId id = table.Add(layer);
            transaction.AddNewlyCreatedDBObject(layer, true);
            return id;
        }

        private static bool PromptPositiveInteger(Editor editor, string label, int defaultValue, out int value)
        {
            var options = new PromptIntegerOptions("\n" + label + " <" + defaultValue + ">: ")
            {
                AllowNone = true, AllowNegative = false, AllowZero = false,
                DefaultValue = defaultValue, LowerLimit = 1, UseDefaultValue = true
            };
            PromptIntegerResult result = editor.GetInteger(options);
            value = result.Status == PromptStatus.OK ? result.Value : defaultValue;
            return result.Status != PromptStatus.Cancel;
        }

        private static bool PromptNonNegativeInteger(Editor editor, string label, int defaultValue, out int value)
        {
            var options = new PromptIntegerOptions("\n" + label + " <" + defaultValue + ">: ")
            {
                AllowNone = true, AllowNegative = false, AllowZero = true,
                DefaultValue = defaultValue, LowerLimit = 0, UseDefaultValue = true
            };
            PromptIntegerResult result = editor.GetInteger(options);
            value = result.Status == PromptStatus.OK ? result.Value : defaultValue;
            return result.Status != PromptStatus.Cancel;
        }

        private static bool PromptPositiveDouble(Editor editor, string label, double defaultValue, out double value)
        {
            var options = new PromptDoubleOptions("\n" + label + " <" + defaultValue.ToString(CultureInfo.CurrentCulture) + ">: ")
            {
                AllowNone = true, AllowNegative = false, AllowZero = false, DefaultValue = defaultValue
            };
            PromptDoubleResult result = editor.GetDouble(options);
            value = result.Status == PromptStatus.OK ? result.Value : defaultValue;
            return result.Status != PromptStatus.Cancel && value > 0.0 && !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static bool PromptNonNegativeDouble(Editor editor, string label, double defaultValue, out double value)
        {
            var options = new PromptDoubleOptions("\n" + label + " <" + defaultValue.ToString(CultureInfo.CurrentCulture) + ">: ")
            {
                AllowNone = true, AllowNegative = false, AllowZero = true, DefaultValue = defaultValue
            };
            PromptDoubleResult result = editor.GetDouble(options);
            value = result.Status == PromptStatus.OK ? result.Value : defaultValue;
            return result.Status != PromptStatus.Cancel && value >= 0.0 && !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static bool PromptYesNo(Editor editor, string label, bool defaultValue)
        {
            var options = new PromptKeywordOptions("\n" + label + " [Yes/No] <" + (defaultValue ? "Yes" : "No") + ">: ") { AllowNone = true };
            options.Keywords.Add("Yes"); options.Keywords.Add("No");
            PromptResult result = editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel) return false;
            return result.Status == PromptStatus.None ? defaultValue : Equal(result.StringResult, "Yes");
        }

        private static IList<string> Row(string property, string value)
        {
            return new List<string> { property, value };
        }

        private static bool Equal(string first, string second)
        {
            return string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
        }

        private static Document ActiveDocument()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
        }
    }

    internal sealed class ParkingOptimiserInput
    {
        public ParkingOptimiserInput(
            ObjectId boundaryId,
            string boundaryHandle,
            ParkingPolygon boundary,
            double elevation,
            ObjectId[] obstacleIds,
            List<string> obstacleHandles,
            List<ParkingPolygon> obstacles,
            ParkingPoint entrance,
            ParkingLayoutSettings settings)
        {
            BoundaryId = boundaryId; BoundaryHandle = boundaryHandle; Boundary = boundary;
            Elevation = elevation; ObstacleIds = obstacleIds; ObstacleHandles = obstacleHandles;
            Obstacles = obstacles; Entrance = entrance; Settings = settings;
        }
        public ObjectId BoundaryId { get; private set; }
        public string BoundaryHandle { get; private set; }
        public ParkingPolygon Boundary { get; private set; }
        public double Elevation { get; private set; }
        public ObjectId[] ObstacleIds { get; private set; }
        public List<string> ObstacleHandles { get; private set; }
        public List<ParkingPolygon> Obstacles { get; private set; }
        public ParkingPoint Entrance { get; private set; }
        public ParkingLayoutSettings Settings { get; private set; }
    }

    internal sealed class ParkingOptimiserLink
    {
        public string BoundaryHandle { get; private set; }
        public List<string> ObstacleHandles { get; private set; }
        public string ElementType { get; private set; }
        public string ElementName { get; private set; }
        public double ParkingAngleDegrees { get; private set; }
        public double OrientationDegrees { get; private set; }
        public double Score { get; private set; }
        public int StandardBayCount { get; private set; }
        public int AccessibleBayCount { get; private set; }
        public int IslandCount { get; private set; }
        public int TargetShortfall { get; private set; }
        public int AccessibleShortfall { get; private set; }
        public int RejectionCount { get; private set; }
        public double EntranceX { get; private set; }
        public double EntranceY { get; private set; }
        public ParkingLayoutSettings Settings { get; private set; }

        public static ParkingOptimiserLink From(IDictionary<string, string> values, List<string> obstacles)
        {
            var link = new ParkingOptimiserLink
            {
                BoundaryHandle = Get(values, "Boundary"), ObstacleHandles = obstacles,
                ElementType = Get(values, "ElementType"), ElementName = Get(values, "ElementName"),
                ParkingAngleDegrees = Double(values, "Angle", 90.0), OrientationDegrees = Double(values, "Orientation", 0.0),
                Score = Double(values, "Score", 0.0), StandardBayCount = Integer(values, "Standard", 0),
                AccessibleBayCount = Integer(values, "Accessible", 0), IslandCount = Integer(values, "Islands", 0),
                TargetShortfall = Integer(values, "TargetShort", 0), AccessibleShortfall = Integer(values, "AccessibleShort", 0),
                RejectionCount = Integer(values, "Rejections", 0), EntranceX = Double(values, "EntranceX", 0.0),
                EntranceY = Double(values, "EntranceY", 0.0)
            };
            link.Settings = new ParkingLayoutSettings(
                Integer(values, "Target", Math.Max(1, link.StandardBayCount + link.AccessibleBayCount)),
                Integer(values, "RequiredAccessible", link.AccessibleBayCount),
                Double(values, "BayWidth", 2.5), Double(values, "AccessibleWidth", 3.6),
                Double(values, "AccessAisle", 1.5), Double(values, "BayDepth", 5.0),
                Double(values, "TrafficAisle", 6.0), Double(values, "EntranceWidth", 6.0),
                Integer(values, "IslandInterval", 0), Double(values, "IslandWidth", 0.0),
                new[] { 90.0, 60.0, 45.0 }, new[] { 0.0, 90.0 });
            return link;
        }

        private static string Get(IDictionary<string, string> values, string key)
        { string value; return values.TryGetValue(key, out value) ? value : string.Empty; }
        private static double Double(IDictionary<string, string> values, string key, double fallback)
        { double value; return double.TryParse(Get(values, key), NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value : fallback; }
        private static int Integer(IDictionary<string, string> values, string key, int fallback)
        { int value; return int.TryParse(Get(values, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? value : fallback; }
    }
}
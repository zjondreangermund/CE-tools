using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using CETools.Core;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.SurfacePondingCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Converts priority-flood fill depth into connected depression-storage zones.
    /// The result estimates terrain storage to the lowest grid spill level; it is
    /// not a time-varying flood depth, hydraulic water surface or legal flood line.
    /// </summary>
    public sealed class SurfacePondingCommands
    {
        private const string RegAppName = "CE_HYDROLOGY_REVIEW";
        private const string ReviewLayer = "CE-HYDROLOGY-REVIEW";
        private const int MaximumGeneratedEdges = 20000;
        private const double Tolerance = 1e-9;

        [CommandMethod("CE_TOOLS", "CE_PONDINGREVIEW", CommandFlags.Modal | CommandFlags.Redraw)]
        public void PondingReview()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            HydrologyCivilInput input;
            if (!SurfaceHydrologyCommands.PromptAnalysisInput(document, out input)) return;

            double minimumDepthMetres;
            if (!PromptPositiveDouble(
                    document.Editor,
                    "Minimum depression depth to report (metres)",
                    0.05,
                    out minimumDepthMetres))
                return;

            try
            {
                HydrologySample sample = SurfaceHydrologyCommands.SampleAndAnalyse(
                    document.Database,
                    input);
                List<PondingZone> zones = BuildZones(
                    sample,
                    input.UnitsPerMetre,
                    minimumDepthMetres);
                if (zones.Count == 0)
                {
                    document.Editor.WriteMessage(
                        "\nCE_PONDINGREVIEW complete. No sampled depression cell met the {0:N3} m reporting threshold.",
                        minimumDepthMetres);
                    return;
                }

                double totalArea = zones.Sum(item => item.AreaHectares);
                double totalVolume = zones.Sum(item => item.StorageCubicMetres);
                double maximumDepth = zones.Max(item => item.MaximumDepthMetres);
                int edgeCount = zones.Sum(item => CountExposedEdges(
                    item.CellIndices,
                    sample.Rows,
                    sample.Columns));
                List<IList<string>> rows = BuildRows(zones, sample);

                GridReportPresenter.ShowReportAndOfferTable(
                    document,
                    "CE Tools - Depression Storage and Affected Area",
                    string.Format(
                        CultureInfo.CurrentCulture,
                        "Priority-filled terrain storage screen. Zones={0}; affected area={1:N3} ha; estimated fill volume={2:N1} m³; maximum depth={3:N3} m. This is not a dynamic flood model.",
                        zones.Count,
                        totalArea,
                        totalVolume,
                        maximumDepth),
                    rows,
                    "CE TOOLS DEPRESSION STORAGE REVIEW");

                if (PromptYesNo(document.Editor, "Export the depression-storage table to Excel", false))
                {
                    string path;
                    if (PromptExcelPath(
                            document.Editor,
                            "CE-Tools-Depression-Storage.xlsx",
                            out path))
                    {
                        SimpleXlsxWriter.Write(path, "Ponding Review", rows);
                        document.Editor.WriteMessage(
                            "\nCE_PONDINGREVIEW workbook created: {0}",
                            path);
                    }
                }

                if (edgeCount > MaximumGeneratedEdges)
                {
                    document.Editor.WriteMessage(
                        "\nAffected-area graphics were not created because {0:N0} perimeter edges exceed the {1:N0} review limit. Increase grid spacing or depth threshold.",
                        edgeCount,
                        MaximumGeneratedEdges);
                    return;
                }

                var review = new List<KeyValuePair<string, string>>
                {
                    Pair("Ponding zones", zones.Count.ToString(CultureInfo.InvariantCulture)),
                    Pair("Affected grid cells", zones.Sum(item => item.CellIndices.Count).ToString(CultureInfo.InvariantCulture)),
                    Pair("Affected area", totalArea.ToString("N3", CultureInfo.CurrentCulture) + " ha"),
                    Pair("Estimated terrain storage", totalVolume.ToString("N1", CultureInfo.CurrentCulture) + " m3"),
                    Pair("Maximum depression depth", maximumDepth.ToString("N3", CultureInfo.CurrentCulture) + " m"),
                    Pair("Perimeter review edges", edgeCount.ToString(CultureInfo.InvariantCulture)),
                    Pair("Source surface/boundary changed", "No"),
                    Pair("Model status", "Depression-to-spill storage screen — not flood depth, duration or hazard")
                };
                if (!PopupTablePresenter.ShowReview(
                        "CE Tools - Create Ponding Review Map",
                        "The map outlines sampled depression cells and labels each connected zone. It does not represent a simulated water surface.",
                        review,
                        "Create Review Map"))
                    return;

                int generated = CreateGraphics(
                    document.Database,
                    input,
                    sample,
                    zones);
                document.Editor.Regen();
                document.Editor.WriteMessage(
                    "\nCE_PONDINGREVIEW complete. Zones={0}; affected area={1:N3} ha; storage={2:N1} m3; generated graphics={3}.",
                    zones.Count,
                    totalArea,
                    totalVolume,
                    generated);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_PONDINGREVIEW failed. No source surface or boundary was modified. {0}",
                    exception.Message);
            }
        }

        private static List<PondingZone> BuildZones(
            HydrologySample sample,
            double unitsPerMetre,
            double minimumDepthMetres)
        {
            var candidates = new HashSet<int>();
            for (int index = 0; index < sample.Analysis.Active.Count; index++)
            {
                if (!sample.Analysis.Active[index]) continue;
                double depthMetres = sample.Analysis.FillDepth(index) / unitsPerMetre;
                if (depthMetres + Tolerance >= minimumDepthMetres)
                    candidates.Add(index);
            }

            var zones = new List<PondingZone>();
            var visited = new HashSet<int>();
            foreach (int seed in candidates.OrderBy(item => item))
            {
                if (!visited.Add(seed)) continue;
                var cells = new List<int>();
                var queue = new Queue<int>();
                queue.Enqueue(seed);
                while (queue.Count > 0)
                {
                    int current = queue.Dequeue();
                    cells.Add(current);
                    GridCell cell = sample.Analysis.CellOf(current);
                    for (int rowOffset = -1; rowOffset <= 1; rowOffset++)
                    {
                        for (int columnOffset = -1; columnOffset <= 1; columnOffset++)
                        {
                            if (rowOffset == 0 && columnOffset == 0) continue;
                            int row = cell.Row + rowOffset;
                            int column = cell.Column + columnOffset;
                            if (row < 0 || row >= sample.Rows ||
                                column < 0 || column >= sample.Columns)
                                continue;
                            int neighbour = row * sample.Columns + column;
                            if (candidates.Contains(neighbour) && visited.Add(neighbour))
                                queue.Enqueue(neighbour);
                        }
                    }
                }
                zones.Add(SummariseZone(
                    zones.Count + 1,
                    sample,
                    cells,
                    unitsPerMetre));
            }
            return zones
                .OrderByDescending(item => item.StorageCubicMetres)
                .ThenBy(item => item.ZoneNumber)
                .Select((item, index) => item.WithZoneNumber(index + 1))
                .ToList();
        }

        private static PondingZone SummariseZone(
            int zoneNumber,
            HydrologySample sample,
            IList<int> cells,
            double unitsPerMetre)
        {
            double cellAreaSquareMetres = sample.CellArea /
                (unitsPerMetre * unitsPerMetre);
            double volume = 0.0;
            double maximumDepth = 0.0;
            int deepest = cells[0];
            foreach (int index in cells)
            {
                double depthMetres = sample.Analysis.FillDepth(index) / unitsPerMetre;
                volume += depthMetres * cellAreaSquareMetres;
                if (depthMetres > maximumDepth)
                {
                    maximumDepth = depthMetres;
                    deepest = index;
                }
            }
            return new PondingZone(
                zoneNumber,
                cells,
                cells.Count * cellAreaSquareMetres / 10000.0,
                volume,
                maximumDepth,
                deepest);
        }

        private static List<IList<string>> BuildRows(
            IList<PondingZone> zones,
            HydrologySample sample)
        {
            var rows = new List<IList<string>>
            {
                new List<string>
                {
                    "ZONE", "CELLS", "AREA (ha)", "MAX DEPTH (m)",
                    "STORAGE (m3)", "DEEPEST GRID CELL", "DEEPEST POINT"
                }
            };
            foreach (PondingZone zone in zones)
            {
                GridCell deepest = sample.Analysis.CellOf(zone.DeepestCellIndex);
                Point3d point = SurfaceHydrologyCommands.CellPoint(
                    sample,
                    zone.DeepestCellIndex,
                    false);
                rows.Add(new List<string>
                {
                    "P" + zone.ZoneNumber.ToString(CultureInfo.InvariantCulture),
                    zone.CellIndices.Count.ToString(CultureInfo.InvariantCulture),
                    zone.AreaHectares.ToString("0.######", CultureInfo.InvariantCulture),
                    zone.MaximumDepthMetres.ToString("0.######", CultureInfo.InvariantCulture),
                    zone.StorageCubicMetres.ToString("0.###", CultureInfo.InvariantCulture),
                    "R" + deepest.Row + " C" + deepest.Column,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "X {0:0.###}; Y {1:0.###}; Z {2:0.###}",
                        point.X,
                        point.Y,
                        point.Z)
                });
            }
            return rows;
        }

        private static int CreateGraphics(
            Database database,
            HydrologyCivilInput input,
            HydrologySample sample,
            IList<PondingZone> zones)
        {
            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                EnsureRegApp(database, transaction);
                ObjectId layerId = GetOrCreateLayer(
                    database,
                    transaction,
                    ReviewLayer);
                BlockTableRecord currentSpace = transaction.GetObject(
                    database.CurrentSpaceId,
                    OpenMode.ForWrite,
                    false) as BlockTableRecord;
                if (currentSpace == null)
                    throw new InvalidOperationException(
                        "The current drawing space could not be opened.");
                int created = 0;
                foreach (PondingZone zone in zones)
                {
                    var cellSet = new HashSet<int>(zone.CellIndices);
                    short colour = DepthColour(zone.MaximumDepthMetres);
                    foreach (PondingMapEdge edge in ExposedEdges(
                        sample,
                        zone.CellIndices,
                        cellSet))
                    {
                        var line = new Line(edge.Start, edge.End);
                        line.SetDatabaseDefaults(database);
                        line.LayerId = layerId;
                        line.Color = Color.FromColorIndex(ColorMethod.ByAci, colour);
                        Tag(line, "PondingPerimeter", input, zone.ZoneNumber);
                        Append(currentSpace, transaction, line);
                        created++;
                    }

                    Point3d deepest = SurfaceHydrologyCommands.CellPoint(
                        sample,
                        zone.DeepestCellIndex,
                        false);
                    var marker = new Circle(
                        deepest,
                        Vector3d.ZAxis,
                        Math.Max(sample.CellSize * 0.3, Tolerance));
                    marker.SetDatabaseDefaults(database);
                    marker.LayerId = layerId;
                    marker.Color = Color.FromColorIndex(ColorMethod.ByAci, colour);
                    Tag(marker, "PondingDeepestPoint", input, zone.ZoneNumber);
                    Append(currentSpace, transaction, marker);
                    created++;

                    var label = new MText();
                    label.SetDatabaseDefaults(database);
                    label.Location = deepest + new Vector3d(
                        sample.CellSize * 0.45,
                        sample.CellSize * 0.45,
                        0.0);
                    label.TextHeight = Math.Max(database.Textsize, sample.CellSize * 0.16);
                    label.Contents = string.Format(
                        CultureInfo.CurrentCulture,
                        "P{0}\\PAREA {1:N3} ha\\PMAX DEPTH {2:N3} m\\PSTORAGE {3:N1} m3",
                        zone.ZoneNumber,
                        zone.AreaHectares,
                        zone.MaximumDepthMetres,
                        zone.StorageCubicMetres);
                    label.Attachment = AttachmentPoint.BottomLeft;
                    label.LayerId = layerId;
                    label.Color = Color.FromColorIndex(ColorMethod.ByAci, colour);
                    Tag(label, "PondingLabel", input, zone.ZoneNumber);
                    Append(currentSpace, transaction, label);
                    created++;
                }
                transaction.Commit();
                return created;
            }
        }

        private static IEnumerable<PondingMapEdge> ExposedEdges(
            HydrologySample sample,
            IEnumerable<int> zoneCells,
            ISet<int> cellSet)
        {
            foreach (int index in zoneCells)
            {
                GridCell cell = sample.Analysis.CellOf(index);
                double left = sample.OriginX + cell.Column * sample.CellSize;
                double right = left + sample.CellSize;
                double bottom = sample.OriginY + cell.Row * sample.CellSize;
                double top = bottom + sample.CellSize;
                if (!ContainsCell(sample, cellSet, cell.Row - 1, cell.Column))
                    yield return new PondingMapEdge(
                        new Point3d(left, bottom, 0.0),
                        new Point3d(right, bottom, 0.0));
                if (!ContainsCell(sample, cellSet, cell.Row, cell.Column + 1))
                    yield return new PondingMapEdge(
                        new Point3d(right, bottom, 0.0),
                        new Point3d(right, top, 0.0));
                if (!ContainsCell(sample, cellSet, cell.Row + 1, cell.Column))
                    yield return new PondingMapEdge(
                        new Point3d(right, top, 0.0),
                        new Point3d(left, top, 0.0));
                if (!ContainsCell(sample, cellSet, cell.Row, cell.Column - 1))
                    yield return new PondingMapEdge(
                        new Point3d(left, top, 0.0),
                        new Point3d(left, bottom, 0.0));
            }
        }

        private static int CountExposedEdges(
            IEnumerable<int> zoneCells,
            int rows,
            int columns)
        {
            var cells = new HashSet<int>(zoneCells);
            int count = 0;
            foreach (int index in cells)
            {
                int row = index / columns;
                int column = index % columns;
                if (row == 0 || !cells.Contains((row - 1) * columns + column)) count++;
                if (row == rows - 1 || !cells.Contains((row + 1) * columns + column)) count++;
                if (column == 0 || !cells.Contains(row * columns + column - 1)) count++;
                if (column == columns - 1 || !cells.Contains(row * columns + column + 1)) count++;
            }
            return count;
        }

        private static bool ContainsCell(
            HydrologySample sample,
            ISet<int> cells,
            int row,
            int column)
        {
            return row >= 0 && row < sample.Rows &&
                   column >= 0 && column < sample.Columns &&
                   cells.Contains(row * sample.Columns + column);
        }

        private static short DepthColour(double depthMetres)
        {
            if (depthMetres >= 1.0) return 1;
            if (depthMetres >= 0.5) return 30;
            if (depthMetres >= 0.2) return 2;
            return 3;
        }

        private static void Tag(
            Entity entity,
            string role,
            HydrologyCivilInput input,
            int zoneNumber)
        {
            entity.XData = new ResultBuffer(
                new TypedValue(
                    (int)DxfCode.ExtendedDataRegAppName,
                    RegAppName),
                new TypedValue(
                    (int)DxfCode.ExtendedDataAsciiString,
                    "Role=" + role),
                new TypedValue(
                    (int)DxfCode.ExtendedDataAsciiString,
                    "Zone=" + zoneNumber.ToString(CultureInfo.InvariantCulture)),
                new TypedValue(
                    (int)DxfCode.ExtendedDataAsciiString,
                    "Surface=" + input.SurfaceId.Handle),
                new TypedValue(
                    (int)DxfCode.ExtendedDataAsciiString,
                    "Boundary=" + input.BoundaryId.Handle));
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
            layer.Color = Color.FromColorIndex(ColorMethod.ByAci, 3);
            ObjectId id = table.Add(layer);
            transaction.AddNewlyCreatedDBObject(layer, true);
            return id;
        }

        private static void Append(
            BlockTableRecord currentSpace,
            Transaction transaction,
            Entity entity)
        {
            currentSpace.AppendEntity(entity);
            transaction.AddNewlyCreatedDBObject(entity, true);
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

        private static bool PromptYesNo(
            Editor editor,
            string question,
            bool defaultYes)
        {
            var options = new PromptKeywordOptions(
                "\n" + question + " [Yes/No] <" +
                (defaultYes ? "Yes" : "No") + ">: ")
            {
                AllowNone = true
            };
            options.Keywords.Add("Yes");
            options.Keywords.Add("No");
            PromptResult result = editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel) return false;
            return result.Status == PromptStatus.None
                ? defaultYes
                : string.Equals(
                    result.StringResult,
                    "Yes",
                    StringComparison.OrdinalIgnoreCase);
        }

        private static bool PromptExcelPath(
            Editor editor,
            string defaultName,
            out string path)
        {
            path = string.Empty;
            var options = new PromptSaveFileOptions(
                "\nChoose the depression-storage Excel workbook path: ")
            {
                DialogCaption = "Export CE Tools Depression Storage Review",
                Filter = "Excel Workbook (*.xlsx)|*.xlsx",
                InitialFileName = defaultName
            };
            PromptFileNameResult result = editor.GetFileNameForSave(options);
            if (result.Status != PromptStatus.OK) return false;
            path = result.StringResult;
            if (!path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
                path += ".xlsx";
            return true;
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

    internal sealed class PondingZone
    {
        public PondingZone(
            int zoneNumber,
            IList<int> cellIndices,
            double areaHectares,
            double storageCubicMetres,
            double maximumDepthMetres,
            int deepestCellIndex)
        {
            ZoneNumber = zoneNumber;
            CellIndices = new List<int>(cellIndices);
            AreaHectares = areaHectares;
            StorageCubicMetres = storageCubicMetres;
            MaximumDepthMetres = maximumDepthMetres;
            DeepestCellIndex = deepestCellIndex;
        }

        public int ZoneNumber { get; private set; }
        public IList<int> CellIndices { get; private set; }
        public double AreaHectares { get; private set; }
        public double StorageCubicMetres { get; private set; }
        public double MaximumDepthMetres { get; private set; }
        public int DeepestCellIndex { get; private set; }

        public PondingZone WithZoneNumber(int zoneNumber)
        {
            return new PondingZone(
                zoneNumber,
                CellIndices,
                AreaHectares,
                StorageCubicMetres,
                MaximumDepthMetres,
                DeepestCellIndex);
        }
    }

    internal sealed class PondingMapEdge
    {
        public PondingMapEdge(Point3d start, Point3d end)
        {
            Start = start;
            End = end;
        }

        public Point3d Start { get; private set; }
        public Point3d End { get; private set; }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.SpecialistModelExchangeCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Vendor-neutral exchange package and specialist-result import framework.
    /// The package is intentionally open and auditable: CSV geometry, a JSON
    /// manifest, explicit units/coordinate metadata and SHA-256 checksums.
    /// It does not claim direct vendor API integration or certified model parity.
    /// </summary>
    public sealed class SpecialistModelExchangeCommands
    {
        private const string ResultRegApp = "CE_MODEL_RESULT_IMPORT";
        private const string ResultLayerPrefix = "CE-MODEL-RESULT-";
        private const int MaximumImportRows = 250000;
        private const int MaximumExportVertices = 1000000;

        [CommandMethod("CE_TOOLS", "CE_MODELEXCHANGETOOLS", CommandFlags.Modal)]
        public void ModelExchangeTools()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            var options = new PromptKeywordOptions(
                "\nSpecialist model exchange [Export/Template/Import/Info/Clear] <Export>: ")
            {
                AllowNone = true
            };
            foreach (string keyword in new[] { "Export", "Template", "Import", "Info", "Clear" })
                options.Keywords.Add(keyword);

            PromptResult result = document.Editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel) return;
            string choice = result.Status == PromptStatus.OK ? result.StringResult : "Export";
            string command;
            if (Equal(choice, "Template")) command = "CE_MODELRESULTTEMPLATE ";
            else if (Equal(choice, "Import")) command = "CE_MODELRESULTIMPORT ";
            else if (Equal(choice, "Info")) command = "CE_MODELRESULTINFO ";
            else if (Equal(choice, "Clear")) command = "CE_MODELRESULTCLEAR ";
            else command = "CE_MODELEXPORTPACKAGE ";
            document.SendStringToExecute(command, true, false, true);
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_MODELEXPORTPACKAGE",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void ExportPackage()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            Editor editor = document.Editor;

            PromptSelectionResult selection = GetSelection(
                editor,
                "\nSelect geometry to include in the specialist-model exchange package: ");
            if (selection.Status != PromptStatus.OK || selection.Value.Count == 0) return;

            double unitsPerMetre;
            if (!PromptPositiveDouble(editor, "\nDrawing units per metre <1.0>: ", 1.0, out unitsPerMetre))
                return;

            double sampleSpacing;
            if (!PromptPositiveDouble(
                    editor,
                    "\nMaximum curve sampling spacing in drawing units <5.0>: ",
                    5.0,
                    out sampleSpacing))
                return;

            string target;
            if (!PromptTarget(editor, out target)) return;

            var saveOptions = new PromptSaveFileOptions(
                "\nChoose the exchange-package manifest path: ")
            {
                Filter = "CE Model Exchange Manifest (*.json)|*.json",
                DialogCaption = "Create CE Tools Specialist Model Exchange Package",
                InitialFileName = "CE-Model-Exchange.json"
            };
            PromptFileNameResult fileResult = editor.GetFileNameForSave(saveOptions);
            if (fileResult.Status != PromptStatus.OK) return;

            string manifestPath = EnsureExtension(fileResult.StringResult, ".json");
            string folder = Path.GetDirectoryName(manifestPath) ?? Environment.CurrentDirectory;
            string baseName = Path.GetFileNameWithoutExtension(manifestPath);
            string geometryPath = Path.Combine(folder, baseName + "-geometry.csv");

            if (File.Exists(manifestPath) || File.Exists(geometryPath))
            {
                editor.WriteMessage(
                    "\nCE_MODELEXPORTPACKAGE stopped. Existing package files will not be overwritten. Choose a new manifest name.");
                return;
            }

            try
            {
                Directory.CreateDirectory(folder);
                List<ExchangeVertex> vertices = ReadSelectedGeometry(
                    document.Database,
                    selection.Value.GetObjectIds(),
                    sampleSpacing);
                if (vertices.Count == 0)
                    throw new InvalidOperationException("No supported point or curve geometry was extracted.");
                if (vertices.Count > MaximumExportVertices)
                    throw new InvalidOperationException(
                        "The extracted geometry exceeds the " + MaximumExportVertices.ToString("N0", CultureInfo.InvariantCulture) +
                        "-vertex safety limit. Increase the sampling spacing or divide the package.");

                WriteGeometryCsv(geometryPath, vertices);
                string geometryHash = ComputeSha256(geometryPath);
                string sourceDrawing = document.Database.Filename ?? string.Empty;
                string sourceHash = File.Exists(sourceDrawing) ? ComputeSha256(sourceDrawing) : string.Empty;
                string coordinateCode = ReadCoordinateSystemCode();
                string units = document.Database.Insunits.ToString();

                WriteManifest(
                    manifestPath,
                    target,
                    sourceDrawing,
                    sourceHash,
                    units,
                    unitsPerMetre,
                    coordinateCode,
                    selection.Value.Count,
                    vertices.Count,
                    Path.GetFileName(geometryPath),
                    geometryHash,
                    sampleSpacing);

                editor.WriteMessage(
                    "\nCE_MODELEXPORTPACKAGE complete. Manifest={0}; geometry={1}; objects={2}; vertices={3}; target={4}.",
                    manifestPath,
                    geometryPath,
                    selection.Value.Count,
                    vertices.Count,
                    target);
                editor.WriteMessage(
                    "\nThe package is vendor-neutral. Verify coordinate reference, units, geometry interpretation and model-specific requirements before import into specialist software.");
            }
            catch (System.Exception exception)
            {
                SafeDelete(geometryPath);
                SafeDelete(manifestPath);
                editor.WriteMessage("\nCE_MODELEXPORTPACKAGE stopped. {0}", exception.Message);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_MODELRESULTTEMPLATE", CommandFlags.Modal)]
        public void CreateResultTemplate()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            var saveOptions = new PromptSaveFileOptions(
                "\nChoose the specialist-result CSV template path: ")
            {
                Filter = "Comma-separated values (*.csv)|*.csv",
                DialogCaption = "Create CE Tools Result Import Template",
                InitialFileName = "CE-Model-Results-Template.csv"
            };
            PromptFileNameResult result = document.Editor.GetFileNameForSave(saveOptions);
            if (result.Status != PromptStatus.OK) return;
            string path = EnsureExtension(result.StringResult, ".csv");
            if (File.Exists(path))
            {
                document.Editor.WriteMessage(
                    "\nCE_MODELRESULTTEMPLATE stopped. Existing files are not overwritten.");
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Environment.CurrentDirectory);
            File.WriteAllText(
                path,
                "X,Y,Z,Depth,Velocity,WaterLevel,Scenario,Time\r\n" +
                "1000.000,2000.000,0.000,0.250,0.800,100.250,Example,00:30:00\r\n",
                new UTF8Encoding(false));
            document.Editor.WriteMessage(
                "\nCE_MODELRESULTTEMPLATE complete. Required columns are X and Y. Optional columns are Z, Depth, Velocity, WaterLevel, Scenario and Time. File={0}",
                path);
        }

        [CommandMethod("CE_TOOLS", "CE_MODELRESULTIMPORT", CommandFlags.Modal | CommandFlags.Redraw)]
        public void ImportResults()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            Editor editor = document.Editor;

            var openOptions = new PromptOpenFileOptions(
                "\nSelect the specialist-model result CSV: ")
            {
                Filter = "Comma-separated values (*.csv)|*.csv",
                DialogCaption = "Import Specialist Model Results"
            };
            PromptFileNameResult fileResult = editor.GetFileNameForOpen(openOptions);
            if (fileResult.Status != PromptStatus.OK) return;
            string path = fileResult.StringResult;
            if (!File.Exists(path))
            {
                editor.WriteMessage("\nCE_MODELRESULTIMPORT stopped. File not found: {0}", path);
                return;
            }

            double coordinateUnitsPerMetre;
            if (!PromptPositiveDouble(
                    editor,
                    "\nCSV coordinate units per metre <1.0>: ",
                    1.0,
                    out coordinateUnitsPerMetre))
                return;
            double drawingUnitsPerMetre;
            if (!PromptPositiveDouble(
                    editor,
                    "\nDrawing units per metre <1.0>: ",
                    1.0,
                    out drawingUnitsPerMetre))
                return;
            double markerRadius;
            if (!PromptPositiveDouble(
                    editor,
                    "\nResult marker radius in drawing units <0.5>: ",
                    0.5,
                    out markerRadius))
                return;

            try
            {
                ResultImportData data = ReadResults(path);
                double coordinateScale = drawingUnitsPerMetre / coordinateUnitsPerMetre;
                ResultImportSummary summary = CreateResultGraphics(
                    document.Database,
                    path,
                    data,
                    coordinateScale,
                    markerRadius);
                editor.Regen();
                editor.WriteMessage(
                    "\nCE_MODELRESULTIMPORT complete. Rows={0}; graphics={1}; scenarios={2}; depth range={3}; velocity range={4}.",
                    data.Rows.Count,
                    summary.Created,
                    summary.Scenarios,
                    FormatRange(summary.MinimumDepth, summary.MaximumDepth, "m"),
                    FormatRange(summary.MinimumVelocity, summary.MaximumVelocity, "m/s"));
                editor.WriteMessage(
                    "\nImported graphics are review aids only. Verify CRS, datum, units, scenario/time, interpolation and specialist-model assumptions before engineering use.");
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage("\nCE_MODELRESULTIMPORT stopped. {0}", exception.Message);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_MODELRESULTINFO", CommandFlags.Modal | CommandFlags.Redraw)]
        public void ResultInformation()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            Editor editor = document.Editor;

            PromptEntityResult selected = editor.GetEntity(
                "\nSelect one imported specialist-result marker or press Esc to cancel: ");
            if (selected.Status != PromptStatus.OK) return;

            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                Entity entity = transaction.GetObject(
                    selected.ObjectId,
                    OpenMode.ForRead,
                    false) as Entity;
                ImportedResultRecord record = ReadResultRecord(entity);
                if (record == null)
                {
                    editor.WriteMessage(
                        "\nCE_MODELRESULTINFO: the selected object is not a CE Tools imported result graphic.");
                    return;
                }

                editor.WriteMessage(
                    "\nCE model result: Source={0}; Scenario={1}; Time={2}; X={3:N3}; Y={4:N3}; Z={5:N3}; Depth={6}; Velocity={7}; WaterLevel={8}; HazardIndex={9}.",
                    record.Source,
                    EmptyAs(record.Scenario, "<Not supplied>"),
                    EmptyAs(record.Time, "<Not supplied>"),
                    record.X,
                    record.Y,
                    record.Z,
                    FormatOptional(record.Depth, "m"),
                    FormatOptional(record.Velocity, "m/s"),
                    FormatOptional(record.WaterLevel, "m"),
                    FormatOptional(record.HazardIndex, string.Empty));
            }
        }

        [CommandMethod("CE_TOOLS", "CE_MODELRESULTCLEAR", CommandFlags.Modal | CommandFlags.Redraw)]
        public void ClearResults()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            Editor editor = document.Editor;
            var options = new PromptKeywordOptions(
                "\nErase all CE Tools imported specialist-result graphics [Yes/No] <No>: ")
            {
                AllowNone = true
            };
            options.Keywords.Add("Yes");
            options.Keywords.Add("No");
            PromptResult result = editor.GetKeywords(options);
            if (result.Status != PromptStatus.OK || !Equal(result.StringResult, "Yes"))
            {
                editor.WriteMessage("\nCE_MODELRESULTCLEAR cancelled.");
                return;
            }

            int erased = 0;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = transaction.GetObject(
                    document.Database.CurrentSpaceId,
                    OpenMode.ForRead,
                    false) as BlockTableRecord;
                if (space != null)
                {
                    foreach (ObjectId id in space)
                    {
                        Entity entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                        if (!HasResultRecord(entity)) continue;
                        entity.UpgradeOpen();
                        entity.Erase();
                        erased++;
                    }
                }
                transaction.Commit();
            }
            editor.Regen();
            editor.WriteMessage(
                "\nCE_MODELRESULTCLEAR complete. Erased imported review graphics={0}. Source files and unrelated drawing objects were unchanged.",
                erased);
        }

        private static List<ExchangeVertex> ReadSelectedGeometry(
            Database database,
            IEnumerable<ObjectId> ids,
            double sampleSpacing)
        {
            var rows = new List<ExchangeVertex>();
            int featureIndex = 0;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids)
                {
                    Entity entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                    if (entity == null || entity.IsErased) continue;
                    featureIndex++;
                    string featureId = "F" + featureIndex.ToString("D6", CultureInfo.InvariantCulture);
                    Curve curve = entity as Curve;
                    if (curve != null)
                    {
                        List<Point3d> points = SampleCurve(curve, sampleSpacing);
                        for (int index = 0; index < points.Count; index++)
                        {
                            rows.Add(new ExchangeVertex(
                                featureId,
                                0,
                                index,
                                entity.GetType().Name,
                                entity.Layer,
                                entity.Handle.ToString(),
                                points[index],
                                index == 0 ? "Start" : index == points.Count - 1 ? "End" : "Vertex"));
                        }
                        continue;
                    }

                    Point3d point;
                    DBPoint dbPoint = entity as DBPoint;
                    BlockReference block = entity as BlockReference;
                    if (dbPoint != null) point = dbPoint.Position;
                    else if (block != null) point = block.Position;
                    else
                    {
                        try
                        {
                            Extents3d extents = entity.GeometricExtents;
                            point = new Point3d(
                                (extents.MinPoint.X + extents.MaxPoint.X) * 0.5,
                                (extents.MinPoint.Y + extents.MaxPoint.Y) * 0.5,
                                (extents.MinPoint.Z + extents.MaxPoint.Z) * 0.5);
                        }
                        catch
                        {
                            continue;
                        }
                    }
                    rows.Add(new ExchangeVertex(
                        featureId,
                        0,
                        0,
                        entity.GetType().Name,
                        entity.Layer,
                        entity.Handle.ToString(),
                        point,
                        "Point"));
                }
            }
            return rows;
        }

        private static List<Point3d> SampleCurve(Curve curve, double spacing)
        {
            var points = new List<Point3d>();
            try
            {
                double startDistance = curve.GetDistanceAtParameter(curve.StartParam);
                double endDistance = curve.GetDistanceAtParameter(curve.EndParam);
                double length = Math.Abs(endDistance - startDistance);
                if (!IsFinite(length) || length <= 1e-9)
                {
                    points.Add(curve.StartPoint);
                    return points;
                }
                int intervals = Math.Max(1, (int)Math.Ceiling(length / spacing));
                if (intervals > 100000)
                    throw new InvalidOperationException("One selected curve exceeds the 100,000-interval safety limit.");
                for (int index = 0; index <= intervals; index++)
                {
                    double distance = length * index / intervals;
                    points.Add(curve.GetPointAtDist(distance));
                }
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                points.Clear();
                points.Add(curve.StartPoint);
                if (curve.EndPoint.DistanceTo(curve.StartPoint) > 1e-9)
                    points.Add(curve.EndPoint);
            }
            return points;
        }

        private static void WriteGeometryCsv(string path, IEnumerable<ExchangeVertex> rows)
        {
            using (var writer = new StreamWriter(path, false, new UTF8Encoding(false)))
            {
                writer.WriteLine("FeatureId,PartIndex,VertexIndex,ObjectType,Layer,Handle,X,Y,Z,Role");
                foreach (ExchangeVertex row in rows)
                {
                    writer.WriteLine(string.Join(",", new[]
                    {
                        Csv(row.FeatureId),
                        row.PartIndex.ToString(CultureInfo.InvariantCulture),
                        row.VertexIndex.ToString(CultureInfo.InvariantCulture),
                        Csv(row.ObjectType),
                        Csv(row.Layer),
                        Csv(row.Handle),
                        row.Point.X.ToString("R", CultureInfo.InvariantCulture),
                        row.Point.Y.ToString("R", CultureInfo.InvariantCulture),
                        row.Point.Z.ToString("R", CultureInfo.InvariantCulture),
                        Csv(row.Role)
                    }));
                }
            }
        }

        private static void WriteManifest(
            string path,
            string target,
            string sourceDrawing,
            string sourceHash,
            string drawingUnits,
            double unitsPerMetre,
            string coordinateCode,
            int objectCount,
            int vertexCount,
            string geometryFile,
            string geometryHash,
            double sampleSpacing)
        {
            var text = new StringBuilder();
            text.AppendLine("{");
            JsonLine(text, "schema", "CE_MODEL_EXCHANGE_1", true);
            JsonLine(text, "createdUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), true);
            JsonLine(text, "target", target, true);
            JsonLine(text, "sourceDrawing", sourceDrawing, true);
            JsonLine(text, "sourceDrawingSha256", sourceHash, true);
            JsonLine(text, "drawingUnits", drawingUnits, true);
            JsonNumber(text, "drawingUnitsPerMetre", unitsPerMetre, true);
            JsonLine(text, "coordinateSystemCode", coordinateCode, true);
            JsonNumber(text, "selectedObjectCount", objectCount, true);
            JsonNumber(text, "geometryVertexCount", vertexCount, true);
            JsonNumber(text, "curveSampleSpacingDrawingUnits", sampleSpacing, true);
            JsonLine(text, "geometryFile", geometryFile, true);
            JsonLine(text, "geometrySha256", geometryHash, true);
            JsonLine(text, "intendedUse", "Preliminary interoperable geometry exchange and review", true);
            JsonLine(text, "disclaimer", "Verify CRS, datum, units, topology, model-specific interpretation and engineering assumptions before use.", false);
            text.AppendLine("}");
            File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
        }

        private static ResultImportData ReadResults(string path)
        {
            string[] lines = File.ReadAllLines(path);
            if (lines.Length < 2)
                throw new InvalidOperationException("The result CSV contains no data rows.");
            if (lines.Length - 1 > MaximumImportRows)
                throw new InvalidOperationException(
                    "The result CSV exceeds the " + MaximumImportRows.ToString("N0", CultureInfo.InvariantCulture) +
                    "-row safety limit.");

            List<string> headings = ParseCsvLine(lines[0]);
            var columns = headings
                .Select((name, index) => new { Name = NormalizeHeading(name), Index = index })
                .GroupBy(item => item.Name)
                .ToDictionary(group => group.Key, group => group.First().Index, StringComparer.OrdinalIgnoreCase);
            if (!columns.ContainsKey("X") || !columns.ContainsKey("Y"))
                throw new InvalidOperationException("The result CSV must contain X and Y columns.");

            var rows = new List<ImportedResultRow>();
            for (int lineIndex = 1; lineIndex < lines.Length; lineIndex++)
            {
                if (string.IsNullOrWhiteSpace(lines[lineIndex])) continue;
                List<string> values = ParseCsvLine(lines[lineIndex]);
                double x = RequiredNumber(values, columns, "X", lineIndex + 1);
                double y = RequiredNumber(values, columns, "Y", lineIndex + 1);
                double? z = OptionalNumber(values, columns, "Z");
                double? depth = OptionalNumber(values, columns, "DEPTH");
                double? velocity = OptionalNumber(values, columns, "VELOCITY");
                double? waterLevel = OptionalNumber(values, columns, "WATERLEVEL");
                string scenario = OptionalText(values, columns, "SCENARIO");
                string time = OptionalText(values, columns, "TIME");
                rows.Add(new ImportedResultRow(x, y, z, depth, velocity, waterLevel, scenario, time));
            }
            if (rows.Count == 0)
                throw new InvalidOperationException("The result CSV contains no valid data rows.");
            return new ResultImportData(path, rows);
        }

        private static ResultImportSummary CreateResultGraphics(
            Database database,
            string sourcePath,
            ResultImportData data,
            double coordinateScale,
            double markerRadius)
        {
            var summary = new ResultImportSummary();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                EnsureRegApp(database, transaction, ResultRegApp);
                BlockTableRecord space = transaction.GetObject(
                    database.CurrentSpaceId,
                    OpenMode.ForWrite,
                    false) as BlockTableRecord;
                if (space == null)
                    throw new InvalidOperationException("The current drawing space could not be opened.");

                var layerIds = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);
                foreach (ImportedResultRow row in data.Rows)
                {
                    string category = ResultCategory(row.Depth, row.Velocity);
                    ObjectId layerId;
                    if (!layerIds.TryGetValue(category, out layerId))
                    {
                        layerId = GetOrCreateResultLayer(
                            database,
                            transaction,
                            category,
                            ResultColour(category));
                        layerIds[category] = layerId;
                    }

                    double x = row.X * coordinateScale;
                    double y = row.Y * coordinateScale;
                    double z = (row.WaterLevel ?? row.Z ?? 0.0) * coordinateScale;
                    var circle = new Circle(
                        new Point3d(x, y, z),
                        Vector3d.ZAxis,
                        markerRadius);
                    circle.SetDatabaseDefaults(database);
                    circle.LayerId = layerId;
                    circle.ColorIndex = 256;
                    space.AppendEntity(circle);
                    transaction.AddNewlyCreatedDBObject(circle, true);
                    WriteResultRecord(circle, sourcePath, row, x, y, z);
                    summary.Include(row);
                    summary.Created++;
                }
                transaction.Commit();
            }
            return summary;
        }

        private static void WriteResultRecord(
            Entity entity,
            string sourcePath,
            ImportedResultRow row,
            double x,
            double y,
            double z)
        {
            entity.XData = new ResultBuffer(
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, ResultRegApp),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, "Schema=1"),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, "Source=" + sourcePath),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, "Scenario=" + (row.Scenario ?? string.Empty)),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, "Time=" + (row.Time ?? string.Empty)),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, "X=" + x.ToString("R", CultureInfo.InvariantCulture)),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, "Y=" + y.ToString("R", CultureInfo.InvariantCulture)),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, "Z=" + z.ToString("R", CultureInfo.InvariantCulture)),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, "Depth=" + OptionalInvariant(row.Depth)),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, "Velocity=" + OptionalInvariant(row.Velocity)),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, "WaterLevel=" + OptionalInvariant(row.WaterLevel)),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, "HazardIndex=" + OptionalInvariant(HazardIndex(row.Depth, row.Velocity))),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, "ImportedUtc=" + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)));
        }

        private static ImportedResultRecord ReadResultRecord(Entity entity)
        {
            if (entity == null) return null;
            ResultBuffer buffer = entity.GetXDataForApplication(ResultRegApp);
            if (buffer == null) return null;
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (TypedValue value in buffer)
            {
                string text = value.Value as string;
                if (string.IsNullOrWhiteSpace(text)) continue;
                int equals = text.IndexOf('=');
                if (equals <= 0) continue;
                values[text.Substring(0, equals)] = text.Substring(equals + 1);
            }
            return new ImportedResultRecord(
                Get(values, "Source"),
                Get(values, "Scenario"),
                Get(values, "Time"),
                ParseOptional(Get(values, "X")) ?? 0.0,
                ParseOptional(Get(values, "Y")) ?? 0.0,
                ParseOptional(Get(values, "Z")) ?? 0.0,
                ParseOptional(Get(values, "Depth")),
                ParseOptional(Get(values, "Velocity")),
                ParseOptional(Get(values, "WaterLevel")),
                ParseOptional(Get(values, "HazardIndex")));
        }

        private static bool HasResultRecord(Entity entity)
        {
            return entity != null && entity.GetXDataForApplication(ResultRegApp) != null;
        }

        private static ObjectId GetOrCreateResultLayer(
            Database database,
            Transaction transaction,
            string category,
            short colour)
        {
            string name = ResultLayerPrefix + category;
            LayerTable table = transaction.GetObject(
                database.LayerTableId,
                OpenMode.ForRead,
                false) as LayerTable;
            if (table == null)
                throw new InvalidOperationException("The layer table could not be opened.");
            if (table.Has(name)) return table[name];
            table.UpgradeOpen();
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

        private static void EnsureRegApp(Database database, Transaction transaction, string name)
        {
            RegAppTable table = transaction.GetObject(
                database.RegAppTableId,
                OpenMode.ForRead,
                false) as RegAppTable;
            if (table == null || table.Has(name)) return;
            table.UpgradeOpen();
            var record = new RegAppTableRecord { Name = name };
            table.Add(record);
            transaction.AddNewlyCreatedDBObject(record, true);
        }

        private static string ResultCategory(double? depth, double? velocity)
        {
            double hazard = HazardIndex(depth, velocity) ?? -1.0;
            if (hazard >= 1.5) return "HAZARD-HIGH";
            if (hazard >= 0.75) return "HAZARD-MODERATE";
            if (depth.HasValue && depth.Value >= 1.0) return "DEPTH-1_00-PLUS";
            if (depth.HasValue && depth.Value >= 0.5) return "DEPTH-0_50-1_00";
            if (depth.HasValue && depth.Value >= 0.15) return "DEPTH-0_15-0_50";
            if (depth.HasValue && depth.Value > 0.0) return "DEPTH-0_00-0_15";
            return "NO-DEPTH";
        }

        private static short ResultColour(string category)
        {
            if (category == "HAZARD-HIGH") return 1;
            if (category == "HAZARD-MODERATE") return 30;
            if (category == "DEPTH-1_00-PLUS") return 6;
            if (category == "DEPTH-0_50-1_00") return 5;
            if (category == "DEPTH-0_15-0_50") return 4;
            if (category == "DEPTH-0_00-0_15") return 3;
            return 8;
        }

        private static double? HazardIndex(double? depth, double? velocity)
        {
            if (!depth.HasValue || !velocity.HasValue) return null;
            if (!IsFinite(depth.Value) || !IsFinite(velocity.Value)) return null;
            return depth.Value * (velocity.Value + 0.5);
        }

        private static bool PromptTarget(Editor editor, out string target)
        {
            var options = new PromptKeywordOptions(
                "\nExchange target [Generic/HECRAS/InfraWorks/Twinmotion/Revit/Other] <Generic>: ")
            {
                AllowNone = true
            };
            foreach (string keyword in new[] { "Generic", "HECRAS", "InfraWorks", "Twinmotion", "Revit", "Other" })
                options.Keywords.Add(keyword);
            PromptResult result = editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel)
            {
                target = string.Empty;
                return false;
            }
            target = result.Status == PromptStatus.OK ? result.StringResult : "Generic";
            return true;
        }

        private static bool PromptPositiveDouble(
            Editor editor,
            string message,
            double defaultValue,
            out double value)
        {
            var options = new PromptDoubleOptions(message)
            {
                AllowNone = true,
                AllowNegative = false,
                AllowZero = false,
                DefaultValue = defaultValue
            };
            PromptDoubleResult result = editor.GetDouble(options);
            if (result.Status == PromptStatus.Cancel)
            {
                value = 0.0;
                return false;
            }
            value = result.Status == PromptStatus.OK ? result.Value : defaultValue;
            return IsFinite(value) && value > 0.0;
        }

        private static PromptSelectionResult GetSelection(Editor editor, string message)
        {
            PromptSelectionResult implied = editor.SelectImplied();
            if (implied.Status == PromptStatus.OK && implied.Value.Count > 0)
            {
                editor.SetImpliedSelection(new ObjectId[0]);
                return implied;
            }
            return editor.GetSelection(new PromptSelectionOptions
            {
                MessageForAdding = message,
                AllowDuplicates = false,
                RejectObjectsFromNonCurrentSpace = true
            });
        }

        private static string ReadCoordinateSystemCode()
        {
            try
            {
                object civilDocument = CivilApplication.ActiveDocument;
                if (civilDocument == null) return string.Empty;
                object settings = GetProperty(civilDocument, "Settings");
                object drawingSettings = GetProperty(settings, "DrawingSettings");
                object unitZone = GetProperty(drawingSettings, "UnitZoneSettings");
                object code = GetProperty(unitZone, "CoordinateSystemCode");
                return code == null ? string.Empty : Convert.ToString(code, CultureInfo.InvariantCulture);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static object GetProperty(object source, string name)
        {
            if (source == null) return null;
            var property = source.GetType().GetProperty(name);
            return property == null ? null : property.GetValue(source, null);
        }

        private static string ComputeSha256(string path)
        {
            using (SHA256 hash = SHA256.Create())
            using (FileStream stream = File.OpenRead(path))
            {
                return string.Concat(hash.ComputeHash(stream).Select(value => value.ToString("x2", CultureInfo.InvariantCulture)));
            }
        }

        private static List<string> ParseCsvLine(string line)
        {
            var values = new List<string>();
            var current = new StringBuilder();
            bool quoted = false;
            for (int index = 0; index < line.Length; index++)
            {
                char character = line[index];
                if (character == '"')
                {
                    if (quoted && index + 1 < line.Length && line[index + 1] == '"')
                    {
                        current.Append('"');
                        index++;
                    }
                    else quoted = !quoted;
                }
                else if (character == ',' && !quoted)
                {
                    values.Add(current.ToString().Trim());
                    current.Clear();
                }
                else current.Append(character);
            }
            values.Add(current.ToString().Trim());
            return values;
        }

        private static double RequiredNumber(
            IList<string> values,
            IDictionary<string, int> columns,
            string name,
            int rowNumber)
        {
            string text = OptionalText(values, columns, name);
            double value;
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) || !IsFinite(value))
                throw new InvalidOperationException("Invalid " + name + " value at CSV row " + rowNumber + ".");
            return value;
        }

        private static double? OptionalNumber(
            IList<string> values,
            IDictionary<string, int> columns,
            string name)
        {
            string text = OptionalText(values, columns, name);
            if (string.IsNullOrWhiteSpace(text)) return null;
            double value;
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) || !IsFinite(value))
                return null;
            return value;
        }

        private static string OptionalText(
            IList<string> values,
            IDictionary<string, int> columns,
            string name)
        {
            int index;
            if (!columns.TryGetValue(name, out index) || index < 0 || index >= values.Count)
                return string.Empty;
            return values[index].Trim();
        }

        private static string NormalizeHeading(string value)
        {
            return new string((value ?? string.Empty)
                .Where(character => char.IsLetterOrDigit(character))
                .Select(char.ToUpperInvariant)
                .ToArray());
        }

        private static string Csv(string value)
        {
            string text = value ?? string.Empty;
            return "\"" + text.Replace("\"", "\"\"") + "\"";
        }

        private static void JsonLine(StringBuilder text, string name, string value, bool comma)
        {
            text.Append("  \"").Append(JsonEscape(name)).Append("\": \"")
                .Append(JsonEscape(value ?? string.Empty)).Append("\"");
            if (comma) text.Append(',');
            text.AppendLine();
        }

        private static void JsonNumber(StringBuilder text, string name, double value, bool comma)
        {
            text.Append("  \"").Append(JsonEscape(name)).Append("\": ")
                .Append(value.ToString("R", CultureInfo.InvariantCulture));
            if (comma) text.Append(',');
            text.AppendLine();
        }

        private static string JsonEscape(string value)
        {
            return (value ?? string.Empty)
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n");
        }

        private static string EnsureExtension(string path, string extension)
        {
            return path.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ? path : path + extension;
        }

        private static void SafeDelete(string path)
        {
            try { if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) File.Delete(path); }
            catch { }
        }

        private static string OptionalInvariant(double? value)
        {
            return value.HasValue ? value.Value.ToString("R", CultureInfo.InvariantCulture) : string.Empty;
        }

        private static double? ParseOptional(string text)
        {
            double value;
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && IsFinite(value)
                ? (double?)value
                : null;
        }

        private static string Get(IDictionary<string, string> values, string key)
        {
            string value;
            return values.TryGetValue(key, out value) ? value : string.Empty;
        }

        private static string EmptyAs(string value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }

        private static string FormatOptional(double? value, string suffix)
        {
            return value.HasValue
                ? value.Value.ToString("0.###", CultureInfo.CurrentCulture) + (string.IsNullOrWhiteSpace(suffix) ? string.Empty : " " + suffix)
                : "<Not supplied>";
        }

        private static string FormatRange(double? minimum, double? maximum, string suffix)
        {
            if (!minimum.HasValue || !maximum.HasValue) return "<Not supplied>";
            return minimum.Value.ToString("0.###", CultureInfo.CurrentCulture) + " to " +
                maximum.Value.ToString("0.###", CultureInfo.CurrentCulture) + " " + suffix;
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static bool Equal(string left, string right)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        private static Document ActiveDocument()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
        }
    }

    internal sealed class ExchangeVertex
    {
        public ExchangeVertex(
            string featureId,
            int partIndex,
            int vertexIndex,
            string objectType,
            string layer,
            string handle,
            Point3d point,
            string role)
        {
            FeatureId = featureId;
            PartIndex = partIndex;
            VertexIndex = vertexIndex;
            ObjectType = objectType;
            Layer = layer;
            Handle = handle;
            Point = point;
            Role = role;
        }

        public string FeatureId { get; private set; }
        public int PartIndex { get; private set; }
        public int VertexIndex { get; private set; }
        public string ObjectType { get; private set; }
        public string Layer { get; private set; }
        public string Handle { get; private set; }
        public Point3d Point { get; private set; }
        public string Role { get; private set; }
    }

    internal sealed class ImportedResultRow
    {
        public ImportedResultRow(
            double x,
            double y,
            double? z,
            double? depth,
            double? velocity,
            double? waterLevel,
            string scenario,
            string time)
        {
            X = x;
            Y = y;
            Z = z;
            Depth = depth;
            Velocity = velocity;
            WaterLevel = waterLevel;
            Scenario = scenario ?? string.Empty;
            Time = time ?? string.Empty;
        }

        public double X { get; private set; }
        public double Y { get; private set; }
        public double? Z { get; private set; }
        public double? Depth { get; private set; }
        public double? Velocity { get; private set; }
        public double? WaterLevel { get; private set; }
        public string Scenario { get; private set; }
        public string Time { get; private set; }
    }

    internal sealed class ResultImportData
    {
        public ResultImportData(string sourcePath, List<ImportedResultRow> rows)
        {
            SourcePath = sourcePath;
            Rows = rows;
        }

        public string SourcePath { get; private set; }
        public List<ImportedResultRow> Rows { get; private set; }
    }

    internal sealed class ResultImportSummary
    {
        private readonly HashSet<string> _scenarios =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public int Created { get; set; }
        public int Scenarios { get { return _scenarios.Count; } }
        public double? MinimumDepth { get; private set; }
        public double? MaximumDepth { get; private set; }
        public double? MinimumVelocity { get; private set; }
        public double? MaximumVelocity { get; private set; }

        public void Include(ImportedResultRow row)
        {
            if (!string.IsNullOrWhiteSpace(row.Scenario)) _scenarios.Add(row.Scenario);
            IncludeRange(row.Depth, ref MinimumDepth, ref MaximumDepth);
            IncludeRange(row.Velocity, ref MinimumVelocity, ref MaximumVelocity);
        }

        private static void IncludeRange(double? value, ref double? minimum, ref double? maximum)
        {
            if (!value.HasValue) return;
            minimum = !minimum.HasValue || value.Value < minimum.Value ? value : minimum;
            maximum = !maximum.HasValue || value.Value > maximum.Value ? value : maximum;
        }
    }

    internal sealed class ImportedResultRecord
    {
        public ImportedResultRecord(
            string source,
            string scenario,
            string time,
            double x,
            double y,
            double z,
            double? depth,
            double? velocity,
            double? waterLevel,
            double? hazardIndex)
        {
            Source = source;
            Scenario = scenario;
            Time = time;
            X = x;
            Y = y;
            Z = z;
            Depth = depth;
            Velocity = velocity;
            WaterLevel = waterLevel;
            HazardIndex = hazardIndex;
        }

        public string Source { get; private set; }
        public string Scenario { get; private set; }
        public string Time { get; private set; }
        public double X { get; private set; }
        public double Y { get; private set; }
        public double Z { get; private set; }
        public double? Depth { get; private set; }
        public double? Velocity { get; private set; }
        public double? WaterLevel { get; private set; }
        public double? HazardIndex { get; private set; }
    }
}
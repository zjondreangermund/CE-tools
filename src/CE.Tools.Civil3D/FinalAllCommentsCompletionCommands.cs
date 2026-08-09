using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.CoordinateTransformationCommands))]
[assembly: CommandClass(typeof(CETools.Civil3D.PdfDrawingImportCommands))]
[assembly: CommandClass(typeof(CETools.Civil3D.PointCircleConversionCommands))]
[assembly: CommandClass(typeof(CETools.Civil3D.FinalSurfaceUtilityCommands))]
[assembly: CommandClass(typeof(CETools.Civil3D.GridSettingOutCommands))]
[assembly: CommandClass(typeof(CETools.Civil3D.FinalWorkflowUtilityCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// User-local popup defaults shared by all DWGs. Drawing-local popup values
    /// still override these defaults when the drawing already contains settings.
    /// </summary>
    internal static class CrossDrawingProductionSettingsStore
    {
        private const string Header = "CE_TOOLS_PRODUCTION_SETTINGS_V1";
        private static readonly object Sync = new object();

        internal static void Load(ProductionSettingsDialogModel model)
        {
            if (model == null) return;
            lock (Sync)
            {
                Dictionary<string, string> values = ReadAll();
                foreach (ProductionSettingsField field in model.Fields)
                {
                    string value;
                    if (!values.TryGetValue(Key(model.Title, field.Key), out value)) continue;
                    if (field.Kind == ProductionSettingsFieldKind.Choice &&
                        field.Choices.Count > 0 &&
                        !field.Choices.Any(item => string.Equals(item, value, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    field.Value = value;
                }
            }
        }

        internal static void Save(ProductionSettingsDialogModel model)
        {
            if (model == null) return;
            lock (Sync)
            {
                Dictionary<string, string> values = ReadAll();
                foreach (ProductionSettingsField field in model.Fields)
                    values[Key(model.Title, field.Key)] = field.Value ?? string.Empty;
                WriteAll(values);
            }
        }

        internal static void Clear()
        {
            lock (Sync)
            {
                try { if (File.Exists(StoragePath)) File.Delete(StoragePath); }
                catch { }
            }
        }

        private static string Key(string title, string key)
        {
            return (title ?? string.Empty).Trim() + "\u001f" + (key ?? string.Empty).Trim();
        }

        private static string StoragePath
        {
            get
            {
                string folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "CE Tools");
                Directory.CreateDirectory(folder);
                return Path.Combine(folder, "ProductionSettings.ceps");
            }
        }

        private static Dictionary<string, string> ReadAll()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(StoragePath)) return result;
            try
            {
                string[] lines = File.ReadAllLines(StoragePath, Encoding.UTF8);
                if (lines.Length == 0 || !string.Equals(lines[0], Header, StringComparison.Ordinal)) return result;
                foreach (string line in lines.Skip(1))
                {
                    string[] parts = line.Split(new[] { '|' }, 2);
                    if (parts.Length != 2) continue;
                    string key = Decode(parts[0]);
                    if (string.IsNullOrWhiteSpace(key)) continue;
                    result[key] = Decode(parts[1]);
                }
            }
            catch { result.Clear(); }
            return result;
        }

        private static void WriteAll(IDictionary<string, string> values)
        {
            var lines = new List<string> { Header };
            foreach (KeyValuePair<string, string> item in values.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
                lines.Add(Encode(item.Key) + "|" + Encode(item.Value));
            string temporary = StoragePath + ".tmp";
            File.WriteAllLines(temporary, lines, Encoding.UTF8);
            if (File.Exists(StoragePath)) File.Delete(StoragePath);
            File.Move(temporary, StoragePath);
        }

        private static string Encode(string value)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? string.Empty));
        }

        private static string Decode(string value)
        {
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(value)); }
            catch { return string.Empty; }
        }
    }

    internal static class NamibiaCoordinateSystemCatalog
    {
        private static readonly Dictionary<string, int> Zones = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            { "Windhoek", 17 }, { "Rehoboth", 17 }, { "Okahandja", 17 }, { "Gobabis", 19 },
            { "Swakopmund", 15 }, { "Walvis Bay", 15 }, { "Henties Bay", 15 }, { "Arandis", 15 },
            { "Usakos", 15 }, { "Karibib", 15 }, { "Omaruru", 15 }, { "Otjiwarongo", 17 },
            { "Outjo", 17 }, { "Khorixas", 15 }, { "Opuwo", 15 }, { "Oshakati", 17 },
            { "Ongwediva", 17 }, { "Ondangwa", 17 }, { "Eenhana", 17 }, { "Outapi", 17 },
            { "Oshikango", 17 }, { "Tsumeb", 17 }, { "Grootfontein", 19 }, { "Rundu", 19 },
            { "Katima Mulilo", 21 }, { "Keetmanshoop", 19 }, { "Mariental", 19 }, { "Luderitz", 15 },
            { "Lüderitz", 15 }, { "Karasburg", 19 }, { "Rosh Pinah", 17 }, { "Oranjemund", 17 },
            { "Aroab", 19 }, { "Aussenkehr", 17 }, { "Berseba", 19 }, { "Gibeon", 19 },
            { "Gochas", 19 }, { "Grunau", 19 }, { "Grünau", 19 }, { "Helmeringhausen", 17 },
            { "Koes", 19 }, { "Koës", 19 }, { "Tses", 19 }, { "Aminuis", 21 },
            { "Witvlei", 19 }, { "Okakarara", 19 }, { "Tsumkwe", 21 }, { "Sesfontein", 15 }
        };

        internal static string PreferredLoName(string town)
        {
            int zone;
            if (string.IsNullOrWhiteSpace(town) || !Zones.TryGetValue(town.Trim(), out zone)) return string.Empty;
            return "LO" + zone.ToString("00", CultureInfo.InvariantCulture);
        }
    }

    internal static class GeoCoordinateTransform
    {
        internal static bool TryDrawingToWgs84(Database database, Point3d drawingPoint, out Point3d geographic, out string error)
        {
            geographic = Point3d.Origin;
            error = string.Empty;
            if (database == null) { error = "No active drawing database."; return false; }
            ObjectId geoId;
            try { geoId = database.GeoDataObject; }
            catch (System.Exception ex) { error = ex.Message; return false; }
            if (geoId.IsNull || geoId.IsErased)
            {
                error = "The drawing has no AutoCAD geographic transformation. Assign the project coordinate system/geographic location first.";
                return false;
            }
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                GeoLocationData data = transaction.GetObject(geoId, OpenMode.ForRead, false) as GeoLocationData;
                if (data == null) { error = "The drawing GeoData object is unavailable."; return false; }
                try
                {
                    geographic = data.TransformToLonLatAlt(drawingPoint);
                    return true;
                }
                catch (System.Exception ex) { error = ex.Message; return false; }
            }
        }

        internal static bool TryWgs84ToDrawing(Database database, double latitude, double longitude, double elevation, out Point3d drawingPoint, out string error)
        {
            drawingPoint = Point3d.Origin;
            error = string.Empty;
            if (database == null) { error = "No active drawing database."; return false; }
            ObjectId geoId;
            try { geoId = database.GeoDataObject; }
            catch (System.Exception ex) { error = ex.Message; return false; }
            if (geoId.IsNull || geoId.IsErased)
            {
                error = "The drawing has no AutoCAD geographic transformation. Assign the project coordinate system/geographic location first.";
                return false;
            }
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                GeoLocationData data = transaction.GetObject(geoId, OpenMode.ForRead, false) as GeoLocationData;
                if (data == null) { error = "The drawing GeoData object is unavailable."; return false; }
                try
                {
                    drawingPoint = data.TransformFromLonLatAlt(new Point3d(longitude, latitude, elevation));
                    return true;
                }
                catch (System.Exception ex) { error = ex.Message; return false; }
            }
        }
    }

    public sealed class CoordinateTransformationCommands
    {
        [CommandMethod("CE_TOOLS", "CE_COORDTRANSFORM", CommandFlags.Modal | CommandFlags.Redraw)]
        public void TransformOne()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Coordinate Transformation",
                "Convert real drawing X/Y coordinates to WGS84 longitude/latitude and vice versa using the AutoCAD GeoLocationData transformation stored in this DWG.");
            model.AddChoice("Action", "01 Conversion", "Conversion", "Drawing X/Y -> WGS84 Lat/Long",
                "The assigned drawing geographic transformation is used; geometry is not moved.",
                new[] { "Drawing X/Y -> WGS84 Lat/Long", "WGS84 Lat/Long -> Drawing X/Y" });
            model.AddText("X", "02 Drawing", "Drawing X / Easting", "0", "Drawing-space X coordinate.");
            model.AddText("Y", "02 Drawing", "Drawing Y / Northing", "0", "Drawing-space Y coordinate.");
            model.AddText("Z", "02 Drawing", "Drawing Z", "0", "Drawing-space elevation.");
            model.AddText("Lat", "03 WGS84", "Latitude", "-22.5609", "WGS84 decimal degrees; south is negative.");
            model.AddText("Lon", "03 WGS84", "Longitude", "17.0658", "WGS84 decimal degrees; east is positive.");
            model.AddText("Alt", "03 WGS84", "Elevation", "0", "WGS84 elevation/altitude value.");
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            string action = model.Text("Action");
            double x, y, z, lat, lon, alt;
            if (!TryNumber(model.Text("X"), out x) || !TryNumber(model.Text("Y"), out y) || !TryNumber(model.Text("Z"), out z) ||
                !TryNumber(model.Text("Lat"), out lat) || !TryNumber(model.Text("Lon"), out lon) || !TryNumber(model.Text("Alt"), out alt))
            {
                document.Editor.WriteMessage("\nCE_COORDTRANSFORM: enter valid numeric coordinate values.");
                return;
            }
            string error;
            if (action.StartsWith("Drawing", StringComparison.OrdinalIgnoreCase))
            {
                Point3d geo;
                if (!GeoCoordinateTransform.TryDrawingToWgs84(document.Database, new Point3d(x, y, z), out geo, out error))
                {
                    document.Editor.WriteMessage("\nCE_COORDTRANSFORM stopped. {0}", error); return;
                }
                PopupTablePresenter.ShowReview(
                    "CE Tools - Coordinate Result",
                    "Drawing coordinate converted through this DWG's geographic transformation.",
                    new List<KeyValuePair<string, string>>
                    {
                        new KeyValuePair<string, string>("Drawing X", x.ToString("N3", CultureInfo.CurrentCulture)),
                        new KeyValuePair<string, string>("Drawing Y", y.ToString("N3", CultureInfo.CurrentCulture)),
                        new KeyValuePair<string, string>("Latitude", geo.Y.ToString("0.00000000", CultureInfo.InvariantCulture)),
                        new KeyValuePair<string, string>("Longitude", geo.X.ToString("0.00000000", CultureInfo.InvariantCulture))
                    }, "Close");
            }
            else
            {
                Point3d dwg;
                if (!GeoCoordinateTransform.TryWgs84ToDrawing(document.Database, lat, lon, alt, out dwg, out error))
                {
                    document.Editor.WriteMessage("\nCE_COORDTRANSFORM stopped. {0}", error); return;
                }
                PopupTablePresenter.ShowReview(
                    "CE Tools - Coordinate Result",
                    "WGS84 coordinate converted through this DWG's geographic transformation.",
                    new List<KeyValuePair<string, string>>
                    {
                        new KeyValuePair<string, string>("Latitude", lat.ToString("0.00000000", CultureInfo.InvariantCulture)),
                        new KeyValuePair<string, string>("Longitude", lon.ToString("0.00000000", CultureInfo.InvariantCulture)),
                        new KeyValuePair<string, string>("Drawing X", dwg.X.ToString("N3", CultureInfo.CurrentCulture)),
                        new KeyValuePair<string, string>("Drawing Y", dwg.Y.ToString("N3", CultureInfo.CurrentCulture))
                    }, "Close");
            }
        }

        [CommandMethod("CE_TOOLS", "CE_COORDTRANSFORMBULK", CommandFlags.Modal | CommandFlags.Redraw)]
        public void TransformBulk()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            PromptOpenFileOptions open = new PromptOpenFileOptions("\nSelect CSV/TXT/XLSX coordinate list: ")
            {
                Filter = "Coordinate files (*.csv;*.txt;*.xlsx)|*.csv;*.txt;*.xlsx|All files (*.*)|*.*"
            };
            PromptFileNameResult input = document.Editor.GetFileNameForOpen(open);
            if (input.Status != PromptStatus.OK) return;
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Bulk Coordinate Conversion",
                "Convert an Excel/CSV survey list using the geographic transformation assigned to the active DWG. Existing drawing geometry is unchanged.");
            model.AddChoice("Action", "Conversion", "Conversion", "Drawing X/Y -> WGS84 Lat/Long",
                "Header names X/Easting and Y/Northing, or Latitude/Longitude, are detected automatically.",
                new[] { "Drawing X/Y -> WGS84 Lat/Long", "WGS84 Lat/Long -> Drawing X/Y" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;
            PromptSaveFileOptions save = new PromptSaveFileOptions("\nSelect output Excel workbook: ")
            {
                Filter = "Excel Workbook (*.xlsx)|*.xlsx",
                InitialFileName = Path.GetFileNameWithoutExtension(input.StringResult) + "-converted.xlsx"
            };
            PromptFileNameResult output = document.Editor.GetFileNameForSave(save);
            if (output.Status != PromptStatus.OK) return;
            string outputPath = output.StringResult.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)
                ? output.StringResult : output.StringResult + ".xlsx";
            try
            {
                List<IList<string>> rows = CoordinateWorkbookReader.Read(input.StringResult);
                if (rows.Count < 2) throw new InvalidOperationException("The coordinate file contains no data rows.");
                List<IList<string>> converted = ConvertRows(document.Database, rows, model.Text("Action"));
                SimpleXlsxWriter.Write(outputPath, "Converted Coordinates", converted);
                document.Editor.WriteMessage("\nCE_COORDTRANSFORMBULK complete. Rows={0}; output={1}", Math.Max(0, converted.Count - 1), outputPath);
            }
            catch (System.Exception ex)
            {
                document.Editor.WriteMessage("\nCE_COORDTRANSFORMBULK failed. {0}", ex.Message);
            }
        }

        private static List<IList<string>> ConvertRows(Database database, List<IList<string>> input, string action)
        {
            List<string> headers = input[0].Select(value => (value ?? string.Empty).Trim()).ToList();
            bool drawingToGeo = action.StartsWith("Drawing", StringComparison.OrdinalIgnoreCase);
            int first = FindHeader(headers, drawingToGeo ? new[] { "X", "EASTING", "E", "DRAWING X" } : new[] { "LATITUDE", "LAT" });
            int second = FindHeader(headers, drawingToGeo ? new[] { "Y", "NORTHING", "N", "DRAWING Y" } : new[] { "LONGITUDE", "LON", "LONG" });
            if (first < 0 || second < 0)
            {
                first = 0; second = 1;
            }
            var output = new List<IList<string>>();
            var outHeader = headers.ToList();
            if (drawingToGeo) { outHeader.Add("Latitude"); outHeader.Add("Longitude"); }
            else { outHeader.Add("Drawing X / Easting"); outHeader.Add("Drawing Y / Northing"); }
            output.Add(outHeader);
            for (int rowIndex = 1; rowIndex < input.Count; rowIndex++)
            {
                IList<string> source = input[rowIndex];
                var row = source.ToList();
                while (row.Count <= Math.Max(first, second)) row.Add(string.Empty);
                double a, b;
                if (!TryNumber(row[first], out a) || !TryNumber(row[second], out b))
                {
                    row.Add("INVALID"); row.Add("INVALID"); output.Add(row); continue;
                }
                string error;
                if (drawingToGeo)
                {
                    Point3d geo;
                    if (GeoCoordinateTransform.TryDrawingToWgs84(database, new Point3d(a, b, 0), out geo, out error))
                    {
                        row.Add(geo.Y.ToString("0.00000000", CultureInfo.InvariantCulture));
                        row.Add(geo.X.ToString("0.00000000", CultureInfo.InvariantCulture));
                    }
                    else { row.Add("ERROR: " + error); row.Add(string.Empty); }
                }
                else
                {
                    Point3d dwg;
                    if (GeoCoordinateTransform.TryWgs84ToDrawing(database, a, b, 0, out dwg, out error))
                    {
                        row.Add(dwg.X.ToString("0.###", CultureInfo.InvariantCulture));
                        row.Add(dwg.Y.ToString("0.###", CultureInfo.InvariantCulture));
                    }
                    else { row.Add("ERROR: " + error); row.Add(string.Empty); }
                }
                output.Add(row);
            }
            return output;
        }

        private static int FindHeader(IList<string> headers, IEnumerable<string> candidates)
        {
            for (int index = 0; index < headers.Count; index++)
                foreach (string candidate in candidates)
                    if (string.Equals(headers[index], candidate, StringComparison.OrdinalIgnoreCase)) return index;
            return -1;
        }

        internal static bool TryNumber(string value, out double number)
        {
            return double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out number) ||
                   double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out number);
        }
    }

    internal static class CoordinateWorkbookReader
    {
        internal static List<IList<string>> Read(string path)
        {
            string extension = Path.GetExtension(path).ToLowerInvariant();
            return extension == ".xlsx" ? ReadXlsx(path) : ReadDelimited(path);
        }

        private static List<IList<string>> ReadDelimited(string path)
        {
            var result = new List<IList<string>>();
            foreach (string line in File.ReadAllLines(path))
            {
                char separator = line.Contains("\t") ? '\t' : (line.Count(ch => ch == ';') > line.Count(ch => ch == ',') ? ';' : ',');
                result.Add(ParseDelimited(line, separator));
            }
            return result;
        }

        private static IList<string> ParseDelimited(string line, char separator)
        {
            var result = new List<string>();
            var current = new StringBuilder();
            bool quoted = false;
            for (int i = 0; i < (line ?? string.Empty).Length; i++)
            {
                char c = line[i];
                if (c == '"')
                {
                    if (quoted && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                    else quoted = !quoted;
                }
                else if (c == separator && !quoted) { result.Add(current.ToString()); current.Clear(); }
                else current.Append(c);
            }
            result.Add(current.ToString());
            return result;
        }

        private static List<IList<string>> ReadXlsx(string path)
        {
            var result = new List<IList<string>>();
            using (ZipArchive archive = ZipFile.OpenRead(path))
            {
                List<string> shared = new List<string>();
                ZipArchiveEntry sharedEntry = archive.GetEntry("xl/sharedStrings.xml");
                if (sharedEntry != null)
                {
                    using (Stream stream = sharedEntry.Open())
                    {
                        XDocument doc = XDocument.Load(stream);
                        XNamespace ns = doc.Root.Name.Namespace;
                        shared = doc.Descendants(ns + "si").Select(si => string.Concat(si.Descendants(ns + "t").Select(t => t.Value))).ToList();
                    }
                }
                ZipArchiveEntry sheet = archive.GetEntry("xl/worksheets/sheet1.xml");
                if (sheet == null) throw new InvalidOperationException("The workbook has no first worksheet.");
                using (Stream stream = sheet.Open())
                {
                    XDocument doc = XDocument.Load(stream);
                    XNamespace ns = doc.Root.Name.Namespace;
                    foreach (XElement row in doc.Descendants(ns + "row"))
                    {
                        var values = new SortedDictionary<int, string>();
                        foreach (XElement cell in row.Elements(ns + "c"))
                        {
                            string reference = (string)cell.Attribute("r") ?? "A1";
                            int column = ColumnIndex(reference);
                            string type = (string)cell.Attribute("t") ?? string.Empty;
                            string value = string.Empty;
                            if (type == "inlineStr") value = string.Concat(cell.Descendants(ns + "t").Select(t => t.Value));
                            else
                            {
                                XElement v = cell.Element(ns + "v");
                                value = v == null ? string.Empty : v.Value;
                                int sharedIndex;
                                if (type == "s" && int.TryParse(value, out sharedIndex) && sharedIndex >= 0 && sharedIndex < shared.Count)
                                    value = shared[sharedIndex];
                            }
                            values[column] = value;
                        }
                        if (values.Count == 0) continue;
                        int maximum = values.Keys.Max();
                        var line = new List<string>();
                        for (int col = 0; col <= maximum; col++)
                        {
                            string value;
                            line.Add(values.TryGetValue(col, out value) ? value : string.Empty);
                        }
                        result.Add(line);
                    }
                }
            }
            return result;
        }

        private static int ColumnIndex(string reference)
        {
            int value = 0;
            foreach (char c in reference.ToUpperInvariant())
            {
                if (c < 'A' || c > 'Z') break;
                value = (value * 26) + (c - 'A' + 1);
            }
            return Math.Max(0, value - 1);
        }
    }

    public sealed class PdfDrawingImportCommands
    {
        [CommandMethod("CE_TOOLS", "CE_PDFTODWG", CommandFlags.Session)]
        public void PdfToDwg()
        {
            Document source = AcApplication.DocumentManager.MdiActiveDocument;
            if (source == null) return;
            PromptOpenFileOptions open = new PromptOpenFileOptions("\nSelect PDF to convert into a DWG: ") { Filter = "PDF (*.pdf)|*.pdf" };
            PromptFileNameResult pdf = source.Editor.GetFileNameForOpen(open);
            if (pdf.Status != PromptStatus.OK) return;
            var settings = new ProductionSettingsDialogModel(
                "CE Tools - PDF to DWG",
                "Create a new DWG and use AutoCAD's native -PDFIMPORT conversion. Vector PDF geometry, TrueType text and supported fills are converted; raster-only content remains subject to AutoCAD PDFIMPORT limitations.");
            settings.AddPositiveInteger("Page", "Import", "PDF page", 1, "One PDF page is imported per created DWG.");
            settings.AddText("X", "Import", "Insertion X", "0", "DWG insertion X.");
            settings.AddText("Y", "Import", "Insertion Y", "0", "DWG insertion Y.");
            settings.AddPositiveDouble("Scale", "Import", "Scale factor", 1.0, "PDFIMPORT scale factor.");
            settings.AddText("Rotation", "Import", "Rotation degrees", "0", "PDF import rotation.");
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;
            PromptSaveFileOptions save = new PromptSaveFileOptions("\nSave converted DWG as: ")
            {
                Filter = "AutoCAD Drawing (*.dwg)|*.dwg",
                InitialFileName = Path.GetFileNameWithoutExtension(pdf.StringResult) + "-P" + settings.Integer("Page", 1).ToString(CultureInfo.InvariantCulture) + ".dwg"
            };
            PromptFileNameResult dwg = source.Editor.GetFileNameForSave(save);
            if (dwg.Status != PromptStatus.OK) return;
            string output = dwg.StringResult.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase) ? dwg.StringResult : dwg.StringResult + ".dwg";
            double x, y, rotation;
            if (!CoordinateTransformationCommands.TryNumber(settings.Text("X"), out x) ||
                !CoordinateTransformationCommands.TryNumber(settings.Text("Y"), out y) ||
                !CoordinateTransformationCommands.TryNumber(settings.Text("Rotation"), out rotation))
            {
                source.Editor.WriteMessage("\nCE_PDFTODWG: invalid insertion/rotation value."); return;
            }
            string pdfPath = pdf.StringResult.Replace("\"", "\"\"");
            string dwgPath = output.Replace("\"", "\"\"");
            DocumentCollection documents = AcApplication.DocumentManager;
            Document target;
            try { target = documents.Add("acad.dwt"); }
            catch { target = documents.Add(string.Empty); }
            documents.MdiActiveDocument = target;
            string script =
                "_.FILEDIA 0 " +
                "_.-PDFIMPORT _F \"" + pdfPath + "\" " + settings.Integer("Page", 1).ToString(CultureInfo.InvariantCulture) + " " +
                x.ToString(CultureInfo.InvariantCulture) + "," + y.ToString(CultureInfo.InvariantCulture) + " " +
                settings.Double("Scale", 1.0).ToString(CultureInfo.InvariantCulture) + " " +
                rotation.ToString(CultureInfo.InvariantCulture) + " " +
                "_.SAVEAS \"" + dwgPath + "\" " +
                "_.FILEDIA 1 ";
            target.SendStringToExecute(script, true, false, true);
            target.Editor.WriteMessage("\nCE_PDFTODWG queued AutoCAD native PDFIMPORT and SAVEAS for page {0}. Output: {1}", settings.Integer("Page", 1), output);
        }
    }

    public sealed class PointCircleConversionCommands
    {
        [CommandMethod("CE_TOOLS", "CE_POINTCIRCLE", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void Convert()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civil = CivilApplication.ActiveDocument;
            if (document == null || civil == null) return;
            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Point / Circle Conversion",
                "Convert AutoCAD/Civil points to circles or circle centres to COGO points. Source deletion is optional.");
            settings.AddChoice("Mode", "Conversion", "Mode", "Points -> circles", "Choose conversion direction.", new[] { "Points -> circles", "Circles -> COGO points" });
            settings.AddPositiveDouble("Radius", "Conversion", "Circle radius", 0.5, "Radius used for generated circles.");
            settings.AddChoice("Erase", "Conversion", "Source objects", "Keep originals", "Keep or erase selected source objects after conversion.", new[] { "Keep originals", "Erase originals" });
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;
            PromptSelectionResult selection = document.Editor.GetSelection(new PromptSelectionOptions { MessageForAdding = "\nSelect source points/circles: ", AllowDuplicates = false });
            if (selection.Status != PromptStatus.OK) return;
            int created = 0;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = transaction.GetObject(document.Database.CurrentSpaceId, OpenMode.ForWrite, false) as BlockTableRecord;
                foreach (ObjectId id in selection.Value.GetObjectIds())
                {
                    Entity entity;
                    try { entity = transaction.GetObject(id, OpenMode.ForWrite, false) as Entity; }
                    catch { continue; }
                    if (entity == null) continue;
                    if (settings.Text("Mode").StartsWith("Points", StringComparison.OrdinalIgnoreCase))
                    {
                        Point3d point;
                        DBPoint dbPoint = entity as DBPoint;
                        CogoPoint cogo = entity as CogoPoint;
                        if (dbPoint != null) point = dbPoint.Position;
                        else if (cogo != null) point = new Point3d(cogo.Easting, cogo.Northing, cogo.Elevation);
                        else continue;
                        var circle = new Circle(point, Vector3d.ZAxis, settings.Double("Radius", 0.5));
                        circle.SetDatabaseDefaults(document.Database);
                        circle.LayerId = entity.LayerId;
                        space.AppendEntity(circle);
                        transaction.AddNewlyCreatedDBObject(circle, true);
                        created++;
                    }
                    else
                    {
                        Circle circle = entity as Circle;
                        if (circle == null) continue;
                        ObjectId cogoId = civil.CogoPoints.Add(circle.Center, "CE", true);
                        CogoPoint point = transaction.GetObject(cogoId, OpenMode.ForWrite, false) as CogoPoint;
                        if (point != null) CogoPointProjectStyleCommands.ApplyPointStyles(document.Database, civil, transaction, point);
                        created++;
                    }
                    if (string.Equals(settings.Text("Erase"), "Erase originals", StringComparison.OrdinalIgnoreCase)) entity.Erase();
                }
                transaction.Commit();
            }
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_POINTCIRCLE complete. Created={0}.", created);
        }
    }

    public sealed class FinalSurfaceUtilityCommands
    {
        [CommandMethod("CE_TOOLS", "CE_SURFACEDUPLICATE", CommandFlags.Modal | CommandFlags.Redraw)]
        public void DuplicateSurface()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civil = CivilApplication.ActiveDocument;
            if (document == null || civil == null) return;
            PromptEntityOptions options = new PromptEntityOptions("\nSelect Civil 3D TIN surface to duplicate: ");
            options.SetRejectMessage("\nSelect a Civil 3D TIN surface.");
            options.AddAllowedClass(typeof(TinSurface), true);
            PromptEntityResult selected = document.Editor.GetEntity(options);
            if (selected.Status != PromptStatus.OK) return;
            string sourceName;
            var points = new Point3dCollection();
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                TinSurface source = transaction.GetObject(selected.ObjectId, OpenMode.ForRead, false) as TinSurface;
                if (source == null) return;
                sourceName = source.Name;
                foreach (TinSurfaceVertex vertex in source.Vertices)
                    points.Add(vertex.Location);
            }
            var settings = new ProductionSettingsDialogModel("CE Tools - Duplicate Surface", "Create an independent TIN copy from the selected surface vertices. The original is not edited.");
            settings.AddText("Name", "Output", "New surface name", sourceName + " - COPY", "Unique Civil 3D surface name.");
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;
            try
            {
                ObjectId outputId = TinSurface.Create(document.Database, settings.Text("Name"));
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    TinSurface output = transaction.GetObject(outputId, OpenMode.ForWrite, false) as TinSurface;
                    if (output == null) throw new InvalidOperationException("Civil 3D did not create the TIN surface.");
                    output.AddVertices(points);
                    output.Rebuild();
                    transaction.Commit();
                }
                document.Editor.WriteMessage("\nCE_SURFACEDUPLICATE complete. Created '{0}' with {1} vertices.", settings.Text("Name"), points.Count);
            }
            catch (System.Exception ex) { document.Editor.WriteMessage("\nCE_SURFACEDUPLICATE failed. {0}", ex.Message); }
        }

        [CommandMethod("CE_TOOLS", "CE_ANNOTATIONSCALESYNC", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void SyncAnnotationScales()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            var settings = new ProductionSettingsDialogModel("CE Tools - Annotation Scale Sync", "Synchronize annotative CE labels/tables to the current drawing annotation scale without changing model geometry.");
            settings.AddChoice("Scope", "Sync", "Objects", "All CE model-space annotations", "Choose all CE annotations or a manual selection.", new[] { "All CE model-space annotations", "Select objects" });
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;
            List<ObjectId> ids = new List<ObjectId>();
            if (settings.Text("Scope").StartsWith("Select", StringComparison.OrdinalIgnoreCase))
            {
                PromptSelectionResult selection = document.Editor.GetSelection();
                if (selection.Status != PromptStatus.OK) return;
                ids.AddRange(selection.Value.GetObjectIds());
            }
            else
            {
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    BlockTableRecord model = transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(document.Database), OpenMode.ForRead, false) as BlockTableRecord;
                    if (model != null) ids.AddRange(model.Cast<ObjectId>());
                }
            }
            int changed = 0;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids)
                {
                    Entity entity;
                    try { entity = transaction.GetObject(id, OpenMode.ForWrite, false) as Entity; }
                    catch { continue; }
                    if (!(entity is MText) && !(entity is MLeader) && !(entity is Dimension) && !(entity is Table) && !(entity is DBText)) continue;
                    try { PaperAnnotationScale.SetAnnotative(entity); changed++; } catch { }
                }
                transaction.Commit();
            }
            try { CeTablePresentationManager.CenterCeTables(document); } catch { }
            try { RuntimeAnnotationLinkManager.ClampLinkedAnnotations(document, true); } catch { }
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_ANNOTATIONSCALESYNC complete. Objects synchronized={0}.", changed);
        }
    }

    public sealed class GridSettingOutCommands
    {
        [CommandMethod("CE_TOOLS", "CE_GRIDSETTINGOUT", CommandFlags.Modal | CommandFlags.Redraw)]
        public void CreateGrid()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civil = CivilApplication.ActiveDocument;
            if (document == null || civil == null) return;
            PromptPointResult first = document.Editor.GetPoint("\nPick first grid corner: ");
            if (first.Status != PromptStatus.OK) return;
            PromptCornerOptions corner = new PromptCornerOptions("\nPick opposite grid corner: ", first.Value);
            PromptPointResult second = document.Editor.GetCorner(corner);
            if (second.Status != PromptStatus.OK) return;
            var settings = new ProductionSettingsDialogModel("CE Tools - Grid Setting-Out", "Create non-duplicated perimeter or full-grid COGO setting-out points between two picked corners.");
            settings.AddPositiveDouble("DX", "Grid", "X spacing", 10.0, "Grid spacing in drawing units.");
            settings.AddPositiveDouble("DY", "Grid", "Y spacing", 10.0, "Grid spacing in drawing units.");
            settings.AddText("Prefix", "Grid", "Point prefix", "G", "Generated point name prefix.");
            settings.AddChoice("Mode", "Grid", "Point layout", "Perimeter", "Perimeter avoids duplicate closed-polyline start/end points; full grid fills all rows and columns.", new[] { "Perimeter", "Full grid" });
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;
            Point3d a = first.Value.TransformBy(document.Editor.CurrentUserCoordinateSystem);
            Point3d b = second.Value.TransformBy(document.Editor.CurrentUserCoordinateSystem);
            double minX = Math.Min(a.X, b.X), maxX = Math.Max(a.X, b.X), minY = Math.Min(a.Y, b.Y), maxY = Math.Max(a.Y, b.Y);
            double dx = settings.Double("DX", 10), dy = settings.Double("DY", 10);
            var points = new List<Point3d>();
            int nx = Math.Max(1, (int)Math.Round((maxX - minX) / dx));
            int ny = Math.Max(1, (int)Math.Round((maxY - minY) / dy));
            for (int iy = 0; iy <= ny; iy++)
                for (int ix = 0; ix <= nx; ix++)
                {
                    bool perimeter = ix == 0 || iy == 0 || ix == nx || iy == ny;
                    if (!perimeter && string.Equals(settings.Text("Mode"), "Perimeter", StringComparison.OrdinalIgnoreCase)) continue;
                    points.Add(new Point3d(minX + ((maxX - minX) * ix / nx), minY + ((maxY - minY) * iy / ny), a.Z));
                }
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                int number = 1;
                foreach (Point3d pointLocation in points.Distinct(new PlanPointComparer()))
                {
                    string name = settings.Text("Prefix") + number.ToString(CultureInfo.InvariantCulture);
                    ObjectId id = civil.CogoPoints.Add(pointLocation, name, true);
                    CogoPoint point = transaction.GetObject(id, OpenMode.ForWrite, false) as CogoPoint;
                    if (point != null)
                    {
                        try { point.PointName = name; } catch { }
                        point.RawDescription = name;
                        CogoPointProjectStyleCommands.ApplyPointStyles(document.Database, civil, transaction, point);
                    }
                    number++;
                }
                transaction.Commit();
            }
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_GRIDSETTINGOUT complete. Unique COGO points={0}.", points.Distinct(new PlanPointComparer()).Count());
        }

        private sealed class PlanPointComparer : IEqualityComparer<Point3d>
        {
            public bool Equals(Point3d a, Point3d b) { return Math.Abs(a.X - b.X) < 1e-7 && Math.Abs(a.Y - b.Y) < 1e-7; }
            public int GetHashCode(Point3d point) { return Math.Round(point.X, 6).GetHashCode() ^ Math.Round(point.Y, 6).GetHashCode(); }
        }
    }

    public sealed class FinalWorkflowUtilityCommands
    {
        [CommandMethod("CE_TOOLS", "CE_BOOKTOOLS", CommandFlags.Modal)]
        public void Books()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            DisciplineWorkflowDialogs.SelectAndRun(document, "CE Tools - Drawing and Client Books", "Drawing-book and client-book production is available from every discipline workflow through this shared hub.",
                new List<DisciplineWorkflowAction>
                {
                    new DisciplineWorkflowAction("Drawing Book", "CE_DRAWINGBOOK", "Create or refresh CE drawing-book layouts.", "01 Drawing Books"),
                    new DisciplineWorkflowAction("Drawing Book Index", "CE_BOOKINDEX", "Export the drawing-book register.", "01 Drawing Books"),
                    new DisciplineWorkflowAction("Client Book", "CE_CLIENTBOOK", "Create A4/A3 client books.", "02 Client Books"),
                    new DisciplineWorkflowAction("Refresh Client Books", "CE_CLIENTBOOKREFRESH", "Refresh linked client-book pages.", "02 Client Books"),
                    new DisciplineWorkflowAction("Client Book Index", "CE_CLIENTBOOKINDEX", "Export client-book register.", "02 Client Books")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_MOSTUSEDOVERALL", CommandFlags.Modal)]
        public void MostUsedOverall()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            IList<ProjectUsageSummary> projects = CommandUsageTracker.Projects(1000);
            var commands = FloatingToolsCommands.ReadDeclaredCommands()
                .Select(item => item.Command.Trim())
                .Where(item => item.StartsWith("CE_", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var rows = new List<IList<string>>();
            foreach (string command in commands)
            {
                long clicks = 0;
                double seconds = 0;
                foreach (ProjectUsageSummary project in projects)
                {
                    CommandUsageRecord value = CommandUsageTracker.Read(project.Key, command);
                    clicks += value.Clicks;
                    seconds += value.TotalSeconds;
                }
                if (clicks <= 0) continue;
                rows.Add(new List<string> { command, clicks.ToString("N0", CultureInfo.CurrentCulture), TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss") });
            }
            rows = rows.OrderByDescending(row => long.Parse(row[1], NumberStyles.AllowThousands, CultureInfo.CurrentCulture)).Take(50).ToList();
            GridReportPresenter.ShowReportAndOfferTable(document, "CE Tools - Overall Most Used Commands", "Usage aggregated across all saved DWGs in this CE Tools user profile.",
                new List<string> { "Command", "Executions", "Command Time" }, rows, "CE TOOLS OVERALL MOST USED");
        }

        [CommandMethod("CE_TOOLS", "CE_SETTINGDEFAULTSCLEAR", CommandFlags.Modal)]
        public void ClearSettingsDefaults()
        {
            if (!DisciplineWorkflowDialogs.Confirm("CE Tools - Clear Cross-Drawing Settings", "Clear the saved CE Tools popup defaults used by new/other drawings? Drawing-local settings will remain untouched.")) return;
            CrossDrawingProductionSettingsStore.Clear();
        }
    }
}
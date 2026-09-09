using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using System.Xml;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.Settings;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.SurveyGoogleEarthCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Survey/geospatial utilities built on AutoCAD's GeoLocationData transform.
    /// This intentionally does not scrape Google imagery. Google Earth display is
    /// via KML, and imagery import consumes a KML/KMZ GroundOverlay supplied by
    /// the user so image rights/source remain explicit.
    /// </summary>
    public sealed class SurveyGoogleEarthCommands
    {
        [CommandMethod("CE_TOOLS", "CE_SURVEYLOCATION", CommandFlags.Modal | CommandFlags.Redraw)]
        public void ShowSurveyLocation()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (document == null || civilDocument == null) return;

            ObjectId boundaryId = SelectClosedPolyline(document.Editor, "\nSelect the closed survey boundary polyline: ");
            if (boundaryId.IsNull) return;

            List<Point3d> boundary;
            Point3d centroid;
            if (!ReadBoundary(document.Database, boundaryId, out boundary, out centroid))
            {
                document.Editor.WriteMessage("\nCE_SURVEYLOCATION: a valid closed lightweight polyline is required.");
                return;
            }

            string[] coordinateCodes = ReadCoordinateCodes();
            string currentCode = ReadDrawingCoordinateSystem(civilDocument);
            SurveyLocationInfo info = BuildSurveyInfo(document.Database, currentCode, centroid, boundary);

            while (true)
            {
                using (var form = new SurveyLocationForm(info, coordinateCodes))
                {
                    AcApplication.ShowModalDialog(form);
                    if (form.Action == SurveyDialogAction.Close || form.Action == SurveyDialogAction.None)
                        return;

                    if (form.Action == SurveyDialogAction.ApplyCoordinateSystem)
                    {
                        string requested = (form.SelectedCoordinateSystem ?? string.Empty).Trim();
                        if (string.IsNullOrWhiteSpace(requested)) continue;
                        string message;
                        if (!ApplyCoordinateSystem(document, civilDocument, requested, out message))
                        {
                            MessageBox.Show(message, "CE Tools - Survey Location", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            continue;
                        }
                        currentCode = requested;
                        info = BuildSurveyInfo(document.Database, currentCode, centroid, boundary);
                        continue;
                    }

                    if (!info.HasGeographicTransform)
                    {
                        MessageBox.Show(
                            "This drawing does not currently contain usable AutoCAD geolocation transformation data. Assign/confirm a Civil 3D coordinate system (and GEOGRAPHICLOCATION when required) before opening/exporting Google Earth data.",
                            "CE Tools - Survey Location",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Warning);
                        continue;
                    }

                    if (form.Action == SurveyDialogAction.OpenGoogleEarth)
                    {
                        string kml = WriteTemporaryBoundaryKml(info);
                        OpenGoogleEarth(kml, info.CentreWgs84);
                        return;
                    }

                    if (form.Action == SurveyDialogAction.ExportKml)
                    {
                        using (var dialog = new SaveFileDialog
                        {
                            Filter = "Google Earth KML (*.kml)|*.kml",
                            DefaultExt = "kml",
                            AddExtension = true,
                            FileName = "CE_Survey_Boundary.kml",
                            Title = "Export Survey Boundary to Google Earth"
                        })
                        {
                            if (dialog.ShowDialog() == DialogResult.OK)
                                File.WriteAllText(dialog.FileName, BuildBoundaryKml(info), new UTF8Encoding(false));
                        }
                        return;
                    }
                }
            }
        }

        [CommandMethod("CE_TOOLS", "CE_GOOGLEEARTHIMAGE", CommandFlags.Modal | CommandFlags.Redraw)]
        public void ImportGoogleEarthImage()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (document == null || civilDocument == null) return;
            Editor editor = document.Editor;

            ObjectId boundaryId = SelectClosedPolyline(editor, "\nSelect the closed polyline defining the Google Earth image area: ");
            if (boundaryId.IsNull) return;

            List<Point3d> boundary;
            Point3d centroid;
            if (!ReadBoundary(document.Database, boundaryId, out boundary, out centroid))
            {
                editor.WriteMessage("\nCE_GOOGLEEARTHIMAGE: a valid closed lightweight polyline is required.");
                return;
            }

            string coordinateSystem = ReadDrawingCoordinateSystem(civilDocument);
            SurveyLocationInfo info = BuildSurveyInfo(document.Database, coordinateSystem, centroid, boundary);
            if (!info.HasGeographicTransform)
            {
                editor.WriteMessage("\nCE_GOOGLEEARTHIMAGE: the drawing needs a valid Civil 3D/AutoCAD geolocation coordinate system first. Run CE_SURVEYLOCATION to inspect it.");
                return;
            }

            PromptKeywordOptions openOptions = new PromptKeywordOptions(
                "\nOpen the selected boundary in Google Earth before choosing the image overlay file? [Yes/No] <Yes>: ");
            openOptions.Keywords.Add("Yes");
            openOptions.Keywords.Add("No");
            openOptions.Keywords.Default = "Yes";
            PromptResult openResult = editor.GetKeywords(openOptions);
            if (openResult.Status == PromptStatus.Cancel) return;
            if (openResult.Status == PromptStatus.None || string.Equals(openResult.StringResult, "Yes", StringComparison.OrdinalIgnoreCase))
            {
                string kml = WriteTemporaryBoundaryKml(info);
                OpenGoogleEarth(kml, info.CentreWgs84);
            }

            string overlayPath;
            using (var dialog = new OpenFileDialog
            {
                Filter = "Google Earth overlay (*.kmz;*.kml)|*.kmz;*.kml|KMZ (*.kmz)|*.kmz|KML (*.kml)|*.kml",
                Multiselect = false,
                CheckFileExists = true,
                Title = "Select a Google Earth GroundOverlay KML/KMZ"
            })
            {
                if (dialog.ShowDialog() != DialogResult.OK) return;
                overlayPath = dialog.FileName;
            }

            GroundOverlayData overlay;
            string parseError;
            if (!TryReadGroundOverlay(overlayPath, document.Database, out overlay, out parseError))
            {
                editor.WriteMessage("\nCE_GOOGLEEARTHIMAGE: {0}", parseError);
                return;
            }

            Point3d[] drawingCorners;
            if (!TryWgs84ToDrawing(document.Database, overlay.Wgs84Corners, out drawingCorners))
            {
                editor.WriteMessage("\nCE_GOOGLEEARTHIMAGE: Google Earth coordinates could not be transformed into this drawing's coordinate system.");
                return;
            }

            try
            {
                ObjectId imageId = AttachGroundOverlay(
                    document,
                    overlay.ImagePath,
                    drawingCorners,
                    boundary);
                editor.Regen();
                if (!imageId.IsNull) editor.SetImpliedSelection(new[] { imageId });
                editor.WriteMessage("\nCE_GOOGLEEARTHIMAGE: image attached, georeferenced from the GroundOverlay and clipped to the selected survey polyline.");
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage("\nCE_GOOGLEEARTHIMAGE failed. {0}", exception.Message);
            }
        }

        private static ObjectId SelectClosedPolyline(Editor editor, string message)
        {
            var options = new PromptEntityOptions(message);
            options.SetRejectMessage("\nSelect a closed lightweight polyline.");
            options.AddAllowedClass(typeof(Polyline), false);
            PromptEntityResult result = editor.GetEntity(options);
            if (result.Status != PromptStatus.OK) return ObjectId.Null;
            return result.ObjectId;
        }

        private static bool ReadBoundary(Database database, ObjectId id, out List<Point3d> boundary, out Point3d centroid)
        {
            boundary = new List<Point3d>();
            centroid = Point3d.Origin;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                Polyline polyline = transaction.GetObject(id, OpenMode.ForRead, false) as Polyline;
                if (polyline == null || !polyline.Closed || polyline.NumberOfVertices < 3) return false;
                boundary = TessellatePolyline(polyline, Math.PI / 36.0);
                if (boundary.Count < 3) return false;
                centroid = PolygonCentroid(boundary);
                return true;
            }
        }

        private static List<Point3d> TessellatePolyline(Polyline polyline, double maxAngle)
        {
            var result = new List<Point3d>();
            int count = polyline.NumberOfVertices;
            for (int index = 0; index < count; index++)
            {
                Point3d start = polyline.GetPoint3dAt(index);
                result.Add(start);
                if (polyline.GetSegmentType(index) != SegmentType.Arc) continue;
                try
                {
                    CircularArc2d arc = polyline.GetArcSegment2dAt(index);
                    double delta = arc.EndAngle - arc.StartAngle;
                    if (!arc.IsClockWise)
                        while (delta <= 0.0) delta += Math.PI * 2.0;
                    else
                        while (delta >= 0.0) delta -= Math.PI * 2.0;
                    int divisions = Math.Max(2, (int)Math.Ceiling(Math.Abs(delta) / Math.Max(0.01, maxAngle)));
                    for (int step = 1; step < divisions; step++)
                    {
                        double angle = arc.StartAngle + delta * step / divisions;
                        result.Add(new Point3d(
                            arc.Center.X + arc.Radius * Math.Cos(angle),
                            arc.Center.Y + arc.Radius * Math.Sin(angle),
                            start.Z));
                    }
                }
                catch { }
            }
            return result;
        }

        private static Point3d PolygonCentroid(IList<Point3d> points)
        {
            double twiceArea = 0.0;
            double cx = 0.0;
            double cy = 0.0;
            for (int i = 0; i < points.Count; i++)
            {
                Point3d a = points[i];
                Point3d b = points[(i + 1) % points.Count];
                double cross = a.X * b.Y - b.X * a.Y;
                twiceArea += cross;
                cx += (a.X + b.X) * cross;
                cy += (a.Y + b.Y) * cross;
            }
            if (Math.Abs(twiceArea) < 1e-12)
                return new Point3d(points.Average(p => p.X), points.Average(p => p.Y), points.Average(p => p.Z));
            return new Point3d(cx / (3.0 * twiceArea), cy / (3.0 * twiceArea), points.Average(p => p.Z));
        }

        private static string ReadDrawingCoordinateSystem(CivilDocument civilDocument)
        {
            try
            {
                string code = civilDocument.Settings.DrawingSettings.UnitZoneSettings.CoordinateSystemCode;
                return string.IsNullOrWhiteSpace(code) ? "." : code;
            }
            catch { return "."; }
        }

        private static string[] ReadCoordinateCodes()
        {
            try
            {
                return SettingsUnitZone.GetAllCodes()
                    .Where(code => !string.IsNullOrWhiteSpace(code))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(code => code, StringComparer.CurrentCultureIgnoreCase)
                    .ToArray();
            }
            catch { return new string[0]; }
        }

        private static SurveyLocationInfo BuildSurveyInfo(
            Database database,
            string coordinateSystem,
            Point3d centroid,
            IList<Point3d> boundary)
        {
            var info = new SurveyLocationInfo
            {
                CoordinateSystem = coordinateSystem,
                DrawingCentre = centroid,
                DrawingBoundary = boundary.ToList()
            };

            Point3d centreGeo;
            List<Point3d> boundaryGeo;
            if (TryDrawingToWgs84(database, centroid, boundary, out centreGeo, out boundaryGeo))
            {
                info.HasGeographicTransform = true;
                info.CentreWgs84 = centreGeo;
                info.BoundaryWgs84 = boundaryGeo;
                info.Town = ReverseGeocodeTown(centreGeo.Y, centreGeo.X);
                if (string.IsNullOrWhiteSpace(info.Town)) info.Town = "Location lookup unavailable";
            }
            else
            {
                info.HasGeographicTransform = false;
                info.Town = "Coordinate system/geolocation not resolved";
                info.BoundaryWgs84 = new List<Point3d>();
            }
            return info;
        }

        private static bool TryDrawingToWgs84(
            Database database,
            Point3d centroid,
            IList<Point3d> boundary,
            out Point3d centreGeo,
            out List<Point3d> boundaryGeo)
        {
            centreGeo = Point3d.Origin;
            boundaryGeo = new List<Point3d>();
            try
            {
                ObjectId geoId = database.GeoDataObject;
                if (geoId.IsNull) return false;
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    GeoLocationData geo = transaction.GetObject(geoId, OpenMode.ForRead, false) as GeoLocationData;
                    if (geo == null) return false;
                    centreGeo = geo.TransformToLonLatAlt(centroid);
                    foreach (Point3d point in boundary)
                        boundaryGeo.Add(geo.TransformToLonLatAlt(point));
                }
                return IsLonLat(centreGeo) && boundaryGeo.All(IsLonLat);
            }
            catch { return false; }
        }

        private static bool TryWgs84ToDrawing(Database database, IList<Point3d> geoPoints, out Point3d[] drawingPoints)
        {
            drawingPoints = new Point3d[0];
            try
            {
                ObjectId geoId = database.GeoDataObject;
                if (geoId.IsNull) return false;
                var result = new List<Point3d>();
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    GeoLocationData geo = transaction.GetObject(geoId, OpenMode.ForRead, false) as GeoLocationData;
                    if (geo == null) return false;
                    foreach (Point3d point in geoPoints)
                        result.Add(geo.TransformFromLonLatAlt(point));
                }
                drawingPoints = result.ToArray();
                return drawingPoints.Length == geoPoints.Count;
            }
            catch { return false; }
        }

        private static bool IsLonLat(Point3d value)
        {
            return !double.IsNaN(value.X) && !double.IsNaN(value.Y) &&
                   !double.IsInfinity(value.X) && !double.IsInfinity(value.Y) &&
                   value.X >= -180.0 && value.X <= 180.0 &&
                   value.Y >= -90.0 && value.Y <= 90.0;
        }

        private static bool ApplyCoordinateSystem(
            Document document,
            CivilDocument civilDocument,
            string code,
            out string message)
        {
            message = string.Empty;
            try
            {
                SettingsUnitZone.GetCoordinateSystemByCode(code);
            }
            catch
            {
                message = "'" + code + "' is not a coordinate-system code available in this Civil 3D installation.";
                return false;
            }

            try
            {
                using (DocumentLock documentLock = document.LockDocument())
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    civilDocument.Settings.DrawingSettings.UnitZoneSettings.CoordinateSystemCode = code;
                    try
                    {
                        ObjectId geoId = document.Database.GeoDataObject;
                        if (!geoId.IsNull)
                        {
                            GeoLocationData geo = transaction.GetObject(geoId, OpenMode.ForWrite, false) as GeoLocationData;
                            if (geo != null)
                            {
                                geo.CoordinateSystem = code;
                                geo.UpdateTransformationMatrix();
                            }
                        }
                    }
                    catch { }
                    transaction.Commit();
                }
                message = "Coordinate system changed to " + code + ".";
                return true;
            }
            catch (System.Exception exception)
            {
                message = "The coordinate system could not be applied. " + exception.Message;
                return false;
            }
        }

        private static string ReverseGeocodeTown(double latitude, double longitude)
        {
            try
            {
                string url = string.Format(
                    CultureInfo.InvariantCulture,
                    "https://nominatim.openstreetmap.org/reverse?format=jsonv2&lat={0:R}&lon={1:R}&zoom=10&addressdetails=1",
                    latitude,
                    longitude);
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.UserAgent = "CE-Tools-Civil3D/1.0 (survey location lookup)";
                request.Timeout = 4500;
                request.ReadWriteTimeout = 4500;
                using (WebResponse response = request.GetResponse())
                using (StreamReader reader = new StreamReader(response.GetResponseStream()))
                {
                    string json = reader.ReadToEnd();
                    foreach (string key in new[] { "city", "town", "village", "municipality", "county", "state" })
                    {
                        Match match = Regex.Match(
                            json,
                            "\\\"" + key + "\\\"\\s*:\\s*\\\"(?<value>(?:\\\\.|[^\\\"])*)\\\"",
                            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                        if (match.Success)
                            return JsonUnescape(match.Groups["value"].Value);
                    }
                }
            }
            catch { }
            return string.Empty;
        }

        private static string JsonUnescape(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            return Regex.Unescape(value.Replace("\\/", "/"));
        }

        private static string WriteTemporaryBoundaryKml(SurveyLocationInfo info)
        {
            string folder = Path.Combine(Path.GetTempPath(), "CE Tools", "Google Earth");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "CE_Survey_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".kml");
            File.WriteAllText(path, BuildBoundaryKml(info), new UTF8Encoding(false));
            return path;
        }

        private static string BuildBoundaryKml(SurveyLocationInfo info)
        {
            string name = SecurityElement.Escape(string.IsNullOrWhiteSpace(info.Town) ? "CE Survey Boundary" : info.Town + " - Survey Boundary");
            var coordinates = new StringBuilder();
            foreach (Point3d point in info.BoundaryWgs84)
                coordinates.AppendFormat(CultureInfo.InvariantCulture, "{0:R},{1:R},0 ", point.X, point.Y);
            if (info.BoundaryWgs84.Count > 0)
            {
                Point3d first = info.BoundaryWgs84[0];
                coordinates.AppendFormat(CultureInfo.InvariantCulture, "{0:R},{1:R},0", first.X, first.Y);
            }

            return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
                   "<kml xmlns=\"http://www.opengis.net/kml/2.2\"><Document>" +
                   "<name>" + name + "</name>" +
                   "<Placemark><name>Survey centre</name><Point><coordinates>" +
                   info.CentreWgs84.X.ToString("R", CultureInfo.InvariantCulture) + "," +
                   info.CentreWgs84.Y.ToString("R", CultureInfo.InvariantCulture) + ",0</coordinates></Point></Placemark>" +
                   "<Placemark><name>Survey boundary</name><Style><LineStyle><width>3</width></LineStyle><PolyStyle><fill>0</fill></PolyStyle></Style>" +
                   "<Polygon><outerBoundaryIs><LinearRing><coordinates>" + coordinates +
                   "</coordinates></LinearRing></outerBoundaryIs></Polygon></Placemark>" +
                   "</Document></kml>";
        }

        private static void OpenGoogleEarth(string kmlPath, Point3d centreWgs84)
        {
            try
            {
                Process.Start(new ProcessStartInfo(kmlPath) { UseShellExecute = true });
                return;
            }
            catch { }

            try
            {
                string url = string.Format(
                    CultureInfo.InvariantCulture,
                    "https://earth.google.com/web/@{0:R},{1:R},1000a,1000d,35y,0h,0t,0r",
                    centreWgs84.Y,
                    centreWgs84.X);
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch { }
        }

        private static bool TryReadGroundOverlay(
            string sourcePath,
            Database database,
            out GroundOverlayData overlay,
            out string error)
        {
            overlay = null;
            error = string.Empty;
            string kmlText;
            string imagePath;
            try
            {
                if (string.Equals(Path.GetExtension(sourcePath), ".kmz", StringComparison.OrdinalIgnoreCase))
                {
                    using (ZipArchive archive = ZipFile.OpenRead(sourcePath))
                    {
                        ZipArchiveEntry kmlEntry = archive.Entries.FirstOrDefault(entry =>
                            string.Equals(Path.GetExtension(entry.FullName), ".kml", StringComparison.OrdinalIgnoreCase));
                        if (kmlEntry == null)
                        {
                            error = "The selected KMZ contains no KML document.";
                            return false;
                        }
                        using (StreamReader reader = new StreamReader(kmlEntry.Open()))
                            kmlText = reader.ReadToEnd();

                        string href = ReadGroundOverlayHref(kmlText);
                        if (string.IsNullOrWhiteSpace(href))
                        {
                            error = "The selected KMZ does not contain a GroundOverlay image reference.";
                            return false;
                        }
                        if (Uri.IsWellFormedUriString(href, UriKind.Absolute))
                        {
                            error = "The GroundOverlay references an online image. Save/export the Google Earth overlay as a KMZ with the image embedded, then import that KMZ.";
                            return false;
                        }
                        string normalizedHref = href.Replace('\\', '/').TrimStart('/');
                        ZipArchiveEntry imageEntry = archive.Entries.FirstOrDefault(entry =>
                            string.Equals(entry.FullName.Replace('\\', '/'), normalizedHref, StringComparison.OrdinalIgnoreCase)) ??
                            archive.Entries.FirstOrDefault(entry =>
                                string.Equals(Path.GetFileName(entry.FullName), Path.GetFileName(normalizedHref), StringComparison.OrdinalIgnoreCase));
                        if (imageEntry == null)
                        {
                            error = "The image referenced by the GroundOverlay is not embedded in the KMZ.";
                            return false;
                        }
                        imagePath = ExtractPersistentImage(imageEntry, database);
                    }
                }
                else
                {
                    kmlText = File.ReadAllText(sourcePath);
                    string href = ReadGroundOverlayHref(kmlText);
                    if (string.IsNullOrWhiteSpace(href))
                    {
                        error = "The selected KML does not contain a GroundOverlay image reference.";
                        return false;
                    }
                    if (Uri.IsWellFormedUriString(href, UriKind.Absolute))
                    {
                        error = "The GroundOverlay references an online image. Use a local image/KML or save the overlay as a KMZ with the image embedded.";
                        return false;
                    }
                    imagePath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath) ?? string.Empty, href.Replace('/', Path.DirectorySeparatorChar)));
                    if (!File.Exists(imagePath))
                    {
                        error = "The GroundOverlay image file could not be found: " + imagePath;
                        return false;
                    }
                }
            }
            catch (System.Exception exception)
            {
                error = "The KML/KMZ could not be read. " + exception.Message;
                return false;
            }

            List<Point3d> corners;
            string cornerError;
            if (!TryReadGroundOverlayCorners(kmlText, out corners, out cornerError))
            {
                error = cornerError;
                return false;
            }

            overlay = new GroundOverlayData { ImagePath = imagePath, Wgs84Corners = corners };
            return true;
        }

        private static string ReadGroundOverlayHref(string kmlText)
        {
            try
            {
                var document = new XmlDocument();
                document.LoadXml(kmlText);
                XmlNode overlay = FindFirstByLocalName(document, "GroundOverlay");
                if (overlay == null) return string.Empty;
                XmlNode icon = FindFirstByLocalName(overlay, "Icon");
                XmlNode href = icon == null ? null : FindFirstByLocalName(icon, "href");
                return href == null ? string.Empty : href.InnerText.Trim();
            }
            catch { return string.Empty; }
        }

        private static bool TryReadGroundOverlayCorners(string kmlText, out List<Point3d> corners, out string error)
        {
            corners = new List<Point3d>();
            error = string.Empty;
            try
            {
                var document = new XmlDocument();
                document.LoadXml(kmlText);
                XmlNode overlay = FindFirstByLocalName(document, "GroundOverlay");
                if (overlay == null)
                {
                    error = "The selected KML/KMZ contains no GroundOverlay.";
                    return false;
                }

                XmlNode quad = FindFirstByLocalName(overlay, "LatLonQuad");
                if (quad != null)
                {
                    XmlNode coordinatesNode = FindFirstByLocalName(quad, "coordinates");
                    if (coordinatesNode != null)
                    {
                        foreach (string token in Regex.Split(coordinatesNode.InnerText.Trim(), "\\s+"))
                        {
                            string[] values = token.Split(',');
                            double lon;
                            double lat;
                            if (values.Length >= 2 &&
                                double.TryParse(values[0], NumberStyles.Float, CultureInfo.InvariantCulture, out lon) &&
                                double.TryParse(values[1], NumberStyles.Float, CultureInfo.InvariantCulture, out lat))
                                corners.Add(new Point3d(lon, lat, 0.0));
                        }
                        if (corners.Count >= 4)
                        {
                            corners = corners.Take(4).ToList();
                            return true;
                        }
                    }
                }

                XmlNode box = FindFirstByLocalName(overlay, "LatLonBox");
                if (box == null)
                {
                    error = "The GroundOverlay has neither gx:LatLonQuad nor LatLonBox coordinates.";
                    return false;
                }
                double north = ReadChildDouble(box, "north");
                double south = ReadChildDouble(box, "south");
                double east = ReadChildDouble(box, "east");
                double west = ReadChildDouble(box, "west");
                double rotation = ReadOptionalChildDouble(box, "rotation", 0.0);
                if (north <= south)
                {
                    error = "The GroundOverlay latitude box is invalid.";
                    return false;
                }

                double centreLon = (west + east) * 0.5;
                double centreLat = (south + north) * 0.5;
                corners.Add(RotateLonLat(west, south, centreLon, centreLat, rotation));
                corners.Add(RotateLonLat(east, south, centreLon, centreLat, rotation));
                corners.Add(RotateLonLat(east, north, centreLon, centreLat, rotation));
                corners.Add(RotateLonLat(west, north, centreLon, centreLat, rotation));
                return true;
            }
            catch (System.Exception exception)
            {
                error = "GroundOverlay coordinates could not be read. " + exception.Message;
                return false;
            }
        }

        private static XmlNode FindFirstByLocalName(XmlNode root, string localName)
        {
            if (root == null) return null;
            if (string.Equals(root.LocalName, localName, StringComparison.OrdinalIgnoreCase)) return root;
            foreach (XmlNode child in root.ChildNodes)
            {
                XmlNode found = FindFirstByLocalName(child, localName);
                if (found != null) return found;
            }
            return null;
        }

        private static double ReadChildDouble(XmlNode parent, string name)
        {
            XmlNode node = FindFirstByLocalName(parent, name);
            if (node == null) throw new InvalidDataException("GroundOverlay is missing '" + name + "'.");
            return double.Parse(node.InnerText, NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        private static double ReadOptionalChildDouble(XmlNode parent, string name, double fallback)
        {
            XmlNode node = FindFirstByLocalName(parent, name);
            double value;
            return node != null && double.TryParse(node.InnerText, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                ? value
                : fallback;
        }

        private static Point3d RotateLonLat(double lon, double lat, double centreLon, double centreLat, double degrees)
        {
            if (Math.Abs(degrees) < 1e-12) return new Point3d(lon, lat, 0.0);
            double cosLat = Math.Max(1e-9, Math.Cos(centreLat * Math.PI / 180.0));
            double x = (lon - centreLon) * cosLat;
            double y = lat - centreLat;
            double angle = degrees * Math.PI / 180.0;
            double rx = x * Math.Cos(angle) - y * Math.Sin(angle);
            double ry = x * Math.Sin(angle) + y * Math.Cos(angle);
            return new Point3d(centreLon + rx / cosLat, centreLat + ry, 0.0);
        }

        private static string ExtractPersistentImage(ZipArchiveEntry entry, Database database)
        {
            string drawingFolder = string.IsNullOrWhiteSpace(database.Filename)
                ? string.Empty
                : Path.GetDirectoryName(database.Filename);
            string folder = !string.IsNullOrWhiteSpace(drawingFolder) && Directory.Exists(drawingFolder)
                ? Path.Combine(drawingFolder, "CE_GoogleEarth_Images")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "CE Tools", "Google Earth Images");
            Directory.CreateDirectory(folder);
            string extension = Path.GetExtension(entry.Name);
            string fileName = "GE_Overlay_" + DateTime.Now.ToString("yyyyMMdd_HHmmssfff", CultureInfo.InvariantCulture) + extension;
            string target = Path.Combine(folder, fileName);
            using (Stream source = entry.Open())
            using (FileStream destination = File.Create(target))
                source.CopyTo(destination);
            return target;
        }

        private static ObjectId AttachGroundOverlay(
            Document document,
            string imagePath,
            Point3d[] drawingCorners,
            IList<Point3d> clipBoundary)
        {
            if (drawingCorners == null || drawingCorners.Length < 4)
                throw new InvalidOperationException("Four GroundOverlay corners are required.");

            Database database = document.Database;
            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                ObjectId dictionaryId = RasterImageDef.GetImageDictionary(database);
                if (dictionaryId.IsNull)
                {
                    RasterImageDef.CreateImageDictionary(database);
                    dictionaryId = RasterImageDef.GetImageDictionary(database);
                }
                DBDictionary dictionary = transaction.GetObject(dictionaryId, OpenMode.ForWrite, false) as DBDictionary;
                if (dictionary == null) throw new InvalidOperationException("Raster image dictionary is unavailable.");

                var definition = new RasterImageDef { SourceFileName = imagePath };
                definition.Load();
                string definitionName = UniqueDictionaryName(dictionary, "CE_GE_Overlay");
                ObjectId definitionId = dictionary.SetAt(definitionName, definition);
                transaction.AddNewlyCreatedDBObject(definition, true);

                BlockTableRecord modelSpace = transaction.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(database),
                    OpenMode.ForWrite,
                    false) as BlockTableRecord;
                if (modelSpace == null) throw new InvalidOperationException("Model space is unavailable.");

                Point3d lowerLeft = drawingCorners[0];
                Vector3d imageX = drawingCorners[1] - drawingCorners[0];
                Vector3d imageY = drawingCorners[3] - drawingCorners[0];
                if (imageX.Length < 1e-9 || imageY.Length < 1e-9)
                    throw new InvalidOperationException("The GroundOverlay transformed to a zero-size image.");

                var raster = new RasterImage
                {
                    ImageDefId = definitionId,
                    Orientation = new CoordinateSystem3d(lowerLeft, imageX, imageY),
                    Rotation = 0.0,
                    ShowImage = true,
                    ImageTransparency = false
                };
                raster.SetDatabaseDefaults(database);
                modelSpace.AppendEntity(raster);
                transaction.AddNewlyCreatedDBObject(raster, true);
                RasterImage.EnableReactors(true);
                raster.AssociateRasterDef(definition);

                TryClipRaster(raster, clipBoundary);
                transaction.Commit();
                return raster.ObjectId;
            }
        }

        private static void TryClipRaster(RasterImage raster, IList<Point3d> modelBoundary)
        {
            try
            {
                Matrix3d modelToPixel = raster.PixelToModelTransform.Inverse();
                var pixels = new Point2dCollection();
                Plane plane = new Plane(Point3d.Origin, Vector3d.ZAxis);
                foreach (Point3d point in modelBoundary)
                    pixels.Add(point.TransformBy(modelToPixel).Convert2d(plane));
                if (pixels.Count < 3) return;
                pixels.Add(pixels[0]);
                raster.SetClipBoundary(ClipBoundaryType.Polygonal, pixels);
                raster.IsClipped = true;
            }
            catch
            {
                // The image is still correctly georeferenced even if a source
                // polyline cannot be represented as a valid raster pixel clip.
            }
        }

        private static string UniqueDictionaryName(DBDictionary dictionary, string prefix)
        {
            if (!dictionary.Contains(prefix)) return prefix;
            for (int index = 2; index < 100000; index++)
            {
                string name = prefix + "_" + index.ToString(CultureInfo.InvariantCulture);
                if (!dictionary.Contains(name)) return name;
            }
            return prefix + "_" + Guid.NewGuid().ToString("N");
        }

        private sealed class SurveyLocationInfo
        {
            internal string Town;
            internal string CoordinateSystem;
            internal bool HasGeographicTransform;
            internal Point3d DrawingCentre;
            internal Point3d CentreWgs84;
            internal List<Point3d> DrawingBoundary;
            internal List<Point3d> BoundaryWgs84;
        }

        private sealed class GroundOverlayData
        {
            internal string ImagePath;
            internal List<Point3d> Wgs84Corners;
        }

        private enum SurveyDialogAction
        {
            None,
            ApplyCoordinateSystem,
            OpenGoogleEarth,
            ExportKml,
            Close
        }

        private sealed class SurveyLocationForm : Form
        {
            private readonly ComboBox _coordinateSystem;
            internal SurveyDialogAction Action { get; private set; }
            internal string SelectedCoordinateSystem { get { return _coordinateSystem.Text; } }

            internal SurveyLocationForm(SurveyLocationInfo info, IEnumerable<string> coordinateSystems)
            {
                Text = "CE Tools - Survey Location / Google Earth";
                StartPosition = FormStartPosition.CenterScreen;
                Width = 720;
                Height = 430;
                MinimizeBox = false;
                MaximizeBox = false;
                FormBorderStyle = FormBorderStyle.FixedDialog;

                var table = new TableLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    ColumnCount = 2,
                    RowCount = 7,
                    Padding = new Padding(14),
                    AutoSize = false
                };
                table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
                table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                Controls.Add(table);

                AddRow(table, 0, "Town / nearest locality", new TextBox { ReadOnly = true, Text = info.Town ?? string.Empty, Dock = DockStyle.Fill });
                AddRow(table, 1, "Drawing centre", new TextBox
                {
                    ReadOnly = true,
                    Text = string.Format(CultureInfo.InvariantCulture, "X {0:0.###}   Y {1:0.###}", info.DrawingCentre.X, info.DrawingCentre.Y),
                    Dock = DockStyle.Fill
                });
                AddRow(table, 2, "WGS84 / Google Earth", new TextBox
                {
                    ReadOnly = true,
                    Text = info.HasGeographicTransform
                        ? string.Format(CultureInfo.InvariantCulture, "Lat {0:0.########}   Lon {1:0.########}", info.CentreWgs84.Y, info.CentreWgs84.X)
                        : "Not available - drawing geolocation is not resolved",
                    Dock = DockStyle.Fill
                });
                AddRow(table, 3, "Civil 3D coordinate system", new TextBox { ReadOnly = true, Text = info.CoordinateSystem ?? ".", Dock = DockStyle.Fill });

                _coordinateSystem = new ComboBox
                {
                    Dock = DockStyle.Fill,
                    DropDownStyle = ComboBoxStyle.DropDown,
                    AutoCompleteMode = AutoCompleteMode.SuggestAppend,
                    AutoCompleteSource = AutoCompleteSource.ListItems
                };
                foreach (string code in coordinateSystems ?? new string[0]) _coordinateSystem.Items.Add(code);
                _coordinateSystem.Text = info.CoordinateSystem ?? string.Empty;
                AddRow(table, 4, "Use different coordinate system", _coordinateSystem);

                var note = new Label
                {
                    Text = "Changing the coordinate system updates the Civil 3D drawing coordinate-system code and the existing AutoCAD geolocation transformation. Google Earth buttons use the actual DWG ↔ longitude/latitude transform stored in the drawing.",
                    AutoSize = true,
                    MaximumSize = new System.Drawing.Size(470, 0),
                    Dock = DockStyle.Fill
                };
                AddRow(table, 5, "", note);

                var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
                buttons.Controls.Add(ActionButton("Apply CRS / Recalculate", SurveyDialogAction.ApplyCoordinateSystem));
                buttons.Controls.Add(ActionButton("Open in Google Earth", SurveyDialogAction.OpenGoogleEarth));
                buttons.Controls.Add(ActionButton("Export KML", SurveyDialogAction.ExportKml));
                buttons.Controls.Add(ActionButton("Close", SurveyDialogAction.Close));
                table.Controls.Add(buttons, 0, 6);
                table.SetColumnSpan(buttons, 2);
            }

            private Button ActionButton(string text, SurveyDialogAction action)
            {
                var button = new Button { Text = text, AutoSize = true, Padding = new Padding(8, 4, 8, 4) };
                button.Click += delegate
                {
                    Action = action;
                    DialogResult = DialogResult.OK;
                    Close();
                };
                return button;
            }

            private static void AddRow(TableLayoutPanel table, int row, string caption, Control control)
            {
                table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                var label = new Label { Text = caption, AutoSize = true, Dock = DockStyle.Fill, TextAlign = System.Drawing.ContentAlignment.MiddleLeft };
                table.Controls.Add(label, 0, row);
                table.Controls.Add(control, 1, row);
            }
        }
    }
}

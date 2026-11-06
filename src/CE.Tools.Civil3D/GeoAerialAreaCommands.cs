using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.GeoAerialAreaCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// User picks a rectangle in drawing coordinates and CE Tools requests
    /// georeferenced JPEG orthoimage tiles from a WMS provider that expressly
    /// permits downloads, local storage, and engineering drawing reuse.
    ///
    /// This NEVER queries or downloads Google Earth/Google Maps imagery.
    /// It does not perform any coordinate system transformation: the WMS SRS
    /// MUST be the exact projected coordinate system of the DWG.
    /// </summary>
    public sealed class GeoAerialAreaCommands
    {
        private const int MaxTiles = 64;
        private const long MaxTotalPixels = 130000000L;

        [CommandMethod("CE_TOOLS", "CE_AERIALAREA", CommandFlags.Modal | CommandFlags.Redraw)]
        public void DownloadAreaFromLicensedWms()
        {
            Document doc = AcApplication.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Select Aerial Image Area",
                "Pick a rectangle in model space, then fetch and insert aerial imagery from a "
                + "licensed WMS aerial/orthophoto provider. Images are saved as JPEG + JGW "
                + "and precisely aligned to the selected drawing coordinates.");
            settings.AddText("Endpoint", "01 Imagery provider", "WMS GetMap service URL", "",
                "Enter your authorized OGC WMS 1.1.1 service endpoint (https://...). "
                + "Use the base URL without a GetMap request. Google Earth/Maps tile services are not supported.");
            settings.AddText("Layer", "01 Imagery provider", "Aerial imagery layer name", "",
                "Exact layer identifier advertised in the provider's WMS GetCapabilities document.");
            settings.AddText("EPSG", "02 Drawing reference", "Drawing projected SRS (EPSG code)", "",
                "Enter the projected EPSG number used by both the Civil 3D DWG and the imagery service. "
                + "No reprojection is performed; the drawing must already use the same projected metres.");
            settings.AddChoice("Rights", "02 Drawing reference", "Offline engineering use permitted", "Not verified",
                "You must verify the service provider's terms explicitly permit JPEG download, "
                + "local storage and CAD use; preserve their attribution requirements.",
                new[] { "Not verified", "I have permission to download and use this imagery" });
            settings.AddChoice("Resolution", "03 Tile quality", "Ground sampling distance (m/pixel)", "0.25",
                "Requested output image pixel size, not necessarily real source imagery resolution. "
                + "The provider decides the actual imagery quality and date.",
                new[] { "0.10", "0.25", "0.50", "1.00" });
            settings.AddChoice("TilePixels", "03 Tile quality", "Tile width and height (pixels)", "2048",
                "Large imagery areas are split into adjacent, correctly referenced JPEGs.",
                new[] { "1024", "2048" });
            settings.AddChoice("After", "04 Insertion", "After retrieval", "Insert and zoom",
                "Attach the JPEG files as external raster references on CE-AERIAL-IMAGERY.",
                new[] { "Insert and zoom", "Insert and keep current view", "Save JPEG/JGW only" });
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;
            try
            {
                string endpoint = settings.Text("Endpoint").Trim();
                string layer = settings.Text("Layer").Trim();
                string epsg = settings.Text("EPSG").Trim();
                if (settings.Text("Rights") != "I have permission to download and use this imagery")
                    throw new InvalidOperationException("Image download requires confirmation of licensing for offline CAD use.");
                if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri address)
                    || address.Scheme != Uri.UriSchemeHttps
                    || address.UserInfo.Length > 0
                    || IsGoogleImageryHost(address.Host))
                    throw new InvalidOperationException("Enter a valid HTTPS WMS service URL from a licensed provider (not Google Earth or Google Maps).");
                if (string.IsNullOrWhiteSpace(layer))
                    throw new InvalidOperationException("Enter a WMS aerial imagery layer from the provider's GetCapabilities listing.");
                if (epsg.Length == 0 || !epsg.All(char.IsDigit)
                    || !int.TryParse(epsg, NumberStyles.None, CultureInfo.InvariantCulture, out int code)
                    || code < 2000 || (code >= 4000 && code < 5000))
                    throw new InvalidOperationException("Enter the drawing's verified projected EPSG code (digits only, e.g. from your Civil 3D drawing coordinate system).");
                if (doc.Database.TileMode == false)
                    throw new InvalidOperationException("Switch to Model space before selecting the aerial image area.");
                doc.Editor.WriteMessage("\nCE_AERIALAREA: select the first corner of the area to capture in model space.");
                PromptPointResult first = doc.Editor.GetPoint("\nFirst corner of aerial imagery area: ");
                if (first.Status != PromptStatus.OK) return;
                var opts = new PromptCornerOptions("\nOpposite corner of aerial imagery area: ", first.Value);
                PromptPointResult last = doc.Editor.GetCorner(opts);
                if (last.Status != PromptStatus.OK) return;
                Matrix3d ucs = doc.Editor.CurrentUserCoordinateSystem;
                Point3d p1 = first.Value.TransformBy(ucs);
                Point3d p2 = last.Value.TransformBy(ucs);
                // The selected rectangle is a drawing-WCS axis-aligned BBOX.
                double minX = Math.Min(p1.X, p2.X);
                double minY = Math.Min(p1.Y, p2.Y);
                double maxX = Math.Max(p1.X, p2.X);
                double maxY = Math.Max(p1.Y, p2.Y);
                double width = maxX - minX, height = maxY - minY;
                if (width <= 0 || height <= 0)
                    throw new InvalidOperationException("Selected image area must have nonzero width and height.");
                double resolution = double.Parse(settings.Text("Resolution"), CultureInfo.InvariantCulture);
                int tilePixels = int.Parse(settings.Text("TilePixels"), CultureInfo.InvariantCulture);
                if (width / resolution > 130000000.0 || height / resolution > 130000000.0)
                    throw new InvalidOperationException("The requested image is too large. Select a smaller area or coarser resolution.");
                int fullWidth = (int)Math.Ceiling(width / resolution);
                int fullHeight = (int)Math.Ceiling(height / resolution);
                if (fullWidth < 1 || fullHeight < 1 ||
                    (long)fullWidth * fullHeight > MaxTotalPixels)
                    throw new InvalidOperationException("Selected area is too large at this pixel size. Choose a coarser resolution or a smaller rectangle.");
                int cols = (fullWidth + tilePixels - 1) / tilePixels;
                int rows = (fullHeight + tilePixels - 1) / tilePixels;
                if ((long)cols * rows > MaxTiles)
                    throw new InvalidOperationException("Maximum 64 tiles per request. Select a smaller area or a coarser pixel size.");

                string folder;
                using (var picker = new FolderBrowserDialog {
                    Description = "Choose permanent folder for georeferenced aerial image JPEG tiles (do not move them after attachment)",
                    ShowNewFolderButton = true
                })
                {
                    if (picker.ShowDialog() != DialogResult.OK) return;
                    folder = picker.SelectedPath;
                }
                var files = new List<string>();
                // One full-width/height ground pixel size across the selection,
                // even when the final tile at each edge is smaller.
                double sx = width / fullWidth;
                double sy = height / fullHeight;
                string prefix = "CE_AERIAL_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
                for (int row = 0; row < rows; row++)
                for (int col = 0; col < cols; col++)
                {
                    int px = col * tilePixels;
                    int py = row * tilePixels;
                    int tileW = Math.Min(tilePixels, fullWidth - px);
                    int tileH = Math.Min(tilePixels, fullHeight - py);
                    double x0 = minX + px * sx;
                    double x1 = minX + (px + tileW) * sx;
                    double y1 = maxY - py * sy;
                    double y0 = maxY - (py + tileH) * sy;
                    string stem = prefix + string.Format(CultureInfo.InvariantCulture, "_R{0:000}_C{1:000}", row + 1, col + 1);
                    string imagePath = Path.Combine(folder, stem + ".jpg");
                    string worldPath = Path.Combine(folder, stem + ".jgw");
                    if (File.Exists(imagePath) || File.Exists(worldPath))
                        throw new IOException("Refusing to overwrite existing image/world file " + stem);
                    byte[] jpeg = FetchWmsJpeg(address, layer, "EPSG:" + code,
                        x0, y0, x1, y1, tileW, tileH);
                    File.WriteAllBytes(imagePath, jpeg);
                    // World files record the exact first *pixel centre*.
                    double centreX = x0 + sx / 2.0;
                    double centreY = y1 - sy / 2.0;
                    File.WriteAllLines(worldPath, new[] {
                        sx.ToString("R", CultureInfo.InvariantCulture),
                        "0", "0", (-sy).ToString("R", CultureInfo.InvariantCulture),
                        centreX.ToString("R", CultureInfo.InvariantCulture),
                        centreY.ToString("R", CultureInfo.InvariantCulture)
                    });
                    files.Add(imagePath);
                    doc.Editor.WriteMessage("\nCE_AERIALAREA: retrieved tile {0}/{1}.", files.Count, rows * cols);
                }
                if (settings.Text("After") != "Save JPEG/JGW only")
                    GeoJpegTilesCommands.InsertAll(doc, files, settings.Text("After") == "Insert and zoom");
                doc.Editor.WriteMessage(
                    "\nCE_AERIALAREA: {0} georeferenced aerial tiles saved at {1}. Verify drawing EPSG, control points and provider attribution.",
                    files.Count, folder);
            }
            catch (System.Exception exception)
            {
                doc.Editor.WriteMessage("\nCE_AERIALAREA: " + exception.Message);
            }
        }

        private static bool IsGoogleImageryHost(string host)
        {
            string h = host.ToLowerInvariant();
            return h == "google.com" || h.EndsWith(".google.com") ||
                h == "googleapis.com" || h.EndsWith(".googleapis.com") ||
                h == "googleusercontent.com" || h.EndsWith(".googleusercontent.com") ||
                h == "gstatic.com" || h.EndsWith(".gstatic.com");
        }

        private static byte[] FetchWmsJpeg(Uri endpoint, string layer, string srs,
            double xmin, double ymin, double xmax, double ymax, int width, int height)
        {
            // WMS 1.1.1 axis order is x,y even for geographic CRS. This command
            // explicitly requires a projected drawing CRS, not latitude/longitude.
            string bbox = string.Join(",", new[] { xmin, ymin, xmax, ymax }
                .Select(v => v.ToString("R", CultureInfo.InvariantCulture)));
            string query = "SERVICE=WMS&VERSION=1.1.1&REQUEST=GetMap"
                + "&LAYERS=" + Uri.EscapeDataString(layer)
                + "&STYLES=&SRS=" + Uri.EscapeDataString(srs)
                + "&BBOX=" + Uri.EscapeDataString(bbox)
                + "&WIDTH=" + width.ToString(CultureInfo.InvariantCulture)
                + "&HEIGHT=" + height.ToString(CultureInfo.InvariantCulture)
                + "&FORMAT=image%2Fjpeg&TRANSPARENT=FALSE";
            string separator = endpoint.Query.Length > 0 ? "&" : "?";
            // A configured WMS URL may include an API key. Do not print URLs
            // in errors/logging, as they could expose service credentials.
            var uri = new Uri(endpoint.AbsoluteUri + separator + query);
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(uri);
            request.Timeout = 60000;
            request.ReadWriteTimeout = 60000;
            request.Method = "GET";
            request.Accept = "image/jpeg";
            request.UserAgent = "CE Tools Civil3D Aerial WMS/1.0";
            request.AllowAutoRedirect = false; // do not redirect credentials
            try
            {
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            {
                if (response.StatusCode != HttpStatusCode.OK)
                    throw new IOException("Aerial imagery service returned HTTP " + (int)response.StatusCode);
                if (response.ContentLength > 35000000L)
                    throw new IOException("Image tile is too large for safe import.");
                string mime = (response.ContentType ?? "").ToLowerInvariant();
                if (!mime.StartsWith("image/jpeg", StringComparison.OrdinalIgnoreCase)
                    && !mime.StartsWith("image/jpg", StringComparison.OrdinalIgnoreCase))
                    throw new IOException("WMS did not return JPEG imagery. Verify its layer, EPSG, output format and access rights.");
                using (Stream responseStream = response.GetResponseStream())
                using (var buffer = new MemoryStream())
                {
                    byte[] bytes = new byte[65536];
                    int count;
                    while ((count = responseStream.Read(bytes, 0, bytes.Length)) > 0)
                    {
                        if (buffer.Length + count > 35000000L)
                            throw new IOException("WMS image exceeds the safe 35 MB tile size.");
                        buffer.Write(bytes, 0, count);
                    }
                    byte[] data = buffer.ToArray();
                    if (data.Length < 10 || data[0] != 0xFF || data[1] != 0xD8)
                        throw new IOException("WMS returned invalid JPEG data.");
                    using (var imageStream = new MemoryStream(data))
                    using (Image image = Image.FromStream(imageStream))
                    {
                        if (image.Width != width || image.Height != height)
                            throw new IOException("WMS returned unexpected image dimensions.");
                    }
                    return data;
                }
            }
            }
            catch (WebException ex)
            {
                // Do not surface the full request URL; WMS service URLs can
                // contain access tokens and private API credentials.
                throw new IOException("WMS request failed (" + ex.Status +
                    "). Verify the service, layer, EPSG and provider access rights.");
            }
        }
    }
}

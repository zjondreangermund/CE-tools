using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using Image = System.Drawing.Image;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.GeoJpegTilesCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Creates high-quality, locally stored JPEG tiles from an image the user
    /// has permission to process, with a matching six-parameter JGW file for
    /// every tile.  Imports these images directly into Civil 3D model space
    /// using their affine world-file coordinates.  No Google tile requests,
    /// screen scraping, or Google Earth downloads occur in this command.
    ///
    /// World files DO NOT store CRS metadata.  Input must already be in the
    /// same projected CRS and drawing units as the Civil 3D DWG; longitude/
    /// latitude degrees must not be mistaken for metres.  Use a GIS reprojection
    /// utility to transform to the DWG CRS before importing.
    /// </summary>
    public sealed class GeoJpegTilesCommands
    {
        private const string LayerName = "CE-AERIAL-IMAGERY";
        private const int MaximumTiles = 256;
        private const long MaximumPixels = 180000000L;

        private sealed class World
        {
            // Pixel-centre world-file coefficients:
            // X = A*col + B*row + C; Y = D*col + E*row + F.
            internal double A, B, C, D, E, F;

            internal World Offset(int col, int row)
            {
                return new World
                {
                    A = A, B = B, D = D, E = E,
                    C = C + A * col + B * row,
                    F = F + D * col + E * row
                };
            }

            internal Point3d LowerLeft(int width, int height)
            {
                return new Point3d(
                    C - 0.5 * A - 0.5 * B + height * B,
                    F - 0.5 * D - 0.5 * E + height * E, 0);
            }

            internal Vector3d Horizontal(int width)
            {
                return new Vector3d(A * width, D * width, 0);
            }

            internal Vector3d Vertical(int height)
            {
                return new Vector3d(-B * height, -E * height, 0);
            }

            internal void Validate()
            {
                double determinant = A * E - B * D;
                if (new[] { A, B, C, D, E, F }.Any(v => double.IsNaN(v) || double.IsInfinity(v))
                    || Math.Abs(determinant) < 1e-16)
                    throw new InvalidDataException("Invalid JPEG world file: coefficients are non-finite or axes are degenerate.");
            }

            internal static World Read(string path)
            {
                string[] lines = File.ReadAllLines(path)
                    .Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
                if (lines.Length != 6)
                    throw new InvalidDataException("JGW needs exactly six numeric lines: A, D, B, E, C, F.");
                double[] v = lines.Select(x => double.Parse(x.Trim(), CultureInfo.InvariantCulture)).ToArray();
                var world = new World { A = v[0], D = v[1], B = v[2], E = v[3], C = v[4], F = v[5] };
                world.Validate();
                return world;
            }

            internal void Write(string path)
            {
                File.WriteAllLines(path, new[] { A, D, B, E, C, F }
                    .Select(v => v.ToString("R", CultureInfo.InvariantCulture)).ToArray());
            }
        }

        [CommandMethod("CE_TOOLS", "CE_GEOIMAGETILES", CommandFlags.Modal | CommandFlags.Redraw)]
        public void CreateAndInsertTiles()
        {
            Run(true);
        }

        [CommandMethod("CE_TOOLS", "CE_GEOIMAGEINSERT", CommandFlags.Modal | CommandFlags.Redraw)]
        public void InsertExistingGeoreferencedJpegs()
        {
            Run(false);
        }

        [CommandMethod("CE_TOOLS", "CE_GEOIMAGECALIBRATE", CommandFlags.Modal | CommandFlags.Redraw)]
        public void CalibrateFromSurveyedCorners()
        {
            Document doc = AcApplication.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Calibrate Existing JPEG from Drawing Control Points",
                "For an already saved, licensed, approximately nadir-view JPEG: pick its actual "
                + "upper-left, upper-right and lower-left image corner locations in your drawing. "
                + "This writes a JGW and inserts the raster. Accuracy depends entirely on the control points "
                + "and whether the image is orthorectified. Do not use oblique Google Earth screenshots as survey data.");
            settings.AddChoice("Confirm", "01 Source and survey", "Known control-point coordinates", "Not confirmed",
                "Only continue with three correctly identified image corners corresponding to accurately located drawing coordinates. Drawing coordinates and image control points must use the SAME projected CRS and units.",
                new[] { "Not confirmed", "Confirmed accurate drawing corner control points" });
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;
            if (settings.Text("Confirm") != "Confirmed accurate drawing corner control points")
            {
                doc.Editor.WriteMessage("\nCE_GEOIMAGECALIBRATE: no calibration performed until three accurately known points are confirmed.");
                return;
            }
            string file;
            using (var picker = new OpenFileDialog
            {
                Title = "Choose a licensed JPEG to calibrate",
                Filter = "JPEG imagery (*.jpg;*.jpeg)|*.jpg;*.jpeg",
                CheckFileExists = true
            })
            {
                if (picker.ShowDialog() != DialogResult.OK) return;
                file = picker.FileName;
            }
            try
            {
                string worldPath = Path.Combine(Path.GetDirectoryName(file),
                    Path.GetFileNameWithoutExtension(file) + ".jgw");
                if (File.Exists(worldPath) || File.Exists(Path.ChangeExtension(file, ".jpgw")))
                    throw new IOException("World file already exists. Back it up or use CE_GEOIMAGEINSERT to attach the correctly referenced image.");
                int width, height;
                using (Image image = Image.FromFile(file))
                {
                    width = image.Width;
                    height = image.Height;
                }
                PromptPointResult upperLeft = doc.Editor.GetPoint(
                    "\nPick surveyed DWG coordinate of the image's OUTER upper-left corner: ");
                if (upperLeft.Status != PromptStatus.OK) return;
                PromptPointResult upperRight = doc.Editor.GetPoint(
                    "\nPick surveyed DWG coordinate of the image's OUTER upper-right corner: ");
                if (upperRight.Status != PromptStatus.OK) return;
                PromptPointResult lowerLeft = doc.Editor.GetPoint(
                    "\nPick surveyed DWG coordinate of the image's OUTER lower-left corner: ");
                if (lowerLeft.Status != PromptStatus.OK) return;
                // Editor.GetPoint is in UCS. Convert to WCS for world files.
                var transform = doc.Editor.CurrentUserCoordinateSystem;
                Point3d ul = upperLeft.Value.TransformBy(transform);
                Point3d ur = upperRight.Value.TransformBy(transform);
                Point3d ll = lowerLeft.Value.TransformBy(transform);
                var world = new World {
                    A = (ur.X - ul.X) / width,
                    D = (ur.Y - ul.Y) / width,
                    B = (ll.X - ul.X) / height,
                    E = (ll.Y - ul.Y) / height
                };
                world.C = ul.X + 0.5 * (world.A + world.B);
                world.F = ul.Y + 0.5 * (world.D + world.E);
                world.Validate();
                if (Math.Abs(world.A * world.B + world.D * world.E) /
                    (Math.Sqrt(world.A * world.A + world.D * world.D) *
                     Math.Sqrt(world.B * world.B + world.E * world.E)) > 0.000001)
                    throw new InvalidDataException("The selected image corners are not perpendicular in the DWG. An affine image shear cannot be represented by a regular AutoCAD raster. Rectify it in GIS before using it.");
                world.Write(worldPath);
                doc.Editor.WriteMessage(
                    "\nCE_GEOIMAGECALIBRATE: JGW saved at {0}. Verify remaining control points and source licensing. Calibration alone does not make non-orthorectified imagery survey accurate.",
                    worldPath);
                InsertAll(doc, new[] { file }, false);
            }
            catch (System.Exception error)
            {
                doc.Editor.WriteMessage("\nCE_GEOIMAGECALIBRATE: " + error.Message);
            }
        }

        private static void Run(bool withTiling)
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            var editor = document.Editor;
            var settings = new ProductionSettingsDialogModel(
                withTiling ? "CE Tools - High Quality Georeferenced JPEG Tiles"
                           : "CE Tools - Insert Georeferenced JPEG Images",
                "Work with licensed JPEG imagery and associated JGW world files. "
              + "The world-file coordinates MUST already match the Civil 3D drawing's projected CRS and drawing units. "
              + "No Google Earth tile downloads are performed.");
            if (withTiling)
            {
                settings.AddChoice("Operation", "01 Imagery", "Operation", "Split into tiles and insert",
                    "Split an existing georeferenced source JPEG or insert existing georeferenced JPEG tiles.",
                    new[] { "Split into tiles and insert", "Insert existing JPEG tiles" });
                settings.AddChoice("TileSize", "02 Quality", "Tile dimensions (pixels)", "2048",
                    "Individual JPEG tile width and height in pixels; edges are smaller and retain exact coordinates.",
                    new[] { "1024", "2048", "4096" });
                settings.AddChoice("Quality", "02 Quality", "JPEG encoding quality", "95",
                    "Higher quality preserves detail but uses more disk space. JPEG is lossy; the source resolution cannot be improved.",
                    new[] { "90", "95", "100" });
            }
            settings.AddChoice("CRS", "03 Georeferencing", "Source and DWG coordinate systems", "Not confirmed",
                "A .jgw contains coordinates and rotation but NO CRS. Both source and DWG must be in the same projected CRS and units. Reproject in GIS first if needed.",
                new[] { "Not confirmed", "Confirmed same projected CRS and drawing units" });
            settings.AddChoice("Zoom", "04 Drawing", "After insertion", "Keep current view",
                "Leave the current view unchanged or zoom to the inserted image extents.",
                new[] { "Keep current view", "Zoom to images" });
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;
            if (settings.Text("CRS") != "Confirmed same projected CRS and drawing units")
            {
                editor.WriteMessage("\nCE_GEOIMAGETILES: stopped. Confirm that world-file coordinates use the exact drawing projected CRS and units. A JGW does not transform geographic coordinates.");
                return;
            }

            bool split = withTiling && settings.Text("Operation") == "Split into tiles and insert";
            var images = new List<string>();
            try
            {
                using (var picker = new OpenFileDialog
                {
                    Title = split ? "Select ONE licensed georeferenced source JPEG" : "Select georeferenced JPEGs and tiles",
                    Filter = "JPEG imagery (*.jpg;*.jpeg)|*.jpg;*.jpeg",
                    Multiselect = !split,
                    CheckFileExists = true
                })
                {
                    if (picker.ShowDialog() != DialogResult.OK) return;
                    images.AddRange(picker.FileNames);
                }
                if (images.Count == 0) return;
                foreach (string path in images)
                {
                    if (!File.Exists(GetWorldFile(path)))
                        throw new FileNotFoundException("Missing .jgw/.jpgw world file for " + Path.GetFileName(path));
                    World.Read(GetWorldFile(path)); // validate BEFORE editing drawing or writing tiles.
                }
                if (split)
                {
                    string root = Path.GetDirectoryName(images[0]);
                    string folder = Path.Combine(root, Path.GetFileNameWithoutExtension(images[0]) + "_CE_TILES");
                    if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
                        throw new IOException("Output directory already exists and is not empty: " + folder
                            + ". Rename or move it to avoid overwriting existing georeferenced tiles.");
                    Directory.CreateDirectory(folder);
                    images = SplitJpeg(images[0], folder,
                        int.Parse(settings.Text("TileSize"), CultureInfo.InvariantCulture),
                        long.Parse(settings.Text("Quality"), CultureInfo.InvariantCulture));
                }
                InsertAll(document, images, settings.Text("Zoom") == "Zoom to images");
            }
            catch (System.Exception error)
            {
                editor.WriteMessage("\nCE_GEOIMAGETILES: " + error.Message);
            }
        }

        private static string GetWorldFile(string imagePath)
        {
            string dir = Path.GetDirectoryName(imagePath);
            string stem = Path.GetFileNameWithoutExtension(imagePath);
            string preferred = Path.Combine(dir, stem + ".jgw");
            if (File.Exists(preferred)) return preferred;
            string alternate = Path.Combine(dir, stem + ".jpgw");
            if (File.Exists(alternate)) return alternate;
            // Windows paths are case-insensitive, but an external source may
            // supply uppercase extensions; search exact basename as fallback.
            return Directory.EnumerateFiles(dir)
                .FirstOrDefault(x => string.Equals(Path.GetFileNameWithoutExtension(x), stem,
                    StringComparison.OrdinalIgnoreCase)
                    && (string.Equals(Path.GetExtension(x), ".jgw", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(Path.GetExtension(x), ".jpgw", StringComparison.OrdinalIgnoreCase)))
                ?? preferred;
        }

        private static List<string> SplitJpeg(string source, string folder, int tileSize, long quality)
        {
            var outputs = new List<string>();
            World world = World.Read(GetWorldFile(source));
            using (Image image = Image.FromFile(source))
            {
                long pixels = (long)image.Width * image.Height;
                int cols = (image.Width + tileSize - 1) / tileSize;
                int rows = (image.Height + tileSize - 1) / tileSize;
                if (pixels > MaximumPixels || (long)cols * rows > MaximumTiles)
                    throw new InvalidOperationException("Image exceeds safe in-process limits. Pre-tile the imagery in GIS to avoid exhausting Civil 3D memory.");
                ImageCodecInfo jpg = ImageCodecInfo.GetImageEncoders()
                    .First(x => x.MimeType == "image/jpeg");
                using (var parameters = new EncoderParameters(1))
                {
                    parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
                    for (int row = 0; row < rows; row++)
                    for (int col = 0; col < cols; col++)
                    {
                        int x = col * tileSize, y = row * tileSize;
                        int width = Math.Min(tileSize, image.Width - x);
                        int height = Math.Min(tileSize, image.Height - y);
                        string name = string.Format(CultureInfo.InvariantCulture,
                            "{0}_R{1:000}_C{2:000}", Path.GetFileNameWithoutExtension(source), row + 1, col + 1);
                        string jpgFile = Path.Combine(folder, name + ".jpg");
                        using (var tile = new Bitmap(width, height, PixelFormat.Format24bppRgb))
                        using (var graphics = Graphics.FromImage(tile))
                        {
                            graphics.DrawImage(image,
                                new Rectangle(0, 0, width, height),
                                new Rectangle(x, y, width, height), GraphicsUnit.Pixel);
                            tile.Save(jpgFile, jpg, parameters);
                        }
                        // For a crop beginning at source pixel (x,y), the tile's
                        // pixel (0,0) is precisely that pixel of the original.
                        world.Offset(x, y).Write(Path.Combine(folder, name + ".jgw"));
                        outputs.Add(jpgFile);
                    }
                }
            }
            return outputs;
        }

        internal static void InsertAll(Document document, IList<string> images, bool zoom)
        {
            if (images.Count > MaximumTiles)
                throw new InvalidOperationException("Maximum " + MaximumTiles + " tiles per insertion. Divide large sets into batches.");
            Database db = document.Database;
            var limits = new List<Point3d>();
            int inserted = 0;
            using (DocumentLock docLock = document.LockDocument())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                ObjectId imgDictId = RasterImageDef.GetImageDictionary(db);
                if (imgDictId.IsNull) imgDictId = RasterImageDef.CreateImageDictionary(db);
                DBDictionary dictionary = (DBDictionary)tr.GetObject(imgDictId, OpenMode.ForWrite);
                BlockTableRecord model = (BlockTableRecord)tr.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);
                ObjectId layerId = EnsureLayer(db, tr);
                foreach (string file in images)
                {
                    World world = World.Read(GetWorldFile(file));
                    int width, height;
                    using (Image source = Image.FromFile(file))
                    {
                        width = source.Width;
                        height = source.Height;
                    }
                    Point3d ll = world.LowerLeft(width, height);
                    Vector3d u = world.Horizontal(width);
                    Vector3d v = world.Vertical(height);
                    if (u.Length < 0.000001 || v.Length < 0.000001)
                        throw new InvalidDataException("Invalid world-file pixel dimensions for " + Path.GetFileName(file));
                    // AutoCAD's raster orientation represents scaled/rotated
                    // rectangular axes. A sheared affine grid needs a raster
                    // reprojection/warp in GIS before it can be drawn faithfully.
                    if (Math.Abs(u.DotProduct(v)) / (u.Length * v.Length) > 0.000001)
                        throw new InvalidDataException("Image has a sheared JGW transform: " +
                            Path.GetFileName(file) + ". Reproject/rectify it in GIS to an orthogonal pixel grid first.");

                    string name = "CE-ORTHO-" + Guid.NewGuid().ToString("N");
                    var definition = new RasterImageDef { SourceFileName = file };
                    definition.Load();
                    ObjectId definitionId = dictionary.SetAt(name, definition);
                    tr.AddNewlyCreatedDBObject(definition, true);
                    using (var raster = new RasterImage())
                    {
                        raster.ImageDefId = definitionId;
                        raster.Orientation = new CoordinateSystem3d(ll, u, v);
                        raster.LayerId = layerId;
                        model.AppendEntity(raster);
                        tr.AddNewlyCreatedDBObject(raster, true);
                        RasterImage.EnableReactors(true);
                        raster.AssociateRasterDef(definition);
                    }
                    limits.Add(ll);
                    limits.Add(ll + u);
                    limits.Add(ll + v);
                    limits.Add(ll + u + v);
                    inserted++;
                }
                tr.Commit();
            }
            document.Editor.Regen();
            if (zoom && limits.Count > 0)
            {
                try
                {
                    double left = limits.Min(p => p.X), right = limits.Max(p => p.X);
                    double bottom = limits.Min(p => p.Y), top = limits.Max(p => p.Y);
                    double margin = Math.Max(Math.Max(right - left, top - bottom) * 0.03, 1.0);
                    // Zoom via the active model-space view, without changing
                    // the user's coordinate system or drawing geometry.
                    var editor = document.Editor;
                    var view = editor.GetCurrentView();
                    view.CenterPoint = new Point2d((left + right) / 2, (bottom + top) / 2);
                    view.Width = Math.Max(right - left + 2 * margin, 1.0);
                    view.Height = Math.Max(top - bottom + 2 * margin, 1.0);
                    editor.SetCurrentView(view);
                }
                catch (System.Exception error)
                {
                    document.Editor.WriteMessage("\nImage attached; automatic zoom could not complete: " + error.Message);
                }
            }
            document.Editor.WriteMessage(
                "\nCE_GEOIMAGETILES: inserted {0} linked georeferenced JPEG images on {1}. Keep JPEG and JGW files at their saved paths. Verify control points and drawing CRS before engineering use.",
                inserted, LayerName);
        }

        private static ObjectId EnsureLayer(Database database, Transaction transaction)
        {
            LayerTable table = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
            if (table.Has(LayerName)) return table[LayerName];
            table.UpgradeOpen();
            using (var layer = new LayerTableRecord { Name = LayerName })
            {
                ObjectId id = table.Add(layer);
                transaction.AddNewlyCreatedDBObject(layer, true);
                return id;
            }
        }
    }
}

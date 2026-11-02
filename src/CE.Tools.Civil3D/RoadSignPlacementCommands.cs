using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.RoadSignPlacementCommands))]
namespace CETools.Civil3D
{
    /// <summary>
    /// Linked sign-image placement and setting-out markers for selected Civil 3D
    /// road alignments. This is not an automated traffic-control compliance design.
    /// </summary>
    public sealed class RoadSignPlacementCommands
    {
        private const string AppName = "CE_ROADSIGN_GENERATED";
        private const string DefaultLayer = "CE-ROAD-SIGNS";
        [CommandMethod("CE_TOOLS", "CE_ROADSIGNS", CommandFlags.Modal | CommandFlags.Redraw | CommandFlags.UsePickSet)]
        public void PlaceSigns()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            string defaultFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "CE-TOOLS", "RoadSigns");
            var options = new ProductionSettingsDialogModel(
                "CE Tools - Road Sign Placement",
                "Place linked road-sign images and setting-out markers on selected Civil 3D alignments. Sign compliance must be reviewed.");
            options.AddText("SignFolder", "01 Sign Library", "Saved image folder", defaultFolder,
                "Choose a permanent folder containing the approved JPG or PNG signs. Images remain externally linked.");
            options.AddChoice("Sign", "01 Sign Library", "Sign image", "STOP",
                "The selected image must exist in the saved sign folder. Add other .jpg/.png signs to that folder.",
                new[] { "STOP", "STOP_4WAY", "YIELD", "SPEED_40", "CROSS_JUNCTION", "T_JUNCTION",
                    "SIDE_ROAD", "CURVE_RIGHT", "CURVE_LEFT", "TURN_RIGHT", "TURN_LEFT",
                    "ROAD_HUMP", "ROAD_DIP", "CHEVRON", "ADDITIONAL" });
            options.AddChoice("Scope", "02 Alignments", "Alignment scope", "Multiple selected",
                "Choose all drawing alignments or select several road alignments.",
                new[] { "All", "Multiple selected" });
            options.AddChoice("Position", "02 Alignments", "Sign station location", "From start and end",
                "Use start/end distances or one absolute alignment chainage.",
                new[] { "From start and end", "From start", "From end", "Specified chainage" });
            options.AddPositiveDouble("Approach", "02 Alignments", "Distance from alignment start/end (m)", 25.0,
                "Place before the end point or after the start point, measured along alignment.");
            options.AddPositiveDouble("Station", "02 Alignments", "Specified chainage (m)", 10.0,
                "Absolute Civil 3D alignment station; this is not a distance from its start.");
            options.AddChoice("Side", "03 Placement", "Side", "Left",
                "Left or right relative to increasing alignment station.",
                new[] { "Left", "Right" });
            options.AddPositiveDouble("RoadHalfWidth", "03 Placement", "Distance from centreline to road edge (m)", 3.0,
                "Measured perpendicular to road alignment.");
            options.AddPositiveDouble("Offset", "03 Placement", "Distance beyond road edge (m)", 1.0,
                "Sign offset is road half-width plus this distance.");
            options.AddPositiveDouble("Size", "03 Placement", "Sign image plotted width (drawing m)", 1.0,
                "Resulting linked raster image width.");
            options.AddPositiveDouble("Rotate", "03 Placement", "Additional rotation (degrees)", 0.01,
                "Rotation from drawing X axis. Set 0.01 for almost horizontal.");
            options.AddText("Layer", "04 Drawing", "Sign layer", DefaultLayer,
                "Existing layer is reused, or a new layer is created.");
            options.AddChoice("Existing", "04 Drawing", "Existing CE-generated signs", "Keep",
                "Remove deletes only CE-generated sign images and marker circles, not unrelated drawing entities.",
                new[] { "Keep", "Remove all CE signs" });
            options.AddChoice("Circle", "04 Drawing", "Sign-setting-out circles", "ON",
                "Draw sign setting-out markers at the sign centre.",
                new[] { "ON", "OFF" });
            options.AddPositiveDouble("Radius", "04 Drawing", "Sign circle radius (m)", 0.3,
                "Radius of the setting-out marker.");
            if (!DisciplineWorkflowDialogs.EditSettings(options)) return;

            string folder = options.Text("SignFolder");
            string sign = options.Text("Sign");
            string imagePath = new[] { ".jpg", ".jpeg", ".png" }
                .Select(ext => Path.Combine(folder, sign + ext))
                .FirstOrDefault(File.Exists);
            if (imagePath == null)
            {
                System.Windows.MessageBox.Show("Image " + sign + " was not found in " + folder +
                    ". Extract the supplied CE RoadSigns reference package into this folder and try again.",
                    "CE Tools - Missing linked sign image");
                return;
            }
            var alignments = new List<ObjectId>();
            CivilDocument civil = CivilApplication.ActiveDocument;
            if (options.Text("Scope") == "All") alignments.AddRange(civil.GetAlignmentIds().Cast<ObjectId>());
            else
            {
                var selectionOptions = new PromptSelectionOptions { MessageForAdding =
                    "\nSelect the road alignments for sign placement: " };
                var filter = new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, "AECC_ALIGNMENT") });
                PromptSelectionResult chosen = document.Editor.GetSelection(selectionOptions, filter);
                if (chosen.Status != PromptStatus.OK || chosen.Value == null) return;
                alignments.AddRange(chosen.Value.GetObjectIds());
            }
            int added = 0;
            using (DocumentLock locked = document.LockDocument())
            using (Transaction tr = document.Database.TransactionManager.StartTransaction())
            {
                Database db = document.Database;
                string layerName = options.Text("Layer");
                if (string.IsNullOrWhiteSpace(layerName)) layerName = DefaultLayer;
                ObjectId layerId = EnsureLayer(db, tr, layerName);
                BlockTableRecord space = tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite) as BlockTableRecord;
                if (space == null) return;
                EnsureRegApp(db, tr);
                if (options.Text("Existing") == "Remove all CE signs")
                {
                    foreach (ObjectId id in space)
                    {
                        Entity entity = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                        if (entity == null || entity.IsErased) continue;
                        using (ResultBuffer data = entity.GetXDataForApplication(AppName))
                        {
                            if (data == null) continue;
                        }
                        entity.UpgradeOpen();
                        entity.Erase();
                    }
                }
                ObjectId imageDefId = EnsureImageDefinition(db, tr, imagePath);
                RasterImageDef imageDef = tr.GetObject(imageDefId, OpenMode.ForRead) as RasterImageDef;
                foreach (ObjectId alignmentId in alignments.Distinct())
                {
                    CivilAlignment alignment = tr.GetObject(alignmentId, OpenMode.ForRead, false) as CivilAlignment;
                    if (alignment == null) continue;
                    double first = alignment.StartingStation, last = alignment.EndingStation;
                    double approach = options.Double("Approach", 25.0);
                    var stations = new List<double>();
                    switch (options.Text("Position"))
                    {
                        case "From start": stations.Add(first + approach); break;
                        case "From end": stations.Add(last - approach); break;
                        case "Specified chainage": stations.Add(options.Double("Station", 10.0)); break;
                        default: stations.Add(first + approach); stations.Add(last - approach); break;
                    }
                    foreach (double station in stations.Distinct())
                    {
                        if (station < first || station > last) continue;
                        double x = 0, y = 0, tx = 0, ty = 0;
                        double a = Math.Max(first, station - 0.05);
                        double b = Math.Min(last, station + 0.05);
                        if (b - a < 1e-6) continue;
                        alignment.PointLocation(station, 0.0, ref x, ref y);
                        alignment.PointLocation(a, 0.0, ref tx, ref ty);
                        double ux = 0, uy = 0;
                        alignment.PointLocation(b, 0.0, ref ux, ref uy);
                        double angle = Math.Atan2(uy - ty, ux - tx);
                        double offset = options.Double("RoadHalfWidth", 3.0) + options.Double("Offset", 1.0);
                        if (options.Text("Side") == "Right") offset *= -1;
                        var point = new Point3d(x - Math.Sin(angle) * offset,
                            y + Math.Cos(angle) * offset, 0.0);
                        var raster = new RasterImage
                        {
                            ImageDefId = imageDefId,
                            LayerId = layerId,
                            Orientation = new CoordinateSystem3d(point,
                                Vector3d.XAxis * options.Double("Size", 1.0),
                                Vector3d.YAxis * options.Double("Size", 1.0)),
                            Rotation = angle + (options.Double("Rotate", 0.01) * Math.PI / 180.0)
                        };
                        space.AppendEntity(raster);
                        tr.AddNewlyCreatedDBObject(raster, true);
                        RasterImage.EnableReactors(true);
                        raster.AssociateRasterDef(imageDef);
                        Tag(raster, sign);
                        if (options.Text("Circle") == "ON")
                        {
                            var marker = new Circle(point, Vector3d.ZAxis, options.Double("Radius", 0.3));
                            marker.SetDatabaseDefaults(db);
                            marker.LayerId = layerId;
                            space.AppendEntity(marker);
                            tr.AddNewlyCreatedDBObject(marker, true);
                            Tag(marker, sign);
                        }
                        added++;
                    }
                }
                tr.Commit();
            }
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_ROADSIGNS: {0} externally linked signs added. Source: {1}", added, imagePath);
        }

        private static void EnsureRegApp(Database database, Transaction transaction)
        {
            RegAppTable apps = transaction.GetObject(database.RegAppTableId, OpenMode.ForRead) as RegAppTable;
            if (apps.Has(AppName)) return;
            apps.UpgradeOpen();
            var record = new RegAppTableRecord { Name = AppName };
            apps.Add(record);
            transaction.AddNewlyCreatedDBObject(record, true);
        }
        private static void Tag(Entity entity, string name)
        {
            entity.XData = new ResultBuffer(new TypedValue((int)DxfCode.ExtendedDataRegAppName, AppName),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, name));
        }
        private static ObjectId EnsureLayer(Database db, Transaction tr, string name)
        {
            LayerTable table = tr.GetObject(db.LayerTableId, OpenMode.ForRead) as LayerTable;
            if (table.Has(name)) return table[name];
            table.UpgradeOpen();
            var layer = new LayerTableRecord { Name = name };
            ObjectId id = table.Add(layer);
            tr.AddNewlyCreatedDBObject(layer, true);
            return id;
        }
        private static ObjectId EnsureImageDefinition(Database db, Transaction tr, string path)
        {
            ObjectId dictionaryId = RasterImageDef.GetImageDictionary(db);
            if (dictionaryId.IsNull) dictionaryId = RasterImageDef.CreateImageDictionary(db);
            DBDictionary dictionary = tr.GetObject(dictionaryId, OpenMode.ForWrite) as DBDictionary;
            string key = "CE_SIGN_" + Path.GetFileNameWithoutExtension(path).Replace("-", "_");
            if (dictionary.Contains(key))
            {
                ObjectId existingId = dictionary.GetAt(key);
                RasterImageDef existing = tr.GetObject(existingId, OpenMode.ForRead) as RasterImageDef;
                if (existing != null && string.Equals(Path.GetFullPath(existing.SourceFileName),
                    Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) return existingId;
                key += "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            }
            var definition = new RasterImageDef { SourceFileName = path };
            definition.Load();
            ObjectId id = dictionary.SetAt(key, definition);
            tr.AddNewlyCreatedDBObject(definition, true);
            return id;
        }
    }
}

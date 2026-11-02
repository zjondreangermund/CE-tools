using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
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
            string preferenceFile = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "CE Tools", "RoadSigns.folder");
            try
            {
                if (File.Exists(preferenceFile))
                {
                    string saved = File.ReadAllText(preferenceFile).Trim();
                    if (Directory.Exists(saved)) defaultFolder = saved;
                }
            }
            catch { }
            string[] packagedNames = { "STOP", "STOP_4WAY", "YIELD", "SPEED_40",
                "CROSS_JUNCTION", "T_JUNCTION", "SIDE_ROAD", "CURVE_RIGHT", "CURVE_LEFT",
                "TURN_RIGHT", "TURN_LEFT", "ROAD_HUMP", "ROAD_DIP", "CHEVRON" };
            var availableNames = new List<string>(packagedNames);
            try
            {
                if (Directory.Exists(defaultFolder))
                    availableNames.AddRange(Directory.EnumerateFiles(defaultFolder).Where(file =>
                        new[] { ".jpg", ".jpeg", ".png" }.Contains(Path.GetExtension(file).ToLowerInvariant()))
                        .Select(Path.GetFileNameWithoutExtension));
            }
            catch { }
            availableNames = availableNames.Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            var options = new ProductionSettingsDialogModel(
                "CE Tools - Road Sign Placement",
                "Place linked road-sign images and setting-out markers on selected Civil 3D alignments. Sign compliance must be reviewed.");
            options.AddText("SignFolder", "01 Sign Library", "Saved image folder", defaultFolder,
                "Choose a permanent folder containing the approved JPG or PNG signs. Images remain externally linked.");
            options.AddChoice("Sign", "01 Sign Library", "Sign image", "STOP",
                "The selected image must exist in the saved sign folder. Add other .jpg/.png signs to that folder.",
                availableNames);
            options.AddChoice("Scope", "02 Alignments", "Alignment scope", "Multiple selected",
                "Choose all drawing alignments or select several road alignments.",
                new[] { "All", "Multiple selected" });
            options.AddChoice("Position", "02 Alignments", "Sign station location", "From start and end",
                "Use start/end distances or one absolute alignment chainage.",
                new[] { "From start and end", "From start", "From end", "Specified chainage", "Approach all alignment curves", "Stop at junctions", "Yield at junctions" });
            options.AddChoice("JunctionControl", "02 Alignments", "Junction priority arrangement", "2-way selected roads",
                "For two-way control, select minor road alignments. Four-way requires selecting every approach road.",
                new[] { "2-way selected roads", "4-way all selected approaches" });
            options.AddPositiveDouble("SharpCurveRadius", "02 Alignments", "Sharp curve threshold radius (m)", 35.0,
                "Curve radius below this value uses TURN signs; larger circular curves use CURVE signs.");
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
            options.AddText("Rotate", "03 Placement", "Additional rotation (degrees)", "0",
                "Signed angle in degrees; 0 keeps the sign aligned to the local road tangent.");
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
            options.AddChoice("Leader", "04 Drawing", "Annotative sign multileaders", "OFF",
                "ON adds a closed-filled arrowhead leader and sign-type annotation.", new[] { "OFF", "ON" });
            options.AddPositiveDouble("ArrowSize", "04 Drawing", "Leader arrowhead size (m)", 0.2,
                "Closed-filled multileader arrowhead size.");
            options.AddPositiveDouble("LeaderDistance", "04 Drawing", "Leader text offset (m)", 2.0,
                "Distance from the sign symbol to its annotation text.");
            options.AddChoice("VertexSettingOut", "04 Drawing", "Open Vertex Setting-Out after placement", "OFF",
                "Open the existing CE vertex setting-out popup after sign insertion.",
                new[] { "OFF", "ON" });
            if (!DisciplineWorkflowDialogs.EditSettings(options)) return;

            string folder = options.Text("SignFolder");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(preferenceFile));
                File.WriteAllText(preferenceFile, folder);
            }
            catch { }
            string sign = options.Text("Sign");
            string imagePath = new[] { ".jpg", ".jpeg", ".png" }
                .Select(ext => Path.Combine(folder, sign + ext))
                .FirstOrDefault(File.Exists);
            if (imagePath == null && options.Text("Position") != "Approach all alignment curves" &&
                options.Text("Position") != "Stop at junctions" && options.Text("Position") != "Yield at junctions")
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
            double rotationDegrees;
            if (!double.TryParse(options.Text("Rotate"), NumberStyles.Float,
                CultureInfo.CurrentCulture, out rotationDegrees))
            {
                System.Windows.MessageBox.Show("Enter a valid signed sign rotation in degrees.");
                return;
            }
            int added = 0;
            try
            {
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
                foreach (ObjectId alignmentId in alignments.Distinct())
                {
                    CivilAlignment alignment = tr.GetObject(alignmentId, OpenMode.ForRead, false) as CivilAlignment;
                    if (alignment == null) continue;
                    double first = alignment.StartingStation, last = alignment.EndingStation;
                    double approach = options.Double("Approach", 25.0);
                    var placements = new List<KeyValuePair<double, string>>();
                    switch (options.Text("Position"))
                    {
                        case "From start":
                            placements.Add(new KeyValuePair<double, string>(first + approach, sign)); break;
                        case "From end":
                            placements.Add(new KeyValuePair<double, string>(last - approach, sign)); break;
                        case "Specified chainage":
                            placements.Add(new KeyValuePair<double, string>(options.Double("Station", 10.0), sign)); break;
                        case "Approach all alignment curves":
                            placements.AddRange(CurveWarningPositions(alignment, approach,
                                options.Double("SharpCurveRadius", 35.0))); break;
                        case "Stop at junctions":
                        case "Yield at junctions":
                            placements.AddRange(JunctionApproaches(alignment, civil, tr, approach,
                                options.Text("Position") == "Stop at junctions" ?
                                (options.Text("JunctionControl") == "4-way all selected approaches" ? "STOP_4WAY" : "STOP") : "YIELD")); break;
                        default:
                            placements.Add(new KeyValuePair<double, string>(first + approach, sign));
                            placements.Add(new KeyValuePair<double, string>(last - approach, sign)); break;
                    }
                    foreach (var placement in placements.GroupBy(x => x.Key.ToString("0.000", CultureInfo.InvariantCulture)
                        + "|" + x.Value).Select(g => g.First()))
                    {
                        double station = placement.Key;
                        string placedSign = placement.Value;
                        string placedPath = new[] { ".jpg", ".jpeg", ".png" }
                            .Select(ext => Path.Combine(folder, placedSign + ext)).FirstOrDefault(File.Exists);
                        if (placedPath == null)
                            throw new FileNotFoundException("Required sign image is missing: " + placedSign, folder);
                        ObjectId imageDefId = EnsureImageDefinition(db, tr, placedPath);
                        RasterImageDef imageDef = tr.GetObject(imageDefId, OpenMode.ForRead) as RasterImageDef;
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
                            Rotation = angle + (rotationDegrees * Math.PI / 180.0)
                        };
                        space.AppendEntity(raster);
                        tr.AddNewlyCreatedDBObject(raster, true);
                        RasterImage.EnableReactors(true);
                        raster.AssociateRasterDef(imageDef);
                        Tag(raster, placedSign, alignment.Name, station);
                        if (options.Text("Circle") == "ON")
                        {
                            var marker = new Circle(point, Vector3d.ZAxis, options.Double("Radius", 0.3));
                            marker.SetDatabaseDefaults(db);
                            marker.LayerId = layerId;
                            space.AppendEntity(marker);
                            tr.AddNewlyCreatedDBObject(marker, true);
                            Tag(marker, placedSign, alignment.Name, station);
                        }
                        if (options.Text("Leader") == "ON")
                        {
                            double leaderDistance = options.Double("LeaderDistance", 2.0);
                            var landing = new Point3d(point.X + Math.Cos(angle) * leaderDistance,
                                point.Y + Math.Sin(angle) * leaderDistance, point.Z);
                            var leader = new MLeader();
                            leader.SetDatabaseDefaults(db);
                            leader.LayerId = layerId;
                            leader.ContentType = ContentType.MTextContent;
                            leader.LeaderLineType = LeaderType.StraightLeader;
                            leader.ArrowSymbolId = ObjectId.Null; // Standard closed-filled arrowhead
                            leader.ArrowSize = options.Double("ArrowSize", 0.2);
                            leader.EnableDogleg = false;
                            leader.Annotative = AnnotativeStates.True;
                            int groupIndex = leader.AddLeader();
                            int lineIndex = leader.AddLeaderLine(groupIndex);
                            leader.AddFirstVertex(lineIndex, point);
                            leader.AddLastVertex(lineIndex, landing);
                            leader.MText = new MText { Contents = placedSign.Replace("_", " "),
                                Location = landing, TextHeight = 0.3,
                                Annotative = AnnotativeStates.True };
                            space.AppendEntity(leader);
                            tr.AddNewlyCreatedDBObject(leader, true);
                            Tag(leader, placedSign, alignment.Name, station);
                        }
                        added++;
                    }
                }
                tr.Commit();
            }
            }
            catch (System.Exception error)
            {
                document.Editor.WriteMessage("\nCE_ROADSIGNS stopped without committing the sign batch: {0}", error.Message);
                return;
            }
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_ROADSIGNS: {0} externally linked signs added from folder: {1}", added, folder);
            if (options.Text("VertexSettingOut") == "ON")
                document.SendStringToExecute("CE_VERTEXSETTINGOUTTOOLS ", true, false, true);
        }

        // Junctions are identified by the actual intersection of centreline entities.
        // The user explicitly chooses which approach roads carry priority control;
        // no automatic major/minor road hierarchy is inferred.
        private static IEnumerable<KeyValuePair<double, string>> JunctionApproaches(
            CivilAlignment road, CivilDocument civil, Transaction transaction,
            double approachDistance, string signName)
        {
            var output = new List<KeyValuePair<double, string>>();
            foreach (ObjectId otherId in civil.GetAlignmentIds())
            {
                if (otherId == road.ObjectId) continue;
                CivilAlignment other = transaction.GetObject(otherId, OpenMode.ForRead, false) as CivilAlignment;
                if (other == null || !IsRoad(other)) continue;
                var intersections = new Point3dCollection();
                try { road.IntersectWith(other, Intersect.OnBothOperands,
                    intersections, IntPtr.Zero, IntPtr.Zero); }
                catch { continue; }
                foreach (Point3d p in intersections)
                {
                    double station = 0.0, offset = 0.0;
                    try { road.StationOffset(p.X, p.Y, ref station, ref offset); }
                    catch { continue; }
                    if (Math.Abs(offset) > 0.25) continue;
                    if (station - approachDistance >= road.StartingStation)
                        output.Add(new KeyValuePair<double, string>(station - approachDistance, signName));
                    if (station + approachDistance <= road.EndingStation)
                        output.Add(new KeyValuePair<double, string>(station + approachDistance, signName));
                }
            }
            return output;
        }
        private static bool IsRoad(CivilAlignment alignment)
        {
            string name = (alignment.Name ?? string.Empty).ToUpperInvariant();
            return name.StartsWith("RD-") || name.StartsWith("ROAD-") ||
                   name.StartsWith("RD_") || name.StartsWith("ROAD_") ||
                   name.StartsWith("RD ") || name.StartsWith("ROAD ");
        }

        private static IEnumerable<KeyValuePair<double, string>> CurveWarningPositions(
            CivilAlignment alignment, double approach, double sharpThreshold)
        {
            var result = new List<KeyValuePair<double, string>>();
            PropertyInfo entitiesProperty = alignment.GetType().GetProperty("Entities");
            IEnumerable entities = entitiesProperty == null ? null :
                entitiesProperty.GetValue(alignment, null) as IEnumerable;
            if (entities == null) return result;
            foreach (object entity in entities)
            {
                if (entity == null) continue;
                double radius = ReadNumber(entity, "Radius");
                double begin = ReadNumber(entity, "StartingStation", "StartStation");
                double finish = ReadNumber(entity, "EndingStation", "EndStation");
                if (double.IsNaN(radius) || radius <= 0 ||
                    double.IsNaN(begin) || double.IsNaN(finish) || finish <= begin) continue;
                double from = Math.Max(alignment.StartingStation, begin + 0.05);
                double to = Math.Min(alignment.EndingStation, finish - 0.05);
                if (to <= from) continue;
                double firstHeading = LocalHeading(alignment, from);
                double lastHeading = LocalHeading(alignment, to);
                double turn = Math.Atan2(Math.Sin(lastHeading - firstHeading),
                    Math.Cos(lastHeading - firstHeading));
                if (Math.Abs(turn) < 0.001) continue;
                bool left = turn > 0;
                bool sharp = radius < sharpThreshold;
                string incoming = sharp ? (left ? "TURN_LEFT" : "TURN_RIGHT") :
                    (left ? "CURVE_LEFT" : "CURVE_RIGHT");
                string opposite = sharp ? (left ? "TURN_RIGHT" : "TURN_LEFT") :
                    (left ? "CURVE_RIGHT" : "CURVE_LEFT");
                if (begin - approach >= alignment.StartingStation)
                    result.Add(new KeyValuePair<double, string>(begin - approach, incoming));
                if (finish + approach <= alignment.EndingStation)
                    result.Add(new KeyValuePair<double, string>(finish + approach, opposite));
            }
            return result;
        }
        private static double ReadNumber(object source, params string[] names)
        {
            foreach (string name in names)
            {
                try
                {
                    PropertyInfo p = source.GetType().GetProperty(name);
                    if (p == null) continue;
                    object value = p.GetValue(source, null);
                    if (value != null) return Convert.ToDouble(value, CultureInfo.InvariantCulture);
                }
                catch { }
            }
            return double.NaN;
        }
        private static double LocalHeading(CivilAlignment alignment, double station)
        {
            double before = Math.Max(alignment.StartingStation, station - 0.02);
            double after = Math.Min(alignment.EndingStation, station + 0.02);
            double x0 = 0, y0 = 0, x1 = 0, y1 = 0;
            alignment.PointLocation(before, 0.0, ref x0, ref y0);
            alignment.PointLocation(after, 0.0, ref x1, ref y1);
            return Math.Atan2(y1 - y0, x1 - x0);
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
        private static void Tag(Entity entity, string name, string alignment, double station)
        {
            entity.XData = new ResultBuffer(
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, AppName),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, name),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, alignment ?? string.Empty),
                new TypedValue((int)DxfCode.ExtendedDataReal, station));
        }
        internal sealed class SignRecord
        {
            public string Name;
            public string Alignment;
            public double Station;
            public Point3d Point;
            public string ImagePath;
        }
        internal static List<SignRecord> ReadSigns(Database database, Transaction transaction)
        {
            var found = new List<SignRecord>();
            BlockTable table = transaction.GetObject(database.BlockTableId, OpenMode.ForRead) as BlockTable;
            BlockTableRecord space = table == null ? null : transaction.GetObject(
                table[BlockTableRecord.ModelSpace], OpenMode.ForRead) as BlockTableRecord;
            if (space == null) return found;
            foreach (ObjectId id in space)
            {
                RasterImage image = transaction.GetObject(id, OpenMode.ForRead, false) as RasterImage;
                if (image == null || image.IsErased) continue;
                using (ResultBuffer data = image.GetXDataForApplication(AppName))
                {
                    if (data == null) continue;
                    TypedValue[] values = data.AsArray();
                    if (values.Length < 4) continue;
                    var sign = new SignRecord
                    {
                        Name = Convert.ToString(values[1].Value, CultureInfo.InvariantCulture),
                        Alignment = Convert.ToString(values[2].Value, CultureInfo.InvariantCulture),
                        Station = Convert.ToDouble(values[3].Value, CultureInfo.InvariantCulture),
                        Point = image.Orientation.Origin
                    };
                    RasterImageDef def = transaction.GetObject(image.ImageDefId, OpenMode.ForRead, false) as RasterImageDef;
                    sign.ImagePath = def == null ? string.Empty : def.SourceFileName;
                    found.Add(sign);
                }
            }
            return found;
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

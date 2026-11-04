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
        private static readonly Dictionary<string, double> SuggestedHeights =
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                { "DANGER", 1.80 }, { "DANGER_OTHER", 1.80 },
                { "CHEVRON", 1.35 }, { "STOP_4WAY", 1.20 }
            };

        private static double ReadRasterAspectRatio(string fileName)
        {
            // Civil 3D 2023 RasterImageDef does not expose ImageSize.
            // Read the actual PNG pixel dimensions rather than guessing a square.
            using (System.Drawing.Image sourceImage = System.Drawing.Image.FromFile(fileName))
            {
                if (sourceImage.Width <= 0 || sourceImage.Height <= 0)
                    throw new InvalidOperationException("Sign image dimensions are invalid: " + fileName);
                return (double)sourceImage.Width / sourceImage.Height;
            }
        }
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
            string[] packagedNames = { "STOP", "STOP_4WAY", "YIELD", "SPEED_40", "SPEED_60", "SPEED_80", "CROSS_JUNCTION", "T_JUNCTION", "SIDE_ROAD", "CURVE_RIGHT", "CURVE_LEFT", "TURN_RIGHT", "TURN_LEFT", "ROAD_HUMP", "ROAD_DIP", "CHEVRON", "DANGER", "DANGER_OTHER" };
            var availableNames = new List<string>(packagedNames);
            try
            {
                if (Directory.Exists(defaultFolder))
                    availableNames.AddRange(Directory.EnumerateFiles(defaultFolder).Where(file =>
                        new[] { ".jpg", ".jpeg", ".png", ".svg" }.Contains(Path.GetExtension(file).ToLowerInvariant()))
                        .Select(file => StripRasterResolution(Path.GetFileNameWithoutExtension(file))));
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
                new[] { "From start and end", "From start", "From end", "Specified chainage", "Approach all alignment curves", "Stop at junctions", "Yield at junctions", "From crossing alignments" });
            options.AddChoice("JunctionControl", "02 Alignments", "STOP junction approach selection", "Crossing - all 4 directions",
                "T-junction single direction: only the terminating road. Crossing 2 directions: select exactly one controlled road alignment.",
                new[] { "T-junction - all 3 directions", "T-junction - 1 direction",
                    "Crossing - all 4 directions", "Crossing - 2 directions" });
            options.AddPositiveDouble("SharpCurveRadius", "02 Alignments", "Sharp curve threshold radius (m)", 35.0,
                "Curve radius below this value uses TURN signs; larger circular curves use CURVE signs.");
            options.AddPositiveDouble("Approach", "02 Alignments", "Distance from alignment start/end (m)", 25.0,
                "Place before the end point or after the start point, measured along alignment.");
            options.AddPositiveDouble("Station", "02 Alignments", "Specified chainage (m)", 10.0,
                "Absolute Civil 3D alignment station; this is not a distance from its start.");
            options.AddChoice("Side", "03 Placement", "Side", "Left",
                "Left or right of the direction of approach (not merely increasing chainage).",
                new[] { "Left", "Right" });
            options.AddPositiveDouble("RoadHalfWidth", "03 Placement", "Distance from centreline to road edge (m)", 3.0,
                "Measured perpendicular to road alignment.");
            options.AddPositiveDouble("Offset", "03 Placement", "Distance beyond road edge (m)", 1.0,
                "Sign offset is road half-width plus this distance.");
            options.AddPositiveDouble("Size", "03 Placement", "Standard sign plotted height (m)", 1.0,
                "Base image height. Aspect ratio is always preserved; W401 danger, chevron and 4-way stop use their own optional heights.");
            options.AddChoice("UseRecommendedSize", "03 Placement", "Use tall sign sizes", "ON",
                "Use configurable taller plotting height for W401/W402 danger, chevron and 4-way stop. Does not certify physical traffic sign dimensions.",
                new[] { "ON", "OFF" });
            options.AddPositiveDouble("DangerHeight", "03 Placement", "Danger marker height (m)", 1.8,
                "Preserve W401/W402 vertical proportions; set required plotted height.");
            options.AddPositiveDouble("ChevronHeight", "03 Placement", "Chevron height (m)", 1.35,
                "Preserve source chevron proportions without stretching or rotating.");
            options.AddPositiveDouble("Stop4Height", "03 Placement", "Four-way stop height (m)", 1.20,
                "Preserve source R1.4 proportions without flattening.");
            options.AddText("Rotate", "03 Placement", "Sign display rotation (degrees)", "0",
                "0 keeps the sign upright in world plan view. Optional manual angle is measured from drawing X; not tied to road direction.");
            options.AddText("Layer", "04 Drawing", "Sign layer", DefaultLayer,
                "Existing layer is reused, or a new layer is created.");
            options.AddChoice("Existing", "04 Drawing", "Existing CE-generated signs", "Keep",
                "Existing signs are removed only AFTER the replacement signs are successfully committed, and only for entities tagged CE_ROADSIGN_GENERATED.",
                new[] { "Keep", "Remove all CE signs" });
            options.AddChoice("Circle", "04 Drawing", "Sign-setting-out circles", "ON",
                "Keep circle at the true sign setting-out point even when the image is shifted above/below/left/right.",
                new[] { "ON", "OFF" });
            options.AddPositiveDouble("Radius", "04 Drawing", "Sign circle radius (m)", 0.3,
                "Radius of the setting-out marker, kept at the surveyed sign position.");
            options.AddChoice("ImagePosition", "04 Drawing", "Image position relative to circle", "Above",
                "Move only the visible sign image; the setting-out circle and exported coordinate remain unchanged.",
                new[] { "Centered", "Above", "Below", "Left", "Right" });
            options.AddPositiveDouble("ImageClearance", "04 Drawing", "Clear distance from circle to image (m)", 0.20,
                "Edge-to-edge clearance, in metres, additional to the circle radius.");
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
            string imagePath = ResolveSignImage(folder, sign);
            if (imagePath == null && options.Text("Position") != "Approach all alignment curves" &&
                options.Text("Position") != "Stop at junctions" && options.Text("Position") != "Yield at junctions" &&
                options.Text("Position") != "From crossing alignments")
            {
                System.Windows.MessageBox.Show("Image " + sign + " was not found in " + folder +
                    ". Extract the supplied CE RoadSigns reference package into this folder and try again.",
                    "CE Tools - Missing linked sign image");
                return;
            }
            var alignments = new List<ObjectId>();
            CivilDocument civil = CivilApplication.ActiveDocument;
            bool stopAtJunctions = options.Text("Position") == "Stop at junctions";
            string junctionChoice = options.Text("JunctionControl");
            if (stopAtJunctions && junctionChoice == "Crossing - 2 directions" &&
                options.Text("Scope") != "Multiple selected")
            {
                System.Windows.MessageBox.Show(
                    "For Crossing - 2 directions, select ONE controlled road alignment using Multiple selected. " +
                    "The two incoming approaches on that road will receive signs; the crossing road remains uncontrolled.",
                    "CE Tools - Choose Controlled Road");
                return;
            }
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
            if (stopAtJunctions && junctionChoice == "Crossing - 2 directions" && alignments.Distinct().Count() != 1)
            {
                System.Windows.MessageBox.Show(
                    "For Crossing - 2 directions select exactly one controlled road alignment.",
                    "CE Tools - Choose One Controlled Alignment");
                return;
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
                    var placements = new List<SignPlacement>();
                    switch (options.Text("Position"))
                    {
                        case "From start":
                            placements.Add(new SignPlacement(first + approach, sign, false)); break;
                        case "From end":
                            placements.Add(new SignPlacement(last - approach, sign, true)); break;
                        case "Specified chainage":
                            placements.Add(new SignPlacement(options.Double("Station", 10.0), sign, false)); break;
                        case "Approach all alignment curves":
                            placements.AddRange(CurveWarningPositions(alignment, approach,
                                options.Double("SharpCurveRadius", 35.0))); break;
                        case "Stop at junctions":
                        case "Yield at junctions":
                        case "From crossing alignments":
                            string selected = stopAtJunctions ?
                                (junctionChoice == "Crossing - all 4 directions" ? "STOP_4WAY" : "STOP") :
                                (options.Text("Position") == "Yield at junctions" ? "YIELD" : sign);
                            placements.AddRange(JunctionApproaches(alignment, civil, tr, approach, selected,
                                stopAtJunctions ? junctionChoice : "Any intersection"));
                            break;
                        default:
                            placements.Add(new SignPlacement(first + approach, sign, false));
                            placements.Add(new SignPlacement(last - approach, sign, true)); break;
                    }
                    // Forward and reverse approach legs are distinct and must retain their travel direction.
                    foreach (SignPlacement placement in placements.GroupBy(x =>
                        x.Station.ToString("0.000", CultureInfo.InvariantCulture) + "|" + x.Sign + "|" +
                        x.ReverseTravel.ToString()).Select(g => g.First()))
                    {
                        double station = placement.Station;
                        string placedSign = placement.Sign;
                        string placedPath = ResolveSignImage(folder, placedSign);
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
                        // Reverse the tangent for traffic approaching from decreasing chainage.
                        double travelAngle = angle + (placement.ReverseTravel ? Math.PI : 0.0);
                        double offset = options.Double("RoadHalfWidth", 3.0) + options.Double("Offset", 1.0);
                        if (options.Text("Side") == "Right") offset *= -1;
                        var point = new Point3d(x - Math.Sin(travelAngle) * offset,
                            y + Math.Cos(travelAngle) * offset, 0.0);
                        if (imageDef == null)
                            throw new InvalidOperationException("Linked sign definition unavailable.");
                        // Preserve PNG proportions, including the portrait W401 danger marker.
                        double aspectRatio = ReadRasterAspectRatio(placedPath);
                        double displayHeight = options.Double("Size", 1.0);
                        if (options.Text("UseRecommendedSize") == "ON")
                        {
                            if (string.Equals(placedSign, "DANGER", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(placedSign, "DANGER_OTHER", StringComparison.OrdinalIgnoreCase) ||
                                placedSign.StartsWith("W401", StringComparison.OrdinalIgnoreCase) ||
                                placedSign.StartsWith("W402", StringComparison.OrdinalIgnoreCase))
                                displayHeight = options.Double("DangerHeight", 1.8);
                            else if (string.Equals(placedSign, "CHEVRON", StringComparison.OrdinalIgnoreCase) ||
                                     placedSign.StartsWith("W409", StringComparison.OrdinalIgnoreCase))
                                displayHeight = options.Double("ChevronHeight", 1.35);
                            else if (string.Equals(placedSign, "STOP_4WAY", StringComparison.OrdinalIgnoreCase) ||
                                     placedSign.StartsWith("R1.4", StringComparison.OrdinalIgnoreCase))
                                displayHeight = options.Double("Stop4Height", 1.2);
                        }
                        double displayWidth = displayHeight * aspectRatio;
                        double displayAngle = rotationDegrees * Math.PI / 180.0;
                        Vector3d imageU = new Vector3d(Math.Cos(displayAngle), Math.Sin(displayAngle), 0.0) * displayWidth;
                        Vector3d imageV = new Vector3d(-Math.Sin(displayAngle), Math.Cos(displayAngle), 0.0) * displayHeight;
                        Point3d imageCenter = ShiftSignImage(point, options.Text("ImagePosition"),
                            options.Double("Radius", 0.3), options.Double("ImageClearance", 0.2),
                            displayWidth, displayHeight);
                        var imageOrigin = imageCenter - imageU * 0.5 - imageV * 0.5;
                        var raster = new RasterImage
                        {
                            ImageDefId = imageDefId,
                            LayerId = layerId,
                            Orientation = new CoordinateSystem3d(imageOrigin, imageU, imageV)
                        };
                        space.AppendEntity(raster);
                        tr.AddNewlyCreatedDBObject(raster, true);
                        RasterImage.EnableReactors(true);
                        raster.AssociateRasterDef(imageDef);
                        Tag(raster, placedSign, alignment.Name, station, point);
                        if (options.Text("Circle") == "ON")
                        {
                            var marker = new Circle(point, Vector3d.ZAxis, options.Double("Radius", 0.3));
                            marker.SetDatabaseDefaults(db);
                            marker.LayerId = layerId;
                            space.AppendEntity(marker);
                            tr.AddNewlyCreatedDBObject(marker, true);
                            Tag(marker, placedSign, alignment.Name, station, point);
                        }
                        if (options.Text("Leader") == "ON")
                        {
                            double leaderDistance = options.Double("LeaderDistance", 2.0);
                            var landing = new Point3d(point.X + Math.Cos(travelAngle) * leaderDistance,
                                point.Y + Math.Sin(travelAngle) * leaderDistance, point.Z);
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
                            Tag(leader, placedSign, alignment.Name, station, point);
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
        // The model-space circle is the physical sign setting-out point.
        // Only the visual image is displaced, with clear edge-to-edge separation.
        private static Point3d ShiftSignImage(Point3d markerPoint, string direction,
            double radius, double clearance, double width, double height)
        {
            double dx = 0.0, dy = 0.0;
            double horizontal = radius + clearance + width * 0.5;
            double vertical = radius + clearance + height * 0.5;
            switch (direction)
            {
                case "Above": dy = vertical; break;
                case "Below": dy = -vertical; break;
                case "Left": dx = -horizontal; break;
                case "Right": dx = horizontal; break;
            }
            return new Point3d(markerPoint.X + dx, markerPoint.Y + dy, markerPoint.Z);
        }

        private sealed class SignPlacement
        {
            internal readonly double Station;
            internal readonly string Sign;
            internal readonly bool ReverseTravel;
            internal SignPlacement(double station, string sign, bool reverseTravel)
            {
                Station = station;
                Sign = sign;
                ReverseTravel = reverseTravel;
            }
        }

        // An approach before an intersection travels in increasing chainage.
        // An approach after it travels in decreasing chainage, so its LEFT side
        // is the opposite side of the Civil 3D alignment's increasing tangent.
        private static IEnumerable<SignPlacement> JunctionApproaches(
            CivilAlignment road, CivilDocument civil, Transaction transaction,
            double approachDistance, string signName, string junctionMode)
        {
            var output = new List<SignPlacement>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (ObjectId otherId in civil.GetAlignmentIds())
            {
                if (otherId == road.ObjectId) continue;
                CivilAlignment other = transaction.GetObject(otherId, OpenMode.ForRead, false) as CivilAlignment;
                if (other == null || !IsRoad(other)) continue;
                var intersections = new Point3dCollection();
                try { road.IntersectWith(other, Intersect.OnBothOperands,
                    intersections, IntPtr.Zero, IntPtr.Zero); }
                catch { continue; }
                foreach (Point3d intersection in intersections)
                {
                    double station = 0.0, offset = 0.0, otherStation = 0.0, otherOffset = 0.0;
                    try
                    {
                        road.StationOffset(intersection.X, intersection.Y, ref station, ref offset);
                        other.StationOffset(intersection.X, intersection.Y, ref otherStation, ref otherOffset);
                    }
                    catch { continue; }
                    if (Math.Abs(offset) > 0.25 || Math.Abs(otherOffset) > 0.25) continue;
                    const double armTolerance = 0.5; // distinguish an ending road from a through road
                    bool roadBefore = station - road.StartingStation > armTolerance;
                    bool roadAfter = road.EndingStation - station > armTolerance;
                    bool otherBefore = otherStation - other.StartingStation > armTolerance;
                    bool otherAfter = other.EndingStation - otherStation > armTolerance;
                    int roadArms = (roadBefore ? 1 : 0) + (roadAfter ? 1 : 0);
                    int otherArms = (otherBefore ? 1 : 0) + (otherAfter ? 1 : 0);
                    int arms = roadArms + otherArms;
                    bool isT = arms == 3;
                    bool isCross = arms == 4;
                    if (junctionMode.StartsWith("T-junction", StringComparison.Ordinal) && !isT) continue;
                    if (junctionMode.StartsWith("Crossing", StringComparison.Ordinal) && !isCross) continue;
                    if (junctionMode == "T-junction - 1 direction" && roadArms != 1) continue;
                    // For two-way crossing control the caller requires exactly one
                    // selected alignment; its two approaches receive the STOP signs.
                    double before = station - approachDistance;
                    double after = station + approachDistance;
                    if (roadBefore && before >= road.StartingStation)
                    {
                        string key = before.ToString("0.000", CultureInfo.InvariantCulture) + "|F";
                        if (seen.Add(key)) output.Add(new SignPlacement(before, signName, false));
                    }
                    if (roadAfter && after <= road.EndingStation)
                    {
                        string key = after.ToString("0.000", CultureInfo.InvariantCulture) + "|R";
                        if (seen.Add(key)) output.Add(new SignPlacement(after, signName, true));
                    }
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

        private static IEnumerable<SignPlacement> CurveWarningPositions(
            CivilAlignment alignment, double approach, double sharpThreshold)
        {
            var result = new List<SignPlacement>();
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
                    result.Add(new SignPlacement(begin - approach, incoming, false));
                if (finish + approach <= alignment.EndingStation)
                    result.Add(new SignPlacement(finish + approach, opposite, true));
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

        // Match the original 18 sign codes in the user-supplied vector replacement ZIP.
        // Civil 3D raster insertion uses PNG exports; SVG is the retained vector master.
        private static readonly Dictionary<string, string> SignMasters =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "STOP", "R1 - STOP SIGN" },
                { "STOP_4WAY", "R1.4 - 4 WAY STOP SIGN" },
                { "YIELD", "R2 - YIELD SIGN" },
                { "SPEED_40", "R201 - 40 SIGN" },
                { "SPEED_60", "R201 - 60 SIGN" },
                { "SPEED_80", "R201 - 80 SIGN" },
                { "CROSS_JUNCTION", "W101 - CROSS ROAD" },
                { "T_JUNCTION", "W104 - T-JUNCTION" },
                { "SIDE_ROAD", "W110 - STAGGERED JUNCTION" },
                { "CURVE_RIGHT", "W202 - GENTLE CURVE RIGHT" },
                { "CURVE_LEFT", "W203 - GENTLE CURVE LEFT" },
                { "TURN_RIGHT", "W204 - SHARP CURVE RIGHT" },
                { "TURN_LEFT", "W205 - SHARP CURVE LEFT" },
                { "ROAD_HUMP", "W332 - SPEEDHUMP" },
                { "ROAD_DIP", "W350 - DRIFT" },
                { "CHEVRON", "W409 - T-JUNCTION CHEVRON" },
                { "DANGER", "W401 - DANGER" },
                { "DANGER_OTHER", "W402 - DANGER" }
            };
        private static string StripRasterResolution(string name)
        {
            return System.Text.RegularExpressions.Regex.Replace(name ?? string.Empty,
                @"_(?:2048|4096)px$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }
        private static string ResolveSignImage(string folder, string selected)
        {
            if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(selected) ||
                !Directory.Exists(folder)) return null;
            string original;
            string name = SignMasters.TryGetValue(selected, out original) ? original :
                StripRasterResolution(selected);
            foreach (string suffix in new[] { "_4096px.png", "_2048px.png", ".png", ".jpg", ".jpeg" })
            {
                string file = Path.Combine(folder, name + suffix);
                if (File.Exists(file)) return file;
            }
            // Missing PNG is an actionable error. Do not pass SVG to RasterImageDef.
            return null;
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
        private static void Tag(Entity entity, string name, string alignment, double station, Point3d point)
        {
            entity.XData = new ResultBuffer(
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, AppName),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, name),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, alignment ?? string.Empty),
                new TypedValue((int)DxfCode.ExtendedDataReal, station),
                new TypedValue((int)DxfCode.ExtendedDataReal, point.X),
                new TypedValue((int)DxfCode.ExtendedDataReal, point.Y));
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
                        Point = values.Length >= 6 ? new Point3d(
                            Convert.ToDouble(values[4].Value, CultureInfo.InvariantCulture),
                            Convert.ToDouble(values[5].Value, CultureInfo.InvariantCulture), 0.0) :
                            image.Orientation.Origin + image.Orientation.Xaxis * 0.5 + image.Orientation.Yaxis * 0.5
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

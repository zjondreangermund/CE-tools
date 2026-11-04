using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;

[assembly: CommandClass(typeof(CETools.Civil3D.VehicleTrackingCommands))]
namespace CETools.Civil3D
{
    /// <summary>Preliminary plan-view vehicle envelope review; not a swept-path simulator or sight-line certification.</summary>
    public sealed class VehicleTrackingCommands
    {

        private const string VehicleTrackingRegApp = "CE_VEHICLE_TRACKING";

        private static readonly string[] VehicleTrackingLayers =
        {
            "CE_VEHICLES", "CE_VEHICLE_WHEELS", "CE_WHEEL_TRACKS",
            "CE_VEHICLE_EDGE_PATHS", "CE_VEHICLE_REFERENCE_PATH",
            "CE_VEHICLE_DIRECTIONS", "CE_VEHICLE_TRACKING_MISC"
        };

        private static void EnsureVehicleTrackingLayers(Database database, Transaction tr)
        {
            LayerTable layers = (LayerTable)tr.GetObject(database.LayerTableId, OpenMode.ForRead);
            bool needsWrite = false;
            for (int i = 0; i < VehicleTrackingLayers.Length; i++)
                if (!layers.Has(VehicleTrackingLayers[i])) { needsWrite = true; break; }
            if (!needsWrite) return;
            layers.UpgradeOpen();
            short[] colors = { 2, 7, 4, 3, 8, 1, 6 };
            for (int i = 0; i < VehicleTrackingLayers.Length; i++)
            {
                string name = VehicleTrackingLayers[i];
                if (layers.Has(name)) continue;
                LayerTableRecord layer = new LayerTableRecord { Name = name, Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, colors[i]) };
                layers.Add(layer);
                tr.AddNewlyCreatedDBObject(layer, true);
            }
        }

        private static void PutOnLayer(Entity entity, string layerName)
        {
            if (entity != null && !string.IsNullOrEmpty(layerName)) entity.Layer = layerName;
        }

        private static void EnsureVehicleTrackingRegApp(Database database, Transaction tr)
        {
            RegAppTable regApps = (RegAppTable)tr.GetObject(database.RegAppTableId, OpenMode.ForRead);
            if (regApps.Has(VehicleTrackingRegApp)) return;
            regApps.UpgradeOpen();
            RegAppTableRecord record = new RegAppTableRecord { Name = VehicleTrackingRegApp };
            regApps.Add(record);
            tr.AddNewlyCreatedDBObject(record, true);
        }

        private static void TagVehicleTrackingEntity(Entity entity, string kind)
        {
            entity.XData = new ResultBuffer(
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, VehicleTrackingRegApp),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, kind ?? "Generated"));
        }

        private static int RemoveExistingVehicleTracking(Database database, Transaction tr)
        {
            int removed = 0;
            BlockTable blockTable = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
            foreach (ObjectId recordId in blockTable)
            {
                BlockTableRecord record = tr.GetObject(recordId, OpenMode.ForRead, false) as BlockTableRecord;
                if (record == null || record.IsErased) continue;
                foreach (ObjectId entityId in record)
                {
                    Entity entity = tr.GetObject(entityId, OpenMode.ForRead, false) as Entity;
                    if (entity == null || entity.IsErased) continue;
                    bool generated = false;
                    using (ResultBuffer data = entity.GetXDataForApplication(VehicleTrackingRegApp))
                    {
                        generated = data != null;
                    }
                    if (!generated) continue;
                    entity.UpgradeOpen();
                    entity.Erase();
                    removed++;
                }
            }
            return removed;
        }

        private sealed class VehiclePreset
        {
            public double Width;
            public double Track;
            public double Wheelbase;
            public double Length;
            // South African geometric-design guide minimum outer-body turning radius.
            public double OuterTurningRadius;
            // Longitudinal axle stations measured from the body centre; + is forward.
            public double[] AxleOffsets;
            // True for axles with dual tyres on each side.
            public bool[] DualTyreAxles;
        }

        private static double CalculateEquivalentSteeringAngle(
            double wheelbase, double width, double outerBodyRadius)
        {
            // Convert the published outer-body radius to an approximate
            // centreline radius, then apply the single-track bicycle model.
            // This is an equivalent model input, not a manufacturer steering-lock value.
            double centreRadius = Math.Max(0.5, outerBodyRadius - width * 0.5);
            return Math.Atan(wheelbase / centreRadius) * 180.0 / Math.PI;
        }

        private static VehiclePreset MakePreset(double width, double track, double wheelbase,
            double length, double outerRadius, double[] axleOffsets, bool[] dualTyreAxles)
        {
            return new VehiclePreset
            {
                Width = width, Track = track, Wheelbase = wheelbase, Length = length,
                OuterTurningRadius = outerRadius, AxleOffsets = axleOffsets,
                DualTyreAxles = dualTyreAxles
            };
        }

        private static VehiclePreset GetVehiclePreset(string vehicleType)
        {
            // Wheel layouts are explicit axle groups, not two axles spread across
            // the complete vehicle length. WB combinations remain plan-view
            // templates; articulated off-tracking is not calculated here.
            switch ((vehicleType ?? string.Empty).Trim())
            {
                case "TRH17 P - Passenger car":
                case "Passenger car":
                case "Light delivery vehicle":
                    return MakePreset(1.80, 1.55, 2.85, 4.80, 6.20,
                        new[] { -1.425, 1.425 }, new[] { false, false });
                case "TRH17 SU - Single unit truck":
                    // TRH17 (1988) Table 2.2.1: width 2.60 m, wheelbase 6.10 m,
                    // front/rear overhang 1.22/1.83 m. Track is a schematic estimate.
                    return MakePreset(2.60, 2.10, 6.10, 9.15, 13.38,
                        new[] { -3.05, 3.05 }, new[] { true, false });
                case "Single-unit truck":
                case "Rigid truck":
                    return MakePreset(2.50, 2.00, 6.10, 9.10, 13.38,
                        new[] { -3.05, 3.05 }, new[] { true, false });
                case "TRH17 BUS - Single unit bus":
                    // TRH17 Table 2.2.1: width 2.60 m, WB 6.00 m,
                    // front/rear overhang 2.50/3.50 m; assumed track 2.10 m.
                    return MakePreset(2.60, 2.10, 6.00, 12.00, 14.50,
                        new[] { -3.0, 3.0 }, new[] { true, false });
                case "Bus":
                    // City bus: 12.19 m body, 2.59 m wide, 7.62 m axle spacing.
                    return MakePreset(2.59, 2.10, 7.62, 12.19, 14.50,
                        new[] { -3.965, 3.655 }, new[] { true, false });
                case "Articulated truck":
                case "Semi-trailer":
                case "WB-15":
                    // WB-15 / articulated combination: steer axle, tractor drive
                    // tandem and trailer tridem. Approximate station layout is
                    // explicitly visual only pending exact selected standard sheet.
                    return MakePreset(2.59, 2.05, 4.50, 16.80, 14.08,
                        new[] { -6.35, -5.13, -3.91, 1.70, 3.05, 4.40 },
                        new[] { true, true, true, true, true, false });
                case "WB-12":
                    return MakePreset(2.44, 2.00, 4.50, 13.87, 13.38,
                        new[] { -4.95, -3.73, -2.51, 1.55, 2.90 },
                        new[] { true, true, true, true, false });
                case "WB-20":
                    // Dimensions follow the supplied TR 514 WB-20 drawing.
                    return MakePreset(2.59, 2.05, 4.57, 22.40, 13.10,
                        new[] { -9.25, -8.03, -6.81, 2.15, 3.37, 4.59 },
                        new[] { true, true, true, true, true, false });
                default:
                    return MakePreset(2.50, 2.00, 6.10, 9.10, 13.38,
                        new[] { -3.05, 3.05 }, new[] { true, false });
            }
        }

        private static double DefaultVehicleLength(string vehicleType)
        {
            return GetVehiclePreset(vehicleType).Length;
        }

        private static Point3d At(CivilAlignment a, double station)
        {
            double x = 0, y = 0;
            a.PointLocation(station, 0, ref x, ref y);
            return new Point3d(x, y, 0);
        }

        private static double NormalizeAngle(double angle)
        {
            while (angle < 0) angle += Math.PI * 2.0;
            while (angle >= Math.PI * 2.0) angle -= Math.PI * 2.0;
            return angle;
        }

        // Builds a constant-radius circular steering arc. No user-entered transition
        // length is used: radius is derived from wheelbase and maximum steering angle.
        private static List<Point3d> BuildTurningCentreline(
            CivilAlignment approach, CivilAlignment outgoing, double step,
            double wheelbase, double steeringAngleDegrees, bool left)
        {
            var results = new List<Point3d>();
            var intersections = new Point3dCollection();
            try
            {
                approach.IntersectWith(outgoing, Intersect.OnBothOperands,
                    intersections, IntPtr.Zero, IntPtr.Zero);
            }
            catch { return results; }

            if (intersections.Count != 1) return results;

            Point3d junction = intersections[0];
            double inStation = 0, inOffset = 0, outStation = 0, outOffset = 0;
            try
            {
                approach.StationOffset(junction.X, junction.Y,
                    ref inStation, ref inOffset);
                outgoing.StationOffset(junction.X, junction.Y,
                    ref outStation, ref outOffset);
            }
            catch { return results; }

            if (Math.Abs(inOffset) > 0.5 || Math.Abs(outOffset) > 0.5)
                return results;

            double probe = 0.1;
            if (inStation - probe < approach.StartingStation ||
                inStation + probe > approach.EndingStation)
                return results;

            Point3d inA = At(approach, inStation - probe);
            Point3d inB = At(approach, inStation + probe);
            Vector2d incoming = new Vector2d(
                inB.X - inA.X, inB.Y - inA.Y);
            if (incoming.Length <= 1e-9) return results;
            incoming = incoming.GetNormal();

            Point3d outA = At(outgoing, Math.Max(
                outgoing.StartingStation, outStation - probe));
            Point3d outB = At(outgoing, Math.Min(
                outgoing.EndingStation, outStation + probe));
            Vector2d outgoingIncreasing = new Vector2d(
                outB.X - outA.X, outB.Y - outA.Y);
            if (outgoingIncreasing.Length <= 1e-9) return results;
            outgoingIncreasing = outgoingIncreasing.GetNormal();

            // Select the alignment travel direction that produces the requested turn.
            Vector2d outgoingDirection = outgoingIncreasing;
            double crossIncreasing =
                incoming.X * outgoingDirection.Y -
                incoming.Y * outgoingDirection.X;
            bool useIncreasing =
                left ? crossIncreasing > 0.0 : crossIncreasing < 0.0;

            if (!useIncreasing)
                outgoingDirection = -outgoingIncreasing;

            double cross =
                incoming.X * outgoingDirection.Y -
                incoming.Y * outgoingDirection.X;
            double dot =
                incoming.X * outgoingDirection.X +
                incoming.Y * outgoingDirection.Y;
            double turnAngle = Math.Atan2(cross, dot);
            if ((left && turnAngle <= 1e-4) ||
                (!left && turnAngle >= -1e-4))
                return results;

            // Keep requested turn direction explicit: left must have positive
            // signed heading change (CCW in drawing XY), right must be negative.
            // Do not silently accept an outgoing alignment that creates the
            // opposite turn due to station direction or alignment orientation.
            bool turnDirectionMatchesRequest = left
                ? turnAngle > 0.0
                : turnAngle < 0.0;
            if (!turnDirectionMatchesRequest)
                return results;

            double steer = Math.Max(1.0,
                Math.Min(60.0, steeringAngleDegrees)) * Math.PI / 180.0;
            double radius = wheelbase / Math.Tan(steer);
            double tangentDistance =
                radius * Math.Tan(Math.Abs(turnAngle) / 2.0);

            if (double.IsNaN(tangentDistance) ||
                double.IsInfinity(tangentDistance) ||
                tangentDistance <= 0.01)
                return results;

            double incomingAvailable = inStation - approach.StartingStation;
            double outgoingAvailable = useIncreasing
                ? outgoing.EndingStation - outStation
                : outStation - outgoing.StartingStation;

            if (tangentDistance >= incomingAvailable ||
                tangentDistance >= outgoingAvailable)
            {
                // The alignment does not provide enough tangent length for the
                // steering radius. Do not silently shorten the radius.
                return results;
            }

            Point3d tangentStart = new Point3d(
                junction.X - incoming.X * tangentDistance,
                junction.Y - incoming.Y * tangentDistance, 0);

            Point3d tangentEnd = new Point3d(
                junction.X + outgoingDirection.X * tangentDistance,
                junction.Y + outgoingDirection.Y * tangentDistance, 0);

            double turnSign = left ? 1.0 : -1.0;
            Vector2d normal = new Vector2d(
                -incoming.Y * turnSign,
                incoming.X * turnSign);

            Point2d circleCentre = new Point2d(
                tangentStart.X + normal.X * radius,
                tangentStart.Y + normal.Y * radius);

            double startAngle = Math.Atan2(
                tangentStart.Y - circleCentre.Y,
                tangentStart.X - circleCentre.X);
            double endAngle = Math.Atan2(
                tangentEnd.Y - circleCentre.Y,
                tangentEnd.X - circleCentre.X);

            double sweep = left
                ? NormalizeAngle(endAngle - startAngle)
                : -NormalizeAngle(startAngle - endAngle);

            // Reject geometry that does not close on the expected tangent point.
            double radialError = Math.Abs(
                Math.Sqrt(
                    Math.Pow(tangentEnd.X - circleCentre.X, 2) +
                    Math.Pow(tangentEnd.Y - circleCentre.Y, 2)) - radius);

            if (radialError > Math.Max(0.05, radius * 0.01) ||
                Math.Abs(sweep) < 1e-4 ||
                Math.Abs(sweep) > Math.PI + 1e-3)
                return results;

            double startApproachStation =
                inStation - tangentDistance;
            double finishOutgoingStation = useIncreasing
                ? outStation + tangentDistance
                : outStation - tangentDistance;

            for (double station = approach.StartingStation;
                 station < startApproachStation - 1e-6;
                 station += step)
                results.Add(At(approach, station));

            results.Add(tangentStart);

            double arcLength = Math.Abs(sweep) * radius;
            int arcSegments = Math.Max(2,
                (int)Math.Ceiling(arcLength / Math.Max(0.25, step)));

            for (int i = 1; i <= arcSegments; i++)
            {
                double angle = startAngle +
                    sweep * ((double)i / arcSegments);
                results.Add(new Point3d(
                    circleCentre.X + radius * Math.Cos(angle),
                    circleCentre.Y + radius * Math.Sin(angle), 0));
            }

            if (useIncreasing)
            {
                for (double station = finishOutgoingStation + step;
                     station < outgoing.EndingStation;
                     station += step)
                    results.Add(At(outgoing, station));
                results.Add(At(outgoing, outgoing.EndingStation));
            }
            else
            {
                for (double station = finishOutgoingStation - step;
                     station > outgoing.StartingStation;
                     station -= step)
                    results.Add(At(outgoing, station));
                results.Add(At(outgoing, outgoing.StartingStation));
            }

            return results;
        }

        private static void AddPlanRectangle(
            Database database, Transaction tr, BlockTableRecord space,
            Point3d centre, double heading, double length, double width,
            short colorIndex, string layerName)
        {
            double hx = length * 0.5;
            double hy = width * 0.5;
            double c = Math.Cos(heading);
            double s = Math.Sin(heading);

            Point2d[] local =
            {
                new Point2d(-hx, -hy),
                new Point2d( hx, -hy),
                new Point2d( hx,  hy),
                new Point2d(-hx,  hy)
            };

            var outline = new Polyline(4);
            outline.SetDatabaseDefaults(database);
            outline.ColorIndex = colorIndex;
            PutOnLayer(outline, layerName);

            for (int i = 0; i < local.Length; i++)
            {
                double x = centre.X + local[i].X * c - local[i].Y * s;
                double y = centre.Y + local[i].X * s + local[i].Y * c;
                outline.AddVertexAt(i, new Point2d(x, y), 0, 0, 0);
            }

            outline.Closed = true;
            space.AppendEntity(outline);
            tr.AddNewlyCreatedDBObject(outline, true);
            TagVehicleTrackingEntity(outline, layerName == "CE_VEHICLE_WHEELS" ? "VehicleWheel" : "VehiclePlan");
        }

        private static double CalculatePathSteeringAngle(
            IList<Point3d> centres, IList<double> headings, int index, double wheelbase)
        {
            if (centres == null || headings == null || centres.Count < 3 ||
                headings.Count != centres.Count || wheelbase <= 0.0)
                return 0.0;

            int before = Math.Max(0, index - 1);
            int after = Math.Min(centres.Count - 1, index + 1);
            if (before == after) return 0.0;

            double dx = centres[after].X - centres[before].X;
            double dy = centres[after].Y - centres[before].Y;
            double distance = Math.Sqrt(dx * dx + dy * dy);
            if (distance < 1e-6) return 0.0;

            // Signed heading change per unit travel approximates path curvature.
            double headingChange = headings[after] - headings[before];
            while (headingChange > Math.PI) headingChange -= 2.0 * Math.PI;
            while (headingChange < -Math.PI) headingChange += 2.0 * Math.PI;
            double curvature = headingChange / distance;
            // Bicycle-model steering: tan(delta) = wheelbase * curvature.
            // Limit the visual steering angle to a plausible 40 degrees.
            double angle = Math.Atan(wheelbase * curvature);
            double limit = 40.0 * Math.PI / 180.0;
            return Math.Max(-limit, Math.Min(limit, angle));
        }

        private static void AddVehiclePlanPose(
            Database database, Transaction tr, BlockTableRecord space,
            Point3d referencePoint, double heading,
            VehiclePreset preset, double bodyLength, double bodyWidth,
            double track, double signedOffset, double steeringAngleRadians)
        {
            // Positive offset is right of travel; negative is left.
            double rightX = Math.Sin(heading), rightY = -Math.Cos(heading);
            referencePoint = new Point3d(
                referencePoint.X + rightX * signedOffset,
                referencePoint.Y + rightY * signedOffset,
                referencePoint.Z);
            AddPlanRectangle(database, tr, space, referencePoint,
                heading, bodyLength, bodyWidth, 2, "CE_VEHICLES");

            double wheelLength = 0.72;
            double wheelWidth = 0.24;
            double c = Math.Cos(heading);
            double s = Math.Sin(heading);
            double halfTrack = track * 0.5;
            for (int axleIndex = 0; axleIndex < preset.AxleOffsets.Length; axleIndex++)
            {
                double axle = preset.AxleOffsets[axleIndex];
                bool dual = axleIndex < preset.DualTyreAxles.Length && preset.DualTyreAxles[axleIndex];
                foreach (double side in new[] { -1.0, 1.0 })
                {
                    int tyreCount = dual ? 2 : 1;
                    for (int tyre = 0; tyre < tyreCount; tyre++)
                    {
                        // Dual tyres are drawn as two separate tyres on each side
                        // of the axle, slightly separated across the vehicle width.
                        double lateral = side * (halfTrack + (dual ? (tyre == 0 ? -0.14 : 0.14) : 0.0));
                        Point3d wheelCentre = new Point3d(
                            referencePoint.X + axle * c - lateral * s,
                            referencePoint.Y + axle * s + lateral * c,
                            referencePoint.Z);
                        // The steering axle is the most-forward axle station. Show its
                        // tyres rotated relative to the tractor chassis so the head unit
                        // visibly steers into the bend in plan view.
                        bool steeringAxle = axleIndex == preset.AxleOffsets.Length - 1;
                        double wheelHeading = steeringAxle
                            ? heading + steeringAngleRadians
                            : heading;
                        AddPlanRectangle(database, tr, space, wheelCentre,
                            wheelHeading, wheelLength, wheelWidth, 7, "CE_VEHICLE_WHEELS");
                    }
                }
            }
            AddVehicleDirectionArrow(database, tr, space, referencePoint, heading, bodyLength, bodyWidth);
        }

        private static void AddVehicleDirectionArrow(Database database, Transaction tr,
            BlockTableRecord space, Point3d referencePoint, double heading,
            double bodyLength, double bodyWidth)
        {
            // Forward arrow is placed just inside the front body edge and rotates with heading.
            double c = Math.Cos(heading), s = Math.Sin(heading);
            double front = bodyLength * 0.5 - Math.Max(0.15, bodyLength * 0.06);
            double halfBase = Math.Min(0.38, Math.Max(0.18, bodyWidth * 0.18));
            double arrowLength = Math.Min(0.95, Math.Max(0.55, bodyLength * 0.09));
            Point2d[] local =
            {
                new Point2d(front - arrowLength, -halfBase),
                new Point2d(front - arrowLength, halfBase),
                new Point2d(front, 0.0)
            };
            Polyline arrow = new Polyline(3);
            arrow.SetDatabaseDefaults(database);
            arrow.ColorIndex = 1;
            PutOnLayer(arrow, "CE_VEHICLE_DIRECTIONS");
            for (int i = 0; i < local.Length; i++)
            {
                double x = referencePoint.X + local[i].X * c - local[i].Y * s;
                double y = referencePoint.Y + local[i].X * s + local[i].Y * c;
                arrow.AddVertexAt(i, new Point2d(x, y), 0, 0, 0);
            }
            arrow.Closed = true;
            space.AppendEntity(arrow);
            tr.AddNewlyCreatedDBObject(arrow, true);
            TagVehicleTrackingEntity(arrow, "VehicleDirectionArrow");
        }

        [CommandMethod("CE_TOOLS", "CE_VEHICLETRACKING", CommandFlags.Modal | CommandFlags.Redraw)]
        public void Run()
        {
            Document doc = AcApplication.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var model = new ProductionSettingsDialogModel("CE Tools - Vehicle Tracking",
                "Preliminary plan envelope and review settings. Turning, 3D clearance and sight-distance checks require detailed design validation.");
            model.AddChoice("Vehicle", "01 Vehicle", "Vehicle type", "Passenger car",
                "Select a South African design-vehicle template. Axle and tyre layouts are shown individually; verify the chosen standard edition before final design use.",
                new[] { "TRH17 P - Passenger car", "TRH17 SU - Single unit truck", "TRH17 BUS - Single unit bus", "Passenger car", "Light delivery vehicle", "Single-unit truck", "Rigid truck", "Articulated truck", "Semi-trailer", "WB-12", "WB-15", "WB-20", "Bus", "Custom" });
            model.OnChoiceChanged("Vehicle", selected =>
            {
                if (string.Equals(selected, "Custom", StringComparison.OrdinalIgnoreCase))
                    return new Dictionary<string, string>();
                VehiclePreset dimensions = GetVehiclePreset(selected);
                Func<double, string> fmt = value => value.ToString("0.###", CultureInfo.InvariantCulture);
                return new Dictionary<string, string>
                {
                    { "Width", fmt(dimensions.Width) },
                    { "Track", fmt(dimensions.Track) },
                    { "Wheelbase", fmt(dimensions.Wheelbase) },
                    { "VehicleLength", fmt(dimensions.Length) },
                    { "SteeringAngle", fmt(CalculateEquivalentSteeringAngle(
                        dimensions.Wheelbase, dimensions.Width, dimensions.OuterTurningRadius)) }
                };
            });
            model.AddPositiveDouble("Width", "01 Vehicle", "Vehicle overall width (m)", 1.8, "Automatically updated from the selected vehicle type; editable for Custom.");
            model.AddPositiveDouble("Track", "01 Vehicle", "Wheel centre-to-centre track (m)", 1.55, "Automatically updated from the selected vehicle type; editable for Custom.");
            model.AddPositiveDouble("Wheelbase", "01 Vehicle", "Wheelbase (m)", 2.8, "Automatically updated from the selected vehicle type; used to calculate the steering radius.");
            model.AddPositiveDouble("VehicleLength", "01 Vehicle", "Overall vehicle length (m)", 4.8, "Automatically updated from the selected vehicle type; editable for Custom.");
            model.AddPositiveDouble("SteeringAngle", "01 Vehicle", "Equivalent front-wheel steering angle (degrees)", 28.0, "Automatically recalculated from the South African design-guide minimum outer-body turning radius and vehicle geometry. This is an equivalent single-track model angle, not a published SANRAL steering-lock value.");
            model.AddPositiveDouble("Spacing", "02 Path", "Sample interval (m)", 2.0, "Distance between generated path points.");
            model.AddChoice("Maneuver", "02 Path", "Movement", "Follow selected path",
                "For junction turns, select the completed vehicle centreline path through the junction.",
                new[] { "Follow selected path", "T-junction left", "T-junction right", "Cross-junction left", "Cross-junction right" });
            model.AddChoice("OffsetSide", "02 Path", "Travel offset side", "Right (+)",
                "Signed offset: negative left, positive right.",
                new[] { "Right (+)", "Left (-)" });
            model.AddPositiveDouble("Offset", "02 Path", "Travel offset magnitude (m)", 0.0, "Offsets the vehicle bodies and all tracking paths from the selected path. Negative = left; positive = right; zero = on the selected path.");
            model.AddChoice("View", "03 Display", "Display mode", "2D plan",
                "3D preview preserves source path elevations where available; not a full vehicle body model.",
                new[] { "2D plan", "3D path preview" });
            model.AddChoice("Edges", "03 Display", "Vehicle edge tracking paths", "ON", "Draw both left and right body-envelope swept paths.", new[] { "ON", "OFF" });
            model.AddChoice("ReferencePath", "03 Display", "Vehicle reference path", "ON", "Plot the sampled vehicle reference trajectory on CE_VEHICLE_REFERENCE_PATH.", new[] { "ON", "OFF" });
            model.AddChoice("WheelTrackDisplay", "03 Display", "Wheel-track curves", "All wheel tracks", "Choose to plot every tyre-centre path, only the outermost left/right tyre path for each axle group, or no wheel-track curves. Output is placed on CE_WHEEL_TRACKS.", new[] { "All wheel tracks", "Outer wheel extents only", "OFF" });
            model.AddChoice("VehiclePlan", "03 Display", "Vehicle body and wheels in plan", "ON", "Draw plan-view vehicle footprints along the movement, showing every configured axle and tyre, including dual tyres.", new[] { "ON", "OFF" });
            model.AddPositiveDouble("VehicleDisplayFrequency", "03 Display", "Vehicle display frequency (m)", 10.0, "Spacing between displayed vehicle body-and-wheel positions along the path. Tracking paths continue to use the sample interval.");
            model.AddChoice("ExistingTracking", "03 Display", "Existing vehicles and paths", "Keep existing", "Keep previous CE vehicle tracking output, or remove all previous CE-generated vehicle footprints and tracking paths before creating this run. Source alignments and polylines are never erased.", new[] { "Keep existing", "Remove existing before creating" });
            model.AddPositiveDouble("VehicleHeight", "04 Checks", "Vehicle height (m)", 4.3, "For later vertical clearance checks.");
            model.AddPositiveDouble("ClearanceAllowance", "04 Checks", "Required clearance allowance (m)", 0.3, "Safety margin above vehicle height.");
            model.AddPositiveDouble("AvailableHeight", "04 Checks", "Measured available vertical clearance (m)", 5.0, "User-entered clearance; does not sample overhead obstacles.");
            model.AddPositiveDouble("AvailableSight", "04 Checks", "Measured available sight distance (m)", 100.0, "User-entered value; no terrain obstruction scan.");
            model.AddPositiveDouble("StoppingRequired", "04 Checks", "Required stopping sight distance (m)", 85.0, "Use project-approved design criteria.");
            model.AddPositiveDouble("OvertakingRequired", "04 Checks", "Required overtaking sight distance (m)", 200.0, "Use project-approved design criteria.");
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;
            var picked = new PromptEntityOptions("\nSelect the vehicle travel path (select the lane-centre alignment/polyline for lane tracking; selecting the road centreline makes the vehicle follow the road centreline): ");
            picked.SetRejectMessage("\nSelect the intended Civil 3D alignment or AutoCAD polyline vehicle path.");
            picked.AddAllowedClass(typeof(CivilAlignment), true);
            picked.AddAllowedClass(typeof(Polyline), true);
            PromptEntityResult choice = doc.Editor.GetEntity(picked);
            if (choice.Status != PromptStatus.OK) return;
            bool turnRequested = model.Text("Maneuver") != "Follow selected path";
            ObjectId leavingRoadId = ObjectId.Null;
            if (turnRequested)
            {
                var departure = new PromptEntityOptions("\nSelect outgoing road alignment for the turning movement: ");
                departure.SetRejectMessage("\nSelect a Civil 3D road alignment.");
                departure.AddAllowedClass(typeof(CivilAlignment), true);
                PromptEntityResult second = doc.Editor.GetEntity(departure);
                if (second.Status != PromptStatus.OK || second.ObjectId == choice.ObjectId) return;
                leavingRoadId = second.ObjectId;
            }
            string selectedVehicle = model.Text("Vehicle");
            VehiclePreset preset = GetVehiclePreset(selectedVehicle);
            bool customVehicle = string.Equals(
                selectedVehicle, "Custom", StringComparison.OrdinalIgnoreCase);
            // Presets are applied to all dimensions as one consistent vehicle definition.
            // Custom uses the values manually entered in the dialog.
            double width = customVehicle ? model.Double("Width", 2.5) : preset.Width;
            double track = customVehicle ? model.Double("Track", 2.0) : preset.Track;
            double wheelbase = customVehicle ? model.Double("Wheelbase", 5.5) : preset.Wheelbase;
            double vehicleLength = customVehicle
                ? model.Double("VehicleLength", 10.0)
                : preset.Length;
            double steeringAngle = customVehicle
                ? model.Double("SteeringAngle", 28.0)
                : CalculateEquivalentSteeringAngle(
                    preset.Wheelbase, preset.Width, preset.OuterTurningRadius);
            doc.Editor.WriteMessage(
                "\\nVehicle template: {0}; applied overall length: {1:0.##} m.",
                selectedVehicle, vehicleLength);
            double interval = model.Double("Spacing", 2.0);
            double offsetMagnitude;
            if (!ProductionSettingsDialogModel.TryDouble(model.Text("Offset"), out offsetMagnitude) ||
                offsetMagnitude < 0.0)
                offsetMagnitude = 0.0;
            double signedOffset = offsetMagnitude *
                (model.Text("OffsetSide") == "Left (-)" ? -1.0 : 1.0);
            if (track > width || width <= 0 || interval <= 0 ||
                wheelbase <= 0 || vehicleLength < wheelbase ||
                steeringAngle <= 0 || steeringAngle >= 60)
            {
                System.Windows.MessageBox.Show("Wheel track must be no wider than the vehicle body; overall length must be at least the wheelbase; steering angle must be between 0 and 60 degrees; all dimensions must be positive.");
                return;
            }
            int samples = 0;
            int removedExisting = 0;
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                EnsureVehicleTrackingRegApp(doc.Database, tr);
                EnsureVehicleTrackingLayers(doc.Database, tr);
                if (model.Text("ExistingTracking") == "Remove existing before creating")
                    removedExisting = RemoveExistingVehicleTracking(doc.Database, tr);
                Entity path = tr.GetObject(choice.ObjectId, OpenMode.ForRead) as Entity;
                var alignment = path as CivilAlignment;
                var polyline = path as Polyline;
                if (alignment == null && polyline == null) return;
                List<Point3d> turningPath = null;
                if (turnRequested)
                {
                    CivilAlignment exitRoad = tr.GetObject(leavingRoadId, OpenMode.ForRead, false) as CivilAlignment;
                    if (alignment == null || exitRoad == null)
                    {
                        doc.Editor.WriteMessage("\nJunction turning requires two Civil 3D alignments.");
                        return;
                    }
                    turningPath = BuildTurningCentreline(
                        alignment, exitRoad, interval, wheelbase, steeringAngle,
                        model.Text("Maneuver").EndsWith("left", StringComparison.OrdinalIgnoreCase));
                    if (turningPath.Count < 3)
                    {
                        doc.Editor.WriteMessage("\nNo valid turning connection found. No tracking lines created.");
                        return;
                    }
                }
                double from = alignment != null ? alignment.StartingStation : 0.0;
                double to = alignment != null ? alignment.EndingStation : polyline.Length;
                if (to <= from) return;
                int count = turningPath == null ?
                    (int)Math.Ceiling((to - from) / interval) + 1 : turningPath.Count;
                if (count > 10000) { doc.Editor.WriteMessage("\nToo many tracking samples; increase the interval."); return; }
                // Body edge paths plus one independent trajectory for every physical tyre.
                // Wheel paths are derived from each sampled vehicle pose and the explicit
                // axle/dual-tyre layout; they are not simple parallel offsets of the road.
                var tracks = new[] { new List<Point3d>(), new List<Point3d>() };
                var wheelPathTracks = new List<List<Point3d>>();
                var wheelPathIsOuterExtent = new List<bool>();
                for (int axleIndex = 0; axleIndex < preset.AxleOffsets.Length; axleIndex++)
                {
                    bool dual = axleIndex < preset.DualTyreAxles.Length && preset.DualTyreAxles[axleIndex];
                    foreach (double side in new[] { -1.0, 1.0 })
                    {
                        int tyreCount = dual ? 2 : 1;
                        for (int tyre = 0; tyre < tyreCount; tyre++)
                        {
                            wheelPathTracks.Add(new List<Point3d>());
                            // For dual tyres, tyre index 1 is the outboard tyre on
                            // each side. A single tyre is itself the outer extent.
                            wheelPathIsOuterExtent.Add(!dual || tyre == 1);
                        }
                    }
                }
                var poseCentres = new List<Point3d>();
                var poseHeadings = new List<double>();
                for (int i = 0; i < count; i++)
                {
                    double station = Math.Min(to, from + i * interval);
                    Point3d center;
                    double tangent;
                    if (turningPath != null)
                    {
                        center = turningPath[i];
                        Point3d previous = turningPath[Math.Max(0, i - 1)];
                        Point3d following = turningPath[Math.Min(turningPath.Count - 1, i + 1)];
                        tangent = Math.Atan2(following.Y - previous.Y, following.X - previous.X);
                    }
                    else if (alignment != null)
                    {
                        double x = 0, y = 0, xb = 0, yb = 0, xa = 0, ya = 0;
                        alignment.PointLocation(station, 0, ref x, ref y);
                        double delta = Math.Min(0.05, Math.Max(0.001, (to - from) * 0.001));
                        double beforeStation = Math.Max(from, station - delta);
                        double afterStation = Math.Min(to, station + delta);
                        alignment.PointLocation(beforeStation, 0, ref xb, ref yb);
                        alignment.PointLocation(afterStation, 0, ref xa, ref ya);
                        tangent = Math.Atan2(ya - yb, xa - xb);
                        center = new Point3d(x, y, 0);
                    }
                    else
                    {
                        center = polyline.GetPointAtDist(Math.Min(polyline.Length, station));
                        Vector3d v = polyline.GetFirstDerivative(center);
                        tangent = Math.Atan2(v.Y, v.X);
                    }
                    // A signed lateral offset must never reverse the direction of travel.
                    // For a curved path, the offset curve's local forward scale is
                    // (1 + offset * curvature), where positive offset is right and
                    // negative offset is left. Reject offsets that collapse/cusp the
                    // offset curve instead of allowing it to fold back and appear to
                    // travel or turn in the opposite direction.
                    if (poseCentres.Count > 0)
                    {
                        Point3d previousCentre = poseCentres[poseCentres.Count - 1];
                        double segmentLength = center.DistanceTo(previousCentre);
                        if (segmentLength > 1e-6)
                        {
                            double headingChange = tangent - poseHeadings[poseHeadings.Count - 1];
                            while (headingChange > Math.PI) headingChange -= 2.0 * Math.PI;
                            while (headingChange < -Math.PI) headingChange += 2.0 * Math.PI;
                            double curvature = headingChange / segmentLength;
                            double offsetForwardScale = 1.0 + signedOffset * curvature;
                            if (offsetForwardScale <= 0.05)
                            {
                                string sideName = signedOffset < 0.0 ? "left" : "right";
                                doc.Editor.WriteMessage(
                                    "\\nThe {0} offset ({1:0.###} m) is too large for this curve radius. " +
                                    "It would fold/reverse the offset path. Reduce the offset; vehicle direction has not been reversed.",
                                    sideName, Math.Abs(signedOffset));
                                return;
                            }
                        }
                    }

                    poseCentres.Add(center);
                    poseHeadings.Add(tangent);
                    double nx = Math.Sin(tangent), ny = -Math.Cos(tangent);
                    tracks[0].Add(new Point3d(center.X + nx * (signedOffset + width / 2), center.Y + ny * (signedOffset + width / 2), center.Z));
                    tracks[1].Add(new Point3d(center.X + nx * (signedOffset - width / 2), center.Y + ny * (signedOffset - width / 2), center.Z));

                    // Transform every tyre centre from the vehicle's local coordinate system
                    // into the sampled world pose. This reproduces the wheel locations shown
                    // in each plan-view footprint. For articulated combinations this is still
                    // a rigid-body visual guide until separate trailer articulation is solved.
                    double cHeading = Math.Cos(tangent), sHeading = Math.Sin(tangent);
                    double halfTrack = track * 0.5;
                    int wheelPathIndex = 0;
                    for (int axleIndex = 0; axleIndex < preset.AxleOffsets.Length; axleIndex++)
                    {
                        double axleOffset = preset.AxleOffsets[axleIndex];
                        bool dual = axleIndex < preset.DualTyreAxles.Length && preset.DualTyreAxles[axleIndex];
                        foreach (double side in new[] { -1.0, 1.0 })
                        {
                            int tyreCount = dual ? 2 : 1;
                            for (int tyre = 0; tyre < tyreCount; tyre++)
                            {
                                double lateral = side * (halfTrack + (dual ? (tyre == 0 ? -0.14 : 0.14) : 0.0));
                                wheelPathTracks[wheelPathIndex++].Add(new Point3d(
                                    center.X + nx * signedOffset + axleOffset * cHeading - lateral * sHeading,
                                    center.Y + ny * signedOffset + axleOffset * sHeading + lateral * cHeading,
                                    center.Z));
                            }
                        }
                    }
                    samples++;
                }
                BlockTableRecord space = tr.GetObject(doc.Database.CurrentSpaceId, OpenMode.ForWrite) as BlockTableRecord;
                bool drawEdges = model.Text("Edges") == "ON";
                string wheelTrackDisplay = model.Text("WheelTrackDisplay");
                bool drawAllWheelTracks = wheelTrackDisplay == "All wheel tracks";
                bool drawOuterWheelExtents = wheelTrackDisplay == "Outer wheel extents only";
                if (model.Text("ReferencePath") == "ON" && poseCentres.Count > 1)
                {
                    var referenceLine = new Polyline3d(Poly3dType.SimplePoly,
                        new Point3dCollection(poseCentres.ToArray()), false);
                    referenceLine.SetDatabaseDefaults();
                    referenceLine.ColorIndex = 8;
                    PutOnLayer(referenceLine, "CE_VEHICLE_REFERENCE_PATH");
                    space.AppendEntity(referenceLine);
                    tr.AddNewlyCreatedDBObject(referenceLine, true);
                    TagVehicleTrackingEntity(referenceLine, "VehicleReferencePath");
                }
                for (int k = 0; k < 2; k++)
                {
                    if (!drawEdges) continue;
                    var line = new Polyline3d(Poly3dType.SimplePoly, new Point3dCollection(tracks[k].ToArray()), false);
                    line.SetDatabaseDefaults();
                    line.ColorIndex = 3;
                    PutOnLayer(line, "CE_VEHICLE_EDGE_PATHS");
                    space.AppendEntity(line);
                    tr.AddNewlyCreatedDBObject(line, true);
                    TagVehicleTrackingEntity(line, "VehicleEdgePath");
                }
                if (drawAllWheelTracks || drawOuterWheelExtents)
                {
                    for (int wheelIndex = 0; wheelIndex < wheelPathTracks.Count; wheelIndex++)
                    {
                        if (drawOuterWheelExtents && !wheelPathIsOuterExtent[wheelIndex])
                            continue;
                        List<Point3d> wheelPoints = wheelPathTracks[wheelIndex];
                        if (wheelPoints.Count < 2) continue;
                        var wheelLine = new Polyline3d(Poly3dType.SimplePoly,
                            new Point3dCollection(wheelPoints.ToArray()), false);
                        wheelLine.SetDatabaseDefaults();
                        // Use alternating standard ACI colours to distinguish the individual tyre paths.
                        wheelLine.ColorIndex = (short)(1 + (wheelIndex % 6));
                        PutOnLayer(wheelLine, "CE_WHEEL_TRACKS");
                        space.AppendEntity(wheelLine);
                        tr.AddNewlyCreatedDBObject(wheelLine, true);
                        TagVehicleTrackingEntity(wheelLine, "WheelTrackingPath");
                    }
                }

                // Draw ghosted plan-view vehicle positions along the movement.
                // Footprints are spaced to remain legible on long alignments.
                if (model.Text("VehiclePlan") == "ON" && poseCentres.Count > 0)
                {
                    double displayFrequency = Math.Max(
                        interval,
                        model.Double("VehicleDisplayFrequency", 10.0));
                    int poseStride = Math.Max(
                        1,
                        (int)Math.Ceiling(displayFrequency / interval));
                    for (int poseIndex = 0; poseIndex < poseCentres.Count; poseIndex += poseStride)
                    {
                        AddVehiclePlanPose(
                            doc.Database, tr, space,
                            poseCentres[poseIndex], poseHeadings[poseIndex],
                            preset, vehicleLength, width, track, signedOffset,
                            CalculatePathSteeringAngle(poseCentres, poseHeadings, poseIndex, preset.Wheelbase));
                    }

                    int lastPose = poseCentres.Count - 1;
                    if (lastPose % poseStride != 0)
                    {
                        AddVehiclePlanPose(
                            doc.Database, tr, space,
                            poseCentres[lastPose], poseHeadings[lastPose],
                            preset, vehicleLength, width, track, signedOffset,
                            CalculatePathSteeringAngle(poseCentres, poseHeadings, lastPose, preset.Wheelbase));
                    }
                }
                tr.Commit();
            }
            doc.Editor.Regen();
            double clearance = model.Double("AvailableHeight", 5) -
                model.Double("VehicleHeight", 4.3) - model.Double("ClearanceAllowance", 0.3);
            double sight = model.Double("AvailableSight", 100);
            string clearanceStatus = clearance < 0 ? "RED - below user-entered minimum" : "ORANGE - user value requires verification";
            string stopStatus = sight < model.Double("StoppingRequired", 85) ? "RED - below entered criterion" : "ORANGE - requires field and geometric review";
            string passStatus = sight < model.Double("OvertakingRequired", 200) ? "RED - below entered criterion" : "ORANGE - requires field and geometric review";
            System.Windows.MessageBox.Show(
                "Vehicle tracking PRELIMINARY only (" + samples + " samples).\n" +
                "Previous generated vehicles/paths removed: " + removedExisting + ".\n" +
                "Turn movement: " + model.Text("Maneuver") + " (follow the supplied path geometry).\n" +
                "Vertical clearance: " + clearanceStatus + "\n" +
                "Stopping sight distance: " + stopStatus + "\n" +
                "Overtaking sight distance: " + passStatus + "\n" +
                "GREEN approval is not issued without validated terrain/obstacle sightlines and vehicle dynamics.\n" +
                "Additional wheel-path curves are plotted separately for every configured tyre centre, including dual tyres. IMPORTANT: in this build, multi-unit vehicle bodies and wheel paths still use a shared rigid-body pose; articulated trailer off-tracking is not implemented and these results must not be used as a true WB-12/WB-15/WB-20 swept-path analysis. Axle stations remain preliminary visual templates pending confirmation against the selected standard edition.",
                "CE Tools - Vehicle Tracking Review");
        }
    }
}
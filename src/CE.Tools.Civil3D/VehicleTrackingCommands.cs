using System;
using System.Collections.Generic;
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
        private static Point3d At(CivilAlignment a, double station)
        {
            double x = 0, y = 0;
            a.PointLocation(station, 0, ref x, ref y);
            return new Point3d(x, y, 0);
        }

        private static List<Point3d> BuildTurningCentreline(CivilAlignment approach,
            CivilAlignment outgoing, double step, double length, bool left)
        {
            var results = new List<Point3d>();
            var intersections = new Point3dCollection();
            try { approach.IntersectWith(outgoing, Intersect.OnBothOperands, intersections,
                IntPtr.Zero, IntPtr.Zero); }
            catch { return results; }
            if (intersections.Count != 1) return results; // avoid ambiguous junctions
            Point3d junction = intersections[0];
            double incomingStation = 0, incomingOffset = 0, outgoingStation = 0, outgoingOffset = 0;
            try
            {
                approach.StationOffset(junction.X, junction.Y, ref incomingStation, ref incomingOffset);
                outgoing.StationOffset(junction.X, junction.Y, ref outgoingStation, ref outgoingOffset);
            }
            catch { return results; }
            if (Math.Abs(incomingOffset) > 0.5 || Math.Abs(outgoingOffset) > 0.5) return results;
            double blend = Math.Min(length, incomingStation - approach.StartingStation);
            if (blend <= step || incomingStation >= approach.EndingStation) return results;
            Point3d initial = At(approach, incomingStation - blend);
            Point3d j0 = At(approach, incomingStation - Math.Min(0.05, blend / 3));
            Point3d j1 = At(approach, incomingStation);
            Point3d out0 = At(outgoing, Math.Max(outgoing.StartingStation, outgoingStation - 0.05));
            Point3d out1 = At(outgoing, Math.Min(outgoing.EndingStation, outgoingStation + 0.05));
            Vector3d arrival = (j1 - j0).GetNormal();
            Vector3d outward = (out1 - out0).GetNormal();
            bool increasing = ((arrival.X * outward.Y - arrival.Y * outward.X) > 0) == left;
            if (!increasing) outward = -outward;
            if (Math.Abs(arrival.X * outward.Y - arrival.Y * outward.X) < 0.05) return results;
            double finishStation = outgoingStation + (increasing ? blend : -blend);
            if (finishStation < outgoing.StartingStation || finishStation > outgoing.EndingStation) return results;
            Point3d finalTurn = At(outgoing, finishStation);
            for (double station = approach.StartingStation; station < incomingStation - blend; station += step)
                results.Add(At(approach, station));
            results.Add(initial);
            int segments = Math.Max(12, (int)Math.Ceiling(2 * blend / step));
            for (int i = 1; i <= segments; i++)
            {
                double t = (double)i / segments;
                results.Add(new Point3d(
                    (1 - t) * (1 - t) * initial.X + 2 * t * (1 - t) * junction.X + t * t * finalTurn.X,
                    (1 - t) * (1 - t) * initial.Y + 2 * t * (1 - t) * junction.Y + t * t * finalTurn.Y, 0));
            }
            if (increasing)
            {
                for (double station = finishStation + step; station < outgoing.EndingStation; station += step)
                    results.Add(At(outgoing, station));
                results.Add(At(outgoing, outgoing.EndingStation));
            }
            else
            {
                for (double station = finishStation - step; station > outgoing.StartingStation; station -= step)
                    results.Add(At(outgoing, station));
                results.Add(At(outgoing, outgoing.StartingStation));
            }
            return results;
        }

        [CommandMethod("CE_TOOLS", "CE_VEHICLETRACKING", CommandFlags.Modal | CommandFlags.Redraw)]
        public void Run()
        {
            Document doc = AcApplication.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var model = new ProductionSettingsDialogModel("CE Tools - Vehicle Tracking",
                "Preliminary plan envelope and review settings. Turning, 3D clearance and sight-distance checks require detailed design validation.");
            model.AddChoice("Vehicle", "01 Vehicle", "Vehicle type", "Passenger car",
                "Vehicle dimensions below are editable; no certified vehicle templates are assumed.",
                new[] { "Passenger car", "Rigid truck", "Articulated truck", "Bus", "Custom" });
            model.AddPositiveDouble("Width", "01 Vehicle", "Vehicle overall width (m)", 2.5, "Measured body width.");
            model.AddPositiveDouble("Track", "01 Vehicle", "Wheel centre-to-centre track (m)", 2.0, "Wheel path spacing.");
            model.AddPositiveDouble("Wheelbase", "01 Vehicle", "Wheelbase (m)", 6.0, "Recorded for later swept-path simulation; this preview does not model off-tracking.");
            model.AddPositiveDouble("Spacing", "02 Path", "Sample interval (m)", 2.0, "Distance between generated path points.");
            model.AddChoice("Maneuver", "02 Path", "Movement", "Follow selected path",
                "For junction turns, select the completed vehicle centreline path through the junction.",
                new[] { "Follow selected path", "T-junction left", "T-junction right", "Cross-junction left", "Cross-junction right" });
            model.AddPositiveDouble("TurnBlend", "02 Path", "Junction turning transition length (m)", 12.0,
                "Smooth preliminary movement through the chosen intersection. Does not model actual vehicle off-tracking.");
            model.AddChoice("OffsetSide", "02 Path", "Travel offset side", "Right (+)",
                "Signed offset: negative left, positive right.",
                new[] { "Right (+)", "Left (-)" });
            model.AddPositiveDouble("Offset", "02 Path", "Offset magnitude (m)", 0.01, "Use 0.01 m for approximately centreline; positive input is signed by selected side.");
            model.AddChoice("View", "03 Display", "Display mode", "2D plan",
                "3D preview preserves source path elevations where available; not a full vehicle body model.",
                new[] { "2D plan", "3D path preview" });
            model.AddChoice("Edges", "03 Display", "Vehicle edge tracking paths", "ON", "Draw both left and right body-envelope guides.", new[] { "ON", "OFF" });
            model.AddChoice("Wheels", "03 Display", "Left/right wheel tracking paths", "ON", "Draw both wheel-centre guide paths.", new[] { "ON", "OFF" });
            model.AddPositiveDouble("VehicleHeight", "04 Checks", "Vehicle height (m)", 4.3, "For later vertical clearance checks.");
            model.AddPositiveDouble("ClearanceAllowance", "04 Checks", "Required clearance allowance (m)", 0.3, "Safety margin above vehicle height.");
            model.AddPositiveDouble("AvailableHeight", "04 Checks", "Measured available vertical clearance (m)", 5.0, "User-entered clearance; does not sample overhead obstacles.");
            model.AddPositiveDouble("AvailableSight", "04 Checks", "Measured available sight distance (m)", 100.0, "User-entered value; no terrain obstruction scan.");
            model.AddPositiveDouble("StoppingRequired", "04 Checks", "Required stopping sight distance (m)", 85.0, "Use project-approved design criteria.");
            model.AddPositiveDouble("OvertakingRequired", "04 Checks", "Required overtaking sight distance (m)", 200.0, "Use project-approved design criteria.");
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;
            var picked = new PromptEntityOptions("\nSelect centreline alignment or polyline for vehicle tracking: ");
            picked.SetRejectMessage("\nSelect a Civil 3D alignment or AutoCAD polyline.");
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
            double width = model.Double("Width", 2.5), track = model.Double("Track", 2.0);
            double interval = model.Double("Spacing", 2.0), signedOffset = model.Double("Offset", 0.01) *
                (model.Text("OffsetSide") == "Left (-)" ? -1.0 : 1.0);
            if (track > width || width <= 0 || interval <= 0)
            {
                System.Windows.MessageBox.Show("Wheel track must be no wider than the vehicle body, and dimensions must be positive.");
                return;
            }
            int samples = 0;
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
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
                    turningPath = BuildTurningCentreline(alignment, exitRoad, interval,
                        model.Double("TurnBlend", 12.0),
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
                var tracks = new[] { new List<Point3d>(), new List<Point3d>(), new List<Point3d>(), new List<Point3d>() };
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
                        double x = 0, y = 0, x1 = 0, y1 = 0;
                        alignment.PointLocation(station, 0, ref x, ref y);
                        double next = Math.Min(to, station + 0.05);
                        double prev = Math.Max(from, station - 0.05);
                        alignment.PointLocation(next, 0, ref x1, ref y1);
                        if (next == station) { alignment.PointLocation(prev, 0, ref x, ref y); tangent = Math.Atan2(y - y1, x - x1); }
                        else tangent = Math.Atan2(y1 - y, x1 - x);
                        center = new Point3d(x, y, 0);
                    }
                    else
                    {
                        center = polyline.GetPointAtDist(Math.Min(polyline.Length, station));
                        Vector3d v = polyline.GetFirstDerivative(center);
                        tangent = Math.Atan2(v.Y, v.X);
                    }
                    double nx = -Math.Sin(tangent), ny = Math.Cos(tangent);
                    double[] displacements = { signedOffset + width / 2, signedOffset - width / 2,
                                                signedOffset + track / 2, signedOffset - track / 2 };
                    for (int k = 0; k < 4; k++)
                        tracks[k].Add(new Point3d(center.X + nx * displacements[k], center.Y + ny * displacements[k], center.Z));
                    samples++;
                }
                BlockTableRecord space = tr.GetObject(doc.Database.CurrentSpaceId, OpenMode.ForWrite) as BlockTableRecord;
                bool drawEdges = model.Text("Edges") == "ON", drawWheels = model.Text("Wheels") == "ON";
                for (int k = 0; k < 4; k++)
                {
                    if (k < 2 && !drawEdges || k >= 2 && !drawWheels) continue;
                    var line = new Polyline3d(Poly3dType.SimplePoly, new Point3dCollection(tracks[k].ToArray()), false);
                    line.SetDatabaseDefaults();
                    line.ColorIndex = k < 2 ? 3 : 5;
                    space.AppendEntity(line);
                    tr.AddNewlyCreatedDBObject(line, true);
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
                "Turn movement: " + model.Text("Maneuver") + " (follow the supplied path geometry).\n" +
                "Vertical clearance: " + clearanceStatus + "\n" +
                "Stopping sight distance: " + stopStatus + "\n" +
                "Overtaking sight distance: " + passStatus + "\n" +
                "GREEN approval is not issued without validated terrain/obstacle sightlines and vehicle dynamics.\n" +
                "Wheel lines show geometric offsets, NOT axle off-tracking or tire swept paths.",
                "CE Tools - Vehicle Tracking Review");
        }
    }
}

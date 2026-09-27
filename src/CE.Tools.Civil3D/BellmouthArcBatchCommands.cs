using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil;
using Autodesk.Civil.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.BellmouthArcBatchCommands))]

namespace CETools.Civil3D
{
    public sealed class BellmouthArcBatchCommands
    {
        [CommandMethod("CE_TOOLS", "CE_BELLMOUTHARCSMULTI", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void ConvertMultipleReturns()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            var settings = new ProductionSettingsDialogModel("CE Tools - Bellmouths to True Arcs",
                "Fit a true circular polyline arc to each selected joined polyline or feature line with intermediate vertices. Non-circular or varying-elevation geometry is skipped.");
            settings.AddPositiveDouble("Tolerance", "01 Fit", "Maximum radial deviation", 0.05,
                "The largest allowable distance from an intermediate point to the fitted circle, in drawing units.");
            settings.AddChoice("Source", "02 Output", "Original geometry", "Keep originals",
                "Keep the source for review or replace it after its arc is created.",
                new[] { "Keep originals", "Replace originals" });
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;
            double tolerance = settings.Double("Tolerance", 0.05);
            bool replace = settings.Text("Source") == "Replace originals";
            PromptSelectionResult selection = document.Editor.SelectImplied();
            if (selection.Status != PromptStatus.OK || selection.Value == null || selection.Value.Count == 0)
                selection = document.Editor.GetSelection(new PromptSelectionOptions
                {
                    MessageForAdding = "\nSelect multiple bellmouth feature lines or joined polylines: ",
                    AllowDuplicates = false, RejectObjectsFromNonCurrentSpace = true
                });
            document.Editor.SetImpliedSelection(new ObjectId[0]);
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;
            int converted = 0, skipped = 0;
            foreach (ObjectId id in selection.Value.GetObjectIds().Distinct())
            {
                try
                {
                    using (Transaction tr = document.Database.TransactionManager.StartTransaction())
                    {
                        Entity source = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                        var points = new List<Point3d>();
                        Polyline poly = source as Polyline;
                        FeatureLine feature = source as FeatureLine;
                        if (poly != null && !poly.Closed)
                            for (int i = 0; i < poly.NumberOfVertices; i++) points.Add(poly.GetPoint3dAt(i));
                        else if (feature != null && !feature.Closed && !feature.IsReferenceObject)
                            foreach (Point3d point in feature.GetPoints(FeatureLinePointType.AllPoints)) points.Add(point);
                        double bulge;
                        if (points.Count < 3 || !TryFit(points, tolerance, out bulge)) { skipped++; continue; }
                        var arc = new Polyline(2);
                        arc.SetDatabaseDefaults(document.Database);
                        arc.LayerId = source.LayerId;
                        arc.Color = source.Color;
                        arc.Elevation = points[0].Z;
                        arc.AddVertexAt(0, new Point2d(points[0].X, points[0].Y), bulge, 0, 0);
                        Point3d end = points[points.Count - 1];
                        arc.AddVertexAt(1, new Point2d(end.X, end.Y), 0, 0, 0);
                        BlockTableRecord space = tr.GetObject(source.OwnerId, OpenMode.ForWrite, false) as BlockTableRecord;
                        if (space == null) { skipped++; continue; }
                        space.AppendEntity(arc);
                        tr.AddNewlyCreatedDBObject(arc, true);
                        if (replace) { source.UpgradeOpen(); source.Erase(); }
                        tr.Commit();
                        converted++;
                    }
                }
                catch { skipped++; }
            }
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_BELLMOUTHARCSMULTI complete. Circular arcs={0}; skipped={1}.", converted, skipped);
        }

        private static bool TryFit(IList<Point3d> points, double tolerance, out double bulge)
        {
            bulge = 0;
            Point3d a = points[0], m = points[points.Count / 2], b = points[points.Count - 1];
            if (points.Any(p => Math.Abs(p.Z - a.Z) > tolerance)) return false;
            double cross = (m.X - a.X) * (b.Y - a.Y) - (m.Y - a.Y) * (b.X - a.X);
            if (Math.Abs(cross) < 1e-8) return false;
            double aa = a.X * a.X + a.Y * a.Y, mm = m.X * m.X + m.Y * m.Y, bb = b.X * b.X + b.Y * b.Y;
            double cx = ((mm - aa) * (b.Y - a.Y) - (bb - aa) * (m.Y - a.Y)) / (2 * cross);
            double cy = ((bb - aa) * (m.X - a.X) - (mm - aa) * (b.X - a.X)) / (2 * cross);
            double radius = Math.Sqrt((a.X - cx) * (a.X - cx) + (a.Y - cy) * (a.Y - cy));
            if (radius < 1e-6 || points.Any(p => Math.Abs(Math.Sqrt((p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy)) - radius) > tolerance)) return false;
            double previous = Math.Atan2(a.Y - cy, a.X - cx), total = 0;
            int direction = Math.Sign(cross);
            for (int i = 1; i < points.Count; i++)
            {
                double next = Math.Atan2(points[i].Y - cy, points[i].X - cx);
                double delta = next - previous;
                while (delta <= -Math.PI) delta += 2 * Math.PI;
                while (delta > Math.PI) delta -= 2 * Math.PI;
                if (Math.Sign(delta) != direction || Math.Abs(delta) < 1e-8) return false;
                total += delta;
                previous = next;
            }
            if (Math.Abs(total) >= 2 * Math.PI - 1e-5) return false;
            bulge = Math.Tan(total / 4);
            return Math.Abs(bulge) > 1e-8;
        }
    }
}

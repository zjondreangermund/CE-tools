using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using CivilStructure = Autodesk.Civil.DatabaseServices.Structure;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.NetworkVertexAuditCommands))]
namespace CETools.Civil3D
{
    public sealed class NetworkVertexAuditCommands
    {
        [CommandMethod("CE_TOOLS", "CE_NETWORKVERTEXCHECK", CommandFlags.Modal | CommandFlags.UsePickSet)]
        public void CheckVertices()
        {
            var doc = AcApplication.DocumentManager.MdiActiveDocument;
            var civil = CivilApplication.ActiveDocument;
            if (doc == null || civil == null) return;
            var settings = new ProductionSettingsDialogModel("CE Tools - Polyline / Structure Centre Check",
                "Report every source vertex that is not at a pipe-network structure centre. This command does not move geometry.");
            settings.AddChoice("Scope", "01 Selection", "Polylines", "Multiple selected",
                "Check selected source polylines or all model-space polylines.", new[] { "Multiple selected", "All" });
            settings.AddPositiveDouble("Tolerance", "02 Distances", "Centre tolerance (drawing units)", 0.01,
                "Maximum horizontal vertex-to-structure distance accepted as centred.");
            settings.AddPositiveDouble("SearchRadius", "02 Distances", "Maximum search distance (drawing units)", 2.0,
                "Search for a nearby structure up to this distance; farther vertices are reported as no structure found.");
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;
            double tolerance = settings.Double("Tolerance", 0.01), search = settings.Double("SearchRadius", 2.0);
            if (search < tolerance)
            {
                doc.Editor.WriteMessage("\nSearch distance must be at least the centre tolerance. No checks performed."); return;
            }
            HashSet<ObjectId> selected = null;
            if (settings.Text("Scope") != "All")
            {
                var selection = doc.Editor.GetSelection(new PromptSelectionOptions { MessageForAdding = "\nSelect multiple source polylines: ", AllowDuplicates = false });
                if (selection.Status != PromptStatus.OK) return;
                selected = new HashSet<ObjectId>(selection.Value.GetObjectIds());
            }
            var problems = new HashSet<ObjectId>(); int checkedVertices = 0, offCentre = 0, missing = 0;
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var structures = new List<Tuple<Point3d, string>>();
                foreach (ObjectId networkId in civil.GetPipeNetworkIds())
                {
                    var network = tr.GetObject(networkId, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Network;
                    if (network == null) continue;
                    foreach (ObjectId id in network.GetStructureIds())
                    {
                        var structure = tr.GetObject(id, OpenMode.ForRead) as CivilStructure;
                        if (structure != null) structures.Add(Tuple.Create(structure.Position, structure.Name));
                    }
                }
                if (structures.Count == 0) { doc.Editor.WriteMessage("\nNo pipe-network structures found in this drawing."); return; }
                var model = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(doc.Database), OpenMode.ForRead);
                foreach (ObjectId id in model)
                {
                    if (selected != null && !selected.Contains(id)) continue;
                    var obj = tr.GetObject(id, OpenMode.ForRead);
                    var points = new List<Point3d>();
                    if (obj is Polyline)
                    {
                        var poly = (Polyline)obj;
                        for (int i = 0; i < poly.NumberOfVertices; i++) points.Add(poly.GetPoint3dAt(i));
                    }
                    else if (obj is Polyline3d)
                        foreach (ObjectId vertex in (Polyline3d)obj) points.Add(((PolylineVertex3d)tr.GetObject(vertex, OpenMode.ForRead)).Position);
                    else if (obj is Polyline2d)
                        foreach (ObjectId vertex in (Polyline2d)obj) points.Add(((Vertex2d)tr.GetObject(vertex, OpenMode.ForRead)).Position);
                    for (int i = 0; i < points.Count; i++)
                    {
                        checkedVertices++;
                        var point = new Point2d(points[i].X, points[i].Y);
                        var nearest = structures.Select(s => new { Name = s.Item2, Distance = point.GetDistanceTo(new Point2d(s.Item1.X, s.Item1.Y)) })
                            .OrderBy(s => s.Distance).First();
                        if (nearest.Distance <= tolerance) continue;
                        problems.Add(id);
                        if (nearest.Distance > search) missing++; else offCentre++;
                        doc.Editor.WriteMessage("\nPolyline {0}, vertex {1} ({2:0.###}, {3:0.###}): {4}", id.Handle, i + 1, point.X, point.Y,
                            nearest.Distance > search ? "no structure within search distance" : nearest.Name + ": offset " + nearest.Distance.ToString("0.###"));
                    }
                }
            }
            if (problems.Count > 0) doc.Editor.SetImpliedSelection(problems.ToArray());
            System.Windows.Forms.MessageBox.Show("Vertices checked: " + checkedVertices + "\nOff-centre vertices: " + offCentre +
                "\nNo structure in search distance: " + missing + "\nAffected polylines: " + problems.Count +
                "\nDetails are in command history; affected polylines are selected.", "CE Tools - Vertex Check");
        }
    }
}

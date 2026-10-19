using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilPipe = Autodesk.Civil.DatabaseServices.Pipe;

[assembly: CommandClass(typeof(CETools.Civil3D.SewerExcavationSurfaceCommands))]
namespace CETools.Civil3D
{
    /// <summary>
    /// Creates a separate excavation-bottom TIN from the bottom-of-trench
    /// points of Civil 3D gravity pipes. Intended for one connected pipe
    /// network at a time. Does not modify the source network, EG or FG.
    /// </summary>
    public sealed class SewerExcavationSurfaceCommands
    {
        [CommandMethod("CE_TOOLS", "CE_SEWEREXCAVATIONSURFACE",
            CommandFlags.Modal | CommandFlags.Redraw | CommandFlags.UsePickSet)]
        public void Create()
        {
            Document doc = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civil = CivilApplication.ActiveDocument;
            if (doc == null || civil == null) return;
            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Sewer Excavation Bottom Surface",
                "Create a new TIN representing trench excavation bottom, based on selected pipe centreline elevations and trench bedding assumptions. Review all TIN triangles before measuring volumes.");
            settings.AddChoice("Scope", "01 Pipe network", "Pipes",
                "Multiple selected sewer pipes",
                "Select one or more gravity-network pipes, or include all sewer pipes in the drawing.",
                new[] { "Multiple selected sewer pipes", "All sewer pipes" });
            settings.AddText("Name", "02 Surface", "New surface name",
                "CE Sewer Excavation Bottom", "Creates a NEW TIN surface. Existing surfaces will not be overwritten.");
            settings.AddPositiveDouble("Width", "03 Trench geometry",
                "Trench bottom width (m)", 0.8,
                "Width of flat trench bottom; measured perpendicular to the pipe centreline.");
            settings.AddPositiveDouble("Bedding", "03 Trench geometry",
                "Bedding below pipe outside (m)", 0.15,
                "Depth below the outside bottom of the pipe, not below the pipe centreline.");
            settings.AddPositiveDouble("Units", "03 Trench geometry",
                "Drawing units per metre", 1.0,
                "Use 1 for drawings in metres and 1000 for drawings in millimetres.");
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;
            double width = settings.Double("Width", 0.8) * settings.Double("Units", 1.0);
            double bedding = settings.Double("Bedding", 0.15) * settings.Double("Units", 1.0);
            string name = (settings.Text("Name") ?? "").Trim();
            if (name.Length == 0) { doc.Editor.WriteMessage("\nProvide a surface name."); return; }
            var ids = new List<ObjectId>();
            if (settings.Text("Scope") == "Multiple selected sewer pipes")
            {
                PromptSelectionResult selection = doc.Editor.GetSelection(new PromptSelectionOptions {
                    MessageForAdding = "\nSelect sewer pipe objects for excavation surface: ",
                    AllowDuplicates = false
                });
                if (selection.Status != PromptStatus.OK || selection.Value == null) return;
                ids.AddRange(selection.Value.GetObjectIds());
            }
            else
            {
                using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
                {
                    BlockTableRecord model = tr.GetObject(
                        SymbolUtilityServices.GetBlockModelSpaceId(doc.Database), OpenMode.ForRead)
                        as BlockTableRecord;
                    if (model != null)
                        foreach (ObjectId id in model)
                            if (tr.GetObject(id, OpenMode.ForRead, false) is CivilPipe)
                                ids.Add(id);
                }
            }
            var vertices = new Point3dCollection();
            int pipes = 0, skipped = 0;
            var networks = new HashSet<ObjectId>();
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids.Distinct())
                {
                    CivilPipe pipe = null;
                    try { pipe = tr.GetObject(id, OpenMode.ForRead, false) as CivilPipe; }
                    catch { }
                    if (pipe == null) { skipped++; continue; }
                    try
                    {
                        Point3d a = pipe.StartPoint, b = pipe.EndPoint;
                        double length = Math.Sqrt(Math.Pow(b.X-a.X,2) + Math.Pow(b.Y-a.Y,2));
                        double diameter = pipe.OuterDiameterOrWidth;
                        if (length < 0.001 || diameter <= 0 || !IsFinite(diameter)) { skipped++; continue; }
                        networks.Add(pipe.NetworkId);
                        double nx = (b.Y-a.Y)/length, ny = -(b.X-a.X)/length;
                        double depth = diameter/2.0 + bedding;
                        // Sample both sides at ends and between along straight pipe
                        // segments. Pipe centreline elevations are interpolated.
                        int steps = Math.Max(1, (int)Math.Ceiling(length / (5.0 * settings.Double("Units",1.0))));
                        for (int i=0; i<=steps; i++)
                        {
                            double t=(double)i/steps;
                            double x=a.X+(b.X-a.X)*t, y=a.Y+(b.Y-a.Y)*t;
                            double z=a.Z+(b.Z-a.Z)*t-depth;
                            vertices.Add(new Point3d(x+nx*width/2,y+ny*width/2,z));
                            vertices.Add(new Point3d(x-nx*width/2,y-ny*width/2,z));
                        }
                        pipes++;
                    }
                    catch { skipped++; }
                }
            }
            if (pipes == 0) { doc.Editor.WriteMessage("\nNo valid sewer pipe elevations found."); return; }
            if (networks.Count > 1)
            {
                doc.Editor.WriteMessage("\nMultiple pipe networks selected. Use one connected network per surface to avoid invalid TIN bridges. No changes made.");
                return;
            }
            PromptKeywordOptions confirm = new PromptKeywordOptions(
                "\nCreate a NEW excavation-bottom TIN for {0} pipes? [Yes/No] <No>: ");
            // Set text explicitly; PromptKeywordOptions does not format parameters.
            confirm = new PromptKeywordOptions("\nCreate new excavation-bottom TIN from " +
                pipes + " pipe(s), " + vertices.Count + " points? [Yes/No] <No>: ");
            confirm.Keywords.Add("Yes"); confirm.Keywords.Add("No");
            confirm.Keywords.Default = "No"; confirm.AllowNone = true;
            PromptResult answer=doc.Editor.GetKeywords(confirm);
            if (answer.Status != PromptStatus.OK || answer.StringResult != "Yes") return;
            using (doc.LockDocument())
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in civil.GetSurfaceIds())
                {
                    Surface existing = tr.GetObject(id, OpenMode.ForRead, false) as Surface;
                    if (existing != null && string.Equals(existing.Name,name,StringComparison.OrdinalIgnoreCase))
                    {
                        doc.Editor.WriteMessage("\nSurface already exists. Choose a new name; nothing replaced.");
                        return;
                    }
                }
                ObjectId created = TinSurface.Create(doc.Database,name);
                TinSurface surface = tr.GetObject(created,OpenMode.ForWrite) as TinSurface;
                if (surface == null) throw new InvalidOperationException("TIN surface creation failed.");
                surface.PointsDefinition.AddPoints(vertices);
                tr.Commit();
            }
            doc.Editor.Regen();
            doc.Editor.WriteMessage("\nCE_SEWEREXCAVATIONSURFACE created '{0}' from {1} pipes; skipped={2}. Inspect and trim TIN boundary/triangles before using for excavation quantities.", name,pipes,skipped);
        }
        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}

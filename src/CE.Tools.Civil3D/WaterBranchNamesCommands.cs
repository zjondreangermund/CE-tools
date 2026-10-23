using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.WaterBranchNamesCommands))]

namespace CETools.Civil3D
{
    public sealed class WaterBranchNamesCommands
    {
        [CommandMethod("CE_TOOLS", "CE_WATERBRANCHNAMES",
            CommandFlags.Modal | CommandFlags.Redraw | CommandFlags.UsePickSet)]
        public void AddWaterBranchNames()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civil = CivilApplication.ActiveDocument;
            if (document == null || civil == null) return;
            PromptSelectionResult implied = document.Editor.SelectImplied();
            HashSet<ObjectId> selected = null;
            if (implied.Status == PromptStatus.OK && implied.Value != null && implied.Value.Count > 0)
                selected = new HashSet<ObjectId>(implied.Value.GetObjectIds());
            else
            {
                var scope = new PromptKeywordOptions("\nWater branch names [All/Selected] <All>: ");
                scope.Keywords.Add("All"); scope.Keywords.Add("Selected"); scope.AllowNone = true;
                PromptResult answer = document.Editor.GetKeywords(scope);
                if (answer.Status == PromptStatus.Cancel) return;
                if (answer.Status == PromptStatus.OK && answer.StringResult == "Selected")
                {
                    PromptSelectionResult pick = document.Editor.GetSelection(new PromptSelectionOptions
                    {
                        MessageForAdding = "\nSelect multiple Water alignments: ",
                        AllowDuplicates = false, RejectObjectsFromNonCurrentSpace = true
                    });
                    if (pick.Status != PromptStatus.OK || pick.Value == null) return;
                    selected = new HashSet<ObjectId>(pick.Value.GetObjectIds());
                }
            }
            int added = 0, existing = 0, skipped = 0;
            using (document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                var model = (BlockTableRecord)transaction.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(document.Database), OpenMode.ForWrite);
                var existingNames = new List<MText>();
                foreach (ObjectId id in model)
                {
                    var text = transaction.GetObject(id, OpenMode.ForRead, false) as MText;
                    if (text != null && !text.IsErased) existingNames.Add(text);
                }
                foreach (ObjectId id in civil.GetAlignmentIds())
                {
                    if (selected != null && !selected.Contains(id)) continue;
                    var alignment = transaction.GetObject(id, OpenMode.ForRead, false) as CivilAlignment;
                    if (alignment == null || alignment.IsReferenceObject) { skipped++; continue; }
                    string name = alignment.Name;
                    bool water = name.StartsWith("W-", StringComparison.OrdinalIgnoreCase) ||
                        name.IndexOf("WATER", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        (alignment.Description ?? string.Empty).StartsWith("CE water alignment", StringComparison.OrdinalIgnoreCase);
                    if (!water) continue;
                    string route = name;
                    string description = alignment.Description ?? string.Empty;
                    const string prefix = "route=";
                    int routeIndex = description.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
                    if (routeIndex >= 0)
                    {
                        route = description.Substring(routeIndex + prefix.Length).Split('|')[0].Trim();
                        if (string.IsNullOrWhiteSpace(route)) route = name;
                    }
                    double station = (alignment.StartingStation + alignment.EndingStation) * 0.5;
                    double x = 0, y = 0;
                    try { alignment.PointLocation(station, 0, ref x, ref y); }
                    catch { skipped++; continue; }
                    var location = new Point3d(x, y, 0);
                    // Idempotent: leave labels already placed by CE_WATERALIGN or by this command.
                    if (existingNames.Any(t => string.Equals(t.Text.Trim(), route, StringComparison.OrdinalIgnoreCase) &&
                        new Point2d(t.Location.X, t.Location.Y).GetDistanceTo(new Point2d(x,y)) <= 15.0))
                    { existing++; continue; }
                    var label = new MText();
                    label.SetDatabaseDefaults(document.Database);
                    label.Contents = route;
                    label.Location = location;
                    label.Attachment = AttachmentPoint.MiddleCenter;
                    label.TextHeight = 2.5;
                    label.LayerId = alignment.LayerId;
                    model.AppendEntity(label);
                    transaction.AddNewlyCreatedDBObject(label, true);
                    existingNames.Add(label);
                    added++;
                }
                transaction.Commit();
            }
            document.Editor.Regen();
            System.Windows.Forms.MessageBox.Show("Water branch names added: " + added +
                "\nExisting matching names retained: " + existing +
                "\nSkipped: " + skipped +
                "\n\nWater alignments and existing labels were not modified.",
                "CE Tools - Water Branch Names");
        }
    }
}

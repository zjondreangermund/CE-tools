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
            var options = new ProductionSettingsDialogModel(
                "CE Tools - Water Branch Names",
                "Select Water branch alignments and control name display without changing pipe geometry.");
            options.AddChoice("Scope", "01 Selection", "Water alignments", "All Water alignments",
                "Select all Water alignments or multiple Water alignments from the drawing.",
                new[] { "All Water alignments", "Multiple selected alignments", "Selected water parts" });
            options.AddChoice("Direction", "02 Presentation", "Label direction", "Along alignment",
                "Orient branch names with the local alignment tangent or keep horizontal.",
                new[] { "Along alignment", "Horizontal" });
            options.AddPaperHeight("TextHeight", "02 Presentation", "Branch-name paper height", 3.5,
                "Annotative text height in paper millimetres.");
            var layers = new List<string> { "<Create new CE Tools layer>" };
            using (var tr = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in (LayerTable)tr.GetObject(document.Database.LayerTableId, OpenMode.ForRead))
                    layers.Add(((LayerTableRecord)tr.GetObject(id, OpenMode.ForRead)).Name);
            }
            options.AddChoice("BranchLayer", "02 Presentation", "Branch-name layer", "<Create new CE Tools layer>",
                "Choose a drawing layer or create the named layer below.", layers);
            options.AddText("NewBranchLayer", "02 Presentation", "New CE Tools layer name", "CE-WATER-BRANCH-LABELS", "Created if missing.");
            options.AddChoice("Side", "02 Presentation", "Branch-name side", "Above", "Side relative to the readable branch tangent.", new[] { "Above", "Below", "Alternating" });
            options.AddPositiveDouble("AboveOffset", "02 Presentation", "Above offset (paper mm)", 10, "Offset above the branch centreline.");
            options.AddPositiveDouble("BelowOffset", "02 Presentation", "Below offset (paper mm)", 5.5, "Offset below the branch centreline.");
            options.AddChoice("UseLongThreshold", "03 Long branches", "Use long-section threshold", "OFF",
                "OFF repeats at the selected pipe/50 m frequency on every branch, regardless of length.", new[] { "OFF", "ON" });
            options.AddPositiveDouble("LongThreshold", "03 Long branches", "Long straight section threshold (m)", 50, "One centred label per alignment entity; repeat on longer sections.");
            options.AddChoice("LongFrequency", "03 Long branches", "Long-section repeat frequency", "Every second pipe",
                "Additional labels at pipe centres, every second pipe, or fixed 50 m spacing.",
                new[] { "Consistent 50 m", "Every pipe", "Every second pipe" });
            options.AddChoice("NameSource", "02 Presentation", "Label text", "Alignment name",
                "Use the actual branch alignment name or the stored CE route identity.",
                new[] { "Alignment name", "Stored route identity" });
            options.AddChoice("Existing", "03 Existing names", "Previously placed names",
                "Update matching labels",
                "Update matching Water branch labels without adding duplicates; other drawing annotations are not changed.",
                new[] { "Update matching labels", "Keep existing matching labels" });
            if (!DisciplineWorkflowDialogs.EditSettings(options)) return;
            HashSet<ObjectId> selected = null;
            if (options.Text("Scope") != "All Water alignments")
            {
                PromptSelectionResult pick = document.Editor.GetSelection(new PromptSelectionOptions
                {
                    MessageForAdding = "\nSelect Water alignments or water pipes/structures: ",
                    AllowDuplicates = false,
                    RejectObjectsFromNonCurrentSpace = true
                });
                if (pick.Status != PromptStatus.OK || pick.Value == null) return;
                selected = new HashSet<ObjectId>(pick.Value.GetObjectIds());
            }
            int added = 0, updated = 0, existing = 0, skipped = 0;
            using (document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                var model = (BlockTableRecord)transaction.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(document.Database), OpenMode.ForWrite);
                var reg = (RegAppTable)transaction.GetObject(document.Database.RegAppTableId, OpenMode.ForRead);
                if (!reg.Has("CE_WATER_BRANCH_LABEL"))
                {
                    reg.UpgradeOpen(); var record = new RegAppTableRecord { Name = "CE_WATER_BRANCH_LABEL" };
                    reg.Add(record); transaction.AddNewlyCreatedDBObject(record, true);
                }
                string layerName = options.Text("BranchLayer") == "<Create new CE Tools layer>" ? options.Text("NewBranchLayer") : options.Text("BranchLayer");
                if (string.IsNullOrWhiteSpace(layerName)) layerName = "CE-WATER-BRANCH-LABELS";
                var layerTable = (LayerTable)transaction.GetObject(document.Database.LayerTableId, OpenMode.ForRead);
                ObjectId layerId;
                if (layerTable.Has(layerName)) layerId = layerTable[layerName];
                else
                {
                    layerTable.UpgradeOpen();
                    var layer = new LayerTableRecord { Name = layerName, Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, 3) };
                    layerId = layerTable.Add(layer); transaction.AddNewlyCreatedDBObject(layer, true);
                }
                var drawingScales = AnnotationScaleSyncManager.ReadUsedDrawingScaleContexts(document.Database, transaction);
                var existingNames = new List<MText>();
                foreach (ObjectId id in model)
                {
                    var text = transaction.GetObject(id, OpenMode.ForRead, false) as MText;
                    if (text != null && !text.IsErased) existingNames.Add(text);
                }
                foreach (ObjectId id in civil.GetAlignmentIds())
                {

                    var alignment = transaction.GetObject(id, OpenMode.ForRead, false) as CivilAlignment;
                    if (alignment == null || alignment.IsReferenceObject) { skipped++; continue; }
                    string name = alignment.Name;
                    string description = alignment.Description ?? string.Empty;
                    bool water = name.StartsWith("W-", StringComparison.OrdinalIgnoreCase) ||
                        name.IndexOf("WATER", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        description.IndexOf("CE water alignment", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        (name.StartsWith("BRANCH-", StringComparison.OrdinalIgnoreCase) &&
                         description.IndexOf("water", StringComparison.OrdinalIgnoreCase) >= 0);
                    if (!water) continue;
                    if (selected != null && !selected.Contains(id))
                    {
                        bool matches = false;
                        foreach (var selectedId in selected)
                        {
                            var part = transaction.GetObject(selectedId, OpenMode.ForRead, false);
                            var pipe = part as Autodesk.Civil.DatabaseServices.Pipe;
                            var structure = part as Autodesk.Civil.DatabaseServices.Structure;
                            if (pipe == null && structure == null) continue;
                            Point3d point = pipe != null ? pipe.StartPoint + (pipe.EndPoint - pipe.StartPoint) * 0.5 : structure.Position;
                            try
                            {
                                double station = 0, offset = 0;
                                alignment.StationOffset(point.X, point.Y, ref station, ref offset);
                                if (Math.Abs(offset) < 0.1 && station >= alignment.StartingStation && station <= alignment.EndingStation) { matches = true; break; }
                            }
                            catch (System.Exception) { }
                        }
                        if (!matches) continue;
                    }
                    string route = name;
                    const string prefix = "route=";
                    int routeIndex = description.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
                    if (routeIndex >= 0)
                    {
                        route = description.Substring(routeIndex + prefix.Length).Split('|')[0].Trim();
                        if (string.IsNullOrWhiteSpace(route)) route = name;
                    }
                    string displayName = options.Text("NameSource") == "Stored route identity" ? route : name;
                    var owned = existingNames.Where(t => !t.IsErased && (Owns(t, alignment.ObjectId.Handle.ToString()) || WaterProductionCommands.IsGeneratedRouteLabel(t, alignment))).ToList();
                    if (owned.Count > 0 && options.Text("Existing") == "Keep existing matching labels") { existing += owned.Count; continue; }
                    foreach (var text in owned) { text.UpgradeOpen(); text.Erase(); updated++; }
                    var pipeAnchors = ReadPipeAnchors(alignment, civil, transaction);
                    int labelIndex = 0;
                    foreach (double station in LabelStations(alignment, civil, transaction, options, pipeAnchors))
                    {
                        double x = 0, y = 0;
                        try { alignment.PointLocation(station, 0, ref x, ref y); }
                        catch { skipped++; continue; }
                        double angle = ReadTangentAngle(alignment, station);
                        bool below = options.Text("Side") == "Below" || (options.Text("Side") == "Alternating" && labelIndex % 2 == 1);
                        double offset = PaperAnnotationScale.ModelTextHeight(document.Database,
                            options.Double(below ? "BelowOffset" : "AboveOffset", below ? 5.5 : 10)) * (below ? -1 : 1);
                        var anchor = new Point3d(x, y, 0);
                        if (pipeAnchors.Count > 0) anchor = pipeAnchors.OrderBy(a => Math.Abs(a.Key - station)).First().Value;
                        var location = new Point3d(anchor.X - Math.Sin(angle) * offset, anchor.Y + Math.Cos(angle) * offset, anchor.Z);
                        // Migrate an old untagged/route label only when both its text and location match.
                        MText old = existingNames.Where(t => !t.IsErased &&
                            new Point2d(t.Location.X, t.Location.Y).GetDistanceTo(new Point2d(x, y)) <= Math.Max(15.0, Math.Abs(offset)))
                            .FirstOrDefault(t => string.Equals(t.Text.Trim(), name, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(t.Text.Trim(), route, StringComparison.OrdinalIgnoreCase));
                        if (old != null && options.Text("Existing") == "Keep existing matching labels") { existing++; continue; }
                        MText label;
                        if (old != null)
                        {
                            old.UpgradeOpen(); old.Contents = displayName; label = old; updated++;
                        }
                        else
                        {
                            label = new MText(); label.SetDatabaseDefaults(document.Database);
                            model.AppendEntity(label); transaction.AddNewlyCreatedDBObject(label, true); added++;
                        }
                        label.Contents = displayName;
                        label.Location = location;
                        label.Attachment = AttachmentPoint.MiddleCenter;
                        label.TextHeight = PaperAnnotationScale.AnnotativeTextHeight(document.Database, options.Double("TextHeight", 3.5));
                        PaperAnnotationScale.SetAnnotative(label);
                        label.BackgroundFill = true; label.UseBackgroundColor = true; label.BackgroundScaleFactor = 1.1;
                        label.Rotation = options.Text("Direction") == "Along alignment" ? angle : 0.0;
                        NameAnnotationScaleOrientation.Synchronize(label, drawingScales);
                        label.LayerId = layerId;
                        WaterProductionCommands.LinkBranchLabel(document.Database, transaction, label, alignment);
                        label.XData = new ResultBuffer(new TypedValue(1001, "CE_WATER_BRANCH_LABEL"),
                            new TypedValue(1000, alignment.ObjectId.Handle.ToString()));
                        existingNames.Remove(label); // never reuse one label for two stations
                        labelIndex++;
                    }
                }
                transaction.Commit();
            }
            document.Editor.Regen();
            System.Windows.Forms.MessageBox.Show("Water branch names added: " + added +
                "\nExisting branch names updated: " + updated +
                "\nExisting matching names retained: " + existing +
                "\nSkipped: " + skipped +
                "\n\nWater alignment geometry was not modified.",
                "CE Tools - Water Branch Names");
        }
        private static bool Owns(MText text, string handle)
        {
            using (var data = text.GetXDataForApplication("CE_WATER_BRANCH_LABEL"))
                return data != null && data.AsArray().Skip(1).Any(v => string.Equals(Convert.ToString(v.Value), handle, StringComparison.OrdinalIgnoreCase));
        }

        private static IEnumerable<double> LabelStations(CivilAlignment alignment, CivilDocument civil,
            Transaction tr, ProductionSettingsDialogModel options, Dictionary<double, Point3d> pipeAnchors)
        {
            var result = new List<double>();
            var sections = new List<Tuple<double, double>>();
            foreach (var entity in alignment.Entities)
            {
                // Entity subclasses expose start/end stations across supported Civil versions.
                var startProperty = entity.GetType().GetProperty("StartStation");
                var endProperty = entity.GetType().GetProperty("EndStation");
                if (startProperty == null || endProperty == null) continue;
                double start = Convert.ToDouble(startProperty.GetValue(entity, null));
                double end = Convert.ToDouble(endProperty.GetValue(entity, null));
                if (end > start) sections.Add(Tuple.Create(start, end));
            }
            if (sections.Count == 0) sections.Add(Tuple.Create(alignment.StartingStation, alignment.EndingStation));
            // Merge tangent-continuous straight entities into a single label run.
            var runs = new List<Tuple<double, double>>();
            foreach (var section in sections.OrderBy(x => x.Item1))
            {
                if (runs.Count > 0)
                {
                    var previous = runs[runs.Count - 1];
                    double delta = ReadTangentAngle(alignment, (previous.Item1 + previous.Item2) * 0.5) -
                        ReadTangentAngle(alignment, (section.Item1 + section.Item2) * 0.5);
                    if (Math.Abs(previous.Item2 - section.Item1) < 0.001 && Math.Abs(Math.Sin(delta)) < 0.001)
                    { runs[runs.Count - 1] = Tuple.Create(previous.Item1, section.Item2); continue; }
                }
                runs.Add(section);
            }
            sections = runs;
            var pipeStations = pipeAnchors.Keys.OrderBy(x => x).ToList();
            foreach (var section in sections)
                result.AddRange(BranchLabelRepeatPlan.Select(pipeStations, section.Item1, section.Item2,
                    options.Text("UseLongThreshold") == "ON", options.Double("LongThreshold", 50), options.Text("LongFrequency")));
            return result.GroupBy(x => Math.Round(x, 3)).Select(g => g.First()).OrderBy(x => x);
        }

        private static Dictionary<double, Point3d> ReadPipeAnchors(CivilAlignment alignment, CivilDocument civil, Transaction tr)
        {
            var result = new Dictionary<double, Point3d>();
            var model = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(alignment.Database), OpenMode.ForRead);
            var pipes = new List<Autodesk.Civil.DatabaseServices.Pipe>();
            foreach (ObjectId networkId in civil.GetPipeNetworkIds())
            {
                var network = tr.GetObject(networkId, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Network;
                if (network == null || !(network.Name.IndexOf("water", StringComparison.OrdinalIgnoreCase) >= 0 || network.Name.StartsWith("W-", StringComparison.OrdinalIgnoreCase))) continue;
                foreach (ObjectId pipeId in network.GetPipeIds())
                {
                    var pipe = tr.GetObject(pipeId, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Pipe;
                    if (pipe == null) continue;
                    pipes.Add(pipe);
                }
            }
            var anchors = SewerBranchAlignmentCommands.ReadPipePlanLabelAnchors(model, pipes.Select(p => p.ObjectId), tr);
            foreach (var pipe in pipes)
            {
                var mid = pipe.StartPoint + (pipe.EndPoint - pipe.StartPoint) * 0.5;
                try
                {
                    double station = 0, offset = 0;
                    alignment.StationOffset(mid.X, mid.Y, ref station, ref offset);
                    if (Math.Abs(offset) > 0.1 || station < alignment.StartingStation || station > alignment.EndingStation) continue;
                    bool onRoute = true;
                    foreach (var endpoint in new[] { pipe.StartPoint, pipe.EndPoint })
                    {
                        double endpointStation = 0, endpointOffset = 0;
                        alignment.StationOffset(endpoint.X, endpoint.Y, ref endpointStation, ref endpointOffset);
                        if (Math.Abs(endpointOffset) > 0.1 || endpointStation < alignment.StartingStation - 0.001 || endpointStation > alignment.EndingStation + 0.001)
                        { onRoute = false; break; }
                    }
                    if (!onRoute) continue;
                    Point3d anchor;
                    result[station] = anchors.TryGetValue(pipe.ObjectId, out anchor) ? anchor : mid;
                }
                catch (System.Exception) { }
            }
            return result;
        }

        private static double ReadTangentAngle(CivilAlignment alignment, double station)
        {
            try
            {
                double lead = Math.Max(alignment.StartingStation, station - 0.1);
                double tail = Math.Min(alignment.EndingStation, station + 0.1);
                double x1 = 0, y1 = 0, x2 = 0, y2 = 0;
                alignment.PointLocation(lead, 0, ref x1, ref y1);
                alignment.PointLocation(tail, 0, ref x2, ref y2);
                double angle = Math.Atan2(y2 - y1, x2 - x1);
                if (angle > Math.PI / 2) angle -= Math.PI;
                if (angle < -Math.PI / 2) angle += Math.PI;
                return angle;
            }
            catch (System.Exception) { return 0.0; }
        }
    }
}

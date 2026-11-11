using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilPipe = Autodesk.Civil.DatabaseServices.Pipe;
using CivilStructure = Autodesk.Civil.DatabaseServices.Structure;
using CivilNetwork = Autodesk.Civil.DatabaseServices.Network;
using PartFamily = Autodesk.Civil.DatabaseServices.Styles.PartFamily;
using PartSize = Autodesk.Civil.DatabaseServices.Styles.PartSize;

[assembly: CommandClass(typeof(CETools.Civil3D.SewerStructureIntervalUpdateCommands))]
namespace CETools.Civil3D
{
    /// <summary>Redistributes structures by physical branch-alignment distance without redesigning grades.</summary>
    public sealed class SewerStructureIntervalUpdateCommands
    {
        private const double Tolerance = 0.001;
        // Read by refresh queues: these edits have their own preservation/numbering pass.
        internal static bool Editing { get; private set; }

        [CommandMethod("CE_TOOLS", "CE_SEWERSTRUCTUREINTERVALUPDATE", CommandFlags.Modal | CommandFlags.Redraw)]
        public void UpdateExistingSewerStructureIntervals()
        {
            Document doc = AcApplication.DocumentManager.MdiActiveDocument;
            if (doc == null || CivilApplication.ActiveDocument == null) return;
            try
            {
                var choices = new Dictionary<string, ObjectId>();
                using (var tr = doc.Database.TransactionManager.StartTransaction())
                    foreach (ObjectId id in CivilApplication.ActiveDocument.GetPipeNetworkIds())
                    {
                        var network = (CivilNetwork)tr.GetObject(id, OpenMode.ForRead);
                        if (!network.IsReferenceObject) choices.Add(network.Name + " [" + id.Handle + "]", id);
                    }
                if (choices.Count == 0) throw new InvalidOperationException("No editable gravity networks found.");
                var settings = new ProductionSettingsDialogModel("CE Tools - Sewer Structure Intervals",
                    "Redistribute along existing branch alignments. Ends, vertices, junctions and design changes stay fixed. " +
                    "Minimum spacing uses drawing units. Existing elevations and grades are not redesigned. Curved or offset pipe routes require separate review.");
                settings.AddChoice("Network", "Network", "Existing sewer network", choices.Keys.First(), "Choose a gravity sewer network.", choices.Keys);
                settings.AddPositiveDouble("Interval", "Spacing", "Minimum interval (drawing units)", 60,
                    "242.88 / 60 gives four equal intervals of 60.72. Fixed points closer than the minimum remain in place.");
                if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;
                ObjectId networkId = choices[settings.Text("Network")];
                double minimum = settings.Double("Interval", 60);
                List<Span> spans;
                Dictionary<ObjectId, StructureState> structures;
                using (var tr = doc.Database.TransactionManager.StartTransaction())
                    spans = Plan(tr, networkId, minimum, out structures); // temporary alignment polylines roll back
                var changed = spans.Where(s => s.Changed).ToList();
                string preview = string.Join("\n", spans.Select(s => s.Description).Take(70));
                if (spans.Count > 70) preview += "\n... " + (spans.Count - 70) + " further spans.";
                preview += "\n\n" + changed.Count + " spans to update; " + spans.Count(s => s.Length < minimum) +
                    " fixed-point spans shorter than the minimum.\nExisting profile views, alignment geometry and station equations stay in place. " +
                    "New parts inherit their source display styles and profile/section membership. Rules are not applied.";
                if (changed.Count == 0) { AcApplication.ShowAlertDialog("No spacing changes required.\n\n" + preview); return; }
                if (!DisciplineWorkflowDialogs.Confirm("Sewer interval preview", preview + "\n\nApply this update?")) return;
                int added = 0, removed = 0;
                Editing = true;
                SewerNetworkDynamicSequenceManager.DiscardPendingForIntervalUpdate();
                try
                {
                    using (var tr = doc.Database.TransactionManager.StartTransaction())
                    {
                        var network = (CivilNetwork)tr.GetObject(networkId, OpenMode.ForWrite);
                        foreach (var span in changed) Apply(tr, network, span, ref added, ref removed);
                        // Existing alignment identity and geometry must not be regenerated: profiles depend on them.
                        // Station references refresh from each part's RefAlignmentId and new XY position.
                        foreach (var state in structures.Values.Where(s => !s.Id.IsErased)) state.Verify(tr);
                        VerifyNetwork(tr, network, spans);
                        Renumber(tr, network);
                        tr.Commit();
                    }
                }
                finally { SewerNetworkDynamicSequenceManager.DiscardPendingForIntervalUpdate(); Editing = false; }
                doc.Editor.Regen();
                AcApplication.ShowAlertDialog("Sewer intervals updated.\nNew structures: " + added + "\nRemoved intermediate structures: " + removed +
                    "\nPipe/structure numbering and native labels updated. Existing branch alignments and profile views retained.");
            }
            catch (System.Exception ex)
            {
                AcApplication.ShowAlertDialog("Sewer interval update stopped: " + ex.Message +
                    "\nAny uncommitted network edits were rolled back.");
            }
        }

        private static List<Span> Plan(Transaction tr, ObjectId networkId, double minimum,
            out Dictionary<ObjectId, StructureState> structures)
        {
            var network = (CivilNetwork)tr.GetObject(networkId, OpenMode.ForRead);
            structures = network.GetStructureIds().Cast<ObjectId>().ToDictionary(id => id, id => new StructureState(tr, id));
            var edges = new List<Edge>();
            foreach (ObjectId id in network.GetPipeIds())
            {
                var pipe = (CivilPipe)tr.GetObject(id, OpenMode.ForRead);
                if (pipe.IsReferenceObject || pipe.StartStructureId.IsNull || pipe.EndStructureId.IsNull)
                    throw new InvalidOperationException("Pipe " + pipe.Name + " is referenced or disconnected. Connect/review it first.");
                var edge = new Edge(tr, pipe);
                if (edge.Alignment.IsNull) throw new InvalidOperationException("Pipe " + pipe.Name + " has no branch reference alignment. Assign it first.");
                edges.Add(edge);
            }
            var result = new List<Span>();
            foreach (var group in edges.GroupBy(e => e.Alignment))
            {
                var alignment = (Alignment)tr.GetObject(group.Key, OpenMode.ForRead);
                if (alignment.IsReferenceObject) throw new InvalidOperationException("Referenced alignment " + alignment.Name + " cannot be updated.");
                var path = tr.GetObject(alignment.GetPolyline(), OpenMode.ForRead) as Polyline;
                if (path == null || path.Closed) throw new InvalidOperationException("Branch " + alignment.Name + " must be an open polyline alignment.");
                var vertices = Enumerable.Range(0, path.NumberOfVertices).Select(i => path.GetDistanceAtParameter(i)).ToArray();
                foreach (var edge in group)
                {
                    edge.A = Distance(path, edge.Start); edge.B = Distance(path, edge.End);
                    edge.Low = Math.Min(edge.A, edge.B); edge.High = Math.Max(edge.A, edge.B);
                    if (edge.High - edge.Low < Tolerance || Math.Abs(edge.High - edge.Low - FlatDistance(edge.Start, edge.End)) > Tolerance ||
                        Math.Abs(edge.PlanLength - FlatDistance(edge.Start, edge.End)) > Tolerance)
                        throw new InvalidOperationException("Pipe " + edge.Name + " crosses a bend/curve or does not follow its alignment. Moving it would change its grade.");
                    edge.LowId = edge.A < edge.B ? edge.StartId : edge.EndId;
                    edge.HighId = edge.A < edge.B ? edge.EndId : edge.StartId;
                    if (FlatDistance(structures[edge.StartId].Position, edge.Start) > Tolerance || FlatDistance(structures[edge.EndId].Position, edge.End) > Tolerance)
                        throw new InvalidOperationException("Pipe " + edge.Name + " has wall/offset connections. Review these before spacing edits.");
                }
                var ordered = group.OrderBy(e => e.Low).ToList();
                Span current = null;
                foreach (var edge in ordered)
                {
                    if (current != null && edge.Low < current.End - Tolerance)
                        throw new InvalidOperationException("Overlapping pipe runs on alignment " + alignment.Name + ". Assign distinct branch alignments first.");
                    bool join = current != null && Math.Abs(edge.Low - current.End) < Tolerance &&
                        current.Edges.Last().HighId == edge.LowId &&
                        !vertices.Any(v => Math.Abs(v - edge.Low) < Tolerance) &&
                        structures[edge.LowId].Connections == 2 &&
                        current.Edges.Last().Compatible(edge) &&
                        Removable(tr, structures[edge.LowId], current.Edges.Last(), edge, structures);
                    if (!join)
                    {
                        current = new Span { Alignment = group.Key, Branch = alignment.Name };
                        result.Add(current);
                    }
                    current.Edges.Add(edge);
                }
                foreach (var span in result.Where(s => s.Alignment == group.Key))
                {
                    var anchors = new List<double> { span.Start };
                    anchors.AddRange(vertices.Where(v => v > span.Start + Tolerance && v < span.End - Tolerance));
                    anchors.Add(span.End);
                    for (int i = 1; i < anchors.Count; i++)
                        span.Stations.AddRange(SewerStructureIntervalPlanner.Stations(anchors[i - 1], anchors[i], minimum).Skip(i == 1 ? 0 : 1));
                    if (span.Stations.Count > 10001) throw new InvalidOperationException("Too many new structures requested.");
                    var old = new[] { span.Start }.Concat(span.Edges.Select(e => e.High)).ToArray();
                    span.Changed = old.Length != span.Stations.Count || old.Where((v, i) => i < span.Stations.Count && Math.Abs(v - span.Stations[i]) > Tolerance).Any();
                    if (!span.Changed) continue;
                    foreach (var edge in span.Edges)
                        if (edge.OverrideRules || edge.Special) throw new InvalidOperationException("Pipe " + edge.Name + " has rule overrides, spanning labels or custom object data. Preserve/review these before redistributing it.");
                    span.Template = structures[span.Edges[0].LowId];
                    var template = (CivilStructure)tr.GetObject(span.Template.Id, OpenMode.ForRead);
                    if (template.OverrideRuleSet) throw new InvalidOperationException("Template structure has individual rule overrides.");
                    ResolveStructurePartChoice(tr, template, out span.Family, out span.Size);
                    foreach (double station in span.Stations.Skip(1).Take(span.Stations.Count - 2))
                    {
                        var edge = span.Edges.First(e => station <= e.High + Tolerance && station >= e.Low - Tolerance);
                        double t = (station - edge.A) / (edge.B - edge.A);
                        Point3d xy = path.GetPointAtDist(station);
                        var start = structures[edge.StartId]; var end = structures[edge.EndId];
                        span.NewNodes.Add(new NewNode { Point = new Point3d(xy.X, xy.Y, edge.Start.Z + (edge.End.Z - edge.Start.Z) * t),
                            Rim = start.Rim + (end.Rim - start.Rim) * t, Sump = start.Sump + (end.Sump - start.Sump) * t });
                    }
                }
            }
            if (result.Sum(s => s.NewNodes.Count) > 10000) throw new InvalidOperationException("More than 10,000 new structures requested in this network.");
            return result;
        }

        private static bool Removable(Transaction tr, StructureState middle, Edge left, Edge right, Dictionary<ObjectId, StructureState> states)
        {
            var part = (CivilStructure)tr.GetObject(middle.Id, OpenMode.ForRead);
            if (!part.ExtensionDictionary.IsNull || part.OverrideRuleSet || middle.HasSpecialLabels) return false;
            var a = states[left.LowId]; var b = states[right.HighId];
            double f = (left.High - left.Low) / (right.High - left.Low);
            // Retain changes in the piecewise rim/sump elevation line as well as pipe grade changes.
            return Math.Abs(middle.Rim - (a.Rim + (b.Rim - a.Rim) * f)) < 0.000001 &&
                Math.Abs(middle.Sump - (a.Sump + (b.Sump - a.Sump) * f)) < 0.000001 &&
                middle.Style == a.Style && middle.Rule == a.Rule && middle.Surface == a.Surface &&
                new HashSet<ObjectId>(middle.LabelStyles).SetEquals(a.LabelStyles) && SameProfileLabels(middle.ProfileLabels, a.ProfileLabels);
        }

        private static void Apply(Transaction tr, CivilNetwork network, Span span, ref int added, ref int removed)
        {
            var seed = span.Edges[0];
            var pipe = (CivilPipe)tr.GetObject(seed.Id, OpenMode.ForWrite);
            var endEdge = span.Edges.Last();
            Point3d low = seed.AtLow, high = endEdge.AtHigh;
            bool forward = seed.A < seed.B;
            // Merge only compatible collinear, equal-grade spans. The seed ID and its labels survive.
            if (span.Edges.Count > 1)
            {
                pipe.ConnectToStructure(forward ? ConnectorPositionType.End : ConnectorPositionType.Start, endEdge.HighId, true);
                pipe.StartPoint = forward ? low : high;
                pipe.EndPoint = forward ? high : low;
                foreach (var edge in span.Edges.Skip(1))
                {
                    var redundant = (CivilPipe)tr.GetObject(edge.Id, OpenMode.ForWrite);
                    redundant.Disconnect(ConnectorPositionType.Start);
                    redundant.Disconnect(ConnectorPositionType.End);
                    redundant.Erase();
                }
                foreach (var edge in span.Edges.Take(span.Edges.Count - 1))
                {
                    var redundant = (CivilStructure)tr.GetObject(edge.HighId, OpenMode.ForWrite);
                    if (redundant.ConnectedPipesCount != 0) throw new InvalidOperationException("An intermediate structure still has connections; update cancelled.");
                    redundant.Erase(); removed++;
                }
            }
            var live = new List<ObjectId> { seed.Id };
            var createdStates = new List<StructureState>();
            foreach (var node in span.NewNodes)
            {
                ObjectId id = ObjectId.Null;
                network.AddStructure(span.Family, span.Size, new Point3d(node.Point.X, node.Point.Y, node.Rim), 0, ref id, false);
                if (id.IsNull) throw new InvalidOperationException("Civil 3D did not create a structure.");
                var structure = (CivilStructure)tr.GetObject(id, OpenMode.ForWrite);
                var template = (CivilStructure)tr.GetObject(span.Template.Id, OpenMode.ForRead);
                structure.StyleId = (ObjectId)Read(template, "StyleId"); structure.RuleSetStyleId = template.RuleSetStyleId;
                structure.RefSurfaceId = template.RefSurfaceId; structure.RefAlignmentId = span.Alignment;
                structure.LayerId = template.LayerId; structure.Rotation = template.Rotation;
                foreach (var property in new[] { "Color", "LineWeight", "LinetypeId", "LinetypeScale", "Description", "ShowToolTip" })
                    Set(structure, property, Read(template, property));
                Set(structure, "ControlSumpBy", Read(template, "ControlSumpBy"));
                Set(structure, "AutomaticRimSurfaceAdjustment", false);
                Set(structure, "RimElevation", node.Rim);
                // Find containing live segment; do not assume which ObjectId BreakPipe retains.
                var containing = live.Select(pid => (CivilPipe)tr.GetObject(pid, OpenMode.ForWrite)).Single(p => Inside(p, node.Point));
                Point3d beforeStart = containing.StartPoint, beforeEnd = containing.EndPoint;
                ObjectId newPipe = ObjectId.Null;
                network.BreakPipe(containing.ObjectId, node.Point, id, ref newPipe);
                if (newPipe.IsNull) throw new InvalidOperationException("Civil 3D did not return the split segment.");
                live.Add(newPipe);
                foreach (ObjectId pid in new[] { containing.ObjectId, newPipe })
                {
                    var piece = (CivilPipe)tr.GetObject(pid, OpenMode.ForWrite);
                    seed.RestoreProperties(piece);
                    // Break/connection operations may adjust Z. Restore the original design line explicitly.
                    piece.StartPoint = OnGrade(piece.StartPoint, beforeStart, beforeEnd);
                    piece.EndPoint = OnGrade(piece.EndPoint, beforeStart, beforeEnd);
                    VerifyGrade(piece, low, high, seed);
                }
                Set(structure, "RimElevation", node.Rim); Set(structure, "SumpElevation", node.Sump);
                Set(structure, "AutomaticRimSurfaceAdjustment", Read(template, "AutomaticRimSurfaceAdjustment"));
                if (Math.Abs(ReadDouble(structure, "RimElevation") - node.Rim) > Tolerance ||
                    Math.Abs(ReadDouble(structure, "SumpElevation") - node.Sump) > Tolerance)
                    throw new InvalidOperationException("Civil 3D changed a new structure's rim/sump elevation.");
                CopyViews(structure, seed.ProfileViews, seed.SectionViews);
                CopyProfileLabels(tr, structure, span.Template.ProfileLabels);
                AddLabels(tr, structure.ObjectId, false, span.Template.LabelStyles);
                createdStates.Add(new StructureState(tr, structure.ObjectId));
                added++;
            }
            foreach (var state in createdStates) state.Verify(tr);
            foreach (ObjectId id in live)
            {
                var piece = (CivilPipe)tr.GetObject(id, OpenMode.ForWrite);
                VerifyGrade(piece, low, high, seed);
                CopyViews(piece, seed.ProfileViews, seed.SectionViews);
                if (id != seed.Id)
                {
                    AddLabels(tr, id, true, seed.LabelStyles);
                    CopyProfileLabels(tr, piece, seed.ProfileLabels);
                }
            }
        }

        private static void VerifyNetwork(Transaction tr, CivilNetwork network, List<Span> spans)
        {
            var pipes = network.GetPipeIds().Cast<ObjectId>().Select(id => (CivilPipe)tr.GetObject(id, OpenMode.ForRead)).ToList();
            int verified = 0;
            foreach (var group in spans.GroupBy(s => s.Alignment))
            {
                var alignment = (Alignment)tr.GetObject(group.Key, OpenMode.ForRead);
                var path = (Polyline)tr.GetObject(alignment.GetPolyline(), OpenMode.ForWrite);
                foreach (var span in group)
                {
                    var pieces = pipes.Where(p => p.RefAlignmentId == group.Key).Select(p => new {
                        Pipe = p, Low = Math.Min(Distance(path, p.StartPoint), Distance(path, p.EndPoint)),
                        High = Math.Max(Distance(path, p.StartPoint), Distance(path, p.EndPoint)) })
                        .Where(p => p.Low >= span.Start - Tolerance && p.High <= span.End + Tolerance).OrderBy(p => p.Low).ToList();
                    double cursor = span.Start;
                    foreach (var piece in pieces)
                    {
                        if (Math.Abs(piece.Low - cursor) > Tolerance || piece.Pipe.StartStructureId.IsNull || piece.Pipe.EndStructureId.IsNull)
                            throw new InvalidOperationException("A gap, overlap or disconnected pipe resulted; update cancelled.");
                        foreach (var original in span.Edges.Where(e => e.High > piece.Low + Tolerance && e.Low < piece.High - Tolerance))
                        {
                            VerifyGrade(piece.Pipe, original.AtLow, original.AtHigh, original);
                            if ((Distance(path, piece.Pipe.StartPoint) < Distance(path, piece.Pipe.EndPoint)) != (original.A < original.B))
                                throw new InvalidOperationException("Pipe direction changed; update cancelled.");
                        }
                        cursor = piece.High; verified++;
                    }
                    if (Math.Abs(cursor - span.End) > Tolerance) throw new InvalidOperationException("Branch coverage changed; update cancelled.");
                }
                path.Erase();
            }
            if (verified != pipes.Count) throw new InvalidOperationException("A pipe no longer belongs to its original branch span.");
        }

        private static void Renumber(Transaction tr, CivilNetwork network)
        {
            var groups = network.GetPipeIds().Cast<ObjectId>().Select(id => (CivilPipe)tr.GetObject(id, OpenMode.ForWrite))
                .GroupBy(p => p.RefAlignmentId).OrderBy(g => g.Key.Handle.ToString()).ToList();
            var named = new HashSet<ObjectId>();
            // Temporary names avoid duplicate name collisions after deletions and reversed ordering.
            string token = Guid.NewGuid().ToString("N");
            foreach (ObjectId id in network.GetPipeIds()) ((CivilPipe)tr.GetObject(id, OpenMode.ForWrite)).Name = "CE-TEMP-" + token + id.Handle;
            foreach (ObjectId id in groups.SelectMany(g => g).SelectMany(p => new[] { p.StartStructureId, p.EndStructureId }).Distinct()) ((CivilStructure)tr.GetObject(id, OpenMode.ForWrite)).Name = "CE-TEMP-" + token + id.Handle;
            var branchNumbers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in groups)
            {
                var alignment = (Alignment)tr.GetObject(group.Key, OpenMode.ForRead);
                string branch = System.Text.RegularExpressions.Regex.Match(alignment.Name, @"\d+(?!.*\d)").Value;
                if (branch.Length == 0 || !branchNumbers.Add(branch))
                    throw new InvalidOperationException("Branch alignments need unique numbered names (for example Branch-8) before renumbering.");
                var path = (Polyline)tr.GetObject(alignment.GetPolyline(), OpenMode.ForWrite);
                int number = 1, mh = 1;
                foreach (var pipe in group.OrderBy(p => Math.Min(Distance(path, p.StartPoint), Distance(path, p.EndPoint))))
                {
                    pipe.Name = "P" + branch + "." + number++;
                    var ids = Distance(path, pipe.StartPoint) < Distance(path, pipe.EndPoint) ?
                        new[] { pipe.StartStructureId, pipe.EndStructureId } : new[] { pipe.EndStructureId, pipe.StartStructureId };
                    foreach (var id in ids)
                        if (named.Add(id)) ((CivilStructure)tr.GetObject(id, OpenMode.ForWrite)).Name = "MH" + branch + "." + mh++;
                    pipe.RecordGraphicsModified(true);
                }
                path.Erase();
            }
        }

        private static IEnumerable<ProfileViewPart> DrawnParts(Transaction tr, ObjectId modelId)
        {
            var space = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(modelId.Database), OpenMode.ForRead);
            foreach (ObjectId id in space)
            {
                var part = tr.GetObject(id, OpenMode.ForRead) as ProfileViewPart;
                if (part != null && part.ModelPartId == modelId) yield return part;
            }
        }
        private static Dictionary<ObjectId, ObjectId[]> ReadProfileLabels(Transaction tr, Part model)
        {
            var result = new Dictionary<ObjectId, ObjectId[]>();
            var labels = new HashSet<ObjectId>(DrawnParts(tr, model.ObjectId).SelectMany(p => p.GetPartProfileLabelIds().Cast<ObjectId>()));
            foreach (ObjectId view in model.GetProfileViewsDisplayingMe())
            {
                var available = model is CivilPipe ? PipeProfileLabel.GetAvailableLabelIds(view) : StructureProfileLabel.GetAvailableLabelIds(view);
                result.Add(view, available.Cast<ObjectId>().Where(labels.Contains)
                    .Select(id => (ObjectId)Read(tr.GetObject(id, OpenMode.ForRead), "StyleId")).Distinct().ToArray());
            }
            return result;
        }
        private static bool SameProfileLabels(Dictionary<ObjectId, ObjectId[]> a, Dictionary<ObjectId, ObjectId[]> b)
        {
            return a.Count == b.Count && a.All(item => b.ContainsKey(item.Key) && new HashSet<ObjectId>(item.Value).SetEquals(b[item.Key]));
        }
        private static void CopyProfileLabels(Transaction tr, Part model, Dictionary<ObjectId, ObjectId[]> styles)
        {
            var drawn = DrawnParts(tr, model.ObjectId).FirstOrDefault();
            foreach (var view in styles)
                foreach (ObjectId style in view.Value)
                {
                    if (drawn == null) throw new InvalidOperationException("New part did not acquire its profile display.");
                    ObjectId id = model is CivilPipe ? PipeProfileLabel.Create(drawn.ObjectId, view.Key, 0.5, style) :
                        StructureProfileLabel.Create(view.Key, drawn.ObjectId, style);
                    if (id.IsNull) throw new InvalidOperationException("Could not preserve a profile label style.");
                }
        }

        private static double Distance(Polyline path, Point3d point)
        {
            var xy = new Point3d(point.X, point.Y, path.Elevation);
            Point3d closest = path.GetClosestPointTo(xy, false);
            if (closest.DistanceTo(xy) > Tolerance) throw new InvalidOperationException("A pipe endpoint is offset from its branch alignment.");
            return path.GetDistAtPoint(closest);
        }
        private static double FlatDistance(Point3d a, Point3d b) { return new Point2d(a.X, a.Y).GetDistanceTo(new Point2d(b.X, b.Y)); }
        private static bool Inside(CivilPipe pipe, Point3d p)
        {
            double a = FlatDistance(pipe.StartPoint, p), b = FlatDistance(p, pipe.EndPoint);
            return a > Tolerance && b > Tolerance && Math.Abs(a + b - FlatDistance(pipe.StartPoint, pipe.EndPoint)) < Tolerance;
        }
        private static Point3d OnGrade(Point3d point, Point3d start, Point3d end)
        {
            double dx = end.X - start.X, dy = end.Y - start.Y;
            double fraction = ((point.X - start.X) * dx + (point.Y - start.Y) * dy) / (dx * dx + dy * dy);
            return new Point3d(point.X, point.Y, start.Z + (end.Z - start.Z) * fraction);
        }
        private static void VerifyGrade(CivilPipe pipe, Point3d low, Point3d high, Edge source)
        {
            if (Math.Abs(pipe.StartPoint.Z - OnGrade(pipe.StartPoint, low, high).Z) > 0.000001 ||
                Math.Abs(pipe.EndPoint.Z - OnGrade(pipe.EndPoint, low, high).Z) > 0.000001 ||
                pipe.PartFamilyId != source.Family || pipe.PartSizeName != source.Size ||
                Math.Abs(pipe.InnerDiameterOrWidth - source.Width) > 0.000001 || Math.Abs(pipe.InnerHeight - source.Height) > 0.000001)
                throw new InvalidOperationException("A pipe's elevation, slope or size changed; update cancelled.");
            source.VerifyProperties(pipe);
        }
        private static void CopyViews(Part part, ObjectId[] profiles, ObjectId[] sections)
        {
            var current = part.GetProfileViewsDisplayingMe().Cast<ObjectId>().ToList();
            foreach (var id in profiles) if (!current.Contains(id)) part.AddToProfileView(id);
            var sectionCurrent = part.GetSectionViewsDisplayingMe().Cast<ObjectId>().ToList();
            foreach (var id in sections) if (!sectionCurrent.Contains(id)) part.AddToSectionView(id);
        }
        private static PropertyInfo ReadableProperty(Type type, string name)
        {
            for (Type current = type; current != null; current = current.BaseType)
            {
                var property = current.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
                if (property != null && property.CanRead) return property;
            }
            return null;
        }
        private static object Read(object target, string property)
        {
            var p = ReadableProperty(target.GetType(), property);
            if (p == null) throw new InvalidOperationException("This Civil 3D version does not expose " + property + ".");
            return p.GetValue(target, null);
        }
        private static double ReadDouble(object target, string name) { return Convert.ToDouble(Read(target, name), CultureInfo.InvariantCulture); }
        private static void Set(object target, string property, object value)
        {
            var p = target.GetType().GetProperty(property);
            if (p == null || !p.CanWrite) throw new InvalidOperationException("Cannot preserve " + property + " in this Civil 3D version.");
            p.SetValue(target, value, null);
        }
        private static ObjectId[] LabelStyles(Transaction tr, Part part, out bool special)
        {
            special = false;
            var ids = part is CivilPipe ? ((CivilPipe)part).GetAvailablePipeLabelIds() : ((CivilStructure)part).GetAvailableStructureLabelIds();
            var result = new List<ObjectId>();
            foreach (ObjectId id in ids)
            {
                var label = tr.GetObject(id, OpenMode.ForRead);
                var property = label.GetType().GetProperty("ProfileViewId");
                if (property != null && property.GetValue(label, null) is ObjectId && !((ObjectId)property.GetValue(label, null)).IsNull) { special = true; continue; }
                result.Add((ObjectId)Read(label, "StyleId"));
            }
            return result.Distinct().ToArray();
        }
        private static void AddLabels(Transaction tr, ObjectId id, bool pipe, ObjectId[] styles)
        {
            foreach (ObjectId style in styles)
            {
                ObjectId label = pipe ? PipeLabel.Create(id, 0.5, style) : StructureLabel.Create(id, style, ((CivilStructure)tr.GetObject(id, OpenMode.ForRead)).Position);
                if (label.IsNull) throw new InvalidOperationException("Could not create a label with the source style.");
            }
        }

        private sealed class StructureState
        {
            internal ObjectId Id, Style, Rule, Surface;
            internal Point3d Position;
            internal double Rim, Sump;
            internal int Connections;
            internal bool HasSpecialLabels;
            internal ObjectId[] LabelStyles;
            internal Dictionary<ObjectId, ObjectId[]> ProfileLabels;
            internal StructureState(Transaction tr, ObjectId id)
            {
                var s = (CivilStructure)tr.GetObject(id, OpenMode.ForRead);
                Id = id; Position = s.Position; Rim = ReadDouble(s, "RimElevation"); Sump = ReadDouble(s, "SumpElevation");
                Style = (ObjectId)Read(s, "StyleId"); Rule = s.RuleSetStyleId; Surface = s.RefSurfaceId; Connections = s.ConnectedPipesCount;
                LabelStyles = SewerStructureIntervalUpdateCommands.LabelStyles(tr, s, out HasSpecialLabels);
                ProfileLabels = ReadProfileLabels(tr, s);
            }
            internal void Verify(Transaction tr)
            {
                var s = (CivilStructure)tr.GetObject(Id, OpenMode.ForRead);
                if (s.Position.DistanceTo(Position) > Tolerance || Math.Abs(ReadDouble(s, "RimElevation") - Rim) > Tolerance ||
                    Math.Abs(ReadDouble(s, "SumpElevation") - Sump) > Tolerance || (ObjectId)Read(s, "StyleId") != Style || s.RuleSetStyleId != Rule || s.RefSurfaceId != Surface)
                    throw new InvalidOperationException("An existing retained structure changed elevation, position or style; update cancelled.");
            }
        }
        private sealed class Edge
        {
            private static readonly string[] PropertyNames = { "StyleId", "RuleSetStyleId", "RefSurfaceId", "RefAlignmentId", "LayerId", "Color", "LinetypeId", "LinetypeScale", "LineWeight", "Description", "FlowDirectionMethod", "FlowRate", "KnownFlow", "HydraulicGradeLineUp", "HydraulicGradeLineDown", "EnergyGradeLineUp", "EnergyGradeLineDown", "EntryLoss", "ExitLoss", "JunctionLoss" };
            internal ObjectId Id, Alignment, StartId, EndId, LowId, HighId, Family;
            internal string Name, Size;
            internal Point3d Start, End;
            internal double A, B, Low, High, Width, Height, PlanLength;
            internal bool OverrideRules, Special;
            internal ObjectId[] ProfileViews, SectionViews, LabelStyles;
            internal Dictionary<ObjectId, ObjectId[]> ProfileLabels;
            internal readonly Dictionary<string, object> Properties = new Dictionary<string, object>();
            internal readonly Dictionary<string, object> Design = new Dictionary<string, object>();
            internal Point3d AtLow { get { return A < B ? Start : End; } }
            internal Point3d AtHigh { get { return A < B ? End : Start; } }
            internal Edge(Transaction tr, CivilPipe p)
            {
                Id = p.ObjectId; Name = p.Name; Alignment = p.RefAlignmentId; StartId = p.StartStructureId; EndId = p.EndStructureId;
                Start = p.StartPoint; End = p.EndPoint; Family = p.PartFamilyId; Size = p.PartSizeName; Width = p.InnerDiameterOrWidth; Height = p.InnerHeight;
                PlanLength = p.Length2DCenterToCenter; OverrideRules = p.OverrideRuleSet;
                ProfileViews = p.GetProfileViewsDisplayingMe().Cast<ObjectId>().ToArray();
                SectionViews = p.GetSectionViewsDisplayingMe().Cast<ObjectId>().ToArray();
                LabelStyles = SewerStructureIntervalUpdateCommands.LabelStyles(tr, p, out Special);
                ProfileLabels = ReadProfileLabels(tr, p);
                Special |= !p.ExtensionDictionary.IsNull || p.GetAvailableSpanningPipeLabelIds().Count > 0;
                foreach (var name in new[] { "Material", "OuterHeight", "OuterDiameterOrWidth", "WallThickness", "HasManningCoefficient" })
                    Design.Add(name, Read(p, name));
                if ((bool)Design["HasManningCoefficient"]) Design.Add("ManningCoefficient", Read(p, "ManningCoefficient"));
                foreach (var name in PropertyNames)
                {
                    var property = ReadableProperty(p.GetType(), name);
                    if (property != null && p.GetType().GetProperty(name).CanWrite) Properties.Add(name, property.GetValue(p, null));
                }
            }
            internal bool Compatible(Edge next)
            {
                return !Special && !next.Special && !OverrideRules && !next.OverrideRules && (A < B) == (next.A < next.B) &&
                    Family == next.Family && Size == next.Size && Width == next.Width && Height == next.Height &&
                    Properties.All(k => next.Properties.ContainsKey(k.Key) && Equals(k.Value, next.Properties[k.Key])) &&
                    Design.Count == next.Design.Count && Design.All(k => next.Design.ContainsKey(k.Key) && Equals(k.Value, next.Design[k.Key])) &&
                    new HashSet<ObjectId>(ProfileViews).SetEquals(next.ProfileViews) && new HashSet<ObjectId>(SectionViews).SetEquals(next.SectionViews) &&
                    new HashSet<ObjectId>(LabelStyles).SetEquals(next.LabelStyles) && SameProfileLabels(ProfileLabels, next.ProfileLabels) &&
                    AtHigh.DistanceTo(next.AtLow) < 0.000001 &&
                    Math.Abs((AtHigh.Z - AtLow.Z) / (High - Low) - (next.AtHigh.Z - next.AtLow.Z) / (next.High - next.Low)) < 0.000000001 &&
                    Math.Abs(FlatDistance(AtLow, next.AtHigh) - (next.High - Low)) < Tolerance;
            }
            internal void RestoreProperties(CivilPipe pipe) { foreach (var item in Properties) Set(pipe, item.Key, item.Value); }
            internal void VerifyProperties(CivilPipe pipe)
            {
                foreach (var item in Properties.Concat(Design)) if (!Equals(Read(pipe, item.Key), item.Value)) throw new InvalidOperationException("Pipe property changed: " + item.Key);
            }
        }
        private sealed class Span
        {
            internal ObjectId Alignment, Family, Size;
            internal string Branch;
            internal StructureState Template;
            internal bool Changed;
            internal readonly List<Edge> Edges = new List<Edge>();
            internal readonly List<double> Stations = new List<double>();
            internal readonly List<NewNode> NewNodes = new List<NewNode>();
            internal double Start { get { return Edges[0].Low; } }
            internal double End { get { return Edges.Last().High; } }
            internal double Length { get { return End - Start; } }
            internal string Description { get { return Branch + ": " + Start.ToString("0.##") + "–" + End.ToString("0.##") +
                "; " + (Stations.Count - 1) + " intervals" + (Changed ? " (update)" : " (unchanged)") + "; spacing " +
                string.Join(", ", Stations.Skip(1).Select((s, i) => (s - Stations[i]).ToString("0.##")).Distinct()); } }
        }
        private sealed class NewNode { internal Point3d Point; internal double Rim, Sump; }
        private static void ResolveStructurePartChoice(
            Transaction transaction,
            CivilStructure template,
            out ObjectId familyId,
            out ObjectId sizeId)
        {
            familyId = template.PartFamilyId;
            sizeId = ObjectId.Null;

            if (familyId.IsNull || familyId.IsErased)
                throw new InvalidOperationException(
                    "The adjacent structure '" + template.Name +
                    "' does not expose a valid part family. No replacement family was guessed.");

            PartFamily family = transaction.GetObject(
                familyId,
                OpenMode.ForRead,
                false) as PartFamily;
            if (family == null)
                throw new InvalidOperationException(
                    "The adjacent structure part family could not be read.");

            string existingSizeName = template.PartSizeName ?? string.Empty;
            string normalizedExisting = NormalizePartSizeIdentity(existingSizeName);
            var exactMatches = new List<ObjectId>();
            var dimensionalMatches = new List<ObjectId>();
            var availableSizes = new List<string>();
            var dimensionalDiagnostics = new List<string>();

            Dictionary<string, double> existingDimensions;
            using (PartDataRecord existingRecord = template.PartData)
            {
                existingDimensions = ReadDimensionSignature(existingRecord);
            }

            for (int index = 0; index < family.PartSizeCount; index++)
            {
                ObjectId candidateId = family[index];
                PartSize candidate = transaction.GetObject(
                    candidateId,
                    OpenMode.ForRead,
                    false) as PartSize;
                if (candidate == null) continue;

                string candidateName = Convert.ToString(Read(candidate, "Name"), CultureInfo.InvariantCulture);
                string candidateDescription = candidate.Description ?? string.Empty;
                availableSizes.Add(
                    candidateName + (string.IsNullOrWhiteSpace(candidateDescription)
                        ? string.Empty
                        : " [" + candidateDescription + "]"));

                if (string.Equals(candidateName, existingSizeName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(candidateDescription, existingSizeName, StringComparison.OrdinalIgnoreCase))
                {
                    exactMatches.Add(candidateId);
                    continue;
                }

                // Civil 3D 2023's Structure class does not expose GetPartSizeId().
                // The visible PartSizeName can contain dimensional suffixes which
                // are not included in PartSize.Name after a catalog regeneration.
                // Compare the actual existing PartData values against the catalog
                // PartSize.SizeDataRecord instead of guessing from a partial name.
                Dictionary<string, double> candidateDimensions;
                using (PartDataRecord sizeRecord = candidate.SizeDataRecord)
                {
                    candidateDimensions = ReadDimensionSignature(sizeRecord);
                }

                int comparedFields;
                bool dimensionsMatch = DimensionSignaturesMatch(
                    existingDimensions,
                    candidateDimensions,
                    out comparedFields);

                dimensionalDiagnostics.Add(
                    candidateName + ": existing/catalogue dimension fields compared=" +
                    comparedFields.ToString(CultureInfo.InvariantCulture) +
                    (dimensionsMatch ? " MATCH" : " no match") +
                    "; existing{" + FormatDimensionSignature(existingDimensions) +
                    "}; catalogue{" + FormatDimensionSignature(candidateDimensions) + "}");

                if (dimensionsMatch)
                    dimensionalMatches.Add(candidateId);

                // Retain normalized full-name matching only as a fallback where
                // the names are genuinely equivalent. Prefix/partial-name matches
                // are deliberately not accepted.
                if (!string.IsNullOrEmpty(normalizedExisting) &&
                    (string.Equals(
                        NormalizePartSizeIdentity(candidateName),
                        normalizedExisting,
                        StringComparison.Ordinal) ||
                     string.Equals(
                        NormalizePartSizeIdentity(candidateDescription),
                        normalizedExisting,
                        StringComparison.Ordinal)))
                {
                    // A normalized name match is safe only when the dimensions
                    // also match, or when no dimensional data exists on either side.
                    if (dimensionsMatch ||
                        (existingDimensions.Count == 0 && candidateDimensions.Count == 0))
                    {
                        dimensionalMatches.Add(candidateId);
                    }
                }
            }

            // Prefer a unique exact name. Otherwise require a unique dimensional
            // signature match from the actual part data records.
            if (exactMatches.Count == 1)
            {
                sizeId = exactMatches[0];
                return;
            }

            if (exactMatches.Count > 1)
                throw new InvalidOperationException(
                    "The existing structure size '" + existingSizeName +
                    "' matched more than one catalogue size in family '" + Convert.ToString(Read(family, "Name"), CultureInfo.InvariantCulture) +
                    "'. The command stopped to avoid selecting an incorrect manhole size.");

            dimensionalMatches = dimensionalMatches.Distinct().ToList();
            if (dimensionalMatches.Count == 1)
            {
                sizeId = dimensionalMatches[0];
                return;
            }

            if (dimensionalMatches.Count > 1)
                throw new InvalidOperationException(
                    "More than one catalogue size in family '" + Convert.ToString(Read(family, "Name"), CultureInfo.InvariantCulture) +
                    "' has the same dimensional signature as existing structure '" +
                    existingSizeName + "'. The command stopped because the size is ambiguous.");

            string availableText = availableSizes.Count == 0
                ? "<family contains no available sizes>"
                : string.Join("; ", availableSizes.Take(12).ToArray());
            string diagnosticText = dimensionalDiagnostics.Count == 0
                ? "<no catalogue size records could be compared>"
                : string.Join("; ", dimensionalDiagnostics.Take(12).ToArray());

            throw new InvalidOperationException(
                "Could not resolve the existing structure size '" + existingSizeName +
                "' in family '" + Convert.ToString(Read(family, "Name"), CultureInfo.InvariantCulture) + "'. The command compared the existing structure's " +
                "PartData dimensions with each catalogue PartSize.SizeDataRecord and found no unique " +
                "dimensionally identical size. It will not substitute another size. " +
                "Existing dimension fields found: " +
                existingDimensions.Count.ToString(CultureInfo.InvariantCulture) +
                ". Available sizes (first 12): " + availableText +
                ". Dimension comparison: " + diagnosticText);
        }

        private static Dictionary<string, double> ReadDimensionSignature(PartDataRecord record)
        {
            var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            if (record == null) return values;

            PartDataField[] fields;
            try
            {
                fields = record.GetAllDataFields();
            }
            catch
            {
                return values;
            }

            if (fields == null) return values;

            foreach (PartDataField field in fields)
            {
                if (field == null) continue;

                string key = NormalizePartSizeIdentity(field.Name ?? string.Empty);
                if (!IsStructureDimensionField(key))
                    key = NormalizePartSizeIdentity(field.Description ?? string.Empty);
                if (string.IsNullOrEmpty(key) ||
                    !IsStructureDimensionField(key) ||
                    values.ContainsKey(key))
                    continue;

                double numericValue;
                if (TryConvertPartDataValue(field.Value, out numericValue))
                    values[key] = numericValue;
            }

            return values;
        }

        private static string FormatDimensionSignature(IDictionary<string, double> values)
        {
            if (values == null || values.Count == 0) return "<none>";

            return string.Join(
                ", ",
                values.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(item =>
                        item.Key + "=" + item.Value.ToString("0.########", CultureInfo.InvariantCulture))
                    .ToArray());
        }

        private static bool IsStructureDimensionField(string normalizedName)
        {
            return normalizedName.Contains("DIAMETER") ||
                   normalizedName.Contains("DIA") ||
                   normalizedName.Contains("FRAME") ||
                   normalizedName.Contains("CONE") ||
                   normalizedName.Contains("WALL") ||
                   normalizedName.Contains("FLOOR") ||
                   normalizedName.Contains("THICKNESS") ||
                   normalizedName.Contains("BARREL") ||
                   normalizedName.Contains("RISER") ||
                   normalizedName.Contains("SUMP") ||
                   normalizedName.Contains("BASE");
        }

        private static bool TryConvertPartDataValue(object value, out double result)
        {
            result = 0.0;
            if (value == null) return false;

            try
            {
                result = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                return !double.IsNaN(result) && !double.IsInfinity(result);
            }
            catch
            {
                string text = Convert.ToString(value, CultureInfo.InvariantCulture);
                if (string.IsNullOrWhiteSpace(text)) return false;

                // Some catalogues expose numeric data as text with a unit suffix.
                // Parse only the leading numeric token; do not infer or convert units.
                text = text.Trim();
                int length = 0;
                while (length < text.Length &&
                       (char.IsDigit(text[length]) ||
                        text[length] == '+' ||
                        text[length] == '-' ||
                        text[length] == '.' ||
                        text[length] == ','))
                    length++;

                if (length == 0) return false;
                string numeric = text.Substring(0, length).Replace(',', '.');
                return double.TryParse(
                           numeric,
                           NumberStyles.Float,
                           CultureInfo.InvariantCulture,
                           out result) &&
                       !double.IsNaN(result) &&
                       !double.IsInfinity(result);
            }
        }

        private static bool DimensionSignaturesMatch(
            IDictionary<string, double> existing,
            IDictionary<string, double> candidate,
            out int comparedFields)
        {
            comparedFields = 0;
            if (existing == null || candidate == null ||
                existing.Count == 0 || candidate.Count == 0)
                return false;

            // The catalogue PartSize.SizeDataRecord is the authoritative set of
            // dimensions which defines that selectable catalogue size. An
            // existing Civil 3D Structure.PartData record can additionally
            // contain instance/calculated values (for example rim-to-sump
            // height, structure diameter, riser dimensions, and floor length)
            // which are not catalogue size keys. Requiring the two dictionaries
            // to have identical counts incorrectly rejects valid existing parts.
            //
            // Therefore every dimension defined by the candidate catalogue size
            // must exist on the existing structure and match within tolerance.
            // Extra existing-instance fields are intentionally ignored.
            foreach (KeyValuePair<string, double> item in candidate)
            {
                double existingValue;
                if (!existing.TryGetValue(item.Key, out existingValue))
                    return false;

                comparedFields++;
                double tolerance = Math.Max(
                    0.000001,
                    Math.Max(Math.Abs(existingValue), Math.Abs(item.Value)) * 0.000001);

                if (Math.Abs(existingValue - item.Value) > tolerance)
                    return false;
            }

            // Require a meaningful catalogue signature, and require every
            // catalogue-defined field to match. Do not accept partial matches.
            return comparedFields >= 2 &&
                   comparedFields == candidate.Count;
        }

        private static string NormalizePartSizeIdentity(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var chars = value.Where(char.IsLetterOrDigit).ToArray();
            return new string(chars).ToUpperInvariant();
        }

    }
}

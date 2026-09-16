using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.September14HatchOuterBoundaryCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// September 14 field correction for attached hatch boundaries.
    ///
    /// The September 11 command intentionally emitted one boundary for every hatch
    /// loop. Field use showed that subdivision hatches normally represent one design
    /// footprint, so the useful result is the outside perimeter with shared internal
    /// hatch edges removed. This command leaves every source hatch untouched and
    /// creates one closed polyline per connected selected hatch cluster.
    /// </summary>
    public sealed class September14HatchOuterBoundaryCommands
    {
        private const double PointTolerance = 1e-6;
        private const double BulgeTolerance = 1e-9;

        [CommandMethod("CE_TOOLS", "CE_HATCHOUTERBOUNDARY", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void CreateOuterBoundary()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            Editor editor = document.Editor;
            PromptSelectionResult selection = editor.SelectImplied();
            if (selection.Status != PromptStatus.OK || selection.Value == null || selection.Value.Count == 0)
            {
                selection = editor.GetSelection(
                    new PromptSelectionOptions
                    {
                        MessageForAdding = "\nSelect attached hatches. CE Tools will remove shared internal edges and create the OUTER boundary: ",
                        MessageForRemoval = "\nRemove hatches from outer-boundary creation: ",
                        AllowDuplicates = false,
                        RejectObjectsFromNonCurrentSpace = true
                    },
                    new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, "HATCH") }));
            }

            if (selection.Status != PromptStatus.OK || selection.Value == null || selection.Value.Count == 0)
                return;

            int hatchCount = 0;
            int skippedLoops = 0;
            int cancelledSharedEdges = 0;
            int openChains = 0;
            var createdIds = new List<ObjectId>();

            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                var allEdges = new List<BoundaryEdge>();
                ObjectId ownerId = document.Database.CurrentSpaceId;
                double elevation = 0.0;
                bool elevationSet = false;

                foreach (ObjectId hatchId in selection.Value.GetObjectIds().Distinct())
                {
                    Hatch hatch = null;
                    try { hatch = transaction.GetObject(hatchId, OpenMode.ForRead, false) as Hatch; }
                    catch { }
                    if (hatch == null) continue;

                    hatchCount++;
                    if (hatchCount == 1) ownerId = hatch.OwnerId;
                    if (!elevationSet)
                    {
                        elevation = hatch.Elevation;
                        elevationSet = true;
                    }

                    List<HatchLoop> outerLoops = ReadOuterPolylineLoops(hatch, ref skippedLoops);
                    foreach (HatchLoop loop in outerLoops)
                        AppendLoopEdges(loop, allEdges, ref skippedLoops);
                }

                if (allEdges.Count == 0)
                {
                    editor.WriteMessage("\nCE_HATCHOUTERBOUNDARY: no supported polyline hatch perimeter loops were found.");
                    return;
                }

                List<BoundaryEdge> perimeterEdges = CancelSharedEdges(allEdges, out cancelledSharedEdges);
                List<List<DirectedBoundaryEdge>> chains = BuildClosedChains(perimeterEdges, ref openChains);
                if (chains.Count == 0)
                {
                    editor.WriteMessage(
                        "\nCE_HATCHOUTERBOUNDARY stopped safely. Shared edges were analysed, but no closed outside perimeter could be chained. Source hatches were not changed.");
                    return;
                }

                BlockTableRecord owner = transaction.GetObject(ownerId, OpenMode.ForWrite, false) as BlockTableRecord;
                if (owner == null)
                    owner = transaction.GetObject(document.Database.CurrentSpaceId, OpenMode.ForWrite, false) as BlockTableRecord;
                if (owner == null) return;

                ObjectId layerId = GetOrCreateBoundaryLayer(document.Database, transaction);
                foreach (List<DirectedBoundaryEdge> chain in chains)
                {
                    if (chain.Count < 3) continue;

                    var boundary = new Polyline(chain.Count);
                    boundary.SetDatabaseDefaults(document.Database);
                    boundary.LayerId = layerId;
                    boundary.Color = Color.FromColorIndex(ColorMethod.ByLayer, 256);
                    boundary.Elevation = elevation;

                    for (int index = 0; index < chain.Count; index++)
                    {
                        DirectedBoundaryEdge edge = chain[index];
                        boundary.AddVertexAt(index, edge.Start, edge.Bulge, 0.0, 0.0);
                    }
                    boundary.Closed = true;

                    owner.AppendEntity(boundary);
                    transaction.AddNewlyCreatedDBObject(boundary, true);
                    createdIds.Add(boundary.ObjectId);
                }

                transaction.Commit();
            }

            if (createdIds.Count > 0)
            {
                try { editor.SetImpliedSelection(createdIds.ToArray()); } catch { }
            }
            editor.Regen();

            string clusterNote = createdIds.Count == 1
                ? "One joined outside boundary was created for the attached hatch set."
                : "The selection contains more than one disconnected hatch cluster; one outside boundary was created per cluster.";
            editor.WriteMessage(
                "\nCE_HATCHOUTERBOUNDARY complete. Hatches={0}; outer boundaries={1}; shared internal edges removed={2}; unsupported loops={3}; unresolved open chains={4}. {5}",
                hatchCount,
                createdIds.Count,
                cancelledSharedEdges,
                skippedLoops,
                openChains,
                clusterNote);
        }

        private static List<HatchLoop> ReadOuterPolylineLoops(Hatch hatch, ref int skippedLoops)
        {
            var supported = new List<Tuple<int, HatchLoop>>();
            var flaggedOuter = new List<HatchLoop>();

            for (int index = 0; index < hatch.NumberOfLoops; index++)
            {
                HatchLoop loop;
                try { loop = hatch.GetLoopAt(index); }
                catch { skippedLoops++; continue; }

                if (loop == null ||
                    (loop.IsPolyline
                        ? loop.Polyline == null || loop.Polyline.Count < 2
                        : loop.Curves == null || loop.Curves.Count < 2))
                {
                    skippedLoops++;
                    continue;
                }

                supported.Add(Tuple.Create(index, loop));
                HatchLoopTypes type = loop.LoopType;
                if ((type & HatchLoopTypes.External) == HatchLoopTypes.External ||
                    (type & HatchLoopTypes.Outermost) == HatchLoopTypes.Outermost)
                    flaggedOuter.Add(loop);
            }

            if (flaggedOuter.Count > 0) return flaggedOuter;
            if (supported.Count > 0)
            {
                // AutoCAD/Civil drawings created by older workflows do not always
                // carry the External/Outermost flag. The first supported hatch loop
                // is the conventional exterior loop, so use it as the conservative
                // fallback instead of accidentally treating islands as outside edges.
                return new List<HatchLoop> { supported[0].Item2 };
            }
            return new List<HatchLoop>();
        }

        private static void AppendLoopEdges(HatchLoop loop, List<BoundaryEdge> edges, ref int skippedLoops)
        {
            if (!loop.IsPolyline)
            {
                int before = edges.Count;
                foreach (object curve in (IEnumerable)loop.Curves)
                {
                    if (curve == null) continue;
                    PropertyInfo startProperty = curve.GetType().GetProperty("StartPoint");
                    PropertyInfo endProperty = curve.GetType().GetProperty("EndPoint");
                    if (startProperty == null || endProperty == null) continue;
                    object startValue = startProperty.GetValue(curve, null);
                    object endValue = endProperty.GetValue(curve, null);
                    if (!(startValue is Point2d) || !(endValue is Point2d)) continue;
                    Point2d start = (Point2d)startValue;
                    Point2d end = (Point2d)endValue;
                    if (start.GetDistanceTo(end) <= PointTolerance) continue;
                    // Edge-defined hatches (the common associative-hatch form)
                    // previously produced no output at all. Preserve their exact
                    // vertices; curved edges are conservatively represented by
                    // their chord when a bulge is not exposed by the host API.
                    edges.Add(new BoundaryEdge(start, end, 0.0));
                }
                if (edges.Count == before) skippedLoops++;
                return;
            }

            var vertices = new List<BulgeVertex>();
            foreach (BulgeVertex vertex in loop.Polyline) vertices.Add(vertex);

            if (vertices.Count > 2 &&
                vertices[0].Vertex.GetDistanceTo(vertices[vertices.Count - 1].Vertex) <= PointTolerance)
                vertices.RemoveAt(vertices.Count - 1);

            if (vertices.Count < 3)
            {
                skippedLoops++;
                return;
            }

            for (int index = 0; index < vertices.Count; index++)
            {
                int next = (index + 1) % vertices.Count;
                Point2d start = vertices[index].Vertex;
                Point2d end = vertices[next].Vertex;
                if (start.GetDistanceTo(end) <= PointTolerance) continue;
                edges.Add(new BoundaryEdge(start, end, vertices[index].Bulge));
            }
        }

        private static List<BoundaryEdge> CancelSharedEdges(
            IEnumerable<BoundaryEdge> source,
            out int cancelledSharedEdges)
        {
            var groups = new Dictionary<EdgeKey, List<BoundaryEdge>>();
            foreach (BoundaryEdge edge in source)
            {
                EdgeKey key = new EdgeKey(edge);
                List<BoundaryEdge> list;
                if (!groups.TryGetValue(key, out list))
                {
                    list = new List<BoundaryEdge>();
                    groups.Add(key, list);
                }
                list.Add(edge);
            }

            cancelledSharedEdges = 0;
            var result = new List<BoundaryEdge>();
            foreach (KeyValuePair<EdgeKey, List<BoundaryEdge>> pair in groups)
            {
                List<BoundaryEdge> list = pair.Value;
                int cancelled = (list.Count / 2) * 2;
                cancelledSharedEdges += cancelled;
                if ((list.Count & 1) == 1)
                    result.Add(list[list.Count - 1]);
            }
            return result;
        }

        private static List<List<DirectedBoundaryEdge>> BuildClosedChains(
            IList<BoundaryEdge> edges,
            ref int openChains)
        {
            var unused = new List<BoundaryEdge>(edges);
            var result = new List<List<DirectedBoundaryEdge>>();

            while (unused.Count > 0)
            {
                BoundaryEdge seed = unused[0];
                unused.RemoveAt(0);

                var chain = new List<DirectedBoundaryEdge>
                {
                    new DirectedBoundaryEdge(seed.Start, seed.End, seed.Bulge)
                };
                Point2d first = seed.Start;
                Point2d current = seed.End;
                int safety = edges.Count + 2;

                while (!SamePoint(current, first) && safety-- > 0)
                {
                    int matchIndex = -1;
                    bool reverse = false;
                    for (int index = 0; index < unused.Count; index++)
                    {
                        if (SamePoint(unused[index].Start, current))
                        {
                            matchIndex = index;
                            break;
                        }
                        if (SamePoint(unused[index].End, current))
                        {
                            matchIndex = index;
                            reverse = true;
                            break;
                        }
                    }

                    if (matchIndex < 0) break;
                    BoundaryEdge next = unused[matchIndex];
                    unused.RemoveAt(matchIndex);
                    if (reverse)
                    {
                        chain.Add(new DirectedBoundaryEdge(next.End, next.Start, -next.Bulge));
                        current = next.Start;
                    }
                    else
                    {
                        chain.Add(new DirectedBoundaryEdge(next.Start, next.End, next.Bulge));
                        current = next.End;
                    }
                }

                if (SamePoint(current, first) && chain.Count >= 3)
                    result.Add(chain);
                else
                    openChains++;
            }

            return result;
        }

        private static bool SamePoint(Point2d left, Point2d right)
        {
            return left.GetDistanceTo(right) <= PointTolerance;
        }

        private static ObjectId GetOrCreateBoundaryLayer(Database database, Transaction transaction)
        {
            const string layerName = "CE-HATCH-BOUNDARY";
            LayerTable layers = transaction.GetObject(database.LayerTableId, OpenMode.ForRead, false) as LayerTable;
            if (layers == null) return ObjectId.Null;
            if (layers.Has(layerName)) return layers[layerName];

            layers.UpgradeOpen();
            var layer = new LayerTableRecord
            {
                Name = layerName,
                Color = Color.FromColorIndex(ColorMethod.ByAci, 7)
            };
            ObjectId id = layers.Add(layer);
            transaction.AddNewlyCreatedDBObject(layer, true);
            return id;
        }

        private sealed class BoundaryEdge
        {
            public BoundaryEdge(Point2d start, Point2d end, double bulge)
            {
                Start = start;
                End = end;
                Bulge = bulge;
            }

            public Point2d Start { get; private set; }
            public Point2d End { get; private set; }
            public double Bulge { get; private set; }
        }

        private sealed class DirectedBoundaryEdge
        {
            public DirectedBoundaryEdge(Point2d start, Point2d end, double bulge)
            {
                Start = start;
                End = end;
                Bulge = bulge;
            }

            public Point2d Start { get; private set; }
            public Point2d End { get; private set; }
            public double Bulge { get; private set; }
        }

        private struct NodeKey : IEquatable<NodeKey>, IComparable<NodeKey>
        {
            public NodeKey(Point2d point)
            {
                X = (long)Math.Round(point.X / PointTolerance);
                Y = (long)Math.Round(point.Y / PointTolerance);
            }

            public readonly long X;
            public readonly long Y;

            public int CompareTo(NodeKey other)
            {
                int x = X.CompareTo(other.X);
                return x != 0 ? x : Y.CompareTo(other.Y);
            }

            public bool Equals(NodeKey other)
            {
                return X == other.X && Y == other.Y;
            }

            public override bool Equals(object obj)
            {
                return obj is NodeKey && Equals((NodeKey)obj);
            }

            public override int GetHashCode()
            {
                unchecked { return (X.GetHashCode() * 397) ^ Y.GetHashCode(); }
            }
        }

        private struct EdgeKey : IEquatable<EdgeKey>
        {
            public EdgeKey(BoundaryEdge edge)
            {
                NodeKey first = new NodeKey(edge.Start);
                NodeKey second = new NodeKey(edge.End);
                if (first.CompareTo(second) <= 0)
                {
                    A = first;
                    B = second;
                }
                else
                {
                    A = second;
                    B = first;
                }
                BulgeMagnitude = (long)Math.Round(Math.Abs(edge.Bulge) / BulgeTolerance);
            }

            public readonly NodeKey A;
            public readonly NodeKey B;
            public readonly long BulgeMagnitude;

            public bool Equals(EdgeKey other)
            {
                return A.Equals(other.A) && B.Equals(other.B) && BulgeMagnitude == other.BulgeMagnitude;
            }

            public override bool Equals(object obj)
            {
                return obj is EdgeKey && Equals((EdgeKey)obj);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = A.GetHashCode();
                    hash = (hash * 397) ^ B.GetHashCode();
                    hash = (hash * 397) ^ BulgeMagnitude.GetHashCode();
                    return hash;
                }
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using CETools.Core;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace CETools.Civil3D
{
    /// <summary>
    /// Creates a native Civil 3D Catchment from the CE sampled catchment perimeter.
    /// The calculation remains driven by the selected TIN surface and CE D8 outlet,
    /// while the final design also appears in Civil 3D Catchments for normal review.
    /// </summary>
    internal static class FloodNativeCatchmentBridge
    {
        private const string GroupName = "CE Flood Catchments";

        internal static ObjectId TryCreate(Database database, FloodDesignResult result)
        {
            if (database == null || result == null || result.Catchment == null || result.Catchment.Count == 0)
                return ObjectId.Null;

            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            try
            {
                CivilDocument civil = CivilApplication.ActiveDocument;
                if (civil == null || civil.Styles.CatchmentStyles.Count == 0)
                {
                    Write(document, "\nCE Flood: native Civil 3D Catchment was not created because no Catchment style is available.");
                    return ObjectId.Null;
                }

                Point3dCollection boundary = BuildLargestBoundary(result.Sample, result.Catchment);
                if (boundary == null || boundary.Count < 4)
                {
                    Write(document, "\nCE Flood: native Civil 3D Catchment boundary could not be assembled; CE plan graphics were retained.");
                    return ObjectId.Null;
                }

                ObjectId groupId;
                var groups = civil.GetCatchmentGroups();
                if (groups.Contains(GroupName)) groupId = groups[GroupName];
                else groupId = CatchmentGroup.Create(database, GroupName);

                ObjectId styleId = civil.Styles.CatchmentStyles[0];
                string name = BuildUniqueName(database, groupId, result.Settings.Description);
                ObjectId catchmentId = Catchment.Create(
                    name,
                    styleId,
                    groupId,
                    result.Input.SurfaceId,
                    boundary);

                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    Catchment catchment = transaction.GetObject(
                        catchmentId,
                        OpenMode.ForWrite,
                        false) as Catchment;
                    if (catchment != null)
                    {
                        catchment.RunoffCoefficient = Math.Max(0.01, Math.Min(1.0, result.Settings.RunoffCoefficient));
                        catchment.Description = string.Format(
                            CultureInfo.InvariantCulture,
                            "CE Flood design; A={0:0.####} km2; Q{1}={2:0.###} m3/s; Tc={3:0.##} min",
                            result.AreaKm2,
                            result.Settings.DesignReturnPeriod,
                            result.Flows[result.Settings.DesignReturnPeriod],
                            result.TimeOfConcentrationMinutes);
                    }
                    transaction.Commit();
                }

                Write(document, "\nCE Flood: native Civil 3D Catchment created: " + name + ".");
                return catchmentId;
            }
            catch (System.Exception exception)
            {
                Write(document, "\nCE Flood: native Civil 3D Catchment creation was skipped. " + exception.Message);
                return ObjectId.Null;
            }
        }

        private static string BuildUniqueName(Database database, ObjectId groupId, string description)
        {
            string root = string.IsNullOrWhiteSpace(description) ? "CE Catchment" : description.Trim();
            root = root.Replace("\\", "-").Replace("/", "-");
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                CatchmentGroup group = transaction.GetObject(groupId, OpenMode.ForRead, false) as CatchmentGroup;
                if (group == null) return root;
                for (int suffix = 0; suffix < 10000; suffix++)
                {
                    string candidate = suffix == 0 ? root : root + " " + (suffix + 1).ToString(CultureInfo.InvariantCulture);
                    try
                    {
                        ObjectId existing = group.GetCatchmentId(candidate);
                        if (!existing.IsNull) continue;
                    }
                    catch
                    {
                        return candidate;
                    }
                }
            }
            return root + " " + DateTime.Now.Ticks.ToString(CultureInfo.InvariantCulture);
        }

        private static Point3dCollection BuildLargestBoundary(
            HydrologySample sample,
            IReadOnlyList<GridCell> catchment)
        {
            ISet<int> cells = new HashSet<int>(catchment.Select(item => item.Index));
            var directed = new Dictionary<GridVertex, GridVertex>();
            foreach (GridCell cell in catchment)
            {
                int r = cell.Row;
                int c = cell.Column;
                if (!Contains(sample, cells, r - 1, c)) Add(directed, new GridVertex(c, r), new GridVertex(c + 1, r));
                if (!Contains(sample, cells, r, c + 1)) Add(directed, new GridVertex(c + 1, r), new GridVertex(c + 1, r + 1));
                if (!Contains(sample, cells, r + 1, c)) Add(directed, new GridVertex(c + 1, r + 1), new GridVertex(c, r + 1));
                if (!Contains(sample, cells, r, c - 1)) Add(directed, new GridVertex(c, r + 1), new GridVertex(c, r));
            }

            var loops = new List<List<GridVertex>>();
            var unused = new HashSet<GridVertex>(directed.Keys);
            while (unused.Count > 0)
            {
                GridVertex start = unused.First();
                GridVertex current = start;
                var loop = new List<GridVertex> { start };
                int guard = directed.Count + 2;
                while (guard-- > 0)
                {
                    GridVertex next;
                    if (!directed.TryGetValue(current, out next)) break;
                    unused.Remove(current);
                    loop.Add(next);
                    current = next;
                    if (current.Equals(start)) break;
                }
                if (loop.Count >= 4 && loop[loop.Count - 1].Equals(start)) loops.Add(loop);
                else unused.Remove(start);
            }

            List<GridVertex> best = loops
                .OrderByDescending(loop => Math.Abs(SignedArea(loop)))
                .FirstOrDefault();
            if (best == null) return null;

            var points = new Point3dCollection();
            foreach (GridVertex vertex in Simplify(best))
            {
                points.Add(new Point3d(
                    sample.OriginX + vertex.Column * sample.CellSize,
                    sample.OriginY + vertex.Row * sample.CellSize,
                    0.0));
            }
            if (points.Count > 0 && !points[0].IsEqualTo(points[points.Count - 1])) points.Add(points[0]);
            return points;
        }

        private static IList<GridVertex> Simplify(IList<GridVertex> loop)
        {
            if (loop == null || loop.Count <= 4) return loop;
            var result = new List<GridVertex>();
            for (int index = 0; index < loop.Count - 1; index++)
            {
                GridVertex previous = loop[(index - 1 + loop.Count - 1) % (loop.Count - 1)];
                GridVertex current = loop[index];
                GridVertex next = loop[(index + 1) % (loop.Count - 1)];
                int dx1 = current.Column - previous.Column;
                int dy1 = current.Row - previous.Row;
                int dx2 = next.Column - current.Column;
                int dy2 = next.Row - current.Row;
                if (dx1 * dy2 == dy1 * dx2) continue;
                result.Add(current);
            }
            if (result.Count > 0) result.Add(result[0]);
            return result;
        }

        private static bool Contains(HydrologySample sample, ISet<int> cells, int row, int column)
        {
            if (row < 0 || row >= sample.Rows || column < 0 || column >= sample.Columns) return false;
            return cells.Contains(sample.Analysis.IndexOf(row, column));
        }

        private static void Add(IDictionary<GridVertex, GridVertex> edges, GridVertex start, GridVertex end)
        {
            if (!edges.ContainsKey(start)) edges[start] = end;
        }

        private static double SignedArea(IList<GridVertex> loop)
        {
            double area = 0.0;
            for (int index = 1; index < loop.Count; index++)
                area += loop[index - 1].Column * loop[index].Row - loop[index].Column * loop[index - 1].Row;
            return area * 0.5;
        }

        private static void Write(Document document, string message)
        {
            try { if (document != null) document.Editor.WriteMessage(message); } catch { }
        }

        private struct GridVertex : IEquatable<GridVertex>
        {
            public GridVertex(int column, int row) { Column = column; Row = row; }
            public int Column { get; private set; }
            public int Row { get; private set; }
            public bool Equals(GridVertex other) { return Column == other.Column && Row == other.Row; }
            public override bool Equals(object obj) { return obj is GridVertex && Equals((GridVertex)obj); }
            public override int GetHashCode() { unchecked { return Column * 397 ^ Row; } }
        }
    }
}

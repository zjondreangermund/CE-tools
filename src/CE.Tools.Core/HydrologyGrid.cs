using System;
using System.Collections.Generic;
using System.Linq;

namespace CETools.Core
{
    /// <summary>
    /// Host-independent regular-grid hydrology engine. It fills enclosed depressions
    /// with a priority-flood algorithm, derives deterministic D8 flow directions,
    /// accumulates contributing area, traces downstream routes and extracts the
    /// upstream catchment of a selected outlet cell.
    /// </summary>
    public sealed class HydrologyGrid
    {
        private static readonly int[] RowOffsets = { -1, -1, -1, 0, 0, 1, 1, 1 };
        private static readonly int[] ColumnOffsets = { -1, 0, 1, -1, 1, -1, 0, 1 };

        private readonly double[] _elevations;
        private readonly bool[] _active;

        public HydrologyGrid(
            int rows,
            int columns,
            double cellSize,
            IReadOnlyList<double> elevations,
            IReadOnlyList<bool> active = null)
        {
            if (rows < 2) throw new ArgumentOutOfRangeException(nameof(rows));
            if (columns < 2) throw new ArgumentOutOfRangeException(nameof(columns));
            if (!IsFinitePositive(cellSize))
                throw new ArgumentOutOfRangeException(nameof(cellSize));
            int count = checked(rows * columns);
            if (elevations == null || elevations.Count != count)
                throw new ArgumentException("Elevation count must equal rows × columns.", nameof(elevations));
            if (active != null && active.Count != count)
                throw new ArgumentException("Active-mask count must equal rows × columns.", nameof(active));

            Rows = rows;
            Columns = columns;
            CellSize = cellSize;
            _elevations = new double[count];
            _active = new bool[count];
            int activeCount = 0;
            for (int index = 0; index < count; index++)
            {
                bool isActive = active == null || active[index];
                double elevation = elevations[index];
                if (isActive && !IsFinite(elevation))
                    throw new ArgumentException("Every active cell requires a finite elevation.", nameof(elevations));
                _active[index] = isActive;
                _elevations[index] = elevation;
                if (isActive) activeCount++;
            }
            if (activeCount == 0)
                throw new ArgumentException("The grid requires at least one active cell.", nameof(active));
        }

        public int Rows { get; }
        public int Columns { get; }
        public double CellSize { get; }
        public int CellCount => checked(Rows * Columns);
        public double CellArea => CellSize * CellSize;

        public int IndexOf(int row, int column)
        {
            if (row < 0 || row >= Rows) throw new ArgumentOutOfRangeException(nameof(row));
            if (column < 0 || column >= Columns) throw new ArgumentOutOfRangeException(nameof(column));
            return row * Columns + column;
        }

        public GridCell CellOf(int index)
        {
            ValidateIndex(index);
            return new GridCell(index / Columns, index % Columns, index);
        }

        public bool IsActive(int row, int column)
        {
            return _active[IndexOf(row, column)];
        }

        public double ElevationAt(int row, int column)
        {
            return _elevations[IndexOf(row, column)];
        }

        public HydrologyGridAnalysis Analyse()
        {
            PriorityFloodResult flood = FillDepressions();
            int[] flowTo = BuildFlowDirections(flood.FilledElevations, flood.DrainageRank);
            double[] accumulation = Accumulate(flowTo);
            return new HydrologyGridAnalysis(
                Rows,
                Columns,
                CellSize,
                (double[])_elevations.Clone(),
                (bool[])_active.Clone(),
                flood.FilledElevations,
                flood.DrainageRank,
                flowTo,
                accumulation);
        }

        private PriorityFloodResult FillDepressions()
        {
            double[] filled = (double[])_elevations.Clone();
            int[] rank = Enumerable.Repeat(int.MaxValue, CellCount).ToArray();
            bool[] visited = new bool[CellCount];
            var heap = new HydrologyMinHeap();

            for (int row = 0; row < Rows; row++)
            {
                for (int column = 0; column < Columns; column++)
                {
                    int index = IndexOf(row, column);
                    if (!_active[index] || !IsDrainageBoundary(row, column)) continue;
                    visited[index] = true;
                    heap.Push(new HydrologyHeapItem(index, filled[index]));
                }
            }

            if (heap.Count == 0)
                throw new InvalidOperationException("The active grid has no drainage boundary.");

            int sequence = 0;
            while (heap.Count > 0)
            {
                HydrologyHeapItem current = heap.Pop();
                if (rank[current.Index] != int.MaxValue) continue;
                rank[current.Index] = sequence++;
                GridCell cell = CellOf(current.Index);

                for (int direction = 0; direction < RowOffsets.Length; direction++)
                {
                    int neighbourRow = cell.Row + RowOffsets[direction];
                    int neighbourColumn = cell.Column + ColumnOffsets[direction];
                    if (!Inside(neighbourRow, neighbourColumn)) continue;
                    int neighbour = IndexOf(neighbourRow, neighbourColumn);
                    if (!_active[neighbour] || visited[neighbour]) continue;
                    visited[neighbour] = true;
                    if (filled[neighbour] < current.Elevation)
                        filled[neighbour] = current.Elevation;
                    heap.Push(new HydrologyHeapItem(neighbour, filled[neighbour]));
                }
            }

            for (int index = 0; index < CellCount; index++)
            {
                if (_active[index] && rank[index] == int.MaxValue)
                    throw new InvalidOperationException("An active grid component could not be reached by priority flood.");
            }
            return new PriorityFloodResult(filled, rank);
        }

        private int[] BuildFlowDirections(double[] filled, int[] rank)
        {
            int[] flowTo = Enumerable.Repeat(-1, CellCount).ToArray();
            for (int index = 0; index < CellCount; index++)
            {
                if (!_active[index]) continue;
                GridCell cell = CellOf(index);
                int best = -1;
                double bestSlope = double.NegativeInfinity;
                int bestRank = int.MaxValue;

                for (int direction = 0; direction < RowOffsets.Length; direction++)
                {
                    int neighbourRow = cell.Row + RowOffsets[direction];
                    int neighbourColumn = cell.Column + ColumnOffsets[direction];
                    if (!Inside(neighbourRow, neighbourColumn)) continue;
                    int neighbour = IndexOf(neighbourRow, neighbourColumn);
                    if (!_active[neighbour]) continue;

                    double distance = RowOffsets[direction] != 0 && ColumnOffsets[direction] != 0
                        ? CellSize * Math.Sqrt(2.0)
                        : CellSize;
                    double drop = filled[index] - filled[neighbour];
                    double slope = drop / distance;
                    bool downhill = drop > 1e-12;
                    bool flatTowardOutlet = Math.Abs(drop) <= 1e-12 && rank[neighbour] < rank[index];
                    if (!downhill && !flatTowardOutlet) continue;

                    if (slope > bestSlope + 1e-15 ||
                        Math.Abs(slope - bestSlope) <= 1e-15 && rank[neighbour] < bestRank)
                    {
                        best = neighbour;
                        bestSlope = slope;
                        bestRank = rank[neighbour];
                    }
                }
                flowTo[index] = best;
            }
            return flowTo;
        }

        private double[] Accumulate(int[] flowTo)
        {
            int[] indegree = new int[CellCount];
            double[] accumulation = new double[CellCount];
            int activeCount = 0;
            for (int index = 0; index < CellCount; index++)
            {
                if (!_active[index]) continue;
                activeCount++;
                accumulation[index] = CellArea;
                int downstream = flowTo[index];
                if (downstream >= 0) indegree[downstream]++;
            }

            var queue = new Queue<int>();
            for (int index = 0; index < CellCount; index++)
                if (_active[index] && indegree[index] == 0) queue.Enqueue(index);

            int processed = 0;
            while (queue.Count > 0)
            {
                int index = queue.Dequeue();
                processed++;
                int downstream = flowTo[index];
                if (downstream < 0) continue;
                accumulation[downstream] += accumulation[index];
                indegree[downstream]--;
                if (indegree[downstream] == 0) queue.Enqueue(downstream);
            }
            if (processed != activeCount)
                throw new InvalidOperationException("The flow-direction graph contains a cycle.");
            return accumulation;
        }

        private bool IsDrainageBoundary(int row, int column)
        {
            if (row == 0 || column == 0 || row == Rows - 1 || column == Columns - 1)
                return true;
            for (int direction = 0; direction < RowOffsets.Length; direction++)
            {
                int neighbourRow = row + RowOffsets[direction];
                int neighbourColumn = column + ColumnOffsets[direction];
                if (!Inside(neighbourRow, neighbourColumn)) return true;
                if (!_active[IndexOf(neighbourRow, neighbourColumn)]) return true;
            }
            return false;
        }

        private bool Inside(int row, int column)
        {
            return row >= 0 && row < Rows && column >= 0 && column < Columns;
        }

        private void ValidateIndex(int index)
        {
            if (index < 0 || index >= CellCount)
                throw new ArgumentOutOfRangeException(nameof(index));
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static bool IsFinitePositive(double value)
        {
            return IsFinite(value) && value > 0.0;
        }
    }

    public sealed class HydrologyGridAnalysis
    {
        internal HydrologyGridAnalysis(
            int rows,
            int columns,
            double cellSize,
            double[] originalElevations,
            bool[] active,
            double[] filledElevations,
            int[] drainageRank,
            int[] flowTo,
            double[] accumulationArea)
        {
            Rows = rows;
            Columns = columns;
            CellSize = cellSize;
            OriginalElevations = originalElevations;
            Active = active;
            FilledElevations = filledElevations;
            DrainageRank = drainageRank;
            FlowTo = flowTo;
            AccumulationArea = accumulationArea;
        }

        public int Rows { get; }
        public int Columns { get; }
        public double CellSize { get; }
        public IReadOnlyList<double> OriginalElevations { get; }
        public IReadOnlyList<bool> Active { get; }
        public IReadOnlyList<double> FilledElevations { get; }
        public IReadOnlyList<int> DrainageRank { get; }
        public IReadOnlyList<int> FlowTo { get; }
        public IReadOnlyList<double> AccumulationArea { get; }

        public int IndexOf(int row, int column)
        {
            if (row < 0 || row >= Rows) throw new ArgumentOutOfRangeException(nameof(row));
            if (column < 0 || column >= Columns) throw new ArgumentOutOfRangeException(nameof(column));
            return row * Columns + column;
        }

        public GridCell CellOf(int index)
        {
            ValidateIndex(index);
            return new GridCell(index / Columns, index % Columns, index);
        }

        public double FillDepth(int index)
        {
            ValidateIndex(index);
            return Active[index]
                ? FilledElevations[index] - OriginalElevations[index]
                : 0.0;
        }

        public IReadOnlyList<GridCell> TraceRoute(int startIndex)
        {
            ValidateActiveIndex(startIndex);
            var route = new List<GridCell>();
            var visited = new HashSet<int>();
            int current = startIndex;
            while (current >= 0 && visited.Add(current))
            {
                route.Add(CellOf(current));
                current = FlowTo[current];
            }
            if (current >= 0)
                throw new InvalidOperationException("A cycle was encountered while tracing the flow route.");
            return route;
        }

        public IReadOnlyList<GridCell> DelineateCatchment(int outletIndex)
        {
            ValidateActiveIndex(outletIndex);
            var upstream = new List<int>[FlowTo.Count];
            for (int index = 0; index < FlowTo.Count; index++)
                upstream[index] = new List<int>();
            for (int index = 0; index < FlowTo.Count; index++)
            {
                int downstream = FlowTo[index];
                if (downstream >= 0) upstream[downstream].Add(index);
            }

            var result = new List<GridCell>();
            var queue = new Queue<int>();
            var visited = new HashSet<int>();
            queue.Enqueue(outletIndex);
            visited.Add(outletIndex);
            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                result.Add(CellOf(current));
                foreach (int source in upstream[current])
                {
                    if (visited.Add(source)) queue.Enqueue(source);
                }
            }
            return result.OrderBy(cell => cell.Index).ToList();
        }

        public int FindMaximumAccumulationCell()
        {
            int best = -1;
            double maximum = double.NegativeInfinity;
            for (int index = 0; index < AccumulationArea.Count; index++)
            {
                if (!Active[index]) continue;
                if (AccumulationArea[index] > maximum)
                {
                    maximum = AccumulationArea[index];
                    best = index;
                }
            }
            return best;
        }

        private void ValidateActiveIndex(int index)
        {
            ValidateIndex(index);
            if (!Active[index])
                throw new ArgumentException("The selected grid cell is inactive.", nameof(index));
        }

        private void ValidateIndex(int index)
        {
            if (index < 0 || index >= FlowTo.Count)
                throw new ArgumentOutOfRangeException(nameof(index));
        }
    }

    public readonly struct GridCell
    {
        public GridCell(int row, int column, int index)
        {
            Row = row;
            Column = column;
            Index = index;
        }

        public int Row { get; }
        public int Column { get; }
        public int Index { get; }
    }

    /// <summary>
    /// Preliminary modified-rational hydrograph generator. It produces a ramp,
    /// optional plateau and recession around the rational-method peak flow.
    /// It is intended for scenario screening, not calibrated hydrological design.
    /// </summary>
    public static class ModifiedRationalHydrograph
    {
        public static HydrographSeries Create(
            double areaHectares,
            double runoffCoefficient,
            double rainfallIntensityMillimetresPerHour,
            double timeOfConcentrationMinutes,
            double stormDurationMinutes,
            double timeStepMinutes)
        {
            if (!IsFinitePositive(areaHectares))
                throw new ArgumentOutOfRangeException(nameof(areaHectares));
            if (!IsFinite(runoffCoefficient) || runoffCoefficient <= 0.0 || runoffCoefficient > 1.0)
                throw new ArgumentOutOfRangeException(nameof(runoffCoefficient));
            if (!IsFinitePositive(rainfallIntensityMillimetresPerHour))
                throw new ArgumentOutOfRangeException(nameof(rainfallIntensityMillimetresPerHour));
            if (!IsFinitePositive(timeOfConcentrationMinutes))
                throw new ArgumentOutOfRangeException(nameof(timeOfConcentrationMinutes));
            if (!IsFinitePositive(stormDurationMinutes))
                throw new ArgumentOutOfRangeException(nameof(stormDurationMinutes));
            if (!IsFinitePositive(timeStepMinutes))
                throw new ArgumentOutOfRangeException(nameof(timeStepMinutes));

            double rationalPeak = runoffCoefficient *
                rainfallIntensityMillimetresPerHour * areaHectares / 360.0;
            double effectivePeak = stormDurationMinutes >= timeOfConcentrationMinutes
                ? rationalPeak
                : rationalPeak * stormDurationMinutes / timeOfConcentrationMinutes;
            double peakTime = Math.Min(stormDurationMinutes, timeOfConcentrationMinutes);
            double plateauEnd = Math.Max(stormDurationMinutes, timeOfConcentrationMinutes);
            double endTime = plateauEnd + timeOfConcentrationMinutes;
            int steps = checked((int)Math.Ceiling(endTime / timeStepMinutes));
            if (steps > 100000)
                throw new ArgumentOutOfRangeException(nameof(timeStepMinutes), "Hydrograph contains too many time steps.");

            var points = new List<HydrographPoint>();
            for (int step = 0; step <= steps; step++)
            {
                double time = Math.Min(step * timeStepMinutes, endTime);
                double flow;
                if (time <= peakTime)
                {
                    flow = peakTime <= 0.0 ? effectivePeak : effectivePeak * time / peakTime;
                }
                else if (time <= plateauEnd)
                {
                    flow = effectivePeak;
                }
                else
                {
                    flow = effectivePeak * (endTime - time) / timeOfConcentrationMinutes;
                    if (flow < 0.0) flow = 0.0;
                }
                points.Add(new HydrographPoint(time, flow));
                if (time >= endTime) break;
            }
            return new HydrographSeries(
                areaHectares,
                runoffCoefficient,
                rainfallIntensityMillimetresPerHour,
                timeOfConcentrationMinutes,
                stormDurationMinutes,
                effectivePeak,
                points);
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static bool IsFinitePositive(double value)
        {
            return IsFinite(value) && value > 0.0;
        }
    }

    public sealed class HydrographSeries
    {
        internal HydrographSeries(
            double areaHectares,
            double runoffCoefficient,
            double rainfallIntensityMillimetresPerHour,
            double timeOfConcentrationMinutes,
            double stormDurationMinutes,
            double peakFlowCubicMetresPerSecond,
            IReadOnlyList<HydrographPoint> points)
        {
            AreaHectares = areaHectares;
            RunoffCoefficient = runoffCoefficient;
            RainfallIntensityMillimetresPerHour = rainfallIntensityMillimetresPerHour;
            TimeOfConcentrationMinutes = timeOfConcentrationMinutes;
            StormDurationMinutes = stormDurationMinutes;
            PeakFlowCubicMetresPerSecond = peakFlowCubicMetresPerSecond;
            Points = points;
        }

        public double AreaHectares { get; }
        public double RunoffCoefficient { get; }
        public double RainfallIntensityMillimetresPerHour { get; }
        public double TimeOfConcentrationMinutes { get; }
        public double StormDurationMinutes { get; }
        public double PeakFlowCubicMetresPerSecond { get; }
        public IReadOnlyList<HydrographPoint> Points { get; }
    }

    public readonly struct HydrographPoint
    {
        public HydrographPoint(double timeMinutes, double flowCubicMetresPerSecond)
        {
            TimeMinutes = timeMinutes;
            FlowCubicMetresPerSecond = flowCubicMetresPerSecond;
        }

        public double TimeMinutes { get; }
        public double FlowCubicMetresPerSecond { get; }
    }

    internal sealed class PriorityFloodResult
    {
        public PriorityFloodResult(double[] filledElevations, int[] drainageRank)
        {
            FilledElevations = filledElevations;
            DrainageRank = drainageRank;
        }

        public double[] FilledElevations { get; }
        public int[] DrainageRank { get; }
    }

    internal readonly struct HydrologyHeapItem
    {
        public HydrologyHeapItem(int index, double elevation)
        {
            Index = index;
            Elevation = elevation;
        }

        public int Index { get; }
        public double Elevation { get; }
    }

    internal sealed class HydrologyMinHeap
    {
        private readonly List<HydrologyHeapItem> _items = new List<HydrologyHeapItem>();

        public int Count => _items.Count;

        public void Push(HydrologyHeapItem item)
        {
            _items.Add(item);
            int index = _items.Count - 1;
            while (index > 0)
            {
                int parent = (index - 1) / 2;
                if (Compare(_items[parent], _items[index]) <= 0) break;
                Swap(parent, index);
                index = parent;
            }
        }

        public HydrologyHeapItem Pop()
        {
            if (_items.Count == 0)
                throw new InvalidOperationException("The heap is empty.");
            HydrologyHeapItem result = _items[0];
            int last = _items.Count - 1;
            _items[0] = _items[last];
            _items.RemoveAt(last);
            int index = 0;
            while (true)
            {
                int left = index * 2 + 1;
                int right = left + 1;
                if (left >= _items.Count) break;
                int smallest = right < _items.Count && Compare(_items[right], _items[left]) < 0
                    ? right
                    : left;
                if (Compare(_items[index], _items[smallest]) <= 0) break;
                Swap(index, smallest);
                index = smallest;
            }
            return result;
        }

        private static int Compare(HydrologyHeapItem first, HydrologyHeapItem second)
        {
            int elevation = first.Elevation.CompareTo(second.Elevation);
            return elevation != 0 ? elevation : first.Index.CompareTo(second.Index);
        }

        private void Swap(int first, int second)
        {
            HydrologyHeapItem value = _items[first];
            _items[first] = _items[second];
            _items[second] = value;
        }
    }
}

// UC-T03-04: bounded scratch storage for the existing river tracer, reused across sources.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace UniverseCreator
{
    /// <summary>Outcome of a bounded river search; only Found carries a complete course.</summary>
    public enum RiverSearchStatus
    {
        /// <summary>A complete source-to-outlet course was found.</summary>
        Found,
        /// <summary>The reachable lattice contains no cell below sea level.</summary>
        NoOutlet,
        /// <summary>The source is already below sea level and needs no course.</summary>
        SourceSubmerged,
        /// <summary>The caller cancelled; no partial course is returned.</summary>
        Cancelled,
        /// <summary>The settled-cell budget was exhausted; reachability remains unknown.</summary>
        VisitLimitExceeded,
        /// <summary>Dimensions, settings, source or an encountered height sample are invalid.</summary>
        InvalidInput
    }

    /// <summary>One search's outcome and measured work; independent of subsequent workspace reuse.</summary>
    public readonly struct RiverSearchResult
    {
        /// <summary>Reason the search ended.</summary>
        public RiverSearchStatus Status { get; }
        /// <summary>Caller-owned cell indices, source first and outlet last; null unless Found.</summary>
        public List<int> Course { get; }
        /// <summary>Settled cells, including the outlet; never exceeds the supplied visit budget.</summary>
        public int VisitedCount { get; }
        /// <summary>Cells inserted or settled, at most the grid cell count.</summary>
        public int TouchedCount { get; }
        /// <summary>Maximum simultaneous heap entries, at most the grid cell count.</summary>
        public int PeakFrontierCount { get; }
        /// <summary>Search cost in metres; NaN when no complete course was found.</summary>
        public double Cost { get; }
        /// <summary>Observed search/reset/reconstruction time in milliseconds, excluding workspace allocation.</summary>
        public double ElapsedMilliseconds { get; }

        internal RiverSearchResult(RiverSearchStatus status, List<int> course, int visited, int touched,
                                   int peak, double cost, long start)
        {
            Status = status; Course = course; VisitedCount = visited; TouchedCount = touched;
            PeakFrontierCount = peak; Cost = cost;
            ElapsedMilliseconds = (Stopwatch.GetTimestamp() - start) * (1000.0 / Stopwatch.Frequency);
        }
    }

    /// <summary>Fixed-capacity indexed heap, costs and breadcrumbs for sequential river searches.</summary>
    /// <remarks>Owned by one course-building pass. Overlapping use is rejected; separate workspaces may run
    /// concurrently. No global pool or native resources. The production caller reserves these managed arrays
    /// under its tile memory budget before allocation. Only previously touched cells reset between searches.</remarks>
    public sealed class RiverSearchWorkspace
    {
        private readonly double[] best;
        private readonly int[] previous, heap, position, touched;
        private int heapCount, touchedCount, visitedCount, peakFrontier, active;

        /// <summary>Maximum grid cell count this workspace accepts.</summary>
        public int Capacity => best.Length;
        /// <summary>Fixed array payload in bytes, excluding object/array headers and returned course storage.</summary>
        public long PayloadBytes => (long)Capacity * 24;

        /// <summary>Allocates scratch arrays once for a positive maximum grid cell count.</summary>
        /// <param name="capacity">Maximum number of cells; must be positive and fit managed array limits.</param>
        /// <exception cref="ArgumentOutOfRangeException">Capacity is not positive.</exception>
        /// <exception cref="OutOfMemoryException">The caller cannot allocate the requested managed storage.</exception>
        public RiverSearchWorkspace(int capacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            best = new double[capacity]; previous = new int[capacity]; heap = new int[capacity];
            position = new int[capacity]; touched = new int[capacity];
        }

        internal RiverSearchResult Trace(float[] height, int resX, int resZ, int source, float sea,
                                         float penalty, double cellX, double cellZ, double heightScale,
                                         int maxVisits, Func<bool> cancelled)
        {
            long start = Stopwatch.GetTimestamp();
            if (Interlocked.CompareExchange(ref active, 1, 0) != 0)
                throw new InvalidOperationException("A river search workspace cannot be shared by overlapping searches.");
            try
            {
                // position: 0 unseen, positive heap slot + 1, -1 settled. Each cell enters touched once.
                for (int k = 0; k < touchedCount; k++) position[touched[k]] = 0;
                touchedCount = heapCount = visitedCount = peakFrontier = 0;
                if (cancelled != null && cancelled()) return Finish(RiverSearchStatus.Cancelled, start);
                if (!Finite(height[source])) return Finish(RiverSearchStatus.InvalidInput, start);
                if (height[source] < sea) return Finish(RiverSearchStatus.SourceSubmerged, start);
                Offer(source, 0.0, -1);
                double diagonal = Math.Sqrt(cellX * cellX + cellZ * cellZ);
                while (heapCount > 0)
                {
                    if (cancelled != null && cancelled()) return Finish(RiverSearchStatus.Cancelled, start);
                    if (visitedCount >= maxVisits) return Finish(RiverSearchStatus.VisitLimitExceeded, start);
                    int cell = Pop();
                    visitedCount++;
                    if (height[cell] < sea)
                    {
                        var course = new List<int>();
                        for (int p = cell; p >= 0 && course.Count < visitedCount; p = previous[p])
                        {
                            if (cancelled != null && cancelled()) return Finish(RiverSearchStatus.Cancelled, start);
                            course.Add(p);
                            if (p == source) break;
                        }
                        if (course[course.Count - 1] != source)
                            throw new InvalidOperationException("River search breadcrumbs did not reach their source.");
                        course.Reverse();
                        return new RiverSearchResult(RiverSearchStatus.Found, course, visitedCount,
                            touchedCount, peakFrontier, best[cell], start);
                    }
                    int x = cell % resX, z = cell / resX;
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        int nz = z + dz;
                        if (nz < 0 || nz >= resZ) continue;
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx;
                            if ((dx == 0 && dz == 0) || nx < 0 || nx >= resX) continue;
                            int next = nz * resX + nx;
                            if (!Finite(height[next])) return Finish(RiverSearchStatus.InvalidInput, start);
                            if (position[next] < 0) continue;
                            double climb = Math.Max(0.0, (double)height[next] - height[cell]) * heightScale;
                            double step = dx == 0 ? cellZ : dz == 0 ? cellX : diagonal;
                            double cost = best[cell] + step + climb * penalty;
                            if (position[next] == 0 || cost < best[next]) Offer(next, cost, cell);
                        }
                    }
                }
                return Finish(RiverSearchStatus.NoOutlet, start);
            }
            finally { Volatile.Write(ref active, 0); }
        }

        private RiverSearchResult Finish(RiverSearchStatus status, long start) =>
            new RiverSearchResult(status, null, visitedCount, touchedCount, peakFrontier, double.NaN, start);

        internal static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private bool Before(int a, int b) => best[a] < best[b] || (best[a] == best[b] && a < b);

        private void Offer(int cell, double cost, int parent)
        {
            best[cell] = cost; previous[cell] = parent;
            int slot = position[cell] - 1;
            if (slot < 0)
            {
                touched[touchedCount++] = cell;
                slot = heapCount++;
                heap[slot] = cell; position[cell] = slot + 1;
                if (heapCount > peakFrontier) peakFrontier = heapCount;
            }
            while (slot > 0)
            {
                int up = (slot - 1) >> 1;
                if (!Before(heap[slot], heap[up])) break;
                Swap(slot, up); slot = up;
            }
        }

        private int Pop()
        {
            int cell = heap[0];
            position[cell] = -1;
            if (--heapCount == 0) return cell;
            heap[0] = heap[heapCount]; position[heap[0]] = 1;
            int slot = 0;
            while (slot < heapCount / 2)
            {
                int left = slot * 2 + 1, right = left + 1, first = slot;
                if (left < heapCount && Before(heap[left], heap[first])) first = left;
                if (right < heapCount && Before(heap[right], heap[first])) first = right;
                if (first == slot) break;
                Swap(slot, first); slot = first;
            }
            return cell;
        }

        private void Swap(int a, int b)
        {
            int cell = heap[a]; heap[a] = heap[b]; heap[b] = cell;
            position[heap[a]] = a + 1; position[heap[b]] = b + 1;
        }
    }
}

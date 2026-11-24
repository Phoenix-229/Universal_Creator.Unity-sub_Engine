// -----------------------------------------------------------------------------
//  Universe Creator - Module 2
//  File:    Hydrology.cs
//  Purpose: Drainage solver shared by the river, lake, basin and wetland nodes.
//
//  The existing Erosion.Hydraulic gives a flow field as a side effect of eroding.
//  That is the wrong tool for asking "where does this pixel's water END UP", which
//  is what rivers, lakes and watersheds all need. This is the standard terrain
//  analysis chain instead:
//
//    FillDepressions -> FlowDirection (D8) -> FlowAccumulation -> Basins
//
//  Filling first matters. On raw noise every local dip is a sink, so accumulation
//  never grows past a few pixels and no channel ever forms. Priority-flood raises
//  each pit to its spill elevation, which is also exactly the lake surface, so the
//  same pass that makes rivers work also tells Lake Fill where the water sits.
//
//  Managed C# only: flat float[]/int[] and a binary heap. No jobs, no natives.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator
{
    public static class Hydrology
    {
        // D8: the eight neighbours, index 0..7. Kept as flat arrays because the
        // inner loops run millions of times and a Vector2Int[] would box-walk.
        private static readonly int[] NX = { -1, 0, 1, -1, 1, -1, 0, 1 };
        private static readonly int[] NZ = { -1, -1, -1, 0, 0, 1, 1, 1 };

        // Diagonals are 1/sqrt(2) as efficient per unit of drop.
        private static readonly float[] NInvDist =
        {
            0.70710678f, 1f, 0.70710678f,
            1f,              1f,
            0.70710678f, 1f, 0.70710678f
        };

        /// <summary>
        /// Priority-flood depression filling. Returns a new array where every pixel
        /// has a non-increasing path to the tile edge. Pass a small epsilon to leave
        /// a residual gradient across filled flats so D8 still has a direction to
        /// pick; zero gives true flat lake surfaces.
        /// </summary>
        public static float[] FillDepressions(float[] heights, int resX, int resZ,
                                              float epsilon, System.Func<bool> isCancelled)
        {
            int count = resX * resZ;
            float[] filled = new float[count];
            bool[] closed = new bool[count];

            for (int i = 0; i < count; i++) filled[i] = heights[i];

            MinHeap heap = new MinHeap(resX * 2 + resZ * 2 + 16);

            // Seed with the border: water can always leave the tile there.
            for (int x = 0; x < resX; x++)
            {
                Push(heap, closed, filled, x);
                Push(heap, closed, filled, (resZ - 1) * resX + x);
            }
            for (int z = 1; z < resZ - 1; z++)
            {
                Push(heap, closed, filled, z * resX);
                Push(heap, closed, filled, z * resX + resX - 1);
            }

            int guard = 0;

            while (heap.Count > 0)
            {
                if (isCancelled != null && (++guard & 4095) == 0 && isCancelled()) return filled;

                int index;
                float level;
                heap.Pop(out index, out level);

                int cx = index % resX;
                int cz = index / resX;

                for (int n = 0; n < 8; n++)
                {
                    int nx = cx + NX[n];
                    int nz = cz + NZ[n];
                    if (nx < 0 || nz < 0 || nx >= resX || nz >= resZ) continue;

                    int ni = nz * resX + nx;
                    if (closed[ni]) continue;

                    // The neighbour cannot sit below the level water reached to get
                    // here, so raise it. That raised value IS the spill elevation.
                    if (filled[ni] <= level) filled[ni] = level + epsilon;

                    closed[ni] = true;
                    heap.Push(ni, filled[ni]);
                }
            }

            return filled;
        }

        private static void Push(MinHeap heap, bool[] closed, float[] filled, int index)
        {
            if (closed[index]) return;
            closed[index] = true;
            heap.Push(index, filled[index]);
        }

        /// <summary>
        /// D8 flow direction. Each pixel gets the neighbour index (0..7) of steepest
        /// descent, or -1 where nothing is lower (an outlet or an unfilled pit).
        /// </summary>
        public static int[] FlowDirection(float[] filled, int resX, int resZ, System.Func<bool> isCancelled)
        {
            int[] dir = new int[resX * resZ];

            for (int z = 0; z < resZ; z++)
            {
                if (isCancelled != null && (z & 31) == 0 && isCancelled()) return dir;

                int row = z * resX;

                for (int x = 0; x < resX; x++)
                {
                    int i = row + x;
                    float h = filled[i];

                    int best = -1;
                    float bestDrop = 0f;

                    for (int n = 0; n < 8; n++)
                    {
                        int nx = x + NX[n];
                        int nz = z + NZ[n];
                        if (nx < 0 || nz < 0 || nx >= resX || nz >= resZ) continue;

                        // Slope, not raw drop: a diagonal neighbour is further away,
                        // so an equal drop there is a gentler gradient.
                        float drop = (h - filled[nz * resX + nx]) * NInvDist[n];
                        if (drop > bestDrop) { bestDrop = drop; best = n; }
                    }

                    dir[i] = best;
                }
            }

            return dir;
        }

        /// <summary>
        /// Flow accumulation by topological order: every pixel starts with its own
        /// rain and pushes its total to its downstream neighbour once all upstream
        /// contributors have reported. O(n), no recursion, no stack overflow on
        /// long channels.
        /// </summary>
        public static float[] FlowAccumulation(int[] dir, int resX, int resZ,
                                               float[] weights, System.Func<bool> isCancelled)
        {
            int count = resX * resZ;
            float[] accum = new float[count];
            int[] inDegree = new int[count];

            for (int i = 0; i < count; i++)
                accum[i] = weights != null ? Mathf.Max(0f, weights[i]) : 1f;

            for (int z = 0; z < resZ; z++)
            {
                int row = z * resX;
                for (int x = 0; x < resX; x++)
                {
                    int d = dir[row + x];
                    if (d < 0) continue;

                    int nx = x + NX[d];
                    int nz = z + NZ[d];
                    if (nx < 0 || nz < 0 || nx >= resX || nz >= resZ) continue;

                    inDegree[nz * resX + nx]++;
                }
            }

            // Start from ridge pixels - the ones nothing drains into.
            int[] queue = new int[count];
            int head = 0, tail = 0;
            for (int i = 0; i < count; i++)
                if (inDegree[i] == 0) queue[tail++] = i;

            int guard = 0;

            while (head < tail)
            {
                if (isCancelled != null && (++guard & 8191) == 0 && isCancelled()) return accum;

                int i = queue[head++];
                int d = dir[i];
                if (d < 0) continue;

                int x = i % resX;
                int z = i / resX;
                int nx = x + NX[d];
                int nz = z + NZ[d];
                if (nx < 0 || nz < 0 || nx >= resX || nz >= resZ) continue;

                int ni = nz * resX + nx;
                accum[ni] += accum[i];

                if (--inDegree[ni] == 0 && tail < count) queue[tail++] = ni;
            }

            return accum;
        }

        /// <summary>
        /// Labels each pixel with the id of the outlet it drains to. Pixels sharing
        /// an outlet form one drainage basin.
        /// </summary>
        public static int[] Basins(int[] dir, int resX, int resZ, out int basinCount,
                                   System.Func<bool> isCancelled)
        {
            int count = resX * resZ;
            int[] label = new int[count];
            for (int i = 0; i < count; i++) label[i] = -1;

            int[] path = new int[count];
            basinCount = 0;

            for (int start = 0; start < count; start++)
            {
                if (label[start] >= 0) continue;
                if (isCancelled != null && (start & 8191) == 0 && isCancelled()) return label;

                int pathLength = 0;
                int cursor = start;
                int resolved = -1;

                // Walk downstream until we hit an outlet or a pixel already labelled.
                while (cursor >= 0 && pathLength < count)
                {
                    if (label[cursor] >= 0) { resolved = label[cursor]; break; }

                    path[pathLength++] = cursor;

                    int d = dir[cursor];
                    if (d < 0) { resolved = basinCount++; break; }

                    int x = cursor % resX;
                    int z = cursor / resX;
                    int nx = x + NX[d];
                    int nz = z + NZ[d];
                    if (nx < 0 || nz < 0 || nx >= resX || nz >= resZ) { resolved = basinCount++; break; }

                    int ni = nz * resX + nx;

                    // A cycle can only appear on a perfectly flat filled surface.
                    // Treat it as its own basin rather than looping forever.
                    if (ni == cursor) { resolved = basinCount++; break; }

                    cursor = ni;
                }

                if (resolved < 0) resolved = basinCount++;
                for (int p = 0; p < pathLength; p++) label[path[p]] = resolved;
            }

            return label;
        }

        /// <summary>
        /// Water depth from filling: how far the spill surface sits above the real
        /// ground. Positive only inside a depression, which is precisely a lake.
        /// </summary>
        public static float[] PondedDepth(float[] original, float[] filled, int count)
        {
            float[] depth = new float[count];
            for (int i = 0; i < count; i++)
            {
                float d = filled[i] - original[i];
                depth[i] = d > 0f ? d : 0f;
            }
            return depth;
        }

        /// <summary>
        /// Traces channel centrelines: pixels above the accumulation threshold,
        /// walked downstream from each source so the result is connected rather
        /// than a scatter of isolated high-accumulation dots.
        /// </summary>
        public static List<List<int>> TraceChannels(int[] dir, float[] accum, int resX, int resZ,
                                                    float threshold, int maxChannels,
                                                    System.Func<bool> isCancelled)
        {
            List<List<int>> channels = new List<List<int>>();
            int count = resX * resZ;
            bool[] visited = new bool[count];

            for (int i = 0; i < count && channels.Count < maxChannels; i++)
            {
                if (accum[i] < threshold || visited[i]) continue;
                if (isCancelled != null && (i & 8191) == 0 && isCancelled()) return channels;

                // A source is a channel pixel with no channel-grade pixel draining in.
                if (HasUpstreamChannel(dir, accum, resX, resZ, i, threshold)) continue;

                List<int> channel = new List<int>();
                int cursor = i;
                int guard = 0;

                while (cursor >= 0 && guard++ < count)
                {
                    channel.Add(cursor);
                    if (visited[cursor] && channel.Count > 1) break;
                    visited[cursor] = true;

                    int d = dir[cursor];
                    if (d < 0) break;

                    int x = cursor % resX;
                    int z = cursor / resX;
                    int nx = x + NX[d];
                    int nz = z + NZ[d];
                    if (nx < 0 || nz < 0 || nx >= resX || nz >= resZ) break;

                    cursor = nz * resX + nx;
                }

                // Two cells is one real reach and gets kept. Discarding it used to
                // leave a GAP: the cells are already marked visited above, so no later
                // source can retrace them, and a short connector between a side channel
                // and the trunk it joins simply vanished from the network. Every such
                // connector is a cell or two of missing trench in the middle of an
                // otherwise continuous course - the reported "not continuous but zigzag
                // hole". Only a single cell is dropped now, because one point has no
                // reach to draw.
                if (channel.Count > 1) channels.Add(channel);
            }

            return channels;
        }

        private static bool HasUpstreamChannel(int[] dir, float[] accum, int resX, int resZ,
                                               int index, float threshold)
        {
            int x = index % resX;
            int z = index / resX;

            for (int n = 0; n < 8; n++)
            {
                int nx = x + NX[n];
                int nz = z + NZ[n];
                if (nx < 0 || nz < 0 || nx >= resX || nz >= resZ) continue;

                int ni = nz * resX + nx;
                if (accum[ni] < threshold) continue;
                if (dir[ni] < 0) continue;

                // Does that neighbour actually drain into us?
                if (nx + NX[dir[ni]] == x && nz + NZ[dir[ni]] == z) return true;
            }

            return false;
        }

        public static void NeighbourOffset(int index, out int dx, out int dz)
        {
            if (index < 0 || index > 7) { dx = 0; dz = 0; return; }
            dx = NX[index];
            dz = NZ[index];
        }

        /// <summary>
        /// Flat cell index a D8 direction CODE points at, or -1 when the cell has no
        /// downhill neighbour (code -1) or the step leaves the grid.
        ///
        /// A `dir` array does NOT hold cell indices. <see cref="FlowDirection"/> stores the
        /// neighbour index 0..7 in the NX/NZ order above, and every Hydrology pass reads it
        /// that way. A consumer that uses the value as a cell index does not fail loudly -
        /// codes 0..7 are valid indices, so it silently reads a cell on the FIRST ROW
        /// instead: the height difference is negative or zero, the slope comes out 0, and
        /// any slope-driven feature quietly does nothing. That is exactly how the river
        /// network came back empty on every world while its hand-built test worlds, which
        /// also wrote flat indices, passed.
        /// </summary>
        public static int DownstreamCell(int[] dir, int index, int resX, int resZ)
        {
            if (dir == null || index < 0 || index >= dir.Length) return -1;
            if (resX < 1 || resZ < 1) return -1;

            int d = dir[index];
            if (d < 0 || d > 7) return -1;

            int x = index % resX;
            int z = index / resX;
            int nx = x + NX[d];
            int nz = z + NZ[d];
            if (nx < 0 || nz < 0 || nx >= resX || nz >= resZ) return -1;

            return nz * resX + nx;
        }

        /// <summary>
        /// Binary min-heap over (index, priority). Written here rather than using
        /// SortedList because priority-flood pushes every pixel exactly once and the
        /// allocation churn of a comparer-based container dominates the solve.
        /// </summary>
        private class MinHeap
        {
            private int[] indices;
            private float[] priorities;
            private int count;

            public MinHeap(int capacity)
            {
                if (capacity < 16) capacity = 16;
                indices = new int[capacity];
                priorities = new float[capacity];
            }

            public int Count { get { return count; } }

            public void Push(int index, float priority)
            {
                if (count == indices.Length) Grow();

                int c = count++;
                indices[c] = index;
                priorities[c] = priority;

                while (c > 0)
                {
                    int parent = (c - 1) >> 1;
                    if (priorities[parent] <= priorities[c]) break;
                    Swap(parent, c);
                    c = parent;
                }
            }

            public void Pop(out int index, out float priority)
            {
                index = indices[0];
                priority = priorities[0];

                count--;
                if (count > 0)
                {
                    indices[0] = indices[count];
                    priorities[0] = priorities[count];

                    int c = 0;
                    while (true)
                    {
                        int left = c * 2 + 1;
                        if (left >= count) break;

                        int right = left + 1;
                        int smallest = (right < count && priorities[right] < priorities[left]) ? right : left;

                        if (priorities[c] <= priorities[smallest]) break;
                        Swap(c, smallest);
                        c = smallest;
                    }
                }
            }

            private void Grow()
            {
                int size = indices.Length * 2;
                int[] ni = new int[size];
                float[] np = new float[size];
                System.Array.Copy(indices, ni, count);
                System.Array.Copy(priorities, np, count);
                indices = ni;
                priorities = np;
            }

            private void Swap(int a, int b)
            {
                int ti = indices[a]; indices[a] = indices[b]; indices[b] = ti;
                float tp = priorities[a]; priorities[a] = priorities[b]; priorities[b] = tp;
            }
        }
    }
}

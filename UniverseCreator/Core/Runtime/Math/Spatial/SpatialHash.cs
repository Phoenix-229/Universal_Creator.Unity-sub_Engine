// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 2: PCG Extended Capability Port
//
//  File:    SpatialHash.cs
//  Purpose: Uniform-grid spatial index over world XZ positions. Answers "is
//           anything within r of this point" and "what is nearest to this point"
//           in roughly constant time.
//
//  WHY THIS EXISTS
//  ---------------
//  Min-spacing rejection is the whole cost of scatter placement. Comparing each
//  candidate against every accepted point is O(n^2): a single chunk asking for
//  20k grass instances is 400M distance tests, which stalls the worker for
//  seconds. Bucketing by a cell the size of the spacing radius means each query
//  looks at a 3x3 neighbourhood - a handful of points regardless of how many
//  have been accepted overall.
//
//  Insert-only by design. Scatter builds an index once and never removes from
//  it, and supporting removal would mean either tombstones or per-cell
//  compaction for a case that does not arise.
//
//  Managed C# on int[]/float[]. No NativeArray, no jobs; safe on a worker Task.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator
{
    /// <summary>
    /// Uniform grid over a world-space XZ rectangle, indexing inserted points by
    /// cell for radius and nearest-neighbour queries.
    /// </summary>
    public class SpatialHash
    {
        // Cells hold indices into the points list rather than positions, so a
        // caller can carry its own parallel arrays (scale, prototype, mask value)
        // and look them up by the same index the query returns.
        private readonly List<int>[] cells;
        private readonly List<Vector2> points = new List<Vector2>();

        private readonly float originX;
        private readonly float originZ;
        private readonly float cellSize;
        private readonly float invCellSize;
        private readonly int cellsX;
        private readonly int cellsZ;

        /// <summary>Number of points inserted so far.</summary>
        public int Count { get { return points.Count; } }

        /// <summary>Edge length of one grid cell, world metres.</summary>
        public float CellSize { get { return cellSize; } }

        /// <summary>
        /// Builds an index covering the given world rectangle.
        /// </summary>
        /// <param name="worldOriginX">Minimum world X covered.</param>
        /// <param name="worldOriginZ">Minimum world Z covered.</param>
        /// <param name="worldWidth">Extent along X, world metres.</param>
        /// <param name="worldDepth">Extent along Z, world metres.</param>
        /// <param name="cellSize">
        /// Should equal the query radius you will use most. Smaller means more
        /// cells to scan per query; larger means more points per cell.
        /// </param>
        public SpatialHash(float worldOriginX, float worldOriginZ,
                           float worldWidth, float worldDepth, float cellSize)
        {
            this.originX = worldOriginX;
            this.originZ = worldOriginZ;
            this.cellSize = Mathf.Max(1e-3f, cellSize);
            this.invCellSize = 1f / this.cellSize;

            // Grid is capped: a 40 km world asked for at 0.5 m spacing would
            // otherwise allocate 6.4 billion cells. Above the cap the grid gets
            // coarser than ideal and queries scan more points, which is slower
            // but still correct - unlike an allocation that throws.
            const int MaxCellsPerAxis = 2048;

            cellsX = Mathf.Clamp(Mathf.CeilToInt(worldWidth * invCellSize) + 1, 1, MaxCellsPerAxis);
            cellsZ = Mathf.Clamp(Mathf.CeilToInt(worldDepth * invCellSize) + 1, 1, MaxCellsPerAxis);

            cells = new List<int>[cellsX * cellsZ];
        }

        /// <summary>Cell column for a world X, clamped into the grid.</summary>
        private int CellX(float worldX)
        {
            int cx = (int)((worldX - originX) * invCellSize);
            if (cx < 0) return 0;
            if (cx >= cellsX) return cellsX - 1;
            return cx;
        }

        /// <summary>Cell row for a world Z, clamped into the grid.</summary>
        private int CellZ(float worldZ)
        {
            int cz = (int)((worldZ - originZ) * invCellSize);
            if (cz < 0) return 0;
            if (cz >= cellsZ) return cellsZ - 1;
            return cz;
        }

        /// <summary>
        /// Adds a point and returns its index. The index is stable and is what
        /// radius and nearest queries report back.
        /// </summary>
        public int Insert(float worldX, float worldZ)
        {
            int index = points.Count;
            points.Add(new Vector2(worldX, worldZ));

            int flat = CellZ(worldZ) * cellsX + CellX(worldX);

            // Lists are allocated lazily. A scatter pass over a sparse mask
            // touches a small fraction of cells, and pre-filling every one costs
            // more than the whole query workload.
            List<int> bucket = cells[flat];
            if (bucket == null)
            {
                bucket = new List<int>(4);
                cells[flat] = bucket;
            }
            bucket.Add(index);

            return index;
        }

        public Vector2 GetPoint(int index)
        {
            if (index < 0 || index >= points.Count) return Vector2.zero;
            return points[index];
        }

        /// <summary>
        /// True if any inserted point lies within <paramref name="radius"/> of the
        /// given position. This is the min-spacing test, and the reason the whole
        /// class exists.
        /// </summary>
        public bool HasNeighbourWithin(float worldX, float worldZ, float radius)
        {
            if (points.Count == 0) return false;

            float sqrRadius = radius * radius;

            // A radius larger than one cell has to reach further than the
            // immediate ring, or points just outside it are missed and the
            // spacing guarantee quietly breaks.
            int span = Mathf.Max(1, Mathf.CeilToInt(radius * invCellSize));

            int cx = CellX(worldX);
            int cz = CellZ(worldZ);

            int minX = Mathf.Max(0, cx - span);
            int maxX = Mathf.Min(cellsX - 1, cx + span);
            int minZ = Mathf.Max(0, cz - span);
            int maxZ = Mathf.Min(cellsZ - 1, cz + span);

            for (int gz = minZ; gz <= maxZ; gz++)
            {
                int rowStart = gz * cellsX;
                for (int gx = minX; gx <= maxX; gx++)
                {
                    List<int> bucket = cells[rowStart + gx];
                    if (bucket == null) continue;

                    for (int i = 0; i < bucket.Count; i++)
                    {
                        Vector2 p = points[bucket[i]];
                        float dx = p.x - worldX;
                        float dz = p.y - worldZ;
                        if (dx * dx + dz * dz < sqrRadius) return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Index of the nearest inserted point within <paramref name="maxRadius"/>,
        /// or -1 if none. <paramref name="sqrDistance"/> reports how far, so a
        /// caller weighting by proximity does not have to recompute it.
        /// </summary>
        public int FindNearest(float worldX, float worldZ, float maxRadius, out float sqrDistance)
        {
            sqrDistance = float.MaxValue;
            if (points.Count == 0) return -1;

            float sqrMax = maxRadius * maxRadius;
            int span = Mathf.Max(1, Mathf.CeilToInt(maxRadius * invCellSize));

            int cx = CellX(worldX);
            int cz = CellZ(worldZ);

            int minX = Mathf.Max(0, cx - span);
            int maxX = Mathf.Min(cellsX - 1, cx + span);
            int minZ = Mathf.Max(0, cz - span);
            int maxZ = Mathf.Min(cellsZ - 1, cz + span);

            int best = -1;

            for (int gz = minZ; gz <= maxZ; gz++)
            {
                int rowStart = gz * cellsX;
                for (int gx = minX; gx <= maxX; gx++)
                {
                    List<int> bucket = cells[rowStart + gx];
                    if (bucket == null) continue;

                    for (int i = 0; i < bucket.Count; i++)
                    {
                        int idx = bucket[i];
                        Vector2 p = points[idx];
                        float dx = p.x - worldX;
                        float dz = p.y - worldZ;
                        float d = dx * dx + dz * dz;

                        if (d >= sqrMax || d >= sqrDistance) continue;
                        sqrDistance = d;
                        best = idx;
                    }
                }
            }

            if (best < 0) sqrDistance = float.MaxValue;
            return best;
        }

        /// <summary>
        /// Appends the indices of every point within <paramref name="radius"/> to
        /// <paramref name="results"/>. The caller owns the list so a loop over
        /// many queries can clear and reuse one allocation.
        /// </summary>
        public void QueryRadius(float worldX, float worldZ, float radius, List<int> results)
        {
            if (results == null || points.Count == 0) return;

            float sqrRadius = radius * radius;
            int span = Mathf.Max(1, Mathf.CeilToInt(radius * invCellSize));

            int cx = CellX(worldX);
            int cz = CellZ(worldZ);

            int minX = Mathf.Max(0, cx - span);
            int maxX = Mathf.Min(cellsX - 1, cx + span);
            int minZ = Mathf.Max(0, cz - span);
            int maxZ = Mathf.Min(cellsZ - 1, cz + span);

            for (int gz = minZ; gz <= maxZ; gz++)
            {
                int rowStart = gz * cellsX;
                for (int gx = minX; gx <= maxX; gx++)
                {
                    List<int> bucket = cells[rowStart + gx];
                    if (bucket == null) continue;

                    for (int i = 0; i < bucket.Count; i++)
                    {
                        int idx = bucket[i];
                        Vector2 p = points[idx];
                        float dx = p.x - worldX;
                        float dz = p.y - worldZ;
                        if (dx * dx + dz * dz < sqrRadius) results.Add(idx);
                    }
                }
            }
        }

        /// <summary>
        /// Number of points within a radius. Cheaper than QueryRadius when only
        /// the count matters, which is what a local-density weighting needs.
        /// </summary>
        public int CountWithin(float worldX, float worldZ, float radius)
        {
            if (points.Count == 0) return 0;

            float sqrRadius = radius * radius;
            int span = Mathf.Max(1, Mathf.CeilToInt(radius * invCellSize));

            int cx = CellX(worldX);
            int cz = CellZ(worldZ);

            int minX = Mathf.Max(0, cx - span);
            int maxX = Mathf.Min(cellsX - 1, cx + span);
            int minZ = Mathf.Max(0, cz - span);
            int maxZ = Mathf.Min(cellsZ - 1, cz + span);

            int total = 0;

            for (int gz = minZ; gz <= maxZ; gz++)
            {
                int rowStart = gz * cellsX;
                for (int gx = minX; gx <= maxX; gx++)
                {
                    List<int> bucket = cells[rowStart + gx];
                    if (bucket == null) continue;

                    for (int i = 0; i < bucket.Count; i++)
                    {
                        Vector2 p = points[bucket[i]];
                        float dx = p.x - worldX;
                        float dz = p.y - worldZ;
                        if (dx * dx + dz * dz < sqrRadius) total++;
                    }
                }
            }

            return total;
        }
    }
}

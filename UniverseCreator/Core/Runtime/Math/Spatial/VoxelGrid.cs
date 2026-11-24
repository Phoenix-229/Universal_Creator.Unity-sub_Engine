// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  File:    VoxelGrid.cs
//  Purpose: PERF-4. The CHEAP mode of the SE-3 spatial index: a voxel key,
//           floor(worldPos / cell) packed into one uint64, for O(1) grid
//           thinning and dedup on constrained hardware.
//
//  WHY A SECOND MODE AND NOT A REPLACEMENT
//  ---------------------------------------
//  SE-3's SpatialHash is a DENSE grid: one List slot per cell, allocated as
//  cellsX * cellsZ up front. That is the right shape for bounded-radius
//  neighbour queries, because scanning a 3x3 block of cells is pointer
//  arithmetic. But it pays for the whole rectangle whether or not anything is in
//  it, and it clamps at MaxCellsPerAxis - so on a large world, or with a cell
//  small enough for fine thinning, the grid silently gets coarser than asked.
//
//  This mode answers a different and much narrower question: "has anything been
//  placed in this cell yet". For that, the dense rectangle is all cost and no
//  benefit. A dictionary keyed by the packed cell costs memory proportional to
//  the cells that are actually OCCUPIED, never coarsens, and has no axis cap, so
//  the cell size requested is the cell size used. Two points a hundred
//  kilometres apart cost two entries here and a vast empty rectangle there.
//
//  WHAT IT DELIBERATELY CANNOT DO
//  ------------------------------
//  It cannot answer a radius query. One cell lookup is not a neighbourhood: two
//  points either side of a cell boundary are a millimetre apart and in different
//  cells, so using this for spacing would let pairs through that a radius test
//  rejects. Spacing and exclusion stay on SpatialHash. This is for thinning and
//  dedup, where "one per cell" IS the rule being applied rather than an
//  approximation of a radius.
//
//  FLOOR, NOT TRUNCATION
//  ---------------------
//  Casting to int truncates toward zero, so x = -0.5 and x = +0.5 would both
//  land in cell 0 with a 1 m cell - two points on opposite sides of the origin
//  silently merged into one. Every world whose origin is not its corner has
//  negative coordinates, so this is not an edge case. FloorToInt is used
//  throughout and the gate pins it.
// -----------------------------------------------------------------------------

// NAMESPACE: flat `UniverseCreator`, matching SpatialHash beside it rather than the
// folder path. A `UniverseCreator.Math` namespace would be found before `System` when
// resolving a bare `Math.Max(...)` anywhere under UniverseCreator, which silently breaks
// unrelated files - it did exactly that to UCMarkdown.cs the first time this shipped.

using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator
{
    /// <summary>
    /// Sparse voxel occupancy keyed on <c>floor(worldPos / cellSize)</c>. The cheap
    /// mode of the shared scatter index (PERF-4): O(1) insert and test, memory
    /// proportional to occupied cells.
    /// </summary>
    public class VoxelGrid
    {
        private readonly HashSet<ulong> occupied = new HashSet<ulong>();
        private readonly float cellSize;
        private readonly float invCellSize;

        /// <summary>Edge length of one voxel, in world metres.</summary>
        public float CellSize { get { return cellSize; } }

        /// <summary>How many distinct cells hold something.</summary>
        public int Count { get { return occupied.Count; } }

        public VoxelGrid(float cellSize)
        {
            // Clamped rather than rejected: a zero or negative cell would divide by
            // zero and produce one key for the whole world, which reads as "every
            // point is a duplicate" and would thin a world down to a single instance.
            this.cellSize = Mathf.Max(1e-3f, cellSize);
            this.invCellSize = 1f / this.cellSize;
        }

        /// <summary>
        /// The voxel key for a world position. Static and pure, so a caller can key
        /// its own table without building a grid - and so the packing can be pinned
        /// by a gate on its own.
        ///
        /// The two signed cell indices are packed whole into the two halves of the
        /// uint64, which cannot collide: distinct (cx, cz) pairs give distinct keys
        /// for every int32 value, where hashing or adding them would not.
        /// </summary>
        public static ulong Key(float worldX, float worldZ, float cellSize)
        {
            float inv = 1f / Mathf.Max(1e-3f, cellSize);

            // FloorToInt, never a cast: see the header. Truncation merges the cells
            // either side of zero.
            int cx = Mathf.FloorToInt(worldX * inv);
            int cz = Mathf.FloorToInt(worldZ * inv);

            return ((ulong)(uint)cx << 32) | (uint)cz;
        }

        /// <summary>This grid's key for a position.</summary>
        public ulong KeyFor(float worldX, float worldZ)
        {
            int cx = Mathf.FloorToInt(worldX * invCellSize);
            int cz = Mathf.FloorToInt(worldZ * invCellSize);
            return ((ulong)(uint)cx << 32) | (uint)cz;
        }

        /// <summary>
        /// Claims the cell this position falls in. Returns true when the cell was
        /// free - meaning KEEP this point - and false when something is already
        /// there, meaning THIN it out.
        ///
        /// This single call is the whole thinning pass: walk the candidates once,
        /// keep the ones that return true. No sorting, no distance maths, no second
        /// structure.
        /// </summary>
        public bool TryClaim(float worldX, float worldZ)
        {
            return occupied.Add(KeyFor(worldX, worldZ));
        }

        /// <summary>Whether this position's cell is already claimed.</summary>
        public bool IsClaimed(float worldX, float worldZ)
        {
            return occupied.Contains(KeyFor(worldX, worldZ));
        }

        /// <summary>Releases every cell, so the grid can be reused for the next pass.</summary>
        public void Clear()
        {
            occupied.Clear();
        }
    }
}

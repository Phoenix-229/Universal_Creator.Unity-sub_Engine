// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 1: Terrain Core
//
//  File:    WorldDrainage.cs
//  Purpose: One drainage solve for the whole world, plus the means to read it back
//           at a chunk's own resolution. Every hydrology node that asks "where does
//           the water go" goes through here instead of solving its own chunk.
//
//  WHY NOT PER CHUNK
//  -----------------
//  Depression filling, flow direction, flow accumulation and basin labelling are
//  all GLOBAL: the spill elevation of a lake depends on the whole rim, the land
//  draining through a cell can begin tens of chunks upstream, and a basin id only
//  means anything if every chunk numbers the basins the same way. Solved chunk by
//  chunk each of those restarts at the seam, which is what a user sees as a lake
//  with a step across the middle, rivers broken into per-chunk veins, and basin
//  shading that changes at every tile border. No ghost margin fixes it - the
//  neighbourhood is unbounded, not merely wide.
//
//  WHY THE SOLVE IS COARSE AND THE READ IS FINE
//  --------------------------------------------
//  The quantities above are decided by the shape of the landscape, not its last
//  metre: at 256 cells per side over a large world the water still leaves each
//  cell the same way. What DOES need full resolution is the ground the water is
//  compared against - a shoreline is only as sharp as the heightmap under it. So
//  the surface is solved coarse and read back per chunk pixel, while the ground it
//  is subtracted from stays at chunk resolution.
//
//  CACHED PER BAKE, NOT PER NODE AND NOT ACROSS EDITS (Step 42)
//  ------------------------------------------------------------
//  The original design cached nothing, for two good reasons: two DIFFERENT nodes
//  asking for drainage may each pull their OWN input branch, so a result shared
//  between nodes could hand one node the other's terrain; and any key short of
//  hashing the whole upstream graph would go stale the moment a slider moved.
//
//  UC-T02-03: captured upstream identity/port/content, world revision and geometry,
//  resolution, epsilon and flow demand identify the solve. Different consumers of
//  the SAME captured input share one in-flight computation. The world owns that
//  computation; cancelling one requester cannot remove another's needed result.
//  Failed/cancelled computations are removed for retry. An absent cache retains
//  the standalone numerical path; a cached miss must run on a worker or be awaited.
//
//  Managed C# over float[] and int[]. No Unity API beyond Mathf and Vector2; safe
//  on a worker Task. UC-T01-12: arrays remain read-only by ownership contract after
//  publication; readonly fields alone do not make their contents immutable.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.Nodes
{
    /// <summary>
    /// Per-bake memo of whole-world drainage solves. One instance is created per bake
    /// (beside the ecology snapshot) and handed to every chunk's <see cref="TileData"/>,
    /// so a solve computed for the first chunk is reused byte-for-byte by the rest
    /// instead of recomputed 16+ (900+) times for the one answer.
    ///
    /// Keyed by the exact call-site parameters so two different nodes - or the same
    /// node asked for two different things - never receive each other's surface. Lives
    /// for one bake: a graph edit starts a fresh RebuildAll with a fresh empty cache,
    /// which is why a slider move can never serve a stale result.
    ///
    /// UC-T02-03: one in-flight owner computes each full input key outside the lock.
    /// Other requesters share its immutable result; requester cancellation does not cancel a sibling's solve.
    /// </summary>
    public sealed partial class WorldDrainageCache
    {
        private readonly Dictionary<string, WorldDrainage> map = new Dictionary<string, WorldDrainage>();
        private readonly object gate = new object();

        /// <summary>Reads a completed immutable solve; in-flight, absent and failed solves return false.</summary>
        /// <param name="key">Non-null full input key.</param>
        /// <param name="value">Completed result, or null.</param>
        /// <returns>True when a completed result exists.</returns>
        public bool TryGet(string key, out WorldDrainage value)
        {
            lock (gate) { return map.TryGetValue(key, out value); }
        }

        /// <summary>Stores an externally completed immutable result; null results are ignored.</summary>
        /// <param name="key">Non-null full input key.</param>
        /// <param name="value">Completed result owned by this bake, or null.</param>
        public void Put(string key, WorldDrainage value)
        {
            if (value == null) return;
            lock (gate) { StoreCompleted(key, value); }
        }
    }


    /// <summary>How a coarse world-scale field is read at chunk resolution.</summary>
    public enum WorldRead
    {
        /// <summary>
        /// Smooth blend of the four enclosing cells. For continuous quantities -
        /// throughflow, wetness - where a stepped read would show as banding.
        /// </summary>
        Bilinear = 0,

        /// <summary>
        /// Whichever of the four is closest. The only valid read for a label: the
        /// average of basin 3 and basin 8 is not a basin.
        /// </summary>
        Nearest = 1,

        /// <summary>
        /// Lowest of the four. The right read for a water SURFACE. Inside a body of
        /// water all four cells hold the same level, so the result is exactly flat;
        /// at the rim the land cells sit higher, so taking the lowest keeps the
        /// surface at its true spill elevation instead of letting it ramp up the
        /// bank and flood ground that should be dry.
        /// </summary>
        Minimum = 2
    }

    /// <summary>
    /// A drainage solve covering the entire world, sampled per chunk. Build one with
    /// <see cref="Solve"/>, then read its fields through <see cref="ReadAtChunk"/>.
    /// </summary>
    public sealed class WorldDrainage
    {
        /// <summary>The world-scale height the solve was run on.</summary>
        public readonly Matrix height;

        /// <summary>Depression-filled height: the surface standing water rests at.</summary>
        public readonly float[] filled;

        /// <summary>Downstream neighbour index per cell. Null unless flow was asked for.</summary>
        public readonly int[] dir;

        /// <summary>Cells draining through each cell. Null unless flow was asked for.</summary>
        public readonly float[] accum;

        public readonly int resX;
        public readonly int resZ;

        /// <summary>Ground one solve cell covers, in square metres.</summary>
        public readonly float cellArea;

        private readonly float originX;
        private readonly float originZ;
        private readonly float mppX;
        private readonly float mppZ;

        private WorldDrainage(Matrix source, float[] filled, int[] dir, float[] accum)
        {
            height = source;
            this.filled = filled;
            this.dir = dir;
            this.accum = accum;

            resX = source.rect.size.x;
            resZ = source.rect.size.z;

            Vector2 pixel = source.PixelSize;
            mppX = Mathf.Max(1e-4f, pixel.x);
            mppZ = Mathf.Max(1e-4f, pixel.y);
            cellArea = mppX * mppZ;

            originX = source.worldPos.x;
            originZ = source.worldPos.z;
        }

        /// <summary>
        /// Test-only seam: wrap an already-known height + filled-surface pair in a
        /// WorldDrainage so <see cref="SeaLevelFloor"/> and the coastal pass can be
        /// unit-tested against a hand-authored world, without standing up a full
        /// graph to drive <see cref="Solve"/>. <paramref name="filled"/> may be null,
        /// in which case the height is treated as its own filled surface (no pooling).
        /// Internal, so it never becomes part of the shipped public API.
        /// </summary>
        internal static WorldDrainage CreateForTests(Matrix height, float[] filled)
        {
            if (height == null || height.arr == null) return null;
            float[] fill = filled != null ? filled : (float[])height.arr.Clone();
            return new WorldDrainage(height, fill, null, null);
        }

        /// <summary>
        /// Test-only seam that also carries a flow-accumulation field, so the flow /
        /// bed-scour reading derived from it (HydroField.BedFlow01) can be exercised on
        /// a hand-authored channel without standing up the full <see cref="Solve"/>
        /// flow pass. <paramref name="accum"/> may be null (no flow known), which is
        /// exactly what the two-argument seam produces. Internal, never shipped.
        /// </summary>
        internal static WorldDrainage CreateForTests(Matrix height, float[] filled, float[] accum)
        {
            if (height == null || height.arr == null) return null;
            float[] fill = filled != null ? filled : (float[])height.arr.Clone();
            return new WorldDrainage(height, fill, null, accum);
        }

        /// <summary>
        /// Test-only seam carrying the FULL flow solve - downstream directions as well as
        /// accumulation - so the river-network extraction can be exercised on a
        /// hand-authored drainage tree whose answer is known by construction.
        ///
        /// The two seams above deliberately leave <c>dir</c> null, which is correct for
        /// what they test but makes a network untraceable: RiverNetworkExtractor needs
        /// `dir` to walk downstream at all. Authoring `dir` by hand is also the only way
        /// to pin confluence and Strahler behaviour on a tree with a known shape rather
        /// than on whatever a noise field happens to produce. Internal, never shipped.
        /// </summary>
        internal static WorldDrainage CreateForTests(Matrix height, float[] filled,
                                                     int[] dir, float[] accum)
        {
            if (height == null || height.arr == null) return null;
            float[] fill = filled != null ? filled : (float[])height.arr.Clone();
            return new WorldDrainage(height, fill, dir, accum);
        }

        /// <summary>UC-T03-03: preserves the original routing API and method-group signature; samples the actual composed height.</summary>
        /// <param name="consumer">Input-owning generator; null returns null.</param>
        /// <param name="inputIndex">Zero-based inlet.</param>
        /// <param name="tile">Captured world context; null returns null.</param>
        /// <param name="resPerSide">Solve cells per side, clamped to 16-512.</param>
        /// <param name="fillEpsilon">Finite normalized downhill fill gradient.</param>
        /// <param name="needFlow">Whether to compute direction and accumulation.</param>
        /// <param name="stop">Optional requester cancellation.</param>
        /// <returns>Immutable drainage, or null for absent/cancelled input.</returns>
        /// <exception cref="System.ArgumentOutOfRangeException">Epsilon is nonfinite.</exception>
        /// <exception cref="System.InvalidOperationException">A shared cache miss would block the main thread.</exception>
        public static WorldDrainage Solve(Generator consumer, int inputIndex, TileData tile,
                                          int resPerSide, float fillEpsilon, bool needFlow, StopToken stop)
        {
            return Solve(consumer, inputIndex, tile, resPerSide, fillEpsilon, needFlow, stop, false);
        }

        /// <summary>
        /// Pulls <paramref name="consumer"/>'s input at world scale and solves its
        /// drainage. Returns null if the input is not connected or the build was
        /// cancelled - both of which the caller must treat as "no water".
        /// UC-T02-03: cached misses execute on workers; synchronous main-thread misses are rejected before computation.
        /// </summary>
        /// <param name="consumer">Node whose input is sampled; null returns null.</param>
        /// <param name="inputIndex">Zero-based inlet; unwired inputs return null.</param>
        /// <param name="tile">Captured field/world context, or null.</param>
        /// <param name="resPerSide">Solve cells per side. Clamped to 16-512.</param>
        /// <param name="fillEpsilon">
        /// Downhill gradient left through filled ground. Zero gives a genuinely flat
        /// lake surface; a hair above zero keeps a levelled reach draining instead of
        /// standing as a pond. Which one is wanted differs per node, which is why it
        /// is a parameter and not a constant.
        /// </param>
        /// <param name="needFlow">
        /// Also compute flow direction and accumulation. Skip it when only the filled
        /// surface is wanted - it is most of the cost.
        /// </param>
        /// <param name="stop">Local requester cancellation; shared cached work belongs to the world's lifetime.</param>
        /// <param name="useTrenchReference">UC-T03-02: true only for terminal terrain guards; classify ground before surviving river cuts. Defaults to false for routing.</param>
        /// <returns>Immutable shared drainage, or null for missing/cancelled input.</returns>
        /// <exception cref="System.ArgumentOutOfRangeException">Epsilon is nonfinite.</exception>
        /// <exception cref="System.InvalidOperationException">A cached miss would block the main thread; schedule and await instead.</exception>
        public static WorldDrainage Solve(Generator consumer, int inputIndex, TileData tile,
                                          int resPerSide, float fillEpsilon, bool needFlow,
                                          StopToken stop, bool useTrenchReference)
        {
            if (consumer == null || tile == null) return null;
            if (stop != null && stop.StopRequested) return null;
            if (float.IsNaN(fillEpsilon) || float.IsInfinity(fillEpsilon))
                throw new System.ArgumentOutOfRangeException(nameof(fillEpsilon), "Drainage epsilon must be finite.");
            resPerSide = Mathf.Clamp(resPerSide, 16, 512);

            // UC-T02-03: identify the captured upstream product, geometry and solve options, not just its consumer.
            WorldDrainageCache cache = tile.drainageCache;
            if (cache != null && tile.Graph != null)
            {
                string key = cache.BuildInputKey(consumer, inputIndex, tile, resPerSide, fillEpsilon, needFlow);
                if (useTrenchReference) key += "|guard-ground"; // UC-T03-02: never reuse carved drainage for guard classification.
                return cache.GetOrCompute(key,
                    sharedStop => Compute(consumer, inputIndex, tile, resPerSide, fillEpsilon, needFlow, sharedStop, useTrenchReference), stop, tile);
            }
            WorldDrainage result = Compute(consumer, inputIndex, tile, resPerSide, fillEpsilon, needFlow, stop, useTrenchReference);
            tile.TrackProductMemory(result, "drainage"); // UC-T02-06: standalone solves also remain pinned through their consumer.
            return result;
        }

        // UC-T02-03: the shared solve observes its owning world lifetime, never an individual chunk's cancellation.
        private static WorldDrainage Compute(Generator consumer, int inputIndex, TileData tile,
                                             int resPerSide, float fillEpsilon, bool needFlow, StopToken stop, bool useTrenchReference)
        {
            tile.RecordDrainageSolve(); // UC-T02-09: count the shared factory once, not each joining requester.
            TileData worldView = TileWindows.WholeWorld(tile, resPerSide);

            // With no graph to re-pull through there is only this chunk to go on -
            // which is also the only case where this chunk IS the whole world.
            TileData pullThrough = worldView != null ? worldView : tile;
            Matrix source = pullThrough.PullInput(consumer, inputIndex, stop) as Matrix;

            if (source == null) return null;
            if (stop != null && stop.StopRequested) return null;

            // UC-T03-02: a trench must not change which coarse cells are classified as ocean/lake/dry ground.
            if (useTrenchReference && source.trenchReference != null) source = source.CopyGuardGround();

            int resX = source.rect.size.x;
            int resZ = source.rect.size.z;
            if (resX < 2 || resZ < 2) return null;

            // UC-T02-06: cover fill/flow arrays plus the growable heap's old/new backing arrays before allocation.
            using (tile.ReserveProductMemory(64 + (long)resX * resZ * 48))
            {
                System.Func<bool> cancel = null;
                if (stop != null) cancel = delegate { return stop.StopRequested; };

                float[] filled = Hydrology.FillDepressions(source.arr, resX, resZ, fillEpsilon, cancel);
                if (stop != null && stop.StopRequested) return null;

                int[] dir = null;
                float[] accum = null;

                if (needFlow)
                {
                    dir = Hydrology.FlowDirection(filled, resX, resZ, cancel);
                    accum = Hydrology.FlowAccumulation(dir, resX, resZ, null, cancel);
                    if (stop != null && stop.StopRequested) return null;
                }

                return new WorldDrainage(source, filled, dir, accum);
            }
        }

        /// <summary>
        /// Cells a real area covers on the solve grid, floored at four. The floor is
        /// numerical rather than hydrological: a threshold finer than the grid would
        /// mark every cell as qualifying.
        /// </summary>
        public float CellsForArea(float squareMetres)
        {
            return Mathf.Max(4f, Mathf.Max(cellArea, squareMetres) / cellArea);
        }

        /// <summary>Real area a cell count represents, in square metres.</summary>
        public float AreaOfCells(float cells)
        {
            return cells * cellArea;
        }

        // ------------------------------------------------------------------
        //  Reading the solve at chunk resolution
        // ------------------------------------------------------------------

        /// <summary>
        /// Resamples a world-scale field onto <paramref name="target"/>'s lattice,
        /// matching it pixel for pixel by world position.
        /// </summary>
        public float[] ReadAtChunk(float[] field, Matrix target, WorldRead mode)
        {
            return ReadAtChunk(field, target, mode, null); // UC-T03-04: preserve the existing method-group API.
        }

        /// <summary>UC-T03-04: resamples a world field with cooperative cancellation before every output row.</summary>
        /// <param name="field">Read-only solve-sized values in their existing units; null produces zeros.</param>
        /// <param name="target">Non-null destination lattice with world-metre position and spacing.</param>
        /// <param name="mode">Interpolation/reduction mode; values retain the source units.</param>
        /// <param name="stop">Optional cancellation token.</param>
        /// <returns>Complete target-sized values; no partially sampled array is returned.</returns>
        /// <exception cref="System.OperationCanceledException">Cancellation was requested.</exception>
        public float[] ReadAtChunk(float[] field, Matrix target, WorldRead mode, StopToken stop)
        {
            return ReadAtChunkCancellable(field, target, mode, stop == null ? (System.Func<bool>)null : () => stop.StopRequested);
        }

        // UC-T03-04: one callback-driven core permits deterministic cancellation acceptance inside the row loop.
        internal float[] ReadAtChunkCancellable(float[] field, Matrix target, WorldRead mode, System.Func<bool> cancelled)
        {
            if (cancelled != null && cancelled()) throw new System.OperationCanceledException("World field sampling cancelled.");
            int outX = target.rect.size.x;
            int outZ = target.rect.size.z;
            float[] result = new float[outX * outZ];
            if (field == null) return result;

            Vector2 pixel = target.PixelSize;
            float stepX = pixel.x;
            float stepZ = pixel.y;
            float baseX = target.worldPos.x;
            float baseZ = target.worldPos.z;

            for (int z = 0; z < outZ; z++)
            {
                if (cancelled != null && cancelled()) throw new System.OperationCanceledException("World field sampling cancelled.");
                int row = z * outX;
                float worldZ = baseZ + z * stepZ;

                for (int x = 0; x < outX; x++)
                    result[row + x] = Sample(field, baseX + x * stepX, worldZ, mode);
            }

            return result;
        }

        /// <summary>
        /// Resamples a world-scale label field onto <paramref name="target"/>'s
        /// lattice. Always nearest-neighbour: a label cannot be averaged.
        /// </summary>
        public int[] ReadLabelsAtChunk(int[] field, Matrix target)
        {
            int outX = target.rect.size.x;
            int outZ = target.rect.size.z;
            int[] result = new int[outX * outZ];
            if (field == null) return result;

            Vector2 pixel = target.PixelSize;
            float stepX = pixel.x;
            float stepZ = pixel.y;
            float baseX = target.worldPos.x;
            float baseZ = target.worldPos.z;

            for (int z = 0; z < outZ; z++)
            {
                int row = z * outX;
                float worldZ = baseZ + z * stepZ;

                for (int x = 0; x < outX; x++)
                {
                    int cell = NearestCell(baseX + x * stepX, worldZ);
                    result[row + x] = field[cell];
                }
            }

            return result;
        }

        /// <summary>One world-scale field value at a world XZ position.</summary>
        public float Sample(float[] field, float worldX, float worldZ, WorldRead mode)
        {
            int x0, z0;
            float tx, tz;
            Locate(worldX, worldZ, out x0, out z0, out tx, out tz);

            int x1 = x0 + 1 < resX ? x0 + 1 : x0;
            int z1 = z0 + 1 < resZ ? z0 + 1 : z0;

            int rowA = z0 * resX;
            int rowB = z1 * resX;

            float v00 = field[rowA + x0];
            float v10 = field[rowA + x1];
            float v01 = field[rowB + x0];
            float v11 = field[rowB + x1];

            if (mode == WorldRead.Nearest)
                return field[(tz < 0.5f ? rowA : rowB) + (tx < 0.5f ? x0 : x1)];

            if (mode == WorldRead.Minimum)
            {
                float m = v00;
                if (v10 < m) m = v10;
                if (v01 < m) m = v01;
                if (v11 < m) m = v11;
                return m;
            }

            return Mathf.Lerp(Mathf.Lerp(v00, v10, tx), Mathf.Lerp(v01, v11, tx), tz);
        }

        /// <summary>Index of the solve cell nearest a world XZ position.</summary>
        public int NearestCell(float worldX, float worldZ)
        {
            int x0, z0;
            float tx, tz;
            Locate(worldX, worldZ, out x0, out z0, out tx, out tz);

            int x = tx < 0.5f || x0 + 1 >= resX ? x0 : x0 + 1;
            int z = tz < 0.5f || z0 + 1 >= resZ ? z0 : z0 + 1;
            return z * resX + x;
        }

        /// <summary>
        /// Lower-left enclosing cell plus the fraction into it. Positions outside the
        /// solve grid clamp to its edge, which only happens for chunks placed beyond
        /// the declared world bounds - there the edge value is the honest answer.
        /// </summary>
        private void Locate(float worldX, float worldZ,
                            out int x0, out int z0, out float tx, out float tz)
        {
            float fx = (worldX - originX) / mppX;
            float fz = (worldZ - originZ) / mppZ;

            x0 = Mathf.FloorToInt(fx);
            z0 = Mathf.FloorToInt(fz);
            tx = fx - x0;
            tz = fz - z0;

            if (x0 < 0) { x0 = 0; tx = 0f; }
            else if (x0 > resX - 1) { x0 = resX - 1; tx = 0f; }

            if (z0 < 0) { z0 = 0; tz = 0f; }
            else if (z0 > resZ - 1) { z0 = resZ - 1; tz = 0f; }
        }
    }
}

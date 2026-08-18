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
//  NOT CACHED, DELIBERATELY
//  ------------------------
//  Two nodes asking for drainage do not necessarily mean the same drainage: each
//  pulls its OWN input branch and may condition it differently, so a shared result
//  would quietly hand one node the other's terrain. And any cache key short of
//  hashing the entire upstream graph goes stale the moment a noise slider moves.
//  At 256x256 against a 1025x1025 chunk the solve is a few per cent of the chunk's
//  own work, and a river in the wrong place costs more than a re-solve.
//
//  Managed C# over float[] and int[]. No Unity API beyond Mathf and Vector2; safe
//  on a worker Task.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes
{
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
        /// Pulls <paramref name="consumer"/>'s input at world scale and solves its
        /// drainage. Returns null if the input is not connected or the build was
        /// cancelled - both of which the caller must treat as "no water".
        /// </summary>
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
        public static WorldDrainage Solve(Generator consumer, int inputIndex, TileData tile,
                                          int resPerSide, float fillEpsilon, bool needFlow,
                                          StopToken stop)
        {
            if (consumer == null || tile == null) return null;

            TileData worldView = TileWindows.WholeWorld(tile, resPerSide);

            // With no graph to re-pull through there is only this chunk to go on -
            // which is also the only case where this chunk IS the whole world.
            TileData pullThrough = worldView != null ? worldView : tile;
            Matrix source = pullThrough.PullInput(consumer, inputIndex, stop) as Matrix;

            if (source == null) return null;
            if (stop != null && stop.StopRequested) return null;

            int resX = source.rect.size.x;
            int resZ = source.rect.size.z;
            if (resX < 2 || resZ < 2) return null;

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

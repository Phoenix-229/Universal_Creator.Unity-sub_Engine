// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 1: Terrain Core
//
//  File:    TileWindows.cs
//  Purpose: Builds the alternative views of a chunk that a neighbourhood-aware
//           node needs: the same chunk with ghost margins around it, and the
//           whole world at a coarse resolution.
//
//  WHY THIS EXISTS
//  ---------------
//  A node that only reads one pixel at a time is correct on any rect. A node that
//  reads a NEIGHBOURHOOD is not: at a chunk seam the neighbours are missing, so
//  the answer changes depending on which chunk asked. That is what a user sees as
//  a vein, a step or a see-saw along the tile grid.
//
//  There are two different fixes, and the distinction matters:
//
//   - A blur, a curvature, a distance transform or a window extreme reads a
//     BOUNDED neighbourhood. Re-pulling the input over a slightly larger rect and
//     cropping the ghost ring off afterwards makes the result identical to what a
//     single world-sized evaluation would have produced. Expand + Crop.
//
//   - Flow accumulation reads an UNBOUNDED one: the land draining through a cell
//     can start anywhere upstream, tens of chunks away. No finite margin fixes
//     that, and a bigger one only moves the seam. The drainage has to be solved
//     once for the whole world and then read per chunk. WholeWorld.
//
//  Both work by re-pulling the upstream graph at a different rect, which is only
//  possible because every Creator is a function of world position rather than of
//  pixel index. That property is what makes this file legal.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes
{
    /// <summary>
    /// Alternative rects a node can re-pull its input through when one chunk on its
    /// own is not enough information to answer correctly.
    /// </summary>
    public static class TileWindows
    {
        /// <summary>Largest admitted whole-world drainage lattice supported by the bounded solver and accounting paths, in cells per side.</summary>
        public const int MaximumDrainageResolution = 2048;

        /// <summary>UC repair 30: preserves requested drainage detail within the supported bound instead of silently capping large worlds at 512.</summary>
        /// <param name="requested">Requested cells per side; values below 16 retain the legacy minimum of 16.</param>
        /// <returns>The requested resolution, or 16 for a smaller request; storage still requires separate budget admission.</returns>
        /// <exception cref="System.ArgumentOutOfRangeException">The request exceeds 2048 cells per side.</exception>
        public static int DrainageResolution(int requested)
        {
            if (requested > MaximumDrainageResolution)
                throw new System.ArgumentOutOfRangeException(nameof(requested),
                    "Whole-world drainage supports at most 2048 cells per side; larger requests require a separately verified solver and memory budget.");
            return Mathf.Max(16, requested);
        }

        /// <summary>UC-T02-08: converts a finite world-space neighbourhood to conservative sample margins on an anisotropic grid.</summary>
        /// <param name="tile">Non-null tile with at least two samples and positive finite world size on both horizontal axes.</param>
        /// <param name="radiusMetres">Finite nonnegative reach in metres.</param>
        /// <returns>Samples on each side, including one interpolation sample; zero for zero reach.</returns>
        /// <exception cref="System.ArgumentOutOfRangeException">The radius or grid is invalid, or the sample count overflows.</exception>
        public static int MarginForMetres(TileData tile, float radiusMetres)
        {
            if (tile == null || tile.rect.size.x < 2 || tile.rect.size.z < 2 ||
                float.IsNaN(radiusMetres) || float.IsInfinity(radiusMetres) || radiusMetres < 0 ||
                float.IsNaN(tile.worldSize.x) || float.IsInfinity(tile.worldSize.x) || tile.worldSize.x <= 0 ||
                float.IsNaN(tile.worldSize.z) || float.IsInfinity(tile.worldSize.z) || tile.worldSize.z <= 0)
                throw new System.ArgumentOutOfRangeException(nameof(radiusMetres), "A finite radius and positive sample spacing are required.");
            if (radiusMetres == 0) return 0;
            double spacing = System.Math.Min((double)tile.worldSize.x / (tile.rect.size.x - 1), (double)tile.worldSize.z / (tile.rect.size.z - 1));
            double samples = System.Math.Ceiling(radiusMetres / spacing) + 1;
            if (samples > int.MaxValue) throw new System.ArgumentOutOfRangeException(nameof(radiusMetres));
            return (int)samples;
        }

        /// <summary>
        /// The same chunk with <paramref name="margin"/> ghost pixels added on every
        /// side, bound to its own evaluator so an input can be pulled at the wider
        /// rect. Returns null when there is no graph to pull through.
        /// </summary>
        /// <remarks>
        /// The world placement is stretched by exactly the margin's worth of metres,
        /// so a Creator sampling absolute coordinates produces the true continuation
        /// of the terrain outside the chunk rather than a repeat of its edge.
        /// </remarks>
        public static TileData Expand(TileData tile, int margin)
        {
            if (tile == null || margin <= 0 || tile.Graph == null) return null;

            // UC-T02-08: refuse overflow rather than wrap an oversized halo into an unrelated coordinate window.
            if ((long)tile.rect.size.x + 2L * margin > int.MaxValue || (long)tile.rect.size.z + 2L * margin > int.MaxValue ||
                (long)tile.rect.offset.x - margin < int.MinValue || (long)tile.rect.offset.z - margin < int.MinValue)
                throw new System.ArgumentOutOfRangeException(nameof(margin));

            float scaleX = tile.rect.size.x > 1
                ? tile.worldSize.x / (tile.rect.size.x - 1) : tile.worldSize.x;
            float scaleZ = tile.rect.size.z > 1
                ? tile.worldSize.z / (tile.rect.size.z - 1) : tile.worldSize.z;

            CoordRect rect = new CoordRect(
                new Coord(tile.rect.offset.x - margin, tile.rect.offset.z - margin),
                new Coord(tile.rect.size.x + margin * 2, tile.rect.size.z + margin * 2));

            Vector3 pos = new Vector3(tile.worldPos.x - scaleX * margin,
                                      tile.worldPos.y,
                                      tile.worldPos.z - scaleZ * margin);

            Vector3 size = new Vector3(tile.worldSize.x + scaleX * margin * 2,
                                       tile.worldSize.y,
                                       tile.worldSize.z + scaleZ * margin * 2);

            return Bind(tile, rect, pos, size);
        }

        /// <summary>
        /// A tile covering the ENTIRE world at <paramref name="resPerSide"/> pixels
        /// per side, bound to its own evaluator. Returns null when there is no graph
        /// to pull through.
        /// </summary>
        /// <param name="tile">Captured tile with a bound graph; null or an absent graph returns null.</param>
        /// <param name="resPerSide">Requested cells per side; minimum 16, maximum 2048. Storage requires budget admission.</param>
        /// <returns>A separately evaluated whole-world view, or null without a graph; retire through the parent after use.</returns>
        /// <exception cref="System.ArgumentOutOfRangeException">A bound graph requests more than 2048 cells per side.</exception>
        /// <remarks>
        /// Coarse on purpose. This exists for quantities that are global but
        /// smooth - which way water leaves a cell, and how much land is behind it -
        /// and those are decided by the shape of the landscape, not by its last
        /// metre of detail. Solving them at chunk resolution over the whole world
        /// would cost hundreds of times more and change the answer very little.
        /// </remarks>
        public static TileData WholeWorld(TileData tile, int resPerSide)
        {
            if (tile == null || tile.Graph == null) return null;

            int res = DrainageResolution(resPerSide); // UC repair 30: every view and solver uses the same explicit bound.

            Vector3 size = new Vector3(Mathf.Max(1f, tile.fullWorldSize.x),
                                       tile.worldSize.y,
                                       Mathf.Max(1f, tile.fullWorldSize.z));

            Vector3 pos = new Vector3(tile.fullWorldPos.x,
                                      tile.worldPos.y,
                                      tile.fullWorldPos.z);

            // Offset 0: this rect is its own coordinate space covering the world, not
            // a window into the chunk grid, so an inherited offset would shift every
            // world-space sample by one chunk's worth of pixels.
            CoordRect rect = new CoordRect(new Coord(0, 0), new Coord(res, res));

            return Bind(tile, rect, pos, size);
        }

        // One construction path for both views: a fresh TileData carrying the parent's
        // world bounds and height range, bound to a fresh evaluator over the same
        // graph. The evaluator is per-view because its visiting set and the tile's
        // product cache are both per-rect - sharing either would hand a node the
        // chunk's answer when it asked for the wider one.
        private static TileData Bind(TileData tile, CoordRect rect, Vector3 pos, Vector3 size)
        {
            TileData view = new TileData(tile.chunkCoord, rect, pos, size, tile.worldHeight);
            tile.CopyViewContext(view); // UC-T07-08: halo functions retain arguments on the requested lattice and the shared input/lifetime owners.
            view.Bind(new GraphEvaluator(tile.Graph));
            return view;
        }

        /// <summary>
        /// Copies the centre of a field produced on an <see cref="Expand"/>ed tile
        /// back out at the original chunk's rect, discarding the ghost ring.
        /// </summary>
        public static Matrix Crop(Matrix padded, TileData tile, int margin)
        {
            Matrix result = tile.CreateMatrix();
            if (padded == null) return result;

            int outX = result.rect.size.x;
            int outZ = result.rect.size.z;
            int padX = padded.rect.size.x;

            for (int z = 0; z < outZ; z++)
            {
                int outRow = z * outX;
                int padRow = (z + margin) * padX + margin;

                for (int x = 0; x < outX; x++)
                    result.arr[outRow + x] = padded.arr[padRow + x];
            }

            // UC-T03-02: the reference must use the same halo crop, not the parent lattice.
            if (padded.trenchReference != null)
                result.trenchReference = Crop(padded.trenchReference, tile, margin);
            return result;
        }
    }
}
